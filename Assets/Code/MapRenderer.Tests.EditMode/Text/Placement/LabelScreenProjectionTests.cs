// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj + Tools/core-tests/Shim/Types/float4x4.cs + the mul(float4x4,float4) shim overload).
// Top-level `using Unity.Mathematics;` + unqualified float2/float4/float4x4/double2/double3 (namespace-
// collision trap — see LabelScreenProjection.cs's header comment).

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S20 T2: <see cref="LabelScreenProjection.TryProjectAnchor"/> golden + culling, over a hand-built
    /// view-projection matrix chosen so every value is hand-computable (no live camera needed — the
    /// EditMode-only <c>LabelScreenProjectionUnityTests</c> cross-checks against a REAL
    /// <c>Camera.WorldToScreenPoint</c>/<c>projectionMatrix</c>).
    ///
    /// <para>
    /// The test matrix maps local render-space <c>(x,y,z)</c> to clip <c>(x, y, z, z)</c> — i.e.
    /// <c>clip.w = local.z</c> (a stand-in "distance from camera": positive z is in front, non-positive is
    /// behind), which is enough to exercise the perspective divide, the behind-camera cull, and the
    /// viewport-margin cull without needing a full projective camera model.
    /// </para>
    /// </summary>
    [TestFixture]
    public class LabelScreenProjectionTests
    {
        // clip = mul(ViewProj, (x,y,z,1)) = (x, y, z, z) — see the class doc.
        private static readonly float4x4 ViewProj = new float4x4(
            new float4(1f, 0f, 0f, 0f),
            new float4(0f, 1f, 0f, 0f),
            new float4(0f, 0f, 1f, 1f),
            new float4(0f, 0f, 0f, 0f));

        private static readonly double2 Viewport = new double2(200.0, 100.0);

        // ── Golden: a known front-of-camera anchor lands at a hand-computed screen pixel ──
        [Test]
        public void TryProjectAnchor_FrontOfCamera_MatchesHandComputedPixel()
        {
            var renderPos = new double3(2.0, 1.0, 4.0);
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = LabelScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, out float2 screenPx, out float depth);

            Assert.IsTrue(ok, "a point in front of the camera and inside the viewport must not be culled");
            // clip=(2,1,4,4) -> ndc=(0.5,0.25,1) -> screenPx=(0.75*200, 0.625*100) = (150, 62.5).
            Assert.AreEqual(150f, screenPx.x, 1e-4f);
            Assert.AreEqual(62.5f, screenPx.y, 1e-4f);
            Assert.AreEqual(1f, depth, 1e-4f);
        }

        // ── T2 decisive tooth: the SceneOriginRender rebase is MANDATORY. Same golden pixel is reproduced
        //    when both renderPos and sceneOriginRender are shifted by the SAME offset (only their
        //    DIFFERENCE matters) — and a naive impl that skips the rebase would land somewhere else. ──
        [Test]
        public void TryProjectAnchor_SceneOriginRebase_OnlyTheDifferenceMatters()
        {
            var sceneOrigin = new double3(50.0, 0.0, 50.0);
            var renderPos = new double3(52.0, 1.0, 54.0); // local = renderPos - sceneOrigin = (2,1,4)

            bool ok = LabelScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, out float2 screenPx, out float depth);

            Assert.IsTrue(ok);
            Assert.AreEqual(150f, screenPx.x, 1e-4f, "rebased local must reproduce the SAME golden pixel as the origin-relative test");
            Assert.AreEqual(62.5f, screenPx.y, 1e-4f);

            // Tooth: a naive impl that used renderPos directly (skipping the rebase) would compute
            // clip=(52,1,54,54) -> ndc=(52/54, 1/54, 1) -> a very different pixel. Confirm the golden
            // pixel is NOT that wrong value (guards against an impl that silently drops the subtraction).
            float naiveNdcX = 52f / 54f;
            float naivePixelX = (naiveNdcX * 0.5f + 0.5f) * 200f;
            Assert.That(screenPx.x, Is.Not.EqualTo(naivePixelX).Within(0.5f),
                "skipping the SceneOriginRender rebase must NOT reproduce this golden -- the rebase is load-bearing");
        }

        // ── Behind-camera cull: clip.w <= 0 -> culled, regardless of viewport position ──
        [Test]
        public void TryProjectAnchor_BehindCamera_Culled()
        {
            var renderPos = new double3(2.0, 1.0, -4.0); // z <= 0 -> clip.w <= 0 in this test matrix
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = LabelScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, out _, out _);

            Assert.IsFalse(ok, "an anchor behind the camera (clip.w <= 0) must be culled");
        }

        [Test]
        public void TryProjectAnchor_AtCameraPlane_Culled()
        {
            var renderPos = new double3(2.0, 1.0, 0.0); // z == 0 -> clip.w == 0 (the <= boundary)
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = LabelScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, out _, out _);

            Assert.IsFalse(ok, "clip.w == 0 is the inclusive boundary of the behind-camera cull");
        }

        // ── Outside-viewport cull: in front of the camera (w > 0) but the projected pixel is nowhere near
        //    the viewport -> culled. Distinct code path from the behind-camera cull. ──
        [Test]
        public void TryProjectAnchor_FarOutsideViewport_Culled()
        {
            var renderPos = new double3(1000.0, 1.0, 4.0); // clip.w = 4 > 0 (NOT behind camera)
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = LabelScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, out float2 screenPx, out _);

            Assert.IsFalse(ok, "a projected pixel far outside the viewport (even with a positive w) must be culled");
        }

        [Test]
        public void TryProjectAnchor_WellInsideViewport_NotCulled()
        {
            // Dead-center of the viewport -- a positive control proving the margin gate isn't just always-false.
            var renderPos = new double3(0.0, 0.0, 4.0);
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = LabelScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, out float2 screenPx, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(100f, screenPx.x, 1e-4f);
            Assert.AreEqual(50f, screenPx.y, 1e-4f);
        }
    }
}
