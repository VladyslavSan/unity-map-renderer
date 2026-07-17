// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S4: unit tests for <see cref="LineCurvatureSubdivision"/> — the shared projection-subdivision policy
    /// (<see cref="LineCurvatureSubdivision.SegmentSteps"/>) and its managed densifier
    /// (<see cref="LineCurvatureSubdivision.Subdivide"/>). Engine-free; both runners.
    /// </summary>
    [TestFixture]
    public class LineCurvatureSubdivisionTests
    {
        private static double VecLen(double2 v) => Math.Sqrt(v.x * v.x + v.y * v.y);

        // Two unit vectors in the XY plane, `angleRad` apart (dot = cos(angleRad) exactly).
        private static (double3 a, double3 b) UpsApart(double angleRad)
            => (new double3(1.0, 0.0, 0.0), new double3(math.cos(angleRad), math.sin(angleRad), 0.0));

        // ─── S4-T1: Mercator ∞ = identity ──────────────────────────────────────────────────

        [Test]
        public void Subdivide_InfiniteTolerance_ReturnsPathValueUnchanged()
        {
            var path = new List<double2> { new double2(0, 0), new double2(3, 7), new double2(10, 10) };
            // Arbitrary — and deliberately NOT unit vectors, since the ∞ early-out must never read them.
            var ups = new List<double3> { new double3(1, 2, 3), new double3(-9, 0, 4), new double3(5, 5, 5) };

            List<double2> dense = LineCurvatureSubdivision.Subdivide(path, ups, double.PositiveInfinity);

            Assert.AreEqual(path.Count, dense.Count, "∞ tolerance must not insert any points");
            for (int i = 0; i < path.Count; i++)
            {
                Assert.AreEqual(path[i].x, dense[i].x, 1e-12, $"point[{i}].x unchanged");
                Assert.AreEqual(path[i].y, dense[i].y, 1e-12, $"point[{i}].y unchanged");
            }
        }

        // ─── S4-T2: globe splits a long segment; inserted points collinear ────────────────

        [Test]
        public void Subdivide_LargeArc_SplitsIntoExpectedCountWithCollinearInsertions()
        {
            const double angleRad = 0.2;
            const double maxRefineAngleRad = 0.035; // ~2 degrees
            (double3 upA, double3 upB) = UpsApart(angleRad);

            var a = new double2(0, 0);
            var b = new double2(100, 40);
            var path = new List<double2> { a, b };
            var ups = new List<double3> { upA, upB };

            List<double2> dense = LineCurvatureSubdivision.Subdivide(path, ups, maxRefineAngleRad);

            int expectedSegs = (int)Math.Ceiling(angleRad / maxRefineAngleRad);
            Assert.AreEqual(1 + expectedSegs, dense.Count, "count == 1 + ceil(angle/tolerance)");
            Assert.AreEqual(a.x, dense[0].x, 1e-12, "first point.x unchanged");
            Assert.AreEqual(a.y, dense[0].y, 1e-12, "first point.y unchanged");
            Assert.AreEqual(b.x, dense[dense.Count - 1].x, 1e-12, "last point.x unchanged");
            Assert.AreEqual(b.y, dense[dense.Count - 1].y, 1e-12, "last point.y unchanged");

            // Structural collinearity lemma: every inserted point M lies ON the chord A-B, so
            // |AM| + |MB| == |AB| (the tile-local arc length at every original vertex is preserved).
            double abLen = VecLen(b - a);
            for (int i = 1; i < dense.Count - 1; i++)
            {
                double amLen = VecLen(dense[i] - a);
                double mbLen = VecLen(b - dense[i]);
                Assert.AreEqual(abLen, amLen + mbLen, 1e-9, $"inserted point[{i}] collinear on A-B");
            }
        }

        // ─── S4-T3: bounded — always-bound-loops ───────────────────────────────────────────

        [Test]
        public void Subdivide_PathologicalTolerance_CapsAtMaxCurveSegments()
        {
            // Antipodal ups: angle == PI, tolerance ~0 — the split count would otherwise be unbounded.
            var upA = new double3(1.0, 0.0, 0.0);
            var upB = new double3(-1.0, 0.0, 0.0);
            var path = new List<double2> { new double2(0, 0), new double2(1, 0) };
            var ups = new List<double3> { upA, upB };

            List<double2> dense = LineCurvatureSubdivision.Subdivide(path, ups, 1e-9);

            Assert.AreEqual(1 + LineCurvatureSubdivision.MaxCurveSegments, dense.Count,
                "a pathological tolerance must still be bounded by MaxCurveSegments");
        }

        // ─── S4-T4: SegmentSteps policy parity ─────────────────────────────────────────────

        [Test]
        public void SegmentSteps_InfiniteTolerance_ReturnsOneForAnyUps()
        {
            Assert.AreEqual(1, LineCurvatureSubdivision.SegmentSteps(
                new double3(1, 0, 0), new double3(0, 1, 0), double.PositiveInfinity));
            Assert.AreEqual(1, LineCurvatureSubdivision.SegmentSteps(
                new double3(1, 0, 0), new double3(-1, 0, 0), double.PositiveInfinity));
        }

        [Test]
        public void SegmentSteps_RepresentativeFiniteAngle_MatchesCeilDivision()
        {
            const double angleRad = 0.2;
            const double maxRefineAngleRad = 0.035;
            (double3 upA, double3 upB) = UpsApart(angleRad);

            int segs = LineCurvatureSubdivision.SegmentSteps(upA, upB, maxRefineAngleRad);

            Assert.AreEqual((int)Math.Ceiling(angleRad / maxRefineAngleRad), segs);
        }

        [Test]
        public void SegmentSteps_Extremes_StayWithinOneToMaxCurveSegments()
        {
            // Coincident ups (angle == 0) still yields >= 1 step.
            int zeroAngle = LineCurvatureSubdivision.SegmentSteps(
                new double3(1, 0, 0), new double3(1, 0, 0), 0.035);
            Assert.AreEqual(1, zeroAngle);

            // Antipodal ups (angle == PI) at a tiny tolerance is capped, never unbounded.
            int maxAngle = LineCurvatureSubdivision.SegmentSteps(
                new double3(1, 0, 0), new double3(-1, 0, 0), 1e-9);
            Assert.AreEqual(LineCurvatureSubdivision.MaxCurveSegments, maxAngle);
        }
    }
}
