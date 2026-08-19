// Unity EditMode only. GC.GetAllocatedBytesForCurrentThread() reads a dead ZERO in this Mono runner (see
// FillMeshPipelineAllocationTests / DensePropertyStoreTests) — it cannot discriminate a multi-hundred-KB
// allocation here. GC.GetTotalMemory IS a live meter at this scale; the calibration canary below proves it is
// alive in THIS run before the decode tooth trusts it. NOT registered in core-tests.csproj (MvtDecoder pulls
// Unity.Collections/Unity.Jobs/Burst — see core-tests.csproj's tile-decode seam).
//
// perf/gc-elimination 2a: MvtDecoder used to allocate one managed uint[] per feature for its geometry
// command stream, plus a List<uint[]> copy inside MvtGeometryMaterializer.Materialize (E3) and the decoder's
// own geometryList. All three are gone: MvtDecoder.DecodeLayer now flattens every feature's geometry command
// words directly off the wire into ONE shared Allocator.Persistent NativeArray<uint> (invisible to the GC),
// and MvtGeometryMaterializer's constructor takes that native shape as-is. No per-feature uint[] geometry
// array exists anywhere in the decode path any more.
//
// GATE-MEASURED (this exact tooth — 20-iteration warmed loop, Gen0-guarded — on sample-tile.bytes):
//   - after the flatten (GREEN):        ~406,000 B/tile (two runs: 405,913 and 408,371; a later branch read
//     418,406 — run-to-run spread ~12 KB, wider than the ~2.5 KB first seen).
//   - per-feature uint[] reinstated (RED): 696,524 B/tile.
//   - production DENSE storage path:     105,267 B/tile — pinned by
//     Decode_SampleTile_DenseStorage_AllocatesFarUnderDictionary (250 KB ceiling; its RED side IS the
//     ~406–418 KB Dictionary figure above). IMPORTANT: the ~406/697 numbers here are the DICTIONARY A/B
//     oracle (the no-arg Decode default), NOT what production runs — production defaults to Dense (105 KB).
// The managed geometry-array elimination therefore saves ~291 KB/tile (~42% of the decode's managed heap) —
// the single biggest decode allocation site, MEASURED not estimated. (An earlier recon ESTIMATE put the
// baseline at ~489 KB and the geometry share at ~318 KB / ~65%; that estimate was imprecise — the two
// numbers above are the authoritative gate measurements, and the RED figure is the faithful "old" baseline
// because it recreates the exact managed condition the pre-2a code held.) The ~406 KB residual is
// tags/Value-table/husks/strings, left to a later retention-pooling stage. The 291 KB delta sits far clear
// of GC.GetTotalMemory's ~100 KB noise floor, so a byte ceiling reliably discriminates fix from regression.

using System;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Mvt
{
    [TestFixture]
    public class DecodeGeometryFlattenAllocTests
    {
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        // GC.GetTotalMemory's own noise floor (brief: only trustworthy at >= ~100 KB/op) — the calibration
        // canary must clear this by a wide margin to prove the meter is alive.
        private const long CalibrationFloor = 100_000;

        // Ceiling at the MEASURED midpoint of green (~406 KB) and red-verify (~697 KB): ~144 KB above green,
        // ~147 KB below red — each margin comfortably clears the ~100 KB meter noise floor AND the observed
        // ~2.5 KB run-to-run variance, so this neither false-reds a healthy run nor false-greens a partial
        // fix that still mints the per-feature uint[] anywhere in Decode. See the file header for both runs.
        private const long Ceiling = 550_000;

        // Ceiling at the MEASURED midpoint of Dense green (105,267 B/tile) and a Dictionary RED-verify on the
        // SAME tooth (418,406 B/tile on-branch — Dense's RED-verify is "force Dictionary", which is exactly
        // the Dictionary tooth's own path): ~145 KB above green, ~168 KB below red — both clear the ~100 KB
        // meter noise floor and the observed run-to-run variance. See the file header's GATE-MEASURED block.
        private const long DenseCeiling = 250_000;

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

        /// <summary>Proves GC.GetTotalMemory is a LIVE meter in this run before the decode tooth below trusts
        /// it — GC.GetAllocatedBytesForCurrentThread is dead in this same Mono runner, and a silently-dead
        /// meter would make the assertion below vacuous.</summary>
        [Test]
        public void Calibration_GetTotalMemory_ReadsALiveAllocation()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetTotalMemory(false);
            byte[] block = new byte[8 << 20];
            block[0] = 1; // defeat dead-store elimination
            long after = GC.GetTotalMemory(false);

            Assert.Greater(after - before, CalibrationFloor,
                "GC.GetTotalMemory must read a live 8 MiB allocation well clear of its own noise floor, or " +
                "the meter is dead in this run and the tooth below cannot be trusted.");
            GC.KeepAlive(block);
        }

        /// <summary>Bytes/decode over a warmed loop, guarded against a Gen0 collection firing inside the
        /// measurement window. The decoded tile is disposed INSIDE the loop: its layers mint
        /// Allocator.Persistent native buffers that GC.GetTotalMemory cannot see, but leaking them across N
        /// iterations still costs real process memory, so each iteration must clean up after itself.
        /// <paramref name="storage"/> selects the <see cref="IMvtPropertyStore"/> under measurement — the
        /// warm-up loop uses the SAME storage as the measured loop, or the other store's JIT/first-touch
        /// allocation lands inside the measured window and inflates the reading.</summary>
        private static long BytesPerDecode(
            byte[] bytes, int iterations, MvtPropertyStorage storage = MvtPropertyStorage.Dictionary)
        {
            // Warm-up: JIT compilation and any one-shot first-touch allocation must not land in the window.
            for (int w = 0; w < 3; w++)
            {
                var warm = MvtDecoder.Decode(FixtureTileId, bytes, storage);
                warm.Dispose();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int collectionsBefore = GC.CollectionCount(0);
            long before = GC.GetTotalMemory(false);

            for (int i = 0; i < iterations; i++)
            {
                var tile = MvtDecoder.Decode(FixtureTileId, bytes, storage);
                tile.Dispose();
            }

            long after = GC.GetTotalMemory(false);
            int collectionsAfter = GC.CollectionCount(0);

            Assert.AreEqual(collectionsBefore, collectionsAfter,
                "a Gen0 collection fired inside the measurement window — the byte delta is unreliable here; " +
                "this indicates a flaky run, not a decode result.");

            return (after - before) / iterations;
        }

        /// <summary>
        /// The flatten's headline tooth: MvtDecoder.Decode's per-feature geometry uint[] was the single
        /// biggest decode allocation site — gate-measured ~291 KB/tile (~406 KB green vs 696,524 B red-verify;
        /// see the file header). RED-verify by reinstating an equivalent per-feature uint[] copy of the
        /// flattened commands inside MvtDecoder.DecodeLayer, before the materializer runs — this lifts the
        /// per-tile figure to ~697 KB, well over the ceiling.
        /// </summary>
        [Test]
        public void Decode_SampleTile_AllocatesUnderCeilingPerTile()
        {
            byte[] bytes = LoadFixture();

            long bytesPerDecode = BytesPerDecode(bytes, iterations: 20, storage: MvtPropertyStorage.Dictionary);
            TestContext.WriteLine($"MEASURE Dictionary bytesPerDecode={bytesPerDecode}");

            Assert.LessOrEqual(bytesPerDecode, Ceiling,
                $"MvtDecoder.Decode allocated {bytesPerDecode} B/tile on sample-tile.bytes — must stay under " +
                $"the {Ceiling} B ceiling. 2a removes the per-feature geometry uint[] (the largest single " +
                "site); the residual is tags/Value-table/husks/strings, left to a later retention-pooling stage.");
        }

        /// <summary>
        /// Locks in the already-landed Dense storage win (production default since <c>MapViewConfig.cs</c>'s
        /// <c>PropertyStorage</c> initializer — see <see cref="MapRenderer.Unity.Rendering.Map.MapViewConfig"/>):
        /// Dense keeps MVT's dense (keyIdx,valIdx) tag pairs instead of expanding each feature into a
        /// <c>Dictionary&lt;string,Value&gt;</c>, eliminating that per-feature allocation. This tooth measures
        /// the decode path directly with <see cref="MvtPropertyStorage.Dense"/>; it does NOT exercise the
        /// production wiring that selects Dense — see <c>ProductionConfig_DefaultsToDensePropertyStorage</c>
        /// in <c>ProductionPropertyStorageDefaultTests</c> for the tooth that guards the wiring end-to-end.
        /// </summary>
        [Test]
        public void Decode_SampleTile_DenseStorage_AllocatesFarUnderDictionary()
        {
            byte[] bytes = LoadFixture();

            long bytesPerDecode = BytesPerDecode(bytes, iterations: 20, storage: MvtPropertyStorage.Dense);
            TestContext.WriteLine($"MEASURE Dense bytesPerDecode={bytesPerDecode}");

            Assert.LessOrEqual(bytesPerDecode, DenseCeiling,
                $"MvtDecoder.Decode with Dense property storage allocated {bytesPerDecode} B/tile on " +
                $"sample-tile.bytes — must stay under the {DenseCeiling} B ceiling. Dense drops the " +
                "per-feature Dictionary<string,Value> that the Dictionary-path tooth above still measures; " +
                "regressing back toward that figure means the Dense store started allocating per-feature " +
                "managed state again.");
        }
    }
}
