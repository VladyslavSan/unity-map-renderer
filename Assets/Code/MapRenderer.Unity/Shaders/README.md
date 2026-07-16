# Shaders

The map renderer's HLSL shader tree. Layout, include rules, and naming conventions live here so a
future layer (or a file move) has a rule to follow rather than a precedent to reverse-engineer.

Updated in **S66** (structural cleanup): each layer is now self-contained; `Common/` holds only
a reference template, not the live framework.

## Architecture (rendering logic)

The folder rules below are *structure*; this section is the *logic* — what these shaders compute and
the invariants they hold.

### Minimal delta over stock URP Lit

Every map shader is **stock URP Lit, verbatim, minus only genuine logic deltas**. A verbatim copy of
the URP Lit set is vendored at `UnityLit/` (`Shader "Template/UnityLit"`) as a diff baseline — a `diff`
of a map pass against its `UnityLit/` counterpart should reduce to exactly:

1. the Properties header (map paint props + the tweakable render-state params
   `_SrcBlend/_DstBlend/_SrcBlendAlpha/_DstBlendAlpha/_ZWrite/_ZTest/_Cull/_BlendOp`),
2. those render-state commands parameterized, and
3. the layer hook — `MapVertexModify` (Fill) or `Line_VertexExtrude` (Line) — plus the coverage/alpha.

Everything else stays byte-identical, **including the full keyword-gated feature set**. Do not
hand-strip "unused" keywords (`_NORMALMAP`, `_DETAIL`, …): `shader_feature` compiles them out when the
material doesn't set them, so keeping them costs nothing and preserves parity. See `UnityLit/README.md`.

### No flat-ground assumption — the per-vertex frame

The shaders make **no flat-ground (XZ / +Y) assumption**. The geometric frame is per-vertex and
**supplied by the mesh**, so the same shader renders a flat Mercator map or a globe unchanged:

- **up** = the mesh `NORMAL` stream (`+Y` for Mercator, radial for a globe) — never a hardcoded axis.
- **across** = `extrudeN` (Line `TEXCOORD0`), a **3D tangent-plane vector** (`(x,0,y)` for Mercator;
  `|extrudeN|` = miter factor). The shader extrudes along it directly — no `float3(x,0,z)` rebuild.
- **tangent** = `cross(up, across)` (side-consistent), derived in `Line_VertexExtrude` — a real tangent
  frame (T=along, B=across, N=up) with no extra vertex stream, so normal-map/detail/parallax work.
- **lift** (the 0.001 m z-fight nudge) is along the surface normal, not world `+Y`.

What's still Mercator-tied is the **position** itself: the mesh builders project 2D tile geometry to a
flat `Vector3(x, 0, y)` via `WebMercator.Forward`. Making *placement* projection-agnostic is the globe
epic — `IProjection` exposes a stateless `ProjectPoint` math + a `Kind` enum the Burst mesh job switches on,
which emits `position + frame` for any projection; the mesh then bakes a non-zero-Y `across` + radial
normals and **these shaders consume them with no change**. (Fill's `MapVertexModify` translate is the
last per-vertex spot still written in XZ; it moves to the normal-relative frame with the same edit.)

### Line coverage & uv

`Line_VertexExtrude` is the single extrusion site — all five line passes call it, so silhouettes are
identical by construction. It emits the line's native parameterization in the standard `uv` channel (a
`float3`): `uv.x` = distance-along in line-width units (`dashU`), `uv.y` = signed cross `∈[-1,1]`
(`side`), `uv.z` = gap inner-fraction. `uv.xy` doubles as the real surface UV fed to
`InitializeStandardLitSurfaceData` (not `float2(0,0)`); `LineCoverage(uv.y, uv.z, uv.x)` produces the
`fwidth`-feathered ribbon coverage that drives the forward-pass alpha and the depth/shadow/gbuffer
`clip()`. The four non-forward line passes are **capability-only** — inert for transparent lines
(URP skips Queue ≥ 2501), present so S69 can flip lines opaque without adding passes.

## Layout

```
Shaders/
  Common/
    LitInput.Template.hlsl   reference skeleton — copy when adding a new layer; NO .shader includes it
  Map/
    Fill/
      Fill.shader
      Fill_LitInput.hlsl       CBUFFER + DOTS bridge + InitializeStandardLitSurfaceData + fill paint props
      Fill_VertexModify.hlsl   MapVertexModify body (fill-translate)
      Fill_LitForwardPass.hlsl
      Fill_LitGBufferPass.hlsl
      Fill_ShadowCasterPass.hlsl
      Fill_DepthOnlyPass.hlsl
      Fill_DepthNormalsPass.hlsl
    Line/
      Line.shader
      Line_LitInput.hlsl         CBUFFER + DOTS bridge + InitializeStandardLitSurfaceData + line paint props
      Line_VertexExtrude.hlsl    LineAttributes struct + Line_VertexExtrude() + LineCoverage() — shared by all line passes (S67)
      Line_LitForwardPass.hlsl
      Line_LitGBufferPass.hlsl   (S67, capability-only — inert for transparent lines)
      Line_ShadowCasterPass.hlsl (S67, capability-only — inert for transparent lines)
      Line_DepthOnlyPass.hlsl    (S67, capability-only — inert for transparent lines)
      Line_DepthNormalsPass.hlsl (S67, capability-only — inert for transparent lines)
```

- `Map/<Layer>/` holds one geometry kind's `.shader`, its `<Layer>_LitInput.hlsl`, its pass bodies,
  and (Fill only) `Fill_VertexModify.hlsl`. Each layer is fully self-contained — no cross-folder
  `../../Common/…` includes.
- `Common/` holds only `LitInput.Template.hlsl`, a Map-flavoured skeleton that **no `.shader` ever
  includes** (the `.Template.hlsl` infix signals "reference only"). New layers copy it and add their
  own paint props at the `// ── <Layer> paint props go here ──` markers.
- `Common/` grows only when a pass or helper is **genuinely shared by two or more layers**. Never
  speculatively; this entire stage is the cost of having done so once.

## Include order (every pass, every layer)

The `.shader` (not the pass body) controls includes, in the natural define-before-use order, matching
Unity's own shaders:

```hlsl
// in each Pass's HLSLPROGRAM, after the #pragmas:
#include "<Layer>_LitInput.hlsl"     // 1. CBUFFER + DOTS bridge + surface helpers (declares props)
#include "Fill_VertexModify.hlsl"    // 2. (Fill only) MapVertexModify body — before any pass uses it
#include "<Layer>_<Pass>.hlsl"       // 3. pass body — vertex/fragment entry points
```

Pass bodies `#include` only the URP library headers they need (`Lighting.hlsl`, `Shadows.hlsl`, etc.);
they do NOT re-include their own layer input. The `.shader` already provides it.

This replaces the old forward-declaration hack (S66): previously the pass body was included first
(pulling its own layer input), and then the vertex-modify file defined the body — which contradicted
the documented include order and required a prototype declaration to paper over it.
The hack is gone. Define-before-use is the rule.

## Naming

- File names: `<Layer>_<OriginalUnityName>` — each file is named for its layer and the URP file it
  derives from. The URP origin is cited in each file's provenance header.
  Examples: `Fill_LitForwardPass.hlsl` ← URP `LitForwardPass.hlsl`; `Line_LitInput.hlsl` ← URP `LitInput.hlsl`.
- Shaders declare `Shader "Map/<Layer>"` (e.g. `Map/Fill`, `Map/Line`).
- Materials bind their shader **by GUID** (the `.mat`'s `m_Shader` guid → the `.shader.meta` guid), so
  renaming the `Shader "…"` string does not break a material. Only `Shader.Find("Map/<Layer>")` (used
  in tests) depends on the declared name.
- The custom inspector is bound via `CustomEditor` by C# class name, not shader name — unaffected by
  shader renames.

## Lines fork on purpose

Line keeps its own `Line_LitInput.hlsl` and `Line_LitForwardPass.hlsl` — not because Line is
special-cased, but because each layer is self-contained. Specifically: the line's TEXCOORD attribute
set (TEXCOORD0 = extrudeN, TEXCOORD1 = side+dist, TEXCOORD2 = widthScale) clashes with the fill
input's UV set, so they cannot share an input header. The two layers fork their inputs on purpose;
`InitializeStandardLitSurfaceData` is duplicated verbatim across them — this is the honest
duplication, co-located and visible, rather than a speculative `Common/` abstraction that never
actually shared.

## Extract to `Common/` only when truly shared

Only extract a pass or helper to `Common/` when a **second layer genuinely needs it**. Never
speculatively. The cost of extracting speculatively (a shared framework used by one layer, confusing
naming, a `Common/` that is not common) was the motivation for this whole stage.

Intra-folder includes are bare (`#include "Fill_LitInput.hlsl"`). There are no cross-folder
`../../Common/…` includes from any layer.

## DOTS / BRG instancing

Lit shaders carry `#pragma multi_compile_instancing` and `UNITY_DOTS_INSTANCED_PROP` property sets so
they render under the BatchRendererGroup backend (S49). The CBUFFER, the `UNITY_DOTS_INSTANCING`
block, and any BRG SoA packing must stay byte-aligned.
