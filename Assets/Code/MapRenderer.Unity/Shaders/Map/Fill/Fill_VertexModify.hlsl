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
// (worldPerNdcY = |clip.w| / P[1][1] — depth-scaled under perspective, constant under ortho), then measure
// how many device pixels it actually spans along `dirWS`. Asking the projection matrix instead of modelling
// it makes this correct under foreshortening, tilt, any latitude, and either projection.
//
// Per-vertex AND per-direction, both of which matter: |clip.w| is view depth, so a far vertex probes with a
// longer ruler; and because the probe steps along `dirWS`, a tilted view measures the hard-foreshortened
// screen-down axis differently from the barely-foreshortened screen-right one. A single frame-wide
// metres-per-pixel scalar (the pre-S104 _MetersPerPixel uniform) cannot express either.
float MapPixelsToWorld(float3 centerWS, float3 dirWS)
{
    float4 clipCenter = TransformWorldToHClip(centerWS);

    float projY  = max(abs(UNITY_MATRIX_P._m11), 1e-6);
    float refMag = (abs(clipCenter.w) / projY) * 0.02;

    float4 clipRef = TransformWorldToHClip(centerWS + dirWS * refMag);

    // Fallback (~the un-foreshortened target) when either point is behind the camera and the perspective
    // divide would be meaningless.
    float refPx = 0.01 * _ScreenParams.y;
    if (clipCenter.w > 1e-5 && clipRef.w > 1e-5)
    {
        float2 ndcDelta = (clipRef.xy / clipRef.w) - (clipCenter.xy / clipCenter.w);
        refPx = length(ndcDelta * 0.5 * _ScreenParams.xy);
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
