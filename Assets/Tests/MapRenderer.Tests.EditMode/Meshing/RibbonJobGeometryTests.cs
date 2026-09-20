using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;
using MapRenderer.Jobs.Lines;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// First-principles geometry teeth for <see cref="RibbonJob"/> over a flat centerline
    /// (<see cref="FlatRibbon"/>): unit normals, exact per-join/per-cap vertex counts, the
    /// miter→bevel/round cascade, the round-cap pivot, winding, and the fold onset at the documented
    /// s_crit. Re-homed off the retired managed line tessellator. Every expectation is analytic — derived
    /// from the join/cap geometry, not read off any producer — and re-derived against the Burst arm's
    /// own formulation, where the extrusion direction is normalize(cross(along, up)) rather than a 2D
    /// left normal. The two agree exactly for a flat centerline with up = +Y, which is why the constants
    /// carry across unchanged.
    /// </summary>
    [TestFixture]
    public class RibbonJobGeometryTests
    {
        // ─── Helpers ───────────────────────────────────────────────────────────────────────

        private static double2 Pt(double x, double y) => new double2(x, y);

        private static double VecLen(double2 v) => math.sqrt(v.x * v.x + v.y * v.y);

        private static void AssertNearlyEqual(double expected, double actual, double tol, string msg)
            => Assert.That(actual, Is.InRange(expected - tol, expected + tol), msg);

        private static (LineRibbonVertex[] Vertices, int[] Indices) Build(
            double2[] pts, JoinType join, CapType cap, double miterLimit = 2.0, int roundSegments = 4,
            double roundLimit = 1.05)
            => FlatRibbon.Build(pts, join, cap, miterLimit, roundSegments, roundLimit);

        /// <summary>Flat 2D projection of a vertex's centerline position (Position.x, Position.z).</summary>
        private static double2 Pos2(LineRibbonVertex v) => new double2(v.Position.x, v.Position.z);

        /// <summary>Flat 2D projection of a vertex's extrusion direction (Across.x, Across.z).</summary>
        private static double2 Across2(LineRibbonVertex v) => new double2(v.Across.x, v.Across.z);

        // ─── Degenerate input ──────────────────────────────────────────────────────────────
        //
        // NullLine_ReturnsEmptyResult (the retired managed tessellator's null-argument overload) is
        // inexpressible against RibbonJob's NativeArray + PointCount signature — there is no "null"
        // NativeArray. SinglePoint_ReturnsEmptyResult (PointCount = 1) is a literal duplicate of the
        // surviving LineRibbonJobTests.LessThanTwoPoints_ProducesZeroOutput, so it is not re-ported here.
        // EmptyList_ReturnsEmptyResult (PointCount = 0) IS expressible — RibbonJob.Execute's
        // `if (PointCount < 2) return;` covers it identically — and FlatRibbon.Build already handles a
        // zero-length input, so it is ported below rather than assumed covered.

        [Test]
        public void EmptyList_ReturnsEmptyResult()
        {
            var (v, i) = Build(new double2[0], JoinType.Miter, CapType.Butt);
            Assert.AreEqual(0, v.Length, "Empty input → 0 verts.");
            Assert.AreEqual(0, i.Length, "Empty input → 0 indices.");
        }

        [Test]
        public void AllDuplicatePoints_ReturnsEmpty()
        {
            var (v, i) = Build(new[] { Pt(5, 3), Pt(5, 3), Pt(5, 3) }, JoinType.Miter, CapType.Butt);
            Assert.AreEqual(0, v.Length, "All-duplicate line → 0 verts.");
            Assert.AreEqual(0, i.Length, "All-duplicate line → 0 indices.");
        }

        [Test]
        public void DuplicatePoints_OnlyTwoDistinct_ProducesSegment()
        {
            // Two actual points and one duplicate of the first — should deduplicate to 2 points.
            var (v, i) = Build(new[] { Pt(0, 0), Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            Assert.AreEqual(4, v.Length, $"One segment after dedup → 4 verts. Got {v.Length}.");
            Assert.AreEqual(6, i.Length, $"One segment after dedup → 6 indices. Got {i.Length}.");
        }

        // ─── Straight 2-point segment (Butt caps, Miter join) ─────────────────────────────

        [Test]
        public void TwoPoints_ButtCap_Miter_ExactlyFourVertsAndTwoTriangles()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            Assert.AreEqual(4, v.Length, $"2-pt butt segment → exactly 4 vertices. Got {v.Length}.");
            Assert.AreEqual(6, i.Length, $"2-pt butt segment → exactly 6 indices (2 triangles). Got {i.Length}.");
        }

        [Test]
        public void TwoPoints_ButtCap_NormalsAreUnitLength()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            foreach (var vert in v)
            {
                double len = VecLen(Across2(vert));
                AssertNearlyEqual(1.0, len, 1e-9,
                    $"Butt segment: every Across must be unit length. Got length={len:G10}.");
            }
        }

        [Test]
        public void TwoPoints_ButtCap_NormalsPerpendicularToSegment()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            double2 tangent = new double2(1, 0);
            foreach (var vert in v)
            {
                double2 across = Across2(vert);
                double dot = across.x * tangent.x + across.y * tangent.y;
                AssertNearlyEqual(0.0, dot, 1e-9,
                    $"Butt segment: Across must be perpendicular to tangent. dot={dot:G10}.");
            }
        }

        [Test]
        public void TwoPoints_ButtCap_NormalsAreOppositeOnLeftAndRight()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            double2 n0 = Across2(v[0]);
            double2 n1 = Across2(v[1]);
            AssertNearlyEqual(0.0, n0.x + n1.x, 1e-9, "Left and right Across must be x-opposites.");
            AssertNearlyEqual(0.0, n0.y + n1.y, 1e-9, "Left and right Across must be y-opposites.");
        }

        [Test]
        public void TwoPoints_ButtCap_PositionsMatchEndpoints()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            for (int i = 0; i < 2; i++)
            {
                double2 p = Pos2(v[i]);
                Assert.That(p.x == 0.0 && p.y == 0.0, Is.True,
                    $"Vertex[{i}] should be at (0,0), got ({p.x},{p.y}).");
            }
            for (int i = 2; i < 4; i++)
            {
                double2 p = Pos2(v[i]);
                Assert.That(p.x == 10.0 && p.y == 0.0, Is.True,
                    $"Vertex[{i}] should be at (10,0), got ({p.x},{p.y}).");
            }
        }

        [Test]
        public void TwoPoints_ButtCap_SideValues_AreCorrect()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            Assert.AreEqual(+1f, v[0].Side, $"Vertex[0] side={v[0].Side}; expected +1.");
            Assert.AreEqual(-1f, v[1].Side, $"Vertex[1] side={v[1].Side}; expected -1.");
            Assert.AreEqual(+1f, v[2].Side, $"Vertex[2] side={v[2].Side}; expected +1.");
            Assert.AreEqual(-1f, v[3].Side, $"Vertex[3] side={v[3].Side}; expected -1.");
        }

        [Test]
        public void AllVertices_WidthScaleIsOne()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(20, 10) }, JoinType.Miter, CapType.Butt);

            foreach (var vert in v)
                Assert.AreEqual(1.0f, vert.WidthScale, $"WidthScale must default to 1. Got {vert.WidthScale}.");
        }

        [Test]
        public void AllVertices_SideIsOneOrMinusOne()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(20, 10) }, JoinType.Miter, CapType.Butt);

            foreach (var vert in v)
                Assert.That(math.abs(math.abs(vert.Side) - 1f), Is.LessThan(1e-6f),
                    $"Side must be +1 or -1 for every vertex. Got {vert.Side}.");
        }

        // ─── DistanceAlong ─────────────────────────────────────────────────────────────────

        [Test]
        public void DistanceAlong_SingleSegment_IsCorrect()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            AssertNearlyEqual(0.0, v[0].DistanceAlong, 1e-9, "Start verts dist=0.");
            AssertNearlyEqual(0.0, v[1].DistanceAlong, 1e-9, "Start verts dist=0.");
            AssertNearlyEqual(10.0, v[2].DistanceAlong, 1e-9, "End verts dist=10.");
            AssertNearlyEqual(10.0, v[3].DistanceAlong, 1e-9, "End verts dist=10.");
        }

        [Test]
        public void DistanceAlong_MultiSegment_MiterJoin_IsNonDecreasing()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Miter, CapType.Butt);

            for (int i = 1; i < v.Length; i++)
                Assert.That(v[i].DistanceAlong, Is.GreaterThanOrEqualTo(v[i - 1].DistanceAlong - 1e-9),
                    $"DistanceAlong must be non-decreasing. Vertex[{i}]={v[i].DistanceAlong:G10} " +
                    $"< Vertex[{i - 1}]={v[i - 1].DistanceAlong:G10}.");
        }

        [Test]
        public void DistanceAlong_FinalVertex_EqualsPolylineLength()
        {
            // (0,0)→(3,0)→(3,4) = 3+4 = 7.
            var (v, _) = Build(new[] { Pt(0, 0), Pt(3, 0), Pt(3, 4) }, JoinType.Miter, CapType.Butt);

            double maxDist = 0;
            foreach (var vert in v)
                if (vert.DistanceAlong > maxDist) maxDist = vert.DistanceAlong;

            AssertNearlyEqual(7.0, maxDist, 1e-9, $"Max DistanceAlong must equal the polyline length. Got {maxDist:G10}.");
        }

        // ─── 90° Miter join ────────────────────────────────────────────────────────────────

        [Test]
        public void RightAngle_MiterJoin_ExactVertexCount()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Miter, CapType.Butt,
                miterLimit: 10.0);

            Assert.AreEqual(6, v.Length, $"3-pt miter join → 6 vertices. Got {v.Length}.");
            Assert.AreEqual(12, i.Length, $"3-pt miter join → 12 indices. Got {i.Length}.");
        }

        [Test]
        public void RightAngle_MiterJoin_NormalLengthIsSqrt2()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Miter, CapType.Butt,
                miterLimit: 10.0);

            double miterExpected = math.sqrt(2.0);
            for (int i = 2; i <= 3; i++)
            {
                double len = VecLen(Across2(v[i]));
                AssertNearlyEqual(miterExpected, len, 1e-9,
                    $"90° miter join: vertex[{i}] |Across| should be √2. Got {len:G10}.");
            }
        }

        [Test]
        public void RightAngle_MiterJoin_SegmentNormalsAreUnit()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Miter, CapType.Butt,
                miterLimit: 10.0);

            int[] segVertIdx = { 0, 1, 4, 5 };
            foreach (int i in segVertIdx)
            {
                double len = VecLen(Across2(v[i]));
                AssertNearlyEqual(1.0, len, 1e-9, $"Vertex[{i}] (segment Across) must be unit length. Got {len:G10}.");
            }
        }

        // ─── Miter fallback to bevel ───────────────────────────────────────────────────────

        [Test]
        public void SharpAngle_MiterFallback_NoBevelVertexExceedsMiterLimit()
        {
            double rad = math.PI_DBL * 170.0 / 180.0; // 170° external angle
            double x2 = math.cos(rad) * 10.0;
            double y2 = math.sin(rad) * 10.0;
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(10 + x2, y2) };

            var (v, _) = Build(pts, JoinType.Miter, CapType.Butt, miterLimit: 2.0);

            double hard = 2.0 * math.sqrt(2.0);
            foreach (var vert in v)
            {
                double len = VecLen(Across2(vert));
                Assert.That(len, Is.LessThan(hard + 1e-6),
                    $"After miter→bevel fallback: no Across should exceed miterLimit×√2. Got |Across|={len:G10}.");
            }
        }

        [Test]
        public void SharpAngle_ExplicitBevel_VertexCountExactlyOneBevelExtra()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Bevel, CapType.Butt);

            Assert.AreEqual(7, v.Length, $"3-pt bevel → 7 verts. Got {v.Length}.");
            Assert.AreEqual(15, i.Length, $"3-pt bevel → 15 indices. Got {i.Length}.");
        }

        // ─── Round join ────────────────────────────────────────────────────────────────────

        [Test]
        public void RightAngle_RoundJoin_ExactVertexCount()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Round, CapType.Butt,
                roundSegments: 4);

            Assert.AreEqual(11, v.Length, $"3-pt round join (roundSegments=4) → 11 verts. Got {v.Length}.");
            Assert.AreEqual(27, i.Length, $"3-pt round join (roundSegments=4) → 27 indices. Got {i.Length}.");
        }

        // ─── Inner-join miter clamp (bevel/round) ─────────────────────────────────────────
        //
        // InnerJoin_Bevel_90LeftTurn_Unclamped (the retired managed suite's left-turn/unclamped case) is
        // not re-ported here: LineRibbonJobTests.InnerJoin_Bevel_90LeftTurn_Across_MatchesAnalyticIntersection
        // already asserts exactly this property directly against RibbonJob and survives the managed
        // arm's retirement — porting it again would duplicate, not extend, coverage.

        /// <summary>Locates the join's inner (concave) vertex: the unique vertex at <paramref name="corner"/>
        /// whose Side sign matches the turn side (left turn ⇒ +1, right turn ⇒ −1).</summary>
        private static int FindInnerVertexIndex(LineRibbonVertex[] verts, double2 corner, bool leftTurn)
        {
            float expectedSide = leftTurn ? +1f : -1f;
            int found = -1, count = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                double2 pos = Pos2(verts[i]);
                if (math.abs(pos.x - corner.x) < 1e-9 && math.abs(pos.y - corner.y) < 1e-9 &&
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
        public void InnerJoin_Round_90RightTurn_Unclamped_MirrorSignBranch()
        {
            // Mirror of the bevel left-turn case (LineRibbonJobTests): round join, 90° RIGHT turn,
            // miterLimit 2.0. Concave (right) inner vertex carries -mu·factor: (-1,-1).
            var corner = Pt(10, 0);
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, -10) }, JoinType.Round, CapType.Butt,
                miterLimit: 2.0, roundSegments: 4);

            int idx = FindInnerVertexIndex(v, corner, leftTurn: false);
            double2 across = Across2(v[idx]);

            AssertNearlyEqual(-1.0, across.x, 1e-9, $"Inner Across.x should be -1.0. Got {across.x:G17}.");
            AssertNearlyEqual(-1.0, across.y, 1e-9, $"Inner Across.y should be -1.0. Got {across.y:G17}.");

            double len = VecLen(across);
            AssertNearlyEqual(1.4142135623730951, len, 1e-9, $"Inner |Across| should be √2. Got {len:G17}.");
        }

        [Test]
        public void InnerJoin_Bevel_150LeftTurn_ClampedBranch()
        {
            // 150° left turn, miterLimit 2.0. Raw factor 1/cos75° ≈ 3.8637 > 2 → clamped to exactly 2.0.
            var corner = Pt(10, 0);
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(-15.980762113533157, 15.0) }, JoinType.Bevel,
                CapType.Butt, miterLimit: 2.0);

            int idx = FindInnerVertexIndex(v, corner, leftTurn: true);
            double2 across = Across2(v[idx]);

            AssertNearlyEqual(-1.9318516525781366, across.x, 1e-9, $"Inner Across.x. Got {across.x:G17}.");
            AssertNearlyEqual(0.5176380902050415, across.y, 1e-9, $"Inner Across.y. Got {across.y:G17}.");

            double len = VecLen(across);
            AssertNearlyEqual(2.0, len, 1e-9, $"Inner |Across| should be clamped to exactly 2.0. Got {len:G17}.");
        }

        [Test]
        public void InnerJoin_Round_150LeftTurn_ClampedBranch()
        {
            // Same fixture as the bevel clamped tooth — covers the round arm of the clamped branch.
            var corner = Pt(10, 0);
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(-15.980762113533157, 15.0) }, JoinType.Round,
                CapType.Butt, miterLimit: 2.0, roundSegments: 4);

            int idx = FindInnerVertexIndex(v, corner, leftTurn: true);
            double2 across = Across2(v[idx]);

            AssertNearlyEqual(-1.9318516525781366, across.x, 1e-9, $"Inner Across.x. Got {across.x:G17}.");
            AssertNearlyEqual(0.5176380902050415, across.y, 1e-9, $"Inner Across.y. Got {across.y:G17}.");

            double len = VecLen(across);
            AssertNearlyEqual(2.0, len, 1e-9, $"Inner |Across| should be clamped to exactly 2.0. Got {len:G17}.");
        }

        [Test]
        public void MiterJoin_RightAngle_AcrossIsExactlyUnitBisector()
        {
            // The tight tooth: a miter at a 90° corner with miterLimit 10 must land on exactly ±1.
            // 1e-12, not the 1e-9 the clamp teeth use — this fixture is axis-aligned, so the bisector
            // normalize operates on an exact unit vector and leaves no room for reordering slack.
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Miter, CapType.Butt,
                miterLimit: 10.0);

            double2 left = Across2(v[2]);
            double2 right = Across2(v[3]);
            AssertNearlyEqual(-1.0, left.x, 1e-12, $"Miter left.x should be -1.0. Got {left.x:G17}.");
            AssertNearlyEqual(1.0, left.y, 1e-12, $"Miter left.y should be 1.0. Got {left.y:G17}.");
            AssertNearlyEqual(1.0, right.x, 1e-12, $"Miter right.x should be 1.0. Got {right.x:G17}.");
            AssertNearlyEqual(-1.0, right.y, 1e-12, $"Miter right.y should be -1.0. Got {right.y:G17}.");
        }

        // ─── Join side — chamfer/arc on the CONVEX side (region-membership teeth) ────────────

        private const double HalfWidth = 2.0;

        /// <summary>Extrude a vertex's flat position by its Across × HalfWidth (matches the vertex shader).</summary>
        private static double2 Extrude(LineRibbonVertex v)
        {
            double2 pos = Pos2(v);
            double2 across = Across2(v);
            return new double2(pos.x + across.x * HalfWidth, pos.y + across.y * HalfWidth);
        }

        private static bool InClosed(double2 q, double2 rMin, double2 rMax, double eps)
            => q.x >= rMin.x - eps && q.x <= rMax.x + eps &&
               q.y >= rMin.y - eps && q.y <= rMax.y + eps;

        private static List<int> CornerVerticesBySide(LineRibbonVertex[] verts, double2 corner, float side)
        {
            var found = new List<int>();
            for (int i = 0; i < verts.Length; i++)
            {
                double2 pos = Pos2(verts[i]);
                if (math.abs(pos.x - corner.x) < 1e-9 && math.abs(pos.y - corner.y) < 1e-9 && verts[i].Side == side)
                    found.Add(i);
            }
            return found;
        }

        [Test]
        public void JoinSide_BevelAndRound_ChamferIsOnTheConvexSide()
        {
            var r1Min = Pt(0, -2); var r1Max = Pt(10, 2);
            var r2Min = Pt(8, 0); var r2Max = Pt(12, 10);
            var corner = Pt(10, 0);
            var mu = new double2(-0.7071067811865476, 0.7071067811865476);
            const double eps = 1e-9;

            var fixtureA = new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) };

            // T1a — the concave vertex must sit INSIDE both bands, on the mu side.
            var (bevelAV, _) = Build(fixtureA, JoinType.Bevel, CapType.Butt, miterLimit: 2.0);
            int concaveIdx = FindInnerVertexIndex(bevelAV, corner, leftTurn: true);
            double2 concaveExtruded = Extrude(bevelAV[concaveIdx]);
            double dotMu = (concaveExtruded.x - corner.x) * mu.x + (concaveExtruded.y - corner.y) * mu.y;

            AssertNearlyEqual(8.0, concaveExtruded.x, eps, $"T1a: concave vertex.x should be 8.0. Got {concaveExtruded.x:G17}.");
            AssertNearlyEqual(2.0, concaveExtruded.y, eps, $"T1a: concave vertex.y should be 2.0. Got {concaveExtruded.y:G17}.");
            AssertNearlyEqual(2.8284271247461903, dotMu, eps, $"T1a: dot(Extrude-C, mu) should be 2.8284271247461903. Got {dotMu:G17}.");
            Assert.Greater(dotMu, 0.0, "T1a: concave vertex must be on the mu (turn) side.");
            Assert.IsTrue(InClosed(concaveExtruded, r1Min, r1Max, eps), "T1a: concave vertex must lie inside R1 (band overlap).");
            Assert.IsTrue(InClosed(concaveExtruded, r2Min, r2Max, eps), "T1a: concave vertex must lie inside R2 (band overlap).");

            // T1b — bevel chamfer chord: the two convex vertices' midpoint must sit strictly OUTSIDE both bands.
            var outerVerts = CornerVerticesBySide(bevelAV, corner, -1f);
            Assert.AreEqual(2, outerVerts.Count, $"T1b: expected exactly 2 convex (side=-1) bevel vertices. Found {outerVerts.Count}.");
            double2 outerA = Extrude(bevelAV[outerVerts[0]]);
            double2 outerB = Extrude(bevelAV[outerVerts[1]]);
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
            var (roundAV, _) = Build(fixtureA, JoinType.Round, CapType.Butt, roundSegments: 4, miterLimit: 2.0);
            var arcSideVerts = CornerVerticesBySide(roundAV, corner, -1f);
            Assert.AreEqual(6, arcSideVerts.Count, $"T1c: expected 6 convex-side round-join vertices. Found {arcSideVerts.Count}.");

            double[] expectedX = { 10.6180339887, 11.1755705046, 11.6180339887, 11.9021130326 };
            double[] expectedY = { -1.9021130326, -1.6180339887, -1.1755705046, -0.6180339887 };
            double minMargin = double.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                double2 fanPos = Extrude(roundAV[arcSideVerts[k + 1]]);
                AssertNearlyEqual(expectedX[k], fanPos.x, 1e-6, $"T1c: arc intermediate[{k}].x. Got {fanPos.x:G17}.");
                AssertNearlyEqual(expectedY[k], fanPos.y, 1e-6, $"T1c: arc intermediate[{k}].y. Got {fanPos.y:G17}.");
                Assert.IsFalse(InClosed(fanPos, r1Min, r1Max, -eps), $"T1c: arc intermediate[{k}] must lie strictly outside R1.");
                Assert.IsFalse(InClosed(fanPos, r2Min, r2Max, -eps), $"T1c: arc intermediate[{k}] must lie strictly outside R2.");

                double marginR1 = fanPos.x - 10.0;
                double marginR2 = -fanPos.y;
                double margin = math.min(marginR1, marginR2);
                if (margin < minMargin) minMargin = margin;
            }
            Assert.Greater(minMargin, 0.6, $"T1c: minimum distance from R1∪R2 should be ~0.618. Got {minMargin:G17}.");

            // T1d — right-turn mirror (Fixture B).
            var fixtureB = new[] { Pt(0, 0), Pt(10, 0), Pt(10, -10) };
            var (bevelBV, _) = Build(fixtureB, JoinType.Bevel, CapType.Butt, miterLimit: 2.0);
            int concaveIdxB = FindInnerVertexIndex(bevelBV, corner, leftTurn: false);
            double2 concaveExtrudedB = Extrude(bevelBV[concaveIdxB]);
            var r2MinB = Pt(8, -10); var r2MaxB = Pt(12, 0);

            AssertNearlyEqual(8.0, concaveExtrudedB.x, eps, $"T1d: concave vertex.x should be 8.0. Got {concaveExtrudedB.x:G17}.");
            AssertNearlyEqual(-2.0, concaveExtrudedB.y, eps, $"T1d: concave vertex.y should be -2.0. Got {concaveExtrudedB.y:G17}.");
            Assert.IsTrue(InClosed(concaveExtrudedB, r1Min, r1Max, eps), "T1d: concave vertex must lie inside R1.");
            Assert.IsTrue(InClosed(concaveExtrudedB, r2MinB, r2MaxB, eps), "T1d: concave vertex must lie inside R2 (mirrored band).");

            var outerVertsB = CornerVerticesBySide(bevelBV, corner, +1f);
            Assert.AreEqual(2, outerVertsB.Count, $"T1d: expected exactly 2 convex (side=+1) bevel vertices. Found {outerVertsB.Count}.");
            double2 outerAB = Extrude(bevelBV[outerVertsB[0]]);
            double2 outerBB = Extrude(bevelBV[outerVertsB[1]]);
            double2 chordMidB = new double2((outerAB.x + outerBB.x) / 2.0, (outerAB.y + outerBB.y) / 2.0);

            AssertNearlyEqual(11.0, chordMidB.x, eps, $"T1d: chord midpoint.x should be 11.0. Got {chordMidB.x:G17}.");
            AssertNearlyEqual(1.0, chordMidB.y, eps, $"T1d: chord midpoint.y should be 1.0. Got {chordMidB.y:G17}.");
            Assert.IsFalse(InClosed(chordMidB, r1Min, r1Max, -eps), "T1d: chamfer chord midpoint must lie strictly outside R1.");
            Assert.IsFalse(InClosed(chordMidB, r2MinB, r2MaxB, -eps), "T1d: chamfer chord midpoint must lie strictly outside R2 (mirrored band).");
        }

        [Test]
        public void JoinVertices_NormalTimesSide_IsUnchanged()
        {
            // Normal (Across) negation and Side flip must happen TOGETHER, so Across×Side is unchanged
            // vertex-for-vertex at every join vertex.
            var corner = Pt(10, 0);
            var fixtureA = new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) };
            var (v, _) = Build(fixtureA, JoinType.Bevel, CapType.Butt, miterLimit: 2.0);

            var cornerVerts = new List<int>();
            for (int i = 0; i < v.Length; i++)
            {
                double2 pos = Pos2(v[i]);
                if (math.abs(pos.x - corner.x) < 1e-9 && math.abs(pos.y - corner.y) < 1e-9)
                    cornerVerts.Add(i);
            }
            Assert.AreEqual(3, cornerVerts.Count, $"Expected exactly 3 bevel join vertices at the corner. Found {cornerVerts.Count}.");

            double2[] expected = { new double2(0, 1), new double2(-1, 1), new double2(-1, 0) };
            for (int k = 0; k < 3; k++)
            {
                var vert = v[cornerVerts[k]];
                double2 across = Across2(vert);
                double2 normalTimesSide = new double2(across.x * vert.Side, across.y * vert.Side);
                AssertNearlyEqual(expected[k].x, normalTimesSide.x, 1e-9,
                    $"Across×Side[{k}].x should be {expected[k].x:G17}. Got {normalTimesSide.x:G17}.");
                AssertNearlyEqual(expected[k].y, normalTimesSide.y, 1e-9,
                    $"Across×Side[{k}].y should be {expected[k].y:G17}. Got {normalTimesSide.y:G17}.");
            }

            AssertNormalMuSideCorrelation(fixtureA, JoinType.Bevel, corner);
            AssertNormalMuSideCorrelation(fixtureA, JoinType.Round, corner);
        }

        private static void AssertNormalMuSideCorrelation(double2[] pts, JoinType joinType, double2 corner)
        {
            var (v, _) = Build(pts, joinType, CapType.Butt, roundSegments: 4, miterLimit: 2.0);
            var mu = new double2(-0.7071067811865476, 0.7071067811865476);

            int asserted = 0, ambiguous = 0;
            for (int i = 0; i < v.Length; i++)
            {
                double2 pos = Pos2(v[i]);
                if (math.abs(pos.x - corner.x) > 1e-9 || math.abs(pos.y - corner.y) > 1e-9) continue;
                if (v[i].Side == 0f) continue; // fan pivot, not a corner-offset vertex.

                double2 across = Across2(v[i]);
                double dot = across.x * mu.x + across.y * mu.y;
                bool sidePositive = v[i].Side > 0f;
                if (math.abs(dot) < 1e-6) { ambiguous++; continue; }
                Assert.AreEqual(dot > 0.0, sidePositive,
                    $"{joinType} vertex[{i}]: dot(Across,mu)={dot:G17} but Side={v[i].Side} — sign mismatch.");
                asserted++;
            }

            Assert.AreEqual(0, ambiguous,
                $"{joinType}: {ambiguous} corner vertices were skipped as sign-ambiguous — re-derive against the new topology.");
            Assert.Greater(asserted, 0, $"{joinType}: no corner vertices were checked — the tooth is vacuous.");
        }

        // ─── Square cap ────────────────────────────────────────────────────────────────────

        [Test]
        public void SingleSegment_SquareCap_ExactVertexCount()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Square);

            Assert.AreEqual(6, v.Length, $"2-pt square caps → 6 verts. Got {v.Length}.");
            Assert.AreEqual(12, i.Length, $"2-pt square caps → 12 indices. Got {i.Length}.");
        }

        // ─── Round cap ─────────────────────────────────────────────────────────────────────

        [Test]
        public void SingleSegment_RoundCap_ExactVertexCount()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Round, roundSegments: 4);

            Assert.AreEqual(16, v.Length, $"2-pt round caps (roundSegments=4) → 16 verts. Got {v.Length}.");
            Assert.AreEqual(36, i.Length, $"2-pt round caps (roundSegments=4) → 36 indices. Got {i.Length}.");
        }

        /// <summary>
        /// Every cap-fan triangle must carry both of its RIM vertices at the same <c>Side</c> SIGN — the
        /// silhouette-seam fix. Pinned directly against <see cref="RibbonJob"/>: the retired managed
        /// tessellator held this guarantee differentially; with the managed arm gone this is the only
        /// place it is checked.
        /// </summary>
        [Test]
        public void RoundCap_FanTriangles_HaveUniformRimSide()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Round, roundSegments: 4);

            int fanTrianglesChecked = 0;
            for (int t = 0; t < i.Length / 3; t++)
            {
                LineRibbonVertex a = v[i[t * 3]];
                LineRibbonVertex b = v[i[t * 3 + 1]];
                LineRibbonVertex c = v[i[t * 3 + 2]];

                if (math.abs(a.Side) > 1e-6f) continue;
                fanTrianglesChecked++;

                Assert.AreEqual(math.sign(b.Side), math.sign(c.Side),
                    $"Cap-fan tri[{t}] rim sides are {b.Side} and {c.Side}: its outer edge is a true " +
                    "silhouette but |side| dips to 0 at the midpoint.");
            }

            Assert.AreEqual(10, fanTrianglesChecked,
                $"Both round caps must contribute 5 pivot-rooted fan triangles each. Found {fanTrianglesChecked}.");
        }

        // ─── line-round-limit: shallow round joins collapse to miter ─────────────────────────

        [Test]
        public void ShallowRoundJoin_CollapsesToMiter_MatchesMiterPathExactly()
        {
            // 20° turn: half-angle 10° ⇒ f = 1/cos10° ≈ 1.0154 < roundLimit(1.05) ⇒ must collapse to miter.
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(19.396926207859085, 3.4202014332566878) };

            var (roundV, roundI) = Build(pts, JoinType.Round, CapType.Butt, miterLimit: 10.0, roundSegments: 4,
                roundLimit: 1.05);
            var (miterV, miterI) = Build(pts, JoinType.Miter, CapType.Butt, miterLimit: 10.0);

            Assert.AreEqual(miterV.Length, roundV.Length,
                $"A shallow round join (f≈1.0154 ≤ roundLimit=1.05) must collapse to the miter path's vertex " +
                $"count. miter={miterV.Length}, round={roundV.Length}.");
            Assert.AreEqual(miterI.Length, roundI.Length,
                $"A shallow round join must collapse to the miter path's index count. miter={miterI.Length}, round={roundI.Length}.");

            for (int idx = 0; idx < miterV.Length; idx++)
            {
                double2 m = Across2(miterV[idx]);
                double2 r = Across2(roundV[idx]);
                AssertNearlyEqual(m.x, r.x, 1e-9, $"vertex[{idx}].Across.x: collapsed round must match the miter path.");
                AssertNearlyEqual(m.y, r.y, 1e-9, $"vertex[{idx}].Across.y: collapsed round must match the miter path.");
                Assert.AreEqual(miterV[idx].Side, roundV[idx].Side, $"vertex[{idx}].Side: collapsed round must match the miter path.");
            }
        }

        [Test]
        public void SharpRoundJoin_PreservesFan_RimAtHalfWidthRadius()
        {
            // 90° turn: f = √2 ≈ 1.414 ≫ roundLimit(1.05) ⇒ fan preserved.
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) };
            var corner = Pt(10, 0);
            const double halfWidth = 2.0;

            var (v, _) = Build(pts, JoinType.Round, CapType.Butt, miterLimit: 2.0, roundSegments: 4, roundLimit: 1.05);

            var rim = CornerVerticesBySide(v, corner, -1f);
            Assert.AreEqual(6, rim.Count,
                $"Sharp round join must still emit the 6-vertex convex rim. Found {rim.Count} — if 1, the join collapsed to a miter it should not have.");

            foreach (int idx in rim)
            {
                double2 extruded = Extrude(v[idx]);
                double dist = VecLen(new double2(extruded.x - corner.x, extruded.y - corner.y));
                AssertNearlyEqual(halfWidth, dist, 1e-6,
                    $"Rim vertex[{idx}] is {dist:G17} from the corner; must sit at exactly halfWidth={halfWidth}.");
            }
        }

        [Test]
        public void RoundLimit_ExceedsMiterLimit_CascadesToBevel_NotUnboundedMiter()
        {
            // roundLimit(3.0) and miterLimit(2.0) independently style-settable: a corner with f=2.5 sits
            // between them and must cascade round→miter→bevel, not fall through to an unbounded miter.
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(3.2, 7.332121111929344) };
            var corner = Pt(10, 0);

            var (v, _) = Build(pts, JoinType.Round, CapType.Butt, miterLimit: 2.0, roundSegments: 4, roundLimit: 3.0);

            var cornerVerts = new List<int>();
            for (int i = 0; i < v.Length; i++)
            {
                double2 pos = Pos2(v[i]);
                if (math.abs(pos.x - corner.x) < 1e-9 && math.abs(pos.y - corner.y) < 1e-9)
                    cornerVerts.Add(i);
            }
            Assert.AreEqual(3, cornerVerts.Count,
                $"A round join whose collapsed miter (f=2.5) exceeds miterLimit=2.0 must bevel (3 join vertices). Found {cornerVerts.Count}.");

            double maxMag = 0.0;
            foreach (int idx in cornerVerts)
                maxMag = math.max(maxMag, VecLen(Across2(v[idx])));
            Assert.LessOrEqual(maxMag, 2.0 + 1e-6,
                $"No join-vertex Across may exceed miterLimit=2.0. Got max |Across|={maxMag:G17}.");
            AssertNearlyEqual(2.0, maxMag, 1e-6,
                $"The inner vertex must be clamped to EXACTLY miterLimit=2.0. Got {maxMag:G17}.");
        }

        // ─── NeedsMiter / NeedsBevel (RibbonJob's internal accessors) ─────────────────────────

        [Test]
        public void NeedsMiter_ParallelSegments_ReturnsTrue()
        {
            double3 n = new double3(0, 0, 1);
            Assert.IsTrue(RibbonJob.NeedsMiter(n, n, 1.05),
                "Parallel segments → miter factor = 1 ≤ any roundLimit ≥ 1 → shallow → collapses to miter.");
        }

        [Test]
        public void NeedsMiter_RightAngle_AboveDefaultLimit_ReturnsFalse()
        {
            double3 n1 = new double3(0, 0, 1);
            double3 n2 = new double3(-1, 0, 0);
            Assert.IsFalse(RibbonJob.NeedsMiter(n1, n2, 1.05),
                "90° turn: miter factor √2 > roundLimit 1.05 → not shallow → NeedsMiter should be false.");
        }

        [Test]
        public void NeedsMiter_HairpinWithNegativeHalfAngleCosine_ReturnsFalse()
        {
            double3 n1 = new double3(0, 0, 1);
            double3 n2 = new double3(1e-9, 0, -1.0000000001);
            Assert.IsFalse(RibbonJob.NeedsMiter(n1, n2, 1.05),
                "Hairpin: miter factor magnitude ≫ roundLimit → NeedsMiter should be false.");
        }

        [Test]
        public void NeedsBevel_ParallelSegments_ReturnsFalse()
        {
            double3 n = new double3(0, 0, 1);
            Assert.IsFalse(RibbonJob.NeedsBevel(n, n, 2.0), "Parallel segments → miter factor = 1 → no bevel needed.");
        }

        [Test]
        public void NeedsBevel_RightAngle_BelowDefaultLimit_ReturnsFalse()
        {
            double3 n1 = new double3(0, 0, 1);
            double3 n2 = new double3(-1, 0, 0);
            Assert.IsFalse(RibbonJob.NeedsBevel(n1, n2, 2.0), "90° turn: miter factor √2 < limit 2 → NeedsBevel should be false.");
        }

        [Test]
        public void NeedsBevel_VerySharpAngle_ExceedsLimit_ReturnsTrue()
        {
            double3 n1 = new double3(0, 0, 1);
            double3 n2 = new double3(0.01, 0, -0.9999); // ~179° turn
            Assert.IsTrue(RibbonJob.NeedsBevel(n1, n2, 2.0), "Near-180° turn: miter factor >> 2 → NeedsBevel should be true.");
        }

        /// <summary>Test-local restatement of the miter-factor math, independent of <see cref="RibbonJob"/> —
        /// used only to state this test's OWN precondition (that the fixture drives a negative cos(θ/2)),
        /// not to assert anything about production.</summary>
        private static double MiterFactor(double2 n1, double2 n2)
        {
            double mx = n1.x + n2.x;
            double my = n1.y + n2.y;
            double mLen = math.sqrt(mx * mx + my * my);
            if (mLen < 1e-12) return double.MaxValue;
            double mux = mx / mLen;
            double muy = my / mLen;
            double dot = mux * n1.x + muy * n1.y;
            if (math.abs(dot) < 1e-12) return double.MaxValue;
            return 1.0 / dot;
        }

        [Test]
        public void NeedsBevel_HairpinWithNegativeHalfAngleCosine_StillReturnsTrue()
        {
            double2 n1 = new double2(0, 1);
            double2 n2 = new double2(1e-9, -1.0000000001);

            double signedFactor = MiterFactor(n1, n2);
            Assert.Less(signedFactor, 0.0,
                $"Precondition: this input must drive cos(θ/2) NEGATIVE. Signed miter factor was {signedFactor:G6}.");
            Assert.Greater(math.abs(signedFactor), 2.0,
                $"Precondition: the miter factor's MAGNITUDE must exceed the limit. Got {math.abs(signedFactor):G6}.");

            double3 n1_3 = new double3(n1.x, 0, n1.y);
            double3 n2_3 = new double3(n2.x, 0, n2.y);
            Assert.IsTrue(RibbonJob.NeedsBevel(n1_3, n2_3, 2.0),
                $"Hairpin with a negative cos(θ/2): |1/cos| = {math.abs(signedFactor):G6} exceeds the limit 2, so the join must bevel.");
        }

        // ─── Winding + extent assertions for all JoinType × CapType combinations ──────────

        private static double TriSignedArea(double2 a, double2 b, double2 c)
            => 0.5 * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));

        private static void AssertGeometryValid(
            LineRibbonVertex[] verts, int[] indices, string label,
            bool checkCapExtent = false,
            double2 startPt = default, double2 startTangent = default,
            double2 endPt = default, double2 endTangent = default)
        {
            Assert.Greater(verts.Length, 0, $"{label}: must have vertices.");
            Assert.Greater(indices.Length, 0, $"{label}: must have indices.");
            Assert.AreEqual(0, indices.Length % 3, $"{label}: index count must be multiple of 3.");

            int triCount = indices.Length / 3;
            int posCount = 0, negCount = 0, zeroCount = 0;
            var dupList = new List<string>();

            for (int t = 0; t < triCount; t++)
            {
                int i0 = indices[t * 3];
                int i1 = indices[t * 3 + 1];
                int i2 = indices[t * 3 + 2];

                if (i0 == i1 || i1 == i2 || i0 == i2)
                    dupList.Add($"tri[{t}]({i0},{i1},{i2})");

                double2 a = Extrude(verts[i0]);
                double2 b = Extrude(verts[i1]);
                double2 c = Extrude(verts[i2]);
                double area = TriSignedArea(a, b, c);

                if (area > 1e-9) posCount++;
                else if (area < -1e-9) negCount++;
                else zeroCount++;
            }

            Assert.AreEqual(0, dupList.Count, $"{label}: {dupList.Count} duplicate-index triangle(s): {string.Join(", ", dupList)}.");
            Assert.AreEqual(0, zeroCount, $"{label}: {zeroCount}/{triCount} degenerate (zero-area) triangles after extrusion.");
            Assert.AreEqual(0, negCount,
                $"{label}: {negCount}/{triCount} negative-winding (CW) triangles after extrusion. pos={posCount} neg={negCount} zero={zeroCount}.");

            if (checkCapExtent)
            {
                bool startCapOk = false;
                bool endCapOk = false;
                double2 outwardStart = new double2(-startTangent.x, -startTangent.y);
                double2 outwardEnd = endTangent;

                foreach (var v in verts)
                {
                    double2 ext = Extrude(v);

                    double2 ds = new double2(ext.x - startPt.x, ext.y - startPt.y);
                    if (ds.x * outwardStart.x + ds.y * outwardStart.y > 1e-6) startCapOk = true;

                    double2 de = new double2(ext.x - endPt.x, ext.y - endPt.y);
                    if (de.x * outwardEnd.x + de.y * outwardEnd.y > 1e-6) endCapOk = true;
                }

                Assert.IsTrue(startCapOk, $"{label}: start cap must have at least one vertex extruding beyond p0 in the backward tangent direction.");
                Assert.IsTrue(endCapOk, $"{label}: end cap must have at least one vertex extruding beyond p1 in the forward tangent direction.");
            }
        }

        private static readonly double2 LineStart = new double2(0, 0);
        private static readonly double2 LineEnd = new double2(10, 0);
        private static readonly double2 LineTangent = new double2(1, 0);

        [Test]
        public void AllJoinCapCombinations_PositiveWindingNoDegenerate()
        {
            var joins = new[] { JoinType.Miter, JoinType.Bevel, JoinType.Round };
            var caps = new[] { CapType.Butt, CapType.Square, CapType.Round };
            var hline = new[] { LineStart, LineEnd };

            foreach (var join in joins)
            {
                foreach (var cap in caps)
                {
                    string label = $"hline {join}/{cap}";
                    var (v, i) = Build(hline, join, cap, roundSegments: 4);

                    bool needsExtentCheck = cap == CapType.Round || cap == CapType.Square;
                    AssertGeometryValid(v, i, label, needsExtentCheck,
                        startPt: LineStart, startTangent: LineTangent, endPt: LineEnd, endTangent: LineTangent);
                }
            }
        }

        [Test]
        public void AllJoinCapCombinations_LShape_PositiveWindingNoDegenerate()
        {
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) };
            var joins = new[] { JoinType.Miter, JoinType.Bevel, JoinType.Round };
            var caps = new[] { CapType.Butt, CapType.Square, CapType.Round };

            foreach (var join in joins)
            {
                foreach (var cap in caps)
                {
                    string label = $"Lshape {join}/{cap}";
                    var (v, i) = Build(pts, join, cap, roundSegments: 4);

                    bool needsExtentCheck = cap == CapType.Round || cap == CapType.Square;
                    AssertGeometryValid(v, i, label, needsExtentCheck,
                        startPt: Pt(0, 0), startTangent: new double2(1, 0),
                        endPt: Pt(10, 10), endTangent: new double2(0, 1));
                }
            }
        }

        [Test]
        public void AllJoinCapCombinations_RightTurn_PositiveWindingNoDegenerate()
        {
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(10, -10) };
            var joins = new[] { JoinType.Miter, JoinType.Bevel, JoinType.Round };
            var caps = new[] { CapType.Butt, CapType.Square, CapType.Round };

            foreach (var join in joins)
            {
                foreach (var cap in caps)
                {
                    string label = $"RightTurn {join}/{cap}";
                    var (v, i) = Build(pts, join, cap, roundSegments: 4);

                    bool needsExtentCheck = cap == CapType.Round || cap == CapType.Square;
                    AssertGeometryValid(v, i, label, needsExtentCheck,
                        startPt: Pt(0, 0), startTangent: new double2(1, 0),
                        endPt: Pt(10, -10), endTangent: new double2(0, -1));
                }
            }
        }

        /// <summary>Doubled signed areas of every triangle, after shader extrusion at <see cref="HalfWidth"/>.</summary>
        private static double[] DoubledSignedAreas(LineRibbonVertex[] verts, int[] indices)
        {
            var areas = new double[indices.Length / 3];
            for (int t = 0; t < areas.Length; t++)
            {
                double2 a = Extrude(verts[indices[t * 3]]);
                double2 b = Extrude(verts[indices[t * 3 + 1]]);
                double2 c = Extrude(verts[indices[t * 3 + 2]]);
                areas[t] = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            }
            return areas;
        }

        [Test]
        [TestCase(JoinType.Miter)]
        [TestCase(JoinType.Bevel)]
        [TestCase(JoinType.Round)]
        public void ShortSegment_InnerJoinFold_OnsetIsExactlyTheDocumentedSCrit(JoinType join)
        {
            var (belowV, belowI) = Build(new[] { Pt(0, 0), Pt(1.8, 0), Pt(1.8, 10) }, join, CapType.Butt,
                roundSegments: 4, miterLimit: 2.0);
            var belowAreas = DoubledSignedAreas(belowV, belowI);
            int belowFolded = 0;
            double mostNegative = 0.0;
            foreach (double area in belowAreas)
                if (area < 0.0) { belowFolded++; if (area < mostNegative) mostNegative = area; }

            Assert.AreEqual(1, belowFolded,
                $"{join}: S = 1.8 < S_crit = 2.0 must fold EXACTLY ONE triangle. Found {belowFolded}.");
            AssertNearlyEqual(-0.8, mostNegative, 1e-9, $"{join}: the folded triangle's doubled signed area should be -0.8. Got {mostNegative:G17}.");

            var (atV, atI) = Build(new[] { Pt(0, 0), Pt(2.0, 0), Pt(2.0, 10) }, join, CapType.Butt,
                roundSegments: 4, miterLimit: 2.0);
            double closestToZero = double.MaxValue;
            foreach (double area in DoubledSignedAreas(atV, atI))
            {
                Assert.GreaterOrEqual(area, -1e-9, $"{join}: nothing may fold AT S_crit. Got {area:G17}.");
                if (math.abs(area) < math.abs(closestToZero)) closestToZero = area;
            }
            AssertNearlyEqual(0.0, closestToZero, 1e-9, $"{join}: at S = S_crit = 2.0 exactly one triangle must be degenerate. Got {closestToZero:G17}.");

            var (aboveV, aboveI) = Build(new[] { Pt(0, 0), Pt(2.2, 0), Pt(2.2, 10) }, join, CapType.Butt,
                roundSegments: 4, miterLimit: 2.0);
            foreach (double area in DoubledSignedAreas(aboveV, aboveI))
                Assert.Greater(area, 0.0, $"{join}: S = 2.2 > S_crit must be uniformly CCW. Got {area:G17}.");
        }

        [Test]
        public void RoundCap_StartAndEnd_BulgeOnCorrectSide()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Round, roundSegments: 4);

            bool startBehind = false;
            bool endForward = false;
            foreach (var vert in v)
            {
                double2 ext = Extrude(vert);
                if (ext.x < -1e-9) startBehind = true;
                if (ext.x > 10.0 + 1e-9) endForward = true;
            }

            Assert.IsTrue(startBehind, "Round start cap must have extruded vertices with x < 0 (behind start point x=0).");
            Assert.IsTrue(endForward, "Round end cap must have extruded vertices with x > 10 (past end point x=10).");
        }

        [Test]
        public void RoundCap_PivotVertex_HasZeroNormal_AndZeroSide()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Round, roundSegments: 4);

            bool foundStartPivot = false;
            bool foundEndPivot = false;

            foreach (var vert in v)
            {
                if (math.abs(vert.Side) < 1e-6f)
                {
                    double2 across = Across2(vert);
                    double normalLen = math.sqrt(across.x * across.x + across.y * across.y);
                    Assert.That(normalLen, Is.LessThan(1e-9),
                        $"Cap pivot: Across must be (0,0). Got |Across|={normalLen:G}.");

                    double2 pos = Pos2(vert);
                    if (math.abs(pos.x) < 1e-9 && math.abs(pos.y) < 1e-9) foundStartPivot = true;
                    else if (math.abs(pos.x - 10.0) < 1e-9 && math.abs(pos.y) < 1e-9) foundEndPivot = true;
                }
            }

            Assert.IsTrue(foundStartPivot, "Round start cap must have a pivot vertex at (0,0) with side≈0 and Across≈(0,0).");
            Assert.IsTrue(foundEndPivot, "Round end cap must have a pivot vertex at (10,0) with side≈0 and Across≈(0,0).");
        }
    }
}
