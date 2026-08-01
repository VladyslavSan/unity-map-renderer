// Unity EditMode only — reads a shader GLOBAL back with Shader.GetGlobalFloat, which has no engine-free
// equivalent. NOT registered in Tools/core-tests/core-tests.csproj.
//
// S110 T3 — the VALUE the frame constant is pushed with, and the routing of its guard.
//
// This is the CPU half of the split: the shader's dash divisor is _Width (device px) × this global, so the
// global must be metres per DEVICE pixel — CameraPoseMath.MetersPerPixel(zoom) is metres per LOGICAL pixel
// and owes a ÷ dpr. Getting that wrong is off by exactly dpr, i.e. the IDENTITY at dpr 1, which is the only
// ratio the rest of the suite runs at.
//
// It cannot replace the rendered teeth and does not try to: a Core-only round trip is VACUOUS here, because
// the dash period in world metres is (w_logical·dpr) × (mpp_logical/dpr) × Σ and the dpr cancels. Only a
// tooth that reads the two halves from the two files that own them can see a basis error.

#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Style;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class LineDashFrameConstantTests
    {
        /// <summary>Tolerance in metres per device px. The pushed value is a float32 round trip of ~305.75
        /// (ulp ≈ 3e-5), so this is ~300 ulp of slack — and four orders of magnitude tighter than the 2×
        /// (152.87 vs 305.75) it exists to reject.</summary>
        private const double Tolerance = 1e-2;

        private static int GlobalId => ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel;

        /// <summary>Reset to 0, NOT to a plausible value. There is no "unset" for a shader global, so 0 is
        /// the honest reset: it restores the fail-safe state (divisor 0 ⇒ dashU 0 ⇒ a uniform half-coverage
        /// line, no dash edges), which the next fixture's first ApplyZoom overwrites anyway. Restoring 1.0
        /// would make a later missing-ApplyZoom bug invisible.</summary>
        [TearDown]
        public void ClearFrameGlobal() => Shader.SetGlobalFloat(GlobalId, 0f);

        private static double PushAndRead(double zoom, double devicePixelRatio)
        {
            Shader.SetGlobalFloat(GlobalId, 0f); // so a missing push reads as 0 rather than the last row's value
            using (var set = new RenderLayerSet())
                set.ApplyZoom(zoom, devicePixelRatio);
            return Shader.GetGlobalFloat(GlobalId);
        }

        /// <summary>
        /// <b>T3.</b> <see cref="RenderLayerSet.ApplyZoom"/> pushes metres per DEVICE pixel — the map's
        /// ground resolution at that zoom divided by the device-pixel ratio.
        ///
        /// <para>Expected values are computed from <see cref="CameraPoseMath.MetersPerPixel"/> rather than
        /// written as literals, so the tooth pins the RELATION (the ÷ dpr) and not a transcription of the
        /// zoom curve. The 1.5 row is there because a dyadic ratio can hide reciprocal-vs-divide drift;
        /// the 0.0 and 100.0 rows are there because the ÷ must go through
        /// <see cref="DeviceScaling.PerLogicalPxToPerDevicePx"/>'s plausibility band — a raw <c>/ dpr</c>
        /// reads +∞ and 3.06 on those two rows and NaNs every dashed layer.</para>
        /// </summary>
        [TestCase(8.0,  1.0,   1.0, TestName = "T3_MetresPerDevicePx_Zoom8_Dpr1")]
        [TestCase(8.0,  2.0,   2.0, TestName = "T3_MetresPerDevicePx_Zoom8_Dpr2")]
        [TestCase(8.0,  1.5,   1.5, TestName = "T3_MetresPerDevicePx_Zoom8_Dpr1p5")]
        [TestCase(8.0,  0.0,   1.0, TestName = "T3_MetresPerDevicePx_Zoom8_Dpr0_FallsBackToOne")]
        [TestCase(8.0,  100.0, 1.0, TestName = "T3_MetresPerDevicePx_Zoom8_Dpr100_FallsBackToOne")]
        [TestCase(14.0, 1.0,   1.0, TestName = "T3_MetresPerDevicePx_Zoom14_Dpr1")]
        [TestCase(14.0, 2.0,   2.0, TestName = "T3_MetresPerDevicePx_Zoom14_Dpr2")]
        public void ApplyZoom_PushesMetresPerDevicePixel(double zoom, double dpr, double effectiveRatio)
        {
            double logical  = CameraPoseMath.MetersPerPixel(zoom);
            double expected = logical / effectiveRatio;

            double pushed = PushAndRead(zoom, dpr);

            Assert.That(pushed, Is.EqualTo(expected).Within(Tolerance),
                $"zoom {zoom}, dpr {dpr}: the frame constant must be {expected:F6} m per DEVICE px " +
                $"(MetersPerPixel({zoom}) = {logical:F6} m per LOGICAL px, ÷ the effective ratio " +
                $"{effectiveRatio}). Read {pushed:F6}. Reading {logical:F6} means the ÷ dpr is missing — " +
                "the shader's _Width already arrived in device px (S107), so the two halves would be a " +
                "factor of dpr apart and the dash period would be wrong by exactly that on any dense panel. " +
                "Reading 0 means ApplyZoom never pushed it at all, which renders a uniform half-coverage " +
                "line rather than a dashed one.");
        }

        /// <summary>The rejected value, stated as its own assertion so a failure names the defect rather
        /// than a number: at dpr 2 the logical-basis reading is exactly twice the correct one.</summary>
        [Test]
        public void ApplyZoom_AtDpr2_IsNotTheLogicalBasis()
        {
            double logical = CameraPoseMath.MetersPerPixel(8.0);
            double pushed  = PushAndRead(8.0, 2.0);

            Assert.That(pushed, Is.EqualTo(0.5 * logical).Within(Tolerance),
                $"at dpr 2 the ruler must halve to {0.5 * logical:F6}; read {pushed:F6}.");
            Assert.That(pushed, Is.Not.EqualTo(logical).Within(Tolerance),
                $"at dpr 2 the pushed ruler is the LOGICAL-pixel value {logical:F6} — the whole DPR trap. " +
                "It is the identity at dpr 1, so no other test in the suite can see it.");
        }
    }
}
#endif
