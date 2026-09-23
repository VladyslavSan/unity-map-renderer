// FillExtrusion_DepthOnlyPass.hlsl — fill-extrusion layer depth-only pass; derived from URP DepthOnlyPass.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream:
//   • FillExtrusion_LitInput.hlsl (our mirror) is included by FillExtrusion.shader before this file.
//   • Attributes carries the extrusion inputs; MapVertexModify(...) is called with all six, before
//     TransformObjectToHClip. The vertex modification MUST also apply here to keep _CameraDepthTexture in
//     sync with the extruded roof/wall silhouette (avoids depth mismatch / SSAO halos).

#ifndef MAP_FILL_EXTRUSION_DEPTH_ONLY_PASS_INCLUDED
#define MAP_FILL_EXTRUSION_DEPTH_ONLY_PASS_INCLUDED

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
    // [MAP DELTA] Extrusion inputs — see StyledFillExtrusionTileBuilder's ExtrudeAndBake doc.
    float4 extrudeUpAndT     : TEXCOORD3;
    float2 bakedBaseHeight   : TEXCOORD4;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    #if defined(_ALPHATEST_ON)
        float2 uv       : TEXCOORD0;
    #endif
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
    MapVertexModify(input.position.xyz, input.normalOS, input.tangentOS,
        input.extrudeUpAndT.xyz, input.extrudeUpAndT.w, input.bakedBaseHeight);

    output.positionCS = TransformObjectToHClip(input.position.xyz);
    return output;
}

half DepthOnlyFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    #if defined(_ALPHATEST_ON)
        Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
    #endif

    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif

    return input.positionCS.z;
}
#endif // MAP_FILL_EXTRUSION_DEPTH_ONLY_PASS_INCLUDED
