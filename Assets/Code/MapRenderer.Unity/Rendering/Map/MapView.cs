using System.Collections.Generic;
using System;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Unity.Text;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// The live multi-tile render loop, a plain C# class hosted by <see cref="MapViewComponent"/>. It is built
    /// with its <see cref="MapViewConfig"/> and a non-null <see cref="MapCamera"/> and owns its
    /// <see cref="RenderLayerSet"/> and <see cref="TileManager"/> from construction, so it has no "wired yet"
    /// guards. Before <see cref="SetStyle(string,System.Threading.CancellationToken)"/> it is an empty map with no sources, so
    /// <see cref="LateUpdate"/> renders nothing. Steady-state frames do not allocate on the BRG backend (docs/gc-and-allocation-design.md § 2).
    /// </summary>
    public sealed partial class MapView
    {
        /// <summary>Profiler marker name constants (SSOT) for the per-frame view path — referenced by the
        /// <see cref="ProfilerMarker"/> fields below and by <c>ProfilerMarkerTests</c> (internal, via
        /// <c>InternalsVisibleTo</c>). Keep the existing hierarchical names so the Profiler flat search groups.</summary>
        internal static class ProfilerMarkerNames
        {
            // UMBRELLA over the whole per-frame pipeline. Its self-time (total − the children below) is the
            // residual unmarked cost: if it is ~0 every per-frame span is mapped.
            internal const string LateUpdate       = "MapRenderer.View.LateUpdate";
            internal const string CameraAdvance    = "MapRenderer.Camera.Advance";
            internal const string ApplyZoom        = "MapRenderer.View.ApplyZoom";
            internal const string ManagerUpdate    = "MapRenderer.Tile.ManagerUpdate";
            internal const string SceneFrame       = "MapRenderer.View.SceneFrame";
            internal const string SymbolCollect    = "MapRenderer.Symbol.Collect";
            internal const string SymbolBatch      = "MapRenderer.Symbol.BatchBuild";
        }

        // ── Profiler markers (allocation-free; static readonly = constructed once at type-init) ──
        private static readonly ProfilerMarker PmLateUpdate =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.LateUpdate);

        // Commit this frame's camera pose (SyncToCamera: pose math + transform/clip push).
        private static readonly ProfilerMarker PmCameraAdvance =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.CameraAdvance);

        // Push zoom uniforms into every layer material (scales with layer count).
        private static readonly ProfilerMarker PmApplyZoom =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ApplyZoom);

        // Cover select + request/release + build pump (CoverSelect/FetchPoll nest under it).
        private static readonly ProfilerMarker PmManagerUpdate =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.ManagerUpdate);

        // Build the per-frame floating-origin scene frame (projection Project + tangent basis).
        private static readonly ProfilerMarker PmSceneFrame =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SceneFrame);

        // The symbol reconcile that runs before the symbol aggregation (pull/reconcile + PumpBuilds).
        private static readonly ProfilerMarker PmSymbolCollect =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SymbolCollect);

        // The symbol aggregation (cross-tile dedup + batch build), a managed main-thread cost that grows with the
        // on-screen symbol count. Its own marker keeps it out of the unmarked LateUpdate self-time.
        private static readonly ProfilerMarker PmSymbolBatch =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SymbolBatch);

        // ── Injected collaborators (set by the constructor — never null) ─────────────────────────
        private readonly MapViewConfig _config;

        /// <summary>The map camera, owned by this view — the single source of camera state. Non-null, set
        /// once at construction: there is no re-injection (a new camera means a new MapView), so no setter.</summary>
        public MapCamera Camera { get; }

        /// <summary>Test seam for the style-transition clock. Production → <c>Time.unscaledTimeAsDouble</c>
        /// (UNSCALED: a theme change is a UI-class animation that must ease under <c>timeScale == 0</c>).
        /// <see cref="Text.SymbolPlacementSystem.EaseFade"/> runs on SCALED time, so the two clocks disagree
        /// under <c>timeScale != 1</c> — a known limitation.</summary>
        internal Func<double> NowSecondsOverride { get; set; }
        // A ternary, not `(NowSecondsOverride ?? DefaultNowSeconds)()`: that converts a method group to a delegate
        // per call, which allocates inside MapView_SteadyStateTick_DoesNotAllocateGCMemory's scope.
        private double NowSeconds => NowSecondsOverride != null ? NowSecondsOverride() : Time.unscaledTimeAsDouble;

        /// <summary>How long a restyled uniform binding eases from its old value to its new one. Backed by
        /// <see cref="MapViewConfig"/>, the ONLY source of the duration/delay: no style key is read.</summary>
        public Rendering.Layers.StyleTransition StyleTransition
            => new Rendering.Layers.StyleTransition
            {
                DurationSeconds = _config.StyleTransitionDurationSeconds,
                DelaySeconds    = _config.StyleTransitionDelaySeconds,
            };

        // Per-style-layer render bundles (fills + lines), built at SetStyle. Owns the materials.
        /// <summary>The per-style-layer render bundles owned by this view. <c>internal</c>: tests read counts
        /// via <c>MapViewTestExtensions</c> (InternalsVisibleTo).</summary>
        internal Rendering.Layers.RenderLayerSet Layers { get; } = new Rendering.Layers.RenderLayerSet();

        // The tile lifecycle — owned by MapView, ticked once per frame. Built in the ctor (needs only Layers).
        internal readonly Tile.TileManager TileManager;

        // Keeps the tile manager's selector and per-frame selection inputs in step with the config and camera.
        internal readonly TileSelectorBinding SelectorBinding;

        // ── The per-frame symbol placement path — a SEPARATE path from the tile lifecycle above, never a
        // static per-(tile,layer) mesh. Needs no ctor dependency (unlike TileManager).
        /// <summary>The dedicated per-frame symbol renderer. <c>internal</c>: test surface (job-parity /
        /// alloc / structural teeth read it via <c>MapViewTestExtensions</c>-style InternalsVisibleTo).</summary>
        internal SymbolPlacementSystem SymbolPlacementSystem { get; }

        /// <summary>
        /// Builds the view over its <paramref name="config"/> (the Inspector knobs, shared by reference with
        /// <see cref="MapViewComponent"/>) and a non-null <paramref name="camera"/>. The
        /// <see cref="TileManager"/> is created here; a data source is wired later via
        /// <see cref="SetStyle(string,System.Threading.CancellationToken)"/>.
        /// </summary>
        public MapView(MapViewConfig config, MapCamera camera)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            Camera  = camera ?? throw new ArgumentNullException(nameof(camera));
            // The PreparedTileCache's Enabled toggle + byte/count budget — maintainer-tunable Inspector
            // fields (placeholder budget defaults pending in-editor VRAM profiling).
            TileManager = new Tile.TileManager(Layers, _config.PreparedCache);
            SelectorBinding = new TileSelectorBinding(_config, Camera, TileManager);
            // Built here, after Camera is set: a field initializer would see a null Camera. Both base materials
            // are optional; a null one leaves that draw path inert (see MapMaterialSet.SymbolIconWorld).
            SymbolPlacementSystem = new SymbolPlacementSystem(Camera,
                _config.MaterialSet != null ? _config.MaterialSet.SymbolTextWorld : null,
                _config.MaterialSet != null ? _config.MaterialSet.SymbolIconWorld : null);
            // Non-local invariant: symbol data arrives through TileManager's per-tile worker factory, but the
            // tile lifecycle is pulled — LateUpdate hands over the loaded set and the subsystem reconciles.
            // The cache flag drives keep-warm-on-release, so symbols match the prepared mesh cache.
            SymbolSubsystem = new SymbolSubsystem(Camera,
                _config.PreparedCache.MaxCount, _config.PreparedCache.Enabled);
            TileManager.SymbolWorkerFactory = SymbolSubsystem;
        }

        // Production symbols (real map data), fed to SymbolPlacementSystem.Update each frame.
        internal readonly SymbolSubsystem SymbolSubsystem;

        /// <summary>The sun/sky/haze writers, wired via <see cref="SetEnvironment"/>. Null until wired; a
        /// style applies over it on every <see cref="SetStyle(string,System.Threading.CancellationToken)"/>.</summary>
        internal SceneEnvironment Environment { get; private set; }

        /// <summary>Points the view at <paramref name="environment"/> (built by <c>MapHost</c> over the scene's
        /// directional light). Disposes any previous environment.</summary>
        internal void SetEnvironment(SceneEnvironment environment)
        {
            Environment?.Dispose();
            Environment = environment;
        }

        // The SymbolRenderLayer objects themselves (same walk as _symbolStyleLayers, same order) —
        // handed to SymbolPlacementSystem.Update each frame so each layer's survivors draw with its own material/presenter.
        private readonly List<Rendering.Layers.SymbolRenderLayer> _symbolRenderLayers = new List<Rendering.Layers.SymbolRenderLayer>();

        // Reused scratch for the per-frame loaded-tile pull handed to the subsystem's reconcile (no alloc).
        private readonly List<Tile.LoadedTileKey> _loadedTileKeys = new List<Tile.LoadedTileKey>();

        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One frame of the live loop, driven from <c>MapViewComponent.LateUpdate</c>, which Unity runs after every
        /// <c>Update</c>, so it sees this frame's input. One ordered pass off one camera snapshot keeps tiles and
        /// symbols frame-coherent: commit the camera, move the tiles, place the symbols. Each telemetry provider
        /// publishes at the end of its own pass. Allocation-free in steady state.
        /// </summary>
        public void LateUpdate()
        {
            using var _lateUpdate = PmLateUpdate.Auto(); // umbrella: self-time = residual unmarked per-frame cost

            // 1. Commit the camera first, with the live DPI: the altitude framing, the selector's framing viewport,
            //    the tile rebase, symbol projection and BuildSceneFrame all read the committed pose.
            Camera.DevicePixelRatio = _config.DevicePixelRatio;
            using (PmCameraAdvance.Auto())
                Camera.SyncToCamera();
            Environment?.Update(NowSeconds, Camera);

            // ONE snapshot for the rest of the frame — tiles and symbols share it, so they can't diverge.
            CameraProperties   cameraProperties = Camera.CurrentProperties;
            Backend.SceneFrame sceneFrame;
            using (PmSceneFrame.Auto())
                sceneFrame = BuildSceneFrame(cameraProperties);

            // 2. Move the tiles. ApplyZoom first, so a fractional-zoom-only change still pushes uniforms. The style's
            //    logical px meet the device-pixel ratio only here, read from _config, which owns the ratio.
            using (PmApplyZoom.Auto())
            {
                // StyleTransition is re-read every frame, so a live change takes effect without a restyle.
                Layers.ApplyZoom(new Rendering.Layers.StyleFrameInputs(
                    cameraProperties.Zoom, _config.DevicePixelRatio, NowSeconds, StyleTransition));
                TileManager.PushLayerDrawGates();
            }

            SelectorBinding.Ensure();
            ApplyVisibilityGroups();
            using (PmManagerUpdate.Auto())
                TileManager.Update(cameraProperties, SelectorBinding.BuildConfig(), NowSeconds, in sceneFrame);

            // Pull the sprite sheet, which the symbol subsystem owns and fetches, into the fill-pattern layers each
            // frame. SetSprites early-outs on an unchanged pair.
            Layers.SetSprites(SymbolSubsystem.SpriteAtlas, SymbolSubsystem.IconTexture);

            // 3. Place the symbols against the SAME snapshot the tiles used (never a second BuildSceneFrame).
            //    A style with no symbol layers simply has nothing to place.
            if (SymbolSubsystem.HasSymbolLayers)
            {
                // One clock read shared by ReconcileLoadedTiles' and CurrentBatch's grace windows.
                double now = Time.timeAsDouble;

                // Pull the post-Update loaded-tile set and reconcile the symbol store: it restores kept-warm symbols
                // for cache-hit re-entries and releases tiles that left cover.
                using (PmSymbolCollect.Auto())
                {
                    TileManager.CollectLoadedTileKeys(_loadedTileKeys);
                    SymbolSubsystem.ReconcileLoadedTiles(_loadedTileKeys, now);
                    // Start ≤MaxBuildsPerFrame queued symbol builds and coalesce the atlas upload.
                    // AFTER reconcile so its loaded-set snapshot drops builds for tiles that just left cover.
                    SymbolSubsystem.PumpBuilds();
                }

                // The per-frame winner plan: collect and cross-tile dedup, recording each winner's baked-block slot.
                SymbolGatherPlan plan;
                using (PmSymbolBatch.Auto())
                    plan = SymbolSubsystem.CurrentBatch();
                // Push the far-distance cull fraction live, then gather, project, collide and present each slot
                // through its own SymbolRenderLayer.
                SymbolPlacementSystem.SymbolMaxDistanceFraction = _config.SymbolMaxDistanceFraction;
                SymbolPlacementSystem.Update(sceneFrame, plan, SymbolSubsystem.Atlas, Time.deltaTime,
                    _symbolRenderLayers, SymbolSubsystem.IconTexture);
            }
        }

        /// <summary>
        /// Builds the per-frame <see cref="Backend.SceneFrame"/> (Level-2 of the two-level RTC) from the
        /// projection and the clamped camera look-at. For Web-Mercator the rebase is the identity, so the frame
        /// equals <c>SceneFrame.Mercator(cam.CenterMercator())</c> bit-for-bit.
        /// Non-local invariant: <see cref="MapCamera.CameraRelativePosition"/> is fresh only because
        /// <c>LateUpdate</c> runs <see cref="MapCamera.SyncToCamera"/> before this call.
        /// </summary>
        /// <remarks><c>internal</c> so a test can call it after <see cref="MapCamera.SyncToCamera"/> without
        /// driving the whole <see cref="LateUpdate"/>.</remarks>
        internal Backend.SceneFrame BuildSceneFrame(in CameraProperties cam)
        {
            IProjection proj = Camera.Projection;
            var lookAt = new GeoCoordinate
            {
                Latitude  = proj.ClampValidLatitude(cam.LookAt.Latitude),
                Longitude = cam.LookAt.Longitude,
            };
            return new Backend.SceneFrame
            {
                SceneOriginRender      = proj.Project(lookAt),
                Rebase                 = math.transpose(proj.TangentBasisAt(lookAt)),
                CameraRelativePosition = Camera.CameraRelativePosition,
            };
        }

        /// <summary>Hands the configured visibility groups to the tile manager, which compares them with the ones it holds and
        /// applies a change at once. Called every frame and at every style load, so an Inspector edit takes effect live.</summary>
        internal void ApplyVisibilityGroups() => TileManager.SetVisibilityGroups(_config.VisibilityGroups);

        /// <summary>
        /// Disposes the tiles, then the layer materials, then the symbol placement system, then the symbol
        /// subsystem, then the environment; idempotent. Non-local invariant: tiles go first because their renderers reference layer
        /// materials, and <see cref="Layers"/> goes before <see cref="SymbolPlacementSystem"/> because a symbol
        /// layer's presenter must not outlive the slot <see cref="Mesh"/> that system owns.
        /// </summary>
        public void Teardown()
        {
            // Each step is isolated and logs its exception, so one fault cannot strand the rest. Play-mode Stop
            // can destroy the Entities World before this runs, so an entity touch in TileManager.Dispose() may throw.
            DisposeStep(TileManager, nameof(TileManager)); // tiles first — their renderers reference Layers' materials
            DisposeStep(Layers,      nameof(Layers));
            DisposeStep(SymbolPlacementSystem,      nameof(SymbolPlacementSystem));
            DisposeStep(SymbolSubsystem,     nameof(SymbolSubsystem));     // destroy the shared glyph atlas texture + manager
            DisposeStep(Environment,         nameof(Environment));        // restore the camera clear + RenderSettings fog

            static void DisposeStep(System.IDisposable subsystem, string name)
            {
                try { subsystem?.Dispose(); }
                catch (System.Exception ex)
                {
                    UnityEngine.Debug.LogError(
                        $"[MapView.Teardown] {name}.Dispose() threw — continuing so the remaining subsystems " +
                        $"still release (a partial teardown beats a stranded graph). {ex}");
                }
            }
        }
    }
}
