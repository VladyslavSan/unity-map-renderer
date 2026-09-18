// Symbol_Input.hlsl — Map/Symbol/Text layer CBUFFER + SDF atlas sampler.
//
// UNLIKE Fill_LitInput.hlsl / Line_LitInput.hlsl, this is NOT derived from URP's LitInput.hlsl — Symbol
// is the affirmed F2 divergence (unlit SDF text, not a lit surface).
// No UnityPerMaterial DOTS-instancing bridge either: Symbol submits via Graphics.RenderMesh/RenderParams
// (one draw call per frame, SRP-Batcher-compatible CBUFFER), never BatchRendererGroup — there is nothing
// to instance.
//
// Every property here is INTERNAL engine plumbing (SDF threshold, AA, per-frame screen size); the names
// avoid every `text-*`/`symbol-*` style-spec term so a future style binding can never collide. There is no
// style-bound group: every `text-*` paint term rides a vertex stream, so none of them appears here.
// `text-color`/`text-opacity` are NOT material properties at all — Slice 1 (and S105) bake them into the
// per-vertex COLOR stream instead (mirrors how `line-color` already rides vertex color in
// StyledLineTileBuilder), so there is nothing here to collide with those two terms either.

#ifndef MAP_SYMBOL_INPUT_INCLUDED
#define MAP_SYMBOL_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

CBUFFER_START(UnityPerMaterial)
// _ScreenParamsLogical — (logicalWidth, logicalHeight, 0, 0) in pixels; refreshed every frame by
//   SymbolPlacementSystem/WorldSymbolRenderer. Scales the constant-px glyph-corner OFFSET into clip space
//   (see the world pass's vertex stage) — the vertex POSITION itself comes from the stock
//   object/view/projection transform, not a logical-px bypass.
float4 _ScreenParamsLogical;
// _MainTex_TexelSize — (1/w, 1/h, w, h) of the SDF atlas, auto-populated by Unity. Used by the analytic AA
//   to convert the SDF's texel range into screen pixels (see the fragment). In the CBUFFER for SRP Batcher.
float4 _MainTex_TexelSize;
// _SdfEdge          — the SDF's fill iso level, normalized [0,1]. 0.75 matches S18's on-disk convention
//                      (GlyphSdf/SdfDistanceFieldTests.IsoLevel = 191/255 ≈ 0.75), NOT the generic 0.5.
// _SdfAaDevicePx    — the coverage ramp's width BEYOND the outline, in RASTER px. KEEP IT AT 1.0.
//   RASTER, not logical: it divides a distance the fragment derives from fwidth(uv), a derivative on the
//   render target's grid. _ScreenParamsLogical above is the OTHER unit and scales vertex corner offsets
//   only — do not mix them (that pairing has bitten this shader before).
//   1.0 is not a taste setting. A linear ramp one sample-pitch wide is the only width whose summed ink is
//   invariant to sub-pixel phase; narrower and a stroke's ink freezes for some phases and snaps at others,
//   which reads as glyphs morphing under pan. MSAA is off project-wide, so this band is the ONLY
//   antialiasing text has. Measured at 0.4: centroid steps 0.009..0.287 px against a uniform 0.125 ideal.
//   Pinned by SymbolTextResamplingTests — and pinned to the shipped .mat, which is the value that ships.
// _SdfRangeTexels    — how many ATLAS TEXELS the field's full distance range spans: the texel count of one unit
//   of normalized distance value (≈ the fontnik radius, 8). The analytic AA scales the signed distance
//   (distSample - _SdfEdge) by this to recover screen-pixel distance, so the edge is crisp at every zoom
//   and WorldBillboardVertex.SdfWidenPx is in those same real screen pixels. NOT a look knob: it describes
//   how the glyphs were BAKED, so it is only ever changed to match a different glyph source.
float _SdfEdge;
float _SdfAaDevicePx;
float _SdfRangeTexels;
CBUFFER_END

// Stage M: a Texture2DArray — one layer per GlyphAtlas page. Single-page maps (the invariant: any map
// that fits in one page) upload exactly one layer, and every vertex's Page is 0, so the array sample at
// layer 0 is pixel-identical to the pre-Stage-M plain Texture2D sample.
TEXTURE2D_ARRAY(_MainTex);
SAMPLER(sampler_MainTex);

#endif // MAP_SYMBOL_INPUT_INCLUDED
