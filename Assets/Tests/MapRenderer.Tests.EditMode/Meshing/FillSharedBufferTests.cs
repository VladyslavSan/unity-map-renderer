// Meshing/FillSharedBufferTests.cs — earcut/triangulation, fill mesh-graph, globe subdivision, and line mesh-graph teeth (Burst/Unity.Collections, EditMode only).
//
// Grouped by production area, alphabetically within each: boundary-glitch regression, then the three Earcut triangulation fixtures, then the four Fill fixtures (build-buffers pool, graph allocation, pipeline bounds, shared buffer, sort-key/opacity), then the two Globe subdivision fixtures, then the standalone jobified-water and layer-pooling fixtures, then the three Line mesh-graph fixtures, then the shared right-handed test projection helper.
//
// Contents:
//   BoundaryGlitchMeshTests          — EditMode, the source of truth: build the REAL boundary_3 line mesh through the production path — projection (TileToGeoJob → managed ProjectPoint) + the actual Burst RibbonJob + the Mercator bake — for the three maintainer-reported "line/polygon…
//   EarcutDegenerateTriangleTests    — Unit tests for PointInTriangle's degenerate-candidate branch.
//   EarcutEarTestScanBoundTests      — Acceptance teeth for the bounding-box index over the ear-clip scan (see docs/mesh-triangulation-robustness-design.md): the scan must visit a small, machine-independent number of candidates, and the index arm must be…
//   EarcutTests                      — EditMode tests for EarcutJobPolygonRunner, the driver for EarcutJob (Unity EditMode only — NativeArray/Burst; not registered in Tools/core-tests).
//   FillMeshBuildBuffersPoolTests    — perf/gc-elimination: StyledFillTileBuilder.WriteMeshData runs FillMeshGraph.Schedule (there is no synchronous FillMeshPipeline.Schedule), which itself allocates schedule-time managed…
//   FillMeshGraphAllocationTests     — the schedule-time managed allocation of the only fill mesher, the graph
//                                      (FillMeshGraph.Schedule + Handle.Complete()).
//   FillMeshPipelineBoundsTests      — Exact sizing pre-count + its never-fired capacity backstop.
//   FillSharedBufferTests            — fill reads a buffer it shares with other layers, and joins its per-feature side arrays by the source-layer ordinal rather than by its own selected-list position.
//   FillSortKeyAndOpacityTests       — the two build-time fill behaviours that show up in the MESH rather than in a uniform: fill-sort-key ordering and data-driven fill-opacity.
//   GlobeSubdivisionJobParityTests   — Unity-only source-of-truth parity tooth.
//   GlobeSubdivisionTests            — Globe-fill SUBDIVISION testbench (mesh-triangulation-robustness-design.md), driving SubdivisionCoverageValidator.
//   JobifiedWaterTriangulationTests  — drives the REAL jobified fill path — Schedule, the Burst EarcutJob — over the committed corpus water tile, and validates the output has no folds and conserves area.
//   LayerMeshBuildPoolingTests       — A pooled ILayerMeshBuild instance is never handed to two renters at once — the hazard LayerMeshBuildPool{T} carries: a class can be…
//   LineExtentRoutingTests           — line's own TileToGeoJob extent routing, which had no observing tooth.
//   LineGraphParityTests             — gates first on the per-ring SUBDIVIDED POINT COUNT, exact, element for element — a quantiser (LineCurvatureSubdivision.SegmentSteps), so a 1-ULP input difference across a ceil boundary can shift every downstream vertex.
//   LineGraphSchedulingTests         — job-scheduling-design.md (line graph) — acceptance teeth (a), (b), (d), (e), (g), (h).
//   RightHandedSphereTestProjection  — Test-only helper (not a fixture): a right-handed mirror of SphericalProjection, used to pin winding independently of the production projection's own handedness.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using LineStyleLayer = MapRenderer.Core.Style.Line.StyleLayer;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Expressions;
using MapRenderer.Jobs.Fill;
using MapRenderer.Core.Tiles;
using System;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Tile.Processing;
using Fill = MapRenderer.Core.Style.Fill;
using Unity.Collections;
using MapRenderer.Tests.TestSupport;
using UnityEngine.Rendering;
using MapRenderer.Tests.Jobs;
using Color = UnityEngine.Color;
using Unity.Jobs;
using Line = MapRenderer.Core.Style.Line;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Json;
using MapRenderer.Jobs.Lines;
using MapRenderer.Jobs.Projection;
using MvtCommandStream = MapRenderer.Tests.Jobs.MvtCommandStream;
using Object = UnityEngine.Object;


namespace MapRenderer.Tests.Meshing
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // BoundaryGlitchMeshTests — real production path over three maintainer-reported line/polygon glitches
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the REAL boundary_3 line mesh through the production projection, Burst RibbonJob and Mercator
    /// bake for three tiles once reported to draw lines across the whole screen, and fails on any triangle
    /// edge longer than half a tile. The Core path covers decode and filter, so a failure here points at the
    /// Burst job or the projection/bake.
    /// </summary>
    public class BoundaryGlitchMeshTests
    {
        // fixture, tile z, x, y  (paint/width evaluate at integer tile z, matching the kick path)
        [TestCase("boundary-9-274-168.pbf.bytes", 9, 274, 168)]
        [TestCase("boundary-6-34-21.pbf.bytes",   6,  34,  21)]
        [TestCase("boundary-6-38-19.pbf.bytes",   6,  38,  19)]
        public void Boundary3Mesh_HasNoAcrossTileTriangle(string fixture, int z, int x, int y)
        {
            var id = new TileId { Z = z, X = x, Y = y };

            string styleJson = File.ReadAllText(
                Path.Combine(Application.dataPath, "StreamingAssets", "Fixtures", "liberty.json"));
            StyleDocument style = StyleParser.Parse(styleJson);
            LineStyleLayer layer = null;
            foreach (var l in style.Layers)
                if (l.Id == "boundary_3") { layer = l as LineStyleLayer; break; }
            Assert.IsNotNull(layer, "boundary_3 must be a Line.StyleLayer");

            byte[] bytes = File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture));
            // Decoded at the SAME id the build bakes from — the buffer is the only copy now.
            using MvtTile tile = MvtDecoder.Decode(id, bytes);
            ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            Assert.IsNotNull(mvtLayer, "boundary_3's source-layer must resolve in this fixture");
            var selected = TestTileMeshBuilder.Select(layer, mvtLayer, z);
            Assert.Greater(selected.Count, 0, "expected boundary_3 line features in this tile");

            // Production path: real projection + real Burst RibbonJob + Mercator bake, over the layer's
            // own buffer with this style layer's ordinal-bearing selection.
            Mesh mesh = TestTileMeshBuilder.BuildLineFromLayer(
                mvtLayer, selected, layer.Paint, layer.Layout, z, id, new WebMercatorProjection());
            Assert.IsNotNull(mesh, "boundary_3 produced no geometry");

            Vector3[] verts = mesh.vertices;
            int[]     tris  = mesh.triangles;

            double tileWorld = EarthConstants.EquatorialCircumferenceMetres / math.pow(2.0, z);
            double threshold = tileWorld * 0.5;

            var w = TestContext.Out;
            w.WriteLine($"=== {fixture} z{z} verts={verts.Length} tris={tris.Length / 3} " +
                        $"tileWorld={tileWorld:0}m threshold={threshold:0}m ===");

            int longTris = 0;
            double worst = 0;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                Vector3 a = verts[tris[t]], b = verts[tris[t + 1]], c = verts[tris[t + 2]];
                double e0 = Vector3.Distance(a, b), e1 = Vector3.Distance(b, c), e2 = Vector3.Distance(c, a);
                double e = math.max(e0, math.max(e1, e2));
                if (e > threshold)
                {
                    longTris++;
                    if (e > worst) worst = e;
                    if (longTris <= 8)
                    {
                        w.WriteLine($"  [ACROSS-TILE] tri#{t / 3} edge={e:0}m");
                        w.WriteLine($"     a=({a.x:0},{a.y:0},{a.z:0}) b=({b.x:0},{b.y:0},{b.z:0}) c=({c.x:0},{c.y:0},{c.z:0})");
                    }
                }
            }
            w.WriteLine($"  => across-tile triangles: {longTris}  worstEdge={worst:0}m");

            // Vertices are the CENTERLINE; the shader extrudes along TexCoord0 (miter-capped) times TexCoord2's
            // width scale, so a huge value flings a vertex across the screen while positions look clean.
            var across = new List<Vector3>();
            mesh.GetUVs(0, across);
            var widthScale = new List<Vector4>(); // TexCoord2 is Float32x1 → x carries widthScale
            mesh.GetUVs(2, widthScale);
            double maxAcross = 0; int maxAcrossVert = -1;
            for (int vi = 0; vi < across.Count; vi++)
            {
                double m = across[vi].magnitude;
                if (m > maxAcross) { maxAcross = m; maxAcrossVert = vi; }
            }
            double maxWidth = 0;
            for (int vi = 0; vi < widthScale.Count; vi++)
                if (widthScale[vi].x > maxWidth) maxWidth = widthScale[vi].x;
            w.WriteLine($"  => maxAcross(miterFactor)={maxAcross:0.00} at vert {maxAcrossVert}  maxWidthScale={maxWidth:0.00}");
            w.Flush();

            Assert.AreEqual(0, longTris,
                $"{fixture}: {longTris} triangle(s) span >half a tile (worst {worst:0}m) — glitch is in the mesh centerline.");
            Assert.Less(maxAcross, 8.0,
                $"{fixture}: across/miter factor {maxAcross:0.0} ≫ miter limit — the shader extrude flings vert {maxAcrossVert} across the screen.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // EarcutDegenerateTriangleTests — PointInTriangle's degenerate-candidate branch
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="EarcutJob.PointInTriangle"/>'s degenerate branch: a collinear candidate is a segment, not
    /// the plane through it, so containment uses its bounding box. Limitation: a direct static call runs
    /// managed IL; <c>FillGraphBurstProbeTests</c> and the Burst-error log grep cover the Burst half.
    /// </summary>
    public class EarcutDegenerateTriangleTests
    {
        [Test]
        public void DegenerateTriangle_PointFarOnSharedLine_IsNotContained()
        {
            // A=(3,-8) B=(3,-8) C=(-3,-8): collinear, so the point set is the segment [-3,3] x {-8}.
            // P=(-11,-8) lies on that line but well outside the segment — the lead's witness.
            Assert.IsFalse(EarcutJob.PointInTriangle(3, -8, 3, -8, -3, -8, -11, -8));
        }

        [Test]
        public void DegenerateTriangle_PointOnTheHullSegment_IsContained()
        {
            // P=(0,-8) lies ON the segment: this rules out "a degenerate candidate is always an ear".
            Assert.IsTrue(EarcutJob.PointInTriangle(3, -8, 3, -8, -3, -8, 0, -8));
        }

        [Test]
        public void NonDegenerateTriangle_InsideAndOutsidePoints_AreUnchanged()
        {
            // A proper triangle (0,0) (4,0) (0,4): the fix must not touch the ordinary path.
            Assert.IsTrue(EarcutJob.PointInTriangle(0, 0, 4, 0, 0, 4, 1, 1));
            Assert.IsFalse(EarcutJob.PointInTriangle(0, 0, 4, 0, 0, 4, 5, 5));
        }

        [Test]
        public void DegenerateTriangle_OnDiagonalHull_BoundingBoxIsTwoDimensional()
        {
            // A=(0,0) B=(2,2) C=(4,4): collinear on y=x, so both the x- and y-bounds of the box are live —
            // an axis-aligned witness alone can't catch a bug in either comparison.
            Assert.IsFalse(EarcutJob.PointInTriangle(0, 0, 2, 2, 4, 4, 10, 10));
            Assert.IsTrue(EarcutJob.PointInTriangle(0, 0, 2, 2, 4, 4, 1, 1));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // EarcutEarTestScanBoundTests — the bounding-box index over the ear-clip scan
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ear-clip scan's bounding-box index (docs/mesh-triangulation-robustness-design.md) visits a
    /// small, machine-independent number of candidates, and is byte-identical to the full linear scan on
    /// the water corpus. Both arms run the production chain through <see cref="EarcutJobGatherHarness"/>.
    /// </summary>
    public class EarcutEarTestScanBoundTests
    {
        private static byte[] LoadFixture(string name)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", name);
            FileAssert.Exists(path);
            return File.ReadAllBytes(path);
        }

        // ---- A counted bound, not a clock ---------------------------------------------------

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

            // The bound leaves ~3x headroom over the indexed count and sits ~500x below the forced linear
            // scan. Limitation: the counter counts items, not empty cells, so extra empty-cell walking is unseen.
            Assert.Less(totalVisits, 3_000_000,
                $"Stockholm's ear-test scan visited {totalVisits} candidates — the grid index should keep " +
                "this far below N^2. A value orders of magnitude higher means the un-indexed linear scan " +
                "ran instead of the grid (e.g. forceLinearEarScan stuck on, or the index broken).");
        }

        // ---- Index arm == forced-linear-fallback arm, byte-identical -------------------------

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

        /// <summary>Triangulates <paramref name="fixtureName"/> through <see cref="EarcutJobGatherHarness.RunLayer"/>
        /// with the grid index and with <c>forceLinearEarScan</c>, and asserts each polygon's used-prefix
        /// vertices, indices and ForceClips agree. Only the scan strategy differs, so polygons pair by
        /// index.</summary>
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // EarcutTests — EarcutJobPolygonRunner, the driver for EarcutJob
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tests for <see cref="EarcutJobPolygonRunner"/>, the driver for <see cref="MapRenderer.Jobs.Fill.EarcutJob"/>,
    /// in tile-space double2 (Y-down). A simple polygon with holes gives outerVerts + 2·holeCount − 2 triangles,
    /// and Σ|triArea| equals |outerArea| − Σ|holeAreas| within epsilon.
    /// </summary>
    public class EarcutTests
    {
        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        private static double SignedTriArea(double2 a, double2 b, double2 c)
            => 0.5 * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));

        private static double TotalTriArea(double2[] verts, int[] indices)
        {
            double total = 0.0;
            for (int i = 0; i < indices.Length; i += 3)
                total += Math.Abs(SignedTriArea(verts[indices[i]], verts[indices[i + 1]], verts[indices[i + 2]]));
            return total;
        }

        private static double RingArea(List<double2> ring)
        {
            double area = 0.0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                double2 a = ring[i];
                double2 b = ring[(i + 1) % n];
                area += (b.x - a.x) * (b.y + a.y);
            }
            return Math.Abs(area) * 0.5;
        }

        // -----------------------------------------------------------------------------------------
        // Square (4 verts, no holes) → 2 triangles
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Square_NoHoles_Produces2Triangles()
        {
            // Exterior winding: positive shoelace (canonical CCW in tile space; reads CW on a Y-down screen).
            var square = new List<double2>
            {
                new double2(0, 0),
                new double2(100, 0),
                new double2(100, 100),
                new double2(0, 100),
            };

            var result = EarcutJobPolygonRunner.Run(square);
            Assert.AreEqual(6, result.Indices.Length, "Square should produce 6 indices (2 triangles).");

            // All indices must be in range.
            foreach (int idx in result.Indices)
                Assert.That(idx, Is.GreaterThanOrEqualTo(0).And.LessThan(result.Vertices.Length),
                    $"Index {idx} out of range [0, {result.Vertices.Length}).");
        }

        // -----------------------------------------------------------------------------------------
        // Pentagon → 3 triangles
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Pentagon_NoHoles_Produces3Triangles()
        {
            var pentagon = new List<double2>();
            for (int i = 0; i < 5; i++)
            {
                double angle = 2.0 * Math.PI * i / 5.0;
                pentagon.Add(new double2(100 + 50 * Math.Cos(angle), 100 + 50 * Math.Sin(angle)));
            }

            var result = EarcutJobPolygonRunner.Run(pentagon);
            Assert.AreEqual(9, result.Indices.Length, "Pentagon should produce 9 indices (3 triangles).");

            foreach (int idx in result.Indices)
                Assert.That(idx, Is.GreaterThanOrEqualTo(0).And.LessThan(result.Vertices.Length));
        }

        // -----------------------------------------------------------------------------------------
        // Square with square hole → 8 triangles.
        // Inner vertex count: outer(4) + hole(4) + 2 bridge copies = 10 verts → 10-2 = 8 triangles.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void SquareWithSquareHole_Produces8Triangles()
        {
            // Outer: exterior ring (positive shoelace)
            var outer = new List<double2>
            {
                new double2(0, 0),
                new double2(200, 0),
                new double2(200, 200),
                new double2(0, 200),
            };

            // Hole: opposite winding (negative shoelace)
            var hole = new List<double2>
            {
                new double2(50, 50),
                new double2(50, 150),
                new double2(150, 150),
                new double2(150, 50),
            };

            var result = EarcutJobPolygonRunner.Run(outer, hole);
            Assert.AreEqual(24, result.Indices.Length, "Square+hole should produce 24 indices (8 triangles).");

            foreach (int idx in result.Indices)
                Assert.That(idx, Is.GreaterThanOrEqualTo(0).And.LessThan(result.Vertices.Length),
                    $"Index {idx} out of range [0, {result.Vertices.Length}).");
        }

        // -----------------------------------------------------------------------------------------
        // Area conservation: Σ|triArea| ≈ |outerArea| − |holeArea| (tile space)
        // -----------------------------------------------------------------------------------------

        [Test]
        public void SquareWithHole_AreaIsConserved()
        {
            var outer = new List<double2>
            {
                new double2(0, 0),
                new double2(400, 0),
                new double2(400, 400),
                new double2(0, 400),
            };
            var hole = new List<double2>
            {
                new double2(100, 100),
                new double2(100, 300),
                new double2(300, 300),
                new double2(300, 100),
            };

            var result = EarcutJobPolygonRunner.Run(outer, hole);
            Assert.Greater(result.Indices.Length, 0, "Should produce triangles.");

            double triArea = TotalTriArea(result.Vertices, result.Indices);
            double expectedArea = RingArea(outer) - RingArea(hole);

            Assert.That(triArea, Is.EqualTo(expectedArea).Within(expectedArea * 1e-6),
                $"Area conservation: got {triArea}, expected {expectedArea}");
        }

        // -----------------------------------------------------------------------------------------
        // Simple polygon (no hole) area conservation.
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Square_NoHoles_AreaIsConserved()
        {
            var outer = new List<double2>
            {
                new double2(0, 0),
                new double2(300, 0),
                new double2(300, 300),
                new double2(0, 300),
            };

            var result = EarcutJobPolygonRunner.Run(outer);
            double triArea = TotalTriArea(result.Vertices, result.Indices);
            double expectedArea = RingArea(outer);

            Assert.That(triArea, Is.EqualTo(expectedArea).Within(expectedArea * 1e-6),
                $"Area conservation: got {triArea}, expected {expectedArea}");
        }

        // -----------------------------------------------------------------------------------------
        // Index range validity (explicit check with hole)
        // -----------------------------------------------------------------------------------------

        [Test]
        public void AllIndicesAreInRange()
        {
            var outer = new List<double2>
            {
                new double2(0, 0), new double2(300, 0),
                new double2(300, 300), new double2(0, 300),
            };
            var hole = new List<double2>
            {
                new double2(50, 50), new double2(50, 250),
                new double2(250, 250), new double2(250, 50),
            };

            var result = EarcutJobPolygonRunner.Run(outer, hole);
            foreach (int idx in result.Indices)
                Assert.That(idx, Is.GreaterThanOrEqualTo(0).And.LessThan(result.Vertices.Length),
                    $"Index {idx} out of range [0, {result.Vertices.Length})");
        }

        // -----------------------------------------------------------------------------------------
        // Degenerate input
        // -----------------------------------------------------------------------------------------

        [Test]
        public void NullOuter_ReturnsEmpty()
        {
            var result = EarcutJobPolygonRunner.Run(null);
            Assert.AreEqual(0, result.Indices.Length);
            Assert.AreEqual(0, result.Vertices.Length);
        }

        [Test]
        public void TooFewVerts_ReturnsEmpty()
        {
            var ring = new List<double2> { new double2(0, 0), new double2(1, 1) };
            var result = EarcutJobPolygonRunner.Run(ring);
            Assert.AreEqual(0, result.Indices.Length);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillMeshBuildBuffersPoolTests — schedule-time managed allocation after the pipeline retirement
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class FillMeshBuildBuffersPoolTests : BaseTestFixture
    {
        private const double Extent = 4096.0;
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double Zoom = 0.0;

        private static uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));

        /// <summary>One axis-aligned square feature, same MVT command encoding <c>FillSortKeyAndOpacityTests</c>
        /// uses — drives the real decode/assemble/earcut path rather than a bypass.</summary>
        private static uint[] Square(int x, int y, int size) => new[]
        {
            (1u << 3) | 1u, ZigZag(x),     ZigZag(y),     // MoveTo (x, y)
            (3u << 3) | 2u, ZigZag(size),  ZigZag(0),     // LineTo +x
                            ZigZag(0),     ZigZag(size),  // LineTo +y
                            ZigZag(-size), ZigZag(0),     // LineTo -x
            (1u << 3) | 7u,                               // ClosePath
        };

        private static DictionaryFeature Feature(int x, int y, double sortKey) => new DictionaryFeature(
            new Dictionary<string, Value> { ["sk"] = Value.Number(sortKey) },
            TileGeometryType.Polygon,
            geometry: Square(x, y, 100));

        /// <summary><paramref name="count"/> distinct, non-overlapping squares with sort keys DESCENDING as
        /// declared (<c>count, count-1, …, 1</c>) — the reverse of ascending sort-key order — so
        /// <c>OrderBySortKey</c> does real reordering work every call, not a short-circuit or a no-op sort.</summary>
        private static List<IFeature> MakeFeatures(int count)
        {
            var list = new List<IFeature>(count);
            for (int i = 0; i < count; i++)
                list.Add(Feature(x: i * 500, y: 0, sortKey: count - i));
            return list;
        }

        // Data-driven over "sk": a CONSTANT fill-color leaves vertices white, which would make the pooled-vs-
        // reference colour comparison vacuous.
        private static readonly Fill.PaintProperties Paint =
            TestStyle.FillPaint(@"{""fill-color"": [""interpolate"",[""linear""],[""get"",""sk""],1,[""rgb"",255,0,0],8,[""rgb"",0,0,255]]}");
        private static readonly Fill.LayoutProperties SortKeyLayout =
            TestStyle.FillLayout(@"{""fill-sort-key"": [""get"", ""sk""]}");

        /// <summary>
        /// Reuse-by-identity tooth (meter-independent — <c>GC.GetAllocatedBytesForCurrentThread()</c> is dead in
        /// this EditMode Mono runner, returning 0 for even a 10 MB allocation, so a byte-differential cannot
        /// discriminate). Every pooled buffer must be the SAME instance across calls (grow-only, never
        /// re-allocated), and a smaller request must reuse the already-grown buffer. A non-pooling "new each
        /// call" implementation fails every <c>AreSame</c> below.
        /// </summary>
        [Test]
        public void TileBuildBuffers_ReusesBuffersByIdentity_GrowOnly()
        {
            var buffers = new TileBuildBuffers();

            int[] rank = buffers.RankStart(8);
            Assert.AreSame(rank, buffers.RankStart(8), "RankStart reuses its backing array for the same size (grow-only).");
            Assert.AreSame(rank, buffers.RankStart(3), "a smaller RankStart request reuses the already-grown buffer, never re-allocates.");
            int[] grown = buffers.RankStart(64);
            Assert.AreSame(grown, buffers.RankStart(64), "after growing past the prior peak, RankStart is stable again.");

            Assert.AreSame(buffers.RankCursor(8), buffers.RankCursor(8), "RankCursor reuses its backing array.");
            Assert.AreSame(buffers.SortKeys(8), buffers.SortKeys(8), "SortKeys reuses its backing array.");
            Assert.AreSame(buffers.DeclaredOrder(8), buffers.DeclaredOrder(8), "DeclaredOrder reuses its backing array.");
            Assert.AreSame(buffers.OrderedFeaturesBuffer(8), buffers.OrderedFeaturesBuffer(8), "OrderedFeaturesBuffer reuses its backing array.");

            float[] keys = buffers.SortKeys(8);
            Assert.AreSame(buffers.SortKeyComparer(keys), buffers.SortKeyComparer(keys),
                "SortKeyComparer is a stored instance re-fielded per call — NOT a fresh delegate/closure the way Array.Sort's lambda overload allocates.");
            Assert.AreSame(buffers.OrderedFeaturesView(4), buffers.OrderedFeaturesView(4),
                "OrderedFeaturesView is a stored IReadOnlyList instance, not a fresh wrapper per call.");

            Assert.AreSame(buffers.SelectionBuffer(8), buffers.SelectionBuffer(8), "SelectionBuffer reuses its backing array.");
            Assert.AreSame(buffers.SelectionView(4), buffers.SelectionView(4),
                "SelectionView is a stored IReadOnlyList instance, not a fresh wrapper per call.");
            Assert.AreNotSame(buffers.OrderedFeaturesBuffer(8), buffers.SelectionBuffer(8),
                "SelectionBuffer must be a DISTINCT array from OrderedFeaturesBuffer — OrderBySortKey reads a " +
                "layer's selection while writing the reordered result, so aliasing the two would corrupt the sort.");
        }

        /// <summary>
        /// A real <see cref="SyncMeshWrite.Fill"/> build ROUTES its allocations through the pooled
        /// <see cref="TileBuildBuffers"/>: under a live <c>fill-sort-key</c>, <c>BuildRingVisitOrder</c> grows
        /// <c>RankStart</c> and <c>OrderBySortKey</c> grows <c>SortKeys</c>. A build that ignored the parameter
        /// leaves both empty.
        /// </summary>
        [Test]
        public void WriteMeshData_RoutesBothBufferSites_ThroughThePooledBuffers()
        {
            List<IFeature> features = MakeFeatures(4);
            IReadOnlyList<SelectedTileFeature> selected = TestTileMeshBuilder.Selection(features);
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, Tile, Extent);
            try
            {
                Assert.Greater(geometry.RingCount, 1, "precondition: multiple rings");

                var buffers = new TileBuildBuffers();
                Assert.AreEqual(0, buffers.RankStart(0).Length, "precondition: RankStart buffer is empty before any build");
                Assert.AreEqual(0, buffers.SortKeys(0).Length, "precondition: SortKeys buffer is empty before any build");

                Mesh.MeshDataArray mda = Mesh.AllocateWritableMeshData(1);
                SyncMeshWrite.Fill(mda[0], selected, geometry, Paint, Zoom, double3.zero,
                    out int verts, out Bounds _, null, SortKeyLayout, default, buffers);
                mda.Dispose();
                Assert.Greater(verts, 0, "non-vacuity: the build must produce geometry");

                Assert.Greater(buffers.RankStart(0).Length, 0,
                    "BuildRingVisitOrder must route through buffers.RankStart — its buffer grew past empty during the build.");
                Assert.Greater(buffers.SortKeys(0).Length, 0,
                    "OrderBySortKey (fill-sort-key declared) must route through buffers.SortKeys — its buffer grew past empty during the build.");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        /// <summary>
        /// A build's output is BYTE-IDENTICAL whether its <see cref="TileBuildBuffers"/> is fresh or was just
        /// grown by a LARGER build, the risk the type's doc names. A 6-feature build grows the buffers, then a
        /// 2-feature build on the same instance must match a <c>buffers: null</c> build in vertices, triangles
        /// and colours.
        /// </summary>
        [Test]
        public void WriteMeshData_BuffersReusedAfterALargerBuild_MatchesAFreshNonPooledBuild()
        {
            List<IFeature> largerFixture  = MakeFeatures(6);
            List<IFeature> smallerFixture = MakeFeatures(2);

            var buffers = new TileBuildBuffers();

            // Grows every buffers buffer to the LARGER fixture's peak — never touched by the smaller build yet.
            Mesh throwaway = TestTileMeshBuilder.BuildFill(
                largerFixture, Paint, Zoom, Extent, Tile, null, SortKeyLayout, default, buffers);
            Assert.IsNotNull(throwaway, "precondition: the larger warm-up build must produce geometry");
            Object.DestroyImmediate(throwaway);

            Mesh pooled = Track(TestTileMeshBuilder.BuildFill(
                smallerFixture, Paint, Zoom, Extent, Tile, null, SortKeyLayout, default, buffers));
            Mesh reference = Track(TestTileMeshBuilder.BuildFill(
                smallerFixture, Paint, Zoom, Extent, Tile, null, SortKeyLayout, default, null));
            Assert.IsNotNull(pooled);
            Assert.IsNotNull(reference);
            CollectionAssert.AreEqual(reference.vertices, pooled.vertices,
                "vertex positions must be byte-identical — a reused, larger-grown buffers must not leak " +
                "the prior (larger) build's data into a smaller one's [0, count) window");
            CollectionAssert.AreEqual(reference.triangles, pooled.triangles,
                "triangle/draw order must be byte-identical — this is what a stale (un-cleared) rank-start " +
                "prefix sum, or a mis-sized ordered-features view, would corrupt first");
            CollectionAssert.AreEqual(reference.colors, pooled.colors,
                "per-vertex colour must be byte-identical — desyncs from geometry exactly when the ordered " +
                "view over-reports its length and the loop reads stale entries from the prior build's tail");
        }

        /// <summary>The non-pooled (<c>buffers: null</c>) path is unchanged by pooling's existence.</summary>
        [Test]
        public void WriteMeshData_NullBuffers_StillProducesGeometry()
        {
            List<IFeature> features = MakeFeatures(2);
            Mesh mesh = Track(TestTileMeshBuilder.BuildFill(features, Paint, Zoom, Extent, Tile, null, SortKeyLayout));
            Assert.IsNotNull(mesh, "buffers: null must still allocate its own buffers and produce geometry");
            Assert.Greater(mesh.vertexCount, 0);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillMeshGraphAllocationTests — the graph is now the only mesher; measures its schedule-time cost
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class FillMeshGraphAllocationTests
    {
        private static string FixturePath =>
            Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");

        // A 31x floor below the pre-flatten baseline (~2,023,424 B/build on the full fixture).
        private const long Ceiling = 65_536;

        // GC.GetTotalMemory's own noise floor (brief: only trustworthy at >= ~100 KB/op) — the calibration
        // canary must clear this by a wide margin to prove the meter is alive.
        private const long CalibrationFloor = 100_000;

        /// <summary>One materialized fixture (a prefix of the "countries" layer's polygon features) plus the
        /// derived <see cref="FillMeshPipeline.LayerInput"/> ready to <c>Schedule</c> repeatedly.</summary>
        private readonly struct Fixture
        {
            public readonly TileGeometryBuffers Geometry;
            public readonly NativeArray<int> VisitOrder;
            public readonly FillMeshPipeline.LayerInput Input;
            public readonly int RingCount;

            public Fixture(TileGeometryBuffers geometry, NativeArray<int> visitOrder,
                FillMeshPipeline.LayerInput input, int ringCount)
            {
                Geometry = geometry; VisitOrder = visitOrder; Input = input; RingCount = ringCount;
            }

            public void Dispose()
            {
                VisitOrder.Dispose();
                Geometry.Dispose();
            }
        }

        /// <summary>Materializes the first <paramref name="featureLimit"/> polygon features of the real
        /// "countries" layer (sample-tile.bytes) — a real, non-synthetic corpus, so the tooth measures the
        /// actual per-polygon shapes (rings, holes) FillMeshGraph.Schedule sees in production.</summary>
        private static Fixture BuildFixture(int featureLimit)
        {
            FileAssert.Exists(FixturePath);
            byte[] mvtBytes = File.ReadAllBytes(FixturePath);
            var layer = MvtFixtureStreams.ReadLayer(mvtBytes, "countries");
            Assert.IsNotNull(layer);

            var kinds    = new List<TileGeometryType>();
            var commands = new List<uint[]>();
            for (int fi = 0; fi < layer.Kinds.Count && kinds.Count < featureLimit; fi++)
                if (layer.Kinds[fi] == TileGeometryType.Polygon && layer.Commands[fi] != null)
                { kinds.Add(layer.Kinds[fi]); commands.Add(layer.Commands[fi]); }

            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            TileGeometryBuffers geometry = MvtGeometryMaterializerTestFactory.Materialize(tile, layer.Extent, kinds, commands);
            NativeArray<int> visitOrder  = TestTileMeshBuilder.FullVisitOrder(geometry);
            var (bMin, _) = tile.MercatorBounds();

            var input = new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = visitOrder,
                OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                Projection     = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
            };

            return new Fixture(geometry, visitOrder, input, geometry.RingCount);
        }

        /// <summary>Bytes/build over a warmed loop, guarded against a Gen0 collection firing inside the
        /// measurement window (which would deflate — or invert — the delta).</summary>
        private static long BytesPerBuild(in FillMeshPipeline.LayerInput input, int iterations)
        {
            // Warm-up: JIT compilation and any one-shot first-touch allocation must not land in the window.
            for (int w = 0; w < 3; w++)
            {
                FillGraphOutput warm = FillMeshGraph.Schedule(input);
                warm.Handle.Complete();
                warm.Dispose();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            int collectionsBefore = GC.CollectionCount(0);
            long before = GC.GetTotalMemory(false);

            for (int i = 0; i < iterations; i++)
            {
                FillGraphOutput b = FillMeshGraph.Schedule(input);
                b.Handle.Complete();
                b.Dispose();
            }

            long after = GC.GetTotalMemory(false);
            int collectionsAfter = GC.CollectionCount(0);

            Assert.AreEqual(collectionsBefore, collectionsAfter,
                "a Gen0 collection fired inside the measurement window — the byte delta is unreliable here; " +
                "this indicates a flaky run, not a pipeline result.");

            return (after - before) / iterations;
        }

        /// <summary>Proves GC.GetTotalMemory is a LIVE meter in this run before the pipeline tooth below
        /// trusts it — GC.GetAllocatedBytesForCurrentThread is dead in this same Mono runner (see
        /// FillMeshBuildBuffersPoolTests), and a silently-dead meter would make every assertion below
        /// vacuous.</summary>
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

        /// <summary>
        /// The flatten's headline tooth: per-polygon handle-arrays measured 2,023,424 B/build on
        /// this exact fixture before the flatten (~43,000 allocations). RED-verify by restoring the retired
        /// `new NativeArray&lt;T&gt;[polyCount]` shape and confirming this blows the ceiling.
        /// </summary>
        [Test]
        public void Schedule_FullFixture_AllocatesUnder65536BytesPerBuild()
        {
            Fixture fx = BuildFixture(featureLimit: int.MaxValue);
            try
            {
                Assert.Greater(fx.RingCount, 1000,
                    "precondition: the full fixture must be the large real-data corpus (hundreds of features, " +
                    "thousands of rings) — a small fixture couldn't have exercised the pre-flatten cost either.");

                long bytesPerBuild = BytesPerBuild(fx.Input, iterations: 10);

                Assert.LessOrEqual(bytesPerBuild, Ceiling,
                    $"FillMeshGraph.Schedule allocated {bytesPerBuild} B/build over the full fixture " +
                    $"({fx.RingCount} rings) — must stay under the {Ceiling} B ceiling (31x below the " +
                    "pre-flatten ~2,023,424 B/build baseline on this same fixture).");
            }
            finally { fx.Dispose(); }
        }

        /// <summary>
        /// The pre-flatten cost scaled ~linearly with ring count (one managed NativeArray handle allocated
        /// per polygon, per stream). Flattened, it allocates a FIXED set of buffers sized once — so
        /// bytes/build must stay near its floor across meaningfully different ring counts, not grow with them.
        /// RED-verify the same way as the ceiling tooth: restoring the per-polygon arrays reintroduces the
        /// scaling and blows this spread by roughly two orders of magnitude.
        /// </summary>
        [Test]
        public void Schedule_AllocationDoesNotScaleWithRingCount()
        {
            Fixture small  = BuildFixture(featureLimit: 20);
            Fixture medium = BuildFixture(featureLimit: 80);
            Fixture large  = BuildFixture(featureLimit: int.MaxValue);
            try
            {
                Assert.Less(small.RingCount, medium.RingCount,
                    "precondition: the three fixtures must actually differ in ring count.");
                Assert.Less(medium.RingCount, large.RingCount,
                    "precondition: the three fixtures must actually differ in ring count.");

                long smallBpb  = BytesPerBuild(small.Input,  iterations: 10);
                long mediumBpb = BytesPerBuild(medium.Input, iterations: 10);
                long largeBpb  = BytesPerBuild(large.Input,  iterations: 10);

                Assert.LessOrEqual(smallBpb,  Ceiling);
                Assert.LessOrEqual(mediumBpb, Ceiling);
                Assert.LessOrEqual(largeBpb,  Ceiling);

                // The ceiling above cannot catch a small per-ring allocation; this spread cap does, because even
                // ~16 B/ring over thousands of extra rings adds tens of KB per build.
                long spread = Math.Abs(largeBpb - smallBpb);
                Assert.Less(spread, 4_096,
                    $"bytes/build spread across ring counts {small.RingCount}/{medium.RingCount}/{large.RingCount} " +
                    $"was {spread} B (small={smallBpb}, medium={mediumBpb}, large={largeBpb} B/build) — " +
                    "allocation is still scaling with ring count; the flatten did not eliminate the per-polygon cost.");
            }
            finally { small.Dispose(); medium.Dispose(); large.Dispose(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillMeshPipelineBoundsTests — exact sizing pre-count + its never-fired capacity backstop
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="FillMeshPipeline.PrecountRingsAndVertices"/> walks the command stream as
    /// <c>MvtDecodeJob.Execute</c> does, so decode buffers are sized exactly for any input, including a
    /// malformed multi-point MoveTo, in every build. <see cref="FillMeshPipeline.EnsureCapacity"/> stays as a
    /// backstop throw that exact sizing never fires; its boundary is tested so a sizing/decode desync throws.
    /// </summary>
    [TestFixture]
    public class FillMeshPipelineBoundsTests
    {
        [Test]
        public void EnsureCapacity_CountWithinCapacity_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => FillMeshPipeline.EnsureCapacity(0, 0, "empty"));
            Assert.DoesNotThrow(() => FillMeshPipeline.EnsureCapacity(5, 10, "ring"));
            Assert.DoesNotThrow(() => FillMeshPipeline.EnsureCapacity(10, 10, "exact-fit"));
        }

        [Test]
        public void EnsureCapacity_CountExceedsCapacity_ThrowsLoudly()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => FillMeshPipeline.EnsureCapacity(11, 10, "ring"));
            StringAssert.Contains("ring", ex.Message, "message must name the overflowing quantity");
            StringAssert.Contains("11", ex.Message, "message must report the actual count");
            StringAssert.Contains("10", ex.Message, "message must report the capacity");
        }

        [Test]
        public void EnsureCapacity_OffByOne_Throws()
        {
            // The boundary: capacity N admits exactly N, rejects N+1.
            Assert.DoesNotThrow(() => FillMeshPipeline.EnsureCapacity(100, 100, "vertex"));
            Assert.Throws<InvalidOperationException>(
                () => FillMeshPipeline.EnsureCapacity(101, 100, "vertex"),
                "count == capacity + 1 must throw (the first out-of-range write).");
        }

        [Test]
        public void EnsureCapacity_LargeOverflow_Throws()
        {
            Assert.Throws<InvalidOperationException>(
                () => FillMeshPipeline.EnsureCapacity(int.MaxValue, 4096, "polygon"));
        }

        // ── Exact pre-count. ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The malformed-stream counterexample: a single Polygon feature whose geometry is ONE multi-point
        /// MoveTo with count=11. The decode job emits 11 single-vertex rings and 11 vertices from this one
        /// header. The OLD heuristic sized maxRings = 23/3 + 1 + 2 = 10 (under-allocating by one), which let
        /// MvtDecodeJob write OutRingOffsets[10]/OutRingFeatureIndex[10] out of range. The exact pre-count
        /// must report exactly 11 rings and 11 vertices so the buffers are sized to hold them.
        /// </summary>
        [Test]
        public void PrecountRingsAndVertices_MultiPointMoveTo_Count11_ExactCounts()
        {
            uint[] geom = MultiPointMoveTo(11);

            // 2a: PrecountRingsAndVertices takes flat (commands, offsets, lengths), so flatten this feature.
            using var flat = MvtGeometryMaterializerTestFactory.Flatten(new List<uint[]> { geom });
            FillMeshPipeline.PrecountRingsAndVertices(
                flat.Commands, flat.FeatureOffsets, flat.FeatureLengths, out int rings, out int vertices);

            Assert.AreEqual(11, rings,    "MoveTo count=11 starts 11 rings");
            Assert.AreEqual(11, vertices, "MoveTo count=11 emits 11 vertices");

            // Sanity: the old heuristic under-allocated rings for exactly this input.
            int totalCommands = geom.Length;                        // 1 header + 22 params = 23
            int oldMaxRings   = totalCommands / 3 + 1 + 2;           // 23/3 + 1 feature + 2 = 10
            Assert.Less(oldMaxRings, rings,
                "regression guard: the replaced heuristic under-allocated for this stream");
        }

        /// <summary>
        /// Spec-compliant geometry: one MoveTo count=1 (ring start) followed by a LineTo count=3
        /// (three more vertices) = 1 ring, 4 vertices. Confirms the walk matches the normal path too.
        /// </summary>
        [Test]
        public void PrecountRingsAndVertices_SpecCompliantRing_ExactCounts()
        {
            // [MoveTo count=1, dx, dy, LineTo count=3, dx,dy, dx,dy, dx,dy]
            var geom = new uint[]
            {
                (1u << 3) | 1u, 0u, 0u,            // MoveTo count=1
                (3u << 3) | 2u, 0u, 0u, 0u, 0u, 0u, 0u, // LineTo count=3
            };
            using var flat = MvtGeometryMaterializerTestFactory.Flatten(new List<uint[]> { geom });
            FillMeshPipeline.PrecountRingsAndVertices(
                flat.Commands, flat.FeatureOffsets, flat.FeatureLengths, out int rings, out int vertices);

            Assert.AreEqual(1, rings);
            Assert.AreEqual(4, vertices);
        }

        /// <summary>
        /// The count=11 malformed Polygon feature flows through <see cref="FillMeshGraph.Schedule"/> with no
        /// out-of-range write; heuristic sizing would write the 11th ring past a length-10 array. The 11
        /// single-vertex rings are degenerate, so Schedule returns default.
        /// </summary>
        [Test]
        public void Schedule_MultiPointMoveTo_Count11_CompletesWithoutOverflow()
        {
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(
                new IFeature[]
                {
                    new DictionaryFeature(properties: null, geometryType: TileGeometryType.Polygon, hasId: false, geometry: MultiPointMoveTo(11)),
                },
                new TileId { Z = 0, X = 0, Y = 0 }, 4096);
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);

            var input = new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = visitOrder,
                OriginRender   = default, // all rings degenerate ⇒ no geometry ⇒ origin irrelevant here
                Projection     = new WebMercatorProjection(),
            };

            FillGraphOutput buffers = default;
            Assert.DoesNotThrow(() =>
            {
                buffers = FillMeshGraph.Schedule(input);
                buffers.Handle.Complete();
            }, "exact pre-count sizing must prevent the in-job out-of-range write for a multi-point MoveTo");

            // No polygons → default buffers; FillGraphOutput.Dispose returns early on !IsCreated, so no guard.
            buffers.Dispose();
            visitOrder.Dispose();
            geometry.Dispose();
        }

        /// <summary>MVT geometry: a single MoveTo command with the given point count, plus its 2*count
        /// (zero-delta) parameter uints. count=N starts N rings inside MvtDecodeJob.</summary>
        private static uint[] MultiPointMoveTo(int count)
        {
            var geom = new uint[1 + 2 * count];
            geom[0] = ((uint)count << 3) | 1u; // command = MoveTo(1), count = N
            // remaining entries left 0 → zigzag-decodes to delta (0,0) per point
            return geom;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillSharedBufferTests — fill reads a buffer shared with other layers, joined by source-layer ordinal
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fill reads a buffer it <b>shares</b> with other layers and joins its per-feature side arrays by the
    /// source-layer <b>ordinal</b>, not its selected-list position. The same three features as
    /// <c>FillSortKeyAndOpacityTests</c> run through a buffer that also holds unselected features and a
    /// selected one fill cannot draw, where slot and ordinal differ.
    /// </summary>
    [TestFixture]
    public class FillSharedBufferTests : BaseTestFixture
    {
        private const double Extent = 4096.0;
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };

        private static readonly string ColourByName =
            @"{""fill-color"": [""match"", [""get"", ""name""],
               ""bottom"", ""#ff0000"", ""middle"", ""#00ff00"", ""top"", ""#0000ff"", ""#ffffff""]}";
        private const string SortKeyBySk = @"{""fill-sort-key"": [""get"", ""sk""]}";

        // ── the shared-buffer form of the draw-order tooth ────────────────────────────────────────

        /// <summary>
        /// Three sort-keyed squares share their buffer with two rejected polygons and a selected LineString
        /// whose sort key interleaves; colour runs and vertex count must equal the three-alone build. It
        /// catches a visit order that skips the selection or kind check, and <c>featureColors</c> indexed by
        /// selected slot instead of ordinal.
        /// </summary>
        [Test]
        public void SharedBuffer_UnselectedAndNonPolygonFeatures_DoNotChangeTheDrawOrderOrTheGeometry()
            => AssertSharedBufferMatchesTheUnsharedControl(TileBufferClip.Disabled);

        /// <summary>
        /// The same comparison on the <b>clip</b> branch, which production takes (<c>FillTileBufferClip = 0.0</c>
        /// is <c>KeepTileUnits(0.0)</c>, so <c>RingClipJob</c> runs). The visit order <c>[5, 2, 1]</c> makes
        /// <c>RingClipJob</c>'s <c>ri = RingVisitOrder[k]</c> observable: <c>ri = k</c> would move both geometry
        /// and colours. Every ring lies inside the b = 0 window, so the clip itself is inert.
        /// </summary>
        [Test]
        public void SharedBuffer_OnTheClipBranch_UnselectedAndNonPolygonFeatures_StillChangeNothing()
            => AssertSharedBufferMatchesTheUnsharedControl(TileBufferClip.KeepTileUnits(0.0));

        private void AssertSharedBufferMatchesTheUnsharedControl(TileBufferClip clip)
        {
            // The control arm: only the three features this layer draws, in declared order.
            var alone = new List<IFeature>
            {
                Square(0, 0, "top",    sortKey: 10.0),
                Square(0, 0, "middle", sortKey:  5.0),
                Square(0, 0, "bottom", sortKey:  1.0),
            };

            // The three at ordinals 1, 2, 5; unselected polygons at 0, 3; a SELECTED LineString at 4 whose
            // sort key (7) sits between "middle" and "top", so a missed skip or slot lookup shifts colours.
            var shared = new List<IFeature>
            {
                Square(2000, 2000, "unselected-a", sortKey: 0.5),
                Square(0, 0, "top",    sortKey: 10.0),
                Square(0, 0, "middle", sortKey:  5.0),
                Square(3000,  100, "unselected-b", sortKey: 99.0),
                Line(1000, 1000, "road", sortKey: 7.0),
                Square(0, 0, "bottom", sortKey:  1.0),
            };
            int[] selectedOrdinals = { 1, 2, 4, 5 };

            var paint  = TestStyle.FillPaint(ColourByName);
            var layout = TestStyle.FillLayout(SortKeyBySk);

            Mesh control = Track(TestTileMeshBuilder.BuildFill(alone, paint, 0.0, Extent, Tile, null, layout, clip));
            Mesh mixed   = Track(BuildFillFromSharedBuffer(shared, selectedOrdinals, paint, layout, clip));

            // Non-vacuity: both arms produced geometry, and the fixtures really do differ in what the
            // BUFFER holds — otherwise this is one build compared with itself.
            Assert.IsNotNull(control, "the control arm must produce geometry");
            Assert.IsNotNull(mixed, "the shared-buffer arm must produce geometry");
            Assert.AreEqual(6, shared.Count, "precondition: the shared buffer holds six features");
            Assert.AreEqual(4, selectedOrdinals.Length, "precondition: this layer selects four of them");
            Assert.AreEqual(TileGeometryType.LineString, shared[4].GeometryType,
                "precondition: one SELECTED feature is a LineString the fill layer cannot draw");

            Assert.AreEqual(control.vertexCount, mixed.vertexCount,
                "the extra features must contribute NO geometry — a larger count means the selection or " +
                "the kind predicate was dropped from the ring visit order, and a smaller one means a " +
                "wanted feature was skipped");
            Assert.AreEqual(control.triangles.Length, mixed.triangles.Length, "…and no extra triangles");

            List<Color> controlRuns = PaintOrderColorRuns(control);
            List<Color> mixedRuns   = PaintOrderColorRuns(mixed);

            Assert.AreEqual(3, controlRuns.Count,
                "precondition: the control really draws three distinguishable colour runs");
            Assert.Greater(controlRuns[0].r, 0.5f,
                "precondition: ascending sort key puts red 'bottom' first in the control. Read a failure " +
                "HERE as the finding rather than as a broken fixture: it means the ring visit order moved " +
                "on the control's own build, before the shared-buffer comparison below could even run. " +
                "(That is exactly how the clip arm reports the `int ri = k;` defect.)");

            Assert.AreEqual(controlRuns.Count, mixedRuns.Count, "same number of colour runs");
            for (int i = 0; i < controlRuns.Count; i++)
                Assert.AreEqual(controlRuns[i], mixedRuns[i],
                    $"colour run {i} differs. Same run count with different colours is the ORDINAL-JOIN " +
                    "failure: featureColors indexed by the selected slot instead of the source-layer " +
                    "ordinal, so every feature paints with a neighbour's colour.");
        }

        // ── the GLOBE colour join (a second, independent read of featureColors) ─────────────

        /// <summary>
        /// The globe fill path reads <c>featureColors</c> a <b>second</b>, independent way
        /// (<c>GlobeFillVertex.Feature</c> after subdivision), so three sort-keyed coincident squares must paint
        /// in the same order on the globe as on the flat sheet. No other globe fill test reads vertex colours
        /// with more than one feature.
        /// </summary>
        [Test]
        public void GlobeFill_ColoursByOrdinalToo_SoTheDrawOrderMatchesTheFlatPath()
        {
            var features = new List<IFeature>
            {
                Square(0, 0, "top",    sortKey: 10.0),
                Square(0, 0, "middle", sortKey:  5.0),
                Square(0, 0, "bottom", sortKey:  1.0),
            };
            var paint  = TestStyle.FillPaint(ColourByName);
            var layout = TestStyle.FillLayout(SortKeyBySk);

            // Band-free flat arm: the non-vacuity claim below is about SUBDIVISION, and the flat build's
            // outward boundary band (which the curved arm does not carry) would otherwise inflate it.
            Mesh flat  = Track(TestTileMeshBuilder.BuildFill(
                features, paint, 0.0, Extent, Tile, null, layout, suppressBoundaryBand: true));
            Mesh globe = Track(TestTileMeshBuilder.BuildFill(
                features, paint, 0.0, Extent, Tile, new SphericalProjection(), layout));

            Assert.IsNotNull(flat, "precondition: the flat arm must produce geometry");
            Assert.IsNotNull(globe, "precondition: the globe arm must produce geometry");

            // Non-vacuity by PROJECTION: these squares are too small to subdivide, so no count differs, but
            // Mercator's Up is the constant (0,1,0) and the globe's geodetic Up here is not.
            Assert.AreNotEqual(new Vector3(0f, 1f, 0f), globe.normals[0],
                "precondition: the globe arm must actually use real geodetic Up, not the flat plane's constant (0,1,0)");

            List<Color> flatRuns  = PaintOrderColorRuns(flat);
            List<Color> globeRuns = PaintOrderColorRuns(globe);

            Assert.AreEqual(3, flatRuns.Count, "precondition: three distinguishable colour runs on the flat path");
            Assert.AreEqual(flatRuns.Count, globeRuns.Count,
                "the globe path must paint the same number of colour runs as the flat one");
            for (int i = 0; i < flatRuns.Count; i++)
                Assert.AreEqual(flatRuns[i], globeRuns[i],
                    $"globe colour run {i} differs from the flat path's. The globe write reads " +
                    "featureColors through GlobeFillVertex.Feature — a SECOND join, which no other test " +
                    "in the repo observes, and which a mis-index turns into a globe-only miscolour that " +
                    "renders perfectly and is simply the wrong colour.");
        }

        // ── WITHIN-feature ring order (the measured blind spot) ────────────────────────────────

        /// <summary>
        /// A polygon whose rings are <b>outer then hole</b> keeps its hole: the visit order keeps decode order
        /// <b>within</b> a rank. Corpus oracles compare two arms that share the defect or use an identity
        /// visit order, so only this test sees it. The oracle is covered area: 8/9 of the solid square, which
        /// reversed rings turn into the hole's 1/9.
        /// </summary>
        [Test]
        public void WithinAFeature_RingsKeepDecodeOrder_SoAPolygonsHoleIsStillCutOut()
        {
            // MvtCommandStream carries the cursor ACROSS rings as the spec requires. Outer is CCW, the hole is
            // opposite and centred at (2000, 2000) inside the outer, so containment accepts it.
            var outerRing = MvtCommandStream.Ring(500, 500, 3500, 500, 3500, 3500, 500, 3500);
            var holeRing  = MvtCommandStream.Ring(1500, 1500, 1500, 2500, 2500, 2500, 2500, 1500);

            var withHole = new List<IFeature>
            {
                new DictionaryFeature(
                    Props("ring", 0.0), TileGeometryType.Polygon,
                    geometry: MvtCommandStream.Feature(outerRing, holeRing)),
            };
            var solid = new List<IFeature>
            {
                new DictionaryFeature(
                    Props("ring", 0.0), TileGeometryType.Polygon,
                    geometry: MvtCommandStream.Feature(outerRing)),
            };

            var paint = TestStyle.FillPaint(@"{""fill-color"":""#ffffff""}");

            Mesh holed = Track(TestTileMeshBuilder.BuildFill(withHole, paint, 0.0, Extent, Tile));
            Mesh full  = Track(TestTileMeshBuilder.BuildFill(solid,    paint, 0.0, Extent, Tile));
            // The same claim on production's clip branch (RingClipJob); the clip is inert here, so the area
            // must not move.
            Mesh holedOnTheClipBranch = Track(TestTileMeshBuilder.BuildFill(
                withHole, paint, 0.0, Extent, Tile, null, null, TileBufferClip.KeepTileUnits(0.0)));

            Assert.IsNotNull(full, "precondition: the solid square must produce geometry");
            Assert.IsNotNull(holed, "precondition: the holed square must produce geometry");

            double solidArea = TriangleArea(full);
            double holedArea = TriangleArea(holed);
            Assert.Greater(solidArea, 0.0, "precondition: the solid square covers a positive area");

            // Non-vacuity: the two fixtures really do differ, and by the amount the geometry says.
            // hole is 1000² of a 3000² outer ⇒ 1/9 removed ⇒ 8/9 remains.
            double expected  = solidArea * 8.0 / 9.0;
            double tolerance = solidArea * 0.01;
            Assert.Greater(math.abs(solidArea - holedArea), tolerance,
                "precondition: a hole this size must change the covered area, or the oracle is inert");

            Assert.AreEqual(expected, holedArea, tolerance,
                "the feature's SECOND ring must be read as the hole of its FIRST. A visit order that " +
                "reversed a feature's rings makes the hole establish the exterior sign, the real outer " +
                "fail its containment check, and the covered area collapse to the hole's 1/9 — a defect " +
                "measured to change 923 fill builds in this suite while every other test stayed green.");

            Assert.IsNotNull(holedOnTheClipBranch, "the clip-branch arm must produce geometry");
            Assert.AreEqual(expected, TriangleArea(holedOnTheClipBranch), tolerance,
                "the same feature built with the clip ENABLED — the production configuration — must cut " +
                "the same hole. RingClipJob walks the ring visit order through its own indirection, so " +
                "within-feature order is a separate claim on that branch.");
        }

        /// <summary>Total covered area of a mesh's triangles, in its own (world) units — an oracle derived
        /// from the vertices the GPU will draw, not from anything the pipeline reports about itself.</summary>
        private static double TriangleArea(Mesh mesh)
        {
            Vector3[] v = mesh.vertices;
            int[]     t = mesh.triangles;
            double    a = 0.0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                // Mercator at z0 is a flat XZ sheet, so the planar cross product in XZ is the area.
                Vector3 p0 = v[t[i]], p1 = v[t[i + 1]], p2 = v[t[i + 2]];
                a += math.abs((p1.x - p0.x) * (p2.z - p0.z) - (p2.x - p0.x) * (p1.z - p0.z)) * 0.5;
            }
            return a;
        }

        // ── the tile extent is ROUTED, not assumed ────────────────────────────────────────────────

        /// <summary>
        /// The fill write reads the extent off the <b>buffer</b>: built at extent 2048 and 4096 from the same
        /// tile fractions, the pattern-coordinate stream (stream 1) is identical. Only this test sees
        /// <c>WriteGeometry</c>'s <c>extentInv</c>: a literal 4096 doubles the 2048 arm's pattern coordinates
        /// while positions stay correct.
        /// </summary>
        [Test]
        public void PatternCoords_ComeFromTheBuffersOwnExtent_NotA4096Literal()
        {
            Mesh low  = Track(BuildQuadAtExtent(2048.0));
            Mesh high = Track(BuildQuadAtExtent(4096.0));

            Assert.IsNotNull(low, "the 2048 arm must produce geometry");
            Assert.IsNotNull(high, "the 4096 arm must produce geometry");
            Assert.AreEqual(high.vertexCount, low.vertexCount,
                "precondition: the two arms describe the same quad, so they triangulate identically");

            Vector2[] lowUv  = low.uv;
            Vector2[] highUv = high.uv;
            Assert.Greater(lowUv.Length, 0, "precondition: stream 1 (pattern coords) really was written");

            // Non-vacuity: the UVs are not all zero, so "identical" is a claim about real numbers.
            bool anyNonZero = false;
            foreach (Vector2 uv in lowUv) if (uv.sqrMagnitude > 0f) { anyNonZero = true; break; }
            Assert.IsTrue(anyNonZero, "precondition: at least one pattern coordinate is non-zero");

            for (int i = 0; i < lowUv.Length; i++)
                Assert.AreEqual(highUv[i], lowUv[i],
                    $"pattern coordinate {i} differs between extent 2048 and extent 4096 for the SAME " +
                    "fraction of the tile. The extent must come off the buffer; a 4096 literal makes the " +
                    "2048 arm exactly 2x.");
        }

        // ── the tile ADDRESS is routed too ────────────────────────────────────────────────────────

        /// <summary>
        /// The pattern stream's world span comes from <c>geometry.Tile</c>, the buffer's own address: the same
        /// quad at z0 and z1 gives pattern coordinates in exactly a 2:1 ratio. A second tile id beside the
        /// buffer, or a hard-coded zoom, collapses the ratio to 1:1. Extent and fractions are the same, so only
        /// the address differs.
        /// </summary>
        [Test]
        public void PatternCoords_ComeFromTheBuffersOwnTile_NotASecondTileId()
        {
            var z0 = new TileId { Z = 0, X = 0, Y = 0 };
            var z1 = new TileId { Z = 1, X = 0, Y = 0 };

            Mesh coarse = Track(BuildQuad(z0, Extent));
            Mesh fine   = Track(BuildQuad(z1, Extent));

            Assert.IsNotNull(coarse, "the z0 arm must produce geometry");
            Assert.IsNotNull(fine, "the z1 arm must produce geometry");
            Assert.AreEqual(coarse.vertexCount, fine.vertexCount,
                "precondition: the two arms describe the same tile-local quad, so they triangulate identically");

            Vector2[] coarseUv = coarse.uv;
            Vector2[] fineUv   = fine.uv;
            Assert.Greater(coarseUv.Length, 0, "precondition: stream 1 (pattern coords) really was written");

            // Non-vacuity: the two arms must actually DIFFER, or "ratio 2" is a statement about zeros.
            bool anyDifferent = false;
            for (int i = 0; i < coarseUv.Length; i++)
                if (coarseUv[i].x != fineUv[i].x || coarseUv[i].y != fineUv[i].y) { anyDifferent = true; break; }
            Assert.IsTrue(anyDifferent,
                "precondition: the two zooms must produce different pattern coordinates at all — if they " +
                "are identical the 2:1 assertion below is a statement about zeros.");

            // Exact, not approximate: the span is C / 2^z, so halving it is exact in binary floating point
            // and so is the resulting product. A tolerance here would let a nearly-right scale through.
            for (int i = 0; i < coarseUv.Length; i++)
            {
                Assert.AreEqual(coarseUv[i].x * 0.5f, fineUv[i].x, 0.0,
                    $"pattern coordinate {i}.x: a z1 tile spans exactly HALF the world units of a z0 tile, " +
                    "so its pattern coordinates must halve. Equal values mean the span was computed from " +
                    "something other than the buffer's own Tile.");
                Assert.AreEqual(coarseUv[i].y * 0.5f, fineUv[i].y, 0.0, $"pattern coordinate {i}.y");
            }
        }

        // ── Fixture ───────────────────────────────────────────────────────────────────────────────

        /// <summary>Builds a fill mesh from a buffer holding <paramref name="all"/>, where this layer selects
        /// only <paramref name="selectedOrdinals"/> — the production shape a plain feature-list harness cannot
        /// express.</summary>
        private static Mesh BuildFillFromSharedBuffer(
            IReadOnlyList<IFeature> all, IReadOnlyList<int> selectedOrdinals,
            Fill.PaintProperties paint, Fill.LayoutProperties layout, TileBufferClip clip = default)
        {
            var selected = new List<SelectedTileFeature>(selectedOrdinals.Count);
            foreach (int ordinal in selectedOrdinals)
                selected.Add(new SelectedTileFeature { Feature = all[ordinal], Ordinal = ordinal });

            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(all, Tile, Extent);
            var mda = Mesh.AllocateWritableMeshData(1);
            try
            {
                SyncMeshWrite.Fill(
                    mda[0], selected, geometry, paint, 0.0,
                    TileRenderOrigin.Project(Tile, null),
                    out int vertexCount, out Bounds bounds, null, layout, clip);
                return Finish(mda, vertexCount, bounds);
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>The tile's middle half, as a single-feature path geometry at the given extent — the same
        /// fraction of the tile at both extents, so every derived quantity that is a fraction (pattern
        /// coordinates, world positions) must agree.</summary>
        private static Mesh BuildQuadAtExtent(double extent) => BuildQuad(Tile, extent);

        /// <summary>The middle half of <paramref name="tile"/> at <paramref name="extent"/>, written through
        /// <c>WriteGeometry</c>. Both the address and the extent reach the write ONLY through the buffer —
        /// there is no second copy to pass, which is the property the two teeth above assert.</summary>
        private static Mesh BuildQuad(TileId tile, double extent)
        {
            double lo = extent * 0.25, hi = extent * 0.75;
            var paths = new[]
            {
                new[]
                {
                    new[]
                    {
                        new double2(lo, lo), new double2(hi, lo),
                        new double2(hi, hi), new double2(lo, hi),
                    },
                },
            };
            var kinds = new[] { TileGeometryType.Polygon };

            TileGeometryBuffers geometry =
                new PathGeometryMaterializer(tile, extent, kinds, paths).Materialize();
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var mda = Mesh.AllocateWritableMeshData(1);
            var featureColors = new NativeArray<Vector4>(1, Allocator.Persistent);
            featureColors[0]  = new Vector4(1f, 1f, 1f, 1f);
            try
            {
                SyncMeshWrite.FillGeometry(
                    mda[0], geometry, visitOrder, featureColors,
                    TileRenderOrigin.Project(tile, null), null, default,
                    out int vertexCount, out Bounds bounds);
                return Finish(mda, vertexCount, bounds);
            }
            finally
            {
                featureColors.Dispose();
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }

        private static Mesh Finish(Mesh.MeshDataArray mda, int vertexCount, Bounds bounds)
        {
            if (vertexCount == 0) { mda.Dispose(); return null; }
            var mesh = new Mesh { name = "B7aFill", indexFormat = IndexFormat.UInt32 };
            Mesh.ApplyAndDisposeWritableMeshData(
                mda, mesh, MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
            mesh.bounds = bounds;
            return mesh;
        }

        /// <summary>Each triangle's colour in RASTERIZATION order (the index buffer), deduped to one entry per
        /// contiguous run — the same oracle <c>FillSortKeyAndOpacityTests</c> uses, and for the same reason: a
        /// vertex-order oracle would keep passing if the pipeline ever regrouped its index emission.</summary>
        private static List<Color> PaintOrderColorRuns(Mesh mesh)
        {
            Color[] colors    = mesh.colors;
            int[]   triangles = mesh.triangles;
            var     runs      = new List<Color>();
            for (int t = 0; t < triangles.Length; t += 3)
            {
                Color c = colors[triangles[t]];
                if (runs.Count == 0 || runs[runs.Count - 1] != c) runs.Add(c);
            }
            return runs;
        }

        private static uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));

        /// <summary>One axis-aligned 100-unit square in MVT command form — the real decode path.</summary>
        private static DictionaryFeature Square(int x, int y, string name, double sortKey)
            => new DictionaryFeature(
                Props(name, sortKey), TileGeometryType.Polygon,
                geometry: new[]
                {
                    (1u << 3) | 1u, ZigZag(x),    ZigZag(y),
                    (3u << 3) | 2u, ZigZag(100),  ZigZag(0),
                                    ZigZag(0),    ZigZag(100),
                                    ZigZag(-100), ZigZag(0),
                    (1u << 3) | 7u,
                });

        /// <summary>A LineString whose ring has 4 points and a large signed area — so it is exactly the
        /// shape the area-based assembler would misread if a kind gate went missing.</summary>
        private static DictionaryFeature Line(int x, int y, string name, double sortKey)
            => new DictionaryFeature(
                Props(name, sortKey), TileGeometryType.LineString,
                geometry: new[]
                {
                    (1u << 3) | 1u, ZigZag(x),    ZigZag(y),
                    (3u << 3) | 2u, ZigZag(400),  ZigZag(0),
                                    ZigZag(0),    ZigZag(400),
                                    ZigZag(-400), ZigZag(0),
                });

        private static Dictionary<string, Value> Props(string name, double sortKey)
            => new Dictionary<string, Value>
            {
                ["name"] = Value.String(name),
                ["sk"]   = Value.Number(sortKey),
            };
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillSortKeyAndOpacityTests — fill-sort-key ordering and data-driven fill-opacity
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-feature fill behaviours that live in the MESH: <c>fill-sort-key</c> order on the INDEX buffer and
    /// data-driven <c>fill-opacity</c> on the COLOR stream. Features are synthetic
    /// (<see cref="DictionaryFeature"/>), because a fixture tile's feature order is an encoder accident.
    /// </summary>
    [TestFixture]
    public class FillSortKeyAndOpacityTests : BaseTestFixture
    {
        private const double Extent = 4096.0;

        private static uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));

        /// <summary>One axis-aligned square, in MVT command form: MoveTo(1) + LineTo(3) + ClosePath with
        /// zig-zag deltas — the same encoding the decoder emits, so this drives the real geometry path
        /// rather than a bypass.</summary>
        private static uint[] Square(int x, int y, int size) => new[]
        {
            (1u << 3) | 1u, ZigZag(x),     ZigZag(y),     // MoveTo (x, y)
            (3u << 3) | 2u, ZigZag(size),  ZigZag(0),     // LineTo +x
                            ZigZag(0),     ZigZag(size),  // LineTo +y
                            ZigZag(-size), ZigZag(0),     // LineTo -x
            (1u << 3) | 7u,                               // ClosePath (ring closes implicitly)
        };

        private static DictionaryFeature Feature(int x, int y, string name, double sortKey, double opacity = 1.0)
            => new DictionaryFeature(
                new Dictionary<string, Value>
                {
                    ["name"] = Value.String(name),
                    ["sk"]   = Value.Number(sortKey),
                    ["op"]   = Value.Number(opacity),
                },
                TileGeometryType.Polygon,
                geometry: Square(x, y, 100));

        /// <summary>
        /// The PAINT order read off the INDEX buffer: each triangle's colour in rasterization order, one entry
        /// per contiguous run. Not <c>mesh.colors</c>, whose vertex order only happens to match. Fills run
        /// <c>ZWrite Off</c> with <c>LEqual</c> (<c>BaseTweaker.ApplyBaseContract</c>), so the last coincident
        /// triangle drawn is on top.
        /// </summary>
        private static List<Color> PaintOrderColorRuns(Mesh mesh)
        {
            Color[] colors    = mesh.colors;
            int[]   triangles = mesh.triangles;
            var     runs      = new List<Color>();

            for (int t = 0; t < triangles.Length; t += 3)
            {
                Color c = colors[triangles[t]];
                if (runs.Count == 0 || runs[runs.Count - 1] != c) runs.Add(c);
            }
            return runs;
        }

        // ── fill-sort-key orders features within the layer ───────────────────────────────────────

        [Test]
        public void SortKey_HigherKeyIsEmittedLast_SoItDrawsOnTop()
        {
            // Declared order is the REVERSE of the sort order, so passing requires an actual
            // sort — not merely preserving input order.
            var features = new List<IFeature>
            {
                Feature(0, 0, "top",    sortKey: 10.0),
                Feature(0, 0, "middle", sortKey: 5.0),
                Feature(0, 0, "bottom", sortKey: 1.0),
            };

            // Distinct colours per feature via a data-driven expression keyed on `name`, so the rasterization
            // order read off the index buffer identifies which feature paints when.
            var paint = TestStyle.FillPaint(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""bottom"", ""#ff0000"", ""middle"", ""#00ff00"", ""top"", ""#0000ff"", ""#ffffff""]}");
            var layout = TestStyle.FillLayout(@"{""fill-sort-key"": [""get"", ""sk""]}");

            Mesh mesh = Track(TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }, null, layout));
            Assert.IsNotNull(mesh, "the stub polygons must produce geometry");
            var runs = PaintOrderColorRuns(mesh);
            Assert.AreEqual(3, runs.Count, "expected one colour run per feature");

            // Ascending sort key ⇒ bottom (1) first, top (10) last. Later == painted on top.
            Assert.Greater(runs[0].r, 0.5f, "lowest sort key (red 'bottom') must be emitted FIRST");
            Assert.Greater(runs[1].g, 0.5f, "middle sort key (green) must be emitted second");
            Assert.Greater(runs[2].b, 0.5f,
                "highest sort key (blue 'top') must be emitted LAST so it draws on top — if this is red, " +
                "fill-sort-key is being ignored and declared order survived.");
        }

        [Test]
        public void SortKey_EqualKeys_PreserveDeclaredOrder()
        {
            // Stability tooth: Array.Sort is an introsort and is NOT stable, so equal keys would otherwise
            // shuffle arbitrarily. The spec's implicit order for ties is the declared one.
            var features = new List<IFeature>
            {
                Feature(0, 0, "first",  sortKey: 4.0),
                Feature(0, 0, "second", sortKey: 4.0),
            };
            var paint = TestStyle.FillPaint(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""first"", ""#ff0000"", ""second"", ""#00ff00"", ""#ffffff""]}");
            var layout = TestStyle.FillLayout(@"{""fill-sort-key"": [""get"", ""sk""]}");

            Mesh mesh = Track(TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }, null, layout));
            var runs = PaintOrderColorRuns(mesh);
            Assert.AreEqual(2, runs.Count);
            Assert.Greater(runs[0].r, 0.5f, "equal sort keys must keep DECLARED order ('first' stays first)");
            Assert.Greater(runs[1].g, 0.5f);
        }

        [Test]
        public void SortKey_Absent_LeavesDeclaredOrderAndMeshUntouched()
        {
            // The byte-identity guarantee: no sort key ⇒ no reorder, so every pre-existing fill mesh (and
            // every snapshot baked from one) is unaffected by it.
            var features = new List<IFeature>
            {
                Feature(0, 0, "first",  sortKey: 99.0), // key present in the DATA but not referenced by the style
                Feature(0, 0, "second", sortKey: 1.0),
            };
            var paint = TestStyle.FillPaint(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""first"", ""#ff0000"", ""second"", ""#00ff00"", ""#ffffff""]}");

            Mesh withoutLayout = Track(TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }));
            Mesh withDefaultLayout = Track(TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }, null,
                TestStyle.FillLayout()));
            var noLayout  = PaintOrderColorRuns(withoutLayout);
            var defLayout = PaintOrderColorRuns(withDefaultLayout);

            Assert.Greater(noLayout[0].r, 0.5f, "declared order must survive when no fill-sort-key is set");
            CollectionAssert.AreEqual(noLayout, defLayout,
                "a layout object with no fill-sort-key must be indistinguishable from no layout at all");
            Assert.AreEqual(withoutLayout.vertexCount, withDefaultLayout.vertexCount);
        }

        /// <summary>
        /// <c>fill-sort-key</c> reorders the GEOMETRY and the COLOURS <b>together</b>, so each colour stays on its
        /// own polygon. The other sort-key cases stack identical squares, where a desync changes nothing; this
        /// is the only fixture with distinct positions AND a live sort key.
        /// </summary>
        [Test]
        public void SortKey_ReordersGeometryAndColoursTogether_SoEachColourKeepsItsOwnPolygon()
        {
            // Declared order is the REVERSE of sort order (so a sort really happens), and the three squares
            // sit at increasing tile-local x (so each colour is identifiable by WHERE it landed).
            var features = new List<IFeature>
            {
                Feature(1000, 1000, "far",  sortKey: 3.0),
                Feature(500,  500,  "mid",  sortKey: 2.0),
                Feature(0,    0,    "near", sortKey: 1.0),
            };
            var paint = TestStyle.FillPaint(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""near"", ""#ff0000"", ""mid"", ""#00ff00"", ""far"", ""#0000ff"", ""#ffffff""]}");
            var layout = TestStyle.FillLayout(@"{""fill-sort-key"": [""get"", ""sk""]}");

            Mesh mesh = Track(TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }, null, layout));
            Assert.IsNotNull(mesh, "the three squares must produce geometry");
            Vector3[] verts  = mesh.vertices;
            Color[]   colors = mesh.colors;
            Assert.AreEqual(verts.Length, colors.Length, "precondition: one colour per vertex");

            // Tile-local x grows eastward, and so does world x — so the three colour groups must appear
            // in the same left-to-right order as the squares were authored, whatever the sort did.
            double nearX = MeanXOfColor(verts, colors, c => c.r > 0.5f && c.g < 0.5f && c.b < 0.5f);
            double midX  = MeanXOfColor(verts, colors, c => c.g > 0.5f && c.r < 0.5f && c.b < 0.5f);
            double farX  = MeanXOfColor(verts, colors, c => c.b > 0.5f && c.r < 0.5f && c.g < 0.5f);

            // Non-vacuity: all three colours must actually be present, or the ordering below is vacuous.
            Assert.IsFalse(double.IsNaN(nearX), "the RED ('near') polygon must be in the mesh");
            Assert.IsFalse(double.IsNaN(midX),  "the GREEN ('mid') polygon must be in the mesh");
            Assert.IsFalse(double.IsNaN(farX),  "the BLUE ('far') polygon must be in the mesh");

            Assert.Less(nearX, midX,
                "the RED colour must land on the square authored at tile x=0 — if geometry and colours " +
                "were sorted independently, red would sit on the FARTHEST square instead");
            Assert.Less(midX, farX,
                "…and GREEN on the middle square, BLUE on the farthest: this pins WHICH polygon each " +
                "colour landed on, not just that all three colours exist");
        }

        /// <summary>Mean world x of the vertices whose colour matches <paramref name="match"/>, or
        /// <c>NaN</c> when that colour is absent (which the caller asserts against).</summary>
        private static double MeanXOfColor(Vector3[] verts, Color[] colors, System.Func<Color, bool> match)
        {
            double sum = 0.0;
            int    n   = 0;
            for (int i = 0; i < verts.Length; i++)
                if (match(colors[i])) { sum += verts[i].x; n++; }
            return n == 0 ? double.NaN : sum / n;
        }

        /// <summary>
        /// A Polygon with a null command stream still takes an ordinal (the materializer reads it as zero
        /// commands) and contributes no ring. The per-feature colour list, built in the same loop, must stay
        /// index-aligned, or every later feature paints its neighbour's colour.
        /// </summary>
        [Test]
        public void NullGeometryPolygon_TakesAnOrdinal_WithoutShiftingItsNeighboursColours()
        {
            var features = new List<IFeature>
            {
                Feature(0, 0, "first", sortKey: 0.0),
                new DictionaryFeature(
                    new Dictionary<string, Value> { ["name"] = Value.String("gap") },
                    TileGeometryType.Polygon,
                    geometry: null),                     // a Polygon with NO geometry, between the two drawn ones
                Feature(500, 500, "second", sortKey: 0.0),
            };
            var paint = TestStyle.FillPaint(@"{""fill-color"": [""match"", [""get"", ""name""],
                ""first"", ""#ff0000"", ""gap"", ""#00ff00"", ""second"", ""#0000ff"", ""#ffffff""]}");

            Mesh mesh = Track(TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }));
            Assert.IsNotNull(mesh, "the two real polygons must still produce geometry");
            var runs = PaintOrderColorRuns(mesh);

            Assert.AreEqual(2, runs.Count,
                "exactly two colour runs — the geometry-less feature contributes no triangles");
            Assert.Greater(runs[0].r, 0.5f,
                "the first polygon must still paint RED; green here means the gap feature's colour " +
                "slid onto its neighbour (the colour list desynced from the feature ordinals)");
            Assert.Greater(runs[1].b, 0.5f,
                "the polygon AFTER the gap must still paint BLUE — an off-by-one would paint it green");
        }

        // ── Data-driven fill-opacity bakes into the COLOR stream's alpha ─────────────────────────

        [Test]
        public void DataDrivenOpacity_BakesDistinctPerFeatureAlpha()
        {
            var features = new List<IFeature>
            {
                Feature(0,   0, "a", sortKey: 0.0, opacity: 0.25),
                Feature(200, 0, "b", sortKey: 0.0, opacity: 0.75),
            };
            var paint = TestStyle.FillPaint(@"{""fill-color"": ""#ffffff"", ""fill-opacity"": [""get"", ""op""]}");

            Assert.IsTrue(paint.Opacity.DependsOnFeature, "precondition: this expression must be data-driven");

            Mesh mesh = Track(TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }));
            var alphas = new HashSet<float>();
            foreach (Color c in mesh.colors) alphas.Add(Mathf.Round(c.a * 100f) / 100f);

            // If a data-driven fill-opacity silently reverted to the default, every vertex would
            // carry alpha 1 and this set would be {1.00}.
            CollectionAssert.AreEquivalent(new[] { 0.25f, 0.75f }, alphas,
                "each feature's evaluated fill-opacity must be baked into its vertices' alpha. " +
                "A single value of 1.00 means the data-driven opacity was dropped.");
        }

        [Test]
        public void ConstantOpacity_IsNotBaked_SoTheUniformStaysTheSingleSource()
        {
            // The double-apply guard's other half: a constant/zoom opacity rides the _Opacity uniform, so it
            // must NOT also appear in vertex alpha or the shader would multiply it in twice.
            var features = new List<IFeature> { Feature(0, 0, "a", sortKey: 0.0) };
            var paint = TestStyle.FillPaint(@"{""fill-color"": ""#ffffff"", ""fill-opacity"": 0.5}");

            Assert.IsFalse(paint.Opacity.DependsOnFeature, "precondition: constant opacity");

            Mesh mesh = Track(TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }));
            foreach (Color c in mesh.colors)
                Assert.AreEqual(1f, c.a, 1e-4,
                    "a CONSTANT fill-opacity must stay on the _Opacity uniform and leave vertex alpha at " +
                    "fill-color's own alpha — baking it too would double-apply it in the fragment.");
        }

        [Test]
        public void DataDrivenOpacity_IsTheStreamsOnlyAlphaCarrier()
        {
            // A CONSTANT fill-color rides _BaseColor (PaintColorSingleApplyTests), so the COLOR stream here
            // carries only the data-driven fill-opacity.
            var features = new List<IFeature> { Feature(0, 0, "a", sortKey: 0.0, opacity: 0.5) };
            var paint = TestStyle.FillPaint(@"{""fill-color"": ""rgba(255,255,255,0.4)"", ""fill-opacity"": [""get"", ""op""]}");

            Mesh mesh = Track(TestTileMeshBuilder.BuildFill(
                features, paint, zoom: 0.0, Extent, new TileId { Z = 0, X = 0, Y = 0 }));
            foreach (Color c in mesh.colors)
                Assert.AreEqual(0.5f, c.a, 1e-3,
                    "fill-color is constant, so the stream carries only the data-driven fill-opacity " +
                    "(0.5) — its own alpha (0.4) now rides _BaseColor.a, not the stream.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeSubdivisionJobParityTests — Unity-only source-of-truth parity tooth
    // ───────────────────────────────────────────────────────────────────────────────────

    public class GlobeSubdivisionJobParityTests
    {
        private const double Extent = 4096;
        private static readonly double R = EarthConstants.A;

        private static byte[] LoadFixture(string name)
        {
            string dir = Directory.GetCurrentDirectory();
            for (int i = 0; i < 16 && dir != null; i++)
            {
                string p = Path.Combine(dir, "Assets", "Fixtures", name);
                if (File.Exists(p)) return File.ReadAllBytes(p);
                dir = Directory.GetParent(dir)?.FullName;
            }
            throw new FileNotFoundException(name);
        }

        // Root triangles come from EarcutJobGatherHarness.BuildEarcutRootsFromFillGraph, whose doc says why
        // extraction runs under a flat projection and why the feature index must be the real one.

        // -----------------------------------------------------------------------------------------------
        // Runs the REAL Burst job and the managed mirror over the SAME input and asserts identical emitted
        // streams (both traverse LIFO); returns both so a caller can run the gap analysis on each.
        // -----------------------------------------------------------------------------------------------

        private readonly struct ParityRun
        {
            public readonly List<SubdivisionCoverageValidator.RootTri> Roots;
            public readonly List<SubdivisionCoverageValidator.LeafRef> MirrorLeaves;
            public readonly int MaxDepthReached;
            public readonly bool BudgetFired;
            public readonly double3[] RealWorld;
            public readonly double2[] RealTile;
            // OutIndices.Length is the EMITTED count and OutVerts.Length the UNIQUE count, so a caller can
            // assert sharing without re-scheduling.
            public readonly int EmittedCount;
            public readonly int UniqueVertexCount;

            public ParityRun(
                List<SubdivisionCoverageValidator.RootTri> roots, List<SubdivisionCoverageValidator.LeafRef> mirrorLeaves,
                int maxDepthReached, bool budgetFired,
                double3[] realWorld, double2[] realTile, int emittedCount, int uniqueVertexCount)
            {
                Roots = roots; MirrorLeaves = mirrorLeaves;
                MaxDepthReached = maxDepthReached; BudgetFired = budgetFired;
                RealWorld = realWorld; RealTile = realTile;
                EmittedCount = emittedCount; UniqueVertexCount = uniqueVertexCount;
            }
        }

        private static ParityRun AssertOrderedParity(
            IProjection proj, TileId id, double extent, double3 origin,
            double2[] tileVerts, int[] triangleIndices, int[] vertexFeatureIdx, int srcVertCount, int srcIndexCount,
            double maxEdgeAngleRad, int maxDepth, int maxOutputVertices)
        {
            var nativeVerts = new NativeList<double2>(tileVerts.Length, Allocator.Persistent);
            nativeVerts.CopyFrom(tileVerts);
            var nativeTris = new NativeList<int>(triangleIndices.Length, Allocator.Persistent);
            nativeTris.CopyFrom(triangleIndices);
            var nativeFeat = new NativeList<int>(vertexFeatureIdx.Length, Allocator.Persistent);
            nativeFeat.CopyFrom(vertexFeatureIdx);
            var nativeBand = new NativeList<float3>(tileVerts.Length, Allocator.Persistent);
            nativeBand.Resize(tileVerts.Length, NativeArrayOptions.ClearMemory);
            var outVerts = new NativeList<GlobeFillVertex>(64, Allocator.Persistent);
            var outIndices = new NativeList<int>(64, Allocator.Persistent);
            try
            {
                // Explicit constants at every call site. srcVertCount/srcIndexCount are unused by Schedule but
                // still read by the mirror/roots build below.
                JobHandle handle = GlobeFillSubdivideDispatch.Schedule(
                    proj, nativeVerts, nativeTris, nativeFeat, nativeBand, id, extent, origin,
                    maxEdgeAngleRad, maxDepth, maxOutputVertices,
                    GlobeFillSubdivideDispatch.DefaultMaxTotalVertices, outVerts, outIndices, default);
                JobHandle.ScheduleBatchedJobs();
                handle.Complete();

                var roots = SubdivisionCoverageValidator.BuildRootsFromRaw(
                    tileVerts, triangleIndices, vertexFeatureIdx, srcVertCount, srcIndexCount);
                SubdivisionCoverageValidator.RunManagedMirror(
                    roots, id, proj, extent, origin, maxEdgeAngleRad, maxDepth, maxOutputVertices,
                    out var mirrorLeaves, out int maxDepthReached, out bool budgetFired);

                // Sharing shrinks storage, not emission: the EMITTED count matches the mirror's leaves 1:1, and
                // de-indexing outVerts[outIndices[i]] rebuilds the emitted stream.
                Assert.AreEqual(mirrorLeaves.Count, outIndices.Length,
                    "emitted-vertex count must match (same LIFO traversal) — sharing changes STORAGE, not emission");
                Assert.LessOrEqual(outVerts.Length, outIndices.Length,
                    "sharing can only reduce or preserve unique storage, never exceed the emitted count");
                var referenced = new bool[outVerts.Length];
                for (int i = 0; i < outIndices.Length; i++)
                {
                    int idx = outIndices[i];
                    Assert.GreaterOrEqual(idx, 0, $"OutIndices[{i}]: must reference a valid vertex");
                    Assert.Less(idx, outVerts.Length, $"OutIndices[{i}]: must reference a valid vertex");
                    referenced[idx] = true;
                }
                // No orphan slots: every unique vertex is referenced by at least one emitted index; a slot the
                // map failed to record would be orphaned.
                for (int k = 0; k < referenced.Length; k++)
                    Assert.IsTrue(referenced[k], $"OutVerts[{k}]: unreferenced — every unique slot must be used");

                var realWorld = new double3[outIndices.Length];
                var realTile = new double2[outIndices.Length];
                for (int i = 0; i < mirrorLeaves.Count; i++)
                {
                    SubdivisionCoverageValidator.LeafRef m = mirrorLeaves[i];
                    GlobeFillVertex real = outVerts[outIndices[i]]; // de-indexed: the vertex THIS emitted position resolves to
                    realWorld[i] = real.World;
                    realTile[i] = real.Tile;

                    Assert.AreEqual(m.Tile.x, real.Tile.x, 0.0, $"vertex {i}: Tile.x exact (same Mid() arithmetic)");
                    Assert.AreEqual(m.Tile.y, real.Tile.y, 0.0, $"vertex {i}: Tile.y exact (same Mid() arithmetic)");
                    Assert.AreEqual(m.Feature, real.Feature, $"vertex {i}: Feature exact (first-index pick)");

                    Assert.AreEqual(m.World.x, real.World.x, R * 1e-9, $"vertex {i}: World.x");
                    Assert.AreEqual(m.World.y, real.World.y, R * 1e-9, $"vertex {i}: World.y");
                    Assert.AreEqual(m.World.z, real.World.z, R * 1e-9, $"vertex {i}: World.z");

                    Assert.AreEqual(m.Up.x, real.Up.x, 1e-9, $"vertex {i}: Up.x");
                    Assert.AreEqual(m.Up.y, real.Up.y, 1e-9, $"vertex {i}: Up.y");
                    Assert.AreEqual(m.Up.z, real.Up.z, 1e-9, $"vertex {i}: Up.z");

                    Assert.AreEqual(m.East.x, real.East.x, 1e-9, $"vertex {i}: East.x");
                    Assert.AreEqual(m.East.y, real.East.y, 1e-9, $"vertex {i}: East.y");
                    Assert.AreEqual(m.East.z, real.East.z, 1e-9, $"vertex {i}: East.z");
                }

                return new ParityRun(
                    roots, mirrorLeaves, maxDepthReached, budgetFired, realWorld, realTile,
                    emittedCount: outIndices.Length, uniqueVertexCount: outVerts.Length);
            }
            finally
            {
                nativeVerts.Dispose(); nativeTris.Dispose(); nativeFeat.Dispose();
                outVerts.Dispose(); outIndices.Dispose();
            }
        }

        // -----------------------------------------------------------------------------------------------
        // Corpus artefact tile: ordered-stream parity, and the real job's MaxGapMeters (paired with the
        // mirror's lineage by output order) must match the mirror's own report.
        // -----------------------------------------------------------------------------------------------

        [Test]
        public void Corpus_Water_6_32_20_Globe_OrderedParityAndGapCorollary()
        {
            var id = new TileId { Z = 6, X = 32, Y = 20 };
            var proj = new SphericalProjection();
            // Decoded by the production MvtDecoder; parity needs only identical input to both arms.
            var (tileVerts, triangleIndices, vertexFeatureIdx, extent) =
                EarcutJobGatherHarness.BuildEarcutRootsFromFillGraph(LoadFixture("water-6-32-20.pbf.bytes"), "water", id);

            ParityRun run = AssertOrderedParity(
                proj, id, extent, new double3(0, 0, 0), tileVerts, triangleIndices, vertexFeatureIdx,
                tileVerts.Length, triangleIndices.Length,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);

            // Hybrid stream: real job's World/Tile, mirror's lineage (reconstructed by ordered pairing).
            var hybridLeaves = new List<SubdivisionCoverageValidator.LeafRef>(run.MirrorLeaves.Count);
            for (int i = 0; i < run.MirrorLeaves.Count; i++)
            {
                SubdivisionCoverageValidator.LeafRef m = run.MirrorLeaves[i];
                hybridLeaves.Add(new SubdivisionCoverageValidator.LeafRef(
                    run.RealWorld[i], m.Up, m.East, run.RealTile[i], m.Feature, m.RootIndex, m.Depth));
            }

            var hybridReport = SubdivisionCoverageValidator.AnalyzeLeafStream(
                run.Roots, hybridLeaves, run.MaxDepthReached, run.BudgetFired, id, proj, extent);
            var mirrorReport = SubdivisionCoverageValidator.AnalyzeLeafStream(
                run.Roots, run.MirrorLeaves, run.MaxDepthReached, run.BudgetFired, id, proj, extent);

            Assert.AreEqual(mirrorReport.MaxGapMeters, hybridReport.MaxGapMeters, mirrorReport.MaxGapMeters * 1e-6 + 1e-6,
                $"real job's own gap analysis must match the mirror's: mirror={mirrorReport.Summary} hybrid={hybridReport.Summary}");
        }

        // -----------------------------------------------------------------------------------------------
        // The z0 "countries" tile, extracted with SuppressBoundaryBand = true so degenerate band quads do
        // not inflate the mirror's roots; same parity and gap checks as above, plus the sharing ratio.
        // -----------------------------------------------------------------------------------------------

        [Test]
        public void Corpus_Countries_Z0_Globe_OrderedParityAndGapCorollaryAndVertexSharingRatio()
        {
            var id = new TileId { Z = 0, X = 0, Y = 0 };
            var proj = new SphericalProjection();
            // The REAL per-source-feature index, not all zeros; the fence below does not catch zeros (see its
            // Limitation), so use real input anyway.
            var (tileVerts, triangleIndices, vertexFeatureIdx, extent) =
                EarcutJobGatherHarness.BuildEarcutRootsFromFillGraph(LoadFixture("sample-tile.bytes"), "countries", id);

            ParityRun run = AssertOrderedParity(
                proj, id, extent, new double3(0, 0, 0), tileVerts, triangleIndices, vertexFeatureIdx,
                tileVerts.Length, triangleIndices.Length,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);

            var hybridLeaves = new List<SubdivisionCoverageValidator.LeafRef>(run.MirrorLeaves.Count);
            for (int i = 0; i < run.MirrorLeaves.Count; i++)
            {
                SubdivisionCoverageValidator.LeafRef m = run.MirrorLeaves[i];
                hybridLeaves.Add(new SubdivisionCoverageValidator.LeafRef(
                    run.RealWorld[i], m.Up, m.East, run.RealTile[i], m.Feature, m.RootIndex, m.Depth));
            }

            var hybridReport = SubdivisionCoverageValidator.AnalyzeLeafStream(
                run.Roots, hybridLeaves, run.MaxDepthReached, run.BudgetFired, id, proj, extent);
            var mirrorReport = SubdivisionCoverageValidator.AnalyzeLeafStream(
                run.Roots, run.MirrorLeaves, run.MaxDepthReached, run.BudgetFired, id, proj, extent);

            Assert.AreEqual(mirrorReport.MaxGapMeters, hybridReport.MaxGapMeters, mirrorReport.MaxGapMeters * 1e-6 + 1e-6,
                $"real job's own gap analysis must match the mirror's: mirror={mirrorReport.Summary} hybrid={hybridReport.Summary}");

            TestContext.WriteLine($"countries z0 (Burst arm, SuppressBoundaryBand=true): " +
                $"emitted={run.EmittedCount} unique={run.UniqueVertexCount}");

            // A fence with headroom around the measured sharing. Limitation: an all-zero vertexFeatureIdx still
            // stays above the 35,000 lower bound, so the fence misses Feature dropped from the merge key.
            Assert.Less(run.UniqueVertexCount, 55_000,
                $"sharing must collapse the countries z0 tile's unique vertex count well below its emitted " +
                $"count: emitted={run.EmittedCount} unique={run.UniqueVertexCount}");
            Assert.Greater(run.UniqueVertexCount, 35_000,
                $"the fence's lower bound guards a vertex key that over-merges (e.g. dropping Feature): " +
                $"emitted={run.EmittedCount} unique={run.UniqueVertexCount}");
            Assert.Less(run.UniqueVertexCount, run.EmittedCount,
                "a genuinely curved z0 tile must have SOME shared conforming split-edge midpoints");
        }


        // -----------------------------------------------------------------------------------------------
        // Discriminating crafted cases — each fires a path the shallow (depth-1, no-budget) water corpus
        // under-exercises, and each is proven by the SAME ordered-stream equality.
        // -----------------------------------------------------------------------------------------------

        [Test]
        public void Discriminating_MaxDepthStop_WholeGlobeTriangleAtDefaultDepth()
        {
            var tileVerts = new[] { new double2(0, 0), new double2(Extent, 0), new double2(0, Extent) };
            var triangleIndices = new[] { 0, 1, 2 };
            var vertexFeatureIdx = new[] { 0, 0, 0 };

            AssertOrderedParity(
                new SphericalProjection(), new TileId { Z = 0, X = 0, Y = 0 }, Extent, new double3(0, 0, 0),
                tileVerts, triangleIndices, vertexFeatureIdx, 3, 3,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);
        }

        [Test]
        public void Discriminating_BudgetFiring_TinyBudgetMidTraversal()
        {
            var tileVerts = new[] { new double2(0, 0), new double2(Extent, 0), new double2(0, Extent) };
            var triangleIndices = new[] { 0, 1, 2 };
            var vertexFeatureIdx = new[] { 0, 0, 0 };

            ParityRun run = AssertOrderedParity(
                new SphericalProjection(), new TileId { Z = 0, X = 0, Y = 0 }, Extent, new double3(0, 0, 0),
                tileVerts, triangleIndices, vertexFeatureIdx, 3, 3,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, 8, 2000); // depth 8 unbounded would explode; budget 2000 must fire

            Assert.IsTrue(run.BudgetFired, "the tiny budget must actually fire on this crafted case (mirror side)");
        }

        [Test]
        public void Discriminating_NonZeroOrigin_WorldIsOriginRelativeOnBothSides()
        {
            var tileVerts = new[] { new double2(0, 0), new double2(Extent, 0), new double2(0, Extent) };
            var triangleIndices = new[] { 0, 1, 2 };
            var vertexFeatureIdx = new[] { 0, 0, 0 };
            var origin = new double3(R * 0.3, R * 0.1, -R * 0.2); // an arbitrary non-zero render-space origin

            AssertOrderedParity(
                new SphericalProjection(), new TileId { Z = 3, X = 3, Y = 3 }, Extent, origin,
                tileVerts, triangleIndices, vertexFeatureIdx, 3, 3,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);
        }

        [Test]
        public void Discriminating_MultipleFeatureIds_PropagatePerFirstIndexOnBothSides()
        {
            // Two disjoint triangles with DIFFERENT feature ids on their first index — the job/mirror both
            // pick feature[triangleIndices[3*t]] per triangle (GlobeFillSubdivider.cs:63-64).
            var tileVerts = new[]
            {
                new double2(0, 0), new double2(Extent * 0.4, 0), new double2(0, Extent * 0.4),           // tri 0
                new double2(Extent * 0.6, Extent * 0.6), new double2(Extent, Extent * 0.6), new double2(Extent * 0.6, Extent), // tri 1
            };
            var triangleIndices = new[] { 0, 1, 2, 3, 4, 5 };
            var vertexFeatureIdx = new[] { 7, 7, 7, 42, 42, 42 };

            AssertOrderedParity(
                new SphericalProjection(), new TileId { Z = 4, X = 5, Y = 5 }, Extent, new double3(0, 0, 0),
                tileVerts, triangleIndices, vertexFeatureIdx, tileVerts.Length, triangleIndices.Length,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);
        }

        // -----------------------------------------------------------------------------------------------
        // Vertex sharing: conforming split-edge midpoints must actually merge.
        // -----------------------------------------------------------------------------------------------

        [Test]
        public void Discriminating_ConformingMidpointsMerge_Z2Quad_UniqueCountMatchesMirrorsDistinctTileFeatureCount()
        {
            // The z2/0/0 whole-tile square (depth 5), split by hand along its diagonal. Mid() is order-symmetric,
            // so each root computes the shared-diagonal midpoints bit-identically and the key must merge them.
            double2[] tileVerts =
            {
                new double2(0, 0), new double2(Extent, 0), new double2(Extent, Extent), new double2(0, Extent),
            };
            int[] triangleIndices = { 0, 1, 2, 0, 2, 3 };
            int[] vertexFeatureIdx = new int[tileVerts.Length];

            var id = new TileId { Z = 2, X = 0, Y = 0 };
            ParityRun run = AssertOrderedParity(
                new SphericalProjection(), id, Extent, new double3(0, 0, 0),
                tileVerts, triangleIndices, vertexFeatureIdx, tileVerts.Length, triangleIndices.Length,
                GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad, GlobeFillSubdivideDispatch.DefaultMaxDepth,
                GlobeFillSubdivideDispatch.DefaultMaxInteriorVertices);

            // Independent oracle: distinct (Tile, Feature) pairs in the mirror's leaves. World/Up/East are pure
            // functions of Tile, so this equals the whole-struct key count: fewer is a collision, more a miss.
            var distinct = new HashSet<(ulong, ulong, int)>();
            foreach (SubdivisionCoverageValidator.LeafRef leaf in run.MirrorLeaves)
                distinct.Add((math.asulong(leaf.Tile.x), math.asulong(leaf.Tile.y), leaf.Feature));

            Assert.AreEqual(distinct.Count, run.UniqueVertexCount,
                $"unique emitted vertices ({run.UniqueVertexCount}) must equal the distinct (Tile,Feature) " +
                $"bit-patterns the mirror's own leaf stream carries ({distinct.Count}) — every conforming " +
                "split-edge midpoint must actually merge");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeSubdivisionTests — globe-fill subdivision testbench
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Globe-fill SUBDIVISION testbench (mesh-triangulation-robustness-design.md), driving <see cref="SubdivisionCoverageValidator"/>.
    /// The 3 real-tile tests re-home onto the REAL jobified fill path via
    /// <see cref="EarcutJobGatherHarness.BuildEarcutRootsFromFillGraph"/>; the 2 synthetic tests earcut
    /// via <see cref="EarcutJobPolygonRunner"/>.
    /// </summary>
    public class GlobeSubdivisionTests
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

        private static readonly TileId Water63220 = new TileId { Z = 6, X = 32, Y = 20 };

        /// <summary>Gets one fixture/layer's earcut-only tile-space root triangles via
        /// <see cref="EarcutJobGatherHarness.BuildEarcutRootsFromFillGraph"/> (see its doc for why
        /// extraction always runs non-curved), then runs <paramref name="mirrorProjection"/>'s own
        /// managed-mirror subdivision over them.</summary>
        private static SubdivisionCoverageValidator.Report RunOnBurstArm(
            byte[] mvtBytes, string layerName, in TileId id, IProjection mirrorProjection)
        {
            var (tileVerts, triangleIndices, vertexFeatureIdx, extent) =
                EarcutJobGatherHarness.BuildEarcutRootsFromFillGraph(mvtBytes, layerName, id);
            return SubdivisionCoverageValidator.ValidateTriangulation(
                tileVerts, triangleIndices, vertexFeatureIdx, id, mirrorProjection, extent);
        }

        // ---- always-on guard — Mercator is a pass-through no-op ("Mercator moves zero pixels") ----

        [Test]
        public void Mercator_IsPassThrough_NoOp()
        {
            var mvt = LoadFixture("water-6-32-20.pbf.bytes");
            var rep = RunOnBurstArm(mvt, "water", Water63220, new WebMercatorProjection());

            Assert.AreEqual(0, rep.MaxDepthReached, "a constant-up projection must never subdivide: " + rep.Summary);
            Assert.IsFalse(rep.Subdivided, "Mercator subdivided — invariant broken: " + rep.Summary);
            Assert.AreEqual(rep.EarcutTriangles, rep.SubTriangles, "no-op ⇒ sub tris == earcut tris: " + rep.Summary);
            Assert.Less(rep.CoverageAreaRelError, 1e-9, "no-op ⇒ area identical: " + rep.Summary);
            Assert.Less(rep.MaxGapFracTile, 1e-9, "no-op ⇒ zero gap: " + rep.Summary);
            Assert.AreEqual(0, rep.TJunctions, "no-op ⇒ no midpoints inserted ⇒ no T-junctions: " + rep.Summary);
        }

        // ---- always-on guard — the validator itself is correct on clean synthetic globe input -----------

        [Test]
        public void CleanSyntheticGlobeSquare_IsConforming()
        {
            // A whole-tile square at the corpus tile z6/32/20: both triangles reach the SAME depth, so unlike the
            // water corpus there is no depth mismatch.
            var outer = new List<double2>
            {
                new double2(0, 0), new double2(4096, 0), new double2(4096, 4096), new double2(0, 4096),
            };
            var res = EarcutJobPolygonRunner.Run(outer);
            var vertexFeatureIdx = new int[res.Vertices.Length];

            var rep = SubdivisionCoverageValidator.ValidateTriangulation(
                res.Vertices, res.Indices, vertexFeatureIdx, Water63220, new SphericalProjection(), extent: 4096);

            Assert.AreEqual(0, rep.FlippedTris, "clean square must not fold: " + rep.Summary);
            Assert.AreEqual(0, rep.DegenerateTris, "clean square must not collapse: " + rep.Summary);
            Assert.IsTrue(rep.Passes(), "clean synthetic globe input must pass: " + rep.Summary);
        }

        // ---- The artefact tile must subdivide with no T-junction gap ----------------------------------------
        // A non-conforming per-triangle 1→4 split fails here on the gap clause alone, far over the 0.05% gate.
        [Test]
        public void Corpus_Water_6_32_20_Globe_SubdivisionIsConforming()
        {
            var mvt = LoadFixture("water-6-32-20.pbf.bytes");
            var rep = RunOnBurstArm(mvt, "water", Water63220, new SphericalProjection());

            Assert.IsTrue(rep.Passes(), "globe fill subdivision has a visible T-junction crack: " + rep.Summary);
            Assert.IsFalse(rep.BudgetFired, "a conforming result must not have relied on the Budget cutoff: " + rep.Summary);
        }

        // ---- DEEP conformity: the z2/0/0 quad reaches depth 5, where the other guards stop at 1 ------------
        // It covers both cross-parent (shared diagonal) and deep intra-parent T-junctions.
        [Test]
        public void Synthetic_Deep_Z2_Quad_Globe_SubdivisionIsConforming()
        {
            var outer = new List<double2>
            {
                new double2(0, 0), new double2(4096, 0), new double2(4096, 4096), new double2(0, 4096),
            };
            var res = EarcutJobPolygonRunner.Run(outer);
            var vertexFeatureIdx = new int[res.Vertices.Length];

            var rep = SubdivisionCoverageValidator.ValidateTriangulation(
                res.Vertices, res.Indices, vertexFeatureIdx, new TileId { Z = 2, X = 0, Y = 0 }, new SphericalProjection(), extent: 4096);

            Assert.IsTrue(rep.Passes(), "deep (z2, depth>=2) globe subdivision has a T-junction crack: " + rep.Summary);
            Assert.IsFalse(rep.BudgetFired, "a conforming result must not have relied on the Budget cutoff: " + rep.Summary);
        }

        // ---- A REAL z0 tile recurses to the depth cap NON-uniformly (fills, needles, bridge slits) --------
        // Gated on gap MAGNITUDE, not T-junction count; needles and slits paint nothing and are excluded.
        [Test]
        public void RealZ0Tile_NonUniformCurvature_IsConforming()
        {
            var rep = RunOnBurstArm(
                LoadFixture("sample-tile.bytes"), "countries", new TileId { Z = 0, X = 0, Y = 0 }, new SphericalProjection());

            Assert.IsTrue(rep.Passes(),
                "z0 non-uniform-curvature globe subdivision has a visible crack on rendered geometry: " + rep.Summary);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // JobifiedWaterTriangulationTests — the real jobified fill path over the committed water tile
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The production fill path (<see cref="FillMeshGraph.Schedule"/> → <see cref="EarcutBatchJob"/>) over
    /// water-8-135-80 has no folds and conserves area. <c>WaterTriangulationTests</c> covers the other 7
    /// corpus tiles on the same Burst arm.
    /// </summary>
    public class JobifiedWaterTriangulationTests
    {
        private static byte[] LoadFixture(string name)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", name);
            FileAssert.Exists(path);
            return File.ReadAllBytes(path);
        }

        [Test]
        public void JobifiedPipeline_Water_8_135_80_TriangulatesFaithfully()
        {
            byte[] mvtBytes = LoadFixture("water-8-135-80.pbf.bytes");
            var tileId  = new TileId { Z = 8, X = 135, Y = 80 };
            using var mvtTile = MvtDecoder.Decode(tileId, mvtBytes);
            var layer   = mvtTile.GetLayer("water");
            Assert.IsNotNull(layer, "water layer present");

            // Arm A: the command streams read from the BYTES, independently of the decoder under test.
            var oracle = MvtFixtureStreams.ReadLayer(mvtBytes, "water");

            // Ground truth: a managed decode + PolygonAssembler, independent of the Burst graph, gives the
            // outer+hole structure that MeshCoverageValidator checks the Burst triangulation against.
            var groundTruthPolys = new List<Polygon>();
            for (int fi = 0; fi < oracle.Kinds.Count; fi++)
            {
                if (oracle.Kinds[fi] != TileGeometryType.Polygon || oracle.Commands[fi] == null) continue;
                groundTruthPolys.AddRange(PolygonAssembler.Assemble(MvtGeometry.Decode(oracle.Commands[fi])));
            }
            Assert.Greater(groundTruthPolys.Count, 0, "water layer has polygons");

            double extent = layer.Extent;
            var (bMin, _) = tileId.MercatorBounds();

            TileGeometryBuffers geometry = layer.Geometry; // BORROWED — the decoded tile owns it
            NativeArray<int> visitOrder   = TestTileMeshBuilder.FullVisitOrder(geometry);
            var pipelineInput = new FillMeshPipeline.LayerInput
            {
                Geometry       = geometry,
                RingVisitOrder = visitOrder,
                OriginRender   = new double3(bMin.x, 0.0, bMin.y),
                Projection     = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
            };

            FillGraphOutput buffers = FillMeshGraph.Schedule(pipelineInput);
            buffers.Handle.Complete();
            try
            {
                Assert.IsTrue(buffers.IsCreated, "jobified pipeline produced no buffers for a tile with water polygons");

                int indexCount = buffers.TriangleIndices.Length;
                var tris = new List<(double2 a, double2 b, double2 c)>(indexCount / 3);
                for (int i = 0; i + 2 < indexCount; i += 3)
                {
                    double2 a = buffers.TileVertices[buffers.TriangleIndices[i]];
                    double2 b = buffers.TileVertices[buffers.TriangleIndices[i + 1]];
                    double2 c = buffers.TileVertices[buffers.TriangleIndices[i + 2]];
                    tris.Add((a, b, c));
                }

                var rep = MeshCoverageValidator.ValidateTriangulation(
                    groundTruthPolys, tris, buffers.Counts[0].ForceClipCount, (int)extent);

                Assert.IsTrue(rep.Passes(areaEps: 0.01, mismatchEps: 1.0),
                    "jobified (Burst EarcutJob) water z8/135/80 triangulation is broken: " +
                    rep.Summary + "\n" + rep.AsciiMap);
            }
            finally
            {
                buffers.Dispose();
                visitOrder.Dispose();
                // geometry is BORROWED from the decoded layer — the `using` frees it.
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LayerMeshBuildPoolingTests — a pooled build instance is never handed to two renters at once
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A pooled <see cref="ILayerMeshBuild"/> is never handed to two renters at once. A second
    /// <c>Dispose()</c> would return it to <see cref="LayerMeshBuildPool{T}"/>'s <c>ConcurrentBag</c> twice;
    /// <see cref="FillLayerBuild.Dispose"/>'s <c>if (_disposed) return;</c> guard prevents that.
    /// </summary>
    [TestFixture]
    public class LayerMeshBuildPoolingTests
    {
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };

        /// <summary>A minimal, never-scheduled build — <c>RingVisitOrder</c>/<c>FeatureColors</c> are
        /// CREATED (so <c>Dispose()</c> has real columns to free) but zero-length and <c>Geometry</c> stays
        /// uncreated, since a never-scheduled build's <c>Dispose()</c> never reads it (only
        /// <c>TryScheduleWrite</c> does).</summary>
        private static FillLayerBuild RentMinimal(int materialIndex)
        {
            var input = new FillMeshPipeline.LayerInput
            {
                RingVisitOrder = new NativeArray<int>(0, Allocator.Persistent),
                OriginRender   = double3.zero,
                Projection     = new WebMercatorProjection(),
            };
            var featureColors = new NativeArray<Vector4>(0, Allocator.Persistent);
            return FillLayerBuild.Rent(input, featureColors, materialIndex, "probe");
        }

        /// <summary><b>RED:</b> remove the <c>if (_disposed) return;</c> guard from
        /// <see cref="FillLayerBuild.Dispose"/> — the double <c>Dispose()</c> below puts the same instance in
        /// the pool's bag twice, and two of the <c>N</c> rents that follow then return the same reference,
        /// failing the pairwise-distinct assertion. Executed and reverted.</summary>
        [Test]
        public void Dispose_CalledTwice_NeverLandsTheSameInstanceInThePoolTwice()
        {
            FillLayerBuild build = RentMinimal(materialIndex: 0);
            build.Dispose();
            build.Dispose(); // the redundant sweep TileBuildGraph.Dispose()'s own idempotency mirrors

            // Always bound loops: N is a small, fixed constant, not runtime-derived.
            const int N = 32;
            var rented = new List<ILayerMeshBuild>(N);
            try
            {
                for (int i = 0; i < N; i++)
                    rented.Add(RentMinimal(materialIndex: i));

                for (int i = 0; i < N; i++)
                    for (int j = i + 1; j < N; j++)
                        Assert.AreNotSame(rented[i], rented[j],
                            $"Rent() calls {i} and {j} returned the SAME instance — a double-Dispose() landed " +
                            "it in the pool's bag twice, so two independent renters now observe one build.");
            }
            finally
            {
                foreach (ILayerMeshBuild b in rented) b.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineExtentRoutingTests — line's own TileToGeoJob extent routing
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Line's <c>TileToGeoJob</c> takes its extent from the buffer: the same ring under two extents lands at
    /// two world positions, while a 4096 literal would give the SAME mesh. Every committed <c>.pbf</c> is
    /// extent 4096, so a literal is inert across the corpus. Non-obvious why: the fixture extent must
    /// never be 4096, or this test passes vacuously.
    /// </summary>
    [TestFixture]
    public class LineExtentRoutingTests
    {
        private static readonly TileId Tile = new TileId { Z = 3, X = 4, Y = 3 };

        /// <summary>Deliberately NOT 4096 — see the fixture note on the type. Half of 4096, so the same
        /// integer ring lands at twice the tile fraction and the difference is large, not marginal.</summary>
        private const uint NonDefaultExtent = 2048;
        private const uint DefaultExtent    = 4096;

        private static Line.PaintProperties Paint() => StyleLayer().Paint;
        private static Line.LayoutProperties Layout() => StyleLayer().Layout;

        private static Line.StyleLayer StyleLayer() => new Line.StyleLayer
        {
            Id = "extent-probe", LayerType = StyleLayerType.Line, SourceLayer = "probe",
            Paint = TestStyle.LinePaint("{\"line-color\":\"#ffffff\",\"line-width\":2}"),
            Layout = TestStyle.LineLayout(),
        };

        /// <summary>One three-point polyline, well inside the tile at BOTH extents (max coord 900 &lt; 2048).</summary>
        private static IReadOnlyList<MapRenderer.Core.Expressions.IFeature> ProbeFeatures() =>
            new MapRenderer.Core.Expressions.IFeature[]
            {
                new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, hasId: false, geometry: MvtCommandStreamForExtentProbe()),
            };

        private static uint[] MvtCommandStreamForExtentProbe()
            => MapRenderer.Tests.Jobs.MvtCommandStream.Feature(
                MapRenderer.Tests.Jobs.MvtCommandStream.Ring(100, 100, 500, 400, 900, 300));

        private static float3[] BuildAt(uint extent)
        {
            using var tile = new InMemoryDecodedTile(
                new InMemoryTileLayer("probe", Tile, ProbeFeatures(), extent));
            var layer = tile.GetLayer("probe");
            Assert.IsTrue(layer.Geometry.IsCreated, "precondition: the probe layer materialized");
            Assert.AreEqual((double)extent, layer.Geometry.Extent,
                "precondition: the buffer must carry the extent it was minted at — if this is 4096 for the " +
                "non-default arm, the fixture is vacuous by construction (the exact reason the defect lived)");

            using var bag = new ObjectDisposalBag();
            Mesh mesh = bag.Track(TestTileMeshBuilder.BuildLineFromLayer(
                layer, TestTileMeshBuilder.Select(StyleLayer(), layer, 0.0),
                Paint(), Layout(), zoom: Tile.Z, id: Tile, origin: double2.zero));
            Assert.IsNotNull(mesh, $"the probe must produce line geometry at extent {extent}");
            Vector3[] verts = mesh.vertices;
            var copy = new float3[verts.Length];
            for (int i = 0; i < verts.Length; i++) copy[i] = verts[i];
            return copy;
        }

        [Test]
        public void LineSubdividedProjection_ReadsTheBuffersOwnExtent_NotA4096Literal()
        {
            float3[] atDefault    = BuildAt(DefaultExtent);
            float3[] atNonDefault = BuildAt(NonDefaultExtent);

            // Non-vacuity: both arms really produced comparable geometry.
            Assert.Greater(atDefault.Length, 0, "precondition: the 4096 arm produced vertices");
            Assert.AreEqual(atDefault.Length, atNonDefault.Length,
                "precondition: the two arms must emit the same vertex COUNT — the rings are identical, only " +
                "the quantization range differs, so a count difference would mean the arms diverge for some " +
                "reason other than the extent and the position comparison below would be meaningless");

            int differing = 0;
            for (int i = 0; i < atDefault.Length; i++)
                if (math.distance(atDefault[i], atNonDefault[i]) > 1e-4f) differing++;

            Assert.AreEqual(atDefault.Length, differing,
                "EVERY vertex must move when the source-layer's extent changes. Tile-local coordinates are " +
                "geo-referenced by x/extent, so the same integer ring at extent 2048 sits at twice the tile " +
                "fraction it does at 4096. If the two builds agree, StyledLineTileBuilder's TileToGeoJob " +
                "calls are NOT reading `geometry.Extent` — they are using a literal, which is precisely the " +
                "injection that survived a full green 2286-test gate (a recorded finding).");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineGraphParityTests — the per-ring subdivided point count, exact, element for element
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class LineGraphParityTests
    {
        [TestCase("boundary-6-34-21.pbf.bytes", "z6", 6, 34, 21, "WebMercator")]
        [TestCase("boundary-6-34-21.pbf.bytes", "z6", 6, 34, 21, "Spherical")]
        [TestCase("boundary-9-274-168.pbf.bytes", "z9", 9, 274, 168, "WebMercator")]
        [TestCase("boundary-9-274-168.pbf.bytes", "z9", 9, 274, 168, "Spherical")]
        public void LineMeshGraph_MatchesTheCapturedManagedOracle_StreamForStream(
            string fixture, string tag, int z, int x, int y, string label)
        {
            var id = new TileId { Z = z, X = x, Y = y };
            IProjection projection = label == "Spherical" ? (IProjection)new SphericalProjection() : new WebMercatorProjection();

            StyleDocument style = StyleParser.Parse(File.ReadAllText(
                Path.Combine(Application.dataPath, "StreamingAssets", "Fixtures", "liberty.json")));
            LineStyleLayer layer = null;
            foreach (var l in style.Layers)
                if (l.Id == "boundary_3") { layer = l as LineStyleLayer; break; }
            Assert.IsNotNull(layer, "boundary_3 must be a Line.StyleLayer");

            using MvtTile tile = MvtDecoder.Decode(
                id, File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", fixture)));
            ITileLayer mvtLayer = SourceLayerResolver.ResolveTileLayer(layer, tile);
            Assert.IsNotNull(mvtLayer, "boundary_3's source-layer must resolve in this fixture");
            var selected = TestTileMeshBuilder.Select(layer, mvtLayer, z);
            Assert.Greater(selected.Count, 0, "expected boundary_3 line features in this tile");

            double3 origin = TileRenderOrigin.Project(id, projection);
            LayerInput input = StyledLineTileBuilder.BuildLayerInput(
                selected, mvtLayer.Geometry, layer.Paint, layer.Layout, z, origin,
                out NativeArray<Vector4> featureColors, out NativeArray<float> featureWidths, projection);
            Assert.IsTrue(input.FeatureSelected.IsCreated, "precondition: real line geometry must be selected");

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value, "precondition: the measure must not fault");
                Assert.Greater(output.Vertices.Length, 0, "precondition: the graph must have produced real geometry");

                JsonValue golden = LoadGolden(tag, label);
                int goldenTotal = golden.GetInt("totalVertexCount");
                int goldenIndexCount = golden.GetInt("indexCount");

                // ── Step 1: the per-ring subdivided point count, exact — gates everything after it. ────
                List<int> graphRingCounts = ComputeGraphRingSubdivideCounts(mvtLayer.Geometry, input, projection);
                List<int> goldenRingCounts = new List<int>();
                foreach (JsonValue v in golden.Get("ringSubdividedPointCounts").Items) goldenRingCounts.Add(v.AsInt());

                Assert.AreEqual(goldenRingCounts.Count, graphRingCounts.Count,
                    $"[{tag}/{label}] ring count disagrees with the captured oracle — a topology change, not a float question.");
                for (int r = 0; r < goldenRingCounts.Count; r++)
                    Assert.AreEqual(goldenRingCounts[r], graphRingCounts[r],
                        $"[{tag}/{label}] ring {r}'s SUBDIVIDED POINT COUNT diverges from the captured oracle " +
                        $"(golden={goldenRingCounts[r]}, graph={graphRingCounts[r]}) — a quantisation mismatch " +
                        "compares different geometry, not different rounding; stop here, do not compare vertices.");

                Assert.AreEqual(goldenTotal, output.Vertices.Length,
                    $"[{tag}/{label}] total vertex count disagrees with the captured oracle.");
                Assert.AreEqual(goldenIndexCount, output.Indices.Length,
                    $"[{tag}/{label}] index count disagrees with the captured oracle.");

                // ── Step 2a: Indices and Stream3 — frozen whole-stream digests, bit-exact. ───────────────
                var idxBytes = new List<byte>();
                for (int i = 0; i < output.Indices.Length; i++) idxBytes.AddRange(BitConverter.GetBytes(output.Indices[i]));
                var stream3Bytes = new List<byte>();
                for (int i = 0; i < output.Vertices.Length; i++)
                {
                    int f = output.VertexFeatureIdx[i];
                    Vector4 c = featureColors[f];
                    float widthScale = output.Vertices[i].WidthScale * featureWidths[f];
                    stream3Bytes.AddRange(BitConverter.GetBytes(c.x));
                    stream3Bytes.AddRange(BitConverter.GetBytes(c.y));
                    stream3Bytes.AddRange(BitConverter.GetBytes(c.z));
                    stream3Bytes.AddRange(BitConverter.GetBytes(c.w));
                    stream3Bytes.AddRange(BitConverter.GetBytes(widthScale));
                }
                Assert.AreEqual(golden.GetString("indicesDigest"), Sha256(idxBytes),
                    $"[{tag}/{label}] Indices diverge from the captured oracle — a real regression, not a re-bake candidate.");
                // These tiles use a CONSTANT line-color, which rides _BaseColor, so the vertex colour is white;
                // a styled colour here means the vertex bake is back.
                Assert.AreEqual(golden.GetString("stream3Digest"), Sha256(stream3Bytes),
                    $"[{tag}/{label}] Stream3 (colour+widthScale) diverges from the captured oracle. This layer's line-color is CONSTANT, so the colour components must be the WHITE identity; a styled colour here means the constant-colour vertex bake was re-introduced and the layer renders colour-squared.");

                // ── Step 2b: Stream0/1/2 per vertex, each component class bound by its own mechanism.
                // Position/Normal/Side are bit-exact; Across and DistanceAlong get measured-plus-margin ceilings.
                uint[] posHex = ParseHexArray(golden.GetString("positionHex"));
                uint[] normHex = ParseHexArray(golden.GetString("normalHex"));
                uint[] acrossHex = ParseHexArray(golden.GetString("acrossHex"));
                uint[] sideDistHex = ParseHexArray(golden.GetString("sideDistHex"));

                // Before indexing, so a truncated golden fails with a message, not an IndexOutOfRangeException.
                int n = output.Vertices.Length;
                Assert.AreEqual(n * 3, posHex.Length, $"[{tag}/{label}] positionHex length != 3 * vertexCount.");
                Assert.AreEqual(n * 3, normHex.Length, $"[{tag}/{label}] normalHex length != 3 * vertexCount.");
                Assert.AreEqual(n * 3, acrossHex.Length, $"[{tag}/{label}] acrossHex length != 3 * vertexCount.");
                Assert.AreEqual(n * 2, sideDistHex.Length, $"[{tag}/{label}] sideDistHex length != 2 * vertexCount.");

                var posMax = new MaxUlp(); var normMax = new MaxUlp(); var acrossMax = new MaxUlp();
                var sideMax = new MaxUlp(); var distMax = new MaxUlp();
                for (int i = 0; i < output.Vertices.Length; i++)
                {
                    LineRibbonVertex rv = output.Vertices[i];
                    float3 pos = (float3)rv.Position, up = (float3)rv.Up, across = (float3)rv.Across;
                    posMax.Observe(Ulp(pos.x, posHex[i * 3 + 0]), i); posMax.Observe(Ulp(pos.y, posHex[i * 3 + 1]), i); posMax.Observe(Ulp(pos.z, posHex[i * 3 + 2]), i);
                    normMax.Observe(Ulp(up.x, normHex[i * 3 + 0]), i); normMax.Observe(Ulp(up.y, normHex[i * 3 + 1]), i); normMax.Observe(Ulp(up.z, normHex[i * 3 + 2]), i);
                    acrossMax.Observe(Ulp(across.x, acrossHex[i * 3 + 0]), i); acrossMax.Observe(Ulp(across.y, acrossHex[i * 3 + 1]), i); acrossMax.Observe(Ulp(across.z, acrossHex[i * 3 + 2]), i);
                    sideMax.Observe(Ulp(rv.Side, sideDistHex[i * 2 + 0]), i);
                    distMax.Observe(Ulp((float)rv.DistanceAlong, sideDistHex[i * 2 + 1]), i);
                }
                TestContext.Out.WriteLine(
                    $"[{tag}/{label}] max ULP by class: Position={posMax} Normal={normMax} Across={acrossMax} " +
                    $"Side={sideMax} DistanceAlong={distMax}");

                // Normal (Up): no hazard. ProjectPointsJob copies pp.Up with no subtraction, and a few double ULP
                // at unit magnitude vanish in the float32 narrowing, so ANY divergence is a real change.
                Assert.AreEqual(0u, normMax.Delta,
                    $"[{tag}/{label}] Normal (Up) exceeds 0 ULP at vertex {normMax.Index} — this component " +
                    "has NO computed hazard (a straight copy/trig evaluation), so ANY divergence is a real " +
                    "regression, not noise.");

                // Side: RibbonJob assigns only literals (+1f/-1f/0f), so both arms are bit-exact by definition.
                Assert.AreEqual(0u, sideMax.Delta,
                    $"[{tag}/{label}] Side exceeds 0 ULP at vertex {sideMax.Index} — every Side value is a " +
                    "bare literal in production; ANY divergence here is a branch/topology bug, never rounding.");

                // Across has two hazards: near-singular miter division (1/cosHalf) amplifies ULP near a hairpin
                // join, and Burst's relaxed-math normalize adds a few ULP. AcrossUlpCeiling covers both.
                Assert.LessOrEqual(acrossMax.Delta, AcrossUlpCeiling,
                    $"[{tag}/{label}] Across exceeds its {AcrossUlpCeiling}-ULP ceiling at vertex {acrossMax.Index} " +
                    "— near-singular miter division and normalize's own relaxed-math divergence are the ONLY " +
                    "hazards this component has; a delta orders of magnitude past this is a different bug, " +
                    "not a sharper join.");

                // DistanceAlong is a per-ring running sum, bit-exact on this corpus (rings up to ~660 points).
                // Limitation: longer rings could drift; if this reds on a larger fixture, derive a bound from that.
                Assert.AreEqual(0u, distMax.Delta,
                    $"[{tag}/{label}] DistanceAlong exceeds 0 ULP at vertex {distMax.Index}.");

                // Position: pp.World - OriginWorld cancels at planetary magnitude, but at tile-local magnitude the
                // double error is far below a float32 ULP, so narrowing erases it and the result is bit-exact.
                Assert.AreEqual(0u, posMax.Delta,
                    $"[{tag}/{label}] Position exceeds 0 ULP at vertex {posMax.Index} — origin-relative " +
                    "cancellation narrows to float32 STRUCTURALLY at tile-local magnitude (see comment above), " +
                    "so ANY divergence here is a formula error, not drift.");
            }
            finally
            {
                output.Dispose();
                if (featureColors.IsCreated) featureColors.Dispose();
                if (featureWidths.IsCreated) featureWidths.Dispose();
            }
        }

        /// <summary>Runs <see cref="LineMeshGraph.ScheduleTyped{TProj}"/>'s first half with the production jobs to
        /// read <c>OutRingSubOffsets</c>, which <see cref="LineGraphOutput"/> does not expose. <c>SrcUp</c> comes
        /// from the Burst <see cref="ProjectionDispatch.Schedule"/>, because <c>SegmentSteps</c> quantises on it
        /// and a managed value could disagree with the real graph.</summary>
        private static List<int> ComputeGraphRingSubdivideCounts(
            TileGeometryBuffers geometry, LayerInput input, IProjection projection)
        {
            using var srcTile = new NativeList<double2>(Allocator.Persistent);
            using var ringSrcOffsets = new NativeList<int>(Allocator.Persistent);
            using var ringFeature = new NativeList<int>(Allocator.Persistent);
            using var srcGeo = new NativeList<GeoCoordinate>(Allocator.Persistent);
            using var srcWorld = new NativeList<double3>(Allocator.Persistent);
            using var srcUp = new NativeList<double3>(Allocator.Persistent);

            new RingGatherJob
            {
                Vertices = geometry.Vertices, RingOffsets = geometry.RingOffsets, RingFeatureIdx = geometry.RingFeatureIdx,
                FeatureGeometryType = geometry.FeatureGeometryType, RingCount = geometry.RingCount,
                FeatureSelected = input.FeatureSelected,
                OutSrcTile = srcTile, OutRingSrcOffsets = ringSrcOffsets, OutRingFeature = ringFeature,
                OutSrcGeo = srcGeo, OutSrcWorld = srcWorld, OutSrcUp = srcUp,
            }.Run();

            new TileToGeoJob
            {
                Tile = geometry.Tile, Extent = geometry.Extent,
                TileCoords = srcTile.AsArray(), OutGeo = srcGeo.AsArray(),
            }.Run(srcTile.Length);

            // The Burst kernel, not the managed ProjectPoint: a few ULP of divergence in SrcUp can flip a ceil()
            // in SegmentSteps. `originWorld` only affects the discarded World output.
            ProjectionDispatch.Schedule(
                projection, double3.zero, srcGeo, srcWorld, srcUp, default).Complete();

            using var subTile = new NativeList<double2>(Allocator.Persistent);
            using var ringSubOffsets = new NativeList<int>(Allocator.Persistent);
            using var subGeo = new NativeList<GeoCoordinate>(Allocator.Persistent);
            using var subWorld = new NativeList<double3>(Allocator.Persistent);
            using var subUp = new NativeList<double3>(Allocator.Persistent);

            new SubdivideJob
            {
                SrcTile = srcTile, RingSrcOffsets = ringSrcOffsets, SrcUp = srcUp,
                MaxRefineAngleRad = projection.MaxRefineAngleRad,
                OutSubTile = subTile, OutRingSubOffsets = ringSubOffsets,
                OutSubGeo = subGeo, OutSubWorld = subWorld, OutSubUp = subUp,
            }.Run();

            var counts = new List<int>();
            for (int r = 0; r < ringSubOffsets.Length - 1; r++)
                counts.Add(ringSubOffsets[r + 1] - ringSubOffsets[r]);
            return counts;
        }

        // Across is the one class this corpus stresses (miter division, relaxed normalize); the ceiling is 4x
        // the largest measured value, leaving margin for a sharper join.
        private const ulong AcrossUlpCeiling = 4000;

        /// <summary>Total-order IEEE-754 ULP distance between the stored float32 <paramref name="actual"/> and a
        /// golden hex bit pattern. Non-obvious why: the <see cref="NearZeroAbs"/> hatch exists because near
        /// zero, values ~1e-12 apart can be a billion ULP apart. 1e-6 is far above that noise and far below
        /// any meaningful tile-local ribbon component.</summary>
        private const float NearZeroAbs = 1e-6f;

        private static ulong Ulp(float actual, uint goldenHex)
        {
            float golden = math.asfloat(goldenHex);
            if (math.abs(actual) < NearZeroAbs && math.abs(golden) < NearZeroAbs)
                return 0; // both negligible — see the near-zero escape hatch doc above.
            return UlpDistance(math.asuint(actual), goldenHex);
        }

        private static ulong ToUlpOrder(uint bits) => (bits & 0x80000000U) != 0 ? ~bits : (bits | 0x80000000U);
        private static ulong UlpDistance(uint a, uint b)
        {
            ulong oa = ToUlpOrder(a), ob = ToUlpOrder(b);
            return oa > ob ? oa - ob : ob - oa;
        }

        /// <summary>The running max ULP delta for one component class, plus WHICH vertex it came from — a
        /// max with no index is useless for tracking down a real regression.</summary>
        private struct MaxUlp
        {
            public ulong Delta;
            public int Index;
            public void Observe(ulong delta, int index) { if (delta > Delta) { Delta = delta; Index = index; } }
            public override string ToString() => $"{Delta}@{Index}";
        }

        private static JsonValue LoadGolden(string tag, string label)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", $"line-graphwrite-golden-{tag}-{label}.json");
            FileAssert.Exists(path);
            return JsonParser.Parse(File.ReadAllText(path));
        }

        private static uint[] ParseHexArray(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return Array.Empty<uint>();
            string[] parts = csv.Split(',');
            var result = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = Convert.ToUInt32(parts[i], 16);
            return result;
        }

        private static string Sha256(List<byte> bytes)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineGraphSchedulingTests — line graph acceptance teeth (a), (b), (d), (e), (g), (h)
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class LineGraphSchedulingTests
    {
        // ── Shared synthetic-fixture helpers ──────────────────────────────────────────────────────

        private static readonly TileId SyntheticTile = new TileId { Z = 10, X = 300, Y = 380 };
        private const double SyntheticExtent = 4096.0;

        /// <summary>Builds a <see cref="LayerInput"/> directly from ring point lists, bypassing MVT
        /// decode — every ring is a selected LineString feature, one feature per ring. Caller disposes the
        /// returned <see cref="LayerInput"/> (via its own <c>Dispose</c> convention: the request's
        /// owning caller frees <c>FeatureSelected</c>) and the returned <c>geometry</c>.</summary>
        private static (LayerInput input, TileGeometryBuffers geometry) SyntheticLineInput(
            IProjection projection, int maxOutputVertices, params double2[][] rings)
        {
            int ringCount = rings.Length;
            int totalVerts = 0;
            foreach (var r in rings) totalVerts += r.Length;

            var geometry = TileGeometryBuffers.Allocate(
                SyntheticTile, SyntheticExtent, featureCount: ringCount, maxRings: ringCount, maxVertices: totalVerts);
            int cursor = 0;
            for (int r = 0; r < ringCount; r++)
            {
                geometry.FeatureGeometryType[r] = TileGeometryType.LineString;
                geometry.RingFeatureIdx[r] = r;
                geometry.RingOffsets[r] = cursor;
                foreach (double2 p in rings[r]) geometry.Vertices[cursor++] = p;
            }
            geometry.RingOffsets[ringCount] = cursor;
            geometry.RingCount = ringCount;
            geometry.VertexCount = cursor;

            var featSelected = new NativeArray<bool>(ringCount, Allocator.Persistent);
            for (int i = 0; i < ringCount; i++) featSelected[i] = true;

            double3 origin = TileRenderOrigin.Project(SyntheticTile, projection);
            var input = new LayerInput
            {
                Geometry = geometry, FeatureSelected = featSelected, OriginRender = origin,
                Projection = projection, Join = JoinType.Miter, Cap = CapType.Butt,
                MiterLimit = 2.0, RoundLimit = 1.05, RoundSegments = 4,
                MaxOutputVertices = maxOutputVertices,
            };
            return (input, geometry);
        }

        private static void DisposeSynthetic(LayerInput input, TileGeometryBuffers geometry)
        {
            if (input.FeatureSelected.IsCreated) input.FeatureSelected.Dispose();
            geometry.Dispose();
        }

        // ── Tooth (b): the ring gate is the line gate, through the GRAPH ─────────────────────────────

        /// <summary>
        /// The mixed buffer of
        /// <c>StyledLineBufferParityTests.LineLayer_WithPolygonAndLineFeaturesSelected_RibbonsOnlyTheLines</c>
        /// plus a selected 2-POINT LineString (line's threshold is <c>&gt;= 2</c>, not fill's 3) and an
        /// UNSELECTED one, through <see cref="LineMeshGraph.Schedule"/>: only selected LineStrings ribbon.
        /// </summary>
        [Test]
        public void LineMeshGraph_RibbonsOnlySelectedLineStrings_TwoPointRingIncluded()
        {
            var polygonRing    = MvtCommandStream.Ring(1000, 1000, 2000, 1000, 2000, 2000, 1000, 2000);
            var lineRing       = MvtCommandStream.Ring(2600, 1200, 2800, 1600, 2900, 2400);
            var twoPointRing   = new[] { new double2(500, 500), new double2(900, 900) };
            var unselectedRing = MvtCommandStream.Ring(3200, 800, 3400, 1200, 3500, 1600);

            // ordinals: 0 = polygon (SELECTED, wrong kind), 1 = line (SELECTED), 2 = twoPoint (SELECTED),
            // 3 = unselected line (NOT selected).
            var geometry = TileGeometryBuffers.Allocate(
                SyntheticTile, SyntheticExtent, featureCount: 4, maxRings: 4,
                maxVertices: polygonRing.Count + lineRing.Count + twoPointRing.Length + unselectedRing.Count);
            int cursor = 0;
            void WriteRing(int ordinal, TileGeometryType kind, IReadOnlyList<double2> pts)
            {
                geometry.FeatureGeometryType[ordinal] = kind;
                geometry.RingFeatureIdx[geometry.RingCount] = ordinal;
                geometry.RingOffsets[geometry.RingCount] = cursor;
                foreach (double2 p in pts) geometry.Vertices[cursor++] = p;
                geometry.RingCount++;
            }
            WriteRing(0, TileGeometryType.Polygon,    polygonRing);
            WriteRing(1, TileGeometryType.LineString, lineRing);
            WriteRing(2, TileGeometryType.LineString, twoPointRing);
            WriteRing(3, TileGeometryType.LineString, unselectedRing);
            geometry.RingOffsets[geometry.RingCount] = cursor;
            geometry.VertexCount = cursor;

            var featSelected = new NativeArray<bool>(4, Allocator.Persistent);
            featSelected[0] = true; featSelected[1] = true; featSelected[2] = true; featSelected[3] = false;

            var projection = new WebMercatorProjection();
            var input = new LayerInput
            {
                Geometry = geometry, FeatureSelected = featSelected,
                OriginRender = TileRenderOrigin.Project(SyntheticTile, projection), Projection = projection,
                Join = JoinType.Miter, Cap = CapType.Butt, MiterLimit = 2.0, RoundLimit = 1.05, RoundSegments = 4,
                MaxOutputVertices = LineMeshGraph.DefaultMaxOutputVertices,
            };

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value);
                Assert.Greater(output.Vertices.Length, 0,
                    "precondition: the selected LineStrings must produce ribbon geometry");

                // Only ordinals 1 (mixed-kind ring) and 2 (two-point ring) may appear; never 0 (polygon) or 3.
                bool sawFeature1 = false, sawFeature2 = false;
                for (int i = 0; i < output.VertexFeatureIdx.Length; i++)
                {
                    int f = output.VertexFeatureIdx[i];
                    Assert.IsFalse(f == 0, "the polygon ring (wrong kind) must never contribute a vertex");
                    Assert.IsFalse(f == 3, "the unselected LineString must never contribute a vertex");
                    if (f == 1) sawFeature1 = true;
                    if (f == 2) sawFeature2 = true;
                }
                Assert.IsTrue(sawFeature1, "the selected mixed-kind LineString must ribbon");
                Assert.IsTrue(sawFeature2, "the selected 2-point LineString must ribbon — line's own >= 2 threshold");
            }
            finally
            {
                output.Dispose();
                featSelected.Dispose();
                geometry.Dispose();
            }
        }

        // ── Tooth (d): dash phase resets per ring ────────────────────────────────────────────────────

        /// <summary>
        /// (d) Over a two-ring synthetic line layer, the graph's <see cref="LineRibbonVertex.DistanceAlong"/>
        /// restarts at 0 at the first vertex of the SECOND ring and its maximum equals that ring's own arc
        /// length — never the running total across both. Two rings is the minimum that can distinguish this
        /// (one ring makes the assertion vacuous).
        /// </summary>
        [Test]
        public void LineMeshGraph_DistanceAlong_ResetsPerRing_NotAccumulatedAcrossRings()
        {
            // Two separate straight rings, 10 and 1000 tile units long. Compared as a RATIO, because
            // DistanceAlong is in world metres and the tile→world scale varies.
            var ring0 = new[] { new double2(500, 500), new double2(510, 500) };
            var ring1 = new[] { new double2(2000, 500), new double2(3000, 500) };

            var projection = new WebMercatorProjection();
            (LayerInput input, TileGeometryBuffers geometry) =
                SyntheticLineInput(projection, LineMeshGraph.DefaultMaxOutputVertices, ring0, ring1);

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value);
                Assert.Greater(output.Vertices.Length, 0, "precondition: both rings must ribbon");

                double ring0Max = 0.0, ring1Max = 0.0;
                bool sawRing0Zero = false, sawRing1Zero = false;
                for (int i = 0; i < output.Vertices.Length; i++)
                {
                    int f = output.VertexFeatureIdx[i];
                    double d = output.Vertices[i].DistanceAlong;
                    if (f == 0) { ring0Max = math.max(ring0Max, d); if (d == 0.0) sawRing0Zero = true; }
                    else        { ring1Max = math.max(ring1Max, d); if (d == 0.0) sawRing1Zero = true; }
                }

                Assert.Greater(ring0Max, 0.0, "precondition: ring 0's own arc must be non-degenerate");

                // This catches accumulation: a ring 1 seeded with ring 0's total never has a vertex at EXACTLY 0.
                // The ratio check below passes under that bug too.
                Assert.IsTrue(sawRing0Zero, "precondition: ring 0 must have a vertex at DistanceAlong == 0");
                Assert.IsTrue(sawRing1Zero,
                    "ring 1's DistanceAlong must restart at 0 — an accumulating bug would start it at " +
                    "ring 0's own max instead, never exactly 0");

                // Fixture sanity only: the two rings really differ in length.
                Assert.Greater(ring1Max, ring0Max * 5.0,
                    $"precondition: ring 1 ({ring1Max}) must be substantially longer than ring 0 ({ring0Max}), " +
                    "or this fixture does not actually distinguish 'ring 1's own arc' from 'ring 0's'.");
            }
            finally
            {
                output.Dispose();
                DisposeSynthetic(input, geometry);
            }
        }

        // ── Tooth (e): the vertex ceiling binds and settles as zero-vertex ───────────────────────────

        /// <summary>
        /// (e) A synthetic ring whose ribbon vertex count exceeds a tiny
        /// <see cref="LayerInput.MaxOutputVertices"/> (passed explicitly, never the production
        /// constant) trips <see cref="LineGraphCounts.ErrorLineVertexCapacity"/> and the graph produces NO
        /// vertices for it — the always-bound-loops backstop.
        /// </summary>
        [Test]
        public void LineMeshGraph_VertexCeiling_Binds_AndSettlesAsZeroVertex()
        {
            // A long zig-zag ring — round joins so each interior point emits a real (roundSegments+~7)-vertex
            // fan, comfortably exceeding a ceiling of 8.
            var pts = new double2[40];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new double2(500 + i * 20, 500 + (i % 2) * 200);

            var projection = new WebMercatorProjection();
            (LayerInput input, TileGeometryBuffers geometry) =
                SyntheticLineInput(projection, maxOutputVertices: 8, pts);
            input.Join = JoinType.Round; input.RoundSegments = 8;

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();

                Assert.AreEqual(LineGraphCounts.ErrorLineVertexCapacity, output.Error.Value,
                    "a ring whose ribbon vertex count exceeds MaxOutputVertices must set the capacity error");
                // RibbonAggregateJob checks BEFORE appending a ring past MaxOutputVertices, so a ring over 8
                // alone writes nothing; flagging the error after appending anyway fails this.
                Assert.LessOrEqual(output.Vertices.Length, 8,
                    "the append loop must have STOPPED at the ceiling, not merely flagged the error after " +
                    "writing everything anyway");
            }
            finally
            {
                output.Dispose();
                DisposeSynthetic(input, geometry);
            }
        }

        /// <summary>Sibling to the RED case above: the SAME ring under a generous ceiling produces real
        /// geometry with NO error — confirms the tiny ceiling in the primary tooth is what trips the flag,
        /// not something else about this fixture.</summary>
        [Test]
        public void LineMeshGraph_VertexCeiling_GenerousCeiling_ProducesRealGeometry_NoError()
        {
            var pts = new double2[40];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new double2(500 + i * 20, 500 + (i % 2) * 200);

            var projection = new WebMercatorProjection();
            (LayerInput input, TileGeometryBuffers geometry) =
                SyntheticLineInput(projection, LineMeshGraph.DefaultMaxOutputVertices, pts);
            input.Join = JoinType.Round; input.RoundSegments = 8;

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value);
                Assert.GreaterOrEqual(output.Vertices.Length, 8 + 40,
                    "under a generous ceiling the SAME ring must produce at least as much geometry as the " +
                    "capped run was cut off at — confirms the cap, not the fixture, was the RED cause above");
            }
            finally
            {
                output.Dispose();
                DisposeSynthetic(input, geometry);
            }
        }

        // ── Tooth (g): winding, through the graph's generic entry ───────────────────────────────────

        /// <summary>
        /// (g) <c>RightHandedSphereProjectionWindingTests</c>' decisive projection, driven through
        /// <see cref="LineMeshGraph.ScheduleTyped{TProj}"/> — a generic entry Burst reaches with NOTHING
        /// registered for this projection type — must still wind the same as flat Mercator. Mirrors that
        /// test's own reconstruction (<c>GlobeLineWindingTests.RibbonWindingSign</c>).
        /// </summary>
        [Test]
        public void LineMeshGraph_RightHandedCurvedProjection_ThroughGenericScheduleTyped_WindsSameAsMercator()
        {
            // A curved-enough polyline (several segments spanning real angular distance) so the winding
            // reconstruction has non-degenerate join triangles to measure.
            var pts = new double2[6];
            for (int i = 0; i < pts.Length; i++)
                pts[i] = new double2(500 + i * 500, 500 + math.sin(i) * 400);

            var flatProjection = new WebMercatorProjection();
            (LayerInput flatInput, TileGeometryBuffers flatGeometry) =
                SyntheticLineInput(flatProjection, LineMeshGraph.DefaultMaxOutputVertices, pts);

            var rhProjection = new RightHandedSphereTestProjection();
            (LayerInput rhInput, TileGeometryBuffers rhGeometry) =
                SyntheticLineInput(rhProjection, LineMeshGraph.DefaultMaxOutputVertices, pts);

            LineGraphOutput flatOutput = default, rhOutput = default;
            try
            {
                flatOutput = LineMeshGraph.Schedule(flatInput);
                flatOutput.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, flatOutput.Error.Value);
                Assert.Greater(flatOutput.Vertices.Length, 0);

                // The decisive call: the generic entry point, bypassing Schedule's closed switch — the
                // shape a projection Burst never registered generically needs.
                rhOutput = LineMeshGraph.ScheduleTyped(rhInput, rhProjection, default);
                rhOutput.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, rhOutput.Error.Value);
                Assert.Greater(rhOutput.Vertices.Length, 0);

                var (flatSign, flatUniformity) = RibbonWindingSign(flatOutput);
                var (rhSign, rhUniformity) = RibbonWindingSign(rhOutput);

                Assert.Greater(flatUniformity, 0.99, "Mercator ribbon winding must be uniform");
                Assert.Greater(rhUniformity, 0.99, "right-handed curved ribbon winding must be uniform");
                Assert.AreEqual(flatSign, rhSign,
                    "a RIGHT-handed curved projection must wind the SAME as Mercator relative to the surface " +
                    "normal — winding is derived from cross(along, up), not from curvature/handedness.");
            }
            finally
            {
                flatOutput.Dispose(); rhOutput.Dispose();
                DisposeSynthetic(flatInput, flatGeometry);
                DisposeSynthetic(rhInput, rhGeometry);
            }
        }

        /// <summary>Tallies the sign of the angle between each triangle's face normal and its surface
        /// normal, over the graph's own pre-write ribbon vertices/indices — the graph-side twin of
        /// <c>GlobeLineWindingTests.RibbonWindingSign</c> (which reads a finished <c>Mesh</c>).</summary>
        private static (int sign, double uniformity) RibbonWindingSign(LineGraphOutput output)
        {
            int pos = 0, neg = 0;
            for (int i = 0; i + 2 < output.Indices.Length; i += 3)
            {
                int ia = output.Indices[i], ib = output.Indices[i + 1], ic = output.Indices[i + 2];
                LineRibbonVertex va = output.Vertices[ia], vb = output.Vertices[ib], vc = output.Vertices[ic];
                double3 pa = va.Position, pb = vb.Position, pc = vc.Position;
                double d = math.max(math.distance(pa, pb), math.max(math.distance(pb, pc), math.distance(pc, pa)));
                if (d < 1e-6) continue;
                double w = 0.05 * d;
                double3 ea = pa + va.Across * w, eb = pb + vb.Across * w, ec = pc + vc.Across * w;
                double3 g = math.cross(eb - ea, ec - ea);
                double3 n = va.Up;
                double gm = math.length(g), nm = math.length(n);
                if (gm <= 0.0 || nm <= 0.0) continue;
                double cos = math.dot(g, n) / (gm * nm);
                if (math.abs(cos) < 0.5) continue;
                if (cos > 0.0) pos++; else neg++;
            }
            int counted = pos + neg;
            Assert.Greater(counted, 0, "no non-degenerate ribbon triangles to measure");
            int sign = pos >= neg ? 1 : -1;
            return (sign, (double)math.max(pos, neg) / counted);
        }

        // ── Tooth (h): lifetime — the pen at every step, and the counters return to zero ─────────────

        /// <summary>
        /// (h) A line request's owned columns are counted live at <see cref="LineMeshGraph.Schedule"/> and
        /// freed at <see cref="LineGraphOutput.Dispose"/> — <see cref="LineGraphOutput.DebugLiveCount"/>
        /// returns to baseline, and <see cref="LineGraphOutput.DebugBuffersAllocated"/> /
        /// <see cref="LineGraphOutput.DebugBufferDisposeNodes"/> stay paired (the non-vacuity witness: both
        /// must have ADVANCED by the same nonzero amount, not merely stayed equal at their starting value).
        /// </summary>
        [Test]
        public void LineGraphOutput_Dispose_ReturnsLiveCountToBaseline_BuffersPaired()
        {
            var ring = new[] { new double2(500, 500), new double2(900, 900), new double2(1200, 600) };
            var projection = new WebMercatorProjection();
            (LayerInput input, TileGeometryBuffers geometry) =
                SyntheticLineInput(projection, LineMeshGraph.DefaultMaxOutputVertices, ring);

            long liveBaseline = LineGraphOutput.DebugLiveCount;
            long allocBaseline = LineGraphOutput.DebugBuffersAllocated;
            long disposeBaseline = LineGraphOutput.DebugBufferDisposeNodes;

            LineGraphOutput output = default;
            try
            {
                output = LineMeshGraph.Schedule(input);
                Assert.Greater(LineGraphOutput.DebugLiveCount, liveBaseline,
                    "non-vacuity: Schedule must have counted its output containers live before completion");

                output.Handle.Complete();
                Assert.AreEqual(LineGraphCounts.Ok, output.Error.Value);
                Assert.Greater(output.Vertices.Length, 0, "precondition: real geometry, or the pen below proves nothing");
            }
            finally
            {
                output.Dispose();
                DisposeSynthetic(input, geometry);
            }

            Assert.AreEqual(liveBaseline, LineGraphOutput.DebugLiveCount,
                "Dispose must free every output container Schedule counted live");
            long allocDelta = LineGraphOutput.DebugBuffersAllocated - allocBaseline;
            long disposeDelta = LineGraphOutput.DebugBufferDisposeNodes - disposeBaseline;
            Assert.Greater(allocDelta, 0, "non-vacuity: real scratch buffers must have been allocated");
            Assert.AreEqual(allocDelta, disposeDelta,
                "every scratch buffer NewBuffer<T> allocated must have a matching ScheduleDispose<T> node");
        }

        /// <summary>Regression injection target for the RED half of tooth (h) — see the RED-verification
        /// note this test's own report cites. Each iteration completes and disposes its own output before
        /// the next starts (no handle held across iterations); a future developer can reproduce the RED by
        /// holding <c>Error</c> undisposed inside <see cref="LineGraphOutput.Dispose"/> (comment out one
        /// Dispose line) and re-running.</summary>
        [Test]
        public void LineGraphOutput_RepeatedSchedule_NeverLeaksAcrossCalls()
        {
            var ring = new[] { new double2(500, 500), new double2(900, 900) };
            var projection = new WebMercatorProjection();

            long liveBaseline = LineGraphOutput.DebugLiveCount;
            for (int iter = 0; iter < 5; iter++)
            {
                (LayerInput input, TileGeometryBuffers geometry) =
                    SyntheticLineInput(projection, LineMeshGraph.DefaultMaxOutputVertices, ring);
                LineGraphOutput output = LineMeshGraph.Schedule(input);
                output.Handle.Complete();
                output.Dispose();
                DisposeSynthetic(input, geometry);
            }
            Assert.AreEqual(liveBaseline, LineGraphOutput.DebugLiveCount,
                "five schedule/dispose cycles must return the live count to baseline — a per-call leak would " +
                "accumulate visibly here even if any single cycle's own before/after looked balanced.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RightHandedSphereTestProjection — test-only helper: a right-handed mirror of SphericalProjection
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>A curved, RIGHT-handed test projection, the mirror of <see cref="SphericalProjection"/>'s
    /// left-handed axis swap (<c>World = (x, y, z)</c>, det(TangentBasis) = +1). Camera members throw. A local
    /// copy of <c>RightHandedSphereProjection</c>; like it, it is never registered for Burst.</summary>
    internal readonly struct RightHandedSphereTestProjection : IProjection
    {
        public const double Radius = EarthConstants.A;

        public ProjectedPoint ProjectPoint(in GeoCoordinate geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0;
            double phi    = geo.Latitude  * math.PI_DBL / 180.0;
            double cosPhi = math.cos(phi), sinPhi = math.sin(phi);
            double cosLam = math.cos(lambda), sinLam = math.sin(lambda);
            double upX = cosPhi * cosLam, upY = cosPhi * sinLam, upZ = sinPhi;
            return new ProjectedPoint
            {
                World = new double3(upX * Radius, upY * Radius, upZ * Radius),
                Up    = new double3(upX, upY, upZ),
            };
        }

        public double3 Project(in GeoCoordinate geo) => ProjectPoint(geo).World;
        public double3 UpAt(in GeoCoordinate geo)    => ProjectPoint(geo).Up;

        public float3x3 TangentBasisAt(in GeoCoordinate geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0;
            double phi    = geo.Latitude  * math.PI_DBL / 180.0;
            double cosPhi = math.cos(phi), sinPhi = math.sin(phi);
            double cosLam = math.cos(lambda), sinLam = math.sin(lambda);
            double3 up   = new double3(cosPhi * cosLam, cosPhi * sinLam, sinPhi);
            double3 east = new double3(-sinLam, cosLam, 0.0);
            double3 north = math.cross(up, east);
            return new float3x3((float3)east, (float3)up, (float3)north);
        }

        public double MetersPerUnit     => 1.0;
        public double MaxRefineAngleRad => SphericalProjection.MaxCurveSegmentRad;

        public bool TryGetHorizonOccluder(out double3 renderCentre, out double radius)
        {
            renderCentre = default; radius = 0.0; return false;
        }

        public GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in MapRenderer.Core.Geo.CameraProperties camera)
            => throw new NotSupportedException("RightHandedSphereTestProjection is a geometry-only test double.");
        public double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in MapRenderer.Core.Geo.CameraProperties camera)
            => throw new NotSupportedException("RightHandedSphereTestProjection is a geometry-only test double.");
        public double ClampValidLatitude(double latitudeDegrees) => math.clamp(latitudeDegrees, -90.0, 90.0);
        public bool IsFinitePlanarWorld => false;
        public GeoCoordinate3D ClampLookAtToWorld(double2 viewportPx, in MapRenderer.Core.Geo.CameraProperties camera)
            => throw new NotSupportedException("RightHandedSphereTestProjection is a geometry-only test double.");
    }
}
