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
// GATE-MEASURED (this exact tooth — 20-iteration warmed loop, Gen0-guarded — on sample-tile.bytes, over the
// sole production storage, Dense; re-measured when the Dictionary A/B toggle was deleted, so the figures
// below post-date the tag-flatten stage's own residual reduction and are lower than this file's earlier
// 105,267 B green / 406–418 KB Dictionary-storage figures):
//   - green:                                       36,864 B/tile.
//   - per-feature uint[] reinstated (RED-verify):  362,086 B/tile.
// The managed geometry-array elimination therefore removed the single biggest decode allocation site,
// MEASURED not estimated. The delta sits far clear of GC.GetTotalMemory's ~100 KB noise floor, so a byte
// ceiling reliably discriminates fix from regression.
//
// RE-MEASURED for the value-table native-intern stage (MvtLayer.Values: List<MvtValue> ->
// NativeArray<MvtValueNative> + string[] side table). The transient List<MvtValueNative>/List<string>
// scratch DecodeLayer now builds (materialized into the native array inside the existing try/finally, see
// MvtDecoder.DecodeLayer) is new per-decode ALLOCATION traffic — retention still drops (the native array
// replaces the retained List<MvtValue>), but the green figure moved:
//   - green:                                       ~49,200 B/tile (49,152 / 49,356 across repeated runs).
//   - per-feature uint[] reinstated (RED-verify):  356,966 B/tile.
// Both re-measured on this tree, 20-iteration warmed loop, Gen0-guarded, per this file's own recipe.

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

        // Ceiling between the RE-MEASURED green (~49,200 B/tile) and the per-feature-uint[] RED-verify
        // (356,966 B/tile, see the file header): ~101 KB above green, ~207 KB below red — both clear the
        // ~100 KB meter noise floor and the observed run-to-run variance (49,152 B / 49,356 B across
        // repeated green runs on this tree).
        private const long Ceiling = 150_000;

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
        /// iterations still costs real process memory, so each iteration must clean up after itself.</summary>
        private static long BytesPerDecode(byte[] bytes, int iterations)
        {
            // Warm-up: JIT compilation and any one-shot first-touch allocation must not land in the window.
            for (int w = 0; w < 3; w++)
            {
                var warm = MvtDecoder.Decode(FixtureTileId, bytes);
                warm.Dispose();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int collectionsBefore = GC.CollectionCount(0);
            long before = GC.GetTotalMemory(false);

            for (int i = 0; i < iterations; i++)
            {
                var tile = MvtDecoder.Decode(FixtureTileId, bytes);
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
        /// biggest decode allocation site (see the file header for the green/RED-verify figures).
        /// RED-verify by reinstating an equivalent per-feature uint[] copy of the flattened commands inside
        /// MvtDecoder.DecodeLayer, before the materializer runs.
        /// </summary>
        [Test]
        public void Decode_SampleTile_AllocatesUnderCeilingPerTile()
        {
            byte[] bytes = LoadFixture();

            long bytesPerDecode = BytesPerDecode(bytes, iterations: 20);
            TestContext.WriteLine($"MEASURE bytesPerDecode={bytesPerDecode}");

            Assert.LessOrEqual(bytesPerDecode, Ceiling,
                $"MvtDecoder.Decode allocated {bytesPerDecode} B/tile on sample-tile.bytes — must stay under " +
                $"the {Ceiling} B ceiling. 2a removes the per-feature geometry uint[] (the largest single " +
                "site); the residual is tags/Value-table/husks/strings, left to a later retention-pooling stage.");
        }
    }
}
