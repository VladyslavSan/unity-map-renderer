// Unity EditMode only. The zero-allocation teeth use UnityEngine.TestTools' Is.Not.AllocatingGCMemory() (the
// Recorder-based meter — the only one that actually detects GC.Alloc in this runner; note
// GC.GetAllocatedBytesForCurrentThread() is DEAD here, returning 0 for even a 10 MB allocation, so a
// thread-local byte delta would be vacuous). To avoid the one-shot-lambda false-positive it can throw on a
// microscopic path, the EXACT delegate the constraint measures is warmed (invoked 50x) before the assertion,
// so its compiled body is JIT'd and the measured invocation allocates nothing on its own. NOT registered in
// core-tests.csproj (MvtDecoder/MvtModels are not compiled there — see core-tests.csproj's tile-decode seam).

using System.Collections.Generic;
using System.IO;
using System;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Mvt
{
    /// <summary>
    /// D1a — dense MVT property storage (<see cref="MvtPropertyStorage.Dense"/>), the GC-eliminating
    /// alternative to the eager per-feature <see cref="MvtPropertyStorage.Dictionary"/> that stays the
    /// default. Two things this stage must prove:
    /// <list type="bullet">
    ///   <item><b>Equivalence</b> — Dense answers <see cref="IFeature.TryGetProperty"/> and
    ///     <see cref="IFeature.Properties"/> identically to Dictionary, for every layer, every feature,
    ///     every declared key, plus one key guaranteed absent.</item>
    ///   <item><b>Zero allocation</b> — the hot single-key <c>TryGetProperty</c> path allocates nothing on
    ///     Dense, for both a present and an absent key.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class DensePropertyStoreTests
    {
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();

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

        // ── Equivalence ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The discriminating oracle: for every layer, every feature, every key the layer declares (plus
        /// one key guaranteed absent from every feature), Dense and Dictionary must agree on both the
        /// presence bool and the resolved <see cref="Value"/>. A dense implementation with an index-math
        /// bug (wrong keyIdx/valIdx, wrong pair picked on a duplicate-key feature, off-by-one on the
        /// backward scan) fails this test; a store that always returns <c>false</c> is caught by the
        /// per-key presence assertion, not just a value comparison. RED-verified by swapping the
        /// keyIdx/valIdx read order inside <see cref="DensePropertyStore.TryGet"/>: every presence
        /// assertion for every real property failed (all reads land on the wrong slot) — restored before
        /// committing.
        /// </summary>
        [Test]
        public void Dense_And_Dictionary_TryGetProperty_AgreeForEveryKeyAndEveryFeature_AcrossAllLayers()
        {
            byte[] bytes = LoadFixture();
            MvtTile dictTile = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, bytes, MvtPropertyStorage.Dictionary));
            MvtTile denseTile = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, bytes, MvtPropertyStorage.Dense));

            Assert.That(denseTile.Layers.Count, Is.EqualTo(dictTile.Layers.Count),
                "precondition: both decodes of the same bytes must produce the same layer count");

            int comparisons = 0;
            for (int li = 0; li < dictTile.Layers.Count; li++)
            {
                MvtLayer dictLayer = dictTile.Layers[li];
                MvtLayer denseLayer = denseTile.Layers[li];
                Assert.That(denseLayer.Features.Count, Is.EqualTo(dictLayer.Features.Count),
                    $"layer '{dictLayer.Name}': both decodes must produce the same feature count");

                // Every key this layer declares, plus one name guaranteed absent from every feature.
                var namesToCheck = new List<string>(dictLayer.Keys) { "NoSuchKeyXYZ123" };

                for (int fi = 0; fi < dictLayer.Features.Count; fi++)
                {
                    IFeature dictFeature = dictLayer.Features[fi];
                    IFeature denseFeature = denseLayer.Features[fi];

                    foreach (string name in namesToCheck)
                    {
                        bool dictFound = dictFeature.TryGetProperty(name, out Value dictValue);
                        bool denseFound = denseFeature.TryGetProperty(name, out Value denseValue);
                        comparisons++;

                        Assert.That(denseFound, Is.EqualTo(dictFound),
                            $"layer '{dictLayer.Name}' feature[{fi}] key '{name}': " +
                            "TryGetProperty presence must agree between Dense and Dictionary");
                        if (dictFound)
                            Assert.That(denseValue, Is.EqualTo(dictValue),
                                $"layer '{dictLayer.Name}' feature[{fi}] key '{name}': " +
                                "TryGetProperty value must agree between Dense and Dictionary");
                    }

                    Assert.That(denseFeature.Properties.Count, Is.EqualTo(dictFeature.Properties.Count),
                        $"layer '{dictLayer.Name}' feature[{fi}]: Properties.Count must agree");
                }
            }

            Assert.That(comparisons, Is.GreaterThan(1000),
                "precondition: the fixture must exercise many (layer, feature, key) combinations — " +
                "too few and a shallow/broken Dense implementation could pass this test vacuously");
        }

        // ── Zero allocation ─────────────────────────────────────────────────────────────────────

        [Test]
        public void TryGetProperty_ExistingKey_OnDenseStore_AllocatesNoGCMemory()
        {
            MvtLayer layer = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture(), MvtPropertyStorage.Dense)).GetLayer("countries");
            IFeature feature = layer.Features[0]; // every countries feature has NAME (has NAME == 239)

            // Warm the EXACT delegate the constraint invokes (not just the method), so its compiled body is
            // JIT'd before measurement — the mitigation for Is.Not.AllocatingGCMemory's one-shot-lambda
            // false-positive. A real per-call allocation would still be caught (the Recorder sees every
            // GC.Alloc); this only removes the one-time JIT of the measured wrapper from the window.
            TestDelegate act = () => feature.TryGetProperty("NAME", out Value _);
            for (int w = 0; w < 50; w++) act();
            Assert.That(act, Is.Not.AllocatingGCMemory(),
                "Dense TryGetProperty on an existing key must not allocate: name→keyIndex is a Dictionary " +
                "lookup (int index, no boxing), the per-feature scan walks an already-decoded uint[], and " +
                "Value is a readonly struct.");
        }

        [Test]
        public void TryGetProperty_MissingKey_OnDenseStore_AllocatesNoGCMemory()
        {
            MvtLayer layer = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture(), MvtPropertyStorage.Dense)).GetLayer("countries");
            IFeature feature = layer.Features[0];

            TestDelegate act = () => feature.TryGetProperty("NoSuchKeyXYZ123", out Value _);
            for (int w = 0; w < 50; w++) act(); // warm the exact measured delegate (JIT its body)
            Assert.That(act, Is.Not.AllocatingGCMemory(),
                "the missing-key path (hit constantly by !has filters) must also not allocate: a failed " +
                "name→keyIndex lookup returns false without ever touching the per-feature tag array.");
        }
    }
}
