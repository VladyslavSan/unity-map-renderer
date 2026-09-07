// FillExtrusion_ShadowCasterPass.hlsl — fill-extrusion layer shadow pass; derived from URP ShadowCasterPass.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream:
//   • FillExtrusion_LitInput.hlsl (our mirror) is included by FillExtrusion.shader before this file.
//   • Attributes carries the D1 extrusion inputs; MapVertexModify(...) is called with all six, before
//     world-space transforms in GetShadowPositionHClip. The vertex modification MUST also apply in this
//     pass so shadows match the extruded roof/wall silhouette.

#ifndef MAP_FILL_EXTRUSION_SHADOW_CASTER_PASS_INCLUDED
#define MAP_FILL_EXTRUSION_SHADOW_CASTER_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

// Shadow Casting Light geometric parameters. These variables are used when applying the shadow
// Normal Bias and are set by UnityEngine.Rendering.Universal.ShadowUtils.SetupShadowCasterConstantBuffer.
// For Directional lights, _LightDirection is used when applying shadow Normal Bias.
// For Spot lights and Point lights, _LightPosition is used to compute the actual light direction.
float3 _LightDirection;
float3 _LightPosition;

struct Attributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float4 tangentOS    : TANGENT;    // [MAP DELTA] for MapVertexModify's tangent-plane frame
    float2 texcoord     : TEXCOORD0;
    // [MAP DELTA S23 I2b] D1 extrusion inputs — see StyledFillExtrusionTileBuilder's ExtrudeAndBake doc.
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
};

float4 GetShadowPositionHClip(Attributes input)
{
    // [MAP DELTA] Apply per-layer vertex modification before world-space transform.
    MapVertexModify(input.positionOS.xyz, input.normalOS, input.tangentOS,
        input.extrudeUpAndT.xyz, input.extrudeUpAndT.w, input.bakedBaseHeight);

    float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
    float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
    positionCS = ApplyShadowClamping(positionCS);
    return positionCS;
}

Varyings ShadowPassVertex(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);

    #if defined(_ALPHATEST_ON)
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    #endif

    output.positionCS = GetShadowPositionHClip(input);
    return output;
}

half4 ShadowPassFragment(Varyings input) : SV_TARGET
{
    UNITY_SETUP_INSTANCE_ID(input);

    #if defined(_ALPHATEST_ON)
        Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
    #endif

    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif

    return 0;
}

#endif // MAP_FILL_EXTRUSION_SHADOW_CASTER_PASS_INCLUDED
