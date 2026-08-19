// PixelsToWorld.hlsl — shared map helper: MapPixelsToWorld (S23 I2a).
//
// Clean-room map math, not a URP/MapLibre mirror. Consumes URP-library macros
// (TransformWorldToHClip, UNITY_MATRIX_P, _ScreenParams) that the includer's own
// <Layer>_LitInput.hlsl brings in first — no UCL header here, this is our own code.
//
// Hoisted out of Fill_VertexModify.hlsl / Line_VertexExtrude.hlsl, which previously carried
// character-identical, sentinel-pinned copies of this block (verified drift-free by
// ShaderStructureTests before the hoist). One copy retires that sentinel mechanism; the
// FillExtrusion layer (S23 I2b) is the third carrier this was built to serve.
//
// Sibling-parent placement (Shaders/Map/), included as "../PixelsToWorld.hlsl" — the same
// pattern SymbolWorldPitchAlign.hlsl already uses for Text/ + Icon/. See Shaders/README.md
// "Shared px→world include".
//
// Must be #included AFTER the includer's own include-guard #define and BEFORE the function
// that calls MapPixelsToWorld (define-before-use) — the position is load-bearing, not
// cosmetic: the body below reads URP macros that only <Layer>_LitInput.hlsl has declared.

#ifndef MAP_PIXELS_TO_WORLD_INCLUDED
#define MAP_PIXELS_TO_WORLD_INCLUDED

// World metres per screen pixel at `centerWS`, measured along the UNIT direction `dirWS`.
//
// Method: pick a reference world length that projects to ~2% of NDC height at this depth
// (worldPerNdcY = |clip.w| / P[1][1] — depth-scaled under perspective, constant under ortho), measure how
// many device pixels it spans along `dirWS`, and divide out the foreshortening the PROBE ITSELF picked up
// (the w-ratio below). Asking the projection matrix instead of modelling it makes this correct under
// foreshortening, tilt, any latitude, and either projection.
//
// Per-vertex AND per-direction, both of which matter: |clip.w| is view depth, so a far vertex probes with a
// longer ruler; and because the probe steps along `dirWS`, a tilted view measures the hard-foreshortened
// screen-down axis differently from the barely-foreshortened screen-right one. A single frame-wide
// metres-per-pixel scalar (the pre-S104 _MetersPerPixel uniform) cannot express either.
//
// ── The w-ratio is CORRECTNESS, not a refinement (S111). Derivation, because it is not obvious ──────
// clip.w is AFFINE in world position under any projective transform, so along the probe w(t) = w0 + t*g for
// some constant g, and clip.xy is affine too. The perspective divide therefore gives
//
//     ndc(t) - ndc(0) = t*B / (1 + t*g/w0) = t*B * (w0 / w(t))
//
// where B is the TRUE (differential) NDC-per-metre at centerWS — the thing we actually want. So the span we
// measure is the true one scaled by exactly w0/wRef: the probe travels into a different depth and shrinks
// there. Multiplying the measured span by wRef/w0 removes that factor IDENTICALLY, to all orders, for
// either sign of dirWS — and the projection hands us both w's for free, so this needs no fov, no view axis,
// and no knowledge of which projection is bound.
//
// Uncorrected, the helper returned the true scale times wRef/w0 = 1 + e, with
// e = 0.02*tan(fov/2)*dot(dirWS,fwd), while -dirWS returned it times (1 - e): TWO DIFFERENT RULERS FOR ONE
// PHYSICAL AXIS, 1.910% apart at fov 60 / tilt 55, 2.336% at the fov-60 ceiling, exactly 0 when dirWS is
// perpendicular to the view axis. The ribbon's two vertices at a station carry opposite extrudeN and hit
// exactly that. What it cost, all of it removed here:
//   - WIDTH: the band's two edges were sized with different rulers. Their WORLD separation stayed
//     2*halfWidth — the errors cancel before the perspective divide — but the band's centre sat
//     e*halfWidth off the centreline, and because screen position is rational in world offset the RENDERED
//     width moved too: 0.008 px on a 16 px band, 0.48 px on a 120 px one.
//   - LINE-OFFSET: also paired, but the two vertices take a COMMON offset each with its own ruler, so an
//     offset of L pixels leaked e*L into the HALF-WIDTH — 12.1% at L = 160 on a 24 px line, and unbounded
//     in L, because nothing bounds it by the styled width.
//   - LINE-TRANSLATE / FILL-TRANSLATE: one direction, so a flat ~0.95% error on the offset magnitude, and
//     only under anchor "map" — the viewport anchor's axes are perpendicular to the view axis.
// Two properties follow and are worth knowing: the result no longer depends on the 0.02 probe length at all
// (it cancels exactly), so that constant is a floating-point PRECISION choice and not an accuracy one; and
// under an orthographic projection wRef == w0 bitwise, so the factor is exactly 1.0 and this function is
// bit-identical to its pre-S111 self.
//
// ORDER IS DELIBERATE: the correction multiplies the PIXEL SPAN, before the clamp. refPx then means what it
// always meant — the device-pixel span of refMag world metres at this vertex's own depth — so the floor
// below keeps its exact meaning. Correcting the RETURNED value instead would scale the cap by 1/(1+e),
// moving it by up to ~1.2%.
float MapPixelsToWorld(float3 centerWS, float3 dirWS)
{
    float4 clipCenter = TransformWorldToHClip(centerWS);

    float projY  = max(abs(UNITY_MATRIX_P._m11), 1e-6);
    float refMag = (abs(clipCenter.w) / projY) * 0.02;

    float4 clipRef = TransformWorldToHClip(centerWS + dirWS * refMag);

    // Fallback (~the un-foreshortened target) for the two states in which the probe carries no information:
    // either endpoint behind the camera (the perspective divide is meaningless there), or a DEGENERATE
    // dirWS. The same test guards the division by clipCenter.w, which is why the w-ratio lives INSIDE this
    // branch: the fallback is an approximation with no probe to correct.
    //
    // dirWS == 0 is the ROUND-CAP PIVOT: Line_VertexExtrude zeroes unitDir_WS at a zero-extrudeN vertex on
    // purpose, to keep the pivot on the centreline. A zero direction steps zero metres, so ndcDelta and
    // refPx are both 0 and the clamp below would return refMag/0.1 — 51x the true scale at the AA fixture's
    // ortho camera, which collapsed the round cap's ink once the width stopped sharing the same blown-up
    // number and the error stopped cancelling. The fallback is the RIGHT answer here, not merely a safe one:
    // refMag/(0.01*H) == 2*|w|/(P11*H) is metres per device pixel at THIS vertex's depth along an
    // unforeshortened screen axis — direction-free, which is exactly what a zero direction asks for, and
    // bitwise the ortho camera's MetresPerPx.
    float refPx = 0.01 * _ScreenParams.y;
    if (dot(dirWS, dirWS) > 1e-12 && clipCenter.w > 1e-5 && clipRef.w > 1e-5)
    {
        float2 ndcDelta = (clipRef.xy / clipRef.w) - (clipCenter.xy / clipCenter.w);
        refPx = length(ndcDelta * 0.5 * _ScreenParams.xy) * (clipRef.w / clipCenter.w);
    }

    // Clamp the measured span so an edge-on direction (refPx → 0) cannot send the scale to infinity. This
    // is a real limit, not just a NaN guard: for a direction nearly parallel to the view axis (a southward
    // offset with the camera tilted at the horizon) the offset falls SHORT of the styled pixel count rather
    // than exploding.
    return refMag / max(refPx, 0.1);
}

#endif // MAP_PIXELS_TO_WORLD_INCLUDED
