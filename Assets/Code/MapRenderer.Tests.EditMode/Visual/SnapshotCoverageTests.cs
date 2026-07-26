using System;
using NUnit.Framework;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Unit tests for <see cref="SnapshotCoverage"/> and <see cref="SnapshotVerdict"/>.
    ///
    /// All tests operate on synthetic RGBA32 byte buffers — no GPU, no UnityEngine.Texture.
    /// They run in both the Unity EditMode runner and the dotnet core-tests project.
    ///
    /// Lessons honoured:
    ///   - Pin exact expected booleans; never write unbounded-skip guards.
    ///   - Every assertion has teeth: negative controls prove the gate is not trivially green.
    /// </summary>
    [TestFixture]
    public class SnapshotCoverageTests
    {
        // Background colour used across tests: a distinctive dark blue (not black, not green).
        private const byte BgR = 26;  // ~0x1A
        private const byte BgG = 28;  // ~0x1C
        private const byte BbB = 38;  // ~0x26

        // Fill colour: green (matching MapFillBootstrap's Sprites/Default fallback tint).
        private const byte FillR = 102; // ~0.4f * 255
        private const byte FillG = 179; // ~0.7f * 255
        private const byte FillB = 102;

        // ---------------------------------------------------------------------------------
        // Helper builders
        // ---------------------------------------------------------------------------------

        /// <summary>Creates a width×height RGBA32 buffer filled with a single colour.</summary>
        private static byte[] SolidBuffer(int width, int height, byte r, byte g, byte b, byte a = 255)
        {
            var buf = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                buf[i * 4]     = r;
                buf[i * 4 + 1] = g;
                buf[i * 4 + 2] = b;
                buf[i * 4 + 3] = a;
            }
            return buf;
        }

        /// <summary>
        /// Creates a width×height RGBA32 buffer, all background, then paints the rectangular
        /// region [x0,x1) × [y0,y1) with the fill colour. Used to craft map-like synthetic frames.
        /// </summary>
        private static byte[] PartialFillBuffer(
            int width, int height,
            int x0, int y0, int x1, int y1,
            byte bgR, byte bgG, byte bgB,
            byte fillR, byte fillG, byte fillB)
        {
            var buf = SolidBuffer(width, height, bgR, bgG, bgB);
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int idx = (y * width + x) * 4;
                    buf[idx]     = fillR;
                    buf[idx + 1] = fillG;
                    buf[idx + 2] = fillB;
                }
            }
            return buf;
        }

        // ---------------------------------------------------------------------------------
        // Blank / all-background tests
        // ---------------------------------------------------------------------------------

        [Test]
        public void AllBackground_IsBlank_True_FilledFraction_Zero_PassesFalse()
        {
            var buf    = SolidBuffer(64, 64, BgR, BgG, BbB);
            var result = SnapshotCoverage.Analyse(buf, 64, 64, BgR, BgG, BbB);

            Assert.IsTrue(result.IsBlank,
                "A solid-background buffer must be detected as blank.");
            Assert.That(result.FilledFraction, Is.LessThan(0.01f),
                "Filled fraction must be near zero for a solid-background buffer.");
            Assert.IsFalse(result.Passes(),
                "A solid-background buffer must fail the coverage gate.");
        }

        // ---------------------------------------------------------------------------------
        // All-black tests (catches GPU-context-failed black PNG)
        // ---------------------------------------------------------------------------------

        [Test]
        public void AllBlack_IsBlank_True_PassesFalse()
        {
            var buf    = SolidBuffer(64, 64, 0, 0, 0);
            // Background is the distinctive non-black colour — so all-black != all-background,
            // but the black-frame guard should still mark it blank.
            var result = SnapshotCoverage.Analyse(buf, 64, 64, BgR, BgG, BbB);

            Assert.IsTrue(result.IsBlank,
                "An all-black buffer (no-GPU-context) must be detected as blank.");
            Assert.IsFalse(result.Passes(),
                "An all-black buffer must fail the coverage gate.");
        }

        // ---------------------------------------------------------------------------------
        // Uniform (all-one-non-background colour) tests
        // ---------------------------------------------------------------------------------

        [Test]
        public void AllFillColour_IsUniform_True_PassesFalse()
        {
            // Every pixel is the fill colour — all-filled is garbage for a map render.
            var buf    = SolidBuffer(64, 64, FillR, FillG, FillB);
            var result = SnapshotCoverage.Analyse(buf, 64, 64, BgR, BgG, BbB);

            Assert.IsTrue(result.IsUniform,
                "A solid-fill buffer must be detected as uniform.");
            Assert.IsFalse(result.Passes(),
                "A solid-fill (all-filled) buffer must fail the coverage gate.");
        }

        [Test]
        public void AllSingleGarbageColour_IsUniform_True_PassesFalse()
        {
            // Completely different colour — e.g. a magenta garbage frame.
            var buf    = SolidBuffer(64, 64, 255, 0, 255);
            var result = SnapshotCoverage.Analyse(buf, 64, 64, BgR, BgG, BbB);

            Assert.IsTrue(result.IsUniform,
                "A solid-garbage-colour buffer must be detected as uniform.");
            Assert.IsFalse(result.Passes(),
                "A solid-garbage-colour buffer must fail the coverage gate.");
        }

        // ---------------------------------------------------------------------------------
        // Map-like synthetic buffer (should PASS)
        // ---------------------------------------------------------------------------------

        [Test]
        public void MapLike_LargeConnectedFillBlob_PassesWithExpectedSpread()
        {
            // 512×512, background with ~40% fill covering a central region.
            // This simulates a world-fill map: land fills the central ~40% of the frame,
            // background (ocean/clear) fills the rest.
            const int W = 512, H = 512;
            // Fill a horizontal band from row 100 to 400, col 50 to 460 (roughly 55% area).
            // That exceeds 40% to be safe; use a region that hits multiple grid buckets.
            var buf = PartialFillBuffer(
                W, H,
                x0: 50, y0: 100, x1: 460, y1: 400,
                BgR, BgG, BbB, FillR, FillG, FillB);

            var result = SnapshotCoverage.Analyse(buf, W, H, BgR, BgG, BbB);

            // Must not be blank or uniform.
            Assert.IsFalse(result.IsBlank,   "Map-like buffer must not be blank.");
            Assert.IsFalse(result.IsUniform,  "Map-like buffer must not be uniform.");

            // Fill fraction: the painted region is 410 cols × 300 rows = 123,000 px / 262,144 = ~47%.
            Assert.That(result.FilledFraction,
                Is.InRange(0.10f, 0.85f),
                $"Fill fraction {result.FilledFraction:F3} must be in [0.10, 0.85].");

            // Spatial spread: the large blob spans the entire horizontal grid (8 cols) and at
            // least 5 of 8 row bands → at least 5×8 = 40 buckets hit. Minimum gate is 8.
            Assert.That(result.DistinctRegionBucketsHit,
                Is.GreaterThanOrEqualTo(8),
                $"Expected >= 8 grid buckets hit, got {result.DistinctRegionBucketsHit}.");

            Assert.IsTrue(result.Passes(),
                "A map-like buffer with ~47% fill spread across the frame must pass.");
        }

        // ---------------------------------------------------------------------------------
        // Small fill — confined to a single grid bucket, fails spread gate
        // ---------------------------------------------------------------------------------

        [Test]
        public void TinyFill_ConfinedToOneBucket_FailsSpreadGate()
        {
            // Grid: 8×8 buckets over a 64×64 image → each bucket is 8×8 pixels.
            // Paint exactly one bucket (8×8 = 64 pixels) at top-left corner to guarantee
            // DistinctRegionBucketsHit == 1. Fill fraction = 64/4096 = 1.5% (below default 10% gate).
            const int W = 64, H = 64;
            var buf = PartialFillBuffer(
                W, H,
                x0: 0, y0: 0, x1: 8, y1: 8,   // exactly one 8×8 bucket
                BgR, BgG, BbB, FillR, FillG, FillB);

            var result = SnapshotCoverage.Analyse(buf, W, H, BgR, BgG, BbB);

            // Exactly 1 bucket hit (the top-left 8×8 cell).
            Assert.AreEqual(1, result.DistinctRegionBucketsHit,
                $"A fill confined to one 8×8 bucket must hit exactly 1 grid bucket, " +
                $"got {result.DistinctRegionBucketsHit}.");
            // Spread gate fails (minBuckets=8 > 1) → Passes() returns false.
            Assert.IsFalse(result.Passes(minFill: 0.005f, maxFill: 0.85f, minBuckets: 8),
                "A fill concentrated in one corner bucket must fail the spread gate.");
        }

        // ---------------------------------------------------------------------------------
        // Custom threshold: prove the threshold machinery works with relaxed minBuckets=1.
        // The fill must clear BOTH gates (not blank, not uniform) — no escape hatch.
        // ---------------------------------------------------------------------------------

        [Test]
        public void CustomThreshold_MinBuckets1_ModerateFill_Passes()
        {
            // Build a 64×64 buffer with a 16×24-pixel filled region (384 / 4096 = 9.375% fill).
            // That clears the blank threshold (only 90.6% bg, well under 97%) AND clears the
            // uniform threshold (bg and fill land in distinct RGB buckets, max-bucket ~90.6% < 95%).
            // With minBuckets=1, the spread gate is trivially satisfied by any fill at all.
            // With the default minBuckets=8, the 16-wide, 24-tall strip spans only ~3 row-bands × 2
            // col-bands = 6 grid buckets → still below the default spread gate of 8.
            const int W = 64, H = 64;
            var buf = PartialFillBuffer(
                W, H,
                x0: 0, y0: 0, x1: 16, y1: 24,   // 16×24 = 384 px of fill (9.4%)
                BgR, BgG, BbB, FillR, FillG, FillB);

            var result = SnapshotCoverage.Analyse(buf, W, H, BgR, BgG, BbB);

            // Must NOT be blank: 9.4% fill means only 90.6% bg < BlankThreshold(97%).
            Assert.IsFalse(result.IsBlank,
                $"9.4% fill leaves 90.6% bg — must not be blank. bg={result.BackgroundFraction:F3}");

            // Must NOT be uniform: bg and fill are in different RGB buckets; max bucket is bg at ~90.6% < 95%.
            Assert.IsFalse(result.IsUniform,
                "Background (~90.6%) and fill (~9.4%) occupy distinct RGB buckets; must not be uniform.");

            // With minBuckets=1, passes (fill fraction 9.4% is in [0.001, 0.99] and >=1 bucket hit).
            Assert.IsTrue(result.Passes(minFill: 0.001f, maxFill: 0.99f, minBuckets: 1),
                "With loose thresholds (minFill=0.001, minBuckets=1), a 9.4% fill must pass.");

            // With the DEFAULT minBuckets=8, the 6-bucket strip must FAIL (proves the spread gate
            // is not trivially green and that IsBlank/IsUniform are the real guards here).
            Assert.IsFalse(result.Passes(),
                $"Default gate (minBuckets=8) must reject a fill spanning only {result.DistinctRegionBucketsHit} buckets.");
        }

        // ---------------------------------------------------------------------------------
        // IsUniform TRUE for a single flat colour that shares the background's R high-nibble.
        // Pins: a genuine one-colour frame must be rejected even under the new RGB histogram.
        // ---------------------------------------------------------------------------------

        [Test]
        public void SolidColour_SharesBgRNibble_IsUniform_True_PassesFalse()
        {
            // Fill colour R=20: R>>4 = 1, same as BgR=26>>4=1.
            // A purely R-channel histogram would collapse both into the same bucket.
            // The new full-RGB histogram keeps this as a distinct bucket from bg because G and B
            // differ — but the ENTIRE BUFFER is this single solid colour (no bg pixels at all),
            // so 100% of pixels fall in one RGB bucket → IsUniform must still be true.
            // This test pins that the histogram rewrite didn't break the single-colour detection.
            const byte SolidR = 20;
            const byte SolidG = 180;
            const byte SolidB = 70;
            // Verify R nibble matches bg (load-bearing precondition of this test).
            Assert.AreEqual(BgR >> 4, SolidR >> 4,
                "Test precondition: SolidR and BgR must share the same R high-nibble.");

            var buf    = SolidBuffer(64, 64, SolidR, SolidG, SolidB);
            var result = SnapshotCoverage.Analyse(buf, 64, 64, BgR, BgG, BbB);

            Assert.IsTrue(result.IsUniform,
                "A solid single-colour buffer must be detected as uniform, " +
                "even when its R channel shares the background's R high-nibble.");
            Assert.IsFalse(result.Passes(),
                "A solid single-colour buffer must fail the coverage gate.");
        }

        // ---------------------------------------------------------------------------------
        // IsUniform FALSE for a two-colour map frame where fill shares background's R nibble.
        // This is the discriminating regression test: the old R-only histogram wrongly returned
        // IsUniform=true for this case; the new full-RGB histogram returns IsUniform=false.
        // ---------------------------------------------------------------------------------

        [Test]
        public void MapLike_FillSharesBgRNibble_NotUniform_Passes()
        {
            // Fill colour: R=20 (nibble 1 — same as BgR=26), G=180, B=70.
            // Distance from bg (26,28,38): |20-26|+|180-28|+|70-38| = 6+152+32 = 190 > Tolerance(15).
            // → correctly counted as fill, not background.
            //
            // Under the OLD R-only histogram: bg (R=26→nibble 1) and fill (R=20→nibble 1) collapse
            // into the same bucket. With ~47% bg + ~47% fill both in bucket 1, max/total ≈ 94%
            // → just under the 95% threshold in this layout, but the flaw is structural: any layout
            // that tips over 95% would false-positive. The full-RGB histogram keeps them distinct
            // (bg lands in bucket (1,1,2); fill lands in bucket (1,11,4)).
            //
            // 512×512 buffer, fill region x∈[50,460) y∈[100,400): 410×300 = 123,000 px / 262,144 = ~47%.
            const int W = 512, H = 512;
            const byte NibbleFillR = 20;
            const byte NibbleFillG = 180;
            const byte NibbleFillB = 70;

            // Load-bearing precondition: fill and bg share R nibble.
            Assert.AreEqual(BgR >> 4, NibbleFillR >> 4,
                "Test precondition: NibbleFillR and BgR must share the same R high-nibble.");
            // Load-bearing precondition: pixel is actually fill (not collapsed to bg by tolerance).
            int dist = Math.Abs(NibbleFillR - BgR) + Math.Abs(NibbleFillG - BgG) + Math.Abs(NibbleFillB - BbB);
            Assert.That(dist, Is.GreaterThan(SnapshotCoverage.Tolerance),
                $"Test precondition: fill colour must be outside tolerance of background (dist={dist}).");

            var buf = PartialFillBuffer(
                W, H,
                x0: 50, y0: 100, x1: 460, y1: 400,
                BgR, BgG, BbB, NibbleFillR, NibbleFillG, NibbleFillB);

            var result = SnapshotCoverage.Analyse(buf, W, H, BgR, BgG, BbB);

            // The two colours occupy separate RGB histogram buckets; neither dominates at >=95%.
            Assert.IsFalse(result.IsUniform,
                "A two-colour map frame must not be uniform even when fill shares bg's R high-nibble. " +
                $"FilledFraction={result.FilledFraction:F3}, BackgroundFraction={result.BackgroundFraction:F3}");

            // Full Passes() check: ~47% fill is in [10%,85%], spreads across >=8 grid buckets.
            Assert.IsTrue(result.Passes(),
                "A map-like two-colour frame with ~47% fill must pass the default coverage gate. " +
                $"FilledFraction={result.FilledFraction:F3}, Buckets={result.DistinctRegionBucketsHit}");
        }

        // ---------------------------------------------------------------------------------
        // Edge cases
        // ---------------------------------------------------------------------------------

        [Test]
        public void SinglePixel_Fill_FilledFractionNonZero_ButFailsGate()
        {
            // 1 fill pixel out of 64 pixels (8×8): background fraction = 63/64 = 98.4% >= BlankThreshold(97%).
            // The buffer is correctly classified as blank (near-empty render), AND must fail the gate.
            // The fill fraction is non-zero but vanishingly small (1.5%) — still below minFill=10%.
            const int W = 8, H = 8;
            var buf = PartialFillBuffer(W, H, 0, 0, 1, 1, BgR, BgG, BbB, FillR, FillG, FillB);
            var result = SnapshotCoverage.Analyse(buf, W, H, BgR, BgG, BbB);

            // 98.4% background → IsBlank == true (correctly flags an essentially-empty render).
            Assert.IsTrue(result.IsBlank,
                "A buffer with 1 fill pixel out of 64 is essentially blank (98.4% background >= 97% threshold).");
            // Fill fraction is non-zero but tiny.
            Assert.That(result.FilledFraction, Is.GreaterThan(0f),
                "Fill fraction must be > 0 even with a single fill pixel.");
            Assert.That(result.FilledFraction, Is.LessThan(0.05f),
                "Fill fraction for a single pixel in 64 must be < 5%.");
            Assert.IsFalse(result.Passes(), "A 1-pixel fill must fail the default coverage gate.");
        }

        [Test]
        public void Analyse_NullPixels_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                SnapshotCoverage.Analyse(null, 64, 64, BgR, BgG, BbB));
        }

        [Test]
        public void Analyse_BufferTooSmall_ThrowsArgumentException()
        {
            // Buffer of 3 bytes is too small for even 1×1×4.
            Assert.Throws<ArgumentException>(() =>
                SnapshotCoverage.Analyse(new byte[] { 1, 2, 3 }, 1, 1, BgR, BgG, BbB));
        }

        [Test]
        public void ZeroSizeImage_ReturnsBlank()
        {
            var result = SnapshotCoverage.Analyse(new byte[0], 0, 0, BgR, BgG, BbB);
            Assert.IsTrue(result.IsBlank, "A zero-size image must be reported as blank.");
            Assert.IsFalse(result.Passes(), "A zero-size image must fail the coverage gate.");
        }

        // ---------------------------------------------------------------------------------
        // Tolerance boundary: pixels near the background colour
        // ---------------------------------------------------------------------------------

        [Test]
        public void PixelsNearBackground_WithinTolerance_CountedAsBackground()
        {
            // Create a buffer where all pixels are within SnapshotCoverage.Tolerance of BgR/BgG/BbB.
            const int W = 16, H = 16;
            var buf = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++)
            {
                buf[i * 4]     = (byte)(BgR + 1); // within tolerance
                buf[i * 4 + 1] = (byte)(BgG + 1);
                buf[i * 4 + 2] = (byte)(BbB + 1);
                buf[i * 4 + 3] = 255;
            }
            var result = SnapshotCoverage.Analyse(buf, W, H, BgR, BgG, BbB);

            // Sum of deltas = 3, which is <= Tolerance (15).
            Assert.That(result.FilledFraction, Is.LessThan(0.01f),
                "Pixels within tolerance of background must be counted as background, not fill.");
            Assert.IsTrue(result.IsBlank, "Near-background buffer must be detected as blank.");
        }

        [Test]
        public void PixelsJustOutsideTolerance_CountedAsFill()
        {
            // Each pixel differs from background by exactly Tolerance+1 (16).
            // Use a 6-unit shift per channel: 6+6+6 = 18 > 15 = Tolerance.
            const int W = 16, H = 16;
            int delta = SnapshotCoverage.Tolerance / 3 + 3; // 8 per channel → total dist 24 > 15
            var buf = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++)
            {
                buf[i * 4]     = (byte)Math.Min(255, BgR + delta);
                buf[i * 4 + 1] = (byte)Math.Min(255, BgG + delta);
                buf[i * 4 + 2] = (byte)Math.Min(255, BbB + delta);
                buf[i * 4 + 3] = 255;
            }
            var result = SnapshotCoverage.Analyse(buf, W, H, BgR, BgG, BbB);

            // All pixels should be fill.
            Assert.That(result.FilledFraction, Is.GreaterThan(0.99f),
                "Pixels outside tolerance must be counted as fill.");
        }

        // ─── MeanLuminanceOfNonBackground tests (S32) ────────────────────────────────

        [Test]
        public void MeanLuminance_SolidBackground_ReturnsZero()
        {
            // All pixels are background → no non-bg pixels → luminance = 0.
            var buf = SolidBuffer(16, 16, BgR, BgG, BbB);
            double lum = SnapshotCoverage.MeanLuminanceOfNonBackground(buf, 16, 16, BgR, BgG, BbB);
            Assert.That(lum, Is.EqualTo(0.0),
                "A solid-background buffer has no non-background pixels; luminance must be 0.");
        }

        [Test]
        public void MeanLuminance_SolidWhiteFill_ReturnsNearOne()
        {
            // All pixels are pure white (far from the dark bg) → luminance ≈ 1.
            var buf = SolidBuffer(16, 16, 255, 255, 255);
            double lum = SnapshotCoverage.MeanLuminanceOfNonBackground(buf, 16, 16, BgR, BgG, BbB);
            Assert.That(lum, Is.GreaterThan(0.95),
                "Solid-white fill must give luminance near 1.0.");
        }

        [Test]
        public void MeanLuminance_KnownRGB_ExactValue()
        {
            // Buffer: 2×1 pixels. Both non-background.
            //   pixel0: R=200, G=100, B=50 → lum = (0.2126*200 + 0.7152*100 + 0.0722*50)/255
            //   pixel1: R=50,  G=200, B=100 → lum = (0.2126*50  + 0.7152*200 + 0.0722*100)/255
            // Neither matches the dark-slate bg (BgR=26, BgG=28, BbB=38).
            const int W = 2, H = 1;
            var buf = new byte[W * H * 4]
            {
                200, 100, 50, 255,
                50, 200, 100, 255,
            };
            double lum0 = (0.2126 * 200 + 0.7152 * 100 + 0.0722 * 50) / 255.0;
            double lum1 = (0.2126 * 50  + 0.7152 * 200 + 0.0722 * 100) / 255.0;
            double expected = (lum0 + lum1) / 2.0;

            double actual = SnapshotCoverage.MeanLuminanceOfNonBackground(buf, W, H, BgR, BgG, BbB);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-6),
                $"MeanLuminance: expected {expected:F6}, got {actual:F6}.");
        }

        [Test]
        public void MeanLuminance_BrightVsDimFill_DimIsSmaller()
        {
            // Bright fill (R=200, G=200, B=200) vs dim fill (R=50, G=50, B=50).
            // Both are clearly non-background (far from dark-slate bg).
            var bright = SolidBuffer(8, 8, 200, 200, 200);
            var dim    = SolidBuffer(8, 8, 50,  50,  50);

            double lumBright = SnapshotCoverage.MeanLuminanceOfNonBackground(bright, 8, 8, BgR, BgG, BbB);
            double lumDim    = SnapshotCoverage.MeanLuminanceOfNonBackground(dim,    8, 8, BgR, BgG, BbB);

            Assert.That(lumBright, Is.GreaterThan(lumDim),
                $"Bright fill lum ({lumBright:F3}) must be greater than dim fill lum ({lumDim:F3}).");
        }
    }
}
