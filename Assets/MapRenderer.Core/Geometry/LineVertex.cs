using Unity.Mathematics;

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
        /// The fan vertex count is controlled by <c>roundSegments</c> in
        /// <see cref="LineTessellator.Triangulate"/>.
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

    /// <summary>
    /// Per-vertex output of <see cref="LineTessellator"/>.
    ///
    /// Mesh/shader contract (channel layout documented in StyledLineTileBuilder):
    /// <list type="bullet">
    ///   <item><description><see cref="Position"/> — centerline point in the mesh build space (world meters for S05).</description></item>
    ///   <item><description><see cref="Normal"/> — 2D extrusion normal in the same space. For straight segments and bevel/round
    ///     joins the length is 1. For miter joins the length equals the miter factor (1/cos(θ/2)), so the vertex shader
    ///     can uniformly apply: <c>worldPos.xz += normal * 0.5 * widthMeters</c>. IMPORTANT: do NOT pack these as SNORM
    ///     (which is unit-only); store as float2 whose magnitude carries the miter factor.</description></item>
    ///   <item><description><see cref="DistanceAlong"/> — cumulative arc length from the line start (reserved for S14 dash patterns). Set now, consumed later.</description></item>
    ///   <item><description><see cref="Side"/> — signed extrude coordinate: +1 for the left/positive side, −1 for the right/negative side.
    ///     Used by the fragment shader for AA edge feathering; not a multiplier on the normal vector (the normal already encodes direction).</description></item>
    ///   <item><description><see cref="WidthScale"/> — per-feature width multiplier (reserved for S12 data-driven width). Defaults to 1.</description></item>
    /// </list>
    /// </summary>
    public struct LineVertex
    {
        /// <summary>Centerline position in mesh build space (world meters for S05).</summary>
        public double2 Position;

        /// <summary>
        /// 2D extrusion normal in mesh build space. Length = 1 for straight/bevel/round vertices;
        /// length = 1/cos(θ/2) (miter factor) for miter join vertices. The vertex shader applies
        /// <c>worldPos += normal * 0.5 * widthMeters</c> uniformly — the miter factor is baked
        /// into the normal's length, not a separate attribute.
        /// </summary>
        public double2 Normal;

        /// <summary>Cumulative distance along the line from the first point (world-meter units).</summary>
        public double DistanceAlong;

        /// <summary>
        /// Signed extrude side: +1 for the positive/left side, −1 for the negative/right side.
        /// Used in the fragment shader for AA edge feathering.
        /// </summary>
        public float Side;

        /// <summary>Per-feature width scale (reserved for S12). Default = 1.</summary>
        public float WidthScale;
    }
}
