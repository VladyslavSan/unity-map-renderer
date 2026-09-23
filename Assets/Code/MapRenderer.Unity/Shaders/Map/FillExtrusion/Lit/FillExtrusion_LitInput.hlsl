// FillExtrusion_LitInput.hlsl — fill-extrusion layer CBUFFER + DOTS bridge + surface helpers; derived from
// URP LitInput.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream: full UnityPerMaterial + fill-extrusion paint properties (_Opacity,
//   _ExtrusionHeight, _ExtrusionBase, _FillExtrusionTranslate,
//   _FillExtrusionTranslateAnchor); DOTS bridge extended for those props; InitializeStandardLitSurfaceData
//   preserved verbatim. No fill-pattern texture (the spec has no fill-extrusion-pattern).
//
// This is a FORK of Fill_LitInput.hlsl (same reasoning as Line_LitInput.hlsl):
//      we CANNOT #include Fill_LitInput.hlsl and add to it — that would produce a duplicate UnityPerMaterial
//      CBUFFER, which the HLSL compiler rejects. SRP Batcher requires the CBUFFER to be IDENTICAL in every
//      pass of the fill-extrusion shader, so all its passes must #include THIS file, not Fill_LitInput.hlsl.
//
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.

#ifndef MAP_FILL_EXTRUSION_INPUT_INCLUDED
#define MAP_FILL_EXTRUSION_INPUT_INCLUDED

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
// NOTE: Do not ifdef the properties here as SRP Batcher cannot handle different layouts.
// Layout mirrors URP LitInput.hlsl exactly (same type/order), plus map-specific additions.
// Map additions are appended at the end to keep upstream delta minimal.
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
// ── Map fill-extrusion paint properties ───────────────────────────────────────
// The per-layer paint color is the standard URP _BaseColor (declared above): it multiplies onto
// albedo (and its alpha into the surface alpha), exactly like Fill.
// _Opacity                     — fill-extrusion-opacity: overall opacity [0,1].
// _ExtrusionHeight             — fill-extrusion-height (constant/zoom path; data-driven bakes per-vertex
//                                 instead — StyledFillExtrusionTileBuilder). NOT named _Height (reserved).
// _ExtrusionBase               — fill-extrusion-base (constant/zoom path).
// _FillExtrusionTranslate      — fill-extrusion-translate: xy = pixel offset (world/viewport per
//                                 _FillExtrusionTranslateAnchor). zw unused; packed as float4 for alignment.
// _FillExtrusionTranslateAnchor — 0 = map world-space, 1 = viewport screen-space.
float  _Opacity;
float  _ExtrusionHeight;
float  _ExtrusionBase;
float4 _FillExtrusionTranslate;
float  _FillExtrusionTranslateAnchor;
CBUFFER_END

// ── DOTS-instancing bridge ────────────────────────────────────────────────────
// NOTE: Do not ifdef the properties for dots instancing, but ifdef the actual usage.
// Otherwise you might break CPU-side as property constant-buffer offsets change per variant.
// NOTE: Dots instancing is orthogonal to the constant buffer above.
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
    // Map fill-extrusion additions:
    UNITY_DOTS_INSTANCED_PROP(float , _Opacity)
    UNITY_DOTS_INSTANCED_PROP(float , _ExtrusionHeight)
    UNITY_DOTS_INSTANCED_PROP(float , _ExtrusionBase)
    UNITY_DOTS_INSTANCED_PROP(float4, _FillExtrusionTranslate)
    UNITY_DOTS_INSTANCED_PROP(float , _FillExtrusionTranslateAnchor)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

// Cache values in statics to avoid redundant load code per property use (same pattern as
// URP LitInput.hlsl — ~10% GPU perf improvement on Meta Quest 2).
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
// Map fill-extrusion statics:
static float  unity_DOTS_Sampled_Opacity;
static float  unity_DOTS_Sampled_ExtrusionHeight;
static float  unity_DOTS_Sampled_ExtrusionBase;
static float4 unity_DOTS_Sampled_FillExtrusionTranslate;
static float  unity_DOTS_Sampled_FillExtrusionTranslateAnchor;

void SetupDOTSMapFillExtrusionMaterialPropertyCaches()
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
    unity_DOTS_Sampled_Opacity              = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Opacity);
    unity_DOTS_Sampled_ExtrusionHeight      = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _ExtrusionHeight);
    unity_DOTS_Sampled_ExtrusionBase        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _ExtrusionBase);
    unity_DOTS_Sampled_FillExtrusionTranslate       = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _FillExtrusionTranslate);
    unity_DOTS_Sampled_FillExtrusionTranslateAnchor = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _FillExtrusionTranslateAnchor);
}

// Redirect UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() → our extended function.
// UNITY_SETUP_INSTANCE_ID calls this macro under DOTS instancing, so all pass bodies
// that call UNITY_SETUP_INSTANCE_ID will automatically load our map properties.
#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSMapFillExtrusionMaterialPropertyCaches()

// Redirect property reads to statics (same pattern as URP LitInput.hlsl lines 99-114).
#define _BaseColor              unity_DOTS_Sampled_BaseColor
#define _SpecColor              unity_DOTS_Sampled_SpecColor
#define _EmissionColor          unity_DOTS_Sampled_EmissionColor
#define _Cutoff                 unity_DOTS_Sampled_Cutoff
#define _Smoothness             unity_DOTS_Sampled_Smoothness
#define _Metallic               unity_DOTS_Sampled_Metallic
#define _BumpScale               unity_DOTS_Sampled_BumpScale
#define _Parallax                unity_DOTS_Sampled_Parallax
#define _OcclusionStrength      unity_DOTS_Sampled_OcclusionStrength
#define _ClearCoatMask          unity_DOTS_Sampled_ClearCoatMask
#define _ClearCoatSmoothness    unity_DOTS_Sampled_ClearCoatSmoothness
#define _DetailAlbedoMapScale   unity_DOTS_Sampled_DetailAlbedoMapScale
#define _DetailNormalMapScale   unity_DOTS_Sampled_DetailNormalMapScale
// Map fill-extrusion redirects:
#define _Opacity                          unity_DOTS_Sampled_Opacity
#define _ExtrusionHeight                  unity_DOTS_Sampled_ExtrusionHeight
#define _ExtrusionBase                    unity_DOTS_Sampled_ExtrusionBase
#define _FillExtrusionTranslate           unity_DOTS_Sampled_FillExtrusionTranslate
#define _FillExtrusionTranslateAnchor     unity_DOTS_Sampled_FillExtrusionTranslateAnchor

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

// No fill-pattern sheet here — the Style Spec has no fill-extrusion-pattern (unlike Fill_LitInput.hlsl's
// _PatternMap / SampleFillPattern).

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

// Returns clear coat parameters
// .x/.r == mask
// .y/.g == smoothness
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
#endif  // _CLEARCOAT
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
// Verbatim from LitInput.hlsl; used by FillExtrusion_LitForwardPass + FillExtrusion_LitGBufferPass.
// _BaseColor (rgb→albedo, a→alpha) is applied INSIDE this function, like stock Lit. After calling,
// the fragment applies the remaining map modulation:
//   surfaceData.albedo *= input.vColor.rgb;   // per-feature data-driven tint
//   surfaceData.alpha  *= input.vColor.a * _Opacity;
// NEVER hand-assemble SurfaceData field-by-field — this function is the gate.
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

#endif // MAP_FILL_EXTRUSION_INPUT_INCLUDED
