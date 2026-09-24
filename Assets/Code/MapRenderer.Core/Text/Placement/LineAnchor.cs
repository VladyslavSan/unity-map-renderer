// Engine-free: no UnityEngine dependency. Plain data (int + float) — no Unity.Mathematics types either.

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// A stable along-line symbol anchor as polyline TOPOLOGY: a segment index plus <see cref="T"/> in [0,1]
    /// within it, not a coordinate. It is computed once at build time in tile space, so a line symbol's repeats
    /// stay on the same world positions as the camera zooms. Its world point is
    /// <c>lerp(pathRender[Segment], pathRender[Segment+1], T)</c>; its per-frame screen arc distance is
    /// <see cref="PolylineArcMath.ArcDistanceAt"/>.
    /// </summary>
    public readonly struct LineAnchor
    {
        /// <summary>Index of the polyline segment <c>[Segment, Segment+1]</c> this anchor lies on.</summary>
        public readonly int Segment;

        /// <summary>Interpolation parameter in [0,1] within the segment (0 = start vertex, 1 = end vertex).</summary>
        public readonly float T;

        public LineAnchor(int segment, float t) { Segment = segment; T = t; }
    }
}
