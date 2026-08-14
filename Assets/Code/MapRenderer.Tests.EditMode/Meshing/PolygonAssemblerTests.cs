using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// EditMode tests for PolygonAssembler.
    /// Verifies outer/hole classification by signed-area sign and multipolygon handling.
    ///
    /// Winding note: geometry is canonical CCW in tile space (Y-up, positive shoelace) — see
    /// docs/coordinates-and-projections.md §7. Classification is by shoelace SIGN, not by frame:
    ///   Positive shoelace area (area2 > 0) = EXTERIOR ring.
    ///   Negative shoelace area (area2 &lt; 0) = HOLE ring (opposite winding).
    /// (The same vertex order reads CW on a Y-down screen; that is a frame, not the convention.)
    /// The assembler classifies based on the sign of the first ring (feature-relative).
    /// </summary>
    public class PolygonAssemblerTests
    {
        // -----------------------------------------------------------------------------------------
        // Helper: compute shoelace area2
        // -----------------------------------------------------------------------------------------

        private static double Area2(List<double2> ring)
        {
            // Cross-product shoelace: Σ (a.x * b.y − b.x * a.y)
            // Matches SignedArea.Compute. Positive shoelace = exterior (canonical CCW in tile space).
            double area = 0.0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                double2 a = ring[i];
                double2 b = ring[(i + 1) % n];
                area += a.x * b.y - b.x * a.y;
            }
            return area;
        }

        // -----------------------------------------------------------------------------------------
        // Utility ring builders
        // -----------------------------------------------------------------------------------------

        /// <summary>Exterior ring: positive shoelace (canonical CCW in tile space).</summary>
        private static List<double2> ExteriorSquare(double x, double y, double size)
            => new List<double2>
            {
                new double2(x, y),
                new double2(x + size, y),
                new double2(x + size, y + size),
                new double2(x, y + size),
            };

        /// <summary>Hole ring: negative shoelace (opposite winding from the exterior).</summary>
        private static List<double2> HoleSquare(double x, double y, double size)
            => new List<double2>
            {
                new double2(x, y),
                new double2(x, y + size),
                new double2(x + size, y + size),
                new double2(x + size, y),
            };

        // -----------------------------------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------------------------------

        [Test]
        public void CwOuter_CcwHole_Produces1PolygonWith1Hole()
        {
            // Outer: exterior ring (positive shoelace)
            var outer = ExteriorSquare(0, 0, 200);
            Assert.Greater(Area2(outer), 0, "Outer should have positive shoelace (exterior).");

            // Hole: opposite winding (negative shoelace)
            var hole = HoleSquare(50, 50, 100);
            Assert.Less(Area2(hole), 0, "Hole should have negative shoelace (hole).");

            var rings = new List<List<double2>> { outer, hole };
            var polygons = PolygonAssembler.Assemble(rings);

            Assert.AreEqual(1, polygons.Count, "Should produce exactly 1 polygon.");
            Assert.AreEqual(1, polygons[0].Holes.Count, "Polygon should have exactly 1 hole.");
        }

        [Test]
        public void CwOuter_DisjointCcwRing_DropsTheRing()
        {
            // A hole-wound ring (opposite sign) placed entirely OUTSIDE the outer.
            // An MVT hole must lie inside its exterior, so this spatially-disjoint opposite-wound
            // ring is a clip/winding artefact and must be DROPPED — not mis-nested. Mis-nesting it
            // makes Earcut bridge across the gap to a far-away ring and emit overlapping triangles
            // (area inflated up to ~34x on real tile data; see HoledPolygon_WorstCase test).
            var outer    = ExteriorSquare(0, 0, 100);
            var disjoint = HoleSquare(500, 500, 50); // far outside the outer
            Assert.Less(Area2(disjoint), 0, "Disjoint ring should have hole sign (negative shoelace).");

            var polygons = PolygonAssembler.Assemble(new List<List<double2>> { outer, disjoint });

            Assert.AreEqual(1, polygons.Count, "Should produce 1 polygon (the outer).");
            Assert.AreEqual(0, polygons[0].Holes.Count,
                "A spatially-disjoint opposite-wound ring must be dropped, not nested as a hole.");
        }

        [Test]
        public void TwoSameSignRings_Produce2Polygons()
        {
            // Two exterior rings (both same sign → multipolygon)
            var ring1 = ExteriorSquare(0, 0, 100);
            var ring2 = ExteriorSquare(200, 200, 100);

            Assert.Greater(Area2(ring1), 0);
            Assert.Greater(Area2(ring2), 0);

            var rings = new List<List<double2>> { ring1, ring2 };
            var polygons = PolygonAssembler.Assemble(rings);

            Assert.AreEqual(2, polygons.Count, "Two same-sign rings should produce 2 polygons.");
            Assert.AreEqual(0, polygons[0].Holes.Count);
            Assert.AreEqual(0, polygons[1].Holes.Count);
        }

        [Test]
        public void MultipolygonWithHoles_CorrectlyClassified()
        {
            // First outer (exterior), its hole, second outer (exterior), its hole
            var outer1 = ExteriorSquare(0, 0, 200);
            var hole1 = HoleSquare(25, 25, 50);
            var outer2 = ExteriorSquare(300, 0, 200);
            var hole2 = HoleSquare(325, 25, 50);

            var rings = new List<List<double2>> { outer1, hole1, outer2, hole2 };
            var polygons = PolygonAssembler.Assemble(rings);

            Assert.AreEqual(2, polygons.Count, "Should produce 2 polygons.");
            Assert.AreEqual(1, polygons[0].Holes.Count, "Polygon 0 should have 1 hole.");
            Assert.AreEqual(1, polygons[1].Holes.Count, "Polygon 1 should have 1 hole.");
        }

        [Test]
        public void EmptyInput_ReturnsEmptyList()
        {
            var polygons = PolygonAssembler.Assemble(new List<List<double2>>());
            Assert.AreEqual(0, polygons.Count);
        }

        [Test]
        public void NullInput_ReturnsEmptyList()
        {
            var polygons = PolygonAssembler.Assemble(null);
            Assert.AreEqual(0, polygons.Count);
        }

        [Test]
        public void DegenerateRing_IsSkipped()
        {
            // A 2-point ring is degenerate and should be skipped.
            var degenerate = new List<double2> { new double2(0, 0), new double2(1, 1) };
            var valid = ExteriorSquare(100, 100, 100);
            var rings = new List<List<double2>> { degenerate, valid };
            var polygons = PolygonAssembler.Assemble(rings);

            // The degenerate ring is skipped; the valid ring becomes the only polygon.
            Assert.AreEqual(1, polygons.Count, "Degenerate ring should be skipped.");
        }

        [Test]
        public void SingleRing_ProducesOnePolygonNoHoles()
        {
            var ring = ExteriorSquare(0, 0, 100);
            var polygons = PolygonAssembler.Assemble(new List<List<double2>> { ring });

            Assert.AreEqual(1, polygons.Count);
            Assert.AreEqual(0, polygons[0].Holes.Count);
        }
    }
}
