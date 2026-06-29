// Unity-only: calls StyledLineTileBuilder.BuildMeshData (NativeArray/Unity.Collections).
// NOT included in Tools/core-tests/core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;
using Line = MapRenderer.Core.Style.Line;
using Unity.Mathematics;
using MapRenderer.Unity.Rendering.Meshing;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S14 acceptance Tooth #2: data-driven per-feature color baking end-to-end through
    /// <see cref="StyledLineTileBuilder.BuildMeshData"/>.
    ///
    /// Proves the S12 bake path is wired end-to-end for line features:
    ///   - A match expression on a per-feature property produces ≥2 DISTINCT linearized colors
    ///     baked into Stream3 <see cref="StyledLineTileBuilder.LineWidthColor.Color"/> values.
    ///   - A constant-input expression (match on a non-existent key → default) produces exactly
    ///     1 distinct color across all features.
    ///
    /// No GPU needed. Tests CPU mesh-data build only.
    /// Uses the "geolines" LineString layer from the fixture (6 features with real MVT geometry).
    /// </summary>
    [TestFixture]
    public class LinePaintS14BuildMeshTests
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
        private static List<MvtFeature> LoadGeolinesWithProperty(
            string propKey, params string[] propValues)
        {
            var tile = MvtDecoder.Decode(LoadFixture());
            var layer = tile.GetLayer("geolines");
            Assert.IsNotNull(layer, "geolines layer must be present in fixture.");

            var result = new List<MvtFeature>();
            int vi = 0;
            foreach (var feature in layer.Features)
            {
                if (feature.GeometryType != MvtGeometryType.LineString) continue;

                // Inject the discriminating property (overwrite if already present).
                string value = vi < propValues.Length ? propValues[vi] : propValues[propValues.Length - 1];
                feature.Properties[propKey] = Value.String(value);
                result.Add(feature);
                vi++;

                if (vi >= Math.Max(propValues.Length, 2)) break; // need at least 2 features
            }
            return result;
        }

        // Shared build parameters (tile 0/0/0, merc origin=(0,0)).
        private static readonly TileId TestTileId = new TileId { Z = 0, X = 0, Y = 0 };
        private static readonly double2 TestOriginMerc = new double2(0.0, 0.0);
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
                Assert.AreEqual(MvtGeometryType.LineString, f.GeometryType,
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

            StyledLineTileBuilder.LayerMeshData data = default;
            try
            {
                data = StyledLineTileBuilder.BuildMeshData(
                    features, paint, layout, TestZoom, TestExtent, TestTileId, TestOriginMerc);

                Assert.IsTrue(data.IsCreated,
                    "BuildMeshData must produce geometry for geolines features with a match expression.");
                Assert.Greater(data.VertexCount, 0,
                    "VertexCount must be > 0 when geometry is produced.");

                // Collect distinct (linearized) colors from Stream3.
                var distinctColors = new HashSet<(int r, int g, int b)>();
                for (int i = 0; i < data.VertexCount; i++)
                {
                    var wc = data.Stream3WidthColor[i];
                    // Quantize to 8-bit per channel (avoid float precision mismatches).
                    int r8 = (int)(wc.Color.x * 255f + 0.5f);
                    int g8 = (int)(wc.Color.y * 255f + 0.5f);
                    int b8 = (int)(wc.Color.z * 255f + 0.5f);
                    distinctColors.Add((r8, g8, b8));
                }

                Assert.GreaterOrEqual(distinctColors.Count, 2,
                    $"A match expression mapping ≥2 distinct feature categories must produce " +
                    $"≥2 distinct linearized colors in Stream3 (got {distinctColors.Count}). " +
                    "This proves the S12 data-driven bake path is wired through BuildMeshData.");
            }
            finally
            {
                data.Dispose();
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

            StyledLineTileBuilder.LayerMeshData data = default;
            try
            {
                data = StyledLineTileBuilder.BuildMeshData(
                    features, paint, layout, TestZoom, TestExtent, TestTileId, TestOriginMerc);

                Assert.IsTrue(data.IsCreated,
                    "BuildMeshData must produce geometry for geolines features with a constant color.");
                Assert.Greater(data.VertexCount, 0, "VertexCount must be > 0.");

                // All vertices should have the same baked color (within 1-unit quantization).
                var distinctColors = new HashSet<(int r, int g, int b)>();
                for (int i = 0; i < data.VertexCount; i++)
                {
                    var wc = data.Stream3WidthColor[i];
                    int r8 = (int)(wc.Color.x * 255f + 0.5f);
                    int g8 = (int)(wc.Color.y * 255f + 0.5f);
                    int b8 = (int)(wc.Color.z * 255f + 0.5f);
                    distinctColors.Add((r8, g8, b8));
                }

                Assert.AreEqual(1, distinctColors.Count,
                    $"A constant-color expression must produce exactly 1 distinct baked color " +
                    $"across all vertices (got {distinctColors.Count} distinct). " +
                    "If more than 1, the data-driven path is incorrectly varying a constant.");
            }
            finally
            {
                data.Dispose();
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

            StyledLineTileBuilder.LayerMeshData data = default;
            try
            {
                data = StyledLineTileBuilder.BuildMeshData(
                    features, paint, layout, TestZoom, TestExtent, TestTileId, TestOriginMerc);

                Assert.IsTrue(data.IsCreated,
                    "BuildMeshData must produce geometry for geolines features with a data-driven width.");
                Assert.Greater(data.VertexCount, 0, "VertexCount must be > 0.");

                // Collect distinct quantized WidthScale values from Stream3.
                // Quantize to 1 decimal place to avoid float noise but distinguish thin vs thick.
                var distinctWidths = new HashSet<int>();
                for (int i = 0; i < data.VertexCount; i++)
                {
                    var wc = data.Stream3WidthColor[i];
                    // Round to nearest integer pixel width (thin=2, thick=10 are well-separated).
                    int w = (int)(wc.WidthScale + 0.5f);
                    distinctWidths.Add(w);
                }

                Assert.GreaterOrEqual(distinctWidths.Count, 2,
                    $"A match expression mapping ≥2 distinct feature widths must produce " +
                    $"≥2 distinct WidthScale values in Stream3 (got {distinctWidths.Count}). " +
                    "This proves the S14 data-driven width bake path is wired through BuildMeshData.");
            }
            finally
            {
                data.Dispose();
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

            StyledLineTileBuilder.LayerMeshData data = default;
            try
            {
                data = StyledLineTileBuilder.BuildMeshData(
                    features, paint, layout, TestZoom, TestExtent, TestTileId, TestOriginMerc);

                Assert.IsTrue(data.IsCreated,
                    "BuildMeshData must produce geometry for geolines features with a data-driven opacity.");
                Assert.Greater(data.VertexCount, 0, "VertexCount must be > 0.");

                // Collect distinct quantized alpha values from Stream3 Color.w.
                // 0.2 → ~51/255; 1.0 → 255/255; well-separated.
                var distinctAlphas = new HashSet<int>();
                for (int i = 0; i < data.VertexCount; i++)
                {
                    var wc = data.Stream3WidthColor[i];
                    int a8 = (int)(wc.Color.w * 255f + 0.5f);
                    distinctAlphas.Add(a8);
                }

                Assert.GreaterOrEqual(distinctAlphas.Count, 2,
                    $"A match expression mapping ≥2 distinct feature opacities must produce " +
                    $"≥2 distinct alpha values in Stream3 Color.w (got {distinctAlphas.Count}). " +
                    "This proves the S14 data-driven opacity bake path is wired through BuildMeshData.");
            }
            finally
            {
                data.Dispose();
            }
        }
    }
}
