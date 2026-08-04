using Unity.Mathematics;

namespace MapRenderer.Core.Style.Line
{
    /// <summary>
    /// S44: line-offset — engine-free CPU helpers for the perpendicular ribbon shift.
    ///
    /// Design decision D1 (shader-space offset): the vertex shader already extrudes each ribbon
    /// vertex along the per-vertex extrusion normal by ±½·widthM. To shift the band center by
    /// <c>offsetM</c> without changing its thickness, we add <c>offsetM</c> in the SAME normal
    /// direction — but multiplied by the vertex's <em>side</em> value (∈{+1,−1}) so that BOTH
    /// vertices of a station shift by the same world vector:
    ///
    ///     displacement = normal × side × offsetM
    ///
    /// The extrusion normal already encodes direction (it flips between left/right vertices).
    /// Multiplying by side aligns the displacement so both sides move in the same direction —
    /// shifting the band center to offsetM while leaving the half-width unchanged.
    ///
    /// HLSL mirror: MapLineForwardPass.hlsl, "S44 line-offset" block.
    ///   offsetWS += unitDir_WS * input.sideAndDist.x * (miter * offsetM);
    /// Keep both in sync on any arithmetic change.
    ///
    /// Round join/cap limitation: fan vertices have per-vertex varying normals with a single
    /// side sign per half-fan, so <c>Displace</c> with fan normals produces a non-uniform shift.
    /// This is the documented MapLibre-parity limitation for round joins at large offset.
    /// The band-center shift on fans is bounded (fan rim normals stay unit-length; the inner fan
    /// vertex is bounded at miterLimit) and does not produce NaN (the fan pivot has normal=0 →
    /// zero displacement).
    ///
    /// Large-offset sharp-corner limitation: the RAW miter factor (1/cos(θ/2)) → ∞ as θ → 180°,
    /// and the displacement magnitude is <c>miter × offsetM</c>. With an UNBOUNDED limit a tight
    /// hairpin at large offset therefore blows up. Production does not run unbounded: at
    /// <c>MiterLimit = 2</c> the miter join falls back to bevel and the bevel/round concave vertex is
    /// saturated at the limit, so the emitted magnitude is at most <c>2 × |offsetM|</c>. This matches
    /// MapLibre's known limitation; no geometric solver is built.
    ///
    /// Engine-free: no UnityEngine references. Runs in both dotnet core-tests and Unity EditMode.
    /// Clean-room: offset semantics from the public MapLibre Style Spec. No MapLibre source read.
    /// </summary>
    public static class LineOffset
    {
        // ── Core displacement helper ──────────────────────────────────────────────────────────
        //
        // Computes the perpendicular displacement for a single ribbon vertex.
        //
        // Parameters:
        //   normal   — per-vertex extrusion normal (from LineVertex.Normal).
        //              For straight segments / miter joins / the bevel-or-round inner vertex:
        //              |normal| = miter factor (≥1, saturated at miterLimit for the inner vertex).
        //              For unit-normal vertices (bevel/round outer, segment ends): |normal| = 1.
        //   side     — signed side value ∈{+1,−1} (from LineVertex.Side).
        //   offsetM  — perpendicular shift in world meters (same space as the normal).
        //
        // Returns: displacement vector to be ADDED to the vertex's position in mesh build space.
        //
        // Shader mirror:  unitDir_WS * input.sideAndDist.x * (miter * offsetM)
        // CPU equivalent: normal        * side               * offsetM
        //
        // The miter factor is already baked into |normal|, so this mirrors the HLSL exactly
        // when the caller uses LineVertex.Normal (not the re-normalised unitDir_WS).
        // For unit-normal vertices the miter factor is 1 — also identical.

        /// <summary>
        /// Returns the perpendicular world-space displacement for a vertex with the given
        /// extrusion <paramref name="normal"/> and <paramref name="side"/> value.
        /// Result must be ADDED to the vertex's position to apply the offset.
        /// </summary>
        public static double2 Displace(double2 normal, float side, double offsetM)
        {
            // normal × side × offsetM — side-consistent shift (band center moves to offsetM).
            return normal * side * offsetM;
        }

        // ── Zoom-coupled px→m conversion ─────────────────────────────────────────────────────
        //
        // Mirror of the width px→m conversion in Shaders/Map/Line/Line_VertexExtrude.hlsl:
        //   widthWorld = _Width * ((_WidthIsPixels > 0.5) ? pxToWorld : 1)
        //
        // NOTE the asymmetry: on the GPU `pxToWorld` is MEASURED per-vertex through the projection matrix
        // (S104 deleted the _MetersPerPixel uniform this comment used to name), so it varies with depth and
        // direction under tilt. This CPU mirror takes a single zoom-derived scalar, which agrees with the
        // GPU at the view centre and drifts from it toward the edges of a tilted frame. That is fine for
        // what it is used for — the RATIO between two zooms, which is what tooth 3 pins.
        //
        // Tooth 3 requires that offset and width share the same conversion path. Using the same
        // branch here guarantees that offsetM(z1)/offsetM(z2) == widthM(z1)/widthM(z2) at two
        // zooms (both linear in metersPerPixel when widthIsPixels=true, constant otherwise).

        /// <summary>
        /// Converts an offset from its source unit to world meters, mirroring the
        /// width px→m path used in the vertex shader.
        /// </summary>
        /// <param name="offsetPx">The raw offset value from the style (in pixels if <paramref name="widthIsPixels"/>, else meters).</param>
        /// <param name="metersPerPixel">Current px→m scale factor (from MapCamera / MetersPerPixel at the current zoom).</param>
        /// <param name="widthIsPixels">True when units are screen pixels (matches _WidthIsPixels > 0.5).</param>
        /// <returns>Offset in world meters.</returns>
        public static double OffsetMeters(double offsetPx, double metersPerPixel, bool widthIsPixels)
        {
            return widthIsPixels ? offsetPx * metersPerPixel : offsetPx;
        }
    }
}
