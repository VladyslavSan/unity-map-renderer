using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Geometry
{
    /// <summary>
    /// Clips ONE open polyline against an axis-aligned window into 0..N open pieces: per-segment
    /// Liang–Barsky clipping, surviving sub-segments joined into runs. Non-obvious why: not
    /// Sutherland–Hodgman, because a ring clipper closes its output and joins a line that left and
    /// re-entered across a gap. Input and output are tile-local <c>double2</c>, origin top-left, Y down; a
    /// run keeps the input order, breaks where a segment is rejected or does not continue exactly, and is
    /// dropped under two vertices. The boundary is inclusive, as in <see cref="RingWindowClipper"/>. An
    /// unclipped endpoint passes verbatim; a clipped one gets the boundary literal on every axis that
    /// clipped it, because <c>p0 + t·d</c> can land an ulp off the seam and neighbours would disagree
    /// (<c>WindowClipperTests</c>).
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
        /// Where one end of the surviving sub-segment sits: the parameter along the input segment and the
        /// window planes that put it there, whose axes <see cref="Snap"/> sets to the boundary exactly.
        /// <see cref="Axes"/> is a mask because a segment crossing exactly at a corner is placed by two
        /// planes at once, and both coordinates must land on the boundary.
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
