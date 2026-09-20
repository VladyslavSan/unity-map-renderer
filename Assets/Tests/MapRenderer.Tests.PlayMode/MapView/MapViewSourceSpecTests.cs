// Epic A / A2 acceptance — plan §F tooth 13 (HIGH c): MapView.BuildSourceSpecs must resolve specs to ONLY
// the MVT-fetching layers (fill/line/symbol with a non-empty source) — background is source-less by design,
// and raster/circle/hillshade/unknown are unsupported-for-now and must never have TileJSON/document loaded
// or a tile source constructed for them (RenderLayerFactory.TryGetFetchSource is the ONE registry).
//
// PlayMode: drives the real async SetStyle + tile-settle over real frames (yield, never Thread.Sleep).

using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Style;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.PlayMode.MapViews
{
    [TestFixture]
    public class MapViewSourceSpecTests
    {
        // rasterSrc is URL-only (no inline tiles[]) — a regression that incorrectly builds a spec for a
        // raster layer would fetch its TileJSON too, making "zero document loads" a real falsifier, not a
        // vacuous one.
        private const string MixedStyle = @"{
            ""version"": 8,
            ""sources"": {
                ""fillSrc"":   { ""type"": ""vector"", ""tiles"": [""https://fill/{z}/{x}/{y}.pbf""] },
                ""rasterSrc"": { ""type"": ""raster"", ""url"": ""https://raster-tilejson.example/meta.json"" }
            },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": ""#00ff00"" } },
                { ""id"": ""f"",  ""type"": ""fill"", ""source"": ""fillSrc"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": ""#ff0000"" } },
                { ""id"": ""r"",  ""type"": ""raster"", ""source"": ""rasterSrc"" }
            ]
        }";

        private static MapView NewView(out GameObject go)
        {
            go = new GameObject("MapViewSourceSpec");
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64; view.Config.MaxMeshBuildsPerTick = 64;
            return view;
        }

        /// <summary>Yields frames until the (Preserved) style task completes, then propagates its result —
        /// the loaders complete on the ThreadPool, so a real frame gives them wall-clock. Never Thread.Sleep.</summary>
        private static IEnumerator SpinToCompleted(UniTask task, int maxSpins = 20000)
        {
            var t = task.Preserve();
            int s = 0;
            while (!t.Status.IsCompleted() && s++ < maxSpins) yield return null;
            t.GetAwaiter().GetResult();
        }

        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) yield break;
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator MixedStyle_ResolvesOnlyTheFillsSource_NoRasterOrBackgroundFetch()
        {
            var view = NewView(out var go);
            int docFetches = 0;
            int factoryCalls = 0;
            var seenTemplates = new List<string>();
            view.View.DocumentLoaderOverride    = (uri, ct) => { Interlocked.Increment(ref docFetches); return UniTask.FromResult(""); };
            view.View.TileSourceFactoryOverride = template =>
            {
                Interlocked.Increment(ref factoryCalls);
                seenTemplates.Add(template);
                return TestDataSource.Absent(); // never actually fetched by this test's assertions
            };
            try
            {
                yield return SpinToCompleted(view.SetStyle(StyleParser.Parse(MixedStyle), "mixed"));
                yield return PumpUntilSettled(view);

                Assert.AreEqual(0, docFetches,
                    "no TileJSON/document load may be issued for the raster (or background) layer.");
                Assert.AreEqual(1, factoryCalls, "exactly ONE source (the fill's) may be constructed.");
                Assert.AreEqual("https://fill/{z}/{x}/{y}.pbf", seenTemplates[0]);
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }
    }
}
