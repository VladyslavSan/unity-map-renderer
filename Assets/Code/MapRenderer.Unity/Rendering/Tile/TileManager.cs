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
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
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

            /// <summary>S55: max tiles admitted per Tick. Default 2 (MapView serialized field).
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

            /// <summary>Tile-load smoothness: CONCURRENCY cap on admitted, not-yet-<see cref="LoadedTile.Built"/>
            /// (tile,source) records — orthogonal to the per-tick RATE caps above (see
            /// <see cref="Map.MapViewConfig.MaxConcurrentTileLoads"/> for the full contract). 0 or negative
            /// means uncapped.</summary>
            public int MaxConcurrentTileLoads;

            /// <summary>Tile-load smoothness: which render-space distance ranks not-yet-admitted tiles for
            /// loading (admission order AND PumpPending's build/consume order — see
            /// <see cref="Map.MapViewConfig.PriorityStrategy"/>).</summary>
            public TilePriorityStrategy PriorityStrategy;

            /// <summary>How much of each tile's MVT buffer the FILL meshes keep before triangulation — one
            /// global knob (<c>MapViewConfig.FillTileBufferClip</c>), read live like the budgets above.
            ///
            /// <para>Changing it mid-run evicts what is in the <c>PreparedTileCache</c> at that moment, and
            /// does NOT rebuild meshes already in cover. Those tiles then re-populate the cache with their
            /// stale-window geometry when they leave cover — <c>PreparedKey</c> is (Style, Tile, LayerId)
            /// with no clip component, so the entry is indistinguishable from a fresh one and re-entering
            /// cover serves it verbatim. A tuning knob, not a live visual toggle: restyle or restart to be
            /// certain every mesh reflects the new value.</para></summary>
            public TileBufferClip BufferClip;
        }

        // ── S47 mesh build payload (S51: Task → UniTask) ────────────────────────────────────

        /// <summary>
        /// Per-tile live record: the in-flight fetch request, the mesh build handle, and the built tile
        /// container GameObject.
        ///
        /// S47/S51, extended by job-scheduling-design.md §8 stage 3: the lifecycle is now:
        ///   1. Fetch (Request → UniTask[SharedDisposable[IDecodedTile]] in-flight, stored as .Preserve())
        ///   2. Mesh build kicked — <see cref="Step"/> becomes <see cref="BuildStep.Prologue"/>
        ///      (MeshBuildTask in-flight, source tiles only) then <see cref="BuildStep.Measure"/> then
        ///      <see cref="BuildStep.Write"/> (Graph in-flight, every tile — a background tile starts
        ///      directly at Measure, no Prologue); FetchCompleted = true either way.
        ///   3. Mesh build consumed (Built = true; Step = BuildStep.None; Go = container)
        ///
        /// Mid-flight release protection: ReleaseTile removes the tile from _loaded immediately,
        /// so PumpPending and DrainMeshBuilds — which iterate _loaded — never visit released
        /// tiles. A tile released while its mesh build is in-flight will never have ConsumeMeshBuild
        /// called for it.
        ///
        /// <c>MeshBuildTask</c> is a <see cref="WorkHandle{T}"/>: pollable across frames with no
        /// <c>.Preserve()</c> needed, and <c>GetResult()</c> is repeatable once terminal (the backing
        /// completion source never recycles). Unlike <c>default(UniTask{T})</c>, a <c>default</c>
        /// <see cref="WorkHandle{T}"/> carries no source and every member throws — <see cref="Step"/>
        /// being <see cref="BuildStep.Prologue"/> is what makes that safe: every read of <c>MeshBuildTask</c>
        /// is guarded by it, so the field is only ever touched while it holds a real handle. <see cref="Graph"/>
        /// is non-null iff <see cref="Step"/> is <see cref="BuildStep.Measure"/> or <see cref="BuildStep.Write"/>.
        /// </summary>
        private struct LoadedTile
        {
            public UniTask<SharedDisposable<IDecodedTile>>          Request;
            public bool                                             FetchCompleted; // fetch done; mesh build may be in-flight
            public WorkHandle<Processing.TilePrologueOutput> MeshBuildTask; // default until fetch completes; default after consumed
            public BuildStep                                        Step;           // which build step (if any) is in flight
            public bool                                             Built;          // mesh produced (or definitively absent/failed)

            /// <summary>The graph-arm build — non-null iff <see cref="Step"/> is
            /// <see cref="BuildStep.Measure"/> or <see cref="BuildStep.Write"/>. Every tile ends up here: a
            /// source tile's prologue hands its dense <c>ILayerMeshBuild[]</c> to
            /// <see cref="Processing.TileBuildGraph.ScheduleMeasureFromDecode"/>; a background tile skips
            /// the prologue and schedules directly via <see cref="Processing.TileBuildGraph.ScheduleMeasure"/>.</summary>
            public Processing.TileBuildGraph Graph;

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
            /// <c>0 &lt; ConsumeCursor &lt; total</c> the tile is partially consumed (Step is
            /// still BuildStep.Prologue, the task is COMPLETE, and some layers are already in Meshes/DrawHandles).
            /// </summary>
            public int ConsumeCursor;

            /// <summary>
            /// S55/A4: the decode-provisioning handle the fetch produced (Epic A / A7:
            /// <see cref="ITileFeatureSource.GetTile"/>'s result). Set when the fetch completes; null before
            /// that, and null again once the record no longer owns it.
            ///
            /// <para><b>This field IS one reference</b> to an already-decoded tile holding
            /// <c>Allocator.Persistent</c> buffers. R1: it is cleared exactly ONE way now —
            /// <see cref="RenderTeardownRecord"/> RELEASES it (cover change, eviction, restyle, teardown) —
            /// for a kicked record precisely as much as a never-kicked one. The mesh kick no longer TRANSFERS
            /// this reference: it takes its own separate one (<see cref="KickMeshBuild"/>'s prologue
            /// <c>Acquire()</c>), so this field stays live and unchanged across the whole kick. Dropping it
            /// any other way leaks the tile.</para>
            ///
            /// <para><b>Deliberate cost, recorded rather than tested (no observing tooth exists for it — see
            /// <c>recorded-limitation-needs-an-observing-tooth</c>).</b> Because this field now survives the
            /// kick instead of being released when the mesh build completes, a decoded tile's
            /// <c>Allocator.Persistent</c> buffers live for the record's WHOLE in-cover lifetime, not just
            /// until its mesh is built — a DURATION increase in peak resident decoded-tile memory on top of
            /// the eager-decode BREADTH increase the prior stage already accepted (every fetched cover tile
            /// decodes, kicked or not). Rendered output is unaffected — this is a resource-lifetime cost, not
            /// a behaviour change — and it was chosen knowingly over the alternative (release at kick
            /// completion instead of at teardown), which would have partly resurrected the transfer machinery
            /// this stage deletes. A future residency-ceiling tooth, if one is ever added, is the thing that
            /// would stop this being deliberate.</para></summary>
            public SharedDisposable<IDecodedTile> Decode;
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
        /// <c>Url</c>/<c>tiles[]</c>/zoom/scheme/bounds/<c>data</c> match. Remote-TileJSON content drift is
        /// out of scope (no refresh — decision 4), so equality is purely over the resolved fields.
        ///
        /// <para><b>Why <c>data</c> is one of them.</b> A source whose payload is INLINE has no
        /// <c>url</c> and no <c>tiles[]</c>, and takes the spec defaults for zoom/scheme/bounds — so without
        /// this field every inline source in existence is value-equal to every other, and a restyle from one
        /// dataset to a different one keeps the FIRST one's pipeline and renders the wrong geometry, silently.
        /// It is carried as a canonical STRING (<see cref="JsonCanonical"/>) rather than as the DOM node:
        /// reference identity would flip the failure the other way — a restyle re-parses the document, so
        /// every inline source would compare as changed and rebuild on every restyle, quietly retiring the
        /// keep-the-pipeline path this key exists to provide.</para>
        ///
        /// <para><b>Why <c>type</c> is one of them.</b> It is not a tolerated extra field — it is the field
        /// that SELECTS WHICH FACTORY RUNS (<c>MapView.BuildSourceSpecs</c> branches on it to build a byte
        /// fetcher or a local slicer), so two definitions differing only in it are not the same source by any
        /// reading of the question this key asks. Without it they hash and compare equal and
        /// <see cref="SetSources"/> keeps the first pipeline, leaving the map fetching MVT for a style that
        /// now declares inline GeoJSON, or slicing a retired dataset for one that now declares vector tiles.
        /// The repro is narrow — each side must also carry the other type's keys, since a data-less geojson
        /// source and a tiles-less vector source are both skipped before a spec is minted — but its
        /// structural value does not depend on the repro: <b>do not delete this field for being
        /// untriggerable.</b></para>
        /// </summary>
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

            /// <summary>PRIVATE, and every parameter required, so <see cref="From"/> is the only way to
            /// mint a key. A defaulted <c>type</c> (or <c>data</c>) is the recorded bug wearing a legal
            /// signature: a caller that omitted it would build a key that compares equal across the very
            /// field the pipeline diff branches on.</summary>
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

            /// <summary>Builds the key from a resolved <see cref="SourceDefinition"/> (post-S83a resolution).</summary>
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
            // TileMesh but not ITileMeshRenderLayer — its geometry comes from a processor, not BuildGraphRequest).
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

        // The live fill tile-buffer clip, cached each Tick alongside _projection and read by both mesh-kick
        // context sites. Unlike the projection this one CAN change at runtime (an Inspector tweak), which is
        // why Tick compares it against the previous value.
        private TileBufferClip _bufferClip;

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

        // Tile-load smoothness: the DESIRED list — (tile, source) keys that want to load but are not yet
        // admitted (no _loaded record, no fetch, no build). Kept in priority order across Ticks (re-sorted
        // every Tick — AdmitFromDesired). _desiredSet mirrors _releaseQueued's dedup role (O(1) membership
        // test on the cover-loop's "already wanted?" check). Pre-sized like the release-queue pair above so
        // steady-state cover churn never lazily allocates the HashSet's buckets.
        private readonly List<LoadedKey>    _desired    = new(64);
        private readonly HashSet<LoadedKey> _desiredSet = new(64);

        // Reused scratch for TilePriority.SortByPriority-style insertion sorts over _desired / PumpPending's
        // per-tick work list — grown (never shrunk) to fit the largest list sorted so far. Never reallocated
        // in steady state (the cover size that drives both lists' capacity stabilizes quickly).
        private double[] _priorityKeys = new double[64];

        // S105/A5b: the symbol-agnostic seam through which the DECOUPLED symbol-symbol subsystem is driven —
        // TileManager holds only this interface (never a symbol/store/glyph type). The per-tile mesh KICK
        // (PumpPending) calls TryBeginBuild on the MAIN THREAD, isolated (a throwing factory must never fault
        // the tile pipeline, mirroring TakeDecodeFromFetch's per-tile fault isolation), then threads the
        // returned pass into the SAME kick task so the symbol worker runs alongside the mesh pass, sharing
        // the A4 shared-decode entry (no re-fetch, no second decode, no touching the mesh/disposal path). The
        // tile LIFECYCLE (which tiles are loaded → which symbols render) is NOT pushed — the subsystem PULLS
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
        private readonly List<Mesh> _consumeMeshes = new(8);

        private readonly List<int> _consumeHandles = new(8);

        // S82: parallel to the two above — the global material index (layerId) of each newly-built mesh,
        // so a Built record can later be transferred into PreparedTileCache keyed per layer.
        private readonly List<int> _consumeMatIndices = new(8);

        // S82: reusable scratch for the dense (declared-order) global material indices whose style layer's
        // source is a given sourceId (ComputeDenseLayerIds). ONLY ever consumed synchronously on the main
        // thread within the same call (transfer-to-cache, probe) — never captured across a thread boundary
        // (KickMeshBuild copies it into a fresh int[] before scheduling the background task).
        private readonly List<int> _denseLayerIds = new(8);

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
        // the handle has already been captured but not yet produced a result. We cannot dispose the
        // NativeArrays immediately — they don't exist yet. Instead we stash the handle here; each
        // Tick drains completed tasks, disposing their TilePrologueOutput's NativeArrays. Teardown
        // spins to completion and disposes everything remaining.
        //
        // This list is only modified on the main thread (ReleaseTile, DrainPendingDisposal, Dispose
        // are all main-thread). No locking is required.
        private readonly List<WorkHandle<Processing.TilePrologueOutput>> _pendingDisposal = new(8);

        // job-scheduling-design.md §6 exit (2)/(3): the graph arm's twin of the seam pen above — same
        // main-thread-only contract, same reason it exists (a build released while genuinely in flight has
        // no result yet to dispose).
        private readonly List<Processing.TileBuildGraph> _pendingGraphDisposal = new(8);

        // ── teardown-cancel: the manager-lifetime token ───────────────────────────────────────────
        // Cancelled ONCE, at the very top of DoDispose, so every in-flight mesh build aborts before the
        // pen drains below wait on it (a cancelled build still settles to a zero-vertex result via
        // RunWorkerPass's unconditional settle loop, so the drain's existing "if Succeeded" disposal path
        // frees it — no new disposal code). Single owner, main-thread only. A field initializer (not the
        // constructor): DoDispose is terminal + idempotent (VerifiedDisposable guard), so this token is
        // never re-created across a SetSources restyle — restyle stashes-but-does-not-cancel, which is the
        // structural guarantee that builds cancel ONLY on teardown.
        private readonly CancellationTokenSource _lifetimeCts = new();

        /// <summary>The execution policy both mesh-build kicks dispatch through: ThreadPool on desktop/editor,
        /// Inline on a WebGL player, where no worker ever picks a dispatch up (docs/web-target.md).
        /// Settable so a test can force Inline and exercise the web-correct path on desktop; rejects a policy
        /// whose <see cref="IWorkScheduler.RunsInline"/> is true while <see cref="MeshBuildGateForTest"/> is
        /// armed, whose park would then freeze the calling (main) thread. The check reads
        /// <see cref="IWorkScheduler.RunsInline"/> rather than the concrete type — a decorator (e.g. a test
        /// spy) wrapping an Inline scheduler is just as deadlock-prone, and that property's own contract is
        /// what makes a wrapper forward the answer instead of hiding it.</summary>
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

        /// <summary>Test-only park: when non-null, EVERY mesh-build worker parks on this jointly with the
        /// lifetime token before running (the field is intentionally NOT self-clearing — unlike its cited
        /// precedent, which parks once), so a test can hold builds genuinely in-flight and observe that
        /// teardown-cancel releases every parked worker promptly. The test releases them by setting the gate
        /// or by tearing down (which cancels the token both parked workers also wait on). Mirrors
        /// <see cref="MapRenderer.Unity.Text.SymbolReconciler.GateForTest"/>. Internal test-only.
        /// <para>Rejects a non-null gate while <see cref="WorkScheduler"/>'s <see cref="IWorkScheduler.RunsInline"/>
        /// is true — the symmetric guard to <see cref="WorkScheduler"/>'s own, since <c>WaitHandle.WaitAny</c>
        /// on the calling (main) thread would then have no release. Same concrete-type-vs-property reasoning
        /// as that guard: a wrapped Inline scheduler is checked via the property, not <c>is InlineWorkScheduler</c>.</para></summary>
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

        /// <summary>The upstream dependency handle every graph kick's measure step waits on before any of
        /// its own jobs run — threaded straight into <see cref="Processing.TileBuildGraph.ScheduleMeasure"/>'s
        /// own <c>deps</c> parameter (job-scheduling-design.md E2's production-legitimate seam). Stage 3
        /// threads it into BOTH graph kicks now: <see cref="KickSourcelessBackground"/> (background tiles)
        /// and the source tile's prologue-complete hand-off (arm (1) of <see cref="PumpPending"/> /
        /// <see cref="DrainMeshBuilds"/>'s (b)). Mirrors <see cref="MeshBuildGateForTest"/>'s role for the
        /// seam arm: a test schedules its own delay job and hands over its handle here to hold a graph
        /// genuinely in-flight. Stays test-only for the reason that survives past stage 3: the PROLOGUE step
        /// is managed (an <c>IWorkScheduler</c> body, not a job) and yields no <c>JobHandle</c> for this to
        /// gate — only the graph steps have one. Not self-clearing. Internal test-only.</summary>
        internal JobHandle GraphDepsForTest { get; set; }

        // ── S84 mid-flight FETCH holding pen ──────────────────────────────────────────────────────
        // When a tile is released before its fetch completes (rapid zoom/cover churn), the preserved
        // fetch UniTask would otherwise be dropped UNOBSERVED — and a fetch cancelled mid-flight faults
        // (the aborted UnityWebRequest), so UniTask's GC finalizer floods the console with
        // "UnityWebRequestException: Unknown Error". Stash the in-flight fetch here on release; each Tick
        // observes completed ones (and Dispose spins the rest), so every fetch task's outcome is consumed
        // exactly once. Main-thread only, like _pendingDisposal.
        private readonly List<UniTask<SharedDisposable<IDecodedTile>>> _pendingFetchDisposal = new(8);

        // S84: running count of genuine (non-cancellation) fetch errors, for bounded logging.
        private int _fetchErrorCount;

        // Running count of DECODE faults, kept separate from _fetchErrorCount so a malformed tile can never
        // be throttled away inside a burst of network errors (they arrive on the same task under D1).
        private int _decodeErrorCount;

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
            // Tile-load smoothness: same reasoning — a desired (tile, source-SLOT) key is only valid against
            // THIS registry's _pipelines indexing; SetSources rebuilds _pipelines with fresh slots below, so
            // a surviving entry would admit against a re-slotted or removed pipeline (wrong source, or an
            // out-of-range _pipelines[slot] access). No re-request is lost: the next Tick's recompute rebuilds
            // desired from the (unchanged) cover against the new registry.
            _desired.Clear();
            _desiredSet.Clear();

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
                Map.RenderBackend.Brg =>
                    new BRGBackend.TileRenderer(LayerMaterials(_layers), LayerShadowModes(_layers)),
                Map.RenderBackend.GameObject =>
                    new GOBackend.TileRenderer(LayerMaterials(_layers), LayerNames(_layers), LayerShadowModes(_layers)),
                _ => new EntBackend.TileRenderer(LayerMaterials(_layers), LayerNames(_layers), LayerShadowModes(_layers)),
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

        /// <summary>The per-layer shadow-cast declaration in the same declared order as
        /// <see cref="LayerMaterials"/>, so every backend transports one list rather than re-deriving the
        /// answer from the layer kind (which is how three backends drift apart). The value is the layer's
        /// own <see cref="Style.IRenderLayer.CastShadows"/> — only <c>fill-extrusion</c> casts today.</summary>
        /// <remarks>Test seam: <c>internal</c> (not <c>private</c>) so the derivation tooth can call it
        /// without constructing a backend.</remarks>
        internal static List<UnityEngine.Rendering.ShadowCastingMode> LayerShadowModes(Style.RenderLayerSet layers)
        {
            var modes = new List<UnityEngine.Rendering.ShadowCastingMode>(layers.Count);
            for (int i = 0; i < layers.Count; i++) modes.Add(layers[i].CastShadows);
            return modes;
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

        /// <summary>Number of pipelines that actually own a feature source — i.e. sources the style got
        /// WIRED, excluding the synthetic source-less background pipeline. Zero says "nothing was wired",
        /// which is the only positive statement available about a source the style-build SKIPPED: not
        /// throwing, not fetching and not rendering are all equally satisfied by a source substituted with
        /// an empty dataset.</summary>
        internal int WiredFeatureSourceCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _pipelines.Count; i++)
                    if (!_pipelines[i].IsSourceless) n++;
                return n;
            }
        }

        /// <summary>S83b: number of currently loaded (or loading) <c>(tile, source)</c> RECORDS — what the
        /// per-frame loops iterate. With a single source (N=1) this equals the distinct tile count, so every
        /// existing assertion is preserved.</summary>
        internal int LoadedTileCount => _loaded.Count;

        /// <summary>Tile-load smoothness: the ACTIVE set <see cref="AdmitFromDesired"/> bounds against
        /// <see cref="TileSelectionConfig.MaxConcurrentTileLoads"/> — admitted, not-yet-<see cref="LoadedTile.Built"/>
        /// (tile,source) records. Test observability for the concurrency-cap tooth (T2).</summary>
        internal int ActiveLoadCount => CountActiveLoads();

        /// <summary>Tile-load smoothness: number of (tile,source) keys wanting to load but not yet admitted
        /// (no <see cref="_loaded"/> record). Test observability for the admission-gate teeth.</summary>
        internal int DesiredCount => _desired.Count;

        /// <summary>Tile-load smoothness: the TileId at the head of the not-yet-admitted desired list — the
        /// next tile <see cref="AdmitFromDesired"/> will admit. <see cref="TileId"/>'s own default
        /// (Z=X=Y=0) if the desired list is empty. Test observability for the re-prioritization tooth (T4).</summary>
        internal TileId DesiredHeadTile => _desired.Count > 0 ? _desired[0].Tile : default;

        /// <summary>
        /// A-1 pull surface: fill <paramref name="into"/> with the current loaded <c>(source, tile)</c>
        /// membership — every record in <see cref="_loaded"/> mapped from its pipeline slot to its source-id.
        /// The symbol-symbol subsystem calls this each frame and reconciles its active/cached symbol sets against
        /// it (retiring the release/restore push-callbacks). Clears <paramref name="into"/> first; reuses the
        /// caller's list, so it is allocation-free in steady state (no per-frame GC — the S95 zero-alloc
        /// contract). Includes tiles still fetching (not yet built): those simply have no symbol entry yet, so
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

        /// <summary>Tile-load smoothness test observability: every currently-ADMITTED tile's <see cref="TileId"/>
        /// (may repeat across sources — mirrors <see cref="CollectLoadedTileKeys"/>, TileId-only).</summary>
        internal void CollectLoadedTileIds(List<TileId> into)
        {
            into.Clear();
            foreach (var kv in _loaded) into.Add(kv.Key.Tile);
        }

        /// <summary>Tile-load smoothness test observability: every DESIRED-but-not-yet-admitted tile's
        /// <see cref="TileId"/>, in current priority order (index 0 == <see cref="DesiredHeadTile"/>).</summary>
        internal void CollectDesiredTileIds(List<TileId> into)
        {
            into.Clear();
            for (int i = 0; i < _desired.Count; i++) into.Add(_desired[i].Tile);
        }

        /// <summary>
        /// Number of tiles released while their mesh build was still in-flight (Step != BuildStep.None
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

        /// <summary>job-scheduling-design.md §11 fork 2: tiles NEWLY STARTED in the most recent PumpPending
        /// call — incremented once per tile, at its FIRST kick (a source tile's prologue kick, or a
        /// background tile's measure kick), never at a later step transition. This is the quantity
        /// <see cref="Config.MaxMeshBuildsPerTick"/> bounds.</summary>
        internal int TileBuildsStartedLastTick { get; private set; }

        /// <summary>job-scheduling-design.md §3.2/§8 stage 2: <see cref="Mesh.MeshDataArray"/>s allocated by
        /// the graph arm's write step (<see cref="Processing.TileBuildGraph.CompleteMeasureAndScheduleWrite"/>)
        /// in the most recent PumpPending/DrainMeshBuilds pass — one per non-empty layer.</summary>
        internal long MeshDataArraysAllocatedLastKick { get; private set; }

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
        /// <para>Unlike the two symbol providers — whose levels their pass already stores, making the refresh a
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
            // one walks every record, built or not). job-scheduling-design.md §8 stage 3: a completed
            // PROLOGUE has nothing to consume yet (it hands off to Measure, uncharged) — only a completed
            // WRITE step is genuinely "write complete, unconsumed", so backlog counts that alone now.
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
                FractionalZoom         = _coverKeyZoom,
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

        /// <summary>Test-only, backend-agnostic: the global material index of each mesh in
        /// <see cref="GetTileMeshes"/>, SAME order (both walk <c>_pipelines</c>/<c>lt.Meshes</c> in lockstep) —
        /// tooth (g)'s own accessor, for asserting draw order across a tile with multiple layer kinds
        /// (job-scheduling-design.md §8 stage 5 Group B: all graph-arm now, joined by dense request
        /// index).</summary>
        internal int[] GetTileMaterialIndices(TileId id)
        {
            List<int> all = null;
            for (int s = 0; s < _pipelines.Count; s++)
            {
                if (_loaded.TryGetValue(new LoadedKey(id, _pipelines[s].Slot), out var lt) && lt.MaterialIndices != null)
                {
                    all ??= new List<int>(8);
                    all.AddRange(lt.MaterialIndices);
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
        /// Test-only: true once every loaded tile has finished building (or is definitively absent) AND
        /// nothing remains in the not-yet-admitted <see cref="_desired"/> list. S47/S51: returns false while
        /// any tile has a pending mesh build. Tile-load smoothness: a concurrency-capped run leaves entries
        /// in <see cref="_desired"/> that a <c>_loaded</c>-only check would silently miss (they have no
        /// record at all until admitted) — without this, "settled" could read true while the desired list
        /// still wants to load tiles the cap deferred.
        /// </summary>
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

        /// <summary>
        /// One frame of the tile loop. Allocation-free in steady state.
        ///
        /// S47/S51: no main-thread mesh build. PumpPending only KICKS background mesh builds
        /// on fetch completion; ConsumeMeshBuilds polls and CONSUMES completed handles
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

            // Defensive backstop, NOT the normalisation point. MapHost does pass null for planar
            // (UseGlobe ? new SphericalProjection() : null), but MapCamera's constructor already resolves it
            // (MapCamera.cs: `Projection = projection ?? new WebMercatorProjection()`), and MapView feeds
            // Camera.Projection into the config — so cfg.Projection is non-null by the time this reads it and
            // this `??` never fires in production. Kept only so a future config path that bypasses MapCamera
            // fails as a wrong default rather than a NullReferenceException three layers down.
            _projection = cfg.Projection ?? new WebMercatorProjection(); // cache for the mesh build bake (Level-1); same projection as origin + frame

            // A changed clip window means every cached mesh was baked against a different tile buffer, so the
            // PreparedTileCache must not serve them. Compared field-by-field rather than via ValueType.Equals,
            // which reflects/boxes — this runs every Tick.
            //
            // What this Clear() does NOT do, stated precisely because the obvious reading is wrong: it evicts
            // what is in the cache AT THIS MOMENT. A tile that is in cover now keeps its stale-window mesh,
            // and when it later LEAVES cover that mesh is Put() into the (now clean) cache under a
            // PreparedKey of (Style, Tile, LayerId) — which carries no clip component, so it is
            // indistinguishable from a freshly-baked entry. Re-entering cover therefore serves it verbatim.
            // The stale geometry survives arbitrarily many leave/re-enter cycles; only an LRU eviction or a
            // restyle clears it. Acceptable because this is a tuning knob, not a live visual toggle — but do
            // not read the Clear() as a guarantee that a value change is eventually reflected everywhere.
            // Closing it properly means keying the cache on the bake parameters; see the design doc.
            if (cfg.BufferClip.IsEnabled             != _bufferClip.IsEnabled ||
                cfg.BufferClip.KeepAtReferenceExtent != _bufferClip.KeepAtReferenceExtent)
            {
                _bufferClip = cfg.BufferClip;
                _prepared.Clear();
            }

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

            // Tile-load smoothness: the shared render-space priority context for THIS Tick — computed once
            // (matches FrustumTileSelector's "look-at at the origin" frame exactly) and reused by BOTH the
            // admission gate and PumpPending's paint-order sort below, so a corner tile can never win either
            // race just because of Dictionary/list enumeration order.
            var priorityCtx = TilePriorityContext.From(in cam, cfg.FramingViewportPx, cfg.Projection,
                cfg.PriorityStrategy);

            // The EXPENSIVE cover recompute (select descent + desired-list merge) is gated on _coverDirty
            // ALONE: a clean camera with tiles still pending must NOT re-run the descent — the cover set is
            // unchanged, so the merge would be a pure no-op that just re-taxes the frame the consume is
            // already loading (stall #6).
            if (_coverDirty)
            {
                CoverRecomputesLastTick = 1; // S95: the full recompute (descent + diff) runs this Tick

                // NOT `using var` — closed explicitly right after the merge below (correct CoverSelect
                // attribution; admission/pump/drain are separate costs, measured by their own markers).
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

                // Tile-load smoothness (E3/E5 merge, NOT rebuild): tiles newly entering the cover — one key
                // per (tile, source pipeline) whose resolved zoom range admits the tile (decision 10: one
                // camera-driven cover, per-pipeline zoom clamp) — join the DESIRED list instead of fetching
                // immediately; admission is priority-ordered and concurrency-capped (AdmitFromDesired, below
                // — every Tick, so entries added THIS Tick still admit THIS Tick).
                for (int i = 0; i < _cover.Count; i++)
                {
                    TileId id = _cover[i];
                    for (int s = 0; s < _pipelines.Count; s++)
                    {
                        var p = _pipelines[s];
                        if (id.Z < p.MinZoom || id.Z > p.MaxZoom) continue; // source doesn't serve this zoom
                        var key = new LoadedKey(id, p.Slot);
                        if (_loaded.ContainsKey(key)) continue; // already admitted — untouched (never re-queued)
                        if (_desiredSet.Add(key)) _desired.Add(key);
                    }
                }

                // E5(ii/iii): drop desired entries whose tile left the cover on this recompute — an
                // already-admitted (_loaded) record is NEVER touched here (no cancel-in-flight; T3), and a
                // still-desired entry still in cover is simply kept for the next AdmitFromDesired pass.
                for (int i = _desired.Count - 1; i >= 0; i--)
                {
                    LoadedKey dk = _desired[i];
                    if (!_coverSet.Contains(dk.Tile))
                    {
                        _desiredSet.Remove(dk);
                        _desired.RemoveAt(i);
                    }
                }

                // Stall #2: records whose tile left the cover are ENQUEUED for deferred release, not freed
                // here. DrainReleaseQueue (below, every frame) frees up to MaxReleasesPerTick of them per
                // Tick. _releaseQueued dedups a record already queued by an earlier recompute.
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

                sCoverSel.Dispose();
            }

            // Runs EVERY Tick — clean or dirty — so pending tiles keep progressing every frame and a
            // concurrency cap reached on a dirty Tick keeps draining once the camera goes still (admission
            // must not be gated on _coverDirty, unlike the recompute above: the desired list can still hold
            // deferred entries long after the cover itself stopped changing).
            AdmitFromDesired(in priorityCtx, cfg.MaxConcurrentTileLoads);
            PumpPending(cam, cfg.MaxConsumesPerTick, cfg.MaxMeshBuildsPerTick, cfg.MaxVerticesPerTick, in priorityCtx);

            // Stall #2: drain a budgeted slice of the deferred-release backlog EVERY Tick (re-validated
            // against the current _coverSet, so a tile that just re-entered on a pan-back is skipped/kept,
            // not destroyed-and-refetched) — runs after admission/pump so this Tick's departures start
            // freeing immediately, bounded to MaxReleasesPerTick.
            DrainReleaseQueue(cfg.MaxReleasesPerTick);
        }

        /// <summary>
        /// S47 deterministic drain — blocks the calling thread until all in-flight fetch tasks and mesh
        /// build handles complete, then consumes their results synchronously (uploads meshes +
        /// creates GameObjects). After this returns, <see cref="AllTilesSettled()"/> is guaranteed
        /// true for all currently loaded tiles.
        ///
        /// This is a full drain: it handles tiles at any stage of the pipeline:
        ///   (a) Fetch in-flight: parks until the fetch UniTask completes, then kicks mesh build inline.
        ///   (b) Mesh build in-flight: parks until the handle completes, then consumes inline.
        ///   (c) Neither (tile not yet fetched): marks Built=true (nothing to do).
        ///
        /// Safe: the fetch UniTask's completion stays OFF the PlayerLoop (see
        /// <see cref="MapRenderer.Unity.Rendering.Tile.Processing.TileDecodeDispatch"/>'s class doc) —
        /// supplied by the decode hop on the <c>HasData</c> path, and by synchronous/inline completion
        /// otherwise — so its continuation fires without needing the Unity PlayerLoop to advance.
        /// The mesh build dispatches through <see cref="WorkScheduler"/>, whose completion fires on the
        /// COMPLETING thread and is never posted to the PlayerLoop (I-2) — the same non-blocking guarantee,
        /// by a different mechanism. Parking on either completion via
        /// <see cref="UniTaskParkExtensions.WaitOffPlayerLoop"/> from the main thread therefore does not
        /// deadlock (no PlayerLoop dependency to dead-end on).
        ///
        /// Called by test helpers for deterministic settle. NOT called from the production Update path.
        ///
        /// <para>Tile-load smoothness: admits EVERY entry in <see cref="_desired"/> first (uncapped —
        /// <see cref="AdmitFromDesired"/> with <see cref="int.MaxValue"/>), so a concurrency-capped run
        /// still reaches the "fully-settled state is unchanged" invariant: this is the deterministic full
        /// drain, so it ignores the concurrency cap exactly as it already ignores the per-tick rate caps.
        /// Order doesn't matter when admitting everything, so no priority sort runs here. Uses the
        /// <c>_projection</c> cached by the last real <see cref="Tick"/> — this method is only ever called
        /// after at least one Tick has run (mirrors <see cref="KickMeshBuild"/>'s existing reliance on the
        /// same cached field).</para>
        /// </summary>
        internal void DrainMeshBuilds(CameraProperties cam)
        {
            // Only .Projection is read on this uncapped path (cap == int.MaxValue skips the priority sort
            // entirely — order is moot when admitting everything) — the other fields are never touched.
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
                string     sourceId = _pipelines[key.Slot].SourceId;
                LoadedTile lt       = _loaded[key];

                // (a) If fetch is still in-flight, spin until it completes and kick mesh build.
                if (!lt.FetchCompleted)
                {
                    var req = lt.Request;
                    // Parks on a kernel event via WaitOffPlayerLoop: req's completion stays OFF the
                    // PlayerLoop (see TileDecodeDispatch's class doc) — supplied by the decode hop on the
                    // HasData path, and by synchronous/inline completion otherwise — so its continuation
                    // wakes this wait without needing the PlayerLoop — no deadlock.
                    req.WaitOffPlayerLoop(10000);

                    lt.FetchCompleted = true;

                    // S84: observe the fetch outcome exactly once (Succeeded / Faulted / Canceled) so a
                    // faulted fetch is never dropped unobserved. Epic A / A7: the mint (bytes → handle) now
                    // happens INSIDE ITileFeatureSource.GetTile; this just observes the handle it returned.
                    // Non-null ⇒ STORED in the record below and kicked by (a2) in this same iteration; null
                    // ⇒ absent, faulted or cancelled, in which case TakeDecodeFromFetch produced no lease
                    // and there is nothing to release.
                    SharedDisposable<IDecodedTile> handle = TakeDecodeFromFetch(req);
                    if (handle != null)
                    {
                        // STORE it in the record rather than passing it straight to KickMeshBuild. This used
                        // to be the one place a decoded-tile reference lived as a bare LOCAL, and a bare
                        // local is the one owner no funnel can see: a main-thread prologue throw inside the
                        // kick unwound this frame with the last reference in it. Held in `lt.Decode` the
                        // reference is instead recovered by a funnel on every exception path — this `lt` is
                        // a COPY that is only written back to `_loaded` at the end of the iteration, so a
                        // throw leaves `_loaded[key]` with `FetchCompleted == false` and its PRESERVED
                        // `Request`, which funnel 2 (DiscardFetchOutcome) consumes and releases.
                        // Behaviourally identical on the happy path: (a2) below kicks it in this same
                        // iteration — its guard is satisfied the moment this line runs — with the same
                        // default (null) symbolPass, so the drain stays symbol-silent BY CONSTRUCTION.
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

                // (a2) S55: fetch completed with data but mesh build not yet kicked — either cap-deferred
                // in the normal pump, or observed by (a) just above (the two paths share this ONE kick site
                // so the record is the owner in both). Kick inline here — drain ignores per-tick caps. A5b:
                // symbolPass left at its default (null) — a cap-deferred tile that settles via drain never
                // attempts a symbol build either (§Q-Drain KEEP), matching the drain path's existing
                // symbol-silent behaviour.
                if (lt.FetchCompleted && lt.Decode != null && lt.Step == BuildStep.None)
                {
                    lt.MeshBuildTask = KickMeshBuild(lt, id, lt.Decode, sourceId);
                    lt.Step          = BuildStep.Prologue;
                    // R1: the record KEEPS its reference — no transfer, no null-out. The kick took its own
                    // separate reference in KickMeshBuild's prologue; this `lt.Decode` stays live until
                    // funnel 1 (RenderTeardownRecord) or funnel 2 (DiscardFetchOutcome, if this `lt` copy
                    // never makes it back to `_loaded`) releases it.
                }

                // Epic A / A2 (design §E step 4, HIGH a): a source-less (background) record the Tick loop
                // only CREATED (never kicked — PumpPending may not have run yet, or was cap-deferred). Kick
                // inline here — drain ignores per-tick caps. Without this branch the record falls straight to
                // the "else { lt.Built = true; }" no-mesh settle below (§F tooth 12) — invisible in exactly
                // the deterministic/snapshot harnesses that settle via DrainMeshBuilds.
                // job-scheduling-design.md §8 stage 2: this is now a GRAPH kick, on every projection.
                if (lt.FetchCompleted && lt.Step == BuildStep.None && lt.Decode == null &&
                    _pipelines[key.Slot].IsSourceless)
                {
                    lt.Graph = KickSourcelessBackground(id, lt.TileOriginRender);
                    lt.Step  = BuildStep.Measure;
                }

                // (b) A PROLOGUE build in-flight — spin, then hand off to the graph (job-scheduling-design.md
                // §8 stage 3). No `else` below: a tile that just handed off here is picked up in THIS SAME
                // iteration by (b')'s Measure/Write arm — one drain walks all three steps.
                if (lt.Step == BuildStep.Prologue)
                {
                    // Bridges into the UniTask I/O chain so WaitOffPlayerLoop applies: the WorkScheduler
                    // completes the handle on the completing thread, never posting to the PlayerLoop (I-2),
                    // so this park wakes without needing the PlayerLoop to advance.
                    UniTask<Processing.TilePrologueOutput> buildTask = lt.MeshBuildTask.ToUniTask();
                    buildTask.WaitOffPlayerLoop(10000);

                    if (!lt.MeshBuildTask.IsSucceeded)
                    {
                        FinishConsume(ref lt);
                    }
                    else
                    {
                        Processing.TilePrologueOutput output = lt.MeshBuildTask.GetResult();
                        // Store BEFORE scheduling — see PumpPending's identical comment on why: a throw from
                        // ScheduleMeasureFromDecode must find `_loaded[key]` already past the completed
                        // MeshBuildTask, or a later drain re-GetResult()s a TilePrologueOutput whose columns
                        // and decode reference the catch already freed — the exact double-free this guards.
                        lt.MeshBuildTask = default;
                        lt.Step          = BuildStep.None;
                        _loaded[key]     = lt;
                        lt.Graph = Processing.TileBuildGraph.ScheduleMeasureFromDecode(
                            output.Layers, output.Decode, GraphDepsForTest);
                        lt.Step = BuildStep.Measure;
                    }
                }

                // (b') GRAPH measure/write in-flight — job-scheduling-design.md §3.3: Complete() from the
                // main thread executes a not-yet-started job inline, so there is no PlayerLoop dependency to
                // dead-end on — no WaitOffPlayerLoop, no timeout needed for this arm.
                if (lt.Step == BuildStep.Measure || lt.Step == BuildStep.Write)
                {
                    if (lt.Step == BuildStep.Measure)
                    {
                        int allocated;
                        using (PmMeshDataAllocate.Auto()) lt.Graph.CompleteMeasureAndScheduleWrite(out allocated);
                        MeshDataArraysAllocatedLastKick += allocated;
                        lt.Step = BuildStep.Write;
                    }
                    // CompleteWriteAndTakePayloads is idempotent — a tile arriving here ALREADY partially
                    // consumed by an earlier Tick (Step == Write, Graph alive, some slots already nulled)
                    // gets back the SAME array with those slots still nulled.
                    Style.MeshDataPayload[] payloads = lt.Graph.CompleteWriteAndTakePayloads();
                    // lt.Graph is NOT disposed here — see PumpPending's identical guard for why: FinishConsume
                    // is the single disposal site, so both call sites keep the same invariant regardless of
                    // budget (this one always completes in one call because the budget is int.MaxValue, but
                    // the invariant does not rely on that).
                    // Drain ignores per-frame caps: unbounded budget consumes ALL layers in one call → Built.
                    ConsumeMeshBuild(id, ref lt, payloads, int.MaxValue, int.MaxValue, out _, out _);
                }
                else if (lt.Step == BuildStep.None && !lt.Built)
                {
                    lt.Built = true;
                }

                _loaded[key] = lt;
            }
        }

        /// <summary>Blocks until every in-flight fetch/mesh-build task among <c>_loaded</c> tiles completes,
        /// parking via <see cref="UniTaskParkExtensions.WaitOffPlayerLoop"/> instead of polling. Consumes,
        /// kicks, and harvests NOTHING — a cap-deferred tile (fetch observed but no build kicked yet) is
        /// skipped, since there is no in-flight task to park on; the real <c>LateUpdate</c> Tick kicks and
        /// consumes it. Callers that need the mesh actually consumed still tick <c>LateUpdate</c>; this method
        /// only parks off the PlayerLoop until in-flight work completes. See <see cref="DrainMeshBuilds"/> for
        /// the consuming peer this method deliberately does not replicate.
        ///
        /// <para>Pump callers invoke this once per settle iteration, re-scanning <c>_loaded</c> each time. For
        /// the FETCH task a timeout still means "still pending", and re-parking on a still-pending
        /// <c>UniTask</c> double-registers its single continuation (see
        /// <see cref="UniTaskParkExtensions.WaitOffPlayerLoop"/>'s contract) — the mesh-build handle's own
        /// bridge does not share that specific hazard (a <see cref="WorkHandle{T}"/>'s completion source
        /// appends continuations rather than occupying one slot), but a hang there is exactly as much a bug.
        /// Either way a per-task timeout throws <see cref="System.TimeoutException"/> — a hung fetch/build is
        /// a hard failure surfaced loudly, never silently re-waited. A healthy task completes in milliseconds
        /// and never approaches <paramref name="timeoutMs"/>.</para></summary>
        /// <param name="timeoutMs">The maximum time to wait per parked task, in milliseconds.</param>
        /// <exception cref="System.TimeoutException">A loaded tile's fetch or mesh-build task did not complete
        /// within <paramref name="timeoutMs"/> (a hang).</exception>
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
                // job-scheduling-design.md §3.3: this method's contract is "advances no step, consumes
                // nothing" — Complete() satisfies that (it does not take/consume payloads) and cannot throw
                // TimeoutException, because a graph is bounded by in-flight CPU with no PlayerLoop dependency
                // to dead-end on.
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

        /// <summary>
        /// S47/S51/S55 pump: per tile, kick its mesh build then consume the built mesh.
        ///
        /// Mesh build (fetch→kick): for tiles whose fetch completed, mint the shared decode entry and kick a
        /// background mesh build (S55: at most <paramref name="maxMeshBuildsPerTick"/> kicks per
        /// Tick — the entry is retained in <see cref="LoadedTile.Decode"/> until the cap allows).
        ///
        /// Consume (build→upload): for tiles whose mesh-build handle is completed, consume the result on the
        /// main thread (UploadMesh → backend registration) MESH-by-mesh (S87). The per-frame budget is dual —
        /// at most <paramref name="maxConsumesPerTick"/> layer MESHES AND
        /// <paramref name="maxVerticesPerTick"/> vertices per frame, whichever binds first; a tile whose
        /// layers exceed the remaining budget is consumed partially and RESUMES (via its ConsumeCursor) on
        /// later Ticks — a single rich tile never lands in one frame.
        ///
        /// Returns the count of tiles still pending (fetch or mesh build in-flight, or cap-deferred).
        ///
        /// Tile-load smoothness: the work list built below is sorted by <paramref name="priorityCtx"/>
        /// before the processing loop — this is the PAINT-order seam (as load-bearing as the admission
        /// gate): without it, a corner tile can still win the ≤N-kicks/consumes-per-Tick race purely from
        /// Dictionary enumeration order, even with priority-ordered admission (the "middle stays white"
        /// symptom). H3: the sort reuses the shared <c>_toRelease</c> scratch FIELD — safe because it is
        /// filled and fully consumed within this one single-threaded call (never live across calls), the same
        /// discipline the "departing" vs. "unsettled" dual-use of this scratch list already relies on.
        ///
        /// Greppability note: there is NO .Schedule().Complete() in this method.
        /// </summary>
        private int PumpPending(
            CameraProperties cam,
            int              maxConsumesPerTick,
            int              maxMeshBuildsPerTick,
            int              maxVerticesPerTick,
            in TilePriorityContext priorityCtx)
        {
            using var sFetchPoll = PmFetchPoll.Auto();

            // Reset per-tick observability counters.
            TileBuildsStartedLastTick     = 0;
            VerticesConsumedLastTick      = 0;
            TilesConsumedLastTick         = 0;
            MeshesConsumedLastTick        = 0;
            MeshDataArraysAllocatedLastKick = 0;

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

            // Tile-load smoothness (E4): nearest-center-first paint order — the same priority the admission
            // gate uses, so the ≤buildCap kicks and ≤consumeCap/vertsCap consumes below reach the center
            // before the edges.
            SortByPriority(_toRelease, in priorityCtx);

            int pending          = 0;
            int meshesConsumed   = 0;
            int verticesConsumed = 0;
            bool scheduledThisPass = false;

            for (int i = 0; i < _toRelease.Count; i++)
            {
                LoadedKey  key      = _toRelease[i];
                TileId     id       = key.Tile;
                string     sourceId = _pipelines[key.Slot].SourceId;
                LoadedTile lt       = _loaded[key];

                // ── (1) A completed PROLOGUE hands off to the graph — job-scheduling-design.md §8 stage 3 ──
                // Nothing to consume here (no mesh, no budget charge): a faulted/cancelled prologue settles
                // via FinishConsume exactly like a faulted seam build used to; a succeeded one schedules the
                // measure graph and moves on. Step transitions of an already-admitted tile are uncharged —
                // TileBuildsStartedLastTick is NOT bumped here (the tile already counted as started at its
                // prologue kick, arm (5)).
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
                    // Store BEFORE scheduling — ScheduleMeasure owns `output` from the call, including on
                    // its own throw path, so the map must not keep a handle whose result the catch already
                    // disposed. If ScheduleMeasure throws, the record is left Step == None, MeshBuildTask ==
                    // default, Decode still the RECORD's own (untouched here) — arm (5) re-kicks it on a
                    // later pass; the schedule error propagates readable rather than being masked by a second
                    // free.
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

                    // CompleteWriteAndTakePayloads is idempotent — the graph owns the array from its first
                    // call on, so a tile resumed here across Ticks (Step stays Write; a budget-bound
                    // ConsumeMeshBuild below can return incomplete) gets back the SAME array with whatever
                    // slots an earlier call already nulled. lt.Graph is NOT disposed here: every other
                    // Step == Write reader in this class (next Tick included) still expects it non-null — see
                    // LoadedTile.Graph's own doc. FinishConsume disposes it, exactly once, only once
                    // ConsumeMeshBuild actually reports complete.
                    Style.MeshDataPayload[] payloads = lt.Graph.CompleteWriteAndTakePayloads();

                    // A Burst job cannot fault — no faulted-task early-out needed here.
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
                    // job-scheduling-design.md §11 fork 2: an already-admitted tile's write transition is
                    // uncharged — it is not re-gated on its way through its own steps.
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
                    // Stall #2: don't start a NEW background build for a record already condemned to release
                    // (DrainReleaseQueue will free it within a few frames). In-flight builds still finish and
                    // land in the existing disposal pens; this only avoids kicking fresh work for a doomed tile.
                    if (_releaseQueued.Contains(key))
                    {
                        pending++;
                        continue;
                    }

                    if (TileBuildsStartedLastTick >= buildCap)
                    {
                        // Cap reached this tick — the record keeps its reference (and with it the decoded
                        // tile's buffers) until a later Tick kicks, or teardown releases it.
                        pending++;
                        continue;
                    }

                    // Epic A / A5b: obtain the symbol worker pass on the MAIN THREAD, isolated (a throwing
                    // factory must never fault the pump — mirrors TakeDecodeFromFetch's per-tile fault
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

                    lt.MeshBuildTask = KickMeshBuild(lt, id, lt.Decode, sourceId, symbolPass);
                    lt.Step          = BuildStep.Prologue;
                    // R1: the record KEEPS its reference — no transfer, no null-out, no Release() here
                    // (which would free the tile mid-build). The kick took its own separate reference in
                    // KickMeshBuild's prologue; this `lt.Decode` stays live until funnel 1
                    // (RenderTeardownRecord) releases it, kicked or not.
                    TileBuildsStartedLastTick++; // this is where a source tile is admitted — the only place it is charged
                    pending++; // mesh build now in-flight
                    _loaded[key] = lt;
                    continue;
                }

                // ── (6) Epic A / A2 (design §E step 4, HIGH 2): kick a source-less (background) build ────
                // A pending record the Tick cover-loop only CREATED (never kicked — no Decode to
                // dispatch on). Rides the SAME condemned-skip + buildCap throttle as the byte path above, so
                // background loads at the shared build cadence, never as one synchronous cover-wide burst.
                // job-scheduling-design.md §8 stage 2: this is now a GRAPH kick, on every projection.
                if (lt.FetchCompleted && lt.Step == BuildStep.None && lt.Decode == null &&
                    _pipelines[key.Slot].IsSourceless)
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

                // ── Observe completed fetch ────────────────────────────────────────────────────
                // S84: observe the fetch outcome exactly once (handles Succeeded / Faulted / Canceled) so
                // a faulted fetch is never left for UniTask's unobserved-exception finalizer.
                lt.FetchCompleted = true;
                SharedDisposable<IDecodedTile> handle = TakeDecodeFromFetch(lt.Request);
                if (handle != null)
                {
                    // A4/A7: retain the decode-provisioning handle ONCE per fetch — the single fork point
                    // where one (source, tile) splits into the mesh and symbol cadences. Kick deferred to a
                    // subsequent Tick (capped by buildCap). A5b: the symbol cadence is no longer pushed here —
                    // it is driven from the KICK block above (SymbolWorkerFactory.TryBeginBuild), sharing
                    // this SAME entry. Epic A / A7: the DECODE already happened inside
                    // ITileFeatureSource.GetTile — this just retains what it returned, and with it the
                    // creator's REFERENCE. The acquire was the decode, not this line; from here the record
                    // owns it, and R1: RenderTeardownRecord (funnel 1) alone is what ends that ownership —
                    // a kick no longer transfers it (KickMeshBuild takes its own separate reference instead).
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

            // job-scheduling-design.md §3.3: flush once per pass, not per tile — a job scheduled from the
            // main thread is not handed to workers until the batch is flushed or an implicit sync point
            // arrives.
            if (scheduledThisPass) JobHandle.ScheduleBatchedJobs();

            return pending;
        }

        /// <summary>
        /// Starts a background mesh build that reads the shared decode (decoding it if this is the first
        /// cadence to reach it — A4) and runs every tile-mesh render layer bound to this <c>(source, tile)</c>
        /// through <see cref="Processing.TileLayerProcessorRunner"/> — the fan-out point (Epic A / A1).
        /// Returns immediately (non-blocking on <see cref="ThreadPoolWorkScheduler"/>; the body already ran
        /// by the time this returns on <see cref="InlineWorkScheduler"/>).
        ///
        /// Dispatches through <see cref="WorkScheduler"/> — <see cref="ThreadPoolWorkScheduler"/> on
        /// desktop/editor, <see cref="InlineWorkScheduler"/> on a WebGL player, where no worker ever picks a
        /// ThreadPool dispatch up (docs/web-target.md). Under both policies, completion fires on the
        /// COMPLETING thread and is never posted to the PlayerLoop (I-2), so <see cref="DrainMeshBuilds"/>'s
        /// and <see cref="Dispose"/>'s synchronous-spin polls — which never pump the PlayerLoop — cannot
        /// dead-end waiting for a continuation that would only ever run there.
        ///
        /// The returned <see cref="WorkHandle{T}"/> is pollable across frames with no <c>.Preserve()</c>
        /// needed — its backing completion source never recycles (see <see cref="WorkHandle{T}"/>'s doc).
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
        /// symbols never appeared in snapshots and no test drove symbols via drain).</para>
        /// </summary>
        private WorkHandle<Processing.TilePrologueOutput> KickMeshBuild(
            LoadedTile                       lt, TileId id, SharedDisposable<IDecodedTile> decode, string sourceId,
            Processing.ISymbolTileWorkerPass symbolPass = null)
        {
            // OWNERSHIP (R1 — symmetric self-owned references): `decode` is BORROWED for this whole
            // prologue and stays the CALLER's — the record's reference is no longer transferred into the
            // kick, and no call site nulls its field around this call any more (see the LoadedTile.Decode
            // field doc: RenderTeardownRecord, funnel 1, is its only release, for the record's whole in-cover
            // lifetime, kicked or not). The KICK instead takes its OWN separate reference — `decode.Acquire()`
            // below, immediately before the scheduler call that reads it is created — and releases exactly that
            // one from the body's `finally`. The `WorkScheduler.Schedule` hand-off can itself throw synchronously
            // (OOM), so the Acquire is guarded (try/catch below) to release on that path too. A main-thread prologue
            // throw therefore never touches the kick's reference at all (it is not acquired yet), and the
            // caller's own reference is untouched either way — one of the funnels frees it in due course.
            // A single `catch` below suffices under BOTH scheduler policies precisely because `Schedule` never
            // propagates a body exception (I-3: both schedulers wrap the body in their own try/catch and route
            // a throw to `TrySetException`) — so a body throw is always absorbed by the body's own `finally`,
            // never by this method's `catch`, and `Schedule` itself can only throw for a dispatch failure that
            // ran BEFORE the body (and its `finally`) ever existed. That holds whether the body runs later on a
            // pool thread or synchronously inside `Schedule` on the calling thread (Inline) — the release count
            // is exactly one either way.
            var     layersSnapshot = _layers.SnapshotLayers();
            double  zoom           = id.Z;
            double3 tileOrigin     = lt.TileOriginRender;

            // S89 Stage C: DENSE per-(tile, source) produce. Collect this-source layers in declared (draw)
            // order — each dense slot becomes one processor, carrying its own global material index. No
            // full-width sparse union / decision-5c null slots. S82: computed via the shared helper (also
            // used by the cache transfer/probe paths) — the dense ids are consumed synchronously below (one
            // processor built per id) before any worker capture; the scratch list itself is never captured
            // across the thread boundary.
            ComputeDenseLayerIds(sourceId, _denseLayerIds);
            int dense = _denseLayerIds.Count;

            // ── MAIN THREAD: one ITileMeshLayerProcessor per this-source layer, rented from its pool —
            // job-scheduling-design.md §8 stage 5 Group B: the graph is the only mesher, so AllocateForKick
            // allocates NO Mesh.MeshDataArray any more (that happens later, in the graph's write step; see
            // PmMeshDataAllocate's OTHER bracket, around CompleteMeasureAndScheduleWrite). The worker builds
            // each processor's graph request in place; the main thread hands it to the graph at consume.
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
                    // teardown-cancel test gate: when non-null, park jointly on the test gate and the lifetime
                    // token before doing any work, so a test can hold a build genuinely in-flight (and thereby
                    // force the cancel-settle path at teardown). Read into a local so the null-check and the
                    // wait see the same value. Released by the test setting the gate, or by teardown cancelling
                    // the token. Not a busy-wait — a kernel WaitHandle.WaitAny park.
                    var gate = MeshBuildGateForTest;
                    if (gate != null) WaitHandle.WaitAny(new[] { gate.WaitHandle, token.WaitHandle });

                    // FUNNEL 3 successor: the kick's OWN reference, acquired in the main-thread prologue above —
                    // no longer a transfer of the record's. It covers the mesh pass AND the un-parked symbol pass
                    // below, which is what makes those two cadences share a single decoded tile (and, with it,
                    // one geometry buffer per source-layer across both). job-scheduling-design.md §8 stage 3: on
                    // SUCCESS the reference TRANSFERS to the returned TilePrologueOutput (freed once handed to
                    // TileBuildGraph.ScheduleMeasure, or by a pen's TilePrologueOutput.Dispose() if it never gets
                    // that far); on a FAULT here it is released by the catch below — "transferred or released" is
                    // exhaustive, so there is no `finally` any more. Releasing frees the tile's
                    // Allocator.Persistent buffers ONLY if it is the last reference — the record's own is always
                    // still outstanding at this point (RenderTeardownRecord is what drops it), and a parked
                    // symbol build may hold a further one of its own, taken during the symbol pass below.
                    try
                    {
                        // The fan-out point (Epic A / A1): read the already-decoded tile off the lease, run every
                        // this-source processor once in dense order against that same tile, then settle every one
                        // of them exactly once — the moved form of today's ensure-wrapped loop.
                        Processing.TilePrologueOutput output =
                            Processing.TileLayerProcessorRunner.RunWorkerPass(decode, in context, processors, token);

                        // Epic A / A5b (§Q5): fault domain 2, disjoint from the mesh domain above — the mesh
                        // OUTPUT is already built, so a symbol fault below can never strand a mesh
                        // MeshDataArray. The pass is infallible BY CONTRACT (owns its own try/catch), but this
                        // outer guard makes that invariant STRUCTURAL rather than a trust in the contract —
                        // belt-and-braces over the pass's own inner guard, exactly A1's per-processor
                        // Complete() guard precedent (RunWorkerPass above).
                        //
                        // teardown-cancel: skip the handoff once the lifetime token is cancelled.
                        // RunWorkerAndHandoff may TryParkBuild -> enqueue into SymbolSubsystem, which
                        // MapView.Teardown disposes AFTER TileManager.DoDispose returns (TileManager -> Layers
                        // -> SymbolPlacementSystem -> Symbols) — enqueuing into a subsystem about to be torn down is exactly
                        // the SymbolTileBlock leak vector this stage exists to close.
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
                // The hand-off itself — the closure/state-machine allocation, or the pool schedule — can
                // throw SYNCHRONOUSLY (OOM) before the lambda ever runs, so its own catch (above) never gets
                // a chance to release. Mirror TryParkBuild's Acquire->Enqueue guard: free the kick's
                // reference here, or the tile's Allocator.Persistent buffers leak with nothing left to
                // observe them.
                decode.Release();
                throw;
            }
        }

        /// <summary>
        /// Epic A / A2 (design §B Q1/Q2), job-scheduling-design.md §8 stage 3: starts the graph-arm build for
        /// a SOURCE-LESS (background) tile — no bytes, no decode, no <c>IWorkScheduler</c>. Called from
        /// <see cref="PumpPending"/> (under the shared build cap) and <see cref="DrainMeshBuilds"/>
        /// (uncapped). Schedules the measure graph for every dense background layer
        /// (<see cref="ComputeSourcelessLayerIds"/>) directly on the main thread — the quad materializer
        /// (<see cref="Processing.BackgroundQuad.BuildLayerInput"/>) is small enough to run at kick time, so
        /// the graph is scheduled without a managed prologue (background tiles skip the prologue step
        /// entirely — unlike a source tile).
        ///
        /// <para>No projection gate — job-scheduling-design.md's E1 was resolved by reordering (the globe
        /// subdivide stage landed first), so the graph builds both arms and this kicks on every projection.
        /// Allocates NO <see cref="Mesh.MeshDataArray"/> here — that is the write step's job
        /// (<see cref="Processing.TileBuildGraph.CompleteMeasureAndScheduleWrite"/>), which is where
        /// <c>PmMeshDataAllocate</c> now brackets the allocation.</para>
        ///
        /// <para>Mints the full-tile-extent quad ONCE (<see cref="Processing.BackgroundQuad.MintFullExtentGeometry"/>)
        /// and shares it, BORROWED, across every dense layer's <see cref="FillMeshPipeline.LayerInput"/> —
        /// every background layer over one tile draws the identical quad, so one allocation backs all of
        /// them. Ownership passes to <see cref="Processing.TileBuildGraph.ScheduleMeasure"/> as the per-tile
        /// <c>ownedGeometry</c> argument, which disposes it once, after every layer — never per-layer (a
        /// build's own Dispose never touches <c>Input.Geometry</c>, matching
        /// <c>FillMeshPipeline.LayerInput.Geometry</c>'s own documented BORROWED contract).</para>
        /// </summary>
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
                // BuildLayerInput's out visitOrder is folded into `input.RingVisitOrder` already — the
                // build owns it (its own Dispose frees it); `geometry` is shared/borrowed, disposed once
                // below via ScheduleMeasure's ownedGeometry argument, not per-layer.

                // A background quad meshes through StyledFillTileBuilder.ScheduleWrite, exactly like a
                // source fill layer — FillLayerBuild.Rent is unconditional here (the quad always produces a
                // real visit order), so this build counts into LayerMeshBuildCounters' own live/total counters
                // exactly like a source tile's, unlike the retired LayerRequest object-initializer bypass.
                builds[d] = Meshing.FillLayerBuild.Rent(input, featureColors, li, payloadName);
            }

            return Processing.TileBuildGraph.ScheduleMeasure(builds, geometry, GraphDepsForTest);
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
        /// Must be called on the Unity main thread, only when the build is complete, with positive
        /// budget (the pump guards budget &lt;= 0). A partially-consumed tile keeps its build step in flight
        /// and !Built; its already-built meshes/handles live in lt.Meshes/lt.DrawHandles, the remaining layers
        /// stay alive in <paramref name="payloads"/> (re-fetched each call). Eviction's holding pen disposes the
        /// remainder (idempotent — already-consumed layers are no-ops).
        ///
        /// <para><paramref name="payloads"/> is the caller's — job-scheduling-design.md §8 stage 3: every
        /// tile now reaches this method only via the graph's <c>CompleteWriteAndTakePayloads</c>; a faulted/
        /// cancelled PROLOGUE is handled by the caller itself, before the graph even exists (a Burst job has
        /// no fault channel, so this method never needs that early-out).</para>
        ///
        /// Mid-flight release: ReleaseTile removes the tile from _loaded, so this is never called for a
        /// released tile — the real discard protection is the _loaded-removal in ReleaseTile.
        /// </summary>
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
                int slot = cursor;
                Style.MeshDataPayload payload = payloads[slot];
                cursor++;

                int materialIndex = payload?.MaterialIndex ?? -1;
                int layerVerts    = payload?.VertexCount   ?? 0;

                if (payload == null || (uint)materialIndex >= (uint)currentLayerCount)
                {
                    payload?.Dispose();
                    // perf/gc-elimination Stage A: null the slot the instant Dispose() has run — see the
                    // note on the main-path Dispose() below for why this is load-bearing, not cosmetic.
                    payloads[slot] = null;
                    continue;
                }

                Mesh mesh;
                using (PmMeshUpload.Auto())
                    mesh = payload.Upload();
                payload.Dispose(); // consumed — free its NativeArrays now

                // perf/gc-elimination Stage A: MeshDataPayload.Dispose() returns the instance to a shared,
                // cross-build ConcurrentBag pool — so the moment Dispose() returns, `payload` may already be
                // some OTHER, concurrently-running build's live instance. payloads[slot] must stop
                // referencing it right here: DisposeWholePayloads's later unconditional sweep (at `complete`,
                // possibly many frames from now) would otherwise call Dispose() a second time on whatever
                // this slot still points to — which, if a concurrent build has since Rent()+Reset()'d it, is
                // NOT a harmless idempotent no-op (that guarantee assumed the reference was never handed to
                // anyone else) but a live double-free of that OTHER build's native array. Nulling here is
                // what makes this Dispose() call provably the payload's last touch from this tile's result.
                payloads[slot] = null;

                if (mesh == null) continue; // empty layer — no AddLayer, no budget charge

                int handle;
                using (PmAddTileLayer.Auto())
                    handle = _instanced.AddTileLayer(mesh, lt.TileOriginRender, materialIndex, id);
                _consumeMeshes.Add(mesh); // S51: track for explicit destruction
                _consumeHandles.Add(handle);
                _consumeMatIndices.Add(materialIndex); // S82: which layerId this mesh belongs to
                meshesConsumed++;
                vertsConsumed += layerVerts;
            }

            lt.ConsumeCursor = cursor;

            // Append this call's new meshes/handles/materialIndices to the tile's arrays (one realloc per
            // partial frame; load time only — steady state never re-enters consume, so no per-frame GC there).
            AppendMeshes(ref lt.Meshes, _consumeMeshes);
            AppendInts(ref lt.DrawHandles,     _consumeHandles);
            AppendInts(ref lt.MaterialIndices, _consumeMatIndices);

            bool complete = cursor >= denseCount;
            if (complete)
            {
                // Dispose the whole payload array (idempotent) — also frees any layers the active style does
                // not render (beyond fillCount/lineCount) — then mark Built and release the task.
                DisposeWholePayloads(payloads);
                FinishConsume(ref lt);
            }

            return complete;
        }

        /// <summary>S87: marks a tile fully consumed — clears the build task/graph and sets Built.
        ///
        /// <para>The single disposal site for <see cref="LoadedTile.Graph"/>: every <c>Step == Write</c>
        /// reader in this class (job-scheduling-design.md §6, <c>LoadedTile.Graph</c>'s own doc) requires
        /// <c>Graph</c> non-null for as long as <c>Step</c> reads <c>Write</c>, including across a
        /// budget-bound <see cref="ConsumeMeshBuild"/> call that returns incomplete — disposing it any
        /// earlier (its call sites used to, right after <c>CompleteWriteAndTakePayloads</c>) leaves a
        /// partially-consumed tile with <c>Step == Write</c> and <c>Graph == null</c>, and the NEXT Tick's
        /// guard dereferences it.</para></summary>
        private static void FinishConsume(ref LoadedTile lt)
        {
            lt.Graph?.Dispose();
            lt.Step          = BuildStep.None;
            lt.MeshBuildTask = default;
            lt.Graph         = null;
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

        /// <summary>Disposes every payload in a dense per-source array — the array
        /// <see cref="Processing.TileBuildGraph.CompleteWriteAndTakePayloads"/> hands back — (null-slot-safe:
        /// a null empty-layer slot, or a slot <see cref="ConsumeMeshBuild"/>'s per-payload loop already
        /// disposed AND NULLED, is a no-op). Called from every discard path for a result that was never
        /// partially consumed. Idempotent against a null/already-nulled slot, but NOT (perf/gc-elimination
        /// Stage A) against re-Disposing the SAME live reference — <c>ConsumeMeshBuild</c> nulls a slot the
        /// instant it disposes that payload precisely so this sweep never gets the chance to (see that
        /// method's comment on why a pooled payload's identity can no longer be assumed stable after its own
        /// Dispose() returns). job-scheduling-design.md §8 stage 3: a source tile's PROLOGUE output has its
        /// own array (<c>TilePrologueOutput.Layers</c>, an <c>ILayerMeshBuild[]</c>) — its disposal is
        /// <see cref="Processing.TilePrologueOutput.Dispose"/>, not this method.</summary>
        private static void DisposeWholePayloads(Style.MeshDataPayload[] payloads)
        {
            if (payloads == null) return;
            for (int li = 0; li < payloads.Length; li++)
                payloads[li]?.Dispose();
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
        /// S48 holding pen: when a mesh build handle is still in-flight at release time (only possible under
        /// <see cref="ThreadPoolWorkScheduler"/> — an <see cref="InlineWorkScheduler"/> handle is already
        /// terminal by the time it is stored), its NativeArray payload has not yet been produced; it will be
        /// allocated on the ThreadPool worker thread still running the build. We stash the handle in
        /// <see cref="_pendingDisposal"/>;
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
            // CollectLoadedTileKeys pull no longer reports it and its reconcile fades/drops the symbols.
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

        /// <summary>
        /// Tile-load smoothness: the number of <see cref="_loaded"/> records not yet
        /// <see cref="LoadedTile.Built"/> — the ACTIVE set <see cref="AdmitFromDesired"/> bounds against
        /// <see cref="TileSelectionConfig.MaxConcurrentTileLoads"/>. Recomputed fresh each call rather than
        /// tracked incrementally (H1: a stale counter field would need write-back on every mutation site of
        /// <c>_loaded</c> — a struct-copy hazard this avoids by construction). Cheap: bounded by the cover
        /// size (dozens of records), same cost class as the existing <c>_toRelease</c>/telemetry loops.
        /// </summary>
        private int CountActiveLoads()
        {
            int n = 0;
            foreach (var kv in _loaded)
                if (!kv.Value.Built) n++;
            return n;
        }

        /// <summary>
        /// Admits one DESIRED (tile, source) key: exactly today's pre-desired-list per-key body — probe the
        /// PreparedTileCache (a hit builds the tile synchronously, <see cref="LoadedTile.Built"/> true, so it
        /// never occupies an active slot), else kick a fetch and create the pending <see cref="_loaded"/>
        /// record. Shared by the per-Tick admission gate (<see cref="AdmitFromDesired"/>, capped) and the
        /// deterministic drain (<see cref="DrainMeshBuilds"/>, uncapped — admits every desired entry so a
        /// capped run still settles to the full cover, matching the "fully-settled state is unchanged"
        /// invariant).
        /// </summary>
        private void AdmitTile(TileId id, int slot, IProjection projection)
        {
            var p = _pipelines[slot];
            var key = new LoadedKey(id, slot);

            // S91-C: the SINGLE projected SW-corner render origin — shared by the mesh bake (threaded into
            // BuildGraphRequest) and the tile transform. Mercator: (mercX, 0, mercZ) == MercatorBounds().min
            // bit-for-bit, so placement is unchanged from the pre-desired-list path.
            double3 origin = TileRenderOrigin.Project(id, projection);

            // Epic A / A2 (design §B Q1, HIGH 2): a source-less (background) record only CREATES a pending
            // record here — it does NOT kick the mesh build inline. The kick moves to PumpPending so it
            // rides the SAME MaxMeshBuildsPerTick cap as a real fetch's kick, instead of one synchronous
            // cover-wide burst. No cache probe (source-less records skip PreparedTileCache transfer
            // entirely, design §B Lifecycle).
            if (p.IsSourceless)
            {
                _loaded[key] = new LoadedTile
                {
                    FetchCompleted   = true,
                    Built            = false,
                    TileOriginRender = origin,
                };
                return;
            }

            // S82: probe the PreparedTileCache BEFORE kicking a fetch — a full-tile hit (every dense
            // layerId of this (tile, source) present in the cache) skips decode/build/upload AND the fetch
            // itself; the feature source's own byte-level cache is simply never consulted on a hit.
            // Disabled (_cacheEnabled == false) skips the probe entirely — every tile is treated as a miss,
            // reverting to pre-S82 always-fetch/-prepare behaviour.
            bool allCached = false;
            if (_cacheEnabled)
            {
                ComputeDenseLayerIds(p.SourceId, _denseLayerIds);
                // Seed from dense-layer COUNT, not an unconditional true: a source with zero dense mesh
                // layers (a symbol-only source) has nothing in the prepared cache to hit, and the loop below
                // never runs to falsify it — so an unconditional `true` made `allCached` vacuously true on
                // every cover entry, sending the tile down BuildTileFromCache forever (no fetch, no kick),
                // so its symbols never built with the cache on (the default). A zero-dense source is never
                // "all cached" — it must fetch.
                allCached = _denseLayerIds.Count > 0;
                for (int d = 0; d < _denseLayerIds.Count; d++)
                {
                    if (!_prepared.Contains(new PreparedKey(CurrentStyle, id, _denseLayerIds[d])))
                    {
                        allCached = false;
                        break;
                    }
                }
            }

            if (allCached)
            {
                _prepared.Hits++;
                _loaded[key] = BuildTileFromCache(id, origin, _denseLayerIds);
                // S105/A-1: a cache HIT re-shows the tile with NO fetch (so no bytes-ready). The symbol
                // subsystem now PULLS this tile back into its loaded set and reconciles — restoring its
                // kept-warm symbols — instead of us pushing a restore callback here.
            }
            else
            {
                _prepared.Misses++;
                UniTask<SharedDisposable<IDecodedTile>> fetchReq;
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

        /// <summary>
        /// Tile-load smoothness: admits from the head of <see cref="_desired"/> — priority-sorted first —
        /// while the ACTIVE set (<see cref="CountActiveLoads"/>) stays under <paramref name="cap"/>. Runs
        /// EVERY Tick (not only on a cover recompute): admission must keep draining the desired list as
        /// slots free from completed builds even while the camera sits still, or a cap reached on a dirty
        /// Tick would stall forever once <c>_coverDirty</c> goes false. <paramref name="cap"/> ≤ 0 or
        /// <see cref="int.MaxValue"/> admits everything unconditionally (uncapped / deterministic drain) and
        /// skips the sort — order is moot when nothing is deferred.
        /// </summary>
        private void AdmitFromDesired(in TilePriorityContext priorityCtx, int cap)
        {
            if (_desired.Count == 0) return;
            if (cap <= 0) cap = int.MaxValue; // convention: 0/negative = uncapped, matching the rate caps

            // Sort BEFORE checking capacity — even while the active set is already saturated, the desired
            // list's HEAD must reflect the LATEST priority: a recompute that re-centers the view while
            // capped must promote the new-center tile to the front immediately, not only once a slot frees
            // (T4). Skipped only when uncapped (order is moot when everything admits regardless).
            if (cap < int.MaxValue) SortDesiredByPriority(priorityCtx);

            int activeCount = CountActiveLoads();
            int admitted    = 0;
            while (admitted < _desired.Count && activeCount < cap)
            {
                LoadedKey key = _desired[admitted];
                AdmitTile(key.Tile, key.Slot, priorityCtx.Projection);
                _desiredSet.Remove(key);
                // A PreparedTileCache hit settles synchronously (Built=true) and never occupies a slot —
                // recommendation §3.7: a hit does not consume an X slot (it does no fetch/build).
                if (!_loaded[key].Built) activeCount++;
                admitted++;
            }

            if (admitted > 0) _desired.RemoveRange(0, admitted);
        }

        /// <summary>
        /// Sorts <see cref="_desired"/> ascending by <see cref="TilePriority.Key"/> — stable insertion sort
        /// over <see cref="LoadedKey"/> (the Core <see cref="TilePriority.SortByPriority"/> overload operates
        /// on bare <see cref="TileId"/>; <c>LoadedKey</c> is private to this Unity-side type, so the same
        /// small algorithm is mirrored here rather than exposing it across the assembly boundary), TileId-
        /// then-Slot tiebreak on an exact key tie. Shared by <see cref="AdmitFromDesired"/> (admission order)
        /// and <see cref="PumpPending"/> (paint order) — both sort with the SAME priority so a corner tile
        /// can never win either race just because of enumeration order.
        /// </summary>
        private void SortByPriority(List<LoadedKey> list, in TilePriorityContext ctx)
        {
            int n = list.Count;
            if (_priorityKeys.Length < n)
                _priorityKeys = new double[math.max(n, _priorityKeys.Length * 2)];

            double[] keys = _priorityKeys;
            for (int i = 0; i < n; i++)
            {
                TileId tile = list[i].Tile;
                keys[i] = TilePriority.Key(in tile, in ctx);
            }

            for (int i = 1; i < n; i++)
            {
                double    k    = keys[i];
                LoadedKey item = list[i];
                int       j    = i - 1;
                while (j >= 0 && IsAfter(keys[j], list[j], k, item))
                {
                    keys[j + 1] = keys[j];
                    list[j + 1] = list[j];
                    j--;
                }

                keys[j + 1] = k;
                list[j + 1] = item;
            }
        }

        /// <summary>Sorts <see cref="_desired"/> in place by the shared priority (helper so call sites read
        /// as intent, not mechanism).</summary>
        private void SortDesiredByPriority(in TilePriorityContext ctx) => SortByPriority(_desired, in ctx);

        /// <summary>True iff (keyA, a) sorts strictly AFTER (keyB, b) — smaller priority key first,
        /// TileId (Z, X, Y) then Slot tiebreak on an exact key match (mirrors
        /// <see cref="TilePriority.SortByPriority"/>'s tiebreak, extended with the per-source Slot since a
        /// tile drawn from N sources can appear up to N times).</summary>
        private static bool IsAfter(double keyA, LoadedKey a, double keyB, LoadedKey b)
        {
            if (keyA != keyB) return keyA > keyB;
            if (a.Tile.Z != b.Tile.Z) return a.Tile.Z > b.Tile.Z;
            if (a.Tile.X != b.Tile.X) return a.Tile.X > b.Tile.X;
            if (a.Tile.Y != b.Tile.Y) return a.Tile.Y > b.Tile.Y;
            return a.Slot > b.Slot;
        }

        /// <summary>
        /// S82: builds a fully-Built <see cref="LoadedTile"/> record directly from a PreparedTileCache HIT
        /// (the Tick request-loop's cache probe, called only once every dense layerId of
        /// <paramref name="denseLayerIds"/> is confirmed <see cref="PreparedTileCache.Contains"/>). TryTakes
        /// each layer's mesh (Model B — ownership transfers back to this record) and re-registers non-null
        /// ones with the backend via <c>AddTileLayer</c>; a <see langword="null"/> (empty-layer marker) entry
        /// is a no-op, reproducing the original prepare's "no mesh for this layer" outcome exactly. No fetch,
        /// no decode, no mesh build, no upload — <see cref="TileBuildsStartedLastTick"/> is untouched by
        /// this path, which is the decisive falsifier a shallow (re-building) cache would trip.
        /// </summary>
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

            var lt = new LoadedTile { Built = true, FetchCompleted = true, TileOriginRender = origin };
            AppendMeshes(ref lt.Meshes, _consumeMeshes);
            AppendInts(ref lt.DrawHandles,     _consumeHandles);
            AppendInts(ref lt.MaterialIndices, _consumeMatIndices);
            return lt;
        }

        /// <summary>
        /// S83b: tears down a record's RENDER state — destroys its Mesh assets, unregisters its backend
        /// draw items, and stashes any in-flight fetch/mesh build in the S48/S84 holding pens (so their
        /// payloads are disposed, never consumed). Does <b>NOT</b> touch the scheduler/cache: that is the
        /// caller's choice — <see cref="ReleaseTile"/> follows with <c>Scheduler.Release</c> (evicts the
        /// cache); restyle (decision 7b) calls this ALONE for a kept source so its cached bytes survive.
        /// The caller removes the key from <see cref="_loaded"/>.
        ///
        /// <para><b>The single record-teardown funnel.</b> All four abandonment paths reach a record through
        /// here — cover change / eviction (<see cref="ReleaseTile"/>), restyle (<see cref="SetSources"/>) and
        /// teardown (<see cref="DoDispose"/>, which cancels an in-flight fetch just before calling this and
        /// spin-drains the pens afterwards). A new per-record teardown obligation belongs in this method and
        /// nowhere else.</para>
        /// </summary>
        private void RenderTeardownRecord(ref LoadedTile lt)
        {
            // FUNNEL 1: the record's own reference to its decoded tile. R1: this is now the ONLY release
            // site for that reference, for a KICKED record as much as a never-kicked one — the record keeps
            // its reference across the whole kick now (the kick takes its own separate one instead of a
            // transfer), so a tile fetched but never kicked (cover churn, eviction, restyle, teardown) and a
            // tile fetched, kicked and still in-cover both hold Allocator.Persistent buffers that only this
            // release frees. These three lines are the whole of its lifetime protocol.
            //
            // DISARM BEFORE FIRING. The field is nulled BEFORE Release() runs, not after: Release() throws
            // on an unbalanced release (that diagnostic is deliberate), and IDecodedTile.Dispose() can throw
            // too. With the null-out afterwards, either throw leaves the field still armed with a handle
            // whose reference is already gone, and the next teardown of the same record releases it a
            // second time — turning one loud fault into a corrupted count. Same "a transfer nulls the
            // source" rule as the mesh lifetime, applied to the effect ORDER and not just its presence.
            SharedDisposable<IDecodedTile> decode = lt.Decode;
            lt.Decode = null;
            decode?.Release();

            // S84: if the FETCH is still in-flight, stash its preserved UniTask so it is observed when it
            // completes. Otherwise the dropped task faults unobserved → UnityWebRequestException console flood.
            if (!lt.FetchCompleted)
            {
                ReleasedMidFetchCount++;
                _pendingFetchDisposal.Add(lt.Request);
            }

            // A record with an unconsumed PROLOGUE build must have its result's NativeArrays disposed —
            // stash in the S48 holding pen. DrainPendingDisposal/DoDispose call TilePrologueOutput.Dispose()
            // on the completed result, which sweeps every ILayerMeshBuild's own columns — the prologue's
            // equivalent of DisposeWholePayloads, over its own ILayerMeshBuild[] rather than an
            // MeshDataPayload[]. Genuine mid-flight (task still running at release) is counted; a task
            // the worker finished but the pump had not yet observed (IsCompleted already true when release
            // fires) is stashed but not counted, matching the pre-stage-3 seam pen's own treatment.
            if (lt.Step == BuildStep.Prologue && !lt.Built)
            {
                if (!lt.MeshBuildTask.IsCompleted)
                    ReleasedMidFlightCount++;
                _pendingDisposal.Add(lt.MeshBuildTask);
                // Mirrors the graph branch's "transfer nulls the source" discipline below — both callers
                // Remove/Clear this record from _loaded immediately after, so nothing reads it again today,
                // but nulling here costs nothing and keeps the two pen stashes uniform.
                lt.MeshBuildTask = default;
            }

            // job-scheduling-design.md §6 exit (2)/(3): the graph-arm twin of the seam pen above — a record
            // released while its measure/write step is genuinely in flight, complete but not yet consumed
            // at all, OR partially consumed (Step still Write, some payloads already taken via
            // CompleteWriteAndTakePayloads while others remain — FinishConsume, which alone would advance
            // Step back to None, never ran). All three land here, not just the first two: lt.Graph stays
            // live until FinishConsume disposes it, so a partial tile's still-live Graph is exactly as
            // pen-worthy as one still mid-job. The transfer nulls the source (the double-free guard);
            // DrainPendingDisposal's graph sweep completes then disposes it.
            //
            // ReleasedMidFlightCount is guarded by !lt.Graph.IsStepComplete, so a partial tile — whose write
            // handle IS complete by definition (CompleteWriteAndTakePayloads already ran) — does NOT count
            // here, matching the seam pen's own treatment of a partial (see its ReleasedMidFlightCount
            // guard above, keyed the same way on task completion rather than on Built).
            if ((lt.Step == BuildStep.Measure || lt.Step == BuildStep.Write) && !lt.Built)
            {
                if (!lt.Graph.IsStepComplete)
                    ReleasedMidFlightCount++;
                // TileBuildGraph.Dispose() (run by DrainPendingDisposal's graph sweep once IsStepComplete)
                // owns the remaining (not-yet-consumed) payload slots itself now — CompleteWriteAndTakePayloads
                // caches the array it returns, so there is no separate LoadedTile-side copy to sweep here.
                _pendingGraphDisposal.Add(lt.Graph);
                lt.Graph = null;
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
        /// S48: Drains completed PROLOGUE tasks from the mid-flight-discard holding pen, disposing their
        /// <see cref="Processing.TilePrologueOutput"/>'s NativeArrays. Called once per Tick and in Dispose.
        ///
        /// Tasks not yet complete remain in the list for the next drain. This is a non-blocking
        /// poll — no spinning, no blocking.
        /// </summary>
        private void DrainPendingDisposal()
        {
            if (_pendingDisposal.Count > 0)
            {
                // Iterate backwards so we can remove in-place without index shifting.
                for (int i = _pendingDisposal.Count - 1; i >= 0; i--)
                {
                    var handle = _pendingDisposal[i];
                    if (!handle.IsCompleted)
                        continue; // still in-flight; check again next Tick

                    // Task completed (succeeded, faulted, or cancelled).
                    if (handle.IsSucceeded)
                        handle.GetResult().Dispose();
                    // Faulted/cancelled: no output produced, nothing to dispose.

                    _pendingDisposal.RemoveAt(i);
                }
            }

            // job-scheduling-design.md §6: the graph arm's twin sweep — a Burst job cannot fault, so
            // Handle.IsCompleted is the only gate; Dispose() completes (a no-op by the time this fires) and
            // frees every owned container.
            for (int i = _pendingGraphDisposal.Count - 1; i >= 0; i--)
            {
                var graph = _pendingGraphDisposal[i];
                if (!graph.IsStepComplete)
                    continue; // still in-flight; check again next Tick

                graph.Dispose();
                _pendingGraphDisposal.RemoveAt(i);
            }
        }

        /// <summary>
        /// S84: Observes a COMPLETED fetch task's terminal outcome exactly once and hands the
        /// decode-provisioning handle to the caller, who <b>now owns it</b> (null on cancel/fault, or on an
        /// absent tile — Epic A / A7: <see cref="ITileFeatureSource.GetTile"/> already mapped
        /// <c>!HasData</c> to null). One of exactly TWO ways a fetch <see cref="UniTask{T}"/> result may be
        /// consumed — the other is <see cref="DiscardFetchOutcome"/> — so a faulted/cancelled fetch is never
        /// left for UniTask's unobserved-exception finalizer (the console-flood bug). This is the
        /// <b>still-wanted</b> arm: a genuine error (5xx / connection) is logged, bounded, by
        /// <see cref="LogFetchErrorThrottled"/>. A tile released mid-fetch cancels its token, surfacing as
        /// <see cref="System.OperationCanceledException"/> (mapped from the aborted request by
        /// <c>UnityWebRequestDataSource</c>) — benign, swallowed silently. Precondition:
        /// <c>req.Status.IsCompleted()</c>; safe because <c>lt.Request</c> is <c>.Preserve()</c>d.
        /// </summary>
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
                // BEFORE the general arm, deliberately. Under the eager decode a malformed tile faults the
                // same task a 5xx does, and routing both into one counter would let a genuinely broken tile
                // hide behind 64 unrelated network errors — and would symbol it "tile fetch failed", which is
                // a lie. The fetch succeeded; the bytes are bad. Its own counter, its own message.
                LogDecodeErrorThrottled(ex);
                return null;
            }
            catch (System.Exception ex)
            {
                LogFetchErrorThrottled(ex);
                return null;
            }
        }

        /// <summary>
        /// S84: Observes a COMPLETED fetch task's terminal outcome exactly once and <b>throws the result
        /// away</b> — the abandonment arm, for a tile nobody wants any more (released mid-fetch, or torn
        /// down). Every outcome is swallowed silently: this path is reached only after the caller already
        /// decided the tile is unwanted, so a 5xx here is noise, not news (exactly the pre-split
        /// <c>logErrors: false</c> behaviour).
        ///
        /// <para><b>The <see langword="void"/> return type is load-bearing — for the CALLER.</b> A discard
        /// site cannot bind the handle, so it cannot retain, re-consume or leak what the fetch produced:
        /// caller-side ownership is compiler-enforced, not conventional. Keep it <see langword="void"/>:
        /// the moment it hands something back it stops being a funnel and becomes a fourth thing to
        /// remember. What it does NOT enforce, so that the guarantee is not read wider than it is: nothing
        /// stops THIS method's own body from retaining or leaking what it observed — that half is a
        /// per-record obligation, and it belongs to the runtime lifetime teeth, not to the signature.</para>
        ///
        /// Precondition: <c>req.Status.IsCompleted()</c>; safe because the stashed task is <c>.Preserve()</c>d.
        /// </summary>
        private void DiscardFetchOutcome(UniTask<SharedDisposable<IDecodedTile>> req)
        {
            try
            {
                // FUNNEL 2: a fetch that succeeded for a tile nobody wants any more still produced a
                // DECODED tile — the eager decode ran the moment the bytes landed, so observing the outcome
                // is no longer enough. This release is what frees its Allocator.Persistent buffers.
                req.GetAwaiter().GetResult()?.Release();
            }
            catch (System.Exception)
            {
                // Cancelled (released mid-fetch) or faulted (5xx / connection / a malformed tile's
                // TileDecodeException) — all expected on an abandoned tile, and all already observed by the
                // GetResult above, which is the whole point of the call. A faulted task produced no lease,
                // so there is nothing to release on this arm. Nothing to log, nothing to hand back.
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
        /// The DECODE sibling of <see cref="LogFetchErrorThrottled"/>, with the same bound and its OWN
        /// counter. Two counters rather than one because the two faults have different causes and different
        /// remedies: a fetch error is the network's, a decode fault is the tile's. Sharing a counter would
        /// let a broken tile fall inside another failure's throttle window and never be seen at all.
        /// </summary>
        private void LogDecodeErrorThrottled(System.Exception ex)
        {
            _decodeErrorCount++;
            if (_decodeErrorCount == 1 || (_decodeErrorCount & 63) == 0)
                Debug.LogWarning($"[TileManager] tile decode failed ({_decodeErrorCount} total): {ex.Message}");
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

                DiscardFetchOutcome(_pendingFetchDisposal[i]); // released → swallow silently
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
            // teardown-cancel constraint 2: cancel FIRST, before either pen drain below waits on anything.
            // A cancelled build still settles (RunWorkerPass's unconditional settle loop), so the drains
            // below complete in milliseconds instead of blocking WaitOffPlayerLoop(10000) per stashed task.
            // Ordering among the two "cancel-first" acts (this, and the fetch-release loop right below) is
            // immaterial — cancellation is a cheap flag flip — but this MUST precede both drains.
            _lifetimeCts.Cancel();

            // Teardown routes EVERY record through the SINGLE record-teardown funnel,
            // RenderTeardownRecord — the same one cover-change, eviction (ReleaseTile) and restyle
            // (SetSources) already use. It unregisters the record's instanced draw items, destroys
            // its tracked Mesh assets (S51 leak guard: a Mesh asset is NOT freed just because nothing
            // references it) and stashes any in-flight fetch / mesh build in the S48/S84 holding pens; the
            // spin-drains below then take those pens to completion. This method used to hand-roll its own
            // three loops instead, which made teardown a FOURTH record-teardown path — one that any future
            // per-record obligation added to the funnel would silently miss.
            //
            // CANCEL THE FETCH FIRST, before the record is stashed: otherwise the spin further down would
            // wait on a request that only the (later) source Dispose would cancel. Release does not touch
            // _loaded, so iterating it here is safe. The `?.` is defensive only — a source-less record
            // always has FetchCompleted == true, so this branch cannot reach a null FeatureSource today.
            //
            // Order: destroy meshes (here, inside the funnel) → dispose the backend (below), so the backend
            // never references a freed Mesh.
            foreach (var kv in _loaded)
            {
                var lt = kv.Value; // foreach value is read-only; teardown needs a ref to null its Meshes
                if (!lt.FetchCompleted)
                    _pipelines[kv.Key.Slot].FeatureSource?.Release(kv.Key.Tile);
                RenderTeardownRecord(ref lt);
            }

            _loaded.Clear();

            // S48: drain the mid-flight-discard holding pen — park to completion, then dispose the
            // TilePrologueOutput's NativeArrays; we're tearing down and must not leak. Runs AFTER the
            // teardown loop above, so it also absorbs every mesh build that loop just stashed.
            // The handle's WorkScheduler completes it on the completing thread, never posting to the
            // PlayerLoop (I-2) — WaitOffPlayerLoop parks on a kernel event set by that completion,
            // deadlock-free (no PlayerLoop dependency).
            for (int i = 0; i < _pendingDisposal.Count; i++)
            {
                var handle = _pendingDisposal[i];
                UniTask<Processing.TilePrologueOutput> buildTask = handle.ToUniTask();
                buildTask.WaitOffPlayerLoop(10000);

                if (handle.IsSucceeded)
                    handle.GetResult().Dispose();
            }

            _pendingDisposal.Clear();

            // job-scheduling-design.md §6 exit (4): the graph arm's twin sweep — Complete() from the main
            // thread executes a not-yet-started job inline (§3.3), so no WaitOffPlayerLoop/timeout is needed
            // here the way the seam pen above needs one for its UniTask bridge.
            foreach (var graph in _pendingGraphDisposal)
                graph.Dispose();
            _pendingGraphDisposal.Clear();

            // S84: the FETCH pen, drained the same way — park, then observe-and-discard through the single
            // abandonment funnel, so no fetch task is dropped unobserved (UnityWebRequestException flood on
            // the abort). Also absorbs what the teardown loop above stashed, whose requests it already
            // cancelled.
            for (int i = 0; i < _pendingFetchDisposal.Count; i++)
            {
                var task = _pendingFetchDisposal[i];
                task.WaitOffPlayerLoop(10000);
                DiscardFetchOutcome(task);
            }

            _pendingFetchDisposal.Clear();

            // S82: destroy every Mesh the PreparedTileCache still holds (out-of-cover tiles handed off by
            // ReleaseTile) — SAME "destroy meshes → dispose backend" ordering as the teardown loop at the
            // top, so the backend never references a freed Mesh either way.
            _prepared.Dispose();

            // Dispose the instanced backend AFTER destroying all tile meshes (it references mesh IDs that
            // become invalid when the Mesh assets are destroyed; this order keeps it from drawing freed
            // meshes). S49 tooth 5: BRG + GraphicsBuffer released; S53b: Entities World disposed.
            _instanced?.Dispose();
            _instanced = null;

            // S83b: dispose every source pipeline (each scheduler + its owned source).
            DisposePipelines();

            // teardown-cancel: dispose the CTS LAST. Both pen drains above have already completed (every
            // worker returned) by this point, so no worker is still parked on token.WaitHandle — only the
            // Tooth-B test gate blocks on the handle at all; the production worker only polls
            // IsCancellationRequested. Disposing here is safe either way, but doing it after both drains
            // avoids any risk of a parked wait observing an ObjectDisposedException.
            _lifetimeCts.Dispose();
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