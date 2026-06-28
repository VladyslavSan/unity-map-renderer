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

## Render-state design

- `ForwardLit`: S58-parameterized (`Blend [_SrcBlend] [_DstBlend]`, `ZWrite [_ZWrite]`, etc.).
- Passes 2–5: hardcode `ZWrite On / Cull Off / ZTest LEqual` inside each `Pass { }` block.
  They cannot inherit the SubShader-level S58 params (defaults are ZWrite Off / ZTest LEqual);
  each pass must declare its own depth-write state explicitly.
