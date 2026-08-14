using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Tests.Meshing
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

        // ─── Inner-join miter clamp (bevel/round) ─────────────────────────────────────────
        //
        // docs/line-rendering-design.md §3 item 2: the bevel/round INNER vertex must carry the
        // same miter-factor magnitude the miter join already carries (min(1/|cos(θ/2)|, miterLimit)),
        // not a unit vector. See §1/§2 of the stage plan for the derivation.

        /// <summary>
        /// Locates the join's inner (concave) vertex robustly (not by index): the unique vertex sitting
        /// at <paramref name="corner"/> whose <see cref="LineVertex.Side"/> sign matches the turn side
        /// (left turn ⇒ concave is left ⇒ Side == +1; right turn ⇒ concave is right ⇒ Side == −1).
        /// Asserts uniqueness as a precondition so a topology change cannot silently make the caller's
        /// assertions vacuous.
        /// </summary>
        private static int FindInnerVertexIndex(LineVertex[] verts, double2 corner, bool leftTurn)
        {
            float expectedSide = leftTurn ? +1f : -1f;
            int found = -1;
            int count = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                double2 pos = verts[i].Position;
                if (Math.Abs(pos.x - corner.x) < 1e-9 && Math.Abs(pos.y - corner.y) < 1e-9 &&
                    verts[i].Side == expectedSide)
                {
                    found = i;
                    count++;
                }
            }
            Assert.AreEqual(1, count,
                $"Expected exactly one inner-join vertex at corner ({corner.x:G},{corner.y:G}) with " +
                $"Side={expectedSide}. Found {count}.");
            return found;
        }

        [Test]
        public void InnerJoin_Bevel_90LeftTurn_Unclamped_MatchesConcaveOffsetLineIntersection()
        {
            // Bevel, 90° left turn, miterLimit 2.0. Raw factor 1/cos45° = √2 ≈ 1.414 < 2 → UNCLAMPED.
            // n1=(0,1), n2=(-1,0), mu=normalize(n1+n2)=(-1,1)/√2. Left turn ⇒ concave is LEFT ⇒ the
            // inner vertex carries mu·factor directly (not negated): (-1,1).
            var corner = Pt(10, 0);
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) },
                JoinType.Bevel, CapType.Butt, miterLimit: 2.0);

            int idx = FindInnerVertexIndex(r.Vertices, corner, leftTurn: true);
            double2 normal = r.Vertices[idx].Normal;

            AssertNearlyEqual(-1.0, normal.x, 1e-9, $"Inner normal.x should be -1.0. Got {normal.x:G17}.");
            AssertNearlyEqual(1.0, normal.y, 1e-9, $"Inner normal.y should be 1.0. Got {normal.y:G17}.");

            double len = VecLen(normal);
            AssertNearlyEqual(1.4142135623730951, len, 1e-9,
                $"Inner |normal| should be √2 = 1.4142135623730951. Got {len:G17}.");

            // Analytic check, not measured: the extruded inner (concave) vertex must sit at the
            // intersection of the incoming LEFT offset line (y=+2) and the outgoing LEFT offset line
            // (x=8) — §1/§2 of the stage plan. Un-fixed (chamfer on the concave side) extrudes to
            // (12,-2); f1ffe702 (pre-blocked-stage, |N|=1 inner) extrudes to
            // (11.414213562373096, -1.414213562373095) — the CONVEX side, at unit magnitude.
            double2 extruded = Extrude(r.Vertices[idx]);
            AssertNearlyEqual(8.0, extruded.x, 1e-9, $"Extruded inner vertex.x should be 8. Got {extruded.x:G17}.");
            AssertNearlyEqual(2.0, extruded.y, 1e-9, $"Extruded inner vertex.y should be 2. Got {extruded.y:G17}.");
        }

        [Test]
        public void InnerJoin_Round_90RightTurn_Unclamped_MirrorSignBranch()
        {
            // Round, 90° RIGHT turn (mirror of the previous tooth's sign branch), miterLimit 2.0.
            // n1=(0,1), t2=(0,-1) ⇒ n2=(1,0), mu=normalize(n1+n2)=(1,1)/√2. Right turn ⇒ concave is
            // RIGHT ⇒ the inner vertex carries -mu·factor: (-1,-1).
            var corner = Pt(10, 0);
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(10, -10) },
                JoinType.Round, CapType.Butt, roundSegments: 4, miterLimit: 2.0);

            int idx = FindInnerVertexIndex(r.Vertices, corner, leftTurn: false);
            double2 normal = r.Vertices[idx].Normal;

            AssertNearlyEqual(-1.0, normal.x, 1e-9, $"Inner normal.x should be -1.0. Got {normal.x:G17}.");
            AssertNearlyEqual(-1.0, normal.y, 1e-9, $"Inner normal.y should be -1.0. Got {normal.y:G17}.");

            double len = VecLen(normal);
            AssertNearlyEqual(1.4142135623730951, len, 1e-9,
                $"Inner |normal| should be √2 = 1.4142135623730951. Got {len:G17}.");

            // Intersection of the two RIGHT offset lines (y=-2, x=8). Un-fixed extrudes to (12,2);
            // f1ffe702 extrudes to (11.414213562373096, 1.414213562373095) — the mirror of the
            // left-turn case above, on the CONVEX (here: left) side at unit magnitude.
            double2 extruded = Extrude(r.Vertices[idx]);
            AssertNearlyEqual(8.0, extruded.x, 1e-9, $"Extruded inner vertex.x should be 8. Got {extruded.x:G17}.");
            AssertNearlyEqual(-2.0, extruded.y, 1e-9, $"Extruded inner vertex.y should be -2. Got {extruded.y:G17}.");
        }

        [Test]
        public void InnerJoin_Bevel_150LeftTurn_ClampedBranch()
        {
            // Bevel, 150° left turn (external turn angle), miterLimit 2.0.
            // Third point = (10,0) + 30·(cos150°, sin150°) = (-15.980762113533157, 15.0).
            // t2 = (-0.8660254037844387, 0.5); n2 = left normal of t2 = (-0.5, -0.8660254037844387).
            // n1 = (0,1). mu = normalize(n1+n2) = (-0.9659258262890683, 0.2588190451025207).
            // cosHalf = dot(mu,n1) = 0.2588190451025207 = cos75°.
            // Raw factor 1/cos75° = 3.8637033051562737 > 2 → CLAMPED to exactly 2.0.
            // Left turn ⇒ concave is LEFT ⇒ inner vertex carries mu·2.0 directly (not negated):
            // (-1.9318516525781366, 0.5176380902050415).
            // Deliberately NOT asserting the offset-line intersection here: the clamp deliberately
            // falls short of the true miter intersection at this angle — do not "fix" that later.
            var corner = Pt(10, 0);
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(-15.980762113533157, 15.0) },
                JoinType.Bevel, CapType.Butt, miterLimit: 2.0);

            int idx = FindInnerVertexIndex(r.Vertices, corner, leftTurn: true);
            double2 normal = r.Vertices[idx].Normal;

            // Three distinguishable readings: un-fixed (chamfer on concave side) |N|=1; correct
            // (clamped) |N|=2.0; clamp-omitted |N|=3.8637033051562737.
            AssertNearlyEqual(-1.9318516525781366, normal.x, 1e-9, $"Inner normal.x should be -1.9318516525781366. Got {normal.x:G17}.");
            AssertNearlyEqual(0.5176380902050415, normal.y, 1e-9, $"Inner normal.y should be 0.5176380902050415. Got {normal.y:G17}.");

            double len = VecLen(normal);
            AssertNearlyEqual(2.0, len, 1e-9, $"Inner |normal| should be clamped to exactly 2.0. Got {len:G17}.");
        }

        [Test]
        public void InnerJoin_Round_150LeftTurn_ClampedBranch()
        {
            // Same fixture geometry as the bevel clamped tooth, JoinType.Round — covers the round
            // arm of the clamped branch (same ComputeInnerNormal helper, different emission path).
            // Left turn ⇒ round join's inner vertex also carries mu·2.0 directly (not negated) — same
            // values as the bevel tooth above.
            var corner = Pt(10, 0);
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(-15.980762113533157, 15.0) },
                JoinType.Round, CapType.Butt, roundSegments: 4, miterLimit: 2.0);

            int idx = FindInnerVertexIndex(r.Vertices, corner, leftTurn: true);
            double2 normal = r.Vertices[idx].Normal;

            AssertNearlyEqual(-1.9318516525781366, normal.x, 1e-9, $"Inner normal.x should be -1.9318516525781366. Got {normal.x:G17}.");
            AssertNearlyEqual(0.5176380902050415, normal.y, 1e-9, $"Inner normal.y should be 0.5176380902050415. Got {normal.y:G17}.");

            double len = VecLen(normal);
            AssertNearlyEqual(2.0, len, 1e-9, $"Inner |normal| should be clamped to exactly 2.0. Got {len:G17}.");
        }

        [Test]
        public void MiterJoin_Untouched_InvariantTooth()
        {
            // This is the "did the refactor stay bit-identical" tooth, not a regression tooth — it
            // must read the same before and after every edit in this stage. 1e-12 (tighter than the
            // clamp teeth's 1e-9): mux*miterFactor is a divide-then-multiply, so it lands within
            // ~1e-16 of ±1.0 but is not guaranteed bit-exact; a flaky invariant tooth is worse than none.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) },
                JoinType.Miter, CapType.Butt, miterLimit: 10.0);

            // Join vertices are verts[2] (left, +1) and verts[3] (right, -1).
            AssertNearlyEqual(-1.0, r.Vertices[2].Normal.x, 1e-12, $"Miter left.x should be -1.0. Got {r.Vertices[2].Normal.x:G17}.");
            AssertNearlyEqual(1.0, r.Vertices[2].Normal.y, 1e-12, $"Miter left.y should be 1.0. Got {r.Vertices[2].Normal.y:G17}.");
            AssertNearlyEqual(1.0, r.Vertices[3].Normal.x, 1e-12, $"Miter right.x should be 1.0. Got {r.Vertices[3].Normal.x:G17}.");
            AssertNearlyEqual(-1.0, r.Vertices[3].Normal.y, 1e-12, $"Miter right.y should be -1.0. Got {r.Vertices[3].Normal.y:G17}.");
        }

        // ─── Join side — chamfer/arc on the CONVEX side (region-membership teeth) ────────────
        //
        // The teeth above (E18-E23) pin normal LENGTHS and POSITIONS but never which side of the
        // band a join vertex lands on — the exact gap that let the inner/outer inversion ship. These
        // teeth compute the two half-width bands from the fixture geometry directly and assert region
        // membership, independent of the production code's own normal arithmetic.

        /// <summary>Closed-rectangle membership test with tolerance <paramref name="eps"/>.</summary>
        private static bool InClosed(double2 q, double2 rMin, double2 rMax, double eps)
            => q.x >= rMin.x - eps && q.x <= rMax.x + eps &&
               q.y >= rMin.y - eps && q.y <= rMax.y + eps;

        /// <summary>
        /// All corner-position vertices (within 1e-9) whose <see cref="LineVertex.Side"/> equals
        /// <paramref name="side"/>, in emission order (ascending index — the array is built by
        /// sequential <c>List.Add</c>, so index order IS emission order).
        /// </summary>
        private static List<int> CornerVerticesBySide(LineVertex[] verts, double2 corner, float side)
        {
            var found = new List<int>();
            for (int i = 0; i < verts.Length; i++)
            {
                double2 pos = verts[i].Position;
                if (Math.Abs(pos.x - corner.x) < 1e-9 && Math.Abs(pos.y - corner.y) < 1e-9 &&
                    verts[i].Side == side)
                    found.Add(i);
            }
            return found;
        }

        [Test]
        public void JoinSide_BevelAndRound_ChamferIsOnTheConvexSide()
        {
            // Fixture A: (0,0)→(10,0)→(10,10), h=2 (HalfWidth), miterLimit=2.0, roundSegments=4.
            // R1 = [0,10]×[-2,2] (segment 1's band), R2 = [8,12]×[0,10] (segment 2's band).
            var r1Min = Pt(0, -2);  var r1Max = Pt(10, 2);
            var r2Min = Pt(8, 0);   var r2Max = Pt(12, 10);
            var corner = Pt(10, 0);
            var mu = new double2(-0.7071067811865476, 0.7071067811865476);
            const double eps = 1e-9;

            var fixtureA = new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) };

            // T1a — the concave vertex must sit INSIDE both bands (the overlap), and on the mu side.
            var bevelA = LineTessellator.Triangulate(fixtureA, JoinType.Bevel, CapType.Butt, miterLimit: 2.0);
            int concaveIdx = FindInnerVertexIndex(bevelA.Vertices, corner, leftTurn: true);
            double2 concaveExtruded = Extrude(bevelA.Vertices[concaveIdx]);
            double dotMu = (concaveExtruded.x - corner.x) * mu.x + (concaveExtruded.y - corner.y) * mu.y;

            AssertNearlyEqual(8.0, concaveExtruded.x, eps, $"T1a: concave vertex.x should be 8.0. Got {concaveExtruded.x:G17}.");
            AssertNearlyEqual(2.0, concaveExtruded.y, eps, $"T1a: concave vertex.y should be 2.0. Got {concaveExtruded.y:G17}.");
            AssertNearlyEqual(2.8284271247461903, dotMu, eps, $"T1a: dot(Extrude-C, mu) should be 2.8284271247461903. Got {dotMu:G17}.");
            Assert.Greater(dotMu, 0.0, "T1a: concave vertex must be on the mu (turn) side.");
            Assert.IsTrue(InClosed(concaveExtruded, r1Min, r1Max, eps), "T1a: concave vertex must lie inside R1 (band overlap).");
            Assert.IsTrue(InClosed(concaveExtruded, r2Min, r2Max, eps), "T1a: concave vertex must lie inside R2 (band overlap).");

            // T1b — bevel chamfer chord: the two convex unit-normal vertices' midpoint must sit
            // strictly OUTSIDE both bands (the uncovered wedge).
            var outerVerts = CornerVerticesBySide(bevelA.Vertices, corner, -1f);
            Assert.AreEqual(2, outerVerts.Count, $"T1b: expected exactly 2 convex (side=-1) bevel vertices at the corner. Found {outerVerts.Count}.");
            double2 outerA = Extrude(bevelA.Vertices[outerVerts[0]]);
            double2 outerB = Extrude(bevelA.Vertices[outerVerts[1]]);
            double2 chordMid = new double2((outerA.x + outerB.x) / 2.0, (outerA.y + outerB.y) / 2.0);

            AssertNearlyEqual(10.0, outerA.x, eps, $"T1b: outerA.x should be 10.0. Got {outerA.x:G17}.");
            AssertNearlyEqual(-2.0, outerA.y, eps, $"T1b: outerA.y should be -2.0. Got {outerA.y:G17}.");
            AssertNearlyEqual(12.0, outerB.x, eps, $"T1b: outerB.x should be 12.0. Got {outerB.x:G17}.");
            AssertNearlyEqual(0.0, outerB.y, eps, $"T1b: outerB.y should be 0.0. Got {outerB.y:G17}.");
            AssertNearlyEqual(11.0, chordMid.x, eps, $"T1b: chord midpoint.x should be 11.0. Got {chordMid.x:G17}.");
            AssertNearlyEqual(-1.0, chordMid.y, eps, $"T1b: chord midpoint.y should be -1.0. Got {chordMid.y:G17}.");
            Assert.IsFalse(InClosed(chordMid, r1Min, r1Max, -eps), "T1b: chamfer chord midpoint must lie strictly outside R1.");
            Assert.IsFalse(InClosed(chordMid, r2Min, r2Max, -eps), "T1b: chamfer chord midpoint must lie strictly outside R2.");

            // T1c — round arc: all 4 arc-intermediate vertices must be strictly outside both bands.
            var roundA = LineTessellator.Triangulate(fixtureA, JoinType.Round, CapType.Butt, roundSegments: 4, miterLimit: 2.0);
            var arcSideVerts = CornerVerticesBySide(roundA.Vertices, corner, -1f);
            Assert.AreEqual(6, arcSideVerts.Count, $"T1c: expected 6 convex-side (side=-1) round-join vertices (arcStart + 4 intermediates + arcEnd). Found {arcSideVerts.Count}.");

            double[] expectedX = { 10.6180339887, 11.1755705046, 11.6180339887, 11.9021130326 };
            double[] expectedY = { -1.9021130326, -1.6180339887, -1.1755705046, -0.6180339887 };
            double minMargin = double.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                // arcSideVerts[0] = arcStart, [1..4] = fan intermediates, [5] = arcEnd.
                double2 fanPos = Extrude(roundA.Vertices[arcSideVerts[k + 1]]);
                AssertNearlyEqual(expectedX[k], fanPos.x, 1e-6, $"T1c: arc intermediate[{k}].x. Got {fanPos.x:G17}.");
                AssertNearlyEqual(expectedY[k], fanPos.y, 1e-6, $"T1c: arc intermediate[{k}].y. Got {fanPos.y:G17}.");
                Assert.IsFalse(InClosed(fanPos, r1Min, r1Max, -eps), $"T1c: arc intermediate[{k}] must lie strictly outside R1. Got ({fanPos.x:G},{fanPos.y:G}).");
                Assert.IsFalse(InClosed(fanPos, r2Min, r2Max, -eps), $"T1c: arc intermediate[{k}] must lie strictly outside R2. Got ({fanPos.x:G},{fanPos.y:G}).");

                // Distance to the UNION is the MIN of the two box distances, not the max: a point clear of
                // R1 by 1.902 but of R2 by only 0.618 is 0.618 from the union. (Using max here read 1.618
                // and over-claimed the margin by ~2.6× — review NIT.) Both points are outside both boxes
                // on one axis only, so the per-axis excess IS the box distance.
                double marginR1 = fanPos.x - 10.0;   // clear of R1 to the right
                double marginR2 = -fanPos.y;         // clear of R2 below
                double margin = Math.Min(marginR1, marginR2);
                if (margin < minMargin) minMargin = margin;
            }
            Assert.Greater(minMargin, 0.6, $"T1c: minimum distance from R1∪R2 should be ~0.618. Got {minMargin:G17}.");

            // T1d — right-turn mirror (Fixture B), closes the single-branch coverage hole (§3.6).
            var fixtureB = new[] { Pt(0, 0), Pt(10, 0), Pt(10, -10) };
            var bevelB = LineTessellator.Triangulate(fixtureB, JoinType.Bevel, CapType.Butt, miterLimit: 2.0);
            int concaveIdxB = FindInnerVertexIndex(bevelB.Vertices, corner, leftTurn: false);
            double2 concaveExtrudedB = Extrude(bevelB.Vertices[concaveIdxB]);
            var r2MinB = Pt(8, -10); var r2MaxB = Pt(12, 0);

            AssertNearlyEqual(8.0, concaveExtrudedB.x, eps, $"T1d: concave vertex.x should be 8.0. Got {concaveExtrudedB.x:G17}.");
            AssertNearlyEqual(-2.0, concaveExtrudedB.y, eps, $"T1d: concave vertex.y should be -2.0. Got {concaveExtrudedB.y:G17}.");
            Assert.IsTrue(InClosed(concaveExtrudedB, r1Min, r1Max, eps), "T1d: concave vertex must lie inside R1.");
            Assert.IsTrue(InClosed(concaveExtrudedB, r2MinB, r2MaxB, eps), "T1d: concave vertex must lie inside R2 (mirrored band).");

            var outerVertsB = CornerVerticesBySide(bevelB.Vertices, corner, +1f);
            Assert.AreEqual(2, outerVertsB.Count, $"T1d: expected exactly 2 convex (side=+1) bevel vertices at the corner. Found {outerVertsB.Count}.");
            double2 outerAB = Extrude(bevelB.Vertices[outerVertsB[0]]);
            double2 outerBB = Extrude(bevelB.Vertices[outerVertsB[1]]);
            double2 chordMidB = new double2((outerAB.x + outerBB.x) / 2.0, (outerAB.y + outerBB.y) / 2.0);

            AssertNearlyEqual(11.0, chordMidB.x, eps, $"T1d: chord midpoint.x should be 11.0. Got {chordMidB.x:G17}.");
            AssertNearlyEqual(1.0, chordMidB.y, eps, $"T1d: chord midpoint.y should be 1.0. Got {chordMidB.y:G17}.");
            Assert.IsFalse(InClosed(chordMidB, r1Min, r1Max, -eps), "T1d: chamfer chord midpoint must lie strictly outside R1.");
            Assert.IsFalse(InClosed(chordMidB, r2MinB, r2MaxB, -eps), "T1d: chamfer chord midpoint must lie strictly outside R2 (mirrored band).");
        }

        [Test]
        public void JoinVertices_NormalTimesSide_IsUnchanged()
        {
            // Trap 1 (plan §3.1): operations (a) negate-the-normal and (b) flip-the-side-tag must be
            // performed TOGETHER, so Normal × Side is unchanged vertex-for-vertex at every join vertex.
            // This is an INVARIANT tooth — relative to the inner-magnitude change already in the tree it
            // reads GREEN both before and after the side swap (only RED if a developer does (a) without
            // (b)). It is NOT green against the pre-magnitude baseline, where the inner normal was unit
            // length and Normal×Side at the corner reads (−0.7071, 0.7071) against the expected (−1, 1).
            // It does not itself prove the join-side correction, which is what
            // JoinSide_BevelAndRound_ChamferIsOnTheConvexSide pins.
            var corner = Pt(10, 0);
            var fixtureA = new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) };
            var r = LineTessellator.Triangulate(fixtureA, JoinType.Bevel, CapType.Butt, miterLimit: 2.0);

            var cornerVerts = new List<int>();
            for (int i = 0; i < r.Vertices.Length; i++)
            {
                double2 pos = r.Vertices[i].Position;
                if (Math.Abs(pos.x - corner.x) < 1e-9 && Math.Abs(pos.y - corner.y) < 1e-9)
                    cornerVerts.Add(i);
            }
            Assert.AreEqual(3, cornerVerts.Count, $"Expected exactly 3 bevel join vertices at the corner. Found {cornerVerts.Count}.");

            double2[] expected = { new double2(0, 1), new double2(-1, 1), new double2(-1, 0) };
            for (int k = 0; k < 3; k++)
            {
                var v = r.Vertices[cornerVerts[k]];
                double2 normalTimesSide = new double2(v.Normal.x * v.Side, v.Normal.y * v.Side);
                AssertNearlyEqual(expected[k].x, normalTimesSide.x, 1e-9,
                    $"Normal×Side[{k}].x should be {expected[k].x:G17}. Got {normalTimesSide.x:G17}.");
                AssertNearlyEqual(expected[k].y, normalTimesSide.y, 1e-9,
                    $"Normal×Side[{k}].y should be {expected[k].y:G17}. Got {normalTimesSide.y:G17}.");
            }

            // Additional invariant: dot(Normal, mu) > 0 ⟺ Side == +1, over every corner vertex, bevel
            // and round both. mu is the incoming/outgoing bisector, recomputed per-fixture below.
            AssertNormalMuSideCorrelation(fixtureA, JoinType.Bevel, corner);
            AssertNormalMuSideCorrelation(fixtureA, JoinType.Round, corner);
        }

        private static void AssertNormalMuSideCorrelation(double2[] pts, JoinType joinType, double2 corner)
        {
            var r = LineTessellator.Triangulate(pts, joinType, CapType.Butt, roundSegments: 4, miterLimit: 2.0);
            // mu for this corner (fixture A): normalize(n1+n2) = (-0.7071067811865476, 0.7071067811865476).
            var mu = new double2(-0.7071067811865476, 0.7071067811865476);

            int asserted = 0, ambiguous = 0;
            for (int i = 0; i < r.Vertices.Length; i++)
            {
                double2 pos = r.Vertices[i].Position;
                if (Math.Abs(pos.x - corner.x) > 1e-9 || Math.Abs(pos.y - corner.y) > 1e-9)
                    continue;
                if (r.Vertices[i].Side == 0f) continue; // fan pivot, not a corner-offset vertex.

                double dot = r.Vertices[i].Normal.x * mu.x + r.Vertices[i].Normal.y * mu.y;
                bool sidePositive = r.Vertices[i].Side > 0f;
                // Exclude near-zero dot (arc vertices near the mu-perpendicular) from the strict
                // correlation — only assert where the sign is unambiguous.
                if (Math.Abs(dot) < 1e-6) { ambiguous++; continue; }
                Assert.AreEqual(dot > 0.0, sidePositive,
                    $"{joinType} vertex[{i}]: dot(Normal,mu)={dot:G17} but Side={r.Vertices[i].Side} — sign mismatch.");
                asserted++;
            }

            // The skip above is an escape hatch for a geometry that does not currently occur (min |dot| is
            // 0.7071 on this fixture for both join types). Pin that, so a future join topology cannot
            // hollow the correlation out silently.
            Assert.AreEqual(0, ambiguous,
                $"{joinType}: {ambiguous} corner vertices were skipped as sign-ambiguous — the correlation " +
                "is no longer checking what it claims. Re-derive it against the new join topology.");
            Assert.Greater(asserted, 0, $"{joinType}: no corner vertices were checked — the tooth is vacuous.");
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
            //   1 capSeed (side=+1, co-located with rightButt — the fan seed) +
            //   1 leftButt (side=+1) + 1 rightButt (side=−1) = 8 verts.
            //   Fan triangles: 4 (one per roundSegments) + 1 final = 5 triangles → 15 indices.
            //
            // Last segment emit (butt): 2 verts + quad(6 indices).
            //
            // Round end cap (roundSegments=4):
            //   1 center pivot + 4 arc intermediates + 1 capSeed (co-located with rightPrev) = 6 verts.
            //   Triangles: 4 + 1 final = 5 triangles → 15 indices.
            //
            // Total verts: 8 (start cap) + 2 (terminal butt) + 6 (end cap) = 16.
            // Total indices: 15 (start fan) + 6 (first quad) + 15 (end fan) = 36.
            //
            // The two capSeeds are the +1-per-round-capped-end the |side| tagging fix adds. They are pure
            // re-tags — same position, same normal as the vertex they shadow — so the INDEX count is
            // unchanged: no new triangles, only a different vertex referenced by one existing triangle.
            //
            // Note: this count is a supplementary pin. The geometric correctness check
            // (winding, extent, no degenerate/duplicate-index triangles) lives in
            // AllJoinCapCombinations_PositiveWindingNoDegenerate and RoundCap_* tests.
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Round, roundSegments: 4);

            Assert.AreEqual(16, r.Vertices.Length,
                $"2-pt round caps (roundSegments=4) → 16 verts. Got {r.Vertices.Length}.");
            Assert.AreEqual(36, r.Indices.Length,
                $"2-pt round caps (roundSegments=4) → 36 indices. Got {r.Indices.Length}.");
        }

        /// <summary>
        /// Every cap-fan triangle must carry both of its RIM vertices at the same <c>Side</c> SIGN.
        ///
        /// <para>This is the semantic the capSeed vertex exists for, and the only assertion in the tree that
        /// reads the seed's sign — vertex/index counts, positions and winding are all identical whichever way
        /// it is tagged. Seeding the fan from the ribbon's <c>rightButt</c>/<c>rightPrev</c> (which the
        /// adjoining quad needs tagged −1) gives one triangle per capped end a <c>+1 → −1</c> outer edge.
        /// That edge is a true silhouette, but <c>|side|</c> passes through 0 at its midpoint, so anything
        /// keyed on <c>|side|</c> — line-blur today, the AA coverage ramp later — reads it as deep interior
        /// and leaves that arc segment hard.</para>
        ///
        /// <para>Pinned on the managed side only, deliberately: <c>LineRibbonJobTests.AssertParity</c> holds
        /// the Burst <c>LineRibbonJob</c> to this tessellator on EXACT <c>Side</c> for
        /// <c>RoundCap_Parity</c> and <c>RoundCap_And_RoundJoin_Parity</c>, so the job inherits the guarantee
        /// differentially. Do not add a redundant copy on the job side.</para>
        /// </summary>
        [Test]
        public void RoundCap_FanTriangles_HaveUniformRimSide()
        {
            var r = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(10, 0) },
                JoinType.Miter, CapType.Round, roundSegments: 4);

            int fanTrianglesChecked = 0;
            for (int t = 0; t < r.Indices.Length / 3; t++)
            {
                LineVertex a = r.Vertices[r.Indices[t * 3]];
                LineVertex b = r.Vertices[r.Indices[t * 3 + 1]];
                LineVertex c = r.Vertices[r.Indices[t * 3 + 2]];

                // The pivot (side=0, normal=(0,0)) leads every cap-fan triangle and appears in no other
                // triangle, so it is what identifies one.
                if (Math.Abs(a.Side) > 1e-6f) continue;
                fanTrianglesChecked++;

                Assert.AreEqual(Math.Sign(b.Side), Math.Sign(c.Side),
                    $"Cap-fan tri[{t}] rim sides are {b.Side} and {c.Side}: its outer edge is a true " +
                    "silhouette but |side| dips to 0 at the midpoint, so the cap renders that arc segment " +
                    "as deep interior.");
            }

            // Precondition, not a bonus assertion: without it the loop asserts nothing the moment the cap
            // topology changes shape.
            Assert.AreEqual(10, fanTrianglesChecked,
                "Both round caps must contribute 5 pivot-rooted fan triangles (roundSegments=4 ⇒ 4 arc " +
                $"triangles + 1 closing triangle each). Found {fanTrianglesChecked}.");
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

        /// <summary>
        /// The hairpin case the magnitude compare exists for. Near 180° the bisector <c>normalize(n1+n2)</c>
        /// is dominated by numerical residual and can point OPPOSITE <c>n1</c>, making <c>cos(θ/2)</c> a small
        /// NEGATIVE — so a signed <c>1/cos > limit</c> gate lets a huge negative factor through and the miter
        /// normal explodes ("line across the whole screen"). The sibling above does NOT discriminate this: its
        /// residual happens to land positive, so it passes under the signed gate too.
        ///
        /// <para>Added in the IR C1 fix stage, when <c>Tools/core-tests/LineMiterHairpinTests.cs</c> — which
        /// caught this over real boundary tiles but had stopped compiling and stopped running — was retired.
        /// Production's own mirror of this gate (<c>LineRibbonJob.NeedsBevel</c>) stays covered end-to-end by
        /// <c>BoundaryGlitchMeshTests</c> over the same three fixtures; this keeps the managed oracle, which
        /// has no production caller, pinned at a value that tells the two comparisons apart.</para>
        /// </summary>
        [Test]
        public void NeedsBevel_HairpinWithNegativeHalfAngleCosine_StillReturnsTrue()
        {
            // Left normals that are near-antipodal but not exactly so — the shape real projected geometry
            // hands the tessellator at a doubled-back boundary ring.
            double2 n1 = new double2(0, 1);
            double2 n2 = new double2(1e-9, -1.0000000001);

            // Precondition, not decoration: this input is only discriminating while the SIGNED factor is
            // negative. If the arithmetic ever changes so it is not, this test stops testing anything.
            double signedFactor = MiterFactor(n1, n2);
            Assert.Less(signedFactor, 0.0,
                $"Precondition: this input must drive cos(θ/2) NEGATIVE for the signed-vs-magnitude " +
                $"distinction to exist. Signed miter factor was {signedFactor:G6}.");
            Assert.Greater(math.abs(signedFactor), 2.0,
                $"Precondition: the miter factor's MAGNITUDE must exceed the limit. Got {math.abs(signedFactor):G6}.");

            Assert.IsTrue(LineTessellator.NeedsBevel(n1, n2, 2.0),
                $"Hairpin with a negative cos(θ/2): |1/cos| = {math.abs(signedFactor):G6} exceeds the limit 2, " +
                "so the join must bevel. Comparing the SIGNED factor instead lets it through and the miter " +
                "normal blows up.");
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
        public void AllJoinCapCombinations_RightTurn_PositiveWindingNoDegenerate()
        {
            // Closes §3.6's coverage hole: AllJoinCapCombinations_LShape_* exercises only the
            // leftTurn branch (Fixture A). A botched quad-index swap (c) or next-pointer swap (d) in
            // the !leftTurn branch would ship green through every other tooth in this file — both
            // producers would agree (parity is agreement, not correctness) and no other winding pin
            // exercises a right turn. Fixture B: (0,0)→(10,0)→(10,-10), right turn at (10,0).
            var pts   = new[] { Pt(0,0), Pt(10,0), Pt(10,-10) };
            var joins = new[] { JoinType.Miter, JoinType.Bevel, JoinType.Round };
            var caps  = new[] { CapType.Butt, CapType.Square, CapType.Round };

            foreach (var join in joins)
            {
                foreach (var cap in caps)
                {
                    string label = $"RightTurn {join}/{cap}";
                    var r = LineTessellator.Triangulate(pts, join, cap, roundSegments: 4);

                    // Start tangent = (1,0), end tangent = (0,-1).
                    bool needsExtentCheck = (cap == CapType.Round || cap == CapType.Square);
                    AssertGeometryValid(r, label, needsExtentCheck,
                        startPt: Pt(0,0),   startTangent: new double2(1,0),
                        endPt: Pt(10,-10),  endTangent: new double2(0,-1));
                }
            }
        }

        /// <summary>
        /// Doubled signed areas of every triangle, after shader extrusion at <see cref="HalfWidth"/>.
        /// Positive = CCW (canonical), negative = the inner-join fold, zero = degenerate.
        /// </summary>
        private static double[] DoubledSignedAreas(LineTessellator.Result r)
        {
            var areas = new double[r.Indices.Length / 3];
            for (int t = 0; t < areas.Length; t++)
            {
                double2 a = Extrude(r.Vertices[r.Indices[t * 3]]);
                double2 b = Extrude(r.Vertices[r.Indices[t * 3 + 1]]);
                double2 c = Extrude(r.Vertices[r.Indices[t * 3 + 2]]);
                areas[t] = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            }
            return areas;
        }

        // The inner-join fold. AllJoinCapCombinations_* above assert UNIFORM CCW, but their fixtures use
        // 10-unit segments against HalfWidth 2 — an order of magnitude clear of the fold regime. Left at
        // that, the suite would read as a guarantee of uniform winding that the producer does NOT provide.
        // These cases pin where the guarantee actually ends.
        //
        // A 90° turn with HalfWidth 2 and miterLimit 2 has k = 1/cos45° = √2 (unclamped), so
        // docs/line-rendering-design.md §3 item 5's threshold
        //     S_crit = 2·h·k·sin(θ/2) / (1 + k·cos(θ/2)) = 2·2·√2·0.7071 / (1 + √2·0.7071) = 4/2
        // is EXACTLY 2.0 here — which makes the boundary itself testable rather than approximate. All three
        // join types fold identically, because the offending triangle is the incoming quad's (L0, R1, L1)
        // and every join emits that quad with the same two corner vertices.
        [Test]
        [TestCase(JoinType.Miter)]
        [TestCase(JoinType.Bevel)]
        [TestCase(JoinType.Round)]
        public void ShortSegment_InnerJoinFold_OnsetIsExactlyTheDocumentedSCrit(JoinType join)
        {
            // Below S_crit: the quad diagonal has inverted. Exactly one triangle, at exactly -0.8.
            var below = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(1.8, 0), Pt(1.8, 10) }, join, CapType.Butt, roundSegments: 4, miterLimit: 2.0);
            var belowAreas = DoubledSignedAreas(below);
            int belowFolded = 0;
            double mostNegative = 0.0;
            foreach (double area in belowAreas)
                if (area < 0.0) { belowFolded++; if (area < mostNegative) mostNegative = area; }

            Assert.AreEqual(1, belowFolded,
                $"{join}: S = 1.8 < S_crit = 2.0 must fold EXACTLY ONE triangle (the incoming quad's " +
                $"(L0, R1, L1)). Found {belowFolded}. If this changed, §3 item 5's bound is stale.");
            AssertNearlyEqual(-0.8, mostNegative, 1e-9,
                $"{join}: the folded triangle's doubled signed area should be -0.8. Got {mostNegative:G17}.");

            // At S_crit the same triangle is exactly degenerate — the boundary is not approximate.
            var at = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(2.0, 0), Pt(2.0, 10) }, join, CapType.Butt, roundSegments: 4, miterLimit: 2.0);
            double closestToZero = double.MaxValue;
            foreach (double area in DoubledSignedAreas(at))
            {
                Assert.GreaterOrEqual(area, -1e-9, $"{join}: nothing may fold AT S_crit. Got {area:G17}.");
                if (Math.Abs(area) < Math.Abs(closestToZero)) closestToZero = area;
            }
            AssertNearlyEqual(0.0, closestToZero, 1e-9,
                $"{join}: at S = S_crit = 2.0 exactly one triangle must be degenerate. Got {closestToZero:G17}.");

            // Above S_crit: uniform CCW, the regime AllJoinCapCombinations_* covers.
            var above = LineTessellator.Triangulate(
                new[] { Pt(0, 0), Pt(2.2, 0), Pt(2.2, 10) }, join, CapType.Butt, roundSegments: 4, miterLimit: 2.0);
            foreach (double area in DoubledSignedAreas(above))
                Assert.Greater(area, 0.0, $"{join}: S = 2.2 > S_crit must be uniformly CCW. Got {area:G17}.");
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
