// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// #5 B1 — the geometric core of curved text: walk a screen polyline by arc length, get point + tangent.
    /// Drives <see cref="PolylineArcMath"/> directly (the SCREEN/<c>float2</c> path) over a local resumable
    /// cursor, the same idiom <c>SymbolStagingMath</c> uses in production; pairs with the WORLD/<c>double3</c>
    /// side in <see cref="PolylineArcMathWorldSampleTests"/>.
    /// </summary>
    [TestFixture]
    public class PolylineArcMathScreenTests
    {
        private const float Tol = 1e-4f;

        // A resumable arc walk over one polyline: cumulative lengths built once, cursor threaded across
        // repeated At() calls exactly as SymbolStagingMath.cs's local `cursor` is (see :341).
        private struct ArcCursor
        {
            public float2[] Points;
            public float[] Cumulative;
            public int Count;
            public float TotalLength;
            public int Cursor;

            public void At(float arc, out float2 point, out float tangentRadians)
                => PolylineArcMath.At(Points, Cumulative, Count, TotalLength, arc, ref Cursor, out point, out tangentRadians);
        }

        private static ArcCursor Walk(params float2[] pts)
        {
            var cumulative = new float[pts.Length];
            float total = PolylineArcMath.BuildCumulative(pts, pts.Length, cumulative);
            return new ArcCursor { Points = pts, Cumulative = cumulative, Count = pts.Length, TotalLength = total, Cursor = 0 };
        }

        [Test]
        public void StraightLine_TotalLength_PointAndConstantTangent()
        {
            ArcCursor w = Walk(new float2(0, 0), new float2(10, 0), new float2(20, 0));
            Assert.AreEqual(20f, w.TotalLength, Tol);

            w.At(5f, out float2 p, out float tan);
            Assert.AreEqual(5f, p.x, Tol); Assert.AreEqual(0f, p.y, Tol);
            Assert.AreEqual(0f, tan, Tol, "horizontal line → tangent 0");

            w.At(15f, out p, out tan); // second segment, still horizontal
            Assert.AreEqual(15f, p.x, Tol); Assert.AreEqual(0f, tan, Tol);
        }

        [Test]
        public void ClampsBeyondEnds()
        {
            ArcCursor w = Walk(new float2(0, 0), new float2(10, 0));
            w.At(-5f, out float2 p, out _);
            Assert.AreEqual(0f, p.x, Tol);
            w.At(999f, out p, out _);
            Assert.AreEqual(10f, p.x, Tol);
        }

        [Test]
        public void LBend_TangentSwitchesToTheSecondSegment()
        {
            // right 10, then up 10.
            ArcCursor w = Walk(new float2(0, 0), new float2(10, 0), new float2(10, 10));
            Assert.AreEqual(20f, w.TotalLength, Tol);

            w.At(5f, out float2 p, out float tan);   // on segment 0 (horizontal)
            Assert.AreEqual(new float2(5, 0).x, p.x, Tol); Assert.AreEqual(0f, p.y, Tol);
            Assert.AreEqual(0f, tan, Tol);

            w.At(15f, out p, out tan);               // 5 into segment 1 (vertical, +y)
            Assert.AreEqual(10f, p.x, Tol); Assert.AreEqual(5f, p.y, Tol);
            Assert.AreEqual(math.PI / 2f, tan, Tol, "vertical +y segment → tangent +pi/2");
        }

        // The float2/screen analogue of PolylineArcMathWorldSampleTests.SampleWorld_SkipsADegenerateZeroLengthSegment:
        // called DIRECTLY at i=0 (not via arc-distance routing), because a degenerate first segment always has
        // zero cumulative length, so any arc > 0 makes SegmentAt route straight past it to segment 1 — the skip
        // loop inside SegmentTangent itself is never reached that way. The second segment is a 45-degree
        // diagonal, not horizontal, so a broken skip (falling through to atan2(0,0) == 0) is distinguishable
        // from the correct answer instead of coincidentally matching it.
        [Test]
        public void DegenerateSegment_DoesNotCollapseTangent()
        {
            var points = new[] { new float2(0, 0), new float2(0, 0), new float2(10, 10) };
            float tangent = PolylineArcMath.SegmentTangent(points, count: 3, i: 0);
            Assert.AreEqual(math.PI / 4f, tangent, Tol,
                "tangent skips the zero-length segment [0,1] and reports [1,2]'s 45-degree direction, not atan2(0,0)");
        }

        [Test]
        public void FortyFiveDegreeSegment_TangentIsQuarterPi()
        {
            ArcCursor w = Walk(new float2(0, 0), new float2(10, 10));
            Assert.AreEqual(math.sqrt(200f), w.TotalLength, 1e-3f);
            w.At(w.TotalLength * 0.5f, out float2 p, out float tan);
            Assert.AreEqual(5f, p.x, Tol); Assert.AreEqual(5f, p.y, Tol);
            Assert.AreEqual(math.PI / 4f, tan, Tol);
        }

        // Three-segment staircase: seg0 →x [0,10], seg1 ↑y [10,20], seg2 →x [20,30]. Distinct segments so a
        // stale cursor would land on the wrong one. Exercises the Lever A resumable cursor.
        private static ArcCursor Staircase() =>
            Walk(new float2(0, 0), new float2(10, 0), new float2(10, 10), new float2(20, 10));

        private static void AssertAt(ref ArcCursor w, float arc, float2 expectPt, float expectTan)
        {
            w.At(arc, out float2 p, out float tan);
            Assert.AreEqual(expectPt.x, p.x, Tol); Assert.AreEqual(expectPt.y, p.y, Tol);
            Assert.AreEqual(expectTan, tan, Tol);
        }

        [Test]
        public void Cursor_OutOfOrderQueries_EachResolvesToTheContainingSegment()
        {
            // A forward jump (25), a big backward jump (5), then a mid jump (15): the resumable cursor must
            // land on the right segment every time, not on wherever the previous query left it.
            ArcCursor w = Staircase();
            Assert.AreEqual(30f, w.TotalLength, Tol);
            // Each Cursor check below only means anything because AssertAt takes `w` by `ref`: without it,
            // the mutation inside PolylineArcMath.At would land on AssertAt's own copy and w.Cursor here
            // would never move off 0 — the point/tangent asserts alone can't tell the difference, since
            // SegmentAt's guarded walk finds the right segment from ANY starting cursor.
            AssertAt(ref w, 25f, new float2(15, 10), 0f);            // seg2 (→x)
            Assert.AreEqual(2, w.Cursor, "cursor lands on segment 2 for arc 25");
            AssertAt(ref w, 5f,  new float2(5, 0),   0f);            // seg0 (→x), cursor jumps back 2 segments
            Assert.AreEqual(0, w.Cursor, "cursor jumps back to segment 0 for arc 5");
            AssertAt(ref w, 15f, new float2(10, 5),  math.PI / 2f);  // seg1 (↑y)
            Assert.AreEqual(1, w.Cursor, "cursor advances to segment 1 for arc 15");
            AssertAt(ref w, 25f, new float2(15, 10), 0f);            // seg2 again, forward jump
            Assert.AreEqual(2, w.Cursor, "cursor advances forward to segment 2 again for arc 25");
        }

        [Test]
        public void Cursor_ReverseMonotonicSweep_MatchesForwardSweep()
        {
            // A reversed (keep-upright) symbol queries arcs in DECREASING order — the backward cursor walk must
            // give the same points as a fresh walker queried forward. `forward` is rebuilt INSIDE the loop:
            // built once outside it, it would walk the SAME descending arcs in the SAME order as `reverse`,
            // making both cursors evolve identically and the two sides agree no matter what At() computes.
            ArcCursor reverse = Staircase();
            float[] arcs = { 3f, 8f, 12f, 18f, 22f, 27f };
            for (int i = arcs.Length - 1; i >= 0; i--)
            {
                reverse.At(arcs[i], out float2 rp, out float rt);
                ArcCursor forward = Staircase(); // independent walker, single cold lookup
                forward.At(arcs[i], out float2 fp, out float ft);
                Assert.AreEqual(fp.x, rp.x, Tol); Assert.AreEqual(fp.y, rp.y, Tol);
                Assert.AreEqual(ft, rt, Tol);
            }
        }
    }
}
