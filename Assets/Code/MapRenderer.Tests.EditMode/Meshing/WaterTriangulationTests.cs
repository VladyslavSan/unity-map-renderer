// Unity EditMode only. It exercises MapRenderer.Jobs.Mvt (the tile-decode seam, moved out of Core),
// which Tools/core-tests does not compile — this file is not registered there.

using System;
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// Mesh-triangulation testbench over real OpenFreeMap water tiles (many-holed polygons — the case that
    /// breaks the hand-rolled ear-clipper). See docs/mesh-triangulation-robustness-design.md.
    ///
    /// Always-on (green): the validator is correct on a clean synthetic polygon, and the corpus tiles decode,
    /// assemble, and triangulate their OUTER rings cleanly — so any regression in decode/assemble/outer is
    /// caught, and the localisation "only hole handling is broken" is pinned.
    ///
    /// Corpus_Water_*_TriangulatesFaithfully: the full water layer of each corpus tile must triangulate
    /// faithfully — the acceptance teeth for the stage-2 hole-handling fix (the cure → split → mirror-retry
    /// cascade in Earcut.cs). Always-on regression guards now that the fix has landed.
    /// </summary>
    public class WaterTriangulationTests
    {
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

        private static readonly string[] Corpus = { "water-8-135-80.pbf.bytes", "water-6-32-20.pbf.bytes" };

        // ---- always-on guards -----------------------------------------------------------------------

        [Test]
        public void Validator_CleanSquareWithHole_IsPerfect()
        {
            var outer = new List<double2>
            {
                new double2(0, 0), new double2(1000, 0), new double2(1000, 1000), new double2(0, 1000),
            };
            var hole = new List<double2>
            {
                new double2(400, 400), new double2(400, 600), new double2(600, 600), new double2(600, 400),
            };
            var poly = new Polygon(outer);
            poly.Holes.Add(hole);

            var rep = MeshCoverageValidator.Validate(new[] { poly }, extent: 1000);
            Assert.AreEqual(0, rep.ForceClips, "clean square+hole must not force-clip");
            Assert.AreEqual(0, rep.WindingFlips, "no folded triangles");
            Assert.Less(rep.AreaRelError, 0.001, "area = outer − hole");
            Assert.Less(rep.MismatchPct, 0.5, "coverage matches source; the hole is subtracted");
        }

        [Test]
        public void Corpus_DecodesAssemblesAndOuterRingsTriangulateClean()
        {
            foreach (string name in Corpus)
            {
                // IR C1 P3: the command streams come from the test-side fixture reader, not from a decoded
                // MvtFeature — a decoded feature carries no geometry now, and reading production's buffer
                // instead would make this oracle audit itself.
                var layer = MvtFixtureStreams.ReadLayer(LoadFixture(name), "water");
                Assert.IsNotNull(layer, $"{name}: water layer present");
                int polys = 0;
                for (int fi = 0; fi < layer.Kinds.Count; fi++)
                {
                    if (layer.Kinds[fi] != TileGeometryType.Polygon) continue;
                    foreach (var poly in PolygonAssembler.Assemble(MvtGeometry.Decode(layer.Commands[fi])))
                    {
                        polys++;
                        var res = Earcut.Triangulate(poly.Outer, new List<List<double2>>()); // outer alone
                        Assert.AreEqual(0, res.ForceClips, $"{name}: outer ring should ear-clip cleanly");
                        double outerA = SignedArea.AbsArea(poly.Outer);
                        if (outerA > 1.0)
                        {
                            double tri = 0;
                            for (int i = 0; i + 2 < res.Indices.Length; i += 3)
                            {
                                var a = res.Vertices[res.Indices[i]]; var b = res.Vertices[res.Indices[i + 1]]; var c = res.Vertices[res.Indices[i + 2]];
                                tri += math.abs(0.5 * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)));
                            }
                            Assert.Less(math.abs(tri - outerA) / outerA, 0.005, $"{name}: outer-alone area conserved");
                        }
                    }
                }
                Assert.Greater(polys, 0, $"{name}: has water polygons");
            }
        }

        // ---- acceptance teeth for the hole-handling fix (stage 2) ------------------------------------

        [Test]
        public void Corpus_Water_8_135_80_TriangulatesFaithfully()
        {
            var rep = MeshCoverageValidator.ValidateTileLayer(LoadFixture("water-8-135-80.pbf.bytes"), "water");
            Assert.IsTrue(rep.Passes(areaEps: 0.01, mismatchEps: 1.0),
                "water z8/135/80 triangulation is broken: " + rep.Summary + "\n" + rep.AsciiMap);
        }

        [Test]
        public void Corpus_Water_6_32_20_TriangulatesFaithfully()
        {
            var rep = MeshCoverageValidator.ValidateTileLayer(LoadFixture("water-6-32-20.pbf.bytes"), "water");
            Assert.IsTrue(rep.Passes(areaEps: 0.01, mismatchEps: 1.0),
                "water z6/32/20 triangulation is broken: " + rep.Summary + "\n" + rep.AsciiMap);
        }

        // ---- real coastline-dense tiles (fjords / archipelagos) — the case the fix targets --------------
        // These are real OpenFreeMap water tiles chosen for pathological hole counts. The 4 CLEAN ones
        // triangulate perfectly (the strict bar); the 2 HARD ones exercise the design's graceful-
        // degradation path (§3.1) — a bounded, SURFACED clean drop, never a fold.

        // CLEAN real tiles — strict bar: ForceClips==0, WindingFlips==0, area+coverage within 1%.
        private static readonly string[] CleanRealCorpus =
        {
            "water-real-aegean-islands-8-145-99.pbf.bytes",
            "water-real-norway-fjords-8-132-72.pbf.bytes",
            "water-real-philippines-palawan-8-212-120.pbf.bytes",
            "water-real-stockholm-archipelago-9-282-150.pbf.bytes",
        };

        [Test]
        public void Corpus_RealCleanTiles_TriangulateFaithfully()
        {
            foreach (string name in CleanRealCorpus)
            {
                var rep = MeshCoverageValidator.ValidateTileLayer(LoadFixture(name), "water");
                Assert.IsTrue(rep.Passes(areaEps: 0.01, mismatchEps: 1.0),
                    $"{name} water triangulation is broken: {rep.Summary}\n{rep.AsciiMap}");
            }
        }

        // HARD real tiles — graceful-degradation bar (design §3.1): the critical invariant is
        // WindingFlips==0 (NO fold/inversion — the visible-corruption failure mode is impossible), with
        // area conserved to <1% and AT MOST ONE bounded, surfaced clean drop (ForceClips <= 1). A clean
        // drop is a VISIBLE signal (it is counted), never silent garbage. This documents that the fix
        // degrades correctly on the hardest real input rather than folding.
        private static readonly string[] HardRealCorpus =
        {
            "water-real-croatia-dalmatia-9-279-187.pbf.bytes",
            "water-real-indonesia-rajaampat-8-220-128.pbf.bytes",
        };

        [Test]
        public void Corpus_RealHardTiles_DegradeGracefullyNeverFold()
        {
            foreach (string name in HardRealCorpus)
            {
                var rep = MeshCoverageValidator.ValidateTileLayer(LoadFixture(name), "water");
                Assert.AreEqual(0, rep.WindingFlips, $"{name}: NO folds/inversions allowed — {rep.Summary}");
                Assert.Less(rep.AreaRelError, 0.01, $"{name}: area conserved within 1% — {rep.Summary}");
                Assert.LessOrEqual(rep.ForceClips, 1,
                    $"{name}: at most one bounded, surfaced clean drop — {rep.Summary}");
            }
        }

        // Permanent unit tooth — the reversed-concave ring flagged in review (a simple concave quad).
        // The invariant: no fold (WindingFlips==0) and area conserved. NB: fed through Triangulate the
        // outer is winding-normalised first, so this specific ring never actually reproduced the reversed-
        // residual overlap on either the pre- or post-fix code; it is kept as a permanent regression guard
        // for the concave-triangulation-with-no-fold contract.
        [Test]
        public void Unit_ReversedConcaveQuad_NoFold_AreaConserved()
        {
            var outer = new List<double2>
            {
                new double2(0, 0), new double2(4, 0), new double2(1, 1), new double2(0, 4),
            };
            var rep = MeshCoverageValidator.Validate(new[] { new Polygon(outer) }, extent: 4);
            Assert.AreEqual(0, rep.WindingFlips, "reversed-concave quad must not fold: " + rep.Summary);
            Assert.Less(rep.AreaRelError, 0.01, "reversed-concave quad area must be conserved: " + rep.Summary);
        }
    }
}
