// Unity EditMode only — drives the live MapView.SetStyle path (MonoBehaviour + NativeArray jobs).
// NOT included in Tools/core-tests.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Source;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S83b acceptance — the live <see cref="MapView.SetStyle"/> path: file:// offline (zero network),
    /// inline-<c>tiles[]</c> fetch-side short-circuit, multi-source per-source routing, restyle reuses an
    /// unchanged source's pipeline+bytes, and <c>styleId</c> establishment. (The TileJSON parse+fill proof
    /// is S83a's fast-core tooth; not re-tested here.)
    /// </summary>
    [TestFixture]
    public class MapViewSetStyleTests
    {
        // Spin a (Preserved) UniTask to completion on the main thread — the file:// document loader and the
        // inline-source spec build both complete on the ThreadPool (no PlayerLoop), so this never deadlocks.
        private static void Await(UniTask task, int maxSpins = 10000)
        {
            var t = task.Preserve();
            int s = 0;
            while (!t.Status.IsCompleted() && s++ < maxSpins) Thread.Sleep(1);
            t.GetAwaiter().GetResult();
        }

        private static void PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
                Thread.Sleep(1);
            }
        }

        private static MapView NewView(out GameObject go)
        {
            go = new GameObject("MapView");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64; view.Config.MaxMeshBuildsPerTick = 64;
            return view;
        }

        private static string OneFillStyle(string sourceId, string tilesTemplate) => $@"{{
            ""version"": 8,
            ""sources"": {{ ""{sourceId}"": {{ ""type"": ""vector"", ""tiles"": [""{tilesTemplate}""] }} }},
            ""layers"": [
                {{ ""id"": ""f"", ""type"": ""fill"", ""source"": ""{sourceId}"",
                   ""source-layer"": ""countries"", ""paint"": {{ ""fill-color"": [""rgba"",200,50,50,1] }} }}
            ]
        }}";

        // Writes the fixture tile to <temp>/<sub>/0/0/0.mvt and returns a file:// {z}/{x}/{y} template.
        private static string WriteFileTileFixture(string sub)
        {
            string dir = Path.Combine(Application.temporaryCachePath, sub);
            Directory.CreateDirectory(Path.Combine(dir, "0", "0"));
            File.WriteAllBytes(Path.Combine(dir, "0", "0", "0.mvt"), SampleTileFixture.Bytes());
            return "file://" + Path.Combine(dir, "{z}", "{x}", "{y}.mvt");
        }

        // ── THE decisive tooth: a file:// style whose vector source resolves to file:// tiles renders with
        //    ZERO network — no UnityWebRequestDataSource is ever constructed across SetStyle + settle. ──────
        [Test]
        public void SetStyle_FileUriChain_RendersOffline_ZeroNetwork()
        {
            string tilesTemplate = WriteFileTileFixture("s83b-offline-tiles");
            string styleDir = Path.Combine(Application.temporaryCachePath, "s83b-offline-style");
            Directory.CreateDirectory(styleDir);
            string stylePath = Path.Combine(styleDir, "style.json");
            File.WriteAllText(stylePath, OneFillStyle("src", tilesTemplate.Replace("\\", "/")));
            string styleUri = "file://" + stylePath;

            int netBefore = UnityWebRequestDataSource.DebugConstructedCount;
            var view = NewView(out var go);
            try
            {
                Await(view.SetStyle(styleUri));          // file:// style doc → parse → resolve (inline) → wire
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "the file:// tile must build from the local fixture");
                Assert.AreEqual(netBefore, UnityWebRequestDataSource.DebugConstructedCount,
                    "a file:// chain must construct ZERO UnityWebRequestDataSource — no network. " +
                    "A >0 delta means the style/TileJSON/tiles hit the network (offline guarantee broken).");
                Assert.AreEqual(styleUri, view.StyleId, "SetStyle(uri) ⇒ styleId == uri");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Inline tiles[] short-circuit (fetch side): a source with inline tiles triggers ZERO TileJSON
        //    document fetches. ─────────────────────────────────────────────────────────────────────────
        [Test]
        public void SetStyle_InlineTiles_FetchesNoTileJson()
        {
            string tilesTemplate = WriteFileTileFixture("s83b-inline-tiles");
            int docFetches = 0;
            var view = NewView(out var go);
            view.View.DocumentLoaderOverride = (uri, ct) => { Interlocked.Increment(ref docFetches); return UniTask.FromResult(""); };
            try
            {
                var style = StyleParser.Parse(OneFillStyle("src", tilesTemplate.Replace("\\", "/")));
                Await(view.SetStyle(style, "inline-style"));
                PumpUntilSettled(view);

                Assert.AreEqual(0, docFetches,
                    "an inline-tiles[] source must trigger NO TileJSON document fetch (fetch-side short-circuit).");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }), "tile builds from inline tiles");
                Assert.AreEqual("inline-style", view.StyleId, "StyleDocument overload surfaces the caller styleId");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── A url (TileJSON) source fetches the TileJSON exactly once and resolves its tiles from it. ──────
        [Test]
        public void SetStyle_TileJsonUrlSource_FetchesOnceAndResolves()
        {
            string tilesTemplate = WriteFileTileFixture("s83b-tilejson-tiles").Replace("\\", "/");
            int docFetches = 0;
            var view = NewView(out var go);
            // Document loader returns a TileJSON whose tiles[] is the local file:// fixture template.
            view.View.DocumentLoaderOverride = (uri, ct) =>
            {
                Interlocked.Increment(ref docFetches);
                return UniTask.FromResult($@"{{ ""tilejson"":""3.0.0"", ""tiles"":[""{tilesTemplate}""], ""minzoom"":0, ""maxzoom"":0 }}");
            };
            try
            {
                // Source uses a `url` (no inline tiles) → resolution must fetch + fill from the TileJSON.
                var style = StyleParser.Parse(@"{
                    ""version"": 8,
                    ""sources"": { ""src"": { ""type"": ""vector"", ""url"": ""https://example.com/tiles.json"" } },
                    ""layers"": [ { ""id"":""f"", ""type"":""fill"", ""source"":""src"", ""source-layer"":""countries"",
                                    ""paint"": { ""fill-color"": [""rgba"",200,50,50,1] } } ]
                }");
                Await(view.SetStyle(style, "tilejson-style"));
                PumpUntilSettled(view);

                Assert.AreEqual(1, docFetches, "a url-only source fetches its TileJSON exactly once");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "tiles resolved from the TileJSON build (a no-op resolve would leave the source tile-less → no build)");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Multi-source per-source routing: two vector sources A,B each with a layer; each source's own
        //    counting IDataSource is fetched (both > 0). A single-source shortcut drives one to 0 → fails. ──
        [Test]
        public void SetStyle_MultiSource_RoutesEachLayerToItsOwnSource()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var perTemplate = new Dictionary<string, TestDataSource>();
            var view = NewView(out var go);
            view.View.TileSourceFactoryOverride = template =>
            {
                var src = TestDataSource.FromBytes(bytes);
                perTemplate[template] = src;
                return src;
            };
            try
            {
                var style = StyleParser.Parse(@"{
                    ""version"": 8,
                    ""sources"": {
                        ""A"": { ""type"":""vector"", ""tiles"":[""https://a/{z}/{x}/{y}.pbf""] },
                        ""B"": { ""type"":""vector"", ""tiles"":[""https://b/{z}/{x}/{y}.pbf""] }
                    },
                    ""layers"": [
                        { ""id"":""la"", ""type"":""fill"", ""source"":""A"", ""source-layer"":""countries"",
                          ""paint"": { ""fill-color"": [""rgba"",200,50,50,1] } },
                        { ""id"":""lb"", ""type"":""fill"", ""source"":""B"", ""source-layer"":""countries"",
                          ""paint"": { ""fill-color"": [""rgba"",50,50,200,1] } }
                    ]
                }");
                Await(view.SetStyle(style, "multi"));
                PumpUntilSettled(view);

                Assert.IsTrue(perTemplate.ContainsKey("https://a/{z}/{x}/{y}.pbf"), "source A pipeline built");
                Assert.IsTrue(perTemplate.ContainsKey("https://b/{z}/{x}/{y}.pbf"), "source B pipeline built");
                int fa = perTemplate["https://a/{z}/{x}/{y}.pbf"].FetchCount;
                int fb = perTemplate["https://b/{z}/{x}/{y}.pbf"].FetchCount;
                Assert.Greater(fa, 0, "source A is fetched for A's records");
                Assert.Greater(fb, 0, "source B is fetched for B's records (a single-source shortcut leaves this 0)");
                Assert.AreEqual(fa, fb, "routing is symmetric — each source fetches only its own (tile,source) records");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        // ── Restyle reuses an UNCHANGED source's pipeline: it is not re-created (construct count stays 1)
        //    and its cached bytes are reused (FetchCount unchanged across the second SetStyle). ────────────
        [Test]
        public void Restyle_UnchangedSource_KeepsPipelineAndReusesBytes()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            int constructsForA = 0;
            TestDataSource srcA = null;
            var view = NewView(out var go);
            view.View.TileSourceFactoryOverride = template =>
            {
                if (template == "https://a/{z}/{x}/{y}.pbf") { constructsForA++; srcA = TestDataSource.FromBytes(bytes); return srcA; }
                return TestDataSource.FromBytes(bytes);
            };
            try
            {
                // Style 1: source A + a red fill layer.
                Await(view.SetStyle(StyleParser.Parse(@"{
                    ""version"":8,
                    ""sources"": { ""A"": { ""type"":""vector"", ""tiles"":[""https://a/{z}/{x}/{y}.pbf""] } },
                    ""layers"": [ { ""id"":""f"", ""type"":""fill"", ""source"":""A"", ""source-layer"":""countries"",
                                    ""paint"": { ""fill-color"": [""rgba"",200,50,50,1] } } ]
                }"), "v1"));
                PumpUntilSettled(view);
                Assert.AreEqual(1, constructsForA, "source A constructed once on first SetStyle");
                int fetchesAfterV1 = srcA.FetchCount;
                Assert.Greater(fetchesAfterV1, 0, "source A fetched its tile on v1");

                // Style 2: SAME source A (unchanged def ⇒ same SourceKey), DIFFERENT layer paint (green).
                Await(view.SetStyle(StyleParser.Parse(@"{
                    ""version"":8,
                    ""sources"": { ""A"": { ""type"":""vector"", ""tiles"":[""https://a/{z}/{x}/{y}.pbf""] } },
                    ""layers"": [ { ""id"":""f"", ""type"":""fill"", ""source"":""A"", ""source-layer"":""countries"",
                                    ""paint"": { ""fill-color"": [""rgba"",50,200,50,1] } } ]
                }"), "v2"));
                PumpUntilSettled(view);

                Assert.AreEqual(1, constructsForA,
                    "an UNCHANGED source must NOT be re-created on restyle (kept pipeline ⇒ identity preserved)");
                Assert.AreEqual(fetchesAfterV1, srcA.FetchCount,
                    "an unchanged source's bytes are reused from its kept cache on restyle — NO re-fetch. " +
                    "A teardown-and-rebuild (or Scheduler.Release on a kept source) would re-fetch → count grows → fails.");
                Assert.AreEqual("v2", view.StyleId, "restyle updates styleId");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }
    }
}
