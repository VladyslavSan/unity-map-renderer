#ifndef MAP_FILL_VERTEX_MODIFY_INCLUDED
#define MAP_FILL_VERTEX_MODIFY_INCLUDED

// ============================================================================
// Fill_VertexModify.hlsl — fill layer implementation of the MapVertexModify hook.
//
// Implements fill-translate: a SCREEN-PIXEL offset, converted to world metres here in the vertex shader
// through the measured px→world below. The CPU binds it in pixels, so a shader that read
// _FillTranslate.xy as world units would displace a [16, -8] fill-translate by 16 world METRES at every
// zoom instead of 16 screen pixels.
//
// Pattern: every layer has a vertex-modify file that defines MapVertexModify.
//   Line           → extrudes laterally along extrudeN (Line_VertexExtrude.hlsl).
//   Fill-extrusion → extrudes along the baked extrudeUp by height (FillExtrusion_VertexModify.hlsl).
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

// MapPixelsToWorld is shared with Line and FillExtrusion via ../PixelsToWorld.hlsl; see
// Shaders/README.md "Shared px→world include".
#include "../PixelsToWorld.hlsl"

// Screen-pixel offset → object-space displacement.
//
// Both anchors measure px→world SEPARATELY PER AXIS. A single scalar is only right when both axes project
// equally; under tilt the north/screen-down axis foreshortens hard while east/right barely does, so one
// scalar visibly skews the offset. line-translate now does the same (docs/line-translate-parity-design.md).
//
// The hook also carries the boundary band. `band` is the mesh's TEXCOORD3: (dirEast, dirNorth, side) in
// the vertex's own surface frame, `side` 0 on an interior vertex and 1 on a band vertex. It is handed to
// the fragment stage as `side` (Fill_BandCoverage.hlsl turns it into coverage) and displaced here: the
// band's outer ring is pushed ONE DEVICE PIXEL along (dirEast, dirNorth), measured per-vertex and
// per-direction so the strip stays a pixel wide under tilt, where the two screen axes foreshorten
// differently. dirEast/dirNorth carry the join's miter factor in their MAGNITUDE, which is what keeps the
// strip a pixel wide measured perpendicular to the edge through a corner.
//
// THE TWO HALVES NEVER MULTIPLY. `band.xy` IS the displacement in pixels; `band.z` is only the coverage
// coordinate. Scaling the displacement by `band.z` as well would be identical on the flat arm (where the
// job emits z ∈ {0,1} and xy == 0 whenever z == 0) and WRONG on the globe: GlobeFillSubdivideJob splits a
// band edge at its midpoint and LERPS both halves, so a product of the two is quadratic in the split
// parameter — a subdivided band pinches to a quarter pixel at every midpoint, compounding with depth,
// exactly where the shipped globe scene lives. Pinned by FillBandAttributeTests.
//
// The displacement is strictly OUTWARD and `band.z == 0` takes the branch away entirely, so an interior
// vertex moves by bitwise zero. That is the mechanism, not an optimisation: any inward component would put
// ramp coverage inside the boundary and reintroduce background bleed at every abutting edge.
void MapVertexModify(inout float3 positionOS, float3 normalOS, float4 tangentOS,
                     float3 band, out float side)
{
    side = band.z;

    // The mesh's own surface frame — normal and tangent, never a hardcoded +Y/XZ, so a globe's radial
    // normal and ENU tangent work unchanged.
    if (band.z != 0.0 && dot(band.xy, band.xy) > 0.0)
    {
        float3 bandUp    = normalize(TransformObjectToWorldNormal(normalOS));
        float3 bandEast  = normalize(TransformObjectToWorldDir(tangentOS.xyz));
        float3 bandNorth = cross(bandEast, bandUp); // unit — east perpendicular to up

        float3 outwardWS = bandEast * band.x + bandNorth * band.y;
        float  miter     = length(outwardWS);
        float3 dirWS     = outwardWS / miter;

        float3 bandCenterWS = TransformObjectToWorld(positionOS);
        positionOS += mul((float3x3)GetWorldToObjectMatrix(),
                          dirWS * (miter * MapPixelsToWorld(bandCenterWS, dirWS)));
    }

    // fill-translate is [0,0] on every shipped layer, so this branch is skipped for essentially every
    // vertex the product draws. An early `return` here would make "below it" a place code could be put —
    // and anything the band needs, put there, would be dead in exactly the shipped case. So there is no
    // "below it": the function ends with this block, and ShaderStructureTests forbids `return` in this
    // file. The condition is `!all(abs(t) < eps)` rather than an `any(abs(t) >= eps)` rewrite: the two
    // differ when a component is NaN, and this file is under a
    // byte-identity invariant.
    if (!all(abs(_FillTranslate.xy) < 1e-6))
    {
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
}

#endif // MAP_FILL_VERTEX_MODIFY_INCLUDED
