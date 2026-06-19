#ifndef MAP_LIT_CORE_INCLUDED
#define MAP_LIT_CORE_INCLUDED

// ============================================================================
// MapLitCore.hlsl — shared foundation for every map-geometry lit shader.
//
// Defines:
//   • UnityPerMaterial CBUFFER  — map style props (_Color, _Opacity) + standard
//     PBR knobs (_Metallic, _Smoothness, _EmissionColor) that feed UniversalFragmentPBR.
//     Shape is IDENTICAL in every pass — SRP Batcher requires this.
//   • DOTS-instancing bridge — same names work under SRP Batcher AND
//     BatchRendererGroup (BRG); never via MaterialPropertyBlock.
//   • MapVertexModify(inout float3 positionOS) — per-layer vertex hook.
//     Fill_Input.hlsl provides a no-op body; Line_Input.hlsl will extrude laterally.
//   • MapEdgeAA — fwidth AA helper (carried for S33 lines; no-op consumer here).
//
// Usage:
//   1. #include "MapLitCore.hlsl" in EACH pass of each layer shader.
//   2. #include the per-layer "*_Input.hlsl" AFTER this file.
//      The per-layer file implements MapVertexModify body and any layer-specific
//      fragment helpers. It must NOT add CBUFFER members (would break SRP Batcher
//      across passes).
//
// Do NOT include URP's LitInput.hlsl alongside this file — it defines its own
// UnityPerMaterial (different layout: _BaseColor, _Metallic, texture STs) which
// would cause a CBUFFER redefinition compile error.
//
// Version note: authored for URP 17.5 / Unity 6000.x.
// Clean-room: this is URP integration, not MapLibre. URP docs/source are fair ref.
// ============================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

// ── UnityPerMaterial CBUFFER ──────────────────────────────────────────────────
// SRP Batcher requires the CBUFFER shape to be IDENTICAL in every pass.
// Do NOT add or remove members in per-pass code — modify here, add to ALL passes.
//
// _Color       — map fill/line/symbol color (replaces URP's _BaseColor).
// _Opacity     — overall opacity [0,1]. Visible only in alpha-capable queue/blend.
//               Wired into CBUFFER for SRP Batcher CBUFFER-shape parity and the
//               DOTS bridge; opaque fills render fully lit regardless of its value.
//               Alpha-to-coverage or transparent blending is needed to show it —
//               scope for a future stage (not S32).
// _Metallic    — PBR metallic [0,1].
// _Smoothness  — PBR smoothness [0,1].
// _EmissionColor — HDR emission color.
CBUFFER_START(UnityPerMaterial)
    float4 _Color;
    float  _Opacity;
    float  _Metallic;
    float  _Smoothness;
    float4 _EmissionColor;
CBUFFER_END

// ── DOTS-instancing bridge ────────────────────────────────────────────────────
// Pattern mirrors URP's LitInput.hlsl: declare properties + cache in statics.
// Under BRG the #defines redirect property reads to the per-instance GPU buffer;
// under SRP Batcher the statics are unused and the CBUFFER value is used directly.
// BRG wiring (MaterialPropertyMetadata registration) is out of scope for S32.
#ifdef UNITY_DOTS_INSTANCING_ENABLED

UNITY_DOTS_INSTANCING_START(UserPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _Color)
    UNITY_DOTS_INSTANCED_PROP(float,  _Opacity)
    UNITY_DOTS_INSTANCED_PROP(float,  _Metallic)
    UNITY_DOTS_INSTANCED_PROP(float,  _Smoothness)
    UNITY_DOTS_INSTANCED_PROP(float4, _EmissionColor)
UNITY_DOTS_INSTANCING_END(UserPropertyMetadata)

// Cache in statics (avoids compiler regenerating load code per property use —
// same pattern as URP LitInput.hlsl for ~10% GPU perf improvement on Quest).
static float4 unity_DOTS_Sampled_Color;
static float  unity_DOTS_Sampled_Opacity;
static float  unity_DOTS_Sampled_Metallic;
static float  unity_DOTS_Sampled_Smoothness;
static float4 unity_DOTS_Sampled_EmissionColor;

#define _Color        unity_DOTS_Sampled_Color
#define _Opacity      unity_DOTS_Sampled_Opacity
#define _Metallic     unity_DOTS_Sampled_Metallic
#define _Smoothness   unity_DOTS_Sampled_Smoothness
#define _EmissionColor unity_DOTS_Sampled_EmissionColor

// Must be called at the start of vertex AND fragment shaders under DOTS instancing.
void SetupDOTSMapLitMaterialPropertyCaches()
{
    unity_DOTS_Sampled_Color         = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _Color);
    unity_DOTS_Sampled_Opacity       = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _Opacity);
    unity_DOTS_Sampled_Metallic      = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _Metallic);
    unity_DOTS_Sampled_Smoothness    = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float,  _Smoothness);
    unity_DOTS_Sampled_EmissionColor = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _EmissionColor);
}

#else

// Non-DOTS path: no-op helper so call sites compile identically.
void SetupDOTSMapLitMaterialPropertyCaches() {}

#endif // UNITY_DOTS_INSTANCING_ENABLED


// ── MapVertexModify (declared here; body supplied by per-layer *_Input.hlsl) ─
// Every pass calls MapVertexModify(positionOS) BEFORE GetVertexPositionInputs.
// Fill body = no-op. Line body = lateral extrusion. Must be defined exactly once.
void MapVertexModify(inout float3 positionOS);


// ── MapEdgeAA ────────────────────────────────────────────────────────────────
// fwidth-based edge coverage for sub-pixel AA. Used by lines (S33); here a
// carrier so every layer shader can include this single header.
// edgeSignedDist: signed distance from the feature edge; negative = inside.
// blurUnits: desired feather width in the same units as edgeSignedDist.
// Returns coverage [0,1]; 1 = fully inside, 0 = fully outside.
float MapEdgeAA(float edgeSignedDist, float blurUnits)
{
    float fw = fwidth(edgeSignedDist);
    float halfBlur = max(blurUnits * 0.5, fw);
    return saturate((-edgeSignedDist) / halfBlur + 0.5);
}

#endif // MAP_LIT_CORE_INCLUDED
