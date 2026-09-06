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
| the tile build's shape | three polled steps per tile: **prologue** (managed, `IWorkScheduler`, shrinking) → **measure graph** (Burst: all geometry) → **write graph** (main allocates one exact-size `MeshData` **per layer**, Burst streams into it). |
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
  (next bullet). The line path has `TileToGeoJob` + `RibbonJob`; decode has `MvtDecodeJob`; symbols have
  six per-frame jobs.
- **Managed work sat between the fill jobs, on the calling thread.** In the now-retired
  `FillMeshPipeline.Schedule` (job-scheduling-design.md §8 stage 4 Group B): the `maxRingLen`/`totalVerts`
  pre-pass over the visit order; the earcut sizing loop that built the four offset tables; the **populate
  pass** — per polygon, the hole-ring sort through `HoleRingComparer` (`(leftmost-x, min-y, ring-index)`, a
  total order the parity tooth depends on, unchanged — `FillMeshGraph`'s gather node uses the same comparer)
  and an O(V) copy of outer + hole vertices into the flat buffers; and the **aggregate** — an O(V+I) concat
  with index rebasing. Plus the count read-backs (`polyCountArr[0]`, per-polygon `perPolyMergedVC`) that
  sized the next stage. `StyledFillTileBuilder.WriteGeometry`'s stream copy into `MeshData` and
  `StyledLineTileBuilder`'s per-point `ProjectPoint` loops, `SubdivideCenterline` and ribbon copy are managed
  the same way (the line path is unaffected by Group B). **A classification that only looks at `.Run()`
  sites cannot see any of this** — §4.2 therefore classifies *dispatch sites*, and §3.2 lists the
  between-job work as nodes.
- **Seventeen `.Run()` job-dispatch sites and one real `.Schedule()`** — ten on the tile-build path, two
  inside managed worker bodies, five per-frame in symbol placement (the brief's recon said twelve; the count
  above is from a fresh grep and is the one §4.2 classifies). The `.Schedule()` is
  `SymbolPlacementSystem.ScheduleCollision`,
  completed one frame later — the repo's only cross-frame `JobHandle`, and the exemplar this design
  generalises). The now-retired `FillMeshPipeline.Schedule(LayerInput)` was *named* Schedule but returned
  fully computed buffers with `PipelineHandle = default`.
- **Dependency ordering was call order, in the retired synchronous pipeline.** Inside
  `FillMeshPipeline.Schedule`, stage N+1's inputs were sized from stage N's outputs read back on the calling
  thread. That read-back, and the managed passes above, are what made the chain synchronous — not any
  stage's nature.
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
  cap", PENDING) plans a measure/write split. Its per-chunk half is retracted at the head of that §4; §3.6
  states what this design takes over from what remains.

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
 │                   → tile→geo → project [→ globe subdivide]                            │
 │                   ⇒ one column set (world/up/east/tile/feature) + indices + counts    │
 │  per line layer:  tile→geo → up → subdivide → tile→geo → project → ribbon             │
 │  fan-in: CombineDependencies(all layers) = the tile's terminal handle                 │
 └────────────────────────────────────────────────── TileBuildGraph.Handle, polled ──────┘
                                                                            │  IsCompleted → Complete()
                                                                            ▼
 ┌─ WRITE GRAPH ───────────── main allocates one exact-size MeshData PER LAYER, then Burst ┐
 │  per layer: AllocateWritableMeshData(1) + SetVertexBufferParams(vertexCount)           │
 │  → StreamWriteJob(the layer's columns → MeshData views; winding swap)                  │
 └────────────────────────────────────────────────── TileBuildGraph.Handle, polled ──────┘
                                                                            │  IsCompleted → Complete()
                                                                            ▼
                         consume (main, budgeted, unchanged): Apply + AddTileLayer per layer
```

**Why three steps and not one.** (a) The job system schedules from the main thread, so the graph cannot be
built from inside the ThreadPool body that runs the prologue today; the prologue must *finish* before the
pump can schedule over its outputs. (b) The `MeshData` stream views a write job fills only exist after
`AllocateWritableMeshData(1)` + `SetVertexBufferParams(count)`, and the layer's exact vertex count is a
measure-graph output — so allocation and sizing sit on the main thread *between* two graphs. That split is
the two-phase kick `docs/tile-pipeline-design.md` §4 already planned for a different reason (blind
allocation, stall #5); this design is what makes it necessary rather than merely desirable. §4's other
half, the mesher vertex cap for consume overshoot, is retracted — see §3.6 and that section's own header.

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
   the layer's columns and `Handle`. `TileMeshBuffers` already has `PipelineHandle` and the doc line *"Complete()
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
| assembly count read-back (`polyCountArr[0]`) → earcut scratch sizing loop (offset tables) | **`SizingJob`** (serial `IJob`): reads the assembly's polygon descriptors, writes the four offset tables and `Resize`s the flat scratch `NativeList`s (a single-threaded job may resize a list it owns); asserts the tables are strictly increasing and sets the error flag otherwise |
| populate pass: hole sort via `HoleRingComparer` + outer/hole vertex copy + `perPolyFeatureIdx` capture | **`FillGatherJob`** (serial `IJob`): `NativeSortExtension.Sort` with the same struct comparer (Burst-compatible). Determinism does not rest on the sort algorithm: the comparer's final `ring-index` key makes it a **total order**, so any correct sort yields the managed order. This is the **first parity risk to RED-verify** (perturb the tie-break → parity reds). |
| per-polygon `EarcutJob.Run()` in a loop, slices taken on the calling thread | **`EarcutBatchJob`** (`IJob` at stage 1; `IJobParallelForDefer` since stage 6): holds the flat columns + offset tables, one polygon per `Execute` call, takes each polygon's `GetSubArray` views there and calls `EarcutJob.Execute()` directly — `EarcutJob`'s fields are untouched, so its two existing callers (`FillMeshPipeline`, `BurstJobRunOffMainSpikeTests`) stay valid. Stage 6 converted this job to `IJobParallelForDefer`, batched by 1 polygon, deferred over `FillMeshGraph`'s own `buffers.PerPolyOuterCount` (the only column with length exactly `polyCount` — every offset table is `polyCount + 1`). It STILL takes the whole `TriangulationBuffers` field (the A.2 branch, §5) — the field just gained `[NativeDisableParallelForRestriction]` — and `PolyCount` retired (§4's count rule): the deferred iteration count is the guard the field used to be. |
| per-polygon merged-count read-back + aggregate (concat + index rebase) | **`AggregateJob`** (serial `IJob`): prefix sums over `perPolyMergedVC`/`perPolyIdxCount`, then the concat; output lists resized by the job |
| `TileToGeoJob.Run(n)`, `ProjectionDispatch.Run` | the same jobs over `AsDeferredJobArray()` views (length resolved at execution); `ScheduleParallel` in stage 6 |
| `GlobeFillSubdivideDispatch.Run` | `GlobeFillSubdivideDispatch.Schedule` node (serial, budgeted `NativeList` appends). **"Budgeted" means bounded, not configurable:** the graph passes `GlobeFillSubdivideDispatch`'s `Default*` constants with no override parameter, so the budget binds but is not wired to any config. The budget tooth pins the *bound mechanism* through a direct dispatcher call — it does not prove an end-to-end wired budget, and should not be read as doing so. |
| `WriteGeometry`'s stream copy loops (position/normal, UV, tangent, colour, index winding swap) | **`FillStreamWriteJob`** in the write graph, one per layer |

**A graph builder validates its inputs before scheduling any node, not partway through.** Found at stage 2:
`ProjectionDispatch.Schedule` rejecting a null projection from inside `FillMeshGraph.Schedule` (after
`RingSelect`/`RingClip`/`RingAssembly`/sizing/gather/earcut/aggregate had already scheduled, all holding the
caller's geometry as a live `[ReadOnly]` input) threw with no terminal handle ever constructed — nothing left
to `Complete()` those jobs, so the caller's own cleanup then failed disposing geometry the safety system
still considered in flight (a bystander fault, not the real defect). The fix is a builder-level rule, not a
one-off guard: a graph builder that throws mid-schedule leaves scheduled jobs holding the caller's inputs, so
builders validate inputs before scheduling any node — `ProjectionDispatch`'s own throw stays as the backstop
for a caller that reaches it by another route, but is never the first line of defense.

The never-fired `EnsureCapacity` backstops cannot throw inside Burst; inside a graph they become a
`NativeReference<int> Error` the write step reads — non-zero ⇒ the layer settles as zero-vertex and logs,
the same shape as a faulting processor today. **Recorded divergence:** `MvtGeometryMaterializer`'s twin
backstop (named at `FillMeshPipeline.cs:275`) stays on the throw shape, because it runs inside the managed
decoder where a throw is observable; the two backstops therefore differ in mechanism until the decoder is a
job. (The flag is *more* observable than today: a sizing job can be fed an undersized capacity in a unit test
and the flag asserted — a path the synchronous throw could never reach.)

**Per tile**, `TileBuildGraph` (class, `MapRenderer.Unity/Rendering/Tile/Processing`) owns: the tile's
terminal `JobHandle`; one `ILayerMeshBuild` per layer, which owns its kind's request, measure output and
write output (the per-layer build-object stage — `TileBuildGraph` itself dispatches nothing per kind); the
prologue's native columns (graph inputs, now owned by each layer's own build); the kick's own
`SharedDisposable<IDecodedTile>` reference (the decoded buffers are `[ReadOnly]` graph inputs, so the
reference is released only after `Complete()` — the Editor safety system throws if the buffers are disposed
under a live reader, which is the tooth); and, in the write step, one `MeshDataPayload` per layer (taken from
its own build). It replaces `MeshBuildResult`. `LoadedTile` gains a `BuildStep` (`Prologue | Measure | Write`) beside
the existing flags; `HasMeshBuild` becomes "has a build step in flight". Every reader of `MeshBuildTask`
takes the extension — `PumpPending`, `DrainMeshBuilds`, `AwaitInFlightMeshBuilds`, `CaptureTelemetry`'s
backlog count, `RenderTeardownRecord`'s pen stash and `DoDispose` — not only the pump.

**Per layer**, the graph builders live where the jobs live: `MapRenderer.Jobs` for the format-neutral geometry
graphs (`FillMeshGraph`, `LineMeshGraph`, `ProjectionDispatch.Schedule`, `GlobeFillSubdivider.Schedule`);
`MapRenderer.Unity/Rendering/Meshing` for the stream-write jobs, which own the vertex-stream
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
| sizing, gather, aggregate | layer | across layers | serial prefix sums / copies, O(polygons) or O(V) |
| **earcut** | **polygon** | **yes** (stage 6) — `IJobParallelForDefer` over the polygon descriptor list, each on its disjoint slice | the one real intra-layer win; needs the sub-slice safety exception (§7) |
| tile→geo, project | vertex | **yes** — already `IJobParallelFor`, today `.Run(n)` single-threaded | `ScheduleParallel` over a deferred array (stage 6) |
| globe subdivide | layer | across layers | serial — budgeted `NativeList` appends |
| line subdivide + ribbon | ring | across rings once ribbon offsets are pre-sized from subdivide counts | three-pass (sizing → `IJobParallelForDefer` ribbon, one ring per batch → serial aggregate), since stage 6; serial per ring before it |
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

### 3.7 The graph's output is ONE column set — decided, executed with the write step

**Decision (2026-09-02), to execute at the start of the write-step stage, not retrofitted later.**
`FillGraphOutput` currently carries ten containers: five populated on the curved arm, three empty on the flat
arm, and an `IsSubdivided` bool saying which — a tagged union
spelled as a bag of fields, and its own doc admits no single node's body states the rule.

The write step needs one record per vertex — world, up, east, tile-uv, feature — plus indices. That is exactly
`GlobeFillVertex`, and the flat arm holds the same information with east = +X. So the output becomes **one**
column set: `WorldPositions, VertexUp, VertexEast, TileVertices, VertexFeatureIdx, TriangleIndices`. The flat
arm's aggregate/project nodes write it directly; the curved arm's write scratch (disposed in-graph like
everything else) and the subdivide node writes the output.

Removed for free: `IsSubdivided`, the `Subdivided*` triple, the `chunkFeatureSource` ternaries, and the dead
curved-arm projection recorded above. The write step then always reads the one column set.

**Correction (2026-09-02): `GlobeFeatureColumnJob` is NOT removed** — it was listed above and that was wrong.
Deleting it means `GlobeFillSubdivideJob<TProj>` writes the six output columns itself, which changes the
output-field signature of a job with **two synchronous callers** (`StyledFillTileBuilder.WriteGlobeSubdivided`
and the extrusion roof at `StyledFillExtrusionTileBuilder.cs:377`). The usual additive escape is closed here:
a container field left `default` fails schedule-time validation, and that rule recurses into grouped structs,
so "synchronous callers pass nothing" is not available (`docs/lessons-learned.md`). So the job survives,
widened from one column to six and renamed for what it does — it scatters the subdivided vertex records into
the output columns. **The decision is unaffected; one of its listed consequences was not free.**

**Update (job-scheduling-design.md §8 stage 4 Group B):** the two synchronous callers this paragraph names
are retired — `StyledFillTileBuilder.WriteGlobeSubdivided` and `StyledFillExtrusionTileBuilder`'s `WriteGlobeRoof`
are both deleted, and `GlobeFillSubdivideJob<TProj>`'s output-field signature is no longer shared with a
synchronous caller at all. `GlobeFillScatterJob` survives anyway — the scatter is real work independent of
caller count, it is the curved arm's own bridge from `GlobeFillVertex` records to the graph's one column set.

The instrument proving the spherical arm was entered moves from the pre-subdivision projected columns to the
subdivided world positions — same projection, so the on-sphere assertion works unchanged. Curved-arm parity
was against `WriteGlobeSubdivided` before Group B retired it; it is now a frozen per-stream golden captured
from that oracle before deletion (`FillMeshGraphGlobeParityTests`).

**Why the timing is load-bearing:** the write job is the first consumer, and `PreparedTileCache`, the extrusion
roof and the line write job will all mirror whatever it consumes. A consumer written against a union-in-a-bag
keeps the bag alive.

### 3.6 Relationship to `docs/tile-pipeline-design.md` §4 — what this design now owns

> **Amended 2026-09-02: the chunking half is dropped, by maintainer decision.** This section previously took
> over §4's per-chunk mesh output and its overshoot tooth. That rested on stall #4, which was asserted and
> never measured, and which reading the code contradicts — a per-mesh budget already exists
> (`MaxConsumesPerTick`, consume already mesh-by-mesh), the main-thread consume cost is dominated by per-mesh
> work rather than per-vertex bytes, and splitting a layer would multiply that cost while spending the whole
> consume budget on one layer. See the retraction at the head of `tile-pipeline-design.md` §4.
>
> **The write graph emits one mesh per layer.** What this design still owns from §4 is unchanged: §4.1's
> state, §4.3's exact-size allocation and its counter. `ChunkPlanJob`, `FillChunk` and the chunk target are
> removed. §3.7's one-column-set output is unaffected — it was decided for the write step's own sake, and it
> is what makes that step a single job.
>
> **Two consequences, written down here so no stage re-derives them.** (1) §4.3's **D7 cache ripple is moot**:
> it existed only because one layer could become K > 1 meshes, so `PreparedTileCache`'s `Mesh`-valued entry
> would have collided with itself on `Put`. One mesh per layer means the existing value shape stays correct
> and stage 3 does not touch the cache. (2) §4's **overshoot tooth is moot** with it — it asserted a per-Tick
> consume bound *and* "the layer produces ≥3 meshes", and the second half is now false by construction. Both
> are struck from the tables and stages below rather than left as teeth nothing can satisfy.


§4 there is a live PENDING plan for the same seam (two-phase kick, and — retracted — per-chunk meshes with a
`PreparedTileCache` ripple). The maintainer decided (2026-09-02) that the write graph emits **per-chunk `MeshData` from the
start**, and then **reversed that on the same day** once reading the consume path showed the stall it was
meant to close is unmeasured and that chunking would worsen the cost that dominates — see the amendment at
the head of this section and the retraction in `tile-pipeline-design.md` §4. **The write graph emits one
`MeshData` per layer.** What this design takes from §4 is the two-phase split and its exact-size allocation,
nothing about chunking. Consequences, stated so neither document claims the other's work:

| §4 part | owner now | how |
|---|---|---|
| §4.1 `LoadedTile` two-phase state (`MeasureTask`/`HasMeasure`; the write step charged to `MaxMeshBuildsPerTick`) | **this design** | `BuildStep` is that state with a third value for the prologue; the write step is **not** charged — `MaxMeshBuildsPerTick` bounds tiles admitted per Tick, and a step transition of an already-admitted tile flows uncharged (§11 fork 2) |
| §4.2 `ILayerGeometry` replacing `IRenderLayer.WriteInto` — a managed `Measure(IReadOnlyList<ITileFeature>, …)` / `WriteChunk(chunk, MeshData, …)` seam | **superseded by this design** | the graph builder + `FillStreamWriteJob` are the measure/write split; there is no managed mesher interface in the middle. `IRenderLayer.WriteInto` is retired when its last caller (the prologue's line/extrusion path, stages 3→5) is a graph. |
| §4.3 exact-size allocation, `MeshDataArraysAllocatedLastKick`, consume/backend needing no change | **this design** | stage 2 allocates one `MeshDataArray` per non-empty layer, sized to that layer's measured count; the counter tooth is adopted |
| §4.3 D7 — `PreparedTileCache` value `Mesh` → `Mesh[]`, `TransferOnRelease` grouping by `MaterialIndices[i]` | **moot** | it existed only for K > 1 meshes per layer; one mesh per layer keeps the current value shape correct |
| §4's overshoot tooth (≥100k-vert fixture across ≥4 features, `MaxVerticesPerTick = 40 000` ⇒ per-tick consume ≤ 40 000 + 32 768, ≥3 meshes) | **moot** | its "≥3 meshes" half is false by construction once a layer is one mesh |
| §4's prerequisite "capture `PmMeshDataAllocate` numbers first" | **moot** | allocation becomes exact regardless of what the numbers say |
| the chunk target 32 768 (D6, serialized constant) | **moot** | nothing reads it once chunking is gone |

What §4 delivers here is stall #5, which falls out of exact sizing. Stall #4 is **not** addressed and is not
claimed to be: it was never measured, and the retraction above is the reason. `tile-pipeline-design.md` §4 should be annotated to point
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

### `JobsUtility.JobWorkerCount = 0` does NOT hold a job incomplete

**Measured 2026-09-02, and it falsifies an instrument this design assumed.** §7.3 and the substrate stage's
plan both leaned on setting the worker count to zero as a way to hold a scheduled graph incomplete inside a
test. It does not do that: with zero workers a scheduled job runs and completes **synchronously**, so
`IsCompleted` already reads `true` at the checkpoint immediately after `Schedule()` + `ScheduleBatchedJobs()`.
`Complete()` afterwards is a correct no-op and the output is right — the job ran, just not later.

Zero worker count remains valid for its *other* use, the determinism check (same output with and without
workers). It is simply not a way to observe an in-flight graph.

**What to use instead — no production surface required.** `FillMeshGraph.Schedule` (and any graph builder)
takes `JobHandle deps`, which is a production parameter for composing onto upstream work. A test schedules its
own delay job with workers at their default and passes that handle as `deps`; the whole graph is then
genuinely in flight until the delay completes. This is what the pen, teardown and Complete-before-read teeth
hold against, and it needs no `internal` hook — the earlier plan's fork between a test-only hook and dropping
those teeth was a false choice.

*Trap:* Burst will fold a synthetic delay — a counting loop reduces to a closed form and a flag spin can have
its load hoisted — so the delay instrument must prove it actually takes time and actually reads `IsCompleted`
false, before any tooth depends on it.

### The count rule — a graph-fed job has no count field

**A job's arrays' lengths ARE its counts.** A synchronous caller passes exact-length views
(`arr.GetSubArray(0, count)`); a graph passes `AsDeferredJobArray()`, whose length resolves at execute time.
Neither needs a separate count field.

This exists because the alternative accretes. Two jobs already carry a boolean selecting between "explicit
count" and "array length" — `RingAssemblyJob.RingCountFromOffsetsLength` and
`GlobeFillSubdivideJob.CountsFromArrayLength` — each added for a sound local reason: a `NativeArray` job field
left at literal `default` fails Unity's schedule-time container validation, so the discriminator had to be a
value type. But the *count field* is what forces the choice, and `RibbonJob.PointCount` is the next one
the line graph would give a third bool. `EarcutJob` never needed a flag, because it already takes views.

Construction cost is small, and was overstated once: `RingAssemblyJob` had 9 construction sites (1 production,
8 test files); `GlobeFillSubdivideDispatch.Run` had 6, all now gone with it. The "143 callers" figure from the
stage-1 commit counted test *cases*, not edit sites. **Apply the rule to every job the line graph touches.**
Stage 4 Group B retired one of the two booleans: `GlobeFillSubdivideJob.CountsFromArrayLength` is gone —
`Execute` now always reads `TileVerts.Length`/`TriangleIndices.Length`, since `Schedule` (deferred views) is
the job's only caller left. `RingAssemblyJob.RingCountFromOffsetsLength` does NOT retire here (DIV-B2) — its
explicit-count arm still has 8 legitimate unit-test construction sites and no production duplication behind
it; it retires with the line graph (stage 5).

**Stage 6 retires the rule's next predicted casualty: `EarcutBatchJob.PolyCount`.** Converting to
`IJobParallelForDefer` over `FillMeshGraph`'s own `buffers.PerPolyOuterCount` removed the count field's last
reason to exist — `Execute(int pi)` has no `polyCount` to guard against, the deferred iteration count IS the
guard. `RibbonBatchJob`/`RibbonAggregateJob` never had a count field to begin with (ring count was
always `RingSubOffsets.Length - 1`).

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
| `MvtNativeFeatureMatcher.MatchAll` (`MvtNativeFeatureMatcher.cs:72`) — one `RunByRef()` per layer-selection, not per feature (stage 7 landed) | inside the managed selection loop (prologue body) | 1 → `.Run()`-class, via `RunByRef` | unchanged until the prologue itself leaves the `IWorkScheduler` body — no `JobHandle` yet | 1 |
| `SymbolPlacementSystem` — gather, compact, projection, cull, stage (5 `.Run`) | frame-synchronous on main | 3 → `.Run()` | unchanged; projection/cull are candidates for `ScheduleParallel().Complete()` pending measurement 2 | 3 |
| `SymbolPlacementSystem.ScheduleCollision` | `.Schedule()`, completed next Tick | 2 | unchanged — the exemplar | 2 |
| `RuntimeDiagnostics` Burst probe | function pointer | — | diagnostic; unchanged | — |
| BRG `OnPerformCulling` | returns a `JobHandle` to Unity | — | backend; out of scope | — |

## 5. The seam — what happens to `IWorkScheduler`, `WorkHandle<T>`, `WorkSchedulerFactory`

> **When the graph/pipeline duplication ends — done, stage 4 commit 2.** Six bodies existed twice — sizing,
> gather, aggregate, the derive pre-pass, the clip/select branch, and two dispatch switches with identical
> case lists — deliberately, for a parity-bound migration, with the parity sweep as the control for it.
> `FillMeshPipeline.Schedule` had no production caller once the extrusion roof moved to the graph (Group A),
> and was deleted then (Group B), together with `TileMeshBuffers` and the graph-vs-pipeline parity tests. The
> oracle afterwards is the snapshot suites plus per-stream SHA-256 goldens captured from the synchronous
> pipeline BEFORE deletion (`FillMeshGraphParityTests`, `FillMeshGraphGlobeParityTests`,
> `StyledFillExtrusionGraphWriteTests`, `TileBuildGraphTests`) — a regression pin against the retired
> independent implementation, not a live differential (which would have become self-referential the moment
> both arms ran the same code).


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
| (1) consumed | `GetResult()` → upload → dispose | `IsCompleted` at the pump → **`Complete()`** → upload each layer's mesh → `TileBuildGraph.Dispose()` (buffers freed, decode reference released). Never a read before `Complete()`. |
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
job. None of that exists on the web. The design makes the Editor's check *sufficient* by construction —
with three qualifications stage 1 established empirically, which are stated here because each one was
assumed before it was measured:

- **Detection of an unsynchronised write-write pair at `Schedule` is real, and confirmed in this repo.** An
  isolated injection (drop one edge, keep the node reachable) threw at the *second* job's `.Schedule()`
  naming both jobs and the container: *"the previously scheduled job `FillRingCountJob` writes to …
  `Counts`. You are trying to schedule a new job `FillSizingJob`, which writes to the same …"*. This is the
  "access" variant, raised from `JobsUtility.Schedule`, not a later check.
- **But it is contingent on the JobsDebugger toggle, not on being in the Editor.** "The Editor's check" reads
  as a guarantee the Editor does not itself give — same hazard class as a Burst toggle left off silently
  changing what a green result means. A gate whose debugger is off proves nothing here. **Observed by a
  precondition tooth** asserting `JobsUtility.JobDebuggerEnabled` (readable *and* settable, so it RED-verifies
  cheaply — unlike the Burst-compile hazard beside it, which no test can see and which is caught by grepping
  the run log instead). Without that tooth every dependency-edge RED in this epic rests on an unread
  environment flag; it went unobserved until stage 2.
- **The check does not localise the defect, and a careless injection does not test what it names.** An
  omission that also *orphans* a node surfaces instead at deallocate time, naming whichever container is
  next touched synchronously — a bystander. See `docs/lessons-learned.md`; both traps cost this stage two
  rounds of unreadable RED evidence.

> **Which of these four are actually implemented (as of stage 2).** This section is a specification and reads
> in the present tense throughout; rule 2 was read as a report of existing coverage by three separate readers
> and the test it named did not exist. So, explicitly: **rule 1 — implemented** (every parity suite schedules
> the real graph with the debugger on; its RED is recorded). **Rule 2 — implemented in stage 2**, and see the
> precondition below. **Rule 3 — implemented in stage 6**, naming `GraphDeterminismTests`
> (`Tests.EditMode/Jobs/GraphDeterminismTests.cs`) — the worker-0-vs-default self-comparison over the full
> fill and line column sets, plus the frozen fill-parity goldens at their real reach. **Rule 4 — not yet,
> nothing anywhere in `Assets/Code` declares `[NativeSetThreadIndex]`**; whoever lands it also owes the
> web-side note now stubbed in `web-target.md` under *Proving a build is what it claims* (stage 6 deliberately
> does not claim off-main execution from this rule's absence — see stage 6's own invariant block). Keep this
> list current when a rule lands — a rule stated in the present tense with nothing behind it is the failure
> mode this whole section exists to prevent (`lessons-learned.md`, "A design doc that names its own tooth").

1. **Every graph edge is a container hand-off.** Stage N+1's dependency on stage N is carried by a
   `NativeArray`/`NativeList` field with a safety handle, and the `deps` handle passed to `Schedule`. A
   builder that forgets `deps` on an edge throws in the Editor at schedule — *if a test schedules the real
   graph*. Hence the substrate tooth: an EditMode test that runs `FillMeshGraph.Schedule` over the fixture
   with the job debugger on (the EditMode default), `Complete()`s, and hashes. Its RED check is executed once
   and recorded: replace one node's `deps` with `default` → `InvalidOperationException` at schedule.
2. **No `[NativeDisableContainerSafetyRestriction]` in a graph builder, and no
   `[NativeDisableParallelForRestriction]` before stage 6.** Stage 6 sanctions exactly two job types, each
   with exactly one occurrence — the per-polygon parallel earcut (`EarcutBatchJob`) and the per-ring parallel
   ribbon (`RibbonBatchJob`). Both share the same shape: `Execute(index)` writes into a pre-sized *slice*
   of a flat oversized-per-item column, taken from consecutive entries of a monotonic offset table a serial
   sizing node built, so the attribute goes on the SINGLE struct field nesting those columns
   (`TriangulationBuffers`/`RibbonBuffers`) — verified empirically that a
   `[NativeDisableParallelForRestriction]` on an OUTER struct field legalises a non-`index` write through a
   nested `NativeList<T>`, even though `NativeList<T>` itself lacks
   `[NativeContainerSupportsMinMaxWriteRestriction]` (package source); a control run with the identical body
   and no attribute throws instead. **Per-item slice disjointness is structural, not asserted** — consecutive
   entries of a monotonic table share no element for any values the table can hold, so the bytes one item
   touches are a function of its own input alone, independent of which worker runs it or how many run at
   once. Each sizing job (`SizingJob`/`RibbonSizingJob`) ALSO asserts its own offset tables are
   strictly increasing (a plain `if` → an error flag, compiled into every build) — this is **defence-in-depth
   against a non-monotonic table surfacing as a player-build error code instead of an Editor-only
   `GetSubArray` bounds throw**, not what makes the parallel node bit-exact; no edit to a sizing job can make
   two items' slices overlap. **This protection reaches the WHOLE downstream chain because every node that
   holds a sizing-owned buffer struct (`TriangulationBuffers`/`RibbonBuffers`) bounds its own loop —
   or its deferred count — by a column its sizing job resizes, never a borrowed count that job's early return
   does not touch. Nodes past the aggregate bound by columns the *aggregate* sizes, and inherit emptiness
   through it.** Three nodes (`AggregateJob`, `RibbonAggregateJob`, then `FillGatherJob` found later —
   the release-build memory-safety defect this rule guards against RECURRED three times, not once) used to
   bound by a borrowed count instead, which a release build's stripped `NativeList`/`NativeArray` bounds check
   turned into a silent out-of-bounds write/read one node past where sizing correctly stopped; every node now
   bounds by the real count sizing resizes. `FillSizingJobTests
   .SizingCapacityOverrun_LeavesGatherAndAggregate_WithNothingToDo` is the observing tooth for
   `FillGatherJob.Execute` and `AggregateJob.Execute` — it drives both, over the same buffers;
   `LineRibbonSizingJobTests.AggregateOverUnsizedBuffers_ProducesEmptyOutput_AndDoesNotIndexPastThem` is its
   line twin, for `RibbonAggregateJob.Execute`. Neither tooth constructs `EarcutBatchJob`/
   `RibbonBatchJob` — those two have no loop of their own to bound (each is deferred over a sizing-owned
   column), so nothing above reds if their deferred-count SOURCE regresses to a borrowed count; that is what
   the consumer-set fence and a `RequiredTokens` pin on the literal `Schedule` call below cover instead.
   **The attribute fence is a structure test** over production sources in `MapRenderer.Jobs/` and
   `MapRenderer.Unity/Rendering/Meshing/` (both directories §3.2 places graph nodes in — scoping it to one
   assembly would leave the stream-write jobs unfenced), enumerating the directories rather than a
   hand-listed set, asserting it found files to scan so a moved directory cannot pass it vacuously, and
   pinning each sanctioned (file, token, count) triple rather than a bare filename — a bare filename would
   exempt that file from EVERY forbidden token at ANY count, which would have let
   `[NativeDisableContainerSafetyRestriction]` through on the same file. **Sanctioned today:
   `EarcutBatchJob.cs` and `RibbonBatchJob.cs`, one `[NativeDisableParallelForRestriction]` occurrence
   each; `[NativeDisableContainerSafetyRestriction]` stays at zero everywhere, unconditionally.**

   **The consumer-set fence is a separate structure test**, over the same two directories: it counts which
   files declare a sizing-owned buffers struct as a field (a regex on the type plus any identifier, not a
   literal including the field name — a renamed field must not walk past it) and asserts that set against a
   pinned allowlist of seven (`FillMeshGraphStructureTests
   .GraphNodes_DeclaringASizingOwnedBufferStruct_MatchTheKnownConsumerSet`). It reds on an EIGHTH consumer,
   naming it — the failure that actually recurred three times was the borrowed bound; a stale consumer set is
   what let the third instance (`FillGatherJob`) hide, and this fence is what catches a stale set mechanically,
   not what value bounds a loop (no fence can check that — it is dataflow, not lexical).
   Test assemblies are out of scope by construction, which is what lets the delay instrument use
   `[NativeDisableContainerSafetyRestriction]` legitimately.
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
  tile. The write graph and every ownership rule are unchanged either way — only the node count per tile
  moves.
- *Tooth:* the probe itself is the deliverable; its numbers are dated and recorded here, not in source.

**Measured 2026-09-02** (Editor, safety checks on, `BurstCompiler.IsEnabled=True`, six read-only container
fields per node, warmed up so one-time compilation is excluded). Cost per `Schedule` call, sweeping the
number of edges created before one flush:

| edges | independent (µs/edge) | chained (µs/edge) |
|---|---|---|
| 1 | 2.90 | 2.20 |
| 16 | 0.24 | 0.31 |
| 64 | 0.23 | 0.37 |
| 256 | 0.28 | 0.31 |
| 1024 | 0.26 | 0.29 |

**Verdict: the per-layer shape stands; stages 1–3 as written.** An edge costs ~0.3 µs to create and the cost
is flat in the number of edges — the `n=1` row is first-call overhead the warm-up does not reach, not a
fixed per-batch price. Flush (`ScheduleBatchedJobs`) stays under 10 µs at every N, so it is not a hidden
per-edge cost in disguise.

Sizing it against the frame: at 0.3 µs, spending 1 ms of a 16.7 ms Tick on scheduling buys ~3 300 edges.
Production `MaxMeshBuildsPerTick` is **2** (`MapViewConfig.cs:46`), so that is ~1 650 edges per kick against
a per-layer graph's 10–15 nodes per fill layer — the shape does not come close to the budget, and the fused
`LayerInputTable` variant is not needed. The EditMode suites that raise the cap to 64 have ~50 edges per kick
per layer at the same price, which is why the gate's runtime does not move either.

The caveat this probe does not cover: the nodes are read-only, so the job system inserts no write-conflict
dependencies. A real graph's write edges cost more to register. That raises the constant, not the shape —
the margin here is ~3 orders of magnitude, so it does not change the verdict.

### Stage 1 — the fill graph, with no production caller

`MapRenderer.Jobs/FillMeshGraph`: the scheduled form of `FillMeshPipeline.Schedule` — same `LayerInput`,
returning `FillGraphOutput { outputs, Handle }` uncompleted. Adds the nodes §3.2 lists:
`SizingJob`, `FillGatherJob` (with the sort), `EarcutBatchJob` (a serial `IJob` looping polygons inside
`Execute`, taking `GetSubArray` views there and calling `EarcutJob.Execute()` directly — `EarcutJob` is not
modified), `AggregateJob`, `Dispose(handle)` scratch nodes, `ProjectionDispatch.Schedule`
(the `internal` generic entry point, §10) and the error flag. `FillMeshPipeline.Schedule` is untouched.

- *Invariant:* pure addition; the existing suite is unchanged. This holds because `EarcutJob`'s fields are
  not touched. If the compile checkpoint shows `GetSubArray` inside `Execute` is not viable under Burst for
  some stream, the fallback is the field-compatible route — extend the existing `OutIndexOffset` idiom to the
  other streams with legacy callers passing `0` and full-length views — and the invariant becomes "existing
  callers are edited to pass zero offsets; the suite is otherwise unchanged". Which route was taken is
  recorded here at landing.
  **Landed 2026-09-02:** all three compile-checkpoint unknowns took the primary route — `GetSubArray` inside
  `Execute` compiles, so `EarcutJob` is untouched and the invariant held as written; `NativeSortExtension.Sort`
  with the struct comparer compiles under Burst; and one struct may implement both `IJobParallelFor` and
  `IJobParallelForDefer`, so no fused projection job was needed and stage 6's scope is unchanged. The one
  divergence is `RingAssemblyJob`'s deferred ring count: the planned optional `NativeArray` field fails
  Unity's schedule-time container validation when left at literal `default`, regardless of `[ReadOnly]` and
  even through `.Run()`, so it is a `bool` selector instead. **An optional job field is safe additive for a
  value type and fatal for a container** — it compiles clean and fails only at runtime, so nothing but a gate
  run catches it.
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
  nothing past capacity (RED: remove the guard → the Editor bounds check throws instead of the flag being set).
  *(A sixth tooth covering the chunk plan was specified here and is struck — `ChunkPlanJob` and its test were
  deleted with the chunking retraction, §3.6.)*

### Stage 2 — the substrate, with the background tile as first client

> **LANDED 2026-09-03 as `a8e713c5`** (gate 2554/2554). What shipped differs from the text below in four
> ways, recorded so a later reader trusts the code over the plan:
>
> - **`BuildStep` has four values, not three.** The text says `Prologue | Measure | Write`; the code is
>   `None | Seam | Measure | Write`. `None` distinguishes "no build in flight", which is what
>   `HasMeshBuild == false` carried and a three-value enum cannot express; `Seam` names what it is — the
>   managed `IWorkScheduler` seam — rather than "prologue", which does not exist for a background tile.
>   Stage 3 renames `Seam` → `Prologue` when the prologue becomes real.
> - **The write job is nested inside its builder**, not a file of its own: `StyledFillTileBuilder.WriteJob.cs`
>   declares it as a private nested struct of a `partial` class. Each builder privately owns its vertex
>   layout *and* its write job, so stage 5's line builder nests its own the same way. §3.2's placement rule
>   is unchanged — it is still in `Meshing`.
> - **A write-side output struct the design never named:** `MeshWriteOutput` mirrors `FillGraphOutput` on the
>   write side (`Mda`, bounds, vertex count, handle, `IsCreated`, with `TakePayload`/`Dispose`). §3.2 rule 1
>   describes exactly this shape but names only the measure-side struct. Stage 5 should copy it rather than
>   reinvent it — the name is mesh-neutral on purpose.
> - **`TileManager.GraphDepsForTest` is a deliberate convention exception.** `conventions-short.md` forbids a
>   member existing solely for a test; this is a new `internal` one. It is accepted because teeth (c)/(d)
>   test the *pump's* pen and teardown behaviour, which driving `TileBuildGraph` directly cannot reach, and
>   because `MeshBuildGateForTest` is an established, exactly-parallel precedent. It becomes production-
>   meaningful at stage 7, when a nativized prologue yields a real `JobHandle` — **not** at stage 3, whose
>   prologue is still managed and yields none.
>
> **Two teeth in the list below could not fail as first written**, and the reason is worth carrying forward.
> (a)'s tick-gap measured the graph's job latency rather than the pump's structure, so folding the two steps
> together did not shrink it; it now asserts the *arm-separation* the pump guarantees (the tick
> `MeshDataArraysAllocatedLastKick` fires versus the tick `TilesConsumedLastTick` does). (b)'s empty layer had
> a zero-length visit order, absorbed by an `!IsCreated` guard one line above the zero-vertex guard it named,
> so that guard had no observer until a degenerate-ring fixture was added. Both were found by *executing*
> the injections, not by reading the tests.
>
> **The write step shipped with no parity tooth at all** and one was added at review: nothing read a byte it
> wrote, and the snapshot suites only sample colour dominance under a lit shader, so a flipped tangent `w` or
> a dropped normal was invisible. `ScheduleWrite_MatchesManagedWriteGeometry_StreamForStream` now compares
> all four streams, the indices and the bounds against the managed writer, on both projections.

> **Reordered 2026-09-02: the globe-subdivide stage below lands FIRST.** Planning this stage found that
> "`KickSourcelessBackground` schedules the graph directly" and "byte-identical background snapshots" cannot
> both hold as written. The shipped demo scene runs `UseGlobe: 1` (`OpenStreetMapLiberty.unity:498` →
> `MapHost.cs:129` → `SphericalProjection`), and the background quad branches into `WriteGlobeSubdivided`
> whenever `MaxRefineAngleRad` is finite (`StyledFillTileBuilder.cs:449`) — a node `FillMeshGraph` does not
> have until the subdivide stage.
>
> The alternative was gating the graph client on a planar projection and deleting the gate later. It was
> rejected because it would ship a substrate the only shipped scene never executes — the same hole as this
> epic's two coverage defects (a parity sweep running the unclipped arm while production clips; a projection
> dispatch reached only through `case null` while production always passes a real projection). Landing
> subdivide first lets the background wire **once**, under both projections, with no gate to remember to
> remove.


`TileBuildGraph`, the `BuildStep` state on `LoadedTile` (and its readers: pump, drains,
`AwaitInFlightMeshBuilds`, `CaptureTelemetry`, `RenderTeardownRecord`, `DoDispose`), the graph pen,
`ScheduleBatchedJobs()` once per Tick, the `FillStreamWriteJob`, the measure→write step in the pump with
one exact-size `MeshDataArray(1)` **per non-empty layer**, the `MeshDataArraysAllocatedLastKick` counter,
and the test-only delay-job seam (§5). `KickSourcelessBackground` schedules the graph directly (its quad
materializer runs on main at kick); source tiles are untouched and still go through `KickMeshBuild`.

- *Invariant:* byte-identical snapshots (the background snapshots and every suite that renders a
  background); source-tile behaviour unchanged. The cache is untouched here and stays untouched — see §3.6's
  consequence (1).
- *Teeth:* (a) **step progression** — after the Tick that kicks a background tile, `AllTilesSettled()` is
  false and the tile's `BuildStep == Measure`; after the next, `Write`; consumed on the one after (RED: a
  pump that `Complete()`s at kick settles in one Tick). This proves scheduling order only — see §7.4;
  (b) **exact-size allocation** — the write step allocates one `MeshDataArray` per *non-empty layer*, sized to
  that layer's measured vertex/index count, and `MeshDataArraysAllocatedLastKick` equals the non-empty layer
  count (RED: allocate for every layer including empty ones, or allocate a fixed capacity → the count and size
  assertions fail);
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

**Recorded in stage 2, deferred deliberately: pooling `TileBuildGraph` and its `LayerBuild[]`.** Both are one
managed allocation per tile build, and "recurs per tile" puts them on the data plane by
`gc-and-allocation-design.md`'s own discriminator, so they are fair game — `pooling needs no measurement`
applies. They are **not** done here for a sequencing reason, not an oversight: this stage is the one that adds
`TileBuildGraph`'s dispose-idempotency and double-schedule guards, and adding instance reuse on top of a
disposal protocol whose double-dispose case was unguarded is how a use-after-free is built. Pool it once the
protocol has teeth.

What *was* closed here: the graph arm takes `MeshDataPayloadPool.Rent()` + `Reset(...)` rather than
`new MeshDataPayload(...)`. The arm it replaces (`TileBackgroundLayerProcessor`) constructs directly while
`MeshDataPayload.Dispose()` returns to the pool unconditionally — so that path has been *feeding* the pool
without ever drawing from it, paying construction every build. The machinery already existed and only
`TileMeshLayerProcessor` used it.

**A throughput consequence to trace in stage 3, not a defect.** Charging *both* the measure kick and the
write kick against `MaxMeshBuildsPerTick` (default 2) halves the number of new tiles that can start per Tick
compared with the seam arm's single kick. That is the intended accounting — one write kick is one budget unit,
per §3.6's §4.1 row — and it is a throughput *choice*, not an error: the two steps do different work and each
costs main-thread time. It is worth **one trace** during stage 3 on a low-zoom pan, where new-tile rate is
highest, to confirm the +2-Tick latency (§3.1) does not compound with a halved kick rate into a visible fill-in
delay. Raise the budget rather than un-split the accounting if it does.

> **Superseded 2026-09-05.** The maintainer's ruling (§11 fork 2) overturned the accounting this paragraph
> defends — the halving no longer applies. The single trace asked for above was never taken, and is no
> longer needed: the argument that retired the rule is structural, not measured (§0/§10). This paragraph's
> own advice was "raise the budget rather than un-split the accounting" — the ruling took the other branch.

**Three findings recorded at stage 2's commit, deferred deliberately** (final architecture pass, 2026-09-03;
none blocking, all verified against the shipped tree):

1. **The graph arm has less exception resilience than the seam arm it replaces, and stage 3 should fix it by
   moving ownership rather than adding a guard.** Both consume sites take the payload array into the pump's
   *local* `LoadedTile` copy and only then call `ConsumeMeshBuild`. If that throws — a backend `AddLayer`, a
   mesh apply — `_loaded[key] = lt` is never reached, so the stored record has `Payloads == null` while the
   graph's `_payloadsTaken` is already set. Every later Tick then re-enters, calls
   `CompleteWriteAndTakePayloads` again and throws, forever, and the taken `MeshDataArray`s leak because only
   the discarded local referenced them. The seam arm survives this because it re-reads its payloads from
   `GetResult()`, which still holds the same array with its consumed slots nulled.
   **The fix is a simplification, not a patch:** let `TileBuildGraph` own the taken array — cache it, return
   it on a repeat call, sweep it in `Dispose()`. That deletes `LoadedTile.Payloads`, both `if (lt.Payloads ==
   null)` guards, the "called twice" throw *and* the `DisposeWholePayloads` line in `RenderTeardownRecord`:
   one ownership rule replacing a throw-guard plus a caller-side null-guard defending against that throw.
   Stage 3 puts more state on this graph, so it benefits directly.

2. **Design tooth (c) is half-covered.** §8 stage 2 (c) asks for a release during `Measure` **and** during
   `Write`; only the Measure release is tested. The Write-in-flight release path — `MeshWriteOutput.Dispose()`
   on a real, still-scheduled `MeshDataArray`, freed through `DisposeTracked` — has no observing tooth, and
   the teardown tooth also holds at Measure. The path reads correct; it is unobserved. Add the Write case
   (hold `GraphDepsForTest`, pump one Tick past the measure-complete arm, then evict) or amend this doc — but
   do not leave the doc asking for a tooth nothing satisfies.

3. **`TileBuildGraph._ownedGeometry` bakes the background's shape into the graph type.** It is a single slot
   holding *one shared quad per tile*, and `DisposeLayerInput` explicitly refuses to free per-layer geometry.
   But `FillMeshPipeline.LayerInput.Geometry`'s own doc states a source-tile layer's geometry is a per-layer
   **private** buffer, so stage 3 cannot reuse the slot as-is. It needs either per-layer ownership on
   `LayerRequest` or a list of owned buffers (N = 1 today) alongside the decode keep-alive §6 already
   promises. Not a tear-up — `LayerRequest` and `Dispose` are the only touch points — but plan it rather than
   discover it.

### Stage 3 — source tiles: prologue → measure → write, and the cache ripple

> **LANDED 2026-09-03 as `f8e61a7e`** (gate 2566/2566, compiled clean, results written by that run, on a
> tree verified unmoved since its freeze). Written before the commit rather than after because source
> comments were already citing plan tokens (`DIV-1`, `R1b`, `R7`, `DIV-4`) that appear **nowhere in this
> document** — the plan is transient scaffolding and must not be cited from `.cs` files. What shipped
> differs from the text below in these ways, recorded so a later reader trusts the code over the plan:
>
> - **The measure kick is charged NOTHING for a source tile.** §3.6 and §10 say "one measure kick and one
>   write kick are each charged as one unit". The pump's arm (1) — the prologue-complete hand-off that calls
>   `ScheduleMeasureFromDecode` — is **uncharged** against `buildCap` and bumps no kick counter. So the real
>   charging is: a **source** tile costs 2 units (prologue kick + write kick), a **background** tile also
>   costs 2 (measure kick + write kick). This is a deliberate divergence — a tile that has already paid for
>   its prologue should not be blocked from starting the Burst work it exists to do — but it was justified
>   in source only by the unresolvable token `DIV-1`, which is why it is written here.
>   **Nothing observes this contract.** It lives in prose plus a mirrored doc on a test extension; a future
>   budget change can silently redefine what a "kick" costs. A tooth for it is an open follow-up.
> - **Two counters, not one.** `MeshBuildsKickedLastTick` becomes a **budget-unit** counter (2 per tile, per
>   above); `TileBuildsStartedLastTick` is the new per-**tile** counter, incremented once at a tile's FIRST
>   kick. Five existing drives that read `kicked >= LoadedTileCount()` were repointed at the latter, because
>   the old reading silently became "half the tiles started".
>   **Superseded 2026-09-05:** the rule the two bullets above record was deleted by §11 fork 2's ruling —
>   the budget-unit counter named above no longer exists, so its name is historical. The "tooth for it is
>   an open follow-up" line is closed by that same stage's new teeth.
> - **`BuildStep.Seam` → `Prologue`**, as stage 2's block predicted. Still four values (`None | Prologue |
>   Measure | Write`).
> - **`ScheduleMeasureFromDecode` is a distinct NAME, not an overload.** Two `ScheduleMeasure`s separated
>   only by `TileGeometryBuffers` vs `SharedDisposable<IDecodedTile>` are unresolvable for a `default`
>   argument (CS0121) — precisely the case a test standing up a bare "nothing to own" graph writes. The
>   codebase had already rejected this shape once, when `LayerRequest` gained named `Create`/`PassThrough`
>   factories so the arm is a stated choice rather than an overload-resolution accident.
> - **`TilePrologueOutput` is the hand-off type**; `MeshBuildResult` is deleted. It owns the kick's decode
>   reference and a dense `LayerRequest[]`, and is disposed either by `TileBuildGraph.Dispose()` once handed
>   over, or by a pen drain that never got there.
> - **`TileBackgroundLayerProcessor` → `BackgroundQuad`**, a producer-only `static class`;
>   `TileLayerProcessorRunner.RunSourcelessWorkerPass` is deleted outright.
> - **`DrainMeshBuilds` uses two NON-CHAINED `if`s**, so one tile can walk Prologue→Measure→Write→consumed
>   within a single loop iteration. Both review arms traced this specifically, because stage 2 shipped a
>   null-deref in this exact function: `Graph` is always assigned in the same statement group that
>   transitions `Step` to `Measure`, and `FinishConsume` is now the single disposal site, which is what fixes
>   the partially-consumed tile that used to dereference a nulled graph.
>
> **`LayerRequest`'s own doc overclaimed, and it was written during this stage.** It said the factories are
> "the only way to build one", that each "asserts the other arm's field is absent", and that half a request
> is "unrepresentable". There are no asserts, `KickSourcelessBackground` builds one with an object
> initializer, and `LayerRequest` is a struct with public fields — so "unrepresentable by construction" is
> unachievable in C# regardless of asserts. Both review arms found it independently. Recorded because the
> failure mode is the recurring one in this epic: a confident register ("by construction", "unrepresentable")
> that stops the reader checking.
>
> **The R4 non-vacuity witness holds in ONE of the pen cases and is vacuous in two — the DRIVE decides,
> not the assertion.** All four pen cases originally ended with
> `Assert.Greater(DebugTotalRequestsCreated, totalCreatedBefore)`, described in the plan as "advancing only
> after the gate opens". Measured 2026-09-03:
> - **Measure case — vacuous.** It drives `InlineWorkScheduler`, so the rest of the multi-tile cover runs to
>   completion during the wait loop: **3 requests created before the pan, gate still shut.** The witness is
>   satisfied by unrelated tiles. **Dropped**, with the limitation recorded in the test; safe because that
>   case carries an independent pairing (`Greater(DebugLiveCount, baseline)` after eviction,
>   `AreEqual(baseline, …)` after drain).
> - **Write case — same pairing, so R4 was redundant. Dropped.**
> - **Prologue case — the witness HOLDS**, and is the only case where it does. Its gate is a real shared
>   `MeshBuildGateForTest` blocking *every* cover worker, so no `LayerRequest.Create` can run while it is
>   shut — confirmed by an assertion that is now a **permanent precondition** in that test, not a throwaway
>   probe. If anyone changes that drive to `InlineWorkScheduler`, the precondition reds instead of the
>   witness silently going vacuous.
>
> Two things generalise. **A process-wide static counter is a poor witness for a per-instance claim** whenever
> the fixture has more than one instance in flight — `DebugTotalRequestsCreated`, `DebugLiveCount` and
> `DebugLiveAllocCount` are all process-wide. And **a fix that is right in one sibling test can destroy the
> only working witness in another**: copying the Measure drop into Prologue would have removed its sole
> non-vacuity check.
>
> **`DebugLiveRequests`/`DebugTotalRequestsCreated` count only requests built through `LayerRequest.Create`.**
> `KickSourcelessBackground` bypasses it, so background-tile requests are invisible to those counters. Inert
> today — the only teeth reading them are source-tile-only — but a leak tooth written against background
> tiles using them would be vacuously green.


`KickMeshBuild` splits. The prologue (selection, paint bake, sort-key order, the symbol worker pass) runs on
the seam as today and returns native per-layer columns; the pump schedules fill layers' graphs over them.
Line and fill-extrusion layers keep their synchronous `WriteInto` *inside the prologue* for this stage
(mixed tile: fill via graph, line via prologue) — the consume path already handles heterogeneous payloads.
`PreparedTileCache` is **untouched**: one mesh per layer keeps its `Mesh`-valued entry correct (§3.6
consequence (1)), so D7's value-shape change is not part of this stage.

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
  (f) **cache round-trip unchanged** — build → release → revisit of a fill layer returns the same vertex count
  and one mesh per layer, with the existing cache suite passing untouched (this stage's claim is that the
  cache does not change, and that suite is the observer).
  *(Two teeth specified here — the overshoot bound and a chunked cache round-trip — are struck: both assert
  K > 1 meshes per layer, which the chunking retraction makes impossible. §3.6 consequence (2).)*

**Obligation inherited from stage 2: retire the background tile's dead seam path.** Once the background
kicks through the graph, `TileLayerProcessorRunner.RunSourcelessWorkerPass`,
`TileBackgroundLayerProcessor.ProcessOnWorker` and its `CreateProcessor` path have **no production caller** —
their only remaining callers are tests, and `TileBackgroundQuadProjectionTests` consequently pins the arm
that no longer ships. Delete all three and repoint that test at `BuildLayerInput` + `TileBuildGraph`, so the
corner values it guards are checked on the path production actually takes. Deferred out of stage 2 only
because it is a retirement with its own test-repointing, not because it is optional; recorded here rather
than in a source comment, which is where stage 2 first put it.

### Stage 4 — globe subdivide and fill-extrusion roofs as graph nodes

> **LANDED 2026-09-04 as `f5e13c19`** (gate 2571/2571, compiled clean, results written by that run, on a
> tree verified unmoved since its freeze — both byte-identical and Assets/-scoped). **Group A, commit 1 of
> 2; Group B landed separately as `c1702ed7` — see its own note below.**
>
> The extrusion roof measures through `FillMeshGraph.Schedule` completely unchanged — no new node, no
> signature change. The write is a new kind-specific job
> (`StyledFillExtrusionTileBuilder.FillExtrusionStreamWriteJob`, nested in
> `StyledFillExtrusionTileBuilder.WriteJob.cs`) that writes the roof at `[0, Vr)` then the walls at
> `[Vr, Vr+Vw)` with wall indices rebased `+Vr` — one exact-sized `MeshData`, matching the "what stage 4
> actually does" paragraph below exactly as planned. The walls themselves are unchanged (still a
> prologue-built managed loop, `StyledFillExtrusionTileBuilder.WriteWalls`) **[stage-4 record; SUPERSEDED
> by §8 stage 5, the wall-job-graph stage — `WriteWalls` is deleted and the walls are scheduled nodes of
> `FillExtrusionMeshGraph`. Left as written because it records what stage 4 shipped.]** except that their five output
> lists moved from method locals into a request-owned `WallColumns` struct so the write job can read them.
> `TileBuildGraph.LayerRequestKind` (`None`/`PassThrough`/`Fill`/`FillExtrusion`) is the one discriminator
> `CompleteMeasureAndScheduleWrite` switches on; a request reaching the write step with no matching case
> throws rather than silently dropping a mesh (a review finding — see the stage-4 plan's B1).
>
> **Group B LANDED 2026-09-04 as `c1702ed7`** (gate 2569/2569, compiled clean, results written by that run,
> on a tree verified unmoved since its freeze — both scopes). The synchronous fill pipeline
> (`FillMeshPipeline.Schedule`, `TileMeshBuffers`,
> `WriteGlobeSubdivided`/`WriteGlobeRoof`, `GlobeFillSubdivideDispatch.Run`/`RunTyped`,
> `GlobeFillSubdivideJob.SrcVertCount`/`SrcIndexCount`/`CountsFromArrayLength`, and the dead curved-arm
> projection this banner describes) is retired. `FillMeshGraph.Schedule` is the only mesher; its regression
> pins are frozen per-stream goldens captured from the synchronous implementation before deletion
> (`FillMeshGraphParityTests`, `FillMeshGraphGlobeParityTests`, `StyledFillExtrusionGraphWriteTests`,
> `TileBuildGraphTests` — R6, B.7). The banner below is now historical: the dead-projection work it describes
> was deleted with `FillMeshPipeline.Schedule`'s stage-4 branch, not left removable for later.

> **The curved path projects every vertex and discards the result.** `GlobeFillSubdivideJob` takes only
> `TileVerts`, `TriangleIndices` and `VertexFeatureIdx`, and projects internally (`GlobeFillSubdivider.cs:176`
> `Project(double2)`); it never reads `WorldPositions`/`VertexUp`, and `WriteGlobeSubdivided` discards them.
> So on a globe — which is what the shipped demo scene runs — the tile→geo and project stages are O(V) of
> dead work per layer per tile. Found while planning this stage, verified 2026-09-02.
>
> **It was deliberately NOT removed at the time this was written.** Parity against the path then current was
> field-for-field, and the instrument that proved the spherical arm was actually entered read exactly those
> world positions — deleting the work would have deleted the instrument. It became removable once the write
> step existed and parity could be asserted on the subdivided output alone (§3.7's reshape), and was in fact
> removed in Group B, together with the rest of the synchronous pipeline — see the "Group B landed" note
> above.


`GlobeFillSubdivider.Schedule`; the globe write becomes a stream-write job over the subdivided lists; the
extrusion roof reuses `FillMeshGraph`.

> **"Walls stay in the prologue" was ambiguous and this sentence used to be unshippable. Corrected
> 2026-09-03** after an architecture review read it literally and showed it cannot work. Walls are NOT a
> second *payload* alongside a graph-produced roof: `StyledFillExtrusionTileBuilder.WriteMeshData` writes roof
> and walls into ONE `MeshData`, and §3.6 emits exactly one exact-sized `MeshData` per layer — so two
> producers for one layer leaves only bad exits (a managed wall write on the main thread after measure,
> which is what this epic exists to remove; or two meshes per layer, which §3.6 retracted and
> `PreparedTileCache`'s `Mesh`-valued entry cannot hold).
>
> **What stage 4 actually does** *(stage-4 record; the walls left the prologue in §8 stage 5 — see the RESOLVED note in that section)*: the walls stay a *prologue-built loop* — off-main, on the seam, exactly as
> today — but they travel as **request-owned native columns with wall-local indices**, not as a payload. The
> write job then emits one mesh: roof `[0, Vr)`, walls `[Vr, Vr+Vw)`, wall indices `+ Vr`, roof indices
> winding-swapped as today. `Vr + Vw` is known at allocation time (roof from the completed measure, walls
> from the prologue's own lists), so the one-exact-sized-`MeshData` rule holds and the vertex order is
> byte-identical to today's. `LayerRequest` therefore needs no third half-and-half state — extrusion is one
> `Kind`, carrying two extra owned columns.
>
> **Walls are NOT blocked on the line graph.** The old wording tied them to stage 5 because "they share the
> per-ring shape" with line. That reason is thin and a future reader will over-read it: walls neither
> subdivide nor ribbon. A wall job is `TileToGeo` → `ProjectionDispatch` — nodes that already exist — plus
> one serial quad-per-edge `IJob`, roughly 60 lines, **independent of the line graph**. Record it as its own
> follow-up ("wall quad job, after stage 4, may land before or after line"), not as a stage-5 dependency.
>
> What stage 4 *deferred* was only the wall loop's **execution placement**: a managed `ProjectPoint` per
> vertex, off-main on desktop and inline on main on web. That was the same dated cost the line layer already
> carried, with the same expiry — a dense city tile is thousands of buildings × ~6 edges × **2**
> `ProjectPoint` calls each (the edge's two endpoints; the four emitted quad vertices reuse them — an earlier
> version of this note said 4 and was 2× high), a cost worth sizing at the time this was written.
>
> **Update (job-scheduling-design.md §8 stage 5, the wall-job-graph stage): this half of measurement 4 is now
> CLOSED, not pending.** The wall loop no longer runs a managed `ProjectPoint` inline on web's main thread at
> all — it schedules through the Burst `ProjectPointsJob<TProj>` (`FillExtrusionMeshGraph.Schedule`), the
> same node the roof already used. There is no separate wall-loop main-thread cost left to size here; only
> the prologue's own selection+paint-bake half of measurement 4 (below) remains open.
>
> **RESOLVED (job-scheduling-design.md §8 stage 5, the wall-job-graph stage) — the boundary this warned about
> was crossed, and "byte-identical" was NOT assumed, it was demonstrated.** The wall loop used to project
> through the *managed* `IProjection.ProjectPoint`; it now projects through the Burst
> `ProjectPointsJob<TProj>` (`FillExtrusionMeshGraph.Schedule`), the same node the roof already used — the
> exact managed→Burst crossing this warning said no stage had made. The parity probe this paragraph called
> for is `WallQuadJobParityTests`/`StyledFillExtrusionGraphWriteTests`: the wall/graphwrite goldens, captured
> from the pre-stage managed computation, compared against the Burst output byte-for-byte (linear quantities)
> or within a measured ULP bound (quantities downstream of a transcendental), RED-verified, checked against
> an independently-held pre-stage content manifest of every fixture file. It held.
>
> The full derivation, and the review that verified the `PreparedTileCache` key collision that rules out the
> two-mesh exit, are in the stage-4 plan.

- *Invariant:* byte-identical globe snapshots (`GlobeSnapshotTests`, `GlobeFillWindingTests`,
  `GlobeFillTangentTests`).
- *Teeth:* parity of the subdivided output against the current `WriteGlobeSubdivided` path over the fixture
  (RED: drop the tangent stream); the vertex budget still binds (the existing budget tooth).

### Stage 5 — the line graph

> **The epic's "byte-identical snapshots" invariant is NOT yet established for line or for the extrusion
> walls, and no prior stage's result covers them.** Recorded 2026-09-03 while planning stage 5.
>
> Every stage so far could claim byte-identical output because fill already went through
> `ProjectionDispatch.Run` — the **same Burst job** the graph uses (`FillMeshPipeline.cs:554`). Managed-
> versus-Burst projection was never on the table. Line is different: it projects through the **managed**
> `IProjection.ProjectPoint` today (`StyledLineTileBuilder.cs:320`, `:340`) — verified 2026-09-03. The
> extrusion walls did too, at the time this was written; the wall-job-graph stage (job-scheduling-design.md
> §8 stage 5) moved them onto the Burst `ProjectPointsJob<TProj>` chain, closing that gap — see the RESOLVED
> note above.
>
> **RESOLVED for the walls (§8 stage 5, the wall-job-graph stage): all three crossings were made, and the
> warning below is why that mattered.** As recorded 2026-09-04, the walls were **three** managed computations,
> not one — `TileToGeoJob.GeoAt`, `proj.ProjectPoint`, and `MetresToWorldFactor` — and this block had named
> only the middle one. **A probe covering only projection would have left two of them unmeasured.** What
> settled it was therefore not a projection probe but the full per-vertex golden comparison: all three now run
> inside Burst nodes of `FillExtrusionMeshGraph.Schedule` (`TileToGeoJob`, `ProjectionDispatch.Schedule`, and
> `MetresToWorldFactor` inside the `[BurstCompile]` `WallQuadJob`), and every wall golden held byte-identical
> across the swap, verified against a content manifest captured before the stage began.
>
> Recorded as it happened: the move also **halves** the projection call count — each ring vertex is projected
> once, rather than once as edge *i*'s A and again as edge *i-1*'s B. That is a call-count change, not a
> behaviour change, since both projections are stateless structs; the goldens confirm it.
>
> **For LINE the boundary is still uncrossed**, and it is now the *second* crossing rather than the first —
> so the walls' outcome is evidence available to it, not a precedent it must establish alone. What the wall
> stage demonstrates is narrower than "managed and Burst agree": it is that *these* crossings, measured
> per-vertex against pre-captured goldens rather than assumed, held. Line must still measure its own.
>
> The one datum the repo has points away from bit-exactness: `RibbonJob`'s own doc claims parity with
> the managed tessellator **"to floating-point epsilon"**, not bit-for-bit. Note also that
> `ProjectPointsJob.cs:26` says it calls the *same* `ProjectPoint` as the OOP `IProjection` — same source
> is not the same codegen, since Burst's math mode may reassociate.
>
> **So do not write "byte-identical" into a line or wall plan on inherited authority.** Settle it first with
> a throwaway EditMode probe comparing the two projection paths bitwise over the boundary fixtures, BEFORE
> any production line is written (stage 5's plan carries this as A0.2):
> - **green** ⇒ strengthen the invariant to byte-identical vertex buffers; parity teeth use exact equality;
> - **red** ⇒ epsilon parity at a *measured* tolerance, rendered goldens remain the authority, and a moved
>   golden is stop-and-report, never a re-bake.
>
> **Record the probe's outcome here, not only in the plan.** If it reds, this section's invariant needs a
> carve-out naming line and walls; if it greens, "managed and Burst projection agree bitwise on this
> hardware" is worth stating once so no later stage re-derives it. Writing the stronger claim first and
> loosening it under time pressure is how a real regression gets waved through.

> **Outcome (measured 2026-09-04, wall-job stage A0, before any wall-job production code existed —
> `stage-wall-job-plan.md`'s A0.2/A0.3 probes, `ProjectionManagedVersusBurstTests`): RED. The invariant at
> this boundary is TOLERANCE-BOUNDED, not exact — and the bound is not a flat epsilon, it is per field,
> decided by whether the field is linear in the inputs or downstream of a transcendental.**
>
> Two shared jobs were measured — `TileToGeoJob.GeoAt` and `ProjectPointsJob<TProj>` — against their managed
> twins (`IProjection.ProjectPoint`), over every ring vertex of both `boundary_3` fixtures
> (`boundary-6-34-21.pbf.bytes`, `boundary-9-274-168.pbf.bytes`), both compiled `[BurstCompile]` with
> `OptimizeFor = OptimizeFor.Performance` and **no `FloatMode`** (this repo has never set one — `grep -rn
> FloatMode Assets/Code` is still zero hits after this measurement).
>
> **The invariant, stated mechanistically:** a quantity that is LINEAR in the inputs is bit-exact between
> managed and Burst; a quantity downstream of a TRANSCENDENTAL (`atan`, `sinh`, `sin`, `cos`) diverges by a
> few ULP, because Burst's relaxed-math reassociation / FMA contraction under `OptimizeFor.Performance` only
> touches the transcendental call sites — if the two formulas actually differed, the linear terms would
> diverge too, and on every fixture measured so far they never do.
>
> **The MAGNITUDE of that divergence is NOT a fixed number — it is fixture- and call-site-scoped, and reusing
> one measurement's ULP figure as a bound on a different call site is wrong.** Two measurements, same two
> jobs, same underlying trig divergence, wildly different ULP counts:
>
> | measurement | call site | `World` origin | max ULP: `World` | max ULP: `Up` |
> |---|---|---|---|---|
> | `ProjectionManagedVersusBurstTests` (`boundary_3`, `origin = double3.zero`) | probe-only, A0 | none — raw `pp.World` at planetary magnitude (~6.37 Mm) | 4 (Spherical `.x/.z`) | 2–3 (Spherical) |
> | Wall-tail (square+courtyard fixture, `TileRenderOrigin.Project(tile, proj)`) | production — `ProjectPointsJob.cs:57`, `WorldPositions[index] = pp.World - OriginWorld` | tile-local RTC origin | **512**, every one of 12 wall vertices | 2 (matches the row above) |
>
> **The discriminator is "is a large magnitude subtracted from this quantity before it is compared", not the
> fixture and not the geometry.** `World` goes through `pp.World - OriginWorld` — two ~6.37 Mm doubles
> subtracted to a ~1e4 tile-local result. The subtraction is catastrophic cancellation: the ABSOLUTE error
> from the trig divergence is unchanged by it, but the RESULT is now ~640× smaller, so the same absolute
> error counts as ~128× more ULP (512/4 ≈ 128; 6.37e6/128 ≈ 5e4, an ordinary tile-local magnitude — the ratio
> is the mechanism, not a coincidence). `Up` proves the rule from the other side: `ProjectPointsJob.cs:58`,
> `Normals[index] = pp.Up`, has **no origin subtraction** — unit magnitude in, unit magnitude out — and its
> ULP count is the same (2–3) in both measurements, because there is nothing to amplify. **No ULP figure
> anywhere in this block may be reused as a bound at a different call site (a different origin, or no origin
> subtraction at all) without re-measuring at that site** — the line-graph stage measures its own bound for
> exactly this reason, and this sentence is why that was the right call, not caution for its own sake.
>
> **Downstream of `WorldPositions`, the float32 narrowing (`WallQuadJob.cs`, `float3 posA = (float3)World[idxA]`)
> erases the origin-relative divergence STRUCTURALLY, not by luck of one fixture.** 512 ULP of a double at
> tile-local magnitude (~1e4) is ≈9.3e-10 absolute; a float32 ULP at that same magnitude is ≈9.8e-4 absolute
> — six orders of margin (verified: `9.8e-4 / 9.3e-10 ≈ 1.05e6`). That ratio holds for any tile-local
> geometry, not just this fixture, because RTC's whole purpose is to keep origin-relative magnitudes in this
> range — so the wall job's hard bit-exact `Position` assertion
> (`StyledFillExtrusionGraphWriteTests.AssertStreamComponent`, `maxUlp: 0`, roof AND wall tail) is a
> STRUCTURAL guarantee, not a number that happened to read zero on one fixture: a red there is a formula
> error, not drift, and the assertion should not be softened to a bound. (`Up` carries no such guarantee — it
> is never origin-subtracted, so its 2–3 ULP reaches the narrowed `float3` un-amplified and un-erased.)
>
> **The divergence was located directly, in the real production code path, not inferred.** A probe
> (`_DiagnoseFirstDivergentStep`, deleted after use) ran `WallQuadJob`'s own formula — `posA`/`posB`/`upA`/
> `upB`/`upAvg`/`edgeDir`/`outward`, in computation order — over the REAL `RingSelectJob`→`TileToGeoJob`→
> `ProjectionDispatch.Run` columns the wall-tail fixture (square+courtyard, Spherical) actually produces,
> checked step-by-step against the retired managed loop on the SAME edges, with a Burst-enabled precondition
> and a post-run Burst-error log grep (this is what settled UMR-94 for this job type — see below). ENUMERATED
> over all 12 wall edges, not sampled — one edge in isolation had already told the wrong story once this
> stage: **2 of 12 edges diverge, 10 agree completely.** Edge 4 diverges first at `upAvg = normalize(upA +
> upB)`; edge 6 diverges first at `edgeDir = normalize(posB - posA)` — a DIFFERENT `normalize` call site,
> confirming the effect is not localized to one expression. `posA`/`posB`/`upA`/`upB` never diverge first on
> ANY of the 12 edges on this fixture (0 counts each), and `outward` never diverges FIRST either (0 count) —
> meaning `math.cross(edgeDir, upAvg)`, given bit-exact inputs, stayed exact on every edge here too, matching
> the earlier isolated-cross measurement — `cross` is EXONERATED as a first cause on this fixture: measured,
> not merely unmentioned. Two of twelve — **about 17%** — is a minority, not a systematic effect: closer to a
> rounding-boundary straddle than a routine one, though "systematic" and "straddle" are both descriptions of
> the same underlying fact (relaxed-math `normalize` does not guarantee managed's rounding), not different
> mechanisms. It also explains this investigation's own false starts: a single-edge probe on edge 0 alone had
> roughly a 5-in-6 chance of finding nothing at 17% incidence — which is exactly what happened three times
> running, and is why an earlier reading of this data concluded (wrongly, and was corrected twice) that the
> arithmetic never diverges. Sample one edge and the odds favour a clean result even when the effect is real.
>
> **This resolves UMR-94 for this job type by construction, not by assumption: a silently-managed-fallback
> job cannot disagree with managed code, and this one does, on two specific fields, at two specific edges,
> while agreeing everywhere else** — that is the signature of genuine Burst codegen taking a different
> (relaxed-math) path on some inputs, not evidence of a compile failure (which `CompileSynchronously = true`
> would have surfaced as a build error, and the log has none).
>
> **So `WallQuadJob`'s own `normalize` DOES diverge from managed on some inputs — but not on others, and not
> only at one call site.** The SAME formula (`normalize(upA + upB)`), run on a DIFFERENT fixture/edge in an
> earlier, narrower probe, was bit-exact — and `WallQuadJobParityTests` independently found a THIRD call site
> diverge: `upA = normalize(Up[idx])` itself, on the high-latitude/Spherical fixture (not this one), at 1 ULP.
> All results are real; none is vacuous (every one ran genuinely Burst-compiled, confirmed the same way).
> **The mechanism is therefore: Burst's relaxed-math `normalize` (likely an approximate `rsqrt` plus a bounded
> refinement step, standard for `OptimizeFor.Performance`) does not guarantee the SAME rounding as managed
> IEEE `sqrt`+divide for every input, at every call site — it agrees for most inputs, and disagrees by roughly
> 1 ULP for some, data-dependently, with no further characterization attempted (disproportionate for a doc
> footnote — the measured BOUND on the actual wall-tail output, not a derivation of which inputs trigger it,
> is the tooth that matters; see below).** `Normal`/`Tangent`'s 1–2 ULP (and, on high-latitude, `ExtrudeUpAndT`'s)
> is this `normalize` divergence reaching the output — not a third, independent source, and not (as an earlier
> draft of this paragraph proposed) the narrowed `Up` failing to absorb its own upstream noise: on THIS
> fixture's edges 0-3/5/7-11, `upA`/`upB` measured bit-exact going INTO `upAvg`, so there was no upstream noise
> left to propagate there; the divergence is `normalize`'s own, and it can appear at ANY of its call sites in
> this job, on inputs this measurement has not further characterized.
>
> **A hypothesis this hazard nearly produced, worth recording as a correction rather than silently dropping:**
> `WallQuadJobParityTests`' high-latitude/Spherical failure was first attributed to `MetresToWorldFactor`'s
> latitude derivative growing near the pole and amplifying `Geo.Latitude`'s already-recorded 3-ULP double
> divergence. That mechanism is real for the FLAT arm (`factor = 1/cos(latitude)`, latitude-dependent) but
> does not apply to the failing case: on the GLOBE arm `MetresToWorldFactor` returns the constant `1.0`
> regardless of latitude (`Globe ? 1.0 : …`), so `Geo.Latitude` cannot be the mechanism there. The actual cause
> is simpler and already established above: `upA = normalize(Up[idx])` is itself a `normalize` call, and this
> is one more input on which it diverges. Recorded so a future reader does not re-derive the wrong mechanism
> from a plausible-sounding but unchecked derivative argument — checking it against the actual code branch
> taken is what caught it.
>
> **The ceiling this bound is not self-fulfilling against:** the wall-tail Normal/Tangent ULP bounds
> (`StyledFillExtrusionGraphWriteTests`'s `WallNormalMaxUlp*`/`WallTangentMaxUlp*`) are the MEASURED 1–2 ULP
> plus a stated `+2` platform margin — not derived from nothing. The value that would have made this a
> stop-and-report defect instead of a recorded bound: anything approaching the scale a real transcription
> error moves bytes by, which was independently measured on this exact test — an injected wall-tail
> transcription error (`WallQuadJob`, edge-partner swap) produced **614458 ULP**, five orders of magnitude
> above the 1–2 measured here. A bound of a few ULP sitting five orders below the smallest real defect this
> stage's own RED-verify could produce is not a fudge factor; it is a band with enormous clearance on both
> sides. (This does not contradict `ProjectionManagedVersusBurstTests.cs`'s "never widen a bound past its
> measured figure" — that rule governs the DOUBLE-domain probe, where E5 is ruled and closed; the wall-tail
> figures here are a FLOAT-domain platform margin on a different, structurally-explained quantity, measured
> and bounded independently.)
>
> **Maintainer's ruling (E5-equivalent, wall-job stage): option C, corrected.** The plan's stage-wall-job-plan.md
> §2 wrote option C as *"exact in the double domain, tolerance-bounded on the derived float streams"* — this
> measurement falsifies that phrasing: `ProjectPointsJob`'s `WorldPositions`/`Normals` are `double3` and
> `GeoCoordinate.Latitude` is a `double`, so the divergence is IN the double domain, not only in a later
> float cast. The invariant that actually holds is **linear ⇒ bit-exact; transcendental-downstream ⇒
> tolerance-bounded, at a magnitude that depends on whether the quantity is origin-subtracted before
> comparison** — never a flat number carried between call sites. `FloatMode` was considered and **not**
> adopted — neither shared job takes one now, and none should be added on their account.
>
> **Cost, stated plainly:** this epic gives up bit-pinning at the managed↔Burst projection boundary for any
> field downstream of a transcendental — except where a structural argument (like `Position`'s six orders of
> narrowing margin) proves the divergence cannot survive to the compared quantity, in which case the bit-exact
> pin stays and is stronger for being justified rather than merely observed. The precedent extends to every
> nativization after this one that crosses the same boundary (line's stage 5 shares this exact probe file,
> `ProjectionManagedVersusBurstTests`, per R7) — measure the bound AT THE CALL SITE that will ship, not at a
> zero-origin probe, and re-derive whether narrowing structurally erases it before asserting bit-exactness.

**What shipped (Group A landed; folded in from the retired standalone `README-line-graph.md`, which
duplicated this section once the graph existed — see this file's own "Files" table, now below).**
`LineMeshGraph.Schedule` chains: `RingGatherJob` (the ring gate — selection + LineString kind + line's
own `>= 2` length filter, never fill's `>= 3`) → `TileToGeoJob` → `ProjectionDispatch` (the ORIGINAL
centerline's per-point surface up — the subdivision metric only; that pass's `world` output is read by
nothing, `StyledLineTileBuilder.cs:320`) → `SubdivideJob` (the managed `SubdivideCenterline`'s
`LineCurvatureSubdivision.SegmentSteps` split, as a Burst job) → `TileToGeoJob` → `ProjectionDispatch` again
(the SUBDIVIDED centerline's real position/up columns) → `RibbonBatchJob` (loops rings inside one
`Execute`, running `RibbonJob` per ring; winding-swaps + offsets indices; the always-bound-loops vertex
ceiling). No arm split, unlike fill: a flat projection's infinite `MaxRefineAngleRad` makes
`SubdivideJob` a no-op (1 step/segment), so the flat case is the chain's own degenerate value, not a
branch. Projection stays a closed enumeration in `LineMeshGraph.Schedule`; the generic
`LineMeshGraph.ScheduleTyped<TProj>` entry point (internal — `ProjectionDispatch.ScheduleTyped` widened
`private` → `internal` alongside it) is how `RightHandedSphereProjectionWindingTests` reaches a projection
Burst never registers generically, bypassing `Schedule`'s switch entirely — the mechanism the previous
revision of this paragraph said did not exist; it now does, by construction, not by assumption.

| file | role |
|---|---|
| `LineMeshGraph.cs` | the graph builder — schedules every node, owns the dispose nodes, returns the output |
| `LineGraphOutput.cs` | the output columns + terminal handle, and their disposal |
| `LineGraphCounts.cs` | the error codes |
| `LayerInput.cs` | `LayerInput` — the per-layer input descriptor |
| `RingGatherJob.cs` | the ring gate + filter that produces the ring set the rest of the chain walks |
| `SubdivideJob.cs` | curvature subdivision, driven by the projection's refine-angle tolerance |
| `RibbonBatchJob.cs` | loops rings, running `RibbonJob` per ring; winding swap + vertex ceiling |
| `RibbonJob.cs` | the projection-agnostic 3D ribbon builder (join/cap topology, one code path) |

- *Invariant:* byte-identical line snapshots (`LinePaintSnapshotTests`, `LineAaSnapshotTests`,
  `GlobeLineSnapshotTests`); `LineRibbonJobTests` unchanged.
- *Teeth:* parity of the ribbon output against the current builder over the fixture, per ring (RED: swap the
  subdivide tolerance); **the winding tooth through the graph** — `RightHandedSphereProjectionWindingTests`
  builds its curved right-handed projection through `LineMeshGraph.ScheduleTyped<RightHandedSphereProjection>`
  and asserts the ribbon winds the same way as flat Mercator relative to the surface normal (RED: derive
  winding from handedness — a `curved ⇒ flip` in the ribbon job — which inverts exactly this case while
  leaving `SphericalProjection` right; the test's original defect, now provable on the graph path).

### Stage 6 — within-stage parallelism (landed)

Four tuned batch constants (fill/project/line/extrusion tile→geo — `VertexBatch = 1024` each, one shared
reasoning: `TileToGeoJob`/`ProjectPointsJob` are ~10-50 µs of work per 1024 vertices, comfortably above a
batch hand-off's own cost); `EarcutBatchJob` → `IJobParallelForDefer` over `FillMeshGraph`'s own
`buffers.PerPolyOuterCount` (`EarcutPolygonBatch = 1` — earcut is superlinear and corpus polygons vary by
orders of magnitude, so batch 1 lets the job system's work-stealing balance the load); the ribbon restructured
into sizing → `RibbonBatchJob` (`IJobParallelForDefer` over rings, `RibbonRingBatch = 1`, same reasoning)
→ aggregate. The stream-write jobs (fill/extrusion/line) were measured (§9) and NOT parallelised — the
measurement did not clear its own pre-committed gate.

- *Invariant:* byte-identical everything (held — the frozen fill/line goldens and every snapshot suite passed
  unchanged); the disabled-safety attribute confined to two job types, one occurrence each (§7 rule 2, above)
  — the plan's original wording ("no disabled-safety attribute outside `EarcutBatchJob`") is corrected by
  A0.3's resolution: the ribbon needed the identical shape, so the sanctioned set is
  `{ EarcutBatchJob, RibbonBatchJob }`, not one job.
> **What bit-exactness actually rests on.** The **slice derivation**, not any assertion: each item
> (polygon/ring) reads and writes only `GetSubArray` views taken between *consecutive* entries of a monotonic
> offset table a serial sizing node built — so the bytes one item touches are a function of its own input
> alone, independent of which worker runs it, in what order, or how many run at once. Each sizing job
> (`SizingJob`/`RibbonSizingJob`) additionally asserts its own tables are strictly increasing —
> defence-in-depth against a non-monotonic table surfacing as a *player-build* error code
> (`ErrorOffsetTableNotDisjoint`) instead of an Editor-only `GetSubArray` bounds throw, **not** what makes the
> parallel node bit-exact. No edit to a sizing job can make two items' slices overlap; the determinism
> tooth's RED injection therefore goes in the CONSUMER (`EarcutBatchJob`/`RibbonBatchJob`), colliding the
> base of adjacent items — e.g. `int sOff = workOffsets[pi - (pi & 1)];` — never in the sizing job.
> **A.2 cleared, contrary to this plan's own reweighting.** The primary-source finding that `NativeList<T>`
> lacks `[NativeContainerSupportsMinMaxWriteRestriction]` was read as meaning the disable attribute could not
> legalise a non-index write through a nested list — empirically, it does: `[NativeDisableParallelForRestriction]`
> on the OUTER struct field suppresses the parallel-write safety check entirely for a container that lacks
> index-restriction support, not narrowly a bound it lacks the metadata to enforce (a control run with the
> identical body and no attribute throws *"is not declared [ReadOnly] in a IJobParallelFor job"* instead). So
> both `EarcutBatchJob` and `RibbonBatchJob` keep their single `Buffers` field, one attribute each — the
> smaller of the two probed shapes, not the 11-written/7-read-only field split A.1 also proved viable.
>
> **A.1's own result, for the record:** a deferred `NativeArray<int>` field with `[NativeDisableParallelForRestriction]`,
> written outside `index`, alongside a `[ReadOnly]` deferred sibling, with `GetSubArray` taken inside
> `Execute(int)` and handed to a nested job's fields — also compiled and ran clean under
> `IJobParallelForDefer`. Both A.1 and A.2 clear; A.2's shape shipped because it is the smaller diff.
- *Teeth:* `GraphDeterminismTests` (§7.3) — worker-0-vs-default self-comparison over the FULL column set
  (including post-subdivision columns, both fill arms) plus the frozen fill-parity goldens at their real
  reach, extended with a line-graph arm over `LineGraphOutput`'s columns; both flushed with
  `JobHandle.ScheduleBatchedJobs()` before `Complete()` (an unflushed "default" pass runs near-inline and
  compares serial to serial). Contention guards sized to the machine (`max(8, JobWorkerCount)`), not to `> 1`.
  `ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus` (B.6) — one arm per batch constant (six: fill/
  project/line/extrusion tile→geo, earcut, ribbon), each reading THAT constant's own declaration and
  RED-verified by restoring it to `1 << 20` — an arm that stays green when its own constant is restored reads
  a different constant than it claims. The attribute-fence structure test, extended to a (file, token, count)
  triple per sanctioned file rather than a bare filename (a bare filename would exempt that file from EVERY
  forbidden token at ANY count — RED-verified: adding `[NativeDisableContainerSafetyRestriction]` to a
  sanctioned file still reds). `FillSizingJobTests`/(new) a standalone monotonicity-assertion arm, RED-verified
  by injecting equal consecutive offset-table entries in the sizing job (never a claim that this demonstrates
  a race — see the box above).

  **The three primary REDs, executed, observed text below (not a recipe — this is what actually happened;
  correcting one wrong prediction in place, per this stage's own review):**
  - **B.5 (i)** — `EarcutBatchJob.Execute`, `int sOff = workOffsets[pi - (pi & 1)];`: observed **not** a
    digest mismatch but a fatal `IndexOutOfRangeException` thrown from `AggregateJob` (the collided base
    makes a polygon's merged count exceed its slice, and the aggregate read past `FlatVx`). The tooth reds —
    through a downstream bounds check, not the two-pass digest comparison — and the run crashed the Editor
    process, a stronger divergence than the digest mismatch this doc predicted.
  - **E.4** — `RibbonBatchJob.Execute`, `RingVertexOffsets[r - (r & 1)]`: observed *"Indices diverge from
    the captured oracle"* on all four `LineGraphParityTests` cases — no crash this time.
  - **B.5 (ii) — the prediction below was WRONG; corrected in place, not merely annotated.** Restoring
    `FillMeshGraph.VertexBatch` to `1 << 20` reds `GraphDeterminismTests` **via its contention guard**
    (`GraphDeterminismTests.cs`'s `vertexCount >= minBatches * VertexBatch` assertion), **not** via the digest
    comparison — the guard reads the LIVE constant, so restoring it to something absurdly large fails the
    guard before the determinism assertion ever runs. The plan predicted green on the assumption the guard
    was independent of the constant it was restoring; it is not. B.6 still covers a distinct claim (whether
    the corpus offers more than one batch), but this specific restoration is no longer a clean demonstration
    of that — it reds for an unrelated, correct reason first.
- **Error-path bonus (C.4), extended by review to close a second hazard the first pass introduced.**
  `EarcutBatchJob`'s deferred count source is `buffers.PerPolyOuterCount` — a `SizingJob` capacity
  early-return leaves it at length 0, so the earcut runs ZERO batches on that path. **That alone was not
  sufficient**: `AggregateJob` (fill) and `RibbonAggregateJob` (line, new this stage) both used to
  take their own loop bound from a BORROWED count (`PolyCountArr[0]` / `RingSubOffsets.Length - 1`) that a
  sizing early-return never touches, so the parallel node's own safe zero-batch behaviour did NOT stop the
  serial aggregate one node downstream from indexing a length-0 `NativeList` for the polygon/ring count the
  UPSTREAM job still reported. `NativeList<T>`'s indexer bounds check is
  `[Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]` — compiled OUT of a release player build — so this was
  not a throw, it was `Ptr[index]` past the allocation, on a path the fill side has been reachable through
  since before this stage (`SizingJob`'s own doc: a test can hand it undersized capacity). Both aggregates
  now bound their own loop by a column their own upstream sizing job resizes (`PerPolyMergedVertexCount.Length`
  / `PerRingVertexCount.Length`), which reads 0 exactly when sizing bailed early — so the WHOLE chain, not
  just the earcut/ribbon node, now safely no-ops on a sizing failure instead of reading garbage-length flat
  lists in a later node.
- **Group D (stream write) — measured, gate refused, D.2 not attempted; a completed group, not a dropped
  one.** Two throwaway single-half `[BurstCompile]` `IJob`s per production write job (vertex half, index
  half), timed on the main thread around `Schedule().Complete()` with `JobHandle.ScheduleBatchedJobs()`
  flushed first and a warm-up pass absorbing Burst's first-schedule JIT cost, on the largest corpus fixture,
  both projections. All three vertex-half readings landed at or below the pre-committed "did not clear"
  threshold — see §9's dated rows for the numbers.

### Stage 7 — prologue nativization (first step) and seam retirement from the tile path

> **SUPERSEDED 2026-09-05 — this title is false on both halves; the stage's recorded intent is left as
> written.** After this stage the prologue is **still managed** (paint bake needs a native expression VM that
> does not exist and is a separate project sequenced after this epic — see the resolved condition above), and
> the **seam still stands** (`IWorkScheduler` is the permanent dispatch policy for the residual managed
> bodies; its own doc was corrected this session). What the stage actually is: **batch the filter VM's
> per-feature dispatch.**
>
> It is also **not a scheduling change**, which is how it was characterised. Selection runs inside the
> `IWorkScheduler` body (`TileMeshLayerProcessor.ProcessOnWorker` → `FeatureSelector.SelectFeatures`), where
> §4 rule 1 makes `Schedule` illegal — so a batched job still `.Run()`s and nothing moves off any thread.
> **What it buys is dispatch-overhead elimination on web**, where the factory binds the inline scheduler and
> thousands of per-feature dispatches each copy a ~764 B job struct by value on the main thread.
> **Scope, corrected 2026-09-05 (mesh-path routing stage) — the SYMBOL-only claim below no longer holds.**
> Before this stage the native VM was reached only from the symbol pass: `FeatureSelector`'s list overloads
> passed `native: null`, and `TileMeshLayerProcessor` took those, so of the shipped style's 105 filtered
> layers 25 (symbol) took the native path and 80 (66 `line` + 14 `fill`) ran the managed `CompiledFilter`.
> **The mesh-build path now routes too**: `TileMeshLayerProcessor.cs:85,91` pass the resolved `ITileLayer`
> through a new `FeatureSelector.SelectFeatures` overload, so both cadences probe the native seam. On the
> largest committed real-data fixture (`boundary-9-274-168.pbf.bytes`), all **77 of 77** resolvable
> fill/line layers bound a native matcher — zero managed fallback on that data (§9 row 11 has the reading
> and the per-tile timing it also took). Binding is still per-layer, per-tile, data-dependent (a literal
> string colliding in a layer's value table refuses — `NativeFilterRebind.cs`), so this is a measured
> reading on one fixture, not a compile-time guarantee. **Whether the mesh path should route natively was
> the open question this stage answered — it is no longer a "planned separately, gated on a measurement"
> item: nativization here is the accepted direction (not a measurement bet), and this stage executed it.**

> **"When the prologue is empty" is doing a lot of work in this section, and as written stage 7 does not
> empty it.** Recorded 2026-09-03 after an architecture review; the three parts have very different
> evidence, and only one is a scheduling change:
>
> - **Selection — real, and mechanical.** The native filter VM exists (`MapRenderer.Jobs/Expressions`,
>   `MvtLayer : INativeFilterSource`), but today it `.Run()`s **one job per feature**
>   (`NativeFilterEvaluator.cs:58`) and only for a compilable subset — single-arm `match` only
>   (`NativeFilterCompiler.cs:319`), everything else falls back to managed. Batching over the feature range
>   is the mechanical part. **What nobody has measured is production coverage:** what fraction of the
>   *shipped style's* filters actually compile natively. A synthetic-layer tooth cannot tell you that — it
>   drives the branches the fixture has. A one-line telemetry counter can.
>
>   **CORRECTED 2026-09-05 — half of this is stale, and only half.** The **subset restriction is real and
>   unchanged**: single-arm `match` only (`NativeFilterCompiler.cs:319-320`), and `:152` refuses every
>   unlisted head. Every one of those refusals is a deliberate byte-identity argument — **do not read this
>   correction as licence to widen the accepted subset.** What is stale is the *inference* that everything
>   outside the subset falls back to managed **in practice**: the shipped liberty style sits **entirely
>   inside** the subset — every one of its 105 filtered layers **compiles**.
>
>   **`CoveredLibertyFilters_Count_Is105` is a COMPILE-coverage tooth, not a dispatch-coverage one — it was
>   miscited here as settling both.** It asserts every liberty filter compiles and the refused set is empty;
>   it observes no call site and says nothing about which cadence reaches the VM or whether a compiled
>   program actually binds on real tile data (`NativeFilterRebind`'s ≥2-id refusal is per layer, per tile,
>   from the data — compiling is not binding). The mesh-path routing stage (above) is what closed the
>   dispatch-coverage half for the mesh cadence, with a real measurement: 77/77 resolvable fill/line layers
>   bound on the largest committed fixture. **No standing telemetry counter exists or is needed** — the
>   routing tooth (`TileMeshLayerProcessorNativeFilterRoutingTests`) pins that the call site is reached; §9
>   row 11 is where a bind-rate reading like this belongs going forward.
> - **Paint bake — NOT a scheduling change.** `StyledFillTileBuilder.cs:361`/`:376` (colour, opacity) and
>   `:161` (sort key) evaluate managed `Expression` trees over `IFeature`. There is **no native expression
>   VM for paint**: `Core/Expressions` is ~1.9k lines plus ~1.1k of ops (ramps/interpolate, match/case,
>   strings, coercions). This section already says it "stops at selection and records the gap" in that case
>   — so be plain about the consequence: **emptying the prologue is an expression-VM project, a different
>   kind of work from a job-graph stage, and it lands after stage 7, not in it.**
> - **The symbol pass — out of scope by decision (§5), and it keeps the kick managed regardless.**
>   `KickMeshBuild`'s `symbolPass?.RunWorkerAndHandoff(decode)` rides the prologue. Even with selection and
>   paint both native, the tile kick still needs a managed body unless symbols get their own kick. A vehicle
>   exists — `SymbolSubsystem.PumpBuilds` already dispatches through its own `WorkScheduler` — but **nobody
>   has written that down as a stage-7 prerequisite.** It is one now.
>
> **§11's 5-before-7 fork is still ungated.** It hinges on measurement 4 (prologue ms versus the Burst chain
> it used to run inline, on a real web trace), which §8 stage 2 asked for "during stage 3" and which is
> **not recorded anywhere**. Get the number before planning stage 4's successor: if paint bake dominates,
> the answer is the expression VM and the plan should name it rather than sequencing around it. Stage 4's
> deferred wall loop — a dense city tile is thousands of buildings × ~6 edges × 4 vertices with
> double-precision trig per vertex — was going to need sizing in this same trace; the wall-job-graph stage
> closed that half (job-scheduling-design.md §8 stage 5): the wall loop schedules through Burst now, so it is
> no longer part of what this measurement needs to size.
>
> **CORRECTED 2026-09-05: "still ungated" no longer holds.** §11 fork 1 was **CLOSED BY ACTION on
> 2026-09-04** — the line graph shipped (`4b373d12`, then `6a1ec875`), so "stage 5 before stage 7" is simply
> what happened, settled by a landed stage rather than by measurement 4. Measurement 4 itself remains
> overdue and worth taking (see §9 row 4) — what is false is that it gates fork 1. Left as written above
> because it records the state of the 2026-09-03 review.


Selection as one job over the layer's feature range through the native filter VM (replacing the per-feature
`NativeFilterEvaluator.Evaluate` loop); paint bake as a job iff the native expression VM covers the paint
properties the default style uses — otherwise this stage stops at selection and records the gap. When the
prologue is empty, `TileManager` drops its `WorkScheduler` seam and the gate guard (§5 names the replacement
for the gate's test capability).

> **CORRECTED 2026-09-05: this clause is unreachable, and the epic no longer aims at it.** The prologue is
> not emptied by this stage or by any planned one — paint bake needs a native expression VM that does not
> exist (`Core/Expressions` is 3,065 lines across 29 files, all managed) and is a separate project sequenced
> after this epic; the symbol pass rides the kick regardless. **`IWorkScheduler` therefore does not retire.**
> It is the permanent dispatch policy for the residual managed bodies, and its own doc was corrected to say
> so. Left as written because it records what stage 7 was planned to reach.

> **The condition above is decidable today, and it evaluates to "stops at selection" — recorded 2026-09-04.**
> There is no native paint-expression VM. A native *filter* evaluator exists (`NativeFilterEvaluator`); paint
> has nothing, and `MapRenderer.Core/Expressions` is 3,065 lines across 29 files, all managed. So this stage
> stops at selection **by its own stated rule**, and no measurement is needed to decide it — the earlier
> framing, that a web trace would settle whether paint bake dominates, would have bought an instrumentation
> stage to confirm a branch already taken. The maintainer has separately confirmed (2026-09-04) that the
> expression VM is its own project, sequenced after this epic completes.

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
| 2 | serial span of `SymbolProjectionJob`/`CullJob` at realistic symbol counts vs the `ScheduleParallel().Complete()` fan-out cost | rule 3 for the per-frame symbol sites | any time |
| 3 | `Complete()` wait on the symbol collision at the next Tick with N tile graphs in flight on web (5 workers) | whether an in-flight-graph cap below `MaxConcurrentTileLoads` is needed. **This is the one runtime hazard with no mitigation in the plan**: jobs have no priorities, and a queued collision behind a long earcut batch stalls the main thread at `HarvestCollision`. The mitigation, if it bites, is that cap — a knob on `TileBuildGraph` scheduling, not a design change. | after stage 3, on a web trace |
| 4 | on web, the prologue's main-thread ms per tile (selection + paint bake) vs the Burst chain it used to run inline | how much of the web win stage 3 delivers versus stage 7; sizes what stage 7 has left to win | **OVERDUE — not taken. Stage 3 is complete and this number does not exist**, and it is still worth taking. It no longer *gates* anything, though: §11 fork 1 was **CLOSED BY ACTION on 2026-09-04** (the line graph shipped — `4b373d12`, then `6a1ec875` — so "stage 5 before stage 7" is simply what happened, settled by a landed stage rather than by this measurement), and §11 fork 2 was separately **CORRECTED on 2026-09-04** to say it is not gated on measurement 4 either. It is one `Tools/build.sh web` away and would still size the web prologue's main-thread cost. The extrusion wall loop no longer belongs in this trace — job-scheduling-design.md §8 stage 5 (the wall-job-graph stage) moved it off the main thread onto the Burst `ProjectPointsJob<TProj>` chain (`FillExtrusionMeshGraph.Schedule`), so it is not a main-thread cost to size here any more; what was a per-edge managed `ProjectPoint` cost (thousands of buildings × ~6 edges × 2 calls each) is now scheduled work, not prologue work. |
| 5 | ~~whether `Mesh.MeshData.SetVertexBufferParams`/`SetIndexBufferParams` are callable from inside a Burst job~~ | **CLOSED 2026-09-02, no run needed.** Off-main: **yes, already proven by shipping code** — `StyledFillTileBuilder.WriteMeshData` runs off the main thread (`StyledFillTileBuilder.cs:29`) and calls `SetVertexBufferParams` at `:457`/`:543`, with `:47` stating these APIs are off-main-thread safe. Inside a **Burst** job: no — it is a managed API taking a `NativeArray<VertexAttributeDescriptor>`. And it does not matter: `AllocateWritableMeshData` is main-only (`MeshDataPayload.cs:37`) and the vertex/index counts are measure-graph outputs, so the main-thread allocate+size step stays regardless. **Write step shape: main allocates and sizes one `MeshData` per layer; a Burst job writes the data.** *Partial — this closes the SHAPE, not every question.* The evidence covers writes made **synchronously on the writing thread**, which is what `WriteMeshData` does. It does **not** cover handing a `MeshData`-derived `NativeArray` to the job system's safety registration and reading it back after `Complete()` — nothing in this repo has done that, and it is what the stream-write job needs. That half is now **CLOSED, measured 2026-09-02**: a `[BurstCompile] IJob` **scheduled** (not `.Run()`) over a `NativeArray<T>` taken from `Mesh.MeshData.GetVertexData<T>()` writes correctly and reads back after `Complete()` + `ApplyAndDisposeWritableMeshData`, with no Burst-error output. **CORRECTED 2026-09-02, during stage 2:** that probe cleared a **single** stream, and the conclusion drawn from it — "so the stream-write job takes plain `NativeArray<T>` fields" — was **wrong for the real job, which writes four**. Every stream view of one `Mesh.MeshData` is tracked under a **shared** safety handle, so two *writable* views cannot be two job fields: scheduling throws *"Stream0 is the same … as Stream1, two containers may not be the same (aliasing)"*. **The shipped shape is this row's own documented fallback** — the job holds the whole `Mesh.MeshData` as ONE field and resolves every stream/index view inside `Execute()`. The probe was not wrong about what it measured; the *generalisation* from one stream to four was never measured, and a single-stream instrument is structurally blind to an aliasing defect that needs two (`lessons-learned.md`, "blind spots don't transfer between instruments"). Caught by the existing background snapshot suite, not by any tooth this stage wrote — which is what the byte-identical invariant is for. | closed |
| 6 | the pen's worst-case `Complete()` at teardown with the load stress scene | confirms "bounded, no timeout" | after stage 3 |
| 7 | a one-line EditMode probe: `Schedule` from a ThreadPool thread throws (Unity's documented contract, confirmed once in this project's version) | rule 1's premise; if it ever did not throw, the three-step split would still be the design (the write step needs main for `MeshData` allocation), but the prologue could hand off without a Tick boundary | stage 0, alongside 1 |
| 8 | **[2026-09-05, stage 6, A.4]** corpus sizes, measured before any batch constant was chosen: fill `maxTileVertices=228276 maxPolygonCount=3218` (largest fixture, both projections); line `maxRibbonVertices=15209`. At `JobsUtility.JobWorkerCount=14`, `max(8,14)=14`; `14 * VertexBatch(1024) = 14336` — both numbers clear the contention guard by a wide margin. | the four `VertexBatch=1024`/`EarcutPolygonBatch=1`/`RibbonRingBatch=1` constants, and `GraphDeterminismTests`' contention guards | closed |
| 9 | **[2026-09-05, stage 6, B.7]** the earcut node's OWN span (not whole-graph), largest fixture, `EarcutBatchJob.Schedule(...) → ScheduleBatchedJobs() → Complete()`, default worker count vs `JobWorkerCount=0`: two runs measured `default≈3.7-4.7ms zero≈20.6ms`, ratio **4.4×-5.6×**. Clears the 1.3× refusal by a wide margin — **Group C kept.** | whether Group C (parallel earcut) is kept or reverted | closed |
| 10 | **[2026-09-05, stage 6, D.1]** the three stream-write jobs' vertex/index halves, timed via two throwaway single-half `[BurstCompile] IJob`s (warmed up first — Burst's first-schedule JIT cost otherwise contaminates the measurement, confirmed empirically: an uncorrected first case read ~2.6 **seconds**), largest fixture, both projections where applicable: fill vertex half **0.20-0.84 ms**; extrusion vertex half **1.32-1.51 ms** (astride the 1.0-1.5 ms band the design explicitly reads as "did not clear" regardless — Editor safety checks bias this instrument toward opening); line vertex half **≈0.11 ms** (at the "cannot discriminate" floor). None clears the ≥1.0 ms proceed gate with the stated calibration. **Group D: measured, D.2 not attempted for any of the three — a completed group.** | whether Group D (parallel stream write) is attempted | closed |
| 11 | **[2026-09-05, stage 7, mesh-path routing]** HEAD (managed `CompiledFilter`, list overload) vs the routed overload (native VM), over liberty's real 80-layer fill+line corpus (77 of them resolve against the fixture) driven against `boundary-9-274-168.pbf.bytes` — the largest committed real-data tile (landcover 1471, place 289, mountain_peak 86, water 76, transportation 74, park 72, landuse 21, waterway 4, boundary 2; **11,941 feature-filter evaluations**). Burst warmed with a discarded pass first; three runs/arm, median, four separate process launches: **3 of 4 agree — head ≈4.1-4.3 ms, routed ≈5.2 ms (routed ~1.0 ms / ~24% slower, ~84 ns/evaluation)**; the 4th launch read head≈6.46 ms / routed≈6.40 ms (near parity), treated as a cold-launch outlier (first batch launch after reimport) and excluded from the reading. The routed arm pays a per-feature `NativeFilterEvaluationJob` struct copy, a `.Run()` dispatch, and five `AtomicSafetyHandle` checks that the managed arm does not — consistent with the measured direction, at roughly double the naive per-evaluation estimate, which fits Editor safety-check overhead (`editor-profiler-overstates-safety-overhead-wins` — this is an upper bound on the release cost, not the release number). **Bind count (F5): 77 of 77 resolvable layers bound a native matcher on this fixture — none fell back to managed on this data.** Nothing refuses on this number — nativization here is the accepted direction, not a measurement-gated decision; this reading sizes what F1's `RunByRef` batching has left to win. | nothing (recorded only) — sizes stage 8/F1's remaining prize | closed |
| 12 | **[2026-09-05, stage 7, F1 batching]** Same fixture/corpus/instrument as row 11 (11,941 evaluations, `boundary-9-274-168.pbf.bytes`, Burst warmed with a discarded pass, 3 runs/launch). **Two comparisons, reported separately because the tree changed mid-measurement:** (a) HEAD vs an INTERMEDIATE per-feature `RunByRef` rung — a job field hoisted onto `NativeFilterEvaluator`, dispatched once per feature (11,941 dispatches) — measured across 3 launches: HEAD `{5.843,7.391,5.504} / {5.145,5.221,5.217} / {5.222,5.145,5.179}` ms, intermediate rung `{4.319,4.263,4.200} / {5.684,5.281,5.164} / {4.372,4.220,4.130}` ms (one of 3 intermediate-rung launches reads in HEAD's own range — an outlier, not excluded, both included above). **This rung is NOT in the shipped tree** — Edit 4 deleted `NativeFilterEvaluator.cs` outright, so this comparison describes a tree this stage discarded, not what ships. (b) HEAD vs the SHIPPED tree — one `RunByRef` per layer-selection (~80 dispatches, not 11,941), `MvtNativeFeatureMatcher.MatchAll` (`MvtNativeFeatureMatcher.cs:72`) — re-measured 2026-09-05 against the tree this epic actually ships, 3 launches, no outliers: `{0.510,0.514,0.508} / {0.498,0.496,0.493} / {0.437,0.444,0.425}` ms. **Batching cuts selection from ≈5.2 ms/tile (HEAD) to ≈0.47 ms/tile (shipped) on this fixture — roughly 11×, not the ~1.0 ms/tile (a) would suggest** — because one dispatch per layer-selection replaces one per feature, not because of any per-feature struct-copy saving (that saving is real but ~2 orders of magnitude smaller, and does not survive into the shipped tree's shape). | nothing (recorded only) — this is the number row 11 asked stage 8/F1 to size, now closed against the shipped tree, not an intermediate one | closed |

## 10. Decisions taken here (overridable), and what was rejected

- **Three polled steps, +2 Ticks — accepted by the maintainer, see §3.1.** Rejected: one graph with a
  main-thread memcpy at consume (reverts the worker-writes-`MeshData` win for a copy that scales with vertex
  count); folding the prologue onto the main thread everywhere (deletes the seam a stage earlier at the price
  of a desktop main-thread regression until nativization).
- **Per-chunk `MeshData` — proposed, adopted, then retracted the same day; see §3.6.** The write graph emits
  one `MeshData` per layer.
- **No bespoke graph description type.** A `Graph`/`Node` DSL would duplicate `JobHandle` and hide the
  safety system's edge check behind a layer it cannot see. Rejected.
- **Projection dispatch stays a closed enumeration.** `ProjectionDispatch.RunTyped<TProj>` /
  `ScheduleTyped<TProj>` (and the globe twin) are `private` — not `internal`, and never were; a decision
  record here previously said `internal` and claimed a test drives a projection production never registered
  by calling the generic entry with its own struct, which was false when written: no test called either
  entry point, so that capability never existed. `RunTyped` (and `ProjectionDispatch.Run`, its only
  synchronous caller) retire with the synchronous fill pipeline (job-scheduling-design.md §8 stage 4 Group
  B); `ScheduleTyped` stays `private`, reached only through `Schedule`'s closed `switch`. The closed `switch`
  is the production enumeration ("the ONE place the concrete projection structs are enumerated", its own
  doc). *Rejected:* a registration-based registry keyed by projection type — process-wide mutable state that
  outlives a domain reload (test-to-test leakage) and erases the enumeration the switch exists to make
  obvious.
- **`EarcutBatchJob` wraps `EarcutJob` rather than changing its fields** (stage 1); the field-compatible
  offset route is the recorded fallback.
- **Error flags replace the never-fired throws inside graphs**; the decoder's twin backstop keeps its throw
  (§3.2, recorded divergence).
- **The prologue keeps `TileBuildScratch` pooling and the symbol pass ordering** (symbol pass after the mesh
  prologue, before the Burst chain instead of after it — the handoff parks or enqueues to main either way).
- **Graphs are scheduled inside the `MaxMeshBuildsPerTick` budget, which counts tiles ADMITTED per Tick.**
  A step transition of an already-admitted tile (prologue→measure, measure→write) is uncharged.

  > **DECIDED (§11 fork 2, ruled 2026-09-05) — the prior two-units-per-tile charging rule was deleted, not
  > fixed.** The argument that killed it: there are **three unequal main-thread costs** per tile — the
  > prologue kick (a cheap closure dispatch on desktop; on web the ENTIRE managed prologue runs inline
  > inside `Schedule`, the most expensive thing in the pipeline), the measure schedule (~13 nodes × layers),
  > and the write kick (exact-size `MeshData` alloc + N jobs). The prior rule charged **two of the three**
  > and left the one it left free — measure — the middle cost, so "2 units per tile" measured nothing
  > physical. The write-kick charge traced back to §4.1's blind-allocation stall (#5), which exact sizing
  > has since closed — the charge had outlived its own justification. This is a **structural** argument and
  > needed no trace to settle; no `MaxWriteKicksPerTick` knob was added in its place. **No new knob until
  > measurement 3 asks for one.**

## 11. Open questions escalated to the maintainer

**Two forks are open as of 2026-09-03, and the first one's own recommendation ("measure 4 during stage 3")
was NOT carried out — stage 3 is complete and that number does not exist.** The stages are sequenced so
either answer to fork 1 works; fork 2 is new.

1. **Order of the second half: line graph (stage 5) before prologue nativization (stage 7), or after?**
   Stage 5 buys parallel line builds on both targets; stage 7 buys the web its remaining main-thread relief
   (the prologue is inline on main there). Measurement 4 sizes the second; the first is structurally certain.
   *Consequence of 5-first:* the web keeps paying the prologue on main for one more stage. *Consequence of
   7-first:* line tiles keep running in the prologue (inline on web) for one more stage, and the paint-bake
   half of stage 7 may be blocked on native expression coverage. **Recommendation:** measure 4 during stage
   3; if the prologue is ≥ the Burst chain on a real web trace, 7 before 5.

   > **CLOSED BY ACTION 2026-09-04 — not by measurement.** The line graph shipped (`4b373d12`, then
   > `6a1ec875` retiring the seam arm), so "5 before 7" is simply what happened. The recommendation above was
   > never carried out: measurement 4 was not taken during stage 3, and this fork was settled by a stage
   > landing rather than by a number. Left as written because it records a real decision point — do not read
   > it as still open.

2. **Should the two-units-per-tile build-budget rule exist at all?** Raised by an architecture review
   2026-09-03; the full argument is at §10, beside the rule it would delete. In short: there are three
   unequal main-thread costs per tile (prologue kick — on web the *entire* managed prologue runs inline;
   measure schedule; write kick), one knob charges two of them, and the one it leaves free is the middle
   cost — so "2 units" measures nothing physical. The write-kick charge was justified by a blind-allocation
   stall that exact sizing has since closed. *Consequence of deleting:* budget becomes tiles admitted per
   tick (`TileBuildsStartedLastTick`), transitions flow uncharged, one counter instead of two — and a
   dedicated `MaxWriteKicksPerTick` is added only if a trace shows write-kick allocation cost. (*This branch
   is the one the ruling below took.*) *Consequence of keeping — not taken, kept for the record:* the rule
   would have stayed unobserved by any tooth, and stage 3 already had to repoint five drives away from the
   unit reading because it was misleading. **This contradicted what stage 3 shipped, so it was a stage-4+
   change, not a stage-3 fix.** The fence *"Stage 4 must not touch a counter or knob until this is ruled
   on"* is **overtaken**: stage 4 landed 2026-09-04 as `f5e13c19` (extrusion roof/walls through the graph)
   and `c1702ed7` (retire the synchronous fill pipeline) — before the ruling below — and touched no counter
   or knob, so the fence was never at risk of being crossed.

   > **CORRECTED 2026-09-04: this fork is NOT gated on measurement 4, and never could have been.**
   > `PmMeshDataAllocate` was taken to be the instrument that would size the write-kick charge. It does not
   > isolate it: the same marker name bracketed **two** of the three costs this fork is about — the write
   > step, and (until the wall-job-graph stage) the kick-time allocation loop. A trace reading it could not
   > say which dominated. The kick-side bracket has since been dropped, because it had begun timing a *pool
   > rent* under a marker called `MeshDataAllocate` — so the marker isolates the write step **now**, but that
   > is a defect this epic fixed, not a measurement anyone took.
   >
   > The case for deleting the rule is **structural** and needs no trace: one knob charges two of three
   > unequal costs and leaves the middle one free, so "2 units" measures nothing physical. A measurement
   > could only ever have *added* a replacement knob back.

   > **CLOSED BY RULING (2026-09-05).** The rule is deleted. Budget becomes tiles admitted per tick
   > (`TileBuildsStartedLastTick`); step transitions of an already-admitted tile (prologue→measure,
   > measure→write) flow uncharged; one counter, not two — the budget-unit counter above is deleted
   > outright. No `MaxWriteKicksPerTick`: it is added only if a future trace shows the write-kick cost is
   > real. The default `MaxMeshBuildsPerTick = 2` is unchanged — retuning it is a separate decision.

*Settled, no longer open:* +2 Ticks of latency (accepted, §3.1); per-chunk output — **rejected**, one mesh
per layer — and ownership of `tile-pipeline-design.md` §4 (§3.6); the scheduling-cost probe as stage 0 (§8).

*FYI, not a decision:* the "graphs reached a worker" reading (§7.4) needs a running player, so on web it is
a manual check beside `Tools/build.sh web`'s `burst:` line. **Added 2026-09-02** to `docs/web-target.md`
under *Proving a build is what it claims* — stubbed there as an obligation, since §7.4 itself is not yet
implemented.

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
- `Assets/Code/MapRenderer.Jobs/MvtGeometryMaterializer.cs:116`, `Mvt/MvtNativeFeatureMatcher.cs:72` — rule-1 sites.
- `Assets/Code/MapRenderer.Unity/Rendering/Meshing/StyledFillTileBuilder.cs:423` `WriteGeometry` (pipeline
  call + managed stream loop `:470–503`); `StyledLineTileBuilder.cs:300–362` (per-ring `.Run()`s and managed
  `ProjectPoint` loops, `SubdivideCenterline` at `:473`).
- `Assets/Code/MapRenderer.Unity/Rendering/Tile/TileManager.cs` — `LoadedTile` (`:194`), `_pendingDisposal`
  (`:637`), `CaptureTelemetry`'s backlog poll (`:1080`), `AwaitInFlightMeshBuilds` (`:1548`), `PumpPending`
  (`:1600`; state-machine body `:1646–1700`), `KickMeshBuild` (`:1827`), `KickSourcelessBackground` (`:1969`),
  `ConsumeMeshBuild` (`:2026`), `ReleaseTile` (`:2217`), the pen stash inside `RenderTeardownRecord` (`:2577`),
  `DoDispose` (`:2761`).
- `Assets/Code/MapRenderer.Unity/Rendering/Tile/PreparedTileCache.cs:201–214` — `Put` destroys the previous
  entry on key collision — which is why D7 existed, and, since one mesh per layer makes that collision
  impossible, why D7 is now moot (§3.6 consequence (1)); the cache is untouched by this design.
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
