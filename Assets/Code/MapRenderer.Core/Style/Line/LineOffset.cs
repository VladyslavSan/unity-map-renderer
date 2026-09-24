using Unity.Mathematics;

namespace MapRenderer.Core.Style.Line
{
    /// <summary>
    /// <c>line-offset</c> CPU helpers for the perpendicular ribbon shift: <c>displacement = normal × side ×
    /// offsetM</c>, so both vertices of a station move by one world vector and the half-width is unchanged.
    /// Non-local invariant: <c>Line_VertexExtrude.hlsl</c> mirrors this arithmetic. Limitation: a round-join
    /// fan shifts non-uniformly (bounded, NaN-free), and a sharp corner's displacement is bounded only by
    /// <c>line-miter-limit</c> at <c>miterLimit × |offsetM|</c>; there is no geometric offset solver.
    /// </summary>
    public static class LineOffset
    {
        // ── Core displacement helper ──────────────────────────────────────────────────────────
        // Non-local invariant: normal is LineRibbonVertex.Across, whose length carries the miter factor, so this
        // matches the HLSL `unitDir_WS * side * miter` term.

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
        // Non-local invariant: mirrors the width px→m conversion in Line_VertexExtrude.hlsl (frame-constant
        // `pxToWorld`), so offsetM(z1)/offsetM(z2) == widthM(z1)/widthM(z2).

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
