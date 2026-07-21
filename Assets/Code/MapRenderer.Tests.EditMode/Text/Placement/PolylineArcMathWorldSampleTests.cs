// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// Stage AC (curved-world) T-CPU: <see cref="PolylineArcMath.SegmentAt"/> (the resumable segment search
    /// factored out of <see cref="PolylineArcMath.At"/>) and <see cref="PolylineArcMath.SampleWorld"/> (the
    /// <c>double3</c> world-polyline sampler at an already-resolved <c>(seg,t)</c>) — the seam
    /// <c>LabelStagingMath.StageCurvedAnchor</c> uses to bake a per-glyph world anchor/tangent at the SAME
    /// index the screen arc walk resolves. Pins the off-by-one seam at segment boundaries and the
    /// zero-length-segment skip <see cref="PolylineArcMath.SegmentTangent"/> already has for the screen path.
    /// </summary>
    [TestFixture]
    public class PolylineArcMathWorldSampleTests
    {
        private const float Tol = 1e-4f;
        private const double DTol = 1e-9;

        // A 3-vertex staircase: seg0 [0,10] →x, seg1 [10,20] ↑y — cumulative = {0, 10, 20}.
        private static float[] Cumulative() => new[] { 0f, 10f, 20f };

        [Test]
        public void SegmentAt_MidSegment_ResolvesTheContainingSegmentAndT()
        {
            float[] cum = Cumulative();
            int cursor = 0;
            PolylineArcMath.SegmentAt(cum, count: 3, total: 20f, arc: 5f, ref cursor, out int seg, out float t);
            Assert.AreEqual(0, seg, "arc 5 sits mid segment 0");
            Assert.AreEqual(0.5f, t, Tol);

            PolylineArcMath.SegmentAt(cum, count: 3, total: 20f, arc: 15f, ref cursor, out seg, out t);
            Assert.AreEqual(1, seg, "arc 15 sits mid segment 1");
            Assert.AreEqual(0.5f, t, Tol);
        }

        // The off-by-one seam: right at a segment boundary the walk must land on the SAME segment on either
        // side of it (a perturbation of ±1 in `seg` would sample the WRONG world edge entirely).
        [Test]
        public void SegmentAt_AtAndAroundASegmentBoundary_DoesNotOffByOne()
        {
            float[] cum = Cumulative();
            int cursor = 0;

            // Exactly at the vertex (arc=10): the walk's `cumulative[seg+1] < arc` / `cumulative[seg] >= arc`
            // guards land on segment 0 with t=1 (the boundary belongs to the segment it terminates, not the
            // one it starts — mirrors PolylineArcMath.At's own interior resolution).
            PolylineArcMath.SegmentAt(cum, count: 3, total: 20f, arc: 10f, ref cursor, out int segAt, out float tAt);
            Assert.AreEqual(0, segAt, "the vertex arc belongs to the segment it terminates");
            Assert.AreEqual(1f, tAt, Tol);

            // Just before / just after the vertex must land on segment 0 / segment 1 respectively — never off
            // by one regardless of which side the resumable cursor last stopped on.
            PolylineArcMath.SegmentAt(cum, count: 3, total: 20f, arc: 9.999f, ref cursor, out int segBefore, out _);
            Assert.AreEqual(0, segBefore, "just before the vertex is still segment 0");

            PolylineArcMath.SegmentAt(cum, count: 3, total: 20f, arc: 10.001f, ref cursor, out int segAfter, out _);
            Assert.AreEqual(1, segAfter, "just after the vertex is segment 1, not segment 0 or 2");
        }

        [Test]
        public void SegmentAt_ReverseMonotonicSweep_MatchesForwardSweep()
        {
            // A reversed (keep-upright) label queries arcs in DECREASING order — the backward-walking cursor
            // must resolve the same (seg,t) as a query starting fresh at that arc.
            float[] cum = Cumulative();
            float[] arcs = { 2f, 8f, 12f, 18f };
            int reverseCursor = 0;
            for (int i = arcs.Length - 1; i >= 0; i--)
            {
                PolylineArcMath.SegmentAt(cum, 3, 20f, arcs[i], ref reverseCursor, out int rSeg, out float rT);
                int freshCursor = 0;
                PolylineArcMath.SegmentAt(cum, 3, 20f, arcs[i], ref freshCursor, out int fSeg, out float fT);
                Assert.AreEqual(fSeg, rSeg, $"seg mismatch at arc {arcs[i]}");
                Assert.AreEqual(fT, rT, Tol, $"t mismatch at arc {arcs[i]}");
            }
        }

        [Test]
        public void SampleWorld_ReproducesTheExactWorldLerp()
        {
            var world = new[] { new double3(0, 0, 0), new double3(10, 0, 0), new double3(10, 0, 10) };

            PolylineArcMath.SampleWorld(world, count: 3, seg: 0, t: 0.5f, out double3 point, out double3 dir);
            Assert.AreEqual(5.0, point.x, DTol);
            Assert.AreEqual(0.0, point.y, DTol);
            Assert.AreEqual(0.0, point.z, DTol);
            Assert.AreEqual(10.0, dir.x, DTol); Assert.AreEqual(0.0, dir.z, DTol);

            PolylineArcMath.SampleWorld(world, count: 3, seg: 1, t: 0.5f, out point, out dir);
            Assert.AreEqual(10.0, point.x, DTol);
            Assert.AreEqual(0.0, point.y, DTol);
            Assert.AreEqual(5.0, point.z, DTol);
            Assert.AreEqual(0.0, dir.x, DTol); Assert.AreEqual(10.0, dir.z, DTol);
        }

        // The double3 analogue of PolylineArcWalkerTests.DegenerateSegment_DoesNotCollapseTangent: a
        // duplicated world vertex (zero-length segment) must be skipped when deriving the fallback direction,
        // not collapse to a zero vector.
        [Test]
        public void SampleWorld_SkipsADegenerateZeroLengthSegment_ForTheFallbackDirection()
        {
            var world = new[] { new double3(0, 0, 0), new double3(0, 0, 0), new double3(10, 0, 0) };

            PolylineArcMath.SampleWorld(world, count: 3, seg: 0, t: 0f, out double3 point, out double3 dir);
            Assert.AreEqual(0.0, point.x, DTol);
            Assert.AreEqual(10.0, dir.x, DTol, "the zero-length segment [0,1] is skipped in favour of [1,2]");
            Assert.AreEqual(0.0, dir.z, DTol);
        }

        // A straight diagonal path: every mid-segment sample must yield the SAME tangent direction (the
        // decisive "no drift along a straight run" property) and land at the analytically exact (seg,t).
        [Test]
        public void SampleWorld_StraightDiagonalPath_ConstantTangentDirection()
        {
            var world = new[] { new double3(0, 0, 0), new double3(20, 0, 20) };
            float[] cum = { 0f, (float)math.sqrt(800.0) };
            float total = cum[1];

            int cursor = 0;
            PolylineArcMath.SegmentAt(cum, 2, total, total * 0.5f, ref cursor, out int seg, out float t);
            Assert.AreEqual(0, seg);
            Assert.AreEqual(0.5f, t, Tol);

            PolylineArcMath.SampleWorld(world, 2, seg, t, out double3 point, out double3 dir);
            Assert.AreEqual(10.0, point.x, 1e-3);
            Assert.AreEqual(10.0, point.z, 1e-3);
            Assert.AreEqual(dir.x, dir.z, DTol, "a 45-degree diagonal has an equal x/z tangent component");
        }
    }
}
