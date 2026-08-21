// FillExtrusion_UnlitForwardPass.hlsl — fill-extrusion layer unlit forward pass; derived from URP
// UnlitForwardPass.hlsl
//
// Origin:   Packages/com.unity.render-pipelines.universal/Shaders/UnlitForwardPass.hlsl
//           com.unity.render-pipelines.universal version 17.5.0 (package hash 0c18adc4ff89)
// Copyright © 2020 Unity Technologies ApS
// Licensed under the Unity Companion License — see THIRD-PARTY-NOTICES.txt
// Modified from upstream:
//   • FillExtrusion_UnlitInput.hlsl (our mirror) is included by FillExtrusionUnlit.shader before this file.
//   • Vertex is IDENTICAL to FillExtrusion_LitForwardPass's vertex for the geometry that matters: it calls
//     the SAME MapVertexModify(...) hook over the SAME height-extrusion inputs (extrudeUpAndT/
//     bakedBaseHeight, TEXCOORD3/4) as the Lit twin, so the extruded silhouette (roof + walls) is
//     pixel-identical between the two — only the FRAGMENT drops lighting. This is the load-bearing
//     invariant the epic plan documents (§1): the vertex hooks consume NORMAL/TANGENT as GEOMETRY (the
//     translate tangent frame), not lighting, so there is no vertex-layout fork. Unlit ≠ 2D: the height
//     extrusion (and therefore the 3D silhouette + self-occlusion) is entirely a property of this SHARED
//     vertex hook, untouched by dropping lighting.
//   • [MAP DELTA S2] No TEXCOORD0/_BaseMap sampling in this pass: StyledFillExtrusionTileBuilder bakes no
//     UV stream at all (see FillExtrusion_UnlitInput.hlsl's header) — the Lit twin's own texColor factor
//     is always the same fixed-UV texel for every vertex, so this twin skips the degenerate sample rather
//     than carry it. Fragment is flat: albedo = _BaseColor × vColor; alpha = _BaseColor.a × vColor.a ×
//     _Opacity — the Lit twin's alpha with the texture factor (always 1 here) and the lighting-only surface
//     plumbing removed.
//   • No InitializeStandardLitSurfaceData, no InputData surface plumbing, no
//     UniversalFragmentPBR/SAMPLE_GI — UniversalFragmentUnlit (URP's own no-lighting exit point) composes
//     the final colour instead.

#ifndef MAP_FILL_EXTRUSION_FORWARD_UNLIT_PASS_INCLUDED
#define MAP_FILL_EXTRUSION_FORWARD_UNLIT_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Unlit.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
// [MAP DELTA — unlit fill-extrusion face shading] GetMainLight() lives here. This is the ONE URP-lighting
// header the unlit twin pulls in, and only for the main light's DIRECTION (no GI, no shadows, no probes) —
// enough to keep 3D buildings from reading as flat solid blocks. See UnlitPassFragment.
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

struct Attributes
{
    float4 positionOS       : POSITION;
    float3 normalOS         : NORMAL;
    float4 tangentOS        : TANGENT;
    // [MAP DELTA S23 I2b] D1 extrusion inputs — see StyledFillExtrusionTileBuilder's ExtrudeAndBake doc.
    float4 extrudeUpAndT    : TEXCOORD3; // xyz = baked extrude-up, w = t (0=floor, 1=roof)
    float2 bakedBaseHeight  : TEXCOORD4; // x = baked base offset, y = baked height offset
    // [MAP DELTA S12] Per-vertex baked color from data-driven expression (mesh COLOR stream).
    float4 color             : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    // [MAP DELTA S12] Per-vertex baked color (data-driven dimension), passed through untouched.
    half4  vColor      : TEXCOORD1;
    float  fogCoord    : TEXCOORD2;
    // [MAP DELTA — unlit fill-extrusion face shading] World-space face normal (roof→up, wall→outward),
    // carried to the fragment for a simple directional term. The lit twin reconstructs this through full
    // URP lighting; the unlit twin needs only the raw normal.
    float3 normalWS    : TEXCOORD3;
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
    // Lit twin's LitPassVertex: height extrusion, then fill-extrusion-translate. See
    // FillExtrusion_VertexModify.hlsl.
    MapVertexModify(input.positionOS.xyz, input.normalOS, input.tangentOS,
        input.extrudeUpAndT.xyz, input.extrudeUpAndT.w, input.bakedBaseHeight);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);

    output.positionCS = vertexInput.positionCS;
    output.fogCoord = ComputeFogFactor(vertexInput.positionCS.z);

    // [MAP DELTA S12] Pass per-vertex baked color to the fragment stage.
    output.vColor = input.color;

    // [MAP DELTA — unlit fill-extrusion face shading] Transform the MESH face normal to world space for the
    // fragment's directional term. MapVertexModify takes normalOS BY VALUE, so input.normalOS is still the
    // baked face normal (roof→up, wall→outward) here, not a lighting-perturbed one.
    output.normalWS = TransformObjectToWorldNormal(input.normalOS);

    return output;
}

void UnlitPassFragment(
    Varyings input
    , out half4 outColor : SV_Target0
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    // [MAP DELTA] Flat albedo/alpha — no lighting, no InitializeStandardLitSurfaceData, no BaseMap sample
    // (see this pass's header / FillExtrusion_UnlitInput.hlsl's header — the mesh carries no UV stream for
    // fill-extrusion).
    half3 albedo = _BaseColor.rgb * input.vColor.rgb;
    half  alpha  = _BaseColor.a   * input.vColor.a * _Opacity;

    // [MAP DELTA — unlit fill-extrusion face shading] Unlit ≠ flat solid blocks. With NO normal term every
    // wall + the roof render the identical colour, so a building reads as one featureless block. Apply a
    // cheap HALF-Lambert off the scene's main directional light: the sun-facing wall is brightest, the
    // opposite wall sits at the 0.5 floor (never pure black), the roof between — so faces at different
    // orientations differ and the 3D form reads. DIRECTION ONLY (no light colour/intensity) keeps it
    // predictable and exposure-independent; rotating the scene sun re-shades the buildings. Fills and lines
    // are flat 2D and deliberately DON'T do this (no face normal to react to). The main light is guaranteed
    // to exist under Unlit — see Bootstrapper.EnsureDirectionalLight.
    Light mainLight = GetMainLight();
    half ndl = saturate(dot(normalize(input.normalWS), mainLight.direction));
    albedo *= ndl * 0.5h + 0.5h;

    alpha = AlphaDiscard(alpha, _Cutoff);
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

#endif // MAP_FILL_EXTRUSION_FORWARD_UNLIT_PASS_INCLUDED
