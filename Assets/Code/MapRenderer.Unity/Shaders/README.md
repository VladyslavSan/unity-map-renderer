# Shaders

The map renderer's HLSL shader tree. Layout, include rules, and naming conventions live here so a
future layer (or a file move) has a rule to follow rather than a precedent to reverse-engineer.

Updated in **S66** (structural cleanup): each layer is now self-contained; `Common/` holds only
a reference template, not the live framework. Updated in **S23 I2a**: the one sanctioned exception to
self-containment is a shared px→world include at `Shaders/Map/PixelsToWorld.hlsl` — see "Shared px→world
include" below.

## Architecture (rendering logic)

The folder rules below are *structure*; this section is the *logic* — what these shaders compute and
the invariants they hold.

### Minimal delta over stock URP Lit

Every map shader is **stock URP Lit, verbatim, minus only genuine logic deltas**. A verbatim copy of
the URP Lit set is vendored at `Unity/Lit/` (`Shader "Template/UnityLit"`) as a diff baseline — a `diff`
of a map pass against its `Unity/Lit/` counterpart should reduce to exactly:

1. the Properties header (map paint props + the tweakable render-state params
   `_SrcBlend/_DstBlend/_SrcBlendAlpha/_DstBlendAlpha/_ZWrite/_ZTest/_Cull/_BlendOp`),
2. those render-state commands parameterized, and
3. the layer hook — `MapVertexModify` (Fill) or `Line_VertexExtrude` (Line) — plus the coverage/alpha.

Everything else stays byte-identical, **including the full keyword-gated feature set**. Do not
hand-strip "unused" keywords (`_NORMALMAP`, `_DETAIL`, …): `shader_feature` compiles them out when the
material doesn't set them, so keeping them costs nothing and preserves parity. See `Unity/Lit/README.md`.

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

### Two rulers, on purpose (S110, S111)

The line shader converts pixels to world metres with **two different rulers**, and neither can do the
other's job.

| ruler | who reads it | why it must be that one |
|---|---|---|
| `MapPixelsToWorld(centerWS, unitDir_WS)` — a **per-vertex, per-direction measurement** | the AA straddle pad, the **min-width floor** (`minHalfWorld`), the `_HAIRLINE_SOLID_CORE` floor (via the pad), and `line-translate` | these are genuinely SAMPLING-GRID quantities *at that vertex*: half a device pixel of ramp must land on half a device pixel of framebuffer, a 1 px legibility floor must rescue a road **where** it thinned, and a translate is a screen displacement. The line shader binds one `metresPerDevicePx` for all of them so they cannot drift apart |
| `_MapFrameMetersPerDevicePixel` — a **frame constant**, measured off the camera (`2·d_lookAt·tan(fov/2)/viewportPx.y`) and pushed by `MapCamera.SyncToCamera` | `widthWorld`, `line-gap-width`, `line-offset`, and the dash divisor `dashMetersPerUnit` | `line-width: N px` means N px **top-down**: it fixes a world width once and the perspective divide decides the rest. A pattern welded to the ground must not depend on where on screen you look, and neither must the width (S116; `docs/line-rendering-design.md` §1) |

**The failure surface — one root, four visible symptoms.** `MapPixelsToWorld` is a *measurement*, and
`dashU` was the one consumer that **integrated** it along the road while every other consumer is bounded
by the styled width. It varies:

1. **with depth** (`refMag ∝ |clip.w|`) — the world period grew with distance and the pattern crawled as
   the camera tilted;
2. **with direction** — the probe steps along `across` while dashes run `along`, so two roads at equal
   depth with perpendicular bearings got different periods;
3. **with the *sign* of the direction** — *historical: S111 removed this term at source, so the
   measurement no longer varies this way at all.* The two ribbon vertices of a station share one centreline
   point and carry opposite `extrudeN`, so they probed the projection in opposite directions and got rulers
   differing by exactly `(1+e)/(1−e)`, `e = 0.02·tan(fov/2)·(across·fwd)` (1.91 % at fov 60 / tilt 55°;
   the exact fov-60 ceiling is **2.336 %** — the `2.31 %` quoted before S111 is the first-order `2e` —
   **exactly 0** when `across ⊥ fwd`). Every dash boundary tilted off perpendicular, by an
   angle growing linearly with accumulated `dashU` — **the diagonal parallelograms**;
4. **because it is sampled per vertex and interpolated** — `dashU` is a plain varying, so the GPU renders
   the perspective-correct *chord* of a hyperbola: the period stepped at every road vertex and depended on
   the road's tessellation density (1.12× between a 2 km and a 30 km mesh). A flat/Mercator projection
   never subdivides, so the sparse case is the normal one.

A frame constant has no depth term, no direction, no sign, and is identical at every vertex so any
interpolant is exact. All four go in one move.

**What S110 left, and S111 fixed.** S110 moved only the dash parameterisation off the measurement; the
measurement itself kept all four dependencies for width / gap / offset / translate. **S111 removed the
*sign* dependence at source**: the helper now multiplies the measured pixel span by
`clipRef.w / clipCenter.w`, dividing out the foreshortening the probe itself picked up. Because `clip.w`
is affine in world position, that cancels *identically and to all orders* for either sign of `dirWS` —
and under an orthographic projection `wRef == w0` bitwise, so the factor is exactly `1.0` and the helper
is **bit-identical** to its pre-S111 self. Depth and direction dependence stay, deliberately: they are
what keeps a road N px wide under tilt.

What the sign asymmetry actually cost — **the pre-S111 text here was wrong in both halves**, quoting the
*ratio* `(1+e)/(1−e)` where an *absolute* `(1±e)` deviation belongs:

- the band's two edges sat at world offsets `+H·k(1+e)` and `−H·k(1−e)`, so their **world separation was
  already exactly `2Hk`** — the errors cancel *before* the perspective divide — while the band's **centre**
  sat `e·h` = **0.0757 px** off the centreline on a 16 px road (0.95 % of the *half*-width). The
  often-quoted **0.15 px** is `2·e·h`, the difference between the two half-widths: true, but mislabelled;
- the AA pad rendered **0.5047 / 0.4953 px**, i.e. `0.5·(1±e)` — *not* the `0.4905 px` this file used to
  claim, which is `0.5·(1−e)/(1+e)`;
- the **rendered screen width was never exactly invariant** either, because screen position is *rational*
  in world offset: correcting the geometry moves it by **+0.008 px** on a 16 px band and **+0.478 px** on a
  120 px one. A screen-width measurement is therefore *not* a null for this defect — which is why S111's
  teeth (`LineProbeSymmetrySnapshotTests`) measure **world** offsets and assert their ratio;
- `line-offset` was the one consumer **not** bounded by the styled width: both station vertices take a
  *common* offset, each measured with its own ruler, so `L` device px of offset leaked `e·L` into the
  **half-width** — **12.1 %** at `L = 160` on a 24 px line, and unbounded in `L`.

**Still open after S111, and NEEDS A DECISION — `refPx` measures the wrong span for the width family.**
`refPx` is the *length* of a 2D NDC delta. For a vertex whose screen-x is `sx` px off centre, the probe's
step along `dirWS` changes that vertex's depth, so the projected point slides **radially** as well as along
the intended screen direction — contributing a component ≈ `|sx|·e` px, which adds **in quadrature**: at
`sx = 100` a 0.946 px component grows a 2.91 px span to 3.06 px, i.e. **+5.1 %**.

That is not a harmless refinement, because **the width family does not want the 2D magnitude — it wants the
component perpendicular to the line.** The radial component points away from the screen centre; whatever
part of it runs *along* the line contributes nothing to the band's perpendicular thickness, yet inflates
`refPx` and so shrinks `pxToWorld`. In the S111 fixture (east–west road, heading 0) the radial component is
entirely along the road, so **all** of it is spurious: the band is ~5 % too narrow at `sx = 100`, measured
as a **2.8–3.0 px** inward bow of the silhouettes across the central 200 columns of a 120 px band at
fov 60 / tilt 55.

**That is larger than the 1.910 % sign asymmetry S111 just removed.** It is unchanged by S111 — the
`w`-ratio scales both signs alike — and it is why the S111 teeth measure within ±10 columns of the screen
centre. Whether to project the NDC delta onto the perpendicular screen direction instead of taking its
magnitude is an open call, not a settled one; it is deliberately **not** fixed here.

**Expected new behaviour, so it is not misfiled as a regression.** Before S110 the on-screen dash period
was *constant everywhere* — which is precisely the incoherent "screen-constant dashes" the world-anchored
semantic rejects. After it, the period falls as `depth⁻²`: 55.06 px at the look-at → 19.87 px at 110 km in
the T1 fixture, and ~16× smaller near a `4·altitude` far plane. At a ~3 px period the fragment walk
degrades (once `fwidth(dashU)` exceeds a run length the `k = 0` branch produces a `lerp` ramp instead of a
correct average) and far dashes go mushy. This is inherent to the semantic — a pattern welded to the road
*must* foreshorten, and MapLibre behaves the same way. The feather itself is fine: `fwidth(dashU)`
self-calibrates to the true local screen gradient, so the ramp stays ≈2 device px before and after.

**Fail-safe.** The global is not a ShaderLab property and is not in `UnityPerMaterial`, so an unset frame
reads `0`; the divisor is 0 and the guard sets `dashU = 0`. That renders a **uniform half-coverage line**
(`smoothstep(−dfw, +dfw, 0) == 0.5` exactly) — **not** a solid one. No dash edges, never a moving pattern:
visible and inert, never corrupt.

**Also note** `dashU` is the surface `u` fed to `InitializeStandardLitSurfaceData` (`uv.xy`, above), so
S110 moved the axis `_BaseMap`/`_BumpMap`/detail maps would sample on. Inert today — `MapLine.mat` binds
no texture and the uv-dependent shader features are gated off — and it is the axis S17 `line-pattern`
wants, but a stage that binds a uv-dependent line texture must know this moved.

## Layout

```
Shaders/
  Common/
    LitInput.Template.hlsl   reference skeleton — copy when adding a new layer; NO .shader includes it
  Map/
    PixelsToWorld.hlsl      shared px→world measurement (S23 I2a) — the one sanctioned cross-folder include
    Fill/
      Fill.shader
      Fill_LitInput.hlsl       CBUFFER + DOTS bridge + InitializeStandardLitSurfaceData + fill paint props
      Fill_VertexModify.hlsl   MapVertexModify body (fill-translate); includes ../PixelsToWorld.hlsl
      Fill_LitForwardPass.hlsl
      Fill_LitGBufferPass.hlsl
      Fill_ShadowCasterPass.hlsl
      Fill_DepthOnlyPass.hlsl
      Fill_DepthNormalsPass.hlsl
    Line/
      Line.shader
      Line_LitInput.hlsl         CBUFFER + DOTS bridge + InitializeStandardLitSurfaceData + line paint props
      Line_VertexExtrude.hlsl    LineAttributes struct + Line_VertexExtrude() + LineCoverage() — shared by all line passes (S67); includes ../PixelsToWorld.hlsl
      Line_LitForwardPass.hlsl
      Line_LitGBufferPass.hlsl   (S67, capability-only — inert for transparent lines)
      Line_ShadowCasterPass.hlsl (S67, capability-only — inert for transparent lines)
      Line_DepthOnlyPass.hlsl    (S67, capability-only — inert for transparent lines)
      Line_DepthNormalsPass.hlsl (S67, capability-only — inert for transparent lines)
```

- `Map/<Layer>/` holds one geometry kind's `.shader`, its `<Layer>_LitInput.hlsl`, its pass bodies,
  and (Fill only) `Fill_VertexModify.hlsl`. Each layer is fully self-contained — no cross-folder
  `../../Common/…` includes — **except the sanctioned `../PixelsToWorld.hlsl`** (S23 I2a; see "Shared
  px→world include" below).
- `Common/` holds only `LitInput.Template.hlsl`, a Map-flavoured skeleton that **no `.shader` ever
  includes** (the `.Template.hlsl` infix signals "reference only"). New layers copy it and add their
  own paint props at the `// ── <Layer> paint props go here ──` markers.
- A helper **genuinely shared by two or more layers** lives at `Shaders/Map/` (their sibling-parent),
  not in `Common/` — the pattern `SymbolWorldPitchAlign.hlsl` set and `PixelsToWorld.hlsl` (S23 I2a)
  follows. `Common/` itself grows only for a reference template, never speculatively; this entire stage
  was the cost of having blurred that once.

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

## Shared px→world include

`Fill_VertexModify.hlsl` and `Line_VertexExtrude.hlsl` (and, from S23 I2b, `FillExtrusion_VertexModify.hlsl`)
each `#include "../PixelsToWorld.hlsl"` — a shared `MapPixelsToWorld(centerWS, dirWS)` measurement at
`Shaders/Map/PixelsToWorld.hlsl`, the sibling-parent of `Fill/`, `Line/`, and `FillExtrusion/`. Before S23
I2a each carrier held a sentinel-pinned, character-identical copy of the same block, kept honest only by a
test comparing the copies byte for byte — real duplication, verified rather than trusted, and it had
already drifted once (see `docs/line-translate-parity-design.md`). One copy retires that mechanism; the
placement mirrors the existing `SymbolWorldPitchAlign.hlsl` precedent (`Shaders/Map/Symbol/`, included by
both `Symbol/Text/` and `Symbol/Icon/`).

## Extract to `Common/` only when truly shared

Only extract a pass or helper to `Common/` when a **second layer genuinely needs it**. Never
speculatively. The cost of extracting speculatively (a shared framework used by one layer, confusing
naming, a `Common/` that is not common) was the motivation for this whole stage. A genuinely-shared map
*helper* (not a Unity-mirrored pass/input) lives at `Shaders/Map/` instead — `Common/` stays
template-only (see "Shared px→world include" above).

Intra-folder includes are bare (`#include "Fill_LitInput.hlsl"`). There are no cross-folder
`../../Common/…` includes from any layer — the one sanctioned cross-folder include anywhere in `Map/` is
`../PixelsToWorld.hlsl`.

## DOTS / BRG instancing

Lit shaders carry `#pragma multi_compile_instancing` and `UNITY_DOTS_INSTANCED_PROP` property sets so
they render under the BatchRendererGroup backend (S49). The CBUFFER, the `UNITY_DOTS_INSTANCING`
block, and any BRG SoA packing must stay byte-aligned.
