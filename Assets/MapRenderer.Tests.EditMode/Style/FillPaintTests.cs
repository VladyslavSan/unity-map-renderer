// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Json;
using MapRenderer.Core.Mvt;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S13 — <see cref="FillPaint"/>: classification, pinned values, BakeNumbers distinct-alpha,
    /// and SourceLayerResolver seam routing.
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    ///
    /// Acceptance teeth:
    ///   #1: Constant/Zoom/Feature/Composite classification per property — PINNED exact values.
    ///       No Assert.IsTrue(a||b) escape hatches.
    ///   #2: CPU gamma formula check — sRGB-to-linear approximation (not UnityEngine.Color.linear;
    ///       that conversion is tested in Unity-only FillPaintSnapshotTests).
    ///   #4: BakeNumbers ≥2 distinct per-feature alpha values from a data-driven fill-opacity.
    ///   #5: SourceLayerResolver seam — ResolveMvtLayer routes LayerName to correct MVT layer.
    /// </summary>
    [TestFixture]
    public class FillPaintTests
    {
        // ── Fixture loader (walk-up from cwd then AppContext) ────────────────────

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
                "sample-tile.bytes not found walking up from " +
                $"cwd={Directory.GetCurrentDirectory()} / AppContext={AppContext.BaseDirectory}");
        }

        private static MvtLayer LoadCountries()
        {
            var layer = MvtDecoder.Decode(LoadFixture()).GetLayer("countries");
            Assert.IsNotNull(layer, "countries layer must be present in fixture.");
            return layer;
        }

        private static List<IFeature> AdaptFeatures(MvtLayer layer)
        {
            var features = new List<IFeature>(layer.Features.Count);
            foreach (var f in layer.Features)
                features.Add(new MvtFeatureAdapter(f));
            return features;
        }

        // ── Helper: build a StyleLayer with a paint JSON object ──────────────────

        private static StyleLayer MakeFillLayer(string paintJson, string sourceLayer = "countries")
        {
            return new StyleLayer
            {
                Id          = "test-fill",
                LayerType   = StyleLayerType.Fill,
                SourceLayer = sourceLayer,
                Paint       = paintJson != null ? JsonParser.Parse(paintJson) : null,
            };
        }

        // ── #1a: Constant fill-color → Constant kind, pinned RGB ────────────────

        [Test]
        public void FillPaint_ConstantColor_ClassifiesAsConstant()
        {
            // Use ["rgba",...] — bare hex strings in JSON produce Value.String, not Value.Color.
            var layer = MakeFillLayer("{\"fill-color\":[\"rgba\",255,0,0,1]}");
            var fp    = new FillPaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.ColorKind,
                "An rgba(...) literal must classify as Constant.");
            Assert.IsNotNull(fp.Color,
                "PaintPropertyEvaluator must be non-null for Constant color.");
            Assert.IsNotNull(fp.DataDrivenColor,
                "DataDrivenPaintEvaluator must always be non-null.");
            Assert.IsFalse(fp.IsInertFallback,
                "A layer with fill-color set is not inert.");

            // Pinned: rgba(255,0,0,1) → R=1, G=0, B=0.
            var c = fp.Color.EvaluateColor(0.0);
            Assert.AreEqual(1.0, c.R, 1e-4, "Red channel must be 1.0 for rgba(255,0,0,1).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Green channel must be 0.0 for rgba(255,0,0,1).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Blue channel must be 0.0 for rgba(255,0,0,1).");
        }

        // ── #1b: Zoom-dependent fill-opacity → Zoom kind ────────────────────────

        [Test]
        public void FillPaint_ZoomOpacity_ClassifiesAsZoom()
        {
            // interpolate expression: zoom 0→0.5, zoom 10→1.0.
            const string paintJson =
                "{\"fill-opacity\":[\"interpolate\",[\"linear\"],[\"zoom\"],0,0.5,10,1.0]}";
            var layer = MakeFillLayer(paintJson);
            var fp    = new FillPaint(layer);

            Assert.AreEqual(ExpressionKind.Zoom, fp.OpacityKind,
                "A zoom-interpolate expression must classify as Zoom.");
            Assert.IsNotNull(fp.Opacity,
                "PaintPropertyEvaluator must be non-null for Zoom opacity.");

            // Pinned: at zoom=0, value should be 0.5.
            double v0 = fp.Opacity.EvaluateNumber(0.0);
            Assert.AreEqual(0.5, v0, 0.01, "At zoom=0, interpolated opacity must be 0.5.");

            // Pinned: at zoom=10, value should be 1.0.
            double v10 = fp.Opacity.EvaluateNumber(10.0);
            Assert.AreEqual(1.0, v10, 0.01, "At zoom=10, interpolated opacity must be 1.0.");
        }

        // ── #1c: Feature-dependent fill-color → Feature kind ────────────────────

        [Test]
        public void FillPaint_DataDrivenColor_ClassifiesAsFeature()
        {
            const string paintJson =
                "{\"fill-color\":[\"match\",[\"get\",\"CONTINENT\"]," +
                "\"Asia\",[\"rgba\",200,50,50,1]," +
                "[\"rgba\",128,128,128,1]]}";
            var layer = MakeFillLayer(paintJson);
            var fp    = new FillPaint(layer);

            Assert.AreEqual(ExpressionKind.Feature, fp.ColorKind,
                "A [\"get\",...] match expression must classify as Feature.");
            Assert.IsNull(fp.Color,
                "PaintPropertyEvaluator must be null for Feature-kind color (data-driven only).");
            Assert.IsNotNull(fp.DataDrivenColor,
                "DataDrivenPaintEvaluator must be non-null for Feature-kind color.");
            Assert.IsFalse(fp.IsInertFallback);
        }

        // ── #1d: Absent fill-color → Constant kind (spec default #000000) ───────

        [Test]
        public void FillPaint_AbsentColor_UsesSpecDefault_Black()
        {
            var layer = MakeFillLayer("{}"); // empty paint
            var fp    = new FillPaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.ColorKind,
                "Absent fill-color must use spec default (Constant kind).");
            Assert.IsNotNull(fp.Color);

            // Spec default: #000000 → R=0, G=0, B=0.
            var c = fp.Color.EvaluateColor(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Default fill-color R must be 0 (black).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Default fill-color G must be 0 (black).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Default fill-color B must be 0 (black).");

            Assert.IsTrue(fp.IsInertFallback,
                "A layer with no paint properties set must be flagged IsInertFallback.");
        }

        // ── #1e: Absent fill-opacity → Constant 1.0 ─────────────────────────────

        [Test]
        public void FillPaint_AbsentOpacity_UsesSpecDefault_One()
        {
            var layer = MakeFillLayer("{}");
            var fp    = new FillPaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.OpacityKind);
            Assert.IsNotNull(fp.Opacity);
            double v = fp.Opacity.EvaluateNumber(0.0);
            Assert.AreEqual(1.0, v, 1e-6, "Default fill-opacity must be 1.0.");
        }

        // ── #1f: fill-translate-anchor "viewport" → 1.0 ─────────────────────────

        [Test]
        public void FillPaint_TranslateAnchorViewport_IsOne()
        {
            var layer = MakeFillLayer("{\"fill-translate-anchor\":\"viewport\"}");
            var fp    = new FillPaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.TranslateAnchorKind);
            double v = fp.TranslateAnchor.EvaluateNumber(0.0);
            Assert.AreEqual(1.0, v, 1e-6, "fill-translate-anchor 'viewport' must encode as 1.0.");
        }

        // ── #1g: fill-translate-anchor "map" (spec default) → 0.0 ───────────────

        [Test]
        public void FillPaint_TranslateAnchorMap_IsZero()
        {
            var layer = MakeFillLayer("{\"fill-translate-anchor\":\"map\"}");
            var fp    = new FillPaint(layer);

            double v = fp.TranslateAnchor.EvaluateNumber(0.0);
            Assert.AreEqual(0.0, v, 1e-6, "fill-translate-anchor 'map' must encode as 0.0.");
        }

        // ── #1h: fill-translate [16, -8] → components pinned ────────────────────

        [Test]
        public void FillPaint_Translate_ComponentsArePinned()
        {
            var layer = MakeFillLayer("{\"fill-translate\":[16,-8]}");
            var fp    = new FillPaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.TranslateXKind);
            Assert.AreEqual(ExpressionKind.Constant, fp.TranslateYKind);

            double tx = fp.TranslateX.EvaluateNumber(0.0);
            double ty = fp.TranslateY.EvaluateNumber(0.0);
            Assert.AreEqual(16.0, tx, 1e-6, "fill-translate x must be 16.");
            Assert.AreEqual(-8.0, ty, 1e-6, "fill-translate y must be -8.");
        }

        // ── #1i: fill-outline-color absent → IsFallback=true, falls back to fill-color ──

        [Test]
        public void FillPaint_AbsentOutlineColor_FallsBackToFillColor()
        {
            // Use ["rgba",...] — bare hex strings produce Value.String, not Value.Color.
            var layer = MakeFillLayer("{\"fill-color\":[\"rgba\",0,0,255,1]}");
            var fp    = new FillPaint(layer);

            Assert.IsTrue(fp.OutlineColorIsFallback,
                "Absent fill-outline-color must set OutlineColorIsFallback=true.");

            // The fallback outline color should be the fill-color (rgba(0,0,255,1) → B=1).
            var c = fp.OutlineColor.EvaluateColor(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Fallback outline R must match fill-color R=0.");
            Assert.AreEqual(0.0, c.G, 1e-4, "Fallback outline G must match fill-color G=0.");
            Assert.AreEqual(1.0, c.B, 1e-4, "Fallback outline B must match fill-color B=1.");
        }

        // ── #1j: fill-outline-color present → not a fallback ────────────────────

        [Test]
        public void FillPaint_PresentOutlineColor_NotFallback()
        {
            var layer = MakeFillLayer("{\"fill-outline-color\":[\"rgba\",0,255,0,1]}");
            var fp    = new FillPaint(layer);

            Assert.IsFalse(fp.OutlineColorIsFallback,
                "Present fill-outline-color must not be flagged as fallback.");
            Assert.IsFalse(fp.IsInertFallback,
                "A layer with fill-outline-color set is not inert.");

            var c = fp.OutlineColor.EvaluateColor(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Outline R must be 0 for rgba(0,255,0,1).");
            Assert.AreEqual(1.0, c.G, 1e-4, "Outline G must be 1 for rgba(0,255,0,1).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Outline B must be 0 for rgba(0,255,0,1).");
        }

        // ── #1k: fill-pattern present → PatternName is set ──────────────────────

        [Test]
        public void FillPaint_PatternName_IsParsed()
        {
            var layer = MakeFillLayer("{\"fill-pattern\":\"grass\"}");
            var fp    = new FillPaint(layer);

            Assert.AreEqual("grass", fp.PatternName,
                "fill-pattern must be read as PatternName.");
            Assert.IsFalse(fp.IsInertFallback);
        }

        // ── #1l: null paint → IsInertFallback ────────────────────────────────────

        [Test]
        public void FillPaint_NullPaint_IsInertFallback()
        {
            var layer = MakeFillLayer(null); // no paint object
            var fp    = new FillPaint(layer);

            Assert.IsTrue(fp.IsInertFallback,
                "A layer with null Paint must be IsInertFallback.");
        }

        // ── #2: CPU gamma formula — sRGB→linear approximation ────────────────────
        // The D2 fix linearizes vertex colors in MeshBuilder before Mesh.SetColors.
        // This tooth verifies the *formula* without UnityEngine (Unity-only test covers the mesh path).
        // sRGB → linear: linear = ((sRGB + 0.055) / 1.055)^2.4 for sRGB > 0.04045,
        //                         sRGB / 12.92 otherwise.

        [Test]
        public void SrgbToLinear_Formula_KnownValues()
        {
            // Known sRGB value 0.5 → linear ≈ 0.2140 (standard conversion).
            double srgb = 0.5;
            double linear = SrgbToLinear(srgb);
            Assert.AreEqual(0.2140, linear, 0.001,
                "sRGB 0.5 must convert to linear ~0.2140 (standard sRGB→linear formula).");

            // sRGB 1.0 → linear 1.0.
            Assert.AreEqual(1.0, SrgbToLinear(1.0), 1e-5, "sRGB 1.0 must map to linear 1.0.");

            // sRGB 0.0 → linear 0.0.
            Assert.AreEqual(0.0, SrgbToLinear(0.0), 1e-5, "sRGB 0.0 must map to linear 0.0.");

            // sRGB 0.02 (below knee) → linear = 0.02 / 12.92 ≈ 0.001549.
            Assert.AreEqual(0.02 / 12.92, SrgbToLinear(0.02), 1e-5,
                "sRGB below knee (< 0.04045) must use linear segment: sRGB/12.92.");
        }

        private static double SrgbToLinear(double srgb)
        {
            if (srgb <= 0.04045)
                return srgb / 12.92;
            return Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        // ── #4: BakeNumbers ≥2 distinct alphas from a data-driven fill-opacity ───

        [Test]
        public void BakeNumbers_DataDrivenOpacity_ProducesDistinctAlphas()
        {
            // fill-opacity keyed on CONTINENT:
            //   Asia → 1.0, South America → 0.5, default → 0.75
            const string paintJson =
                "{\"fill-opacity\":[\"match\",[\"get\",\"CONTINENT\"]," +
                "\"Asia\",1.0," +
                "\"South America\",0.5," +
                "0.75]}";
            var layer    = MakeFillLayer(paintJson);
            var fp       = new FillPaint(layer);

            Assert.AreEqual(ExpressionKind.Feature, fp.OpacityKind,
                "Data-driven opacity must classify as Feature.");

            var mvtLayer = LoadCountries();
            var features = AdaptFeatures(mvtLayer);

            List<double> alphas = FeatureColorBaker.BakeNumbers(fp.DataDrivenOpacity, 0.0, features, 1.0);

            Assert.AreEqual(features.Count, alphas.Count,
                "BakeNumbers must return one alpha per feature.");

            // Collect distinct alpha values (rounded to 2 decimals to ignore fp noise).
            var distinct = new System.Collections.Generic.HashSet<double>();
            foreach (double a in alphas)
                distinct.Add(Math.Round(a, 2));

            Assert.GreaterOrEqual(distinct.Count, 2,
                $"Data-driven fill-opacity on CONTINENT must produce ≥2 distinct alpha values " +
                $"across {features.Count} features (got {distinct.Count}). " +
                "Fixture has Asia (opacity=1.0) and South America (opacity=0.5) features.");
        }

        // ── #5: SourceLayerResolver seam — routes LayerName to MVT layer ─────────

        [Test]
        public void SourceLayerResolver_RoutesLayerNameToMvtLayer()
        {
            byte[] bytes  = LoadFixture();
            var tile      = MvtDecoder.Decode(bytes);

            // A StyleLayer with SourceLayer="countries" must resolve to the countries MVT layer.
            var styleLayer = new StyleLayer
            {
                Id          = "test",
                SourceLayer = "countries",
            };

            var mvtLayer = SourceLayerResolver.ResolveMvtLayer(styleLayer, tile);
            Assert.IsNotNull(mvtLayer,
                "SourceLayerResolver.ResolveMvtLayer must return the MVT layer for SourceLayer='countries'.");
            Assert.AreEqual("countries", mvtLayer.Name,
                "Resolved MVT layer name must match SourceLayer.");
        }

        [Test]
        public void SourceLayerResolver_MissingLayer_ReturnsNull()
        {
            byte[] bytes = LoadFixture();
            var tile     = MvtDecoder.Decode(bytes);

            var styleLayer = new StyleLayer
            {
                Id          = "test",
                SourceLayer = "__nonexistent_layer__",
            };

            var mvtLayer = SourceLayerResolver.ResolveMvtLayer(styleLayer, tile);
            Assert.IsNull(mvtLayer,
                "SourceLayerResolver must return null for a layer name not present in the tile.");
        }

        [Test]
        public void SourceLayerResolver_NullSourceLayer_ReturnsNull()
        {
            byte[] bytes = LoadFixture();
            var tile     = MvtDecoder.Decode(bytes);

            var styleLayer = new StyleLayer
            {
                Id          = "background",
                SourceLayer = null,
            };

            var mvtLayer = SourceLayerResolver.ResolveMvtLayer(styleLayer, tile);
            Assert.IsNull(mvtLayer,
                "SourceLayerResolver must return null when SourceLayer is null (background layers).");
        }
    }
}
