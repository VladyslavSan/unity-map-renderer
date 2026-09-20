// GlobeLineSubdivisionTests (S91-C, C-3) — falsifiable teeth for the globe line curvature subdivision.
// The render snapshot only proves "not blank"; these assert the actual behaviour: a long-arc segment is
// densified proportionally to its great-circle span, a short segment is left alone, and every sub-point
// lies on the original chord in tile space. A no-op (or wrong-threshold) subdivision fails here.
//
// job-scheduling-design.md §8 stage 5 Group B: StyledLineTileBuilder.SubdivideCenterline (the NativeList
// container-plumbing wrapper this file used to drive) retired with the managed ring loop it belonged to.
// These teeth are re-pointed to LineCurvatureSubdivision.Subdivide, an engine-free helper unaffected by
// this stage.
//
// RECORDED GAP: this no longer observes the mesh path. LineCurvatureSubdivision.Subdivide's one production
// consumer is SymbolFeatureExtractor.cs (the SYMBOL path) — the mesh path runs its own Burst node,
// SubdivideJob (MapRenderer.Jobs), which shares only the STEP-COUNT policy (SegmentSteps) with
// Subdivide; its own linear-interpolation loop is a separate re-implementation (currently identical, but
// not the same code under test). LongArc_IsDensifiedProportionalToSpan and ShortArc_IsNotSubdivided still
// pin the shared SegmentSteps policy both paths call. SubPoints_LieOnTheTileSpaceChord_EndpointsPreserved
// does NOT observe SubdivideJob at all any more — a bad interpolation or a dropped endpoint there reds
// nothing here.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Tests.Globe
{
    public class GlobeLineSubdivisionTests
    {
        // Unit ECEF surface normal at (lon,lat) degrees — the per-point "up" the subdivider measures arcs from.
        private static double3 Normal(double lonDeg, double latDeg)
        {
            double lam = lonDeg * math.PI_DBL / 180.0;
            double phi = latDeg * math.PI_DBL / 180.0;
            double cp = math.cos(phi);
            return new double3(cp * math.cos(lam), cp * math.sin(lam), math.sin(phi));
        }

        private static System.Collections.Generic.List<double2> Subdivide(double2 a, double2 b, double3 upA, double3 upB)
            => LineCurvatureSubdivision.Subdivide(
                new[] { a, b }, new[] { upA, upB }, SphericalProjection.MaxCurveSegmentRad);

        [Test]
        public void LongArc_IsDensifiedProportionalToSpan()
        {
            // 90° arc (equator, 0°→90° lon) at ~2°/step ⇒ ≈ 45 sub-segments ⇒ ≈ 46 points.
            int c90 = Subdivide(new double2(0, 0), new double2(4096, 0), Normal(0, 0), Normal(90, 0)).Count;
            Assert.GreaterOrEqual(c90, 45, "90° arc must split into ~45 sub-segments");
            Assert.LessOrEqual(c90, 47, "…and not wildly more (≈ 90°/2°)");

            // 30° arc ⇒ ≈ 15 sub-segments — strictly fewer than the 90° case (proportional to span).
            int c30 = Subdivide(new double2(0, 0), new double2(4096, 0), Normal(0, 0), Normal(30, 0)).Count;
            Assert.Less(c30, c90, "a shorter arc must produce fewer points");
            Assert.GreaterOrEqual(c30, 15, "30° arc must split into ~15 sub-segments");
        }

        [Test]
        public void ShortArc_IsNotSubdivided()
        {
            // 1° arc < the 2° threshold ⇒ no subdivision ⇒ just the 2 endpoints.
            int c = Subdivide(new double2(10, 20), new double2(30, 20), Normal(0, 0), Normal(1, 0)).Count;
            Assert.AreEqual(2, c, "a segment under the arc threshold stays a single chord (2 points)");
        }

        [Test]
        public void SubPoints_LieOnTheTileSpaceChord_EndpointsPreserved()
        {
            var a = new double2(100, 200);
            var b = new double2(900, 600);
            var sub = Subdivide(a, b, Normal(0, 0), Normal(60, 0));
            int c = sub.Count;

            Assert.AreEqual(a, sub[0], "first point preserved");
            Assert.AreEqual(b, sub[c - 1], "last point preserved");
            // Every sub-point is the linear interpolation a→b (collinear, monotone in x).
            for (int i = 1; i < c; i++)
            {
                double t = (sub[i].x - a.x) / (b.x - a.x);
                double2 expected = a + (b - a) * t;
                Assert.AreEqual(expected.y, sub[i].y, 1e-6, $"sub-point {i} off the chord");
                Assert.Greater(t, (sub[i - 1].x - a.x) / (b.x - a.x) - 1e-9, "monotone along the chord");
            }
        }
    }
}
