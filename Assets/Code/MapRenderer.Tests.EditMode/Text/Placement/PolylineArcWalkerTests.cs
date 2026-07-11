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
    }
}
