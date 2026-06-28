// Line_ShadowCasterPass.hlsl — line layer shadow-caster pass; derived from URP ShadowCasterPass.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
//           Verbatim reference copy: Assets/MapRenderer.Unity/Shaders/UnityLit/ShadowCasterPass.hlsl
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
//
// MINIMAL DELTA vs upstream ShadowCasterPass.hlsl:
//   • Attributes → LineAttributes (in Line_VertexExtrude.hlsl); world-space ribbon extrusion replaces
//     the direct position transform (so the shadow silhouette matches the forward pass exactly).
//   • The conditional alpha-test `uv` becomes an always-present float3 carrying the line coverage
//     coordinate (uv = dashU/side/innerFrac); the fragment clips by LineCoverage to give the shadow a
//     ribbon silhouette, in place of the _ALPHATEST_ON path.
//
// CAPABILITY-ONLY (S67): inert for transparent lines (Queue ≥ 2501; URP excludes them from
// ShadowCaster). S69 activates it if the line moves to an opaque/forced-shadow mode.
//
// Include order (set by Line.shader): Line_LitInput.hlsl → Line_VertexExtrude.hlsl → this file.

#ifndef MAP_LINE_SHADOW_CASTER_PASS_INCLUDED
#define MAP_LINE_SHADOW_CASTER_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

// Shadow Casting Light geometric parameters (set by ShadowUtils.SetupShadowCasterConstantBuffer).
float3 _LightDirection;
float3 _LightPosition;

struct LineShadowVaryings
{
    float3 uv           : TEXCOORD0;   // LINE DELTA: (dashU, side, innerFrac) — ribbon coverage clip
    float4 positionCS   : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// LINE DELTA: world-space ribbon extrusion replaces the direct position transform; also emits the
// coverage coordinates the fragment needs.
float4 LineShadowGetPositionHClip(LineAttributes input, out float side, out float innerFrac, out float dashU)
{
    float4 tangentOS_unused;
    float3 posOS      = Line_VertexExtrude(input, side, innerFrac, dashU, tangentOS_unused);
    float3 positionWS = TransformObjectToWorld(posOS);
    float3 normalWS   = TransformObjectToWorldNormal(input.normalOS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
    positionCS = ApplyShadowClamping(positionCS);
    return positionCS;
}

LineShadowVaryings LineShadowPassVertex(LineAttributes input)
{
    LineShadowVaryings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    float side, innerFrac, dashU;
    output.positionCS = LineShadowGetPositionHClip(input, side, innerFrac, dashU);
    output.uv = float3(dashU, side, innerFrac);
    return output;
}

half4 LineShadowPassFragment(LineShadowVaryings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    // LINE DELTA: ribbon-silhouette coverage clip (replaces the _ALPHATEST_ON alpha test).
    clip(LineCoverage(input.uv.y, input.uv.z, input.uv.x) - 0.5);

    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif

    return 0;
}

#endif // MAP_LINE_SHADOW_CASTER_PASS_INCLUDED
