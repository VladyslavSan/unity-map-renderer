// SymbolIcon_Input.hlsl — Map/Symbol/Icon layer CBUFFER + sprite-atlas sampler.
//
// Cloned from SymbolText_Input.hlsl (see its header for the full "not derived from URP LitInput.hlsl /
// Graphics.RenderMesh submission" rationale — applies identically here). Delta (b): only the internal
// engine-plumbing props survive — no `text-halo-*` style-bound group (icons have no halo), no SDF props
// (icons sample the sprite atlas directly, no distance field). `_MainTex_TexelSize` is SDF-only in the
// text shader's fragment AA, so it is dropped here too — nothing in SymbolPassFragment needs it.

#ifndef MAP_SYMBOL_ICON_INPUT_INCLUDED
#define MAP_SYMBOL_ICON_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

CBUFFER_START(UnityPerMaterial)
// _ScreenParamsLogical — (logicalWidth, logicalHeight, 0, 0) in pixels; refreshed every frame by
//   SymbolPlacementSystem/WorldSymbolRenderer. Converted a vertex's logical-screen-px POSITION to clip space
//   directly in the retired screen-space shader's Graphics.RenderMesh bypass; the surviving world path's
//   vertex stage still reads it, but only to scale the constant-px glyph-corner OFFSET into clip space —
//   the position itself now comes from the stock object/view/projection transform.
float4 _ScreenParamsLogical;
CBUFFER_END

TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

#endif // MAP_SYMBOL_ICON_INPUT_INCLUDED
