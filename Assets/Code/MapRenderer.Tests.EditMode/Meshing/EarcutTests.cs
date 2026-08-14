using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Tests
{
    /// <summary>
    /// EditMode tests for the clean-room Earcut triangulator.
    /// All geometry is defined in tile-space double2 (Y-down, origin top-left).
    ///
    /// Triangle count formula for a simple polygon with holes:
    ///   triangles = outerVerts + 2 * holeCount - 2
    ///   (each hole adds 2 bridge verts, each new poly needs n-2 triangles)
    ///
    /// Area conservation: Σ|triArea| should equal |outerArea| − Σ|holeAreas|, within epsilon.
    /// Tests use Earcut.Result.Vertices for index lookup (no out-of-band reconstruction needed).
    /// </summary>
    public class EarcutTests
    {
        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        private static double SignedTriArea(double2 a, double2 b, double2 c)
            => 0.5 * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));

        private static double TotalTriArea(double2[] verts, int[] indices)
        {
            double total = 0.0;
            for (int i = 0; i < indices.Length; i += 3)
                total += Math.Abs(SignedTriArea(verts[indices[i]], verts[indices[i + 1]], verts[indices[i + 2]]));
            return total;
        }

        private static double RingArea(List<double2> ring)
        {
            double area = 0.0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                double2 a = ring[i];
                double2 b = ring[(i + 1) % n];
                area += (b.x - a.x) * (b.y + a.y);
            }
            return Math.Abs(area) * 0.5;
        }

        // -----------------------------------------------------------------------------------------
        // Square (4 verts, no holes) → 2 triangles
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Square_NoHoles_Produces2Triangles()
        {
            // Exterior winding: positive shoelace (canonical CCW in tile space; reads CW on a Y-down screen).
            var square = new List<double2>
            {
                new double2(0, 0),
                new double2(100, 0),
                new double2(100, 100),
                new double2(0, 100),
            };

            var result = Earcut.Triangulate(square, null);
            Assert.AreEqual(6, result.Indices.Length, "Square should produce 6 indices (2 triangles).");

            // All indices must be in range.
            foreach (int idx in result.Indices)
                Assert.That(idx, Is.GreaterThanOrEqualTo(0).And.LessThan(result.Vertices.Length),
                    $"Index {idx} out of range [0, {result.Vertices.Length}).");
        }

        // -----------------------------------------------------------------------------------------
        // Pentagon → 3 triangles
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Pentagon_NoHoles_Produces3Triangles()
        {
            var pentagon = new List<double2>();
            for (int i = 0; i < 5; i++)
            {
                double angle = 2.0 * Math.PI * i / 5.0;
                pentagon.Add(new double2(100 + 50 * Math.Cos(angle), 100 + 50 * Math.Sin(angle)));
            }

            var result = Earcut.Triangulate(pentagon, null);
            Assert.AreEqual(9, result.Indices.Length, "Pentagon should produce 9 indices (3 triangles).");

            foreach (int idx in result.Indices)
                Assert.That(idx, Is.GreaterThanOrEqualTo(0).And.LessThan(result.Vertices.Length));
        }

        // -----------------------------------------------------------------------------------------
        // Square with square hole → 8 triangles.
        // Inner vertex count: outer(4) + hole(4) + 2 bridge copies = 10 verts → 10-2 = 8 triangles.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void SquareWithSquareHole_Produces8Triangles()
        {
            // Outer: exterior ring (positive shoelace)
            var outer = new List<double2>
            {
                new double2(0, 0),
                new double2(200, 0),
                new double2(200, 200),
                new double2(0, 200),
            };

            // Hole: opposite winding (negative shoelace)
            var hole = new List<double2>
            {
                new double2(50, 50),
                new double2(50, 150),
                new double2(150, 150),
                new double2(150, 50),
            };

            var result = Earcut.Triangulate(outer, new List<List<double2>> { hole });
            Assert.AreEqual(24, result.Indices.Length, "Square+hole should produce 24 indices (8 triangles).");

            foreach (int idx in result.Indices)
                Assert.That(idx, Is.GreaterThanOrEqualTo(0).And.LessThan(result.Vertices.Length),
                    $"Index {idx} out of range [0, {result.Vertices.Length}).");
        }

        // -----------------------------------------------------------------------------------------
        // Area conservation: Σ|triArea| ≈ |outerArea| − |holeArea| (tile space)
        // -----------------------------------------------------------------------------------------

        [Test]
        public void SquareWithHole_AreaIsConserved()
        {
            var outer = new List<double2>
            {
                new double2(0, 0),
                new double2(400, 0),
                new double2(400, 400),
                new double2(0, 400),
            };
            var hole = new List<double2>
            {
                new double2(100, 100),
                new double2(100, 300),
                new double2(300, 300),
                new double2(300, 100),
            };

            var result = Earcut.Triangulate(outer, new List<List<double2>> { hole });
            Assert.Greater(result.Indices.Length, 0, "Should produce triangles.");

            double triArea = TotalTriArea(result.Vertices, result.Indices);
            double expectedArea = RingArea(outer) - RingArea(hole);

            Assert.That(triArea, Is.EqualTo(expectedArea).Within(expectedArea * 1e-6),
                $"Area conservation: got {triArea}, expected {expectedArea}");
        }

        // -----------------------------------------------------------------------------------------
        // Simple polygon (no hole) area conservation.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Square_NoHoles_AreaIsConserved()
        {
            var outer = new List<double2>
            {
                new double2(0, 0),
                new double2(300, 0),
                new double2(300, 300),
                new double2(0, 300),
            };

            var result = Earcut.Triangulate(outer, null);
            double triArea = TotalTriArea(result.Vertices, result.Indices);
            double expectedArea = RingArea(outer);

            Assert.That(triArea, Is.EqualTo(expectedArea).Within(expectedArea * 1e-6),
                $"Area conservation: got {triArea}, expected {expectedArea}");
        }

        // -----------------------------------------------------------------------------------------
        // Index range validity (explicit check with hole)
        // -----------------------------------------------------------------------------------------

        [Test]
        public void AllIndicesAreInRange()
        {
            var outer = new List<double2>
            {
                new double2(0, 0), new double2(300, 0),
                new double2(300, 300), new double2(0, 300),
            };
            var hole = new List<double2>
            {
                new double2(50, 50), new double2(50, 250),
                new double2(250, 250), new double2(250, 50),
            };

            var result = Earcut.Triangulate(outer, new List<List<double2>> { hole });
            foreach (int idx in result.Indices)
                Assert.That(idx, Is.GreaterThanOrEqualTo(0).And.LessThan(result.Vertices.Length),
                    $"Index {idx} out of range [0, {result.Vertices.Length})");
        }

        // -----------------------------------------------------------------------------------------
        // Degenerate input
        // -----------------------------------------------------------------------------------------

        [Test]
        public void NullOuter_ReturnsEmpty()
        {
            var result = Earcut.Triangulate(null, null);
            Assert.AreEqual(0, result.Indices.Length);
            Assert.AreEqual(0, result.Vertices.Length);
        }

        [Test]
        public void TooFewVerts_ReturnsEmpty()
        {
            var ring = new List<double2> { new double2(0, 0), new double2(1, 1) };
            var result = Earcut.Triangulate(ring, null);
            Assert.AreEqual(0, result.Indices.Length);
        }
    }
}
