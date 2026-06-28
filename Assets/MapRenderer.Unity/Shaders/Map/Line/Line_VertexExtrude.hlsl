// Line_VertexExtrude.hlsl — bespoke map helper: world-space ribbon extrusion + coverage.
//
// This is NOT a <Layer>_<OriginalUnityName> file — it is a map-specific helper named for its
// function, following the Fill_VertexModify.hlsl precedent. URP exposes no vertex hook that
// accommodates the line's per-vertex coverage inputs (side / innerFrac / dashU), so this helper
// owns all of: the LineAttributes struct, the extrusion function, and the LineCoverage formula.
//
// Every line pass (ForwardLit, ShadowCaster, DepthOnly, DepthNormals, GBuffer) includes this file.
// Include order: Line_LitInput.hlsl → Line_VertexExtrude.hlsl → Line_<Pass>.hlsl.
//
// Line_LitInput.hlsl must be included BEFORE this file (reads CBUFFER props:
//   _Width, _WidthIsPixels, _MetersPerPixel, _GapWidth, _LineOffset, _LineTranslate).
//
// See docs/lit-rendering-design.md §"Line specifics (S33)" for the extrusion rationale.
// Authored for URP 17.5 / Unity 6000.x. Clean-room map logic, not MapLibre or Unity source.

#ifndef MAP_LINE_VERTEX_EXTRUDE_INCLUDED
#define MAP_LINE_VERTEX_EXTRUDE_INCLUDED

// ── Vertex attributes ─────────────────────────────────────────────────────────
// IMPORTANT: TEXCOORD0/1/2 carry line-specific data (NOT uv/lightmapUV/dynamicLightmapUV
// as in Fill_LitForwardPass.hlsl Attributes). This is why we cannot reuse that struct.
// All five line passes consume this byte-identical input — silhouette guarantee across passes.
//
// S14: COLOR attribute added for per-feature data-driven color (baked by StyledLineTileBuilder).
//      Default white = identity multiply when no data-driven color is set.
struct LineAttributes
{
    float4 positionOS   : POSITION;
    float3 normalOS     : NORMAL;     // constant +Y lighting normal (stream 1)
    float3 extrudeN     : TEXCOORD0;  // 3D across-direction (tangent-plane; Y=0 Mercator); miter factor in |n|
    float2 sideAndDist  : TEXCOORD1;  // (side ∈ {+1,−1}, distanceAlong)
    float  widthScale   : TEXCOORD2;  // per-feature width scale (default=1)
    float4 color        : COLOR;      // S14: per-vertex baked color (data-driven); white=identity
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// ── Line_VertexExtrude ────────────────────────────────────────────────────────
// Performs the S05 world-space extrusion. Called by the vertex entry point of EVERY line pass
// so all five passes (ForwardLit, ShadowCaster, DepthOnly, DepthNormals, GBuffer) share exactly
// one extrusion site — silhouette divergence is impossible by construction.
//
// Returns: extruded OBJECT-SPACE position (ready for GetVertexPositionInputs or TransformObjectToHClip).
// Out params: the three per-vertex coverage inputs that must be interpolated across every pass's
//             Varyings (required by LineCoverage, which uses fwidth — a fragment-stage function).
//
// Signature intentionally does NOT follow MapVertexModify(inout float3) — the line must emit
// three additional per-vertex outputs that a simple inout-position hook cannot express.
float3 Line_VertexExtrude(
    LineAttributes input,
    out float side,
    out float innerFrac,
    out float dashU,
    out float4 tangentOS)
{
    // ── Miter / unit extrusion direction ─────────────────────────────────────
    // extrudeN: 3D across-direction in the surface tangent plane (Y=0 for the flat Mercator frame;
    // a globe projection bakes a non-zero Y). |extrudeN| = miter factor (>1 for sharp corners).
    float miter = length(input.extrudeN);
    // Avoid div-by-zero on degenerate (cap) vertices.
    float3 unitDir_OS = (miter > 1e-6) ? (input.extrudeN / miter) : float3(0, 0, 0);

    // ── Resolve width in meters ───────────────────────────────────────────────
    // widthScale = per-feature scale, default 1.
    float widthM = (_WidthIsPixels > 0.5)
        ? _Width * _MetersPerPixel
        : _Width;
    widthM *= input.widthScale;

    // ── S14: gap-width in meters ──────────────────────────────────────────────
    // Same unit conversion as width; NOT scaled by widthScale (layer-level property).
    float gapM = (_WidthIsPixels > 0.5)
        ? _GapWidth * _MetersPerPixel
        : _GapWidth;

    // ── Outer extrude radius ──────────────────────────────────────────────────
    // gap=0 → outerM = 0.5*widthM (identical to previous solid path).
    // gap>0 → outerM = 0.5*gapM + widthM (gap half-width + full line width from outer edge).
    float outerM = (gapM > 1e-6) ? (0.5 * gapM + widthM) : (0.5 * widthM);

    // ── Object-space → world-space frame ─────────────────────────────────────
    // NORMALIZE strips parent scale (the decisive S05 fix) so _Width stays invariant in world meters.
    float3x3 O2W = (float3x3)GetObjectToWorldMatrix();
    float3 unitDir_WS = normalize(mul(O2W, unitDir_OS));

    // Per-vertex surface up — from the mesh NORMAL stream, NOT a hardcoded +Y (+Y for Mercator, radial
    // for a globe). The lateral/lift frame is built from this, so the shader makes no flat-ground assumption.
    float3 upWS = normalize(TransformObjectToWorldNormal(input.normalOS));

    // Lateral world offset = unit world across-direction × miter factor × outerM.
    float3 offsetWS = unitDir_WS * (miter * outerM);

    // ── S44: line-offset ──────────────────────────────────────────────────────
    // Shift the band center perpendicular to the centerline by offsetM.
    // Multiplied by sideAndDist.x (∈{+1,−1}) so both vertices of a station shift by the same
    // world vector (the extrusion normal flips between sides, cancelling the flip).
    // NOT scaled by widthScale (layer-level offset, not per-feature).
    //
    // CPU mirror: LineOffset.Displace / LineOffset.OffsetMeters (Assets/MapRenderer.Core/Style/LineOffset.cs).
    float offsetM = (_WidthIsPixels > 0.5) ? _LineOffset * _MetersPerPixel : _LineOffset;
    offsetWS += unitDir_WS * input.sideAndDist.x * (miter * offsetM);

    // ── Surface-normal lift (0.001 world-meters) ─────────────────────────────
    // Lift along the per-vertex surface up (NOT world +Y) to avoid coplanar z-fighting with fills under
    // any projection. Applied in every pass via this single helper — the silhouette single-site guarantee.
    offsetWS += upWS * 0.001;

    // ── S14: line-translate ───────────────────────────────────────────────────
    // Shift the ribbon by the specified pixel offset in world XZ.
    // anchor=0 (map): px→world via _MetersPerPixel. Applied in world space as XZ offset.
    // anchor=1 (viewport): approximated; apply same px→world path (documented approximation).
    // NOT scaled by widthScale (layer-level offset, not per-feature).
    float translateScale = _MetersPerPixel;
    float3 translateWS = float3(_LineTranslate.x * translateScale, 0.0, _LineTranslate.y * translateScale);
    offsetWS += translateWS;

    // ── Round-trip to object space ────────────────────────────────────────────
    // Add world-space offset back to object-space position so GetVertexPositionInputs /
    // TransformObjectToHClip can work normally downstream.
    float3x3 W2O = (float3x3)GetWorldToObjectMatrix();
    float3 posOS = input.positionOS.xyz + mul(W2O, offsetWS);

    // ── S14: innerFrac for gap-width fragment clipping ────────────────────────
    // innerFrac = fraction of [0,outerM] that is the inner (gap) hole, in [side]-space.
    // When gap=0, innerFrac=0 → no clipping in fragment (solid line path, unchanged).
    // Inner hole: |side| < innerFrac (in normalized side-space).
    innerFrac = (gapM > 1e-6) ? (0.5 * gapM / outerM) : 0.0;

    // ── Out parameters ────────────────────────────────────────────────────────
    side  = input.sideAndDist.x;  // ∈ {+1,−1}, interpolated for AA
    // S43: dashU = distanceAlong / widthM — dimensionless position in line-width units.
    // Mirror of LineDash.DashCoverage's "u = distanceAlong / widthM" (CPU D1 formula).
    dashU = (widthM > 1e-6) ? (input.sideAndDist.y / widthM) : 0.0;

    // ── Tangent (along the line) — derived, projection-agnostic ───────────────
    // Built from the surface up (normalOS) and the across-direction — no extra vertex stream and no
    // flat-ground assumption. sideAndDist.x keeps it consistent across the ribbon (extrudeN flips per
    // side). Gives the line a real tangent frame (T=along, B=across, N=up) so normal-map/detail/parallax
    // work. w=-1 orients the bitangent toward +across (a 1-bit handedness calibration). Degenerate cap
    // vertices (across≈0) fall back to a stable default.
    float3 alongOS  = cross(input.normalOS, unitDir_OS * input.sideAndDist.x);
    float  alongLen = length(alongOS);
    tangentOS = (alongLen > 1e-6) ? float4(alongOS / alongLen, -1.0) : float4(1.0, 0.0, 0.0, -1.0);

    return posOS;
}

// ── LineCoverage ──────────────────────────────────────────────────────────────
// Computes the fwidth-smoothstep ribbon alpha from the three interpolated coverage inputs.
// Used as the alpha multiplier in the forward pass and as the binary clip threshold
// (clip(LineCoverage(...) - 0.5)) in every depth-writing pass.
//
// FRAGMENT-STAGE function: uses fwidth(side) and fwidth(dashU). The three inputs MUST be
// interpolated Varyings in every pass that calls this (not by-value constants).
//
// This is a pure extraction of the coverage formula from Line_LitForwardPass.hlsl — the
// output is byte-for-byte identical to the inline code it replaces.
float LineCoverage(float side, float innerFrac, float dashU)
{
    // ── Outer and inner AA edges ──────────────────────────────────────────────
    float absSide  = abs(side);
    float feather  = fwidth(side);

    // Outer edge: smoothstep from (|side|=1) inward. Identical to S05 solid formula.
    float outerEdgeDist = 1.0 - absSide;
    float outerAA       = smoothstep(0.0, max(feather * _Blur, 1e-4), outerEdgeDist);

    // S14: inner edge (gap hole): smoothstep from (|side|=innerFrac) outward.
    // When innerFrac=0 (no gap), innerAA=1.0 → coverage = outerAA (unchanged, gap=0 path).
    float innerAA = (innerFrac > 1e-6)
        ? smoothstep(0.0, max(feather * _Blur, 1e-4), absSide - innerFrac)
        : 1.0;

    float coverage = outerAA * innerAA;

    // ── S43: dash coverage ────────────────────────────────────────────────────
    // _DashCount == 0: identity guard (solid line, no dashing). Byte-identical to pre-S43 path.
    // _DashCount >= 2: walk on/off runs (even index=on, odd=off); AA-feather transitions with fwidth.
    //
    // dashU = distanceAlong / widthM (set in vertex, interpolated — NOT a constant).
    // period = sum of all _DashArray entries (in line-width units).
    // phase  = fmod(dashU, period) — position within one dash cycle.
    //
    // CPU mirror: LineDash.DashCoverage (Assets/MapRenderer.Core/Style/LineDash.cs).
    if (_DashCount >= 0.5)
    {
        // Compute period from the active entries only (unused slots are 0, contribute 0).
        float da0 = (_DashCount > 0.5) ? _DashArray.x : 0.0;
        float da1 = (_DashCount > 1.5) ? _DashArray.y : 0.0;
        float da2 = (_DashCount > 2.5) ? _DashArray.z : 0.0;
        float da3 = (_DashCount > 3.5) ? _DashArray.w : 0.0;
        float period = da0 + da1 + da2 + da3;

        if (period > 1e-5)
        {
            float dfw   = max(fwidth(dashU), 1e-5);
            float phase = fmod(dashU, period);
            if (phase < 0.0) phase += period;

            // Walk on/off runs, accumulating the coverage value.
            // Even slots (0,2) = on-run (value=1), odd slots (1,3) = off-run (value=0).
            // At each boundary we smoothstep from the previous run's value to the current.
            float dashCoverage = 1.0;
            float cursor = 0.0;
            float prevVal = 0.0; // last slot is always odd=off for even-count patterns

            float spans[4];
            spans[0] = da0; spans[1] = da1; spans[2] = da2; spans[3] = da3;

            bool found = false;
            [unroll]
            for (int k = 0; k < 4; k++)
            {
                if ((float)k >= _DashCount) break;
                float segEnd = cursor + spans[k];
                float onVal = (k % 2 == 0) ? 1.0 : 0.0;

                // Is phase in this run (including feather overlap from neighbors)?
                if (!found && phase < segEnd + dfw)
                {
                    // Feather at entry edge (transition from prevVal to onVal).
                    float entryBlend = smoothstep(cursor - dfw, cursor + dfw, phase);
                    float val = lerp(prevVal, onVal, entryBlend);

                    // Feather at exit edge (transition from onVal to next run's value).
                    float nextVal = ((k + 1) % 2 == 0) ? 1.0 : 0.0;
                    if ((float)(k + 1) >= _DashCount) nextVal = 1.0; // wrap to slot 0 = on
                    float exitBlend = smoothstep(segEnd - dfw, segEnd + dfw, phase);
                    val = lerp(val, nextVal, exitBlend);

                    dashCoverage = val;
                    found = true;
                }
                prevVal = onVal;
                cursor = segEnd;
            }

            // If phase was not found (e.g. precision edge past all runs), treat as on.
            if (!found) dashCoverage = 1.0;

            coverage *= dashCoverage;
        }
    }

    return coverage;
}

#endif // MAP_LINE_VERTEX_EXTRUDE_INCLUDED
