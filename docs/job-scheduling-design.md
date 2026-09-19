# Job scheduling — a real job graph over the Burst work

**Status:** designed 2026-09-02, revised the same day after a dual-arm review. Stage 0's probe reached its
verdict (per-layer shape stands) and has no production code of its own to land. Stages 1–6 landed — see
each stage's own status note for what shipped and when (Stage 1: 2026-09-02; Stage 2: `a8e713c5`; Stage 3:
`f8e61a7e`; Stage 4: Group A `f5e13c19`, Group B `c1702ed7`; Stage 5: Group A, per its "What shipped" note;
Stage 6: per its own header). Stage 7's original framing is superseded — its own correction blocks say so
— but what it became (batching the filter VM's per-feature dispatch, later widened by mesh-path routing)
did land; see those corrections for the actual scope. Design SSOT
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
  release pen (`PendingDisposalQueue`, UMR-112) and the teardown drain already exist for `WorkHandle`s.
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

The graph/pipeline duplication (sizing, gather, aggregate, the derive pre-pass, the clip/select branch, two
dispatch switches with identical case lists) existed deliberately, as the control for a parity-bound
migration. It ended at stage 4 commit 2: `FillMeshPipeline.Schedule` and `TileMeshBuffers` were deleted once
the extrusion roof moved to the graph, together with the graph-vs-pipeline parity tests. The oracle
afterwards is the snapshot suites plus per-stream SHA-256 goldens captured from the synchronous pipeline
before deletion (`FillMeshGraphParityTests`, `FillMeshGraphGlobeParityTests`, `StyledFillExtrusionGraphWriteTests`,
`TileBuildGraphTests`) — a regression pin against the retired implementation, not a live differential.

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
| (2) released in flight | handle → `PendingDisposalQueue.StashPrologue`; per-Tick `DrainCompleted()` poll; dispose on completion | `TileBuildGraph` → `StashGraph`, the same queue's second pen; per-Tick poll on `Handle.IsCompleted`; then `Complete()` + `Dispose()`. |
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

**Status of the four rules below:** 1 and 2 are implemented (rule 2's precondition tooth follows). 3 is
implemented in stage 6 (`GraphDeterminismTests`, `Tests.EditMode/Jobs/GraphDeterminismTests.cs` — the
worker-0-vs-default self-comparison over the full fill and line column sets, plus the frozen fill-parity
goldens). Rule 4 is **not yet implemented** — nothing in `Assets/Code` declares `[NativeSetThreadIndex]` —
and whoever lands it also owes the web-side note stubbed in `web-target.md` under *Proving a build is what
it claims*.

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

**Landed 2026-09-02.** Added `MapRenderer.Jobs/FillMeshGraph` — the scheduled form of `FillMeshPipeline.Schedule`,
with no production caller yet: `SizingJob`, `FillGatherJob` (with the sort), `EarcutBatchJob` (wrapping
`EarcutJob` unchanged), `AggregateJob`, the scratch `Dispose(handle)` nodes, `ProjectionDispatch.Schedule`,
and the error flag. `RingAssemblyJob`'s deferred ring count became a `bool` selector rather than an optional
`NativeArray` field, because a `NativeArray` job field left at literal `default` fails Unity's schedule-time
container validation — an optional field is safe additive for a value type and fatal for a container.
Parity against `FillMeshPipeline.Schedule` was hashed vertex-for-vertex and index-for-index over every
fixture tile and fill layer, including hole-bearing polygons.

### Stage 2 — the substrate, with the background tile as first client

**Landed 2026-09-03 as `a8e713c5`** (gate 2554/2554). Added `TileBuildGraph`, a four-value `BuildStep`
(`None | Seam | Measure | Write`; `Seam` is renamed `Prologue` at stage 3), the graph pen,
`ScheduleBatchedJobs()` once per Tick, `FillStreamWriteJob`, and the measure→write step allocating one
exact-size `MeshDataArray` per non-empty layer. `KickSourcelessBackground` schedules the graph directly, as
the first production client — the globe-subdivide stage (Stage 4) was landed first so its `WriteGlobeSubdivided`
node already existed for the shipped `UseGlobe: 1` demo scene. Deferred deliberately: pooling
`TileBuildGraph`/`LayerBuild[]`, until the disposal protocol this stage adds has teeth. Recorded finding 2,
deferred to stage 3: the release-during-`Write` pen path (as opposed to `Measure`) had no observing tooth.

> **Superseded 2026-09-05.** This stage's throughput note (charging both the measure and write kicks against
> `MaxMeshBuildsPerTick` halves new-tile start rate) is moot — §11 fork 2 deleted that charging rule.

### Stage 3 — source tiles: prologue → measure → write, and the cache ripple

**Landed 2026-09-03 as `f8e61a7e`** (gate 2566/2566). `KickMeshBuild` splits into the prologue (selection,
paint bake, sort-key order, the symbol worker pass — on the seam, returning native per-layer columns) and
the pump-scheduled fill graph; line and fill-extrusion layers keep synchronous `WriteInto` inside the
prologue for this stage. `PreparedTileCache` is untouched. `BuildStep.Seam` is renamed `Prologue`; the
hand-off type is `TilePrologueOutput`, replacing `MeshBuildResult`. The background tile's dead seam path
(`TileLayerProcessorRunner.RunSourcelessWorkerPass`, `TileBackgroundLayerProcessor`) was retired here, with
`TileBackgroundQuadProjectionTests` repointed at `BuildLayerInput` + `TileBuildGraph`.

> **Superseded 2026-09-05.** The stage's charging model (a source tile costs 2 units — prologue kick + write
> kick, measure step uncharged) was deleted by §11 fork 2's ruling.

#### `TileManager.LoadedTile`'s lifecycle (moved from its struct doc, UMR-118)

The per-tile live record's lifecycle (S47/S51, extended by stage 3 above):

1. Fetch (`Request` → `UniTask<SharedDisposable<IDecodedTile>>` in-flight, stored as `.Preserve()`).
2. Mesh build kicked — `Step` becomes `BuildStep.Prologue` (`MeshBuildTask` in-flight, source tiles only)
   then `BuildStep.Measure` then `BuildStep.Write` (`Graph` in-flight, every tile — a background tile
   starts directly at Measure, no Prologue); `FetchCompleted = true` either way.
3. Mesh build consumed (`Built = true`; `Step = BuildStep.None`).

Mid-flight release protection: `ReleaseTile` removes the tile from `_loaded` immediately, so `PumpPending`
and `DrainMeshBuilds` — which iterate `_loaded` — never visit released tiles. A tile released while its
mesh build is in-flight will never have `ConsumeMeshBuild` called for it.

#### `TileManager.KickSourcelessBackground`'s E1 history (moved from its method doc, UMR-118)

No projection gate: E1 was resolved by reordering (the globe subdivide stage landed first), so the graph
builds both arms and `KickSourcelessBackground` kicks on every projection. It allocates NO
`Mesh.MeshDataArray` itself — that is the write step's job (`TileBuildGraph.CompleteMeasureAndScheduleWrite`),
which is where `PmMeshDataAllocate` now brackets the allocation.

### Stage 4 — globe subdivide and fill-extrusion roofs as graph nodes

**Landed 2026-09-04.** Group A (`f5e13c19`, gate 2571/2571): the extrusion roof measures through
`FillMeshGraph.Schedule` unchanged; the write becomes a kind-specific job writing the roof at `[0, Vr)` then
the walls at `[Vr, Vr+Vw)` (wall indices rebased `+Vr`) into ONE exact-sized `MeshData` — the walls travel as
request-owned native columns rather than a second payload, since a layer emits exactly one mesh —
discriminated by `TileBuildGraph.LayerRequestKind`. The walls themselves stayed a prologue-built managed
loop at this point, superseded at stage 5. Group B (`c1702ed7`, gate 2569/2569) retired the synchronous fill
pipeline entirely (`FillMeshPipeline.Schedule`, `TileMeshBuffers`, `WriteGlobeSubdivided`/`WriteGlobeRoof`):
`FillMeshGraph.Schedule` is the only mesher, with regression pins as frozen per-stream goldens
(`FillMeshGraphParityTests`, `FillMeshGraphGlobeParityTests`, `StyledFillExtrusionGraphWriteTests`,
`TileBuildGraphTests`).

On a globe, `GlobeFillSubdivideJob` was found (verified 2026-09-02) to project every vertex internally and
discard `WorldPositions`/`VertexUp` — O(V) dead work per layer per tile. It was kept until the write step
existed to assert parity on the subdivided output alone, then removed in Group B.

### Stage 5 — the line graph

Every stage before this one could claim byte-identical output because fill already went through
`ProjectionDispatch.Run`, the same Burst job the graph uses — managed-versus-Burst projection was never on
the table. Line and the extrusion walls project through the managed `IProjection.ProjectPoint`, so this
stage measured that boundary before writing any production code (`ProjectionManagedVersusBurstTests`,
against the real `boundary_3` fixtures, both compiled `[BurstCompile]` with `OptimizeFor.Performance` and no
`FloatMode` — this repo has never set one). **Resolved for the walls, in what other citing files call "the
wall-job-graph stage":** their three managed computations (`TileToGeoJob.GeoAt`, `ProjectPoint`,
`MetresToWorldFactor`) all moved into Burst nodes of `FillExtrusionMeshGraph.Schedule`, including a new
`[BurstCompile]` `WallQuadJob`, with every wall golden (`WallQuadJobParityTests`/`StyledFillExtrusionGraphWriteTests`)
holding within the bound this measurement established.

**Outcome (measured 2026-09-04): the boundary is TOLERANCE-BOUNDED, not exact, and the bound is per field,
not a flat epsilon.** A quantity linear in the inputs is bit-exact between managed and Burst; a quantity
downstream of a transcendental (`atan`, `sinh`, `sin`, `cos`) diverges by a few ULP, because Burst's
relaxed-math reassociation under `OptimizeFor.Performance` touches only the transcendental call sites. The
divergence magnitude is not fixed: a zero-origin probe measured 4 ULP on `World` / 2–3 ULP on `Up`; the same
two jobs in production (`ProjectPointsJob.cs:57`, `WorldPositions[index] = pp.World - OriginWorld`, a
tile-local RTC origin) measured **512 ULP** on `World`, with `Up` unchanged at 2–3. The discriminator is
whether a large magnitude is subtracted from the quantity before it is compared: `World`'s origin
subtraction is a catastrophic cancellation that shrinks the result ~640× while the absolute error stays
fixed, so the same error reads as ~128× more ULP; `Up` has no such subtraction and does not move. No ULP
figure from one call site may be reused as a bound at a different one without re-measuring there.

Downstream of `WorldPositions`, the float32 narrowing (`WallQuadJob.cs`, `(float3)World[idxA]`) erases the
divergence structurally: 512 ULP of a double at tile-local magnitude (~1e4) is ≈9.3e-10 absolute, versus a
float32 ULP there of ≈9.8e-4 — six orders of margin (9.8e-4 / 9.3e-10 ≈ 1.05e6). That is why `Position`'s
bit-exact assertion (`StyledFillExtrusionGraphWriteTests.AssertStreamComponent`, `maxUlp: 0`) stays a
structural guarantee rather than a softened bound.

The root cause was confirmed against the real production code path, not inferred: Burst's relaxed-math
`normalize` does not guarantee the same rounding as managed IEEE `sqrt`+divide for every input — 2 of the
12 real wall edges diverged (at `normalize(upA + upB)` and, separately, `normalize(posB - posA)`), the rest
agreed completely, and `math.cross` stayed exact everywhere checked. A third `normalize` call site
(`upA = normalize(Up[idx])`) was independently found to diverge by 1 ULP on a different, high-latitude
fixture. This is the signature of genuine Burst codegen taking a different path on some inputs, not a
compile failure (`CompileSynchronously = true` would have surfaced one, and none appeared).

The shipped bound (`WallNormalMaxUlp*`/`WallTangentMaxUlp*`) is the measured 1–2 ULP plus a stated `+2`
platform margin, with enormous clearance: an injected wall-tail transcription defect (edge-partner swap)
produced 614458 ULP, five orders of magnitude above the measured bound. **The invariant this measurement
established (the ruling other citing files name "E5"): linear operations are bit-exact between managed and
Burst; a quantity downstream of a transcendental is tolerance-bounded, at a magnitude that scales with
whether the quantity is origin-subtracted before comparison.** It is not a flat number carried between call
sites — never widen a bound past its measured figure — and it extends to every later nativization crossing
the same boundary (line included), each of which measures its own bound at its own call site rather than
inheriting this one.

> **Correction to a current-state claim — the extrusion walls now honour the tile-buffer clip (UMR-93,
> 2026-09-07).** The wall-job stage shipped the wall chain on `RingSelectJob` **unconditionally**, so the roof
> was cut at the tile-buffer window while the walls were not; with the shipped `FillTileBufferClip: 0` config
> (which `TileBufferClip.FromInspectorUnits` decodes to the ENABLED `KeepTileUnits(0.0)` — only a negative
> value disables) two neighbouring tiles each raised the whole wall set of every building crossing the seam.
> That was a real defect, recorded at the time as this stage's behaviour-preserving boundary; it is now fixed.
> `FillExtrusionMeshGraph.Schedule`'s wall chain takes the same select-or-clip branch on `input.Clip` the roof
> takes, with one new node — `ProjectionColumnSizingJob` — because the clipped gather has no schedule-time
> output length. **This does not rewrite what the wall-job stage did**: the stage genuinely deferred the fix,
> and the goldens it captured are unmoved (the wall goldens run `clip: default`; the graphwrite goldens enable
> the clip but their fixture lies wholly inside the window, where `RingClipJob`'s bbox fast path copies
> verbatim).
>
> **Decision (a): a wall is emitted for EVERY edge of the clipped ring, cut edges included** — what is
> extruded is the clipped polygon, and no per-edge "this edge is a cut" provenance is tracked anywhere. The
> cut walls are invisible under the shipped opaque `fill-extrusion` regime (`ZWrite On`, `One/Zero` —
> `depth-and-render-regimes-design.md` §6.E defers translucency): each faces into the other half of the
> building and is back-face-culled or depth-rejected. They are *not* invisible where it matters — at the edge
> of the loaded cover, and while a neighbour is still loading, (a) shows a closed truncated building where
> suppressing the cut edges would show a hollow shell.
>
> **The one condition that reopens the alternative:** translucent fill-extrusion
> (`depth-and-render-regimes-design.md` §6.E). Under alpha blending the hidden cut walls become a visible
> seam band — the same defect class `TileBufferClip` exists to remove for flat fill. Suppressing them then
> requires a new per-output-edge column out of `RingClipJob`, a contract change across all its call sites; do
> not implement it speculatively before §6.E lands.

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

Four tuned batch constants (fill/project/line/extrusion tile→geo — `VertexBatch = 1024`, since
`TileToGeoJob`/`ProjectPointsJob` cost ~10-50 µs per 1024 vertices, well above a batch hand-off's own cost);
`EarcutPolygonBatch = 1`; `RibbonRingBatch = 1` (earcut and ribbon items vary by orders of magnitude, so
batch 1 lets the job system's work-stealing balance the load). The stream-write jobs were measured (§9,
Group D) and not parallelised — the measurement did not clear its own pre-committed gate. Bit-exactness
rests on the slice derivation, not an assertion — see §7 rule 2 for the mechanism.

The sub-labels below are cited by name from production/test source; kept findable here rather than only in
those files.

**Group A — the sanctioned attribute shape.** A.1 (probed, not shipped): split the columns into individual
deferred `NativeArray` fields on the job, read/write separated. A.2 (shipped, on both `EarcutBatchJob` and
`RibbonBatchJob`): one `Buffers` field holding all columns, `[NativeDisableParallelForRestriction]` on that
one outer field — legal even though the nested `NativeList<T>` lacks index-restriction support; the smaller
diff. A0.3: the finding that the ribbon needed A.2's identical shape, widening the sanctioned set to
`{ EarcutBatchJob, RibbonBatchJob }`.

**Group B — the determinism teeth.** B.3: `GraphDeterminismTests`' post-subdivision vertex-count sizing
detail for line's contention guard. B.5: the determinism tooth itself — worker-0-vs-default self-comparison
over the full column set, plus the frozen fill-parity goldens at their real reach. B.6:
`ParallelNodes_OfferMoreThanOneBatch_OverTheRealCorpus`, one arm per batch constant, RED-verified by
restoring that constant to `1 << 20`.

**Group C — fill/earcut parallel.** C.1: the capacity-overrun `Error` flag, relocated from `EarcutBatchJob`
to `AggregateJob` (a `NativeReference<int>` can't be parallel-written without a disable attribute it doesn't
warrant). C.2: `SizingJob`'s offset-table monotonicity assertion — defence-in-depth, not the earcut's
bit-exactness precondition (that is structural, §7 rule 2). C.3: converting `EarcutBatchJob` to
`IJobParallelForDefer` over polygons. **C.4 — the error-path bonus**, a real defect outside this stage's own
scope, the third recurrence of the one §7 rule 2 describes: `AggregateJob`/`RibbonAggregateJob` used to bound
their loops by a borrowed count instead of their own sizing job's resized column, so a `SizingJob` capacity
early-return left the parallel earcut/ribbon node safely at zero batches while the serial aggregate one node
downstream indexed past a length-0 `NativeList` — undetected in a release build, where that bounds check is
compiled out. Both aggregates now bound their own loop by the column their own sizing job resizes. C.5: the
attribute-fence structure test, extended to a (file, token, count) triple rather than a bare filename.

**Group E — line/ribbon parallel, mirrors Group C.** E.2: `RibbonSizingJob` + `RibbonAggregateJob` +
`RibbonBatchJob`'s defer-over-`PerRingVertexCount` choice. E.3: the `MaxOutputVertices` overflow rule, moved
into `RibbonAggregateJob` unchanged. E.4: `RibbonBatchJob`'s own A.2-equivalent attribute occurrence.
`GraphDeterminismTests` (§7 rule 3) was extended with a line-graph arm, and the attribute-fence and
consumer-set structure tests (§7 rule 2, sanctioned set `A0.3/C.5/A.2/E.4`) were extended to cover it.

### Stage 7 — prologue nativization (first step) and seam retirement from the tile path

**What actually shipped: batch the filter VM's per-feature dispatch — not a scheduling change, and not
prologue emptying.** Selection still runs inside the `IWorkScheduler` body (`.Run()`, per §4 rule 1), so
this only eliminates web's per-feature dispatch overhead (thousands of dispatches each copying a ~764 B job
struct by value on the main thread). The native filter VM compiles single-arm `match` only, but the shipped
liberty style's 105 filtered layers all compile; mesh-path routing (`TileMeshLayerProcessor.cs:85,91`) was
added so both the mesh-build and symbol cadences probe it, and on the largest committed fixture 77 of 77
resolvable fill/line layers bound natively (§9 row 11).

Paint bake did not go native and the prologue is not emptied: there is no native expression VM
(`Core/Expressions`, 3,065 lines, all managed), and it is a separate project after this epic.
`IWorkScheduler` therefore does not retire. §11 fork 1 (line graph before or after this stage) was closed by
action: the line graph shipped first.

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
| 11 | **[2026-09-05, stage 7, mesh-path routing]** HEAD (managed `CompiledFilter`, list overload) vs the routed overload (native VM), over liberty's real 80-layer fill+line corpus (77 of them resolve against the fixture) driven against `boundary-9-274-168.pbf.bytes` — the largest committed real-data tile (landcover 1471, place 289, mountain_peak 86, water 76, transportation 74, park 72, landuse 21, waterway 4, boundary 2; **11,941 feature-filter evaluations**). Burst warmed with a discarded pass first; three runs/arm, median, four separate process launches: **3 of 4 agree — head ≈4.1-4.3 ms, routed ≈5.2 ms (routed ~1.0 ms / ~24% slower, ~84 ns/evaluation)**; the 4th launch read head≈6.46 ms / routed≈6.40 ms (near parity), treated as a cold-launch outlier (first batch launch after reimport) and excluded from the reading. The routed arm pays a per-feature `NativeFilterEvaluationJob` struct copy, a `.Run()` dispatch, and five `AtomicSafetyHandle` checks that the managed arm does not — consistent with the measured direction, at roughly double the naive per-evaluation estimate, which fits Editor safety-check overhead (the Editor profiler overstates it, so this is an upper bound on the release cost, not the release number). **Bind count (F5): 77 of 77 resolvable layers bound a native matcher on this fixture — none fell back to managed on this data.** Nothing refuses on this number — nativization here is the accepted direction, not a measurement-gated decision; this reading sizes what F1's `RunByRef` batching has left to win. | nothing (recorded only) — sizes stage 8/F1's remaining prize | closed |
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

  > **DECIDED (§11 fork 2, ruled 2026-09-05).** The prior two-units-per-tile charging rule was deleted: it
  > charged only two of three unequal main-thread costs per tile (prologue kick, measure schedule ~13 nodes
  > × layers, write kick — exact-size `MeshData` alloc + N jobs) and left the middle one, measure, free, so
  > "2 units per tile" measured nothing physical. The write-kick charge traced back to §4.1's blind-allocation
  > stall (#5), which exact sizing has since closed. No `MaxWriteKicksPerTick` knob replaces it; one is added
  > only if a future trace shows the write-kick cost is real.

## 11. Open questions escalated to the maintainer

Both escalated forks are resolved. Fork 1 (line graph before or after prologue nativization) was closed by
action 2026-09-04: the line graph shipped, so stage 5 ran before stage 7. Fork 2 (the two-units-per-tile
build-budget charge) was closed by ruling 2026-09-05: the rule is deleted — budget is tiles admitted per
Tick (`TileBuildsStartedLastTick`), every step transition flows uncharged, and no `MaxWriteKicksPerTick`
knob replaces it unless a future trace shows the write-kick cost is real.

The `TileBuildGraph` worker-index reading (§7 rule 4) needs a running player to observe on web; the
obligation is stubbed in `docs/web-target.md` under *Proving a build is what it claims* until §7 rule 4 is
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
- `Assets/Code/MapRenderer.Unity/Rendering/Tile/TileManager.cs` — `LoadedTile` (`:104`),
  `CaptureTelemetry`'s backlog poll (`:577`), `AwaitInFlightMeshBuilds` (`:945`), `PumpPending` (`:979`),
  `KickMeshBuild` (`:1199`), `KickSourcelessBackground` (`:1279`), `ConsumeMeshBuild` (`:1318`),
  `ReleaseTile` (`:1458`), the pen stash inside `RenderTeardownRecord` (`:1648`), `DoDispose` (`:1744`).
  **UMR-112:** the three release-time pens (formerly `_pendingDisposal` + two siblings) moved to
  `Assets/Code/MapRenderer.Unity/Rendering/Tile/PendingDisposalQueue.cs`; `RenderTeardownRecord`'s stash
  calls and `DoDispose`'s `_pending.FlushAll()` are what remain on `TileManager.cs` at the lines above.
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
