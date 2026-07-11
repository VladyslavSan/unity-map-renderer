// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified float2 — this
// file lives in MapRenderer.Core.Text.Placement; an inline `Unity.Mathematics.float2` would bind to a
// (nonexistent) `MapRenderer.Core.Text.Placement.Unity.Mathematics` namespace (CS0234). See PolylineArcWalker.

using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// The arc-walk geometry of curved along-line text, factored out of <see cref="PolylineArcWalker"/> as
    /// pure, allocation-free static functions over caller-owned spans. Blittable-shaped (spans of value types,
    /// no class state) so the same math drives both the managed per-frame walker AND the future Burst staging
    /// job (Lever C) — one source of truth, no divergence. <see cref="PolylineArcWalker"/> is now a thin
    /// stateful wrapper that holds the reused buffers and forwards to these.
    /// </summary>
    public static class PolylineArcMath
    {
        /// <summary>Fills <paramref name="cumulative"/>[0..count) with the arc length at each vertex and returns
        /// the total (0 for &lt; 2 points). <paramref name="cumulative"/> must have room for <paramref name="count"/>.</summary>
        public static float BuildCumulative(ReadOnlySpan<float2> points, int count, Span<float> cumulative)
        {
            if (count <= 0) return 0f;
            cumulative[0] = 0f;
            for (int i = 1; i < count; i++)
                cumulative[i] = cumulative[i - 1] + math.length(points[i] - points[i - 1]);
            return cumulative[count - 1];
        }

        /// <summary>The screen arc distance of a stable <see cref="LineAnchor"/> <c>(segment, t)</c> along this
        /// polyline. <paramref name="segment"/> is clamped to a valid segment; <paramref name="t"/> to [0,1].</summary>
        public static float ArcDistanceAt(ReadOnlySpan<float> cumulative, int count, int segment, float t)
        {
            if (count < 2) return 0f;
            int seg = math.clamp(segment, 0, count - 2);
            float ct = math.saturate(t);
            float segStart = cumulative[seg];
            return segStart + ct * (cumulative[seg + 1] - segStart);
        }

        /// <summary>
        /// Point + tangent angle (radians) at arc distance <paramref name="arc"/> from the start, clamped to the
        /// endpoints. <paramref name="cursor"/> is a resumable segment hint (Lever A): the caller queries arcs in
        /// monotonic order per label, so resuming from the last hit is O(1) amortized instead of O(count) per glyph.
        /// Robust to out-of-order queries — the two guarded walks land on the containing segment regardless of where
        /// the cursor started. Pass a cursor seeded to 0 at the start of each polyline.
        /// </summary>
        public static void At(ReadOnlySpan<float2> points, ReadOnlySpan<float> cumulative, int count, float total,
            float arc, ref int cursor, out float2 point, out float tangentRadians)
        {
            if (count == 0) { point = float2.zero; tangentRadians = 0f; return; }
            if (count == 1) { point = points[0]; tangentRadians = 0f; return; }

            if (arc <= 0f)
            {
                point = points[0];
                tangentRadians = SegmentTangent(points, count, 0);
                return;
            }
            if (arc >= total)
            {
                point = points[count - 1];
                tangentRadians = SegmentTangent(points, count, count - 2);
                return;
            }

            // Resume the segment search from the cursor: forward while `arc` is beyond this segment's end, then
            // backward while it is before its start. Leaves cumulative[seg] < arc <= cumulative[seg+1].
            int seg = cursor < 0 ? 0 : (cursor > count - 2 ? count - 2 : cursor);
            while (seg < count - 2 && cumulative[seg + 1] < arc) seg++;
            while (seg > 0 && cumulative[seg] >= arc) seg--;
            cursor = seg;

            float segStart = cumulative[seg];
            float segLen = cumulative[seg + 1] - segStart;
            float t = segLen > 0f ? (arc - segStart) / segLen : 0f;
            point = math.lerp(points[seg], points[seg + 1], t);
            tangentRadians = SegmentTangent(points, count, seg);
        }

        /// <summary>Tangent of segment [i, i+1]; skips forward/backward over zero-length (coincident) vertices so
        /// a degenerate segment doesn't collapse the tangent to atan2(0,0).</summary>
        public static float SegmentTangent(ReadOnlySpan<float2> points, int count, int i)
        {
            for (int j = i; j < count - 1; j++)
            {
                float2 d = points[j + 1] - points[j];
                if (math.lengthsq(d) > 1e-12f) return (float)math.atan2(d.y, d.x);
            }
            for (int j = i - 1; j >= 0; j--)
            {
                float2 d = points[j + 1] - points[j];
                if (math.lengthsq(d) > 1e-12f) return (float)math.atan2(d.y, d.x);
            }
            return 0f;
        }
    }
}
