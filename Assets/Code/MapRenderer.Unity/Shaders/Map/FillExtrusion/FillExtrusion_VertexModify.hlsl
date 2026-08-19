#ifndef MAP_FILL_EXTRUSION_VERTEX_MODIFY_INCLUDED
#define MAP_FILL_EXTRUSION_VERTEX_MODIFY_INCLUDED

// ============================================================================
// FillExtrusion_VertexModify.hlsl — fill-extrusion layer implementation of the MapVertexModify hook.
//
// S23 I2b — D1: height extrusion happens ENTIRELY here, in the vertex shader, along a per-vertex baked
// `extrudeUp` direction (see StyledFillExtrusionTileBuilder's class doc for the full sec-φ derivation,
// OQ1/C1-A). The mesh itself carries a HEIGHT-AGNOSTIC footprint (elevation 0); `_ExtrusionBase`/
// `_ExtrusionHeight` (constant/zoom, S11) plus the per-vertex `bakedBaseHeight` (data-driven, S12) select
// how far along `extrudeUp` this vertex sits. See StyledFillExtrusionMeshTests' T1 (position
// height-agnostic) and T5 (uniform vs bake) for the invariant this hook must preserve: changing
// `_ExtrusionHeight` must NEVER require a re-mesh.
//
// fill-extrusion-vertical-gradient (I4): FillExtrusionVerticalGradientFactor darkens the WALL sides toward
// the base to fake ambient occlusion. It is a per-vertex factor the passes fold into vColor (see the
// function doc) rather than a term read here — MapVertexModify only moves position.
//
// fill-extrusion-translate (I2b): the SAME anchor-aware px→world pattern as Fill_VertexModify, applied
// AFTER the height extrusion (so the translate's "map" anchor tangent frame reads the ALREADY-extruded
// vertex — correct for a roof vertex whose position has moved along extrudeUp).
// ============================================================================

// MapPixelsToWorld is shared with Fill and Line via ../PixelsToWorld.hlsl (S23 I2a/I2b) — FillExtrusion is
// the THIRD carrier; see Shaders/README.md "Shared px→world include".
#include "../PixelsToWorld.hlsl"

// fill-extrusion-vertical-gradient (I4): the wall-base brightness when the gradient is ON. The spec property
// is a BOOLEAN (no amount), so this AO look is a fixed constant, derived clean-room and visually tunable.
#define FILL_EXTRUSION_VGRADIENT_BASE 0.6

// Per-vertex vertical-gradient multiplier for the surface color. Darkens toward the base (t=0) and fades to
// no-op at the roof (t=1). The passes fold it into vColor in the vertex shader — because the factor is LINEAR
// in t, per-vertex interpolation equals a per-fragment evaluation across a flat wall quad, so no extra
// fragment interpolator is needed; the fragment's existing `albedo *= vColor.rgb` applies it. Roof-cap verts
// carry t=1 (StyledFillExtrusionTileBuilder.WriteFlatRoof/WriteGlobeRoof) ⇒ factor 1 ⇒ the roof is never
// darkened. Gated by _VerticalGradient (spec default 1 = on; 0 = off ⇒ factor 1 everywhere).
half FillExtrusionVerticalGradientFactor(float t)
{
    float baseBrightness = lerp(1.0, FILL_EXTRUSION_VGRADIENT_BASE, _VerticalGradient); // _VG=0 → 1 (off)
    return (half)lerp(baseBrightness, 1.0, t);                                          // t=0 base → 1 roof
}

// Must be #included AFTER FillExtrusion_LitInput.hlsl (which declares the CBUFFER props used below).
// FillExtrusion.shader includes FillExtrusion_LitInput.hlsl → FillExtrusion_VertexModify.hlsl →
// FillExtrusion_<Pass>.hlsl in that define-before-use order.
//
// extrudeUp:    the sec(φ)-baked extrude-up direction (OBJECT space — TransformObjectToWorldDir converts
//               it, exactly like normalOS/tangentOS below), SEPARATE from the lighting normal.
// t:            0 = floor/base, 1 = roof/height.
// bakedBaseHeight: x = per-vertex baked fill-extrusion-base, y = per-vertex baked fill-extrusion-height
//               (S12; both 0 on the constant/zoom-only path — see ExtrudeAndBake's doc in the C# builder).
void MapVertexModify(
    inout float3 positionOS, float3 normalOS, float4 tangentOS, float3 extrudeUp, float t, float2 bakedBaseHeight)
{
    // Height extrusion (D1, C1-A, OQ1): ADDITIVE composition of the uniform (constant/zoom) and the baked
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
