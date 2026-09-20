namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// Join type for polyline corners. Baked at mesh build time — changing join type requires
    /// a mesh rebuild (per ARCHITECTURE §2 styling model).
    /// </summary>
    public enum JoinType
    {
        /// <summary>
        /// Miter join: the two ribbon edges are extended until they meet at a point. The miter
        /// normal length encodes the miter factor (1/cos(θ/2)) so the vertex shader applies a
        /// single formula: worldPos += normal * 0.5 * width.
        /// Falls back to bevel when the miter ratio exceeds <c>miterLimit</c>.
        /// </summary>
        Miter,

        /// <summary>
        /// Bevel join: the corner is clipped flat with an extra triangle.
        /// </summary>
        Bevel,

        /// <summary>
        /// Round join: the corner is filled with a fan of triangles approximating a circular arc.
        /// The fan vertex count is controlled by <c>roundSegments</c> in <c>RibbonJob</c>.
        /// </summary>
        Round,
    }

    /// <summary>
    /// Cap type for polyline endpoints. Baked at mesh build time.
    /// </summary>
    public enum CapType
    {
        /// <summary>Butt cap: the ribbon ends flush with the endpoint (no extension).</summary>
        Butt,

        /// <summary>
        /// Round cap: a semicircular fan extends beyond the endpoint.
        /// Fan vertex count controlled by <c>roundSegments</c>.
        /// </summary>
        Round,

        /// <summary>Square cap: the ribbon extends by half-width beyond the endpoint.</summary>
        Square,
    }
}
