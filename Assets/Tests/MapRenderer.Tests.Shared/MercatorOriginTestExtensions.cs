using Unity.Mathematics;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Test-only Mercator origin conversion. The backend/builder API takes a <c>double3</c> render origin
    /// (the tile's projected SW corner); for the planar Web-Mercator tests that is <c>(mercX, 0, mercZ)</c>.
    /// Backend/renderer tests express a Mercator origin as a <c>double2</c> and convert here, so the
    /// production API stays double3-only.
    /// </summary>
    internal static class MercatorOriginTestExtensions
    {
        /// <summary>The double3 render origin for a Web-Mercator SW corner: <c>(mercX, 0, mercZ)</c>.</summary>
        public static double3 ToRenderOrigin(this double2 merc) => new double3(merc.x, 0.0, merc.y);
    }
}
