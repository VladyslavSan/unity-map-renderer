// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// #5 B1 — the geometric core of curved text: walk a screen polyline by arc length, get point + tangent.
    /// </summary>
    [TestFixture]
    public class PolylineArcWalkerTests
    {
        private const float Tol = 1e-4f;

        private static PolylineArcWalker Walk(params float2[] pts)
        {
            var w = new PolylineArcWalker();
            w.Init(pts, pts.Length);
            return w;
        }

        [Test]
        public void StraightLine_TotalLength_PointAndConstantTangent()
        {
            PolylineArcWalker w = Walk(new float2(0, 0), new float2(10, 0), new float2(20, 0));
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
            PolylineArcWalker w = Walk(new float2(0, 0), new float2(10, 0));
            w.At(-5f, out float2 p, out _);
            Assert.AreEqual(0f, p.x, Tol);
            w.At(999f, out p, out _);
            Assert.AreEqual(10f, p.x, Tol);
        }

        [Test]
        public void LBend_TangentSwitchesToTheSecondSegment()
        {
            // right 10, then up 10.
            PolylineArcWalker w = Walk(new float2(0, 0), new float2(10, 0), new float2(10, 10));
            Assert.AreEqual(20f, w.TotalLength, Tol);

            w.At(5f, out float2 p, out float tan);   // on segment 0 (horizontal)
            Assert.AreEqual(new float2(5, 0).x, p.x, Tol); Assert.AreEqual(0f, p.y, Tol);
            Assert.AreEqual(0f, tan, Tol);

            w.At(15f, out p, out tan);               // 5 into segment 1 (vertical, +y)
            Assert.AreEqual(10f, p.x, Tol); Assert.AreEqual(5f, p.y, Tol);
            Assert.AreEqual(math.PI / 2f, tan, Tol, "vertical +y segment → tangent +pi/2");
        }

        [Test]
        public void DegenerateSegment_DoesNotCollapseTangent()
        {
            // a duplicated vertex (zero-length segment) must be skipped for the tangent.
            PolylineArcWalker w = Walk(new float2(0, 0), new float2(0, 0), new float2(10, 0));
            Assert.AreEqual(10f, w.TotalLength, Tol);
            w.At(5f, out float2 p, out float tan);
            Assert.AreEqual(5f, p.x, Tol);
            Assert.AreEqual(0f, tan, Tol, "tangent skips the zero-length segment, not atan2(0,0)");
        }

        [Test]
        public void FortyFiveDegreeSegment_TangentIsQuarterPi()
        {
            PolylineArcWalker w = Walk(new float2(0, 0), new float2(10, 10));
            Assert.AreEqual(math.sqrt(200f), w.TotalLength, 1e-3f);
            w.At(w.TotalLength * 0.5f, out float2 p, out float tan);
            Assert.AreEqual(5f, p.x, Tol); Assert.AreEqual(5f, p.y, Tol);
            Assert.AreEqual(math.PI / 4f, tan, Tol);
        }

        // Three-segment staircase: seg0 →x [0,10], seg1 ↑y [10,20], seg2 →x [20,30]. Distinct segments so a
        // stale cursor would land on the wrong one. Exercises the Lever A resumable cursor.
        private static PolylineArcWalker Staircase() =>
            Walk(new float2(0, 0), new float2(10, 0), new float2(10, 10), new float2(20, 10));

        private static void AssertAt(PolylineArcWalker w, float arc, float2 expectPt, float expectTan)
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
            PolylineArcWalker w = Staircase();
            Assert.AreEqual(30f, w.TotalLength, Tol);
            AssertAt(w, 25f, new float2(15, 10), 0f);            // seg2 (→x)
            AssertAt(w, 5f,  new float2(5, 0),   0f);            // seg0 (→x), cursor jumps back 2 segments
            AssertAt(w, 15f, new float2(10, 5),  math.PI / 2f);  // seg1 (↑y)
            AssertAt(w, 25f, new float2(15, 10), 0f);            // seg2 again, forward jump
        }

        [Test]
        public void Cursor_ReverseMonotonicSweep_MatchesForwardSweep()
        {
            // A reversed (keep-upright) label queries arcs in DECREASING order — the backward cursor walk must
            // give the same points as a fresh walker queried forward.
            PolylineArcWalker reverse = Staircase();
            PolylineArcWalker forward = Staircase();
            float[] arcs = { 3f, 8f, 12f, 18f, 22f, 27f };
            for (int i = arcs.Length - 1; i >= 0; i--)
            {
                reverse.At(arcs[i], out float2 rp, out float rt);
                forward.At(arcs[i], out float2 fp, out float ft); // independent walker, single lookup
                Assert.AreEqual(fp.x, rp.x, Tol); Assert.AreEqual(fp.y, rp.y, Tol);
                Assert.AreEqual(ft, rt, Tol);
            }
        }
    }
}
