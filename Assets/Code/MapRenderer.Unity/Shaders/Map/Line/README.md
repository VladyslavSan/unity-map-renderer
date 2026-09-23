# Map/Line — shader notes

Design rationale and pass-set structure for `Map/Line` and its unlit twin `Map/LineUnlit`. The
antialiasing model's design — invariants, rejected directions, known limits, open questions — is
`docs/line-antialiasing-design.md`; the hairline measurements live here, in "Hairline strategies". The width
model is `docs/line-rendering-design.md` § "The width model".

## Pass set

| Shader | Pass | LightMode | Status |
|---|---|---|---|
| `Line.shader` | `ForwardLit` | `UniversalForward` | Active — transparent forward rendering |
| | `ShadowCaster` | `ShadowCaster` | Capability-only |
| | `DepthOnly` | `DepthOnly` | Capability-only |
| | `DepthNormals` | `DepthNormals` | Capability-only |
| | `GBuffer` | `UniversalGBuffer` | Capability-only |
| `LineUnlit.shader` | `Unlit` | `UniversalForward` | Active |
| | `DepthOnly` | `DepthOnly` | Capability-only |
| | `DepthNormalsOnly` | `DepthNormalsOnly` | Capability-only |

**Capability-only** means the pass is present and compiles, but URP never executes it: lines use
`Queue=Transparent` (≥ 2501), which URP excludes from the opaque depth/GBuffer prepasses. They exist
so the line rendering mode can go opaque, or force shadows, without adding passes.

The unlit twin has **no ShadowCaster**: stock URP Unlit has none, because an unlit material cannot
cast a shadow. Both twins reuse `Line_DepthOnlyPass.hlsl` and
`Line_DepthNormalsPass.hlsl` verbatim from the kind root, so six pass instances come from four files.

## Why a line needs bespoke pass bodies

Fill's depth passes work by running flat geometry through the shared `MapVertexModify` hook. The line
cannot reuse that hook — its geometry is **built in the vertex shader** by world-space lateral
extrusion, and it must emit three per-vertex coverage outputs that an `inout float3 position` hook
cannot express.

Every pass must replay the **same** extrusion, or it writes depth/normals for the un-extruded
centreline instead of the actual ribbon silhouette. `Line_VertexExtrude.hlsl` is that single site:
it defines `LineAttributes`, `Line_VertexExtrude()` and `LineCoverage()`, and every pass of both
twins includes it before its own body. One extrusion site — silhouette divergence between passes
cannot happen.

## Include order (every line pass)

```hlsl
// in each Pass's HLSLPROGRAM, after #pragmas:
#include "Line_<Mode>Input.hlsl"        // 1. CBUFFER + DOTS bridge (props, SRP Batcher)
#include "../Line_VertexExtrude.hlsl"   // 2. LineAttributes + Line_VertexExtrude() + LineCoverage()
#include "Line_<Pass>.hlsl"             // 3. pass body (vertex/fragment entry points)
```

`Line_VertexExtrude.hlsl` sits at the kind root because both shading modes share it; the `../` reach
is one of the two sanctioned shapes (`Shaders/README.md` "Include rules").

## Coverage clip in depth passes

Each non-forward pass calls `LineCoverage(side, innerFrac, dashU)` and `clip(coverage - 0.5)`.
`LineCoverage` takes screen-space derivatives of `side` and `dashU` — a fragment-stage operation — so
`side/innerFrac/dashU` must be **interpolated varyings** in every pass, never per-vertex constants; a
by-value constant has a zero derivative and every ramp collapses to a hard edge. That is why the line
depth passes carry those three interpolators even though depth passes normally carry only
`positionCS`.

## Antialiasing model — a strict one-pixel straddle (toggleable)

`LineCoverage` ramps coverage `1 → 0` across **exactly one device pixel centred on the styled edge** —
half a pixel inside, half outside. Two properties follow, and both are load-bearing:

- the **50 % contour sits on the styled edge**, so apparent width is unchanged;
- the interior stays **`a == 1`**, which is what lets a cased road's fill sit on its casing without
  bleeding it through the seam.

The ribbon extrudes half a device pixel past the styled half-width, because the outer half of the ramp
needs somewhere to land (the rasterizer only produces fragments where a triangle covers a pixel
centre). That pad goes into `outerWorld` **before** the miter multiply, so the *perpendicular* pad
stays 0.5 px at any corner: the miter factor is `1/cos(θ/2)`, so padding after the multiply would give
`pad·cos(θ/2)`, which shrinks toward zero exactly where geometry is tightest. The pad is measured with
`MapPixelsToWorld` and **never** as `0.5 × pxToWorld` — `pxToWorld` is a unit conversion (literally
`1.0` when the width is already in world metres), so reusing it would pad a world-unit layer by half a
**metre**.

`innerFrac` is re-derived against the padded outer, and the gap-hole cut is a symmetric ±0.5 px
straddle centred on `|side| == innerFrac`: `saturate((|side| − innerFrac)/|∇side| + 0.5)`. That `+ 0.5`
is what centres it — the outer formula would shift the hole half a pixel outward.

The ramp width is a **compile-time constant**; there is nothing bindable to widen. Thin lines keep the
**min-width floor** in `Line_VertexExtrude()` (half-width `≥ 0.5 px` ⇒ a stable 1 px hairline).

### Why the fade lives at the edge and not inside the band

A fade *inside* the styled band is a compositing failure, not a coverage one: the fill's skirt ramps
to transparent over the casing and shows it through. Neither a wider fade nor MSAA fixes that — the
maintainer measured MSAA to 16× with no meaningful gain. A strict straddle escapes it because
everywhere the fill is meant to be opaque `a == 1`, and `dst = src·a + dst·(1−a)` cannot bleed there.
The trilemma — {solid core, unchanged apparent width, antialiased}, pick two — is resolved by paying
for the third with **geometry** (the 0.5 px pad) rather than with the fade. There is no successor to a
tunable AA width, because widening a fade blurs and never adds resolution.

### The toggle, and why its polarity is that way round

The whole mechanism is switchable, which is also the maintainer's A/B. `_EdgeAntialiasing`
(`[ToggleUI]`, default `1`) drives `_EDGE_ANTIALIASING_OFF`, declared plain
`#pragma shader_feature_local` in **every** pass of both twins — the `_fragment` form would strip it
from the vertex stage, and since the pad *is* a vertex-stage change the passes would then disagree on
the silhouette.

`_OFF` means the **shipping** (AA-on) variant carries no keyword, so it can never be stripped from a
player build. Inverting it would make AA work in the Editor and silently vanish when built.
`[ToggleUI]` attaches no keyword on its own — `LineShaderGUI.ValidateMaterial` (and
`LineUnlitShaderGUI.ValidateMaterial` for the twin) syncs it in code, mirroring `_ReceiveShadows`.
With the keyword set, the OFF branches are the original pre-AA expressions **verbatim**, not the new
ones with a zero added.

### `_Blur` is a style property, not the AA knob

`_Blur` is MapLibre `line-blur` — an opt-in inward soft edge, bound from the style by
`MaterialFactory`, defaulting to `0` (hard) and multiplying on top of the AA ramp. It is not
antialiasing. The naming rule it enforces: **never name an internal render param after a
`line-*`/`fill-*` term**, and keep the two prop groups labelled separately in `Line_LitInput.hlsl` /
`Line.shader`. A collision there makes the styler silently write an engine param.

### The width estimate is Euclidean, not `fwidth`

`side` is baked ±1, so `|∇side| = 1/H` for a padded half-width `H`, giving
`styledHalf = 1/|∇side| − 0.5`. `fwidth` is Manhattan (`|ddx| + |ddy|`) and over-reads the true length
by `|cos θ| + |sin θ| ∈ [1, √2]` — so the same road would render softer **and** thinner where it runs
diagonally, losing 0.41 px of ink at 45°. Antialiasing quality must not depend on which way a road
runs. **Both AA coverage ramps use the same Euclidean `sideGrad`**, and the width estimate reuses it.
`_Blur` and dash keep `fwidth`: they are separate style features, not antialiasing.

## Hairline strategies — `_HairlineStrategy`

The straddle's solid core is `W − 1` device pixels wide, so **at `W ≤ 1` it vanishes**. What is left
is a tent: the profile peaks at `1 − φ`, where `φ` is the distance from the centreline to the nearest
pixel centre. A one-pixel road therefore renders as one bright pixel or two half-bright ones depending
on sub-pixel phase — energy is conserved, but peak brightness oscillates as that phase drifts, and the
line shimmers under motion. That oscillation is the whole problem; both strategies exist to remove it.

| Value | Keyword | Behaviour |
|---|---|---|
| 0 — Default | *(none)* | the plain straddle |
| 1 — Hard | `_HAIRLINE_HARD` | the ramp narrows toward a step as the painted band falls below 2 px |
| 2 — SolidCore | `_HAIRLINE_SOLID_CORE` | the band is clamped to 2 px so it always has a solid core, and coverage is scaled by `W_true/W_min` so the ink is unchanged |

Measured at `W = 1`, swept across sub-pixel phase:

| strategy | peak | spread | ink (Σ) |
|---|---|---|---|
| Default | 1.000 → 0.602 | **0.398** | 1.000 |
| Hard | 1.000 | **0.000** | 1.000 |
| SolidCore | 0.506 | **0.000** | ~1.01 |

Both strategies remove the shimmer. They differ in what they trade for it, and **the trade is not
free on either side** — read this before choosing one. Neither pops at its threshold: peak brightness
varies smoothly with width across each strategy's engagement range.

**Hard** keeps full brightness and takes the classic hard-edge positional snap. It also gives up
width-proportionality over `(1.0, 1.2]` px: a 1.2 px road paints the same single full pixel as a
1.0 px one. *Below* 1.0 px the flatness is **not** Hard's doing — it is the min-width floor, which
pins any pixel-width line to a 1 px hairline and behaves identically under Default.

**SolidCore** stays width-proportional all the way down, but buys that by **bypassing the min-width
floor entirely**. Its clamp forces the extruded half-width to ≥ 1.5 px, always above the floor's
1.0 px, so `minHalfWorld` can never bind. At `W = 0.8` px: Default renders Σ = 1.000 at peak α 0.80,
SolidCore renders Σ = 0.796 at peak α 0.40. **That is the cost:** the floor exists so hairline roads
stay legible when zoomed out, and SolidCore trades legibility-at-a-floor for proportionality.
Sub-pixel roads get genuinely fainter.

### Hard — both edges harden, and the ramp stays centred

`_HAIRLINE_HARD` scales the ramp width by `lerp(0.05, 1, smoothstep(1, 2, bandPx))` while keeping the
transition **centred on the styled edge**. Narrowing without re-centring leaves the hard edge half a
pixel out and renders the line a pixel fat — the pixel-outset artefact. At `bandPx ≥ 2` the expression
is algebraically the default one, so wide lines are untouched.

`bandPx` measures what is actually **painted** — for a hollow/cased line that is the ring,
`halfStyled − innerFrac·halfPad`, not the outer radius — and the inner gap-hole straddle narrows with
the same `rampPx`. Hardening only the outer silhouette would leave a hairline-thin casing ring crisp
outside and still shimmering inside, on the same ring, which is worse than either consistent choice.

### SolidCore — both halves ship together

`_HAIRLINE_SOLID_CORE` is the only strategy with a vertex-stage part, so it needs a compensation
scalar in the fragment: the line `uv` varying is `float4` in **the two forward passes**, whose `w`
carries `hairlineScale`. The capability passes `clip(LineCoverage(...) − 0.5)` and keep `float3`.

**Both halves or neither** — the clamp alone renders a 1 px road with twice the styled ink (measured
Σ = 2.007), and the compensation alone leaves the phase-dependent tent it was meant to remove
(measured spread 0.398). The 0.5 peak is the design, not a dim bug.

*Known limit:* the capability passes clip a hairline's silhouette at the **clamped** width, because
`LineCoverage`'s signature is untouched and the compensation is applied after it in the forward passes
only. They are inert today. Activating them means widening those `Varyings` structs the same way and
multiplying by the scalar before `clip(... − 0.5)` — the same two-line change the forward passes
already carry.

### `[Enum]` attaches no keyword, and Default must stay keyword-less

`[Enum(...)]` is a UI-only drawer; `LineShaderGUI.ValidateMaterial` syncs the keyword in code, exactly
as `_EdgeAntialiasing` does. `_` (Default) is the member of the keyword set that carries **no**
keyword, which is what makes the shipping variant un-strippable from a player build — the same
argument as `_EDGE_ANTIALIASING_OFF`'s polarity, reached the other way round. **Never make Default a
named keyword.**

**Variant arithmetic:** `_EDGE_ANTIALIASING_OFF` (2) × strategy (3) = 6 local combinations per pass,
of which the three AA-off ones are behaviourally identical (the strategy blocks are guarded on
`!defined(_EDGE_ANTIALIASING_OFF)`, so AA-off wins). Folding them into one mutually-exclusive set
would make "AA off" and "hard" inexpressible independently in the Inspector, so the redundancy is
accepted. A player build shipping the base line material at its defaults compiles one of the six; the
rest are Editor compile time only.

### Known limit — world-unit widths have no min-width floor

The floor in `Line_VertexExtrude()` is pixel-width-only, so under `_HAIRLINE_HARD` a sub-pixel
*world-unit* line renders as a broken dashed line (lit only where a pixel centre falls inside the
styled half-width) rather than a faint continuous one. No production layer takes that path —
`MaterialFactory` sets `_WidthIsPixels = 1` for every style layer — but the committed base materials
(`Materials/Map/Line/{Lit,Unlit}/Line.mat`) themselves ship `_WidthIsPixels: 0`, so anything rendering
with a base material directly does. Do not "fix" this by touching the floor.

## Render-state design

- The forward pass takes the SubShader's parameterized render state (`Blend [_SrcBlend] [_DstBlend]`,
  `ZWrite [_ZWrite]`, `ZTest [_ZTest]`, `Cull [_Cull]`, `BlendOp [_BlendOp]`), driven by the typed
  tweaker layer and the ShaderGUI.
- The depth/shadow/GBuffer passes inherit `Cull [_Cull]` but **override `ZWrite On`** (plus
  `ZTest LEqual` where they need it) inside each `Pass { }` block. They cannot inherit the
  SubShader-level state, whose transparent default is `ZWrite Off` — that would break their depth
  and shadow writes.
- The full blend-state property family (`_Surface`, `_Blend`, `_SrcBlend`, `_DstBlend`, `_ZWrite`,
  `_ZTest`, `_Cull`, `_BlendOp`) must be **declared** even though ShaderLab fixes the render state,
  because URP's `ValidateMaterial` resolves the queue from `_Surface`/`_QueueControl` and needs
  `material.HasProperty` to be true. Without the declarations it defaults the material to opaque and
  forces queue 2000 on import, clobbering `Queue=Transparent`.
