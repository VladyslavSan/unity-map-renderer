// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Uses only MapRenderer.Core types + Unity.Mathematics (shimmed headless).

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// B-3: the pre-projection horizon/distance cull. Teeth: the radius scales with viewport-spans × the larger
    /// viewport dimension × ground metres/pixel; a label within the radius is kept and one beyond is culled
    /// (3-D distance); and a non-positive radius/input DISABLES the cull (the safe pre-B-3 fallback — a mis-wired
    /// caller must not cull everything).
    /// </summary>
    [TestFixture]
    public class LabelViewDistanceTests
    {
        [Test]
        public void CullRadius_ScalesWithSpansViewportAndGroundResolution()
        {
            // 4 spans × max(800,600) × 10 m/px = 32000 m.
            double r = LabelViewDistance.CullRadiusMeters(new double2(800, 600), groundResolutionMeters: 10.0, viewportSpans: 4.0);
            Assert.AreEqual(32000.0, r, 1e-6);
            // Uses the LARGER dimension (so a tall viewport isn't under-radiused).
            double rTall = LabelViewDistance.CullRadiusMeters(new double2(600, 800), 10.0, 4.0);
            Assert.AreEqual(32000.0, rTall, 1e-6, "the larger viewport dimension drives the radius");
        }

        [Test]
        public void CullRadius_NonPositiveInputs_YieldZero()
        {
            Assert.AreEqual(0.0, LabelViewDistance.CullRadiusMeters(new double2(800, 600), 10.0, 0.0), 1e-9);
            Assert.AreEqual(0.0, LabelViewDistance.CullRadiusMeters(new double2(800, 600), -5.0, 4.0), 1e-9);
            Assert.AreEqual(0.0, LabelViewDistance.CullRadiusMeters(new double2(0, 0), 10.0, 4.0), 1e-9);
        }

        [Test]
        public void IsCulled_KeepsWithinRadius_CullsBeyond()
        {
            double3 origin = new double3(1000, 0, 2000);
            double radius = 500.0;
            Assert.IsFalse(LabelViewDistance.IsCulled(origin + new double3(300, 0, 300), origin, radius),
                "√(300²+300²)=424 < 500 → kept");
            Assert.IsTrue(LabelViewDistance.IsCulled(origin + new double3(400, 0, 400), origin, radius),
                "√(400²+400²)=566 > 500 → culled");
        }

        [Test]
        public void IsCulled_Uses3DDistance()
        {
            double3 origin = new double3(0, 0, 0);
            // Distance purely on the Y (up) axis still counts (a globe anchor is off the ground plane).
            Assert.IsTrue(LabelViewDistance.IsCulled(new double3(0, 600, 0), origin, cullRadiusMeters: 500.0));
            Assert.IsFalse(LabelViewDistance.IsCulled(new double3(0, 400, 0), origin, cullRadiusMeters: 500.0));
        }

        [Test]
        public void IsCulled_NonPositiveRadius_DisablesTheCull()
        {
            double3 origin = new double3(0, 0, 0);
            double3 farAway = new double3(1e9, 0, 1e9);
            Assert.IsFalse(LabelViewDistance.IsCulled(farAway, origin, 0.0), "radius 0 → cull disabled (keep all)");
            Assert.IsFalse(LabelViewDistance.IsCulled(farAway, origin, -1.0), "negative radius → cull disabled");
        }
    }
}
