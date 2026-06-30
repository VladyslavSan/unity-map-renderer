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
using BRGBackend = MapRenderer.Unity.Rendering.Backend.BRG;
using EntBackend = MapRenderer.Unity.Rendering.Backend.Entities;
using GOBackend = MapRenderer.Unity.Rendering.Backend.GameObjects;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// Owns the tile lifecycle for <see cref="Map.View"/> — the cover→fetch→tessellate→consume→evict
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
    /// draw item via the selected <see cref="Backend.ITileRenderBackend"/> (Entities, BRG, or GameObject). The
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
            /// <summary>S71: the FRAMING viewport in pixels — <c>(refH · liveAspect, refH)</c>, NOT raw live
            /// px (see <see cref="IVisibleTileSelector"/> D6/D7). Flows into the per-tick
            /// <see cref="ViewContext"/>.</summary>
            public double2      FramingViewportPx;
            /// <summary>S71: the active pixel↔ground projection (Web-Mercator today). Per-frame view context.</summary>
            public IProjection  Projection;
            /// <summary>S87: per-frame MESH-upload count budget — the max number of tile-layer meshes
            /// uploaded + registered with the backend per Tick (was an S55 per-TILE cap; now per-mesh, since a
            /// single rich tile's layers are consumed resumably across frames). Bounds AddLayer/entity-add and
            /// GPU upload per frame — the responsiveness knob (pair with <see cref="MaxVerticesPerTick"/>;
            /// whichever binds first stops the frame). <b>0 blocks consume entirely</b> (used by tests to build
            /// a backlog) — it is NOT "uncapped"; set it high (e.g. 64) for effectively-uncapped.</summary>
            public int          MaxBuildsPerTick;
            /// <summary>S55: max tessellation kick-offs per Tick (Phase 1). Default 2 (MapView serialized field).
            /// Caps the background tessellation fan-out per frame without dropping work.
            /// 0 means uncapped (same as int.MaxValue) so unset config structs are harmless.</summary>
            public int          MaxTessellationsPerTick;
            /// <summary>S55/S87: per-frame VERTEX budget for Phase-2 consume (S87: per-MESH granularity).
            /// Default 50000. Layer meshes are consumed one at a time until the running vertex total reaches the
            /// budget, then the rest defer to the next Tick; the mesh that crosses the budget is still consumed
            /// (one-MESH overshoot, documented — a single mesh cannot be split). 0 means uncapped.</summary>
            public int          MaxVerticesPerTick;
        }

        // ── S47 tessellation payload (S51: Task → UniTask) ────────────────────────────────────

        /// <summary>
        /// Per-layer mesh data produced by one tile's background tessellation.
        /// One element per fill layer, one element per line layer.
        /// </summary>
        private struct TessellationResult
        {
            /// <summary>Per-fill-layer CPU mesh data (index matches _layers.Fills).</summary>
            public Meshing.StyledFillTileBuilder.LayerMeshData[] LayerData;
            /// <summary>S14: per-line-layer CPU mesh data (index matches _layers.Lines).</summary>
            public Meshing.StyledLineTileBuilder.LayerMeshData[] LineLayerData;
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
            /// <see cref="Backend.ITileRenderBackend"/> (Entities, BRG, or GameObject). Set by ConsumeTessellationTask;
            /// used by ReleaseTile to unregister the draw items.
            /// </summary>
            public int[]                     DrawHandles;
            /// <summary>
            /// S87: resumable per-mesh consume cursor — index of the next layer to upload, spanning fill
            /// <c>[0..FillCount)</c> then line <c>[0..LineCount)</c>. Advanced by
            /// <see cref="ConsumeTessellationTask"/> as the per-frame mesh/vertex budget allows; the tile is
            /// <see cref="Built"/> only once the cursor reaches the end. 0 until consume starts. While
            /// <c>0 &lt; ConsumeCursor &lt; total</c> the tile is partially consumed (HasTessellationTask is
            /// still true, the task is COMPLETE, and some layers are already in Meshes/DrawHandles).
            /// </summary>
            public int                       ConsumeCursor;
            /// <summary>
            /// S55: MVT bytes received from fetch and awaiting a (capped) tessellation kick.
            /// Set when the fetch completes and the per-tick kick cap has been reached. Null in all
            /// other states. Cleared (to null) when KickTessellationTask fires. The byte[] is GC-owned;
            /// no special disposal needed on eviction.</summary>
            public byte[]                    ReadyBytes;
        }

        /// <summary>
        /// S83b: the composite key for the multi-source loaded table — a tile address paired with the
        /// <see cref="SourcePipeline.Slot"/> of the source that fetched it. The lifecycle is per-(tile,
        /// source): a tile drawn from N sources has N records, one per pipeline.
        ///
        /// <b>Value-type + <see cref="System.IEquatable{T}"/></b> so it is a <b>zero-boxing</b> Dictionary
        /// key — the steady-state no-GC contract (the S53b zero-alloc tooth runs <c>_loaded.ContainsKey</c>
        /// per cover-tile×pipeline every Tick; a plain struct key would box on every probe via
        /// <c>ValueType.Equals</c>/<c>GetHashCode</c> and trip the recorder). Mirrors <see cref="TileId"/>.
        /// </summary>
        private readonly struct LoadedKey : System.IEquatable<LoadedKey>
        {
            public readonly TileId Tile;
            public readonly int    Slot;
            public LoadedKey(TileId tile, int slot) { Tile = tile; Slot = slot; }

            public bool Equals(LoadedKey other) => Slot == other.Slot && Tile.Equals(other.Tile);
            public override bool Equals(object obj) => obj is LoadedKey o && Equals(o);
            public override int GetHashCode() { unchecked { return Tile.GetHashCode() * 31 + Slot; } }
        }

        /// <summary>
        /// S83b: one data pipeline per RENDERED source-id (a source with ≥1 fill/line layer bound to it).
        /// Per-source <see cref="TileScheduler"/> + <see cref="TileCache"/> (independent ownership: each
        /// source's bytes/in-flight/negative-cache/lifetime survive a restyle of <i>other</i> sources). The
        /// <see cref="Slot"/> is the stable index into <see cref="_pipelines"/> and the per-source component
        /// of <see cref="LoadedKey"/>, so the per-frame loop never hashes a string.
        /// </summary>
        private sealed class SourcePipeline
        {
            public string        SourceId;   // the StyleLayer.Source this pipeline serves (normalized, never null)
            public int           Slot;        // index into _pipelines (0..N-1)
            public IDataSource   Source;
            public bool          OwnsSource;  // dispose Source on teardown (false when shared, e.g. legacy Initialise)
            public TileScheduler Scheduler;
            public TileCache     Cache;
            public int           MinZoom;     // resolved source minzoom — admission clamp (decision 10)
            public int           MaxZoom;     // resolved source maxzoom
            public SourceKey     DefKey;      // resolved-definition identity — restyle "unchanged?" diff (7a)
        }

        /// <summary>
        /// S83b: value-equality identity of a resolved source definition — the restyle diff key (decision
        /// 7a). Two sources are "the same" (keep the pipeline, reuse cached bytes) iff their resolved
        /// <c>Url</c>/<c>tiles[]</c>/zoom/scheme/bounds match. Remote-TileJSON content drift is out of scope
        /// (no refresh — decision 4), so equality is purely over the resolved fields.
        /// </summary>
        internal readonly struct SourceKey : System.IEquatable<SourceKey>
        {
            public readonly string Url;
            public readonly string Tiles;   // tiles[] joined with '\n' — cheap value-equality
            public readonly int    MinZoom;
            public readonly int    MaxZoom;
            public readonly string Scheme;
            public readonly string Bounds;  // bounds joined with ',' — value-equality (null when default/absent)

            public SourceKey(string url, string tiles, int minZoom, int maxZoom, string scheme, string bounds)
            { Url = url; Tiles = tiles; MinZoom = minZoom; MaxZoom = maxZoom; Scheme = scheme; Bounds = bounds; }

            /// <summary>Builds the key from a resolved <see cref="SourceDefinition"/> (post-S83a resolution).</summary>
            public static SourceKey From(SourceDefinition def)
            {
                string tiles  = def.Tiles  != null ? string.Join("\n", def.Tiles)  : null;
                string bounds = def.Bounds != null ? string.Join(",",  def.Bounds) : null;
                return new SourceKey(def.Url, tiles, def.MinZoom, def.MaxZoom, def.Scheme, bounds);
            }

            public bool Equals(SourceKey o)
                => Url == o.Url && Tiles == o.Tiles && MinZoom == o.MinZoom
                   && MaxZoom == o.MaxZoom && Scheme == o.Scheme && Bounds == o.Bounds;
            public override bool Equals(object obj) => obj is SourceKey o && Equals(o);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + (Url    ?? string.Empty).GetHashCode();
                    h = h * 31 + (Tiles  ?? string.Empty).GetHashCode();
                    h = h * 31 + MinZoom;
                    h = h * 31 + MaxZoom;
                    h = h * 31 + (Scheme ?? string.Empty).GetHashCode();
                    h = h * 31 + (Bounds ?? string.Empty).GetHashCode();
                    return h;
                }
            }
        }

        /// <summary>
        /// S83b: the caller's (MapView.SetStyle) recipe for one rendered source pipeline. Carries a
        /// <see cref="CreateSource"/> thunk rather than a built <see cref="IDataSource"/> so
        /// <see cref="SetSources"/> only constructs sources for NEW/CHANGED pipelines — a restyle keeps an
        /// unchanged source's existing instance (and its warm cache), never re-creating it (decision 7a).
        /// </summary>
        internal readonly struct SourceSpec
        {
            public readonly string                SourceId;
            public readonly SourceKey             Key;
            public readonly int                   MinZoom;
            public readonly int                   MaxZoom;
            public readonly System.Func<IDataSource> CreateSource;

            public SourceSpec(string sourceId, SourceKey key, int minZoom, int maxZoom,
                System.Func<IDataSource> createSource)
            { SourceId = sourceId; Key = key; MinZoom = minZoom; MaxZoom = maxZoom; CreateSource = createSource; }
        }

        // ── Injected collaborators (stable for life) ─────────────────────────────────────────
        private readonly Style.StyledLayerSet _layers; // owned by MapView; this reads bundles/materials/counts

        // ── Live state ─────────────────────────────────────────────────────────────────────────────────
        // S83b: per-source pipeline registry (replaces the single _scheduler/_source/_ownsSource). The
        // per-tile lifecycle is per-(tile, source) — see LoadedKey.
        private readonly List<SourcePipeline> _pipelines = new List<SourcePipeline>(4);
        private bool _initialised;

        /// <summary>S83b: the normalized source-id a rendered style layer draws from (null → "" so it is a
        /// valid Dictionary key and matches a pipeline built for an absent <c>source</c>).</summary>
        private static string SourceIdOf(StyleLayer layer) => layer?.Source ?? string.Empty;

        // ── Tile render backend (Entities, BRG, or GameObject) — constructed in Initialise ────
        private Backend.ITileRenderBackend _instanced; // null only before Initialise / after Dispose

        // ── S71: the visible-tile-selection seam (default = ViewportCornerTileSelector; injected by MapView) ─
        // Held as the interface type so a future mixed-zoom (distance-LOD) impl drops in with no change here.
        private IVisibleTileSelector _selector;

        /// <summary>The visible-tile selection algorithm. Set by MapView (default
        /// <see cref="ViewportCornerTileSelector"/>); a different <see cref="IVisibleTileSelector"/> is a
        /// drop-in replacement. The consumer (this class) builds a <see cref="ViewContext"/> per tick and
        /// owns the request/release transition — the seam returns just the set.</summary>
        internal IVisibleTileSelector Selector { get => _selector; set => _selector = value; }

        // Reused buffers — never reallocated in steady state.
        private readonly List<TileId>                      _cover     = new List<TileId>(64);
        private readonly HashSet<TileId>                   _coverSet  = new HashSet<TileId>();
        // S83b: keyed by (tile, source-slot) — one record per (tile, source).
        private readonly Dictionary<LoadedKey, LoadedTile> _loaded    = new Dictionary<LoadedKey, LoadedTile>();
        private readonly List<LoadedKey>                   _toRelease = new List<LoadedKey>(32);

        private bool _coverDirty = true;

        // ── S50/S71: tile-selection key (scalar fields, no boxing) ─────────────────────────────
        // S71: keyed on the FULL camera+viewport inputs the selector reads — fractional zoom, heading, and
        // framing viewport size all change the covered set now (the old key tracked only integer zoom).
        private double _coverKeyLon;
        private double _coverKeyLat;
        private double _coverKeyZoom;
        private double _coverKeyHeading;
        private double _coverKeyViewportX;
        private double _coverKeyViewportY;
        private bool   _coverKeyInitialised;

        // ── S51 test observability: mid-flight release counter ─────────────────────────────────
        // Counts tiles released while their tessellation was still in-flight (HasTessellationTask
        // && !Built). Exposed for tests to prove the race actually occurred. See S51 tooth 5b.
        private int _releasedMidFlightCount;

        // ── S84 test observability: mid-FETCH release counter ──────────────────────────────────────
        // Counts tiles released while their FETCH was still in-flight (!FetchCompleted). Exposed for the
        // S84 test to prove the cancel-mid-fetch race actually occurred (non-vacuous).
        private int _releasedMidFetchCount;

        // ── S55/S87 test-observability: throttle counters (reset at each PumpPendingBuilds entry) ─────────
        private int _tessellationsKickedLastTick;
        private int _verticesConsumedLastTick;
        private int _tilesConsumedLastTick;
        // S87: meshes (tile-layers) uploaded+registered this tick — the per-frame mesh-count budget observable.
        private int _meshesConsumedLastTick;

        // S87: reusable scratch for one ConsumeTessellationTask call's newly-built meshes/handles (main-thread
        // only, not re-entrant). Cleared at the start of each call; merged into the tile's arrays at the end.
        // Reused so a partial consume frame doesn't allocate a fresh list per call.
        private readonly List<Mesh> _consumeScratchMeshes  = new List<Mesh>(8);
        private readonly List<int>  _consumeScratchHandles = new List<int>(8);

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

        // ── S84 mid-flight FETCH holding pen ──────────────────────────────────────────────────────
        // When a tile is released before its fetch completes (rapid zoom/cover churn), the preserved
        // fetch UniTask would otherwise be dropped UNOBSERVED — and a fetch cancelled mid-flight faults
        // (the aborted UnityWebRequest), so UniTask's GC finalizer floods the console with
        // "UnityWebRequestException: Unknown Error". Stash the in-flight fetch here on release; each Tick
        // observes completed ones (and Dispose spins the rest), so every fetch task's outcome is consumed
        // exactly once. Main-thread only, like _pendingDisposal.
        private readonly List<UniTask<TileResponse>> _pendingFetchDisposal = new List<UniTask<TileResponse>>(8);

        // S84: running count of genuine (non-cancellation) fetch errors, for bounded logging.
        private int _fetchErrorCount;

        public TileManager(Style.StyledLayerSet layers)
        {
            _layers = layers;
        }

        // ── Lifecycle / injection ────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates the scheduler over <paramref name="source"/> and resets selection state. If
        /// <paramref name="ownsSource"/> is true, <see cref="Dispose"/> disposes the source.
        ///
        /// Constructs the tile render backend for <paramref name="backend"/>: a <see cref="BRGBackend.TileRenderer"/>
        /// (Brg), a <see cref="Backend.GameObjects.TileRenderer"/> (GameObject), or an <see cref="Backend.Entities.TileRenderer"/>
        /// (Entities, the default), all built from the styled layer set passed at construction.
        /// </summary>
        public void Initialise(IDataSource source, bool ownsSource,
            Map.RenderBackend backend = Map.RenderBackend.Entities)
        {
            // S83b legacy single-source entry: wire EVERY rendered source-id to the one injected source, so
            // a style whose layers all name one source (the common case) builds exactly one pipeline ⇒ N=1 ⇒
            // behaviour identical to the pre-S83b single-source path. The first pipeline owns the shared
            // source (if ownsSource); the rest share the same instance without owning it (no double-dispose).
            // Zoom range is left wide-open (cover is already zoom-clamped by MapView); SetStyle (the S83b
            // entry) supplies per-source resolved minzoom/maxzoom instead.
            DisposePipelines();

            bool first = true;
            foreach (string sid in CollectRenderedSourceIds())
            {
                AddPipeline(sid, source, ownsSource && first, minZoom: 0, maxZoom: int.MaxValue);
                first = false;
            }
            // A style with no rendered fill/line layers builds no pipeline (nothing fetches) — but the
            // manager is still "initialised" and owns the (otherwise unused) source for disposal.
            if (_pipelines.Count == 0 && ownsSource)
                _orphanSource = source;

            _coverDirty          = true;
            _coverKeyInitialised = false;
            _initialised         = true;

            BuildBackend(backend);
        }

        /// <summary>
        /// S83b multi-source entry (the <see cref="Map.View.SetStyle"/> path). Applies <paramref name="specs"/>
        /// — one per rendered source-id, already resolved (inline <c>tiles[]</c> or via S83a TileJSON) — as
        /// the pipeline registry, and (re)builds the backend from the current <see cref="StyledLayerSet"/>.
        ///
        /// <para>Handles BOTH first call and RESTYLE in one pass (decision 7):</para>
        /// <list type="number">
        ///   <item><b>Render-teardown EVERY existing record</b> (destroy meshes, unregister draw items,
        ///     stash in-flight in the holding pens) and clear <c>_loaded</c> — the old records reference the
        ///     old layer indexing + the old backend, so they must go. This does NOT touch any scheduler/cache
        ///     (7b), so a kept source's warm cache survives.</item>
        ///   <item><b>Diff the registry by <see cref="SourceKey"/></b>: a spec whose <c>(SourceId, Key)</c>
        ///     matches an existing pipeline KEEPS that pipeline instance (its source + scheduler + cache, so
        ///     already-fetched bytes are reused — 7a); a new/changed spec builds a fresh pipeline; an existing
        ///     pipeline matched by no spec is pipeline-torn-down (scheduler + owned source disposed).</item>
        ///   <item><b>Rebuild the backend</b> (7c — the material list changed) and re-arm cover selection;
        ///     the next Tick re-requests the cover, hitting kept caches (no re-fetch).</item>
        /// </list>
        /// </summary>
        internal void SetSources(System.Collections.Generic.IReadOnlyList<SourceSpec> specs, Map.RenderBackend backend)
        {
            // 1. Render-teardown every existing record (NO scheduler release — keep warm caches). The
            //    in-flight tasks land in the S48/S84 pens → disposed, never consumed against the new backend
            //    (7d). _orphanSource (a zero-pipeline owned source) is freed by the diff below if dropped.
            foreach (var kv in _loaded)
            {
                var lt = kv.Value; // foreach value is read-only; teardown needs a ref to null its Meshes
                RenderTeardownRecord(ref lt);
            }
            _loaded.Clear();

            // 2. Diff the pipeline registry by (SourceId, resolved Key).
            var kept = new List<SourcePipeline>(specs.Count);
            var keptOld = new HashSet<SourcePipeline>();
            for (int i = 0; i < specs.Count; i++)
            {
                var spec = specs[i];
                SourcePipeline existing = FindPipeline(spec.SourceId, spec.Key);
                if (existing != null)
                {
                    existing.MinZoom = spec.MinZoom; // zoom may be re-read from a re-resolved def; identity kept
                    existing.MaxZoom = spec.MaxZoom;
                    kept.Add(existing);
                    keptOld.Add(existing);
                }
                else
                {
                    var cache  = new TileCache(capacity: 256);
                    var source = spec.CreateSource();
                    kept.Add(new SourcePipeline
                    {
                        SourceId = spec.SourceId, Source = source, OwnsSource = true,
                        Scheduler = new TileScheduler(source, cache), Cache = cache,
                        MinZoom = spec.MinZoom, MaxZoom = spec.MaxZoom, DefKey = spec.Key,
                    });
                }
            }

            // Pipeline-teardown the ones no spec kept (removed sources): dispose scheduler + owned source.
            for (int i = 0; i < _pipelines.Count; i++)
            {
                var p = _pipelines[i];
                if (keptOld.Contains(p)) continue;
                p.Scheduler?.Dispose();
                if (p.OwnsSource) p.Source?.Dispose();
            }
            _orphanSource?.Dispose();
            _orphanSource = null;

            // Commit the new registry with stable slots 0..N-1.
            _pipelines.Clear();
            for (int i = 0; i < kept.Count; i++) { kept[i].Slot = i; _pipelines.Add(kept[i]); }

            // 3. Rebuild the backend from the (caller-rebuilt) styled layer set; re-arm cover selection.
            _coverDirty          = true;
            _coverKeyInitialised = false;
            _initialised         = true;
            BuildBackend(backend);
        }

        /// <summary>S83b: the existing pipeline with this source-id AND matching resolved key, or null.</summary>
        private SourcePipeline FindPipeline(string sourceId, SourceKey key)
        {
            for (int i = 0; i < _pipelines.Count; i++)
                if (_pipelines[i].SourceId == sourceId && _pipelines[i].DefKey.Equals(key))
                    return _pipelines[i];
            return null;
        }

        /// <summary>Constructs the tile render backend from the styled layer set (default arm Entities so a
        /// legacy serialized value resolves safely). Disposes any prior backend first (restyle / re-init).</summary>
        private void BuildBackend(Map.RenderBackend backend)
        {
            _instanced?.Dispose();
            _instanced = backend switch
            {
                Map.RenderBackend.Brg        => new BRGBackend.TileRenderer(_layers),
                Map.RenderBackend.GameObject => new GOBackend.TileRenderer(FlattenLayerMaterials(_layers), FlattenLayerNames(_layers)),
                _                            => new EntBackend.TileRenderer(FlattenLayerMaterials(_layers), FlattenLayerNames(_layers)),
            };
        }

        // S83b: an owned source for a style with zero rendered pipelines — held only so Dispose frees it.
        private IDataSource _orphanSource;

        /// <summary>S83b: the distinct rendered (fill/line) source-ids, in first-seen declared order — the
        /// set of pipelines a style needs. Background/raster/symbol layers never reach
        /// <see cref="StyledLayerSet"/>, so this enumerates only vector-rendered sources.</summary>
        private List<string> CollectRenderedSourceIds()
        {
            var ids = new List<string>(4);
            for (int i = 0; i < _layers.FillCount; i++) AddDistinct(ids, SourceIdOf(_layers.Fills[i].StyleLayer));
            for (int i = 0; i < _layers.LineCount; i++) AddDistinct(ids, SourceIdOf(_layers.Lines[i].StyleLayer));
            return ids;

            static void AddDistinct(List<string> list, string id)
            {
                if (!list.Contains(id)) list.Add(id);
            }
        }

        /// <summary>S83b: appends a pipeline (its own scheduler + cache) for <paramref name="sourceId"/>.</summary>
        private void AddPipeline(string sourceId, IDataSource source, bool ownsSource, int minZoom, int maxZoom)
        {
            var cache = new TileCache(capacity: 256);
            _pipelines.Add(new SourcePipeline
            {
                SourceId   = sourceId,
                Slot       = _pipelines.Count,
                Source     = source,
                OwnsSource = ownsSource,
                Scheduler  = new TileScheduler(source, cache),
                Cache      = cache,
                MinZoom    = minZoom,
                MaxZoom    = maxZoom,
            });
        }

        /// <summary>Flattens the styled layer set's materials (fills in declared order, then lines) — the
        /// material list every backend indexes by <c>materialIndex</c>.</summary>
        private static System.Collections.Generic.List<Material> FlattenLayerMaterials(Style.StyledLayerSet layers)
        {
            var mats = new System.Collections.Generic.List<Material>(layers.FillCount + layers.LineCount);
            for (int i = 0; i < layers.FillCount; i++) mats.Add(layers.Fills[i].Material);
            for (int i = 0; i < layers.LineCount; i++) mats.Add(layers.Lines[i].Material);
            return mats;
        }

        /// <summary>Flattens the per-layer style ids in the same (fills then lines) order as
        /// <see cref="FlattenLayerMaterials"/>, so the Entities backend can name each layer entity after its
        /// style layer (e.g. "water") in the Entities Hierarchy instead of the shared material name.</summary>
        private static System.Collections.Generic.List<string> FlattenLayerNames(Style.StyledLayerSet layers)
        {
            var names = new System.Collections.Generic.List<string>(layers.FillCount + layers.LineCount);
            for (int i = 0; i < layers.FillCount; i++) names.Add(layers.Fills[i].StyleLayer?.Id);
            for (int i = 0; i < layers.LineCount; i++) names.Add(layers.Lines[i].StyleLayer?.Id);
            return names;
        }

        /// <summary>True once <see cref="Initialise"/> has been called successfully.</summary>
        public bool IsInitialised => _initialised;

        // ── Test observability (internal; surfaced to tests through MapViewTestExtensions, not the
        //    production API). TileManager is already an internal type, so these stay close to the state
        //    they read; MapView no longer mirrors them. ───────────────────────────────────────────────

        /// <summary>The in-flight fetch count summed across every source pipeline.</summary>
        internal int InFlightCount
        {
            get { int n = 0; for (int i = 0; i < _pipelines.Count; i++) n += _pipelines[i].Scheduler.InFlightCount; return n; }
        }

        /// <summary>S83b: number of currently loaded (or loading) <c>(tile, source)</c> RECORDS — what the
        /// per-frame loops iterate. With a single source (N=1) this equals the distinct tile count, so every
        /// existing assertion is preserved.</summary>
        internal int LoadedTileCount => _loaded.Count;

        /// <summary>
        /// Number of tiles released while their tessellation was still in-flight (HasTessellationTask
        /// and not yet Built at the moment of release). Incremented by ReleaseTile. Read by tests
        /// to prove the mid-flight race actually occurred in <c>S51DisposalLeakGuardTests</c>.
        /// </summary>
        internal int ReleasedMidFlightCount => _releasedMidFlightCount;

        /// <summary>S84: number of tiles released while their fetch was still in-flight.</summary>
        internal int ReleasedMidFetchCount => _releasedMidFetchCount;

        /// <summary>S55: tessellation kicks issued in the most recent PumpPendingBuilds call.
        /// Exposed for tests; never call from production code.</summary>
        internal int TessellationsKickedLastTick => _tessellationsKickedLastTick;
        /// <summary>S55/S87: sum of layer-mesh vertex counts consumed in the most recent PumpPendingBuilds call.</summary>
        internal int VerticesConsumedLastTick => _verticesConsumedLastTick;
        /// <summary>S55/S87: number of tiles that reached <c>Built</c> (fully consumed) in the most recent
        /// PumpPendingBuilds call. With S87's per-mesh consume a tile may take several ticks to complete.</summary>
        internal int TilesConsumedLastTick => _tilesConsumedLastTick;
        /// <summary>S87: number of layer MESHES uploaded + registered in the most recent PumpPendingBuilds call —
        /// the per-frame mesh-count budget observable (bounds AddLayer / entity-add and GPU upload per frame).</summary>
        internal int MeshesConsumedLastTick => _meshesConsumedLastTick;

        /// <summary>The live BRG renderer, or null when not on the BRG backend / before <see cref="Initialise"/>.</summary>
        internal BRGBackend.TileRenderer BrgRenderer => _instanced as BRGBackend.TileRenderer;

        /// <summary>The live Entities-Graphics renderer, or null when not on the Entities backend / before <see cref="Initialise"/>.</summary>
        internal EntBackend.TileRenderer EntitiesRenderer => _instanced as EntBackend.TileRenderer;

        /// <summary>The live GameObject renderer, or null when not on the GameObject backend / before <see cref="Initialise"/>.</summary>
        internal GOBackend.TileRenderer GameObjectRenderer => _instanced as GOBackend.TileRenderer;

        /// <summary>
        /// Invalidates the cached cover-selection key so the next <see cref="Tick"/> re-selects the cover.
        /// Called when the camera is re-wired (<see cref="Map.View.SetCamera"/>).
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
        /// <summary>S83b: true ⟺ the tile has ≥1 source-record, ALL its records are <c>Built</c>, and the
        /// union produced geometry. N=1 ⇒ identical to the old single-record (built + has-geometry) check.</summary>
        internal bool TryGetBuiltTile(TileId id)
        {
            bool any = false, anyGeom = false;
            foreach (var kv in _loaded)
            {
                if (!kv.Key.Tile.Equals(id)) continue;
                any = true;
                if (!kv.Value.Built) return false;
                if (kv.Value.DrawHandles != null || kv.Value.Meshes != null) anyGeom = true;
            }
            return any && anyGeom;
        }

        /// <summary>
        /// Test-only, backend-agnostic: the <see cref="Mesh"/> assets built for a loaded tile — the UNION
        /// across its source-records (fills-then-lines per record, in pipeline-slot order), or null if the
        /// tile produced no geometry. N=1 ⇒ the single record's meshes. Replaces the old "inspect the tile's
        /// child GameObjects" probe.
        /// </summary>
        internal Mesh[] GetTileMeshes(TileId id)
        {
            List<Mesh> all = null;
            for (int s = 0; s < _pipelines.Count; s++)
            {
                if (_loaded.TryGetValue(new LoadedKey(id, _pipelines[s].Slot), out var lt) && lt.Meshes != null)
                {
                    all ??= new List<Mesh>(8);
                    all.AddRange(lt.Meshes);
                }
            }
            return all?.ToArray();
        }

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
            if (!_initialised || _selector == null) return;

            if (!_coverKeyInitialised ||
                cam.LookAt.Longitude    != _coverKeyLon ||
                cam.LookAt.Latitude     != _coverKeyLat ||
                cam.Zoom                != _coverKeyZoom ||
                cam.Heading.Degrees     != _coverKeyHeading ||
                cfg.FramingViewportPx.x != _coverKeyViewportX ||
                cfg.FramingViewportPx.y != _coverKeyViewportY)
            {
                _coverDirty = true;
            }

            // S48: drain any completed mid-flight-discard tasks so their NativeArrays are freed.
            DrainPendingDisposal();
            // S84: observe any completed mid-flight-released fetch tasks (no unobserved-exception flood).
            DrainPendingFetchDisposal();

            int pending = PumpPendingBuilds(cam, cfg.MaxBuildsPerTick, cfg.MaxTessellationsPerTick, cfg.MaxVerticesPerTick);

            if (!_coverDirty && pending == 0)
                return;

            using var sCoverSel = PmCoverSelect.Auto();

            // S71: select through the seam. Build the per-frame view context (camera + framing viewport +
            // active projection); the request/release transition below is unchanged (instant swap).
            ViewContext view = new ViewContext
            {
                Camera     = cam,
                ViewportPx = cfg.FramingViewportPx,
                Projection = cfg.Projection,
            };
            _selector.SelectVisibleTiles(in view, _cover);

            _coverSet.Clear();
            for (int i = 0; i < _cover.Count; i++)
                _coverSet.Add(_cover[i]);

            // Request tiles newly entering the cover — one record per (tile, source pipeline) whose resolved
            // zoom range admits the tile (decision 10: one camera-driven cover, per-pipeline zoom clamp).
            for (int i = 0; i < _cover.Count; i++)
            {
                TileId id = _cover[i];
                for (int s = 0; s < _pipelines.Count; s++)
                {
                    var p = _pipelines[s];
                    if (id.Z < p.MinZoom || id.Z > p.MaxZoom) continue; // source doesn't serve this zoom
                    var key = new LoadedKey(id, p.Slot);
                    if (!_loaded.ContainsKey(key))
                    {
                        UniTask<TileResponse> fetchReq;
                        {
                            using var sSchedReq = PmSchedulerReq.Auto();
                            // .Preserve() allows polling .IsCompleted across frames without exhausting the UniTask.
                            fetchReq = p.Scheduler.Request(id).Preserve();
                        }
                        _loaded[key] = new LoadedTile
                        {
                            Request        = fetchReq,
                            Built          = false,
                            TileOriginMerc = FloatingOrigin.TileLocalOriginMercator(id),
                        };
                    }
                }
            }

            // Release records whose tile left the cover (route each to its OWNING pipeline's scheduler).
            _toRelease.Clear();
            foreach (var kv in _loaded)
                if (!_coverSet.Contains(kv.Key.Tile))
                    _toRelease.Add(kv.Key);
            for (int i = 0; i < _toRelease.Count; i++)
                ReleaseTile(_toRelease[i]);

            _coverKeyLon         = cam.LookAt.Longitude;
            _coverKeyLat         = cam.LookAt.Latitude;
            _coverKeyZoom        = cam.Zoom;
            _coverKeyHeading     = cam.Heading.Degrees;
            _coverKeyViewportX   = cfg.FramingViewportPx.x;
            _coverKeyViewportY   = cfg.FramingViewportPx.y;
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
            // Collect all unsettled records.
            var unsettled = new List<LoadedKey>(8);
            foreach (var kv in _loaded)
                if (!kv.Value.Built)
                    unsettled.Add(kv.Key);

            foreach (var key in unsettled)
            {
                TileId id       = key.Tile;
                string sourceId = _pipelines[key.Slot].SourceId;
                LoadedTile lt   = _loaded[key];

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

                    // S84: observe the fetch outcome exactly once (Succeeded / Faulted / Canceled) so a
                    // faulted fetch is never dropped unobserved.
                    TileResponse resp = ObserveFetchOutcome(req, logErrors: true);
                    if (resp.HasData && resp.Bytes != null)
                    {
                        // Kick tessellation synchronously (wait inline).
                        var tessTask = KickTessellationTask(lt, id, resp.Bytes, cam, sourceId);
                        lt.HasTessellationTask = true;
                        lt.TessellationTask    = tessTask;
                    }
                    else
                    {
                        // Absent/failed/cancelled fetch — nothing to tessellate.
                        lt.Built = true;
                        _loaded[key] = lt;
                        continue;
                    }
                }

                // (a2) S55: fetch completed with data but tessellation not yet kicked (cap-deferred in
                // normal pump). Kick inline here — drain ignores per-tick caps.
                if (lt.FetchCompleted && lt.ReadyBytes != null && !lt.HasTessellationTask)
                {
                    lt.TessellationTask    = KickTessellationTask(lt, id, lt.ReadyBytes, cam, sourceId);
                    lt.HasTessellationTask = true;
                    lt.ReadyBytes          = null;
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
                    // Drain ignores per-frame caps: unbounded budget consumes ALL layers in one call → Built.
                    ConsumeTessellationTask(id, ref lt, int.MaxValue, int.MaxValue, out _, out _);
                }
                else
                {
                    lt.Built = true;
                }

                _loaded[key] = lt;
            }
        }

        /// <summary>
        /// S47/S51/S55 pump: two-phase pipeline per tile.
        ///
        /// Phase 1 (fetch→kick-tessellation): for tiles whose fetch completed, store bytes and kick a
        /// background tessellation UniTask (S55: at most <paramref name="maxTessellationsPerTick"/> kicks
        /// per Tick — bytes are retained in <see cref="LoadedTile.ReadyBytes"/> until the cap allows).
        ///
        /// Phase 2 (tessellate→consume): for tiles whose tessellation UniTask is completed, consume the
        /// result on the main thread (UploadMesh → backend registration) MESH-by-mesh (S87). The per-frame
        /// budget is dual — at most <paramref name="maxBuildsPerTick"/> layer MESHES AND
        /// <paramref name="maxVerticesPerTick"/> vertices per frame, whichever binds first; a tile whose
        /// layers exceed the remaining budget is consumed partially and RESUMES (via its ConsumeCursor) on
        /// later Ticks — a single rich tile never lands in one frame.
        ///
        /// Returns the count of tiles still pending (fetch or tessellation in-flight, or cap-deferred).
        ///
        /// Greppability note: there is NO .Schedule().Complete() in this method.
        /// </summary>
        private int PumpPendingBuilds(
            CameraProperties cam,
            int maxBuildsPerTick,
            int maxTessellationsPerTick,
            int maxVerticesPerTick)
        {
            using var sFetchPoll = PmFetchPoll.Auto();

            // Reset per-tick observability counters.
            _tessellationsKickedLastTick = 0;
            _verticesConsumedLastTick    = 0;
            _tilesConsumedLastTick       = 0;
            _meshesConsumedLastTick      = 0;

            // S55: treat 0 as uncapped for the kick + vertex caps (unset config field → harmless default).
            int tessCap  = maxTessellationsPerTick > 0 ? maxTessellationsPerTick : int.MaxValue;
            int vertsCap = maxVerticesPerTick      > 0 ? maxVerticesPerTick      : int.MaxValue;
            // S87: the per-frame MESH-count cap is used directly — 0 BLOCKS consume (tests build a backlog
            // that way); set it high (e.g. 64) for effectively-uncapped. The asymmetry with the vertex cap
            // (0 = uncapped) is intentional and documented on TileSelectionConfig.MaxBuildsPerTick.
            int meshCap  = maxBuildsPerTick;

            _toRelease.Clear();
            foreach (var kv in _loaded)
                if (!kv.Value.Built)
                    _toRelease.Add(kv.Key);

            int pending          = 0;
            int tessKicked       = 0;
            int meshesConsumed   = 0;
            int verticesConsumed = 0;

            for (int i = 0; i < _toRelease.Count; i++)
            {
                LoadedKey  key      = _toRelease[i];
                TileId     id       = key.Tile;
                string     sourceId = _pipelines[key.Slot].SourceId;
                LoadedTile lt       = _loaded[key];

                // ── Phase 2 (S87): consume a completed tessellation MESH-by-mesh under the dual budget ──
                if (lt.FetchCompleted && lt.HasTessellationTask && lt.TessellationTask.Status.IsCompleted())
                {
                    int meshBudgetLeft = meshCap  - meshesConsumed;
                    int vertBudgetLeft = vertsCap - verticesConsumed;
                    // Budget exhausted this frame (or meshCap == 0 → blocked): defer the rest to the next Tick.
                    // The tile keeps its ConsumeCursor; pending++ keeps Tick pumping until it drains.
                    if (meshBudgetLeft <= 0 || vertBudgetLeft <= 0)
                    {
                        pending++;
                        continue;
                    }

                    bool complete = ConsumeTessellationTask(
                        id, ref lt, meshBudgetLeft, vertBudgetLeft,
                        out int meshesThisCall, out int vertsThisCall);

                    meshesConsumed            += meshesThisCall;
                    verticesConsumed          += vertsThisCall;
                    _meshesConsumedLastTick    = meshesConsumed;
                    _verticesConsumedLastTick  = verticesConsumed;
                    if (complete) _tilesConsumedLastTick++;
                    else          pending++;   // tile partially consumed — resume next Tick
                    _loaded[key] = lt;
                    continue;
                }

                // ── Tessellation in-flight ─────────────────────────────────────────────────────
                if (lt.FetchCompleted && lt.HasTessellationTask)
                {
                    pending++;
                    continue;
                }

                // ── Phase 1: kick tessellation from ReadyBytes ─────────────────────────────────
                if (lt.FetchCompleted && lt.ReadyBytes != null)
                {
                    if (tessKicked >= tessCap)
                    {
                        // Cap reached this tick — bytes retained for next Tick.
                        pending++;
                        continue;
                    }
                    lt.TessellationTask    = KickTessellationTask(lt, id, lt.ReadyBytes, cam, sourceId);
                    lt.HasTessellationTask = true;
                    lt.ReadyBytes          = null;
                    tessKicked++;
                    _tessellationsKickedLastTick = tessKicked;
                    pending++; // tessellation now in-flight
                    _loaded[key] = lt;
                    continue;
                }

                // ── Fetch in-flight ────────────────────────────────────────────────────────────
                if (!lt.Request.Status.IsCompleted())
                {
                    pending++;
                    continue;
                }

                // ── Observe completed fetch ────────────────────────────────────────────────────
                // S84: observe the fetch outcome exactly once (handles Succeeded / Faulted / Canceled) so
                // a faulted fetch is never left for UniTask's unobserved-exception finalizer.
                lt.FetchCompleted = true;
                TileResponse resp = ObserveFetchOutcome(lt.Request, logErrors: true);
                if (resp.HasData && resp.Bytes != null)
                {
                    // Store bytes; kick deferred to a subsequent Tick (capped by tessCap).
                    lt.ReadyBytes = resp.Bytes;
                    pending++; // bytes awaiting kick
                }
                else
                {
                    // Absent / failed / cancelled — mark built (nothing to render).
                    lt.Built = true;
                }
                _loaded[key] = lt;
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
            LoadedTile lt, TileId id, byte[] mvtBytes, CameraProperties cam, string sourceId)
        {
            var layerRecordsSnapshot     = _layers.SnapshotFills();
            var lineRecordsSnapshot      = _layers.SnapshotLines();
            double zoom          = cam.Zoom;
            double2 tileOrigin   = lt.TileOriginMerc;

            return UniTask.RunOnThreadPool(() =>
            {
                MvtTile mvtTile = MvtDecoder.Decode(mvtBytes);

                // ── Fill layers ────────────────────────────────────────────────
                // S83b: the result stays FULL-WIDTH (every fill slot), but THIS source's task populates only
                // the layers bound to THIS source-id; layers of other sources get an empty LayerMeshData
                // (identical to the no-features path). So the global materialIndex is preserved and the union
                // across source-records covers all layers — see decision 5c.
                var layerData = new Meshing.StyledFillTileBuilder.LayerMeshData[layerRecordsSnapshot.Length];

                for (int li = 0; li < layerRecordsSnapshot.Length; li++)
                {
                    var rec = layerRecordsSnapshot[li];

                    var features = (SourceIdOf(rec.StyleLayer) != sourceId)
                        ? (System.Collections.Generic.IReadOnlyList<MvtFeature>)System.Array.Empty<MvtFeature>()
                        : MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(rec.StyleLayer, mvtTile, zoom);
                    if (features.Count == 0)
                    {
                        layerData[li] = new Meshing.StyledFillTileBuilder.LayerMeshData
                        {
                            Features = new System.Collections.Generic.List<Meshing.StyledFillTileBuilder.FeatureMeshData>()
                        };
                        continue;
                    }

                    MvtLayer mvtLayer = MapRenderer.Core.Style.SourceLayerResolver.ResolveMvtLayer(
                        rec.StyleLayer, mvtTile);
                    if (mvtLayer == null)
                    {
                        layerData[li] = new Meshing.StyledFillTileBuilder.LayerMeshData
                        {
                            Features = new System.Collections.Generic.List<Meshing.StyledFillTileBuilder.FeatureMeshData>()
                        };
                        continue;
                    }

                    layerData[li] = Meshing.StyledFillTileBuilder.BuildMeshData(
                        features, rec.Paint, zoom, mvtLayer.Extent, id, tileOrigin);
                }

                // ── S14: Line layers ───────────────────────────────────────────
                // Feature selection routes through SourceLayerResolver seam (tooth #6 — same as fills).
                var lineLayerData = new Meshing.StyledLineTileBuilder.LayerMeshData[lineRecordsSnapshot.Length];

                for (int li = 0; li < lineRecordsSnapshot.Length; li++)
                {
                    var rec = lineRecordsSnapshot[li];

                    // S83b: skip layers of other sources (empty slot → default LayerMeshData, IsCreated=false).
                    if (SourceIdOf(rec.StyleLayer) != sourceId)
                        continue;

                    var features = MapRenderer.Core.Filters.FeatureSelector.SelectFeatures(
                        rec.StyleLayer, mvtTile, zoom);
                    if (features.Count == 0)
                        continue; // IsCreated=false, no dispose needed

                    MvtLayer mvtLayer = MapRenderer.Core.Style.SourceLayerResolver.ResolveMvtLayer(
                        rec.StyleLayer, mvtTile);
                    if (mvtLayer == null)
                        continue;

                    lineLayerData[li] = Meshing.StyledLineTileBuilder.BuildMeshData(
                        features, rec.Paint, rec.Layout, zoom, mvtLayer.Extent, id, tileOrigin);
                }

                return new TessellationResult { LayerData = layerData, LineLayerData = lineLayerData };
            }, configureAwait: false).Preserve(); // .Preserve() allows polling .IsCompleted across multiple frames
        }

        /// <summary>
        /// S87 resumable per-MESH consume. Uploads + registers tile-layer meshes starting at
        /// <see cref="LoadedTile.ConsumeCursor"/> (fill <c>[0..FillCount)</c> then line <c>[0..LineCount)</c>),
        /// consuming layers until a budget binds (<paramref name="meshBudget"/> meshes OR
        /// <paramref name="vertBudget"/> vertices — checked before each layer, so at most one-mesh overshoot;
        /// a single mesh cannot be split) or the tile is fully consumed. Each consumed layer's NativeArrays
        /// are disposed immediately after upload; on completion the whole result is disposed (idempotent) to
        /// also free any layers the active style does not render. Returns true when the tile is now fully
        /// consumed (<see cref="LoadedTile.Built"/>); false when partially consumed (resume next Tick).
        ///
        /// Must be called on the Unity main thread, only when TessellationTask.IsCompleted, with positive
        /// budget (the pump guards budget &lt;= 0). A partially-consumed tile keeps HasTessellationTask = true
        /// and !Built; its already-built meshes/handles live in lt.Meshes/lt.DrawHandles, the remaining layers
        /// stay alive in the Preserved task (re-fetched each call). Eviction's holding pen disposes the
        /// remainder (idempotent — already-consumed layers are no-ops).
        ///
        /// Mid-flight release: ReleaseTile removes the tile from _loaded, so this is never called for a
        /// released tile — the real discard protection is the _loaded-removal in ReleaseTile.
        /// </summary>
        private bool ConsumeTessellationTask(
            TileId id, ref LoadedTile lt, int meshBudget, int vertBudget,
            out int meshesConsumed, out int vertsConsumed)
        {
            meshesConsumed = 0;
            vertsConsumed  = 0;

            var task = lt.TessellationTask;

            // Faulted or cancelled — nothing to render; complete immediately.
            if (task.Status != UniTaskStatus.Succeeded)
            {
                FinishConsume(ref lt);
                return true;
            }

            // .GetResult() is Preserve-safe and re-callable across frames (the pump calls this only when
            // IsCompleted is true). Per-layer NativeArrays are disposed as each layer is consumed.
            TessellationResult result = task.GetAwaiter().GetResult();

            int fillCount   = (result.LayerData     != null) ? math.min(_layers.FillCount, result.LayerData.Length)     : 0;
            int lineCount   = (result.LineLayerData != null) ? math.min(_layers.LineCount, result.LineLayerData.Length) : 0;
            int totalLayers = fillCount + lineCount;

            _consumeScratchMeshes.Clear();
            _consumeScratchHandles.Clear();

            // Consume from the cursor until a budget binds or all layers are done. Skipped (empty-geometry)
            // layers are free — they only advance the cursor. The pump guarantees positive budget, so at
            // least one layer is processed per call → progress is guaranteed (a lone huge mesh consumes in
            // one go, one-mesh overshoot).
            int cursor = lt.ConsumeCursor;
            while (cursor < totalLayers && meshesConsumed < meshBudget && vertsConsumed < vertBudget)
            {
                Mesh mesh;
                int  materialIndex;
                int  layerVerts;
                if (cursor < fillCount)
                {
                    int li     = cursor;
                    layerVerts = result.LayerData[li].VertexCount;
                    using (PmMeshUpload.Auto())
                        mesh = Meshing.StyledFillTileBuilder.UploadMesh(result.LayerData[li]);
                    result.LayerData[li].Dispose();              // consumed — free its NativeArrays now
                    materialIndex = li;
                }
                else
                {
                    int li     = cursor - fillCount;             // 0-based line layer index
                    layerVerts = result.LineLayerData[li].VertexCount;
                    using (PmMeshUpload.Auto())
                        mesh = Meshing.StyledLineTileBuilder.UploadMesh(result.LineLayerData[li]);
                    result.LineLayerData[li].Dispose();
                    materialIndex = _layers.FillCount + li;      // flattened material order: fills then lines
                }
                cursor++;

                if (mesh == null) continue; // empty layer — disposed above, no AddLayer, no budget charge

                int handle;
                using (PmAddTileLayer.Auto())
                    handle = _instanced.AddTileLayer(mesh, lt.TileOriginMerc, materialIndex, id);
                _consumeScratchMeshes.Add(mesh);                 // S51: track for explicit destruction
                _consumeScratchHandles.Add(handle);
                meshesConsumed++;
                vertsConsumed += layerVerts;
            }

            lt.ConsumeCursor = cursor;

            // Append this call's new meshes/handles to the tile's arrays (one realloc per partial frame; load
            // time only — steady state never re-enters consume, so no per-frame GC there).
            AppendMeshes(ref lt.Meshes, _consumeScratchMeshes);
            AppendHandles(ref lt.DrawHandles, _consumeScratchHandles);

            bool complete = cursor >= totalLayers;
            if (complete)
            {
                // Dispose the whole result (idempotent) — also frees any layers the active style does not
                // render (beyond fillCount/lineCount) — then mark Built and release the task.
                DisposeWholeResult(result);
                FinishConsume(ref lt);
            }
            return complete;
        }

        /// <summary>S87: marks a tile fully consumed — clears the tessellation task and sets Built.</summary>
        private static void FinishConsume(ref LoadedTile lt)
        {
            lt.HasTessellationTask = false;
            lt.TessellationTask    = default;
            lt.Built               = true;
        }

        /// <summary>S87: appends freshly-built meshes to a tile's tracked-Mesh array (grows by realloc).</summary>
        private static void AppendMeshes(ref Mesh[] arr, List<Mesh> add)
        {
            if (add.Count == 0) return;
            int oldLen = arr?.Length ?? 0;
            var merged = new Mesh[oldLen + add.Count];
            if (arr != null) System.Array.Copy(arr, merged, oldLen);
            for (int k = 0; k < add.Count; k++) merged[oldLen + k] = add[k];
            arr = merged;
        }

        /// <summary>S87: appends freshly-registered draw handles to a tile's handle array (grows by realloc).</summary>
        private static void AppendHandles(ref int[] arr, List<int> add)
        {
            if (add.Count == 0) return;
            int oldLen = arr?.Length ?? 0;
            var merged = new int[oldLen + add.Count];
            if (arr != null) System.Array.Copy(arr, merged, oldLen);
            for (int k = 0; k < add.Count; k++) merged[oldLen + k] = add[k];
            arr = merged;
        }

        /// <summary>S48/S87: disposes every LayerMeshData NativeArray in a result (idempotent — IsCreated
        /// guard, so layers already disposed during a partial consume are safe no-ops).</summary>
        private static void DisposeWholeResult(TessellationResult result)
        {
            if (result.LayerData != null)
                for (int li = 0; li < result.LayerData.Length; li++)
                    result.LayerData[li].Dispose();
            if (result.LineLayerData != null)
                for (int li = 0; li < result.LineLayerData.Length; li++)
                    result.LineLayerData[li].Dispose();
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
        private void ReleaseTile(LoadedKey key)
        {
            if (_loaded.TryGetValue(key, out var lt))
            {
                RenderTeardownRecord(ref lt);
                _loaded.Remove(key);
            }
            // Route the scheduler release to the OWNING pipeline — a record on source B never touches A.
            _pipelines[key.Slot].Scheduler.Release(key.Tile);
        }

        /// <summary>
        /// S83b: tears down a record's RENDER state — destroys its Mesh assets, unregisters its backend
        /// draw items, and stashes any in-flight fetch/tessellation in the S48/S84 holding pens (so their
        /// payloads are disposed, never consumed). Does <b>NOT</b> touch the scheduler/cache: that is the
        /// caller's choice — <see cref="ReleaseTile"/> follows with <c>Scheduler.Release</c> (evicts the
        /// cache); restyle (decision 7b) calls this ALONE for a kept source so its cached bytes survive.
        /// The caller removes the key from <see cref="_loaded"/>.
        /// </summary>
        private void RenderTeardownRecord(ref LoadedTile lt)
        {
            // S84: if the FETCH is still in-flight, stash its preserved UniTask so it is observed when it
            // completes. Otherwise the dropped task faults unobserved → UnityWebRequestException console flood.
            if (!lt.FetchCompleted)
            {
                _releasedMidFetchCount++;
                _pendingFetchDisposal.Add(lt.Request);
            }

            // A record with an unconsumed-or-partially-consumed tessellation must have its result's
            // NativeArrays disposed — stash in the S48 holding pen (idempotent dispose makes a partial
            // record's already-consumed layers safe no-ops). Genuine mid-flight (task still running) is
            // counted; an S87 partial (task complete, cursor mid-way) is stashed but not counted.
            if (lt.HasTessellationTask && !lt.Built)
            {
                if (!lt.TessellationTask.Status.IsCompleted())
                    _releasedMidFlightCount++;
                _pendingDisposal.Add(lt.TessellationTask);
            }

            // Unregister the instanced draw items before destroying Mesh assets (RemoveItem drops the draw
            // item — on Entities its layer entity + tile root once empty; the Mesh is freed just after).
            if (_instanced != null && lt.DrawHandles != null)
            {
                for (int hi = 0; hi < lt.DrawHandles.Length; hi++)
                    _instanced.RemoveItem(lt.DrawHandles[hi]);
            }

            // S51 leak guard: destroy tracked Mesh assets explicitly (Unity does not free a Mesh asset just
            // because nothing references it). lt.Meshes holds direct references.
            DestroyTrackedMeshes(ref lt);
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
        /// S84: Observes a COMPLETED fetch task's terminal outcome exactly once and returns its response
        /// (default / no-data on cancel or fault). This is the single place a fetch <see cref="UniTask{T}"/>
        /// result is consumed, so a faulted/cancelled fetch is never left for UniTask's unobserved-exception
        /// finalizer (the console-flood bug). A tile released mid-fetch cancels its token, surfacing as
        /// <see cref="System.OperationCanceledException"/> (mapped from the aborted request by
        /// <c>UnityWebRequestDataSource</c>) — benign, swallowed silently. A genuine error (5xx / connection)
        /// is surfaced only when <paramref name="logErrors"/> is set (the still-wanted path) and is bounded.
        /// Precondition: <c>req.Status.IsCompleted()</c>; safe because <c>lt.Request</c> is <c>.Preserve()</c>d.
        /// </summary>
        private TileResponse ObserveFetchOutcome(UniTask<TileResponse> req, bool logErrors)
        {
            try
            {
                return req.GetAwaiter().GetResult();
            }
            catch (System.OperationCanceledException)
            {
                return default; // tile released mid-fetch — benign cancellation
            }
            catch (System.Exception ex)
            {
                if (logErrors) LogFetchErrorThrottled(ex);
                return default;
            }
        }

        /// <summary>
        /// S84: bounded fetch-error logging. Chaotic input can fail many still-wanted tiles at once, so a
        /// per-tile-per-frame warning would just relocate the flood. Surface the first error and then a
        /// periodic count, so a genuinely broken endpoint stays visible without spamming the console.
        /// </summary>
        private void LogFetchErrorThrottled(System.Exception ex)
        {
            _fetchErrorCount++;
            if (_fetchErrorCount == 1 || (_fetchErrorCount & 63) == 0)
                Debug.LogWarning($"[TileManager] tile fetch failed ({_fetchErrorCount} total): {ex.Message}");
        }

        /// <summary>
        /// S84: Drains completed tasks from the mid-flight FETCH holding pen, observing (and discarding)
        /// each outcome silently — these are released tiles we no longer want. Non-blocking poll; called
        /// once per Tick (mirrors <see cref="DrainPendingDisposal"/>).
        /// </summary>
        private void DrainPendingFetchDisposal()
        {
            if (_pendingFetchDisposal.Count == 0) return;

            for (int i = _pendingFetchDisposal.Count - 1; i >= 0; i--)
            {
                if (!_pendingFetchDisposal[i].Status.IsCompleted())
                    continue; // still in-flight; check again next Tick

                ObserveFetchOutcome(_pendingFetchDisposal[i], logErrors: false); // released → swallow silently
                _pendingFetchDisposal.RemoveAt(i);
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
            if (!_initialised) return; // already torn down (idempotent guard)

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

            // S84: cancel + observe any FETCH still in-flight at teardown — loaded tiles whose fetch
            // hasn't completed, plus the mid-flight-released holding pen — so no fetch task is dropped
            // unobserved (UnityWebRequestException flood on the abort). CANCEL FIRST via _scheduler.Release
            // so the in-flight request faults/cancels promptly; otherwise the spin below would wait on a
            // request that only the (later) _scheduler.Dispose would cancel. (Release does not touch
            // _loaded, so iterating it here is safe.)
            foreach (var kv in _loaded)
            {
                if (kv.Value.FetchCompleted) continue;
                _pipelines[kv.Key.Slot].Scheduler.Release(kv.Key.Tile);
                var fetchTask = kv.Value.Request;
                int spins = 0;
                while (!fetchTask.Status.IsCompleted() && spins++ < 10000)
                    Thread.Sleep(1);
                ObserveFetchOutcome(fetchTask, logErrors: false);
            }
            for (int i = 0; i < _pendingFetchDisposal.Count; i++)
            {
                var task = _pendingFetchDisposal[i];
                int spins = 0;
                while (!task.Status.IsCompleted() && spins++ < 10000)
                    Thread.Sleep(1);
                ObserveFetchOutcome(task, logErrors: false);
            }
            _pendingFetchDisposal.Clear();

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

            // S83b: dispose every source pipeline (each scheduler + its owned source).
            DisposePipelines();

            _initialised = false; // idempotent guard
        }

        /// <summary>S83b: disposes and clears every source pipeline (scheduler always; the data source only
        /// when that pipeline owns it). Also frees an owned source for a zero-pipeline style. Idempotent.</summary>
        private void DisposePipelines()
        {
            for (int i = 0; i < _pipelines.Count; i++)
            {
                var p = _pipelines[i];
                p.Scheduler?.Dispose();          // non-owning of source/cache by contract — frees CTSs/maps
                if (p.OwnsSource) p.Source?.Dispose();
            }
            _pipelines.Clear();

            _orphanSource?.Dispose();
            _orphanSource = null;
        }
    }
}
