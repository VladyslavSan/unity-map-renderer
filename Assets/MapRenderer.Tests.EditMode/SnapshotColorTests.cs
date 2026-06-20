using NUnit.Framework;
using MapRenderer.Core.Imaging;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S07 — unit tests for the region samplers added to <see cref="SnapshotCoverage"/>
    /// (<c>SampleRegionMeanColor</c> + <c>RegionColorVariance</c>), used by the multi-layer
    /// reorder snapshot test. Engine-free → runs in both the Unity EditMode runner and the
    /// fast dotnet core-tests project. Hand-built RGBA buffers with KNOWN regions pin exact
    /// mean colours and low-vs-high variance.
    /// </summary>
    [TestFixture]
    public class SnapshotColorTests
    {
        // Build a width×height RGBA32 top-left-origin buffer; pixelFn(x,y) → (r,g,b,a).
        private static byte[] Build(int w, int h, System.Func<int, int, (byte, byte, byte, byte)> fn)
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int b = (y * w + x) * 4;
                var (r, g, bl, a) = fn(x, y);
                px[b] = r; px[b + 1] = g; px[b + 2] = bl; px[b + 3] = a;
            }
            return px;
        }

        [Test]
        public void SampleRegionMeanColor_UniformRegion_ReturnsExactColor()
        {
            // Whole 8×8 image is solid (100, 200, 50).
            byte[] px = Build(8, 8, (x, y) => (100, 200, 50, 255));
            double[] mean = SnapshotCoverage.SampleRegionMeanColor(px, 8, 8, 0, 0, 8, 8);
            Assert.That(mean[0], Is.EqualTo(100.0 / 255.0).Within(1e-9));
            Assert.That(mean[1], Is.EqualTo(200.0 / 255.0).Within(1e-9));
            Assert.That(mean[2], Is.EqualTo(50.0  / 255.0).Within(1e-9));
        }

        [Test]
        public void SampleRegionMeanColor_SamplesOnlyTheGivenSubRect()
        {
            // Left half red, right half blue. Sample only the right half → pure blue.
            byte[] px = Build(8, 8, (x, y) => x < 4 ? ((byte)255, (byte)0, (byte)0, (byte)255)
                                                    : ((byte)0, (byte)0, (byte)255, (byte)255));
            double[] right = SnapshotCoverage.SampleRegionMeanColor(px, 8, 8, 4, 0, 8, 8);
            Assert.That(right[0], Is.EqualTo(0.0).Within(1e-9), "right-half mean R must be 0 (no red).");
            Assert.That(right[2], Is.EqualTo(1.0).Within(1e-9), "right-half mean B must be 1 (all blue).");

            double[] left = SnapshotCoverage.SampleRegionMeanColor(px, 8, 8, 0, 0, 4, 8);
            Assert.That(left[0], Is.EqualTo(1.0).Within(1e-9), "left-half mean R must be 1 (all red).");
            Assert.That(left[2], Is.EqualTo(0.0).Within(1e-9), "left-half mean B must be 0 (no blue).");
        }

        [Test]
        public void RegionColorVariance_UniformRegion_IsZero()
        {
            byte[] px = Build(8, 8, (x, y) => (40, 80, 160, 255));
            double var = SnapshotCoverage.RegionColorVariance(px, 8, 8, 0, 0, 8, 8);
            Assert.That(var, Is.EqualTo(0.0).Within(1e-9),
                "A flat single-colour region must have zero colour variance (clean composite).");
        }

        [Test]
        public void RegionColorVariance_CheckerRegion_IsHigh()
        {
            // 1px checkerboard of black/white → maximal per-channel variance (0.25 per channel × 3).
            byte[] px = Build(8, 8, (x, y) => ((x + y) % 2 == 0) ? ((byte)255, (byte)255, (byte)255, (byte)255)
                                                                 : ((byte)0, (byte)0, (byte)0, (byte)255));
            double var = SnapshotCoverage.RegionColorVariance(px, 8, 8, 0, 0, 8, 8);
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
            byte[] clean   = Build(8, 8, (x, y) => (0, 200, 0, 255));
            byte[] speckle = Build(8, 8, (x, y) => ((x + y) % 2 == 0) ? ((byte)0, (byte)200, (byte)0, (byte)255)
                                                                      : ((byte)200, (byte)0, (byte)200, (byte)255));
            double cleanVar   = SnapshotCoverage.RegionColorVariance(clean,   8, 8, 0, 0, 8, 8);
            double speckleVar = SnapshotCoverage.RegionColorVariance(speckle, 8, 8, 0, 0, 8, 8);
            Assert.That(cleanVar, Is.LessThan(0.001), "Clean composite variance must be near zero.");
            Assert.That(speckleVar, Is.GreaterThan(cleanVar + 0.1),
                "Speckle (z-fight) variance must dominate the clean variance by a wide margin.");
        }

        [Test]
        public void SampleRegionMeanColor_ClampsOutOfBoundsRect()
        {
            byte[] px = Build(4, 4, (x, y) => (10, 20, 30, 255));
            // Rect exceeds bounds; must clamp and still return the uniform colour.
            double[] mean = SnapshotCoverage.SampleRegionMeanColor(px, 4, 4, -5, -5, 100, 100);
            Assert.That(mean[0], Is.EqualTo(10.0 / 255.0).Within(1e-9));
            Assert.That(mean[1], Is.EqualTo(20.0 / 255.0).Within(1e-9));
            Assert.That(mean[2], Is.EqualTo(30.0 / 255.0).Within(1e-9));
        }

        [Test]
        public void EmptyRegion_ReturnsZeros()
        {
            byte[] px = Build(4, 4, (x, y) => (10, 20, 30, 255));
            double[] mean = SnapshotCoverage.SampleRegionMeanColor(px, 4, 4, 2, 2, 2, 2); // zero-area
            Assert.That(mean[0], Is.EqualTo(0.0));
            Assert.That(mean[1], Is.EqualTo(0.0));
            Assert.That(mean[2], Is.EqualTo(0.0));
            Assert.That(SnapshotCoverage.RegionColorVariance(px, 4, 4, 2, 2, 2, 2), Is.EqualTo(0.0));
        }
    }
}
