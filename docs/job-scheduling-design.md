# Job scheduling — a real job graph over the Burst work

**Status:** designed 2026-09-02, revised the same day after a dual-arm review, nothing landed. Design SSOT
for turning the project's Burst job *library* into a job *graph*: how a multi-stage tile build is expressed
as `JobHandle` dependencies instead of call order, who owns the handles and the native buffers across the
chain, how completion reaches the main thread, and what happens to the managed-closure seam
(`IWorkScheduler`). Clean-room: derived from Unity's job-system contract and from this repo's own
measurements; no other renderer's source was read.

**Read first:** `docs/web-target.md` §"Measured in THIS project" (the measurement this design answers),
`docs/async-architecture.md` §"Disposal & cancellation contract" (the lifetime contract this extends),
`docs/gc-and-allocation-design.md` §5 (why off-main scratch is `Persistent`), and
`docs/tile-pipeline-design.md` §4 (the two-phase kick plan this design now partly owns — §3.6 below says
which parts).

---

## 0. The decisions, up front

| question | decision |
|---|---|
| how a chain is expressed | as `JobHandle` edges: every stage takes `deps` and returns a handle; a per-layer *graph builder* returns `(outputs, terminal handle)`; per-layer graphs fan in by `JobHandle.CombineDependencies` to one per-tile handle. No bespoke DAG type — `JobHandle` *is* the declaration. Whether the builders are per-layer or fused per-tile is decided by the stage-0 probe (§8). |
| who owns handles and buffers | the struct that owns the output buffers owns the terminal handle, indivisibly (`TileMeshBuffers.PipelineHandle` already has the slot; it is `default` today). Intermediate scratch is disposed *inside* the graph via `Dispose(JobHandle)` nodes, so the caller sees only outputs + one handle. |
| where the graph is scheduled | on the main thread, in the tile pump — the job system's `Schedule` contract. Off-main managed code never schedules; it produces native inputs the main thread schedules over. |
| how completion reaches the consumer | polled, once per Tick, on `JobHandle.IsCompleted`, at the same pump that already polls `WorkHandle.IsCompleted`. No callback, no UniTask hop. When `IsCompleted` reads true the pump calls `Complete()` **before** touching any output — `IsCompleted` is a wait-free check, not a release of the safety system's registration. |
| the tile build's shape | three polled steps per tile: **prologue** (managed, `IWorkScheduler`, shrinking) → **measure graph** (Burst: all geometry + the chunk plan) → **write graph** (main allocates one exact-size `MeshData` **per chunk**, Burst streams into it). |
| `.Run()` vs `.Schedule()` | decided by the discriminator in §4 — call-site placement first, then latency tolerance, then span — not per site. Applied in §4.2: the ten tile-build `.Run()` sites become graph nodes once their call site moves to the pump; the five per-frame symbol `.Run()` sites stay; the two `.Run()`s inside managed worker bodies (decode, per-feature filter) stay until those bodies are jobs. |
| `IWorkScheduler` | survives, shrunk to the still-managed bodies (prologue, decode, two symbol sites) and deleted from a site the moment that site's body is a job. Its XML doc's stated destination (`.Run()`) is corrected to *"a graph node scheduled from the pump"*. |
| platform | one graph shape; zero `#if`. The single platform decision point stays `WorkSchedulerFactory`, for the residual managed bodies only. |
| cancellation | never disposes and never interrupts: an evicted tile's `(buffers, MeshDataArrays, handle, decode reference)` unit moves to the pen, which completes then disposes. The lifetime token gates the *next main-thread step*, never a running job. |
| safety | every graph edge is a container hand-off the Editor safety system can check; the one sub-slice exception (per-polygon parallel earcut, stage 6) is confined to one job type with a disjointness assertion. A missing edge is caught at `Schedule` time by an EditMode test that schedules the real graph. |

---

## 1. Why now — the assumption this replaces

The current concurrency design executes **managed lambdas**, on a ThreadPool thread (desktop) or inline on the
main thread (web). It was built when the web target was believed to have no off-main compute at all, so the
Burst pipeline was deliberately made *thread-agnostic* — every stage `.Run()`, callable from whatever thread
the lambda landed on — and the only source of parallelism was "whole tiles on ThreadPool threads".

`docs/web-target.md` records the measurement (2026-09-02, this project's own web player, Unity 6000.6.0f1 /
Burst 2.0) that falsifies the premise: Burst-compiled jobs **do** reach worker threads on the web (a serial
chain returned from `Schedule()` in 0.0 ms and completed 66 ms later on worker 3; an `IJobParallelFor` reached
five workers for ~3.6×), while every managed offload mechanism is still dead there. The web target's real
shape is **main thread + Burst workers, nothing else**.

Two consequences, and they are different:

- **Correctness:** a body in a job runs on every platform with no branch; a managed closure needs one to run
  on the web at all. This was already the stated direction (`web-target.md` §"The rule").
- **Performance:** the one execution resource the web has is unused. `.Run()` executes on the calling thread
  — on web, the main thread — while five workers sit idle. The `.Run()`-everywhere pipeline is not merely
  "serial"; it is *serial on the render thread*.

So the stated destination of `IWorkScheduler`'s own doc — "a Burst job struct dispatched `.Run()`" — is no
longer the destination. It was chosen because scheduling needs the main thread and the ThreadPool was the
off-main resource. Now the job system is the off-main resource, and the main thread is exactly where
scheduling belongs.

## 2. What exists — a library without a graph

Verified against source (2026-09-02):

- **Seventeen `[BurstCompile]` types, all in `MapRenderer.Jobs`** (`MapRenderer.Unity` carries none), plus
  two attribute sites for the diagnostic probe in `MapRenderer.App`. The fill path's *stages* are jobs —
  `RingSelectJob`/`RingClipJob` → `RingAssemblyJob` → `EarcutJob` (per polygon) → `TileToGeoJob` →
  `ProjectPointsJob<TProj>` → `GlobeFillSubdivideJob<TProj>` — but the work **between** those stages is not
  (next bullet). The line path has `TileToGeoJob` + `LineRibbonJob`; decode has `MvtDecodeJob`; symbols have
  six per-frame jobs.
- **Managed work sits between the fill jobs, on the calling thread.** In `FillMeshPipeline.Schedule`:
  the `maxRingLen`/`totalVerts` pre-pass over the visit order (`:619–627`); the earcut sizing loop that
  builds the four offset tables (`:318–353`); the **populate pass** (`:384–421`) — per polygon, the hole-ring
  sort through `HoleRingComparer` (`(leftmost-x, min-y, ring-index)`, a total order the parity tooth depends
  on) and an O(V) copy of outer + hole vertices into the flat buffers; and the **aggregate** (`:498–519`) —
  an O(V+I) concat with index rebasing. Plus the count read-backs (`polyCountArr[0]`, per-polygon
  `perPolyMergedVC`) that size the next stage. `StyledFillTileBuilder.WriteGeometry`'s stream copy into
  `MeshData` (`:470–503`) and `StyledLineTileBuilder`'s per-point `ProjectPoint` loops, `SubdivideCenterline`
  and ribbon copy are managed the same way. **A classification that only looks at `.Run()` sites cannot see
  any of this** — §4.2 therefore classifies *dispatch sites*, and §3.2 lists the between-job work as nodes.
- **Seventeen `.Run()` job-dispatch sites and one real `.Schedule()`** — ten on the tile-build path, two
  inside managed worker bodies, five per-frame in symbol placement (the brief's recon said twelve; the count
  above is from a fresh grep and is the one §4.2 classifies). The `.Schedule()` is
  `SymbolPlacementSystem.ScheduleCollision`,
  completed one frame later — the repo's only cross-frame `JobHandle`, and the exemplar this design
  generalises). `FillMeshPipeline.Schedule(LayerInput)` is *named* Schedule and returns fully computed
  buffers with `PipelineHandle = default`.
- **Dependency ordering is call order.** Inside `FillMeshPipeline.Schedule`, stage N+1's inputs are sized from
  stage N's outputs read back on the calling thread. That read-back, and the managed passes above, are what
  make the chain synchronous — not any stage's nature.
- **The seam:** `Concurrency/IWorkScheduler` (`Schedule<T>(Func<CancellationToken,T>)`), `WorkHandle<T>`
  (over a `UniTaskCompletionSource`), `WorkSchedulerFactory` (one `#if`), two policies. Five production
  consumers (§5).
- **The tile pump** (`TileManager.PumpPending`) is already a per-Tick polled state machine over
  `LoadedTile`: fetch in flight → `HasMeshBuild && !IsCompleted` → consume under budget. The mid-flight
  release pen (`_pendingDisposal`) and the teardown drain already exist for `WorkHandle`s.
- **Main-thread anchors** in a build today: `Mesh.AllocateWritableMeshData` at kick (one blind `MeshDataArray`
  per layer, sized later by the worker's `SetVertexBufferParams`) and `ApplyAndDisposeWritableMeshData` at
  consume.
- **A live plan for the same seam:** `docs/tile-pipeline-design.md` §4 ("Two-phase kick + mesher vertex
  cap", PENDING) plans a measure/write split with per-chunk meshes. §3.6 states what this design takes over
  from it.

The gap is exactly one thing: nobody holds an uncompleted `JobHandle` across a Tick for a tile build.

## 3. Target architecture

### 3.1 The tile build as three polled steps

```
fetch (UniTask) ──▶ decode (managed, IWorkScheduler; unchanged) ──▶ SharedDisposable<IDecodedTile>
                                                                            │  pump observes completion
                                                                            ▼
 ┌─ PROLOGUE ───────────────────────────────── managed, IWorkScheduler (pool / inline) ─┐
 │  per layer: feature selection, paint bake (colour/opacity/width), sort-key order,     │
 │  the symbol worker pass → NATIVE per-layer columns (RingVisitOrder, colours, widths)  │
 └───────────────────────────────────────────────── WorkHandle<PrologueResult>, polled ──┘
                                                                            │  IsCompleted
                                                                            ▼
 ┌─ MEASURE GRAPH ─────────────────────────────── Burst, scheduled on main, polled ──────┐
 │  per fill layer:  select/clip → assemble → size → gather → earcut → aggregate         │
 │                   → tile→geo → project [→ globe subdivide] → chunk plan               │
 │                   ⇒ NativeLists + counts + chunk descriptors                          │
 │  per line layer:  tile→geo → up → subdivide → tile→geo → project → ribbon → chunk plan│
 │  fan-in: CombineDependencies(all layers) = the tile's terminal handle                 │
 └────────────────────────────────────────────────── TileBuildGraph.Handle, polled ──────┘
                                                                            │  IsCompleted → Complete()
                                                                            ▼
 ┌─ WRITE GRAPH ───────────── main allocates one exact-size MeshData PER CHUNK, then Burst ┐
 │  per chunk: AllocateWritableMeshData(1) + SetVertexBufferParams(chunkVerts)            │
 │  → StreamWriteJob(chunk range → MeshData views; indices rebased; winding swap)         │
 └────────────────────────────────────────────────── TileBuildGraph.Handle, polled ──────┘
                                                                            │  IsCompleted → Complete()
                                                                            ▼
                         consume (main, budgeted, unchanged): Apply + AddTileLayer per chunk
```

**Why three steps and not one.** (a) The job system schedules from the main thread, so the graph cannot be
built from inside the ThreadPool body that runs the prologue today; the prologue must *finish* before the
pump can schedule over its outputs. (b) The `MeshData` stream views a write job fills only exist after
`AllocateWritableMeshData(1)` + `SetVertexBufferParams(count)`, and both the *number* of chunks and each
chunk's count are measure-graph outputs — so allocation and sizing sit on the main thread *between* two
graphs. That split is the two-phase kick `docs/tile-pipeline-design.md` §4 already planned for a different
reason (blind allocation, stall #5; consume overshoot, stall #4); this design is what makes it necessary
rather than merely desirable. §3.6 says which of §4's parts this design now owns.

**What it costs — decided, not open (maintainer, 2026-09-02).** Up to two extra Ticks of latency per tile
(prologue completes Tick N; measure scheduled N, observed N+1; write scheduled N+1, consumed N+2) is
**accepted as a standing cost**. It is invisible against network fetch and the same order as the one-frame
deferral the symbol collision already accepted. The uniform three-step path stands for every tile; a
small-tile fast path (prologue on main at kick) is a later knob, revisited only if a trace shows tiles
arriving visibly late relative to fetch.

**What shrinks over time.** The prologue is the entire remaining reason `IWorkScheduler` exists on the tile
path. Each managed step that becomes a job moves from the prologue into the measure graph
(selection → the native filter VM as one job over the layer's feature range; paint bake → a job once the
native expression VM covers paint properties). When the prologue is empty, the tile path has no managed body
and the pump schedules the measure graph directly from the decode. **On the web the prologue runs inline on
the main thread before and after this design**; what leaves the main thread there is the geometry work
(§3.2's node list), not the prologue. Its nativization is where the rest of the web payoff lands (§9,
measurement 4).

### 3.2 The graph as a value — who owns what, and every node

Three rules, all consequences of one fact: **a native container is disposed only by its owner, only after
every job that references it has been `Complete()`d by that owner.**

1. **A graph builder returns `(outputs, handle)` as one struct.** `FillMeshGraph.Schedule(in LayerInput,
   JobHandle deps) → FillGraphOutput` where the struct holds the output `NativeList`s / `NativeArray`s, the
   chunk descriptors and `Handle`. `TileMeshBuffers` already has `PipelineHandle` and the doc line *"Complete()
   before reading output; never dispose while jobs are in flight"* — the type was designed for this and the
   implementation regressed to synchronous. `FillGraphOutput` is the natural moment to stop carrying the seven
   `TileMeshBuffers` fields the fill path leaves `default` today (`RingOffsets`, `RingFeatureIdx`,
   `PolyOuterRingIdx`, `PolyHoleListStart`, `PolyHoleCount`, `HoleRingIdxs`, and the assembly-stage counts).
   The struct's `Dispose()` is defined as `Handle.Complete(); free everything`.
2. **Scratch never leaves the graph.** Every intermediate buffer (offset tables, earcut scratch columns, the
   geodetic array) is freed by a `buffer.Dispose(lastReader)` node scheduled inside the builder, and the
   returned handle includes those dispose nodes. `Complete()` on the terminal handle therefore guarantees
   all scratch is freed; the owner never enumerates it.
3. **Sizing and every between-stage pass happens inside the graph, not on the calling thread.** The
   managed passes §2 lists each get a node or are absorbed:

| today (managed, calling thread) | in the graph |
|---|---|
| `maxRingLen`/`totalVerts` pre-pass sizing the clip's ping-pong scratch and output capacities | **absorbed**: `RingClipJob` allocates its ping-pong scratch as `Allocator.Temp` locals inside `Execute` (the job-scratch rule in `lessons-learned.md`) sized from the visit order it already walks; output lists grow themselves |
| assembly count read-back (`polyCountArr[0]`) → earcut scratch sizing loop (offset tables) | **`FillSizingJob`** (serial `IJob`): reads the assembly's polygon descriptors, writes the four offset tables and `Resize`s the flat scratch `NativeList`s (a single-threaded job may resize a list it owns); asserts the tables are strictly increasing and sets the error flag otherwise |
| populate pass: hole sort via `HoleRingComparer` + outer/hole vertex copy + `perPolyFeatureIdx` capture | **`FillGatherJob`** (serial `IJob`): `NativeSortExtension.Sort` with the same struct comparer (Burst-compatible). Determinism does not rest on the sort algorithm: the comparer's final `ring-index` key makes it a **total order**, so any correct sort yields the managed order. This is the **first parity risk to RED-verify** (perturb the tie-break → parity reds). |
| per-polygon `EarcutJob.Run()` in a loop, slices taken on the calling thread | **`EarcutBatchJob`** (serial `IJob`, stage 1): holds the flat columns + offset tables, loops polygons inside `Execute`, takes each polygon's `GetSubArray` views there and calls `EarcutJob.Execute()` directly — `EarcutJob`'s fields are untouched, so its two existing callers (`FillMeshPipeline`, `BurstJobRunOffMainSpikeTests`) stay valid. Stage 6 converts this one job to `IJobParallelForDefer`. |
| per-polygon merged-count read-back + aggregate (concat + index rebase) | **`FillAggregateJob`** (serial `IJob`): prefix sums over `perPolyMergedVC`/`perPolyIdxCount`, then the concat; output lists resized by the job |
| `TileToGeoJob.Run(n)`, `ProjectionDispatch.Run` | the same jobs over `AsDeferredJobArray()` views (length resolved at execution); `ScheduleParallel` in stage 6 |
| `GlobeFillSubdivideDispatch.Run` | `GlobeFillSubdivider.Schedule` node (serial, budgeted `NativeList` appends) |
| — (new) | **`ChunkPlanJob`** (serial `IJob`): walks `VertexFeatureIdx` (per-feature vertex runs are contiguous) accumulating features until the next would cross the chunk target (32 768, `tile-pipeline-design.md` D6); a single over-target feature ships as one oversized chunk. Emits `(vertexStart, vertexCount, indexStart, indexCount)` per chunk. |
| `WriteGeometry`'s stream copy loops (position/normal, UV, tangent, colour, index winding swap) | **`FillStreamWriteJob`** in the write graph, one per chunk, indices written as `global − chunkVertexStart` |

The never-fired `EnsureCapacity` backstops cannot throw inside Burst; inside a graph they become a
`NativeReference<int> Error` the write step reads — non-zero ⇒ the layer settles as zero-vertex and logs,
the same shape as a faulting processor today. **Recorded divergence:** `MvtGeometryMaterializer`'s twin
backstop (named at `FillMeshPipeline.cs:275`) stays on the throw shape, because it runs inside the managed
decoder where a throw is observable; the two backstops therefore differ in mechanism until the decoder is a
job. (The flag is *more* observable than today: a sizing job can be fed an undersized capacity in a unit test
and the flag asserted — a path the synchronous throw could never reach.)

**Per tile**, `TileBuildGraph` (class, `MapRenderer.Unity/Rendering/Tile/Processing`) owns: the tile's
terminal `JobHandle`; one `FillGraphOutput`/`LineGraphOutput` per layer; the prologue's native columns (graph
inputs); the kick's own `SharedDisposable<IDecodedTile>` reference (the decoded buffers are `[ReadOnly]` graph
inputs, so the reference is released only after `Complete()` — the Editor safety system throws if the buffers
are disposed under a live reader, which is the tooth); and, in the write step, one `MeshDataPayload` per
chunk. It replaces `MeshBuildResult`. `LoadedTile` gains a `BuildStep` (`Prologue | Measure | Write`) beside
the existing flags; `HasMeshBuild` becomes "has a build step in flight". Every reader of `MeshBuildTask`
takes the extension — `PumpPending`, `DrainMeshBuilds`, `AwaitInFlightMeshBuilds`, `CaptureTelemetry`'s
backlog count, `RenderTeardownRecord`'s pen stash and `DoDispose` — not only the pump.

**Per layer**, the graph builders live where the jobs live: `MapRenderer.Jobs` for the format-neutral geometry
graphs (`FillMeshGraph`, `LineMeshGraph`, `ProjectionDispatch.Schedule`, `GlobeFillSubdivider.Schedule`,
`ChunkPlanJob`); `MapRenderer.Unity/Rendering/Meshing` for the stream-write jobs, which own the vertex-stream
layout and the `MeshData` boundary. Placement follows ARCHITECTURE.md §2; nothing new goes to Core.

### 3.3 Completion and the UniTask boundary

**Polled at the pump; `Complete()` before any read.** The job system offers no completion callback, and a
UniTask bridge (`UniTask.WaitUntil(() => handle.IsCompleted)`) is a poll wearing an awaiter. The pump already
polls `WorkHandle.IsCompleted` per Tick; `JobHandle.IsCompleted` is a non-blocking flag read and slots in
beside it. **`IsCompleted == true` only says the wait would return at once; it does not release the job's
registration on the containers' safety handles — only `Complete()` does.** So every step transition is
`IsCompleted` → `Complete()` (wait-free at that point) → read outputs / allocate `MeshData` / upload. A
transition that reads an output before `Complete()` throws in the Editor and is undefined in a player.

The bridge into the UniTask I/O chain is unchanged: the fetch→decode chain still ends in a
`UniTask<SharedDisposable<IDecodedTile>>` the pump observes, and no `Task`↔`UniTask` interop is introduced
anywhere. `WorkHandle.ToUniTask()` stays for the prologue's deterministic drains.

**`ScheduleBatchedJobs()` once per Tick, after every step transition and kick.** A job scheduled from the
main thread is not handed to workers until the batch is flushed or an implicit sync point arrives — the
symbol collision's `ScheduleBatchedJobs()` call exists for exactly this. Both the measure kick and the
measure→write transition schedule from the same `PumpPending` pass, so the flush is one call at the end of
that pass, never per tile.

**Blocking drains.** Two, both bounded because jobs are finite CPU: the deterministic test settle
(`DrainMeshBuilds`) calls `Complete()` on each in-flight step in order; teardown calls `Complete()` on
everything in the pen. `Complete()` from the main thread executes a not-yet-started job inline, so neither
drain has a PlayerLoop dependency to dead-end on — the same property the `WaitOffPlayerLoop` parks needed,
by a simpler mechanism. No timeout is needed for the graph pens (unlike the 10 s cap on `WorkHandle` drains,
which guards a managed body that may never run).

### 3.4 Where the wall-clock win is — off-main versus parallel

The measurement is explicit that a chained sequence still costs its full serial time off-main. Two distinct
wins, sourced differently:

| win | mechanism | who gets it |
|---|---|---|
| **the main thread stops paying for geometry** | every node in §3.2 leaves the calling thread; on web that thread is the render thread. The prologue (§3.1) does *not* leave it on web until nativized. | web (large); desktop already had it via the ThreadPool |
| **across tiles** | N tiles' graphs in flight fan out over workers with no code — the job system does it | web (new: 5 workers); desktop (unchanged in kind: pool threads → job workers) |
| **within a stage** | `IJobParallelFor` / `IJobParallelForDefer` where the stage is element-wise | both; new on both |

Stage-by-stage, what is parallelisable and what is serial by nature:

| stage | unit | parallel? | note |
|---|---|---|---|
| decode (`MvtDecodeJob`) | source layer | across layers only | serial per layer (sequential command stream); stays inside the managed decoder until that nativizes |
| select / clip | fill style layer | across layers | serial per layer — appends to `NativeList`s |
| ring assembly | layer | across layers | serial — writes counts, per-feature sign state |
| sizing, gather, aggregate, chunk plan | layer | across layers | serial prefix sums / copies, O(polygons) or O(V) |
| **earcut** | **polygon** | **yes** (stage 6) — `IJobParallelForDefer` over the polygon descriptor list, each on its disjoint slice | the one real intra-layer win; needs the sub-slice safety exception (§7) |
| tile→geo, project | vertex | **yes** — already `IJobParallelFor`, today `.Run(n)` single-threaded | `ScheduleParallel` over a deferred array (stage 6) |
| globe subdivide | layer | across layers | serial — budgeted `NativeList` appends |
| line subdivide + ribbon | ring | across rings once ribbon offsets are pre-sized from subdivide counts | two-pass (subdivide → size → ribbon), serial per ring first |
| stream write | vertex | yes | trivial parallel-for into `MeshData` views |

Determinism under parallelism holds by construction: parallel stages are element-wise or write disjoint
pre-sized slices, and the one sort is over a total order, so output is independent of which worker ran
what. That is what lets every migration stage keep **byte-identical snapshots** as its invariant.

### 3.5 One platform

One graph shape, zero `#if`. The job system is the same API on both targets; what differs is
`JobsUtility.JobWorkerCount` (read, never branched on). The single platform decision point remains
`WorkSchedulerFactory.ForCurrentPlatform()` and it governs only the residual managed bodies — it disappears
with the last of them. The only web-specific *risk* is a Burst-off build, where jobs execute inline inside
`Schedule()` on the main thread (a performance cliff, not a blank map); `RuntimeDiagnostics` already detects
Burst-off at startup, and §7 adds a per-graph worker-index sample so the cliff is visible in telemetry rather
than inferred from frame time.

### 3.6 Relationship to `docs/tile-pipeline-design.md` §4 — what this design now owns

§4 there is a live PENDING plan for the same seam (two-phase kick, per-chunk meshes, `PreparedTileCache`
ripple). The maintainer decided (2026-09-02) that the write graph emits **per-chunk `MeshData` from the
start**: the chunk plan is a pure function of `VertexFeatureIdx`, which the measure graph already produces,
so it is nearly free here and expensive to retrofit — and it closes stall #4 (consume overshoot bounded by
one *feature*, not one layer) rather than deferring it. Consequences, stated so neither document claims the
other's work:

| §4 part | owner now | how |
|---|---|---|
| §4.1 `LoadedTile` two-phase state (`MeasureTask`/`HasMeasure`; the write step charged to `MaxMeshBuildsPerTick`) | **this design** | `BuildStep` is that state with a third value for the prologue; the write step is charged to the same budget, one write kick = one budget unit |
| §4.2 `ILayerGeometry` replacing `IRenderLayer.WriteInto` — a managed `Measure(IReadOnlyList<ITileFeature>, …)` / `WriteChunk(chunk, MeshData, …)` seam | **superseded by this design** | the graph builder + `ChunkPlanJob` + `FillStreamWriteJob` are the measure/write split; there is no managed mesher interface in the middle. `IRenderLayer.WriteInto` is retired when its last caller (the prologue's line/extrusion path, stages 3→5) is a graph. |
| §4.3 exact-size per-chunk allocation, `MeshDataArraysAllocatedLastKick`, consume/backend needing no change | **this design** | stage 2 allocates per non-empty chunk; the counter tooth is adopted verbatim |
| §4.3 D7 — `PreparedTileCache` value `Mesh` → `Mesh[]`, `TransferOnRelease` grouping by `MaterialIndices[i]`, `TakeAsLoadedTile` registering each chunk | **this design**, stage 3 | the first stage at which a cached layer can be K > 1 chunks; `Put` destroys the previous entry on key collision, so the value-shape change must land with it |
| §4's overshoot tooth (≥100k-vert fixture across ≥4 features, `MaxVerticesPerTick = 40 000` ⇒ per-tick consume ≤ 40 000 + 32 768, ≥3 meshes) | **this design**, stage 3 | adopted verbatim |
| §4's prerequisite "capture `PmMeshDataAllocate` numbers first" | **moot** | allocation becomes exact regardless of what the numbers say |
| the chunk target 32 768 (D6, serialized constant) | unchanged | read from the same config |

Nothing in §4 is delivered "for free": stall #5 falls out of exact sizing; stall #4 is the chunk plan job
and the cache ripple, both explicit stages here. `tile-pipeline-design.md` §4 should be annotated to point
here when the substrate stage lands (the annotation is that doc's owner's edit).

## 4. The dispatch discriminator — `.Run()` or `.Schedule()`

The discriminator classifies a **call site**, and the first question is about the site's placement, not
the platform. Apply in order; stop at the first answer.

1. **Is this call inside an `IWorkScheduler` body** — i.e. not guaranteed to be on the main thread?
   → **`.Run()`.** The job system schedules from the main thread only (its documented contract; §9
   measurement 7 confirms it once), and an in-thread `.Run()` is the cheapest thing a worker can do. A
   property of the code's placement, so it answers the same on every platform — under
   `InlineWorkScheduler` the body happens to be on main, and `.Run()` is still the only correct dispatch
   there. *Transitional: this branch empties as bodies become jobs and their call sites move to the pump.*
2. **Can the consumer tolerate seeing this output on a later Tick?** (Is the consumer already a polled
   state, or could it become one — as the collision verdict did?) → **`Schedule(deps)`.** The handle joins
   the owner that polls it; `Complete()` is called only after `IsCompleted` is true, or in a bounded drain.
3. Otherwise the call is frame-synchronous on the main thread. **Is it an `IJobParallelFor` whose measured
   serial span exceeds the fan-out cost?** → **`ScheduleParallel(...).Complete()`** — the main thread blocks
   but wall time drops. Otherwise → **`.Run()`**: for a serial job, or a small one, `Schedule().Complete()`
   only adds a hand-off. Before settling here, ask whether the consumer could become one frame late; if it
   can, go back to 2.

A contributor adding a site answers three yes/no questions; the answer is not a list to look up. The
migration's work, in these terms, is **moving call sites out of rule 1** — a tile-build stage answers
`.Run()` today (it sits inside `KickMeshBuild`'s body) and `Schedule` once its call site is the pump.

### 4.1 What the discriminator says about the existing argument for `.Run()`

`SymbolPlacementSystem.ProjectSymbols` argues (its own comment) that "the caller blocks here regardless, so
an inline `.Run()` is strictly cheaper than `Schedule().Complete()`". That is rule 3 stated for the serial
case, and it is right *unless* the parallel-for's span is large enough to fan out — which nobody has
measured (§9, measurement 2). Until measured, `.Run()` stands.

### 4.2 Every existing site, classified — today and after its call site moves

| site | today | rule today | after migration | rule after |
|---|---|---|---|---|
| `FillMeshPipeline` — select/clip, assembly, earcut ×N, tile→geo (5 `.Run`) + `ProjectionDispatch.Run` | synchronous chain inside `KickMeshBuild`'s body | 1 → `.Run()` | nodes of `FillMeshGraph`, scheduled by the pump | 2 → `Schedule` |
| `GlobeFillSubdivider.Run` (via `GlobeFillSubdivideDispatch.Run` from the fill and extrusion builders) | same | 1 | `GlobeFillSubdivider.Schedule` node | 2 |
| `StyledLineTileBuilder` — tile→geo ×2, ribbon (3 `.Run`) + two managed `ProjectPoint` loops + `SubdivideCenterline` | synchronous per ring inside the body | 1 | nodes of `LineMeshGraph` | 2 |
| `StyledFillTileBuilder`/`StyledFillExtrusionTileBuilder` stream-write loops (managed) | worker write into `MeshData` inside the body | — (managed) | `FillStreamWriteJob` in the write graph | 2 |
| `MvtGeometryMaterializer` — `MvtDecodeJob.Run()` | inside the managed decoder's body | 1 | unchanged until the decoder is a job (deferred) | 1 |
| `NativeFilterEvaluator.Evaluate` — one `.Run()` per feature | inside the managed selection loop (prologue body) | 1 | unchanged until selection is one job over the feature range (stage 7), at which point the site is a graph node | 1 → 2 |
| `SymbolPlacementSystem` — gather, compact, projection, cull, stage (5 `.Run`) | frame-synchronous on main | 3 → `.Run()` | unchanged; projection/cull are candidates for `ScheduleParallel().Complete()` pending measurement 2 | 3 |
| `SymbolPlacementSystem.ScheduleCollision` | `.Schedule()`, completed next Tick | 2 | unchanged — the exemplar | 2 |
| `RuntimeDiagnostics` Burst probe | function pointer | — | diagnostic; unchanged | — |
| BRG `OnPerformCulling` | returns a `JobHandle` to Unity | — | backend; out of scope | — |

## 5. The seam — what happens to `IWorkScheduler`, `WorkHandle<T>`, `WorkSchedulerFactory`

**It survives, shrunk, and is deleted per site rather than replaced.** The interface's own doc already says
it is a bridge; what changes is the *destination* it names. A managed body cannot be a job, and on the web
a managed body runs inline on main whatever wraps it — the seam is the honest expression of that, and
nothing about the graph changes it. What the graph changes is that the seam stops being the thing that
*carries the Burst work*: the prologue produces native inputs and returns; the Burst work is scheduled by the
pump over those inputs. The seam therefore never wraps a job.

The five consumers, and each one's path:

| site | body | managed captures (disqualifying for a job) | path |
|---|---|---|---|
| `TileDecodeDispatch.DecodeAsync` | managed protobuf walk → per-layer `MvtDecodeJob.Run()` + property stores | `ITileDecoder`, `byte[]`, managed strings/dictionaries | **stays** on the seam until the decoder is nativized (deferred). On web this is main-thread cost today and after this design. |
| `TileManager.KickMeshBuild` | select + paint bake + sort-key + `WriteInto` (Burst chain + managed stream write) + symbol pass | `_layers` snapshot, `IFeature`s, expression evaluators, `SharedDisposable`, `TileBuildScratch` | **splits**: the managed half becomes the prologue (still on the seam, shrinking); the Burst half becomes the measure + write graphs scheduled by the pump. |
| `TileManager.KickSourcelessBackground` | mint a 4-corner quad → `WriteGeometry` | none of consequence (a trivial managed materializer) | **leaves the seam first**: the quad's prologue is small enough to run on the main thread at kick; the graph is scheduled directly. The first production client of the substrate (§8). |
| `SymbolSubsystem.PumpBuilds` parked drain | symbol extraction (`ExtractLayers`) → main-thread shaping tail | `StyledSymbolTileBuilder`, style layers, `SymbolTileBuffer`, sprite atlas | **stays**; symbol internals are out of scope. |
| `SymbolSubsystem.ScheduleReconcileIfDirty` | `SymbolReconciler.Run` — a `Dictionary` dedup | the reconciler, snapshots | **stays**; out of scope. |

`WorkHandle<T>` stays as the prologue's handle. `WorkSchedulerFactory` stays as the one `#if`. When the
prologue is empty, `TileManager` has no use of the seam and drops its `WorkScheduler` seam and the
`MeshBuildGateForTest` mutual-exclusion guard with it; the seam then serves decode and symbols only, and its
doc says so. **What replaces the gate's capability** — holding a build genuinely in flight so teardown can be
exercised against it (`TeardownCancelInflightBuildsTests`, and the mutual-exclusion tooth in
`MeshBuildWorkSchedulerTests`) — is a test-only *delay job*: a bounded busy-loop `IJob` prepended to the
graph through a test seam on `TileBuildGraph`. A Burst job cannot park on a `WaitHandle`, but it can spend a
bounded, fixture-chosen number of iterations, which holds the handle incomplete across the teardown call.
The mutual-exclusion tooth retires with the gate; the delay-job seam needs no exclusion because it is
policy-independent.

The doc corrections that land with the substrate stage: `IWorkScheduler`'s XML (destination = a graph node
scheduled from the pump), `docs/web-target.md` §"The rule" (add: *and a job that can tolerate a later Tick is
`Schedule`d, never `.Run()`*), `docs/meshing-design.md` §"Threading & lifetime" (the `.Run()`-on-the-worker
sentence), the `tile-pipeline-design.md` §4 pointer (§3.6).

## 6. Disposal and cancellation — the invariant

`docs/async-architecture.md`'s contract stands; this extends it to buffers a scheduled job may still be
reading. The one rule, stated once:

> **A native buffer (`NativeArray`/`NativeList`, and the `Mesh.MeshDataArray` a write job fills), the
> `JobHandle` of the last job that references it, and the reference that keeps its inputs alive are one
> indivisible ownership unit. They move together, and they are released in that order: `Complete()` the
> handle, then read or upload, then dispose the buffers, then release the input reference. Cancellation
> never disposes; it transfers the unit to the pen.**

The four exits, extended:

| exit | today (`WorkHandle`) | with graphs |
|---|---|---|
| (1) consumed | `GetResult()` → upload → dispose | `IsCompleted` at the pump → **`Complete()`** → upload each chunk → `TileBuildGraph.Dispose()` (buffers freed, decode reference released). Never a read before `Complete()`. |
| (2) released in flight | handle → `_pendingDisposal`; per-Tick poll; dispose on completion | `TileBuildGraph` → the pen; per-Tick poll on `Handle.IsCompleted`; then `Complete()` + `Dispose()`. Same pen, second element type. |
| (3) cancelled mid-work | the managed body checks the token and settles zero-vertex | **no in-job cancellation** — a scheduled job is finite and not interruptible, so there is nothing to cancel; the token gates the *next main-thread step* (the pump does not schedule measure after a cancelled prologue, does not allocate/schedule write after a cancelled measure, does not upload after a cancelled write). A cancelled unit goes to the pen like exit (2). |
| (4) teardown | cancel, then park up to 10 s per stashed handle | cancel (gates future steps), then `Complete()` every pen entry synchronously and dispose. Bounded by in-flight CPU; no timeout. Order unchanged: destroy meshes → dispose backend. |

Two consequences worth naming:

- **The decode reference is held longer.** Today the kick's own `SharedDisposable` reference is released in
  the body's `finally`, i.e. when the Burst chain finishes. With graphs it is released by
  `TileBuildGraph.Dispose()` after `Complete()` — at consume or in the pen. The window is the same in
  substance (the chain's duration); it is now expressed as ownership rather than as a `finally`.
- **`Allocator.Persistent` everywhere in a graph, as today** — `TempJob`'s four-frame guard counts
  main-thread frames and a build spans more (`gc-and-allocation-design.md` §5). Scratch disposed via
  `Dispose(handle)` nodes is still `Persistent`; the deferred-dispose node is what makes deterministic
  release possible without a main-thread read-back. A job's *own* transient scratch (the clip's ping-pong
  buffers) is the exception: `Allocator.Temp` locals inside `Execute`, freed when the job returns.

**Teeth for the invariant** (all RED-verified by injecting the defect, per the repo's rule):
`TileBuildGraph.DebugLiveCount` returns to zero after load → release-in-flight (at each of the three steps)
→ teardown, and after a faulted build; releasing the decode reference *before* `Complete()` makes the Editor
safety system throw at `Dispose` (a positive control, executed once); reading an output before `Complete()`
at a step transition throws the same way (positive control); the existing `NativeArray`/`Mesh` leak
baselines (`DisposalLeakGuardTests`, `TeardownCancelInflightBuildsTests`) run unchanged.

## 7. Safety — catching a dependency bug in a test, not in a wrong-looking web build

The Editor's job safety system (`ENABLE_UNITY_COLLECTIONS_CHECKS`, on in EditMode and stripped in players)
checks, at `Schedule` time, that a job touching a container another scheduled job writes declares that job
as a dependency, and at access time that the main thread does not read or dispose a container under a live
job. None of that exists on the web. The design makes the Editor's check *sufficient* by construction:

1. **Every graph edge is a container hand-off.** Stage N+1's dependency on stage N is carried by a
   `NativeArray`/`NativeList` field with a safety handle, and the `deps` handle passed to `Schedule`. A
   builder that forgets `deps` on an edge throws in the Editor at schedule — *if a test schedules the real
   graph*. Hence the substrate tooth: an EditMode test that runs `FillMeshGraph.Schedule` over the fixture
   with the job debugger on (the EditMode default), `Complete()`s, and hashes. Its RED check is executed once
   and recorded: replace one node's `deps` with `default` → `InvalidOperationException` at schedule.
2. **No `[NativeDisableContainerSafetyRestriction]` in a graph builder, and no
   `[NativeDisableParallelForRestriction]` before stage 6.** The single exception is per-polygon parallel
   earcut (stage 6), whose `Execute(polygon)` writes a pre-sized *slice* of shared flat columns and needs
   `[NativeDisableParallelForRestriction]` on those fields. It is confined to that job type; the sizing job
   asserts the offset tables are strictly increasing and non-overlapping (a plain `if` → the error flag,
   compiled into every build, exactly the `EnsureCapacity` posture); and its tooth is a determinism check,
   not a safety check. A structure test greps for both attributes and allows exactly the earcut batch job's
   fields.
3. **Determinism tooth.** Run the fill graph over the fixture corpus with `JobsUtility.JobWorkerCount = 0`
   and with the default worker count; assert identical content hashes, and identical to the managed-reference
   hash `JobifiedPipelineTests` already pins. A parallel decomposition that races produces a hash difference
   under contention; an element-wise one cannot. (A race can pass by luck; that is why rule 2 confines
   disabled restrictions to one place, and why the safety system — not this tooth — is the primary guard.)
4. **Worker-index sample — the instrument that tells "off-main" from "inline".** Each graph's last node
   records `[NativeSetThreadIndex]` into a `NativeReference<int>`; telemetry counts graphs that completed on
   thread 0 versus a worker. A web build whose builds all report 0 is running inline (Burst off or workers
   absent) and says so in the existing diagnostics panel, instead of merely rendering slowly. This copies the
   `[BurstDiscard]` guard's lesson from `web-target.md`: a probe must be able to tell "ran inline" from "ran
   on a worker", or it returns no verdict — and it is the only reading any tooth in §8 may use to claim
   off-main execution. A `BuildStep` trail proves scheduling order, never placement.

## 8. Staged migration

Each stage is one revertible commit on the feature branch, gated by `./Tools/run-tests.sh` green on the frozen
tree. **Invariant** names what must not change; **teeth** are the tests a shallow or wrong implementation
cannot pass, with the defect each one is RED-verified against. Snapshot goldens are never re-baked: a
byte-level diff means behaviour changed.

### Stage 0 — the scheduling-cost probe (measurement 1), gating the per-layer shape

Before any builder exists: an EditMode probe that schedules N empty Burst `IJob`s with K container fields
each from the main thread, flushes, completes, and reports µs per `Schedule` with safety checks on; the same
probe run once in a player build. The EditMode gate drives `MaxMeshBuildsPerTick` to 64 in several suites
(`TileManagerLoadPriorityTests`, `ProfilerMarkerTests`, `GeoJsonSourceTests`), so the Editor number bounds
test runtime as well as the frame.

- *Decides:* per-layer graph builders (≈10–15 nodes per fill layer × layers per tile × kicks per Tick) versus
  **fused per-tile jobs** (one job per stage over all fill layers of the tile, each layer's data addressed
  through a layer-indexed offset table).
- *If per-layer fits* (the kick's scheduling cost is a small fraction of the per-Tick budget in a player and
  keeps the EditMode suite's runtime flat): stages 1–3 as written.
- *If it does not:* `FillMeshGraph.Schedule` takes a `LayerInputTable` (all fill layers of one tile) instead of
  one `LayerInput`, every node loops layers internally, and per-tile fan-in disappears (one handle per stage
  chain). Stage 1's parity tooth stays per-layer (hash each layer's slice); stages 2–3 wire one table per
  tile. The chunk plan, the write graph and every ownership rule are unchanged either way — only the node
  count per tile moves.
- *Tooth:* the probe itself is the deliverable; its numbers are dated and recorded here, not in source.

### Stage 1 — the fill graph, with no production caller

`MapRenderer.Jobs/FillMeshGraph`: the scheduled form of `FillMeshPipeline.Schedule` — same `LayerInput`,
returning `FillGraphOutput { outputs, chunk descriptors, Handle }` uncompleted. Adds the nodes §3.2 lists:
`FillSizingJob`, `FillGatherJob` (with the sort), `EarcutBatchJob` (a serial `IJob` looping polygons inside
`Execute`, taking `GetSubArray` views there and calling `EarcutJob.Execute()` directly — `EarcutJob` is not
modified), `FillAggregateJob`, `ChunkPlanJob`, `Dispose(handle)` scratch nodes, `ProjectionDispatch.Schedule`
(the `internal` generic entry point, §10) and the error flag. `FillMeshPipeline.Schedule` is untouched.

- *Invariant:* pure addition; the existing suite is unchanged. This holds because `EarcutJob`'s fields are
  not touched. If the compile checkpoint shows `GetSubArray` inside `Execute` is not viable under Burst for
  some stream, the fallback is the field-compatible route — extend the existing `OutIndexOffset` idiom to the
  other streams with legacy callers passing `0` and full-length views — and the invariant becomes "existing
  callers are edited to pass zero offsets; the suite is otherwise unchanged". Which route was taken is
  recorded here at landing.
- *Teeth:* (a) **parity** — over every fixture tile and every fill layer of the default style,
  `FillMeshGraph.Schedule(...).Complete()` output hashes equal `FillMeshPipeline.Schedule` output hashes,
  vertex-for-vertex and index-for-index, *including hole-bearing polygons* so the gather's sort is exercised
  (RED, two injections: perturb the aggregate's index offset by one; drop the comparer's `ring-index`
  tie-break — the parity-risk RED named in §3.2);
  (b) **not-completed-at-return** — for every non-empty layer, `Handle.IsCompleted` is false immediately after
  `Schedule` returns and *before* any flush (RED: a builder that `Complete()`s internally). Construction note:
  the poll must be immediate and precede `ScheduleBatchedJobs()`; an unflushed job cannot have started, so
  the tooth cannot false-red on a tiny fixture — but for the same reason it proves only that nothing
  completed synchronously, which is exactly the defect it names, and no more. Its honest complement is (b′),
  a structural check that `FillMeshGraph`'s source contains no `Complete()`;
  (c) **edge check** — the recorded RED run of §7.1 (drop one `deps`, expect the safety-system throw);
  (d) **scratch-freed** — after `Complete()` + `Dispose()`, `FillGraphOutput.DebugLiveCount` (counting scratch
  *and* outputs) is zero (RED: remove one `Dispose(handle)` node);
  (e) **error flag** — a sizing job handed a capacity smaller than the polygon count sets the flag and writes
  nothing past capacity (RED: remove the guard → the Editor bounds check throws instead of the flag being set);
  (f) **chunk plan** — over a synthetic layer with ≥4 features totalling >2× the chunk target, the plan splits
  only at feature boundaries and every chunk but an oversized single feature is ≤ the target (RED: split
  mid-feature → a triangle straddles two chunks, detectable as an index outside its chunk's vertex range).

### Stage 2 — the substrate, with the background tile as first client

`TileBuildGraph`, the `BuildStep` state on `LoadedTile` (and its readers: pump, drains,
`AwaitInFlightMeshBuilds`, `CaptureTelemetry`, `RenderTeardownRecord`, `DoDispose`), the graph pen,
`ScheduleBatchedJobs()` once per Tick, the `FillStreamWriteJob`, the measure→write step in the pump with
one exact-size `MeshDataArray(1)` **per non-empty chunk**, the `MeshDataArraysAllocatedLastKick` counter,
and the test-only delay-job seam (§5). `KickSourcelessBackground` schedules the graph directly (its quad
materializer runs on main at kick); source tiles are untouched and still go through `KickMeshBuild`.

- *Invariant:* byte-identical snapshots (the background snapshots and every suite that renders a
  background); source-tile behaviour unchanged. The background quad is one chunk, so the cache is untouched
  here.
- *Teeth:* (a) **step progression** — after the Tick that kicks a background tile, `AllTilesSettled()` is
  false and the tile's `BuildStep == Measure`; after the next, `Write`; consumed on the one after (RED: a
  pump that `Complete()`s at kick settles in one Tick). This proves scheduling order only — see §7.4;
  (b) **exact-size allocation** — the write step allocates one `MeshDataArray` per *non-empty chunk*, sized to
  that chunk's measured count, and `MeshDataArraysAllocatedLastKick` equals the non-empty chunk count (RED:
  allocate blind per layer → the count and size assertions fail);
  (c) **pen** — release a background tile while `BuildStep == Measure` and again while `Write`, with the
  delay job holding the handle incomplete; `TileBuildGraph.DebugLiveCount` and
  `MeshDataPayload.DebugLiveAllocCount` return to zero after the pen drains (RED: skip the pen's
  `Complete()`+`Dispose()` for one step);
  (d) **teardown** — teardown with the delay job holding a graph genuinely in flight returns and leaks
  nothing (extends `TeardownCancelInflightBuildsTests`; without the delay job the tooth would only exercise
  the completed-handle pen path, which is a different path);
  (e) **seam left** — the background kick reaches *no* `IWorkScheduler.Schedule<T>` call **and** the tile
  settles with its mesh registered (the existing spy suite's own rule: observe both ends, or "absent" and
  "never kicked" are indistinguishable) (RED: leave the seam call in);
  (f) **Complete-before-read** — the positive control from §6 (read an output at the measure→write
  transition before `Complete()` → safety throw), executed once and recorded.

### Stage 3 — source tiles: prologue → measure → write, and the cache ripple

`KickMeshBuild` splits. The prologue (selection, paint bake, sort-key order, the symbol worker pass) runs on
the seam as today and returns native per-layer columns; the pump schedules fill layers' graphs over them.
Line and fill-extrusion layers keep their synchronous `WriteInto` *inside the prologue* for this stage
(mixed tile: fill via graph, line via prologue) — the consume path already handles heterogeneous payloads.
`PreparedTileCache`'s value becomes `Mesh[]` (D7): `TransferOnRelease` groups a record's tracked meshes by
`MaterialIndices[i]` before `Put`; `TakeAsLoadedTile` registers each chunk; `null` stays the empty-layer
marker.

- *Invariant:* byte-identical snapshots across the whole visual suite; on desktop the prologue still runs on
  a pool thread (the existing `RecordingWorkScheduler` tooth reads the body's thread id).
- *Teeth:* (a) **three-step progression** on a fill tile, as stage 2(a) but through `Prologue` first;
  (b) **the Burst work is not executed inside the prologue body** — with `InlineWorkScheduler` injected (the
  web policy), the prologue's `WorkHandle` completes with native columns and *no* geometry, and the fill graph
  is scheduled by the pump on a later Tick (`BuildStep == Measure` after the kick Tick) (RED: run
  `FillMeshPipeline.Schedule` inside the prologue — the tile settles in one Tick). This tooth proves
  placement of the *call*, not of the *execution*: EditMode is not web, and a Burst-off Editor would leave
  the same trail. The execution-placement reading is (b′): across the fixture corpus, at least one graph's
  worker-index sample (§7.4) is non-zero — stated plainly as an EditMode fact about this machine's worker
  pool, never as a web claim;
  (c) **decode reference** — the kick's reference is released only by `TileBuildGraph.Dispose()`; a positive
  control releases it early and asserts the safety-system throw;
  (d) **pen at every step** — release while `Prologue`, `Measure`, `Write`; counters return to zero (RED per
  step);
  (e) **symbol pass still runs** — the populated `symbolPass` tooth from `MeshBuildWorkSchedulerTests` passes
  unchanged (the pass moved earlier relative to the Burst chain; it must still run exactly once);
  (f) **overshoot bound** — `tile-pipeline-design.md` §4's tooth verbatim: a ≥100k-vertex fixture across ≥4
  features with `MaxVerticesPerTick = 40 000` ⇒ `VerticesConsumedLastTick ≤ 40 000 + 32 768` per Tick and the
  layer produces ≥3 meshes (RED: chunk plan returns one chunk);
  (g) **cache round-trip** — build → release → revisit of a chunked layer: the vertex sum equals the original
  and the mesh count equals the chunk count (RED: keep the `Mesh`-valued cache → `Put`'s collision destroys
  chunks 1..K−1 and the sum drops).

### Stage 4 — globe subdivide and fill-extrusion roofs as graph nodes

`GlobeFillSubdivider.Schedule`; the globe write becomes a stream-write job over the subdivided lists (the
chunk plan runs after subdivision there, over the subdivided feature runs); the extrusion roof reuses
`FillMeshGraph` (walls stay in the prologue until the line graph lands, they share the per-ring shape).

- *Invariant:* byte-identical globe snapshots (`GlobeSnapshotTests`, `GlobeFillWindingTests`,
  `GlobeFillTangentTests`).
- *Teeth:* parity of the subdivided output against the current `WriteGlobeSubdivided` path over the fixture
  (RED: drop the tangent stream); the vertex budget still binds (the existing budget tooth).

### Stage 5 — the line graph

`LineMeshGraph`: tile→geo, up (typed projection job), a `LineSubdivideJob` (the managed
`SubdivideCenterline` as a Burst job), tile→geo, project, per-ring ribbon into pre-sized ranges, chunk plan,
stream write. Projection stays a closed enumeration; the graph exposes `internal` generic
`ScheduleTyped<TProj>` entry points (§10) so the right-handed-sphere winding tooth drives its
never-registered-by-production projection through the graph path.

- *Invariant:* byte-identical line snapshots (`LinePaintSnapshotTests`, `LineAaSnapshotTests`,
  `GlobeLineSnapshotTests`); `LineRibbonJobTests` unchanged.
- *Teeth:* parity of the ribbon output against the current builder over the fixture, per ring (RED: swap the
  subdivide tolerance); **the winding tooth through the graph** — `RightHandedSphereProjectionWindingTests`
  builds its curved right-handed projection through `LineMeshGraph.ScheduleTyped<RightHandedSphereProjection>`
  and asserts the ribbon winds the same way as flat Mercator relative to the surface normal (RED: derive
  winding from handedness — a `curved ⇒ flip` in the ribbon job — which inverts exactly this case while
  leaving `SphericalProjection` right; the test's original defect, now provable on the graph path).

### Stage 6 — within-stage parallelism

`EarcutBatchJob` → `IJobParallelForDefer` over the polygon descriptor list, with
`[NativeDisableParallelForRestriction]` on its flat-column fields; tile→geo / project / stream write →
`ScheduleParallel`; ribbon → parallel over rings.

- *Invariant:* byte-identical everything; no disabled-safety attribute outside `EarcutBatchJob`.
- *Teeth:* the determinism tooth (§7.3) over the corpus at worker count 0 and default (RED: make two
  polygons' scratch ranges overlap in the sizing job — the hashes diverge under contention *and* the
  disjointness assertion trips, which is the primary guard); the structure test of §7.2 (RED: add the
  attribute to any other job).

### Stage 7 — prologue nativization (first step) and seam retirement from the tile path

Selection as one job over the layer's feature range through the native filter VM (replacing the per-feature
`NativeFilterEvaluator.Evaluate` loop); paint bake as a job iff the native expression VM covers the paint
properties the default style uses — otherwise this stage stops at selection and records the gap. When the
prologue is empty, `TileManager` drops its `WorkScheduler` seam and the gate guard (§5 names the replacement
for the gate's test capability).

- *Invariant:* byte-identical snapshots; the selection parity oracle (`FeatureSelectorNativeFilterTests`)
  extends to the batched job.
- *Teeth:* a synthetic-layer selection tooth whose fixture actually drives every filter branch (the
  parity-oracle-vacuous lesson); the prologue's `Schedule<T>` count on a fill-only style is zero *and* the
  tile settles with its mesh (RED: leave one managed step in it).

**Deferred, explicitly:** nativizing the protobuf decoder; the symbol subsystem's two seam sites and its
per-frame `.Run()` jobs; scheduling graphs at the frame tail (`endContextRendering`, `frame-timeline.md`
§3) so workers overlap the render thread — a knob on top of this design, not part of it; backend, shaders.

## 9. Measurements this design needs (none invented here)

| # | measurement | decides | when |
|---|---|---|---|
| 1 | main-thread µs per scheduled node (Editor, safety on; player), × nodes per tile | per-layer builders vs fused per-tile jobs | **stage 0** |
| 2 | serial span of `SymbolProjectionJob`/`SymbolCullJob` at realistic symbol counts vs the `ScheduleParallel().Complete()` fan-out cost | rule 3 for the per-frame symbol sites | any time |
| 3 | `Complete()` wait on the symbol collision at the next Tick with N tile graphs in flight on web (5 workers) | whether an in-flight-graph cap below `MaxConcurrentTileLoads` is needed. **This is the one runtime hazard with no mitigation in the plan**: jobs have no priorities, and a queued collision behind a long earcut batch stalls the main thread at `HarvestCollision`. The mitigation, if it bites, is that cap — a knob on `TileBuildGraph` scheduling, not a design change. | after stage 3, on a web trace |
| 4 | on web, the prologue's main-thread ms per tile (selection + paint bake) vs the Burst chain it used to run inline | how much of the web win stage 3 delivers versus stage 7; the ordering question in §11 | during stage 3 |
| 5 | whether `Mesh.MeshData.SetVertexBufferParams`/`SetIndexBufferParams` are callable from inside a Burst job | **demoted by per-chunk output**: the chunk *count* is a measure-graph output and `AllocateWritableMeshData` is main-only, so the main-thread step between measure and write stays regardless. A positive answer only moves the per-chunk *sizing* call off main; it no longer removes a Tick. Still cheap; answer before stage 2 so the write step's shape is chosen with it known. | before stage 2 |
| 6 | the pen's worst-case `Complete()` at teardown with the load stress scene | confirms "bounded, no timeout" | after stage 3 |
| 7 | a one-line EditMode probe: `Schedule` from a ThreadPool thread throws (Unity's documented contract, confirmed once in this project's version) | rule 1's premise; if it ever did not throw, the three-step split would still be the design (the write step needs main for `MeshData` allocation), but the prologue could hand off without a Tick boundary | stage 0, alongside 1 |

## 10. Decisions taken here (overridable), and what was rejected

- **Three polled steps, +2 Ticks — accepted by the maintainer, see §3.1.** Rejected: one graph with a
  main-thread memcpy at consume (reverts the worker-writes-`MeshData` win for a copy that scales with vertex
  count); folding the prologue onto the main thread everywhere (deletes the seam a stage earlier at the price
  of a desktop main-thread regression until nativization).
- **Per-chunk `MeshData` from the start — maintainer decision, see §3.6.**
- **No bespoke graph description type.** A `Graph`/`Node` DSL would duplicate `JobHandle` and hide the
  safety system's edge check behind a layer it cannot see. Rejected.
- **Projection dispatch stays a closed enumeration; the generic entry points become `internal`.**
  `ProjectionDispatch.RunTyped<TProj>` / `ScheduleTyped<TProj>` (and the globe twin) are `internal`, and
  `MapRenderer.Jobs` already grants `InternalsVisibleTo` to the test assemblies, so a test drives a projection
  production never registers by calling the generic entry with its own struct — EditMode JITs the generic job,
  so no `RegisterGenericJobType` is needed there. The closed `switch` stays as the production enumeration
  ("the ONE place the concrete projection structs are enumerated", its own doc). *Rejected:* a
  registration-based registry keyed by projection type — process-wide mutable state that outlives a domain
  reload (test-to-test leakage) and erases the enumeration the switch exists to make obvious. The
  `internal` route also makes the winding tooth *stronger*: it proves the generic job works for a type
  nothing registered, which is the property that made it decisive.
- **`EarcutBatchJob` wraps `EarcutJob` rather than changing its fields** (stage 1); the field-compatible
  offset route is the recorded fallback.
- **Error flags replace the never-fired throws inside graphs**; the decoder's twin backstop keeps its throw
  (§3.2, recorded divergence).
- **The prologue keeps `TileBuildScratch` pooling and the symbol pass ordering** (symbol pass after the mesh
  prologue, before the Burst chain instead of after it — the handoff parks or enqueues to main either way).
- **Graphs are scheduled inside the existing `MaxMeshBuildsPerTick` budget**, one measure kick = one unit
  and one write kick = one unit (the `tile-pipeline-design.md` §4.1 charging rule). No new knob until
  measurement 3 asks for one.

## 11. Open questions escalated to the maintainer

One genuine fork remains; the stages are sequenced so either answer works.

1. **Order of the second half: line graph (stage 5) before prologue nativization (stage 7), or after?**
   Stage 5 buys parallel line builds on both targets; stage 7 buys the web its remaining main-thread relief
   (the prologue is inline on main there). Measurement 4 sizes the second; the first is structurally certain.
   *Consequence of 5-first:* the web keeps paying the prologue on main for one more stage. *Consequence of
   7-first:* line tiles keep running in the prologue (inline on web) for one more stage, and the paint-bake
   half of stage 7 may be blocked on native expression coverage. **Recommendation:** measure 4 during stage
   3; if the prologue is ≥ the Burst chain on a real web trace, 7 before 5.

*Settled, no longer open:* +2 Ticks of latency (accepted, §3.1); per-chunk output and ownership of
`tile-pipeline-design.md` §4 (§3.6); the scheduling-cost probe as stage 0 (§8).

*FYI, not a decision:* the "graphs reached a worker" reading (§7.4) needs a running player, so on web it is
a manual check beside `Tools/build.sh web`'s `burst:` line; the note belongs in `docs/web-target.md`, whose
owner adds it when the substrate lands.

## 12. Grounding (verified file:symbol, 2026-09-02)

- `docs/tile-pipeline-design.md` §4 (`:418–517`) — the two-phase kick plan §3.6 takes over; D6/D7 in its
  decision table (`:61–62`).
- `Assets/Code/MapRenderer.Unity/Concurrency/IWorkScheduler.cs` — the seam and its stated destination;
  `WorkHandle.cs` (`ToUniTask` bridge), `WorkSchedulerFactory.cs` (the one `#if`).
- `Assets/Code/MapRenderer.Jobs/FillMeshPipeline.cs:210` `Schedule(LayerInput)` — five `.Run()` sites plus
  `ProjectionDispatch.Run` (`:554`); the managed between-job passes at `:318–353` (sizing), `:384–421`
  (populate + hole sort at `:402`), `:498–519` (aggregate), `:619–627` (pre-pass); `HoleRingComparer` at
  `:714`; `PipelineHandle = default` at `:582`; the seven defaulted fields at `:585–590`.
- `Assets/Code/MapRenderer.Jobs/EarcutJob.cs:57` — fields are whole `NativeArray`s with `OutIndexOffset` the
  only offset; `new EarcutJob` at `FillMeshPipeline.cs:435` and
  `Tests.EditMode/Jobs/BurstJobRunOffMainSpikeTests.cs:189` (the two callers stage 1 leaves valid).
- `Assets/Code/MapRenderer.Jobs/TileMeshBuffers.cs:82` `PipelineHandle` and its doc contract.
- `Assets/Code/MapRenderer.Jobs/ProjectionDispatch.cs:26–47` (closed switch, `private static RunTyped<TProj>`),
  `GlobeFillSubdivider.cs:200–234` — the twin; `Assets/Code/MapRenderer.Jobs/InternalsVisibleTo.cs` (the
  grant the `internal` entry points rely on).
- `Library/PackageCache/com.unity.collections@…/Unity.Collections/Jobs/IJobParallelForDefer.cs:111, :172` —
  the only deferred-length scheduling overloads, all parallel-for (why stage 1's batch job is a plain `IJob`).
- `Assets/Code/MapRenderer.Jobs/MvtGeometryMaterializer.cs:116`, `Mvt/NativeFilterEvaluator.cs:58` — rule-1 sites.
- `Assets/Code/MapRenderer.Unity/Rendering/Meshing/StyledFillTileBuilder.cs:423` `WriteGeometry` (pipeline
  call + managed stream loop `:470–503`); `StyledLineTileBuilder.cs:300–362` (per-ring `.Run()`s and managed
  `ProjectPoint` loops, `SubdivideCenterline` at `:473`).
- `Assets/Code/MapRenderer.Unity/Rendering/Tile/TileManager.cs` — `LoadedTile` (`:194`), `_pendingDisposal`
  (`:637`), `CaptureTelemetry`'s backlog poll (`:1080`), `AwaitInFlightMeshBuilds` (`:1548`), `PumpPending`
  (`:1600`; state-machine body `:1646–1700`), `KickMeshBuild` (`:1827`), `KickSourcelessBackground` (`:1969`),
  `ConsumeMeshBuild` (`:2026`), `ReleaseTile` (`:2217`), the pen stash inside `RenderTeardownRecord` (`:2577`),
  `DoDispose` (`:2761`).
- `Assets/Code/MapRenderer.Unity/Rendering/Tile/PreparedTileCache.cs:201–214` — `Put` destroys the previous
  entry on key collision (why D7 lands with stage 3).
- `Assets/Code/MapRenderer.Unity/Rendering/Tile/Processing/TileLayerProcessorRunner.cs`,
  `TileMeshLayerProcessor.cs`, `TileDecodeDispatch.cs:58`; `Rendering/Style/MeshDataPayload.cs:38`
  (`AllocateTracked`, the leak counter pattern to copy).
- `Assets/Code/MapRenderer.Unity/Text/Placement/SymbolPlacementSystem.cs:1086` `ScheduleCollision`
  (`.Schedule()` at `:1126`, `ScheduleBatchedJobs` at `:1129`), `HarvestCollision` (`:1046`), `DoDispose`
  (`:1713`) — the cross-frame exemplar; `:1022` the `.Run()` argument; the five per-frame `.Run()`s at
  `:971, :1002, :1036, :1336, :1566`.
- `Assets/Code/MapRenderer.Unity/Text/SymbolSubsystem.cs:700, :977` — the two symbol seam sites.
- Tests reused as oracles: `Tiles/JobifiedPipelineTests.cs` (content-hash parity),
  `Tiles/MeshBuildWorkSchedulerTests.cs` (+ the gate mutual-exclusion tooth at `:217`, retired with the gate)
  + `TestSupport/RecordingWorkScheduler.cs` (thread placement), `Lifetime/DisposalLeakGuardTests.cs`,
  `Lifetime/TeardownCancelInflightBuildsTests.cs`, `Globe/RightHandedSphereProjectionWindingTests.cs`,
  `Filters/FeatureSelectorNativeFilterTests.cs`, the `Visual/*SnapshotTests.cs` suites.
