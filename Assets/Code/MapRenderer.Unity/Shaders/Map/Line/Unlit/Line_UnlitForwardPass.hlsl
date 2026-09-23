// Line_UnlitForwardPass.hlsl — line layer unlit forward pass; derived from URP UnlitForwardPass.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/UnlitForwardPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream:
//   • Line_UnlitInput.hlsl (our mirror) is included by LineUnlit.shader before this file, and
//     Line_VertexExtrude.hlsl (which DEFINES LineAttributes and LineCoverage — see that file) is included
//     between the two. This file therefore does NOT declare its own Attributes struct — it consumes the
//     SAME LineAttributes every line pass (Lit and Unlit) shares. That is the load-bearing invariant:
//     with one shared struct, the Lit and Unlit vertex layouts cannot fork.
//   • Vertex calls the SAME Line_VertexExtrude(...) the Lit twin's LinePassVertex calls — identical
//     world-space ribbon extrusion, identical silhouette, identical AA/dash coverage inputs. Only the
//     FRAGMENT drops lighting.
//   • [MAP DELTA] Line AA is PRESERVED, not stubbed: alpha is the SAME LineCoverage(...) formula the Lit
//     twin's LinePassFragment uses — LineCoverage lives in the reused Line_VertexExtrude.hlsl and is
//     unchanged, so the straddle AA / gap-hole / line-blur / dash coverage all carry over verbatim. The
//     _HAIRLINE_SOLID_CORE energy-compensation multiply (× hairlineScale) is reproduced exactly too.
//   • No InitializeStandardLitSurfaceData, no InputData surface plumbing, no BaseMap sample (the line mesh
//     carries no lightable UV stream — see Line_UnlitInput.hlsl's header), no
//     UniversalFragmentPBR/SAMPLE_GI — UniversalFragmentUnlit (URP's own no-lighting exit point) composes
//     the final colour instead.

#ifndef MAP_LINE_UNLIT_FORWARD_PASS_INCLUDED
#define MAP_LINE_UNLIT_FORWARD_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Unlit.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

// ── LineAttributes is defined in Line_VertexExtrude.hlsl (included by LineUnlit.shader before this) ──

struct Varyings
{
    // LINE DELTA: float4 uv = (dashU [along, width-units], side [signed cross ∈ -1..1], innerFrac [gap],
    // hairlineScale [_HAIRLINE_SOLID_CORE energy compensation; 1 otherwise]) — mirrors LineVaryings.uv in
    // Line_LitForwardPass.hlsl.
    float4 uv          : TEXCOORD0;
    // LINE DELTA: per-feature data-driven color (white = identity), baked by StyledLineTileBuilder.
    half4  vColor       : TEXCOORD1;
    float  fogCoord     : TEXCOORD2;
    float4 positionCS   : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

void InitializeInputData(Varyings input, out InputData inputData)
{
    inputData = (InputData)0;
    inputData.positionWS = float3(0, 0, 0);
    inputData.normalWS = half3(0, 0, 1);
    inputData.viewDirectionWS = half3(0, 0, 1);
    inputData.shadowCoord = 0;
    inputData.fogCoord = 0;
    inputData.vertexLighting = half3(0, 0, 0);
    inputData.bakedGI = half3(0, 0, 0);
    inputData.normalizedScreenSpaceUV = 0;
    inputData.shadowMask = half4(1, 1, 1, 1);
}

Varyings LineUnlitPassVertex(LineAttributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // [MAP DELTA] World-space ribbon extrusion — IDENTICAL call to the Lit twin's LinePassVertex. Line AA
    // is preserved because these coverage inputs (side/innerFrac/dashU/hairlineScale) are interpolated
    // Varyings, fed to the SAME LineCoverage() in the fragment below.
    float side, innerFrac, dashU;
    float4 tangentOS_unused;
    float hairlineScale;
    float3 posOS = Line_VertexExtrude(input, side, innerFrac, dashU, tangentOS_unused, hairlineScale);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(posOS);

    output.positionCS = vertexInput.positionCS;
    output.fogCoord = ComputeFogFactor(vertexInput.positionCS.z);

    // LINE DELTA: uv carries the line parameterization, NOT TRANSFORM_TEX'd (coverage needs raw dashU).
    output.uv = float4(dashU, side, innerFrac, hairlineScale);
    output.vColor = input.color;

    return output;
}

void LineUnlitPassFragment(
    Varyings input
    , out half4 outColor : SV_Target0
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    // [MAP DELTA] Flat albedo — no lighting, no BaseMap sample (see this pass's header). Alpha is the SAME
    // LineCoverage(...) × _Opacity × vColor.a × _BaseColor.a formula the Lit twin's fragment uses — this IS the
    // line's antialiasing (straddle AA, gap-hole cut, opt-in blur, dash coverage), preserved verbatim
    // because it is computed by the reused Line_VertexExtrude.hlsl, not reimplemented here.
    half3 albedo = _BaseColor.rgb * input.vColor.rgb;

    float coverage = LineCoverage(input.uv.y, input.uv.z, input.uv.x);
#if defined(_HAIRLINE_SOLID_CORE)
    // Energy compensation for the vertex-stage band clamp (see Line_VertexExtrude). 1.0 on every other
    // path, so this multiply is compiled out.
    coverage *= input.uv.w;
#endif
    half alpha = (half)(coverage * _Opacity * input.vColor.a * _BaseColor.a);

    albedo = AlphaModulate(albedo, alpha);

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    InitializeInputData(input, inputData);

    half4 color = UniversalFragmentUnlit(inputData, albedo, alpha);
    color.rgb = MixFog(color.rgb, input.fogCoord);
    color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent());

    outColor = color;
}

#endif // MAP_LINE_UNLIT_FORWARD_PASS_INCLUDED
