using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Unit tests for <see cref="LineTessellator"/>.
    ///
    /// Engine-free: compiled verbatim in both the Unity EditMode runner and
    /// <c>Tools/core-tests</c> (dotnet test). All geometry verified from first principles.
    ///
    /// Lessons honoured:
    ///   - Pin exact expected counts (no unbounded skips).
    ///   - Verify from data when counts fail: dump the actual vertices/normals.
    ///   - DistanceAlong is strictly monotonic for miter/bevel paths only; non-decreasing for
    ///     round joins/caps (fan vertices share the same cumulative distance).
    /// </summary>
    [TestFixture]
    public class LineTessellatorTests
    {
        // ─── Helpers ───────────────────────────────────────────────────────────────────────

        private static double2 Pt(double x, double y) => new double2(x, y);

        private static double VecLen(double2 v) => Math.Sqrt(v.x * v.x + v.y * v.y);

        private static void AssertNearlyEqual(double expected, double actual, double tol, string msg)
            => Assert.That(actual, Is.InRange(expected - tol, expected + tol), msg);

        // ─── Degenerate input ──────────────────────────────────────────────────────────────

        [Test]
        public void NullLine_ReturnsEmptyResult()
        {
            var r = LineTessellator.Triangulate(null);
            Assert.AreEqual(0, r.Vertices.Length, "Null input → 0 vertices.");
            Assert.AreEqual(0, r.Indices.Length,  "Null input → 0 indices.");
        }

        [Test]
        public void SinglePoint_ReturnsEmptyResult()
        {
            var r = LineTessellator.Triangulate(new[] { Pt(0, 0) });
            Assert.AreEqual(0, r.Vertices.Length, "Single point → 0 vertices.");
            Assert.AreEqual(0, r.Indices.Length,  "Single point → 0 indices.");
        }

        [Test]
        public void EmptyList_ReturnsEmptyResult()
        {
            var r = LineTessellator.Triangulate(new List<double2>());
            Assert.AreEqual(0, r.Vertices.Length, "Empty list → 0 vertices.");
            Assert.AreEqual(0, r.Indices.Length,  "Empty list → 0 indices.");
        }

        [Test]
        public void DuplicatePoints_OnlyTwoDistinct_ProducesSegment()
        {
            // Two actual points and one duplicate of the first — should deduplicate to 2 points.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Butt);

            // After dedup: 2 distinct points → 1 segment → 4 verts, 6 indices.
            Assert.AreEqual(4, r.Vertices.Length,
                $"One segment after dedup → 4 verts. Got {r.Vertices.Length}.");
            Assert.AreEqual(6, r.Indices.Length,
                $"One segment after dedup → 6 indices. Got {r.Indices.Length}.");
        }

        [Test]
        public void AllDuplicatePoints_ReturnsEmpty()
        {
            var r = LineTessellator.Triangulate(
                new[] { Pt(5, 3), Pt(5, 3), Pt(5, 3) });
            Assert.AreEqual(0, r.Vertices.Length, "All-duplicate line → 0 verts.");
            Assert.AreEqual(0, r.Indices.Length,  "All-duplicate line → 0 indices.");
        }

        // ─── Straight 2-point segment (Butt caps, Miter join) ─────────────────────────────

        [Test]
        public void TwoPoints_ButtCap_Miter_ExactlyFourVertsAndTwoTriangles()
        {
            // Horizontal segment: (0,0) → (10,0). Segment tangent = (1,0), left normal = (0,1).
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Butt);

            Assert.AreEqual(4, r.Vertices.Length,
                $"2-pt butt segment → exactly 4 vertices. Got {r.Vertices.Length}.");
            Assert.AreEqual(6, r.Indices.Length,
                $"2-pt butt segment → exactly 6 indices (2 triangles). Got {r.Indices.Length}.");
        }

        [Test]
        public void TwoPoints_ButtCap_NormalsAreUnitLength()
        {
            // Horizontal segment: tangent=(1,0), left normal=(0,1), right normal=(0,-1).
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Butt);

            foreach (var v in r.Vertices)
            {
                double len = VecLen(v.Normal);
                AssertNearlyEqual(1.0, len, 1e-9,
                    $"Butt segment: every normal must be unit length. Got length={len:G10} " +
                    $"for normal=({v.Normal.x:G}, {v.Normal.y:G}).");
            }
        }

        [Test]
        public void TwoPoints_ButtCap_NormalsPerpendicularToSegment()
        {
            // Horizontal segment: tangent = (1, 0). All normals must be perpendicular → dot = 0.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Butt);

            double2 tangent = new double2(1, 0);
            foreach (var v in r.Vertices)
            {
                double dot = v.Normal.x * tangent.x + v.Normal.y * tangent.y;
                AssertNearlyEqual(0.0, dot, 1e-9,
                    $"Butt segment: normals must be perpendicular to tangent. dot={dot:G10} " +
                    $"for normal=({v.Normal.x:G}, {v.Normal.y:G}).");
            }
        }

        [Test]
        public void TwoPoints_ButtCap_NormalsAreOppositeOnLeftAndRight()
        {
            // The two normals must be +(0,1) and -(0,1) = (0,-1) for a horizontal segment.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Butt);

            // Collect unique normals.
            double2 n0 = r.Vertices[0].Normal;
            double2 n1 = r.Vertices[1].Normal;
            // n0 + n1 should be ~(0,0) (opposite).
            AssertNearlyEqual(0.0, n0.x + n1.x, 1e-9, "Left and right normals must be x-opposites.");
            AssertNearlyEqual(0.0, n0.y + n1.y, 1e-9, "Left and right normals must be y-opposites.");
        }

        [Test]
        public void TwoPoints_ButtCap_PositionsMatchEndpoints()
        {
            // Positions should be (0,0) and (10,0) (2 verts per endpoint).
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Butt);

            // First two verts at (0,0).
            for (int i = 0; i < 2; i++)
                Assert.That(
                    r.Vertices[i].Position.x == 0.0 && r.Vertices[i].Position.y == 0.0,
                    Is.True,
                    $"Vertex[{i}] should be at (0,0), got ({r.Vertices[i].Position.x},{r.Vertices[i].Position.y}).");

            // Last two verts at (10,0).
            for (int i = 2; i < 4; i++)
                Assert.That(
                    r.Vertices[i].Position.x == 10.0 && r.Vertices[i].Position.y == 0.0,
                    Is.True,
                    $"Vertex[{i}] should be at (10,0), got ({r.Vertices[i].Position.x},{r.Vertices[i].Position.y}).");
        }

        [Test]
        public void TwoPoints_ButtCap_SideValues_AreCorrect()
        {
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Butt);

            // Vertices 0,2 are left (+1); vertices 1,3 are right (−1).
            Assert.AreEqual(+1f, r.Vertices[0].Side, $"Vertex[0] side={r.Vertices[0].Side}; expected +1.");
            Assert.AreEqual(-1f, r.Vertices[1].Side, $"Vertex[1] side={r.Vertices[1].Side}; expected -1.");
            Assert.AreEqual(+1f, r.Vertices[2].Side, $"Vertex[2] side={r.Vertices[2].Side}; expected +1.");
            Assert.AreEqual(-1f, r.Vertices[3].Side, $"Vertex[3] side={r.Vertices[3].Side}; expected -1.");
        }

        [Test]
        public void AllVertices_WidthScaleIsOne()
        {
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(20, 10) },
                JoinType.Miter, CapType.Butt);

            foreach (var v in r.Vertices)
                Assert.AreEqual(1.0f, v.WidthScale,
                    $"WidthScale must default to 1 for all vertices. Got {v.WidthScale}.");
        }

        [Test]
        public void AllVertices_SideIsOneOrMinusOne()
        {
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(20, 10) },
                JoinType.Miter, CapType.Butt);

            foreach (var v in r.Vertices)
                Assert.That(Math.Abs(Math.Abs(v.Side) - 1f), Is.LessThan(1e-6f),
                    $"Side must be +1 or -1 for every vertex. Got {v.Side}.");
        }

        // ─── DistanceAlong ─────────────────────────────────────────────────────────────────

        [Test]
        public void DistanceAlong_SingleSegment_IsCorrect()
        {
            // Segment length = 10 units.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Butt);

            // All verts at x=0 → dist=0.
            AssertNearlyEqual(0.0, r.Vertices[0].DistanceAlong, 1e-9, "Start verts dist=0.");
            AssertNearlyEqual(0.0, r.Vertices[1].DistanceAlong, 1e-9, "Start verts dist=0.");
            // All verts at x=10 → dist=10.
            AssertNearlyEqual(10.0, r.Vertices[2].DistanceAlong, 1e-9, "End verts dist=10.");
            AssertNearlyEqual(10.0, r.Vertices[3].DistanceAlong, 1e-9, "End verts dist=10.");
        }

        [Test]
        public void DistanceAlong_MultiSegment_MiterJoin_IsNonDecreasing()
        {
            // L-shaped line: (0,0)→(10,0)→(10,10). Segment lengths: 10 and 10.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) },
                JoinType.Miter, CapType.Butt);

            for (int i = 1; i < r.Vertices.Length; i++)
                Assert.That(r.Vertices[i].DistanceAlong,
                    Is.GreaterThanOrEqualTo(r.Vertices[i - 1].DistanceAlong - 1e-9),
                    $"DistanceAlong must be non-decreasing. Vertex[{i}]={r.Vertices[i].DistanceAlong:G10} " +
                    $"< Vertex[{i-1}]={r.Vertices[i-1].DistanceAlong:G10}.");
        }

        [Test]
        public void DistanceAlong_FinalVertex_EqualsPolylineLength()
        {
            // (0,0)→(3,4) length = 5.  (0,0)→(3,0)→(3,4) = 3+4 = 7.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(3, 0), Pt(3, 4) },
                JoinType.Miter, CapType.Butt);

            double maxDist = 0;
            foreach (var v in r.Vertices)
                if (v.DistanceAlong > maxDist) maxDist = v.DistanceAlong;

            AssertNearlyEqual(7.0, maxDist, 1e-9,
                $"Max DistanceAlong must equal the polyline length (3+4=7). Got {maxDist:G10}.");
        }

        // ─── 90° Miter join ────────────────────────────────────────────────────────────────

        [Test]
        public void RightAngle_MiterJoin_ExactVertexCount()
        {
            // 3-point line with 90° corner (left turn): (0,0)→(10,0)→(10,10).
            // Layout (butt caps, miter join):
            //   Start cap:    2 verts
            //   Join at (10,0): 2 verts + quad = 2 verts
            //   Last seg:     2 verts + quad = 2 verts
            //   Total: 6 verts, 12 indices
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) },
                JoinType.Miter, CapType.Butt, miterLimit: 10.0);

            Assert.AreEqual(6, r.Vertices.Length,
                $"3-pt miter join → 6 vertices. Got {r.Vertices.Length}.");
            Assert.AreEqual(12, r.Indices.Length,
                $"3-pt miter join → 12 indices. Got {r.Indices.Length}.");
        }

        [Test]
        public void RightAngle_MiterJoin_NormalLengthIsSqrt2()
        {
            // At a 90° left-turn join, the miter factor = 1/cos(45°) = √2.
            // The join vertices (index 2 and 3, left and right) have |normal| = √2.
            // The segment start/end normals (indices 0,1,4,5) have |normal| = 1.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) },
                JoinType.Miter, CapType.Butt, miterLimit: 10.0);

            // Join vertices are at the corner (10,0) — that's verts[2] and verts[3].
            double miterExpected = Math.Sqrt(2.0);
            for (int i = 2; i <= 3; i++)
            {
                double len = VecLen(r.Vertices[i].Normal);
                AssertNearlyEqual(miterExpected, len, 1e-9,
                    $"90° miter join: vertex[{i}] |normal| should be √2 = {miterExpected:G10}. " +
                    $"Got {len:G10} for normal=({r.Vertices[i].Normal.x:G},{r.Vertices[i].Normal.y:G}).");
            }
        }

        [Test]
        public void RightAngle_MiterJoin_SegmentNormalsAreUnit()
        {
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) },
                JoinType.Miter, CapType.Butt, miterLimit: 10.0);

            // Segment start verts (0,1) and terminal verts (4,5) must be unit length.
            int[] segVertIdx = { 0, 1, 4, 5 };
            foreach (int i in segVertIdx)
            {
                double len = VecLen(r.Vertices[i].Normal);
                AssertNearlyEqual(1.0, len, 1e-9,
                    $"Vertex[{i}] (segment normal) must be unit length. Got {len:G10}.");
            }
        }

        // ─── Miter fallback to bevel ───────────────────────────────────────────────────────

        [Test]
        public void SharpAngle_MiterFallback_NoBevelVertexExceedsMiterLimit()
        {
            // Very sharp angle (~10°) with miterLimit=2.
            // Miter factor for 10° turn = 1/cos(5°) ≈ 1.004 (well under 2) → still miter.
            // Use a near-U-turn (170°) to force bevel: 1/cos(85°) ≈ 11.5 > 2.
            double rad = Math.PI * 170.0 / 180.0; // 170° external angle
            double x2  = Math.Cos(rad) * 10.0;
            double y2  = Math.Sin(rad) * 10.0;
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(10 + x2, y2) };

            var r = LineTessellator.Triangulate(pts, JoinType.Miter, CapType.Butt, miterLimit: 2.0);

            // With bevel fallback: no normal length should exceed miterLimit * sqrt(2) (sanity bound).
            double hard = 2.0 * Math.Sqrt(2.0);
            foreach (var v in r.Vertices)
            {
                double len = VecLen(v.Normal);
                Assert.That(len, Is.LessThan(hard + 1e-6),
                    $"After miter→bevel fallback: no normal should exceed miterLimit×√2. " +
                    $"Got |normal|={len:G10} for normal=({v.Normal.x:G},{v.Normal.y:G}).");
            }
        }

        [Test]
        public void SharpAngle_ExplicitBevel_VertexCountExactlyOneBevelExtra()
        {
            // One bevel join at (10,0) for a ~90° left-turn adds 3 join verts (outerA, inner, outerB)
            // instead of 2 miter verts → net +1 vertex and +3 extra indices (1 bevel triangle).
            // 3-pt bevel: start_cap(2) + bevel(3) + last_seg(2) = 7 verts.
            //             first_quad(6) + bevel_tri(3) + last_quad(6) = 15 indices.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) },
                JoinType.Bevel, CapType.Butt);

            Assert.AreEqual(7, r.Vertices.Length,
                $"3-pt bevel → 7 verts. Got {r.Vertices.Length}.");
            Assert.AreEqual(15, r.Indices.Length,
                $"3-pt bevel → 15 indices. Got {r.Indices.Length}.");
        }

        // ─── Round join ────────────────────────────────────────────────────────────────────

        [Test]
        public void RightAngle_RoundJoin_ExactVertexCount()
        {
            // Round join at (10,0) with roundSegments=4:
            //   Join verts = 1 inner + 1 arcStart + 4 fan intermediates + 1 arcEnd = 7 verts.
            //   Plus: start_cap(2) + last_seg(2) = 4.
            //   Total: 4 + 7 = 11 verts.
            // Indices:
            //   first quad (to arcStartIdx and innerIdx): 6
            //   4 fan triangles: 12
            //   1 last fan triangle: 3
            //   last segment quad: 6
            //   Total: 27 indices.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) },
                JoinType.Round, CapType.Butt, roundSegments: 4);

            Assert.AreEqual(11, r.Vertices.Length,
                $"3-pt round join (roundSegments=4) → 11 verts. Got {r.Vertices.Length}.");
            Assert.AreEqual(27, r.Indices.Length,
                $"3-pt round join (roundSegments=4) → 27 indices. Got {r.Indices.Length}.");
        }

        // ─── Square cap ────────────────────────────────────────────────────────────────────

        [Test]
        public void SingleSegment_SquareCap_ExactVertexCount()
        {
            // Square start cap: 2 verts (left, right with offset normals).
            // Last segment: 2 verts (butt at terminal).
            // End square cap: 2 more verts + 1 quad = 2 verts.
            // Total: 2 + 2 + 2 = 6 verts, 6+6=12 indices.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Square);

            Assert.AreEqual(6, r.Vertices.Length,
                $"2-pt square caps → 6 verts. Got {r.Vertices.Length}.");
            Assert.AreEqual(12, r.Indices.Length,
                $"2-pt square caps → 12 indices. Got {r.Indices.Length}.");
        }

        // ─── Round cap ─────────────────────────────────────────────────────────────────────

        [Test]
        public void SingleSegment_RoundCap_ExactVertexCount()
        {
            // Round start cap (roundSegments=4) — center-pivot structure:
            //   1 center pivot (side=0, normal=(0,0)) +
            //   4 arc intermediates (side=+1, rim) +
            //   1 leftButt (side=+1) + 1 rightButt (side=−1) = 7 verts.
            //   Fan triangles: 4 (one per roundSegments) + 1 final = 5 triangles → 15 indices.
            //
            // Last segment emit (butt): 2 verts + quad(6 indices).
            //
            // Round end cap (roundSegments=4):
            //   1 center pivot + 4 arc intermediates = 5 verts.
            //   Triangles: 4 + 1 final = 5 triangles → 15 indices.
            //
            // Total verts: 7 (start cap) + 2 (terminal butt) + 5 (end cap) = 14.
            // Total indices: 15 (start fan) + 6 (first quad) + 15 (end fan) = 36.
            //
            // Note: this count is a supplementary pin. The geometric correctness check
            // (winding, extent, no degenerate/duplicate-index triangles) lives in
            // AllJoinCapCombinations_PositiveWindingNoDegenerate and RoundCap_* tests.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Round, roundSegments: 4);

            Assert.AreEqual(14, r.Vertices.Length,
                $"2-pt round caps (roundSegments=4) → 14 verts. Got {r.Vertices.Length}.");
            Assert.AreEqual(36, r.Indices.Length,
                $"2-pt round caps (roundSegments=4) → 36 indices. Got {r.Indices.Length}.");
        }

        // ─── NeedsBevel static accessor ────────────────────────────────────────────────────

        [Test]
        public void NeedsBevel_ParallelSegments_ReturnsFalse()
        {
            double2 n = new double2(0, 1);
            Assert.IsFalse(LineTessellator.NeedsBevel(n, n, 2.0),
                "Parallel segments → miter factor = 1 → no bevel needed.");
        }

        [Test]
        public void NeedsBevel_RightAngle_BelowDefaultLimit_ReturnsFalse()
        {
            // 90° left turn: n1=(0,1), n2=(−1,0). Miter factor = 1/cos(45°) = √2 ≈ 1.414.
            // Default miterLimit=2: √2 < 2 → NeedsBevel = false.
            double2 n1 = new double2(0, 1);
            double2 n2 = new double2(-1, 0);
            Assert.IsFalse(LineTessellator.NeedsBevel(n1, n2, 2.0),
                "90° turn: miter factor √2 < limit 2 → NeedsBevel should be false.");
        }

        [Test]
        public void NeedsBevel_VerySharpAngle_ExceedsLimit_ReturnsTrue()
        {
            // Near 180° turn: n1=(0,1), n2≈(0,−1). Miter factor → ∞ → bevel needed.
            double2 n1 = new double2(0, 1);
            double2 n2 = new double2(0.01, -0.9999); // ~179° turn
            Assert.IsTrue(LineTessellator.NeedsBevel(n1, n2, 2.0),
                "Near-180° turn: miter factor >> 2 → NeedsBevel should be true.");
        }

        [Test]
        public void MiterFactor_RightAngle_IsSqrt2()
        {
            // 90° left turn: n1=(0,1), n2=(−1,0). Factor = 1/cos(45°) = √2.
            double2 n1 = new double2(0, 1);
            double2 n2 = new double2(-1, 0);
            double factor = MiterFactor(n1, n2);
            AssertNearlyEqual(Math.Sqrt(2.0), factor, 1e-9,
                $"90° miter factor should be √2. Got {factor:G10}.");
        }

        // ─── Winding + extent assertions for all JoinType × CapType combinations ──────────
        //
        // Geometry contract verified here (not just vertex counts):
        //   (a) No degenerate triangles (no zero-area after shader extrusion at halfWidth=2).
        //   (b) No duplicate-index triangles (index[3k]==index[3k+1] or ==index[3k+2] or etc.).
        //   (c) All triangles have POSITIVE signed area after extrusion (consistent CCW winding).
        //   (d) For non-Butt caps: the cap's extruded vertices extend BEYOND the endpoint in the
        //       outward tangent direction (start cap: dot(extruded−p0, −tangent)≥0;
        //       end cap: dot(extruded−pN, +tangent)≥0).
        //
        // Line: (0,0)→(10,0). halfWidth=2. Tangent=(1,0), leftNormal=(0,1).

        private const double HalfWidth = 2.0;

        /// <summary>
        /// Extrude a vertex's position by its normal * halfWidth and return the world position.
        /// Matches the vertex shader: worldPos += normal * 0.5 * width = normal * halfWidth.
        /// </summary>
        private static double2 Extrude(LineVertex v)
            => new double2(v.Position.x + v.Normal.x * HalfWidth,
                           v.Position.y + v.Normal.y * HalfWidth);

        /// <summary>
        /// Signed area of extruded triangle (positive = CCW).
        /// Using 2D cross product: 0.5 * ((b-a) × (c-a)).
        /// </summary>
        private static double TriSignedArea(double2 a, double2 b, double2 c)
            => 0.5 * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));

        private static void AssertGeometryValid(
            LineTessellator.Result r,
            string label,
            bool checkCapExtent = false,
            double2 startPt = default,
            double2 startTangent = default,
            double2 endPt = default,
            double2 endTangent = default)
        {
            Assert.Greater(r.Vertices.Length, 0, $"{label}: must have vertices.");
            Assert.Greater(r.Indices.Length, 0,  $"{label}: must have indices.");
            Assert.AreEqual(0, r.Indices.Length % 3, $"{label}: index count must be multiple of 3.");

            int triCount = r.Indices.Length / 3;
            int posCount = 0, negCount = 0, zeroCount = 0;
            var dupList  = new List<string>();

            for (int t = 0; t < triCount; t++)
            {
                int i0 = r.Indices[t * 3];
                int i1 = r.Indices[t * 3 + 1];
                int i2 = r.Indices[t * 3 + 2];

                // Duplicate-index check.
                if (i0 == i1 || i1 == i2 || i0 == i2)
                    dupList.Add($"tri[{t}]({i0},{i1},{i2})");

                // Signed-area check after shader extrusion.
                double2 a = Extrude(r.Vertices[i0]);
                double2 b = Extrude(r.Vertices[i1]);
                double2 c = Extrude(r.Vertices[i2]);
                double area = TriSignedArea(a, b, c);

                if (area > 1e-9)       posCount++;
                else if (area < -1e-9) negCount++;
                else                   zeroCount++;
            }

            Assert.AreEqual(0, dupList.Count,
                $"{label}: {dupList.Count} duplicate-index triangle(s): {string.Join(", ", dupList)}.");
            Assert.AreEqual(0, zeroCount,
                $"{label}: {zeroCount}/{triCount} degenerate (zero-area) triangles after extrusion.");
            Assert.AreEqual(0, negCount,
                $"{label}: {negCount}/{triCount} negative-winding (CW) triangles after extrusion. " +
                $"All must be CCW (positive area). pos={posCount} neg={negCount} zero={zeroCount}.");

            if (checkCapExtent)
            {
                // For non-Butt caps: verify that at least one vertex per cap lies beyond the endpoint
                // in the outward tangent direction (i.e. the cap arcs actually bulge outward).
                //
                // Start cap: outward = −startTangent; check dot(extrude(v) − startPt, −startTangent) > 0
                // for at least one vertex.
                bool startCapOk = false;
                bool endCapOk   = false;
                double2 outwardStart = new double2(-startTangent.x, -startTangent.y);
                double2 outwardEnd   = endTangent; // +tangent for end

                foreach (var v in r.Vertices)
                {
                    double2 ext = Extrude(v);

                    // Start cap: vertex extruded beyond start point in backward direction.
                    double2 ds = new double2(ext.x - startPt.x, ext.y - startPt.y);
                    if (ds.x * outwardStart.x + ds.y * outwardStart.y > 1e-6)
                        startCapOk = true;

                    // End cap: vertex extruded beyond end point in forward direction.
                    double2 de = new double2(ext.x - endPt.x, ext.y - endPt.y);
                    if (de.x * outwardEnd.x + de.y * outwardEnd.y > 1e-6)
                        endCapOk = true;
                }

                Assert.IsTrue(startCapOk,
                    $"{label}: start cap must have at least one vertex extruding beyond p0 " +
                    $"in the backward tangent direction (outward = {outwardStart.x:G},{outwardStart.y:G}). " +
                    $"Start cap geometry is on the wrong side (body side).");
                Assert.IsTrue(endCapOk,
                    $"{label}: end cap must have at least one vertex extruding beyond p1 " +
                    $"in the forward tangent direction (outward = {outwardEnd.x:G},{outwardEnd.y:G}). " +
                    $"End cap geometry is on the wrong side (body side).");
            }
        }

        // Horizontal line: (0,0) → (10,0). tangent=(1,0), leftNormal=(0,1).
        private static readonly double2 LineStart   = new double2(0,  0);
        private static readonly double2 LineEnd     = new double2(10, 0);
        private static readonly double2 LineTangent = new double2(1,  0);

        [Test]
        public void AllJoinCapCombinations_PositiveWindingNoDegenerate()
        {
            // Test all 9 combinations of JoinType × CapType on the horizontal line (0,0)→(10,0).
            // Butt caps: no cap-extent check needed (butt is flush at the endpoint).
            // Round/Square caps: verify extent check (cap must bulge outward).
            var joins = new[] { JoinType.Miter, JoinType.Bevel, JoinType.Round };
            var caps  = new[] { CapType.Butt, CapType.Square, CapType.Round };

            var hline = new[] { LineStart, LineEnd };

            foreach (var join in joins)
            {
                foreach (var cap in caps)
                {
                    string label = $"hline {join}/{cap}";
                    var r = LineTessellator.Triangulate(hline, join, cap, roundSegments: 4);

                    bool needsExtentCheck = (cap == CapType.Round || cap == CapType.Square);
                    AssertGeometryValid(r, label, needsExtentCheck,
                        startPt: LineStart, startTangent: LineTangent,
                        endPt: LineEnd, endTangent: LineTangent);
                }
            }
        }

        [Test]
        public void AllJoinCapCombinations_LShape_PositiveWindingNoDegenerate()
        {
            // L-shaped line to exercise join geometry across cap types.
            // (0,0) → (10,0) → (10,10): left turn at (10,0).
            var pts  = new[] { Pt(0,0), Pt(10,0), Pt(10,10) };
            var joins = new[] { JoinType.Miter, JoinType.Bevel, JoinType.Round };
            var caps  = new[] { CapType.Butt, CapType.Square, CapType.Round };

            foreach (var join in joins)
            {
                foreach (var cap in caps)
                {
                    string label = $"Lshape {join}/{cap}";
                    var r = LineTessellator.Triangulate(pts, join, cap, roundSegments: 4);

                    // Start tangent = (1,0), end tangent = (0,1).
                    bool needsExtentCheck = (cap == CapType.Round || cap == CapType.Square);
                    AssertGeometryValid(r, label, needsExtentCheck,
                        startPt: Pt(0,0),  startTangent: new double2(1,0),
                        endPt: Pt(10,10),  endTangent: new double2(0,1));
                }
            }
        }

        [Test]
        public void RoundCap_StartAndEnd_BulgeOnCorrectSide()
        {
            // Focused regression test for the previously wrong semicircle direction.
            // For (0,0)→(10,0) with halfWidth=2:
            //   Start cap must have at least one extruded vertex with x < 0 (behind start point).
            //   End cap must have at least one extruded vertex with x > 10 (past end point).
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Round, roundSegments: 4);

            bool startBehind = false;
            bool endForward  = false;
            foreach (var v in r.Vertices)
            {
                double2 ext = Extrude(v);
                if (ext.x < -1e-9) startBehind = true; // start cap: x < 0
                if (ext.x > 10.0 + 1e-9) endForward  = true; // end cap: x > 10
            }

            Assert.IsTrue(startBehind,
                "Round start cap must have extruded vertices with x < 0 (behind start point x=0). " +
                "Arc was sweeping through the body side (positive-x region) — should sweep backward.");
            Assert.IsTrue(endForward,
                "Round end cap must have extruded vertices with x > 10 (past end point x=10). " +
                "Arc was sweeping through the body side (x < 10) — should sweep forward.");
        }

        [Test]
        public void RoundCap_PivotVertex_HasZeroNormal_AndZeroSide()
        {
            // The round cap pivot vertex must have normal=(0,0) so the vertex shader keeps it
            // on the centerline, and side=0 so the fragment shader assigns full interior alpha.
            // This ensures AA feathering occurs at the cap rim (side=±1), not at the pivot.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Round, roundSegments: 4);

            bool foundStartPivot = false;
            bool foundEndPivot   = false;

            foreach (var v in r.Vertices)
            {
                if (Math.Abs(v.Side) < 1e-6f)
                {
                    // side≈0 → this is a cap pivot vertex. Check:
                    //   (a) normal ≈ (0,0) (stays on centerline after extrusion).
                    double normalLen = Math.Sqrt(v.Normal.x * v.Normal.x + v.Normal.y * v.Normal.y);
                    Assert.That(normalLen, Is.LessThan(1e-9),
                        $"Cap pivot at {v.Position.x},{v.Position.y}: normal must be (0,0). " +
                        $"Got normal=({v.Normal.x:G},{v.Normal.y:G}), |n|={normalLen:G}.");

                    // Determine if this is the start or end pivot by position.
                    if (Math.Abs(v.Position.x) < 1e-9 && Math.Abs(v.Position.y) < 1e-9)
                        foundStartPivot = true;
                    else if (Math.Abs(v.Position.x - 10.0) < 1e-9 && Math.Abs(v.Position.y) < 1e-9)
                        foundEndPivot = true;
                }
            }

            Assert.IsTrue(foundStartPivot,
                "Round start cap must have a pivot vertex at (0,0) with side≈0 and normal≈(0,0). " +
                "Without it, AA computes edgeDist=0 at the centerline pivot and the cap looks flat.");
            Assert.IsTrue(foundEndPivot,
                "Round end cap must have a pivot vertex at (10,0) with side≈0 and normal≈(0,0). " +
                "Without it, AA computes edgeDist=0 at the centerline pivot and the cap looks flat.");
        }

        // ── Test-local oracle ────────────────────────────────────────────────────────────────────
        /// <summary>
        /// The miter factor (1/cos(θ/2)) for the join between two segment left normals; double.MaxValue
        /// when the normals cancel (180° hairpin) or the join is degenerate.
        ///
        /// <para>Kept HERE rather than in <see cref="LineTessellator"/>, where it was public with no
        /// production caller. It is a third statement of math the tessellator already contains twice —
        /// <c>ComputeMiterNormals</c> derives the same normalised average, and <c>NeedsBevel</c> compares
        /// the same 1/dot against the miter limit. As a test-local restatement it is an oracle: it fails
        /// when the tessellator's own version drifts. As a production member it was just a third copy.</para>
        /// </summary>
        private static double MiterFactor(double2 n1, double2 n2)
        {
            double mx   = n1.x + n2.x;
            double my   = n1.y + n2.y;
            double mLen = math.sqrt(mx * mx + my * my);
            if (mLen < 1e-12) return double.MaxValue;
            double mux = mx / mLen;
            double muy = my / mLen;
            double dot = mux * n1.x + muy * n1.y;
            if (math.abs(dot) < 1e-12) return double.MaxValue;
            return 1.0 / dot;
        }
    }
}
