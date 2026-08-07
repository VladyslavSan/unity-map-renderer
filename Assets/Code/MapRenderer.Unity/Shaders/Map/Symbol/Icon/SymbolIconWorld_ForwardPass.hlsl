// SymbolIconWorld_ForwardPass.hlsl — Map/Symbol/IconWorld vertex + fragment (Epic A / A1: world-anchored
// icon billboards).
//
// VERTEX: the A0 SymbolTextWorld vertex stage (world MVP + px offset + near-pin), duplicated verbatim minus
// the `page` (TEXCOORD1) attribute — icons sample a single-page RGBA sprite atlas (plain Texture2D), not the
// glyph atlas's Texture2DArray, so Page is simply UNREAD here (the retired screen-space icon shader had the
// identical "ignore TEXCOORD1" precedent against the shared BillboardVertex/WorldBillboardVertex layout).
// Same A0-F2 Offset.y convention as SymbolTextWorld (baked in BillboardMath.BuildWorldQuad,
// not here — the vertex stage just consumes it).
//
// P-B: the along-line tangent branch (Stage AC's, ported VERBATIM from SymbolTextWorld_ForwardPass.hlsl —
// the duplication is the convention: a Map/ shader layer may not include another's pass, see
// docs/meshing-design.md §"Folder layout" — pinned by ShaderStructureTests.MapLayerFiles_DoNotIncludeCommonFolder).
// Without it a map-aligned line icon
// (road_one_way_arrow*) would draw unrotated — every arrow pointing screen-right regardless of the road.
//
// FRAGMENT: was DUPLICATED from the retired screen-space icon shader (delta (a): plain sprite sample x
// vertex color, no SDF/halo) — not factored out at the time, same A4 dedupe-fence rationale as
// SymbolTextWorld_ForwardPass.hlsl's header. Now the only surviving copy.

// W2: the map-pitch branch, on the same terms as the TEXT pass. This arm is NOT optional —
// road_one_way_arrow / road_one_way_arrow_opposite are line-placed ICON layers that resolve to
// pitch-alignment: map, and W1 already put their ANCHORS on the world ruler (StageCurved is shared across
// AtlasKind). Shipping the text arm alone would leave curved icons with world spacing and screen size — the
// exact defect W2 exists to remove, on the arm nobody is looking at.

#ifndef MAP_SYMBOL_ICON_WORLD_FORWARD_PASS_INCLUDED
#define MAP_SYMBOL_ICON_WORLD_FORWARD_PASS_INCLUDED

#include "../SymbolWorldPitchAlign.hlsl"

// HLSL binds vertex inputs by semantic, not struct declaration order (see SymbolTextWorld_ForwardPass.hlsl's
// identical note) — the LOAD-BEARING order lives in WorldBillboardMeshBuilder's VertexAttributeDescriptor
// array / WorldBillboardVertex's field order. Page (TEXCOORD1) is present in the shared buffer but unread here.
struct SymbolIconWorldAttributes
{
    float3 anchorOS   : POSITION;  // tile-local render-space anchor (WorldBillboardVertex.AnchorLocal)
    float3 colorRGB   : COLOR;     // icon color, verbatim (no sRGB conversion — see WorldBillboardVertex)
    float2 uv         : TEXCOORD0;
    float2 offsetPx   : TEXCOORD2; // unrotated glyph-corner offset — logical px, OR METRES when bit2 is set (W2)
    float  alignFlags : TEXCOORD3; // bit0: map(1)/viewport(0) rotation-alignment — WRITTEN, UNREAD;
                                   // bit1: along-line (P-B) — READ below;
                                   // bit2: map PITCH alignment (W2) — offsetPx is metres
    float  opacity    : TEXCOORD4; // stream 1 — the A-4 fade
    float3 tangentOS  : TEXCOORD5; // P-B: tile-local WORLD tangent along the line; zero/unread for a point icon
    float3 up         : TEXCOORD6; // P2: the unit surface normal at the anchor; read only by W2's map-pitch branch
};

struct SymbolIconWorldVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float4 color      : COLOR;
};

// Stage AC D-E (ported for P-B): rotates a 2D vector CCW (y-up logical-px frame) by `ang` — the SAME
// convention BillboardMath.Rotate uses on the CPU side.
float2 RotateOffsetPx(float2 p, float ang)
{
    float s, c;
    sincos(ang, s, c);
    return float2(c * p.x - s * p.y, s * p.x + c * p.y);
}

SymbolIconWorldVaryings SymbolIconWorldPassVertex(SymbolIconWorldAttributes input)
{
    SymbolIconWorldVaryings output = (SymbolIconWorldVaryings)0;

    float4 clip = TransformObjectToHClip(input.anchorOS);    // stock URP MVP; floating origin in unity_ObjectToWorld
    float2 off  = input.offsetPx;                             // corner as staged (a point icon: no bearing rotation)

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
        // P-B: bit1 set ⇒ along-line (a map-aligned line icon) — rotate `off` by the LIVE projected screen angle
        // of the baked world Tangent, ignoring bit0 (MapLibre line placement ignores rotation-alignment). A POINT
        // icon never sets bit1, so this branch is never taken for it (byte-identical render, unread tangentOS).
        // `off` may already carry a CONSTANT icon-rotate baked in on the CPU (CandidateEmit.ExtraRotationRadians);
        // 2D rotations commute, so rotating it here by the tangent composes the two correctly.
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
            // D-H (RESOLVED — empirically, per WorldCurvedAbRenderSnapshotTests' 45°/90° diagonal+vertical sweep
            // on the TEXT pass this is copied from): NO extra Y-frame flip here. The A0-F2 Offset.y negation
            // (baked in BillboardMath.BuildWorldQuad) reconciles the OLD path's on-screen calibration flip for
            // the STATIC corner offset — a separate concern from this Jacobian's angle, which already lands in
            // the SAME sense as BillboardMath's Y-up screen rotation (`atan2(sdir.y, sdir.x)` directly,
            // unnegated). Flipping sdir.y here rotates every glyph to the mirrored angle (confirmed RED there:
            // it passed the 0° horizontal case — which cannot discriminate a sign error — and failed 45° and 90°).

            // D-J: a degenerate projected tangent (edge-on to the camera under tilt) OR an anchor behind the
            // camera (the ndc division feeding sdir is undefined there) falls back to angle 0 (upright) — rare,
            // graceful; the near-pin below still keeps the icon drawn.
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
    output.color = float4(input.colorRGB, input.opacity);
    return output;
}

half4 SymbolIconWorldPassFragment(SymbolIconWorldVaryings input) : SV_Target
{
    half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
    return c * input.color;
}

#endif // MAP_SYMBOL_ICON_WORLD_FORWARD_PASS_INCLUDED
