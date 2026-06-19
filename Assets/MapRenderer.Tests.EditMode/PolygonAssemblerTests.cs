using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Tests
{
    /// <summary>
    /// EditMode tests for PolygonAssembler.
    /// Verifies outer/hole classification by signed-area sign and multipolygon handling.
    ///
    /// Winding note: in MVT tile space (Y-down, top-left origin):
    ///   Positive shoelace area (area2 > 0) = CW on screen = EXTERIOR in MVT order.
    ///   Negative shoelace area (area2 &lt; 0) = CCW on screen = HOLE in MVT order.
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
            // Matches SignedArea.Compute. Positive = CW on screen in Y-down tile space (exterior).
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

        /// <summary>CW square in tile space (positive area2 = exterior in MVT).</summary>
        private static List<double2> CwSquare(double x, double y, double size)
            => new List<double2>
            {
                new double2(x, y),
                new double2(x + size, y),
                new double2(x + size, y + size),
                new double2(x, y + size),
            };

        /// <summary>CCW square in tile space (negative area2 = hole in MVT).</summary>
        private static List<double2> CcwSquare(double x, double y, double size)
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
            // Outer: CW in tile space (positive area2)
            var outer = CwSquare(0, 0, 200);
            Assert.Greater(Area2(outer), 0, "Outer should be CW (positive area2 in Y-down).");

            // Hole: CCW in tile space (negative area2) — opposite sign
            var hole = CcwSquare(50, 50, 100);
            Assert.Less(Area2(hole), 0, "Hole should be CCW (negative area2 in Y-down).");

            var rings = new List<List<double2>> { outer, hole };
            var polygons = PolygonAssembler.Assemble(rings);

            Assert.AreEqual(1, polygons.Count, "Should produce exactly 1 polygon.");
            Assert.AreEqual(1, polygons[0].Holes.Count, "Polygon should have exactly 1 hole.");
        }

        [Test]
        public void CwOuter_DisjointCcwRing_DropsTheRing()
        {
            // A CCW ring (opposite sign → "hole" by winding) placed entirely OUTSIDE the outer.
            // An MVT hole must lie inside its exterior, so this spatially-disjoint opposite-wound
            // ring is a clip/winding artefact and must be DROPPED — not mis-nested. Mis-nesting it
            // makes Earcut bridge across the gap to a far-away ring and emit overlapping triangles
            // (area inflated up to ~34x on real tile data; see HoledPolygon_WorstCase test).
            var outer    = CwSquare(0, 0, 100);
            var disjoint = CcwSquare(500, 500, 50); // far outside the outer
            Assert.Less(Area2(disjoint), 0, "Disjoint ring should be CCW (hole sign).");

            var polygons = PolygonAssembler.Assemble(new List<List<double2>> { outer, disjoint });

            Assert.AreEqual(1, polygons.Count, "Should produce 1 polygon (the outer).");
            Assert.AreEqual(0, polygons[0].Holes.Count,
                "A spatially-disjoint opposite-wound ring must be dropped, not nested as a hole.");
        }

        [Test]
        public void TwoSameSignRings_Produce2Polygons()
        {
            // Two CW rings (both exterior = same sign → multipolygon)
            var ring1 = CwSquare(0, 0, 100);
            var ring2 = CwSquare(200, 200, 100);

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
            // First outer (CW), its hole (CCW), second outer (CW), its hole (CCW)
            var outer1 = CwSquare(0, 0, 200);
            var hole1 = CcwSquare(25, 25, 50);
            var outer2 = CwSquare(300, 0, 200);
            var hole2 = CcwSquare(325, 25, 50);

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
            var valid = CwSquare(100, 100, 100);
            var rings = new List<List<double2>> { degenerate, valid };
            var polygons = PolygonAssembler.Assemble(rings);

            // The degenerate ring is skipped; the valid ring becomes the only polygon.
            Assert.AreEqual(1, polygons.Count, "Degenerate ring should be skipped.");
        }

        [Test]
        public void SingleRing_ProducesOnePolygonNoHoles()
        {
            var ring = CwSquare(0, 0, 100);
            var polygons = PolygonAssembler.Assemble(new List<List<double2>> { ring });

            Assert.AreEqual(1, polygons.Count);
            Assert.AreEqual(0, polygons[0].Holes.Count);
        }
    }
}
