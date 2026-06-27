// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference.
//
// Tests ConstrainedAngle (S68): B2 Heading Wrap, B3 Tilt Clamp, B5 runtime Clamped, operator+.

using NUnit.Framework;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class ConstrainedAngleTests
    {
        // ── B2: Heading — Wrap to [0, 360) ───────────────────────────────────────────────────────
        //
        // Degree storage guarantees bitwise-exact integer-degree wraps (370 % 360 == 10 exactly).

        [Test]
        public void Heading_370_Returns10_Exactly()
        {
            double d = ConstrainedAngle.Heading(370.0).Degrees;
            Assert.AreEqual(10.0, d, 0.0, "Heading(370) must be exactly 10° (bitwise, no tolerance).");
        }

        [Test]
        public void Heading_Negative10_Returns350_Exactly()
        {
            double d = ConstrainedAngle.Heading(-10.0).Degrees;
            Assert.AreEqual(350.0, d, 0.0, "Heading(-10) must be exactly 350°.");
        }

        [Test]
        public void Heading_0_Returns0_Exactly()
        {
            double d = ConstrainedAngle.Heading(0.0).Degrees;
            Assert.AreEqual(0.0, d, 0.0, "Heading(0) must be exactly 0°.");
        }

        [Test]
        public void Heading_360_Returns0_Exactly()
        {
            // The range is half-open [0, 360); 360° wraps to 0°.
            double d = ConstrainedAngle.Heading(360.0).Degrees;
            Assert.AreEqual(0.0, d, 0.0, "Heading(360) must wrap to exactly 0° (half-open [0,360)).");
        }

        // ── B3: Tilt — Clamp to [0, 90] ──────────────────────────────────────────────────────────
        //
        // This is the NEW CameraProperties.Tilt invariant that S68 introduces. The existing test
        // suite uses tilt ≤ 60 and never exercised the clamp; these tests pin it explicitly.

        [Test]
        public void Tilt_Above90_ClampsTo90()
        {
            double d = ConstrainedAngle.Tilt(95.0).Degrees;
            Assert.AreEqual(90.0, d, 0.0, "Tilt(95) must clamp to exactly 90°.");
        }

        [Test]
        public void Tilt_Negative_ClampsTo0()
        {
            double d = ConstrainedAngle.Tilt(-5.0).Degrees;
            Assert.AreEqual(0.0, d, 0.0, "Tilt(-5) must clamp to exactly 0°.");
        }

        [Test]
        public void Tilt_InRange_IsPreserved()
        {
            double d = ConstrainedAngle.Tilt(45.0).Degrees;
            Assert.AreEqual(45.0, d, 1e-9, "Tilt(45) must be preserved within [0,90].");
        }

        [Test]
        public void Tilt_ExactlyAtUpperBound_Is90()
        {
            double d = ConstrainedAngle.Tilt(90.0).Degrees;
            Assert.AreEqual(90.0, d, 0.0, "Tilt(90) must be exactly 90° (inclusive upper bound).");
        }

        // ── B5: Clamped — runtime range ≠ the [0,90] Tilt preset ─────────────────────────────────
        //
        // ViewInput.ApplyTilt's maxPitch limit is a runtime value, distinct from the [0,90] Tilt
        // type invariant. If both were collapsed into the Tilt preset, Clamped(75,0,60) would return
        // 75° (accepted by the [0,90] preset) — but with the correct separate Clamped path it returns 60°.

        [Test]
        public void Clamped_AboveHi_ClampsToHi()
        {
            double d = ConstrainedAngle.Clamped(75.0, 0.0, 60.0).Degrees;
            Assert.AreEqual(60.0, d, 0.0,
                "Clamped(75, 0, 60) must clamp to exactly 60°, proving runtime maxPitch≠[0,90] preset.");
        }

        [Test]
        public void Clamped_BelowLo_ClampsToLo()
        {
            double d = ConstrainedAngle.Clamped(-5.0, 0.0, 60.0).Degrees;
            Assert.AreEqual(0.0, d, 0.0, "Clamped(-5, 0, 60) must clamp to exactly 0°.");
        }

        [Test]
        public void Clamped_InRange_IsPreserved()
        {
            double d = ConstrainedAngle.Clamped(30.0, 0.0, 60.0).Degrees;
            Assert.AreEqual(30.0, d, 1e-9, "Clamped(30, 0, 60) must be preserved within [0,60].");
        }

        // ── operator + (ConstrainedAngle + Angle) ────────────────────────────────────────────────

        [Test]
        public void Operator_Plus_Heading_ReappliesWrap()
        {
            // Heading(350) + 20° = 370° → wraps to exactly 10°.
            ConstrainedAngle result = ConstrainedAngle.Heading(350.0) + Angle.FromDegrees(20.0);
            Assert.AreEqual(10.0, result.Degrees, 0.0,
                "(Heading(350) + 20°) must re-apply Wrap → exactly 10°.");
        }

        [Test]
        public void Operator_Plus_Clamped_ReappliesClamp()
        {
            // Clamped(50, 0, 60) + 20° = 70° → clamped to exactly 60°.
            ConstrainedAngle result = ConstrainedAngle.Clamped(50.0, 0.0, 60.0) + Angle.FromDegrees(20.0);
            Assert.AreEqual(60.0, result.Degrees, 0.0,
                "(Clamped(50,0,60) + 20°) must re-apply Clamp → exactly 60°.");
        }

        // ── default(ConstrainedAngle) ─────────────────────────────────────────────────────────────
        //
        // Documents the degenerate-default behavior: reads return 0°, matching default(double).
        // Never rely on a defaulted value re-clamping anything; always construct through a preset.

        [Test]
        public void Default_Degrees_IsZero()
        {
            ConstrainedAngle d = default;
            Assert.AreEqual(0.0, d.Degrees, 0.0,
                "default(ConstrainedAngle).Degrees must be 0 (matches default(double)).");
        }

        // ── WithDegrees re-applies same constraint ────────────────────────────────────────────────

        [Test]
        public void WithDegrees_Heading_ReappliesWrap()
        {
            ConstrainedAngle h   = ConstrainedAngle.Heading(45.0);
            ConstrainedAngle h2  = h.WithDegrees(720.0);
            Assert.AreEqual(0.0, h2.Degrees, 0.0,
                "Heading.WithDegrees(720) must wrap to 0°.");
        }

        [Test]
        public void WithDegrees_Tilt_ReappliesClamp()
        {
            ConstrainedAngle t  = ConstrainedAngle.Tilt(45.0);
            ConstrainedAngle t2 = t.WithDegrees(-10.0);
            Assert.AreEqual(0.0, t2.Degrees, 0.0,
                "Tilt.WithDegrees(-10) must clamp to 0°.");
        }
    }
}
