using System;
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
using MapRenderer.Unity.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.View;
using MapRenderer.Unity.Common;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Jobs.Fill;
using MapRenderer.Unity.Jobs.Geometry;
using MapRenderer.Unity.Jobs.Tiles;
using BRGBackend = MapRenderer.Unity.Rendering.Backend.BRG;
using EntBackend = MapRenderer.Unity.Rendering.Backend.Entities;
using GOBackend = MapRenderer.Unity.Rendering.Backend.GameObjects;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Rendering.Layers;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>Owns the tile lifecycle for <see cref="Map.MapView"/> — cover→fetch→build→consume→evict
    /// plus disposal &amp; mesh-leak guards, uniform across backends via <see cref="Backend.ITileRenderBackend"/>.</summary>
    internal sealed partial class TileManager : VerifiedDisposable
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
            internal const string InstancedRebuild = "MapRenderer.Tile.InstancedRebuild";
        }

        // Drive the render backend per frame (on Entities this ticks the EG system groups).
        private static readonly ProfilerMarker PmInstancedRebuild =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.InstancedRebuild);

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

        /// <summary>Tile-selection knobs from MapView's serialized fields, read live each <see cref="Update"/> — not snapshotted.</summary>
        public struct TileSelectionConfig
        {
            /// <summary>The framing viewport in pixels — <c>(refH · liveAspect, refH)</c>, not raw live px
            /// (see <see cref="IVisibleTileSelector"/>). Flows into the per-tick <see cref="ViewContext"/>.</summary>
            public double2 FramingViewportPx;

            /// <summary>The active pixel↔ground projection (Web-Mercator or globe). Per-frame view context.</summary>
            public IProjection Projection;

            /// <summary>Per-frame mesh-upload budget — unlike the budgets below, 0 blocks consume rather than uncapping it.</summary>
            public int MaxConsumesPerTick;

            /// <summary>Max tiles admitted per Update (default 2), capping mesh-build fan-out. 0 means uncapped.</summary>
            public int MaxMeshBuildsPerTick;

            /// <summary>Per-frame vertex budget for consume — the mesh that crosses it still finishes. 0 means uncapped.</summary>
            public int MaxVerticesPerTick;

            /// <summary>Per-frame budget of records fully released; records past it stay in <c>_loaded</c> until drained. 0 = uncapped.</summary>
            public int MaxReleasesPerTick;

            /// <summary>Concurrency cap on admitted, not-yet-built records — orthogonal to the rate caps above. ≤0 means uncapped.</summary>
            public int MaxConcurrentTileLoads;

            /// <summary>Which render-space distance ranks not-yet-admitted tiles for loading and <see cref="PumpPending"/>'s build/consume order.</summary>
            public TilePriorityStrategy PriorityStrategy;

            /// <summary>How much of each tile's MVT buffer the fill meshes keep before triangulation. A change starts a new bake revision; a tile already in cover rebuilds in the background and swaps in when ready.</summary>
            public TileBufferClip BufferClip;

            /// <summary>Concurrency cap on in-flight prepared-ahead records, apart from <see cref="MaxConcurrentTileLoads"/>. Clamped to at
            /// least 1, because a prepared record is never cancelled and so the set must stay bounded.</summary>
            public int MaxConcurrentPrepareLoads;

            /// <summary>Seconds a record waits after a network fault before its fetch starts again. Decode errors and absent tiles never retry.</summary>
            public double FetchRetrySeconds;
        }

        /// <summary>Why a record is loaded, with precedence Display, Hold, Bridge, Prepare. A tile in the cover is
        /// <see cref="Display"/>. A shown tile that left the cover but still has a relative in it is <see cref="Hold"/>.
        /// A hidden tile between a Hold and a cover tile is <see cref="Bridge"/>. A tile prepared ahead of the level switch
        /// is <see cref="Prepare"/>: registered hidden and reported to no subsystem. A record with none is released.</summary>
        internal enum TileRole
        {
            Display = 0,
            Prepare,
            Hold,
            Bridge,
        }

        // ── Mesh build payload ──────────────────────────────────────────────────────────────

        /// <summary>Per-tile live record: fetch request, mesh build handle, and build progress (see
        /// <c>docs/job-scheduling-design.md</c>). A default instance carries no source, so every
        /// read needs <see cref="Step"/> == <see cref="BuildStep.Prologue"/>; <see cref="Graph"/> is non-null
        /// only when <see cref="Step"/> is <see cref="BuildStep.Measure"/> or <see cref="BuildStep.Write"/>.</summary>
        internal struct LoadedTile
        {
            public UniTask<SharedDisposable<IDecodedTile>>          Request;
            public bool                                             FetchCompleted; // fetch done; mesh build may be in-flight
            public WorkHandle<Processing.TilePrologueOutput> MeshBuildTask; // default until fetch completes; default after consumed
            public BuildStep                                        Step;           // which build step (if any) is in flight
            public bool                                             Built;          // mesh produced (or definitively absent/failed)

            /// <summary><see cref="_bakeRevision"/> when this tile's build was kicked. A missed stamp only loses a
            /// cache hit, because revisions only increase. Non-local invariant: take the stamp in the same
            /// main-thread step that copies <see cref="_bufferClip"/> into the kick context. A stamp newer than
            /// the captured clip would let stale geometry match the current revision.</summary>
            public int BakeRevision;

            /// <summary>True from the start of a rebuild for a newer <see cref="_bakeRevision"/> until its consume
            /// retires the previous geometry. It stays true on a Built record whose rebuild failed, which keeps
            /// drawing <see cref="OldMeshes"/> and <see cref="OldDrawHandles"/> until release. Non-local invariant: the
            /// consume that registers the new meshes also retires the old ones in the same call, so no frame
            /// shows the tile with neither or with both.</summary>
            public bool Rebaking;

            /// <summary>The previous revision's meshes while <see cref="Rebaking"/>. Destroyed exactly once, by <see cref="RetireOldGeometry"/> or teardown.</summary>
            public Mesh[] OldMeshes;

            /// <summary>The previous revision's draw-item handles, parallel to <see cref="OldMeshes"/>.</summary>
            public int[] OldDrawHandles;

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

            /// <summary>The previous revision's material index per handle while <see cref="Rebaking"/>, parallel to <see cref="OldDrawHandles"/>.
            /// It tells which visibility group each old handle belongs to.</summary>
            public int[] OldMaterialIndices;

            /// <summary>Resumable per-mesh consume index, 0 until consume starts; mid-range means the tile is partially consumed.</summary>
            public int ConsumeCursor;

            /// <summary>The decode handle from the fetch — null before completion, and again once this record stops owning it.</summary>
            public SharedDisposable<IDecodedTile> Decode;

            /// <summary>Why the record is loaded. A prepared-ahead admission starts as <see cref="TileRole.Prepare"/>; <see cref="RecomputeRoles"/> rewrites it.</summary>
            public TileRole Role;

            /// <summary>True from a network fault until the retry starts. The record is not Built, is skipped by the pump, and is not ready.</summary>
            public bool WaitingRetry;

            /// <summary>The <see cref="Update"/> clock reading at which a <see cref="WaitingRetry"/> record fetches again.</summary>
            public double RetryAtSeconds;

            /// <summary>The visibility groups that still hold a payload this record has not consumed, valid only once <see cref="GroupsDerived"/>.</summary>
            public ulong PendingGroups;

            /// <summary>True once a consume call derived <see cref="PendingGroups"/>. Before that a record that is not Built counts as pending for
            /// every group of its source.</summary>
            public bool GroupsDerived;

        }


        /// <summary>Value-equality identity of a resolved source definition — the restyle diff key. Two
        /// sources are "the same" (keep the pipeline, reuse cached bytes) iff their resolved
        /// <c>Url</c>/<c>tiles[]</c>/zoom/scheme/bounds/<c>data</c>/<c>buffer</c>/<c>type</c> match.
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
            public readonly string Bounds; // the VALIDATED GeoBounds joined with ',' — null when no gate
            public readonly string Data;   // canonical `data` text — null when the key is absent
            public readonly string Buffer; // round-trip invariant `buffer` text — null when absent (geojson only)
            public readonly SourceType Type; // the discriminator that selects the factory — see the type doc

            /// <summary>Private, with every parameter required, so <see cref="From"/> is the only way to
            /// mint a key — a defaulted <c>type</c>/<c>data</c> would silently compare equal across the field the diff branches on.</summary>
            private SourceKey(string url, string tiles, int minZoom, int maxZoom, string scheme, string bounds,
                string data, string buffer, SourceType type)
            {
                Type    = type;
                Url     = url;
                Tiles   = tiles;
                MinZoom = minZoom;
                MaxZoom = maxZoom;
                Scheme  = scheme;
                Bounds  = bounds;
                Data    = data;
                Buffer  = buffer;
            }

            /// <summary>Builds the key from a resolved <see cref="SourceDefinition"/> and its VALIDATED
            /// <paramref name="bounds"/> gate — not <c>def.Bounds</c>'s raw array — so a malformed bounds
            /// (no gate) and one that resolves to the same spec-default array (e.g. absent bounds) never
            /// collide onto one key.</summary>
            public static SourceKey From(SourceDefinition def, in GeoBounds bounds = default)
            {
                string tiles      = def.Tiles != null ? string.Join("\n", def.Tiles) : null;
                string boundsText = bounds.HasBounds ? JoinInvariant(bounds) : null;
                string data       = def.Data  != null ? JsonCanonical.Write(def.Data.Raw) : null;
                string buffer     = def.Buffer.HasValue
                    ? def.Buffer.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                return new SourceKey(
                    def.Url, tiles, def.MinZoom, def.MaxZoom, def.Scheme, boundsText, data, buffer, def.Type);
            }

            /// <summary>Joins the four bounds fields with a comma, each formatted round-trip ("R") and
            /// culture-invariant — the current-culture default (<see cref="double.ToString()"/>) renders a
            /// fraction with a comma under e.g. de-DE, colliding with the separator.</summary>
            private static string JoinInvariant(in GeoBounds bounds)
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                return bounds.West.ToString("R", ci) + "," + bounds.South.ToString("R", ci) + "," +
                       bounds.East.ToString("R", ci) + "," + bounds.North.ToString("R", ci);
            }

            public bool Equals(SourceKey o)
                => Type       == o.Type    && Url    == o.Url    && Tiles   == o.Tiles
                   && MinZoom == o.MinZoom && MaxZoom == o.MaxZoom && Scheme == o.Scheme
                   && Bounds  == o.Bounds  && Data   == o.Data    && Buffer == o.Buffer;

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
                    h = h * 31 + (Buffer ?? string.Empty).GetHashCode();
                    h = h * 31 + (int)Type;
                    return h;
                }
            }

            /// <summary>A canonical text form carrying every field <see cref="Equals"/> compares — used to
            /// fold a source's resolved identity into <see cref="Map.MapView"/>'s style token (so a
            /// TileJSON that resolves differently under an otherwise-unchanged style busts the prepared-tile
            /// cache, whose own key carries no source identity).</summary>
            public override string ToString()
                => $"{Type}|{Url}|{Tiles}|{MinZoom}|{MaxZoom}|{Scheme}|{Bounds}|{Data}|{Buffer}";
        }

        /// <summary>The caller's recipe for one source pipeline — a <see cref="CreateSource"/> thunk lets <see cref="SetSources"/> build only new/changed pipelines.</summary>
        internal readonly struct SourceSpec
        {
            public readonly string                          SourceId;
            public readonly SourceKey                       Key;
            public readonly int                             MinZoom;
            public readonly int                             MaxZoom;
            /// <summary>The declared bounds gate; <c>default</c> (<see cref="GeoBounds.HasBounds"/> false)
            /// means no gate.</summary>
            public readonly GeoBounds                       Bounds;
            public readonly System.Func<ITileFeatureSource> CreateSource;

            public SourceSpec(string            sourceId, SourceKey key, int minZoom, int maxZoom,
                System.Func<ITileFeatureSource> createSource, in GeoBounds bounds = default)
            {
                SourceId     = sourceId;
                Key          = key;
                MinZoom      = minZoom;
                MaxZoom      = math.max(maxZoom, 0); // a negative maxzoom is not a zoom level: ServingTile would shift past the tile's zoom
                CreateSource = createSource;
                Bounds       = bounds;
            }
        }

        // ── Injected collaborators (stable for life) ─────────────────────────────────────────
        private readonly RenderLayerSet _layers; // owned by MapView; this reads the ordered render layers

        // Per-source pipeline registry — the per-tile lifecycle is per-(tile, source); see LoadedKey.
        internal readonly SourceRegistry _sources = new();

        /// <summary>The normalized source-id a rendered style layer draws from (null → "").</summary>
        private static string SourceIdOf(StyleLayer layer) => layer?.Source ?? string.Empty;

        /// <summary>Dense global material indices whose style layer's source is <paramref name="sourceId"/> — shared by the kick, cache-transfer, and Update probe.</summary>
        private void ComputeDenseLayerIds(string sourceId, List<int> into)
        {
            into.Clear();
            // Reads the live _layers, not SnapshotLayers() — that would heap-allocate on every probe/release.
            for (int li = 0; li < _layers.Count; li++)
                if (_layers[li] is ITileMeshRenderLayer && SourceIdOf(_layers[li].StyleLayer) == sourceId)
                    into.Add(li);
        }

        /// <summary>Dense global material indices of every <see cref="BackgroundRenderLayer"/> with a live material (a null must never reach AddTileLayer).</summary>
        private void ComputeSourcelessLayerIds(List<int> into)
        {
            into.Clear();
            for (int li = 0; li < _layers.Count; li++)
                if (_layers[li] is BackgroundRenderLayer bg && bg.Material != null)
                    into.Add(li);
        }

        // ── Tile render backend (Entities, BRG, or GameObject) — constructed in SetSources ────
        internal Backend.ITileRenderBackend Instanced { get; private set; } // null only before SetSources / after Dispose

        /// <summary>The launch-time projection, cached each Update — null means WebMercator; it's launch-constant, so any Update's value is correct.</summary>
        private IProjection _projection;

        /// <summary>The live fill tile-buffer clip, cached each Update — unlike <c>_projection</c> it can change at runtime, which is why <see cref="UpdateCore"/> diffs it.</summary>
        private TileBufferClip _bufferClip;

        /// <summary>The bake-parameter generation, bumped whenever <see cref="_bufferClip"/> changes. A kick
        /// stamps it on the record, and <see cref="PreparedKey.Revision"/> carries it into the cache.</summary>
        private int _bakeRevision;

        /// <summary>True while some in-cover record may still hold geometry from an older bake revision. Set by a
        /// revision bump and cleared by <see cref="MarkStaleRecords"/> once every record is current.</summary>
        private bool _rebakePending;
        private IVisibleTileSelector _selector;

        /// <summary>The visible-tile selection seam (default <see cref="FrustumTileSelector"/>), owning the per-tick request/release transition.
        /// Setting it marks the cover stale, so a new selector applies on a still camera.</summary>
        internal IVisibleTileSelector Selector
        {
            get => _selector;
            set
            {
                _selector = value;
                _coverGate.Invalidate();
            }
        }

        // Reused buffers — never reallocated in steady state.
        internal readonly TileSelection _selection = new();

        // Keyed by (tile, source-slot) — one record per (tile, source).
        internal readonly Dictionary<LoadedKey, LoadedTile> _loaded    = new();
        private readonly List<LoadedKey>                   _toRelease = new(32);

        /// <summary>Deferred-release queue — <see cref="Update"/> enqueues records leaving cover; <see cref="DrainReleaseQueue"/> frees up to the per-Update budget.</summary>
        internal readonly Queue<LoadedKey> _releaseQueue = new(64);

        /// <summary>Dedups <c>_releaseQueue</c> and lets <see cref="PumpPending"/> skip an already-condemned record. Pre-sized to avoid a lazy allocation.</summary>
        private readonly HashSet<LoadedKey> _releaseQueued = new(64);

        /// <summary>Keys wanting to load but not yet admitted, kept in priority order (re-sorted each Update by
        /// <see cref="AdmitFromDesired"/>). <c>_desiredSet</c> mirrors this for O(1) membership; both pre-sized.</summary>
        internal readonly List<LoadedKey>    _desired    = new(64);
        private readonly HashSet<LoadedKey> _desiredSet = new(64);

        /// <summary>Prepare keys wanting to load, admitted under their own cap. A key leaves this list when it is admitted,
        /// when its tile leaves the preload set, or when its tile enters the cover and the key moves to <see cref="_desired"/>.</summary>
        internal readonly List<LoadedKey>    _prepareDesired    = new(64);
        private readonly HashSet<LoadedKey> _prepareDesiredSet = new(64);

        /// <summary>The cover's tiles, their ancestors and the keys of the records that serve them. Rebuilt with each cover recompute.</summary>
        internal readonly CoverIndex _coverIndex = new();

        /// <summary>The record keys prepared ahead (P), and the larger set that keeps a finished prepared record loaded (K).
        /// Neither holds a key that already serves a cover tile. Rebuilt with each cover recompute.</summary>
        private readonly HashSet<LoadedKey> _preloadSet = new();
        private readonly HashSet<LoadedKey> _keepSet    = new();

        /// <summary>Scratch for <see cref="RecomputeRoles"/> and <see cref="PartitionPrepareLast"/>; reused.</summary>
        private readonly List<LoadedKey> _roleChanges    = new(32);
        private readonly List<LoadedKey> _prepareScratch = new(32);

        /// <summary>True while an in-flight prepared record outlives its sets, so the next Update checks again once it finishes.</summary>
        private bool _rolePassPending;

        /// <summary>The areas the swap has shown, the groups revealed on them, and the shown tiles each record key serves.</summary>
        private readonly RevealedAreas _areas;

        private readonly List<TileId> _partialTiles = new();
        /// <summary>The order a new tile's layers appear in: render slot to group, and source slot to the groups it fills.</summary>
        private readonly VisibilityGroupMap _groups = new();

        /// <summary>Scratch for <see cref="SwapStep"/>, <see cref="ServiceRetries"/> and <see cref="RecomputeRoles"/>; reused.</summary>
        private readonly List<TileId>    _swapTiles     = new(32);
        private readonly List<TileId>    _swapCovering  = new(32);
        private readonly List<LoadedKey> _retryScratch  = new(8);

        /// <summary>The scene frame of the last <see cref="Update"/>, which <see cref="DrainMeshBuilds"/> places its tiles from.</summary>
        private Backend.SceneFrame _lastSceneFrame;

        /// <summary>True once an <see cref="Update"/> has stored <see cref="_lastSceneFrame"/>.</summary>
        private bool _hasLastSceneFrame;

        /// <summary>The clock and retry cooldown of the current <see cref="Update"/>; the drain reads the last values.</summary>
        private double _nowSeconds;
        private double _fetchRetrySeconds;

        /// <summary>The retry cooldown used when the config gives none: a zero would refetch a failed tile every Update.</summary>
        private const double DefaultFetchRetrySeconds = 10.0;

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

        /// <summary>Handles waiting to be shown, and handles waiting to be hidden, at the next <see cref="FlushVisibility"/>.</summary>
        private readonly VisibilityBatch _visibility = new();

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

        /// <summary>Prepared-cache hit count (telemetry only) — forwards to the cache's own counter, bumped by the Update probe on a hit.</summary>
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
        public TileManager(RenderLayerSet layers, Map.PreparedTileCacheConfig cacheConfig)
        {
            _layers       = layers;
            _cacheEnabled = cacheConfig.Enabled;
            _prepared     = new PreparedTileCache(cacheConfig.ByteBudget, cacheConfig.MaxCount);
            _areas        = new RevealedAreas(_sources);
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
            ClearPreloadState();
            _areas.Clear(); // every record is gone, and the backend is rebuilt below

            // No purge here: CurrentStyle is a content-derived token (see its own doc), so a changed style
            // already partitions to a different token and an unchanged one is safe to keep and reuse.

            // 2. Diff the pipeline registry against the new specs and commit stable slots.
            _sources.Rebuild(specs, HasBackgroundLayer());
            RebuildGroupMaps();

            // 3. Rebuild the backend from the (caller-rebuilt) styled layer set; re-arm cover selection.
            _coverGate.Invalidate();
            BuildBackend(backend);
        }

        /// <summary>The <see cref="Map.View.SetStyle"/> PARTIAL-SURVIVAL entry point — called only
        /// after <see cref="RenderLayerSet.TryRestyleInPlace"/> has patched <c>_layers</c> in place.
        /// Keeps (re-keys) a record whose source pipeline survived instead of tearing it down; see
        /// `docs/tile-pipeline-design.md` for why that is sound and what happens to a removed slot.</summary>
        internal void RestyleSourcesInPlace(IReadOnlyList<SourceSpec> specs, Map.RenderBackend backend)
        {
            int[] slotMap = ApplySourceDiff(specs);
            TeardownRecordsOnDepartedPipelines(slotMap);
            RebuildGroupMaps();
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
                var newKey = new LoadedKey(oldKey.Tile, newSlot);
                _loaded.Remove(oldKey);
                _loaded[newKey] = lt;
            }

            _areas.Recount();

            // Deferred-release/desired bookkeeping is per-tick derived state, invalid against the just-
            // rebuilt slot indexing regardless of what survived — cleared unconditionally, as before.
            _releaseQueue.Clear();
            _releaseQueued.Clear();
            _desired.Clear();
            _desiredSet.Clear();
            ClearPreloadState();
        }

        /// <summary>Sets the order a new tile's layers appear in, and applies it at once. A null or empty list is one group of everything.
        /// An unchanged list costs a comparison and nothing else, so a caller may pass it every frame. A change remaps what is already
        /// revealed, so nothing shown hides and nothing hidden shows early.</summary>
        internal void SetVisibilityGroups(IReadOnlyList<Map.VisibilityGroup> groups)
        {
            if (_groups.SameAs(groups)) return;

            int[] previousGroupOfSlot = _groups.GroupOfSlots;
            _groups.SetKinds(groups);
            RebuildGroupMaps();
            RemapRevealedGroups(previousGroupOfSlot);
        }

        /// <summary>Moves the reveal state to a new group list. A tile revealed whole stays so. A tile revealed group by group keeps exactly the
        /// layers it showed: a new group counts as revealed only when every layer in it was. Every record derives its pending groups afresh at its next
        /// consume, and holds its whole source until then.</summary>
        private void RemapRevealedGroups(int[] previousGroupOfSlot)
        {
            _areas.Remap(_groups, previousGroupOfSlot);

            var keys = new List<LoadedKey>(_loaded.Keys);
            foreach (LoadedKey key in keys)
            {
                LoadedTile lt = _loaded[key];
                lt.GroupsDerived = false;
                _loaded[key]     = lt;
            }
        }

        /// <summary>Resolves every render slot to its visibility group, and every source slot to the groups its layers fill. Slots never
        /// renumber, so only these two maps change when the style or the group list does.</summary>
        private void RebuildGroupMaps()
        {
            _groups.Rebuild(_layers, _sources.Count);
            for (int slot = 0; slot < _sources.Count; slot++)
            {
                if (_sources.IsSourceless(slot)) ComputeSourcelessLayerIds(_denseLayerIds);
                else ComputeDenseLayerIds(_sources.SourceIdOf(slot), _denseLayerIds);
                _groups.SetGroupsOfSource(slot, _groups.GroupsOf(_denseLayerIds));
            }
        }

        /// <summary>Drops the prepared-ahead bookkeeping: its keys are only valid against the slot indexing it was built for. The next cover recompute rebuilds it.</summary>
        private void ClearPreloadState()
        {
            _prepareDesired.Clear();
            _prepareDesiredSet.Clear();
            _coverIndex.ForgetServedKeys();
            _preloadSet.Clear();
            _keepSet.Clear();
            _rolePassPending = false;
        }

        /// <summary><see cref="RestyleSourcesInPlace"/>'s backend step. This method is never reached
        /// with a null <see cref="Instanced"/> — the partial-survival arm only runs after a first, full
        /// <see cref="SetStyle"/> has already called <see cref="SetSources"/> → <see cref="BuildBackend"/> at
        /// least once — but falls back to building one rather than assuming that.</summary>
        private void EnsureBackend(Map.RenderBackend backend)
        {
            if (Instanced == null) { BuildBackend(backend); return; }
            Instanced.SetLayerMaterials(LayerMaterials(_layers), LayerShadowModes(_layers));
        }

        /// <summary>Constructs the tile render backend from the styled layer set (default arm Entities so a
        /// legacy serialized value resolves safely). Disposes any prior backend first (restyle / re-init).</summary>
        private void BuildBackend(Map.RenderBackend backend)
        {
            Instanced?.Dispose();
            _visibility.Clear(); // handles of the disposed backend mean nothing to the new one
            Instanced = backend switch
            {
                Map.RenderBackend.Brg =>
                    new BRGBackend.TileRenderer(LayerMaterials(_layers), LayerShadowModes(_layers)),
                Map.RenderBackend.GameObject =>
                    new GOBackend.TileRenderer(LayerMaterials(_layers), LayerNames(_layers), LayerShadowModes(_layers)),
                _ => new EntBackend.TileRenderer(LayerMaterials(_layers), LayerNames(_layers), LayerShadowModes(_layers)),
            };
            PushLayerDrawGates(); // a fresh backend starts all-visible; seed it before the first item lands
        }

        /// <summary>True iff the current render layers include a <see cref="BackgroundRenderLayer"/> —
        /// the synthetic source-less pipeline slot's admission test.</summary>
        private bool HasBackgroundLayer()
        {
            for (int li = 0; li < _layers.Count; li++)
                if (_layers[li] is BackgroundRenderLayer)
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
        private static List<Material> LayerMaterials(RenderLayerSet layers)
        {
            var mats = new List<Material>(layers.Count);
            for (int i = 0; i < layers.Count; i++) mats.Add(layers[i].Material);
            return mats;
        }

        /// <summary>Per-layer style ids in <see cref="LayerMaterials"/>'s order, so backends can name each entity by style layer.</summary>
        private static List<string> LayerNames(RenderLayerSet layers)
        {
            var names = new List<string>(layers.Count);
            for (int i = 0; i < layers.Count; i++) names.Add(layers[i].StyleLayer?.Id);
            return names;
        }

        /// <summary>Per-layer shadow-cast flags in <see cref="LayerMaterials"/>'s order, so backends share one list.</summary>
        internal static List<UnityEngine.Rendering.ShadowCastingMode> LayerShadowModes(RenderLayerSet layers)
        {
            var modes = new List<UnityEngine.Rendering.ShadowCastingMode>(layers.Count);
            for (int i = 0; i < layers.Count; i++) modes.Add(layers[i].CastShadows);
            return modes;
        }

        /// <summary>Fills <paramref name="into"/> with the (source, tile) membership of every record that has a role, flagged
        /// <see cref="LoadedTileKey.Shown"/> while its geometry is visible. A record with no role is left out, so its symbols depart.
        /// Allocation-free.</summary>
        internal void CollectLoadedTileKeys(List<LoadedTileKey> into)
        {
            into.Clear();
            foreach (var kv in _loaded)
            {
                if (IsCondemned(kv.Key)) continue; // a swapped-out tile's labels end with its tile, not with its release
                into.Add(new LoadedTileKey(_sources.SourceIdOf(kv.Key.Slot), kv.Key.Tile, shown: IsRecordVisible(kv.Key)));
            }
        }

        /// <summary>True iff <paramref name="key"/>'s record serves a revealed tile that has revealed one of the record's own groups: the one
        /// question the symbol side asks about visibility. A source with no mesh layer follows the tile alone.</summary>
        private bool IsRecordVisible(LoadedKey key)
        {
            if (!_areas.IsRecordShown(key)) return false;
            ulong own = _groups.GroupsOfSource(key.Slot); // a source with no mesh layer, symbols only, has no group: its labels follow the tile
            return own == 0 || (_areas.GroupsOf(key) & own) != 0;
        }

        /// <summary>True iff the record waits in the release queue with no role. A swapped-out record keeps its old stored role until its
        /// release drains. <see cref="RecomputeRoles"/> takes a record that regains a role (a swing-back) off the queue.</summary>
        private bool IsCondemned(LoadedKey key) => _releaseQueued.Contains(key);

        /// <summary>Records in <see cref="_loaded"/> with <paramref name="role"/>, recomputed fresh each call.</summary>
        internal int CountByRole(TileRole role)
        {
            int n = 0;
            foreach (var kv in _loaded)
                if (kv.Value.Role == role && !IsCondemned(kv.Key)) n++;
            return n;
        }

        /// <summary>Tiles released while their mesh build was still in-flight. Incremented by <see cref="ReleaseTile"/>.</summary>
        internal int ReleasedMidFlightCount { get; private set; }

        /// <summary>Number of tiles released while their fetch was still in-flight.</summary>
        internal int ReleasedMidFetchCount { get; private set; }

        /// <summary>Times the full cover recompute ran in the most recent <see cref="Update"/>, as opposed to an early-out.</summary>
        internal int CoverRecomputesLastTick { get; private set; }

        /// <summary>Tiles newly started in the most recent <see cref="Update"/> — incremented once
        /// per tile, at its first kick. This is what <see cref="Config.MaxMeshBuildsPerTick"/> bounds.</summary>
        internal int TileBuildsStartedLastTick { get; private set; }

        /// <summary><see cref="Mesh.MeshDataArray"/>s allocated by the graph arm's write step in the most recent pass — one per non-empty layer.</summary>
        internal long MeshDataArraysAllocatedLastKick { get; private set; }

        /// <summary>Sum of layer-mesh vertex counts consumed in the most recent <see cref="Update"/>.</summary>
        internal int VerticesConsumedLastTick { get; private set; }

        /// <summary>Tiles that reached <c>Built</c> in the most recent <see cref="Update"/>.</summary>
        internal int TilesConsumedLastTick { get; private set; }

        /// <summary>Layer meshes uploaded and registered in the most recent <see cref="Update"/> — the per-frame mesh-count budget observable.</summary>
        internal int MeshesConsumedLastTick { get; private set; }

        /// <summary>(Tile, source) records fully released in the most recent <see cref="Update"/>.</summary>
        internal int TilesReleasedLastTick { get; private set; }

        /// <summary><c>SetItemsVisible</c> calls issued by the most recent <see cref="Update"/>: at most one to show and one to hide.</summary>
        internal int VisibilityBatchesLastTick { get; private set; }

        /// <summary>Pull-based telemetry, refreshed at the end of every <see cref="Update"/> whether or not anyone
        /// reads it — never read by the request/release decision. Returned by reference, no copy or boxing
        /// (<c>docs/telemetry-design.md</c>).</summary>
        internal ref readonly TileTelemetrySnapshot Telemetry => ref _telemetry;

        private TileTelemetrySnapshot _telemetry;

        internal TileTelemetrySnapshot CaptureTelemetry()
        {
            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(_selection.Cover, _coverStatsX, _coverStatsY);

            // One shared pass for Pending + ConsumeBacklog — only a completed Write step, unconsumed, counts as backlog.
            int pending = 0;
            int backlog = 0;
            int prologue = 0;
            int graphMeasure = 0;
            int graphWrite = 0;
            int display = 0;
            int preparing = 0;
            int held = 0;
            int bridged = 0;
            foreach (var kv in _loaded)
            {
                LoadedTile lt = kv.Value;
                bool condemned = IsCondemned(kv.Key);
                if (lt.Role == TileRole.Prepare) { if (!condemned) preparing++; continue; }
                if (lt.Role == TileRole.Hold) { if (!condemned) held++; }
                else if (lt.Role == TileRole.Bridge) { if (!condemned) bridged++; }
                else if (!condemned) display++;
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
                VisibleTileCount       = _selection.Cover.Count,
                CoverColumns           = columns,
                CoverRows              = rows,
                SelectionZoom          = maxZ,
                IsMixedZoom            = minZ != maxZ,
                CoverMinZoom           = minZ,
                CoverMaxZoom           = maxZ,
                FractionalZoom         = _coverGate.LastZoom,
                LoadedTileCount        = display,
                PreparingTileCount     = preparing,
                HeldTileCount          = held,
                BridgeTileCount        = bridged,
                VisibilityBatchesLastTick = VisibilityBatchesLastTick,
                PendingTileCount       = pending,
                ConsumeBacklog         = backlog,
                PrologueInFlight       = prologue,
                GraphMeasureInFlight   = graphMeasure,
                GraphWriteInFlight     = graphWrite,
                InFlightFetches        = _sources.TotalInFlight,
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

        /// <summary>
        /// Pushes each layer's visibility to the backend as a per-slot draw gate, so a layer that paints
        /// nothing the framebuffer can show submits no draw item at all. Called beside every
        /// <see cref="RenderLayerSet.ApplyZoom"/> — that is what refreshes the values read here, and
        /// a frame must never render between the two. Kinds with no opacity of their own always draw.
        /// </summary>
        public void PushLayerDrawGates()
        {
            if (Instanced == null) return;
            for (int li = 0; li < _layers.Count; li++)
                Instanced.SetLayerVisible(
                    li, !(_layers[li] is IFadeableRenderLayer fadeable) || fadeable.PaintsSomething);
        }

        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>One frame of the tile loop — a thin shell over <see cref="UpdateCore"/> so telemetry
        /// refreshes after every return from it, the <c>Selector == null</c> early return included (a
        /// dirty-frame-only update would freeze when the map goes still). A throw from <see cref="UpdateCore"/> skips the refresh.
        /// The frame ends with the backend's <c>Rebuild</c>, so every item this Update registered or revealed is placed and drawn this frame.</summary>
        /// <param name="nowSeconds">The clock retries are timed against.</param>
        /// <param name="sceneFrame">The frame's camera-relative origin and rebase, which the backend places every tile from.</param>
        public void Update(CameraProperties cam, TileSelectionConfig cfg, double nowSeconds, in Backend.SceneFrame sceneFrame)
        {
            UpdateCore(cam, cfg, nowSeconds);
            RebuildBackend(in sceneFrame);
            _telemetry = CaptureTelemetry();
        }

        /// <summary>Places every loaded tile from <paramref name="sceneFrame"/> and remembers the frame for <see cref="DrainMeshBuilds"/>.</summary>
        private void RebuildBackend(in Backend.SceneFrame sceneFrame)
        {
            _lastSceneFrame    = sceneFrame;
            _hasLastSceneFrame = true;
            using (PmInstancedRebuild.Auto())
                Instanced?.Rebuild(in sceneFrame);
        }

        private void UpdateCore(CameraProperties cam, TileSelectionConfig cfg, double nowSeconds)
        {
            if (Selector == null) return;

            ResetPerTickCounters();
            _nowSeconds        = nowSeconds;
            _fetchRetrySeconds = cfg.FetchRetrySeconds > 0.0 ? cfg.FetchRetrySeconds : DefaultFetchRetrySeconds;

            // Defensive backstop, not the normal path — cfg.Projection is always non-null here in production.
            _projection = cfg.Projection ?? new WebMercatorProjection(); // cached for the mesh build bake

            // A changed clip window starts a new bake revision, which is what invalidates cached meshes. The
            // Clear() only frees the now-unreachable entries early. In-cover tiles rebuild via MarkStaleRecords.
            if (!cfg.BufferClip.Equals(_bufferClip))
            {
                _bufferClip = cfg.BufferClip;
                _bakeRevision++;
                _rebakePending = true;
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
                CoverRecomputesLastTick = 1; // The full recompute (descent + diff) runs this Update

                // Not `using var` — closed explicitly so CoverSelect attribution excludes admission/pump/drain costs.
                var sCoverSel = PmCoverSelect.Auto();

                // Select through the seam over the per-frame view context.
                ViewContext view = new ViewContext
                {
                    Camera     = cam,
                    ViewportPx = cfg.FramingViewportPx,
                    Projection = cfg.Projection,
                };
                Selector.SelectVisibleTiles(in view, _selection);

                _coverIndex.Rebuild(_selection.Cover, _sources);

                // Merge, not rebuild — newly-covered keys join the desired list; admission below is priority-ordered and capped.
                // Cover tiles a source serves from one maxzoom ancestor share a key, so the merge queues it once.
                for (int i = 0; i < _selection.Cover.Count; i++)
                {
                    TileId id = _selection.Cover[i];
                    foreach (LoadedKey key in ServingKeys(id))
                    {
                        if (_loaded.ContainsKey(key)) continue; // already admitted — untouched (never re-queued)
                        if (_desiredSet.Add(key)) _desired.Add(key);
                    }
                }

                ComputePreloadSets();

                // Drop desired entries that no longer serve a cover tile — an already-admitted record is never touched here.
                for (int i = _desired.Count - 1; i >= 0; i--)
                {
                    LoadedKey dk = _desired[i];
                    if (!_coverIndex.Serves(dk))
                    {
                        _desiredSet.Remove(dk);
                        _desired.RemoveAt(i);
                    }
                }

                MergePrepareDesired();

                // Re-role every record, then queue those left with no role for deferred release.
                RecomputeRoles();

                _coverGate.Commit(in cam, in cfg);

                sCoverSel.Dispose();
            }

            // A prepared record that outlived its sets finishes here, then is released.
            if (_rolePassPending) RecomputeRoles();

            // Runs every Update, clean or dirty — admission is not gated on _coverGate, so entries drain once the camera stills.
            AdmitFromDesired(in priorityCtx, cfg.MaxConcurrentTileLoads);
            AdmitFromPrepareDesired(in priorityCtx, cfg.MaxConcurrentPrepareLoads);
            if (_rebakePending) MarkStaleRecords(cfg.MaxConcurrentTileLoads, cfg.MaxConcurrentPrepareLoads, in priorityCtx);
            PumpPending(cam, cfg.MaxConsumesPerTick, cfg.MaxMeshBuildsPerTick, cfg.MaxVerticesPerTick, in priorityCtx);

            ServiceRetries();
            SwapStep();

            // Drain a budgeted slice of the deferred-release backlog EVERY Update, after admission/pump.
            DrainReleaseQueue(cfg.MaxReleasesPerTick);

            FlushVisibility();
        }

        /// <summary>Zeroes the six per-tick counters — cover recomputes, builds started, tiles/vertices/meshes
        /// consumed, tiles released — at the top of <see cref="UpdateCore"/>. Five are read-only telemetry;
        /// <see cref="TileBuildsStartedLastTick"/> also bounds <see cref="PumpPending"/>'s build cap, over
        /// the whole Update rather than just the pump — a stray charge before <c>PumpPending</c> fails safe
        /// (admits nothing) instead of silently doubling the cap, though no tooth tells which path charged it.</summary>
        private void ResetPerTickCounters()
        {
            // A throw before PumpPending still clears the previous Update's counts, rather than retaining them.
            CoverRecomputesLastTick   = 0;
            TileBuildsStartedLastTick = 0;
            VerticesConsumedLastTick  = 0;
            TilesConsumedLastTick     = 0;
            MeshesConsumedLastTick    = 0;
            TilesReleasedLastTick     = 0;
            VisibilityBatchesLastTick = 0;
        }

        /// <summary>Deterministic drain: blocks until every in-flight fetch and mesh-build task completes and consumes it, so every
        /// loaded tile is built afterward. Test-only. Non-obvious why it stays here: it mirrors <see cref="Update"/>'s frame order and
        /// drives the same private admit, swap and flush steps.</summary>
        internal void DrainMeshBuilds(CameraProperties cam)
        {
            // Only .Projection is read on this uncapped path — cap == int.MaxValue skips the priority sort entirely.
            var admitCtx = new TilePriorityContext(_projection, default, default, default, default);
            AdmitFromDesired(in admitCtx, int.MaxValue);
            AdmitFromPrepareDesired(in admitCtx, int.MaxValue);

            // Collect all unsettled records.
            var unsettled = new List<LoadedKey>(8);
            foreach (var kv in _loaded)
            {
                if (!kv.Value.Built && !kv.Value.WaitingRetry)
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
                    FetchOutcome outcome = TakeDecodeFromFetch(req);
                    if (outcome.Decode != null)
                    {
                        // STORE it, not a bare local — a throw inside the kick must leave `_loaded[key]` recoverable, not lost.
                        lt.Decode = outcome.Decode;
                    }
                    else
                    {
                        // Absent/undecodable/cancelled: nothing to build. A network fault waits for its retry instead.
                        if (outcome.NetworkFault)
                        {
                            lt.WaitingRetry   = true;
                            lt.RetryAtSeconds = _nowSeconds + _fetchRetrySeconds;
                        }
                        else lt.Built = true;
                        _loaded[key] = lt;
                        continue;
                    }
                }

                // (a2) fetch completed but not yet kicked (cap-deferred or observed above) — kick inline; drain ignores per-tick caps.
                if (lt.FetchCompleted && lt.Decode != null && lt.Step == BuildStep.None)
                {
                    lt.MeshBuildTask = KickMeshBuild(lt, id, lt.Decode, sourceId);
                    lt.Step          = BuildStep.Prologue;
                    lt.BakeRevision  = _bakeRevision;
                    // The record keeps its reference — the kick took its own in KickMeshBuild's prologue; lt.Decode stays live until teardown.
                }

                // A source-less record is only created, never kicked — kick inline or it falls invisible to the no-mesh settle below.
                if (lt.FetchCompleted && lt.Step == BuildStep.None && lt.Decode == null &&
                    _sources.IsSourceless(key.Slot))
                {
                    lt.Graph        = KickSourcelessBackground(id, lt.TileOriginRender);
                    lt.Step         = BuildStep.Measure;
                    lt.BakeRevision = _bakeRevision;
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
                    MeshDataPayload[] payloads = lt.Graph.CompleteWriteAndTakePayloads();
                    // lt.Graph is NOT disposed here, FinishConsume is the single disposal site — drain ignores per-frame caps.
                    ConsumeMeshBuild(key, ref lt, payloads, int.MaxValue, int.MaxValue, out _, out _);
                }
                else if (lt.Step == BuildStep.None && !lt.Built)
                {
                    lt.Built = true;
                }

                _loaded[key] = lt;
            }

            ServiceRetries();
            SwapStep();
            FlushVisibility();
            if (_hasLastSceneFrame) RebuildBackend(in _lastSceneFrame);
        }

        /// <summary>Kicks each tile's mesh build then consumes it, bounded by the mesh-build/consume/vertex
        /// budgets — whichever binds first stops it for this Update. Sorted by <paramref name="priorityCtx"/>, the paint-order seam (<c>docs/tile-pipeline-design.md</c>).</summary>
        private int PumpPending(
            CameraProperties cam,
            int              maxConsumesPerTick,
            int              maxMeshBuildsPerTick,
            int              maxVerticesPerTick,
            in TilePriorityContext priorityCtx)
        {
            using var sFetchPoll = PmFetchPoll.Auto();

            // The four consume/build counters reset in ResetPerTickCounters() (once per Update); this field's
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
                if (!kv.Value.Built && !kv.Value.WaitingRetry)
                    _toRelease.Add(kv.Key);
            }

            // Nearest-center-first paint order — the same priority the admission gate uses. Records in the cover go first
            // (stable), so a prepared record never takes a per-tick budget from one.
            _sorter.Sort(_toRelease, in priorityCtx);
            PartitionPrepareLast(_toRelease);

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
                    MeshDataPayload[] payloads = lt.Graph.CompleteWriteAndTakePayloads();

                    bool complete = ConsumeMeshBuild(
                        key, ref lt, payloads, meshBudgetLeft, vertBudgetLeft,
                        out int meshesThisCall, out int vertsThisCall);

                    meshesConsumed           += meshesThisCall;
                    verticesConsumed         += vertsThisCall;
                    MeshesConsumedLastTick   =  meshesConsumed;
                    VerticesConsumedLastTick =  verticesConsumed;
                    if (complete) TilesConsumedLastTick++;
                    else pending++; // tile partially consumed — resume next Update
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
                        // Cap reached — the record keeps its reference until a later Update kicks it.
                        pending++;
                        continue;
                    }

                    // Obtain the symbol worker pass on the main thread, isolated — a throwing factory must never fault the pump — then ride the kick task.
                    Processing.ISymbolTileWorkerPass symbolPass = null;
                    try
                    {
                        if (!lt.Rebaking) symbolPass = SymbolWorkerFactory?.TryBeginBuild(sourceId, id); // a rebake changes no symbol input
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[TileManager] symbol factory (begin-build) threw for {id}: {ex.Message}");
                    }

                    lt.MeshBuildTask = KickMeshBuild(lt, id, lt.Decode, sourceId, symbolPass);
                    lt.Step          = BuildStep.Prologue;
                    lt.BakeRevision  = _bakeRevision;
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

                    lt.Graph        = KickSourcelessBackground(id, lt.TileOriginRender);
                    lt.Step         = BuildStep.Measure;
                    lt.BakeRevision = _bakeRevision;
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
                FetchOutcome outcome = TakeDecodeFromFetch(lt.Request);
                if (outcome.Decode != null)
                {
                    // Retain the handle once per fetch — the fork point where one (source, tile) splits into the mesh and symbol cadences.
                    lt.Decode = outcome.Decode;
                    pending++; // decode awaiting kick
                }
                else if (outcome.NetworkFault)
                {
                    lt.WaitingRetry   = true; // not Built, so not ready: the hold stays until a retry succeeds
                    lt.RetryAtSeconds = _nowSeconds + _fetchRetrySeconds;
                }
                else
                {
                    // Absent / undecodable / cancelled — mark built (nothing to render).
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
                var layer = (ITileMeshRenderLayer)layersSnapshot[li];
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
                var layer = (BackgroundRenderLayer)layersSnapshot[li];
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
        /// until a budget binds or the tile finishes. Returns true when fully consumed, false to resume next Update.</summary>
        private bool ConsumeMeshBuild(
            LoadedKey key, ref LoadedTile lt, MeshDataPayload[] payloads, int meshBudget, int vertBudget,
            out int meshesConsumed, out int vertsConsumed)
        {
            TileId id = key.Tile;
            meshesConsumed = 0;
            vertsConsumed  = 0;

            // A rebake consumes the whole tile in one call, so its old meshes retire in the same frame the new ones appear.
            if (lt.Rebaking) meshBudget = vertBudget = int.MaxValue;

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
                MeshDataPayload payload = payloads[slot];
                cursor++;

                int materialIndex = payload?.MaterialIndex ?? -1;
                int layerVerts    = payload?.VertexCount   ?? 0;

                if (payload == null
                 || (uint)materialIndex >= (uint)currentLayerCount
                 || _layers[materialIndex] is TombstoneRenderLayer)
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
                    handle = Instanced.AddTileLayer(mesh, lt.TileOriginRender, materialIndex, id);
                _consumeMeshes.Add(mesh); // Track for explicit destruction
                _consumeHandles.Add(handle);
                _consumeMatIndices.Add(materialIndex); // Which layerId this mesh belongs to
                meshesConsumed++;
                vertsConsumed += layerVerts;
            }

            lt.ConsumeCursor = cursor;

            // Append this call's new meshes/handles/indices to the tile's arrays — one realloc per partial frame, load time only.
            AppendMeshes(ref lt.Meshes, _consumeMeshes);
            int handlesBefore = lt.DrawHandles?.Length ?? 0;
            AppendInts(ref lt.DrawHandles,     _consumeHandles);
            AppendInts(ref lt.MaterialIndices, _consumeMatIndices);
            bool complete = cursor >= denseCount;
            if (!complete)
            {
                // Derived from what is still unconsumed, so a call that took one of two fills leaves the group pending.
                ulong pending = 0;
                for (int i = cursor; i < denseCount; i++)
                    if (payloads[i] != null) pending |= _groups.GroupBit(payloads[i].MaterialIndex);
                lt.PendingGroups = pending;
                lt.GroupsDerived = true;
            }

            // Only this call's handles: earlier calls already queued theirs. A record follows its tile: its items show for the groups the
            // tile has revealed, and a tile's reveal belongs to the swap step.
            ulong shownGroups = GroupsToShow(key);
            if (_consumeHandles.Count > 0 && shownGroups != 0)
                QueueShowGroups(lt.DrawHandles.AsSpan(handlesBefore), lt.MaterialIndices.AsSpan(handlesBefore), shownGroups);

            if (complete)
            {
                // Dispose the whole payload array (idempotent), freeing layers the active style doesn't render, then mark Built and release the task.
                DisposeWholePayloads(payloads);
                RetireOldGeometry(ref lt);
                FinishConsume(ref lt);
            }

            return complete;
        }

        /// <summary>Marks a tile fully consumed and clears its build state — the single disposal site for
        /// <see cref="LoadedTile.Graph"/>; disposing earlier would leave a partial tile whose null Graph the next Update dereferences.</summary>
        private static void FinishConsume(ref LoadedTile lt)
        {
            lt.Graph?.Dispose();
            lt.Step          = BuildStep.None;
            lt.MeshBuildTask = default;
            lt.Graph         = null;
            lt.Built         = true;
            lt.PendingGroups = 0;
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
        private static void DisposeWholePayloads(MeshDataPayload[] payloads)
        {
            if (payloads == null) return;
            for (int li = 0; li < payloads.Length; li++)
                payloads[li]?.Dispose();
        }

        /// <summary>Releases up to <paramref name="budget"/> queued records this Update (0 = uncapped). A key that is no longer queued, or
        /// whose record is gone, is skipped without spending budget.</summary>
        private void DrainReleaseQueue(int budget)
        {
            if (_releaseQueue.Count == 0) return;

            int cap      = budget > 0 ? budget : int.MaxValue;
            int released = 0;
            while (released < cap && _releaseQueue.Count > 0)
            {
                LoadedKey key = _releaseQueue.Dequeue();
                // A key that left the set regained a role (RecomputeRoles); a key missing from _loaded was torn down (a source left). Neither spends budget.
                if (!_releaseQueued.Remove(key) || !_loaded.ContainsKey(key)) continue;
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
        /// <param name="transferToCache">Eviction only: hand a Built record's current-revision meshes to
        /// <see cref="_prepared"/> first. An older-revision record is destroyed, since no lookup matches it.</param>
        private void RemoveAndTeardownRecord(LoadedKey key, bool transferToCache = false)
        {
            if (!_loaded.Remove(key, out LoadedTile lt)) return;

            // Transfer instead of destroy — scoped to eviction, not restyle (docs/tile-pipeline-design.md).
            // A source-less background record is excluded: a full-tile quad is trivial to rebuild on re-entry.
            if (transferToCache && _cacheEnabled && lt.Built && lt.Meshes != null && lt.BakeRevision == _bakeRevision
                && !_sources.IsSourceless(key.Slot))
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
                _prepared.Put(new PreparedKey(CurrentStyle, id, lt.MaterialIndices[i], lt.BakeRevision), lt.Meshes[i]);

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
                    _prepared.Put(new PreparedKey(CurrentStyle, id, layerId, lt.BakeRevision), null);
            }

            lt.Meshes          = null;
            lt.MaterialIndices = null;
        }

        /// <summary>Records in the cover not yet <see cref="LoadedTile.Built"/>, recomputed fresh each call rather than
        /// tracked incrementally — a cached counter would need write-back on every mutation site.</summary>
        internal int CountActiveLoads() => CountInFlight(prepared: false);

        /// <summary>Prepared-ahead records not yet built: what <see cref="TileSelectionConfig.MaxConcurrentPrepareLoads"/> bounds.</summary>
        private int CountPrepareLoads() => CountInFlight(prepared: true);

        /// <summary>In-flight records that are (<paramref name="prepared"/>) or are not prepared ahead. A Bridge counts with the cover;
        /// a record waiting to retry counts in neither, so a dead source cannot hold every slot.</summary>
        private int CountInFlight(bool prepared)
        {
            int n = 0;
            foreach (var kv in _loaded)
                if (!kv.Value.Built && !kv.Value.WaitingRetry && (kv.Value.Role == TileRole.Prepare) == prepared) n++;
            return n;
        }

        /// <summary>Admits one desired key: probes the cache (a hit builds synchronously without occupying
        /// an active slot), else kicks a fetch. Shared by the capped per-Update gate and the uncapped drain.</summary>
        private void AdmitTile(TileId id, int slot, IProjection projection, TileRole role = TileRole.Display)
        {
            var key = new LoadedKey(id, slot);
            Debug.Assert(!_loaded.ContainsKey(key), "AdmitTile overwrites a record that is already loaded");

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
                    Role             = role,
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
                    if (!_prepared.Contains(new PreparedKey(CurrentStyle, id, _denseLayerIds[d], _bakeRevision)))
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
                LoadedTile restored = BuildTileFromCache(id, origin, _denseLayerIds, role);
                _loaded[key] = restored;
                ShowRecord(key, in restored);
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
                    Role             = role,
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
        /// <paramref name="cap"/> — runs every Update, or a cap reached mid-recompute would stall once <c>_coverGate</c> goes clean.</summary>
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

        /// <summary>Admits prepared-ahead keys while the in-flight prepared records stay under <paramref name="cap"/>,
        /// so preparing never takes a slot from a tile in the cover. Runs every Update and in the drain.</summary>
        private void AdmitFromPrepareDesired(in TilePriorityContext priorityCtx, int cap)
        {
            if (_prepareDesired.Count == 0) return;
            cap = math.max(cap, 1);
            if (cap < int.MaxValue) _sorter.Sort(_prepareDesired, in priorityCtx);

            int active   = CountPrepareLoads();
            int admitted = 0;
            while (admitted < _prepareDesired.Count && active < cap)
            {
                LoadedKey key = _prepareDesired[admitted];
                _prepareDesiredSet.Remove(key);
                admitted++;
                AdmitTile(key.Tile, key.Slot, priorityCtx.Projection, TileRole.Prepare);
                if (!_loaded[key].Built) active++; // a cache hit settles at once and takes no slot
            }

            if (admitted > 0) _prepareDesired.RemoveRange(0, admitted);
        }

        /// <summary>Fills <see cref="_preloadSet"/> (P) and <see cref="_keepSet"/> (K) from the selector's
        /// preload and keep tiles. Both are empty without the prepared-tile cache, which is where a
        /// prepared tile goes when it is released.</summary>
        private void ComputePreloadSets()
        {
            _preloadSet.Clear();
            _keepSet.Clear();
            if (!_cacheEnabled) return;

            foreach (TileId tile in _selection.Preload) AddPreloadKeys(tile, _preloadSet);
            foreach (TileId tile in _selection.Keep) AddPreloadKeys(tile, _keepSet);
        }

        /// <summary>Adds the record key each admitting slot serves <paramref name="tile"/> with, unless that key already serves a cover tile.</summary>
        private void AddPreloadKeys(TileId tile, HashSet<LoadedKey> into)
        {
            foreach (LoadedKey key in ServingKeys(tile))
                if (!_coverIndex.Serves(key)) into.Add(key);
        }

        /// <summary>Drops prepare keys whose tile left P, which includes one that entered the cover (the cover merge already
        /// desired it), then adds a key for each P tile and admitting slot that has no record.</summary>
        private void MergePrepareDesired()
        {
            for (int i = _prepareDesired.Count - 1; i >= 0; i--)
            {
                LoadedKey key = _prepareDesired[i];
                if (!_preloadSet.Contains(key)) // P excludes the cover, so a key that entered it is dropped here too
                {
                    _prepareDesiredSet.Remove(key);
                    _prepareDesired.RemoveAt(i);
                }
            }

            foreach (LoadedKey key in _preloadSet)
            {
                if (_loaded.ContainsKey(key)) continue;
                if (_prepareDesiredSet.Add(key)) _prepareDesired.Add(key);
            }
        }

        /// <summary>True iff the record has a reason to stay loaded, and which. A tile in the cover is shown (Display). A shown tile
        /// that left it keeps a relative in it as a Hold. A hidden tile between a Hold and a cover tile is a Bridge. A tile in P is
        /// prepared, and a prepared record stays while it is unfinished or inside K. An in-flight prepared record is never
        /// cancelled for leaving P.</summary>
        private bool TryResolveRole(in LoadedKey key, in LoadedTile lt, out TileRole role)
        {
            TileId tile = key.Tile;
            if (_coverIndex.Serves(key)) { role = TileRole.Display; return true; }

            bool shown = _areas.IsRecordShown(key); // the record serves a revealed area, built or not
            if (shown && _coverIndex.HasRelativeInCover(tile)) { role = TileRole.Hold; return true; }
            if (!shown && IsBetweenHoldAndCover(tile)) { role = TileRole.Bridge; return true; }

            role = TileRole.Prepare;
            if (_preloadSet.Contains(key)) return true;
            return lt.Role == TileRole.Prepare && ((!lt.Built && !lt.WaitingRetry) || _keepSet.Contains(key));
        }

        /// <summary>Gives every record its role, and one with no role joins the deferred-release queue.
        /// Showing and hiding belong to <see cref="SwapStep"/>. Runs on each cover recompute and after
        /// each swap, and again while a prepared record outlives its sets.</summary>
        private void RecomputeRoles()
        {
            _roleChanges.Clear();
            _toRelease.Clear();
            _rolePassPending = false;
            foreach (var kv in _loaded)
            {
                LoadedTile lt = kv.Value;
                if (!TryResolveRole(kv.Key, in lt, out TileRole role)) _toRelease.Add(kv.Key);
                else if (role != lt.Role || _releaseQueued.Contains(kv.Key)) _roleChanges.Add(kv.Key); // a queued record that regained a role leaves the queue
                else if (role == TileRole.Prepare && !lt.Built && !lt.WaitingRetry && !_preloadSet.Contains(kv.Key) && !_keepSet.Contains(kv.Key))
                    _rolePassPending = true;
            }

            for (int i = 0; i < _roleChanges.Count; i++)
            {
                LoadedKey  key = _roleChanges[i];
                LoadedTile lt  = _loaded[key];
                TryResolveRole(in key, in lt, out TileRole role);
                lt.Role = role;
                _loaded[key] = lt;
                _releaseQueued.Remove(key);
            }

            for (int i = 0; i < _toRelease.Count; i++)
            {
                LoadedKey key = _toRelease[i];
                if (_releaseQueued.Add(key)) _releaseQueue.Enqueue(key);
            }
        }

        /// <summary>Stable partition: records in the cover first, prepared records after, each group keeping its order.</summary>
        private void PartitionPrepareLast(List<LoadedKey> keys)
        {
            _prepareScratch.Clear();
            int write = 0;
            for (int read = 0; read < keys.Count; read++)
            {
                LoadedKey key = keys[read];
                if (_loaded[key].Role == TileRole.Prepare) _prepareScratch.Add(key);
                else keys[write++] = key;
            }

            for (int i = 0; i < _prepareScratch.Count; i++) keys[write++] = _prepareScratch[i];
        }

        /// <summary>Sorts <see cref="_desired"/> in place by the shared priority (helper so call sites read
        /// as intent, not mechanism).</summary>
        private void SortDesiredByPriority(in TilePriorityContext ctx) => _sorter.Sort(_desired, in ctx);

        /// <summary>Builds a fully-Built record directly from a cache hit — TryTakes each layer's mesh and
        /// re-registers the non-null ones, shown only for a <paramref name="role"/> in the cover. No fetch, decode, build, or upload.</summary>
        private LoadedTile BuildTileFromCache(TileId id, double3 origin, List<int> denseLayerIds, TileRole role)
        {
            _consumeMeshes.Clear();
            _consumeHandles.Clear();
            _consumeMatIndices.Clear();

            for (int d = 0; d < denseLayerIds.Count; d++)
            {
                int layerId = denseLayerIds[d];
                _prepared.TryTake(new PreparedKey(CurrentStyle, id, layerId, _bakeRevision), out Mesh mesh);
                if (mesh == null) continue; // empty-layer marker — nothing to register

                int handle;
                using (PmAddTileLayer.Auto())
                    handle = Instanced.AddTileLayer(mesh, origin, layerId, id);
                _consumeMeshes.Add(mesh);
                _consumeHandles.Add(handle);
                _consumeMatIndices.Add(layerId);
            }

            // A cache hit only probes at the current revision, so re-stamp it here — a later ReleaseTile must Put() under that same revision.
            var lt = new LoadedTile { Built = true, FetchCompleted = true, TileOriginRender = origin, BakeRevision = _bakeRevision, Role = role };
            AppendMeshes(ref lt.Meshes, _consumeMeshes);
            AppendInts(ref lt.DrawHandles,     _consumeHandles);
            AppendInts(ref lt.MaterialIndices, _consumeMatIndices);
            return lt;
        }

        /// <summary>The one path by which a record catches up with a tile that is shown: queues its current draw
        /// handles for the next <see cref="FlushVisibility"/>. A record of a tile that is not shown stays hidden, because a
        /// tile's first reveal belongs to <see cref="SwapStep"/>, which checks the tiles around it. Showing a shown item changes nothing.</summary>
        private void ShowRecord(LoadedKey key, in LoadedTile lt)
        {
            ulong shownGroups = GroupsToShow(key);
            if (shownGroups == 0) return;
            QueueShowGroups(lt.DrawHandles, lt.MaterialIndices, shownGroups);
        }

        /// <summary>The visibility groups the items of <paramref name="key"/>'s record show now: those revealed on the tiles it serves, and
        /// none while it waits in the release queue. Non-obvious why it keeps no record of an earlier show: a queued record with a shown tile has no
        /// relative in the cover, so the same Update's swap step conceals that tile, and the hide wins before the flush.</summary>
        private ulong GroupsToShow(LoadedKey key) => IsCondemned(key) ? 0UL : _areas.GroupsOf(key);

        /// <summary>Queues the handles whose layer group is in <paramref name="groups"/> to show.</summary>
        private void QueueShowGroups(ReadOnlySpan<int> handles, ReadOnlySpan<int> materialIndices, ulong groups)
        {
            for (int i = 0; i < handles.Length; i++)
            {
                int materialIndex = i < materialIndices.Length ? materialIndices[i] : -1;
                if ((_groups.GroupBit(materialIndex) & groups) != 0) _visibility.QueueShow(handles[i]);
            }
        }

        /// <summary>Queues a record's current and, while it rebakes, previous draw handles to hide at the next flush.</summary>
        private void HideRecord(in LoadedTile lt)
        {
            _visibility.QueueHide(lt.DrawHandles);
            _visibility.QueueHide(lt.OldDrawHandles);
        }

        // ── Hold and swap: a tile that left the cover stays shown until ready tiles cover its area ──
        // See docs/tile-pipeline-design.md "Hold and swap".

        /// <summary>Reveals <paramref name="tile"/> whole: the swap knows its area is covered, or that nothing shown is near it. Every group
        /// shows at once, never group by group.</summary>
        private void RevealTile(TileId tile)
        {
            if (!_areas.Mark(tile)) return;
            foreach (LoadedKey key in ServingKeys(tile))
                RevealRecordGroups(key, ulong.MaxValue);
        }

        /// <summary>Adds <paramref name="groups"/> to what <paramref name="key"/> shows, and shows the items of those groups when the record exists.</summary>
        private void RevealRecordGroups(LoadedKey key, ulong groups)
        {
            ulong added = _areas.RevealGroups(key, groups);
            if (added == 0 || !_loaded.TryGetValue(key, out LoadedTile lt)) return; // a record that registers later reads the groups at once
            QueueShowGroups(lt.DrawHandles, lt.MaterialIndices, added);
            QueueShowGroups(lt.OldDrawHandles, lt.OldMaterialIndices, added);
        }

        /// <summary>Conceals <paramref name="tile"/>. A record that serves it hides when it serves no other shown tile. The records stay loaded
        /// until their release drains.</summary>
        private void ConcealTile(TileId tile)
        {
            if (!_areas.Remove(tile)) return;
            foreach (LoadedKey key in ServingKeys(tile))
            {
                RecordReveal rest = _areas.Release(key);
                if (rest.Count > 0) continue;
                if (!_loaded.TryGetValue(key, out LoadedTile lt)) continue;
                _areas.NoteConcealed(key, rest.Groups);
                HideRecord(in lt);
            }
        }

        /// <summary>The record key of every slot that admits <paramref name="tile"/>, in slot order. An allocation-free <c>foreach</c> source.</summary>
        private ServingKeyWalk ServingKeys(TileId tile) => new ServingKeyWalk(_sources, tile);

        /// <summary>Starts the fetch again for every record whose network-fault cooldown has passed and that is not queued for release.
        /// The general failure arm also catches a throw after the scheduler cached the bytes, so the retry releases the tile in its source
        /// first: that evicts the cached bytes and makes the request a fresh fetch.</summary>
        private void ServiceRetries()
        {
            _retryScratch.Clear();
            // Non-local invariant: a queued record must not restart, or it counts as having a role while no pass unqueues it and the drain cancels it.
            foreach (var kv in _loaded)
                if (kv.Value.WaitingRetry && _nowSeconds >= kv.Value.RetryAtSeconds && !_releaseQueued.Contains(kv.Key))
                    _retryScratch.Add(kv.Key);

            for (int i = 0; i < _retryScratch.Count; i++)
            {
                LoadedKey  key = _retryScratch[i];
                LoadedTile lt  = _loaded[key];
                _sources.ReleaseTile(key.Slot, key.Tile);
                lt.Request        = _sources.SourceAt(key.Slot).GetTile(key.Tile).Preserve();
                lt.FetchCompleted = false;
                lt.WaitingRetry   = false;
                _loaded[key]      = lt;
            }
        }

        /// <summary>Sends the queued shows and hides to the backend in at most one call each. Runs at the end of <see cref="UpdateCore"/> and of
        /// <see cref="DrainMeshBuilds"/>.</summary>
        private void FlushVisibility() => VisibilityBatchesLastTick += _visibility.Flush(Instanced);

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
            if (Instanced != null && lt.DrawHandles != null)
                Instanced.RemoveItems(lt.DrawHandles);

            RetireOldGeometry(ref lt); // a record released mid-rebake still owns its previous geometry
            DestroyTrackedMeshes(ref lt); // Unity does not free a Mesh asset just because nothing references it
        }

        /// <summary>Observes a completed fetch's outcome once and hands the decode handle to the caller, who
        /// now owns it (null on cancel/fault/absent) — the other consumption path is
        /// <see cref="PendingDisposalQueue"/>'s own discard, for a fetch nobody consumes.</summary>
        private FetchOutcome TakeDecodeFromFetch(UniTask<SharedDisposable<IDecodedTile>> req)
        {
            try
            {
                return new FetchOutcome(req.GetAwaiter().GetResult(), networkFault: false);
            }
            catch (System.OperationCanceledException)
            {
                return default; // tile released mid-fetch — benign cancellation
            }
            catch (Processing.TileDecodeException ex)
            {
                // Before the general arm: a malformed tile faults the same task a 5xx does, so a shared counter would mislabel bad bytes as a fetch failure.
                LogDecodeErrorThrottled(ex);
                return default; // bad bytes: the same bytes fail again, so this is ready-empty and never retried
            }
            catch (System.NotSupportedException ex)
            {
                LogDecodeErrorThrottled(ex);
                return default; // any NotSupportedException from a fetch (an unknown encoding, for one) is deterministic: ready-empty, never retried
            }
            catch (System.Exception ex)
            {
                LogFetchErrorThrottled(ex);
                return new FetchOutcome(null, networkFault: true);
            }
        }

        /// <summary>What a completed fetch gave the record: a decode handle, or nothing. <see cref="NetworkFault"/> is true
        /// only for a failure worth retrying; a cancelled, absent or undecodable tile has none and is ready-empty.</summary>
        private readonly struct FetchOutcome
        {
            public readonly SharedDisposable<IDecodedTile> Decode;
            public readonly bool                           NetworkFault;

            public FetchOutcome(SharedDisposable<IDecodedTile> decode, bool networkFault)
            {
                Decode       = decode;
                NetworkFault = networkFault;
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

        /// <summary>Unregisters and destroys the previous revision's geometry of a rebaking record, then clears
        /// the rebake state. A no-op when the record holds none. Each mesh is destroyed exactly once.</summary>
        private void RetireOldGeometry(ref LoadedTile lt)
        {
            if (Instanced != null && lt.OldDrawHandles != null)
                Instanced.RemoveItems(lt.OldDrawHandles);
            if (lt.OldMeshes != null)
                for (int i = 0; i < lt.OldMeshes.Length; i++)
                    lt.OldMeshes[i].DestroySafely(allowDestroyingAssets: true);
            lt.OldMeshes          = null;
            lt.OldDrawHandles     = null;
            lt.OldMaterialIndices = null;
            lt.Rebaking           = false;
        }

        /// <summary>Starts a rebuild of every in-cover record baked at an older revision, nearest first, while
        /// active loads stay under <paramref name="loadCap"/>. The old geometry keeps drawing until
        /// <see cref="ConsumeMeshBuild"/> swaps it. Clears <see cref="_rebakePending"/> once every record is current.</summary>
        private void MarkStaleRecords(int loadCap, int prepareCap, in TilePriorityContext priorityCtx)
        {
            _toRelease.Clear();
            bool anyOutstanding = false;
            foreach (var kv in _loaded)
            {
                LoadedTile lt = kv.Value;
                if (lt.BakeRevision == _bakeRevision) continue;
                if (!lt.Built)
                {
                    // In flight at an old revision: it becomes Built stale and is caught by a later scan.
                    if (lt.Step != BuildStep.None) anyOutstanding = true;
                    continue;
                }

                // Nothing was baked (absent fetch, no layers): there is no geometry to rebuild. The revision
                // stamp only matters where meshes exist, so the record is left alone (a write would invalidate the enumerator).
                if (lt.Meshes == null && lt.Decode == null) continue;

                anyOutstanding = true;
                if (!_releaseQueued.Contains(kv.Key)) _toRelease.Add(kv.Key);
            }

            _rebakePending = anyOutstanding;
            if (_toRelease.Count == 0) return;

            _sorter.Sort(_toRelease, in priorityCtx);
            PartitionPrepareLast(_toRelease);
            int cap            = loadCap > 0 ? loadCap : int.MaxValue;
            int prepareCapAct  = math.max(prepareCap, 1);
            int active         = CountActiveLoads();
            int activePrepare  = CountPrepareLoads();
            for (int i = 0; i < _toRelease.Count; i++)
            {
                LoadedKey  key = _toRelease[i];
                LoadedTile lt  = _loaded[key];
                bool prepared  = lt.Role == TileRole.Prepare;
                // A clip change never lets a prepared rebake take a slot from one in the cover.
                if (prepared ? activePrepare >= prepareCapAct : active >= cap) continue;
                if (lt.Meshes != null)
                {
                    lt.OldMeshes          = lt.Meshes;
                    lt.OldDrawHandles     = lt.DrawHandles;
                    lt.OldMaterialIndices = lt.MaterialIndices;
                }

                lt.Meshes          = null;
                lt.DrawHandles     = null;
                lt.MaterialIndices = null;
                lt.ConsumeCursor   = 0;
                lt.GroupsDerived   = false; // the rebuild holds every group of its source until a consume says otherwise
                lt.PendingGroups   = 0;
                lt.Built           = false;
                lt.Rebaking        = true;
                if (lt.Decode == null && !_sources.IsSourceless(key.Slot))
                {
                    // A record restored from the cache holds no decode, so it fetches again like a miss.
                    lt.FetchCompleted = false;
                    lt.Request        = _sources.SourceAt(key.Slot).GetTile(key.Tile).Preserve();
                }

                _loaded[key] = lt;
                if (prepared) activePrepare++;
                else active++;
            }
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
            // A pen entry that never settles makes FlushAll throw once every entry is flushed; the rest of teardown still runs.
            try
            {
                _pending.FlushAll();
            }
            finally
            {
                // Destroy every Mesh the PreparedTileCache still holds — SAME ordering as the teardown loop.
                _prepared.Dispose();

                // Dispose the backend AFTER destroying all tile meshes, so it never references a freed Mesh.
                Instanced?.Dispose();
                Instanced = null;

                // After the teardown pass above, which releases fetches through this registry.
                _sources.Dispose();

                // Dispose the CTS LAST — the pen flush above has completed, so no worker is parked (bar a hung one it reported).
                _lifetimeCts.Dispose();
            }
        }
    }
}