// Unity EditMode only — real Materials via MapMaterialSetTestUtil, and a real decoded fixture tile for the
// bake-path teeth. NOT registered in Tools/core-tests/core-tests.csproj (SymbolFeatureExtractor depends on
// Unity.Collections — see its own header).
//
// The symbol text-color two-carrier split (style-transitions epic): a CONSTANT `text-color` rides the
// per-layer `_TextColor` uniform (SymbolRenderLayer.BindTextPaint); every other kind bakes into the vertex
// COLOR stream (SymbolFeatureExtractor.EvaluatePaint), gated by the single shared predicate
// SymbolTextColorCarrier.RidesUniform. The rendered-pixel product tooth lives separately, in
// Visual/SymbolTextColorRenderTests.cs — these teeth all share a CPU model of the two call sites.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Tests.TestSupport;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Tests.Style
{
    [TestFixture]
    public class SymbolTextColorCarrierTests
    {
        private const double Zoom = 8.0;

        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();

        private static T FirstLayer<T>(string json) where T : StyleLayer
            => (T)StyleParser.Parse(json).Layers[0];

        // ── Bake-path fixture (mirrors SymbolFeatureExtractorTests) ─────────────────────────────

        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static byte[] LoadFixture()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("sample-tile.bytes not found walking up from cwd/AppContext.");
        }

        private static SymbolStyle.StyleLayer CentroidsLayer(string paintJson, string textField = "{NAME}")
            => new SymbolStyle.StyleLayer
            {
                Id          = "labels",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "centroids",
                Paint       = TestStyle.SymbolPaint(paintJson),
                Layout      = TestStyle.SymbolLayout("{\"text-field\":\"" + textField + "\"}"),
            };

        private static List<SymbolStyle.SymbolFeature> Extract(SymbolStyle.StyleLayer layer)
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection();
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, FixtureTile, Zoom, projection, symbols);
            return symbols;
        }

        // ── T1: constant arm, bind ────────────────────────────────────────────────────────────

        /// <summary>The bound `_TextColor` must be the authored sRGB triple — NO manual gamma conversion
        /// before <c>ZoomStyleApplier.BindColor</c> (the same UMR-135 defect shape one field over). RED-verify:
        /// pre-convert the Constant arm's colour to linear before constructing the <c>CoreColor</c> passed to
        /// <c>BindColor</c> in <c>SymbolRenderLayer.BindTextPaint</c>.</summary>
        [Test]
        public void ConstantTextColor_Bind_CarriesTheAuthoredSrgbTriple()
        {
            var layer = FirstLayer<SymbolStyle.StyleLayer>(
                "{\"version\":8,\"layers\":[{\"id\":\"labels\",\"type\":\"symbol\",\"source\":\"s\"," +
                "\"source-layer\":\"l\",\"layout\":{\"text-field\":\"{NAME}\"}," +
                "\"paint\":{\"text-color\":\"#996633\"}}]}");
            var renderLayer = SymbolRenderLayer.Create(layer, MapMaterialSetTestUtil.Load(), Zoom, drawIndex: 0);
            try
            {
                Color c = renderLayer.WorldTextMaterial.GetColor(Shader.PropertyToID("_TextColor"));
                Assert.That(c.r, Is.EqualTo(0x99 / 255f).Within(1e-4f), "R must be the raw sRGB channel.");
                Assert.That(c.g, Is.EqualTo(0x66 / 255f).Within(1e-4f), "G must be the raw sRGB channel.");
                Assert.That(c.b, Is.EqualTo(0x33 / 255f).Within(1e-4f), "B must be the raw sRGB channel.");
            }
            finally { renderLayer.Dispose(); }
        }

        // ── T2: constant arm, alpha ────────────────────────────────────────────────────────────

        /// <summary>(a) the uniform's alpha is pinned to 1 regardless of the authored alpha — the uniform
        /// carries RGB only. RED-verify: pass <c>(float)c.A</c> instead of <c>1f</c> at the bind site.</summary>
        [Test]
        public void ConstantTextColorWithAlpha_Bind_UniformAlphaIsPinnedToOne()
        {
            var layer = FirstLayer<SymbolStyle.StyleLayer>(
                "{\"version\":8,\"layers\":[{\"id\":\"labels\",\"type\":\"symbol\",\"source\":\"s\"," +
                "\"source-layer\":\"l\",\"layout\":{\"text-field\":\"{NAME}\"}," +
                "\"paint\":{\"text-color\":[\"rgba\",153,102,51,0.5]}}]}");
            var renderLayer = SymbolRenderLayer.Create(layer, MapMaterialSetTestUtil.Load(), Zoom, drawIndex: 0);
            try
            {
                Color c = renderLayer.WorldTextMaterial.GetColor(Shader.PropertyToID("_TextColor"));
                Assert.That(c.a, Is.EqualTo(1f).Within(1e-6f),
                    "the _TextColor uniform's alpha must always be 1 — text-color's own alpha rides the " +
                    "vertex opacity stream, never this uniform.");
            }
            finally { renderLayer.Dispose(); }
        }

        /// <summary>(b) the authored alpha still reaches the extracted <see cref="SymbolPaint"/> — the
        /// bake path is where <c>text-color</c>'s alpha actually travels. RED-verify: set
        /// <c>textRgba.w = 1f</c> unconditionally in <c>EvaluatePaint</c>.</summary>
        [Test]
        public void ConstantTextColorWithAlpha_Bake_AlphaReachesTheExtractedPaint()
        {
            var symbols = Extract(CentroidsLayer("{\"text-color\":[\"rgba\",153,102,51,0.5]}"));
            Assert.Greater(symbols.Count, 0, "the fixture must yield at least one symbol.");
            Assert.That(symbols[0].Paint.TextColor.w, Is.EqualTo(0.5f).Within(1e-4f),
                "the extracted SymbolPaint's alpha must be the authored 0.5, whichever carrier holds RGB.");
        }

        // ── T3: constant arm, bake — the vertex stays white ───────────────────────────────────

        /// <summary>A CONSTANT text-color's extracted vertex RGB must be white — the colour lives in the
        /// uniform instead. RED-verify: delete the <c>RidesUniform</c> guard in
        /// <c>EvaluatePaint</c> (both carriers then hold the colour — squared, on render).</summary>
        [Test]
        public void ConstantTextColor_Bake_LeavesTheVertexWhite()
        {
            var symbols = Extract(CentroidsLayer("{\"text-color\":\"#996633\"}"));
            Assert.Greater(symbols.Count, 0, "the fixture must yield at least one symbol.");
            float4 rgba = symbols[0].Paint.TextColor;
            string what = $"a CONSTANT text-color must leave the vertex WHITE (got {rgba}) — the colour " +
                          "rides the _TextColor uniform instead. Anything else means both carriers hold it.";
            Assert.That(rgba.x, Is.EqualTo(1f).Within(1e-4f), what);
            Assert.That(rgba.y, Is.EqualTo(1f).Within(1e-4f), what);
            Assert.That(rgba.z, Is.EqualTo(1f).Within(1e-4f), what);
        }

        // ── T4: data-driven arm untouched ──────────────────────────────────────────────────────

        /// <summary>(a) a data-driven text-color must still bake its PER-FEATURE colour into the vertex,
        /// not white. RED-verify: invert the guard to <c>!RidesUniform</c>.</summary>
        [Test]
        public void DataDrivenTextColor_Bake_KeepsThePerFeatureColorInTheVertex()
        {
            var symbols = Extract(CentroidsLayer(
                "{\"text-color\":[\"match\",[\"get\",\"NAME\"],\"Aruba\",\"#996633\",\"#336699\"]}"));
            int aruba = symbols.FindIndex(s => s.Text == "Aruba");
            Assert.GreaterOrEqual(aruba, 0, "fixture precondition: a symbol named 'Aruba' must be extracted.");
            float4 rgba = symbols[aruba].Paint.TextColor;
            Assert.That(rgba.x, Is.EqualTo(0x99 / 255f).Within(1e-3f), "R must be the matched #996633, not white.");
            Assert.That(rgba.y, Is.EqualTo(0x66 / 255f).Within(1e-3f), "G must be the matched #996633, not white.");
            Assert.That(rgba.z, Is.EqualTo(0x33 / 255f).Within(1e-3f), "B must be the matched #996633, not white.");
        }

        /// <summary>(b) <c>Create</c> must leave <c>_TextColor</c> at white for a data-driven text-color —
        /// and must not throw (a data-driven expression's <c>Evaluate(zoom)</c> throws; binding
        /// unconditionally would fault the whole layer). RED-verify: bind a non-white grey in the
        /// non-constant arm.</summary>
        [Test]
        public void DataDrivenTextColor_Bind_LeavesTheUniformWhite()
        {
            var layer = FirstLayer<SymbolStyle.StyleLayer>(
                "{\"version\":8,\"layers\":[{\"id\":\"labels\",\"type\":\"symbol\",\"source\":\"s\"," +
                "\"source-layer\":\"l\",\"layout\":{\"text-field\":\"{NAME}\"}," +
                "\"paint\":{\"text-color\":[\"get\",\"c\"]}}]}");

            SymbolRenderLayer renderLayer = null;
            Assert.DoesNotThrow(() =>
                renderLayer = SymbolRenderLayer.Create(layer, MapMaterialSetTestUtil.Load(), Zoom, drawIndex: 0),
                "a data-driven text-color must not throw at Create — Evaluate(zoom) is never called for it.");
            try
            {
                Color c = renderLayer.WorldTextMaterial.GetColor(Shader.PropertyToID("_TextColor"));
                Assert.That(c, Is.EqualTo(new Color(1f, 1f, 1f, 1f)),
                    "a data-driven text-color must leave _TextColor at white — its colour lives in the vertex.");
            }
            finally { renderLayer?.Dispose(); }
        }

        // ── T5: D4's fence — a data-driven halo must not swallow the text-color bind ──────────

        /// <summary>A data-driven <c>text-halo-color</c> is skipped by its own <c>!DependsOnFeature</c>
        /// guard in <c>BindTextPaint</c>; the <c>text-color</c> arm is a separate, independent guard, so a
        /// data-driven halo cannot suppress it. RED-verify: hoist all four binds back under one
        /// <c>try</c>/<c>catch (ArgumentException)</c> and evaluate the halo first — the swallowed exception
        /// then skips the text-color bind too, leaving the uniform white while the vertex is already white
        /// (invisible text). The property this tooth guards no longer has a mechanism that can break it —
        /// each bind sits behind its own guard — but the recipe above still fires the assert below if
        /// something re-couples them.</summary>
        [Test]
        public void DataDrivenHalo_DoesNotSwallowTheConstantTextColorBind()
        {
            var layer = FirstLayer<SymbolStyle.StyleLayer>(
                "{\"version\":8,\"layers\":[{\"id\":\"labels\",\"type\":\"symbol\",\"source\":\"s\"," +
                "\"source-layer\":\"l\",\"layout\":{\"text-field\":\"{NAME}\"}," +
                "\"paint\":{\"text-color\":\"#996633\",\"text-halo-color\":[\"get\",\"hc\"]}}]}");
            var renderLayer = SymbolRenderLayer.Create(layer, MapMaterialSetTestUtil.Load(), Zoom, drawIndex: 0);
            try
            {
                Color c = renderLayer.WorldTextMaterial.GetColor(Shader.PropertyToID("_TextColor"));
                Assert.That(c.r, Is.EqualTo(0x99 / 255f).Within(1e-4f),
                    "a data-driven text-halo-color must not prevent the constant text-color bind (R).");
                Assert.That(c.g, Is.EqualTo(0x66 / 255f).Within(1e-4f),
                    "a data-driven text-halo-color must not prevent the constant text-color bind (G).");
                Assert.That(c.b, Is.EqualTo(0x33 / 255f).Within(1e-4f),
                    "a data-driven text-halo-color must not prevent the constant text-color bind (B).");
            }
            finally { renderLayer.Dispose(); }
        }

        // ── T7: the predicate itself, over real parsed expressions ────────────────────────────

        private static StyleProperty<CoreColor> ColorProperty(string json)
            => new StyleProperty<CoreColor>(JsonParser.Parse(json), new CoreColor(0, 0, 0, 1),
                v => v.AsColorCoerced());

        /// <summary><c>RidesUniform</c> is true for Constant, and false for Zoom/Feature/Composite — the
        /// discriminator is <c>Kind == Constant</c>, not <c>!DependsOnFeature</c>, because Zoom stays on the
        /// vertex-bake carrier by the decision recorded in SSOT §6 (a Zoom-kind ease path is out of scope,
        /// see <see cref="RestyleSurvivorGateTests"/>'s <c>SymbolLayer_ZoomKindTextColorChange_IsRefused</c>).
        /// RED-verify: widen the predicate to <c>!DependsOnFeature</c> — the Zoom row is the one that then
        /// wrongly reads true.</summary>
        [Test]
        public void RidesUniform_IsTrueOnlyForConstant()
        {
            var constant  = ColorProperty("\"#996633\"");
            var zoom      = ColorProperty("[\"interpolate\",[\"linear\"],[\"zoom\"],5,\"#000000\",15,\"#ffffff\"]");
            var feature   = ColorProperty("[\"get\",\"c\"]");
            var composite = ColorProperty("[\"interpolate\",[\"linear\"],[\"zoom\"],5,[\"get\",\"c\"],15,\"#ffffff\"]");

            Assert.AreEqual(MapRenderer.Core.Expressions.ExpressionKind.Constant, constant.Kind);
            Assert.AreEqual(MapRenderer.Core.Expressions.ExpressionKind.Zoom, zoom.Kind);
            Assert.AreEqual(MapRenderer.Core.Expressions.ExpressionKind.Feature, feature.Kind);
            Assert.AreEqual(MapRenderer.Core.Expressions.ExpressionKind.Composite, composite.Kind);

            Assert.IsTrue(SymbolTextColorCarrier.RidesUniform(constant), "Constant must ride the uniform.");
            Assert.IsFalse(SymbolTextColorCarrier.RidesUniform(zoom),
                "Zoom must NOT ride the uniform — it stays on the vertex-bake carrier by the SSOT §6 decision; " +
                "the restyle gate reads this same predicate, so widening it here would silently free a key " +
                "the in-place path cannot re-bake.");
            Assert.IsFalse(SymbolTextColorCarrier.RidesUniform(feature), "Feature must not ride the uniform.");
            Assert.IsFalse(SymbolTextColorCarrier.RidesUniform(composite), "Composite must not ride the uniform.");
        }

        // ── T9 (UMR-147): the extraction half of skip 3's "no symbol BAKE output" claim ───────

        /// <summary>Skip 3's observer (<c>MapView.cs</c>): a Constant <c>text-color</c> change between two
        /// otherwise-identical symbol layers must extract IDENTICAL <see cref="SymbolPaint"/>s (white vertex
        /// RGB both sides), which is what makes <c>SymbolSubsystem</c> keeping the previous document's
        /// symbol layers checkable rather than asserted. RED-verify: delete the <c>RidesUniform</c> guard in
        /// <c>SymbolFeatureExtractor.EvaluatePaint</c> — the two extractions then carry different vertex
        /// colours and the <c>TextColor.x</c> assertion fires.</summary>
        [Test]
        public void ConstantTextColorChange_LeavesTheExtractedPaintIdentical()
        {
            var oldSymbols = Extract(CentroidsLayer("{\"text-color\":\"#996633\"}"));
            var newSymbols = Extract(CentroidsLayer("{\"text-color\":\"#2288DD\"}"));
            Assert.Greater(oldSymbols.Count, 0, "the fixture must yield at least one symbol.");
            Assert.AreEqual(oldSymbols.Count, newSymbols.Count, "precondition: same feature count both sides.");

            float4 oldRgba = oldSymbols[0].Paint.TextColor;
            float4 newRgba = newSymbols[0].Paint.TextColor;
            Assert.That(newRgba.x, Is.EqualTo(oldRgba.x).Within(1e-6f),
                "a Constant text-color CHANGE must not move the extracted vertex colour (both stay white) — " +
                "only #996633 vs #2288DD differ, and both are Constant.");
            Assert.That(newRgba.y, Is.EqualTo(oldRgba.y).Within(1e-6f));
            Assert.That(newRgba.z, Is.EqualTo(oldRgba.z).Within(1e-6f));
            Assert.That(newSymbols[0].Paint.Opacity, Is.EqualTo(oldSymbols[0].Paint.Opacity).Within(1e-6f));
            // Its own assertion, not folded into the RGB clauses above — a RED names only the FIRST
            // assertion that fires (see RestyleSurvivorGateTests' alpha-only tooth for the differing case).
            Assert.That(newRgba.w, Is.EqualTo(oldRgba.w).Within(1e-6f),
                "a Constant text-color CHANGE must not move the extracted vertex ALPHA either.");
        }
    }
}
