// Fill_DepthOnlyPass.hlsl — fill layer depth-only pass; derived from URP DepthOnlyPass.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream:
//   • Fill_LitInput.hlsl (our mirror) is included by Fill.shader before this file.
//   • Calls MapVertexModify(input.position.xyz) before TransformObjectToHClip.
//   The vertex modification MUST also apply here to keep _CameraDepthTexture in sync with
//   the lit silhouette (avoids depth mismatch / SSAO halos).

#ifndef MAP_DEPTH_ONLY_PASS_INCLUDED
#define MAP_DEPTH_ONLY_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

struct Attributes
{
    float4 position     : POSITION;
    float3 normalOS     : NORMAL;     // [MAP DELTA] for MapVertexModify's tangent-plane frame
    float4 tangentOS    : TANGENT;    // [MAP DELTA] for MapVertexModify's tangent-plane frame
    float2 texcoord     : TEXCOORD0;
    float3 band         : TEXCOORD3;  // [MAP DELTA] boundary band (dirEast, dirNorth, side)
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    #if defined(_ALPHATEST_ON)
        float2 uv       : TEXCOORD0;
    #endif
    float  side         : TEXCOORD1;  // [MAP DELTA] band coordinate for the clip below; next free slot here
    float4 positionCS   : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings DepthOnlyVertex(Attributes input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    #if defined(_ALPHATEST_ON)
        output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    #endif

    // [MAP DELTA] Apply per-layer vertex modification before clip-space transform.
    MapVertexModify(input.position.xyz, input.normalOS, input.tangentOS, input.band, output.side);

    output.positionCS = TransformObjectToHClip(input.position.xyz);
    return output;
}

half DepthOnlyFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    // [MAP DELTA] The outward boundary band is clipped OUT of every depth-writing pass. A coverage-0 band
    // fragment under this pass's hardcoded ZWrite On would still write depth and cast a shadow, so the
    // depth prepass and the shadow silhouette stay exactly the hard geometry's — not a screen-space skirt
    // half a pixel wide. Clipping on `side` rather than on coverage is what keeps that bit-exact.
    clip(input.side > 0.0 ? -1.0 : 1.0);

    #if defined(_ALPHATEST_ON)
        Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
    #endif

    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif

    return input.positionCS.z;
}
#endif // MAP_DEPTH_ONLY_PASS_INCLUDED
