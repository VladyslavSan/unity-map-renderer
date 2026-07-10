// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests
{
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
}
