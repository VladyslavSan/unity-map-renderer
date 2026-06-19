// MapLineForwardPass.hlsl — line-layer forward-lit pass for MapRenderer
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream (S33 line deltas):
//   • Line Attributes: POSITION, NORMAL(+Y), TEXCOORD0(extrudeN), TEXCOORD1(side+dist),
//     TEXCOORD2(widthScale). NOT reusing MapLitForwardPass.hlsl Attributes (TEXCOORD0-2 clash).
//   • World-space extrusion performed directly in this vertex fn (cannot use shared
//     MapVertexModify hook — needs extrudeN/widthScale attributes unavailable there).
//   • Transparent fragment: InitializeStandardLitSurfaceData + UniversalFragmentPBR,
//     then alpha = fwidth-coverage * _Opacity (S05 smoothstep formula, not MapEdgeAA).
//   • No GBuffer/ShadowCaster/DepthOnly/DepthNormals passes — lines are forward-transparent
//     (Queue>=2501) and excluded from the opaque depth/GBuffer prepasses.
//
// Decisive S05 fix: world-space extrusion with normalize() strips parent scale from the
// direction vector so _Width is invariant under arbitrary object→world transforms.
//
// See THIRD-PARTY-NOTICES.txt for Unity Companion License attribution.
// See docs/lit-rendering-design.md §"Line specifics (S33)" for full design rationale.

#ifndef MAP_LINE_FORWARD_PASS_INCLUDED
#define MAP_LINE_FORWARD_PASS_INCLUDED

#include "MapLineInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

// ── Vertex attributes ─────────────────────────────────────────────────────────
// IMPORTANT: TEXCOORD0/1/2 carry line-specific data (NOT uv/lightmapUV/dynamicLightmapUV
// as in MapLitForwardPass.hlsl). This is why we cannot reuse that struct.
struct LineAttributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;     // constant +Y lighting normal (stream 1)
    float2 extrudeN     : TEXCOORD0;  // extrusion normal in tile space (miter factor in |n|)
    float2 sideAndDist  : TEXCOORD1;  // (side ∈ {+1,−1}, distanceAlong)
    float  widthScale   : TEXCOORD2;  // per-feature width scale (S12 reserved, default=1)
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// ── Varyings ──────────────────────────────────────────────────────────────────
struct LineVaryings
{
    float4 positionCS               : SV_POSITION;

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    float3 positionWS               : TEXCOORD1;
#endif

    float3 normalWS                 : TEXCOORD2;

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    half4 fogFactorAndVertexLight   : TEXCOORD5;
#else
    half  fogFactor                 : TEXCOORD5;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord              : TEXCOORD6;
#endif

    DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 8);

#ifdef DYNAMICLIGHTMAP_ON
    float2 dynamicLightmapUV        : TEXCOORD9;
#endif

#ifdef USE_APV_PROBE_OCCLUSION
    float4 probeOcclusion           : TEXCOORD10;
#endif

    // Line-specific: AA coverage value interpolated across the ribbon.
    float  side                     : TEXCOORD0;   // ∈ [−1, +1], used for edge AA

    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

// ── InitializeInputData ───────────────────────────────────────────────────────
// Simplified vs MapLitForwardPass: no tangent, no normal map (line has flat +Y normal),
// no parallax, no detail. Lighting normal is interpolated normalWS from the NORMAL stream.
void LineInitializeInputData(LineVaryings input, out InputData inputData)
{
    inputData = (InputData)0;

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    inputData.positionWS = input.positionWS;
#endif

#if defined(DEBUG_DISPLAY)
    inputData.positionCS = input.positionCS;
#endif

    inputData.normalWS = NormalizeNormalPerPixel(input.normalWS);
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    inputData.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
#else
    inputData.shadowCoord = float4(0, 0, 0, 0);
#endif

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactorAndVertexLight.x);
    inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
#else
    inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
#endif

#if defined(UNITY_PRETRANSFORM_TO_DISPLAY_ORIENTATION)
    float2 preRotatedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    switch (UNITY_DISPLAY_ORIENTATION_PRETRANSFORM)
    {
    default:
        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_0:   inputData.normalizedScreenSpaceUV = preRotatedScreenSpaceUV; break;
        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_90:  inputData.normalizedScreenSpaceUV = float2(1 - preRotatedScreenSpaceUV.y, preRotatedScreenSpaceUV.x); break;
        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_180: inputData.normalizedScreenSpaceUV = float2(1 - preRotatedScreenSpaceUV.x, 1 - preRotatedScreenSpaceUV.y); break;
        case UNITY_DISPLAY_ORIENTATION_PRETRANSFORM_270: inputData.normalizedScreenSpaceUV = float2(preRotatedScreenSpaceUV.y, 1 - preRotatedScreenSpaceUV.x); break;
    }
#else
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
#endif

#ifdef DYNAMICLIGHTMAP_ON
    inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.dynamicLightmapUV, input.vertexSH, inputData.normalWS);
    inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);
#elif !defined(LIGHTMAP_ON) && (defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2))
    inputData.bakedGI = SAMPLE_GI(input.vertexSH,
        GetAbsolutePositionWS(inputData.positionWS),
        inputData.normalWS,
        inputData.viewDirectionWS,
        input.positionCS.xy,
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

LineVaryings LinePassVertex(LineAttributes input)
{
    LineVaryings output = (LineVaryings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // ── World-space extrusion (DECISIVE S05 fix) ──────────────────────────────
    // Problem S05 had: extrusion done in object space before transform → incorrect
    // under non-identity object→world (tile transforms, floating-origin rebasing).
    //
    // Fix: transform the unit extrusion direction to world space and NORMALIZE to strip
    // any scale from the object→world matrix. Then offset in world space by ½ · widthM.
    // Finally, round-trip back to object space so GetVertexPositionInputs works normally.
    //
    // extrudeN: 2D vector in tile space; |extrudeN| = miter factor (>1 for sharp corners).
    // The full miter-scaled offset = unit_dir * miter * ½ * widthM.

    float miter  = length(input.extrudeN);
    // Avoid div-by-zero on degenerate vertices.
    float2 unitN = (miter > 1e-6) ? (input.extrudeN / miter) : float2(0, 0);

    // Resolve width in meters.
    float widthM = (_WidthIsPixels > 0.5)
        ? _Width * _MetersPerPixel
        : _Width;
    widthM *= input.widthScale;

    // Object-space unit direction: 2D tile normal → 3D (x, 0, y).
    float3 unitDir_OS = float3(unitN.x, 0.0, unitN.y);

    // Transform to world space (rotation + scale) then NORMALIZE to remove scale.
    // The normalize() is the decisive step: strips parent scale → _Width is in world meters
    // regardless of the object→world scale, so acceptance #2 (non-identity scale) passes.
    float3x3 O2W = (float3x3)GetObjectToWorldMatrix();
    float3 unitDir_WS = normalize(mul(O2W, unitDir_OS));

    // Lateral world offset = unit world direction × miter factor × ½ × widthM.
    float3 offsetWS = unitDir_WS * (miter * 0.5 * widthM);

    // Tiny +Y lift (0.001 world-meters) to prevent coplanar z-fighting with fills (req #7).
    offsetWS.y += 0.001;

    // Round-trip offset back to object space, add to vertex position.
    float3x3 W2O = (float3x3)GetWorldToObjectMatrix();
    float3 posOS = input.positionOS.xyz + mul(W2O, offsetWS);

    // Standard URP vertex transform from here.
    VertexPositionInputs vertexInput = GetVertexPositionInputs(posOS);

    // Light normal: transform +Y object-space to world space (don't normalize per-vertex —
    // GetVertexNormalInputs does this correctly; the +Y normal is constant so this is safe).
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, float4(1,0,0,1));

    half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);

    half fogFactor = 0;
    #if !defined(_FOG_FRAGMENT)
        fogFactor = ComputeFogFactor(vertexInput.positionCS.z);
    #endif

    output.normalWS = normalInput.normalWS;

    OUTPUT_LIGHTMAP_UV(float2(0,0), unity_LightmapST, output.staticLightmapUV);
#ifdef DYNAMICLIGHTMAP_ON
    output.dynamicLightmapUV = float2(0,0);
#endif
    OUTPUT_SH4(vertexInput.positionWS, output.normalWS.xyz,
               GetWorldSpaceNormalizeViewDir(vertexInput.positionWS),
               output.vertexSH, output.probeOcclusion);

#ifdef _ADDITIONAL_LIGHTS_VERTEX
    output.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
#else
    output.fogFactor = fogFactor;
#endif

#if defined(REQUIRES_WORLD_SPACE_POS_INTERPOLATOR)
    output.positionWS = vertexInput.positionWS;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

    output.positionCS = vertexInput.positionCS;
    output.side       = input.sideAndDist.x;  // ∈ {+1,−1}, interpolated for AA

    return output;
}

// ── Fragment ──────────────────────────────────────────────────────────────────
// Transparent: blends with Blend SrcAlpha OneMinusSrcAlpha.
// Alpha = S05's fwidth smoothstep coverage × _Opacity (makes _Opacity functional, closes S34).
// Surface initialized via InitializeStandardLitSurfaceData (greppable, never hand-assembled).
void LinePassFragment(
    LineVaryings input
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    // ── AA edge coverage (S05 formula — NOT MapEdgeAA; the ≤3px test calibrates to this) ──
    // side ∈ [−1,+1] across ribbon width. |side|=1 at edges, 0 at center.
    float edgeDist = 1.0 - abs(input.side);
    float feather  = fwidth(input.side);
    float coverage = smoothstep(0.0, max(feather * _Blur, 1e-4), edgeDist);

    // ── Surface data ──────────────────────────────────────────────────────────
    // uv=0 → _BaseMap="white" returns (1,1,1,1), so albedo is modulated purely by _MapColor.
    SurfaceData surfaceData;
    InitializeStandardLitSurfaceData(float2(0, 0), surfaceData);

    // [LINE DELTA] Modulate albedo by map color; alpha driven by fwidth coverage × opacity.
    // init-then-modulate: never hand-assembled (S34 design rule + reviewer grep).
    surfaceData.albedo *= _MapColor.rgb;
    // Alpha: coverage (edge AA) × _Opacity. NOT surfaceData.alpha *= coverage (that multiplied
    // the BaseMap alpha, which is always 1 here; multiply directly to make _Opacity functional).
    float alpha = coverage * _Opacity;

    // ── Lighting ──────────────────────────────────────────────────────────────
    InputData inputData;
    LineInitializeInputData(input, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, float2(0, 0));

#if defined(_DBUFFER)
    ApplyDecalToSurfaceData(input.positionCS, surfaceData, inputData);
#endif

    half4 color = UniversalFragmentPBR(inputData, surfaceData);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);

    // Output: lit RGB + transparent alpha (fwidth coverage × _Opacity).
    outColor = half4(color.rgb, alpha);

#ifdef _WRITE_RENDERING_LAYERS
    outRenderingLayers = EncodeMeshRenderingLayer();
#endif
}

#endif // MAP_LINE_FORWARD_PASS_INCLUDED
