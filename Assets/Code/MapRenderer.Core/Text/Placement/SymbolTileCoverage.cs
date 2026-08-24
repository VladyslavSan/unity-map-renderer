// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified double3/float2/
// float4x4 — this file lives in MapRenderer.Core.Text.Placement (see SymbolScreenProjection's header for the
// inline-qualification trap this avoids).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The per-TILE screen-coverage pre-cull metric — a coarse companion to the per-symbol distance cull
    /// (<see cref="SymbolFarPlaneCull"/>). A tile that covers only a sliver of the screen (the tilt-foreshortened
    /// horizon pile-up) has most of its symbols collision-discarded anyway, so gathering/projecting/staging them
    /// is wasted work; skipping the whole tile stabilizes per-frame symbol cost with barely any lost information.
    /// Where the distance cull is a per-symbol camera <i>range</i>, this is a per-tile screen <i>area</i>, which catches the
    /// foreshortened slivers a radius keeps.
    ///
    /// <para>The measurement is camera-dependent, so it runs per-frame; only the tile's 4 render-space corners
    /// are camera-independent and stored once per rebuild (on <see cref="SymbolBatch"/>). Corners are
    /// projected through the SAME <see cref="SymbolScreenProjection.TryProjectPoint"/> the symbol anchors use, so
    /// the metric is globe-correct (no flat-earth / Mercator-bounds shortcut).</para>
    /// </summary>
    public static class SymbolTileCoverage
    {
        /// <summary>
        /// The fraction of the viewport a tile covers this frame: project its 4 render-space corners
        /// (<paramref name="corner0"/>..<paramref name="corner3"/>, in ring order) to logical screen pixels,
        /// take the shoelace area of the projected quad, and divide by the viewport area. Returns
        /// <see cref="double.PositiveInfinity"/> ("never cull") when ANY corner is behind the camera — the tile
        /// straddles the near plane, where the projected quad is meaningless and dropping it could hide on-screen
        /// symbols — or when the viewport is degenerate.
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

        // |signed shoelace| for the quad ring p0→p1→p2→p3 — twice the enclosed area (self-intersection aside,
        // irrelevant for a convex tile quad). double accumulation so a large off-screen quad doesn't lose the
        // sign in float. The winding (CW vs CCW) only flips the sign, which the abs discards.
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
