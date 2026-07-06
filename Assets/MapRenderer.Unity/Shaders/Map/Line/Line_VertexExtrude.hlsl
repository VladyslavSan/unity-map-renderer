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
//   _Width, _WidthIsPixels, _GapWidth, _LineOffset, _LineTranslate).
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

    // ── S104: width & pixel-based line props resolved in SCREEN space — NO _MetersPerPixel uniform ─────────
    // For pixel widths we MEASURE the local world-metres-per-screen-pixel along the across direction (the
    // px→world scale — foreshortening-correct at any latitude/tilt/projection) instead of reading a per-frame
    // CPU uniform. width, gap, line-offset and line-translate all convert through it.

    // World-space frame. NORMALIZE strips parent scale (the S05 fix) so extrusion is scale-invariant.
    float3x3 objectToWorld = (float3x3)GetObjectToWorldMatrix();
    float3 unitDir_WS = normalize(mul(objectToWorld, unitDir_OS));
    // Per-vertex surface up from the mesh NORMAL stream (+Y for Mercator, radial for a globe) — no
    // flat-ground assumption.
    float3 upWS = normalize(TransformObjectToWorldNormal(input.normalOS));
    float3 centerWS = TransformObjectToWorld(input.positionOS.xyz);

    // px→world scale. Non-pixel widths are already world metres (×1). Pixel widths measure it:
    float pxToWorld = 1.0;
    if (_WidthIsPixels > 0.5)
    {
        float4 clipCenter = TransformWorldToHClip(centerWS);
        // A reference world length that projects to ~2% of NDC height (a few device px) at THIS depth under
        // ANY projection: worldPerNdcY = |clip.w| / P[1][1] (perspective ⇒ depth-scaled; ortho ⇒ constant).
        // Then MEASURE its actual on-screen size along `across`, so foreshortening (grazing tiles) is included.
        float projY  = max(abs(UNITY_MATRIX_P._m11), 1e-6);
        float refMag = (abs(clipCenter.w) / projY) * 0.02;
        float4 clipRef = TransformWorldToHClip(centerWS + unitDir_WS * refMag);
        float refPx = 0.01 * _ScreenParams.y;   // fallback (~the un-foreshortened target) if ref is behind camera
        if (clipCenter.w > 1e-5 && clipRef.w > 1e-5)
        {
            float2 ndcDelta = (clipRef.xy / clipRef.w) - (clipCenter.xy / clipCenter.w);
            refPx = length(ndcDelta * 0.5 * _ScreenParams.xy);
        }
        // Clamp measured px so an edge-on `across` (refPx → 0) can't send pxToWorld to infinity (guard).
        pxToWorld = refMag / max(refPx, 0.1);
    }

    // Width / gap / outer radius in world metres (widthScale = per-feature; gap is layer-level).
    float widthWorld = _Width * input.widthScale * pxToWorld;
    float gapWorld   = _GapWidth * pxToWorld;
    float outerWorld = (gapWorld > 1e-6) ? (0.5 * gapWorld + widthWorld) : (0.5 * widthWorld);

    // Min-width floor (pixel widths only): half-width never below 0.5 px ⇒ a stable 1 px hairline.
    // This is the sole thin-line safeguard now that edge AA is removed — the geometry IS the styled width
    // and LineCoverage draws it with a hard edge, so a sub-pixel line would vanish without this floor.
    float minHalfWorld = (_WidthIsPixels > 0.5) ? (0.5 * pxToWorld) : 0.0;
    float3 lateralWS = unitDir_WS * max(miter * outerWorld, minHalfWorld);
    float3 offsetWS  = lateralWS;

    // ── S44: line-offset ──────────────────────────────────────────────────────
    // Shift the band centre perpendicular to the centerline. ×sideAndDist.x so both station vertices shift by
    // the same world vector. Layer-level (not per-feature). CPU mirror: LineOffset (Core/Style/LineOffset.cs).
    offsetWS += unitDir_WS * input.sideAndDist.x * (miter * _LineOffset * pxToWorld);

    // ── Surface-normal lift (0.001 world-meters) ─────────────────────────────
    // Lift along the per-vertex surface up (NOT world +Y) to avoid coplanar z-fighting with fills under
    // any projection. Applied in every pass via this single helper — the silhouette single-site guarantee.
    offsetWS += upWS * 0.001;

    // ── S14: line-translate ── pixel offset in world XZ. Off-axis, so pxToWorld is an approximation here (as
    // the old _MetersPerPixel path was); layer-level, not per-feature.
    offsetWS += float3(_LineTranslate.x * pxToWorld, 0.0, _LineTranslate.y * pxToWorld);

    // ── Round-trip to object space ────────────────────────────────────────────
    // Add world-space offset back to object-space position so GetVertexPositionInputs /
    // TransformObjectToHClip can work normally downstream.
    float3x3 worldToObject = (float3x3)GetWorldToObjectMatrix();
    float3 posOS = input.positionOS.xyz + mul(worldToObject, offsetWS);

    // ── S14: innerFrac for gap-width fragment clipping ────────────────────────
    // innerFrac = fraction of [0,outerWorld] that is the inner (gap) hole, in [side]-space.
    // When gap=0, innerFrac=0 → no clipping in fragment (solid line path, unchanged).
    // Inner hole: |side| < innerFrac (in normalized side-space). |side| spans the styled width now (no
    // outset pad), so the raw gap/outer ratio is already in the right |side|-space.
    innerFrac = (gapWorld > 1e-6) ? (0.5 * gapWorld / outerWorld) : 0.0;

    // ── Out parameters ────────────────────────────────────────────────────────
    side  = input.sideAndDist.x;  // ∈ {+1,−1}, interpolated for AA
    // S43: dashU = distanceAlong / widthWorld — dimensionless position in line-width units.
    // Mirror of LineDash.DashCoverage's "u = distanceAlong / widthWorld" (CPU D1 formula).
    dashU = (widthWorld > 1e-6) ? (input.sideAndDist.y / widthWorld) : 0.0;

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
// Computes the ribbon alpha from the three interpolated coverage inputs: a HARD outer edge, a hard gap-hole
// cut, opt-in line-blur, and dash coverage. Used as the alpha multiplier in the forward pass and as the
// binary clip threshold (clip(LineCoverage(...) - 0.5)) in every depth-writing pass.
//
// FRAGMENT-STAGE function: uses fwidth(dashU) (and fwidth(side) only when _Blur > 0). The three inputs MUST
// be interpolated Varyings in every pass that calls this (not by-value constants).
float LineCoverage(float side, float innerFrac, float dashU)
{
    // ── Outer + inner edges: HARD edges + opt-in line-blur (AA removed) ─────────────────────────────────────
    // Edge antialiasing was removed: the styled edge is the HARD triangle silhouette (aliased). Thin lines
    // stay visible via the vertex MIN-WIDTH FLOOR (half-width ≥ 0.5 px), not an AA feather buffer. `_Blur`
    // (MapLibre line-blur) is a SEPARATE, opt-in soft edge (default 0 ⇒ hard) — it is NOT antialiasing, so
    // it is kept. When AA returns it will be a different mechanism (single-pass cased-line compositing).
    float absSide = abs(side);

    // Outer edge: the ribbon spans |side| ≤ 1, so coverage is solid up to the rasterized silhouette.
    float coverage = 1.0;

    // S14 inner edge (gap hole): HARD cut — drop the inner |side| < innerFrac region for cased/hollow lines.
    if (innerFrac > 1e-6)
        coverage *= step(innerFrac, absSide);

    // line-blur (MapLibre line-blur; opt-in soft edge): feathers the outer _Blur px inward. 0 ⇒ no-op (hard).
    if (_Blur > 1e-6)
    {
        float feather = max(fwidth(side), 1e-6);
        coverage *= smoothstep(0.0, feather * _Blur, 1.0 - absSide);
    }

    // ── S43: dash coverage ────────────────────────────────────────────────────
    // _DashCount == 0: identity guard (solid line, no dashing). Byte-identical to pre-S43 path.
    // _DashCount >= 2: walk on/off runs (even index=on, odd=off); AA-feather transitions with fwidth.
    //
    // dashU = distanceAlong / widthWorld (set in vertex, interpolated — NOT a constant).
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
