// Line_DepthOnlyPass.hlsl — line layer depth-only pass; derived from URP DepthOnlyPass.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
//           Verbatim reference copy: Assets/Code/MapRenderer.Unity/Shaders/Unity/Lit/DepthOnlyPass.hlsl
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
//
// MINIMAL DELTA vs upstream DepthOnlyPass.hlsl:
//   • Attributes → LineAttributes (in Line_VertexExtrude.hlsl); world-space ribbon extrusion replaces
//     the direct position transform (depth silhouette matches the forward pass).
//   • The conditional alpha-test `uv` becomes an always-present float3 carrying the line coverage
//     coordinate (uv = dashU/side/innerFrac); the fragment clips by LineCoverage so the depth
//     silhouette is ribbon-shaped, in place of the _ALPHATEST_ON path.
//
// CAPABILITY-ONLY (S67): inert for transparent lines (Queue ≥ 2501). S69 activates it for depth-write mode.
//
// Include order (set by Line.shader): Line_LitInput.hlsl → Line_VertexExtrude.hlsl → this file.

#ifndef MAP_LINE_DEPTH_ONLY_PASS_INCLUDED
#define MAP_LINE_DEPTH_ONLY_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

struct LineDepthOnlyVaryings
{
    float3 uv           : TEXCOORD0;   // LINE DELTA: (dashU, side, innerFrac) — ribbon coverage clip
    float4 positionCS   : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

LineDepthOnlyVaryings LineDepthOnlyVertex(LineAttributes input)
{
    LineDepthOnlyVaryings output = (LineDepthOnlyVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // LINE DELTA: world-space ribbon extrusion replaces the direct position transform.
    float side, innerFrac, dashU;
    float4 tangentOS_unused;
    float3 posOS = Line_VertexExtrude(input, side, innerFrac, dashU, tangentOS_unused);

    output.positionCS = TransformObjectToHClip(posOS);
    output.uv = float3(dashU, side, innerFrac);
    return output;
}

half LineDepthOnlyFragment(LineDepthOnlyVaryings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    // LINE DELTA: ribbon-silhouette coverage clip (replaces the _ALPHATEST_ON alpha test).
    clip(LineCoverage(input.uv.y, input.uv.z, input.uv.x) - 0.5);

    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif

    return input.positionCS.z;
}

#endif // MAP_LINE_DEPTH_ONLY_PASS_INCLUDED
