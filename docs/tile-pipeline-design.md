# Tile-pipeline design — TileManager decomposition + stall elimination

**Status:** design + live plan. Steps 1–5 (the scheduling/backend stall fixes) are **implemented**; steps 6–10
(the measure-first profiling, the decomposition, the two-phase kick, off-thread symbol shaping) are **pending**.
The per-layer processor unification that reshaped `KickMeshBuild` is a **separate, complete** epic —
`docs/per-layer-tile-processing-design.md`; read it alongside this. All paths relative to
`Assets/Code/MapRenderer.Unity/` unless noted.

Hard constraints throughout (see `docs/async-architecture.md`): Core stays engine-free; UniTask only;
`UnityEngine.Object` create/destroy and `Mesh.AllocateWritableMeshData`/`ApplyAndDisposeWritableMeshData`
main-thread only; single-owner Mesh with null-on-transfer; steady-state zero-GC
(`MapView_SteadyStateTick_DoesNotAllocateGCMemory`); load-path allocations flagged where introduced.

---

## 0. The two problems this addresses

Two maintainer-raised problems, findings verified against source:

1. **`TileManager` is a ~2067-line god-object** by responsibility count (seven distinct jobs). The raw line
   count overstates it — ~920 lines are code, ~695 comments, ~175 blank — and ~40% of the file is comment
   archaeology (S47/S51/S55/S82/S83b/S84/S87/S89 stage tags restating superseded semantics; the S55-vs-S87 budget
   story is told at least three times). Of the code, ~300 lines are extraction-worthy accident, ~180 are
   test/telemetry that shouldn't be here (the repo's own convention), and the rest is irreducible lifecycle. The
   per-(tile,source) `LoadedTile` state machine with its four async exit paths is genuine essential complexity
   and stays in one place; the source registry, the kick/consume engine, and the prepared-cache bridge are
   cleanly separable (§1).
2. **Residual intermittent main-thread stalls** despite hard rate-limiting (kick cap, dual consume budget of
   mesh-count AND vertex-count, resumable per-mesh consume). The budgets correctly bound the *consume* path — but
   three whole categories of main-thread work are **unbudgeted**: the synchronous symbol-label build burst
   (§2), the release/eviction path (§5), and per-layer ECS structural cost inside the budgeted consume (§6). Plus
   the one genuine mesher fix (§4) and two measurement-first items. These are structural, fixable problems, not
   tuning problems.

### Ranked stall sources (verified file:line)

| # | Source | What stalls / when | Fix | Section |
|---|---|---|---|---|
| 1 | **Synchronous symbol-label build burst (unbudgeted)** | `SymbolTileBytesReady` fires inside `PumpPending` for **every** fetch completing this frame; `BuildTileAsync` runs synchronously on main — a second `MvtDecoder.Decode`, extract, and (once glyph ranges are cached) all shaping + layout inline, plus a full 4096² R8 = 16 MB atlas upload per tile that added a glyph. The known 600 ms-class stall. | Queue + ≤1 build/frame + coalesced upload; decode+extract off-thread; shaping off-thread against an immutable snapshot | §2 |
| 2 | **Unbudgeted release/eviction storm** | Zoom-out/fast pan releases N tiles × L layers in one frame: N·L entity destroys, N·L cache Puts, plus a `DestroyMesh` burst on byte-budget overflow. Consume is budgeted; release is not — a mirror-image spike (15 tiles × 30 layers = 450 structural changes/frame on Entities). | Deferred-release queue with a per-frame budget; batch `DestroyEntity` on Entities | §5 |
| 3 | **Entities `AddTileLayer`: per-layer `RenderMeshArray` + structural changes** | Every consumed mesh creates a fresh one-element `RenderMeshArray` shared component → EG batch registration per entity, plus `CreateEntity` + a `ComponentTypeSet` migration (own comment: "PRIME SUSPECT"). | `RegisterMesh`/`RegisterMaterial` IDs + ID-based `MaterialMeshInfo` + prototype `Instantiate` + unregister-on-remove | §6 |
| 4 | **One-mesh overshoot** (the only mesher fix) | Budget checked *before* each layer, so a single 100k+-vert water/landcover fill uploads whole — the vertex budget cannot cap it. Worst on exactly the tiles users look at (oceans at low zoom). | Cap verts per payload (~32k, split at feature boundaries); requires the two-phase kick | §4 |
| 5 | **`KickMeshBuild` main-thread allocate loop** | One native MeshData alloc per this-source layer per kick — dozens/kick, most ending 0-vertex (allocated, carried, disposed unused). | Subsumed by the two-phase kick (#4). Measure-first via `PmMeshDataAllocate` | §4 |
| 6 | **Cover recompute every frame while anything pends** | Static camera + pending tiles → full `SelectVisibleTiles` descent + `_coverSet` rebuild + cover×pipeline probes every frame **for zero effect** (~0.5–2 ms/frame during exactly the frames already under consume load). | Gate the recompute on `_coverDirty` alone | §3 |
| 7 | **GC collections from off-thread load work** | Per-tile `byte[]`, `MvtDecoder.Decode` object graphs on the pool, payload/closure allocs per kick — Mono GC stops the main thread regardless of which thread allocated. Hypothesis, not measured. | Profile with GC markers during a pan storm; enable incremental GC if confirmed; pool decode buffers | measure-first |
| 8 | **BRG `Rebuild` full per-frame repack** (opt-in path) | Per frame: sort all items, repack 76 floats × N, 33 material-property reads × N, full `SetData`. Steady cost, not a spike. | Dirty-flag material props; repack transforms only on frame change; partial `SetData` | only if BRG becomes default |
| 9 | **Restyle hitch** | Full teardown + Entities `World` rebuild + every cached mesh destroyed, one frame. Big but rare, user-initiated. | Accept; documented tradeoff | — |

Classification: #1, #2, #6 are **scheduling-only**; #3, #8 are **backend-only**; #4 (+#5) needs the **meshers**;
#7 is measurement-first.

### Decision summary

| # | Topic | Decision |
|---|---|---|
| D1 | Decomposition order | `SourcePipelineRegistry` → `PreparedTileBridge` → `TileMeshBuildEngine`; test surface exits to the test assembly in the engine step |
| D2 | Dense layer-id cache | Lives on `SourcePipeline` (computed once in `ApplySpecs`), **not** in the bridge — the kick, the probe, and the transfer all read the same `int[]`, so the three-consumer agreement invariant becomes structural |
| D3 | Symbol budgeting | Queue in `SymbolSubsystem` + explicit `PumpBuilds()` from `MapView.LateUpdate`; ≤1 build start/frame (configurable); atlas upload moves out of `TryBeginBuild` into the pump (≤1 upload/frame by construction) |
| D4 | Symbol threading | Stage 1: decode+extract on the pool. Stage 2: shape+layout on the pool against an immutable per-tile snapshot (`IGlyphMetricsProvider` + `IGlyphAtlasView`) built on main **after** pass-1 appends. Atlas/cache **writes never leave the main thread**; no lock |
| D5 | Two-phase kick | Measure task (worker, geometry into interim `TileMeshBuffers`) → main-thread exact-count `AllocateWritableMeshData(1)` per **non-empty chunk** → write task (worker). One `MeshDataArray` per chunk (a whole-array alloc conflicts with per-mesh resumable consume) |
| D6 | Mesher chunking | New `ILayerGeometry` seam (measure/write-chunk) replaces `IRenderLayer.WriteInto`; split at feature boundaries, target 32 768 verts/chunk; a single over-budget feature ships as one oversized chunk (no re-triangulation) |
| D7 | PreparedTileCache value | `Mesh` → `Mesh[]` per (style, tile, layerId) so a chunked layer round-trips the cache without the Put-collision destroying earlier chunks; `null` empty-layer marker unchanged |
| D8 | Release budgeting | Deferred-release queue drained ≤`MaxReleasesPerTick` records/frame; re-validated against the live cover at dequeue (a tile that came back is un-queued, not destroyed) |
| D9 | Batched removal | New `ITileRenderBackend.RemoveItems(ReadOnlySpan<int>)`; Entities implements it as **one** `EntityManager.DestroyEntity(NativeArray<Entity>)` per call (layers + emptied roots together) |
| D10 | Entities vs BRG | **Fix Entities, keep it the ship default.** Prototype-entity + `Instantiate` + `RegisterMesh`/`RegisterMaterial` + ID-based `MaterialMeshInfo`, with unregister-on-remove. BRG stays the zero-alloc opt-in. (maintainer-accepted, §8) |
| D11 | Cover gate | Recompute gated on `_coverDirty` alone; release-queue drain moves above the gate so it runs every frame |
| D12 | Budget-zero asymmetry | Unify: `MaxConsumesPerTick == 0` becomes **uncapped** like the other caps; tests get an explicit `internal bool BlockConsumeForTests` seam on the engine |

---

## 1. Decomposition

> *Anchor note (predates Epic A):* this is a still-live pending plan — its goals stand, but its target *signatures*
> were sketched before the per-layer tile-processing epic landed. Re-verify them against current source at
> implementation time; the one concretely-drifted anchor is §1.4's `Kick(…)` (now byte-agnostic — see there).

Target structure: three separable classes come out of `TileManager`; the `LoadedTile` state machine and its exit
paths (the single-owner mesh-lifetime contract) stay — smearing it across classes is how double-frees happen.
Cover-key/dirty tracking stays (cohesive with `Tick`). Holding pens move *with* their producers (mesh pen →
engine, fetch pen → stays), not into a generic "DisposalService".

### 1.1 Shared moves (prerequisite, mechanical)

`LoadedKey` and `LoadedTile` leave `TileManager`'s private nesting and become namespace-level `internal` structs
in `Rendering/Tile/LoadedTile.cs` — the engine takes `ref LoadedTile` and cannot see a private nested type. No
field changes. `SourceKey`/`SourceSpec` similarly become namespace-level `internal` types (they are already
`internal`; `MapView.BuildSourceSpecs` changes `Tile.TileManager.SourceSpec` → `Tile.SourceSpec`).

### 1.2 `SourcePipelineRegistry` — `Rendering/Tile/SourcePipelineRegistry.cs`

Moves out of `TileManager`: `SourcePipeline` (class), `SourceKey`, `SourceSpec`, the diff/keep/teardown body of
`SetSources` step 2, `FindPipeline`, `DisposePipelines`, `SourceIdOf`, `InFlightCount`. **Plus (D2):** the dense
layer-id computation (`ComputeDenseLayerIds` + scratch) is retired in favour of a per-source cached array.

```csharp
namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>One data pipeline per rendered source-id, plus the source's cached dense layer-id set —
    /// the single array the kick, the cache probe, and the release transfer all read, so the three can
    /// never disagree on what "this tile's complete prepared set" means.</summary>
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
        /// Computed once per ApplySpecs; immutable between restyles — safe to capture into a background
        /// closure WITHOUT a defensive copy.</summary>
        public int[]         DenseLayerIds;
    }

    internal sealed class SourcePipelineRegistry : System.IDisposable
    {
        public int Count { get; }
        public SourcePipeline this[int slot] { get; }

        /// <summary>Diffs specs against existing pipelines by (SourceId, SourceKey): kept pipelines survive
        /// with warm caches, new specs build fresh ones, unmatched pipelines are torn down. Recomputes every
        /// kept+new pipeline's DenseLayerIds from <paramref name="layers"/>.</summary>
        public void ApplySpecs(IReadOnlyList<SourceSpec> specs, Style.RenderLayerSet layers);

        public int InFlightCount { get; }        // summed in-flight fetches (telemetry)
        public void Dispose();                    // disposes every pipeline (scheduler always; source only when owned). Idempotent.
    }
}
```

Removing the per-probe/per-release O(layerCount) scan also removes the shared-scratch-list "never capture across
a thread boundary" trap and `KickMeshBuild`'s per-kick `int[]` copy — the cached array is immutable between
restyles, so the closure captures it directly. **Zero-GC note:** `ApplySpecs` allocates (restyle path, allowed);
the steady-state Tick does zero list work for the probe. `TileManager.SetSources` shrinks to: teardown `_loaded`
records → `_bridge.Clear()` → `_registry.ApplySpecs(specs, _layers)` → `BuildBackend(backend)` → re-arm cover.

### 1.3 `PreparedTileBridge` — `Rendering/Tile/PreparedTileBridge.cs`

Moves: ownership of `PreparedTileCache` + `_cacheEnabled` + `CurrentStyle`, the Tick probe block's cache branch,
`TransferBuiltMeshesToCache`, `BuildTileFromCache`.

```csharp
internal sealed class PreparedTileBridge : System.IDisposable
{
    public PreparedTileBridge(Map.PreparedTileCacheConfig config);

    public bool       Enabled      { get; }
    public StyleToken CurrentStyle { get; set; }   // MapView sets in SetStyle (via TileManager forward)
    // Telemetry forwards: Hits, Misses, Count, MaxCount, BytesHeld, ByteBudget, Evictions.

    /// <summary>Tick probe: true iff EVERY dense layerId is present (mesh or empty-marker). Counts one
    /// whole-tile Hit or Miss. Disabled ⇒ always false (counts a Miss).</summary>
    public bool ProbeFullHit(TileId id, int[] denseLayerIds);

    /// <summary>Cache-hit path: TryTake each layer's meshes (ownership back out — Model B), register
    /// non-null ones via backend.AddTileLayer, return the fully-Built record. Main-thread only.</summary>
    public LoadedTile TakeAsLoadedTile(Backend.ITileRenderBackend backend, TileId id, double3 origin, int[] denseLayerIds);

    /// <summary>Release path: transfer a Built record's tracked meshes in (nulls lt.Meshes /
    /// lt.MaterialIndices — the double-free guard), inserting empty-markers for uncovered dense ids.
    /// No-op when disabled or the record has no tracked meshes.</summary>
    public bool TransferOnRelease(TileId id, int[] denseLayerIds, ref LoadedTile lt);

    public void Clear();     // restyle purge (destroys held meshes; cache stays usable)
    public void Dispose();   // teardown (destroys held meshes)
}
```

The dead "structural parity" lock inside `PreparedTileCache` is deleted here — the class is documented
main-thread-only and the bridge preserves that; the removal is behaviour-preserving and covered by the existing
cache tests.

### 1.4 `TileMeshBuildEngine` — `Rendering/Tile/TileMeshBuildEngine.cs`

Moves: `MeshBuildResult`, `KickMeshBuild`, `ConsumeMeshBuild`, `FinishConsume`, `AppendMeshes`, `AppendInts`,
`DisposeWholeResult`, `_consumeScratch*`, `_pendingDisposal` + `DrainPendingDisposal` + the blocking drain from
`DoDispose`. This is where every future consume-path fix (two-phase kick, mesher split) lands without touching
the lifecycle class again — the main payoff of the split. The signatures below already show the final two-phase
form; the extraction step ships them with `KickMeasure` absent and today's single-phase `Kick` body (extraction
is behaviour-preserving; §4 then changes only this class + the meshers). *(Post-A7: the live
`TileManager.KickMeshBuild(ref LoadedTile, TileId, IDecodedTileHandle decode, string sourceId, …)` is
byte-agnostic — it takes an `IDecodedTileHandle`, not `byte[]`; the signatures below reflect that, and the
source pipeline exposes `ITileFeatureSource FeatureSource` rather than a raw byte source.)*

```csharp
internal sealed class TileMeshBuildEngine : System.IDisposable
{
    public TileMeshBuildEngine(Style.RenderLayerSet layers);

    /// <summary>Test seam (D12): true blocks Consume entirely — replaces the old "MaxConsumesPerTick == 0
    /// blocks" trick, so 0 can mean uncapped like every other budget.</summary>
    internal bool BlockConsumeForTests { get; set; }

    // ── Extraction step ships this (today's KickMeshBuild body, denseLayerIds from the registry) ──
    public UniTask<MeshBuildResult> Kick(TileId id, IDecodedTileHandle decode, int[] denseLayerIds, double3 tileOriginRender, IProjection projection);

    // ── §4 (two-phase) replaces Kick with the pair: ──
    public UniTask<MeasuredGeometry> KickMeasure(TileId id, IDecodedTileHandle decode, int[] denseLayerIds, double3 tileOriginRender, IProjection projection);
    /// <summary>MAIN THREAD: allocates one exact-size writable MeshDataArray per non-empty chunk, then
    /// schedules the worker write. Takes ownership of measured.</summary>
    public UniTask<MeshBuildResult> KickWrite(MeasuredGeometry measured);

    /// <summary>Resumable per-mesh consume under the dual budget. Registers each uploaded mesh with the
    /// passed backend (rebuilt on restyle, so the engine never holds a stale reference). Main-thread only.</summary>
    public bool Consume(Backend.ITileRenderBackend backend, TileId id, ref LoadedTile lt, int meshBudget, int vertBudget, out int meshesConsumed, out int vertsConsumed);

    public void StashDiscard(UniTask<MeshBuildResult> task);          // mid-flight-release holding pens
    public void StashDiscardMeasure(UniTask<MeasuredGeometry> task);
    public void DrainDiscards();                                       // non-blocking per-Tick poll of both pens
    public void DrainAllBlocking();                                    // teardown: spin every stashed task (bounded ~10 s cap)
    public void Dispose();                                             // == DrainAllBlocking()
}
```

`DoDispose` in `TileManager` changes its in-flight-build handling to: for each `_loaded` record with
`HasMeshBuild`/`HasMeasure` → `_engine.StashDiscard*(...)`; then `_engine.DrainAllBlocking()`. Same spin-cap
semantics, one owner for the spin code.

### 1.5 Slimmed `TileManager` — what stays, ownership, teardown

Stays (~450 code lines): `_loaded` (`Dictionary<LoadedKey, LoadedTile>`), `Tick` (cover key/dirty tracking,
request/release transition), `PumpPending` (state machine driver calling `_engine.Kick*/Consume`), `ReleaseTile`,
`RenderTeardownRecord`, `_pendingFetchDisposal` + drain,
`TakeDecodeFromFetch`/`DiscardFetchOutcome` (D0 split the single `ObserveFetchOutcome` into the owning and
the discarding arm)/`LogFetchErrorThrottled`,
`SymbolTileBytesReady`, `BuildBackend` + backend accessors + `InstancedRebuild`, `CaptureTelemetry` (production
caller: debug readout), `DoDispose` orchestration.

Construction: `_registry`, `_engine = new TileMeshBuildEngine(layers)`, `_bridge = new PreparedTileBridge(cacheConfig)`
— all `internal readonly` (test extensions reach them via `InternalsVisibleTo`).

`DoDispose` order (preserves "destroy meshes → dispose backend" exactly): (1) stash every `_loaded` in-flight
build into the engine pens; `DrainAllBlocking()`; (2) cancel + spin + observe in-flight fetches; drain
`_pendingFetchDisposal`; (3) `DestroyTrackedMeshes` over `_loaded`; clear; (4) `_bridge.Dispose()` (cached meshes
— still **before** the backend); (5) `_instanced?.Dispose()`; (6) `_registry.Dispose()` (schedulers + owned
sources — after the backend).

### 1.6 Test-surface extraction

Per the repo's test-bloat rule: `TryGetBuiltTile`, `GetTileMeshes`, `AllTilesSettled`, `ComputeSceneBounds`,
`DrainMeshBuilds` move to `TileManagerTestExtensions` in `MapRenderer.Tests.EditMode` (extension methods over the
now-`internal` `_loaded`/`_registry`/`_engine`/`_instanced`). `DrainMeshBuilds` re-expresses itself as: spin
fetch → `_engine.Kick` inline → spin task → `_engine.Consume(backend, …, int.MaxValue, int.MaxValue, …)`. The
`*LastTick` counters **stay** (read by `CaptureTelemetry`/the debug panel — production callers). Comment
archaeology is pruned during the moves — keep the *why*, drop the diffs git history holds.

**Migration steps** (each independently shippable, gate = `./Tools/run-tests.sh` green):

- **M1** — `LoadedTile.cs`/`SourceSpec` de-nesting (mechanical; MapView reference updates).
- **M2** — `SourcePipelineRegistry` + `DenseLayerIds`; consumers switch to `_registry[slot].DenseLayerIds`;
  delete `ComputeDenseLayerIds` + scratch. *Tooth:* existing multi-source + restyle suites; plus a new assertion
  that a restyle recomputes the dense set (a stale cache serves the OLD set and fails the completeness tests).
- **M3** — `PreparedTileBridge` (+ cache lock removal). *Tooth:* existing S82 cache suite passes unchanged;
  `CountMeshObjects` leak baseline unchanged across load→release→revisit.
- **M4** — `TileMeshBuildEngine` + test-surface extraction + D12 budget-zero unification. *Tooth:* full EditMode
  suite; the S87 resumable-consume and S48/S51 leak-guard tests exercise every moved path; a dedicated test flips
  `BlockConsumeForTests` and asserts a backlog builds.

### 1.7 `TileManager`'s extraction rationale (moved from its class doc, UMR-118)

`TileManager` was extracted from `MapView` as the biggest, most cohesive cut of the decomposition:
`MapView` keeps the camera, the `RenderLayerSet`, and the scene origin; `TileManager` keeps the scheduler,
the loaded-tile table, and all the in-flight async machinery, and is just ticked once per frame.

Explicit interface — the three things the lifecycle needs from `MapView`, passed in per call so `MapView`'s
inspector-editable config and rebasing scene origin stay authoritative:

- the current `CameraProperties`,
- the scene origin (Mercator) for tile placement,
- tile-selection config (`TileSelectionConfig`) read from `MapView`'s serialized fields.

The `RenderLayerSet` (render bundles) is stable for the object's life, so it's injected at construction.

### 1.8 `TileManager.SetSources`'s restyle decision log (moved from its method doc, UMR-118)

`SetSources` (the `MapView.SetStyle` entry point) handles BOTH first call and RESTYLE in one pass:

1. **Render-teardown EVERY existing record** (destroy meshes, unregister draw items, stash in-flight in the
   holding pens) and clear `_loaded` — the old records reference the old layer indexing + the old backend,
   so they must go. This does NOT touch any scheduler/cache, so a kept source's warm cache survives.
2. **Diff the registry by `SourceKey`**: a spec whose `(SourceId, Key)` matches an existing pipeline KEEPS
   that pipeline instance (its source + scheduler + cache, so already-fetched bytes are reused); a
   new/changed spec builds a fresh pipeline; an existing pipeline matched by no spec is pipeline-torn-down
   (scheduler + owned source disposed).
3. **Rebuild the backend** (the material list changed) and re-arm cover selection; the next Tick
   re-requests the cover, hitting kept caches (no re-fetch).

### 1.9 `TileManager.KickMeshBuild`'s bake-at-integer-zoom decision (moved from its method doc, UMR-118)

The paint bake zoom is the tile's INTEGER zoom (`id.Z`), not the fractional camera zoom — a tile is built
exactly once per load (never re-baked while `LoadedTile.Built`), so baking at `id.Z` makes the prepared
artifact a pure function of `(styleId, tileId, layerId)` — the sound key `PreparedTileCache` needs.
`CameraProperties` is not read for this purpose; the call sites take it only for other reasons.

This is the bake-parameter SSOT (alongside the clip-window bake parameter recorded as finding F-CLIP-1 in
`docs/meshing-design.md`) — see the commit that introduced the bake-revision mechanism,
`fix(tile-pipeline): key prepared tiles by bake revision`.

---

## 2. Symbol-path budgeting + off-thread staging (stall #1) — Stages A, B goals met (mechanism superseded by Epic A); C pending

> **⚠ Superseded mechanism (Epic A).** Stages A & B achieved their **goals** — symbol builds are budgeted and
> decode/extract run off the main thread — but the specific **mechanism** the code below documents (a
> `SymbolLabelSubsystem.OnTileBytesReady` → `_buildQueue` push feed, and a second `MvtDecoder.Decode` inside
> `BuildTileAsync`) was **removed** by the per-layer tile-processing epic. The live path is kick-driven:
> `TileManager.KickMeshBuild` → `ISymbolTileWorkerPass.RunWorkerAndHandoff` over a shared `IDecodedTileHandle`
> (no push queue, no per-symbol decode) — `TileSymbolKickTests` now asserts `OnTileBytesReady` / `_buildQueue` /
> `SymbolTileBytesReady` are zero-occurrence. For the current design see `docs/per-layer-tile-processing-design.md`
> §A3/A4/A5b. The §2.1/§2.2 snippets are retained as the stall-#1 problem framing and the budgeting / coalescing /
> cancellation goals they still describe accurately — not as a map of current code.

### 2.1 Stage A — scheduling only — ✅ goal met, mechanism retired (see banner)

Queue + build cap + upload coalescing + cancellation, all inside `SymbolLabelSubsystem`; `MapView` gains one
`PumpBuilds()` call.

```csharp
private readonly Queue<PendingSymbolBuild> _buildQueue = new(32);
private CancellationTokenSource _buildCts;   // recreated in SetStyle; cancelled in SetStyle/Dispose

/// <summary>Max symbol-tile builds STARTED per frame (≤). Default 1.</summary>
public int MaxBuildsPerFrame { get; set; } = 1;

// OnTileBytesReady: was BuildTileAsync(...).Forget() — now ONLY enqueues.
public void OnTileBytesReady(string sourceId, TileId tile, byte[] bytes);

/// <summary>MAIN THREAD, once per frame from MapView.LateUpdate (after TileManager.Tick, after
/// ReconcileLoadedTiles): starts ≤MaxBuildsPerFrame queued builds, skipping tiles that left the loaded set;
/// then performs AT MOST ONE atlas GPU upload if the glyph count grew since the last upload. Bounded.</summary>
public void PumpBuilds();
```

`BuildTileAsync` gains the token (threaded into `_builder.BuildAsync(..., ct)` → `EnsureGlyphRangesAsync` →
`GlyphManager.EnsureFontRangeAsync` → `IGlyphSource.FetchAsync`), checked after every await before touching
`_glyphManager`/`_atlasTexture`/`_store`;
`catch (OperationCanceledException) { CancelledBuildCount++; return; }` ahead of the generic catch — a
restyle/teardown mid-build is silent, never a warning, and never touches disposed state. The atlas-upload block
**moves out** of `BuildTileAsync` into `PumpBuilds` — the pump's single check per frame coalesces N tiles' worth
of new glyphs into one ≤16 MB `LoadRawTextureData`+`Apply`. `SetStyle` cancels + recreates the CTS and clears the
queue; `Dispose` cancels + disposes.

**Cost/latency:** labels for a burst of N fetched tiles complete over N frames instead of one — off-screen-
invisible (labels arrive seconds after tiles due to network). **Teeth:** budget (5 callbacks in one frame ⇒
1 started, 4 queued; all built after 5 pumps), coalescing (`AtlasUploadsLastPump ≤ 1`), cancellation (gate a fake
`IGlyphSource`, `SetStyle` mid-await ⇒ `CancelledBuildCount == 1`, no warning, no `ObjectDisposedException`),
stale-drop (reconcile the tile out then pump ⇒ 0 started).

*Landed note:* `PumpBuilds()` runs AFTER `ReconcileLoadedTiles` (the stale-drop needs `_loadedNow` to reflect
this frame's loaded set), and an `internal GlyphSourceFactoryOverride` seam injects a fixture glyph source for
the teeth.

### 2.2 Stage B — decode + extract off the main thread — ✅ goal met, mechanism retired (see banner)

`BuildTileAsync` restructures around explicit switch points (UniTask only):

```csharp
await UniTask.SwitchToThreadPool();
MvtTile mvt = MvtDecoder.Decode(bytes);                          // Core, engine-free
var extractedPerLayer = ExtractAllLayers(mvt, tile, layers, zoom, projection); // SymbolFeatureExtractor
await UniTask.SwitchToMainThread(ct);                            // honours cancellation on resume
// pass 1 (glyph ensure) + everything after stays on main
```

`SymbolFeatureExtractor.Extract`, the parsed `Symbol.StyleLayer` list, and `IProjection` (stateless math) are
worker-safe; none touch the glyph cache, atlas, or any `UnityEngine.Object`. `StyledSymbolTileBuilder` split into
worker-safe `ExtractLayers` + main-thread `Shape` (`BuildAsync` a byte-parity wrapper composing
`CollectRequiredRanges` → `EnsureGlyphRangesAsync` → `Shape`).

**Main-thread boundary after Stage B:**

| Work | Thread |
|---|---|
| MVT decode, symbol feature extract | pool |
| Glyph fetch await/resume, PBF decode, `GlyphCache.Store`, `GlyphAtlas.Append` (SDF blit) | **main** (`GlyphManager`'s documented contract) |
| Shape + TextQuadLayout/CurvedTextLayout | main (moves off in Stage C) |
| `SymbolTileLabelStore` mutations, `GlyphAtlasTexture.Upload` | **main** |

**Test-harness note:** `SwitchToMainThread` posts to the PlayerLoop `Update` queue, which a synchronous `[Test]`
never pumps — so the completion-dependent symbol teeth became **`[UnityTest]` coroutines that `yield return null`**
(each yield ticks the editor PlayerLoop → drains UniTask's main-thread continuations). Do NOT use
`.GetAwaiter().GetResult()` on a main-thread-hopping build in a `[Test]` — it deadlocks. **Teeth:** thread
attribution via a `ProfilerRecorder` on `MapRenderer.Symbol.TileDecode` (`CollectOnlyOnCurrentThread` on main
records zero; the cross-thread recorder ≥1), plus byte-parity of the built `LabelInstance` lists.

### 2.3 Stage C — shaping + layout off the main thread (the shared-atlas hazard) — PENDING

> *Anchor note (predates Epic A):* the goal, the shared-atlas hazard analysis, and the `GlyphResolutionSnapshot`
> design below still hold (that class is genuinely unbuilt — zero repo hits). Only the `BuildTileAsync`
> switch-point sketch predates A5a's `RunWorkerAndHandoff` / tail-pump worker structure; re-derive the entry-point
> against current source when implementing.

The hazard: pass 2 reads `GlyphCache` (via `FontStackResolver : IGlyphMetricsProvider`) and `GlyphAtlas` (via
`IGlyphAtlasView`) while the main thread may concurrently run *another* tile's pass 1 (`GlyphCache.Store` +
`GlyphAtlas.Append` — Dictionary mutation ⇒ torn reads).

**Design: immutable per-tile snapshot, no lock.** After this tile's pass 1 completes on main, every glyph it
needs is cached and appended; the atlas is FIXED-size so `Size` is a constant. Snapshot exactly the read surface
the shaper + layouts consume (both small existing interfaces):

```csharp
namespace MapRenderer.Core.Text
{
    /// <summary>An immutable, per-tile snapshot of the glyph read surface — built ON THE MAIN THREAD after
    /// the tile's pass-1 appends, then handed to a worker for shape/layout. Copies only the entries the
    /// tile's texts reference, so concurrent main-thread Append/Store for OTHER tiles can never tear a
    /// worker read. Engine-free (Core).</summary>
    public sealed class GlyphResolutionSnapshot : IGlyphMetricsProvider, IGlyphAtlasView
    {
        /// <summary>Resolve + copy every codepoint of texts against the live cache/atlas. MAIN THREAD.</summary>
        public static GlyphResolutionSnapshot Capture(FontStack fontStack, GlyphCache cache, GlyphAtlas atlas, IReadOnlyList<string> texts);
        public int2 Size { get; }                                        // fixed atlas size
        public bool TryGetEntry(uint codepoint, out GlyphAtlasEntry entry);
        // IGlyphMetricsProvider — same members FontStackResolver provides, served from the copy.
    }
}
```

Flow: main (pass 1 ensure) → main `Capture` → `SwitchToThreadPool` → `Shape`/`TextQuadLayout.Layout`/
`CurvedTextLayout.Layout` (they already take `IGlyphAtlasView`; the shaper takes `IGlyphMetricsProvider` — no
layout/shaper changes) → `SwitchToMainThread(ct)` → `_store.CompleteBuild`. `GlyphAtlas.Append` never leaves the
main thread; no lock added. Load-path allocation: one snapshot per tile build. **Teeth:** thread-attribution on a
`MapRenderer.Symbol.ShapeLayout` marker; a torn-read regression (two tiles built concurrently through a gated
fake glyph source, tile B's pass 1 appending while tile A shapes ⇒ tile A's label UVs byte-equal a serial-build
baseline; the worker lambda captures only the snapshot, so it type-cannot see the live atlas). **Unverified:**
`IGlyphMetricsProvider`'s full member list — `Capture` must mirror whatever `FontStackResolver` serves the
shaper; confirm at implementation time.

---

## 3. Cover-recompute gate (stall #6) — ✅ DONE

`Tick` today: `PumpPending` → `if (!_coverDirty && pending == 0) return;` → full recompute. `pending > 0` forces
the descent every frame for zero effect. Change (with §5's release queue in mind):

```csharp
DrainPendingDisposal();            // (engine.DrainDiscards() post-decomposition)
DrainPendingFetchDisposal();
DrainReleaseQueue(cfg.MaxReleasesPerTick);   // §5 — must run every frame, hence ABOVE the gate
int pending = PumpPending(...);
if (!_coverDirty) return;                     // pending no longer forces the recompute
CoverRecomputesLastTick = 1;
// ... descent + request/release-enqueue + cover-key commit (unchanged)
```

`pending` drops from the gate entirely — its only job was to keep Tick from early-outing *before* `PumpPending`,
and the pump now runs unconditionally above the gate. Verified no hidden dependency: the request loop keys off
`!_loaded.ContainsKey` (no-op on unchanged cover); the release loop keys off cover membership (finds nothing on
unchanged cover). **Tooth:** style a map, hold the camera still, `BlockConsumeForTests = true` so a backlog
pends; 10 Ticks ⇒ `Σ CoverRecomputesLastTick(ticks 2..10) == 0` while `pending > 0`. Control: nudge zoom by 0.01
⇒ next Tick's counter == 1.

---

## 4. Two-phase kick + mesher vertex cap (stalls #4/#5) — PENDING

> **RETRACTED 2026-09-02 — the vertex cap is not built, and stall #4 does not survive reading the code.**
> The cap existed to stop one 100k+-vertex layer uploading whole because `MaxVerticesPerTick` is checked
> before each layer. That was argued, never measured — this section said "structurally certain regardless",
> which is an assertion standing in for a number. Three facts kill it:
>
> - **A per-mesh budget already exists.** `MaxConsumesPerTick` (default 4) bounds "tile-layer meshes uploaded
>   + registered per Tick", and consume is already mesh-by-mesh, so a rich tile already spreads across frames.
> - **The main-thread cost is mostly per-mesh, not per-vertex.** `MeshDataPayload.Upload()` does `new Mesh`,
>   `ApplyAndDisposeWritableMeshData` with validation and bounds-recalculation disabled, and a bounds
>   assignment from a worker-computed AABB. Filling, bounds and index validation are already off-main.
> - **So chunking makes it worse.** Splitting a layer into four leaves the uploaded bytes unchanged,
>   quadruples the per-mesh cost, and spends the whole consume budget on one layer.
>
> The maintainer's judgement, which the code supports: mesh upload has never been a demonstrated bottleneck
> here. **If the vertex cap is ever revisited it needs a measurement first** — a marker around the per-mesh
> main-thread work over a low-zoom pan — and it should probably budget meshes rather than vertices, since
> that is where the cost is. §4.1's two-phase kick is unaffected and lands for its own reasons (exact-size
> allocation, stall #5).
>
> Recorded because the failure is instructive: a cost was assumed, a proxy was chosen to bound it
> (`MaxVerticesPerTick`), and a design was built on the proxy without checking that the cost was real or that
> the proxy tracked it.


> *Anchor note (predates Epic A):* still a live pending plan. It partially anticipated the epic — §4.2's
> `Measure(IReadOnlyList<ITileFeature> features, …)` already assumes the A6 neutral-feature surface — so re-verify
> signatures at implementation time but don't assume this section is stale wholesale; only §1.4's top-level `Kick`
> anchor actually drifted.
>
> *Second anchor note (2026-09-02):* **a second live plan now covers this same seam** —
> `docs/job-scheduling-design.md` §3.6 states which parts of this section it takes over (§4.1's state, §4.3's
> exact-size allocation, D7's `Mesh[]` cache ripple and the overshoot tooth) and which it supersedes (§4.2's
> managed `ILayerGeometry` seam, replaced by a graph builder plus a chunk-plan job). Read that section before
> implementing anything here; neither doc alone is the whole plan. Nothing has landed yet, so this section is
> still live rather than retired.

Prerequisite: capture `PmMeshDataAllocate` numbers from a liberty-style pan session first — this stage's
alloc-loop half is measure-first. The overshoot half (#4) is structurally certain regardless.

### `TileManager.PumpPending`'s paint-order seam (implemented today; moved from its method doc, UMR-118)

The work list `PumpPending` builds is sorted by the priority context before the processing loop — this is
the PAINT-order seam (as load-bearing as the admission gate): without it, a corner tile can still win the
≤N-kicks/consumes-per-Tick race purely from Dictionary enumeration order, even with priority-ordered
admission (the "middle stays white" symptom). The sort reuses the shared `_toRelease` scratch field — safe
because it is filled and fully consumed within this one single-threaded call (never live across calls),
the same discipline the "departing" vs. "unsettled" dual-use of this scratch list already relies on.

### 4.1 New `LoadedTile` state

```csharp
/// <summary>Two-phase kick, phase A: the worker measure task (decode + geometry + chunk plan into interim
/// native buffers). Set by PumpPending's kick branch; cleared when KickWrite consumes it.
/// "Measured, awaiting allocation" ⇔ HasMeasure && MeasureTask.IsCompleted && !HasMeshBuild.</summary>
public UniTask<MeasuredGeometry> MeasureTask;
public bool HasMeasure;
```

Pump state machine gains one branch, ordered before the existing consume branch:

```
ReadyBytes != null            → (kick cap) engine.KickMeasure(...)        [was: Kick]
HasMeasure && task completed  → (kick cap, SAME budget) engine.KickWrite  [NEW: the main-thread alloc]
HasMeasure && task running    → pending++
HasMeshBuild && completed     → Consume under dual budget                  [unchanged]
```

`KickWrite` is **not** charged against `MaxMeshBuildsPerTick` (job-scheduling-design.md §11 fork 2) — exact
sizing closed the blind-allocation stall the charge once answered, so the budget now bounds tiles admitted
per Tick, not this step. **Latency cost: every tile gains +1 frame** (measure completes frame N; alloc+write
kicks frame N+1) — invisible next to network fetch. Mid-flight release: `RenderTeardownRecord` stashes a
live/completed `MeasureTask` via `engine.StashDiscardMeasure` (the pen disposes `MeasuredGeometry`'s native
buffers). `MeasuredGeometry` gets a `DebugLiveCount` (leak tooth).

### 4.2 Mesher seam — `ILayerGeometry` replaces `IRenderLayer.WriteInto`

```csharp
namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>Phase-A product of one layer's mesh build: projected geometry in interim native buffers plus
    /// a chunk plan (feature-boundary splits, ≤ maxVerticesPerChunk target). Value-type data per the
    /// mesh-lifetime contract: disposed deterministically by KickWrite (after writing) or the measure pen.</summary>
    internal interface ILayerGeometry : System.IDisposable
    {
        int ChunkCount { get; }                    // 0 = empty layer
        int ChunkVertexCount(int chunk);
        /// <summary>WORKER: write one chunk into a caller-allocated MeshData (exact-size). Chunk-local
        /// indices rebased from the interim buffers' global indices (per-feature triangles never cross
        /// features, so a feature-boundary chunk is self-contained).</summary>
        void WriteChunk(int chunk, Mesh.MeshData md, out int vertexCount, out Bounds bounds);
    }

    internal interface IRenderLayer : System.IDisposable
    {
        // StyleLayer, Material, ApplyZoom unchanged.
        /// <summary>WORKER: decode-to-geometry + chunk plan. Replaces WriteInto (which fused measure and
        /// write and therefore forced the main thread to pre-allocate blind).</summary>
        ILayerGeometry Measure(IReadOnlyList<ITileFeature> features, double zoom, double extent, TileId id,
            double3 tileOriginRender, IProjection projection, int maxVerticesPerChunk);
    }
}
```

`StyledFillTileBuilder`/`StyledLineTileBuilder` split: the decode→assemble→earcut→project front half becomes the
`Measure` body — the interim buffers are **retained** in the returned geometry object instead of disposed in a
`finally`; the stream-write back half becomes `WriteChunk` (same code, parameterised by a vertex/index window,
indices written as `global − chunkVertexStart`). Chunk planning walks `VertexFeatureIdx` (per-feature vertex runs
are contiguous) accumulating features until the next would cross `maxVerticesPerChunk` (32 768 target, D6). **A
single feature larger than the target ships as one oversized chunk** — splitting inside a feature means
re-triangulation; the overshoot bound becomes "one *feature*" instead of "one *layer*" (a low-zoom ocean *layer*
is many polygons; a single monster polygon remains a known residual). `MeasuredGeometry` = the per-tile
aggregate: `ILayerGeometry[]` (dense order) + the capture set + the chunk→(layer, chunkIndex) flat plan.

### 4.3 Allocation + consume + cache

`KickWrite` (main): for each **non-empty** chunk *(read "layer" — chunking is retracted, see this section's
head)*, `MeshDataPayload.AllocateTracked(1)` under `PmMeshDataAllocate`
— exact counts are known, so the "dozens of 0-vertex arrays allocated, carried, disposed unused" loop (#5) is
gone. Per-chunk single-element arrays are kept deliberately (`ApplyAndDisposeWritableMeshData` applies a whole
array at once, and the resumable per-mesh consume requires applying one mesh at a time).

Consume/backends: **no changes needed.** Payloads carry `MaterialIndex`; `ConsumeMeshBuild` walks a dense payload
array of arbitrary length via `ConsumeCursor`; `AddTileLayer` has no per-(tile, layer) uniqueness assumption in
any backend (Entities: entity per call; BRG: `DrawItem` per call; GameObjects: GO per call). Two payloads sharing
a `MaterialIndex` produce two draw items with the same material — correct order preserved because chunks emit in
dense/draw order and backends order by material renderQueue.

**One real cache ripple:** `PreparedTileCache.Put` destroys the previous entry on key collision — with K chunks
per (style, tile, layerId), the release-path transfer would destroy chunks 1..K−1. **D7:** the cache value
becomes `Mesh[]` (`null` array stays the empty-layer marker); `TransferOnRelease` groups the record's tracked
meshes by `MaterialIndices[i]` before `Put`; `TakeAsLoadedTile` registers each chunk of a taken array.

> **Dead with the retraction (2026-09-02).** D7 above exists only because a layer could become K > 1 meshes.
> One mesh per layer keeps the current `Mesh`-valued entry correct, so **the cache is not changed at all** —
> there is no ripple. Recorded in `job-scheduling-design.md` §3.6 as consequence (1).

**Teeth:** overshoot bound (≥100k-vert fixture across ≥4 features, `MaxVerticesPerTick = 40 000` ⇒ per-tick
`VerticesConsumedLastTick ≤ 40 000 + 32 768` and the layer produces ≥3 meshes); allocation exactness (a new
`MeshDataArraysAllocatedLastKick` counter < D dense layers on the liberty fixture); leak (release in the
measured-awaiting-alloc state ⇒ `DebugLiveCount` back to 0); cache round-trip (build→release→revisit vertex sum
equals the original; a `Mesh`-valued cache loses chunks); parity (full GPU-snapshot suite unchanged).

> **Which of those teeth survive (2026-09-02).** The overshoot bound and the cache round-trip are **struck**:
> both assert K > 1 meshes per layer, which the retraction makes impossible — a tooth nothing can satisfy is
> worse than no tooth, because it reads as coverage. Allocation exactness, the leak tooth and parity survive
> and are adopted by `job-scheduling-design.md` stage 2, restated there per *layer*.

---

## 5. Release-path budgeting (stall #2) — ✅ DONE

### 5.1 Deferred-release queue in `TileManager`

```csharp
/// <summary>Per-frame budget of (tile, source) RECORDS fully released per Tick. 0 = uncapped. Default 4.</summary>
public int MaxReleasesPerTick;

private readonly Queue<LoadedKey>   _releaseQueue  = new Queue<LoadedKey>(64);
private readonly HashSet<LoadedKey> _releaseQueued = new HashSet<LoadedKey>();  // dedup across recomputes
```

- Tick's release loop enqueues instead of releasing: `if (_releaseQueued.Add(key)) _releaseQueue.Enqueue(key);`.
- `DrainReleaseQueue(int budget)` runs **every frame above the cover gate** (§3): dequeue up to `budget` keys;
  for each — remove from `_releaseQueued`; **re-validate**: if `_coverSet.Contains(key.Tile)` (the tile came back
  during the 1–3 frame linger) or `!_loaded.ContainsKey(key)` (restyle cleared it), skip; else `ReleaseTile(key)`
  (unchanged body). Re-validation converts fast pan-out-pan-back from destroy+refetch churn into a free no-op.
- Records queued for release stay in `_loaded` and are still pumped for 1–3 frames; the kick branch skips
  `_releaseQueued.Contains(key)` records (don't start a build for a condemned tile). In-flight work proceeds to
  the existing pens. `SetSources` clears both structures. Zero-GC: both containers pre-sized; the steady state
  never touches them.

**`TileManager.ReleaseTile`'s cache-transfer scoping** (moved from its inline comment, UMR-118): a fully-Built
tile's mesh transfer into `PreparedTileCache` is scoped to THIS method (a genuine eviction) — deliberately NOT
folded into `RenderTeardownRecord`, which `SetSources` ALSO calls for every record on a restyle (rebuilt
backend + re-indexed `RenderLayerSet`). Caching those meshes there would file them under whatever
`CurrentStyle` happens to be at that moment, risking a later hit under a coincidentally-matching `(tileId,
layerId)` serving stale-style geometry; sidestepped entirely by never transferring on that path — a restyle
keeps the always-destroy behaviour unchanged. `_cacheEnabled == false` skips the transfer entirely —
`RenderTeardownRecord`'s `DestroyTrackedMeshes` then destroys `lt.Meshes` exactly as it did pre-cache.

### 5.2 Batched removal — backend seam

```csharp
internal interface ITileRenderBackend : IDisposable
{
    int  AddTileLayer(Mesh mesh, double3 tileOriginRender, int materialIndex, TileId tileId);
    void RemoveItem(int handle);
    /// <summary>Removes many draw items in ONE backend operation where supported — Entities destroys all
    /// layer entities (plus any tile roots emptied by the batch) via a single
    /// EntityManager.DestroyEntity(NativeArray&lt;Entity&gt;) structural change. BRG/GameObjects default to a
    /// RemoveItem loop. Idempotent for unknown handles.</summary>
    void RemoveItems(ReadOnlySpan<int> handles);
    void Rebuild(in SceneFrame frame);
    Bounds ComputeSceneBounds(float tileSizeWorld);
}
```

`RenderTeardownRecord` switches its unregister loop to `_instanced.RemoveItems(lt.DrawHandles)`. Entities: reuse
a persistent `NativeList<Entity> _destroyScratch`; append each handle's entity, run the root bookkeeping
(decrement `ChildCount`, append emptied roots), then one `_em.DestroyEntity(_destroyScratch.AsArray())`. One
structural change per released record (≤`MaxReleasesPerTick` per frame) instead of L+1 per record. With R=4 and
L=30, the worst frame goes from 450 structural changes to ≤4. **Teeth:** budget (8 tiles leave cover,
`MaxReleasesPerTick = 2` ⇒ Tick 1 releases 2, queue depth 6; queue empties in 4 ticks, `CountMeshObjects` back to
baseline); batching (`DestroyEntityBatchesLastRemove` == 1 for a 10-layer tile, `EntitiesDestroyedLastRemove` ==
11; a `RemoveItems`-loops-`RemoveItem` impl yields batches == 0); pan-return (tile returns before dequeue ⇒
record survives, `TileBuildsStartedLastTick == 0`).

---

## 6. Entities `AddTileLayer` redesign (stall #3) — ✅ DONE

**Decision (D10): fix Entities and keep it the ship default; BRG stays the opt-in.** The fix is small and uses
EG's own intended fast path (ID-based `MaterialMeshInfo` exists precisely to bypass per-entity `RenderMeshArray`
registration), preserves the Entities-Hierarchy debuggability that is the backend's documented reason to exist,
and shipping BRG as default would first require fixing BRG's own steady-state per-frame repack (#8). Maintainer
accepted; BRG-as-default is not pursued now (maintainer notes lean BRG long-term — revisit if #8 is fixed).

```csharp
private EntitiesGraphicsSystem _eg;               // GetExistingSystemManaged<EntitiesGraphicsSystem>()
private BatchMaterialID[]      _materialIds;      // RegisterMaterial per layer material, once
private Entity                 _layerPrototype;   // built ONCE via RenderMeshUtility.AddComponents with a
                                                  // placeholder RenderMeshArray + Parent/LocalTransform,
                                                  // Prefab-tagged so it never renders itself
// ItemRec gains:  public BatchMeshID MeshId;     // for unregister-on-remove

public int AddTileLayer(Mesh mesh, double3 tileOriginRender, int materialIndex, TileId tileId)
{
    Entity e = _em.Instantiate(_layerPrototype);   // ONE structural op (preserves the prototype archetype)
    BatchMeshID meshId = _eg.RegisterMesh(mesh);
    _em.SetComponentData(e, new MaterialMeshInfo(_materialIds[materialIndex], meshId)); // ctor (materialID, meshID) — the FromMeshIDAndMaterialID factory isn't in this EG version
    _em.SetComponentData(e, new Parent { Value = GetOrCreateRoot(tileId, tileOriginRender) });
    // LocalTransform.Identity, LocalToWorld, RenderBounds sets as today (sets, not migrations).
}
```

- `RemoveItems`/`RemoveItem` gain `_eg.UnregisterMesh(item.MeshId)` per destroyed item (without it EG's mesh
  registry grows unboundedly and holds destroyed meshes' IDs). Materials unregister in `DoDispose` only if the
  world is still alive (world disposal tears down EG's registries wholesale — for symmetry, not correctness).
- Structural cost per consumed mesh: today 2 structural changes + a fresh one-element `RenderMeshArray`; after: 1
  `Instantiate` + `RegisterMesh` (a registry add, not an archetype change). At `MaxConsumesPerTick = 4`: ≤4
  instantiates/frame. Root creation path unchanged (roots are rare — one per tile).

**Risk / unverified:** the exact component set `RenderMeshUtility.AddComponents` stamps varies by EG version; the
placeholder-`RenderMeshArray`-on-prototype inertness under ID-based `MaterialMeshInfo` must be validated in-project
(fall back to a shared dummy array if EG asserts). **Teeth:** no per-entity array (`RenderMeshArraysCreated` ≤1
after N calls); registry balance (`RegisteredMeshCount == 0` after a full load→release cycle — catches a missing
unregister); parity (GPU-snapshot byte-identical + Entities Hierarchy probes unchanged).

---

## 7. Ordered plan (each step independently shippable, gate = EditMode suite green)

| Step | Content | Section | Depends on | Risk |
|---|---|---|---|---|
| 1 ✅ DONE | Symbol Stage A: queue + ≤1 build/frame + upload coalescing + CTS | §2.1 | — | low (scheduling; attacks the worst stall) |
| 2 ✅ DONE | Symbol Stage B: decode+extract to pool | §2.2 | 1 | low |
| 3 ✅ DONE | Cover gate on `_coverDirty` alone + tooth | §3 | — | low (one line; suites arbitrate) |
| 4 ✅ DONE | Release queue + `MaxReleasesPerTick` + `RemoveItems` seam + Entities batched destroy | §5 | 3 | medium |
| 5 ✅ DONE | Entities `AddTileLayer` prototype/ID redesign + unregister-on-remove | §6 | — | medium (EG version detail) |
| 6 | **Measure**: GC pan-storm profile (#7) + `PmMeshDataAllocate` capture — Editor session, no code | — | — | none (gates 9's alloc-half; do nothing on #7 until confirmed) |
| 7 | Decompose M1+M2 (`LoadedTile` de-nest; `SourcePipelineRegistry` + dense-id cache) | §1.1–1.2 | — | low |
| 8 | Decompose M3+M4 (`PreparedTileBridge`; `TileMeshBuildEngine` + test-surface exit + D12 + comment prune) | §1.3–1.6 | 7 | medium (large mechanical move; leak suites are the net) |
| 9 | Two-phase kick + `ILayerGeometry` chunking + `Mesh[]` cache values | §4 | 8, 6 (numbers) | high (mesher change; parity suite is the net) |
| 10 | Symbol Stage C: snapshot + shaping off-thread | §2.3 | 2 | medium (the documented atlas hazard — snapshot defuses it structurally) |

Steps 1–5 are independent of the decomposition and deliberately front-loaded; 7–8 must precede 9 so the two-phase
kick lands in `TileMeshBuildEngine`, not in another 200 lines of the god-object. Step 10 can run any time after 2.

## 8. Resolved decisions (maintainer)

All accepted, decision-complete: (1) **fix Entities, keep it default** (§6, D10) — BRG stays the zero-alloc
opt-in; (2) **budget defaults** — `MaxReleasesPerTick` = 4, symbol `MaxBuildsPerFrame` = 1, both Inspector-
serialized; (3) **chunk vertex target** = 32 768 (serialized constant); (4) **PreparedTileCache byte budget** —
keep the S82 placeholder (D7 only changes the value shape; VRAM profiling is a later tuning pass); (5) **GC-stall
hypothesis (#7)** — measure first (step 6); enable incremental GC only if the trace confirms collections landing
in the load window; no decode-buffer pooling otherwise.

## 9. Honest costs, risks, and what was not verified

- **Latency:** +1 frame per tile from the two-phase kick (§4.1); labels a few frames later (§2.1); released tiles
  linger 1–3 frames (§5.1). All judged invisible; all stated.
- **More, smaller meshes** after chunking: more draw items for monster layers (bounded by size / 32k); BRG
  per-frame repack cost scales with item count — another nudge toward keep-Entities-default.
- **Not verified:** `IGlyphMetricsProvider`'s full member list (§2.3); the exact EG component set / placeholder-
  `RenderMeshArray` inertness for the prototype pattern (§6); whether any test depends on `MaxConsumesPerTick == 0`-
  blocks semantics beyond backlog-building (D12 migration must sweep those); the cover gate's no-hidden-dependency
  claim is argued from code reading — the cover/consume suites are the arbiter (§3).
- **Load-path allocations introduced** (allowed, listed): symbol build queue entries + per-tile glyph snapshot
  (§2), `MeasuredGeometry`/chunk plans (§4), cache-transfer `Mesh[]` grouping (§4.3), release-queue warm-up
  (§5.1). Steady-state zero-GC is untouched by design; `MapView_SteadyStateTick_DoesNotAllocateGCMemory` gates
  all of them.
