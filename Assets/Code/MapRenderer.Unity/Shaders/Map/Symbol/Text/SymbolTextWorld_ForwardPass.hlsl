// SymbolTextWorld_ForwardPass.hlsl — Map/Symbol/TextWorld vertex + fragment (Epic A / A0; Stage AC adds
// the along-line tangent-rotation branch for curved text).
//
// VERTEX: the world-anchored sibling of the retired screen-space shader's px→clip bypass. Every
// WorldBillboardVertex's AnchorLocal IS a real object-space position (tile-local render space — the
// floating-origin Level-1 bake, see FloatingOrigin.cs) — this vertex stage runs it through the STOCK URP
// MVP (TransformObjectToHClip; the floating-origin Level-2 rebase lives in unity_ObjectToWorld, set once
// per frame by the label renderer, mirrors tile placement — world-anchored-labels-design.md §3.4), then
// adds a constant-LOGICAL-px glyph-corner offset in clip space so the glyph stays a fixed screen size at
// any depth. No `ndc.y = -ndc.y`: that was the retired OLD path's own on-screen calibration for its
// px→clip bypass — stock MVP already handles Y correctly here (A0's Y-flip
// reconciliation lives in the emit/test convention, never a shader flip — see WorldSymbolAbRenderSnapshotTests).
//
// Stage AC (curved-world) D-I: when AlignFlags bit1 is set (along-line, curved only), the UNROTATED corner
// (input.offsetPx) is rotated by the LIVE projected screen angle of the baked world Tangent — see D-E below
// — instead of the point/icon north-up passthrough. Point/icon never take this branch (bit1 clear), so
// their render stays byte-identical.
//
// FRAGMENT: was DUPLICATED verbatim from the retired screen-space shader (not factored out at the time —
// that would have risked the OLD path's frozen goldens). Now the only surviving copy; a follow-up may
// still fold it into a shared include (no functional change either way).

// W2: when AlignFlags bit2 is ALSO set (map pitch alignment, curved only), `offsetPx` is not pixels at all —
// it is WORLD METRES, and the corner is displaced in the ground plane at the anchor BEFORE projection so the
// glyph is a fixed WORLD size that foreshortens with depth. See SymbolWorldPitchAlign.hlsl for the model and
// for why that path queries no ruler. Bit2 clear (every point/icon label, every viewport-pitched curved
// label) keeps the constant-logical-px path below, statement for statement.

#ifndef MAP_SYMBOL_WORLD_FORWARD_PASS_INCLUDED
#define MAP_SYMBOL_WORLD_FORWARD_PASS_INCLUDED

#include "../SymbolWorldPitchAlign.hlsl"

// HLSL binds vertex inputs by semantic, not struct declaration order — this ordering is cosmetic here
// (mirrors SymbolAttributes' identical note); the LOAD-BEARING order lives in WorldBillboardMeshBuilder's
// VertexAttributeDescriptor array / WorldBillboardVertex's field order.
struct SymbolWorldAttributes
{
    float3 anchorOS   : POSITION;  // tile-local render-space anchor (WorldBillboardVertex.AnchorLocal)
    float3 colorRGB   : COLOR;     // text color, verbatim (no sRGB conversion — see WorldBillboardVertex)
    float2 uv         : TEXCOORD0;
    float  page       : TEXCOORD1; // atlas Texture2DArray layer
    float2 offsetPx   : TEXCOORD2; // unrotated glyph-corner offset — logical px, OR METRES when bit2 is set (W2)
    float  alignFlags : TEXCOORD3; // bit0: map(1)/viewport(0) rotation-alignment (A3); bit1: along-line
                                   // (Stage AC); bit2: map PITCH alignment (W2) — offsetPx is metres
    float  opacity    : TEXCOORD4; // stream 1 — A0: constant 1
    float3 tangentOS  : TEXCOORD5; // Stage AC: tile-local WORLD tangent along the line; zero/unread for point/icon
    float3 up         : TEXCOORD6; // P2: the unit surface normal at the anchor; read only by W2's map-pitch branch
};

struct SymbolWorldVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float4 color      : COLOR;
    float  page       : TEXCOORD1;
};

// Stage AC D-E: rotates a 2D vector CCW (y-up logical-px frame) by `ang` — the SAME convention
// BillboardMath.Rotate uses on the CPU side for the old screen path's rotation.
float2 RotateOffsetPx(float2 p, float ang)
{
    float s, c;
    sincos(ang, s, c);
    return float2(c * p.x - s * p.y, s * p.x + c * p.y);
}

SymbolWorldVaryings SymbolWorldPassVertex(SymbolWorldAttributes input)
{
    SymbolWorldVaryings output = (SymbolWorldVaryings)0;

    float4 clip = TransformObjectToHClip(input.anchorOS);    // stock URP MVP; floating origin in unity_ObjectToWorld
    float2 off  = input.offsetPx;                             // unrotated corner (both point/icon and curved)

    // W2: bit2 set ⇒ map PITCH alignment. `off` is then WORLD METRES, and the whole clip position is rebuilt
    // by displacing the anchor in its own ground plane before projection — see SymbolWorldPitchAlign.hlsl.
    // The tangent rotation the else-branch applies below is intrinsic there (the ground frame's x̂ IS the road
    // tangent), so the two branches are alternatives, never composed.
    if (SymbolWorldIsMapPitched(input.alignFlags))
    {
        clip = SymbolWorldMapPitchClip(input.anchorOS, input.tangentOS, input.up, off);
    }
    else
    {
        // Stage AC D-I: bit1 set ⇒ along-line (curved) — rotate `off` by the LIVE projected screen angle of the
        // baked world Tangent, ignoring bit0 (MapLibre line placement ignores text-rotation-alignment). Point/
        // icon never set bit1, so this branch is never taken for them (byte-identical render, unread Tangent).
        if (input.alignFlags >= 1.5)
        {
            // D-E (primary form, magnitude-robust over a finite-difference second point): project the world
            // tangent as a DIRECTION (w=0) through the SAME object→world→clip transform TransformObjectToHClip
            // composes — the Jacobian only holds if clipT and clipA share that transform. UNITY_MATRIX_MVP is
            // used NOWHERE in this codebase and may not resolve under URP; GetWorldToHClipMatrix/
            // GetObjectToWorldMatrix are the codebase's object-space convention (Line_VertexExtrude.hlsl).
            float4 clipT = mul(GetWorldToHClipMatrix(), mul(GetObjectToWorldMatrix(), float4(input.tangentOS, 0.0)));

            // Screen-space direction of the world tangent (quotient rule d(clip.xy/clip.w)); only the DIRECTION
            // matters, so this is invariant to the tangent's (unit) magnitude and never blows up near w=0 the
            // way a finite-difference second point would.
            float2 sdir = clipT.xy * clip.w - clip.xy * clipT.w;
            sdir *= _ScreenParamsLogical.xy;             // ndc→px aspect correction (x,y px-per-ndc differ)
            // D-H (RESOLVED — empirically, per WorldCurvedAbRenderSnapshotTests' 45°/90° diagonal+vertical sweep):
            // NO extra Y-frame flip here. The A0-F2 Offset.y negation (this file's header) reconciles the OLD
            // path's on-screen calibration flip for the STATIC corner offset — a separate concern from this
            // Jacobian's angle, which already lands in the SAME sense as BillboardMath's Y-up screen rotation
            // (`atan2(sdir.y, sdir.x)` directly, unnegated, matches OLD's `atan2(chord.y, chord.x)`). Flipping
            // sdir.y here rotates every curved glyph to the mirrored angle (confirmed RED: it passed the 0°
            // horizontal case — which can't discriminate a sign error — and failed both the 45° and 90° cases,
            // exactly the D-H risk this file's plan flagged).

            // D-J: a degenerate projected tangent (edge-on to the camera under tilt) OR an anchor behind the
            // camera (the ndc division feeding sdir is undefined there) falls back to angle 0 (upright) — rare,
            // graceful; the near-pin below still keeps the glyph drawn.
            bool degenerate = dot(sdir, sdir) < 1e-8 || clip.w <= 1e-6;
            float ang = degenerate ? 0.0 : atan2(sdir.y, sdir.x);
            off = RotateOffsetPx(off, ang);
        }

        clip.xy += off / _ScreenParamsLogical.xy * 2.0 * clip.w;  // LOGICAL viewport (not physical — DPR bug), constant-px size
    }

    // Deliberately OUTSIDE the branch: the near-pin is what keeps labels drawn over geometry, and a
    // map-pitched glyph needs it exactly as much as a screen one.
    clip.z   = UNITY_NEAR_CLIP_VALUE * clip.w;                 // near-pin; MUST be * clip.w (NOT the old w=1 form)

    output.positionCS = clip;
    output.uv    = input.uv;
    output.page  = input.page;
    output.color = float4(input.colorRGB, input.opacity);     // fragment's input.color.a still works unchanged
    return output;
}

half4 SymbolWorldPassFragment(SymbolWorldVaryings input) : SV_Target
{
    half distSample = SAMPLE_TEXTURE2D_ARRAY(_MainTex, sampler_MainTex, input.uv, input.page).r;

    // Analytic screen-space AA (Chlumský msdfgen technique — public/MIT, clean-room-safe; NOT MapLibre's
    // shader). Derive the field's screen-pixel scale from the LINEAR uv gradient + atlas texel size + the
    // SDF's texel range — NOT fwidth of the nonlinearly-sampled distance (which is noisy AND size-blind, so
    // it over-softened and forced a low _SdfEdge to stay visible). screenDist is then the signed distance
    // from the fill edge in SCREEN PIXELS: the transition is a fixed ~1px at every zoom, and the body is
    // solid the instant distSample passes _SdfEdge (the "above threshold => solid" behaviour we want).
    float2 unitRange     = _SdfPixelRange * _MainTex_TexelSize.xy;   // SDF range, in uv units
    float2 screenTexSize = 1.0 / max(fwidth(input.uv), 1e-6);        // screen px per uv unit
    float  screenPxRange = max(0.5 * dot(unitRange, screenTexSize), 1.0);

    half screenDist = (distSample - _SdfEdge) * screenPxRange;       // signed screen px from the fill edge
    half fillAlpha  = saturate(screenDist / max(_SdfSoftness, 1e-3h) + 0.5h);

    // Halo: the SAME signed field, with the edge pushed OUT by _HaloWidthPx screen px and the transition
    // widened by _HaloBlurPx — both now real screen pixels (size-independent), no px->SDF approximation.
    half haloAlpha = saturate((screenDist + _HaloWidthPx) / max(_SdfSoftness + _HaloBlurPx, 1e-3h) + 0.5h);

    half4 result;
    result.rgb = lerp(_HaloColor.rgb, input.color.rgb, fillAlpha);
    half coverage = max(fillAlpha, haloAlpha * _HaloColor.a);
    result.a = coverage * input.color.a;
    return result;
}

#endif // MAP_SYMBOL_WORLD_FORWARD_PASS_INCLUDED
