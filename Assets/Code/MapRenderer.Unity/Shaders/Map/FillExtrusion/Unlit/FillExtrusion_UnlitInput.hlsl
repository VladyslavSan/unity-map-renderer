// FillExtrusion_UnlitInput.hlsl — fill-extrusion-unlit layer CBUFFER + DOTS bridge; derived from URP
// UnlitInput.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/UnlitInput.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream: the map-paint additions below are copied from FillExtrusion_LitInput.hlsl's own
//   CBUFFER tail (this repo's code, not URP's) — see that file for the per-property rationale.
//
// The unlit twin of FillExtrusion_LitInput.hlsl (unlit rendering mode epic, stage 2). Declares the SAME
// map-paint property names FillExtrusion_LitInput.hlsl does — that is what lets FillExtrusionTweaker /
// ZoomStyleApplier / the C# ShaderProperties registry bind onto this material unchanged (no new PropertyId
// needed; every name here is already a registered alias under ShaderProperties.FillExtrusion /
// ShaderProperties.PropertyId). What is DROPPED versus FillExtrusion_LitInput.hlsl is exactly the
// URP-Lit-only surface set this shader never reads: _SpecColor/_EmissionColor/_Smoothness/_Metallic/
// _BumpScale/_Parallax/_OcclusionStrength/_ClearCoatMask/_ClearCoatSmoothness/_Detail*MapScale and their
// textures — Unlit has no lighting model to feed them.
//
// [MAP DELTA S2] _BaseMap/_BaseMap_ST/_BaseMap_TexelSize are declared (mirrors stock Unlit + the Fill unlit
// twin) but never SAMPLED by FillExtrusion_UnlitForwardPass.hlsl: StyledFillExtrusionTileBuilder bakes no
// TEXCOORD0 UV stream at all — its VertexDescriptors write only Position/Normal/Tangent/Color/TexCoord3/
// TexCoord4 (StyledFillExtrusionTileBuilder.cs:114-121; TEXCOORD0-2 are "DELIBERATELY not supplied", per
// that file's own comment). A texture read here would therefore sample the identical zero-UV texel for
// every vertex — a degenerate, pointless "texture" that the Lit twin's own InitializeStandardLitSurfaceData
// already carries for free (it samples that same fixed UV too) but that fill-extrusion styling has never
// used. The property stays declared for structural/GUI parity with the other twins (Surface Inputs' Base
// Map picker, and the reused DepthOnly/DepthNormals passes' _ALPHATEST_ON branch, which DOES read
// _BaseMap/_BaseMap_ST when alpha-clipping is toggled on) — see FillExtrusion_DepthNormalsPass.hlsl's
// REQUIRES_UV_INTERPOLATOR branch, reused verbatim from the Lit twin.
//
// Included by FillExtrusionUnlit.shader BEFORE any pass body (FillExtrusion_VertexModify.hlsl, then
// FillExtrusion_Unlit<Pass>.hlsl / FillExtrusion_Depth*Pass.hlsl) — define-before-use, same order
// FillExtrusion_LitInput.hlsl is included in FillExtrusion.shader. SRP Batcher requires the
// UnityPerMaterial shape to be IDENTICAL across all of THIS shader's passes — never add/remove CBUFFER
// members in per-pass code; only modify this file.

#ifndef MAP_FILL_EXTRUSION_UNLIT_INPUT_INCLUDED
#define MAP_FILL_EXTRUSION_UNLIT_INPUT_INCLUDED

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
// ── Map fill-extrusion paint properties (mirrors FillExtrusion_LitInput.hlsl's own block — see that file
// for the per-property rationale; the names are IDENTICAL so the C# registry/tweakers bind unchanged) ──
float  _Opacity;
float  _ExtrusionHeight;
float  _ExtrusionBase;
float4 _FillExtrusionTranslate;
float  _FillExtrusionTranslateAnchor;
CBUFFER_END

// ── DOTS-instancing bridge ────────────────────────────────────────────────────
#ifdef UNITY_DOTS_INSTANCING_ENABLED

UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DOTS_INSTANCED_PROP(float , _Cutoff)
    UNITY_DOTS_INSTANCED_PROP(float , _Opacity)
    UNITY_DOTS_INSTANCED_PROP(float , _ExtrusionHeight)
    UNITY_DOTS_INSTANCED_PROP(float , _ExtrusionBase)
    UNITY_DOTS_INSTANCED_PROP(float4, _FillExtrusionTranslate)
    UNITY_DOTS_INSTANCED_PROP(float , _FillExtrusionTranslateAnchor)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

static float4 unity_DOTS_Sampled_BaseColor;
static float  unity_DOTS_Sampled_Cutoff;
static float  unity_DOTS_Sampled_Opacity;
static float  unity_DOTS_Sampled_ExtrusionHeight;
static float  unity_DOTS_Sampled_ExtrusionBase;
static float4 unity_DOTS_Sampled_FillExtrusionTranslate;
static float  unity_DOTS_Sampled_FillExtrusionTranslateAnchor;

void SetupDOTSMapUnlitFillExtrusionMaterialPropertyCaches()
{
    unity_DOTS_Sampled_BaseColor                    = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor);
    unity_DOTS_Sampled_Cutoff                       = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Cutoff);
    unity_DOTS_Sampled_Opacity                      = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Opacity);
    unity_DOTS_Sampled_ExtrusionHeight              = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _ExtrusionHeight);
    unity_DOTS_Sampled_ExtrusionBase                = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _ExtrusionBase);
    unity_DOTS_Sampled_FillExtrusionTranslate       = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _FillExtrusionTranslate);
    unity_DOTS_Sampled_FillExtrusionTranslateAnchor = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _FillExtrusionTranslateAnchor);
}

#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSMapUnlitFillExtrusionMaterialPropertyCaches()

#define _BaseColor                        unity_DOTS_Sampled_BaseColor
#define _Cutoff                           unity_DOTS_Sampled_Cutoff
#define _Opacity                          unity_DOTS_Sampled_Opacity
#define _ExtrusionHeight                  unity_DOTS_Sampled_ExtrusionHeight
#define _ExtrusionBase                    unity_DOTS_Sampled_ExtrusionBase
#define _FillExtrusionTranslate           unity_DOTS_Sampled_FillExtrusionTranslate
#define _FillExtrusionTranslateAnchor     unity_DOTS_Sampled_FillExtrusionTranslateAnchor

#endif // UNITY_DOTS_INSTANCING_ENABLED

#endif // MAP_FILL_EXTRUSION_UNLIT_INPUT_INCLUDED
