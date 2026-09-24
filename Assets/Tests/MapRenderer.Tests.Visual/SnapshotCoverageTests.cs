// Fast-lane tests of the SnapshotCoverage/SnapshotVerdict test-infra types. Non-obvious why: they sit at
// Visual/'s root because they test a test-harness type, so no commit-scope topic fits, and as this assembly's
// only fast-lane members (Tools/core-tests) they cannot join a GPU-touching file without breaking that compile.
//
// Contents:
//   SnapshotColorTests     — unit tests for the region samplers on SnapshotCoverage (SampleRegionMeanColor + RegionColorVariance), used by the multi-layer reorder snapshot test.
//   SnapshotCoverageTests  — Unit tests for SnapshotCoverage and SnapshotVerdict.

using NUnit.Framework;
using System;
using UnityEngine;

namespace MapRenderer.Tests.Visual
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // SnapshotColorTests — unit tests for the region samplers on SnapshotCoverage (SampleRegionMeanColor…
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unit tests for the region samplers on <see cref="SnapshotCoverage"/>
    /// (<c>SampleRegionMeanColor</c> + <c>RegionColorVariance</c>), used by the multi-layer
    /// reorder snapshot test. Engine-free → runs in both the Unity EditMode runner and the
    /// fast dotnet core-tests project. Hand-built frames with KNOWN regions pin exact
    /// mean colours and low-vs-high variance.
    /// </summary>
    [TestFixture]
    public class SnapshotColorTests
    {
        // Build a width×height frame; pixelFn(x,y) → (r,g,b,a).
        private static Frame Build(int w, int h, System.Func<int, int, (byte, byte, byte, byte)> fn)
        {
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, bl, a) = fn(x, y);
                px[y * w + x] = new Color32(r, g, bl, a);
            }
            return new Frame(px, w, h);
        }

        [Test]
        public void SampleRegionMeanColor_UniformRegion_ReturnsExactColor()
        {
            // Whole 8×8 image is solid (100, 200, 50).
            Frame frame = Build(8, 8, (x, y) => (100, 200, 50, 255));
            double[] mean = SnapshotCoverage.SampleRegionMeanColor(frame, 0, 0, 8, 8);
            Assert.That(mean[0], Is.EqualTo(100.0 / 255.0).Within(1e-9));
            Assert.That(mean[1], Is.EqualTo(200.0 / 255.0).Within(1e-9));
            Assert.That(mean[2], Is.EqualTo(50.0  / 255.0).Within(1e-9));
        }

        [Test]
        public void SampleRegionMeanColor_SamplesOnlyTheGivenSubRect()
        {
            // Left half red, right half blue. Sample only the right half → pure blue.
            Frame frame = Build(8, 8, (x, y) => x < 4 ? ((byte)255, (byte)0, (byte)0, (byte)255)
                                                       : ((byte)0, (byte)0, (byte)255, (byte)255));
            double[] right = SnapshotCoverage.SampleRegionMeanColor(frame, 4, 0, 8, 8);
            Assert.That(right[0], Is.EqualTo(0.0).Within(1e-9), "right-half mean R must be 0 (no red).");
            Assert.That(right[2], Is.EqualTo(1.0).Within(1e-9), "right-half mean B must be 1 (all blue).");

            double[] left = SnapshotCoverage.SampleRegionMeanColor(frame, 0, 0, 4, 8);
            Assert.That(left[0], Is.EqualTo(1.0).Within(1e-9), "left-half mean R must be 1 (all red).");
            Assert.That(left[2], Is.EqualTo(0.0).Within(1e-9), "left-half mean B must be 0 (no blue).");
        }

        [Test]
        public void RegionColorVariance_UniformRegion_IsZero()
        {
            Frame frame = Build(8, 8, (x, y) => (40, 80, 160, 255));
            double var = SnapshotCoverage.RegionColorVariance(frame, 0, 0, 8, 8);
            Assert.That(var, Is.EqualTo(0.0).Within(1e-9),
                "A flat single-colour region must have zero colour variance (clean composite).");
        }

        [Test]
        public void RegionColorVariance_CheckerRegion_IsHigh()
        {
            // 1px checkerboard of black/white → maximal per-channel variance (0.25 per channel × 3).
            Frame frame = Build(8, 8, (x, y) => ((x + y) % 2 == 0) ? ((byte)255, (byte)255, (byte)255, (byte)255)
                                                                    : ((byte)0, (byte)0, (byte)0, (byte)255));
            double var = SnapshotCoverage.RegionColorVariance(frame, 0, 0, 8, 8);
            // Each channel: values are 0 or 1 with equal probability → variance = 0.25.
            // Sum over R,G,B → 0.75. This is FAR above any clean-composite threshold.
            Assert.That(var, Is.EqualTo(0.75).Within(1e-6),
                "A black/white checker (z-fight analogue) must have high variance (~0.75 summed).");
            Assert.That(var, Is.GreaterThan(0.5),
                "Speckle variance must clearly exceed any low clean-composite threshold.");
        }

        [Test]
        public void RegionColorVariance_DistinguishesCleanFromSpeckle()
        {
            // Clean: solid green. Speckle: alternating green/magenta.
            Frame clean   = Build(8, 8, (x, y) => (0, 200, 0, 255));
            Frame speckle = Build(8, 8, (x, y) => ((x + y) % 2 == 0) ? ((byte)0, (byte)200, (byte)0, (byte)255)
                                                                      : ((byte)200, (byte)0, (byte)200, (byte)255));
            double cleanVar   = SnapshotCoverage.RegionColorVariance(clean,   0, 0, 8, 8);
            double speckleVar = SnapshotCoverage.RegionColorVariance(speckle, 0, 0, 8, 8);
            Assert.That(cleanVar, Is.LessThan(0.001), "Clean composite variance must be near zero.");
            Assert.That(speckleVar, Is.GreaterThan(cleanVar + 0.1),
                "Speckle (z-fight) variance must dominate the clean variance by a wide margin.");
        }

        [Test]
        public void SampleRegionMeanColor_ClampsOutOfBoundsRect()
        {
            Frame frame = Build(4, 4, (x, y) => (10, 20, 30, 255));
            // Rect exceeds bounds; must clamp and still return the uniform colour.
            double[] mean = SnapshotCoverage.SampleRegionMeanColor(frame, -5, -5, 100, 100);
            Assert.That(mean[0], Is.EqualTo(10.0 / 255.0).Within(1e-9));
            Assert.That(mean[1], Is.EqualTo(20.0 / 255.0).Within(1e-9));
            Assert.That(mean[2], Is.EqualTo(30.0 / 255.0).Within(1e-9));
        }

        [Test]
        public void EmptyRegion_ReturnsZeros()
        {
            Frame frame = Build(4, 4, (x, y) => (10, 20, 30, 255));
            double[] mean = SnapshotCoverage.SampleRegionMeanColor(frame, 2, 2, 2, 2); // zero-area
            Assert.That(mean[0], Is.EqualTo(0.0));
            Assert.That(mean[1], Is.EqualTo(0.0));
            Assert.That(mean[2], Is.EqualTo(0.0));
            Assert.That(SnapshotCoverage.RegionColorVariance(frame, 2, 2, 2, 2), Is.EqualTo(0.0));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SnapshotCoverageTests — Unit tests for SnapshotCoverage and SnapshotVerdict.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unit tests for <see cref="SnapshotCoverage"/> and <see cref="SnapshotVerdict"/> over synthetic
    /// <see cref="Frame"/> buffers (no GPU), in both the Unity EditMode runner and dotnet core-tests.
    /// They pin exact expected booleans, and negative controls prove the gate is not trivially green.
    /// </summary>
    [TestFixture]
    public class SnapshotCoverageTests
    {
        // Background colour used across tests: a distinctive dark blue (not black, not green).
        private const byte BgR = 26;  // ~0x1A
        private const byte BgG = 28;  // ~0x1C
        private const byte BbB = 38;  // ~0x26
        private static readonly Color32 Bg = new Color32(BgR, BgG, BbB, 255);

        // Fill colour: green (matching MapFillBootstrap's Sprites/Default fallback tint).
        private const byte FillR = 102; // ~0.4f * 255
        private const byte FillG = 179; // ~0.7f * 255
        private const byte FillB = 102;

        // ---------------------------------------------------------------------------------
        // Helper builders
        // ---------------------------------------------------------------------------------

        /// <summary>Creates a width×height frame filled with a single colour.</summary>
        private static Frame SolidBuffer(int width, int height, byte r, byte g, byte b, byte a = 255)
        {
            var buf = new Color32[width * height];
            var c = new Color32(r, g, b, a);
            for (int i = 0; i < buf.Length; i++) buf[i] = c;
            return new Frame(buf, width, height);
        }

        /// <summary>
        /// Creates a width×height frame, all background, then paints the rectangular
        /// region [x0,x1) × [y0,y1) with the fill colour. Crafts map-like synthetic frames.
        /// </summary>
        private static Frame PartialFillBuffer(
            int width, int height,
            int x0, int y0, int x1, int y1,
            byte bgR, byte bgG, byte bgB,
            byte fillR, byte fillG, byte fillB)
        {
            Frame frame = SolidBuffer(width, height, bgR, bgG, bgB);
            var fill = new Color32(fillR, fillG, fillB, 255);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    frame.Pixels[y * width + x] = fill;
            return frame;
        }

        // ---------------------------------------------------------------------------------
        // Blank / all-background tests
        // ---------------------------------------------------------------------------------

        [Test]
        public void AllBackground_IsBlank_True_FilledFraction_Zero_PassesFalse()
        {
            Frame frame  = SolidBuffer(64, 64, BgR, BgG, BbB);
            var result = SnapshotCoverage.Analyse(frame, Bg);

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
            Frame frame  = SolidBuffer(64, 64, 0, 0, 0);
            // Background is the distinctive non-black colour — so all-black != all-background,
            // but the black-frame guard should still mark it blank.
            var result = SnapshotCoverage.Analyse(frame, Bg);

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
            Frame frame  = SolidBuffer(64, 64, FillR, FillG, FillB);
            var result = SnapshotCoverage.Analyse(frame, Bg);

            Assert.IsTrue(result.IsUniform,
                "A solid-fill buffer must be detected as uniform.");
            Assert.IsFalse(result.Passes(),
                "A solid-fill (all-filled) buffer must fail the coverage gate.");
        }

        [Test]
        public void AllSingleGarbageColour_IsUniform_True_PassesFalse()
        {
            // Completely different colour — e.g. a magenta garbage frame.
            Frame frame  = SolidBuffer(64, 64, 255, 0, 255);
            var result = SnapshotCoverage.Analyse(frame, Bg);

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
            // 512×512 world-fill map: land fills the central ~40% of the frame, background (ocean) fills
            // the rest.
            const int W = 512, H = 512;
            // Fill a horizontal band from row 100 to 400, col 50 to 460 (roughly 55% area).
            // That exceeds 40% to be safe; use a region that hits multiple grid buckets.
            Frame frame = PartialFillBuffer(
                W, H,
                x0: 50, y0: 100, x1: 460, y1: 400,
                BgR, BgG, BbB, FillR, FillG, FillB);

            var result = SnapshotCoverage.Analyse(frame, Bg);

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
            // An 8×8 grid over 64×64 px: paint one whole 8×8 bucket so DistinctRegionBucketsHit == 1.
            // Fill fraction = 64/4096 = 1.5%, below the default 10% gate.
            const int W = 64, H = 64;
            Frame frame = PartialFillBuffer(
                W, H,
                x0: 0, y0: 0, x1: 8, y1: 8,   // exactly one 8×8 bucket
                BgR, BgG, BbB, FillR, FillG, FillB);

            var result = SnapshotCoverage.Analyse(frame, Bg);

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
            // Non-obvious why: a 16×24 strip in 64×64 (9.4% fill) clears the blank (97%) and uniform (95%)
            // gates. It spans ~6 grid buckets, so minBuckets=1 passes and the default 8 would not.
            const int W = 64, H = 64;
            Frame frame = PartialFillBuffer(
                W, H,
                x0: 0, y0: 0, x1: 16, y1: 24,   // 16×24 = 384 px of fill (9.4%)
                BgR, BgG, BbB, FillR, FillG, FillB);

            var result = SnapshotCoverage.Analyse(frame, Bg);

            // Must NOT be blank: 9.4% fill leaves 90.6% bg < BlankThreshold(97%).
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
            // R=20 shares the background's R nibble, but the WHOLE buffer is this one colour, so 100% of
            // pixels fall in one RGB bucket and the full-RGB histogram must still report IsUniform.
            const byte SolidR = 20;
            const byte SolidG = 180;
            const byte SolidB = 70;
            // Verify R nibble matches bg (load-bearing precondition of this test).
            Assert.AreEqual(BgR >> 4, SolidR >> 4,
                "Test precondition: SolidR and BgR must share the same R high-nibble.");

            Frame frame = SolidBuffer(64, 64, SolidR, SolidG, SolidB);
            var result = SnapshotCoverage.Analyse(frame, Bg);

            Assert.IsTrue(result.IsUniform,
                "A solid single-colour buffer must be detected as uniform, " +
                "even when its R channel shares the background's R high-nibble.");
            Assert.IsFalse(result.Passes(),
                "A solid single-colour buffer must fail the coverage gate.");
        }

        // ---------------------------------------------------------------------------------
        // IsUniform FALSE for a two-colour map frame where fill shares background's R nibble. An R-only
        // histogram would merge the two colours into one bucket.
        // ---------------------------------------------------------------------------------

        [Test]
        public void MapLike_FillSharesBgRNibble_NotUniform_Passes()
        {
            // Fill (20,180,70) is 190 from bg (26,28,38), past Tolerance 15, but shares its R nibble. Full-RGB
            // buckets keep them apart: bg (1,1,2), fill (1,11,4). The fill region is 410×300 px (~47%).
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

            Frame frame = PartialFillBuffer(
                W, H,
                x0: 50, y0: 100, x1: 460, y1: 400,
                BgR, BgG, BbB, NibbleFillR, NibbleFillG, NibbleFillB);

            var result = SnapshotCoverage.Analyse(frame, Bg);

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
            // 1 fill pixel in 8×8: background is 98.4% >= BlankThreshold (97%), so the frame is blank and
            // fails the gate; its 1.5% fill is also below minFill=10%.
            const int W = 8, H = 8;
            Frame frame = PartialFillBuffer(W, H, 0, 0, 1, 1, BgR, BgG, BbB, FillR, FillG, FillB);
            var result = SnapshotCoverage.Analyse(frame, Bg);

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
                SnapshotCoverage.Analyse(new Frame(null, 64, 64), Bg));
        }

        [Test]
        public void Analyse_BufferTooSmall_ThrowsArgumentException()
        {
            // Buffer of 1 pixel is too small for even 1×1.
            Assert.Throws<ArgumentException>(() =>
                SnapshotCoverage.Analyse(new Frame(new Color32[0], 1, 1), Bg));
        }

        [Test]
        public void ZeroSizeImage_ReturnsBlank()
        {
            var result = SnapshotCoverage.Analyse(new Frame(new Color32[0], 0, 0), Bg);
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
            var buf = new Color32[W * H];
            var c = new Color32((byte)(BgR + 1), (byte)(BgG + 1), (byte)(BbB + 1), 255); // within tolerance
            for (int i = 0; i < buf.Length; i++) buf[i] = c;
            var result = SnapshotCoverage.Analyse(new Frame(buf, W, H), Bg);

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
            var buf = new Color32[W * H];
            var c = new Color32(
                (byte)Math.Min(255, BgR + delta), (byte)Math.Min(255, BgG + delta), (byte)Math.Min(255, BbB + delta), 255);
            for (int i = 0; i < buf.Length; i++) buf[i] = c;
            var result = SnapshotCoverage.Analyse(new Frame(buf, W, H), Bg);

            // All pixels should be fill.
            Assert.That(result.FilledFraction, Is.GreaterThan(0.99f),
                "Pixels outside tolerance must be counted as fill.");
        }

        // ─── MeanLuminanceOfNonBackground tests ─────────────────────────────────────

        [Test]
        public void MeanLuminance_SolidBackground_ReturnsZero()
        {
            // All pixels are background → no non-bg pixels → luminance = 0.
            Frame frame = SolidBuffer(16, 16, BgR, BgG, BbB);
            double lum = SnapshotCoverage.MeanLuminanceOfNonBackground(frame, Bg);
            Assert.That(lum, Is.EqualTo(0.0),
                "A solid-background buffer has no non-background pixels; luminance must be 0.");
        }

        [Test]
        public void MeanLuminance_SolidWhiteFill_ReturnsNearOne()
        {
            // All pixels are pure white (far from the dark bg) → luminance ≈ 1.
            Frame frame = SolidBuffer(16, 16, 255, 255, 255);
            double lum = SnapshotCoverage.MeanLuminanceOfNonBackground(frame, Bg);
            Assert.That(lum, Is.GreaterThan(0.95),
                "Solid-white fill must give luminance near 1.0.");
        }

        [Test]
        public void MeanLuminance_KnownRGB_ExactValue()
        {
            // 2×1 buffer, both pixels non-background; each lum = (0.2126·R + 0.7152·G + 0.0722·B)/255.
            var buf = new Color32[]
            {
                new Color32(200, 100, 50, 255),
                new Color32(50, 200, 100, 255),
            };
            var frame = new Frame(buf, 2, 1);
            double lum0 = (0.2126 * 200 + 0.7152 * 100 + 0.0722 * 50) / 255.0;
            double lum1 = (0.2126 * 50  + 0.7152 * 200 + 0.0722 * 100) / 255.0;
            double expected = (lum0 + lum1) / 2.0;

            double actual = SnapshotCoverage.MeanLuminanceOfNonBackground(frame, Bg);
            Assert.That(actual, Is.EqualTo(expected).Within(1e-6),
                $"MeanLuminance: expected {expected:F6}, got {actual:F6}.");
        }

        [Test]
        public void MeanLuminance_BrightVsDimFill_DimIsSmaller()
        {
            // Bright fill (R=200, G=200, B=200) vs dim fill (R=50, G=50, B=50).
            // Both are clearly non-background (far from dark-slate bg).
            Frame bright = SolidBuffer(8, 8, 200, 200, 200);
            Frame dim    = SolidBuffer(8, 8, 50,  50,  50);

            double lumBright = SnapshotCoverage.MeanLuminanceOfNonBackground(bright, Bg);
            double lumDim    = SnapshotCoverage.MeanLuminanceOfNonBackground(dim,    Bg);

            Assert.That(lumBright, Is.GreaterThan(lumDim),
                $"Bright fill lum ({lumBright:F3}) must be greater than dim fill lum ({lumDim:F3}).");
        }
    }
}
