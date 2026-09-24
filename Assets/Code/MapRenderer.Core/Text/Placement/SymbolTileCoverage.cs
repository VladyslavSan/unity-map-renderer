// TOP-LEVEL `using Unity.Mathematics;` + unqualified types (see SymbolScreenProjection's header for the
// inline-qualification trap this avoids).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The per-TILE screen-coverage pre-cull metric, a companion to the per-symbol distance cull
    /// (<see cref="SymbolFarPlaneCull"/>): a tile covering a sliver of the screen (the tilted horizon) loses most
    /// symbols to collision anyway, so skipping it is cheap. It runs per frame over the tile's 4 stored
    /// render-space corners, projected through the same <see cref="SymbolScreenProjection.TryProjectPoint"/> as
    /// the anchors, so it is globe-correct.
    /// </summary>
    public static class SymbolTileCoverage
    {
        /// <summary>
        /// The fraction of the viewport a tile covers this frame: the shoelace area of its 4 projected corners
        /// (<paramref name="corner0"/>..<paramref name="corner3"/>, ring order) over the viewport area. Returns
        /// <see cref="double.PositiveInfinity"/> ("never cull") when a corner is behind the camera, where the
        /// projected quad is meaningless, or when the viewport is degenerate.
        /// </summary>
        public static double ScreenCoverage(
            in double3 corner0, in double3 corner1, in double3 corner2, in double3 corner3,
            in double3 sceneOriginRender, in float4x4 viewProj, in double2 viewportLogicalPx, in float3x3 rebase)
        {
            if (!SymbolScreenProjection.TryProjectPoint(corner0, sceneOriginRender, viewProj, viewportLogicalPx, rebase, out float2 p0, out _) ||
                !SymbolScreenProjection.TryProjectPoint(corner1, sceneOriginRender, viewProj, viewportLogicalPx, rebase, out float2 p1, out _) ||
                !SymbolScreenProjection.TryProjectPoint(corner2, sceneOriginRender, viewProj, viewportLogicalPx, rebase, out float2 p2, out _) ||
                !SymbolScreenProjection.TryProjectPoint(corner3, sceneOriginRender, viewProj, viewportLogicalPx, rebase, out float2 p3, out _))
                return double.PositiveInfinity; // any corner behind the near plane → treat as visible

            double viewportArea = viewportLogicalPx.x * viewportLogicalPx.y;
            if (viewportArea <= 0.0) return double.PositiveInfinity; // degenerate viewport → cull nothing

            return TwiceQuadArea(p0, p1, p2, p3) / (2.0 * viewportArea);
        }

        /// <summary>
        /// True when <paramref name="coverage"/> is below <paramref name="minCoverage"/> and the tile should be
        /// skipped. A non-positive threshold DISABLES the cull (mirrors <see cref="SymbolFarPlaneCull"/>'s
        /// non-positive-distance fallback), so a mis-wired caller degrades to "cull nothing" rather than culling
        /// everything; a <see cref="double.PositiveInfinity"/> coverage (behind-camera / degenerate viewport) is
        /// never below a finite threshold, so it is never culled.
        /// </summary>
        public static bool IsCulled(double coverage, double minCoverage)
            => minCoverage > 0.0 && coverage < minCoverage;

        // |signed shoelace| of the ring p0→p1→p2→p3: twice the area of the convex tile quad, in any winding.
        // Accumulates in double so a large off-screen quad keeps its sign.
        private static double TwiceQuadArea(in float2 p0, in float2 p1, in float2 p2, in float2 p3)
        {
            double s = (double)p0.x * p1.y - (double)p1.x * p0.y
                     + (double)p1.x * p2.y - (double)p2.x * p1.y
                     + (double)p2.x * p3.y - (double)p3.x * p2.y
                     + (double)p3.x * p0.y - (double)p0.x * p3.y;
            return math.abs(s);
        }
    }
}
