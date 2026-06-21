// MapLineInput.hlsl — map-renderer line-layer CBUFFER fork, derived from URP LitInput.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream: full UnityPerMaterial + line-specific paint properties
//   (_MapColor, _Opacity, _Width, _WidthIsPixels, _MetersPerPixel, _Blur);
//   DOTS bridge extended for line props; InitializeStandardLitSurfaceData preserved verbatim.
//
// S33: This is a DELIBERATE FORK of MapLitInput.hlsl for the line layer.
//      The line needs additional CBUFFER props (_Width, _WidthIsPixels, _MetersPerPixel, _Blur).
//      We CANNOT #include MapLitInput.hlsl and add to it — that would produce a duplicate
//      UnityPerMaterial CBUFFER, which the HLSL compiler rejects.
//      SRP Batcher requires the CBUFFER to be IDENTICAL in every pass of the line shader,
//      so all line passes must #include THIS file, not MapLitInput.hlsl.
//
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.

#ifndef MAP_LINE_INPUT_INCLUDED
#define MAP_LINE_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ParallaxMapping.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/DebugMipmapStreamingMacros.hlsl"
#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/SurfaceType.hlsl"

#if defined(_DETAIL_MULX2) || defined(_DETAIL_SCALED)
#define _DETAIL
#endif

// ── UnityPerMaterial CBUFFER ─────────────────────────────────────────────────
// Layout mirrors URP LitInput.hlsl exactly (same type/order), then appends map-
// specific additions. Line-specific props (_Width etc.) come last.
// NOTE: Must be IDENTICAL across ALL passes of Line.shader (SRP Batcher requires this).
CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST;
float4 _BaseMap_TexelSize;
float4 _DetailAlbedoMap_ST;
half4 _BaseColor;
half4 _SpecColor;
half4 _EmissionColor;
half _Cutoff;
half _Smoothness;
half _Metallic;
half _BumpScale;
half _Parallax;
half _OcclusionStrength;
half _ClearCoatMask;
half _ClearCoatSmoothness;
half _DetailAlbedoMapScale;
half _DetailNormalMapScale;
UNITY_TEXTURE_STREAMING_DEBUG_VARS;
// ── Map paint properties (S33 line additions) ─────────────────────────────────
// _MapColor — line color, multiplied onto albedo. Named _MapColor (not _Color) so URP's
//            legacy _BaseColor alias has no bare _Color to clobber to {1,1,1} on import.
// _Opacity — overall opacity [0,1], multiplied onto fwidth AA coverage.
// _Width   — line width in pixels (WidthIsPixels=1) or meters (WidthIsPixels=0).
// _WidthIsPixels   — 0 = meters, 1 = pixels.
// _MetersPerPixel  — px→m conversion for pixel-mode width.
// _Blur    — AA feather multiplier (~1 = 1px feather, matches S05 smoothstep).
// _GapWidth        — S14: line-gap-width in same units as _Width (pixels). Produces cased/hollow line.
//                    0 = solid line (default); >0 = outer extrude to gap/2+width, discard inner band.
// _LineTranslate   — S14: line-translate as float4(x, y, 0, 0) in pixels. Applied in vertex shader.
// _LineTranslateAnchor — S14: 0 = "map" (world-space), 1 = "viewport" (clip-space approximation).
// _LinePattern     — S14: hook flag. 0 = solid color; 1 = pattern (falls back to solid until S17).
// _DashArray       — S43: on/off lengths (up to 4) in line-width units. Unused slots = 0.
// _DashCount       — S43: number of valid entries in _DashArray (0 = solid identity, no dashing).
// _LineOffset      — S44: perpendicular band-center shift in pixels (same units as _Width).
//                    0 = no shift (default). Positive = left of travel direction.
//                    Converted to meters via same px→m path as _Width.
float4 _MapColor;
float  _Opacity;
float  _Width;
float  _WidthIsPixels;
float  _MetersPerPixel;
float  _Blur;
float  _GapWidth;
float4 _LineTranslate;
float  _LineTranslateAnchor;
float  _LinePattern;
float4 _DashArray;
float  _DashCount;
float  _LineOffset;
CBUFFER_END

// ── DOTS-instancing bridge ────────────────────────────────────────────────────
#ifdef UNITY_DOTS_INSTANCING_ENABLED

UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _SpecColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _EmissionColor)
    UNITY_DOTS_INSTANCED_PROP(float , _Cutoff)
    UNITY_DOTS_INSTANCED_PROP(float , _Smoothness)
    UNITY_DOTS_INSTANCED_PROP(float , _Metallic)
    UNITY_DOTS_INSTANCED_PROP(float , _BumpScale)
    UNITY_DOTS_INSTANCED_PROP(float , _Parallax)
    UNITY_DOTS_INSTANCED_PROP(float , _OcclusionStrength)
    UNITY_DOTS_INSTANCED_PROP(float , _ClearCoatMask)
    UNITY_DOTS_INSTANCED_PROP(float , _ClearCoatSmoothness)
    UNITY_DOTS_INSTANCED_PROP(float , _DetailAlbedoMapScale)
    UNITY_DOTS_INSTANCED_PROP(float , _DetailNormalMapScale)
    // Line paint additions:
    UNITY_DOTS_INSTANCED_PROP(float4, _MapColor)
    UNITY_DOTS_INSTANCED_PROP(float , _Opacity)
    UNITY_DOTS_INSTANCED_PROP(float , _Width)
    UNITY_DOTS_INSTANCED_PROP(float , _WidthIsPixels)
    UNITY_DOTS_INSTANCED_PROP(float , _MetersPerPixel)
    UNITY_DOTS_INSTANCED_PROP(float , _Blur)
    UNITY_DOTS_INSTANCED_PROP(float , _GapWidth)
    UNITY_DOTS_INSTANCED_PROP(float4, _LineTranslate)
    UNITY_DOTS_INSTANCED_PROP(float , _LineTranslateAnchor)
    UNITY_DOTS_INSTANCED_PROP(float , _LinePattern)
    UNITY_DOTS_INSTANCED_PROP(float4, _DashArray)
    UNITY_DOTS_INSTANCED_PROP(float , _DashCount)
    UNITY_DOTS_INSTANCED_PROP(float , _LineOffset)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

static float4 unity_DOTS_Sampled_BaseColor;
static float4 unity_DOTS_Sampled_SpecColor;
static float4 unity_DOTS_Sampled_EmissionColor;
static float  unity_DOTS_Sampled_Cutoff;
static float  unity_DOTS_Sampled_Smoothness;
static float  unity_DOTS_Sampled_Metallic;
static float  unity_DOTS_Sampled_BumpScale;
static float  unity_DOTS_Sampled_Parallax;
static float  unity_DOTS_Sampled_OcclusionStrength;
static float  unity_DOTS_Sampled_ClearCoatMask;
static float  unity_DOTS_Sampled_ClearCoatSmoothness;
static float  unity_DOTS_Sampled_DetailAlbedoMapScale;
static float  unity_DOTS_Sampled_DetailNormalMapScale;
// Line paint statics:
static float4 unity_DOTS_Sampled_MapColor;
static float  unity_DOTS_Sampled_Opacity;
static float  unity_DOTS_Sampled_Width;
static float  unity_DOTS_Sampled_WidthIsPixels;
static float  unity_DOTS_Sampled_MetersPerPixel;
static float  unity_DOTS_Sampled_Blur;
static float  unity_DOTS_Sampled_GapWidth;
static float4 unity_DOTS_Sampled_LineTranslate;
static float  unity_DOTS_Sampled_LineTranslateAnchor;
static float  unity_DOTS_Sampled_LinePattern;
static float4 unity_DOTS_Sampled_DashArray;
static float  unity_DOTS_Sampled_DashCount;
static float  unity_DOTS_Sampled_LineOffset;

void SetupDOTSMapLineMaterialPropertyCaches()
{
    unity_DOTS_Sampled_BaseColor            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor);
    unity_DOTS_Sampled_SpecColor            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _SpecColor);
    unity_DOTS_Sampled_EmissionColor        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _EmissionColor);
    unity_DOTS_Sampled_Cutoff               = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Cutoff);
    unity_DOTS_Sampled_Smoothness           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Smoothness);
    unity_DOTS_Sampled_Metallic             = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Metallic);
    unity_DOTS_Sampled_BumpScale            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _BumpScale);
    unity_DOTS_Sampled_Parallax             = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Parallax);
    unity_DOTS_Sampled_OcclusionStrength    = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _OcclusionStrength);
    unity_DOTS_Sampled_ClearCoatMask        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _ClearCoatMask);
    unity_DOTS_Sampled_ClearCoatSmoothness  = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _ClearCoatSmoothness);
    unity_DOTS_Sampled_DetailAlbedoMapScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DetailAlbedoMapScale);
    unity_DOTS_Sampled_DetailNormalMapScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DetailNormalMapScale);
    unity_DOTS_Sampled_MapColor             = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _MapColor);
    unity_DOTS_Sampled_Opacity              = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Opacity);
    unity_DOTS_Sampled_Width                = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Width);
    unity_DOTS_Sampled_WidthIsPixels        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _WidthIsPixels);
    unity_DOTS_Sampled_MetersPerPixel       = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _MetersPerPixel);
    unity_DOTS_Sampled_Blur                 = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Blur);
    unity_DOTS_Sampled_GapWidth             = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _GapWidth);
    unity_DOTS_Sampled_LineTranslate        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _LineTranslate);
    unity_DOTS_Sampled_LineTranslateAnchor  = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _LineTranslateAnchor);
    unity_DOTS_Sampled_LinePattern          = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _LinePattern);
    unity_DOTS_Sampled_DashArray            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _DashArray);
    unity_DOTS_Sampled_DashCount            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DashCount);
    unity_DOTS_Sampled_LineOffset           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _LineOffset);
}

#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSMapLineMaterialPropertyCaches()

#define _BaseColor              unity_DOTS_Sampled_BaseColor
#define _SpecColor              unity_DOTS_Sampled_SpecColor
#define _EmissionColor          unity_DOTS_Sampled_EmissionColor
#define _Cutoff                 unity_DOTS_Sampled_Cutoff
#define _Smoothness             unity_DOTS_Sampled_Smoothness
#define _Metallic               unity_DOTS_Sampled_Metallic
#define _BumpScale              unity_DOTS_Sampled_BumpScale
#define _Parallax               unity_DOTS_Sampled_Parallax
#define _OcclusionStrength      unity_DOTS_Sampled_OcclusionStrength
#define _ClearCoatMask          unity_DOTS_Sampled_ClearCoatMask
#define _ClearCoatSmoothness    unity_DOTS_Sampled_ClearCoatSmoothness
#define _DetailAlbedoMapScale   unity_DOTS_Sampled_DetailAlbedoMapScale
#define _DetailNormalMapScale   unity_DOTS_Sampled_DetailNormalMapScale
// Line paint redirects:
#define _MapColor               unity_DOTS_Sampled_MapColor
#define _Opacity                unity_DOTS_Sampled_Opacity
#define _Width                  unity_DOTS_Sampled_Width
#define _WidthIsPixels          unity_DOTS_Sampled_WidthIsPixels
#define _MetersPerPixel         unity_DOTS_Sampled_MetersPerPixel
#define _Blur                   unity_DOTS_Sampled_Blur
#define _GapWidth               unity_DOTS_Sampled_GapWidth
#define _LineTranslate          unity_DOTS_Sampled_LineTranslate
#define _LineTranslateAnchor    unity_DOTS_Sampled_LineTranslateAnchor
#define _LinePattern            unity_DOTS_Sampled_LinePattern
#define _DashArray              unity_DOTS_Sampled_DashArray
#define _DashCount              unity_DOTS_Sampled_DashCount
#define _LineOffset             unity_DOTS_Sampled_LineOffset

#endif // UNITY_DOTS_INSTANCING_ENABLED

// ── Texture declarations (verbatim from LitInput.hlsl) ───────────────────────
TEXTURE2D(_ParallaxMap);        SAMPLER(sampler_ParallaxMap);
TEXTURE2D(_OcclusionMap);       SAMPLER(sampler_OcclusionMap);
TEXTURE2D(_DetailMask);         SAMPLER(sampler_DetailMask);
TEXTURE2D(_DetailAlbedoMap);    SAMPLER(sampler_DetailAlbedoMap);
TEXTURE2D(_DetailNormalMap);    SAMPLER(sampler_DetailNormalMap);
TEXTURE2D(_MetallicGlossMap);   SAMPLER(sampler_MetallicGlossMap);
TEXTURE2D(_SpecGlossMap);       SAMPLER(sampler_SpecGlossMap);
TEXTURE2D(_ClearCoatMap);       SAMPLER(sampler_ClearCoatMap);

#ifdef _SPECULAR_SETUP
    #define SAMPLE_METALLICSPECULAR(uv) SAMPLE_TEXTURE2D(_SpecGlossMap, sampler_SpecGlossMap, uv)
#else
    #define SAMPLE_METALLICSPECULAR(uv) SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, uv)
#endif

// ── Helper functions (verbatim from LitInput.hlsl) ───────────────────────────
half4 SampleMetallicSpecGloss(float2 uv, half albedoAlpha)
{
    half4 specGloss;

#ifdef _METALLICSPECGLOSSMAP
    specGloss = half4(SAMPLE_METALLICSPECULAR(uv));
    #ifdef _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
        specGloss.a = albedoAlpha * _Smoothness;
    #else
        specGloss.a *= _Smoothness;
    #endif
#else // _METALLICSPECGLOSSMAP
    #if _SPECULAR_SETUP
        specGloss.rgb = _SpecColor.rgb;
    #else
        specGloss.rgb = _Metallic.rrr;
    #endif

    #ifdef _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
        specGloss.a = albedoAlpha * _Smoothness;
    #else
        specGloss.a = _Smoothness;
    #endif
#endif

    return specGloss;
}

half SampleOcclusion(float2 uv)
{
    #ifdef _OCCLUSIONMAP
        half occ = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, uv).g;
        return LerpWhiteTo(occ, _OcclusionStrength);
    #else
        return half(1.0);
    #endif
}

half2 SampleClearCoat(float2 uv)
{
#if defined(_CLEARCOAT) || defined(_CLEARCOATMAP)
    half2 clearCoatMaskSmoothness = half2(_ClearCoatMask, _ClearCoatSmoothness);
#if defined(_CLEARCOATMAP)
    clearCoatMaskSmoothness *= SAMPLE_TEXTURE2D(_ClearCoatMap, sampler_ClearCoatMap, uv).rg;
#endif
    return clearCoatMaskSmoothness;
#else
    return half2(0.0, 1.0);
#endif
}

void ApplyPerPixelDisplacement(half3 viewDirTS, inout float2 uv)
{
#if defined(_PARALLAXMAP)
    uv += ParallaxMapping(TEXTURE2D_ARGS(_ParallaxMap, sampler_ParallaxMap), viewDirTS, _Parallax, uv);
#endif
}

half3 ScaleDetailAlbedo(half3 detailAlbedo, half scale)
{
    return half(2.0) * detailAlbedo * scale - scale + half(1.0);
}

half3 ApplyDetailAlbedo(float2 detailUv, half3 albedo, half detailMask)
{
#if defined(_DETAIL)
    half3 detailAlbedo = SAMPLE_TEXTURE2D(_DetailAlbedoMap, sampler_DetailAlbedoMap, detailUv).rgb;
#if defined(_DETAIL_SCALED)
    detailAlbedo = ScaleDetailAlbedo(detailAlbedo, _DetailAlbedoMapScale);
#else
    detailAlbedo = half(2.0) * detailAlbedo;
#endif
    return albedo * LerpWhiteTo(detailAlbedo, detailMask);
#else
    return albedo;
#endif
}

half3 ApplyDetailNormal(float2 detailUv, half3 normalTS, half detailMask)
{
#if defined(_DETAIL)
#if BUMP_SCALE_NOT_SUPPORTED
    half3 detailNormalTS = UnpackNormal(SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, detailUv));
#else
    half3 detailNormalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, detailUv), _DetailNormalMapScale);
#endif
    detailNormalTS = normalize(detailNormalTS);
    return lerp(normalTS, BlendNormalRNM(normalTS, detailNormalTS), detailMask);
#else
    return normalTS;
#endif
}

// ── InitializeStandardLitSurfaceData ─────────────────────────────────────────
// Verbatim from LitInput.hlsl; used by MapLineForwardPass.
// After calling, the fragment applies: surfaceData.albedo *= _MapColor.rgb;
// NEVER hand-assemble SurfaceData field-by-field.
inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData outSurfaceData)
{
    half4 albedoAlpha = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
    outSurfaceData.alpha = Alpha(albedoAlpha.a, _BaseColor, _Cutoff);

    half4 specGloss = SampleMetallicSpecGloss(uv, albedoAlpha.a);
    outSurfaceData.albedo = albedoAlpha.rgb * _BaseColor.rgb;
    outSurfaceData.albedo = AlphaModulate(outSurfaceData.albedo, outSurfaceData.alpha);

#if _SPECULAR_SETUP
    outSurfaceData.metallic = half(1.0);
    outSurfaceData.specular = specGloss.rgb;
#else
    outSurfaceData.metallic = specGloss.r;
    outSurfaceData.specular = half3(0.0, 0.0, 0.0);
#endif

    outSurfaceData.smoothness = specGloss.a;
    outSurfaceData.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);
    outSurfaceData.occlusion = SampleOcclusion(uv);
    outSurfaceData.emission = SampleEmission(uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap));

#if defined(_CLEARCOAT) || defined(_CLEARCOATMAP)
    half2 clearCoat = SampleClearCoat(uv);
    outSurfaceData.clearCoatMask       = clearCoat.r;
    outSurfaceData.clearCoatSmoothness = clearCoat.g;
#else
    outSurfaceData.clearCoatMask       = half(0.0);
    outSurfaceData.clearCoatSmoothness = half(0.0);
#endif

#if defined(_DETAIL)
    half detailMask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, uv).a;
    float2 detailUv = uv * _DetailAlbedoMap_ST.xy + _DetailAlbedoMap_ST.zw;
    outSurfaceData.albedo = ApplyDetailAlbedo(detailUv, outSurfaceData.albedo, detailMask);
    outSurfaceData.normalTS = ApplyDetailNormal(detailUv, outSurfaceData.normalTS, detailMask);
#endif
}

#endif // MAP_LINE_INPUT_INCLUDED
