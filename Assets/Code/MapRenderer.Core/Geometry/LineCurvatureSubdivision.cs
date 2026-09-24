// Engine-free: no UnityEngine dependency.

using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// The shared projection-subdivision POLICY: how many equal sub-segments a tile-local centerline segment
    /// needs so its projected arc stays under a projection's <c>MaxRefineAngleRad</c>, plus a managed densifier.
    /// <see cref="SegmentSteps"/> is shared by <c>MapRenderer.Unity.Text.SymbolFeatureExtractor</c> and the
    /// Burst <c>SubdivideJob</c>; each keeps its own densification loop. An ∞ tolerance (flat Mercator) yields
    /// 1 step per segment: the no-op case of the same code, with no branch on projection kind.
    /// </summary>
    public static class LineCurvatureSubdivision
    {
        /// <summary>Always-bound-loops ceiling on the sub-segments one centerline segment can split into
        /// (mirrors the mesh path's prior-art cap — a pathological near-zero tolerance can never spin an
        /// unbounded loop).</summary>
        public const int MaxCurveSegments = 128;

        /// <summary>Number of equal sub-segments a centerline segment is split into so its projected arc (the
        /// angle between the two unit surface normals) stays under <paramref name="maxRefineAngleRad"/>; ≥1,
        /// capped at <see cref="MaxCurveSegments"/>. A ∞ tolerance always yields 1 (no subdivision).</summary>
        public static int SegmentSteps(double3 upA, double3 upB, double maxRefineAngleRad)
        {
            double ang = math.acos(math.clamp(math.dot(upA, upB), -1.0, 1.0));
            // Clamp in double before the int cast: an out-of-range (int) cast is platform-defined (x64 gives
            // int.MinValue), so casting first would return 1 for the segment that needs the most steps.
            return (int)math.clamp(math.ceil(ang / maxRefineAngleRad), 1.0, MaxCurveSegments);
        }

        /// <summary>
        /// Densifies <paramref name="tilePath"/> so each segment's projected arc, measured by the per-point
        /// normals <paramref name="ups"/>, stays under <paramref name="maxRefineAngleRad"/>. Each segment splits
        /// into <see cref="SegmentSteps"/> equal LINEAR parts in tile space, so no original vertex's arc length
        /// or anchor moves. An ∞ tolerance returns the path unchanged and reads no <paramref name="ups"/>
        /// (<c>Subdivide_InfiniteTolerance_ReturnsPathValueUnchanged</c>).
        /// </summary>
        public static List<double2> Subdivide(
            IReadOnlyList<double2> tilePath, IReadOnlyList<double3> ups, double maxRefineAngleRad)
        {
            if (double.IsPositiveInfinity(maxRefineAngleRad))
                return new List<double2>(tilePath);

            int n = tilePath.Count;
            int count = 1; // the first point, then `segs` points per segment (sub-points + the segment end)
            for (int k = 0; k < n - 1; k++) count += SegmentSteps(ups[k], ups[k + 1], maxRefineAngleRad);

            var dense = new List<double2>(count) { tilePath[0] };
            for (int k = 0; k < n - 1; k++)
            {
                int     segs = SegmentSteps(ups[k], ups[k + 1], maxRefineAngleRad);
                double2 a    = tilePath[k];
                double2 b    = tilePath[k + 1];
                for (int j = 1; j <= segs; j++)
                {
                    double t = (double)j / segs;
                    dense.Add(a + (b - a) * t);
                }
            }
            return dense;
        }
    }
}
