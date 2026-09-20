// Unity EditMode only — drives EarcutJobGatherHarness (NativeArray, Burst jobs). NOT registered in
// core-tests.csproj (moved off the fast loop in A0 — see docs/mesh-triangulation-robustness-design.md).

using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// Acceptance teeth for UMR-106 Stage 2's bounding-box index over the ear-clip scan (see
    /// docs/mesh-triangulation-robustness-design.md §2.1/§6.1): the scan must visit a small,
    /// machine-independent number of candidates (T2.1), and the index arm must be byte-identical to
    /// the full-linear-scan fallback arm on the whole water corpus (T2.2). Re-homed onto
    /// <see cref="EarcutJobGatherHarness"/> in A0 — both arms now drive the real production dispatch
    /// chain (RingSelect → RingAssembly → gather → EarcutJob), not the managed twin.
    /// </summary>
    public class EarcutEarTestScanBoundTests
    {
        private static byte[] LoadFixture(string name)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", name);
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        // ---- T2.1: a counted bound, not a clock ---------------------------------------------------

        [Test]
        public void Stockholm_EarTestScan_VisitsFewCandidates()
        {
            var id = new TileId { Z = 9, X = 282, Y = 150 };
            var runs = EarcutJobGatherHarness.RunLayer(
                LoadFixture("water-real-stockholm-archipelago-9-282-150.pbf.bytes"), "water", id,
                forceLinearEarScan: false);

            long totalVisits = 0;
            foreach (var run in runs) totalVisits += run.CandidateVisits;

            // Surfaced on every run (pass or fail) so a drift toward the bound is visible in the
            // results before the day it reds, not discovered only in a failure message.
            TestContext.WriteLine($"Stockholm ear-test scan visited {totalVisits} candidates (Burst arm).");

            // Re-measured for the Burst arm in A0 (RingAssemblyJob + FillGatherJob upstream, not
            // PolygonAssembler + List.Sort): 884,455 candidates measured on this fixture. Bound set with
            // headroom comparable to the managed arm's own margin (5,000,000 against a measured 756,893,
            // ~6.6x) — 3,000,000 against 884,455 is ~3.4x, still far below the un-indexed linear-scan
            // order of magnitude. RED-verified (T-A0.2): flipping forceLinearEarScan to true on this same
            // fixture visits 1,538,896,078 candidates — ~1740x the indexed arm's 884,455, same order of
            // magnitude as the managed arm's own forced-linear measurement (1,201,497,972). Note the Burst
            // arm is not bit-identical to the managed one here — it visits ~17% more indexed (884,455 vs
            // 756,893) and ~28% more forced-linear (1,538,896,078 vs 1,201,497,972) — same conclusion,
            // same order, different implementation; not a regression signal if read again later.
            // NOTE: the counter increments per item, not per empty cell walked, so an empty-cell step is
            // invisible to this bound — benign here (few cells/call on Stockholm), but a design that grows
            // empty-cell traffic without growing item visits would not red.
            Assert.Less(totalVisits, 3_000_000,
                $"Stockholm's ear-test scan visited {totalVisits} candidates — the grid index should keep " +
                "this far below N^2. A value orders of magnitude higher means the un-indexed linear scan " +
                "ran instead of the grid (e.g. forceLinearEarScan stuck on, or the index broken).");
        }

        // ---- T2.2: index arm == forced-linear-fallback arm, byte-identical -------------------------

        private static readonly (string File, TileId Id)[] WaterCorpus =
        {
            ("water-6-32-20.pbf.bytes", new TileId { Z = 6, X = 32, Y = 20 }),
            ("water-8-135-80.pbf.bytes", new TileId { Z = 8, X = 135, Y = 80 }),
            ("water-real-aegean-islands-8-145-99.pbf.bytes", new TileId { Z = 8, X = 145, Y = 99 }),
            ("water-real-croatia-dalmatia-9-279-187.pbf.bytes", new TileId { Z = 9, X = 279, Y = 187 }),
            ("water-real-indonesia-rajaampat-8-220-128.pbf.bytes", new TileId { Z = 8, X = 220, Y = 128 }),
            ("water-real-norway-fjords-8-132-72.pbf.bytes", new TileId { Z = 8, X = 132, Y = 72 }),
            ("water-real-philippines-palawan-8-212-120.pbf.bytes", new TileId { Z = 8, X = 212, Y = 120 }),
            ("water-real-stockholm-archipelago-9-282-150.pbf.bytes", new TileId { Z = 9, X = 282, Y = 150 }),
        };

        [Test]
        public void IndexArmMatchesForcedLinearFallback_OnEveryWaterFixture()
        {
            foreach (var (file, id) in WaterCorpus)
                AssertArmsAgree(file, "water", id);
        }

        [Test]
        public void IndexArmMatchesForcedLinearFallback_OnSampleTile()
        {
            // All layers (RunLayer's layerName: null mode) — sample-tile's full-layer coverage.
            AssertArmsAgree("sample-tile.bytes", null, new TileId { Z = 0, X = 0, Y = 0 });
        }

        /// <summary>Triangulates <paramref name="fixtureName"/> twice through
        /// <see cref="EarcutJobGatherHarness.RunLayer"/> — once through the grid index, once with
        /// <c>forceLinearEarScan</c> forcing the full linear scan the grid normally prunes to — and
        /// asserts every polygon's used-prefix vertices, indices and ForceClips agree. Drives the SAME
        /// production dispatch chain both times (only <see cref="MapRenderer.Jobs.Fill.EarcutJob"/>'s
        /// internal scan strategy differs), so polygon order and count are identical between arms and a
        /// by-index pairing is sound.</summary>
        private static void AssertArmsAgree(string fixtureName, string layerName, TileId id)
        {
            byte[] mvt = LoadFixture(fixtureName);

            var indexed = EarcutJobGatherHarness.RunLayer(mvt, layerName, id, forceLinearEarScan: false);
            var linear  = EarcutJobGatherHarness.RunLayer(mvt, layerName, id, forceLinearEarScan: true);

            Assert.AreEqual(indexed.Count, linear.Count, $"{fixtureName}: polygon count differs between arms");
            for (int i = 0; i < indexed.Count; i++)
            {
                Assert.AreEqual(indexed[i].ForceClips, linear[i].ForceClips,
                    $"{fixtureName} poly {i}: ForceClips differs between the index and linear-fallback arms");
                CollectionAssert.AreEqual(indexed[i].Vertices, linear[i].Vertices,
                    $"{fixtureName} poly {i}: vertices (used prefix) differ between the index and linear-fallback arms");
                CollectionAssert.AreEqual(indexed[i].Indices, linear[i].Indices,
                    $"{fixtureName} poly {i}: indices (used prefix) differ between the index and linear-fallback arms");
            }
        }
    }
}
