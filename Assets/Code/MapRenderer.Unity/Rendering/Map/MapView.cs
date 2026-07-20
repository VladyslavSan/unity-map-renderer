using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Source;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Unity.Rendering.Map
{
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

    /// <summary>
    /// The live multi-tile render loop — per-layer styled fill/line rendering. A <b>plain C# class</b>
    /// (the MonoBehaviour host is <see cref="MapViewComponent"/>): correct by construction — it is built with
    /// its <see cref="MapViewConfig"/> and a non-null <see cref="MapCamera"/>, and owns its
    /// <see cref="RenderLayerSet"/> and <see cref="TileManager"/> from construction, so there are no
    /// "is it wired yet" null guards and no lifecycle flag: before <see cref="SetStyle(string,CancellationToken)"/>
    /// runs it is simply an empty map whose <see cref="LateUpdate"/> selects a cover but has no sources to fetch from,
    /// so it renders nothing — a safe no-op, not an invalid state.
    ///
    /// <para>Architecture: one render bundle per fill/line style layer (declared/painter's order), each with a
    /// per-layer Material + ZoomStyleApplier, held by the <see cref="RenderLayerSet"/>. The tile lifecycle
    /// (cover→fetch→build→consume→evict, disposal/leak guards) lives in <see cref="TileManager"/>, ticked
    /// once per frame. Per-frame: push zoom uniforms, snap the render origin to the look-at, rebuild, tick.</para>
    ///
    /// <para>Steady-state no-GC: the ApplyZoom loop is a plain <c>for</c> over a <c>List</c> (struct
    /// enumerator); TileManager early-outs before any allocation when the cover is clean and nothing pends.</para>
    ///
    /// <para>Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.</para>
    /// </summary>
    public sealed class MapView
    {
        // ── Profiler markers (allocation-free; static readonly = constructed once at type-init) ──
        //   LateUpdate       — UMBRELLA over the whole per-frame pipeline. Its self-time (total − the children
        //                      below) is the residual unmarked cost: if it is ~0 every per-frame span is mapped.
        //   CameraAdvance    — commit this frame's camera pose (SyncToCamera: pose math + transform/clip push).
        //   ApplyZoom        — push zoom uniforms into every layer material (scales with layer count).
        //   InstancedRebuild — drive the render backend per frame (on Entities this ticks the EG system groups).
        //   ManagerTick      — cover select + request/release + build pump (CoverSelect/FetchPoll nest under it).
        //   SceneFrame       — build the per-frame floating-origin scene frame (projection Project + tangent basis).
        //   SymbolBatch      — aggregate the frame's active labels: A-3 cross-tile dedup (CollectInto) + LabelInstance
        //                      → SoA batch build. Runs between Symbol.Collect and Labels.Tick — a managed main-thread
        //                      hot spot in its own right (grows with the on-screen label count at high zoom).
        private static readonly ProfilerMarker PmLateUpdate       = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.View.LateUpdate");
        private static readonly ProfilerMarker PmCameraAdvance     = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Camera.Advance");
        private static readonly ProfilerMarker PmApplyZoom        = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.View.ApplyZoom");
        private static readonly ProfilerMarker PmInstancedRebuild = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.View.InstancedRebuild");
        private static readonly ProfilerMarker PmManagerTick      = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.ManagerTick");
        private static readonly ProfilerMarker PmSceneFrame       = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.View.SceneFrame");
        // The symbol reconcile that runs before the label aggregation (A-1 pull/reconcile + PumpBuilds).
        private static readonly ProfilerMarker PmSymbolCollect    = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.Collect");
        // The label AGGREGATION (A-3 cross-tile dedup CollectInto + SoA batch build) — its own marker so the
        // dedup/build cost is not misattributed to the unmarked LateUpdate self-time (it feeds Labels.Tick).
        private static readonly ProfilerMarker PmSymbolBatch      = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Symbol.BatchBuild");

        // ── Injected collaborators (correct by construction — never null) ────────────────────────
        private readonly MapViewConfig _config;

        /// <summary>The map camera, owned by this view — the single source of camera state. Non-null, set
        /// once at construction: there is no re-injection (a new camera means a new MapView), so no setter.</summary>
        public MapCamera Camera { get; }

        private StyleDocument _style;

        // Per-style-layer render bundles (fills + lines), built at SetStyle. Owns the materials.
        /// <summary>The per-style-layer render bundles owned by this view. <c>internal</c>: tests read counts
        /// via <c>MapViewTestExtensions</c> (InternalsVisibleTo).</summary>
        internal Style.RenderLayerSet Layers { get; } = new Style.RenderLayerSet();

        // The tile lifecycle — owned by MapView, ticked once per frame. Built in the ctor (needs only Layers).
        internal readonly Tile.TileManager TileManager;

        // ── S20 Slice 1: the per-frame label placement path (F1) — a SEPARATE path from the tile lifecycle
        // above, never a static per-(tile,layer) mesh (T5). Needs no ctor dependency (unlike TileManager).
        /// <summary>The dedicated per-frame label renderer. <c>internal</c>: test surface (job-parity /
        /// alloc / structural teeth read it via <c>MapViewTestExtensions</c>-style InternalsVisibleTo).</summary>
        internal LabelPlacementSystem Labels { get; }

        /// <summary>
        /// Candidate labels for this frame — no collision yet (Slice 1: every label whose anchor projects
        /// on-screen is placed; Slice 2 adds the greedy sort-key survivor selection). <b>Demo-only seam for
        /// S20 Slice 1</b> (<c>SyntheticLabelSource</c> sets this from hand-built labels over a real SDF
        /// atlas); S105 replaces the setter with real data from a parsed <c>Symbol</c> style layer +
        /// decoded point features — this property's shape does not need to change for that.
        /// </summary>
        public IReadOnlyList<LabelInstance> LabelInstances { get; set; }

        /// <summary>The uploaded R8 SDF glyph atlas backing every <see cref="LabelInstances"/> quad's UVs.
        /// Demo-only seam for S20 Slice 1 (see <see cref="LabelInstances"/>) — MapView does not own this
        /// texture (it is not disposed by <see cref="Teardown"/>); its owner disposes it.</summary>
        public GlyphAtlasTexture LabelAtlas { get; set; }

        /// <summary>
        /// Builds the view over its <paramref name="config"/> (the Inspector knobs, shared by reference with
        /// <see cref="MapViewComponent"/>) and a non-null <paramref name="camera"/>. The
        /// <see cref="TileManager"/> is created here; a data source is wired later via
        /// <see cref="SetStyle(string,CancellationToken)"/>.
        /// </summary>
        public MapView(MapViewConfig config, MapCamera camera)
        {
            _config     = config ?? throw new ArgumentNullException(nameof(config));
            Camera      = camera ?? throw new ArgumentNullException(nameof(camera));
            // S82: the PreparedTileCache's Enabled toggle + byte/count budget — maintainer-tunable Inspector
            // fields (placeholder budget defaults pending in-editor VRAM profiling, stage Risk 3).
            TileManager = new Tile.TileManager(Layers, _config.PreparedCache);
            // S20: one label system per view, owning this view's camera (constructed here, after Camera is
            // set — a field initializer would see a null Camera). I5b: the icon base material rides alongside
            // the text one — both optional (null → that draw path stays inert, see MapMaterialSet.SymbolIcon's doc).
            Labels      = new LabelPlacementSystem(Camera,
                _config.MaterialSet != null ? _config.MaterialSet.SymbolText : null,
                _config.MaterialSet != null ? _config.MaterialSet.SymbolIcon : null);
            // S105: the decoupled symbol-label subsystem produces the real map labels Labels.Tick renders.
            // D11/E2: per-layer materials (SymbolText clone + text-halo-* bind) now live on each
            // SymbolRenderLayer (Layers.Build), not here. A5b: DATA arrives via TileManager's per-tile KICK
            // (_symbols implements ISymbolTileWorkerFactory); the tile LIFECYCLE is PULLED — each frame we
            // hand it TileManager's loaded set and it reconciles (no release/restore callbacks). cacheEnabled
            // drives keep-warm-on-release so it matches the prepared mesh cache.
            _symbols    = new SymbolLabelSubsystem(Camera,
                _config.PreparedCache.MaxCount, _config.PreparedCache.Enabled);
            TileManager.SymbolWorkerFactory = _symbols;
        }

        // S105: production symbol labels (real map data), fed to Labels.Tick each frame. The demo
        // LabelInstances/LabelAtlas seam below is used only when the style has NO symbol layers.
        private readonly SymbolLabelSubsystem _symbols;

        // D10: reused scratch for SetStyle's symbol-layer derivation (below) — a restyle never allocates a
        // fresh list; the single registry (RenderLayerFactory) is walked once via Layers.Layers.
        private readonly List<Symbol.StyleLayer> _symbolLayerScratch = new List<Symbol.StyleLayer>();
        // D11/E2: the SymbolRenderLayer objects themselves (same walk as _symbolLayerScratch, same order) —
        // handed to Labels.Tick each frame so each layer's survivors draw with its own material/presenter.
        private readonly List<Style.SymbolRenderLayer> _symbolRenderLayers = new List<Style.SymbolRenderLayer>();
        // A-1: reused scratch for the per-frame loaded-tile pull handed to the subsystem's reconcile (no alloc).
        private readonly List<Tile.LoadedTileKey> _symbolLoadedScratch = new List<Tile.LoadedTileKey>();

        // ── SetStyle — the style is the single source of truth ─────────────────────────────────
        // There is no separate "Initialise": the map is fully valid at construction (an empty map whose
        // Tick safely no-ops until data is wired). Loading/changing data is SetStyle — an async operation
        // (fetch style + TileJSON) that fits the MonoBehaviour host's async Start naturally.


        /// <summary>S83b: the id of the active style (forward contract for S82's prepared-tile cache).
        /// For <see cref="SetStyle(string,CancellationToken)"/> it is the style URI; for the
        /// <see cref="StyleDocument"/> overload it is the caller-supplied id.</summary>
        internal string StyleId { get; private set; }

        // S83b loader seams — production defaults; tests inject counting/offline fakes via InternalsVisibleTo.
        internal System.Func<string, CancellationToken, UniTask<string>> DocumentLoaderOverride;
        internal System.Func<string, IDataSource>                        TileSourceFactoryOverride;

        /// <summary>
        /// S83b: load a style from <paramref name="styleUri"/> (file:// or http(s)://), resolve each of its
        /// sources (inline <c>tiles[]</c>, else S83a TileJSON), wire one data pipeline per source-id, and
        /// build the render layers — each fetching from ITS OWN source. <c>styleId == styleUri</c>.
        /// </summary>
        public async UniTask SetStyle(string styleUri, CancellationToken ct = default)
        {
            var loader = DocumentLoaderOverride ?? StyleDocumentLoader.LoadTextAsync;
            string json = await loader(styleUri, ct);
            StyleDocument style = StyleParser.Parse(json);
            await SetStyle(style, styleUri, ct);
        }

        /// <summary>
        /// S83b: apply an already-parsed <paramref name="style"/> with a caller-supplied
        /// <paramref name="styleId"/>. A second call RESTYLES: <see cref="RenderLayerSet"/> rebuilds and the
        /// source registry diffs (unchanged sources keep their warm pipeline; removed are torn down).
        /// </summary>
        public async UniTask SetStyle(StyleDocument style, string styleId, CancellationToken ct = default)
        {
            // Epic A / A2 (design §E step 3, MED 4 / HIGH b — transactional restyle, option (i) bounded):
            // the ONE await runs FIRST, before any layer/identity mutation, so the OLD style's layers/
            // materials/backend/identity stay fully live through the resolution window — a delayed or
            // cancelled restyle leaves the previous map rendering (no blank background, no destroyed-
            // material window, no new-cache-token probe under old visuals). BuildSourceSpecs now derives its
            // source-ids from style.Layers directly (RenderLayerFactory.TryGetFetchSource), NOT from the
            // not-yet-built Layers.Layers.
            var specs = await BuildSourceSpecs(style, ct);
            ct.ThrowIfCancellationRequested(); // last safe abort — nothing mutated yet (old style stays intact)

            // Round-4 TOCTOU fix: MapMaterialSet fields (and _config.MaterialSet itself) are live-mutable, so
            // capture ONCE here and validate that SAME captured reference — a concurrent mutation between
            // this check and its use (Layers.Build below) cannot slip a null base past validation (no await
            // between validate and use). Fail-loud: an unconfigured base is a developer configuration error.
            var materialSet = _config.MaterialSet;
            materialSet.Validate();

            // Identity commits HERE (not before the await, HIGH b) — a delayed restyle must not run the OLD
            // layers/pipelines under the NEW cache token, and a cancel above must not report the new identity.
            _style  = style;
            StyleId = styleId;
            // S82: the PreparedTileCache's opaque cache-key token — constant default until S83 supplies a
            // real per-style id (Risk 2); set before SetSources so a hit/miss probe this Tick already sees it.
            TileManager.CurrentStyle = new Tile.StyleToken(StyleId);

            Layers.Build(_style, Camera.CurrentProperties.Zoom, materialSet);
            // D10: derive the symbol layers from the just-built set (RenderLayerFactory is the sole
            // registry) instead of re-walking style.Layers with an is-check (kills §1.6). One walk, two
            // lists (D11/E2): the typed StyleLayer for the subsystem, the owning SymbolRenderLayer (its
            // material + presenter) for Labels.Tick — same order, so the ordinal mapping stays 1:1.
            // A2: the Mercator-only background gate is gone — background is now a per-covered-tile TileMesh
            // layer projected through the same IProjection fill/line use, so the globe renders it correctly.
            _symbolLayerScratch.Clear();
            _symbolRenderLayers.Clear();
            foreach (var layer in Layers.Layers)
                if (layer is Style.SymbolRenderLayer s) { _symbolLayerScratch.Add(s.SymbolLayer); _symbolRenderLayers.Add(s); }
            _symbols.SetStyle(_style, _symbolLayerScratch); // S105: group symbol layers + (re)build the shared glyph pipeline

            TileManager.SetSources(specs, _config.Backend);
        }

        /// <summary>
        /// S83b: resolves each rendered source-id of <paramref name="style"/> into a
        /// <see cref="Tile.TileManager.SourceSpec"/>. Inline <c>tiles[]</c> short-circuits (no TileJSON
        /// fetch); a <c>url</c>-only source fetches its TileJSON ONCE and resolves via S83a. Failure
        /// isolation: an offline/404/malformed TileJSON logs a warning and skips THAT source.
        ///
        /// Epic A / A2 (HIGH b/c): runs BEFORE <see cref="Style.RenderLayerSet.Build"/> now (the transactional
        /// restyle reorder — see <see cref="SetStyle(StyleDocument,string,CancellationToken)"/>), so it can no
        /// longer walk the built <see cref="Layers"/> set. Walks <paramref name="style"/>'s raw layers through
        /// <see cref="Style.RenderLayerFactory.TryGetFetchSource"/> instead — the ONE registry of which style
        /// layers fetch MVT tiles (fill/line/symbol with a non-empty source; background is source-less by
        /// design; raster/circle/hillshade/unknown are excluded so no non-MVT bytes reach the MVT decode).
        /// </summary>
        private async UniTask<List<Tile.TileManager.SourceSpec>> BuildSourceSpecs(
            StyleDocument style, CancellationToken ct)
        {
            var loader  = DocumentLoaderOverride    ?? StyleDocumentLoader.LoadTextAsync;
            var factory = TileSourceFactoryOverride ?? TileDataSourceFactory.Create;

            // Distinct rendered source-ids in declared order.
            var seen    = new HashSet<string>();
            var ordered = new List<string>();
            foreach (var sl in style.Layers)
            {
                if (!Style.RenderLayerFactory.TryGetFetchSource(sl, out string sid)) continue;
                if (seen.Add(sid)) ordered.Add(sid);
            }

            var specs = new List<Tile.TileManager.SourceSpec>(ordered.Count);
            foreach (string sid in ordered)
            {
                SourceDefinition def = style.GetSource(sid);
                if (def == null)
                {
                    Debug.LogWarning($"[MapView.SetStyle] layer references undefined source '{sid}' — skipped.");
                    continue;
                }

                // Fetch + resolve the TileJSON ONCE when the source is url-only (inline tiles[] short-circuits).
                if (SourceResolver.NeedsTileJson(def))
                {
                    try
                    {
                        string tjText = await loader(def.Url, ct);
                        SourceResolver.Resolve(def, TileJsonParser.Parse(tjText));
                    }
                    catch (System.OperationCanceledException) { throw; }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[MapView.SetStyle] TileJSON load failed for source '{sid}' " +
                                         $"({def.Url}): {ex.Message}. Source skipped (no tiles).");
                        continue; // failure isolation — other sources still wire
                    }
                }

                if (def.Tiles == null || def.Tiles.Length == 0)
                {
                    Debug.LogWarning($"[MapView.SetStyle] source '{sid}' resolved to no tiles — skipped.");
                    continue;
                }

                string template = def.Tiles[0]; // first template (no multi-host round-robin yet)
                var key = Tile.TileManager.SourceKey.From(def);
                // Epic A / A7: the ONE production site that wraps the byte fetcher into the raised
                // ITileFeatureSource seam — TileManager never names the byte-level type (F-1).
                specs.Add(new Tile.TileManager.SourceSpec(
                    sid, key, def.MinZoom, def.MaxZoom, () => new Tile.Processing.MvtTileFeatureSource(factory(template))));
            }
            return specs;
        }


        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One frame of the live loop, mirroring (and driven from) <c>MapViewComponent.LateUpdate</c>. Runs in
        /// LateUpdate on purpose: the input <c>Controller</c> mutates the camera props in its <c>Update</c>, and
        /// Unity runs every LateUpdate after every Update, so this pipeline is GUARANTEED to see this frame's
        /// input — no execution-order attributes needed. The whole per-frame pipeline lives HERE, in one ordered
        /// pass off a SINGLE camera snapshot, so tiles and labels are frame-coherent by construction (the pan-lag
        /// fix — they used to sample the look-at in two different phases):
        /// <list type="number">
        ///   <item>commit the camera (DPI refresh + <see cref="MapCamera.SyncToCamera"/>) — the merged input
        ///         state from every controller this frame;</item>
        ///   <item>move the tiles — push zoom uniforms, then rebase every loaded tile onto the look-at's
        ///         floating origin;</item>
        ///   <item>place the labels — project their anchors against the SAME snapshot + just-committed camera.</item>
        /// </list>
        /// Allocation-free in steady state.
        /// </summary>
        public void LateUpdate()
        {
            using var _lateUpdate = PmLateUpdate.Auto(); // umbrella: self-time = residual unmarked per-frame cost

            // 1. Update the camera FIRST — commit this frame's merged input to the Unity camera, so the tile
            //    rebase and the label projection below both read the just-committed pose. DPI is refreshed from
            //    the live config before the commit (it feeds the altitude framing). This ordering is also what
            //    keeps Camera.CameraRelativePosition fresh for BuildSceneFrame one line below.
            Camera.DevicePixelRatio = _config.DevicePixelRatio;
            using (PmCameraAdvance.Auto())
                Camera.SyncToCamera();

            // ONE snapshot for the rest of the frame — tiles and labels share it, so they can't diverge.
            CameraProperties cameraProperties = Camera.CurrentProperties;
            Backend.SceneFrame sceneFrame;
            using (PmSceneFrame.Auto())
                sceneFrame = BuildSceneFrame(cameraProperties);

            // 2. Move the tiles. ApplyZoom first — so a fractional-zoom-only change always pushes uniforms
            //    (fill/line zoom paint, zoom-step dasharrays for pixel line width).
            using (PmApplyZoom.Auto())
                Layers.ApplyZoom(cameraProperties.Zoom);

            // Camera-relative rendering: snap the render origin to the look-at every frame, then place all
            // loaded tiles relative to it — best float precision, no threshold/rebase machinery.
            // S91-C Slice 2: place tiles via the projection-agnostic scene frame built from the launch-time
            // projection + the look-at. Mercator: rebase = identity and SceneOriginRender = (mercX, 0, mercZ)
            // (== SceneFrame.Mercator(cam.CenterMercator())), so placement is bit-for-bit the pre-S91 translation.
            // Globe: rebase rotates every tile into the look-at's local ENU frame (up = +Y), so the same
            // CameraPoseMath.ComputeRelativePose orbit frames it.
            using (PmInstancedRebuild.Auto())
                TileManager.InstancedRebuild(sceneFrame);

            EnsureSelector();
            using (PmManagerTick.Auto())
                TileManager.Tick(cameraProperties, BuildTileSelectionConfig());

            // 3. Place the labels against the SAME snapshot the tiles used (never a second BuildSceneFrame).
            //    Production: the symbol subsystem's real map labels (when the style has symbol layers).
            //    Fallback: the demo LabelInstances/LabelAtlas seam (SyntheticLabelSource), used only when a
            //    style has NO symbol layers — so a leftover demo component can't mask the real feature.
            // Push the live-tunable label knobs (read from the shared config every Tick, so an Inspector tweak
            // during Play takes effect the same frame — mirrors the DevicePixelRatio push above).
            Labels.MinTileScreenCoverage = _config.LabelTileCoverageCull;
            if (_symbols.HasSymbolLayers)
            {
                // A-1: PULL the current loaded-tile set (post-Tick, so cache-hit adds and releases are already
                // reflected) and reconcile the label store before collecting — restores kept-warm labels for
                // cache-hit re-entries, releases tiles that left cover. Then aggregate the active labels.
                using (PmSymbolCollect.Auto())
                {
                    TileManager.CollectLoadedTileKeys(_symbolLoadedScratch);
                    // Pass this frame's wall-clock so the store can time the departing (leave-cover) fade-out window.
                    _symbols.ReconcileLoadedTiles(_symbolLoadedScratch, Time.timeAsDouble);
                    // Stall #1: start ≤MaxBuildsPerFrame queued symbol builds and coalesce the atlas upload.
                    // AFTER reconcile so its loaded-set snapshot drops builds for tiles that just left cover.
                    _symbols.PumpBuilds();
                }
                // Lever C: the blittable label batch — collect (+ cross-tile dedup) + LabelInstance→SoA, rebuilt every
                // frame (allocation-free; the version cache was removed). Hoisted out of the Labels.Tick argument so
                // its managed dedup/build cost is MARKED (Symbol.BatchBuild), not folded into the umbrella self-time.
                SymbolLabelBatch batch;
                using (PmSymbolBatch.Auto())
                    batch = _symbols.CurrentBatch();
                // Then project/collide/build the placement, presenting each slot through its own SymbolRenderLayer
                // (D11/E2 — material + persistent presenter).
                Labels.Tick(sceneFrame, batch, _symbols.Atlas, Time.deltaTime,
                    _symbolRenderLayers, _symbols.IconTexture);
            }
            else
            {
                Labels.Tick(sceneFrame, LabelInstances, LabelAtlas, Time.deltaTime);
            }
        }

        /// <summary>
        /// Builds the per-frame <see cref="Backend.SceneFrame"/> (Level-2 of the two-level RTC) from the
        /// launch-time projection and the camera look-at: the render-space scene origin
        /// (<c>projection.Project(lookAt)</c>) and the render→look-at-local-ENU rebase
        /// (<c>transpose(projection.TangentBasisAt(lookAt))</c>). The look-at latitude is clamped to the
        /// projection's valid range (<see cref="IProjection.ClampValidLatitude"/>) — ±85.05° for Web-Mercator
        /// (matching <c>CenterMercator</c>), ±90° for the globe. For Web-Mercator the basis is the identity, so
        /// the frame equals <c>SceneFrame.Mercator(cam.CenterMercator())</c> bit-for-bit.
        ///
        /// <para>Folds in <see cref="MapCamera.CameraRelativePosition"/> — fresh because <c>LateUpdate</c> runs
        /// <see cref="MapCamera.SyncToCamera"/> before this call, never a <c>transform.position</c> round-trip.</para>
        /// </summary>
        /// <remarks><c>internal</c> (not <c>private</c>) solely so the single-owner test can call it directly
        /// after <see cref="MapCamera.SyncToCamera"/> without re-driving the whole <see cref="LateUpdate"/>
        /// pipeline — no other production caller.</remarks>
        internal Backend.SceneFrame BuildSceneFrame(in CameraProperties cam)
        {
            IProjection proj = Camera.Projection;
            var lookAt = new GeoCoordinate
            {
                Latitude  = proj.ClampValidLatitude(cam.LookAt.Latitude),
                Longitude = cam.LookAt.Longitude,
            };
            return new Backend.SceneFrame(proj.Project(lookAt), math.transpose(proj.TangentBasisAt(lookAt)),
                Camera.CameraRelativePosition);
        }

        // ── S71: visible-tile selector, rebuilt only when a selection input (or the projection) changes ──
        private (bool globe, TileLodMode lod, int minZoom, int maxZoom, int onScreenPx,
                 double mercFarCap, double globeFarCap)? _selectorInputs;

        private void EnsureSelector()
        {
            var tileSelection = _config.TileSelection;
            bool globe = Camera.Projection is SphericalProjection;
            var key = (globe, tileSelection.LodMode, tileSelection.MinZoom, tileSelection.MaxZoom,
                       tileSelection.OnScreenTilePx, tileSelection.MercatorFarPlaneCap, tileSelection.GlobeFarPlaneCap);
            if (TileManager.Selector != null && _selectorInputs == key) return;
            _selectorInputs = key;

            // One universal FrustumTileSelector for every projection (occlusion via IProjection). LOD strategy
            // from config; far-plane policy per projection — ray-sphere for a self-occluding globe
            // (curvature-correct: tight near, limb far), geometry-aware for the flat atlas. The camera gets the
            // SAME far so the rendered frustum is byte-for-byte the selected one.
            ITileLodStrategy lod = tileSelection.LodMode == TileLodMode.ScreenSpaceLod
                ? new ScreenSpaceLodStrategy()
                : new FlatLodStrategy();
            IFarPlanePolicy far = Camera.Projection.TryGetHorizonOccluder(out _, out double occRadius)
                ? new RaySphereFarPlane(occRadius, tileSelection.GlobeFarPlaneCap)
                : new GeometryAwareFarPlane(tileSelection.MercatorFarPlaneCap);

            Camera.FarPlanePolicy = far;
            TileManager.Selector = new FrustumTileSelector(
                tileSelection.MinZoom, tileSelection.MaxZoom, tileSelection.OnScreenTilePx, lod, far);
        }

        /// <summary>
        /// The per-frame view inputs the selector consumes. The camera IS the viewport: the framing viewport
        /// is the wrapped Unity camera's live pixel size (<see cref="MapCamera.ViewportPx"/>), normalised to
        /// LOGICAL pixels by <see cref="MapViewConfig.DevicePixelRatio"/> (S86 DPI slice) so an on-screen tile
        /// is the same physical size across panel densities; the projection is the pixel↔ground service the
        /// camera owns (Web-Mercator today).
        /// </summary>
        private Tile.TileManager.TileSelectionConfig BuildTileSelectionConfig()
            => new Tile.TileManager.TileSelectionConfig
            {
                FramingViewportPx       = Camera.ViewportPx / _config.DevicePixelRatio,
                Projection              = Camera.Projection,
                MaxConsumesPerTick        = _config.MaxConsumesPerTick,
                MaxMeshBuildsPerTick = _config.MaxMeshBuildsPerTick,
                MaxVerticesPerTick      = _config.MaxVerticesPerTick,
                MaxReleasesPerTick      = _config.MaxReleasesPerTick,
            };

        /// <summary>
        /// S85: pull-based tile/render telemetry (observability only — never referenced from the live tile
        /// loop). Forwards to <see cref="Tile.TileManager.CaptureTelemetry"/>; <see cref="TileManager"/> is
        /// never null (built at construction), so there is no "before init" case to special-case here — an
        /// unstyled view simply reports an empty cover.
        /// </summary>
        internal TileTelemetrySnapshot CaptureTelemetry() => TileManager.CaptureTelemetry();

        /// <summary>
        /// Pull-based SYMBOL-label telemetry (observability only): the label subsystem's active/cached tile
        /// counts + the placement pass's last-Tick candidate/survivor/quad counts. Composed here because the
        /// subsystem (<see cref="_symbols"/>) and placement system (<see cref="Labels"/>) are owned by the
        /// view, not the <see cref="TileManager"/>.
        /// </summary>
        internal SymbolTelemetrySnapshot CaptureSymbolTelemetry() => new SymbolTelemetrySnapshot
        {
            ActiveLabelTiles        = _symbols.ActiveTileCount,
            CachedLabelTiles        = _symbols.CachedTileCount,
            InputLabelCount         = Labels.LastInputLabelCount,
            DistanceCulledLabels    = Labels.LastDistanceCulledCount,
            HorizonCulledLabels     = Labels.LastHorizonCulledCount,
            CollisionCandidateCount = Labels.LastCandidateCount,
            CollisionSurvivorCount  = Labels.LastSurvivorCount,
            PlacedQuadCount         = Labels.LastQuadCount,
        };

        /// <summary>
        /// Releases all tile resources (via the <see cref="Tile.TileManager"/>), then disposes the
        /// RenderLayerSet's materials, then the label placement system's mesh/material/native buffers.
        /// Order matters TWICE: tiles first — their renderers reference layer materials — and (E2)
        /// <see cref="Layers"/> before <see cref="Labels"/> — a <see cref="Style.SymbolRenderLayer"/>'s
        /// presenter (destroyed by <c>Layers.Dispose()</c>) references a slot <see cref="Mesh"/> owned by
        /// <see cref="Labels"/>; a MeshRenderer must not outlive the mesh it points at. <see cref="Labels"/>
        /// is NOT responsible for <see cref="LabelAtlas"/> — that texture is demo/S105-owned, disposed by
        /// its own owner, never here. Idempotent (every dispose here is). The MonoBehaviour host calls
        /// this from OnDestroy.
        /// </summary>
        public void Teardown()
        {
            TileManager.Dispose();  // tiles first — their renderers reference Layers' materials
            Layers.Dispose();
            Labels.Dispose();
            _symbols.Dispose();     // S105: destroy the shared glyph atlas texture + manager
        }
    }
}
