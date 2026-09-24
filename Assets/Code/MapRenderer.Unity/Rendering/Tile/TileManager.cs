using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.View;
using MapRenderer.Unity.Common;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using BRGBackend = MapRenderer.Unity.Rendering.Backend.BRG;
using EntBackend = MapRenderer.Unity.Rendering.Backend.Entities;
using GOBackend = MapRenderer.Unity.Rendering.Backend.GameObjects;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>Owns the tile lifecycle for <see cref="Map.MapView"/> — cover→fetch→build→consume→evict
    /// plus disposal &amp; mesh-leak guards, uniform across backends via <see cref="Backend.ITileRenderBackend"/>.</summary>
    internal sealed class TileManager : VerifiedDisposable
    {
        // Profiler markers (allocation-free) — MapRenderer.* names, asserted exactly by ProfilerMarkerTests.

        /// <summary>Profiler marker name constants (SSOT), asserted exactly by <c>ProfilerMarkerTests</c> — keep hierarchical names for Profiler flat search.</summary>
        internal static class ProfilerMarkerNames
        {
            internal const string CoverSelect      = "MapRenderer.Tile.CoverSelect";
            internal const string FetchPoll        = "MapRenderer.Tile.FetchPoll";
            internal const string SchedulerRequest = "MapRenderer.Scheduler.Request";
            internal const string MeshUpload       = "MapRenderer.Mesh.Upload";
            internal const string AddTileLayer     = "MapRenderer.Tile.AddLayer";
            internal const string MeshDataAllocate = "MapRenderer.Tile.MeshDataAllocate";
        }

        private static readonly ProfilerMarker PmCoverSelect =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.CoverSelect);

        private static readonly ProfilerMarker PmFetchPoll =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.FetchPoll);

        private static readonly ProfilerMarker PmSchedulerReq =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.SchedulerRequest);

        private static readonly ProfilerMarker PmMeshUpload =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.MeshUpload);

        /// <summary>Marks registering the mesh with the backend, split from <c>PmMeshUpload</c> to attribute spikes to upload vs registration.</summary>
        private static readonly ProfilerMarker PmAddTileLayer =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.AddTileLayer);

        /// <summary>Brackets the MeshData allocation loop in <see cref="KickMeshBuild"/> for Profiler attribution.</summary>
        private static readonly ProfilerMarker PmMeshDataAllocate =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.MeshDataAllocate);

        /// <summary>Tile-selection knobs from MapView's serialized fields, read live each <see cref="Tick"/> — not snapshotted.</summary>
        public struct TileSelectionConfig
        {
            /// <summary>The framing viewport in pixels — <c>(refH · liveAspect, refH)</c>, not raw live px
            /// (see <see cref="IVisibleTileSelector"/>). Flows into the per-tick <see cref="ViewContext"/>.</summary>
            public double2 FramingViewportPx;

            /// <summary>The active pixel↔ground projection (Web-Mercator or globe). Per-frame view context.</summary>
            public IProjection Projection;

            /// <summary>Per-frame mesh-upload budget — unlike the budgets below, 0 blocks consume rather than uncapping it.</summary>
            public int MaxConsumesPerTick;

            /// <summary>Max tiles admitted per Tick (default 2), capping mesh-build fan-out. 0 means uncapped.</summary>
            public int MaxMeshBuildsPerTick;

            /// <summary>Per-frame vertex budget for consume — the mesh that crosses it still finishes. 0 means uncapped.</summary>
            public int MaxVerticesPerTick;

            /// <summary>Per-frame budget of records fully released; records past it stay in <c>_loaded</c> until drained. 0 = uncapped.</summary>
            public int MaxReleasesPerTick;

            /// <summary>Concurrency cap on admitted, not-yet-built records — orthogonal to the rate caps above. ≤0 means uncapped.</summary>
            public int MaxConcurrentTileLoads;

            /// <summary>Which render-space distance ranks not-yet-admitted tiles for loading and <see cref="PumpPending"/>'s build/consume order.</summary>
            public TilePriorityStrategy PriorityStrategy;

            /// <summary>How much of each tile's MVT buffer the fill meshes keep before triangulation. Not a live toggle — cover keeps old geometry until re-entry.</summary>
            public TileBufferClip BufferClip;
        }

        // ── Mesh build payload ──────────────────────────────────────────────────────────────

        /// <summary>Per-tile live record: fetch request, mesh build handle, and build progress (see
        /// <c>docs/job-scheduling-design.md</c>). A default instance carries no source, so every
        /// read needs <see cref="Step"/> == <see cref="BuildStep.Prologue"/>; <see cref="Graph"/> is non-null
        /// only when <see cref="Step"/> is <see cref="BuildStep.Measure"/> or <see cref="BuildStep.Write"/>.</summary>
        private struct LoadedTile
        {
            public UniTask<SharedDisposable<IDecodedTile>>          Request;
            public bool                                             FetchCompleted; // fetch done; mesh build may be in-flight
            public WorkHandle<Processing.TilePrologueOutput> MeshBuildTask; // default until fetch completes; default after consumed
            public BuildStep                                        Step;           // which build step (if any) is in flight
            public bool                                             Built;          // mesh produced (or definitively absent/failed)

            /// <summary>The graph-arm build — non-null iff <see cref="Step"/> is <see cref="BuildStep.Measure"/> or <see cref="BuildStep.Write"/>.</summary>
            public Processing.TileBuildGraph Graph;

            /// <summary>The tile's SW-corner render origin — shared by the mesh bake and tile transform (Mercator: (mercX,0,mercZ); globe: ECEF).</summary>
            public double3 TileOriginRender;

            /// <summary>Per-layer Mesh assets from <see cref="ConsumeMeshBuild"/> — must be destroyed explicitly; null until consumed.</summary>
            public Mesh[] Meshes;

            /// <summary>Backend draw-item handles per tile-layer mesh, set by <see cref="ConsumeMeshBuild"/> and unregistered by <see cref="ReleaseTile"/>.</summary>
            public int[] DrawHandles;

            /// <summary>Parallel to <see cref="Meshes"/> — each mesh's global material index; read by <see cref="ReleaseTile"/>'s cache-transfer.</summary>
            public int[] MaterialIndices;

            /// <summary>Resumable per-mesh consume index, 0 until consume starts; mid-range means the tile is partially consumed.</summary>
            public int ConsumeCursor;

            /// <summary>The decode handle from the fetch — null before completion, and again once this record stops owning it.</summary>
            public SharedDisposable<IDecodedTile> Decode;
        }

        /// <summary>Composite key for the multi-source loaded table — value-type + <see cref="System.IEquatable{T}"/> avoids boxing on every Dictionary probe.</summary>
        internal readonly struct LoadedKey : System.IEquatable<LoadedKey>
        {
            public readonly TileId Tile;
            public readonly int    Slot;

            public LoadedKey(TileId tile, int slot)
            {
                Tile = tile;
                Slot = slot;
            }

            public          bool Equals(LoadedKey other) => Slot == other.Slot && Tile.Equals(other.Tile);
            public override bool Equals(object    obj)   => obj is LoadedKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    return Tile.GetHashCode() * 31 + Slot;
                }
            }
        }

        /// <summary>Value-equality identity of a resolved source definition — the restyle diff key. Two
        /// sources are "the same" (keep the pipeline, reuse cached bytes) iff their resolved
        /// <c>Url</c>/<c>tiles[]</c>/zoom/scheme/bounds/<c>data</c>/<c>type</c> match.
        /// Non-obvious why: an inline source has no <c>url</c>/<c>tiles[]</c>, so without <c>data</c> a restyle to
        /// another dataset keeps the first pipeline. <c>type</c> selects the factory (byte fetcher or local slicer),
        /// so without it a switch between MVT and inline GeoJSON also keeps the wrong pipeline. That repro is
        /// narrow; do not delete the <c>type</c> field for being untriggerable.</summary>
        internal readonly struct SourceKey : System.IEquatable<SourceKey>
        {
            public readonly string Url;
            public readonly string Tiles; // tiles[] joined with '\n' — cheap value-equality
            public readonly int    MinZoom;
            public readonly int    MaxZoom;
            public readonly string Scheme;
            public readonly string Bounds; // bounds joined with ',' — value-equality (null when default/absent)
            public readonly string Data;   // canonical `data` text — null when the key is absent
            public readonly SourceType Type; // the discriminator that selects the factory — see the type doc

            /// <summary>Private, with every parameter required, so <see cref="From"/> is the only way to
            /// mint a key — a defaulted <c>type</c>/<c>data</c> would silently compare equal across the field the diff branches on.</summary>
            private SourceKey(string url, string tiles, int minZoom, int maxZoom, string scheme, string bounds,
                string data, SourceType type)
            {
                Type    = type;
                Url     = url;
                Tiles   = tiles;
                MinZoom = minZoom;
                MaxZoom = maxZoom;
                Scheme  = scheme;
                Bounds  = bounds;
                Data    = data;
            }

            /// <summary>Builds the key from a resolved <see cref="SourceDefinition"/>.</summary>
            public static SourceKey From(SourceDefinition def)
            {
                string tiles  = def.Tiles  != null ? string.Join("\n", def.Tiles) : null;
                string bounds = def.Bounds != null ? string.Join(",",  def.Bounds) : null;
                string data   = def.Data   != null ? JsonCanonical.Write(def.Data) : null;
                return new SourceKey(def.Url, tiles, def.MinZoom, def.MaxZoom, def.Scheme, bounds, data, def.Type);
            }

            public bool Equals(SourceKey o)
                => Type       == o.Type    && Url    == o.Url    && Tiles   == o.Tiles
                   && MinZoom == o.MinZoom && MaxZoom == o.MaxZoom && Scheme == o.Scheme
                   && Bounds  == o.Bounds  && Data   == o.Data;

            public override bool Equals(object obj) => obj is SourceKey o && Equals(o);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + (Url   ?? string.Empty).GetHashCode();
                    h = h * 31 + (Tiles ?? string.Empty).GetHashCode();
                    h = h * 31 + MinZoom;
                    h = h * 31 + MaxZoom;
                    h = h * 31 + (Scheme ?? string.Empty).GetHashCode();
                    h = h * 31 + (Bounds ?? string.Empty).GetHashCode();
                    h = h * 31 + (Data   ?? string.Empty).GetHashCode();
                    h = h * 31 + (int)Type;
                    return h;
                }
            }
        }

        /// <summary>The caller's recipe for one source pipeline — a <see cref="CreateSource"/> thunk lets <see cref="SetSources"/> build only new/changed pipelines.</summary>
        internal readonly struct SourceSpec
        {
            public readonly string                          SourceId;
            public readonly SourceKey                       Key;
            public readonly int                             MinZoom;
            public readonly int                             MaxZoom;
            public readonly System.Func<ITileFeatureSource> CreateSource;

            public SourceSpec(string            sourceId, SourceKey key, int minZoom, int maxZoom,
                System.Func<ITileFeatureSource> createSource)
            {
                SourceId     = sourceId;
                Key          = key;
                MinZoom      = minZoom;
                MaxZoom      = maxZoom;
                CreateSource = createSource;
            }
        }

        // ── Injected collaborators (stable for life) ─────────────────────────────────────────
        private readonly Style.RenderLayerSet _layers; // owned by MapView; this reads the ordered render layers

        // Per-source pipeline registry — the per-tile lifecycle is per-(tile, source); see LoadedKey.
        private readonly SourceRegistry _sources = new();

        /// <summary>The normalized source-id a rendered style layer draws from (null → "").</summary>
        private static string SourceIdOf(StyleLayer layer) => layer?.Source ?? string.Empty;

        /// <summary>Dense global material indices whose style layer's source is <paramref name="sourceId"/> — shared by the kick, cache-transfer, and Tick probe.</summary>
        private void ComputeDenseLayerIds(string sourceId, List<int> into)
        {
            into.Clear();
            // Reads the live _layers, not SnapshotLayers() — that would heap-allocate on every probe/release.
            for (int li = 0; li < _layers.Count; li++)
                if (_layers[li] is Style.ITileMeshRenderLayer && SourceIdOf(_layers[li].StyleLayer) == sourceId)
                    into.Add(li);
        }

        /// <summary>Dense global material indices of every <see cref="Style.BackgroundRenderLayer"/> with a live material (a null must never reach AddTileLayer).</summary>
        private void ComputeSourcelessLayerIds(List<int> into)
        {
            into.Clear();
            for (int li = 0; li < _layers.Count; li++)
                if (_layers[li] is Style.BackgroundRenderLayer bg && bg.Material != null)
                    into.Add(li);
        }

        // ── Tile render backend (Entities, BRG, or GameObject) — constructed in SetSources ────
        private Backend.ITileRenderBackend _instanced; // null only before SetSources / after Dispose

        /// <summary>The launch-time projection, cached each Tick — null means WebMercator; it's launch-constant, so any Tick's value is correct.</summary>
        private IProjection _projection;

        /// <summary>The live fill tile-buffer clip, cached each Tick — unlike <c>_projection</c> it can change at runtime, which is why <see cref="TickCore"/> diffs it.</summary>
        private TileBufferClip _bufferClip;
        /// <summary>The visible-tile selection seam (default <see cref="FrustumTileSelector"/>), owning the per-tick request/release transition.</summary>
        internal IVisibleTileSelector Selector { get; set; }

        // Reused buffers — never reallocated in steady state.
        private readonly List<TileId> _cover = new(64);

        private readonly HashSet<TileId> _coverSet = new();

        // Keyed by (tile, source-slot) — one record per (tile, source).
        private readonly Dictionary<LoadedKey, LoadedTile> _loaded    = new();
        private readonly List<LoadedKey>                   _toRelease = new(32);

        /// <summary>Deferred-release queue — <see cref="Tick"/> enqueues records leaving cover; <see cref="DrainReleaseQueue"/> frees up to the per-Tick budget.</summary>
        private readonly Queue<LoadedKey> _releaseQueue = new(64);

        /// <summary>Dedups <c>_releaseQueue</c> and lets <see cref="PumpPending"/> skip an already-condemned record. Pre-sized to avoid a lazy allocation.</summary>
        private readonly HashSet<LoadedKey> _releaseQueued = new(64);

        /// <summary>Keys wanting to load but not yet admitted, kept in priority order (re-sorted each Tick by
        /// <see cref="AdmitFromDesired"/>). <c>_desiredSet</c> mirrors this for O(1) membership; both pre-sized.</summary>
        private readonly List<LoadedKey>    _desired    = new(64);
        private readonly HashSet<LoadedKey> _desiredSet = new(64);

        /// <summary>Sorts <see cref="_desired"/> and <see cref="_toRelease"/> by priority key.</summary>
        private readonly TilePrioritySorter _sorter = new();

        /// <summary>The seam driving the decoupled symbol-placement subsystem — a throwing factory must
        /// never fault the tile pipeline. Lifecycle is pulled via <see cref="CollectLoadedTileKeys"/>, never pushed.</summary>
        internal Processing.ISymbolTileWorkerFactory SymbolWorkerFactory { get; set; }

        // Reused TileCoverStats scratch — never reallocated (CaptureTelemetry steady-state no-GC).
        private readonly HashSet<int> _coverStatsX = new();
        private readonly HashSet<int> _coverStatsY = new();

        /// <summary>The cover-recompute staleness gate — dirty when the camera/viewport moved since the last commit.</summary>
        private readonly CoverKeyGate _coverGate = new();

        /// <summary>Reusable scratch for one <see cref="ConsumeMeshBuild"/> call's new meshes/handles — cleared per call, merged into the tile's arrays.</summary>
        private readonly List<Mesh> _consumeMeshes = new(8);

        private readonly List<int> _consumeHandles = new(8);

        // Parallel to the two above — each newly-built mesh's global material index, for the cache transfer.
        private readonly List<int> _consumeMatIndices = new(8);

        /// <summary>Reusable scratch for <see cref="ComputeDenseLayerIds"/>'s output — never captured across a thread boundary.</summary>
        private readonly List<int> _denseLayerIds = new(8);

        /// <summary>Cache of built tile-layer meshes so a revisit/style-toggle skips re-decode/build/upload.
        /// <see cref="ReleaseTile"/> transfers meshes here on eviction; disposed in <c>Dispose()</c> before the backend.</summary>
        private readonly PreparedTileCache _prepared;

        /// <summary>Master cache toggle, read once at construction — <see langword="false"/> reverts exactly to pre-cache behaviour.</summary>
        private readonly bool _cacheEnabled;

        /// <summary>The active style's opaque cache-key token, set by MapView in SetStyle.</summary>
        internal StyleToken CurrentStyle { get; set; } = StyleToken.Default;

        /// <summary>Prepared-cache hit count (telemetry only) — forwards to the cache's own counter, bumped by the Tick probe on a hit.</summary>
        internal int PreparedCacheHits => _prepared.Hits;

        /// <summary>Prepared-cache miss count (see <see cref="PreparedCacheHits"/>).</summary>
        internal int PreparedCacheMisses => _prepared.Misses;

        /// <summary>The three release-time holding pens (prologue, graph, fetch).</summary>
        private readonly PendingDisposalQueue _pending = new();

        /// <summary>Teardown-cancel token, cancelled once at the top of <c>DoDispose</c> so builds abort first.
        /// A field initializer, not set in the constructor, so it's never re-created across a restyle.</summary>
        private readonly CancellationTokenSource _lifetimeCts = new();

        /// <summary>Execution policy mesh-build kicks dispatch through (ThreadPool desktop/editor, Inline
        /// WebGL). Rejects <see cref="IWorkScheduler.RunsInline"/> while <see cref="MeshBuildGateForTest"/> is armed.</summary>
        internal IWorkScheduler WorkScheduler
        {
            get => _workScheduler;
            set
            {
                if (value != null && value.RunsInline && _meshBuildGateForTest != null)
                    throw new System.InvalidOperationException(
                        "WorkScheduler: cannot select a RunsInline policy while MeshBuildGateForTest is armed " +
                        "— the kick's WaitHandle.WaitAny park would then run on the CALLING thread (the body " +
                        "runs inline), i.e. main, and the only release (_lifetimeCts.Cancel() in teardown) is " +
                        "itself main-thread work that could never run — a guaranteed deadlock.");
                _workScheduler = value;
            }
        }

        private IWorkScheduler _workScheduler = WorkSchedulerFactory.ForCurrentPlatform();

        /// <summary>Test-only park: every mesh-build worker parks on this before running (not self-clearing).
        /// Rejects arming while <see cref="WorkScheduler"/> is <see cref="IWorkScheduler.RunsInline"/>.</summary>
        internal ManualResetEventSlim MeshBuildGateForTest
        {
            get => _meshBuildGateForTest;
            set
            {
                if (value != null && _workScheduler != null && _workScheduler.RunsInline)
                    throw new System.InvalidOperationException(
                        "MeshBuildGateForTest: cannot arm the gate while WorkScheduler.RunsInline is true — " +
                        "the gate's WaitHandle.WaitAny park runs on the calling (main) thread under that " +
                        "policy, with no release available — a guaranteed deadlock.");
                _meshBuildGateForTest = value;
            }
        }

        private ManualResetEventSlim _meshBuildGateForTest;

        /// <summary>The dependency handle a graph kick's measure step waits on — mirrors <see cref="MeshBuildGateForTest"/>'s
        /// role for the graph arm. Test-only, since the prologue step yields no <c>JobHandle</c> for this to gate.</summary>
        internal JobHandle GraphDepsForTest { get; set; }

        // Running count of genuine (non-cancellation) fetch errors, for bounded logging.
        private int _fetchErrorCount;

        // Decode-fault count, separate from _fetchErrorCount so a malformed tile isn't throttled inside a network-error burst.
        private int _decodeErrorCount;

        /// <summary><paramref name="cacheConfig"/> supplies the <see cref="PreparedTileCache"/>'s toggle and
        /// byte/count budget — placeholder defaults pending VRAM profiling; tunable, not correctness-critical.</summary>
        public TileManager(Style.RenderLayerSet layers, Map.PreparedTileCacheConfig cacheConfig)
        {
            _layers       = layers;
            _cacheEnabled = cacheConfig.Enabled;
            _prepared     = new PreparedTileCache(cacheConfig.ByteBudget, cacheConfig.MaxCount);
        }

        // ── Lifecycle / injection ────────────────────────────────────────────────────────────

        /// <summary>The <see cref="Map.View.SetStyle"/> FULL-REBUILD entry point: applies
        /// <paramref name="specs"/> and (re)builds the backend, unconditionally. Why unconditional, and how
        /// this differs from <see cref="RestyleSourcesInPlace"/> — `docs/tile-pipeline-design.md`.</summary>
        /// <param name="teardownRecordProbe">Test seam; null in production.</param>
        internal void SetSources(
            IReadOnlyList<SourceSpec> specs, Map.RenderBackend backend, System.Action teardownRecordProbe = null)
        {
            // 1. Render-teardown every existing record (no scheduler release, keep warm caches) — in-flight tasks land in the holding pens.
            var teardownKeys = new List<LoadedKey>(_loaded.Keys);
            for (int i = 0; i < teardownKeys.Count; i++)
            {
                RemoveAndTeardownRecord(teardownKeys[i]);
                teardownRecordProbe?.Invoke();
            }

            // Redundant on every path that reaches it: the loop above removed every key it snapshotted.
            // A throw inside the loop skips this line too — the records it never reached stay, intact.
            _loaded.Clear();
            // Records were torn down above — drop the deferred-release bookkeeping too (a queued key can't survive a restyle).
            _releaseQueue.Clear();
            _releaseQueued.Clear();
            // Also invalidated: a desired key is only valid against this registry's indexing, rebuilt below with fresh slots.
            _desired.Clear();
            _desiredSet.Clear();

            // No purge here: CurrentStyle is a content-derived token (see its own doc), so a changed style
            // already partitions to a different token and an unchanged one is safe to keep and reuse.

            // 2. Diff the pipeline registry against the new specs and commit stable slots.
            _sources.Rebuild(specs, HasBackgroundLayer());

            // 3. Rebuild the backend from the (caller-rebuilt) styled layer set; re-arm cover selection.
            _coverGate.Invalidate();
            BuildBackend(backend);
        }

        /// <summary>The <see cref="Map.View.SetStyle"/> PARTIAL-SURVIVAL entry point — called only
        /// after <see cref="Style.RenderLayerSet.TryRestyleInPlace"/> has patched <c>_layers</c> in place.
        /// Keeps (re-keys) a record whose source pipeline survived instead of tearing it down; see
        /// `docs/tile-pipeline-design.md` for why that is sound and what happens to a removed slot.</summary>
        internal void RestyleSourcesInPlace(IReadOnlyList<SourceSpec> specs, Map.RenderBackend backend)
        {
            int[] slotMap = ApplySourceDiff(specs);
            TeardownRecordsOnDepartedPipelines(slotMap);
            _coverGate.Invalidate();
            EnsureBackend(backend);
        }

        /// <summary>Diffs the pipeline registry against <paramref name="specs"/> and commits the new stable
        /// slots. Returns the OLD-slot → NEW-slot map (<see cref="SourceRegistry.Rebuild"/>'s own contract)
        /// for <see cref="TeardownRecordsOnDepartedPipelines"/> to apply against <see cref="_loaded"/>.</summary>
        private int[] ApplySourceDiff(IReadOnlyList<SourceSpec> specs) => _sources.Rebuild(specs, HasBackgroundLayer());

        /// <summary>
        /// Tears down a loaded record iff its pipeline departed (<paramref name="slotMap"/>'s <c>-1</c>);
        /// re-keys every other record to its new slot. <see cref="RestyleSourcesInPlace"/>'s narrower
        /// counterpart to <see cref="SetSources"/>'s unconditional teardown loop.
        /// </summary>
        /// <param name="slotMap">OLD slot → NEW slot, or <c>-1</c> for a departed pipeline
        /// (<see cref="SourceRegistry.Rebuild"/>).</param>
        private void TeardownRecordsOnDepartedPipelines(int[] slotMap)
        {
            var departed = new List<LoadedKey>();
            var reKeyed  = new List<(LoadedKey oldKey, LoadedTile lt, int newSlot)>();
            foreach (var kv in _loaded)
            {
                int newSlot = slotMap[kv.Key.Slot];
                if (newSlot == -1) departed.Add(kv.Key);
                else if (newSlot != kv.Key.Slot) reKeyed.Add((kv.Key, kv.Value, newSlot));
            }

            foreach (LoadedKey key in departed)
                RemoveAndTeardownRecord(key);

            foreach (var (oldKey, lt, newSlot) in reKeyed)
            {
                _loaded.Remove(oldKey);
                _loaded[new LoadedKey(oldKey.Tile, newSlot)] = lt;
            }

            // Deferred-release/desired bookkeeping is per-tick derived state, invalid against the just-
            // rebuilt slot indexing regardless of what survived — cleared unconditionally, as before.
            _releaseQueue.Clear();
            _releaseQueued.Clear();
            _desired.Clear();
            _desiredSet.Clear();
        }

        /// <summary><see cref="RestyleSourcesInPlace"/>'s backend step. This method is never reached
        /// with a null <see cref="_instanced"/> — the partial-survival arm only runs after a first, full
        /// <see cref="SetStyle"/> has already called <see cref="SetSources"/> → <see cref="BuildBackend"/> at
        /// least once — but falls back to building one rather than assuming that.</summary>
        private void EnsureBackend(Map.RenderBackend backend)
        {
            if (_instanced == null) { BuildBackend(backend); return; }
            _instanced.SetLayerMaterials(LayerMaterials(_layers), LayerShadowModes(_layers));
        }

        /// <summary>Constructs the tile render backend from the styled layer set (default arm Entities so a
        /// legacy serialized value resolves safely). Disposes any prior backend first (restyle / re-init).</summary>
        private void BuildBackend(Map.RenderBackend backend)
        {
            _instanced?.Dispose();
            _instanced = backend switch
            {
                Map.RenderBackend.Brg =>
                    new BRGBackend.TileRenderer(LayerMaterials(_layers), LayerShadowModes(_layers)),
                Map.RenderBackend.GameObject =>
                    new GOBackend.TileRenderer(LayerMaterials(_layers), LayerNames(_layers), LayerShadowModes(_layers)),
                _ => new EntBackend.TileRenderer(LayerMaterials(_layers), LayerNames(_layers), LayerShadowModes(_layers)),
            };
            PushLayerDrawGates(); // a fresh backend starts all-visible; seed it before the first item lands
        }

        /// <summary>True iff the current render layers include a <see cref="Style.BackgroundRenderLayer"/> —
        /// the synthetic source-less pipeline slot's admission test.</summary>
        private bool HasBackgroundLayer()
        {
            for (int li = 0; li < _layers.Count; li++)
                if (_layers[li] is Style.BackgroundRenderLayer)
                    return true;
            return false;
        }

        /// <summary>
        /// True iff calling <see cref="SetSources"/> with <paramref name="specs"/> would rebuild the
        /// pipeline registry identically (same pipelines, same slots) — the restyle-in-place gate's third
        /// conjunct (<c>Map.MapView.SetStyle</c>): an in-place restyle must not tear down loaded tiles for
        /// a source set it would only re-derive unchanged.
        /// </summary>
        internal bool SourcesUnchanged(IReadOnlyList<SourceSpec> specs)
            => _sources.Matches(specs, HasBackgroundLayer());

        /// <summary>Per-layer materials in SLOT order — the one full-width list every backend indexes by
        /// <c>materialIndex</c>. A null entry is possible (unassigned base material); backends must tolerate it.</summary>
        private static List<Material> LayerMaterials(Style.RenderLayerSet layers)
        {
            var mats = new List<Material>(layers.Count);
            for (int i = 0; i < layers.Count; i++) mats.Add(layers[i].Material);
            return mats;
        }

        /// <summary>Per-layer style ids in <see cref="LayerMaterials"/>'s order, so backends can name each entity by style layer.</summary>
        private static List<string> LayerNames(Style.RenderLayerSet layers)
        {
            var names = new List<string>(layers.Count);
            for (int i = 0; i < layers.Count; i++) names.Add(layers[i].StyleLayer?.Id);
            return names;
        }

        /// <summary>Per-layer shadow-cast flags in <see cref="LayerMaterials"/>'s order, so backends share one list.</summary>
        internal static List<UnityEngine.Rendering.ShadowCastingMode> LayerShadowModes(Style.RenderLayerSet layers)
        {
            var modes = new List<UnityEngine.Rendering.ShadowCastingMode>(layers.Count);
            for (int i = 0; i < layers.Count; i++) modes.Add(layers[i].CastShadows);
            return modes;
        }

        // ── Test observability (surfaced through MapViewTestExtensions, not the production API) ────────

        /// <summary>The in-flight fetch count summed across every source pipeline.</summary>
        internal int InFlightCount => _sources.TotalInFlight;

        /// <summary>Pipelines that actually own a feature source, excluding the synthetic background pipeline.
        /// Zero is the only positive signal that a source was skipped — not-throwing/fetching/rendering all look the same otherwise.</summary>
        internal int WiredFeatureSourceCount => _sources.RealSourceCount;

        /// <summary>Loaded/loading (tile, source) records — what the per-frame loops iterate.</summary>
        internal int LoadedTileCount => _loaded.Count;

        /// <summary>The active set <see cref="AdmitFromDesired"/> bounds against the concurrency cap — admitted, not-yet-built records.</summary>
        internal int ActiveLoadCount => CountActiveLoads();

        /// <summary>(tile,source) keys wanting to load but not yet admitted.</summary>
        internal int DesiredCount => _desired.Count;

        /// <summary>The head of the not-yet-admitted desired list — the next tile <see cref="AdmitFromDesired"/>
        /// will admit, or <see cref="TileId"/>'s default if the list is empty.</summary>
        internal TileId DesiredHeadTile => _desired.Count > 0 ? _desired[0].Tile : default;

        /// <summary>Fills <paramref name="into"/> with the current loaded (source, tile) membership. Allocation-free.</summary>
        internal void CollectLoadedTileKeys(List<LoadedTileKey> into)
        {
            into.Clear();
            foreach (var kv in _loaded)
            {
                into.Add(new LoadedTileKey(_sources.SourceIdOf(kv.Key.Slot), kv.Key.Tile));
            }
        }

        /// <summary>Every currently-admitted tile's <see cref="TileId"/> (may repeat across sources).</summary>
        internal void CollectLoadedTileIds(List<TileId> into)
        {
            into.Clear();
            foreach (var kv in _loaded) into.Add(kv.Key.Tile);
        }

        /// <summary>Every desired-but-not-admitted tile's <see cref="TileId"/>, in priority order.</summary>
        internal void CollectDesiredTileIds(List<TileId> into)
        {
            into.Clear();
            for (int i = 0; i < _desired.Count; i++) into.Add(_desired[i].Tile);
        }

        /// <summary>Tiles released while their mesh build was still in-flight. Incremented by <see cref="ReleaseTile"/>.</summary>
        internal int ReleasedMidFlightCount { get; private set; }

        /// <summary>Number of tiles released while their fetch was still in-flight.</summary>
        internal int ReleasedMidFetchCount { get; private set; }

        /// <summary>Times the full cover recompute ran in the most recent <see cref="Tick"/>, as opposed to an early-out.</summary>
        internal int CoverRecomputesLastTick { get; private set; }

        /// <summary>Tiles newly started in the most recent <see cref="Tick"/> — incremented once
        /// per tile, at its first kick. This is what <see cref="Config.MaxMeshBuildsPerTick"/> bounds.</summary>
        internal int TileBuildsStartedLastTick { get; private set; }

        /// <summary><see cref="Mesh.MeshDataArray"/>s allocated by the graph arm's write step in the most recent pass — one per non-empty layer.</summary>
        internal long MeshDataArraysAllocatedLastKick { get; private set; }

        /// <summary>Sum of layer-mesh vertex counts consumed in the most recent <see cref="Tick"/>.</summary>
        internal int VerticesConsumedLastTick { get; private set; }

        /// <summary>Tiles that reached <c>Built</c> in the most recent <see cref="Tick"/>.</summary>
        internal int TilesConsumedLastTick { get; private set; }

        /// <summary>Layer meshes uploaded and registered in the most recent <see cref="Tick"/> — the per-frame mesh-count budget observable.</summary>
        internal int MeshesConsumedLastTick { get; private set; }

        /// <summary>(Tile, source) records fully released in the most recent <see cref="Tick"/>.</summary>
        internal int TilesReleasedLastTick { get; private set; }

        /// <summary>Current deferred-release backlog depth (records awaiting <see cref="DrainReleaseQueue"/>).</summary>
        internal int ReleaseQueueDepth => _releaseQueue.Count;

        /// <summary>Pull-based telemetry, refreshed at the end of every <see cref="Tick"/> whether or not anyone
        /// reads it — never read by the request/release decision. Returned by reference, no copy or boxing
        /// (<c>docs/telemetry-design.md</c>).</summary>
        internal ref readonly TileTelemetrySnapshot Telemetry => ref _telemetry;

        private TileTelemetrySnapshot _telemetry;

        internal TileTelemetrySnapshot CaptureTelemetry()
        {
            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(_cover, _coverStatsX, _coverStatsY);

            // One shared pass for Pending + ConsumeBacklog — only a completed Write step, unconsumed, counts as backlog.
            int pending = 0, backlog = 0, prologue = 0, graphMeasure = 0, graphWrite = 0;
            foreach (var kv in _loaded)
            {
                LoadedTile lt = kv.Value;
                if (lt.Built) continue;
                pending++;
                if (lt.Step == BuildStep.Write && lt.Graph.IsStepComplete)
                    backlog++;
                if (lt.Step == BuildStep.Prologue) prologue++;
                else if (lt.Step == BuildStep.Measure) graphMeasure++;
                else if (lt.Step == BuildStep.Write) graphWrite++;
            }

            return new TileTelemetrySnapshot
            {
                VisibleTileCount       = _cover.Count,
                CoverColumns           = columns,
                CoverRows              = rows,
                SelectionZoom          = maxZ,
                IsMixedZoom            = minZ != maxZ,
                CoverMinZoom           = minZ,
                CoverMaxZoom           = maxZ,
                FractionalZoom         = _coverGate.LastZoom,
                LoadedTileCount        = _loaded.Count,
                PendingTileCount       = pending,
                ConsumeBacklog         = backlog,
                PrologueInFlight       = prologue,
                GraphMeasureInFlight   = graphMeasure,
                GraphWriteInFlight     = graphWrite,
                InFlightFetches        = InFlightCount,
                ReleasedMidFlightCount = ReleasedMidFlightCount,
                ReleasedMidFetchCount  = ReleasedMidFetchCount,
                FetchErrorCount        = _fetchErrorCount,

                // PreparedTileCache utilization — read straight off the live cache, allocates nothing.
                PreparedCacheEnabled    = _cacheEnabled,
                PreparedCacheHits       = _prepared.Hits,
                PreparedCacheMisses     = _prepared.Misses,
                PreparedCacheEntryCount = _prepared.Count,
                PreparedCacheMaxCount   = _prepared.MaxCount,
                PreparedCacheBytesHeld  = _prepared.BytesHeld,
                PreparedCacheByteBudget = _prepared.ByteBudget,
                PreparedCacheEvictions  = _prepared.Evictions,
            };
        }

        /// <summary>The live BRG renderer, or null when not on the BRG backend / before <see cref="SetSources"/>.</summary>
        internal BRGBackend.TileRenderer BrgRenderer => _instanced as BRGBackend.TileRenderer;

        /// <summary>The live Entities-Graphics renderer, or null when not on the Entities backend / before <see cref="SetSources"/>.</summary>
        internal EntBackend.TileRenderer EntitiesRenderer => _instanced as EntBackend.TileRenderer;

        /// <summary>The live GameObject renderer, or null when not on the GameObject backend / before <see cref="SetSources"/>.</summary>
        internal GOBackend.TileRenderer GameObjectRenderer => _instanced as GOBackend.TileRenderer;

        /// <summary>Invalidates the cached cover-selection key so the next <see cref="Tick"/> re-selects the
        /// cover. Called when the camera is re-wired (<see cref="Map.View.SetCamera"/>).</summary>
        public void InvalidateCover() => _coverGate.Invalidate();

        /// <summary>Rebuilds the per-tile object-to-world transforms (and refreshes backend state) for all
        /// loaded tiles from <paramref name="frame"/>. Called by MapView once per frame.</summary>
        public void InstancedRebuild(in Backend.SceneFrame frame)
        {
            _instanced?.Rebuild(frame);
        }

        /// <summary>
        /// Pushes each layer's visibility to the backend as a per-slot draw gate, so a layer that paints
        /// nothing the framebuffer can show submits no draw item at all. Called beside every
        /// <see cref="Style.RenderLayerSet.ApplyZoom"/> — that is what refreshes the values read here, and
        /// a frame must never render between the two. Kinds with no opacity of their own always draw.
        /// </summary>
        public void PushLayerDrawGates()
        {
            if (_instanced == null) return;
            for (int li = 0; li < _layers.Count; li++)
                _instanced.SetLayerVisible(
                    li, !(_layers[li] is Style.IFadeableRenderLayer fadeable) || fadeable.PaintsSomething);
        }

        /// <summary>Test-only: true ⟺ the tile has ≥1 source-record, ALL its records are <c>Built</c>, and
        /// the union produced geometry. N=1 ⇒ identical to a single-record (built + has-geometry) check.</summary>
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

        /// <summary>Test-only, backend-agnostic: <see cref="Mesh"/> assets for a loaded tile — the union
        /// across its source-records (pipeline-slot order), or null if the tile has no geometry.</summary>
        internal Mesh[] GetTileMeshes(TileId id)
        {
            List<Mesh> all = null;
            for (int s = 0; s < _sources.Count; s++)
            {
                if (_loaded.TryGetValue(new LoadedKey(id, s), out var lt) && lt.Meshes != null)
                {
                    all ??= new List<Mesh>(8);
                    all.AddRange(lt.Meshes);
                }
            }

            return all?.ToArray();
        }

        /// <summary>Test-only: global material index of each mesh in <see cref="GetTileMeshes"/>, same order
        /// (both walk pipelines/meshes in lockstep).</summary>
        internal int[] GetTileMaterialIndices(TileId id)
        {
            List<int> all = null;
            for (int s = 0; s < _sources.Count; s++)
            {
                if (_loaded.TryGetValue(new LoadedKey(id, s), out var lt) && lt.MaterialIndices != null)
                {
                    all ??= new List<int>(8);
                    all.AddRange(lt.MaterialIndices);
                }
            }

            return all?.ToArray();
        }

        /// <summary>Test-only: scene-space bounds of all live tile draw items, via the instanced backend.
        /// Returns <c>default</c> if there's no backend or no tiles.</summary>
        internal Bounds ComputeSceneBounds(float tileSizeWorld)
            => _instanced != null ? _instanced.ComputeSceneBounds(tileSizeWorld) : default;

        /// <summary>Test-only: true once every loaded tile has finished building and <see cref="_desired"/>
        /// is empty — a <c>_loaded</c>-only check would miss cap-deferred tiles, which have no record until admitted.</summary>
        internal bool AllTilesSettled()
        {
            if (_desired.Count > 0) return false;

            foreach (var kv in _loaded)
            {
                if (!kv.Value.Built)
                    return false;
            }

            return true;
        }

        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>One frame of the tile loop — a thin shell over <see cref="TickCore"/> so telemetry
        /// refreshes after every return from it, early returns included (a dirty-frame-only update would
        /// freeze when the map goes still). A throw from <see cref="TickCore"/> skips the refresh.</summary>
        public void Tick(CameraProperties cam, TileSelectionConfig cfg)
        {
            TickCore(cam, cfg);
            _telemetry = CaptureTelemetry();
        }

        private void TickCore(CameraProperties cam, TileSelectionConfig cfg)
        {
            if (Selector == null) return;

            ResetPerTickCounters();

            // Defensive backstop, not the normal path — cfg.Projection is always non-null here in production.
            _projection = cfg.Projection ?? new WebMercatorProjection(); // cached for the mesh build bake

            // A changed clip window invalidates every cached mesh — this Clear() IS the invalidation, not a reclamation.
            if (cfg.BufferClip.IsEnabled             != _bufferClip.IsEnabled ||
                cfg.BufferClip.KeepAtReferenceExtent != _bufferClip.KeepAtReferenceExtent)
            {
                _bufferClip = cfg.BufferClip;
                _prepared.Clear();
            }

            _coverGate.MarkStaleIfMoved(in cam, in cfg);

            // Drain any completed mid-flight-discard work so its NativeArrays/fetch tasks are freed.
            _pending.DrainCompleted();

            // Shared priority context, reused by the admission gate and PumpPending's sort, so enumeration order can't decide a race.
            var priorityCtx = TilePriorityContext.From(in cam, cfg.FramingViewportPx, cfg.Projection,
                cfg.PriorityStrategy);

            // The cover recompute is gated on _coverGate alone — a clean camera must not re-run the descent.
            if (_coverGate.IsDirty)
            {
                CoverRecomputesLastTick = 1; // The full recompute (descent + diff) runs this Tick

                // Not `using var` — closed explicitly so CoverSelect attribution excludes admission/pump/drain costs.
                var sCoverSel = PmCoverSelect.Auto();

                // Select through the seam over the per-frame view context.
                ViewContext view = new ViewContext
                {
                    Camera     = cam,
                    ViewportPx = cfg.FramingViewportPx,
                    Projection = cfg.Projection,
                };
                Selector.SelectVisibleTiles(in view, _cover);

                _coverSet.Clear();
                for (int i = 0; i < _cover.Count; i++)
                    _coverSet.Add(_cover[i]);

                // Merge, not rebuild — newly-covered keys join the desired list; admission below is priority-ordered and capped.
                for (int i = 0; i < _cover.Count; i++)
                {
                    TileId id = _cover[i];
                    for (int s = 0; s < _sources.Count; s++)
                    {
                        if (!_sources.AdmitsZoom(s, id.Z)) continue; // source doesn't serve this zoom
                        var key = new LoadedKey(id, s);
                        if (_loaded.ContainsKey(key)) continue; // already admitted — untouched (never re-queued)
                        if (_desiredSet.Add(key)) _desired.Add(key);
                    }
                }

                // Drop desired entries whose tile left the cover — an already-admitted record is never touched here.
                for (int i = _desired.Count - 1; i >= 0; i--)
                {
                    LoadedKey dk = _desired[i];
                    if (!_coverSet.Contains(dk.Tile))
                    {
                        _desiredSet.Remove(dk);
                        _desired.RemoveAt(i);
                    }
                }

                // Records whose tile left the cover are enqueued for deferred release — _releaseQueued dedups repeats.
                _toRelease.Clear();
                foreach (var kv in _loaded)
                {
                    if (!_coverSet.Contains(kv.Key.Tile))
                        _toRelease.Add(kv.Key);
                }

                for (int i = 0; i < _toRelease.Count; i++)
                {
                    LoadedKey key = _toRelease[i];
                    if (_releaseQueued.Add(key)) _releaseQueue.Enqueue(key);
                }

                _coverGate.Commit(in cam, in cfg);

                sCoverSel.Dispose();
            }

            // Runs every Tick, clean or dirty — admission is not gated on _coverGate, so entries drain once the camera stills.
            AdmitFromDesired(in priorityCtx, cfg.MaxConcurrentTileLoads);
            PumpPending(cam, cfg.MaxConsumesPerTick, cfg.MaxMeshBuildsPerTick, cfg.MaxVerticesPerTick, in priorityCtx);

            // Drain a budgeted slice of the deferred-release backlog EVERY Tick, after admission/pump.
            DrainReleaseQueue(cfg.MaxReleasesPerTick);
        }

        /// <summary>Zeroes the six per-tick counters — cover recomputes, builds started, tiles/vertices/meshes
        /// consumed, tiles released — at the top of <see cref="TickCore"/>. Five are read-only telemetry;
        /// <see cref="TileBuildsStartedLastTick"/> also bounds <see cref="PumpPending"/>'s build cap, over
        /// the whole Tick rather than just the pump — a stray charge before <c>PumpPending</c> fails safe
        /// (admits nothing) instead of silently doubling the cap, though no tooth tells which path charged it.</summary>
        private void ResetPerTickCounters()
        {
            // A throw before PumpPending still clears the previous Tick's counts, rather than retaining them.
            CoverRecomputesLastTick   = 0;
            TileBuildsStartedLastTick = 0;
            VerticesConsumedLastTick  = 0;
            TilesConsumedLastTick     = 0;
            MeshesConsumedLastTick    = 0;
            TilesReleasedLastTick     = 0;
        }

        /// <summary>Deterministic drain: blocks until every in-flight fetch/mesh-build task completes and
        /// consumes it, so <see cref="AllTilesSettled()"/> is true afterward. Test-only, not called from Update.</summary>
        internal void DrainMeshBuilds(CameraProperties cam)
        {
            // Only .Projection is read on this uncapped path — cap == int.MaxValue skips the priority sort entirely.
            var admitCtx = new TilePriorityContext(_projection, default, default, default, default);
            AdmitFromDesired(in admitCtx, int.MaxValue);

            // Collect all unsettled records.
            var unsettled = new List<LoadedKey>(8);
            foreach (var kv in _loaded)
            {
                if (!kv.Value.Built)
                    unsettled.Add(kv.Key);
            }

            foreach (var key in unsettled)
            {
                TileId     id       = key.Tile;
                string     sourceId = _sources.SourceIdOf(key.Slot);
                LoadedTile lt       = _loaded[key];

                // (a) If fetch is still in-flight, spin until it completes and kick mesh build.
                if (!lt.FetchCompleted)
                {
                    var req = lt.Request;
                    // req's completion stays OFF the PlayerLoop, so this park wakes with no deadlock risk.
                    req.WaitOffPlayerLoop(10000);

                    lt.FetchCompleted = true;

                    // Observe the fetch outcome exactly once so a faulted fetch is never dropped unobserved.
                    SharedDisposable<IDecodedTile> handle = TakeDecodeFromFetch(req);
                    if (handle != null)
                    {
                        // STORE it, not a bare local — a throw inside the kick must leave `_loaded[key]` recoverable, not lost.
                        lt.Decode = handle;
                    }
                    else
                    {
                        // Absent/failed/cancelled fetch — nothing to build.
                        lt.Built     = true;
                        _loaded[key] = lt;
                        continue;
                    }
                }

                // (a2) fetch completed but not yet kicked (cap-deferred or observed above) — kick inline; drain ignores per-tick caps.
                if (lt.FetchCompleted && lt.Decode != null && lt.Step == BuildStep.None)
                {
                    lt.MeshBuildTask = KickMeshBuild(lt, id, lt.Decode, sourceId);
                    lt.Step          = BuildStep.Prologue;
                    // The record keeps its reference — the kick took its own in KickMeshBuild's prologue; lt.Decode stays live until teardown.
                }

                // A source-less record is only created, never kicked — kick inline or it falls invisible to the no-mesh settle below.
                if (lt.FetchCompleted && lt.Step == BuildStep.None && lt.Decode == null &&
                    _sources.IsSourceless(key.Slot))
                {
                    lt.Graph = KickSourcelessBackground(id, lt.TileOriginRender);
                    lt.Step  = BuildStep.Measure;
                }

                // (b) A PROLOGUE build in-flight — spin, then hand off; a handoff here is picked up in this same iteration.
                if (lt.Step == BuildStep.Prologue)
                {
                    // WaitOffPlayerLoop applies because WorkScheduler completes on the completing thread, never the PlayerLoop.
                    UniTask<Processing.TilePrologueOutput> buildTask = lt.MeshBuildTask.ToUniTask();
                    buildTask.WaitOffPlayerLoop(10000);

                    if (!lt.MeshBuildTask.IsSucceeded)
                    {
                        FinishConsume(ref lt);
                    }
                    else
                    {
                        Processing.TilePrologueOutput output = lt.MeshBuildTask.GetResult();
                        // Store BEFORE scheduling — ScheduleMeasureFromDecode must find `_loaded[key]` past the completed task, or a drain double-frees it.
                        lt.MeshBuildTask = default;
                        lt.Step          = BuildStep.None;
                        _loaded[key]     = lt;
                        lt.Graph = Processing.TileBuildGraph.ScheduleMeasureFromDecode(
                            output.Layers, output.Decode, GraphDepsForTest);
                        lt.Step = BuildStep.Measure;
                    }
                }

                // (b') GRAPH measure/write in-flight — Complete() runs inline from the main thread, so no WaitOffPlayerLoop is needed.
                if (lt.Step == BuildStep.Measure || lt.Step == BuildStep.Write)
                {
                    if (lt.Step == BuildStep.Measure)
                    {
                        int allocated;
                        using (PmMeshDataAllocate.Auto()) lt.Graph.CompleteMeasureAndScheduleWrite(out allocated);
                        MeshDataArraysAllocatedLastKick += allocated;
                        lt.Step = BuildStep.Write;
                    }
                    // CompleteWriteAndTakePayloads is idempotent — a partially-consumed tile gets the same array back with nulled slots.
                    Style.MeshDataPayload[] payloads = lt.Graph.CompleteWriteAndTakePayloads();
                    // lt.Graph is NOT disposed here, FinishConsume is the single disposal site — drain ignores per-frame caps.
                    ConsumeMeshBuild(id, ref lt, payloads, int.MaxValue, int.MaxValue, out _, out _);
                }
                else if (lt.Step == BuildStep.None && !lt.Built)
                {
                    lt.Built = true;
                }

                _loaded[key] = lt;
            }
        }

        /// <summary>Waits for in-flight fetch and mesh-build tasks on <c>_loaded</c> tiles, parking off the
        /// PlayerLoop rather than polling. Does not consume or kick anything — a caller that needs the mesh
        /// built must still tick <c>LateUpdate</c>. <see cref="DrainMeshBuilds"/> is the consuming peer.
        ///
        /// <para>Never re-park a still-pending fetch: it double-registers the <c>UniTask</c>'s single
        /// continuation slot.</para></summary>
        /// <param name="timeoutMs">Maximum wait per task, in milliseconds.</param>
        /// <exception cref="System.TimeoutException">A task did not finish within <paramref name="timeoutMs"/>.</exception>
        internal void AwaitInFlightMeshBuilds(int timeoutMs)
        {
            foreach (var kv in _loaded)
            {
                LoadedTile lt = kv.Value;
                if (lt.Built) continue;

                bool completed;
                if (!lt.FetchCompleted)
                    completed = lt.Request.WaitOffPlayerLoop(timeoutMs);
                else if (lt.Step == BuildStep.Prologue)
                {
                    UniTask<Processing.TilePrologueOutput> buildTask = lt.MeshBuildTask.ToUniTask();
                    completed = buildTask.WaitOffPlayerLoop(timeoutMs);
                }
                // Complete() advances no step and consumes nothing; it can't throw TimeoutException, since a graph has no PlayerLoop to dead-end on.
                else if (lt.Step == BuildStep.Measure || lt.Step == BuildStep.Write)
                {
                    lt.Graph.Complete();
                    completed = true;
                }
                else
                    continue; // fetch observed but not yet kicked (cap-deferred) — no in-flight task to park on.

                if (!completed)
                    throw new System.TimeoutException(
                        $"AwaitInFlightMeshBuilds: tile {kv.Key.Tile} did not complete within {timeoutMs}ms — " +
                        "a hung fetch or mesh-build task. Re-parking a still-pending task would double-register " +
                        "its single continuation, so this fails loud instead of retrying.");
            }
        }

        /// <summary>Kicks each tile's mesh build then consumes it, bounded by the mesh-build/consume/vertex
        /// budgets — whichever binds first stops it for this Tick. Sorted by <paramref name="priorityCtx"/>, the paint-order seam (<c>docs/tile-pipeline-design.md</c>).</summary>
        private int PumpPending(
            CameraProperties cam,
            int              maxConsumesPerTick,
            int              maxMeshBuildsPerTick,
            int              maxVerticesPerTick,
            in TilePriorityContext priorityCtx)
        {
            using var sFetchPoll = PmFetchPoll.Auto();

            // The four consume/build counters reset in ResetPerTickCounters() (once per Tick); this field's
            // window is the KICK pass — the suffix says so — and DrainMeshBuilds also accumulates into it.
            MeshDataArraysAllocatedLastKick = 0;

            // Treat 0 as uncapped for the kick + vertex caps (unset config field → harmless default).
            int buildCap = maxMeshBuildsPerTick > 0 ? maxMeshBuildsPerTick : int.MaxValue;
            int vertsCap = maxVerticesPerTick   > 0 ? maxVerticesPerTick : int.MaxValue;
            // The mesh-count cap is used directly — 0 BLOCKS consume (tests build a backlog that way).
            int consumeCap = maxConsumesPerTick;

            _toRelease.Clear();
            foreach (var kv in _loaded)
            {
                if (!kv.Value.Built)
                    _toRelease.Add(kv.Key);
            }

            // Nearest-center-first paint order — the same priority the admission gate uses.
            _sorter.Sort(_toRelease, in priorityCtx);

            int pending          = 0;
            int meshesConsumed   = 0;
            int verticesConsumed = 0;
            bool scheduledThisPass = false;

            for (int i = 0; i < _toRelease.Count; i++)
            {
                LoadedKey  key      = _toRelease[i];
                TileId     id       = key.Tile;
                string     sourceId = _sources.SourceIdOf(key.Slot);
                LoadedTile lt       = _loaded[key];

                // (1) A completed PROLOGUE hands off to the graph — uncharged, already counted at its prologue kick.
                if (lt.FetchCompleted && lt.Step == BuildStep.Prologue && lt.MeshBuildTask.IsCompleted)
                {
                    if (!lt.MeshBuildTask.IsSucceeded)
                    {
                        FinishConsume(ref lt);
                        TilesConsumedLastTick++;
                        _loaded[key] = lt;
                        continue;
                    }

                    Processing.TilePrologueOutput output = lt.MeshBuildTask.GetResult();
                    // Store BEFORE scheduling — ScheduleMeasure owns `output` including its throw path; a throw leaves Step == None to re-kick later.
                    lt.MeshBuildTask = default;
                    lt.Step          = BuildStep.None;
                    _loaded[key]     = lt;

                    lt.Graph          = Processing.TileBuildGraph.ScheduleMeasureFromDecode(
                        output.Layers, output.Decode, GraphDepsForTest);
                    lt.Step           = BuildStep.Measure;
                    scheduledThisPass = true;
                    pending++; // measure step now in-flight
                    _loaded[key] = lt;
                    continue;
                }

                // ── (2) Consume a completed GRAPH write step, under the same dual budget ──────────────
                if (lt.Step == BuildStep.Write && lt.Graph.IsStepComplete)
                {
                    int meshBudgetLeft = consumeCap - meshesConsumed;
                    int vertBudgetLeft = vertsCap   - verticesConsumed;
                    if (meshBudgetLeft <= 0 || vertBudgetLeft <= 0)
                    {
                        pending++;
                        continue;
                    }

                    // CompleteWriteAndTakePayloads is idempotent — a resumed tile gets the same array back; lt.Graph is disposed only by FinishConsume.
                    Style.MeshDataPayload[] payloads = lt.Graph.CompleteWriteAndTakePayloads();

                    bool complete = ConsumeMeshBuild(
                        id, ref lt, payloads, meshBudgetLeft, vertBudgetLeft,
                        out int meshesThisCall, out int vertsThisCall);

                    meshesConsumed           += meshesThisCall;
                    verticesConsumed         += vertsThisCall;
                    MeshesConsumedLastTick   =  meshesConsumed;
                    VerticesConsumedLastTick =  verticesConsumed;
                    if (complete) TilesConsumedLastTick++;
                    else pending++; // tile partially consumed — resume next Tick
                    _loaded[key] = lt;
                    continue;
                }

                // ── (3) Complete a finished GRAPH measure step and schedule its write step ────────────
                if (lt.Step == BuildStep.Measure && lt.Graph.IsStepComplete)
                {
                    // An already-admitted tile's write transition is uncharged — not re-gated per step.
                    int allocated;
                    using (PmMeshDataAllocate.Auto()) lt.Graph.CompleteMeasureAndScheduleWrite(out allocated);
                    MeshDataArraysAllocatedLastKick += allocated;
                    lt.Step = BuildStep.Write;
                    scheduledThisPass = true;
                    pending++; // write step now in-flight
                    _loaded[key] = lt;
                    continue;
                }

                // ── (4) In-flight arms — neither step above has completed yet ──────────────────────────
                if (lt.FetchCompleted && lt.Step == BuildStep.Prologue)
                {
                    pending++;
                    continue;
                }
                if (lt.Step == BuildStep.Measure || lt.Step == BuildStep.Write)
                {
                    pending++;
                    continue;
                }

                // ── (5) Kick a mesh build from Decode ─────────────────────────────────────────
                if (lt.FetchCompleted && lt.Decode != null)
                {
                    // Don't start a new background build for a record already condemned — in-flight builds still finish into the disposal pens.
                    if (_releaseQueued.Contains(key))
                    {
                        pending++;
                        continue;
                    }

                    if (TileBuildsStartedLastTick >= buildCap)
                    {
                        // Cap reached — the record keeps its reference until a later Tick kicks it.
                        pending++;
                        continue;
                    }

                    // Obtain the symbol worker pass on the main thread, isolated — a throwing factory must never fault the pump — then ride the kick task.
                    Processing.ISymbolTileWorkerPass symbolPass = null;
                    try
                    {
                        symbolPass = SymbolWorkerFactory?.TryBeginBuild(sourceId, id);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[TileManager] symbol factory (begin-build) threw for {id}: {ex.Message}");
                    }

                    lt.MeshBuildTask = KickMeshBuild(lt, id, lt.Decode, sourceId, symbolPass);
                    lt.Step          = BuildStep.Prologue;
                    // The record keeps its reference — the kick took its own, live until RenderTeardownRecord releases it.
                    TileBuildsStartedLastTick++; // this is where a source tile is admitted — the only place it is charged
                    pending++; // mesh build now in-flight
                    _loaded[key] = lt;
                    continue;
                }

                // (6) Kicks a source-less build, only created (never kicked) — rides the same condemned-skip + buildCap throttle.
                if (lt.FetchCompleted && lt.Step == BuildStep.None && lt.Decode == null &&
                    _sources.IsSourceless(key.Slot))
                {
                    if (_releaseQueued.Contains(key))
                    {
                        pending++;
                        continue;
                    }

                    if (TileBuildsStartedLastTick >= buildCap)
                    {
                        pending++;
                        continue;
                    }

                    lt.Graph = KickSourcelessBackground(id, lt.TileOriginRender);
                    lt.Step  = BuildStep.Measure;
                    TileBuildsStartedLastTick++; // this is where a background tile is admitted — same as arm (5)
                    scheduledThisPass = true;
                    pending++; // measure step now in-flight
                    _loaded[key] = lt;
                    continue;
                }

                // ── Fetch in-flight ────────────────────────────────────────────────────────────
                if (!lt.Request.Status.IsCompleted())
                {
                    pending++;
                    continue;
                }

                // Observe the fetch outcome exactly once, so a faulted fetch is never left unobserved.
                lt.FetchCompleted = true;
                SharedDisposable<IDecodedTile> handle = TakeDecodeFromFetch(lt.Request);
                if (handle != null)
                {
                    // Retain the handle once per fetch — the fork point where one (source, tile) splits into the mesh and symbol cadences.
                    lt.Decode = handle;
                    pending++; // decode awaiting kick
                }
                else
                {
                    // Absent / failed / cancelled — mark built (nothing to render).
                    lt.Built = true;
                }

                _loaded[key] = lt;
            }

            // Flush once per pass, not per tile — a scheduled job isn't handed to workers until flushed.
            if (scheduledThisPass) JobHandle.ScheduleBatchedJobs();

            return pending;
        }

        /// <summary>Starts a background mesh build over the shared decode (off-PlayerLoop completion — see
        /// <c>docs/async-architecture.md</c> "Disposal &amp; cancellation contract"). Bakes at the tile's
        /// integer zoom — see <c>docs/tile-pipeline-design.md</c> for the full bake-parameter SSOT.</summary>
        private WorkHandle<Processing.TilePrologueOutput> KickMeshBuild(
            LoadedTile                       lt, TileId id, SharedDisposable<IDecodedTile> decode, string sourceId,
            Processing.ISymbolTileWorkerPass symbolPass = null)
        {
            // `decode` is borrowed for the caller — the kick takes its own reference and releases exactly that one.
            var     layersSnapshot = _layers.SnapshotLayers();
            double  zoom           = id.Z;
            double3 tileOrigin     = lt.TileOriginRender;

            // Dense per-(tile, source) produce, slot order — each dense slot becomes one processor, never captured across threads.
            ComputeDenseLayerIds(sourceId, _denseLayerIds);
            int dense = _denseLayerIds.Count;

            // One processor per this-source layer, rented from its pool — AllocateForKick allocates no MeshDataArray yet.
            var processors = new Processing.ITileMeshLayerProcessor[dense];
            for (int d = 0; d < dense; d++)
            {
                int li = _denseLayerIds[d];
                // ComputeDenseLayerIds already filtered to ITileMeshRenderLayer slots — safe cast.
                var layer = (Style.ITileMeshRenderLayer)layersSnapshot[li];
                processors[d] = Processing.TileMeshLayerProcessor.AllocateForKick(layer, li);
            }

            var context = new Processing.TileLayerProcessContext
            {
                Tile             = id,
                Zoom             = zoom,
                TileOriginRender = tileOrigin,
                Projection       = _projection,
                BufferClip       = _bufferClip,
            };

            decode.Acquire(); // the kick's OWN reference — see the ownership comment above
            CancellationToken token = _lifetimeCts.Token; // captured as a local so the lambda closes over the token, not `this`
            try
            {
                return WorkScheduler.Schedule(_ =>
                {
                    // Test gate: when non-null, park jointly on it and the lifetime token so a test can hold a build in-flight.
                    var gate = MeshBuildGateForTest;
                    if (gate != null) WaitHandle.WaitAny(new[] { gate.WaitHandle, token.WaitHandle });

                    // The kick's own reference covers the mesh and symbol passes — on success it transfers to TilePrologueOutput, on fault the catch releases it.
                    try
                    {
                        // The fan-out point: runs every this-source processor once in dense order against the decoded tile, settling each once.
                        Processing.TilePrologueOutput output =
                            Processing.TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors, token);

                        // The symbol fault domain: the mesh output is already built, so a symbol fault below can't
                        // strand it. Skipped once the lifetime token cancels.
                        try
                        {
                            if (!token.IsCancellationRequested)
                                symbolPass?.RunWorkerAndHandoff(decode);
                        }
                        catch (System.Exception)
                        { /* a contract-violating throw must not strand the mesh arrays */
                        }

                        output.Decode = decode; // transferred — see the comment above
                        return output;
                    }
                    catch
                    {
                        decode.Release();
                        throw;
                    }
                }, token);
            }
            catch
            {
                // The hand-off can throw synchronously (OOM) before the lambda runs, so the inner catch can't release — free the kick's reference here.
                decode.Release();
                throw;
            }
        }

        /// <summary>Starts the graph-arm build for a source-less (background) tile — no bytes, decode, or
        /// scheduler; skips the prologue entirely. Mints the full-tile quad once and shares it borrowed across
        /// every layer; <see cref="Processing.TileBuildGraph.ScheduleMeasure"/> disposes it once, never per-layer.</summary>
        private Processing.TileBuildGraph KickSourcelessBackground(TileId id, double3 origin)
        {
            var    layersSnapshot = _layers.SnapshotLayers();
            double zoom           = id.Z;

            ComputeSourcelessLayerIds(_denseLayerIds);
            int dense = _denseLayerIds.Count;

            var context = new Processing.TileLayerProcessContext
            {
                Tile             = id,
                Zoom             = zoom,
                TileOriginRender = origin,
                Projection       = _projection,
                BufferClip       = _bufferClip,
            };

            TileGeometryBuffers geometry = Processing.BackgroundQuad.MintFullExtentGeometry(id);

            var builds = new Meshing.ILayerMeshBuild[dense];
            for (int d = 0; d < dense; d++)
            {
                int li    = _denseLayerIds[d];
                var layer = (Style.BackgroundRenderLayer)layersSnapshot[li];
                string payloadName = layer?.StyleLayer?.Id ?? "background";

                FillMeshPipeline.LayerInput input = Processing.BackgroundQuad.BuildLayerInput(
                    context, geometry, out NativeArray<int> visitOrder, out NativeArray<Vector4> featureColors);
                // visitOrder folds into `input.RingVisitOrder`, owned by the build; `geometry` is shared/borrowed, disposed once via ScheduleMeasure.

                // A background quad meshes exactly like a source fill layer, so it counts into LayerMeshBuildCounters the same way.
                builds[d] = Meshing.FillLayerBuild.Rent(input, featureColors, li, payloadName);
            }

            return Processing.TileBuildGraph.ScheduleMeasure(builds, geometry, GraphDepsForTest);
        }

        /// <summary>Resumable per-mesh consume: uploads and registers layers from <see cref="LoadedTile.ConsumeCursor"/>
        /// until a budget binds or the tile finishes. Returns true when fully consumed, false to resume next Tick.</summary>
        private bool ConsumeMeshBuild(
            TileId id, ref LoadedTile lt, Style.MeshDataPayload[] payloads, int meshBudget, int vertBudget,
            out int meshesConsumed, out int vertsConsumed)
        {
            meshesConsumed = 0;
            vertsConsumed  = 0;

            int denseCount        = payloads?.Length ?? 0;
            int currentLayerCount = _layers.Count;

            _consumeMeshes.Clear();
            _consumeHandles.Clear();
            _consumeMatIndices.Clear();

            // Consume dense payloads from the cursor until a budget binds — a positive budget guarantees at least one layer per call.
            int cursor = lt.ConsumeCursor;
            while (cursor < denseCount && meshesConsumed < meshBudget && vertsConsumed < vertBudget)
            {
                // A payload whose slot is gone — out of range, or RETIRED to a tombstone by a mid-flight
                // restyle — is freed, never registered: width never shrinks, so a count check cannot see it.
                int slot = cursor;
                Style.MeshDataPayload payload = payloads[slot];
                cursor++;

                int materialIndex = payload?.MaterialIndex ?? -1;
                int layerVerts    = payload?.VertexCount   ?? 0;

                if (payload == null
                 || (uint)materialIndex >= (uint)currentLayerCount
                 || _layers[materialIndex] is Style.TombstoneRenderLayer)
                {
                    payload?.Dispose();
                    // Null the slot the instant Dispose() has run — load-bearing, not cosmetic (see below).
                    payloads[slot] = null;
                    continue;
                }

                Mesh mesh;
                using (PmMeshUpload.Auto())
                    mesh = payload.Upload();
                payload.Dispose(); // consumed — free its NativeArrays now

                // Dispose() returns the instance to a shared pool — payload may already be another build's live instance, so stop referencing it.
                payloads[slot] = null;

                if (mesh == null) continue; // empty layer — no AddLayer, no budget charge

                int handle;
                using (PmAddTileLayer.Auto())
                    handle = _instanced.AddTileLayer(mesh, lt.TileOriginRender, materialIndex, id);
                _consumeMeshes.Add(mesh); // Track for explicit destruction
                _consumeHandles.Add(handle);
                _consumeMatIndices.Add(materialIndex); // Which layerId this mesh belongs to
                meshesConsumed++;
                vertsConsumed += layerVerts;
            }

            lt.ConsumeCursor = cursor;

            // Append this call's new meshes/handles/indices to the tile's arrays — one realloc per partial frame, load time only.
            AppendMeshes(ref lt.Meshes, _consumeMeshes);
            AppendInts(ref lt.DrawHandles,     _consumeHandles);
            AppendInts(ref lt.MaterialIndices, _consumeMatIndices);

            bool complete = cursor >= denseCount;
            if (complete)
            {
                // Dispose the whole payload array (idempotent), freeing layers the active style doesn't render, then mark Built and release the task.
                DisposeWholePayloads(payloads);
                FinishConsume(ref lt);
            }

            return complete;
        }

        /// <summary>Marks a tile fully consumed and clears its build state — the single disposal site for
        /// <see cref="LoadedTile.Graph"/>; disposing earlier would leave a partial tile whose null Graph the next Tick dereferences.</summary>
        private static void FinishConsume(ref LoadedTile lt)
        {
            lt.Graph?.Dispose();
            lt.Step          = BuildStep.None;
            lt.MeshBuildTask = default;
            lt.Graph         = null;
            lt.Built         = true;
        }

        /// <summary>Appends freshly-built meshes to a tile's tracked-Mesh array (grows by realloc).</summary>
        private static void AppendMeshes(ref Mesh[] arr, List<Mesh> add)
        {
            if (add.Count == 0) return;
            int oldLen = arr?.Length ?? 0;
            var merged = new Mesh[oldLen + add.Count];
            if (arr           != null) System.Array.Copy(arr, merged, oldLen);
            for (int k = 0; k < add.Count; k++) merged[oldLen + k] = add[k];
            arr = merged;
        }

        /// <summary>Appends freshly-produced ints to a tile's tracked int[] (grows by realloc). Used for
        /// both DrawHandles and MaterialIndices, parallel arrays built the same way.</summary>
        private static void AppendInts(ref int[] arr, List<int> add)
        {
            if (add.Count == 0) return;
            int oldLen = arr?.Length ?? 0;
            var merged = new int[oldLen + add.Count];
            if (arr           != null) System.Array.Copy(arr, merged, oldLen);
            for (int k = 0; k < add.Count; k++) merged[oldLen + k] = add[k];
            arr = merged;
        }

        /// <summary>Disposes every payload in a dense per-source array (null-slot-safe) — not idempotent
        /// against re-disposing the same reference, since a pooled payload's identity isn't stable after
        /// Dispose() returns. A prologue output's own array is disposed by <see cref="Processing.TilePrologueOutput.Dispose"/> instead.</summary>
        private static void DisposeWholePayloads(Style.MeshDataPayload[] payloads)
        {
            if (payloads == null) return;
            for (int li = 0; li < payloads.Length; li++)
                payloads[li]?.Dispose();
        }

        /// <summary>Releases up to <paramref name="budget"/> queued records this Tick (0 = uncapped). Each
        /// key is re-validated — one back in cover, or already cleared by a restyle, is skipped without spending budget.</summary>
        private void DrainReleaseQueue(int budget)
        {
            if (_releaseQueue.Count == 0) return;

            int cap      = budget > 0 ? budget : int.MaxValue;
            int released = 0;
            while (released < cap && _releaseQueue.Count > 0)
            {
                LoadedKey key = _releaseQueue.Dequeue();
                _releaseQueued.Remove(key);
                // Re-validate: back in cover, or already gone (restyle) → skip without spending budget.
                if (_coverSet.Contains(key.Tile) || !_loaded.ContainsKey(key)) continue;
                ReleaseTile(key);
                released++;
            }

            TilesReleasedLastTick = released;
        }

        /// <summary>Releases a tile: scheduler release, unregister draw items, free meshes. Doesn't wait for
        /// in-flight work — a still-running handle goes into <see cref="_pending"/> so it never leaks.</summary>
        private void ReleaseTile(LoadedKey key)
        {
            RemoveAndTeardownRecord(key, transferToCache: true);

            // Route release to the owning pipeline — a record on source B never touches A; the source-less pipeline is a no-op.
            _sources.ReleaseTile(key.Slot, key.Tile);
            // No symbol-release callback — the subsystem's next CollectLoadedTileKeys pull stops reporting it, and reconcile fades the symbols.
        }

        /// <summary>Drops <paramref name="key"/> from <see cref="_loaded"/> BEFORE tearing its record down —
        /// the one ordering every abandonment path shares. The dictionary value is a copy, so a teardown that
        /// ran first would leave a HUSK behind if it threw. A key already gone is a no-op.</summary>
        /// <param name="transferToCache">Eviction only: hand a Built record's meshes to
        /// <see cref="_prepared"/> first, so teardown finds nothing left to destroy.</param>
        private void RemoveAndTeardownRecord(LoadedKey key, bool transferToCache = false)
        {
            if (!_loaded.Remove(key, out LoadedTile lt)) return;

            // Transfer instead of destroy — scoped to eviction, not restyle (docs/tile-pipeline-design.md).
            // A source-less background record is excluded: a full-tile quad is trivial to rebuild on re-entry.
            if (transferToCache && _cacheEnabled && lt.Built && lt.Meshes != null && !_sources.IsSourceless(key.Slot))
                TransferBuiltMeshesToCache(key.Tile, _sources.SourceIdOf(key.Slot), ref lt);

            RenderTeardownRecord(ref lt);
        }

        /// <summary>Transfers a Built record's meshes into <see cref="_prepared"/>, keyed per layer under
        /// <see cref="CurrentStyle"/>. A dense layerId with no mesh gets a null marker, so the completeness
        /// check (<see cref="ComputeDenseLayerIds"/>) still counts a partially-empty tile as a full cache hit.</summary>
        private void TransferBuiltMeshesToCache(TileId id, string sourceId, ref LoadedTile lt)
        {
            ComputeDenseLayerIds(sourceId, _denseLayerIds);

            int trackedCount = lt.MaterialIndices?.Length ?? 0;
            for (int i = 0; i < trackedCount; i++)
                _prepared.Put(new PreparedKey(CurrentStyle, id, lt.MaterialIndices[i]), lt.Meshes[i]);

            for (int d = 0; d < _denseLayerIds.Count; d++)
            {
                int  layerId = _denseLayerIds[d];
                bool covered = false;
                for (int i = 0; i < trackedCount; i++)
                    if (lt.MaterialIndices[i] == layerId)
                    {
                        covered = true;
                        break;
                    }

                if (!covered)
                    _prepared.Put(new PreparedKey(CurrentStyle, id, layerId), null);
            }

            lt.Meshes          = null;
            lt.MaterialIndices = null;
        }

        /// <summary>Records in <see cref="_loaded"/> not yet <see cref="LoadedTile.Built"/>, recomputed fresh
        /// each call rather than tracked incrementally — a cached counter would need write-back on every mutation site.</summary>
        private int CountActiveLoads()
        {
            int n = 0;
            foreach (var kv in _loaded)
                if (!kv.Value.Built) n++;
            return n;
        }

        /// <summary>Admits one desired key: probes the cache (a hit builds synchronously without occupying
        /// an active slot), else kicks a fetch. Shared by the capped per-Tick gate and the uncapped drain.</summary>
        private void AdmitTile(TileId id, int slot, IProjection projection)
        {
            var key = new LoadedKey(id, slot);

            // The SINGLE projected SW-corner render origin, shared by the mesh bake and the tile transform.
            double3 origin = TileRenderOrigin.Project(id, projection);

            // A source-less record only creates a pending record — the kick moves to PumpPending, riding the same build cap as a real fetch.
            if (_sources.IsSourceless(slot))
            {
                _loaded[key] = new LoadedTile
                {
                    FetchCompleted   = true,
                    Built            = false,
                    TileOriginRender = origin,
                };
                return;
            }

            // Probe PreparedTileCache before kicking a fetch — a full-tile hit skips decode/build/upload and the fetch itself.
            bool allCached = false;
            if (_cacheEnabled)
            {
                ComputeDenseLayerIds(_sources.SourceIdOf(slot), _denseLayerIds);
                // Seed from dense-layer COUNT: a zero-dense source (symbol-only) is never "all cached".
                allCached = _denseLayerIds.Count > 0;
                for (int d = 0; d < _denseLayerIds.Count; d++)
                {
                    if (!_prepared.Contains(new PreparedKey(CurrentStyle, id, _denseLayerIds[d])))
                    {
                        allCached = false;
                        break;
                    }
                }
                // A hit is Built+FetchCompleted and never kicks a symbol build, so a hit whose symbol block is gone
                // (a full-rebuild restyle Clears the store) would lose its labels for good. Require both sides.
                if (allCached) allCached = SymbolsCachedFor(_sources.SourceIdOf(slot), id);
            }

            if (allCached)
            {
                _prepared.Hits++;
                _loaded[key] = BuildTileFromCache(id, origin, _denseLayerIds);
                // A cache HIT re-shows the tile with NO fetch; the symbol subsystem PULLS it back in.
            }
            else
            {
                _prepared.Misses++;
                UniTask<SharedDisposable<IDecodedTile>> fetchReq;
                {
                    using var sSchedReq = PmSchedulerReq.Auto();
                    // .Preserve() allows polling .IsCompleted across frames without exhausting the UniTask.
                    fetchReq = _sources.SourceAt(slot).GetTile(id).Preserve();
                }
                _loaded[key] = new LoadedTile
                {
                    Request          = fetchReq,
                    Built            = false,
                    TileOriginRender = origin,
                };
            }
        }

        /// <summary>Isolated <see cref="Processing.ISymbolTileWorkerFactory.SymbolsCachedFor"/> — a throwing
        /// factory must never fault admission, and a factory that cannot answer is treated as NOT cached, so
        /// the tile re-fetches rather than re-showing label-less.</summary>
        private bool SymbolsCachedFor(string sourceId, TileId id)
        {
            if (SymbolWorkerFactory == null) return true;
            try { return SymbolWorkerFactory.SymbolsCachedFor(sourceId, id); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[TileManager] symbol factory (cache probe) threw for {id}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Admits from the head of <see cref="_desired"/> while the active set stays under
        /// <paramref name="cap"/> — runs every Tick, or a cap reached mid-recompute would stall once <c>_coverGate</c> goes clean.</summary>
        private void AdmitFromDesired(in TilePriorityContext priorityCtx, int cap)
        {
            if (_desired.Count == 0) return;
            if (cap <= 0) cap = int.MaxValue; // convention: 0/negative = uncapped, matching the rate caps

            // Sort before checking capacity — the desired list's head must reflect the latest priority even while saturated.
            if (cap < int.MaxValue) SortDesiredByPriority(priorityCtx);

            int activeCount = CountActiveLoads();
            int admitted    = 0;
            while (admitted < _desired.Count && activeCount < cap)
            {
                LoadedKey key = _desired[admitted];
                AdmitTile(key.Tile, key.Slot, priorityCtx.Projection);
                _desiredSet.Remove(key);
                // A cache hit settles synchronously (Built=true) and never occupies a slot — it does no fetch/build.
                if (!_loaded[key].Built) activeCount++;
                admitted++;
            }

            if (admitted > 0) _desired.RemoveRange(0, admitted);
        }

        /// <summary>Sorts <see cref="_desired"/> in place by the shared priority (helper so call sites read
        /// as intent, not mechanism).</summary>
        private void SortDesiredByPriority(in TilePriorityContext ctx) => _sorter.Sort(_desired, in ctx);

        /// <summary>Builds a fully-Built record directly from a cache hit — TryTakes each layer's mesh and
        /// re-registers the non-null ones. No fetch, decode, build, or upload.</summary>
        private LoadedTile BuildTileFromCache(TileId id, double3 origin, List<int> denseLayerIds)
        {
            _consumeMeshes.Clear();
            _consumeHandles.Clear();
            _consumeMatIndices.Clear();

            for (int d = 0; d < denseLayerIds.Count; d++)
            {
                int layerId = denseLayerIds[d];
                _prepared.TryTake(new PreparedKey(CurrentStyle, id, layerId), out Mesh mesh);
                if (mesh == null) continue; // empty-layer marker — nothing to register

                int handle;
                using (PmAddTileLayer.Auto())
                    handle = _instanced.AddTileLayer(mesh, origin, layerId, id);
                _consumeMeshes.Add(mesh);
                _consumeHandles.Add(handle);
                _consumeMatIndices.Add(layerId);
            }

            // A cache hit only probes at the current revision, so re-stamp it here — a later ReleaseTile must Put() under that same revision.
            var lt = new LoadedTile { Built = true, FetchCompleted = true, TileOriginRender = origin };
            AppendMeshes(ref lt.Meshes, _consumeMeshes);
            AppendInts(ref lt.DrawHandles,     _consumeHandles);
            AppendInts(ref lt.MaterialIndices, _consumeMatIndices);
            return lt;
        }

        /// <summary>Tears down a record's RENDER state: destroys its meshes, unregisters its draw items, and stashes
        /// any in-flight fetch/mesh build in the holding pens. It does not touch the scheduler/cache:
        /// <see cref="ReleaseTile"/> follows with <c>Scheduler.Release</c>, and restyle calls this alone for a kept
        /// source so its cached bytes survive. <see cref="RemoveAndTeardownRecord"/> has already dropped the key
        /// from <see cref="_loaded"/>; <see cref="DoDispose"/> has not. Non-local invariant: every abandonment path
        /// (<see cref="ReleaseTile"/>, <see cref="SetSources"/>, <see cref="DoDispose"/>) reaches a record here, so a
        /// new per-record teardown duty belongs here only.</summary>
        private void RenderTeardownRecord(ref LoadedTile lt)
        {
            // Only release site for the record's decode reference — null the field BEFORE Release(), since a thrown-through field would release twice.
            SharedDisposable<IDecodedTile> decode = lt.Decode;
            lt.Decode = null;
            decode?.Release();

            // If the fetch is still in-flight, stash it for later observation — otherwise it faults unobserved (a console flood).
            if (!lt.FetchCompleted)
            {
                ReleasedMidFetchCount++;
                _pending.StashFetch(lt.Request);
            }

            // An unconsumed PROLOGUE build's result must have its NativeArrays disposed later — only a genuinely in-flight task is counted.
            if (lt.Step == BuildStep.Prologue && !lt.Built)
            {
                if (!lt.MeshBuildTask.IsCompleted)
                    ReleasedMidFlightCount++;
                _pending.StashPrologue(lt.MeshBuildTask);
                lt.MeshBuildTask = default;
            }

            // The graph-arm twin of the seam pen above — excludes a partial tile whose write handle is already complete.
            if ((lt.Step == BuildStep.Measure || lt.Step == BuildStep.Write) && !lt.Built)
            {
                if (!lt.Graph.IsStepComplete)
                    ReleasedMidFlightCount++;
                _pending.StashGraph(lt.Graph);
                lt.Graph = null;
            }

            // Unregister draw items before destroying meshes — one batched call so Entities drops all layer entities in a single structural change.
            if (_instanced != null && lt.DrawHandles != null)
                _instanced.RemoveItems(lt.DrawHandles);

            DestroyTrackedMeshes(ref lt); // Unity does not free a Mesh asset just because nothing references it
        }

        /// <summary>Observes a completed fetch's outcome once and hands the decode handle to the caller, who
        /// now owns it (null on cancel/fault/absent) — the other consumption path is
        /// <see cref="PendingDisposalQueue"/>'s own discard, for a fetch nobody consumes.</summary>
        private SharedDisposable<IDecodedTile> TakeDecodeFromFetch(UniTask<SharedDisposable<IDecodedTile>> req)
        {
            try
            {
                return req.GetAwaiter().GetResult();
            }
            catch (System.OperationCanceledException)
            {
                return null; // tile released mid-fetch — benign cancellation
            }
            catch (Processing.TileDecodeException ex)
            {
                // Before the general arm: a malformed tile faults the same task a 5xx does, so a shared counter would mislabel bad bytes as a fetch failure.
                LogDecodeErrorThrottled(ex);
                return null;
            }
            catch (System.Exception ex)
            {
                LogFetchErrorThrottled(ex);
                return null;
            }
        }

        /// <summary>Bounded fetch-error logging — chaotic input can fail many tiles at once, so a per-tile
        /// warning would just relocate the flood. Surfaces the first error, then a periodic count.</summary>
        private void LogFetchErrorThrottled(System.Exception ex)
        {
            _fetchErrorCount++;
            if (_fetchErrorCount == 1 || (_fetchErrorCount & 63) == 0)
                Debug.LogWarning($"[TileManager] tile fetch failed ({_fetchErrorCount} total): {ex.Message}");
        }

        /// <summary>The decode sibling of <see cref="LogFetchErrorThrottled"/>, with its own counter —
        /// sharing one would let a broken tile fall inside another failure's throttle window and go unseen.</summary>
        private void LogDecodeErrorThrottled(System.Exception ex)
        {
            _decodeErrorCount++;
            if (_decodeErrorCount == 1 || (_decodeErrorCount & 63) == 0)
                Debug.LogWarning($"[TileManager] tile decode failed ({_decodeErrorCount} total): {ex.Message}");
        }

        /// <summary>Destroys every <see cref="Mesh"/> tracked in <paramref name="lt"/>.Meshes — not freed
        /// just because nothing references it — then nulls the field to prevent a double-free.</summary>
        private void DestroyTrackedMeshes(ref LoadedTile lt)
        {
            if (lt.Meshes == null) return;
            for (int i = 0; i < lt.Meshes.Length; i++)
                lt.Meshes[i].DestroySafely(allowDestroyingAssets: true);
            lt.Meshes = null;
        }

        /// <summary>Releases every tile GameObject/Mesh, drains build tasks, and disposes the scheduler and
        /// (if owned) the data source. Does not dispose the RenderLayerSet — MapView disposes it after, since
        /// tile renderers still reference layer materials.</summary>
        protected override void DoDispose()
        {
            // Cancel first, before the pen flush waits on anything — a cancelled build settles in milliseconds instead of blocking.
            _lifetimeCts.Cancel();

            // Every record goes through the single teardown funnel — cancel the fetch first, or the spin below waits on a request only Dispose cancels.
            foreach (var kv in _loaded)
            {
                var lt = kv.Value; // foreach value is read-only; teardown needs a ref to null its Meshes
                if (!lt.FetchCompleted)
                    _sources.ReleaseTile(kv.Key.Slot, kv.Key.Tile);
                RenderTeardownRecord(ref lt);
            }

            // Skipped if the loop above threw, which leaves a torn-down record in _loaded as a husk.
            // Tolerated ONLY because this is terminal: nothing reads _loaded after the manager is disposed.
            _loaded.Clear();

            // The teardown loop above just re-filled the pens — flush AFTER it, or this drains pens that refill and never empty.
            _pending.FlushAll();

            // Destroy every Mesh the PreparedTileCache still holds — SAME ordering as the teardown loop.
            _prepared.Dispose();

            // Dispose the backend AFTER destroying all tile meshes, so it never references a freed Mesh.
            _instanced?.Dispose();
            _instanced = null;

            // After the teardown pass above, which releases fetches through this registry.
            _sources.Dispose();

            // Dispose the CTS LAST — the pen flush above has already completed, so no worker is parked.
            _lifetimeCts.Dispose();
        }
    }
}