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
    /// D1a — dense MVT property storage (<see cref="DensePropertyStore"/>), the GC-eliminating alternative
    /// to an eager per-feature dictionary and the sole production property store. Two things this stage
    /// must prove:
    /// <list type="bullet">
    ///   <item><b>Equivalence</b> — <see cref="DensePropertyStore.TryGet"/> (the backward tag-pair scan
    ///     <see cref="IFeature.TryGetProperty"/> hits) agrees with
    ///     <see cref="MvtLayerPropertyResolver.ResolveToDictionary"/> (the forward walk
    ///     <see cref="IFeature.Properties"/> resolves through), for every layer, every feature, every
    ///     declared key, plus one key guaranteed absent. Two independent implementations of the same
    ///     resolve — not a comparison against a second store.</item>
    ///   <item><b>Zero allocation</b> — the hot single-key <c>TryGetProperty</c> path allocates nothing,
    ///     for both a present and an absent key.</item>
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
        /// one key guaranteed absent from every feature), <see cref="IFeature.TryGetProperty"/> (backward
        /// tag-pair scan, <see cref="DensePropertyStore.TryGetByKeyIndex"/>) must agree with
        /// <see cref="IFeature.Properties"/> (forward walk, <see cref="MvtLayerPropertyResolver.ResolveToDictionary"/>)
        /// on both the presence bool and the resolved <see cref="Value"/> — two independent
        /// implementations of the same resolve. An index-math bug in either (wrong keyIdx/valIdx, wrong
        /// pair picked on a duplicate-key feature, off-by-one) fails this test; a store that always returns
        /// <c>false</c> is caught by the per-key presence assertion, not just a value comparison.
        /// RED-verified by swapping the keyIdx/valIdx read order inside
        /// <see cref="DensePropertyStore.TryGetByKeyIndex"/>: every presence assertion for every real
        /// property failed (all reads land on the wrong slot) — restored before committing.
        /// </summary>
        [Test]
        public void TryGetProperty_AgreesWithResolveToDictionary_ForEveryKeyAndEveryFeature_AcrossAllLayers()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTileId, LoadFixture()));

            int comparisons = 0;
            foreach (MvtLayer layer in tile.Layers)
            {
                // Every key this layer declares, plus one name guaranteed absent from every feature.
                var namesToCheck = new List<string>(layer.Keys) { "NoSuchKeyXYZ123" };

                for (int fi = 0; fi < layer.Features.Count; fi++)
                {
                    IFeature feature = layer.Features[fi];
                    // The forward oracle: MvtLayerPropertyResolver.ResolveToDictionary via AsDictionary(),
                    // independent of the backward TryGet scan under test below.
                    IReadOnlyDictionary<string, Value> resolved = feature.Properties;

                    foreach (string name in namesToCheck)
                    {
                        bool resolvedFound = resolved.TryGetValue(name, out Value resolvedValue);
                        bool tryGetFound = feature.TryGetProperty(name, out Value tryGetValue);
                        comparisons++;

                        Assert.That(tryGetFound, Is.EqualTo(resolvedFound),
                            $"layer '{layer.Name}' feature[{fi}] key '{name}': " +
                            "TryGetProperty presence must agree with ResolveToDictionary");
                        if (resolvedFound)
                            Assert.That(tryGetValue, Is.EqualTo(resolvedValue),
                                $"layer '{layer.Name}' feature[{fi}] key '{name}': " +
                                "TryGetProperty value must agree with ResolveToDictionary");
                    }
                }
            }

            Assert.That(comparisons, Is.GreaterThan(1000),
                "precondition: the fixture must exercise many (layer, feature, key) combinations — " +
                "too few and a shallow/broken TryGet implementation could pass this test vacuously");
        }

        // ── Zero allocation ─────────────────────────────────────────────────────────────────────

        [Test]
        public void TryGetProperty_ExistingKey_OnDenseStore_AllocatesNoGCMemory()
        {
            MvtLayer layer = TestDecodedTiles.Track(
                MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");
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
                MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");
            IFeature feature = layer.Features[0];

            TestDelegate act = () => feature.TryGetProperty("NoSuchKeyXYZ123", out Value _);
            for (int w = 0; w < 50; w++) act(); // warm the exact measured delegate (JIT its body)
            Assert.That(act, Is.Not.AllocatingGCMemory(),
                "the missing-key path (hit constantly by !has filters) must also not allocate: a failed " +
                "name→keyIndex lookup returns false without ever touching the per-feature tag array.");
        }
    }
}
