// SymbolIcon_ForwardPass.hlsl — Map/Symbol/Icon vertex + fragment (icon-support I5b).
//
// VERTEX: byte-identical to SymbolText_ForwardPass.hlsl's SymbolPassVertex (delta (c) is a NO-delta —
// verbatim clone, kept in lockstep on purpose). Every BillboardVertex's POSITION is ALREADY a logical
// screen-pixel coordinate; this stage converts px -> clip DIRECTLY, never touching the object-to-world
// transform (see SymbolText_ForwardPass.hlsl's header for the full rationale + the y-flip / reverse-Z-pin
// comments, which apply unchanged). NO UV re-flip here either — I4's SpriteSheet row-flip on decode already
// orients the sprite texture to match the glyph atlas's top-left-origin (px,py)==GetPixel(px,py) contract,
// so the UV path is identical to text's.
//
// FRAGMENT: delta (a) — plain RGBA sprite sample × vertex color (icon-opacity rides color.a, exactly like
// text-opacity rides it in the text shader). No SDF median/screenPxRange, no halo — an icon is a rasterized
// sprite, not a distance field.

#ifndef MAP_SYMBOL_ICON_FORWARD_PASS_INCLUDED
#define MAP_SYMBOL_ICON_FORWARD_PASS_INCLUDED

// Field order mirrors BillboardVertex/LabelPlacementSystem.VertexDescriptors (Position, Color, TexCoord0)
// — cosmetic here (HLSL binds by semantic), the LOAD-BEARING order lives in the mesh's own
// VertexAttributeDescriptor array (see SymbolText_ForwardPass.hlsl's identical comment).
struct SymbolAttributes
{
    float4 positionOS : POSITION; // xy = logical screen px; z = NDC depth carried through unused (ZTest Always)
    float4 color      : COLOR;    // icon-color x icon-opacity (LabelPlacementSystem-baked, mirrors text-color)
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

    // Y-flip for the on-screen render path — see SymbolText_ForwardPass.hlsl's SymbolPassVertex for the
    // full explanation (URP's intermediate-RT blit flip). Kept verbatim: the icon path shares the exact
    // same screen-space submission as text.
    ndc.y = -ndc.y;

    // Clip-space Z pinned to the near plane — see SymbolText_ForwardPass.hlsl's SymbolPassVertex for why
    // (the GL-convention-vs-reverse-Z clip-volume-test trap ZTest Always/ZWrite Off does NOT bypass).
    output.positionCS = float4(ndc, UNITY_NEAR_CLIP_VALUE, 1.0);
    output.uv = input.uv;
    output.color = input.color;
    return output;
}

half4 SymbolPassFragment(SymbolVaryings input) : SV_Target
{
    half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
    return c * input.color;
}

#endif // MAP_SYMBOL_ICON_FORWARD_PASS_INCLUDED
