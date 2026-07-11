# Tile-pipeline design — decomposition + stall elimination

**Status:** design, 2026-07-11 (branch `perf/label-instrumentation`). Companion to
[`tile-pipeline-architectural-review.md`](tile-pipeline-architectural-review.md) — that review's findings
(verified file:line) are taken as established here; this document turns them into buildable seams,
signatures, test teeth, and an ordered migration plan. All paths relative to
`Assets/Code/MapRenderer.Unity/` unless noted.

Hard constraints honoured throughout (see `docs/async-architecture.md`): Core stays engine-free; UniTask
only; `UnityEngine.Object` create/destroy and `Mesh.AllocateWritableMeshData`/`ApplyAndDisposeWritableMeshData`
main-thread only; single-owner Mesh with null-on-transfer; steady-state zero-GC
(`MapView_SteadyStateTick_DoesNotAllocateGCMemory`); load-path allocations flagged where introduced.

---

## 0. Decision summary

| # | Topic | Decision |
|---|---|---|
| D1 | Decomposition order | `SourcePipelineRegistry` → `PreparedTileBridge` → `TileMeshBuildEngine`; test surface exits to the test assembly in the engine step |
| D2 | Dense layer-id cache | Lives on `SourcePipeline` (computed once in `ApplySpecs`), **not** in the bridge — the kick, the probe, and the transfer all read the same `int[]`, so the three-consumer agreement invariant becomes structural |
| D3 | Symbol budgeting | Queue in `SymbolLabelSubsystem` + explicit `PumpBuilds()` called from `MapView.LateUpdate`; ≤1 build start/frame (configurable); atlas upload decision moves out of `BuildTileAsync` into the pump (≤1 upload/frame by construction) |
| D4 | Symbol threading | Stage 1: decode+extract on the pool. Stage 2: shape+layout on the pool against an immutable per-tile snapshot (`IGlyphMetricsProvider` + `IGlyphAtlasView`) built on main **after** pass-1 appends complete. Atlas/cache **writes never leave the main thread**; no lock |
| D5 | Two-phase kick | Measure task (worker, geometry into interim `TileMeshBuffers`) → main-thread exact-count `AllocateWritableMeshData(1)` per **non-empty chunk** → write task (worker). One `MeshDataArray` per chunk is kept (a whole-array alloc conflicts with per-mesh resumable consume — review #5's catch) |
| D6 | Mesher chunking | New `ILayerGeometry` seam (measure/write-chunk) replaces `IRenderLayer.WriteInto`; split at feature boundaries, target 32 768 verts/chunk; a single over-budget feature ships as one oversized chunk (no re-triangulation) |
| D7 | PreparedTileCache value | `Mesh` → `Mesh[]` per (style, tile, layerId) so a chunked layer round-trips the cache without the Put-collision destroying earlier chunks; `null` empty-layer marker unchanged |
| D8 | Release budgeting | Deferred-release queue drained ≤`MaxReleasesPerTick` records/frame; re-validated against the live cover at dequeue (a tile that came back is un-queued, not destroyed) |
| D9 | Batched removal | New `ITileRenderBackend.RemoveItems(ReadOnlySpan<int>)`; Entities implements it as **one** `EntityManager.DestroyEntity(NativeArray<Entity>)` per call (layers + emptied roots together) |
| D10 | Entities vs BRG | **Fix Entities, keep it the ship default.** Prototype-entity + `Instantiate` + `RegisterMesh`/`RegisterMaterial` + `MaterialMeshInfo.FromMeshIDAndMaterialID`, with unregister-on-remove. BRG stays the zero-alloc opt-in. (RESOLVED — maintainer accepted, §8) |
| D11 | Cover gate | Recompute gated on `_coverDirty` alone; release-queue drain moves above the gate so it runs every frame |
| D12 | Budget-zero asymmetry | Unify: `MaxConsumesPerTick == 0` becomes **uncapped** like the other caps; tests get an explicit `internal bool BlockConsumeForTests` seam on the engine |

---

## 1. Decomposition

### 1.1 Shared moves (prerequisite, mechanical)

`LoadedKey` and `LoadedTile` leave `TileManager`'s private nesting and become namespace-level `internal`
structs in `Rendering/Tile/LoadedTile.cs` — the engine takes `ref LoadedTile` and cannot see a private
nested type. No field changes in this step. `SourceKey`/`SourceSpec` similarly become namespace-level
`internal` types (they are already `internal`; `MapView.BuildSourceSpecs` changes
`Tile.TileManager.SourceSpec` → `Tile.SourceSpec`).

### 1.2 `SourcePipelineRegistry` — `Rendering/Tile/SourcePipelineRegistry.cs`

Moves out of `TileManager`: `SourcePipeline` (class), `SourceKey`, `SourceSpec`, the diff/keep/teardown
body of `SetSources` step 2 (L480–518), `FindPipeline`, `DisposePipelines`, `SourceIdOf`, the
`InFlightCount` aggregate. **Plus (D2):** the dense layer-id computation (`ComputeDenseLayerIds` +
`_denseLayerIdsScratch`) is retired in favour of a per-source cached array:

```csharp
namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>One data pipeline per rendered source-id (S83b), plus the source's cached dense
    /// layer-id set — the single array the kick, the cache probe, and the release transfer all read,
    /// so the three can never disagree on what "this tile's complete prepared set" means.</summary>
    internal sealed class SourcePipeline
    {
        public string        SourceId;
        public int           Slot;
        public IDataSource   Source;
        public bool          OwnsSource;
        public TileScheduler Scheduler;
        public TileCache     Cache;
        public int           MinZoom;
        public int           MaxZoom;
        public SourceKey     DefKey;
        /// <summary>Declared-order global material indices whose style layer draws from this source.
        /// Computed once per ApplySpecs (the only time RenderLayerSet reindexes); immutable between
        /// restyles — safe to capture into a background closure WITHOUT a defensive copy.</summary>
        public int[]         DenseLayerIds;
    }

    internal sealed class SourcePipelineRegistry : System.IDisposable
    {
        public int Count { get; }
        public SourcePipeline this[int slot] { get; }

        /// <summary>Diffs specs against existing pipelines by (SourceId, SourceKey): kept pipelines
        /// survive with warm caches, new specs build fresh ones, unmatched pipelines are torn down.
        /// Recomputes every kept+new pipeline's DenseLayerIds from <paramref name="layers"/> (the
        /// caller rebuilt the RenderLayerSet before calling — MapView.SetStyle ordering).</summary>
        public void ApplySpecs(IReadOnlyList<SourceSpec> specs, Style.RenderLayerSet layers);

        /// <summary>Summed in-flight fetches across pipelines (telemetry).</summary>
        public int InFlightCount { get; }

        /// <summary>Disposes every pipeline (scheduler always; source only when owned). Idempotent.</summary>
        public void Dispose();
    }
}
```

Removing the per-probe/per-release O(layerCount) scan (review §4) also removes the shared-scratch-list
"never capture across a thread boundary" trap and `KickMeshBuild`'s per-kick `int[]` copy — the cached
array is immutable between restyles, so the closure captures it directly. **Zero-GC note:** `ApplySpecs`
allocates (restyle path, allowed); the steady-state Tick now does zero list work for the probe.

`TileManager.SetSources` shrinks to: teardown `_loaded` records → `_bridge.Clear()` →
`_registry.ApplySpecs(specs, _layers)` → `BuildBackend(backend)` → re-arm cover.

### 1.3 `PreparedTileBridge` — `Rendering/Tile/PreparedTileBridge.cs`

Moves: ownership of `PreparedTileCache` + `_cacheEnabled` + `CurrentStyle`, the Tick probe block
(L853–894's cache branch), `TransferBuiltMeshesToCache`, `BuildTileFromCache`.

```csharp
internal sealed class PreparedTileBridge : System.IDisposable
{
    public PreparedTileBridge(Map.PreparedTileCacheConfig config);

    public bool       Enabled      { get; }
    public StyleToken CurrentStyle { get; set; }   // MapView sets in SetStyle (via TileManager forward)

    // Telemetry forwards (CaptureTelemetry reads these): Hits, Misses, Count, MaxCount,
    // BytesHeld, ByteBudget, Evictions.

    /// <summary>Tick probe: true iff EVERY dense layerId is present (mesh or empty-marker).
    /// Counts one whole-tile Hit or Miss. Disabled ⇒ always false (counts a Miss) — pre-S82 parity.</summary>
    public bool ProbeFullHit(TileId id, int[] denseLayerIds);

    /// <summary>Cache-hit path: TryTake each layer's meshes (ownership back out — Model B), register
    /// non-null ones via <paramref name="backend"/>.AddTileLayer, return the fully-Built record.
    /// Main-thread only.</summary>
    public LoadedTile TakeAsLoadedTile(Backend.ITileRenderBackend backend, TileId id,
        double3 origin, int[] denseLayerIds);

    /// <summary>Release path: transfer a Built record's tracked meshes in (nulls lt.Meshes /
    /// lt.MaterialIndices — the double-free guard), inserting empty-markers for uncovered dense ids.
    /// No-op (returns false) when disabled or the record has no tracked meshes.</summary>
    public bool TransferOnRelease(TileId id, int[] denseLayerIds, ref LoadedTile lt);

    public void Clear();     // restyle purge (destroys held meshes; cache stays usable)
    public void Dispose();   // teardown (destroys held meshes)
}
```

The dead "structural parity" lock inside `PreparedTileCache` (review §4) is deleted in this step — the
class is documented main-thread-only and the bridge preserves that; the lock removal is
behaviour-preserving and covered by the existing cache tests.

### 1.4 `TileMeshBuildEngine` — `Rendering/Tile/TileMeshBuildEngine.cs`

Moves: `MeshBuildResult`, `KickMeshBuild`, `ConsumeMeshBuild`, `FinishConsume`, `AppendMeshes`,
`AppendInts`, `DisposeWholeResult`, `_consumeScratch*`, `_pendingDisposal` + `DrainPendingDisposal` + the
blocking drain from `DoDispose` (L1691–1718). This is where the two-phase kick (§4) lands later — the
signatures below already show the final two-phase form; the extraction step ships them with `KickMeasure`
absent and today's single-phase `Kick` body (extraction is behaviour-preserving; §4 then changes only this
class + the meshers).

```csharp
internal sealed class TileMeshBuildEngine : System.IDisposable
{
    public TileMeshBuildEngine(Style.RenderLayerSet layers);

    /// <summary>Test seam (D12): true blocks Consume entirely — replaces the old
    /// "MaxConsumesPerTick == 0 blocks" trick, so 0 can mean uncapped like every other budget.</summary>
    internal bool BlockConsumeForTests { get; set; }

    // ── Extraction step ships this (today's KickMeshBuild body, denseLayerIds from the registry) ──
    public UniTask<MeshBuildResult> Kick(TileId id, byte[] mvtBytes, int[] denseLayerIds,
        double3 tileOriginRender, IProjection projection);

    // ── §4 (two-phase) replaces Kick with the pair: ──
    public UniTask<MeasuredGeometry> KickMeasure(TileId id, byte[] mvtBytes, int[] denseLayerIds,
        double3 tileOriginRender, IProjection projection);
    /// <summary>MAIN THREAD: allocates one exact-size writable MeshDataArray per non-empty chunk
    /// (PmMeshDataAllocate), then schedules the worker write. Takes ownership of measured.</summary>
    public UniTask<MeshBuildResult> KickWrite(MeasuredGeometry measured);

    /// <summary>Resumable per-mesh consume under the dual budget. Registers each uploaded mesh with
    /// <paramref name="backend"/> (passed per call — the backend is rebuilt on restyle, so the engine
    /// never holds a potentially-stale reference). Main-thread only.</summary>
    public bool Consume(Backend.ITileRenderBackend backend, TileId id, ref LoadedTile lt,
        int meshBudget, int vertBudget, out int meshesConsumed, out int vertsConsumed);

    /// <summary>Mid-flight-release holding pens (mesh-build task / measured-geometry task).</summary>
    public void StashDiscard(UniTask<MeshBuildResult> task);
    public void StashDiscardMeasure(UniTask<MeasuredGeometry> task);
    /// <summary>Non-blocking per-Tick poll of both pens.</summary>
    public void DrainDiscards();
    /// <summary>Teardown: spin every stashed task to completion (bounded, same ~10 s cap as today)
    /// and dispose payloads. Dispose() == DrainAllBlocking().</summary>
    public void DrainAllBlocking();
    public void Dispose();
}
```

`DoDispose` in `TileManager` changes its in-flight-build handling to: for each `_loaded` record with
`HasMeshBuild`/`HasMeasure` → `_engine.StashDiscard*(...)`; then `_engine.DrainAllBlocking()`. Same
spin-cap semantics, one owner for the spin code.

### 1.5 Slimmed `TileManager` — what stays, ownership, teardown order

Stays (~450 code lines): `_loaded` (`Dictionary<LoadedKey, LoadedTile>`), `Tick` (cover key/dirty
tracking, request/release transition), `PumpPending` (state machine driver calling
`_engine.Kick*/Consume`), `ReleaseTile`, `RenderTeardownRecord`, `_pendingFetchDisposal` + drain,
`ObserveFetchOutcome`/`LogFetchErrorThrottled`, `SymbolTileBytesReady`, `BuildBackend` + backend
accessors + `InstancedRebuild`, `CaptureTelemetry` (production caller: debug readout), `DoDispose`
orchestration.

Construction (TileManager ctor): `_registry = new SourcePipelineRegistry()`,
`_engine = new TileMeshBuildEngine(layers)`, `_bridge = new PreparedTileBridge(cacheConfig)`.
All three are `internal readonly` fields (test extensions reach them via `InternalsVisibleTo`).

`DoDispose` order (preserves today's "destroy meshes → dispose backend" contract exactly):

1. Stash every `_loaded` in-flight build into the engine pens; `_engine.DrainAllBlocking()` (frees
   NativeArrays / MeshDataArrays).
2. Cancel + spin + observe in-flight fetches; drain `_pendingFetchDisposal` (unchanged, stays here).
3. `DestroyTrackedMeshes` over `_loaded`; clear.
4. `_bridge.Dispose()` (destroys cached meshes — still **before** the backend).
5. `_instanced?.Dispose()`.
6. `_registry.Dispose()` (schedulers + owned sources — after the backend, as today).

### 1.6 Test-surface extraction

Per the repo's test-bloat rule: `TryGetBuiltTile`, `GetTileMeshes`, `AllTilesSettled`,
`ComputeSceneBounds`, `DrainMeshBuilds` move to `TileManagerTestExtensions` in
`MapRenderer.Tests.EditMode` (extension methods over the now-`internal` `_loaded`/`_registry`/`_engine`/
`_instanced`; `InternalsVisibleTo` already exists). `DrainMeshBuilds` re-expresses itself as: spin fetch →
`_engine.Kick` inline → spin task → `_engine.Consume(backend, …, int.MaxValue, int.MaxValue, …)`. The
`*LastTick` counters **stay** (they are read by `CaptureTelemetry`/the debug panel — production callers);
`ReleasedMidFlightCount`/`ReleasedMidFetchCount` stay for the same reason. Comment archaeology
(the triple-told S55-vs-S87 story etc.) is pruned during the moves — keep the *why*, drop the diffs
git history holds.

**Migration steps** (each independently shippable, gate = `./Tools/run-tests.sh` green):

1. **M1** — `LoadedTile.cs`/`SourceSpec` de-nesting (mechanical; MapView reference updates).
2. **M2** — `SourcePipelineRegistry` + `DenseLayerIds` on `SourcePipeline`; `TileManager` consumers
   switch to `_registry[slot].DenseLayerIds`; delete `ComputeDenseLayerIds` + scratch.
   *Tooth:* existing multi-source + restyle suites; plus a new assertion that a restyle recomputes
   the dense set (change a layer's source between styles ⇒ probe/kick both see the new set — a stale
   cache serves the OLD set and fails the prepared-cache completeness tests).
3. **M3** — `PreparedTileBridge` (+ cache lock removal). *Tooth:* existing S82 cache suite
   (`MeshBuildsKickedLastTick` untouched on a hit) passes unchanged; `CountMeshObjects` leak baseline
   unchanged across load→release→revisit.
4. **M4** — `TileMeshBuildEngine` + test-surface extraction + D12 budget-zero unification
   (`MaxConsumesPerTick == 0` → uncapped; tests that built backlogs with 0 switch to
   `BlockConsumeForTests`). *Tooth:* full EditMode suite; the S87 resumable-consume tests and the S48/S51
   leak-guard tests exercise every moved path; a dedicated test flips `BlockConsumeForTests` and asserts
   a backlog builds (the seam the old `0` semantics served).

---

## 2. Symbol-path budgeting + off-thread staging (stall #1)

### 2.1 Stage A — scheduling only (queue + build cap + upload coalescing + cancellation)

All inside `SymbolLabelSubsystem`; `TileManager`/`MapView` wiring gains one call.

```csharp
internal sealed class SymbolLabelSubsystem : IDisposable
{
    private readonly struct PendingSymbolBuild
    {
        public string    SourceId  { get; init; }
        public TileId    Tile      { get; init; }
        public byte[]    Bytes     { get; init; }
        public List<int> LayerIndices { get; init; }   // the _layersBySource list (not copied)
    }

    private readonly Queue<PendingSymbolBuild> _buildQueue = new(32);
    private readonly HashSet<SymbolTileLabelStore.Key> _loadedNow = new(); // refreshed by ReconcileLoadedTiles
    private CancellationTokenSource _buildCts;   // recreated in SetStyle; cancelled in SetStyle/Dispose

    /// <summary>Max symbol-tile builds STARTED per frame (≤). Serialized via MapViewConfig; default 1.</summary>
    public int MaxBuildsPerFrame { get; set; } = 1;

    // OnTileBytesReady: was BuildTileAsync(...).Forget() — now ONLY enqueues (bytes are GC-owned,
    // retention is bounded by the cover size; a released tile's entry is dropped at dequeue).
    public void OnTileBytesReady(string sourceId, TileId tile, byte[] bytes);

    /// <summary>MAIN THREAD, once per frame from MapView.LateUpdate (after TileManager.Tick, before
    /// ReconcileLoadedTiles): starts ≤MaxBuildsPerFrame queued builds, skipping tiles that left the
    /// loaded set; then performs AT MOST ONE atlas GPU upload if the glyph count grew since the last
    /// upload (coalesces every commit/glyph-fetch that landed since last frame). Bounded: the dequeue
    /// loop runs at most Queue.Count iterations (skips are dequeues).</summary>
    public void PumpBuilds();

    // Per-frame observability (pattern: TileManager's *LastTick counters)
    internal int BuildsStartedLastPump  { get; private set; }
    internal int AtlasUploadsLastPump   { get; private set; }
    internal int QueuedBuildCount       => _buildQueue.Count;
    internal int CancelledBuildCount    { get; private set; }
}
```

`BuildTileAsync` changes:
- Signature gains the token: `private async UniTaskVoid BuildTileAsync(string sourceId, TileId tile,
  byte[] bytes, List<int> layerIndices, CancellationToken ct)`; the token is threaded into
  `_builder.BuildAsync(..., ct)` (parameter already exists and flows into
  `GlyphManager.EnsureFontStackRangeAsync` → `IGlyphSource.FetchAsync`) and checked
  (`ct.ThrowIfCancellationRequested()`) after every await before touching `_glyphManager`/
  `_atlasTexture`/`_store`. `catch (OperationCanceledException) { CancelledBuildCount++; return; }`
  ahead of the generic catch — a restyle/teardown mid-build is silent, never a warning, and never
  touches disposed state. This closes both latent risks from review §4 (restyle-vs-in-flight-build,
  missing token).
- The atlas-upload block (`glyphCount > _lastUploadedGlyphCount` → `Upload`) **moves out** of
  `BuildTileAsync` into `PumpBuilds` — commits and late glyph-range arrivals mark nothing; the pump's
  single check per frame coalesces N tiles' worth of new glyphs into one ≤16 MB `LoadRawTextureData`
  +`Apply`.

`SetStyle`: `_buildCts?.Cancel(); _buildCts?.Dispose(); _buildCts = new CancellationTokenSource();`
before `DisposePipeline()`, and `_buildQueue.Clear()`. `Dispose`: cancel + dispose the CTS.

`MapView.LateUpdate` (inside the `_symbols.HasSymbolLayers` branch, before the reconcile):
`_symbols.PumpBuilds();`.

**Cost/latency:** labels for a burst of N fetched tiles now complete over N frames instead of one
frame — off-screen-invisible (labels already arrive seconds after tiles due to network). Queue holds
`byte[]` references a few frames longer (bounded by cover size × tile bytes; same order as
`ReadyBytes` retention that already exists).

**Teeth (Stage A):**
- *Budget:* push 5 bytes-ready callbacks in one frame (fake source) ⇒ `BuildsStartedLastPump == 1`,
  `QueuedBuildCount == 4`; after 5 pumps all built (store `ActiveTileCount == 5`). A shallow
  implementation that still builds inline fails the first assertion.
- *Coalescing:* a build whose glyph fetches complete across 3 frames ⇒ `AtlasUploadsLastPump ≤ 1`
  every frame, and total uploads over the build ≤ frames-with-new-glyphs (vs today's per-commit upload).
- *Cancellation:* gate a fake `IGlyphSource` on a `UniTaskCompletionSource`; call `SetStyle` mid-await;
  release the gate ⇒ `CancelledBuildCount == 1`, no `LogWarning`, store empty, no
  `ObjectDisposedException`. Today's code fails this (the catch logs a warning after touching
  disposed state).
- *Stale-drop:* enqueue, then reconcile the tile out of the loaded set, then pump ⇒
  `BuildsStartedLastPump == 0`, queue empty.

### 2.2 Stage B — decode + extract off the main thread

`BuildTileAsync` restructures around explicit switch points (UniTask only):

```csharp
await UniTask.SwitchToThreadPool();
MvtTile mvt = MvtDecoder.Decode(bytes);                          // Core, engine-free
var extractedPerLayer = ExtractAllLayers(mvt, tile, layers, zoom, projection); // SymbolFeatureExtractor
await UniTask.SwitchToMainThread(ct);                            // honours cancellation on resume
// pass 1 (glyph ensure) + everything after stays on main — unchanged in this stage
```

`SymbolFeatureExtractor.Extract`, the parsed `Symbol.StyleLayer` list (immutable after parse), and
`IProjection` (stateless math) are worker-safe; neither touches the glyph cache, the atlas, nor any
`UnityEngine.Object`. `StyledSymbolTileBuilder.BuildAsync` splits its per-layer extract out into a
worker-safe pre-pass (`Extract` results passed in) so the builder no longer decodes-then-extracts on
main. The doubled `MvtDecoder.Decode` (mesh worker decodes the same bytes) remains — review option (d)
rejected: sharing the decoded `MvtTile` couples symbol timing to mesh-kick timing for no main-thread win
once decode is off-main.

**Main-thread boundary after Stage B** (the append-on-main discipline, explicit):

| Work | Thread |
|---|---|
| MVT decode, symbol feature extract | pool |
| Glyph fetch await/resume, PBF decode, `GlyphCache.Store`, `GlyphAtlas.Append` (SDF blit) | **main** (unchanged — `GlyphManager`'s documented contract) |
| Shape + TextQuadLayout/CurvedTextLayout | main (moves in Stage C) |
| `SymbolTileLabelStore` mutations, `GlyphAtlasTexture.Upload` | **main** |

**Teeth (Stage B):** thread attribution via the existing recorder pattern
(`MeshDataThreadWriteSpikeTests` precedent): a `ProfilerRecorder` on `MapRenderer.Symbol.TileDecode`
created with `CollectOnlyOnCurrentThread` on the main thread records **zero** samples across a full
build, while the cross-thread recorder records ≥1. Plus byte-parity: built `LabelInstance` lists equal
the pre-change baseline for the fixture tile (order included — `FeatureIndex` tiebreak preserved).

> **Implemented 2026-07-11 (gate green 1335/1335).** `StyledSymbolTileBuilder` split into worker-safe
> `ExtractLayers` + main-thread `ShapeAsync` (`BuildAsync` is now a byte-parity wrapper). `BuildTileAsync`
> is exactly the linear async above: `SwitchToThreadPool` → decode+extract → `SwitchToMainThread(ct)` →
> shape. **Test-harness note (important):** `SwitchToMainThread` posts to the PlayerLoop `Update` queue,
> which a synchronous `[Test]` never pumps — so the two completion-dependent symbol teeth (coalesced-upload,
> restyle-cancellation) plus the new off-main attribution tooth had to become **`[UnityTest]` coroutines
> that `yield return null`** (each yield ticks the editor PlayerLoop → drains UniTask's main-thread
> continuations). Production is unaffected (Play mode pumps every frame); this is purely how the async build
> is driven to completion in EditMode. Do NOT use `.GetAwaiter().GetResult()` on a main-thread-hopping build
> in a `[Test]` — it deadlocks (main blocked, can't pump).

### 2.3 Stage C — shaping + layout off the main thread (the shared-atlas hazard)

The hazard: pass 2 reads `GlyphCache` (via `FontStackResolver : IGlyphMetricsProvider`) and
`GlyphAtlas` (via `IGlyphAtlasView.TryGetEntry/Size`) while the main thread may concurrently run
*another* tile's pass 1 (`GlyphCache.Store` + `GlyphAtlas.Append` — Dictionary mutation ⇒ torn reads).

**Design: immutable per-tile snapshot, no lock.** After this tile's pass 1 completes on main, every
glyph this tile needs is cached and appended; the atlas is FIXED-size so `Size` is a constant. Snapshot
exactly the read surface the shaper + layouts consume — both are small existing interfaces:

```csharp
namespace MapRenderer.Core.Text
{
    /// <summary>An immutable, per-tile snapshot of the glyph read surface — built ON THE MAIN THREAD
    /// after the tile's pass-1 appends, then handed to a worker for shape/layout. Copies only the
    /// entries the tile's texts reference (bounded by total text length), so concurrent main-thread
    /// Append/Store for OTHER tiles can never tear a worker read. Engine-free (Core).</summary>
    public sealed class GlyphResolutionSnapshot : IGlyphMetricsProvider, IGlyphAtlasView
    {
        /// <summary>Resolve + copy every codepoint of <paramref name="texts"/> against the live
        /// cache/atlas. MAIN THREAD (reads the mutable structures).</summary>
        public static GlyphResolutionSnapshot Capture(
            FontStack fontStack, GlyphCache cache, GlyphAtlas atlas, IReadOnlyList<string> texts);

        public int2 Size { get; }                                        // fixed atlas size
        public bool TryGetEntry(uint codepoint, out GlyphAtlasEntry entry);
        // IGlyphMetricsProvider — same members FontStackResolver provides, served from the copy.
    }
}
```

`BuildTileAsync` Stage C flow: main (pass 1 ensure) → main `Capture` → `SwitchToThreadPool` →
`Shape`/`TextQuadLayout.Layout(run, snapshot, …)`/`CurvedTextLayout.Layout(run, snapshot)` (they already
take `IGlyphAtlasView`; the shaper already takes `IGlyphMetricsProvider` — no layout/shaper changes) →
`SwitchToMainThread(ct)` → `_store.CompleteBuild`. `GlyphAtlas.Append` never leaves the main thread;
no lock is added anywhere. Load-path allocation: one snapshot per tile build (dictionary sized to the
tile's distinct codepoints) — load path, allowed; called out.

**Teeth (Stage C):** (1) same thread-attribution recorder on a new
`MapRenderer.Symbol.ShapeLayout` marker (zero main-thread samples); (2) *torn-read regression*: two
tiles built concurrently through a gated fake glyph source, tile B's pass 1 appending new glyphs while
tile A shapes ⇒ tile A's label UVs byte-equal a serial-build baseline (a shallow implementation that
passes the live atlas to the worker can fail this only probabilistically — the snapshot makes it
structural, and the test asserts the worker path type-cannot see the live atlas by API: the worker
lambda captures only the snapshot).

**I could not verify** `IGlyphMetricsProvider`'s full member list in this pass (only its consumers);
`Capture` must mirror whatever `FontStackResolver` serves the shaper — confirm at implementation time.

---

## 3. Cover-recompute gate (stall #6)

`Tick` control flow today: `PumpPending` → `if (!_coverDirty && pending == 0) return;` → full recompute.
`pending > 0` therefore forces the descent every frame of the whole load window for zero effect.

Change (with §5's release queue already in mind):

```csharp
DrainPendingDisposal();            // (engine.DrainDiscards() post-decomposition)
DrainPendingFetchDisposal();
DrainReleaseQueue(cfg.MaxReleasesPerTick);   // §5 — must run every frame, hence ABOVE the gate
int pending = PumpPending(...);
if (!_coverDirty) return;                     // pending no longer forces the recompute
CoverRecomputesLastTick = 1;
// ... descent + request/release-enqueue + cover-key commit (unchanged)
```

`pending` is dropped from the gate entirely — its only historical job was to keep Tick from early-outing
*before* `PumpPending`, and the pump now runs unconditionally above the gate. Verified no hidden
dependency: the request loop keys off `!_loaded.ContainsKey` (no-op on unchanged cover) and the release
loop keys off cover membership (finds nothing on unchanged cover); neither has a side effect the pump
needs. The review flagged the same conclusion with the same caveat — the cover/consume suites are the
final arbiter.

**Tooth** (the review's, made concrete): style a map, hold the camera still, set
`BlockConsumeForTests = true` (D12) so a backlog pends; run 10 Ticks ⇒
`Σ CoverRecomputesLastTick(ticks 2..10) == 0` while `pending > 0` throughout (assert via
`CaptureTelemetry().PendingTileCount`). Control: nudge zoom by 0.01 ⇒ next Tick's counter == 1.
Today's code fails the first assertion (counter == 1 every tick).

---

## 4. Two-phase kick + mesher vertex cap (stalls #4/#5)

Prerequisite (review #5): capture `PmMeshDataAllocate` numbers from a liberty-style pan session first —
this stage's alloc-loop half is measure-first. The overshoot half (#4) is structurally certain regardless.

### 4.1 New `LoadedTile` state

```csharp
internal struct LoadedTile
{
    // ... existing fields unchanged ...
    /// <summary>Two-phase kick, phase A: the worker measure task (decode + geometry + chunk plan into
    /// interim native buffers). Set by PumpPending's kick branch; cleared when KickWrite consumes it.
    /// "Measured, awaiting allocation" ⇔ HasMeasure && MeasureTask.IsCompleted && !HasMeshBuild.</summary>
    public UniTask<MeasuredGeometry> MeasureTask;
    public bool HasMeasure;
}
```

Pump state machine gains one branch, ordered before the existing consume branch:

```
ReadyBytes != null            → (kick cap) engine.KickMeasure(...)        [was: Kick]
HasMeasure && task completed  → (kick cap, SAME budget) engine.KickWrite  [NEW: the main-thread alloc]
HasMeasure && task running    → pending++
HasMeshBuild && completed     → Consume under dual budget                  [unchanged]
```

`KickWrite` is charged against `MaxMeshBuildsPerTick` — it is the step that does main-thread
`AllocateWritableMeshData` work, which is exactly what that cap exists to bound. **Latency cost, stated
plainly: every tile gains +1 frame** (measure completes frame N; alloc+write kicks frame N+1 at the
earliest) — invisible next to network fetch, and the review's #4 accepts it explicitly.

Mid-flight release: `RenderTeardownRecord` stashes a live/completed `MeasureTask` via
`engine.StashDiscardMeasure` (the pen disposes `MeasuredGeometry`'s native buffers). `MeasuredGeometry`
gets a `DebugLiveCount` mirroring `MeshDataPayload.LiveAllocCount` (leak tooth).

### 4.2 Mesher seam — `ILayerGeometry` replaces `IRenderLayer.WriteInto`

```csharp
namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>Phase-A product of one layer's mesh build: the projected geometry in interim native
    /// buffers plus a chunk plan (feature-boundary splits, ≤ maxVerticesPerChunk target). Value-type
    /// data per the mesh-lifetime contract: disposed deterministically by KickWrite (after writing) or
    /// the measure holding pen (discard).</summary>
    internal interface ILayerGeometry : System.IDisposable
    {
        int ChunkCount { get; }                    // 0 = empty layer (no geometry)
        int ChunkVertexCount(int chunk);
        /// <summary>WORKER: write one chunk into a caller-allocated MeshData (exact-size — the caller
        /// allocated from ChunkVertexCount/index counts). Chunk-local indices are rebased from the
        /// interim buffers' global indices (per-feature triangles never cross features, so a
        /// feature-boundary chunk is self-contained).</summary>
        void WriteChunk(int chunk, Mesh.MeshData md, out int vertexCount, out Bounds bounds);
    }

    internal interface IRenderLayer : System.IDisposable
    {
        // StyleLayer, Material, ApplyZoom unchanged.
        /// <summary>WORKER: decode-to-geometry + chunk plan. Replaces WriteInto (which fused measure
        /// and write and therefore forced the main thread to pre-allocate blind).</summary>
        ILayerGeometry Measure(IReadOnlyList<MvtFeature> features, double zoom, double extent,
            TileId id, double3 tileOriginRender, IProjection projection, int maxVerticesPerChunk);
    }
}
```

`StyledFillTileBuilder`/`StyledLineTileBuilder` split accordingly: the existing
decode→assemble→earcut→project front half (through `TileMeshBuffers` / the globe-subdivided
`NativeList`s) becomes the `Measure` body — the buffers are **retained** in the returned geometry object
instead of disposed in a `finally`; the stream-write back half becomes `WriteChunk` (same code,
parameterised by a `[vertexStart, vertexCount)` / `[indexStart, indexCount)` window, indices written as
`global − chunkVertexStart`). Chunk planning walks `VertexFeatureIdx` (per-feature vertex runs are
contiguous — features are appended sequentially) accumulating features until the next feature would
cross `maxVerticesPerChunk` (32 768 target, D6). **A single feature larger than the target ships as one
oversized chunk** — splitting inside a feature means re-triangulation; the overshoot bound becomes
"one *feature*" instead of "one *layer*", which is the practical win (a low-zoom ocean *layer* is many
polygons; a single monster polygon remains a known residual, called out honestly).

`MeasuredGeometry` (engine-level) = the per-tile aggregate: `ILayerGeometry[]` (dense order) + the
capture set (`TileId`, `denseLayerIds`, origin) + the chunk→(layer, chunkIndex) flat plan.

### 4.3 Allocation + consume + cache

`KickWrite` (main): for each **non-empty** chunk, `MeshDataPayload.AllocateTracked(1)` under
`PmMeshDataAllocate` — exact counts are known, so the "dozens of 0-vertex arrays allocated, carried,
disposed unused" loop (#5) is gone: allocations == non-empty chunk count. Per-chunk single-element
arrays are kept deliberately — `ApplyAndDisposeWritableMeshData` applies a whole array at once, and the
S87 resumable per-mesh consume requires applying one mesh at a time (the review's "interim single
`AllocateWritableMeshData(dense)`" trap).

Consume/backends: **verified no changes needed.** Payloads already carry `MaterialIndex`
(`IRenderLayerPayload.MaterialIndex`); `ConsumeMeshBuild` walks a dense payload array of arbitrary
length via `ConsumeCursor` and appends to the parallel `Meshes`/`DrawHandles`/`MaterialIndices` arrays;
`AddTileLayer(mesh, origin, materialIndex, tileId)` has no per-(tile, layer) uniqueness assumption in any
backend (Entities: entity per call; BRG: `DrawItem` per call; GameObjects: GO per call). Two payloads
sharing a `MaterialIndex` simply produce two draw items with the same material — correct draw order is
preserved because chunks are emitted in dense/draw order and the backends order by material
renderQueue, not item insertion.

**One real ripple the review missed:** `PreparedTileCache.Put` destroys the previous entry on key
collision — with K chunks per (style, tile, layerId), the release-path transfer would destroy chunks
1..K−1 and a cache-hit revisit would silently drop most of a layer's geometry. **D7:** the cache value
becomes `Mesh[]` (`null` array stays the empty-layer marker; byte estimate sums the chunks);
`TransferOnRelease` groups the record's tracked meshes by `MaterialIndices[i]` before `Put`;
`TakeAsLoadedTile` registers each chunk of a taken array. Key/probe/completeness semantics are
unchanged. Load-path allocation (the grouping): fine, flagged.

**Teeth:**
- *Overshoot bound:* fixture layer with ≥100k verts across ≥4 features; `MaxVerticesPerTick = 40 000` ⇒
  per-tick `VerticesConsumedLastTick ≤ 40 000 + 32 768`, and the layer produces ≥3 meshes
  (`GetTileMeshes(id).Length ≥ 3` for that layer's material index). Today's code consumes all 100k in
  one tick — fails the bound.
- *Allocation exactness:* new engine counter `internal int MeshDataArraysAllocatedLastKick`; a style
  with D dense layers of which E produce geometry ⇒ counter == Σ non-empty chunks ≤ would-be D; with the
  liberty fixture (many empty layers per tile) assert counter < D. Today's code allocates exactly D —
  fails.
- *Leak:* release a tile in the measured-awaiting-alloc state ⇒ `MeasuredGeometry.DebugLiveCount`
  returns to 0 after the pen drain; `MeshDataPayload.DebugLiveAllocCount` net-zero across the cycle
  (existing guard extends).
- *Cache round-trip:* build a chunked tile → release (transfer) → revisit (hit) ⇒ vertex-count sum of
  re-registered meshes equals the original build's; a shallow `Mesh`-valued cache fails (loses chunks).
- *Parity:* full GPU-snapshot suite unchanged (chunking must not alter rendered output).

---

## 5. Release-path budgeting (stall #2)

### 5.1 Deferred-release queue in `TileManager`

```csharp
// TileSelectionConfig gains:
/// <summary>Per-frame budget of (tile, source) RECORDS fully released (backend removal + mesh
/// destroy/transfer + scheduler release) per Tick. 0 = uncapped (D12-consistent). Default 4.</summary>
public int MaxReleasesPerTick;

// TileManager fields:
private readonly Queue<LoadedKey>   _releaseQueue  = new Queue<LoadedKey>(64);
private readonly HashSet<LoadedKey> _releaseQueued = new HashSet<LoadedKey>();  // dedup across recomputes
internal int TilesReleasedLastTick { get; private set; }
internal int ReleaseQueueDepth     => _releaseQueue.Count;
```

- Tick's release loop enqueues instead of releasing: `if (_releaseQueued.Add(key)) _releaseQueue.Enqueue(key);`.
- `DrainReleaseQueue(int budget)` runs **every frame above the cover gate** (§3): dequeue up to
  `budget` keys; for each — remove from `_releaseQueued`; **re-validate**: if `_coverSet.Contains(key.Tile)`
  (the tile came back during the 1–3 frame linger) or `!_loaded.ContainsKey(key)` (restyle cleared it),
  skip; else `ReleaseTile(key)` (unchanged body). Re-validation converts fast pan-out-pan-back from
  destroy+refetch churn into a free no-op — a behaviour improvement, tested below.
- Records queued for release are still in `_loaded` and still pumped for 1–3 frames; the only guard
  added is the kick branch skipping `_releaseQueued.Contains(key)` records (don't start a build for a
  tile already condemned). In-flight work proceeds to the existing pens.
- `SetSources` clears both structures (records are torn down wholesale there anyway).
- Zero-GC: both containers pre-sized; `Queue<LoadedKey>`/`HashSet<LoadedKey>` of the zero-boxing struct
  key are steady-state alloc-free after warm-up, and the steady state (no cover churn) never touches them.

Tradeoff: released tiles linger 1–3 frames — off-screen by definition; `ReadyBytes`/task memory
retention extends by the same bound.

### 5.2 Batched removal — backend seam

```csharp
internal interface ITileRenderBackend : IDisposable
{
    int  AddTileLayer(Mesh mesh, double3 tileOriginRender, int materialIndex, TileId tileId);
    void RemoveItem(int handle);
    /// <summary>Removes many draw items in ONE backend operation where the backend supports it —
    /// the Entities backend destroys all layer entities (plus any tile roots emptied by the batch)
    /// via a single EntityManager.DestroyEntity(NativeArray&lt;Entity&gt;) structural change instead of
    /// one per layer. BRG/GameObjects default to a RemoveItem loop. Idempotent for unknown handles.</summary>
    void RemoveItems(ReadOnlySpan<int> handles);
    void Rebuild(in SceneFrame frame);
    Bounds ComputeSceneBounds(float tileSizeWorld);
}
```

`RenderTeardownRecord` switches its unregister loop to
`_instanced.RemoveItems(lt.DrawHandles)`. Entities implementation: reuse a persistent
`NativeList<Entity> _destroyScratch` member; append each handle's entity, run the existing root
bookkeeping (decrement `ChildCount`, append emptied roots to the same list), then one
`_em.DestroyEntity(_destroyScratch.AsArray())`; `_destroyScratch.Clear()`. One structural change per
released record (≤ `MaxReleasesPerTick` per frame) instead of L+1 per record. BRG/GameObjects: trivial
loop over `RemoveItem` (BRG's remove is a dict remove; GameObjects is Inspector-debug only).

With budget R=4 and L=30 layers: worst frame goes from 450 structural changes (review's example) to ≤4.

**Teeth:**
- *Budget:* load 8 tiles (single source), zoom far out so all leave cover; `MaxReleasesPerTick = 2` ⇒
  Tick 1: `TilesReleasedLastTick == 2`, `ReleaseQueueDepth == 6`; after 4 ticks queue empty,
  `LoadedTileCount == 0` (or the new cover's count), `CountMeshObjects` back to baseline (no leak
  through the deferral). Today's code releases all 8 in one Tick — fails the first assertion.
- *Batching:* new internal counter on `Entities.TileRenderer`: `DestroyEntityBatchesLastRemove`
  (incremented once per `RemoveItems` call that destroyed ≥1 entity) + `EntitiesDestroyedLastRemove`.
  Release a 10-layer tile ⇒ batches == 1, destroyed == 11 (10 layers + root). A shallow
  `RemoveItems`-loops-over-`RemoveItem` implementation on Entities yields batches == 0 (counter never
  incremented) — fails.
- *Pan-return:* tile leaves cover, camera returns before its dequeue ⇒ record survives:
  `MeshBuildsKickedLastTick == 0` and `PreparedCacheMisses` unchanged across the round trip (today the
  tile is destroyed and re-fetched or cache-probed — either observable moves).

---

## 6. Entities `AddTileLayer` redesign (stall #3)

**Decision (D10): fix Entities and keep it the ship default; BRG stays the opt-in.** Reasoning: the fix
is small and uses EG's own intended fast path (ID-based `MaterialMeshInfo` exists precisely to bypass
per-entity `RenderMeshArray` registration), it preserves the Entities-Hierarchy debuggability that is the
backend's documented reason to exist, and shipping BRG as default would first require fixing BRG's own
steady-state per-frame repack (review #8: 33 material reads × N + full `SetData` every frame) — a second
work package. **RESOLVED (2026-07-11):** the maintainer accepted fix-Entities-keep-default; BRG-as-default
(with #8's dirty-flagging prerequisite) is not pursued now. The maintainer's notes lean BRG long-term, so
revisit if the per-frame repack is ever fixed independently.

Implementation (in `Backend/Entities/TileRenderer.cs`):

```csharp
// Construction (after world init):
private EntitiesGraphicsSystem _eg;               // _world.GetExistingSystemManaged<EntitiesGraphicsSystem>()
private BatchMaterialID[]      _materialIds;      // RegisterMaterial per layer material, once
private Entity                 _layerPrototype;   // built ONCE via RenderMeshUtility.AddComponents with a
                                                  // placeholder RenderMeshArray + Parent/LocalTransform added,
                                                  // Prefab-tagged so it never renders itself

// ItemRec gains:  public BatchMeshID MeshId;     // for unregister-on-remove

public int AddTileLayer(Mesh mesh, double3 tileOriginRender, int materialIndex, TileId tileId)
{
    // per call: ONE structural op (Instantiate preserves the prototype archetype incl. shared
    // components), then pure SetComponentData:
    Entity e = _em.Instantiate(_layerPrototype);
    BatchMeshID meshId = _eg.RegisterMesh(mesh);
    _em.SetComponentData(e, MaterialMeshInfo.FromMeshIDAndMaterialID(meshId, _materialIds[materialIndex]));
    _em.SetComponentData(e, new Parent { Value = GetOrCreateRoot(tileId, tileOriginRender) });
    // LocalTransform.Identity, LocalToWorld, RenderBounds sets as today (sets, not migrations).
}
```

- `RemoveItems`/`RemoveItem` gain `_eg.UnregisterMesh(item.MeshId)` per destroyed item (the explicit
  unregister the ID route requires — without it EG's mesh registry grows unboundedly and holds destroyed
  meshes' IDs). Materials are unregistered in `DoDispose` only if the world is still alive at that point
  (world disposal tears down EG's registries wholesale; the unregister is for symmetry, not correctness —
  note in code).
- Structural cost per consumed mesh: today 2 structural changes + a fresh one-element `RenderMeshArray`
  shared-component (EG batch registration per entity); after: 1 `Instantiate` + `RegisterMesh` (a
  registry add, not an archetype change). At `MaxConsumesPerTick = 4`: ≤4 instantiates/frame.
- Root creation path unchanged (roots are rare — one per tile).

**Risk / unverified:** the exact component set `RenderMeshUtility.AddComponents` stamps varies by
Entities Graphics version; the prototype+`Instantiate` pattern is EG's documented runtime-creation
recipe, but the placeholder-`RenderMeshArray`-on-prototype detail must be validated in-project (a
zero-length `RenderMeshArray` shared component on instances is expected to be inert under ID-based
`MaterialMeshInfo` — verify with the render-parity suite; fall back to registering one shared dummy
array if EG asserts).

**Teeth:**
- *No per-entity array:* internal counter `RenderMeshArraysCreated` — after N `AddTileLayer` calls it
  is ≤1 (the prototype's). Today's code creates N — fails.
- *Registry balance:* `_eg`-registered mesh count is not directly readable; track our own
  `internal int RegisteredMeshCount` (inc on RegisterMesh, dec on UnregisterMesh). After a full
  load→release cycle: `RegisteredMeshCount == 0` — catches a missing unregister (the ID route's known
  trap, review #3 tradeoff).
- *Parity:* GPU-snapshot suite on the Entities backend byte-identical; Entities Hierarchy probes
  (`RootChildBufferCount`, `IsParentedToTileRoot`, entity names) unchanged.
- *Spike:* `MapRenderer.Tile.AddLayer.Register` marker remains for before/after Profiler comparison
  (manual evidence, not a test gate).

---

## 7. Ordered plan (each step independently shippable, gate = EditMode suite green)

| Step | Content | Section | Depends on | Risk |
|---|---|---|---|---|
| 1 ✅ DONE | Symbol Stage A: queue + ≤1 build/frame + upload coalescing + CTS/CancellationToken | §2.1 | — | low (scheduling only; attacks the worst stall) |
| 2 ✅ DONE | Symbol Stage B: decode+extract to pool | §2.2 | 1 | low |
| 3 ✅ DONE | Cover gate on `_coverDirty` alone + tooth | §3 | — | low (one line; suites arbitrate) |
| 4 ✅ DONE | Release queue + `MaxReleasesPerTick` + `RemoveItems` seam + Entities batched destroy | §5 | 3 (drain placement) | medium (lifecycle-adjacent; pens unchanged) |
| 5 ✅ DONE | Entities `AddTileLayer` prototype/ID redesign + unregister-on-remove | §6 | — | medium (EG version detail) |
| 6 | **Measure**: GC pan-storm profile (review #7) + `PmMeshDataAllocate` capture — Editor session, no code | — | — | none (gates 9's alloc-half and any pooling work; do nothing on #7 until confirmed) |
| 7 | Decompose M1+M2 (`LoadedTile` de-nest; `SourcePipelineRegistry` + dense-id cache) | §1.1–1.2 | — | low |
| 8 | Decompose M3+M4 (`PreparedTileBridge`; `TileMeshBuildEngine` + test-surface exit + D12 + comment prune) | §1.3–1.6 | 7 | medium (large mechanical move; leak suites are the net) |
| 9 | Two-phase kick + `ILayerGeometry` chunking + `Mesh[]` cache values | §4 | 8 (lands in the engine), 6 (numbers) | high (mesher change; parity suite is the net) |
| 10 | Symbol Stage C: snapshot + shaping off-thread | §2.3 | 2 | medium (the documented atlas hazard — snapshot defuses it structurally) |

Steps 1–5 are independent of the decomposition and deliberately front-loaded (review §5 sequence);
7–8 must precede 9 so the two-phase kick lands in `TileMeshBuildEngine`, not in another 200 lines of
the god-object. Step 10 can run any time after 2.

### Step 1 — implemented 2026-07-11 (EditMode gate green: 1328/1328)

Landed in `SymbolLabelSubsystem.cs` + `MapView.cs`, with 4 teeth in
`MapRenderer.Tests.EditMode/Text/SymbolLabelSubsystemPumpTests.cs` (bounded-starts, coalesced-upload,
stale-drop, restyle-cancellation). Two deviations from §2.1 as written, both deliberate:
- **`PumpBuilds()` runs AFTER `ReconcileLoadedTiles`, not before.** §2.1 said "before", but the
  stale-drop needs `_loadedNow` to reflect this frame's loaded set (and §2.1's own stale-drop tooth
  requires reconcile-then-pump). Placed after reconcile, before `CollectInto`.
- **Added an `internal Func<StyleDocument, IGlyphSource> GlyphSourceFactoryOverride` seam** so the teeth
  can inject a fixture/gated `TestGlyphSource` (mirrors the existing `IDataSource` dependency-inversion);
  null ⇒ production `GlyphSourceFactory.Create`. `MaxBuildsPerFrame` kept as a plain property (default 1);
  MapViewConfig serialization deferred as noted. Not committed (working tree).

---

## 8. RESOLVED DECISIONS (maintainer, 2026-07-11)

All five recommended defaults accepted by the maintainer. This section is now decision-complete.

1. **Entities-fixed-as-default** ✅ — fix Entities, keep it the ship default (§6, D10). BRG stays the
   zero-alloc opt-in; BRG-as-default (with review #8 repack dirty-flagging) is NOT pursued now.
2. **Budget defaults** ✅ — `MaxReleasesPerTick` = 4 (mirrors `MaxConsumesPerTick`); symbol
   `MaxBuildsPerFrame` = 1. Both Inspector-serialized; tune in-editor if needed.
3. **Chunk vertex target** ✅ — 32 768. Serialized constant (not Inspector). Revisit only if the step-6
   `PmMeshDataAllocate` numbers argue for a different balance.
4. **PreparedTileCache byte budget** ✅ — keep the S82 placeholder for now (unchanged by this design;
   D7 only changes the value shape). Maintainer's in-editor VRAM profiling remains a later tuning pass.
5. **GC-stall hypothesis (review #7)** ✅ — measure first (step 6); enable incremental GC only if the
   trace confirms collections landing in the load window; no decode-buffer pooling otherwise.

## 9. Honest costs, risks, and what was not verified

- **Latency:** +1 frame per tile from the two-phase kick (§4.1); labels a few frames later (§2.1);
  released tiles linger 1–3 frames (§5.1). All judged invisible; all stated.
- **More, smaller meshes** after chunking: more draw items for monster layers (bounded by geometry
  size / 32k); BRG per-frame repack cost scales with item count — another nudge toward D10's
  keep-Entities-default.
- **Not verified in this pass:** `IGlyphMetricsProvider`'s full member list (§2.3); the exact EG
  component set / placeholder-`RenderMeshArray` inertness for the prototype pattern (§6); whether any
  test currently depends on `MaxConsumesPerTick == 0`-blocks semantics beyond backlog-building (D12
  migration must sweep those call sites); the cover gate's no-hidden-dependency claim is argued from
  code reading — the cover/consume suites are the arbiter (§3).
- **Load-path allocations introduced** (allowed, listed): symbol build queue entries + per-tile glyph
  snapshot (§2), `MeasuredGeometry`/chunk plans (§4), cache-transfer `Mesh[]` grouping (§4.3),
  release-queue warm-up (§5.1). Steady-state zero-GC surface is untouched by design in every section;
  the existing `MapView_SteadyStateTick_DoesNotAllocateGCMemory` tooth gates all of them.
