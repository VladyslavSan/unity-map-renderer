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
`LineCoverage` uses `fwidth()` — a fragment-stage function — so `side/innerFrac/dashU` must be
**interpolated varyings** in every pass (not computed per-vertex constants). This is why the line
depth passes carry those three interpolators even though depth passes normally carry only `positionCS`.

## Antialiasing model — opaque core + edge buffer

The line is **opaque-core + feathered-edge**: `Line_VertexExtrude()` pads the lateral extrude OUTWARD by
`(_AaEdgeWidth + _Blur)` device pixels (perspective-correct, measured per-vertex from the projected
centre/edge), and `LineCoverage`'s `fwidth` smoothstep falls off over that same width — so the styled width
stays `coverage = 1` (fully opaque) and only the buffer is partial. The interior is never made translucent,
so overlapping roads/casings/joins composite with **no alpha accumulation**.

**Two distinct properties — do NOT merge them** (see `docs/lessons-learned.md` § Shaders & HLSL):

| Property | Meaning | Default | Style-bound? |
|---|---|---|---|
| `_AaEdgeWidth` | internal AA buffer/edge width, px per side | 1 | **no** (engine plumbing) |
| `_Blur` | MapLibre **`line-blur`** paint | 0 | yes (`MaterialFactory` binds `line-blur`) |

`_AaEdgeWidth` was once named `_Blur`; that collided with the `line-blur` style term (`line-X → _X`), so the
styler bound its spec-default `0` over it and **silently zeroed antialiasing on every backend**. Internal
render params (`_AaEdgeWidth`, `_WidthIsPixels`, `_MetersPerPixel`) are kept in a separate labeled group
from the style-bound `line-*` props in `Line_LitInput.hlsl` / `Line.shader` — keep that boundary.

**Known residual artifact (DEFERRED): sub-pixel lines over-thicken.** The pad is *geometric*, so a line
narrower than the buffer (e.g. ~0.3px) is fattened to ~`0.3 + 2·_AaEdgeWidth` ≈ 2.3px and blurred; two such
thin lines close together have their buffers overlap into a smeared mess. Fixing it needs coverage-conserving
sub-pixel handling (clamp geometry to ~1px, scale alpha by the true width ratio) — which re-opens the
translucent-overlap problem and likely needs the whole-layer composite. Tracked in
`agents-devloop/stages/S70-line-antialiasing-subpixel-crawl.md` § "Known residual artifact".

## Render-state design

- `ForwardLit`: S58-parameterized (`Blend [_SrcBlend] [_DstBlend]`, `ZWrite [_ZWrite]`, etc.).
- Passes 2–5: hardcode `ZWrite On / Cull Off / ZTest LEqual` inside each `Pass { }` block.
  They cannot inherit the SubShader-level S58 params (defaults are ZWrite Off / ZTest LEqual);
  each pass must declare its own depth-write state explicitly.
