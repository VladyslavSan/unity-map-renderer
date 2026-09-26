// Fill_UnlitForwardPass.hlsl — fill layer unlit forward pass; derived from URP UnlitForwardPass.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/UnlitForwardPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream:
//   • Fill_UnlitInput.hlsl (our mirror) is included by FillUnlit.shader before this file.
//   • Vertex is IDENTICAL to Fill_LitForwardPass's vertex for the geometry that matters: it calls the
//     SAME MapVertexModify(...) hook over the SAME Attributes (POSITION/NORMAL/TANGENT/TEXCOORD0/COLOR),
//     so fill-translate and the mesh silhouette are pixel-identical between the Lit and Unlit twins — only
//     the FRAGMENT drops lighting. This is the load-bearing invariant: the vertex hooks consume
//     NORMAL/TANGENT as GEOMETRY, not lighting, so there is no vertex-layout fork.
//   • Fragment is flat: albedo = _BaseColor × _BaseMap × vColor; alpha = _BaseColor.a × _BaseMap.a ×
//     vColor.a × _Opacity (_BaseMap.a stays 1 for every layer — fill-color never sets a custom
//     _BaseMap — but _BaseColor.a carries a Constant/Zoom fill-color's authored alpha, read the
//     same way the Lit twin reads it, so this is the Lit twin's alpha with the lighting-only
//     surface-data plumbing removed; an alpha that dropped _BaseMap.a/_BaseColor.a would lose that
//     authored alpha). No InitializeStandardLitSurfaceData, no InputData, no
//     UniversalFragmentPBR/SAMPLE_GI — UniversalFragmentUnlit (URP's own no-lighting exit point)
//     composes the final color instead.
//   • Keeps the fill-pattern branch (SampleFillPattern + clip + tint) in step with Fill_LitForwardPass.hlsl.

#ifndef MAP_FORWARD_UNLIT_PASS_INCLUDED
#define MAP_FORWARD_UNLIT_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Unlit.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

// The fill boundary band's coverage ramp, shared with the Lit twin.
#include "../Fill_BandCoverage.hlsl"

struct Attributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;
    float4 tangentOS    : TANGENT;
    float2 texcoord     : TEXCOORD0;
    // [MAP DELTA] boundary band (dirEast, dirNorth, side). The slot is the MESH's attribute index, so it
    // is TEXCOORD3 here too even though this pass declares no TEXCOORD1/2 of its own.
    float3 band         : TEXCOORD3;
    // [MAP DELTA] Per-vertex baked color from data-driven expression (mesh COLOR stream).
    float4 color        : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float2 uv          : TEXCOORD0;
    // [MAP DELTA] Per-vertex baked color (data-driven dimension), passed through untouched.
    half4  vColor      : TEXCOORD1;
    float  fogCoord    : TEXCOORD2;
    // [MAP DELTA] Outward boundary band coordinate; next free slot in this pass.
    float  side        : TEXCOORD3;
    float4 positionCS  : SV_POSITION;
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

Varyings UnlitPassVertex(Attributes input)
{
    Varyings output = (Varyings)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // [MAP DELTA] Apply per-layer vertex modification before position transform — IDENTICAL call to the
    // Lit twin's LitPassVertex. Fill: fill-translate. See Fill_VertexModify.hlsl.
    MapVertexModify(input.positionOS.xyz, input.normalOS, input.tangentOS, input.band, output.side);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);

    output.positionCS = vertexInput.positionCS;
    output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    // View-space z, not a fog factor: the fragment computes fog per pixel, as the Lit twin does, so a
    // tile-sized triangle does not clamp the factor at its vertices.
    output.fogCoord = vertexInput.positionVS.z;

    // [MAP DELTA] Pass per-vertex baked color to the fragment stage.
    output.vColor = input.color;

    return output;
}

void UnlitPassFragment(
    Varyings input
    , out half4 outColor : SV_Target0
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);

    // [MAP DELTA] Flat albedo/alpha — no lighting, no InitializeStandardLitSurfaceData. Composite order
    // mirrors Fill_LitForwardPass's InitializeStandardLitSurfaceData + its [MAP DELTA] modulation:
    //   texColor  = the URP surface factor (identity {1,1,1,1} for every real fill — no custom _BaseMap).
    //   _BaseColor = a Constant/Zoom fill-color, converted and uploaded once per material.
    //   vColor    = the data-driven bake (Source/Expression fill-color) — white when _BaseColor
    //     carries the color instead, so exactly one of the two is non-white per layer.
    //   _Opacity  = the paint property.
    half3 albedo = texColor.rgb * _BaseColor.rgb * input.vColor.rgb;
    half  alpha  = texColor.a   * _BaseColor.a   * input.vColor.a * _Opacity;

    // [MAP DELTA] fill-pattern — the rule of Fill_LitForwardPass.hlsl: the sprite's rgb times the layer
    // colour (white when fill-color is absent, so the sprite shows untinted), the sprite's alpha times
    // _Opacity.
    bool  patternClipped;
    half4 patternTexel = SampleFillPattern(input.uv, patternClipped);
    clip(patternClipped ? -1.0 : 1.0);
    if (_FillPattern >= 0.5)
    {
        albedo = patternTexel.rgb * _BaseColor.rgb * input.vColor.rgb;
        alpha  = patternTexel.a * _Opacity;
    }

    // [MAP DELTA] Boundary antialiasing, applied AFTER the pattern branch (which REPLACES alpha) and
    // before AlphaDiscard/AlphaModulate, so a premultiplied composite carries the coverage too.
    alpha *= MapFillBandCoverage(input.side);

    alpha = AlphaDiscard(alpha, _Cutoff);
    albedo = AlphaModulate(albedo, alpha);

#ifdef LOD_FADE_CROSSFADE
    LODFadeCrossFade(input.positionCS);
#endif

    InputData inputData;
    InitializeInputData(input, inputData);
    SETUP_DEBUG_TEXTURE_DATA(inputData, UNDO_TRANSFORM_TEX(input.uv, _BaseMap));

    half4 color = UniversalFragmentUnlit(inputData, albedo, alpha);
    // Fog depth counts from the near plane, as InitializeInputDataFog does for the Lit twin.
    color.rgb = MixFog(color.rgb, ComputeFogFactorZ0ToFar(max(-input.fogCoord - _ProjectionParams.y, 0.0)));
    color.a = OutputAlpha(color.a, IsSurfaceTypeTransparent());

    outColor = color;
}

#endif // MAP_FORWARD_UNLIT_PASS_INCLUDED
