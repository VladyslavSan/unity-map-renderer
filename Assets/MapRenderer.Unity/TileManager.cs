using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity
{
    /// <summary>
    /// Owns the tile lifecycle for <see cref="MapView"/> — the cover→fetch→tessellate→consume→evict
    /// pipeline plus the S48/S51 disposal &amp; mesh-leak guards. Extracted from MapView as the biggest,
    /// most cohesive cut of the decomposition: MapView keeps the camera, the <see cref="StyledLayerSet"/>,
    /// and the scene origin; this object keeps the scheduler, the loaded-tile table, and all the in-flight
    /// async machinery, and is just ticked once per frame.
    ///
    /// <para>Explicit interface (the three things the lifecycle needs from MapView, passed in per call so
    /// MapView's inspector-editable config and rebasing scene origin stay authoritative):
    /// <list type="bullet">
    ///   <item>the current <see cref="CameraProperties"/>,</item>
    ///   <item>the scene origin (Mercator) for tile placement,</item>
    ///   <item>tile-selection config (<see cref="TileSelectionConfig"/>) read from MapView's serialized fields.</item>
    /// </list>
    /// The <see cref="StyledLayerSet"/> (render bundles) is stable for the object's life, so it's injected
    /// at construction.</para>
    ///
    /// <para>The consume step (<see cref="ConsumeTessellationTask"/>) registers each tile-layer mesh as a
    /// draw item via the selected <see cref="ITileRenderBackend"/> (Entities, BRG, or GameObject). The
    /// backend is uniform behind that interface — TileManager has no per-backend branching.</para>
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    internal sealed class TileManager
    {
        // ── Profiler markers (allocation-free; static readonly = constructed once at type-init) ──
        // Namespace: MapRenderer.* — greppable per S46 acceptance. Names must match exactly
        // (ProfilerMarkerTests asserts the MapRenderer.* string set).
        private static readonly ProfilerMarker PmCoverSelect  = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.CoverSelect");
        private static readonly ProfilerMarker PmFetchPoll    = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.FetchPoll");
        private static readonly ProfilerMarker PmSchedulerReq = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Scheduler.Request");
        private static readonly ProfilerMarker PmTileDecode   = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.Decode");
        private static readonly ProfilerMarker PmMeshUpload    = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Mesh.Upload");
        // Split out from Mesh.Upload: registering the mesh with the backend (Entities = create entity +
        // RenderMeshUtility.AddComponents + EG batch registration; BRG = add a draw item). Separated so a
        // build-time spike is attributable to GPU upload vs ECS structural-change/batch churn.
        private static readonly ProfilerMarker PmAddTileLayer  = new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.Tile.AddLayer");

        /// <summary>
        /// Tile-selection knobs read from MapView's serialized (inspector-editable) fields and passed in
        /// each <see cref="Tick"/> — NOT snapshotted at construction, so runtime inspector tweaks take
        /// effect immediately (the originals must stay serialized on the MonoBehaviour).
        /// </summary>
        public struct TileSelectionConfig
        {
            public float ViewportAspect;
            public float PadFactor;
            public int   MinZoom;
            public int   MaxZoom;
            public int   MaxBuildsPerTick;
        }

        // ── S47 tessellation payload (S51: Task → UniTask) ────────────────────────────────────

        /// <summary>
        /// Per-layer mesh data produced by one tile's background tessellation.
        /// One element per fill layer, one element per line layer.
        /// </summary>
        private struct TessellationResult
        {
            /// <summary>Per-fill-layer CPU mesh data (index matches _layers.Fills).</summary>
            public StyledFillTileBuilder.LayerMeshData[] LayerData;
            /// <summary>S14: per-line-layer CPU mesh data (index matches _layers.Lines).</summary>
            public StyledLineTileBuilder.LayerMeshData[] LineLayerData;
        }

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
            public double2                   TileOriginMerc;
            /// <summary>
            /// S51 leak guard: per-layer Mesh assets created by ConsumeTessellationTask. Must be
            /// explicitly destroyed on release/teardown (Unity does not destroy a Mesh asset just because
            /// nothing references it). Null until the tile is consumed; set by ConsumeTessellationTask.
            /// </summary>
            public Mesh[]                    Meshes;
            /// <summary>
            /// Backend draw-item handles for each tile-layer mesh registered with the
            /// <see cref="ITileRenderBackend"/> (Entities, BRG, or GameObject). Set by ConsumeTessellationTask;
            /// used by ReleaseTile to unregister the draw items.
            /// </summary>
            public int[]                     DrawHandles;
        }

        // ── Injected collaborators (stable for life) ─────────────────────────────────────────
        private readonly StyledLayerSet _layers; // owned by MapView; this reads bundles/materials/counts

        // ── Live state ─────────────────────────────────────────────────────────────────────────────────
        private TileScheduler   _scheduler;
        private IDataSource     _source;
        private bool            _ownsSource;

        // ── Tile render backend (Entities, BRG, or GameObject) — constructed in Initialise ────
        private ITileRenderBackend _instanced; // null only before Initialise / after Dispose

        // Reused buffers — never reallocated in steady state.
        private readonly List<TileId>                   _cover     = new List<TileId>(64);
        private readonly HashSet<TileId>                _coverSet  = new HashSet<TileId>();
        private readonly Dictionary<TileId, LoadedTile> _loaded    = new Dictionary<TileId, LoadedTile>();
        private readonly List<TileId>                   _toRelease = new List<TileId>(32);

        private bool _coverDirty = true;

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
        // This list is only modified on the main thread (ReleaseTile, DrainPendingDisposal, Dispose
        // are all main-thread). No locking is required.
        private readonly List<UniTask<TessellationResult>> _pendingDisposal = new List<UniTask<TessellationResult>>(8);

        public TileManager(StyledLayerSet layers)
        {
            _layers = layers;
        }

        // ── Lifecycle / injection ────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates the scheduler over <paramref name="source"/> and resets selection state. If
        /// <paramref name="ownsSource"/> is true, <see cref="Dispose"/> disposes the source.
        ///
        /// Constructs the tile render backend for <paramref name="backend"/>: a <see cref="BrgTileRenderer"/>
        /// (Brg), a <see cref="GameObjectTileRenderer"/> (GameObject), or an <see cref="EntitiesTileRenderer"/>
        /// (Entities, the default), all built from the styled layer set passed at construction.
        /// </summary>
        public void Initialise(IDataSource source, bool ownsSource,
            RenderBackend backend = RenderBackend.Entities)
        {
            _source     = source;
            _ownsSource = ownsSource;
            _scheduler  = new TileScheduler(source, new TileCache(capacity: 256));

            _coverDirty          = true;
            _coverKeyInitialised = false;

            // Construct the backend (built from the styled layer set passed at construction).
            // The default arm is Entities so any unknown/legacy serialized value resolves safely.
            _instanced?.Dispose(); // dispose any prior backend (e.g. re-initialise after teardown)
            _instanced = backend switch
            {
                RenderBackend.Brg        => new BrgTileRenderer(_layers),
                RenderBackend.GameObject => new GameObjectTileRenderer(FlattenLayerMaterials(_layers), FlattenLayerNames(_layers)),
                _                        => new EntitiesTileRenderer(FlattenLayerMaterials(_layers), FlattenLayerNames(_layers)),
            };
        }

        /// <summary>Flattens the styled layer set's materials (fills in declared order, then lines) — the
        /// material list every backend indexes by <c>materialIndex</c>.</summary>
        private static System.Collections.Generic.List<Material> FlattenLayerMaterials(StyledLayerSet layers)
        {
            var mats = new System.Collections.Generic.List<Material>(layers.FillCount + layers.LineCount);
            for (int i = 0; i < layers.FillCount; i++) mats.Add(layers.Fills[i].Material);
            for (int i = 0; i < layers.LineCount; i++) mats.Add(layers.Lines[i].Material);
            return mats;
        }

        /// <summary>Flattens the per-layer style ids in the same (fills then lines) order as
        /// <see cref="FlattenLayerMaterials"/>, so the Entities backend can name each layer entity after its
        /// style layer (e.g. "water") in the Entities Hierarchy instead of the shared material name.</summary>
        private static System.Collections.Generic.List<string> FlattenLayerNames(StyledLayerSet layers)
        {
            var names = new System.Collections.Generic.List<string>(layers.FillCount + layers.LineCount);
            for (int i = 0; i < layers.FillCount; i++) names.Add(layers.Fills[i].StyleLayer?.Id);
            for (int i = 0; i < layers.LineCount; i++) names.Add(layers.Lines[i].StyleLayer?.Id);
            return names;
        }

        /// <summary>True once <see cref="Initialise"/> has been called successfully.</summary>
        public bool IsInitialised => _scheduler != null;

        // ── Test observability (internal; surfaced to tests through MapViewTestExtensions, not the
        //    production API). TileManager is already an internal type, so these stay close to the state
        //    they read; MapView no longer mirrors them. ───────────────────────────────────────────────

        /// <summary>The scheduler's in-flight fetch count.</summary>
        internal int InFlightCount => _scheduler != null ? _scheduler.InFlightCount : 0;

        /// <summary>Number of currently loaded (or loading) tiles.</summary>
        internal int LoadedTileCount => _loaded.Count;

        /// <summary>
        /// Number of tiles released while their tessellation was still in-flight (HasTessellationTask
        /// and not yet Built at the moment of release). Incremented by ReleaseTile. Read by tests
        /// to prove the mid-flight race actually occurred in <c>S51DisposalLeakGuardTests</c>.
        /// </summary>
        internal int ReleasedMidFlightCount => _releasedMidFlightCount;

        /// <summary>The live BRG renderer, or null when not on the BRG backend / before <see cref="Initialise"/>.</summary>
        internal BrgTileRenderer BrgRenderer => _instanced as BrgTileRenderer;

        /// <summary>The live Entities-Graphics renderer, or null when not on the Entities backend / before <see cref="Initialise"/>.</summary>
        internal EntitiesTileRenderer EntitiesRenderer => _instanced as EntitiesTileRenderer;

        /// <summary>The live GameObject renderer, or null when not on the GameObject backend / before <see cref="Initialise"/>.</summary>
        internal GameObjectTileRenderer GameObjectRenderer => _instanced as GameObjectTileRenderer;

        /// <summary>
        /// Invalidates the cached cover-selection key so the next <see cref="Tick"/> re-selects the cover.
        /// Called when the camera is re-wired (<see cref="MapView.SetCamera"/>).
        /// </summary>
        public void InvalidateCover() => _coverKeyInitialised = false;

        /// <summary>
        /// Rebuilds the per-tile object-to-world transforms (and refreshes backend state) for all loaded
        /// tiles from <paramref name="sceneOrigin"/> (S52 camera-relative rendering: the origin tracks the
        /// look-at). Called by MapView once per frame.
        /// </summary>
        public void InstancedRebuild(double2 sceneOrigin)
        {
            _instanced?.Rebuild(sceneOrigin);
        }

        /// <summary>
        /// Test-only: true when the tile is loaded AND produced geometry — <c>lt.Built</c> and either draw
        /// handles were registered with the backend or meshes were tracked. Backend-agnostic: TileManager
        /// tracks no per-tile GameObject, so there is nothing to hand back but the boolean.
        /// </summary>
        internal bool TryGetBuiltTile(TileId id)
            => _loaded.TryGetValue(id, out var lt) && lt.Built && (lt.DrawHandles != null || lt.Meshes != null);

        /// <summary>
        /// Test-only, backend-agnostic: the <see cref="Mesh"/> assets built for a loaded tile (one per
        /// rendered layer, fills then lines), or null if the tile is not built / produced no geometry.
        /// Replaces the old "inspect the tile's child GameObjects" probe.
        /// </summary>
        internal Mesh[] GetTileMeshes(TileId id)
            => _loaded.TryGetValue(id, out var lt) ? lt.Meshes : null;

        /// <summary>Test-only: scene-space bounds of all live tile draw items (for camera framing), via
        /// the instanced backend. <paramref name="tileSizeWorld"/> is the tile's world extent at the
        /// current zoom. Returns <c>default</c> if no backend / no tiles.</summary>
        internal Bounds ComputeSceneBounds(float tileSizeWorld)
            => _instanced != null ? _instanced.ComputeSceneBounds(tileSizeWorld) : default;

        /// <summary>
        /// Test-only: true once every loaded tile has finished building (or is definitively absent).
        /// S47/S51: returns false while any tile has a pending tessellation.
        /// </summary>
        internal bool AllTilesSettled()
        {
            foreach (var kv in _loaded)
                if (!kv.Value.Built) return false;
            return true;
        }

        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One frame of the tile loop. Allocation-free in steady state.
        ///
        /// S47/S51: no main-thread tessellation. PumpPendingBuilds only KICKS background UniTasks
        /// on fetch completion; ConsumeTessellationResults polls and CONSUMES completed UniTasks
        /// (uploads mesh + creates GameObjects). Neither step blocks on tessellation.
        ///
        /// The caller (MapView) runs <see cref="StyledLayerSet.ApplyZoom"/> and refreshes the scene
        /// origin BEFORE this — the origin is passed in so tile placement and camera sync share it.
        /// </summary>
        public void Tick(CameraProperties cam, TileSelectionConfig cfg)
        {
            if (_scheduler == null) return;

            int integerZoom = cam.IntegerZoom;
            if (!_coverKeyInitialised ||
                cam.LookAt.Longitude != _coverKeyLon ||
                cam.LookAt.Latitude != _coverKeyLat ||
                integerZoom    != _coverKeyIntegerZoom)
            {
                _coverDirty = true;
            }

            // S48: drain any completed mid-flight-discard tasks so their NativeArrays are freed.
            DrainPendingDisposal();

            int pending = PumpPendingBuilds(cam, cfg.MaxBuildsPerTick);

            if (!_coverDirty && pending == 0)
                return;

            using var sCoverSel = PmCoverSelect.Auto();

            TileCover.Cover(cam, cfg.ViewportAspect, cfg.PadFactor, cfg.MinZoom, cfg.MaxZoom, _cover);

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

            _coverKeyLon         = cam.LookAt.Longitude;
            _coverKeyLat         = cam.LookAt.Latitude;
            _coverKeyIntegerZoom = integerZoom;
            _coverKeyInitialised = true;

            _coverDirty = false;
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
        internal void DrainTessellation(CameraProperties cam)
        {
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
        private int PumpPendingBuilds(CameraProperties cam, int maxBuildsPerTick)
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
                    if (builds >= maxBuildsPerTick)
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
        /// path in Dispose poll IsCompleted on the main thread WITHOUT pumping the PlayerLoop, so
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
            var layerRecordsSnapshot     = _layers.SnapshotFills();
            var lineRecordsSnapshot      = _layers.SnapshotLines();
            double zoom          = cam.Zoom;
            double2 tileOrigin   = lt.TileOriginMerc;

            return UniTask.RunOnThreadPool(() =>
            {
                MvtTile mvtTile = MvtDecoder.Decode(mvtBytes);

                // ── Fill layers ────────────────────────────────────────────────
                var layerData = new StyledFillTileBuilder.LayerMeshData[layerRecordsSnapshot.Length];

                for (int li = 0; li < layerRecordsSnapshot.Length; li++)
                {
                    var rec = layerRecordsSnapshot[li];

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
                    var rec = lineRecordsSnapshot[li];

                    var features = MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(
                        rec.StyleLayer, mvtTile, zoom);
                    if (features.Count == 0)
                        continue; // IsCreated=false, no dispose needed

                    MvtLayer mvtLayer = MapRenderer.Core.Style.SourceLayerResolver.ResolveMvtLayer(
                        rec.StyleLayer, mvtTile);
                    if (mvtLayer == null)
                        continue;

                    lineLayerData[li] = StyledLineTileBuilder.BuildMeshData(
                        features, rec.Paint, rec.Layout, zoom, mvtLayer.Extent, id, tileOrigin);
                }

                return new TessellationResult { LayerData = layerData, LineLayerData = lineLayerData };
            }, configureAwait: false).Preserve(); // .Preserve() allows polling .IsCompleted across multiple frames
        }

        /// <summary>
        /// Consumes a completed tessellation: uploads each layer's mesh and registers it as a draw item
        /// with the selected backend (Entities, BRG, or GameObject).
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

                bool hasFillLayers = _layers.FillCount > 0;
                bool hasLineLayers = _layers.LineCount > 0;
                if (!hasFillLayers && !hasLineLayers) return;

                bool anyGeometry = false;
                // S51 leak guard: track created Mesh assets so they can be explicitly destroyed on release
                // (Unity does not free a Mesh asset just because nothing references it).
                var createdMeshes = new System.Collections.Generic.List<Mesh>(8);

                // Backend draw-item handles (one per tile-layer mesh).
                var drawHandles = new System.Collections.Generic.List<int>(8);

                // Register each tile-layer mesh with the selected backend (Entities, BRG, or GameObject).
                // Material index = fill index, then FillCount + line index (matching the flattened
                // layer-material order every backend indexes by).

                // ── Fill layers ───────────────────────────────────────────
                if (result.LayerData != null)
                {
                    for (int li = 0; li < _layers.FillCount && li < result.LayerData.Length; li++)
                    {
                        // S48: UploadMesh uses the advanced NativeArray API (no managed Set* calls).
                        // It does NOT dispose data — we dispose in the finally block after the loop.
                        Mesh mesh;
                        using (PmMeshUpload.Auto())
                            mesh = StyledFillTileBuilder.UploadMesh(result.LayerData[li]);
                        if (mesh == null) continue;

                        createdMeshes.Add(mesh); // S51: track Mesh assets for explicit destruction

                        int handle;
                        using (PmAddTileLayer.Auto())
                            handle = _instanced.AddTileLayer(mesh, lt.TileOriginMerc, li, id);
                        drawHandles.Add(handle);
                        anyGeometry = true;
                    }
                }

                // ── S14: Line layers ──────────────────────────────────────
                if (result.LineLayerData != null)
                {
                    for (int li = 0; li < _layers.LineCount && li < result.LineLayerData.Length; li++)
                    {
                        Mesh mesh;
                        using (PmMeshUpload.Auto())
                            mesh = StyledLineTileBuilder.UploadMesh(result.LineLayerData[li]);
                        if (mesh == null) continue;

                        createdMeshes.Add(mesh);

                        int handle;
                        using (PmAddTileLayer.Auto())
                            handle = _instanced.AddTileLayer(mesh, lt.TileOriginMerc, _layers.FillCount + li, id);
                        drawHandles.Add(handle);
                        anyGeometry = true;
                    }
                }

                if (!anyGeometry) return;

                lt.Meshes      = createdMeshes.Count > 0 ? createdMeshes.ToArray() : null;
                lt.DrawHandles = drawHandles.Count > 0 ? drawHandles.ToArray() : null;
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
        /// Releases a tile: scheduler release + unregister its instanced draw items + free its meshes.
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

                // Unregister the instanced draw items before destroying Mesh assets. RemoveItem drops the
                // draw item (and, on Entities, its layer entity + the tile root once empty); the Mesh
                // asset is then freed below via DestroyTrackedMeshes.
                if (_instanced != null && lt.DrawHandles != null)
                {
                    for (int hi = 0; hi < lt.DrawHandles.Length; hi++)
                        _instanced.RemoveItem(lt.DrawHandles[hi]);
                }

                // S51 leak guard: destroy tracked Mesh assets explicitly (Unity does not free a Mesh
                // asset just because nothing references it). lt.Meshes holds direct references.
                DestroyTrackedMeshes(ref lt);

                _loaded.Remove(id);
            }
            _scheduler.Release(id);
        }

        /// <summary>
        /// S48: Drains completed tasks from the mid-flight-discard holding pen, disposing their
        /// NativeArray payloads. Called once per Tick and in Dispose.
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
        /// S51 leak guard: a Mesh asset is NOT freed just because nothing references it.
        /// <see cref="ConsumeTessellationTask"/> populates <c>lt.Meshes</c> with direct references to
        /// every Mesh it creates; this method iterates that array for reliable, deterministic destruction.
        /// After destruction, <c>lt.Meshes</c> is nulled to prevent double-free.
        /// Must be called on the Unity main thread (Object.Destroy constraint).
        /// </summary>
        private void DestroyTrackedMeshes(ref LoadedTile lt)
        {
            if (lt.Meshes == null) return;
            for (int i = 0; i < lt.Meshes.Length; i++)
            {
                if (lt.Meshes[i] != null)
                {
                    if (Application.isPlaying) Object.Destroy(lt.Meshes[i]);
                    else                       Object.DestroyImmediate(lt.Meshes[i], allowDestroyingAssets: true);
                }
            }
            lt.Meshes = null;
        }

        /// <summary>
        /// Releases all tile GameObjects and Mesh assets, drains tessellation tasks, and disposes the
        /// scheduler and (if owned) the data source. Does NOT dispose the StyledLayerSet — MapView owns
        /// that and disposes it AFTER this (tile renderers reference layer materials, so the order matters).
        ///
        /// <para>Idempotent: safe to call more than once (subsequent calls are no-ops via the
        /// <c>_scheduler == null</c> guard).</para>
        /// </summary>
        public void Dispose()
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

            // Destroy each tile's tracked Mesh assets.
            //
            // S51 leak guard: a Mesh asset is NOT freed just because nothing references it. lt.Meshes
            // holds direct Mesh references (set by ConsumeTessellationTask) for reliable, index-safe
            // destruction; DestroyTrackedMeshes iterates that array directly.
            //
            // Order: destroy meshes → dispose the backend (below), so the backend never references a
            // freed Mesh.
            foreach (var kv in _loaded)
            {
                var lt = kv.Value;
                // DestroyTrackedMeshes takes ref — use a local copy (foreach var is read-only).
                DestroyTrackedMeshes(ref lt);
            }
            _loaded.Clear();

            // Dispose the instanced backend AFTER destroying all tile meshes (it references mesh IDs that
            // become invalid when the Mesh assets are destroyed; this order keeps it from drawing freed
            // meshes). S49 tooth 5: BRG + GraphicsBuffer released; S53b: Entities World disposed.
            _instanced?.Dispose();
            _instanced = null;

            _scheduler?.Dispose();
            _scheduler = null; // idempotent guard

            if (_ownsSource) _source?.Dispose();
            _source = null;
        }
    }
}
