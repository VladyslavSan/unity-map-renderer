# Render-layer unification + Burst tessellation — design

Status: **IN PROGRESS** (hand-driven). Author: cleanup epic, 2026-07-01.

## Progress log
- **Stage A — DONE (`92e2e0b`, 2026-07-01).** `IRenderLayer`/`RenderLayerSet` + `RenderLayerFactory`;
  fill/line unified into one ordered `List<IRenderLayer>` (index == draw order == material index);
  `TessellationResult` two lanes → one `IRenderLayerTessellation[]` (reference-type handle so `Dispose`
  mutates the NativeArray struct in place); all 3 backends index the one declared-order material list;
  `FillCount+li` flatten + "fills first" comments gone. Pure indirection over the UNCHANGED managed
  builders — behaviour-preserving (906 EditMode green). Decision-5c sparse fill KEPT (dies in C). New
  `RenderLayerSetTests` locks: one ordered list, fill/line interleaved not type-bucketed, monotonic queue,
  non-renderable background takes no slot, factory is sole dispatch.
- **Stage B — DONE (2026-07-01, 907 EditMode green).** `Mesh.MeshData` replaced the bespoke `LayerMeshData`
  + `UploadMesh`/`BuildMesh` + the `SetVertexBufferData` copy. `WriteMeshData` tessellates straight into a
  caller-allocated writable `MeshData` off the worker; one shared `MeshDataTessellation` handle (a
  count-1 `MeshDataArray`) replaced Stage A's per-type handles. TileManager allocates per this-source layer
  at kick, worker writes, consume `ApplyAndDisposeWritableMeshData` per-layer (S87 budget); fault-safe
  wrapping disposes every array. Leak guard → `MeshDataTessellation.DebugLiveAllocCount`. Perf-parity flags
  (`DontValidateIndices|DontRecalculateBounds` + worker AABB) preserved. Coupled tests reworked onto the new
  API (SyntheticLineMesh, MapViewAsyncTessellation, S51/S55 leak guards, LinePaintS14 bake, FillSceneHelper,
  MapViewLiveLoop) + shared `TestTileMeshBuilder`.
- **Stage D1 (line kernel) — DONE (2026-07-02, 915 EditMode green).** `LineTessellationJob` rewritten from
  a 2-point `float3` smoke stub into a faithful `[BurstCompile]` transliteration of the managed
  `LineTessellator.Triangulate` (all join types — miter/bevel/round; all caps — butt/square/round; dedup,
  miter→bevel fallback, worst-case `MaxVertexCount`/`MaxIndexCount` sizing). Emits `NativeArray<LineVertex>`
  (double-precision) so it is **bit-comparable** to the oracle. `LineTessellationJobTests` reworked into the
  differential oracle: **strict bit-exact** parity on the arithmetic-only paths (miter/bevel + butt/square +
  the fixture `geolines` layer — only +,−,*,/,sqrt, IEEE-exact between Burst and Mono) and **tight-tolerance
  (1e-9)** parity on the transcendental round join/cap (atan2/cos/sin may differ a ULP; triangle topology
  still exact). Isolated/test-only — touches NO live code. **Fill kernels** (`MvtDecodeJob`/`RingAssemblyJob`/
  `EarcutJob`/`ProjectTileToWebMercatorJob` via `TileTessellationPipeline`) were already oracle-validated
  strict by `JobifiedPipelineTests` — so the geometry-Burst port (D1) is now complete. Remaining: **C** (per-
  `(tile,layer)` produce) + **D2** (wire the D1 jobs into the live lifecycle; `UniTask→JobHandle` + drain pen).
- **Stage C (dense per-source produce) — DONE (2026-07-02, 915 EditMode green).** Killed the S83b
  decision-5c full-width sparse union. `TessellationResult.Payloads` is now **dense per-`(tile, source)`**
  (one slot per this-source layer in draw order, no null other-source slots); each payload
  (`MeshDataTessellation`) **carries its own `MaterialIndex`**. Consume uses `payload.MaterialIndex`
  (not `cursor == materialIndex`) with a restyle-shrink guard (`(uint)materialIndex >= _layers.Count` →
  free, no backend throw); `ConsumeCursor` is now a dense resume index. Kick allocates/produces only dense
  slots. Behaviour-preserving (Visual snapshots unchanged). Acceptance tooth #2 (“no decision-5c sparse
  union anywhere”) met.
- **Stage D2 (fills → live Burst geometry) — DONE (2026-07-02, 917 EditMode green).** The live fill path now
  tessellates via **Burst geometry in the running pipeline** (the jobs were test-only before). Key
  reconception (vs the doc's async-JobHandle D2): the GC win is entirely in **Phase-1 geometry**
  (decode/assemble/earcut/project → `List`/array garbage); the managed stream-write is already alloc-free.
  So run the existing Burst kernels **synchronously on the current `UniTask` worker via `.Run()`** — no
  lifecycle rewire (kick/consume/S48/S84/S87 unchanged). Spike-proven off-main
  (`BurstJobRunOffMainSpikeTests`: `ProjectJob.Run()` + `EarcutJob.Run()` correct on a `RunOnThreadPool`
  worker; kernels use caller-provided Persistent scratch, no internal `Allocator.Temp`). `TileTessellationPipeline`
  switched `.Schedule()→.Run()` (output bit-identical, `JobifiedPipelineTests` still holds) + emits a
  per-vertex `VertexFeatureIdx` so the stream-write assigns per-feature color. `StyledFillTileBuilder`
  Phase-1 (managed `MvtGeometry.Decode`/`PolygonAssembler`/`Earcut`/`ProjectVerticesManaged` + `FeatureMeshData`)
  deleted → drives the pipeline; managed tessellation garbage gone. Lines still managed (next slice).
- **Stage D2 (lines → live Burst tessellation) — DONE (2026-07-02, 918 EditMode green).** Same
  `.Run()`-on-worker pattern: `StyledLineTileBuilder` swaps the per-ring managed `LineTessellator.Triangulate`
  for the D1 `LineTessellationJob.Run()` (now USED in the live path). Decode + `ProjectLineRing` stay managed
  (double2 feeds the double-precision job → strict parity on the miter/butt path; round join/cap within
  snapshot tolerance). Guarded by an added `BurstJobRunOffMainSpikeTests` case — `LineTessellationJob` uses
  `Allocator.Temp` INTERNALLY (unlike the fill kernels' caller scratch), proven safe off a `RunOnThreadPool`
  worker. **S89 complete: managed Core geometry (`Earcut`/`PolygonAssembler`/`LineTessellator`) is retired
  from the live production path — differential oracle only.** (Line decode+projection remain managed — a
  later throughput follow-up, not a correctness gap.)
- **Stage B (historical note) —** `Mesh.MeshData` replaces the bespoke `LayerMeshData` + hand-rolled 4-stream
  consume. **Spike-verified threading rules (`MeshDataThreadWriteSpikeTests`):**
  `SetVertexBufferParams`/`GetVertexData`/`SetIndexBufferParams`/`GetIndexData`/`SetSubMesh` **DO** run on a
  raw `UniTask.RunOnThreadPool` worker (a known vertex round-trips through `ApplyAndDisposeWritableMeshData`);
  `AllocateWritableMeshData` and `ApplyAndDisposeWritableMeshData` are **main-thread only**
  ("CreateNewMeshDatas can only be called from the main thread"). **Choreography (locked):** allocate one
  `MeshDataArray(1)` per this-source layer at KICK (main); write on the WORKER; `ApplyAndDispose` per-layer
  at CONSUME (main), budget-gated (S87 — per-`(tile,layer)` array so a rich tile still spreads across
  frames). Per-layer allocation → the two Stage-A payload handles collapse into ONE `MeshDataTessellation`
  (post-write the handle is layer-type-agnostic). Leak guard retargeted to a MeshDataArray alloc/dispose
  counter (unapplied arrays are native leaks Unity tracks); holding-pen + teardown dispose every unapplied
  array. Perf-parity flags preserved (`DontValidateIndices|DontRecalculateBounds` + worker-computed bounds).
- Stages C, D1, D2 — pending (see §4).

This is the "make it right" plan for the styled-layer / tessellation pipeline. It replaces the
fills-vs-lines split (which drifted away from `ARCHITECTURE.md`'s "ordered list of layers") with a
single, extensible render-layer abstraction, moves tessellation onto Burst jobs over
`Unity.Collections` data, and collapses the bespoke mesh-result plumbing onto `Mesh.MeshData`.

---

## 1. Why (the current mess)

1. **`StyledLayerSet` splits into `_fills` / `_lines`.** Contradicts `ARCHITECTURE.md` §"Layer ordering":
   *"the style is an ordered list of layers, composited in order."* The declared interleaving is thrown
   away in storage and rebuilt via `renderQueue`.
2. **`materialIndex` is a second ordering.** Backends index a *fills-then-lines* flattened material list
   (`FillCount + li`), an implicit cross-component contract hand-rolled in `TileManager` **and** all three
   backends, with comments ("fills first, then lines") that read as false draw-order claims.
3. **`TessellationResult` carries two typed arrays** (`LayerData` fills / `LineLayerData` lines); the
   consume cursor walks fills-then-lines.
4. **Per-`(tile, source)` tessellation produces a FULL-WIDTH sparse result** (decision 5c): a slot for
   every layer, empty for other sources' layers, unioned at consume — dead slots carried per source.
5. **Tessellation is managed** (`UniTask.RunOnThreadPool`) producing bespoke `LayerMeshData` structs of
   `NativeArray`s, then a **hand-rolled main-thread consume** copies four vertex streams into
   `UnityEngine.Mesh` — reinventing what `Mesh.MeshData` models natively.
6. **The geometry stack is managed, engine-free Core** (`PolygonAssembler.Assemble(List<List<double2>>)`,
   `Earcut`, `LineTessellator` — all `List<>`/alloc based). Not Burst-compatible.

All of 1–4 are the same root: **fill and line are modeled as two parallel typed lanes end-to-end.**

## 2. Locked decisions

- **D1 — `IRenderLayer` + `StyleLayer` split.** `StyleLayer` (Core, engine-free) = parsed paint/layout
  *expressions*, filter, source, source-layer. `IRenderLayer` (Unity, managed) = the runtime render object:
  owns its `Material`, layer type, **vertex layout**, `ZoomStyleApplier`/per-frame apply, and the scheduler
  for its Burst tessellation job. It *references* a `StyleLayer`.
- **D2 — One big epic now** (internally sequenced), with **`Visual/` snapshot parity** (identical pixels) +
  898 EditMode green as the safety net throughout. Add snapshot coverage for any touched path that lacks it
  *before* refactoring it.
- **D3 — Full Burst Job System** for tessellation (not a function-pointer half-measure). Managed decode/
  filter → blittable bucket → Burst `IJob` → `Mesh.MeshData`.
- **D4 — `IRenderLayer` owns its `VertexAttributeDescriptor[]`** — the layout is the byte-for-byte contract
  binding the Burst job's writes ↔ `MeshData` params ↔ the shader's vertex input. One owner.
- **D5 — `Unity.Collections` types throughout** the blittable/job path (`NativeArray`, `NativeList`,
  `NativeHashMap`, …).

## 3. Target architecture

### 3.1 Data vs render object

```
StyleLayer  (Core, managed, engine-free)   — parsed paint/layout expressions, filter, source, source-layer
IRenderLayer(Unity, managed)               — Material, LayerType, VertexAttributeDescriptor[] layout,
                                             ZoomStyleApplier + ApplyZoom(zoom, metersPerPixel),
                                             ScheduleTessellation(bucket, meshData, deps) -> JobHandle,
                                             ref StyleLayer
```

Concrete: `FillRenderLayer`, `LineRenderLayer` now; `FillExtrusionRenderLayer`, … later. A type registry
maps a `StyleLayer` subtype → `IRenderLayer` factory. **Adding a layer type = one `IRenderLayer` class +
one registry entry** — no edits to the layer set, the backends, or the consume loop.

`RenderLayerSet` (replaces `StyledLayerSet`): a managed `List<IRenderLayer>` **in declared order**.
`index == draw order == material index`. One ordering. `_fills`/`_lines`, `FillCount + li`, and the
"fills first" comments are gone.

Symbols/text are explicitly **out of scope** — per `ARCHITECTURE.md` §"Two geometry classes" they are a
separate, placed-every-frame path. `IRenderLayer` covers the **static-geometry** class (fill, line,
fill-extrusion).

### 3.2 The managed ⇄ Burst boundary (the crux)

Three phases per tile, with one hard boundary:

```
Phase 1  MANAGED (thread pool, per (tile, source))
         MvtDecoder.Decode(bytes) -> MvtTile           (protobuf + string props: cannot be Burst)
         FeatureSelector.SelectFeatures(styleLayer,…)  (filter expressions over string keys: managed)
         → emit a BLITTABLE GeometryBucket per (tile, renderLayer):
             NativeArray<double2> points, NativeArray<int> ring/part offsets,
             NativeArray<FeatureMeta> (data-driven width/color, evaluated managed-side)
         ─────────────────────────── BOUNDARY: everything below is Unity.Collections only ───────────────
Phase 2  BURST IJob (per (tile, renderLayer))
         renderLayer's concrete [BurstCompile] job reads the bucket, tessellates
         (earcut fill / centerline expansion line) and writes directly into Mesh.MeshData
         (SetVertexBufferParams(layout) + SetIndexBufferParams, then fills the streams).
Phase 3  MAIN THREAD
         poll JobHandle.IsCompleted across frames; on done Complete() +
         Mesh.ApplyAndDisposeWritableMeshData -> Mesh -> backend.AddTileLayer(mesh, origin, layerIndex, id)
```

- **Decode stays managed.** MVT is protobuf with `string→Value` property dicts; the filter evaluates
  expressions over string keys. Neither is Burst-able. `ARCHITECTURE.md`'s "Burst for decode" is
  aspirational; **tessellation** is the realistic Burst target.
- **`GeometryBucket` is the handoff type** — the single place the pipeline crosses from managed to
  Unity.Collections. One bucket = one `(tile, renderLayer)`. No full-width arrays, no sparse union (5c dies).
- **`TessellationResult` is deleted.** Each bucket → job → `MeshData` → mesh flows independently.

### 3.3 Assembly placement + the core-tests tension  ⚠ biggest structural risk

The geometry kernels (`Earcut`, `PolygonAssembler`, `LineTessellator`) are **managed, engine-free Core**
today and run in the **fast `Tools/core-tests` (`dotnet test`, ~0.1s, no Unity)** via a 2-field
`Unity.Mathematics.double2` shim. Burst needs `Unity.Collections`, which is a full UPM package, **not** a
2-field shim — so a `NativeArray`-based earcut **cannot** stay in engine-free Core without breaking the fast
core-tests (or forcing them to pull the Collections package).

Resolution:
- **The Burst tessellation kernels live in `MapRenderer.Jobs`** (the existing "Burst + Collections jobs"
  assembly), not Core. This is where `ARCHITECTURE.md` already puts Burst work.
- **Port `Earcut` / `PolygonAssembler` / line-expansion to `NativeArray`/`NativeList`, allocation-free,
  `[BurstCompile]`** as jobs/static kernels in `MapRenderer.Jobs`.
- **Core's managed geometry**: options — (a) keep it as the reference impl + oracle for a differential test
  (Burst kernel output must match managed output on the fixture), then retire once trusted; or (b) delete
  after parity. Recommend **(a)** — the managed version becomes the test oracle, which is a *strength* (it
  makes the Burst port falsifiable), then decide on retirement in a follow-up.
- Fast core-tests keep testing the managed Core geometry; the Burst kernels are covered by EditMode
  (Unity) tests + the differential oracle.

### 3.4 Job lifecycle (replaces the UniTask tessellation machinery)

- Tessellation tasks become **`JobHandle`s**, not `UniTask`s. `TileManager` polls `JobHandle.IsCompleted`
  across frames (same shape as today's `.IsCompleted` polling), then `Complete()` + apply on the main thread.
- **Input `NativeArray` buckets must outlive the job** and be disposed only after the job finishes. The S48
  "mid-flight discard holding pen" is **kept, not removed**: a released tile's in-flight job goes into a
  deferred-drain pen that **polls `JobHandle.IsCompleted`** each tick and disposes the bucket + `MeshData`
  when done. Do **not** `Complete()`-and-discard on release — `Complete()` blocks the main thread, and earcut
  on a dense tile is not "short"; that would reintroduce the exact stall S87 killed. `Complete()` is forced
  only at teardown (bounded spin, as today). Burst jobs are not cancellable mid-run, so a released job runs
  out and its output is discarded — but off the main thread.
- **Fetch** stays managed `UniTask` network I/O; **S84 fetch-cancellation is unchanged** (only the
  *tessellation* half moves to jobs).
- **S87 per-mesh consume** is preserved: one `MeshDataArray` per `(tile, layer)` keeps per-mesh apply
  granularity so upload stays budgeted (no per-tile stall).

### 3.5 Backends

`AddTileLayer(mesh, origin, layerIndex, tileId)` — `layerIndex` is the single declared-order index (==
material index). Each backend builds its material registry from the one ordered `List<IRenderLayer>`; the
fills-then-lines flatten (`FlattenLayerMaterials`/`FlattenLayerNames`) is deleted.

## 4. Sequencing (internal stages of the one epic)

Each stage is independently green (898 + snapshot parity) before the next. The `UniTask→JobHandle` +
Burst-port is deliberately **last and isolated** — it is the highest-risk change.

| Stage | Scope | Kills | Risk |
|---|---|---|---|
| **A. Render-layer object** | `IRenderLayer` + `StyleLayer` split; `StyledLayerSet` → one ordered `List<IRenderLayer>`; fill/line impls wrap the *existing* managed builders; backends index by list order | `_fills`/`_lines`, `FillCount+li` flatten, lying comments | Med |
| **B. `MeshData` result** | replace bespoke `LayerMeshData` + hand-rolled 4-stream consume with `Mesh.MeshData` (still written on the managed thread pool); one `MeshData` per `(tile,layer)` | two-array `TessellationResult`, manual consume copy | Med |
| **C. Per-`(tile,layer)` produce** | fan out tessellation per render-layer; remove the 5c full-width sparse union; progressive tile fill | decision 5c, tile-atomic produce | High |
| **D1. Burst kernels** | port `Earcut`/`PolygonAssembler`/line-expansion to `Unity.Collections` `[BurstCompile]` jobs in `MapRenderer.Jobs`, validated **purely against the managed Core oracle** — **zero lifecycle change**, does not touch live tessellation | (nothing yet — parity harness only) | Med (isolated) |
| **D2. Burst lifecycle** | introduce the `GeometryBucket` boundary; swap the managed builders for the D1 jobs; rewire tessellation lifecycle `UniTask→JobHandle` + deferred-drain pen | managed tessellation, Core geometry in the hot path | **Highest** |

**D is split deliberately:** D1 answers "is the Burst earcut correct?" against the oracle with no risk to
the running pipeline (it can even start early — it touches no live code). D2 answers "does the new lifecycle
work?" Bundling them would conflate two independent failure modes. C and D2 both rewire the tessellation
lifecycle and may land together.

**Scope note — what postdates the "full Burst" decision.** A + B deliver the goal actually stated
("extensible, not a mess"): the single ordered `IRenderLayer` list, the collapsed result, no fills-then-lines
drift. **C + D are performance + a tile-lifecycle rewrite** — and D's true cost (porting the entire managed
Core geometry stack to Burst/Collections, plus the core-tests tension in §3.3) was only discovered *after*
the decision. D2 rewires the S48/S84 machinery, historically the source of multi-hour failures. So A+B vs
C+D2 is a legitimate "now vs follow-up" call to make with that cost visible — see §6.

### Acceptance teeth
- **A / B / C** are behaviour-preserving: all 898 EditMode green; `Visual/` snapshots **identical**
  (existing tolerance). Interleaving is already pinned by `LayerOrderSnapshotTests` (line-then-fill AND
  fill-then-line) + `LitLine_CoplanarFillAndLine_NoZFighting` — that's the ordering safety net.
- **D1** adds a **differential oracle test**: Burst kernel output ≡ managed Core geometry output over the
  committed fixture. This holds **only if the port is a faithful double-precision transliteration** — so D1
  explicitly *excludes* float/SIMD reformulation (that's a separate, tolerance-gated change later). A
  shallow/wrong port cannot pass.
- **D2** is gated by the oracle (D1) **plus** snapshots-within-tolerance (a lifecycle/scheduling change may
  perturb sub-pixel timing but not geometry).
- **Snapshot-coverage gate (stage 0):** before touching a path, confirm a snapshot exercises it; the audit
  above shows fill, line, interleaving, and all three backends are covered — fill any gap found (e.g.
  zoom-dependent line, data-driven fill under BRG) as the first commit, not a footnote.
- No new `_field` + separate-getter pairs; no `IsInitialised`-style lifecycle flags; no fills-then-lines
  index arithmetic outside a single owner.

## 5. Risks / open questions

1. **Earcut in Burst is non-trivial.** It's a linked-list algorithm; a `NativeArray`-index port + no
   allocations is real work. The managed oracle (3.3a) de-risks correctness.
2. **`Mesh.AllocateWritableMeshData` is main-thread**; writing is off-thread/in-job; apply is main-thread.
   The schedule/complete choreography must interleave with the existing per-frame pump and S87 budget.
3. **Decode→tessellate handoff** chains two async primitives (`UniTask` decode → `JobHandle` tessellate)
   per record. Needs a clean state machine in `TileManager` (the current one is UniTask-only).
4. **Core geometry disposition** — keep as oracle vs delete. Recommend keep-as-oracle, decide later.
5. **Data-driven paint** (per-feature width/color) is evaluated managed-side into `FeatureMeta` in the
   bucket, then consumed in-job — confirm all data-driven inputs can be reduced to blittable per-feature
   values at the boundary (they should: they end up as vertex attributes anyway).

## 6. Scope decision — LOCKED: all-in, D now

**Decided (2026-07-01): one epic, A → B → C → D1 → D2 — Burst included now** (option (i)). D1 (the
oracle-validated kernel port) still runs as an isolated step and may start early, but D2 (the lifecycle
rewire) lands in this epic, not as a follow-up. The end state is the full Burst target. The rationale below
is kept for the record.

---

Everything is designed; one call remains — **does D (Burst) ride in this epic, or land as a fast follow-up?**

- **A + B + C** achieve the stated goal — an extensible, single-ordering render-layer pipeline that isn't a
  mess and cleanly admits new layer types. All behaviour-preserving, snapshot-gated. This is the "make it
  right" the user asked for.
- **D1 + D2** are the performance upgrade (real Burst tessellation) — and carry the costs only surfaced
  *after* the "full Burst" call: porting the whole managed Core geometry stack to `Unity.Collections`, the
  core-tests-vs-Collections tension (§3.3), and rewiring the S48/S84 tessellation lifecycle (§3.4).

Two viable shapes:
- **(i) One epic, A→B→C→D1→D2.** Everything lands together; D1 can begin early (oracle-only). Longest, most
  risk concentrated, but the end state is the full target.
- **(ii) A→B→C now; D1 in parallel/early as an isolated oracle-validated port; D2 as a separate follow-up
  once D1 is proven.** Gets the architecture-correctness win banked and de-risked; defers only the
  lifecycle rewire. **Recommended** — it keeps the highest-risk change (D2) off the critical path without
  losing the Burst end state.

Recommendation: **(ii)**. Same destination; the risky lifecycle rewrite doesn't block the win the user
actually wants, and D1's correctness is proven standalone before D2 touches the tile loop.
