// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` + unqualified float2 —
// this file lives in MapRenderer.Core.Text.Placement; an inline `Unity.Mathematics.float2` would bind to a
// (nonexistent) `MapRenderer.Core.Text.Placement.Unity.Mathematics` namespace (CS0234). See the sibling
// SymbolScreenProjection header comment for the namespace-collision trap.

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
        private int _cursor;           // Lever A: resumable segment cursor (see At) — reset each Init.

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
            _cursor = 0;

            if (_cumulative.Length < _count)
            {
                int cap = _cumulative.Length == 0 ? 8 : _cumulative.Length;
                while (cap < _count) cap *= 2;
                _cumulative = new float[cap];
            }

            TotalLength = PolylineArcMath.BuildCumulative(_points, _count, _cumulative);
        }

        /// <summary>
        /// A-2: the SCREEN arc distance from the start of a stable <see cref="LineAnchor"/> — the projected
        /// position of a tile-space <c>(segment, t)</c> anchor along THIS frame's polyline. Feed the result to
        /// <see cref="At(float, out float2, out float)"/> (± glyph offsets) to lay a curved symbol out around the
        /// anchor. <paramref name="segment"/> is clamped to a valid segment; <paramref name="t"/> to [0,1].
        /// </summary>
        public float ArcDistanceAt(int segment, float t)
            => PolylineArcMath.ArcDistanceAt(_cumulative, _count, segment, t);

        /// <summary>
        /// The point and tangent angle (radians, atan2 of the segment direction) at arc distance
        /// <paramref name="arc"/> from the start. Clamps to the endpoints. A single-point or empty polyline
        /// returns that point (or origin) with tangent 0.
        /// </summary>
        public void At(float arc, out float2 point, out float tangentRadians)
            => PolylineArcMath.At(_points, _cumulative, _count, TotalLength, arc, ref _cursor, out point, out tangentRadians);
    }
}
