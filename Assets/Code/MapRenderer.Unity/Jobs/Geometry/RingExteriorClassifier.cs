using Unity.Collections;
using Unity.Mathematics;

namespace MapRenderer.Unity.Jobs.Geometry
{
    /// <summary>
    /// The one exterior/hole predicate for a Polygon feature's rings. <c>RingAssemblyJob</c> (fill) and
    /// <c>SymbolFeatureExtractor</c> (symbol-placement line/line-center) both classify through
    /// <see cref="State.Classify"/>, so the two can never disagree about the same polygon.
    /// </summary>
    internal static class RingExteriorClassifier
    {
        /// <summary>Rings with |2*area| (shoelace) below this threshold are treated as degenerate.</summary>
        internal const double DegenerateThreshold = 1.0;

        /// <summary>A ring's role once classified against its feature's running exterior state.</summary>
        internal enum Role : byte { Dropped = 0, Outer = 1, Hole = 2 }

        /// <summary>
        /// Shoelace signed area × 2: Σ (x_i*y_{i+1} − x_{i+1}*y_i).
        /// Positive = CCW in Y-up = CW on screen in Y-down (MVT exterior).
        /// </summary>
        internal static double SignedArea2(NativeArray<double2> verts, int start, int len)
        {
            double area = 0.0;
            for (int i = 0; i < len; i++)
            {
                double2 a = verts[start + i];
                double2 b = verts[start + (i + 1) % len];
                area += a.x * b.y - b.x * a.y;
            }
            return area;
        }

        private static double2 Centroid(NativeArray<double2> verts, int start, int len)
        {
            double sx = 0.0, sy = 0.0;
            for (int i = 0; i < len; i++) { sx += verts[start + i].x; sy += verts[start + i].y; }
            return new double2(sx / len, sy / len);
        }

        /// <summary>Even-odd ray-cast point-in-polygon.</summary>
        private static bool PointInRing(double2 p, NativeArray<double2> ring, int rStart, int rLen)
        {
            bool inside = false;
            for (int i = 0, j = rLen - 1; i < rLen; j = i++)
            {
                double xi = ring[rStart + i].x, yi = ring[rStart + i].y;
                double xj = ring[rStart + j].x, yj = ring[rStart + j].y;
                bool straddle = (yi > p.y) != (yj > p.y);
                if (straddle && (p.x < (xj - xi) * (p.y - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>True when ring [holeStart,holeLen) lies inside ring [outerStart,outerLen) — its centroid,
        /// or (a degenerate centroid) its first vertex.</summary>
        internal static bool RingContainedIn(
            NativeArray<double2> verts,
            int holeStart, int holeLen,
            int outerStart, int outerLen)
        {
            double2 centroid = Centroid(verts, holeStart, holeLen);
            if (PointInRing(centroid, verts, outerStart, outerLen)) return true;
            return PointInRing(verts[holeStart], verts, outerStart, outerLen);
        }

        /// <summary>
        /// Per-feature running state for <see cref="Classify"/>. Walk one feature's rings in decode order with
        /// a single instance; construct (or <see cref="Reset"/>) a fresh one at every feature boundary.
        /// </summary>
        internal struct State
        {
            private double _exteriorSign;
            private int    _outerStart;
            private int    _outerLen;

            /// <summary>Clears the running exterior sign. Call once per feature, before its first ring.</summary>
            internal void Reset()
            {
                _exteriorSign = 0.0;
                _outerStart   = 0;
                _outerLen     = 0;
            }

            /// <summary>
            /// Classifies ring [start,len) against this feature's running state: the first non-degenerate ring
            /// sets the exterior sign (<see cref="Role.Outer"/>); a same-sign ring starts a new outer ring
            /// (multipolygon island, also <see cref="Role.Outer"/>); an opposite-sign ring is a
            /// <see cref="Role.Hole"/> only if it lies inside the current outer ring, else
            /// <see cref="Role.Dropped"/> (a disjoint artefact ring). A ring under 3 vertices, or with
            /// near-zero area, is always <see cref="Role.Dropped"/> without touching the running state.
            /// </summary>
            internal Role Classify(NativeArray<double2> vertices, int start, int len)
            {
                if (len < 3) return Role.Dropped; // degenerate ring

                double area2 = SignedArea2(vertices, start, len);
                if (area2 < DegenerateThreshold && area2 > -DegenerateThreshold)
                    return Role.Dropped; // degenerate ring

                if (_exteriorSign == 0.0)
                {
                    _exteriorSign = area2 > 0.0 ? 1.0 : -1.0;
                    _outerStart = start; _outerLen = len;
                    return Role.Outer;
                }

                double ringSign = area2 > 0.0 ? 1.0 : -1.0;
                if (ringSign == _exteriorSign)
                {
                    _outerStart = start; _outerLen = len;
                    return Role.Outer;
                }

                return RingContainedIn(vertices, start, len, _outerStart, _outerLen) ? Role.Hole : Role.Dropped;
            }
        }
    }
}
