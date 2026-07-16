// Symbol_Input.hlsl — Map/Symbol/Text layer CBUFFER + SDF atlas sampler.
//
// UNLIKE Fill_LitInput.hlsl / Line_LitInput.hlsl, this is NOT derived from URP's LitInput.hlsl — Symbol
// is the affirmed F2 divergence (unlit SDF text, not a lit surface; see Shaders/Map/Symbol/Text/README.md).
// No UnityPerMaterial DOTS-instancing bridge either: Symbol submits via Graphics.RenderMesh/RenderParams
// (one draw call per frame, SRP-Batcher-compatible CBUFFER), never BatchRendererGroup — there is nothing
// to instance.
//
// Two deliberately-separate property groups (the `_Blur`/line-blur lesson):
//   (A) STYLE-BOUND — genuine `text-halo-*` spec terms; production S105 binds these by name.
//   (B) INTERNAL    — engine plumbing (SDF threshold, AA, per-frame screen size). Names avoid every
//                     `text-*`/`symbol-*` style-spec term so a future style binding can never collide.
// `text-color`/`text-opacity` are NOT material properties at all — Slice 1 (and S105) bake them into the
// per-vertex COLOR stream instead (mirrors how `line-color` already rides vertex color in
// StyledLineTileBuilder), so there is nothing here to collide with those two terms either.

#ifndef MAP_SYMBOL_INPUT_INCLUDED
#define MAP_SYMBOL_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

CBUFFER_START(UnityPerMaterial)
// (B) Internal — engine plumbing, never style-bound:
// _ScreenParamsLogical — (logicalWidth, logicalHeight, 0, 0) in pixels; refreshed every frame by
//   LabelPlacementSystem. Converts a vertex's logical-screen-px POSITION to clip space (the
//   Graphics.RenderMesh screen-space bypass — see the pass body + README).
float4 _ScreenParamsLogical;
// _MainTex_TexelSize — (1/w, 1/h, w, h) of the SDF atlas, auto-populated by Unity. Used by the analytic AA
//   to convert the SDF's texel range into screen pixels (see the fragment). In the CBUFFER for SRP Batcher.
float4 _MainTex_TexelSize;
// _SdfEdge          — the SDF's fill iso level, normalized [0,1]. 0.75 matches S18's on-disk convention
//                      (GlyphSdf/SdfDistanceFieldTests.IsoLevel = 191/255 ≈ 0.75), NOT the generic 0.5.
// _SdfSoftness       — antialiasing width in SCREEN PIXELS (~1 = a crisp 1px edge; higher = softer).
// _SdfPixelRange     — the SDF distance field's range in ATLAS TEXELS: the texel count spanned by one unit
//   of normalized distance value (≈ the fontnik radius, 8). The analytic AA scales the signed distance
//   (distSample - _SdfEdge) by this to recover screen-pixel distance, so the edge is crisp at every zoom
//   and the halo width/blur are real screen pixels. Tune if glyphs read too soft (raise) or aliased (lower).
float _SdfEdge;
float _SdfSoftness;
float _SdfPixelRange;

// (A) Style-bound — genuine MapLibre text-halo-* spec terms (production binds text-halo-color/width/blur
// onto these BY NAME once S105 lands; Slice 1's demo leaves them at their Inspector/default values):
float4 _HaloColor;
float  _HaloWidthPx;
float  _HaloBlurPx;
CBUFFER_END

TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

#endif // MAP_SYMBOL_INPUT_INCLUDED
