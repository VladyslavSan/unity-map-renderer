using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
// MapRenderer.Jobs not used directly in MapView — StyledFillTileBuilder owns projection math.

namespace MapRenderer.Unity
{
    /// <summary>
    /// S40 live multi-tile render loop — per-layer styled fill rendering.
    ///
    /// Replaces the S06 single-shared-material path with one styled draw per <c>fill</c> style layer,
    /// ordered by painter's algorithm (LayerDrawOrder), filtered by S10 FeatureSelector, painted by
    /// S13 FillPaint (per-feature data-driven color baked into vertex stream via StyledFillTileBuilder).
    ///
    /// Architecture:
    ///   • <see cref="StyleDocument"/> is provided at <see cref="Initialise"/> time. MapView iterates
    ///     its fill layers once on initialise to build a <see cref="FillLayerRecord"/> list — one record
    ///     per fill style layer in declared (painter's) order. Each record holds a per-layer Material
    ///     instance (SRP-batcher-safe: never MaterialPropertyBlock) and a ZoomStyleApplier.
    ///   • Materials are SHARED across tiles for the same style layer: every tile's child renderer for
    ///     layer <em>i</em> uses <c>_layerRecords[i].Material</c>. Data-driven per-feature color lives
    ///     in the vertex stream (baked by StyledFillTileBuilder), so the material itself is tile-neutral.
    ///   • Per-frame: <see cref="ApplyZoom"/> pushes zoom uniforms into each layer's material before
    ///     anything else in Tick — so a fractional-zoom-only change (bearing/pitch) still updates
    ///     uniforms even when the cover is clean and the early-out fires immediately after.
    ///   • On tile build: <see cref="KickTessellationTask"/> iterates fill layers in order, kicks a
    ///     background UniTask per tile, then <see cref="ConsumeTessellationResults"/> polls on later frames.
    ///   • Eviction: the tile container GameObject (and all child renderers) are destroyed. Layer
    ///     Material instances live on <c>_layerRecords</c> and are disposed only in OnDestroy.
    ///
    /// S47 async tessellation (S51: Task → UniTask):
    ///   The managed decode/assemble/earcut/project loop runs inside
    ///   <c>UniTask.RunOnThreadPool(configureAwait: false)</c> (ThreadPool), so the Unity main thread
    ///   never blocks on tessellation during Update. configureAwait: false keeps completion on the
    ///   ThreadPool so IsCompleted is true immediately, enabling synchronous polling in DrainTessellation
    ///   and OnDestroy without a PlayerLoop dependency. PumpPendingBuilds polls the fetch UniTask; when
    ///   fetch completes, a tessellation UniTask is kicked. On a later frame, ConsumeTessellationResults
    ///   polls completed tessellation UniTasks and does only the main-thread UploadMesh + GameObject
    ///   creation. Released tiles are removed from _loaded, so PumpPendingBuilds/DrainTessellation
    ///   never consume their in-flight results. DrainTessellation() provides deterministic drain for
    ///   headless tests.
    ///
    /// Steady-state no-GC contract (preserved from S06):
    ///   The ApplyZoom loop over _layerRecords is a plain <c>for</c> over a <c>List</c> (struct
    ///   enumerator, no allocation). The early-out fires before any Request / collection mutation when
    ///   cover is clean AND nothing is pending. Reused buffers, no LINQ, no closures in hot paths.
    ///   UniTask and MeshData allocations happen only on the transient fetch-completion edge, never in
    ///   the steady-state Tick path.
    ///
    /// S14: line style layers are now rendered via the same per-layer styled path as fills.
    /// One <see cref="LineLayerRecord"/> per line layer, using the <c>MapRenderer/Line</c> shader
    /// and <see cref="StyledLineTileBuilder"/> for tessellation.
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public sealed class MapView : MonoBehaviour
    {
        // ── Profiler markers (allocation-free; static readonly = constructed once at type-init) ──
        // Namespace: MapRenderer.* — greppable per S46 acceptance.
        private static readonly ProfilerMarker PmCameraAdvance   = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Camera.Advance");
        private static readonly ProfilerMarker PmCoverSelect     = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.CoverSelect");
        private static readonly ProfilerMarker PmFetchPoll       = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.FetchPoll");
        private static readonly ProfilerMarker PmSchedulerReq    = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Scheduler.Request");
        private static readonly ProfilerMarker PmTileDecode      = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.Decode");
        private static readonly ProfilerMarker PmMeshUpload      = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Mesh.Upload");

        // ── Configuration ──────────────────────────────────────────────────────────────────────
        [Tooltip("Cover over-select: viewport aspect (w/h) and pad factor (absorbs viewport size + pitch).")]
        public float ViewportAspect = 1.5f;
        public float PadFactor      = 1.5f;

        [Tooltip("Zoom clamp for tile selection.")]
        public int MinZoom = 0;
        public int MaxZoom = 14;

        [Tooltip("Rebase the scene origin to the camera when it drifts more than this many metres.")]
        public double RebaseThresholdMeters = 2000.0;

        [Tooltip("Max tile pipeline builds per Tick (load smoothing).")]
        public int MaxBuildsPerTick = 4;

        // ── Per-style-layer record (built once at Initialise, shared across all tiles) ──────────

        /// <summary>One record per fill style layer in declared order.</summary>
        private struct FillLayerRecord
        {
            /// <summary>The parsed fill paint for this layer.</summary>
            public FillPaint Paint;

            /// <summary>The style layer (needed by FeatureSelector for source-layer + filter).</summary>
            public StyleLayer StyleLayer;

            /// <summary>Shared Material instance. renderQueue = TransparentQueue + layerIndex.</summary>
            public Material Material;

            /// <summary>Applies zoom-dependent paint uniforms to Material each frame.</summary>
            public ZoomStyleApplier Applier;
        }

        /// <summary>One record per line style layer in declared order.</summary>
        private struct LineLayerRecord
        {
            /// <summary>The parsed line paint for this layer.</summary>
            public LinePaint Paint;

            /// <summary>The style layer (needed by FeatureSelector for source-layer + filter).</summary>
            public StyleLayer StyleLayer;

            /// <summary>Shared Material instance. renderQueue = TransparentQueue + layerIndex.</summary>
            public Material Material;

            /// <summary>Applies zoom-dependent paint uniforms to Material each frame.</summary>
            public ZoomStyleApplier Applier;
        }

        // ── S47 tessellation payload (S51: Task → UniTask) ────────────────────────────────────

        /// <summary>
        /// Per-layer mesh data produced by one tile's background tessellation.
        /// One element per fill layer, one element per line layer.
        /// </summary>
        private struct TessellationResult
        {
            /// <summary>Per-fill-layer CPU mesh data (index matches _layerRecords).</summary>
            public StyledFillTileBuilder.LayerMeshData[] LayerData;
            /// <summary>S14: per-line-layer CPU mesh data (index matches _lineRecords).</summary>
            public StyledLineTileBuilder.LayerMeshData[] LineLayerData;
        }

        // ── Live state ─────────────────────────────────────────────────────────────────────────────────
        private TileScheduler _scheduler;
        private IDataSource   _source;
        private bool          _ownsSource;

        // ── S50: Core camera system + Unity sync layer (owned by MapView, D4) ─────────────────
        private CameraSystem _cameraSystem;
        private MapCamera    _mapCamera;

        private StyleDocument _style;

        // Per-fill-layer records (built once at Initialise).
        private readonly List<FillLayerRecord> _layerRecords     = new List<FillLayerRecord>(16);
        // S14: per-line-layer records (built once at Initialise).
        private readonly List<LineLayerRecord> _lineRecords      = new List<LineLayerRecord>(16);

        // Reused buffers — never reallocated in steady state.
        private readonly List<TileId>                   _cover     = new List<TileId>(64);
        private readonly HashSet<TileId>                _coverSet  = new HashSet<TileId>();
        private readonly Dictionary<TileId, LoadedTile> _loaded    = new Dictionary<TileId, LoadedTile>();
        private readonly List<TileId>                   _toRelease = new List<TileId>(32);

        private double2 _sceneOrigin;
        private bool    _sceneOriginInitialised;
        private bool    _coverDirty = true;

        // ── S50: tile-selection key (scalar fields, no boxing) ─────────────────────────────────
        private double _coverKeyLon;
        private double _coverKeyLat;
        private int    _coverKeyIntegerZoom;
        private bool   _coverKeyInitialised;

        // ── S51 test observability: mid-flight release counter ─────────────────────────────────
        // Counts tiles released while their tessellation was still in-flight (HasTessellationTask
        // && !Built). Exposed for tests to prove the race actually occurred. See S51 tooth 5b.
        private int _releasedMidFlightCount;

        // ── S48 mid-flight discard holding pen ────────────────────────────────────────────────
        // When a tile is released mid-flight (ReleaseTile while TessellationTask is still running),
        // the UniTask has already been captured but not yet produced a result. We cannot dispose the
        // NativeArrays immediately — they don't exist yet. Instead we stash the UniTask here; each
        // Tick drains completed tasks, disposing their NativeArray payloads. Teardown spins to
        // completion and disposes everything remaining.
        //
        // This list is only modified on the main thread (ReleaseTile, DrainPendingDisposal, Teardown
        // are all main-thread). No locking is required.
        private readonly List<UniTask<TessellationResult>> _pendingDisposal = new List<UniTask<TessellationResult>>(8);

        /// <summary>
        /// Per-tile live record: the in-flight fetch request, the tessellation UniTask, and the built tile
        /// container GameObject.
        ///
        /// S47/S51: the lifecycle is now:
        ///   1. Fetch (Request → UniTask[TileResponse] in-flight, stored as .Preserve())
        ///   2. Tessellation kicked (TessellationTask in-flight; FetchCompleted = true)
        ///   3. Tessellation consumed (Built = true; TessellationTask = default; Go = container)
        ///
        /// Mid-flight release protection: ReleaseTile removes the tile from _loaded immediately,
        /// so PumpPendingBuilds and DrainTessellation — which iterate _loaded — never visit released
        /// tiles. A tile released while its tessellation is in-flight will never have ConsumeTessellationTask
        /// called for it.
        ///
        /// Note: UniTask is a struct. .Preserve() on the stored UniTask allows polling .IsCompleted
        /// and reading .GetAwaiter().GetResult() only after IsCompleted is true.
        /// </summary>
        private struct LoadedTile
        {
            public UniTask<TileResponse>     Request;
            public bool                      FetchCompleted;   // fetch done; tessellation may be in-flight
            public UniTask<TessellationResult> TessellationTask; // default until fetch completes; default after consumed
            public bool                      HasTessellationTask; // true when TessellationTask is valid
            public bool                      Built;            // mesh produced (or definitively absent/failed)
            public GameObject                Go;               // tile container; child GameObjects are per-layer
            public double2                   TileOriginMerc;
            /// <summary>
            /// S51 leak guard: per-fill-layer Mesh assets created by ConsumeTessellationTask.
            /// Must be explicitly destroyed on release/teardown because Unity does NOT destroy
            /// MeshFilter.sharedMesh when the GameObject is destroyed.
            /// Null until the tile is consumed; set by ConsumeTessellationTask.
            /// </summary>
            public Mesh[]                    Meshes;
        }

        // ── Lifecycle / injection ────────────────────────────────────────────────────────────

        /// <summary>
        /// Injects the data source, style document, and initial camera (call before the first
        /// <see cref="Tick"/>). If <paramref name="ownsSource"/> is true, <see cref="OnDestroy"/>
        /// disposes the source. The scheduler is always owned by this MapView.
        /// </summary>
        public void Initialise(IDataSource source, CameraProperties initialView,
            bool ownsSource = false, StyleDocument style = null)
        {
            _source     = source;
            _ownsSource = ownsSource;
            _style      = style;
            _scheduler  = new TileScheduler(source, new TileCache(capacity: 256));

            // Ensure a camera system exists (DD1: Tick must never read a null system).
            if (_cameraSystem == null)
                _cameraSystem = new CameraSystem(initialView);

            _sceneOriginInitialised = false;
            _coverDirty             = true;
            _coverKeyInitialised    = false;

            BuildLayerRecords();
        }

        /// <summary>
        /// True once <see cref="Initialise"/> has been called successfully.
        /// </summary>
        public bool IsInitialised => _scheduler != null;

        /// <summary>
        /// Current camera state (read-only). S50: this is <see cref="CameraSystem.Current"/> directly.
        /// </summary>
        public CameraProperties View => _cameraSystem != null ? _cameraSystem.Current : CameraProperties.Default;

        /// <summary>Number of currently loaded (or loading) tiles. Exposed for tests.</summary>
        public int LoadedTileCount => _loaded.Count;

        /// <summary>The scheduler's in-flight fetch count. Exposed for tests.</summary>
        public int InFlightCount => _scheduler != null ? _scheduler.InFlightCount : 0;

        /// <summary>The scene root's current Mercator origin. Exposed for tests.</summary>
        public double2 SceneOrigin => _sceneOrigin;

        /// <summary>Number of fill style layers in the loaded style. Exposed for tests.</summary>
        public int FillLayerCount => _layerRecords.Count;

        /// <summary>Number of line style layers in the loaded style. Exposed for tests (S14).</summary>
        public int LineLayerCount => _lineRecords.Count;

        /// <summary>
        /// Number of tiles released while their tessellation was still in-flight (HasTessellationTask
        /// and not yet Built at the moment of release). Incremented by ReleaseTile. Exposed for tests
        /// to prove the mid-flight race actually occurred in <see cref="S51DisposalLeakGuardTests"/>.
        /// </summary>
        public int ReleasedMidFlightCount => _releasedMidFlightCount;

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
            _coverKeyInitialised = false;
        }

        /// <summary>
        /// Test-only: returns true and the built tile's container GameObject when the tile is loaded
        /// AND its mesh has been produced.
        /// </summary>
        public bool TryGetBuiltTile(TileId id, out GameObject go)
        {
            go = null;
            if (_loaded.TryGetValue(id, out var lt) && lt.Built && lt.Go != null)
            {
                go = lt.Go;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Test-only: true once every loaded tile has finished building (or is definitively absent).
        /// S47/S51: returns false while any tile has a pending tessellation.
        /// </summary>
        public bool AllTilesSettled()
        {
            foreach (var kv in _loaded)
                if (!kv.Value.Built) return false;
            return true;
        }

        /// <summary>
        /// S47 deterministic drain — blocks the calling thread until all in-flight fetch and
        /// tessellation UniTasks complete, then consumes their results synchronously (uploads meshes +
        /// creates GameObjects). After this returns, <see cref="AllTilesSettled()"/> is guaranteed
        /// true for all currently loaded tiles.
        ///
        /// This is a full drain: it handles tiles at any stage of the pipeline:
        ///   (a) Fetch in-flight: spins until the fetch UniTask completes, then kicks tessellation inline.
        ///   (b) Tessellation in-flight: spins until the UniTask completes, then consumes inline.
        ///   (c) Neither (tile not yet fetched): marks Built=true (nothing to do).
        ///
        /// Safe: both fetch and tessellation UniTasks use configureAwait: false (UniTask.RunOnThreadPool),
        /// so they complete on the ThreadPool and IsCompleted becomes true without needing the Unity
        /// PlayerLoop to advance. Spinning on IsCompleted from the main thread therefore does not
        /// deadlock (no PlayerLoop dependency to dead-end on).
        ///
        /// Called by test helpers for deterministic settle. NOT called from the production Update path.
        /// </summary>
        public void DrainTessellation()
        {
            if (_cameraSystem == null) return;
            CameraProperties cam = _cameraSystem.Current;

            // Collect all unsettled tiles.
            var unsettled = new List<TileId>(8);
            foreach (var kv in _loaded)
                if (!kv.Value.Built)
                    unsettled.Add(kv.Key);

            foreach (var id in unsettled)
            {
                LoadedTile lt = _loaded[id];

                // (a) If fetch is still in-flight, spin until it completes and kick tessellation.
                if (!lt.FetchCompleted)
                {
                    var req = lt.Request;
                    // Spin: fetch UniTask completes on the ThreadPool (configureAwait: false /
                    // SwitchToThreadPool pattern), so IsCompleted becomes true without the PlayerLoop.
                    // Thread.Sleep(1) yields real CPU time so the ThreadPool can run the continuation
                    // from FetchAndCacheAsync (which also uses SwitchToThreadPool internally).
                    // Thread.Sleep(0) is insufficient: it yields only to threads of equal priority
                    // and may not let the ThreadPool continuation run before the spin limit.
                    int spins = 0;
                    while (!req.Status.IsCompleted() && spins++ < 10000)
                        Thread.Sleep(1);

                    lt.FetchCompleted = true;

                    if (req.Status == UniTaskStatus.Succeeded &&
                        req.GetAwaiter().GetResult().HasData &&
                        req.GetAwaiter().GetResult().Bytes != null)
                    {
                        // Kick tessellation synchronously (wait inline).
                        var tessTask = KickTessellationTask(lt, id, req.GetAwaiter().GetResult().Bytes, cam);
                        lt.HasTessellationTask = true;
                        lt.TessellationTask    = tessTask;
                    }
                    else
                    {
                        // Absent/failed fetch — nothing to tessellate.
                        lt.Built = true;
                        _loaded[id] = lt;
                        continue;
                    }
                }

                // (b) Tessellation in-flight — spin and consume.
                if (lt.HasTessellationTask)
                {
                    var tessTask = lt.TessellationTask;
                    // Safe spin: tessellation UniTask uses configureAwait: false (RunOnThreadPool),
                    // so IsCompleted is true on the ThreadPool without needing the PlayerLoop.
                    // Thread.Sleep(1) yields real CPU time so the ThreadPool can complete the work.
                    int spins = 0;
                    while (!tessTask.Status.IsCompleted() && spins++ < 10000)
                        Thread.Sleep(1);
                    ConsumeTessellationTask(id, ref lt);
                }
                else
                {
                    lt.Built = true;
                }

                _loaded[id] = lt;
            }
        }

        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// S45 D5 / S50 D4 — Deterministic frame update. Called from <see cref="Update"/>
        /// (MonoBehaviour) with <c>Time.deltaTime</c>. Also callable from tests with explicit dt.
        /// </summary>
        public void UpdateFrame(double dt)
        {
            // STEP 1: Advance camera (D5 — must run before tile selection reads the camera).
            if (_cameraSystem != null)
            {
                using var sCamAdv = PmCameraAdvance.Auto();
                _cameraSystem.Advance(dt);

                // Refresh the scene origin BEFORE positioning the camera so camera and tiles share one
                // origin this frame (no rebase-frame glitch). Then sync the camera CAMERA-RELATIVE: pass
                // the look-at's render-space ground offset (lookAtMercator − sceneOrigin) so the camera
                // tracks the panned look-at over the stable tile field. This is the actual pan fix — the
                // camera was previously pinned to the render origin, so a sub-rebase pan moved nothing.
                CameraProperties curCam = _cameraSystem.Current;
                UpdateSceneOrigin(curCam);
                double2 lookAtMerc = curCam.CenterMercator();
                double2 lookAtRenderOffset = new double2(
                    lookAtMerc.x - _sceneOrigin.x, lookAtMerc.y - _sceneOrigin.y);
                _mapCamera?.Sync(_cameraSystem, lookAtRenderOffset);
            }

            // STEP 2: Tile loop (reads the now-advanced _cameraSystem.Current).
            Tick();
        }

        /// <summary>
        /// One frame of the live loop. Allocation-free in steady state.
        ///
        /// ApplyZoom runs FIRST (before the early-out) so zoom-dependent uniforms are always
        /// up-to-date, even on frames where the cover is unchanged.
        ///
        /// S47/S51: no main-thread tessellation. PumpPendingBuilds only KICKS background UniTasks
        /// on fetch completion; ConsumeTessellationResults polls and CONSUMES completed UniTasks
        /// (uploads mesh + creates GameObjects). Neither step blocks on tessellation.
        /// </summary>
        public void Tick()
        {
            if (_scheduler == null) return;

            CameraProperties cam = _cameraSystem.Current;

            int integerZoom = cam.IntegerZoom;
            if (!_coverKeyInitialised ||
                cam.LookAt.Lon != _coverKeyLon ||
                cam.LookAt.Lat != _coverKeyLat ||
                integerZoom    != _coverKeyIntegerZoom)
            {
                _coverDirty = true;
            }

            // ApplyZoom first — before any early-out — so fractional-zoom changes always push uniforms.
            for (int i = 0; i < _layerRecords.Count; i++)
                _layerRecords[i].Applier.ApplyZoom(cam.Zoom);
            // S14: apply zoom uniforms to line layers too.
            // S43: also re-evaluate line-dasharray (zoom-step arrays re-evaluate per-frame).
            // Pixel-mode line width needs the live ground resolution: the shader computes
            //   widthM = _Width(px) * _MetersPerPixel
            // and the world is in Web-Mercator metres, so _MetersPerPixel must track the current zoom.
            // (Previously _MetersPerPixel was left at its initial 1.0 → every pixel-spec width rendered
            //  as that-many metres ≈ 9.5x too thin at z14, ~1000s× too thin when zoomed out.)
            float metersPerPixel = (float)CameraPoseMath.MetersPerPixel(cam.Zoom);
            for (int i = 0; i < _lineRecords.Count; i++)
            {
                _lineRecords[i].Applier.ApplyZoom(cam.Zoom);
                _lineRecords[i].Material.SetFloat("_MetersPerPixel", metersPerPixel);
                ApplyLineDashArray(_lineRecords[i].Paint, _lineRecords[i].Material, cam.Zoom);
            }

            UpdateSceneOrigin(cam);

            // S48: drain any completed mid-flight-discard tasks so their NativeArrays are freed.
            DrainPendingDisposal();

            int pending = PumpPendingBuilds(cam);

            if (!_coverDirty && pending == 0)
                return;

            using var sCoverSel = PmCoverSelect.Auto();

            TileCover.Cover(cam, ViewportAspect, PadFactor, MinZoom, MaxZoom, _cover);

            _coverSet.Clear();
            for (int i = 0; i < _cover.Count; i++)
                _coverSet.Add(_cover[i]);

            // Request tiles newly entering the cover.
            for (int i = 0; i < _cover.Count; i++)
            {
                TileId id = _cover[i];
                if (!_loaded.ContainsKey(id))
                {
                    UniTask<TileResponse> fetchReq;
                    {
                        using var sSchedReq = PmSchedulerReq.Auto();
                        // .Preserve() allows polling .IsCompleted across frames without exhausting the UniTask.
                        fetchReq = _scheduler.Request(id).Preserve();
                    }
                    _loaded[id] = new LoadedTile
                    {
                        Request        = fetchReq,
                        Built          = false,
                        TileOriginMerc = FloatingOrigin.TileLocalOriginMercator(id),
                    };
                }
            }

            // Release tiles leaving the cover.
            _toRelease.Clear();
            foreach (var kv in _loaded)
                if (!_coverSet.Contains(kv.Key))
                    _toRelease.Add(kv.Key);
            for (int i = 0; i < _toRelease.Count; i++)
                ReleaseTile(_toRelease[i]);

            _coverKeyLon         = cam.LookAt.Lon;
            _coverKeyLat         = cam.LookAt.Lat;
            _coverKeyIntegerZoom = integerZoom;
            _coverKeyInitialised = true;

            _coverDirty = false;
        }

        /// <summary>
        /// S47/S51 pump: two-phase pipeline per tile.
        ///
        /// Phase 1 (fetch→tessellate): for tiles whose fetch UniTask just completed, kick a background
        /// tessellation UniTask via UniTask.Run (decode + polygon assemble + earcut + project — all
        /// managed, off-main-thread safe). The main thread does NOT call BuildMesh here.
        ///
        /// Phase 2 (tessellate→consume): for tiles whose tessellation UniTask is completed, consume the
        /// result on the main thread (UploadMesh → MeshBuilder.Build → GameObject creation). Capped at
        /// MaxBuildsPerTick consumes per frame.
        ///
        /// Returns the count of tiles still pending (fetch or tessellation in-flight).
        ///
        /// Greppability note: there is NO .Schedule().Complete() in this method.
        /// </summary>
        private int PumpPendingBuilds(CameraProperties cam)
        {
            using var sFetchPoll = PmFetchPoll.Auto();

            _toRelease.Clear();
            foreach (var kv in _loaded)
                if (!kv.Value.Built)
                    _toRelease.Add(kv.Key);

            int builds  = 0;
            int pending = 0;

            for (int i = 0; i < _toRelease.Count; i++)
            {
                TileId id = _toRelease[i];
                LoadedTile lt = _loaded[id];

                // ── Phase 2: consume a completed tessellation UniTask ──────────────────────────
                if (lt.FetchCompleted && lt.HasTessellationTask && lt.TessellationTask.Status.IsCompleted())
                {
                    if (builds >= MaxBuildsPerTick)
                    {
                        pending++;
                        continue;
                    }

                    ConsumeTessellationTask(id, ref lt);
                    builds++;
                    _loaded[id] = lt;
                    continue;
                }

                // ── Still waiting for tessellation (in-flight) ────────────────────────────────
                if (lt.FetchCompleted && lt.HasTessellationTask)
                {
                    pending++;
                    continue;
                }

                // ── Phase 1: fetch completed → kick tessellation ──────────────────────────────
                if (!lt.Request.Status.IsCompleted())
                {
                    // Fetch still in-flight.
                    pending++;
                    continue;
                }

                // Mark fetch done; kick tessellation regardless of HasData so the tile settles.
                lt.FetchCompleted = true;

                if (lt.Request.Status == UniTaskStatus.Succeeded)
                {
                    // GetResult() is safe because IsCompleted is true (checked above via Status).
                    TileResponse resp = lt.Request.GetAwaiter().GetResult();
                    if (resp.HasData && resp.Bytes != null)
                    {
                        lt.TessellationTask    = KickTessellationTask(lt, id, resp.Bytes, cam);
                        lt.HasTessellationTask = true;
                        pending++; // tessellation now in-flight
                    }
                    else
                    {
                        // Absent tile — mark built (nothing to render).
                        lt.Built = true;
                    }
                }
                else
                {
                    // Fetch faulted or cancelled — mark built (nothing to render).
                    lt.Built = true;
                }

                _loaded[id] = lt;
            }

            return pending;
        }

        /// <summary>
        /// Starts a background <see cref="UniTask{TessellationResult}"/> that runs decode / assemble /
        /// earcut / project for all fill layers of one tile. Returns immediately (non-blocking).
        ///
        /// S51: uses UniTask.RunOnThreadPool(configureAwait: false) instead of Task.Run.
        /// configureAwait: false is REQUIRED: the default (true) posts the final continuation via
        /// UniTask.Yield() to the Unity PlayerLoop. DrainTessellation() and the synchronous-spin
        /// path in OnDestroy poll IsCompleted on the main thread WITHOUT pumping the PlayerLoop, so
        /// the task would never reach Succeeded with configureAwait: true. With configureAwait: false,
        /// completion stays on the ThreadPool and IsCompleted is true as soon as the work body returns.
        ///
        /// The UniTask is stored with .Preserve() in the caller so its .IsCompleted can be polled
        /// across multiple frames without exhausting the UniTask.
        ///
        /// The task captures only value-type / immutable inputs (bytes, layer records are read-only
        /// after Initialise). No Unity.Object is captured or touched off-main.
        /// </summary>
        private UniTask<TessellationResult> KickTessellationTask(
            LoadedTile lt, TileId id, byte[] mvtBytes, CameraProperties cam)
        {
            var layerRecordsSnapshot     = _layerRecords.ToArray();
            var lineRecordsSnapshot      = _lineRecords.ToArray();
            double zoom          = cam.Zoom;
            double2 tileOrigin   = lt.TileOriginMerc;

            return UniTask.RunOnThreadPool(() =>
            {
                MvtTile mvtTile = MvtDecoder.Decode(mvtBytes);

                // ── Fill layers ────────────────────────────────────────────────
                var layerData = new StyledFillTileBuilder.LayerMeshData[layerRecordsSnapshot.Length];

                for (int li = 0; li < layerRecordsSnapshot.Length; li++)
                {
                    FillLayerRecord rec = layerRecordsSnapshot[li];

                    var features = MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(
                        rec.StyleLayer, mvtTile, zoom);
                    if (features.Count == 0)
                    {
                        layerData[li] = new StyledFillTileBuilder.LayerMeshData
                        {
                            Features = new System.Collections.Generic.List<StyledFillTileBuilder.FeatureMeshData>()
                        };
                        continue;
                    }

                    MvtLayer mvtLayer = MapRenderer.Core.Style.SourceLayerResolver.ResolveMvtLayer(
                        rec.StyleLayer, mvtTile);
                    if (mvtLayer == null)
                    {
                        layerData[li] = new StyledFillTileBuilder.LayerMeshData
                        {
                            Features = new System.Collections.Generic.List<StyledFillTileBuilder.FeatureMeshData>()
                        };
                        continue;
                    }

                    layerData[li] = StyledFillTileBuilder.BuildMeshData(
                        features, rec.Paint, zoom, mvtLayer.Extent, id, tileOrigin);
                }

                // ── S14: Line layers ───────────────────────────────────────────
                // Feature selection routes through SourceLayerResolver seam (tooth #6 — same as fills).
                var lineLayerData = new StyledLineTileBuilder.LayerMeshData[lineRecordsSnapshot.Length];

                for (int li = 0; li < lineRecordsSnapshot.Length; li++)
                {
                    LineLayerRecord rec = lineRecordsSnapshot[li];

                    var features = MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(
                        rec.StyleLayer, mvtTile, zoom);
                    if (features.Count == 0)
                        continue; // IsCreated=false, no dispose needed

                    MvtLayer mvtLayer = MapRenderer.Core.Style.SourceLayerResolver.ResolveMvtLayer(
                        rec.StyleLayer, mvtTile);
                    if (mvtLayer == null)
                        continue;

                    lineLayerData[li] = StyledLineTileBuilder.BuildMeshData(
                        features, rec.Paint, zoom, mvtLayer.Extent, id, tileOrigin);
                }

                return new TessellationResult { LayerData = layerData, LineLayerData = lineLayerData };
            }, configureAwait: false).Preserve(); // .Preserve() allows polling .IsCompleted across multiple frames
        }

        /// <summary>
        /// Consumes a completed tessellation: uploads meshes + creates GameObjects for the tile.
        /// Must be called on the Unity main thread. Called only when TessellationTask.IsCompleted.
        ///
        /// S51: reads UniTaskStatus.Succeeded (was TaskStatus.RanToCompletion).
        /// .GetAwaiter().GetResult() is safe here because IsCompleted is true before this is called.
        ///
        /// Mid-flight release: ReleaseTile removes the tile from _loaded, so PumpPendingBuilds and
        /// DrainTessellation never call this method for a released tile — no explicit generation check
        /// is needed. The real discard protection is the _loaded-removal in ReleaseTile.
        /// </summary>
        private void ConsumeTessellationTask(TileId id, ref LoadedTile lt)
        {
            var task = lt.TessellationTask;
            lt.HasTessellationTask = false;
            lt.TessellationTask    = default;
            lt.Built               = true;

            // Faulted or cancelled — mark built (nothing to render) and return.
            if (task.Status != UniTaskStatus.Succeeded)
                return;

            // .GetResult() is safe: IsCompleted was true before ConsumeTessellationTask was called.
            TessellationResult result = task.GetAwaiter().GetResult();

            // S48 DECISIVE: dispose ALL LayerMeshData NativeArrays on EVERY exit path.
            // UploadMesh copies data into the Mesh (SetVertexBufferData); the source NativeArrays
            // are no longer needed after upload. The finally block disposes regardless of exceptions
            // or early returns — guaranteeing no NativeArray leak for consumed results.
            try
            {
                if (result.LayerData == null && result.LineLayerData == null) return;

                bool hasFillLayers = _layerRecords.Count > 0;
                bool hasLineLayers = _lineRecords.Count > 0;
                if (!hasFillLayers && !hasLineLayers) return;

                // Create the tile container. Child GameObjects are per fill/line layer.
                var container = new GameObject($"Tile_{id}");
                container.transform.SetParent(transform, worldPositionStays: false);
                container.transform.localPosition =
                    (Vector3)(float3ToVector(FloatingOrigin.TileLocalToScene(lt.TileOriginMerc, _sceneOrigin)));

                bool anyGeometry = false;
                // S51 leak guard: track created Mesh assets so they can be explicitly destroyed on release.
                // Unity does NOT destroy MeshFilter.sharedMesh when the owning GameObject is destroyed.
                var createdMeshes = new System.Collections.Generic.List<Mesh>(8);

                // ── Fill layers ────────────────────────────────────────────────
                if (result.LayerData != null)
                {
                    for (int li = 0; li < _layerRecords.Count && li < result.LayerData.Length; li++)
                    {
                        FillLayerRecord rec = _layerRecords[li];

                        using var sMeshUpload = PmMeshUpload.Auto();

                        // S48: UploadMesh uses the advanced NativeArray API (no managed Set* calls).
                        // It does NOT dispose data — we dispose in the finally block after the loop.
                        Mesh mesh = StyledFillTileBuilder.UploadMesh(result.LayerData[li]);
                        if (mesh == null) continue;

                        createdMeshes.Add(mesh); // S51: track for explicit destruction on release/teardown

                        var layerGo = new GameObject($"Layer_{li}_{rec.StyleLayer.Id}");
                        layerGo.transform.SetParent(container.transform, worldPositionStays: false);
                        layerGo.transform.localPosition = Vector3.zero;

                        var mf = layerGo.AddComponent<MeshFilter>();
                        mf.sharedMesh = mesh;

                        var mr = layerGo.AddComponent<MeshRenderer>();
                        mr.sharedMaterial = rec.Material;
                        mr.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
                        mr.receiveShadows     = false;

                        anyGeometry = true;
                    }
                }

                // ── S14: Line layers ───────────────────────────────────────────
                if (result.LineLayerData != null)
                {
                    for (int li = 0; li < _lineRecords.Count && li < result.LineLayerData.Length; li++)
                    {
                        LineLayerRecord rec = _lineRecords[li];

                        using var sMeshUpload = PmMeshUpload.Auto();

                        Mesh mesh = StyledLineTileBuilder.UploadMesh(result.LineLayerData[li]);
                        if (mesh == null) continue;

                        createdMeshes.Add(mesh);

                        var layerGo = new GameObject($"LineLayer_{li}_{rec.StyleLayer.Id}");
                        layerGo.transform.SetParent(container.transform, worldPositionStays: false);
                        layerGo.transform.localPosition = Vector3.zero;

                        var mf = layerGo.AddComponent<MeshFilter>();
                        mf.sharedMesh = mesh;

                        var mr = layerGo.AddComponent<MeshRenderer>();
                        mr.sharedMaterial = rec.Material;
                        mr.shadowCastingMode  = UnityEngine.Rendering.ShadowCastingMode.Off;
                        mr.receiveShadows     = false;

                        anyGeometry = true;
                    }
                }

                if (!anyGeometry)
                {
                    if (Application.isPlaying) Destroy(container);
                    else                       DestroyImmediate(container);
                    return;
                }

                lt.Go     = container;
                lt.Meshes = createdMeshes.Count > 0 ? createdMeshes.ToArray() : null;
            }
            finally
            {
                // S48 DECISIVE: dispose all LayerMeshData NativeArrays after upload (or on any exit).
                // This covers: normal consume, early return (LayerData null, no layers, no geometry).
                // Dispose() is idempotent (IsCreated guard) so double-dispose is safe.
                if (result.LayerData != null)
                {
                    for (int li = 0; li < result.LayerData.Length; li++)
                        result.LayerData[li].Dispose();
                }
                // S14: dispose line layer NativeArrays.
                if (result.LineLayerData != null)
                {
                    for (int li = 0; li < result.LineLayerData.Length; li++)
                        result.LineLayerData[li].Dispose();
                }
            }
        }

        /// <summary>
        /// Releases a tile: scheduler release + destroy its container GameObject.
        /// Does NOT wait for in-flight work (non-blocking). Mid-flight tessellation is removed
        /// from _loaded immediately so PumpPendingBuilds/DrainTessellation never visit it again.
        ///
        /// S48 holding pen: when a tessellation UniTask is still in-flight at release time, its
        /// NativeArray payload has not yet been produced (it will be allocated on the ThreadPool
        /// after this method returns). We stash the UniTask in <see cref="_pendingDisposal"/>;
        /// <see cref="DrainPendingDisposal"/> polls it each Tick and disposes the payload when the
        /// task completes. This guarantees no NativeArray leak for mid-flight-released tiles.
        /// </summary>
        private void ReleaseTile(TileId id)
        {
            if (_loaded.TryGetValue(id, out var lt))
            {
                // Track tiles released mid-flight (tessellation in-flight but not yet consumed).
                // This counter is read by S51 tooth 5b to prove the race genuinely occurred.
                if (lt.HasTessellationTask && !lt.Built)
                {
                    _releasedMidFlightCount++;

                    // S48 DECISIVE: stash the in-flight UniTask in the holding pen.
                    // The tessellation may still be running on the ThreadPool — its NativeArrays
                    // don't exist yet. DrainPendingDisposal() polls this task on subsequent Ticks
                    // and disposes the payload when it completes.
                    _pendingDisposal.Add(lt.TessellationTask);
                }

                // S51 leak guard: destroy tracked Mesh assets explicitly.
                // Unity does NOT destroy MeshFilter.sharedMesh when a GameObject is destroyed.
                // lt.Meshes holds direct references to created Mesh assets for reliable destruction.
                DestroyTrackedMeshes(ref lt);

                if (lt.Go != null)
                {
                    if (Application.isPlaying) Destroy(lt.Go);
                    else                       DestroyImmediate(lt.Go);
                }
                _loaded.Remove(id);
            }
            _scheduler.Release(id);
        }

        /// <summary>
        /// S48: Drains completed tasks from the mid-flight-discard holding pen, disposing their
        /// NativeArray payloads. Called once per Tick and in Teardown.
        ///
        /// Tasks not yet complete remain in the list for the next drain. This is a non-blocking
        /// poll — no spinning, no blocking.
        /// </summary>
        private void DrainPendingDisposal()
        {
            if (_pendingDisposal.Count == 0) return;

            // Iterate backwards so we can remove in-place without index shifting.
            for (int i = _pendingDisposal.Count - 1; i >= 0; i--)
            {
                var task = _pendingDisposal[i];
                if (!task.Status.IsCompleted())
                    continue; // still in-flight; check again next Tick

                // Task completed (succeeded, faulted, or cancelled).
                if (task.Status == UniTaskStatus.Succeeded)
                {
                    TessellationResult result = task.GetAwaiter().GetResult();
                    if (result.LayerData != null)
                    {
                        for (int li = 0; li < result.LayerData.Length; li++)
                            result.LayerData[li].Dispose();
                    }
                    // S14: dispose line layer NativeArrays too.
                    if (result.LineLayerData != null)
                    {
                        for (int li = 0; li < result.LineLayerData.Length; li++)
                            result.LineLayerData[li].Dispose();
                    }
                }
                // Faulted/cancelled: no LayerData produced, nothing to dispose.

                _pendingDisposal.RemoveAt(i);
            }
        }

        /// <summary>
        /// Explicitly destroys all <see cref="Mesh"/> assets tracked in <paramref name="lt"/>.Meshes.
        ///
        /// S51 leak guard: Unity does NOT automatically destroy <c>sharedMesh</c> when a MeshFilter
        /// or its parent GameObject is destroyed. <see cref="ConsumeTessellationTask"/> populates
        /// <c>lt.Meshes</c> with direct references to every Mesh it creates; this method iterates
        /// that array for reliable, deterministic destruction. After destruction, <c>lt.Meshes</c>
        /// is nulled to prevent double-free.
        /// Must be called on the Unity main thread (Object.Destroy constraint).
        /// </summary>
        private void DestroyTrackedMeshes(ref LoadedTile lt)
        {
            if (lt.Meshes == null) return;
            for (int i = 0; i < lt.Meshes.Length; i++)
            {
                if (lt.Meshes[i] != null)
                {
                    if (Application.isPlaying) Destroy(lt.Meshes[i]);
                    else                       DestroyImmediate(lt.Meshes[i], allowDestroyingAssets: true);
                }
            }
            lt.Meshes = null;
        }

        /// <summary>
        /// Explicitly destroys all <see cref="Mesh"/> assets referenced by
        /// <see cref="MeshFilter.sharedMesh"/> on any child of <paramref name="container"/>.
        ///
        /// Retained as a fallback; prefer <see cref="DestroyTrackedMeshes"/> when <c>lt.Meshes</c>
        /// is available. Unity does NOT automatically destroy <c>sharedMesh</c> when a MeshFilter
        /// or its parent GameObject is destroyed — shared meshes are treated as assets.
        /// Must be called on the Unity main thread (Object.Destroy constraint).
        /// </summary>
        private void DestroyTileMeshes(GameObject container)
        {
            if (container == null) return;
            var mfs = container.GetComponentsInChildren<MeshFilter>(includeInactive: true);
            for (int i = 0; i < mfs.Length; i++)
            {
                var mesh = mfs[i].sharedMesh;
                if (mesh != null)
                {
                    if (Application.isPlaying) Destroy(mesh);
                    else                       DestroyImmediate(mesh, allowDestroyingAssets: true);
                }
            }
        }

        /// <summary>
        /// Initialises or rebases the scene origin to the camera's Mercator position.
        /// On rebase, shifts every loaded tile's local position by the rebase delta.
        /// </summary>
        private void UpdateSceneOrigin(CameraProperties cam)
        {
            double2 cameraMerc = cam.CenterMercator();

            if (!_sceneOriginInitialised)
            {
                _sceneOrigin = cameraMerc;
                _sceneOriginInitialised = true;
                return;
            }

            if (!FloatingOrigin.ShouldRebase(_sceneOrigin, cameraMerc, RebaseThresholdMeters))
                return;

            double2 oldOrigin = _sceneOrigin;
            _sceneOrigin = cameraMerc;
            double2 delta = FloatingOrigin.RebaseDelta(oldOrigin, _sceneOrigin);

            foreach (var kv in _loaded)
            {
                var lt = kv.Value;
                if (lt.Go != null)
                    lt.Go.transform.localPosition = float3ToVector(
                        FloatingOrigin.TileLocalToScene(lt.TileOriginMerc, _sceneOrigin));
            }
            _ = delta;
        }

        private static Vector3 float3ToVector(float3 v) => new Vector3(v.x, v.y, v.z);

        /// <summary>
        /// Builds per-fill-layer and per-line-layer records from the style document. Called once at Initialise.
        /// </summary>
        private void BuildLayerRecords()
        {
            DisposeLayerRecords();

            if (_style == null) return;

            var fillLayers = new List<StyleLayer>(8);
            var lineLayers = new List<StyleLayer>(8);
            foreach (var layer in _style.Layers)
            {
                if (layer.LayerType == StyleLayerType.Fill) fillLayers.Add(layer);
                else if (layer.LayerType == StyleLayerType.Line) lineLayers.Add(layer);
            }

            // ── Fill layers ────────────────────────────────────────────────────
            if (fillLayers.Count > 0)
            {
                int[] queues = LayerDrawOrder.ComputeQueues(fillLayers.Count);

                for (int i = 0; i < fillLayers.Count; i++)
                {
                    StyleLayer sl = fillLayers[i];
                    FillPaint paint = new FillPaint(sl);

                    Material mat = CreateFillMaterial();
                    mat.renderQueue = queues[i];

                    var applier = new ZoomStyleApplier(mat);
                    BindFillPaintToApplier(paint, applier, mat);
                    applier.ApplyZoom(_cameraSystem != null ? _cameraSystem.Current.Zoom : 0.0);

                    _layerRecords.Add(new FillLayerRecord
                    {
                        Paint      = paint,
                        StyleLayer = sl,
                        Material   = mat,
                        Applier    = applier,
                    });
                }
            }

            // ── S14: Line layers ───────────────────────────────────────────────
            if (lineLayers.Count > 0)
            {
                // Line layers use the Transparent render queue + layer offset, placed IMMEDIATELY
                // ABOVE the fill band so lines composite on top of fills by default (painter's intent).
                // Fills occupy [TransparentQueue .. TransparentQueue + fillCount - 1] (ComputeQueues uses
                // base TransparentQueue = 3000), so the line band must start at TransparentQueue + fillCount.
                // (Previously this was 2501 + fillCount, which placed lines BELOW the 3000-based fill band —
                //  fills then overpainted every road. Surfaced by the OpenFreeMap experiment: roads vanished
                //  under landuse/landcover/water on a city tile.)
                int lineQueueBase = LayerDrawOrder.TransparentQueue + fillLayers.Count;

                for (int i = 0; i < lineLayers.Count; i++)
                {
                    StyleLayer sl = lineLayers[i];
                    LinePaint paint = new LinePaint(sl);

                    Material mat = CreateLineMaterial();
                    mat.renderQueue = lineQueueBase + i;

                    var applier = new ZoomStyleApplier(mat);
                    BindLinePaintToApplier(paint, applier, mat);
                    applier.ApplyZoom(_cameraSystem != null ? _cameraSystem.Current.Zoom : 0.0);

                    _lineRecords.Add(new LineLayerRecord
                    {
                        Paint      = paint,
                        StyleLayer = sl,
                        Material   = mat,
                        Applier    = applier,
                    });
                }
            }
        }

        /// <summary>
        /// Creates a base fill Material for a style layer.
        /// </summary>
        private static Material CreateFillMaterial()
        {
            var shader = Shader.Find("MapRenderer/Fill");
            if (shader != null)
            {
                var mat = new Material(shader) { name = "MapView_Fill" };
                mat.SetColor("_MapColor",   Color.white);
                mat.SetColor("_BaseColor",  Color.white);
                mat.SetFloat("_Opacity",    1f);
                mat.SetFloat("_Metallic",   0f);
                mat.SetFloat("_Smoothness", 0f);
                mat.SetFloat("_ZWrite",     0f);
                return mat;
            }
            Debug.LogWarning("[MapView] MapRenderer/Fill shader not found — using Sprites/Default fallback.");
            return new Material(Shader.Find("Sprites/Default")) { name = "MapView_Fallback" };
        }

        /// <summary>
        /// Binds constant/zoom paint properties from <paramref name="paint"/> to the material.
        /// </summary>
        private static void BindFillPaintToApplier(FillPaint paint, ZoomStyleApplier applier, Material mat)
        {
            if (paint.Opacity != null)
                applier.BindFloat(paint.Opacity, "_Opacity");

            if (paint.OutlineColor != null && !paint.OutlineColorIsFallback)
                applier.BindColor(paint.OutlineColor, "_FillOutlineColor");

            if (paint.Antialias != null)
                applier.BindFloat(paint.Antialias, "_FillAntialias");

            float tx = (float)paint.TranslateX.EvaluateNumber(0.0);
            float ty = (float)paint.TranslateY.EvaluateNumber(0.0);
            mat.SetVector("_FillTranslate", new Vector4(tx, ty, 0f, 0f));

            if (paint.TranslateAnchor != null)
                applier.BindFloat(paint.TranslateAnchor, "_FillTranslateAnchor");
        }

        /// <summary>
        /// Creates a base line Material for a style layer using the MapRenderer/Line shader.
        /// </summary>
        private static Material CreateLineMaterial()
        {
            var shader = Shader.Find("MapRenderer/Line");
            if (shader != null)
            {
                var mat = new Material(shader) { name = "MapView_Line" };
                mat.SetColor("_MapColor",         Color.white);
                mat.SetColor("_BaseColor",         Color.white);
                mat.SetFloat("_Opacity",           1f);
                mat.SetFloat("_Width",             2f);
                mat.SetFloat("_WidthIsPixels",     1f); // line-width is in pixels per spec
                mat.SetFloat("_MetersPerPixel",    1f);
                mat.SetFloat("_Blur",              1f);
                mat.SetFloat("_GapWidth",          0f);
                mat.SetVector("_LineTranslate",    Vector4.zero);
                mat.SetFloat("_LineTranslateAnchor", 0f);
                mat.SetFloat("_LinePattern",       0f);
                // S43: line-dasharray defaults — solid identity (_DashCount=0).
                mat.SetVector("_DashArray",        Vector4.zero);
                mat.SetFloat("_DashCount",         0f);
                // S44: line-offset default — no perpendicular shift.
                mat.SetFloat("_LineOffset",        0f);
                mat.SetFloat("_Metallic",          0f);
                mat.SetFloat("_Smoothness",        0f);
                mat.SetFloat("_ZWrite",            0f);
                return mat;
            }
            Debug.LogWarning("[MapView] MapRenderer/Line shader not found — using Sprites/Default fallback.");
            return new Material(Shader.Find("Sprites/Default")) { name = "MapView_LineFallback" };
        }

        /// <summary>
        /// Binds constant/zoom line paint properties from <paramref name="paint"/> to the material.
        /// Data-driven properties (Feature/Composite color) are handled by StyledLineTileBuilder bake;
        /// only Constant/Zoom-kind properties are bound here as uniforms.
        /// </summary>
        private static void BindLinePaintToApplier(LinePaint paint, ZoomStyleApplier applier, Material mat)
        {
            // line-color: bind only for non-data-driven (Constant/Zoom). Data-driven → vertex bake.
            if (paint.Color != null)
                applier.BindColor(paint.Color, "_MapColor");

            // line-opacity.
            if (paint.Opacity != null)
                applier.BindFloat(paint.Opacity, "_Opacity");

            // line-width (in pixels per MapLibre spec).
            // Convention (data-driven width): when WidthKind depends on feature, the evaluated width
            // is baked into WidthScale (stream 3) by StyledLineTileBuilder. Set _Width = 1.0 so
            // the shader formula (_Width × WidthScale) yields the full baked width directly.
            // For Constant/Zoom kind, Width is non-null → bind normally as a uniform.
            if (MapRenderer.Core.Expressions.ExpressionKinds.DependsOnFeature(paint.WidthKind))
                mat.SetFloat("_Width", 1f); // base = 1; evaluated width baked into WidthScale per feature
            else if (paint.Width != null)
                applier.BindFloat(paint.Width, "_Width");
            // Ensure WidthIsPixels=1 so the shader interprets width as pixels.
            mat.SetFloat("_WidthIsPixels", 1f);

            // line-blur.
            if (paint.Blur != null)
                applier.BindFloat(paint.Blur, "_Blur");

            // line-gap-width.
            if (paint.GapWidth != null)
                applier.BindFloat(paint.GapWidth, "_GapWidth");

            // line-offset (S44).
            if (paint.Offset != null)
                applier.BindFloat(paint.Offset, "_LineOffset");

            // line-translate: constant components baked into material vector.
            float tx = (float)paint.TranslateX.EvaluateNumber(0.0);
            float ty = (float)paint.TranslateY.EvaluateNumber(0.0);
            mat.SetVector("_LineTranslate", new Vector4(tx, ty, 0f, 0f));

            // line-translate-anchor.
            if (paint.TranslateAnchor != null)
                applier.BindFloat(paint.TranslateAnchor, "_LineTranslateAnchor");

            // line-pattern hook: set flag; solid fallback until S17.
            // S14_LINE_PATTERN_HOOK: _LinePattern=1 signals a pattern layer; renders solid _MapColor fallback.
            mat.SetFloat("_LinePattern", paint.PatternName != null ? 1f : 0f);

            // S43: line-dasharray initial bind (constant or first zoom-step evaluation at zoom=0).
            // Per-frame re-evaluation for zoom-step patterns is done by ApplyLineDashArray in ApplyZoom.
            // Feature-dependent dasharray is out of scope; constant + zoom-step are the supported forms.
            // S43_DEFER_LIVE_ZOOM_STEP: zoom-step dasharray re-evaluates per-frame via ApplyLineDashArray;
            // static bind here is for constant arrays only (zoom=0 is a safe initial value).
            ApplyLineDashArray(paint, mat, 0.0);
        }

        /// <summary>
        /// S43: Evaluates the line-dasharray expression at <paramref name="zoom"/> and sets
        /// <c>_DashArray</c>/<c>_DashCount</c> on the material. Called both at bind time and
        /// per-frame (for zoom-step patterns). When absent or degenerate, sets _DashCount=0
        /// (solid identity — no change to rendering path).
        ///
        /// Not routed through ZoomStyleApplier (scalar/color only). Array evaluation uses
        /// <see cref="LineDash.TryEvaluateDashArray"/> directly.
        /// </summary>
        private static void ApplyLineDashArray(LinePaint paint, Material mat, double zoom)
        {
            if (!paint.HasDashArray)
            {
                // No dasharray: ensure solid identity (guard against stale values).
                mat.SetVector("_DashArray", Vector4.zero);
                mat.SetFloat("_DashCount",  0f);
                return;
            }

            if (LineDash.TryEvaluateDashArray(paint.DashArrayJson, zoom, out float[] pattern))
            {
                var (x, y, z, w, count) = LineDash.Pack(pattern);
                mat.SetVector("_DashArray", new Vector4(x, y, z, w));
                mat.SetFloat("_DashCount",  count);
            }
            else
            {
                // Parse failed: solid fallback.
                mat.SetVector("_DashArray", Vector4.zero);
                mat.SetFloat("_DashCount",  0f);
            }
        }

        /// <summary>Dispose all fill and line layer Material instances.</summary>
        private void DisposeLayerRecords()
        {
            for (int i = 0; i < _layerRecords.Count; i++)
            {
                var mat = _layerRecords[i].Material;
                if (mat != null)
                {
                    if (Application.isPlaying) Destroy(mat);
                    else                       DestroyImmediate(mat);
                }
            }
            _layerRecords.Clear();

            // S14: dispose line material instances.
            for (int i = 0; i < _lineRecords.Count; i++)
            {
                var mat = _lineRecords[i].Material;
                if (mat != null)
                {
                    if (Application.isPlaying) Destroy(mat);
                    else                       DestroyImmediate(mat);
                }
            }
            _lineRecords.Clear();
        }

        private void Update()
        {
            UpdateFrame(Time.deltaTime);
        }

        /// <summary>
        /// Releases all tile GameObjects and Mesh assets, drains tessellation tasks, disposes the
        /// scheduler, and (if owned) the data source.
        ///
        /// This is the canonical teardown body. <see cref="OnDestroy"/> delegates to it so that
        /// production Play-mode cleanup (triggered automatically by Unity's lifecycle) shares the
        /// same path.
        ///
        /// <para>
        /// <b>Why a separate public method?</b><br/>
        /// In Unity EditMode (no <c>[ExecuteAlways]</c> attribute), <c>MonoBehaviour.OnDestroy</c>
        /// is <em>not</em> triggered when <c>Object.DestroyImmediate(go)</c> is called from an
        /// EditMode test — Unity only fires lifecycle callbacks (<c>Awake</c>/<c>Start</c>/
        /// <c>OnDestroy</c>) for components that opted into Edit-Mode execution. Headless Edit-Mode
        /// tests that need to exercise the cleanup contract call <c>Teardown()</c> explicitly before
        /// destroying the GameObject; production code relies on the <c>OnDestroy</c> delegation.
        /// </para>
        ///
        /// <para>Idempotent: safe to call more than once (subsequent calls are no-ops).</para>
        /// </summary>
        public void Teardown()
        {
            if (_scheduler == null) return; // already torn down (idempotent guard)

            // S51/S48: drain outstanding tessellation UniTasks before tearing down.
            // Safe spin: tessellation UniTasks use configureAwait: false (UniTask.RunOnThreadPool),
            // so IsCompleted becomes true on the ThreadPool without needing the PlayerLoop. Spinning
            // here on the main thread is therefore deadlock-free.
            //
            // S48: after spinning, dispose the NativeArray payload — we're tearing down and must
            // not leak. (These are tiles still in _loaded; mid-flight-released tiles are in
            // _pendingDisposal, drained separately below.)
            foreach (var kv in _loaded)
            {
                if (kv.Value.HasTessellationTask)
                {
                    var tessTask = kv.Value.TessellationTask;
                    // Thread.Sleep(1) yields real CPU time so the ThreadPool can complete the task.
                    int spins = 0;
                    while (!tessTask.Status.IsCompleted() && spins++ < 10000)
                        Thread.Sleep(1);

                    // S48: dispose the produced NativeArrays (or no-op if faulted/cancelled).
                    if (tessTask.Status == UniTaskStatus.Succeeded)
                    {
                        TessellationResult result = tessTask.GetAwaiter().GetResult();
                        if (result.LayerData != null)
                        {
                            for (int li = 0; li < result.LayerData.Length; li++)
                                result.LayerData[li].Dispose();
                        }
                        // S14: dispose line NativeArrays.
                        if (result.LineLayerData != null)
                        {
                            for (int li = 0; li < result.LineLayerData.Length; li++)
                                result.LineLayerData[li].Dispose();
                        }
                    }
                }
            }

            // S48: drain the mid-flight-discard holding pen — spin to completion, then dispose.
            for (int i = 0; i < _pendingDisposal.Count; i++)
            {
                var task = _pendingDisposal[i];
                int spins = 0;
                while (!task.Status.IsCompleted() && spins++ < 10000)
                    Thread.Sleep(1);

                if (task.Status == UniTaskStatus.Succeeded)
                {
                    TessellationResult result = task.GetAwaiter().GetResult();
                    if (result.LayerData != null)
                    {
                        for (int li = 0; li < result.LayerData.Length; li++)
                            result.LayerData[li].Dispose();
                    }
                    // S14: dispose line NativeArrays.
                    if (result.LineLayerData != null)
                    {
                        for (int li = 0; li < result.LineLayerData.Length; li++)
                            result.LineLayerData[li].Dispose();
                    }
                }
            }
            _pendingDisposal.Clear();

            // Destroy sharedMesh assets for each tile container, then destroy the container.
            //
            // S51 leak guard: Mesh assets set as MeshFilter.sharedMesh are NOT destroyed when the
            // containing GameObject is destroyed — Unity treats sharedMesh as a shared asset, not
            // a component-owned one. lt.Meshes holds direct Mesh references (set by ConsumeTessellationTask)
            // for reliable, index-safe destruction. DestroyTrackedMeshes iterates lt.Meshes directly,
            // avoiding the GetComponentsInChildren approach (which requires a live GameObject hierarchy).
            //
            // Order: destroy meshes → destroy container → (Unity destroys parent → children recursively).
            foreach (var kv in _loaded)
            {
                var lt = kv.Value;
                // DestroyTrackedMeshes takes ref — use a local copy (foreach var is read-only).
                DestroyTrackedMeshes(ref lt);
                if (lt.Go != null)
                {
                    if (Application.isPlaying) Destroy(lt.Go);
                    else                       DestroyImmediate(lt.Go);
                }
            }
            _loaded.Clear();

            DisposeLayerRecords();

            _scheduler?.Dispose();
            _scheduler = null; // idempotent guard

            if (_ownsSource) _source?.Dispose();
            _source = null;
        }

        private void OnDestroy()
        {
            Teardown();
        }
    }
}
