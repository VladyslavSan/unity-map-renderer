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

// MapPixelsToWorld is shared with Line and FillExtrusion via ../PixelsToWorld.hlsl (S23 I2a); see
// Shaders/README.md "Shared px→world include". It previously lived here as a sentinel-pinned,
// character-identical copy of the block also carried by Map/Line/Line_VertexExtrude.hlsl, kept honest
// by ShaderStructureTests comparing the two byte for byte. One copy now serves every carrier.
#include "../PixelsToWorld.hlsl"

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
