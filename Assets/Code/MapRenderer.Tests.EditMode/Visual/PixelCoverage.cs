// Unity EditMode only — operates on SnapshotRenderer.RawPixels (RGBA32, bottom-up origin).
// NOT registered in Tools/core-tests/core-tests.csproj.

using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Tests.Visual
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

        /// <summary>Alpha-weighted coverage at one pixel: the composite's position on the
        /// background→plateau axis. Exactly the rendered alpha when <paramref name="plateau"/> is a
        /// fully-covered pixel of the same material.</summary>
        public static float CoverageAt(
            byte[] pixels, int width, int height, int column, int row, float3 background, float3 plateau)
        {
            float3 axis  = plateau - background;
            float  denom = math.dot(axis, axis);
            if (denom < 1e-9f) return 0f;
            return math.saturate(
                math.dot(SampleLinear(pixels, width, height, column, row) - background, axis) / denom);
        }

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
    }
}
