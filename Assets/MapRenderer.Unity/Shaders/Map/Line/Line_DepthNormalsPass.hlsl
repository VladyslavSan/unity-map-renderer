// Line_DepthNormalsPass.hlsl — line layer depth+normals pass; derived from URP LitDepthNormalsPass.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/LitDepthNormalsPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
//           Verbatim reference copy: Assets/MapRenderer.Unity/Shaders/UnityLit/LitDepthNormalsPass.hlsl
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
//
// MINIMAL DELTA vs upstream LitDepthNormalsPass.hlsl:
//   • Attributes → LineAttributes; world-space ribbon extrusion (Line_VertexExtrude) replaces the direct
//     position transform and supplies the derived per-vertex tangent (a real tangent frame).
//   • The line carries its coverage coordinate in an always-present float3 uv (dashU/side/innerFrac);
//     uv.xy doubles as the surface UV. The fragment clips by LineCoverage (ribbon silhouette) in place of
//     the _ALPHATEST_ON path. Everything else — the full _NORMALMAP/_DETAIL/_PARALLAXMAP feature paths and
//     the _GBUFFER_NORMALS_OCT packing — is the stock fragment, keyword-gated (functional via the real tangent).
//
// CAPABILITY-ONLY (S67): inert for transparent lines (Queue ≥ 2501). S69 activates it for depth-write mode.
// Include order (set by Line.shader): Line_LitInput.hlsl → Line_VertexExtrude.hlsl → this file.

#ifndef MAP_LINE_DEPTH_NORMALS_PASS_INCLUDED
#define MAP_LINE_DEPTH_NORMALS_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

#if defined(_PARALLAXMAP)
#define REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR
#endif

#if (defined(_NORMALMAP) || (defined(_PARALLAXMAP) && !defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR))) || defined(_DETAIL)
#define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
#endif

struct LineDepthNormalsVaryings
{
    float4 positionCS  : SV_POSITION;
    float3 uv          : TEXCOORD1;   // LINE DELTA: (dashU, side, innerFrac) — always present (coverage clip); xy = surface UV
    half3 normalWS     : TEXCOORD2;
#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    half4 tangentWS    : TEXCOORD4;    // xyz: tangent, w: sign
#endif
    half3 viewDirWS    : TEXCOORD5;
#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS    : TEXCOORD8;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

LineDepthNormalsVaryings LineDepthNormalsVertex(LineAttributes input)
{
    LineDepthNormalsVaryings output = (LineDepthNormalsVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // LINE DELTA: world-space ribbon extrusion + derived tangent (real tangent frame).
    float side, innerFrac, dashU;
    float4 tangentOS;
    float3 posOS = Line_VertexExtrude(input, side, innerFrac, dashU, tangentOS);
    output.positionCS = TransformObjectToHClip(posOS);
    output.uv = float3(dashU, side, innerFrac);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(posOS);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, tangentOS);
    output.normalWS = half3(normalInput.normalWS);

#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR) || defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    float sign = tangentOS.w * float(GetOddNegativeScale());
    half4 tangentWS = half4(normalInput.tangentWS.xyz, sign);
#endif
#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    output.tangentWS = tangentWS;
#endif

    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);
    output.viewDirWS = viewDirWS;
#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    output.viewDirTS = GetViewDirectionTangentSpace(tangentWS, output.normalWS, viewDirWS);
#endif

    return output;
}

void LineDepthNormalsFragment(
    LineDepthNormalsVaryings input
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    // LINE DELTA: ribbon-silhouette coverage clip (replaces the _ALPHATEST_ON alpha test).
    clip(LineCoverage(input.uv.y, input.uv.z, input.uv.x) - 0.5);

    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif

    float2 baseUV = input.uv.xy;   // LINE DELTA: surface UV from the line uv channel

    #if defined(_GBUFFER_NORMALS_OCT)
        float3 normalWS = normalize(input.normalWS);
        float2 octNormalWS = PackNormalOctQuadEncode(normalWS);           // [-1, +1]
        float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);   // [ 0,  1]
        half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS);      // [ 0,  1]
        outNormalWS = half4(packedNormalWS, 0.0);
    #else
        #if defined(_PARALLAXMAP)
            #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                half3 viewDirTS = input.viewDirTS;
            #else
                half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, input.viewDirWS);
            #endif
            ApplyPerPixelDisplacement(viewDirTS, baseUV);
        #endif

        #if defined(_NORMALMAP) || defined(_DETAIL)
            float sgn = input.tangentWS.w;      // should be either +1 or -1
            float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
            float3 normalTS = SampleNormal(baseUV, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);

            #if defined(_DETAIL)
                half detailMask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, baseUV).a;
                float2 detailUv = baseUV * _DetailAlbedoMap_ST.xy + _DetailAlbedoMap_ST.zw;
                normalTS = ApplyDetailNormal(detailUv, normalTS, detailMask);
            #endif

            float3 normalWS = TransformTangentToWorld(normalTS, half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz));
        #else
            float3 normalWS = input.normalWS;
        #endif

        outNormalWS = half4(NormalizeNormalPerPixel(normalWS), 0.0);
    #endif

    #ifdef _WRITE_RENDERING_LAYERS
        outRenderingLayers = EncodeMeshRenderingLayer();
    #endif
}

#endif // MAP_LINE_DEPTH_NORMALS_PASS_INCLUDED
