// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified double2/double3 —
// this file lives in MapRenderer.Core.Text.Placement (see LabelScreenProjection's header for the inline-
// qualification trap this avoids).

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// B-3: the PRE-projection horizon / far-distance label cull. In a tilted view the far half of the frustum
    /// compresses a huge ground area into a thin band at the horizon, so most labels there pile up, get
    /// collision-discarded, and jitter (their projection is numerically unstable as depth → the far plane).
    /// Testing a label's ground distance from the look-at BEFORE projecting it lets the placement pass skip its
    /// whole staging (project + collision-box build) — the bulk of the per-frame <c>Symbol.Project</c> cost — for
    /// labels that would be discarded or unstable anyway.
    ///
    /// <para>The cull radius is a few viewport-spans of ground around the look-at, sized by the ground
    /// resolution at the current zoom (render/Mercator metres per logical pixel — the SAME units as the anchor
    /// distances, so no latitude correction is needed). It is deliberately CONSERVATIVE: a top-down view's
    /// on-screen labels sit well within one span, so the radius only ever trims the far horizon band a tilted
    /// view adds; the multiplier is a maintainer tunable (raise to keep more distant labels, lower to cull the
    /// horizon harder). Distance is 3-D render-space length, so it is well-defined for the planar atlas and the
    /// globe alike (globe gets its own tighter occlusion cull separately).</para>
    /// </summary>
    public static class LabelViewDistance
    {
        /// <summary>
        /// The cull radius in render/Mercator metres: <paramref name="viewportSpans"/> × the viewport's larger
        /// logical dimension × <paramref name="groundResolutionMeters"/> (metres per logical pixel at the current
        /// zoom, e.g. <c>CameraPoseMath.MetersPerPixel(zoom)</c>). A non-positive input yields 0 (which
        /// <see cref="IsCulled"/> treats as "cull nothing", so a mis-wired caller degrades to the pre-B-3
        /// behaviour rather than culling everything).
        /// </summary>
        public static double CullRadiusMeters(in double2 viewportLogicalPx, double groundResolutionMeters,
            double viewportSpans)
        {
            double maxDim = math.max(viewportLogicalPx.x, viewportLogicalPx.y);
            double radius = viewportSpans * maxDim * groundResolutionMeters;
            return radius > 0.0 ? radius : 0.0;
        }

        /// <summary>
        /// True when <paramref name="anchorRender"/> is farther than <paramref name="cullRadiusMeters"/> from the
        /// look-at (<paramref name="sceneOriginRender"/>) and should be skipped before projection. A
        /// non-positive radius disables the cull (returns false for every label) — the safe pre-B-3 fallback.
        /// Compares squared distances (no sqrt) via <c>dot</c>.
        /// </summary>
        public static bool IsCulled(in double3 anchorRender, in double3 sceneOriginRender, double cullRadiusMeters)
        {
            if (cullRadiusMeters <= 0.0) return false;
            double3 d = anchorRender - sceneOriginRender;
            return math.dot(d, d) > cullRadiusMeters * cullRadiusMeters;
        }
    }
}
