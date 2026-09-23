#ifndef MAP_FILL_EXTRUSION_VERTEX_MODIFY_INCLUDED
#define MAP_FILL_EXTRUSION_VERTEX_MODIFY_INCLUDED

// ============================================================================
// FillExtrusion_VertexModify.hlsl — fill-extrusion layer implementation of the MapVertexModify hook.
//
// Height extrusion happens ENTIRELY here, in the vertex shader, along a per-vertex baked
// `extrudeUp` direction (see StyledFillExtrusionTileBuilder's class doc for the full sec-φ derivation).
// The mesh itself carries a HEIGHT-AGNOSTIC footprint (elevation 0); `_ExtrusionBase`/
// `_ExtrusionHeight` (constant/zoom) plus the per-vertex `bakedBaseHeight` (data-driven) select
// how far along `extrudeUp` this vertex sits. StyledFillExtrusionMeshTests pins the invariant this hook
// must preserve — Wall_FloorAndRoof_CoincideInFootprintPosition (position height-agnostic) and
// ConstantHeight_BakeStreamStaysZero / DataDrivenHeight_BakesEvaluatedValuePerVertex (uniform vs bake):
// changing `_ExtrusionHeight` must NEVER require a re-mesh.
//
// fill-extrusion-translate: the SAME anchor-aware px→world pattern as Fill_VertexModify, applied
// AFTER the height extrusion (so the translate's "map" anchor tangent frame reads the ALREADY-extruded
// vertex — correct for a roof vertex whose position has moved along extrudeUp).
// ============================================================================

// MapPixelsToWorld is shared with Fill and Line via ../PixelsToWorld.hlsl; see Shaders/README.md
// "Shared px→world include".
#include "../PixelsToWorld.hlsl"

// Must be #included AFTER FillExtrusion_LitInput.hlsl (which declares the CBUFFER props used below).
// FillExtrusion.shader includes FillExtrusion_LitInput.hlsl → FillExtrusion_VertexModify.hlsl →
// FillExtrusion_<Pass>.hlsl in that define-before-use order.
//
// extrudeUp:    the sec(φ)-baked extrude-up direction (OBJECT space — TransformObjectToWorldDir converts
//               it, exactly like normalOS/tangentOS below), SEPARATE from the lighting normal.
// t:            0 = floor/base, 1 = roof/height.
// bakedBaseHeight: x = per-vertex baked fill-extrusion-base, y = per-vertex baked fill-extrusion-height
//               (both 0 on the constant/zoom-only path — see ExtrudeAndBake's doc in the C# builder).
void MapVertexModify(
    inout float3 positionOS, float3 normalOS, float4 tangentOS, float3 extrudeUp, float t, float2 bakedBaseHeight)
{
    // Height extrusion: ADDITIVE composition of the uniform (constant/zoom) and the baked
    // (data-driven) contributions — so the uniform-only and bake-only paths both reduce to the plain
    // uniform behaviour when the other side is 0, with no branch (see ExtrudeAndBake's doc).
    float elevation = lerp(_ExtrusionBase + bakedBaseHeight.x, _ExtrusionHeight + bakedBaseHeight.y, t);

    // TransformObjectToWorldDir is the VECTOR (not point) transform — extrudeUp*elevation is a world-space
    // displacement, so the object-space delta to add is its image under the INVERSE transform's linear part
    // (float3x3, no translation) — same technique Fill_VertexModify uses for its px→world offset below.
    float3 extrudeUpWS = TransformObjectToWorldDir(extrudeUp);
    positionOS += mul((float3x3)GetWorldToObjectMatrix(), extrudeUpWS * elevation);

    // fill-extrusion-translate: same anchor-aware px→world pattern as Fill_VertexModify (Both anchors
    // measure px→world SEPARATELY PER AXIS — see that file's comment for why a single scalar is wrong
    // under tilt).
    if (all(abs(_FillExtrusionTranslate.xy) < 1e-6))
        return; // spec default [0,0] — skip the projection round-trips entirely

    float3 centerWS = TransformObjectToWorld(positionOS);

    float3 axisRightWS;
    float3 axisDownWS;
    if (_FillExtrusionTranslateAnchor > 0.5)
    {
        // "viewport": screen-aligned.
        axisRightWS =  normalize(UNITY_MATRIX_I_V._m00_m10_m20);
        axisDownWS  = -normalize(UNITY_MATRIX_I_V._m01_m11_m21);
    }
    else
    {
        // "map": the surface tangent frame comes from the MESH (normal + tangent), never a hardcoded
        // +Y/XZ — see Fill_VertexModify's comment. On a roof vertex tangentOS is the projection's own east
        // (Mercator: constant +X; globe: per-vertex east); on a wall vertex it is the wall's own along-edge
        // direction (StyledFillExtrusionTileBuilder bakes it there for a non-degenerate TBN) — so "map"
        // translate on a WALL vertex is anchored to the wall's edge, not true geographic east/north. A
        // known imprecision for a rarely-used property (translate render is deferred to be exact only for
        // the common roof case); it does not affect height, winding, or vertex count.
        float3 up    = normalize(TransformObjectToWorldNormal(normalOS));
        float3 east  = normalize(TransformObjectToWorldDir(tangentOS.xyz));
        float3 north = cross(east, up); // unit — east ⟂ up
        axisRightWS =  east;
        axisDownWS  = -north;
    }

    float3 offsetWS = axisRightWS * (_FillExtrusionTranslate.x * MapPixelsToWorld(centerWS, axisRightWS))
                    + axisDownWS  * (_FillExtrusionTranslate.y * MapPixelsToWorld(centerWS, axisDownWS));

    // Back to object space: the hook's contract is a positionOS displacement.
    positionOS += mul((float3x3)GetWorldToObjectMatrix(), offsetWS);
}

#endif // MAP_FILL_EXTRUSION_VERTEX_MODIFY_INCLUDED
