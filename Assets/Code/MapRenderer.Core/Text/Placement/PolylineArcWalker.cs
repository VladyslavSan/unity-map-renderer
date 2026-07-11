// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified float2 —
// this file lives in MapRenderer.Core.Text.Placement; an inline `Unity.Mathematics.float2` would bind to a
// (nonexistent) `MapRenderer.Core.Text.Placement.Unity.Mathematics` namespace (CS0234). See the sibling
// LabelScreenProjection header comment for the namespace-collision trap.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// Walks a screen-space polyline by arc length: given a distance along the line, returns the point there
    /// and the local tangent angle. The geometric core of curved along-line text (#5) — glyphs are placed at
    /// arc distances and rotated to the tangent.
    ///
    /// <para><b>Reusable / zero-alloc</b> (the placement path is per-frame and militantly no-GC): call
    /// <see cref="Init"/> each frame over a caller-owned, reused points array; the cumulative-length buffer
    /// grows once and is reused across calls. Holds a REFERENCE to the caller's points array (does not copy).</para>
    /// </summary>
    public sealed class PolylineArcWalker
    {
        private float2[] _points;      // referenced, not owned
        private int _count;
        private float[] _cumulative = System.Array.Empty<float>(); // cumulative arc length at each vertex; reused

        /// <summary>Total arc length of the last <see cref="Init"/>'d polyline (0 for &lt; 2 points).</summary>
        public float TotalLength { get; private set; }

        /// <summary>Vertex count of the current polyline.</summary>
        public int Count => _count;

        /// <summary>
        /// (Re)initialize over <paramref name="points"/>[0..<paramref name="count"/>). Reuses the internal
        /// cumulative buffer (grows geometrically, never shrinks) so a steady-state per-frame call after
        /// warm-up allocates nothing.
        /// </summary>
        public void Init(float2[] points, int count)
        {
            _points = points;
            _count = count < 0 ? 0 : count;

            if (_cumulative.Length < _count)
            {
                int cap = _cumulative.Length == 0 ? 8 : _cumulative.Length;
                while (cap < _count) cap *= 2;
                _cumulative = new float[cap];
            }

            if (_count == 0) { TotalLength = 0f; return; }
            _cumulative[0] = 0f;
            for (int i = 1; i < _count; i++)
                _cumulative[i] = _cumulative[i - 1] + math.length(points[i] - points[i - 1]);
            TotalLength = _cumulative[_count - 1];
        }

        /// <summary>
        /// The point and tangent angle (radians, atan2 of the segment direction) at arc distance
        /// <paramref name="arc"/> from the start. Clamps to the endpoints. A single-point or empty polyline
        /// returns that point (or origin) with tangent 0.
        /// </summary>
        public void At(float arc, out float2 point, out float tangentRadians)
        {
            if (_count == 0) { point = float2.zero; tangentRadians = 0f; return; }
            if (_count == 1) { point = _points[0]; tangentRadians = 0f; return; }

            if (arc <= 0f)
            {
                point = _points[0];
                tangentRadians = SegmentTangent(0);
                return;
            }
            if (arc >= TotalLength)
            {
                point = _points[_count - 1];
                tangentRadians = SegmentTangent(_count - 2);
                return;
            }

            // Find the segment [i, i+1] whose cumulative range contains `arc`.
            int seg = 0;
            for (int i = 1; i < _count; i++)
            {
                if (_cumulative[i] >= arc) { seg = i - 1; break; }
            }

            float segStart = _cumulative[seg];
            float segLen = _cumulative[seg + 1] - segStart;
            float t = segLen > 0f ? (arc - segStart) / segLen : 0f;
            point = math.lerp(_points[seg], _points[seg + 1], t);
            tangentRadians = SegmentTangent(seg);
        }

        // Tangent of segment [i, i+1]; skips forward/backward over zero-length (coincident) vertices so a
        // degenerate segment doesn't collapse the tangent to atan2(0,0).
        private float SegmentTangent(int i)
        {
            for (int j = i; j < _count - 1; j++)
            {
                float2 d = _points[j + 1] - _points[j];
                if (math.lengthsq(d) > 1e-12f) return (float)math.atan2(d.y, d.x);
            }
            for (int j = i - 1; j >= 0; j--)
            {
                float2 d = _points[j + 1] - _points[j];
                if (math.lengthsq(d) > 1e-12f) return (float)math.atan2(d.y, d.x);
            }
            return 0f;
        }
    }
}
