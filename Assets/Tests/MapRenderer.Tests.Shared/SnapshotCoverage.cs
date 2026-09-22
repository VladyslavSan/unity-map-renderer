using System;
using Unity.Mathematics;
using UnityEngine;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Result of analysing a decoded RGBA32 pixel buffer for "map-like" coverage.
    ///
    /// Fields:
    ///   FilledFraction      — fraction of pixels that differ from background beyond tolerance.
    ///   BackgroundFraction  — fraction of pixels that match background within tolerance.
    ///   DistinctRegionBucketsHit — number of NxN grid cells containing >= MinFillPixelsPerBucket fill pixels.
    ///   IsBlank             — buffer is ~100% background or ~100% black (GPU context failure or empty render).
    ///   IsUniform           — buffer is ~100% a single coarse colour per a 4096-bucket full-RGB histogram
    ///                         (catches all-filled / garbage flat frames; full-RGB avoids false positives
    ///                         when fill shares the background's R high-nibble but differs in G or B).
    /// </summary>
    public readonly struct SnapshotVerdict
    {
        public readonly float FilledFraction;
        public readonly float BackgroundFraction;
        public readonly int   DistinctRegionBucketsHit;
        public readonly bool  IsBlank;
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
        /// Returns true when the buffer looks like a real (non-blank, non-garbage) render:
        ///   - not blank (not all-background, not all-black)
        ///   - not uniform (not a single flat colour)
        ///   - fill fraction in the expected band [minFill, maxFill]
        ///   - spatial spread: at least minBuckets grid cells contain fill pixels.
        /// Thresholds are wide (10–85%, 8 of 64 buckets) to stay GPU/driver/Unity-tolerant.
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
    /// Analyses a decoded RGBA32 <see cref="Frame"/> and returns a <see cref="SnapshotVerdict"/>.
    ///
    /// Design: zero dependency on <c>Texture2D</c> or any render-target type — only the
    /// <see cref="UnityEngine.Color32"/>-typed <see cref="Frame"/>, which itself compiles under both
    /// runners via a matching shim (<c>Tools/core-tests/Shim/Types/Color32.cs</c>). This keeps Core
    /// GPU-free and unit-testable with a dotnet runner.
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
        /// Fraction of pixels that must share a single coarse colour to declare the buffer "uniform".
        /// Uses a coarse 4096-bucket full-RGB histogram (4 bits per channel: R>>4, G>>4, B>>4).
        /// The histogram is tolerant: up to 5% outlier pixels (GPU noise, sub-pixel AA) are allowed.
        ///
        /// Design choice: full-RGB rather than R-only avoids false positives when the fill colour shares
        /// the background's R high-nibble (e.g. bg.R=26 → nibble 1; fill.R=20 → nibble 1 also, but
        /// G/B differ significantly). A purely R-channel histogram would erroneously declare such a
        /// two-colour buffer "uniform". IsBlank covers the nearly-all-background case separately, so
        /// the 95% threshold here is applied against the dominant RGB bucket only.
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

            // Coarse full-RGB histogram for uniformity detection (4096 buckets: 4 bits per channel).
            // Index = ((r>>4) << 8) | ((g>>4) << 4) | (b>>4).  Range: 0–4095.
            // Full-RGB avoids the false-positive of an R-only histogram: two colours that share an
            // R high-nibble (e.g. bg.R=26 and fill.R=20 both → nibble 1) are kept distinct if they
            // differ in G or B, so a legitimate map frame is not wrongly declared "uniform".
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

            // IsUniform: a single coarse colour dominates (catches all-fill or all-garbage flat colour).
            // IsBlank already covers the nearly-all-background case; IsUniform fires when a non-bg
            // flat colour occupies ≥95% of pixels — e.g. an all-red garbage frame or renderer hang.
            // The full-RGB histogram (4096 buckets) keeps bg and fill in separate buckets whenever
            // they differ in any channel's high nibble, preventing the R-only false positive.
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
        /// Compute the mean luminance (relative [0,1]) of pixels that are NOT the background colour
        /// (Manhattan distance > <see cref="Tolerance"/>).
        ///
        /// Luminance uses the standard Rec.709 coefficients: Y = 0.2126·R + 0.7152·G + 0.0722·B.
        ///
        /// Returns 0.0 when there are no non-background pixels (e.g. blank render or GPU failure).
        ///
        /// Used by <c>LitFillSnapshotTests</c> to prove lighting is active: under a directional
        /// light with intensity I1 vs I2, the mean luminance of non-background pixels differs.
        /// A constant unlit color would give equal luminance regardless of light intensity.
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
        // Region samplers for the multi-layer draw-order snapshot test.
        //
        // The painter's-algorithm reorder test samples a fixed screen rectangle where two
        // layers overlap, then asserts (a) the top layer's colour dominates the region mean
        // and (b) the region has near-zero colour variance (a clean composite, no coplanar
        // z-fighting speckle). Engine-free (plain ints plus the Color32 shim), so they
        // unit-test under dotnet.
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
        /// Total colour variance inside the rectangle: the sum of the per-channel variances of R, G, B
        /// (each normalised to [0,1] before squaring). A flat, single-colour region → ~0; a speckled /
        /// z-fighting region → noticeably positive. Empty region returns 0.
        ///
        /// Used by the reorder snapshot to assert a CLEAN composite (no coplanar z-fight): a coplanar
        /// ZWrite-On stack would alternate between two layers' colours per pixel and inflate this value;
        /// the ZWrite-Off painter's stack composites flat → near-zero.
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
