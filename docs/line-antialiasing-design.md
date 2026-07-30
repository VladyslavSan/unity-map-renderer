# Line antialiasing — design & SSOT

**Status: SHIPPED** (stages A1–A4). Line silhouettes render a strict one-device-pixel straddle centred on
the styled edge, behind the `_EDGE_ANTIALIASING_OFF` shader feature (§5.2); fills remain out of scope by
decision (§9.1).

> **Implementation disproved four claims in this document.** They are corrected in place below and flagged
> **[CORRECTED AT IMPLEMENTATION]** so nobody re-derives the wrong thing from the surrounding prose:
> §5.1's pad formula (`0.5 · pxToWorld` is wrong), §5.1's post-miter rationale (the failure direction was
> backwards), §7's T3a framing (the wrong implementation it names is unreachable in this shader), and
> §6.5's symptom statement (no cap segment rendered *at all* — the whole cap was NaN). §9.4 records the
> limits implementation surfaced.

`docs/meshing-design.md` §2 remains the compressed record of **why edge AA was removed** (commit `0b910c7`)
and is cited, not duplicated, here.

---

## 1. Problem statement

Map lines render with a **hard, aliased edge**. `Line_VertexExtrude.hlsl:257` sets `coverage = 1.0`
unconditionally; the styled silhouette is whatever the rasterizer's one-sample-per-pixel triangle test
produces. The sole thin-line safeguard is the min-width floor at `Line_VertexExtrude.hlsl:138-142`
(half-width ≥ 0.5 px ⇒ a stable 1 px hairline). Diagonal roads staircase.

Goal: **antialiased outer silhouettes on map lines, with the fill↔casing boundary of a cased road staying
crisp.** Both halves, or it is not a fix.

---

## 2. Why the removed AA failed — the precise mechanism

> **[CORRECTED AT IMPLEMENTATION — A6.0] The removed AA had TWO independent defects; this document
> recorded only one.** The compositing bleed analysed below is real and is what the straddle fixes. The
> second was never diagnosed: the ramp divided by `fwidth`, which is `abs(ddx) + abs(ddy)` — the L1/Manhattan
> length of the gradient, not its true L2 length. It over-reads by `|cos θ| + |sin θ| ∈ [1, √2]` with the
> silhouette's screen angle, so a 45° diagonal got a **1.41 px** ramp where an axis-aligned line got 1.00 px,
> and lost 0.41 px of ink with it. Every diagonal and curve in the frame was softer and thinner than the
> horizontals beside it — plausibly a large part of why the removed AA "looked bad" in a way the compositing
> analysis alone does not explain.
>
> **A3 reproduced the same expression, so the defect survived into the rebuild and shipped.** Measured on
> `b8329c9e` with a styled 12 px line: horizontal **12.003 px**, 45° diagonal **11.586 px** — the latter
> matching the predicted `W + 1 − √2 = 11.586` to three decimals, with an implied ramp width of exactly
> **1.414 px**. A6.0 switches both AA ramps to `length(float2(ddx(side), ddy(side)))`; the diagonal then
> reads **11.995 px** and the ramp **1.005 px**. `_Blur` and dash keep `fwidth` — they are separate style
> features, not antialiasing.


`docs/meshing-design.md` §2 (lines 120-162) is the record. Compressed:

- **The trilemma.** A shader-space alpha fade cannot hold {solid core, unchanged apparent width,
  antialiased} at once. Outset fattens; inset dims and thins; straddle softens thin lines.
- **A fade *width* is not a quality dial.** Correct coverage AA is a ~1 px transition. Widening it blurs; it
  never adds resolution.
- **The compositing complaint.** A cased road is two stacked transparent draws (`Queue=Transparent`,
  `ZWrite Off`, painter-ordered). The narrower fill's fade skirt ramps to *transparent* over the wider
  casing, so casing colour shows through the fill's edge pixels.

**The mechanism, stated precisely — this is what the whole design turns on.** Straight alpha blending
computes `dst = src·a + dst·(1−a)`. **Where `a == 1` there is no bleed at all** — the fill exactly replaces
the casing. Bleed exists only where the fill's alpha is < 1. So the removed model's real defects were:

1. **The fade lived *inside* the styled band.** S104 decision 2 made AA an **inset** feather eating inward
   over `_AaEdgeWidth` px (capped at 50 % of the half-width by decision 4). For a 2–3 px road fill that is a
   large fraction of the band at `a < 1` ⇒ casing colour visible well *inside* the fill. That is the mush.
2. **The outset variant padded geometry by the full fade width** while keeping `a == 1` out to the styled
   edge, so the line *rendered* fatter than styled and the fill sat on a wrong-width casing base
   (S104 §1, S77 §1).
3. **The fade was a tunable knob** (`_AaEdgeWidth`, and worse `aaEdgePixels = _AaEdgeWidth + _Blur`), so it
   was widened as a "quality dial" and produced multi-pixel transitions.
4. **Blended coverage can accumulate where two partial-coverage edges overlap.** Not fixed by geometry or
   tuning; quantified in §6.

Defects 1–3 are properties of *that implementation*, not of alpha-blended coverage AA. A **strict ±0.5 px
analytical straddle with no knob** has none of them: the 50 %-coverage contour sits exactly on the styled
edge (width unchanged), the interior is `a == 1` (no bleed), and the ramp is one device pixel by
construction (no dial to widen).

**And a ~1 px transition between fill colour and casing colour is not mush — it is what a correctly
antialiased boundary looks like.** Tooth T2 (§7) permits ≤ 1 px for exactly this reason.

---

## 3. What the code actually looks like

| Fact | Citation |
|---|---|
| One global draw index over ALL painted layers; `renderQueue = 3000 + drawIndex` | `Rendering/Style/RenderLayerSet.cs:90-100`, `Core/Rendering/LayerDrawOrder.cs:55` |
| **Fills and lines MUST share one transparent band** — the opaque queue drains entirely before the transparent queue, so a fill declared above a line would render *under* it | `Core/Rendering/LayerDrawOrder.cs:19-29` (explicit, load-bearing) |
| One mesh per `(tile, layer)`; one processor per layer, dense per source | `Rendering/Tile/TileManager.cs:1560-1580` |
| A processor selects features for **exactly one** `StyleLayer` | `Rendering/Tile/Processing/TileMeshLayerProcessor.cs:48` |
| A layer owns **one** material; per-layer uniforms bound by name (`line-X → _X`) | `Rendering/Style/LineRenderLayer.cs:70-78`, `Rendering/Materials/MaterialFactory.cs:172+` |
| Line vertex format: 4 streams, one colour + one widthScale per vertex | `Rendering/Meshing/StyledLineTileBuilder.cs:84-92, 288-292` |
| `side` is a per-vertex ±1 baked into the mesh and interpolated; `LineCoverage` keys on `\|side\|` | `Jobs/LineRibbonJob.cs:489-498`, `Line_VertexExtrude.hlsl:222, 247-268` |
| Ribbon is one continuous shared-vertex strip — consecutive primitives thread `leftPrev`/`rightPrev` | `Jobs/LineRibbonJob.cs:148-149, 190-191, 202-203, 287-288, 302-303, 360-361` |
| `LineVaryings.uv` is a **fully packed `float3`** (dashU/side/innerFrac) — no spare component | `Line_LitForwardPass.hlsl:53-56` |
| **Five** passes, and **all five** `#include "Line_VertexExtrude.hlsl"` | `Line.shader:168, 258, 290, 320, 352` (passes) / `:247, 281, 311, 342, 407` (includes) |
| `shader_feature_local` is the established keyword idiom; the vertex-affecting ones are plain `shader_feature_local`, the fragment-only ones `_fragment` | `Line.shader:181-194, 273, 304, 333, 368-375` |
| An `_OFF`-polarity keyword driven by a plain **`[ToggleUI]`** float is already in this shader — `[ToggleUI]` is UI-only and attaches **no** keyword | `Line.shader:96` (`[ToggleUI] _ReceiveShadows`) |
| Keywords are synced **explicitly in code**, in the GUI's `ValidateMaterial` override — this codebase deliberately does not rely on Unity's keyword-attaching drawers | `Editor/ShaderGUI/BaseShaderGUI.cs:120-122`, `LitShaderGUI.cs:23-45`; rationale in `Materials/ShaderKeywords.cs`'s XML doc |
| Keyword names live in one registry | `Rendering/Materials/ShaderKeywords.cs:23` (`ReceiveShadowsOff`) |
| An editor-only Properties-block float that drives a keyword is **not** a CBUFFER member | `_ReceiveShadows` is absent from `Line_LitInput.hlsl`'s CBUFFER; it lives in the shared `ShaderProperties/PropertyNames.cs:54` |
| Structure guards: no raw-string Material API under `Assets/Code/MapRenderer.Unity/**`; `PropertyId` files derive from `PropertyNames`; registry files carry **exact-count** assertions | `Tests.EditMode/Structure/NoRawStringMaterialAccessGuardTests.cs:139, 174`; `MaterialPropertyRegistryParityTests.cs:70, 90` |
| Style→shader binding is an **explicit allowlist** of `PropertyId` constants — no reflection, no name-derived loop | `Rendering/Materials/MaterialFactory.cs:177-239` |
| Per-layer materials are `new Material(source)` clones (+ `parent` in Editor) — the copy ctor carries the source's keyword set | `Rendering/Materials/MaterialExtensions.cs:23-30` |
| A tweaker enabling a `shader_feature_local` keyword at runtime on a cloned per-layer material is **existing, load-bearing practice** | `Rendering/Materials/FillTweaker.cs:43` (`_SURFACE_TYPE_TRANSPARENT`) |
| Both mesh backends register **whole `Material` objects**; the BRG SoA carries per-instance *properties*, never keywords | `Backend/BRG/TileRenderer.cs:117`, `Backend/Entities/TileRenderer.cs:187` |
| px→world scale is measured per-vertex, per-direction, foreshortening-correct | `Line_VertexExtrude.hlsl:42-65` (`MapPixelsToWorld`) |
| `LineRibbonVertex.Position` is the raw centerline point — the entire styled width is applied in the vertex shader, never in mesh positions | `Jobs/LineRibbonJob.cs:489-498` |
| Line geometry is **not clipped to the tile boundary** — a decoded feature is ribboned as-is | `Rendering/Meshing/StyledLineTileBuilder.cs:206-308` (no clip step) |
| The Burst job and the managed `LineTessellator` are held to **exact differential parity**, `Side` included | `Tests.EditMode/LineRibbonJobTests.cs:94-112` (`AssertParity`) |
| MSAA is off project-wide | `Assets/Settings/RPAsset.asset:28` (`m_MSAA: 1`) |

### 3.1 The real style: what `_casing` actually means

`Assets/StreamingAssets/Fixtures/liberty.json` (111 layers, 67 line layers) is the production style; the demo
and the winding/boundary tests load it. All verified against the file:

- **The `_casing` suffix is a real convention** — 22 of 67 line layers carry it, in three parallel families
  (`tunnel_*`, `road_*`, `bridge_*`).
- **Casings are NOT drawn adjacent to their fill.** In the road family, layers 42–48 are *all seven casings*
  and 50–56 are the fills, with `road_path_pedestrian` (49) interleaved between the two blocks. The block
  ordering is deliberate: every casing draws under every fill, so at a junction a minor road's white fill
  (53) covers the motorway's orange casing (48) instead of being cut by it. Same structure in `tunnel_*`
  (22–28 / 29–40) and `bridge_*` (63–70 / 71–78).
- **Filters differ between "paired" layers.** `road_minor_casing` (45) carries an extra
  `["!=", ["get","ramp"], 1]` clause that `road_minor` (53) does not; `road_secondary_tertiary_casing` (46)
  likewise. `road_motorway_casing` (48) / `road_motorway` (56) *do* match exactly. There is no single rule.
- **Name-based pairing is wrong on real data.** `tunnel_street_casing` (25) filters
  `class ∈ {street, street_limited}`; its only plausible partner `tunnel_minor` (33) filters
  `class ∈ {minor}` — **disjoint**. Same for `bridge_street_casing` (66) / `bridge_street` (75). Three
  casings have no same-stem fill; `road_path_pedestrian` (49) and `tunnel_path_pedestrian` (29) are fills
  with no casing.
- **Paired layers disagree on paint.** `road_motorway_casing` has a constant `line-color`, `road_motorway`'s
  is a zoom interpolation; `road_minor_casing` has a zoom `line-opacity` fade over z12→12.5 its fill lacks;
  several `tunnel_*_casing` layers carry a `line-dasharray` their fill does not. At z13.5–14 several fills
  evaluate to **0** width while their casing is 1–4 px.
- **Join/cap distribution:** 44 of 67 line layers request `line-join: round`, 23 take the `miter` default
  (`Core/Style/Line/LayoutProperties.cs:64`); **15 request `line-cap: round`**, 52 take the `butt` default
  (`:71`). Exactly one layer uses `line-gap-width` (`waterway_tunnel`, butt cap); no layer sets `line-blur`.
  These last two facts matter for §6.5.

Two other styles are loaded by tests: `openfreemap-style.json` has one `road-casing`;
`maplibre-demo-style.json` has **no** casing layers at all.

---

## 4. Rejected directions

### 4.1 Rejected — fuse the casing + fill into one draw

*(`meshing-design.md` §2 direction 1.)*

- **Pairing predicate: unsound.** The `_casing` suffix is a real convention (§3.1) but name-matching
  mis-pairs `tunnel_street_casing`/`tunnel_minor` and `bridge_street_casing`/`bridge_street` onto *disjoint*
  filters. A MapLibre-correct predicate needs **per-feature evaluation of both filters**; filter subsumption
  is not decidable statically over arbitrary expressions, and `TileMeshLayerProcessor.cs:48` selects for
  exactly one `StyleLayer`.
- **Unpaired layers.** 45 of 67 line layers are unpaired, so both a fused and an unfused path must exist and
  stay consistent forever.
- **Draw order — the decisive cost.** Fusing collapses two slots into one, and the style deliberately
  interleaves other layers between the casing block and the fill block (§3.1). Fusing motorway at slot 56
  puts its casing above every other road fill; fusing at 48 puts its white fill below every other road fill.
  **Neither reproduces the declared compositing**, and this is the arrangement every OpenMapTiles-derived
  style uses. `line-sort-key` would compound it.
- **Disagreeing paint properties.** One material carries one `_Width`, `_BaseColor`, `_Opacity`,
  `_DashArray`/`_DashCount`, `_LineOffset`, `_LineTranslate`, `_Blur`. Fusion needs two of each ⇒ a second,
  non-colliding naming scheme for every style-bound line property (straight into the `_Blur`/`line-blur`
  collision hazard), a prefix-parameterized `BindLinePaintToApplier`, two `ZoomStyleApplier`s, and a second
  per-vertex colour and widthScale. A dashed casing under a solid fill needs two dash walks in one fragment.

**Rejected:** invasive across five shipped subsystems and it regresses junction rendering by construction.

### 4.2 Rejected — replace-compositing (opaque queue, or blend-off + alpha-to-coverage)

*(`meshing-design.md` §2 direction 2.)* Dead on two **locked maintainer rulings**:

- **No depth. LOCKED.** Map layers do not ZWrite and do not depth-test; painter order is the render-queue
  band. This is independent of AA and is not re-litigated here. (It also rules out the §2 wording "opaque
  queue, ZWrite On, depth-offset off the fills" — which `LayerDrawOrder.cs:19-29` would have broken anyway:
  Unity drains the entire opaque queue before the entire transparent queue, so lines below 2500 would render
  under every fill regardless of declared order.)
- **No MSAA. LOCKED.** The maintainer has empirically tried plain MSAA up to **16×** in this project and saw
  no meaningful improvement — consistent with §2's own claim that MSAA antialiases coverage while the layers
  still blend. Recorded, not re-litigated (§8).

Since alpha-to-coverage *requires* MSAA (with one sample it degenerates to a binary threshold), the whole
replace-compositing family goes with it. *(Note for a future reader: `_AlphaToMask` is declared and wired on
`Line.shader:92,158` and `Fill.shader:92,153` but never set. It was the mechanism this rejected direction
would have used. It is not part of the recommendation and setting it without MSAA does nothing.)*

### 4.3 Considered and rejected — manual supersampling

Rendering at 2×/4× into an offscreen target and box-downsampling is nominally compatible with both rulings
(no hardware MSAA, no depth). It is rejected for the same reason MSAA is: **the problem was never sampling
density.** Transparent layers still blend rather than replace, so SSAA reproduces the same compositing at a
strictly higher cost (a full extra render target plus a downsample pass) than the 1-sample analytical
straddle achieves exactly.

---

## 5. Recommendation — strict analytical straddle, blended

**One device pixel of true analytical coverage, straddling the styled edge, with no tunable width. Keep
straight-alpha blending, the transparent queue band, `ZWrite Off`, and every existing paint property.**

- Coverage ramps **linearly from 1 to 0 across exactly one device pixel centred on the styled edge** —
  0.5 px inside, 0.5 px outside. The 50 % contour sits exactly on the styled edge, so **apparent width is
  unchanged**. Interior coverage is exactly 1, so **there is no bleed inside the band** (§2).
- The ramp width is a **compile-time constant, not a material property.** No `_AaEdgeWidth` successor,
  nothing bindable, nothing for a later reader to widen. This kills defects 2 and 3 structurally rather than
  by discipline. (AA as a whole is switchable on/off — see §5.2 — but its *width* is not.)
- `_Blur` (`line-blur`) stays exactly as it is — a separate, opt-in, multiplicative soft-edge mask, default 0
  (`Line_VertexExtrude.hlsl:263-268`). It is not AA and must not be folded into the ramp; that conflation was
  S104's `aaEdgePixels = _AaEdgeWidth + _Blur` bug.
- The min-width floor (`:138-142`), dash coverage, `line-offset` and `line-translate` are untouched.

**Because MSAA and depth are both out, `line-opacity` and `line-blur` stop being problems entirely.**
Straight-alpha blending keeps working for both: a layer at `line-opacity: 0.75` simply multiplies its
coverage, exactly as today. The per-layer opacity fallback an earlier draft of this doc needed for
replace-compositing **dissolves** — there is one render-state regime. That is a real simplification and a
reason to prefer this direction beyond the maintainer's rulings.

**The honest cost, recorded rather than buried: this epic leaves the map half-antialiased.** Lines come out
smooth; **fills stay aliased**, and will until a separate epic lands — which is a decided outcome, not an
oversight (§9.1). Fills cannot follow this mechanism: they have no `side`-equivalent distance field, and
earcut's internal edges mean a per-triangle fade would cross-hatch every polygon. So the visible result of
shipping A1–A5 is smooth roads and boundaries against still-jagged water, landuse and building edges. The
maintainer should expect exactly that at the A5 eyeball and not read it as a defect in this work.

### 5.1 The geometry change — a fixed 0.5 px pad (do not skip this)

A straddle needs half its ramp **outside** the styled edge, and the rasterizer only produces fragments where
a triangle covers a pixel centre. So the ribbon must be extruded **0.5 device pixels past the styled
half-width**, or the outward half of the ramp has nowhere to land and the result silently degenerates into
the inset that failed (defect 1).

This is **not** the S70 outset failure. S70 padded by the full *tunable* `aaPx` **and** kept coverage 1 out
to the styled edge, so the line rendered fatter. Here the pad is a **fixed 0.5 px** and the 50 % contour
stays on the styled edge, so apparent width is unchanged. The distinction is the whole design; a developer
who reads "analytical coverage" as fragment-only will produce the broken variant. Tooth T2b guards it.

#### The exact formulation (do not re-derive it at implementation time)

Let `styledHalf` be today's `max(miter · outerWorld, minHalfWorld)` expression at
`Line_VertexExtrude.hlsl:142`, and `paddedHalf = styledHalf + 0.5 · MapPixelsToWorld(centerWS, unitDir_WS)`.
Extrude by `paddedHalf`.

> **[CORRECTED AT IMPLEMENTATION]** This originally read `paddedHalf = styledHalf + 0.5 · pxToWorld`, which
> is **wrong**. `pxToWorld` is a unit-CONVERSION factor, not a metres-per-pixel scale:
> `Line_VertexExtrude.hlsl` sets it to a literal `1.0` whenever the width is already in world metres. So
> `0.5 · pxToWorld` pads a world-unit layer by **half a metre** — about 1.8 px at the snapshot camera —
> while looking perfectly correct on a pixel-width layer, which is every production layer
> (`MaterialFactory.cs:193` sets `_WidthIsPixels = 1` unconditionally). The pad must always be MEASURED.
> Injecting the bug made T2b's world-unit variant read **8.659 px against a styled 6.0** while its
> pixel-width variant still passed at 6.003 — which is precisely why T2b is parameterised over both width
> modes.

`side` stays baked ±1 exactly as today. **No new vertex attribute, no new interpolator, no CBUFFER change,
and no new float uniform** — the only addition to the material surface is §5.2's boolean keyword, which is
not a CBUFFER member. This is worth stating flatly because `LineVaryings.uv` is already a fully packed
`float3` (`Line_LitForwardPass.hlsl:53-56`), so a developer who assumes the shader needs the styled-edge
threshold as a *separate quantity* will reach for a new varying threaded through all five pass structs —
a needless footprint expansion, and the single most likely way the "minimal footprint" claim gets broken.
It is unnecessary, because:

- After padding, `|side| == 1` is the padded edge, and the styled edge sits at `t = styledHalf / paddedHalf`.
- `fwidth(side)` is the side-units-per-device-pixel derivative, i.e. `≈ metresPerPixel / paddedHalf`.
- Therefore `1 − t = (paddedHalf − styledHalf)/paddedHalf = 0.5·metresPerPixel/paddedHalf = 0.5·fwidth(side)`,
  so **`t = 1 − 0.5·fwidth(side)` exactly** — the threshold is a function of `side` and its own screen-space
  derivative, nothing else.
- The one-device-pixel ramp centred on `t` therefore collapses to a single line:

  ```hlsl
  coverage = saturate((1.0 - abs(side)) / max(fwidth(side), 1e-6));
  ```

  `|side| = 1` (padded edge) ⇒ 0; `|side| = 1 − fwidth` ⇒ 1; `|side| = t` ⇒ **exactly 0.5**, on the styled
  edge. It composes multiplicatively with `_Blur` and dash exactly as coverage does today, and it is the
  same self-referencing `fwidth` pattern the existing `_Blur` smoothstep already uses (`:266-267`).

This holds identically whether `styledHalf` came from the miter branch or the min-width-floor branch, and it
holds per-fragment on a trapezoidal quad whose two ends have different `pxToWorld` — `fwidth` is inherently
local, so no global closed form is needed.

#### Two implementation-order hazards

- **Add the pad to `outerWorld`, BEFORE the miter multiply — not to the post-miter result.** The miter factor
  is `1/cos(θ/2)` (`ComputeMiterNormals`, `LineRibbonJob.cs:234-247`), defined so that the *perpendicular*
  distance from the centreline equals `outerWorld` regardless of `outerWorld`'s value. Scaling
  `outerWorld → outerWorld + pad` and re-applying the same multiply keeps the perpendicular pad at exactly
  0.5 px, at any corner.

  > **[CORRECTED AT IMPLEMENTATION]** This bullet used to say a post-miter pad "fattens at sharp corners".
  > That is **backwards**. A pad added after the multiply gives a lateral magnitude `miter·outer + pad`,
  > whose perpendicular projection is `outer + pad·cos(θ/2)`; since `cos(θ/2) < 1` at a corner, the
  > perpendicular pad **shrinks toward zero as the corner sharpens** — the ramp loses its landing room
  > exactly where geometry is tightest. Pre-miter is still the correct choice and is corner-invariant; only
  > the stated symptom was wrong, and it would have sent someone debugging a corner artifact looking for
  > fattening instead of thinning.
- **`innerFrac` must be re-derived against the padded outer.** It is currently
  `0.5 · gapWorld / outerWorld` (`:219`); with `|side| = 1` now at `paddedHalf`, leaving it unchanged shifts
  the hollow/cased line's gap hole. The inner edge is also a real silhouette, so it should get the same 1 px
  ramp rather than staying a hard `step` — a small in-stage call, but the re-derivation itself is mandatory
  for correctness.

#### Confirmed non-hazards

- **Grazing angles / globe.** `MapPixelsToWorld`'s `max(refPx, 0.1)` clamp (`:64`) is a documented real limit
  ("the offset falls SHORT of the styled pixel count"), **pre-existing** — it already affects styled width
  today. Because the ramp is `fwidth`-driven rather than computed from a baked CPU ratio, its *shape*
  self-corrects to whatever `pxToWorld` the fragment actually sees; it does not compound the pre-existing
  width error into a second, independent AA defect.
- **Mesh bounds.** `LineRibbonVertex.Position` is the raw centerline point (`:489-498`) — the entire styled
  width is applied in the vertex shader and never appears in mesh positions. So bounds must already
  accommodate the full un-padded styled width (a pre-existing necessity unrelated to this epic), and an extra
  fixed 0.5 device px is immaterial. If bounds are *not* widened for the styled width today, that is a
  pre-existing gap, out of scope here and not worsened by the pad.
- **Vertex/index counts** are unaffected by a shader-side pad (`MaxVertexCount`/`MaxIndexCount`,
  `LineRibbonJob.cs:74-99`). §6.5's fix does change them; that is accounted for there.

### 5.2 AA is switchable on/off — a boolean shader feature (maintainer amendment)

AA sits behind a **shader feature toggled in the material Inspector**, surfaced by `LineShaderGUI.cs`.

**This does not reopen §5's "no knob".** The failure mode §5 guards against is a tunable ramp **width**
(`_AaEdgeWidth`, and worse `aaEdgePixels = _AaEdgeWidth + _Blur`) — a dial that got widened as a "quality"
setting and produced multi-pixel blur instead of antialiasing. This is a **boolean**, and a boolean cannot be
widened. The ramp width remains a compile-time constant.

> **Hard constraint.** The toggle is a **`[Toggle…]` keyword, never a `Range`/float.** If a future change
> wants to expose the ramp width as a number, that is a *rejection of §5* and must be argued on its own
> merits — it is not an incremental extension of this toggle.

**Prior art — the mechanism, not the approach.** The parked stash (`stash@{0}`) implemented exactly this
switch: `[Toggle(_LINE_AA_OUTSET)] _AaOutset ("AA Outset Margin", Float) = 1.0` plus
`#pragma shader_feature_local _LINE_AA_OUTSET` declared in **all five** passes, the four non-forward ones
carrying the comment *"shared extrusion silhouette (see ForwardLit)"*. The **AA approach** it toggled is the
ruled-out outset — do not resurrect that. The **toggle mechanism** is proven in this exact shader; copy it.

#### Settled design points

1. **Declared in every pass that includes `Line_VertexExtrude.hlsl` — all five.** Confirmed: `ForwardLit`,
   `ShadowCaster`, `DepthOnly`, `DepthNormals`, `GBuffer` (`Line.shader:168, 258, 290, 320, 352`), each
   including the helper (`:247, 281, 311, 342, 407`). The 0.5 px pad is **vertex-side**, so a keyword present
   in some passes and absent in others gives those passes different silhouettes — a direct **S67 violation**.
   The stash's five-pass declaration is the pattern. Corollary: it must be plain `shader_feature_local`, not
   `shader_feature_local_fragment` — the `_fragment` variant strips the keyword from the vertex stage, which
   would desync the pad exactly the same way. (`Line.shader` already keeps this distinction: `_NORMALMAP` and
   `_RECEIVE_SHADOWS_OFF` are plain, the fragment-only ones carry `_fragment`.)
2. **`shader_feature_local`, not `multi_compile`** — matches the file's existing idiom (`:181-194`) and keeps
   variants per-material. **Honest cost:** it doubles the line shader's variant count across all five passes
   for every combination of the existing keywords. That is the real price of the toggle, and it is accepted
   because the alternative (`multi_compile`) would compile both variants unconditionally for every material
   in the project.
3. **A keyword is not a CBUFFER member**, so `UnityPerMaterial` stays byte-identical across passes and §10's
   SRP Batcher constraint holds. The doc's "no CBUFFER change" claim survives intact; §5.1's claim is now
   precisely *"no new float uniform, no new interpolator, one boolean keyword"*.
4. **Polarity: `_OFF`, defaulting to AA ON.** This is not cosmetic — it is a build-correctness issue.
   `FillTweaker.cs`'s own doc records the rule: **`shader_feature_local` variants are stripped from player
   builds unless a material in the build declares the keyword.** With positive polarity (`…_AA_ON`, the
   stash's choice) the *shipping* variant is the one at risk of being stripped, and AA would work in the
   Editor and silently vanish in a build. With `_OFF` polarity the default variant carries no keyword and can
   never be stripped; only the opt-out variant depends on a material declaring it, which is correct.
   URP uses this polarity throughout (`_SPECULARHIGHLIGHTS_OFF`, `_ENVIRONMENTREFLECTIONS_OFF`,
   `_RECEIVE_SHADOWS_OFF`), and so does this shader — but note *how*, per point 5.
5. **Declaration: a plain `[ToggleUI]` float, keyword synced in code — NOT a keyword-attaching drawer.**
   This codebase deliberately does not use Unity's `[Toggle(KEYWORD)]` / `[ToggleOff]` drawers to attach
   keywords. The in-file precedent is `Line.shader:96` — **`[ToggleUI] _ReceiveShadows("Receive Shadows",
   Float) = 1.0`**, a UI-only drawer on a plain float that attaches *no* keyword — with the keyword set
   explicitly at `BaseShaderGUI.cs:120-122`:

   ```csharp
   if (material.HasProperty(ShaderProperties.PropertyId.ReceiveShadows))
       CoreUtils.SetKeyword(material, ShaderKeywords.ReceiveShadowsOff,
           material.GetFloat(ShaderProperties.PropertyId.ReceiveShadows) == 0f);
   ```

   `ShaderKeywords.cs`'s own XML doc states the rationale: these names are *"derived from a material's
   property values by the editor keyword sync (`BaseShaderGUI.ValidateMaterial` + `LitShaderGUI.ValidateMaterial`)
   — the clean-room replacement for URP's editor-only `SetMaterialKeywords`."* Follow that idiom.
   **Copy the `[ToggleUI]` + `ValidateMaterial` pattern; do not use `[Toggle(...)]`/`[ToggleOff]` to carry the
   keyword.** (The stash used `[Toggle(_LINE_AA_OUTSET)]` — its *five-pass `#pragma` placement* is the part to
   copy, not its drawer.)
6. **Name: `_EdgeAntialiasing` (property, default `1.0`) ↔ `_EDGE_ANTIALIASING_OFF` (keyword).** It reads
   positively in the Inspector, defaults on, and goes in the shader's **(B) Internal render params** group
   beside `_WidthIsPixels`. Collision-safety, per §10's rule, is established two ways: (a) no MapLibre
   paint/layout term is `line-edge-antialiasing`, so the conventional `line-X → _X` mapping cannot produce
   this name; and (b) structurally — `BindLinePaintToApplier` (`MaterialFactory.cs:177-239`) is an **explicit
   allowlist** of `PropertyId` constants with no reflection and no name-derived loop, so a style layer cannot
   reach a property that is not in that list. **Do not reuse `_AaEdgeWidth`** — that was the deleted *width
   dial*, and reviving the name would misrepresent a boolean as the thing §5 rejects.

   **It is a Properties-block float only — NOT a CBUFFER member.** Nothing in HLSL reads it; only the keyword
   is read. `_ReceiveShadows` is exactly this shape (absent from `Line_LitInput.hlsl`'s CBUFFER, present in
   the shared `ShaderProperties/PropertyNames.cs:54`). This keeps `MaterialPropertyRegistryParityTests`'
   `LineCbufferCount_IsExactly24` untouched and makes §5.1's "no CBUFFER change" claim exact rather than
   approximate. Do **not** add it to the CBUFFER, the `UNITY_DOTS_INSTANCING` block, or the BRG SoA.
7. **Registry entries are mandatory, and one is gate-enforced while the other is not.** Both go in the
   registries, never as raw strings:
   - `Rendering/Materials/ShaderKeywords.cs` — `EdgeAntialiasingOff = "_EDGE_ANTIALIASING_OFF"`, in a new
     *Line-only features* group (the file already separates "Lit shading-model" from "Fill-only").
   - `Rendering/ShaderProperties/Line/PropertyNames.cs` — `EdgeAntialiasing = "_EdgeAntialiasing"`, plus the
     matching `Line/PropertyId.cs` entry deriving from it (`PropertyIdFiles_NoBareLiterals`,
     `NoRawStringMaterialAccessGuardTests.cs:174`, requires the derivation).

   **What the gate actually catches** — stated precisely, because the difference decides what review must do
   by hand. `NoRawStringMaterialApiCalls_InUnityAssembly` (`:139`) matches
   `\.(Set|Get)(Float|Color|Vector|Int|Texture)\s*\(\s*"` and `\.HasProperty\s*\(\s*"` over every `.cs` under
   `Assets/Code/MapRenderer.Unity/**` — which **does** cover `LineShaderGUI.cs`, and **does** catch a raw
   `material.HasProperty("_EdgeAntialiasing")` or `material.GetFloat("_EdgeAntialiasing")`. It does **not**
   match `CoreUtils.SetKeyword(material, "_EDGE_ANTIALIASING_OFF", …)` — a raw *keyword* literal is not a
   Material API call and slips through. So the `ShaderKeywords` entry is a **convention the reviewer must
   check**, not a gate-enforced one.

   **Two registry count tests move, legitimately.**
   `MaterialPropertyRegistryParityTests.LinePropertyNamesCount_IsExactly10` (`:70`) must become `11`, and
   `Line/PropertyNames.cs`'s XML doc — which currently describes itself as the names appearing in
   `Line_LitInput.hlsl`'s CBUFFER — must be amended to admit one editor-only keyword driver. This is an
   **expected-value update for a registry that legitimately grew**, categorically different from re-baking a
   rendered snapshot (§7's "never re-bake" rule): the bound is exactly +1, in exactly one file, and the
   reviewer should reject any larger drift. If a subset/parity assertion tying `Line/PropertyNames.cs` to the
   Line CBUFFER exists beyond the count, the property belongs in the shared registry instead — check before
   choosing.
8. **`LineShaderGUI.cs` — real sync work, and a false doc comment.** Its XML doc currently asserts *"The line
   declares no extra shader-feature keywords, so it inherits `LitShaderGUI`'s full keyword sync unchanged."*
   Both halves stop being true: the line now declares one, **and it needs its own sync override**.
   `LineShaderGUI` currently overrides only `RegisterMiddleScopes`; the sync hook is the virtual
   `ValidateMaterial`, which `BaseShaderGUI` declares (`:97`) and `LitShaderGUI` already extends (`:23`,
   calling `base.ValidateMaterial` first). The developer adds the third link:

   ```csharp
   /// <summary>
   /// Keyword sync at the line level: the base + Lit sets, then the line's own edge-AA feature.
   /// Mirrors BaseShaderGUI's ReceiveShadows pattern — an editor-only [ToggleUI] float drives an
   /// _OFF-polarity keyword, so the default 1 leaves the keyword clear and AA on.
   /// </summary>
   public override void ValidateMaterial(Material material)
   {
       base.ValidateMaterial(material);

       if (material.HasProperty(ShaderProperties.Line.PropertyId.EdgeAntialiasing))
           CoreUtils.SetKeyword(material, ShaderKeywords.EdgeAntialiasingOff,
               material.GetFloat(ShaderProperties.Line.PropertyId.EdgeAntialiasing) == 0f);
   }
   ```

   (`CoreUtils` needs `using UnityEngine.Rendering;`; `ShaderKeywords` is already in scope via the file's
   `using MapRenderer.Unity.Rendering.Materials;`.) Plus the property row in `DrawLineInputs`'s (B) group
   beside `WidthIsPixels`. **Without this override the toggle is completely inert** — the drawer attaches
   nothing.
   *(Adjacent fix while editing that comment: its `<see cref="LineMaterialTweaker"/>` is a **stale
   reference** — no such type exists. The class is `LineTweaker`; only a test file aliases the old name.)*
9. **Default state and the clone path.** `MapLine.mat` carries the property at `1.0` and **no** keyword
   (= AA on). `CloneWithParent` is `new Material(source)` (`MaterialExtensions.cs:23-30`), and Unity's copy
   constructor carries the source's keyword set, so every per-layer clone inherits the base state — no
   `LineTweaker.ApplyPainterContract` change is required for the default. `FillTweaker.cs:43` is the
   precedent for a tweaker asserting a keyword when one is needed; with `_OFF` polarity there is nothing to
   assert, which is one more reason to prefer it.
10. **The toggle gates BOTH the geometry pad and the fragment ramp.** Off ⇒ no pad *and* no ramp. Gating only
   the ramp leaves lines 1 px fatter with a hard edge — the worst of both, and precisely the S70 outset
   artifact. Gating only the pad leaves the ramp with nowhere to land — the failed inset. T4 is the guard.

#### Backend behaviour — verified, with one residual

Both mesh backends register **whole `Material` objects** and address them by id:
`BrgTileRenderer` calls `_brg.RegisterMaterial(mat)` (`Backend/BRG/TileRenderer.cs:117`), Entities calls
`_eg.RegisterMaterial(_layerMaterials[i])` (`Backend/Entities/TileRenderer.cs:187`). A keyword is
**per-material state carried by the Material object**, not per-instance data, so:

- **It never touches the BRG SoA.** The SoA packs per-instance *properties* (`_BaseColor`, `_Width`, …);
  there is nothing to add there, and no `FloatsPerInstance`/`MetaCount` change.
- **It cannot split batches**, because the toggle is uniform across every instance drawn with a given layer's
  material — each layer already has its own material and its own `BatchMaterialID`. Two layers disagreeing
  about AA is already two materials, which is already two ids.

**Residual to confirm empirically at implementation** (stated rather than assumed): that the BRG draw path
honours a `shader_feature_local` keyword on a registered material. The evidence says yes — this repo already
depends on it: `FillTweaker.ApplyPainterContract` enables `_SURFACE_TYPE_TRANSPARENT` at runtime on every
cloned per-layer fill material, and its own doc states that without the keyword "fills render solid however
transparent their colour, opacity or pattern says they are." Fill transparency demonstrably works under the
BRG backend, so the mechanism is already proven in this codebase. Confirm it holds for a *vertex-stage*
keyword (the fill precedent is `_fragment`) before relying on it.

**The final verdict on this epic is a maintainer eyeball in the demo.** Headless snapshots are necessary and
not sufficient — S104 shipped with exactly that caveat, leaving the globe grazing-horizon behaviour and the
"total width unchanged" look to the maintainer. **The primary A/B is toggling `_EdgeAntialiasing` on and off
on the live material** (§5.2 exists partly to make that possible). Scenes: cased roads at z13–16 (junctions,
bridges, tunnels), the z12→12.5 `road_minor_casing` fade, dense same-layer junctions, **a round-capped line
end** (§6.5), and a grazing-horizon globe view.

---

## 6. Defect 4 and the round-cap tagging bug

Blended coverage AA accumulates where **two partial-coverage edges of the same layer overlap over
background**: two pixels each at 50 % that should compose to full coverage instead read `2c − c² = 0.75`.
This section is the verification, and §6.5 is the one place the first pass of this doc got it wrong.

### 6.1 Verified: joins do not self-overlap or seam

- **`side` reaches ±1 only at the ribbon's lateral boundary**, so coverage keyed on `|side|` fades **only at
  the silhouette**, never at an internal triangle boundary.
- **Miter join** (`LineRibbonJob.cs:184-192`): two shared vertices `miterL`/`miterR` and one quad;
  `leftPrev`/`rightPrev` thread forward. One continuous strip, no overlap.
- **Bevel join** (`:266-305`): a fan about a single shared inner vertex. Every shared **edge** has endpoints
  `(+1, −1)`, so the interpolated `|side|` field is C⁰-continuous across it. No overlap, no seam.
- **Round join** (`:307-362`) — **the dominant path: 44 of 67 liberty line layers request
  `line-join: round`**. Same fan-about-a-shared-inner-vertex structure; every shared edge is `(+1, −1)` or
  `(arc, inner)`. No overlap, no seam. Critically, the join **manufactures fresh flank vertices at one
  consistent sign** (`leftTurn ? +1 : −1` throughout, `:327-355`), so every arc edge on the silhouette has
  constant `|side| = 1`. That is exactly what §6.5's cap fails to do.
- **Round cap — the T-junction where the cap meets the main ribbon quad is fine.** The centre pivot is
  `MakeVertex(p, double3.zero, …, side 0f)`: no extrusion, exactly on the centreline. The quad's single edge
  `leftButt→rightButt` faces the cap's two edges meeting at the pivot, but the pivot is the geometric
  midpoint of that chord **and** `side 0` is the midpoint of `±1`, so position and `|side|` agree along the
  shared line. No crack. **This much of the original claim holds; the cap's own internal arc tagging does
  not — see §6.5.**
- **Square cap** (`:380-386`, `:437-445`): `across ± along` gives a √2-length extrude vector consumed by the
  shader's miter factor; sides stay ±1. Nothing special.

### 6.2 Same-layer overlap is *approximately*, not exactly, self-cancelling

`dst = src·a + dst·(1−a)` with `src == dst` yields exactly `dst`. Within one layer every feature paints the
same **albedo**, so an edge band landing on another same-layer feature's interior is very nearly invisible —
the T-junction case, where a stem's cap edge sits inside the through-road's interior, produces essentially
no artifact.

**This is an approximation, not an algebraic identity.** `Line_LitForwardPass.hlsl` is a genuine forward-lit
PBR pass (`UniversalFragmentPBR`, real `viewDirectionWS`, specular and environment-reflection terms), so two
same-layer fragments at different world positions share albedo but not necessarily the exact final lit
colour — `viewDirectionWS` and hence specular response vary per fragment. In practice the residual is small
(default `_SpecColor` is a dim `(0.2,0.2,0.2)`, default `_Smoothness` 0.5, and map views are near-top-down
with modest per-fragment view-angle change across a junction), which is consistent with §6.4's
"measurable but minor" verdict and does not change it. It is stated here at the accuracy it actually has.

### 6.3 Where defect 4 genuinely arises, and how much

| case | reach | magnitude |
|---|---|---|
| **Junction crotch** — two same-layer features meeting at an angle, silhouettes converging over background | a wedge of ~1–3 px at each junction node | up to ~25 % under-coverage (reads 0.75 where 1.0 is correct) — a faint light notch |
| **Cross-tile duplicate geometry** — MVT tiles carry a buffer and this pipeline does **not** clip to tile bounds (`StyledLineTileBuilder.cs:206-308`) | the buffer strip along every tile boundary | the edge reads `2c − c²` instead of `c` ⇒ the 50 % contour shifts outward by ~0.25 px. **Sub-pixel.** The duplication is **pre-existing** — already visible today on any `line-opacity < 1` layer — AA only adds this sub-pixel edge-position error |
| **Short-segment fold** — a miter or bevel quad folds when segment length ≲ line width | rare; bounded by `MiterLimit = 2.0` (`StyledLineTileBuilder.cs:99`) | faint sub-pixel bright spot; also pre-existing geometry, invisible today under an opaque hard edge |
| **Genuinely self-crossing polyline** | rare — OMT road geometry is noded at intersections | same as the junction crotch |

Not affected: the casing/fill pair itself. The casing's outer edge band is over background and the fill's
outer edge band is over the casing's *interior* (`a == 1`), so a cased road never double-fades.

### 6.4 Defect 4 verdict

**Measurable but minor — not a shipping risk.** No intra-feature seams for any join or cap type; a few
pixels at junction crotches; a sub-pixel edge shift in tile buffer strips. Every affected case is strictly
better-looking than today's hard staircase. An earlier framing of this doc overstated it as an irreducible
blocker, which is how a viable direction came to be rejected. It is a **known limit to accept and
document**, with T3b (§7) recording the measurement rather than gating on it.

### 6.5 Round-cap `side` tagging — a real bug, IN SCOPE, and pre-existing

**The first pass of this doc claimed round caps have "no crack, no seam." That is false for the cap's own
arc.** The T-junction against the ribbon quad is fine (§6.1); the arc tagging is not.

**Mechanism** (`LineRibbonJob.cs:388-422`, `EmitStartCap`, `CapType.Round`). Vertices: centre pivot
`side 0`; arc intermediates all `+1`; `leftButt` `+1`; `rightButt` `−1`. The fan is then seeded
`prevFan = rightButtIdx` (`:409`), so the **first** triangle is `(centre[0], fan1[+1], rightButt[−1])`. Its
outer edge `fan1→rightButt` **is the true silhouette** — no triangle lies beyond it — yet `side` interpolates
`+1 → −1` along it and passes through **0 at the edge midpoint**. Any ramp keyed on `|side|` reads that as
deep interior and applies **no fade**: one arc segment per cap renders aliased while the rest of the same cap
fades correctly. Every other fan triangle has both rim vertices at `+1` and is fine.

> **[CORRECTED AT IMPLEMENTATION]** The tagging analysis above is right, but the *symptom* stated here — and
> in §7's T3c — was not observable, because **round caps did not rasterize at all**. The fan pivots on a
> vertex carrying `extrudeN == 0` (it must stay on the centreline); the extrusion helper guarded the divide
> by that length and then handed the zero vector to `normalize()`, which is NaN, and a NaN position discards
> every triangle referencing the vertex — which is every triangle in the fan. `line-cap: round` therefore
> rendered pixel-identical to `butt` for as long as the helper had existed: on the golden line
> `line-round-cap.png` spanned cols 110–401, exactly like butt's `line-golden.png`, where
> `line-square-cap.png` reached 101–410.
>
> Stage **A2b** fixed that (carry the degenerate case through the `normalize` as well) and is a
> **prerequisite** for observing this bug at all. The A2 tagging fix is still correct and is pinned by a
> GPU-free unit test on the tessellator (`RoundCap_FanTriangles_HaveUniformRimSide`) — vertex counts,
> positions and winding are identical whichever way the seed is tagged, so nothing else could catch it.

`EmitEndCap` (`:447-471`) mirrors it: the closing triangle `(centre, rightPrev[−1], prevFan[+1])` at
`:467-469` has the same mismatch, on the other flank. **One bad arc segment per capped end, both ends.**

**Why the round join escapes and the cap does not.** The join manufactures its own flank vertices at a
uniform sign (§6.1). The cap uniquely **reuses a rail vertex** — `rightButt` / `rightPrev` — that the
adjoining ribbon quad needs tagged `−1` for its own correctness (gap-hole cut, line-offset sign). It cannot
be retagged in place.

**The fix.** Emit a **second vertex co-located with `rightButt`** (same position, same `−across` direction)
tagged `+1`, used only as the cap fan's seed; the rail's `rightButt` stays `−1` for the quad. `leftButt` is
already `+1` on both of its uses and needs no duplicate. The end cap mirrors this for `rightPrev`. So it is
**exactly one extra vertex per round-capped end**, and zero extra triangles.

The pattern to copy is the round join's discipline of emitting a fresh flank vertex at the sign the fan
needs. *(A note on provenance: the join does not emit each arc vertex at both signs — `LineTessellator.cs:369-373`
and `:400-405` emit `arcStart`/`arcEnd` **once**, branching on `leftTurn`. The transferable precedent is
"manufacture a fresh vertex at the sign this fan requires", not "duplicate every vertex per sign".)*

**Four consequences the implementation must handle:**

1. **The managed oracle has the identical defect and must change in the same commit.**
   `LineTessellator.cs:470-520` mirrors the job exactly — arc intermediates `+1`, `leftButt` `+1`,
   `rightButt` `−1`, `prevFan = rightButtIdx`. `LineRibbonJobTests.AssertParity` asserts equal vertex counts
   (`:94`), equal index counts (`:95`), index-by-index equality (`:98`) and **exact `Side` equality**
   (`:103`). Changing one side alone turns `RoundCap_Parity` (`:181`) and `RoundCap_And_RoundJoin_Parity`
   (`:186`) red. Changed consistently, they stay green — the differential test is itself a tooth for the fix.
2. **Emission order is load-bearing.** `Execute()` reads `leftPrev = v-2; rightPrev = v-1` immediately after
   the start cap (`:148-149`), and the managed side documents the same `[count-2]=left, [count-1]=right`
   contract (`LineTessellator.cs:472-475`). The new duplicate must therefore be emitted **before**
   `leftButt`/`rightButt`, not appended after.
3. **Worst-case sizing.** `LineRibbonJob.MaxVertexCount` (`:74-85`): `startCap` `rs + 3 → rs + 4`,
   `endCap` `rs + 1 → rs + 2`. `MaxIndexCount` is **unchanged** (no new triangles). Three callers depend on
   these (`StyledLineTileBuilder.cs:251-252`, `LineRibbonJobTests.cs:40-41`,
   `BurstJobRunOffMainSpikeTests.cs:131-132`). The managed tessellator is `List`-based and has no formula to
   update.
4. **A new tooth, T3c** (§7) — neither T1 nor T3a catches this: T1 cuts perpendicular to a diagonal line,
   never a cap flank, and T3a asserts no interior *dip*, whereas this is a **missing fade on part of the
   outer silhouette**. Without T3c it would pass the entire headless gate.

**Why this is a pre-existing bug rather than new scope — with the honest qualification.** `LineCoverage`
already keys `_Blur` on `|side|` (`:264-268`) and already cuts the gap hole on `|side| < innerFrac`
(`:260-261`). On the bad arc segment `|side|` dips to 0, so *today*: a round-capped layer with
`line-blur > 0` renders that segment hard while the rest of the cap is blurred, and a round-capped
hollow/cased line has a wedge punched out of that segment. Both are live defects in the current shader, not
things AA introduces — AA merely makes the same mis-tagging visible on every round-capped line.
**The qualification:** no layer in `liberty.json` currently trips either, because the one `line-gap-width`
layer (`waterway_tunnel`) takes the butt-cap default and no layer sets `line-blur` (§3.1). So the bug is
**latent in production data but real in the shipped code** — a correct claim, weaker than "visibly broken
today", and the doc should not overstate it.

**Decision: fix it, in its own stage immediately before the straddle (§8 stage A2).** Not accept-and-document
— an aliased notch sitting next to freshly-antialiased neighbours reads as a defect, in a way a uniformly
hard edge does not. It gets its own stage rather than riding inside the straddle stage because its blast
radius is different in kind: it is a **mesh-topology change across two implementations plus the sizing
formulas**, whereas the straddle is shader-only. Keeping them apart preserves independent revertibility and
keeps the straddle stage's "no mesh change, no CBUFFER change" property honest. It can also be RED-verified
on its own terms *before any AA exists*, using the `_Blur`/gap-hole mis-shading above — a stronger, earlier
tooth than waiting for T3c under the new ramp.

---

## 7. Acceptance teeth

The obvious tooth is toothless. `LineSnapshotTests.LineEdge_AATransition_IsAtMostThreePixelsWide`
(`LineSnapshotTests.cs:294`) asserts the edge transition is ≤ 3 px and **passes today at 0 px, on a hard
staircase**. Any tooth of that shape is already satisfied by the broken build.

- **T1 — the outer silhouette IS antialiased.** Render a diagonal line; take a perpendicular cut and compute
  the **alpha-weighted coverage integral** `Σ saturate((brightness − bg) / (fullLine − bg))`. Assert that
  strictly-intermediate coverage pixels exist along the silhouette and that the profile across a diagonal cut
  is monotonic rather than binary. **RED on the current build** — a hard edge yields only 0 and 1.
- **T2 — the internal fill↔casing boundary stays crisp.** Render a cased pair (wide dark casing + narrow
  light fill). On a perpendicular cut assert **≤ 1 px of intermediate colour** between the fill plateau and
  the casing plateau, and **zero casing-colour contribution inside the fill band**. **RED on a naively
  re-added fade-AA build.** Passes today (hard edge), which is exactly why T1 must accompany it.
- **T2b — apparent width is unchanged.** The styled N-px line's coverage integral across a perpendicular cut
  equals N (± tol). **RED on the outset variant** (which reads N + 1). This is the guard that §5.1's pad was
  implemented as a straddle and not a fattening, and that the pad went in *before* the miter multiply.
- **T3a — no intra-feature seam at a join (hard gate).** A single polyline with a corner, once per
  `line-join` (miter / bevel / round). Assert **zero intensity dip along the join's interior** beyond noise
  tolerance, at the radii where the join geometry is the only thing drawing.

  > **[CORRECTED AT IMPLEMENTATION]** This originally named the wrong implementation as "coverage derived
  > from per-triangle edge distance". **That variant is unreachable in this shader**: it needs
  > `SV_Barycentrics` (SM 6.1) and the ForwardLit pass is `#pragma target 2.0`, which the epic is forbidden
  > to raise. What T3a actually gates is that **the ramp keys on the C0-continuous interpolated `|side|`
  > varying**, so no interior edge of the ribbon-plus-fan seams; the reachable defect is a `|side|`
  > discontinuity or dip at an interior edge — the same class as §6.5's cap bug.
  >
  > Two further limits found by measurement. **Near the corner nothing is observable at all:** the two
  > adjoining ribbon quads overlap the join geometry and draw at `a == 1`, compositing over any seam the fan
  > renders underneath — flipping the fan *intermediates*' flank sign produces no measurable change
  > anywhere. Only the arc's **terminal** flanks (`arcStart`/`arcEnd`, which are quad corners) are visible,
  > and that is what the RED verification uses. And the probe angle matters: a bevel's chord stands off at
  > only `half-width·cos(θ/2)`, so at 90° it falls *inside* a two-thirds-half-width probe — the shipped tooth
  > uses a 60° corner.
- **T3b — same-layer junction under-coverage (recorded limit, not a gate).** Two same-layer features meeting
  at an angle: measure and record the worst-case coverage deficit at the crotch (expected ≲ 25 % over ≲ 3 px,
  §6.3). Recorded so a future regression is visible; not a pass/fail threshold, because §6.4 accepts it.
- **T3c — the round cap fades all the way around (hard gate, §6.5).** Render a `line-cap: round` line end and
  walk the cap's silhouette arc: assert the fade is **present and monotonic at every angular position**, with
  no un-faded segment. **RED against the current vertex tagging.** This is the tooth that proves the §6.5 fix
  and the only one that catches it.
  - *Additionally, before the ramp exists:* the same defect is observable through `_Blur > 0` (that arc
    segment stays hard) — the pre-AA form, which stays in the suite afterwards as the guard on that
    mechanism. **Both forms require A2b first** (§6.5's correction): before it, no cap rendered at all, so
    the probe fails on its own "am I inside the cap" precondition rather than on the fade.
  - The AA-ON form is **not** redundant with the `_Blur` one. `line-blur` is opt-in and `liberty.json` sets
    it on **zero** layers, so the blur probe only ever exercises a configuration production never renders;
    the AA-ON form measures the shipped default, against a one-pixel ramp rather than `smoothstep` over
    three.

- **T4 — AA OFF reproduces today's hard edge (hard gate, §5.2).** With `_EDGE_ANTIALIASING_OFF` set, the
  rendered output must match the current pre-A3 build: same apparent width, hard edge, no fade. Measured on
  the existing snapshot harness under §7's rules — take the same perpendicular cut as T1/T2b and assert
  (i) the coverage profile is **binary**: no pixel reads strictly-intermediate coverage beyond the harness's
  background tolerance, and (ii) the coverage integral equals the same N px the pre-A3 build produces, so
  the pad is gone and not merely un-ramped.
  This is the regression guard that makes the toggle honest, it catches the "gated the ramp but not the pad"
  half-implementation §5.2 point 10 warns about, and it gives the maintainer a clean A/B at stage A5.

> **T1, T2, T2b, T3a and T3c all run with AA ON.** T4 is the only test that sets the keyword. A developer
> must not be able to green the suite by turning AA off; each AA-ON tooth explicitly asserts the keyword is
> clear for its render.

Rules for all of them:

- **Alpha-weighted coverage integrals, never thresholded non-background pixel counts.** S70's lesson:
  threshold counts swing ±1–2 px as feathered edge pixels cross the Manhattan-distance threshold and would
  flake a *correct* fix.
- Every snapshot test keeps its `IsAllBlack ⇒ Assert.Inconclusive` no-GPU guard
  (`LineSnapshotTests.cs:143-152`).
- **A first-run GPU-snapshot failure is re-run once before being treated as real** — editing a shared `.hlsl`
  CBUFFER/include can run a stale shader variant on the first batch run (`docs/lessons-learned.md`
  § Shaders & HLSL). `Tools/run-tests.sh` warms the cache (`shaders_need_warmup`, ~L96-125) but does not
  eliminate the hazard.
- **Never re-bake a snapshot to go green.** A diff means behaviour changed.
- Regression teeth that must not move: `LineWidth_MeasuredOnPerpendicular_MatchesExpected`,
  `WidthChange_NoMeshRebuild_RenderedWidthChanges`, `RoundCap_Parity` and `RoundCap_And_RoundJoin_Parity`
  (which stay green **only** if §6.5 changes both implementations consistently), the min-width floor
  behaviour, `_Blur` staying an opt-in soft edge distinct from AA, dash coverage, and the gap-hole cut
  position (§5.1's `innerFrac` re-derivation is the thing most likely to break it).

---

## 8. Stage sequence

The epic remains small relative to the rejected directions: no render-state change, no project settings
change, no vertex-*format* change, no new float uniform, no CBUFFER change, no material-binding change, no
build-pipeline change. §6.5 adds one vertex per round-capped end in two implementations; §5.2 adds one
boolean keyword.

| stage | content | gate |
|---|---|---|
| **A1** | T1, T2, T2b, T3a land **RED-verified** against the current build: T1 fails (staircase); T2, T2b, T3a pass. Record each observation — a tooth never seen red is not a tooth. | green except intentionally-red T1 |
| **A2** | **Round-cap tagging fix (§6.5).** One duplicated, `+1`-tagged flank vertex per round-capped end, emitted *before* `leftButt`/`rightButt`; **both** `LineRibbonJob` and `LineTessellator`, same commit; `MaxVertexCount` start/end cap terms +1 each, `MaxIndexCount` unchanged. RED-verified via the pre-AA `_Blur`/gap-hole probe (§7 T3c note). | full gate green, `RoundCap_*_Parity` green |
| **A3** | **The straddle + the toggle.** (i) Fixed 0.5 px pad added to `outerWorld` *before* the miter multiply at `Line_VertexExtrude.hlsl:142`; the `saturate((1 − \|side\|)/fwidth(side))` ramp in `LineCoverage`; `innerFrac` re-derived against the padded outer; `dashU` still on the styled width. (ii) **Both** guarded by `_EDGE_ANTIALIASING_OFF`, `#pragma shader_feature_local` in **all five** passes. (iii) Registry + GUI, in order: `ShaderKeywords.EdgeAntialiasingOff` → `Line/PropertyNames.EdgeAntialiasing` → `Line/PropertyId.EdgeAntialiasing` → `[ToggleUI] _EdgeAntialiasing` in `Line.shader`'s (B) group → **`LineShaderGUI.ValidateMaterial` override** + `DrawLineInputs` row + XML-doc correction (incl. the stale `LineMaterialTweaker` cref) → `MapLine.mat` default. (iv) `LinePropertyNamesCount_IsExactly10` → `11`, with the `Line/PropertyNames.cs` class doc amended (§5.2 point 7). **No width knob, no new uniform, no new varying, no CBUFFER change.** T3c and T4 land here. | full gate green; T1, T2, T2b, T3a, T3c green with AA on, T4 green with AA off |
| **A4** | T3b recorded; docs — this file's status, `meshing-design.md` §2 rewritten to the shipped model, the line shader `README.md` § antialiasing, and `docs/lessons-learned.md`'s "per-line transparent-fade AA cannot render a crisp cased line" entry corrected to the §2 mechanism. | green |
| **A6.0** | **The AA ramps divide by the EUCLIDEAN gradient of `side`, not `fwidth`.** Two lines in `LineCoverage` — the outer edge and the gap-hole straddle. `fwidth` is Manhattan and over-reads by up to √2 with screen angle, so diagonals rendered softer *and* 0.41 px thinner than axis-aligned lines of the same styled width; the defect predates A3 and shipped with it (§2). `_Blur` and dash keep `fwidth` — separate features. | full gate green; diagonal apparent width == axis-aligned |
| **A6a** | **Hairline strategy `_HAIRLINE_HARD`.** Below ~2 px painted band the coverage ramp narrows toward a step, staying centred on the styled edge; **both** the outer silhouette and the gap-hole cut narrow, so a hairline-thin casing ring is not half-hardened. Fragment-only — the band width comes from A6.0's `sideGrad`, so no new vertex data and no `fwidth`. `LinePropertyNamesCount_IsExactly11` → `12`. `_` (Default) is the shipping, un-strippable member of the keyword set. | full gate green; default path unchanged |
| **A6b** | **Hairline strategy `_HAIRLINE_SOLID_CORE`.** Vertex clamp of the rendered band to 2 device px + coverage scaled by `W_true/W_min`, so a hairline always has a solid core and still carries the styled ink. Both halves ship together. The compensation scalar rides `LineVaryings.uv.w` in the forward pass **only**; the other four keep `float3`. `widthWorld` is untouched so `dashU` keeps its styled-width phase. | full gate green; default path unchanged |
| **A5** | **Maintainer eyeball in the demo** (§5.2) — **primary A/B is toggling `_EdgeAntialiasing` on/off on the live material**, plus a round-capped line end and the scene list in §5.2. Not headless-verifiable. | sign-off |

A3 is deliberately one commit, and **the toggle belongs in it** rather than as a follow-up: the keyword gates
the very code A3 introduces, so landing the straddle first would ship an ungated intermediate and landing the
toggle first would gate nothing. Within A3 the three parts are inseparable for the same reason — the pad
without the ramp renders a visibly fatter line, the ramp without the pad degenerates to the failed inset, and
a toggle that gates only one of them is §5.2 point 10's half-implementation. A2 is deliberately *not* folded
in — see §6.5's closing paragraph.

**Recorded, not re-litigated:** the maintainer's empirical result that plain MSAA up to 16× produced no
meaningful improvement in this project. It is the reason the replace-compositing family (§4.2) is off the
table and it is not revisited by this epic.

**Explicitly deferred (scope fence).** **Fill antialiasing — out of scope by decision, and it is a
different mechanism, not a port of this one** (§9.1): a fill has no `side`-equivalent distance field, and
earcut's internal diagonals and hole-bridge edges mean a per-triangle fade would cross-hatch every polygon
interior. A separate epic. Also deferred: direction 1 fusion in any form,
including "just a helper that pairs layers for later"; `line-sort-key`; the whole-layer offscreen composite
(§9.2); manual supersampling (§4.3); S77's sub-pixel energy-conservation regime beyond the existing
min-width floor; along-line dash aliasing; activating the four capability-only line passes; `line-pattern`;
clipping line geometry to the tile boundary (§6.3's second row — a separate concern with its own trade-offs);
widening mesh bounds for the styled width (§5.1, pre-existing if it is a gap at all); any change to `_Blur`,
`line-offset`, `line-translate` or gap-hole semantics beyond §5.1's required `innerFrac` re-derivation.

---

## 9. Resolved decisions and recorded limits

No questions remain open for the maintainer beyond locking the design itself.

### 9.1 RESOLVED — fills are NOT in this epic, and would need a different mechanism

**Decision (maintainer):** fill antialiasing is out of scope. Not a scheduling preference — the mechanism
does not carry over.

**Correcting an earlier claim in this doc.** A previous revision argued the mechanism transfers because
`Fill_VertexModify.hlsl` carries the character-identical `MapPixelsToWorld` block. That conflates two
different things, and a future reader must not re-derive the wrong conclusion from the shared block:

- **What transfers:** only the **px→world scale measurement helper** (`MAP-SHARED-BEGIN: PixelsToWorld`,
  `Fill_VertexModify.hlsl:42-78`). It is a ruler, not a coverage mechanism.
- **What does not transfer:** the **distance field the entire straddle keys on.** A line carries `side` — a
  per-vertex *signed distance from the centreline*, normalized to ±1 at the lateral edge and interpolated
  across the ribbon (`LineRibbonJob.cs:489-498`). `|side|`, `fwidth(side)`, `innerFrac` and the `_Blur` mask
  are all functions of it. **A fill has no such field**: it is an earcut triangle mesh whose vertex streams
  are Position+Normal, a tile-space UV, Tangent and Color (`StyledFillTileBuilder.cs:76-82`) — an interior
  vertex carries no distance-to-boundary, and there is nothing to ramp against.

**Second, independent reason — verified.** Earcut emits **internal** edges throughout the polygon interior,
so a naive per-triangle edge-distance fade would antialias the triangulation itself, cross-hatching seams
through the middle of every filled polygon. Two distinct kinds of internal edge exist and both would show:
ordinary ear-cut diagonals, and **hole-bridge edges** — `Earcut.Result` returns "the flattened vertex array
(which includes bridge-duplicate verts) and the triangle indices into that array"
(`Core/Geometry/Earcut.cs:15, 41-47`), so every hole is joined to its outer ring by a duplicated-vertex
bridge. A per-triangle fade would draw a visible slit from each hole to the polygon exterior.

So a fill solution must first **distinguish true polygon-boundary edges from triangulation artifacts** —
which is a build-pipeline problem, not a shader one. That is a genuinely different design with its own reach,
**a separate epic, not a port of this one.**

### 9.2 Recorded — how defect 4 is treated

§6.4 recommends *accept and document*; T3b records the measurement rather than gating on it. If the junction
crotch proves visible at A5, the only real fix is the whole-layer offscreen composite named in S77 §6 —
render a layer to an offscreen target, then composite once, making intra-layer overlap idempotent. **It is
compatible with both maintainer rulings** (needs neither depth nor MSAA) and would be a separate epic, not a
stage of this one.

### 9.3 Stub — the future fill-AA epic (naming only, not designed here)

Two candidate mechanisms, recorded so the epic starts from something rather than nothing. **Neither is
designed here and neither is endorsed:**

- **Flag boundary edges at build time** and carry a distance-to-boundary varying, giving fills the field
  lines already have. Reach: `Earcut`/`PolygonAssembler` must mark which edges are real boundary, plus a new
  fill vertex stream.
- **Some other mechanism entirely** — the maintainer's framing was that fills have no "SDF-like" rendering,
  so the answer may not be a distance ramp at all.

One hazard worth carrying into that epic: adjacent fills and tile-boundary abutments share edges exactly, so
whatever mechanism is chosen has to reason about two fills meeting rather than one fill over background.

**Not questions** (locked by the maintainer, recorded so they are not reopened):

- **No depth.** Map layers do not ZWrite and do not depth-test; painter order is the render-queue band.
- **No MSAA.** Empirically tried to 16× with no meaningful improvement; analytical AA is required. This also
  closes alpha-to-coverage and every replace-compositing variant.
- **No opaque-queue migration** — `LayerDrawOrder.cs:19-29`.
- **No un-stashing the outset-AA experiment.** It is the ruled-out approach and is written against the old
  `Assets/MapRenderer.Unity/...` layout so it cannot apply cleanly. `MapPixelsToWorld`
  (`Line_VertexExtrude.hlsl:42-65`) already supersedes its `sideScale` parametrization.

---

### 9.4 Recorded at implementation — open findings the epic surfaced

None of these blocks the shipped feature. They are here because this is where the epic's durable record
lives, and each was found by measurement rather than review.

1. **The cap pivot's `pxToWorld` is an artefact, not a measurement.** One finding, two symptoms — track it
   as one item. The round cap's fan pivot carries `extrudeN == 0` by design, so it has **no direction to
   measure along**; `MapPixelsToWorld(centerWS, float3(0,0,0))` returns the `max(refPx, 0.1)` clamp value
   (≈ 14.0 at the snapshot camera against a true 0.2734 — about 51× too large). Consequences, both confined
   to that one vertex:
   - **`dashU` is always wrong there** (`widthWorld` scales with `pxToWorld`), so a *dashed* round-capped
     line gets a warped dash phase across the cap fan. **Confirmed unreachable in the shipped style:**
     `liberty.json` has 17 layers with `line-dasharray` and 15 with `line-cap: round`, and **0 with both**
     (measured, A7). That is what makes deferring this defensible — it needs a vertex-stream change for
     zero live benefit.
   - **`innerFrac` is wrong there on world-unit-width layers only.** For a pixel-width layer every term is
     linear in `pxToWorld` and it cancels exactly. For a world-unit layer `outerWorld` carries no such
     factor while `aaPadWorld` does, so the ratio does not cancel: 0.111 at the pivot against 0.468 at the
     rim (1 m width, 2 m gap), and `innerFrac` is interpolated, so a round cap's gap hole would bow inward.
     Needs world-unit width **and** `line-gap-width` **and** round caps together — unreachable in
     production, since `MaterialFactory.cs:193` sets `_WidthIsPixels = 1` unconditionally and the BRG
     backend seeds its instanced props from the same material.

   A fix needs a direction the pivot vertex does not carry — i.e. a vertex-stream change, not a shader
   tweak. Not attempted here.

2. **`line-blur` now feathers from the padded edge**, half a pixel outside where it used to start. Sub-pixel,
   and `liberty.json` sets `line-blur` on **0** layers.

3. **World-unit-width layers pay a second `MapPixelsToWorld` per vertex** — the pad must be measured, and
   unlike a pixel-width layer there is no existing measurement to reuse. No production style layer takes
   this path, but note that the committed `MapLine.mat` itself ships `_WidthIsPixels: 0`, so anything
   rendering with the base material directly does. Skipped entirely under `_EDGE_ANTIALIASING_OFF`.

4. **Sub-pixel world-unit lines now fade instead of shimmering.** *(A6a interacts: under `_HAIRLINE_HARD`
   the same line renders as a broken dashed one — lit only where a pixel centre falls inside the styled
   half-width. No production layer takes the path, but `MapLine.mat` itself ships `_WidthIsPixels: 0`. Do
   not "fix" it by touching the min-width floor.)* The min-width floor is pixel-width-only,
   so a world-unit line thinner than a pixel was previously at the mercy of the rasterizer's centre test;
   it now renders a faint continuous line. Arguably an improvement, recorded because it is a behaviour
   change the floor does not gate.

5. **Same-layer junction under-coverage, measured (T3b).** Worst composite coverage **0.931 — a 6.9 %
   deficit** — on a 40° V of two same-layer features, 2 of 4228 deep-interior pixels below 0.95, at the
   point where the two bands separate. Comfortably inside §6.3's ≲25 % prediction. Recorded, not gated
   (§6.4); the number is written to the test log so a regression shows as a changed measurement.

6. **Observation for the `line-blur` and dash owners, not an AA finding.** `_Blur`'s feather
   (`fwidth(side)`) and the dash edge feather (`fwidth(dashU)`) use the same Manhattan gradient length that
   A6.0 replaced in the AA ramps, so their softness likewise varies by up to √2 with a line's screen
   direction. Whether that matters is a question about those features' own semantics — `line-blur` is a
   MapLibre paint property whose softness is what it has always been — and it is recorded here only so it is
   known, not as a recommendation.

7. **The axis-aligned case did NOT move — but the in-test scalar wobbles by 0.009 px between runs, and that
   is unexplained.** `fwidth` and the Euclidean length are mathematically identical when one derivative is
   zero, so no axis-aligned line should move, and none did: **all 13 axis-aligned snapshot fixtures are
   byte-identical** across `b8329c9e` → A6.0 (`line-aa-t2b-*`, `line-aa-t2-cased-pair`, `line-width-*`,
   `line-aa-edge`, `line-stream3-cyan`, every `lit-line-*`). Recomputing T2b's integral offline from the
   `b8329c9e` PNG and the A6.0 PNG gives **5.9943 for both**.

   The reported scalar nevertheless read 6.003 in three intermediate runs and 5.994 in the baseline and
   final ones. Since the pixels are identical, that is a property of the measurement path across runs, not
   of the render — the direction being measured (0.417 px) is ~46× larger, so it does not affect any A6.0
   conclusion. Recorded because it is not fully explained and a future reader comparing scalars across runs
   should expect ±0.01 px.

8. **Under `_HAIRLINE_SOLID_CORE` the four capability passes clip an UNCOMPENSATED silhouette.**
   `LineCoverage`'s signature is untouched, so `clip(LineCoverage(...) − 0.5)` in ShadowCaster / DepthOnly /
   DepthNormals / GBuffer sees the band at its **clamped** width, not the styled one — a hairline's depth
   silhouette would be 2 px wide. All four are capability-only and inert (URP excludes `Queue=Transparent`
   from the opaque prepasses), so this matters only if S69 activates them; whoever does must revisit it.

9. **`_HAIRLINE_SOLID_CORE` bypasses the min-width floor, by design and at a cost.** Its clamp forces the
   extruded half-width to ≥ 1.5 px, always above `minHalfWorld + aaPad` = 1.0 px, so the floor can never
   bind. That is what buys proportionality below 1 px — at `W = 0.8` Default renders Σ = 1.000 at peak
   α 0.80 while SolidCore renders Σ = 0.796 at peak α 0.40. The floor exists so hairline roads stay legible
   zoomed out, so this is a real trade the strategy choice has to weigh, not a free win.

10. **CLOSED by A7 — the strategies are covered across the keyword matrix and on real geometry.** All six
    `_EDGE_ANTIALIASING_OFF` × strategy combinations are asserted individually, and each strategy is
    exercised on a 45° diagonal, a miter join and a round cap as well as straight horizontal runs. The three
    AA-off cells measure identically, which is what the guards should produce; the *localisation* claim is
    narrower than the coverage claim — one injection was shown to fail exactly one cell, which demonstrates
    the cases are separable, not that every possible defect localises.

11. **RESOLVED by A7 — interpolating `hairlineScale` is a non-issue for every production layer.** For a
    pixel-width line `aaPadWorld = 0.5·pxToWorld` ⇒ `minWidthWorld = 2·pxToWorld` and
    `widthWorld = W·ws·pxToWorld`, so `hairlineScale = saturate(W·ws / max(W·ws, 2))` — **`pxToWorld` cancels
    completely.** It cannot vary with depth, tilt or latitude, the clamp engages for a whole feature or not
    at all, and `noperspective` would change nothing. World-unit widths are the case that *can* vary
    (`widthWorld` is constant in metres while `minWidthWorld` tracks `pxToWorld`), so the clamp could engage
    mid-segment there; no production layer takes that path.

12. **OPEN, and the cause is NOT established — a long single segment measures differently from a
    subdivided one under a tilted camera.** With the corrected instrument (item 13), a 1 px line run as ONE
    segment from z = −12 → 220 reads a relative peak of `0.498 / 0.498 / 0.318 / 0.349 / 0.501` — flat at
    the ends, sagging in the middle, spread **0.182** — while the same line subdivided into 40 segments
    reads `0.498 / 0.498 / 0.499 / 0.497 / 0.501`, spread **0.003**.

    **A7 first attributed this to per-vertex `pxToWorld` evaluation. That attribution is refuted.** For this
    camera (`Euler(12,0,0)`, so the camera's right axis is exactly world +x, which is exactly the across
    direction for a line along z) a probe step along `unitDir_WS` does not change `clip.w`, so
    `pxToWorld ∝ clip.w` exactly; `clip.w` is affine in world position, so the required offset
    `h(t) = 1.5·pxToWorld(t)` is affine in the segment parameter, and **linear interpolation of the two
    vertex offsets reproduces it exactly at every point.** The extruded locus is correct mid-segment, not
    only at the vertices. Whatever produces the sag, it is not that.

    Unattributed, therefore, and recorded rather than acted on. If re-opened: (i) re-measure with an across
    direction that has a view-axis component, since this fixture cancels the named mechanism by
    construction; (ii) check whether `pxToWorld` sizes the offset's *projected* length along `n` while the
    visible width is the *perpendicular* distance — a `cos θ` slant varying toward the vanishing point,
    which subdivision would not fix and so cannot be the whole story either; and (iii) note the sag is
    localised to two adjacent samples rather than a smooth drift, which is as consistent with the 1 px
    plateau being missed by the sampling as with a geometry error. **Fix the measurement before touching
    the shader.** No production layer is known to be affected: MVT lines are densely noded and a top-down
    map has little depth range per segment.

13. **CLOSED — the "irreproducible tilt drift" was the instrument, not the renderer.** A7 reported the same
    fixture reading spread 0.046 in one run and 0.303 in another, and separately claimed lit-shading had
    been ruled out because the `a == 1` companion "reads exactly 1.000 at every depth". **Both were the same
    artefact.** The normaliser took the most saturated pixel on a whole column as the `a == 1` reference,
    and `CoverageAt` ends in `saturate` — so the companion was *above* that reference and pinned at the
    clamp, which is what 1.000-at-every-depth actually meant; shading was never being cancelled, and a run
    where the column max landed on a different pixel rescaled the numerator while the denominator stayed
    pinned.

    Measuring the plateau from a **named interior box** of the reference bar and taking both terms as **raw,
    unsaturated** projections: the companion reads `1.100 → 1.192` across the sampled depths (shading does
    vary with depth by ~8.4 %, exactly as §6.2 says it should), the hairline tracks it `0.548 → 0.597`, and
    the ratio is `0.498 / 0.498 / 0.499 / 0.497 / 0.501` — **spread 0.003, dead on the predicted 0.5.** Item
    11's answer is now confirmed by measurement, not only algebra. *Lesson, the same one A7.2 already paid
    for once: a ratio of two lit surfaces must be taken from unsaturated values, and a normaliser must be a
    named reference, never "whatever is brightest".*

14. **Still recorded, not acted on:** `_Blur`'s and dash's Manhattan `fwidth` (item 6, an observation for
    those features' owners); the ±0.01 px scalar wobble (item 7); and the A5 eyeball, where the maintainer
    approved the AA in the demo and reads Hard as the worst of the three strategies — consistent with it
    being the one that gives up width-proportionality over `(1.0, 1.2]` px.

15. **A one-pixel straddle produces NO partially-covered pixel where the silhouette is both axis-aligned and
    boundary-aligned.** Both straddling pixel centres then sit ~0.5 px from the edge and read 1.0 and 0.0.
   This is correct AA, it is invisible to the eye, and it bit three separate test formulations in this epic
   — a round cap's 0° chord lands exactly on a pixel boundary at the obvious fixture placement. Any tooth of
   the form "there must be a partial pixel here" needs a quarter-pixel fixture offset. Also in
   `docs/lessons-learned.md`.

16. **For the DPR epic: the AA pad is DEVICE pixels and must stay that way.** Recorded at merge time, when
    this branch integrated `docs/device-pixel-ratio-design.md`. That epic's premise is that a style's `px`
    are *logical* pixels and `line-*` currently does not honour it — `_Width` reaches the shader as raw
    device px with `_WidthIsPixels = 1`, and `DevicePixelRatio` appears nowhere in the shaders. When it lands,
    `_Width`'s meaning changes.
    **The 0.5 px pad, the `1/sideGrad` width estimate and `HAIRLINE_MIN_WIDTH_PX` must NOT be converted
    along with it.** Antialiasing is a property of the physical raster: a ramp has to be one *device* pixel
    wide, or a DPR-2 panel gets a 2-device-px blur. `MapPixelsToWorld` already measures device pixels
    correctly and needs no change. What *does* need thought is that the hairline strategies then engage at a
    different *logical* width — at DPR 2 a 1-logical-px road is 2 device px, above `HAIRLINE_MIN_WIDTH_PX`,
    so the clamp stops engaging for it. That is arguably correct (the strategies exist for device-pixel
    legibility) but it is a behaviour change per-panel and should be decided, not inherited.
    Note also that every test here runs at `DevicePixelRatio = 1.0`, where logical and device coincide — so
    nothing in this epic is evidence about DPR ≠ 1.

## 10. Constraints the implementation must respect

- `Map/` shader files may **NOT** include `Map/Common` — shared HLSL is duplicated per layer on purpose and
  pinned by `ShaderStructureTests.MapLayerFiles_DoNotIncludeCommonFolder` and
  `SharedShaderBlocks_AreIdenticalAcrossLayers` via the `MAP-SHARED-BEGIN/END` sentinels
  (`Line_VertexExtrude.hlsl:19-30`). Note that the fill's copy of `MapPixelsToWorld` is a *ruler*, not a
  coverage mechanism — its existence is not evidence that this epic's approach extends to fills (§9.1). If a
  future fill epic needs shared HLSL, it is **copied verbatim**, never extracted.
- **Never name an internal shader property after a MapLibre style term** — the styler binds `line-X → _X` and
  silently overwrites collisions; that is how AA was off on every backend for weeks
  (`docs/lessons-learned.md` § Shaders & HLSL). The recommendation adds exactly one property,
  `_EdgeAntialiasing`, which is collision-safe on both counts established in §5.2 point 5, and it goes in the
  shader's **(B) Internal render params** group so the boundary stays visible.
- **The AA ramp width is a compile-time constant.** The toggle is a **boolean keyword, never a `Range`/float**
  (§5.2). Exposing the width as a number is a rejection of §5, not an extension of the toggle.
- **Registries, not raw strings.** Keyword names go in `Rendering/Materials/ShaderKeywords.cs`; property names
  in the layered `PropertyNames`/`PropertyId` registry, with `PropertyId` deriving from `PropertyNames`.
  `NoRawStringMaterialAccessGuardTests` enforces the property half over the whole Unity assembly; the keyword
  half is convention the reviewer must check, because a raw literal inside `CoreUtils.SetKeyword` does not
  match the guard's regex (§5.2 point 7).
- **Keyword sync is written in code**, in the GUI's `ValidateMaterial` override — Unity's keyword-attaching
  drawers are not used in this codebase. A `[ToggleUI]`/`[Toggle]` property with no `ValidateMaterial` entry
  is inert (§5.2 points 5 and 8).
- **Single extrusion site (S67):** the 0.5 px pad goes in `Line_VertexExtrude.hlsl` so all five passes keep an
  identical silhouette. A pad in the forward vertex path alone desyncs pass silhouettes — **and so does a
  keyword declared in fewer than all five passes, or declared `shader_feature_local_fragment` instead of
  plain `shader_feature_local`** (the `_fragment` form strips it from the vertex stage). See §5.2 point 1.
- **SRP Batcher:** `UnityPerMaterial` must stay byte-identical across passes; a CBUFFER edit must be mirrored
  in the CBUFFER, the `UNITY_DOTS_INSTANCING` block and the BRG SoA packing. The recommendation needs **no**
  CBUFFER change — a keyword is not a CBUFFER member. Keep it that way.
- **Build-variant stripping:** `shader_feature_local` variants are stripped from player builds unless a
  material in the build declares the keyword (`FillTweaker.cs`'s doc records this). This is why the keyword is
  `_OFF`-polarity with AA on by default — the shipping variant carries no keyword and can never be stripped.
  Inverting the polarity would make AA work in the Editor and silently vanish in a build.
- **Burst/managed parity:** `LineRibbonJob` and `LineTessellator` are held to exact differential parity
  including `Side` (`LineRibbonJobTests.AssertParity`). Any topology or tagging change lands in both, in one
  commit.
- `Unity.Mathematics` only (`System.Math` banned); `Core` stays engine-free; test-only members do not go on
  production classes. See `docs/conventions-short.md`.
