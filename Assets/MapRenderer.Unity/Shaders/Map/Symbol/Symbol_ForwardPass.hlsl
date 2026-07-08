// Symbol_ForwardPass.hlsl — Map/SymbolText vertex + fragment (S20 Slice 1).
//
// VERTEX: the Graphics.RenderMesh screen-space bypass (S20 plan Risk #1). Every BillboardVertex's
// POSITION is ALREADY a logical screen-pixel coordinate (computed by LabelScreenProjection +
// BillboardMath in C# from the real camera's view-projection matrix) — NOT an object/world-space mesh
// position. This vertex stage converts px -> clip DIRECTLY and NEVER calls
// TransformObjectToWorld/TransformWorldToHClip/UNITY_MATRIX_VP: the object-to-world matrix
// Graphics.RenderMesh is given is irrelevant here (see README).
//
// FRAGMENT: unlit SDF threshold (S18 GlyphSdf/iso convention, _SdfEdge default 0.75) with fwidth-derived
// antialiasing (the Green/Valve "Improved Alpha-Tested Magnification" technique — public paper, no
// MapLibre source read) + a halo-under-fill second (wider) threshold.

#ifndef MAP_SYMBOL_FORWARD_PASS_INCLUDED
#define MAP_SYMBOL_FORWARD_PASS_INCLUDED

// Field order mirrors BillboardVertex/LabelPlacementSystem.VertexDescriptors (Position, Color, TexCoord0)
// for readability; HLSL binds vertex inputs by semantic, not struct declaration order, so this ordering
// is cosmetic here — the LOAD-BEARING order lives in the mesh's own VertexAttributeDescriptor array.
struct SymbolAttributes
{
    float4 positionOS : POSITION; // xy = logical screen px; z = NDC depth carried through unused in Slice 1
                                   // (ZTest Always; clip.z is pinned to UNITY_NEAR_CLIP_VALUE below, not this field)
    float4 color      : COLOR;    // text-color × text-opacity, already baked by LabelPlacementSystem
    float2 uv         : TEXCOORD0;
};

struct SymbolVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float4 color      : COLOR;
};

SymbolVaryings SymbolPassVertex(SymbolAttributes input)
{
    SymbolVaryings output = (SymbolVaryings)0;

    float2 ndc = (input.positionOS.xy / _ScreenParamsLogical.xy) * 2.0 - 1.0;

    // Y-flip for the on-screen render path. Our input is a y-up, bottom-left-origin screen pixel
    // (LabelScreenProjection mirrors Camera.WorldToScreenPoint). Empirically the on-screen render carries
    // an EXTRA vertical flip vs a naive clip mapping — URP renders the camera to an intermediate
    // RenderTexture and blits to the backbuffer, and that blit flips Y (and _ProjectionParams.x is -1 in
    // BOTH the on-screen intermediate-RT and the off-screen snapshot RT, so it can't distinguish them).
    // On-screen is the ground truth, so flip here to land north-up / glyph-upright on screen; the
    // headless snapshot test (direct camera→RT readback, which lacks the URP blit flip) is realigned to
    // this convention separately.
    ndc.y = -ndc.y;

    // Clip-space Z: ZTest Always/ZWrite Off make the real depth irrelevant for the depth TEST, but the
    // GPU's near/far clip VOLUME test happens regardless of ZTest/ZWrite -- a separate stage from depth
    // testing. input.positionOS.z carries clip.z/clip.w from Camera.projectionMatrix, which is Unity's
    // OpenGL-convention matrix (NDC z in [-1,1]) -- on a reverse-Z platform (Metal/D3D/Vulkan, NDC z in
    // [0,1]) a near-ish point's GL-convention z routinely falls outside [0,1] and the whole triangle is
    // clipped before rasterization (0 pixels, no warning -- this is what
    // SymbolAtlasOrientationSnapshotTests caught). Pin z to the near plane instead, exactly like URP's own
    // GetFullScreenTriangleVertexPosition (Common.hlsl) does for its manually-built clip position.
    output.positionCS = float4(ndc, UNITY_NEAR_CLIP_VALUE, 1.0);
    output.uv = input.uv;
    output.color = input.color;
    return output;
}

half4 SymbolPassFragment(SymbolVaryings input) : SV_Target
{
    half distSample = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).r;

    half aa = max(fwidth(distSample) * _SdfSoftness, 1e-5h);
    half fillAlpha = smoothstep(_SdfEdge - aa, _SdfEdge + aa, distSample);

    // Halo: a second, WIDER threshold under the fill (Slice 1 linear px-approximation — see the input
    // header's doc comment + README; Slice 3 calibrates this precisely).
    half haloShift = _HaloWidthPx * _SdfDistancePerPixel;
    half haloAA = aa + max(_HaloBlurPx * _SdfDistancePerPixel, 0.0h);
    half haloEdge = saturate(_SdfEdge - haloShift);
    half haloAlpha = smoothstep(haloEdge - haloAA, haloEdge + haloAA, distSample);

    half4 result;
    result.rgb = lerp(_HaloColor.rgb, input.color.rgb, fillAlpha);
    half coverage = max(fillAlpha, haloAlpha * _HaloColor.a);
    result.a = coverage * input.color.a;
    return result;
}

#endif // MAP_SYMBOL_FORWARD_PASS_INCLUDED
