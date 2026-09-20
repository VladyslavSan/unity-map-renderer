// Unity EditMode only — operates on SnapshotRenderer.RawPixels (RGBA32, bottom-up origin).
// NOT registered in Tools/core-tests/core-tests.csproj.

using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Alpha-weighted coverage recovery from a rendered RGBA32 buffer — the measurement
    /// <see cref="LineAaSnapshotTests"/> established, in the test assembly so a second fixture can measure
    /// the same way rather than hand-rolling a band finder that the AA ramp would dominate.
    ///
    /// <para>Coverage at a pixel is the composite's position on the background→plateau colour axis, in
    /// LINEAR space. The project renders in linear colour, so the 8-bit snapshot bytes are sRGB-ENCODED
    /// composites: skipping the decode bends the alpha ramp and corrupts every integral. Σ coverage across a
    /// perpendicular cut is the band's APPARENT WIDTH in pixels — never a thresholded "non-background pixel"
    /// count, which is what made earlier AA work flake.</para>
    ///
    /// <para>Rows are bottom-up (<see cref="SnapshotRenderer.RawPixels"/>'s native convention). Every
    /// quantity here is a magnitude or a count, so it is flip-invariant — a caller asserting a vertical
    /// DIRECTION must un-mirror first.</para>
    ///
    /// <para><see cref="LineAaSnapshotTests"/> still carries its own private copies; de-duplicating that
    /// fixture onto this helper is a deliberate follow-up, kept out of the DPR stage so no AA tooth is
    /// touched by a change that cannot affect it.</para>
    /// </summary>
    internal static class PixelCoverage
    {
        private static readonly bool LinearColorSpace =
            QualitySettings.activeColorSpace == ColorSpace.Linear;

        /// <summary>sRGB byte → linear float (identity when the project renders in gamma space).</summary>
        public static float ToLinear(byte encoded)
        {
            float s = encoded / 255f;
            if (!LinearColorSpace) return s;
            return s <= 0.04045f ? s / 12.92f : math.pow((s + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>Linear-space RGB at a pixel; out-of-range coordinates clamp to the border.</summary>
        public static float3 SampleLinear(byte[] pixels, int width, int height, int column, int row)
        {
            column = math.clamp(column, 0, width  - 1);
            row    = math.clamp(row,    0, height - 1);
            int i  = (row * width + column) * 4;
            return new float3(ToLinear(pixels[i]), ToLinear(pixels[i + 1]), ToLinear(pixels[i + 2]));
        }

        /// <summary>Mean linear colour over a (2·radius+1)² box — reads a plateau or the background without
        /// picking up single-pixel noise.</summary>
        public static float3 SampleLinearBox(byte[] pixels, int width, int height, int column, int row, int radius)
        {
            float3 sum = float3.zero;
            int    n   = 0;
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                sum += SampleLinear(pixels, width, height, column + dx, row + dy);
                n++;
            }
            return sum / n;
        }

        /// <summary>The frame's background, read from a corner no centred fixture reaches.</summary>
        public static float3 BackgroundLinear(byte[] pixels, int width, int height)
            => SampleLinearBox(pixels, width, height, 8, 8, 2);

        /// <summary>The background→plateau axis projection shared by every coverage reader — factored out so
        /// <see cref="CoverageAt"/> (integer lattice) and <see cref="CoverageProfileAlongRay"/> (general
        /// direction, bilinear) cannot drift apart on what "coverage" means.</summary>
        private static float ProjectCoverage(float3 sample, float3 background, float3 plateau)
        {
            float3 axis  = plateau - background;
            float  denom = math.dot(axis, axis);
            if (denom < 1e-9f) return 0f;
            return math.saturate(math.dot(sample - background, axis) / denom);
        }

        /// <summary>Alpha-weighted coverage at one pixel: the composite's position on the
        /// background→plateau axis. Exactly the rendered alpha when <paramref name="plateau"/> is a
        /// fully-covered pixel of the same material.</summary>
        public static float CoverageAt(
            byte[] pixels, int width, int height, int column, int row, float3 background, float3 plateau)
            => ProjectCoverage(SampleLinear(pixels, width, height, column, row), background, plateau);

        /// <summary>The most saturated sample on a column cut — the band's fully-covered interior.</summary>
        public static float3 PlateauOnColumn(
            byte[] pixels, int width, int height, int column, int rowFrom, int rowTo, float3 background)
        {
            float3 best     = background;
            float  bestDist = 0f;
            for (int row = rowFrom; row <= rowTo; row++)
            {
                float3 c    = SampleLinear(pixels, width, height, column, row);
                float  dist = math.distancesq(c, background);
                if (dist > bestDist) { bestDist = dist; best = c; }
            }
            return best;
        }

        /// <summary>Per-row coverage along a perpendicular cut, background-normalised.</summary>
        public static float[] CoverageProfileOnColumn(
            byte[] pixels, int width, int height, int column, int rowFrom, int rowTo,
            float3 background, float3 plateau)
        {
            var profile = new float[rowTo - rowFrom + 1];
            for (int row = rowFrom; row <= rowTo; row++)
                profile[row - rowFrom] = CoverageAt(pixels, width, height, column, row, background, plateau);
            return profile;
        }

        /// <summary>Σ coverage over a cut — the band's APPARENT WIDTH in pixels.</summary>
        public static float CoverageIntegral(float[] profile)
        {
            float sum = 0f;
            foreach (float c in profile) sum += c;
            return sum;
        }

        /// <summary>Fail with a readable profile when a coverage assertion trips.</summary>
        public static string FormatProfile(float[] profile, int rowFrom)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < profile.Length; i++)
                sb.Append($"[{rowFrom + i}]={profile[i]:F3} ");
            return sb.ToString();
        }

        // ── General-direction extensions (Stage T) — add-only, no existing member's behaviour changes ────
        //
        // Everything above this line is column-cut (axis-aligned) and untouched: DevicePixelRatioSnapshotTests
        // and LineProbeSymmetrySnapshotTests depend on its exact behaviour. Below is a general-direction cut
        // for fixtures under tilt, where the silhouette a tooth wants to measure is not vertical on screen.

        /// <summary>Bilinear sample in SCREEN coordinates (not a pixel index). Pixel index j's centre is at
        /// screen coordinate j + 0.5 (<see cref="GroundRowSolver"/> is the authority on this half-pixel, and
        /// on there being no flip — rows are bottom-up and Unity screen-y grows up too), so the fractional
        /// pixel index is <paramref name="screenPoint"/> − 0.5. Border behaviour matches <see cref="SampleLinear"/>
        /// exactly, because each of the 4 taps IS a <see cref="SampleLinear"/> call. Existing integer-index
        /// callers are unaffected — this is a new entry point for cuts that are not axis-aligned.</summary>
        public static float3 SampleLinearBilinear(byte[] pixels, int width, int height, double2 screenPoint)
        {
            double2 idx = screenPoint - 0.5;
            int    x0 = (int)math.floor(idx.x);
            int    y0 = (int)math.floor(idx.y);
            double fx = idx.x - x0;
            double fy = idx.y - y0;

            float3 c00 = SampleLinear(pixels, width, height, x0,     y0);
            float3 c10 = SampleLinear(pixels, width, height, x0 + 1, y0);
            float3 c01 = SampleLinear(pixels, width, height, x0,     y0 + 1);
            float3 c11 = SampleLinear(pixels, width, height, x0 + 1, y0 + 1);

            float3 cx0 = math.lerp(c00, c10, (float)fx);
            float3 cx1 = math.lerp(c01, c11, (float)fx);
            return math.lerp(cx0, cx1, (float)fy);
        }

        /// <summary>Coverage samples <c>i = 0..steps-1</c> marching from <paramref name="originScreen"/> along
        /// <paramref name="unitDirScreen"/> in steps of <paramref name="stepPx"/> screen px, each read through
        /// <see cref="SampleLinearBilinear"/> and projected onto the background→plateau axis by the SAME
        /// <see cref="ProjectCoverage"/> helper <see cref="CoverageAt"/> uses — so a column cut and a ray cut
        /// cannot disagree on what "coverage" means.</summary>
        public static float[] CoverageProfileAlongRay(
            byte[] pixels, int width, int height, double2 originScreen, double2 unitDirScreen,
            int steps, double stepPx, float3 background, float3 plateau)
        {
            var profile = new float[steps];
            for (int i = 0; i < steps; i++)
            {
                double2 p = originScreen + unitDirScreen * (i * stepPx);
                profile[i] = ProjectCoverage(SampleLinearBilinear(pixels, width, height, p), background, plateau);
            }
            return profile;
        }

        /// <summary>The most-saturated sample along the same ray <see cref="CoverageProfileAlongRay"/> marches
        /// — the ray analogue of <see cref="PlateauOnColumn"/>.</summary>
        public static float3 PlateauAlongRay(
            byte[] pixels, int width, int height, double2 originScreen, double2 unitDirScreen,
            int steps, double stepPx, float3 background)
        {
            float3 best     = background;
            float  bestDist = 0f;
            for (int i = 0; i < steps; i++)
            {
                double2 p      = originScreen + unitDirScreen * (i * stepPx);
                float3  sample = SampleLinearBilinear(pixels, width, height, p);
                float   dist   = math.distancesq(sample, background);
                if (dist > bestDist) { bestDist = dist; best = sample; }
            }
            return best;
        }

        /// <summary>Distance from the ray origin (<c>i = 0</c>) to the first <c>≥0.5 → &lt;0.5</c> crossing in
        /// <paramref name="profile"/>, linearly interpolated between the bracketing samples and scaled by
        /// <paramref name="stepPx"/>. Generalises <c>LineAaSnapshotTests.BisectorReachPx</c> to an arbitrary
        /// screen direction.
        ///
        /// <para>ACCURACY, stated honestly: this is a SILHOUETTE-REACH estimator, good to a small fraction of a
        /// pixel at a hard-ish edge — it is NOT the sub-0.1 px estimator
        /// (<c>LineProbeSymmetrySnapshotTests</c>'s half-sum rejects the 0.5-crossing for that régime, and that
        /// rejection stands). Use the coverage INTEGRAL where apparent WIDTH is wanted; use this where
        /// silhouette REACH is wanted.</para>
        ///
        /// <para>Fails loudly when no crossing occurs within <paramref name="profile"/>'s length — a silent 0
        /// would read as "the feature vanished" and could pass a <c>&lt;</c> assertion for the wrong
        /// reason.</para></summary>
        public static double HalfCrossingDistancePx(float[] profile, double stepPx)
        {
            for (int i = 1; i < profile.Length; i++)
            {
                if (profile[i - 1] >= 0.5f && profile[i] < 0.5f)
                {
                    float t = (profile[i - 1] - 0.5f) / math.max(profile[i - 1] - profile[i], 1e-6f);
                    return (i - 1 + t) * stepPx;
                }
            }
            Assert.Fail(
                $"HalfCrossingDistancePx: no ≥0.5→<0.5 crossing found within {profile.Length} steps of " +
                $"{stepPx:F3} px — either the feature does not reach this far (a real result the caller must " +
                "see, not a silent 0) or the profile never entered coverage at all.");
            return double.NaN; // unreachable: Assert.Fail throws.
        }
    }
}
