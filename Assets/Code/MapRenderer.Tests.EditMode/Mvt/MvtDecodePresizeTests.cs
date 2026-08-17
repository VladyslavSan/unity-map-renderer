// EditMode only (decodes a real MVT fixture through MvtDecoder, which lives in MapRenderer.Jobs and is not
// compiled by the Tools/core-tests seam).

using System;
using System.IO;
using NUnit.Framework;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Mvt
{
    /// <summary>
    /// H1 (safe subset) — <see cref="MvtDecoder"/> pre-sizes each layer's <c>Features</c> / <c>Keys</c> /
    /// <c>Values</c> lists (and its two per-feature scratch lists) from a read-only counting pass, so the
    /// streaming decode never grows them by doubling and discarding a chain of backing arrays. Tooth: after
    /// decode, every list's <c>Capacity</c> equals its <c>Count</c> exactly — no growth over-allocation.
    /// </summary>
    /// <remarks>
    /// RED without the pre-size: a <see cref="System.Collections.Generic.List{T}"/> grown by doubling lands on
    /// the next power of two ≥ Count, strictly greater whenever Count is not itself a reached power of two.
    /// The fixture-has-a-non-power-of-two guard keeps the tooth from passing vacuously on a layer whose count
    /// a doubling build would coincidentally land on.
    /// </remarks>
    [TestFixture]
    public class MvtDecodePresizeTests
    {
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();

        [Test]
        public void DecodedLayerLists_HaveCapacityEqualToCount_NoGrowthOverAllocation()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTileId, LoadFixture()));

            Assert.That(tile.Layers.Count, Is.GreaterThan(0), "fixture sanity: at least one layer decoded.");

            bool sawNonPowerOfTwo = false;
            foreach (MvtLayer layer in tile.Layers)
            {
                Assert.That(layer.Features.Capacity, Is.EqualTo(layer.Features.Count),
                    $"layer '{layer.Name}': Features must be pre-sized exactly (no doubling reallocation).");
                Assert.That(layer.Keys.Capacity, Is.EqualTo(layer.Keys.Count),
                    $"layer '{layer.Name}': Keys must be pre-sized exactly.");
                Assert.That(layer.Values.Capacity, Is.EqualTo(layer.Values.Count),
                    $"layer '{layer.Name}': Values must be pre-sized exactly.");

                // A 0-count layer is Capacity==Count==0 with or without the fix — not a discriminator — so it
                // must not satisfy the guard; require a non-empty layer whose count a doubling build overshoots.
                if (layer.Features.Count > 0 && !IsPowerOfTwo(layer.Features.Count)) sawNonPowerOfTwo = true;
            }

            // The Capacity==Count check only discriminates the fix when at least one count is not a power of
            // two (a doubling build would otherwise coincidentally land on Count). Fail loudly if the fixture
            // ever loses that property rather than let the tooth pass vacuously.
            Assert.That(sawNonPowerOfTwo, Is.True,
                "fixture must contain a layer whose feature count is not a power of two for this tooth to bite.");
        }

        private static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

        private static byte[] LoadFixture()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"sample-tile.bytes not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }
    }
}
