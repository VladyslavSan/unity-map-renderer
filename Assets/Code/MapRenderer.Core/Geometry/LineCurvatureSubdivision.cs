// Engine-free: no UnityEngine dependency.

using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// S4: the shared projection-subdivision POLICY — how many equal sub-segments a tile-local centerline
    /// segment needs so its projected arc stays under a projection's <c>MaxRefineAngleRad</c> tolerance — plus
    /// the managed densifier built on it. <see cref="SegmentSteps"/> is the one piece two callers share:
    /// <c>MapRenderer.Core.Style.Symbol.SymbolFeatureExtractor</c> (this assembly, over managed
    /// <c>List&lt;double2&gt;</c>) and the mesh line builder (<c>StyledLineTileBuilder.SubdivideCenterline</c>,
    /// over <c>NativeList&lt;double2&gt;</c> — welded to <c>Unity.Collections</c>, so it cannot live here).
    /// Each caller keeps its own container-specific densification loop; only the curvature math is unified
    /// (duplicating THAT would be the real smell — "unify, don't propagate smell").
    /// <para>A ∞ tolerance (flat/Mercator projection) always yields 1 step per segment, so subdivision is the
    /// degenerate no-op case of the SAME code — no capability flag, no branch on projection kind.</para>
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
            // Clamp in DOUBLE space before the int cast: for a pathologically small maxRefineAngleRad,
            // ang/maxRefineAngleRad can exceed int range, and (int) of an out-of-range double is
            // implementation-defined (x64 CVTTSD2SI yields int.MinValue "integer indefinite"; ARM64 FCVTZS
            // saturates to int.MaxValue instead) — casting first made the `< 1` guard fire on x64 and silently
            // return 1 (NO subdivision) for the segment that needed the MOST, defeating the cap it was meant
            // to enforce. Clamping the double first makes the cast always land in [1, MaxCurveSegments].
            return (int)math.clamp(math.ceil(ang / maxRefineAngleRad), 1.0, MaxCurveSegments);
        }

        /// <summary>
        /// Densifies tile-local <paramref name="tilePath"/> so each segment's projected arc stays under
        /// <paramref name="maxRefineAngleRad"/>, per-point surface normals <paramref name="ups"/> (same count
        /// and order as <paramref name="tilePath"/>) driving the split metric. Each segment is split into
        /// <see cref="SegmentSteps"/> equal parts by LINEAR interpolation in tile space — collinear on the
        /// original chord, so the tile-local cumulative arc length at every ORIGINAL vertex is unchanged (the
        /// S4 byte-identity spine: an inserted split point never moves an anchor's arc-length position, only
        /// refines which segment it falls in).
        /// <para>∞-tolerance early-out: returns <paramref name="tilePath"/> unchanged, with no <paramref
        /// name="ups"/> reads. This is a general safety net for any direct caller of <see cref="Subdivide"/>
        /// (pinned by S4-T1) — it is NOT the live Mercator no-op guarantee: the wired caller
        /// (<c>SymbolFeatureExtractor.Extract</c>) checks <c>IsPositiveInfinity</c> itself and bypasses
        /// <see cref="Subdivide"/> entirely on that path, passing the original list straight through. Do not
        /// describe this early-out as "the" Mercator guard; it is a second, currently-unexercised-in-production
        /// backstop.</para>
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
