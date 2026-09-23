using Unity.Mathematics;

namespace MapRenderer.Core.Style.Line
{
    /// <summary>
    /// <c>line-offset</c> — engine-free CPU helpers for the perpendicular ribbon shift. The vertex shader
    /// extrudes each vertex along its extrusion normal by ±½·widthM. The offset adds <c>offsetM</c> along the
    /// same normal times the vertex's side (±1), so both vertices of a station move by one world vector and the
    /// half-width is unchanged: <c>displacement = normal × side × offsetM</c>. HLSL mirror:
    /// <c>Line_VertexExtrude.hlsl</c>; keep both in sync on any arithmetic change.
    /// <para>Two limitations. A round-join fan has one side sign per half-fan, so <see cref="Displace"/> shifts
    /// it non-uniformly; the shift stays bounded and NaN-free (the fan pivot has normal 0). On a sharp corner
    /// the raw miter factor (1/cos(θ/2)) → ∞ as θ → 180°, but <c>line-miter-limit</c> (default 2) bounds the
    /// displacement at <c>miterLimit × |offsetM|</c>. No geometric offset solver exists.</para>
    /// </summary>
    public static class LineOffset
    {
        // ── Core displacement helper ──────────────────────────────────────────────────────────
        // normal = the 2D extrusion normal (LineRibbonVertex.Across); |normal| already carries the miter factor,
        // so with Across (not the re-normalised unitDir_WS) this matches the HLSL `unitDir_WS * side * miter` term.

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
        // Mirrors the width px→m conversion in Line_VertexExtrude.hlsl, where `pxToWorld` is a frame constant
        // (1 for a world-unit width). Offset and width share it, so offsetM(z1)/offsetM(z2) == widthM(z1)/widthM(z2).

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
