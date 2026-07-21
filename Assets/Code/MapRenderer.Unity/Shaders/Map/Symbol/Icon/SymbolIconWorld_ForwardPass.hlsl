// SymbolIconWorld_ForwardPass.hlsl — Map/Symbol/IconWorld vertex + fragment (Epic A / A1: world-anchored
// icon billboards).
//
// VERTEX: the A0 SymbolTextWorld vertex stage (world MVP + px offset + near-pin), duplicated verbatim minus
// the `page` (TEXCOORD1) attribute — icons sample a single-page RGBA sprite atlas (plain Texture2D), not the
// glyph atlas's Texture2DArray, so Page is simply UNREAD here (the retired screen-space icon shader had the
// identical "ignore TEXCOORD1" precedent against the shared BillboardVertex/WorldBillboardVertex layout).
// Same A0-F2 OffsetPx.y convention as SymbolTextWorld (baked in BillboardMath.BuildWorldQuad,
// not here — the vertex stage just consumes it).
//
// FRAGMENT: was DUPLICATED from the retired screen-space icon shader (delta (a): plain sprite sample x
// vertex color, no SDF/halo) — not factored out at the time, same A4 dedupe-fence rationale as
// SymbolTextWorld_ForwardPass.hlsl's header. Now the only surviving copy.

#ifndef MAP_SYMBOL_ICON_WORLD_FORWARD_PASS_INCLUDED
#define MAP_SYMBOL_ICON_WORLD_FORWARD_PASS_INCLUDED

// HLSL binds vertex inputs by semantic, not struct declaration order (see SymbolTextWorld_ForwardPass.hlsl's
// identical note) — the LOAD-BEARING order lives in WorldBillboardMeshBuilder's VertexAttributeDescriptor
// array / WorldBillboardVertex's field order. Page (TEXCOORD1) is present in the shared buffer but unread here.
struct SymbolIconWorldAttributes
{
    float3 anchorOS   : POSITION;  // tile-local render-space anchor (WorldBillboardVertex.AnchorLocal)
    float3 colorRGB   : COLOR;     // icon color, verbatim (no sRGB conversion — see WorldBillboardVertex)
    float2 uv         : TEXCOORD0;
    float2 offsetPx   : TEXCOORD2; // unrotated glyph-corner offset, logical px
    float  alignFlags : TEXCOORD3; // bit0: map(1)/viewport(0) rotation-alignment — WRITTEN, UNREAD in A1
    float  opacity    : TEXCOORD4; // stream 1 — the A-4 fade
};

struct SymbolIconWorldVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float4 color      : COLOR;
};

SymbolIconWorldVaryings SymbolIconWorldPassVertex(SymbolIconWorldAttributes input)
{
    SymbolIconWorldVaryings output = (SymbolIconWorldVaryings)0;

    float4 clip = TransformObjectToHClip(input.anchorOS);    // stock URP MVP; floating origin in unity_ObjectToWorld
    float2 off  = input.offsetPx;                             // A1: no bearing rotation (north-up; AlignFlags deferred)
    clip.xy += off / _ScreenParamsLogical.xy * 2.0 * clip.w;  // LOGICAL viewport (not physical — DPR bug), constant-px size
    clip.z   = UNITY_NEAR_CLIP_VALUE * clip.w;                 // near-pin; MUST be * clip.w (NOT the old w=1 form)

    output.positionCS = clip;
    output.uv    = input.uv;
    output.color = float4(input.colorRGB, input.opacity);
    return output;
}

half4 SymbolIconWorldPassFragment(SymbolIconWorldVaryings input) : SV_Target
{
    half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
    return c * input.color;
}

#endif // MAP_SYMBOL_ICON_WORLD_FORWARD_PASS_INCLUDED
