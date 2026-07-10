// Line_LitGBufferPass.hlsl — line layer GBuffer pass; derived from URP LitGBufferPass.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/LitGBufferPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
//           Verbatim reference copy: Assets/Code/MapRenderer.Unity/Shaders/UnityLit/LitGBufferPass.hlsl
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
//
// MINIMAL DELTA vs upstream LitGBufferPass.hlsl:
//   • Attributes → LineAttributes; world-space ribbon extrusion (Line_VertexExtrude) replaces the direct
//     position transform and supplies the derived per-vertex tangent (a real tangent frame, so the
//     _NORMALMAP/_DETAIL/_PARALLAXMAP paths work without a tangent vertex stream).
//   • Varyings carry the line uv as float3 (x=dashU along, y=side, z=innerFrac); uv.xy is the real
//     surface UV. vColor carries the per-feature data-driven color.
//   • Fragment: ribbon coverage clip; albedo *= vColor.rgb, alpha *= vColor.a * _Opacity. The
//     InitializeInputData / GI / BRDF / PackGBuffers tail is the stock GBuffer path.
//
// CAPABILITY-ONLY (S67): inert for transparent lines (Queue ≥ 2501). S69 activates it for deferred.
// Include order (set by Line.shader): Line_LitInput.hlsl → Line_VertexExtrude.hlsl → this file.

#ifndef MAP_LINE_LIT_GBUFFER_PASS_INCLUDED
#define MAP_LINE_LIT_GBUFFER_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferOutput.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

#if defined(_PARALLAXMAP) && (SHADER_TARGET >= 30)
#define REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR
#endif

#if (defined(_NORMALMAP) || (defined(_PARALLAXMAP) && !defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR))) || defined(_DETAIL)
#define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
#endif

struct LineGBufferVaryings
{
    float3 uv                       : TEXCOORD0;   // LINE DELTA: (dashU, side, innerFrac); xy = surface uv

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    float3 positionWS               : TEXCOORD1;
#endif

    half3  normalWS                 : TEXCOORD2;
#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    half4 tangentWS                 : TEXCOORD3;    // xyz: tangent, w: sign
#endif
#ifdef _ADDITIONAL_LIGHTS_VERTEX
    half3  vertexLighting           : TEXCOORD4;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord              : TEXCOORD5;
#endif

#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS                 : TEXCOORD6;
#endif

    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 7);
#ifdef DYNAMICLIGHTMAP_ON
    float2 dynamicLightmapUV        : TEXCOORD8;
#endif

#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion           : TEXCOORD9;
#endif

    half4  vColor                   : TEXCOORD10;   // LINE DELTA: per-feature data-driven color

    float4 positionCS               : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// Stock GBuffer InitializeInputData (the _NORMALMAP/_DETAIL tangent path works via the line's real tangent).
void LineGBufferInitializeInputData(LineGBufferVaryings input, half3 normalTS, out InputData inputData)
{
    inputData = (InputData)0;

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    inputData.positionWS = input.positionWS;
#endif

    inputData.positionCS = input.positionCS;
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
#if defined(_NORMALMAP) || defined(_DETAIL)
    float sgn = input.tangentWS.w;      // should be either +1 or -1
    float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
    inputData.normalWS = TransformTangentToWorld(normalTS, half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz));
#else
    inputData.normalWS = input.normalWS;
#endif

    inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
    inputData.viewDirectionWS = viewDirWS;

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    inputData.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

    inputData.fogCoord = 0.0; // GBuffer pass: no fog

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    inputData.vertexLighting = input.vertexLighting.xyz;
#else
    inputData.vertexLighting = half3(0, 0, 0);
#endif

    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
}

// Verbatim from stock LitGBufferPass (same GI paths apply to the line).
void LineGBufferInitializeBakedGIData(LineGBufferVaryings input, inout InputData inputData)
{
#if defined(_SCREEN_SPACE_IRRADIANCE)
    inputData.bakedGI = SAMPLE_GI(_ScreenSpaceIrradiance, input.positionCS.xy);
#elif defined(DYNAMICLIGHTMAP_ON)
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.dynamicLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    inputData.bakedGI = SAMPLE_GI(input.vertexSH,
        GetAbsolutePositionWS(inputData.positionWS),
        inputData.normalWS,
        inputData.viewDirectionWS,
        inputData.positionCS.xy,
        input.probeOcclusion,
        inputData.shadowMask);
#else
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#endif
}

///////////////////////////////////////////////////////////////////////////////
//                  Vertex and Fragment functions                            //
///////////////////////////////////////////////////////////////////////////////

LineGBufferVaryings LineGBufferPassVertex(LineAttributes input)
{
    LineGBufferVaryings output = (LineGBufferVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // LINE DELTA: world-space ribbon extrusion + derived tangent (real tangent frame).
    float side, innerFrac, dashU;
    float4 tangentOS;
    float3 posOS = Line_VertexExtrude(input, side, innerFrac, dashU, tangentOS);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(posOS);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, tangentOS);
    output.normalWS = normalInput.normalWS;

#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR) || defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    real sign = tangentOS.w * GetOddNegativeScale();
    half4 tangentWS = half4(normalInput.tangentWS.xyz, sign);
#endif
#if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    output.tangentWS = tangentWS;
#endif
#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);
    output.viewDirTS = GetViewDirectionTangentSpace(tangentWS, output.normalWS, viewDirWS);
#endif

    // LINE DELTA: line has no lightmap UV stream — emit zero (lines are not lightmapped).
    OUTPUT_LIGHTMAP_UV(float2(0, 0), unity_LightmapST, output.staticLightmapUV);
#ifdef DYNAMICLIGHTMAP_ON
    output.dynamicLightmapUV = float2(0, 0);
#endif
    OUTPUT_SH4(vertexInput.positionWS, output.normalWS.xyz,
               GetWorldSpaceNormalizeViewDir(vertexInput.positionWS), output.vertexSH, output.probeOcclusion);

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    output.vertexLighting = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
#endif

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    output.positionWS = vertexInput.positionWS;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

    output.positionCS = vertexInput.positionCS;
    output.uv         = float3(dashU, side, innerFrac);   // LINE DELTA: coverage + surface uv
    output.vColor     = input.color;                      // LINE DELTA: per-feature data-driven color

    return output;
}

GBufferFragOutput LineGBufferPassFragment(LineGBufferVaryings input)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    // LINE DELTA: ribbon-silhouette coverage clip (replaces the _ALPHATEST_ON alpha test).
    clip(LineCoverage(input.uv.y, input.uv.z, input.uv.x) - 0.5);

    float2 baseUV = input.uv.xy;   // LINE DELTA: surface UV from the line uv channel

#if defined(_PARALLAXMAP)
#if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS = input.viewDirTS;
#else
    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, viewDirWS);
#endif
    ApplyPerPixelDisplacement(viewDirTS, baseUV);
#endif

    SurfaceData surfaceData;
    InitializeStandardLitSurfaceData(baseUV, surfaceData);   // LINE DELTA: real uv (was float2(0,0))

    surfaceData.albedo *= input.vColor.rgb;              // LINE DELTA: per-feature data-driven tint
    surfaceData.alpha  *= input.vColor.a * _Opacity;     // LINE DELTA: per-feature alpha × opacity

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    LineGBufferInitializeInputData(input, surfaceData.normalTS, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(baseUV, _BaseMap));

#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
#endif

    LineGBufferInitializeBakedGIData(input, inputData);

    BRDFData brdfData;
    InitializeBRDFData(surfaceData.albedo, surfaceData.metallic, surfaceData.specular,
                       surfaceData.smoothness, surfaceData.alpha, brdfData);

    Light mainLight = GetMainLight(inputData.shadowCoord, inputData.positionWS, inputData.shadowMask);
    MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI, inputData.shadowMask);

    half3 color = GlobalIllumination(brdfData, (BRDFData)0, 0,
                                     inputData.bakedGI, surfaceData.occlusion, inputData.positionWS,
                                     inputData.normalWS, inputData.viewDirectionWS,
                                     inputData.normalizedScreenSpaceUV);

    return PackGBuffersBRDFData(brdfData, inputData, surfaceData.smoothness,
                                surfaceData.emission + color, surfaceData.occlusion);
}

#endif // MAP_LINE_LIT_GBUFFER_PASS_INCLUDED
