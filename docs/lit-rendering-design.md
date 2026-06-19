# Lit rendering convention — design

**Status:** adopted convention — **S34 shipped**. S32 shipped a *shallow* fill (stripped surface).
S34 establishes the real convention: **structural parity with URP's `Lit.shader` + the complete material
surface.** Implementation: `Assets/MapRenderer.Unity/Shaders/`. Lines-migration: S33 (onto the S34 base).
This is the reference design every map-geometry shader follows. Researched against URP's public docs
(URP 17.5 / Unity 6000.x); version-dependent items flagged. Clean-room note: this concerns Unity's own
URP rendering system — not MapLibre — so URP source/docs are fair reference.

**S34 implementation mandates (enforced by `ShaderStructureTests` + headless `LitFillSnapshotTests`):**
- `MapLitInput.hlsl` in `Shaders/` holds the **complete** `UnityPerMaterial` CBUFFER (full URP Lit shape
  + `_Color`/`_Opacity` map additions) and `InitializeStandardLitSurfaceData`. NEVER define a stripped
  CBUFFER or hand-assemble `SurfaceData` field-by-field.
- After calling `InitializeStandardLitSurfaceData`, the fragment **init-then-modulates**:
  `surfaceData.albedo *= _Color.rgb; surfaceData.alpha *= _Opacity;` — this is the only allowed pattern.
- `Shaders/Fill.shader` is the authoritative shader (thin scaffold, passes mirror URP `Lit.shader`).
  `Materials/Fill.shader` is the deprecated S32 file (renamed `Hidden/MapRenderer/Fill_S32_Deprecated`).
- Mesh must supply **UV0** (tile-space [0,1]) and **TANGENT** (float4(1,0,0,1) for flat XZ geometry) for
  normal/detail map sampling. `MeshBuilder.AddFeature(verts, indices, tileVerts, extent)` does this.
- Every mirrored `*.hlsl` file carries a **UCL attribution header**; `THIRD-PARTY-NOTICES.txt` has the
  UCL entry; `Assets/ThirdParty/UnityCompanionLicense.txt` holds the committed license text.
- `shader_feature_local _NORMALMAP` and `shader_feature_local_fragment _METALLICSPECGLOSSMAP` pragmas
  are present in `Fill.shader` so normal/metallic maps compile into the shader variants.

**Composition correction — S34 (supersedes the S32 note):**
S32 sidestepped the URP `LitInput.hlsl` `UnityPerMaterial` collision by declaring its *own* tiny CBUFFER
(`_Color`, `_Opacity`, + 3 PBR scalars) and **hand-assembling `SurfaceData`** — which silently dropped the
entire URP Lit surface (base/normal/metallic/occlusion/emission/detail maps, alpha clip, workflow modes…).
That was a self-inflicted loss: the "collision" only existed because of the custom CBUFFER.

The **adopted approach (S34)** is to **mirror-copy URP's structure into `Shaders/` and extend it**:
- A thin `.shader` scaffold mirroring URP `Lit.shader`'s pass list; each pass `#include`s mirror-copied
  building blocks (`MapLitInput.hlsl` ← `LitInput.hlsl`; `MapLit*Pass.hlsl` ← URP's pass files), kept
  verbatim-plus-our-delta so **upstream URP updates apply via `git diff`**.
- The **full** `UnityPerMaterial` (URP Lit's complete shape) **+ our map additions**; `SurfaceData` comes
  from `InitializeStandardLitSurfaceData`, **NEVER hand-assembled field-by-field** (the rule that keeps a
  surface from silently going shallow again — and the reviewer greps for it).
- We own **only** the per-pass vertex function (URP exposes no vertex hook), to inject `MapVertexModify`.
  Net result: the shader ≈ stock URP Lit + a vertex hook. If it looks wildly different, it's been stripped.
- Mirror-copied URP files are **our editable derivatives under `Shaders/`** with UCL attribution headers +
  a `THIRD-PARTY-NOTICES.txt` entry (Unity Companion License; not copyleft — see `ARCHITECTURE.md §4`).
- Folder layout: `Shaders/` (shaders + includes), `Materials/` (template `.mat`), `Editor/` (custom
  `ShaderGUI` material inspectors, in a `MapRenderer.Unity.Editor` asmdef).

## Goal

Map geometry renders with Unity's **standard URP Lit (PBR) lighting model**, not a custom unlit shader.
Appearance is driven by **both** MapLibre paint properties (color, width, opacity, …) **and** Unity Lit
material properties (metallic, smoothness, emission, normal maps, …) — both first-class. A basic URP Lit
material + our per-layer vertex logic + our styling as material properties. Style changes are material
property changes — **no mesh rebuild** (e.g. `material.SetFloat("_Width", …)` restyles line width live).

## Decision: hand-written HLSL with a shared base (NOT ShaderGraph)

ShaderGraph is ruled out: it cannot share one HLSL vertex function across N per-layer graphs, gates the
pass list behind toggles, and is impractical for the complex per-layer logic (extrusion, fwidth AA, SDF
symbols). We use hand-HLSL with a shared **`MapLitCore.hlsl`** skeleton `#include`d into every pass of
every layer shader — the single source of truth for the vertex transform and the material CBUFFER.

## The multi-pass requirement (the part a shallow "use Lit" misses)

URP Lit is a **multi-pass** material. The vertex transform (lateral extrusion, height, billboard, …)
MUST be applied **identically in every pass**, or the silhouette the shadow/depth/GBuffer sees diverges
from the lit pixels (shadow acne, depth mismatch, SSAO halos). Unity explicitly mandates sharing the
vertex code via an include/macro for vertex-moving shaders.

**Pass set to implement** (mandatory minimum for a lit, shadowing, depth-participating map):
- `UniversalForward` (ForwardLit) — the visible lit pixels. **Mandatory.**
- `ShadowCaster` — casts shadows. **Mandatory** if the feature casts shadows.
- `DepthOnly` — camera depth prepass / `_CameraDepthTexture`. Needed when depth texture is on.
- `DepthNormals` — depth+normals prepass. Needed for SSAO.
- `UniversalGBuffer` — **only for deferred-opaque layers** (fills). Omit for forward-only layers (lines).
- Meta / Universal2D / MotionVectors / XR — opt-in only (lightmapping / 2D / TAA / XR).

Every pass `#include`s `MapLitCore.hlsl` and routes `positionOS` (and the extrusion vector) through the
shared `MapVertexModify(...)` before `TransformObjectToHClip`. Reuse Unity's stock pass bodies
(`LitForwardPass.hlsl`, `LitGBufferPass.hlsl`, `ShadowCasterPass.hlsl`, `DepthOnlyPass.hlsl`,
`LitDepthNormalsPass.hlsl`) and substitute only the vertex math — version drift is then mostly keyword
lists + Attributes/Varyings fields.

## Deferred vs. anti-aliasing — the decision

- **Fills (opaque):** Deferred-eligible — author the GBuffer pass; deferred lighting + SSAO + MSAA/auto
  alpha-to-coverage edge AA.
- **Lines (transparent):** forward-transparent, lit via `UniversalForward`, keep the `fwidth` alpha-feather
  AA. URP renders transparents in forward **regardless** of the deferred setting, and both sample the same
  lighting/SSAO — so mixing forward-transparent lines with deferred-opaque fills is the *supported* design.
- **Coplanar z-fighting** between fills and lines on the flat XZ plane: solve with render-queue / polygon
  offset / a tiny Y lift — NOT by changing the AA model.
- (MSAA-in-deferred and auto alpha-to-coverage are URP-version-sensitive; pin to the project's URP version.)

## Styling as material properties

- All style props (`_Width`, `_Color`, `_Opacity`, metallic/smoothness/emission, …) live in **one
  `CBUFFER` named `UnityPerMaterial`** in the shared header, identical in every pass (SRP Batcher requires
  this exact shape). Readable in vertex AND fragment of every pass — so `_Width` read in the vertex stage
  of ShadowCaster/DepthOnly is fine, *as long as the shared include is present in that pass*.
- **Per-layer variation: use a separate Material instance per layer** (`material.SetFloat(...)` restyles
  live). **NEVER `MaterialPropertyBlock`** on batched renderers — it silently disables the SRP Batcher.
- **Adopt the DOTS-instanced-property pattern from the start** (it's the BatchRendererGroup path the
  project targets): declare each style prop in the CBUFFER AND as a `UNITY_DOTS_INSTANCED_PROP`, with the
  redefine macro, so the same names work under SRP Batcher and BRG. Use `MaterialPropertyMetadata` (URP
  built-ins) or `UserPropertyMetadata` for fully custom props.

## Geometry contract

- **World-space extrusion (REQUIRED).** `MapVertexModify` must extrude correctly under **arbitrary
  object→world transforms** (tile transforms, floating-origin rebasing) — extrude in world space, or
  transform the extrusion vector by the object→world matrix. S05's current shader extrudes in object space
  before `TransformObjectToHClip`, which is correct **only under an identity transform** — that must be
  fixed when lines move onto this base (it works for S05's golden shapes but breaks under real tile
  transforms).
- **Normals:** map geometry is flat on XZ → the lighting/surface normal is **+Y**; the extrusion direction
  is a **separate** per-vertex attribute in XZ. Do NOT overload the lighting `NORMAL` channel with the
  extrusion direction. (GBuffer stores the +Y world normal; the offset only moves position — coherent.)
- **Tangents:** required only when sampling a normal/detail map. Minimal attrs:
  - Lit, no normal map: `POSITION` + `NORMAL(+Y)` + extrusion vector (in a free `TEXCOORD`) + UV0 if textured.
  - Lit, with normal map: add `TANGENT` (float4, `.w` = bitangent sign).

## Shared-skeleton structure

- `MapLitCore.hlsl` — the `UnityPerMaterial` CBUFFER (+ DOTS-instancing bridge), `MapVertexModify(inout
  float3 positionWS/OS, float3 extrudeDir, …)`, and `MapEdgeAA(…)` (fwidth coverage). One file, everywhere.
- per-layer `*_Input.hlsl` — that layer's `MapVertexModify` body (line lateral extrude; fill = no-op;
  fill-extrusion = height; circle billboard; SDF symbol quad) and any layer-specific fragment.
- per-layer `.shader` — the pass scaffolding; each `HLSLPROGRAM` includes `MapLitCore.hlsl` + the layer
  input + Unity's stock pass body; adds ONLY its vertex/fragment difference.

## Gotchas to design around
- `MaterialPropertyBlock` silently kills the SRP Batcher + GPU instancing — per-layer materials / DOTS-
  instanced props instead.
- The vertex offset must be in EVERY pass via the shared include, or shadows/depth/SSAO desync.
- Don't overload the lighting `NORMAL` with the extrusion direction.
- Coplanar fill-vs-line z-fighting on the flat plane.
- Deferred renders transparents (lines) in forward anyway; don't expect lines in the GBuffer.
- The 9-pass list and MSAA-in-deferred / alpha-to-coverage behavior are URP-version-dependent; pin them.

## Line specifics (S33)

### Forward-transparent rationale
Lines use `Queue=Transparent` (≥2501) and a single `UniversalForward` pass. URP renders all
transparents in forward regardless of the deferred setting, so lines get the same lighting and
SSAO as deferred-opaque fills. The GBuffer/ShadowCaster/DepthOnly/DepthNormals passes are
intentionally absent — they are dead code for a transparent material (URP excludes Queue≥2501
from the opaque depth/GBuffer prepasses) and would only add compile time.

### CBUFFER fork (MapLineInput.hlsl)
The line needs `_Width`, `_WidthIsPixels`, `_MetersPerPixel`, `_Blur` in `UnityPerMaterial` in
addition to the fill's properties. We cannot `#include MapLitInput.hlsl` and append — the HLSL
compiler rejects a duplicate `CBUFFER_START(UnityPerMaterial)`. The solution is a deliberate
verbatim fork: `MapLineInput.hlsl` ← `LitInput.hlsl`, with the full URP Lit CBUFFER body
copied byte-for-byte, then our line additions appended. `InitializeStandardLitSurfaceData` is
also duplicated verbatim (the upstream pattern, not a helper call). The duplication is
intentional and correct; `git diff` against URP upstream remains meaningful for both files.

### World-space extrusion (decisive S05 fix)
S05's `Materials/Line.shader` extruded in object space before `TransformObjectToHClip`. This is
only correct under an identity object→world transform; it fails under tile-scale or translated
parents. The fix in `MapLineForwardPass.hlsl` (vertex function):
1. Extract the unit extrusion direction from `extrudeN` (strip the miter factor).
2. Transform to world space via `(float3x3)GetObjectToWorldMatrix()`.
3. **Normalize** the world-space direction — this strips any object→world scale, so `_Width`
   is invariant in world meters regardless of parent scale.
4. Apply `unitDir_WS * miter * 0.5 * widthM` as the lateral offset in world space.
5. Round-trip back to object space via `(float3x3)GetWorldToObjectMatrix()` before calling
   `GetVertexPositionInputs`.
The `normalize()` in step 3 is the decisive step: acceptance test #2 (non-identity parent
scale) passes because of it.

### Attribute layout (S33)
The S05 NORMAL slot was unused (no lighting normal); S33 fills it with constant +Y. The
extrusion vector stays on TEXCOORD0. The `LineMeshBuilder` now emits stream 1 = `Normal`
(float3, constant (0,1,0)) so the lit vertex shader receives a proper lighting normal.
Attribute layout:
- `POSITION` (float3): centerline east=+X, height=+Y=0, north=+Z
- `NORMAL`   (float3): constant (0,1,0) — lighting normal, NOT the extrusion direction
- `TEXCOORD0` (float2): extrudeN — extrusion direction (miter factor in |n|)
- `TEXCOORD1` (float2): (side ∈ {+1,−1}, distanceAlong)
- `TEXCOORD2` (float):  widthScale (per-feature, reserved S12)

### Z-fighting mitigation (#7)
Fills and lines are coplanar on the flat XZ plane. The fix is a tiny constant +Y lift of
`0.001` world-meters applied in `MapLineForwardPass.hlsl`'s vertex function (added to the
world-space offset before rounding back to object space). This is in addition to the
`Queue=Transparent` / `ZWrite Off` ordering that the painter's-algorithm layer ordering relies
on. The lift is invisible at any map scale and prevents depth-buffer fights.

## Material inspector — DERIVE, not copy (S35)

**Sharpened copy-vs-derive rule (the whole Lit convention):**
*Copy where there's no hook (shader passes — URP's pass `.hlsl` hardcodes the vertex function with no
injection point, so S34 mirror-copies the pass building blocks under UCL); derive the structure where there
is one (the material GUI — `BaseShaderGUI` is `public abstract` and built to be subclassed via
`DrawSurfaceOptions`/`DrawSurfaceInputs`/`DrawAdvancedOptions`/`FillAdditionalFoldouts`, so S35 derives the
inspector's skeleton from `BaseShaderGUI`). **But the derivation does NOT eliminate the copy:** the bodies of
the three `Draw*` overrides still replicate URP `LitShader`'s protected override expressions verbatim (the
workflow popup, `LitGUI.Inputs` + `DrawEmissionProperties` + `DrawTileOffset`, and the highlights/reflections
null-guarded block) — URP exposes no hook to reach those expressions without copying. **Those replicated
bodies ARE attributed under UCL** (a UCL header on `MapLitShaderGUI.cs` + a `THIRD-PARTY-NOTICES.txt` entry,
`MapLitShaderGUI.cs ← derived from LitShader.cs`). This is the `BaseShaderGUI`-fallback "URP GUI code is
copied" branch of the ticket, not the pure-derive branch.*

**Implementation (`Assets/MapRenderer.Unity/Editor/MapLitShaderGUI.cs`):**
- `MapLitShaderGUI : UnityEditor.BaseShaderGUI` — URP declares its `BaseShaderGUI` in the plain
  `UnityEditor` namespace (its `LitGUI` helper lives in `UnityEditor.Rendering.Universal.ShaderGUI`), so the
  base type's runtime `FullName` is `UnityEditor.BaseShaderGUI`. It is the public URP base, **not** raw
  `UnityEditor.ShaderGUI`. A structural EditMode test (`MapLitShaderGUITests`) asserts the immediate base is
  `typeof(UnityEditor.BaseShaderGUI)` (Type-object comparison, not a brittle string) and is **not**
  `typeof(UnityEditor.ShaderGUI)`.
- URP's own `LitShader` GUI is `internal` → not subclassable from our assembly, so we derive from
  `BaseShaderGUI` (the ticket's "smaller fallback" — here the *required* path) and reproduce `LitShader`'s
  behaviour through the **public** `LitGUI` API: `LitGUI.LitProperties`, `LitGUI.Inputs`,
  `LitGUI.SetMaterialKeywords`, `LitGUI.Styles`, `LitGUI.WorkflowMode`. Reaching that behaviour, however,
  requires reproducing `LitShader`'s three `Draw*` override bodies verbatim — they are short protected
  expressions URP exposes through no hook. So this lands on the **fallback "URP GUI code is copied" branch**,
  not the pure-derive branch: `MapLitShaderGUI.cs` carries a **UCL attribution header** and a
  **`THIRD-PARTY-NOTICES.txt` entry** (`MapLitShaderGUI.cs ← derived from LitShader.cs`). The copied scope is
  only those three override bodies; the Map Paint foldout and the rest of the file are original.
- Keyword / render-state sync stays intact: we do **not** override `OnGUI` (base `OnGUI` → `OnOpenGUI`
  registers the standard foldouts and runs first-time keyword setup), and `ValidateMaterial` forwards to
  `SetMaterialKeywords(material, LitGUI.SetMaterialKeywords)`.
- The **Details foldout is intentionally dropped** — `LitDetailGUI` is `internal` and unreachable from our
  assembly. This is the lone, ticket-sanctioned scope reduction; detail-map properties declared in the
  shaders are not editable in the inspector. All other Lit sections (Surface Options incl. workflow popup,
  Surface Inputs incl. base/normal/metallic-or-specular/occlusion/emission map slots with tiling/offset,
  Advanced) are full-fidelity.
- A single **"Map Paint"** foldout (registered via `FillAdditionalFoldouts`, occupying the slot where
  Details normally sits) draws `_Color` and `_Opacity`, plus the line knobs (`_Width`, `_WidthIsPixels`,
  `_MetersPerPixel`, `_Blur`) gated on `material.HasProperty("_Width")`. `_Width` exists only on
  `MapRenderer/Line`, so one shared `CustomEditor` (`MapRenderer.Unity.Editor.MapLitShaderGUI`) serves both
  `MapRenderer/Fill` and `MapRenderer/Line` and shows the line knobs only on the line material.
- `MapExpandable.MapPaint = 1 << 4` is the foldout's persisted expand-state bit — outside URP's
  `BaseShaderGUI.Expandable` range (SurfaceOptions=1<<0 … Details=1<<3) so it does not collide.

**Assembly references (`MapRenderer.Unity.Editor.asmdef`):** `Unity.RenderPipelines.Universal.Editor`
(for `BaseShaderGUI`/`LitGUI`) and `Unity.RenderPipelines.Core.Editor` (for `MaterialHeaderScopeList`,
which the `FillAdditionalFoldouts` override names in its signature — the defining assembly must be
referenced directly even though URP re-exports it). The headless EditMode run compiles editor assemblies,
so a wrong/renamed reference is caught as a compile failure (acceptance tooth 2).

**Line note:** `MapRenderer/Line` hardcodes its transparent render state in ShaderLab
(`ZWrite Off`, `Cull Off`, `Blend SrcAlpha OneMinusSrcAlpha`, `Queue=Transparent`), so the Surface-Options
toggles the inherited GUI exposes are cosmetically inert on the line — they do not fight the fixed
transparent state. Acceptable for this stage.

**Manual gate (NOT loop-closable):** opening `MapFill.mat` / `MapLine.mat` and confirming the inspector
renders URP's full Lit UI plus the Map Paint section (and that editing affects the material) is a human
step. The headless loop cannot verify inspector UI and must report it as outstanding-manual.

## Key references (Unity URP)
- URP `Lit.shader` (authoritative pass list): https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.universal/Shaders/Lit.shader
- ShaderLab Pass tags: https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@14.0/manual/urp-shaders/urp-shaderlab-pass-tags.html
- Deferred rendering path: https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@14.0/manual/rendering/deferred-rendering-path.html
- Depth-only pass (share vertex code across passes): https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@14.0/manual/writing-shaders-urp-depth-only.html
- SRP Batcher compatibility: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/shaders-in-universalrp-srp-batcher.html
- DOTS-instanced shader properties: https://docs.unity3d.com/6000.3/Documentation/Manual/dots-instancing-shaders-declare.html
- BatchRendererGroup shaders: https://docs.unity3d.com/6000.0/Documentation/Manual/batch-renderer-group-writing-shaders.html
