// Unity EditMode only. It exercises MapRenderer.Jobs.Mvt (the tile-decode seam, moved out of Core),
// which Tools/core-tests does not compile — this file is not registered there.

using System;
using System.IO;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// Mesh-triangulation testbench over real OpenFreeMap water tiles (many-holed polygons — the case that
    /// breaks a hand-rolled ear-clipper). See docs/mesh-triangulation-robustness-design.md.
    ///
    /// A0 (Core oracle cleanup) re-homed this corpus onto the REAL jobified fill path —
    /// <see cref="FillMeshGraph.Schedule"/>, the Burst <see cref="EarcutJob"/> — the same production
    /// dispatch <c>JobifiedWaterTriangulationTests</c> already proves for water-8-135-80 (kept there,
    /// not duplicated here; together the two files cover all 8 corpus tiles on the Burst arm, none on
    /// the retired managed twin). <b>Band setting: <c>SuppressBoundaryBand</c> is left unset (band ON)</b>
    /// here — the boundary band's outer vertices share their inner twin's coordinate (see
    /// <c>FillMeshGraph</c>'s own comment), so every band triangle has near-zero area, sign 0, and is
    /// ignored by <see cref="MeshCoverageValidator.ValidateTriangulation"/>'s winding majority and raster
    /// coverage — this is the only place in A0 where band-on is correct (§6.6 needs it off).
    ///
    /// Always-on (green): the validator is correct on a clean synthetic polygon, and the corpus tiles decode,
    /// assemble, and triangulate their OUTER rings cleanly — so any regression in decode/assemble/outer is
    /// caught, and the localisation "only hole handling is broken" is pinned.
    ///
    /// Corpus_Water_*_TriangulatesFaithfully: the full water layer of each corpus tile must triangulate
    /// faithfully — the acceptance teeth for the stage-2 hole-handling fix (the cure → split → mirror-retry
    /// cascade <see cref="EarcutJob"/> carries forward). Always-on regression guards now that the fix has
    /// landed.
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

        /// <summary>Converts an <see cref="EarcutJobPolygonRunner.Result"/> into the flat triangle list
        /// <see cref="MeshCoverageValidator.ValidateTriangulation"/> takes.</summary>
        private static List<(double2 a, double2 b, double2 c)> ToTriangles(EarcutJobPolygonRunner.Result res)
        {
            var tris = new List<(double2, double2, double2)>(res.Indices.Length / 3);
            for (int i = 0; i + 2 < res.Indices.Length; i += 3)
                tris.Add((res.Vertices[res.Indices[i]], res.Vertices[res.Indices[i + 1]], res.Vertices[res.Indices[i + 2]]));
            return tris;
        }

        /// <summary>Drives the REAL jobified fill path over one fixture/layer, band ON (see class doc), and
        /// validates the output against the ground-truth polygons the fixture's own command streams decode
        /// to. Mirrors <c>JobifiedWaterTriangulationTests</c>' body — the template this sweep applies to the
        /// remaining 7 corpus tiles.</summary>
        private static MeshCoverageValidator.Report RunOnBurstArm(string fixtureName, TileId tileId, string layerName)
        {
            byte[] mvtBytes = LoadFixture(fixtureName);
            using var mvtTile = MvtDecoder.Decode(tileId, mvtBytes);
            var layer = mvtTile.GetLayer(layerName);
            Assert.IsNotNull(layer, $"{fixtureName}: {layerName} layer present");

            var oracle = MvtFixtureStreams.ReadLayer(mvtBytes, layerName);
            var groundTruthPolys = new List<Polygon>();
            for (int fi = 0; fi < oracle.Kinds.Count; fi++)
            {
                if (oracle.Kinds[fi] != TileGeometryType.Polygon || oracle.Commands[fi] == null) continue;
                groundTruthPolys.AddRange(PolygonAssembler.Assemble(MvtGeometry.Decode(oracle.Commands[fi])));
            }
            Assert.Greater(groundTruthPolys.Count, 0, $"{fixtureName}: {layerName} layer has polygons");

            double extent = layer.Extent;
            var (bMin, _) = tileId.MercatorBounds();

            TileGeometryBuffers geometry = layer.Geometry; // BORROWED (IR C1 P3)
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var pipelineInput = new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = visitOrder,
                OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                Projection     = new WebMercatorProjection(),
            };

            FillGraphOutput buffers = FillMeshGraph.Schedule(pipelineInput);
            buffers.Handle.Complete();
            try
            {
                Assert.IsTrue(buffers.IsCreated, $"{fixtureName}: jobified pipeline produced no buffers");

                int indexCount = buffers.TriangleIndices.Length;
                var tris = new List<(double2 a, double2 b, double2 c)>(indexCount / 3);
                for (int i = 0; i + 2 < indexCount; i += 3)
                {
                    double2 a = buffers.TileVertices[buffers.TriangleIndices[i]];
                    double2 b = buffers.TileVertices[buffers.TriangleIndices[i + 1]];
                    double2 c = buffers.TileVertices[buffers.TriangleIndices[i + 2]];
                    tris.Add((a, b, c));
                }

                return MeshCoverageValidator.ValidateTriangulation(
                    groundTruthPolys, tris, buffers.Counts[0].ForceClipCount, (int)extent);
            }
            finally
            {
                buffers.Dispose();
                visitOrder.Dispose();
            }
        }

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

            var res = EarcutJobPolygonRunner.Run(outer, hole);
            var groundTruth = new[] { new Polygon(outer) };
            groundTruth[0].Holes.Add(hole);

            var rep = MeshCoverageValidator.ValidateTriangulation(groundTruth, ToTriangles(res), res.ForceClips, extent: 1000);
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
                        var res = EarcutJobPolygonRunner.Run(poly.Outer); // outer alone
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

        // ---- acceptance teeth for the hole-handling fix (stage 2/3) — Burst arm ----------------------
        // water-8-135-80 is proven by JobifiedWaterTriangulationTests (not duplicated here).

        [Test]
        public void Corpus_Water_6_32_20_TriangulatesFaithfully()
        {
            // water-6-32-20's poly 0 (outer=879, 13 holes) hits ONE locus the cure -> split cascade cannot
            // resolve, and drops it cleanly. Measured at the drop site (UMR-106, managed arm): 5 live
            // vertices, residual signed area +33.5 tile-space units^2 in a 4096^2 tile — sub-pixel at z6,
            // ~13x below MeshCoverageValidator's raster resolution, so ForceClips is the only instrument
            // that observes it (that argument survives the arm change unchanged). Re-measured for the
            // Burst arm in A0: ForceClips=1, byte-identical to the managed pin — the two arms agree on
            // this tile's cascade. The count is pinned EXACTLY, not bounded, to keep that sentinel armed.
            var rep = RunOnBurstArm("water-6-32-20.pbf.bytes", new TileId { Z = 6, X = 32, Y = 20 }, "water");
            TestContext.WriteLine($"water-6-32-20 (Burst arm): {rep.Summary}");
            Assert.AreEqual(0, rep.WindingFlips, "water z6/32/20: NO folds/inversions allowed - " + rep.Summary);
            Assert.LessOrEqual(rep.AreaRelError, 0.01, "water z6/32/20: area conserved within 1% - " + rep.Summary);
            Assert.LessOrEqual(rep.MismatchPct, 1.0, "water z6/32/20: coverage matches the source - " + rep.Summary);
            Assert.AreEqual(1, rep.ForceClips,
                "water z6/32/20's clean-drop count (Burst arm) moved off its pinned value. " +
                "Reading 0 means the locus was RESOLVED - that is an IMPROVEMENT, not a regression: re-pin this " +
                "to 0 and delete the justification comment above. A higher count means a NEW drop appeared and " +
                "must be investigated before this pin is touched. Either way, do not widen this to an inequality - " +
                "ForceClips is the only instrument in this suite that can see a sub-cell drop. " + rep.Summary);
        }

        // ---- real coastline-dense tiles (fjords / archipelagos) — the case the fix targets --------------
        // These are real OpenFreeMap water tiles chosen for pathological hole counts. The 4 CLEAN ones
        // triangulate perfectly (the strict bar); the 2 HARD ones exercise the design's graceful-
        // degradation path (§3.1) — a bounded, SURFACED clean drop, never a fold.

        // CLEAN real tiles — strict bar: ForceClips==0, WindingFlips==0, area+coverage within 1%.
        private static readonly (string File, TileId Id)[] CleanRealCorpus =
        {
            ("water-real-aegean-islands-8-145-99.pbf.bytes", new TileId { Z = 8, X = 145, Y = 99 }),
            ("water-real-norway-fjords-8-132-72.pbf.bytes", new TileId { Z = 8, X = 132, Y = 72 }),
            ("water-real-philippines-palawan-8-212-120.pbf.bytes", new TileId { Z = 8, X = 212, Y = 120 }),
            ("water-real-stockholm-archipelago-9-282-150.pbf.bytes", new TileId { Z = 9, X = 282, Y = 150 }),
        };

        [Test]
        public void Corpus_RealCleanTiles_TriangulateFaithfully()
        {
            foreach (var (name, id) in CleanRealCorpus)
            {
                var rep = RunOnBurstArm(name, id, "water");
                TestContext.WriteLine($"{name} (Burst arm): {rep.Summary}");
                Assert.IsTrue(rep.Passes(areaEps: 0.01, mismatchEps: 1.0),
                    $"{name} water triangulation is broken: {rep.Summary}\n{rep.AsciiMap}");
            }
        }

        // HARD real tiles — graceful-degradation bar (design §3.1): the critical invariant is
        // WindingFlips==0 (NO fold/inversion — the visible-corruption failure mode is impossible), with
        // area conserved to <1% and a bounded, surfaced clean-drop count. Measured for the Burst arm in
        // A0: croatia-dalmatia ForceClips=1, indonesia-rajaampat ForceClips=1 — both at the bound, same
        // as the managed arm's own `<=1` allowance (the design's own tolerance, not a placeholder copied
        // from a tighter managed value). A clean drop is a VISIBLE signal (it is counted), never silent
        // garbage. This documents that the fix degrades correctly on the hardest real input rather than
        // folding.
        private static readonly (string File, TileId Id)[] HardRealCorpus =
        {
            ("water-real-croatia-dalmatia-9-279-187.pbf.bytes", new TileId { Z = 9, X = 279, Y = 187 }),
            ("water-real-indonesia-rajaampat-8-220-128.pbf.bytes", new TileId { Z = 8, X = 220, Y = 128 }),
        };

        [Test]
        public void Corpus_RealHardTiles_DegradeGracefullyNeverFold()
        {
            foreach (var (name, id) in HardRealCorpus)
            {
                var rep = RunOnBurstArm(name, id, "water");
                TestContext.WriteLine($"{name} (Burst arm): {rep.Summary}");
                Assert.AreEqual(0, rep.WindingFlips, $"{name}: NO folds/inversions allowed — {rep.Summary}");
                Assert.Less(rep.AreaRelError, 0.01, $"{name}: area conserved within 1% — {rep.Summary}");
                Assert.LessOrEqual(rep.ForceClips, 1,
                    $"{name}: at most one bounded, surfaced clean drop — {rep.Summary}");
            }
        }

        // Permanent unit tooth — the reversed-concave ring flagged in review (a simple concave quad).
        // The invariant: no fold (WindingFlips==0) and area conserved. NB: fed through the triangulator the
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
            var res = EarcutJobPolygonRunner.Run(outer);
            var rep = MeshCoverageValidator.ValidateTriangulation(
                new[] { new Polygon(outer) }, ToTriangles(res), res.ForceClips, extent: 4);
            Assert.AreEqual(0, rep.WindingFlips, "reversed-concave quad must not fold: " + rep.Summary);
            Assert.Less(rep.AreaRelError, 0.01, "reversed-concave quad area must be conserved: " + rep.Summary);
        }
    }
}
