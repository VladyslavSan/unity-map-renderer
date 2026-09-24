using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Tests.TestSupport
{
    /// <summary>
    /// Groups a feature's decoded MVT rings (as produced by MvtDecodeJob) into polygons with explicit
    /// outer + hole lists, per the MVT spec order (exterior rings first, holes follow). The first ring
    /// sets the exterior sign. A later ring with the SAME signed-area sign starts a new Polygon; a
    /// ring with the OPPOSITE sign is a hole of the current polygon. Near-zero-area rings are skipped
    /// to prevent downstream stalls.
    /// </summary>
    public static class PolygonAssembler
    {
        /// <summary>Rings with |2×area| (standard shoelace) below this threshold are skipped.</summary>
        private const double DegenerateThreshold = 1.0;

        public static List<Polygon> Assemble(List<List<double2>> rings)
        {
            var polygons = new List<Polygon>();
            if (rings == null || rings.Count == 0)
                return polygons;

            double exteriorSign = 0.0;  // sign of the first valid ring (exterior sign for this feature)
            Polygon current = null;

            foreach (var ring in rings)
            {
                if (ring == null || ring.Count < 3)
                    continue;

                double area2 = SignedArea.Compute(ring);
                if (math.abs(area2) < DegenerateThreshold)
                    continue;  // degenerate ring — skip

                if (exteriorSign == 0.0)
                {
                    // First ring: sets the exterior sign for this feature.
                    exteriorSign = area2 > 0.0 ? 1.0 : -1.0;
                    current = new Polygon(ring);
                    polygons.Add(current);
                }
                else
                {
                    double ringSign = area2 > 0.0 ? 1.0 : -1.0;
                    if (ringSign == exteriorSign)
                    {
                        // Same sign as exterior → new outer ring (multipolygon: new island).
                        current = new Polygon(ring);
                        polygons.Add(current);
                    }
                    else
                    {
                        // Non-obvious why: a disjoint opposite-wound ring is a clip artefact; nesting
                        // it makes Earcut bridge to a far ring and emit overlapping triangles. So attach
                        // only a ring contained in the current outer ring.
                        if (current != null && RingContainedIn(ring, current.Outer))
                            current.Holes.Add(ring);
                        // else: misplaced ring — drop it.
                    }
                }
            }

            return polygons;
        }

        /// <summary>
        /// True if <paramref name="ring"/> lies inside <paramref name="outer"/>. MVT holes are
        /// always interior to their exterior, so a representative point of a real hole falls inside
        /// the exterior outer ring. Tests the centroid, with a first-vertex fallback for robustness.
        /// </summary>
        private static bool RingContainedIn(List<double2> ring, List<double2> outer)
        {
            if (PointInRing(Centroid(ring), outer)) return true;
            return PointInRing(ring[0], outer);
        }

        private static double2 Centroid(List<double2> ring)
        {
            double sx = 0.0, sy = 0.0;
            for (int i = 0; i < ring.Count; i++) { sx += ring[i].x; sy += ring[i].y; }
            return new double2(sx / ring.Count, sy / ring.Count);
        }

        /// <summary>Even-odd ray-cast point-in-polygon test against a ring.</summary>
        private static bool PointInRing(double2 p, List<double2> ring)
        {
            bool inside = false;
            int n = ring.Count;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = ring[i].x, yi = ring[i].y, xj = ring[j].x, yj = ring[j].y;
                bool straddle = (yi > p.y) != (yj > p.y);
                if (straddle && (p.x < (xj - xi) * (p.y - yi) / (yj - yi) + xi))
                    inside = !inside;
            }
            return inside;
        }
    }
}
