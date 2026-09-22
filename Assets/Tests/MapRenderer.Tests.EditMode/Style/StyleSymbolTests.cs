// Style/StyleSymbolTests.cs — the symbol text-color carrier split, partial-survival restyle semantics,
// the restyle survivor gate, and the per-layer fade gate. Unity EditMode only — real Materials via
// MapMaterialSetTestUtil/MaterialFactory, real RenderLayerSet; not registered in core-tests.csproj.
//
// Carries both `using System;` (FileNotFoundException) and `using UnityEngine;` safely: no member here
// imports MapRenderer.Core.Expressions bare, so `Color` (UnityEngine's) stays unambiguous, and no member
// calls bare `Object.*`, so `System`'s presence never collides with it.
//
// Contents:
//   SymbolTextColorCarrierTests  — symbol text-color: constant rides the uniform, else bakes into the stream.
//   PartialSurvivalRestyleTests  — a partial restyle: surviving slots keep instance/Material, others tombstone.
//   RestyleSurvivorGateTests     — the survivor gate: RootMatches/LayerSurvives teeth.
//   LayerFadeGateTests           — the per-layer fade gate: eases across [minzoom,maxzoom), fade 1 is identity.

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
using MapRenderer.Core.Rendering;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Style
{

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolTextColorCarrierTests — symbol text-color: constant rides the uniform, else bakes into the stream
    // ───────────────────────────────────────────────────────────────────────────────────

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


    // ───────────────────────────────────────────────────────────────────────────────────
    // PartialSurvivalRestyleTests — a partial restyle: surviving slots keep instance/Material, others tombstone
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class PartialSurvivalRestyleTests
    {
        // Three fill layers, one source. "a" changes fill-color (still gate-eligible: paint-only); "c" is
        // byte-identical; "b" is removed in the new document.
        private const string ThreeFillOld = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""b"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""b"", ""paint"": { ""fill-color"": [""rgba"",0,255,0,1] } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";

        private const string ThreeFillRemovedB = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,128,0,1] } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";

        private static RenderLayerSet Build(string json)
        {
            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(json), 0.0, MapMaterialSetTestUtil.Load());
            return set;
        }

        /// <summary>The discriminating tooth. Three fill layers A, B, C; restyle to (A, C) with A's
        /// fill-color changed. A and C must survive with their instance/Material/slot unchanged; B's slot
        /// must become a tombstone with its Material destroyed exactly once. A REPLACED Material is out of
        /// this stage's scope, so "destroyed and vacated" against "preserved and easing" is the strongest
        /// statement available here.</summary>
        [Test]
        public void RemovalRestyle_KeepsSurvivorsAndRetiresOnlyTheRemovedLayer()
        {
            var oldStyle = StyleParser.Parse(ThreeFillOld);
            var newStyle = StyleParser.Parse(ThreeFillRemovedB);
            var set = Build(ThreeFillOld);

            IRenderLayer layerA = set[0];
            IRenderLayer layerB = set[1];
            IRenderLayer layerC = set[2];
            Material materialA = layerA.Material;
            Material materialB = layerB.Material;
            Material materialC = layerC.Material;

            Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                "A paint-only change on A plus a removal of B must take the in-place path.");

            Assert.AreEqual(3, set.Count, "the slot WIDTH never shrinks — B's slot becomes a tombstone, not a gap.");
            Assert.AreSame(layerA, set[0], "A's render-layer INSTANCE must be reference-equal — not rebuilt.");
            Assert.AreSame(layerC, set[2], "C's slot must be UNCHANGED — not compacted from 2 to 1.");
            Assert.AreSame(materialA, set[0].Material, "A's Material OBJECT must survive (identity, not just value).");
            Assert.AreSame(materialC, set[2].Material, "C's Material OBJECT must survive untouched.");

            Assert.IsInstanceOf<TombstoneRenderLayer>(set[1], "B's slot must hold a tombstone, not be compacted away.");
            Assert.IsTrue(materialB == null, // Unity fake-null: true only once Destroy actually ran.
                "B's Material must be destroyed — the tombstone swap disposes it exactly once.");

            Assert.Greater(layerA.TransitioningCount, 0, "A's fill-color changed — its uniform must be easing.");
            Assert.AreEqual(0, layerC.TransitioningCount, "C is byte-identical — nothing to ease.");
        }

        // Fill, symbol, fill restyled to (C, S, A): every slot survives, only declared order changes. The
        // symbol layer's ICON sits at LayerSubSlot.Base of its band, its TEXT at LayerSubSlot.Above.
        private const string FillSymbolFillOld = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""s"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""s"", ""layout"": { ""text-field"": ""{NAME}"" } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";
        private const string FillSymbolFillReordered = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } },
        { ""id"": ""s"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""s"", ""layout"": { ""text-field"": ""{NAME}"" } },
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } }
    ]
}";

        /// <summary>T2. A reorder with NO paint change: every slot/Mesh/Material identity stays, but every
        /// declared-order-derived <c>Material.renderQueue</c> moves to the NEW order, including a symbol
        /// layer's icon band.</summary>
        [Test]
        public void ReorderRestyle_KeepsEverySlotAndRewritesOnlyTheQueue()
        {
            var oldStyle = StyleParser.Parse(FillSymbolFillOld);
            var newStyle = StyleParser.Parse(FillSymbolFillReordered);
            var set = Build(FillSymbolFillOld);

            var instances = new IRenderLayer[] { set[0], set[1], set[2] };
            var materials = new Material[] { set[0].Material, set[1].Material, set[2].Material };
            var iconMaterial = ((SymbolRenderLayer)set[1]).WorldIconMaterial;

            Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                "a pure reorder with no paint change must take the in-place path.");

            for (int i = 0; i < 3; i++)
            {
                Assert.AreSame(instances[i], set[i], $"slot {i}'s instance must be unchanged.");
                Assert.AreSame(materials[i], set[i].Material, $"slot {i}'s Material identity must be unchanged.");
            }
            Assert.AreSame(iconMaterial, ((SymbolRenderLayer)set[1]).WorldIconMaterial, "the icon Material must survive too.");

            // Slots never move on a reorder (Build assigned a=0, s=1, c=2); queue is a function of the NEW
            // declared order (c, s, a), read off each layer's slot — so c is lowest now and a is highest.
            int queueA = set[0].Material.renderQueue;     // slot 0 = fill "a", now declared LAST
            int queueSIcon = iconMaterial.renderQueue;    // slot 1 = symbol "s" icon (Base), declared SECOND
            int queueSText = set[1].Material.renderQueue; // slot 1 = symbol "s" text (Above), declared SECOND
            int queueC = set[2].Material.renderQueue;     // slot 2 = fill "c", now declared FIRST

            Assert.Less(queueC, queueSIcon, "c (declared first) must draw below s's icon.");
            Assert.Less(queueSIcon, queueSText, "the icon (Base) must draw below its OWN layer's text (Above).");
            Assert.Less(queueSText, queueA, "s's text (declared second) must draw below a (declared last).");
        }

        // fill a(0), fill b(1), symbol s(2), fill c(3) -> (a, c): removes BOTH b and s. b is removed at a
        // LOWER slot than s on purpose — a too-late fence disposes b before reaching s, which clause 2 sees.
        private const string FillFillSymbolFillOld = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""b"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""b"", ""paint"": { ""fill-color"": [""rgba"",0,255,0,1] } },
        { ""id"": ""s"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""s"", ""layout"": { ""text-field"": ""{NAME}"" } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";

        private const string FillFillSymbolRemoved = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,0,0,1] } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";

        /// <summary>Removing a SYMBOL layer must REFUSE the in-place path (UMR-152 would let it survive).
        /// That arm skips the <c>_symbolRenderLayers</c> rebuild, so tombstoning a symbol slot leaves the
        /// list handing <c>SymbolPlacementSystem.Tick</c> materials <c>SymbolRenderLayer.Dispose</c> has
        /// destroyed. Clause 2 pins WHERE the fence sits: a fence below the mutation pass still returns
        /// false, but leaves the lower-slotted fill b disposed and tombstoned.</summary>
        [Test]
        public void SymbolRemovalRestyle_RefusesAndLeavesEveryLayerUntouched()
        {
            var oldStyle = StyleParser.Parse(FillFillSymbolFillOld);
            var newStyle = StyleParser.Parse(FillFillSymbolRemoved);
            var set = Build(FillFillSymbolFillOld);
            try
            {
                Assert.AreEqual(4, set.Count, "drive precondition: all four layers must have taken a slot.");
                var instances = new IRenderLayer[] { set[0], set[1], set[2], set[3] };
                var materials = new Material[] { set[0].Material, set[1].Material, set[2].Material, set[3].Material };
                var iconMaterial = ((SymbolRenderLayer)set[2]).WorldIconMaterial;

                Assert.IsFalse(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                    "a restyle that REMOVES a symbol layer must refuse and fall through to the full rebuild.");

                for (int i = 0; i < 4; i++)
                {
                    Assert.AreSame(instances[i], set[i],
                        $"slot {i}'s instance must be UNCHANGED on refusal — a fence below the mutation pass " +
                        "leaves an earlier removed slot already tombstoned.");
                    Assert.IsFalse(materials[i] == null, $"slot {i}'s Material must NOT have been destroyed on refusal.");
                }
                Assert.IsFalse(iconMaterial == null, "the symbol layer's icon Material must NOT have been destroyed on refusal.");
            }
            finally
            {
                set.Dispose();
            }
        }

        /// <summary>Not plan §7.1's T7 (that needs a mid-call observation hook <c>TryRestyleInPlace</c>
        /// exposes none of — no <c>CommitProbe</c>-equivalent exists inside a single synchronous call).
        /// This is the two-pass shape's OTHER half instead: a REFUSED restyle must leave every original
        /// Material and instance completely untouched — never disposed, never replaced.</summary>
        [Test]
        public void RefusedRestyle_LeavesEveryLayerAndMaterialInstanceUntouched()
        {
            // "b" changes filter — a MESH-AFFECTING change — so the whole restyle must refuse.
            const string MeshAffectingChange = @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""a"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""a"", ""paint"": { ""fill-color"": [""rgba"",255,128,0,1] } },
        { ""id"": ""c"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""c"", ""filter"": [""=="",""class"",""x""], ""paint"": { ""fill-color"": [""rgba"",0,0,255,1] } }
    ]
}";
            var oldStyle = StyleParser.Parse(ThreeFillOld);
            var newStyle = StyleParser.Parse(MeshAffectingChange);
            var set = Build(ThreeFillOld);
            var instances = new IRenderLayer[] { set[0], set[1], set[2] };
            var materials = new Material[] { set[0].Material, set[1].Material, set[2].Material };

            Assert.IsFalse(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                "an added-relative-to-removed pairing plus a filter change on the survivor must refuse.");

            for (int i = 0; i < 3; i++)
            {
                Assert.AreSame(instances[i], set[i], $"slot {i}'s instance must be UNCHANGED on refusal.");
                Assert.IsFalse(materials[i] == null, $"slot {i}'s Material must NOT have been destroyed on refusal.");
                Assert.AreSame(materials[i], set[i].Material, $"slot {i}'s Material identity must be UNCHANGED on refusal.");
            }
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // RestyleSurvivorGateTests — the survivor gate: RootMatches/LayerSurvives teeth
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class RestyleSurvivorGateTests
    {
        private const string FillTemplate = @"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [
        {{ ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"", {0}
          ""paint"": {{ {1} }} }}
    ]
}}";

        private static StyleDocument FillStyle(string paintJson, string extraLayerJson = "")
            => StyleParser.Parse(string.Format(FillTemplate, extraLayerJson, paintJson));

        // ── The draw-gate keys are freely transitionable ─────────────────────────────────────

        // A layer with NO paint member at all — the shape that takes Signature's FIRST early return, and
        // therefore the only shape that can see whether the draw-gate reduction runs before it.
        private static StyleDocument NoPaintStyle(string extraLayerJson) => StyleParser.Parse($@"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [ {{ ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0""{extraLayerJson} }} ]
}}");

        /// <summary>
        /// A restyle differing only in <c>minzoom</c>/<c>maxzoom</c>/<c>layout.visibility</c> survives in
        /// place, INCLUDING for a layer with no <c>paint</c> block — which takes an early return in
        /// <c>Signature</c> and is skipped entirely if the reduction is written after it. In
        /// <c>liberty.json</c> that is 7 layers, 6 of them bounded, so the wrong placement misses the
        /// majority of the affected set.
        /// </summary>
        /// <param name="oldExtra">The old layer's members after its id/type/source.</param>
        /// <param name="newExtra">The new layer's members, differing only as the case describes.</param>
        [TestCase(@", ""minzoom"": 5", @", ""minzoom"": 7", true,
                  TestName = "a_MinzoomOnly_NoPaint_Survives")]
        [TestCase(@", ""maxzoom"": 12", @", ""maxzoom"": 14", true,
                  TestName = "a2_MaxzoomOnly_NoPaint_Survives")]
        [TestCase(@", ""minzoom"": 5, ""filter"": [""=="", ""class"", ""a""]",
                  @", ""minzoom"": 7, ""filter"": [""=="", ""class"", ""b""]", false,
                  TestName = "c_MinzoomPlusFilter_NoPaint_Refuses")]
        [TestCase(@", ""maxzoom"": 12, ""filter"": [""=="", ""class"", ""a""]",
                  @", ""maxzoom"": 14, ""filter"": [""=="", ""class"", ""b""]", false,
                  TestName = "c2_MaxzoomPlusFilter_NoPaint_Refuses")]
        [TestCase(@"", @", ""layout"": {""visibility"": ""none""}", true,
                  TestName = "d_LayoutAppearsCarryingOnlyVisibility_Survives")]
        [TestCase(@"", @", ""layout"": {""line-cap"": ""round""}", false,
                  TestName = "e_LayoutAppearsCarryingAnotherKey_Refuses")]
        public void DrawGateRestyle_SurvivesTheGate_EvenWithNoPaintBlock(
            string oldExtra, string newExtra, bool expectedSurvives)
        {
            StyleDocument oldStyle = NoPaintStyle(oldExtra), newStyle = NoPaintStyle(newExtra);
            Assert.AreEqual(expectedSurvives, WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                expectedSurvives
                    ? "minzoom/maxzoom/layout.visibility are read per frame off IRenderLayer.StyleLayer, "
                      + "which Restyle moves forward, and no mesh or PreparedKey depends on them — so this "
                      + "pair must survive in place. A refusal means the reduction never ran: written after "
                      + "Signature's early returns it is skipped for exactly this no-paint shape."
                    : "this pair differs in more than the three draw-gate keys, so the gate must still "
                      + "refuse. Accepting it means the reduction strips too much — dropping `layout` "
                      + "wholesale rather than only when stripping `visibility` empties it would make EVERY "
                      + "layout change survivable, a far worse hole than the one being closed.");
        }

        /// <summary>The same <c>minzoom</c>-only change WITH a paint block — the control that isolates the
        /// no-paint case above to <c>Signature</c>'s early-return path, since this one passes under either
        /// placement of the reduction.</summary>
        [Test]
        public void DrawGateRestyle_WithAPaintBlock_Survives()
        {
            var oldStyle = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1]", @"""minzoom"": 5,");
            var newStyle = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1]", @"""minzoom"": 7,");
            Assert.IsTrue(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a minzoom-only change on a layer that DOES carry paint must survive — this passes under "
                + "either placement of the reduction, which is what makes it the control.");
        }

        // ── 10. First style load never transitions ───────────────────────────────────────────

        [Test]
        public void FirstStyleLoad_NeverTransitions()
        {
            var style = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1]");
            var set = new RenderLayerSet();
            set.Build(style, 0.0, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(0, set.TransitioningCount, "the very first build must arm no transition.");
            Color pushed = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);
            Assert.AreEqual(new Color(0.4f, 0.6f, 0.8f, 1f).r, pushed.r, 1e-4f,
                "frame 0 must already read the style's own value — nothing to ease from.");
        }

        // ── 11. Default duration is 300ms, from the API, never the style ─────────────────────

        [Test]
        public void DefaultDuration_Is300ms_FromTheApi_NotTheStyle()
        {
            var oldStyle = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1]");
            var newStyle = FillStyle(@"""fill-color"": [""rgba"",204,102,51,1]");
            var set = new RenderLayerSet();
            set.Build(oldStyle, 0.0, MapMaterialSetTestUtil.Load());

            Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                "drive precondition: a paint-only, gate-eligible restyle must take the in-place path.");

            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.15));
            Assert.Greater(set.TransitioningCount, 0, "mid-way through the default duration, still easing.");

            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.30));
            Assert.AreEqual(0, set.TransitioningCount, "settled at exactly the default duration (300ms).");

            // Second clause (decision 0a): a style document carrying a root `transition` block AND a
            // `fill-color-transition` key must produce IDENTICAL timing, because nothing parses either —
            // MapView.StyleTransition is the only source. The extra keys sit on BOTH sides (not just the
            // new style) so the gate's own "unknown root/paint key" fail-closed rule (correct, unrelated
            // to this clause) does not refuse before the timing question is even reached. There is no
            // parse site to point a RED injection at: this clause is a guard against a future regression
            // (someone adding one), not a property provable today (see plan §3 tooth 11 and §4).
            string WithTransitionHints(string fillColorRgba) => @"{
    ""version"": 8, ""name"": ""T"", ""transition"": { ""duration"": 5000, ""delay"": 5000 },
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"",
          ""paint"": { ""fill-color"": " + fillColorRgba + @",
                       ""fill-color-transition"": { ""duration"": 5000 } } }
    ]
}";
            var oldStyleWithHints = StyleParser.Parse(WithTransitionHints(@"[""rgba"",102,153,204,1]"));
            var newStyleWithHints = StyleParser.Parse(WithTransitionHints(@"[""rgba"",204,102,51,1]"));
            var set2 = new RenderLayerSet();
            set2.Build(oldStyleWithHints, 0.0, MapMaterialSetTestUtil.Load());
            Assert.IsTrue(set2.TryRestyleInPlace(oldStyleWithHints, newStyleWithHints, StyleTransition.Default, 0.0),
                "drive precondition: the extra keys are identical on both sides, so the gate must still accept.");
            set2.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.30));
            Assert.AreEqual(0, set2.TransitioningCount,
                "a root/per-property transition block in the STYLE must be inert — timing comes only from " +
                "MapView.StyleTransition (decision 0a); a 5s duration in the JSON must not extend this " +
                "300ms settle.");
        }

        // ── 16. A mesh-affecting restyle takes the rebuild path ──────────────────────────────

        private const string PaintOnly = @"""fill-color"": [""rgba"",102,153,204,1]";

        [Test]
        public void MeshAffectingRestyle_Filter_TakesTheRebuildPath()
        {
            var oldStyle = FillStyle(PaintOnly, @"""filter"": [""=="", ""class"", ""a""],");
            var newStyle = FillStyle(PaintOnly, @"""filter"": [""=="", ""class"", ""b""],");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a changed filter must move the layer signature and refuse the gate.");
        }

        [Test]
        public void MeshAffectingRestyle_SourceLayer_TakesTheRebuildPath()
        {
            var oldStyle = StyleParser.Parse(@"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [ { ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"",
        ""paint"": { ""fill-color"": [""rgba"",102,153,204,1] } } ]
}");
            var newStyle = StyleParser.Parse(@"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [ { ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill1"",
        ""paint"": { ""fill-color"": [""rgba"",102,153,204,1] } } ]
}");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a changed source-layer must move the layer signature and refuse the gate.");
        }

        [Test]
        public void MeshAffectingRestyle_FillAntialias_TakesTheRebuildPath()
        {
            var oldStyle = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1], ""fill-antialias"": true");
            var newStyle = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1], ""fill-antialias"": false");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "fill-antialias selects a meshing path (SSOT §1.1) — it must stay inside the signature.");
        }

        // ── 17. A data-driven paint change takes the rebuild path ────────────────────────────

        [Test]
        public void DataDrivenPaintChange_TakesTheRebuildPath()
        {
            var oldStyle = FillStyle(@"""fill-color"": [""get"",""c1""]");
            var newStyle = FillStyle(@"""fill-color"": [""get"",""c2""]");

            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "two DIFFERENT data-driven fill-colors both skip the bind — without clause (c) the gate " +
                "would accept and every tile would keep the previous style's baked colour forever.");
        }

        // ── UMR-147: symbol layers survive and ease across a restyle (T1, T2a–e) ─────────────

        private const string SymbolTemplate = @"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [
        {{ ""id"": ""label0"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""label0"",
          ""layout"": {{ ""text-field"": ""{{NAME}}"" }},
          ""paint"": {{ {0} }} }}
    ]
}}";

        private static StyleDocument SymbolStyleDoc(string paintJson)
            => StyleParser.Parse(string.Format(SymbolTemplate, paintJson));

        /// <summary>
        /// <b>T1.</b> The real shipped liberty → liberty-night pair must survive the gate once text-color
        /// and text-halo-color are transitionable — this is the stage's actual deliverable (§0). RED-verify
        /// (two injections, either alone leaves the other's RED unproven): remove text-halo-color from
        /// TransitionablePaintKeys (18 refuse); separately remove text-color (20 refuse).
        /// </summary>
        [Test]
        public void DayNightPair_SurvivesTheRestyleGate()
        {
            var day   = SymbolTestFixtures.LibertyDoc();
            var night = SymbolTestFixtures.LibertyNightDoc();
            Assert.IsTrue(WholeDocumentGate.AllLayersSurvive(day, night),
                "the real shipped liberty -> liberty-night pair, differing only in symbol text-color and " +
                "text-halo-color, must survive the gate — this is the stage's deliverable.");
        }

        /// <summary><b>T2a.</b> A synthetic pair differing only in a Constant text-color survives — T2b's
        /// non-vacuity control (same fixture shape, opposite verdict). RED-verify: remove text-color from
        /// TransitionablePaintKeys.</summary>
        [Test]
        public void SymbolLayer_ConstantTextColorChange_Survives()
        {
            var oldStyle = SymbolStyleDoc(@"""text-color"": ""#996633""");
            var newStyle = SymbolStyleDoc(@"""text-color"": ""#2288DD""");
            Assert.IsTrue(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a Constant text-color change must survive — the applier re-binds the uniform on Restyle.");
        }

        /// <summary><b>T2b.</b> A Zoom-kind text-color change — different stop outputs on each side — must
        /// be REFUSED: the in-place path never re-bakes the vertex COLOR stream a non-Constant text-color
        /// lives in. RED-verify: change the gate's text-color arm from RidesUniform(kind) to
        /// !DependsOnFeature(kind).</summary>
        [Test]
        public void SymbolLayer_ZoomKindTextColorChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(
                @"""text-color"": [""interpolate"",[""linear""],[""zoom""],5,""#000000"",15,""#996633""]");
            var newStyle = SymbolStyleDoc(
                @"""text-color"": [""interpolate"",[""linear""],[""zoom""],5,""#000000"",15,""#2288DD""]");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "freeing text-color above Constant requires the in-place path to re-bake the vertex COLOR " +
                "stream — MapView's SymbolSubsystem.SetStyle skip and _symbolStyleLayers assume it never has to.");
        }

        /// <summary><b>T2c.</b> A Zoom-kind text-halo-color change — different stops on each side — must
        /// be REFUSED, on exactly the same grounds as T2b's text-color. This arm asserted the OPPOSITE while
        /// the whole halo trio bound to per-layer uniforms; once the halo became GEOMETRY, a non-Constant
        /// text-halo-color bakes into the vertex COLOR stream of a second glyph run, which the in-place path
        /// never re-bakes. RED-verify: widen the gate's symbol-colour arm from RidesUniform(kind) to
        /// !DependsOnFeature(kind).</summary>
        [Test]
        public void SymbolLayer_ZoomKindHaloColorChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(
                @"""text-halo-color"": [""interpolate"",[""linear""],[""zoom""],5,""#000000"",15,""#808080""]");
            var newStyle = SymbolStyleDoc(
                @"""text-halo-color"": [""interpolate"",[""linear""],[""zoom""],5,""#000000"",15,""#4099C0""]");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "freeing text-halo-color above Constant requires the in-place path to re-bake the halo run's " +
                "vertex COLOR stream — the same carrier argument that refuses a Zoom-kind text-color (T2b).");
        }

        /// <summary><b>T2c2.</b> The Constant control for T2c — same fixture shape, opposite verdict, so
        /// T2c's refusal is not vacuous. RED-verify: remove text-halo-color from
        /// TransitionablePaintKeys.</summary>
        [Test]
        public void SymbolLayer_ConstantHaloColorChange_Survives()
        {
            var oldStyle = SymbolStyleDoc(@"""text-halo-color"": ""#808080""");
            var newStyle = SymbolStyleDoc(@"""text-halo-color"": ""#4099C0""");
            Assert.IsTrue(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a Constant text-halo-color change must survive — it rides _HaloColor, which Restyle re-binds.");
        }

        /// <summary><b>T2d.</b> A data-driven text-halo-color change must be REFUSED — the gate guard and
        /// BindTextPaint's !DependsOnFeature guard are exact complements. RED-verify: drop the
        /// !DependsOnFeature half of the default arm.</summary>
        [Test]
        public void SymbolLayer_DataDrivenHaloColorChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(@"""text-halo-color"": [""get"",""hc1""]");
            var newStyle = SymbolStyleDoc(@"""text-halo-color"": [""get"",""hc2""]");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "two DIFFERENT data-driven text-halo-colors both skip the uniform bind — without the guard " +
                "every symbol layer would keep the previous style's halo forever.");
        }

        /// <summary><b>T2e.</b> A text-halo-width change — a key deliberately NOT freed — must still
        /// refuse, guarding against a future "make the gate pass" by adding keys.</summary>
        [Test]
        public void SymbolLayer_HaloWidthChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(@"""text-halo-width"": 1");
            var newStyle = SymbolStyleDoc(@"""text-halo-width"": 3");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "text-halo-width is not a gate key — a change to it must move the layer signature.");
        }

        /// <summary>
        /// A Constant <c>text-color</c> pair differing ONLY in alpha must be REFUSED — only RGB rides the
        /// <c>_TextColor</c> uniform; alpha travels by the vertex COLOR stream
        /// (<c>SymbolFeatureExtractor.EvaluatePaint</c>'s <c>textRgba.w</c>), which the in-place path never
        /// re-bakes. RED-verify: drop the <c>ConstantAlphaMatches</c> conjunct from
        /// <c>SurvivingLayerGate.FreelyTransitionableKeys</c>.
        /// </summary>
        /// <summary>
        /// The halo twin of <see cref="SymbolLayer_AlphaOnlyTextColorChange_IsRefused"/>. text-halo-color's
        /// alpha rides the opacity stream AND decides whether a halo run is emitted at all
        /// (<c>WorldSymbolRenderer.Emit</c>), so an alpha-only change must refuse even though both sides are
        /// Constant. RED-verify: restrict <c>ConstantAlphaMatches</c> back to text-color only.
        /// </summary>
        [Test]
        public void SymbolLayer_AlphaOnlyHaloColorChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(@"""text-halo-color"": ""#808080""");
            var newStyle = SymbolStyleDoc(@"""text-halo-color"": [""rgba"",128,128,128,0.5]");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "an alpha-only text-halo-color change must refuse — the uniform is RGB-only, and the alpha " +
                "also gates whether the halo run exists.");
        }

        [Test]
        public void SymbolLayer_AlphaOnlyTextColorChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(@"""text-color"": ""#000000""");
            var newStyle = SymbolStyleDoc(@"""text-color"": [""rgba"",0,0,0,0.5]");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "an alpha-only text-color change must refuse — the two colours differ ONLY in the " +
                "component the in-place path cannot move (the uniform is RGB-only).");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // LayerFadeGateTests — the per-layer fade gate: eases across [minzoom,maxzoom), fade 1 is identity
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The per-layer fade gate: a layer draws inside its <c>[minzoom, maxzoom)</c> and is gated out
    /// elsewhere, the gate eases rather than snapping, and fade 1 is the identity.
    ///
    /// <para>Reads the quantity itself — <c>_Opacity</c> off the layer's Material — which nothing in these
    /// tests writes. The C# half of the gate; the RENDERED half is
    /// <c>FillExtrusionDrawGateTests</c> and they join at <c>PaintsSomething</c>.</para>
    /// </summary>
    [TestFixture]
    public class LayerFadeGateTests
    {
        private const double UnboundedZoom = 3.0;

        /// <summary>Authored below 1 on purpose: at authored 1 the product authored x fade is
        /// invisible, because 1 * f(p) == f(p) for every candidate f.</summary>
        private const float AuthoredOpacity = 0.5f;

        // Bounds are declared once and every asserted zoom is derived from them, so no literal sits next to
        // a boundary where an off-by-one-zoom-level fix would still read correct.
        private const double BoundedMin = 13.0;
        private const double BoundedMax = 14.0;
        private const double LowerOnly  = 5.0;

        private static StyleDocument Style(params string[] layers)
            => StyleParser.Parse($@"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [ {string.Join(",", layers)} ]
}}");

        /// <summary>"viewport" as <c>fill-translate-anchor</c> encodes it — 1, the value "map" (0) cannot
        /// separate from any scaling of itself.</summary>
        private const float ViewportAnchor = 1f;

        private static string FillLayer(string id, string extra = "", string extraPaint = "")
            => $@"{{ ""id"": ""{id}"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""{id}"", {extra}
        ""paint"": {{ ""fill-color"": [""rgba"",102,153,204,1], ""fill-opacity"": {AuthoredOpacity}{extraPaint} }} }}";

        private static string ExtrusionLayer(string id, string extra = "")
            => $@"{{ ""id"": ""{id}"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""{id}"", {extra}
        ""paint"": {{ ""fill-extrusion-color"": [""rgba"",204,102,51,1],
                      ""fill-extrusion-opacity"": {AuthoredOpacity} }} }}";

        private static float OpacityOf(RenderLayerSet set, int i)
            => set[i].Material.GetFloat(ShaderProperties.PropertyId.Opacity);

        private static float AnchorOf(RenderLayerSet set, int i)
            => set[i].Material.GetFloat(ShaderProperties.Fill.PropertyId.FillTranslateAnchor);

        /// <summary>Drive one frame and settle any ease by pumping the clock well past the duration.</summary>
        private static void ApplySettled(RenderLayerSet set, double zoom)
        {
            set.ApplyZoom(new StyleFrameInputs(zoom, 1.0, 0.0));
            set.ApplyZoom(new StyleFrameInputs(zoom, 1.0, StyleTransition.Default.DurationSeconds * 4.0));
        }

        /// <summary>
        /// A layer's pushed <c>_Opacity</c> is its authored value inside <c>[minzoom, maxzoom)</c> and 0
        /// outside; an unbounded layer keeps its authored value at every zoom.
        ///
        /// <para>The <c>maxzoom</c> row is the discriminating one: <c>maxzoom</c> is EXCLUSIVE, so a fix
        /// that copies <c>SourceRegistry.AdmitsZoom</c>'s inclusive upper bound — a different quantity — is
        /// wrong by one zoom level and visible only there.</para>
        /// </summary>
        [Test]
        public void BoundedLayer_ReadsZeroOpacityOutsideItsZoomRange()
        {
            var set = new RenderLayerSet();
            set.Build(Style(
                    FillLayer("unbounded"),
                    FillLayer("lower", $@"""minzoom"": {LowerOnly},"),
                    FillLayer("both", $@"""minzoom"": {BoundedMin}, ""maxzoom"": {BoundedMax},")),
                UnboundedZoom, MapMaterialSetTestUtil.Load());

            const int unbounded = 0, lower = 1, both = 2;
            double insideBoth = (BoundedMin + BoundedMax) / 2.0;

            ApplySettled(set, UnboundedZoom);                       // below every bound
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, unbounded), 1e-6f,
                "an unbounded layer draws at every zoom — a gate that fires unconditionally reds here.");
            Assert.AreEqual(0f, OpacityOf(set, lower), 1e-6f, $"z{UnboundedZoom} is below minzoom {LowerOnly}.");
            Assert.AreEqual(0f, OpacityOf(set, both), 1e-6f, $"z{UnboundedZoom} is below minzoom {BoundedMin}.");

            ApplySettled(set, LowerOnly + 1.0);                     // inside `lower`, below `both`
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, lower), 1e-6f,
                $"z{LowerOnly + 1.0} is at or above minzoom {LowerOnly}, so the layer draws its authored value.");

            ApplySettled(set, insideBoth);                          // strictly inside [min, max)
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, both), 1e-6f,
                $"z{insideBoth} is inside [{BoundedMin}, {BoundedMax}).");

            ApplySettled(set, BoundedMax);                          // EXACTLY maxzoom — exclusive
            Assert.AreEqual(0f, OpacityOf(set, both), 1e-6f,
                $"maxzoom is EXCLUSIVE, so a layer with maxzoom {BoundedMax} must be gated out AT z{BoundedMax}. "
                + "Reading its authored value here means the bound was implemented as inclusive — the shape "
                + "SourceRegistry.AdmitsZoom uses for a DIFFERENT quantity (a source's data range).");
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, unbounded), 1e-6f,
                "the unbounded layer is unaffected at every zoom asserted above.");
        }

        /// <summary>
        /// A hidden layer is never BUILT, whatever zoom the set is built at — including one strictly inside
        /// its declared range, the discriminating value an implementation that consults only the zoom
        /// bounds passes everywhere else.
        /// </summary>
        [Test]
        public void HiddenLayer_IsNeverBuilt_AtAnyZoomIncludingInsideItsRange()
        {
            foreach (double buildZoom in new[] { 4.0, 10.0, 21.0 })
            {
                var set = new RenderLayerSet();
                set.Build(Style(
                        FillLayer("sibling"),
                        FillLayer("hidden",
                            @"""minzoom"": 5, ""maxzoom"": 20, ""layout"": {""visibility"": ""none""},")),
                    buildZoom, MapMaterialSetTestUtil.Load());

                // The sibling is what makes the count discriminating: 1 vs 2, not 0 vs 1.
                Assert.AreEqual(1, set.Count,
                    $"built at z{buildZoom}, only the sibling may take a slot — a visibility:none layer "
                    + "owns no slot, no material and no backend registration. z10 is INSIDE [5,20) and is "
                    + "the row that separates the visibility flag from the zoom bounds: a build that "
                    + "consults only the bounds keeps the hidden layer there.");
                Assert.AreEqual("sibling", set[0].StyleLayer.Id);
            }
        }

        /// <summary>
        /// Crossing a bound moves <c>_Opacity</c> continuously and recovers the authored value on the way
        /// back in. The only observer of <c>authored x fade</c> at <c>0 &lt; p &lt; 1</c>, where the
        /// composition is distinguishable from <c>authored x p^2</c> and friends.
        /// </summary>
        [Test]
        public void BoundaryCrossing_EasesTheOpacity_OutAndBackIn()
        {
            var set = new RenderLayerSet();
            set.Build(Style(FillLayer("bounded", $@"""minzoom"": {LowerOnly},")),
                LowerOnly + 1.0, MapMaterialSetTestUtil.Load());

            double d = StyleTransition.Default.DurationSeconds;
            double inside = LowerOnly + 1.0, outside = LowerOnly - 1.0;

            set.ApplyZoom(new StyleFrameInputs(inside, 1.0, 0.0));
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, 0), 1e-6f, "precondition: settled and in range.");

            // ── OUT leg: monotone decreasing, strictly interior, exact at the midpoint ──
            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, 0.0));   // arm the fade at t = 0
            float prev = OpacityOf(set, 0);
            bool sawInterior = false;
            for (int step = 1; step <= 4; step++)
            {
                set.ApplyZoom(new StyleFrameInputs(outside, 1.0, d * step / 5.0));
                float now = OpacityOf(set, 0);
                Assert.LessOrEqual(now, prev + 1e-6f, "the OUT leg must not increase.");
                if (now > 1e-6f && now < AuthoredOpacity - 1e-6f) sawInterior = true;
                prev = now;
            }
            Assert.IsTrue(sawInterior,
                "no intermediate frame landed strictly between 0 and the authored value — the gate SNAPPED "
                + "instead of easing.");

            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, d * 4.0));
            Assert.AreEqual(0f, OpacityOf(set, 0), 1e-6f, "the OUT leg must settle exactly on 0.");

            // ── Return leg: the midpoint pins the COMPOSITION, not just betweenness ──
            double armed = d * 4.0;
            set.ApplyZoom(new StyleFrameInputs(inside, 1.0, armed));
            set.ApplyZoom(new StyleFrameInputs(inside, 1.0, armed + d / 2.0));
            Assert.AreEqual(AuthoredOpacity * 0.5f, OpacityOf(set, 0), 1e-5f,
                "at half the duration the read must be authored x 0.5 = 0.25. smoothstep(0,1,0.5) is exactly "
                + "0.5, so this pins that fade appears EXACTLY ONCE in the product: a squared or "
                + "otherwise compounded fade reads 0.125 here while still passing every betweenness and "
                + "endpoint clause.");

            set.ApplyZoom(new StyleFrameInputs(inside, 1.0, armed + d * 4.0));
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, 0), 1e-6f,
                "coming back into range must recover the AUTHORED value exactly, not an eased approximation.");
        }

        /// <summary>
        /// A layer mid-fade still paints something. <c>PaintsSomething</c> — the predicate the
        /// per-slot draw gate reads (<c>ITileRenderBackend.SetLayerVisible</c>) — turns false only once the
        /// product of fade and authored opacity drops below one 8-bit step. A gate that fired when the
        /// fade STARTED would retire the draw while there was still something to blend.
        /// </summary>
        [Test]
        public void FadingLayer_IsNotAbsentUntilTheFadeSettles()
        {
            var set = new RenderLayerSet();
            set.Build(Style(FillLayer("bounded", $@"""minzoom"": {LowerOnly},")),
                LowerOnly + 1.0, MapMaterialSetTestUtil.Load());

            double d = StyleTransition.Default.DurationSeconds;
            double inside = LowerOnly + 1.0, outside = LowerOnly - 1.0;
            var fadeable = (IFadeableRenderLayer)set[0];

            set.ApplyZoom(new StyleFrameInputs(inside, 1.0, 0.0));
            Assert.IsTrue(fadeable.PaintsSomething,
                "precondition: a layer inside its range, at a showable opacity, must be drawn.");

            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, 0.0));     // arm the fade OUT
            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, d / 2.0)); // halfway through it

            float midFade = OpacityOf(set, 0);
            Assert.Greater(midFade, 0f,
                "fixture: the halfway frame must read strictly above 0, or there is no fade to measure.");
            Assert.Less(midFade, AuthoredOpacity,
                "fixture: the halfway frame must read strictly below the authored value, or the gate " +
                "snapped and 'mid-fade' is not the state under test.");
            Assert.IsTrue(fadeable.PaintsSomething,
                $"a layer at fade strictly between 0 and 1 (_Opacity {midFade}) must still be DRAWN. " +
                "Reading false here gates the draw out mid-transition, so the fade has nothing to blend and " +
                "the layer disappears the instant it leaves its zoom range.");

            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, d * 4.0));
            Assert.AreEqual(0f, OpacityOf(set, 0), 1e-6f, "fixture: the fade must have settled on 0.");
            Assert.IsFalse(fadeable.PaintsSomething,
                "once the fade has settled on 0 the layer must be gated out — otherwise the draw gate " +
                "never fires and every always-off layer keeps paying for its vertex stage and draw call.");
        }

        /// <summary>
        /// The gate reads fade TIMES the authored opacity, so a zoom-interpolated opacity that ramps
        /// through zero gates the layer out at the zooms where it shows nothing — with no zoom bounds
        /// anywhere, which is what makes the authored term the only thing that can have moved the predicate.
        /// </summary>
        [Test]
        public void ZoomInterpolatedOpacity_GatesTheLayerWhereItPaintsNothing()
        {
            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(@"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [ { ""id"": ""ramp"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""ramp"",
        ""paint"": { ""fill-color"": [""rgba"",102,153,204,1],
                     ""fill-opacity"": [""interpolate"",[""linear""],[""zoom""],5,0,15,1] } } ]
}"), 15.0, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(1, set.Count,
                "fixture: a ZOOM-dependent opacity must still be BUILT — only a CONSTANT transparent one " +
                "is refused at construction, and a refused layer would make the reads below vacuous.");
            var fadeable = (IFadeableRenderLayer)set[0];

            ApplySettled(set, 15.0);
            Assert.IsTrue(fadeable.PaintsSomething,
                $"at z15 the ramp reads its full authored opacity ({OpacityOf(set, 0)}), so the layer must " +
                "be drawn.");

            ApplySettled(set, 5.0);
            Assert.AreEqual(0f, OpacityOf(set, 0), 1e-6f,
                "fixture: the ramp must reach 0 at z5, or there is nothing for the gate to catch.");
            Assert.IsFalse(fadeable.PaintsSomething,
                "at z5 the layer paints nothing an 8-bit framebuffer can show, so its draw must not be " +
                "submitted. Reading true here means the gate looks at fade alone — the layer is " +
                "inside its (unbounded) zoom range, so fade is 1 and only the authored term differs.");
        }

        /// <summary>
        /// A feature-dependent opacity can never gate the layer out. No per-layer scalar represents it, so
        /// <c>MaterialFactory</c> binds a constant 1 and the layer stays submitted whatever its features
        /// carry. Without that the gate would read an unevaluable property and could retire a layer whose
        /// features are fully opaque.
        /// </summary>
        [Test]
        public void FeatureDependentOpacity_NeverGatesTheLayerOut()
        {
            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(@"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [ { ""id"": ""dd"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""dd"",
        ""paint"": { ""fill-color"": [""rgba"",102,153,204,1],
                     ""fill-opacity"": [""get"", ""op""] } } ]
}"), UnboundedZoom, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(1, set.Count,
                "fixture: a data-driven opacity must still be BUILT — the per-feature values are baked " +
                "into vertex alpha, and a refused layer would make the read below vacuous.");

            ApplySettled(set, UnboundedZoom);
            Assert.IsTrue(((IFadeableRenderLayer)set[0]).PaintsSomething,
                "a layer whose opacity varies PER FEATURE must always be submitted: one per-layer scalar " +
                "cannot stand for it, so the gate has to fail safe. Reading false here retires a whole " +
                "layer on a value that does not describe it.");
        }

        /// <summary>
        /// A layer built at a zoom where it is out of range reads 0 on its FIRST frame — it does not ease
        /// down from the authored value, which would flash the layer for the whole transition on load.
        /// </summary>
        [Test]
        public void OutOfRangeLayer_StartsHidden_NoFirstFrameFlash()
        {
            var set = new RenderLayerSet();
            set.Build(Style(FillLayer("in-range"), FillLayer("out-of-range", $@"""minzoom"": {BoundedMin},")),
                UnboundedZoom, MapMaterialSetTestUtil.Load());

            // Read BEFORE any ApplyZoom pumps the clock — this is the first-frame state.
            Assert.AreEqual(0f, OpacityOf(set, 1), 1e-6f,
                $"a layer with minzoom {BoundedMin} built at z{UnboundedZoom} must be seeded hidden. Reading "
                + "its authored value here means it starts visible and fades out — a flash on every load.");
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, 0), 1e-6f,
                "its in-range twin, built in the same set, carries the authored value — so the row above is "
                + "a real gate and not a material that was never written.");
        }

        /// <summary>
        /// At fade 1 the authored value reaches the material untouched. Pins that fade is the
        /// IDENTITY at 1 — it does not separate authored x p from authored x p^2, which are the same number
        /// there; that is <see cref="BoundaryCrossing_EasesTheOpacity_OutAndBackIn"/>'s midpoint clause.
        /// </summary>
        [Test]
        public void FadeOne_LeavesTheAuthoredOpacityByteIdentical()
        {
            var set = new RenderLayerSet();
            set.Build(Style(FillLayer("unbounded")), UnboundedZoom, MapMaterialSetTestUtil.Load());
            ApplySettled(set, UnboundedZoom);

            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, 0), 1e-6f,
                $"an unbounded layer authoring fill-opacity {AuthoredOpacity} must push exactly that. This is "
                + "the only opacity row in the suite with a non-1 authored value, so a defect that is the "
                + "identity at 1 but not elsewhere — a clobbered LastPushed, a hard-coded 1, a fade that "
                + "is not the identity — is invisible everywhere else.");
        }

        /// <summary>
        /// Fill-extrusion gates INSTANTLY: ONE frame outside the range reads 0, with the gate transition
        /// left at production's default.
        ///
        /// <para>The only observer of <c>FillExtrusionRenderLayer.SetFade</c> substituting
        /// <c>StyleTransition.Instant</c> for the transition it is handed. Pass the argument through and
        /// this frame lands at <c>elapsed == 0</c>, where the ease returns false and the authored value
        /// survives — so it reads 0.5. That is not a cosmetic difference: the elevated contract blends
        /// One/Zero, so alpha is DISCARDED and an intermediate fade renders a fully solid building
        /// for the whole fade.</para>
        /// </summary>
        [Test]
        public void FillExtrusionLayer_GatesInstantly_NotOverTheTransition()
        {
            var set = new RenderLayerSet();
            set.Build(Style(ExtrusionLayer("ext", $@"""minzoom"": {BoundedMin}, ""maxzoom"": {BoundedMax},")),
                (BoundedMin + BoundedMax) / 2.0, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, 0), 1e-6f,
                "precondition: built inside [min, max), so the authored opacity is on the material and the "
                + "layer was not skipped for a missing fill-extrusion base.");

            set.ApplyZoom(new StyleFrameInputs(BoundedMax, 1.0, 0.0));   // ONE frame, outside the range

            Assert.AreEqual(0f, OpacityOf(set, 0), 1e-6f,
                $"a fill-extrusion layer leaving [{BoundedMin}, {BoundedMax}) must reach 0 on the FIRST "
                + "frame. Reading the authored value means the gate is easing this kind — which paints a "
                + "solid building for the transition, because One/Zero blending discards alpha.");
        }

        /// <summary>
        /// A layer mid-fade scales ONLY its opacity. <c>fill-translate-anchor</c> — a second float binding
        /// on the same material — must still read its authored <c>viewport</c> while fade sits at 0.5.
        ///
        /// <para>The only observer of the applier's <c>ScaledByFade</c> flag. Without it every settled float
        /// on a fading layer is re-pushed multiplied by fade: the anchor drifts from viewport toward map
        /// for the length of the fade, and a building's height shrinks toward the ground with it. The
        /// device-pixel tests cannot see this — <c>_Width</c> and its family live in a separate binding list
        /// whose push loop carries no fade term at all.</para>
        /// </summary>
        [Test]
        public void FadingLayer_ScalesOnlyItsOpacity_NotItsOtherFloats()
        {
            var set = new RenderLayerSet();
            set.Build(Style(FillLayer("bounded", $@"""minzoom"": {LowerOnly},",
                    @", ""fill-translate-anchor"": ""viewport""")),
                LowerOnly + 1.0, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(ViewportAnchor, AnchorOf(set, 0), 1e-6f,
                "precondition: the authored anchor reached the material at bind time.");

            double d = StyleTransition.Default.DurationSeconds, outside = LowerOnly - 1.0;
            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, 0.0));        // arm the fade at t = 0
            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, d / 2.0));    // half way through it

            Assert.AreEqual(AuthoredOpacity * 0.5f, OpacityOf(set, 0), 1e-5f,
                "precondition: fade must be strictly interior on this frame, or the clause below is "
                + "vacuous — it would be asserting the anchor against a fade of exactly 1.");

            Assert.AreEqual(ViewportAnchor, AnchorOf(set, 0), 1e-6f,
                "fill-translate-anchor is not an opacity, so the fade must leave it alone. Reading "
                + $"{ViewportAnchor * 0.5f} here means fade multiplies every float the layer pushes.");
        }
    }
}
