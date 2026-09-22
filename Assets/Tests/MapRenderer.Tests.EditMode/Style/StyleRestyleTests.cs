// Style/StyleRestyleTests.cs — data-driven color baking, style-document parsing, symbol feature
// extraction (icon + text), Fill.PaintProperties, and the retired whole-document restyle gate. Several
// members exercise MapRenderer.Jobs.Mvt / Unity.Collections and are not registered in core-tests.csproj.
//
// StyleTransitionTests.cs/StyleSymbolTests.cs (UnityEngine-side, bare `Object`/`Color` = UnityEngine's)
// and DevicePixelRatioBindingTests.cs stay separate files: this file's bare `Color` means
// MapRenderer.Core.Expressions.Color (CS0104 otherwise) — see docs/conventions-short.md's "Plain-import
// collisions" note.
//
// Contents:
//   DataDrivenColorBakeTests           — data-driven vs constant color: which carrier bakes it, uniform or stream.
//   LibertyNightColorOnlyRestyleTests  — a color-only restyle over the liberty style survives in place.
//   StyleParserTests                   — a MapLibre Style JSON parses into the typed model; all layer types dispatched.
//   SymbolFeatureExtractorIconTests    — SymbolFeatureExtractor.Extract's icon path (icon-image -> SymbolQuad).
//   SymbolFeatureExtractorTests        — SymbolFeatureExtractor.Extract over the fixture's centroids layer.
//   FillPaintTests                     — Fill.PaintProperties: classification, pinned values, BakeNumbers distinct-alpha.
//   WholeDocumentGate                  — the retired whole-document restyle gate, composing SurvivingLayerGate's predicates.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using Background = MapRenderer.Core.Style.Background;
using MapRenderer.Jobs.Tiles;
using Unity.Mathematics;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.Tiles;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests.TestSupport;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Tests.Style
{

    // ───────────────────────────────────────────────────────────────────────────────────
    // DataDrivenColorBakeTests — data-driven vs constant color: which carrier bakes it, uniform or stream
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S12 / S60 — data-driven paint evaluation over the real fixture: <see cref="StyleProperty{T}"/>
    /// resolved once per decoded MVT feature, which is what <c>StyledFillTileBuilder</c> and
    /// <c>StyledLineTileBuilder</c> do per feature while meshing a tile.
    ///
    /// The fixture (Assets/Fixtures/sample-tile.bytes) contains the "countries" layer with 239 polygon
    /// features and a "CONTINENT" string property (confirmed: 8 distinct values, including "Asia" and
    /// "South America").
    ///
    /// Teeth:
    ///   1. Distinct-color tooth: a match expression on CONTINENT produces ≥2 distinct colors across
    ///      the 239 baked features (robust against fixture ordering).
    ///   2. Constant-input control: an expression that resolves identically for all features (match on
    ///      a non-existent key → default) produces exactly 1 distinct color across all features.
    ///   3. Constant-kind evaluator: bake with a constant expression → all colors identical.
    /// </summary>
    [TestFixture]
    public class DataDrivenColorBakeTests
    {

        /// <summary>IR C1 P3: the address the committed fixture is decoded at (its buffers are stamped with
        /// it). z0/0/0 — the fixture's own tile.</summary>
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        /// <summary>IR C1 P3: a decoded tile owns Allocator.Persistent buffers — release them per test.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
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
                $"sample-tile.bytes not found. Tried walking up from " +
                $"cwd={Directory.GetCurrentDirectory()} and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        private static MvtLayer LoadCountries()
        {
            var layer = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");
            Assert.IsNotNull(layer, "countries layer must be present in fixture.");
            return layer;
        }

        private static List<IFeature> AdaptFeatures(MvtLayer layer)
        {
            var features = new List<IFeature>(layer.Features.Count);
            foreach (var f in layer.Features)
                features.Add(f); // A6: MvtFeature implements IFeature directly — no adapter
            return features;
        }

        /// <summary>
        /// One evaluation per feature, mirroring the tile builders' inner loop. A failed evaluation
        /// (expression error, wrong type) yields <paramref name="fallback"/> for that feature.
        /// </summary>
        private static List<Color> BakeColors(
            StyleProperty<Color> prop, double zoom, IEnumerable<IFeature> features, Color fallback = default)
        {
            var result = new List<Color>();
            foreach (var feature in features)
                result.Add(prop.TryEvaluate(zoom, feature, out Color c) ? c : fallback);
            return result;
        }

        private static StyleProperty<Color> ColProp(string json)
            => new StyleProperty<Color>(
                MapRenderer.Core.Json.JsonParser.Parse(json), new Color(0, 0, 0, 1), v => v.AsColorCoerced());

        private static HashSet<(int r, int g, int b)> DistinctRgb(List<Color> colors)
        {
            var distinct = new HashSet<(int r, int g, int b)>();
            foreach (var c in colors)
                distinct.Add(
                    ((int)Math.Round(c.R * 255), (int)Math.Round(c.G * 255), (int)Math.Round(c.B * 255)));
            return distinct;
        }

        private const string MatchExpr =
            "[\"match\",[\"get\",\"CONTINENT\"]," +
            "\"Asia\",[\"rgba\",200,50,50,1]," +
            "\"South America\",[\"rgba\",50,50,200,1]," +
            "[\"rgba\",128,128,128,1]]";

        // ── 1. Distinct-color tooth ───────────────────────────────────────────

        [Test]
        public void BakeColors_DistinctContinent_ProducesDistinctColors()
        {
            var layer    = LoadCountries();
            var features = AdaptFeatures(layer);
            var prop     = ColProp(MatchExpr);

            List<Color> colors = BakeColors(prop, 0.0, features);

            Assert.AreEqual(features.Count, colors.Count,
                "BakeColors must return one color per input feature.");

            var distinct = DistinctRgb(colors);

            Assert.GreaterOrEqual(distinct.Count, 2,
                $"A match expression on CONTINENT must produce ≥2 distinct colors (got {distinct.Count}).");
        }

        // ── 2. Constant-input control → exactly 1 distinct color ─────────────

        [Test]
        public void BakeColors_ConstantControl_ProducesUniformColor()
        {
            const string controlExpr =
                "[\"match\",[\"get\",\"__NONEXISTENT_KEY__\"]," +
                "\"x\",[\"rgba\",255,0,0,1]," +
                "[\"rgba\",77,77,77,1]]";

            var layer    = LoadCountries();
            var features = AdaptFeatures(layer);
            var prop     = ColProp(controlExpr);

            List<Color> colors = BakeColors(prop, 0.0, features);

            Assert.AreEqual(features.Count, colors.Count, "BakeColors must return one color per feature.");

            var distinct = DistinctRgb(colors);

            Assert.AreEqual(1, distinct.Count,
                $"Constant-input control must produce exactly 1 distinct color (got {distinct.Count}).");
        }

        // ── 3. Constant-kind evaluator → all colors identical ────────────────

        [Test]
        public void BakeColors_ConstantEvaluator_AllIdentical()
        {
            var layer    = LoadCountries();
            var features = AdaptFeatures(layer);
            var prop     = ColProp("\"#3a8f3a\"");

            List<Color> colors = BakeColors(prop, 0.0, features);

            Assert.AreEqual(features.Count, colors.Count);

            Color expected = colors[0];
            for (int i = 1; i < colors.Count; i++)
                Assert.AreEqual(expected, colors[i],
                    $"Constant-kind evaluator must produce the same color for every feature (index {i} differs).");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // LibertyNightColorOnlyRestyleTests — a color-only restyle over the liberty style survives in place
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// UMR-143: pins <c>liberty-night.json</c> as a COLOUR-ONLY restyle of <c>liberty.json</c> — same
    /// sources, same layer id sequence, same per-layer type/source/source-layer/filter/minzoom/maxzoom/
    /// layout, only <c>*-color</c> paint values differ. Without this, an edit to the night style can
    /// silently drift into a structural restyle, and any conclusion drawn from clicking between the two
    /// (e.g. "geometry stayed, so the tile cache was reused") stops being true with nothing going red.
    /// </summary>
    [TestFixture]
    public class LibertyNightColorOnlyRestyleTests
    {
        private const int MinDifferingColorValues = 100;

        // Non-paint layer fields that must be byte-identical between the two styles.
        private static readonly string[] StructuralFields =
            { "type", "source", "source-layer", "filter", "minzoom", "maxzoom", "layout" };

        // ---- fixture loader (walk-up; same pattern as TileJsonTests/SymbolTestFixtures) -----------
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
                $"{fileName} not found under Assets/StreamingAssets/Fixtures walking up from " +
                $"{Directory.GetCurrentDirectory()} or {AppContext.BaseDirectory}");
        }

        private static JsonValue Day => JsonParser.Parse(LoadStreamingFixtureText("liberty.json"));
        private static JsonValue Night => JsonParser.Parse(LoadStreamingFixtureText("liberty-night.json"));

        [Test]
        public void Sources_AreIdentical()
        {
            Assert.AreEqual(JsonCanonical.Write(Day.Get("sources")), JsonCanonical.Write(Night.Get("sources")),
                "liberty-night.json's 'sources' must be byte-identical to liberty.json's — a colour-only " +
                "restyle changes no source, so cached tile geometry stays reusable across a switch.");
        }

        [Test]
        public void LayerIdSequence_And_StructuralFields_AreIdentical()
        {
            IReadOnlyList<JsonValue> dayLayers = Day.Get("layers").Items;
            IReadOnlyList<JsonValue> nightLayers = Night.Get("layers").Items;

            Assert.AreEqual(dayLayers.Count, nightLayers.Count,
                "liberty.json and liberty-night.json must declare the same number of layers.");

            for (int i = 0; i < dayLayers.Count; i++)
            {
                string dayId = dayLayers[i].GetString("id");
                string nightId = nightLayers[i].GetString("id");
                Assert.AreEqual(dayId, nightId,
                    $"layer id sequence diverges at index {i}: liberty has '{dayId}', liberty-night has " +
                    $"'{nightId}' — the layer ORDER must match, not just the set of ids.");

                foreach (string field in StructuralFields)
                {
                    string dayField = JsonCanonical.Write(dayLayers[i].Get(field));
                    string nightField = JsonCanonical.Write(nightLayers[i].Get(field));
                    Assert.AreEqual(dayField, nightField,
                        $"layer '{dayId}' diverges in non-colour field '{field}': " +
                        $"liberty has {dayField}, liberty-night has {nightField} — liberty-night.json must " +
                        "be a COLOUR-ONLY restyle (regenerate it with Tools/generate-liberty-night.py).");
                }
            }
        }

        [Test]
        public void AtLeastMostColorValues_Differ()
        {
            IReadOnlyList<JsonValue> dayLayers = Day.Get("layers").Items;
            IReadOnlyList<JsonValue> nightLayers = Night.Get("layers").Items;

            int differing = 0;
            for (int i = 0; i < dayLayers.Count; i++)
            {
                JsonValue dayPaint = dayLayers[i].Get("paint");
                JsonValue nightPaint = nightLayers[i].Get("paint");
                if (dayPaint == null) continue;

                foreach (string key in dayPaint.Members.Keys)
                {
                    if (!key.EndsWith("-color", StringComparison.Ordinal)) continue;
                    string dayValue = JsonCanonical.Write(dayPaint.Get(key));
                    string nightValue = JsonCanonical.Write(nightPaint?.Get(key));
                    if (dayValue != nightValue) differing++;
                }
            }

            Assert.GreaterOrEqual(differing, MinDifferingColorValues,
                $"only {differing} '*-color' paint values differ between liberty.json and liberty-night.json " +
                $"— expected at least {MinDifferingColorValues}. The night style should be a genuine, " +
                "visually-distinct recolour, not a token edit.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // StyleParserTests — a MapLibre Style JSON parses into the typed model; all layer types dispatched
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S08 acceptance: a MapLibre Style JSON parses into the typed model; spec defaults applied;
    /// all 10 layer types recognized + dispatched; unknown keys tolerated; raw paint/layout/filter
    /// retained; the source-layer string drives feature selection from the decoded MVT fixture.
    /// </summary>
    [TestFixture]
    public class StyleParserTests
    {
        // ---- fixture loader (walk-up; works under Unity batch mode AND dotnet test) ---------------
        private static byte[] LoadFixtureBytes()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(candidate))
                        return File.ReadAllBytes(candidate);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"sample-tile.bytes not found (cwd={Directory.GetCurrentDirectory()}," +
                $" base={AppContext.BaseDirectory})");
        }

        private static string LoadFixtureText(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Assets", "Fixtures", fileName);
                    if (File.Exists(candidate))
                        return File.ReadAllText(candidate);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found (cwd={Directory.GetCurrentDirectory()}," +
                $" base={AppContext.BaseDirectory})");
        }

        // =========================================================================================
        // 0. A REAL published MapLibre Style JSON parses into the typed model (acceptance, sentence 1).
        //    Fixture: Assets/Fixtures/maplibre-demo-style.json — MapLibre's public demo basemap style
        //    (https://demotiles.maplibre.org/style.json), committed as data (clean-room-safe: it is a
        //    style document, not MapLibre's parser source). Exercises real nested expression arrays,
        //    metadata, template URLs, center/zoom — surfaces a synthetic literal omits.
        // =========================================================================================
        [Test]
        public void RealMapLibreStyle_ParsesIntoTypedModel()
        {
            string json = LoadFixtureText("maplibre-demo-style.json");

            StyleDocument doc = null;
            Assert.DoesNotThrow(() => doc = StyleParser.Parse(json),
                "a real published style must parse without throwing");

            Assert.AreEqual(8, doc.Version, "real style spec version");
            Assert.AreEqual("MapLibre", doc.Name);
            Assert.IsNotNull(doc.Glyphs, "glyphs URL parsed");

            // Sources: a vector source + a geojson source (discriminator over a real document).
            Assert.IsTrue(doc.Sources.Count >= 2);
            var vec = doc.GetSource("maplibre");
            Assert.IsNotNull(vec, "vector source 'maplibre' present");
            Assert.AreEqual(SourceType.Vector, vec.Type);
            var crimea = doc.GetSource("crimea");
            Assert.IsNotNull(crimea, "geojson source 'crimea' present");
            Assert.AreEqual(SourceType.GeoJson, crimea.Type);

            // Layers: declared order preserved; every layer recognized (none Unknown).
            Assert.IsTrue(doc.Layers.Count >= 8, "real style has its layers");
            Assert.AreEqual(StyleLayerType.Background, doc.Layers[0].LayerType, "first declared layer");
            Assert.IsInstanceOf<Background.StyleLayer>(doc.Layers[0],
                "background dispatches to its typed subclass (E3), like fill/line/symbol.");
            foreach (var l in doc.Layers)
                Assert.AreNotEqual(StyleLayerType.Unknown, l.LayerType,
                    $"every layer type in the real style is recognized (offender id={l.Id})");

            // The real style's source-layers ("countries"/"geolines"/"centroids") match our MVT fixture:
            // a real style layer resolves features from the real decoded tile.
            using var tile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, LoadFixtureBytes());
            StyleLayer coastline = null;
            foreach (var l in doc.Layers)
                if (l.Id == "coastline") { coastline = l; break; }
            Assert.IsNotNull(coastline, "expected 'coastline' layer in the real style");
            Assert.AreEqual("countries", coastline.SourceLayer);
            var mvt = SourceLayerResolver.ResolveTileLayer(coastline, tile);
            Assert.IsNotNull(mvt, "real style layer resolves to the MVT source-layer");
            Assert.AreEqual(239, mvt.Features.Count,
                "real style's source-layer string selects the real 239 country features");

            // Raw retains the whole layer object, paint/layout sub-trees included, for later stages.
            StyleLayer fill = null;
            foreach (var l in doc.Layers)
                if (l.LayerType == StyleLayerType.Fill && l.Raw.Get("paint") != null) { fill = l; break; }
            Assert.IsNotNull(fill, "a fill layer with a paint block exists in the real style");
            Assert.IsTrue(fill.Raw.Get("paint").IsObject, "real paint sub-tree retained as queryable JSON");
        }

        // =========================================================================================
        // 1. Minimal valid style: version, one vector source, one fill layer.
        // =========================================================================================
        [Test]
        public void MinimalStyle_ParsesRootSourceAndLayer()
        {
            const string json = @"{
              ""version"": 8,
              ""name"": ""Minimal"",
              ""sprite"": ""https://example.com/sprite"",
              ""glyphs"": ""https://example.com/{fontstack}/{range}.pbf"",
              ""sources"": {
                ""basemap"": { ""type"": ""vector"", ""url"": ""https://example.com/tiles.json"" }
              },
              ""layers"": [
                { ""id"": ""land"", ""type"": ""fill"", ""source"": ""basemap"", ""source-layer"": ""countries"" }
              ]
            }";

            var doc = StyleParser.Parse(json);

            Assert.AreEqual(8, doc.Version);
            Assert.AreEqual("Minimal", doc.Name);
            Assert.AreEqual("https://example.com/sprite", doc.Sprite);
            Assert.AreEqual("https://example.com/{fontstack}/{range}.pbf", doc.Glyphs);

            Assert.AreEqual(1, doc.Sources.Count);
            var src = doc.GetSource("basemap");
            Assert.IsNotNull(src);
            Assert.AreEqual(SourceType.Vector, src.Type);
            Assert.AreEqual("https://example.com/tiles.json", src.Url);

            Assert.AreEqual(1, doc.Layers.Count);
            var layer = doc.Layers[0];
            Assert.AreEqual("land", layer.Id);
            Assert.AreEqual(StyleLayerType.Fill, layer.LayerType);
            Assert.AreEqual("basemap", layer.Source);
            Assert.AreEqual("countries", layer.SourceLayer);
        }

        // =========================================================================================
        // 2. Missing-optional-field defaults — EXACT spec values.
        //    Source: scheme="xyz", minzoom=0, maxzoom=22, bounds=[-180,-85.051129,180,85.051129].
        //    Layer:  minzoom/maxzoom have NO default → stay null (absent = unbounded).
        // =========================================================================================
        [Test]
        public void MissingOptionalFields_UseSpecDefaults()
        {
            const string json = @"{
              ""version"": 8,
              ""sources"": { ""v"": { ""type"": ""vector"" } },
              ""layers"": [ { ""id"": ""l"", ""type"": ""line"", ""source"": ""v"", ""source-layer"": ""roads"" } ]
            }";

            var doc = StyleParser.Parse(json);

            var src = doc.GetSource("v");
            Assert.AreEqual("xyz", src.Scheme, "source scheme default");
            Assert.AreEqual(0, src.MinZoom, "source minzoom default");
            Assert.AreEqual(22, src.MaxZoom, "source maxzoom default");
            Assert.IsNotNull(src.Bounds);
            Assert.AreEqual(4, src.Bounds.Length);
            Assert.AreEqual(-180.0, src.Bounds[0], 1e-9);
            Assert.AreEqual(-85.051129, src.Bounds[1], 1e-9);
            Assert.AreEqual(180.0, src.Bounds[2], 1e-9);
            Assert.AreEqual(85.051129, src.Bounds[3], 1e-9);

            var layer = doc.Layers[0];
            Assert.IsNull(layer.MinZoom, "layer minzoom has no spec default → null");
            Assert.IsNull(layer.MaxZoom, "layer maxzoom has no spec default → null");
            // And present values are read through:
            Assert.IsNull(layer.Filter);
            Assert.IsNull(layer.Raw.Get("paint"));
        }

        [Test]
        public void PresentZoomFields_AreReadThrough()
        {
            const string json = @"{
              ""version"": 8,
              ""sources"": { ""v"": { ""type"": ""vector"", ""scheme"": ""tms"", ""minzoom"": 4, ""maxzoom"": 14 } },
              ""layers"": [ { ""id"": ""l"", ""type"": ""fill"", ""source"": ""v"", ""source-layer"": ""x"", ""minzoom"": 5, ""maxzoom"": 12 } ]
            }";

            var doc = StyleParser.Parse(json);
            var src = doc.GetSource("v");
            Assert.AreEqual("tms", src.Scheme);
            Assert.AreEqual(4, src.MinZoom);
            Assert.AreEqual(14, src.MaxZoom);

            var layer = doc.Layers[0];
            Assert.AreEqual(5.0, layer.MinZoom);
            Assert.AreEqual(12.0, layer.MaxZoom);
        }

        // =========================================================================================
        // 3. Multi-layer multi-source: all 10 layer types present → each dispatches to its enum
        //    (no parse failure on any spec type); declared order preserved.
        // =========================================================================================
        [Test]
        public void MultiLayerMultiSource_AllTenTypesRecognized_OrderPreserved()
        {
            const string json = @"{
              ""version"": 8,
              ""sources"": {
                ""vec"": { ""type"": ""vector"", ""url"": ""u"" },
                ""ras"": { ""type"": ""raster"", ""tiles"": [""https://a/{z}/{x}/{y}.png""] }
              },
              ""layers"": [
                { ""id"": ""bg"",   ""type"": ""background"" },
                { ""id"": ""f"",    ""type"": ""fill"",           ""source"": ""vec"", ""source-layer"": ""a"" },
                { ""id"": ""ln"",   ""type"": ""line"",           ""source"": ""vec"", ""source-layer"": ""b"" },
                { ""id"": ""sym"",  ""type"": ""symbol"",         ""source"": ""vec"", ""source-layer"": ""c"" },
                { ""id"": ""cir"",  ""type"": ""circle"",         ""source"": ""vec"", ""source-layer"": ""d"" },
                { ""id"": ""heat"", ""type"": ""heatmap"",        ""source"": ""vec"", ""source-layer"": ""e"" },
                { ""id"": ""fe"",   ""type"": ""fill-extrusion"", ""source"": ""vec"", ""source-layer"": ""f"" },
                { ""id"": ""r"",    ""type"": ""raster"",         ""source"": ""ras"" },
                { ""id"": ""hs"",   ""type"": ""hillshade"",      ""source"": ""ras"" },
                { ""id"": ""cr"",   ""type"": ""color-relief"",   ""source"": ""ras"" }
              ]
            }";

            var doc = StyleParser.Parse(json);

            Assert.AreEqual(2, doc.Sources.Count);
            Assert.AreEqual(SourceType.Vector, doc.GetSource("vec").Type);
            Assert.AreEqual(SourceType.Raster, doc.GetSource("ras").Type);
            CollectionAssert.AreEqual(new[] { "https://a/{z}/{x}/{y}.png" }, doc.GetSource("ras").Tiles);

            var expectedTypes = new[]
            {
                StyleLayerType.Background, StyleLayerType.Fill, StyleLayerType.Line,
                StyleLayerType.Symbol, StyleLayerType.Circle, StyleLayerType.Heatmap,
                StyleLayerType.FillExtrusion, StyleLayerType.Raster, StyleLayerType.Hillshade,
                StyleLayerType.ColorRelief
            };
            Assert.AreEqual(expectedTypes.Length, doc.Layers.Count, "all 10 layers parsed");

            // No layer is Unknown → every spec type was recognized & dispatched to its enum.
            for (int i = 0; i < expectedTypes.Length; i++)
            {
                Assert.AreNotEqual(StyleLayerType.Unknown, doc.Layers[i].LayerType,
                    $"layer {doc.Layers[i].Id} should be a recognized type");
                Assert.AreEqual(expectedTypes[i], doc.Layers[i].LayerType,
                    $"declared order preserved at index {i}");
            }

            // Background has no source / source-layer; resolver tolerates that.
            Assert.IsNull(doc.Layers[0].Source);
            Assert.IsNull(doc.Layers[0].SourceLayer);
        }

        // =========================================================================================
        // 4. Forward-compat: unknown root key, unknown layer key, unknown paint/layout key, unknown
        //    layer type → parse succeeds; bad type → Unknown enum; raw subtrees retained.
        // =========================================================================================
        [Test]
        public void ForwardCompat_UnknownKeysAndTypesTolerated()
        {
            const string json = @"{
              ""version"": 8,
              ""future-root-key"": { ""anything"": [1, 2, 3] },
              ""center"": [10.0, 20.0],
              ""sources"": { ""v"": { ""type"": ""vector"", ""url"": ""u"", ""future-source-key"": true } },
              ""layers"": [
                {
                  ""id"": ""x"", ""type"": ""totally-new-layer-type"", ""source"": ""v"",
                  ""future-layer-key"": 42,
                  ""paint"": { ""future-paint-prop"": ""#fff"" },
                  ""layout"": { ""future-layout-prop"": ""visible"" }
                }
              ]
            }";

            StyleDocument doc = null;
            Assert.DoesNotThrow(() => doc = StyleParser.Parse(json), "unknown keys must not throw");

            Assert.AreEqual(StyleLayerType.Unknown, doc.Layers[0].LayerType, "unknown type → Unknown");
            Assert.AreEqual("totally-new-layer-type", doc.Layers[0].RawType, "raw type preserved");

            // Unknown root key preserved on Root.
            Assert.IsTrue(doc.Root.TryGet("future-root-key", out _), "unknown root key preserved");
            Assert.IsTrue(doc.Root.TryGet("center", out _), "tolerated root key preserved");

            // Unknown layer key preserved on the layer's Raw.
            Assert.IsTrue(doc.Layers[0].Raw.TryGet("future-layer-key", out var flk));
            Assert.AreEqual(42, flk.AsInt());

            // Unknown paint/layout props retained inside Raw's paint/layout subtrees.
            Assert.IsNotNull(doc.Layers[0].Raw.Get("paint"));
            Assert.AreEqual("#fff", doc.Layers[0].Raw.Get("paint").GetString("future-paint-prop"));
            Assert.IsNotNull(doc.Layers[0].Raw.Get("layout"));
            Assert.AreEqual("visible", doc.Layers[0].Raw.Get("layout").GetString("future-layout-prop"));
        }

        [Test]
        public void MissingVersion_DoesNotThrow()
        {
            // version is spec-required, but forward-compat tolerance means absence must not throw.
            const string json = @"{ ""sources"": {}, ""layers"": [] }";
            StyleDocument doc = null;
            Assert.DoesNotThrow(() => doc = StyleParser.Parse(json));
            Assert.AreEqual(0, doc.Version, "absent version → 0 (no throw)");
        }

        // =========================================================================================
        // 5. Source-layer resolution (sharpest tooth) — the source-layer string drives feature
        //    selection from the decoded MVT fixture (countries=239, geolines=6, background=null).
        // =========================================================================================
        [Test]
        public void SourceLayerResolution_DrivesFeatureSelectionFromMvt()
        {
            using var tile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, LoadFixtureBytes());

            const string json = @"{
              ""version"": 8,
              ""sources"": { ""basemap"": { ""type"": ""vector"", ""url"": ""u"" } },
              ""layers"": [
                { ""id"": ""bg"",       ""type"": ""background"" },
                { ""id"": ""land"",     ""type"": ""fill"", ""source"": ""basemap"", ""source-layer"": ""countries"" },
                { ""id"": ""graticule"",""type"": ""line"", ""source"": ""basemap"", ""source-layer"": ""geolines"" },
                { ""id"": ""missing"",  ""type"": ""fill"", ""source"": ""basemap"", ""source-layer"": ""does-not-exist"" }
              ]
            }";

            var doc = StyleParser.Parse(json);
            var bg = doc.Layers[0];
            var land = doc.Layers[1];
            var graticule = doc.Layers[2];
            var missing = doc.Layers[3];

            // fill layer with source-layer "countries" → the 239-feature MVT layer.
            var landMvt = SourceLayerResolver.ResolveTileLayer(land, tile);
            Assert.IsNotNull(landMvt, "countries source-layer resolves");
            Assert.AreEqual("countries", landMvt.Name);
            Assert.AreEqual(239, landMvt.Features.Count, "fill layer selects the real 239 country features");

            // line layer with source-layer "geolines" → the 6-feature MVT layer.
            var graticuleMvt = SourceLayerResolver.ResolveTileLayer(graticule, tile);
            Assert.IsNotNull(graticuleMvt, "geolines source-layer resolves");
            Assert.AreEqual(6, graticuleMvt.Features.Count, "line layer selects the real 6 geoline features");

            // background layer with no source-layer → null, no throw.
            Assert.IsNull(SourceLayerResolver.ResolveTileLayer(bg, tile),
                "background (no source-layer) resolves to null without throwing");

            // a source-layer that is absent from the tile → null, no throw.
            Assert.IsNull(SourceLayerResolver.ResolveTileLayer(missing, tile),
                "unknown source-layer resolves to null without throwing");

            // The layer's source id resolves to a Vector source (discriminator exercised).
            var landSource = SourceLayerResolver.ResolveSource(land, doc);
            Assert.IsNotNull(landSource);
            Assert.AreEqual(SourceType.Vector, landSource.Type);

            // Full resolvability gate: vector source + matching MVT layer.
            Assert.IsTrue(SourceLayerResolver.HasResolvableFeatures(land, doc, tile));
            Assert.IsFalse(SourceLayerResolver.HasResolvableFeatures(bg, doc, tile),
                "background has no resolvable features");
            Assert.IsFalse(SourceLayerResolver.HasResolvableFeatures(missing, doc, tile),
                "missing source-layer has no resolvable features");
        }

        // =========================================================================================
        // 6. Raw subtree retention: paint/layout/filter are queryable JsonValue when declared.
        // =========================================================================================
        [Test]
        public void RawSubtrees_RetainedAndQueryable()
        {
            const string json = @"{
              ""version"": 8,
              ""sources"": { ""v"": { ""type"": ""vector"", ""url"": ""u"" } },
              ""layers"": [
                {
                  ""id"": ""roads"", ""type"": ""line"", ""source"": ""v"", ""source-layer"": ""road"",
                  ""filter"": [""=="", [""get"", ""class""], ""motorway""],
                  ""layout"": { ""line-cap"": ""round"", ""line-join"": ""round"" },
                  ""paint"": { ""line-color"": ""#ff0000"", ""line-width"": 2 }
                }
              ]
            }";

            var layer = StyleParser.Parse(json).Layers[0];

            Assert.IsNotNull(layer.Filter);
            Assert.IsTrue(layer.Filter.IsArray, "legacy filter retained as raw array");
            Assert.AreEqual("==", layer.Filter.Items[0].AsString());

            Assert.IsNotNull(layer.Raw.Get("layout"));
            Assert.AreEqual("round", layer.Raw.Get("layout").GetString("line-cap"));
            Assert.AreEqual("round", layer.Raw.Get("layout").GetString("line-join"));

            Assert.IsNotNull(layer.Raw.Get("paint"));
            Assert.AreEqual("#ff0000", layer.Raw.Get("paint").GetString("line-color"));
            Assert.AreEqual(2.0, layer.Raw.Get("paint").GetDouble("line-width"), 1e-9);
        }

        // =========================================================================================
        // JSON parser robustness: escapes round-trip, invariant-culture numbers, malformed throws,
        // trailing junk rejected, unknown keys never throw.
        // =========================================================================================
        [Test]
        public void Json_StringEscapes_RoundTrip()
        {
            const string json = "{ \"s\": \"a\\\"b\\\\c\\/d\\ne\\tf\\u00e9g\" }";
            var v = JsonParser.Parse(json);
            Assert.AreEqual("a\"b\\c/d\ne\tfég", v.GetString("s"));
        }

        [Test]
        public void Json_Numbers_InvariantCulture()
        {
            var v = JsonParser.Parse(@"{ ""a"": 85.051129, ""b"": -1.5e2, ""c"": 42 }");
            Assert.AreEqual(85.051129, v.GetDouble("a"), 1e-12);
            Assert.AreEqual(-150.0, v.GetDouble("b"), 1e-12);
            Assert.AreEqual(42, v.GetInt("c"));
        }

        [Test]
        public void Json_Malformed_Throws()
        {
            Assert.Throws<JsonParseException>(() => JsonParser.Parse("{ \"a\": }"));
            Assert.Throws<JsonParseException>(() => JsonParser.Parse("[1, 2"));
            Assert.Throws<JsonParseException>(() => JsonParser.Parse("{ \"a\" 1 }"));
            Assert.Throws<JsonParseException>(() => JsonParser.Parse("nul"));
        }

        [Test]
        public void Json_TrailingJunk_Rejected()
        {
            Assert.Throws<JsonParseException>(() => JsonParser.Parse("{} garbage"));
            Assert.Throws<JsonParseException>(() => JsonParser.Parse("123 456"));
            // Trailing whitespace is fine.
            Assert.DoesNotThrow(() => JsonParser.Parse("{}   \n  "));
        }

        [Test]
        public void Json_EmptyContainers_Parse()
        {
            Assert.IsTrue(JsonParser.Parse("{}").IsObject);
            Assert.IsTrue(JsonParser.Parse("[]").IsArray);
            Assert.AreEqual(0, JsonParser.Parse("{}").Members.Count);
            Assert.AreEqual(0, JsonParser.Parse("[]").Items.Count);
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolFeatureExtractorIconTests — SymbolFeatureExtractor.Extract's icon path (icon-image -> SymbolQuad)
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// I3: <see cref="SymbolFeatureExtractor.Extract"/>'s icon path — a supplied
    /// <see cref="SpriteAtlasView"/> resolves <c>icon-image</c> per feature and lays out an
    /// <see cref="SymbolQuad"/>-carrying <see cref="SymbolStyle.SymbolFeature"/> (<c>Kind == Icon</c>)
    /// independently of the existing text path (<c>Kind == Text</c>, unchanged). Mirrors
    /// <c>SymbolFeatureExtractorTests</c>'s hand-encoded MultiPoint pattern. Unity EditMode only (see
    /// file header) — it drives a Waist-1 <c>TileGeometryBuffers</c> extraction.
    /// </summary>
    [TestFixture]
    public class SymbolFeatureExtractorIconTests
    {

        /// <summary>IR C1 P3: a synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this epic exists to remove.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        // Walk-up fixture loader — mirrors SymbolFeatureExtractorTests.LoadFixture.
        private static string LoadSpriteJson()
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, "Assets", "Fixtures", "sprites", "sample-sprite.json");
                    if (File.Exists(p)) return File.ReadAllText(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("sample-sprite.json not found walking up from cwd/AppContext.");
        }

        private static readonly int2 SheetSize = new int2(64, 64);

        private static SpriteAtlasView LoadAtlas()
            => new SpriteAtlasView { Index = SpriteIndex.Parse(LoadSpriteJson()), Size = SheetSize };

        // Fixture sprites (mirrors Assets/Fixtures/sprites/sample-sprite.json — pinned independently in
        // IconQuadLayoutTests too, so a fixture-file edit breaks both, loudly).
        private static readonly SpriteEntry MarkerEntry = new SpriteEntry { X = 0, Y = 0, Width = 16, Height = 16, PixelRatio = 1f, Sdf = false };
        private static readonly SpriteEntry StarEntry = new SpriteEntry { X = 16, Y = 0, Width = 24, Height = 24, PixelRatio = 2f, Sdf = false };

        private const uint Extent = 4096;
        private static readonly TileId TileId0 = new TileId { Z = 1, X = 0, Y = 0 };

        /// <summary>Protobuf zigzag ENcode — mirrors SymbolFeatureExtractorTests.ZigZagEncode.</summary>
        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        /// <summary>Hand-encodes a single-point MVT geometry — mirrors SymbolFeatureExtractorTests.MultiPointGeometry
        /// specialized to one point.</summary>
        private static uint[] SinglePointGeometry(double2 p)
            => new uint[]
            {
                1u | (1u << 3),                 // MoveTo, count=1
                ZigZagEncode((long)p.x),
                ZigZagEncode((long)p.y),
            };

        private static SymbolStyle.StyleLayer PointLayer(string layoutJson)
            => new SymbolStyle.StyleLayer
            {
                Id = "points",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "points",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout(layoutJson),
            };

        private static IDecodedTile OnePointTile(double2 point)
        {
            var feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.Point, hasId: false, geometry: SinglePointGeometry(point));
            return TestDecodedTiles.Of("points", TileId0, new List<IFeature> { feature }, Extent);
        }

        [Test]
        public void Extract_IconOnlyFeature_YieldsOneIconWithExactQuad()
        {
            // PRIMARY tooth (RED-verified): a feature with NO text-field but a resolvable icon-image must
            // still emit a symbol — the pre-I3 extractor would have skipped it entirely on "text==null".
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"icon-image\":\"star\"}");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), symbols, atlas);

            Assert.AreEqual(1, symbols.Count, "an icon-only feature must yield exactly one (icon) symbol");
            SymbolStyle.SymbolFeature symbol = symbols[0];
            Assert.AreEqual(SymbolKind.Icon, symbol.Kind);
            Assert.IsNull(symbol.Text, "an icon symbol carries no text");

            SymbolQuad expected = IconQuadLayout.Layout(StarEntry, SheetSize, 1f, TextAnchor.Center, float2.zero);
            AssertQuadEqual(expected, symbol.IconQuad);
        }

        [Test]
        public void Extract_UnknownSpriteName_YieldsZeroIcons_NoThrow()
        {
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"text-field\":\"L\",\"icon-image\":\"does-not-exist\"}");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            Assert.DoesNotThrow(() =>
                SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), symbols, atlas));

            Assert.AreEqual(1, symbols.Count, "the text symbol still resolves");
            Assert.AreEqual(SymbolKind.Text, symbols[0].Kind, "an unresolvable sprite name must not emit an icon symbol");
        }

        [Test]
        public void Extract_TextAndIcon_YieldsTwoSymbols_IconFirstThenText()
        {
            // §10 D8/D10 (road-shields, road-shields-design.md — supersedes D5): a feature resolving BOTH a
            // text and an icon is ONE placement instance. The icon (collision owner) is emitted first, the
            // text rides as its Rider — ONE placement instance downstream (SymbolPairing / StagePointPair), so
            // neither half's overlap flags are forced anymore; both carry their AUTHORED
            // text-allow-overlap/text-ignore-placement (default false, unset here).
            // NOTE (P-A): this layer leaves every anchor/offset at its default, i.e. the halves are CENTRED —
            // the retired conjuncts were all inert at that value, so this test does NOT discriminate the P-A
            // predicate. SymbolPairPredicateTests carries the teeth that do.
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"text-field\":\"L\",\"icon-image\":\"marker\"}");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), symbols, atlas);

            Assert.AreEqual(2, symbols.Count, "a feature with both text-field and icon-image yields two symbols");
            Assert.AreEqual(SymbolKind.Icon, symbols[0].Kind, "the icon (pair owner) is emitted first");
            Assert.AreEqual(0, symbols[0].FeatureIndex);
            Assert.IsFalse(symbols[0].AllowOverlap); Assert.IsFalse(symbols[0].IgnorePlacement);
            Assert.AreEqual(SymbolKind.Text, symbols[1].Kind, "the rider text is emitted second");
            Assert.AreEqual("L", symbols[1].Text);
            Assert.AreEqual(1, symbols[1].FeatureIndex, "the text symbol continues the SAME ordinal sequence");
            Assert.IsFalse(symbols[1].AllowOverlap, "the D5 forcing is retired — the rider carries its AUTHORED flag (default false)");
            Assert.IsFalse(symbols[1].IgnorePlacement, "the D5 forcing is retired — the rider carries its AUTHORED flag (default false)");
            // §10 D10: the pair is stamped Owner/Rider sharing one PairId (the owner's own FeatureIndex).
            Assert.AreEqual(MapRenderer.Core.Text.SymbolPairRole.Owner, symbols[0].PairRole);
            Assert.AreEqual(MapRenderer.Core.Text.SymbolPairRole.Rider, symbols[1].PairRole);
            Assert.AreEqual(symbols[0].FeatureIndex, symbols[0].PairId);
            Assert.AreEqual(symbols[0].PairId, symbols[1].PairId);
            // Both symbols share the same anchor (same point).
            Assert.AreEqual(symbols[0].AnchorRender.x, symbols[1].AnchorRender.x, 1e-9);
            Assert.AreEqual(symbols[0].AnchorRender.y, symbols[1].AnchorRender.y, 1e-9);
            Assert.AreEqual(symbols[0].AnchorRender.z, symbols[1].AnchorRender.z, 1e-9);
        }

        [Test]
        public void Extract_IconSizeOffsetAnchor_AreForwardedToLayout()
        {
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer(
                "{\"icon-image\":\"marker\",\"icon-size\":2,\"icon-offset\":[2,0],\"icon-anchor\":\"top-left\"}");

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), symbols, atlas);

            Assert.AreEqual(1, symbols.Count);
            SymbolQuad expected = IconQuadLayout.Layout(MarkerEntry, SheetSize, 2f, TextAnchor.TopLeft, new float2(2f, 0f));
            AssertQuadEqual(expected, symbols[0].IconQuad);
        }

        [Test]
        public void Extract_TextOnly_NullAtlas_YieldsZeroIcons_ByteIdenticalToPreI3()
        {
            var tile = OnePointTile(new double2(100, 200));
            var layer = PointLayer("{\"text-field\":\"L\",\"icon-image\":\"marker\"}");

            // 5-arg (pre-I3) call and the explicit 6-arg call with spriteAtlas: null must agree exactly.
            var preI3Style = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), preI3Style);

            var explicitNull = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), explicitNull, null);

            Assert.AreEqual(1, preI3Style.Count, "a null atlas never emits an icon symbol, even with icon-image set");
            Assert.AreEqual(1, explicitNull.Count);
            Assert.AreEqual(SymbolKind.Text, preI3Style[0].Kind);
            Assert.AreEqual(preI3Style[0].Text, explicitNull[0].Text);
            Assert.AreEqual(preI3Style[0].FeatureIndex, explicitNull[0].FeatureIndex);
            Assert.AreEqual(preI3Style[0].PaddingPx, explicitNull[0].PaddingPx, 1e-9);
        }

        [Test]
        public void Extract_AtlasPresent_TextOnlyLayer_NoIconImage_YieldsZeroIcons()
        {
            var tile = OnePointTile(new double2(100, 200));
            var atlas = LoadAtlas();
            var layer = PointLayer("{\"text-field\":\"L\"}"); // no icon-image at all

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, TileId0, 0.0, new WebMercatorProjection(), symbols, atlas);

            Assert.AreEqual(1, symbols.Count, "an atlas being present doesn't manufacture icons the layer never asked for");
            Assert.AreEqual(SymbolKind.Text, symbols[0].Kind);
        }

        private static void AssertQuadEqual(in SymbolQuad expected, in SymbolQuad actual)
        {
            const float eps = 1e-5f;
            Assert.AreEqual(expected.TopLeft.x, actual.TopLeft.x, eps, "TopLeft.x");
            Assert.AreEqual(expected.TopLeft.y, actual.TopLeft.y, eps, "TopLeft.y");
            Assert.AreEqual(expected.BottomRight.x, actual.BottomRight.x, eps, "BottomRight.x");
            Assert.AreEqual(expected.BottomRight.y, actual.BottomRight.y, eps, "BottomRight.y");
            Assert.AreEqual(expected.UvTopLeft.x, actual.UvTopLeft.x, eps, "UvTopLeft.x");
            Assert.AreEqual(expected.UvTopLeft.y, actual.UvTopLeft.y, eps, "UvTopLeft.y");
            Assert.AreEqual(expected.UvBottomRight.x, actual.UvBottomRight.x, eps, "UvBottomRight.x");
            Assert.AreEqual(expected.UvBottomRight.y, actual.UvBottomRight.y, eps, "UvBottomRight.y");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolFeatureExtractorTests — SymbolFeatureExtractor.Extract over the fixture's centroids layer
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S105 Slice 2 (A3): <see cref="SymbolFeatureExtractor.Extract"/> over the committed fixture's
    /// <c>centroids</c> layer (250 Point features with <c>NAME</c>/<c>ABBREV</c>) yields the right count,
    /// the right resolved text for the first feature (<c>"Aruba"</c>), an anchor that is the REAL
    /// tile→geo→project chain (not a stub), and honours the layer filter. Unity EditMode only (see
    /// file header) — it drives a Waist-1 <c>TileGeometryBuffers</c> extraction.
    /// </summary>
    [TestFixture]
    public class SymbolFeatureExtractorTests
    {

        /// <summary>IR C1 P3: a synthetic decoded tile owns <c>Allocator.Persistent</c> buffers now, so the
        /// fixture releases every one it built. Leak detection is off in the batch gate — without this the
        /// leak would be invisible, which is the failure class this epic exists to remove.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
        // Walk-up fixture loader (works in Unity batch mode AND dotnet test) — mirrors MvtPropertyDecodeTests.
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

        private static readonly TileId FixtureTile = new TileId { Z = 0, X = 0, Y = 0 };

        private static SymbolStyle.StyleLayer CentroidsLayer(string textField = "{NAME}", string filterJson = null)
            => new SymbolStyle.StyleLayer
            {
                Id = "labels",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "centroids",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"" + textField + "\"}"),
                Filter = filterJson != null ? JsonParser.Parse(filterJson) : null,
            };

        [Test]
        public void Extract_CentroidsLayer_Yields250SymbolsWithRealAnchors()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection();

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(CentroidsLayer(), tile, FixtureTile, 0.0, projection, symbols);

            // The fixture has 250 centroids features; one symbol per point feature with a NON-EMPTY NAME.
            // Two features have an absent/empty NAME → their "{NAME}" resolves empty → skipped (the A2 skip
            // rule proven on real data), so 248 symbols. Compute the expectation independently, then pin it.
            MvtLayer centroids = tile.GetLayer("centroids");
            Assert.AreEqual(250, centroids.Features.Count, "fixture pin: 250 centroids features");
            int pointFeaturesWithName = 0;
            foreach (MvtFeature feat in centroids.Features)
            {
                if (feat.GeometryType != TileGeometryType.Point) continue;
                if (feat.Properties.TryGetValue("NAME", out MapRenderer.Core.Expressions.Value name)
                    && !string.IsNullOrWhiteSpace(name.ToDisplayString()))
                    pointFeaturesWithName++;
            }
            Assert.AreEqual(pointFeaturesWithName, symbols.Count,
                "one symbol per point feature with a resolvable NAME (empty/absent NAME is skipped, not blank)");
            Assert.AreEqual(248, symbols.Count, "fixture pin: 248 of 250 centroids resolve a non-empty NAME");

            // First feature resolves to "Aruba".
            Assert.AreEqual("Aruba", symbols[0].Text, "feature[0]'s NAME is Aruba");

            // Its anchor is the REAL tile→geo→project chain, not a stubbed origin. Recompute independently
            // from the decoded first point and assert equality; also pin the fixture point (1252,1904).
            // IR C1 P3: arm A reads the command stream from the BYTES (a decoded feature carries none).
            List<List<double2>> paths = MvtGeometry.Decode(
                MvtFixtureStreams.ReadLayer(LoadFixture(), "centroids").Commands[0]);
            double2 firstPoint = paths[0][0];
            Assert.AreEqual(1252.0, firstPoint.x, 1e-6, "fixture pin: feature[0].point.x");
            Assert.AreEqual(1904.0, firstPoint.y, 1e-6, "fixture pin: feature[0].point.y");

            double2 lonLat = FixtureTile.ToLonLat(firstPoint.x, firstPoint.y, centroids.Extent);
            double3 expected = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
            Assert.AreEqual(expected.x, symbols[0].AnchorRender.x, 1e-6, "anchor.x must be the real projection (not a stub)");
            Assert.AreEqual(expected.y, symbols[0].AnchorRender.y, 1e-6);
            Assert.AreEqual(expected.z, symbols[0].AnchorRender.z, 1e-6);

            // Stable per-tile ordinal + spec padding default carried through.
            Assert.AreEqual(0, symbols[0].FeatureIndex);
            Assert.AreEqual(2f, symbols[0].PaddingPx, 1e-6, "text-padding spec default is 2");
        }

        // ── Layer visibility predicate (MapLibre minzoom<=zoom<maxzoom, min inclusive / max EXCLUSIVE, null =
        //    unbounded). This is evaluated at DISPLAY time against the live camera zoom (see the extraction test
        //    below for WHY it is not gated at build time). ──
        [Test]
        public void StyleLayer_IsVisibleAtZoom_MinInclusive_MaxExclusive_NullUnbounded()
        {
            MapRenderer.Core.Style.StyleLayer L(double? min, double? max) =>
                new SymbolStyle.StyleLayer { Id = "x", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, MinZoom = min, MaxZoom = max };

            Assert.IsFalse(L(15.0, null).IsVisibleAtZoom(14.0), "below minzoom → hidden");
            Assert.IsTrue (L(15.0, null).IsVisibleAtZoom(15.0), "at minzoom → visible (inclusive)");
            Assert.IsTrue (L(15.0, null).IsVisibleAtZoom(16.0), "above minzoom → visible");
            Assert.IsFalse(L(null, 8.0).IsVisibleAtZoom(8.0),  "at maxzoom → hidden (exclusive)");
            Assert.IsTrue (L(null, 8.0).IsVisibleAtZoom(7.99), "just below maxzoom → visible");
            Assert.IsTrue (L(null, null).IsVisibleAtZoom(14.0), "unbounded → visible at any zoom");
            Assert.IsTrue (L(15.0, 17.0).IsVisibleAtZoom(16.0), "within [min,max) → visible");
            Assert.IsFalse(L(15.0, 17.0).IsVisibleAtZoom(17.0), "at max of a bounded range → hidden");
        }

        // ── Extraction is deliberately zoom-VISIBILITY-agnostic: it emits a layer's symbols regardless of the
        //    layer's minzoom/maxzoom, because tile DATA tops out at a max source zoom (z14 for OpenFreeMap) and is
        //    OVERZOOMED at higher camera zooms without rebuilding. Gating at build time would freeze visibility and
        //    hide layers MapLibre reveals as you zoom past the data level; the gate lives at display time instead
        //    (StyleLayer.IsVisibleAtZoom). This pins that a minzoom-15 layer STILL extracts at z14 (a build-time
        //    gate here would be the regression). ──
        [Test]
        public void Extract_DoesNotGateByLayerZoom_SoOverzoomWorks()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection();
            var layer = new SymbolStyle.StyleLayer
            {
                Id = "labels", LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol, SourceLayer = "centroids",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"{NAME}\"}"), MinZoom = 15.0, // MapLibre-hidden at z14
            };
            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(layer, tile, FixtureTile, 14.0, projection, symbols);
            Assert.AreEqual(248, symbols.Count,
                "a minzoom-15 layer must still EXTRACT at z14 (its symbols live in the store); display-time " +
                "IsVisibleAtZoom hides them until the camera reaches z15 — so overzoomed data reveals them correctly");
        }

        [Test]
        public void Extract_TextTransform_CaseFoldsResolvedText()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection();

            SymbolStyle.StyleLayer Layer(string transform) => new SymbolStyle.StyleLayer
            {
                Id = "labels",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "centroids",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"{NAME}\",\"text-transform\":\"" + transform + "\"}"),
            };

            var upper = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(Layer("uppercase"), tile, FixtureTile, 0.0, projection, upper);
            Assert.AreEqual("ARUBA", upper[0].Text, "text-transform:uppercase must uppercase the resolved symbol");

            var lower = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(Layer("lowercase"), tile, FixtureTile, 0.0, projection, lower);
            Assert.AreEqual("aruba", lower[0].Text, "text-transform:lowercase must lowercase the resolved symbol");

            // Teeth: default (no transform) leaves the mixed-case source untouched — so the two above are
            // genuine transforms, not a fixture that happens to be already-cased.
            var none = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(CentroidsLayer(), tile, FixtureTile, 0.0, projection, none);
            Assert.AreEqual("Aruba", none[0].Text, "no text-transform leaves the source casing as-is");
        }

        [Test]
        public void Extract_LinePlacement_ProducesLineSymbolsWithProjectedPath()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection();

            SymbolStyle.StyleLayer LineLayer(string placement) => new SymbolStyle.StyleLayer
            {
                Id = "lines",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "geolines",
                Paint = TestStyle.SymbolPaint(),
                // Literal text-field so every line feature resolves a symbol regardless of its properties.
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\",\"symbol-placement\":\"" + placement + "\"}"),
            };

            var lineSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(LineLayer("line-center"), tile, FixtureTile, 0.0, projection, lineSymbols);

            Assert.Greater(lineSymbols.Count, 0, "the geolines LineString layer yields line symbols");
            SymbolStyle.SymbolFeature first = lineSymbols[0];
            Assert.AreEqual(MapRenderer.Core.Text.SymbolPlacement.LineCenter, first.Placement);
            Assert.AreEqual(250f, first.SpacingPx, 1e-6, "symbol-spacing default (250) carried onto the line symbol");
            Assert.IsNotNull(first.PathRender, "a line symbol carries the projected path");
            Assert.GreaterOrEqual(first.PathRender.Length, 2, "a placeable line has >= 2 vertices");
            // A-2: the extractor computes the zoom-invariant along-line anchors (line-center → exactly one).
            Assert.IsNotNull(first.LineAnchors, "a line symbol carries its build-time anchors");
            Assert.AreEqual(1, first.LineAnchors.Length, "line-center places a single centred anchor");
            Assert.AreEqual("L", first.Text);

            // The path IS the real tile->geo->project chain (recompute the first line's first vertex).
            MvtLayer geolines = tile.GetLayer("geolines");
            List<List<double2>> paths = MvtGeometry.Decode(
                MvtFixtureStreams.ReadLayer(LoadFixture(), "geolines").Commands[0]);
            double2 lonLat = FixtureTile.ToLonLat(paths[0][0].x, paths[0][0].y, geolines.Extent);
            double3 expected = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
            Assert.AreEqual(expected.x, first.PathRender[0].x, 1e-6, "path vertex is the real projection, not a stub");

            // D2 (road-shields): a POINT-placement layer over the SAME line layer now anchors each path at its
            // mid arc-length (one symbol per path) instead of skipping it outright — the G2 fix. (Superseded the
            // pre-shields "point placement skips LineString features" assertion, which was the very bug D2 fixes.)
            var pointOverLines = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(LineLayer("point"), tile, FixtureTile, 0.0, projection, pointOverLines);
            Assert.Greater(pointOverLines.Count, 0, "D2: point placement now anchors a LineString path at its mid arc-length");
            foreach (SymbolStyle.SymbolFeature l in pointOverLines)
            {
                Assert.AreEqual(MapRenderer.Core.Text.SymbolPlacement.Point, l.Placement, "a mid-arc anchor is Point-placed");
                Assert.IsNull(l.PathRender, "a point-placed (mid-arc) symbol carries no curved path");
            }
        }

        // ── S4-T5: Mercator extractor length-identity — the engine-free half of the byte-identity invariant ──

        [Test]
        public void Extract_LinePlacement_Mercator_PathRenderLengthMatchesOriginalVertexCount()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection(); // MaxRefineAngleRad == +infinity

            var lineLayer = new SymbolStyle.StyleLayer
            {
                Id = "lines",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "geolines",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\",\"symbol-placement\":\"line-center\"}"),
            };

            var lineSymbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(lineLayer, tile, FixtureTile, 0.0, projection, lineSymbols);
            Assert.Greater(lineSymbols.Count, 0, "the geolines LineString layer yields line symbols");

            List<List<double2>> paths = MvtGeometry.Decode(
                MvtFixtureStreams.ReadLayer(LoadFixture(), "geolines").Commands[0]);
            int originalVertexCount = paths[0].Count;

            Assert.AreEqual(originalVertexCount, lineSymbols[0].PathRender.Length,
                "S4: on a flat projection (MaxRefineAngleRad == +infinity) LineCurvatureSubdivision.Subdivide "
                + "must never fire — PathRender length stays the original decoded vertex count");
        }

        // ── S4-T6: globe subdivision + anchor alignment (the crux) ─────────────────────────────────────

        /// <summary>Minimal engine-free <see cref="IFeature"/>/<see cref="ITileLayer"/>/<see cref="IDecodedTile"/>
        /// test doubles for a synthetic tile — mirrors <c>A7TileFeatureSourceTests.FixtureDecodedTile</c>/
        /// <c>FixtureTileLayer</c> (test-only, duplicated locally per convention rather than shared, since both
        /// are private test fixtures, not a production type).</summary>
        /// <summary>Protobuf zigzag ENcode — the inverse of <see cref="MvtGeometry.ZigZag"/> — for hand-building
        /// a synthetic MVT command stream.</summary>
        private static uint ZigZagEncode(long n) => (uint)((n << 1) ^ (n >> 63));

        /// <summary>Hand-encodes a single 2-point LineString <c>(0,0) → (extent,extent)</c> as an MVT geometry
        /// command stream: MoveTo(1 point) + LineTo(1 point), zigzag-encoded deltas (mirrors <see cref="MvtGeometry.Decode"/>).</summary>
        private static uint[] DiagonalLineGeometry(uint extent)
            => new uint[]
            {
                (1u) | (1u << 3), 0, 0,                                       // MoveTo (0,0)
                (2u) | (1u << 3), ZigZagEncode(extent), ZigZagEncode(extent), // LineTo (extent,extent)
            };

        [Test]
        public void Extract_GlobeProjection_SubdividesLongLineSegment_AndAnchorStaysAligned()
        {
            const uint extent = 4096;
            var feature = new DictionaryFeature(properties: null, geometryType: MapRenderer.Core.Tiles.TileGeometryType.LineString, hasId: false, geometry: DiagonalLineGeometry(extent));
            // A low zoom so the two endpoints' surface normals subtend a large arc — many splits.
            var tileId = new TileId { Z = 1, X = 0, Y = 0 };
            var tile = TestDecodedTiles.Of("lines", tileId, new List<IFeature> { feature }, extent);
            var projection = new SphericalProjection(); // MaxRefineAngleRad ~2 degrees (finite)

            var lineLayer = new SymbolStyle.StyleLayer
            {
                Id = "lines",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "lines",
                Paint = TestStyle.SymbolPaint(),
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\",\"symbol-placement\":\"line-center\"}"),
            };

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(lineLayer, tile, tileId, 0.0, projection, symbols);

            Assert.AreEqual(1, symbols.Count, "one line symbol for the single synthetic feature");
            SymbolStyle.SymbolFeature symbol = symbols[0];

            // (a) tooth #1 — subdivision fired, off the chord.
            Assert.Greater(symbol.PathRender.Length, 2, "the globe must subdivide the 2-vertex line");
            double3 chordStart = symbol.PathRender[0];
            double3 chordEnd = symbol.PathRender[symbol.PathRender.Length - 1];
            double3 chordMid = (chordStart + chordEnd) * 0.5;
            double3 midVertex = symbol.PathRender[symbol.PathRender.Length / 2];
            double distFromChord = math.length(midVertex - chordMid);
            Assert.Greater(distFromChord, 1.0,
                "an inserted mid vertex must sit OFF the straight render chord (curvature, not a facet)");

            // (b) tooth #2 — count/position invariant, index refined.
            Assert.IsNotNull(symbol.LineAnchors);
            Assert.AreEqual(1, symbol.LineAnchors.Length, "line-center still places a single anchor (arc-length invariant)");
            Assert.Greater(symbol.LineAnchors[0].Segment, 0,
                "the anchor's segment index must be refined onto the finer path (0 would mean it never resubdivided)");

            // (c) tooth #3 — resolves to the correct arc position on the RENDER curve. Independently project the
            // geographic midpoint of the (still 2-vertex) tile-local line — the arc-length midpoint of a straight
            // 2-point segment is its linear midpoint — and compare against what the anchor resolves to.
            double2 midTile = new double2(extent * 0.5, extent * 0.5);
            double2 midLonLat = tileId.ToLonLat(midTile.x, midTile.y, extent);
            double3 expectedRenderMid = projection.Project(
                new GeoCoordinate { Latitude = midLonLat.y, Longitude = midLonLat.x });

            LineAnchor anchor = symbol.LineAnchors[0];
            double3 segStart = symbol.PathRender[anchor.Segment];
            double3 segEnd = symbol.PathRender[anchor.Segment + 1];
            double3 resolved = segStart + (segEnd - segStart) * (double)anchor.T; // math.lerp has no double3 overload in the shim
            double resolveError = math.length(resolved - expectedRenderMid);
            // FIXTURE FRAGILITY: this passes at ~0 error only because the z=1 diagonal (0,0)->(extent,extent)
            // subdivides into an EVEN step count, which puts a subdivision vertex exactly at the arc-length
            // midpoint. An odd count would put the anchor mid-sub-segment instead (~1 km sagitta on this
            // geometry, over the 1.0 m tolerance below) and flip this tooth falsely RED. A geometry/zoom
            // change here needs re-checking the resulting subdivision count's parity, not just re-running.
            Assert.Less(resolveError, 1.0,
                "the anchor must resolve to (approximately) the true midpoint on the finer render curve");
        }

        // ── Stage B: single-world clip — point anchors outside [0, extent) are source world-copies ──────

        /// <summary>Hand-encodes a MultiPoint MVT geometry command stream: a single MoveTo(count=N) followed
        /// by N zigzag-encoded cumulative deltas from a cursor starting at (0,0) — mirrors
        /// <see cref="MvtGeometry.Decode"/>'s MoveTo-repeat semantics, where each repeat starts its own
        /// 1-point path (i.e. a MultiPoint feature decodes to N separate 1-point paths).</summary>
        private static uint[] MultiPointGeometry(params double2[] tilePoints)
        {
            var stream = new List<uint> { 1u | ((uint)tilePoints.Length << 3) }; // MoveTo, count=N
            long cursorX = 0, cursorY = 0;
            foreach (double2 p in tilePoints)
            {
                long x = (long)p.x, y = (long)p.y;
                stream.Add(ZigZagEncode(x - cursorX));
                stream.Add(ZigZagEncode(y - cursorY));
                cursorX = x;
                cursorY = y;
            }
            return stream.ToArray();
        }

        [Test]
        public void Extract_PointPlacement_ClipsOutOfBoundsAnchorsToTile()
        {
            const uint extent = 4096;
            // In-bounds (kept): an interior point, another interior point, the min edge (inclusive), and the
            // max edge (extent - 1, inclusive). Out-of-bounds (dropped): the plan's own examples
            // (extent*1.5, -extent*0.5, extent+1) plus the half-open upper-bound edges x==extent/y==extent
            // (a shared-edge anchor belongs to the NEXT tile, not this one).
            double2 inA = new double2(100, 200);
            double2 outXHigh = new double2(extent * 1.5, 200);
            double2 inB = new double2(300, 300);
            double2 outXNeg = new double2(-(double)extent * 0.5, 200);
            double2 outYHigh = new double2(250, extent + 1);
            double2 inMinEdge = new double2(0, 0);
            double2 inMaxEdge = new double2(extent - 1, extent - 1);
            double2 outXEdge = new double2(extent, 50);
            double2 outYEdge = new double2(50, extent);

            var feature = new DictionaryFeature(properties: null, geometryType: MapRenderer.Core.Tiles.TileGeometryType.Point, hasId: false, geometry: MultiPointGeometry(
                    inA, outXHigh, inB, outXNeg, outYHigh, inMinEdge, inMaxEdge, outXEdge, outYEdge));
            var tileId = new TileId { Z = 1, X = 0, Y = 0 };
            var tile = TestDecodedTiles.Of("points", tileId, new List<IFeature> { feature }, extent);
            var projection = new WebMercatorProjection();

            var pointLayer = new SymbolStyle.StyleLayer
            {
                Id = "points",
                LayerType = MapRenderer.Core.Style.StyleLayerType.Symbol,
                SourceLayer = "points",
                Paint = TestStyle.SymbolPaint(),
                // Literal text-field (no {} interpolation) so resolution doesn't depend on feature properties
                // — this feature carries none (properties: null) (mirrors the LineLayer literal-text pattern
                // used elsewhere in this file).
                Layout = TestStyle.SymbolLayout("{\"text-field\":\"L\"}"),
            };

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(pointLayer, tile, tileId, 0.0, projection, symbols);

            Assert.AreEqual(4, symbols.Count,
                "only the 4 in-bounds anchors are emitted; the 5 out-of-bounds/edge points are clipped");

            double2[] expectedTilePoints = { inA, inB, inMinEdge, inMaxEdge };
            for (int i = 0; i < expectedTilePoints.Length; i++)
            {
                double2 lonLat = tileId.ToLonLat(expectedTilePoints[i].x, expectedTilePoints[i].y, extent);
                double3 expectedAnchor = projection.Project(
                    new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                Assert.AreEqual(expectedAnchor.x, symbols[i].AnchorRender.x, 1e-6, $"symbol[{i}].anchor.x");
                Assert.AreEqual(expectedAnchor.y, symbols[i].AnchorRender.y, 1e-6, $"symbol[{i}].anchor.y");
                Assert.AreEqual(expectedAnchor.z, symbols[i].AnchorRender.z, 1e-6, $"symbol[{i}].anchor.z");
                Assert.AreEqual(i, symbols[i].FeatureIndex,
                    "ordinal stays contiguous across skipped out-of-bounds points (no gaps from the clip)");
            }
        }

        [Test]
        public void Extract_WithFilter_NarrowsToNamedFeature()
        {
            MvtTile tile = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTile, LoadFixture()));
            var projection = new WebMercatorProjection();

            var symbols = new List<SymbolStyle.SymbolFeature>();
            SymbolFeatureExtractor.Extract(
                CentroidsLayer(filterJson: "[\"==\",[\"get\",\"ABBREV\"],\"Afg.\"]"),
                tile, FixtureTile, 0.0, projection, symbols);

            Assert.AreEqual(1, symbols.Count, "the ABBREV=='Afg.' filter selects exactly one feature");
            Assert.AreEqual("Afghanistan", symbols[0].Text);
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // FillPaintTests — Fill.PaintProperties: classification, pinned values, BakeNumbers distinct-alpha
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S13 / S60 — <see cref="Fill.PaintProperties"/>: classification, pinned values, BakeNumbers
    /// distinct-alpha, and SourceLayerResolver seam routing.
    ///
    /// Unity EditMode only (see file header) — it exercises the MapRenderer.Jobs.Tiles/.Mvt decode seam.
    ///
    /// S60 changes:
    ///   • All properties are <c>StyleProperty&lt;T&gt;</c>; XKind → <c>.Kind</c>; null-guard →
    ///     <c>.DependsOnFeature</c>; DataDrivenX → same property.
    ///   • fill-translate: ONE <c>StyleProperty&lt;double2&gt;</c>; access via <c>.Translate.Evaluate(0.0).x/y</c>.
    ///   • BakeNumbers: now typed <c>StyleProperty&lt;float&gt;</c>.
    /// </summary>
    [TestFixture]
    public class FillPaintTests
    {

        /// <summary>IR C1 P3: the address the committed fixture is decoded at (its buffers are stamped with
        /// it). z0/0/0 — the fixture's own tile.</summary>
        private static readonly TileId FixtureTileId = new TileId { Z = 0, X = 0, Y = 0 };

        /// <summary>IR C1 P3: a decoded tile owns Allocator.Persistent buffers — release them per test.</summary>
        [TearDown]
        public void ReleaseFixtureTiles() => TestDecodedTiles.DisposeAll();
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
            var layer = TestDecodedTiles.Track(MvtDecoder.Decode(FixtureTileId, LoadFixture())).GetLayer("countries");
            Assert.IsNotNull(layer, "countries layer must be present in fixture.");
            return layer;
        }

        private static List<IFeature> AdaptFeatures(MvtLayer layer)
        {
            var features = new List<IFeature>(layer.Features.Count);
            foreach (var f in layer.Features)
                features.Add(f); // A6: MvtFeature implements IFeature directly — no adapter
            return features;
        }

        private static Fill.StyleLayer MakeFillLayer(string paintJson, string sourceLayer = "countries")
        {
            return new Fill.StyleLayer
            {
                Id          = "test-fill",
                LayerType   = StyleLayerType.Fill,
                SourceLayer = sourceLayer,
                Paint       = TestStyle.FillPaint(paintJson),
                Layout      = TestStyle.FillLayout(),
            };
        }

        // ── #1a: Constant fill-color → Constant kind, pinned RGB ────────────────

        [Test]
        public void FillPaint_ConstantColor_ClassifiesAsConstant()
        {
            var layer = MakeFillLayer("{\"fill-color\":[\"rgba\",255,0,0,1]}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.ColorKind,
                "An rgba(...) literal must classify as Constant.");
            Assert.IsFalse(fp.Color.DependsOnFeature,
                "Constant color must not depend on feature.");
            Assert.IsFalse(fp.IsInertFallback,
                "A layer with fill-color set is not inert.");

            var c = fp.Color.Evaluate(0.0);
            Assert.AreEqual(1.0, c.R, 1e-4, "Red channel must be 1.0 for rgba(255,0,0,1).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Green channel must be 0.0 for rgba(255,0,0,1).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Blue channel must be 0.0 for rgba(255,0,0,1).");
        }

        // ── #1b: Zoom-dependent fill-opacity → Zoom kind ────────────────────────

        [Test]
        public void FillPaint_ZoomOpacity_ClassifiesAsZoom()
        {
            const string paintJson =
                "{\"fill-opacity\":[\"interpolate\",[\"linear\"],[\"zoom\"],0,0.5,10,1.0]}";
            var layer = MakeFillLayer(paintJson);
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Zoom, fp.OpacityKind,
                "A zoom-interpolate expression must classify as Zoom.");

            float v0 = fp.Opacity.Evaluate(0.0);
            Assert.AreEqual(0.5f, v0, 0.01f, "At zoom=0, interpolated opacity must be 0.5.");

            float v10 = fp.Opacity.Evaluate(10.0);
            Assert.AreEqual(1.0f, v10, 0.01f, "At zoom=10, interpolated opacity must be 1.0.");
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
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Feature, fp.ColorKind,
                "A [\"get\",...] match expression must classify as Feature.");
            Assert.IsTrue(fp.Color.DependsOnFeature,
                "Data-driven color must DependsOnFeature.");
            Assert.IsFalse(fp.IsInertFallback);
        }

        // ── #1d: Absent fill-color → Constant kind (spec default #000000) ───────

        [Test]
        public void FillPaint_AbsentColor_UsesSpecDefault_Black()
        {
            var layer = MakeFillLayer("{}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.ColorKind,
                "Absent fill-color must use spec default (Constant kind).");
            Assert.IsFalse(fp.Color.DependsOnFeature);

            var c = fp.Color.Evaluate(0.0);
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
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.OpacityKind);
            float v = fp.Opacity.Evaluate(0.0);
            Assert.AreEqual(1.0f, v, 1e-6f, "Default fill-opacity must be 1.0.");
        }

        // ── #1f: fill-translate-anchor "viewport" → 1.0 ─────────────────────────

        [Test]
        public void FillPaint_TranslateAnchorViewport_IsOne()
        {
            var layer = MakeFillLayer("{\"fill-translate-anchor\":\"viewport\"}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.TranslateAnchorKind);
            float v = fp.TranslateAnchor.Evaluate(0.0);
            Assert.AreEqual(1.0f, v, 1e-6f, "fill-translate-anchor 'viewport' must encode as 1.0.");
        }

        // ── #1g: fill-translate-anchor "map" (spec default) → 0.0 ───────────────

        [Test]
        public void FillPaint_TranslateAnchorMap_IsZero()
        {
            var layer = MakeFillLayer("{\"fill-translate-anchor\":\"map\"}");
            var fp    = layer.Paint;

            float v = fp.TranslateAnchor.Evaluate(0.0);
            Assert.AreEqual(0.0f, v, 1e-6f, "fill-translate-anchor 'map' must encode as 0.0.");
        }

        // ── #1h: fill-translate [16, -8] → double2 pinned ───────────────────────

        [Test]
        public void FillPaint_Translate_ComponentsArePinned()
        {
            var layer = MakeFillLayer("{\"fill-translate\":[16,-8]}");
            var fp    = layer.Paint;

            Assert.AreEqual(ExpressionKind.Constant, fp.TranslateKind);
            var t = fp.Translate.Evaluate(0.0);
            Assert.AreEqual(16.0, t.x, 1e-6, "fill-translate x must be 16.");
            Assert.AreEqual(-8.0, t.y, 1e-6, "fill-translate y must be -8.");
        }

        // ── #1i: fill-outline-color absent → IsFallback=true, falls back to fill-color ──

        [Test]
        public void FillPaint_AbsentOutlineColor_FallsBackToFillColor()
        {
            var layer = MakeFillLayer("{\"fill-color\":[\"rgba\",0,0,255,1]}");
            var fp    = layer.Paint;

            Assert.IsTrue(fp.OutlineColorIsFallback,
                "Absent fill-outline-color must set OutlineColorIsFallback=true.");

            var c = fp.OutlineColor.Evaluate(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Fallback outline R must match fill-color R=0.");
            Assert.AreEqual(0.0, c.G, 1e-4, "Fallback outline G must match fill-color G=0.");
            Assert.AreEqual(1.0, c.B, 1e-4, "Fallback outline B must match fill-color B=1.");
        }

        // ── #1j: fill-outline-color present → not a fallback ────────────────────

        [Test]
        public void FillPaint_PresentOutlineColor_NotFallback()
        {
            var layer = MakeFillLayer("{\"fill-outline-color\":[\"rgba\",0,255,0,1]}");
            var fp    = layer.Paint;

            Assert.IsFalse(fp.OutlineColorIsFallback,
                "Present fill-outline-color must not be flagged as fallback.");
            Assert.IsFalse(fp.IsInertFallback,
                "A layer with fill-outline-color set is not inert.");

            var c = fp.OutlineColor.Evaluate(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Outline R must be 0 for rgba(0,255,0,1).");
            Assert.AreEqual(1.0, c.G, 1e-4, "Outline G must be 1 for rgba(0,255,0,1).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Outline B must be 0 for rgba(0,255,0,1).");
        }

        // ── #1k: fill-pattern present → PatternName is set ──────────────────────

        [Test]
        public void FillPaint_PatternName_IsParsed()
        {
            var layer = MakeFillLayer("{\"fill-pattern\":\"grass\"}");
            var fp    = layer.Paint;

            Assert.AreEqual("grass", fp.PatternName, "fill-pattern must be read as PatternName.");
            Assert.IsFalse(fp.IsInertFallback);
        }

        // ── #1l: null paint → IsInertFallback ────────────────────────────────────

        [Test]
        public void FillPaint_NullPaint_IsInertFallback()
        {
            var layer = MakeFillLayer(null);
            var fp    = layer.Paint;

            Assert.IsTrue(fp.IsInertFallback, "A layer with null Paint must be IsInertFallback.");
        }

        // ── #2: CPU gamma formula — sRGB→linear approximation ────────────────────

        [Test]
        public void SrgbToLinear_Formula_KnownValues()
        {
            double srgb = 0.5;
            double linear = SrgbToLinear(srgb);
            Assert.AreEqual(0.2140, linear, 0.001,
                "sRGB 0.5 must convert to linear ~0.2140 (standard sRGB→linear formula).");

            Assert.AreEqual(1.0, SrgbToLinear(1.0), 1e-5, "sRGB 1.0 must map to linear 1.0.");
            Assert.AreEqual(0.0, SrgbToLinear(0.0), 1e-5, "sRGB 0.0 must map to linear 0.0.");
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

        /// <summary>
        /// One evaluation per feature, mirroring the tile builders' inner loop. A failed evaluation
        /// (expression error, wrong type) yields <paramref name="fallback"/> for that feature.
        /// </summary>
        private static List<double> BakeNumbers(
            StyleProperty<float> prop, double zoom, IEnumerable<IFeature> features, double fallback)
        {
            var result = new List<double>();
            foreach (var feature in features)
                result.Add(prop.TryEvaluate(zoom, feature, out float n) ? n : fallback);
            return result;
        }

        [Test]
        public void BakeNumbers_DataDrivenOpacity_ProducesDistinctAlphas()
        {
            const string paintJson =
                "{\"fill-opacity\":[\"match\",[\"get\",\"CONTINENT\"]," +
                "\"Asia\",1.0," +
                "\"South America\",0.5," +
                "0.75]}";
            var layer    = MakeFillLayer(paintJson);
            var fp       = layer.Paint;

            Assert.AreEqual(ExpressionKind.Feature, fp.OpacityKind,
                "Data-driven opacity must classify as Feature.");

            var mvtLayer = LoadCountries();
            var features = AdaptFeatures(mvtLayer);

            List<double> alphas = BakeNumbers(fp.Opacity, 0.0, features, 1.0);

            Assert.AreEqual(features.Count, alphas.Count,
                "BakeNumbers must return one alpha per feature.");

            var distinct = new System.Collections.Generic.HashSet<double>();
            foreach (double a in alphas)
                distinct.Add(Math.Round(a, 2));

            Assert.GreaterOrEqual(distinct.Count, 2,
                $"Data-driven fill-opacity on CONTINENT must produce ≥2 distinct alpha values " +
                $"across {features.Count} features (got {distinct.Count}).");
        }

        // ── #5: SourceLayerResolver seam ────────────────────────────────────────

        [Test]
        public void SourceLayerResolver_RoutesLayerNameToMvtLayer()
        {
            byte[] bytes  = LoadFixture();
            using var tile = MvtDecoder.Decode(FixtureTileId, bytes);

            var styleLayer = new StyleLayer
            {
                Id          = "test",
                SourceLayer = "countries",
            };

            var mvtLayer = SourceLayerResolver.ResolveTileLayer(styleLayer, tile);
            Assert.IsNotNull(mvtLayer,
                "SourceLayerResolver.ResolveTileLayer must return the MVT layer for SourceLayer='countries'.");
            Assert.AreEqual("countries", mvtLayer.Name,
                "Resolved MVT layer name must match SourceLayer.");
        }

        [Test]
        public void SourceLayerResolver_MissingLayer_ReturnsNull()
        {
            byte[] bytes = LoadFixture();
            using var tile = MvtDecoder.Decode(FixtureTileId, bytes);

            var styleLayer = new StyleLayer
            {
                Id          = "test",
                SourceLayer = "__nonexistent_layer__",
            };

            var mvtLayer = SourceLayerResolver.ResolveTileLayer(styleLayer, tile);
            Assert.IsNull(mvtLayer,
                "SourceLayerResolver must return null for a layer name not present in the tile.");
        }

        [Test]
        public void SourceLayerResolver_NullSourceLayer_ReturnsNull()
        {
            byte[] bytes = LoadFixture();
            using var tile = MvtDecoder.Decode(FixtureTileId, bytes);

            var styleLayer = new StyleLayer
            {
                Id          = "background",
                SourceLayer = null,
            };

            var mvtLayer = SourceLayerResolver.ResolveTileLayer(styleLayer, tile);
            Assert.IsNull(mvtLayer,
                "SourceLayerResolver must return null when SourceLayer is null (background layers).");
        }

        // ── fill-antialias: the boolean the Style Spec declares ─────────────────

        /// <summary>
        /// <c>fill-antialias</c> is a JSON boolean, and <c>false</c> must survive the parse as 0.
        /// It once did not: the converter projected the value through <c>Value.AsNumber()</c>, which
        /// throws on a boolean, and the property's own <c>try</c>/<c>catch</c> swallowed that into the
        /// default 1 — so the ONE value anybody writes the property to say was the one it could not
        /// express. Everything that is not a boolean still falls to the default, which is the same
        /// <c>catch</c> doing its intended job.
        /// </summary>
        /// <param name="paintJson">The layer's paint block.</param>
        /// <param name="expected">The value <c>Antialias</c> must evaluate to.</param>
        [TestCase("{\"fill-antialias\":false}", false)]
        [TestCase("{\"fill-antialias\":true}", true)]
        [TestCase("{\"fill-color\":\"#fff\"}", true)]
        [TestCase("{\"fill-antialias\":0}", true)]
        [TestCase("{\"fill-antialias\":[\"get\",\"aa\"]}", true)]
        public void FillPaint_Antialias_ParsesAsABoolean(string paintJson, bool expected)
        {
            // MakeFillLayer parses with Parse's default antialiasDefault=true (the Style Spec's own
            // default), so the cases that fall back land on true exactly as before. The project default
            // is exercised by FillAntialiasBandTests.
            var fp = MakeFillLayer(paintJson).Paint;

            Assert.AreEqual(expected, fp.Antialias.Evaluate(0.0),
                $"fill-antialias in {paintJson} must evaluate to {expected}.");
            Assert.IsFalse(fp.Antialias.DependsOnFeature,
                "fill-antialias must never be data-driven — a feature-dependent value falls to the default.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // WholeDocumentGate — the retired whole-document restyle gate, composing SurvivingLayerGate's predicates
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The retired WHOLE-DOCUMENT restyle gate, kept here because it still names the concept these
    /// teeth assert: every layer of the new document takes the old one's meshes and materials, at the same
    /// index. Composes the two predicates production DOES still call —
    /// <see cref="SurvivingLayerGate.RootMatches"/> and <see cref="SurvivingLayerGate.LayerSurvives"/> — so
    /// the gate's rules stay pinned even though nothing composes them this way any more.</summary>
    internal static class WholeDocumentGate
    {
        /// <summary>True when both documents are present, their roots-minus-layers match, and every layer
        /// survives against the layer at its own index.</summary>
        internal static bool AllLayersSurvive(StyleDocument oldStyle, StyleDocument newStyle)
        {
            if (oldStyle == null || newStyle == null) return false;
            if (!SurvivingLayerGate.RootMatches(oldStyle, newStyle)) return false;
            if (oldStyle.Layers.Count != newStyle.Layers.Count) return false;

            for (int i = 0; i < oldStyle.Layers.Count; i++)
                if (!SurvivingLayerGate.LayerSurvives(oldStyle.Layers[i], newStyle.Layers[i]))
                    return false;

            return true;
        }
    }
}
