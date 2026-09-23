using Unity.Mathematics;

namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// Per-vertex output of the 3D ribbon builder (<c>RibbonJob</c>). The centerline is projected FIRST, so every
    /// field is final render-space data: no downstream reconstruction, tangent frame, or winding flip.
    /// <see cref="Across"/> is <c>normalize(cross(along, up))</c> from the SAME <c>up</c> the centerline was
    /// projected with, so the ribbon front-faces outward for every projection (<c>GlobeLineWindingTests</c>).
    /// <para>Line mesh streams (<c>StyledLineTileBuilder</c>): 0 = Position + <see cref="Up"/> (Normal);
    /// 1 = <see cref="Across"/> (TexCoord0); 2 = (<see cref="Side"/>, <see cref="DistanceAlong"/>);
    /// 3 = color + <see cref="WidthScale"/>.</para>
    /// </summary>
    public struct LineRibbonVertex
    {
        /// <summary>Centerline point in origin-relative render space (the projected, subdivided centerline).</summary>
        public double3 Position;

        /// <summary>
        /// 3D extrusion direction in the surface tangent plane (perpendicular to <see cref="Up"/> and the line).
        /// <b>Magnitude carries the miter factor</b> (1 for straight/terminal, cap-rim, and bevel/round OUTER
        /// vertices; min(1/cos(θ/2), miterLimit) for a miter join and the bevel/round INNER vertex), so the
        /// shader (<c>Line_VertexExtrude.hlsl</c>) applies
        /// <c>lateral = across/|across| · |across| · outerM</c>. Per-side sign is baked in.
        /// </summary>
        public double3 Across;

        /// <summary>Surface up (radial normal) at this centerline point — the lighting Normal and the frame
        /// <see cref="Across"/> is derived against. Constant +Y for the planar Mercator; the geodetic normal on a globe.</summary>
        public double3 Up;

        /// <summary>Cumulative arc length along the (projected) centerline from the first point — dash coordinate.</summary>
        public double DistanceAlong;

        /// <summary>Signed extrude side: +1 left, −1 right, 0 for cap-fan pivots. Fragment-stage AA only (not a
        /// multiplier on <see cref="Across"/>, whose per-side sign is already baked in).</summary>
        public float Side;

        /// <summary>Per-feature width scale (default 1). Multiplies the shader <c>_Width</c>.</summary>
        public float WidthScale;
    }
}
