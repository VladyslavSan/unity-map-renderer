using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>The Core distance math for the pre-projection far-plane symbol cull: a symbol farther from the camera
    /// than the cull distance is culled; a non-positive distance disables it; and the per-frame rebase rotation is
    /// applied to the anchor (so the cull measures the true camera→anchor separation, not a pre-rebase one). The
    /// gather-path WIRING (fraction × far, fade-out-in-place) is pinned separately in the EditMode placement teeth.</summary>
    [TestFixture]
    public class SymbolFarPlaneCullTests
    {
        // Camera at the floating origin (rebased frame), so with an identity rebase the camera→anchor distance is
        // just the anchor's offset from the scene origin — easy to reason about.
        private static readonly double3 Origin = new double3(1000.0, -2000.0, 3000.0);

        /// <summary>An anchor within the cull distance survives; one beyond it is culled. The scene origin is
        /// non-zero to prove the double-subtract runs (a naive |anchor| would mis-measure).</summary>
        [Test]
        public void WithinDistance_Survives_BeyondDistance_Culled()
        {
            var cam = double3.zero;               // camera at the origin in the rebased frame
            var near = Origin + new double3(0.0, 0.0, 300.0); // 300 m from camera
            var far  = Origin + new double3(0.0, 0.0, 700.0); // 700 m from camera

            Assert.IsFalse(SymbolFarPlaneCull.IsCulled(near, Origin, float3x3.identity, cam, 500.0),
                "300 m < 500 m cull distance ⇒ kept");
            Assert.IsTrue(SymbolFarPlaneCull.IsCulled(far, Origin, float3x3.identity, cam, 500.0),
                "700 m > 500 m cull distance ⇒ culled");
        }

        /// <summary>Distance is measured from the CAMERA, not the scene origin: shifting the camera toward a distant
        /// anchor keeps it, and away from a near anchor culls it — the opposite of a look-at-radius measure.</summary>
        [Test]
        public void DistanceIsMeasuredFromCamera_NotOrigin()
        {
            var anchor = Origin + new double3(0.0, 0.0, 700.0); // 700 m from the origin
            var camNear = new double3(0.0, 0.0, 400.0);         // camera 400 m along +Z ⇒ 300 m from anchor
            var camFar  = new double3(0.0, 0.0, -400.0);        // camera 400 m along −Z ⇒ 1100 m from anchor

            Assert.IsFalse(SymbolFarPlaneCull.IsCulled(anchor, Origin, float3x3.identity, camNear, 500.0),
                "camera 300 m from the anchor ⇒ kept, though it is 700 m from the origin");
            Assert.IsTrue(SymbolFarPlaneCull.IsCulled(anchor, Origin, float3x3.identity, camFar, 500.0),
                "camera 1100 m from the anchor ⇒ culled");
        }

        /// <summary>A non-positive cull distance disables the cull (keep every symbol) — the safe fallback for a
        /// mis-wired caller, so it degrades to "cull nothing" rather than culling everything.</summary>
        [Test]
        public void NonPositiveDistance_DisablesCull()
        {
            var farAway = Origin + new double3(1e6, 0.0, 0.0);
            Assert.IsFalse(SymbolFarPlaneCull.IsCulled(farAway, Origin, float3x3.identity, double3.zero, 0.0),
                "0 ⇒ cull disabled");
            Assert.IsFalse(SymbolFarPlaneCull.IsCulled(farAway, Origin, float3x3.identity, double3.zero, -1.0),
                "negative ⇒ cull disabled");
        }

        /// <summary>The rebase rotation is applied to the anchor before the distance is taken. A 180° rotation about
        /// Y (unambiguous — sin = 0, no handedness question) flips the anchor's X across an off-axis camera, moving
        /// it from inside to outside the cull distance. If the rebase were skipped the decision would not change.</summary>
        [Test]
        public void RebaseRotation_IsAppliedToAnchor()
        {
            var cam    = new double3(100.0, 0.0, 0.0);          // off-axis camera (in the rebased frame)
            var anchor = Origin + new double3(400.0, 0.0, 0.0); // local offset (400,0,0)

            // Identity: rebased local (400,0,0), distance to camera (100,0,0) = 300 m.
            Assert.IsFalse(SymbolFarPlaneCull.IsCulled(anchor, Origin, float3x3.identity, cam, 400.0),
                "identity rebase ⇒ 300 m from camera ⇒ kept");

            // Ry(180°): local (400,0,0) → (−400,0,0); distance to (100,0,0) = 500 m ⇒ now culled.
            var ry180 = new float3x3(-1f, 0f, 0f,
                                      0f, 1f, 0f,
                                      0f, 0f, -1f);
            Assert.IsTrue(SymbolFarPlaneCull.IsCulled(anchor, Origin, ry180, cam, 400.0),
                "180° rebase ⇒ 500 m from camera ⇒ culled (proves the rebase is applied)");
        }

        /// <summary>Pins <c>float3x3</c>'s 9-scalar constructor to the real Unity.Mathematics layout
        /// (row-major arguments, column-major storage — verified by reflecting the real
        /// UnityEngine.MathematicsModule.dll) with a NON-symmetric matrix. The tests above only ever
        /// build <c>ry180</c>, a diagonal matrix that reads identically under transpose, so they could
        /// never catch a transposed constructor in the Tools/core-tests shim — this is the tooth that
        /// would.</summary>
        [Test]
        public void Float3x3_NineArgConstructor_IsRowMajorArgsColumnMajorStorage()
        {
            var m = new float3x3(1f, 2f, 3f,
                                  4f, 5f, 6f,
                                  7f, 8f, 9f);

            Assert.That(m.c0.x, Is.EqualTo(1f)); Assert.That(m.c0.y, Is.EqualTo(4f)); Assert.That(m.c0.z, Is.EqualTo(7f));
            Assert.That(m.c1.x, Is.EqualTo(2f)); Assert.That(m.c1.y, Is.EqualTo(5f)); Assert.That(m.c1.z, Is.EqualTo(8f));
            Assert.That(m.c2.x, Is.EqualTo(3f)); Assert.That(m.c2.y, Is.EqualTo(6f)); Assert.That(m.c2.z, Is.EqualTo(9f));
        }
    }
}
