// Engine-free: no UnityEngine dependency. Plain data (int + float) — no Unity.Mathematics types either.

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// A stable along-line symbol anchor expressed as polyline TOPOLOGY — a segment index plus an
    /// interpolation parameter <see cref="T"/> in [0,1] within that segment — NOT a screen or world
    /// coordinate. Computed ONCE at build time in TILE space (projection-agnostic, zoom-invariant) so a
    /// <c>symbol-placement: line</c> symbol's repeats stay pinned to the same world positions as the camera
    /// zooms — fixing the slide where fixed screen-px-from-start anchors drifted to different world points as
    /// the projected line length changed.
    ///
    /// <para><b>Recovering positions.</b> The anchor's world (pre-RTC render-space) point is
    /// <c>lerp(pathRender[Segment], pathRender[Segment+1], T)</c> — a zoom-independent position the
    /// cross-tile identity key builds on. The anchor's per-frame SCREEN arc distance along the projected
    /// polyline is <see cref="PolylineArcMath.ArcDistanceAt"/>.</para>
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
