// Engine-free: compiled verbatim by both the Unity EditMode runner and the fast dotnet test project
// (Tools/core-tests). Do NOT add any UnityEngine reference.
//
// Tests Angle value type (S68): B1 deg<->rad round-trips, trig association, B4 shortest-path lerp.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Cameras
{
    [TestFixture]
    public class AngleTests
    {
        // ── B1: degrees↔radians conversion ───────────────────────────────────────────────────────

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
        public void Trig_90Degrees_SinIsOne()
        {
            double s = Angle.FromDegrees(90.0).Sin;
            Assert.AreEqual(1.0, s, 1e-12, "sin(90°) must be 1.");
        }

        [Test]
        public void Trig_0Degrees_CosIsOne()
        {
            double c = Angle.FromDegrees(0.0).Cos;
            Assert.AreEqual(1.0, c, 1e-12, "cos(0°) must be 1.");
        }

        [Test]
        public void Trig_180Degrees_SinIsZero()
        {
            double s = Angle.FromDegrees(180.0).Sin;
            Assert.AreEqual(0.0, s, 1e-12, "sin(180°) must be 0.");
        }

        [Test]
        public void Trig_60Degrees_CosMatchesMathFormula()
        {
            // cos(60°) = 0.5 exactly by definition
            double c = Angle.FromDegrees(60.0).Cos;
            Assert.AreEqual(0.5, c, 1e-12, "cos(60°) must be 0.5.");
        }

        // ── NormalizedDegrees ─────────────────────────────────────────────────────────────────────

        [Test]
        public void NormalizedDegrees_370_Returns10_Exactly()
        {
            double d = Angle.FromDegrees(370.0).NormalizedDegrees().Degrees;
            Assert.AreEqual(10.0, d, 0.0, "370° normalized must be exactly 10° (degree storage, no rounding).");
        }

        [Test]
        public void NormalizedDegrees_Negative10_Returns350_Exactly()
        {
            double d = Angle.FromDegrees(-10.0).NormalizedDegrees().Degrees;
            Assert.AreEqual(350.0, d, 0.0, "-10° normalized must be exactly 350°.");
        }

        [Test]
        public void NormalizedDegrees_360_Returns0_Exactly()
        {
            double d = Angle.FromDegrees(360.0).NormalizedDegrees().Degrees;
            Assert.AreEqual(0.0, d, 0.0, "360° normalized must be exactly 0° (half-open [0,360)).");
        }

        // ── B4: LerpShortest — shortest-path heading lerp ──────────────────────────────────────────
        //
        // The decisive acceptance test: 350°→10° must traverse +20° (NOT −340°).
        // A naive linear lerp yields (350 + (10-350)*t = 350 - 340t), landing at 180° at t=0.5
        // and at 10° at t=1 (but going the LONG way around).
        // LerpShortest must recognize the +20° short path and land near 0°/360° at t=0.5.

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
            // The naive linear midpoint between 350° and 10° going the long way would be ≈180°.
            // The correct short-path midpoint is ≈0° (as pinned above).
            // This assertion provides a direct contrast: result is NOT near 180°.
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
}
