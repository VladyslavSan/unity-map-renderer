# GC & allocation — design and hard-won learnings

Why managed-heap allocation is a first-class performance concern in this renderer, what the stutter it
causes looks like in a profiler (rarely where you'd guess), where the allocation-prone paths are, and the
architecture that keeps them low.

This is the **narrative / "why"**. The two operational halves live elsewhere and are cross-referenced from
here:

- The **rule** — the *none → native → pooled* allocation ladder every hot path must climb — is
  [`conventions.md` → "Hot-path allocations"](conventions.md) (short form in `conventions-short.md`).
- The **measurement gotchas** — which GC meters lie on this Unity Mono runner and which one to trust —
  are [`lessons-learned.md` → "Test workflow"](lessons-learned.md).

---

## 1. The one fact that reframes everything: GC is stop-the-world

Unity's Boehm GC is **non-incremental and stop-the-world**. When a collection triggers, it **freezes every
thread** — the main thread, every job worker, every background build — for the whole collection, then
resumes them. A collection is triggered by *allocation*: crossing the heap's block threshold. So the cost
of an allocation is not the few nanoseconds to bump a pointer; it is the **entire pause** that the
allocation eventually forces, charged to whatever happens to be running when the threshold is crossed.

Two consequences follow, and both cost real debugging time before they are internalised:

### 1.1 GC inflates the timing of *everything*, not the code that allocated

Because the pause freezes all threads and the profiler attributes the frozen wall-clock time to **whatever
marker is on the stack at that instant**, a GC pause masquerades as a CPU cost *in an unrelated system*.
Under rapid-zoom cover churn, a GC-driven "CPU spike" jumps between `ApplyZoom`, `InstancedRebuild`, and
`Symbol.Collect` **frame to frame** — three different, innocent markers. None of them is slow. They are the
bystanders holding the stack when the collection lands. Kill the allocation and all three "spikes" vanish
together.

**The tell:** a heavy marker that (a) *changes identity* between otherwise-identical frames, and (b)
*always co-occurs with a GC-alloc spike on the same frame*, is not that marker — it is GC, and the marker
is a phantom. Do not optimise the marker. Find the allocation.

> This phantom-attribution is why a naïve read of the profiler sends you optimising the wrong function.
> The first move on any "spiky, wandering" cost is to look at the **GC Alloc** column, not the time column.

### 1.2 An off-thread allocation freezes the main thread too

"It allocates on a job/worker thread, so it can't hurt frame time" is **false**. Stop-the-world means
*all* threads, so a worker-thread allocation during an off-main tile build stalls the main thread mid-frame
as a main-thread allocation would. This is why the ladder (`conventions.md`) applies to **off-main build
code at least as hard as to per-frame code** — and why allocation on the *worker* threads (decode /
mesh-build / symbol-extract) matters even though a Main-Thread-only profiler view never shows it.

---

## 2. Where the allocation-prone paths are, and what keeps them off the GC heap

Cover churn fires per-feature / per-vertex / per-glyph work in several stages at once. Any managed allocation
there multiplies by the feature count of every newly covered tile, which crosses the block threshold within a
few frames and forces a global pause. Each stage keeps its unit of work off the GC heap by one rung of the
ladder:

| Stage | Per-unit-of-work hazard | What keeps it off the GC heap |
|---|---|---|
| **Decode** (`MvtDecoder`) | a property dictionary and a command array per feature; layer lists that grow-and-copy | properties decode into a **dense tag-pair store**, not a dictionary per feature; each feature's geometry and tag words are captured as a byte range and flattened into shared per-layer `NativeArray`s — one for geometry commands, one for tag words — so no per-feature `uint[]` exists; layer lists are **pre-sized from a counting pass** |
| **Mesh build** (fill / line) | per-feature attribution arrays; a per-polygon hole-sort array | line and fill attribution columns are `NativeArray`s (so is the symbol path's counting-sort scratch); ring-visit-order and sort-key arrays come from a thread-safe per-build pool; the hole-ring sort runs in a **reused `NativeArray<int>` through a struct comparer** (`FillMeshPipeline.HoleRingComparer`, taken by generic constraint, so no boxing) |
| **Expression eval** (`FunctionExpression.Evaluate`) | an argument buffer per call node, per feature | a per-thread free-list (`EvalArgBuffers`) hands out a scratch `Value[]` and reclaims it on return, clearing every slot so no evaluated `Value` — which may hold a `string` — is retained |
| **Off-main build scratch** | `TempJob` reclaimed mid-build | `Allocator.Persistent`, disposed when the build completes — see "Allocator lifetime trap" |

Steady-state camera motion over already-loaded cover therefore allocates next to nothing. The remaining GC
pressure is the **new-tile-build tail**: worker-thread allocation during fetch → decode → build of freshly
covered tiles, which a Main-Thread profiler view does not show ("An off-thread allocation freezes the
main thread too").

---

## 3. Attacking the new-tile-build tail

1. **Profile before chasing.** Capture a real spike frame, switch the Profiler thread dropdown from
   *Main Thread* to the *Worker/Job* thread, and sort by **GC Alloc**. A static allocation audit can rank a
   **phantom** (a "per-polygon closure" that the compiler had already cached to one delegate per call — see
   "Learnings that cost time"), so a ranking is a hypothesis, not a target.
2. **Known candidates, unverified order:** the decode object graph (`new MvtFeature`, a class, per feature),
   the globe symbol-extract per-path arrays (`SymbolFeatureExtractor`), and a few per-kick layer-snapshot
   arrays. Re-measure each against a captured frame before any code changes.

---

## 4. Learnings that cost time (bank these before you chase an allocation)

- **A closure that captures only method-scoped / loop-stable locals does NOT allocate per iteration.**
  Roslyn hoists it to one display-class instance at the capture's scope and caches the delegate in a
  synthesized field, so `Array.Sort(arr, (a,b) => f(local, a, b))` inside a `for`-loop allocates the
  delegate **once per method call**, not once per iteration. Only a capture of a *loop-local* allocates each
  pass. A per-polygon `Array.Sort` comparator of that shape reads like a per-polygon delegate allocation, yet
  injecting it leaves the allocation meter green. In that shape the per-polygon cost is a sibling managed
  array (`new int[holeCount]`), which is why the hole sort uses a reused `NativeArray<int>` (the table in
  "Where the allocation-prone paths are"). **Moral:** before treating a closure as a per-iteration cost, ask whether it captures a
  loop-local or a method-scoped local — and RED-verify the allocation claim (inject the form, watch the meter
  go red) rather than trusting the reading of the source.

- **A "working fix" is not a confirmed diagnosis.** Both the closure above and the timing-phantom ("GC inflates the timing of *everything*")
  are cases where an intervention *appeared* to help for the wrong reason. Confirm the mechanism (meter goes
  red on the specific seam; the spike moves when *this* allocation is removed), not just the outcome.

- **The allocation ladder is not a style preference — it is the fix.** *(1) allocate nothing (reuse / hoist
  / `FixedList*Bytes` / `stackalloc`), (2) unmanaged → `NativeArray` / native container off the GC heap, (3)
  managed-only → pool, never `new` per call.* For temporary index/scratch buffers the answer is almost
  always rung 2 (a reused `NativeArray`), **not** pooling a managed array — native memory is off the GC heap
  entirely, so it can never trigger the stop-the-world pause in the first place. Full rule + the pooling
  sub-cases (`ArrayPool`, `UnityEngine.Pool` main-thread-only, `[ThreadStatic]` free-list off-main) in
  [`conventions.md`](conventions.md).

---

## 5. Allocator lifetime trap: `TempJob` is *main-thread* frames

`Allocator.TempJob`'s "safe for ~4 frames" lifetime is measured in **main-thread frames**. An off-main tile
build routinely spans more than 4 main-thread frames, so `TempJob` scratch inside it trips Unity's
`deleting an allocation older than 4 frames` warning — and can be reclaimed **mid-build**. Off-main build
scratch must therefore be **`Allocator.Persistent`**, disposed deterministically when the build completes.
The "`Temp` / `TempJob` for method-local scratch" guidance applies to **main-thread** scratch only. (Banked
in `conventions.md`'s allocation rule and `lessons-learned.md`.)

---

## 6. Measuring GC in tests — the short version

Full detail (with the canary self-checks that exposed the dead meters) is in `lessons-learned.md`. The
one-line takeaways so a doc reader here does not repeat the discovery:

- **`GC.GetAllocatedBytesForCurrentThread()` returns a constant `0`** in this EditMode Mono runner — a
  known-80 KB allocation reads as `0`. Any "0 allocations" from it is a broken instrument, not a real zero.
  (It *does* work in the `Tools/core-tests` real-.NET project.)
- **`GC.GetTotalMemory()`** measures net heap delta, not allocation traffic — GC-timing-dependent, swings
  wildly, useless as an allocation meter.
- **Trust `UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory()`** — it samples the `GC.Alloc`
  profiler recorder, so it sees transient churn and is immune to GC timing. Caveats: it answers *allocates:
  yes/no*, not a byte count; **warm the exact measured delegate** first (a one-shot lambda's JIT can
  register a false positive); and a **single** call can be alloc-free while a **run of N** trips it, so
  measure over a loop. Always **canary** any GC meter against a known allocation before trusting a `0`.
