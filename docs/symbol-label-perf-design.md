# Symbol label per-frame cost — design

Companion to [`labels-and-symbols-design.md`](labels-and-symbols-design.md) ("Pipeline flow", and its
"Tile-coverage pre-cull") and to [`labels-async-reconcile-design.md`](labels-async-reconcile-design.md),
which owns the dedup/reconcile half of this design in full. This document is the umbrella for the rest:
what the symbol pipeline bakes once versus recomputes every frame, and why.

---

## 1. The governing principle

A symbol label's per-frame cost splits into two kinds of work, and the design keeps them apart:

- **Camera-independent work** — glyph quads, colours, fade ids, world anchors, tile origins, and which
  label wins a cross-tile dedup — is a function of the loaded tile set, not of camera pose. It does not
  change on pan, rotate, or tilt, and it changes only when a tile is added, removed, or rebuilt.
- **Camera-dependent work** — anchor projection, screen-space staging (including the curved arc-walk),
  and collision — must run when the camera moves.

The architecture computes the first kind once, when the tile set changes, and re-runs only the second
kind every frame. Four mechanisms keep camera-independent work off the per-frame path: the per-tile bake
("The tile-build-time bake"), the event-driven cross-tile dedup
([`labels-async-reconcile-design.md`](labels-async-reconcile-design.md)), the memoized Burst gather into
the render mirror ("The gather is memoized on the winner-set version", "The gather runs as a synchronous
Burst job"), and the one-frame-late collision verdict ("The collision verdict applies one frame late").

## 2. Constraints (non-negotiable)

1. **No result-caching / skip-when-nothing-changed.** A motion-keyed static-frame skip (B-1) is rejected:
   it is smooth on a still camera and janky on a moving one. The per-frame pass must stay consistent under
   motion — cheap is fine, a motion-keyed 0-or-full cost cliff is not.
2. **Off-main-as-a-hide is not a fix.** Work must be removed from the critical path (done once, at the
   point the tile set changes), not shunted to a worker to hide the same cost every frame.
3. **Behaviour-preserving.** Render output stays byte-identical, except where a section below states
   otherwise (the one-frame-late collision verdict); the A-3 finest-zoom-wins dedup, stable cross-zoom
   identity, and fade behaviour stay intact.

## 3. The tile-build-time bake

Each tile's blittable symbol representation is baked once, on the existing bytes-ready build hook
(already off the main thread), and held on the tile's `SymbolTileStore` entry with the same lifecycle as
its symbols: built on bytes-ready, kept warm in the prepared cache, dropped on eviction. This removes the
per-label glyph-quad copy, colour conversion, and fade-id hashing from the per-frame path entirely — it
happens once per tile, not once per tile per frame.

## 4. Curved labels stay camera-dependent

A curved (along-line) label cannot be baked the same way a point label is: its layout depends on the
arc-walk over the *projected* line, which changes with the camera. Any mechanism that treats the label
batch as static state — the memoized gather, a persistent per-tile draw — carves curved labels
out, or restricts itself to the parts of a curved label's data that really are camera-independent (its
glyph identities and tile-space geometry, as opposed to its screen-space layout).

## 5. Dedup duplication is static per tile-set, not per camera pose

The cross-tile dedup (`SymbolTileStore.CollectInto`, A-3) exists because two tiles can carry the same
label — edge/buffer duplication between neighbouring tiles and cross-source duplication. A coarse parent
tile and a fine child tile overlapping in view does not happen while the cover is a quadtree cut, so the
parent/child finest-zoom-wins tiebreak this dedup performs has nothing to arbitrate. The duplication that
does occur is **static per tile-set**: it does not depend on camera pose or on fractional zoom, only on
which tiles are loaded. So the deduped winner set is a pure function of the loaded tile set, and
recomputing it every frame recomputes the same answer for nothing.

The dedup and its cross-tile identity/reconcile design in full — including the tile-event-driven state
machine that keeps the dedup off the per-frame path — live in
[`labels-async-reconcile-design.md`](labels-async-reconcile-design.md). That design follows the
`off-main-thread-principle`: the dedup is scheduled off-main on a tile-set change and picked up on a
later frame, so the per-frame path processes an already-deduped, cached set. Two orthogonal wins compose
with it: interning label text to an integer key (removing the per-frame string hash), and reusing
buffers (moving work off-main does not exempt it from Unity's stop-the-world GC).

## 6. Ruled out: an incrementally-patched winner index

A per-tile-event incremental index in place of the dictionary dedup costs more than the dedup: it
cold-reseeds its entire index — rebuilding a per-record contender, winner, and tile-contribution object
for every label — on every zoom-quantize change, and a moving camera changes the zoom-quantize
constantly. Do not re-attempt an incremental index keyed on zoom-quantize without first checking how
often that key changes under motion.

## 10. The residual: what stays expensive after the reconcile

The async reconcile (`labels-async-reconcile-design.md`) removes the per-frame dedup. What remains is a
smaller set of per-frame costs downstream of it, inside `SymbolPlacementSystem`'s per-frame tick:
compacting the deduped winner set into the native render mirror (the *gather*), projecting and staging
labels on screen, running collision, and emitting quads. Each subsection below is a self-contained
mechanism; the collision-setup lever is the only one still open.

**Cost estimates for these loops are hypotheses, not answers.** Weighting a raw item count by a plausible
per-item cost does not predict the cost of this pipeline's loops. Split a combined profiler marker into its
per-shape sub-costs before ranking which one to attack, and measure a change directly rather than model it.

### 10.1 The gather step exists to compact winners into a render-ready mirror

`SymbolPlacementSystem.GatherIntoMirror` (run as `SymbolGatherJob`, see "The gather runs as a synchronous
Burst job") compacts each reconcile
winner's pre-baked `SymbolTileBlock` slice into a single native mirror — the render-ready buffer that
downstream projection, staging, collision, and emit all read. This step exists because the winner set
lives as per-tile blocks scattered across `SymbolTileStore`, while the rest of the per-frame pipeline
needs one contiguous, indexable buffer.

### 10.2 The gather is memoized on the winner-set version

Everything the gather produces — quads, glyphs, anchors, fade ids, world points, and every per-block
`*Start`/`*Count` remap — is a pure function of the winner *set*: `plan.BlockId`, `LocalIndex`,
`Departing`, and `Blocks` all come from `_frontResult`, the reconcile output, which changes only on a
front swap (a tile event). The only genuinely per-frame inputs are three per-record byte masks
(`CoverageFading` / `Dropped`, from `SymbolTileCoverageFilter.ClassifyActive`) plus `Departing`, which is
set-derived but re-written every frame because doing so is free. So the heavy per-pool rebuild runs only
when the winner-set version changes; every other frame writes just the three byte masks (a few memcpys
and a subtraction) instead of walking every record.

**The memo key is `SymbolSubsystem._frontSetVersion`**, a monotonic `int` bumped on every change to
`_frontResult`'s content — the reconcile swap (`PickupCompletedReconcile`) and `SetStyle`/`Dispose`
clears — and stamped onto `SymbolGatherPlan.WinnerSetVersion` at build time. `_store.CollectGeneration`
was considered and rejected as the key: it moves at the tile *event*, while the front only moves later,
at the swap, so a `CollectGeneration`-keyed memo would keep hitting straight through the swap window and
serve a stale mirror. The version must be bumped at the swap, not at the event.

**Cross-overload invalidation is bidirectional.** One `_mirrorSource`(object)/`_mirrorVersion`(long) pair
covers both the demo `Tick(batch)` path and the production plan path, so a demo tick between two
production gathers invalidates the production memo and vice versa — one `ReferenceEquals`-and-version
comparison rather than two parallel sentinels.

**A release-build backstop.** The memo predicate also checks `plan.WinnerCount == _mirrorCount`, not just
source and version — if a front-mutation site is ever added without a version bump, this falls
through to a full rebuild instead of a length-mismatched `NativeArray.Copy` (a checked throw in the
Editor, a silent out-of-bounds read in a release player). A debug-only assert fires whenever this
backstop engages.

**Why this is not the rejected motion-keyed skip ("Constraints"):** the memo key is a tile event (the front swap),
not camera stillness. Cost is O(set delta), always applied, identical whether the camera is still or
moving — there is no camera-state predicate and no "skip when nothing changed" branch.

GPU snapshots do not exercise this memo — every snapshot fixture drives the demo `Tick(batch)` overload,
never the production plan path. Whether the memo actually hits is not headless-observable; it is a
Play-mode profiling question ("The memo is structurally dead under continuous motion").

**Why a retained mirror cannot be corrupted between frames.** The memo key above answers "is the mirror
stale?"; it does not by itself answer "can a second writer corrupt it while it is retained?" — that holds
because of the write ownership `SymbolPlacementSystem.cs` enforces: the `_mirror*` pools are written only
inside `GatherIntoMirror`; every downstream stage (`RunStageJob`'s `StageJob`, and the collision
schedule/harvest pair, `ScheduleCollision`/`HarvestCollision`) reads them but writes its own results into
separate `_stage*` pools (`_stageBoxes`, `_stageQuads`, `_stageCandidates`, `_stageEmit`) — the in-place
candidate sort collision performs touches `_stageCandidates`, never the mirror. `plan.Blocks`
(`SymbolTileStore.OrderedBlocks`) stays pinned for as long as a snapshot is in service
(`SymbolTileStore.ReleasePins`), so nothing between two gathers can invalidate a retained mirror's source
blocks short of a front swap.

### 10.3 An unadopted, low-cost lever in the collision setup

The main-thread grid clear ahead of collision is a managed loop over every grid cell with per-element
bounds checks, and `NodeUpperBoundByCandidates` walks every candidate's box references on the main
thread. Both are small, low-risk, and architecture-preserving — no design blocks folding the clear into
a job or a memset. Both are open.

### 10.4 The memo is structurally dead under continuous motion

Every tile-lifecycle path dirties the store — `BeginBuild`, `CompleteBuild`, `Release`, the cached-FIFO
evict, `Restore`, and `PurgeExpiredDeparting` (the last runs every frame, dirtying whenever any departing
tile's grace period expires). While panning or zooming, these fire continuously and overlap, so
`CollectGeneration` moves on nearly every frame, a reconcile stays in flight continuously, a front swap
arrives on nearly every frame, and `_frontSetVersion` moves on nearly every frame too. **The memo gets no
quiet frame to hit on during continuous motion** — a rebuild-rate telemetry counter
(`MirrorRebuildCount`) confirms the rebuild rate stays near the frame rate while panning. The gap between
a still camera (where the memo hits and the gather cost collapses to the three mask writes) and a moving
one (where it does not) is the true per-frame cost of this step under motion.

**The deeper, motion-independent point:** the mirror rebuild is all-or-nothing. A one-tile delta in a
several-hundred-tile visible set costs a full rebuild of every winner's baked slice. Memoization only
ever answers "did anything change?", and under continuous motion the answer is always yes — so a lever
that only skips unchanged work cannot close this gap; the rebuild itself has to get cheap, or leave the
critical path, regardless of how often it runs.

### 10.5 Rejected for now: an async, double-buffered gather

Mirroring the async reconcile one layer down, the mirror itself could be an explicit state produced
off-main: on a front swap, schedule a Burst job that builds the next mirror into a back buffer from the
new winner set, and swap it in — a pointer swap — when the job completes a frame or two later. Per frame
the main thread would then pay only the mask writes and, occasionally, the swap; the rebuild would leave
the critical path entirely rather than being skipped when nothing changed, which is what would make it
work under continuous motion.

Building this raises two open design questions:

1. **Burst cannot hold a managed array of `SymbolTileBlock`s** (each owning several `NativeArray`s). An
   async gather needs either a flattened pointer table (a `NativeArray` of block descriptors carrying
   `[NativeDisableUnsafePtrRestriction]` pointers and lengths) or a shared bake-time mega-buffer that
   turns the gather into pure index arithmetic. The synchronous gather needs neither, so it uses neither;
   this choice is the crux of building the async version.
2. **The per-frame masks must classify the displayed set, not the newest reconcile front.** With an async
   gather the live mirror corresponds to an older winner set than the current reconcile front, so
   `SymbolTileCoverageFilter.ClassifyActive` has to classify the snapshot actually on screen, or the
   masks index a set the mirror was not built from.

The synchronous Burst gather already removes this step from the still-vs-moving comparison, so the async
version's remaining upside is small. Its cost is not: a third staleness generation stacked on top of the
reconcile's (1–4 frames) and the mirror's own (1–2 frames), double-buffered view and count tables, pin
accounting across an async boundary with no safety-handle protection on the view table's raw pointers,
and the displayed-set classification above. The lifetime model it would need — pinning the snapshot an
in-flight gather reads independently of the front pin, releasing when its mirror leaves service — rides
the same pin/`ReleasePins` refcount contract the reconcile already uses. Nothing forecloses building it
later; the trade reverses only if a profile shows the synchronous gather itself dominating a frame.

### 10.6 The gather runs as a synchronous Burst job

`SymbolGatherJob` (`Assets/Code/MapRenderer.Jobs/Symbols/SymbolGatherJob.cs`) is the compaction: one
`[BurstCompile(CompileSynchronously = true)] IJob`, run synchronously (`.Run()`) inside the frame's gather
step, strictly before the rest of the tick. It is one job, not two passes: pass 1 totals the per-pool
sizes, pass 2 resizes the output `NativeList<T>`s once and fills them.

**Storage: a per-frame view table over unchanged block storage.** `SymbolTileBlock` keeps its
`NativeArray<T>` fields unchanged. Immediately before the job runs, `BuildBlockViews`
(`SymbolPlacementSystem.cs`) builds a reusable `NativeList<BlockView>` — one entry per block the plan
references — where `BlockView` (`Assets/Code/MapRenderer.Jobs/Symbols/BlockView.cs`) holds non-owning
`UnsafeList<T>` views over each field, built from the block's existing native arrays. Converting block
storage itself to `UnsafeList<T>`, or building a bake-time mega-buffer with a generation counter, were
both considered and rejected: either would touch on the order of a hundred call sites outside the gather
(the baker, disposal guards, leak-baseline fixtures, baker tests) for no benefit the view table doesn't
already give, and converting to `UnsafeList<T>` would additionally delete the Editor
`AtomicSafetyHandle` check every block read carries today — for every reader, not only the gather — since
`UnsafeList<T>` carries no safety handle. Neither is foreclosed for later; the view table is the smaller
step and is sufficient for a synchronous gather.

`BuildBlockViews` itself still goes through `NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr`, which
performs an `AtomicSafetyHandle` read check — a disposed block is still caught, in the Editor, at the
moment the view is taken, on the main thread, with a real stack. Because the job runs inline via `.Run()`
on the calling thread, no view can outlive the block it points into; an async version would need
its own lifetime protection, since a scheduled job's view table can outlive the frame that built it.

`_gatherBlockViews` and `_gatherCounts` are single, reused containers, valid only because the gather runs
inline: an async gather scheduled instead of run would need frame N+1's `BuildBlockViews` to not rewrite
a table an in-flight job is still reading, which means double-buffering both containers.

Growing an externally-owned `NativeList<T>` from inside a Burst job has in-repo precedent —
`GlobeFillSubdivideJob<TProj>.Execute` calls `Add` on a `NativeList<GlobeFillVertex>` allocated by its
caller (`FillMeshGraph`) — and `SymbolGatherJob`'s growth is single-shot and bounded (computed in its own
pass 1), not per-element, so it does not need `StageJob`'s fixed-length-output approach.

**Byte-identical**, confirmed by a differential oracle (`SymbolGatherParityTests`) that independently
reimplements the gather's compaction/remap spine — winner order, the `(blockId, localIndex)` mapping, the
running-offset arithmetic — against the production job. That independence has one limit: the oracle and
the production baker share the same per-label field-building helpers, so a bug inside those helpers would
produce matching wrong values on both sides; the test covers the compaction spine, not those helpers.

**An Editor profile overstates this job's advantage in a player build.** Much of the job's advantage
comes from erasing `AtomicSafetyHandle` checks, which only `ENABLE_UNITY_COLLECTIONS_CHECKS` builds (the
Editor) pay. A player build does not pay that cost, so its advantage here is smaller.

### 10.7 A container moves to native storage only together with its Burst consumer

`StageJob` (an `IJob`, `.Run()`) resolves incumbency itself, per arm, with no managed main-thread loop
over the visible records, because the two arms consume it differently:

- **The point arm inlines `Placed.Contains(s.FadeId)`** directly in Burst.
- **The curved arm resolves at the top of `Execute()`**, filling an `AnchorWasPlaced` byte array, because
  it cannot inline: `SymbolStagingMath.StageCurved` takes a `ReadOnlySpan<byte>` and lives in
  `MapRenderer.Core`, whose assembly references only `Unity.Mathematics` — a `NativeHashSet` in that
  signature would pull `Unity.Collections` into the engine-free `Tools/core-tests` project. The per-arm
  split is the only design that keeps `Core` engine-free and both arms' resolution off the main thread.

`.Run()` (not `Schedule()`) is load-bearing here: a later managed step in the same frame mutates
`_placedLastFrame`, so a scheduled job reading it would race.

Three of the four per-label placement containers are native for a Burst reader: `_placedLastFrame` is
read by `StageJob`, and `_fadeOpacity` and `_forceFadeOut` by `CompactJob`. `_seenFade` is native but has
no Burst reader: only `EaseFade` and `DecayUnseenFadeSymbols` touch it, on the main thread. It is the one
container that does not follow the rule below (open). **The general rule:** a container
moves to native storage only when the Burst consumer that reads it lands in the same change. A container
touched only by managed main-thread code pays `NativeHashMap` safety-handle overhead against a
`Dictionary` comparer the JIT already inlines, so moving it ahead of its consumer trades a
correctness-neutral, byte-identical change for a slowdown that no headless test can observe (only a
Play-mode profile shows it).

### 10.8 Carrying the previous frame's winners forward as incumbents converges after one step

Deferring the collision verdict by a frame ("The collision verdict applies one frame late") means a
later tick stages with a non-empty `_placedLastFrame`, so the A-5 incumbency term enters
`ComparePlacementOrder` on every tick after the first. This is safe because incumbency-carry-forward has a
measured one-step fixed point: feeding a generation's collision survivors forward as the next generation's incumbents, repeatedly, converges to
the same winner set at every subsequent generation — it does not oscillate. Inverting the incumbency term
(sorting incumbents last) does make the winner set churn hard, which confirms the fixed point is a real
property of the algorithm and not an artifact of a test that cannot fail.

This settles only whether repeated incumbency-carry changes *which* labels win — it says nothing about
latency: a later tick can still see a different candidate set than the tick before it, which is a
runtime trade (next section), not a correctness question.

### 10.9 The collision verdict applies one frame late (B-4b)

`CollisionJob` runs on a worker. Only its `Complete()` is deferred: collision is scheduled at the end of
frame N and its survivors are harvested at the top of frame N+1. This removes the main-thread wait, not
the work — the job runs on the same worker either way.

**Project and Stage stay current-frame.** They consume the current frame's screen position, depth, and
validity, and produce the screen-derived rotation, size, and curved arc-walk the world quads are built
from ("Curved labels stay camera-dependent") — deferring them would lag curved glyphs along their line
under motion. Only the collision verdict itself is a frame late.

**Three structural consequences of deferring only the verdict:**

1. **Emit runs before the schedule, not after.** Scheduling first would race the job's in-place sort of
   the staged candidates against the emit loop's read of the same array. Emit order is therefore staging
   order rather than placement order. That sets the blend order of overlapping transparent quads during a
   fade transition — an effect confined to a dying label overlapping a live one, not a change to which
   labels are visible.
2. **No double-buffering.** One `Complete` per tick dominates every code path into it; nothing else
   touches the job-held buffers in between.
3. **`_placedLastFrame` is refilled at harvest** — literally the current frame's survivor set, filtered by
   whether a candidate actually placed and is not force-faded-out. There is one incumbency set, not two;
   a second set would push incumbency two frames stale for no gain.

**This is not byte-identical to a same-frame verdict, and must not be described as one.** What is
preserved: which labels win on a settled scene (the one-step fixed point, previous section). What differs:
a one-frame verdict latency under camera motion, no survivors on the very first tick, the emit order
above, and `LastSurvivorCount` describing the previous frame rather than the current one. A camera jump is
not special-cased: the first frame after it applies the verdict computed for the previous view, and the
A-4 fade absorbs the difference.

**`FadeId` is the authoritative display key**, not only the opacity record's key — a debug-only
per-frame duplicate assert enforces its documented uniqueness contract, because the harvested survivor set
is what the user actually sees a frame later. Its scope is one frame: it cannot see sequential id reuse
across frames, and no cross-frame check exists for that, because stable cross-frame ids are the intended
behaviour and there is no separate invariant to assert there.

**No headless assertion can distinguish a genuinely deferred verdict from one that completes eagerly and
only reports as deferred** — the harvest branches on a once-per-tick handle check, and `JobHandle.Complete`
is idempotent, so forcing the job to finish early changes *when* the work happens without changing when
its result is applied. Confirming the deferral is real requires a Play-mode profile showing both halves
move together: the wait drops out of the collision marker, and the harvest cost stays near zero.

### 10.10 The fade map's contract: entries live only while a label is visible or fading in

`EaseFade` stores an entry for every faded identity it computes; the sweep that decays unseen entries
walks the whole map every frame. The map's size is bounded by the *visible* label count only if entries
that have eased to (or below) a small epsilon are dropped rather than kept at zero — otherwise the map
grows toward the total candidate count (every candidate that is ever staged but never placed parks a
permanent near-zero entry), and the per-frame decay sweep pays for that growth every frame regardless of
how few labels are actually fading.

The drop is guarded to a fade-**out** only (`target <= current`): a fade-**in** whose first step lands
below epsilon must still be stored, or it would restart from zero every frame — a permanent, invisible
stall. This matters because production's `deltaTime` is legitimately `0` while the game is paused, and a
just-shown candidate at `current == 0` then computes `next == 0`; guarded correctly, that stores a
legitimate sub-epsilon fade-in record instead. The map's contract is therefore "visible or fading in," not
"opacity above epsilon" — a maintainer narrowing the contract to the latter, even just in a comment,
reintroduces the unbounded growth with every existing test still green, because nothing asserts the
excluded case.

**Seen and stored are the same event.** The set of identities marked "seen this frame" (used to decide
what the decay sweep may drop) is updated in the same branch that stores the fade entry, not
unconditionally in the caller's loop — recording "seen" separately from "stored" reintroduces a mismatch
where an identity is stored (a guarded sub-epsilon fade-in) but not marked seen, so the sweep decays it
straight back out.

**A raw counter of the map's size is real telemetry** (`SymbolLiveFade`, surfacing
`SymbolPlacementTelemetrySnapshot.LiveFadeSymbolCount`), because it is the number whose unbounded growth
this contract prevents; it sits next to the collision-candidate count so the failure mode — the fade map
tracking candidates instead of placed labels — is visible at a glance. The counter must report the map's
raw size, not a filtered count of entries above epsilon: filtering it hides the growth it exists to
expose, and lets the map grow without bound while the counter still reads a small, reassuring number.

**Emit does not re-derive a placement bool it already has.** `StageJob` resolves `Placed.Contains(FadeId)`
in Burst and carries the result on the candidate as `WasPlacedLastFrame` (see "A container moves to native
storage only together with its Burst consumer"), and the sole writer of `_placedLastFrame`
runs before staging in the same tick, so the two cannot disagree. The emit loop does not read
`_placedLastFrame` again for the same fact. The curved arm's `WasPlacedLastFrame` comes from a sliced
array with a trailing centred-fallback slot — a layout prone to off-by-one errors — so a change here needs
to be checked against the curved arm specifically, not only against the aggregate test suite: a global
assertion can pass while the curved arm alone is silently wrong.

**Rejected: a grow-only cache of a quad's triangle indices.** A candidate's index buffer follows directly
from its ordinal (`4k + {0,1,2,0,2,3}` for the k-th quad), so a cache could avoid appending it per frame.
The appends are not the cost, and the cache would make every future emit code path depend on each quad
contributing exactly four vertices — a standing constraint for no measured gain. Do not re-attempt this
cache without first isolating the emit loop's cost by direct measurement (see "The residual").

**What remains in the per-quad emit loop is real work, not overhead**: per-vertex rotation, translation,
and tangent math (`BillboardMath.BuildWorldQuad`) that has to run once per visible quad. There is no
further win available here without changing what a quad is.
