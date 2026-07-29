using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests
{
    /// <summary>
    /// P2 regression — the sprite sheet must be fetched for a style that has <b>no symbol layers</b>.
    ///
    /// <para>The sheet used to be an icons-only resource, so <c>SymbolLabelSubsystem.SetStyle</c> returned
    /// early ("no symbol layers — stay idle") <i>before</i> kicking off the fetch. Once <c>fill-pattern</c>
    /// began resolving against the same sheet that early return became a silent feature-killer: a style with
    /// pattern fills and no symbol layers would never fetch a sheet, so every pattern layer would stay
    /// unresolved and clip forever — no error, no warning, just missing fills.</para>
    ///
    /// <para>Liberty hides this (it has symbol layers), which is exactly why it needs its own tooth.</para>
    /// </summary>
    [TestFixture]
    public class SpriteFetchGatingTests
    {
        private const string SymbolFreePatternStyle = @"{
            ""version"": 8,
            ""sprite"": ""https://example.invalid/sprite"",
            ""sources"": { ""src"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""plazas"", ""type"": ""fill"", ""source"": ""src"", ""source-layer"": ""transportation"",
                  ""paint"": { ""fill-pattern"": ""marker"" } }
            ]
        }";

        [UnityTest]
        public IEnumerator StyleWithNoSymbolLayers_StillFetchesTheSpriteSheet()
        {
            var go  = new GameObject("SpriteFetchGatingHost");
            var cam = go.AddComponent<Camera>();
            var mapCamera = new MapCamera(cam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));
            var subsystem = new SymbolLabelSubsystem(mapCamera);
            try
            {
                int fetches = 0;
                subsystem.SpriteSourceFactoryOverride = _ =>
                {
                    fetches++;
                    return new FixtureSpriteSource();
                };

                // No symbol layers at all — the case the early return used to swallow.
                var style = StyleParser.Parse(SymbolFreePatternStyle);
                subsystem.SetStyle(style, System.Array.Empty<SymbolStyle.StyleLayer>());

                Assert.IsFalse(subsystem.HasSymbolLayers,
                    "precondition: this style must genuinely have no symbol layers, or the test proves nothing");
                Assert.AreEqual(1, fetches,
                    "the sprite sheet must be fetched even with zero symbol layers — fill-pattern layers " +
                    "resolve against the same sheet. A 0 here means the no-symbol-layers early return has " +
                    "moved back above the fetch and pattern fills will silently never paint.");

                // The fixture source completes synchronously, but the decode hops to the main thread — pump a
                // few frames so the sheet actually lands rather than asserting on the in-flight state.
                for (int i = 0; i < 8 && subsystem.SpriteAtlas == null; i++) yield return null;

                Assert.IsNotNull(subsystem.SpriteAtlas,
                    "the fetched sheet must reach SpriteAtlas — that is what RenderLayerSet.SetSprites pushes " +
                    "to the fill layers.");
                Assert.IsTrue(
                    MapRenderer.Core.Style.Fill.FillPattern.TryResolve("marker", subsystem.SpriteAtlas, out _),
                    "the fixture sheet's 'marker' sprite must resolve through the delivered atlas");
            }
            finally
            {
                subsystem.Dispose();
                Object.DestroyImmediate(go);
            }
        }
    }
}
