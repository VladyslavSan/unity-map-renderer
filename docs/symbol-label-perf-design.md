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
