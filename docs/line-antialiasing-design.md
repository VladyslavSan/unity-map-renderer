# Line antialiasing — design & SSOT

Line silhouettes render a one-device-pixel coverage ramp **straddling** the styled edge, behind the
`_EDGE_ANTIALIASING_OFF` shader feature. This doc is the *why*: the compositing mechanism the design turns on,
the invariants, the rejected directions and the known limits. The shader-level mechanism notes live in
`Assets/Code/MapRenderer.Unity/Shaders/Map/Line/README.md`; the width model the ramp sits on is
`docs/line-rendering-design.md` § "The width model".

---

## The goal

**Antialiased outer silhouettes on map lines, with the fill↔casing boundary of a cased road staying crisp.**
Both halves, or it is not a fix. The line's only other thin-line safeguard is the min-width floor (styled
half-width ≥ 0.5 device px ⇒ a stable 1 px hairline).

---

## Why a fade inside the band fails

A cased road is two stacked transparent draws: the casing under, the fill over (`Queue=Transparent`,
`ZWrite Off`, painter-ordered). Straight alpha blending computes `dst = src·a + dst·(1−a)`. **Where `a == 1`
there is no bleed at all** — the fill replaces the casing. Bleed exists only where the fill's alpha is < 1.

That one fact decides every candidate:

- **An inset feather** (a fade eating inward over some width) puts `a < 1` inside the styled band. For a
  2–3 px road fill that is a large fraction of the band, so casing colour shows well inside the fill. That is
  the "mush".
- **An outset padded by the full fade width** keeps `a == 1` out to the styled edge, so the line renders
  fatter than styled and the fill sits on a wrong-width casing base.
- **A tunable fade width** gets widened as a "quality dial" and produces multi-pixel transitions. Correct
  coverage AA is a ~1 px transition; widening it blurs and never adds resolution. Folding `line-blur` into the
  AA width (`aaEdgePixels = _AaEdgeWidth + _Blur`) is the same error.

The trilemma {solid core, unchanged apparent width, antialiased} holds for any shader-space fade that has to
buy all three with alpha. The straddle below holds all three for bands wider than about 2 device px, because
the pad gives the outer half of the ramp room to land. Below that the solid core vanishes; the hairline
strategies choose which property to give up. **A ~1 px transition between fill colour and casing colour is not
mush; it is what a correctly antialiased boundary looks like.**

---

## The mechanism: a strict analytical straddle

**One device pixel of analytical coverage, straddling the styled edge, with no tunable width.** Straight-alpha
blending, the transparent queue band, `ZWrite Off` and every paint property stay as they are.

- Coverage ramps **linearly from 1 to 0 across one device pixel centred on the styled edge** — 0.5 px inside,
  0.5 px outside. The 50 % contour sits on the styled edge, so **apparent width is unchanged**. Interior
  coverage is 1, so **there is no bleed inside the band**.
- The ramp width is a **compile-time constant, not a material property.** Nothing is bindable, so there is
  nothing for a later reader to widen. AA as a whole is switchable (see "The toggle"); its *width* is not.
- `_Blur` (`line-blur`) stays a separate, opt-in, multiplicative soft edge, default 0. It is a style property,
  not AA, and it is never folded into the ramp.
- `line-opacity` multiplies coverage as before. There is one render-state regime, so no per-layer opacity
  fallback exists.

### The half-pixel pad

A straddle needs half its ramp **outside** the styled edge, and the rasterizer only produces fragments where a
triangle covers a pixel centre. So the ribbon extrudes **0.5 device pixels past the styled half-width**, or the
outward half of the ramp has nowhere to land and the result degenerates into the inset that fails. A reader
who takes "analytical coverage" as fragment-only builds exactly that broken variant.

This is not the outset failure: the pad is a **fixed 0.5 px** and the 50 % contour stays on the styled edge.

- **The pad is always measured** — `0.5 · MapPixelsToWorld(centerWS, unitDir_WS)`, in both width modes. It is
  never `0.5 · pxToWorld`: `pxToWorld` is a unit conversion that reads `1.0` for a world-unit width, so it
  would pad a world-unit layer by half a **metre** while looking correct on every pixel-width layer.
- **The pad goes into `outerWorld` BEFORE the miter multiply.** The miter factor `1/cos(θ/2)`
  (`RibbonJob.ComputeMiterNormals`) makes the *perpendicular* distance equal the value it multiplies (`outer + pad`), so
  `miter·(outer + pad)` holds the perpendicular pad at 0.5 px at any corner. A pad added after the multiply has
  a perpendicular component `pad·cos(θ/2)`, which **shrinks toward zero** as the corner sharpens — the ramp
  loses its landing room where geometry is tightest.
- **`innerFrac` is taken against the padded outer.** `|side| == 1` is the padded edge, so a gap hole sized
  against the unpadded half-width shifts.

### Why the threshold needs no new data

`side` stays baked ±1. After padding, `|side| = 1` is the padded edge and the styled edge sits at
`t = styledHalf / paddedHalf`. The gradient `|∇side|` is side-units per device pixel, `≈ metresPerPixel /
paddedHalf`, so `1 − t = 0.5·|∇side|`. The threshold is a function of `side` and its own screen-space
derivative, nothing else, and the ramp collapses to one line:

```hlsl
coverage = saturate((1.0 - abs(side)) / sideGrad);   // sideGrad = |∇side|, Euclidean
```

`|side| = 1` ⇒ 0; `|side| = t` ⇒ 0.5, on the styled edge. This holds whether `styledHalf` came from the miter
branch or the min-width-floor branch, and per fragment on a trapezoidal quad whose ends have different scales —
the derivative is local, so no global closed form is needed.

So there is **no new vertex attribute, no new interpolator, no CBUFFER change and no new float uniform.** This
matters because the line varying `uv` is already packed (dashU / side / innerFrac, plus the SolidCore scalar
in `w` on the forward passes); a design that
assumes the shader needs the styled-edge threshold as a separate quantity reaches for a new varying threaded
through every pass struct, and that is unnecessary.

**The gradient is Euclidean:** `sideGrad = length(float2(ddx(side), ddy(side)))`, shared by the outer ramp and
the gap-hole ramp. A ramp divided by `fwidth` is direction-dependent: `fwidth` is `abs(ddx) + abs(ddy)`, the
L1 length of the gradient, which over-reads the true length by `|cos θ| + |sin θ| ∈ [1, √2]` with the
silhouette's screen angle. A 45° diagonal then gets a 1.41 px ramp where an axis-aligned line gets 1.00 px,
and loses 0.41 px of ink (`W + 1 − √2`), so every diagonal and curve renders softer and thinner than the
horizontals beside it. `_Blur` and dash keep `fwidth`; they are separate style features, not antialiasing.

**The gap hole** (cased/hollow lines) gets a symmetric ±0.5 px straddle centred on `|side| = innerFrac`:
`saturate((|side| − innerFrac)/sideGrad + 0.5)`. It is not the outer formula: the inner edge has ribbon on
both sides, so a centred straddle is reachable without a pad, and the `+ 0.5` is what centres it.

**With `_EDGE_ANTIALIASING_OFF`, the OFF branches are the pre-AA expressions verbatim** — no pad, a hard edge,
a hard gap cut — not the AA expressions with a zero added.

**Grazing angles.** `MapPixelsToWorld`'s `max(refPx, 0.1)` clamp makes an edge-on offset fall short of the
styled pixel count. Because the ramp is derivative-driven, its *shape* self-corrects to the scale the fragment
actually sees; it does not add a second, independent AA defect.

**Mesh bounds and counts.** `LineRibbonVertex.Position` is the raw centreline point; the whole styled width is
applied in the vertex shader. Line mesh bounds are the box around the centreline points. They include neither
the styled width nor the pad, so a line whose centreline is just outside a culling volume can be culled while
its band is on screen. The pad does not change vertex or index counts.

---

## Invariants

Each holds with AA on unless stated. Each AA-on guard below first calls `AssertAaKeywordClear` on its
material, so turning the keyword off reds the guard instead of greening it.

| invariant | guard (`LineAaSnapshotTests`, in `LineEdgePrecisionTests.cs`) |
|---|---|
| The outer silhouette carries strictly-intermediate coverage, monotonic across a perpendicular cut — never binary | `OuterSilhouette_IsAntialiased` |
| A cased pair's fill↔casing boundary has ≤ 1 px of intermediate colour, and zero casing colour inside the fill band | `CasedPair_InternalBoundary_StaysCrisp` |
| The coverage integral across a styled N px line equals N, in both width modes (the pad is a straddle, not a fattening, and sits before the miter multiply) | `ApparentWidth_Unchanged_MatchesStyledPixels` |
| Apparent width does not depend on screen direction | `ApparentWidth_IsDirectionIndependent` |
| No intra-feature seam at a miter, bevel or round join | `Join_NoInteriorSeam` |
| A round cap fades at every angular position of its arc | `RoundCap_Silhouette_FadesAtEveryAngle`, `RoundCap_BlurFadesAllTheWayAround` |
| With AA off, the profile is binary and the integral equals the pre-AA width — the pad is gone, not merely un-ramped | `AaOff_ReproducesHardEdge` |

**Seam-freedom rests on the ramp keying on the interpolated `|side|` varying**, which is C⁰-continuous across
every interior edge of the ribbon and its fans. Coverage derived from per-triangle edge distance is not
available: it needs `SV_Barycentrics` (SM 6.1), and the forward pass is `#pragma target 2.0`. The reachable
defect is a `|side|` discontinuity or dip at an interior edge — the class of the round-cap tagging contract
below.

**Limitation no test can observe:** near a join's corner, the two adjoining ribbon quads overlap the join
geometry and draw at `a == 1`, compositing over any seam the fan renders underneath. Only the arc's terminal
flanks (`arcStart`/`arcEnd`, which are quad corners) are visible. A bevel's chord stands off at only
`halfWidth·cos(θ/2)`, so the probe angle matters (the join test uses a 60° corner).

**A one-pixel straddle produces no partially-covered pixel where the silhouette is both axis-aligned and
aligned to a pixel boundary**: both straddling pixel centres sit ~0.5 px from the edge and read 1.0 and 0.0.
This is correct AA and is invisible to the eye; a test that requires a partial pixel needs a sub-pixel fixture
offset.

**How the guards measure.** Width is an alpha-weighted coverage integral across a cut, never a count of
non-background pixels over a threshold: threshold counts swing ±1–2 px as feathered edge pixels cross the
threshold, and would flake a correct fix. A coverage normaliser is a named reference region, taken from
unsaturated values, never the brightest pixel on the column: a saturated reference pins at the clamp and hides
the variation it should measure.

---

## The toggle

AA sits behind a boolean shader feature. **This does not reopen "no knob".** The failure mode is a tunable ramp
**width**; a boolean cannot be widened.

> **Hard constraint.** The toggle is a **keyword, never a `Range`/float.** Exposing the ramp width as a number
> is a rejection of this design and must be argued on its own merits — it is not an extension of the toggle.

- **Name: `_EdgeAntialiasing` (property, default `1.0`) ↔ `_EDGE_ANTIALIASING_OFF` (keyword).** It reads
  positively in the Inspector and sits in the shader's internal render-params group beside `_WidthIsPixels`.
  It is collision-safe two ways: no MapLibre term is `line-edge-antialiasing`, so the `line-X → _X` binding
  cannot produce it; and the style→shader binding (`MaterialFactory.BindLinePaintToApplier`) is an explicit
  allowlist of `PropertyId` constants, so a style layer cannot reach a property outside it. The deleted width
  dial's name, `_AaEdgeWidth`, is not reused.
- **Declared in every pass that includes `Line_VertexExtrude.hlsl`, in both twins** (`Line.shader`,
  `LineUnlit.shader`). The pad is vertex-side, so a keyword missing from any pass gives that pass a different
  silhouette. For the same reason it is plain `shader_feature_local`, never `shader_feature_local_fragment`,
  which strips the keyword from the vertex stage.
- **`shader_feature_local`, not `multi_compile`.** It doubles the line shader's variant count per pass; that is
  accepted, because `multi_compile` compiles both variants for every material in the project.
- **Polarity `_OFF`, AA on by default — a build-correctness rule.** `shader_feature_local` variants are
  stripped from player builds unless a material in the build declares the keyword. With `_OFF` polarity the
  shipping variant carries no keyword and can never be stripped. Positive polarity makes AA work in the Editor
  and vanish in a build.
- **A plain `[ToggleUI]` float, keyword synced in code.** `[ToggleUI]` attaches no keyword. The keyword is set
  in `LineShaderGUI.ValidateMaterial` (and the unlit twin's GUI), following `_ReceiveShadows` in
  `BaseShaderGUI`. This codebase does not use Unity's keyword-attaching `[Toggle(KEYWORD)]`/`[ToggleOff]`
  drawers; a toggle with no `ValidateMaterial` entry is inert.
- **A Properties-block float only — not a CBUFFER member.** Nothing in HLSL reads it; only the keyword is read.
  So `UnityPerMaterial`, the `UNITY_DOTS_INSTANCING` block and the BRG SoA are untouched.
- **Registries, not raw strings:** `ShaderKeywords.EdgeAntialiasingOff`, and `EdgeAntialiasing` in
  `Line/PropertyNames` with `Line/PropertyId` deriving from it. `NoRawStringMaterialAccessGuardTests` catches a
  raw property-name literal in a Material API call; it does **not** catch a raw keyword literal inside
  `CoreUtils.SetKeyword`, so the keyword half is a review convention.
- **The toggle gates BOTH the pad and the ramp.** Off ⇒ no pad *and* no ramp. Gating only the ramp leaves lines
  1 px fatter with a hard edge — the outset artefact. Gating only the pad leaves the ramp nowhere to land — the
  failed inset.
- **Default state and clones.** The base line material carries the property at `1.0` and no keyword (AA on).
  Per-layer materials are `new Material(source)` clones, and the copy constructor carries the source's keyword
  set, so every clone inherits the base state with no tweaker change.

**Backend behaviour.** Both mesh backends register whole `Material` objects. A keyword is per-material state,
not per-instance data, so it never touches the BRG SoA, and it cannot split batches: two layers that disagree
about AA are already two materials and two `BatchMaterialID`s. The BRG path honours a `shader_feature_local`
keyword on a registered material: `FillTweaker.ApplyPainterContract` depends on it for
`_SURFACE_TYPE_TRANSPARENT` on every per-layer fill clone (see Open questions for the vertex-stage case).

---

## Hairline strategies

At a styled width of one device pixel the straddle's solid core (`W − 1` px) vanishes. The profile becomes a
tent that peaks only where a pixel centre lands on the centreline, so a hairline reads as one bright pixel or
two half-bright ones depending on sub-pixel phase, and shimmers under motion. `_HairlineStrategy` selects a
remedy; the strategies, their trade-offs and their measurements are in the line shader README
§ "Hairline strategies — `_HairlineStrategy`". The design constraints:

- **Default (`_`) carries no keyword**, which keeps the shipping variant un-strippable — the same argument as
  the toggle's polarity. Default is never a named keyword.
- **`_HAIRLINE_HARD` narrows the ramp and keeps it centred on the styled edge.** Narrowing without re-centring
  leaves the hard edge half a pixel out — the outset artefact. It narrows the outer silhouette and the gap-hole
  cut together, so a thin casing ring is not half-hardened. It is fragment-only: the band width comes from
  `1/sideGrad`. It gives up width-proportionality over `(1.0, 1.2]` px.
- **`_HAIRLINE_SOLID_CORE` clamps the rendered band to 2 device px and scales coverage by the true width over
  the clamped width — both halves or neither.** The clamp alone renders a hairline with twice its styled ink;
  the compensation alone keeps the tent. The scalar rides the `uv` varying's `w` in the forward passes only.
  - Its clamp forces the extruded half-width to ≥ 1.5 px, above the min-width floor's 1.0 px, so **the floor
    never binds under SolidCore.** That is what buys proportionality below 1 px, and it is a real cost: the
    floor exists so hairline roads stay legible zoomed out.
  - Under the world-width model the clamp is a device-pixel floor on a world length, so for a pixel-width line
    it engages progressively as the line recedes, and the compensation dims it to match.
- **The hairline quantities are device pixels.** The 0.5 px pad, the `1/sideGrad` width estimate and
  `HAIRLINE_MIN_WIDTH_PX` are sampling-grid quantities (`docs/device-pixel-ratio-design.md` classifies them as
  device), so at dpr 2 a 1-logical-px road is 2 device px and the hairline strategies do not engage for it.
  Every AA and hairline guard runs at DPR 1, where logical and device pixels are the same. The DPR line tests
  (`DevicePixelRatioSnapshotTests`) use a 16 px line, clear of both thin-line clamps, so the hairline
  behaviour at DPR ≠ 1 is derived, not measured.
- **With AA off, every strategy renders the pre-AA hard edge.** The strategy blocks are guarded on
  `!defined(_EDGE_ANTIALIASING_OFF)`, so the strategy keywords are inert under `_EDGE_ANTIALIASING_OFF`.

---

## Round-cap `side` tagging

Every triangle of a round cap's fan must carry a **uniform rim sign**: the pivot at `side 0`, every rim vertex
at `+1`. A rim edge whose endpoints are `+1` and `−1` interpolates through `|side| = 0` at its midpoint, reads as
deep interior, and gets no fade — one aliased arc segment per capped end. `_Blur` and the gap-hole cut key on
`|side|` too, so the same mis-tag renders a hard blur segment and punches a wedge out of a hollow cap.

**The cap reuses a rail vertex.** `EmitStartCap`'s `rightButt` (and `EmitEndCap`'s `rightPrev`) is the adjoining
ribbon quad's corner, and the quad needs it at `−1` for its own gap-hole cut and `line-offset` sign. It cannot
be retagged in place. So each round-capped end emits **one extra vertex, co-located with that rail vertex and
tagged `+1`**, used only as the fan's seed. `leftButt` is already `+1` on both its uses. That is one extra
vertex per round-capped end and zero extra triangles.

- **Emission order is load-bearing.** `Execute()` reads `leftPrev = v-2; rightPrev = v-1` right after the start
  cap (the `[count-2]=left, [count-1]=right` contract), so the seed is emitted **before**
  `leftButt`/`rightButt`.
- **Worst-case sizing** counts the seed: `RibbonJob.MaxVertexCount` gives the start cap `rs + 4` and the end cap
  `rs + 2`; `MaxIndexCount` is unaffected.
- **The round join does not need this**, because it manufactures its own flank vertices at one sign rather than
  reusing a rail vertex. The transferable rule is "manufacture a fresh vertex at the sign this fan needs", not
  "duplicate every vertex per sign".
- **The pivot has no direction.** It carries `extrudeN == 0` so it stays on the centreline. The extrusion
  carries that zero through `normalize()` (which is NaN at zero and would discard every fan triangle), and
  `MapPixelsToWorld` returns the direction-free scale for a zero direction.

Vertex counts, positions and winding are identical whichever way the seed is tagged, so only a direct check on
the tag catches a regression: `RibbonJobGeometryTests.RoundCap_FanTriangles_HaveUniformRimSide` is the one place
the uniform-rim-sign guarantee is checked.

---

## Seams and same-layer overlap

Blended coverage accumulates where **two partial-coverage edges of the same layer overlap over background**: two
pixels at 50 % that should compose to full coverage read `2c − c² = 0.75`.

### Joins and caps do not self-overlap

- **`side` reaches ±1 only at the ribbon's lateral boundary**, so coverage keyed on `|side|` fades only at the
  silhouette, never at an internal triangle boundary.
- **Miter:** two shared vertices and one quad; `leftPrev`/`rightPrev` thread forward. One continuous strip.
- **Bevel and round:** a fan about one shared inner (concave) vertex, with the chamfer or arc on the convex side
  (`docs/line-rendering-design.md` § "The join contract"). Every shared edge is `(+1, −1)` or `(arc, inner)`, so
  `|side|` is C⁰-continuous across it. The flank vertices are emitted at one consistent sign, so every arc edge on
  the silhouette has `|side| = 1`.
- **Round cap against the ribbon quad:** the pivot is the geometric midpoint of the quad's `leftButt→rightButt`
  chord **and** `side 0` is the midpoint of `±1`, so position and `|side|` agree along the shared line. No crack.
- **Square cap:** `across ± along` gives a √2-length extrude vector consumed by the miter factor; sides stay ±1.

### Same-layer overlap is approximately self-cancelling

`dst = src·a + dst·(1−a)` with `src == dst` yields `dst`. Within one layer every feature paints the same
**albedo**, so an edge band landing on another same-layer feature's interior (a stem's cap inside the
through-road) is very nearly invisible. It is an approximation, not an identity: the forward pass is genuine
lit PBR, so two same-layer fragments at different positions share albedo but not necessarily the final lit
colour. The residual is small at the default dim specular and a near-top-down view.

### Where same-layer overlap under-covers

| case | reach | magnitude |
|---|---|---|
| **Junction crotch** — two same-layer features meeting at an angle, silhouettes converging over background | a wedge of ~1–3 px at each junction node | up to ~25 % under-coverage — a faint light notch |
| **Cross-tile duplicate geometry** — MVT tiles carry a buffer and line geometry is **not** clipped to tile bounds | the buffer strip along every tile boundary | the edge reads `2c − c²` instead of `c`, so the 50 % contour shifts outward by ~0.25 px. The duplication itself is independent of AA and already shows on any `line-opacity < 1` layer |
| **Short-segment fold** — a join quad folds when the segment is shorter than `S_crit`, up to **`miterLimit` × line width** per joint (2× at the default) (`docs/line-rendering-design.md` § "The width-dependent inner-join fold") | rare; bounded by `line-miter-limit` (default 2) | a faint sub-pixel bright spot |
| **Genuinely self-crossing polyline** | rare — OMT road geometry is noded at intersections | same as the junction crotch |

Not affected: the casing/fill pair itself. The casing's outer band lies over background and the fill's outer
band over the casing's `a == 1` interior, so a cased road never double-fades.

### Verdict: accepted

**Measurable but minor.** There are no intra-feature seams for any join or cap type; the cost is a few pixels at
junction crotches and a sub-pixel edge shift in tile buffer strips, each better-looking than a hard staircase.
`LineAaSnapshotTests.Junction_UnderCoverage_Recorded` records the crotch deficit to the test log rather than
gating on it, so a regression shows as a changed measurement.

The only real fix is a **whole-layer offscreen composite** — render a layer to an offscreen target, then
composite once, which makes intra-layer overlap idempotent. It needs neither depth nor MSAA, so it is compatible
with both locked rulings below, and it is a separate piece of work.

---

## Rejected directions

**Locked maintainer rulings**, recorded so they are not reopened:

- **No depth.** Map layers do not ZWrite and do not depth-test; painter order is the render-queue band. Fills
  and lines share one transparent band (`LayerDrawOrder`): Unity drains the entire opaque queue before the
  transparent one, so a line moved below 2500 renders under every fill regardless of declared order.
- **No MSAA.** Plain MSAA up to **16×** in this project gave no meaningful improvement — MSAA antialiases
  coverage while the layers still blend. MSAA is off project-wide.

### Rejected: fusing casing and fill into one draw

`liberty.json` (the production style) shows why. Its `_casing` suffix is a real convention, in three parallel
families (`tunnel_*`, `road_*`, `bridge_*`), but:

- **Casings are not drawn adjacent to their fill.** In the road family every casing draws under every fill, with
  `road_path_pedestrian` interleaved between the two blocks, so at a junction a minor road's fill covers the
  motorway's casing instead of being cut by it. Fusing collapses two slots into one: fused at the fill's slot the
  casing covers every other road fill; fused at the casing's slot the fill sits under them. **Neither reproduces
  the declared compositing**, and every OpenMapTiles-derived style uses this arrangement. `line-sort-key` would
  compound it.
- **The pairing predicate is unsound.** Name-matching pairs `tunnel_street_casing` with `tunnel_minor` and
  `bridge_street_casing` with `bridge_street`, whose filters are **disjoint**. "Paired" layers differ in filter
  clauses, and some casings have no same-stem fill. A correct predicate needs per-feature evaluation of both
  filters; filter subsumption is not decidable statically, and a layer processor selects features for one
  `StyleLayer`.
- **Most line layers are unpaired**, so a fused and an unfused path must both exist and stay consistent.
- **Paired layers disagree on paint** — constant vs zoom-interpolated colour, a zoom opacity fade on one side
  only, a dasharray on the casing only, and fill widths that reach 0 while the casing is 1–4 px. One material
  carries one of each line property, so fusion needs a second naming scheme for every style-bound property (into
  the `_Blur`/`line-blur` collision hazard), a second applier, a second per-vertex colour and width scale, and two
  dash walks in one fragment for a dashed casing under a solid fill.

### Rejected: replace-compositing

The opaque queue, or blend-off plus alpha-to-coverage, falls to both rulings. Alpha-to-coverage requires MSAA;
with one sample it degenerates to a binary threshold. `_AlphaToMask` is declared on the line and fill shaders
and never set; it is not part of this design, and setting it without MSAA does nothing.

### Rejected: manual supersampling

Rendering at 2×/4× offscreen and box-downsampling is compatible with both rulings, and fails for the MSAA
reason: **the problem is not sampling density.** Transparent layers still blend rather than replace, so SSAA
reproduces the same compositing at strictly higher cost than a 1-sample analytical straddle.

---

## Why fills do not use this mechanism

A line's two long edges have no abutting partner, so half the ramp may lie inside the edge. A fill's edges do:
two fills abut along shared boundaries and tile seams, and any ramp partly inside the boundary shows the
background through a trench there. Fills therefore grow their band strictly outward — see
`docs/fill-boundary-antialiasing-design.md`. `MapPixelsToWorld`, shared by both, is a ruler, not a coverage
mechanism; its sharing is no evidence that this coverage approach extends to fills.

A fill also has no `side`-equivalent field: its interior is an earcut mesh whose vertices carry no distance to
the boundary, and a per-triangle fade would also ramp earcut's internal diagonals and hole-bridge seams. The
fill band is separate geometry partly for this reason.

---

## Limitations

- **`line-blur` feathers from the padded edge**, half a pixel outside the styled edge. Sub-pixel.
- **`_Blur`'s and dash's feathers use `fwidth`**, so their softness varies by up to √2 with a line's screen
  direction. Whether that matters is a question for those features' own semantics; it is not antialiasing.
- **World-unit widths have no min-width floor** (the floor is pixel-width-only). A sub-pixel world-unit line
  fades rather than holding a 1 px hairline, and under `_HAIRLINE_HARD` it renders as a broken dashed line. No
  production layer takes that path (`MaterialFactory` sets `_WidthIsPixels = 1`), but the committed base line
  materials ship `_WidthIsPixels: 0`, so anything rendering with a base material directly does. The floor is not
  the fix.
- **Under `_HAIRLINE_SOLID_CORE` the capability passes clip an uncompensated silhouette.** `LineCoverage`'s
  signature carries no compensation, so `clip(LineCoverage(...) − 0.5)` in ShadowCaster / DepthOnly /
  DepthNormals / GBuffer sees the band at its clamped width. All of them are inert (URP excludes
  `Queue=Transparent` from the opaque prepasses); activating them means revisiting this.

## Open questions

1. **A long single segment measures differently from a subdivided one under a tilted camera; the cause is not
   established.** A 1 px line run as one segment across a large depth range reads a relative peak that sags in
   the middle (spread ~0.18), while the same line subdivided into 40 segments reads flat (spread ~0.003).
   Per-vertex scale evaluation is ruled out for the measured fixture: its across direction is perpendicular to
   the view axis, so the required offset is affine along the segment and linear interpolation reproduces it.
   Leads: re-measure with an across direction that has a view-axis component; check whether the scale sizes the
   offset's projected length while the visible width is the perpendicular distance (the shader README's
   "Open — `refPx` measures the wrong span for a directional probe" is a candidate); and note the sag sits in two
   adjacent samples, as consistent with a sampling miss as with a geometry error. **Fix the measurement before
   touching the shader.** No production layer is known to be affected: MVT lines are densely noded and a
   top-down map has little depth range per segment.
2. **A vertex-stage keyword under BRG is unverified.** The fill precedent (`_SURFACE_TYPE_TRANSPARENT`) is a
   fragment-stage keyword; `_EDGE_ANTIALIASING_OFF` changes the vertex stage. No test renders the toggle
   through the BRG backend.

---

## Constraints the implementation must respect

- **Shared HLSL across layer folders goes only through the one sanctioned include**, `../PixelsToWorld.hlsl`
  (`Shaders/README.md` § "Shared px→world include"). Any other shared HLSL (a pass, a coverage mechanism) is
  copied verbatim, never extracted.
- **Never name an internal shader property after a MapLibre style term.** The styler binds `line-X → _X` and
  silently overwrites a collision (`docs/lessons-learned.md` § "Shaders & HLSL"). Internal params go in the
  shader's internal render-params group so the boundary stays visible.
- **The AA ramp width is a compile-time constant.** The toggle is a boolean keyword, never a `Range`/float.
- **Registries, not raw strings.** Keyword names in `ShaderKeywords`; property names in the layered
  `PropertyNames`/`PropertyId` registry, with `PropertyId` deriving from `PropertyNames`. The registries carry
  exact-count assertions, so a new entry moves its count by exactly one.
- **Keyword sync is written in code**, in the GUI's `ValidateMaterial` override.
- **Single extrusion site:** the pad lives in `Line_VertexExtrude.hlsl`, so every pass keeps an identical
  silhouette. A pad in one pass's vertex path alone desyncs the passes, and so does a keyword declared in fewer
  than all passes or declared `_fragment`.
- **SRP Batcher:** `UnityPerMaterial` stays byte-identical across passes; a CBUFFER edit is mirrored in the
  CBUFFER, the `UNITY_DOTS_INSTANCING` block and the BRG SoA packing. AA needs no CBUFFER change — a keyword is
  not a CBUFFER member.
- **Build-variant stripping** is why the keyword is `_OFF`-polarity (see "The toggle").
- **`RibbonJob`'s geometry, `Side` included, is pinned directly** by `RibbonJobGeometryTests`; there is no
  second producer to hold it differentially.
