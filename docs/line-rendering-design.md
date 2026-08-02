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

1. **The corner miter is baked in world space.** `LineTessellator.ComputeMiterNormals` (and its
   `LineRibbonJob` twin) bake `normalize(n₁+n₂)` scaled by `1/cos(θ_world/2)`. The premise is *not* that a
   styled width is a screen quantity — §1 denies that. It is that a **miter factor is a property of the
   corner as it appears**, and the ground→screen map is anisotropic, so the world half-angle is not the
   screen one; a band of constant world width still needs its corner mitred by the angle the viewer sees.
   Under the world-width model this is a genuine but modest error — and note that a fix must not reintroduce a
   per-vertex screen-space scale (§2). *A tile mesh is built once and viewed from every angle, so it may
   bake only view-independent quantities; the two segment tangents qualify, the miter derived from them
   does not.*
2. **Bevel and round joins' inner vertex** is emitted with the miter factor discarded (`|Normal| = 1`), so
   it sits `cos(θ/2)` too far in — wrong in world terms, before any screen question. Needs an inner-join
   clamp rule.
3. **Square caps** bake `across ∓ along` (length √2) and are indistinguishable from a 90° join by
   `(bisector, miter)` alone. `distanceAlong == 0` separates the *start* cap; the end cap is not separable
   from the current stream and does not need to be, because `EmitEndCap` appends an isolated quad while
   `EmitStartCap`'s corners **are** the ribbon's first vertices.
4. **Cap and join types at grazing incidence are untested.** All three caps (butt/round/square) and all
   three joins (miter/bevel/round) are implemented and wired; none has been measured under tilt. Prior art
   before planning it: a `normalize(0)` NaN once made `line-cap: round` render as butt, and it hid because
   nothing measured caps.

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
