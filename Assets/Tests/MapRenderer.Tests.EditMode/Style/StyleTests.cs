// Style/StyleTests.cs — style-document parsing and paint/layout property evaluation for the fast (engine-
// free) lane: TileJSON, StyleProperty<T>'s parse/classify/evaluate/GC-allocation contract, the eager-parse
// property model, and per-kind paint/layout parsing (fill/fill-pattern/fill-extrusion/line/background/
// symbol, plus the symbol text-field and icon-image token resolvers). Compiled verbatim by both the Unity
// EditMode runner and the fast dotnet test project (Tools/core-tests/). Do NOT add any UnityEngine,
// MeshBuilder, NativeArray, or MonoBehaviour references.
//
// Contents:
//   TileJsonTests               — TileJSON parses into the typed model; SourceResolver fills a SourceDefinition.
//   StylePropertyTests          — StyleProperty<T>: parse, classify, evaluate (uniform + bake), GC-allocation gate.
//   StyleLayerEagerParseTests   — the eight style property types parse eagerly via a static Parse factory.
//   FillLayoutTests             — Fill.LayoutProperties: parsing fill-sort-key.
//   FillPatternTests            — Fill.FillPattern: resolving a fill-pattern sprite name against a sheet.
//   FillExtrusionPaintTests     — FillExtrusion.PaintProperties: classification + spec defaults.
//   LinePaintTests              — Line.PaintProperties/LayoutProperties: classification, translate, join/cap layout.
//   BackgroundPaintTests        — Background.PaintProperties: spec defaults, explicit parse, zoom classification.
//   SymbolStyleLayerTests       — a symbol layer parses to the typed Symbol.StyleLayer with spec defaults.
//   TextFieldResolverTests      — Symbol.TextFieldResolver.Resolve: token sugar + expression form -> label.
//   LineDashTests               — Line.LineDash: dash coverage, zoom-stability, width coupling, dasharray parse/eval.
//   IconImageResolverTests      — Symbol.IconImageResolver.Resolve: token sugar + expression form -> sprite name.
//
// LineOffsetTests.cs stays its own file: its `using MapRenderer.Core.Style.Line;` (a namespace import, for
// LineOffset) brings `MapRenderer.Core.Style.Line.StyleLayer` into scope, colliding with the bare
// `StyleLayer` (MapRenderer.Core.Style.StyleLayer) used above (CS0104).

using System;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Core.Expressions;
using System.Linq;
using System.Reflection;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using Background = MapRenderer.Core.Style.Background;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Text;
using System.Collections.Generic;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests.Style
{

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileJsonTests — TileJSON parses into the typed model; SourceResolver fills a SourceDefinition
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S83a acceptance: a TileJSON document parses into the typed <see cref="TileJson"/> model (tolerant,
    /// spec defaults), and <see cref="SourceResolver"/> fills a <see cref="SourceDefinition"/> from it —
    /// with an inline-<c>tiles[]</c> short-circuit. Fixtures use the REAL shipped demo style
    /// <c>liberty.json</c> source shapes (vector <c>openmaptiles</c> via <c>url</c>; raster
    /// <c>ne2_shaded</c> inline) so the parse-and-fill is exercised against production data.
    /// </summary>
    [TestFixture]
    public class TileJsonTests
    {
        // ---- fixture loader (walk-up; works under Unity batch mode AND dotnet test) ---------------
        private static string LoadStreamingFixtureText(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Assets", "StreamingAssets", "Fixtures", fileName);
                    if (File.Exists(candidate))
                        return File.ReadAllText(candidate);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found under Assets/StreamingAssets/Fixtures " +
                $"(cwd={Directory.GetCurrentDirectory()}, base={AppContext.BaseDirectory})");
        }

        // A representative TileJSON for the OpenFreeMap planet source (the document the liberty
        // `openmaptiles` source's `url` points at). Carries the real tiles[] + a constrained zoom
        // range (0..14, NOT the style-spec default 22) so a no-op resolve is detectable.
        private const string PlanetTileJson = @"{
          ""tilejson"": ""3.0.0"",
          ""name"": ""OpenMapTiles"",
          ""scheme"": ""xyz"",
          ""tiles"": [ ""https://tiles.openfreemap.org/planet/VERSION/{z}/{x}/{y}.pbf"" ],
          ""minzoom"": 0,
          ""maxzoom"": 14,
          ""bounds"": [ -180, -85.0511, 180, 85.0511 ],
          ""vector_layers"": [ { ""id"": ""water"" }, { ""id"": ""park"" } ]
        }";

        // =========================================================================================
        // 0. THE decisive test — parse a TileJSON, then fill the REAL liberty `openmaptiles` source
        //    (vector, has `url`, no inline `tiles`). After Resolve: Tiles is populated and the zoom
        //    range comes from the TileJSON (maxzoom 14, NOT the style default 22). A no-op / shallow
        //    impl that leaves Tiles null, or ignores the TileJSON zoom range, FAILS here.
        // =========================================================================================
        [Test]
        public void Resolve_FillsVectorSourceFromTileJson_RealLibertyShapes()
        {
            var style = StyleParser.Parse(LoadStreamingFixtureText("liberty.json"));
            var openmaptiles = style.GetSource("openmaptiles");

            // Precondition (real liberty shape): vector source, indirect via `url`, no inline tiles.
            Assert.IsNotNull(openmaptiles, "liberty has an 'openmaptiles' source");
            Assert.AreEqual(SourceType.Vector, openmaptiles.Type);
            Assert.AreEqual("https://tiles.openfreemap.org/planet", openmaptiles.Url);
            Assert.IsNull(openmaptiles.Tiles, "real liberty openmaptiles has no inline tiles[]");
            Assert.IsTrue(SourceResolver.NeedsTileJson(openmaptiles),
                "a url-only source needs TileJSON resolution");
            // Before resolution the source carries the style-spec default maxzoom (22), not the real 14.
            Assert.AreEqual(StyleParser.DefaultSourceMaxZoom, openmaptiles.MaxZoom,
                "pre-resolve maxzoom is the style-spec default");

            var tj = TileJsonParser.Parse(PlanetTileJson);
            var resolved = SourceResolver.Resolve(openmaptiles, tj);

            // Tiles now populated from the TileJSON.
            Assert.IsNotNull(resolved.Tiles, "Tiles filled from TileJSON (a no-op impl leaves this null → FAIL)");
            Assert.AreEqual(1, resolved.Tiles.Length);
            Assert.AreEqual("https://tiles.openfreemap.org/planet/VERSION/{z}/{x}/{y}.pbf", resolved.Tiles[0]);

            // Zoom range comes from the TileJSON (14), NOT the style-spec default (22).
            Assert.AreEqual(0, resolved.MinZoom, "minzoom from TileJSON");
            Assert.AreEqual(14, resolved.MaxZoom, "maxzoom from TileJSON, NOT the style default 22");
            Assert.AreEqual("xyz", resolved.Scheme, "scheme from TileJSON");
            Assert.AreEqual(4, resolved.Bounds.Length);
            Assert.AreEqual(85.0511, resolved.Bounds[3], 1e-9, "bounds from TileJSON");

            // After resolution it no longer needs TileJSON.
            Assert.IsFalse(SourceResolver.NeedsTileJson(resolved));
        }

        // =========================================================================================
        // 1. Inline `tiles[]` short-circuit — the real liberty `ne2_shaded` raster source has inline
        //    tiles and maxzoom 6. Resolve must return it UNCHANGED and NOT apply any TileJSON. A
        //    resolver that overwrites or re-derives the inline tiles FAILS.
        // =========================================================================================
        [Test]
        public void Resolve_InlineTilesShortCircuits_RealLibertyShapes()
        {
            var style = StyleParser.Parse(LoadStreamingFixtureText("liberty.json"));
            var ne2 = style.GetSource("ne2_shaded");

            Assert.IsNotNull(ne2, "liberty has an 'ne2_shaded' source");
            Assert.AreEqual(SourceType.Raster, ne2.Type);
            Assert.IsNotNull(ne2.Tiles, "real liberty ne2_shaded has inline tiles[]");
            Assert.AreEqual(1, ne2.Tiles.Length);
            string originalTile = ne2.Tiles[0];
            Assert.AreEqual(6, ne2.MaxZoom, "real liberty ne2_shaded maxzoom is 6");

            // Inline source must NOT need resolution …
            Assert.IsFalse(SourceResolver.NeedsTileJson(ne2),
                "an inline-tiles source does not need a TileJSON fetch");

            // … and even if a (wrong) caller hands it a contradictory TileJSON, Resolve leaves it intact.
            var contradictory = TileJsonParser.Parse(PlanetTileJson); // maxzoom 14, different tiles[]
            var result = SourceResolver.Resolve(ne2, contradictory);

            Assert.AreSame(ne2, result, "inline source returned unchanged (same instance)");
            Assert.AreEqual(originalTile, result.Tiles[0], "inline tiles[] not overwritten");
            Assert.AreEqual(6, result.MaxZoom, "inline source's maxzoom not clobbered by TileJSON");
        }

        // =========================================================================================
        // 2. TileJsonParser extracts every modeled field from a full document.
        // =========================================================================================
        [Test]
        public void TileJsonParser_ExtractsAllFields()
        {
            var tj = TileJsonParser.Parse(PlanetTileJson);

            Assert.IsNotNull(tj.Tiles);
            Assert.AreEqual(1, tj.Tiles.Length);
            Assert.AreEqual("https://tiles.openfreemap.org/planet/VERSION/{z}/{x}/{y}.pbf", tj.Tiles[0]);
            Assert.AreEqual(0, tj.MinZoom);
            Assert.AreEqual(14, tj.MaxZoom);
            Assert.AreEqual("xyz", tj.Scheme);
            Assert.AreEqual(4, tj.Bounds.Length);
            Assert.AreEqual(-180.0, tj.Bounds[0], 1e-9);
            Assert.IsNotNull(tj.Raw, "raw document retained for forward-compat (vector_layers, name, …)");
            Assert.IsTrue(tj.Raw.TryGet("vector_layers", out _), "unknown-but-present field preserved on Raw");
        }

        // =========================================================================================
        // 3. Tolerant: unknown/extra fields don't throw and known fields still extract.
        // =========================================================================================
        [Test]
        public void TileJsonParser_ToleratesUnknownFields()
        {
            const string json = @"{
              ""tilejson"": ""2.2.0"",
              ""tiles"": [ ""https://a/{z}/{x}/{y}.pbf"", ""https://b/{z}/{x}/{y}.pbf"" ],
              ""maxzoom"": 9,
              ""some-future-field"": { ""nested"": [1, 2, 3] },
              ""attribution"": ""© whoever""
            }";

            TileJson tj = null;
            Assert.DoesNotThrow(() => tj = TileJsonParser.Parse(json), "unknown fields must not throw");
            Assert.AreEqual(2, tj.Tiles.Length, "tiles still extracted alongside unknown fields");
            Assert.AreEqual(9, tj.MaxZoom);
            Assert.IsTrue(tj.Raw.TryGet("some-future-field", out _), "unknown field preserved on Raw");
        }

        // =========================================================================================
        // 4. Missing optional fields → shared style-spec defaults (single source of those constants).
        // =========================================================================================
        [Test]
        public void TileJsonParser_MissingFields_UseSharedSpecDefaults()
        {
            const string json = @"{ ""tiles"": [ ""https://a/{z}/{x}/{y}.pbf"" ] }";
            var tj = TileJsonParser.Parse(json);

            Assert.AreEqual(StyleParser.DefaultScheme, tj.Scheme, "scheme default == style-spec default");
            Assert.AreEqual(StyleParser.DefaultSourceMinZoom, tj.MinZoom, "minzoom default == style-spec default");
            Assert.AreEqual(StyleParser.DefaultSourceMaxZoom, tj.MaxZoom, "maxzoom default == style-spec default");
            Assert.IsNotNull(tj.Bounds);
            CollectionAssert.AreEqual(StyleParser.DefaultBounds, tj.Bounds, "bounds default == style-spec default");
        }

        // =========================================================================================
        // 5. Malformed JSON surfaces JsonParseException (not a silent null). Non-object documents are
        //    tolerated to defaults (consistent with StyleParser's empty-document tolerance).
        // =========================================================================================
        [Test]
        public void TileJsonParser_MalformedJson_Throws()
        {
            Assert.Throws<JsonParseException>(() => TileJsonParser.Parse("{ \"tiles\": }"));
            Assert.Throws<JsonParseException>(() => TileJsonParser.Parse("not json"));
        }

        [Test]
        public void TileJsonParser_NonObjectDocument_ResolvesToDefaults()
        {
            var tj = TileJsonParser.Parse("[1, 2, 3]"); // valid JSON, but not a TileJSON object
            Assert.IsNull(tj.Tiles, "no tiles in a non-object document");
            Assert.AreEqual(StyleParser.DefaultScheme, tj.Scheme);
            Assert.AreEqual(StyleParser.DefaultSourceMaxZoom, tj.MaxZoom);
            Assert.IsNotNull(tj.Bounds);
        }

        // =========================================================================================
        // 6. NeedsTileJson predicate truth table (the fetch-side short-circuit S83b relies on).
        // =========================================================================================
        [Test]
        public void NeedsTileJson_TruthTable()
        {
            Assert.IsFalse(SourceResolver.NeedsTileJson(null), "null → false");

            var urlOnly = new SourceDefinition { Url = "https://x/tiles.json" };
            Assert.IsTrue(SourceResolver.NeedsTileJson(urlOnly), "url + no tiles → needs resolution");

            var inline = new SourceDefinition { Tiles = new[] { "https://a/{z}/{x}/{y}.pbf" } };
            Assert.IsFalse(SourceResolver.NeedsTileJson(inline), "inline tiles → no resolution");

            var both = new SourceDefinition { Url = "https://x/tiles.json", Tiles = new[] { "https://a/{z}/{x}/{y}.pbf" } };
            Assert.IsFalse(SourceResolver.NeedsTileJson(both), "inline tiles win even when url present");

            var neither = new SourceDefinition();
            Assert.IsFalse(SourceResolver.NeedsTileJson(neither), "no url and no tiles → nothing to resolve");

            var emptyTiles = new SourceDefinition { Url = "https://x/tiles.json", Tiles = new string[0] };
            Assert.IsTrue(SourceResolver.NeedsTileJson(emptyTiles), "empty tiles[] treated as absent");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // StylePropertyTests — StyleProperty<T>: parse, classify, evaluate (uniform + bake), GC-allocation gate
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S60 — <see cref="StyleProperty{T}"/>: parse, classify, evaluate (uniform + bake), and
    /// GC-allocation gate. Rehomes S11 PaintPropertyEvaluatorTests and S12 DataDrivenPaintEvaluatorTests
    /// onto the single collapsed type.
    ///
    /// Covers:
    ///   1. Constant vs Zoom classification.
    ///   2. Numeric interpolation sampled at stop zooms and a mid-zoom (spec-derived math).
    ///   3. Color interpolation with premultiplied-alpha over zoom.
    ///   4. Feature / Composite input → Evaluate(zoom) throws (deferred to S12 guard).
    ///   5. No-GC sweep gate (Core-side): evaluating a zoom sweep in a tight loop allocates 0 bytes.
    ///   6. Bake-path (Evaluate(zoom,feature)): all ExpressionKinds accepted; no throw.
    ///   7. Data-driven match expression → distinct colors per feature.
    ///   8. Absent property → DefaultValue, Kind == Constant (no JSON round-trip).
    /// </summary>
    [TestFixture]
    public class StylePropertyTests
    {
        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        private static StyleProperty<float> NumProp(string json)
            => new StyleProperty<float>(MapRenderer.Core.Json.JsonParser.Parse(json), 0f, v => (float)v.AsNumber());

        private static StyleProperty<Color> ColProp(string json)
            => new StyleProperty<Color>(MapRenderer.Core.Json.JsonParser.Parse(json), new Color(0,0,0,1), v => v.AsColorCoerced());

        private static DictionaryFeature MakeFeature(params (string key, string val)[] props)
        {
            var d = new System.Collections.Generic.Dictionary<string, Value>();
            foreach (var (k, v) in props)
                d[k] = Value.String(v);
            return new DictionaryFeature(d);
        }

        private static DictionaryFeature MakeFeatureNum(params (string key, double val)[] props)
        {
            var d = new System.Collections.Generic.Dictionary<string, Value>();
            foreach (var (k, v) in props)
                d[k] = Value.Number(v);
            return new DictionaryFeature(d);
        }

        // ── 1. Classification ─────────────────────────────────────────────────────────────────

        [Test]
        public void Constant_LiteralNumber_IsConstantKind()
        {
            var prop = NumProp("5.0");
            Assert.AreEqual(ExpressionKind.Constant, prop.Kind);
            Assert.IsFalse(prop.IsZoomDependent);
        }

        [Test]
        public void Zoom_InterpolateWithZoom_IsZoomKind()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,1.0,15,20.0]");
            Assert.AreEqual(ExpressionKind.Zoom, prop.Kind);
            Assert.IsTrue(prop.IsZoomDependent);
        }

        // ── 2. Numeric interpolation ──────────────────────────────────────────────────────────

        [Test]
        public void Zoom_WidthInterpolate_AtLowStop_ReturnsStoValue()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(2.0f, prop.Evaluate(5.0), 1e-6f);
        }

        [Test]
        public void Zoom_WidthInterpolate_AtHighStop_ReturnsHighValue()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(20.0f, prop.Evaluate(15.0), 1e-6f);
        }

        [Test]
        public void Zoom_WidthInterpolate_AtMidZoom_ReturnsLinearInterpolation()
        {
            // At zoom=10, t = (10-5)/(15-5) = 0.5, value = 2.0 + 0.5*(20.0-2.0) = 11.0
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(11.0f, prop.Evaluate(10.0), 1e-4f);
        }

        [Test]
        public void Zoom_WidthInterpolate_BelowFirstStop_ClampsToFirst()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(2.0f, prop.Evaluate(0.0), 1e-6f);
        }

        [Test]
        public void Zoom_WidthInterpolate_AboveLastStop_ClampsToLast()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(20.0f, prop.Evaluate(20.0), 1e-6f);
        }

        // ── 3. Color interpolation with premultiplied-alpha ───────────────────────────────────
        //
        // Same hand-derived values as the retired PaintPropertyEvaluatorTests — byte-identity pin.

        [Test]
        public void Zoom_ColorInterpolate_PremultAlpha_TransparentToOpaque()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",0,0,0,0], 1, [\"rgba\",255,255,255,1]]");

            Color c = prop.Evaluate(0.5);
            double[] rgba = c.ToRgbaArray();

            Assert.AreEqual(255.0, rgba[0], 0.5, "Premult alpha: R at t=0.5 must be 255, not 127.5.");
            Assert.AreEqual(255.0, rgba[1], 0.5, "G must equal R for white.");
            Assert.AreEqual(255.0, rgba[2], 0.5, "B must equal R for white.");
            Assert.AreEqual(0.5,   rgba[3], 1e-6, "Alpha at t=0.5 must be 0.5.");
        }

        [Test]
        public void Zoom_ColorInterpolate_PremultAlpha_DivergenceFromStraightLerp()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",0,0,0,0], 1, [\"rgba\",255,255,255,1]]");

            Color c = prop.Evaluate(0.5);
            double r255 = c.ToRgbaArray()[0];
            Assert.Greater(r255, 200.0,
                "Premult interpolation must give R>200 (vs straight lerp's 127.5) for transparent→opaque.");
        }

        [Test]
        public void Zoom_ColorInterpolate_AlphaOne_ReducesToStraightLerp()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"to-color\", \"#000000\"], 1, [\"to-color\", \"#ffffff\"]]");

            Color c = prop.Evaluate(0.5);
            double[] rgba = c.ToRgbaArray();
            Assert.AreEqual(127.5, rgba[0], 1e-6, "At alpha=1, premult reduces to straight sRGB lerp.");
            Assert.AreEqual(127.5, rgba[1], 1e-6);
            Assert.AreEqual(127.5, rgba[2], 1e-6);
            Assert.AreEqual(1.0,   rgba[3], 1e-9);
        }

        [Test]
        public void Zoom_ColorInterpolate_PartialAlpha_PremultResult()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",200,0,0,0.5], 1, [\"rgba\",0,0,200,1.0]]");

            Color c = prop.Evaluate(0.5);
            double[] rgba = c.ToRgbaArray();

            Assert.AreEqual(0.75, rgba[3], 1e-6, "Alpha must be linear lerp of 0.5 and 1.0 at t=0.5.");
            Assert.AreEqual(66.67, rgba[0], 0.5, "R channel via premult lerp.");
            Assert.AreEqual(133.33, rgba[2], 0.5, "B channel via premult lerp.");
        }

        // ── 4. Feature / Composite → Evaluate(zoom) throws ──────────────────────────────────

        [Test]
        public void FeatureKind_Evaluate_Zoom_Throws()
        {
            // Construction does NOT throw (unlike old PaintPropertyEvaluator).
            var prop = NumProp("[\"get\", \"width\"]");
            Assert.AreEqual(ExpressionKind.Feature, prop.Kind);
            Assert.IsTrue(prop.DependsOnFeature);

            Assert.Throws<ArgumentException>(
                () => prop.Evaluate(0.0),
                "Feature-kind expressions must throw on Evaluate(zoom) — use Evaluate(zoom,feature) instead.");
        }

        [Test]
        public void CompositeKind_Evaluate_Zoom_Throws()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,[\"get\",\"w\"],10,5.0]");
            Assert.AreEqual(ExpressionKind.Composite, prop.Kind);

            Assert.Throws<ArgumentException>(
                () => prop.Evaluate(0.0),
                "Composite-kind expressions must throw on Evaluate(zoom).");
        }

        // ── 5. No-GC sweep gate ──────────────────────────────────────────────────────────────

        [Test]
        public void ZoomInterpolateNumber_SweepingZoom_AllocatesZeroBytes()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "Zoom number interpolation must not allocate any GC heap memory per call.");
        }

        [Test]
        public void ZoomInterpolateColor_SweepingZoom_AllocatesZeroBytes()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "5,[\"rgb\",255,0,0],15,[\"rgb\",0,0,255]]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "Zoom color interpolation must not allocate any GC heap memory per call.");
        }

        [Test]
        public void ZoomInterpolateNumber_WrappedWithNumberAssertion_AllocatesZeroBytes()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"number\",[\"zoom\"]],5,2.0,15,20.0]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "AssertExpression wrapping zoom must not allocate.");
        }

        [Test]
        public void Constant_LiteralNumber_EvaluatesIgnoringZoom()
        {
            var prop = NumProp("7.5");
            Assert.AreEqual(7.5f, prop.Evaluate(0.0), 1e-12f);
            Assert.AreEqual(7.5f, prop.Evaluate(99.0), 1e-12f);
        }

        // ── 6. Bake-path: all ExpressionKinds accepted by Evaluate(zoom, feature) ──────────

        [Test]
        public void AllKinds_BakePath_DoesNotThrow()
        {
            var f = MakeFeature(("width", "5"));

            // Constant (use numeric literal, not string literal)
            var c = NumProp("1.0");
            Assert.DoesNotThrow(() => c.Evaluate(0.0, null));

            // Zoom
            var z = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],0,1.0,8,5.0]");
            Assert.DoesNotThrow(() => z.Evaluate(0.0, null));

            // Feature — does NOT throw when using TryEvaluate (bake path)
            var feat = new StyleProperty<float>(
                MapRenderer.Core.Json.JsonParser.Parse("[\"to-number\",[\"get\",\"width\"]]"),
                0f, v => (float)v.AsNumber());
            Assert.DoesNotThrow(() => feat.TryEvaluate(0.0, f, out _));
        }

        // ── 7. Data-driven match → distinct colors per feature ───────────────────────────────

        private const string MatchExpr =
            "[\"match\",[\"get\",\"CONTINENT\"]," +
            "\"Asia\",[\"rgba\",200,50,50,1]," +
            "\"South America\",[\"rgba\",50,50,200,1]," +
            "[\"rgba\",128,128,128,1]]";

        [Test]
        public void FeatureKind_Match_TwoFeatures_DistinctColors()
        {
            var prop = ColProp(MatchExpr);
            Assert.AreEqual(ExpressionKind.Feature, prop.Kind);
            Assert.IsTrue(prop.DependsOnFeature);

            var asia = MakeFeature(("CONTINENT", "Asia"));
            var sam  = MakeFeature(("CONTINENT", "South America"));

            prop.TryEvaluate(0.0, asia, out Color cAsia);
            prop.TryEvaluate(0.0, sam,  out Color cSam);

            Assert.Greater(cAsia.R, cAsia.B + 0.3, "Asia must be reddish.");
            Assert.Greater(cSam.B,  cSam.R  + 0.3, "South America must be bluish.");
            Assert.AreNotEqual(cAsia, cSam, "Two different continents must produce distinct colors.");
        }

        [Test]
        public void FeatureKind_Match_DefaultBranch_IsGray()
        {
            var prop = ColProp(MatchExpr);
            var eur  = MakeFeature(("CONTINENT", "Europe"));

            prop.TryEvaluate(0.0, eur, out Color c);
            Assert.AreEqual(128.0 / 255.0, c.R, 0.01, "Default branch: R must be ~128/255.");
            Assert.AreEqual(128.0 / 255.0, c.G, 0.01);
            Assert.AreEqual(128.0 / 255.0, c.B, 0.01);
        }

        [Test]
        public void MissingProperty_FallsToMatchDefault()
        {
            var prop       = ColProp(MatchExpr);
            var noContinent = new DictionaryFeature();

            prop.TryEvaluate(0.0, noContinent, out Color c);
            Assert.AreEqual(128.0 / 255.0, c.R, 0.01);
            Assert.AreEqual(128.0 / 255.0, c.G, 0.01);
            Assert.AreEqual(128.0 / 255.0, c.B, 0.01);
        }

        // ── 8. Absent property → DefaultValue, Constant kind ────────────────────────────────

        [Test]
        public void AbsentProperty_ConstantCtor_UsesDefaultValue()
        {
            var prop = new StyleProperty<float>(42.5f);
            Assert.AreEqual(ExpressionKind.Constant, prop.Kind);
            Assert.IsFalse(prop.DependsOnFeature);
            Assert.AreEqual(42.5f, prop.Evaluate(0.0), 1e-12f);
            Assert.AreEqual(42.5f, prop.DefaultValue, 1e-12f);
        }

        [Test]
        public void AbsentColorProperty_ConstantCtor_ReturnsDefault()
        {
            var defaultColor = new Color(1f, 0f, 0f, 1f);
            var prop = new StyleProperty<Color>(defaultColor);
            Assert.AreEqual(ExpressionKind.Constant, prop.Kind);
            Color c = prop.Evaluate(0.0);
            Assert.AreEqual(defaultColor.R, c.R, 1e-6f);
            Assert.AreEqual(defaultColor.G, c.G, 1e-6f);
            Assert.AreEqual(defaultColor.B, c.B, 1e-6f);
        }

        // ── 9. Data-driven bake path (rehomed from S12 DataDrivenPaintEvaluatorTests) ──────────

        [Test]
        public void ConstantControl_SameColorForAllFeatures()
        {
            const string controlExpr =
                "[\"match\",[\"get\",\"__NONEXISTENT__\"]," +
                "\"x\",[\"rgba\",255,0,0,1]," +
                "[\"rgba\",64,64,64,1]]";

            var prop = ColProp(controlExpr);
            var f1 = MakeFeature(("CONTINENT", "Asia"));
            var f2 = MakeFeature(("CONTINENT", "South America"));
            var f3 = new DictionaryFeature();

            prop.TryEvaluate(0.0, f1, out Color c1);
            prop.TryEvaluate(0.0, f2, out Color c2);
            prop.TryEvaluate(0.0, f3, out Color c3);

            Assert.AreEqual(c1, c2, "Constant-control: all features must produce the same color (c1==c2).");
            Assert.AreEqual(c2, c3, "Constant-control: all features must produce the same color (c2==c3).");
            Assert.AreEqual(64.0 / 255.0, c1.R, 0.01, "Control color R must be 64/255.");
        }

        [Test]
        public void Composite_InterpolateZoom_FeatureColorStops_DistinctAtSameZoom()
        {
            const string compositeExpr =
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0,[\"match\",[\"get\",\"CONTINENT\"],\"Asia\",[\"rgba\",200,50,50,1],[\"rgba\",50,50,200,1]]," +
                "8,[\"match\",[\"get\",\"CONTINENT\"],\"Asia\",[\"rgba\",220,20,20,1],[\"rgba\",20,20,220,1]]]";

            var prop = ColProp(compositeExpr);
            Assert.AreEqual(ExpressionKind.Composite, prop.Kind,
                "Interpolate over zoom with feature-dependent stops must be Composite kind.");

            var asia = MakeFeature(("CONTINENT", "Asia"));
            var sam  = MakeFeature(("CONTINENT", "South America"));

            prop.TryEvaluate(0.0, asia, out Color asiaZ0);
            prop.TryEvaluate(0.0, sam,  out Color samZ0);
            Assert.AreEqual(200.0 / 255.0, asiaZ0.R, 0.01, "Asia at z=0: R must be 200/255.");
            Assert.AreEqual(50.0  / 255.0, asiaZ0.B, 0.01, "Asia at z=0: B must be 50/255.");
            Assert.AreEqual(50.0  / 255.0, samZ0.R,  0.01, "S.America at z=0: R must be 50/255.");
            Assert.AreEqual(200.0 / 255.0, samZ0.B,  0.01, "S.America at z=0: B must be 200/255.");

            prop.TryEvaluate(8.0, asia, out Color asiaZ8);
            prop.TryEvaluate(8.0, sam,  out Color samZ8);
            Assert.AreEqual(220.0 / 255.0, asiaZ8.R, 0.01, "Asia at z=8: R must be 220/255.");
            Assert.AreEqual(20.0  / 255.0, asiaZ8.B, 0.01, "Asia at z=8: B must be 20/255.");
            Assert.AreEqual(20.0  / 255.0, samZ8.R,  0.01, "S.America at z=8: R must be 20/255.");
            Assert.AreEqual(220.0 / 255.0, samZ8.B,  0.01, "S.America at z=8: B must be 220/255.");

            prop.TryEvaluate(4.0, asia, out Color asiaZ4);
            Assert.AreEqual(210.0 / 255.0, asiaZ4.R, 0.01, "Asia at z=4 (mid): R must be 210/255.");
            Assert.AreEqual(35.0  / 255.0, asiaZ4.G, 0.01, "Asia at z=4 (mid): G must be 35/255.");
            Assert.AreEqual(35.0  / 255.0, asiaZ4.B, 0.01, "Asia at z=4 (mid): B must be 35/255.");

            prop.TryEvaluate(4.0, sam, out Color samZ4);
            Assert.AreEqual(35.0  / 255.0, samZ4.R, 0.01, "S.America at z=4: R must be 35/255.");
            Assert.AreEqual(210.0 / 255.0, samZ4.B, 0.01, "S.America at z=4: B must be 210/255.");

            Assert.AreNotEqual(asiaZ4, samZ4, "Composite at zoom 4: Asia and S.America must differ.");
        }

        [Test]
        public void FeatureKind_NumericStep_OpacityPerFeature()
        {
            const string stepExpr = "[\"step\",[\"get\",\"fid\"],0.3,10,0.7,100,1.0]";
            var prop = NumProp(stepExpr);
            Assert.AreEqual(ExpressionKind.Feature, prop.Kind);

            var low  = MakeFeatureNum(("fid", 5.0));
            var mid  = MakeFeatureNum(("fid", 50.0));
            var high = MakeFeatureNum(("fid", 150.0));

            prop.TryEvaluate(0.0, low,  out float opLow);
            prop.TryEvaluate(0.0, mid,  out float opMid);
            prop.TryEvaluate(0.0, high, out float opHigh);

            Assert.AreEqual(0.3f, opLow,  1e-6f, "fid=5 must yield opacity 0.3.");
            Assert.AreEqual(0.7f, opMid,  1e-6f, "fid=50 must yield opacity 0.7.");
            Assert.AreEqual(1.0f, opHigh, 1e-6f, "fid=150 must yield opacity 1.0.");

            Assert.AreNotEqual(opLow, opMid,  "Opacity must vary per feature (low vs mid).");
            Assert.AreNotEqual(opMid, opHigh, "Opacity must vary per feature (mid vs high).");
        }

        [Test]
        public void AllKinds_ConstructionDoesNotThrow()
        {
            Assert.DoesNotThrow(() => ColProp("\"#ff0000\""), "Constant-kind must not throw.");
            Assert.DoesNotThrow(
                () => ColProp("[\"interpolate\",[\"linear\"],[\"zoom\"],0,\"#ff0000\",8,\"#0000ff\"]"),
                "Zoom-kind must not throw.");
            Assert.DoesNotThrow(
                () => ColProp("[\"get\",\"width\"]"),
                "Feature-kind must not throw at construction.");
            Assert.DoesNotThrow(
                () => ColProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,[\"get\",\"w\"],10,\"#ff0000\"]"),
                "Composite-kind must not throw at construction.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // StyleLayerEagerParseTests — the eight style property types parse eagerly via a static Parse factory
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// UMR-108 acceptance: the eight style property types (<c>Fill</c>/<c>Line</c>/<c>Symbol</c>/
    /// <c>Background</c>/<c>FillExtrusion</c> × <c>Paint</c>/<c>Layout</c>, where each exists) parse eagerly
    /// via a static <c>Parse</c> factory — not lazily from a retained raw JSON field.
    ///
    /// Tooth 1: <see cref="StyleParser"/> threads its <c>fillAntialiasDefault</c> parameter into the parse,
    /// rather than a hardcoded constant — pins behaviour preservation (passes against the pre-change code
    /// too; it is tooth 2 below that observes the laziness actually being removed).
    /// Tooth 2: reflection over the compiled types — the laziness cannot be reconstructed (see each clause).
    /// Tooth 4: <c>Parse(null)</c> is tolerated (returns all spec defaults, does not throw) by all eight
    /// types — the idiom ~60 test call sites and every production caller with an absent paint/layout block
    /// depend on.
    /// </summary>
    [TestFixture]
    public class StyleLayerEagerParseTests
    {
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        // ── Tooth 1 — behaviour preservation through StyleParser (passes today too) ────────────────

        /// <summary>Builds a minimal style with exactly one fill layer, whose <c>paint</c> block is empty
        /// unless <paramref name="antialias"/> is supplied.</summary>
        /// <param name="antialias">When set, the layer's <c>fill-antialias</c> value; when null, the key
        /// is omitted entirely.</param>
        /// <returns>The style JSON string.</returns>
        private static string MinimalFillStyle(bool? antialias)
        {
            string paint = antialias.HasValue ? $"{{\"fill-antialias\":{(antialias.Value ? "true" : "false")}}}" : "{}";
            return "{\"version\":8,\"sources\":{\"s\":{\"type\":\"vector\",\"tiles\":[\"https://example.com/{z}/{x}/{y}.pbf\"]}}," +
                   "\"layers\":[{\"id\":\"f\",\"type\":\"fill\",\"source\":\"s\",\"source-layer\":\"l\",\"paint\":" + paint + "}]}";
        }

        [Test]
        public void StyleParser_FillAntialiasOmitted_HostDefaultFalse_ReachesTheParse()
        {
            var doc = StyleParser.Parse(MinimalFillStyle(antialias: null), fillAntialiasDefault: false);
            var fill = (Fill.StyleLayer)doc.Layers[0];
            Assert.IsFalse(fill.Paint.Antialias.Evaluate(0.0),
                "an omitted fill-antialias must fall back to the HOST default, not a hardcoded true.");
        }

        [Test]
        public void StyleParser_FillAntialiasOmitted_HostDefaultTrue_ReachesTheParse()
        {
            var doc = StyleParser.Parse(MinimalFillStyle(antialias: null), fillAntialiasDefault: true);
            var fill = (Fill.StyleLayer)doc.Layers[0];
            Assert.IsTrue(fill.Paint.Antialias.Evaluate(0.0),
                "the host default must be READ, not just present as an unused parameter.");
        }

        [Test]
        public void StyleParser_FillAntialiasExplicit_WinsOverTheHostDefault()
        {
            var doc = StyleParser.Parse(MinimalFillStyle(antialias: true), fillAntialiasDefault: false);
            var fill = (Fill.StyleLayer)doc.Layers[0];
            Assert.IsTrue(fill.Paint.Antialias.Evaluate(0.0),
                "an explicit layer value must win over the host default, whichever it is.");
        }

        // ── Tooth 2 — the laziness cannot come back (reflection, never text) ───────────────────────

        /// <summary>True when <paramref name="property"/>'s setter carries the <c>IsExternalInit</c>
        /// required custom modifier — the compiler's marker for an <c>init</c> accessor. Tolerates a
        /// missing setter (<c>SetMethod == null</c>) by returning false rather than throwing.
        ///
        /// <para>Compared by <c>FullName</c>, never <c>== typeof(IsExternalInit)</c>: Core carries its own
        /// <c>internal static class IsExternalInit</c> polyfill (<c>Core/IsExternalInit.cs</c>), duplicated
        /// per assembly, and this TEST assembly has none of its own — a type-identity comparison would read
        /// false even for a genuinely init-only property, making the tooth a permanent false RED.</para>
        /// </summary>
        private static bool IsInitOnly(PropertyInfo property)
        {
            if (property.SetMethod == null) return false;
            return property.SetMethod.ReturnParameter.GetRequiredCustomModifiers()
                .Any(t => t.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        }

        /// <summary>Every typed <c>StyleLayer</c> subclass's <c>Paint</c>/<c>Layout</c> properties (only
        /// the ones the type actually declares — <c>Background</c>/<c>FillExtrusion</c> have no Layout).</summary>
        private static readonly (Type Layer, string Property)[] TypedLayerViews =
        {
            (typeof(Fill.StyleLayer), "Paint"),
            (typeof(Fill.StyleLayer), "Layout"),
            (typeof(Line.StyleLayer), "Paint"),
            (typeof(Line.StyleLayer), "Layout"),
            (typeof(SymbolStyle.StyleLayer), "Paint"),
            (typeof(SymbolStyle.StyleLayer), "Layout"),
            (typeof(Background.StyleLayer), "Paint"),
            (typeof(FillExtrusion.StyleLayer), "Paint"),
        };

        /// <summary>Clause A — non-vacuity + init-only: every typed layer's Paint/Layout property exists
        /// and its setter carries the <c>init</c> modreq. The discriminator is the required custom modifier,
        /// not merely <c>SetMethod != null</c> — <c>{ get => _paint ??= …; set => _paint = value; }</c> would
        /// pass a bare non-null check while restoring the laziness in full.</summary>
        [Test]
        public void TypedLayerViews_ArePresentAndInitOnly()
        {
            foreach ((Type layer, string propertyName) in TypedLayerViews)
            {
                PropertyInfo property = layer.GetProperty(propertyName, PublicInstance);
                Assert.IsNotNull(property, $"{layer.Name}.{propertyName} must exist.");
                Assert.IsTrue(IsInitOnly(property),
                    $"{layer.Name}.{propertyName} must be init-only (carry the IsExternalInit modreq).");
            }
        }

        /// <summary>Clause B — no public writable property re-creating the deleted
        /// <c>Fill.StyleLayer.AntialiasDefault</c> laziness knob.</summary>
        [Test]
        public void FillStyleLayer_AntialiasDefault_IsGone()
        {
            PropertyInfo property = typeof(Fill.StyleLayer).GetProperty("AntialiasDefault", PublicInstance);
            Assert.IsNull(property,
                "Fill.StyleLayer must not carry a public writable AntialiasDefault (or anything reviving it).");
        }

        /// <summary>All eight property types, for clause C.</summary>
        private static readonly Type[] PropertyTypes =
        {
            typeof(Fill.PaintProperties), typeof(Fill.LayoutProperties),
            typeof(Line.PaintProperties), typeof(Line.LayoutProperties),
            typeof(SymbolStyle.PaintProperties), typeof(SymbolStyle.LayoutProperties),
            typeof(Background.PaintProperties),
            typeof(FillExtrusion.PaintProperties),
        };

        /// <summary>Clause C — no public constructor on any of the eight property types; <c>Parse</c> is
        /// the only way in.</summary>
        [Test]
        public void PropertyTypes_HaveNoPublicConstructor()
        {
            foreach (Type type in PropertyTypes)
                Assert.AreEqual(0, type.GetConstructors().Length,
                    $"{type.FullName} must have zero public constructors — Parse is the only way in.");
        }

        // ── Tooth 4 — Parse(null) is tolerated by all eight types ───────────────────────────────────

        /// <summary><c>Parse(null)</c> returning all-spec-defaults (never throwing) is the idiom ~60 test
        /// call sites and every production caller with an absent paint/layout block rely on. One straight-line
        /// test covering all eight types, not eight separate ones.</summary>
        [Test]
        public void ParseNull_ReturnsNonNull_ForAllEightTypes()
        {
            Assert.IsNotNull(Fill.PaintProperties.Parse(null));
            Assert.IsNotNull(Fill.LayoutProperties.Parse(null));
            Assert.IsNotNull(Line.PaintProperties.Parse(null));
            Assert.IsNotNull(Line.LayoutProperties.Parse(null));
            Assert.IsNotNull(SymbolStyle.PaintProperties.Parse(null));
            Assert.IsNotNull(SymbolStyle.LayoutProperties.Parse(null));
            Assert.IsNotNull(Background.PaintProperties.Parse(null));
            Assert.IsNotNull(FillExtrusion.PaintProperties.Parse(null));
        }

        // ── layout.visibility parses into the one draw-gate predicate ──────────────────────────

        /// <summary>
        /// <c>StyleParser.ParseLayer</c> sets <c>Visible</c> to <c>false</c> only for
        /// <c>visibility: "none"</c>. Any other value — including an absent layout, an absent key, and a
        /// garbage string — is <c>true</c>, which the spec's default gives for free.
        /// </summary>
        /// <param name="layoutJson">The layer's <c>layout</c> block, or <c>null</c> to omit it entirely.</param>
        [TestCase("{\"visibility\":\"none\"}",    false, TestName = "Visibility_None_Hides")]
        [TestCase("{\"visibility\":\"visible\"}", true,  TestName = "Visibility_Visible_Draws")]
        [TestCase("{}",                            true,  TestName = "Visibility_AbsentKey_Draws")]
        [TestCase("{\"visibility\":\"NONE\"}",    true,  TestName = "Visibility_WrongCase_Draws")]
        [TestCase(null,                            true,  TestName = "Visibility_NoLayoutBlock_Draws")]
        public void ParseLayer_Visibility_SetsVisible(string layoutJson, bool expected)
        {
            string layout = layoutJson == null ? "" : $"\"layout\": {layoutJson},";
            StyleDocument doc = StyleParser.Parse($@"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [ {{ ""id"": ""f0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""f0"", {layout}
        ""paint"": {{ ""fill-color"": [""rgba"",102,153,204,1] }} }} ]
}}");
            Assert.AreEqual(expected, doc.Layers[0].Visible,
                $"layout {layoutJson ?? "(absent)"} must parse to Visible={expected}. Only the exact "
                + "string \"none\" hides a layer; every other value is the spec default \"visible\".");
        }

        /// <summary>
        /// A hidden layer is invisible even at a zoom strictly INSIDE its declared range — the row an
        /// implementation that ANDs the flag in the wrong place fails.
        /// </summary>
        [Test]
        public void HiddenLayer_IsNotVisible_EvenInsideItsZoomRange()
        {
            var layer = new StyleLayer { Id = "x", MinZoom = 5.0, MaxZoom = 20.0 };
            Assert.IsTrue(layer.IsVisibleAtZoom(10.0), "precondition: z10 is inside [5,20), so it draws.");

            layer.Visible = false;
            Assert.IsFalse(layer.IsVisibleAtZoom(10.0),
                "visibility:none must hide the layer at EVERY zoom, including one inside its declared "
                + "range. A flag consulted only outside the bounds passes the out-of-range rows and fails "
                + "exactly here.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // FillLayoutTests — Fill.LayoutProperties: parsing fill-sort-key
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// P3 — <see cref="Fill.LayoutProperties"/>: parsing <c>fill-sort-key</c>.
    ///
    /// <para>The ordering behaviour it drives is pinned engine-side by <c>FillSortKeySnapshotTests</c>;
    /// this fixture covers the parse, the default, and the zoom/feature capability that decides whether the
    /// key is evaluated per feature at build time or could be hoisted.</para>
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    /// </summary>
    [TestFixture]
    public class FillLayoutTests
    {
        [Test]
        public void SortKey_Absent_DefaultsToZeroAndFlagsDefault()
        {
            var layout = TestStyle.FillLayout();

            Assert.IsTrue(layout.SortKeyIsDefault,
                "an absent fill-sort-key must be flagged so the builder can skip the sort entirely — that " +
                "is what keeps existing fill meshes byte-identical.");
            Assert.AreEqual(0f, layout.SortKey.Evaluate(0.0), 1e-9, "spec default is 0");
        }

        [Test]
        public void SortKey_EmptyLayoutObject_IsStillDefault()
        {
            var layout = TestStyle.FillLayout("{}");
            Assert.IsTrue(layout.SortKeyIsDefault, "a layout object without the key is the same as no layout");
        }

        [Test]
        public void SortKey_Constant_IsParsedAndNotFlaggedDefault()
        {
            var layout = TestStyle.FillLayout(@"{""fill-sort-key"": 7}");

            Assert.IsFalse(layout.SortKeyIsDefault, "an explicit key must NOT be treated as absent");
            Assert.AreEqual(7f, layout.SortKey.Evaluate(0.0), 1e-9);
            Assert.AreEqual(ExpressionKind.Constant, layout.SortKey.Kind);
        }

        [Test]
        public void SortKey_ZoomExpression_IsZoomDependent()
        {
            var layout = TestStyle.FillLayout(@"{""fill-sort-key"": [""interpolate"", [""linear""], [""zoom""], 0, 0, 10, 100]}");

            Assert.IsFalse(layout.SortKeyIsDefault);
            Assert.IsTrue(layout.SortKey.IsZoomDependent, "a zoom expression must classify as zoom-dependent");
            Assert.AreEqual(0f,   layout.SortKey.Evaluate(0.0),  1e-4);
            Assert.AreEqual(100f, layout.SortKey.Evaluate(10.0), 1e-4);
        }

        [Test]
        public void SortKey_FeatureExpression_DependsOnFeature()
        {
            var layout = TestStyle.FillLayout(@"{""fill-sort-key"": [""get"", ""rank""]}");

            Assert.IsTrue(layout.SortKey.DependsOnFeature,
                "a data-driven sort key must be evaluated per FEATURE — the builder relies on this to sort " +
                "features against each other rather than hoisting one value for the layer.");
        }

        [Test]
        public void StyleLayer_ExposesParsedLayout()
        {
            var style = MapRenderer.Core.Style.StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://e.invalid/{z}/{x}/{y}.pbf""] } },
                ""layers"": [ { ""id"": ""f"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""l"",
                                ""layout"": { ""fill-sort-key"": 3 } } ]
            }");

            var fillLayer = (Fill.StyleLayer)style.Layers[0];
            Assert.IsFalse(fillLayer.Layout.SortKeyIsDefault, "the parser must route layout onto the typed layer");
            Assert.AreEqual(3f, fillLayer.Layout.SortKey.Evaluate(0.0), 1e-9);
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // FillPatternTests — Fill.FillPattern: resolving a fill-pattern sprite name against a sheet
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// P2 — <see cref="Fill.FillPattern"/>: resolving a <c>fill-pattern</c> sprite name against a sheet into
    /// the rect + repeat count the fill shader samples with.
    ///
    /// <para>The load-bearing case is the NEGATIVE one. A <c>fill-pattern</c> layer characteristically
    /// declares no <c>fill-color</c>, so it inherits the spec's opaque-black default; if an unresolvable
    /// pattern fell back to that colour instead of reporting "unresolved", the layer paints solid black.
    /// That is exactly the defect this stage fixes (Liberty's <c>road_area_pattern</c> plazas and
    /// <c>landcover_wetland</c>) — see docs/fill-parity-design.md §1.</para>
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    /// </summary>
    [TestFixture]
    public class FillPatternTests
    {
        // A 64×64 sheet holding one 32×32 @1x sprite and one 32×32 @2x sprite (16 css px logical).
        private const string SheetJson = @"{
            ""plaza"":    { ""x"": 0,  ""y"": 0,  ""width"": 32, ""height"": 32, ""pixelRatio"": 1 },
            ""plaza2x"":  { ""x"": 32, ""y"": 0,  ""width"": 32, ""height"": 32, ""pixelRatio"": 2 },
            ""tall"":     { ""x"": 0,  ""y"": 32, ""width"": 16, ""height"": 32, ""pixelRatio"": 1 },
            ""degenerate"":{ ""x"": 0, ""y"": 0,  ""width"": 0,  ""height"": 0,  ""pixelRatio"": 1 }
        }";

        private static SpriteAtlasView Sheet() => new SpriteAtlasView
        {
            Index = SpriteIndex.Parse(SheetJson),
            Size  = new int2(64, 64),
        };

        // ── The fix: every "cannot resolve" path reports unresolved, never a colour fallback ──────

        [Test]
        public void Unresolved_WhenNoPatternDeclared()
        {
            Assert.IsFalse(Fill.FillPattern.TryResolve(null, Sheet(), out var r),
                "a layer with no fill-pattern must not resolve a pattern");
            Assert.IsFalse(r.IsResolved);
            Assert.AreEqual(0.0, r.Rect.z, "unresolved must be a ZERO-AREA rect — the shader's clip signal");
            Assert.AreEqual(0.0, r.Rect.w);
        }

        [Test]
        public void Unresolved_WhenSheetHasNotArrivedYet()
        {
            // The real steady state for the first frames of every style: the sheet is fetched async, so a
            // pattern layer's material is built before it exists.
            Assert.IsFalse(Fill.FillPattern.TryResolve("plaza", null, out var r),
                "a null atlas (sheet not fetched yet) must report unresolved, not fall back to fill-color");
            Assert.IsFalse(r.IsResolved);
            Assert.AreEqual(0.0, r.Rect.z);
        }

        [Test]
        public void Unresolved_WhenNameAbsentFromSheet()
        {
            Assert.IsFalse(Fill.FillPattern.TryResolve("no-such-sprite", Sheet(), out var r),
                "a name absent from the sheet must report unresolved (spec: the layer is not painted)");
            Assert.IsFalse(r.IsResolved);
            Assert.AreEqual(0.0, r.Rect.z);
        }

        [Test]
        public void Unresolved_WhenSpriteRectIsDegenerate()
        {
            Assert.IsFalse(Fill.FillPattern.TryResolve("degenerate", Sheet(), out var r),
                "a zero-area sprite is not drawable and must report unresolved");
            Assert.IsFalse(r.IsResolved);
        }

        // ── Resolution arithmetic ────────────────────────────────────────────────────────────────

        [Test]
        public void Resolved_RectIsTheSpriteSheetPixelRect()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza2x", Sheet(), out var r));
            Assert.IsTrue(r.IsResolved);
            Assert.AreEqual(32.0, r.Rect.x, "rect.xy is the sprite's top-left in SHEET pixels");
            Assert.AreEqual(0.0,  r.Rect.y);
            Assert.AreEqual(32.0, r.Rect.z, "rect.zw is the sprite's size in SHEET pixels (not logical px)");
            Assert.AreEqual(32.0, r.Rect.w);
        }

        [Test]
        public void Resolved_CarriesTheSpritesAuthoredPixelSize()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));
            Assert.AreEqual(32.0, r.LogicalSizePixels.x, 1e-9, "32 sheet px at pixelRatio 1 = 32 css px");
            Assert.AreEqual(32.0, r.LogicalSizePixels.y, 1e-9);
        }

        [Test]
        public void Resolved_PixelRatioHalvesTheAuthoredSize()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza2x", Sheet(), out var r));
            Assert.AreEqual(16.0, r.LogicalSizePixels.x, 1e-9,
                "32 sheet px at pixelRatio 2 is a 16 css px sprite — that divisor is what keeps an @2x sheet " +
                "from drawing its patterns at double size");
        }

        [Test]
        public void Resolved_AspectIsHeightOverWidth()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("tall", Sheet(), out var r)); // 16×32
            Assert.AreEqual(2.0, r.Aspect, 1e-9, "a 16×32 sprite is twice as tall as it is wide");
        }

        [Test]
        public void Resolved_MalformedZeroPixelRatioDoesNotProduceInfiniteRepeats()
        {
            var sheet = new SpriteAtlasView
            {
                Index = SpriteIndex.Parse(
                    @"{ ""bad"": { ""x"":0, ""y"":0, ""width"":32, ""height"":32, ""pixelRatio"": 0 } }"),
                Size = new int2(64, 64),
            };
            Assert.IsTrue(Fill.FillPattern.TryResolve("bad", sheet, out var r),
                "a malformed pixelRatio must degrade to 1, not reject an otherwise-valid sprite");
            Assert.AreEqual(32.0, r.LogicalSizePixels.x, 1e-9);
        }

        // ── Sizing: ScreenRelative (spec) vs WorldAbsolute (engine extension) ────────────────────
        //
        // These now measure repetitions per WORLD UNIT, not per tile. The tile frame was abandoned because a
        // tile's own zoom is not derivable from the display zoom: OpenFreeMap's source stops at z14 while the
        // camera zooms to 18+, so the same tiles are stretched across five display zooms, and a mixed-zoom
        // cover (ScreenSpaceLod, the default) mixes levels within one frame. Both made the old tile-relative
        // correction wrong by up to 16×.

        [Test]
        public void ScreenRelative_PeriodIsTheSpriteAtItsAuthoredPixelSize()
        {
            // The spec's meaning: the sprite occupies its own pixel size on screen. So one repetition spans
            // logicalPixels × metresPerPixel of world — no tile term anywhere.
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));
            const double zoom = 14.0;

            double2 repeats = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, zoom, 0.0);

            double expectedPeriod = 32.0 * WebMercator.GroundResolution(zoom); // 32 css px sprite
            Assert.AreEqual(1.0 / expectedPeriod, repeats.x, 1e-12);
        }

        [Test]
        public void ScreenRelative_IsContinuousInZoom_NoSteppingAndNoPerLevelSnap()
        {
            // The artefact this replaced: repeats used to be rounded to whole numbers per tile, so a
            // continuous zoom produced ~16 discrete snaps per zoom level — invisible in a screenshot, obvious
            // while zooming. Period is now a smooth function of zoom, so consecutive samples must differ
            // smoothly and never repeat a value (which is what a stair-step looks like numerically).
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));

            double previous = -1.0;
            for (int step = 0; step <= 80; step++)
            {
                double zoom = 14.0 + step / 40.0;   // spans two whole zoom levels
                double value = Fill.FillPattern.RepeatsPerWorldUnit(
                    r, Fill.FillPatternSizing.ScreenRelative, zoom, 0.0).x;

                Assert.Greater(value, previous,
                    $"repeats-per-world-unit must increase STRICTLY with zoom (at {zoom}); an equal " +
                    "consecutive value is a stair-step, which is exactly the snapping this replaced");
                if (previous > 0.0)
                    Assert.Less(value / previous, 1.1,
                        $"and it must step smoothly — a jump at {zoom} would be a visible pop while zooming");
                previous = value;
            }
        }

        [Test]
        public void ScreenRelative_CrossingAZoomLevelBoundaryIsSmooth()
        {
            // The old form reset at each integer zoom (the cover switching level). Nothing resets now, so
            // straddling z15 must be indistinguishable from any other neighbouring pair.
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));

            double below = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, 14.999, 0.0).x;
            double above = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, 15.001, 0.0).x;

            Assert.AreEqual(1.0, above / below, 0.01,
                "crossing an integer zoom must not jump — the tile-relative form snapped here, which is what " +
                "made zooming look broken");
        }

        [Test]
        public void ScreenRelative_IsIndependentOfTheTilesOwnZoom_SoOverzoomIsCorrect()
        {
            // THE overzoom tooth. Past the source's maxzoom the same tiles are drawn at every display zoom, so
            // any term derived from the tile would be frozen while the camera keeps going. Repeats depend on
            // the DISPLAY zoom only, so the apparent size stays correct: two display zooms one level apart
            // must differ by exactly 2× regardless of which tiles are underneath.
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));

            double atZ14 = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, 14.0, 0.0).x;
            double atZ18 = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, 18.0, 0.0).x;

            Assert.AreEqual(16.0, atZ18 / atZ14, 1e-9,
                "four zoom levels in ⇒ 16× more repetitions per world unit, so the pattern holds its screen " +
                "size. The tile-relative form was off by exactly this factor past maxzoom — the reported " +
                "'pattern is too zoomed in' at z15-18 over a z14 source");
        }

        [Test]
        public void WorldAbsolute_IsIndependentOfZoomEntirely()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));
            const double period = 50.0;

            foreach (double zoom in new[] { 0.0, 7.5, 14.0, 18.25, 22.0 })
                Assert.AreEqual(1.0 / period,
                    Fill.FillPattern.RepeatsPerWorldUnit(
                        r, Fill.FillPatternSizing.WorldAbsolute, zoom, period).x, 1e-12,
                    $"a world-sized pattern must not vary with zoom at all (checked {zoom})");
        }

        [Test]
        public void WorldAbsolute_PeriodOfOne_AdvancesExactlyOneRepetitionPerWorldUnit()
        {
            // The defining case: pattern coordinate IS world distance. A fill covering one world unit covers
            // exactly one repetition — which is what makes "period" the honest name for the parameter.
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));

            Assert.AreEqual(1.0,
                Fill.FillPattern.RepeatsPerWorldUnit(
                    r, Fill.FillPatternSizing.WorldAbsolute, 14.0, 1.0).x, 1e-12);
        }

        [Test]
        public void WorldAbsolute_PreservesSpriteAspect_SoANonSquareSpriteIsNotStretched()
        {
            // "tall" is 16×32, so at a 50-unit width period it must be 100 units tall — i.e. half as many
            // repetitions per world unit vertically.
            Assert.IsTrue(Fill.FillPattern.TryResolve("tall", Sheet(), out var r));
            double2 repeats = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.WorldAbsolute, 14.0, 50.0);

            Assert.AreEqual(repeats.x / 2.0, repeats.y, 1e-12,
                "the period applies along the sprite's WIDTH; the height follows the sprite's aspect, so a " +
                "16×32 sprite repeats half as often vertically instead of being squashed square");
        }

        [Test]
        public void PixelRatio_HalvesTheAuthoredSize_SoAn2xSpriteRepeatsTwiceAsOften()
        {
            // @2x sheets store a 16 css px sprite as 32 sheet px. Without the divisor an @2x sheet would draw
            // its patterns at double size.
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza",   Sheet(), out var at1x));
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza2x", Sheet(), out var at2x));

            double r1 = Fill.FillPattern.RepeatsPerWorldUnit(
                at1x, Fill.FillPatternSizing.ScreenRelative, 14.0, 0.0).x;
            double r2 = Fill.FillPattern.RepeatsPerWorldUnit(
                at2x, Fill.FillPatternSizing.ScreenRelative, 14.0, 0.0).x;

            Assert.AreEqual(2.0, r2 / r1, 1e-9);
            Assert.AreEqual(at1x.Rect.z, at2x.Rect.z,
                "pixelRatio must NOT change the sheet-pixel rect — only the authored size");
        }

        [Test]
        public void WorldAbsolute_WithNoPeriod_FallsBackToScreenRelativeRatherThanDividingByZero()
        {
            Assert.IsTrue(Fill.FillPattern.TryResolve("plaza", Sheet(), out var r));

            double unset = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.WorldAbsolute, 14.0, 0.0).x;
            double screen = Fill.FillPattern.RepeatsPerWorldUnit(
                r, Fill.FillPatternSizing.ScreenRelative, 14.0, 0.0).x;

            Assert.AreEqual(screen, unset, 1e-12,
                "WorldAbsolute with no period must degrade to the spec behaviour, not to infinity");
            Assert.IsFalse(double.IsInfinity(unset) || double.IsNaN(unset));
        }

        [Test]
        public void Unresolved_YieldsZeroRepeats_InEitherMode()
        {
            var unresolved = Fill.FillPattern.Resolution.Unresolved;
            foreach (var sizing in new[] { Fill.FillPatternSizing.ScreenRelative,
                                           Fill.FillPatternSizing.WorldAbsolute })
                Assert.AreEqual(0.0,
                    Fill.FillPattern.RepeatsPerWorldUnit(unresolved, sizing, 14.0, 50.0).x, 1e-12,
                    $"an unresolved pattern has no period to invert ({sizing})");
        }

        [Test]
        public void ScreenRelativeIsTheDefaultMode_BecauseThatIsWhatAMapLibreStyleMeans()
        {
            Assert.AreEqual(0, (int)Fill.FillPatternSizing.ScreenRelative,
                "ScreenRelative must be the zero/default enum value — a stock MapLibre style has no way to " +
                "ask for world-absolute sizing, so the default must be the spec behaviour.");
        }

        // ── The sizing mode is parsed from the style ─────────────────────────────────────────────

        [Test]
        public void Paint_NoSizeKey_IsScreenRelative_TheSpecBehaviour()
        {
            var paint = TestStyle.FillPaint(@"{""fill-pattern"":""plaza""}");

            Assert.AreEqual(Fill.FillPatternSizing.ScreenRelative, paint.PatternSizing,
                "every stock MapLibre style must mean screen-relative — it has no way to ask for anything else");
            Assert.AreEqual(0.0, paint.PatternWorldPeriodMetres, 1e-9);
        }

        [Test]
        public void Paint_ExtensionSizeKey_SwitchesToWorldAbsolute()
        {
            var paint = TestStyle.FillPaint(@"{""fill-pattern"":""plaza"", ""x-fill-pattern-metres"": 25.5}");

            Assert.AreEqual(Fill.FillPatternSizing.WorldAbsolute, paint.PatternSizing,
                "a ground size is only meaningful under world-absolute sizing, so supplying one selects it — " +
                "the two cannot disagree because they are one key");
            Assert.AreEqual(25.5, paint.PatternWorldPeriodMetres, 1e-9);
        }

        [Test]
        public void Paint_UnusableSizeValue_FallsBackToSpecBehaviour()
        {
            // Forward-compat posture, matching the rest of this parser: an unreadable EXTENSION must never
            // cost the layer its rendering. Zero and negative are unusable as a divisor; a string is garbage.
            foreach (string value in new[] { "0", "-4", "\"big\"" })
            {
                var paint = TestStyle.FillPaint($@"{{""fill-pattern"":""plaza"", ""x-fill-pattern-metres"": {value}}}");

                Assert.AreEqual(Fill.FillPatternSizing.ScreenRelative, paint.PatternSizing,
                    $"x-fill-pattern-metres={value} is unusable and must degrade to screen-relative, not throw");
                Assert.AreEqual(0.0, paint.PatternWorldPeriodMetres, 1e-9);
            }
        }

        [Test]
        public void Paint_ExtensionKeyCountsAsPresent_SoTheLayerIsNotInert()
        {
            // IsInertFallback drives "this layer declared nothing" short-circuits; an extension-only paint
            // block HAS declared something, so treating it as inert would silently drop the layer.
            var paint = TestStyle.FillPaint(@"{""x-fill-pattern-metres"": 10}");
            Assert.IsFalse(paint.IsInertFallback);
        }

        // ── The paint side of the same defect ────────────────────────────────────────────────────

        [Test]
        public void PatternLayerWithoutFillColor_StillCarriesTheOpaqueBlackDefault()
        {
            // Pins WHY the shader must clip rather than paint: this is Liberty's road_area_pattern verbatim,
            // and its Color evaluates to opaque black. Nothing here is wrong — the spec default IS black —
            // which is precisely why "unresolved" cannot be allowed to fall through to the colour path.
            var paint = TestStyle.FillPaint(@"{""fill-pattern"":""pedestrian_polygon""}");

            Assert.AreEqual("pedestrian_polygon", paint.PatternName);
            var color = paint.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, color.R, 1e-9, "fill-color default is opaque black …");
            Assert.AreEqual(0.0, color.G, 1e-9);
            Assert.AreEqual(0.0, color.B, 1e-9);
            Assert.AreEqual(1.0, color.A, 1e-9, "… fully opaque — hence solid black regions, not faint ones");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // FillExtrusionPaintTests — FillExtrusion.PaintProperties: classification + spec defaults
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S23 I1 — <see cref="FillExtrusion.PaintProperties"/>: classification + spec defaults, mirroring
    /// <c>FillPaintTests</c>. Also pins <see cref="StyleParser"/>'s <c>"fill-extrusion"</c> dispatch to the
    /// typed <see cref="FillExtrusion.StyleLayer"/> subclass.
    /// </summary>
    [TestFixture]
    public class FillExtrusionPaintTests
    {
        private static FillExtrusion.StyleLayer MakeLayer(string paintJson, string sourceLayer = "buildings")
        {
            return new FillExtrusion.StyleLayer
            {
                Id          = "test-fill-extrusion",
                LayerType   = StyleLayerType.FillExtrusion,
                SourceLayer = sourceLayer,
                Paint       = TestStyle.FillExtrusionPaint(paintJson),
            };
        }

        // ── height ────────────────────────────────────────────────────────────

        [Test]
        public void Height_ConstantValue_ClassifiesAsConstantAndPins()
        {
            var layer = MakeLayer("{\"fill-extrusion-height\":42}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.HeightKind, "a numeric literal must classify as Constant.");
            Assert.AreEqual(42f, fp.Height.Evaluate(0.0), 1e-4f, "fill-extrusion-height must pin to its literal value.");
            Assert.IsFalse(fp.IsInertFallback, "a layer with fill-extrusion-height set is not inert.");
        }

        [Test]
        public void Height_Absent_UsesSpecDefault_Zero()
        {
            var layer = MakeLayer("{}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.HeightKind, "absent fill-extrusion-height must use spec default (Constant kind).");
            Assert.AreEqual(0f, fp.Height.Evaluate(0.0), 1e-6f, "default fill-extrusion-height must be 0.");
            Assert.IsTrue(fp.IsInertFallback, "a layer with no paint properties set must be flagged IsInertFallback.");
        }

        [Test]
        public void Height_ZoomExpression_ClassifiesAsZoom()
        {
            const string paintJson =
                "{\"fill-extrusion-height\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0,16,50]}";
            var layer = MakeLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Zoom, fp.HeightKind, "a zoom-interpolate expression must classify as Zoom.");
            Assert.AreEqual(0f, fp.Height.Evaluate(10.0), 0.01f);
            Assert.AreEqual(50f, fp.Height.Evaluate(16.0), 0.01f);
        }

        [Test]
        public void Height_DataDriven_ClassifiesAsFeature()
        {
            const string paintJson = "{\"fill-extrusion-height\":[\"get\",\"height\"]}";
            var layer = MakeLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Feature, fp.HeightKind, "a [\"get\",...] expression must classify as Feature.");
            Assert.IsTrue(fp.Height.DependsOnFeature);
        }

        // ── base ──────────────────────────────────────────────────────────────

        [Test]
        public void Base_ConstantValue_ClassifiesAsConstantAndPins()
        {
            var layer = MakeLayer("{\"fill-extrusion-base\":5}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.BaseKind);
            Assert.AreEqual(5f, fp.Base.Evaluate(0.0), 1e-4f, "fill-extrusion-base must pin to its literal value.");
        }

        [Test]
        public void Base_Absent_UsesSpecDefault_Zero()
        {
            var layer = MakeLayer("{}");
            var fp    = layer.Paint;

            Assert.AreEqual(0f, fp.Base.Evaluate(0.0), 1e-6f, "default fill-extrusion-base must be 0.");
        }

        [Test]
        public void Base_ZoomExpression_ClassifiesAsZoom()
        {
            const string paintJson =
                "{\"fill-extrusion-base\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0,16,4]}";
            var layer = MakeLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Zoom, fp.BaseKind, "a zoom-interpolate expression must classify as Zoom.");
            Assert.AreEqual(0f, fp.Base.Evaluate(10.0), 0.01f);
            Assert.AreEqual(4f, fp.Base.Evaluate(16.0), 0.01f);
        }

        [Test]
        public void Base_DataDriven_ClassifiesAsFeature()
        {
            const string paintJson = "{\"fill-extrusion-base\":[\"get\",\"min_height\"]}";
            var layer = MakeLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Feature, fp.BaseKind, "a [\"get\",...] expression must classify as Feature.");
            Assert.IsTrue(fp.Base.DependsOnFeature);
        }

        // ── color ─────────────────────────────────────────────────────────────

        [Test]
        public void Color_Constant_ClassifiesAsConstantAndPins()
        {
            var layer = MakeLayer("{\"fill-extrusion-color\":[\"rgba\",200,100,50,1]}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.ColorKind);
            var c = fp.Color.Evaluate(0.0);
            Assert.AreEqual(200.0 / 255.0, c.R, 1e-3, "Red channel must match rgba(200,100,50,1).");
            Assert.AreEqual(100.0 / 255.0, c.G, 1e-3, "Green channel must match rgba(200,100,50,1).");
            Assert.AreEqual(50.0 / 255.0, c.B, 1e-3, "Blue channel must match rgba(200,100,50,1).");
        }

        [Test]
        public void Color_Absent_UsesSpecDefault_Black()
        {
            var layer = MakeLayer("{}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.ColorKind, "absent fill-extrusion-color must use spec default (Constant kind).");
            var c = fp.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Default fill-extrusion-color R must be 0 (black).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Default fill-extrusion-color G must be 0 (black).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Default fill-extrusion-color B must be 0 (black).");
            Assert.AreEqual(1.0, c.A, 1e-4, "Default fill-extrusion-color must be fully opaque.");
        }

        [Test]
        public void Color_ZoomExpression_ClassifiesAsZoom()
        {
            const string paintJson =
                "{\"fill-extrusion-color\":[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "10,[\"rgba\",255,0,0,1],16,[\"rgba\",0,0,255,1]]}";
            var layer = MakeLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Zoom, fp.ColorKind, "a zoom-interpolate expression must classify as Zoom.");

            var atMin = fp.Color.Evaluate(10.0);
            Assert.AreEqual(1.0, atMin.R, 1e-3, "At zoom=10, interpolated color must be the first stop (red).");
            Assert.AreEqual(0.0, atMin.B, 1e-3);

            var atMax = fp.Color.Evaluate(16.0);
            Assert.AreEqual(0.0, atMax.R, 1e-3, "At zoom=16, interpolated color must be the second stop (blue).");
            Assert.AreEqual(1.0, atMax.B, 1e-3);
        }

        [Test]
        public void Color_DataDriven_ClassifiesAsFeature()
        {
            const string paintJson =
                "{\"fill-extrusion-color\":[\"match\",[\"get\",\"type\"]," +
                "\"residential\",[\"rgba\",200,50,50,1]," +
                "[\"rgba\",128,128,128,1]]}";
            var layer = MakeLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Feature, fp.ColorKind, "a [\"match\",[\"get\",...],...] expression must classify as Feature.");
            Assert.IsTrue(fp.Color.DependsOnFeature);
        }

        // ── opacity ───────────────────────────────────────────────────────────

        [Test]
        public void Opacity_Absent_UsesSpecDefault_One()
        {
            var layer = MakeLayer("{}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.OpacityKind);
            Assert.AreEqual(1.0f, fp.Opacity.Evaluate(0.0), 1e-6f, "default fill-extrusion-opacity must be 1.0.");
        }

        [Test]
        public void Opacity_Constant_Pins()
        {
            var layer = MakeLayer("{\"fill-extrusion-opacity\":0.5}");
            var fp    = layer.Paint;

            Assert.AreEqual(0.5f, fp.Opacity.Evaluate(0.0), 1e-6f);
        }

        [Test]
        public void Opacity_ZoomExpression_ClassifiesAsZoom()
        {
            const string paintJson =
                "{\"fill-extrusion-opacity\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0.2,16,1.0]}";
            var layer = MakeLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Zoom, fp.OpacityKind, "a zoom-interpolate expression must classify as Zoom.");
            Assert.AreEqual(0.2f, fp.Opacity.Evaluate(10.0), 0.01f);
            Assert.AreEqual(1.0f, fp.Opacity.Evaluate(16.0), 0.01f);
        }

        [Test]
        public void Opacity_DataDriven_ClassifiesAsFeature()
        {
            const string paintJson = "{\"fill-extrusion-opacity\":[\"get\",\"opacity\"]}";
            var layer = MakeLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Feature, fp.OpacityKind, "a [\"get\",...] expression must classify as Feature.");
            Assert.IsTrue(fp.Opacity.DependsOnFeature);
        }

        // ── vertical-gradient ─────────────────────────────────────────────────

        [Test]
        public void VerticalGradient_Absent_UsesSpecDefault_True()
        {
            var layer = MakeLayer("{}");
            var fp    = layer.Paint;

            Assert.AreEqual(1.0f, fp.VerticalGradient.Evaluate(0.0), 1e-6f,
                "default fill-extrusion-vertical-gradient must encode 'true' as 1.0.");
        }

        [Test]
        public void VerticalGradient_False_EncodesAsZero()
        {
            var layer = MakeLayer("{\"fill-extrusion-vertical-gradient\":false}");
            var fp    = layer.Paint;

            Assert.AreEqual(0.0f, fp.VerticalGradient.Evaluate(0.0), 1e-6f,
                "fill-extrusion-vertical-gradient:false must encode as 0.0.");
        }

        // ── translate-anchor ──────────────────────────────────────────────────

        [Test]
        public void TranslateAnchor_Viewport_IsOne()
        {
            var layer = MakeLayer("{\"fill-extrusion-translate-anchor\":\"viewport\"}");
            var fp    = layer.Paint;

            Assert.AreEqual(1.0f, fp.TranslateAnchor.Evaluate(0.0), 1e-6f,
                "fill-extrusion-translate-anchor 'viewport' must encode as 1.0.");
        }

        [Test]
        public void TranslateAnchor_AbsentDefaultsToMap_IsZero()
        {
            var layer = MakeLayer("{}");
            var fp    = layer.Paint;

            Assert.AreEqual(0.0f, fp.TranslateAnchor.Evaluate(0.0), 1e-6f,
                "absent fill-extrusion-translate-anchor must default to 'map' (0.0).");
        }

        // ── translate (I2b — parsed through the expression engine; mirrors the Height teeth above) ──

        [Test]
        public void Translate_ConstantValue_ClassifiesAsConstantAndPins()
        {
            // A bare [x,y] array — the common constant form real styles use — must parse (via
            // WrapBareArrayLiterals) rather than throw "operator must be a string".
            var layer = MakeLayer("{\"fill-extrusion-translate\":[12,-4]}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.TranslateKind);
            var t = fp.Translate.Evaluate(0.0);
            Assert.AreEqual(12.0, t.x, 1e-6, "fill-extrusion-translate.x must pin to its literal value.");
            Assert.AreEqual(-4.0, t.y, 1e-6, "fill-extrusion-translate.y must pin to its literal value.");
            Assert.IsFalse(fp.IsInertFallback, "a layer with fill-extrusion-translate set is not inert.");
        }

        [Test]
        public void Translate_Absent_UsesSpecDefault_Zero()
        {
            var layer = MakeLayer("{}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.TranslateKind);
            var t = fp.Translate.Evaluate(0.0);
            Assert.AreEqual(0.0, t.x, 1e-6);
            Assert.AreEqual(0.0, t.y, 1e-6);
        }

        [Test]
        public void Translate_ZoomExpression_ClassifiesAsZoom_NotCollapsedToDefault()
        {
            // The array-valued stop outputs must be wrapped ["literal", [...]] per the Style Spec — the
            // engine's InterpolateExpression.Lerp handles ValueType.Array element-wise (Ops/Ramps.cs).
            const string paintJson =
                "{\"fill-extrusion-translate\":[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "10,[\"literal\",[0,0]],16,[\"literal\",[20,-10]]]}";
            var layer = MakeLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Zoom, fp.TranslateKind,
                "a zoom-interpolate translate must classify as Zoom, not collapse to a Constant [0,0].");

            var atMin = fp.Translate.Evaluate(10.0);
            Assert.AreEqual(0.0, atMin.x, 0.01);
            Assert.AreEqual(0.0, atMin.y, 0.01);

            var atMax = fp.Translate.Evaluate(16.0);
            Assert.AreEqual(20.0, atMax.x, 0.01,
                "at zoom=16 the interpolated translate must reach its second stop, NOT stay at [0,0].");
            Assert.AreEqual(-10.0, atMax.y, 0.01);
        }

        [Test]
        public void Translate_DataDriven_FallsBackToDefault()
        {
            // fill-extrusion-translate is a layer-level property; a data-driven value is spec-invalid and
            // must fall back to the default rather than throw at bind time (mirrors VerticalGradient).
            var layer = MakeLayer("{\"fill-extrusion-translate\":[\"get\",\"offset\"]}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.TranslateKind,
                "a data-driven translate must fall back to the Constant default, not classify as Feature.");
            var t = fp.Translate.Evaluate(0.0);
            Assert.AreEqual(0.0, t.x, 1e-6);
            Assert.AreEqual(0.0, t.y, 1e-6);
        }

        // ── inert fallback / null paint ───────────────────────────────────────

        [Test]
        public void NullPaint_IsInertFallback()
        {
            var layer = MakeLayer(null);
            var fp    = layer.Paint;

            Assert.IsTrue(fp.IsInertFallback, "a layer with null Paint must be IsInertFallback.");
        }

        // ── StyleParser dispatch (T4 Core half) ──────────────────────────────────

        [Test]
        public void StyleParser_ParseLayer_FillExtrusion_YieldsTypedSubclass()
        {
            const string json = @"{
              ""version"": 8,
              ""sources"": { ""s"": { ""type"": ""vector"", ""url"": ""u"" } },
              ""layers"": [
                { ""id"": ""buildings-3d"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""buildings"",
                  ""paint"": { ""fill-extrusion-height"": 30 } }
              ]
            }";

            var doc = StyleParser.Parse(json);

            Assert.AreEqual(1, doc.Layers.Count);
            Assert.AreEqual(StyleLayerType.FillExtrusion, doc.Layers[0].LayerType);
            Assert.IsInstanceOf<FillExtrusion.StyleLayer>(doc.Layers[0],
                "'fill-extrusion' must dispatch to its typed subclass, like fill/line/symbol/background.");

            var fe = (FillExtrusion.StyleLayer)doc.Layers[0];
            Assert.AreEqual(30f, fe.Paint.Height.Evaluate(0.0), 1e-4f,
                "the typed subclass's eagerly-parsed Paint must read the real paint sub-tree.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // LinePaintTests — Line.PaintProperties/LayoutProperties: classification, translate, join/cap layout
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S14 / S60 — <see cref="Line.PaintProperties"/> / <see cref="Line.LayoutProperties"/>:
    /// classification, pinned values, translate-array parse, anchor encoding, pattern-name capture,
    /// join/cap layout parse (now typed enums), and inert-fallback.
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    ///
    /// S60 changes:
    ///   • line-color/opacity/width/blur/gap-width/offset: single <c>StyleProperty&lt;T&gt;</c>
    ///     (no more DataDrivenX / XKind fields; ColorKind etc. are convenience aliases for .Kind).
    ///   • Data-driven gate: null check → <c>.DependsOnFeature</c>.
    ///   • line-translate: ONE <c>StyleProperty&lt;double2&gt;</c>; access via <c>.Translate.Evaluate(0.0).x/y</c>.
    ///   • line-translate-anchor: <c>StyleProperty&lt;float&gt;</c>.
    ///   • line-join / line-cap: <c>JoinType</c> / <c>CapType</c> enums on LayoutProperties.
    /// </summary>
    [TestFixture]
    public class LinePaintTests
    {
        private static Line.StyleLayer MakeLineLayer(string paintJson, string layoutJson = null,
            string sourceLayer = "roads")
        {
            return new Line.StyleLayer
            {
                Id          = "test-line",
                LayerType   = StyleLayerType.Line,
                SourceLayer = sourceLayer,
                Paint       = TestStyle.LinePaint(paintJson),
                Layout      = TestStyle.LineLayout(layoutJson),
            };
        }

        // ── #1: Constant line-color → Constant kind, pinned RGB ─────────────────

        [Test]
        public void LinePaint_ConstantColor_ClassifiesAsConstant()
        {
            var layer = MakeLineLayer("{\"line-color\":[\"rgba\",255,0,0,1]}");
            var lp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, lp.ColorKind,
                "An rgba(...) literal must classify as Constant.");
            Assert.IsFalse(lp.Color.DependsOnFeature,
                "Constant color must not depend on feature.");
            Assert.IsFalse(lp.IsInertFallback,
                "A layer with line-color set is not inert.");

            // Pinned: rgba(255,0,0,1) → R=1, G=0, B=0.
            var c = lp.Color.Evaluate(0.0);
            Assert.AreEqual(1.0, c.R, 1e-4, "Red channel must be 1.0 for rgba(255,0,0,1).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Green channel must be 0.0.");
            Assert.AreEqual(0.0, c.B, 1e-4, "Blue channel must be 0.0.");
        }

        // ── #1: Zoom-dependent line-width → Zoom kind, sampled values pinned ────

        [Test]
        public void LinePaint_ZoomWidth_ClassifiesAsZoom_SampledValuesPinned()
        {
            const string paintJson =
                "{\"line-width\":[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,10.0]}";
            var layer = MakeLineLayer(paintJson);
            var lp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Zoom, lp.WidthKind,
                "A zoom-interpolate expression must classify as Zoom.");
            Assert.IsFalse(lp.Width.DependsOnFeature, "Zoom width must not depend on feature.");

            float v5 = lp.Width.Evaluate(5.0);
            Assert.AreEqual(2.0f, v5, 0.01f, "At zoom=5, interpolated width must be 2.0.");

            float v15 = lp.Width.Evaluate(15.0);
            Assert.AreEqual(10.0f, v15, 0.01f, "At zoom=15, interpolated width must be 10.0.");

            float v10 = lp.Width.Evaluate(10.0);
            Assert.Greater(v10, 2.0f, "At zoom=10, width must be > 2.0.");
            Assert.Less(v10, 10.0f, "At zoom=10, width must be < 10.0.");
        }

        // ── Feature-dependent line-color → Feature kind ──────────────────────────

        [Test]
        public void LinePaint_DataDrivenColor_ClassifiesAsFeature()
        {
            const string paintJson =
                "{\"line-color\":[\"match\",[\"get\",\"road_class\"]," +
                "\"motorway\",[\"rgba\",200,50,50,1]," +
                "[\"rgba\",128,128,128,1]]}";
            var layer = MakeLineLayer(paintJson);
            var lp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Feature, lp.ColorKind,
                "A [\"get\",...] match expression must classify as Feature.");
            Assert.IsTrue(lp.Color.DependsOnFeature,
                "Data-driven color must DependsOnFeature.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        // ── Absent line-color → Constant kind (spec default #000000) ────────────

        [Test]
        public void LinePaint_AbsentColor_UsesSpecDefault_Black()
        {
            var layer = MakeLineLayer("{}");
            var lp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, lp.ColorKind,
                "Absent line-color must use spec default (Constant kind).");
            Assert.IsFalse(lp.Color.DependsOnFeature);

            var c = lp.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Default line-color R must be 0 (black).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Default line-color G must be 0 (black).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Default line-color B must be 0 (black).");

            Assert.IsTrue(lp.IsInertFallback,
                "A layer with no paint properties set must be flagged IsInertFallback.");
        }

        // ── Absent line-opacity → Constant 1.0 ─────────────────────────────────

        [Test]
        public void LinePaint_AbsentOpacity_UsesSpecDefault_One()
        {
            var layer = MakeLineLayer("{}");
            var lp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, lp.OpacityKind);
            float v = lp.Opacity.Evaluate(0.0);
            Assert.AreEqual(1.0f, v, 1e-6f, "Default line-opacity must be 1.0.");
        }

        // ── Absent line-width → Constant 1.0 ───────────────────────────────────

        [Test]
        public void LinePaint_AbsentWidth_UsesSpecDefault_One()
        {
            var layer = MakeLineLayer("{}");
            var lp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, lp.WidthKind);
            float v = lp.Width.Evaluate(0.0);
            Assert.AreEqual(1.0f, v, 1e-6f, "Default line-width must be 1.0.");
        }

        // ── Absent line-blur → Constant 0 ──────────────────────────────────────

        [Test]
        public void LinePaint_AbsentBlur_UsesSpecDefault_Zero()
        {
            var layer = MakeLineLayer("{}");
            var lp    = layer.Paint;

            float v = lp.Blur.Evaluate(0.0);
            Assert.AreEqual(0.0f, v, 1e-6f, "Default line-blur must be 0.0.");
        }

        // ── Absent line-gap-width → Constant 0 ─────────────────────────────────

        [Test]
        public void LinePaint_AbsentGapWidth_UsesSpecDefault_Zero()
        {
            var layer = MakeLineLayer("{}");
            var lp    = layer.Paint;

            float v = lp.GapWidth.Evaluate(0.0);
            Assert.AreEqual(0.0f, v, 1e-6f, "Default line-gap-width must be 0.0.");
        }

        // ── line-gap-width present → parsed ────────────────────────────────────

        [Test]
        public void LinePaint_GapWidth_Present_Parsed()
        {
            var layer = MakeLineLayer("{\"line-gap-width\":8.0}");
            var lp    = layer.Paint;

            float v = lp.GapWidth.Evaluate(0.0);
            Assert.AreEqual(8.0f, v, 1e-6f, "line-gap-width must be 8.0.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        // ── line-translate [16, -8] → double2 pinned ───────────────────────────

        [Test]
        public void LinePaint_Translate_ComponentsArePinned()
        {
            var layer = MakeLineLayer("{\"line-translate\":[16,-8]}");
            var lp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, lp.TranslateKind);
            var t = lp.Translate.Evaluate(0.0);
            Assert.AreEqual(16.0, t.x, 1e-6, "line-translate x must be 16.");
            Assert.AreEqual(-8.0, t.y, 1e-6, "line-translate y must be -8.");
        }

        // ── line-translate-anchor "viewport" → 1.0 ─────────────────────────────

        [Test]
        public void LinePaint_TranslateAnchorViewport_IsOne()
        {
            var layer = MakeLineLayer("{\"line-translate-anchor\":\"viewport\"}");
            var lp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, lp.TranslateAnchorKind);
            float v = lp.TranslateAnchor.Evaluate(0.0);
            Assert.AreEqual(1.0f, v, 1e-6f, "line-translate-anchor 'viewport' must encode as 1.0.");
        }

        // ── line-translate-anchor "map" → 0.0 ──────────────────────────────────

        [Test]
        public void LinePaint_TranslateAnchorMap_IsZero()
        {
            var layer = MakeLineLayer("{\"line-translate-anchor\":\"map\"}");
            var lp    = layer.Paint;

            float v = lp.TranslateAnchor.Evaluate(0.0);
            Assert.AreEqual(0.0f, v, 1e-6f, "line-translate-anchor 'map' must encode as 0.0.");
        }

        // ── #D4: line-join, line-cap from layout (now typed enums) ──────────────

        [Test]
        public void LinePaint_LayoutJoinCap_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-join\":\"round\",\"line-cap\":\"square\"}");
            var lo    = layer.Layout;

            Assert.AreEqual(JoinType.Round,  lo.Join, "line-join='round' must parse to JoinType.Round.");
            Assert.AreEqual(CapType.Square, lo.Cap,  "line-cap='square' must parse to CapType.Square.");
        }

        [Test]
        public void LinePaint_LayoutJoinBevel_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-join\":\"bevel\"}");
            var lo    = layer.Layout;

            Assert.AreEqual(JoinType.Bevel, lo.Join, "line-join='bevel' must parse to JoinType.Bevel.");
        }

        [Test]
        public void LinePaint_LayoutCapRound_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-cap\":\"round\"}");
            var lo    = layer.Layout;

            Assert.AreEqual(CapType.Round, lo.Cap, "line-cap='round' must parse to CapType.Round.");
        }

        [Test]
        public void LinePaint_LayoutMiterLimit_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-miter-limit\":5.0}");
            var lo    = layer.Layout;

            Assert.AreEqual(5.0, lo.MiterLimit, 1e-6, "line-miter-limit must be parsed from layout.");
        }

        [Test]
        public void LinePaint_LayoutRoundLimit_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-round-limit\":1.2}");
            var lo    = layer.Layout;

            Assert.AreEqual(1.2, lo.RoundLimit, 1e-6, "line-round-limit must be parsed from layout.");
        }

        [Test]
        public void LinePaint_AbsentLayout_UsesSpecDefaults()
        {
            var layer = MakeLineLayer("{}");
            var lo    = layer.Layout;

            Assert.AreEqual(JoinType.Miter, lo.Join,       "Default line-join must be JoinType.Miter.");
            Assert.AreEqual(CapType.Butt,   lo.Cap,        "Default line-cap must be CapType.Butt.");
            Assert.AreEqual(2.0,            lo.MiterLimit, 1e-6, "Default miter-limit must be 2.0.");
            Assert.AreEqual(1.05,           lo.RoundLimit, 1e-6, "Default round-limit must be 1.05.");
        }

        // ── S44: line-offset ─────────────────────────────────────────────────────

        [Test]
        public void LinePaint_Offset_ConstantValue_IsParsed()
        {
            var layer = MakeLineLayer("{\"line-offset\":5}");
            var lp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, lp.OffsetKind,
                "A numeric line-offset must classify as Constant.");
            float v = lp.Offset.Evaluate(0.0);
            Assert.AreEqual(5.0f, v, 1e-6f, "line-offset must evaluate to 5.0.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        [Test]
        public void LinePaint_Offset_Absent_DefaultsToZero()
        {
            var layer = MakeLineLayer("{}");
            var lp    = layer.Paint;

            float v = lp.Offset.Evaluate(0.0);
            Assert.AreEqual(0.0f, v, 1e-6f, "Absent line-offset must default to 0.0.");
        }

        [Test]
        public void LinePaint_Offset_NegativeValue_IsParsed()
        {
            var layer = MakeLineLayer("{\"line-offset\":-3}");
            var lp    = layer.Paint;

            float v = lp.Offset.Evaluate(0.0);
            Assert.AreEqual(-3.0f, v, 1e-6f, "Negative line-offset must parse correctly.");
        }

        [Test]
        public void LinePaint_Offset_ZoomInterpolate_ClassifiesAsZoom()
        {
            var json  = "{\"line-offset\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0,14,8]}";
            var layer = MakeLineLayer(json);
            var lp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Zoom, lp.OffsetKind,
                "A zoom-interpolate line-offset must classify as Zoom kind.");
        }

        // ── #5: line-pattern hook → PatternName is set ──────────────────────────

        [Test]
        public void LinePaint_PatternName_IsParsed()
        {
            var layer = MakeLineLayer("{\"line-pattern\":\"road_shield\"}");
            var lp    = layer.Paint;

            Assert.AreEqual("road_shield", lp.PatternName, "line-pattern must be read as PatternName.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        // ── Null paint → IsInertFallback ────────────────────────────────────────

        [Test]
        public void LinePaint_NullPaint_IsInertFallback()
        {
            var layer = MakeLineLayer(null);
            var lp    = layer.Paint;

            Assert.IsTrue(lp.IsInertFallback, "A layer with null Paint must be IsInertFallback.");
        }

        // ── S60: PropertyNames value constants ──────────────────────────────────

        [Test]
        public void PropertyNames_ValueConstants_AreCorrect()
        {
            Assert.AreEqual("butt",   Line.PropertyNames.CapButt);
            Assert.AreEqual("round",  Line.PropertyNames.CapRound);
            Assert.AreEqual("square", Line.PropertyNames.CapSquare);
            Assert.AreEqual("miter",  Line.PropertyNames.JoinMiter);
            Assert.AreEqual("round",  Line.PropertyNames.JoinRound);
            Assert.AreEqual("bevel",  Line.PropertyNames.JoinBevel);
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // BackgroundPaintTests — Background.PaintProperties: spec defaults, explicit parse, zoom classification
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// E3 — <see cref="Background.PaintProperties"/>: spec defaults, explicit parse, and zoom
    /// classification (the Fill pattern, <see cref="FillPaintTests"/>).
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    /// </summary>
    [TestFixture]
    public class BackgroundPaintTests
    {
        private static Background.StyleLayer MakeBackgroundLayer(string paintJson)
        {
            return new Background.StyleLayer
            {
                Id        = "test-background",
                LayerType = StyleLayerType.Background,
                Paint     = TestStyle.BackgroundPaint(paintJson),
            };
        }

        // ── Defaults when paint is absent ────────────────────────────────────────

        [Test]
        public void BackgroundPaint_AbsentPaint_UsesSpecDefaults()
        {
            var layer = MakeBackgroundLayer("{}");
            var bp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, bp.Color.Kind);
            var c = bp.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Default background-color R must be 0 (black).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Default background-color G must be 0 (black).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Default background-color B must be 0 (black).");
            Assert.AreEqual(1.0, c.A, 1e-4, "Default background-color A must be 1 (opaque).");

            Assert.AreEqual(1.0f, bp.Opacity.Evaluate(0.0), 1e-6f, "Default background-opacity must be 1.0.");
            Assert.IsNull(bp.PatternName, "Default background-pattern must be null.");
            Assert.IsTrue(bp.IsInertFallback, "A layer with no paint properties set must be IsInertFallback.");
        }

        [Test]
        public void BackgroundPaint_NullPaint_IsInertFallback()
        {
            var layer = MakeBackgroundLayer(null);
            var bp    = layer.Paint;

            Assert.IsTrue(bp.IsInertFallback, "A layer with null Paint must be IsInertFallback.");
        }

        // ── Explicit parse ────────────────────────────────────────────────────────

        [Test]
        public void BackgroundPaint_ExplicitColorString_Parses()
        {
            var layer = MakeBackgroundLayer("{\"background-color\":\"#ff0000\"}");
            var bp    = layer.Paint;

            var c = bp.Color.Evaluate(0.0);
            Assert.AreEqual(1.0, c.R, 1e-4, "Red channel must be 1.0 for #ff0000.");
            Assert.AreEqual(0.0, c.G, 1e-4);
            Assert.AreEqual(0.0, c.B, 1e-4);
            Assert.IsFalse(bp.IsInertFallback);
        }

        [Test]
        public void BackgroundPaint_ExplicitColorRgbaArray_Parses()
        {
            var layer = MakeBackgroundLayer("{\"background-color\":[\"rgba\",0,255,0,1]}");
            var bp    = layer.Paint;

            var c = bp.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4);
            Assert.AreEqual(1.0, c.G, 1e-4, "Green channel must be 1.0 for rgba(0,255,0,1).");
            Assert.AreEqual(0.0, c.B, 1e-4);
        }

        [Test]
        public void BackgroundPaint_ExplicitOpacityNumber_Parses()
        {
            var layer = MakeBackgroundLayer("{\"background-opacity\":0.5}");
            var bp    = layer.Paint;

            Assert.AreEqual(0.5f, bp.Opacity.Evaluate(0.0), 1e-6f);
            Assert.IsFalse(bp.IsInertFallback);
        }

        [Test]
        public void BackgroundPaint_ExplicitPatternString_Parses()
        {
            var layer = MakeBackgroundLayer("{\"background-pattern\":\"stripes\"}");
            var bp    = layer.Paint;

            Assert.AreEqual("stripes", bp.PatternName);
            Assert.IsFalse(bp.IsInertFallback);
        }

        // ── Zoom classification ──────────────────────────────────────────────────

        [Test]
        public void BackgroundPaint_ZoomColor_ClassifiesAsZoom_AndEvaluatesDifferentlyAcrossZooms()
        {
            const string paintJson =
                "{\"background-color\":[\"interpolate\",[\"exponential\",1],[\"zoom\"]," +
                "0,\"#000000\",10,\"#ffffff\"]}";
            var layer = MakeBackgroundLayer(paintJson);
            var bp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Zoom, bp.Color.Kind,
                "A zoom-interpolate background-color must classify as Zoom.");

            var atZero = bp.Color.Evaluate(0.0);
            var atTen  = bp.Color.Evaluate(10.0);
            Assert.AreEqual(0.0, atZero.R, 1e-4, "At zoom=0, background-color must be black.");
            Assert.AreEqual(1.0, atTen.R, 1e-4, "At zoom=10, background-color must be white.");
            Assert.AreNotEqual(atZero.R, atTen.R, "A zoom-dependent background-color must evaluate differently at two zooms.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolStyleLayerTests — a symbol layer parses to the typed Symbol.StyleLayer with spec defaults
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S105 Slice 1 (A1): a <c>symbol</c> layer parses to the typed <see cref="SymbolStyle.StyleLayer"/> (NOT the
    /// generic base) with its <c>text-*</c>/<c>symbol-*</c> paint+layout values — and an EMPTY symbol layer
    /// yields every MapLibre spec DEFAULT. Engine-free; runs in both runners.
    /// </summary>
    [TestFixture]
    public class SymbolStyleLayerTests
    {
        // Author JSON with single quotes for readability, then swap to real quotes.
        private static StyleDocument Parse(string json) => StyleParser.Parse(json.Replace('\'', '"'));

        private const string StyleJson = @"{
            'version': 8,
            'layers': [
                { 'id':'labels', 'type':'symbol', 'source':'src', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':24, 'symbol-sort-key':3,
                              'symbol-spacing':180, 'text-max-angle':30, 'text-keep-upright':false,
                              'text-allow-overlap':true, 'text-padding':5 },
                  'paint':  { 'text-color':'#ff0000', 'text-halo-color':'#ffffff', 'text-halo-width':1.5 } },
                { 'id':'bare', 'type':'symbol', 'source':'src', 'source-layer':'centroids' }
            ]
        }";

        [Test]
        public void SymbolLayer_ParsesToTypedSubclass_WithExpectedValues()
        {
            StyleDocument doc = Parse(StyleJson);
            Assert.AreEqual(2, doc.Layers.Count);

            StyleLayer layer = doc.Layers[0];
            Assert.IsInstanceOf<SymbolStyle.StyleLayer>(layer,
                "a 'symbol' layer must parse to the typed Symbol.StyleLayer, not the generic base");
            var sym = (SymbolStyle.StyleLayer)layer;

            // text-field kept as raw JSON (resolved per-feature, not a scalar).
            Assert.IsNotNull(sym.Layout.TextField, "text-field must be retained (raw) for per-feature resolution");
            Assert.AreEqual("{NAME}", sym.Layout.TextField.AsString(null));

            // Layout scalars.
            Assert.AreEqual(24f, sym.Layout.TextSize.Evaluate(0.0), 1e-6);
            Assert.AreEqual(3f, sym.Layout.SymbolSortKey.Evaluate(0.0), 1e-6);
            Assert.AreEqual(180f, sym.Layout.SymbolSpacing.Evaluate(0.0), 1e-6, "symbol-spacing parses");
            Assert.AreEqual(30f, sym.Layout.TextMaxAngle.Evaluate(0.0), 1e-6, "text-max-angle parses");
            Assert.IsFalse(sym.Layout.TextKeepUpright, "text-keep-upright:false parses");
            Assert.AreEqual(5f, sym.Layout.TextPadding.Evaluate(0.0), 1e-6);
            Assert.IsTrue(sym.Layout.TextAllowOverlap, "text-allow-overlap:true must parse to true");

            // Paint.
            Assert.AreEqual(new Color(1, 0, 0, 1), sym.Paint.Color.Evaluate(0.0), "text-color #ff0000 → opaque red");
            Assert.AreEqual(new Color(1, 1, 1, 1), sym.Paint.HaloColor.Evaluate(0.0), "text-halo-color #ffffff → opaque white");
            Assert.AreEqual(1.5f, sym.Paint.HaloWidth.Evaluate(0.0), 1e-6);
        }

        [Test]
        public void SymbolLayer_EmptyLayoutAndPaint_YieldsSpecDefaults()
        {
            StyleDocument doc = Parse(StyleJson);
            var sym = (SymbolStyle.StyleLayer)doc.Layers[1];

            // Layout defaults.
            Assert.AreEqual(16f, sym.Layout.TextSize.Evaluate(0.0), 1e-6, "text-size default is 16");
            Assert.AreEqual(2f, sym.Layout.TextPadding.Evaluate(0.0), 1e-6,
                "text-padding default is 2 (the resolver applies the spec default; the record carrier's default is 0)");
            Assert.IsFalse(sym.Layout.TextAllowOverlap, "text-allow-overlap default is false");
            Assert.IsFalse(sym.Layout.TextIgnorePlacement, "text-ignore-placement default is false");
            Assert.AreEqual(SymbolPlacement.Point, sym.Layout.SymbolPlacement.Evaluate(0.0), "symbol-placement default is point");
            Assert.AreEqual(250f, sym.Layout.SymbolSpacing.Evaluate(0.0), 1e-6, "symbol-spacing default is 250");
            Assert.AreEqual(45f, sym.Layout.TextMaxAngle.Evaluate(0.0), 1e-6, "text-max-angle default is 45");
            Assert.IsTrue(sym.Layout.TextKeepUpright, "text-keep-upright default is true");

            // Paint defaults.
            Assert.AreEqual(0f, sym.Paint.HaloWidth.Evaluate(0.0), 1e-6, "text-halo-width default is 0");
            Assert.AreEqual(new Color(0, 0, 0, 1), sym.Paint.Color.Evaluate(0.0), "text-color default is opaque black");
            Assert.AreEqual(1f, sym.Paint.Opacity.Evaluate(0.0), 1e-6, "text-opacity default is 1");
            Assert.AreEqual(new float2(0, 0), sym.Paint.Translate, "text-translate default is [0,0]");
            Assert.AreEqual(TextTranslateAnchor.Map, sym.Paint.TranslateAnchor, "text-translate-anchor default is map");
            Assert.IsTrue(sym.Paint.IsInertFallback, "an empty paint sub-tree is an inert fallback");

            // Slice A layout defaults.
            Assert.AreEqual(TextAnchor.Center, sym.Layout.TextAnchor, "text-anchor default is center");
            Assert.AreEqual(TextJustify.Center, sym.Layout.TextJustify,
                "text-justify default is the spec 'center' — NOT the enum zero-value Auto");
            Assert.AreEqual(float2.zero, sym.Layout.TextOffset, "text-offset default is [0,0]");
            Assert.AreEqual(1.2f, sym.Layout.TextLineHeight.Evaluate(0.0), 1e-6, "text-line-height default is 1.2");
            Assert.AreEqual(0f, sym.Layout.TextLetterSpacing.Evaluate(0.0), 1e-6, "text-letter-spacing default is 0");
            Assert.AreEqual(0f, sym.Layout.TextRadialOffset.Evaluate(0.0), 1e-6, "text-radial-offset default is 0");
            Assert.AreEqual(TextTransform.None, sym.Layout.TextTransform, "text-transform default is none");
            Assert.AreEqual(AlignmentMode.Auto, sym.Layout.TextRotationAlignment, "text-rotation-alignment default is auto");
            Assert.AreEqual(AlignmentMode.Auto, sym.Layout.TextPitchAlignment, "text-pitch-alignment default is auto");

            // icon-* layout defaults.
            Assert.IsNull(sym.Layout.IconImage, "icon-image default is absent (null)");
            Assert.AreEqual(1f, sym.Layout.IconSize.Evaluate(0.0), 1e-6, "icon-size default is 1");
            Assert.AreEqual(2f, sym.Layout.IconPadding.Evaluate(0.0), 1e-6, "icon-padding default is 2");
            Assert.AreEqual(TextAnchor.Center, sym.Layout.IconAnchor, "icon-anchor default is center");
            Assert.AreEqual(AlignmentMode.Auto, sym.Layout.IconRotationAlignment, "icon-rotation-alignment default is auto");
            Assert.AreEqual(AlignmentMode.Auto, sym.Layout.IconPitchAlignment, "icon-pitch-alignment default is auto");
            Assert.IsFalse(sym.Layout.IconAllowOverlap, "icon-allow-overlap default is false");
            Assert.IsFalse(sym.Layout.IconIgnorePlacement, "icon-ignore-placement default is false");
            Assert.AreEqual(float2.zero, sym.Layout.IconOffset, "icon-offset default is [0,0]");

            // icon-opacity paint default.
            Assert.AreEqual(1f, sym.Paint.IconOpacity.Evaluate(0.0), 1e-6, "icon-opacity default is 1");
        }

        [Test]
        public void SymbolLayer_IconProperties_Parse()
        {
            var sym = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'icon-image':['get','icon'],'icon-size':2,'icon-offset':[3,4],'icon-anchor':'top-left',
                    'icon-rotation-alignment':'map','icon-allow-overlap':true,'icon-ignore-placement':true,
                    'icon-padding':5 },
                  'paint':{ 'icon-opacity':0.5 } } ] }").Layers[0];

            Assert.IsNotNull(sym.Layout.IconImage, "icon-image must be retained (raw) for per-feature resolution");
            Assert.IsTrue(sym.Layout.IconImage.IsArray, "icon-image data-driven expression survives as a raw array (resolution is I3)");
            Assert.AreEqual(2f, sym.Layout.IconSize.Evaluate(0.0), 1e-6);
            Assert.AreEqual(5f, sym.Layout.IconPadding.Evaluate(0.0), 1e-6);
            Assert.AreEqual(new float2(3, 4), sym.Layout.IconOffset);
            Assert.AreEqual(TextAnchor.TopLeft, sym.Layout.IconAnchor, "hyphenated 'top-left' → TopLeft");
            Assert.AreEqual(AlignmentMode.Map, sym.Layout.IconRotationAlignment);
            Assert.IsTrue(sym.Layout.IconAllowOverlap, "icon-allow-overlap:true must parse to true");
            Assert.IsTrue(sym.Layout.IconIgnorePlacement, "icon-ignore-placement:true must parse to true");

            Assert.AreEqual(0.5f, sym.Paint.IconOpacity.Evaluate(0.0), 1e-6);
            Assert.IsFalse(sym.Paint.IsInertFallback, "an icon-opacity-only paint sub-tree is NOT an inert fallback");
        }

        // ── C1 (stage C) — icon-optional / text-optional parse as plain layout booleans ────────────────────
        [Test]
        public void SymbolLayer_IconAndTextOptional_ParseWithSpecDefaultFalse()
        {
            var absent = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{ 'text-field':'{NAME}' } } ] }").Layers[0];
            Assert.IsFalse(absent.Layout.IconOptional, "icon-optional default is false (spec)");
            Assert.IsFalse(absent.Layout.TextOptional, "text-optional default is false (spec)");

            var set = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','icon-optional':true,'text-optional':true } } ] }").Layers[0];
            Assert.IsTrue(set.Layout.IconOptional, "icon-optional:true must parse to true");
            Assert.IsTrue(set.Layout.TextOptional, "text-optional:true must parse to true");

            // Independent, not one flag read twice: liberty's airport sets only text-optional, and its four
            // label_* layers set only icon-optional.
            var textOnly = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-optional':true } } ] }").Layers[0];
            Assert.IsFalse(textOnly.Layout.IconOptional, "text-optional must not set icon-optional");
            Assert.IsTrue(textOnly.Layout.TextOptional);

            // Malformed (a string, not a bool) degrades to the spec default rather than throwing.
            var malformed = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','icon-optional':'yes','text-optional':7 } } ] }").Layers[0];
            Assert.IsFalse(malformed.Layout.IconOptional, "a malformed icon-optional degrades to false");
            Assert.IsFalse(malformed.Layout.TextOptional, "a malformed text-optional degrades to false");
        }

        [Test]
        public void SymbolLayer_Alignment_ParsesMapViewportAndDegradesToAuto()
        {
            var sym = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-rotation-alignment':'map','text-pitch-alignment':'viewport' } } ] }").Layers[0];
            Assert.AreEqual(AlignmentMode.Map, sym.Layout.TextRotationAlignment);
            Assert.AreEqual(AlignmentMode.Viewport, sym.Layout.TextPitchAlignment);

            var bad = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-rotation-alignment':'sideways' } } ] }").Layers[0];
            Assert.AreEqual(AlignmentMode.Auto, bad.Layout.TextRotationAlignment, "an unrecognized alignment degrades to auto");
        }

        // T6 (P1, pitch-alignment epic) — icon/text parse independence. AlignmentMode has only two usable
        // non-auto values for FOUR keys, so one layer can never give all four keys distinguishable values
        // (a prior version of this test wrongly claimed it could, setting two of the four to the same
        // 'map'). Instead: FOUR layers, each setting exactly ONE of the four {text,icon}-{rotation,pitch}
        // keys to 'map' and leaving the other three absent (auto). A copy-paste that reads the wrong
        // PropertyNames constant, or assigns a pitch property from its sibling rotation key, then makes the
        // SET key read Auto or an absent key read Map — caught unambiguously in exactly one of the four
        // layers below.
        [Test]
        public void SymbolLayer_FourAlignmentKeys_ParseIndependently()
        {
            var textRotationOnly = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-rotation-alignment':'map' } } ] }").Layers[0];
            Assert.AreEqual(AlignmentMode.Map, textRotationOnly.Layout.TextRotationAlignment, "text-rotation-alignment: the key that was set");
            Assert.AreEqual(AlignmentMode.Auto, textRotationOnly.Layout.TextPitchAlignment, "text-pitch-alignment must stay auto");
            Assert.AreEqual(AlignmentMode.Auto, textRotationOnly.Layout.IconRotationAlignment, "icon-rotation-alignment must stay auto");
            Assert.AreEqual(AlignmentMode.Auto, textRotationOnly.Layout.IconPitchAlignment, "icon-pitch-alignment must stay auto");

            var textPitchOnly = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-pitch-alignment':'map' } } ] }").Layers[0];
            Assert.AreEqual(AlignmentMode.Auto, textPitchOnly.Layout.TextRotationAlignment, "text-rotation-alignment must stay auto");
            Assert.AreEqual(AlignmentMode.Map, textPitchOnly.Layout.TextPitchAlignment, "text-pitch-alignment: the key that was set");
            Assert.AreEqual(AlignmentMode.Auto, textPitchOnly.Layout.IconRotationAlignment, "icon-rotation-alignment must stay auto");
            Assert.AreEqual(AlignmentMode.Auto, textPitchOnly.Layout.IconPitchAlignment, "icon-pitch-alignment must stay auto");

            var iconRotationOnly = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','icon-rotation-alignment':'map' } } ] }").Layers[0];
            Assert.AreEqual(AlignmentMode.Auto, iconRotationOnly.Layout.TextRotationAlignment, "text-rotation-alignment must stay auto");
            Assert.AreEqual(AlignmentMode.Auto, iconRotationOnly.Layout.TextPitchAlignment, "text-pitch-alignment must stay auto");
            Assert.AreEqual(AlignmentMode.Map, iconRotationOnly.Layout.IconRotationAlignment, "icon-rotation-alignment: the key that was set");
            Assert.AreEqual(AlignmentMode.Auto, iconRotationOnly.Layout.IconPitchAlignment, "icon-pitch-alignment must stay auto");

            var iconPitchOnly = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','icon-pitch-alignment':'map' } } ] }").Layers[0];
            Assert.AreEqual(AlignmentMode.Auto, iconPitchOnly.Layout.TextRotationAlignment, "text-rotation-alignment must stay auto");
            Assert.AreEqual(AlignmentMode.Auto, iconPitchOnly.Layout.TextPitchAlignment, "text-pitch-alignment must stay auto");
            Assert.AreEqual(AlignmentMode.Auto, iconPitchOnly.Layout.IconRotationAlignment, "icon-rotation-alignment must stay auto");
            Assert.AreEqual(AlignmentMode.Map, iconPitchOnly.Layout.IconPitchAlignment, "icon-pitch-alignment: the key that was set");

            var bad = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','icon-pitch-alignment':'sideways' } } ] }").Layers[0];
            Assert.AreEqual(AlignmentMode.Auto, bad.Layout.IconPitchAlignment,
                "an unrecognized icon-pitch-alignment degrades to auto");
        }

        [Test]
        public void SymbolLayer_LayoutOptions_ParseEnumsAndScalars()
        {
            var sym = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-anchor':'top-left','text-justify':'right',
                    'text-offset':[1,2],'text-line-height':1.5,'text-letter-spacing':0.1,'text-radial-offset':0.5,
                    'text-transform':'uppercase' } } ] }").Layers[0];

            Assert.AreEqual(TextAnchor.TopLeft, sym.Layout.TextAnchor, "hyphenated 'top-left' → TopLeft");
            Assert.AreEqual(TextJustify.Right, sym.Layout.TextJustify);
            Assert.AreEqual(TextTransform.Uppercase, sym.Layout.TextTransform);
            // text-offset is retained RAW (y-down, un-flipped) at parse; the y-flip happens in the builder.
            Assert.AreEqual(new float2(1f, 2f), sym.Layout.TextOffset);
            Assert.AreEqual(1.5f, sym.Layout.TextLineHeight.Evaluate(0.0), 1e-6);
            Assert.AreEqual(0.1f, sym.Layout.TextLetterSpacing.Evaluate(0.0), 1e-6);
            Assert.AreEqual(0.5f, sym.Layout.TextRadialOffset.Evaluate(0.0), 1e-6);
        }

        [Test]
        public void SymbolLayer_UnrecognizedAnchorJustify_DegradeToSpecDefault()
        {
            var sym = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-field':'{NAME}','text-anchor':'nonsense','text-justify':'sideways',
                    'text-transform':'italic' } } ] }").Layers[0];

            Assert.AreEqual(TextAnchor.Center, sym.Layout.TextAnchor, "an unrecognized text-anchor degrades to center");
            Assert.AreEqual(TextJustify.Center, sym.Layout.TextJustify, "an unrecognized text-justify degrades to center");
            Assert.AreEqual(TextTransform.None, sym.Layout.TextTransform, "an unrecognized text-transform degrades to none");
        }

        // ── B1 (P-B): icon-rotate parses as a zoom-capable float, spec default 0. ──
        [Test]
        public void SymbolLayer_IconRotate_ParsesConstantZoomAndDefault()
        {
            var bare = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c' } ] }").Layers[0];
            Assert.AreEqual(0f, bare.Layout.IconRotate.Evaluate(0.0), 1e-6, "icon-rotate default is 0 (spec)");

            var constant = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'icon-rotate':180 } } ] }").Layers[0];
            Assert.AreEqual(180f, constant.Layout.IconRotate.Evaluate(0.0), 1e-6,
                "a constant icon-rotate parses in DEGREES (the conversion is the extractor's)");
            Assert.AreEqual(180f, constant.Layout.IconRotate.Evaluate(18.0), 1e-6, "a constant is zoom-invariant");

            // Zoom-capable: an interpolate expression must be honoured, not collapsed to the default.
            var zoomed = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'icon-rotate':['interpolate',['linear'],['zoom'],10,0,20,90] } } ] }").Layers[0];
            Assert.AreEqual(0f, zoomed.Layout.IconRotate.Evaluate(10.0), 1e-6, "z10 -> 0");
            Assert.AreEqual(45f, zoomed.Layout.IconRotate.Evaluate(15.0), 1e-4, "z15 -> the linear midpoint");
            Assert.AreEqual(90f, zoomed.Layout.IconRotate.Evaluate(20.0), 1e-6, "z20 -> 90");
        }

        [Test]
        public void SymbolLayer_TextTranslate_ParsesPixelsAndAnchor()
        {
            var sym = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','paint':{
                    'text-translate':[4,6],'text-translate-anchor':'viewport' } } ] }").Layers[0];

            Assert.AreEqual(new float2(4, 6), sym.Paint.Translate, "text-translate parses to [x,y] px (raw y-down)");
            Assert.AreEqual(TextTranslateAnchor.Viewport, sym.Paint.TranslateAnchor);
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // TextFieldResolverTests — Symbol.TextFieldResolver.Resolve: token sugar + expression form → label
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S105 Slice 1 (A2): <see cref="SymbolStyle.TextFieldResolver.Resolve"/> — token sugar (<c>{prop}</c>) AND
    /// expression form (<c>["get",…]</c>/<c>["coalesce",…]</c>) resolve to the exact label string; a missing
    /// property SKIPS the feature (returns null), never a blank label. Engine-free; runs in both runners.
    /// </summary>
    [TestFixture]
    public class TextFieldResolverTests
    {
        // Author field JSON with single quotes, then swap to real quotes.
        private static JsonValue Field(string json) => JsonParser.Parse(json.Replace('\'', '"'));

        private static IFeature Feature(params (string key, string val)[] props)
        {
            var dict = new Dictionary<string, Value>();
            foreach (var (key, val) in props) dict[key] = Value.String(val);
            return new DictionaryFeature(dict, TileGeometryType.Point);
        }

        private static readonly IFeature Aruba = Feature(("NAME", "Aruba"));
        private static readonly IFeature Afghanistan = Feature(("NAME", "Afghanistan"), ("ABBREV", "Afg."));

        [Test]
        public void Token_SingleProperty_Resolves()
        {
            Assert.AreEqual("Aruba", SymbolStyle.TextFieldResolver.Resolve(Field("'{NAME}'"), Aruba));
        }

        [Test]
        public void Expression_Get_Resolves()
        {
            Assert.AreEqual("Aruba", SymbolStyle.TextFieldResolver.Resolve(Field("['get','NAME']"), Aruba));
        }

        [Test]
        public void Token_MultiTokenWithLiterals_Resolves()
        {
            Assert.AreEqual("Afghanistan (Afg.)",
                SymbolStyle.TextFieldResolver.Resolve(Field("'{NAME} ({ABBREV})'"), Afghanistan));
        }

        [Test]
        public void Expression_CoalesceFallback_Resolves()
        {
            // name:en absent → coalesce falls back to NAME.
            Assert.AreEqual("Aruba",
                SymbolStyle.TextFieldResolver.Resolve(Field("['coalesce',['get','name:en'],['get','NAME']]"), Aruba));
        }

        [Test]
        public void MissingProperty_Token_SkipsWithNull()
        {
            Assert.IsNull(SymbolStyle.TextFieldResolver.Resolve(Field("'{missing}'"), Aruba),
                "an unknown token resolving to empty text must SKIP the feature (null), not emit a blank label");
        }

        [Test]
        public void MissingProperty_Expression_SkipsWithNull()
        {
            Assert.IsNull(SymbolStyle.TextFieldResolver.Resolve(Field("['get','missing']"), Aruba),
                "a get on a missing property must SKIP the feature (null), not emit a blank label");
        }

        [Test]
        public void LiteralNoTokens_PassesThrough()
        {
            Assert.AreEqual("Airport", SymbolStyle.TextFieldResolver.Resolve(Field("'Airport'"), Aruba));
        }

        [Test]
        public void NullField_Skips()
        {
            Assert.IsNull(SymbolStyle.TextFieldResolver.Resolve(null, Aruba));
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // LineDashTests — Line.LineDash: dash coverage, zoom-stability, width coupling, dasharray parse/eval
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S43 / S60 — <see cref="Line.LineDash"/>: dash coverage function, zoom-stability, width
    /// coupling, dasharray parse/eval, and shader structure (tooth 4 — greppable assertion).
    ///
    /// S60: <c>TryEvaluatePattern</c> now returns <c>float4 packed + int count</c> (alloc-free);
    /// <c>Pack</c> deleted. Tests updated accordingly. <c>DashCoverage(float[])</c> remains for
    /// the CPU mirror. All passing count preserved.
    /// </summary>
    [TestFixture]
    public class LineDashTests
    {
        // ── Tooth 1 (DECISIVE): Dash ratio 2:1 for [2, 1] ────────────────────────────────────

        [Test]
        public void DashCoverage_Pattern_2_1_ProducesCorrectOnOffRatio()
        {
            float[] pattern = new float[] { 2f, 1f };
            double widthM = 1.0;

            int onCount = 0, offCount = 0;
            int totalSamples = 30000;
            for (int i = 0; i < totalSamples; i++)
            {
                double dist = (double)i / totalSamples * 30.0;
                float cov = Line.LineDash.DashCoverage(dist, widthM, pattern);
                if (cov >= 0.5f) onCount++;
                else offCount++;
            }

            double ratio = (double)onCount / offCount;
            Assert.That(ratio, Is.InRange(1.9, 2.1),
                $"Expected on:off ratio ≈ 2:1 for [2,1], got {ratio:F4} (on={onCount}, off={offCount})");
        }

        [Test]
        public void DashCoverage_SolidControl_SingleEntry_AllOn()
        {
            float[] pattern = new float[] { 1f };
            for (int i = 0; i < 100; i++)
            {
                float cov = Line.LineDash.DashCoverage(i * 0.17, 1.0, pattern);
                Assert.That(cov, Is.EqualTo(1.0f),
                    $"[1] (odd-length) must be solid identity, got {cov} at dist={i * 0.17}");
            }
        }

        [Test]
        public void DashCoverage_NullPattern_AllOn()
        {
            for (int i = 0; i < 20; i++)
            {
                float cov = Line.LineDash.DashCoverage(i * 0.5, 1.0, null);
                Assert.That(cov, Is.EqualTo(1.0f));
            }
        }

        [Test]
        public void DashCoverage_EmptyPattern_AllOn()
        {
            for (int i = 0; i < 20; i++)
            {
                float cov = Line.LineDash.DashCoverage(i * 0.5, 1.0, new float[0]);
                Assert.That(cov, Is.EqualTo(1.0f));
            }
        }

        [Test]
        public void DashCoverage_DegenerateWidthM_AllOn()
        {
            float[] pattern = new float[] { 2f, 1f };
            Assert.That(Line.LineDash.DashCoverage(5.0, 0.0, pattern), Is.EqualTo(1.0f));
            Assert.That(Line.LineDash.DashCoverage(5.0, -1.0, pattern), Is.EqualTo(1.0f));
        }

        // ── Tooth 1 (continued): On/Off regions are hard binary ──────────────────────────────

        [Test]
        public void DashCoverage_Pattern_2_1_HardBinaryAtCenterOfRuns()
        {
            float[] pattern = new float[] { 2f, 1f };
            double widthM = 1.0;

            Assert.That(Line.LineDash.DashCoverage(1.0, widthM, pattern), Is.EqualTo(1.0f),
                "Center of on-run (distU=1) must be 1.0");
            Assert.That(Line.LineDash.DashCoverage(2.5, widthM, pattern), Is.EqualTo(0.0f),
                "Center of off-run (distU=2.5) must be 0.0");
            Assert.That(Line.LineDash.DashCoverage(4.0, widthM, pattern), Is.EqualTo(1.0f),
                "Center of second on-run (distU=4) must be 1.0");
            Assert.That(Line.LineDash.DashCoverage(5.5, widthM, pattern), Is.EqualTo(0.0f),
                "Center of second off-run (distU=5.5) must be 0.0");
        }

        // ── Tooth 2: Zoom-stable ──────────────────────────────────────────────────────────────

        [Test]
        public void DashCoverage_ZoomStable_CycleCountTimesWidthMIsConstantAcrossZoom()
        {
            // S93 relabel: the 512 convention shifts every zoom number −1, so what was z14/z15 is now z13/z14 —
            // the widthM / cycle counts are bit-identical to the pre-flip run (GroundResolution_512(13) ==
            // GroundResolution_256(14)), keeping this discretization-sensitive check on the same footing.
            double zoom1 = 13.0;
            double zoom2 = 14.0;
            const double widthPx = 4.0;
            double mpp1 = CameraPoseMath.MetersPerPixel(zoom1);
            double mpp2 = CameraPoseMath.MetersPerPixel(zoom2);
            double widthM_z1 = widthPx * mpp1;
            double widthM_z2 = widthPx * mpp2;

            Assert.That(widthM_z1, Is.Not.EqualTo(widthM_z2).Within(1e-6),
                "widthM at zoom14 and zoom15 must differ.");
            Assert.That(widthM_z2, Is.LessThan(widthM_z1),
                "Higher zoom must yield smaller widthM.");

            float[] pattern = new float[] { 2f, 1f };
            double totalDistM = 100.0;
            const int samples = 100000;

            int cycles1 = CountCycles(totalDistM, widthM_z1, pattern, samples);
            int cycles2 = CountCycles(totalDistM, widthM_z2, pattern, samples);

            double product1 = cycles1 * widthM_z1;
            double product2 = cycles2 * widthM_z2;
            double relErr = Math.Abs(product1 - product2) / ((product1 + product2) * 0.5);
            Assert.That(relErr, Is.LessThanOrEqualTo(0.05),
                $"cycles*widthM must be zoom-stable (no drift). " +
                $"z14: cycles={cycles1} widthM={widthM_z1:F4} product={product1:F4}; " +
                $"z15: cycles={cycles2} widthM={widthM_z2:F4} product={product2:F4}; " +
                $"relErr={relErr:P2}");
        }

        [Test]
        public void DashCoverage_ZoomStable_CycleCountScalesWithWidthM()
        {
            float[] pattern = new float[] { 2f, 1f };
            double totalDistM = 60.0;
            double widthM_narrow = 1.0;
            double widthM_wide   = 2.0;

            int cycles_narrow = CountCycles(totalDistM, widthM_narrow, pattern, 10000);
            int cycles_wide   = CountCycles(totalDistM, widthM_wide,   pattern, 10000);

            Assert.That(cycles_wide * 2, Is.EqualTo(cycles_narrow),
                $"Doubling widthM must halve cycle count: narrow={cycles_narrow}, wide={cycles_wide}");
        }

        // ── Tooth 3: Width-coupled ────────────────────────────────────────────────────────────

        [Test]
        public void DashCoverage_WidthCoupled_DoublingWidthMDoublesOnOffLengths()
        {
            float[] pattern = new float[] { 2f, 1f };

            Assert.That(Line.LineDash.DashCoverage(1.0, 1.0, pattern), Is.EqualTo(1.0f));
            Assert.That(Line.LineDash.DashCoverage(2.5, 1.0, pattern), Is.EqualTo(0.0f));
            Assert.That(Line.LineDash.DashCoverage(2.0, 2.0, pattern), Is.EqualTo(1.0f),
                "At 2x width, on-run center at dist=2 must be 1.0");
            Assert.That(Line.LineDash.DashCoverage(5.0, 2.0, pattern), Is.EqualTo(0.0f),
                "At 2x width, off-run center at dist=5 must be 0.0");
            Assert.That(Line.LineDash.DashCoverage(3.0, 2.0, pattern), Is.EqualTo(1.0f),
                "At 2x width, dist=3 still in on-run");
            Assert.That(Line.LineDash.DashCoverage(3.9, 2.0, pattern), Is.EqualTo(1.0f),
                "At 2x width, dist=3.9 still in on-run (boundary)");
        }

        // ── Tooth 5 (non-regression): solid control via DashCount=0 identity ─────────────────

        [Test]
        public void DashCoverage_OddLength_Is_SolidIdentity()
        {
            float[] odd1 = new float[] { 1f };
            float[] odd3 = new float[] { 2f, 1f, 3f };

            for (int i = 0; i < 20; i++)
            {
                Assert.That(Line.LineDash.DashCoverage(i * 0.7, 1.0, odd1), Is.EqualTo(1.0f),
                    "[1] must be solid identity");
                Assert.That(Line.LineDash.DashCoverage(i * 0.7, 1.0, odd3), Is.EqualTo(1.0f),
                    "[2,1,3] (odd) must be solid identity");
            }
        }

        // ── S60: TryEvaluatePattern → float4 + count (alloc-free, replaces Pack) ─────────────

        [Test]
        public void TryEvaluatePattern_TwoEntry_PackedCorrectly()
        {
            // [2, 1] → packed.x=2, packed.y=1, count=2
            var expr = Line.LineDash.ParseDashArray(JsonParser.Parse("[2, 1]"));
            Assert.IsNotNull(expr, "ParseDashArray must succeed for [2,1].");

            bool ok = Line.LineDash.TryEvaluatePattern(expr, 14.0, out float4 packed, out int count);
            Assert.IsTrue(ok, "TryEvaluatePattern must return true for a valid [2,1] pattern.");
            Assert.AreEqual(2, count, "Count must be 2 for a two-entry pattern.");
            Assert.AreEqual(2f, packed.x, 1e-6f, "packed.x must be 2 (first dash length).");
            Assert.AreEqual(1f, packed.y, 1e-6f, "packed.y must be 1 (first gap length).");
            Assert.AreEqual(0f, packed.z, 1e-6f, "packed.z must be 0 (unused).");
            Assert.AreEqual(0f, packed.w, 1e-6f, "packed.w must be 0 (unused).");
        }

        [Test]
        public void TryEvaluatePattern_FourEntry_PackedCorrectly()
        {
            var expr = Line.LineDash.ParseDashArray(JsonParser.Parse("[4, 1, 1, 1]"));
            bool ok = Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 packed, out int count);
            Assert.IsTrue(ok);
            Assert.AreEqual(4, count);
            Assert.AreEqual(4f, packed.x, 1e-6f);
            Assert.AreEqual(1f, packed.y, 1e-6f);
            Assert.AreEqual(1f, packed.z, 1e-6f);
            Assert.AreEqual(1f, packed.w, 1e-6f);
        }

        [Test]
        public void TryEvaluatePattern_OddEntry_ReturnsFalse_CountZero()
        {
            // [2, 1, 3] is odd-length → solid identity → false, count=0
            var expr = Line.LineDash.ParseDashArray(JsonParser.Parse("[2, 1, 3]"));
            bool ok = Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 packed, out int count);
            Assert.IsFalse(ok, "Odd-length must return false (solid identity).");
            Assert.AreEqual(0, count, "Count must be 0 for odd-length (solid identity sentinel).");
        }

        [Test]
        public void TryEvaluatePattern_Null_ReturnsFalse()
        {
            bool ok = Line.LineDash.TryEvaluatePattern(null, 10.0, out float4 packed, out int count);
            Assert.IsFalse(ok, "Null expression must return false.");
            Assert.AreEqual(0, count);
        }

        [Test]
        public void TryEvaluatePattern_CapAt4Entries()
        {
            // 5-entry → truncated to 4 (even) → count=4, ok=true
            var expr = Line.LineDash.ParseDashArray(JsonParser.Parse("[1, 1, 1, 1, 1]"));
            bool ok = Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 packed, out int count);
            // After truncation to 4 entries (even), count=4
            Assert.AreEqual(4, count, "5 entries truncated to 4 (even) → count=4");
        }

        // ── ParseDashArray + TryEvaluatePattern: expression system integration ────────────────

        private static MapRenderer.Core.Expressions.Expression DashExpr(string json)
            => Line.LineDash.ParseDashArray(JsonParser.Parse(json));

        [Test]
        public void ParseDashArray_ConstantBareArray_IsConstantKind_AndEvaluates()
        {
            var expr = DashExpr("[2, 1]");
            Assert.That(expr, Is.Not.Null);
            Assert.That(expr.Kind, Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Constant),
                "A bare constant dasharray must classify as Constant.");

            bool ok = Line.LineDash.TryEvaluatePattern(expr, 14.0, out float4 packed, out int count);
            Assert.That(ok, Is.True);
            Assert.That(count, Is.EqualTo(2));
            Assert.That(packed.x, Is.EqualTo(2f));
            Assert.That(packed.y, Is.EqualTo(1f));
        }

        [Test]
        public void ParseDashArray_LiteralExpression_IsConstantKind()
        {
            var expr = DashExpr("[\"literal\", [4, 2]]");
            Assert.That(expr.Kind, Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Constant));
            Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 packed, out int count);
            Assert.That(count, Is.EqualTo(2));
            Assert.That(packed.x, Is.EqualTo(4f));
            Assert.That(packed.y, Is.EqualTo(2f));
        }

        [Test]
        public void ParseDashArray_ZoomStep_IsZoomKind_AndPicksCorrectStep()
        {
            var expr = DashExpr("[\"step\", [\"zoom\"], [1,1], 10, [2,1], 14, [4,1]]");
            Assert.That(expr.Kind, Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Zoom),
                "A zoom-step dasharray must classify as Zoom.");

            Line.LineDash.TryEvaluatePattern(expr, 9.0,  out float4 p9, out _);
            Assert.That(p9.x, Is.EqualTo(1f), "zoom=9: default [1,1]");

            Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 p10, out _);
            Assert.That(p10.x, Is.EqualTo(2f), "zoom=10: [2,1]");

            Line.LineDash.TryEvaluatePattern(expr, 14.0, out float4 p14, out _);
            Assert.That(p14.x, Is.EqualTo(4f), "zoom=14: [4,1]");

            Line.LineDash.TryEvaluatePattern(expr, 15.0, out float4 p15, out _);
            Assert.That(p15.x, Is.EqualTo(4f), "zoom=15: still [4,1]");
        }

        [Test]
        public void ParseDashArray_Null_ReturnsNull_AndEvaluatesToSolid()
        {
            Assert.That(Line.LineDash.ParseDashArray(null), Is.Null);
            bool ok = Line.LineDash.TryEvaluatePattern(null, 10.0, out float4 packed, out int count);
            Assert.That(ok, Is.False);
            Assert.That(count, Is.EqualTo(0));
        }

        [Test]
        public void ParseDashArray_DataDriven_DegradesToSolid()
        {
            var expr = DashExpr("[\"match\", [\"get\", \"cls\"], \"a\", [2,1], [4,2]]");
            Assert.That(MapRenderer.Core.Expressions.ExpressionKinds.DependsOnFeature(expr.Kind), Is.True);
            Assert.That(Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 packed, out int count), Is.False,
                "Data-driven dasharray must not crash — it degrades to solid (no pattern).");
            Assert.That(count, Is.EqualTo(0));
        }

        // ── Tooth 4 (GREPPABLE): Shader consumes per-vertex sideAndDist.y as dashU ────────────

        /// <summary>Resolves a repo-relative path by walking up from the test runner's working directory —
        /// the runner's cwd differs between the Unity EditMode runner and <c>Tools/core-tests</c>, and
        /// neither is the repo root. Returns null when no ancestor contains it.</summary>
        private static string FindRepoFile(params string[] segments)
        {
            string[] starts = new[]
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory,
            };

            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, Path.Combine(segments));
                    if (File.Exists(candidate)) return candidate;
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            return null;
        }

        [Test]
        public void ShaderStructure_LineLitForwardPass_ConsumesDistanceAlongAsPerVertexAttribute()
        {
            // Resolved by name (move-proof) — the lit forward pass now lives under Map/Line/Lit/.
            string text = File.ReadAllText(EngineFreeShaderPaths.ResolveMapShaderPath("Line_LitForwardPass.hlsl"));

            // S67 + UV-channel cleanup: dashU is computed in the shared Line_VertexExtrude helper; the
            // forward pass calls it and carries the result in the line uv channel (uv.x — the native
            // along-coordinate), and the fragment reads uv.x for dash coverage. There is no dedicated
            // dashU varying any more (per-vertex distanceAlong sourcing is asserted on the helper below).
            Assert.That(text, Does.Contain("Line_VertexExtrude("),
                "Line_LitForwardPass.hlsl must call the shared Line_VertexExtrude helper (which sources dashU).");
            Assert.That(text, Does.Contain("output.uv"),
                "Vertex shader must carry the line coordinates (dashU/side/innerFrac) in output.uv.");
            Assert.That(text, Does.Contain("input.uv.x"),
                "Fragment shader must read the dash coordinate from input.uv.x.");

            // _DashCount: S67 factored the dash logic into Line_VertexExtrude.hlsl (shared by all passes).
            // Assert the guard is present there — still a single-site check, just in the helper.
            // Shared helper, resolved by name (move-proof) — stays in the Map/Line/ kind root.
            string extrudeText = File.ReadAllText(EngineFreeShaderPaths.ResolveMapShaderPath("Line_VertexExtrude.hlsl"));
            Assert.That(extrudeText, Does.Contain("sideAndDist.y"),
                "Line_VertexExtrude.hlsl must consume per-vertex distanceAlong (input.sideAndDist.y) to form dashU.");
            Assert.That(extrudeText, Does.Contain("dashU"),
                "Line_VertexExtrude.hlsl must emit the dashU dash coordinate.");
            Assert.That(extrudeText, Does.Contain("_DashCount"),
                "Line_VertexExtrude.hlsl must have _DashCount guard for solid identity (S67: dash logic lives in shared helper).");
        }

        // ── S110 T6: the CPU mirror's pointer to its HLSL twin must name a file that exists ──────

        /// <summary>Resolves a repo-relative DIRECTORY the same way <see cref="FindRepoFile"/> resolves a
        /// file. Returns null when no ancestor contains it.</summary>
        private static string FindRepoDir(params string[] segments)
        {
            string[] starts = new[]
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory,
            };

            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, Path.Combine(segments));
                    if (Directory.Exists(candidate)) return candidate;
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            return null;
        }

        /// <summary>
        /// <b>S110 T6.</b> <see cref="Line.LineDash"/> is the declared single source of truth for the dash
        /// function and points at its HLSL mirror by name. Those pointers must name the file that actually
        /// carries the mirror — <c>Line_VertexExtrude.hlsl</c>, where S67 moved it — and not the
        /// <c>MapLineForwardPass.hlsl</c> that has not existed for several stages.
        ///
        /// <para>The second half is what stops this from rotting: every <c>*.hlsl</c> token in the file is
        /// resolved on disk. <c>MapLineForwardPass.hlsl</c> was a CORRECT pointer once; without a
        /// resolve-on-disk clause, a name-only assertion is one rename away from being green and wrong
        /// again. Distinct from the greppable tooth above, which reads the SHADER files rather than this
        /// one.</para>
        /// </summary>
        [Test]
        public void ShaderPointers_InLineDashSource_ResolveOnDisk()
        {
            string sourcePath = FindRepoFile(
                "Assets", "Code", "MapRenderer.Core", "Style", "Line", "LineDash.cs");
            Assert.That(sourcePath, Is.Not.Null,
                $"LineDash.cs not found. Tried walking up 16 levels from " +
                $"cwd={Directory.GetCurrentDirectory()} and AppContext.BaseDirectory={AppContext.BaseDirectory}");
            string text = File.ReadAllText(sourcePath);

            Assert.That(text, Does.Contain("Line_VertexExtrude.hlsl"),
                "LineDash.cs must point at Line_VertexExtrude.hlsl — the file that carries the HLSL mirror " +
                "of DashCoverage since S67, and the file S110 changed the dash divisor in.");
            Assert.That(text, Does.Not.Contain("MapLineForwardPass"),
                "LineDash.cs still points at MapLineForwardPass.hlsl, which no longer exists. A reader " +
                "asked to 'keep both in sync on any arithmetic change' cannot find the other half.");

            string shadersDir = FindRepoDir("Assets", "Code", "MapRenderer.Unity", "Shaders");
            Assert.That(shadersDir, Is.Not.Null, "Shaders directory not found from the test working directory.");

            var referenced = new System.Collections.Generic.HashSet<string>();
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, @"[\w\.]+\.hlsl"))
                referenced.Add(Path.GetFileName(m.Value));

            Assert.That(referenced, Is.Not.Empty,
                "LineDash.cs names no .hlsl file at all — the mirror pointer has been deleted rather than " +
                "corrected, and the two halves of the dash function are no longer linked in either direction.");

            foreach (string name in referenced)
                Assert.That(Directory.GetFiles(shadersDir, name, SearchOption.AllDirectories), Is.Not.Empty,
                    $"LineDash.cs points at '{name}', which does not exist anywhere under " +
                    $"Assets/Code/MapRenderer.Unity/Shaders. Every shader pointer in the CPU mirror must " +
                    "resolve, or the 'keep both in sync' instruction is unfollowable.");
        }

        // ── LinePaint.HasDashArray parse integration ──────────────────────────────────────────

        private static Line.StyleLayer MakeDashLayer(string paintJson)
        {
            return new Line.StyleLayer
            {
                Id          = "test-dash",
                LayerType   = MapRenderer.Core.Style.StyleLayerType.Line,
                SourceLayer = "roads",
                Paint       = TestStyle.LinePaint(paintJson),
                Layout      = TestStyle.LineLayout(),
            };
        }

        [Test]
        public void LinePaint_HasDashArray_FalseWhenAbsent()
        {
            var layer = MakeDashLayer("{\"line-width\":2}");
            var paint = layer.Paint;
            Assert.That(paint.HasDashArray, Is.False);
            Assert.That(paint.DashArray, Is.Null);
        }

        [Test]
        public void LinePaint_HasDashArray_TrueWhenPresent()
        {
            var layer = MakeDashLayer("{\"line-dasharray\":[2,1]}");
            var paint = layer.Paint;
            Assert.That(paint.HasDashArray, Is.True);
            Assert.That(paint.DashArray, Is.Not.Null);
            Assert.That(paint.DashArrayKind,
                Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Constant));
        }

        [Test]
        public void LinePaint_HasDashArray_IsNotInertFallback()
        {
            var layer = MakeDashLayer("{\"line-dasharray\":[2,1]}");
            var paint = layer.Paint;
            Assert.That(paint.IsInertFallback, Is.False,
                "Line layer with line-dasharray must not be IsInertFallback.");
        }

        // ── Helper: count on-cycles over a line span ──────────────────────────────────────────

        private static int CountCycles(double totalDistM, double widthM, float[] pattern, int samples)
        {
            bool wasOn = false;
            int transitionCount = 0;
            for (int i = 0; i < samples; i++)
            {
                double dist = (double)i / samples * totalDistM;
                bool isOn = Line.LineDash.DashCoverage(dist, widthM, pattern) >= 0.5f;
                if (i > 0 && wasOn != isOn)
                    transitionCount++;
                wasOn = isOn;
            }
            return (transitionCount + 1) / 2;
        }
    }




    // ───────────────────────────────────────────────────────────────────────────────────
    // IconImageResolverTests — Symbol.IconImageResolver.Resolve: token sugar + expression form → sprite name
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// I3: <see cref="SymbolStyle.IconImageResolver.Resolve"/> — token sugar (<c>{prop}</c>) AND expression
    /// form (<c>["get",…]</c>/<c>["coalesce",…]</c>) resolve to the exact sprite name; a missing property
    /// SKIPS the icon (returns null), never an empty name. Mirrors <c>TextFieldResolverTests</c> for the
    /// icon-image analogue. Engine-free; runs in both runners.
    /// </summary>
    [TestFixture]
    public class IconImageResolverTests
    {
        // Author field JSON with single quotes, then swap to real quotes.
        private static JsonValue Field(string json) => JsonParser.Parse(json.Replace('\'', '"'));

        private static IFeature Feature(params (string key, string val)[] props)
        {
            var dict = new Dictionary<string, Value>();
            foreach (var (key, val) in props) dict[key] = Value.String(val);
            return new DictionaryFeature(dict, TileGeometryType.Point);
        }

        private static readonly IFeature Marker = Feature(("icon", "marker"));
        private static readonly IFeature Star = Feature(("icon", "star"), ("kind", "poi"));

        [Test]
        public void Token_SingleProperty_Resolves()
        {
            Assert.AreEqual("marker", SymbolStyle.IconImageResolver.Resolve(Field("'{icon}'"), Marker));
        }

        [Test]
        public void Expression_Get_Resolves()
        {
            Assert.AreEqual("marker", SymbolStyle.IconImageResolver.Resolve(Field("['get','icon']"), Marker));
        }

        [Test]
        public void Expression_CoalesceFallback_Resolves()
        {
            // icon:2x absent → coalesce falls back to icon.
            Assert.AreEqual("star",
                SymbolStyle.IconImageResolver.Resolve(Field("['coalesce',['get','icon:2x'],['get','icon']]"), Star));
        }

        [Test]
        public void UnknownToken_SkipsWithNull()
        {
            Assert.IsNull(SymbolStyle.IconImageResolver.Resolve(Field("'{missing}'"), Marker),
                "an unknown token resolving to empty text must SKIP the icon (null), not emit an empty name");
        }

        [Test]
        public void MissingProperty_Expression_SkipsWithNull()
        {
            Assert.IsNull(SymbolStyle.IconImageResolver.Resolve(Field("['get','missing']"), Marker),
                "a get on a missing property must SKIP the icon (null), not emit an empty name");
        }

        [Test]
        public void LiteralNoTokens_PassesThrough()
        {
            Assert.AreEqual("pin", SymbolStyle.IconImageResolver.Resolve(Field("'pin'"), Marker));
        }

        [Test]
        public void AbsentField_Skips()
        {
            Assert.IsNull(SymbolStyle.IconImageResolver.Resolve(null, Marker));
        }

        [Test]
        public void EmptyLiteral_Skips()
        {
            Assert.IsNull(SymbolStyle.IconImageResolver.Resolve(Field("''"), Marker),
                "an empty/whitespace resolution must skip, mirroring TextFieldResolver");
        }
    }
}
