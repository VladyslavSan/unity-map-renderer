#ifndef MAP_FILL_VERTEX_MODIFY_INCLUDED
#define MAP_FILL_VERTEX_MODIFY_INCLUDED

// ============================================================================
// Fill_VertexModify.hlsl — fill layer implementation of the MapVertexModify hook.
//
// MapVertexModify implements the S13 fill-translate offset, applied in the surface TANGENT PLANE
// (built from the mesh normal + tangent — no flat-ground assumption) by _FillTranslate.xy East/North
// pixels (px→world applied CPU-side; the viewport/clip-space anchor is a future path).
//
// Pattern: every layer has a vertex-modify file that defines MapVertexModify.
//   Line (S33)              → extrudes laterally along extrudeN (done in its own pass body).
//   Fill-extrusion (future) → will offset +Y by height attribute.
//
// Must be #included AFTER Fill_LitInput.hlsl (which declares the CBUFFER props
// used by the body below). Fill.shader includes Fill_LitInput.hlsl → Fill_VertexModify.hlsl
// → Fill_<Pass>.hlsl in that define-before-use order.
//
// fill-translate semantics (MapLibre Style Spec):
//   _FillTranslate.xy   = offset in pixels (screen pixels at current zoom).
//   _FillTranslateAnchor = 0 → "map": translate in world XZ (East/North) plane.
//                        = 1 → "viewport": translate in clip-space (screen-aligned).
//
// World-space (anchor=0): _FillTranslate.xy is treated as world units directly.
//   At zoom 0, 1 tile-unit ≈ world unit; callers must convert px→world before
//   writing _FillTranslate (the CPU-side bootstrap applies the current scale).
//
// Viewport-space (anchor=1): translation is applied in clip space after projection.
//   Here we apply it to positionOS as an approximation (full clip-space shift
//   requires post-projection access; this is acceptable for map fills which are
//   flat in XZ with an identity-ish transform). The full clip-space path is a
//   follow-up for perspective cameras.
// ============================================================================

// Translate in the surface tangent plane — east/north come from the mesh frame, not a hardcoded XZ
// axis. For Mercator (normal=+Y, tangent=+X) east=+X and north=+Z, so this is byte-identical to the
// old XZ offset; a globe projection bakes a radial normal + ENU tangent and the same code follows it.
void MapVertexModify(inout float3 positionOS, float3 normalOS, float4 tangentOS)
{
    float3 up    = normalize(normalOS);
    float3 east  = normalize(tangentOS.xyz);
    float3 north = cross(east, up);   // unit (east is perpendicular to up)
    positionOS += east * _FillTranslate.x + north * _FillTranslate.y;
}

#endif // MAP_FILL_VERTEX_MODIFY_INCLUDED
