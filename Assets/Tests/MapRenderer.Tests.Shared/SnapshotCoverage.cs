using System;
using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Result of analysing a decoded RGBA32 pixel buffer for "map-like" coverage.
    /// </summary>
    public readonly struct SnapshotVerdict
    {
        /// <summary>Fraction of pixels that differ from the background beyond tolerance.</summary>
        public readonly float FilledFraction;
        /// <summary>Fraction of pixels that match the background within tolerance.</summary>
        public readonly float BackgroundFraction;
        /// <summary>Number of grid cells with at least <c>MinFillPixelsPerBucket</c> fill pixels.</summary>
        public readonly int   DistinctRegionBucketsHit;
        /// <summary>~100% background or ~100% black: a GPU context failure or an empty render.</summary>
        public readonly bool  IsBlank;
        /// <summary>~100% one coarse colour in the full-RGB histogram: an all-filled or garbage flat frame.</summary>
        public readonly bool  IsUniform;

        public SnapshotVerdict(
            float filledFraction,
            float backgroundFraction,
            int   distinctRegionBucketsHit,
            bool  isBlank,
            bool  isUniform)
        {
            FilledFraction           = filledFraction;
            BackgroundFraction       = backgroundFraction;
            DistinctRegionBucketsHit = distinctRegionBucketsHit;
            IsBlank                  = isBlank;
            IsUniform                = isUniform;
        }

        /// <summary>
        /// Returns true when the buffer looks like a real render: not blank, not uniform, a fill fraction in
        /// [minFill, maxFill], and fill pixels in at least minBuckets grid cells. The default thresholds are
        /// wide so that they tolerate GPU, driver and Unity differences.
        /// </summary>
        public bool Passes(
            float minFill    = 0.10f,
            float maxFill    = 0.85f,
            int   minBuckets = 8)
        {
            if (IsBlank || IsUniform) return false;
            if (FilledFraction < minFill || FilledFraction > maxFill) return false;
            if (DistinctRegionBucketsHit < minBuckets) return false;
            return true;
        }
    }

    /// <summary>
    /// Analyses a decoded RGBA32 <see cref="Frame"/> and returns a <see cref="SnapshotVerdict"/>. It depends
    /// on no render-target type, only on <see cref="UnityEngine.Color32"/>, which a shim
    /// (<c>Tools/core-tests/Shim/Types/Color32.cs</c>) supplies, so it also runs under dotnet.
    /// </summary>
    public static class SnapshotCoverage
    {
        /// <summary>
        /// Number of grid cells per axis when computing spatial spread (8×8 = 64 total buckets).
        /// </summary>
        public const int GridN = 8;

        /// <summary>
        /// Minimum fill pixels per grid bucket to count as "hit".
        /// </summary>
        public const int MinFillPixelsPerBucket = 1;

        /// <summary>
        /// Fraction of pixels that must be black (R=G=B=0) to declare the buffer "all-black".
        /// </summary>
        private const float BlackThreshold = 0.97f;

        /// <summary>
        /// Fraction of pixels that must match the background colour to declare the buffer "blank".
        /// </summary>
        private const float BlankThreshold = 0.97f;

        /// <summary>
        /// Fraction of pixels that must share one bucket of a 4096-bucket full-RGB histogram (4 bits per
        /// channel) to declare the buffer "uniform"; up to 5% outliers (GPU noise, AA) are allowed.
        /// Non-obvious why: an R-only histogram would call a two-colour frame uniform when fill and background
        /// share an R high-nibble but differ in G or B.
        /// </summary>
        private const float UniformThreshold = 0.95f;

        /// <summary>
        /// Colour-match tolerance: two pixels are "same colour" if |ΔR| + |ΔG| + |ΔB| &lt;= Tolerance.
        /// </summary>
        public const int Tolerance = 15;

        /// <summary>
        /// Analyse <paramref name="frame"/> against <paramref name="background"/> and return a verdict.
        /// </summary>
        public static SnapshotVerdict Analyse(Frame frame, Color32 background)
        {
            Color32[] pixels = frame.Pixels;
            if (pixels == null) throw new ArgumentNullException(nameof(frame));
            int width = frame.Width, height = frame.Height;
            int totalPixels = width * height;
            if (totalPixels == 0) return new SnapshotVerdict(0f, 1f, 0, true, false);
            if (pixels.Length < totalPixels)
                throw new ArgumentException(
                    $"frame.Pixels.Length ({pixels.Length}) < width*height ({totalPixels}).");

            int fillCount       = 0;
            int bgCount         = 0;
            int blackCount      = 0;

            // Bucket grid for spatial spread: GridN × GridN cells.
            var bucketFill = new int[GridN * GridN];

            // Coarse full-RGB histogram for uniformity detection (see UniformThreshold).
            // Index = ((r>>4) << 8) | ((g>>4) << 4) | (b>>4).  Range: 0–4095.
            var rgbHist = new int[4096];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color32 px = pixels[y * width + x];
                    byte r = px.r, g = px.g, b = px.b;
                    // Alpha ignored for coverage analysis.

                    // Black check.
                    if (r == 0 && g == 0 && b == 0) blackCount++;

                    // Background check.
                    int dist = math.abs(r - background.r) + math.abs(g - background.g) + math.abs(b - background.b);
                    bool isBg = dist <= Tolerance;

                    if (isBg)
                    {
                        bgCount++;
                    }
                    else
                    {
                        fillCount++;
                        // Assign to grid bucket.
                        int bx = math.min(x * GridN / width,  GridN - 1);
                        int by = math.min(y * GridN / height, GridN - 1);
                        bucketFill[by * GridN + bx]++;
                    }

                    // Coarse full-RGB histogram (4 bits per channel → 16×16×16 = 4096 buckets).
                    rgbHist[((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4)]++;
                }
            }

            float fillFrac = (float)fillCount / totalPixels;
            float bgFrac   = (float)bgCount   / totalPixels;

            // IsBlank: nearly all background, or nearly all black.
            bool isBlank = bgFrac >= BlankThreshold ||
                           (float)blackCount / totalPixels >= BlackThreshold;

            // IsUniform: one coarse colour dominates, e.g. an all-red garbage frame or a renderer hang.
            // IsBlank covers the nearly-all-background case.
            int maxBucket = 0;
            for (int i = 0; i < rgbHist.Length; i++)
                if (rgbHist[i] > maxBucket) maxBucket = rgbHist[i];
            bool isUniform = (float)maxBucket / totalPixels >= UniformThreshold;

            // Count how many grid cells have at least MinFillPixelsPerBucket fill pixels.
            int bucketsHit = 0;
            for (int i = 0; i < bucketFill.Length; i++)
                if (bucketFill[i] >= MinFillPixelsPerBucket) bucketsHit++;

            return new SnapshotVerdict(fillFrac, bgFrac, bucketsHit, isBlank, isUniform);
        }

        /// <summary>
        /// Compute the mean Rec.709 luminance (relative [0,1]) of pixels farther than
        /// <see cref="Tolerance"/> from the background colour; 0.0 when there are none. A lighting test
        /// compares it under two light intensities: an unlit colour gives the same value for both.
        /// </summary>
        public static double MeanLuminanceOfNonBackground(Frame frame, Color32 background)
        {
            Color32[] pixels = frame.Pixels;
            if (pixels == null) throw new ArgumentNullException(nameof(frame));
            int width = frame.Width, height = frame.Height;
            int total = width * height;
            if (total == 0) return 0.0;
            if (pixels.Length < total)
                throw new ArgumentException(
                    $"frame.Pixels.Length ({pixels.Length}) < width*height ({total}).");

            double lumSum = 0.0;
            int    count  = 0;

            for (int i = 0; i < total; i++)
            {
                Color32 px = pixels[i];
                int dist = math.abs(px.r - background.r) + math.abs(px.g - background.g) + math.abs(px.b - background.b);
                if (dist > Tolerance)
                {
                    // Rec.709 luminance (linear approx — good enough for delta comparison).
                    double lum = (0.2126 * px.r + 0.7152 * px.g + 0.0722 * px.b) / 255.0;
                    lumSum += lum;
                    count++;
                }
            }

            return count > 0 ? lumSum / count : 0.0;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Region samplers for the draw-order snapshot: over a rectangle where two layers overlap, the top
        // layer's colour dominates the mean and the variance is near zero (no z-fight speckle).
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Mean (R, G, B) of all pixels inside the inclusive-exclusive rectangle
        /// [<paramref name="x0"/>, <paramref name="x1"/>) × [<paramref name="y0"/>, <paramref name="y1"/>),
        /// returned as a 3-element double array in [0,1] (channel / 255).
        ///
        /// Coordinates are clamped to the image bounds; an empty region returns (0,0,0).
        /// </summary>
        public static double[] SampleRegionMeanColor(Frame frame, int x0, int y0, int x1, int y1)
        {
            Color32[] pixels = frame.Pixels;
            if (pixels == null) throw new ArgumentNullException(nameof(frame));
            int width = frame.Width, height = frame.Height;
            int total = width * height;
            if (pixels.Length < total)
                throw new ArgumentException(
                    $"frame.Pixels.Length ({pixels.Length}) < width*height ({total}).");

            ClampRect(ref x0, ref y0, ref x1, ref y1, width, height);

            long sumR = 0, sumG = 0, sumB = 0;
            int count = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * width;
                for (int x = x0; x < x1; x++)
                {
                    Color32 px = pixels[row + x];
                    sumR += px.r;
                    sumG += px.g;
                    sumB += px.b;
                    count++;
                }
            }

            if (count == 0) return new double[] { 0.0, 0.0, 0.0 };
            return new double[]
            {
                (double)sumR / count / 255.0,
                (double)sumG / count / 255.0,
                (double)sumB / count / 255.0,
            };
        }

        /// <summary>
        /// Total colour variance inside the rectangle: the sum of the R, G, B variances, each normalised to
        /// [0,1]; 0 for an empty region. A flat region gives ~0. A coplanar ZWrite-On stack alternates two
        /// layers' colours per pixel and inflates it; the ZWrite-Off painter's stack stays near zero.
        /// </summary>
        public static double RegionColorVariance(Frame frame, int x0, int y0, int x1, int y1)
        {
            Color32[] pixels = frame.Pixels;
            if (pixels == null) throw new ArgumentNullException(nameof(frame));
            int width = frame.Width, height = frame.Height;
            int total = width * height;
            if (pixels.Length < total)
                throw new ArgumentException(
                    $"frame.Pixels.Length ({pixels.Length}) < width*height ({total}).");

            ClampRect(ref x0, ref y0, ref x1, ref y1, width, height);

            // Two passes: mean, then variance. Region is small (sample sub-rect), so cost is negligible.
            double[] mean = SampleRegionMeanColor(frame, x0, y0, x1, y1);

            double sumSqR = 0, sumSqG = 0, sumSqB = 0;
            int count = 0;
            for (int y = y0; y < y1; y++)
            {
                int row = y * width;
                for (int x = x0; x < x1; x++)
                {
                    Color32 px = pixels[row + x];
                    double dr = px.r / 255.0 - mean[0];
                    double dg = px.g / 255.0 - mean[1];
                    double db = px.b / 255.0 - mean[2];
                    sumSqR += dr * dr;
                    sumSqG += dg * dg;
                    sumSqB += db * db;
                    count++;
                }
            }

            if (count == 0) return 0.0;
            return (sumSqR + sumSqG + sumSqB) / count;
        }

        /// <summary>
        /// Clamp a rectangle to [0,width] × [0,height] and normalise so x0&lt;=x1, y0&lt;=y1.
        /// </summary>
        private static void ClampRect(ref int x0, ref int y0, ref int x1, ref int y1, int width, int height)
        {
            if (x1 < x0) (x0, x1) = (x1, x0);
            if (y1 < y0) (y0, y1) = (y1, y0);
            x0 = math.max(0, math.min(x0, width));
            x1 = math.max(0, math.min(x1, width));
            y0 = math.max(0, math.min(y0, height));
            y1 = math.max(0, math.min(y1, height));
        }
    }
}
