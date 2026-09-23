// SymbolWorldPitchAlign.hlsl — the map-pitched symbol vertex position.
//
// THE MODEL (settled by the maintainer; do not relitigate). `text-size` under `*-pitch-alignment: map` means
// X px TOP-DOWN. It is the same principle as `line-width`: a WORLD size is fixed once, and the perspective
// divide does the rest. A map-pitched label is welded to the ground — letters AND letter spacing foreshorten
// together, like paint on the road. The user-facing statement of the acceptance criterion: "a billboard
// laying on the ground — when you move away it gets harder to read, but it won't make the letters move
// closer to each other."
//
// THIS FILE QUERIES NO RULER, AND MUST NOT. `offsetMetres` arrives ALREADY in metres from the CPU. It is
// `SymbolStagingMath.StageCurved`'s own `arcScale` that put it there — the very same factor that spaced the
// glyph ANCHORS along the world polyline. So spacing and size come from ONE constant, and their ratio
// is the purely typographic `ΔArcCenter / cellWidthBaked`: every scale cancels, at every depth. That is why
// no per-anchor pixel ruler is needed here — the two quantities being compared were put on the same ruler
// BEFORE projection, so the projection is allowed to be nonlinear.
//
//   ⚠ THE PROHIBITION. A px→metre ruler derived inside this shader breaks the model above. Nothing in this
//   file may read `_MapFrameMetersPerDevicePixel`, read `UNITY_MATRIX_P._m11`, read `clip.w` as a size ruler,
//   or introduce any global/uniform converting pixels to metres. If an edit here seems to need "how many
//   metres is a pixel at this vertex", the edit is wrong: the offsets are already metres and the
//   perspective divide is the entire ruler.
//
// Shared by BOTH symbol world passes (Text/ and Icon/) — two consumers, which is the threshold at which
// `docs/lessons-learned.md`'s shader-layer self-containment rule permits sharing. It lives directly under
// Map/Symbol/ (the parent of Text/ and Icon/), which ShaderStructureTests' loose-file rules allow: they
// forbid loose files at the Shaders/ ROOT and non-template files in Common/, neither of which this is.
//
// Include order: both .shader files include their Symbol*_Input.hlsl (which pulls in URP Core.hlsl) BEFORE
// the forward-pass file that includes this one, so TransformObjectToWorld / TransformObjectToWorldDir /
// TransformWorldToHClip / UNITY_MATRIX_V / _ProjectionParams all resolve here.

#ifndef MAP_SYMBOL_WORLD_PITCH_ALIGN_INCLUDED
#define MAP_SYMBOL_WORLD_PITCH_ALIGN_INCLUDED

// The screen-Y sense of the ground frame's ŷ.
//
// ⚠ MEASURED, NOT DERIVED — and it must stay that way. `WorldBillboardVertex.Offset` reaches this shader
// in a y-DOWN frame (the Y negation in BillboardMath.BuildWorldQuad), and BillboardMath's own doc states
// outright that this convention must NOT be re-derived on paper. So which of ±cross(upWS, x̂) is "downward
// on screen" is settled by rendering both values on one tree: the one whose tilt-0 render matches the
// VIEWPORT arm is the one kept. The viewport arm is the reference because it shares no code
// with this branch — SymbolWorldIsMapPitched sends the two down mutually exclusive paths.
//
// THE READINGS (MapPitchedGlyphSizeTiltZeroTests: tilt 0, road angles 0°/45°/90°, DPR 1 and 2):
//
//   +1.0 → ink COUNT ratio 1.00000 in all six cells, but ink CENTROID off by 22.56 px (DPR 1) / 44.73 px
//          (DPR 2) in EVERY cell — a pure translation PERPENDICULAR to the road, the same magnitude at
//          every angle. That is the signature of a mirrored ŷ acting on a cell whose ink is not centred on
//          its anchor: the flip moves the ink from +c to −c, i.e. by 2c, and leaves the AREA untouched.
//          The count clause cannot see it — a mirror is an isometry.
//   −1.0 → ink COUNT ratio 1.00000 AND ink centroid delta 0.000 px in all six cells. The two arms render
//          PIXEL-IDENTICALLY, which is exactly what the tilt-0 calibration predicts.
//
// The tests therefore discriminate by ~22× against their 1.0 px bound; this is not a marginal call.
#define SYMBOL_WORLD_MAP_Y_SIGN (-1.0)

// bit2 of the float-encoded AlignFlags word — WorldSymbolRenderer's MapPitchAlignFlag (4). bit0 is the
// map-bearing flag, bit1 is along-line, so the only reachable words are {0, 2, 6}.
//
// Written as a REAL bit test rather than a `>= 3.5` magnitude test, which a future bit3 would silently
// break. It is EXACT for every flag word this field can hold: the word is a small non-negative integer, so
// it is represented without error in a float32; `* 0.25` is a power-of-two scale and therefore exact;
// `floor` of an exact value is exact; and `fmod` of two exact small integers is exact. No epsilon is
// involved in the arithmetic — the `>= 0.5` only converts an exact 0-or-1 into a bool.
bool SymbolWorldIsMapPitched(float alignFlags)
{
    return fmod(floor(alignFlags * 0.25), 2.0) >= 0.5;
}

// The glyph's tangent frame, in WORLD space. Returns false (with x̂/ŷ zeroed) if the inputs cannot define a
// ground plane, in which case the caller takes the camera-facing METRE fallback below.
//
// WORLD space, not object space: TransformObjectToWorldDir normalizes, so the frame carries no
// dependence on the object transform's scale, and render space is metres by the same definition the anchor
// spacing relies on (PolylineArcMath.BuildCumulativeWorld's `total` is metres in this space).
bool SymbolWorldGroundFrame(float3 tangentOS, float3 upOS, out float3 xh, out float3 yh)
{
    xh = float3(0.0, 0.0, 0.0);
    yh = float3(0.0, 0.0, 0.0);

    // Guard BEFORE any normalize, both operands. TransformObjectToWorldDir's normalizing overload is
    // normalize(mul(M, v)), and normalize(0) is NaN — one unguarded normalize is enough to break NaN-safety.
    // Both inputs are unit vectors when they are meaningful (SymbolStagingMath writes a unit tangent and a
    // unit surface normal), so `< 0.5` cleanly separates "unit" from the
    // float3(0,0,0) that ~10 older fixtures — and SymbolTileBlockBaker / the parity oracle whenever
    // PathUpRender is null — still write for Up.
    if (dot(upOS, upOS) < 0.5 || dot(tangentOS, tangentOS) < 0.5) return false;

    float3 upWS = TransformObjectToWorldDir(upOS);
    float3 tWS  = TransformObjectToWorldDir(tangentOS);

    // A road pointing along the surface normal has no in-surface direction to be tangent to.
    float axial = dot(tWS, upWS);
    if (abs(axial) > 1.0 - 1e-3) return false;

    // Gram-Schmidt — keeps x̂ IN the surface. `1 - axial²  >=  2e-3` after the guard above, so the vector
    // being normalized has length >= ~0.045 and this normalize needs no second guard of its own.
    //
    // LIMITATION: this projection is INERT on every Mercator fixture in the suite — upWS is the constant
    // (0,1,0) and every baked road tangent is horizontal, so `axial == 0` exactly and `xh == tWS`. Its
    // stated purpose only exists on a globe (spherical is this renderer's default projection). No test
    // discriminates the operand order here; closing that needs a spherical curved-map fixture.
    xh = normalize(tWS - upWS * axial);

    // Why `_ProjectionParams.x` — DERIVED, not inherited. The viewport branch adds its offset to clip.xy
    // AFTER projection, so its on-screen sense is independent of the projection matrix. This branch displaces
    // BEFORE projection, so a world step d·ŷ reaches clip.y as d·(P·ŷ)_y — and URP negates the projection's
    // Y row when rendering to a flipped target, publishing exactly that flip as _ProjectionParams.x (+1
    // unflipped, −1 flipped). Carrying the runtime value here is what makes "green offscreen, mirrored on
    // screen" structurally impossible; a hard-coded −1 would be a bet on the current render target.
    yh = SYMBOL_WORLD_MAP_Y_SIGN * _ProjectionParams.x * cross(upWS, xh);
    return true;
}

// The whole map-pitched vertex position: displace the corner by `offsetMetres` in the ground plane at the
// anchor, then reproject.
float4 SymbolWorldMapPitchClip(float3 anchorOS, float3 tangentOS, float3 upOS, float2 offsetMetres)
{
    float3 posWS = TransformObjectToWorld(anchorOS);

    float3 xh, yh;
    if (!SymbolWorldGroundFrame(tangentOS, upOS, xh, yh))
    {
        // THE DEGENERATE FALLBACK IS A CAMERA-FACING METRE FRAME, NOT THE VIEWPORT BRANCH. Once `off` is in
        // metres, "fall back to viewport" would hand a metre magnitude to a pixel formula and draw the glyph
        // at ~mpp× its correct size — a spectacular failure, not a graceful one. Keeping the displacement in
        // metres means the glyph keeps its correct WORLD size and merely stops lying in the ground plane.
        // Silent degradation is the right failure mode for a vertex stage, which has no channel to report.
        //
        // It is the SAME formula as the ground branch, with the CAMERA-FACING normal substituted for the
        // surface one — not a second, separately-invented convention. UNITY_MATRIX_V is world→view, so row 0
        // is the world direction mapping to view +x (camera right) and row 2 the one mapping to view +z,
        // i.e. the direction pointing BACK at the camera — the normal of the camera plane, exactly what
        // `up` means to the expression below.
        //
        //   ⚠ DO NOT "simplify" ŷ to ±UNITY_MATRIX_V[1].xyz. The guess cross(V[2], V[0]) == V[1] is WRONG:
        //   Unity's world basis is left-handed under the standard cross product (right × up == forward),
        //   so for (V[0], V[1], V[2]) == (right, up, −forward) the product is cross(V[2], V[0]) == −V[1].
        //   A paper reading misses this; MEASUREMENT does not — at tilt 0 the guess gives the fallback the
        //   OPPOSITE screen sense to the ground frame, so two MapPitchedGlyphSizeTiltZeroTests cannot both
        //   pass: MapPitchedWithDegenerateUp_AtTiltZero_TakesTheMetreFallback_InkCentroid and
        //   MapPitched_AtTiltZero_MatchesViewport_InkCentroid. Writing the cross product out keeps the two
        //   branches provably continuous (at tilt 0 the surface normal IS V[2].xyz, so the two expressions
        //   are then the same number) and needs no handedness claim at all.
        xh = UNITY_MATRIX_V[0].xyz;
        yh = SYMBOL_WORLD_MAP_Y_SIGN * _ProjectionParams.x * cross(UNITY_MATRIX_V[2].xyz, xh);
    }

    posWS += offsetMetres.x * xh + offsetMetres.y * yh;
    return TransformWorldToHClip(posWS);
}

#endif // MAP_SYMBOL_WORLD_PITCH_ALIGN_INCLUDED
