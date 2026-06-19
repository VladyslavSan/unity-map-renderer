using System;

namespace MapRenderer.Core.Imaging
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
        /// Thresholds are deliberately wide (10–85%, 8 of 64 buckets) to be GPU/driver/Unity-tolerant.
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
    /// Analyses an RGBA32 byte buffer (4 bytes per pixel, R G B A order, row-major, top-left origin)
    /// and returns a <see cref="SnapshotVerdict"/>.
    ///
    /// Design: zero dependency on UnityEngine (no Color32, no Texture2D). The caller converts
    /// Color32[] → byte[] before calling. This keeps Core GPU-free and unit-testable with a dotnet runner.
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
        /// Analyse <paramref name="pixels"/> (RGBA32, 4 bytes/pixel, row-major) and return a verdict.
        /// </summary>
        /// <param name="pixels">Flat byte array: length must equal width * height * 4.</param>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        /// <param name="bgR">Background red channel (0–255).</param>
        /// <param name="bgG">Background green channel (0–255).</param>
        /// <param name="bgB">Background blue channel (0–255).</param>
        public static SnapshotVerdict Analyse(
            byte[] pixels,
            int    width,
            int    height,
            byte   bgR,
            byte   bgG,
            byte   bgB)
        {
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            int totalPixels = width * height;
            if (totalPixels == 0) return new SnapshotVerdict(0f, 1f, 0, true, false);
            if (pixels.Length < totalPixels * 4)
                throw new ArgumentException(
                    $"pixels.Length ({pixels.Length}) < width*height*4 ({totalPixels * 4}).");

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
                    int baseIdx = (y * width + x) * 4;
                    byte r = pixels[baseIdx];
                    byte g = pixels[baseIdx + 1];
                    byte b = pixels[baseIdx + 2];
                    // Alpha ignored for coverage analysis.

                    // Black check.
                    if (r == 0 && g == 0 && b == 0) blackCount++;

                    // Background check.
                    int dist = Math.Abs(r - bgR) + Math.Abs(g - bgG) + Math.Abs(b - bgB);
                    bool isBg = dist <= Tolerance;

                    if (isBg)
                    {
                        bgCount++;
                    }
                    else
                    {
                        fillCount++;
                        // Assign to grid bucket.
                        int bx = Math.Min(x * GridN / width,  GridN - 1);
                        int by = Math.Min(y * GridN / height, GridN - 1);
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
        /// Convenience overload accepting the pixel buffer as a <c>byte[]</c> (Unity's
        /// <c>Texture2D.GetRawTextureData()</c>) with explicit RGBA32 layout.
        /// </summary>
        public static SnapshotVerdict Analyse(
            byte[] pixels,
            int    width,
            int    height,
            int    bgR,
            int    bgG,
            int    bgB)
            => Analyse(pixels, width, height, (byte)bgR, (byte)bgG, (byte)bgB);
    }
}
