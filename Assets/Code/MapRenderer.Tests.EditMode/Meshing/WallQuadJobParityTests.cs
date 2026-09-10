// stage-wall-job-plan.md §4 A2/A3 — the wall block's own parity teeth, direct against BuildLayerInput's
// `walls` output (WallColumns), the same call site Assets/Fixtures/extrusion-wall-golden-*.json was captured
// from (provenance sha 74a9f604, before this stage's Burst wall job existed — see each golden's own header).
// Comparing here, rather than through the full assembled mesh, isolates exactly the code this stage replaced:
// the roof/index-rebase machinery is independently pinned by StyledFillExtrusionMeshTests (7 teeth) and
// StyledFillExtrusionGraphWriteTests (unmodified observers, per the plan's §3 fence).
//
// Two-part comparison, same shape as StyledFillExtrusionGraphWriteTests and grounded in the SAME measurement
// (docs/job-scheduling-design.md §8 stage 5's opening invariant block + the wall-tail addendum):
//   - Position, ExtrudeUpAndT.w (t), BakedBaseHeight, Color, Indices: BIT-EXACT. Position for the STRUCTURAL
//     reason stated in §8 (tile-local RTC magnitude puts the origin-relative double divergence six orders
//     below a float32 ULP — a straight (float3) cast, no normalize involved, so this holds regardless of
//     fixture); BakedBaseHeight/Color/t/Indices because they never touch a projected value at all.
//   - Normal (`outward`), Tangent (`edgeDir`) AND ExtrudeUpAndT.xyz (`upA`/`upB` scaled): BOUNDED. All three
//     are (or are built from) a `math.normalize` call, which §8's wall-tail addendum established diverges
//     from managed data-dependently on SOME inputs, not universally. ExtrudeUpAndT.xyz was ORIGINALLY written
//     here as bit-exact — "measured bit-exact on every edge of the square+courtyard fixture" — and running
//     this exact tooth against the OTHER three captured fixtures found that claim false: high-latitude/
//     Spherical diverges by 1 ULP at ExtrudeUpAndT.x[0]. That is this test doing its job on its own design,
//     not a defect in production — recorded here as the reason all three normalize-derived fields share one
//     bound rather than two of them claiming an exactness only checked on one fixture. The bound used (5 ULP)
//     is a stated ceiling above every fixture this file measures (1-2 ULP, square+courtyard) with the SAME
//     enormous clearance below a real defect's signature (614458 ULP, measured from an injected transcription
//     error in StyledFillExtrusionGraphWriteTests) that its own bounds carry — not re-measured per fixture
//     beyond what running this tooth already does, because a ceiling this far below a real defect and this
//     far above what four fixtures actually produced does not need to be exact per fixture to do its job:
//     distinguish Burst rounding noise from a formula error.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Meshing;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;

namespace MapRenderer.Tests.Meshing
{
    [TestFixture]
    public class WallQuadJobParityTests
    {
        private const double Extent = 4096.0;
        private static readonly TileId ModerateTile = new TileId { Z = 10, X = 300, Y = 380 };
        private static readonly TileId EquatorTile0 = new TileId { Z = 0, X = 0, Y = 0 };

        // ── Fixture builders — mirrors _CaptureWallGoldens.cs's own copies (that harness is deleted; these ──
        // ── recreate the SAME four fixtures the goldens were captured from, byte-for-byte). ──────────────────
        private static uint ZigZag(int n) => (uint)((n << 1) ^ (n >> 31));
        private static void AppendMoveTo(List<uint> cmds, ref int cx, ref int cy, int x, int y)
        { cmds.Add((1u << 3) | 1u); cmds.Add(ZigZag(x - cx)); cmds.Add(ZigZag(y - cy)); cx = x; cy = y; }
        private static void AppendLineTo(List<uint> cmds, ref int cx, ref int cy, params (int x, int y)[] pts)
        {
            cmds.Add(((uint)pts.Length << 3) | 2u);
            foreach (var p in pts) { cmds.Add(ZigZag(p.x - cx)); cmds.Add(ZigZag(p.y - cy)); cx = p.x; cy = p.y; }
        }
        private static uint[] SquareRing(int x0, int y0, int size)
        {
            var cmds = new List<uint>(); int cx = 0, cy = 0;
            AppendMoveTo(cmds, ref cx, ref cy, x0, y0);
            AppendLineTo(cmds, ref cx, ref cy, (x0 + size, y0), (x0 + size, y0 + size), (x0, y0 + size));
            return cmds.ToArray();
        }
        private static uint[] SquareWithHoleRing(int x0, int y0, int size, int holeX0, int holeY0, int holeSize)
        {
            var cmds = new List<uint>(); int cx = 0, cy = 0;
            AppendMoveTo(cmds, ref cx, ref cy, x0, y0);
            AppendLineTo(cmds, ref cx, ref cy, (x0 + size, y0), (x0 + size, y0 + size), (x0, y0 + size));
            AppendMoveTo(cmds, ref cx, ref cy, holeX0, holeY0);
            AppendLineTo(cmds, ref cx, ref cy, (holeX0, holeY0 + holeSize), (holeX0 + holeSize, holeY0 + holeSize), (holeX0 + holeSize, holeY0));
            return cmds.ToArray();
        }
        private static uint[] TwoPointLine(int x0, int y0, int x1, int y1)
        {
            var cmds = new List<uint>(); int cx = 0, cy = 0;
            AppendMoveTo(cmds, ref cx, ref cy, x0, y0);
            AppendLineTo(cmds, ref cx, ref cy, (x1, y1));
            return cmds.ToArray();
        }
        private static IFeature SquareFeature(int x0, int y0, int size, IReadOnlyDictionary<string, Value> props = null)
            => new DictionaryFeature(properties: props, geometryType: TileGeometryType.Polygon, geometry: SquareRing(x0, y0, size));
        private static IFeature CourtyardFeature(int x0, int y0, int size, int holeInset, int holeSize)
            => new DictionaryFeature(properties: null, geometryType: TileGeometryType.Polygon,
                geometry: SquareWithHoleRing(x0, y0, size, x0 + holeInset, y0 + holeInset, holeSize));
        private static IFeature LineFeature(int x0, int y0, int x1, int y1)
            => new DictionaryFeature(properties: null, geometryType: TileGeometryType.LineString, geometry: TwoPointLine(x0, y0, x1, y1));

        private static FillExtrusion.PaintProperties ConstantHeightPaint(double height)
            => FillExtrusion.PaintProperties.Parse(JsonParser.Parse($"{{\"fill-extrusion-height\":{height.ToString(CultureInfo.InvariantCulture)}}}"));
        private static FillExtrusion.PaintProperties DataDrivenPaint()
            => FillExtrusion.PaintProperties.Parse(JsonParser.Parse(
                "{\"fill-extrusion-height\":[\"get\",\"h\"],\"fill-extrusion-base\":[\"get\",\"b\"],\"fill-extrusion-color\":[\"get\",\"c\"]}"));

        private struct FixtureCase
        {
            public string Name;
            public IReadOnlyList<IFeature> Features;
            public FillExtrusion.PaintProperties Paint;
            public TileId Tile;
        }

        private static FixtureCase Square() => new FixtureCase
        {
            Name = "square", Tile = ModerateTile, Paint = ConstantHeightPaint(50),
            Features = new[] { SquareFeature(1000, 1000, 500) },
        };
        private static FixtureCase Courtyard() => new FixtureCase
        {
            Name = "courtyard", Tile = ModerateTile, Paint = ConstantHeightPaint(50),
            Features = new[] { CourtyardFeature(2000, 1000, 600, holeInset: 150, holeSize: 300) },
        };
        private static FixtureCase HighLatitude() => new FixtureCase
        {
            Name = "high-latitude", Tile = EquatorTile0, Paint = ConstantHeightPaint(10),
            Features = new[] { SquareFeature(1800, 400, 100) },
        };
        private static FixtureCase Interleaved() => new FixtureCase
        {
            Name = "interleaved", Tile = ModerateTile, Paint = DataDrivenPaint(),
            Features = new IFeature[]
            {
                LineFeature(0, 0, 100, 100),
                SquareFeature(1000, 1000, 500, new Dictionary<string, Value>
                {
                    ["h"] = Value.Number(30.0), ["b"] = Value.Number(2.0),
                    ["c"] = Value.OfColor(new MapRenderer.Core.Expressions.Color(1.0, 0.0, 0.0, 1.0)),
                }),
                SquareFeature(2200, 1000, 400, new Dictionary<string, Value>
                {
                    ["h"] = Value.Number(75.0), ["b"] = Value.Number(9.0),
                    ["c"] = Value.OfColor(new MapRenderer.Core.Expressions.Color(0.0, 1.0, 0.0, 1.0)),
                }),
            },
        };

        // Measured (2026-09-04, square+courtyard, this file's own sibling StyledFillExtrusionGraphWriteTests)
        // — the ceiling stated in the file header. Not re-measured per fixture; see the header for why that
        // is safe here.
        private const int WallNormalTangentMaxUlp = 5;

        [TestCase("square", false), TestCase("square", true)]
        [TestCase("courtyard", false), TestCase("courtyard", true)]
        [TestCase("high-latitude", false), TestCase("high-latitude", true)]
        [TestCase("interleaved", false), TestCase("interleaved", true)]
        public void WallStreams_AreBitIdenticalOrWithinMeasuredBound_ToTheManagedGoldens(string fixtureName, bool spherical)
        {
            FixtureCase fc = fixtureName switch
            {
                "square" => Square(),
                "courtyard" => Courtyard(),
                "high-latitude" => HighLatitude(),
                "interleaved" => Interleaved(),
                _ => throw new ArgumentException(fixtureName),
            };
            string label = spherical ? "Spherical" : "WebMercator";
            IProjection projection = spherical ? (IProjection)new SphericalProjection() : new WebMercatorProjection();

            IReadOnlyList<SelectedTileFeature> selected = TestTileMeshBuilder.Selection(fc.Features);
            double3 renderOrigin = TileRenderOrigin.Project(fc.Tile, projection);
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(fc.Features, fc.Tile, Extent);

            NativeArray<Vector4> colors = default;
            NativeArray<Vector2> bake = default;
            NativeArray<int> ringVisitOrder = default;
            FillExtrusionGraphOutput ext = default;
            try
            {
                FillMeshPipeline.LayerInput input = StyledFillExtrusionTileBuilder.BuildLayerInput(
                    selected, geometry, fc.Paint, 0.0, renderOrigin,
                    out colors, out bake, projection, clip: default);
                ringVisitOrder = input.RingVisitOrder;
                Assert.IsTrue(input.RingVisitOrder.IsCreated, $"{fixtureName}/{label}: fixture must select real work.");
                ext = FillExtrusionMeshGraph.Schedule(input, colors, bake);
                ext.Handle.Complete();
                StyledFillExtrusionTileBuilder.WallColumns walls = ext.Walls;
                Assert.Greater(walls.VertexCount, 0, $"{fixtureName}/{label}: expected wall geometry.");

                var golden = LoadWallGolden(fixtureName, label);
                Assert.AreEqual(golden.VertexCount, walls.VertexCount,
                    $"{fixtureName}/{label}: golden vertex count ({golden.VertexCount}) != build's ({walls.VertexCount}) — a topology change.");
                Assert.AreEqual(golden.Indices.Length, walls.IndexCount,
                    $"{fixtureName}/{label}: golden index count ({golden.Indices.Length}) != build's ({walls.IndexCount}).");

                for (int i = 0; i < walls.IndexCount; i++)
                    Assert.AreEqual(golden.Indices[i], walls.Indices[i], $"{fixtureName}/{label}: Indices[{i}] diverges — a real regression.");

                for (int i = 0; i < walls.VertexCount; i++)
                {
                    StyledFillExtrusionTileBuilder.PositionNormal pn = walls.PositionNormal[i];
                    StyledFillExtrusionTileBuilder.ExtrudeAndBake eb = walls.Extrude[i];
                    Vector4 tan = walls.Tangent[i];
                    Vector4 col = walls.Color[i];

                    AssertBitExact(pn.Position.x, golden.PosHex[i * 3 + 0], fixtureName, label, i, "Position.x");
                    AssertBitExact(pn.Position.y, golden.PosHex[i * 3 + 1], fixtureName, label, i, "Position.y");
                    AssertBitExact(pn.Position.z, golden.PosHex[i * 3 + 2], fixtureName, label, i, "Position.z");

                    AssertBounded(pn.Normal.x, golden.NormHex[i * 3 + 0], WallNormalTangentMaxUlp, fixtureName, label, i, "Normal.x");
                    AssertBounded(pn.Normal.y, golden.NormHex[i * 3 + 1], WallNormalTangentMaxUlp, fixtureName, label, i, "Normal.y");
                    AssertBounded(pn.Normal.z, golden.NormHex[i * 3 + 2], WallNormalTangentMaxUlp, fixtureName, label, i, "Normal.z");

                    // ExtrudeUpAndT.xyz = upA * factor: BOUNDED, not bit-exact — upA is math.normalize(Up[idx]),
                    // and normalize was measured (this file's header + §8's wall-tail addendum) to diverge
                    // from managed data-dependently on SOME inputs. It happened to be bit-exact on every edge
                    // of the square+courtyard fixture but is NOT bit-exact on high-latitude/Spherical (found
                    // running this exact tooth, RED-verified by the discovery itself — a 1-ULP divergence at
                    // ExtrudeUpAndT.x[0] on that fixture is what corrected this from an earlier, overclaimed
                    // bit-exact assumption). .w (=t) stays bit-exact — it is the literal constant 0f/1f, never
                    // touches a projected value.
                    AssertBounded(eb.ExtrudeUpAndT.x, golden.ExtrudeHex[i * 4 + 0], WallNormalTangentMaxUlp, fixtureName, label, i, "ExtrudeUpAndT.x");
                    AssertBounded(eb.ExtrudeUpAndT.y, golden.ExtrudeHex[i * 4 + 1], WallNormalTangentMaxUlp, fixtureName, label, i, "ExtrudeUpAndT.y");
                    AssertBounded(eb.ExtrudeUpAndT.z, golden.ExtrudeHex[i * 4 + 2], WallNormalTangentMaxUlp, fixtureName, label, i, "ExtrudeUpAndT.z");
                    AssertBitExact(eb.ExtrudeUpAndT.w, golden.ExtrudeHex[i * 4 + 3], fixtureName, label, i, "ExtrudeUpAndT.w");
                    AssertBitExact(eb.BakedBaseHeight.x, golden.BakeHex[i * 2 + 0], fixtureName, label, i, "BakedBaseHeight.x");
                    AssertBitExact(eb.BakedBaseHeight.y, golden.BakeHex[i * 2 + 1], fixtureName, label, i, "BakedBaseHeight.y");

                    AssertBounded(tan.x, golden.TanHex[i * 4 + 0], WallNormalTangentMaxUlp, fixtureName, label, i, "Tangent.x");
                    AssertBounded(tan.y, golden.TanHex[i * 4 + 1], WallNormalTangentMaxUlp, fixtureName, label, i, "Tangent.y");
                    AssertBounded(tan.z, golden.TanHex[i * 4 + 2], WallNormalTangentMaxUlp, fixtureName, label, i, "Tangent.z");
                    AssertBitExact(tan.w, golden.TanHex[i * 4 + 3], fixtureName, label, i, "Tangent.w"); // always the constant 1f

                    AssertBitExact(col.x, golden.ColorHex[i * 4 + 0], fixtureName, label, i, "Color.x");
                    AssertBitExact(col.y, golden.ColorHex[i * 4 + 1], fixtureName, label, i, "Color.y");
                    AssertBitExact(col.z, golden.ColorHex[i * 4 + 2], fixtureName, label, i, "Color.z");
                    AssertBitExact(col.w, golden.ColorHex[i * 4 + 3], fixtureName, label, i, "Color.w");
                }
            }
            finally
            {
                if (colors.IsCreated) colors.Dispose();
                if (bake.IsCreated) bake.Dispose();
                if (ringVisitOrder.IsCreated) ringVisitOrder.Dispose();
                ext.Dispose();
                geometry.Dispose();
            }
        }

        /// <summary>
        /// The wall chain honours the tile-buffer clip (UMR-93): it takes the SAME select-or-clip branch on
        /// <c>LayerInput.Clip</c> the roof takes, so a building crossing the tile-buffer window is extruded
        /// from the CLIPPED footprint, not the full one. Before the fix the walls ran
        /// <c>RingSelectJob</c> unconditionally, and with the shipped <c>FillTileBufferClip: 0</c> config —
        /// which decodes to the ENABLED <c>KeepTileUnits(0.0)</c>, only a negative value disables — two
        /// neighbouring tiles each raised a crossing building's whole wall set.
        ///
        /// <para><b>Fixture and the arithmetic behind every number below.</b> Two squares at
        /// <c>Extent = 4096</c>, window <c>[0, 4096]²</c> under <c>KeepTileUnits(0.0)</c>:
        /// <c>straddling</c> at (3900,3900)+500 crosses the window's far corner; <c>whollyOutside</c> at
        /// (5000,5000)+300 lies entirely beyond it. Clipped, <c>whollyOutside</c>'s ring is DROPPED
        /// (<c>RingClipJob</c>: <c>clippedLen == 0 ⇒ continue</c>) and contributes no wall at all, while
        /// <c>straddling</c> becomes (3900,3900) (4096,3900) (4096,4096) (3900,4096) — still 4 vertices,
        /// hence 4 edges, since the wall job closes the ring with <c>(i+1) % len</c>. At 4 vertices +
        /// 6 indices per edge that is <b>16 wall vertices / 24 wall indices</b>. Unclipped, both squares
        /// survive at 4 edges each: <b>32 / 48</b>.</para>
        ///
        /// <para><b>Why 16 and not 8.</b> 8 would be the rejected alternative — walls suppressed along the
        /// two edges the clip CUT. This renderer emits a wall for EVERY edge of the clipped ring, cut edges
        /// included, because what is extruded is the clipped polygon; the cut quads are hidden inside the
        /// opaque solid in steady state and are what keeps a building at the edge of the loaded cover CLOSED
        /// rather than a hollow shell. The one condition that reopens the alternative is translucent
        /// fill-extrusion (<c>depth-and-render-regimes-design.md</c> §6.E) — until then, a "simplification"
        /// to 8 is a regression, and this number is what stops it landing silently.</para>
        ///
        /// <para><b>The disabled-arm control (32/48) is not decoration.</b> Without it assertion 1 would be a
        /// statement about wall emission in general rather than about the clip: it is what pins that the
        /// <c>RingSelectJob</c> arm did not move, inside the tooth that changes.</para>
        ///
        /// <para><b>Reachability witness.</b> The roof vertex counts of the two arms must differ — that is
        /// what proves <c>whollyOutside</c>'s ring really reaches <c>RingClipJob</c>'s drop branch rather
        /// than the fixture quietly ceasing to exercise it. Post-fix the roof and the walls now BOTH lose
        /// that ring; the roof half is the independent half, since the wall counts are what the assertions
        /// below are testing.</para>
        /// </summary>
        [Test]
        public void Walls_HonourTheTileBufferClip()
        {
            var straddling = SquareFeature(3900, 3900, 500);
            var whollyOutside = SquareFeature(5000, 5000, 300);
            IReadOnlyList<IFeature> features = new[] { straddling, whollyOutside };
            FillExtrusion.PaintProperties paint = ConstantHeightPaint(50);
            IProjection projection = new WebMercatorProjection();

            BuildCounts enabled = BuildWallCounts(features, paint, projection, TileBufferClip.KeepTileUnits(0.0));
            BuildCounts disabled = BuildWallCounts(features, paint, projection, TileBufferClip.Disabled);

            Assert.AreNotEqual(disabled.RoofVertexCount, enabled.RoofVertexCount,
                "precondition: the two arms' ROOF vertex counts must differ — if they don't, the " +
                "wholly-outside feature's ring was not dropped by RingClipJob and this fixture is not " +
                "exercising the clip branch it exists to observe.");

            // (1) The clipped arm: whollyOutside dropped entirely, straddling clipped to 4 edges.
            Assert.AreEqual(16, enabled.WallVertexCount,
                "clipped walls: whollyOutside's ring is dropped (0 walls) and straddling clips to a 4-vertex " +
                "ring ⇒ 4 edges × 4 vertices = 16. 32 means the walls ignored the clip; 8 means the cut " +
                "edges were suppressed (the rejected alternative — see this test's doc).");
            Assert.AreEqual(24, enabled.WallIndexCount, "clipped walls: 4 edges × 6 indices = 24.");

            // (2) The disabled-arm control: the RingSelectJob arm is untouched by this fix.
            Assert.AreEqual(32, disabled.WallVertexCount,
                "unclipped walls: both squares survive at 4 edges each ⇒ 8 edges × 4 vertices = 32.");
            Assert.AreEqual(48, disabled.WallIndexCount, "unclipped walls: 8 edges × 6 indices = 48.");
        }

        /// <summary>Roof and wall counts of one <see cref="FillExtrusionMeshGraph.Schedule"/> build — the
        /// per-arm measurement <see cref="Walls_HonourTheTileBufferClip"/> compares.</summary>
        private struct BuildCounts
        {
            public int RoofVertexCount, WallVertexCount, WallIndexCount;
        }

        /// <summary>Runs the extrusion graph over one fixture at one clip setting and returns its counts.</summary>
        /// <param name="features">Tile features to build.</param>
        /// <param name="paint">The layer's fill-extrusion paint.</param>
        /// <param name="projection">Projection to build under.</param>
        /// <param name="clip">The tile-buffer clip the graph's roof AND wall chains both read.</param>
        /// <returns>Roof vertex count plus wall vertex/index counts.</returns>
        private static BuildCounts BuildWallCounts(
            IReadOnlyList<IFeature> features, FillExtrusion.PaintProperties paint,
            IProjection projection, TileBufferClip clip)
        {
            IReadOnlyList<SelectedTileFeature> selected = TestTileMeshBuilder.Selection(features);
            double3 renderOrigin = TileRenderOrigin.Project(ModerateTile, projection);
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, ModerateTile, Extent);
            NativeArray<Vector4> colors = default; NativeArray<Vector2> bake = default;
            NativeArray<int> ringVisitOrder = default;
            FillExtrusionGraphOutput ext = default;
            try
            {
                FillMeshPipeline.LayerInput input = StyledFillExtrusionTileBuilder.BuildLayerInput(
                    selected, geometry, paint, 0.0, renderOrigin, out colors, out bake, projection, clip);
                Assert.IsTrue(input.RingVisitOrder.IsCreated, "precondition: the fixture must select real work.");
                // Capture it: BuildLayerInput hands ownership to the caller, so the finally below disposes
                // nothing unless this assignment happens.
                ringVisitOrder = input.RingVisitOrder;
                ext = FillExtrusionMeshGraph.Schedule(input, colors, bake);
                ext.Handle.Complete();
                return new BuildCounts
                {
                    RoofVertexCount = ext.Roof.TileVertices.Length,
                    WallVertexCount = ext.Walls.VertexCount,
                    WallIndexCount  = ext.Walls.IndexCount,
                };
            }
            finally
            {
                if (colors.IsCreated) colors.Dispose();
                if (bake.IsCreated) bake.Dispose();
                if (ringVisitOrder.IsCreated) ringVisitOrder.Dispose();
                ext.Dispose();
                geometry.Dispose();
            }
        }

        private struct WallGolden
        {
            public uint[] PosHex, NormHex, ExtrudeHex, BakeHex, TanHex, ColorHex;
            public int[] Indices;
            public int VertexCount;
        }

        private static WallGolden LoadWallGolden(string fixtureName, string projectionLabel)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", $"extrusion-wall-golden-{fixtureName}-{projectionLabel}.json");
            Assert.IsTrue(File.Exists(path), $"golden missing: {path}");
            JsonValue root = JsonParser.Parse(File.ReadAllText(path));
            return new WallGolden
            {
                VertexCount = root.Get("observedWallVertexCount").AsInt(),
                PosHex = ParseHexArray(root.Get("positionHex").AsString(null)),
                NormHex = ParseHexArray(root.Get("normalHex").AsString(null)),
                ExtrudeHex = ParseHexArray(root.Get("extrudeUpAndTHex").AsString(null)),
                BakeHex = ParseHexArray(root.Get("bakedBaseHeightHex").AsString(null)),
                TanHex = ParseHexArray(root.Get("tangentHex").AsString(null)),
                ColorHex = ParseHexArray(root.Get("colorHex").AsString(null)),
                Indices = ParseIntArray(root.Get("indices")),
            };
        }

        private static uint[] ParseHexArray(string csv)
        {
            string[] parts = csv.Split(',');
            var result = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = Convert.ToUInt32(parts[i], 16);
            return result;
        }

        private static int[] ParseIntArray(JsonValue arr)
        {
            var items = arr.Items;
            var result = new int[items.Count];
            for (int i = 0; i < items.Count; i++) result[i] = items[i].AsInt();
            return result;
        }

        private static void AssertBitExact(float actual, uint goldenHex, string fixtureName, string label, int index, string field)
        {
            uint actualHex = math.asuint(actual);
            Assert.AreEqual(goldenHex, actualHex,
                $"{fixtureName}/{label}: {field}[{index}] diverges from the bit-exact golden — " +
                $"actual=0x{actualHex:X8} ({actual:R}) golden=0x{goldenHex:X8} ({math.asfloat(goldenHex):R}). A real regression.");
        }

        private static void AssertBounded(float actual, uint goldenHex, int maxUlp, string fixtureName, string label, int index, string field)
        {
            uint actualHex = math.asuint(actual);
            ulong delta = UlpDistance(actualHex, goldenHex);
            Assert.LessOrEqual(delta, (ulong)maxUlp,
                $"{fixtureName}/{label}: {field}[{index}] exceeds its {maxUlp}-ULP ceiling — " +
                $"actual=0x{actualHex:X8} ({actual:R}) golden=0x{goldenHex:X8} ({math.asfloat(goldenHex):R}) delta={delta} ULP.");
        }

        private static ulong ToUlpOrder(uint bits) => (bits & 0x80000000U) != 0 ? ~bits : (bits | 0x80000000U);
        private static ulong UlpDistance(uint a, uint b)
        {
            ulong oa = ToUlpOrder(a), ob = ToUlpOrder(b);
            return oa > ob ? oa - ob : ob - oa;
        }
    }
}
