// Style/StyleTests.cs — engine-free style parsing and evaluation, compiled by both Unity EditMode and
// Tools/core-tests. Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.
//
// Contents:
//   TileJsonTests               — TileJSON parses into the typed model; SourceResolver fills a SourceDefinition.
//   StylePropertyTests          — StyleProperty<T>: parse, classify, evaluate (uniform + bake), GC-allocation gate.
//   StyleLayerEagerParseTests   — the eight style property types parse eagerly via a static Parse factory.
//   FillLayoutTests             — Fill.LayoutProperties: parsing fill-sort-key.
//   FillPaintTranslateTests     — Fill.PaintProperties.Translate: the shared TranslateProperty route.
//   FillPatternTests            — Fill.FillPattern: resolving a fill-pattern sprite name against a sheet.
//   FillExtrusionPaintTests     — FillExtrusion.PaintProperties: classification + spec defaults.
//   LinePaintTests              — Line.PaintProperties/LayoutProperties: classification, translate, join/cap layout.
//   BackgroundPaintTests        — Background.PaintProperties: spec defaults, explicit parse, zoom classification.
//   SymbolStyleLayerTests       — a symbol layer parses to the typed Symbol.StyleLayer with spec defaults.
//   SymbolStringPropertyTests   — Symbol.LayoutProperties.{TextField,IconImage}: shared token desugar + expression form.
//   LineDashTests               — Line.LineDash: dash coverage, zoom-stability, width coupling, dasharray parse/eval.
//   LightSkyTests               — root light/sky blocks: parse, spec defaults, malformed input.
//
// LineOffsetTests.cs stays its own file: its `using MapRenderer.Unity.Style.Line;` (a namespace import, for
// LineOffset) brings `MapRenderer.Unity.Style.Line.StyleLayer` into scope, colliding with the bare
// `StyleLayer` (MapRenderer.Unity.Style.StyleLayer) used above (CS0104).

using System;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Json;
using MapRenderer.Unity.Style;
using MapRenderer.Core.Expressions;
using System.Linq;
using System.Reflection;
using Fill = MapRenderer.Unity.Style.Fill;
using Line = MapRenderer.Unity.Style.Line;
using SymbolStyle = MapRenderer.Unity.Style.Symbol;
using Background = MapRenderer.Unity.Style.Background;
using FillExtrusion = MapRenderer.Unity.Style.FillExtrusion;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Text.Sprites;
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
    /// A TileJSON document parses into the typed <see cref="TileJson"/> model (tolerant,
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

        // A representative TileJSON for liberty's `openmaptiles` source url. Its zoom range (0..14, NOT the
        // style-spec default 22) makes a no-op resolve detectable.
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
        // 0. Resolving liberty's `openmaptiles` source (`url`, no inline `tiles`) populates Tiles and takes
        //    the zoom range from the TileJSON (maxzoom 14, NOT the style default 22).
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
        // 1. Inline `tiles[]` short-circuit: liberty's `ne2_shaded` raster source (inline tiles, maxzoom 6)
        //    resolves UNCHANGED, with no TileJSON applied.
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
        // 5b. The parser is the ONE place that decides "malformed" — a typed flag, not a re-read of Raw.
        //     Absent stays unmalformed; a present non-array value, wrong shape, a non-number element, and
        //     an out-of-range value (south > north, or a longitude outside [-180, 180]) all set it, on
        //     both StyleParser.ParseSource's own bounds AND TileJsonParser's, and SourceResolver carries
        //     the TileJSON one onto the resolved SourceDefinition.
        // =========================================================================================
        [Test]
        public void BoundsMalformed_IsATypedFlag_NotARawReread()
        {
            SourceDefinition Source(string boundsJson) => StyleParser.Parse($@"{{
                ""sources"": {{ ""v"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""]
                    {(boundsJson == null ? "" : $@", ""bounds"": {boundsJson}")} }} }}
            }}").Sources["v"];

            SourceDefinition absent = Source(null);
            Assert.IsFalse(absent.BoundsMalformed, "an absent bounds key is not malformed");
            CollectionAssert.AreEqual(StyleParser.DefaultBounds, absent.Bounds);

            SourceDefinition wrongLength = Source("[0, 0, 180]");
            Assert.IsTrue(wrongLength.BoundsMalformed, "a 3-element array is not [west, south, east, north]");
            CollectionAssert.AreEqual(StyleParser.DefaultBounds, wrongLength.Bounds);

            SourceDefinition notAnArray = Source(@"""x""");
            Assert.IsTrue(notAnArray.BoundsMalformed,
                "a PRESENT bounds key that is not an array must be malformed, not treated as absent");
            CollectionAssert.AreEqual(StyleParser.DefaultBounds, notAnArray.Bounds);

            SourceDefinition jsonNull = Source("null");
            Assert.IsTrue(jsonNull.BoundsMalformed, "a PRESENT bounds key holding JSON null must be malformed");

            SourceDefinition nonNumber = Source(@"[0, 0, 180, ""x""]");
            Assert.IsTrue(nonNumber.BoundsMalformed,
                "a non-number element must be REJECTED, not silently coerced by AsDouble()");
            CollectionAssert.AreEqual(StyleParser.DefaultBounds, nonNumber.Bounds);

            SourceDefinition southPastNorth = Source("[0, 10, 180, 0]");
            Assert.IsTrue(southPastNorth.BoundsMalformed, "south (10) > north (0) is not a valid box");

            SourceDefinition lonOutOfRange = Source("[-200, 0, 180, 10]");
            Assert.IsTrue(lonOutOfRange.BoundsMalformed, "west (-200) is outside [-180, 180]");

            SourceDefinition valid = Source("[0, 0, 180, 10]");
            Assert.IsFalse(valid.BoundsMalformed);
            CollectionAssert.AreEqual(new[] { 0.0, 0.0, 180.0, 10.0 }, valid.Bounds);

            // TileJsonParser shares the same validation, and SourceResolver carries its flag onto the
            // resolved SourceDefinition (mirroring how it already carries Bounds itself).
            var tileJson = TileJsonParser.Parse(@"{ ""tiles"": [""https://x/{z}/{x}/{y}.pbf""], " +
                                                 @"""bounds"": [0, 10, 180, 0] }");
            Assert.IsTrue(tileJson.BoundsMalformed, "TileJsonParser must validate bounds the same way");

            var resolvedFromUrl = new SourceDefinition { Type = SourceType.Vector, Url = "u" };
            SourceResolver.Resolve(resolvedFromUrl, tileJson);
            Assert.IsTrue(resolvedFromUrl.BoundsMalformed,
                "SourceResolver must carry the TileJSON's BoundsMalformed flag onto the resolved source, " +
                "the same way it already carries Bounds itself");
        }

        // =========================================================================================
        // 6. NeedsTileJson predicate truth table (the fetch-side short-circuit relies on).
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

        // =========================================================================================
        // 7. geojson `maxzoom` defaults to the spec's 18, not the vector default of 22.
        // =========================================================================================
        [Test]
        public void MaxZoomDefault_Is18ForGeoJson_22ForVectorAndOthers()
        {
            SourceDefinition GeoJson(string maxZoomJson = null) => StyleParser.Parse($@"{{
                ""sources"": {{ ""g"": {{ ""type"": ""geojson"", ""data"": {{ ""type"": ""FeatureCollection"",
                    ""features"": [] }}{(maxZoomJson == null ? "" : $@", ""maxzoom"": {maxZoomJson}")} }} }}
            }}").Sources["g"];

            Assert.AreEqual(StyleParser.DefaultGeoJsonSourceMaxZoom, GeoJson().MaxZoom,
                "an absent maxzoom on a geojson source must default to the spec's 18, not the vector default");
            Assert.AreEqual(12, GeoJson("12").MaxZoom, "an explicit maxzoom is read as-is");

            SourceDefinition vector = StyleParser.Parse(@"{
                ""sources"": { ""v"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } }
            }").Sources["v"];
            Assert.AreEqual(StyleParser.DefaultSourceMaxZoom, vector.MaxZoom,
                "a vector source's absent maxzoom must stay the vector default (22), unaffected by the " +
                "geojson-specific default");
        }

        // =========================================================================================
        // 8. geojson `buffer`: an AUTHORED value only, clamped to [0, 512]; absent/non-number is null.
        // =========================================================================================
        [Test]
        public void BufferParsing_ClampsToSpecRange_AndIsNullWhenAbsentOrNotANumber()
        {
            SourceDefinition GeoJsonWithBuffer(string bufferJson) => StyleParser.Parse($@"{{
                ""sources"": {{ ""g"": {{ ""type"": ""geojson"", ""data"": {{ ""type"": ""FeatureCollection"",
                    ""features"": [] }}, ""buffer"": {bufferJson} }} }}
            }}").Sources["g"];

            Assert.AreEqual(0.0, GeoJsonWithBuffer("-5").Buffer, "a negative buffer clamps to 0");
            Assert.AreEqual(512.0, GeoJsonWithBuffer("9999").Buffer, "a buffer above 512 clamps to the spec ceiling");
            Assert.AreEqual(128.0, GeoJsonWithBuffer("128").Buffer, "an in-range value is read as-is");
            Assert.IsNull(GeoJsonWithBuffer(@"""x""").Buffer,
                "a non-number value falls back to null (the same as an absent key), not a thrown parse or a coerced 0");

            SourceDefinition absent = StyleParser.Parse(@"{
                ""sources"": { ""g"": { ""type"": ""geojson"", ""data"": { ""type"": ""FeatureCollection"",
                    ""features"": [] } } }
            }").Sources["g"];
            Assert.IsNull(absent.Buffer, "an absent buffer key is null — an AUTHORED value only");

            SourceDefinition vector = StyleParser.Parse(@"{
                ""sources"": { ""v"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""],
                    ""buffer"": 64 } }
            }").Sources["v"];
            Assert.IsNull(vector.Buffer,
                "`buffer` is a geojson-only key — a vector source's value must not be read at all");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // StylePropertyTests — StyleProperty<T>: parse, classify, evaluate (uniform + bake), GC-allocation gate
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="StyleProperty{T}"/>: Constant vs Zoom classification, numeric and premultiplied colour
    /// interpolation, Evaluate(zoom) throwing on Feature/Composite, a zero-GC zoom sweep, the bake path for
    /// every ExpressionKind, per-feature match colours, and an absent property as a Constant DefaultValue.
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

        /// <summary>A width interpolation over stops (5,2.0)-(15,20.0), sampled at each stop, mid-zoom
        /// (linear interpolation), and past each end (clamped).</summary>
        [Test]
        [TestCase(5.0, 2.0f, 1e-6f, TestName = "Zoom_WidthInterpolate_MatchesExpected(AtLowStop_ReturnsStopValue)")]
        [TestCase(15.0, 20.0f, 1e-6f, TestName = "Zoom_WidthInterpolate_MatchesExpected(AtHighStop_ReturnsHighValue)")]
        // t = (10-5)/(15-5) = 0.5, value = 2.0 + 0.5*(20.0-2.0) = 11.0.
        [TestCase(10.0, 11.0f, 1e-4f, TestName = "Zoom_WidthInterpolate_MatchesExpected(AtMidZoom_ReturnsLinearInterpolation)")]
        [TestCase(0.0, 2.0f, 1e-6f, TestName = "Zoom_WidthInterpolate_MatchesExpected(BelowFirstStop_ClampsToFirst)")]
        [TestCase(20.0, 20.0f, 1e-6f, TestName = "Zoom_WidthInterpolate_MatchesExpected(AboveLastStop_ClampsToLast)")]
        public void Zoom_WidthInterpolate_MatchesExpected(double zoom, float expected, float tolerance)
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(expected, prop.Evaluate(zoom), tolerance);
        }

        // ── 3. Color interpolation with premultiplied-alpha ───────────────────────────────────
        //
        // Same hand-derived values as the retired PaintPropertyEvaluatorTests — byte-identity pin.

        /// <summary>A zoom-interpolated color premultiplies alpha across every stop-alpha combination:
        /// transparent→opaque (diverging sharply from a straight lerp), alpha=1 (reducing to a straight sRGB
        /// lerp, the one case premult and straight agree), and partial-alpha stops on both ends.</summary>
        [Test]
        public void ZoomInterpolate_Color_PremultipliesAlpha()
        {
            var transparentToOpaque = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",0,0,0,0], 1, [\"rgba\",255,255,255,1]]");
            double[] tToO = transparentToOpaque.Evaluate(0.5).ToRgbaArray();
            Assert.AreEqual(255.0, tToO[0], 0.5, "Premult alpha: R at t=0.5 must be 255, not 127.5.");
            Assert.AreEqual(255.0, tToO[1], 0.5, "G must equal R for white.");
            Assert.AreEqual(255.0, tToO[2], 0.5, "B must equal R for white.");
            Assert.AreEqual(0.5,   tToO[3], 1e-6, "Alpha at t=0.5 must be 0.5.");

            var alphaOne = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"to-color\", \"#000000\"], 1, [\"to-color\", \"#ffffff\"]]");
            double[] a1 = alphaOne.Evaluate(0.5).ToRgbaArray();
            Assert.AreEqual(127.5, a1[0], 1e-6, "At alpha=1, premult reduces to straight sRGB lerp.");
            Assert.AreEqual(127.5, a1[1], 1e-6);
            Assert.AreEqual(127.5, a1[2], 1e-6);
            Assert.AreEqual(1.0,   a1[3], 1e-9);

            var partialAlpha = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",200,0,0,0.5], 1, [\"rgba\",0,0,200,1.0]]");
            double[] partial = partialAlpha.Evaluate(0.5).ToRgbaArray();
            Assert.AreEqual(0.75, partial[3], 1e-6, "Alpha must be linear lerp of 0.5 and 1.0 at t=0.5.");
            Assert.AreEqual(66.67, partial[0], 0.5, "R channel via premult lerp.");
            Assert.AreEqual(133.33, partial[2], 0.5, "B channel via premult lerp.");
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

        // ── 9. Data-driven bake path ───────────────────────────────────────────────────────────

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
        public void Composite_InterpolateZoom_FeatureColorStops_DistinctAtSameZoom_AndWarnsAtStyleLoad()
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

            // Style load warns once per composite property, naming layer and property. The zoom-only and
            // feature-only keys are the negative controls; the legacy function is the second composite.
            StyleDocument doc = StyleParser.Parse("{\"version\":8,\"sources\":{},\"layers\":[{\"id\":\"countries-fill\"," +
                "\"type\":\"fill\",\"paint\":{\"fill-color\":" + compositeExpr + "," +
                "\"fill-outline-color\":[\"interpolate\",[\"linear\"],[\"zoom\"],0,[\"rgba\",0,0,0,1],8,[\"rgba\",255,255,255,1]]," +
                "\"fill-antialias\":[\"match\",[\"get\",\"CONTINENT\"],\"Asia\",true,false]," +
                "\"fill-opacity\":{\"property\":\"pop\",\"type\":\"exponential\"," +
                "\"stops\":[[{\"zoom\":0,\"value\":0},0.2],[{\"zoom\":0,\"value\":10},0.4]," +
                "[{\"zoom\":8,\"value\":0},0.6],[{\"zoom\":8,\"value\":10},0.8]]}}}]}");
            Assert.AreEqual(2, doc.Warnings.Count, string.Join(" | ", doc.Warnings));
            StringAssert.Contains("layer 'countries-fill' paint 'fill-color'", doc.Warnings[0]);
            StringAssert.Contains("layer 'countries-fill' paint 'fill-opacity'", doc.Warnings[1]);
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

        // ── Legacy categorical function: no "default" degrades to the STYLE PROPERTY's default ──────

        [Test]
        public void Categorical_NoDefault_UnmatchedKey_TryEvaluateFailsToPropertyDefault()
        {
            // No "default": the fallback is null, which a numeric project cannot read — TryEvaluate must
            // catch that and hand back NumProp's own default (0), not throw out of the bake path.
            var prop = NumProp("{\"property\":\"class\",\"type\":\"categorical\",\"stops\":[[\"motorway\",8]]}");
            bool ok = prop.TryEvaluate(0.0, MakeFeature(("class", "primary")), out float value);
            Assert.IsFalse(ok, "an unmatched category with no default is a spec error, not a value");
            Assert.AreEqual(0f, value);
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // StyleLayerEagerParseTests — the eight style property types parse eagerly via a static Parse factory
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The eight style property types (<c>Fill</c>/<c>Line</c>/<c>Symbol</c>/
    /// <c>Background</c>/<c>FillExtrusion</c> × <c>Paint</c>/<c>Layout</c>, where each exists) parse eagerly
    /// via a static <c>Parse</c> factory — not lazily from a retained raw JSON field. Reflection pins that
    /// no lazy path exists; <see cref="StyleParser"/> threads <c>fillAntialiasDefault</c> into the parse;
    /// and <c>Parse(null)</c> returns spec defaults for all eight, as absent paint/layout blocks need.
    /// </summary>
    [TestFixture]
    public class StyleLayerEagerParseTests
    {
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

        // ── behaviour preservation through StyleParser ────────────────────────────────────────────

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

        /// <summary>An omitted fill-antialias falls back to the HOST default (read, not just an unused
        /// parameter); an explicit layer value wins over the host default either way.</summary>
        [Test]
        [TestCase(null, false, false, TestName = "StyleParser_FillAntialias_MatchesExpected(Omitted_HostDefaultFalse)")]
        [TestCase(null, true, true, TestName = "StyleParser_FillAntialias_MatchesExpected(Omitted_HostDefaultTrue)")]
        [TestCase(true, false, true, TestName = "StyleParser_FillAntialias_MatchesExpected(Explicit_WinsOverHostDefault)")]
        public void StyleParser_FillAntialias_MatchesExpected(bool? antialias, bool hostDefault, bool expected)
        {
            var doc = StyleParser.Parse(MinimalFillStyle(antialias: antialias), fillAntialiasDefault: hostDefault);
            var fill = (Fill.StyleLayer)doc.Layers[0];
            Assert.AreEqual(expected, fill.Paint.Antialias.Evaluate(0.0),
                $"antialias={antialias}, hostDefault={hostDefault} must evaluate to {expected}.");
        }

        [Test]
        public void FillAntialias_LegacyZoomFunction_StepsBetweenBooleans()
        {
            // fill-antialias has no "interpolate" marker (and is a bool, not a number): an absent "type"
            // steps between the literal bool outputs.
            var paint = TestStyle.FillPaint(@"{""fill-antialias"": {""stops"": [[10, true], [14, false]]}}");
            Assert.IsTrue(paint.Antialias.Evaluate(12.0), "z12 is below the second stop (14)");
            Assert.IsFalse(paint.Antialias.Evaluate(15.0));
        }

        // ── the laziness cannot come back (reflection, never text) ─────────────────────────────────

        /// <summary>True when <paramref name="property"/>'s setter carries the <c>IsExternalInit</c>
        /// required custom modifier — the compiler's marker for an <c>init</c> accessor. Tolerates a
        /// missing setter by returning false. Compared by <c>FullName</c>, not by type identity: each product
        /// assembly has its own internal <c>IsExternalInit</c> polyfill and this test assembly has none.
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

        // ── Parse(null) is tolerated by all eight types ─────────────────────────────────────────────

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
    /// <see cref="Fill.LayoutProperties"/>: parsing <c>fill-sort-key</c>.
    ///
    /// <para>The ordering behaviour it drives is pinned engine-side by <c>FillSortKeyAndOpacityTests</c>;
    /// this fixture covers the parse, the default, and the zoom/feature capability that decides whether the
    /// key is evaluated per feature at build time or could be hoisted.</para>
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    /// </summary>
    [TestFixture]
    public class FillLayoutTests
    {
        [Test]
        public void SortKey_Absent_IsNull()
        {
            var layout = TestStyle.FillLayout();

            Assert.IsNull(layout.SortKey,
                "an absent fill-sort-key must be null so the builder can skip the sort entirely — that " +
                "is what keeps existing fill meshes byte-identical.");
        }

        [Test]
        public void SortKey_EmptyLayoutObject_IsStillNull()
        {
            var layout = TestStyle.FillLayout("{}");
            Assert.IsNull(layout.SortKey, "a layout object without the key is the same as no layout");
        }

        [Test]
        public void SortKey_Constant_IsParsedAndNotNull()
        {
            var layout = TestStyle.FillLayout(@"{""fill-sort-key"": 7}");

            Assert.IsNotNull(layout.SortKey, "an explicit key must NOT be treated as absent");
            Assert.AreEqual(7f, layout.SortKey.Evaluate(0.0), 1e-9);
            Assert.AreEqual(ExpressionKind.Constant, layout.SortKey.Kind);
        }

        [Test]
        public void SortKey_ZoomExpression_IsZoomDependent()
        {
            var layout = TestStyle.FillLayout(@"{""fill-sort-key"": [""interpolate"", [""linear""], [""zoom""], 0, 0, 10, 100]}");

            Assert.IsNotNull(layout.SortKey);
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
            var style = MapRenderer.Unity.Style.StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://e.invalid/{z}/{x}/{y}.pbf""] } },
                ""layers"": [ { ""id"": ""f"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""l"",
                                ""layout"": { ""fill-sort-key"": 3 } } ]
            }");

            var fillLayer = (Fill.StyleLayer)style.Layers[0];
            Assert.IsNotNull(fillLayer.Layout.SortKey, "the parser must route layout onto the typed layer");
            Assert.AreEqual(3f, fillLayer.Layout.SortKey.Evaluate(0.0), 1e-9);
        }

        [Test]
        public void SortKey_LegacyZoomFunction_DefaultsToInterval_NotRamp()
        {
            // fill-sort-key has no "interpolate" marker: an absent "type" steps (interval), it does not ramp.
            var layout = TestStyle.FillLayout(@"{""fill-sort-key"": {""stops"": [[10, 1], [14, 5]]}}");

            Assert.AreEqual(1f, layout.SortKey.Evaluate(12.0), 1e-9, "z12 is below the second stop (14)");
            Assert.AreEqual(5f, layout.SortKey.Evaluate(15.0), 1e-9);
        }

        [Test]
        public void SortKey_ZoomAndPropertyCategorical_OuterAxisSteps()
        {
            // fill-sort-key is non-interpolatable, so the OUTER zoom axis of a zoom-and-property function
            // steps between its inner (categorical) groups, instead of ramping between them.
            var layout = TestStyle.FillLayout(@"{""fill-sort-key"": {""property"":""cls"",""type"":""categorical"",
                ""stops"":[[{""zoom"":10,""value"":""a""},1],[{""zoom"":10,""value"":""b""},2],
                           [{""zoom"":14,""value"":""a""},3],[{""zoom"":14,""value"":""b""},4]]}}");

            var b = new DictionaryFeature(new System.Collections.Generic.Dictionary<string, Value>
                { ["cls"] = Value.String("b") });

            Assert.AreEqual(2f, layout.SortKey.Evaluate(12.0, b), 1e-9, "z12 is below the second zoom group (14)");
            Assert.AreEqual(4f, layout.SortKey.Evaluate(15.0, b), 1e-9);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillPaintTranslateTests — Fill.PaintProperties.Translate: the shared TranslateProperty route
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Its own fixture (not <see cref="FillPaintTests"/> in StyleRestyleTests.cs, which needs
    /// Unity.Collections for other siblings): every type named here is Core/Unity.Mathematics-engine-free,
    /// so it lives in this file for core-tests coverage.</summary>
    [TestFixture]
    public class FillPaintTranslateTests
    {
        [Test]
        public void Translate_ZoomExpression_ClassifiesAsZoom_NotCollapsedToDefault()
        {
            var paint = TestStyle.FillPaint(
                "{\"fill-translate\":[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "10,[\"literal\",[0,0]],16,[\"literal\",[20,-10]]]}");

            Assert.AreEqual(ExpressionKind.Zoom, paint.Translate.Kind,
                "a zoom-interpolate translate must classify as Zoom, not collapse to a Constant [0,0].");

            var atMin = paint.Translate.Evaluate(10.0);
            Assert.AreEqual(0.0, atMin.x, 0.01);
            Assert.AreEqual(0.0, atMin.y, 0.01);

            var atMax = paint.Translate.Evaluate(16.0);
            Assert.AreEqual(20.0, atMax.x, 0.01,
                "at zoom=16 the interpolated translate must reach its second stop, NOT stay at [0,0].");
            Assert.AreEqual(-10.0, atMax.y, 0.01);
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // FillPatternTests — Fill.FillPattern: resolving a fill-pattern sprite name against a sheet
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="Fill.FillPattern"/>: resolving a <c>fill-pattern</c> sprite name against a sheet into
    /// the rect + repeat count the fill shader samples with. The NEGATIVE case matters most: a pattern layer
    /// inherits the opaque-black <c>fill-color</c> default, so an unresolved pattern that fell back to colour
    /// would paint solid black. See docs/fill-parity-design.md.
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

        /// <summary>Every "cannot resolve" path reports unresolved (a ZERO-AREA rect, the shader's clip
        /// signal) — never a colour fallback: no pattern declared, the sheet not fetched yet, the name
        /// absent from the sheet, and a degenerate (zero-area) sprite rect.</summary>
        [Test]
        [TestCase(null, true, "a layer with no fill-pattern must not resolve a pattern",
            TestName = "Unresolved_ReportsUnresolved(NoPatternDeclared)")]
        [TestCase("plaza", false, "a null atlas (sheet not fetched yet) must report unresolved, not fall back to fill-color",
            TestName = "Unresolved_ReportsUnresolved(SheetHasNotArrivedYet)")]
        [TestCase("no-such-sprite", true, "a name absent from the sheet must report unresolved (spec: the layer is not painted)",
            TestName = "Unresolved_ReportsUnresolved(NameAbsentFromSheet)")]
        [TestCase("degenerate", true, "a zero-area sprite is not drawable and must report unresolved",
            TestName = "Unresolved_ReportsUnresolved(SpriteRectIsDegenerate)")]
        public void Unresolved_ReportsUnresolved(string patternName, bool useSheet, string message)
        {
            SpriteAtlasView atlas = useSheet ? Sheet() : null;
            Assert.IsFalse(Fill.FillPattern.TryResolve(patternName, atlas, out var r), message);
            Assert.IsFalse(r.IsResolved);
            Assert.AreEqual(0.0, r.Rect.z, "unresolved must be a ZERO-AREA rect — the shader's clip signal");
            Assert.AreEqual(0.0, r.Rect.w);
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
        // Non-obvious why: repeats are per WORLD UNIT, not per tile, because a tile's zoom is not the display
        // zoom — sources overzoom past z14 and a ScreenSpaceLod cover mixes levels in one frame.

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
            // Period is a smooth function of zoom: whole-number repeats per tile would snap while zooming, so
            // consecutive samples must never repeat a value.
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
            // Overzoom: past the source's maxzoom the tiles stay the same, so repeats depend on the DISPLAY zoom
            // only. Two display zooms one level apart differ by exactly 2×, whatever tiles are underneath.
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

        // ── The paint side of the same defect ────────────────────────────────────────────────────

        /// <summary>
        /// A pattern layer's absent fill-color defaults to white, because the shader multiplies the sprite by
        /// it: white leaves the sprite untinted. A solid fill keeps the spec's opaque black. Liberty's
        /// road_area_pattern is the pattern case verbatim.
        /// </summary>
        [Test]
        public void FillColorDefault_IsWhiteOnAPatternLayer_AndBlackOnASolidFill()
        {
            var pattern = TestStyle.FillPaint(@"{""fill-pattern"":""pedestrian_polygon""}").Color.Evaluate(0.0);
            Assert.AreEqual((1.0, 1.0, 1.0, 1.0), (pattern.R, pattern.G, pattern.B, pattern.A),
                "a pattern layer with no fill-color must default to opaque white, so the sprite shows untinted.");

            var solid = TestStyle.FillPaint(@"{""fill-opacity"":0.5}").Color.Evaluate(0.0);
            Assert.AreEqual((0.0, 0.0, 0.0, 1.0), (solid.R, solid.G, solid.B, solid.A),
                "a solid fill with no fill-color must keep the spec default, opaque black.");

            // An expression fill-pattern is not a sprite name, so the layer draws as a solid fill: black.
            var expression = TestStyle.FillPaint(@"{""fill-pattern"":[""get"",""p""]}").Color.Evaluate(0.0);
            Assert.AreEqual((0.0, 0.0, 0.0, 1.0), (expression.R, expression.G, expression.B, expression.A),
                "a layer whose fill-pattern is an expression draws solid, so it must keep opaque black.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // FillExtrusionPaintTests — FillExtrusion.PaintProperties: classification + spec defaults
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="FillExtrusion.PaintProperties"/>: classification + spec defaults, mirroring
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

        /// <summary>fill-extrusion-height across every input kind: a constant literal pins, absence uses the
        /// spec default (0), a zoom-interpolate ramps between its stops, and a data-driven <c>get</c>
        /// classifies Feature.</summary>
        [Test]
        public void Height_ParsesAcrossInputKinds()
        {
            var constant = MakeLayer("{\"fill-extrusion-height\":42}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, constant.Height.Kind, "a numeric literal must classify as Constant.");
            Assert.AreEqual(42f, constant.Height.Evaluate(0.0), 1e-4f, "fill-extrusion-height must pin to its literal value.");

            var absent = MakeLayer("{}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, absent.Height.Kind, "absent fill-extrusion-height must use spec default (Constant kind).");
            Assert.AreEqual(0f, absent.Height.Evaluate(0.0), 1e-6f, "default fill-extrusion-height must be 0.");

            var zoom = MakeLayer("{\"fill-extrusion-height\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0,16,50]}").Paint;
            Assert.AreEqual(ExpressionKind.Zoom, zoom.Height.Kind, "a zoom-interpolate expression must classify as Zoom.");
            Assert.AreEqual(0f, zoom.Height.Evaluate(10.0), 0.01f);
            Assert.AreEqual(50f, zoom.Height.Evaluate(16.0), 0.01f);

            var dataDriven = MakeLayer("{\"fill-extrusion-height\":[\"get\",\"height\"]}").Paint;
            Assert.AreEqual(ExpressionKind.Feature, dataDriven.Height.Kind, "a [\"get\",...] expression must classify as Feature.");
            Assert.IsTrue(dataDriven.Height.DependsOnFeature);
        }

        // ── base ──────────────────────────────────────────────────────────────

        /// <summary>fill-extrusion-base across every input kind: a constant literal pins, absence uses the
        /// spec default (0), a zoom-interpolate ramps between its stops, and a data-driven <c>get</c>
        /// classifies Feature.</summary>
        [Test]
        public void Base_ParsesAcrossInputKinds()
        {
            var constant = MakeLayer("{\"fill-extrusion-base\":5}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, constant.Base.Kind);
            Assert.AreEqual(5f, constant.Base.Evaluate(0.0), 1e-4f, "fill-extrusion-base must pin to its literal value.");

            var absent = MakeLayer("{}").Paint;
            Assert.AreEqual(0f, absent.Base.Evaluate(0.0), 1e-6f, "default fill-extrusion-base must be 0.");

            var zoom = MakeLayer("{\"fill-extrusion-base\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0,16,4]}").Paint;
            Assert.AreEqual(ExpressionKind.Zoom, zoom.Base.Kind, "a zoom-interpolate expression must classify as Zoom.");
            Assert.AreEqual(0f, zoom.Base.Evaluate(10.0), 0.01f);
            Assert.AreEqual(4f, zoom.Base.Evaluate(16.0), 0.01f);

            var dataDriven = MakeLayer("{\"fill-extrusion-base\":[\"get\",\"min_height\"]}").Paint;
            Assert.AreEqual(ExpressionKind.Feature, dataDriven.Base.Kind, "a [\"get\",...] expression must classify as Feature.");
            Assert.IsTrue(dataDriven.Base.DependsOnFeature);
        }

        // ── color ─────────────────────────────────────────────────────────────

        /// <summary>fill-extrusion-color across every input kind: a constant rgba pins, absence uses the
        /// spec default (opaque black), a zoom-interpolate ramps between its stop colors, and a data-driven
        /// <c>match</c> classifies Feature.</summary>
        [Test]
        public void Color_ParsesAcrossInputKinds()
        {
            var constant = MakeLayer("{\"fill-extrusion-color\":[\"rgba\",200,100,50,1]}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, constant.Color.Kind);
            var c = constant.Color.Evaluate(0.0);
            Assert.AreEqual(200.0 / 255.0, c.R, 1e-3, "Red channel must match rgba(200,100,50,1).");
            Assert.AreEqual(100.0 / 255.0, c.G, 1e-3, "Green channel must match rgba(200,100,50,1).");
            Assert.AreEqual(50.0 / 255.0, c.B, 1e-3, "Blue channel must match rgba(200,100,50,1).");

            var absent = MakeLayer("{}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, absent.Color.Kind, "absent fill-extrusion-color must use spec default (Constant kind).");
            var defaultColor = absent.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, defaultColor.R, 1e-4, "Default fill-extrusion-color R must be 0 (black).");
            Assert.AreEqual(0.0, defaultColor.G, 1e-4, "Default fill-extrusion-color G must be 0 (black).");
            Assert.AreEqual(0.0, defaultColor.B, 1e-4, "Default fill-extrusion-color B must be 0 (black).");
            Assert.AreEqual(1.0, defaultColor.A, 1e-4, "Default fill-extrusion-color must be fully opaque.");

            var zoom = MakeLayer("{\"fill-extrusion-color\":[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "10,[\"rgba\",255,0,0,1],16,[\"rgba\",0,0,255,1]]}").Paint;
            Assert.AreEqual(ExpressionKind.Zoom, zoom.Color.Kind, "a zoom-interpolate expression must classify as Zoom.");
            var atMin = zoom.Color.Evaluate(10.0);
            Assert.AreEqual(1.0, atMin.R, 1e-3, "At zoom=10, interpolated color must be the first stop (red).");
            Assert.AreEqual(0.0, atMin.B, 1e-3);
            var atMax = zoom.Color.Evaluate(16.0);
            Assert.AreEqual(0.0, atMax.R, 1e-3, "At zoom=16, interpolated color must be the second stop (blue).");
            Assert.AreEqual(1.0, atMax.B, 1e-3);

            var dataDriven = MakeLayer("{\"fill-extrusion-color\":[\"match\",[\"get\",\"type\"]," +
                "\"residential\",[\"rgba\",200,50,50,1]," +
                "[\"rgba\",128,128,128,1]]}").Paint;
            Assert.AreEqual(ExpressionKind.Feature, dataDriven.Color.Kind, "a [\"match\",[\"get\",...],...] expression must classify as Feature.");
            Assert.IsTrue(dataDriven.Color.DependsOnFeature);
        }

        // ── opacity ───────────────────────────────────────────────────────────

        /// <summary>fill-extrusion-opacity across every input kind: absence uses the spec default (1.0), a
        /// constant literal pins, a zoom-interpolate ramps between its stops, and a data-driven <c>get</c>
        /// classifies Feature.</summary>
        [Test]
        public void Opacity_ParsesAcrossInputKinds()
        {
            var absent = MakeLayer("{}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, absent.Opacity.Kind);
            Assert.AreEqual(1.0f, absent.Opacity.Evaluate(0.0), 1e-6f, "default fill-extrusion-opacity must be 1.0.");

            var constant = MakeLayer("{\"fill-extrusion-opacity\":0.5}").Paint;
            Assert.AreEqual(0.5f, constant.Opacity.Evaluate(0.0), 1e-6f);

            var zoom = MakeLayer("{\"fill-extrusion-opacity\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0.2,16,1.0]}").Paint;
            Assert.AreEqual(ExpressionKind.Zoom, zoom.Opacity.Kind, "a zoom-interpolate expression must classify as Zoom.");
            Assert.AreEqual(0.2f, zoom.Opacity.Evaluate(10.0), 0.01f);
            Assert.AreEqual(1.0f, zoom.Opacity.Evaluate(16.0), 0.01f);

            var dataDriven = MakeLayer("{\"fill-extrusion-opacity\":[\"get\",\"opacity\"]}").Paint;
            Assert.AreEqual(ExpressionKind.Feature, dataDriven.Opacity.Kind, "a [\"get\",...] expression must classify as Feature.");
            Assert.IsTrue(dataDriven.Opacity.DependsOnFeature);
        }

        // ── vertical-gradient ─────────────────────────────────────────────────

        [Test]
        [TestCase("{}", 1.0f, TestName = "VerticalGradient_MatchesExpected(Absent_UsesSpecDefault_True)")]
        [TestCase("{\"fill-extrusion-vertical-gradient\":false}", 0.0f, TestName = "VerticalGradient_MatchesExpected(False_EncodesAsZero)")]
        public void VerticalGradient_MatchesExpected(string paintJson, float expected)
        {
            var layer = MakeLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(expected, fp.VerticalGradient.Evaluate(0.0), 1e-6f,
                $"fill-extrusion-vertical-gradient ({paintJson}) must encode as {expected}.");
        }

        // ── translate-anchor ──────────────────────────────────────────────────

        [Test]
        [TestCase("{\"fill-extrusion-translate-anchor\":\"viewport\"}", 1.0f, TestName = "TranslateAnchor_MatchesExpected(Viewport_IsOne)")]
        [TestCase("{}", 0.0f, TestName = "TranslateAnchor_MatchesExpected(AbsentDefaultsToMap_IsZero)")]
        public void TranslateAnchor_MatchesExpected(string paintJson, float expected)
        {
            var layer = MakeLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(expected, fp.TranslateAnchor.Evaluate(0.0), 1e-6f,
                $"fill-extrusion-translate-anchor ({paintJson}) must encode as {expected}.");
        }

        // ── translate (parsed through the expression engine; mirrors the Height teeth above) ──

        /// <summary>fill-extrusion-translate across every input kind: a bare [x,y] constant array pins,
        /// absence uses the spec default [0,0], a zoom-interpolate ramps between its stops (never collapsing
        /// to the default), and a data-driven value — spec-invalid for a layer-level property — falls back
        /// to the Constant default rather than throwing.</summary>
        [Test]
        public void Translate_ParsesAcrossInputKinds()
        {
            // A bare [x,y] array — the common constant form real styles use — must parse (via
            // WrapBareArrayLiterals) rather than throw "operator must be a string".
            var constant = MakeLayer("{\"fill-extrusion-translate\":[12,-4]}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, constant.Translate.Kind);
            var constantT = constant.Translate.Evaluate(0.0);
            Assert.AreEqual(12.0, constantT.x, 1e-6, "fill-extrusion-translate.x must pin to its literal value.");
            Assert.AreEqual(-4.0, constantT.y, 1e-6, "fill-extrusion-translate.y must pin to its literal value.");

            var absent = MakeLayer("{}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, absent.Translate.Kind);
            var absentT = absent.Translate.Evaluate(0.0);
            Assert.AreEqual(0.0, absentT.x, 1e-6);
            Assert.AreEqual(0.0, absentT.y, 1e-6);

            // The array-valued stop outputs must be wrapped ["literal", [...]] per the Style Spec — the
            // engine's InterpolateExpression.Lerp handles ValueType.Array element-wise (Ops/Ramps.cs).
            var zoom = MakeLayer("{\"fill-extrusion-translate\":[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "10,[\"literal\",[0,0]],16,[\"literal\",[20,-10]]]}").Paint;
            Assert.AreEqual(ExpressionKind.Zoom, zoom.Translate.Kind,
                "a zoom-interpolate translate must classify as Zoom, not collapse to a Constant [0,0].");
            var zoomAtMin = zoom.Translate.Evaluate(10.0);
            Assert.AreEqual(0.0, zoomAtMin.x, 0.01);
            Assert.AreEqual(0.0, zoomAtMin.y, 0.01);
            var zoomAtMax = zoom.Translate.Evaluate(16.0);
            Assert.AreEqual(20.0, zoomAtMax.x, 0.01,
                "at zoom=16 the interpolated translate must reach its second stop, NOT stay at [0,0].");
            Assert.AreEqual(-10.0, zoomAtMax.y, 0.01);

            // fill-extrusion-translate is a layer-level property; a data-driven value is spec-invalid and
            // must fall back to the default rather than throw at bind time (mirrors VerticalGradient).
            var dataDriven = MakeLayer("{\"fill-extrusion-translate\":[\"get\",\"offset\"]}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, dataDriven.Translate.Kind,
                "a data-driven translate must fall back to the Constant default, not classify as Feature.");
            var dataDrivenT = dataDriven.Translate.Evaluate(0.0);
            Assert.AreEqual(0.0, dataDrivenT.x, 1e-6);
            Assert.AreEqual(0.0, dataDrivenT.y, 1e-6);
        }

        // ── StyleParser dispatch (Core half) ─────────────────────────────────────

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
    /// <see cref="Line.PaintProperties"/> / <see cref="Line.LayoutProperties"/>:
    /// classification, pinned values, translate-array parse (one <c>StyleProperty&lt;double2&gt;</c>), anchor
    /// encoding, pattern-name capture, and join/cap layout parse into <c>JoinType</c>/<c>CapType</c>.
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

            Assert.AreEqual(ExpressionKind.Constant, lp.Color.Kind,
                "An rgba(...) literal must classify as Constant.");
            Assert.IsFalse(lp.Color.DependsOnFeature,
                "Constant color must not depend on feature.");

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

            Assert.AreEqual(ExpressionKind.Zoom, lp.Width.Kind,
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

            Assert.AreEqual(ExpressionKind.Feature, lp.Color.Kind,
                "A [\"get\",...] match expression must classify as Feature.");
            Assert.IsTrue(lp.Color.DependsOnFeature,
                "Data-driven color must DependsOnFeature.");
        }

        /// <summary>Every line-paint property, absent, uses its own spec default: color black, opacity 1,
        /// width 1, blur 0, gap-width 0.</summary>
        [Test]
        public void LinePaint_AbsentProperty_UsesSpecDefault()
        {
            var lp = MakeLineLayer("{}").Paint;

            Assert.AreEqual(ExpressionKind.Constant, lp.Color.Kind,
                "Absent line-color must use spec default (Constant kind).");
            Assert.IsFalse(lp.Color.DependsOnFeature);
            var c = lp.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Default line-color R must be 0 (black).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Default line-color G must be 0 (black).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Default line-color B must be 0 (black).");

            Assert.AreEqual(ExpressionKind.Constant, lp.Opacity.Kind);
            Assert.AreEqual(1.0f, lp.Opacity.Evaluate(0.0), 1e-6f, "Default line-opacity must be 1.0.");

            Assert.AreEqual(ExpressionKind.Constant, lp.Width.Kind);
            Assert.AreEqual(1.0f, lp.Width.Evaluate(0.0), 1e-6f, "Default line-width must be 1.0.");

            Assert.AreEqual(0.0f, lp.Blur.Evaluate(0.0), 1e-6f, "Default line-blur must be 0.0.");

            Assert.AreEqual(0.0f, lp.GapWidth.Evaluate(0.0), 1e-6f, "Default line-gap-width must be 0.0.");
        }

        // ── line-gap-width present → parsed ────────────────────────────────────

        [Test]
        public void LinePaint_GapWidth_Present_Parsed()
        {
            var layer = MakeLineLayer("{\"line-gap-width\":8.0}");
            var lp    = layer.Paint;

            float v = lp.GapWidth.Evaluate(0.0);
            Assert.AreEqual(8.0f, v, 1e-6f, "line-gap-width must be 8.0.");
        }

        /// <summary>line-translate parses or falls back across every input shape: a bare [x,y] constant
        /// pins, a zoom-interpolate expression ramps (not collapsing to the default), a legacy
        /// <c>{"stops":…}</c> function gives the same two values as its modern equivalent, and a
        /// data-driven value or a too-short array (constant OR zoom-kind) all fall back to the default
        /// rather than throwing.</summary>
        [Test]
        public void LinePaint_Translate_ParsesOrFallsBackAcrossInputShapes()
        {
            var constant = MakeLineLayer("{\"line-translate\":[16,-8]}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, constant.Translate.Kind);
            var constantT = constant.Translate.Evaluate(0.0);
            Assert.AreEqual(16.0, constantT.x, 1e-6, "line-translate x must be 16.");
            Assert.AreEqual(-8.0, constantT.y, 1e-6, "line-translate y must be -8.");

            var zoom = MakeLineLayer("{\"line-translate\":[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "10,[\"literal\",[0,0]],16,[\"literal\",[20,-10]]]}").Paint;
            Assert.AreEqual(ExpressionKind.Zoom, zoom.Translate.Kind,
                "a zoom-interpolate translate must classify as Zoom, not collapse to a Constant [0,0].");
            var zoomAtMin = zoom.Translate.Evaluate(10.0);
            Assert.AreEqual(0.0, zoomAtMin.x, 0.01);
            Assert.AreEqual(0.0, zoomAtMin.y, 0.01);
            var zoomAtMax = zoom.Translate.Evaluate(16.0);
            Assert.AreEqual(20.0, zoomAtMax.x, 0.01,
                "at zoom=16 the interpolated translate must reach its second stop, NOT stay at [0,0].");
            Assert.AreEqual(-10.0, zoomAtMax.y, 0.01);

            // The shared TranslateProperty route also parses a legacy {"stops": …} function, through the
            // same WrapBareArrayLiterals + ExpressionParser path as the modern form above.
            var legacyStops = MakeLineLayer("{\"line-translate\":{\"stops\":[[10,[0,0]],[16,[20,-10]]]}}").Paint;
            var legacyAtMin = legacyStops.Translate.Evaluate(10.0);
            Assert.AreEqual(0.0, legacyAtMin.x, 0.01);
            Assert.AreEqual(0.0, legacyAtMin.y, 0.01);
            var legacyAtMax = legacyStops.Translate.Evaluate(16.0);
            Assert.AreEqual(20.0, legacyAtMax.x, 0.01);
            Assert.AreEqual(-10.0, legacyAtMax.y, 0.01);

            var dataDriven = MakeLineLayer("{\"line-translate\":[\"get\",\"t\"]}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, dataDriven.Translate.Kind,
                "a data-driven translate must fall back to the Constant default, not classify as Feature.");
            var dataDrivenT = dataDriven.Translate.Evaluate(0.0);
            Assert.AreEqual(0.0, dataDrivenT.x, 1e-6);
            Assert.AreEqual(0.0, dataDrivenT.y, 1e-6);

            var shortArray = MakeLineLayer("{\"line-translate\":[5]}").Paint;
            var shortArrayT = shortArray.Translate.Evaluate(0.0);
            Assert.AreEqual(0.0, shortArrayT.x, 1e-6);
            Assert.AreEqual(0.0, shortArrayT.y, 1e-6);

            // A CONSTANT short array is caught once, at parse/construction time. A ZOOM-kind one is evaluated
            // through the per-frame span fast path instead (StyleProperty.EvalProjected), uncaught at that
            // call site — a short array there must still fall back to Default, not throw IndexOutOfRange.
            var zoomLayer = MakeLineLayer("{\"line-translate\":[\"step\",[\"zoom\"],[5],10,[1,2]]}");
            var zoomLp    = zoomLayer.Paint;
            Assert.AreEqual(ExpressionKind.Zoom, zoomLp.Translate.Kind,
                "precondition: a step-by-zoom translate must classify as Zoom, or this exercises the constant path.");
            var zoomShort = zoomLp.Translate.Evaluate(5.0); // below the "10" step: the short [5] branch
            Assert.AreEqual(0.0, zoomShort.x, 1e-6);
            Assert.AreEqual(0.0, zoomShort.y, 1e-6);
            var zoomLong = zoomLp.Translate.Evaluate(12.0); // at/past "10": the well-formed [1,2] branch
            Assert.AreEqual(1.0, zoomLong.x, 1e-6);
            Assert.AreEqual(2.0, zoomLong.y, 1e-6);
        }

        /// <summary>line-translate-anchor encodes viewport as 1.0 and map (including absence, defaulting to
        /// map) as 0.0.</summary>
        [Test]
        [TestCase("{\"line-translate-anchor\":\"viewport\"}", 1.0f, TestName = "LinePaint_TranslateAnchor_MatchesExpected(Viewport_IsOne)")]
        [TestCase("{\"line-translate-anchor\":\"map\"}", 0.0f, TestName = "LinePaint_TranslateAnchor_MatchesExpected(Map_IsZero)")]
        public void LinePaint_TranslateAnchor_MatchesExpected(string paintJson, float expected)
        {
            var lp = MakeLineLayer(paintJson).Paint;
            Assert.AreEqual(ExpressionKind.Constant, lp.TranslateAnchor.Kind);
            Assert.AreEqual(expected, lp.TranslateAnchor.Evaluate(0.0), 1e-6f,
                $"line-translate-anchor ({paintJson}) must encode as {expected}.");
        }

        /// <summary>line-join/line-cap/line-miter-limit/line-round-limit each parse from layout (typed enums
        /// for join/cap), and an entirely absent layout uses every one's spec default.</summary>
        [Test]
        public void LinePaint_Layout_ParsesJoinCapMiterRoundLimit()
        {
            var joinCap = MakeLineLayer("{}", "{\"line-join\":\"round\",\"line-cap\":\"square\"}").Layout;
            Assert.AreEqual(JoinType.Round, joinCap.Join, "line-join='round' must parse to JoinType.Round.");
            Assert.AreEqual(CapType.Square, joinCap.Cap,  "line-cap='square' must parse to CapType.Square.");

            var joinBevel = MakeLineLayer("{}", "{\"line-join\":\"bevel\"}").Layout;
            Assert.AreEqual(JoinType.Bevel, joinBevel.Join, "line-join='bevel' must parse to JoinType.Bevel.");

            var capRound = MakeLineLayer("{}", "{\"line-cap\":\"round\"}").Layout;
            Assert.AreEqual(CapType.Round, capRound.Cap, "line-cap='round' must parse to CapType.Round.");

            var miterLimit = MakeLineLayer("{}", "{\"line-miter-limit\":5.0}").Layout;
            Assert.AreEqual(5.0, miterLimit.MiterLimit, 1e-6, "line-miter-limit must be parsed from layout.");

            var roundLimit = MakeLineLayer("{}", "{\"line-round-limit\":1.2}").Layout;
            Assert.AreEqual(1.2, roundLimit.RoundLimit, 1e-6, "line-round-limit must be parsed from layout.");

            var absent = MakeLineLayer("{}").Layout;
            Assert.AreEqual(JoinType.Miter, absent.Join,       "Default line-join must be JoinType.Miter.");
            Assert.AreEqual(CapType.Butt,   absent.Cap,        "Default line-cap must be CapType.Butt.");
            Assert.AreEqual(2.0,            absent.MiterLimit, 1e-6, "Default miter-limit must be 2.0.");
            Assert.AreEqual(1.05,           absent.RoundLimit, 1e-6, "Default round-limit must be 1.05.");
        }

        // ── Line-offset ──────────────────────────────────────────────────────────

        /// <summary>line-offset across every value form: a positive constant classifies Constant, absence
        /// defaults to 0.0, a negative constant parses correctly, and a zoom-interpolate expression
        /// classifies Zoom.</summary>
        [Test]
        public void LinePaint_Offset_ParsesAcrossValueForms()
        {
            var constant = MakeLineLayer("{\"line-offset\":5}").Paint;
            Assert.AreEqual(ExpressionKind.Constant, constant.Offset.Kind,
                "A numeric line-offset must classify as Constant.");
            Assert.AreEqual(5.0f, constant.Offset.Evaluate(0.0), 1e-6f, "line-offset must evaluate to 5.0.");

            var absent = MakeLineLayer("{}").Paint;
            Assert.AreEqual(0.0f, absent.Offset.Evaluate(0.0), 1e-6f, "Absent line-offset must default to 0.0.");

            var negative = MakeLineLayer("{\"line-offset\":-3}").Paint;
            Assert.AreEqual(-3.0f, negative.Offset.Evaluate(0.0), 1e-6f, "Negative line-offset must parse correctly.");

            var zoom = MakeLineLayer("{\"line-offset\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0,14,8]}").Paint;
            Assert.AreEqual(ExpressionKind.Zoom, zoom.Offset.Kind,
                "A zoom-interpolate line-offset must classify as Zoom kind.");
        }

        // ── #5: line-pattern hook → PatternName is set ──────────────────────────

        [Test]
        public void LinePaint_PatternName_IsParsed()
        {
            var layer = MakeLineLayer("{\"line-pattern\":\"road_shield\"}");
            var lp    = layer.Paint;

            Assert.AreEqual("road_shield", lp.PatternName, "line-pattern must be read as PatternName.");
        }

        // ── PropertyNames value constants ───────────────────────────────────────

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
    /// <see cref="Background.PaintProperties"/>: spec defaults, explicit parse, and zoom
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
        }

        // ── Explicit parse ────────────────────────────────────────────────────────

        /// <summary>Each explicit background-paint value parses: a color string, a color rgba array,
        /// an opacity number, and a pattern string.</summary>
        [Test]
        public void BackgroundPaint_ExplicitValues_Parse()
        {
            var colorString = MakeBackgroundLayer("{\"background-color\":\"#ff0000\"}").Paint;
            var cs = colorString.Color.Evaluate(0.0);
            Assert.AreEqual(1.0, cs.R, 1e-4, "Red channel must be 1.0 for #ff0000.");
            Assert.AreEqual(0.0, cs.G, 1e-4);
            Assert.AreEqual(0.0, cs.B, 1e-4);

            var colorRgba = MakeBackgroundLayer("{\"background-color\":[\"rgba\",0,255,0,1]}").Paint;
            var cr = colorRgba.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, cr.R, 1e-4);
            Assert.AreEqual(1.0, cr.G, 1e-4, "Green channel must be 1.0 for rgba(0,255,0,1).");
            Assert.AreEqual(0.0, cr.B, 1e-4);

            var opacity = MakeBackgroundLayer("{\"background-opacity\":0.5}").Paint;
            Assert.AreEqual(0.5f, opacity.Opacity.Evaluate(0.0), 1e-6f);

            var pattern = MakeBackgroundLayer("{\"background-pattern\":\"stripes\"}").Paint;
            Assert.AreEqual("stripes", pattern.PatternName);
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
    /// A <c>symbol</c> layer parses to the typed <see cref="SymbolStyle.StyleLayer"/> (NOT the
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

            // text-field is a StyleProperty<string>, evaluated per feature.
            var nameFeature = new DictionaryFeature(
                new Dictionary<string, Value> { ["NAME"] = Value.String("Test") }, TileGeometryType.Point);
            Assert.AreEqual("Test", sym.Layout.TextField.Evaluate(0.0, nameFeature),
                "the {NAME} token text-field must resolve against a feature.");

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
            Assert.AreEqual(new double2(0.0, 0.0), sym.Paint.Translate.Evaluate(0.0), "text-translate default is [0,0]");
            Assert.AreEqual(TextTranslateAnchor.Map, sym.Paint.TranslateAnchor, "text-translate-anchor default is map");

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
        public void SymbolLayer_SymbolSortKey_LegacyStopsFunction_DefaultsToInterval_NotRamp()
        {
            // symbol-sort-key has no "interpolate" marker: an absent "type" steps, it does not ramp.
            var sym = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'symbol-sort-key':{'stops':[[10,1],[14,5]]} } } ] }").Layers[0];

            Assert.AreEqual(1f, sym.Layout.SymbolSortKey.Evaluate(12.0), 1e-6, "z12 is below the second stop (14)");
            Assert.AreEqual(5f, sym.Layout.SymbolSortKey.Evaluate(15.0), 1e-6);
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

            var iconFeature = new DictionaryFeature(
                new Dictionary<string, Value> { ["icon"] = Value.String("marker") }, TileGeometryType.Point);
            Assert.AreEqual("marker", sym.Layout.IconImage.Evaluate(0.0, iconFeature),
                "the [\"get\",\"icon\"] icon-image must resolve against a feature.");
            Assert.AreEqual(2f, sym.Layout.IconSize.Evaluate(0.0), 1e-6);
            Assert.AreEqual(5f, sym.Layout.IconPadding.Evaluate(0.0), 1e-6);
            Assert.AreEqual(new float2(3, 4), sym.Layout.IconOffset);
            Assert.AreEqual(TextAnchor.TopLeft, sym.Layout.IconAnchor, "hyphenated 'top-left' → TopLeft");
            Assert.AreEqual(AlignmentMode.Map, sym.Layout.IconRotationAlignment);
            Assert.IsTrue(sym.Layout.IconAllowOverlap, "icon-allow-overlap:true must parse to true");
            Assert.IsTrue(sym.Layout.IconIgnorePlacement, "icon-ignore-placement:true must parse to true");

            Assert.AreEqual(0.5f, sym.Paint.IconOpacity.Evaluate(0.0), 1e-6);
        }

        // ── C1 — icon-optional / text-optional parse as plain layout booleans ──────────────────────────────
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

        // Icon/text parse independence: AlignmentMode has too few values for four keys in one layer, so each
        // of FOUR layers sets ONE {text,icon}-{rotation,pitch} key to 'map'; a crossed key reads wrong in one.
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

        // ── icon-rotate parses as a zoom-capable float, spec default 0. ──
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

            Assert.AreEqual(new double2(4, 6), sym.Paint.Translate.Evaluate(0.0),
                "text-translate parses to [x,y] px (raw y-down)");
            Assert.AreEqual(ExpressionKind.Constant, sym.Paint.Translate.Kind);
            Assert.AreEqual(TextTranslateAnchor.Viewport, sym.Paint.TranslateAnchor);

            // Zoom-capable: text-translate is re-evaluated per frame (SymbolPlacementSystem), so an
            // interpolate expression must be honoured, not collapsed to the constant default.
            var zoomed = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','paint':{
                    'text-translate':['interpolate',['linear'],['zoom'],0,[0,0],10,[20,10]] } } ] }").Layers[0];
            Assert.AreEqual(ExpressionKind.Zoom, zoomed.Paint.Translate.Kind);
            Assert.AreEqual(new double2(0, 0), zoomed.Paint.Translate.Evaluate(0.0), "z0 -> [0,0]");
            Assert.AreEqual(new double2(10, 5), zoomed.Paint.Translate.Evaluate(5.0), "z5 -> the linear midpoint");
            Assert.AreEqual(new double2(20, 10), zoomed.Paint.Translate.Evaluate(10.0), "z10 -> [20,10]");
        }

        [Test]
        public void SymbolLayer_TextFont_PlainArrayAndExpressionForms()
        {
            var bare = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c' } ] }").Layers[0];
            CollectionAssert.AreEqual(new[] { "Open Sans Regular", "Arial Unicode MS Regular" },
                bare.Layout.TextFont.Evaluate(0.0), "text-font default is the spec stack");

            // The common constant form: a bare array of font names (no ["literal", …] wrapper).
            var plain = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-font':['Noto Sans Regular'] } } ] }").Layers[0];
            CollectionAssert.AreEqual(new[] { "Noto Sans Regular" }, plain.Layout.TextFont.Evaluate(0.0));

            // The expression form: ["literal", […]] must read the wrapped array, not its own operator token.
            var literalExpr = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-font':['literal',['Noto Sans Regular']] } } ] }").Layers[0];
            CollectionAssert.AreEqual(new[] { "Noto Sans Regular" }, literalExpr.Layout.TextFont.Evaluate(0.0),
                "an expression-form text-font must evaluate, not read its array items as font names");

            // Zoom-capable: a step on zoom between two literal stacks.
            var zoomed = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-font':['step',['zoom'],['literal',['Low Font']],10,['literal',['High Font']]] } } ] }")
                .Layers[0];
            CollectionAssert.AreEqual(new[] { "Low Font" }, zoomed.Layout.TextFont.Evaluate(0.0));
            CollectionAssert.AreEqual(new[] { "High Font" }, zoomed.Layout.TextFont.Evaluate(12.0));
        }

        [Test]
        public void SymbolLayer_TextFont_LegacyStopsFunction_StepsNotRamps()
        {
            // text-font has no "interpolate" marker, so a legacy stops function steps between stacks
            // instead of ramping.
            var stepped = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-font':{'stops':[[10,['Low Font']],[14,['High Font']]]} } } ] }").Layers[0];
            CollectionAssert.AreEqual(new[] { "Low Font" }, stepped.Layout.TextFont.Evaluate(0.0));
            CollectionAssert.AreEqual(new[] { "High Font" }, stepped.Layout.TextFont.Evaluate(14.0));
        }

        [Test]
        public void SymbolLayer_TextFont_DataDrivenExpression_DegradesToDefaultStack()
        {
            // ["get",…] has the same all-string shape as a plain name array. The font stack has no
            // per-feature resolution site, so TryEvaluate(zoom, null) must fail and degrade to the default.
            var dataDriven = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'text-font':['get','fontProp'] } } ] }").Layers[0];
            bool ok = dataDriven.Layout.TextFont.TryEvaluate(12.0, null, out string[] degraded);
            Assert.IsFalse(ok, "a data-driven text-font has no feature at the per-layer evaluation site");
            CollectionAssert.AreEqual(new[] { "Open Sans Regular", "Arial Unicode MS Regular" }, degraded,
                "TryEvaluate must degrade to the spec default stack, not the literal tokens [\"get\",\"fontProp\"]");
        }

        [Test]
        public void SymbolLayer_IconPadding_ArrayForm_ParsesAndCollapsesToLargestSide()
        {
            // A bare number stays a plain scalar (unaffected by the array-form fix).
            var scalar = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{ 'icon-padding':6 } } ] }")
                .Layers[0];
            Assert.AreEqual(6f, scalar.Layout.IconPadding.Evaluate(0.0), 1e-6);

            // The spec's array form must parse (not fail the whole style) and collapse to the largest entry.
            var oneEntry = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{ 'icon-padding':[3] } } ] }")
                .Layers[0];
            Assert.AreEqual(3f, oneEntry.Layout.IconPadding.Evaluate(0.0), 1e-6, "[n] means all sides n");

            var fourEntry = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'icon-padding':[2,4,6,8] } } ] }").Layers[0];
            Assert.AreEqual(8f, fourEntry.Layout.IconPadding.Evaluate(0.0), 1e-6,
                "[top,right,bottom,left] collapses to the largest entry");
        }

        /// <summary>A zoom-interpolated icon-padding array evaluates (matching stop lengths, including a
        /// 4-entry array that also looks like an rgb()/rgba() colour, so Ramps.Lerp must take the
        /// element-wise array branch before colour coercion) or falls back via TryEvaluate for genuinely
        /// mismatched stop lengths (Evaluate itself still throws — surviving it is the caller's job).</summary>
        [Test]
        public void IconPadding_ZoomInterpolatedArray_EvaluatesOrFallsBack()
        {
            var matchingLengths = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'icon-padding':['interpolate',['linear'],['zoom'],0,[2,4],10,[8,16]] } } ] }").Layers[0];
            // z5 lerps each entry to [5,10] (halfway between [2,4] and [8,16]); the largest is 10.
            Assert.AreEqual(10f, matchingLengths.Layout.IconPadding.Evaluate(5.0), 1e-4);

            var fourEntries = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'icon-padding':['interpolate',['linear'],['zoom'],0,[2,4,6,8],10,[8,16,24,32]] } } ] }")
                .Layers[0];
            // z5 lerps each entry to [5,10,15,20] (halfway); the largest is 20.
            Assert.AreEqual(20f, fourEntries.Layout.IconPadding.Evaluate(5.0), 1e-4);

            var mismatched = (SymbolStyle.StyleLayer)Parse(@"{ 'version':8, 'layers':[
                { 'id':'a','type':'symbol','source':'s','source-layer':'c','layout':{
                    'icon-padding':['interpolate',['linear'],['zoom'],0,[2],10,[8,16]] } } ] }").Layers[0];
            Assert.Throws<ExpressionEvaluationException>(() => mismatched.Layout.IconPadding.Evaluate(5.0));
            bool ok = mismatched.Layout.IconPadding.TryEvaluate(5.0, null, out float fallback);
            Assert.IsFalse(ok);
            Assert.AreEqual(2f, fallback, 1e-6, "TryEvaluate must degrade to the spec default (2px)");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolStringPropertyTests — Symbol.LayoutProperties.{TextField,IconImage}: shared parse/resolve path
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>text-field</c> and <c>icon-image</c> share one path (<c>LayoutProperties.ParseSymbolString</c>):
    /// token sugar (<c>{prop}</c>, desugared to <c>["concat",…]</c> at parse time) and expression form
    /// (<c>["get",…]</c>/<c>["coalesce",…]</c>) both resolve to a string, empty for a missing property.
    /// Exercised once against <c>text-field</c>; <c>SharedPath_TokenResolvesWithFeatureKind_LegacyObjectIsNull</c>
    /// is the tooth proving <c>icon-image</c> rides the same path. Engine-free; runs in both runners.
    /// </summary>
    [TestFixture]
    public class SymbolStringPropertyTests
    {
        // Author the property's value with single quotes, then swap to real quotes, wrapped in a layout.
        private static SymbolStyle.LayoutProperties Layout(string valueJson, string property = "text-field")
            => SymbolStyle.LayoutProperties.Parse(
                JsonParser.Parse(("{'" + property + "':" + valueJson + "}").Replace('\'', '"')));

        // The raw evaluated string — null only when the property is absent or malformed. The
        // empty/whitespace-skip decision is the extractor's, not StyleProperty's (see StyleShieldTests.cs).
        private static string Resolve(SymbolStyle.LayoutProperties layout, IFeature feature, double zoom)
            => layout.TextField != null && layout.TextField.TryEvaluate(zoom, feature, out string resolved)
                ? resolved
                : null;

        private static IFeature Feature(params (string key, string val)[] props)
        {
            var dict = new Dictionary<string, Value>();
            foreach (var (key, val) in props) dict[key] = Value.String(val);
            return new DictionaryFeature(dict, TileGeometryType.Point);
        }

        private static readonly IFeature Aruba = Feature(("NAME", "Aruba"), ("A", "foo"), ("B", "bar"));
        private static readonly IFeature Afghanistan = Feature(("NAME", "Afghanistan"), ("ABBREV", "Afg."));

        [Test]
        public void Token_SingleProperty_Resolves()
        {
            Assert.AreEqual("Aruba", Resolve(Layout("'{NAME}'"), Aruba, 0.0));
        }

        [Test]
        public void Expression_Get_Resolves()
        {
            Assert.AreEqual("Aruba", Resolve(Layout("['get','NAME']"), Aruba, 0.0));
        }

        [Test]
        public void Token_MultiTokenWithLiterals_Resolves()
        {
            Assert.AreEqual("Afghanistan (Afg.)", Resolve(Layout("'{NAME} ({ABBREV})'"), Afghanistan, 0.0));
        }

        [Test]
        public void Expression_CoalesceFallback_Resolves()
        {
            // name:en absent → coalesce falls back to NAME.
            Assert.AreEqual("Aruba",
                Resolve(Layout("['coalesce',['get','name:en'],['get','NAME']]"), Aruba, 0.0));
        }

        [Test]
        public void MissingProperty_Token_ResolvesToEmpty()
        {
            Assert.AreEqual("", Resolve(Layout("'{missing}'"), Aruba, 0.0), "an unknown token resolves to empty text.");
        }

        [Test]
        public void MissingProperty_Expression_ResolvesToEmpty()
        {
            Assert.AreEqual("", Resolve(Layout("['get','missing']"), Aruba, 0.0),
                "a get on a missing property resolves to empty text.");
        }

        [TestCase("'Airport'", "Airport", TestName = "LiteralNoTokens_PassesThrough")]
        [TestCase("42", "42", TestName = "BareNumber_RendersToString")]
        [TestCase("true", "true", TestName = "BareBool_RendersToString")]
        [TestCase("'{A}{B}'", "foobar", TestName = "Token_AdjacentTokens_NoLiteralBetween_Resolves")]
        [TestCase("'a{'", "a{", TestName = "Token_UnterminatedBrace_TreatsRestAsLiteral")]
        [TestCase("'{}'", "", TestName = "Token_EmptyBraces_ResolvesToEmpty")]
        public void Template_Resolves(string json, string expected)
        {
            Assert.AreEqual(expected, Resolve(Layout(json), Aruba, 0.0));
        }

        [Test]
        public void NullField_Skips()
        {
            Assert.IsNull(Resolve(TestStyle.SymbolLayout(), Aruba, 0.0));
        }

        [Test]
        public void Expression_ZoomStep_EvaluatesAtTheGivenZoom_NotZero()
        {
            var layout = Layout("['step',['zoom'],'low',10,'high']");
            Assert.AreEqual("low", Resolve(layout, Aruba, 0.0),
                "below the first stop, zoom 0 reads the default output");
            Assert.AreEqual("low", Resolve(layout, Aruba, 5.0),
                "below the first stop, zoom 5 reads the same default output as zoom 0");
            Assert.AreEqual("high", Resolve(layout, Aruba, 12.0),
                "a text-field zoom step must read the CALLER's zoom, not always zoom 0");
        }

        [Test]
        public void PlainLiteral_IsConstant_TokenForm_IsFeature()
        {
            Assert.AreEqual(ExpressionKind.Constant, Layout("'Airport'").TextField.Kind,
                "a plain literal text-field must classify as Constant.");
            Assert.AreEqual(ExpressionKind.Feature, Layout("'{NAME}'").TextField.Kind,
                "a token text-field must classify as Feature — Kind is now observable on text-field.");
        }

        // ── The wiring tooth: icon-image rides the identical ParseSymbolString path as text-field ──────

        [TestCase("text-field")]
        [TestCase("icon-image")]
        public void SharedPath_TokenResolvesWithFeatureKind_LegacyObjectIsNull(string property)
        {
            var layout = Layout("'{NAME}'", property);
            StyleProperty<string> prop = property == "text-field" ? layout.TextField : layout.IconImage;
            Assert.AreEqual("Aruba", prop.Evaluate(0.0, Aruba));
            Assert.AreEqual(ExpressionKind.Feature, prop.Kind);

            var legacy = Layout("{'stops':[[0,'a']]}", property);
            StyleProperty<string> legacyProp = property == "text-field" ? legacy.TextField : legacy.IconImage;
            Assert.IsNull(legacyProp, $"a legacy function object is never routed to the parser for {property}.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // LineDashTests — Line.LineDash: dash coverage, zoom-stability, width coupling, dasharray parse/eval
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="Line.LineDash"/>: dash coverage function, zoom-stability, width
    /// coupling, dasharray parse/eval, and shader structure (tooth 4 — greppable assertion).
    ///
    /// <c>TryEvaluatePattern</c> returns two <c>float4</c>s + <c>int count</c> (alloc-free);
    /// <c>DashCoverage(float[])</c> is the CPU mirror.
    /// </summary>
    [TestFixture]
    public class LineDashTests
    {
        // ── DECISIVE: Dash ratio 2:1 for [2, 1] ──────────────────────────────────────────────

        [Test]
        public void DashCoverage_Pattern_2_1_ProducesCorrectOnOffRatio()
        {
            float[] pattern = new float[] { 2f, 1f };
            double widthM = 1.0;

            int onCount = 0;
            int offCount = 0;
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

        /// <summary>Every degenerate DashCoverage input is always-on (coverage 1.0): a null pattern, an
        /// empty pattern, and a zero/negative dash-width-in-metres. An odd-length pattern is NOT degenerate
        /// under N=8 — it repeats with a parity flip; see
        /// <see cref="DashCoverage_OddLength_RepeatsWithParityFlip_PastNIsSolid"/>.</summary>
        [Test]
        public void DashCoverage_DegenerateInputs_AlwaysOn()
        {
            for (int i = 0; i < 20; i++)
                Assert.That(Line.LineDash.DashCoverage(i * 0.5, 1.0, null), Is.EqualTo(1.0f));

            for (int i = 0; i < 20; i++)
                Assert.That(Line.LineDash.DashCoverage(i * 0.5, 1.0, new float[0]), Is.EqualTo(1.0f));

            float[] pattern = new float[] { 2f, 1f };
            Assert.That(Line.LineDash.DashCoverage(5.0, 0.0, pattern), Is.EqualTo(1.0f));
            Assert.That(Line.LineDash.DashCoverage(5.0, -1.0, pattern), Is.EqualTo(1.0f));
        }

        // ── Dash ratio, continued: On/Off regions are hard binary ────────────────────────────

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

        // ── Zoom-stable ───────────────────────────────────────────────────────────────────────

        [Test]
        public void DashCoverage_ZoomStable_CycleCountTimesWidthMIsConstantAcrossZoom()
        {
            // Under the 512 convention GroundResolution(13) equals the 256-convention z14, so this
            // discretization-sensitive check keeps bit-identical widthM and cycle counts.
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

        // ── Width-coupled ─────────────────────────────────────────────────────────────────────

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

        // ── Odd-length repeats over 2P with parity flip; past N=8 entries is solid, never truncated ──

        [Test]
        public void DashCoverage_OddLength_RepeatsWithParityFlip_PastNIsSolid()
        {
            // [2,1,3]: P=6, period=12. Second half (phase>=6) re-walks the same entries with parity flipped.
            float[] threeEntry = { 2f, 1f, 3f };
            double[] us        = { 1, 2.5, 4, 7, 8.5, 10, 13 };
            float[]  expected  = { 1f, 0f, 1f, 0f, 1f, 0f, 1f };
            for (int i = 0; i < us.Length; i++)
                Assert.That(Line.LineDash.DashCoverage(us[i], 1.0, threeEntry), Is.EqualTo(expected[i]),
                    $"[2,1,3] at u={us[i]}");

            // [2] alone: P=2, period=4 — a lone dash behaves like [2,2], never doubling its own array.
            float[] oneEntry = { 2f };
            Assert.That(Line.LineDash.DashCoverage(1, 1.0, oneEntry), Is.EqualTo(1f), "[2] at u=1: on");
            Assert.That(Line.LineDash.DashCoverage(3, 1.0, oneEntry), Is.EqualTo(0f), "[2] at u=3: off (flipped half)");
            Assert.That(Line.LineDash.DashCoverage(5, 1.0, oneEntry), Is.EqualTo(1f), "[2] at u=5: on (next period)");

            // 7 ones: P=7, period=14. u=7.5 falls just past the P boundary — the flipped half.
            float[] sevenOnes = { 1f, 1f, 1f, 1f, 1f, 1f, 1f };
            Assert.That(Line.LineDash.DashCoverage(7.5, 1.0, sevenOnes), Is.EqualTo(0f),
                "[1x7] at u=7.5 is in the flipped half: off");

            // 8 (even) entries fit whole — N=8 must not truncate its own boundary case.
            float[] eight = { 1f, 1f, 1f, 1f, 1f, 1f, 2f, 2f };
            Assert.That(Line.LineDash.DashCoverage(7.5, 1.0, eight), Is.EqualTo(1f),
                "8 entries must not be truncated (period sums to 10; u=7.5 is inside the 7th, on, entry)");

            // Past N=8: solid at every phase, not a truncated 8-entry read.
            float[] nine = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
            for (int i = 0; i < 20; i++)
                Assert.That(Line.LineDash.DashCoverage(i * 0.7, 1.0, nine), Is.EqualTo(1.0f), "> N entries must be solid");
        }

        // ── TryEvaluatePattern → two float4s + count (alloc-free) ────────────────────────────

        /// <summary>TryEvaluatePattern packs or degrades across every dash-array shape: two-, four- and
        /// eight-entry patterns pack into lo/hi + count; an odd-length pattern packs too (it repeats — it
        /// is not solid identity) and a negative entry clamps to 0; a null expression returns false with
        /// count 0. The N=8 entry-count boundary itself is
        /// <see cref="TryEvaluatePattern_EntryCountBoundary"/>.</summary>
        [Test]
        public void TryEvaluatePattern_PacksOrDegrades()
        {
            // [2, 1] → lo.x=2, lo.y=1, count=2, hi unused
            var twoEntry = Line.LineDash.ParseDashArray(JsonParser.Parse("[2, 1]"));
            Assert.IsNotNull(twoEntry, "ParseDashArray must succeed for [2,1].");
            bool twoOk = Line.LineDash.TryEvaluatePattern(twoEntry, 14.0, out float4 twoLo, out float4 twoHi, out int twoCount);
            Assert.IsTrue(twoOk, "TryEvaluatePattern must return true for a valid [2,1] pattern.");
            Assert.AreEqual(2, twoCount, "Count must be 2 for a two-entry pattern.");
            Assert.AreEqual(2f, twoLo.x, 1e-6f, "lo.x must be 2 (first dash length).");
            Assert.AreEqual(1f, twoLo.y, 1e-6f, "lo.y must be 1 (first gap length).");
            Assert.AreEqual(0f, twoLo.z, 1e-6f, "lo.z must be 0 (unused).");
            Assert.AreEqual(0f, twoLo.w, 1e-6f, "lo.w must be 0 (unused).");
            Assert.AreEqual(float4.zero, twoHi, "hi must be 0 (unused) for a two-entry pattern.");

            var fourEntry = Line.LineDash.ParseDashArray(JsonParser.Parse("[4, 1, 1, 1]"));
            bool fourOk = Line.LineDash.TryEvaluatePattern(fourEntry, 10.0, out float4 fourLo, out float4 fourHi, out int fourCount);
            Assert.IsTrue(fourOk);
            Assert.AreEqual(4, fourCount);
            Assert.AreEqual(new float4(4f, 1f, 1f, 1f), fourLo);
            Assert.AreEqual(float4.zero, fourHi, "hi must stay zero for a 4-entry pattern.");

            // Nothing above exercises the hi half at all.
            var eightEntry = Line.LineDash.ParseDashArray(JsonParser.Parse("[1, 2, 3, 4, 5, 6, 7, 8]"));
            bool eightOk = Line.LineDash.TryEvaluatePattern(eightEntry, 10.0, out float4 eightLo, out float4 eightHi, out int eightCount);
            Assert.IsTrue(eightOk);
            Assert.AreEqual(8, eightCount);
            Assert.AreEqual(new float4(1f, 2f, 3f, 4f), eightLo);
            Assert.AreEqual(new float4(5f, 6f, 7f, 8f), eightHi);

            // [2, 1, 3] is odd-length — it now repeats (DashCoverage), it is not solid identity.
            var oddEntry = Line.LineDash.ParseDashArray(JsonParser.Parse("[2, 1, 3]"));
            bool oddOk = Line.LineDash.TryEvaluatePattern(oddEntry, 10.0, out float4 oddLo, out float4 oddHi, out int oddCount);
            Assert.IsTrue(oddOk, "Odd-length must return true — it repeats, not falls back to solid.");
            Assert.AreEqual(3, oddCount);
            Assert.AreEqual(new float4(2f, 1f, 3f, 0f), oddLo);
            Assert.AreEqual(float4.zero, oddHi);

            // A negative entry packs as 0, matching DashCoverage's own clamp (LineDash.cs), so the CPU
            // mirror and what the shader reads never disagree over a malformed negative span.
            var negEntry = Line.LineDash.ParseDashArray(JsonParser.Parse("[2, -1, 3]"));
            bool negOk = Line.LineDash.TryEvaluatePattern(negEntry, 10.0, out float4 negLo, out _, out int negCount);
            Assert.IsTrue(negOk);
            Assert.AreEqual(3, negCount);
            Assert.AreEqual(new float4(2f, 0f, 3f, 0f), negLo, "the negative entry must clamp to 0, not pack as -1.");

            bool nullOk = Line.LineDash.TryEvaluatePattern(null, 10.0, out _, out _, out int nullCount);
            Assert.IsFalse(nullOk, "Null expression must return false.");
            Assert.AreEqual(0, nullCount);
        }

        [TestCase(5, true, 5)]   // odd, well within N — survives whole, not truncated
        [TestCase(8, true, 8)]   // exactly N — still valid
        [TestCase(9, false, 0)]  // past N — solid, never truncated to N
        public void TryEvaluatePattern_EntryCountBoundary(int entries, bool expectOk, int expectCount)
        {
            string json = "[" + string.Join(",", Enumerable.Repeat("1", entries)) + "]";
            var expr = Line.LineDash.ParseDashArray(JsonParser.Parse(json));
            bool ok = Line.LineDash.TryEvaluatePattern(expr, 10.0, out float4 lo, out float4 hi, out int count);
            Assert.AreEqual(expectOk, ok, $"{entries} entries");
            Assert.AreEqual(expectCount, count, $"{entries} entries");
        }

        // ── ParseDashArray + TryEvaluatePattern: expression system integration ────────────────

        private static MapRenderer.Core.Expressions.Expression DashExpr(string json)
            => Line.LineDash.ParseDashArray(JsonParser.Parse(json));

        /// <summary>ParseDashArray classifies and evaluates every VALID expression form: constant/literal,
        /// zoom-step, a legacy stops function (interval, not ramp), and zoom-interpolate over a
        /// colour-shaped array (never colour-coerced). Degenerate inputs are
        /// <see cref="ParseDashArray_NullOrDataDriven_DegradesToSolid"/>.</summary>
        [Test]
        public void ParseDashArray_ClassifiesAndEvaluatesAcrossExpressionForms()
        {
            var constantBareArray = DashExpr("[2, 1]");
            Assert.That(constantBareArray, Is.Not.Null);
            Assert.That(constantBareArray.Kind, Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Constant),
                "A bare constant dasharray must classify as Constant.");
            bool constantOk = Line.LineDash.TryEvaluatePattern(constantBareArray, 14.0, out float4 constantPacked, out _, out int constantCount);
            Assert.That(constantOk, Is.True);
            Assert.That(constantCount, Is.EqualTo(2));
            Assert.That(constantPacked.x, Is.EqualTo(2f));
            Assert.That(constantPacked.y, Is.EqualTo(1f));

            var literalExpr = DashExpr("[\"literal\", [4, 2]]");
            Assert.That(literalExpr.Kind, Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Constant));
            Line.LineDash.TryEvaluatePattern(literalExpr, 10.0, out float4 literalPacked, out _, out int literalCount);
            Assert.That(literalCount, Is.EqualTo(2));
            Assert.That(literalPacked.x, Is.EqualTo(4f));
            Assert.That(literalPacked.y, Is.EqualTo(2f));

            var zoomStep = DashExpr("[\"step\", [\"zoom\"], [1,1], 10, [2,1], 14, [4,1]]");
            Assert.That(zoomStep.Kind, Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Zoom),
                "A zoom-step dasharray must classify as Zoom.");
            Line.LineDash.TryEvaluatePattern(zoomStep, 9.0,  out float4 p9, out _, out _);
            Assert.That(p9.x, Is.EqualTo(1f), "zoom=9: default [1,1]");
            Line.LineDash.TryEvaluatePattern(zoomStep, 10.0, out float4 p10, out _, out _);
            Assert.That(p10.x, Is.EqualTo(2f), "zoom=10: [2,1]");
            Line.LineDash.TryEvaluatePattern(zoomStep, 14.0, out float4 p14, out _, out _);
            Assert.That(p14.x, Is.EqualTo(4f), "zoom=14: [4,1]");
            Line.LineDash.TryEvaluatePattern(zoomStep, 15.0, out float4 p15, out _, out _);
            Assert.That(p15.x, Is.EqualTo(4f), "zoom=15: still [4,1]");

            // line-dasharray has no "interpolate" marker, so an absent "type" steps; each stop output is an
            // array, wrapped as a literal (the spec's stop-output rule) rather than parsed as an operator call.
            var legacyStops = DashExpr("{\"stops\":[[10,[2,1]],[14,[4,2]]]}");
            Line.LineDash.TryEvaluatePattern(legacyStops, 13.0, out float4 p13, out _, out int c13);
            Assert.AreEqual(new float4(2, 1, 0, 0), p13);
            Assert.AreEqual(2, c13);
            Line.LineDash.TryEvaluatePattern(legacyStops, 15.0, out float4 p15b, out _, out int c15);
            Assert.AreEqual(new float4(4, 2, 0, 0), p15b);
            Assert.AreEqual(2, c15);

            // A 4-entry dash pattern also looks like an rgb()/rgba() colour; Ramps.Lerp must take the
            // array branch first, or this silently draws solid (a non-array TryEvaluatePattern result).
            var zoomInterpolate = DashExpr("[\"interpolate\",[\"linear\"],[\"zoom\"],0,[2,1,2,1],10,[4,2,4,2]]");
            bool interpOk = Line.LineDash.TryEvaluatePattern(zoomInterpolate, 5.0, out float4 interpPacked, out _, out int interpCount);
            Assert.IsTrue(interpOk, "a matching-length zoom-interpolated dash array must evaluate, not fall back to solid");
            Assert.AreEqual(4, interpCount);
            Assert.AreEqual(new float4(3f, 1.5f, 3f, 1.5f), interpPacked);
        }

        /// <summary>Two degenerate dasharray inputs both degrade to solid rather than crashing: a null array
        /// parses to null, and a data-driven (feature-dependent) array cannot resolve without a feature, so
        /// <c>TryEvaluatePattern</c> must not attempt it.</summary>
        [Test]
        public void ParseDashArray_NullOrDataDriven_DegradesToSolid()
        {
            Assert.That(Line.LineDash.ParseDashArray(null), Is.Null);
            bool nullOk = Line.LineDash.TryEvaluatePattern(null, 10.0, out _, out _, out int nullCount);
            Assert.That(nullOk, Is.False);
            Assert.That(nullCount, Is.EqualTo(0));

            var dataDriven = DashExpr("[\"match\", [\"get\", \"cls\"], \"a\", [2,1], [4,2]]");
            Assert.That(MapRenderer.Core.Expressions.ExpressionKinds.DependsOnFeature(dataDriven.Kind), Is.True);
            Assert.That(Line.LineDash.TryEvaluatePattern(dataDriven, 10.0, out _, out _, out int dataDrivenCount), Is.False,
                "Data-driven dasharray must not crash — it degrades to solid (no pattern).");
            Assert.That(dataDrivenCount, Is.EqualTo(0));
        }

        // ── GREPPABLE: Shader consumes per-vertex sideAndDist.y as dashU ──────────────────────

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

            // The shared Line_VertexExtrude helper computes dashU; the forward pass carries it in uv.x and
            // the fragment reads uv.x for dash coverage.
            Assert.That(text, Does.Contain("Line_VertexExtrude("),
                "Line_LitForwardPass.hlsl must call the shared Line_VertexExtrude helper (which sources dashU).");
            Assert.That(text, Does.Contain("output.uv"),
                "Vertex shader must carry the line coordinates (dashU/side/innerFrac) in output.uv.");
            Assert.That(text, Does.Contain("input.uv.x"),
                "Fragment shader must read the dash coordinate from input.uv.x.");

            // _DashCount: the dash logic lives in Line_VertexExtrude.hlsl, shared by all passes and
            // resolved by name, so the guard is a single-site check there.
            string extrudeText = File.ReadAllText(EngineFreeShaderPaths.ResolveMapShaderPath("Line_VertexExtrude.hlsl"));
            Assert.That(extrudeText, Does.Contain("sideAndDist.y"),
                "Line_VertexExtrude.hlsl must consume per-vertex distanceAlong (input.sideAndDist.y) to form dashU.");
            Assert.That(extrudeText, Does.Contain("dashU"),
                "Line_VertexExtrude.hlsl must emit the dashU dash coordinate.");
            Assert.That(extrudeText, Does.Contain("_DashCount"),
                "Line_VertexExtrude.hlsl must have _DashCount guard for solid identity (dash logic lives in shared helper).");
        }

        // ── the CPU mirror's pointer to its HLSL twin must name a file that exists ───────────────

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
        /// <see cref="Line.LineDash"/> is the declared single source of truth for the dash
        /// function and points at its HLSL mirror by name. Those pointers must name the file that actually
        /// carries the mirror — <c>Line_VertexExtrude.hlsl</c>. Every <c>*.hlsl</c> token in the file is also
        /// resolved on disk, so a renamed shader file turns the tooth red.
        /// </summary>
        [Test]
        public void ShaderPointers_InLineDashSource_ResolveOnDisk()
        {
            string sourcePath = FindRepoFile(
                "Assets", "Code", "MapRenderer.Unity", "Style", "Line", "LineDash.cs");
            Assert.That(sourcePath, Is.Not.Null,
                $"LineDash.cs not found. Tried walking up 16 levels from " +
                $"cwd={Directory.GetCurrentDirectory()} and AppContext.BaseDirectory={AppContext.BaseDirectory}");
            string text = File.ReadAllText(sourcePath);

            Assert.That(text, Does.Contain("Line_VertexExtrude.hlsl"),
                "LineDash.cs must point at Line_VertexExtrude.hlsl — the file that carries the HLSL mirror " +
                "of DashCoverage, and the file the dash divisor lives in.");
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

        // ── LinePaint.DashArray parse integration ─────────────────────────────────────────────

        private static Line.StyleLayer MakeDashLayer(string paintJson)
        {
            return new Line.StyleLayer
            {
                Id          = "test-dash",
                LayerType   = MapRenderer.Unity.Style.StyleLayerType.Line,
                SourceLayer = "roads",
                Paint       = TestStyle.LinePaint(paintJson),
                Layout      = TestStyle.LineLayout(),
            };
        }

        [Test]
        public void LinePaint_DashArray_NullWhenAbsent()
        {
            var layer = MakeDashLayer("{\"line-width\":2}");
            var paint = layer.Paint;
            Assert.That(paint.DashArray, Is.Null);
        }

        [Test]
        public void LinePaint_DashArray_ParsedWhenPresent()
        {
            var layer = MakeDashLayer("{\"line-dasharray\":[2,1]}");
            var paint = layer.Paint;
            Assert.That(paint.DashArray, Is.Not.Null);
            Assert.That(paint.DashArrayKind,
                Is.EqualTo(MapRenderer.Core.Expressions.ExpressionKind.Constant));
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
    // LightSkyTests — root light/sky blocks: parse, spec defaults, malformed input
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="StyleLight.Parse"/> and <see cref="StyleSky.Parse"/>: spec defaults when the block or a key is
    /// absent, explicit values parse, and malformed values fall back rather than throw (except a malformed
    /// or data-driven color/number expression, which throws at eager parse like every other paint property).
    /// </summary>
    [TestFixture]
    public class LightSkyTests
    {
        private static JsonValue Root(string json) => JsonParser.Parse(json);

        // ── light: defaults ──────────────────────────────────────────────────────

        [Test]
        public void Light_AbsentBlock_UsesSpecDefaults()
        {
            var light = StyleLight.Parse(null);

            var pos = light.Position.Evaluate(0.0);
            Assert.AreEqual(1.15, pos.Radial, 1e-6);
            Assert.AreEqual(210.0, pos.Azimuthal.Degrees, 1e-6);
            Assert.AreEqual(30.0, pos.Polar.Degrees, 1e-6);

            var c = light.Color.Evaluate(0.0);
            Assert.AreEqual(1.0, c.R, 1e-6);
            Assert.AreEqual(1.0, c.G, 1e-6);
            Assert.AreEqual(1.0, c.B, 1e-6);
            Assert.AreEqual(1.0, c.A, 1e-6);

            Assert.AreEqual(0.5f, light.Intensity.Evaluate(0.0), 1e-6f);
        }

        // ── light: explicit parse ────────────────────────────────────────────────

        [Test]
        public void Light_ExplicitValues_Parse()
        {
            var root = Root("{\"light\":{\"position\":[2.0,90,45],\"color\":\"#ff0000\",\"intensity\":0.8}}");
            var light = StyleLight.Parse(root.Get("light"));

            var pos = light.Position.Evaluate(0.0);
            Assert.AreEqual(2.0, pos.Radial, 1e-6);
            Assert.AreEqual(90.0, pos.Azimuthal.Degrees, 1e-6);
            Assert.AreEqual(45.0, pos.Polar.Degrees, 1e-6);

            var c = light.Color.Evaluate(0.0);
            Assert.AreEqual(1.0, c.R, 1e-4);
            Assert.AreEqual(0.0, c.G, 1e-4);

            Assert.AreEqual(0.8f, light.Intensity.Evaluate(0.0), 1e-6f);
        }

        [Test]
        public void Light_AnchorKey_IsIgnored()
        {
            // anchor is parsed by no member of Light; a style setting it must not throw and must still
            // apply the other keys.
            var root = Root("{\"light\":{\"anchor\":\"viewport\",\"intensity\":0.9}}");
            var light = StyleLight.Parse(root.Get("light"));

            Assert.AreEqual(0.9f, light.Intensity.Evaluate(0.0), 1e-6f);
        }

        // ── light: malformed input ───────────────────────────────────────────────

        [Test]
        public void Light_MalformedPositionArray_FallsBackToDefault()
        {
            var root = Root("{\"light\":{\"position\":[1.0]}}"); // too few elements
            var light = StyleLight.Parse(root.Get("light"));

            var pos = light.Position.Evaluate(0.0);
            Assert.AreEqual(1.15, pos.Radial, 1e-6, "A short position array must fall back to the default.");
        }

        [Test]
        public void Light_NonArrayPosition_FallsBackToDefault()
        {
            var root = Root("{\"light\":{\"position\":\"nope\"}}");
            var light = StyleLight.Parse(root.Get("light"));

            var pos = light.Position.Evaluate(0.0);
            Assert.AreEqual(1.15, pos.Radial, 1e-6);
        }

        [Test]
        public void Light_ZoomExpressionColor_Classifies()
        {
            var root = Root("{\"light\":{\"color\":[\"interpolate\",[\"linear\"],[\"zoom\"],0,\"#000000\",10,\"#ffffff\"]}}");
            var light = StyleLight.Parse(root.Get("light"));

            Assert.AreEqual(ExpressionKind.Zoom, light.Color.Kind);
        }

        /// <summary>Light.color and Light.intensity are both expression-backed, each behind its own guard
        /// call, so an invalid value fails at eager parse rather than deferring to an apply-time throw or a
        /// default fallback.</summary>
        [Test]
        // Unlike position, color is expression-backed: a bad value throws at eager parse, like any other
        // paint color, instead of falling back to the default.
        [TestCase("\"color\":\"notacolor\"", TestName = "Light_InvalidValue_FailsParse(Color_Malformed)")]
        // A legacy identity function on a feature property is data-driven (Feature kind): SunLight has no
        // feature to evaluate against, so the parse must fail rather than defer to a throw at apply.
        [TestCase("\"color\":{\"type\":\"identity\",\"property\":\"c\"}", TestName = "Light_InvalidValue_FailsParse(Color_LegacyPropertyFunction)")]
        // A modern ["get",...] color is data-driven too, and the guard covers it the same as identity.
        [TestCase("\"color\":[\"get\",\"c\"]", TestName = "Light_InvalidValue_FailsParse(Color_ModernGet)")]
        // Intensity gets its own guard call, independent of color's.
        [TestCase("\"intensity\":[\"get\",\"i\"]", TestName = "Light_InvalidValue_FailsParse(DataDrivenIntensity)")]
        public void Light_InvalidValue_FailsParse(string lightProperty)
        {
            var root = Root($"{{\"light\":{{{lightProperty}}}}}");
            Assert.Throws<ExpressionEvaluationException>(() => StyleLight.Parse(root.Get("light")));
        }

        [Test]
        public void Sky_LegacyPropertyFunctionFogColor_FailsParse()
        {
            // SkyGradient/DistanceHaze evaluate sky colors with no feature — a data-driven value must fail
            // the parse, the same guard as light.color.
            var root = Root("{\"sky\":{\"fog-color\":{\"type\":\"identity\",\"property\":\"c\"}}}");
            Assert.Throws<ExpressionEvaluationException>(() => StyleSky.Parse(root.Get("sky")));
        }

        // ── sky: defaults ─────────────────────────────────────────────────────────

        [Test]
        public void Sky_AbsentBlock_UsesSpecDefaults()
        {
            var sky = StyleSky.Parse(null);

            var sc = sky.SkyColor.Evaluate(0.0);
            Assert.AreEqual(0x88 / 255.0, sc.R, 1e-4);
            Assert.AreEqual(0xC6 / 255.0, sc.G, 1e-4);
            Assert.AreEqual(0xFC / 255.0, sc.B, 1e-4);

            var hc = sky.HorizonColor.Evaluate(0.0);
            Assert.AreEqual(1.0, hc.R, 1e-4);
            Assert.AreEqual(1.0, hc.G, 1e-4);
            Assert.AreEqual(1.0, hc.B, 1e-4);

            var fc = sky.FogColor.Evaluate(0.0);
            Assert.AreEqual(1.0, fc.R, 1e-4);
        }

        // ── sky: explicit parse ──────────────────────────────────────────────────

        [Test]
        public void Sky_FogColorOnly_ParsesFogLeavesOthersDefault()
        {
            var root = Root("{\"sky\":{\"fog-color\":\"#ff0000\"}}");
            var sky  = StyleSky.Parse(root.Get("sky"));

            var fc = sky.FogColor.Evaluate(0.0);
            Assert.AreEqual(1.0, fc.R, 1e-4);
            Assert.AreEqual(0.0, fc.G, 1e-4);

            var sc = sky.SkyColor.Evaluate(0.0);
            Assert.AreEqual(0x88 / 255.0, sc.R, 1e-4);
        }

        [Test]
        public void Sky_AllThreeColors_Parse()
        {
            var root = Root("{\"sky\":{\"sky-color\":\"#010203\",\"horizon-color\":\"#040506\",\"fog-color\":\"#070809\"}}");
            var sky  = StyleSky.Parse(root.Get("sky"));

            Assert.AreEqual(0x01 / 255.0, sky.SkyColor.Evaluate(0.0).R, 1e-4);
            Assert.AreEqual(0x04 / 255.0, sky.HorizonColor.Evaluate(0.0).R, 1e-4);
            Assert.AreEqual(0x07 / 255.0, sky.FogColor.Evaluate(0.0).R, 1e-4);
        }

        // ── sky: unmodeled blend keys are ignored, not thrown ───────────────────

        [Test]
        public void Sky_BlendKeys_AreIgnored()
        {
            var root = Root(
                "{\"sky\":{\"sky-horizon-blend\":0.2,\"horizon-fog-blend\":0.3,\"fog-ground-blend\":0.4," +
                "\"atmosphere-blend\":0.5,\"fog-color\":\"#00ff00\"}}");
            var sky = StyleSky.Parse(root.Get("sky"));

            Assert.AreEqual(1.0, sky.FogColor.Evaluate(0.0).G, 1e-4);
        }

        [Test]
        public void Sky_ZoomExpressionFogColor_Classifies()
        {
            var root = Root(
                "{\"sky\":{\"fog-color\":[\"interpolate\",[\"linear\"],[\"zoom\"],0,\"#000000\",10,\"#ffffff\"]}}");
            var sky = StyleSky.Parse(root.Get("sky"));

            Assert.AreEqual(ExpressionKind.Zoom, sky.FogColor.Kind);
        }

        // ── StyleParser integration ──────────────────────────────────────────────

        [Test]
        public void StyleParser_PopulatesLightAndSky()
        {
            var doc = StyleParser.Parse(
                "{\"version\":8,\"layers\":[],\"light\":{\"intensity\":0.7}," +
                "\"sky\":{\"fog-color\":\"#123456\"}}");

            Assert.IsNotNull(doc.Light);
            Assert.AreEqual(0.7f, doc.Light.Intensity.Evaluate(0.0), 1e-6f);

            Assert.IsNotNull(doc.Sky);
            Assert.AreEqual(0x12 / 255.0, doc.Sky.FogColor.Evaluate(0.0).R, 1e-4);
        }

        [Test]
        public void StyleParser_AbsentLightAndSky_UseDefaults()
        {
            var doc = StyleParser.Parse("{\"version\":8,\"layers\":[]}");

            Assert.IsNotNull(doc.Light);
            Assert.AreEqual(0.5f, doc.Light.Intensity.Evaluate(0.0), 1e-6f);

            Assert.IsNotNull(doc.Sky);
            Assert.AreEqual(1.0, doc.Sky.FogColor.Evaluate(0.0).R, 1e-4);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SpriteReferenceParseTests — root `sprite`: string form and array form
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SpriteReferenceParseTests
    {
        [Test]
        public void StringForm_ParsesToOneDefaultEntry()
        {
            var doc = StyleParser.Parse("{\"version\":8,\"sprite\":\"https://example.invalid/sprite\",\"layers\":[]}");

            Assert.AreEqual(1, doc.Sprites.Count);
            Assert.AreEqual("default", doc.Sprites[0].Id);
            Assert.AreEqual("https://example.invalid/sprite", doc.Sprites[0].Url);
        }

        [Test]
        public void ArrayForm_KeepsEveryIdInDeclaredOrder()
        {
            var doc = StyleParser.Parse(
                "{\"version\":8,\"sprite\":[" +
                "{\"id\":\"default\",\"url\":\"https://example.invalid/a\"}," +
                "{\"id\":\"extra\",\"url\":\"https://example.invalid/b\"}" +
                "],\"layers\":[]}");

            Assert.AreEqual(2, doc.Sprites.Count);
            Assert.AreEqual("default", doc.Sprites[0].Id);
            Assert.AreEqual("https://example.invalid/a", doc.Sprites[0].Url);
            Assert.AreEqual("extra", doc.Sprites[1].Id);
            Assert.AreEqual("https://example.invalid/b", doc.Sprites[1].Url);
        }

        [Test]
        public void ArrayForm_MalformedEntry_IsDroppedNotThrown()
        {
            var doc = StyleParser.Parse(
                "{\"version\":8,\"sprite\":[" +
                "{\"id\":\"good\",\"url\":\"https://example.invalid/a\"}," +
                "{\"id\":\"missingUrl\"}," +
                "{\"url\":\"https://example.invalid/missing-id\"}," +
                "\"not-an-object\"" +
                "],\"layers\":[]}");

            Assert.AreEqual(1, doc.Sprites.Count, "only the well-formed entry must survive");
            Assert.AreEqual("good", doc.Sprites[0].Id);
        }

        [Test]
        [TestCase("{\"version\":8,\"layers\":[]}", TestName = "Sprite_YieldsAnEmptyList(Absent)")]
        // A number where the spec expects a string or an array — tolerate rather than throw.
        [TestCase("{\"version\":8,\"sprite\":42,\"layers\":[]}", TestName = "Sprite_YieldsAnEmptyList(MalformedType)")]
        public void Sprite_YieldsAnEmptyList(string styleJson)
        {
            var doc = StyleParser.Parse(styleJson);

            Assert.AreEqual(0, doc.Sprites.Count);
        }
    }
}
