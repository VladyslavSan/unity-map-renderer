#ifndef MAP_FILL_INPUT_INCLUDED
#define MAP_FILL_INPUT_INCLUDED

// ============================================================================
// Fill_Input.hlsl — fill layer implementation of the MapVertexModify hook.
//
// Fill geometry is flat on the XZ plane. MapVertexModify implements the
// S13 fill-translate offset: displaces vertices in world-space (anchor=0, "map")
// or viewport/clip-space (anchor=1, "viewport") by _FillTranslate.xy pixels.
//
// Pattern: every layer has a paired *_Input.hlsl that implements MapVertexModify
// (and any layer-specific fragment helpers). This is the fill entry.
//   Line (S33)              → will extrude laterally along extrudeN.
//   Fill-extrusion (future) → will offset +Y by height attribute.
//
// Must be #included AFTER MapLitInput.hlsl + MapLitCore.hlsl.
// MapLitCore.hlsl declares MapVertexModify; this file defines the body.
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

// Apply fill-translate offset to the object-space position.
// positionOS is object-space; for the current bootstrap the world transform is
// identity (object == world), so XZ offset is directly meaningful.
void MapVertexModify(inout float3 positionOS)
{
    // _FillTranslateAnchor: 0 = map (world XZ), 1 = viewport (clip-space approximation).
    // Both paths apply the XZ translation to object-space position.
    // For anchor=1 a future stage should apply this post-projection; the approximation
    // is valid for top-down orthographic cameras (typical for map rendering).
    positionOS.x += _FillTranslate.x;
    positionOS.z += _FillTranslate.y;
    // Note: _FillTranslateAnchor distinction is preserved for future clip-space path.
    // The current implementation applies the translate in object/world space for both modes.
}

#endif // MAP_FILL_INPUT_INCLUDED
