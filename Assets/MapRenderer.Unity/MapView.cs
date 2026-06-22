using UnityEngine;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity
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
    /// S49: selects the render submission backend.
    /// Default is <see cref="GameObject"/> (the existing per-layer GameObject path, unchanged).
    /// Set to <see cref="Brg"/> to submit tile meshes via <see cref="BrgTileRenderer"/> instead.
    /// The GameObject path is preserved in full when <see cref="GameObject"/> is selected (tooth 1).
    /// </summary>
    public enum RenderBackend
    {
        /// <summary>Default: per-layer GameObject + MeshRenderer (existing path, unchanged).</summary>
        GameObject = 0,
        /// <summary>S49 BRG path: draw tile meshes via BatchRendererGroup.</summary>
        Brg        = 1,
    }

    public sealed class MapView : MonoBehaviour
    {
        // ── Profiler markers (allocation-free; static readonly = constructed once at type-init) ──
        // The tile-pipeline markers moved with TileManager; MapView keeps only the camera-advance marker.
        private static readonly ProfilerMarker PmCameraAdvance = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Camera.Advance");

        // ── Configuration (serialized — inspector-editable; read every Tick, never snapshotted) ──
        [Tooltip("Cover over-select: viewport aspect (w/h) and pad factor (absorbs viewport size + pitch).")]
        public float ViewportAspect = 1.5f;
        public float PadFactor      = 1.5f;

        [Tooltip("Zoom clamp for tile selection.")]
        public int MinZoom = 0;
        public int MaxZoom = 14;

        [Tooltip("Max tile pipeline builds per Tick (load smoothing).")]
        public int MaxBuildsPerTick = 4;

        // ── S49: render backend selector ─────────────────────────────────────────────────────
        [Tooltip("S49: render submission backend. GameObject (default) = existing per-layer MonoBehaviour path. " +
                 "Brg = BatchRendererGroup path. The GameObject path is byte-for-byte unchanged when this is " +
                 "set to GameObject.")]
        public RenderBackend Backend = RenderBackend.GameObject;

        // ── S50: Core camera system + Unity sync layer (owned by MapView, D4) ─────────────────
        private CameraSystem _cameraSystem;
        private MapCamera    _mapCamera;

        private StyleDocument _style;

        // Per-style-layer render bundles (fills + lines), built once at Initialise. Owns the materials.
        private readonly StyledLayerSet _layers = new StyledLayerSet();

        // The tile lifecycle — owned by MapView, ticked once per frame. Created at Initialise.
        private TileManager _tileManager;

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

            _layers.Build(_style, _cameraSystem != null ? _cameraSystem.CurrentProperties.Zoom : 0.0);

            if (_tileManager == null)
                _tileManager = new TileManager(transform, _layers);
            _tileManager.Initialise(source, ownsSource, Backend == RenderBackend.Brg ? _layers : null);
        }

        /// <summary>True once <see cref="Initialise"/> has been called successfully.</summary>
        public bool IsInitialised => _tileManager != null && _tileManager.IsInitialised;

        /// <summary>
        /// Current camera state (read-only). S50: this is <see cref="CameraSystem.CurrentProperties"/> directly.
        /// </summary>
        public CameraProperties View => _cameraSystem != null ? _cameraSystem.CurrentProperties : CameraProperties.Default;

        /// <summary>Number of currently loaded (or loading) tiles. Exposed for tests.</summary>
        public int LoadedTileCount => _tileManager != null ? _tileManager.LoadedTileCount : 0;

        /// <summary>The scheduler's in-flight fetch count. Exposed for tests.</summary>
        public int InFlightCount => _tileManager != null ? _tileManager.InFlightCount : 0;

        /// <summary>The scene root's current Mercator origin. Exposed for tests.</summary>
        public double2 SceneOrigin => _sceneOrigin;

        /// <summary>
        /// The per-style-layer render bundles owned by this view. <c>internal</c>, not <c>public</c>: the
        /// only production consumer is MapRoot's startup diagnostic (same assembly); tests read layer
        /// counts via <c>MapViewTestExtensions</c> (InternalsVisibleTo). Keeps test-support surface OUT of
        /// MapView's public API.
        /// </summary>
        internal StyledLayerSet Layers => _layers;

        /// <summary>
        /// Number of tiles released while their tessellation was still in-flight (HasTessellationTask
        /// and not yet Built at the moment of release). Exposed for tests to prove the mid-flight race
        /// actually occurred in <see cref="S51DisposalLeakGuardTests"/>.
        /// </summary>
        public int ReleasedMidFlightCount => _tileManager != null ? _tileManager.ReleasedMidFlightCount : 0;

        /// <summary>
        /// S49 test observability: the live BRG renderer (null when Backend == GameObject or not yet
        /// initialised). Exposed so tests can read instance buffer state without GPU readback.
        /// </summary>
        internal BrgTileRenderer BrgRenderer => _tileManager?.BrgRenderer;

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
            _tileManager?.InvalidateCover(); // null before Initialise (MapRoot.Wire order); reset re-armed there
        }

        /// <summary>
        /// Test-only: returns true and the built tile's container GameObject when the tile is loaded
        /// AND its mesh has been produced.
        /// </summary>
        public bool TryGetBuiltTile(TileId id, out GameObject go)
        {
            if (_tileManager != null)
                return _tileManager.TryGetBuiltTile(id, out go);
            go = null;
            return false;
        }

        /// <summary>
        /// Test-only: true once every loaded tile has finished building (or is definitively absent).
        /// S47/S51: returns false while any tile has a pending tessellation.
        /// </summary>
        public bool AllTilesSettled() => _tileManager == null || _tileManager.AllTilesSettled();

        /// <summary>
        /// S47 deterministic drain — blocks until all in-flight fetch and tessellation UniTasks complete,
        /// then consumes their results synchronously. After this returns, <see cref="AllTilesSettled()"/>
        /// is guaranteed true for all currently loaded tiles. Delegates to <see cref="TileManager"/>,
        /// passing the current scene origin so drained tiles are placed correctly. Called by test helpers;
        /// NOT on the production Update path.
        /// </summary>
        public void DrainTessellation()
        {
            if (_cameraSystem == null || _tileManager == null) return;
            _tileManager.DrainTessellation(_cameraSystem.CurrentProperties, _sceneOrigin);
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
        /// the scene origin, then ticks the <see cref="TileManager"/>. Allocation-free in steady state.
        /// </summary>
        public void Tick()
        {
            if (_tileManager == null || !_tileManager.IsInitialised) return;

            CameraProperties cam = _cameraSystem.CurrentProperties;

            // ApplyZoom first — before the TileManager early-out — so fractional-zoom changes always push
            // uniforms (fill/line zoom paint, live _MetersPerPixel for pixel line width, zoom-step dasharrays).
            _layers.ApplyZoom(cam.Zoom);

            // Camera-relative rendering: snap the render origin to the look-at every frame, then place all
            // loaded tiles relative to it. The look-at therefore sits at the render origin (matching the
            // camera, which orbits the origin), and visible tiles + camera stay at their smallest possible
            // coordinates — best float precision, no threshold/rebase machinery.
            _sceneOrigin = cam.CenterMercator();

            if (Backend == RenderBackend.Brg)
                _tileManager.BrgRebuild(_sceneOrigin);
            else
                _tileManager.RebaseTiles(_sceneOrigin);

            _tileManager.Tick(cam, _sceneOrigin, BuildTileSelectionConfig());
        }

        private TileManager.TileSelectionConfig BuildTileSelectionConfig() => new TileManager.TileSelectionConfig
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
        /// Releases all tile GameObjects and Mesh assets (via the <see cref="TileManager"/>), then disposes
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
            _tileManager?.Dispose();  // tiles first — their renderers reference _layers' materials
            _layers.Dispose();
        }

        private void OnDestroy()
        {
            Teardown();
        }
    }
}
