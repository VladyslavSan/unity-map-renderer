# Job scheduling — the Burst work as a job graph (design / SSOT)

**Status:** the model below ships. A multi-stage tile build is a `JobHandle` dependency chain scheduled from
the main thread and polled at the tile pump. This doc is the SSOT for how Burst work is chained, for the
`.Run()`/`.Schedule()` discriminator, for the disposal and cancellation invariant an in-flight `JobHandle`
imposes, and for which parts of `docs/tile-pipeline-design.md`'s build seam this design owns.

**Read with:** `docs/web-target.md` §"Measured in THIS project" (what the web target can and cannot run
off-main), `docs/async-architecture.md` §"Disposal & cancellation contract" (the lifetime contract §8
extends), `docs/gc-and-allocation-design.md` §5 (why off-main scratch is `Persistent`),
`docs/meshing-design.md` §1 (what the mesh stages compute), and `docs/tile-geometry-ir-design.md` (the
buffers a graph reads).

---

## 1. The itch

A library of Burst jobs is not a pipeline. A job dispatched through `.Run()` executes on the calling thread,
so a chain of ten of them is a synchronous function call wearing job structs. What makes such a chain
synchronous is the read-back between the stages, not any stage's nature. One thing turns the library into a
pipeline: **holding an uncompleted `JobHandle` across a Tick.**

The web target makes that structural rather than cosmetic. Its only execution resources are the main thread
and Burst workers; every managed offload mechanism is inert there, while a scheduled Burst chain does reach
a worker. So `.Run()`-everywhere on the web is not merely serial — it is **serial on the render thread**,
with the workers idle. Two independent consequences follow:

- **Correctness.** A body inside a job runs on every platform with no branch. A managed closure needs one to
  run off-main at all.
- **Performance.** The one execution resource the web has is otherwise unused.

## 2. The decisions, up front

| question | decision |
|---|---|
| how a chain is expressed | as `JobHandle` edges: every stage takes `deps` and returns a handle; a per-layer graph builder returns `(outputs, terminal handle)` as one struct; per-layer graphs fan in through `JobHandle.CombineDependencies` to one per-tile handle. There is no bespoke DAG type — `JobHandle` **is** the declaration. |
| who owns handles and buffers | the struct that owns the output buffers owns the terminal handle, indivisibly (`FillGraphOutput.Handle`, `LineGraphOutput.Handle`). Intermediate scratch is freed by `Dispose(JobHandle)` nodes inside the graph, so the caller sees outputs plus one handle. |
| where a graph is scheduled | on the main thread, at the tile pump — the job system's contract. Off-main managed code never schedules; it produces native inputs the main thread schedules over. |
| how completion reaches the consumer | polled once per Tick on `JobHandle.IsCompleted`, at the pump that already polls `WorkHandle.IsCompleted`. No callback, no UniTask hop. `Complete()` runs before any output is read. |
| the tile build's shape | three polled steps per tile: **prologue** (managed, `IWorkScheduler`, shrinking) → **measure graph** (Burst; all geometry) → **write graph** (main allocates one exact-size `MeshData` per layer, Burst streams into it). |
| `.Run()` vs `.Schedule()` | the ordered discriminator in §7 — call-site placement first, then latency tolerance, then span. Never a per-site judgement call. |
| `IWorkScheduler` | survives, shrunk to the bodies that are still managed, and is deleted per site as each body becomes a job. It never wraps a job. |
| platform | one graph shape, zero `#if`. The single platform decision point is `WorkSchedulerFactory`, and it governs only the residual managed bodies. |
| cancellation | never disposes and never interrupts. A released tile's `(buffers, MeshDataArrays, handle, decode reference)` unit moves to a pen that completes it, then disposes it. A lifetime token gates the *next main-thread step*, never a running job. |
| safety | every graph edge is a container hand-off the Editor safety system checks at `Schedule` time. The parallel-slice exceptions are confined to two job types and fenced by a structure test (§9). |

## 3. The tile build — three polled steps

```
fetch (UniTask) ─▶ decode (managed, IWorkScheduler) ─▶ SharedDisposable<IDecodedTile>
                                                                 │  pump observes completion
                                                                 ▼
 ┌─ PROLOGUE ──────────────────────────── managed, IWorkScheduler (pool / inline) ─┐
 │  per layer: feature selection, paint bake (colour/opacity/width), sort-key       │
 │  order, the symbol worker pass ⇒ NATIVE per-layer request columns                │
 └─────────────────────────────────── WorkHandle<TilePrologueOutput>, polled ───────┘
                                                                 │  IsCompleted
                                                                 ▼
 ┌─ MEASURE GRAPH ────────────────────────── Burst, scheduled on main, polled ──────┐
 │  one graph per layer (§4), fanned in by CombineDependencies to one tile handle    │
 └────────────────────────────────────────────── TileBuildGraph, polled ────────────┘
                                                                 │  IsCompleted → Complete()
                                                                 ▼
 ┌─ WRITE GRAPH ───────── main allocates one exact-size MeshData PER LAYER, then Burst ┐
 │  AllocateWritableMeshData(1) + SetVertexBufferParams(measured count)                │
 │  → the layer's stream-write job (columns → MeshData views; winding swap)            │
 └────────────────────────────────────────────── TileBuildGraph, polled ───────────────┘
                                                                 │  IsCompleted → Complete()
                                                                 ▼
                          consume (main, budgeted): Apply + AddTileLayer per layer
```

**Why three steps and not one.** The job system schedules from the main thread, so a graph cannot be built
from inside the worker body that runs the prologue — the prologue must *finish* before the pump can schedule
over its outputs. And the `MeshData` stream views a write job fills exist only after
`AllocateWritableMeshData(1)` and `SetVertexBufferParams(count)`, while the layer's exact vertex count is a
measure-graph output. Allocation and sizing therefore sit on the main thread *between* two graphs.

**What it costs.** Up to two extra Ticks of latency per tile: the prologue completes on Tick N, measure is
scheduled on N and observed on N+1, write is scheduled on N+1 and consumed on N+2. This is a standing
accepted cost. It is invisible against network fetch and is the same order as the one-frame deferral the
symbol collision already accepts. The uniform three-step path holds for every tile; a small-tile fast path
(prologue on main at kick) is a later knob, worth revisiting only if a trace shows tiles arriving visibly
late relative to fetch.

**What shrinks.** The prologue is the whole remaining reason `IWorkScheduler` exists on the tile path. Each
managed step that becomes a job moves out of the prologue and into the measure graph. When the prologue is
empty the tile path has no managed body, and the pump schedules the measure graph straight off the decode.
**On the web the prologue runs inline on the main thread**; what leaves the main thread there is the geometry
work, not the prologue, so the prologue's nativization is where the rest of the web payoff sits.

### The per-tile record's lifecycle

`TileManager.LoadedTile` carries a `BuildStep` (`None | Prologue | Measure | Write`) beside its flags:

1. **Fetch** — a `UniTask<SharedDisposable<IDecodedTile>>` in flight, stored `.Preserve()`d.
2. **Build** — `Step` becomes `Prologue` (`MeshBuildTask` in flight, source tiles only), then `Measure`,
   then `Write` (`Graph` in flight, every tile). A background tile starts at `Measure`: it has no prologue,
   and it allocates no `Mesh.MeshDataArray` of its own — that is the write step's work.
3. **Consumed** — `Built = true`, `Step = None`.

`ReleaseTile` removes a tile from `_loaded` at once, and `PumpPending`/`DrainMeshBuilds` iterate `_loaded`,
so a released tile is never visited again and never has its build consumed.

`MaxMeshBuildsPerTick` counts **tiles admitted** per Tick. A step transition of an already-admitted tile is
uncharged: the three main-thread costs per tile (prologue kick, measure schedule, write kick) are unequal, so
charging some of them measures nothing physical.

## 4. The job graph — the chains

Each geometry kind has one graph builder, in the assembly its jobs live in. Every node takes `deps` and
returns a handle; `ScheduleDispose` nodes free scratch behind the last reader.

**Fill** — `MapRenderer.Jobs/Fill/FillMeshGraph.Schedule`:

```
RingSelectJob | RingClipJob  →  RingAssemblyJob  →  SizingJob  →  FillGatherJob<HoleRingComparer>
  →  EarcutBatchJob (parallel over polygons)  →  AggregateJob  →  FillBandJob
  ├─ flat arm:   TileToGeoJob (parallel) → ProjectionDispatch.Schedule
  └─ curved arm: GlobeFillSubdivideDispatch.Schedule → GlobeFillScatterJob
```

The clip-or-select branch is the tile-buffer window; the arm split picks the aggregate's targets. On the
curved arm the pre-subdivision geodetic and projected nodes are genuinely dead — `GlobeFillSubdivideJob`
reads only tile vertices, indices and feature indices and projects internally — so those buffers are
disposed behind the band node rather than fed forward. `GlobeFillScatterJob` is the curved arm's bridge from
`GlobeFillVertex` records into the same output columns the flat arm writes.

**Line** — `MapRenderer.Jobs/Lines/LineMeshGraph.Schedule`:

```
RingGatherJob  →  TileToGeoJob → ProjectionDispatch   (the ORIGINAL centerline's per-point surface up —
                                                       the subdivision metric only)
  →  SubdivideJob  →  TileToGeoJob → ProjectionDispatch   (the SUBDIVIDED centerline's real columns)
  →  RibbonSizingJob  →  RibbonBatchJob (parallel over rings)  →  RibbonAggregateJob
```

`RingGatherJob` is the ring gate: selection, LineString kind, and line's own `>= 2` length filter — never
fill's `>= 3`. **The line chain has no arm split, unlike fill.** A flat projection's infinite
`MaxRefineAngleRad` makes `SubdivideJob` a one-step no-op, so the flat case is the chain's own degenerate
value rather than a branch.

**Fill extrusion** — `MapRenderer.Unity/Rendering/Meshing/FillExtrusionMeshGraph.Schedule`: the roof is
`FillMeshGraph.Schedule` composed unchanged; the walls are their own chain,
`RingSelectJob | RingClipJob → ProjectionColumnSizingJob → TileToGeoJob → ProjectionDispatch → WallQuadJob`.
The wall chain takes the same clip-or-select branch on the same input the roof takes, so a building crossing
a tile seam is cut identically in both. `ProjectionColumnSizingJob` exists because a clipped gather has no
schedule-time output length.

**Write** — one stream-write job per kind (`MapRenderer.Unity/Rendering/Meshing`, beside the builders that
own the vertex-stream layout). An extrusion layer writes the roof at `[0, Vr)` and the walls at
`[Vr, Vr + Vw)` with wall indices rebased, into one exact-sized `MeshData`: a layer emits exactly one mesh.

**A graph builder validates its inputs before scheduling any node.** A builder that throws partway leaves
scheduled jobs holding the caller's inputs with no terminal handle ever returned — nothing can `Complete()`
them, and the caller's own cleanup then fails while disposing geometry the safety system still sees as
in flight. That failure names a bystander container, not the real defect. A node's own argument check stays
as a backstop for a caller arriving by another route; it is never the first line of defence.

**A backstop inside a graph is an error flag, not a throw.** Burst cannot throw, so a capacity overrun sets
`NativeReference<int> Error`; a non-zero flag settles the layer as zero-vertex and logs. The decoder's twin
backstop keeps its throw, because it runs inside a managed body where a throw is observable — the two differ
in mechanism until the decoder is a job.

## 5. Ownership — the graph as a value

Three rules, all consequences of one fact: **a native container is disposed only by its owner, and only
after every job that references it has been `Complete()`d by that owner.**

1. **A graph builder returns `(outputs, handle)` as one struct.** `FillGraphOutput` and `LineGraphOutput`
   hold the output lists, the counts, the error flag and `Handle`. Their `Dispose()` is defined as
   `Handle.Complete(); free everything`.
2. **Scratch never leaves the graph.** Every intermediate buffer is freed by a `Dispose(lastReader)` node
   scheduled inside the builder, and the returned handle includes those nodes. `Complete()` on the terminal
   handle frees all scratch, and the owner never enumerates it.
3. **Sizing and every between-stage pass is a node, not calling-thread work.** A read-back that sizes the
   next stage is what makes a chain synchronous, so the sizing lives in `SizingJob`/`RibbonSizingJob` and the
   next stage takes `AsDeferredJobArray()` views whose length resolves at execution.

**Per layer**, one `ILayerMeshBuild` (`FillLayerBuild`, `FillExtrusionLayerBuild`, `LineLayerBuild`) owns its
kind's request columns, measure output and write output behind four members — `ScheduleMeasure`,
`TryScheduleWrite`, `TakePayload`, `Dispose`. Builds are pooled, never allocated per tile build.

**Per tile**, `TileBuildGraph` owns the terminal handle for whichever step is in flight, the dense
`ILayerMeshBuild[]` (aliased from the prologue's output, not copied — ownership transfers on the call that
hands it over, including on that call's own throw path), the one per-tile geometry buffer a background
tile's layers all borrow, and the kick's `SharedDisposable<IDecodedTile>` reference. It **dispatches nothing
per kind**: every layer is an `ILayerMeshBuild`, so there is no tagged union of layer requests to keep in
step with the kinds.

A source tile's layers borrow their geometry from the decoded tile the reference keeps alive; a background
tile's layers borrow one quad buffer the graph itself owns. The two differ by *who owns the geometry*, and
the release order in `Dispose` follows from that: every layer, then the owned buffer, then — last — the
decode reference that ultimately backs what the layers read.

### The graph's output is one column set

A fill graph emits **one** column set regardless of arm: `WorldPositions`, `VertexUp`, `VertexEast`,
`VertexBand`, `TileVertices`, `VertexFeatureIdx`, `TriangleIndices`. The curved arm writes it through the
scatter node; the flat arm's aggregate and project nodes write it directly, with east = +X.

The alternative is a tagged union spelled as a bag of fields — columns populated on one arm, empty on the
other, and a bool saying which — where no single node's body states the rule. The write step is the first
consumer, and the cache, the extrusion roof and the line write job all mirror what it consumes, so a
consumer written against the bag keeps the bag alive.

## 6. Completion, and the UniTask boundary

**Polled at the pump; `Complete()` before any read.** The job system offers no completion callback, and
`UniTask.WaitUntil(() => handle.IsCompleted)` is a poll wearing an awaiter. `JobHandle.IsCompleted` is a
non-blocking flag read and slots in beside the `WorkHandle.IsCompleted` poll the pump already runs.

**`IsCompleted == true` says only that the wait would return at once. It does not release the job's
registration on the containers' safety handles — `Complete()` does.** Every step transition is therefore
`IsCompleted` → `Complete()` (wait-free at that point) → read outputs, allocate `MeshData`, upload. A read
before `Complete()` throws in the Editor and is undefined in a player.

**`ScheduleBatchedJobs()` once per Tick.** A job scheduled from the main thread is not handed to workers
until the batch is flushed or an implicit sync point arrives. Both the measure kick and the measure→write
transition schedule from the same pump pass, so the flush is one call at the end of that pass, never one
per tile.

**Blocking drains are bounded because jobs are finite CPU.** The deterministic test settle completes each
in-flight step in order; teardown completes everything in the pen. `Complete()` from the main thread runs a
not-yet-started job inline, so neither drain has a PlayerLoop dependency to dead-end on, and neither needs
the timeout a managed body's drain needs.

The bridge into the UniTask I/O chain is unchanged: fetch→decode still ends in a
`UniTask<SharedDisposable<IDecodedTile>>` the pump observes, and no `Task`↔`UniTask` interop exists.

## 7. The dispatch discriminator — `.Run()` or `.Schedule()`

The discriminator classifies a **call site**, and the first question is about placement, not platform. Apply
in order and stop at the first answer.

1. **Is the call inside an `IWorkScheduler` body** — not guaranteed to be on the main thread?
   → **`.Run()`.** The job system schedules from the main thread only, and an in-thread `.Run()` is the
   cheapest thing a worker can do. This is a property of where the code sits, so it answers the same on every
   platform: under `InlineWorkScheduler` the body happens to be on main, and `.Run()` is still the only
   correct dispatch there. This branch empties as bodies become jobs and their call sites move to the pump.
2. **Can the consumer tolerate seeing this output on a later Tick?** — it is already a polled state, or it
   could become one. → **`Schedule(deps)`.** The handle joins the owner that polls it, and `Complete()` runs
   only after `IsCompleted` is true or inside a bounded drain.
3. Otherwise the call is frame-synchronous on the main thread. **Is it an `IJobParallelFor` whose measured
   serial span exceeds the fan-out cost?** → **`ScheduleParallel(...).Complete()`**: the main thread blocks,
   but wall time drops. Otherwise → **`.Run()`**, because for a serial or small job `Schedule().Complete()`
   only adds a hand-off. Before settling here, ask again whether the consumer could become one frame late.

A contributor answers three yes/no questions. There is no list of sites to look up.

The per-frame symbol sites sit at rule 3 and stay `.Run()`: `SymbolPlacementSystem` blocks on them
regardless, and nothing has measured a parallel-for span there large enough to pay for fan-out.
`SymbolPlacementSystem.ScheduleCollision` is the rule-2 exemplar — the repo's one cross-frame `JobHandle`,
completed a frame later. The decoder's `MvtDecodeJob.Run()` and selection's `MvtNativeFeatureMatcher.MatchAll`
sit at rule 1 and stay there until their bodies are jobs.

**Dispatch granularity is part of rule 1.** A `.Run()` inside a managed loop pays a job-struct copy and a
dispatch per iteration. Selection runs one `RunByRef` per layer-selection rather than one per feature; on the
largest committed fixture (11,941 feature-filter evaluations) that is the difference between ~5.2 ms and
~0.47 ms per tile, and it comes from the dispatch count, not from any per-call saving.

### The count rule — a graph-fed job carries no count field

**A job's arrays' lengths ARE its counts.** A synchronous caller passes exact-length views
(`arr.GetSubArray(0, count)`); a graph passes `AsDeferredJobArray()`, whose length resolves at execute time.
Neither needs a separate count field.

The rule exists because the alternative accretes. A `NativeArray` job field left at literal `default` fails
Unity's schedule-time container validation, so a job that wants an *optional* count cannot express it as an
optional container — it has to add a boolean selecting between "explicit count" and "array length". Each such
boolean is locally sound and globally a tax: the next job added to a graph gets the next one.
`RingAssemblyJob.RingCountFromOffsetsLength` is the last survivor, kept only by unit-test construction sites
with no production duplication behind it.

### Holding a graph in flight inside a test

**`JobsUtility.JobWorkerCount = 0` does not hold a job incomplete.** With zero workers a scheduled job runs
and completes synchronously, so `IsCompleted` already reads true at the checkpoint after `Schedule()` +
`ScheduleBatchedJobs()`. Zero worker count is still valid for its other use — the determinism check, same
output with and without workers — but it cannot observe an in-flight graph.

**The `deps` parameter is the instrument, and it needs no test-only production hook.** Every graph builder
takes `JobHandle deps` for composing onto upstream work. A test schedules its own spin job at the default
worker count and passes that handle as `deps`; the whole graph is then genuinely in flight until the spin
releases. That is what the pen, teardown and complete-before-read teeth hold against.

*Trap:* Burst folds a synthetic delay — a counting loop reduces to a closed form, a flag spin can have its
load hoisted — so the instrument must first prove it takes time and reads `IsCompleted` false.

## 8. Disposal and cancellation — the invariant

`docs/async-architecture.md`'s contract stands. This extends it to buffers a scheduled job may still be
reading, in one rule:

> **A native buffer (`NativeArray`/`NativeList`, and the `Mesh.MeshDataArray` a write job fills), the
> `JobHandle` of the last job that references it, and the reference that keeps its inputs alive are one
> indivisible ownership unit. They move together, and they are released in that order: `Complete()` the
> handle, then read or upload, then dispose the buffers, then release the input reference. Cancellation
> never disposes; it transfers the unit to the pen.**

The four exits:

| exit | what happens |
|---|---|
| **consumed** | `IsCompleted` at the pump → `Complete()` → upload each layer's mesh → `TileBuildGraph.Dispose()`, which frees the buffers and releases the decode reference. Never a read before `Complete()`. |
| **released in flight** | the graph moves to `PendingDisposalQueue`'s graph pen; the pen polls `IsCompleted` per Tick, then `Complete()`s and disposes. |
| **cancelled mid-work** | **there is no in-job cancellation.** A scheduled job is finite and not interruptible, so there is nothing to interrupt. The token gates the *next main-thread step*: the pump does not schedule measure after a cancelled prologue, does not allocate or schedule write after a cancelled measure, and does not upload after a cancelled write. The unit then goes to the pen. |
| **teardown** | cancel (which gates future steps), then `Complete()` every pen entry and dispose. Bounded by in-flight CPU, so no timeout. Order unchanged: destroy meshes → dispose backend. |

Two consequences worth naming:

- **The decode reference is held to the end of the chain.** It is released by `TileBuildGraph.Dispose()` after
  `Complete()`, at consume or in the pen. The window matches the chain's duration either way; it is now
  expressed as ownership rather than as a `finally` in a worker body.
- **`Allocator.Persistent` everywhere in a graph.** `TempJob`'s four-frame guard counts main-thread frames and
  a build spans more. Scratch released through `Dispose(handle)` nodes is still `Persistent` — the deferred
  dispose node is what gives deterministic release with no main-thread read-back. A job's *own* transient
  scratch is the exception: `Allocator.Temp` locals inside `Execute`, freed when the job returns.

## 9. Safety — making the Editor's check sufficient

The Editor's job safety system checks, at `Schedule` time, that a job touching a container another scheduled
job writes declares that job as a dependency, and at access time that the main thread does not read or
dispose a container under a live job. None of that exists in a player, and none of it exists on the web. The
design is shaped so the Editor's check covers the graph — with two qualifications:

- **The check is contingent on the JobsDebugger toggle, not on being in the Editor.** A gate whose debugger
  is off proves nothing, so a precondition tooth asserts `JobsUtility.JobDebuggerEnabled` — readable and
  settable, so it RED-verifies cheaply. Without it, every dependency-edge RED rests on an unread flag.
- **The check does not localise the defect.** A dropped edge that leaves the node reachable throws at the
  *second* job's `Schedule()`, naming both jobs and the container. A dropped edge that also *orphans* the
  node surfaces at deallocate time instead, naming whichever container is next touched synchronously — a
  bystander. See `docs/lessons-learned.md`.

**Rule 1 — every graph edge is a container hand-off.** Stage N+1's dependency on stage N is carried by a
`NativeArray`/`NativeList` field with a safety handle plus the `deps` handle passed to `Schedule`. A builder
that forgets `deps` on an edge throws in the Editor at schedule — *if a test schedules the real graph*. Hence
the tooth: an EditMode test that runs a real graph builder over the fixture, completes, and hashes.

**Rule 2 — disabled safety restrictions are enumerated, not judged case by case.**
`[NativeDisableContainerSafetyRestriction]` is forbidden in production graph code, unconditionally.
`[NativeDisableParallelForRestriction]` is sanctioned in exactly two job types, once each: the per-polygon
parallel earcut (`EarcutBatchJob`) and the per-ring parallel ribbon (`RibbonBatchJob`). Both have the same
shape — `Execute(index)` writes into a pre-sized *slice* of a flat per-item column, taken from consecutive
entries of a monotonic offset table a serial sizing node built — so the attribute goes on the single struct
field nesting those columns (`TriangulationBuffers`/`RibbonBuffers`). The attribute on an outer struct field
legalises a non-`index` write through a nested `NativeList<T>`, which `NativeList<T>` alone does not support.

*Per-item slice disjointness is structural.* Consecutive entries of a monotonic table share no element for
any values the table can hold, so the bytes one item touches are a function of its own input alone —
independent of which worker runs it, or how many run at once. Each sizing job also checks that its offset
tables are strictly increasing and raises the error flag otherwise. That check is defence in depth, so a
non-monotonic table surfaces as a player-build error code rather than an Editor-only bounds throw. It is not
what makes the parallel node bit-exact.

**The bounding rule the same fence protects:** every node that holds a sizing-owned buffer struct bounds its
own loop — or its deferred count — by a column its own sizing job resizes, never by a borrowed count that
job's early return does not touch. Nodes past the aggregate bound by columns the *aggregate* sizes, and
inherit emptiness through it. A borrowed bound turns a sizing capacity early-return into a silent
out-of-bounds write in a release build, where the container bounds check is compiled out.

Two structure tests fence this, over `MapRenderer.Jobs/` and `MapRenderer.Unity/Rendering/Meshing/` — both
directories graph nodes live in, so scoping to one assembly would leave the stream-write jobs unfenced. Each
enumerates its directories rather than a hand-listed file set, and asserts it found files to scan so a moved
directory cannot pass it vacuously.

- The **attribute fence** pins each sanctioned *(file, token, count)* triple. A bare filename would exempt
  that file from every forbidden token at any count — including the one that is never sanctioned.
- The **consumer-set fence** counts which files declare a sizing-owned buffer struct as a field, matching the
  type plus any identifier so a renamed field cannot walk past it, and asserts that set against a pinned
  allowlist of seven. It reds on an eighth consumer and names it. **No fence can check the bound itself** —
  that is dataflow, not lexical text. What the fence catches is a *stale consumer set*, which is how the
  third recurrence of the borrowed-bound defect stayed hidden.

Test assemblies are out of this fence's scope, which is what lets the in-flight test instrument use a
disabled restriction legitimately.

**Rule 3 — determinism tooth.** Run the fill and line graphs over the fixture corpus with
`JobsUtility.JobWorkerCount = 0` and with the default worker count, and assert identical content hashes over
the full column sets. A parallel decomposition that races produces a hash difference under contention; an
element-wise one cannot. A race can still pass by luck, which is why rule 2 confines disabled restrictions to
two places, and why the safety system rather than this tooth is the primary guard.

**Rule 4 — the worker-index sample, the instrument that tells "off-main" from "inline".** A graph's last node
records `[NativeSetThreadIndex]` into a `NativeReference<int>`, and telemetry counts graphs that completed on
thread 0 versus on a worker. A web build whose builds all report 0 is running inline — Burst off, or workers
absent — and says so in the diagnostics panel instead of merely rendering slowly. **A probe that cannot tell
"ran inline" from "ran on a worker" returns no verdict**, and this is the only reading that may be used to
claim off-main execution: a `BuildStep` trail proves scheduling order, never placement. Nothing in
`Assets/Code` declares `[NativeSetThreadIndex]` today, so off-main execution on the web is currently
unobserved.

## 10. Where the wall-clock win is — off-main versus parallel

A chained sequence still costs its full serial time when it runs off-main. Three distinct wins, sourced
differently:

| win | mechanism | who gets it |
|---|---|---|
| **the main thread stops paying for geometry** | every graph node leaves the calling thread; on web that thread is the render thread. The prologue does not leave it on web until it is nativized. | web (large); desktop already had it through the ThreadPool |
| **across tiles** | N tiles' graphs in flight fan out over workers with no code — the job system does it | web (new); desktop (pool threads → job workers) |
| **within a stage** | `IJobParallelFor`/`IJobParallelForDefer` where the stage is element-wise | both |

What is parallel and what is serial by nature:

| stage | unit | parallel? |
|---|---|---|
| decode | source layer | across layers only — a layer's command stream is sequential |
| select / clip, ring assembly, sizing, gather, aggregate | layer | serial per layer (appends, counts, prefix sums), parallel across layers |
| **earcut** | **polygon** | **yes** — `IJobParallelForDefer` over the polygon descriptors, each on its disjoint slice. The one real intra-layer fill win. |
| tile→geo, project | vertex | **yes** — `IJobParallelFor` over a deferred array |
| globe subdivide | layer | serial — budgeted `NativeList` appends |
| line subdivide + ribbon | ring | sizing (serial) → `IJobParallelForDefer` ribbon → serial aggregate |
| stream write | vertex | measured and **not** parallelised — the per-job span did not clear the fan-out gate |

**Batch size follows per-item cost variance, not item count.** Vertex-wise stages batch at 1024, because
`TileToGeoJob`/`ProjectPointsJob` cost far more per 1024 vertices than a batch hand-off costs. Earcut and
ribbon batch at **1**: their items vary by orders of magnitude, so a batch of one lets the job system's
work-stealing balance the load.

Determinism under parallelism follows from the decomposition: parallel stages are element-wise or write
disjoint pre-sized slices, and the one sort is over a total order (the hole comparer's final ring-index key),
so output does not depend on which worker ran what.

**One platform.** One graph shape, zero `#if`. The job system is the same API on both targets; what differs
is `JobsUtility.JobWorkerCount`, which is read and never branched on. `WorkSchedulerFactory.ForCurrentPlatform()`
is the single platform decision point and it governs only the residual managed bodies, so it disappears with
the last of them. The one web-specific risk is a Burst-off build, where jobs execute inline inside
`Schedule()` on the main thread — a performance cliff, not a blank map. `RuntimeDiagnostics` detects Burst-off
at startup; rule 4's sample is what would make the cliff visible rather than inferred from frame time.

## 11. The seam — `IWorkScheduler`, `WorkHandle<T>`, `WorkSchedulerFactory`

**The seam survives, shrunk, and is deleted per site rather than replaced.** A managed body cannot be a job,
and on the web a managed body runs inline on main whatever wraps it — the seam is the honest expression of
that. What the graph changes is that the seam stops *carrying* the Burst work: a prologue produces native
inputs and returns, and the pump schedules the Burst work over those inputs. **The seam never wraps a job.**

Four production sites remain:

| site | why it is still managed | path |
|---|---|---|
| `TileDecodeDispatch.DecodeAsync` | a protobuf walk over `byte[]` into managed property stores | stays until the decoder is nativized. On web this is main-thread cost. |
| `TileManager.KickMeshBuild` | the prologue: selection, paint bake, sort keys, the symbol pass — over style layers, `IFeature`s and expression evaluators | shrinks as each step becomes a job; the Burst half is already the measure and write graphs |
| `SymbolSubsystem`'s parked-build drain | symbol extraction over managed builders and the sprite atlas | stays; symbol internals are out of scope |
| `SymbolSubsystem.ScheduleReconcileIfDirty` | a `Dictionary` dedup | stays |

`KickSourcelessBackground` has already left the seam: a background tile's prologue is small enough to run on
the main thread at kick, and its graph is scheduled directly.

`WorkHandle<T>` is the prologue's handle. `WorkSchedulerFactory` is the one `#if`. When the prologue is empty,
`TileManager` has no use for the seam and drops it together with the `MeshBuildGateForTest`
mutual-exclusion guard; the seam then serves decode and symbols only. What replaces the gate's capability —
holding a build genuinely in flight so teardown can be exercised against it — is the `deps` spin job of §7: a
Burst job cannot park on a `WaitHandle`, but it can spend a bounded, fixture-chosen number of iterations, and
it needs no mutual exclusion because it is policy-independent.

## 12. What this design owns from `docs/tile-pipeline-design.md`

§4 there plans the same seam. This design owns the two-phase split and its exact-size allocation, and nothing
about chunking.

**The write graph emits one mesh per layer.** Per-chunk mesh output answers a consume stall that is asserted
and never measured, and that reading the code contradicts: a per-mesh budget already exists, consume is
already mesh-by-mesh, the main-thread consume cost is dominated by per-mesh work rather than per-vertex
bytes, and splitting a layer would multiply that cost while spending the whole consume budget on one layer.

| §4 part | owner | how |
|---|---|---|
| §4.1 the two-phase `LoadedTile` state | **this design** | `BuildStep`, with a third value for the prologue; a step transition of an admitted tile is uncharged (§3) |
| §4.2 `ILayerGeometry` replacing `IRenderLayer.WriteInto` — a managed measure/write mesher interface | **superseded** | the graph builder plus the stream-write job *are* the measure/write split. There is no managed mesher interface in the middle, and `WriteInto` is gone. |
| §4.3 exact-size allocation, the allocation counter, consume and backend unchanged | **this design** | one `MeshDataArray` per non-empty layer, sized to that layer's measured count |
| §4.3 D7 — `PreparedTileCache` value `Mesh` → `Mesh[]` | **moot** | it existed only because one layer could become K > 1 meshes. One mesh per layer keeps the current value shape correct, and the cache is untouched by this design. |
| §4's consume-overshoot tooth | **moot** | it asserted a per-Tick consume bound *and* "the layer produces ≥ 3 meshes"; the second half is false once a layer is one mesh |
| the chunk target constant | **moot** | nothing reads it once chunking is gone |

What §4 delivers here is the blind-allocation stall, which falls out of exact sizing. The consume stall
chunking was meant to close is **not** addressed and is not claimed to be.

## 13. Invariants that constrain what is built next

1. **Managed and Burst agree exactly on linear operations, and within a measured tolerance downstream of a
   transcendental.** A quantity linear in its inputs is bit-exact between a managed implementation and a
   Burst one. A quantity downstream of `atan`/`sinh`/`sin`/`cos`/`normalize` diverges by a few ULP, because
   relaxed-math reassociation touches the transcendental call sites. **The divergence magnitude is not fixed:
   it scales with whether a large magnitude is subtracted from the quantity before it is compared.** An
   origin subtraction that shrinks a result ~640× while the absolute error stays fixed makes the same error
   read as ~128× more ULP. **No ULP figure from one call site may be reused as a bound at another without
   re-measuring there**, and a bound is never widened past its measured figure. Every future nativization
   crossing this boundary measures its own bound at its own call site.
2. **A float32 narrowing downstream of that boundary erases the divergence.** Several hundred ULP of a double
   at tile-local magnitude is ~six orders of magnitude below one float32 ULP there. That is why a narrowed
   position keeps a bit-exact assertion rather than a softened bound.
3. **A wall is emitted for every edge of a clipped ring, cut edges included.** What is extruded is the
   clipped polygon, and no per-edge "this edge is a cut" provenance exists anywhere in the chain. Under the
   opaque `fill-extrusion` regime a cut wall faces into the other half of the building and is back-face-culled
   or depth-rejected. It is not invisible where it matters: at the edge of the loaded cover, and while a
   neighbour is still loading, this shows a closed truncated building where suppressing cut edges would show
   a hollow shell. **The one condition that reopens the alternative is translucent fill-extrusion**
   (`docs/depth-and-render-regimes-design.md` §6.E), under which the hidden cut walls become a visible seam
   band. Suppressing them then needs a new per-output-edge column out of `RingClipJob` — a contract change
   across all its call sites. Do not build it before §6.E lands.
4. **Every stream view of one `Mesh.MeshData` shares one safety handle.** Two writable views cannot be two
   job fields: scheduling throws an aliasing error. A stream-write job therefore holds the whole
   `Mesh.MeshData` as **one** field and resolves every stream and index view inside `Execute()`.
   `AllocateWritableMeshData` is main-thread-only, so the allocate-and-size step stays on the main thread
   whatever else moves.
5. **A graph builder's inputs are borrowed, and the borrow outlives the call.** A builder holds the caller's
   geometry as a live `[ReadOnly]` input until the terminal handle completes. The Editor safety system throws
   when such an input is disposed before `Complete()`, which is the tooth for releasing the decode reference
   last.

## 14. Rejected alternatives

- **A bespoke graph/node description type.** It would duplicate `JobHandle` and hide the safety system's edge
  check behind a layer that cannot see it.
- **Fused per-tile jobs** — one job per stage over all fill layers, each layer addressed through an offset
  table — instead of per-layer graph builders. Measured 2026-09-02 (Editor, safety checks on, Burst enabled,
  warmed): creating a `JobHandle` edge costs ~0.3 µs and the cost is flat in the number of edges, with
  `ScheduleBatchedJobs` under 10 µs at every count. At the production admit rate that is roughly three orders
  of magnitude of headroom against a per-layer graph's ~10–15 nodes per fill layer. Per-layer builders stand;
  the fused variant buys nothing and costs the per-layer parity oracle.
- **A completion callback or UniTask bridge for `JobHandle`.** The job system has no callback, and an awaiter
  over `IsCompleted` is a poll with a hop added.
- **One graph with a main-thread memcpy at consume.** Reverts the worker-writes-`MeshData` win for a copy
  that scales with vertex count.
- **Folding the prologue onto the main thread everywhere.** Deletes the seam one stage earlier at the price
  of a desktop main-thread regression until nativization.
- **Per-chunk `MeshData`.** See §12.
- **A registration-based projection registry** keyed by projection type. `ProjectionDispatch`'s closed
  `switch` is the one place the concrete projection structs are enumerated; a registry is process-wide
  mutable state that outlives a domain reload (test-to-test leakage) and erases that enumeration. The generic
  `ScheduleTyped<TProj>` entry point is `internal` so a test can reach a projection Burst never registers
  generically; production reaches only the closed `switch`.
- **Changing `EarcutJob`'s fields to take offsets.** `EarcutBatchJob` wraps it instead, taking each polygon's
  views inside `Execute` and calling `EarcutJob.Execute()` directly, which leaves the inner job's contract
  and its own callers untouched.

## 15. Open

- **Jobs have no priorities.** A symbol-collision job queued behind a long earcut batch stalls the main
  thread at harvest, and nothing in this design mitigates it. The remedy, if it bites, is a cap on in-flight
  tile graphs below `MaxConcurrentTileLoads` — a scheduling knob on `TileBuildGraph`, not a change of shape.
  Sizing it needs a web trace with several graphs in flight.
- **§9 rule 4's worker-index reading needs a running player to observe on web.** `docs/web-target.md`'s
  *Proving a build is what it claims* carries the web-side half of that obligation.

## 16. Grounding (file:symbol touch points)

`MapRenderer.Jobs/Fill/`: `FillMeshGraph.Schedule` (the fill chain, both arms, the batch constants),
`SizingJob`, `FillGatherJob`, `EarcutBatchJob`, `AggregateJob`, `FillBandJob`, `GlobeFillSubdivider` /
`GlobeFillScatterJob` (the curved arm), `FillGraphOutput` (the one column set + terminal handle),
`RingAssemblyJob` (the surviving count selector), `TriangulationBuffers`. `FillMeshPipeline` survives only as
the home of `LayerInput`, `HoleRingComparer` and the decode-sizing pair.
`MapRenderer.Jobs/Lines/`: `LineMeshGraph.Schedule` (the line chain, no arm split), `RingGatherJob` (the ring
gate and line's own length filter), `SubdivideJob`, `RibbonSizingJob`, `RibbonBatchJob`,
`RibbonAggregateJob`, `RibbonBuffers`, `LineGraphOutput`.
`MapRenderer.Jobs/Projection/ProjectionDispatch` — `Schedule` (the closed switch) and `ScheduleTyped<TProj>`.
`MapRenderer.Unity/Rendering/Meshing/`: `FillExtrusionMeshGraph.Schedule` (roof + wall chains,
`ProjectionColumnSizingJob`, `WallQuadJob`), `ILayerMeshBuild` and its three implementations, the per-kind
stream-write jobs, `MeshWriteOutput`.
`MapRenderer.Unity/Rendering/Tile/`: `TileBuildGraph` (per-tile ownership, the step transitions),
`BuildStep`, `TileManager.PumpPending` / `KickMeshBuild` / `KickSourcelessBackground` / `ReleaseTile`,
`PendingDisposalQueue` (the pens), `Processing/TilePrologueOutput`, `Processing/TileDecodeDispatch`.
`MapRenderer.Unity/Concurrency/`: `IWorkScheduler`, `WorkHandle`, `WorkSchedulerFactory`.
`MapRenderer.Unity/Rendering/Style/MeshDataPayload` — the main-thread allocation boundary.
`MapRenderer.Unity/Text/Placement/SymbolPlacementSystem` — `ScheduleCollision`/`HarvestCollision`, the
cross-frame exemplar.
Teeth: `Tests.EditMode/Jobs/GraphDeterminismTests` (rule 3), `Tests.EditMode/Structure/StructureTests`'s
`FillMeshGraphStructureTests` (the attribute and consumer-set fences),
`Tests.EditMode/Tiles/TileBuildGraphTests` / `SourceTileGraphBuildTests`,
`Tests.EditMode/Lifetime/DisposalLeakGuardTests` / `TeardownCancelInflightBuildsTests`,
`Tests.Shared/SpinUntilGateJob` (the in-flight instrument of §7).
