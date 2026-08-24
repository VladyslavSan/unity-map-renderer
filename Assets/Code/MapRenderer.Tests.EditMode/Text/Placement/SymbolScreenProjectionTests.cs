// Engine-free: shared verbatim between the Unity EditMode runner and Tools/core-tests (registered in
// core-tests.csproj + Tools/core-tests/Shim/Types/float4x4.cs + the mul(float4x4,float4) shim overload).
// Top-level `using Unity.Mathematics;` + unqualified float2/float4/float4x4/double2/double3 (namespace-
// collision trap — see SymbolScreenProjection.cs's header comment).

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View;

namespace MapRenderer.Tests.Text.Placement
{
    /// <summary>
    /// S20 T2: <see cref="SymbolScreenProjection.TryProjectAnchor"/> golden + culling, over a hand-built
    /// view-projection matrix chosen so every value is hand-computable (no live camera needed — the
    /// EditMode-only <c>SymbolScreenProjectionUnityTests</c> cross-checks against a REAL
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
    public class SymbolScreenProjectionTests
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

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out float2 screenPx, out float depth);

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

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out float2 screenPx, out float depth);

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

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out _, out _);

            Assert.IsFalse(ok, "an anchor behind the camera (clip.w <= 0) must be culled");
        }

        [Test]
        public void TryProjectAnchor_AtCameraPlane_Culled()
        {
            var renderPos = new double3(2.0, 1.0, 0.0); // z == 0 -> clip.w == 0 (the <= boundary)
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out _, out _);

            Assert.IsFalse(ok, "clip.w == 0 is the inclusive boundary of the behind-camera cull");
        }

        // ── Outside-viewport cull: in front of the camera (w > 0) but the projected pixel is nowhere near
        //    the viewport -> culled. Distinct code path from the behind-camera cull. ──
        [Test]
        public void TryProjectAnchor_FarOutsideViewport_Culled()
        {
            var renderPos = new double3(1000.0, 1.0, 4.0); // clip.w = 4 > 0 (NOT behind camera)
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out float2 screenPx, out _);

            Assert.IsFalse(ok, "a projected pixel far outside the viewport (even with a positive w) must be culled");
        }

        [Test]
        public void TryProjectAnchor_WellInsideViewport_NotCulled()
        {
            // Dead-center of the viewport -- a positive control proving the margin gate isn't just always-false.
            var renderPos = new double3(0.0, 0.0, 4.0);
            var sceneOrigin = new double3(0.0, 0.0, 0.0);

            bool ok = SymbolScreenProjection.TryProjectAnchor(
                in renderPos, in sceneOrigin, in ViewProj, in Viewport, float3x3.identity, out float2 screenPx, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(100f, screenPx.x, 1e-4f);
            Assert.AreEqual(50f, screenPx.y, 1e-4f);
        }

        // ── S2 keystone tooth: a NON-identity rebase (SceneFrame.Rebase on a globe look-at) must actually be
        //    applied, not silently dropped. Uses a real globe rebase (transpose(TangentBasisAt(lookAt))) at a
        //    NON-origin look-at, and a NON-look-at anchor -- a point AT the look-at has local = anchor -
        //    sceneOrigin = 0, so rebase * 0 = 0 with or without the fix (non-discriminating, the design table's
        //    original phrasing). This anchor's local delta is nonzero, so the rebase actually moves the pixel. ──
        [Test]
        public void TryProjectPoint_NonIdentityRebase_MatchesMeshOracle_AndDiffersFromNoRebase()
        {
            var proj = new SphericalProjection();
            var lookAt = new GeoCoordinate { Latitude = 45.0, Longitude = 30.0 };
            double3 sceneOrigin = proj.Project(lookAt);
            float3x3 basis = proj.TangentBasisAt(lookAt);
            float3x3 rebase = math.transpose(basis); // matches SceneFrame.Rebase's definition

            // NON-look-at anchor -- local = anchor - sceneOrigin != 0, so the rebase is discriminating.
            var anchorGeo = new GeoCoordinate { Latitude = 47.0, Longitude = 33.0 };
            double3 anchor = proj.Project(anchorGeo);

            var viewProj = float4x4.identity;
            var viewport = new double2(200.0, 100.0);

            // (1) Correctness vs the mesh oracle: FloatingOrigin.TileToSceneRebased is the SAME "double subtract,
            //     narrow to float, rotate" the seam must perform for the tile-placement RTC math. Reproduce its
            //     clip/screen pixel by hand and compare against the fixed TryProjectPoint's result.
            float3 oracleLocal = FloatingOrigin.TileToSceneRebased(anchor, sceneOrigin, rebase);
            float4 oracleClip = math.mul(viewProj, new float4(oracleLocal, 1f));
            float2 oracleScreen = new float2(
                (oracleClip.x / oracleClip.w * 0.5f + 0.5f) * (float)viewport.x,
                (oracleClip.y / oracleClip.w * 0.5f + 0.5f) * (float)viewport.y);

            bool ok = SymbolScreenProjection.TryProjectPoint(
                in anchor, in sceneOrigin, in viewProj, in viewport, rebase, out float2 screenPx, out _);

            Assert.IsTrue(ok, "the anchor must project in front of this identity-viewProj camera");
            Assert.AreEqual(oracleScreen.x, screenPx.x, 1e-3f, "rebased seam pixel must match the mesh-RTC oracle");
            Assert.AreEqual(oracleScreen.y, screenPx.y, 1e-3f, "rebased seam pixel must match the mesh-RTC oracle");

            // (2) Discrimination: the no-rebase (identity) result must differ -- proves the rebase is load-bearing,
            //     not a no-op that happens to cancel out.
            bool okNoRebase = SymbolScreenProjection.TryProjectPoint(
                in anchor, in sceneOrigin, in viewProj, in viewport, float3x3.identity, out float2 screenPxNoRebase, out _);
            Assert.IsTrue(okNoRebase);
            Assert.That(math.distance(screenPx, screenPxNoRebase), Is.GreaterThan(1e-3f),
                "a non-identity rebase must move the projected pixel -- dropping it would silently reproduce the no-rebase result");

            // (3) Rebase self-check (non-circular -- hand-computed targets, independent of transpose's internals):
            //     rebase maps each render basis column back to its own ENU axis.
            AssertApprox(math.mul(rebase, basis.c0), new float3(1f, 0f, 0f), "rebase * basis.c0 must recover East");
            AssertApprox(math.mul(rebase, basis.c1), new float3(0f, 1f, 0f), "rebase * basis.c1 must recover Up");
            AssertApprox(math.mul(rebase, basis.c2), new float3(0f, 0f, 1f), "rebase * basis.c2 must recover North");
        }

        private static void AssertApprox(float3 actual, float3 expected, string message)
        {
            Assert.AreEqual(expected.x, actual.x, 1e-5f, message);
            Assert.AreEqual(expected.y, actual.y, 1e-5f, message);
            Assert.AreEqual(expected.z, actual.z, 1e-5f, message);
        }
    }
}
