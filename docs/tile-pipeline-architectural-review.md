# Tile-pipeline architectural review — TileManager + consume pipeline

**Status:** review, 2026-07-11 (branch `perf/label-instrumentation`). Author: Fable review pass;
findings independently verified against source (file:line confirmed for the top stall sources and the
decomposition line-count breakdown). Input to the companion design doc
[`tile-pipeline-design.md`](tile-pipeline-design.md).

**Motivation:** two maintainer-raised problems — (1) `TileManager.cs` is ~1790 lines and hard to hold in
the head; (2) residual intermittent main-thread stalls from tile jobs despite hard rate-limiting (per-frame
mesh-build-kick cap, dual per-frame consume budget of mesh-count AND vertex-count, resumable per-mesh
consume). This review locates the *specific* remaining spike sources and the decomposition seams; the design
doc turns them into a concrete plan.

Scope: `TileManager.cs` (1790 lines), the fetch→build→consume pipeline, the three render backends, the
symbol-bytes push, and the per-frame loop in `MapView.LateUpdate`. All paths relative to
`Assets/Code/MapRenderer.Unity/` unless noted.

---

## 1. Verdict

TileManager is a real god-object by responsibility count (seven distinct jobs), but the raw line count
overstates it: **920 lines are code, 695 are comments, 175 blank**. Roughly a third of the code is
test/telemetry observability that the repo's own conventions say doesn't belong in a production class. The
per-(tile,source) `LoadedTile` state machine with its four async exit paths is genuine essential complexity
and should stay in one place; the source registry, the kick/consume engine, and the prepared-cache bridge are
cleanly separable and should come out.

On stalls: the budgets you built (kick cap, dual consume budget, resumable consume) correctly bound the
*consume* path — but three whole categories of main-thread work are **unbudgeted**: the synchronous
symbol-label build burst, the release/eviction path, and per-layer ECS structural cost inside the budgeted
consume. Those are structural, fixable problems, not tuning problems. One (the one-mesh overshoot) genuinely
requires a mesher change; everything else is scheduling/backend work.

## 2. TileManager decomposition

### What it actually contains today (code-line estimates, comments excluded)

| Block | Code lines | Location |
|---|---|---|
| Source pipeline registry + restyle diff (`SourcePipeline`, `SourceKey`, `SourceSpec`, `SetSources`, `FindPipeline`, `DisposePipelines`) | ~150 | L196–533, L1777–1788 |
| Backend construction + accessors (`BuildBackend`, `LayerMaterials`, `LayerNames`, three backend getters, `InstancedRebuild`) | ~50 | L535–565, L692–715 |
| Cover selection + request/release (`Tick`, cover-key fields, `InvalidateCover`) | ~110 | L348–361, L786–917 |
| Fetch pump + kick + consume (`PumpPending`, `KickMeshBuild`, `ConsumeMeshBuild`, `FinishConsume`, `AppendMeshes/Ints`, `DisposeWholeResult`, `ObserveFetchOutcome`, `LogFetchErrorThrottled`) | ~290 | L1035–1415, L1598–1635 |
| Prepared-cache integration (Tick probe, `TransferBuiltMeshesToCache`, `BuildTileFromCache`, `ComputeDenseLayerIds`) | ~90 | L302–314, L853–894, L1470–1527 |
| Release + disposal pens + teardown (`ReleaseTile`, `RenderTeardownRecord`, both drains, `DestroyTrackedMeshes`, `DoDispose`) | ~150 | L1428–1596, L1642–1775 |
| Telemetry + test observability (`CaptureTelemetry`, ~12 counters/probes, `DrainMeshBuilds`, `TryGetBuiltTile`, `GetTileMeshes`, `AllTilesSettled`, `ComputeSceneBounds`) | ~180 | L567–690, L717–772, L937–1015 |

### Target structure (concrete)

**`SourcePipelineRegistry`** (new file, ~200 lines with docs)
- Moves: `SourcePipeline`, `SourceKey`, `SourceSpec`, the diff/keep/teardown body of `SetSources` step 2
  (L480–518), `FindPipeline`, `DisposePipelines`, `SourceIdOf`, `InFlightCount`.
- Seam: `int Count`, `SourcePipeline this[int slot]`, `void ApplySpecs(IReadOnlyList<SourceSpec>)`,
  `void DisposeAll()`.
- `TileManager.SetSources` shrinks to: teardown `_loaded` records → `_prepared.Clear()` →
  `_registry.ApplySpecs(specs)` → `BuildBackend(...)` → re-arm cover. The registry never touches `_loaded` or
  the backend today, so this cut is clean — no behavior change.

**`TileMeshBuildEngine`** (new file, ~380 lines with docs)
- Moves: `MeshBuildResult`, `KickMeshBuild` (L1192–1269), `ConsumeMeshBuild` (L1290–1373), `FinishConsume`,
  `AppendMeshes`, `AppendInts`, `DisposeWholeResult`, the `_consumeScratch*` lists, the `_pendingDisposal` pen
  + `DrainPendingDisposal` + the blocking drain from `DoDispose` (L1691–1718).
- Seam: `UniTask<MeshBuildResult> Kick(TileId, byte[], string sourceId, double3 origin)`,
  `bool Consume(TileId, ref LoadedTile, int meshBudget, int vertBudget, out int meshes, out int verts)`,
  `void StashDiscard(task)`, `void DrainDiscards()`, `void DrainAllBlocking()`.
- This is where every future consume-path fix (two-phase kick, mesher split handling) lands without touching
  the lifecycle class again — the main payoff of the split.

**`PreparedTileBridge`** (new file, ~180 lines with docs)
- Moves: ownership of `PreparedTileCache` + `CurrentStyle`, `ComputeDenseLayerIds` + scratch, the Tick probe
  block (L857–869), `TransferBuiltMeshesToCache`, `BuildTileFromCache`.
- Seam: `bool Enabled`, `ProbeFullHit(...)`, `TakeAsLoadedTile(...)`, `TransferOnRelease(...)`, `Clear()`,
  `Dispose()`, hit/miss counters.
- Side benefit: the dense-layer-id set can be **cached per source** and invalidated on `ApplySpecs`, removing
  the O(layerCount) scan currently done per cover-tile per pipeline on every dirty frame (L859) and per
  release (L1472).

**Test surface** — apply the repo's own rule: `TryGetBuiltTile`, `GetTileMeshes`, `AllTilesSettled`,
`ComputeSceneBounds` and most `*LastTick` counters become test-assembly extension methods over `internal`
state. `DrainMeshBuilds` (L937–1015, explicitly "NOT called from the production Update path") re-expresses
itself on top of the engine seam (`Kick`+`Consume` with `int.MaxValue` budgets). `CaptureTelemetry` stays
(the debug readout panel is a production caller).

**What stays in `TileManager`** (~420–500 code lines): `LoadedKey`/`LoadedTile`, `_loaded`, `Tick`,
`PumpPending` (calling `_engine.Kick/Consume`), `ReleaseTile`, `RenderTeardownRecord`, `_pendingFetchDisposal`
+ drain, `ObserveFetchOutcome`, `DoDispose` orchestration, `SymbolTileBytesReady`.

**What NOT to split:**
- The `LoadedTile` state machine and its exit paths. This is the single-owner mesh-lifetime contract from
  `docs/async-architecture.md`; smearing it across classes is how double-frees happen. Its size is essential
  complexity.
- Cover-key/dirty tracking — ~30 lines, cohesive with `Tick`.
- The holding pens move *with* their producers (mesh pen → engine, fetch pen → stays), not into a generic
  "DisposalService".

**Honest bottom line on size:** ~40% of the file is comments, much of it stage archaeology
(S47/S51/S55/S82/S83b/S84/S87/S89/S91/S95/S105) that restates superseded semantics — the S55-vs-S87 budget
story is told at least three times (L78–93, L1017–1033, L1271–1289). Prune during the split. Of the 920 code
lines, ~300 are extraction-worthy accident, ~180 are test/telemetry that shouldn't be here, and the rest is
irreducible lifecycle. 1790 lines is a real navigation problem, but it is not 1790 lines of design debt.

## 3. Main-thread stalls — ranked spike sources

The dual consume budget works as designed. The remaining stalls come from paths the budgets never see.
(✅ = independently verified against source in this pass.)

| # | Source | File:line | What stalls / when | Magnitude | Fix | Tradeoff |
|---|---|---|---|---|---|---|
| 1 ✅ | **Synchronous symbol-label build burst (unbudgeted)** | TileManager.cs:1148–1155 → SymbolLabelSubsystem.cs:203–208, 232–266 → StyledSymbolTileBuilder.cs:87–157 | `SymbolTileBytesReady` fires inside `PumpPending` for **every** fetch completing this frame (fetch observation is uncapped — only kicks are). `BuildTileAsync` runs synchronously on main: a **second** `MvtDecoder.Decode` of the same bytes (SymbolLabelSubsystem.cs:241; the mesh worker decodes them again at TileManager.cs:1229), extract, and — whenever glyph ranges are cached, i.e. every tile after the first few — the per-character awaits (StyledSymbolTileBuilder.cs:87–91) complete synchronously so **all shaping + TextQuadLayout/CurvedTextLayout run inline on main**. Cold-cache resume work (GlyphPbfDecoder.Decode + SDF `AppendToAtlas`, GlyphManager.cs:105–143) is also main-thread. Plus a full 4096² R8 = **16 MB** `LoadRawTextureData`+`Apply` per tile that added any glyph (SymbolLabelSubsystem.cs:259–265, GlyphAtlasTexture.cs:59–60). | The known 600 ms-class stall; scales with tiles-completed-per-frame × labels-per-tile | (a) Scheduling-only, first: queue bytes-ready tiles, build ≤1/frame; coalesce atlas uploads to ≤1/frame. (b) Decode+extract to thread pool (touch neither atlas nor Unity objects). (c) Off-thread shaping: pass 1 on main, then shape/lay out on the pool against an immutable resolver snapshot — the fixed-size atlas is only *read* in pass 2; `Append` stays main-thread. (d) Optionally reuse the mesh worker's decoded `MvtTile`. | (a) labels a few frames later — invisible. (b/c) the shared glyph cache/atlas is the documented hazard — needs a lock or strict append-on-main/read-off-main discipline. (d) couples symbol push to mesh-build timing — probably not worth it vs. decoding off-thread. |
| 2 ✅ | **Unbudgeted release/eviction storm** | TileManager.cs:899–905 (all out-of-cover records released in one Tick); Entities/TileRenderer.cs:359–374 (one `DestroyEntity` structural change **per layer** + root); PreparedTileCache.cs:206–235 (`Put` + inline LRU `DestroyMesh` loop) | Zoom-out/fast pan releases N tiles × L layers in one frame: N·L entity destroys, N·L cache Puts, plus a `DestroyMesh` burst when the byte budget overflows. Consume is budgeted to 4 meshes/50k verts; release has **no budget** — a mirror-image spike. | Structurally certain; e.g. 15 tiles × 30 layers = 450 structural changes in one frame on Entities | Deferred-release queue with a per-frame budget (same pattern as consume); on Entities, batch destroys via `EntityManager.DestroyEntity(NativeArray<Entity>)` once per frame | Released tiles linger 1–3 frames — invisible (off-screen by definition). Slight bookkeeping for incremental `_toRelease` draining |
| 3 ✅ | **Entities `AddTileLayer`: per-layer `RenderMeshArray` + structural changes** | Entities/TileRenderer.cs:292–297, :308 | Every consumed mesh creates a fresh one-element `RenderMeshArray` shared component → EG batch registration per entity, plus `CreateEntity` + a `ComponentTypeSet` migration. Own comment (L97–98) names this "PRIME SUSPECT". At `MaxConsumesPerTick=4`: 4 × (2 structural changes + EG registration) per load frame | The FetchPoll spike previously profiled on the Entities backend | Register once: `EntitiesGraphicsSystem.RegisterMaterial` per layer at construction, `RegisterMesh` per mesh at consume, `MaterialMeshInfo.FromMeshIDAndMaterialID`, pre-built archetype so `CreateEntity(archetype)` is one cheap op. Or ship on BRG (`AddTileLayer` = dict add + `RegisterMesh`, BRG/TileRenderer.cs:271–296) and keep Entities as the debug opt-in | ID route needs explicit unregister-on-remove; BRG route loses Entities Hierarchy inspectability for the default config |
| 4 | **One-mesh overshoot** (the only mesher fix) | TileManager.cs:1319–1322 (documented), 1340–1348 | Budget checked *before* each layer, so a single 100k+-vert water/landcover fill uploads whole: one un-splittable `ApplyAndDisposeWritableMeshData` + registration. The vertex budget cannot cap it by definition | Several ms, worst on exactly the tiles users look at (oceans at low zoom) | Cap verts per payload in `StyledFillTileBuilder`/`StyledLineTileBuilder.WriteMeshData` (~32k, split at feature boundaries), emit K payloads per layer. Consume/backends need **no** changes — payloads carry `MaterialIndex` (L1327–1330), `Meshes/DrawHandles` are arrays. Catch: `KickMeshBuild` pre-allocates MeshDataArrays on main **before** the worker knows counts (L1215–1220) → requires the **two-phase kick**: frame A worker decodes + measures; frame B main allocates exact arrays; worker writes | +1 frame tile latency (invisible next to network); a new "measured, awaiting alloc" `LoadedTile` state; more, smaller meshes. Also fixes #5 |
| 5 | **`KickMeshBuild` main-thread allocate loop** | TileManager.cs:1195 (`SnapshotLayers()` fresh array/kick), 1215–1220 (`dense` × `AllocateWritableMeshData(1)`) | One native MeshData alloc per this-source layer per kick — liberty-class styles: dozens per kick × 2 kicks/frame, most ending 0-vertex (allocated, carried, disposed unused) | **Unmeasured** — `PmMeshDataAllocate` was added on this branch (correctly measure-first) | Subsumed by two-phase kick (#4). Interim single `AllocateWritableMeshData(dense)` call conflicts with per-layer resumable consume (whole-array apply) — don't | Get the number from the new marker before spending effort here |
| 6 | **Cover recompute every frame while anything pends** | TileManager.cs:813–816, then 818–905 | Static camera + pending tiles (the whole load window): full `SelectVisibleTiles` descent + `_coverSet` rebuild + cover×pipeline probes every frame **for zero effect** — unchanged cover means the request loop is all `ContainsKey` no-ops, the release loop finds nothing. The descent is all-managed, 5 interface-dispatched projections per visited tile (FrustumTileSelector.cs:172–176) | ~0.5–2 ms/frame tax during exactly the frames already under consume load — widens stalls | Gate the recompute on `_coverDirty` alone; `pending` only needs to prevent the early-out before `PumpPending` — which already ran (L811). Test: N ticks, pending>0, clean camera ⇒ `CoverRecomputesLastTick == 0` (the S95 counter exists precisely for this) | I see no hidden dependency (releases/requests key off cover *changes*) — but verify with the cover/consume suite before trusting me |
| 7 | **GC collections from off-thread load work** | per-tile `byte[]` (L1142), `MvtDecoder.Decode` object graphs on the pool (L1229), payload/closure allocs per kick | Mono GC stops the main thread regardless of which thread allocated. The zero-GC contract covers the *idle* loop; the load path allocates freely on workers — a collection lands as an "intermittent stall the budgets can't explain" | **Hypothesis, not measured** — but matches the intermittent-despite-budgets symptom precisely | Profile with GC markers during a pan storm; enable incremental GC. If confirmed: pool decode buffers; long-term, decode-to-NativeArray | Pooling managed decode graphs is invasive; do nothing until the profiler confirms |
| 8 | **BRG `Rebuild` full per-frame repack** (opt-in path) | BRG/TileRenderer.cs:334–384, 395–438 | Per frame: sort all items (delegate comparator), repack 76 floats × N, **33 material-property reads × N**, full `SetData` | Steady cost, not a spike (200 items → ~6.6k material reads/frame) | Dirty-flag material props (change only via `ApplyZoom`), repack transforms only on frame change, partial `SetData` | Only matters if BRG becomes the default per #3 |
| 9 | **Restyle hitch** | TileManager.cs:466–478 + BuildBackend:537–546 (disposes and recreates the entire Entities `World`), PreparedTileCache.Clear:261–277 | Full teardown + world rebuild + every cached mesh destroyed, one frame | Big but rare, user-initiated | Accept; documented tradeoff | — |

Classification: #1, #2, #6 are **scheduling-only**; #3, #8 are **backend-only**; #4 (+#5) needs the
**meshers**; #7 is measurement-first.

## 4. Over-engineering / risk callouts

- **Test observability inside the production class** — ~180 code lines (L567–772, L937–1015) exist for tests,
  against the repo's own convention. The single largest *accidental* contributor to the bulk.
- **Comment archaeology** — 695 comment lines, heavily stage-tagged, repeatedly restating superseded
  semantics. Keep the *why*, drop the "was X in S55, became Y in S87" narrations git history already holds.
- **Budget-zero semantics asymmetry** (L84–93, L1049–1055): `MaxConsumesPerTick == 0` blocks consume entirely
  while `MaxVerticesPerTick == 0` means uncapped — two adjacent Inspector-serialized fields. Zeroing the wrong
  one freezes the map. Make 0 uniform (uncapped); give tests an explicit block seam.
- **`ComputeDenseLayerIds` linear scan** per cover-tile×pipeline probe on dirty frames (L859) and per release
  (L1472). Cache per source; falls out of the `PreparedTileBridge` cut.
- **`PreparedTileCache`'s lock** kept "for structural parity" while documented main-thread-only
  (PreparedTileCache.cs:63–67). Dead weight.
- **Shared `_toRelease` scratch with two meanings** — `PumpPending` fills it with not-Built keys
  (L1057–1060), `Tick` with left-cover keys (L900). Correct today, a reader-trap; rename.
- Genuinely fine, one line each: the dual holding pens are necessary given UniTask's unobserved-exception
  finalizer; the zero-boxing keys are correct; the prepared-cache node pool is justified with documented
  empirical A/B evidence; `MeshDataPayload`'s exactly-once Upload/Dispose contract is clean.

**Latent lifetime risks:**
- **Restyle vs. in-flight symbol build:** `SymbolLabelSubsystem.SetStyle` disposes
  `_glyphManager`/`_atlasTexture`/clears the store (SymbolLabelSubsystem.cs:127–130, 296–307) while a
  `BuildTileAsync` may be suspended awaiting a glyph fetch; on resume it touches disposed state. The catch at
  L268 swallows it into a warning and the store generation likely rejects the commit — probably benign,
  unverified; worth a test.
- **`BuildTileAsync(...).Forget()` carries no CancellationToken** (L207) — teardown mid-build resumes into
  disposed state. Same fix: a subsystem CTS cancelled in `SetStyle`/`Dispose`.
- **Teardown spin caps** — `DoDispose` spins up to ~10 s per in-flight task class (TileManager.cs:1696–1743);
  a hung fetch = ~10 s freeze on exit. Acceptable, but know it's there.
- `LoadedTile.ReadyBytes` retention while kick-capped is unbounded in aggregate (N × ~0.1–1 MB) during heavy
  churn — memory, not correctness.

## 5. Recommended sequence

1. **Budget the symbol path** (scheduling-only, in `SymbolLabelSubsystem`): queue bytes-ready tiles, ≤1
   build/frame; coalesce atlas uploads to ≤1/frame. Attacks the known worst stall with near-zero risk. Then
   decode+extract off-thread; shaping off-thread last (that's where the shared-atlas hazard lives).
2. **Gate the cover recompute on `_coverDirty` only** — one line + a test against `CoverRecomputesLastTick`.
3. **Budget the release path** — deferred-release queue mirroring the consume budget; batch `DestroyEntity`
   per frame on Entities.
4. **Fix Entities `AddTileLayer`** — RegisterMesh/RegisterMaterial + `MaterialMeshInfo` IDs + pre-built
   archetype (or make BRG the ship default and keep Entities as the debug backend, which its own doc comments
   half-endorse).
5. **Profile GC during a pan storm** before any pooling work — confirm or retire the invisible-spike
   hypothesis cheaply.
6. **Two-phase kick + mesher vertex cap** — the only fix for the one-mesh overshoot; subsumes the
   kick-allocate loop. Do it with the `PmMeshDataAllocate` numbers in hand.
7. **Decompose TileManager** (`SourcePipelineRegistry` → `PreparedTileBridge` → `TileMeshBuildEngine`, test
   surface out to the test assembly). Do it **before** #6: the two-phase kick wants to land in
   `TileMeshBuildEngine`, not in another 200 lines of the god-object.
