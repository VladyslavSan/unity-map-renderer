using UnityEngine;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// S40 live multi-tile render loop — per-layer styled fill rendering.
    ///
    /// Replaces the S06 single-shared-material path with one styled draw per <c>fill</c> style layer,
    /// ordered by painter's algorithm (LayerDrawOrder), filtered by S10 FeatureSelector, painted by
    /// S13 FillPaint (per-feature data-driven color baked into vertex stream via StyledFillTileBuilder).
    ///
    /// Architecture (post-decomposition):
    ///   • <see cref="StyleDocument"/> is provided at <see cref="Initialise"/> time. MapView builds a
    ///     <see cref="StyledLayerSet"/> from its fill+line layers — one bundle per fill/line style layer in
    ///     declared (painter's) order, each holding a per-layer Material instance and a ZoomStyleApplier.
    ///   • The tile lifecycle (cover→fetch→tessellate→consume→evict, plus disposal/leak guards) lives in
    ///     <see cref="TileManager"/>, which MapView owns and ticks once per frame. MapView keeps only the
    ///     three things the lifecycle reads from it: the camera state, the StyledLayerSet, and the scene
    ///     origin — passed in per call so MapView's serialized config and rebasing origin stay authoritative.
    ///   • Per-frame: <see cref="StyledLayerSet.ApplyZoom"/> pushes zoom uniforms into each layer's material
    ///     first (so a fractional-zoom-only change still updates uniforms), then MapView refreshes the scene
    ///     origin, then ticks the TileManager.
    ///   • Eviction / Mesh-leak guards / async tessellation all live in <see cref="TileManager"/>; layer
    ///     Material instances live on the <see cref="StyledLayerSet"/> and are disposed in <see cref="Teardown"/>
    ///     AFTER the TileManager (tile renderers reference layer materials, so order matters).
    ///
    /// Steady-state no-GC contract (preserved from S06): the ApplyZoom loop is a plain <c>for</c> over a
    /// <c>List</c> (struct enumerator, no allocation); TileManager early-outs before any allocation when the
    /// cover is clean and nothing is pending.
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    /// <summary>
    /// Selects the tile render backend. All three register one draw item per tile-layer mesh behind
    /// <see cref="ITileRenderBackend"/> and are driven by a per-frame floating-origin <c>Rebuild</c>; only
    /// the submission differs (Entities Graphics, raw BRG, or stock GameObjects).
    /// </summary>
    public enum RenderBackend
    {
        /// <summary>Default (S53c): each tile-layer draw item is an <see cref="Backend.Entities.TileRenderer"/>
        /// entity rendered via Entities Graphics (BatchRendererGroup under the hood), grouped per tile and
        /// inspectable/disable-able in the Entities Hierarchy. Value 0 so scenes serialized with the old
        /// default deserialize to Entities.</summary>
        Entities = 0,
        /// <summary>S49 BRG path: draw tile meshes via a hand-packed <see cref="Backend.BRG.TileRenderer"/>
        /// BatchRendererGroup. The zero-allocation production path.</summary>
        Brg      = 1,
        /// <summary>The original per-tile-layer GameObject path (<see cref="Backend.GameObjects.TileRenderer"/>):
        /// one MeshFilter+MeshRenderer child per layer under a <c>"Tile z/x/y"</c> container, drawn by the
        /// SRP Batcher. The simplest, most Inspector-debuggable backend — retired in S53c (the Entities
        /// Hierarchy covered the debug need) and restored as an explicit opt-in.</summary>
        GameObject = 2,
    }

    public sealed class MapView : MonoBehaviour
    {
        // ── Profiler markers (allocation-free; static readonly = constructed once at type-init) ──
        // The tile-pipeline markers moved with TileManager; MapView keeps the per-frame Tick sub-phases so a
        // spike inside MapView.Update is attributable to a specific stage instead of the whole Update blob:
        //   ApplyZoom        — push zoom uniforms into every layer material (scales with layer count).
        //   InstancedRebuild — drive the render backend per frame (on Entities this ticks the EG system
        //                      groups; split further inside EntitiesTileRenderer.Rebuild).
        //   ManagerTick      — cover select + request/release + build pump (CoverSelect/FetchPoll nest under it).
        private static readonly ProfilerMarker PmCameraAdvance    = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Camera.Advance");
        private static readonly ProfilerMarker PmApplyZoom        = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.View.ApplyZoom");
        private static readonly ProfilerMarker PmInstancedRebuild = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.View.InstancedRebuild");
        private static readonly ProfilerMarker PmManagerTick      = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.ManagerTick");

        // ── Configuration (serialized — inspector-editable; read every Tick, never snapshotted) ──
        [Tooltip("Cover over-select: viewport aspect (w/h) and pad factor (absorbs viewport size + pitch).")]
        public float ViewportAspect = 1.5f;
        public float PadFactor      = 1.5f;

        [Tooltip("Zoom clamp for tile selection.")]
        public int MinZoom = 0;
        public int MaxZoom = 14;

        [Tooltip("Max tile pipeline builds per Tick (load smoothing).")]
        public int MaxBuildsPerTick = 4;

        // ── Render backend selector ──────────────────────────────────────────────────────────
        [Tooltip("Tile render backend. Entities (default) = per-tile entity hierarchy via Entities " +
                 "Graphics, inspectable in the Entities Hierarchy. Brg = hand-packed BatchRendererGroup, " +
                 "the zero-allocation production path. GameObject = one MeshFilter+MeshRenderer child per " +
                 "tile-layer (SRP Batcher), the simplest Inspector-debuggable path.")]
        public RenderBackend Backend = RenderBackend.Entities;

        // ── S50: Core camera system + Unity sync layer (owned by MapView, D4) ─────────────────
        private CameraSystem _cameraSystem;
        private MapCamera    _mapCamera;

        private StyleDocument _style;

        [Tooltip("Optional: base materials per rendering technique (MapMaterialSet asset). When set, each " +
                 "per-layer material is a CLONE of the matching base — a Material Variant in the Editor, so " +
                 "editing the base .mat in Play live-tunes every layer. When unset, legacy shader-built defaults " +
                 "are used.")]
        public Materials.MapMaterialSet MaterialSet;

        // Per-style-layer render bundles (fills + lines), built once at Initialise. Owns the materials.
        private readonly Style.StyledLayerSet _layers = new Style.StyledLayerSet();

        // The tile lifecycle — owned by MapView, ticked once per frame. Created at Initialise.
        internal Tile.TileManager TileManager;

        // Camera-relative rendering: the scene's float render origin tracks the camera look-at every frame
        // (recomputed in Tick), so the look-at always sits at the render origin. Exposed for tests.
        private double2 _sceneOrigin;

        // ── Lifecycle / injection ────────────────────────────────────────────────────────────

        /// <summary>
        /// Injects the data source, style document, and initial camera (call before the first
        /// <see cref="Tick"/>). If <paramref name="ownsSource"/> is true, <see cref="OnDestroy"/>
        /// disposes the source. The TileManager (and its scheduler) is always owned by this MapView.
        /// </summary>
        public void Initialise(IDataSource source, CameraProperties initialView,
            bool ownsSource = false, StyleDocument style = null)
        {
            _style = style;

            // Ensure a camera system exists (DD1: Tick must never read a null system).
            if (_cameraSystem == null)
                _cameraSystem = new CameraSystem(initialView);

            _layers.Build(_style, _cameraSystem != null ? _cameraSystem.CurrentProperties.Zoom : 0.0, MaterialSet);

            if (TileManager == null)
                TileManager = new Tile.TileManager(_layers);
            TileManager.Initialise(source, ownsSource, Backend);
        }

        /// <summary>True once <see cref="Initialise"/> has been called successfully.</summary>
        public bool IsInitialised => TileManager != null && TileManager.IsInitialised;

        /// <summary>
        /// Current camera state (read-only). S50: this is <see cref="CameraSystem.CurrentProperties"/> directly.
        /// </summary>
        public CameraProperties CurrentProperties => _cameraSystem != null ? _cameraSystem.CurrentProperties : CameraProperties.Default;

        /// <summary>
        /// The scene root's current Mercator origin — this view's own per-frame state (not the
        /// TileManager's). <c>internal</c>: read by tests via <c>InternalsVisibleTo</c>; the rest of the
        /// test-support surface lives in <c>MapViewTestExtensions</c>, not on MapView's public API.
        /// </summary>
        internal double2 SceneOrigin => _sceneOrigin;

        /// <summary>
        /// The per-style-layer render bundles owned by this view. <c>internal</c>, not <c>public</c>: the
        /// only production consumer is MapRoot's startup diagnostic (same assembly); tests read layer
        /// counts via <c>MapViewTestExtensions</c> (InternalsVisibleTo). Keeps test-support surface OUT of
        /// MapView's public API.
        /// </summary>
        internal Style.StyledLayerSet Layers => _layers;

        // ── S45/S50: Camera system accessors ───────────────────────────────────────────────────

        /// <summary>The Core camera system, owned by this MapView. The single source of camera state.</summary>
        public CameraSystem Camera => _cameraSystem;

        /// <summary>
        /// Injects a fully-configured <see cref="CameraSystem"/> + <see cref="MapCamera"/> sync layer.
        /// </summary>
        public void SetCamera(MapCamera mapCamera, CameraSystem cameraSystem)
        {
            _mapCamera     = mapCamera;
            _cameraSystem  = cameraSystem;
            TileManager?.InvalidateCover(); // null before Initialise (Bootstrapper.Wire order); reset re-armed there
        }

        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// S45 D5 / S50 D4 — Deterministic frame update. Called from <see cref="Update"/>
        /// (MonoBehaviour) with <c>Time.deltaTime</c>. Also callable from tests with explicit dt.
        /// </summary>
        public void UpdateFrame(double dt)
        {
            // STEP 1: Advance the camera model, then apply it to the Unity camera (D5 — must run before
            // tile selection reads the camera). Under camera-relative rendering the look-at IS the render
            // origin, so the camera is a pure function of CameraProperties — it orbits the origin, with no
            // scene-origin term. The look-at→render placement of the TILES happens in Tick (_sceneOrigin).
            if (_cameraSystem != null)
            {
                using var sCamAdv = PmCameraAdvance.Auto();
                _cameraSystem.Update(dt);
                _mapCamera?.ApplyCameraProperties(_cameraSystem.CurrentProperties);
            }

            // STEP 2: Tile loop (reads the now-advanced _cameraSystem.CurrentProperties).
            Tick();
        }

        /// <summary>
        /// One frame of the live loop. Pushes zoom uniforms (always, even on a clean-cover frame), refreshes
        /// the scene origin, then ticks the <see cref="Tile.TileManager"/>. Allocation-free in steady state.
        /// </summary>
        public void Tick()
        {
            if (TileManager == null || !TileManager.IsInitialised) return;

            CameraProperties cam = _cameraSystem.CurrentProperties;

            // ApplyZoom first — before the TileManager early-out — so fractional-zoom changes always push
            // uniforms (fill/line zoom paint, live _MetersPerPixel for pixel line width, zoom-step dasharrays).
            using (PmApplyZoom.Auto())
                _layers.ApplyZoom(cam.Zoom);

            // Camera-relative rendering: snap the render origin to the look-at every frame, then place all
            // loaded tiles relative to it. The look-at therefore sits at the render origin (matching the
            // camera, which orbits the origin), and visible tiles + camera stay at their smallest possible
            // coordinates — best float precision, no threshold/rebase machinery.
            _sceneOrigin = cam.CenterMercator();

            // Reposition all loaded tiles relative to the new origin (one transform write per tile on the
            // Entities backend, per-instance on BRG), then select/build/evict cover.
            using (PmInstancedRebuild.Auto())
                TileManager.InstancedRebuild(_sceneOrigin);

            using (PmManagerTick.Auto())
                TileManager.Tick(cam, BuildTileSelectionConfig());
        }

        private Tile.TileManager.TileSelectionConfig BuildTileSelectionConfig() => new Tile.TileManager.TileSelectionConfig
        {
            ViewportAspect   = ViewportAspect,
            PadFactor        = PadFactor,
            MinZoom          = MinZoom,
            MaxZoom          = MaxZoom,
            MaxBuildsPerTick = MaxBuildsPerTick,
        };

        private void Update()
        {
            UpdateFrame(Time.deltaTime);
        }

        /// <summary>
        /// Releases all tile GameObjects and Mesh assets (via the <see cref="Tile.TileManager"/>), then disposes
        /// the StyledLayerSet's materials.
        ///
        /// This is the canonical teardown body. <see cref="OnDestroy"/> delegates to it so that production
        /// Play-mode cleanup (triggered automatically by Unity's lifecycle) shares the same path.
        ///
        /// <para>Order matters: tiles are torn down BEFORE the layer materials — tile renderers reference
        /// <c>StyledLayerSet</c> materials, so disposing materials first would orphan live renderers.</para>
        ///
        /// <para><b>Why a separate public method?</b> In Unity EditMode (no <c>[ExecuteAlways]</c>),
        /// <c>MonoBehaviour.OnDestroy</c> is NOT triggered by <c>Object.DestroyImmediate(go)</c> from an
        /// EditMode test. Headless tests that exercise the cleanup contract call <c>Teardown()</c> explicitly
        /// before destroying the GameObject; production code relies on the <c>OnDestroy</c> delegation.</para>
        ///
        /// <para>Idempotent: safe to call more than once — <c>TileManager.Dispose</c> and
        /// <c>StyledLayerSet.Dispose</c> are both idempotent.</para>
        /// </summary>
        public void Teardown()
        {
            TileManager?.Dispose();  // tiles first — their renderers reference _layers' materials
            _layers.Dispose();
        }

        private void OnDestroy()
        {
            Teardown();
        }
    }
}
