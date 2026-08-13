using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// Clips ONE open polyline against an axis-aligned window, emitting 0..N open pieces.
    ///
    /// <para><b>This is NOT Sutherland–Hodgman and must not be replaced by it.</b> A ring clipper closes its
    /// output and therefore joins the two ends of a line that left the window and came back — drawing a
    /// segment across a gap the input never had. Lines are not "the polygon case with the closure removed":
    /// they need a genuinely different algorithm, and this one is per-segment Liang–Barsky parametric
    /// clipping with the surviving sub-segments accumulated into runs.</para>
    ///
    /// <para><b>Coordinate space and winding (producer declaration).</b> Input and output are tile-local
    /// <c>double2</c>, origin top-left, Y down. Output paths are <b>OPEN</b> — no closure, no winding, no
    /// ring semantics; vertex order along each output path is the input's order. One input polyline emits as
    /// many output paths as it has runs inside the window; a run is broken whenever a segment is rejected
    /// outright or a surviving sub-segment does not begin exactly where the previous one ended. Paths with
    /// fewer than two vertices are discarded.</para>
    ///
    /// <para><b>Boundary is inclusive</b>, matching <see cref="RingWindowClipper"/> and
    /// <c>MapRenderer.Jobs.RingClipJob</c>: a vertex exactly on the window edge is inside. An endpoint that
    /// was not clipped is passed through VERBATIM (rather than recomputed as <c>p0 + t·d</c>), so a run that
    /// crosses several fully-inside segments is bit-identical to the input and the continuity test between
    /// consecutive segments is exact; an endpoint that WAS clipped gets the boundary value assigned exactly
    /// on EVERY axis that clipped it — one for an edge crossing, both for a corner crossing.</para>
    ///
    /// <para><b>Why the assignment, and not just <c>p0 + t·d</c>.</b> The interpolated value is not exact,
    /// and the error is not a curiosity. Clip <c>(100, 1000) → (5600, 3000)</c> at <c>x = 4096</c>:
    /// <c>t = (4096 − 100)/5500</c>, and <c>100 + t·5500</c> evaluates to <c>4095.9999999999995</c>, one ulp
    /// inside the tile rather than on its edge. Clip the same world line from the neighbouring tile, whose
    /// frame puts that crossing at <c>x = 0</c>, and the interpolation lands at <c>−4.547e−13</c> — outside
    /// that tile's window altogether. Neither vertex is ON the seam, so neither tile's inclusive-boundary
    /// test recognises it as a seam vertex, and (in the frames the slicer actually builds, which do not
    /// round alike — see <c>GeoJsonTileSlicerTests.T13b</c>) the two tiles need not even agree on the point.
    /// The assignment removes all of that: the clipped axis is the boundary literal in each frame, so the
    /// vertex is on the seam by construction and the two tiles name the same edge. This is the same
    /// guarantee <see cref="RingWindowClipper"/>'s <c>Intersect</c> makes, stated the same way, so ring and
    /// line geometry meeting at one tile edge cannot part company by a rounding step.
    /// <c>WindowClipperTests</c>' seam and corner teeth are what observe it.</para>
    ///
    /// <para><i>Note on scope of the claim:</i> <c>GeoJsonTileSlicer</c> quantizes to integers AFTER
    /// clipping, which currently masks a sub-ulp disagreement of this size. The guarantee is the clipper's
    /// own, and it is what a consumer working at unquantized precision — seam-matched stroke and label
    /// geometry, S2/S3 — gets to rely on.</para>
    /// </summary>
    public static class PolylineWindowClipper
    {
        /// <summary>
        /// Clips <paramref name="path"/> to the inclusive window <c>[min, max]</c>. Returns the surviving
        /// open pieces in input order; an empty list when nothing survives.
        /// </summary>
        public static List<List<double2>> Clip(IReadOnlyList<double2> path, double2 min, double2 max)
        {
            var pieces = new List<List<double2>>();
            if (path == null || path.Count < 2) return pieces;

            List<double2> run = null;

            for (int i = 0; i + 1 < path.Count; i++)
            {
                double2 a = path[i];
                double2 b = path[i + 1];

                if (!ClipSegment(a, b, min, max, out Crossing enter, out Crossing leave))
                {
                    run = CloseRun(pieces, run);
                    continue;
                }

                double2 delta = b - a;
                double2 entry = enter.T == 0.0 ? a : enter.Snap(a + enter.T * delta);
                double2 exit  = leave.T == 1.0 ? b : leave.Snap(a + leave.T * delta);

                if (run != null && SameVertex(run[run.Count - 1], entry))
                {
                    Emit(run, exit);
                }
                else
                {
                    run = CloseRun(pieces, run);
                    run = new List<double2> { entry };
                    Emit(run, exit);
                }
            }

            CloseRun(pieces, run);
            return pieces;
        }

        /// <summary>Files <paramref name="run"/> if it is a usable path, and returns null so the caller can
        /// start a fresh one.</summary>
        private static List<double2> CloseRun(List<List<double2>> pieces, List<double2> run)
        {
            if (run != null && run.Count >= 2) pieces.Add(run);
            return null;
        }

        private static void Emit(List<double2> run, double2 v)
        {
            if (run.Count > 0 && SameVertex(run[run.Count - 1], v)) return;
            run.Add(v);
        }

        private static bool SameVertex(double2 a, double2 b) => a.x == b.x && a.y == b.y;

        /// <summary>
        /// Where one end of the surviving sub-segment sits: the parameter along the input segment, plus
        /// which window planes (if any) put it there. <see cref="Snap"/> then assigns each of those axes the
        /// boundary EXACTLY rather than the interpolated value — the same guarantee
        /// <see cref="RingWindowClipper"/> makes, so a later inclusive-boundary test cannot disagree by a
        /// rounding step and a run that leaves and re-enters is not silently glued back together.
        ///
        /// <para><see cref="Axes"/> is a MASK rather than one axis because a segment entering or leaving
        /// exactly at a window CORNER is put there by two planes at the same parameter, and both of its
        /// coordinates then have to land on the boundary. Recording only the first plane would leave the
        /// other coordinate interpolated — off the window by an ulp, on the outside, which is exactly the
        /// case the assignment exists to prevent.</para>
        /// </summary>
        private struct Crossing
        {
            public double  T;
            public int     Axes;      // bit 0 = x was clipped, bit 1 = y; 0 = the endpoint was not clipped
            public double2 Boundary;

            /// <summary>A plane that supersedes whatever was recorded: it meets the segment strictly later
            /// (entry) or strictly earlier (exit) than any plane seen so far.</summary>
            public static Crossing At(double t, int axis, double boundary)
            {
                var crossing = new Crossing { T = t };
                crossing.Add(axis, boundary);
                return crossing;
            }

            /// <summary>A second plane meeting the segment at the SAME parameter — a corner.</summary>
            public void Add(int axis, double boundary)
            {
                Axes |= 1 << axis;
                if (axis == 0) Boundary.x = boundary; else Boundary.y = boundary;
            }

            public double2 Snap(double2 p)
            {
                if ((Axes & 1) != 0) p.x = Boundary.x;
                if ((Axes & 2) != 0) p.y = Boundary.y;
                return p;
            }
        }

        /// <summary>
        /// Liang–Barsky: narrows the parameter interval <c>[t0, t1]</c> ⊂ <c>[0, 1]</c> of segment
        /// <paramref name="a"/>→<paramref name="b"/> against the window's four half-planes. Returns false
        /// when the segment misses the window entirely.
        /// </summary>
        private static bool ClipSegment(double2 a, double2 b, double2 min, double2 max,
                                        out Crossing enter, out Crossing leave)
        {
            enter = new Crossing { T = 0.0 };
            leave = new Crossing { T = 1.0 };

            double dx = b.x - a.x;
            double dy = b.y - a.y;

            return Narrow(-dx, a.x - min.x, 0, min.x, ref enter, ref leave)
                && Narrow( dx, max.x - a.x, 0, max.x, ref enter, ref leave)
                && Narrow(-dy, a.y - min.y, 1, min.y, ref enter, ref leave)
                && Narrow( dy, max.y - a.y, 1, max.y, ref enter, ref leave);
        }

        /// <summary>
        /// One half-plane: <c>p·t &lt;= q</c>. <c>p == 0</c> means the segment is parallel to this plane, so
        /// it is either wholly on the inside (<c>q &gt;= 0</c>, nothing to narrow) or wholly outside.
        ///
        /// <para>The REJECTION tests stay strict, so a segment that only touches the window at a single
        /// parameter still survives; the ASSIGNMENT tests admit the tie, so a corner crossing records both
        /// planes instead of only the first one tried.</para>
        /// </summary>
        private static bool Narrow(double p, double q, int axis, double boundary,
                                   ref Crossing enter, ref Crossing leave)
        {
            if (p == 0.0) return q >= 0.0;

            double r = q / p;
            if (p < 0.0)
            {
                if (r > leave.T) return false;
                if (r > enter.T)       enter = Crossing.At(r, axis, boundary);
                else if (r == enter.T) enter.Add(axis, boundary);
            }
            else
            {
                if (r < enter.T) return false;
                if (r < leave.T)       leave = Crossing.At(r, axis, boundary);
                else if (r == leave.T) leave.Add(axis, boundary);
            }
            return true;
        }
    }
}
