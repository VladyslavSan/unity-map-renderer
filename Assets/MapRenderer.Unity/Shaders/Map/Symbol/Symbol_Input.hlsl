// Symbol_Input.hlsl — Map/SymbolText layer CBUFFER + SDF atlas sampler.
//
// UNLIKE Fill_LitInput.hlsl / Line_LitInput.hlsl, this is NOT derived from URP's LitInput.hlsl — Symbol
// is the affirmed F2 divergence (unlit SDF text, not a lit surface; see Shaders/Map/SymbolText/README.md).
// No UnityPerMaterial DOTS-instancing bridge either: Symbol submits via Graphics.RenderMesh/RenderParams
// (one draw call per frame, SRP-Batcher-compatible CBUFFER), never BatchRendererGroup — there is nothing
// to instance.
//
// Two deliberately-separate property groups (the `_Blur`/line-blur lesson — docs/lessons-learned.md):
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
// _SdfEdge          — the SDF's fill iso level, normalized [0,1]. 0.75 matches S18's on-disk convention
//                      (GlyphSdf/SdfDistanceFieldTests.IsoLevel = 191/255 ≈ 0.75), NOT the generic 0.5.
// _SdfSoftness       — extra multiplier on the fwidth-derived antialiasing half-width (1 = plain fwidth AA).
// _SdfDistancePerPixel — approximate px -> normalized-SDF-distance conversion used only by the halo
//   threshold shift below; Slice 1 ships a simple linear approximation (documented in the README) —
//   precise px-calibration is a Slice 3 (paint tuning) follow-up.
float _SdfEdge;
float _SdfSoftness;
float _SdfDistancePerPixel;

// (A) Style-bound — genuine MapLibre text-halo-* spec terms (production binds text-halo-color/width/blur
// onto these BY NAME once S105 lands; Slice 1's demo leaves them at their Inspector/default values):
float4 _HaloColor;
float  _HaloWidthPx;
float  _HaloBlurPx;
CBUFFER_END

TEXTURE2D(_MainTex);
SAMPLER(sampler_MainTex);

#endif // MAP_SYMBOL_INPUT_INCLUDED
