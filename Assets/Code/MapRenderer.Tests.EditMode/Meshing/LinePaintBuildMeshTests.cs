// Unity-only: calls StyledLineTileBuilder.BuildMeshData (NativeArray/Unity.Collections).
// NOT included in Tools/core-tests/core-tests.csproj.

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

namespace MapRenderer.Tests.Meshing
{
    /// <summary>
    /// S14 acceptance Tooth #2: data-driven per-feature color baking end-to-end through
    /// <see cref="StyledLineTileBuilder.BuildMeshData"/>.
    ///
    /// Proves the S12 bake path is wired end-to-end for line features:
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
        /// <remarks>IR C1 P3: a decoded <c>MvtFeature</c> no longer carries a command stream, so the
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
        // S91-C: WriteMeshData now takes a double3 render origin (Mercator: (mercX, 0, mercZ)); zero here.
        private static readonly double3 TestOriginMerc = new double3(0.0, 0.0, 0.0);
        private const double TestExtent = 4096.0;
        private const double TestZoom   = 0.0;

        // ── Tooth #2a: data-driven match → ≥2 distinct baked colors ────────────

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

            var paintLayer = new StyleLayer
            {
                Id          = "test-geolines",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "geolines",
                PaintJson   = MapRenderer.Core.Json.JsonParser.Parse(
                    $"{{\"line-color\":{matchExpr},\"line-width\":4}}"),
            };
            var paint = new Line.PaintProperties(paintLayer);
            var layout = new Line.LayoutProperties(paintLayer);

            var mda = Mesh.AllocateWritableMeshData(1);
            // IR C1 P2: the builder BORROWS the source-layer buffer; the caller mints and frees it. The
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
                    "This proves the S12 data-driven bake path is wired through WriteMeshData.");
            }
            finally
            {
                geometry.Dispose();
                mda.Dispose();
            }
        }

        // ── Tooth #2b: constant-input control → exactly 1 distinct baked color ─

        [Test]
        public void BuildMeshData_ConstantColorExpression_BakesUniformColor()
        {
            var features = LoadGeolinesWithProperty("__unused_prop__", "X", "X");
            Assert.GreaterOrEqual(features.Count, 2,
                "Need at least 2 geolines features for the constant-color control.");

            // Constant rgba literal: same for all features regardless of properties.
            const string constantExpr = "[\"rgba\",100,150,200,1]";

            var paintLayer = new StyleLayer
            {
                Id          = "test-constant",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "geolines",
                PaintJson   = MapRenderer.Core.Json.JsonParser.Parse(
                    $"{{\"line-color\":{constantExpr},\"line-width\":4}}"),
            };
            var paint = new Line.PaintProperties(paintLayer);
            var layout = new Line.LayoutProperties(paintLayer);

            var mda = Mesh.AllocateWritableMeshData(1);
            // IR C1 P2: the builder BORROWS the source-layer buffer; the caller mints and frees it. The
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

        // ── Tooth #2c: data-driven line-width → ≥2 distinct baked WidthScales ───

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

            var paintLayer = new StyleLayer
            {
                Id          = "test-dd-width",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "geolines",
                // line-width is data-driven (Feature kind); line-color is constant.
                PaintJson   = MapRenderer.Core.Json.JsonParser.Parse(
                    $"{{\"line-width\":{widthExpr}}}"),
            };
            var paint = new Line.PaintProperties(paintLayer);
            var layout = new Line.LayoutProperties(paintLayer);

            // Confirm the paint classified WidthKind as Feature (gate for the bake).
            Assert.AreEqual(MapRenderer.Core.Expressions.ExpressionKind.Feature, paint.WidthKind,
                "WidthKind must be Feature for a [\"get\",...] match expression.");

            var mda = Mesh.AllocateWritableMeshData(1);
            // IR C1 P2: the builder BORROWS the source-layer buffer; the caller mints and frees it. The
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
                    "This proves the S14 data-driven width bake path is wired through WriteMeshData.");
            }
            finally
            {
                geometry.Dispose();
                mda.Dispose();
            }
        }

        // ── Tooth #2d: data-driven line-opacity → ≥2 distinct baked alpha values ─

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

            var paintLayer = new StyleLayer
            {
                Id          = "test-dd-opacity",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "geolines",
                // line-opacity is data-driven (Feature kind); other properties at defaults.
                PaintJson   = MapRenderer.Core.Json.JsonParser.Parse(
                    $"{{\"line-opacity\":{opacityExpr}}}"),
            };
            var paint = new Line.PaintProperties(paintLayer);
            var layout = new Line.LayoutProperties(paintLayer);

            // Confirm the paint classified OpacityKind as Feature.
            Assert.AreEqual(MapRenderer.Core.Expressions.ExpressionKind.Feature, paint.OpacityKind,
                "OpacityKind must be Feature for a [\"get\",...] match expression.");

            var mda = Mesh.AllocateWritableMeshData(1);
            // IR C1 P2: the builder BORROWS the source-layer buffer; the caller mints and frees it. The
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
                    "This proves the S14 data-driven opacity bake path is wired through WriteMeshData.");
            }
            finally
            {
                geometry.Dispose();
                mda.Dispose();
            }
        }

        // ── Tooth #3: line-miter-limit threaded (latent-bug fix) ────────────────

        /// <summary>
        /// Before this stage, <see cref="SyncMeshWrite.Line"/>'s production predecessor hardcoded the ribbon job's
        /// miter limit to <c>2.0</c> regardless of style, so a style-authored <c>line-miter-limit</c> was
        /// silently ignored. A synthetic 90° corner (f = 1/cos(45°) = √2 ≈ 1.414) stays a sharp miter under
        /// the default limit (2.0 — <c>LineTessellatorTests.RightAngle_MiterJoin_ExactVertexCount</c>: 6
        /// verts) but must BEVEL under a tight <c>"line-miter-limit": 1.0</c> (√2 &gt; 1.0 —
        /// <c>LineTessellatorTests.SharpAngle_ExplicitBevel_VertexCountExactlyOneBevelExtra</c>: 7 verts).
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

            var styleLayer = new StyleLayer
            {
                Id          = "test-miter-limit",
                LayerType   = StyleLayerType.Line,
                SourceLayer = "test",
                PaintJson   = MapRenderer.Core.Json.JsonParser.Parse("{\"line-width\":4}"),
                LayoutJson  = MapRenderer.Core.Json.JsonParser.Parse("{\"line-miter-limit\":1.0}"),
            };
            var paint  = new Line.PaintProperties(styleLayer);
            var layout = new Line.LayoutProperties(styleLayer);

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

        // ── Tooth #4: line-round-limit threaded ──────────────────────────────────

        /// <summary>
        /// F2 review finding: hardcoding <c>RoundLimit</c> in <see cref="StyledLineTileBuilder"/> (instead of
        /// reading <c>layout.RoundLimit</c>) passes the entire suite — the same latent-bug class T3 above
        /// exists to catch for <c>MiterLimit</c>. Reuses T3's 90° corner (f = 1/cos(45°) = √2 ≈ 1.414) with
        /// <c>line-join: round</c>: under <c>line-round-limit: 1.05</c> the corner is NOT shallow (√2 > 1.05)
        /// so the fan is preserved (11 verts — <c>LineTessellatorTests.RightAngle_RoundJoin_ExactVertexCount</c>);
        /// under <c>line-round-limit: 2.0</c> the SAME corner IS shallow (√2 ≤ 2.0) and collapses to the
        /// (unclamped, since miterLimit stays default 2.0 ⇒ √2 ≤ 2.0 does not bevel) miter path (6 verts —
        /// <c>LineTessellatorTests.RightAngle_MiterJoin_ExactVertexCount</c>). A build that hardcodes
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
                var styleLayer = new StyleLayer
                {
                    Id          = $"test-round-limit-{roundLimit}",
                    LayerType   = StyleLayerType.Line,
                    SourceLayer = "test",
                    PaintJson   = MapRenderer.Core.Json.JsonParser.Parse("{\"line-width\":4}"),
                    LayoutJson  = MapRenderer.Core.Json.JsonParser.Parse(
                        $"{{\"line-join\":\"round\",\"line-round-limit\":{roundLimit}}}"),
                };
                var paint  = new Line.PaintProperties(styleLayer);
                var layout = new Line.LayoutProperties(styleLayer);
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
}
