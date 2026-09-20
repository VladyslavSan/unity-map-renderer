# Line rendering — width, joins and caps

## 1. The width model — settled, and the whole of it

**`line-width: N px` means the line is N px wide viewed top-down. That is the entire specification.**

The styled pixel width fixes a **world** half-width once — `N × metresPerDevicePixel at the current zoom` —
and the perspective divide does the rest. At pitch 0 every ground point shares a depth, so the line renders
exactly N px everywhere. Under pitch, that same world width renders **wider than N near the camera and
thinner than N far away**. Nothing is measured per vertex. Nothing compensates.

**Reference measurement.** Against the reference renderer at its maximum pitch (more conservative than the
tilt this renderer permits): **~54 px near the camera, ~16 px at the horizon** — a ratio of **~3.375**.
Bounded, not trending to zero: exactly the depth ratio across the frame, which is the signature of a
constant world width. *(Black-box observation of rendered output. No MapLibre source has been read; this
repo is clean-room with respect to it.)*

In `Line_VertexExtrude.hlsl`:

```hlsl
float pxToWorld = (_WidthIsPixels > 0.5) ? _MapFrameMetersPerDevicePixel : 1.0;
```

`_MapFrameMetersPerDevicePixel` is a per-frame global — one number with no depth term, no direction and no
sign, identical at every vertex, so any interpolant of it is exact. The dash path had already adopted it for
precisely these reasons; the width had not.

```hlsl
float pxToWorld = (_WidthIsPixels > 0.5)
    ? ((_MapFrameMetersPerDevicePixel > 1e-9) ? _MapFrameMetersPerDevicePixel : MapPixelsToWorld(...))
    : 1.0;
```

The `> 1e-9` branch is a **missing-push fail-safe, not part of the model** — see §1.1.

**The legitimate per-vertex exceptions are the SAMPLING-GRID quantities**, and there are three: the **AA
straddle pad**, the **min-width floor** (`minHalfWorld` — a sub-pixel road must be rescued to ~1 device px
*where it thinned*, not where the look-at is), and the `_HAIRLINE_SOLID_CORE` floor, which derives from the
pad. Half a device pixel genuinely *is* a screen quantity at that vertex, in both width modes, so all three
keep `MapPixelsToWorld`; `line-translate` measures its own axes for the same reason.

The distinction is load-bearing and easy to lose: before S116 the width and these floors were the *same
expression*, so moving the width to the frame constant silently rebased every one of them. The shader now
binds one named `metresPerDevicePx` that all the screen quantities read, so a future change to the width
cannot move them by accident.

### 1.1 Where the constant comes from — the camera, measured

`MapCamera.SyncToCamera` pushes it, as the last act of the per-frame camera commit
(`MapCamera.MetresPerDevicePixel`):

```
_MapFrameMetersPerDevicePixel  ==  2 · distanceToLookAt · tan(verticalFov/2) / viewportDevicePx.y
```

**The reference depth is the distance to the look-at**, which under camera-relative rendering is
`|CameraRelativePosition|` — the orbit radius `ComputeRelativePose` was handed. That depth and no other:
`AltitudeForZoom` frames the look-at and nothing else, so any other reference would make the ruler disagree
with the camera that produced it. It is projection-agnostic (a distance and an angle — no Web-Mercator
constant, no latitude, no finite-vs-cyclic branch) and **constant under tilt**, because the orbit radius is;
tilt changes only where in the frame each depth lands.

`dpr` is **absent from the expression on purpose**. `ViewportPx` is already physical and the ratio enters
exactly once, through `ViewportLogicalPx` inside the altitude framing — so the two halves cannot disagree by
construction.

*Previously pushed by `RenderLayerSet.ApplyZoom` as `MetersPerPixel(zoom) / dpr`. That is algebraically the
same number whenever `AltitudeMultiplier == 1` and the 0.1 m altitude floor is not binding — every
production scene today — so the move was behaviour-preserving. Its value is that the ruler can no longer
silently disagree with the camera under any projection, any art-direction multiplier or the floor; and that
a render path which builds a `MapCamera` cannot forget to push it. It had been forgotten: shader globals are
**process** state, and a fixture that rendered without calling `ApplyZoom` read a ruler three zoom levels
stale (a clean factor of 8) left behind by an earlier fixture in the same batch.*

### 1.2 The invariants to test

Along a straight road:

```
renderedWidthPx × clip.w  ==  constant
```

with the ~3.375 near/far ratio as an independent cross-check. **Any tooth asserting "the band is exactly N
device pixels at every depth" is pinning the wrong thing** — see §4.

At the look-at itself, the two orientations differ and both are derived, not tuned:

| road orientation at the look-at | rendered width, tilt θ |
|---|---|
| running **toward** the camera | exactly `N` device px |
| running **across** the view azimuth | `N · cos θ` device px |

The `cos θ` is the ground plane's own foreshortening: an across-the-view road's across-axis lies in the
ground plane along the tilt direction, so its fixed world width projects short. At θ = 55° that is
`N × 0.5736` — the reason a 16 px band measures 9.18 px and a 120 px band 68.8 px in the tilted fixtures.
A tooth that expects `N` there is asserting the deleted premise.

---

## 2. What this replaced

Four stages existed only to hold the rendered width constant in device pixels at every depth:

| stage | what it added |
|---|---|
| S111 | per-vertex px→world probe, w-ratio corrected |
| S112 | reject that probe against the road's screen direction |
| S114 | Möbius closed form `t = h/(1 − h·g/w₀)` plus a screen residual |
| S115 | intersect the two legs' offset lines in screen pixels |

Every one measured green on a synthetic flat fixture. All of it was compensation for a divide that should
not have been fought. Reverted; `HEAD` reset to `cf90ffad`, with the full history preserved on branch
**`archive/line-width-compensation`**.

Their symptoms shared one cause — a per-vertex scale and direction applied to something that needed
neither:

- a tilt-dependent, depth-**in**dependent over-thickening (~3× at high tilt)
- bitangents whose screen image stopped being perpendicular, visible as skewed railway/hatch ticks
- spikes where the per-vertex scale blew up at joins

**The diagnosis was available in the first sentence of the original bug report.** "Lines get bigger toward
the horizon" cannot be a perspective defect — perspective alone can only make a receding line *smaller*. A
symptom pointing the other way proves something is actively fighting the divide, and names the fix: delete
that mechanism, do not refine it.

---

## 3. Still open

Each is real, independent of the width question, and much smaller than the width work implied.

1. **The corner miter is baked in world space.** `RibbonJob.ComputeMiterNormals` bakes
   `normalize(n₁+n₂)` scaled by `1/cos(θ_world/2)`. The premise is *not* that a
   styled width is a screen quantity — §1 denies that. It is that a **miter factor is a property of the
   corner as it appears**, and the ground→screen map is anisotropic, so the world half-angle is not the
   screen one; a band of constant world width still needs its corner mitred by the angle the viewer sees.
   Under the world-width model this is a genuine but modest error — and note that a fix must not reintroduce a
   per-vertex screen-space scale (§2). *A tile mesh is built once and viewed from every angle, so it may
   bake only view-independent quantities; the two segment tangents qualify, the miter derived from them
   does not.*
2. ~~**Bevel and round joins' inner vertex** is emitted with the miter factor discarded (`|Normal| = 1`), so
   it sits `cos(θ/2)` too far in — wrong in world terms, before any screen question. Needs an inner-join
   clamp rule.~~ **RESOLVED, in two parts.** First, the concave vertex of a bevel/round join now emits
   `min(1/cos(θ/2), miterLimit)` — the same bisector-direction, miter-factor magnitude the miter join
   already emits at that corner, saturated at `miterLimit` instead of falling back to bevel (see
   `RibbonJob.ComputeInnerNormal`; pinned by `RibbonJobGeometryTests.InnerJoin_*` and
   `LineRibbonJobTests.InnerJoin_*`). Second — found while re-deriving the first part — that magnitude
   vertex was being emitted on the **wrong side of the corner**: the original comment claimed
   `t1×t2 > 0 ⇒ left turn, outer side = left`, but a
   left turn actually puts the CONCAVE side on the left (the two half-width bands overlap there); the
   convex (uncovered-wedge) side is the other one. So bevel/round joins were chamfering/fanning the
   concave side (hidden inside the band overlap — a bevel rendered as a miter, a round join never rendered
   round) and placing the single miter-factor vertex on the convex side, the reverse of the intended
   contract. This inversion **pre-dated this stage** (review finding F1, `inner-join-miter` stage); the
   join-side-correction stage fixed it, moving the chamfer/arc to the convex side and the single
   bisector/miter-factor vertex to the concave side — the *derivation* of the magnitude (this item's
   original scope) was correct throughout and needed no change. Pinned by
   `RibbonJobGeometryTests.JoinSide_BevelAndRound_ChamferIsOnTheConvexSide` (region-membership: the chamfer
   chord / arc sit strictly outside both segments' half-width bands) and
   `RibbonJobGeometryTests.JoinVertices_NormalTimesSide_IsUnchanged` (the AA/offset contract is unaffected).
   This left one thing open — see the new item below.
3. **Square caps** bake `across ∓ along` (length √2) and are indistinguishable from a 90° join by
   `(bisector, miter)` alone. `distanceAlong == 0` separates the *start* cap; the end cap is not separable
   from the current stream and does not need to be, because `EmitEndCap` appends an isolated quad while
   `EmitStartCap`'s corners **are** the ribbon's first vertices.
4. **Cap and join types at grazing incidence are untested.** All three caps (butt/round/square) and all
   three joins (miter/bevel/round) are implemented and wired; none has been measured under tilt. Prior art
   before planning it: a `normalize(0)` NaN once made `line-cap: round` render as butt, and it hid because
   nothing measured caps.
5. **Open (recorded, not fixed): the width-dependent inner-join overlap.** All three join types place the
   inner vertex along the bisector at `halfWidth · k`, `k = min(1/cos(θ/2), miterLimit)` — the intersection
   of the two inner offset lines while unclamped, deliberately short of it once the clamp engages. Its
   along-track overshoot from the joint is `halfWidth · k · sin(θ/2)` (which equals `halfWidth · √(k² − 1)`
   **only** unclamped), rising to a supremum of `miterLimit · halfWidth = 2 · halfWidth` as θ → 180°.
   The ribbon does not wait for the vertices to cross: the incoming quad's second triangle `(L0, R1, L1)`
   has signed area `½·halfWidth·(S·(k·cos(θ/2) + 1) − 2·halfWidth·k·sin(θ/2))`, so it inverts (folds, and
   is then culled) once the adjacent segment is shorter than

       S_crit = 2 · halfWidth · k · sin(θ/2) / (1 + k · cos(θ/2))

   whose supremum is `2 · miterLimit · halfWidth = 4 · halfWidth` — i.e. **twice the line width**, not the
   ~1.7 half-widths that `k · sin(θ/2)` alone suggests. Measured at `halfWidth = 2, miterLimit = 2` (bevel,
   round and the miter→bevel fallback all fold at the same threshold): `S_crit / halfWidth` = 1.00 (θ=90°),
   1.73 (120°), 2.55 (150°), 3.93 (179°); with both ends of a segment clamped, a fold at 4.1 half-widths.
   Note θ > 120° is exactly where `miterLimit = 2.0` engages, so for sharp corners bevel/round now fold
   where the old unit-length inner vertex did not — but net band coverage still **improves** (the
   correction restores far more of the intended band than the culled sliver removes), so this is a
   sharp-corner artefact to schedule, not a regression to revert. **This is the one place the canonical
   CCW winding contract (`docs/coordinates-and-projections.md` §7.1) does not hold**, so the bound above is
   pinned rather than left as prose: `RibbonJobGeometryTests.ShortSegment_InnerJoinFold_OnsetIsExactlyTheDocumentedSCrit`
   asserts, for all three join types at a 90° corner where `S_crit` is exactly 2.0, that S = 1.8 folds
   exactly one triangle at exactly −0.8, that S = 2.0 makes it exactly degenerate, and that S = 2.2 is
   uniformly CCW. That exact pin runs directly against the Burst producer (`RibbonJob`), which ships this
   geometry. `AllJoinCapCombinations_*`'s uniform-CCW claim is scoped to its own 10-unit fixtures, an order
   of magnitude clear of this regime. Fixing it requires the world width, which
   neither ribbon producer has by construction (width is a shader uniform × per-vertex `WidthScale`). Applies
   to all three join types, including the miter path that has shipped since before this stage. Same
   phenomenon as `docs/line-antialiasing-design.md` §6.3's "short-segment fold" row, which understated the
   bound as "segment length ≲ line width" and has been reconciled with the derivation above.

Three smaller findings from the join-side-correction review are recorded here rather than fixed in-stage:

- ~~**No *direct* analytic tooth on the Burst arm's right-turn branch or the round join's convex rim.**~~
  **RESOLVED.** Both were carried only transitively, by a differential-parity oracle against a managed
  reference producer; that oracle retired with the reference. Each now has a direct assertion on the Burst
  arm: `RibbonJobGeometryTests.InnerJoin_Round_90RightTurn_Unclamped_MirrorSignBranch` (right-turn sign
  branch) and `RibbonJobGeometryTests.SharpRoundJoin_PreservesFan_RimAtHalfWidthRadius` (the convex rim
  sits at exactly halfWidth from the corner), with
  `LineRibbonJobTests.InnerJoin_Bevel_90LeftTurn_Across_MatchesAnalyticIntersection` on the left.
- **`ComputeInnerNormal`'s `|cos(θ/2)| < 1e-12` branch returns `mu · miterLimit`** with a `mu` whose
  *direction* is floating-point noise, where `ComputeMiterNormals` returns the inert `n1` fallback for the
  same condition. The branch is narrow (reachable only for `|n₁+n₂| ∈ [1e-12, 2e-12)`) and documented at both
  sites; consider unifying on the `n1` fallback.
- **The round fan's `|side| = 0` locus is no longer the centreline at a *clamped* join** — the pivot sits up
  to `2 · halfWidth` from the corner while the rim is at `1 · halfWidth`. Identical to the miter join's
  long-standing situation and not observed to matter (`Join_NoInteriorSeam` is green at 60°), but it is the
  geometric assumption the AA ramp rests on and nothing currently measures it at a clamped corner.

---

## 4. How the teeth failed, and what to do instead

Every reverted tooth asserted the band was exactly N device pixels — **the compensation's own goal**. They
passed while the render was visibly wrong, because they encoded the premise under test.

Three fixture gaps let this run for four stages, and all three are the same mistake:

- every line tooth rendered **one straight road**, so a corner defect was invisible
- the dash perpendicularity probe runs on a **straight** road, so a cross-bar tilt at joins was invisible
- the only square-cap scene builds with **world-metre** widths, so it never called the pixel-width path at all

Before trusting any future line tooth, ask what it would read if the mechanism it targets were absent —
and check the reference renderer's *output* early. One measurement of the reference would have ended this
on the first day.

---

## 5. Dash distance-along reset

The Burst producer's `LineRibbonVertex.DistanceAlong` resets to 0 at the start of each RING (each
geometry part), not once per tile. A source feature can decode
into many rings — boundary and transportation layers split at attribute changes and way boundaries, not at
the tile grid, so a feature that looks like one road on screen is rarely one part.

**Most dash joints sit inside a tile, not on its edge.** Measured on a real z9 transportation layer: 74
features decode into 1665 parts, and of the resulting shared endpoints, 1817 are interior to a tile against
309 on a tile edge — roughly 85% of joints are interior. A dash-continuity scheme that reasons only about
tile-edge seams addresses the smaller part of the problem.

**Fit a dash pattern by JOINT count, not by length.** Length-weighting hides the defect: a few long,
well-phased parts can outweigh many short, badly-phased ones in a metre-weighted average, even though the
visible artefact (a phase jump) is per joint. Pinned by
`LineGraphSchedulingTests.LineMeshGraph_DistanceAlong_ResetsPerRing_NotAccumulatedAcrossRings`.

---

## References

- `docs/line-antialiasing-design.md` — the coverage ramp, unaffected by any of the above.
- `docs/meshing-design.md` §1–4 — pipeline stages, lit model, render layers.
- `docs/coordinates-and-projections.md` §7.1 — winding contract for geometry producers.
- `Assets/Code/MapRenderer.Unity/Shaders/README.md` — shader layout and conventions.
- Nicolas P. Rougier, *Shader-Based Antialiased, Dashed, Stroked Polylines*, JCGT 2(2):105–121, 2013.
  <http://jcgt.org/published/0002/02/08/>. Its analytic cap/join distance table (Table 1) is the useful
  part for §3 items 3–4; its dash atlas is a precision regression against our analytic dashes, and its 3D
  case uses camera-facing impostors, which suits neither ground-draped roads nor a shadow pass. Document is
  CC BY-ND 3.0 — implement from it, do not reproduce it here; supplemental code is BSD.
