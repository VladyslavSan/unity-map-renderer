#ifndef MAP_FILL_VERTEX_MODIFY_INCLUDED
#define MAP_FILL_VERTEX_MODIFY_INCLUDED

// ============================================================================
// Fill_VertexModify.hlsl — fill layer implementation of the MapVertexModify hook.
//
// Implements fill-translate: a SCREEN-PIXEL offset, converted to world metres here in the vertex shader
// through the measured px→world below (P5). It previously treated _FillTranslate.xy as world units
// while its own comment claimed "px→world applied CPU-side" — nothing applied it, so a fill-translate of
// [16, -8] displaced the geometry by 16 world METRES at every zoom instead of 16 screen pixels.
//
// Pattern: every layer has a vertex-modify file that defines MapVertexModify.
//   Line (S33)              → extrudes laterally along extrudeN (done in its own pass body).
//   Fill-extrusion (future) → will offset +Y by height attribute.
//
// Must be #included AFTER Fill_LitInput.hlsl (which declares the CBUFFER props used by the body below).
// Fill.shader includes Fill_LitInput.hlsl → Fill_VertexModify.hlsl → Fill_<Pass>.hlsl in that
// define-before-use order.
//
// fill-translate semantics (MapLibre Style Spec):
//   _FillTranslate.xy    = offset in SCREEN PIXELS. The spec states "negatives indicate left and up",
//                          so +x is RIGHT and +y is DOWN.
//   _FillTranslateAnchor = 0 → "map":      the offset rides the map — rotating and tilting with it.
//                        = 1 → "viewport": the offset is fixed to the screen, independent of map bearing.
// ============================================================================

// DUPLICATED, deliberately: the line layer carries a character-identical copy of the block below, in
// Map/Line/Line_VertexExtrude.hlsl. Sharing it via an include would mean reaching into the shared Common
// folder (spelled without the trailing slash on purpose — MapLayerFiles_DoNotIncludeCommonFolder greps for
// that literal substring anywhere in the file, comments included), which S66
// removed on purpose — it dissolved MapLitCore.hlsl so every layer folder is self-contained, and
// ShaderStructureTests pins that. So the copies are kept honest by a test instead of by hand:
// ShaderStructureTests.SharedShaderBlocks_AreIdenticalAcrossLayers extracts the text between the
// MAP-SHARED-BEGIN/END sentinels in each file and requires it to match character for character.
//
// Consequences worth knowing before editing: (1) any change here must be pasted verbatim into the line
// copy, or the gate fails — that is the point; (2) everything outside the sentinels is free to differ, so
// this explanatory comment does not have to be mirrored; (3) deliberately diverging the two is still
// possible — delete the sentinels — which is a visible, reviewable act rather than silent drift.
// If a THIRD layer ever needs this, that is the moment to reopen S66 properly rather than add a third copy.

// MAP-SHARED-BEGIN: PixelsToWorld
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

    // Fallback (~the un-foreshortened target) when either point is behind the camera and the perspective
    // divide would be meaningless. The same test guards the division by clipCenter.w, which is why the
    // w-ratio lives INSIDE this branch: the fallback is an approximation with no probe to correct.
    float refPx = 0.01 * _ScreenParams.y;
    if (clipCenter.w > 1e-5 && clipRef.w > 1e-5)
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
// MAP-SHARED-END: PixelsToWorld

// Screen-pixel offset → object-space displacement.
//
// Both anchors measure px→world SEPARATELY PER AXIS. A single scalar is only right when both axes project
// equally; under tilt the north/screen-down axis foreshortens hard while east/right barely does, so one
// scalar visibly skews the offset. line-translate now does the same (docs/line-translate-parity-design.md).
void MapVertexModify(inout float3 positionOS, float3 normalOS, float4 tangentOS)
{
    if (all(abs(_FillTranslate.xy) < 1e-6))
        return; // spec default [0,0] — skip the projection round-trips entirely

    float3 centerWS = TransformObjectToWorld(positionOS);

    float3 axisRightWS;
    float3 axisDownWS;
    if (_FillTranslateAnchor > 0.5)
    {
        // "viewport": screen-aligned. Camera right/up in world space are the inverse-view matrix's first two
        // basis columns; screen-down is -up, matching the spec's +y = down.
        axisRightWS =  normalize(UNITY_MATRIX_I_V._m00_m10_m20);
        axisDownWS  = -normalize(UNITY_MATRIX_I_V._m01_m11_m21);
    }
    else
    {
        // "map": the surface tangent frame comes from the MESH (normal + tangent), never a hardcoded +Y/XZ,
        // so a globe's radial normal and ENU tangent work unchanged. For Mercator (normal=+Y, tangent=+X)
        // east is +X and north is +Z. Screen-down on a north-up map is SOUTH, hence -north.
        float3 up    = normalize(TransformObjectToWorldNormal(normalOS));
        float3 east  = normalize(TransformObjectToWorldDir(tangentOS.xyz));
        float3 north = cross(east, up); // unit — east ⟂ up
        axisRightWS =  east;
        axisDownWS  = -north;
    }

    float3 offsetWS = axisRightWS * (_FillTranslate.x * MapPixelsToWorld(centerWS, axisRightWS))
                    + axisDownWS  * (_FillTranslate.y * MapPixelsToWorld(centerWS, axisDownWS));

    // Back to object space: the hook's contract is a positionOS displacement, and a tile's transform carries
    // a camera-relative translation (plus a rebase rotation on the globe), so identity cannot be assumed.
    positionOS += mul((float3x3)GetWorldToObjectMatrix(), offsetWS);
}

#endif // MAP_FILL_VERTEX_MODIFY_INCLUDED
