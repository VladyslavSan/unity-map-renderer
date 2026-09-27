// Engine-free: Tools/core-tests also compiles this file, so add no UnityEngine reference.
// Tests the Angle value type: deg↔rad round-trips, trig association, shortest-path lerp.


using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Cameras
{

    [TestFixture]
    public class AngleTests
    {
        // ── Degrees↔radians conversion ───────────────────────────────────────────────────────────

        [Test]
        public void FromDegrees45_Radians_EqualsPI_Over4()
        {
            double r = Angle.FromDegrees(45.0).Radians;
            Assert.AreEqual(math.PI_DBL / 4.0, r, 1e-12,
                "FromDegrees(45).Radians must equal π/4 to 1e-12.");
        }

        [Test]
        public void FromRadiansPI_Degrees_Equals180()
        {
            double d = Angle.FromRadians(math.PI_DBL).Degrees;
            Assert.AreEqual(180.0, d, 1e-9,
                "FromRadians(π).Degrees must equal 180 to 1e-9.");
        }

        [Test]
        public void FromDegrees_RoundTrip_IsIdentity()
        {
            double[] cases = { 0.0, 37.5, 123.456, -88.0, 359.999 };
            foreach (double d in cases)
            {
                double roundTrip = Angle.FromDegrees(d).Degrees;
                Assert.AreEqual(d, roundTrip, 1e-9,
                    $"FromDegrees({d}).Degrees must round-trip to within 1e-9.");
            }
        }

        // ── Trig association ──────────────────────────────────────────────────────────────────────

        [Test]
        [TestCase(90.0, 1.0, TestName = "Trig_SinMatchesFormula(90Degrees_SinIsOne)")]
        [TestCase(180.0, 0.0, TestName = "Trig_SinMatchesFormula(180Degrees_SinIsZero)")]
        public void Trig_SinMatchesFormula(double degrees, double expected)
        {
            double s = Angle.FromDegrees(degrees).Sin;
            Assert.AreEqual(expected, s, 1e-12, $"sin({degrees}°) must be {expected}.");
        }

        [Test]
        [TestCase(0.0, 1.0, TestName = "Trig_CosMatchesFormula(0Degrees_CosIsOne)")]
        // cos(60°) = 0.5 exactly by definition.
        [TestCase(60.0, 0.5, TestName = "Trig_CosMatchesFormula(60Degrees_CosIsHalf)")]
        public void Trig_CosMatchesFormula(double degrees, double expected)
        {
            double c = Angle.FromDegrees(degrees).Cos;
            Assert.AreEqual(expected, c, 1e-12, $"cos({degrees}°) must be {expected}.");
        }

        // ── NormalizedDegrees ─────────────────────────────────────────────────────────────────────

        [Test]
        [TestCase(370.0, 10.0, TestName = "NormalizedDegrees_ReturnsExactly(370_Returns10)")]
        [TestCase(-10.0, 350.0, TestName = "NormalizedDegrees_ReturnsExactly(Negative10_Returns350)")]
        [TestCase(360.0, 0.0, TestName = "NormalizedDegrees_ReturnsExactly(360_Returns0)")] // half-open [0,360)
        public void NormalizedDegrees_ReturnsExactly(double degrees, double expected)
        {
            double d = Angle.FromDegrees(degrees).NormalizedDegrees().Degrees;
            Assert.AreEqual(expected, d, 0.0, $"{degrees}° normalized must be exactly {expected}° (degree storage, no rounding).");
        }

        // ── LerpShortest — shortest-path heading lerp ──────────────────────────────────────────────
        // 350°→10° must traverse +20°; a naive lerp goes −340° and passes 180° at t=0.5.

        [Test]
        public void LerpShortest_350To10_At_t1_ReturnsNear10()
        {
            Angle result = Angle.LerpShortest(Angle.FromDegrees(350.0), Angle.FromDegrees(10.0), 1.0);
            Assert.AreEqual(10.0, result.Degrees, 1e-9,
                "LerpShortest(350°→10°, t=1) must reach 10° (short +20° path).");
        }

        [Test]
        public void LerpShortest_350To10_At_t05_ReturnsNear0or360()
        {
            // At t=0.5 on the short +20° path: 350° + 10° = 360° ≡ 0°.
            // Accept ≈0° or ≈360° (both mean the same physical direction).
            Angle result = Angle.LerpShortest(Angle.FromDegrees(350.0), Angle.FromDegrees(10.0), 0.5);
            double d     = result.Degrees;
            // min(d, 360-d) < 1e-9 means it is within 1e-9° of either 0° or 360°.
            double distFromZero = d < 180.0 ? d : 360.0 - d;
            Assert.Less(distFromZero, 1e-9,
                $"LerpShortest(350°→10°, t=0.5) must be near 0°/360° (short path midpoint). Got {d:F6}°.");
        }

        [Test]
        public void LerpShortest_350To10_NotNaiveMidpoint()
        {
            // A naive long-way lerp puts the midpoint near 180°; the short-path midpoint is ≈0°.
            Angle result = Angle.LerpShortest(Angle.FromDegrees(350.0), Angle.FromDegrees(10.0), 0.5);
            double d     = result.Degrees;
            Assert.False(d > 170.0 && d < 190.0,
                $"LerpShortest(350°→10°, t=0.5) must NOT be near 180° (that would be the wrong −340° path). Got {d:F6}°.");
        }

        // ── ApproximatelyEquals ───────────────────────────────────────────────────────────────────

        [Test]
        public void ApproximatelyEquals_WithinTolerance_ReturnsTrue()
        {
            Angle a = Angle.FromDegrees(45.0);
            Angle b = Angle.FromDegrees(45.0 + 1e-10);
            Assert.IsTrue(a.ApproximatelyEquals(b, 1e-9),
                "Angles within tolerance must compare approximately equal.");
        }

        [Test]
        public void ApproximatelyEquals_OutsideTolerance_ReturnsFalse()
        {
            Angle a = Angle.FromDegrees(45.0);
            Angle b = Angle.FromDegrees(46.0);
            Assert.IsFalse(a.ApproximatelyEquals(b, 0.5),
                "Angles outside tolerance must not compare approximately equal.");
        }
    }

// Engine-free: Tools/core-tests also compiles this file, so add no UnityEngine reference.
// Tests ConstrainedAngle: Heading Wrap, Tilt Clamp, runtime Clamped, operator+.



    [TestFixture]
    public class ConstrainedAngleTests
    {
        // ── Heading — Wrap to [0, 360) ───────────────────────────────────────────────────────────
        //
        // Degree storage guarantees bitwise-exact integer-degree wraps (370 % 360 == 10 exactly).

        [Test]
        [TestCase(370.0, 10.0, TestName = "Heading_MatchesExpected(370_Returns10)")]
        [TestCase(-10.0, 350.0, TestName = "Heading_MatchesExpected(Negative10_Returns350)")]
        [TestCase(0.0, 0.0, TestName = "Heading_MatchesExpected(0_Returns0)")]
        // The range is half-open [0, 360); 360° wraps to 0°.
        [TestCase(360.0, 0.0, TestName = "Heading_MatchesExpected(360_Returns0)")]
        public void Heading_MatchesExpected(double degrees, double expected)
        {
            double d = ConstrainedAngle.Heading(degrees).Degrees;
            Assert.AreEqual(expected, d, 0.0, $"Heading({degrees}) must be exactly {expected}° (bitwise, no tolerance).");
        }

        // ── Tilt — Clamp to [0, 90] ──────────────────────────────────────────────────────────────
        // The CameraProperties.Tilt invariant; other tests keep tilt ≤ 60 and never reach the clamp.

        [Test]
        [TestCase(95.0, 90.0, 0.0, TestName = "Tilt_MatchesExpected(Above90_ClampsTo90)")]
        [TestCase(-5.0, 0.0, 0.0, TestName = "Tilt_MatchesExpected(Negative_ClampsTo0)")]
        [TestCase(45.0, 45.0, 1e-9, TestName = "Tilt_MatchesExpected(InRange_IsPreserved)")]
        [TestCase(90.0, 90.0, 0.0, TestName = "Tilt_MatchesExpected(ExactlyAtUpperBound_Is90)")]
        public void Tilt_MatchesExpected(double degrees, double expected, double tolerance)
        {
            double d = ConstrainedAngle.Tilt(degrees).Degrees;
            Assert.AreEqual(expected, d, tolerance, $"Tilt({degrees}) must be {expected}°.");
        }

        // ── Clamped — runtime range ≠ the [0,90] Tilt preset ─────────────────────────────────────

        [Test]
        // ViewInput.ApplyTiltDelta's runtime maxPitch is not the [0,90] Tilt preset: Clamped(75,0,60) is 60°,
        // proving the runtime range, not the Tilt preset, is what clamped this value.
        [TestCase(75.0, 60.0, 0.0, TestName = "Clamped_MatchesExpected(AboveHi_ClampsToHi)")]
        [TestCase(-5.0, 0.0, 0.0, TestName = "Clamped_MatchesExpected(BelowLo_ClampsToLo)")]
        [TestCase(30.0, 30.0, 1e-9, TestName = "Clamped_MatchesExpected(InRange_IsPreserved)")]
        public void Clamped_MatchesExpected(double value, double expected, double tolerance)
        {
            double d = ConstrainedAngle.Clamped(value, 0.0, 60.0).Degrees;
            Assert.AreEqual(expected, d, tolerance, $"Clamped({value}, 0, 60) must be {expected}°.");
        }

        // ── operator + (ConstrainedAngle + Angle) ────────────────────────────────────────────────

        private static IEnumerable<TestCaseData> OperatorPlusCases()
        {
            // Heading(350) + 20° = 370° → wraps to exactly 10°.
            yield return new TestCaseData((Func<ConstrainedAngle>)(() => ConstrainedAngle.Heading(350.0)), 20.0, 10.0)
                .SetName("OperatorPlus_ReappliesSameConstraint(Heading_WrapsPast360)");
            // Clamped(50, 0, 60) + 20° = 70° → clamped to exactly 60°.
            yield return new TestCaseData((Func<ConstrainedAngle>)(() => ConstrainedAngle.Clamped(50.0, 0.0, 60.0)), 20.0, 60.0)
                .SetName("OperatorPlus_ReappliesSameConstraint(Clamped_ClampsAtHi)");
        }

        [Test]
        [TestCaseSource(nameof(OperatorPlusCases))]
        public void OperatorPlus_ReappliesSameConstraint(Func<ConstrainedAngle> makeStart, double addDegrees, double expected)
        {
            ConstrainedAngle result = makeStart() + Angle.FromDegrees(addDegrees);
            Assert.AreEqual(expected, result.Degrees, 0.0,
                $"(start + {addDegrees}°) must re-apply the start's own constraint → exactly {expected}°.");
        }

        // ── default(ConstrainedAngle) ─────────────────────────────────────────────────────────────
        // A defaulted value reads 0° and re-clamps nothing; construct through a preset.

        [Test]
        public void Default_Degrees_IsZero()
        {
            ConstrainedAngle d = default;
            Assert.AreEqual(0.0, d.Degrees, 0.0,
                "default(ConstrainedAngle).Degrees must be 0 (matches default(double)).");
        }

        // ── WithDegrees re-applies same constraint ────────────────────────────────────────────────

        private static IEnumerable<TestCaseData> WithDegreesCases()
        {
            yield return new TestCaseData((Func<double, ConstrainedAngle>)(d => ConstrainedAngle.Heading(45.0).WithDegrees(d)), 720.0, 0.0)
                .SetName("WithDegrees_ReappliesSameConstraint(Heading_WrapsPast360)");
            yield return new TestCaseData((Func<double, ConstrainedAngle>)(d => ConstrainedAngle.Tilt(45.0).WithDegrees(d)), -10.0, 0.0)
                .SetName("WithDegrees_ReappliesSameConstraint(Tilt_ClampsBelowZero)");
        }

        [Test]
        [TestCaseSource(nameof(WithDegreesCases))]
        public void WithDegrees_ReappliesSameConstraint(Func<double, ConstrainedAngle> withDegrees, double newDegrees, double expected)
        {
            ConstrainedAngle result = withDegrees(newDegrees);
            Assert.AreEqual(expected, result.Degrees, 0.0,
                $"WithDegrees({newDegrees}) must re-apply the start's own constraint → exactly {expected}°.");
        }
    }
}
