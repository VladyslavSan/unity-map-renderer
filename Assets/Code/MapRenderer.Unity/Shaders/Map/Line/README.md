# Map/Line — shader notes

Design rationale and pass-set structure for `Map/Line`. Updated in **S67**.

## Pass set (as of S67)

`Line.shader` has five passes, matching `Map/Fill`:

| Pass | LightMode | Status |
|---|---|---|
| `ForwardLit` | `UniversalForward` | Active — transparent forward rendering |
| `ShadowCaster` | `ShadowCaster` | Capability-only (present-but-inert) |
| `DepthOnly` | `DepthOnly` | Capability-only (present-but-inert) |
| `DepthNormals` | `DepthNormals` | Capability-only (present-but-inert) |
| `GBuffer` | `UniversalGBuffer` | Capability-only (present-but-inert) |

**Capability-only** means: the pass is present and compiles, but URP never executes it at runtime.
Lines use `Queue=Transparent` (≥ 2501), and URP automatically excludes transparent materials from the
opaque depth/GBuffer prepasses (ShadowCaster, DepthOnly, DepthNormals, GBuffer). The passes are inert
until S69, which will change the rendering mode to enable depth-writing or shadow casting.

## Why a line needs bespoke pass bodies

Fill's depth passes work by running flat geometry through the shared `MapVertexModify` hook.
The line cannot reuse that hook — its geometry is **built in the vertex shader** via world-space
lateral extrusion (POSITION + extrudeN on TEXCOORD0 + side+dist on TEXCOORD1 + widthScale on
TEXCOORD2 + gap-width, line-offset, dash-U, +Y lift). Every pass must replay the **same** extrusion,
or it writes depth/normals for the un-extruded centerline, not the actual ribbon silhouette.

The solution (S67): `Line_VertexExtrude.hlsl` — a shared helper included by **every** line pass before
its body. It defines `LineAttributes`, `Line_VertexExtrude()`, and `LineCoverage()`. Single extrusion
site → silhouette divergence between passes is impossible by construction.

## Include order (every line pass)

```hlsl
// in each Pass's HLSLPROGRAM, after #pragmas:
#include "Line_LitInput.hlsl"        // 1. CBUFFER + DOTS bridge (props, SRP Batcher)
#include "Line_VertexExtrude.hlsl"   // 2. LineAttributes + Line_VertexExtrude() + LineCoverage()
#include "Line_<Pass>.hlsl"          // 3. pass body (vertex/fragment entry points)
```

## Coverage clip in depth passes

The four new passes call `LineCoverage(side, innerFrac, dashU)` and `clip(coverage - 0.5)`.
`LineCoverage` takes screen-space derivatives of `side` and `dashU` — a fragment-stage operation — so
`side/innerFrac/dashU` must be **interpolated varyings** in every pass (not computed per-vertex constants);
a by-value constant has a zero derivative and every ramp would collapse to a hard edge. This is why the line
depth passes carry those three interpolators even though depth passes normally carry only `positionCS`.

## Antialiasing model — a strict one-pixel straddle (toggleable)

`LineCoverage` ramps coverage `1 → 0` across **exactly one device pixel centred on the styled edge** — half
a pixel inside, half outside. Two properties follow, and both are load-bearing:

- the **50 % contour sits on the styled edge**, so apparent width is unchanged;
- the interior stays **`a == 1`**, which is what lets a cased road's fill sit on its casing without bleeding
  it through the seam — the failure that got the earlier, *inset*, fade removed in `0b910c7`.

The ribbon extrudes half a device pixel past the styled half-width, because the outer half of the ramp needs
somewhere to land (the rasterizer only produces fragments where a triangle covers a pixel centre). That pad
goes into `outerWorld` **before** the miter multiply, so the *perpendicular* pad stays 0.5 px at any corner:
the miter factor is `1/cos(θ/2)`, so padding after the multiply would give `pad·cos(θ/2)`, which shrinks
toward zero as a corner sharpens. The pad is measured with `MapPixelsToWorld` and **never** as
`0.5 * pxToWorld` — `pxToWorld` is a unit-conversion factor (literally `1.0` when the width is already in
world metres), so reusing it would pad a world-unit layer by half a **metre**.

`innerFrac` is re-derived against the padded outer, and the gap-hole cut becomes a symmetric ±0.5 px
straddle centred on `|side| == innerFrac`: `saturate((|side| − innerFrac)/|∇side| + 0.5)`. That `+ 0.5`
is what centres it — the outer formula would shift the hole half a pixel.

The ramp width is a **compile-time constant**; there is nothing bindable to widen. A tunable AA width is how
the removed model went wrong. Thin lines keep the **min-width floor** in `Line_VertexExtrude()` (half-width
`≥ 0.5 px` ⇒ a stable 1 px hairline).

**The whole mechanism is switchable**, which is also the maintainer's A/B. `_EdgeAntialiasing` (`[ToggleUI]`,
default `1`) drives `_EDGE_ANTIALIASING_OFF`, declared plain `#pragma shader_feature_local` in **all five**
passes — the `_fragment` form would strip it from the vertex stage, and since the pad *is* a vertex-stage
change the passes would disagree on the silhouette. Note the polarity: `_OFF` means the **shipping** (AA-on)
variant carries no keyword, so it can never be stripped from a player build; inverting it would make AA work
in the Editor and silently vanish when built. `[ToggleUI]` attaches no keyword on its own —
`LineShaderGUI.ValidateMaterial` syncs it in code, mirroring `_ReceiveShadows`. With the keyword set, the OFF
branches are the original pre-AA expressions **verbatim**, not the new ones with a zero added.

| Property | Meaning | Default | Style-bound? |
|---|---|---|---|
| `_Blur` | MapLibre **`line-blur`** — an opt-in inward soft edge (NOT antialiasing) | 0 (hard) | yes (`MaterialFactory` binds `line-blur`) |

`_Blur` is a real spec paint property, not AA; `0` ⇒ hard edge. It was once *also* the AA knob, which
collided with the `line-blur` style term (`line-X → _X`) and silently zeroed AA — the internal AA width later
became `_AaEdgeWidth` (now removed with the AA model). The naming lesson still stands: never name an internal
render param after a `line-*`/`fill-*` term, and keep the
two prop groups labeled-separate in `Line_LitInput.hlsl` / `Line.shader`.

**Why the earlier attempts failed, and what changed:** the removed model faded *inside* the styled band, so
the fill's skirt ramped to transparent over the casing and showed it through — a compositing failure, not a
coverage one, which is why neither a wider fade nor MSAA fixed it (the maintainer measured MSAA to 16× with
no meaningful gain). A strict straddle escapes it because the fade lives at the edge rather than inside the
band: everywhere the fill is meant to be opaque, `a == 1`, and `dst = src·a + dst·(1−a)` cannot bleed at
`a == 1`. The trilemma that entry records — {solid core, unchanged apparent width, antialiased}, pick two —
is resolved by paying for the third with **geometry** (the 0.5 px pad) rather than with the fade. Stages
`S70` / `S77` (the removed opaque-core buffer and its sub-pixel over-thicken artifact) remain superseded.

### Hairline strategies — `_HairlineStrategy`

The straddle's solid core is `W − 1` device pixels wide, so **at `W ≤ 1` it vanishes**. What is left is a
tent: the profile peaks at `1 − φ`, where `φ` is the distance from the centreline to the nearest pixel
centre. A one-pixel road therefore renders as one bright pixel or two half-bright ones depending on
sub-pixel phase — energy is conserved, but peak brightness oscillates as that phase drifts, and the line
shimmers under motion. Measured on the default build at `W = 1`: peaks **1.000 / 0.892 / 0.799 / 0.703 /
0.607** across φ = 0.0 … 0.4, a spread of 0.393.

| Value | Keyword | Behaviour |
|---|---|---|
| 0 — Default | *(none)* | today's straddle, unchanged |
| 1 — Hard | `_HAIRLINE_HARD` | the ramp narrows toward a step as the painted band falls below 2 px |
| 2 — SolidCore | `_HAIRLINE_SOLID_CORE` | the band is clamped to 2 px so it always has a solid core, and coverage is scaled by `W_true/W_min` so the ink is unchanged |

Measured at `W = 1`, across sub-pixel phases φ = 0.0…0.4:

| strategy | peak | spread | ink (Σ) |
|---|---|---|---|
| Default | 1.000 → 0.602 | **0.398** | 1.000 |
| Hard | 1.000 | **0.000** | 1.000 |
| SolidCore | 0.506 | **0.000** | ~1.01 |

Both strategies remove the shimmer; they differ in what they trade for it, and **the trade is not free on
either side** — read this before choosing one.

**Hard** keeps full brightness and takes the classic hard-edge positional snap. It also gives up
width-proportionality over `(1.0, 1.2]` px: a 1.2 px road paints the same single full pixel as a 1.0 px one.
*Below* 1.0 px the flatness is **not** Hard's doing — it is the pre-existing min-width floor, which pins any
pixel-width line to a 1 px hairline and behaves identically under Default.

**SolidCore** stays width-proportional all the way down (peak 0.398 / 0.506 / 0.594 / 0.703 / 0.799 / 0.909 /
1.000 as `W` runs 0.8 → 2.0, ink tracking `W` throughout) — but it buys that by **bypassing the min-width
floor entirely**. The clamp forces the extruded half-width to ≥ 1.5 px, always above the floor's 1.0 px, so
`minHalfWorld` can never bind. At `W = 0.8` px: Default renders Σ = 1.000 at peak α 0.80, SolidCore renders
Σ = 0.796 at peak α 0.40. **That is the cost:** the floor exists so hairline roads stay legible when zoomed
out, and SolidCore trades legibility-at-a-floor for proportionality. Sub-pixel roads get genuinely fainter.

Neither pops at its threshold: the largest peak step between adjacent widths is 0.000 for Hard and 0.11 for
SolidCore, both smooth.

`_HAIRLINE_HARD` scales the ramp width by `lerp(0.05, 1, smoothstep(1, 2, bandPx))` while keeping the
transition **centred on the styled edge** — narrowing without re-centring would leave the hard edge half a
pixel out and render the line a pixel fat, which is the S70 outset artefact. At `bandPx ≥ 2` the expression
is algebraically the default one, so wide lines are untouched (a 6 px line still measures 6.004 px with
partial pixels on both flanks). At `W = 1` the peak is 1.000 at every phase, spread 0.000, and the coverage
integral is still 1.000.

Both edges harden. `bandPx` measures what is actually **painted** — for a hollow/cased line that is the
ring, `halfStyled − innerFrac·halfPad`, not the outer radius — and the inner gap-hole straddle narrows with
the same `rampPx`. Hardening only the outer silhouette would leave a hairline-thin casing ring crisp
outside and still shimmering inside, on the same ring.

**The width estimate is Euclidean, not `fwidth`.** `side` is baked ±1, so `|∇side| = 1/H` for a padded
half-width `H`, giving `styledHalf = 1/|∇side| − 0.5`. `fwidth` is Manhattan (`|ddx| + |ddy|`) and
over-reads by up to √2 with screen direction — a true 1 px line reads 1.00 axis-aligned but 0.41 at 45° —
which would make antialiasing quality depend on which way a road runs. **Both AA coverage ramps use the
same Euclidean `sideGrad`** (A6.0) — the width estimate simply reuses it. `_Blur` and dash keep `fwidth`:
they are separate style features, not antialiasing.

`[Enum(...)]` is a UI-only drawer that attaches no keyword: `LineShaderGUI.ValidateMaterial` syncs it in
code, exactly as `_EdgeAntialiasing` does. `_` (Default) is the member of the keyword set that carries **no**
keyword, which is what makes the shipping variant un-strippable from a player build — the same argument as
`_EDGE_ANTIALIASING_OFF`'s polarity, reached the other way round. Never make Default a named keyword.

`_HAIRLINE_SOLID_CORE` is the only strategy with a vertex-stage part, so it needs the compensation scalar in
the fragment: `LineVaryings.uv` is `float4` in **`Line_LitForwardPass.hlsl` only**. The other four passes
`clip(LineCoverage(...) − 0.5)` and keep `float3`. **Both halves ship together** — the clamp alone renders a
1 px road with twice the styled ink (measured Σ = 2.007), and the compensation alone leaves the
phase-dependent tent it was meant to remove (measured spread 0.398). The 0.5 peak is the design, not a dim
bug. *Known limit:* those four passes would clip a hairline's silhouette at the **clamped** width, because
`LineCoverage`'s signature is untouched and the compensation is applied after it in the forward pass only.
They are capability-only and inert today (URP excludes `Queue=Transparent` from the opaque prepasses). **The
fix, if S69 activates them:** widen those four `Varyings` structs the same way and multiply by the scalar
before `clip(... − 0.5)` — the same two-line change already made in `Line_LitForwardPass.hlsl`.

**Variant arithmetic:** `_EDGE_ANTIALIASING_OFF` (2) × strategy (3) = 6 local combinations per pass, of
which the three AA-off ones are behaviourally identical (the strategy blocks are guarded on
`!defined(_EDGE_ANTIALIASING_OFF)`, so AA-off wins). Accepted deliberately: folding them into one
mutually-exclusive set would rewrite A3's shipped keyword surface and make "AA off" and "hard" inexpressible
independently in the Inspector. A player build shipping `MapLine.mat` at its defaults compiles one of the
six; the rest are Editor compile time only.

**Known limit — world-unit widths have no min-width floor.** The floor at `Line_VertexExtrude()` is
pixel-width-only, so under `_HAIRLINE_HARD` a sub-pixel *world-unit* line renders as a broken dashed line
(lit only where a pixel centre falls inside the styled half-width) rather than today's faint continuous one.
No production layer takes that path — `MaterialFactory` sets `_WidthIsPixels = 1` for every style layer —
but the committed `MapLine.mat` itself ships `_WidthIsPixels: 0`. Do not "fix" this by touching the floor.

## Render-state design

- `ForwardLit`: S58-parameterized (`Blend [_SrcBlend] [_DstBlend]`, `ZWrite [_ZWrite]`, etc.).
- Passes 2–5: hardcode `ZWrite On / Cull Off / ZTest LEqual` inside each `Pass { }` block.
  They cannot inherit the SubShader-level S58 params (defaults are ZWrite Off / ZTest LEqual);
  each pass must declare its own depth-write state explicitly.
