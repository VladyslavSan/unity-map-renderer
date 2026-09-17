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
| 6 | **Cover recompute every frame while anything pends** | Static camera + pending tiles → full `SelectVisibleTiles` descent + `_coverSet` rebuild + cover×pipeline probes every frame **for zero effect** (~0.5–2 ms/frame during exactly the frames already under consume load). | Gate the recompute on the cover-key gate alone (shipped as `CoverKeyGate`, UMR-112) | §3 |
| 7 | **GC collections from off-thread load work** | Per-tile `byte[]`, `MvtDecoder.Decode` object graphs on the pool, payload/closure allocs per kick — Mono GC stops the main thread regardless of which thread allocated. Hypothesis, not measured. | Profile with GC markers during a pan storm; enable incremental GC if confirmed; pool decode buffers | measure-first |
| 8 | **BRG `Rebuild` full per-frame repack** (opt-in path) | Per frame: sort all items, repack 76 floats × N, 33 material-property reads × N, full `SetData`. Steady cost, not a spike. | Dirty-flag material props; repack transforms only on frame change; partial `SetData` | only if BRG becomes default |
| 9 | **Restyle hitch** | Full teardown + Entities `World` rebuild + every cached mesh destroyed, one frame. Big but rare, user-initiated. | Accept; documented tradeoff | — |

Classification: #1, #2, #6 are **scheduling-only**; #3, #8 are **backend-only**; #4 (+#5) needs the **meshers**;
#7 is measurement-first.

### Decision summary

| # | Topic | Decision |
|---|---|---|
| D1 | Decomposition order | `SourcePipelineRegistry` → `PreparedTileBridge` → `TileMeshBuildEngine`; test surface exits to the test assembly in the engine step. **UMR-112 shipped only the first step, narrower**: `SourceRegistry` (§1.2 below), never given an indexer — see that section's note |
| D2 | Dense layer-id cache | Lives on `SourcePipeline` (computed once in `ApplySpecs`), **not** in the bridge — the kick, the probe, and the transfer all read the same `int[]`, so the three-consumer agreement invariant becomes structural. **Not built** — UMR-112's `SourceRegistry` does not carry a dense layer-id cache; `ComputeDenseLayerIds` stays on `TileManager` unchanged |
| D3 | Symbol budgeting | Queue in `SymbolSubsystem` + explicit `PumpBuilds()` from `MapView.LateUpdate`; ≤1 build start/frame (configurable); atlas upload moves out of `TryBeginBuild` into the pump (≤1 upload/frame by construction) |
| D4 | Symbol threading | Stage 1: decode+extract on the pool. Stage 2: shape+layout on the pool against an immutable per-tile snapshot (`IGlyphMetricsProvider` + `IGlyphAtlasView`) built on main **after** pass-1 appends. Atlas/cache **writes never leave the main thread**; no lock |
| D5 | Two-phase kick | Measure task (worker, geometry into interim `TileMeshBuffers`) → main-thread exact-count `AllocateWritableMeshData(1)` per **non-empty chunk** → write task (worker). One `MeshDataArray` per chunk (a whole-array alloc conflicts with per-mesh resumable consume) |
| D6 | Mesher chunking | New `ILayerGeometry` seam (measure/write-chunk) replaces `IRenderLayer.WriteInto`; split at feature boundaries, target 32 768 verts/chunk; a single over-budget feature ships as one oversized chunk (no re-triangulation) |
| D7 | PreparedTileCache value | `Mesh` → `Mesh[]` per (style, tile, layerId) so a chunked layer round-trips the cache without the Put-collision destroying earlier chunks; `null` empty-layer marker unchanged |
| D8 | Release budgeting | Deferred-release queue drained ≤`MaxReleasesPerTick` records/frame; re-validated against the live cover at dequeue (a tile that came back is un-queued, not destroyed) |
| D9 | Batched removal | New `ITileRenderBackend.RemoveItems(ReadOnlySpan<int>)`; Entities implements it as **one** `EntityManager.DestroyEntity(NativeArray<Entity>)` per call (layers + emptied roots together) |
| D10 | Entities vs BRG | **Fix Entities, keep it the ship default.** Prototype-entity + `Instantiate` + `RegisterMesh`/`RegisterMaterial` + ID-based `MaterialMeshInfo`, with unregister-on-remove. BRG stays the zero-alloc opt-in. (maintainer-accepted, §8) |
| D11 | Cover gate | Recompute gated on the cover-key gate alone (shipped as `CoverKeyGate`, UMR-112); release-queue drain moves above the gate so it runs every frame |
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

**Landed as `SourceRegistry` (UMR-112), narrower than this sketch.** `SourceKey`/`SourceSpec` stayed nested
on `TileManager` (25 external call sites reference `TileManager.SourceSpec`, moving zero state — not worth
the churn). `ComputeDenseLayerIds` and D2's cached array were not built; `SourceIdOf`/`InFlightCount` moved
as planned. The sketch below is UNBUILT design history — read it for the diff/teardown shape, not the
current API.

**UMR-112 did not ship the indexer.** `public SourcePipeline this[int slot]`
hands every caller the full pipeline — including the `ITileFeatureSource` and the mutable `MinZoom`/`MaxZoom`
fields no caller should touch directly — so every future need ("just the source-id", "is this slot
sourceless?") is satisfied by property-punching the returned object instead of adding an intention-revealing
method. `SourceRegistry` exposes ten narrow, slot-keyed operations (`SourceIdOf`, `IsSourceless`,
`AdmitsZoom`, `SourceAt`, `ReleaseTile`, …) and never returns `SourcePipeline` at all — a structural test
(`TileProcessingStructureTests.SourceRegistry_SurfaceIsExactlyTenMembers`, UMR-112) pins the ten-member
bound so a future "just add a getter" change fails loudly instead of silently reopening the indexer shape.

Moves out of `TileManager`: `SourcePipeline` (class), the diff/keep/teardown body of `SetSources` step 2,
`FindPipeline`, `DisposePipelines`, `SourceIdOf`, `InFlightCount`.

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
        // REJECTED at ship time (UMR-112) — `public SourcePipeline this[int slot] { get; }` sketched here
        // originally. See the note above: ten narrow methods shipped instead; SourcePipeline never escapes.

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
`DoDispose`. **This engine was never built.** UMR-112 extracted only the three holding pens (this section's
`_pendingDisposal` plus the graph and fetch pens) into `PendingDisposalQueue` — `DrainPendingDisposal` and
`DrainPendingFetchDisposal` merged into one `DrainCompleted()`; the kick/consume machinery below stayed on
`TileManager`, unmoved. This is where every future consume-path fix (two-phase kick, mesher split) lands without touching
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
  **Not built.** UMR-112 shipped the registry (§1.2) without a dense layer-id cache or an indexer —
  `ComputeDenseLayerIds` stayed put, and slot-keyed access goes through named methods
  (`SourceIdOf(slot)`, `IsSourceless(slot)`, …), never `_registry[slot].AnyField`.
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
exactly once per load (never re-baked while `LoadedTile.Built`). `CameraProperties` is not read for this
purpose; the call sites take it only for other reasons.

**This is the bake-parameter SSOT.** The prepared artifact is a pure function of the closed set:

> `{ Tile, Zoom = id.Z, origin = f(tile, projection), projection, bufferClip }` + the typed paint/layout
> (i.e. the style content **and** `MapViewConfig.FillAntialiasing`) + the built layer numbering.

The rule that licenses `PreparedTileCache` holding no purge of its own: **a new bake input either enters
the cache token or gets its own purge.** Today's inputs split across the two mechanisms as follows —
content, `FillAntialiasing` and the built layer numbering are folded into `TileManager.CurrentStyle`'s
`StyleToken` (`MapView.SetStyle`, keyed via `JsonCanonical.Digest`); the clip window has its own
diff-and-purge in `TileManager.TickCore` (a changed `BufferClip` clears `_prepared` directly); `Zoom = id.Z`
and `projection` are session-constant, so neither needs a token component or a purge.

**Why the layer-numbering fold is per-index, not a plain count.** `MapView.LayerNumbering` folds each
`(li, StyleLayer.Id)` pair, not `RenderLayerSet.Count`.

`MapMaterialSet.Validate()` enforces `FillMaterial`, `LineMaterial` and `SymbolTextWorld`.
`FillExtrusionMaterial` is the one material whose absence makes a layer lose its SLOT:
`FillExtrusionRenderLayer.TryCreate` returns null and `RenderLayerSet.Build` skips it. Symbol and background
always take their declared slot whatever their material config — `RenderLayerFactory` routes them through a
`Create` that never returns null, unlike the `TryCreate` the mesh kinds use — so the layers above them keep
their numbering (`LayerSkipReason.MaterialUnconfigured` records that this reason is reachable only through
`FillExtrusionMaterial` on the `SetStyle` path). That gives a `MaterialSet` mutation exactly one degree of
freedom — it can change the built layer COUNT but never PERMUTE at a fixed count, since every other skip
reason is content-driven and content already sits in the digest. A count-only fold is therefore
behaviourally equivalent TODAY, but the equivalence rests on a property of today's `Validate()`, not a
declared invariant: **a second SLOT-DROPPING material field — of ANY layer kind, mesh or not, since dense ids
index the full layer list, so a non-mesh slot vanishing shifts every mesh layer after it —** would make
permutation reachable, and a count fold would then serve one layer's mesh under another's material,
silently. The per-index fold costs one `StringBuilder` per style load, on a path that already awaits — not
on any measured budget.

### 1.10 Partial-survival restyle (UMR-151, style-transitions Stage 3) — slot vs draw order, the tombstone, the three exits

Before this stage `RenderLayerSet`'s list index was four things at once: declared order, draw order,
material index, and backend slot. A restyle that reordered or removed even one layer had to collapse all
four back to a fresh count-0..N-1 sequence, so it took the full rebuild — tearing down every tile's mesh
for every layer, not just the one that actually changed.

**E1 null placeholder (pre-existing, restated here since the per-backend doc blocks that used to carry it
were trimmed).** A symbol/background slot whose base material is unconfigured stores a `null` entry in each
backend's full-width material list — a placeholder, NOT registered with the engine — so the list stays
full-width aligned; `AddTileLayer` is never called for that index either way.

**Slot vs draw order.** `IRenderLayer.DrawIndex` is now purely the **slot** — the backend `materialIndex`,
the `LoadedTile.MaterialIndices` entry, `PreparedKey`'s layer id — set once by `RenderLayerSet.Build` and
STABLE across a restyle for a surviving layer. **Draw order** is a separate quantity, the layer's position
in the CURRENT document's declared `layers` array, written only into `Material.renderQueue` via
`IRenderLayer.SetDrawOrder` (`LayerDrawOrder.QueueFor`). A fresh `Build` is the only place the two coincide
(no survivors yet); a reorder moves draw order without moving any slot.

**The tombstone.** A layer's removed slot becomes a `TombstoneRenderLayer` — material-less, mesh-less,
implementing neither `ITileMeshRenderLayer` nor `ISpriteConsumerRenderLayer` — never `null`. Every site that
already tolerated a null `Material` (dense-layer-id computation, sprite push, `LayerMaterials`) skips it for
free; the handful of sites that would NRE on a raw null (`ApplyZoom`, `TransitioningCount`, `ClearLayers`,
`TileManager.LayerNames`/`LayerShadowModes`) needed no edit either. Exactly ONE site did need one:
`TileManager.ConsumeMeshBuild`'s guard over an already-kicked mesh build, whose payload carries the slot it
was built for. Because the width never shrinks, its `materialIndex < Count` check cannot see a retirement, so
it tests for `TombstoneRenderLayer` itself. `ITileMeshRenderLayer` is NOT the right predicate there — a
background layer implements neither interface yet does register a per-tile quad, so that test would drop
every background payload.

**`RenderLayerSet.TryRestyleInPlace`'s three exits**, an ID-keyed diff (not the old by-reference, same-index
walk, which can't express removal or reorder at all): walk the new document's layers in order, look each
one's id up among the OLD document's RENDERED layers —

The old side is keyed TWICE, and both maps are needed. `_layers` holds only the layers `Build` could render,
so it is SHORTER than `oldStyle.Layers` whenever any layer was skipped (an unsupported kind, an unconfigured
material — 1 of `liberty.json`'s 111 is a `raster`): the slot map is built over `_layers`, and a rendered
layer whose `StyleLayer.Id` is null refuses the whole restyle rather than risk pairing the wrong layer. The
second map is over `oldStyle.Layers` and exists only so an unchanged NEVER-RENDERED layer can be told apart
from a genuine addition at exit 1 — without it, `liberty.json`'s raster layer would pin every liberty
restyle to a full rebuild, which the whole-document gate it replaces did not do either.

1. **not found, and not an unchanged never-rendered layer either** (a genuinely ADDED layer, or one whose
   never-rendered — e.g. `raster` — content changed) → refuse the whole restyle; falls through to the full
   rebuild above (S4's territory — per-layer mesh build onto an already-loaded record — is what would let
   an ADD survive in place, and is explicitly out of this stage's scope).
2. **found, but its mesh-affecting signature changed** (`SurvivingLayerGate.LayerSurvives`) → refuse, same
   fallthrough.
3. **found and survives** → keep its slot, `Restyle` its uniform bindings, `SetDrawOrder` it to its new
   declared position.

Any old slot not claimed by exit 3 is a removal — tombstoned in a SECOND pass, after every layer has been
classified, so a mid-walk refusal leaves `_layers` completely unmodified (the method's own contract).

**The symbol-removal fence** (§6's deferred fence). A REMOVED layer never reaches `LayerSurvives` at all —
the classification walk only visits layers present in the NEW document — so a fourth refusal runs over the
unclaimed slots, STILL IN THE READ-ONLY PASS (a check where removal first becomes visible, in the mutation
pass, would run after the first `Dispose()` and break the unmodified-on-refusal contract). Its predicate is
*"this slot's render layer is referenced by a list the in-place arm does not refresh"*:
`MapView._symbolRenderLayers` is the only field outside `RenderLayerSet` that holds render-layer references
— every other site threads them as a parameter — so `SymbolRenderLayer` is the only kind that qualifies
today, and a second such field is what would change the answer. Without the fence the removed symbol slot
is disposed while `MapView.SetStyle`'s in-place arm returns without refreshing that list, and
`SymbolPlacementSystem.Tick` then resolves materials `SymbolRenderLayer.Dispose` has destroyed. A removed
symbol layer takes the full rebuild instead; `UMR-152` is what would let it survive in place.
REORDER is deliberately NOT fenced: that arm skips the `_symbolRenderLayers` rebuild and
`SymbolSubsystem.SetStyle` TOGETHER, so the list and the subsystem's slot numbering stay mutually
consistent, and removal is the only class that mutates a slot.

**The record-keep fence.** `TileManager.SetSources` (the full-rebuild entry point) is UNCHANGED — it still
tears down every loaded record unconditionally, because a full rebuild replaces every render layer's
Material and nothing else re-derives which already-loaded tile needs a fresh mesh against the new set. The
partial-survival arm instead calls a separate, narrower entry point, `TileManager.RestyleSourcesInPlace`,
which diffs the SOURCE-pipeline registry (`SourceRegistry.Rebuild`'s old-slot→new-slot map) and tears down
only a record whose pipeline actually departed — everything else is re-keyed to its new slot, not rebuilt.
`SourceRegistry.Rebuild` reuses the SYNTHETIC background pipeline's identity across the diff for the same
reason it reuses a real one's: the background pipeline holds no resource, but if a fresh instance were
minted each call, its old slot would read as departed on every restyle that has a background, tearing down
every record parked there for no actual change.
It also always pushes the current per-layer material/shadow lists into the backend
(`ITileRenderBackend.SetLayerMaterials`), even for a pure reorder with no source change at all, because
that is the only place the BRG backend's cached per-item render queue gets re-stamped (§3 below).

**`MapView.SetStyle`'s in-place branch.** `TileManager.SourcesUnchanged(specs)` is evaluated BEFORE
`Layers.TryRestyleInPlace` — the latter re-binds every surviving layer's applier (and, since this stage,
retires a removed layer's slot), so there is no reason to pay it when the source set alone already
disqualifies the restyle. That pre-diff answer is allowed to go STALE, because it only gates ENTRY to the
arm: the one thing the layer diff can change — a background layer going away, which retires the synthetic
source-less pipeline — is re-read INSIDE by `SourceRegistry.Rebuild(specs, HasBackgroundLayer())` after the
diff, so `TeardownRecordsOnDepartedPipelines` still tears down exactly that record (T5 pins it), and
evaluating it twice would buy nothing. Once inside the branch, `Layers.Build`, `TileManager.CurrentStyle`,
`SymbolSubsystem.SetStyle`, `LogSkippedLayers` and `_symbolStyleLayers`/`_symbolRenderLayers` stay skipped
because none of them has anything to redo: `Layers.Build` would discard and rebuild every layer, which is
exactly what the in-place arm exists to avoid; `TileManager.CurrentStyle`'s token folds in the layer
numbering, which an in-place restyle never moves (a removal only tombstones a slot, it never shifts one);
`SymbolSubsystem.SetStyle` rebuilds label state for a changed symbol set, but a removed SYMBOL layer is
refused by `TryRestyleInPlace`'s own removal fence above, before reaching here, so no symbol layer this
branch reaches has been removed; and `LogSkippedLayers` would just re-log a compatibility summary that
`Build` never re-populated, so it stays whatever the last full rebuild logged. This stage's only correction
is that a removed layer leaves the token's numbering fold unchanged too (its vacated slot's cache entries
are unreachable, evicted by budget — `UMR-141`'s territory). `TileManager.SetSources` (the
full-rebuild one) is unreachable from this branch; `RestyleSourcesInPlace` is its counterpart, called
UNCONDITIONALLY here — not gated to "only when something removed" — because it is the only place the BRG
backend's cached per-item render queue gets re-stamped, even for a pure reorder with no source change.

**No per-frame cost.** `SetLayerMaterials` re-stamps `BRG.TileRenderer.DrawItem.LayerRenderQueue` once, at
restyle time — never a live `material.renderQueue` read inside the per-frame culling callback. A slot whose
material went from a live one to `null` (a retired layer) has its live draw items removed right there too,
in all three backends: nothing in the source-pipeline diff tears these down on its own, since a layer
removal that doesn't touch any source pipeline leaves `TeardownRecordsOnDepartedPipelines` with nothing to
tear down.

The THREE BACKENDS DO NOT DO THE SAME WORK here, and are deliberately not described as if they did: BRG
only drops the `DrawItem` dict entry (no per-item mesh unregister exists in that backend at all); the
GameObjects backend parks the child in its pool, similarly no engine-side unregister; the Entities backend
additionally calls `_eg.UnregisterMesh` and decrements `RegisteredMeshCount` IMMEDIATELY, ahead of the
normal tile-release timing that count otherwise tracks (`EntitiesTileRendererTestExtensions`'
`RegisteredMeshCount` teeth, and the PlayMode `PreparedCacheTests` leak tooth, both read it). In every
backend the tile's `Mesh` ASSET itself is untouched by this cleanup — `TileManager` still owns it and
destroys it at ordinary tile release, so the record briefly keeps a `Mesh` no backend draws, a bounded hold
rather than a growing leak (`UMR-141`'s territory) — but how EARLY each backend's own bookkeeping reflects
the retirement differs, and a reader comparing the three should expect that.

**`SetLayerMaterials`'s two implementation traps.** (1) A retiring slot's OLD material must be compared by
`ReferenceEquals(oldMat, null)`, never `oldMat != null` — Unity's overloaded `==`/`!=` reports a DESTROYED
object as fake-null, and the old material IS already destroyed by the time this runs
(`RenderLayerSet.TryRestyleInPlace` disposes a removed layer before `TileManager.RestyleSourcesInPlace`
reaches the backend). `oldMat != null` would silently skip the unregister in EditMode (`DestroyImmediate`
makes the fake-null show up immediately) while still running it in a shipped player (`Destroy` defers to
frame end) — the two environments taking OPPOSITE branches, so an EditMode tooth over a registration count
would measure a path production never takes. (2) A caller MUST call every survivor's
`IRenderLayer.SetDrawOrder` before calling `SetLayerMaterials` — the BRG backend's render-queue re-stamp
reads each slot's material AS IT IS at that moment, so calling them in the other order re-stamps the OLD
queue.

**`MapView.CommitProbe`** is a test-only seam over `SetStyle`'s FULL-REBUILD arm — an `Action<CommitPhase>`,
null in production, invoked after each of six mutation sites in commit order: the `_style`/`StyleId`
identity commit, the `_committedFillAntialiasing`/`_committedMaterials` memo write, `RenderLayerSet.Build`,
the `TileManager.CurrentStyle` token write, `SymbolSubsystem.SetStyle`, and (the one INTERIOR site — the
only phase that can fire more than once per call) inside `TileManager.SetSources`'s teardown loop, once per
record. A test sets it to throw at a chosen phase to observe what an interrupted rebuild leaves behind.

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
unchanged cover). **UMR-112 renames** (illustrative pseudocode above kept as-shipped-then): `_coverDirty` is
`CoverKeyGate.IsDirty`; `DrainPendingDisposal`/`DrainPendingFetchDisposal` merged into one call,
`PendingDisposalQueue.DrainCompleted()`. **Tooth:** style a map, hold the camera still, `BlockConsumeForTests = true` so a backlog
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
| 3 ✅ DONE | Cover gate on the cover-key gate alone (shipped as `CoverKeyGate`, UMR-112) + tooth | §3 | — | low (one line; suites arbitrate) |
| 4 ✅ DONE | Release queue + `MaxReleasesPerTick` + `RemoveItems` seam + Entities batched destroy | §5 | 3 | medium |
| 5 ✅ DONE | Entities `AddTileLayer` prototype/ID redesign + unregister-on-remove | §6 | — | medium (EG version detail) |
| 6 | **Measure**: GC pan-storm profile (#7) + `PmMeshDataAllocate` capture — Editor session, no code | — | — | none (gates 9's alloc-half; do nothing on #7 until confirmed) |
| 7 ✅ PARTIAL | Decompose M1+M2 (`LoadedTile` de-nest; `SourcePipelineRegistry` + dense-id cache) — UMR-112 shipped the registry half as `SourceRegistry` (§1.2), without the dense-id cache | §1.1–1.2 | — | low |
| 8 | Decompose M3+M4 (`PreparedTileBridge`; `TileMeshBuildEngine` + test-surface exit + D12 + comment prune) | §1.3–1.6 | 7 | medium (large mechanical move; leak suites are the net) |
| 9 | Two-phase kick + `ILayerGeometry` chunking + `Mesh[]` cache values | §4 | 8, 6 (numbers) | high (mesher change; parity suite is the net) |
| 10 | Symbol Stage C: snapshot + shaping off-thread | §2.3 | 2 | medium (the documented atlas hazard — snapshot defuses it structurally) |

Steps 1–5 are independent of the decomposition and deliberately front-loaded; 7–8 must precede 9 so the two-phase
kick lands in `TileMeshBuildEngine`, not in another 200 lines of the god-object. Step 10 can run any time after 2.

## 7.5 A layer draws, or it is not submitted (the minzoom/maxzoom/visibility draw gate)

One rule decides the whole section: **a layer that paints nothing the framebuffer can show does not reach
the GPU.** Where that is decided depends on whether it can change while the style is loaded.

### Decided once, at construction

Two properties can never turn on later in the session, so `RenderLayerFactory.Create` refuses the layer
outright — no `IRenderLayer`, no slot, no material, no backend registration, and no mesh built for it on any
tile:

| property | skip reason |
|---|---|
| `layout: {"visibility": "none"}` | `LayerSkipReason.Hidden` |
| an opacity that is a **constant** below `ZoomStyleApplier.VisibleOpacityEpsilon` (one 8-bit step) | `LayerSkipReason.FullyTransparent` |

`RenderLayerFactory.TryGetFetchSource` applies the same two tests, so a source whose only readers can never
draw is never fetched and its tiles are never MVT-decoded. Both are the author's intent rather than a
renderer limitation, so `MapView.LogSkippedLayers` stays silent about them, as it already does for a
source-less symbol layer.

A **zoom-dependent** or **feature-dependent** opacity is deliberately not in that table. Neither reduces to
a decision that holds for the whole session: the first changes with the camera, and the second cannot be
represented by any single per-layer scalar.

### Decided per frame, at submission

What is left varies with the camera, so each remaining layer carries a **fade factor** `p in [0,1]`. It
is not a style property. Its target is a boolean from one predicate — `StyleLayer.IsVisibleAtZoom(liveZoom)`
— evaluated per layer per frame in `RenderLayerSet.ApplyZoom`. `p` eases to its target over
`StyleTransition` using a fourth copy of the ease `ZoomStyleApplier` already runs three times, and
multiplies the layer's `_Opacity` at the one `SetFloat` that pushes it.

The gate reads the product, not fade alone:

```
ZoomStyleApplier.EffectiveOpacityIsZero  ==  fade * authoredOpacity < VisibleOpacityEpsilon
```

`IFadeableRenderLayer` re-exposes it and `TileManager.PushLayerDrawGates` pushes it per slot into
`ITileRenderBackend.SetLayerVisible`. That push sits beside **every** `RenderLayerSet.ApplyZoom` — the call
that refreshes both terms — so no frame can render between the two. The authored term is the unscaled value
`ApplyZoom` just pushed (the binding carrying `ScaledByFade`, of which `BindOpacity` is the sole writer), so
it is never a frame stale; a settled Constant keeps its bind-time value, which is its value at every zoom.

Two properties of that predicate are load-bearing and each has its own tooth:

- **Settled, not merely heading for zero.** A layer mid-fade still shows something, so it keeps submitting —
  the fade needs something to blend. `fill-extrusion` never takes an intermediate value at all:
  `RenderLayerSet.AdvanceFade` substitutes `StyleTransition.Instant` for the ease's duration when
  `FillExtrusionRenderLayer.FadesGradually` is `false`, because
  `FillExtrusionTweaker.ApplyElevatedContract` blends `One/Zero` with `DepthWrite.On`, so alpha is discarded
  there and a partly-present building would render solid rather than translucent.
- **A feature-dependent opacity fails safe.** All four paint binders in `Materials.MaterialFactory` bind a
  constant `1` when `Opacity.DependsOnFeature` (the per-feature value is baked into vertex alpha instead), so
  the authored term reads 1 and such a layer is never gated out on a value that does not describe it.

**Why it rides `_Opacity` rather than a dedicated uniform.** `_Opacity` has one binding path
(`ZoomStyleApplier.BindOpacity`), so fade composes with authored opacity at a single site. A dedicated
uniform would cost a shader Property, a CBUFFER member and a DOTS instanced prop in every one of the three
kind families, and would move every parity count — for a value always multiplied into `_Opacity` anyway.

### How each backend honours the gate

The three differ in mechanism and must agree on outcome — the same shape `IRenderLayer.CastShadows` carries.

| backend | mechanism | cost |
|---|---|---|
| BRG | the slot is skipped in `ComputeEmitOrder`, for the camera AND the light view | one list read per item per cull |
| GameObjects | `Renderer.enabled = false` on that slot's layer children | one write per item, on CHANGE only |
| Entities | `DisableRendering` added to that slot's entities | one batched structural change, on CHANGE only |

`AddTileLayer` consults the gate too: tiles keep finishing while a layer is gated, so an item registered into
an already-gated slot must arrive undrawn. BRG gets that from reading the gate at emit time; the other two
apply it per item as they build it. The gate is two-way — lifting it re-enables the slot — which is the half
a "the layer disappears" test cannot see, so each backend's tooth asserts the return trip explicitly.

**No map fragment pass reads fade, and a structure fence keeps it that way.** A discard on `_Opacity`
would be dead work, since a gated layer never reaches a fragment. The rule is enforced structurally rather
than by a rendered tooth because most of the passes it covers cannot run: of the 18 fragment passes across
Fill, Line and FillExtrusion only **7** can rasterise in the shipped configuration — the four forward passes
of Fill and Line, the two fill-extrusion forward passes, and the fill-extrusion ShadowCaster. The GBuffer
passes never run under Forward+ (`Renderer.asset` is `m_RenderingMode: 2`); DepthOnly and DepthNormals are
built with `RenderQueueRange.opaque` while every map layer sits at queue >= 3000; and the Fill and Line
ShadowCaster passes are dropped by all three backends because those kinds declare `ShadowCastingMode.Off`.
Every row of that split is a configuration, not a construction, so
`ShaderStructureTests.NoMapPassBody_DiscardsOnTheLayerFadeUniform` is the sole observer for the other 11.

### What the gate costs, and the one thing it gives up

A gated layer costs no vertex work, no draw call, no depth and no shadow — but its mesh still exists, because
the gate acts after the tile is built. Removing the mesh too means removing the layer, and that is the
subject of the next section.

Construction-time refusal gives up one thing: a skipped layer cannot **ease** back in. Flipping
`visibility` to `visible`, or an opacity from a constant `0` to a constant `0.8`, changes the built layer
set, so `RenderLayerSet.TryRestyleInPlace`'s id-keyed diff refuses and the full-rebuild arm runs instead of a
fade. Correct, merely not fast, and identical for both skip reasons.

### Why a layer is NOT removed and rebuilt as the camera crosses its zoom bounds

The obvious extension — drop an out-of-range layer entirely, rebuild it on re-entry — is not viable against
this pipeline, and the reason is worth stating because the question keeps arising.

The prepared mesh cache would not absorb the re-entry; it would be discarded wholesale on every crossing.
`PreparedKey` is (`StyleToken`, `TileId`, `LayerId`), and `StyleToken` digests
`MapView.LayerNumbering(Layers)` — the `index:id` pairing of the **built** set. A layer set that varies with
zoom therefore changes the token at every crossing, re-keying every entry for every tile and every layer, not
just the layer that moved. Independently, `LayerId` is the slot index, so removing a layer mid-list renumbers
every later layer and their cached meshes are keyed wrong. Each crossing would cost a full re-decode and
re-mesh of the whole cover, and `liberty.json` has 15 zoom-bounded layers.

The slot model cannot express "absent at this zoom, present at another" either. A skipped layer COMPACTS the
numbering — `RenderLayerSet.Build` increments `drawIndex` only on the real-layer branch — which is exactly
what a zoom-varying set must not do. `TombstoneRenderLayer` is the construct that removes a layer while
holding its slot width, and it is necessary but **not sufficient** here: it would fix `LayerId` and
`LoadedTile.MaterialIndices`, while the style token would additionally have to stop depending on which layers
are currently present. That is a separate decision about what the cache key means — today it captures the
built numbering precisely so a style whose layer set changed cannot silently reuse meshes.

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
