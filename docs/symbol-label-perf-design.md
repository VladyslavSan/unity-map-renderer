# Symbol label per-frame cost — design

**Status:** designed, not started. Solution survey + recommended architecture for cutting the per-frame
symbol-label aggregation cost. The raw per-lens investigation (four independent proposer agents) is archived
in the devloop repo (`agents-devloop/proposals/symbol-label-batchbuild-perf.md`); this is the consolidated
SSOT. Companion to [`labels-and-symbols-design.md`](labels-and-symbols-design.md) (§1 pipeline, §1.5 the
tile-coverage pre-cull that already landed).

---

## 1. The problem

Live Play-mode profile, camera moving:

```
PlayerLoop                              16.61 ms
  MapRenderer.Symbol.BatchBuild         15.76 ms   ← ~95% of the frame
    …BatchBuild.Collect                  6.78 ms
    …BatchBuild.SoA                      8.98 ms
```

`SymbolLabelSubsystem.CurrentBatch` runs **every frame** and rebuilds the entire label batch from scratch:

- **Collect** (`SymbolTileLabelStore.CollectInto`) — the A-3 cross-tile point-label dedup over all active
  tiles (dedup grid = `MetersPerPixel(zoom)`).
- **SoA** (`SymbolLabelBatchBuilder.Build`) — converts the managed `LabelInstance` graph of every active tile
  into the blittable `SymbolLabelBatch`: glyph-quad copies, sRGB→linear vertex colours, fade-id string hashes,
  world anchors, per-tile world origins, and the point/curved stage inputs.

**Symptom:** camera **still** → acceptable (the landed tile-coverage pre-cull, `d5e712c0`, trims off-screen
/ tilt tiles). Camera **moving** → the visible set churns and grows, and the whole visible set is
re-converted from managed carriers every frame — 95% of the frame in a full rebuild.

There is also a **second, un-profiled copy of the same data**: after `Build`, `LabelPlacementSystem`
mirrors the managed SoA into the Burst-job NativeLists (`RefreshBatchMirror`, keyed on `BuildId`) every
frame. So the per-frame label data is materialised **twice**.

## 2. Root cause — the work is camera-independent but recomputed every frame

Almost everything `BatchBuild` produces is **fixed the moment a tile's labels are built**:

- **SoA is fully camera-independent** — glyph quads, colours, fade-ids, world anchors, and tile origins are
  world-space / launch-time-projection values. Pan / rotate / tilt do not change any of it.
- **Collect depends only additionally on zoom** (the dedup quantize). Within a zoom band the dedup result is
  stable; the quantize only ever merges a parent+child pair, which exists only during a zoom transition.

The genuinely **camera-dependent** work — anchor projection, global collision, billboarding — is the *cheap*
part, already Burst and downstream in `LabelPlacementSystem.Tick`. So the 15.76 ms is spent recomputing a
byte-identical result on every pan frame. **The fix is to stop redoing the camera-independent per-label
conversion per frame — bake it once, at tile build.**

## 3. Constraints (non-negotiable)

1. **Scope:** symbol label code only — `MapRenderer.Unity/Text/**`, `MapRenderer.Core/Text/**`,
   `MapRenderer.Core/Style/Symbol/**`, and the symbol tile-build path. NOT the tile pipeline / camera /
   projection / fill-line meshing / backends (beyond the existing symbol build + reconcile hooks and drawing
   per-tile the way fill/line already do).
2. **No result-caching / skip-when-nothing-changed.** The B-1 static-frame skip (keyed on camera stillness)
   was rejected: smooth static, janky on move — the inconsistency is unacceptable. The per-frame pass must
   stay **consistent under motion** (cheap is fine; a motion-keyed 0-or-full cost is not).
3. **Off-main-as-a-hide is not a fix.** Work must be genuinely *removed* from the critical path (done once at
   build), not shunted to a worker to hide the same cost.
4. **Behaviour-preserving.** Byte-identical render (GPU snapshots green, no re-bake); A-3 finest-zoom-wins
   dedup + stable cross-zoom identity + fade behaviour intact.

## 4. Shared foundation — build-time bake (all options build on this)

Bake each tile's blittable representation **once, on the existing bytes-ready build hook** (already off the
main thread), and hold it on the tile's `SymbolTileLabelStore` entry with the same lifecycle as its labels
(built on bytes-ready, kept warm in the prepared cache, dropped on eviction). This lifts the entire
`SoA.Hash` + `SoA.Copy` work — ~8.98 ms — out of the per-frame path to once-per-tile. Every option below is
a different answer to *what remains per-frame* after the bake.

## 5. Design options

| | Approach | Kills the per-frame residual via | Risk | Byte-identical |
|---|---|---|---|---|
| **A** | build-time bake (managed) | — leaves concat + re-dedup per frame | low | yes |
| **B** | bake **native** + Burst gather | native memcpy into the job NativeLists; also collapses the `RefreshBatchMirror` double-copy | low | yes |
| **D** | persist batch, patch **O(delta)** on tile changes | concat/dedup only on a tile-set delta | med | testable |
| **E** | **per-tile persistent draws** | draw never aggregates; only collision inputs stay per-frame | high | point/icon yes; curved carved out |
| **C** | reduce label **volume** (LOD/budgets) | fewer labels enter at all | — | **no** — visual change |

### A — build-time bake (the seed)
Bake the blittable SoA block per tile; per-frame `BatchBuild` concatenates the active blocks (memcpy) + runs
the cross-tile dedup over **precomputed** key parts (per-frame only re-quantizes the anchor by zoom). SoA →
~0/frame; Collect shrinks to the dedup resolve. Open question A leaves: is the per-frame concatenation cheap
enough, or should downstream iterate per-tile blocks in place?

### B — native bake + Burst gather (the low-risk realization of A)
Bake the block into **native** buffers (`NativeArray<PointStageInput>/<CurvedStageInput>` + flat
quad/glyph/anchor/worldpoint/fade-id pools — all already-blittable types). Per frame, a Burst `.Run()` gather
takes the collected winners `(blockId, localIndex)` and compacts them straight into the job-input NativeLists
— replacing **both** `Build` **and** `RefreshBatchMirror`. Keep the managed `CollectInto` dedup + collected
order (parity-safe → byte-identical by construction; it only records `(blockId, localIndex)`, does no SoA
work). Bake the dedup key components (anchor, layer, 64-bit text+icon hash) so per-frame stops re-hashing
strings. **SoA → ~0; the second mirror copy is gone; Collect shrinks.** Stretch (gated on the snapshot +
collision-differential tests): `LabelCollision.ComparePlacementOrder` is a strict total order, so the
survivor mesh depends on the candidate *set* not collected order — which unlocks moving the dedup itself into
a Burst hashmap job later.

### D — persistent, incrementally-maintained batch (O(delta))
Keep **one persistent batch** (SoA + dedup index) and patch it only when the active tile set changes (commit
inserts a block, release removes, rebuild replaces) at the `SymbolTileLabelStore` mutation points
(`CompleteBuild` / `BeginBuild` / `Release` / `Restore`). Between deltas the structure — and the native
mirror — are untouched, so a large visible set held steady while panning pays **~0** aggregation. Escapes
constraint #2 honestly: cost is O(structural delta) *every* frame, always applied, not a stillness predicate
(a slow pan crossing one boundary pays for that one tile whether "still" or moving). Cross-tile dedup deltas
stay local (a per-cell multiset promotes the runner-up on removal; cells are singleton/pair in a single
band) and zoom-stable between deltas. **The real risk is not the cache objection but byte-identical order
preservation** — point order today is `_dedup` hash-enumeration order and collision assigns by creation
ordinal, so an incremental structure needs a canonical order (e.g. TileKey, feature index) that the
GPU-snapshot suite proves equivalent (green ⇒ done; a flip ⇒ that scene has an order-dependent collision).
Plus tombstone/compaction to keep the buffer contiguous. **Biggest win where A is weakest** (steady set,
long pan). Sequential with A: A bakes the block, D persists + patches it.

### E — per-tile persistent draws (decouple collision from draw)
The world-anchored migration already made point/icon quads camera-independent geometry, yet
`WorldLabelRenderer` rebuilds each slot's mesh from survivors every frame. Instead, build each tile's label
quad geometry as a **persistent per-tile static mesh at tile-build time** and draw it directly like fill/line;
the per-frame collision pass produces only a small per-label **visibility + opacity** result that masks/fades
the static mesh (static vertex buffer + per-frame dynamic **index** buffer in collision-sorted order +
dynamic opacity stream → byte-identical overdraw). The draw half never re-aggregates; only the small
collision-input metadata is iterated per frame. This **refutes the code's "Copy is intrinsic per frame"
assumption** — true only when there is one aggregated draw. **Honest carve-out:** curved (along-line) labels
are a projected screen-space arc-walk → camera-dependent → *not* static; the win applies to **point/icon**
(the bulk), curved keeps a per-frame path. Also needs `text-rotation-alignment:map` moved to a shader bearing
uniform to stay static under a rotating map (north-up + panning unaffected). Highest ambition/risk.

### C — algorithmic do-less (orthogonal, visual)
Reduce the label **volume** — per-tile / per-region budgets or stricter zoom-LOD, prune by importance before
materialisation — so far fewer labels ever enter the pipeline. Distinct from A/B/D/E because it changes
**what renders** (not byte-identical) → needs explicit maintainer sign-off. A constant-factor lever that can
stack on any of the above. (Left as an open lever — the assigned proposer did not return a worked design.)

## 6. Recommended architecture & phased path

1. **Phase 1 — A realized as B** (native per-tile bake + Burst gather). The low-risk, high-confidence first
   move: removes ~9 ms of SoA **and** the `RefreshBatchMirror` double-copy from the per-frame path, stays
   byte-identical (managed dedup + collected order preserved), and is the enabling refactor every other
   option sits on. **Re-profile after it lands.**
2. **Phase 2 — only if the residual concat/dedup is still heavy on a large panning set:** escalate to **D**
   (incremental O(delta) — best for a steady set + long pan, at the cost of order-preservation + compaction
   machinery) **or** **E** (per-tile persistent draws for point/icon, accepting the curved carve-out + the
   bearing-uniform shader work). Choose by what Phase-1 re-profiling shows dominates.
3. **C (LOD)** — separate track, only if the maintainer wants fewer labels drawn (a visual decision).

Rationale: everyone converges on the build-time bake; **B is the safest way to bank the guaranteed ~9 ms +
the double-copy without touching dedup semantics or draw architecture.** D and E are larger, riskier bets to
attack the residual, and should be driven by a fresh profile rather than taken on speculatively.

## 7. Risks & open questions

- **Native lifetime on the store** (B/D): deterministic `Dispose` of per-tile native blocks across
  active/cached/departing + the async build-race generation guard — maps onto the repo's mesh-lifetime
  contract (value-data disposed at a boundary, single owner).
- **Byte-identical order** (D, and B's dedup stretch): current point order is `_dedup` hash-enumeration;
  a canonical order must be proven equivalent by the GPU-snapshot suite (never a re-bake to go green).
- **Baked text/icon key = 64-bit hash** (B): astronomically-low dedup collision risk; note as a parity caveat.
- **Curved labels** (E): camera-dependent arc-walk can't be static — sizing the point/icon vs curved split on
  the live scene is the key unknown for E's payoff.
- **Compaction** (D): tombstone/compact (or a block-indexed `LabelStageJob`) must keep the Burst mirror valid.
- **Coverage pre-cull + departing/fade interplay** (all): a culled tile skips the gather but keeps its baked
  block; departing/coverage-fade flags must ride the baked/persistent representation.

## 8. Target

`MapRenderer.Symbol.BatchBuild` well under a few ms/frame **while the camera moves**, byte-identical render
(snapshots green, no re-bake), no motion-keyed cost cliff. Phase 1 alone should reclaim the ~9 ms SoA + the
double-copy; Phase 2 targets the Collect/concat residual if it still matters.

## 9. OUTCOME — Phase 2 was a wrong turn; the real bottleneck is the per-frame dedup (2026-07-24)

A maintainer Play-mode re-profile after D1+D2+D3 landed **falsified this document's founding premise.** The
findings, and the pivot they force:

**What the profile actually showed (z14-15, moving camera):**

| marker | D1 (Phase 1 + coverage mask) | D2+D3 | verdict |
|---|---|---|---|
| `BatchBuild` | 10.04 ms | 18.16 ms | D2+D3 **regressed** it |
| `BatchBuild.Collect` | 9.37 ms | 17.45 ms | ~2× worse |
| ↳ `Collect.Dedup` (CollectInto) | ~9 ms | ~17 ms | **the whole cost** |
| ↳ `Collect.Classify` (coverage) | ~0.7 ms | ~0.7 ms | negligible |
| `BatchBuild.SoA` | ~0.7 ms | ~0.7 ms | Phase 1's bake win was real |

**The founding profile mis-attributed the cost.** §1 blamed `SoA` (the glyph-copy, 8.98 ms) and built Phase 1
+ D3 to eliminate the *copy*. Phase 1's bake genuinely killed the SoA (8.98 → ~0.7 ms). But the real cost was
always in **`Collect` — the per-frame cross-tile DEDUP** (`SymbolTileLabelStore.CollectInto`): for every
on-screen point label, every frame, build a `CrossTileLabelKey` (which contains the label's **text string**)
and hash it into a dictionary to pick the finest-zoom winner. That is O(labels) × string-hashed dict op, per
frame. D3 optimized a copy that was already cheap; it removed ~0 ms.

**D2 targeted the right thing (the dedup) with the wrong mechanism, and REGRESSED it.** Its incremental winner
index cold-reseeds on every zoom-quantize change — and a moving camera zooms constantly — so `Reseed` rebuilds
the entire index (`new Contender`/`WinnerRecord`/`TileContribution` **per label, per frame**) + GC churn:
strictly more work than the dict dedup it replaced. **D2+D3 were reverted** (`e3a9208e`), kept in history as a
documented negative result. Baseline is D1 (10 ms, stable).

**The key structural fact (from the maintainer):** the parent/child tile *overlap* that justifies the fuzzy
pixel-scaled grid + finest-zoom-wins machinery **does not currently happen** — coarse-under-fine display is
future work. Today the only real duplication is **same-zoom edge/buffer + cross-source**, which is **static
per tile-set** (independent of camera pose *and* fractional zoom). So the dedup is a **pure function of the
loaded tile set**, recomputed every frame for nothing.

**The right direction (superseded §9's options A–E; now LANDED — see §10):** stop treating the label batch as a per-frame rebuild.
Make the label system a **tile-event-driven state machine** — the deduped visible-label set is *state*, mutated
only when a tile is added/removed/rebuilt, and that recomputation is **scheduled off-main** and *picked up* on
a later update (dedup once per tile-set change, on a worker; process the cached set per frame). This is the
`off-main-thread-principle` + the pull/reconcile label-smoothness direction, now made concrete. **Full design:
[`labels-async-reconcile-design.md`](labels-async-reconcile-design.md).** Cheap orthogonal wins that compose
with it: intern label text → int (kill the per-frame string hash), and reuse buffers (off-main doesn't dodge
Unity's stop-the-world GC). Salvage from the reverted D2/D3: the `LabelStagingMath` finite-`SortKey`/NaN
collision-order fix (real, orthogonal), and the map of the 13 tile-lifecycle mutation points (where the
invalidation events fire).

## 10. The post-reconcile residual — what is expensive NOW (2026-07-25)

The async reconcile ([`labels-async-reconcile-design.md`](labels-async-reconcile-design.md), Stages 1–4,
`9bf19457`) hit its target: the per-frame dedup is gone and `BatchBuild` is down to ≤ 1.8 ms. The maintainer's
next Play-mode profile (numbers + full marker tree in that doc's §8 OUTCOME) shows the cost has simply moved
on. `MapRenderer.View.LateUpdate` = 19.36 ms, split:

| marker | ms | what it actually is | camera-dependent? |
|---|---|---|---|
| `Symbol.Gather` | 5.12 | `LabelPlacementSystem.GatherIntoMirror` — managed loop over every winner, per-record `NativeList` indexer writes + many small `NativeArray.Copy` calls, compacting the baked block slices into the mirror | **no** (except 3 byte masks) |
| `Symbol.Project` | 3.64 | `GatherSymbolPoints` (managed, ~1.4 with `ProjectSymbols`) + `Stage` 2.27 (`LabelStageJob`, Burst but `.Run()` = single-threaded, inline) | yes |
| `Symbol.Collide` | 5.03 | 4.06 of it is `JobHandle.Complete` — the main thread **idling** on the 4.05 ms single-threaded `LabelCollisionJob`; ~1 ms is main-thread grid pre-sizing | yes |
| `Symbol.Emit` | 3.04 | managed per-candidate loop: `HashSet`/`Dictionary<long>` fade lookups, then per-glyph-quad `NativeList.Add` ×13 in `WorldLabelRenderer.Emit` | yes |

### 10.1 The ordering rule (the §9 lesson, applied to ourselves)

§9's founding profile mis-attributed the cost, and D3 spent a stage optimizing a copy that was already cheap.
So: **one lever at a time, each gated on a fresh maintainer Play-mode re-profile.** The levers below are ranked
by (confidence × size ÷ risk); only R1 is committed. R2–R4 are candidates whose ordering the re-profile after R1
decides — do not plan them speculatively.

### 10.2 R1 (LANDED) — memoize the native gather on the front-set version

**Claim:** `GatherIntoMirror` is a pure function of the winner *set* — `plan.BlockId` / `LocalIndex` /
`Departing` / `Blocks` all come from `_frontResult`, the reconcile output, which changes **only on a front swap**
(a tile event). The only per-frame inputs are the `CoverageFading` / `Dropped` byte masks
(`LabelTileCoverageFilter.ClassifyActive`) — and `Departing`, which is set-derived but stays a per-frame write
because it is free. So: rebuild the heavy pools (quads / glyphs / anchors / fade-ids / world points + every
`*Start`/`*Count` remap) **only when the set version changes**; every frame write just the three per-record byte
masks. `Gather` 5.12 → ~0.1 ms is the **memo-hit-frame** figure — see the honest hit-rate note below.

**Expected hit rate — NOT ~100 %.** The memo invalidates on every front swap, and a swap follows every
reconcile — which is scheduled whenever `_store.CollectGeneration` moves, i.e. on every `BeginBuild` /
`CompleteBuild` / `Release` / `Restore`. While panning or zooming — **the condition the 5.12 ms was measured
under** — tile events are near-continuous and the worker turns around in 1–4 frames, so expect a rebuild every
~2–5 frames: a **~50–80 % hit rate**, not ~100 %. The full win lands on a **stable cover / still camera**. So R1
removes the steady-state gather cost; it does not remove the per-rebuild spike, and `Symbol.Gather` does not
"disappear" from a panning profile. This is not a re-introduction of the rejected §3-constraint-2 motion-keyed
cache: the cost is still O(set delta), always applied, with no camera-state predicate — a slow pan crossing one
tile boundary pays for that one event whether the camera is "still" or moving. The maintainer Play-mode
re-profile must therefore cover **both** a still camera and an active pan — reading the verdict off only one of
the two reads it off the wrong data.

**Why this is not the rejected constraint-#2 cache:** the memo key is a **tile event** (the front swap), not
camera stillness. Cost is O(set delta), always applied, identical whether the camera is still or moving — the
same honesty the async reconcile is built on (`labels-async-reconcile-design.md` §2). There is no motion-keyed
cliff and no "skip when nothing changed" predicate over camera state.

**Verified preconditions** (checked against source before committing to this):
- The mirror `_m*` pools are written **only** in `GatherIntoMirror` / `RefreshBatchMirror`; downstream
  (`LabelStageJob`, `RunCollision`) reads them and writes the separate `_sj*` pools, and the in-place candidate
  sort touches `_stageCandidates`, not the mirror. So a retained mirror cannot be corrupted between frames.
- `_orderedBlocks` is copied wholesale from the reconcile result, and blocks are **pinned** while a snapshot is in
  service — so a block's contents cannot change under a retained mirror without a front swap.

**Design notes (as landed):**
- The key is `SymbolLabelSubsystem._frontSetVersion` — a monotonic `int` bumped on every change to `_frontResult`'s
  CONTENT: the successful reconcile swap (`PickupCompletedReconcile`), and the `SetStyle`/`Dispose` clears.
  Stamped onto `SymbolGatherPlan.WinnerSetVersion` at `Build` time.
- **`_store.CollectGeneration` was considered and rejected as the key** (this design note originally suggested
  reusing it — that was wrong, corrected during planning). `CollectGeneration` moves at the tile **event**
  (`ScheduleReconcileIfDirty` latches it immediately); the front only moves 1–4 frames later, at the swap
  (apply-stale, `labels-async-reconcile-design.md` §2). A `CollectGeneration`-keyed memo would keep hitting
  straight through the swap window and serve a stale mirror for those frames. The version must be bumped
  **at the swap**, not at the event.
- **Cross-overload invalidation, both directions.** The old demo-path sentinel (`_lastBatch`/`_mirrorBuildId`)
  generalized to `_mirrorSource` (object) / `_mirrorVersion` (long) — one pair covering both the batch (demo) and
  plan (production) sources, so a demo `Tick(batch)` between two production gathers invalidates the production
  memo and vice versa (one `ReferenceEquals`-and-version comparison, not two parallel sentinels).
- **A release-build backstop.** The memo predicate also checks `plan.WinnerCount == _mirrorCount` (not just source +
  version) — if a future front-mutation site ever lands without a version bump, this falls through to a full
  rebuild instead of a length-mismatched `NativeArray.Copy` (a checked throw in the Editor, a silent
  out-of-bounds read in a release player). A debug-only assert fires whenever this backstop engages.
- A same-version frame is three `NativeArray<byte>.Copy` memcpys (the masks) + a subtraction (`DroppedCount`),
  not a per-record loop — the record count that mattered for the ~0.1 ms target.

**Acceptance teeth (all landed, `LabelGatherMemoTests` + two integration tests in `SymbolLabelReconcileAsyncTests`):**
- N ticks with no tile event ⇒ mirror byte-identical to a fresh `GatherIntoMirror` over the same plan
  (`CopyMirrorInto` is the sanctioned seam), with `MirrorRebuildCount` proving the memo actually engaged.
- A version change invalidates (unit); a REAL front swap through the production subsystem invalidates
  (integration — the stage's only end-to-end guard); a demo `Tick(batch)` between two production gathers
  invalidates (the reverse cross-overload direction); a restyle invalidates against a now-empty front.
- Masks (`Departing`/`CoverageFading`/`Dropped`) track per-frame classification while the pools are held —
  including Dropped, asserted behaviourally via a real `Tick` + `WorldMeshReadback` mesh comparison (Dropped
  isn't visible through `CopyMirrorInto`).
- RED-verified against every listed defect (dropped version term, missing swap/restyle bump, skipped mask write,
  removed early-out) — a memo test that never engages is trivially green.
- GPU snapshots green, no re-bake — but they are **blind to the memo** (every snapshot fixture drives the demo
  `Tick(batch)` overload, never the production plan path — `grep -c SymbolGatherPlan` is 0 in all of them). Perf
  itself is **not** headless-gateable: the verdict is a maintainer Play-mode re-profile, covering both a still
  camera and an active pan (see the hit-rate note above).

### 10.3 R2–R4 (candidates, unplanned — ordered by the post-R1 re-profile)

- **R2 — native fade state. LANDED, but NARROWED — see §10.6.** As drafted here it proposed moving all four
  managed containers (`_fadeOpacity`, `_seenFade`, `_forceFadeOut`, `_placedLastFrame`) to native ones. Only
  `_placedLastFrame` actually moved; the other three have no Burst consumer and moving them would have been a
  pessimisation. `ResolveIncumbency` is deleted, not "made Burstable".
- **R3 — deferred collision (the old B-4b).** Reclaim the 4.06 ms `JobHandle.Complete` stall by scheduling the
  collision and consuming its survivors on the **next** frame. **Fence:** defer *only the collision* — Project +
  Stage must stay current-frame, because `LabelStageJob` consumes this frame's `Screen`/`Depth`/`Valid` and
  produces screen-derived rotation/size + the curved arc-walk that the world quads are built from (the same
  camera-dependence §5 option E carves curved labels out for). Deferring the whole chain lags curved glyphs along
  their line under motion. Cost of R3: the collision sorts `_stageCandidates` in place, so last frame's survivor
  flags must be re-keyed to this frame's candidates by `FadeId` — real work, its own stage.
- **R4 — cheap main-thread scraps in `Collide`.** The grid clear is a managed `for` over `W*H` cells with
  per-element bounds checks (fold into the job / memset), and `NodeUpperBoundByCandidates` walks every
  candidate's box references on the main thread. ~1 ms, low risk, no architecture change.
- **Not a lever yet:** `LabelCollisionJob` itself (4.05 ms, single-threaded greedy) — inherently serial by
  design; attack it only if the re-profile leaves it dominant after R3 hides the wait.

### 10.4 OUTCOME — R1 measured: faster when still, UNCHANGED under motion (2026-07-25)

Maintainer Play-mode re-profile of R1, both scenarios as §10.2 required:

- **Camera still:** faster. The memo engages, `Symbol.Gather` collapses to three mask memcpys. The key is right
  and the state machine behaves.
- **Camera moving:** *exactly the same.* The memo effectively never hits.

**§10.2's "~50-80 % hit rate under motion" was wrong** — the real hit rate under motion is ≈0. The reasoning
was right in kind and wrong in degree: it assumed tile events are occasional. They are not.

**Why the memo is structurally dead under motion.** Every tile-lifecycle path dirties the store, and while
panning they fire continuously and overlap: `BeginBuild` (`SymbolTileLabelStore.cs:156`), `CompleteBuild`
(`:195`), `Release` (`:213`), the cached-FIFO evict (`:222`), `Restore` (`:233`), and `PurgeExpiredDeparting`
(`:606`) — the last running *every frame*, dirtying whenever any departing tile's grace expires. So
`CollectGeneration` moves ~every frame ⇒ a reconcile is always in flight ⇒ a front swap always lands ⇒
`_frontSetVersion` always moves. The memo never gets a quiet frame to hit on.

**The deeper point, which outlives R1:** the mirror rebuild is **all-or-nothing**. A one-tile delta in a
several-hundred-tile visible set costs a full 5.12 ms rebuild of every winner's baked slice. Memoization can only
ever answer "did *anything* change?", and under motion the answer is always yes. **So the remaining levers must
be motion-independent** — make the rebuild cheap, or move it off the critical path — not "rebuild less often."

R1 stays landed and correct: it is the enabling refactor (the mirror is now explicit STATE with an explicit
version and an explicit invalidation event), and it is a real win on a settled camera. It is simply not the
frame-rate story.

### 10.5 The direction — the gather as a state machine, mirroring the reconcile (maintainer, 2026-07-25)

> "Can we make gather work exactly the same as the previous work? The gathered labels are a state, a job mutates
> one state into another state — data-oriented design — and when the job finishes the state is simply applied."

Yes, and it is the natural next move: apply the **same pattern that already worked one layer up**. The async
reconcile made the deduped winner set a state produced off-main and applied at a pickup; R1 made the native
mirror an explicit versioned state. The missing half is that the mirror's *production* still happens
synchronously on the main thread.

```
tile events ─► [worker] dedup ─► winner-set state ─► [job] gather ─► MIRROR state ─► per-frame camera passes
               (landed: reconcile)                    (proposed)                     (project/collide/emit)
```

**The shape.** On a front swap, schedule a Burst job that builds the *next* mirror into a back buffer from the
new winner set; when it completes (a frame or two later), swap it in — a pointer swap, exactly like the
reconcile's front/back snapshot swap. Per frame the main thread pays only the three per-record mask writes plus,
occasionally, the swap. The rebuild leaves the critical path entirely instead of being merely skipped when
nothing changed — which is what makes it work under motion, where "nothing changed" is never true.

This composes with R1 rather than replacing it: R1 supplies the version, the invalidation event and the
byte-identity teeth the async version needs to prove itself against.

**The two real wrinkles** (resolve in the plan, do not hand-wave):

1. **Burst cannot hold `SymbolTileLabelBlock[]`** — a managed array of class instances each owning
   `NativeArray`s. The job needs either a flattened pointer table (`NativeArray` of block descriptors with
   `[NativeDisableUnsafePtrRestriction]` pointers + lengths) or, more invasively, a shared bake-time mega-buffer
   so the gather becomes pure index arithmetic. The pointer table is the smaller step; the mega-buffer is the
   cleaner end state. **This choice is the crux of the stage.**
2. **The per-frame masks must follow the DISPLAYED set, not the newest front.** With an async gather the live
   mirror corresponds to an *older* winner set than the current reconcile front, so
   `LabelTileCoverageFilter.ClassifyActive` must classify the displayed snapshot — otherwise the masks index a
   set the mirror is not built from. R1's `plan.WinnerCount == _mirrorCount` backstop would catch the mismatch, but
   catching it is not the same as being correct: the classify input has to be the displayed set by construction.

**Lifetime** rides the existing machinery: pin the snapshot whose blocks an in-flight gather reads, release when
its mirror leaves service — the same Pin/ReleasePins contract the reconcile already uses (§3.2 of the reconcile
design), extended one layer down.

**Staleness cost:** the mirror lands 1–2 frames behind the reconcile front, which is itself 1–4 frames behind the
tile events. Same accepted trade as everywhere else here — labels appear slightly late, the frame rate stays
stable.

**Fallback if the async gather proves too invasive:** Burst the gather *synchronously* first (the plan's stated
fallback — the 5.12 ms is dominated by managed `NativeList` indexer overhead, not the memcpys). That is a smaller
change with a smaller win, and it is a stepping stone to the async version, since both need the same pointer-table
or mega-buffer refactor.

**Same pattern, one layer further:** R3 (deferred collision) is the identical idea applied to the collision pass —
schedule now, apply next frame. Sequenced after this, the whole label pipeline becomes one pipelined state machine
with only the genuinely camera-dependent passes left on the main thread. That is the end state
`off-main-thread-principle` points at.

**Measurement first.** Before committing to the above, surface the rebuild rate (`MirrorRebuildCount` delta per
second) in the telemetry panel. ≈60/s confirms the memo is structurally dead under motion and the async gather is
the right target; a materially lower number would mean something *else* dominates the moving-camera frame and the
ranking changes. Cheap, and it replaces the estimate that §10.4 just had to retract.

### 10.6 R2 (LANDED) — the A-5 incumbency resolve moved into Burst

**What shipped, and why it is narrower than §10.3 proposed.** §10.3 listed four managed containers to move to
native ones. Applying a "who reads this from Burst?" test to each, only **one** qualified:

| container | callers | in-stage Burst consumer? | moved? |
|---|---|---|---|
| `_placedLastFrame` | `ResolveIncumbency` (→ `LabelStageJob`), `Emit` | **yes** | **yes** |
| `_fadeOpacity` | `EaseFade`, `DecayUnseenFadeRecords`, `MarkFadeOutIfAlive`, `TryForceFadeOut` | no | no |
| `_seenFade` | `Emit`, `DecayUnseenFadeRecords` | no | no |
| `_forceFadeOut` | `GatherSymbolPoints`, `MarkFadeOutIfAlive`, `TryForceFadeOut`, `Emit` | no | no |

The other three are touched **exclusively by managed main-thread code**. Moving them buys nothing and plausibly
costs: managed-side `NativeHashMap` access pays safety-handle checks against a `Dictionary<long,float>` whose
`long` comparer the JIT inlines. That would have landed a byte-identical, green, *slower* change — and since perf
here is not headless-gateable, it would only have surfaced at the maintainer's re-profile. **Rule adopted: a
container moves to native storage only when the Burst consumer that reads it lands in the same stage.** The three
belong to the later emit-job stage, where their cost can be attributed and their Burst correctness proven.

**The shape.** `ResolveIncumbency` — two managed main-thread loops running once per visible record (~30-40 k in
the profiled scene, inside `Symbol.Project`) — is **deleted**. `LabelStageJob` (an `IJob`, `.Run()`) resolves
incumbency itself, per arm, because the two arms consume it differently:

- **Point arm:** inlines `Placed.Contains(s.FadeId)`. `_stagePointWasPlaced` is deleted outright (field, alloc,
  resize, fill loop, job field, Dispose).
- **Curved arm:** the resolve loop moves to the top of `Execute()`, still filling the `AnchorWasPlaced` byte
  array. It **cannot** inline, and this is structural, not a preference: `LabelStagingMath.StageCurved` takes a
  `ReadOnlySpan<byte>`, lives in `MapRenderer.Core` — whose asmdef references only `Unity.Mathematics` and
  UniTask — and is compiled **verbatim** into the engine-free `Tools/core-tests` project. A `NativeHashSet` in
  that signature would demand a Collections shim there. The per-arm hybrid is the only design that keeps `Core`
  engine-free, leaves `StageCurved`'s signature (and every Core-side differential test) untouched, and still
  gets both arms' resolution off the main thread. No new job, no new dispatch.

`.Run()` (not `Schedule()`) is load-bearing: the managed `Emit` loop mutates `_placedLastFrame` later in the same
frame, so a scheduled job reading it would race. `AsReadOnly()` is constructed per frame at the job-construction
site and never cached — the `ReadOnly` captures a raw pointer + safety handle at construction, so a stored copy
would dangle the first time `Add()` grows the set.

**Coverage this stage had to create.** Nothing tested the `_placedLastFrame` → `WasPlacedLastFrame` path
end-to-end (`LabelCandidateCollisionTests` sets the flag by hand; `LabelStageJobTests` fed it as a raw input;
`LabelProjectionJobTests` deliberately arranges distinct sort keys so incumbency is a no-op), and **every**
existing differential case passed `wasPlaced = {0,0}` — so the curved arm's incumbency term had never been
exercised at all. Both were added and RED-verified against an injected defect (both `Placed.Contains` calls
replaced by constants): the point-arm test failed `Expected: 2, But was: 3`, and 8 of 14
`BurstStage_MatchesManaged_CurvedBends(…,True)` cases failed on `candidate WasPlacedLastFrame`.

**Gate:** EditMode 1708/1708, `Tools/core-tests` 1076/1076 (the latter proving `Core` was not dragged into
`Unity.Collections` — the fork's load-bearing constraint).

**Open findings (recorded, not fixed in-stage):**

- **The perf verdict is still owed, and one scenario is not evidence.** `_placedLastFrame.Add` in the `Emit`
  loop is now a safety-checked native call from managed code. The trade is quantitatively favourable — tens of
  thousands of per-record lookups leave the main thread, at most survivor-count adds get marginally dearer — but
  only a maintainer re-profile covering a **still camera AND an active pan** can confirm `Symbol.Project` fell by
  more than `Symbol.Emit` rose. That is §10.4's lesson applied to this stage: R1 looked good on one scenario and
  was worthless on the other.
- **Differential coverage gap (F2).** `BurstStage_MatchesManaged_CurvedBends` can never stage the **centred
  fallback** — its centre coincides with the sole anchor's, so both fail the same max-angle gate — which is also
  why 6 of the 14 `(…,True)` cases cannot go red under injection (total arc is 120 for every bend, so the gate
  drops the anchor exactly when `bendDeg > maxAngleDeg`, leaving `Staged == 0`). This leaves
  `AnchorWasPlaced[fadeStart + anchorCount]` unasserted for incumbency. Not a hole in R2 — an off-by-one would
  still go red via `AnchorWasPlaced[0]` — but a case with an asymmetric polyline, or an anchor near an end so
  only the fallback can stage, would close it.
- **Two-site sizing coupling (F4).** `AnchorWasPlaced.Length == AnchorFadeIds.Length` is now maintained by two
  independent sites (`PreSizeStageOutputs` sizes from `_mirrorFadeCount`; `RunStageJob` passes `_mirrorFadeIds.AsArray()`).
  Both are correct today, and safe only because `Mirror` truncates to `count` while the batch's own arrays are
  `Grow`-doubled — an asymmetry invisible unless you read both. A debug-only assert in `RunStageJob` would make a
  future mirror change fail loudly instead of silently under-filling (or reading out of bounds in a player with
  safety checks off) — same spirit as `AssertMemoPlanMatchesMirror`.
- **Nits deferred:** the `Allocator.Temp` set round-trip in `LabelStageJobTests`' case body (removable in favour
  of a byte literal), and the A-5/R2 rationale appearing three times in `LabelStageJob` (class summary + two
  field comments).

### 10.7 Measured: carrying incumbency forward does NOT change the winners (2026-07-25)

R3 (deferred collision) needs to know whether a test that ticks **twice** selects the same labels as one that
ticks once. It does not obviously: with the collision deferred, tick 2 stages with a non-empty
`_placedLastFrame`, so the A-5 incumbency term enters `ComparePlacementOrder` and could reorder the greedy pass.
`LabelCollision.cs:140-142` *claims* it cannot — "incumbency only ever RAISES priority, last frame's survivor set
is a one-step fixed point (no oscillation)". That is a comment, not a measurement, and it sits in the same file
as the `FadeId` tiebreak that exists precisely because an A-5 feedback **limit cycle** did once occur
(`line-label-overlap-crosstile-identity`). So it was measured.

**Probe** (throwaway, run in `Tools/core-tests`, not committed): randomized dense scenes — 60-100 candidates,
a mix of 1-box (point) and 3-box (curved) candidates in a small field, with deliberately coarse `SortKey` /
`FeatureIndex` / `TileKey` so ties are common and the incumbency term is reached often. Run
`SelectSurvivors` three generations deep, feeding each generation's survivor set forward as the next one's
incumbents.

**Result: identical survivor sets at every generation, all 8 seeds.** Contention was real, not vacuous — e.g.
92 candidates → 39 survivors, ~60-70 % losing across seeds.

**The probe was RED-verified**, because a fixed-point test that cannot fail proves nothing: inverting the
incumbency term (incumbents sort LAST) made the winner set churn hard — 13-19 labels added and 12-20 dropped per
seed. So the green result reflects the property, not an insensitive probe.

**Consequence for R3.** Making a fixture tick twice does not change which labels win, so existing single-tick
expectations remain valid under a two-tick sequence. What still changes is that tick 1 has no collision result
yet, so a **one-tick** test sees no survivors — those fixtures must gain a second tick. That is a sequencing
change to the tests, not a change to their expected values: **no expected value or tolerance may be weakened**
to accommodate R3. (Note for anyone carrying over the earlier framing: this repo has **no committed
golden-image baselines** — the "snapshot" tests render in-run and assert structural properties, so "never
re-bake" means "never loosen an assertion".)

This settles the collision fixed-point question only. It says nothing about R3's *latency* behaviour under
motion, where frame N+1's candidate set genuinely differs from frame N's — that remains a runtime trade
(labels decided one frame late), not a test-correctness question.

### 10.8 R3 (LANDED) — the collision verdict applies one frame late

`LabelCollisionJob` always ran on a worker; `RunCollision` simply blocked on it, so **4.06 ms of the 5.03 ms
`Symbol.Collide` marker was the main thread idling**. R3 schedules at the end of frame N and harvests the
survivors at the top of frame N+1. Nothing moved to a thread — the *wait* was removed.

**Project and Stage stay current-frame** (the fence): they consume this frame's `Screen`/`Depth`/`Valid` and
produce the screen-derived rotation/size plus the curved arc-walk the world quads are built from. Deferring
them would lag curved glyphs along their line under motion. Only the verdict moves.

**Three structural consequences, none optional:**

1. **Emit moves BEFORE the schedule.** Scheduling first races the job's in-place sort of `_stageCandidates`
   against the emit loop's reads of the same array — Unity's safety system throws on exactly that, which the
   RED-verify confirmed. Emit order therefore becomes **staging order** rather than placement order, changing
   the blend order of overlapping transparent quads. Measured gate exposure: of 99 `.Tick(in ` call sites, 31
   pass an explicit `deltaTime`, and the only **pixel-rendering** fixture among them
   (`SymbolLayerOrderSnapshotTests`) passes `+inf`, which snaps opacity so losers never draw. **No asserted
   pixel can change.** The live effect is confined to fade transients where a dying label overlaps a live one.
2. **No double-buffering.** One `Complete` at the top of `TickCore` dominates all three `Tick` overloads;
   nothing touches the seven job-held buffers in between.
3. **One set, not two.** `_placedLastFrame` itself is refilled at harvest, filtered by
   `_nSurvivors[s] != 0 && !_forceFadeOut.Contains(id)` — literally today's `show` expression on frame N's data.
   Two sets would push A-5 incumbency two frames stale for no gain.

**Invariant — NOT byte-identical, and it must not be described as one.** Preserved: which labels win on a
settled scene (guaranteed by §10.7's measured one-step fixed point). Not preserved, deliberately: a one-frame
verdict latency under motion, nothing on the very first `Tick`, emit order as above, and `LastSurvivorCount`
now describing the previous frame.

**FadeId became the authoritative display key,** so `LabelCandidate.FadeId`'s documented uniqueness contract is
now load-bearing for what the user sees rather than only for the opacity record. A debug-only per-frame
duplicate assert enforces it. **The assert immediately justified itself**, self-reporting two colliding
fixtures (`WorldLabelGroupingTests`, `LabelPlacementAllocTests`) that a manual sweep had missed on top of the
two already known — which is why §2.5 of the plan was rewritten to call the sweep best-effort and name the
assert as the real mechanism. Its scope limit is real and recorded: it sees one frame's candidates, so
sequential id reuse across frames is invisible to it, and no cross-frame check is proposed because stable
cross-frame ids are the *designed* behaviour — there is no well-formed invariant to assert.

**Gate:** EditMode 1712/1712, `Tools/core-tests` 1076/1076. RED-verified against the null implementation
(schedule before emit + positional survivors) — 55 tests red with the predicted `AtomicSafetyHandle` throw.

**What the headless suite CANNOT prove, and why the perf verdict is a required gate step.** No EditMode
assertion can distinguish "genuinely deferred" from "structurally deferred but eagerly completed": the harvest
branches only on a once-per-Tick `_collisionHandle is { }` check and `JobHandle.Complete()` is idempotent, so forcing
the job to finish early changes when the CPU work happens, not when its result is applied. The plan's original
"primary RED-verify" claimed otherwise and was **wrong**; adversarial review caught it. The verdict is a
maintainer Play-mode profile reporting **both** numbers — `Symbol.Collide` down ~4 ms **and**
`Symbol.CollideHarvest` ≈0. Either alone is satisfiable by a broken implementation: an eager `Complete()` sits
inside `PmCollide`, so `Collide` would *not* shrink while `CollideHarvest` still read ≈0.

**Scope note:** R3 is a **~4 ms** lever, not ~5 ms. The ~1 ms of main-thread grid pre-sizing stays on frame N —
that is R4.

**Recorded finding — the tick churn disarmed two tests, and "no weakened assertion" did not catch it.** ~40
call sites gained a second `Tick`. One reviewer verified no assertion was weakened or tolerance widened — true,
and insufficient: two pre-existing tests kept their expected values while **losing the precondition that gave
them meaning**. `DemoThenProduction_SameBuildId_…` stopped reproducing its own `BuildId` collision (the demo
overload rebuilds its batch per `Tick`, so the duplicate pushed `_mirrorVersion` to 2 while the production
batch stayed at 1 — the version term alone then discriminated, and the identity term it exists to pin went
untested); `Memo_DropFlip_TrackedWhilePoolsHeld` lost its "the slot was visible before the Drop" premise and
asserted vacuously. Both restored, the first RED-verified live against a version-only guard. **Lesson for any
future stage that changes tick sequencing: check that each touched test's PRECONDITIONS still hold, not merely
that its expected values are unchanged.** A disarmed test reads identically to a healthy one.

### 10.9 R (LANDED) — the gather itself as a synchronous Burst job (Stage 1 of 2)

`LabelPlacementSystem.GatherIntoMirror`'s two managed loops (compact each winner's pre-baked
`SymbolTileLabelBlock` slice into the native mirror, remapping every `Detail`/`*Start` field by a running pool
offset) are now one `[BurstCompile(CompileSynchronously = true)] IJob` (`SymbolGatherJob`,
`Assets/Code/MapRenderer.Jobs/SymbolGatherJob.cs`), run **synchronously** (`.Run()`) in the exact same place the
managed loops ran — inside `PmGather.Auto()`, strictly before `TickCore`. This is Stage 1 of 2 (§10.5): stage 2
(async, back buffer + swap) is fenced out; stage 1 exists because it is **byte-identical and therefore
testable**, which the async version is not by construction.

**Storage decision (a′) — a per-frame view table, block storage untouched.** `SymbolTileLabelBlock` keeps its
19 `NativeArray<T>` fields exactly as they are. Immediately before the job runs, `BuildBlockViews`
(`LabelPlacementSystem.cs`) builds a reusable `NativeList<BlockView>` — one entry per
`plan.Blocks[0, plan.BlockCount)` — where `BlockView` (`Assets/Code/MapRenderer.Jobs/BlockView.cs`)
holds 19 typed **non-owning `UnsafeList<T>` views**, each built via
`new UnsafeList<T>((T*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(array), array.Length)`. Two options were
rejected, on call-site count AND on lifetime safety:

- **(b) convert block storage itself to `UnsafeList<T>`** — ≈110 call sites outside the gather (block field
  decls, the baker's allocations/writes, `DoDispose`'s `IsCreated` guards, the leak-baseline fixtures, ~30
  `SymbolTileLabelBlockBakerTests` assertion sites) vs. (a′)'s ≈0. Worse: it would delete the Editor
  `AtomicSafetyHandle` detection every block read has TODAY (`NativeArray<T>` carries a safety handle;
  `UnsafeList<T>` does not) — for every reader, baker and tests included — in exchange for nothing (a′) doesn't
  already give.
- **(c) a bake-time mega-buffer with a generation counter** — ≈130 call sites + a new shared-buffer ownership
  design (who grows/defragments it). Remains reachable later (nothing in (a′) forecloses it) but wasn't needed
  here.

Under (a′), the unsafe surface is confined to ONE function (`BuildBlockViews`) and ONE job. `BuildBlockViews`
itself still goes through `NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr`, which performs
`AtomicSafetyHandle.CheckRead` — so a disposed block is still caught, in the Editor, at the moment the view is
taken, on the main thread, with a real stack — a materially better failure mode than a job silently reading
freed memory. Stage 1 is safe by construction: `.Run()` executes inline on the calling thread, so no view can
outlive the block it points into.

**Stage 2's lifetime obligation, recorded now so its planner doesn't rediscover it.** Once the gather is
scheduled ahead of a front swap, the view table's raw pointers DO outlive the frame, and the front snapshot
whose blocks they point into may be demoted and released in between. Four obligations, none optional: (1) pin
the snapshot the gather reads, independently of the front pin, via the store's existing
`Pin`/`ReleasePins`/`DisposeOrDefer` refcount — taken at schedule, released when the mirror that job produced
leaves service (not at apply); (2) a debug-only generation stamp on the view table, checked at apply — no
safety handle catches a cross-frame violation, so the invariant needs its own assert; (3) `ClassifyActive` must
classify the DISPLAYED set, not the newest front (§10.5 wrinkle 2); **(4) — found in review, not in the
original §2.4 list — `_gatherBlockViews` and `_gatherCounts` are single REUSED containers.** The moment the
gather is scheduled instead of run inline, frame N+1's `BuildBlockViews` would rewrite a table an in-flight job
is still reading (and `_gatherCounts` likewise). `NativeList`'s own safety handle would throw loudly in the
Editor rather than corrupt silently — a real backstop, but not a substitute for the fix — so stage 2 must
double-buffer BOTH containers, the same way the mirror lists themselves will need a back buffer.

**Two more findings from review, recorded, not fixed this stage (out of the §9 fence):**
- **Pin scope is wider than referenced blocks.** `BuildBlockViews` takes pointers from every block in
  `plan.Blocks[0, plan.BlockCount)`, including blocks no winner references this frame — the managed gather
  only ever touched referenced blocks. Safe today (the paired front/back snapshots already pin the WHOLE
  ordered set, `SymbolLabelSubsystem.cs:670-673`), but stage 2's pin accounting must cover the whole ordered
  set, not just the referenced subset, or it will under-pin relative to what stage 1 silently relied on.
- **Per-record view copy cost.** `BlockView block = BlockViews[BlockId[r]]` copies 19 `UnsafeList<T>`
  fields (~608 B) per winner, twice per winner (pass 1 and pass 2). Burst will likely SROA this away — but if
  the maintainer's Play-mode profile misses the §7 falsifier-1 threshold (`Symbol.Gather` moving > 2.5 ms),
  look HERE before concluding "memcpy bandwidth dominates" and re-ranking toward option (c).
- **Editor-vs-player scope**, from the critique's §6: this stage's whole win rests on erasing
  `AtomicSafetyHandle` checks that `ENABLE_UNITY_COLLECTIONS_CHECKS` only pays in the Editor — the measured win
  may not transfer 1:1 to a player build, where those checks were never being paid. Not previously in this doc;
  recorded now.
- **`SymbolLabelBatchDiff` does not compare `RecordDropped`** (`SymbolLabelBatchDiff.cs:31-40` — Departing and
  CoverageFading only). Pre-existing gap in the parity oracle, not introduced by this stage; `RecordDropped`
  coverage lives in `SymbolGatherPlanDropMaskTests` instead.

**Job shape.** One self-sizing `IJob`: pass 1 totals the per-pool sizes, pass 2 resizes the 19 output
`NativeList<T>`s once and fills them. Growing an externally-owned `Allocator.Persistent` `NativeList<T>` from
inside a Burst job (as opposed to `Allocator.Temp` scratch created inside the job, `EarcutJob`'s precedent) has
its OWN in-repo precedent: `GlobeFillSubdivideJob<TProj>.Execute` calls `OutVerts.Add(...)` on a
`NativeList<GlobeFillVertex>` allocated by its caller (`StyledFillTileBuilder.cs:268`) — confirmed by reading
both sites, not merely cited. `LabelStageJob`'s doc comment ("Burst cannot grow a container mid-run") does not
contradict this: it describes ITS OWN fixed-length `NativeArray` outputs (that job's caller doesn't know the
per-record counts either, so it sizes to the worst case) — `SymbolGatherJob` computes the exact counts in its
OWN pass 1 before pass 2 needs the resize, so its growth is single-shot and bounded, not per-element. The comment
was reworded (`LabelStageJob.cs`) so the two jobs' docs don't read as contradicting each other.

**Invariant — byte-identical, confirmed, not merely claimed.** `SymbolGatherParityTests.Gather_MatchesBuildOracle_FieldByField`
(the differential oracle — `SymbolLabelBatchBuilder.Build`, a completely independent managed implementation
that never touches a block, a view, or the job) passed unchanged against the new Burst gather. Its
independence claim needs one qualifier: it is a real, independent oracle for the gather's compaction/remap
spine (winner order, the `(blockId, localIndex)` mapping, the running-offset arithmetic this stage rewrites)
— that is what its two RED-verify siblings actually probe — but `SymbolTileLabelBlockBaker.Bake` and the
oracle's own `Build` share the `BuildPointInput`/`BuildCurvedInput` per-label field helpers, so a bug INSIDE
those helpers would produce matching wrong values on both sides and this test cannot catch it. Not a gap this
stage introduces (the job never touches those helpers); recorded so a future reader doesn't cite the test as
covering more than it does.

**§6.2 widened fixture — landed, and it took TWO rounds to stop being vacuous.** The original 4-tile fixture is
one winner per block, so no winner's own quad/glyph/anchor/fade source offset within its block is ever
non-zero — a Burst-only running-offset regression in that dimension is invisible to it. The final
`Gather_MatchesBuildOracle_FieldByField_MultiWinnerInterleavedBlocks` test (hand-built via `SymbolGatherPlan.Build`
directly — no store/`FilterActive`, full control over winner order and which raw slot is null) covers: block A
with 6 real winners (a null slot between the first two; two quad-bearing points; two glyph-AND-anchor-bearing
curved labels with a zero-glyph curved label sandwiched between them) plus block B's 2 winners (a zero-anchor
curved label + a point), 8 winners total, interleaved A,B,A,B,A,A,A,A.

Two vacuity rounds, both caught empirically rather than by inspection (`[[red-verify-against-injected-defect]]`):
1. **Round 1 (dev pass):** every point record's true source quad offset happened to be `0`, so D3's
   hardcoded-`0` defect was undetectable — fixed by adding a second quad-bearing point to block A (asserted:
   `blockA.PointQuadStart[blockA.Detail[…]] != 0`, a block-level precondition, not an oracle-derived UV proxy —
   the first fix used a UV-inequality proxy on the ORACLE's mirrored quads, which review flagged as indirect:
   deleting the first point would still pass while the real property went untested; replaced with the direct
   block-field assertion).
2. **Round 2 (review pass, R1 — the one BLOCKing finding):** every fixture in the repo, including round 1's
   widening, has AT MOST ONE curved label per block, so all three curved source-offset remaps
   (`CurvedGlyphStart`/`CurvedAnchorStart`/`CurvedAnchorFadeStart`) were always `0` and a hardcoded `0` for any
   of them was undetectable — the exact D3 class, just unaddressed for the curved arm. Fixed by giving block A
   TWO glyph-and-anchor-bearing curved winners with a zero-glyph one sandwiched between them, so the second
   real curved winner's three source starts are all genuinely non-zero (pinned as block-level preconditions,
   same shape as the point-arm fix).

**RED-verify — seven named defects across both rounds, actual failure text, quoted:**

| # | injected defect | predicted RED | actual result |
|---|---|---|---|
| D1 | `worldStart = 0` instead of `worldStart = mWorld` | plan claimed: needs the widened fixture | **REFUTED** — RED on the EXISTING (un-widened) fixture too: `Gather_MatchesBuildOracle_FieldByField` → `"WorldStart[1] 3 vs 0"`. `worldStart` is a mirror-wide cursor accumulated across ALL winners, not reset per block, so it diverges from the second record on already. Widened fixture also caught it (`"WorldStart[1] 1 vs 0"`). Two unrelated tests (`SymbolGatherPlanDropMaskTests.MaskedDrop_{Curved,Point}Only_…`) failed as collateral — corrupted world points cascade into fade-decay opacity reads (`"Expected: greater than 0.99000001f But was: 0.0f"`) — a missed remap is silent corruption, not a crash, confirmed live. |
| D2 | `fades += ac` instead of `fades += ac + 1` | fails on `AnchorFadeCount`/`CurvedAnchorFadeStart[i]`, and/or an out-of-range write | **Confirmed, as an out-of-range write**: `"System.IndexOutOfRangeException: Index 1 is out of range of '1' Length. This Exception was thrown from a job compiled with Burst"` — pass 1 undersizes `MFadeIds` by the trailing fallback slot; pass 2's unguarded fade copy then writes past it. 5 tests failed total, including collateral `IndexOutOfRangeException`s in `SymbolGatherPlanDropMaskTests`. |
| D3 | copy quads from source offset `0` instead of `block.PointQuadStart[detail]` | needs the widened fixture (a running offset within one block's own pool only differs from zero once ≥2 quad-bearing points share a block) | **Confirmed, widened-fixture-only**: `Gather_MatchesBuildOracle_FieldByField_MultiWinnerInterleavedBlocks` → `"Quads[2]"`; the original fixture stayed green (as predicted — it has no case where a point's true source offset is non-zero). |
| D4 | curved arm: `maxBoxes += glyphCount` instead of `+= placements * glyphCount` | fails on `MaxBoxes` | **Confirmed**: `Gather_MatchesBuildOracle_FieldByField` → `"MaxBoxes 7 vs 5"`. The widened fixture's curved records all happen to have `placements == 1` (zero-anchor curved labels), so `placements * glyphCount == glyphCount` there — indistinguishable; the ORIGINAL fixture's one curved label (`anchorCount = 1` ⇒ `placements = 2`) is what catches this one. |
| R1-glyph | curved arm: `glyphStartSrc = 0` instead of `block.CurvedGlyphStart[detail]` | fails on `Glyphs[i]`, widened-fixture-only (needs a block with 2 glyph-bearing curveds) | **Confirmed**: `Gather_MatchesBuildOracle_FieldByField_MultiWinnerInterleavedBlocks` → `"Glyphs[3]"`. Went undetected until round 2's fixture rewrite (see above) — every fixture before it had ≤1 curved label per block. |
| R1-anchor | curved arm: `anchorStartSrc = 0` instead of `block.CurvedAnchorStart[detail]` | fails on `Anchors[i]`, widened-fixture-only | **Confirmed**: same test → `"Anchors[1]"`. |
| R1-fade | curved arm: `fadeStartSrc = 0` instead of `block.CurvedAnchorFadeStart[detail]` | fails on `AnchorFadeIds[i]`, widened-fixture-only | **Confirmed**: same test → `"AnchorFadeIds[3]"`. |

Every defect reverted and the gate re-confirmed green (1713/1713) before landing.

**Gate:** EditMode **1713/1713** (1712 baseline + the new widened parity test), fresh XML confirmed by
start-time. `Tools/core-tests` **1076/1076**, unchanged (Core untouched — the fence held). CP2's Burst-compiled
check: `grep -iE 'Burst|BC[0-9]{4}|error' Logs/test-run.log` shows zero lines naming `SymbolGatherJob` —
`.Run()` did not silently fall back to managed. `MapRenderer.Jobs.asmdef`'s `"allowUnsafeCode": false` did NOT
need flipping — `UnsafeList<T>`'s public API (the `this[int]` indexer, the `(T*, int)` view constructor) is
callable from safe code; only `BuildBlockViews`'s pointer-taking (`Unity.Collections.LowLevel.Unsafe`, guarded
by `MapRenderer.Unity.asmdef`'s existing `"allowUnsafeCode": true`) is genuinely unsafe.

**§6.4 strengthening, applied:** `GatherIntoMirror_Warm_AllocatesNoGCMemory` now asserts a `MirrorRebuildCount`
delta of exactly 1 around the measured call, so it can never silently degrade into measuring a memo hit (its
own message's claim was previously unenforced).

**Review nits, applied:** `CopyView`'s `UnsafeList<T>` parameter dropped its `in` (the type isn't a readonly
struct, so `in` forced a per-element defensive copy in the hottest loop of the stage — the repo's
`in ⟺ readonly struct` gate); `BuildBlockViews` moved to below `GatherIntoMirror` so its own doc comment isn't
stranded reading as `GatherIntoMirror`'s; the "its caller's pass 1" wording (`LabelStageJob.cs` and this
section) corrected to "its own pass 1" — pass 1 is inside `SymbolGatherJob.Execute`, the caller computes
nothing.

**Perf verdict owed — NOT measured here (headless-gateable claim ends at "compiled and correct").** Per §10.4's
lesson, the verdict is a maintainer Play-mode profile covering BOTH a still camera and an active pan:
`Symbol.Gather` moving is predicted to drop from ~5.12 ms to ≤1.5 ms (Editor `AtomicSafetyHandle`-check
overhead erased by Burst); still-camera behaviour must be UNCHANGED (the memo still hits, unaffected by this
stage). Neither number is in this write-up — reported here only as the still-open gate step, exactly as R2/R3
recorded theirs.

### 10.10 OUTCOME — the epic closes; the async gather is deliberately NOT built (2026-07-25)

Maintainer Play-mode re-profile after R2 + R3 + the Burst gather:

| | before the epic | after |
|---|---|---|
| `MapRenderer.View.LateUpdate` | 19.36 ms | **10.34 ms** |
| still vs moving camera | 13 ms vs 19 ms | **~identical** |
| `Symbol.Gather` (memo-hit frame) | ~0.1 ms | 0.003 ms |

**The still/moving gap closing is the result, not the totals.** §10.4 predicted exactly this shape: the memo is
structurally dead under motion, the rebuild is all-or-nothing, therefore the remaining levers had to be
*motion-independent*. Bursting the rebuild is motion-independent, and the delta it was hiding behind vanished
rather than shrank. §10.4's reasoning is confirmed by its own prediction coming true.

**Reading the 0.003 ms honestly:** that is a **memo-hit** frame (R1's early-out — three `NativeArray<byte>.Copy`
mask memcpys and a subtraction), not a measurement of the Burst rebuild. A rebuild over ~35 k labels memcpys
hundreds of KB and cannot run in 3 µs. The evidence that rebuild frames are now cheap is the **aggregate**:
under motion the front swaps near-continuously (§10.4), so rebuild frames are certainly present in a moving
capture, and they no longer produce a visible delta. Do not record 0.003 ms as "the gather's cost".

**The async gather (§10.5) is closed WITHOUT being built.** This was decided against a pre-registered rule
stated before the profile was taken, not rationalised after it:

- Stage 1 (Burst, synchronous, `.Run()` inline on the main thread) took the gather to where it does not show
  up in the still-vs-moving comparison at all.
- Stage 2's remaining upside is therefore a fraction of a millisecond.
- Its cost is not: a third staleness generation on top of reconcile (1-4 frames) and mirror (1-2 frames),
  double-buffered `_gatherBlockViews` + `_gatherCounts`, pin accounting across an async boundary with **no
  safety handle** on the view table's pointers, and `ClassifyActive` having to classify the displayed set by
  construction rather than the newest front.

**The asynchrony was the expensive part of the design; the speed was the cheap part; the speed was
sufficient.** Splitting stage 1 out as byte-identical is what makes stopping here a finished state rather than
an abandoned one — nothing is half-migrated, and §10.5's design plus the four lifetime obligations in §10.9
remain on record should a future profile ever justify them.

**Remaining levers, re-ranked against the 10.34 ms that is left:**

| lever | ms | note |
|---|---|---|
| `Symbol.Emit` | ~3.0 | now the largest single item. An emit job is also what would finally justify moving `_fadeOpacity` / `_seenFade` / `_forceFadeOut` native — R2 correctly refused to move them without a Burst consumer in the same stage, and this is that consumer. |
| R4 — grid clear + `NodeUpperBoundByCandidates` | ~1 | low risk, no architecture change |
| `LabelCollisionJob` itself | 4.05 on a worker | serial by design; only matters if it ever exceeds one frame, since R3 removed the wait |

**Process record — five teeth that could not fail.** Three in R2 (one caught by each reviewer, one by the
RED-verify itself), R3's original T1 (which claimed to catch a defect it structurally could not — no headless
assertion can distinguish deferred from eagerly-completed), and the Burst gather's curved-arm source offsets
(every fixture in the repo has at most one curved label per block, so three remaps could be hardcoded to `0`
with the whole suite green). Each was caught by a *different* mechanism. The transferable rules are in §10.8's
closing note (check preconditions, not just expected values) and: **when a fixture is widened to make some
index non-trivial, check every arm that indexes the same way.**

**Estimates that were wrong, and how they were caught** — all three by measuring rather than arguing: R1's
"~50-80 % hit rate under motion" (really ≈0, §10.4); R3's headless-provable deferral (refuted by adversarial
review); and the assumption that this repo has committed golden-image baselines (it has none — "no re-bake"
means "no assertion loosened"). This is why the Burst stage shipped with a falsifiable prediction and a stated
consequence for missing it, rather than a claim.

### 10.11 The Emit follow-on — the fade map was unbounded, not the loop expensive (2026-07-25)

§10.10 ranked `Symbol.Emit` (~3.0 ms) the largest remaining item and proposed an **emit job** — the Burst
consumer that would finally justify moving `_fadeOpacity` / `_seenFade` / `_forceFadeOut` native. That job is
not what the numbers asked for.

**Step 1 — split the marker before designing against it.** `Symbol.Emit` was one marker over two costs with
different shapes: `EmitLoop` is per-CANDIDATE, `EmitDecay` is per-LIVE-FADE-IDENTITY. Measured z14-15:

| marker | ms |
|---|---|
| `Symbol.Emit` | 3.42 |
| ├─ `Symbol.EmitLoop` | 2.76 |
| └─ `Symbol.EmitDecay` | ~0.66 |

**Step 2 — get the N's.** `SymbolCollisionCandidates` **30 854** vs `SymbolPlacedQuads` **2 507**. The 12:1
ratio inverted the working assumption (that quads dominate the loop) and pointed at the per-candidate fade
lookups instead.

**The defect.** `EaseFade` stored every identity it computed, including the ones that had eased to 0.
`DecayUnseenFadeRecords` — the only collector — drops ids **not seen this frame**, and a staged candidate is
always seen. So every candidate that never places parked a permanent 0, and `_fadeOpacity` grew toward the
candidate count instead of the visible-label count. The whole sweep then walked ~30 k keys per frame to decay a
few hundred live ones. The floor rule that fixes it already existed just below, inside `DecayUnseenFadeRecords`
itself; it had simply never reached the hot path.

**The fix**, in `LabelPlacementSystem.EaseFade`:
- Drop rather than store when `next <= FadeEpsilon`. Both readers outside the loop (`MarkFadeOutIfAlive`,
  `TryForceFadeOut`) test `TryGetValue && > FadeEpsilon`, where absent and stored-zero are indistinguishable —
  so this is behaviour-preserving, not a semantic change.
- Guard it on `target <= current`, confining the drop to a fade-OUT. Without that, a fade-IN whose first step
  landed below epsilon would be dropped and restart from 0 every frame — a permanent invisible stall, not a
  transient one. Reachability is not only the >3300 fps corner (`step < 1e-3` ⟺ `dt < 3e-4 s` at
  `FadeDurationSeconds = 0.3`): production passes `Time.deltaTime`, which is **0 while paused**
  (`Time.timeScale == 0`), and a shown candidate at `current == 0` then computes `next == 0`. With the guard,
  that stores a legitimate sub-epsilon fade-in record instead — which is why the fade map's contract is
  "visible **or fading in**", not "> epsilon".
- **`_seenFade` moves inside `EaseFade`, into the store branch.** This is the part that is easy to get wrong:
  the `Add` was unconditional in the loop, so it survived the fix and left ~30 k `HashSet` inserts/frame in
  place — and the tempting follow-up, moving it below the `opacity <= FadeEpsilon` continue, *re-breaks the
  guard* by leaving a guarded sub-epsilon fade-in unmarked, so the sweep decays it straight back. The invariant
  that makes both correct is **seen ⟺ stored**, which is only enforceable where the store happens.

**Teeth.** `LabelFadeTests.Tick_StableCollisionLoser_LeavesNoFadeRecordBehind`, sited on the existing stable-loser
fixture because that fixture already establishes the precondition this needs — a candidate staged every frame
and placed by none. A bare count assertion would be the epic's sixth vacuous tooth, so it also asserts the
precondition (`LastCandidateCount == 2` while `LastQuadCount == 1`, i.e. the loser really is still staged) and
**stability across further ticks**, which rejects a periodic-clear implementation that a single sample accepts.
RED-verified against the real defect (unconditional store).

**Telemetry.** `SymbolLiveFadeRecords` (`LabelPlacementTelemetrySnapshot.LiveFadeRecordCount` → `MapTelemetryPanel`) —
genuine production instrumentation, not a test seam: it is the exact number whose absence hid this, and it sits
next to `SymbolCollisionCandidates` so the failure mode is legible at a glance (if it tracks candidates rather
than placed quads, invisible identities are being retained again).

**The counter must stay the RAW map size.** Review caught the first draft defining it as "identities with a
live (> epsilon) opacity" at all four doc sites — false (a fade-IN legitimately stores sub-epsilon), and worse,
a standing invitation: a maintainer taking it literally would "correct" the property to
`_fadeOpacity.Count(kv => kv.Value > FadeEpsilon)`, which **restores the full defect with the test still
green**. The raw count is the swept-entry count, i.e. the cost itself; that is what the tooth pins and why the
property carries an explicit do-not-filter note. Worth recording as a shape: *a comment can pre-install the
gate-gaming move that the test was written to prevent* — the tooth was sound and the prose around it was the
hazard.

**Calibration, recorded before re-measuring:** `EmitDecay` should mostly vanish — that sweep walks
`_fadeOpacity.Keys` verbatim. `EmitLoop` should move **less** than the entry-count drop suggests, because
`Dictionary.Remove` does not shrink the bucket array, so probe locality does not fully recover. Judge the
result against these, and do not design the next stage until they are in.

**Measured (maintainer, z14-15): `Emit` 3.42 → 1.58 ms, and `EmitDecay` is gone** (`Emit` and `EmitLoop` now
read identical). Against the calibration above: `EmitDecay` vanished as predicted; **`EmitLoop` 2.76 → 1.58 ms
beat the prediction** — the bucket-array caveat was real but over-weighted, and the map shrink helped the
lookups more than it suggested. Record the miss: the prediction was too pessimistic, not too optimistic.

**Second increment — the placed set was being re-probed per candidate.** With the fade map fixed, the residual
1.58 ms was still three hash lookups per candidate over 30 854 candidates. One of them was redundant:
`LabelStageJob` already resolves `Placed.Contains(FadeId)` in Burst and carries it on `LabelCandidate.
WasPlacedLastFrame`, and `HarvestCollision` — the set's sole writer — runs *before* the stage job, so the
contents cannot differ between them. The emit loop was recomputing a bool already in the struct it had loaded,
paying an `AtomicSafetyHandle.CheckRead` per probe in the Editor. Plus an early-out: a candidate neither placed
nor holding a fade record produces nothing (`EaseFade` eases 0→0, stores nothing, `_seenFade` untouched, caller
`continue`s), so skipping it is exactly equivalent. Per candidate **3 lookups → 1** on the ~28 k that produce
nothing, **→ 2** on the ~2.5 k that draw.

*The risk was the curved arm, and the RED-verify is why we know rather than believe.* `WasPlacedLastFrame` on
curved labels comes from a sliced array with a trailing centred-fallback slot — exactly where an off-by-one
hides, and the same blind spot that let three curved offsets be hardcoded to `0` earlier in this epic. The
pairing was verified by source trace (whole-range element-wise fill, identical slices, same index in
`StageCurved`), *and* by inverting the bit for curved candidates only: **13 failures, all curved-specific** — 5
`WorldCurvedAbRenderSnapshotTests` golden images, 6 `LabelPlacementStructureTests`, the curved-only drop-mask
parity test, the curved world-emit alloc test. Had that come back green it would have meant curved emit is
*untested*, not that the change is safe — run the targeted inversion, not the global one, precisely because the
global one tells you nothing about the arm you were worried about.

### 10.11.1 NEGATIVE RESULT — the grow-only index cache (landed `ce28dd33`, REVERTED)

With triage ruled out, the arithmetic *appeared* to localise the remaining 1.50 ms: `WorldLabelRenderer.Emit`
does **14 `NativeList.Add` per quad** (4 vertices, 4 opacity, 6 indices) = **35 098 per frame** at 2 507 quads;
at an assumed ~40 ns each under collections-checks that is ~1.4 ms, matching almost exactly. Six of the fourteen
looked free: the k-th quad always occupies vertices `4k..4k+3`, so its indices are `4k + {0,1,2, 0,2,3}` — a
pure function of the ordinal. `Slot.Indices` was made a grow-only high-water cache (not cleared per frame,
appended only for unseen ordinals, build slices the prefix); steady state appended **zero**. Gate 1715/1715,
teeth aimed at the growth boundary (5 → 2 → 8), RED-verified on the shrink leg.

**Maintainer re-profile: `EmitLoop` UNCHANGED at 1.50 ms. Reverted.**

The inference is tight and worth keeping: if deleting **43 % of the `Add` calls** changes nothing *in the
Editor* — where `ENABLE_UNITY_COLLECTIONS_CHECKS` makes each `Add` as expensive as it will ever be — then the
`Add`s were never the cost, and they matter even less in a player build. **The ~40 ns-per-`Add` estimate was
simply wrong**, and with it the whole "35 k container ops ≈ 1.4 ms" model that motivated this change.

Reverted rather than kept, because it was not free: it made the renderer permanently depend on *every quad
contributing exactly 4 vertices* (`vBase == 4 * ordinal`), so any later billboard-topology change would silently
corrupt every index past that point. **A standing constraint on future work traded for zero measured gain.**
Same disposition as D2/D3 in §9 — landed with a green gate and real teeth, came back flat, reverted, kept in
history as a documented negative result.

**What this leaves, by elimination rather than by modelling:** `BillboardMath.BuildWorldQuad` — four vertices
per quad, each with rotation, translate and tangent math, 2 507 times. Arithmetic on data that has to be
produced: genuine WORK, not overhead. That is the stopping line.

**Two wrong cost models in a row, both about this one loop** (~56 k hash lookups removed → 0.08 ms; 15 k of
35 k `Add`s removed → 0.00 ms). The transferable rule: **when a prediction about where time goes fails twice,
stop predicting and stop optimizing** — measure directly with a targeted experiment, or accept the cost. The
next honest datapoint here is a *build* profile, not another edit.

**The 12:1 ratio misled in BOTH directions — the ratio of items is not the ratio of work.** First it looked
like quads dominated; the candidate count said otherwise and drove the fade fix (right answer, right reason).
Then the probe reduction assumed candidates still dominated — and bought 0.08 ms. Both readings of the same two
numbers were defensible on the counts alone.

**And the follow-up correction was wrong too.** The obvious repair was "weight each count by its per-item cost"
— 2 507 quads × 14 container ops versus 30 854 candidates × one cheap branch — which is what motivated §10.11.1.
That model was *also* falsified: deleting 43 % of the container ops changed nothing. **Estimated per-item costs
were no more reliable than the raw counts.** Neither counting items nor pricing them from first principles
located this cost; only deleting a candidate cost and re-measuring did, and it took three attempts to run out of
wrong models. Treat a per-item cost estimate as a hypothesis to falsify cheaply, never as the arbiter.

**Read the Editor numbers with the checks in mind.** `ENABLE_UNITY_COLLECTIONS_CHECKS` is ON in Editor play
mode, so every `NativeArray`/`NativeHashSet` touch in this loop pays a safety check a player build does not.
The per-candidate probe costs are therefore over-represented in the 1.58 ms — which is an argument for
deleting redundant probes (free) and against building an emit *job* to chase what remains.

**Overhead-before-architecture, twice in one epic** — §10.10 closed the async gather because Burst
alone sufficed, and §10.11 replaces an emit *job* with a floor check. Both hot spots were accidental overhead
wearing the profile of real work. The general rule: **split the marker and get the N's before designing**; a
measurement can invalidate a stage before it is planned, and costs far less.
