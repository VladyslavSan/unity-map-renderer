# GC & allocation — design and hard-won learnings

Why managed-heap allocation is a first-class performance concern in this renderer, what the stutter it
causes actually looks like in a profiler (rarely where you'd guess), the current allocation state, and the
architecture that keeps it low.

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
During rapid-zoom cover churn this repo showed a "CPU spike" that jumped between `ApplyZoom`,
`InstancedRebuild`, and `Symbol.Collect` **frame to frame** — three different, innocent markers. None of
them was slow. They were the bystanders holding the stack when the collection landed. Kill the allocation
and all three "spikes" vanish together.

**The tell:** a heavy marker that (a) *changes identity* between otherwise-identical frames, and (b)
*always co-occurs with a GC-alloc spike on the same frame*, is not that marker — it is GC, and the marker
is a phantom. Do not optimise the marker. Find the allocation.

> This phantom-attribution is why a naïve read of the profiler sends you optimising the wrong function.
> The first move on any "spiky, wandering" cost is to look at the **GC Alloc** column, not the time column.

### 1.2 An off-thread allocation freezes the main thread too

"It allocates on a job/worker thread, so it can't hurt frame time" is **false**. Stop-the-world means
*all* threads, so a worker-thread allocation during an off-main tile build stalls the main thread mid-frame
exactly as a main-thread allocation would. This is why the ladder (`conventions.md`) applies to **off-main
build code at least as hard as to per-frame code** — and why the biggest remaining spikes here are on the
*worker* threads (decode / mesh-build / symbol-extract), invisible on a Main-Thread-only profiler view.

---

## 2. Where the allocations were, in this renderer

The rapid-zoom stutter was **process-wide GC**, driven by per-feature / per-vertex / per-glyph managed
allocation in three off-thread stages fired during cover churn:

| Stage | What allocated, per unit of work |
|---|---|
| **Decode** (`MvtDecoder`) | a `Dictionary<string,Value>` **per feature**; `new MvtFeature` (class) + two `uint[]` per feature; per-layer `List<uint[]>` grown from empty |
| **Mesh build** (`FillMeshPipeline` / line pipeline) | per-feature managed attribution arrays; a per-polygon `new int[holeCount]` for the hole-ring sort; a capturing sort comparator |
| **Symbol extract** (`SymbolFeatureExtractor`) | per-path `double[]` / `double3[]` for subdivide / project / anchor placement |
| **Expression eval** (`FunctionExpression.Evaluate`) | a `new Value[]` argument buffer per call node, per feature, during style filter evaluation |

At peak these summed to **20–96 MB in a single cover-churn frame** — far past the block threshold, so a
collection was essentially guaranteed every few frames, each one a global pause.

---

## 3. Current state (as of the `perf/gc-elimination` pass)

**Steady-state zoom is essentially solved.** A camera moving over already-loaded cover now allocates
~**15 KB/frame**, down from a ~**20 MB/frame** average. There is no longer a steady-state GC pause; the
stutter that motivated the campaign is gone in the common case.

What that pass changed, at the level of *mechanism* (see git log on the branch for the commits):

- **Per-feature `Dictionary` eliminated** — MVT properties decode into a **dense** tag-pair store instead
  of one dictionary per feature.
- **Decode / mesh managed buffers → native** — line and fill attribution columns, symbol counting-sort
  scratch, and packed-varint decode all stage into `NativeArray` off the GC heap; the layer decode lists
  are **pre-sized from a counting pass** so they never grow-and-copy.
- **Per-build fill scratch pooled** — the ring-visit-order and sort-key arrays come from a thread-safe
  per-build pool, and the hole-ring sort now runs in a **reused `NativeArray<int>` through a struct
  comparer** (taken by generic constraint, so no boxing) instead of a per-polygon managed `int[]`.
- **Expression argument buffers reused** — a per-thread free-list hands `FunctionExpression.Evaluate` a
  scratch `Value[]` and reclaims it on return (clearing every slot so no evaluated `Value` — which may hold
  a `string` — is retained).
- **Off-main build scratch is `Persistent`, not `TempJob`** — see §5.

### What remains (the new-tile-build tail)

**New-tile-build frames still spike ~20–35 MB off-thread** during fetch → decode → build of freshly
covered tiles. These do **not** show on a Main-Thread profiler view (§1.2). They are smaller-win than the
steady-state fix that already landed, and they are **worker-thread** allocations, so the discipline for
attacking them is:

1. **Profile before chasing.** Capture a real ≥30 MB spike frame, switch the Profiler thread dropdown from
   *Main Thread* to the *Worker/Job* thread, and sort by **GC Alloc**. The static allocation audit produced
   at least one **phantom** ranking in this campaign (a "per-polygon closure" that the compiler had already
   cached to one delegate per call — see §4), so a ranking is a hypothesis, not a target.
2. Known candidates, unverified order: decode object graph (`new MvtFeature` + the two `uint[]` per
   feature), globe symbol-extract per-path arrays, and a few per-kick layer-snapshot arrays. Each should be
   re-measured against a captured frame before any code changes.

---

## 4. Learnings that cost time (bank these before you chase an allocation)

- **A closure that captures only method-scoped / loop-stable locals does NOT allocate per iteration.**
  Roslyn hoists it to one display-class instance at the capture's scope and caches the delegate in a
  synthesized field, so `Array.Sort(arr, (a,b) => f(local, a, b))` inside a `for`-loop allocates the
  delegate **once per method call**, not once per iteration. Only a capture of a *loop-local* allocates each
  pass. In this campaign a per-polygon `Array.Sort` comparator was diagnosed as a per-polygon delegate
  allocation and "fixed" — but the RED-verify meter stayed green, because the delegate was already cached.
  The real per-polygon cost was the sibling `new int[holeCount]` managed array. **Moral:** before treating a
  closure as a per-iteration cost, ask whether it captures a loop-local or a method-scoped local — and
  RED-verify the allocation claim (inject the form, watch the meter go red) rather than trusting the reading
  of the source.

- **A "working fix" is not a confirmed diagnosis.** Both the closure above and the timing-phantom (§1.1)
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
