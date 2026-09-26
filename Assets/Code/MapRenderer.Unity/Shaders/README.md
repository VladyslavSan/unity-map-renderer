# Shaders

The map renderer's HLSL shader tree. Layout, include rules, and naming conventions live here so a
new layer (or a file move) has a rule to follow rather than a precedent to reverse-engineer.

## Architecture (rendering logic)

### Minimal delta over stock URP

Every map shader is **stock URP Lit or Unlit, verbatim, minus only genuine logic deltas**. Verbatim
copies of both URP sets are vendored at `Unity/Lit/` (`Shader "Template/UnityLit"`) and
`Unity/Unlit/` as diff baselines — a `diff` of a map pass against its vendored counterpart reduces
to exactly:

1. the Properties header (map paint props + the tweakable render-state params
   `_SrcBlend/_DstBlend/_SrcBlendAlpha/_DstBlendAlpha/_ZWrite/_ZTest/_Cull/_BlendOp`),
2. those render-state commands parameterized, and
3. the layer hook — `MapVertexModify` (Fill, FillExtrusion) or `Line_VertexExtrude` (Line) — plus
   the coverage/alpha it feeds and the keywords that gate it.

Everything else stays byte-identical, **including the full keyword-gated feature set**. Do not
hand-strip "unused" keywords (`_NORMALMAP`, `_DETAIL`, …): `shader_feature` compiles them out when
the material does not set them, so keeping them costs nothing and preserves parity.

The unlit twins omit `_NORMALMAP` / `_PARALLAXMAP` / `_DETAIL_*`, and that **is** the parity rather
than an exception to it: stock URP Unlit declares none of them, and a `<Layer>_UnlitInput.hlsl`
declares no `_BumpMap` / `_DetailAlbedoMap` CBUFFER member, so enabling one would compile a branch
that reads properties the shader never declares. See `Unity/Lit/README.md`.

### No flat-ground assumption — the per-vertex frame

The shaders make **no flat-ground (XZ / +Y) assumption**. The geometric frame is per-vertex and
**supplied by the mesh**, so the same shader renders a flat Mercator map or a globe unchanged:

- **up** = the mesh `NORMAL` stream (`+Y` for Mercator, radial for a globe) — never a hardcoded axis.
- **across** = `extrudeN` (Line `TEXCOORD0`), a **3D tangent-plane vector** (`(x,0,y)` for Mercator;
  `|extrudeN|` = miter factor). The shader extrudes along it directly — no `float3(x,0,z)` rebuild.
- **tangent** = `cross(up, across)` (side-consistent), derived in `Line_VertexExtrude` — a real tangent
  frame (T=along, B=across, N=up) with no extra vertex stream, so normal-map/detail/parallax work.
- **lift** (the 0.001 m z-fight nudge) is along the surface normal, not world `+Y`.

Projection choice therefore lives entirely upstream of the shader: the mesh jobs emit
`position + frame`, and a shader that consumes both needs no branch on which projection produced it.

### Line coverage & uv

`Line_VertexExtrude` is the single extrusion site — every line pass of both twins calls it, so
silhouettes cannot diverge between passes. It emits the line's native parameterization in the
standard `uv` channel: `uv.x` = distance-along in line-width units (`dashU`), `uv.y` = signed cross
`∈[-1,1]` (`side`), `uv.z` = gap inner-fraction. `uv.xy` doubles as the surface UV fed to
`InitializeStandardLitSurfaceData`; `LineCoverage(uv.y, uv.z, uv.x)` produces the
feathered ribbon coverage that drives the forward-pass alpha and the depth/shadow/gbuffer `clip()`.

`uv.xy` is `(dashU, side)`, not a conventional surface UV. A layer that binds a uv-dependent line
texture samples on that axis — which is the axis a `line-pattern` feature wants, and is why no
line material binds one today.

The non-forward line passes are **capability-only** — inert for transparent lines, because URP
excludes `Queue ≥ 2501` from the opaque depth/GBuffer prepasses. They are present so the line
rendering mode can go opaque without adding passes.

### Two rulers, on purpose

The line shader converts pixels to world metres with **two different rulers**, and neither can do the
other's job. `Line_VertexExtrude` binds each one under its own name so a change to one cannot move
the other by accident; that file carries the authoritative per-consumer audit.

| ruler | who reads it | why it must be that one |
|---|---|---|
| `MapPixelsToWorld(centerWS, unitDir_WS)` — a **per-vertex, per-direction measurement** | the AA straddle pad, the **min-width floor** (`minHalfWorld`), the `_HAIRLINE_SOLID_CORE` floor (via the pad), and `line-translate` (its own per-axis calls) | these are SAMPLING-GRID quantities *at that vertex*: half a device pixel of ramp must land on half a device pixel of framebuffer, a 1 px legibility floor must rescue a road **where** it thinned, and a translate is a screen displacement. The line shader binds one `metresPerDevicePx` for all of them so they cannot drift apart |
| `_MapFrameMetersPerDevicePixel` — a **frame constant**, measured off the camera (`2·d_lookAt·tan(fov/2)/viewportPx.y`) and pushed by `MapCamera.SyncToCamera` | `widthWorld`, `line-gap-width`, `line-offset`, and the dash divisor `dashMetersPerUnit` (which reads the global directly) | `line-width: N px` means N px **top-down**: it fixes a world width once and the perspective divide decides the rest. A pattern welded to the ground must not depend on where on screen you look, and neither must the width (`docs/line-rendering-design.md` § "The width model") |

Keep them named and keep them distinct. One expression serving both is how a change to the width
silently rebases every screen quantity that shares it.

### Why the width family cannot ride the measurement

`MapPixelsToWorld` is a *measurement*, so it varies three ways that a styled width must not:

1. **with depth** — `refMag ∝ |clip.w|`, so a far vertex probes with a longer ruler;
2. **with direction** — the probe steps along `across` while dashes run `along`, so two roads at equal
   depth with perpendicular bearings measure differently;
3. **because it is sampled per vertex and interpolated** — a quantity derived from it is a plain
   varying, so the GPU renders the perspective-correct *chord* of a hyperbola. A value derived that
   way steps at every road vertex and depends on the mesh's tessellation density (1.12× between a
   2 km and a 30 km mesh). A flat/Mercator projection never subdivides, so the sparse case is the
   normal one.

It does **not** vary with the *sign* of the direction. The helper multiplies the measured pixel span
by `clipRef.w / clipCenter.w`, dividing out the foreshortening the probe itself picks up. Because
`clip.w` is affine in world position, that cancels identically and to all orders for either sign of
`dirWS`; under an orthographic projection `wRef == w0` bitwise, so the factor is exactly `1.0` and
the helper is **bit-identical** to an uncorrected one. Without the correction the two rulers for one
physical axis differ by `(1+e)/(1−e)`, `e = 0.02·tan(fov/2)·(across·fwd)` — **1.910 %** at fov 60 /
tilt 55°, a **2.336 %** ceiling at fov 60, and exactly **0** when `across ⊥ fwd`.

A frame constant has no depth term, no direction, no sign, and is identical at every vertex, so any
interpolant of it is exact. Depth and direction dependence stay in the measurement on purpose: they
are what keeps the AA pad half a device pixel, and a road N px wide, under tilt.

**A ruler defect is not observable as a screen width.** The two band edges sized with opposite-sign
rulers sit at world offsets `+H·k(1+e)` and `−H·k(1−e)`, so their **world separation is exactly
`2Hk`** — the errors cancel *before* the perspective divide — while the band's centre shifts. Screen
position is *rational* in world offset, so the rendered width moves by a different, much smaller
amount. A screen-width measurement is therefore **not a null** for this class of defect, which is why
`LineProbeSymmetrySnapshotTests` measures **world** offsets and asserts their ratio.

`line-offset` is the one width-family consumer **not** bounded by the styled width: both station
vertices take a *common* offset, so `L` device px of offset leaks `e·L` into the **half-width** —
**12.1 %** at `L = 160` on a 24 px line, and unbounded in `L`.

### Open — `refPx` measures the wrong span for a directional probe

**This needs a maintainer decision. It is not settled, and it is not fixed.**

**Scope.** The line width family (width, `line-gap-width`, `line-offset`) reads the frame constant, so this
affects it only through the missing-push fallback (see "Fail-safes"). The consumers of `MapPixelsToWorld` are
the line's AA pad and min-width floor, the fill band width (`Fill_VertexModify.hlsl`), and the per-axis
translate of line, fill and fill-extrusion. The analysis below applies to each of them; the band-width
figures describe a band sized by this probe.

`refPx` is the *length* of a 2D NDC delta. For a vertex whose screen-x is `sx` px off centre, the
probe's step along `dirWS` changes that vertex's depth, so the projected point slides **radially** as
well as along the intended screen direction — contributing a component ≈ `|sx|·e` px, which adds **in
quadrature**: at `sx = 100` a 0.946 px component grows a 2.91 px span to 3.06 px, i.e. **+5.1 %**.

That is not a harmless refinement, because **a directional consumer does not want the 2D magnitude — it
wants the component along its own screen direction** (for a band width, perpendicular to the line). The radial component points away from the screen
centre; whatever part of it runs *along* the line contributes nothing to the band's perpendicular
thickness, yet inflates `refPx` and so shrinks the returned scale. On an east–west road at heading 0
the radial component is entirely along the road, so **all** of it is spurious: a band sized by this
probe is ~5 % too narrow at `sx = 100`, measured as a **2.8–3.0 px** inward bow of the silhouettes
across the central 200 columns of a 120 px band at fov 60 / tilt 55.

**That is larger than the 1.910 % sign asymmetry the `w`-ratio removes**, and the `w`-ratio does not
touch it — that factor scales both signs alike. It is why the probe-symmetry teeth measure within
**±10 columns** of the screen centre.

The fork: project the NDC delta onto the perpendicular screen direction, or keep taking its
magnitude. Projecting costs a second screen-space direction at every measured vertex and changes
every probe-sized quantity away from the screen centre; keeping the magnitude leaves the bow. Nothing in
`PixelsToWorld.hlsl` records this, so this file is its only home — do not drop it while resolving it.
It is also a candidate lead for `docs/line-antialiasing-design.md` § "Open questions" (the long-segment
sag); that connection is not established.

### Dash period under a world-anchored pattern

A pattern welded to the road **must** foreshorten: the on-screen dash period falls as `depth⁻²`. That
is the semantic working, not a regression — a screen-constant period is the incoherent alternative it
rejects. Where the period reaches a few device pixels the fragment walk degrades: once
`fwidth(dashU)` exceeds a run length the first slot's branch produces a `lerp` ramp instead of a
correct average, and far dashes go soft. The feather itself is unaffected — `fwidth(dashU)`
self-calibrates to the local screen gradient, so the ramp stays ≈2 device px at any depth.

### Fail-safes

`_MapFrameMetersPerDevicePixel` is not a ShaderLab property and is not in `UnityPerMaterial`, so an
unpushed frame reads `0`. Two consumers, two different fail-safes, and the split is intentional:

- **the width family** falls back to the per-vertex measurement. It is unreachable in production —
  `MapCamera.SyncToCamera` pushes the global — so reaching it means a render path forgot to push.
  A plausibly-sized line is a better failure than the alternative: `0` gives `widthWorld = 0`, and
  every road of every styled width collapses to the same 1 device-px hairline, which looks like a
  defect in something other than the missing push. The branch is on a uniform, so no wave diverges.
- **the dash divisor** does *not* take that branch. A divisor of `0` sets `dashU = 0`, which renders a
  **uniform half-coverage line** (`smoothstep(−dfw, +dfw, 0) == 0.5` exactly) — **not** a solid one.
  No dash edges, never a moving pattern: visible and inert, never corrupt. Routing it through the
  width's fallback would trade that benign symptom for a subtle one.

## Layout

The tree is two levels of grouping, and each level has a rule:

- `Map/<Kind>/` is the unit of self-containment — one geometry kind. Its **root** holds the map
  helpers every shading mode of that kind shares: the vertex hook
  (`<Kind>_VertexModify.hlsl` / `Line_VertexExtrude.hlsl`) and the pass bodies that are identical
  across modes (`<Kind>_DepthOnlyPass.hlsl`, `<Kind>_DepthNormalsPass.hlsl`).
- `Map/<Kind>/{Lit,Unlit}/` holds the entry point (`.shader`) and everything mode-specific: the
  `<Kind>_<Mode>Input.hlsl` (CBUFFER + DOTS bridge + `InitializeStandardLitSurfaceData` + paint
  props) and the mode's own pass bodies.

```
Shaders/
  Common/
    LitInput.Template.hlsl   reference skeleton — copy when adding a new layer; NO .shader includes it
  Map/
    PixelsToWorld.hlsl       shared px→world measurement — the one sanctioned Map-root include
    Fill/                    Fill_VertexModify, Fill_BandCoverage, Fill_Depth{Only,Normals}Pass
      Lit/                   Fill.shader + Fill_LitInput + forward/gbuffer/shadowcaster bodies
      Unlit/                 FillUnlit.shader + Fill_UnlitInput + forward body
    FillExtrusion/           same shape as Fill/
      Lit/  Unlit/
    Line/                    Line_VertexExtrude, Line_Depth{Only,Normals}Pass
      Lit/                   Line.shader + Line_LitInput + forward/gbuffer/shadowcaster bodies
      Unlit/                 LineUnlit.shader + Line_UnlitInput + forward body
    Symbol/
      SymbolWorldPitchAlign.hlsl   shared by Text/ and Icon/ — the same sibling-parent pattern
      Text/  Icon/           one .shader + _Input.hlsl + _ForwardPass.hlsl each
  Unity/
    Lit/  Unlit/             verbatim vendored URP sets — diff baselines, bound by no material
```

`Common/` holds only `LitInput.Template.hlsl`, a Map-flavoured skeleton that **no `.shader` ever
includes** (the `.Template.hlsl` infix signals "reference only"). New layers copy it and add their
own paint props at the `// ── <Layer> paint props go here ──` markers.

A helper **genuinely shared by two or more kinds** lives at their sibling-parent — `Shaders/Map/` for
`PixelsToWorld.hlsl`, `Shaders/Map/Symbol/` for `SymbolWorldPitchAlign.hlsl` — never in `Common/`.
`Common/` stays template-only: a shared framework extracted for one consumer costs confusing naming
and a `Common/` that is not common, so extract only when a **second kind genuinely needs it**.

## Include rules

The `.shader` (not the pass body) controls includes, in the natural define-before-use order, matching
Unity's own shaders:

```hlsl
// in each Pass's HLSLPROGRAM, after the #pragmas:
#include "<Kind>_<Mode>Input.hlsl"   // 1. CBUFFER + DOTS bridge + surface helpers (declares props)
#include "../<Kind>_VertexModify.hlsl"  // 2. the vertex hook — before any pass uses it
#include "<Kind>_<Pass>.hlsl"        // 3. pass body — vertex/fragment entry points
```

Pass bodies `#include` only the URP library headers they need (`Lighting.hlsl`, `Shadows.hlsl`, …);
they do **not** re-include their own layer input. The `.shader` already provides it. Define-before-use
is the rule — a pass body included before the file that defines its hook would need a
forward-declaration prototype to compile, and that prototype is what makes an include order rot
unnoticed.

A `../` reach is sanctioned in exactly two shapes, and
`ShaderStructureTests.MapLayerFiles_ShareOnlyViaSanctionedInclude` validates them **structurally** —
each `../` target must resolve on disk to one of the two — rather than against a name allow-list:

1. **`../PixelsToWorld.hlsl`** at the `Map/` root, reached from each kind's vertex file.
2. **The Lit/Unlit split** — a file under `Map/<Kind>/{Lit,Unlit}/` reaching one level up to its
   **own** kind root for that kind's shared includes.

Anything else — a reach into `Common/`, into a sibling kind, or outside `Map/` — fails, as does a
`../` include that resolves to nothing. Intra-folder includes are bare
(`#include "Fill_LitInput.hlsl"`).

## Naming

- File names: `<Kind>_<OriginalUnityName>` — each file is named for its kind and the URP file it
  derives from. The URP origin is cited in each file's provenance header.
  Examples: `Fill_LitForwardPass.hlsl` ← URP `LitForwardPass.hlsl`; `Line_LitInput.hlsl` ← URP `LitInput.hlsl`.
- A map-specific helper with no URP counterpart is named for its **function** instead —
  `Fill_VertexModify.hlsl`, `Line_VertexExtrude.hlsl`, `PixelsToWorld.hlsl`.
- Shaders declare `Shader "Map/<Kind>"` (e.g. `Map/Fill`, `Map/Line`), the unlit twins `Map/<Kind>Unlit`.
- Materials bind their shader **by GUID** (the `.mat`'s `m_Shader` guid → the `.shader.meta` guid), so
  renaming the `Shader "…"` string does not break a material. `Shader.Find` depends on the declared name:
  the tests use it, and so does `SkyGradient` for `Map/Sky` (see "The sky" below).
- The custom inspector is bound via `CustomEditor` by C# class name, not shader name — unaffected by
  shader renames.

## The sky

`Sky/Sky.shader` (`Map/Sky`) is the one shader outside `Map/`: it draws no layer kind, it is a skybox.
`SkyGradient` binds a runtime instance to the camera's `Skybox` component, never `RenderSettings.skybox`,
so the ambient convolution and the default reflection do not see it. No asset references it, so it is in
GraphicsSettings' Always Included Shaders (`SkyShader_IsInAlwaysIncludedShaders`). It is also the one
shader that takes world `+Y` as up with no mesh frame. That holds on both projections, because globe
tiles are rebased into the look-at tangent frame. The blend spans the visible sky strip: from
`_MapEdgeElevation`, the ray elevation where the rendered map ends (the far cut, or the globe's limb when
nearer), to `_SkyTopElevation`, the ray through the top of the screen. So the colour depends on the view,
not only on the ray's direction. `SkyGradient.UpdateMapEdge` pushes both every frame from the committed
camera.

## The haze

Every fill/line map forward pass mixes URP linear fog per fragment, with depth counted from the near plane
(`EveryMapForwardPass_IncludesUrpFog`). The Unlit twins carry view-space z to the fragment instead of a
per-vertex factor, so a tile-sized triangle does not clamp the fog at its vertices. `DistanceHaze` is the
only writer of the `RenderSettings` fog fields. The symbol shaders do not mix fog colour per fragment: they
scale alpha by the fog visibility at the anchor, computed once per vertex, so a label fades out in full haze
and collision is unchanged. `Map/Sky` takes no fog. Fog stripping is Custom and keeps Linear only (`FogStripping_KeepsTheHazeFogMode`), because
no scene enables fog and Automatic stripping would drop the variants the haze turns on at runtime.

## Lines fork on purpose

Line keeps its own input header and forward pass — not because Line is special-cased, but because
each kind is self-contained. Specifically: the line's TEXCOORD attribute set (TEXCOORD0 = extrudeN,
TEXCOORD1 = side+dist, TEXCOORD2 = widthScale) clashes with the fill input's UV set, so they cannot
share an input header. `InitializeStandardLitSurfaceData` is duplicated verbatim across them — this
is the honest duplication, co-located and visible, rather than a speculative `Common/` abstraction
that never actually shared.

## Shared px→world include

`Fill_VertexModify.hlsl`, `Line_VertexExtrude.hlsl` and `FillExtrusion_VertexModify.hlsl` each
`#include "../PixelsToWorld.hlsl"` — one `MapPixelsToWorld(centerWS, dirWS)` measurement at
`Shaders/Map/PixelsToWorld.hlsl`, the sibling-parent of all three.

The fence on it is **positive**, and the shape matters: a test that compares per-carrier copies byte
for byte catches drift between copies that exist and says nothing when a copy comes back.
`SharedPixelsToWorldInclude_ReferencedByEveryCarrier` instead asserts that every carrier includes the
shared file **and** that no carrier locally re-defines the function.

The include's **position** inside a carrier is load-bearing, not cosmetic: it must sit after the
carrier's own include-guard `#define` and before the first call, because its body reads URP macros
(`TransformWorldToHClip`, `UNITY_MATRIX_P`, `_ScreenParams`) that only the layer input declares.

## DOTS / BRG instancing

Lit shaders carry `#pragma multi_compile_instancing` and `UNITY_DOTS_INSTANCED_PROP` property sets so
they render under the BatchRendererGroup backend. The CBUFFER, the `UNITY_DOTS_INSTANCING` block, and
any BRG SoA packing must stay byte-aligned, and the CBUFFER must be byte-identical across every pass
of a shader — the SRP Batcher requires it. Never add or remove a CBUFFER member in per-pass code.
