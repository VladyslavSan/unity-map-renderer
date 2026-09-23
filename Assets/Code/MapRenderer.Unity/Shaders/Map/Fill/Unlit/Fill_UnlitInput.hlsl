// Fill_UnlitInput.hlsl — fill-unlit layer CBUFFER + DOTS bridge; derived from URP UnlitInput.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/UnlitInput.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream: the map-paint additions below are copied from Fill_LitInput.hlsl's own
//   CBUFFER tail (this repo's code, not URP's) — see that file for the per-property rationale.
//
// The unlit twin of Fill_LitInput.hlsl (plan: docs "unlit rendering mode" epic, stage 1). Declares the
// SAME map-paint property names Fill_LitInput.hlsl does — that is what lets FillTweaker / ZoomStyleApplier
// / the C# ShaderProperties registry bind onto this material unchanged (no new PropertyId needed; every
// name here is already a registered alias). What is DROPPED versus Fill_LitInput.hlsl is exactly the
// URP-Lit-only surface set this shader never reads: _Smoothness/_Metallic/_SpecColor/_BumpScale/_Parallax/
// _OcclusionStrength/_ClearCoatMask/_ClearCoatSmoothness/_Detail*MapScale and their textures — Unlit has no
// lighting model to feed them.
//
// Included by FillUnlit.shader BEFORE any pass body (Fill_VertexModify.hlsl, then Fill_Unlit<Pass>.hlsl /
// Fill_Depth*Pass.hlsl) — define-before-use, same order Fill_LitInput.hlsl is included in Fill.shader.
// SRP Batcher requires the UnityPerMaterial shape to be IDENTICAL across all of THIS shader's passes —
// never add/remove CBUFFER members in per-pass code; only modify this file.

#ifndef MAP_UNLIT_INPUT_INCLUDED
#define MAP_UNLIT_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/DebugMipmapStreamingMacros.hlsl"
#include "Packages/com.unity.render-pipelines.universal/Shaders/Utils/SurfaceType.hlsl"

// ── UnityPerMaterial CBUFFER ─────────────────────────────────────────────────
// NOTE: Do not ifdef the properties here as SRP Batcher cannot handle different layouts.
CBUFFER_START(UnityPerMaterial)
float4 _BaseMap_ST;
float4 _BaseMap_TexelSize;
half4 _BaseColor;
half _Cutoff;
UNITY_TEXTURE_STREAMING_DEBUG_VARS;
// ── Map paint properties (mirrors Fill_LitInput.hlsl's fill-only block — see that file for the
// per-property rationale; the names are IDENTICAL so the C# registry/tweakers bind unchanged) ──
float  _Opacity;
float4 _FillOutlineColor;
float4 _FillTranslate;
float  _FillAntialias;
float  _FillTranslateAnchor;
float  _FillPattern;
float4 _PatternRect;
float4 _PatternScale;
float4 _PatternMap_TexelSize;
CBUFFER_END

// ── DOTS-instancing bridge ────────────────────────────────────────────────────
#ifdef UNITY_DOTS_INSTANCING_ENABLED

UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DOTS_INSTANCED_PROP(float , _Cutoff)
    UNITY_DOTS_INSTANCED_PROP(float , _Opacity)
    UNITY_DOTS_INSTANCED_PROP(float4, _FillOutlineColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _FillTranslate)
    UNITY_DOTS_INSTANCED_PROP(float , _FillAntialias)
    UNITY_DOTS_INSTANCED_PROP(float , _FillTranslateAnchor)
    UNITY_DOTS_INSTANCED_PROP(float , _FillPattern)
    UNITY_DOTS_INSTANCED_PROP(float4, _PatternRect)
    UNITY_DOTS_INSTANCED_PROP(float4, _PatternScale)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

static float4 unity_DOTS_Sampled_BaseColor;
static float  unity_DOTS_Sampled_Cutoff;
static float  unity_DOTS_Sampled_Opacity;
static float4 unity_DOTS_Sampled_FillOutlineColor;
static float4 unity_DOTS_Sampled_FillTranslate;
static float  unity_DOTS_Sampled_FillAntialias;
static float  unity_DOTS_Sampled_FillTranslateAnchor;
static float  unity_DOTS_Sampled_FillPattern;
static float4 unity_DOTS_Sampled_PatternRect;
static float4 unity_DOTS_Sampled_PatternScale;

void SetupDOTSMapUnlitFillMaterialPropertyCaches()
{
    unity_DOTS_Sampled_BaseColor           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor);
    unity_DOTS_Sampled_Cutoff              = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Cutoff);
    unity_DOTS_Sampled_Opacity             = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Opacity);
    unity_DOTS_Sampled_FillOutlineColor    = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _FillOutlineColor);
    unity_DOTS_Sampled_FillTranslate       = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _FillTranslate);
    unity_DOTS_Sampled_FillAntialias       = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _FillAntialias);
    unity_DOTS_Sampled_FillTranslateAnchor = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _FillTranslateAnchor);
    unity_DOTS_Sampled_FillPattern         = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _FillPattern);
    unity_DOTS_Sampled_PatternRect         = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _PatternRect);
    unity_DOTS_Sampled_PatternScale        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _PatternScale);
}

#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSMapUnlitFillMaterialPropertyCaches()

#define _BaseColor              unity_DOTS_Sampled_BaseColor
#define _Cutoff                 unity_DOTS_Sampled_Cutoff
#define _Opacity                unity_DOTS_Sampled_Opacity
#define _FillOutlineColor       unity_DOTS_Sampled_FillOutlineColor
#define _FillTranslate          unity_DOTS_Sampled_FillTranslate
#define _FillAntialias          unity_DOTS_Sampled_FillAntialias
#define _FillTranslateAnchor    unity_DOTS_Sampled_FillTranslateAnchor
#define _FillPattern            unity_DOTS_Sampled_FillPattern
#define _PatternRect            unity_DOTS_Sampled_PatternRect
#define _PatternScale           unity_DOTS_Sampled_PatternScale

#endif // UNITY_DOTS_INSTANCING_ENABLED

// ── [MAP DELTA] fill-pattern sheet — same contract as Fill_LitInput.hlsl's copy ─────────────────
TEXTURE2D(_PatternMap);         SAMPLER(sampler_PatternMap);

// Samples the fill-pattern sprite at `uv` (the world-unit offset from the tile origin), tiling it
// _PatternScale times per world unit.
// Returns the sprite texel; `clipped` is true when this is a pattern layer whose sprite did not resolve.
// Verbatim logic/rationale as Fill_LitInput.hlsl's SampleFillPattern (explicit-gradient sampling to avoid
// mip/bleed artifacts at the frac() wrap seam) — duplicated here because the two CBUFFERs differ and
// cannot share an include; see that file for the full derivation.
half4 SampleFillPattern(float2 uv, out bool clipped)
{
    clipped = false;
    if (_FillPattern < 0.5)
        return half4(1.0, 1.0, 1.0, 1.0); // solid layer — identity multiply, the fill-color path

    if (_PatternRect.z <= 0.0 || _PatternRect.w <= 0.0)
    {
        // Declared but unresolved: no sheet yet, or a name absent from it. The layer is NOT painted.
        clipped = true;
        return half4(0.0, 0.0, 0.0, 0.0);
    }

    float2 sheetSize   = _PatternMap_TexelSize.zw;
    float2 patternUv   = uv * _PatternScale.xy;
    float2 spriteUv    = (_PatternRect.xy + frac(patternUv) * _PatternRect.zw) / sheetSize;
    float2 texelPerUv  = _PatternRect.zw / sheetSize;
    float2 gradX       = ddx(patternUv) * texelPerUv;
    float2 gradY       = ddy(patternUv) * texelPerUv;

    return SAMPLE_TEXTURE2D_GRAD(_PatternMap, sampler_PointClamp, spriteUv, gradX, gradY);
}

#endif // MAP_UNLIT_INPUT_INCLUDED
