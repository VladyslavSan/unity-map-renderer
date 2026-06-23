// MapLitCore.hlsl — shared vertex hook and edge-AA helper for map-geometry lit shaders.
//
// S34 redesign: the UnityPerMaterial CBUFFER and DOTS bridge have moved to MapLitInput.hlsl
// (mirroring URP's LitInput.hlsl structure). This file now contains only:
//   • MapVertexModify — per-layer vertex hook declaration; body supplied by per-layer *_Input.hlsl.
//   • MapEdgeAA — fwidth-based edge coverage helper for line AA (S33 carrier).
//
// Usage order in every pass:
//   #include "MapLitInput.hlsl"    ← CBUFFER, DOTS bridge, InitializeStandardLitSurfaceData
//   #include "MapLitCore.hlsl"     ← vertex hook declaration + AA helper
//   #include "<Layer>_Input.hlsl"  ← MapVertexModify body implementation
//   #include "<PassBody>.hlsl"     ← pass vertex/fragment functions
//
// Version note: authored for URP 17.5 / Unity 6000.x.
// Clean-room: this is URP integration, not MapLibre. URP docs/source are fair reference.

#ifndef MAP_LIT_CORE_INCLUDED
#define MAP_LIT_CORE_INCLUDED

// ── MapVertexModify (declared here; body supplied by per-layer *_Input.hlsl) ─
// Every pass calls MapVertexModify(positionOS) BEFORE GetVertexPositionInputs.
// Fill body = no-op. Line body = lateral extrusion.
// Must be defined exactly once (the *_Input.hlsl file provides the definition).
void MapVertexModify(inout float3 positionOS);


// ── MapEdgeAA ────────────────────────────────────────────────────────────────
// fwidth-based edge coverage for sub-pixel AA. Used by lines (S33); here a
// carrier so every layer shader can include this single header.
// edgeSignedDist: signed distance from the feature edge; negative = inside.
// blurUnits: desired feather width in the same units as edgeSignedDist.
// Returns coverage [0,1]; 1 = fully inside, 0 = fully outside.
float MapEdgeAA(float edgeSignedDist, float blurUnits)
{
    float fw = fwidth(edgeSignedDist);
    float halfBlur = max(blurUnits * 0.5, fw);
    return saturate((-edgeSignedDist) / halfBlur + 0.5);
}

#endif // MAP_LIT_CORE_INCLUDED
