// Line_UnlitInput.hlsl — line-unlit layer CBUFFER + DOTS bridge; derived from URP UnlitInput.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/UnlitInput.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream: the map-paint additions below are copied VERBATIM from Line_LitInput.hlsl's own
//   CBUFFER tail (this repo's code, not URP's) — see that file for the per-property rationale (the
//   style-bound / internal-render-param split, and the "keep ruler (1) vs ruler (2) distinct" note that
//   governs how Line_VertexExtrude.hlsl reads these).
//
// The unlit twin of Line_LitInput.hlsl (unlit rendering mode epic, stage 3). Declares the SAME map-paint
// property names Line_LitInput.hlsl does — that is what lets LineTweaker / ZoomStyleApplier / the C#
// ShaderProperties.Line registry bind onto this material unchanged (no new PropertyId needed; every name
// here is already a registered alias under ShaderProperties.Line / ShaderProperties.PropertyId). What is
// DROPPED versus Line_LitInput.hlsl is exactly the URP-Lit-only surface set this shader never reads:
// _SpecColor/_EmissionColor/_Smoothness/_Metallic/_BumpScale/_Parallax/_OcclusionStrength/_ClearCoatMask/
// _ClearCoatSmoothness/_Detail*MapScale and their textures — Unlit has no lighting model to feed them.
//
// [MAP DELTA S3] _BaseMap/_BaseMap_ST/_BaseMap_TexelSize are declared (mirrors stock Unlit + the other two
// twins) but never SAMPLED by Line_UnlitForwardPass.hlsl — the line mesh carries no lightable UV stream
// either; Line_VertexExtrude's dashU doubles as a surface u only for a future line-pattern (S17), unused
// today. The property stays declared for structural/GUI parity with the other twins.
//
// _MapFrameMetersPerDevicePixel is declared OUTSIDE the CBUFFER, exactly as in Line_LitInput.hlsl — see
// that file's header for why (a per-frame GLOBAL pushed by MapCamera.SyncToCamera, not a per-material
// value; Line_VertexExtrude.hlsl reads it directly). Line_VertexExtrude.hlsl requires this declaration to
// exist in whichever *_Input.hlsl precedes it — this file is that provider for the unlit twin.
//
// Included by LineUnlit.shader BEFORE Line_VertexExtrude.hlsl / Line_Unlit<Pass>.hlsl / Line_Depth*Pass.hlsl
// — define-before-use, same order Line_LitInput.hlsl is included in Line.shader. SRP Batcher requires the
// UnityPerMaterial shape to be IDENTICAL across all of THIS shader's passes — never add/remove CBUFFER
// members in per-pass code; only modify this file.
//
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.

#ifndef MAP_LINE_UNLIT_INPUT_INCLUDED
#define MAP_LINE_UNLIT_INPUT_INCLUDED

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
// ── Map line properties (mirrors Line_LitInput.hlsl's own block VERBATIM — see that file for the
// per-property rationale; the names are IDENTICAL so the C# registry/tweakers bind unchanged) ──
// (A) Style-bound — MapLibre line-* paint/layout:
float  _Opacity;
float  _Width;
float  _Blur;
float  _GapWidth;
float4 _LineTranslate;
float  _LineTranslateAnchor;
float  _LinePattern;
float4 _DashArray;
float  _DashCount;
float  _LineOffset;
// (B) Internal render params — NOT style properties; the styler never writes these:
float  _WidthIsPixels;
CBUFFER_END

// ── Frame global — NOT UnityPerMaterial, NOT a ShaderLab Property ────────────────────────────
// Required by Line_VertexExtrude.hlsl (the width/dash ruler — see Line_LitInput.hlsl's extended header for
// the full "two rulers" derivation, unchanged here). Pushed once per frame by MapCamera.SyncToCamera via
// Shader.SetGlobalFloat — a whole-process singleton, deliberately outside the CBUFFER so it does not cost a
// DOTS-instancing slot for a value identical on every layer.
float _MapFrameMetersPerDevicePixel;

// ── DOTS-instancing bridge ────────────────────────────────────────────────────
#ifdef UNITY_DOTS_INSTANCING_ENABLED

UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DOTS_INSTANCED_PROP(float , _Cutoff)
    // Line — (A) style-bound:
    UNITY_DOTS_INSTANCED_PROP(float , _Opacity)
    UNITY_DOTS_INSTANCED_PROP(float , _Width)
    UNITY_DOTS_INSTANCED_PROP(float , _Blur)
    UNITY_DOTS_INSTANCED_PROP(float , _GapWidth)
    UNITY_DOTS_INSTANCED_PROP(float4, _LineTranslate)
    UNITY_DOTS_INSTANCED_PROP(float , _LineTranslateAnchor)
    UNITY_DOTS_INSTANCED_PROP(float , _LinePattern)
    UNITY_DOTS_INSTANCED_PROP(float4, _DashArray)
    UNITY_DOTS_INSTANCED_PROP(float , _DashCount)
    UNITY_DOTS_INSTANCED_PROP(float , _LineOffset)
    // Line — (B) internal render params (not style):
    UNITY_DOTS_INSTANCED_PROP(float , _WidthIsPixels)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

static float4 unity_DOTS_Sampled_BaseColor;
static float  unity_DOTS_Sampled_Cutoff;
static float  unity_DOTS_Sampled_Opacity;
static float  unity_DOTS_Sampled_Width;
static float  unity_DOTS_Sampled_Blur;
static float  unity_DOTS_Sampled_GapWidth;
static float4 unity_DOTS_Sampled_LineTranslate;
static float  unity_DOTS_Sampled_LineTranslateAnchor;
static float  unity_DOTS_Sampled_LinePattern;
static float4 unity_DOTS_Sampled_DashArray;
static float  unity_DOTS_Sampled_DashCount;
static float  unity_DOTS_Sampled_LineOffset;
static float  unity_DOTS_Sampled_WidthIsPixels;

void SetupDOTSMapUnlitLineMaterialPropertyCaches()
{
    unity_DOTS_Sampled_BaseColor           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor);
    unity_DOTS_Sampled_Cutoff              = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Cutoff);
    unity_DOTS_Sampled_Opacity             = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Opacity);
    unity_DOTS_Sampled_Width               = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Width);
    unity_DOTS_Sampled_Blur                = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Blur);
    unity_DOTS_Sampled_GapWidth            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _GapWidth);
    unity_DOTS_Sampled_LineTranslate       = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _LineTranslate);
    unity_DOTS_Sampled_LineTranslateAnchor = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _LineTranslateAnchor);
    unity_DOTS_Sampled_LinePattern         = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _LinePattern);
    unity_DOTS_Sampled_DashArray           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _DashArray);
    unity_DOTS_Sampled_DashCount           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DashCount);
    unity_DOTS_Sampled_LineOffset          = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _LineOffset);
    unity_DOTS_Sampled_WidthIsPixels       = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _WidthIsPixels);
}

#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSMapUnlitLineMaterialPropertyCaches()

#define _BaseColor              unity_DOTS_Sampled_BaseColor
#define _Cutoff                 unity_DOTS_Sampled_Cutoff
#define _Opacity                unity_DOTS_Sampled_Opacity
#define _Width                  unity_DOTS_Sampled_Width
#define _Blur                   unity_DOTS_Sampled_Blur
#define _GapWidth               unity_DOTS_Sampled_GapWidth
#define _LineTranslate          unity_DOTS_Sampled_LineTranslate
#define _LineTranslateAnchor    unity_DOTS_Sampled_LineTranslateAnchor
#define _LinePattern            unity_DOTS_Sampled_LinePattern
#define _DashArray              unity_DOTS_Sampled_DashArray
#define _DashCount              unity_DOTS_Sampled_DashCount
#define _LineOffset             unity_DOTS_Sampled_LineOffset
#define _WidthIsPixels          unity_DOTS_Sampled_WidthIsPixels

#endif // UNITY_DOTS_INSTANCING_ENABLED

#endif // MAP_LINE_UNLIT_INPUT_INCLUDED
