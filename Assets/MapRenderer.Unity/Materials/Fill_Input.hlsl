#ifndef MAP_FILL_INPUT_INCLUDED
#define MAP_FILL_INPUT_INCLUDED

// ============================================================================
// Fill_Input.hlsl — fill layer implementation of the MapVertexModify hook.
//
// Fill geometry is flat on the XZ plane; no lateral extrusion or height offset
// is needed in the vertex shader. MapVertexModify is a documented no-op here.
//
// Pattern: every layer has a paired *_Input.hlsl that implements MapVertexModify
// (and any layer-specific fragment helpers). This is the fill entry.
//   Line (S33)          → will extrude laterally along extrudeN.
//   Fill-extrusion (future) → will offset +Y by height attribute.
//
// Must be #included AFTER MapLitCore.hlsl (which declares MapVertexModify).
// ============================================================================

// Fill: no vertex modification needed — geometry is already at correct world position.
// positionOS is object-space; for this bootstrap the transform is identity so
// object == world; the hook exists for future tile transforms / floating origin.
void MapVertexModify(inout float3 positionOS)
{
    // No-op for fills: no extrusion, no height offset.
    // Future: apply height attribute for fill-extrusion layers.
}

#endif // MAP_FILL_INPUT_INCLUDED
