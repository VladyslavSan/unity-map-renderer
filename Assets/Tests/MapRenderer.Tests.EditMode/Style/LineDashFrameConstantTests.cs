// Unity EditMode only — reads a shader GLOBAL back with Shader.GetGlobalFloat, which has no engine-free
// equivalent, and builds a real UnityEngine.Camera. NOT registered in Tools/core-tests/core-tests.csproj.
//
// The VALUE the frame constant is pushed with, and where it comes from.
//
// _MapFrameMetersPerDevicePixel is the ruler the line shader converts EVERY px-valued width property with
// (width, gap, line-offset) plus the dash parameterisation. Two things can be wrong with it and this file
// pins both:
//
//   BASIS. The shader's dash divisor is _Width (device px) × this global, so the global must be
//   metres per DEVICE pixel. CameraPoseMath.MetersPerPixel(zoom) is metres per LOGICAL pixel and owes a
//   ÷ dpr. Getting that wrong is off by exactly dpr, i.e. the IDENTITY at dpr 1, which is the only ratio
//   the rest of the suite runs at.
//
//   SOURCE. The push lives in MapCamera.SyncToCamera and is MEASURED off the camera. Derived
//   instead from a Web-Mercator zoom formula it only HAPPENS to equal the camera's own scale at
//   AltitudeMultiplier 1, and any render path that never called ApplyZoom reads whatever an earlier
//   fixture left in this PROCESS global — which is how a z8 fixture rendered against a stale z5
//   ruler, a clean factor of 8.
//
// A Core-only round trip is VACUOUS for the basis half, because the dash period in world metres is
// (w_logical·dpr) × (mpp_logical/dpr) × Σ and the dpr cancels — only a tooth that reads the two halves from
// the two files that own them can see a basis error. It is NOT vacuous for the source half: T2 below is a
// pure CPU tooth and is the only row in the suite that can tell the two derivations apart.

#if UNITY_EDITOR
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;
using MapRenderer.Unity.Rendering.Map;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Style
{
    [TestFixture]
    public class LineDashFrameConstantTests
    {
        /// <summary>Tolerance in metres per device px. The pushed value is a float32 round trip of ~305.75
        /// (ulp ≈ 3e-5), so this is ~300 ulp of slack — and four orders of magnitude tighter than the 2×
        /// (152.87 vs 305.75) it exists to reject.</summary>
        private const double Tolerance = 1e-2;

        /// <summary>The fixture's framebuffer. 512 so the numbers below are the same ones every rendered
        /// line fixture in the suite works at.</summary>
        private const int Size = 512;

        private const double LookAtLat = 30.0;
        private const double LookAtLon = 30.0;

        private static int GlobalId => ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel;

        /// <summary>Reset to 0, NOT to a plausible value. There is no "unset" for a shader global, so 0 is
        /// the honest reset: it restores the fail-safe state, which the next fixture's first camera sync
        /// overwrites anyway. Restoring 1.0 would make a later missing-push bug invisible.</summary>
        [TearDown]
        public void ClearFrameGlobal() => Shader.SetGlobalFloat(GlobalId, 0f);

        /// <summary>Builds a real <see cref="MapCamera"/> on a fixed <see cref="Size"/>×<see cref="Size"/>
        /// RenderTexture, whose ctor syncs — and therefore pushes — immediately. Returns the camera and the
        /// value that landed in the global; the caller disposes via the returned GameObject.</summary>
        private static (GameObject go, RenderTexture rt, MapCamera cam, double pushed) PushAndRead(
            double zoom, double devicePixelRatio, float altitudeMultiplier = 1f, double tiltDeg = 0.0)
        {
            Shader.SetGlobalFloat(GlobalId, 0f); // so a missing push reads as 0 rather than the last row's value

            var go   = new GameObject("FrameConst_TestCamera");
            var uCam = go.AddComponent<Camera>();
            var rt   = new RenderTexture(Size, Size, 0);
            uCam.targetTexture = rt;
            uCam.enabled       = false;

            var props = new CameraProperties(
                new GeoCoordinate3D { Latitude = LookAtLat, Longitude = LookAtLon, Altitude = 0.0 },
                zoom: zoom, heading: 0.0, tilt: tiltDeg);
            var cam = new MapCamera(uCam, props, altitudeMultiplier, null, devicePixelRatio);

            Assert.That(cam.ViewportPx.y, Is.EqualTo((double)Size).Within(1e-9),
                $"precondition: the physical viewport height must be {Size} — the frame constant divides by " +
                "it, so a different framebuffer silently rescales every expected value below.");

            return (go, rt, cam, Shader.GetGlobalFloat(GlobalId));
        }

        private static void Destroy(GameObject go, RenderTexture rt)
        {
            if (go != null) Object.DestroyImmediate(go);
            if (rt != null) { rt.Release(); Object.DestroyImmediate(rt); }
        }

        /// <summary>The camera's own scale, recomputed here from the pose it committed rather than read off
        /// <see cref="MapCamera.MetresPerDevicePixel"/> — otherwise the assertion would be the production
        /// expression compared against itself.</summary>
        private static double CameraMeasured(MapCamera cam)
            => 2.0 * math.length(cam.CameraRelativePosition)
                   * math.tan(math.radians(cam.CurrentProperties.VerticalFovDeg) * 0.5) / cam.ViewportPx.y;

        /// <summary>
        /// <b>T1.</b> The pushed frame constant is metres per DEVICE pixel AND is the camera's own measured
        /// scale at the look-at — two independent readings of one number, asserted together.
        ///
        /// <para>The device-px clause: expected values are computed from
        /// <see cref="CameraPoseMath.MetersPerPixel"/> rather than written as literals, so the tooth pins the
        /// RELATION (the ÷ dpr) and not a transcription of the zoom curve. The 1.5 row is there because a
        /// dyadic ratio can hide reciprocal-vs-divide drift; the 0.0 and 100.0 rows are there because the ÷
        /// must go through <see cref="DeviceScaling.PerLogicalPxToPerDevicePx"/>'s plausibility band — a raw
        /// <c>/ dpr</c> reads +∞ and 3.06 on those two. Their expected values are unaffected by the move to
        /// the camera, and not by luck: the same fallback is inherited through
        /// <see cref="MapCamera.ViewportLogicalPx"/> → <see cref="DeviceScaling.DeviceToLogicalPx"/>, which is
        /// the only place the ratio enters the altitude framing.</para>
        ///
        /// <para>The camera clause is the tooth that would have caught the 8×: a fixture
        /// rendering at z8 read 2445.985 = <c>MetersPerPixel(5.0)</c> out of this global — three whole zoom
        /// levels of stale process state — while its own camera measured 305.748113. Nothing in the suite
        /// compared the two.</para>
        /// </summary>
        [TestCase(8.0,  1.0,   1.0, TestName = "T1_MetresPerDevicePx_Zoom8_Dpr1")]
        [TestCase(8.0,  2.0,   2.0, TestName = "T1_MetresPerDevicePx_Zoom8_Dpr2")]
        [TestCase(8.0,  1.5,   1.5, TestName = "T1_MetresPerDevicePx_Zoom8_Dpr1p5")]
        [TestCase(8.0,  0.0,   1.0, TestName = "T1_MetresPerDevicePx_Zoom8_Dpr0_FallsBackToOne")]
        [TestCase(8.0,  100.0, 1.0, TestName = "T1_MetresPerDevicePx_Zoom8_Dpr100_FallsBackToOne")]
        [TestCase(14.0, 1.0,   1.0, TestName = "T1_MetresPerDevicePx_Zoom14_Dpr1")]
        [TestCase(14.0, 2.0,   2.0, TestName = "T1_MetresPerDevicePx_Zoom14_Dpr2")]
        public void SyncToCamera_PushesTheCameraMeasuredMetresPerDevicePixel(
            double zoom, double dpr, double effectiveRatio)
        {
            double logical  = CameraPoseMath.MetersPerPixel(zoom);
            double expected = logical / effectiveRatio;

            var (go, rt, cam, pushed) = PushAndRead(zoom, dpr);
            try
            {
                double measured = CameraMeasured(cam);

                Assert.That(pushed, Is.EqualTo(expected).Within(Tolerance),
                    $"zoom {zoom}, dpr {dpr}: the frame constant must be {expected:F6} m per DEVICE px " +
                    $"(MetersPerPixel({zoom}) = {logical:F6} m per LOGICAL px, ÷ the effective ratio " +
                    $"{effectiveRatio}). Read {pushed:F6}. Reading {logical:F6} means the ÷ dpr is missing — " +
                    "the shader's _Width already arrives in device px, so the two halves would be a " +
                    "factor of dpr apart and every px width would be wrong by exactly that on a dense panel. " +
                    "Reading 0 means SyncToCamera never pushed it at all, which is a blank frame for every " +
                    "pixel-width layer.");

                Assert.That(pushed, Is.EqualTo(measured).Within(Tolerance),
                    $"zoom {zoom}, dpr {dpr}: the pushed constant {pushed:F6} must be the camera's OWN scale " +
                    $"at the look-at, 2·|camPos|·tan(fov/2)/viewportPx.y = {measured:F6}. A disagreement " +
                    "means the ruler the shader sizes lines with was derived from something other than the " +
                    "camera doing the rendering — the defect, which presented as a clean factor of 8 " +
                    "(three zoom levels of stale process global) in DevicePixelRatioSnapshotTests.");
            }
            finally { Destroy(go, rt); }
        }

        /// <summary>
        /// <b>T2 — the discriminating row.</b> The constant tracks the CAMERA, not the zoom formula.
        ///
        /// <para><see cref="MapCamera.AltitudeMultiplier"/> is an art-direction scale on the orbit radius:
        /// at 1.5 the camera sits 1.5× further out, so a device pixel covers 1.5× as much ground and the
        /// ruler must read 1.5× larger. The retired push read <c>MetersPerPixel(zoom)/dpr</c>, which does not
        /// contain the multiplier at all — so it stays at 305.748 while the camera is at 458.622, and every
        /// px-width line renders 1.5× too thin with no test in the suite able to see it. This is the ONE row
        /// that separates the two derivations; every row of T1 is green under both.</para>
        /// </summary>
        [Test]
        public void SyncToCamera_TracksTheAltitudeMultiplier_NotTheZoomFormula()
        {
            const float Multiplier = 1.5f;
            double zoomFormula = CameraPoseMath.MetersPerPixel(8.0);        // 305.748113
            double expected    = zoomFormula * Multiplier;                  // 458.622170

            var (go, rt, cam, pushed) = PushAndRead(8.0, 1.0, altitudeMultiplier: Multiplier);
            try
            {
                Assert.That(pushed, Is.EqualTo(CameraMeasured(cam)).Within(Tolerance),
                    $"the pushed constant {pushed:F6} must equal the camera's measured scale " +
                    $"{CameraMeasured(cam):F6} at every altitude multiplier.");

                Assert.That(pushed, Is.EqualTo(expected).Within(Tolerance),
                    $"at AltitudeMultiplier {Multiplier} the camera orbits {Multiplier}× further out, so a " +
                    $"device pixel covers {Multiplier}× the ground and the ruler must read {expected:F6} m. " +
                    $"Read {pushed:F6}.");

                Assert.That(pushed, Is.Not.EqualTo(zoomFormula).Within(Tolerance),
                    $"the pushed constant is {zoomFormula:F6} — MetersPerPixel(8)/dpr, the retired " +
                    "Web-Mercator zoom formula, which has no AltitudeMultiplier term. The constant must be " +
                    "MEASURED off the camera, not re-derived from the zoom the camera was framed with.");
            }
            finally { Destroy(go, rt); }
        }

        /// <summary>The rejected basis, stated as its own assertion so a failure names the defect rather
        /// than a number: at dpr 2 the logical-basis reading is exactly twice the correct one.</summary>
        [Test]
        public void SyncToCamera_AtDpr2_IsNotTheLogicalBasis()
        {
            double logical = CameraPoseMath.MetersPerPixel(8.0);

            var (go, rt, _, pushed) = PushAndRead(8.0, 2.0);
            try
            {
                Assert.That(pushed, Is.EqualTo(0.5 * logical).Within(Tolerance),
                    $"at dpr 2 the ruler must halve to {0.5 * logical:F6}; read {pushed:F6}.");
                Assert.That(pushed, Is.Not.EqualTo(logical).Within(Tolerance),
                    $"at dpr 2 the pushed ruler is the LOGICAL-pixel value {logical:F6} — the whole DPR trap. " +
                    "It is the identity at dpr 1, so no other test in the suite can see it.");
            }
            finally { Destroy(go, rt); }
        }

        /// <summary>
        /// <b>T2b.</b> The constant does not move with TILT. The orbit radius is what
        /// <see cref="CameraPoseMath.ComputeRelativePose"/> holds fixed, so the world width a style asks for
        /// is the same overhead as at the horizon — tilt changes only where in the frame each depth lands,
        /// which is the perspective divide's business and not the ruler's. A tilt term here would be a
        /// per-frame version of exactly the compensation that was reverted.
        /// </summary>
        [TestCase(0.0,  TestName = "T2b_FrameConstant_Tilt0")]
        [TestCase(30.0, TestName = "T2b_FrameConstant_Tilt30")]
        [TestCase(55.0, TestName = "T2b_FrameConstant_Tilt55")]
        public void SyncToCamera_IsIndependentOfTilt(double tiltDeg)
        {
            double expected = CameraPoseMath.MetersPerPixel(8.0);

            var (go, rt, _, pushed) = PushAndRead(8.0, 1.0, tiltDeg: tiltDeg);
            try
            {
                Assert.That(pushed, Is.EqualTo(expected).Within(Tolerance),
                    $"at tilt {tiltDeg}° the frame constant reads {pushed:F6}; it must stay {expected:F6}, " +
                    "the same value as overhead. The camera orbits at a FIXED radius, so the reference depth " +
                    "does not move with tilt.");
            }
            finally { Destroy(go, rt); }
        }
    }
}
#endif
