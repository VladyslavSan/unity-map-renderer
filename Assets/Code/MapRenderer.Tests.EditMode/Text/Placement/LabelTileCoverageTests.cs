// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj). Uses only MapRenderer.Core types + Unity.Mathematics (shimmed headless).

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// The per-tile screen-coverage pre-cull metric. Teeth: a full-screen tile reports ~1.0 coverage (survives), a
    /// tiny tile reports its shoelace fraction (culled below the threshold), a behind-camera corner yields +inf
    /// (never culled — straddles the near plane), and a non-positive threshold DISABLES the cull (a mis-wired
    /// caller must not cull everything — mirrors <see cref="LabelViewDistance"/>).
    /// </summary>
    [TestFixture]
    public class LabelTileCoverageTests
    {
        private static readonly double2 Viewport = new double2(1000, 1000);
        private static readonly double3 Origin = new double3(0, 0, 0);

        // Identity viewProj + origin 0: a render point (x,y,z) → NDC (x,y) → screen ((x/2+0.5)*W, (y/2+0.5)*H).
        // So a corner ring in NDC directly controls the projected quad's screen area.
        private static readonly float4x4 Identity = float4x4.identity;

        // A viewProj whose 4th column forces clip.w = -1 for every point (mul(m,(x,y,z,1)).w = -1) → every corner
        // reads as behind the camera (clip.w <= 0), so ScreenCoverage returns +inf.
        private static readonly float4x4 AllBehind = new float4x4(
            new float4(1, 0, 0, 0), new float4(0, 1, 0, 0), new float4(0, 0, 1, 0), new float4(0, 0, 0, -1));

        [Test]
        public void ScreenCoverage_FullViewportTile_IsOne()
        {
            // NDC corners (-1,-1),(1,-1),(1,1),(-1,1) → screen (0,0),(1000,0),(1000,1000),(0,1000) → area == viewport.
            double c = LabelTileCoverage.ScreenCoverage(
                new double3(-1, -1, 0), new double3(1, -1, 0), new double3(1, 1, 0), new double3(-1, 1, 0),
                Origin, Identity, Viewport, float3x3.identity);
            Assert.AreEqual(1.0, c, 1e-6);
            Assert.IsFalse(LabelTileCoverage.IsCulled(c, 0.05), "a full-screen tile is never culled");
        }

        [Test]
        public void ScreenCoverage_TinyTile_IsSmallFraction_AndCulled()
        {
            // NDC span 0.2 × 0.2 → coverage 0.25 · 0.2 · 0.2 = 0.01 (1% of the screen).
            double c = LabelTileCoverage.ScreenCoverage(
                new double3(-0.1, -0.1, 0), new double3(0.1, -0.1, 0), new double3(0.1, 0.1, 0), new double3(-0.1, 0.1, 0),
                Origin, Identity, Viewport, float3x3.identity);
            Assert.AreEqual(0.01, c, 1e-6);
            Assert.IsTrue(LabelTileCoverage.IsCulled(c, 0.05), "1% < 5% → culled");
            Assert.IsFalse(LabelTileCoverage.IsCulled(c, 0.005), "1% > 0.5% → kept");
        }

        [Test]
        public void ScreenCoverage_WindingIndependent()
        {
            // Reversed ring order (CW vs CCW) flips the shoelace sign; the abs must yield the same coverage.
            double ccw = LabelTileCoverage.ScreenCoverage(
                new double3(-1, -1, 0), new double3(1, -1, 0), new double3(1, 1, 0), new double3(-1, 1, 0),
                Origin, Identity, Viewport, float3x3.identity);
            double cw = LabelTileCoverage.ScreenCoverage(
                new double3(-1, 1, 0), new double3(1, 1, 0), new double3(1, -1, 0), new double3(-1, -1, 0),
                Origin, Identity, Viewport, float3x3.identity);
            Assert.AreEqual(ccw, cw, 1e-9);
        }

        [Test]
        public void ScreenCoverage_BehindCamera_IsInfinite_NeverCulled()
        {
            double c = LabelTileCoverage.ScreenCoverage(
                new double3(-1, -1, 0), new double3(1, -1, 0), new double3(1, 1, 0), new double3(-1, 1, 0),
                Origin, AllBehind, Viewport, float3x3.identity);
            Assert.IsTrue(double.IsPositiveInfinity(c), "a behind-camera corner → +inf coverage");
            Assert.IsFalse(LabelTileCoverage.IsCulled(c, 0.05), "+inf coverage is never culled");
        }

        [Test]
        public void ScreenCoverage_DegenerateViewport_IsInfinite()
        {
            double c = LabelTileCoverage.ScreenCoverage(
                new double3(-1, -1, 0), new double3(1, -1, 0), new double3(1, 1, 0), new double3(-1, 1, 0),
                Origin, Identity, new double2(0, 0), float3x3.identity);
            Assert.IsTrue(double.IsPositiveInfinity(c), "a zero-area viewport → cull nothing");
        }

        [Test]
        public void IsCulled_NonPositiveThreshold_DisablesTheCull()
        {
            Assert.IsFalse(LabelTileCoverage.IsCulled(0.0001, 0.0), "threshold 0 → cull disabled (keep all)");
            Assert.IsFalse(LabelTileCoverage.IsCulled(0.0001, -1.0), "negative threshold → cull disabled");
        }
    }
}
