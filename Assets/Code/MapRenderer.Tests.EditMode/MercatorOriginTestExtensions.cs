using Unity.Mathematics;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Test-only Mercator origin conversion. The S91-C backend/builder API takes a <c>double3</c> render
    /// origin (the tile's projected SW corner); for the planar Web-Mercator tests that is
    /// <c>(mercX, 0, mercZ)</c>. This lets the many backend/renderer tests keep expressing a Mercator origin
    /// as a <c>double2</c> and convert explicitly at the call site — a named, obvious adapter rather than an
    /// inline <c>new double3(o.x, 0, o.y)</c> repeated per site — while the production API stays double3-only.
    /// </summary>
    internal static class MercatorOriginTestExtensions
    {
        /// <summary>The double3 render origin for a Web-Mercator SW corner: <c>(mercX, 0, mercZ)</c>.</summary>
        public static double3 ToRenderOrigin(this double2 merc) => new double3(merc.x, 0.0, merc.y);
    }
}
