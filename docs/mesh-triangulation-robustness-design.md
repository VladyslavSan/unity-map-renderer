# Mesh triangulation robustness — design (SSOT)

What keeps fill triangulation correct on polygons with many holes, and what keeps the globe subdivision that
follows it watertight: the invariants, the hole-elimination and failure-cascade design, the ear-scan index and
why it cannot change the answer, conforming subdivision, and the instruments that check all of it.

Many-hole polygons are the stress case — a water layer at z6–z9 is one large outer ring with dozens of island
holes. The water polygon paints over a land-coloured background there, so a triangulator that folds renders
islands as water and tears thin slits across the interior of a single tile.

Earcut runs in flat tile space, before projection, and is identical for every projection. The renderer
triangulates only with the Burst `EarcutJob`; there is no managed triangulator.

---

## 1. Invariants

For any valid (clean, correctly-classified) polygon-with-holes, the triangulation must satisfy:

1. **No garbage on failure.** Where ear detection cannot progress, the failure path drops the offending locus
   cleanly and **never emits an overlapping or inverted triangle**. `ForceClips` counts those clean drops.
   Giving up cleanly is acceptable; a fold is the bug.
2. **Area conservation.** Σ triangle area ≈ outerArea − Σ holeArea, within tolerance.
3. **Holes subtracted.** No triangle covers the interior of a hole.
4. **No spill.** No triangle covers area outside the outer ring.
5. **Winding consistency.** All emitted triangles share orientation. A flipped triangle is a fold; this is
   what catches a thin sliver that area conservation alone cannot.

## 2. Hole elimination — the bridges must not cross

The core ear-clip is sound on a single ring. What fails on many-hole polygons is **hole elimination**: each
hole is bridged into the outer ring by splicing a zero-width seam, and the merged ring is ear-clipped. The
failure is a function of the **set** of holes bridged together, not of any one hole: every hole is fine alone,
and the failure count grows with hole count and fluctuates with the combination. A later bridge that crosses
an earlier seam tangles the merged ring into a self-intersecting one, ear detection stalls, and any path that
forces progress by clipping a non-ear triangle emits folds.

There is **no single bad hole to repair**, so repairing an offending hole is rejected. The design makes hole
elimination itself scale:

- **Holes are eliminated one at a time, in a deterministic order** — leftmost x, then min y, then ring index
  (`FillMeshPipeline.HoleRingComparer`) — each against the already-merged ring.
- **Each bridge is validated non-crossing.** `FindBridgeVertex` picks a candidate, and `LocallyInside` +
  `SectorContainsSector` check it against both the merged ring and the hole's own edges, falling back to
  another candidate when it fails. The merged ring therefore stays simple on clean input.

This is the published ear-clipping-with-holes technique; `THIRD-PARTY-NOTICES.txt` records its provenance.

## 3. The failure cascade

When ear detection stalls, `EarcutJob` runs a cascade that degrades correctly and never folds:
`CureLocalIntersections` → `SplitPolygon` (via `TrySplit`) → clean drop. Every triangle the cascade emits
passes `LocallyInside`/`IsValidDiagonal`; the only alternative is a counted clean drop.

- **Explicit stack.** Burst does not reliably support recursion, so the split recursion is an explicit
  stack of pending ring-jobs. `TrySplit` pushes the second half, then the first, so the first half — including
  its own nested splits — finishes before the second starts, as a recursive call would.
- **Bounded headroom.** The working buffers are pre-sized to the merged-ring size plus a split headroom of
  `2 × min(MaxSplits, max(8, holeCount × 4))` vertices (`SizingJob`). `TrySplit` checks remaining capacity
  before it writes a split's two new vertices and refuses the split when the headroom is exhausted, so the
  caller falls through to a clean drop. `OutMergedVertexCount` reports the count used, which is usually less
  than the capacity.

**Limitations.**

- **The split path is not exercised by any committed test.** It is memory-safe (the capacity check refuses
  before any write) and cannot fold (every cascade triangle passes the diagonal checks), but nothing fires it.
  A test that does must craft a self-intersecting input that provably splits; a water-tile check passes
  without touching the path.
- **A headroom-exhaustion drop and a genuine-degeneracy drop are indistinguishable** — both increment
  `ForceClips`.
- **Adversarial synthetic polygons keep a small overlap tail.** Adversarial star polygons show no winding
  flips, but a small fraction overlap by more than 1 % of their area (overlap, not fold; invisible for an
  opaque fill). It is not observed on real tiles. The suspected cause: `IsEar` skips bridge-copy vertices in
  its containment test, which can admit an ear that straddles a zero-width bridge seam. Tightening that is the
  principled way to drive the tail toward zero.
- **A reversed-concave residual** could in principle overlap with `ForceClips == 0` and no winding flip. The
  non-crossing bridge keeps a clean input's merged ring simple, so it is not reachable on real data; a unit
  test pins the case (`Unit_ReversedConcaveQuad_NoFold_AreaConserved`).

The level this design operates at is **correct on real data, plus bounded, counted degradation** on
adversarial input — not overlap-free output on every input.

## 4. The ear-scan index — why it cannot change the answer

Correctness and cost are separate concerns. Without an index, every ear test scans every vertex of the merged
ring. The index changes which vertices an ear test visits; it must never change the verdict.

**The degenerate branch.** On a degenerate (zero-area) candidate triangle, `PointInTriangle` uses an explicit
bounding-box test rather than the bare cross-product sign check, which would report a distant collinear vertex
as contained. The branch is safe at any coordinate precision, not just on integer tile coordinates (the clip
stage introduces fractional ones — `RingClipJob.Intersect`): a triangle's point set is always a subset of its
own bounding box, so the branch can only turn a spurious `true` into a correct `false`; it never discards a
genuine containment. This branch is the precondition for the index being answer-preserving.

**The index.** `EarcutJob.EarGrid` is a uniform bucket grid (CSR layout, counting sort) built once per polygon
over the merged ring (`BuildEarGrid`). `ComputeIsEar` walks only the cells that overlap the candidate
triangle's bounding box, and falls back to the linear scan when that box spans too many cells. Vertices added
by a split, after the grid is built, go to a linear `Overflow` list that every ear test scans.

**Why nothing is skipped.** `PointInTriangle(A,B,C,P) == true ⇒ P ∈ AABB{A,B,C}` on both branches: the
degenerate branch *is* the box test, and the cross-product branch can only accept a P inside the triangle's
convex hull, which is a subset of that same box. So any vertex that could block an ear lies in the candidate
triangle's own box. `EarGrid.CellX`/`CellY` are one monotone function, used identically at build time and at
query time, so a vertex at x ∈ [triMinX, triMaxX] always maps to a cell in [CellX(triMinX), CellX(triMaxX)] —
the range `ComputeIsEar` walks — and likewise for y. The cell walk therefore visits every base vertex the box
could contain; split-added vertices are covered by the `Overflow` scan, and the wide-box fallback (or the
test-only `ForceLinearEarScan`) only widens the visited set to everything.

## 5. Globe subdivision must be conforming

On a curved projection the straight triangle edges chord through the sphere, so `GlobeFillSubdivideJob`
refines each earcut triangle after projection. It is the only globe-only stage, so a globe-only crack is a
subdivision defect, never an earcut one.

**Non-conforming refinement cracks.** If a triangle splits (inserting midpoints on its edges) while its
neighbour across a shared edge does not, the neighbour keeps that edge as a straight chord. In flat tile space
the inserted midpoint is collinear and invisible, which is why Mercator shows nothing; on the sphere the
midpoint bulges off the chord and opens a **T-junction gap**. The gap grows with the shared edge's subtended
angle, so the largest gaps fall on earcut's longest edges — the coast→island bridge diagonals.

**Per-edge marking.** An edge is marked iff its endpoints' great-circle angle exceeds the target (3°,
`GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad`). A mark is a function of the edge's two endpoints alone, so two
triangles sharing an edge always compute the same mark: conforming without connectivity. Each triangle then
takes one of the templates keyed by its mark count — 0: emit; 1: bisect; 2: the 1→3 split with the **shorter**
interior diagonal; 3: the 1→4 split — and recurses. Interior diagonals are private to their parent, so the
mesh is T-junction-free at every depth. On Mercator the up vector is constant, no edge is ever marked, and
the stage is a byte-identical pass-through.

**Rejected alternatives.**

- **Uniform per-tile depth** — one depth for the whole tile from its maximum edge curvature. Conforming, but it
  reintroduces the low-zoom explosion adaptivity exists to avoid (a z0–z2 triangle spans much of the globe).
- **A T-junction stitching post-pass** — insert each T-junction midpoint into the offending straight edge
  after adaptive subdivision. A bolt-on, and it can cascade.

**Bounds.** `MaxDepth` (5) and two per-tile vertex budgets (`InteriorBudget`, `TotalBudget`) keep a whole-globe
z0 tile from exploding.

**Limitation (no test observes it).** A forced stop can still leave a T-junction. The `MaxDepth` cap or either
budget makes ONE triangle emit flat whatever its own marks say, including a still-marked edge shared with a
neighbour that has not been forced to stop. On a tile with non-uniform curvature the two sides reach the cap
at different times. The committed fixtures are uniformly curved, so every triangle there reaches its stop test
in lockstep and the gap never appears.

**Measure severity, not count.** A subdivision check gates on the render-space gap **magnitude** (visible vs
sub-pixel), not on the T-junction count. The count is large and meaningless: almost all T-junctions are
sub-metre and invisible, and a handful of kilometre-scale ones are the whole visible defect.

## 6. Validation instruments

- **`MeshCoverageValidator`** (EditMode, test side) checks an already-triangulated result against its
  ground-truth rings in flat tile space: `ForceClips`, winding flips, area relative error, and a rasterised
  coverage diff against the even-odd source fill (missing cells = phantom holes, extra cells = spill).
  Tolerances are the caller's (`areaEps`, `mismatchEps`). It rasterises because a folded sliver has ~zero area
  but is visible. Winding flips use one global majority sign, because every outer ring is normalised to one
  winding, so a flip anywhere is a fold. **`ForceClips` is the only instrument that sees a drop smaller than
  one raster cell**, so a corpus test pins that count exactly rather than as an inequality.
- **`SubdivisionCoverageValidator`** checks the subdivided globe mesh against the flat triangulation: no
  coverage hole, no flipped or degenerate sub-triangle, and T-junction cracks measured as render-space gap
  magnitude.
- **A real-tile corpus** — many-hole water tiles (`water-8-135-80`, `water-6-32-20`) and a panel of
  coastline/archipelago tiles. Real data exposes these defects; synthetic stand-ins do not. Adversarial
  synthetic stars exercise the no-fold invariant beyond the corpus.

**Coverage shape.** Correctness is *property* coverage against geometric ground truth (`PolygonAssembler` +
signed area + even-odd rasterisation) over the Burst `EarcutJob`'s own output. There is no second,
independently implemented triangulator to compare against: property coverage is independent of the
triangulator under test but weaker than a bit-identity check. The earcut tests drive `NativeArray`/Burst, so
they cannot compile in the `Tools/core-tests` fast loop (no shim may bridge that — see that project's own
doc); they run in the Unity EditMode gate.

**`TriangulationBuffers.HoleCountOffsets` is capacity, `PolyHoleCount` is truth.** `HoleCountOffsets[pi+1]
- HoleCountOffsets[pi]` is the sizing pass's per-polygon CAPACITY — padded to 1 even for a zero-hole
polygon, to match `EarcutJob.SortedHoleCounts`'s own length-1-when-empty contract. It is not that
polygon's real hole count; `PolyHoleCount[pi]` is. Reconstructing a polygon's holes from the offsets
column alone silently manufactures one zero-vertex phantom hole per zero-hole polygon.

## 7. Out of scope and open

- **T-junction seams between adjacent tiles.** `GlobeFillSubdivideJob` refines each tile's boundary edges
  independently — a separate globe-fill watertightness issue, for skirts or conforming boundary tessellation.
- **z0 earcut slivers.** Zero-area bridge slits and antimeridian needle slivers appear at z0. They paint
  nothing, and the subdivision-quality metrics exclude them; an earcut-side antimeridian fix is open.
- **Input sanitization** for genuinely self-intersecting source rings is not built. On such input the failure
  cascade must still not fold.
