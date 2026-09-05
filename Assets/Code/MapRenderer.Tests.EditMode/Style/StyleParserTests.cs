// Unity EditMode only. It exercises MapRenderer.Jobs.Tiles/.Mvt (the tile-decode seam, moved out of
// Core), which Tools/core-tests does not compile — this file is not registered there.

using System;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using Background = MapRenderer.Core.Style.Background;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Style
{
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

            // A raw paint/layout sub-tree from the real style is retained for later stages.
            StyleLayer fill = null;
            foreach (var l in doc.Layers)
                if (l.LayerType == StyleLayerType.Fill && l.PaintJson != null) { fill = l; break; }
            Assert.IsNotNull(fill, "a fill layer with a paint block exists in the real style");
            Assert.IsTrue(fill.PaintJson.IsObject, "real paint sub-tree retained as queryable JSON");
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
            Assert.IsNull(layer.PaintJson);
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

            // Unknown paint/layout props retained inside the raw subtrees.
            Assert.IsNotNull(doc.Layers[0].PaintJson);
            Assert.AreEqual("#fff", doc.Layers[0].PaintJson.GetString("future-paint-prop"));
            Assert.IsNotNull(doc.Layers[0].LayoutJson);
            Assert.AreEqual("visible", doc.Layers[0].LayoutJson.GetString("future-layout-prop"));
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

            Assert.IsNotNull(layer.LayoutJson);
            Assert.AreEqual("round", layer.LayoutJson.GetString("line-cap"));
            Assert.AreEqual("round", layer.LayoutJson.GetString("line-join"));

            Assert.IsNotNull(layer.PaintJson);
            Assert.AreEqual("#ff0000", layer.PaintJson.GetString("line-color"));
            Assert.AreEqual(2.0, layer.PaintJson.GetDouble("line-width"), 1e-9);
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
}
