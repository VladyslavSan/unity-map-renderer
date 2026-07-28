using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Style;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Common;
using MapRenderer.Jobs;
using BRGBackend = MapRenderer.Unity.Rendering.Backend.BRG;
using EntBackend = MapRenderer.Unity.Rendering.Backend.Entities;
using GOBackend = MapRenderer.Unity.Rendering.Backend.GameObjects;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// Owns the tile lifecycle for <see cref="Map.MapView"/> — the cover→fetch→build→consume→evict
    /// pipeline plus the S48/S51 disposal &amp; mesh-leak guards. Extracted from MapView as the biggest,
    /// most cohesive cut of the decomposition: MapView keeps the camera, the <see cref="Style.RenderLayerSet"/>,
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
    /// The <see cref="Style.RenderLayerSet"/> (render bundles) is stable for the object's life, so it's injected
    /// at construction.</para>
    ///
    /// <para>The consume step (<see cref="ConsumeMeshBuild"/>) registers each tile-layer mesh as a
    /// draw item via the selected <see cref="Backend.ITileRenderBackend"/> (Entities, BRG, or GameObject). The
    /// backend is uniform behind that interface — TileManager has no per-backend branching.</para>
    ///
    /// Clean-room: design follows the MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    internal sealed class TileManager : VerifiedDisposable
    {
        // ── Profiler markers (allocation-free; static readonly = constructed once at type-init) ──
        // Namespace: MapRenderer.* — greppable per S46 acceptance. Names must match exactly
        // (ProfilerMarkerTests asserts the MapRenderer.* string set).

        /// <summary>Profiler marker name constants (SSOT) for the tile-manager path — referenced by the
        /// <see cref="ProfilerMarker"/> fields below and by <c>ProfilerMarkerTests</c> (internal, via
        /// <c>InternalsVisibleTo</c>). Keep the existing hierarchical names so the Profiler flat search groups.</summary>
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

        // A4: "MapRenderer.Tile.Decode" moved to the shared decode handle (the decode's new sole home).
        private static readonly ProfilerMarker PmMeshUpload =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.MeshUpload);

        // Split out from Mesh.Upload: registering the mesh with the backend (Entities = create entity +
        // RenderMeshUtility.AddComponents + EG batch registration; BRG = add a draw item). Separated so a
        // build-time spike is attributable to GPU upload vs ECS structural-change/batch churn.
        private static readonly ProfilerMarker PmAddTileLayer =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.AddTileLayer);

        // S95: brackets the mesh build-kick MeshData allocation loop in KickMeshBuild
        // (main-thread Mesh.AllocateWritableMeshData, one per this-source layer) — measure-first
        // instrumentation so a Profiler trace can attribute cost to the allocate step specifically.
        private static readonly ProfilerMarker PmMeshDataAllocate =
            new(ProfilerCategory.Scripts, ProfilerMarkerNames.MeshDataAllocate);

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
            public double2 FramingViewportPx;

            /// <summary>S71: the active pixel↔ground projection (Web-Mercator today). Per-frame view context.</summary>
            public IProjection Projection;

            /// <summary>S87: per-frame MESH-upload count budget — the max number of tile-layer meshes
            /// uploaded + registered with the backend per Tick (was an S55 per-TILE cap; now per-mesh, since a
            /// single rich tile's layers are consumed resumably across frames). Bounds AddLayer/entity-add and
            /// GPU upload per frame — the responsiveness knob (pair with <see cref="MaxVerticesPerTick"/>;
            /// whichever binds first stops the frame). <b>0 blocks consume entirely</b> (used by tests to build
            /// a backlog) — it is NOT "uncapped"; set it high (e.g. 64) for effectively-uncapped.</summary>
            public int MaxConsumesPerTick;

            /// <summary>S55: max mesh build kick-offs per Tick. Default 2 (MapView serialized field).
            /// Caps the background mesh build fan-out per frame without dropping work.
            /// 0 means uncapped (same as int.MaxValue) so unset config structs are harmless.</summary>
            public int MaxMeshBuildsPerTick;

            /// <summary>S55/S87: per-frame VERTEX budget for consume (S87: per-MESH granularity).
            /// Default 50000. Layer meshes are consumed one at a time until the running vertex total reaches the
            /// budget, then the rest defer to the next Tick; the mesh that crosses the budget is still consumed
            /// (one-MESH overshoot, documented — a single mesh cannot be split). 0 means uncapped.</summary>
            public int MaxVerticesPerTick;

            /// <summary>Stall #2: per-frame budget of (tile, source) RECORDS fully released per Tick (backend
            /// removal + mesh destroy/transfer + scheduler release). A zoom-out/fast-pan otherwise releases the
            /// whole departing cover in one frame — the mirror image of the budgeted consume. Records queued for
            /// release linger in <c>_loaded</c> (still pumped) for a few frames until drained. 0 = uncapped.
            /// Default 4 (mirrors <see cref="MaxConsumesPerTick"/>).</summary>
            public int MaxReleasesPerTick;
        }

        // ── S47 mesh build payload (S51: Task → UniTask) ────────────────────────────────────

        /// <summary>
        /// Mesh build payloads produced by one tile's background mesh build. S89 Stage C: a DENSE array —
        /// one element per render layer bound to THIS task's source, in declared (draw) order. Each payload
        /// carries its own global <see cref="Style.IRenderLayerPayload.MaterialIndex"/>, so the consume
        /// step no longer relies on <c>cursor == materialIndex</c>. Replaces the S83b decision-5c full-width
        /// sparse union (a slot per layer, null for other-source layers) — no dead slots per source.
        /// </summary>
        private struct MeshBuildResult
        {
            /// <summary>Dense per-<c>(tile, source)</c> payloads in draw order; each knows its material index.
            /// Empty (0-vertex) this-source layers still take a slot (freed at consume).</summary>
            public Style.IRenderLayerPayload[] Payloads;
        }

        /// <summary>
        /// Per-tile live record: the in-flight fetch request, the mesh build UniTask, and the built tile
        /// container GameObject.
        ///
        /// S47/S51: the lifecycle is now:
        ///   1. Fetch (Request → UniTask[IDecodedTileHandle] in-flight, stored as .Preserve())
        ///   2. Mesh build kicked (MeshBuildTask in-flight; FetchCompleted = true)
        ///   3. Mesh build consumed (Built = true; MeshBuildTask = default; Go = container)
        ///
        /// Mid-flight release protection: ReleaseTile removes the tile from _loaded immediately,
        /// so PumpPending and DrainMeshBuilds — which iterate _loaded — never visit released
        /// tiles. A tile released while its mesh build is in-flight will never have ConsumeMeshBuild
        /// called for it.
        ///
        /// Note: UniTask is a struct. .Preserve() on the stored UniTask allows polling .IsCompleted
        /// and reading .GetAwaiter().GetResult() only after IsCompleted is true.
        /// </summary>
        private struct LoadedTile
        {
            public UniTask<IDecodedTileHandle> Request;
            public bool                        FetchCompleted; // fetch done; mesh build may be in-flight
            public UniTask<MeshBuildResult>    MeshBuildTask;  // default until fetch completes; default after consumed
            public bool                        HasMeshBuild;   // true when MeshBuildTask is valid
            public bool                        Built;          // mesh produced (or definitively absent/failed)

            /// <summary>S91-C: the tile's SW-corner projected render origin (double3) — the SINGLE bake-and-place
            /// origin shared by the mesh bake and the tile transform. Mercator: (mercX, 0, mercZ); globe: ECEF.</summary>
            public double3 TileOriginRender;

            /// <summary>
            /// S51 leak guard: per-layer Mesh assets created by ConsumeMeshBuild. Must be
            /// explicitly destroyed on release/teardown (Unity does not destroy a Mesh asset just because
            /// nothing references it). Null until the tile is consumed; set by ConsumeMeshBuild.
            /// </summary>
            public Mesh[] Meshes;

            /// <summary>
            /// Backend draw-item handles for each tile-layer mesh registered with the
            /// <see cref="Backend.ITileRenderBackend"/> (Entities, BRG, or GameObject). Set by ConsumeMeshBuild;
            /// used by ReleaseTile to unregister the draw items.
            /// </summary>
            public int[] DrawHandles;

            /// <summary>
            /// S82: parallel to <see cref="Meshes"/> — the global material index (layerId) of each tracked
            /// mesh, in the same order. Set by <see cref="ConsumeMeshBuild"/> alongside Meshes/
            /// DrawHandles; read by ReleaseTile's transfer-to-<see cref="PreparedTileCache"/> path (the cache
            /// key is per layer, so it needs to know which layerId each tracked mesh belongs to).
            /// </summary>
            public int[] MaterialIndices;

            /// <summary>
            /// S87: resumable per-mesh consume cursor — a dense index over this source's mesh build
            /// payloads (draw order), <c>[0..denseCount)</c>. Advanced by
            /// <see cref="ConsumeMeshBuild"/> as the per-frame mesh/vertex budget allows; the tile is
            /// <see cref="Built"/> only once the cursor reaches the end. 0 until consume starts. While
            /// <c>0 &lt; ConsumeCursor &lt; total</c> the tile is partially consumed (HasMeshBuild is
            /// still true, the task is COMPLETE, and some layers are already in Meshes/DrawHandles).
            /// </summary>
            public int ConsumeCursor;

            /// <summary>
            /// S55/A4: the decode-provisioning handle minted by the fetch (Epic A / A7:
            /// <see cref="ITileFeatureSource.GetTile"/>'s result), awaiting a (capped) mesh build kick. Set
            /// when the fetch completes and the per-tick kick cap has been reached. Null in all other states.
            /// Cleared (to null) when KickMeshBuild fires. GC-owned; no special disposal needed on eviction —
            /// a never-kicked entry is simply dropped with the record, no lifetime protocol to run (Q1).</summary>
            public IDecodedTileHandle ReadyDecode;
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

        /// <summary>
        /// S83b: one data pipeline per RENDERED source-id (a source with ≥1 fill/line layer bound to it).
        /// Epic A / A7: the raised <see cref="ITileFeatureSource"/> boundary — byte-fetch, scheduling/
        /// caching, and their independent per-source ownership (each source's bytes/in-flight/negative-cache/
        /// lifetime survive a restyle of <i>other</i> sources) now live INSIDE the feature source
        /// implementation, not here. The <see cref="Slot"/> is the stable index into <see cref="_pipelines"/>
        /// and the per-source component of <see cref="LoadedKey"/>, so the per-frame loop never hashes a
        /// string.
        /// </summary>
        private sealed class SourcePipeline
        {
            public string             SourceId; // the StyleLayer.Source this pipeline serves (normalized, never null)
            public int                Slot;     // index into _pipelines (0..N-1)
            public ITileFeatureSource FeatureSource;
            public int                MinZoom; // resolved source minzoom — admission clamp (decision 10)
            public int                MaxZoom; // resolved source maxzoom
            public SourceKey          DefKey;  // resolved-definition identity — restyle "unchanged?" diff (7a)

            /// <summary>Epic A / A2 (design §B Q1): true for the ONE synthetic pipeline serving every
            /// <see cref="Style.BackgroundRenderLayer"/> — it has no <see cref="FeatureSource"/> to fetch/
            /// decode from, and admits every zoom (<c>MinZoom = int.MinValue, MaxZoom = int.MaxValue</c>).
            /// Appended by <see cref="SetSources"/> after the real (fetching) pipelines take their stable
            /// slots, iff the styled layer set contains ≥1 background layer. Derived from
            /// <see cref="FeatureSource"/> (single source of truth — a real pipeline always has a non-null
            /// source), so the "source-less" state cannot desync.</summary>
            public bool IsSourceless => FeatureSource == null;
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
            public readonly string Tiles; // tiles[] joined with '\n' — cheap value-equality
            public readonly int    MinZoom;
            public readonly int    MaxZoom;
            public readonly string Scheme;
            public readonly string Bounds; // bounds joined with ',' — value-equality (null when default/absent)

            public SourceKey(string url, string tiles, int minZoom, int maxZoom, string scheme, string bounds)
            {
                Url     = url;
                Tiles   = tiles;
                MinZoom = minZoom;
                MaxZoom = maxZoom;
                Scheme  = scheme;
                Bounds  = bounds;
            }

            /// <summary>Builds the key from a resolved <see cref="SourceDefinition"/> (post-S83a resolution).</summary>
            public static SourceKey From(SourceDefinition def)
            {
                string tiles  = def.Tiles  != null ? string.Join("\n", def.Tiles) : null;
                string bounds = def.Bounds != null ? string.Join(",",  def.Bounds) : null;
                return new SourceKey(def.Url, tiles, def.MinZoom, def.MaxZoom, def.Scheme, bounds);
            }

            public bool Equals(SourceKey o)
                => Url        == o.Url     && Tiles  == o.Tiles  && MinZoom == o.MinZoom
                   && MaxZoom == o.MaxZoom && Scheme == o.Scheme && Bounds  == o.Bounds;

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
                    return h;
                }
            }
        }

        /// <summary>
        /// S83b: the caller's (MapView.SetStyle) recipe for one rendered source pipeline. Carries a
        /// <see cref="CreateSource"/> thunk rather than a built <see cref="ITileFeatureSource"/> so
        /// <see cref="SetSources"/> only constructs sources for NEW/CHANGED pipelines — a restyle keeps an
        /// unchanged source's existing instance (and its warm cache), never re-creating it (decision 7a).
        /// Epic A / A7: raised from a byte-fetcher-producing thunk — the byte fetcher is now wrapped INSIDE
        /// this thunk (<c>MapView.BuildSourceSpecs</c>), so <see cref="TileManager"/> never names the byte type.
        /// </summary>
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

        // ── Live state ─────────────────────────────────────────────────────────────────────────────────
        // S83b: per-source pipeline registry (replaces the single _scheduler/_source/_ownsSource). The
        // per-tile lifecycle is per-(tile, source) — see LoadedKey.
        private readonly List<SourcePipeline> _pipelines = new(4);

        /// <summary>S83b: the normalized source-id a rendered style layer draws from (null → "" so it is a
        /// valid Dictionary key and matches a pipeline built for an absent <c>source</c>).</summary>
        private static string SourceIdOf(StyleLayer layer) => layer?.Source ?? string.Empty;

        /// <summary>
        /// S82: the dense (declared-order) GLOBAL material indices whose style layer's source is
        /// <paramref name="sourceId"/> — the same set <see cref="KickMeshBuild"/> builds for one
        /// (tile, source) load. Shared by the kick, the ReleaseTile transfer-to-cache, and the Tick probe so
        /// all three agree on exactly which layerIds make up "this tile's complete prepared set" (a
        /// divergence here would desync the cache's completeness check from what is actually built).
        /// <paramref name="into"/> is cleared first; ONLY ever consumed synchronously within the same call on
        /// the main thread (never captured across an await/thread boundary — see call sites).
        /// </summary>
        private void ComputeDenseLayerIds(string sourceId, List<int> into)
        {
            into.Clear();
            // Reads the LIVE _layers directly (Count + indexer) — NOT SnapshotLayers()/ToArray(). Every
            // caller of this method (the Tick probe, the ReleaseTile transfer) runs synchronously on the
            // main thread, so there is no background-thread race to defend against here (unlike
            // KickMeshBuild's OWN separate SnapshotLayers() call, which IS captured into a background
            // closure and must stay a real copy). SnapshotLayers()/ToArray() would heap-allocate a fresh
            // array on EVERY probe/release — exactly the per-Tick allocation the S95 zero-alloc contract
            // (MapView_SteadyStateTick_DoesNotAllocateGCMemory) forbids.
            // E1: only ITileMeshRenderLayer slots feed the REAL-SOURCE tile produce/consume loop — the
            // symbol slot (FramePlaced) never goes through this path at all, and A2's background slot goes
            // through the separate source-less ComputeSourcelessLayerIds (below) instead (background is
            // TileMesh but not ITileMeshRenderLayer — its geometry comes from a processor, not WriteInto).
            // Filtering HERE (the one shared helper) keeps the kick, the ReleaseTile cache transfer, and the
            // Tick completeness probe in agreement for every REAL source.
            for (int li = 0; li < _layers.Count; li++)
                if (_layers[li] is Style.ITileMeshRenderLayer && SourceIdOf(_layers[li].StyleLayer) == sourceId)
                    into.Add(li);
        }

        /// <summary>
        /// Epic A / A2: the dense (declared-order) GLOBAL material indices of every
        /// <see cref="Style.BackgroundRenderLayer"/> — the source-less sibling of
        /// <see cref="ComputeDenseLayerIds"/>, filtered by TYPE (not by <c>SourceId</c> — background has no
        /// source). <c>&amp;&amp; bg.Material != null</c> is a REAL runtime guard (round-4), not a
        /// <see cref="System.Diagnostics.Debug"/>-only assertion: <see cref="Materials.MapMaterialSet"/>'s
        /// fields are live-mutable, and <c>MapView.SetStyle</c>'s captured-set <c>Validate()</c> only
        /// guarantees non-null AT COMMIT — a later live mutation of <c>FillMaterial</c> to null must still
        /// never reach a backend's <c>AddTileLayer</c> (a null-material slot crash/leak — round-1 HIGH 1),
        /// so this per-tile guard is kept as belt-and-braces alongside the commit-time validation.
        /// <paramref name="into"/> is cleared first; ONLY ever consumed synchronously within the same call on
        /// the main thread (same contract as <see cref="ComputeDenseLayerIds"/>).
        /// </summary>
        private void ComputeSourcelessLayerIds(List<int> into)
        {
            into.Clear();
            for (int li = 0; li < _layers.Count; li++)
                if (_layers[li] is Style.BackgroundRenderLayer bg && bg.Material != null)
                    into.Add(li);
        }

        // ── Tile render backend (Entities, BRG, or GameObject) — constructed in SetSources ────
        private Backend.ITileRenderBackend _instanced; // null only before SetSources / after Dispose

        // S91-C: the launch-time projection, cached each Tick from the TileSelectionConfig so the (possibly
        // off-Tick) mesh build write bakes vertices with the SAME projection the tile origin + scene frame
        // use. null ⇒ WebMercator (planar). Launch-constant, so any Tick's value is correct.
        private IProjection _projection;

        /// <summary>The visible-tile selection seam. Set by MapView (default <see cref="FrustumTileSelector"/>);
        /// a different <see cref="IVisibleTileSelector"/> is a drop-in replacement. This consumer builds a
        /// <see cref="ViewContext"/> per tick and owns the request/release transition — the seam returns just
        /// the set.</summary>
        internal IVisibleTileSelector Selector { get; set; }

        // Reused buffers — never reallocated in steady state.
        private readonly List<TileId> _cover = new(64);

        private readonly HashSet<TileId> _coverSet = new();

        // S83b: keyed by (tile, source-slot) — one record per (tile, source).
        private readonly Dictionary<LoadedKey, LoadedTile> _loaded    = new();
        private readonly List<LoadedKey>                   _toRelease = new(32);

        // Stall #2: deferred-release queue. Tick ENQUEUES (tile, source) records that left the cover instead
        // of releasing the whole departing set in one frame; DrainReleaseQueue frees up to MaxReleasesPerTick
        // per Tick, ABOVE the cover gate so a clean frame still drains the backlog. _releaseQueued dedups
        // across recomputes and lets PumpPending skip kicking a build for a record already condemned. Both are
        // pre-sized and only touched during cover churn — the steady state never allocates here.
        private readonly Queue<LoadedKey> _releaseQueue = new(64);

        // Pre-sized (like _releaseQueue) so the FIRST Add during cover churn doesn't lazily allocate the
        // HashSet's buckets — that first-Add allocation tripped the steady-state zero-GC tooth on a
        // heading-change tick that churns an edge tile.
        private readonly HashSet<LoadedKey> _releaseQueued = new(64);

        // S105/A5b: the symbol-agnostic seam through which the DECOUPLED symbol-label subsystem is driven —
        // TileManager holds only this interface (never a label/store/glyph type). The per-tile mesh KICK
        // (PumpPending) calls TryBeginBuild on the MAIN THREAD, isolated (a throwing factory must never fault
        // the tile pipeline, mirroring ObserveFetchOutcome's per-tile fault isolation), then threads the
        // returned pass into the SAME kick task so the symbol worker runs alongside the mesh pass, sharing
        // the A4 shared-decode entry (no re-fetch, no second decode, no touching the mesh/disposal path). The
        // tile LIFECYCLE (which tiles are loaded → which labels render) is NOT pushed — the subsystem PULLS
        // it via CollectLoadedTileKeys and reconciles (A-1: fragile release/restore callbacks retired).
        internal Processing.ISymbolTileWorkerFactory SymbolWorkerFactory { get; set; }

        // S85: reused TileCoverStats scratch — never reallocated (CaptureTelemetry steady-state no-GC).
        private readonly HashSet<int> _coverStatsX = new();
        private readonly HashSet<int> _coverStatsY = new();

        private bool _coverDirty = true;

        // ── S50/S71: tile-selection key (scalar fields, no boxing) ─────────────────────────────
        // S71: keyed on the FULL camera+viewport inputs the selector reads — fractional zoom, heading, TILT,
        // and framing viewport size all change the covered set now (the old key tracked only integer zoom;
        // tilt was added when the selector became frustum-based — before, tilt was invisible to selection).
        private double _coverKeyLon;
        private double _coverKeyLat;
        private double _coverKeyZoom;
        private double _coverKeyHeading;
        private double _coverKeyTilt;
        private double _coverKeyViewportX;
        private double _coverKeyViewportY;
        private bool   _coverKeyInitialised;

        // S87: reusable scratch for one ConsumeMeshBuild call's newly-built meshes/handles (main-thread
        // only, not re-entrant). Cleared at the start of each call; merged into the tile's arrays at the end.
        // Reused so a partial consume frame doesn't allocate a fresh list per call.
        private readonly List<Mesh> _consumeScratchMeshes = new(8);

        private readonly List<int> _consumeScratchHandles = new(8);

        // S82: parallel to the two above — the global material index (layerId) of each newly-built mesh,
        // so a Built record can later be transferred into PreparedTileCache keyed per layer.
        private readonly List<int> _consumeScratchMatIndices = new(8);

        // S82: reusable scratch for the dense (declared-order) global material indices whose style layer's
        // source is a given sourceId (ComputeDenseLayerIds). ONLY ever consumed synchronously on the main
        // thread within the same call (transfer-to-cache, probe) — never captured across a thread boundary
        // (KickMeshBuild copies it into a fresh int[] before scheduling the background task).
        private readonly List<int> _denseLayerIdsScratch = new(8);

        // ── S82: PreparedTileCache — cache built tile-layer Meshes so a revisit/style-toggle skips
        // re-decode/re-build/re-upload. Owns cached meshes; ReleaseTile transfers a Built tile's meshes
        // here on eviction, the Tick probe TryTakes them back on a revisit. Constructed once, for the life of
        // this TileManager; disposed (destroying every held Mesh) in Dispose(), BEFORE the backend.
        private readonly PreparedTileCache _prepared;

        /// <summary>S82: master toggle (from <see cref="Map.PreparedTileCacheConfig.Enabled"/>), read once at
        /// construction. <see langword="false"/> reverts to pre-S82 behaviour exactly: the Tick probe
        /// (~<see cref="Tick"/>'s request loop) never treats a tile as cached (always fetch/prepare), and
        /// <see cref="ReleaseTile"/> never transfers a Built tile's meshes into <see cref="_prepared"/> (they
        /// are destroyed immediately by <see cref="DestroyTrackedMeshes"/>, as before S82). The cache object
        /// itself still exists but simply stays empty.</summary>
        private readonly bool _cacheEnabled;

        /// <summary>S82: the active style's opaque cache-key token — a constant default until S83's
        /// SetStyle supplies a real stable per-style id. Plain auto-property; MapView sets it in SetStyle.</summary>
        internal StyleToken CurrentStyle { get; set; } = StyleToken.Default;

        /// <summary>S82: prepared-cache hit count (test/telemetry observability; never read from the live
        /// tile loop). Forwards to the cache's own counter — bumped by the Tick probe on a full-tile hit.</summary>
        internal int PreparedCacheHits => _prepared.Hits;

        /// <summary>S82: prepared-cache miss count (see <see cref="PreparedCacheHits"/>).</summary>
        internal int PreparedCacheMisses => _prepared.Misses;

        // ── S48 mid-flight discard holding pen ────────────────────────────────────────────────
        // When a tile is released mid-flight (ReleaseTile while MeshBuildTask is still running),
        // the UniTask has already been captured but not yet produced a result. We cannot dispose the
        // NativeArrays immediately — they don't exist yet. Instead we stash the UniTask here; each
        // Tick drains completed tasks, disposing their NativeArray payloads. Teardown spins to
        // completion and disposes everything remaining.
        //
        // This list is only modified on the main thread (ReleaseTile, DrainPendingDisposal, Dispose
        // are all main-thread). No locking is required.
        private readonly List<UniTask<MeshBuildResult>> _pendingDisposal = new(8);

        // ── S84 mid-flight FETCH holding pen ──────────────────────────────────────────────────────
        // When a tile is released before its fetch completes (rapid zoom/cover churn), the preserved
        // fetch UniTask would otherwise be dropped UNOBSERVED — and a fetch cancelled mid-flight faults
        // (the aborted UnityWebRequest), so UniTask's GC finalizer floods the console with
        // "UnityWebRequestException: Unknown Error". Stash the in-flight fetch here on release; each Tick
        // observes completed ones (and Dispose spins the rest), so every fetch task's outcome is consumed
        // exactly once. Main-thread only, like _pendingDisposal.
        private readonly List<UniTask<IDecodedTileHandle>> _pendingFetchDisposal = new(8);

        // S84: running count of genuine (non-cancellation) fetch errors, for bounded logging.
        private int _fetchErrorCount;

        /// <summary>
        /// S82: <paramref name="cacheConfig"/> supplies the <see cref="PreparedTileCache"/>'s master toggle
        /// and byte/count budget (byte budget primary, count a belt-and-suspenders cap) — MapView passes its
        /// <see cref="Map.MapViewConfig.PreparedCache"/> field through unchanged. Placeholder budget defaults
        /// pending the maintainer's in-editor VRAM profiling (S82 Risk 3) — tunable, not load-bearing for
        /// correctness (a too-small budget just lowers the hit rate). The cache is constructed regardless of
        /// <see cref="Map.PreparedTileCacheConfig.Enabled"/> — disabled just means it is never Put into/probed.
        /// </summary>
        public TileManager(Style.RenderLayerSet layers, Map.PreparedTileCacheConfig cacheConfig)
        {
            _layers       = layers;
            _cacheEnabled = cacheConfig.Enabled;
            _prepared     = new PreparedTileCache(cacheConfig.ByteBudget, cacheConfig.MaxCount);
        }

        // ── Lifecycle / injection ────────────────────────────────────────────────────────────

        /// <summary>
        /// S83b multi-source entry (the <see cref="Map.View.SetStyle"/> path). Applies <paramref name="specs"/>
        /// — one per rendered source-id, already resolved (inline <c>tiles[]</c> or via S83a TileJSON) — as
        /// the pipeline registry, and (re)builds the backend from the current <see cref="RenderLayerSet"/>.
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
        internal void SetSources(IReadOnlyList<SourceSpec> specs, Map.RenderBackend backend)
        {
            // 1. Render-teardown every existing record (NO scheduler release — keep warm caches). The
            //    in-flight tasks land in the S48/S84 pens → disposed, never consumed against the new backend (7d).
            foreach (var kv in _loaded)
            {
                var lt = kv.Value; // foreach value is read-only; teardown needs a ref to null its Meshes
                RenderTeardownRecord(ref lt);
            }

            _loaded.Clear();
            // Stall #2: the records were torn down wholesale above — drop the deferred-release bookkeeping too
            // (a queued key referencing the old layer indexing / backend must not survive a restyle).
            _releaseQueue.Clear();
            _releaseQueued.Clear();

            // S82: purge the PreparedTileCache on EVERY SetSources call (first style AND every restyle) —
            // a restyle rebuilds RenderLayerSet's layer indexing, so a held entry's layerId may no longer
            // denote the same semantic layer, and pre-S83 StyleToken is a constant default shared by every
            // style (so without this, a restyle could get a false HIT serving a prior style's baked
            // geometry under a coincidentally-matching (tileId, layerId)). Destroys every held Mesh.
            _prepared.Clear();

            // 2. Diff the pipeline registry by (SourceId, resolved Key).
            var kept    = new List<SourcePipeline>(specs.Count);
            var keptOld = new HashSet<SourcePipeline>();
            for (int i = 0; i < specs.Count; i++)
            {
                var            spec     = specs[i];
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
                    kept.Add(new SourcePipeline
                    {
                        SourceId = spec.SourceId, FeatureSource = spec.CreateSource(),
                        MinZoom  = spec.MinZoom, MaxZoom        = spec.MaxZoom, DefKey = spec.Key,
                    });
                }
            }

            // Pipeline-teardown the ones no spec kept (removed sources): dispose the feature source — byte
            // fetch + scheduler + cache all live inside it now (Epic A / A7), one call replaces the old
            // scheduler-then-owned-source pair.
            for (int i = 0; i < _pipelines.Count; i++)
            {
                var p = _pipelines[i];
                if (keptOld.Contains(p)) continue;
                p.FeatureSource?.Dispose();
            }

            // Commit the new registry with stable slots 0..N-1.
            _pipelines.Clear();
            for (int i = 0; i < kept.Count; i++)
            {
                kept[i].Slot = i;
                _pipelines.Add(kept[i]);
            }

            // Epic A / A2 (design §B Q1): append the source-less pipeline AFTER the real (fetching)
            // pipelines take their stable slots, iff the styled layer set contains ≥1 background layer. It is
            // stateless (no Scheduler/Source/Cache to diff/reuse), so it is simply dropped and re-appended
            // every SetSources call rather than diffed like the real pipelines above.
            bool hasBackground = false;
            for (int li = 0; li < _layers.Count; li++)
                if (_layers[li] is Style.BackgroundRenderLayer)
                {
                    hasBackground = true;
                    break;
                }

            if (hasBackground)
            {
                _pipelines.Add(new SourcePipeline
                {
                    // Source left null ⇒ IsSourceless is true (computed). The synthetic background pipeline.
                    SourceId = string.Empty, Slot    = _pipelines.Count,
                    MinZoom  = int.MinValue, MaxZoom = int.MaxValue,
                });
            }

            // 3. Rebuild the backend from the (caller-rebuilt) styled layer set; re-arm cover selection.
            _coverDirty          = true;
            _coverKeyInitialised = false;
            BuildBackend(backend);
        }

        /// <summary>S83b: the existing pipeline with this source-id AND matching resolved key, or null.</summary>
        private SourcePipeline FindPipeline(string sourceId, SourceKey key)
        {
            for (int i = 0; i < _pipelines.Count; i++)
            {
                if (_pipelines[i].SourceId == sourceId && _pipelines[i].DefKey.Equals(key))
                {
                    return _pipelines[i];
                }
            }

            return null;
        }

        /// <summary>Constructs the tile render backend from the styled layer set (default arm Entities so a
        /// legacy serialized value resolves safely). Disposes any prior backend first (restyle / re-init).</summary>
        private void BuildBackend(Map.RenderBackend backend)
        {
            _instanced?.Dispose();
            _instanced = backend switch
            {
                Map.RenderBackend.Brg => new BRGBackend.TileRenderer(LayerMaterials(_layers)),
                Map.RenderBackend.GameObject =>
                    new GOBackend.TileRenderer(LayerMaterials(_layers), LayerNames(_layers)),
                _ => new EntBackend.TileRenderer(LayerMaterials(_layers), LayerNames(_layers)),
            };
        }

        /// <summary>The per-layer materials in declared order — the single, FULL-WIDTH list every backend
        /// indexes by <c>materialIndex</c> (<c>index == draw order == material index</c>; no
        /// fills-then-lines flatten). No slot is null when the material set is configured (E2 made symbol
        /// material-bearing, D11; E3 made background material-bearing) — a null entry per-slot is still
        /// possible when that slot's own base material is unassigned, and backends must tolerate it (§3.3).
        /// The tile produce path never emits an <c>AddTileLayer</c> for a non-tile-mesh slot (symbol or
        /// background) either way, since <see cref="ComputeDenseLayerIds"/> filters to
        /// <see cref="Style.ITileMeshRenderLayer"/> slots only — a symbol/background slot's material reaches
        /// a backend's material REGISTRATION here, never its draw path.</summary>
        private static List<Material> LayerMaterials(Style.RenderLayerSet layers)
        {
            var mats = new List<Material>(layers.Count);
            for (int i = 0; i < layers.Count; i++) mats.Add(layers[i].Material);
            return mats;
        }

        /// <summary>The per-layer style ids in the same declared order as <see cref="LayerMaterials"/>, so the
        /// Entities/GameObject backends can name each layer entity after its style layer (e.g. "water") in the
        /// Hierarchy instead of the shared material name.</summary>
        private static List<string> LayerNames(Style.RenderLayerSet layers)
        {
            var names = new List<string>(layers.Count);
            for (int i = 0; i < layers.Count; i++) names.Add(layers[i].StyleLayer?.Id);
            return names;
        }

        // ── Test observability (internal; surfaced to tests through MapViewTestExtensions, not the
        //    production API). TileManager is already an internal type, so these stay close to the state
        //    they read; MapView no longer mirrors them. ───────────────────────────────────────────────

        /// <summary>The in-flight fetch count summed across every source pipeline.</summary>
        internal int InFlightCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _pipelines.Count; i++)
                {
                    // Epic A / A2: the source-less pipeline has no FeatureSource — contributes 0.
                    if (_pipelines[i].IsSourceless) continue;
                    n += _pipelines[i].FeatureSource.InFlightCount;
                }

                return n;
            }
        }

        /// <summary>S83b: number of currently loaded (or loading) <c>(tile, source)</c> RECORDS — what the
        /// per-frame loops iterate. With a single source (N=1) this equals the distinct tile count, so every
        /// existing assertion is preserved.</summary>
        internal int LoadedTileCount => _loaded.Count;

        /// <summary>
        /// A-1 pull surface: fill <paramref name="into"/> with the current loaded <c>(source, tile)</c>
        /// membership — every record in <see cref="_loaded"/> mapped from its pipeline slot to its source-id.
        /// The symbol-label subsystem calls this each frame and reconciles its active/cached label sets against
        /// it (retiring the release/restore push-callbacks). Clears <paramref name="into"/> first; reuses the
        /// caller's list, so it is allocation-free in steady state (no per-frame GC — the S95 zero-alloc
        /// contract). Includes tiles still fetching (not yet built): those simply have no label entry yet, so
        /// reconcile leaves them for the bytes-ready build push.
        /// </summary>
        internal void CollectLoadedTileKeys(List<LoadedTileKey> into)
        {
            into.Clear();
            foreach (var kv in _loaded)
            {
                into.Add(new LoadedTileKey(_pipelines[kv.Key.Slot].SourceId, kv.Key.Tile));
            }
        }

        /// <summary>
        /// Number of tiles released while their mesh build was still in-flight (HasMeshBuild
        /// and not yet Built at the moment of release). Incremented by ReleaseTile. Read by tests
        /// to prove the mid-flight race actually occurred in <c>S51DisposalLeakGuardTests</c>.
        /// </summary>
        internal int ReleasedMidFlightCount { get; private set; }

        /// <summary>S84: number of tiles released while their fetch was still in-flight.</summary>
        internal int ReleasedMidFetchCount { get; private set; }

        /// <summary>
        /// S95: number of times the FULL cover recompute (<see cref="IVisibleTileSelector.SelectVisibleTiles"/>
        /// descent + the request/release diff) actually ran in the most recent <see cref="Tick"/> — as
        /// opposed to an early-out (<c>!_coverDirty &amp;&amp; pending == 0</c>). Reset to 0 at the top of every
        /// Tick, set when the recompute block runs. A proxy for "how often is the expensive per-frame select
        /// tax actually paid" — lets a test/reviewer sum this across N sub-tile camera nudges instead of
        /// eyeballing a Profiler trace. Mirrors the <c>*LastTick</c> pattern below.
        /// </summary>
        internal int CoverRecomputesLastTick { get; private set; }

        /// <summary>S55: mesh build kicks issued in the most recent PumpPending call.
        /// Exposed for tests; never call from production code.</summary>
        internal int MeshBuildsKickedLastTick { get; private set; }

        /// <summary>S55/S87: sum of layer-mesh vertex counts consumed in the most recent PumpPending call.</summary>
        internal int VerticesConsumedLastTick { get; private set; }

        /// <summary>S55/S87: number of tiles that reached <c>Built</c> (fully consumed) in the most recent
        /// PumpPending call. With S87's per-mesh consume a tile may take several ticks to complete.</summary>
        internal int TilesConsumedLastTick { get; private set; }

        /// <summary>S87: number of layer MESHES uploaded + registered in the most recent PumpPending call —
        /// the per-frame mesh-count budget observable (bounds AddLayer / entity-add and GPU upload per frame).</summary>
        internal int MeshesConsumedLastTick { get; private set; }

        /// <summary>Stall #2: (tile, source) records fully released in the most recent
        /// <see cref="DrainReleaseQueue"/> call — the per-frame release-budget observable.</summary>
        internal int TilesReleasedLastTick { get; private set; }

        /// <summary>Stall #2: current deferred-release backlog depth (records that left cover and await
        /// <see cref="DrainReleaseQueue"/>).</summary>
        internal int ReleaseQueueDepth => _releaseQueue.Count;

        /// <summary>
        /// S85: pull-based runtime telemetry — the caller asks, this computes on demand (never published per
        /// tick). Observability only: referenced ONLY from here and the debug readout panel — never from
        /// <see cref="Tick"/>'s request/release decision, the selector, or <c>ReleaseTile</c>.
        ///
        /// <see cref="TileTelemetrySnapshot.FractionalZoom"/> reads <see cref="_coverKeyZoom"/> — the last
        /// Tick's <c>cam.Zoom</c> — rather than a dedicated field: it is already cached for the cover-key
        /// comparison and is current on every clean or dirty frame alike (a clean frame's cached value equals
        /// the live one by definition of "clean").
        /// </summary>
        /// <summary>
        /// This provider's own levels, owned as a field and handed out BY REFERENCE — no copy, no boxing
        /// (<c>docs/telemetry-design.md</c> §3). The tile manager owns every number in
        /// <see cref="TileTelemetrySnapshot"/>, so it produces them itself rather than exposing counters for
        /// someone else to assemble.
        ///
        /// <para>Unlike the two label providers — whose levels their pass already stores, making the refresh a
        /// repackage — this one derives two of its numbers: the cover-stats pass and the walk of <c>_loaded</c> in
        /// <see cref="CaptureTelemetry"/>. It does them once per <see cref="Tick"/> regardless of whether anyone
        /// reads. Both are bounded by the cover plus its pad ring (tens of records) over reused scratch arrays, so
        /// the cost is not worth a staleness flag to dodge; if it ever shows in a profile, the
        /// <c>MapRenderer.Tiles.*</c> counters are how you would see it.</para>
        /// </summary>
        internal ref readonly TileTelemetrySnapshot Telemetry => ref _telemetry;

        private TileTelemetrySnapshot _telemetry;

        internal TileTelemetrySnapshot CaptureTelemetry()
        {
            var (columns, rows, minZ, maxZ) = TileCoverStats.Compute(_cover, _coverStatsX, _coverStatsY);

            // One shared pass over _loaded for Pending + ConsumeBacklog (S85 decision — reuse the loop, not
            // two). ConsumeBacklog mirrors PumpPending's consume-entry condition plus the
            // explicit !Built this method needs (that loop only ever sees _toRelease's !Built subset; this
            // one walks every record, built or not).
            int pending = 0, backlog = 0;
            foreach (var kv in _loaded)
            {
                LoadedTile lt = kv.Value;
                if (lt.Built) continue;
                pending++;
                if (lt.FetchCompleted && lt.HasMeshBuild && lt.MeshBuildTask.Status.IsCompleted())
                    backlog++;
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
                FractionalZoom         = _coverKeyZoom,
                LoadedTileCount        = _loaded.Count,
                PendingTileCount       = pending,
                ConsumeBacklog         = backlog,
                InFlightFetches        = InFlightCount,
                ReleasedMidFlightCount = ReleasedMidFlightCount,
                ReleasedMidFetchCount  = ReleasedMidFetchCount,
                FetchErrorCount        = _fetchErrorCount,

                // S82: PreparedTileCache utilization — read straight off the live cache (Count/BytesHeld
                // take its internal lock but allocate nothing; Hits/Misses/Evictions are plain int reads).
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

        /// <summary>
        /// Invalidates the cached cover-selection key so the next <see cref="Tick"/> re-selects the cover.
        /// Called when the camera is re-wired (<see cref="Map.View.SetCamera"/>).
        /// </summary>
        public void InvalidateCover() => _coverKeyInitialised = false;

        /// <summary>
        /// Rebuilds the per-tile object-to-world transforms (and refreshes backend state) for all loaded
        /// tiles from <paramref name="frame"/> (S52/S91-C camera-relative rendering: the scene frame tracks the
        /// look-at). Called by MapView once per frame.
        /// </summary>
        public void InstancedRebuild(in Backend.SceneFrame frame)
        {
            _instanced?.Rebuild(frame);
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
        /// S47/S51: returns false while any tile has a pending mesh build.
        /// </summary>
        internal bool AllTilesSettled()
        {
            foreach (var kv in _loaded)
            {
                if (!kv.Value.Built)
                    return false;
            }

            return true;
        }

        // ── The live loop ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One frame of the tile loop. Allocation-free in steady state.
        ///
        /// S47/S51: no main-thread mesh build. PumpPending only KICKS background UniTasks
        /// on fetch completion; ConsumeMeshBuilds polls and CONSUMES completed UniTasks
        /// (uploads mesh + creates GameObjects). Neither step blocks on mesh build.
        ///
        /// The caller (MapView) runs <see cref="RenderLayerSet.ApplyZoom"/> and refreshes the scene
        /// origin BEFORE this — the origin is passed in so tile placement and camera sync share it.
        ///
        /// <para>A thin shell over <see cref="TickCore"/> so telemetry refreshes on EVERY exit path — the
        /// clean-cover tick returns early, and a level that only updates on dirty frames would be a readout
        /// that freezes exactly when the map goes still.</para>
        /// </summary>
        public void Tick(CameraProperties cam, TileSelectionConfig cfg)
        {
            TickCore(cam, cfg);
            _telemetry = CaptureTelemetry();
        }

        private void TickCore(CameraProperties cam, TileSelectionConfig cfg)
        {
            if (Selector == null) return;

            CoverRecomputesLastTick = 0; // S95: reset each Tick; set below only if the full recompute runs

            _projection = cfg.Projection; // cache for the mesh build bake (Level-1); same projection as origin + frame

            if (!_coverKeyInitialised                         ||
                cam.LookAt.Longitude    != _coverKeyLon       ||
                cam.LookAt.Latitude     != _coverKeyLat       ||
                cam.Zoom                != _coverKeyZoom      ||
                cam.Heading.Degrees     != _coverKeyHeading   ||
                cam.Tilt.Degrees        != _coverKeyTilt      ||
                cfg.FramingViewportPx.x != _coverKeyViewportX ||
                cfg.FramingViewportPx.y != _coverKeyViewportY)
            {
                _coverDirty = true;
            }

            // S48: drain any completed mid-flight-discard tasks so their NativeArrays are freed.
            DrainPendingDisposal();
            // S84: observe any completed mid-flight-released fetch tasks (no unobserved-exception flood).
            DrainPendingFetchDisposal();

            // PumpPending always runs (above) so pending tiles keep progressing every frame. The EXPENSIVE
            // cover recompute below (select descent + request/release diff) is gated on _coverDirty ALONE:
            // a clean camera with tiles still pending must NOT re-run the descent — the cover set is
            // unchanged, so the request/release loops would be pure no-ops that just re-tax the frame the
            // consume is already loading (stall #6). PumpPending's pending count is no longer part of the gate.
            PumpPending(cam, cfg.MaxConsumesPerTick, cfg.MaxMeshBuildsPerTick, cfg.MaxVerticesPerTick);

            if (!_coverDirty)
            {
                // Clean tick: the cover is unchanged, so _coverSet is still current — drain a budgeted slice of
                // the deferred-release backlog (re-validated against it, stall #2) and skip the expensive
                // recompute (stall #6). The drain runs EVERY frame, so a zoom-out backlog keeps whittling down
                // even while the camera sits still.
                DrainReleaseQueue(cfg.MaxReleasesPerTick);
                return;
            }

            CoverRecomputesLastTick = 1; // S95: the full recompute (descent + diff) runs this Tick

            // NOT `using var` — closed explicitly before DrainReleaseQueue at the end so the release cost is
            // not mis-attributed to the CoverSelect marker (the drain must run AFTER _coverSet is updated below
            // so its pan-back re-validation sees this frame's cover).
            var sCoverSel = PmCoverSelect.Auto();

            // S71: select through the seam. Build the per-frame view context (camera + framing viewport +
            // active projection); the request/release transition below is unchanged (instant swap).
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
                        // S91-C: the SINGLE projected SW-corner render origin — shared by the mesh bake
                        // (threaded into WriteInto) and the tile transform. Mercator: (mercX, 0, mercZ) ==
                        // MercatorBounds().min bit-for-bit, so placement is unchanged from the pre-S91 path.
                        double3 origin = TileRenderOrigin.Project(id, cfg.Projection);

                        // Epic A / A2 (design §B Q1, HIGH 2): a source-less (background) record only CREATES
                        // a pending record here — it does NOT kick the mesh build inline. The kick moves to
                        // PumpPending (below) so it rides the SAME MaxMeshBuildsPerTick cap as a real fetch's
                        // kick, instead of one synchronous cover-wide burst on every cover/zoom transition.
                        // From here the EXISTING PumpPending consume / ReleaseTile eviction / teardown handle
                        // this record with no further new code — no cache probe (source-less records skip
                        // PreparedTileCache transfer entirely, design §B Lifecycle).
                        if (p.IsSourceless)
                        {
                            _loaded[key] = new LoadedTile
                            {
                                FetchCompleted   = true,
                                Built            = false,
                                TileOriginRender = origin,
                            };
                            continue;
                        }

                        // S82: probe the PreparedTileCache BEFORE kicking a fetch — a full-tile hit (every
                        // dense layerId of this (tile, source) present in the cache) skips decode/build/
                        // upload AND the fetch itself; the feature source's own byte-level cache is simply
                        // never consulted on a hit. Disabled (_cacheEnabled == false) skips the probe
                        // entirely — every tile is treated as a miss, reverting to pre-S82 always-fetch/
                        // -prepare behaviour.
                        bool allCached = false;
                        if (_cacheEnabled)
                        {
                            ComputeDenseLayerIds(p.SourceId, _denseLayerIdsScratch);
                            // Seed from dense-layer COUNT, not an unconditional true: a source with zero dense
                            // mesh layers (a symbol-only source) has nothing in the prepared cache to hit, and
                            // the loop below never runs to falsify it — so an unconditional `true` made
                            // `allCached` vacuously true on every cover entry, sending the tile down
                            // BuildTileFromCache forever (no fetch, no kick), so its labels never built with the
                            // cache on (the default). A zero-dense source is never "all cached" — it must fetch.
                            allCached = _denseLayerIdsScratch.Count > 0;
                            for (int d = 0; d < _denseLayerIdsScratch.Count; d++)
                            {
                                if (!_prepared.Contains(new PreparedKey(CurrentStyle, id, _denseLayerIdsScratch[d])))
                                {
                                    allCached = false;
                                    break;
                                }
                            }
                        }

                        if (allCached)
                        {
                            _prepared.Hits++;
                            _loaded[key] = BuildTileFromCache(id, origin, _denseLayerIdsScratch);
                            // S105/A-1: a cache HIT re-shows the tile with NO fetch (so no bytes-ready). The
                            // symbol subsystem now PULLS this tile back into its loaded set and reconciles —
                            // restoring its kept-warm labels — instead of us pushing a restore callback here.
                        }
                        else
                        {
                            _prepared.Misses++;
                            UniTask<IDecodedTileHandle> fetchReq;
                            {
                                using var sSchedReq = PmSchedulerReq.Auto();
                                // .Preserve() allows polling .IsCompleted across frames without exhausting the UniTask.
                                fetchReq = p.FeatureSource.GetTile(id).Preserve();
                            }
                            _loaded[key] = new LoadedTile
                            {
                                Request          = fetchReq,
                                Built            = false,
                                TileOriginRender = origin,
                            };
                        }
                    }
                }
            }

            // Stall #2: records whose tile left the cover are ENQUEUED for deferred release, not freed here.
            // DrainReleaseQueue (above, every frame) frees up to MaxReleasesPerTick of them per Tick.
            // _releaseQueued dedups a record already queued by an earlier recompute.
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

            _coverKeyLon         = cam.LookAt.Longitude;
            _coverKeyLat         = cam.LookAt.Latitude;
            _coverKeyZoom        = cam.Zoom;
            _coverKeyHeading     = cam.Heading.Degrees;
            _coverKeyTilt        = cam.Tilt.Degrees;
            _coverKeyViewportX   = cfg.FramingViewportPx.x;
            _coverKeyViewportY   = cfg.FramingViewportPx.y;
            _coverKeyInitialised = true;

            _coverDirty = false;

            sCoverSel.Dispose(); // close the CoverSelect marker BEFORE the release drain (correct attribution)

            // Stall #2: drain a budgeted slice of the deferred-release backlog AFTER the recompute updated
            // _coverSet — so the re-validation sees this frame's cover (a tile that just re-entered on a
            // pan-back is skipped/kept, not destroyed-and-refetched) — and the departures this recompute just
            // enqueued start freeing this same frame, bounded to MaxReleasesPerTick.
            DrainReleaseQueue(cfg.MaxReleasesPerTick);
        }

        /// <summary>
        /// S47 deterministic drain — blocks the calling thread until all in-flight fetch and
        /// mesh build UniTasks complete, then consumes their results synchronously (uploads meshes +
        /// creates GameObjects). After this returns, <see cref="AllTilesSettled()"/> is guaranteed
        /// true for all currently loaded tiles.
        ///
        /// This is a full drain: it handles tiles at any stage of the pipeline:
        ///   (a) Fetch in-flight: spins until the fetch UniTask completes, then kicks mesh build inline.
        ///   (b) Mesh build in-flight: spins until the UniTask completes, then consumes inline.
        ///   (c) Neither (tile not yet fetched): marks Built=true (nothing to do).
        ///
        /// Safe: both fetch and mesh build UniTasks use configureAwait: false (UniTask.RunOnThreadPool),
        /// so they complete on the ThreadPool and IsCompleted becomes true without needing the Unity
        /// PlayerLoop to advance. Spinning on IsCompleted from the main thread therefore does not
        /// deadlock (no PlayerLoop dependency to dead-end on).
        ///
        /// Called by test helpers for deterministic settle. NOT called from the production Update path.
        /// </summary>
        internal void DrainMeshBuilds(CameraProperties cam)
        {
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
                string     sourceId = _pipelines[key.Slot].SourceId;
                LoadedTile lt       = _loaded[key];

                // (a) If fetch is still in-flight, spin until it completes and kick mesh build.
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
                    // faulted fetch is never dropped unobserved. Epic A / A7: the mint (bytes → handle) now
                    // happens INSIDE ITileFeatureSource.GetTile; this just observes the handle it returned.
                    IDecodedTileHandle handle = ObserveFetchOutcome(req, logErrors: true);
                    if (handle != null)
                    {
                        // Kick mesh build synchronously (wait inline). A4: mint inline, single-consumer — no
                        // push here today, none added. A5b: symbolPass is left at its default (null) — the
                        // drain path stays symbol-silent BY CONSTRUCTION (§Q-Drain; symbolPass is computed
                        // only at the PumpPending kick site).
                        var tessTask = KickMeshBuild(lt, id, handle, sourceId);
                        lt.HasMeshBuild  = true;
                        lt.MeshBuildTask = tessTask;
                    }
                    else
                    {
                        // Absent/failed/cancelled fetch — nothing to build.
                        lt.Built     = true;
                        _loaded[key] = lt;
                        continue;
                    }
                }

                // (a2) S55: fetch completed with data but mesh build not yet kicked (cap-deferred in
                // normal pump). Kick inline here — drain ignores per-tick caps. A5b: symbolPass left at its
                // default (null) — a cap-deferred tile that settles via drain never attempts a symbol build
                // either (§Q-Drain KEEP), matching the drain path's existing symbol-silent behaviour.
                if (lt.FetchCompleted && lt.ReadyDecode != null && !lt.HasMeshBuild)
                {
                    lt.MeshBuildTask = KickMeshBuild(lt, id, lt.ReadyDecode, sourceId);
                    lt.HasMeshBuild  = true;
                    lt.ReadyDecode   = null;
                }

                // Epic A / A2 (design §E step 4, HIGH a): a source-less (background) record the Tick loop
                // only CREATED (never kicked — PumpPending may not have run yet, or was cap-deferred). Kick
                // inline here — drain ignores per-tick caps. Without this branch the record falls straight to
                // the "else { lt.Built = true; }" no-mesh settle below (§F tooth 12) — invisible in exactly
                // the deterministic/snapshot harnesses that settle via DrainMeshBuilds.
                if (lt.FetchCompleted && !lt.HasMeshBuild && lt.ReadyDecode == null &&
                    _pipelines[key.Slot].IsSourceless)
                {
                    lt.MeshBuildTask = KickSourcelessBackground(id, lt.TileOriginRender);
                    lt.HasMeshBuild  = true;
                }

                // (b) Mesh build in-flight — spin and consume.
                if (lt.HasMeshBuild)
                {
                    var tessTask = lt.MeshBuildTask;
                    // Safe spin: mesh build UniTask uses configureAwait: false (RunOnThreadPool),
                    // so IsCompleted is true on the ThreadPool without needing the PlayerLoop.
                    // Thread.Sleep(1) yields real CPU time so the ThreadPool can complete the work.
                    int spins = 0;
                    while (!tessTask.Status.IsCompleted() && spins++ < 10000)
                        Thread.Sleep(1);
                    // Drain ignores per-frame caps: unbounded budget consumes ALL layers in one call → Built.
                    ConsumeMeshBuild(id, ref lt, int.MaxValue, int.MaxValue, out _, out _);
                }
                else
                {
                    lt.Built = true;
                }

                _loaded[key] = lt;
            }
        }

        /// <summary>
        /// S47/S51/S55 pump: per tile, kick its mesh build then consume the built mesh.
        ///
        /// Mesh build (fetch→kick): for tiles whose fetch completed, mint the shared decode entry and kick a
        /// background mesh-build UniTask (S55: at most <paramref name="maxMeshBuildsPerTick"/> kicks per
        /// Tick — the entry is retained in <see cref="LoadedTile.ReadyDecode"/> until the cap allows).
        ///
        /// Consume (build→upload): for tiles whose mesh-build UniTask is completed, consume the result on the
        /// main thread (UploadMesh → backend registration) MESH-by-mesh (S87). The per-frame budget is dual —
        /// at most <paramref name="maxConsumesPerTick"/> layer MESHES AND
        /// <paramref name="maxVerticesPerTick"/> vertices per frame, whichever binds first; a tile whose
        /// layers exceed the remaining budget is consumed partially and RESUMES (via its ConsumeCursor) on
        /// later Ticks — a single rich tile never lands in one frame.
        ///
        /// Returns the count of tiles still pending (fetch or mesh build in-flight, or cap-deferred).
        ///
        /// Greppability note: there is NO .Schedule().Complete() in this method.
        /// </summary>
        private int PumpPending(
            CameraProperties cam,
            int              maxConsumesPerTick,
            int              maxMeshBuildsPerTick,
            int              maxVerticesPerTick)
        {
            using var sFetchPoll = PmFetchPoll.Auto();

            // Reset per-tick observability counters.
            MeshBuildsKickedLastTick = 0;
            VerticesConsumedLastTick = 0;
            TilesConsumedLastTick    = 0;
            MeshesConsumedLastTick   = 0;

            // S55: treat 0 as uncapped for the kick + vertex caps (unset config field → harmless default).
            int buildCap = maxMeshBuildsPerTick > 0 ? maxMeshBuildsPerTick : int.MaxValue;
            int vertsCap = maxVerticesPerTick   > 0 ? maxVerticesPerTick : int.MaxValue;
            // S87: the per-frame MESH-count cap is used directly — 0 BLOCKS consume (tests build a backlog
            // that way); set it high (e.g. 64) for effectively-uncapped. The asymmetry with the vertex cap
            // (0 = uncapped) is intentional and documented on TileSelectionConfig.MaxConsumesPerTick.
            int consumeCap = maxConsumesPerTick;

            _toRelease.Clear();
            foreach (var kv in _loaded)
            {
                if (!kv.Value.Built)
                    _toRelease.Add(kv.Key);
            }

            int pending          = 0;
            int buildsKicked     = 0;
            int meshesConsumed   = 0;
            int verticesConsumed = 0;

            for (int i = 0; i < _toRelease.Count; i++)
            {
                LoadedKey  key      = _toRelease[i];
                TileId     id       = key.Tile;
                string     sourceId = _pipelines[key.Slot].SourceId;
                LoadedTile lt       = _loaded[key];

                // ── Consume a completed mesh build MESH-by-mesh under the dual budget (S87) ──
                if (lt.FetchCompleted && lt.HasMeshBuild && lt.MeshBuildTask.Status.IsCompleted())
                {
                    int meshBudgetLeft = consumeCap - meshesConsumed;
                    int vertBudgetLeft = vertsCap   - verticesConsumed;
                    // Budget exhausted this frame (or consumeCap == 0 → blocked): defer the rest to the next Tick.
                    // The tile keeps its ConsumeCursor; pending++ keeps Tick pumping until it drains.
                    if (meshBudgetLeft <= 0 || vertBudgetLeft <= 0)
                    {
                        pending++;
                        continue;
                    }

                    bool complete = ConsumeMeshBuild(
                        id,                     ref lt, meshBudgetLeft, vertBudgetLeft,
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

                // ── Mesh build in-flight ───────────────────────────────────────────────────────
                if (lt.FetchCompleted && lt.HasMeshBuild)
                {
                    pending++;
                    continue;
                }

                // ── Kick a mesh build from ReadyDecode ─────────────────────────────────────────
                if (lt.FetchCompleted && lt.ReadyDecode != null)
                {
                    // Stall #2: don't start a NEW background build for a record already condemned to release
                    // (DrainReleaseQueue will free it within a few frames). In-flight builds still finish and
                    // land in the existing disposal pens; this only avoids kicking fresh work for a doomed tile.
                    if (_releaseQueued.Contains(key))
                    {
                        pending++;
                        continue;
                    }

                    if (buildsKicked >= buildCap)
                    {
                        // Cap reached this tick — the shared decode is retained for next Tick.
                        pending++;
                        continue;
                    }

                    // Epic A / A5b: obtain the symbol worker pass on the MAIN THREAD, isolated (a throwing
                    // factory must never fault the pump — mirrors ObserveFetchOutcome's per-tile fault
                    // isolation), then thread it into the SAME kick task so the symbol worker rides the mesh
                    // kick, sharing the shared decode below (the feed swap — retires the parallel push).
                    Processing.ISymbolTileWorkerPass symbolPass = null;
                    try
                    {
                        symbolPass = SymbolWorkerFactory?.TryBeginBuild(sourceId, id);
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[TileManager] symbol factory (begin-build) threw for {id}: {ex.Message}");
                    }

                    lt.MeshBuildTask = KickMeshBuild(lt, id, lt.ReadyDecode, sourceId, symbolPass);
                    lt.HasMeshBuild  = true;
                    lt.ReadyDecode   = null;
                    buildsKicked++;
                    MeshBuildsKickedLastTick = buildsKicked;
                    pending++; // mesh build now in-flight
                    _loaded[key] = lt;
                    continue;
                }

                // ── Epic A / A2 (design §E step 4, HIGH 2): kick a source-less (background) build ──────
                // A pending record the Tick cover-loop only CREATED (never kicked — no ReadyDecode to
                // dispatch on). Rides the SAME condemned-skip + buildCap throttle as the byte path above, so
                // background loads at the shared build cadence, never as one synchronous cover-wide burst.
                if (lt.FetchCompleted && !lt.HasMeshBuild && lt.ReadyDecode == null &&
                    _pipelines[key.Slot].IsSourceless)
                {
                    if (_releaseQueued.Contains(key))
                    {
                        pending++;
                        continue;
                    }

                    if (buildsKicked >= buildCap)
                    {
                        pending++;
                        continue;
                    }

                    lt.MeshBuildTask = KickSourcelessBackground(id, lt.TileOriginRender);
                    lt.HasMeshBuild  = true;
                    buildsKicked++;
                    MeshBuildsKickedLastTick = buildsKicked;
                    pending++; // mesh build now in-flight
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
                IDecodedTileHandle handle = ObserveFetchOutcome(lt.Request, logErrors: true);
                if (handle != null)
                {
                    // A4/A7: retain the decode-provisioning handle ONCE per fetch — the single fork point
                    // where one (source, tile) splits into the mesh and symbol cadences. Kick deferred to a
                    // subsequent Tick (capped by buildCap). A5b: the symbol cadence is no longer pushed here —
                    // it is driven from the KICK block above (SymbolWorkerFactory.TryBeginBuild), sharing
                    // this SAME entry. Epic A / A7: the mint (bytes → handle) already happened inside
                    // ITileFeatureSource.GetTile — this just retains what it returned.
                    lt.ReadyDecode = handle;
                    pending++; // decode awaiting kick
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
        /// Starts a background <see cref="UniTask{MeshBuildResult}"/> that reads the shared decode
        /// (decoding it if this is the first cadence to reach it — A4) and runs every tile-mesh render
        /// layer bound to this <c>(source, tile)</c> through <see cref="Processing.TileLayerProcessorRunner"/>
        /// — the fan-out point (Epic A / A1). Returns immediately (non-blocking).
        ///
        /// S51: uses UniTask.RunOnThreadPool(configureAwait: false) instead of Task.Run.
        /// configureAwait: false is REQUIRED: the default (true) posts the final continuation via
        /// UniTask.Yield() to the Unity PlayerLoop. DrainMeshBuilds() and the synchronous-spin
        /// path in Dispose poll IsCompleted on the main thread WITHOUT pumping the PlayerLoop, so
        /// the task would never reach Succeeded with configureAwait: true. With configureAwait: false,
        /// completion stays on the ThreadPool and IsCompleted is true as soon as the work body returns.
        ///
        /// The UniTask is stored with .Preserve() in the caller so its .IsCompleted can be polled
        /// across multiple frames without exhausting the UniTask.
        ///
        /// The task captures only value-type / immutable inputs (<paramref name="decode"/>, layer records
        /// are read-only after Initialise). No Unity.Object is captured or touched off-main.
        ///
        /// S82 Decision 2 (option A): the paint bake zoom is the tile's INTEGER zoom (<c>id.Z</c>), not the
        /// fractional camera zoom — a tile is built exactly once per load (never re-baked while
        /// <see cref="LoadedTile.Built"/>), so baking at <c>id.Z</c> makes the prepared artifact a pure
        /// function of <c>(styleId, tileId, layerId)</c> — the sound key <see cref="PreparedTileCache"/>
        /// needs. <c>cam</c> is no longer read here (dropped from the signature; the 3 call sites still take
        /// a <c>CameraProperties</c> for other purposes and simply stop forwarding it here).
        ///
        /// <para>Epic A / A5b: <paramref name="symbolPass"/> (default null) rides this SAME kick task,
        /// AFTER the mesh pass, over the SAME shared <paramref name="decode"/> — the feed swap that retires
        /// the parallel symbol push. Computed only at the <c>PumpPending</c> kick site (§Q-Drain); the two
        /// <see cref="DrainMeshBuilds"/> call sites pass nothing, so drain stays symbol-silent (unchanged —
        /// labels never appeared in snapshots and no test drove symbols via drain).</para>
        /// </summary>
        private UniTask<MeshBuildResult> KickMeshBuild(
            LoadedTile                       lt, TileId id, IDecodedTileHandle decode, string sourceId,
            Processing.ISymbolTileWorkerPass symbolPass = null)
        {
            var     layersSnapshot = _layers.SnapshotLayers();
            double  zoom           = id.Z;
            double3 tileOrigin     = lt.TileOriginRender;

            // S89 Stage C: DENSE per-(tile, source) produce. Collect this-source layers in declared (draw)
            // order — each dense slot becomes one processor, carrying its own global material index. No
            // full-width sparse union / decision-5c null slots. S82: computed via the shared helper (also
            // used by the cache transfer/probe paths) — the dense ids are consumed synchronously below (one
            // processor built per id) before any worker capture; the scratch list itself is never captured
            // across the thread boundary.
            ComputeDenseLayerIds(sourceId, _denseLayerIdsScratch);
            int dense = _denseLayerIdsScratch.Count;

            // ── MAIN THREAD: one ITileMeshLayerProcessor per this-source layer, each pre-allocating its own
            // writable MeshDataArray (Mesh.AllocateWritableMeshData is main-thread only — spike-verified).
            // Epic A / A1: the worker decodes ONCE and writes into these processors' arrays in place; the
            // main thread applies at consume. Every allocated array is wrapped by a processor's Complete()
            // (written OR 0-vertex) — via TileLayerProcessorRunner's settlement loop below — so it is
            // disposed exactly once on the main thread, including on a faulted tile.
            var processors = new Processing.ITileMeshLayerProcessor[dense];
            using (PmMeshDataAllocate.Auto())
            {
                for (int d = 0; d < dense; d++)
                {
                    int li = _denseLayerIdsScratch[d];
                    // ComputeDenseLayerIds already filtered to ITileMeshRenderLayer slots — safe cast.
                    var layer = (Style.ITileMeshRenderLayer)layersSnapshot[li];
                    processors[d] = Processing.TileMeshLayerProcessor.AllocateForKick(layer, li);
                }
            }

            var context = new Processing.TileLayerProcessContext
            {
                Tile             = id,
                Zoom             = zoom,
                TileOriginRender = tileOrigin,
                Projection       = _projection,
            };

            return UniTask.RunOnThreadPool(() =>
            {
                // The fan-out point (Epic A / A1): read the shared decode (decoding it if this cadence
                // arrives first — A4), run every this-source processor once in dense order against that
                // same decoded tile, then settle every one of them exactly once — the moved form of today's
                // ensure-wrapped loop.
                Style.IRenderLayerPayload[] payloads =
                    Processing.TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors);
                var result = new MeshBuildResult { Payloads = payloads }; // mesh domain closed, arrays settled

                // Epic A / A5b (§Q5): fault domain 2, disjoint from the mesh domain above — the mesh RESULT
                // is already built, so a symbol fault below can never strand a mesh MeshDataArray. The pass
                // is infallible BY CONTRACT (owns its own try/catch), but this outer guard makes that
                // invariant STRUCTURAL rather than a trust in the contract — belt-and-braces over the pass's
                // own inner guard, exactly A1's per-processor Complete() guard precedent (RunWorkerPass above).
                try
                {
                    symbolPass?.RunWorkerAndHandoff(decode);
                }
                catch (System.Exception)
                { /* a contract-violating throw must not strand the mesh arrays */
                }

                return result;
            }, configureAwait: false).Preserve(); // .Preserve() allows polling .IsCompleted across multiple frames
        }

        /// <summary>
        /// Epic A / A2 (design §B Q1/Q2): starts a background <see cref="UniTask{MeshBuildResult}"/> for a
        /// SOURCE-LESS (background) tile — no bytes, no decode. Called from <see cref="PumpPending"/> (under
        /// the shared build cap) and <see cref="DrainMeshBuilds"/> (uncapped). Mirrors
        /// <see cref="KickMeshBuild"/>'s structure exactly, minus the byte/decode plumbing: one
        /// <see cref="Processing.TileBackgroundLayerProcessor"/> per background layer (dense order, by TYPE —
        /// <see cref="ComputeSourcelessLayerIds"/>), each pre-allocating its own writable
        /// <see cref="Mesh.MeshDataArray"/> on the MAIN THREAD, then <see cref="Processing.TileLayerProcessorRunner.RunSourcelessWorkerPass"/>
        /// on the ThreadPool.
        /// </summary>
        private UniTask<MeshBuildResult> KickSourcelessBackground(TileId id, double3 origin)
        {
            var    layersSnapshot = _layers.SnapshotLayers();
            double zoom           = id.Z;

            ComputeSourcelessLayerIds(_denseLayerIdsScratch);
            int dense = _denseLayerIdsScratch.Count;

            var processors = new Processing.ITileMeshLayerProcessor[dense];
            using (PmMeshDataAllocate.Auto())
            {
                for (int d = 0; d < dense; d++)
                {
                    int li    = _denseLayerIdsScratch[d];
                    var layer = (Style.BackgroundRenderLayer)layersSnapshot[li];
                    processors[d] = Processing.TileBackgroundLayerProcessor.AllocateForKick(layer, li);
                }
            }

            var context = new Processing.TileLayerProcessContext
            {
                Tile             = id,
                Zoom             = zoom,
                TileOriginRender = origin,
                Projection       = _projection,
            };

            return UniTask.RunOnThreadPool(() =>
            {
                Style.IRenderLayerPayload[] payloads =
                    Processing.TileLayerProcessorRunner.RunSourcelessWorkerPass(in context, processors);
                return new MeshBuildResult { Payloads = payloads };
            }, configureAwait: false).Preserve();
        }

        /// <summary>
        /// S87 resumable per-MESH consume. Uploads + registers tile-layer meshes starting at
        /// <see cref="LoadedTile.ConsumeCursor"/> (a dense index over this source's payloads, in draw order),
        /// consuming layers until a budget binds (<paramref name="meshBudget"/> meshes OR
        /// <paramref name="vertBudget"/> vertices — checked before each layer, so at most one-mesh overshoot;
        /// a single mesh cannot be split) or the tile is fully consumed. Each consumed layer's NativeArrays
        /// are disposed immediately after upload; on completion the whole result is disposed (idempotent) to
        /// also free any layers the active style does not render. Returns true when the tile is now fully
        /// consumed (<see cref="LoadedTile.Built"/>); false when partially consumed (resume next Tick).
        ///
        /// Must be called on the Unity main thread, only when MeshBuildTask.IsCompleted, with positive
        /// budget (the pump guards budget &lt;= 0). A partially-consumed tile keeps HasMeshBuild = true
        /// and !Built; its already-built meshes/handles live in lt.Meshes/lt.DrawHandles, the remaining layers
        /// stay alive in the Preserved task (re-fetched each call). Eviction's holding pen disposes the
        /// remainder (idempotent — already-consumed layers are no-ops).
        ///
        /// Mid-flight release: ReleaseTile removes the tile from _loaded, so this is never called for a
        /// released tile — the real discard protection is the _loaded-removal in ReleaseTile.
        /// </summary>
        private bool ConsumeMeshBuild(
            TileId  id,             ref LoadedTile lt, int meshBudget, int vertBudget,
            out int meshesConsumed, out int        vertsConsumed)
        {
            meshesConsumed = 0;
            vertsConsumed  = 0;

            var task = lt.MeshBuildTask;

            // Faulted or cancelled — nothing to render; complete immediately.
            if (task.Status != UniTaskStatus.Succeeded)
            {
                FinishConsume(ref lt);
                return true;
            }

            // .GetResult() is Preserve-safe and re-callable across frames (the pump calls this only when
            // IsCompleted is true). Per-layer NativeArrays are disposed as each layer is consumed.
            MeshBuildResult result = task.GetAwaiter().GetResult();

            int denseCount        = result.Payloads?.Length ?? 0;
            int currentLayerCount = _layers.Count;

            _consumeScratchMeshes.Clear();
            _consumeScratchHandles.Clear();
            _consumeScratchMatIndices.Clear();

            // Consume the dense per-source payloads from the cursor until a budget binds or all are done.
            // Empty (0-vertex) layers are free — they only advance the cursor. The pump guarantees positive
            // budget, so at least one layer is processed per call → progress is guaranteed (a lone huge mesh
            // consumes in one go, one-mesh overshoot).
            int cursor = lt.ConsumeCursor;
            while (cursor < denseCount && meshesConsumed < meshBudget && vertsConsumed < vertBudget)
            {
                // S89 C: the payload carries its own material index (draw order); the cursor is just a dense
                // resume position. A payload whose material index no longer exists (restyle shrank the layer
                // set mid-flight) is freed without registering — the backend would otherwise throw on it.
                Style.IRenderLayerPayload payload = result.Payloads[cursor];
                cursor++;

                int materialIndex = payload?.MaterialIndex ?? -1;
                int layerVerts    = payload?.VertexCount   ?? 0;

                if (payload == null || (uint)materialIndex >= (uint)currentLayerCount)
                {
                    payload?.Dispose();
                    continue;
                }

                Mesh mesh;
                using (PmMeshUpload.Auto())
                    mesh = payload.Upload();
                payload.Dispose(); // consumed — free its NativeArrays now

                if (mesh == null) continue; // empty layer — no AddLayer, no budget charge

                int handle;
                using (PmAddTileLayer.Auto())
                    handle = _instanced.AddTileLayer(mesh, lt.TileOriginRender, materialIndex, id);
                _consumeScratchMeshes.Add(mesh); // S51: track for explicit destruction
                _consumeScratchHandles.Add(handle);
                _consumeScratchMatIndices.Add(materialIndex); // S82: which layerId this mesh belongs to
                meshesConsumed++;
                vertsConsumed += layerVerts;
            }

            lt.ConsumeCursor = cursor;

            // Append this call's new meshes/handles/materialIndices to the tile's arrays (one realloc per
            // partial frame; load time only — steady state never re-enters consume, so no per-frame GC there).
            AppendMeshes(ref lt.Meshes, _consumeScratchMeshes);
            AppendInts(ref lt.DrawHandles,     _consumeScratchHandles);
            AppendInts(ref lt.MaterialIndices, _consumeScratchMatIndices);

            bool complete = cursor >= denseCount;
            if (complete)
            {
                // Dispose the whole result (idempotent) — also frees any layers the active style does not
                // render (beyond fillCount/lineCount) — then mark Built and release the task.
                DisposeWholeResult(result);
                FinishConsume(ref lt);
            }

            return complete;
        }

        /// <summary>S87: marks a tile fully consumed — clears the mesh build task and sets Built.</summary>
        private static void FinishConsume(ref LoadedTile lt)
        {
            lt.HasMeshBuild  = false;
            lt.MeshBuildTask = default;
            lt.Built         = true;
        }

        /// <summary>S87: appends freshly-built meshes to a tile's tracked-Mesh array (grows by realloc).</summary>
        private static void AppendMeshes(ref Mesh[] arr, List<Mesh> add)
        {
            if (add.Count == 0) return;
            int oldLen = arr?.Length ?? 0;
            var merged = new Mesh[oldLen + add.Count];
            if (arr           != null) System.Array.Copy(arr, merged, oldLen);
            for (int k = 0; k < add.Count; k++) merged[oldLen + k] = add[k];
            arr = merged;
        }

        /// <summary>S87/S82: appends freshly-produced ints to a tile's tracked int[] (grows by realloc).
        /// Generic over the field's meaning — used for both DrawHandles and (S82) MaterialIndices, which are
        /// parallel arrays built the same way, one realloc per partial-consume call.</summary>
        private static void AppendInts(ref int[] arr, List<int> add)
        {
            if (add.Count == 0) return;
            int oldLen = arr?.Length ?? 0;
            var merged = new int[oldLen + add.Count];
            if (arr           != null) System.Array.Copy(arr, merged, oldLen);
            for (int k = 0; k < add.Count; k++) merged[oldLen + k] = add[k];
            arr = merged;
        }

        /// <summary>S48/S87: disposes every payload in a result (null-slot- and idempotent-safe — a payload
        /// already disposed during a partial consume, or a null empty-layer slot, is a no-op). The single
        /// place a <see cref="MeshBuildResult"/>'s NativeArrays are freed, called from every discard path.</summary>
        private static void DisposeWholeResult(MeshBuildResult result)
        {
            if (result.Payloads == null) return;
            for (int li = 0; li < result.Payloads.Length; li++)
                result.Payloads[li]?.Dispose();
        }

        /// <summary>
        /// Stall #2: releases up to <paramref name="budget"/> queued (tile, source) records this Tick (0 =
        /// uncapped, D12-consistent). Runs every frame above the cover gate, so a clean tick still whittles a
        /// zoom-out backlog. Each dequeued key is RE-VALIDATED: a tile that came back into the cover during
        /// its 1–3 frame linger (<see cref="_coverSet"/> still contains it) or a record a restyle already
        /// cleared (gone from <see cref="_loaded"/>) is SKIPPED — turning a fast pan-out-pan-back into a free
        /// no-op instead of destroy+refetch. A skip does not consume budget (it is a pure dequeue).
        /// </summary>
        private void DrainReleaseQueue(int budget)
        {
            TilesReleasedLastTick = 0;
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

        /// <summary>
        /// Releases a tile: scheduler release + unregister its instanced draw items + free its meshes.
        /// Does NOT wait for in-flight work (non-blocking). Mid-flight mesh build is removed
        /// from _loaded immediately so PumpPending/DrainMeshBuilds never visit it again.
        ///
        /// S48 holding pen: when a mesh build UniTask is still in-flight at release time, its
        /// NativeArray payload has not yet been produced (it will be allocated on the ThreadPool
        /// after this method returns). We stash the UniTask in <see cref="_pendingDisposal"/>;
        /// <see cref="DrainPendingDisposal"/> polls it each Tick and disposes the payload when the
        /// task completes. This guarantees no NativeArray leak for mid-flight-released tiles.
        /// </summary>
        private void ReleaseTile(LoadedKey key)
        {
            if (_loaded.TryGetValue(key, out var lt))
            {
                // Whether this tile's meshes transfer to the PreparedTileCache (Model B) vs are destroyed now.
                // Epic A / A2 (design §B Lifecycle): source-less (background) records deliberately SKIP the
                // cache transfer — a full-tile-extent quad is trivial to rebuild, and
                // TransferBuiltMeshesToCache keys off ComputeDenseLayerIds(sourceId), which has no
                // source-less variant (out of scope). Background meshes destroy on release and rebuild on
                // re-entry, keeping the S82 cache paths byte-identical for real sources.
                bool transferredToCache =
                    _cacheEnabled && lt.Built && lt.Meshes != null && !_pipelines[key.Slot].IsSourceless;
                // S82: a fully-Built tile with tracked geometry TRANSFERS its meshes into the
                // PreparedTileCache instead of letting RenderTeardownRecord destroy them (Model B — the
                // cache takes ownership). Nulling lt.Meshes here makes DestroyTrackedMeshes's existing
                // null-check a natural no-op — no separate "handed off" branch needed there.
                //
                // Scoped to THIS method (a genuine eviction) — deliberately NOT folded into
                // RenderTeardownRecord, which SetSources ALSO calls for every record on a restyle (rebuilt
                // backend + re-indexed RenderLayerSet). Caching those meshes there would file them under
                // whatever CurrentStyle happens to be at that moment, risking a later hit under a
                // coincidentally-matching (tileId, layerId) serving stale-style geometry — out of scope
                // pre-S83 (styleId is a constant default) and sidestepped entirely by never transferring
                // on that path; a restyle keeps today's always-destroy behaviour unchanged.
                //
                // _cacheEnabled == false skips the transfer entirely — RenderTeardownRecord's
                // DestroyTrackedMeshes then destroys lt.Meshes exactly as it did pre-S82.
                if (transferredToCache)
                    TransferBuiltMeshesToCache(key.Tile, _pipelines[key.Slot].SourceId, ref lt);

                RenderTeardownRecord(ref lt);
                _loaded.Remove(key);
            }

            // Route the release to the OWNING pipeline — a record on source B never touches A.
            // Epic A / A2: the source-less pipeline has no FeatureSource — no-op for a background record.
            _pipelines[key.Slot].FeatureSource?.Release(key.Tile);
            // S105/A-1: no symbol-release callback — this tile just left _loaded, so the subsystem's next
            // CollectLoadedTileKeys pull no longer reports it and its reconcile fades/drops the labels.
        }

        /// <summary>
        /// S82: transfers a fully-Built record's tracked meshes into <see cref="_prepared"/> (Model B — the
        /// cache now owns them), keyed per layer under <see cref="CurrentStyle"/>. Inserts one entry per
        /// DENSE layerId of this (tile, source) — a real <see cref="Mesh"/> for a produced layer, a
        /// <see langword="null"/> "empty-layer" marker for a dense layerId this record never produced a mesh
        /// for — so the Tick probe's completeness check (which walks the SAME dense set via
        /// <see cref="ComputeDenseLayerIds"/>) is exact: a partially-empty multi-layer tile still counts as a
        /// full cache hit on revisit.
        /// </summary>
        private void TransferBuiltMeshesToCache(TileId id, string sourceId, ref LoadedTile lt)
        {
            ComputeDenseLayerIds(sourceId, _denseLayerIdsScratch);

            int trackedCount = lt.MaterialIndices?.Length ?? 0;
            for (int i = 0; i < trackedCount; i++)
                _prepared.Put(new PreparedKey(CurrentStyle, id, lt.MaterialIndices[i]), lt.Meshes[i]);

            for (int d = 0; d < _denseLayerIdsScratch.Count; d++)
            {
                int  layerId = _denseLayerIdsScratch[d];
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

        /// <summary>
        /// S82: builds a fully-Built <see cref="LoadedTile"/> record directly from a PreparedTileCache HIT
        /// (the Tick request-loop's cache probe, called only once every dense layerId of
        /// <paramref name="denseLayerIds"/> is confirmed <see cref="PreparedTileCache.Contains"/>). TryTakes
        /// each layer's mesh (Model B — ownership transfers back to this record) and re-registers non-null
        /// ones with the backend via <c>AddTileLayer</c>; a <see langword="null"/> (empty-layer marker) entry
        /// is a no-op, reproducing the original prepare's "no mesh for this layer" outcome exactly. No fetch,
        /// no decode, no mesh build, no upload — <see cref="MeshBuildsKickedLastTick"/> is untouched by
        /// this path, which is the decisive falsifier a shallow (re-building) cache would trip.
        /// </summary>
        private LoadedTile BuildTileFromCache(TileId id, double3 origin, List<int> denseLayerIds)
        {
            _consumeScratchMeshes.Clear();
            _consumeScratchHandles.Clear();
            _consumeScratchMatIndices.Clear();

            for (int d = 0; d < denseLayerIds.Count; d++)
            {
                int layerId = denseLayerIds[d];
                _prepared.TryTake(new PreparedKey(CurrentStyle, id, layerId), out Mesh mesh);
                if (mesh == null) continue; // empty-layer marker — nothing to register

                int handle;
                using (PmAddTileLayer.Auto())
                    handle = _instanced.AddTileLayer(mesh, origin, layerId, id);
                _consumeScratchMeshes.Add(mesh);
                _consumeScratchHandles.Add(handle);
                _consumeScratchMatIndices.Add(layerId);
            }

            var lt = new LoadedTile { Built = true, FetchCompleted = true, TileOriginRender = origin };
            AppendMeshes(ref lt.Meshes, _consumeScratchMeshes);
            AppendInts(ref lt.DrawHandles,     _consumeScratchHandles);
            AppendInts(ref lt.MaterialIndices, _consumeScratchMatIndices);
            return lt;
        }

        /// <summary>
        /// S83b: tears down a record's RENDER state — destroys its Mesh assets, unregisters its backend
        /// draw items, and stashes any in-flight fetch/mesh build in the S48/S84 holding pens (so their
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
                ReleasedMidFetchCount++;
                _pendingFetchDisposal.Add(lt.Request);
            }

            // A record with an unconsumed-or-partially-consumed mesh build must have its result's
            // NativeArrays disposed — stash in the S48 holding pen (idempotent dispose makes a partial
            // record's already-consumed layers safe no-ops). Genuine mid-flight (task still running) is
            // counted; an S87 partial (task complete, cursor mid-way) is stashed but not counted.
            if (lt.HasMeshBuild && !lt.Built)
            {
                if (!lt.MeshBuildTask.Status.IsCompleted())
                    ReleasedMidFlightCount++;
                _pendingDisposal.Add(lt.MeshBuildTask);
            }

            // Unregister the instanced draw items before destroying Mesh assets — one batched call so the
            // Entities backend drops this record's layer entities (+ the emptied tile root) in a SINGLE
            // structural change (stall #2) instead of one per layer; the Mesh is freed just after.
            if (_instanced != null && lt.DrawHandles != null)
                _instanced.RemoveItems(lt.DrawHandles);

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
                    DisposeWholeResult(task.GetAwaiter().GetResult());
                // Faulted/cancelled: no payloads produced, nothing to dispose.

                _pendingDisposal.RemoveAt(i);
            }
        }

        /// <summary>
        /// S84: Observes a COMPLETED fetch task's terminal outcome exactly once and returns its
        /// decode-provisioning handle (null on cancel/fault, or on an absent tile — Epic A / A7:
        /// <see cref="ITileFeatureSource.GetTile"/> already mapped <c>!HasData</c> to null). This is the
        /// single place a fetch <see cref="UniTask{T}"/> result is consumed, so a faulted/cancelled fetch is
        /// never left for UniTask's unobserved-exception finalizer (the console-flood bug). A tile released
        /// mid-fetch cancels its token, surfacing as <see cref="System.OperationCanceledException"/> (mapped
        /// from the aborted request by <c>UnityWebRequestDataSource</c>) — benign, swallowed silently. A
        /// genuine error (5xx / connection) is surfaced only when <paramref name="logErrors"/> is set (the
        /// still-wanted path) and is bounded. Precondition: <c>req.Status.IsCompleted()</c>; safe because
        /// <c>lt.Request</c> is <c>.Preserve()</c>d.
        /// </summary>
        private IDecodedTileHandle ObserveFetchOutcome(UniTask<IDecodedTileHandle> req, bool logErrors)
        {
            try
            {
                return req.GetAwaiter().GetResult();
            }
            catch (System.OperationCanceledException)
            {
                return null; // tile released mid-fetch — benign cancellation
            }
            catch (System.Exception ex)
            {
                if (logErrors) LogFetchErrorThrottled(ex);
                return null;
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
        /// <see cref="ConsumeMeshBuild"/> populates <c>lt.Meshes</c> with direct references to
        /// every Mesh it creates; this method iterates that array for reliable, deterministic destruction.
        /// After destruction, <c>lt.Meshes</c> is nulled to prevent double-free.
        /// Must be called on the Unity main thread (Object.Destroy constraint).
        /// </summary>
        private void DestroyTrackedMeshes(ref LoadedTile lt)
        {
            if (lt.Meshes == null) return;
            for (int i = 0; i < lt.Meshes.Length; i++)
                lt.Meshes[i].DestroySafely(allowDestroyingAssets: true);
            lt.Meshes = null;
        }

        /// <summary>
        /// Releases all tile GameObjects and Mesh assets, drains mesh build tasks, and disposes the
        /// scheduler and (if owned) the data source. Does NOT dispose the RenderLayerSet — MapView owns
        /// that and disposes it AFTER this (tile renderers reference layer materials, so the order matters).
        ///
        /// <para>Idempotent: guarded by the inherited <see cref="VerifiedDisposable"/> Dispose() guard, so a
        /// second call (or a call on a never-styled manager) is a no-op.</para>
        /// </summary>
        protected override void DoDispose()
        {
            // S51/S48: drain outstanding mesh build UniTasks before tearing down.
            // Safe spin: mesh build UniTasks use configureAwait: false (UniTask.RunOnThreadPool),
            // so IsCompleted becomes true on the ThreadPool without needing the PlayerLoop. Spinning
            // here on the main thread is therefore deadlock-free.
            //
            // S48: after spinning, dispose the NativeArray payload — we're tearing down and must
            // not leak. (These are tiles still in _loaded; mid-flight-released tiles are in
            // _pendingDisposal, drained separately below.)
            foreach (var kv in _loaded)
            {
                if (kv.Value.HasMeshBuild)
                {
                    var tessTask = kv.Value.MeshBuildTask;
                    // Thread.Sleep(1) yields real CPU time so the ThreadPool can complete the task.
                    int spins = 0;
                    while (!tessTask.Status.IsCompleted() && spins++ < 10000)
                        Thread.Sleep(1);

                    // S48: dispose the produced NativeArrays (or no-op if faulted/cancelled).
                    if (tessTask.Status == UniTaskStatus.Succeeded)
                        DisposeWholeResult(tessTask.GetAwaiter().GetResult());
                }
            }

            // S48: drain the mid-flight-discard holding pen — spin to completion, then dispose.
            for (int i = 0; i < _pendingDisposal.Count; i++)
            {
                var task  = _pendingDisposal[i];
                int spins = 0;
                while (!task.Status.IsCompleted() && spins++ < 10000)
                    Thread.Sleep(1);

                if (task.Status == UniTaskStatus.Succeeded)
                    DisposeWholeResult(task.GetAwaiter().GetResult());
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
                _pipelines[kv.Key.Slot].FeatureSource.Release(kv.Key.Tile);
                var fetchTask = kv.Value.Request;
                int spins     = 0;
                while (!fetchTask.Status.IsCompleted() && spins++ < 10000)
                    Thread.Sleep(1);
                ObserveFetchOutcome(fetchTask, logErrors: false);
            }

            for (int i = 0; i < _pendingFetchDisposal.Count; i++)
            {
                var task  = _pendingFetchDisposal[i];
                int spins = 0;
                while (!task.Status.IsCompleted() && spins++ < 10000)
                    Thread.Sleep(1);
                ObserveFetchOutcome(task, logErrors: false);
            }

            _pendingFetchDisposal.Clear();

            // Destroy each tile's tracked Mesh assets.
            //
            // S51 leak guard: a Mesh asset is NOT freed just because nothing references it. lt.Meshes
            // holds direct Mesh references (set by ConsumeMeshBuild) for reliable, index-safe
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

            // S82: destroy every Mesh the PreparedTileCache still holds (out-of-cover tiles handed off by
            // ReleaseTile) — SAME "destroy meshes → dispose backend" ordering as the loop just above, so the
            // backend never references a freed Mesh either way.
            _prepared.Dispose();

            // Dispose the instanced backend AFTER destroying all tile meshes (it references mesh IDs that
            // become invalid when the Mesh assets are destroyed; this order keeps it from drawing freed
            // meshes). S49 tooth 5: BRG + GraphicsBuffer released; S53b: Entities World disposed.
            _instanced?.Dispose();
            _instanced = null;

            // S83b: dispose every source pipeline (each scheduler + its owned source).
            DisposePipelines();
        }

        /// <summary>S83b: disposes and clears every source pipeline — Epic A / A7: one
        /// <see cref="ITileFeatureSource.Dispose"/> call per pipeline now covers what used to be a
        /// scheduler-dispose-then-owned-source-dispose pair (both live inside the feature source).
        /// Idempotent.</summary>
        private void DisposePipelines()
        {
            for (int i = 0; i < _pipelines.Count; i++)
                _pipelines[i].FeatureSource?.Dispose();
            _pipelines.Clear();
        }
    }
}