// Meshing/RibbonJobGeometryTests.cs — line ribbon/paint mesh-build teeth, the shared-buffer line join, and styled fill-extrusion mesh-graph teeth (Burst/Unity.Collections, EditMode only).
//
// The three Line fixtures first (paint bake, ribbon geometry, shared buffer), then the two standalone spike/geometry fixtures, then source-layer buffer sharing, then the two StyledFillExtrusion fixtures.
//
// Contents:
//   LinePaintBuildMeshTests             — data-driven per-feature color baking end-to-end through BuildMeshData.
//   LineRibbonJobTests                  — Direct teeth against RibbonJob (3D, Burst) over a flat centerline (points on the XZ plane, up = +Y, via FlatRibbon).
//   LineSharedBufferTests               — line reads a buffer it shares with the whole source layer, and joins its per-feature colour/width columns by the source-layer ordinal rather than by its own selected-list position.
//   MeshDataThreadWriteSpikeTests       — (Established once as a spike: writing works off-thread, but AllocateWritableMeshData / ApplyAndDisposeWritableMeshData are main-thread only — "CreateNewMeshDatas can only be called from the main thread" — which is why allocation happens at kick and apply…
//   RibbonJobGeometryTests              — First-principles geometry teeth for RibbonJob over a flat centerline (FlatRibbon): unit normals, exact per-join/per-cap vertex counts, the miter→bevel/round cascade, the round-cap pivot, winding, and the fold onset at the documented s_crit.
//   SourceLayerBufferSharingTests       — one materialization per source-layer per tile, shared across BOTH cadences of a kick.
//   StyledFillExtrusionGraphWriteTests  — the naive bound "vertex count > N" does NOT discriminate a mis-wound hole ring (which silently reads as a SECOND EXTERIOR) from a real courtyard — both produce roughly the same total.
//   StyledFillExtrusionMeshTests        — Wall quad layout is a WHITE-BOX assumption these teeth rely on (StyledFillExtrusionTileBuilder.WriteWalls appends exactly 4 vertices per boundary edge, AFTER all roof vertices, in floorA/floorB/roofB/roofA order): a single hole-less convex N-gon footprint…

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.Style;
using Line = MapRenderer.Core.Style.Line;
using Unity.Mathematics;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;
using MapRenderer.Core.Geometry;
using MapRenderer.Jobs.Lines;
using MapRenderer.Core.Json;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Tests.Jobs;
using Color = UnityEngine.Color;                  // aliased: MapRenderer.Core.Expressions has its own Color
using Cysharp.Threading.Tasks;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering.Tile;
using System.Threading;
using Unity.Collections;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Core.Text;
using MapRenderer.Unity.Text;
using MapRenderer.Tests.Tiles;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Fill;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using Object = UnityEngine.Object;


namespace MapRenderer.Tests.Meshing
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // LinePaintBuildMeshTests — data-driven per-feature color baking end-to-end
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Data-driven per-feature color baking end-to-end through
    /// <see cref="StyledLineTileBuilder.BuildMeshData"/>.
    ///
    /// Proves the bake path is wired end-to-end for line features:
    ///   - A match expression on a per-feature property produces ≥2 DISTINCT linearized colors
    ///     baked into Stream3 <see cref="StyledLineTileBuilder.LineWidthColor.Color"/> values.
    ///   - A constant-input expression (match on a non-existent key → default) produces exactly
    ///     1 distinct color across all features, and that colour is the WHITE identity — a constant
    ///     line-color rides the _BaseColor uniform instead of being baked.
    ///
    /// No GPU needed. Tests CPU mesh-data build only.
    /// Uses the "geolines" LineString layer from the fixture (6 features with real MVT geometry).
    /// </summary>
    [TestFixture]
    public class LinePaintBuildMeshTests
    {
        // ── Fixture loader ─────────────────────────────────────────────────────

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
                "sample-tile.bytes not found. Tried walking up from " +
                $"cwd={Directory.GetCurrentDirectory()} and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        /// <summary>
        /// Load geolines features from fixture, inject a discriminating string property.
        /// Reuses real MVT LineString geometry — no hand-encoding.
        /// </summary>
        /// <remarks>A decoded <c>MvtFeature</c> carries no command stream, so the
        /// injected-property doubles are rebuilt as <c>DictionaryFeature</c>s over the fixture's REAL
        /// LineString streams, read from the bytes by <c>MvtFixtureStreams</c>. Same geometry, same order —
        /// only the property bag is synthetic, which is what this fixture was always doing.</remarks>
        private static List<IFeature> LoadGeolinesWithProperty(
            string propKey, params string[] propValues)
        {
            MvtFixtureStreams.Layer layer = MvtFixtureStreams.ReadLayer(LoadFixture(), "geolines");
            Assert.IsNotNull(layer, "geolines layer must be present in fixture.");

            var result = new List<IFeature>();
            int vi = 0;
            for (int fi = 0; fi < layer.Kinds.Count; fi++)
            {
                if (layer.Kinds[fi] != TileGeometryType.LineString) continue;

                string value = vi < propValues.Length ? propValues[vi] : propValues[propValues.Length - 1];
                result.Add(new DictionaryFeature(
                    properties:   new Dictionary<string, Value> { [propKey] = Value.String(value) },
                    geometryType: TileGeometryType.LineString,
                    geometry:     layer.Commands[fi]));
                vi++;

                if (vi >= Math.Max(propValues.Length, 2)) break; // need at least 2 features
            }
            return result;
        }

        // Shared build parameters (tile 0/0/0, merc origin=(0,0)).
        private static readonly TileId TestTileId = new TileId { Z = 0, X = 0, Y = 0 };
        // WriteMeshData takes a double3 render origin (Mercator: (mercX, 0, mercZ)); zero here.
        private static readonly double3 TestOriginMerc = new double3(0.0, 0.0, 0.0);
        private const double TestExtent = 4096.0;
        private const double TestZoom   = 0.0;

        // ── data-driven match → ≥2 distinct baked colors ───────────────────────

        [Test]
        public void BuildMeshData_DataDrivenMatchColor_BakesDistinctColorsPerFeature()
        {
            const string propKey = "__test_cat__";
            var features = LoadGeolinesWithProperty(propKey, "A", "B");
            Assert.GreaterOrEqual(features.Count, 2,
                "Need at least 2 geolines LineString features to run the distinct-color test.");

            // Verify the features are actually LineStrings (guard against fixture change).
            foreach (var f in features)
                Assert.AreEqual(TileGeometryType.LineString, f.GeometryType,
                    "Each geolines feature must be a LineString.");

            // Match expression: A → reddish, B → bluish, default → gray.
            const string matchExpr =
                "[\"match\",[\"get\",\"__test_cat__\"]," +
                "\"A\",[\"rgba\",200,50,50,1]," +
                "\"B\",[\"rgba\",50,50,200,1]," +
                "[\"rgba\",128,128,128,1]]";

            var paintLayer = new Line.StyleLayer
            {
                Id          = "test-geolines",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "geolines",
                Paint       = TestStyle.LinePaint($"{{\"line-color\":{matchExpr},\"line-width\":4}}"),
                Layout      = TestStyle.LineLayout(),
            };
            var paint = paintLayer.Paint;
            var layout = paintLayer.Layout;

            var mda = Mesh.AllocateWritableMeshData(1);
            // The builder BORROWS the source-layer buffer; the caller mints and frees it. The
            // feature list IS the layer here, so ordinals are 0..n-1.
            TileGeometryBuffers geometry =
                TestTileMeshBuilder.Materialize(features, TestTileId, TestExtent);
            try
            {
                SyncMeshWrite.Line(
                    mda[0], TestTileMeshBuilder.Selection(features), geometry, paint, layout, TestZoom,
                    TestOriginMerc, out int vertexCount, out _);

                Assert.Greater(vertexCount, 0,
                    "WriteMeshData must produce geometry for geolines features with a match expression.");

                // Collect distinct (linearized) colors from Stream3, read back straight from the MeshData.
                var s3 = mda[0].GetVertexData<StyledLineTileBuilder.LineWidthColor>(3);
                var distinctColors = new HashSet<(int r, int g, int b)>();
                for (int i = 0; i < vertexCount; i++)
                {
                    var wc = s3[i];
                    // Quantize to 8-bit per channel (avoid float precision mismatches).
                    int r8 = (int)(wc.Color.x * 255f + 0.5f);
                    int g8 = (int)(wc.Color.y * 255f + 0.5f);
                    int b8 = (int)(wc.Color.z * 255f + 0.5f);
                    distinctColors.Add((r8, g8, b8));
                }

                Assert.GreaterOrEqual(distinctColors.Count, 2,
                    $"A match expression mapping ≥2 distinct feature categories must produce " +
                    $"≥2 distinct linearized colors in Stream3 (got {distinctColors.Count}). " +
                    "This proves the data-driven bake path is wired through WriteMeshData.");
            }
            finally
            {
                geometry.Dispose();
                mda.Dispose();
            }
        }

        // ── constant-input control → exactly 1 distinct baked color ─

        [Test]
        public void BuildMeshData_ConstantColorExpression_BakesUniformColor()
        {
            var features = LoadGeolinesWithProperty("__unused_prop__", "X", "X");
            Assert.GreaterOrEqual(features.Count, 2,
                "Need at least 2 geolines features for the constant-color control.");

            // Constant rgba literal: same for all features regardless of properties.
            const string constantExpr = "[\"rgba\",100,150,200,1]";

            var paintLayer = new Line.StyleLayer
            {
                Id          = "test-constant",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "geolines",
                Paint       = TestStyle.LinePaint($"{{\"line-color\":{constantExpr},\"line-width\":4}}"),
                Layout      = TestStyle.LineLayout(),
            };
            var paint = paintLayer.Paint;
            var layout = paintLayer.Layout;

            var mda = Mesh.AllocateWritableMeshData(1);
            // The builder BORROWS the source-layer buffer; the caller mints and frees it. The
            // feature list IS the layer here, so ordinals are 0..n-1.
            TileGeometryBuffers geometry =
                TestTileMeshBuilder.Materialize(features, TestTileId, TestExtent);
            try
            {
                SyncMeshWrite.Line(
                    mda[0], TestTileMeshBuilder.Selection(features), geometry, paint, layout, TestZoom,
                    TestOriginMerc, out int vertexCount, out _);

                Assert.Greater(vertexCount, 0,
                    "WriteMeshData must produce geometry for geolines features with a constant color.");

                // All vertices should have the same baked color (within 1-unit quantization).
                var s3 = mda[0].GetVertexData<StyledLineTileBuilder.LineWidthColor>(3);
                var distinctColors = new HashSet<(int r, int g, int b)>();
                for (int i = 0; i < vertexCount; i++)
                {
                    var wc = s3[i];
                    int r8 = (int)(wc.Color.x * 255f + 0.5f);
                    int g8 = (int)(wc.Color.y * 255f + 0.5f);
                    int b8 = (int)(wc.Color.z * 255f + 0.5f);
                    distinctColors.Add((r8, g8, b8));
                }

                Assert.AreEqual(1, distinctColors.Count,
                    $"A constant-color expression must produce exactly 1 distinct baked color " +
                    $"across all vertices (got {distinctColors.Count} distinct). " +
                    "If more than 1, the data-driven path is incorrectly varying a constant.");

                // That single colour must be WHITE. A constant line-color is NOT baked — it rides the
                // _BaseColor uniform (BindLinePaintToApplier binds it for exactly !DependsOnFeature) and the
                // fragment multiplies the two, so baking it as well renders the colour squared. Without this
                // half the count assertion above passes whether the constant is baked or not, and measures
                // nothing about which carrier holds it.
                var only = new List<(int r, int g, int b)>(distinctColors)[0];
                Assert.AreEqual((255, 255, 255), only,
                    $"The one baked colour for a CONSTANT line-color must be the white identity, not the " +
                    $"evaluated rgba(100,150,200) — the uniform carries it. Reading the styled colour here " +
                    $"means the vertex bake still runs for a constant and the layer renders colour-squared.");
            }
            finally
            {
                geometry.Dispose();
                mda.Dispose();
            }
        }

        // ── data-driven line-width → ≥2 distinct baked WidthScales ───

        [Test]
        public void BuildMeshData_DataDrivenWidth_BakesDistinctWidthScalesPerFeature()
        {
            const string propKey = "__test_width_cat__";
            var features = LoadGeolinesWithProperty(propKey, "thin", "thick");
            Assert.GreaterOrEqual(features.Count, 2,
                "Need at least 2 geolines LineString features for the distinct-width test.");

            // Match expression: thin → 2px, thick → 10px.
            const string widthExpr =
                "[\"match\",[\"get\",\"__test_width_cat__\"]," +
                "\"thin\",2.0," +
                "\"thick\",10.0," +
                "2.0]";

            var paintLayer = new Line.StyleLayer
            {
                Id          = "test-dd-width",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "geolines",
                // line-width is data-driven (Feature kind); line-color is constant.
                Paint       = TestStyle.LinePaint($"{{\"line-width\":{widthExpr}}}"),
                Layout      = TestStyle.LineLayout(),
            };
            var paint = paintLayer.Paint;
            var layout = paintLayer.Layout;

            // Confirm the paint classified WidthKind as Feature (gate for the bake).
            Assert.AreEqual(MapRenderer.Core.Expressions.ExpressionKind.Feature, paint.WidthKind,
                "WidthKind must be Feature for a [\"get\",...] match expression.");

            var mda = Mesh.AllocateWritableMeshData(1);
            // The builder BORROWS the source-layer buffer; the caller mints and frees it. The
            // feature list IS the layer here, so ordinals are 0..n-1.
            TileGeometryBuffers geometry =
                TestTileMeshBuilder.Materialize(features, TestTileId, TestExtent);
            try
            {
                SyncMeshWrite.Line(
                    mda[0], TestTileMeshBuilder.Selection(features), geometry, paint, layout, TestZoom,
                    TestOriginMerc, out int vertexCount, out _);

                Assert.Greater(vertexCount, 0,
                    "WriteMeshData must produce geometry for geolines features with a data-driven width.");

                // Collect distinct quantized WidthScale values from Stream3 (thin=2, thick=10 well-separated).
                var s3 = mda[0].GetVertexData<StyledLineTileBuilder.LineWidthColor>(3);
                var distinctWidths = new HashSet<int>();
                for (int i = 0; i < vertexCount; i++)
                {
                    var wc = s3[i];
                    int w = (int)(wc.WidthScale + 0.5f);
                    distinctWidths.Add(w);
                }

                Assert.GreaterOrEqual(distinctWidths.Count, 2,
                    $"A match expression mapping ≥2 distinct feature widths must produce " +
                    $"≥2 distinct WidthScale values in Stream3 (got {distinctWidths.Count}). " +
                    "This proves the data-driven width bake path is wired through WriteMeshData.");
            }
            finally
            {
                geometry.Dispose();
                mda.Dispose();
            }
        }

        // ── data-driven line-opacity → ≥2 distinct baked alpha values ─

        [Test]
        public void BuildMeshData_DataDrivenOpacity_BakesDistinctAlphaPerFeature()
        {
            const string propKey = "__test_opacity_cat__";
            var features = LoadGeolinesWithProperty(propKey, "transparent", "opaque");
            Assert.GreaterOrEqual(features.Count, 2,
                "Need at least 2 geolines LineString features for the distinct-opacity test.");

            // Match expression: transparent → 0.2, opaque → 1.0.
            const string opacityExpr =
                "[\"match\",[\"get\",\"__test_opacity_cat__\"]," +
                "\"transparent\",0.2," +
                "\"opaque\",1.0," +
                "1.0]";

            var paintLayer = new Line.StyleLayer
            {
                Id          = "test-dd-opacity",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "geolines",
                // line-opacity is data-driven (Feature kind); other properties at defaults.
                Paint       = TestStyle.LinePaint($"{{\"line-opacity\":{opacityExpr}}}"),
                Layout      = TestStyle.LineLayout(),
            };
            var paint = paintLayer.Paint;
            var layout = paintLayer.Layout;

            // Confirm the paint classified OpacityKind as Feature.
            Assert.AreEqual(MapRenderer.Core.Expressions.ExpressionKind.Feature, paint.OpacityKind,
                "OpacityKind must be Feature for a [\"get\",...] match expression.");

            var mda = Mesh.AllocateWritableMeshData(1);
            // The builder BORROWS the source-layer buffer; the caller mints and frees it. The
            // feature list IS the layer here, so ordinals are 0..n-1.
            TileGeometryBuffers geometry =
                TestTileMeshBuilder.Materialize(features, TestTileId, TestExtent);
            try
            {
                SyncMeshWrite.Line(
                    mda[0], TestTileMeshBuilder.Selection(features), geometry, paint, layout, TestZoom,
                    TestOriginMerc, out int vertexCount, out _);

                Assert.Greater(vertexCount, 0,
                    "WriteMeshData must produce geometry for geolines features with a data-driven opacity.");

                // Collect distinct quantized alpha values from Stream3 Color.w (0.2→~51/255; 1.0→255/255).
                var s3 = mda[0].GetVertexData<StyledLineTileBuilder.LineWidthColor>(3);
                var distinctAlphas = new HashSet<int>();
                for (int i = 0; i < vertexCount; i++)
                {
                    var wc = s3[i];
                    int a8 = (int)(wc.Color.w * 255f + 0.5f);
                    distinctAlphas.Add(a8);
                }

                Assert.GreaterOrEqual(distinctAlphas.Count, 2,
                    $"A match expression mapping ≥2 distinct feature opacities must produce " +
                    $"≥2 distinct alpha values in Stream3 Color.w (got {distinctAlphas.Count}). " +
                    "This proves the data-driven opacity bake path is wired through WriteMeshData.");
            }
            finally
            {
                geometry.Dispose();
                mda.Dispose();
            }
        }

        // ── line-miter-limit threaded ──────────────────────────────────────────

        /// <summary>
        /// Before this stage, <see cref="SyncMeshWrite.Line"/>'s production predecessor hardcoded the ribbon job's
        /// miter limit to <c>2.0</c> regardless of style, so a style-authored <c>line-miter-limit</c> was
        /// silently ignored. A synthetic 90° corner (f = 1/cos(45°) = √2 ≈ 1.414) stays a sharp miter under
        /// the default limit (2.0 — <c>RibbonJobGeometryTests.RightAngle_MiterJoin_ExactVertexCount</c>: 6
        /// verts) but must BEVEL under a tight <c>"line-miter-limit": 1.0</c> (√2 &gt; 1.0 —
        /// <c>RibbonJobGeometryTests.SharpAngle_ExplicitBevel_VertexCountExactlyOneBevelExtra</c>: 7 verts).
        /// The vertex-count delta between those two shapes is exactly what pins the threading fix: reading 6
        /// here means the hardcoded 2.0 is still in effect.
        /// </summary>
        [Test]
        public void BuildMeshData_MiterLimitThreaded_SharpCornerBevelsUnderTightLimit()
        {
            // MoveTo(0,0) → LineTo(1024,0) → LineTo(1024,1024): 90° corner, hand-encoded MVT command stream
            // (zigzag-delta encoded per the MVT spec — see MapRenderer.Tests.FullExtentRingCommandStream for
            // the same encoding worked out digit-by-digit). Extent 4096.
            var commands = new uint[] { 9, 0, 0, 18, 2048, 0, 0, 2048 };
            var feature  = new DictionaryFeature(geometryType: TileGeometryType.LineString, geometry: commands);
            var features = new List<IFeature> { feature };

            var styleLayer = new Line.StyleLayer
            {
                Id          = "test-miter-limit",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "test",
                Paint       = TestStyle.LinePaint("{\"line-width\":4}"),
                Layout      = TestStyle.LineLayout("{\"line-miter-limit\":1.0}"),
            };
            var paint  = styleLayer.Paint;
            var layout = styleLayer.Layout;

            Assert.AreEqual(1.0, layout.MiterLimit, 1e-9,
                "Precondition: layout must parse the tight miter-limit — otherwise this tooth checks nothing.");

            int vertexCount = TestTileMeshBuilder.LineVertexCount(
                features, paint, layout, zoom: 0.0, extent: 4096.0,
                id: new TileId { Z = 0, X = 0, Y = 0 }, origin: double2.zero);

            Assert.AreEqual(7, vertexCount,
                $"line-miter-limit: 1.0 at a 90° corner (f=1.414 > 1.0) must bevel (7 verts), not stay a " +
                $"sharp miter (6 verts). Got {vertexCount} — StyledLineTileBuilder must thread " +
                "layout.MiterLimit into RibbonJob instead of hardcoding 2.0.");
        }

        // ── line-round-limit threaded ────────────────────────────────────────────

        /// <summary>
        /// Hardcoding <c>RoundLimit</c> in <see cref="StyledLineTileBuilder"/> (instead of
        /// reading <c>layout.RoundLimit</c>) passes the entire suite — the same latent-bug class
        /// <c>BuildMeshData_MiterLimitThreaded_SharpCornerBevelsUnderTightLimit</c> exists to catch for
        /// <c>MiterLimit</c>. Reuses its 90° corner (f = 1/cos(45°) = √2 ≈ 1.414) with
        /// <c>line-join: round</c>: under <c>line-round-limit: 1.05</c> the corner is NOT shallow (√2 > 1.05)
        /// so the fan is preserved (11 verts — <c>RibbonJobGeometryTests.RightAngle_RoundJoin_ExactVertexCount</c>);
        /// under <c>line-round-limit: 2.0</c> the SAME corner IS shallow (√2 ≤ 2.0) and collapses to the
        /// (unclamped, since miterLimit stays default 2.0 ⇒ √2 ≤ 2.0 does not bevel) miter path (6 verts —
        /// <c>RibbonJobGeometryTests.RightAngle_MiterJoin_ExactVertexCount</c>). A build that hardcodes
        /// <c>RoundLimit</c> reads the SAME count for both styles.
        /// </summary>
        [Test]
        public void BuildMeshData_RoundLimitThreaded_SameCornerFansOrCollapsesByStyle()
        {
            // Same 90° corner as BuildMeshData_MiterLimitThreaded_SharpCornerBevelsUnderTightLimit.
            var commands = new uint[] { 9, 0, 0, 18, 2048, 0, 0, 2048 };
            var id       = new TileId { Z = 0, X = 0, Y = 0 };

            int VertexCountAtRoundLimit(double roundLimit)
            {
                var feature  = new DictionaryFeature(geometryType: TileGeometryType.LineString, geometry: commands);
                var features = new List<IFeature> { feature };
                var styleLayer = new Line.StyleLayer
                {
                    Id          = $"test-round-limit-{roundLimit}",
                    LayerType   = StyleLayerType.Line,
                    SourceLayer = "test",
                    Paint       = TestStyle.LinePaint("{\"line-width\":4}"),
                    Layout      = TestStyle.LineLayout($"{{\"line-join\":\"round\",\"line-round-limit\":{roundLimit}}}"),
                };
                var paint  = styleLayer.Paint;
                var layout = styleLayer.Layout;
                Assert.AreEqual(roundLimit, layout.RoundLimit, 1e-9,
                    "Precondition: layout must parse the requested round-limit — otherwise this straddle " +
                    "checks nothing.");

                return TestTileMeshBuilder.LineVertexCount(
                    features, paint, layout, zoom: 0.0, extent: 4096.0, id: id, origin: double2.zero);
            }

            int fanCount       = VertexCountAtRoundLimit(1.05); // √2 > 1.05 ⇒ NOT shallow ⇒ fan preserved
            int collapsedCount = VertexCountAtRoundLimit(2.0);  // √2 ≤ 2.0  ⇒ shallow ⇒ collapses to miter

            Assert.AreEqual(11, fanCount,
                $"line-round-limit: 1.05 at a 90° corner must preserve the round fan (11 verts). Got {fanCount}.");
            Assert.AreEqual(6, collapsedCount,
                $"line-round-limit: 2.0 at the SAME 90° corner must collapse to the (unclamped) miter path " +
                $"(6 verts). Got {collapsedCount} — if this reads 11 (same as the fan case), " +
                "StyledLineTileBuilder is not threading layout.RoundLimit into RibbonJob.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineRibbonJobTests — direct teeth against RibbonJob over a flat centerline
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Direct teeth against <see cref="RibbonJob"/> (3D, Burst) over a flat centerline (points on the XZ
    /// plane, <c>up = +Y</c>, via <see cref="FlatRibbon"/>). The first-principles properties live in
    /// <c>RibbonJobGeometryTests</c>; there is no managed reference tessellator. What remains
    /// here are the teeth that were always direct assertions on the job's own output, not comparisons —
    /// they name themselves as such in their own doc comments below.
    /// </summary>
    [TestFixture]
    public class LineRibbonJobTests
    {
        private static string FixturePath =>
            Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");

        // ── Oracle harness ──────────────────────────────────────────────────────────────────────

        private static (LineRibbonVertex[] verts, int[] indices) RunJob(
            double2[] pts, JoinType join, CapType cap, double miterLimit, int roundSegments,
            double roundLimit = 1.05)
            => FlatRibbon.Build(pts, join, cap, miterLimit, roundSegments, roundLimit);

        // ── Degenerate / smoke ───────────────────────────────────────────────────────────────────

        [Test]
        public void LessThanTwoPoints_ProducesZeroOutput()
        {
            var (jv, ji) = RunJob(new[] { new double2(5, 5) }, JoinType.Miter, CapType.Butt, 2.0, 4);
            Assert.AreEqual(0, jv.Length, "< 2 points → 0 verts.");
            Assert.AreEqual(0, ji.Length, "< 2 points → 0 indices.");
        }

        // ── line-round-limit: shallow round joins collapse to miter ────────────────────────────────

        /// <summary>Mixed-regime fixture: a shallow (20°, collapses to miter) join followed by a sharp (90°,
        /// fan preserved) join in the SAME polyline.</summary>
        private static readonly double2[] MixedRoundLimitFixture =
        {
            new double2(0, 0),
            new double2(10, 0),
            new double2(19.396926207859085, 3.4202014332566878),  // 20° turn — shallow, collapses to miter
            new double2(15.976724774602397, 12.817127641115771),  // further 90° turn — sharp, fan preserved
        };

        /// <summary>
        /// Direct assertion on the raw Job output. Exact counts worked out by hand from
        /// <see cref="MixedRoundLimitFixture"/>'s emission shape — see the inline breakdown below.
        /// </summary>
        [Test]
        public void RoundLimit_ShallowCorner_JobEmitsMiterVertexCountDirectly()
        {
            var (jv, ji) = RunJob(MixedRoundLimitFixture, JoinType.Round, CapType.Butt, 2.0, 4, roundLimit: 1.05);

            // Verts: start cap(2) + shallow-join-as-miter(2, NOT the 7-vert fan) + sharp round join
            // (7: 1 inner + 1 arcStart + 4 fan intermediates + 1 arcEnd) + last seg(2) = 13.
            // Indices: start-cap→join1 connecting quad(6) + join1→join2 connecting quad(6, part of the round
            // join's own emission) + join2's 4 fan triangles(12) + join2's 1 closing fan triangle(3) +
            // last-seg quad(6) = 33. (Matches RightAngle_RoundJoin_ExactVertexCount's 11v/27i for a single
            // round join, plus this fixture's extra shallow-as-miter join: +2v/+6i.)
            Assert.AreEqual(13, jv.Length,
                $"Mixed shallow+sharp fixture: shallow join must contribute only 2 verts (miter), not a " +
                $"7-vert fan. Expected 13 total verts, got {jv.Length}.");
            Assert.AreEqual(33, ji.Length,
                $"Mixed shallow+sharp fixture: expected 33 total indices (no fan triangles at the shallow " +
                $"join). Got {ji.Length}.");
        }

        /// <summary>
        /// Direct analytic assertion on the Burst side: the inner-join vertex at a 90° left turn (bevel)
        /// sits at the analytic intersection of the two concave offset lines — hand-computed here, not
        /// read from a managed arm.
        /// </summary>
        [Test]
        public void InnerJoin_Bevel_90LeftTurn_Across_MatchesAnalyticIntersection()
        {
            var pts = new[] { new double2(0, 0), new double2(10, 0), new double2(10, 10) };
            var (jv, _) = RunJob(pts, JoinType.Bevel, CapType.Butt, 2.0, 4);

            double3 corner = new double3(10, 0, 0);
            int found = -1, count = 0;
            for (int i = 0; i < jv.Length; i++)
            {
                double3 pos = jv[i].Position;
                if (math.abs(pos.x - corner.x) < 1e-9 && math.abs(pos.y - corner.y) < 1e-9 &&
                    math.abs(pos.z - corner.z) < 1e-9 && jv[i].Side == +1f) // left turn ⇒ concave Side == +1
                {
                    found = i;
                    count++;
                }
            }
            Assert.AreEqual(1, count, $"Expected exactly one inner-join vertex at the corner with Side=+1. Found {count}.");

            double3 across = jv[found].Across;
            Assert.AreEqual(-1.0, across.x, 1e-9, $"Across.x should be -1.0. Got {across.x:G17}.");
            Assert.AreEqual(0.0, across.y, 1e-9, $"Across.y should be 0.0. Got {across.y:G17}.");
            Assert.AreEqual(1.0, across.z, 1e-9, $"Across.z should be 1.0. Got {across.z:G17}.");

            double len = math.length(across);
            Assert.AreEqual(1.4142135623730951, len, 1e-9, $"|Across| should be √2 = 1.4142135623730951. Got {len:G17}.");
        }

        // ── Fixture-wide robustness over real line geometry (geolines layer) ────────────────────────

        /// <summary>
        /// Runs <see cref="RibbonJob"/> over every real line path in the fixture's geolines layer and
        /// asserts basic geometric health: finite positions, an index count that is a multiple of 3, and
        /// a flat-mapping's out-of-plane component (<c>Across.y</c>) staying zero. No longer a
        /// differential comparison — the managed reference it once ran against is retired.
        /// </summary>
        [Test]
        public void Fixture_Geolines_RibbonJob_ProducesHealthyGeometry()
        {
            FileAssert.Exists(FixturePath);
            // The command streams come from the bytes (MvtFixtureStreams), not off a decoded
            // feature — this must not share its input path with production's decoder.
            var layer = MvtFixtureStreams.ReadLayer(File.ReadAllBytes(FixturePath), "geolines");
            Assert.IsNotNull(layer, "geolines layer present in fixture");

            int pathsChecked = 0;
            for (int fi = 0; fi < layer.Kinds.Count; fi++)
            {
                if (layer.Kinds[fi] != TileGeometryType.LineString || layer.Commands[fi] == null)
                    continue;

                List<List<double2>> paths = MvtGeometry.Decode(layer.Commands[fi]);
                foreach (var path in paths)
                {
                    if (path.Count < 2) continue;

                    var (jv, ji) = RunJob(path.ToArray(), JoinType.Miter, CapType.Butt, 2.0, 4);
                    string label = $"geolines#{fi}";

                    Assert.AreEqual(0, ji.Length % 3, $"{label}: index count must be a multiple of 3.");
                    foreach (var v in jv)
                    {
                        Assert.IsFalse(double.IsNaN(v.Position.x) || double.IsNaN(v.Position.y) || double.IsNaN(v.Position.z),
                            $"{label}: Position must be finite.");
                        Assert.IsFalse(double.IsNaN(v.Across.x) || double.IsNaN(v.Across.y) || double.IsNaN(v.Across.z),
                            $"{label}: Across must be finite.");
                        Assert.AreEqual(0.0, v.Across.y, 1e-9, $"{label}: flat mapping must keep Across.y == 0.");
                        Assert.AreEqual(0.0, v.Position.y, 1e-9, $"{label}: flat mapping must keep Position.y == 0.");
                    }
                    pathsChecked++;
                }
            }

            Assert.Greater(pathsChecked, 0, "expected at least one geoline path to build");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineSharedBufferTests — line reads a shared buffer, joined by source-layer ordinal
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Line reads a buffer it <b>shares</b> with the whole source
    /// layer, and joins its per-feature colour/width columns by the source-layer <b>ordinal</b> rather than by
    /// its own selected-list position.
    ///
    /// <para>Both tests drive the production line path through
    /// <see cref="TestTileMeshBuilder.BuildLineFromLayer"/>, which builds from the layer's borrowed buffer
    /// through <c>StyledLineTileBuilder.BuildLayerInput</c>.</para>
    ///
    /// <para><b>The production configuration is the one under test</b> (the standing check). Every existing
    /// line instrument in the repo — <c>StyledLineBufferParityTests</c>, <c>LineRibbonJobTests</c>, every line
    /// pixel suite — hands the builder a feature list that <i>is</i> the whole layer, so slot and ordinal
    /// coincide and the ordinal join is inert. This fixture is the only one where they differ:
    /// the style layer's filter admits a <b>strict subset</b> (ordinals <c>[0, 3, 4, 5]</c> of six), the
    /// unselected features <b>have rings</b> — including one that is a <c>LineString</c>, so a missing
    /// selection gate produces line geometry rather than nothing — and a <b>Polygon is selected</b> and
    /// interleaved among the selected LineStrings, so the kind gate stays load-bearing and independently
    /// falsifiable.</para>
    /// </summary>
    [TestFixture]
    public class LineSharedBufferTests : BaseTestFixture
    {

        /// <summary>The synthetic layers built above own <c>Allocator.Persistent</c> buffers.</summary>
        protected override void OnTearDown()
        {
            try { TestDecodedTiles.DisposeAll(); }
            finally { base.OnTearDown(); }
        }
        private const double Extent = 4096.0;
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };
        private const double Zoom = 0.0;

        // Data-driven on `cls`, so the colour and width columns are DISCRIMINATING: a column read at the
        // wrong index lands a visibly different value, which a constant paint could never show.
        private const string PaintJson =
            @"{""line-color"": [""match"", [""get"", ""cls""],
                 ""a"", ""#ff0000"", ""b"", ""#00ff00"", ""c"", ""#0000ff"", ""#ffffff""],
               ""line-width"": [""match"", [""get"", ""cls""],
                 ""a"", 2, ""b"", 4, ""c"", 6, 1]}";

        // Excludes cls == "skip". Two of the six layer features carry it, so the selection is a STRICT
        // subset and its ordinals are NOT 0..n-1 — the discriminator the whole fixture rests on.
        private const string FilterJson = @"[""!="", ""cls"", ""skip""]";

        // ── per-feature attribution over a shared layer buffer ─────────────────────────────────────

        /// <summary>
        /// The mesh built from the SIX-feature source layer, with only four features selected, is
        /// element-wise identical (positions, colours, width scales, indices) to the mesh built from the three
        /// drawable features alone with an identity selection.
        ///
        /// <para><b>Catches:</b> indexing <c>featColors</c>/<c>featWidths</c> by the selected-list slot (or by
        /// ring-order position) instead of by the layer ordinal — the exact re-base performed. Under the
        /// slot form, ordinal 3's colour is read out of slot 3 (which holds feature <c>c</c>'s value) and
        /// ordinal 5 reads past the selected features entirely; the geometry stays right and only the colours
        /// and widths permute, which no vertex-count assertion can see.</para>
        ///
        /// <para>Also catches a missing selection gate (the unselected <c>LineString</c> at ordinal 2 would
        /// add a ribbon) and a missing kind gate (the selected <c>Polygon</c> at ordinal 4 would add two).</para>
        /// </summary>
        [Test]
        public void LineSharedLayerBuffer_AttributesColourAndWidthByOrdinal_NotBySelectedSlot()
        {
            IReadOnlyList<IFeature> layerFeatures = SharedLayerFeatures();
            IReadOnlyList<SelectedTileFeature> selection = SelectFromLayer(layerFeatures);

            // ── Non-vacuity #1: the selection really is a strict, NON-IDENTITY subset. Without this the
            //    slot-vs-ordinal mixup is inert and every equality below is true for the wrong reason.
            Assert.AreEqual(6, layerFeatures.Count, "precondition: the source layer carries six features");
            Assert.AreEqual(4, selection.Count,
                "precondition: the style layer's filter must admit a STRICT SUBSET of the layer");
            CollectionAssert.AreEqual(new[] { 0, 3, 4, 5 }, Ordinals(selection),
                "precondition: the admitted ordinals must NOT be 0..n-1 — an identity selection makes slot " +
                "and ordinal coincide and this whole fixture blind");

            // ── Non-vacuity #2: the features the filter rejected carry real rings, one of them a LineString,
            //    so a missing selection gate is observable as EXTRA line geometry rather than as nothing.
            Assert.AreEqual(TileGeometryType.LineString, layerFeatures[2].GeometryType,
                "precondition: the unselected feature at ordinal 2 must be a LineString — an unselected " +
                "POLYGON would be filtered out by the kind gate anyway, leaving the selection gate unobserved");
            Assert.AreEqual(TileGeometryType.Polygon, layerFeatures[4].GeometryType,
                "precondition: a POLYGON must be among the SELECTED features, interleaved between selected " +
                "LineStrings — that is what keeps the kind gate load-bearing here");

            Mesh control = Track(BuildControl());
            Mesh shared  = Track(BuildShared(layerFeatures, selection));
            {
                Assert.IsNotNull(control, "non-vacuity: the drawable-features-only control must build a mesh");
                Assert.IsNotNull(shared,  "the shared-layer build must produce the same mesh, not nothing");
                Assert.Greater(control.vertexCount, 0, "non-vacuity: the control carries vertices");

                Vector3[] controlPos = control.vertices;
                Vector3[] sharedPos  = shared.vertices;
                Color[]   controlCol = control.colors;
                Color[]   sharedCol  = shared.colors;
                var controlWidth = new List<Vector4>(); control.GetUVs(2, controlWidth); // TexCoord2.x = widthScale
                var sharedWidth  = new List<Vector4>(); shared.GetUVs(2, sharedWidth);

                // ── Non-vacuity #3: the columns under test really do vary across the fixture. A single
                //    colour (or a single width) would make a permuted read indistinguishable from a correct one.
                Assert.GreaterOrEqual(DistinctCount(controlCol), 3,
                    "precondition: the control must carry at least three DISTINCT colours — a constant " +
                    "colour column cannot detect a permuted read");
                Assert.GreaterOrEqual(DistinctWidths(controlWidth), 3,
                    "precondition: the control must carry at least three DISTINCT width scales, for the " +
                    "same reason");

                Assert.AreEqual(control.vertexCount, shared.vertexCount,
                    "the four unselected/undrawable rings in the shared layer must contribute EXACTLY " +
                    "nothing — a differing count means the selection gate or the kind gate is missing");
                CollectionAssert.AreEqual(control.triangles, shared.triangles,
                    "…including the index buffer, element for element: ring emission order is draw order");

                for (int i = 0; i < controlPos.Length; i++)
                {
                    Assert.AreEqual(controlPos[i], sharedPos[i], $"vertex {i} position");
                    Assert.AreEqual(controlCol[i], sharedCol[i],
                        $"vertex {i} COLOUR — a mismatch here is the slot-vs-ordinal mis-join: the geometry " +
                        "is right and the per-feature column landed on the wrong feature");
                    Assert.AreEqual(controlWidth[i].x, sharedWidth[i].x,
                        $"vertex {i} WIDTH SCALE — the second per-feature column, joined through the same index");
                }
            }
        }

        // ── ring emission order ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The rings the shared build emits are, in order, exactly the selected LineString features'
        /// rings in <b>decode order</b>: feature <c>a</c>'s ring, then <c>b</c>'s, then <c>c</c>'s two, and
        /// nothing else.
        ///
        /// <para>The ordering argument — <c>FeatureSelector</c> appends in <c>Features</c> order, so
        /// selected order IS decode order, so iterating the layer buffer with a selection gate reproduces it —
        /// <b>is an argument, not a measurement.</b> This is the measurement, and it pins WHICH ring is where
        /// rather than only how many there are: every ring in the fixture is authored HORIZONTAL, at a
        /// distinct tile <c>y</c>, so each ring collapses to exactly one render-space <c>z</c> and the
        /// vertex stream's <c>z</c>-run sequence names the rings in emission order. The expected sequence is
        /// built from three <b>independent single-feature builds</b>, not from the shared build itself.</para>
        ///
        /// <para><b>Catches:</b> iterating the layer's rings without the selection gate (the unselected
        /// LineString at ordinal 2 is authored at <c>y = 1000</c>, i.e. BETWEEN two selected rings, so it
        /// inserts a fifth run in the middle rather than at an end); a per-feature gather that visits the
        /// selected list in a different order; a polygon ring reaching the ribbon (its rings are not
        /// horizontal, so they shatter the run structure).</para>
        /// </summary>
        [Test]
        public void LineRingOrder_OverTheSharedBuffer_IsDecodeOrderOfTheSelectedLineRings()
        {
            IReadOnlyList<IFeature> layerFeatures = SharedLayerFeatures();
            IReadOnlyList<SelectedTileFeature> selection = SelectFromLayer(layerFeatures);

            Mesh shared = Track(BuildShared(layerFeatures, selection));
            Mesh aOnly  = Track(BuildAlone(layerFeatures[0]));
            Mesh bOnly  = Track(BuildAlone(layerFeatures[3]));
            Mesh cOnly  = Track(BuildAlone(layerFeatures[5]));
            {
                Assert.IsNotNull(shared, "the shared-layer build must produce a mesh");

                List<float> expected = new List<float>();
                expected.AddRange(RingZSequence(aOnly));
                expected.AddRange(RingZSequence(bOnly));
                expected.AddRange(RingZSequence(cOnly));

                // ── Non-vacuity: the oracle really does name four distinct rings, and the ORDER of the
                //    expected sequence is information (a constant sequence would pin nothing).
                Assert.AreEqual(4, expected.Count,
                    "precondition: the three drawable features contribute 1 + 1 + 2 = 4 rings");
                CollectionAssert.AllItemsAreUnique(expected,
                    "precondition: each ring must occupy its OWN render-space z, or the run sequence cannot " +
                    "distinguish a permutation from the correct order");

                List<float> actual = RingZSequence(shared);

                Assert.AreEqual(4, actual.Count,
                    "the shared build must emit exactly four rings. A fifth means the unselected LineString " +
                    "at ordinal 2 (authored at y = 1000, between two selected rings) reached the ribbon — " +
                    "i.e. the selection gate is missing; more than five means a polygon ring did.");
                CollectionAssert.AreEqual(expected, actual,
                    "the emitted rings must appear in DECODE order — feature a, feature b, then feature c's " +
                    "two rings in their own decode order. The expected sequence comes from three independent " +
                    "single-feature builds, so this pins WHICH ring is where, not merely how many.");
            }
        }

        // ── Fixture ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The source layer, in decode order. Every LineString ring is HORIZONTAL and at its own tile
        /// <c>y</c> — the run oracle of
        /// <c>LineRingOrder_OverTheSharedBuffer_IsDecodeOrderOfTheSelectedLineRings</c> depends on it; the
        /// polygons are NOT horizontal.
        /// </summary>
        private static IReadOnlyList<IFeature> SharedLayerFeatures() => new List<IFeature>
        {
            /* 0 */ LineFeature("a",    MvtCommandStream.Ring( 400,  500, 1200,  500, 2000,  500)),
            /* 1 */ PolygonFeature("skip", MvtCommandStream.Ring( 600,  900, 1400,  900, 1400, 1700,  600, 1700)),
            /* 2 */ LineFeature("skip", MvtCommandStream.Ring( 500, 1000, 1500, 1000)),
            /* 3 */ LineFeature("b",    MvtCommandStream.Ring( 300, 1500, 1100, 1500, 2100, 1500)),
            /* 4 */ PolygonFeature("a", MvtCommandStream.Ring(2500, 1800, 3300, 1800, 3300, 2600, 2500, 2600)),
            /* 5 */ LineFeature("c",    MvtCommandStream.Ring( 400, 2000, 1600, 2000),
                                        MvtCommandStream.Ring( 700, 2500, 2400, 2500)),
        };

        /// <summary>The three drawable features alone, as their OWN layer with an identity selection — the
        /// control arm. Ordinal == slot here, which is the configuration every pre-existing line
        /// instrument runs in.</summary>
        private static Mesh BuildControl()
        {
            IReadOnlyList<IFeature> layer = SharedLayerFeatures();
            var control = new List<IFeature> { layer[0], layer[3], layer[5] };
            return TestTileMeshBuilder.BuildLineFromLayer(
                control, TestTileMeshBuilder.Selection(control), Paint(), Layout(), Zoom, Extent, Tile,
                double2.zero);
        }

        private static Mesh BuildShared(
            IReadOnlyList<IFeature> layerFeatures, IReadOnlyList<SelectedTileFeature> selection)
            => TestTileMeshBuilder.BuildLineFromLayer(
                layerFeatures, selection, Paint(), Layout(), Zoom, Extent, Tile, double2.zero);

        private static Mesh BuildAlone(IFeature feature)
        {
            var one = new List<IFeature> { feature };
            return TestTileMeshBuilder.BuildLineFromLayer(
                one, TestTileMeshBuilder.Selection(one), Paint(), Layout(), Zoom, Extent, Tile, double2.zero);
        }

        /// <summary>Runs the REAL selection seam, so the ordinals under test are the ones production
        /// computes rather than hand-written constants.</summary>
        private static IReadOnlyList<SelectedTileFeature> SelectFromLayer(IReadOnlyList<IFeature> features)
        {
            var selected = new List<SelectedTileFeature>();
            FeatureSelector.SelectFeatures(
                StyleLayerWithFilter(), TestDecodedTiles.Of("p2", Tile, features, (uint)Extent).GetLayer("p2"),
                Zoom, selected);
            return selected;
        }

        private static Line.StyleLayer StyleLayerWithFilter() => new Line.StyleLayer
        {
            Id          = "p2-line",
            LayerType   = StyleLayerType.Line,
            SourceLayer = "p2",
            Paint       = TestStyle.LinePaint(PaintJson),
            Layout      = TestStyle.LineLayout(),
            Filter      = JsonParser.Parse(FilterJson),
        };

        private static Line.PaintProperties  Paint()  => StyleLayerWithFilter().Paint;
        private static Line.LayoutProperties Layout() => StyleLayerWithFilter().Layout;

        private static IFeature LineFeature(string cls, params IReadOnlyList<double2>[] rings)
            => Feature(cls, TileGeometryType.LineString, rings);

        private static IFeature PolygonFeature(string cls, params IReadOnlyList<double2>[] rings)
            => Feature(cls, TileGeometryType.Polygon, rings);

        private static IFeature Feature(string cls, TileGeometryType kind, IReadOnlyList<double2>[] rings)
            => new DictionaryFeature(
                properties: new Dictionary<string, Value> { ["cls"] = Value.String(cls) },
                geometryType: kind,
                geometry: MvtCommandStream.Feature(rings));

        // ── Readers ────────────────────────────────────────────────────────────────────────────────

        private static int[] Ordinals(IReadOnlyList<SelectedTileFeature> selection)
        {
            var ordinals = new int[selection.Count];
            for (int i = 0; i < selection.Count; i++) ordinals[i] = selection[i].Ordinal;
            return ordinals;
        }

        /// <summary>The vertex stream's render-space <c>z</c>, collapsed to one entry per consecutive run.
        /// Every fixture ring is horizontal in tile space, so one run == one emitted ring, in emission
        /// order.</summary>
        private static List<float> RingZSequence(Mesh mesh)
        {
            var runs = new List<float>();
            if (mesh == null) return runs;
            Vector3[] positions = mesh.vertices;
            for (int i = 0; i < positions.Length; i++)
                if (i == 0 || positions[i].z != positions[i - 1].z)
                    runs.Add(positions[i].z);
            return runs;
        }

        private static int DistinctCount(Color[] colors)
        {
            var seen = new HashSet<Color>();
            foreach (Color c in colors) seen.Add(c);
            return seen.Count;
        }

        private static int DistinctWidths(List<Vector4> widths)
        {
            var seen = new HashSet<float>();
            foreach (Vector4 w in widths) seen.Add(w.x);
            return seen.Count;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MeshDataThreadWriteSpikeTests — off-thread writing works; allocate/apply stay main-thread only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MeshDataThreadWriteSpikeTests : BaseTestFixture
    {
        [Test]
        public void WriteMeshData_FromRunOnThreadPoolWorker_RoundTrips()
        {
            var mda = Mesh.AllocateWritableMeshData(1);   // MAIN THREAD (kick-time allocation)
            Exception workerEx = null;
            bool wrote = false;

            var task = UniTask.RunOnThreadPool(() =>
            {
                try
                {
                    Mesh.MeshData md = mda[0];
                    md.SetVertexBufferParams(3,
                        new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0));

                    var pos = md.GetVertexData<Vector3>(0);   // AtomicSafetyHandle must permit off-job write
                    pos[0] = new Vector3(1f, 2f, 3f);
                    pos[1] = new Vector3(4f, 5f, 6f);
                    pos[2] = new Vector3(7f, 8f, 9f);

                    md.SetIndexBufferParams(3, IndexFormat.UInt16);
                    var idx = md.GetIndexData<ushort>();
                    idx[0] = 0; idx[1] = 1; idx[2] = 2;

                    md.subMeshCount = 1;
                    md.SetSubMesh(0, new SubMeshDescriptor(0, 3, MeshTopology.Triangles),
                        MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds);
                    wrote = true;
                }
                catch (Exception e) { workerEx = e; }
            }, configureAwait: false).Preserve();

            task.WaitOffPlayerLoop(10000);

            if (workerEx != null)
            {
                mda.Dispose(); // never applied — dispose to avoid a native leak
                Assert.Fail("Writing MeshData from a RunOnThreadPool worker THREW — the off-thread write " +
                            $"is broken (Unity upgrade?). Exception: {workerEx}");
                return;
            }
            Assert.IsTrue(wrote, "worker did not complete the write");

            var mesh = Track(new Mesh());
            Mesh.ApplyAndDisposeWritableMeshData(mda, mesh);   // MAIN THREAD (apply-at-consume; disposes mda)
            Assert.AreEqual(3, mesh.vertexCount, "vertex count must round-trip from the worker-written MeshData.");
            Vector3[] verts = mesh.vertices;
            Assert.AreEqual(new Vector3(4f, 5f, 6f), verts[1],
                "a known worker-written vertex value must round-trip through ApplyAndDisposeWritableMeshData.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // RibbonJobGeometryTests — first-principles geometry teeth for RibbonJob
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// First-principles geometry teeth for <see cref="RibbonJob"/> over a flat centerline
    /// (<see cref="FlatRibbon"/>): unit normals, exact per-join/per-cap vertex counts, the
    /// miter→bevel/round cascade, the round-cap pivot, winding, and the fold onset at the documented
    /// s_crit. Re-homed off the retired managed line tessellator. Every expectation is analytic — derived
    /// from the join/cap geometry, not read off any producer — and re-derived against the Burst arm's
    /// own formulation, where the extrusion direction is normalize(cross(along, up)) rather than a 2D
    /// left normal. The two agree exactly for a flat centerline with up = +Y, which is why the constants
    /// carry across unchanged.
    /// </summary>
    [TestFixture]
    public class RibbonJobGeometryTests
    {
        // ─── Helpers ───────────────────────────────────────────────────────────────────────

        private static double2 Pt(double x, double y) => new double2(x, y);

        private static double VecLen(double2 v) => math.sqrt(v.x * v.x + v.y * v.y);

        private static void AssertNearlyEqual(double expected, double actual, double tol, string msg)
            => Assert.That(actual, Is.InRange(expected - tol, expected + tol), msg);

        private static (LineRibbonVertex[] Vertices, int[] Indices) Build(
            double2[] pts, JoinType join, CapType cap, double miterLimit = 2.0, int roundSegments = 4,
            double roundLimit = 1.05)
            => FlatRibbon.Build(pts, join, cap, miterLimit, roundSegments, roundLimit);

        /// <summary>Flat 2D projection of a vertex's centerline position (Position.x, Position.z).</summary>
        private static double2 Pos2(LineRibbonVertex v) => new double2(v.Position.x, v.Position.z);

        /// <summary>Flat 2D projection of a vertex's extrusion direction (Across.x, Across.z).</summary>
        private static double2 Across2(LineRibbonVertex v) => new double2(v.Across.x, v.Across.z);

        // ─── Degenerate input ──────────────────────────────────────────────────────────────
        //
        // NullLine_ReturnsEmptyResult (the retired managed tessellator's null-argument overload) is
        // inexpressible against RibbonJob's NativeArray + PointCount signature — there is no "null"
        // NativeArray. SinglePoint_ReturnsEmptyResult (PointCount = 1) is a literal duplicate of the
        // surviving LineRibbonJobTests.LessThanTwoPoints_ProducesZeroOutput, so it is not re-ported here.
        // EmptyList_ReturnsEmptyResult (PointCount = 0) IS expressible — RibbonJob.Execute's
        // `if (PointCount < 2) return;` covers it identically — and FlatRibbon.Build already handles a
        // zero-length input, so it is ported below rather than assumed covered.

        [Test]
        public void EmptyList_ReturnsEmptyResult()
        {
            var (v, i) = Build(new double2[0], JoinType.Miter, CapType.Butt);
            Assert.AreEqual(0, v.Length, "Empty input → 0 verts.");
            Assert.AreEqual(0, i.Length, "Empty input → 0 indices.");
        }

        [Test]
        public void AllDuplicatePoints_ReturnsEmpty()
        {
            var (v, i) = Build(new[] { Pt(5, 3), Pt(5, 3), Pt(5, 3) }, JoinType.Miter, CapType.Butt);
            Assert.AreEqual(0, v.Length, "All-duplicate line → 0 verts.");
            Assert.AreEqual(0, i.Length, "All-duplicate line → 0 indices.");
        }

        [Test]
        public void DuplicatePoints_OnlyTwoDistinct_ProducesSegment()
        {
            // Two actual points and one duplicate of the first — should deduplicate to 2 points.
            var (v, i) = Build(new[] { Pt(0, 0), Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            Assert.AreEqual(4, v.Length, $"One segment after dedup → 4 verts. Got {v.Length}.");
            Assert.AreEqual(6, i.Length, $"One segment after dedup → 6 indices. Got {i.Length}.");
        }

        // ─── Straight 2-point segment (Butt caps, Miter join) ─────────────────────────────

        [Test]
        public void TwoPoints_ButtCap_Miter_ExactlyFourVertsAndTwoTriangles()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            Assert.AreEqual(4, v.Length, $"2-pt butt segment → exactly 4 vertices. Got {v.Length}.");
            Assert.AreEqual(6, i.Length, $"2-pt butt segment → exactly 6 indices (2 triangles). Got {i.Length}.");
        }

        [Test]
        public void TwoPoints_ButtCap_NormalsAreUnitLength()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            foreach (var vert in v)
            {
                double len = VecLen(Across2(vert));
                AssertNearlyEqual(1.0, len, 1e-9,
                    $"Butt segment: every Across must be unit length. Got length={len:G10}.");
            }
        }

        [Test]
        public void TwoPoints_ButtCap_NormalsPerpendicularToSegment()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            double2 tangent = new double2(1, 0);
            foreach (var vert in v)
            {
                double2 across = Across2(vert);
                double dot = across.x * tangent.x + across.y * tangent.y;
                AssertNearlyEqual(0.0, dot, 1e-9,
                    $"Butt segment: Across must be perpendicular to tangent. dot={dot:G10}.");
            }
        }

        [Test]
        public void TwoPoints_ButtCap_NormalsAreOppositeOnLeftAndRight()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            double2 n0 = Across2(v[0]);
            double2 n1 = Across2(v[1]);
            AssertNearlyEqual(0.0, n0.x + n1.x, 1e-9, "Left and right Across must be x-opposites.");
            AssertNearlyEqual(0.0, n0.y + n1.y, 1e-9, "Left and right Across must be y-opposites.");
        }

        [Test]
        public void TwoPoints_ButtCap_PositionsMatchEndpoints()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            for (int i = 0; i < 2; i++)
            {
                double2 p = Pos2(v[i]);
                Assert.That(p.x == 0.0 && p.y == 0.0, Is.True,
                    $"Vertex[{i}] should be at (0,0), got ({p.x},{p.y}).");
            }
            for (int i = 2; i < 4; i++)
            {
                double2 p = Pos2(v[i]);
                Assert.That(p.x == 10.0 && p.y == 0.0, Is.True,
                    $"Vertex[{i}] should be at (10,0), got ({p.x},{p.y}).");
            }
        }

        [Test]
        public void TwoPoints_ButtCap_SideValues_AreCorrect()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            Assert.AreEqual(+1f, v[0].Side, $"Vertex[0] side={v[0].Side}; expected +1.");
            Assert.AreEqual(-1f, v[1].Side, $"Vertex[1] side={v[1].Side}; expected -1.");
            Assert.AreEqual(+1f, v[2].Side, $"Vertex[2] side={v[2].Side}; expected +1.");
            Assert.AreEqual(-1f, v[3].Side, $"Vertex[3] side={v[3].Side}; expected -1.");
        }

        [Test]
        public void AllVertices_WidthScaleIsOne()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(20, 10) }, JoinType.Miter, CapType.Butt);

            foreach (var vert in v)
                Assert.AreEqual(1.0f, vert.WidthScale, $"WidthScale must default to 1. Got {vert.WidthScale}.");
        }

        [Test]
        public void AllVertices_SideIsOneOrMinusOne()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(20, 10) }, JoinType.Miter, CapType.Butt);

            foreach (var vert in v)
                Assert.That(math.abs(math.abs(vert.Side) - 1f), Is.LessThan(1e-6f),
                    $"Side must be +1 or -1 for every vertex. Got {vert.Side}.");
        }

        // ─── DistanceAlong ─────────────────────────────────────────────────────────────────

        [Test]
        public void DistanceAlong_SingleSegment_IsCorrect()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Butt);

            AssertNearlyEqual(0.0, v[0].DistanceAlong, 1e-9, "Start verts dist=0.");
            AssertNearlyEqual(0.0, v[1].DistanceAlong, 1e-9, "Start verts dist=0.");
            AssertNearlyEqual(10.0, v[2].DistanceAlong, 1e-9, "End verts dist=10.");
            AssertNearlyEqual(10.0, v[3].DistanceAlong, 1e-9, "End verts dist=10.");
        }

        [Test]
        public void DistanceAlong_MultiSegment_MiterJoin_IsNonDecreasing()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Miter, CapType.Butt);

            for (int i = 1; i < v.Length; i++)
                Assert.That(v[i].DistanceAlong, Is.GreaterThanOrEqualTo(v[i - 1].DistanceAlong - 1e-9),
                    $"DistanceAlong must be non-decreasing. Vertex[{i}]={v[i].DistanceAlong:G10} " +
                    $"< Vertex[{i - 1}]={v[i - 1].DistanceAlong:G10}.");
        }

        [Test]
        public void DistanceAlong_FinalVertex_EqualsPolylineLength()
        {
            // (0,0)→(3,0)→(3,4) = 3+4 = 7.
            var (v, _) = Build(new[] { Pt(0, 0), Pt(3, 0), Pt(3, 4) }, JoinType.Miter, CapType.Butt);

            double maxDist = 0;
            foreach (var vert in v)
                if (vert.DistanceAlong > maxDist) maxDist = vert.DistanceAlong;

            AssertNearlyEqual(7.0, maxDist, 1e-9, $"Max DistanceAlong must equal the polyline length. Got {maxDist:G10}.");
        }

        // ─── 90° Miter join ────────────────────────────────────────────────────────────────

        [Test]
        public void RightAngle_MiterJoin_ExactVertexCount()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Miter, CapType.Butt,
                miterLimit: 10.0);

            Assert.AreEqual(6, v.Length, $"3-pt miter join → 6 vertices. Got {v.Length}.");
            Assert.AreEqual(12, i.Length, $"3-pt miter join → 12 indices. Got {i.Length}.");
        }

        [Test]
        public void RightAngle_MiterJoin_NormalLengthIsSqrt2()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Miter, CapType.Butt,
                miterLimit: 10.0);

            double miterExpected = math.sqrt(2.0);
            for (int i = 2; i <= 3; i++)
            {
                double len = VecLen(Across2(v[i]));
                AssertNearlyEqual(miterExpected, len, 1e-9,
                    $"90° miter join: vertex[{i}] |Across| should be √2. Got {len:G10}.");
            }
        }

        [Test]
        public void RightAngle_MiterJoin_SegmentNormalsAreUnit()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Miter, CapType.Butt,
                miterLimit: 10.0);

            int[] segVertIdx = { 0, 1, 4, 5 };
            foreach (int i in segVertIdx)
            {
                double len = VecLen(Across2(v[i]));
                AssertNearlyEqual(1.0, len, 1e-9, $"Vertex[{i}] (segment Across) must be unit length. Got {len:G10}.");
            }
        }

        // ─── Miter fallback to bevel ───────────────────────────────────────────────────────

        [Test]
        public void SharpAngle_MiterFallback_NoBevelVertexExceedsMiterLimit()
        {
            double rad = math.PI_DBL * 170.0 / 180.0; // 170° external angle
            double x2 = math.cos(rad) * 10.0;
            double y2 = math.sin(rad) * 10.0;
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(10 + x2, y2) };

            var (v, _) = Build(pts, JoinType.Miter, CapType.Butt, miterLimit: 2.0);

            double hard = 2.0 * math.sqrt(2.0);
            foreach (var vert in v)
            {
                double len = VecLen(Across2(vert));
                Assert.That(len, Is.LessThan(hard + 1e-6),
                    $"After miter→bevel fallback: no Across should exceed miterLimit×√2. Got |Across|={len:G10}.");
            }
        }

        [Test]
        public void SharpAngle_ExplicitBevel_VertexCountExactlyOneBevelExtra()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Bevel, CapType.Butt);

            Assert.AreEqual(7, v.Length, $"3-pt bevel → 7 verts. Got {v.Length}.");
            Assert.AreEqual(15, i.Length, $"3-pt bevel → 15 indices. Got {i.Length}.");
        }

        // ─── Round join ────────────────────────────────────────────────────────────────────

        [Test]
        public void RightAngle_RoundJoin_ExactVertexCount()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Round, CapType.Butt,
                roundSegments: 4);

            Assert.AreEqual(11, v.Length, $"3-pt round join (roundSegments=4) → 11 verts. Got {v.Length}.");
            Assert.AreEqual(27, i.Length, $"3-pt round join (roundSegments=4) → 27 indices. Got {i.Length}.");
        }

        // ─── Inner-join miter clamp (bevel/round) ─────────────────────────────────────────
        //
        // InnerJoin_Bevel_90LeftTurn_Unclamped (the retired managed suite's left-turn/unclamped case) is
        // not re-ported here: LineRibbonJobTests.InnerJoin_Bevel_90LeftTurn_Across_MatchesAnalyticIntersection
        // already asserts exactly this property directly against RibbonJob and survives the managed
        // arm's retirement — porting it again would duplicate, not extend, coverage.

        /// <summary>Locates the join's inner (concave) vertex: the unique vertex at <paramref name="corner"/>
        /// whose Side sign matches the turn side (left turn ⇒ +1, right turn ⇒ −1).</summary>
        private static int FindInnerVertexIndex(LineRibbonVertex[] verts, double2 corner, bool leftTurn)
        {
            float expectedSide = leftTurn ? +1f : -1f;
            int found = -1, count = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                double2 pos = Pos2(verts[i]);
                if (math.abs(pos.x - corner.x) < 1e-9 && math.abs(pos.y - corner.y) < 1e-9 &&
                    verts[i].Side == expectedSide)
                {
                    found = i;
                    count++;
                }
            }
            Assert.AreEqual(1, count,
                $"Expected exactly one inner-join vertex at corner ({corner.x:G},{corner.y:G}) with " +
                $"Side={expectedSide}. Found {count}.");
            return found;
        }

        [Test]
        public void InnerJoin_Round_90RightTurn_Unclamped_MirrorSignBranch()
        {
            // Mirror of the bevel left-turn case (LineRibbonJobTests): round join, 90° RIGHT turn,
            // miterLimit 2.0. Concave (right) inner vertex carries -mu·factor: (-1,-1).
            var corner = Pt(10, 0);
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, -10) }, JoinType.Round, CapType.Butt,
                miterLimit: 2.0, roundSegments: 4);

            int idx = FindInnerVertexIndex(v, corner, leftTurn: false);
            double2 across = Across2(v[idx]);

            AssertNearlyEqual(-1.0, across.x, 1e-9, $"Inner Across.x should be -1.0. Got {across.x:G17}.");
            AssertNearlyEqual(-1.0, across.y, 1e-9, $"Inner Across.y should be -1.0. Got {across.y:G17}.");

            double len = VecLen(across);
            AssertNearlyEqual(1.4142135623730951, len, 1e-9, $"Inner |Across| should be √2. Got {len:G17}.");
        }

        [Test]
        public void InnerJoin_Bevel_150LeftTurn_ClampedBranch()
        {
            // 150° left turn, miterLimit 2.0. Raw factor 1/cos75° ≈ 3.8637 > 2 → clamped to exactly 2.0.
            var corner = Pt(10, 0);
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(-15.980762113533157, 15.0) }, JoinType.Bevel,
                CapType.Butt, miterLimit: 2.0);

            int idx = FindInnerVertexIndex(v, corner, leftTurn: true);
            double2 across = Across2(v[idx]);

            AssertNearlyEqual(-1.9318516525781366, across.x, 1e-9, $"Inner Across.x. Got {across.x:G17}.");
            AssertNearlyEqual(0.5176380902050415, across.y, 1e-9, $"Inner Across.y. Got {across.y:G17}.");

            double len = VecLen(across);
            AssertNearlyEqual(2.0, len, 1e-9, $"Inner |Across| should be clamped to exactly 2.0. Got {len:G17}.");
        }

        [Test]
        public void InnerJoin_Round_150LeftTurn_ClampedBranch()
        {
            // Same fixture as the bevel clamped tooth — covers the round arm of the clamped branch.
            var corner = Pt(10, 0);
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(-15.980762113533157, 15.0) }, JoinType.Round,
                CapType.Butt, miterLimit: 2.0, roundSegments: 4);

            int idx = FindInnerVertexIndex(v, corner, leftTurn: true);
            double2 across = Across2(v[idx]);

            AssertNearlyEqual(-1.9318516525781366, across.x, 1e-9, $"Inner Across.x. Got {across.x:G17}.");
            AssertNearlyEqual(0.5176380902050415, across.y, 1e-9, $"Inner Across.y. Got {across.y:G17}.");

            double len = VecLen(across);
            AssertNearlyEqual(2.0, len, 1e-9, $"Inner |Across| should be clamped to exactly 2.0. Got {len:G17}.");
        }

        [Test]
        public void MiterJoin_RightAngle_AcrossIsExactlyUnitBisector()
        {
            // The tight tooth: a miter at a 90° corner with miterLimit 10 must land on exactly ±1.
            // 1e-12, not the 1e-9 the clamp teeth use — this fixture is axis-aligned, so the bisector
            // normalize operates on an exact unit vector and leaves no room for reordering slack.
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) }, JoinType.Miter, CapType.Butt,
                miterLimit: 10.0);

            double2 left = Across2(v[2]);
            double2 right = Across2(v[3]);
            AssertNearlyEqual(-1.0, left.x, 1e-12, $"Miter left.x should be -1.0. Got {left.x:G17}.");
            AssertNearlyEqual(1.0, left.y, 1e-12, $"Miter left.y should be 1.0. Got {left.y:G17}.");
            AssertNearlyEqual(1.0, right.x, 1e-12, $"Miter right.x should be 1.0. Got {right.x:G17}.");
            AssertNearlyEqual(-1.0, right.y, 1e-12, $"Miter right.y should be -1.0. Got {right.y:G17}.");
        }

        // ─── Join side — chamfer/arc on the CONVEX side (region-membership teeth) ────────────

        private const double HalfWidth = 2.0;

        /// <summary>Extrude a vertex's flat position by its Across × HalfWidth (matches the vertex shader).</summary>
        private static double2 Extrude(LineRibbonVertex v)
        {
            double2 pos = Pos2(v);
            double2 across = Across2(v);
            return new double2(pos.x + across.x * HalfWidth, pos.y + across.y * HalfWidth);
        }

        private static bool InClosed(double2 q, double2 rMin, double2 rMax, double eps)
            => q.x >= rMin.x - eps && q.x <= rMax.x + eps &&
               q.y >= rMin.y - eps && q.y <= rMax.y + eps;

        private static List<int> CornerVerticesBySide(LineRibbonVertex[] verts, double2 corner, float side)
        {
            var found = new List<int>();
            for (int i = 0; i < verts.Length; i++)
            {
                double2 pos = Pos2(verts[i]);
                if (math.abs(pos.x - corner.x) < 1e-9 && math.abs(pos.y - corner.y) < 1e-9 && verts[i].Side == side)
                    found.Add(i);
            }
            return found;
        }

        [Test]
        public void JoinSide_BevelAndRound_ChamferIsOnTheConvexSide()
        {
            var r1Min = Pt(0, -2); var r1Max = Pt(10, 2);
            var r2Min = Pt(8, 0); var r2Max = Pt(12, 10);
            var corner = Pt(10, 0);
            var mu = new double2(-0.7071067811865476, 0.7071067811865476);
            const double eps = 1e-9;

            var fixtureA = new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) };

            // T1a — the concave vertex must sit INSIDE both bands, on the mu side.
            var (bevelAV, _) = Build(fixtureA, JoinType.Bevel, CapType.Butt, miterLimit: 2.0);
            int concaveIdx = FindInnerVertexIndex(bevelAV, corner, leftTurn: true);
            double2 concaveExtruded = Extrude(bevelAV[concaveIdx]);
            double dotMu = (concaveExtruded.x - corner.x) * mu.x + (concaveExtruded.y - corner.y) * mu.y;

            AssertNearlyEqual(8.0, concaveExtruded.x, eps, $"T1a: concave vertex.x should be 8.0. Got {concaveExtruded.x:G17}.");
            AssertNearlyEqual(2.0, concaveExtruded.y, eps, $"T1a: concave vertex.y should be 2.0. Got {concaveExtruded.y:G17}.");
            AssertNearlyEqual(2.8284271247461903, dotMu, eps, $"T1a: dot(Extrude-C, mu) should be 2.8284271247461903. Got {dotMu:G17}.");
            Assert.Greater(dotMu, 0.0, "T1a: concave vertex must be on the mu (turn) side.");
            Assert.IsTrue(InClosed(concaveExtruded, r1Min, r1Max, eps), "T1a: concave vertex must lie inside R1 (band overlap).");
            Assert.IsTrue(InClosed(concaveExtruded, r2Min, r2Max, eps), "T1a: concave vertex must lie inside R2 (band overlap).");

            // T1b — bevel chamfer chord: the two convex vertices' midpoint must sit strictly OUTSIDE both bands.
            var outerVerts = CornerVerticesBySide(bevelAV, corner, -1f);
            Assert.AreEqual(2, outerVerts.Count, $"T1b: expected exactly 2 convex (side=-1) bevel vertices. Found {outerVerts.Count}.");
            double2 outerA = Extrude(bevelAV[outerVerts[0]]);
            double2 outerB = Extrude(bevelAV[outerVerts[1]]);
            double2 chordMid = new double2((outerA.x + outerB.x) / 2.0, (outerA.y + outerB.y) / 2.0);

            AssertNearlyEqual(10.0, outerA.x, eps, $"T1b: outerA.x should be 10.0. Got {outerA.x:G17}.");
            AssertNearlyEqual(-2.0, outerA.y, eps, $"T1b: outerA.y should be -2.0. Got {outerA.y:G17}.");
            AssertNearlyEqual(12.0, outerB.x, eps, $"T1b: outerB.x should be 12.0. Got {outerB.x:G17}.");
            AssertNearlyEqual(0.0, outerB.y, eps, $"T1b: outerB.y should be 0.0. Got {outerB.y:G17}.");
            AssertNearlyEqual(11.0, chordMid.x, eps, $"T1b: chord midpoint.x should be 11.0. Got {chordMid.x:G17}.");
            AssertNearlyEqual(-1.0, chordMid.y, eps, $"T1b: chord midpoint.y should be -1.0. Got {chordMid.y:G17}.");
            Assert.IsFalse(InClosed(chordMid, r1Min, r1Max, -eps), "T1b: chamfer chord midpoint must lie strictly outside R1.");
            Assert.IsFalse(InClosed(chordMid, r2Min, r2Max, -eps), "T1b: chamfer chord midpoint must lie strictly outside R2.");

            // T1c — round arc: all 4 arc-intermediate vertices must be strictly outside both bands.
            var (roundAV, _) = Build(fixtureA, JoinType.Round, CapType.Butt, roundSegments: 4, miterLimit: 2.0);
            var arcSideVerts = CornerVerticesBySide(roundAV, corner, -1f);
            Assert.AreEqual(6, arcSideVerts.Count, $"T1c: expected 6 convex-side round-join vertices. Found {arcSideVerts.Count}.");

            double[] expectedX = { 10.6180339887, 11.1755705046, 11.6180339887, 11.9021130326 };
            double[] expectedY = { -1.9021130326, -1.6180339887, -1.1755705046, -0.6180339887 };
            double minMargin = double.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                double2 fanPos = Extrude(roundAV[arcSideVerts[k + 1]]);
                AssertNearlyEqual(expectedX[k], fanPos.x, 1e-6, $"T1c: arc intermediate[{k}].x. Got {fanPos.x:G17}.");
                AssertNearlyEqual(expectedY[k], fanPos.y, 1e-6, $"T1c: arc intermediate[{k}].y. Got {fanPos.y:G17}.");
                Assert.IsFalse(InClosed(fanPos, r1Min, r1Max, -eps), $"T1c: arc intermediate[{k}] must lie strictly outside R1.");
                Assert.IsFalse(InClosed(fanPos, r2Min, r2Max, -eps), $"T1c: arc intermediate[{k}] must lie strictly outside R2.");

                double marginR1 = fanPos.x - 10.0;
                double marginR2 = -fanPos.y;
                double margin = math.min(marginR1, marginR2);
                if (margin < minMargin) minMargin = margin;
            }
            Assert.Greater(minMargin, 0.6, $"T1c: minimum distance from R1∪R2 should be ~0.618. Got {minMargin:G17}.");

            // T1d — right-turn mirror (Fixture B).
            var fixtureB = new[] { Pt(0, 0), Pt(10, 0), Pt(10, -10) };
            var (bevelBV, _) = Build(fixtureB, JoinType.Bevel, CapType.Butt, miterLimit: 2.0);
            int concaveIdxB = FindInnerVertexIndex(bevelBV, corner, leftTurn: false);
            double2 concaveExtrudedB = Extrude(bevelBV[concaveIdxB]);
            var r2MinB = Pt(8, -10); var r2MaxB = Pt(12, 0);

            AssertNearlyEqual(8.0, concaveExtrudedB.x, eps, $"T1d: concave vertex.x should be 8.0. Got {concaveExtrudedB.x:G17}.");
            AssertNearlyEqual(-2.0, concaveExtrudedB.y, eps, $"T1d: concave vertex.y should be -2.0. Got {concaveExtrudedB.y:G17}.");
            Assert.IsTrue(InClosed(concaveExtrudedB, r1Min, r1Max, eps), "T1d: concave vertex must lie inside R1.");
            Assert.IsTrue(InClosed(concaveExtrudedB, r2MinB, r2MaxB, eps), "T1d: concave vertex must lie inside R2 (mirrored band).");

            var outerVertsB = CornerVerticesBySide(bevelBV, corner, +1f);
            Assert.AreEqual(2, outerVertsB.Count, $"T1d: expected exactly 2 convex (side=+1) bevel vertices. Found {outerVertsB.Count}.");
            double2 outerAB = Extrude(bevelBV[outerVertsB[0]]);
            double2 outerBB = Extrude(bevelBV[outerVertsB[1]]);
            double2 chordMidB = new double2((outerAB.x + outerBB.x) / 2.0, (outerAB.y + outerBB.y) / 2.0);

            AssertNearlyEqual(11.0, chordMidB.x, eps, $"T1d: chord midpoint.x should be 11.0. Got {chordMidB.x:G17}.");
            AssertNearlyEqual(1.0, chordMidB.y, eps, $"T1d: chord midpoint.y should be 1.0. Got {chordMidB.y:G17}.");
            Assert.IsFalse(InClosed(chordMidB, r1Min, r1Max, -eps), "T1d: chamfer chord midpoint must lie strictly outside R1.");
            Assert.IsFalse(InClosed(chordMidB, r2MinB, r2MaxB, -eps), "T1d: chamfer chord midpoint must lie strictly outside R2 (mirrored band).");
        }

        [Test]
        public void JoinVertices_NormalTimesSide_IsUnchanged()
        {
            // Normal (Across) negation and Side flip must happen TOGETHER, so Across×Side is unchanged
            // vertex-for-vertex at every join vertex.
            var corner = Pt(10, 0);
            var fixtureA = new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) };
            var (v, _) = Build(fixtureA, JoinType.Bevel, CapType.Butt, miterLimit: 2.0);

            var cornerVerts = new List<int>();
            for (int i = 0; i < v.Length; i++)
            {
                double2 pos = Pos2(v[i]);
                if (math.abs(pos.x - corner.x) < 1e-9 && math.abs(pos.y - corner.y) < 1e-9)
                    cornerVerts.Add(i);
            }
            Assert.AreEqual(3, cornerVerts.Count, $"Expected exactly 3 bevel join vertices at the corner. Found {cornerVerts.Count}.");

            double2[] expected = { new double2(0, 1), new double2(-1, 1), new double2(-1, 0) };
            for (int k = 0; k < 3; k++)
            {
                var vert = v[cornerVerts[k]];
                double2 across = Across2(vert);
                double2 normalTimesSide = new double2(across.x * vert.Side, across.y * vert.Side);
                AssertNearlyEqual(expected[k].x, normalTimesSide.x, 1e-9,
                    $"Across×Side[{k}].x should be {expected[k].x:G17}. Got {normalTimesSide.x:G17}.");
                AssertNearlyEqual(expected[k].y, normalTimesSide.y, 1e-9,
                    $"Across×Side[{k}].y should be {expected[k].y:G17}. Got {normalTimesSide.y:G17}.");
            }

            AssertNormalMuSideCorrelation(fixtureA, JoinType.Bevel, corner);
            AssertNormalMuSideCorrelation(fixtureA, JoinType.Round, corner);
        }

        private static void AssertNormalMuSideCorrelation(double2[] pts, JoinType joinType, double2 corner)
        {
            var (v, _) = Build(pts, joinType, CapType.Butt, roundSegments: 4, miterLimit: 2.0);
            var mu = new double2(-0.7071067811865476, 0.7071067811865476);

            int asserted = 0, ambiguous = 0;
            for (int i = 0; i < v.Length; i++)
            {
                double2 pos = Pos2(v[i]);
                if (math.abs(pos.x - corner.x) > 1e-9 || math.abs(pos.y - corner.y) > 1e-9) continue;
                if (v[i].Side == 0f) continue; // fan pivot, not a corner-offset vertex.

                double2 across = Across2(v[i]);
                double dot = across.x * mu.x + across.y * mu.y;
                bool sidePositive = v[i].Side > 0f;
                if (math.abs(dot) < 1e-6) { ambiguous++; continue; }
                Assert.AreEqual(dot > 0.0, sidePositive,
                    $"{joinType} vertex[{i}]: dot(Across,mu)={dot:G17} but Side={v[i].Side} — sign mismatch.");
                asserted++;
            }

            Assert.AreEqual(0, ambiguous,
                $"{joinType}: {ambiguous} corner vertices were skipped as sign-ambiguous — re-derive against the new topology.");
            Assert.Greater(asserted, 0, $"{joinType}: no corner vertices were checked — the tooth is vacuous.");
        }

        // ─── Square cap ────────────────────────────────────────────────────────────────────

        [Test]
        public void SingleSegment_SquareCap_ExactVertexCount()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Square);

            Assert.AreEqual(6, v.Length, $"2-pt square caps → 6 verts. Got {v.Length}.");
            Assert.AreEqual(12, i.Length, $"2-pt square caps → 12 indices. Got {i.Length}.");
        }

        // ─── Round cap ─────────────────────────────────────────────────────────────────────

        [Test]
        public void SingleSegment_RoundCap_ExactVertexCount()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Round, roundSegments: 4);

            Assert.AreEqual(16, v.Length, $"2-pt round caps (roundSegments=4) → 16 verts. Got {v.Length}.");
            Assert.AreEqual(36, i.Length, $"2-pt round caps (roundSegments=4) → 36 indices. Got {i.Length}.");
        }

        /// <summary>
        /// Every cap-fan triangle must carry both of its RIM vertices at the same <c>Side</c> SIGN — the
        /// silhouette-seam fix. Pinned directly against <see cref="RibbonJob"/>: the retired managed
        /// tessellator held this guarantee differentially; with the managed arm gone this is the only
        /// place it is checked.
        /// </summary>
        [Test]
        public void RoundCap_FanTriangles_HaveUniformRimSide()
        {
            var (v, i) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Round, roundSegments: 4);

            int fanTrianglesChecked = 0;
            for (int t = 0; t < i.Length / 3; t++)
            {
                LineRibbonVertex a = v[i[t * 3]];
                LineRibbonVertex b = v[i[t * 3 + 1]];
                LineRibbonVertex c = v[i[t * 3 + 2]];

                if (math.abs(a.Side) > 1e-6f) continue;
                fanTrianglesChecked++;

                Assert.AreEqual(math.sign(b.Side), math.sign(c.Side),
                    $"Cap-fan tri[{t}] rim sides are {b.Side} and {c.Side}: its outer edge is a true " +
                    "silhouette but |side| dips to 0 at the midpoint.");
            }

            Assert.AreEqual(10, fanTrianglesChecked,
                $"Both round caps must contribute 5 pivot-rooted fan triangles each. Found {fanTrianglesChecked}.");
        }

        // ─── line-round-limit: shallow round joins collapse to miter ─────────────────────────

        [Test]
        public void ShallowRoundJoin_CollapsesToMiter_MatchesMiterPathExactly()
        {
            // 20° turn: half-angle 10° ⇒ f = 1/cos10° ≈ 1.0154 < roundLimit(1.05) ⇒ must collapse to miter.
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(19.396926207859085, 3.4202014332566878) };

            var (roundV, roundI) = Build(pts, JoinType.Round, CapType.Butt, miterLimit: 10.0, roundSegments: 4,
                roundLimit: 1.05);
            var (miterV, miterI) = Build(pts, JoinType.Miter, CapType.Butt, miterLimit: 10.0);

            Assert.AreEqual(miterV.Length, roundV.Length,
                $"A shallow round join (f≈1.0154 ≤ roundLimit=1.05) must collapse to the miter path's vertex " +
                $"count. miter={miterV.Length}, round={roundV.Length}.");
            Assert.AreEqual(miterI.Length, roundI.Length,
                $"A shallow round join must collapse to the miter path's index count. miter={miterI.Length}, round={roundI.Length}.");

            for (int idx = 0; idx < miterV.Length; idx++)
            {
                double2 m = Across2(miterV[idx]);
                double2 r = Across2(roundV[idx]);
                AssertNearlyEqual(m.x, r.x, 1e-9, $"vertex[{idx}].Across.x: collapsed round must match the miter path.");
                AssertNearlyEqual(m.y, r.y, 1e-9, $"vertex[{idx}].Across.y: collapsed round must match the miter path.");
                Assert.AreEqual(miterV[idx].Side, roundV[idx].Side, $"vertex[{idx}].Side: collapsed round must match the miter path.");
            }
        }

        [Test]
        public void SharpRoundJoin_PreservesFan_RimAtHalfWidthRadius()
        {
            // 90° turn: f = √2 ≈ 1.414 ≫ roundLimit(1.05) ⇒ fan preserved.
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) };
            var corner = Pt(10, 0);
            const double halfWidth = 2.0;

            var (v, _) = Build(pts, JoinType.Round, CapType.Butt, miterLimit: 2.0, roundSegments: 4, roundLimit: 1.05);

            var rim = CornerVerticesBySide(v, corner, -1f);
            Assert.AreEqual(6, rim.Count,
                $"Sharp round join must still emit the 6-vertex convex rim. Found {rim.Count} — if 1, the join collapsed to a miter it should not have.");

            foreach (int idx in rim)
            {
                double2 extruded = Extrude(v[idx]);
                double dist = VecLen(new double2(extruded.x - corner.x, extruded.y - corner.y));
                AssertNearlyEqual(halfWidth, dist, 1e-6,
                    $"Rim vertex[{idx}] is {dist:G17} from the corner; must sit at exactly halfWidth={halfWidth}.");
            }
        }

        [Test]
        public void RoundLimit_ExceedsMiterLimit_CascadesToBevel_NotUnboundedMiter()
        {
            // roundLimit(3.0) and miterLimit(2.0) independently style-settable: a corner with f=2.5 sits
            // between them and must cascade round→miter→bevel, not fall through to an unbounded miter.
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(3.2, 7.332121111929344) };
            var corner = Pt(10, 0);

            var (v, _) = Build(pts, JoinType.Round, CapType.Butt, miterLimit: 2.0, roundSegments: 4, roundLimit: 3.0);

            var cornerVerts = new List<int>();
            for (int i = 0; i < v.Length; i++)
            {
                double2 pos = Pos2(v[i]);
                if (math.abs(pos.x - corner.x) < 1e-9 && math.abs(pos.y - corner.y) < 1e-9)
                    cornerVerts.Add(i);
            }
            Assert.AreEqual(3, cornerVerts.Count,
                $"A round join whose collapsed miter (f=2.5) exceeds miterLimit=2.0 must bevel (3 join vertices). Found {cornerVerts.Count}.");

            double maxMag = 0.0;
            foreach (int idx in cornerVerts)
                maxMag = math.max(maxMag, VecLen(Across2(v[idx])));
            Assert.LessOrEqual(maxMag, 2.0 + 1e-6,
                $"No join-vertex Across may exceed miterLimit=2.0. Got max |Across|={maxMag:G17}.");
            AssertNearlyEqual(2.0, maxMag, 1e-6,
                $"The inner vertex must be clamped to EXACTLY miterLimit=2.0. Got {maxMag:G17}.");
        }

        // ─── NeedsMiter / NeedsBevel (RibbonJob's internal accessors) ─────────────────────────

        [Test]
        public void NeedsMiter_ParallelSegments_ReturnsTrue()
        {
            double3 n = new double3(0, 0, 1);
            Assert.IsTrue(RibbonJob.NeedsMiter(n, n, 1.05),
                "Parallel segments → miter factor = 1 ≤ any roundLimit ≥ 1 → shallow → collapses to miter.");
        }

        [Test]
        public void NeedsMiter_RightAngle_AboveDefaultLimit_ReturnsFalse()
        {
            double3 n1 = new double3(0, 0, 1);
            double3 n2 = new double3(-1, 0, 0);
            Assert.IsFalse(RibbonJob.NeedsMiter(n1, n2, 1.05),
                "90° turn: miter factor √2 > roundLimit 1.05 → not shallow → NeedsMiter should be false.");
        }

        [Test]
        public void NeedsMiter_HairpinWithNegativeHalfAngleCosine_ReturnsFalse()
        {
            double3 n1 = new double3(0, 0, 1);
            double3 n2 = new double3(1e-9, 0, -1.0000000001);
            Assert.IsFalse(RibbonJob.NeedsMiter(n1, n2, 1.05),
                "Hairpin: miter factor magnitude ≫ roundLimit → NeedsMiter should be false.");
        }

        [Test]
        public void NeedsBevel_ParallelSegments_ReturnsFalse()
        {
            double3 n = new double3(0, 0, 1);
            Assert.IsFalse(RibbonJob.NeedsBevel(n, n, 2.0), "Parallel segments → miter factor = 1 → no bevel needed.");
        }

        [Test]
        public void NeedsBevel_RightAngle_BelowDefaultLimit_ReturnsFalse()
        {
            double3 n1 = new double3(0, 0, 1);
            double3 n2 = new double3(-1, 0, 0);
            Assert.IsFalse(RibbonJob.NeedsBevel(n1, n2, 2.0), "90° turn: miter factor √2 < limit 2 → NeedsBevel should be false.");
        }

        [Test]
        public void NeedsBevel_VerySharpAngle_ExceedsLimit_ReturnsTrue()
        {
            double3 n1 = new double3(0, 0, 1);
            double3 n2 = new double3(0.01, 0, -0.9999); // ~179° turn
            Assert.IsTrue(RibbonJob.NeedsBevel(n1, n2, 2.0), "Near-180° turn: miter factor >> 2 → NeedsBevel should be true.");
        }

        /// <summary>Test-local restatement of the miter-factor math, independent of <see cref="RibbonJob"/> —
        /// used only to state this test's OWN precondition (that the fixture drives a negative cos(θ/2)),
        /// not to assert anything about production.</summary>
        private static double MiterFactor(double2 n1, double2 n2)
        {
            double mx = n1.x + n2.x;
            double my = n1.y + n2.y;
            double mLen = math.sqrt(mx * mx + my * my);
            if (mLen < 1e-12) return double.MaxValue;
            double mux = mx / mLen;
            double muy = my / mLen;
            double dot = mux * n1.x + muy * n1.y;
            if (math.abs(dot) < 1e-12) return double.MaxValue;
            return 1.0 / dot;
        }

        [Test]
        public void NeedsBevel_HairpinWithNegativeHalfAngleCosine_StillReturnsTrue()
        {
            double2 n1 = new double2(0, 1);
            double2 n2 = new double2(1e-9, -1.0000000001);

            double signedFactor = MiterFactor(n1, n2);
            Assert.Less(signedFactor, 0.0,
                $"Precondition: this input must drive cos(θ/2) NEGATIVE. Signed miter factor was {signedFactor:G6}.");
            Assert.Greater(math.abs(signedFactor), 2.0,
                $"Precondition: the miter factor's MAGNITUDE must exceed the limit. Got {math.abs(signedFactor):G6}.");

            double3 n1_3 = new double3(n1.x, 0, n1.y);
            double3 n2_3 = new double3(n2.x, 0, n2.y);
            Assert.IsTrue(RibbonJob.NeedsBevel(n1_3, n2_3, 2.0),
                $"Hairpin with a negative cos(θ/2): |1/cos| = {math.abs(signedFactor):G6} exceeds the limit 2, so the join must bevel.");
        }

        // ─── Winding + extent assertions for all JoinType × CapType combinations ──────────

        private static double TriSignedArea(double2 a, double2 b, double2 c)
            => 0.5 * ((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y));

        private static void AssertGeometryValid(
            LineRibbonVertex[] verts, int[] indices, string label,
            bool checkCapExtent = false,
            double2 startPt = default, double2 startTangent = default,
            double2 endPt = default, double2 endTangent = default)
        {
            Assert.Greater(verts.Length, 0, $"{label}: must have vertices.");
            Assert.Greater(indices.Length, 0, $"{label}: must have indices.");
            Assert.AreEqual(0, indices.Length % 3, $"{label}: index count must be multiple of 3.");

            int triCount = indices.Length / 3;
            int posCount = 0, negCount = 0, zeroCount = 0;
            var dupList = new List<string>();

            for (int t = 0; t < triCount; t++)
            {
                int i0 = indices[t * 3];
                int i1 = indices[t * 3 + 1];
                int i2 = indices[t * 3 + 2];

                if (i0 == i1 || i1 == i2 || i0 == i2)
                    dupList.Add($"tri[{t}]({i0},{i1},{i2})");

                double2 a = Extrude(verts[i0]);
                double2 b = Extrude(verts[i1]);
                double2 c = Extrude(verts[i2]);
                double area = TriSignedArea(a, b, c);

                if (area > 1e-9) posCount++;
                else if (area < -1e-9) negCount++;
                else zeroCount++;
            }

            Assert.AreEqual(0, dupList.Count, $"{label}: {dupList.Count} duplicate-index triangle(s): {string.Join(", ", dupList)}.");
            Assert.AreEqual(0, zeroCount, $"{label}: {zeroCount}/{triCount} degenerate (zero-area) triangles after extrusion.");
            Assert.AreEqual(0, negCount,
                $"{label}: {negCount}/{triCount} negative-winding (CW) triangles after extrusion. pos={posCount} neg={negCount} zero={zeroCount}.");

            if (checkCapExtent)
            {
                bool startCapOk = false;
                bool endCapOk = false;
                double2 outwardStart = new double2(-startTangent.x, -startTangent.y);
                double2 outwardEnd = endTangent;

                foreach (var v in verts)
                {
                    double2 ext = Extrude(v);

                    double2 ds = new double2(ext.x - startPt.x, ext.y - startPt.y);
                    if (ds.x * outwardStart.x + ds.y * outwardStart.y > 1e-6) startCapOk = true;

                    double2 de = new double2(ext.x - endPt.x, ext.y - endPt.y);
                    if (de.x * outwardEnd.x + de.y * outwardEnd.y > 1e-6) endCapOk = true;
                }

                Assert.IsTrue(startCapOk, $"{label}: start cap must have at least one vertex extruding beyond p0 in the backward tangent direction.");
                Assert.IsTrue(endCapOk, $"{label}: end cap must have at least one vertex extruding beyond p1 in the forward tangent direction.");
            }
        }

        private static readonly double2 LineStart = new double2(0, 0);
        private static readonly double2 LineEnd = new double2(10, 0);
        private static readonly double2 LineTangent = new double2(1, 0);

        [Test]
        public void AllJoinCapCombinations_PositiveWindingNoDegenerate()
        {
            var joins = new[] { JoinType.Miter, JoinType.Bevel, JoinType.Round };
            var caps = new[] { CapType.Butt, CapType.Square, CapType.Round };
            var hline = new[] { LineStart, LineEnd };

            foreach (var join in joins)
            {
                foreach (var cap in caps)
                {
                    string label = $"hline {join}/{cap}";
                    var (v, i) = Build(hline, join, cap, roundSegments: 4);

                    bool needsExtentCheck = cap == CapType.Round || cap == CapType.Square;
                    AssertGeometryValid(v, i, label, needsExtentCheck,
                        startPt: LineStart, startTangent: LineTangent, endPt: LineEnd, endTangent: LineTangent);
                }
            }
        }

        [Test]
        public void AllJoinCapCombinations_LShape_PositiveWindingNoDegenerate()
        {
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(10, 10) };
            var joins = new[] { JoinType.Miter, JoinType.Bevel, JoinType.Round };
            var caps = new[] { CapType.Butt, CapType.Square, CapType.Round };

            foreach (var join in joins)
            {
                foreach (var cap in caps)
                {
                    string label = $"Lshape {join}/{cap}";
                    var (v, i) = Build(pts, join, cap, roundSegments: 4);

                    bool needsExtentCheck = cap == CapType.Round || cap == CapType.Square;
                    AssertGeometryValid(v, i, label, needsExtentCheck,
                        startPt: Pt(0, 0), startTangent: new double2(1, 0),
                        endPt: Pt(10, 10), endTangent: new double2(0, 1));
                }
            }
        }

        [Test]
        public void AllJoinCapCombinations_RightTurn_PositiveWindingNoDegenerate()
        {
            var pts = new[] { Pt(0, 0), Pt(10, 0), Pt(10, -10) };
            var joins = new[] { JoinType.Miter, JoinType.Bevel, JoinType.Round };
            var caps = new[] { CapType.Butt, CapType.Square, CapType.Round };

            foreach (var join in joins)
            {
                foreach (var cap in caps)
                {
                    string label = $"RightTurn {join}/{cap}";
                    var (v, i) = Build(pts, join, cap, roundSegments: 4);

                    bool needsExtentCheck = cap == CapType.Round || cap == CapType.Square;
                    AssertGeometryValid(v, i, label, needsExtentCheck,
                        startPt: Pt(0, 0), startTangent: new double2(1, 0),
                        endPt: Pt(10, -10), endTangent: new double2(0, -1));
                }
            }
        }

        /// <summary>Doubled signed areas of every triangle, after shader extrusion at <see cref="HalfWidth"/>.</summary>
        private static double[] DoubledSignedAreas(LineRibbonVertex[] verts, int[] indices)
        {
            var areas = new double[indices.Length / 3];
            for (int t = 0; t < areas.Length; t++)
            {
                double2 a = Extrude(verts[indices[t * 3]]);
                double2 b = Extrude(verts[indices[t * 3 + 1]]);
                double2 c = Extrude(verts[indices[t * 3 + 2]]);
                areas[t] = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            }
            return areas;
        }

        [Test]
        [TestCase(JoinType.Miter)]
        [TestCase(JoinType.Bevel)]
        [TestCase(JoinType.Round)]
        public void ShortSegment_InnerJoinFold_OnsetIsExactlyTheDocumentedSCrit(JoinType join)
        {
            var (belowV, belowI) = Build(new[] { Pt(0, 0), Pt(1.8, 0), Pt(1.8, 10) }, join, CapType.Butt,
                roundSegments: 4, miterLimit: 2.0);
            var belowAreas = DoubledSignedAreas(belowV, belowI);
            int belowFolded = 0;
            double mostNegative = 0.0;
            foreach (double area in belowAreas)
                if (area < 0.0) { belowFolded++; if (area < mostNegative) mostNegative = area; }

            Assert.AreEqual(1, belowFolded,
                $"{join}: S = 1.8 < S_crit = 2.0 must fold EXACTLY ONE triangle. Found {belowFolded}.");
            AssertNearlyEqual(-0.8, mostNegative, 1e-9, $"{join}: the folded triangle's doubled signed area should be -0.8. Got {mostNegative:G17}.");

            var (atV, atI) = Build(new[] { Pt(0, 0), Pt(2.0, 0), Pt(2.0, 10) }, join, CapType.Butt,
                roundSegments: 4, miterLimit: 2.0);
            double closestToZero = double.MaxValue;
            foreach (double area in DoubledSignedAreas(atV, atI))
            {
                Assert.GreaterOrEqual(area, -1e-9, $"{join}: nothing may fold AT S_crit. Got {area:G17}.");
                if (math.abs(area) < math.abs(closestToZero)) closestToZero = area;
            }
            AssertNearlyEqual(0.0, closestToZero, 1e-9, $"{join}: at S = S_crit = 2.0 exactly one triangle must be degenerate. Got {closestToZero:G17}.");

            var (aboveV, aboveI) = Build(new[] { Pt(0, 0), Pt(2.2, 0), Pt(2.2, 10) }, join, CapType.Butt,
                roundSegments: 4, miterLimit: 2.0);
            foreach (double area in DoubledSignedAreas(aboveV, aboveI))
                Assert.Greater(area, 0.0, $"{join}: S = 2.2 > S_crit must be uniformly CCW. Got {area:G17}.");
        }

        [Test]
        public void RoundCap_StartAndEnd_BulgeOnCorrectSide()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Round, roundSegments: 4);

            bool startBehind = false;
            bool endForward = false;
            foreach (var vert in v)
            {
                double2 ext = Extrude(vert);
                if (ext.x < -1e-9) startBehind = true;
                if (ext.x > 10.0 + 1e-9) endForward = true;
            }

            Assert.IsTrue(startBehind, "Round start cap must have extruded vertices with x < 0 (behind start point x=0).");
            Assert.IsTrue(endForward, "Round end cap must have extruded vertices with x > 10 (past end point x=10).");
        }

        [Test]
        public void RoundCap_PivotVertex_HasZeroNormal_AndZeroSide()
        {
            var (v, _) = Build(new[] { Pt(0, 0), Pt(10, 0) }, JoinType.Miter, CapType.Round, roundSegments: 4);

            bool foundStartPivot = false;
            bool foundEndPivot = false;

            foreach (var vert in v)
            {
                if (math.abs(vert.Side) < 1e-6f)
                {
                    double2 across = Across2(vert);
                    double normalLen = math.sqrt(across.x * across.x + across.y * across.y);
                    Assert.That(normalLen, Is.LessThan(1e-9),
                        $"Cap pivot: Across must be (0,0). Got |Across|={normalLen:G}.");

                    double2 pos = Pos2(vert);
                    if (math.abs(pos.x) < 1e-9 && math.abs(pos.y) < 1e-9) foundStartPivot = true;
                    else if (math.abs(pos.x - 10.0) < 1e-9 && math.abs(pos.y) < 1e-9) foundEndPivot = true;
                }
            }

            Assert.IsTrue(foundStartPivot, "Round start cap must have a pivot vertex at (0,0) with side≈0 and Across≈(0,0).");
            Assert.IsTrue(foundEndPivot, "Round end cap must have a pivot vertex at (10,0) with side≈0 and Across≈(0,0).");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SourceLayerBufferSharingTests — one materialization per source-layer per tile, shared across cadences
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tooth <b>C</b> — <b>one materialization per source-layer per tile, shared across BOTH
    /// cadences of a kick</b>.
    ///
    /// <para><b>What this is for.</b> A recorded finding: nothing observed the <i>point</i> of
    /// the conversion: changing the symbol processor's store argument to <c>null</c> left all 2286 tests
    /// green while silently reverting one-buffer-per-source-layer, because the fallback minted a private
    /// store and the OUTPUT was byte-identical. The store is gone, so the current shape of that same silent
    /// regression is a <c>Geometry</c> getter that re-materializes per read, or a consumer that mints its
    /// own. Both are output-neutral. This is the tooth that sees them.</para>
    ///
    /// <para><b>Production configuration — the standing check, taken seriously.</b> The fan-in is ≥ 2 on
    /// BOTH sides and it CROSSES the mesh/symbol boundary: <c>countries</c> is named by two fill layers,
    /// <c>centroids</c> by <b>two symbol layers</b> (the half the finding says a mesh-only fixture would
    /// miss), and <c>geolines</c> by exactly one layer so the distinct-buffer count is not trivially
    /// satisfied by "everything is one buffer". Both passes run inside ONE decode scope — the shape
    /// <c>TileManager.KickMeshBuild</c> creates — and the real production processors run alongside the
    /// probes, so the arrangement under test is one that actually happens.</para>
    /// </summary>
    [TestFixture]
    public class SourceLayerBufferSharingTests
    {
        private static readonly TileId Tile = new TileId { Z = 0, X = 0, Y = 0 };
        private static TileLayerProcessContext MakeContext() => new TileLayerProcessContext
        {
            Tile             = Tile,
            Zoom             = 0.0,
            TileOriginRender = double3.zero,
            Projection       = new WebMercatorProjection(),
        };

        /// <summary>Counts how many times the tile's bytes were actually parsed.</summary>
        private sealed class CountingDecoder : ITileDecoder
        {
            private readonly ITileDecoder _inner = new MvtTileDecoder();
            public int DecodeCount { get; private set; }
            public IDecodedTile Decode(TileId id, byte[] bytes)
            {
                DecodeCount++;
                return _inner.Decode(id, bytes);
            }
        }

        /// <summary>Records the buffer a consumer of <paramref name="sourceLayer"/> would borrow, read through
        /// the SAME expression the production consumers use — <c>SourceLayerResolver.ResolveTileLayer(style,
        /// tile).Geometry</c> (<c>TileMeshLayerProcessor</c> and <c>SymbolFeatureExtractor</c> both do exactly
        /// this). Not a transcription of production: what is asserted is buffer IDENTITY across independent
        /// consumers, which a re-materializing getter breaks while leaving every output byte-identical.</summary>
        private sealed class BufferProbeMeshProcessor : ITileMeshLayerProcessor
        {
            private readonly StyleLayer _style;
            private readonly List<(string source, NativeArray<double2> buffer)> _log;
            public BufferProbeMeshProcessor(StyleLayer style, List<(string, NativeArray<double2>)> log)
            { _style = style; _log = log; }

            public LayerPhase Phase => LayerPhase.WorkerOnly;
            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
                => _log.Add((_style.SourceLayer,
                             SourceLayerResolver.ResolveTileLayer(_style, tile)?.Geometry.Vertices ?? default));
            public bool TryTakeGraphRequest(out ILayerMeshBuild build) { build = null; return false; }
            public void Release() { }
        }

        private sealed class BufferProbeSymbolProcessor : ITileWorkerThenMainLayerProcessor
        {
            private readonly StyleLayer _style;
            private readonly List<(string source, NativeArray<double2> buffer)> _log;
            public BufferProbeSymbolProcessor(StyleLayer style, List<(string, NativeArray<double2>)> log)
            { _style = style; _log = log; }

            public LayerPhase Phase => LayerPhase.WorkerThenMain;
            public void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)
                => _log.Add((_style.SourceLayer,
                             SourceLayerResolver.ResolveTileLayer(_style, tile)?.Geometry.Vertices ?? default));
            public void CompleteOnMain(CancellationToken ct) { }
        }

        private static MapRenderer.Core.Style.Fill.StyleLayer Fill(string id, string sourceLayer) => new MapRenderer.Core.Style.Fill.StyleLayer
        {
            Id = id, LayerType = StyleLayerType.Fill, Source = "s", SourceLayer = sourceLayer,
            Paint = TestStyle.FillPaint("{\"fill-color\":\"#ffffff\"}"),
            Layout = TestStyle.FillLayout(),
        };

        private static SymbolStyle.StyleLayer Symbol(string id, string sourceLayer) => new SymbolStyle.StyleLayer
        {
            Id = id, LayerType = StyleLayerType.Symbol, Source = "s", SourceLayer = sourceLayer,
            Paint = TestStyle.SymbolPaint(),
            Layout = TestStyle.SymbolLayout("{\"text-field\":\"{NAME}\"}"),
        };

        [Test]
        public void OneKick_MaterializesEachSourceLayerOnce_SharedAcrossFillAndSymbolConsumers()
        {
            var decoder = new CountingDecoder();
            var handle  = new SharedDisposable<IDecodedTile>(decoder.Decode(Tile, SampleTileFixture.Bytes()));
            var context = MakeContext();

            // Two fill layers over "countries", one over "geolines" (named by EXACTLY ONE style layer, so
            // the distinct-count assertion below is not satisfiable by collapsing everything onto one
            // buffer), and TWO symbol layers over "centroids".
            StyleLayer fillA = Fill("fill-a", "countries");
            StyleLayer fillB = Fill("fill-b", "countries");
            StyleLayer fillC = Fill("fill-c", "geolines");
            SymbolStyle.StyleLayer symbolA = Symbol("sym-a", "centroids");
            SymbolStyle.StyleLayer symbolB = Symbol("sym-b", "centroids");

            var meshLog   = new List<(string source, NativeArray<double2> buffer)>();
            var symbolLog = new List<(string source, NativeArray<double2> buffer)>();

            // A real builder over an empty glyph source: the worker step (ExtractLayers) never touches the
            // glyph cache — it is the main-thread Shape tail that does — so an empty source is enough to
            // run the PRODUCTION symbol processors here, which is the point (the fan-in under test must be
            // one production actually creates).
            using var glyphManager = new GlyphManager(
                TestGlyphSource.FromRanges(new Dictionary<(string fontStack, int rangeStart), byte[]>()), new GlyphAtlas(256, 256));
            var builder = new StyledSymbolTileBuilder(glyphManager);
            // 4.4c: the symbol processors now write their raw symbol slots into a reused SymbolTileBuffer (not a
            // per-symbol managed carrier list). This test only exercises the WORKER pass (ExtractLayers) + buffer-sharing —
            // the scratch is never read here, just handed to the processor ctors.
            var tileBuffer  = new MapRenderer.Core.Text.Placement.SymbolTileBuffer();

            // ONE reference around BOTH passes, released in a finally — TileManager.KickMeshBuild's shape
            // exactly.
            try
            {
                var meshProcessors = new ITileMeshLayerProcessor[]
                {
                    new BufferProbeMeshProcessor(fillA, meshLog),
                    new BufferProbeMeshProcessor(fillB, meshLog),
                    new BufferProbeMeshProcessor(fillC, meshLog),
                };
                TilePrologueOutput meshOutput = TileLayerProcessorRunner.RunWorkerPass(handle, in context, meshProcessors);
                for (int i = 0; i < meshOutput.Layers.Length; i++) meshOutput.Layers[i]?.Dispose();

                var symbolProcessors = new ITileWorkerThenMainLayerProcessor[]
                {
                    new BufferProbeSymbolProcessor(symbolA, symbolLog),
                    new BufferProbeSymbolProcessor(symbolB, symbolLog),
                    // The REAL symbol processors run in the same pass, so the arrangement under test is one
                    // production actually creates — two symbol layers naming one source-layer, extracting.
                    new TileSymbolLayerProcessor(builder, symbolA, 0, tileBuffer),
                    new TileSymbolLayerProcessor(builder, symbolB, 1, tileBuffer),
                };
                TileLayerProcessorRunner.RunSymbolWorkerPass(handle, in context, symbolProcessors);
            }
            finally { handle.Release(); }

            // ── Clause 0: ONE decode for the whole kick. ─────────────────────────────────────────────
            Assert.AreEqual(1, decoder.DecodeCount,
                "the mesh pass and the symbol pass of ONE kick share ONE decode — that is what the single " +
                "kick reference buys, and what a per-PASS store could never give (it materialized a " +
                "shared source-layer once per pass, i.e. twice per kick).");

            // ── Non-vacuity: every probe really saw a live buffer. ───────────────────────────────────
            Assert.AreEqual(3, meshLog.Count,   "precondition: all three mesh probes ran");
            Assert.AreEqual(2, symbolLog.Count, "precondition: both symbol probes ran");
            foreach ((string source, NativeArray<double2> buffer) in meshLog)
                Assert.IsTrue(buffer.IsCreated, $"precondition: '{source}' must really carry geometry");
            foreach ((string source, NativeArray<double2> buffer) in symbolLog)
                Assert.IsTrue(buffer.IsCreated, $"precondition: '{source}' must really carry geometry");

            // ── Clause i: the fan-in shares, WITHIN and ACROSS the cadences. ─────────────────────────
            // NativeArray<T>.Equals compares backing pointer + length, so these are IDENTITY assertions:
            // a getter that re-materialized would hand back equal CONTENT at a different pointer.
            Assert.IsTrue(meshLog[0].buffer.Equals(meshLog[1].buffer),
                "two fill layers naming 'countries' must borrow the SAME buffer");
            Assert.IsTrue(symbolLog[0].buffer.Equals(symbolLog[1].buffer),
                "two SYMBOL layers naming 'centroids' must borrow the SAME buffer — the half a recorded " +
                "finding says a mesh-only fixture cannot see");

            // Cross-cadence sharing is clause 0 + clause ii together: ONE decode served both passes, and
            // one decode holds exactly one buffer per layer — so a mesh consumer and a symbol consumer of
            // the same source-layer necessarily hold the same allocation. (There is no scope to re-open and
            // no re-decode to provoke any more — both are retired. The tile is decoded ONCE, before any lease
            // exists, and every reference reads that same instance; the sharing is a property of the
            // decoded tile now, not of who happens to hold a scope open.)

            // ── Clause ii: distinct buffers == distinct source-layers touched. ───────────────────────
            var distinct = new List<NativeArray<double2>>();
            var sources  = new HashSet<string>();
            foreach ((string source, NativeArray<double2> buffer) in Concat(meshLog, symbolLog))
            {
                sources.Add(source);
                bool seen = false;
                foreach (NativeArray<double2> d in distinct) if (d.Equals(buffer)) { seen = true; break; }
                if (!seen) distinct.Add(buffer);
            }
            Assert.AreEqual(3, sources.Count,
                "precondition: the fixture must touch THREE distinct source-layers, one of them named by " +
                "exactly one style layer — otherwise 'distinct buffers == distinct source-layers' is " +
                "satisfiable by collapsing everything onto one buffer");
            Assert.AreEqual(sources.Count, distinct.Count,
                "the number of DISTINCT buffers must equal the number of distinct source-layers touched: " +
                "one materialization per source-layer, no more (a per-read materialization gives 5) and no " +
                "fewer (a collapsed memo gives 1).");
        }

        private static IEnumerable<(string source, NativeArray<double2> buffer)> Concat(
            List<(string source, NativeArray<double2> buffer)> a,
            List<(string source, NativeArray<double2> buffer)> b)
        {
            foreach (var x in a) yield return x;
            foreach (var x in b) yield return x;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // StyledFillExtrusionGraphWriteTests — vertex-count bound alone can't discriminate a mis-wound hole ring
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class StyledFillExtrusionGraphWriteTests
    {
        private static uint ZigZag(int n) => (uint)((n << 1) ^ (n >> 31));

        private static void AppendMoveTo(List<uint> cmds, ref int cx, ref int cy, int x, int y)
        {
            cmds.Add((1u << 3) | 1u);
            cmds.Add(ZigZag(x - cx)); cmds.Add(ZigZag(y - cy));
            cx = x; cy = y;
        }

        private static void AppendLineTo(List<uint> cmds, ref int cx, ref int cy, params (int x, int y)[] pts)
        {
            cmds.Add(((uint)pts.Length << 3) | 2u);
            foreach (var p in pts)
            {
                cmds.Add(ZigZag(p.x - cx)); cmds.Add(ZigZag(p.y - cy));
                cx = p.x; cy = p.y;
            }
        }

        // CW on screen (Y-down) = MVT exterior (RingAssemblyJob.cs:179's own convention) — same corner
        // order StyledFillExtrusionMeshTests' SquareRing uses.
        private static uint[] SquareRing(int x0, int y0, int size)
        {
            var cmds = new List<uint>();
            int cx = 0, cy = 0;
            AppendMoveTo(cmds, ref cx, ref cy, x0, y0);
            AppendLineTo(cmds, ref cx, ref cy, (x0 + size, y0), (x0 + size, y0 + size), (x0, y0 + size));
            return cmds.ToArray();
        }

        // Exterior ring identical in shape to SquareRing; the hole ring is wound OPPOSITE (reversed corner
        // order) — RingAssemblyJob classifies a ring by comparing its own shoelace sign against the first
        // (exterior) ring's, so a matching sign would read as a SECOND EXTERIOR, not a hole.
        private static uint[] SquareWithHoleRing(int x0, int y0, int size, int holeX0, int holeY0, int holeSize)
        {
            var cmds = new List<uint>();
            int cx = 0, cy = 0;
            AppendMoveTo(cmds, ref cx, ref cy, x0, y0);
            AppendLineTo(cmds, ref cx, ref cy, (x0 + size, y0), (x0 + size, y0 + size), (x0, y0 + size));
            AppendMoveTo(cmds, ref cx, ref cy, holeX0, holeY0);
            AppendLineTo(cmds, ref cx, ref cy, (holeX0, holeY0 + holeSize), (holeX0 + holeSize, holeY0 + holeSize), (holeX0 + holeSize, holeY0));
            return cmds.ToArray();
        }

        private static IFeature SquareFeature(int x0, int y0, int size, IReadOnlyDictionary<string, Value> props)
            => new DictionaryFeature(properties: props, geometryType: TileGeometryType.Polygon, geometry: SquareRing(x0, y0, size));

        private static IFeature CourtyardFeature(
            int x0, int y0, int size, int holeInset, int holeSize, IReadOnlyDictionary<string, Value> props)
            => new DictionaryFeature(properties: props, geometryType: TileGeometryType.Polygon,
                geometry: SquareWithHoleRing(x0, y0, size, x0 + holeInset, y0 + holeInset, holeSize));

        private const double Extent = 4096.0;
        private static readonly TileId ModerateTile = new TileId { Z = 10, X = 300, Y = 380 }; // mid-latitude, shared with StyledFillExtrusionMeshTests

        private static IReadOnlyList<IFeature> Fixture()
        {
            var squareProps    = new Dictionary<string, Value> { ["h"] = Value.Number(30.0) };
            var courtyardProps = new Dictionary<string, Value> { ["h"] = Value.Number(50.0) }; // h on BOTH features
            return new[]
            {
                SquareFeature(1000, 1000, 500, squareProps),
                CourtyardFeature(2000, 1000, 600, holeInset: 150, holeSize: 300, courtyardProps),
            };
        }

        private static FillExtrusion.PaintProperties Paint()
            => TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":[\"get\",\"h\"]}");

        /// <summary>
        /// Fixture-authoring sanity, independent of anything the graph or the managed pipeline computes:
        /// <c>Materialize</c> must have decoded exactly THREE rings (the square's one exterior + the
        /// courtyard's exterior and hole) — a two-ring command stream the decoder mis-parses (dropped
        /// MoveTo, wrong repeat count) fails loudly here rather than silently becoming "two exteriors" three
        /// tests down.
        /// </summary>
        [Test]
        public void Fixture_MaterializesToExactlyThreeRings()
        {
            var features = Fixture();
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, ModerateTile, Extent);
            try
            {
                Assert.AreEqual(3, geometry.RingCount,
                    "the square (1 exterior) + the courtyard (1 exterior + 1 hole) must decode to 3 rings.");
            }
            finally { geometry.Dispose(); }
        }

        /// <summary>
        /// The ASSERTED, permanent form of the reconstruction-validation the wall-job stage ran once in a
        /// throwaway harness (now deleted): re-hashes each golden JSON's own per-vertex Position+Normal
        /// (Stream0) and Tangent (Stream2) bytes, interleaved in the SAME order
        /// <see cref="ScheduleWrite_MatchesSplitGoldens_StreamForStream"/> hashes them, and asserts the
        /// result still reproduces the corresponding <see cref="FrozenGoldensFlat"/>/
        /// <see cref="FrozenGoldensSpherical"/> Stream0/Stream2 digest. Makes the whole-stream pin outlive the
        /// split: a golden byte changing for any reason fails this test loudly, rather than silently drifting
        /// from the proof that justified trusting it in the first place.
        /// </summary>
        [Test]
        public void GoldenJson_ReproducesFrozenDigests(
            [Values(false, true)] bool spherical)
        {
            string label = spherical ? "Spherical" : "WebMercator";
            (uint[] posHex, uint[] normHex, uint[] tanHex, int roofVertexCount, int totalVertexCount) =
                LoadGraphWriteGolden(label);
            Assert.AreEqual(posHex.Length, normHex.Length,
                $"[spherical={spherical}] positionHex/normalHex vertex counts disagree — golden is internally inconsistent.");
            int n = totalVertexCount;
            Assert.AreEqual(n * 3, posHex.Length, $"[spherical={spherical}] positionHex length != 3 * totalVertexCount.");
            Assert.AreEqual(n * 4, tanHex.Length, $"[spherical={spherical}] tangentHex length != 4 * totalVertexCount.");

            var s0 = new List<byte>();
            var s2 = new List<byte>();
            for (int i = 0; i < n; i++)
            {
                for (int c = 0; c < 3; c++) s0.AddRange(BitConverter.GetBytes(math.asfloat(posHex[i * 3 + c])));
                for (int c = 0; c < 3; c++) s0.AddRange(BitConverter.GetBytes(math.asfloat(normHex[i * 3 + c])));
                for (int c = 0; c < 4; c++) s2.AddRange(BitConverter.GetBytes(math.asfloat(tanHex[i * 4 + c])));
            }

            Dictionary<string, string> frozen = ParseGolden(spherical ? FrozenGoldensSpherical : FrozenGoldensFlat);
            Assert.AreEqual(frozen["Stream0"], Sha256(s0),
                $"[spherical={spherical}] extrusion-graphwrite-golden-{label}.json's Position+Normal bytes no " +
                "longer reproduce the frozen Stream0 digest — the golden changed since its own reconstruction " +
                "was validated, and this is what would have caught it.");
            Assert.AreEqual(frozen["Stream2"], Sha256(s2),
                $"[spherical={spherical}] extrusion-graphwrite-golden-{label}.json's Tangent bytes no longer " +
                "reproduce the frozen Stream2 digest — the golden changed since its own reconstruction was " +
                "validated, and this is what would have caught it.");
        }

        // Frozen goldens: captured from the managed StyledFillExtrusionTileBuilder.WriteMeshData arm —
        // stream0 (PositionNormal), stream1 (ExtrudeAndBake), stream2 (tangent), stream3 (colour), indices,
        // bounds — captured before WriteMeshData was rewritten to route
        // through the graph.
        // Stream0/Stream2 in these strings are used ONLY as the reconstruction-validation reference (see the
        // file header) — the live per-vertex comparison reads Assets/Fixtures/extrusion-graphwrite-golden-
        // *.json instead. Stream1/Stream3/Indices/Bounds are still compared against these strings directly.
        //
        // Stream3 (colour) was re-captured 2026-09-07 and is the ONLY digest here that has moved
        // since. This fixture styles a CONSTANT fill-extrusion-color, which is not baked into
        // the COLOR stream — it rides the _BaseColor uniform and the vertex carries the white identity. The
        // digest therefore pins that white-identity fact, not a baked colour (see its assertion below).
        // Stream0/1/2, Indices and Bounds are untouched, which is what confines that change to the colour path.
        //
        // vertex sharing (Spherical only — WebMercator never enters the subdivider):
        // GlobeFillSubdivideJob shares the roof's shared vertices, so its storage layout shrank
        // (roofVertexCount 30→12, totalVertexCount 78→60 — see extrusion-graphwrite-golden-Spherical.json)
        // and Stream0/1/2/3/Indices moved with it (Bounds did not — a min/max reduction is invariant to
        // which duplicate of a shared coordinate survives sharing, confirmed unmoved by the same capture).
        // Trustworthiness of these new numbers rests on RoofDeindexedStream_MatchesFrozenDigest_Spherical
        // (below), not on this capture alone — see that test's doc.
        private const string FrozenGoldensFlat =
            "Stream0=/BSPNaJt/vKUH5jnoGe18XG++7De4CQ5CaAkGo5/8cA= Stream1=5yoGCdWnImZda9zBFM2lMZ4cZpoio+676rYaTQc6Tj4= " +
            "Stream2=RZIb0svnW8ClUJy19C/CWEwcYrSRiGcuVrCU8pAINPM= Stream3=mTU6yDV37mWN86QXk22ebkLiBzEWq1ZAV/SCMCedDyQ= " +
            "Indices=UDPCPGXJU3OsWoNjbMg2T3AYq8M+lCd4AJOThHpv+uk= Bounds=U2c5vKyQgtfxmbjk+DwMuZ6m8SWFVpA+FZdtEiShrRs=";
        private const string FrozenGoldensSpherical =
            "Stream0=9sMgYPrYFR94lXLoVJDxijo6Bs3AYfbsdqlK3MWQ+R4= Stream1=ucir7tIjYxm9UiBL4GkqayK2I7hiB5hw4DCCGZfnQoI= " +
            "Stream2=eXX7a9tjYp/WC1KRsmKGg+/hW+jTh+hfTF93KqI353A= Stream3=jujmGM1QiI66NmIvsgG6bNN2zuISOIHEKZU+Q6CN02U= " +
            "Indices=nKqdVPkofLl5PE9XkvXfc3w1AAP8eddsf9IYEORH8Rc= Bounds=gokodWKrmk2WSnB0etMqXgtn1CTMRDmjLL82v96b+oE=";

        // Measured 2026-09-04 (wall-job stage): per-component max ULP delta between the retired managed
        // WriteWalls loop and the new Burst WallQuadJob chain, on THIS fixture's wall tail, plus a stated +2
        // margin for hardware/Burst-version headroom. job-scheduling-design.md bounds the
        // RAW double-precision projection output; Normal/Tangent here are NORMALIZED DIFFERENCES of two
        // nearby float32 positions — a different quantity, not safely inferred from that table, so measured
        // fresh rather than assumed. A regression that moves bytes (wrong index, wrong argument order, wrong
        // latitude) moves them far past these bounds; only genuine Burst-vs-managed rounding sits under them.
        // Position has no bound array: it is asserted bit-exact (a literal 0) everywhere, roof and wall tail,
        // for the STRUCTURAL reason above the assertion call sites — a divergence provably cannot reach it,
        // so a tolerance here would trade a proven guarantee for an unneeded one.
        private static readonly int[] WallNormalMaxUlpFlat        = { 2, 2, 2 };
        private static readonly int[] WallTangentMaxUlpFlat       = { 2, 2, 2, 2 };
        private static readonly int[] WallNormalMaxUlpSpherical   = { 3, 3, 3 };
        private static readonly int[] WallTangentMaxUlpSpherical  = { 4, 2, 3, 2 };

        /// <summary>
        /// (a) Extrusion stream parity — job-scheduling-design.md, split per the
        /// wall-job stage (2026-09-04, see the file header). Stream1/Stream3/Indices/Bounds still match the
        /// frozen whole-stream hashes exactly; Stream0/Stream2 compare per-vertex against the golden JSON —
        /// bit-exact on the roof prefix, within the measured ULP bound on the wall tail.
        ///
        /// RED — two EXECUTED against this exact instrument (2026-09-04), one per split arm, confirmed RED
        /// with the injection and GREEN with it reverted:
        /// <list type="bullet">
        /// <item><b>Roof-prefix arm.</b> In <c>FillExtrusionStreamWriteJob.Execute</c>
        /// (<c>StyledFillExtrusionTileBuilder.WriteJob.cs</c>), forced <c>s2[i] = new Vector4(1f, 0f, 0f, 1f)</c>
        /// unconditionally instead of the per-vertex east — inert on the flat arm (its east already IS the
        /// constant <c>(1,0,0)</c>) and RED on the spherical arm with
        /// <c>"[spherical=True] Stream2.Tangent.x[0] (ROOF) diverges from the bit-exact golden — actual=0x3F800000
        /// (1) golden=0x3F769FC6 (0.963375449)"</c> — the bit-exact roof-prefix assertion, exactly the arm this
        /// injection targets.</item>
        /// <item><b>Wall-tail arm.</b> In <c>WallQuadJob.Execute</c> (<c>StyledFillExtrusionTileBuilder.WallJob.cs</c>),
        /// swapped <c>posA</c> for its edge partner's projected position (<c>World[idxB]</c> instead of
        /// <c>World[idxA]</c>) — RED on both projections (Position is bit-exact everywhere, so a shift shows
        /// immediately), e.g. <c>"[spherical=False] Stream0.Position.x[14] (WALL) diverges from the bit-exact
        /// golden — actual=0x465FEFC5 (14331.9424) golden=0x46154A84 (9554.629)"</c> — index 14 is
        /// <c>roofVertexCount</c> on the flat fixture, i.e. the first wall vertex, confirming the wall-tail
        /// assertion (not the roof one) is what fired. <b>The roof-prefix arm is RED-verified on the
        /// SPHERICAL projection only</b> — the injection is inert on flat (its east already IS the
        /// constant <c>(1,0,0)</c>, so the corruption writes the same bytes the golden expects); the flat
        /// roof prefix has no executed RED of its own, only the shared assertion machinery the spherical
        /// run already exercised.</item>
        /// </list>
        /// Both injections were reverted immediately after RED-verifying — checked by re-running this test
        /// class clean (3/3 green) and independently by grepping for the INJECTED expressions themselves
        /// (never the bare <c>RED-VERIFY</c> token — this paragraph's own prose contains it, which would make
        /// a bare token grep against it self-defeating): <c>grep -n "s2\[i\] = new Vector4(1f, 0f, 0f, 1f);"
        /// StyledFillExtrusionTileBuilder.WriteJob.cs</c> and <c>grep -n "posA = (float3)World\[idxB\];"
        /// StyledFillExtrusionTileBuilder.WallJob.cs</c> (the LEGITIMATE <c>posB = (float3)World[idxB];</c> a
        /// line below does not match — the injected pattern names <c>posA</c>, not <c>posB</c>, specifically),
        /// both returning empty. Both injections were re-run once more, one at a time against a freshly
        /// confirmed-clean baseline (no injection ⇒ 3/3 green, verified immediately before each), to remove
        /// any doubt about which message belonged to which arm.
        ///
        /// <para><b>A real defect the first roof-prefix RED-verify surfaced, in this test itself, not
        /// production:</b> <c>AssertStreamComponent</c> for Normal/Tangent passed the WALL-TAIL ULP
        /// bound unconditionally, never gated to bit-exact on the roof prefix — so a roof regression smaller
        /// than the wall-tail margin (2-4 ULP) would have passed silently. The first roof-prefix RED still
        /// caught the (enormous, 614458-ULP) injected error, but through the loose bound, not the bit-exact
        /// roof pin the doc above claims — a red for the wrong reason looks identical to a red for the right
        /// one. Gating Normal/Tangent's bound to <c>roof ? 0 : measuredBound</c>, same as Position, fixes it:
        /// the roof-prefix RED now fires the bit-exact assertion, not the bounded one.</para>
        /// </summary>
        // vertex sharing: the permanent de-indexed tooth item 6 asks for, standing in
        // for the deleted managed WriteWalls oracle. What it certifies: walking the INDEX buffer and
        // expanding each index to its full vertex tuple (Position/Normal/Tangent/ExtrudeAndBake/Colour) is
        // representation-independent of how much storage GlobeFillSubdivideJob's sharing changes — i.e. sharing
        // changes the roof's storage LAYOUT (fewer unique vertices, so ScheduleWrite_MatchesSplitGoldens_
        // StreamForStream's storage-order golden legitimately moves), never its de-indexed CONTENT (this
        // digest does not). Empirically verified once, ordinal-zero: stashed GlobeFillSubdivider.cs (only),
        // captured this exact digest on the pre-sharing tree, popped the stash, captured it again — Stream0,
        // Stream1, Stream2, Stream3, Bounds, roofIndexCount and streamLength were BYTE-IDENTICAL; only the
        // raw Indices= hash (literal index integers, not part of this claim — they shift because the roof's
        // vertex count the wall half rebases against shrank) differed, as expected. That equality is what
        // lets ScheduleWrite_MatchesSplitGoldens_StreamForStream's freshly re-captured storage-order golden
        // inherit the retired managed oracle's provenance instead of resting on this stage's own say-so.
        [Test]
        public void RoofDeindexedStream_MatchesFrozenDigest_Spherical()
        {
            IProjection projection = new SphericalProjection();
            IReadOnlyList<IFeature> features = Fixture();
            IReadOnlyList<SelectedTileFeature> selected = TestTileMeshBuilder.Selection(features);
            FillExtrusion.PaintProperties paint = Paint();
            double3 renderOrigin = TileRenderOrigin.Project(ModerateTile, projection);
            TileBufferClip clip = TileBufferClip.KeepTileUnits(0.0);
            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, ModerateTile, Extent);

            MeshWriteOutput graphWrite = default;
            NativeArray<Vector4> colors = default;
            NativeArray<Vector2> bake = default;
            NativeArray<int> ringVisitOrder = default;
            FillExtrusionGraphOutput ext = default;
            try
            {
                FillMeshPipeline.LayerInput input = StyledFillExtrusionTileBuilder.BuildLayerInput(
                    selected, geometry, paint, 0.0, renderOrigin, out colors, out bake, projection, clip);
                ringVisitOrder = input.RingVisitOrder;

                ext = FillExtrusionMeshGraph.Schedule(input, colors, bake);
                ext.Handle.Complete();
                graphWrite = StyledFillExtrusionTileBuilder.ScheduleWrite(
                    ext.Roof, colors, bake, ext.Walls, projection, ModerateTile, Extent);
                graphWrite.Handle.Complete();

                Mesh.MeshData b = graphWrite.Mda[0];
                var b0 = b.GetVertexData<StyledFillExtrusionTileBuilder.PositionNormal>(0);
                var b1 = b.GetVertexData<StyledFillExtrusionTileBuilder.ExtrudeAndBake>(1);
                var b2 = b.GetVertexData<Vector4>(2);
                var b3 = b.GetVertexData<Vector4>(3);
                NativeArray<int> bi = b.GetIndexData<int>();
                int roofIndexCount = ext.Roof.TriangleIndices.Length;

                var s0 = new List<byte>(); var s1 = new List<byte>(); var s2 = new List<byte>(); var s3 = new List<byte>();
                for (int i = 0; i < bi.Length; i++)
                {
                    int vi = bi[i];
                    Vector3 p = b0[vi].Position, n = b0[vi].Normal;
                    Vector4 tan = b2[vi];
                    Vector4 eut = b1[vi].ExtrudeUpAndT; Vector2 bbh = b1[vi].BakedBaseHeight;
                    s0.AddRange(BitConverter.GetBytes(p.x)); s0.AddRange(BitConverter.GetBytes(p.y)); s0.AddRange(BitConverter.GetBytes(p.z));
                    s0.AddRange(BitConverter.GetBytes(n.x)); s0.AddRange(BitConverter.GetBytes(n.y)); s0.AddRange(BitConverter.GetBytes(n.z));
                    s1.AddRange(BitConverter.GetBytes(eut.x)); s1.AddRange(BitConverter.GetBytes(eut.y));
                    s1.AddRange(BitConverter.GetBytes(eut.z)); s1.AddRange(BitConverter.GetBytes(eut.w));
                    s1.AddRange(BitConverter.GetBytes(bbh.x)); s1.AddRange(BitConverter.GetBytes(bbh.y));
                    s2.AddRange(BitConverter.GetBytes(tan.x)); s2.AddRange(BitConverter.GetBytes(tan.y)); s2.AddRange(BitConverter.GetBytes(tan.z)); s2.AddRange(BitConverter.GetBytes(tan.w));
                    Vector4 col = b3[vi];
                    s3.AddRange(BitConverter.GetBytes(col.x)); s3.AddRange(BitConverter.GetBytes(col.y));
                    s3.AddRange(BitConverter.GetBytes(col.z)); s3.AddRange(BitConverter.GetBytes(col.w));
                }

                float3x2 gb = graphWrite.Bounds[0];
                float3 boundsCenter = (gb.c0 + gb.c1) * 0.5f;
                float3 boundsSize = gb.c1 - gb.c0;
                var boundsBytes = new List<byte>();
                boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.x)); boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.y)); boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.z));
                boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.x)); boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.y)); boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.z));

                Assert.AreEqual(FrozenDeindexedRoofIndexCount, roofIndexCount,
                    "roof/wall split (emitted index count) moved — a topology change, not a sharing-representation question.");
                Assert.AreEqual(FrozenDeindexedStreamLength, bi.Length,
                    "de-indexed triangle-stream length moved — a topology change, not a sharing-representation question.");
                Assert.AreEqual(FrozenDeindexedStream0, Sha256(s0), "de-indexed Position+Normal diverges — a real regression.");
                Assert.AreEqual(FrozenDeindexedStream1, Sha256(s1), "de-indexed ExtrudeUpAndT+Bake diverges — a real regression.");
                Assert.AreEqual(FrozenDeindexedStream2, Sha256(s2), "de-indexed Tangent diverges — a real regression.");
                Assert.AreEqual(FrozenDeindexedStream3, Sha256(s3), "de-indexed colour diverges — a real regression.");
                Assert.AreEqual(FrozenDeindexedBounds, Sha256(boundsBytes), "de-indexed Bounds diverges — a real regression.");
            }
            finally
            {
                graphWrite.Dispose();
                if (colors.IsCreated) colors.Dispose();
                if (bake.IsCreated) bake.Dispose();
                if (ringVisitOrder.IsCreated) ringVisitOrder.Dispose();
                ext.Dispose();
                geometry.Dispose();
            }
        }

        // Frozen de-indexed digests (vertex sharing) — captured once, verified identical
        // pre- and post-sharing by the ordinal-zero stash protocol described on the test above.
        private const int FrozenDeindexedRoofIndexCount = 30;
        private const int FrozenDeindexedStreamLength = 102;
        private const string FrozenDeindexedStream0 = "7ICFr669P/x2IHqVJ5T023SWd1v90P8WMj2DItamSOk=";
        private const string FrozenDeindexedStream1 = "ezdw3k30Z4SXZi+Cl7BMLml1g6AA0p9C9HOBhMBo/GU=";
        private const string FrozenDeindexedStream2 = "F9ebvG8+LKB7EXhnPGhjYThVKcO6hZr4LI+BoI4jiuA=";
        private const string FrozenDeindexedStream3 = "v1RlA1kuuUf4NeACVRLaRb4cxUkaXgeCYNDTMeI6/1I=";
        private const string FrozenDeindexedBounds = "gokodWKrmk2WSnB0etMqXgtn1CTMRDmjLL82v96b+oE=";

        [Test]
        public void ScheduleWrite_MatchesSplitGoldens_StreamForStream(
            [Values(false, true)] bool spherical)
        {
            IProjection projection = spherical ? (IProjection)new SphericalProjection() : new WebMercatorProjection();
            IReadOnlyList<IFeature> features = Fixture();
            IReadOnlyList<SelectedTileFeature> selected = TestTileMeshBuilder.Selection(features);
            FillExtrusion.PaintProperties paint = Paint();
            double3 renderOrigin = TileRenderOrigin.Project(ModerateTile, projection);
            TileBufferClip clip = TileBufferClip.KeepTileUnits(0.0);

            TileGeometryBuffers geometry = TestTileMeshBuilder.Materialize(features, ModerateTile, Extent);

            MeshWriteOutput graphWrite = default;
            NativeArray<Vector4> colors = default;
            NativeArray<Vector2> bake   = default;
            NativeArray<int> ringVisitOrder = default;
            FillExtrusionGraphOutput ext = default;
            try
            {
                FillMeshPipeline.LayerInput input = StyledFillExtrusionTileBuilder.BuildLayerInput(
                    selected, geometry, paint, 0.0, renderOrigin,
                    out colors, out bake, projection, clip);
                ringVisitOrder = input.RingVisitOrder;
                Assert.IsTrue(input.RingVisitOrder.IsCreated, "precondition: the fixture must select real work.");

                // One graph, one ownership story (review NIT 4) — FillExtrusionMeshGraph.Schedule already
                // composes the roof via FillMeshGraph.Schedule internally; a second, separate
                // FillMeshGraph.Schedule(input) call here would schedule the roof TWICE over the same input.
                ext = FillExtrusionMeshGraph.Schedule(input, colors, bake);
                ext.Handle.Complete();
                Assert.AreEqual(FillGraphCounts.Ok, ext.Roof.Error.Value, "precondition: the measure must not fault.");
                Assert.GreaterOrEqual(ext.Roof.Counts[0].HoleCount, 1,
                    "precondition: the hole-bridge path must have been entered — a mis-wound inner " +
                    "ring reads as a second exterior and this stays 0, which is exactly the bug this fixture exists to catch.");

                graphWrite = StyledFillExtrusionTileBuilder.ScheduleWrite(
                    ext.Roof, colors, bake, ext.Walls, projection, ModerateTile, Extent);
                graphWrite.Handle.Complete();
                Assert.Greater(graphWrite.VertexCount, 0,
                    "precondition: the graph must have produced real geometry — an empty mesh would pass while proving nothing.");

                Mesh.MeshData b = graphWrite.Mda[0];
                var b0 = b.GetVertexData<StyledFillExtrusionTileBuilder.PositionNormal>(0);
                var b1 = b.GetVertexData<StyledFillExtrusionTileBuilder.ExtrudeAndBake>(1);
                var b2 = b.GetVertexData<Vector4>(2);
                var b3 = b.GetVertexData<Vector4>(3);
                NativeArray<int> bi = b.GetIndexData<int>();

                // vertex sharing: STORAGE order — this whole-stream golden
                // pins what the write step actually PUT in the buffer (the same reason TileBuildGraphTests
                // stays storage-order). The de-indexed representation lives separately, as its own permanent
                // tooth (RoofDeindexedStream_MatchesFrozenDigest_Spherical, below) — that is what certifies
                // these freshly-captured storage-order numbers are representation-equivalent to what the
                // (now-retired) managed oracle validated, without conflating two different
                // questions ("what got written" vs "is it still the same geometry") in one digest.
                var s1 = new List<byte>(); var s3 = new List<byte>();
                bool anyBaked = false;
                bool anySecPhi = false;
                bool everyRoofUnit = true;
                for (int i = 0; i < graphWrite.VertexCount; i++)
                {
                    Vector4 eut = b1[i].ExtrudeUpAndT; Vector2 bbh = b1[i].BakedBaseHeight;
                    s1.AddRange(BitConverter.GetBytes(eut.x)); s1.AddRange(BitConverter.GetBytes(eut.y));
                    s1.AddRange(BitConverter.GetBytes(eut.z)); s1.AddRange(BitConverter.GetBytes(eut.w));
                    s1.AddRange(BitConverter.GetBytes(bbh.x)); s1.AddRange(BitConverter.GetBytes(bbh.y));
                    Vector4 col = b3[i];
                    s3.AddRange(BitConverter.GetBytes(col.x)); s3.AddRange(BitConverter.GetBytes(col.y));
                    s3.AddRange(BitConverter.GetBytes(col.z)); s3.AddRange(BitConverter.GetBytes(col.w));

                    if (bbh.y != 0f) anyBaked = true;
                    float mag = new Vector3(eut.x, eut.y, eut.z).magnitude;
                    if (!spherical && mag > 1.05f) anySecPhi = true;
                    if (spherical && (mag < 1f - 1e-5f || mag > 1f + 1e-5f)) everyRoofUnit = false;
                }
                Assert.IsTrue(anyBaked, "precondition: the data-driven height bake must have been entered (BakedBaseHeight.y != 0 somewhere).");
                if (!spherical)
                    Assert.IsTrue(anySecPhi, "precondition: the flat arm's sec(φ) factor must actually be applied somewhere (ModerateTile is mid-latitude).");
                else
                    Assert.IsTrue(everyRoofUnit, "precondition: the globe arm's extrude-up must be un-scaled unit-up everywhere (Globe was derived, not defaulted).");

                var idxBytes = new List<byte>();
                for (int i = 0; i < bi.Length; i++) idxBytes.AddRange(BitConverter.GetBytes(bi[i]));

                // Center/size (not raw min/max c0/c1) — matches the captured oracle's Bounds struct, which
                // stores (min+max)*0.5 / max-min, a different float bit pattern than the raw endpoints.
                float3x2 gb = graphWrite.Bounds[0];
                float3 boundsCenter = (gb.c0 + gb.c1) * 0.5f;
                float3 boundsSize   = gb.c1 - gb.c0;
                var boundsBytes = new List<byte>();
                boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.x)); boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.y)); boundsBytes.AddRange(BitConverter.GetBytes(boundsCenter.z));
                boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.x));   boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.y));   boundsBytes.AddRange(BitConverter.GetBytes(boundsSize.z));

                // Stream1/Stream3/Indices/Bounds — UNCHANGED, still a whole-stream hash against the frozen
                // constant (the split narrows only Stream0/Stream2, below).
                Dictionary<string, string> frozen = ParseGolden(spherical ? FrozenGoldensSpherical : FrozenGoldensFlat);
                Assert.AreEqual(frozen["Stream1"], Sha256(s1), $"[spherical={spherical}] Stream1 (ExtrudeUpAndT+Bake) diverges — a real regression, not a re-bake candidate.");
                // NOT "never a re-bake candidate" — that claim held when a constant colour WAS baked
                // into this stream; it is not now. The digest encodes a white vertex, so what a
                // divergence means has flipped: reading a STYLED colour here means the constant-colour bake
                // came back and the layer renders colour-squared. Any OTHER divergence is still a regression.
                Assert.AreEqual(frozen["Stream3"], Sha256(s3), $"[spherical={spherical}] Stream3 (colour) diverges. This fixture's fill-extrusion-color is CONSTANT, so the stream must carry the WHITE identity and the colour must ride _BaseColor; a styled colour here means the vertex bake was re-introduced (colour-squared). Re-bake ONLY on a deliberate, stated change to which carrier holds a constant colour.");
                Assert.AreEqual(frozen["Indices"], Sha256(idxBytes), $"[spherical={spherical}] Indices diverge — a real regression, not a re-bake candidate.");
                Assert.AreEqual(frozen["Bounds"], Sha256(boundsBytes), $"[spherical={spherical}] Bounds diverge — a real regression, not a re-bake candidate.");

                // Stream0 (Position+Normal) / Stream2 (Tangent) — split: bit-exact on the roof prefix, within
                // the measured ULP bound on the wall tail (see the file header + the bound constants above).
                string label = spherical ? "Spherical" : "WebMercator";
                (uint[] posHex, uint[] normHex, uint[] tanHex, int roofVertexCount, int totalVertexCount) =
                    LoadGraphWriteGolden(label);
                Assert.AreEqual(totalVertexCount, graphWrite.VertexCount,
                    $"[spherical={spherical}] golden vertex count ({totalVertexCount}) no longer matches the " +
                    $"build's ({graphWrite.VertexCount}) — a topology change, not a float-noise question.");

                int[] normBound = spherical ? WallNormalMaxUlpSpherical : WallNormalMaxUlpFlat;
                int[] tanBound = spherical ? WallTangentMaxUlpSpherical : WallTangentMaxUlpFlat;

                for (int i = 0; i < graphWrite.VertexCount; i++)
                {
                    bool roof = i < roofVertexCount;
                    Vector3 p = b0[i].Position, n = b0[i].Normal;
                    Vector4 tan = b2[i];

                    // Position: BIT-EXACT everywhere, roof AND wall tail — STRUCTURALLY, not by luck of this
                    // fixture (docs/job-scheduling-design.md): the
                    // origin-relative double divergence this stage measured is ~9.3e-10 absolute at tile-local
                    // magnitude, six orders below a float32 ULP there (~9.8e-4) — the (float3) narrowing
                    // erases it for any tile-local geometry, not just this one. A red here is a formula error,
                    // never drift; bounding it would be a WEAKER tooth than the code earns.
                    AssertStreamComponent(roof, p.x, posHex[i * 3 + 0], 0, spherical, i, "Stream0.Position.x");
                    AssertStreamComponent(roof, p.y, posHex[i * 3 + 1], 0, spherical, i, "Stream0.Position.y");
                    AssertStreamComponent(roof, p.z, posHex[i * 3 + 2], 0, spherical, i, "Stream0.Position.z");
                    // Normal/Tangent: bit-exact on the roof prefix too (roof is 100% untouched by this stage —
                    // a bound there would let a real roof regression under the wall-tail's own ULP margin pass
                    // silently), the measured bound applies ONLY past roofVertexCount.
                    AssertStreamComponent(roof, n.x, normHex[i * 3 + 0], roof ? 0 : normBound[0], spherical, i, "Stream0.Normal.x");
                    AssertStreamComponent(roof, n.y, normHex[i * 3 + 1], roof ? 0 : normBound[1], spherical, i, "Stream0.Normal.y");
                    AssertStreamComponent(roof, n.z, normHex[i * 3 + 2], roof ? 0 : normBound[2], spherical, i, "Stream0.Normal.z");
                    AssertStreamComponent(roof, tan.x, tanHex[i * 4 + 0], roof ? 0 : tanBound[0], spherical, i, "Stream2.Tangent.x");
                    AssertStreamComponent(roof, tan.y, tanHex[i * 4 + 1], roof ? 0 : tanBound[1], spherical, i, "Stream2.Tangent.y");
                    AssertStreamComponent(roof, tan.z, tanHex[i * 4 + 2], roof ? 0 : tanBound[2], spherical, i, "Stream2.Tangent.z");
                    AssertStreamComponent(roof, tan.w, tanHex[i * 4 + 3], roof ? 0 : tanBound[3], spherical, i, "Stream2.Tangent.w");
                }
            }
            finally
            {
                graphWrite.Dispose();
                if (colors.IsCreated) colors.Dispose();
                if (bake.IsCreated) bake.Dispose();
                // Caller-owned (FillMeshPipeline.LayerInput.RingVisitOrder's own doc — FillMeshGraph.Schedule
                // only reads it) — pre-existing leak, found chasing a Persistent-allocation leak the wall-job
                // stage's gate surfaced; unrelated to WriteWalls, confirmed by reading FillMeshGraph.cs (it
                // never disposes input.RingVisitOrder, only WriteMeshData's caller-side `using var` did).
                if (ringVisitOrder.IsCreated) ringVisitOrder.Dispose();
                ext.Dispose();
                geometry.Dispose();
            }
        }

        private static string Sha256(List<byte> bytes)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            return System.Convert.ToBase64String(sha256.ComputeHash(bytes.ToArray()));
        }

        private static Dictionary<string, string> ParseGolden(string golden)
        {
            var result = new Dictionary<string, string>();
            foreach (string part in golden.Split(' '))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) result[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            return result;
        }

        /// <summary>Loads a per-vertex Stream0/Stream2 golden written by the reconstruction capture harness
        /// (deleted after use — see the file header) — hex IEEE-754 bit patterns, comma-separated, read back
        /// via <see cref="math.asfloat(uint)"/>.
        ///
        /// <para>vertex sharing (the Spherical file only — WebMercator never enters the
        /// subdivider, unaffected): re-captured in STORAGE order (unchanged framing) once sharing shrank the
        /// roof's real unique vertex count — <c>roofVertexCount</c>/<c>totalVertexCount</c> are the new,
        /// smaller counts. Trustworthiness of the new numbers rests on
        /// <see cref="RoofDeindexedStream_MatchesFrozenDigest_Spherical"/>, a separate permanent tooth that
        /// proves the roof's DE-INDEXED triangle-stream content is bit-identical to the pre-sharing tree —
        /// i.e. sharing only changed storage layout, never geometry — which is what lets this storage-order
        /// capture inherit the retired managed oracle's provenance instead of being trusted on its own
        /// say-so.</para></summary>
        private static (uint[] posHex, uint[] normHex, uint[] tanHex, int roofVertexCount, int totalVertexCount)
            LoadGraphWriteGolden(string projectionLabel)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", $"extrusion-graphwrite-golden-{projectionLabel}.json");
            FileAssert.Exists(path);
            JsonValue root = JsonParser.Parse(File.ReadAllText(path));
            int roofVertexCount = root.Get("roofVertexCount").AsInt();
            int totalVertexCount = root.Get("totalVertexCount").AsInt();
            uint[] posHex = ParseHexArray(root.Get("positionHex").AsString(null));
            uint[] normHex = ParseHexArray(root.Get("normalHex").AsString(null));
            uint[] tanHex = ParseHexArray(root.Get("tangentHex").AsString(null));
            return (posHex, normHex, tanHex, roofVertexCount, totalVertexCount);
        }

        private static uint[] ParseHexArray(string csv)
        {
            string[] parts = csv.Split(',');
            var result = new uint[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = System.Convert.ToUInt32(parts[i], 16);
            return result;
        }

        /// <summary>The roof prefix is bit-exact (the roof path is untouched by this stage); the wall tail is
        /// within <paramref name="maxUlp"/> — the total-order IEEE-754 ULP-distance mapping (Bruce Dawson's
        /// AlmostEqualUlps idiom), never a raw bit-pattern subtraction, which is wrong across zero/sign.
        /// <paramref name="roof"/> is the TRUE <c>i &lt; roofVertexCount</c> flag (labels the failure
        /// correctly) — comparison strictness is driven by <paramref name="maxUlp"/> alone, so a bit-exact
        /// bound (e.g. Position, everywhere) still reports "(WALL)" honestly on a wall-vertex mismatch.</summary>
        private static void AssertStreamComponent(
            bool roof, float actual, uint goldenHex, int maxUlp, bool spherical, int index, string label)
        {
            uint actualHex = math.asuint(actual);
            string where = roof ? "(ROOF)" : "(WALL)";
            if (maxUlp == 0)
            {
                Assert.AreEqual(goldenHex, actualHex,
                    $"[spherical={spherical}] {label}[{index}] {where} diverges from the bit-exact golden — " +
                    $"actual=0x{actualHex:X8} ({actual:R}) golden=0x{goldenHex:X8} ({math.asfloat(goldenHex):R}). " +
                    (roof ? "The roof path is untouched by this stage — this is a real regression."
                          : "This field is bit-exact everywhere (measured 0 ULP) — this is a real regression."));
                return;
            }

            ulong delta = UlpDistance(actualHex, goldenHex);
            Assert.LessOrEqual(delta, (ulong)maxUlp,
                $"[spherical={spherical}] {label}[{index}] {where} exceeds its measured {maxUlp}-ULP bound — " +
                $"actual=0x{actualHex:X8} ({actual:R}) golden=0x{goldenHex:X8} ({math.asfloat(goldenHex):R}) delta={delta} ULP.");
        }

        private static ulong ToUlpOrder(uint bits) => (bits & 0x80000000U) != 0 ? ~bits : (bits | 0x80000000U);

        private static ulong UlpDistance(uint a, uint b)
        {
            ulong oa = ToUlpOrder(a), ob = ToUlpOrder(b);
            return oa > ob ? oa - ob : ob - oa;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // StyledFillExtrusionMeshTests — wall quad layout is a white-box assumption these teeth rely on
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class StyledFillExtrusionMeshTests : BaseTestFixture
    {
        // ── Fixture geometry: a single convex square ring (exterior only, no holes) ──────────────
        // MVT command stream: MoveTo(1) to the first corner, LineTo(3) for the rest, implicitly closed.
        // Winding (CW/CCW in tile-local Y-down space) is NOT asserted here — the Winding_FrontFacesPointOut_*
        // tests measure the ACTUAL rendered front-face sign, they do not assume one.

        private static uint ZigZag(int n) => (uint)((n << 1) ^ (n >> 31));

        private static uint[] SquareRing(int x0, int y0, int size)
        {
            return new uint[]
            {
                (1u << 3) | 1u, ZigZag(x0), ZigZag(y0),                       // MoveTo count=1 → (x0,y0)
                (3u << 3) | 2u,                                               // LineTo count=3
                ZigZag(size), ZigZag(0),                                     // → (x0+size, y0)
                ZigZag(0),    ZigZag(size),                                  // → (x0+size, y0+size)
                ZigZag(-size), ZigZag(0),                                    // → (x0, y0+size)
            };
        }

        private static IFeature SquareFeature(int x0, int y0, int size, IReadOnlyDictionary<string, Value> props = null)
            => new DictionaryFeature(properties: props, geometryType: TileGeometryType.Polygon,
                geometry: SquareRing(x0, y0, size));

        private const double Extent = 4096.0;
        private static readonly TileId ModerateTile = new TileId { Z = 10, X = 300, Y = 380 }; // mid-latitude, arbitrary

        // ── RED-verify + topology invariant: a single square footprint ───────────────────────────

        [Test]
        public void Wall_FloorAndRoof_CoincideInFootprintPosition()
        {
            var feature = SquareFeature(1000, 1000, 500);
            var paint   = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":50}"); // constant — no bake

            Mesh mesh = Track(TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, paint, 0.0, Extent, ModerateTile));
            Assert.IsNotNull(mesh, "a single square footprint must produce geometry.");

            Vector3[] positions = mesh.vertices;
            var extrudeUpAndT = new List<Vector4>();
            mesh.GetUVs(3, extrudeUpAndT);
            Assert.AreEqual(positions.Length, extrudeUpAndT.Count, "TEXCOORD3 (extrudeUpAndT) must be populated for every vertex.");

            // A hole-less convex quad triangulates without bridge splits: roof = 4 verts, walls = 4 edges *
            // 4 verts = 16 — appended in that order (see the class doc's white-box note).
            Assert.AreEqual(20, positions.Length,
                "expected roof(4) + walls(4 edges * 4 verts) = 20 vertices for a hole-less square footprint.");

            int wallStart = positions.Length - 16;
            for (int edge = 0; edge < 4; edge++)
            {
                int b = wallStart + edge * 4; // floorA, floorB, roofB, roofA
                Vector3 floorA = positions[b + 0], floorB = positions[b + 1];
                Vector3 roofB  = positions[b + 2], roofA  = positions[b + 3];
                float tFloorA = extrudeUpAndT[b + 0].w, tFloorB = extrudeUpAndT[b + 1].w;
                float tRoofB  = extrudeUpAndT[b + 2].w, tRoofA  = extrudeUpAndT[b + 3].w;

                Assert.AreEqual(0f, tFloorA, 1e-6f); Assert.AreEqual(0f, tFloorB, 1e-6f);
                Assert.AreEqual(1f, tRoofB,  1e-6f); Assert.AreEqual(1f, tRoofA,  1e-6f);

                // Floor/roof AT THE SAME RING POINT share the identical footprint position — the mesh
                // is height-agnostic; only t (+ the extrude-up magnitude, checked below) differs.
                Assert.AreEqual(floorA, roofA,
                    $"edge {edge}: floorA and roofA must coincide in position (height-agnostic footprint).");
                Assert.AreEqual(floorB, roofB,
                    $"edge {edge}: floorB and roofB must coincide in position (height-agnostic footprint).");

                Vector4 upA0 = extrudeUpAndT[b + 0], upA1 = extrudeUpAndT[b + 3];
                Vector4 upB0 = extrudeUpAndT[b + 1], upB1 = extrudeUpAndT[b + 2];
                Assert.AreEqual(new Vector3(upA0.x, upA0.y, upA0.z), new Vector3(upA1.x, upA1.y, upA1.z),
                    $"edge {edge}: extrude-up must be identical for the floor/roof pair at A.");
                Assert.AreEqual(new Vector3(upB0.x, upB0.y, upB0.z), new Vector3(upB1.x, upB1.y, upB1.z),
                    $"edge {edge}: extrude-up must be identical for the floor/roof pair at B.");
            }

            // RED-VERIFIED (2026-09-04): baking `extrudeUp * 10` into `Position` for t>0.5 in AddWallVertex
            // failed "edge 0: floorA and roofA must coincide in position (height-agnostic footprint)." —
            // this test's own message, confirming the tooth can fail.
        }

        [Test]
        public void HeightUniformChange_DoesNotChangeVertexOrIndexCount()
        {
            var feature = SquareFeature(1000, 1000, 500);

            Mesh meshLow  = Track(TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":1}"),  0.0, Extent, ModerateTile));
            Mesh meshHigh = Track(TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":999}"), 0.0, Extent, ModerateTile));
            Assert.IsNotNull(meshLow); Assert.IsNotNull(meshHigh);

            Assert.AreEqual(meshLow.vertexCount, meshHigh.vertexCount,
                "changing _ExtrusionHeight's VALUE must never change the vertex count — extrusion is a VS op.");
            Assert.AreEqual(meshLow.triangles.Length, meshHigh.triangles.Length,
                "changing _ExtrusionHeight's VALUE must never change the index count.");
        }

        // ── uniform vs bake ───────────────────────────────────────────────────────────────────────

        [Test]
        public void ConstantHeight_BakeStreamStaysZero()
        {
            var feature = SquareFeature(1000, 1000, 500);
            var paint   = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":50,\"fill-extrusion-base\":5}");

            Mesh mesh = Track(TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, paint, 0.0, Extent, ModerateTile));
            Assert.IsNotNull(mesh);

            var bakedBaseHeight = new List<Vector2>();
            mesh.GetUVs(4, bakedBaseHeight);
            Assert.AreEqual(mesh.vertexCount, bakedBaseHeight.Count);
            foreach (var bake in bakedBaseHeight)
                Assert.AreEqual(Vector2.zero, bake,
                    "constant/zoom height+base must NOT be baked per-vertex — the uniform carries them.");
        }

        [Test]
        public void DataDrivenHeight_BakesEvaluatedValuePerVertex()
        {
            var props  = new Dictionary<string, Value> { ["h"] = Value.Number(42.0) };
            var feature = SquareFeature(1000, 1000, 500, props);
            var paint   = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":[\"get\",\"h\"]}");
            Assert.IsTrue(paint.Height.DependsOnFeature, "fixture sanity: [\"get\",\"h\"] must classify as Feature-kind.");

            Mesh mesh = Track(TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, paint, 0.0, Extent, ModerateTile));
            Assert.IsNotNull(mesh);

            var bakedBaseHeight = new List<Vector2>();
            mesh.GetUVs(4, bakedBaseHeight);
            Assert.AreEqual(mesh.vertexCount, bakedBaseHeight.Count);
            foreach (var bake in bakedBaseHeight)
                Assert.AreEqual(42.0, bake.y, 1e-4,
                    "data-driven fill-extrusion-height must bake the EVALUATED value per vertex.");
        }

        // ── height factor tracks per-vertex sec(φ) — two discriminators ────────────────────────────
        // z=0 (one tile spans the whole globe): a footprint near the north edge sits at high latitude
        // (sec φ ≫ 1); one straddling the equator sits at φ≈0 (sec φ = 1, the value a MISSING factor would
        // also produce — the equator is not a discriminating point on its own). Both builds share the SAME
        // TileId, so a per-TILE-constant implementation (wrong: the contract is PER-VERTEX) would answer identically
        // for both footprints; only a genuinely per-vertex sec φ tracks the different Y.

        [Test]
        public void ExtrudeUpMagnitude_TracksPerVertexLatitude_NotPerTileConstant()
        {
            var z0 = new TileId { Z = 0, X = 0, Y = 0 };
            var paint = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":10}");

            // High-latitude footprint (~φ=80°, sec≈5.76) — see TileToGeoJob.GeoAt: v≈0.1146 ⇒ y≈469 at extent 4096.
            var highLatFeature = SquareFeature(1800, 400, 100);
            // Equatorial footprint (φ≈0°, sec≈1) — v=0.5 ⇒ y=2048.
            var equatorFeature = SquareFeature(1800, 2000, 100);

            Mesh highLatMesh = Track(TestTileMeshBuilder.BuildFillExtrusion(new[] { highLatFeature }, paint, 0.0, Extent, z0));
            Mesh equatorMesh = Track(TestTileMeshBuilder.BuildFillExtrusion(new[] { equatorFeature }, paint, 0.0, Extent, z0));
            Assert.IsNotNull(highLatMesh); Assert.IsNotNull(equatorMesh);

            double highLatFactor = MaxExtrudeUpMagnitude(highLatMesh);
            double equatorFactor = MaxExtrudeUpMagnitude(equatorMesh);

            var w = TestContext.Out;
            w.WriteLine($"high-latitude extrudeUp magnitude (expect ~5.76): {highLatFactor:0.0000}");
            w.WriteLine($"equatorial extrudeUp magnitude    (expect ~1.00): {equatorFactor:0.0000}");
            w.Flush();

            Assert.That(equatorFactor, Is.EqualTo(1.0).Within(0.05),
                "at the equator sec(φ)=1 — the extrude-up must be un-scaled unit-up.");
            Assert.That(highLatFactor, Is.GreaterThan(3.0),
                "at φ≈80° sec(φ)≈5.76 — a missing/per-tile-constant factor would read ≈1.0 here too.");
        }

        private static double MaxExtrudeUpMagnitude(Mesh mesh)
        {
            var extrudeUpAndT = new List<Vector4>();
            mesh.GetUVs(3, extrudeUpAndT);
            double max = 0.0;
            foreach (var v in extrudeUpAndT)
                max = math.max(max, (double)new Vector3(v.x, v.y, v.z).magnitude);
            return max;
        }

        // ── winding — front faces point OUT, under both Mercator and the globe ─────────────────────
        // Mirrors GlobeFillWindingTests.WindingSign: tally sign(dot(cross(edges), lighting-normal)) across
        // every triangle. An ABSOLUTE +1 check (not merely globe==Mercator) — see that file's comment for
        // why a relative-only check can pass while both sides are inverted.

        /// <summary>
        /// Tallies sign(dot(face-normal, lighting-normal)) over triangles whose FIRST vertex index falls in
        /// <c>[minVertex, maxVertexExclusive)</c> — lets the caller isolate roof triangles (vertex indices
        /// 0..3 for the square fixture: earcut always emits the roof first) from wall triangles (4..19),
        /// so a failure names WHICH side is inverted rather than an ambiguous combined tally.
        /// </summary>
        private static (int sign, double uniformity, int counted) WindingSign(Mesh mesh, int minVertex, int maxVertexExclusive)
        {
            // Height is extruded in the VERTEX SHADER (the mesh is built once), so a wall quad is degenerate in
            // stored object space — floor(t=0) and roof(t=1) coincide in Position. Measuring winding off raw
            // mesh.vertices gives a zero-area cross for every wall triangle (counted=0). Reconstruct the
            // post-shader position (footprint + extrudeUp·elevation) first. Winding sign is invariant to a
            // positive height scale, so unit elevation (t itself) suffices; the roof, lifted uniformly, keeps
            // its horizontal winding, and the walls gain their vertical extent.
            Vector3[] raw = mesh.vertices;
            Vector3[] n = mesh.normals;
            int[] t = mesh.triangles;
            var uv3 = new List<Vector4>();
            mesh.GetUVs(3, uv3); // TEXCOORD3 = (extrudeUp.xyz, t)
            var v = new Vector3[raw.Length];
            for (int j = 0; j < raw.Length; j++)
            {
                Vector4 e = uv3[j];
                v[j] = raw[j] + new Vector3(e.x, e.y, e.z) * e.w;
            }
            int pos = 0, neg = 0;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                if (t[i] < minVertex || t[i] >= maxVertexExclusive) continue;
                float3 va = v[t[i]], vb = v[t[i + 1]], vc = v[t[i + 2]];
                float3 g = math.cross(vb - va, vc - va);
                float3 nn = n[t[i]];
                float gm = math.length(g), nm = math.length(nn);
                if (gm <= 1e-12f || nm <= 1e-12f) continue;
                float cos = math.dot(g, nn) / (gm * nm);
                if (math.abs(cos) < 0.3f) continue; // near edge-on — skip as noise
                if (cos > 0f) pos++; else neg++;
            }
            int counted = pos + neg;
            Assert.Greater(counted, 0, "no non-degenerate triangles to measure winding on.");
            int sign = pos >= neg ? 1 : -1;
            return (sign, (double)math.max(pos, neg) / counted, counted);
        }

        private static void AssertRoofAndWallWindOut(Mesh mesh, string label)
        {
            // Wall vertex count is fixture-solid regardless of projection: walls never subdivide (the
            // builder's "Globe wall chording" note), so exactly 4 edges * 4 verts = 16 wall vertices,
            // appended after the roof. The roof itself MAY subdivide on the globe (more than 4 vertices) —
            // computing the split point from the total (rather than hardcoding roof=4) stays correct either way.
            const int wallVertexCount = 16;
            int roofVertexCount = mesh.vertexCount - wallVertexCount;
            Assert.Greater(roofVertexCount, 0, "fixture-shape assumption violated: fewer than 16 wall vertices.");

            // Independent, handedness-free check that the wall LIGHTING normal ('outward') actually points
            // away from the building interior. Without this, the sign test below is relative-wearing-absolute:
            // 'outward' is DERIVED from edgeDir, so dot(face-normal, outward)==+1 can pass with BOTH the
            // normal and the geometry inverted (the exact hazard the roof avoids because +up is unambiguous).
            // The convex square's footprint centroid is knowable, so we ground the direction on the fixture.
            AssertWallNormalsPointOutward(mesh, roofVertexCount, label);

            var (roofSign, roofUnif, roofN) = WindingSign(mesh, 0, roofVertexCount);
            var (wallSign, wallUnif, wallN) = WindingSign(mesh, roofVertexCount, mesh.vertexCount);

            TestContext.Out.WriteLine($"{label} roof: sign={roofSign} uniformity={roofUnif:0.0000} triangles={roofN}");
            TestContext.Out.WriteLine($"{label} wall: sign={wallSign} uniformity={wallUnif:0.0000} triangles={wallN}");

            Assert.Greater(roofUnif, 0.9, $"{label}: roof winding is not uniform.");
            Assert.Greater(wallUnif, 0.9, $"{label}: wall winding is not uniform.");

            Assert.AreEqual(1, roofSign,
                $"{label}: roof front face must point OUT (Unity-front under stock Cull Back). The roof " +
                "reuses StyledFillTileBuilder's already-calibrated 2↔3 index swap, so this failing would be " +
                "surprising — check FillExtrusionStreamWriteJob (StyledFillExtrusionTileBuilder.WriteJob.cs) first.");
            Assert.AreEqual(1, wallSign,
                $"{label}: wall front face must point OUT (Unity-front under stock Cull Back). With the outward " +
                "normal independently confirmed above, this is an absolute winding check: REMEDY is to reverse " +
                "the wall TRIANGLE index order in StyledFillExtrusionTileBuilder.WriteWalls (the two Add-index " +
                "triples). NOTE the A/B vertex swap is a no-op here — it negates the face normal and 'outward' " +
                "together, leaving cos invariant.");
        }

        /// <summary>
        /// Asserts each wall quad's stored lighting normal points AWAY from the footprint centroid — a
        /// fixture-grounded, handedness-free proof that 'outward' is genuinely outward, so the sign tooth
        /// downstream is a real absolute check rather than a self-referential one.
        /// </summary>
        private static void AssertWallNormalsPointOutward(Mesh mesh, int roofVertexCount, string label)
        {
            Vector3[] p = mesh.vertices;
            Vector3[] n = mesh.normals;

            // Footprint centroid: mean of the roof-cap vertices (the exterior ring of the convex square).
            Vector3 centroid = Vector3.zero;
            for (int j = 0; j < roofVertexCount; j++) centroid += p[j];
            centroid /= roofVertexCount;

            // Walls are groups of 4: floorA(+0), floorB(+1), roofB(+2), roofA(+3); all 4 share one 'outward'.
            for (int b = roofVertexCount; b + 3 < mesh.vertexCount; b += 4)
            {
                Vector3 outward = n[b];
                Vector3 edgeMid = (p[b + 0] + p[b + 1]) * 0.5f; // floorA, floorB — the wall's base edge
                float projected = Vector3.Dot(outward, edgeMid - centroid);
                Assert.Greater(projected, 0f,
                    $"{label}: wall quad at vertex {b} has an INWARD lighting normal " +
                    $"(dot(outward, mid-centroid)={projected:0.000}). Fix 'outward' in WriteWalls (negate the " +
                    "cross, or swap its operands) — do NOT touch the triangle order for this failure.");
            }
        }

        [Test]
        public void Winding_FrontFacesPointOut_Mercator()
        {
            var feature = SquareFeature(1000, 1000, 500);
            var paint   = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":50}");
            Mesh mesh = Track(TestTileMeshBuilder.BuildFillExtrusion(new[] { feature }, paint, 0.0, Extent, ModerateTile));
            Assert.IsNotNull(mesh);
            Assert.AreEqual(20, mesh.vertexCount, "fixture-shape assumption: roof(4)+walls(16)=20 for a hole-less square.");

            AssertRoofAndWallWindOut(mesh, "Mercator");
        }

        [Test]
        public void Winding_FrontFacesPointOut_Globe()
        {
            var feature = SquareFeature(1000, 1000, 500);
            var paint   = TestStyle.FillExtrusionPaint("{\"fill-extrusion-height\":50}");
            Mesh mesh = Track(TestTileMeshBuilder.BuildFillExtrusion(
                new[] { feature }, paint, 0.0, Extent, ModerateTile, new SphericalProjection()));
            Assert.IsNotNull(mesh);
            // No exact vertexCount assertion here (unlike the Mercator test): the globe roof MAY subdivide
            // (GlobeFillSubdivideDispatch), so its vertex count is not pinned to 4 — only the 16 wall
            // vertices are fixture-solid (see AssertRoofAndWallWindOut).

            AssertRoofAndWallWindOut(mesh, "Globe");
        }
    }
}
