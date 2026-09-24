// Unity EditMode only — operates on SnapshotRenderer.Pixels (bottom-up origin).
// NOT registered in Tools/core-tests/core-tests.csproj.

using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Alpha-weighted coverage recovery from a rendered <see cref="Frame"/>, shared so fixtures do not
    /// hand-roll a band finder that the AA ramp would dominate. Non-obvious why: coverage is the position on
    /// the background→plateau axis in LINEAR space, because the snapshot bytes are sRGB-encoded and skipping
    /// the decode bends the ramp. Σ coverage across a cut is the band's apparent width, never a thresholded
    /// pixel count. Rows are bottom-up; a caller asserting a vertical DIRECTION must un-mirror first.
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
        public static float3 SampleLinear(Frame frame, int column, int row)
        {
            column = math.clamp(column, 0, frame.Width  - 1);
            row    = math.clamp(row,    0, frame.Height - 1);
            Color32 px = frame[column, row];
            return new float3(ToLinear(px.r), ToLinear(px.g), ToLinear(px.b));
        }

        /// <summary>Mean linear colour over a (2·radius+1)² box — reads a plateau or the background without
        /// picking up single-pixel noise.</summary>
        public static float3 SampleLinearBox(Frame frame, int column, int row, int radius)
        {
            float3 sum = float3.zero;
            int    n   = 0;
            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                sum += SampleLinear(frame, column + dx, row + dy);
                n++;
            }
            return sum / n;
        }

        /// <summary>The frame's background, read from a corner no centred fixture reaches.</summary>
        public static float3 BackgroundLinear(Frame frame)
            => SampleLinearBox(frame, 8, 8, 2);

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
            Frame frame, int column, int row, float3 background, float3 plateau)
            => ProjectCoverage(SampleLinear(frame, column, row), background, plateau);

        /// <summary>The most saturated sample on a column cut — the band's fully-covered interior.</summary>
        public static float3 PlateauOnColumn(
            Frame frame, int column, int rowFrom, int rowTo, float3 background)
        {
            float3 best     = background;
            float  bestDist = 0f;
            for (int row = rowFrom; row <= rowTo; row++)
            {
                float3 c    = SampleLinear(frame, column, row);
                float  dist = math.distancesq(c, background);
                if (dist > bestDist) { bestDist = dist; best = c; }
            }
            return best;
        }

        /// <summary>Per-row coverage along a perpendicular cut, background-normalised.</summary>
        public static float[] CoverageProfileOnColumn(
            Frame frame, int column, int rowFrom, int rowTo,
            float3 background, float3 plateau)
        {
            var profile = new float[rowTo - rowFrom + 1];
            for (int row = rowFrom; row <= rowTo; row++)
                profile[row - rowFrom] = CoverageAt(frame, column, row, background, plateau);
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

        // ── General-direction extensions ─────────────────────────────────────────────────────────────────
        //
        // Cuts for tilted fixtures, where a silhouette is not vertical. The column cuts above stay unchanged.

        /// <summary>Bilinear sample in SCREEN coordinates, not a pixel index. Pixel j's centre is at screen
        /// j + 0.5 with no flip (<see cref="GroundRowSolver"/> is the authority), so the fractional index is
        /// <paramref name="screenPoint"/> − 0.5. Each of the 4 taps is a <see cref="SampleLinear"/> call, so
        /// border behaviour matches it.</summary>
        public static float3 SampleLinearBilinear(Frame frame, double2 screenPoint)
        {
            double2 idx = screenPoint - 0.5;
            int    x0 = (int)math.floor(idx.x);
            int    y0 = (int)math.floor(idx.y);
            double fx = idx.x - x0;
            double fy = idx.y - y0;

            float3 c00 = SampleLinear(frame, x0,     y0);
            float3 c10 = SampleLinear(frame, x0 + 1, y0);
            float3 c01 = SampleLinear(frame, x0,     y0 + 1);
            float3 c11 = SampleLinear(frame, x0 + 1, y0 + 1);

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
            Frame frame, double2 originScreen, double2 unitDirScreen,
            int steps, double stepPx, float3 background, float3 plateau)
        {
            var profile = new float[steps];
            for (int i = 0; i < steps; i++)
            {
                double2 p = originScreen + unitDirScreen * (i * stepPx);
                profile[i] = ProjectCoverage(SampleLinearBilinear(frame, p), background, plateau);
            }
            return profile;
        }

        /// <summary>The most-saturated sample along the same ray <see cref="CoverageProfileAlongRay"/> marches
        /// — the ray analogue of <see cref="PlateauOnColumn"/>.</summary>
        public static float3 PlateauAlongRay(
            Frame frame, double2 originScreen, double2 unitDirScreen,
            int steps, double stepPx, float3 background)
        {
            float3 best     = background;
            float  bestDist = 0f;
            for (int i = 0; i < steps; i++)
            {
                double2 p      = originScreen + unitDirScreen * (i * stepPx);
                float3  sample = SampleLinearBilinear(frame, p);
                float   dist   = math.distancesq(sample, background);
                if (dist > bestDist) { bestDist = dist; best = sample; }
            }
            return best;
        }

        /// <summary>Distance from the ray origin (<c>i = 0</c>) to the first <c>≥0.5 → &lt;0.5</c> crossing in
        /// <paramref name="profile"/>, linearly interpolated between the bracketing samples and scaled by
        /// <paramref name="stepPx"/>. Generalises <c>LineAaSnapshotTests.BisectorReachPx</c> to an arbitrary
        /// screen direction. Limitation: it estimates silhouette REACH to a fraction of a pixel, not sub-0.1 px;
        /// use the coverage integral for apparent WIDTH. It fails when no crossing occurs, because a silent 0
        /// could pass a <c>&lt;</c> assertion for the wrong reason.</summary>
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
