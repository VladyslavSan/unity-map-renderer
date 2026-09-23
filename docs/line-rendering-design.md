# Line rendering — width, joins and caps

## The width model

**`line-width: N px` means the line is N px wide viewed top-down. That is the entire specification.**

The styled pixel width fixes a **world** half-width once — `N × metresPerDevicePixel at the current zoom` —
and the perspective divide does the rest. At pitch 0 every ground point shares a depth, so the line renders
N px everywhere. Under pitch, that same world width renders **wider than N near the camera and thinner than N
far away**. Nothing is measured per vertex. Nothing compensates.

**Reference measurement.** Against the reference renderer at its maximum pitch (more conservative than the
tilt this renderer permits): **~54 px near the camera, ~16 px at the horizon** — a ratio of **~3.375**. It is
bounded, not trending to zero: it is the depth ratio across the frame, which is the signature of a constant
world width. *(Black-box observation of rendered output.)*

In `Line_VertexExtrude.hlsl`:

```hlsl
float pxToWorld = (_WidthIsPixels > 0.5)
    ? ((_MapFrameMetersPerDevicePixel > 1e-9) ? _MapFrameMetersPerDevicePixel : metresPerDevicePx)
    : 1.0;
```

`_MapFrameMetersPerDevicePixel` is a per-frame global — one number with no depth term, no direction and no
sign, identical at every vertex, so any interpolant of it is exact. The width, `line-gap-width`,
`line-offset` and the dash divisor all read it.

The `> 1e-9` branch is a **missing-push fail-safe, not part of the model** — see "Where the constant comes
from".

**The legitimate per-vertex exceptions are the SAMPLING-GRID quantities**, and there are three: the **AA
straddle pad**, the **min-width floor** (`minHalfWorld` — a sub-pixel road must be rescued to ~1 device px
*where it thinned*, not where the look-at is), and the `_HAIRLINE_SOLID_CORE` floor, which derives from the
pad. Half a device pixel genuinely *is* a screen quantity at that vertex, in both width modes, so all three
read the per-vertex `MapPixelsToWorld` measurement; `line-translate` measures its own axes for the same
reason.

The two rulers are easy to confuse, and confusing them is silent: a floor written against the width's ruler
compiles, renders plausibly top-down, and stops flooring anything under tilt. The shader therefore binds one
named `metresPerDevicePx` that every screen quantity reads, so a change to the width cannot move them.
**Limitation:** under a top-down orthographic camera the two rulers are bitwise equal, and no test exercises
the min-width floor under a perspective camera, so no test distinguishes the floor's ruler from the width's.

### Where the constant comes from — the camera, measured

`MapCamera.SyncToCamera` pushes it, as the last act of the per-frame camera commit
(`MapCamera.MetresPerDevicePixel`):

```
_MapFrameMetersPerDevicePixel  ==  2 · distanceToLookAt · tan(verticalFov/2) / viewportDevicePx.y
```

**The reference depth is the distance to the look-at**, which under camera-relative rendering is
`|CameraRelativePosition|` — the orbit radius `ComputeRelativePose` is handed. That depth and no other:
`AltitudeForZoom` frames the look-at and nothing else, so any other reference would make the ruler disagree
with the camera that produced it. It is projection-agnostic (a distance and an angle — no Web-Mercator
constant, no latitude, no finite-vs-cyclic branch) and **constant under tilt**, because the orbit radius is;
tilt changes only where in the frame each depth lands.

`dpr` is **absent from the expression**. `ViewportPx` is already physical and the ratio enters once, through
`ViewportLogicalPx` inside the altitude framing — so the two halves cannot disagree.

Deriving the ruler from the camera, rather than from `MetersPerPixel(zoom) / dpr`, has two consequences. The
ruler cannot disagree with the camera under any projection, any art-direction multiplier or the 0.1 m
altitude floor. And a render path that builds a `MapCamera` cannot forget to push it. That matters because
shader globals are **process** state: a fixture that renders without pushing reads whatever ruler an earlier
fixture in the same batch left behind.

### The invariants to test

Along a straight road:

```
renderedWidthPx × clip.w  ==  constant
```

with the ~3.375 near/far ratio as an independent cross-check. **Any test asserting "the band is exactly N
device pixels at every depth" pins the wrong thing** — it encodes the premise the model rejects (see
"Rejected: holding the rendered width constant in device pixels").

At the look-at itself, the two orientations differ and both are derived, not tuned:

| road orientation at the look-at | rendered width, tilt θ |
|---|---|
| running **toward** the camera | `N` device px |
| running **across** the view azimuth | `N · cos θ` device px |

The `cos θ` is the ground plane's own foreshortening: an across-the-view road's across-axis lies in the ground
plane along the tilt direction, so its fixed world width projects short. At θ = 55° that is `N × 0.5736` —
the reason a 16 px band measures 9.18 px and a 120 px band 68.8 px in the tilted fixtures. A test that
expects `N` there asserts the rejected premise.

---

## Rejected: holding the rendered width constant in device pixels

Every per-vertex scheme that holds the rendered width at N device pixels at every depth fights the
perspective divide, and each form of it breaks something else:

- a per-vertex px→world probe, even when w-ratio corrected;
- rejecting that probe against the road's screen direction;
- a Möbius closed form `t = h/(1 − h·g/w₀)` plus a screen residual;
- intersecting the two legs' offset lines in screen pixels.

All of them apply a per-vertex scale and direction to something that needs neither. The results are a
tilt-dependent, depth-**in**dependent over-thickening (~3× at high tilt), bitangents whose screen image is no
longer perpendicular (skewed railway/hatch ticks), and spikes where the per-vertex scale blows up at joins.
Each can pass a synthetic flat fixture.

**The diagnostic is in the symptom.** "Lines get bigger toward the horizon" cannot be a perspective defect —
perspective alone can only make a receding line *smaller*. A symptom pointing the other way proves that
something is fighting the divide, and names the fix: delete that mechanism, do not refine it.

---

## Joins and caps

### The join contract

For a turn, the **concave** side is where the two half-width bands overlap; for a left turn (`t1×t2 > 0`) that
is the left side. The **convex** side is the uncovered wedge.

- **Miter** emits the bisector at `1/cos(θ/2)` on both sides, falling back to bevel past `miterLimit`.
- **Bevel and round** put their chamfer chord or arc fan on the **convex** side — those flank vertices are
  the join's silhouette. On the **concave** side they emit one vertex along the bisector at
  `min(1/cos(θ/2), miterLimit)` (`RibbonJob.ComputeInnerNormal`) — the same direction and magnitude the miter
  join emits there, saturated at `miterLimit` instead of falling back to bevel.
- **A round join whose miter factor is ≤ `line-round-limit`** takes the miter path instead of a fan, and that
  path is still subject to `miterLimit`. The two limits are set independently with no cross-clamp, so the
  cascade is round → miter → bevel.

Guards: `RibbonJobGeometryTests.JoinSide_BevelAndRound_ChamferIsOnTheConvexSide` (the chamfer chord and arc
sit outside both segments' half-width bands) and `JoinVertices_NormalTimesSide_IsUnchanged` (the AA/offset
contract).

### Square caps

Square caps bake `across ∓ along` (length √2) and are indistinguishable from a 90° join by
`(bisector, miter)` alone. `distanceAlong == 0` separates the *start* cap. The end cap is not separable from
the vertex stream and does not need to be: `EmitEndCap` appends an isolated quad, while `EmitStartCap`'s
corners **are** the ribbon's first vertices.

### The width-dependent inner-join fold

All three join types place the inner vertex along the bisector at `halfWidth · k`,
`k = min(1/cos(θ/2), miterLimit)` — the intersection of the two inner offset lines while unclamped, short of
it once the clamp engages. Its along-track overshoot from the joint is `halfWidth · k · sin(θ/2)` (which
equals `halfWidth · √(k² − 1)` **only** unclamped), rising to a supremum of `miterLimit · halfWidth`
(2 · halfWidth at the default) as θ → 180°.

The ribbon does not wait for the vertices to cross. The incoming quad's second triangle `(L0, R1, L1)` has
signed area `½·halfWidth·(S·(k·cos(θ/2) + 1) − 2·halfWidth·k·sin(θ/2))`, so it inverts (folds, and is then
culled) once the adjacent segment is shorter than

    S_crit = 2 · halfWidth · k · sin(θ/2) / (1 + k · cos(θ/2))

whose supremum is `2 · miterLimit · halfWidth` — **twice the line width** at the `line-miter-limit` default of
2, not the ~1.7 half-widths that `k · sin(θ/2)` alone suggests. At `miterLimit = 2`, `S_crit / halfWidth` is
1.00 at 90°, 1.73 at 120°, 2.55 at 150° and 3.93 at 179°; bevel, round and the miter→bevel fallback all fold at
the same threshold. `S_crit` is for one joint. A segment with a clamped join at each end adds both overshoots
and folds at a longer length. θ > 120° is where `miterLimit = 2` engages, so for sharp corners bevel and
round fold where a unit-length inner vertex would not. Net band coverage is still better with the bisector
vertex than without, so this is a sharp-corner artefact, not a reason to revert.

**This is the one place the canonical CCW winding contract (`docs/coordinates-and-projections.md`
§ "Handedness, winding & why the ECEF reflection is load-bearing") does not hold.** The bound is pinned by
`RibbonJobGeometryTests.ShortSegment_InnerJoinFold_OnsetIsExactlyTheDocumentedSCrit` against the Burst
producer; `AllJoinCapCombinations_*`'s uniform-CCW claim is scoped to its own 10-unit fixtures, an order of
magnitude clear of this regime. A fix needs the world width, which the ribbon producer does not have (width is a
shader uniform × per-vertex `WidthScale`). The same fold appears in `docs/line-antialiasing-design.md`
§ "Where same-layer overlap under-covers".

---

## Open questions

1. **The corner miter is baked in world space.** `RibbonJob.ComputeMiterNormals` bakes `normalize(n₁+n₂)`
   scaled by `1/cos(θ_world/2)`. The premise is *not* that a styled width is a screen quantity — the width
   model denies that. It is that a **miter factor is a property of the corner as it appears**, and the
   ground→screen map is anisotropic, so the world half-angle is not the screen one; a band of constant world
   width still needs its corner mitred by the angle the viewer sees. Under the world-width model this is a
   genuine but modest error, and a fix must not reintroduce a per-vertex screen-space scale. *A tile mesh is
   built once and viewed from every angle, so it may bake only view-independent quantities; the two segment
   tangents qualify, the miter derived from them does not.*
2. **Cap and join types at grazing incidence are untested.** All three caps (butt/round/square) and all three
   joins (miter/bevel/round) are implemented and wired; none is measured under tilt at grazing incidence. A
   `normalize(0)` NaN once made `line-cap: round` render as butt, and it hid because nothing measured caps.
   The only rendered square-cap test uses world-metre widths, so a square cap is never rendered with a pixel
   width.
3. **`ComputeInnerNormal`'s `|cos(θ/2)| < 1e-12` branch returns `mu · miterLimit`** with a `mu` whose
   *direction* is floating-point noise, where `ComputeMiterNormals` returns the inert `n1` fallback for the
   same condition. The branch is narrow (reachable only for `|n₁+n₂| ∈ [1e-12, 2e-12)`) and documented at both
   sites; unifying on the `n1` fallback is an option.
4. **The round fan's `|side| = 0` locus is not the centreline at a *clamped* join** — the pivot sits up to
   `2 · halfWidth` from the corner while the rim is at `1 · halfWidth`. The miter join is in the same
   situation and it is not observed to matter (`Join_NoInteriorSeam` is green at 60°), but it is the
   geometric assumption the AA ramp rests on, and nothing measures it at a clamped corner.

---

## Dash distance-along reset

The Burst producer's `LineRibbonVertex.DistanceAlong` resets to 0 at the start of each RING (each geometry
part), not once per tile. A source feature can decode into many rings — boundary and transportation layers
split at attribute changes and way boundaries, not at the tile grid, so a feature that looks like one road on
screen is rarely one part. Pinned by
`LineGraphSchedulingTests.LineMeshGraph_DistanceAlong_ResetsPerRing_NotAccumulatedAcrossRings`.

**Most dash joints sit inside a tile, not on its edge** — roughly 85% on a real z9 transportation layer. A
dash-continuity scheme that reasons only about tile-edge seams addresses the smaller part of the problem.

**Fit a dash pattern by JOINT count, not by length.** Length-weighting hides the defect: a few long,
well-phased parts can outweigh many short, badly-phased ones in a metre-weighted average, even though the
visible artefact (a phase jump) is per joint.

---

## References

- `docs/line-antialiasing-design.md` — the coverage ramp, independent of the width model.
- `docs/meshing-design.md` — pipeline stages, lit model, render layers.
- `docs/coordinates-and-projections.md` § "Handedness, winding & why the ECEF reflection is load-bearing" —
  the winding contract for geometry producers.
- `Assets/Code/MapRenderer.Unity/Shaders/README.md` — shader layout and conventions.
- Nicolas P. Rougier, *Shader-Based Antialiased, Dashed, Stroked Polylines*, JCGT 2(2):105–121, 2013.
  <http://jcgt.org/published/0002/02/08/>. Its analytic cap/join distance table (Table 1) is the useful part
  for caps and joins under tilt (open question 2); its dash atlas is a precision regression against our
  analytic dashes, and its 3D case uses camera-facing impostors, which suits neither ground-draped roads nor a
  shadow pass. The document is CC BY-ND 3.0 — implement from it, do not reproduce it here; supplemental code
  is BSD.
