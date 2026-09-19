// Engine-free: decodes fixtures via MvtFixtureStreams/MvtGeometry (TestSupport, ProtobufReader-only)
// and triangulates with the managed Earcut directly — no MeshCoverageValidator, so no MapRenderer.Jobs.Mvt
// dependency. Registered in Tools/core-tests/core-tests.csproj alongside those two TestSupport files.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Tiles;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// Acceptance teeth for UMR-106 Stage 2's bounding-box index over the ear-clip scan (see
    /// docs/mesh-triangulation-robustness-design.md §2.1/§6.1): the scan must visit a small,
    /// machine-independent number of candidates (T2.1), and the index arm must be byte-identical to
    /// the full-linear-scan fallback arm on the whole water corpus (T2.2).
    /// </summary>
    public class EarcutEarTestScanBoundTests
    {
        [TearDown]
        public void ResetTestHooks()
        {
            // Static test-only hooks on Earcut — never leak a forced-fallback into another test.
            Earcut.ForceLinearEarScan = false;
        }

        private static byte[] LoadFixture(string name)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", name);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException($"{name} not found walking up from {AppContext.BaseDirectory}");
        }

        /// <summary>Decode + assemble + triangulate every polygon feature of <paramref name="layerName"/>
        /// (or every layer, when null) via the managed Earcut — mirrors WaterTriangulationTests'
        /// decode/assemble shape without going through MeshCoverageValidator.</summary>
        private static List<Earcut.Result> TriangulatePolygons(byte[] mvt, string layerName)
        {
            var layers = layerName == null
                ? MvtFixtureStreams.ReadLayers(mvt)
                : new List<MvtFixtureStreams.Layer> { MvtFixtureStreams.ReadLayer(mvt, layerName) };

            var results = new List<Earcut.Result>();
            foreach (var layer in layers)
            {
                if (layer == null) continue;
                for (int fi = 0; fi < layer.Kinds.Count; fi++)
                {
                    if (layer.Kinds[fi] != TileGeometryType.Polygon) continue;
                    foreach (var poly in PolygonAssembler.Assemble(MvtGeometry.Decode(layer.Commands[fi])))
                        results.Add(Earcut.Triangulate(poly.Outer, poly.Holes));
                }
            }
            return results;
        }

        // ---- T2.1: a counted bound, not a clock ---------------------------------------------------

        [Test]
        public void Stockholm_EarTestScan_VisitsFewCandidates()
        {
            Earcut.CandidateVisitCount = 0;
            TriangulatePolygons(LoadFixture("water-real-stockholm-archipelago-9-282-150.pbf.bytes"), "water");

            // Surfaced on every run (pass or fail) so a drift toward the bound is visible in the
            // results before the day it reds, not discovered only in a failure message.
            TestContext.WriteLine($"Stockholm ear-test scan visited {Earcut.CandidateVisitCount} candidates.");

            // N = 25482 live vertices in Stockholm's merged ring, N^2 ~= 6.5e8; the un-indexed linear
            // scan visits ~1.2e9 total (measured, UMR-106). 5e6 sits far below both — ~200 visits per
            // IsEar call, ~150x the measured irreducible 33216, so it is not self-fulfilling and still
            // leaves headroom for the wide-AABB fallback. NOTE: the counter increments per item, not
            // per empty cell walked, so an empty-cell step is invisible to this bound — benign here
            // (~15 cells/call on Stockholm), but a design that grows empty-cell traffic would not red.
            Assert.Less(Earcut.CandidateVisitCount, 5_000_000,
                $"Stockholm's ear-test scan visited {Earcut.CandidateVisitCount} candidates — the grid " +
                "index should keep this far below N^2. A value near 1.2e9 means the un-indexed linear " +
                "scan ran instead of the grid (e.g. ForceLinearEarScan stuck on, or the index broken).");
        }

        // ---- T2.2: index arm == forced-linear-fallback arm, byte-identical -------------------------

        private static readonly string[] WaterCorpus =
        {
            "water-6-32-20.pbf.bytes",
            "water-8-135-80.pbf.bytes",
            "water-real-aegean-islands-8-145-99.pbf.bytes",
            "water-real-croatia-dalmatia-9-279-187.pbf.bytes",
            "water-real-indonesia-rajaampat-8-220-128.pbf.bytes",
            "water-real-norway-fjords-8-132-72.pbf.bytes",
            "water-real-philippines-palawan-8-212-120.pbf.bytes",
            "water-real-stockholm-archipelago-9-282-150.pbf.bytes",
        };

        [Test]
        public void IndexArmMatchesForcedLinearFallback_OnEveryWaterFixture()
        {
            foreach (string name in WaterCorpus)
                AssertArmsAgree(name, "water");
        }

        [Test]
        public void IndexArmMatchesForcedLinearFallback_OnSampleTile()
        {
            AssertArmsAgree("sample-tile.bytes", null);
        }

        /// <summary>Triangulates <paramref name="fixtureName"/> twice — once through the grid index,
        /// once with <see cref="Earcut.ForceLinearEarScan"/> forcing the full linear scan the grid
        /// normally prunes to — and asserts every polygon's vertices, indices and ForceClips agree.
        /// Drives the SAME production fallback path T2.1's RED recipe uses; never a re-implemented
        /// scan, so this stays a test of the branch that actually ships.</summary>
        private static void AssertArmsAgree(string fixtureName, string layerName)
        {
            byte[] mvt = LoadFixture(fixtureName);

            Earcut.ForceLinearEarScan = false;
            var indexed = TriangulatePolygons(mvt, layerName);

            Earcut.ForceLinearEarScan = true;
            List<Earcut.Result> linear;
            try { linear = TriangulatePolygons(mvt, layerName); }
            finally { Earcut.ForceLinearEarScan = false; }

            Assert.AreEqual(indexed.Count, linear.Count, $"{fixtureName}: polygon count differs between arms");
            for (int i = 0; i < indexed.Count; i++)
            {
                Assert.AreEqual(indexed[i].ForceClips, linear[i].ForceClips,
                    $"{fixtureName} poly {i}: ForceClips differs between the index and linear-fallback arms");
                CollectionAssert.AreEqual(indexed[i].Vertices, linear[i].Vertices,
                    $"{fixtureName} poly {i}: vertices differ between the index and linear-fallback arms");
                CollectionAssert.AreEqual(indexed[i].Indices, linear[i].Indices,
                    $"{fixtureName} poly {i}: indices differ between the index and linear-fallback arms");
            }
        }
    }
}
