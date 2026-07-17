# UnityUnlit — vendored URP Unlit shader (reference template)

A **verbatim copy** of Unity's Universal Render Pipeline *Unlit* shader set, vendored into the repo to
serve as a **reference template** for the planned GPU-cheap Unlit map-shader variant (see
`docs/meshing-design.md` §3 "Unlit variant"). It is the Unlit counterpart to the `UnityLit/` template:
keeping these files in-tree lets a future `Map/*` Unlit shader be diffed directly against its upstream
counterpart to confirm the only differences are the intended ones.

## Provenance

| | |
|---|---|
| Source package | `com.unity.render-pipelines.universal` |
| URP version    | **17.5.0** (package hash `0c18adc4ff89`) |
| Unity editor   | **6000.5.0f1** |
| Copied from    | `Packages/com.unity.render-pipelines.universal@0c18adc4ff89/Shaders/` |
| License        | **Unity Companion License** — see `THIRD-PARTY-NOTICES.txt`. Each file retains its upstream Unity copyright header. |

## Files

`Unlit.shader` plus the includes it pulls in that have a local copy: `UnlitInput.hlsl`,
`UnlitForwardPass.hlsl`, `UnlitGBufferPass.hlsl`, `UnlitDepthNormalsPass.hlsl`, `UnlitMetaPass.hlsl`.

The shader's `DepthOnly` pass includes stock `DepthOnlyPass.hlsl`, which is **not** Unlit-specific and is
**not** copied here — that include is left on its package path (the same file is vendored under
`UnityLit/DepthOnlyPass.hlsl` if a local baseline is ever needed). Likewise the `Meta`/`MotionVectors`/
`XRMotionVectors` passes and every `#include_with_pragmas "…/ShaderLibrary/…"` keep their package paths.

## Modifications from upstream

These are the **only** changes from the package originals — everything else is byte-identical:

1. **Shader name** — `Universal Render Pipeline/Unlit` → `Template/UnityUnlit`, so this reference copy does
   **not** collide with the package's own `Unlit` shader (a duplicate shader name would let Unity bind the
   wrong one to materials).
2. **Include paths** — the shader's includes that resolve to a **local sibling copy** were rewritten from
   the absolute `Packages/com.unity.render-pipelines.universal/Shaders/…` form to **local relative**
   (`#include "UnlitInput.hlsl"`, …) so the template is self-contained within this folder. Includes with no
   local copy (`DepthOnlyPass.hlsl`, `Utils/SurfaceType.hlsl`, all `ShaderLibrary/…`) keep their package
   paths.

The `Template/UnityUnlit` shader is reference-only; it is not used by any material at runtime.

## How a future Unlit map shader relates to this template

An Unlit `Map/*` shader should diff against this template down to the same small delta set the Lit map
shaders keep over `UnityLit/` (see `../UnityLit/README.md`):

1. **Properties header** — our own map props (`_Color`, `_Opacity`, `_Width`, `_Blur`, gap/dash/offset, …)
   plus the render-state params that make the shader tweakable (`_SrcBlend`/`_DstBlend`/`_SrcBlendAlpha`/
   `_DstBlendAlpha`/`_ZWrite`/`_ZTest`/`_Cull`/`_BlendOp`).
2. **Render-state commands** — parameterized (`Blend [_SrcBlend] [_DstBlend], …`, `ZWrite [_ZWrite]`, …)
   instead of upstream's hardcodes.
3. **The hook includes** — the layer's `<Layer>_UnlitInput` / vertex-hook / `<Layer>_<Pass>` in place of
   stock `UnlitInput` / `UnlitForwardPass` / …, carrying the same vertex transform (extrusion,
   fill-translate, z-lift, floating-origin) as the Lit variant. Only the lighting side of the fragment is
   dropped.

Every pragma, keyword declaration, comment banner, instancing line, and pass ordering should otherwise
match this template exactly.

## Refreshing when URP updates

When the URP package version changes:

1. Re-copy the shader set from the new `Packages/com.unity.render-pipelines.universal@<hash>/Shaders/`.
2. Re-apply the two modifications above (shader name, relative includes).
3. Update the **Provenance** table here (version + hash + Unity editor) and the `THIRD-PARTY-NOTICES.txt`
   entry.
4. Re-diff any `Map/*` Unlit shaders against the refreshed template and reconcile any upstream drift.
