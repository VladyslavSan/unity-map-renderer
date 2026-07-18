# UnityLit — vendored URP Lit shader (reference template)

A **verbatim copy** of Unity's Universal Render Pipeline *Lit* shader set, vendored into the repo to
serve as a **reference template**. Our map shaders (`Map/Line`, `Map/Fill`, …) are intended to be a
*minimal, auditable delta* over stock URP Lit — keeping these files in-tree lets you diff a map shader
directly against its upstream counterpart and confirm the only differences are the intended ones.

## Provenance

| | |
|---|---|
| Source package | `com.unity.render-pipelines.universal` |
| URP version    | **17.5.0** (package hash `0c18adc4ff89`) |
| Unity editor   | **6000.5.0f1** |
| Copied from    | `Packages/com.unity.render-pipelines.universal@0c18adc4ff89/Shaders/` |
| License        | **Unity Companion License** — see `THIRD-PARTY-NOTICES.txt`. Each file retains its upstream Unity copyright header. |

## Files

`UnityLit.shader` plus the includes it pulls in: `LitInput.hlsl`, `LitForwardPass.hlsl`,
`LitGBufferPass.hlsl`, `ShadowCasterPass.hlsl`, `DepthOnlyPass.hlsl`, `DepthNormalsPass.hlsl`,
`LitDepthNormalsPass.hlsl`, `LitMetaPass.hlsl`.

## Modifications from upstream

These are the **only** changes from the package originals — everything else is byte-identical:

1. **Shader name** — `Universal Render Pipeline/Lit` → `Template/UnityLit`, so this reference copy does
   **not** collide with the package's own `Lit` shader (a duplicate shader name would let Unity bind the
   wrong one to materials).
2. **Include paths** — rewritten from the absolute `Packages/com.unity.render-pipelines.universal/Shaders/…`
   form to **local relative** (`#include "LitInput.hlsl"`) so the template is self-contained within this
   folder.

The `Template/UnityLit` shader is reference-only; it is not used by any material at runtime.

## How our map shaders relate to this template

A map shader (e.g. `Map/Line/Line.shader`) should diff against the matching template file down to exactly
three deltas:

1. **Properties header** — our own map props (`_Width`, `_Opacity`, `_Blur`, gap/dash/offset, …) plus the
   render-state params that make the shader tweakable (`_SrcBlend`/`_DstBlend`/`_SrcBlendAlpha`/
   `_DstBlendAlpha`/`_ZWrite`/`_ZTest`/`_Cull`/`_BlendOp`).
2. **Render-state commands** — parameterized (`Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]`,
   `ZWrite [_ZWrite]`, …) instead of upstream's hardcodes.
3. **The hook includes** — `Line_LitInput` / `Line_VertexExtrude` / `Line_<Pass>` in place of stock
   `LitInput` / `LitForwardPass` / …, carrying the line-specific extrusion + coverage.

Every pragma, keyword declaration, comment banner, instancing line, and pass ordering should otherwise
match this template exactly.

## Refreshing when URP updates

When the URP package version changes:

1. Re-copy the shader set from the new `Packages/com.unity.render-pipelines.universal@<hash>/Shaders/`.
2. Re-apply the two modifications above (shader name, relative includes).
3. Update the **Provenance** table here (version + hash + Unity editor) and the `THIRD-PARTY-NOTICES.txt`
   entry.
4. Re-diff the `Map/*` shaders against the refreshed template and reconcile any upstream drift.
