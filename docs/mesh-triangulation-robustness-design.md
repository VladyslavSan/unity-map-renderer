# Mesh triangulation robustness — design (SSOT)

**Status:** epic, in progress on `feat/mesh-triangulation-robustness`.
**Owner doc:** this is the single source of truth — decisions, stage sequence, and open findings live here.

Fill polygons with many holes (water, at z6–z9 especially) are triangulated **wrong**: islands get filled
in as water, and thin folded slivers tear across the interior. This doc records the measured evidence, the
localized root cause, the validation testbench that guards the fix, and the staged fix plan.

---

## 1. Symptom (as observed)

On the globe at z≈6–9, large water bodies render broken:
- Parts of islands (e.g. England, the Danish isles) **disappear** — the water polygon fills over them, so
  land reads as water. Roads (lines) on the same ground render correctly → "roads floating in water."
- Thin **diagonal slits** cut across the interior of a single tile — *not* at any tile boundary. What looked
  at first like inter-tile seams are also this: bad geometry *inside* one tile.
- The artifacts **blink** as you zoom 6↔9. This is not a runtime cull — it is per-zoom-level **tile
  swapping**: crossing zoom levels swaps tilesets, and the tiles whose fill is mis-generated simply aren't
  drawn correctly, while the ones that happen to triangulate cleanly are.

The rendering at these zooms is **inverted**: we paint the *water* polygon over a land-colored background,
so a broken water mesh directly corrupts the land/water boundary.

## 2. Root cause (measured + localized)

Reproduced headless over two real OpenFreeMap `water`-layer tiles (`Tools/core-tests`, ~1 s, no Editor).
Both are one large outer ring with many island holes:

| Tile | outer verts | holes | earcut force-clips | area error | raster coverage error |
|------|-------------|-------|--------------------|-----------|-----------------------|
| z8/135/80 | 2571 | 50 | **54** | **+91%** | 20% (water spilled onto land) |
| z6/32/20  | 879  | 13 | **5**  | +0.79% | thin sliver (near-zero area — invisible to an area check) |

The `Earcut.Result.ForceClips` counter is the canary: **non-zero ⇒ the triangulator gave up and emitted
geometrically invalid triangles.**

Four discriminating checks on each tile's worst polygon (`WaterRepro.DeepDive_WorstWaterPolygon`) localize
the defect to a single stage:

| Check | Result | Conclusion |
|-------|--------|------------|
| (A) Input self-intersection — outer + every hole | outer clean; **0/50** and **0/13** holes self-intersect | **Input is clean** — not a sanitization problem |
| (B) Assembler classification — each hole inside outer? | **50/50** and **13/13** holes correctly inside outer | **`PolygonAssembler` is correct** — no mis-nesting, no dropped/inverted rings |
| (C) Outer ring triangulated ALONE (no holes) | **0 force-clips, 0.00% area error** | **The core ear-clip is fine** on a single ring |
| (D) Same outer WITH its holes | **54 / 5 force-clips, +91% / +0.79% area** | **Hole handling is the whole defect** |

**Root cause:** `Earcut.Triangulate` bridges each hole into the outer ring by splicing a zero-width seam
(`FindBridgeVertex` + the `copyHoleLM`/`copyOuter` slit), then ear-clips the merged ring. With many holes
the merged ring's seams interfere and ear detection **stalls**; the stall-guard (`Earcut.cs` ~L249-284)
then **force-clips a non-ear triangle** to make progress. Those force-clipped triangles fold and overlap
(z8's mesh area is ~1.9× the *outer ring's own* area — gross overlap, not merely unsubtracted holes), which
renders as filled islands and torn slits. The exact interference mode (later bridge crossing an earlier
seam vs. `IsEar` bridge-copy skipping breaking down at scale) is the **first fix-stage's investigation** —
it is not needed to know the stage.

Two independent mechanisms, same stage:
- **Overlap/fold** (z8, dominant) — many holes → many force-clips → folded triangles fill the holes.
- **Thin sliver** (z6, minor) — a few force-clips → a near-zero-area inverted triangle → a visible slit.

### 2.1 Robustness vs performance — keep separate

The **correctness** bug is *force-clip emits garbage*. The triangulator's **O(n³)** cost (`IsEar` scans all
vertices, per clip) is an **independent** performance concern. They must not be conflated: the fix for the
visible bug is "on failure, degrade correctly (never emit a fold)"; z-order/hashed acceleration is a
separate, optional perf item that changes no output. This doc scopes **robustness first**; perf is deferred
(§6).

## 3. Invariants the fix must hold

For any valid (clean, correctly-classified) polygon-with-holes, the triangulation must satisfy:

1. **No garbage on failure.** `ForceClips == 0`, OR — if a genuinely degenerate input is hit — the failure
   path degrades *correctly* (drop/repair the offending locus) and **never emits an overlapping or inverted
   triangle**. Emitting a fold is the bug; giving up cleanly is acceptable, folding is not.
2. **Area conservation.** Σ triangle area ≈ outerArea − Σ holeArea, within tolerance.
3. **Holes subtracted.** No triangle covers the interior of a hole.
4. **No spill.** No triangle covers area outside the outer ring.
5. **Winding consistency.** All emitted triangles share orientation (a flipped triangle is a fold — this is
   what makes thin slivers that area conservation alone can't catch).

## 4. Testbench (the RED teeth)

A durable, reusable mesh-validation harness — the "identify weird holes made mid-pipeline" tool. Pure
geometry (tile-space `double2`), engine-free, runs in the ~1 s fast `Tools/core-tests` loop.

**Validator** (proposed `MeshCoverageValidator`, Core `Imaging`/`Geometry`, alongside the existing
`SnapshotCoverage`): given assembled `Polygon`s and an `Earcut.Result`, reports and asserts invariants §3:
- area expected vs actual + relative error;
- rasterized coverage diff (even-odd over outer+holes = ground truth, nesting-agnostic) → **missing cells
  (phantom holes)**, **extra cells (spill)**;
- force-clip count;
- winding consistency.

**Corpus:** commit the two real pathological tiles as fixtures next to the existing
`Assets/Fixtures/boundary-*.pbf.bytes` (`water-8-135-80.pbf.bytes`, `water-6-32-20.pbf.bytes`, ~170 KB each)
— real data is what exposed this; synthetic stand-ins would not.

**Green-gate hygiene:** the bench asserts clean on good geometry (guards regressions). The two corpus tiles
are **RED against today's earcut**, so their hard assertions land RED-first but **`[Explicit]`/ignored with
a tracking note**, keeping the normal gate green while the reproduction is one flag away. Removing the
ignore is the acceptance gate for the fix stages — flip to always-on green when the fix lands.

**Optional later:** a thin Unity EditMode wrapper drives the *jobified* path (`FillMeshPipeline` +
`GlobeFillSubdivideJob`) over the same corpus, so the full engine pipeline is covered, not just managed
Core. Deferred behind the pure-Core bench (the bug is pre-projection, so Core covers it).

The throwaway `Tools/core-tests/WaterRepro.cs` + the fetched `.raw` tiles are the seed; they get
productionized into the validator + corpus and then removed.

## 5. Fix approach (LOCKED — direction W: rewrite hole elimination)

The core ear-clip is sound (check C); only hole handling fails. **Stage 1 bisection locks the direction.**

**Stage 1 evidence** (z8/135/80 worst polygon, outer=2571, 50 holes, 54 force-clips):
- **Every hole is individually fine** — outer + any *single* hole force-clips **0/50** times.
- **Force-clips scale with hole COUNT and fluctuate with the combination** — cumulative outer+holes[0..k]:
  k=3→6, k=6→29, k=13→35, … and adding some holes *reduces* the count (k=29: 53→52). The failure is a
  function of the *set* of holes bridged together, not any one hole.

⇒ **Bridge interference.** Bridging many holes into one merged ring (`FindBridgeVertex` + sequential splice)
tangles it into a self-intersecting ring that stalls ear-detection; the stall-guard then force-clips folds.
There is **no localized hole to repair** — so **(R) Repair is rejected**. The hole-elimination step itself
does not scale.

**Decision: (W) Rewrite hole elimination** following the proven mapbox/earcut *algorithm* (clean-room — it
is a published algorithm; ISC-licensed reference, compatible with this repo's no-copyleft rule), keeping the
sound core ear-clip:
- **Robust hole elimination** — a `findHoleBridge` that provably picks a **non-crossing** bridge from each
  hole (sort holes by leftmost x; eliminate one at a time against the already-merged ring), so the merged
  ring stays simple.
- **A failure cascade** — `cureLocalIntersections` → `splitPolygon` → retry — that on ear-detection failure
  **degrades correctly and NEVER emits an overlapping/inverted triangle**. This is the invariant that fixes
  the visible bug; the force-clip-emits-a-fold path is deleted.

## 6. Stage sequence

0. **Testbench + corpus (this branch, first).** Productionize the validator (§4), commit the two corpus
   tiles, land the RED-first `[Explicit]` assertions. *No production change.* Green gate stays green.
1. **Bridging root-cause + approach lock.** ✅ DONE — bisection proved bridge interference (no single bad
   hole; force-clips scale with hole-set); **direction W locked** (§5).
2. **Fix hole handling.** ✅ DONE (managed `Earcut.cs`). A **validate-and-fallback provably non-crossing**
   bridge selection (`LocallyInside` + `SectorContainsSector`, checking the merged ring and each hole's own
   edges) keeps the merged ring simple; the force-clip-fold path is deleted and replaced by a
   `CureLocalIntersections → SplitPolygon → clean-drop` cascade that never folds (the reactive mirror-retry
   an earlier iteration tried was removed — the non-crossing bridge makes it unnecessary). Corpus `[Explicit]`
   assertions flipped to always-on GREEN. **Managed-vs-Burst note:** the renderer uses only the Burst
   `EarcutJob`; managed `Earcut` is test-only, so this stage moves **zero rendered pixels**. `countries`
   had 1 managed force-clip → 0, so its index hash moved → `JobifiedPipelineTests.…MatchManagedPath` is
   `[Explicit]`-deferred to Stage 3 (managed fixed ahead of Burst; re-greened when Stage 3 ports it).
3. **Jobified-path parity — THE VISIBLE FIX.** ✅ DONE (`4137f15a`). Ported into Burst `EarcutJob` via an
   explicit-stack DFS (push c-then-a = managed's a-before-c order); scratch pre-sized
   `baseCap + 2*min(MaxSplits, max(8, holeCount*4))` with `TrySplit` refusing (drop-clean) before any
   overflow; `OutMergedVertexCount` keeps `MatchManagedPath`'s hash bit-identical. `MatchManagedPath`
   un-`[Explicit]`'d and GREEN (managed==Burst on countries, resolved by cure not split). New
   `JobifiedWaterTriangulationTests` drives the real `FillMeshPipeline` over `water-8-135-80` and validates
   no-folds/area via `MeshCoverageValidator.ValidateTriangulation`. No GPU snapshot renders water → no
   re-bake needed. `run-tests.sh` 1490/1490 green. Dual-reviewed (Opus APPROVE + Codex no-OOB).

**EPIC COMPLETE (S0–S3), branch `feat/mesh-triangulation-robustness` unpushed. Maintainer confirmed the
missing-land / islands-filled-as-water artefact is FIXED on the globe.** Each stage is one revertible commit.

## 6.2 NEXT EPIC (separate) — globe fill SUBDIVISION artefact (NOT earcut)

Maintainer isolated a *distinct* remaining artefact: a thin **diagonal "hole" in the middle of the ocean**,
inside a single tile (e.g. **tile 6/32/20, water layer**), **present ONLY in globe view — Mercator is clean**
(the Mercator mesh outline shows no gap there). Since earcut runs in flat tile space *before* projection and
is identical for both, a globe-only artefact **cannot be earcut** — it is `GlobeFillSubdivideJob` (the 3°
adaptive subdivision, the only globe-only stage).

**Root cause CONFIRMED + MEASURED (managed mirror of the job over `water-6-32-20`, tile-space T-junction
detection + render-space gap magnitude):**

| Signal | Value | Conclusion |
|--------|-------|-----------|
| `maxDepthReached` | **1** (of MaxDepth 5) | shallow — only ~11 of 1676 earcut triangles curve enough to split |
| `budgetFired` | **False** (Budget 200 000 untouched) | the Budget/MaxDepth hard-cutoff mechanism is **RULED OUT** |
| worst render-space gap | **3612 m = 1.03% of the z6 tile span** (352 km) | a supra-pixel, visible crack — the diagonal the maintainer saw |
| gap distribution | 18 sub-metre (invisible) + 2 mid + **3 in the 1–10 km bucket** | **severity, not count** — 3 big gaps are the artefact; the T-junction *tally* (23) is meaningless |

**Mechanism (general — subsumes the earlier "bridge-slit" hypothesis):** the adaptive 1→4 refinement is
**non-conforming**. A triangle splits (inserting a midpoint on *all three* edges) when any one of its edges
subtends > 3°; its neighbour across a shared edge may be all-flat and **not** split, keeping that edge a
straight chord. In flat tile space the inserted midpoint is exactly collinear (invisible — why Mercator is
clean); on the sphere the midpoint bulges off the chord → a **T-junction gap**. The gap size scales with the
shared edge's subtended angle, so the *largest* gaps fall on earcut's *longest* edges — the coast→island
bridge diagonals. The "bridge-slit" is therefore just the **maximal-asymmetry instance** of the general
depth-mismatch, not a separate cause. (Measured by `Tools/core-tests/SubdivRepro.cs` — throwaway seed.)

**STATUS: RESOLVED (candidate A landed, commit `f566ad23` on `feat/globe-fill-subdivision`).** The Burst
`GlobeFillSubdivideJob` now does **edge-conforming red-green refinement**: each edge is marked iff its
endpoints' great-circle angle exceeds the 3° target (a function of the edge alone, so two triangles sharing
it always agree — conforming without connectivity), split by mark-count via 3 templates (1→bisect, 2→1→3
with the **shorter** interior diagonal, 3→1→4), recursing; interior diagonals are parent-private ⇒
T-junction-free at every depth. Mercator stays a byte-identical pass-through (constant up ⇒ 0 marks). Both
RED-first teeth flipped GREEN (z6 corpus + z2 depth-5 quad, maxGap 0.0m) + a z0 real-tile non-uniform tooth
(rendered-geometry gap 0.0002%, sub-visible). Full gate 1500/1500. **Investigation note:** the z0 "residual"
during the fix was phantom — zero-area earcut **bridge slits** + antimeridian earcut **needle slivers**
(0.08% of z0 fill area), an *earcut* pathology (excluded from the subdivision-quality metrics, they paint
nothing); a possible earcut-side antimeridian fix is a separate future epic.

**Fix-direction candidates (considered; A chosen):**
- **(A) Edge-conforming refinement** — subdivide each edge by a count that is a function of its two
  endpoints *alone* (great-circle angle between their ups), so both triangles sharing it agree → conforming
  by construction, still adaptive. Fill each triangle's interior respecting its (possibly unequal) per-edge
  sample counts (red-green / template tessellation). *Most work, truly conforming, keeps adaptivity.*
- **(B) Uniform per-tile depth** — one depth for the whole tile from its max edge curvature; conforming by
  construction (all edges split identically in tile space). *Simplest, but reintroduces the low-zoom
  (z0–2 whole-globe triangle) explosion adaptivity exists to avoid — Budget-bounded but risky.*
- **(C) T-junction stitching post-pass** — after adaptive subdivision, insert each T-junction midpoint into
  the offending straight edge (fan the unsplit triangle). *Bolt-on; can cascade.*

**Wanted (maintainer):** a **separate test suite for the subdivision** (analogous to the earcut testbench) —
validate the subdivided globe mesh is watertight / introduces no coverage hole vs the flat triangulation, no
flipped/degenerate sub-triangles, and no T-junction cracks. **Gate on render-space gap magnitude (visible vs
sub-pixel), NOT T-junction count** (the earcut epic's "severity, not count" lesson — the count is large and
meaningless by construction). `GlobeFillSubdivideJob` is Burst (Unity-only), but its logic mirrors managed
via the engine-free `SphericalProjection` (in `core-tests`) for a fast first harness (the mirror iterates;
it is NOT the acceptance tooth) — **source-of-truth is a Unity EditMode test over the REAL Burst job** on
`water-6-32-20` (verify the mirror's gap numbers match the real job first). **Invariant to protect: Mercator
stays a pass-through no-op** (constant up → never subdivides; byte-identical, matching the earcut epic's
"zero pixels moved on Mercator" discipline). Corpus seed: `water-6-32-20.pbf.bytes` (already committed).
This is a NEW epic — supersedes the "Globe fill T-junction seams" fence in §7.

### 6.1 Stage-2 acceptance bar (measured) + hardening backlog

**Acceptance bar = "correct on real data + bounded, surfaced degradation"** (the level mapbox/earcut itself
operates at — it does not guarantee overlap-free output on all inputs; it degrades and reports a deviation).
Concretely, measured on the committed corpus + a fetched panel of coastline-dense tiles:
- **Real tiles are clean or graceful.** 6 coastline/archipelago tiles (Aegean, Norway fjords/150 polys,
  Palawan, Stockholm arch./28k tris, Croatia, Raja Ampat): 4 perfectly clean (ForceClips=0, WindingFlips=0,
  area 0.00%); 2 with a **single surfaced clean drop** (ForceClips=1, area <0.8%, no fold). No folds, no
  islands-as-water. These are committed as corpus teeth (4 strict, 2 graceful-bound).
- **No folds anywhere.** 60 000 adversarial synthetic star polygons: **WindingFlips=0 across all** (no
  inversions). Only 0.45% exceed 1% exact-area error (bounded overlaps, invisible for opaque fill; max
  12.82% on 2 spiky stars). `ForceClips` surfaces genuine clean drops.

**Hardening backlog (NOT blocking Stage 2 — quantified, deferred):**
- **Synthetic-star overlap tail** — 0.45% of adversarial clean stars overlap >1% (max 12.82%), all
  overlap-not-fold, `WindingFlips=0`. Not observed on real tiles. A dedicated hardening pass could drive
  this toward zero.
- **Root cause suspect (advisor):** `IsEar` skips `isBridgeCopy` vertices in its point-in-triangle test,
  which can admit an ear straddling a zero-width bridge seam → the residual overlap on the synthetic tail.
  Tightening this is the principled follow-up for driving the synthetic tail toward zero; do it in/after
  Stage 3, guarded by the same testbench.
- Codex-review note (kept as a permanent unit tooth, `Unit_ReversedConcaveQuad_…`): a reversed-*concave*
  residual could in principle emit overlap with `ForceClips=0`/`WindingFlips=0`. The non-crossing bridge
  keeps clean input's merged ring simple so this is not reachable on the real corpus; tracked by the tail
  backlog above.
- **Stage-3 Burst split-cascade is empirically UNEXERCISED (recorded, deferred).** The Burst `EarcutJob`
  port is bit-identical to managed on the *bridge* path (`MatchManagedPath` green on `countries`, which the
  fix resolves via `CureLocalIntersections` — no split), and the jobified water tooth passes with
  `ForceClips=0`. But no committed test fires the `TrySplit`/`SplitPolygon`/explicit-stack-DFS path, so its
  managed↔Burst bit-identity is unverified. It ships accepted because it is **memory-safe** (the
  `total+2 > Vx.Length` guard refuses before any write — Opus + orchestrator both traced it) and
  **cannot-fold by construction** (every cascade triangle passes `LocallyInside`/`IsValidDiagonal`; the only
  alternative is a counted clean drop). The Burst headroom (`min(512, max(8, holeCount*4))*2`) intentionally
  diverges from managed's grow-to-512 on exhaustion — both degrade to bounded, surfaced clean-drop, never
  garbage. The proper future tooth (with the synthetic-star hardening): a crafted self-intersecting input
  that provably fires a split (`splitCount>0`) AND asserts `managed == Burst` — a plain water-tile hash check
  is insufficient (it passes without touching the path). Minor observability follow-up: a
  headroom-exhaustion drop is currently indistinguishable from a genuine-degeneracy drop (both `ForceClips++`).

## 7. Scope fences (out of this epic)

- **Globe fill T-junction seams** between adjacently-subdivided tiles (`GlobeFillSubdivideJob` refines each
  tile's boundary edges independently) — a *separate* globe-fill watertightness issue; skirts or conforming
  boundary tessellation. Tracked separately, not here.
- **Triangulator performance** (O(n³) → z-order/hashed ear search) — §2.1; correctness-neutral; deferred.
- **Curved-line / symbol tile-bounds clipping** — unrelated prior work.
- **Input sanitization** for genuinely self-intersecting source rings — not needed for these tiles (check A
  clean); if a future corpus tile has dirty input, the correct-degradation path (§3.1) must still not fold.

## 8. Open questions

- ~~R vs W~~ — **resolved (W), §5.**
- Winding-consistency tolerance and the exact area-conservation ε for the validator (set from the clean
  corpus baseline).
- Whether the Burst path shares the managed fix verbatim or needs its own port (stage 3).
