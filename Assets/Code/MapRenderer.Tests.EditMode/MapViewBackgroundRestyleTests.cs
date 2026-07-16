// Epic A / A2 acceptance — plan §F tooth 10 (MED 4 / HIGH b): a restyle keeps the PREVIOUS background +
// identity valid until the synchronous commit. A2 removed E3's always-on background presenter, so background
// now depends on TileManager.SetSources — if identity/layer mutation ran BEFORE the one await
// (BuildSourceSpecs), a delayed or cancelled restyle would blank the background and/or report the NEW
// identity while still rendering the OLD tiles. The fix (MapView.SetStyle, §E step 3) moves ALL mutation
// into the synchronous post-await commit.

using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class MapViewBackgroundRestyleTests
    {
        private static string BackgroundOnlyStyle(string colorHex) => $@"{{
            ""version"": 8,
            ""layers"": [ {{ ""id"": ""bg"", ""type"": ""background"",
                             ""paint"": {{ ""background-color"": ""{colorHex}"" }} }} ]
        }}";

        // A style whose ONE fetching layer resolves via TileJSON (url-only source, no inline tiles[]) — so
        // MapView.BuildSourceSpecs' await(loader) genuinely suspends until the test releases the gate.
        private const string BackgroundPlusUrlSourceStyle = @"{
            ""version"": 8,
            ""sources"": { ""s"": { ""type"": ""vector"", ""url"": ""https://example.com/tilejson.json"" } },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": ""#ff0000"" } },
                { ""id"": ""f"",  ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": ""#0000ff"" } }
            ]
        }";

        private static MapView NewView(out GameObject go)
        {
            go = new GameObject("MapViewBackgroundRestyle");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64; view.Config.MaxMeshBuildsPerTick = 64;
            // Style B's "https://example.com/..." tile template is a PLACEHOLDER, never meant to be
            // fetched for real — without this override, PumpUntilSettled's real cover/fetch loop would hit
            // the network (via the DEFAULT TileDataSourceFactory), producing a flaky/slow test AND an
            // unobserved-exception console flood from the doomed request (mirrors MapViewSetStyleTests'
            // convention: every non-file:// test either overrides the factory or stays fully offline).
            view.View.TileSourceFactoryOverride = _ => TestDataSource.Absent();
            return view;
        }

        /// <summary>A gated document loader: <c>await</c>ing <see cref="Load"/> suspends (returns control to
        /// the caller) until <see cref="Release"/> is called — entirely on the MAIN thread throughout (a
        /// <see cref="UniTaskCompletionSource{T}"/>-backed task, never a ThreadPool hop), so
        /// <see cref="Release"/> can run the REST of MapView.SetStyle's synchronous commit (which touches
        /// Unity APIs — Object.DestroySafely et al. — main-thread only) inline, exactly like production's
        /// UnityWebRequest-backed loader resuming on the main thread. Lets the test observe SetStyle's
        /// mid-resolution state deterministically, without a real network.</summary>
        private sealed class GatedLoader
        {
            private readonly UniTaskCompletionSource<string> _tcs = new UniTaskCompletionSource<string>();
            public string TileJsonText = @"{ ""tilejson"":""3.0.0"", ""tiles"":[""https://example.com/{z}/{x}/{y}.pbf""], ""minzoom"":0, ""maxzoom"":0 }";
            public int CallCount;

            public UniTask<string> Load(string uri, CancellationToken ct)
            {
                Interlocked.Increment(ref CallCount);
                return _tcs.Task;
            }

            public void Release() => _tcs.TrySetResult(TileJsonText);
        }

        /// <summary>Spins to completion WITHOUT observing the result — used where the caller expects (and
        /// separately asserts on) a fault/cancel.</summary>
        private static void SpinToCompleted(UniTask task, int maxSpins = 20000)
        {
            var t = task.Preserve();
            int s = 0;
            while (!t.Status.IsCompleted() && s++ < maxSpins) Thread.Sleep(1);
        }

        /// <summary>Spins to completion and RE-THROWS on fault/cancel — used where the caller expects
        /// success, so a regression surfaces as the real exception instead of a silently-stale assertion.</summary>
        private static void SpinToSucceeded(UniTask task, int maxSpins = 20000)
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

        [Test]
        public void DelayedRestyle_KeepsPreviousBackgroundRendered_UntilCommit()
        {
            var view = NewView(out var go);
            try
            {
                // Style A: background-only (no fetching layer, so its own SetStyle commits synchronously).
                SpinToSucceeded(view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#00ff00")), "A"));
                PumpUntilSettled(view);
                Assert.AreEqual("A", view.StyleId);
                Material bgMaterialA = view.Layers[0].Material;
                Assert.IsNotNull(bgMaterialA);

                // Style B: background + a url-source fill — the ONE await genuinely suspends on the gate.
                var gate = new GatedLoader();
                view.View.DocumentLoaderOverride = gate.Load;
                UniTask restyleTask = view.SetStyle(StyleParser.Parse(BackgroundPlusUrlSourceStyle), "B").Preserve();

                // Mid-resolution: OLD identity/layers/background must still be live — nothing mutated yet.
                Assert.AreEqual("A", view.StyleId, "identity must not change before the commit.");
                Assert.AreSame(bgMaterialA, view.Layers[0].Material,
                    "the OLD background layer/material must still be live during resolution (no blank window).");
                Assert.AreEqual(1, view.Layers.Count, "the OLD (one-layer) RenderLayerSet must be untouched.");

                // Release the gate — resolution completes, the commit runs.
                gate.Release();
                SpinToSucceeded(restyleTask);
                PumpUntilSettled(view);

                Assert.AreEqual("B", view.StyleId, "after commit, the NEW identity must be reported.");
                Assert.AreEqual(2, view.Layers.Count, "after commit, the NEW (two-layer) RenderLayerSet is live.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        [Test]
        public void CancelledDuringResolution_LeavesPreviousStyleIntact()
        {
            var view = NewView(out var go);
            try
            {
                SpinToSucceeded(view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#00ff00")), "A"));
                PumpUntilSettled(view);
                Assert.AreEqual("A", view.StyleId);
                var layersRef = view.Layers; // same instance across restyle (rebuilt in place)
                Material bgMaterialA = view.Layers[0].Material;

                var gate = new GatedLoader();
                view.View.DocumentLoaderOverride = gate.Load;
                using var cts = new CancellationTokenSource();
                UniTask restyleTask = view.SetStyle(StyleParser.Parse(BackgroundPlusUrlSourceStyle), "B", cts.Token).Preserve();

                // Cancel WHILE still suspended in resolution — nothing has mutated yet.
                cts.Cancel();
                Assert.AreEqual("A", view.StyleId, "cancel mid-resolution must not have touched identity yet.");

                gate.Release(); // let the (now-doomed) resolution finish so the task can observe the token
                SpinToCompleted(restyleTask);

                Assert.IsTrue(restyleTask.Status == UniTaskStatus.Canceled || restyleTask.Status == UniTaskStatus.Faulted,
                    $"a cancelled restyle's task must not succeed (status={restyleTask.Status}).");
                Assert.AreEqual("A", view.StyleId, "a cancelled restyle must leave the OLD identity intact.");
                Assert.AreSame(bgMaterialA, view.Layers[0].Material,
                    "a cancelled restyle must leave the OLD background layer/material intact (no destroyed-material draw).");
                Assert.AreSame(layersRef, view.Layers, "the RenderLayerSet instance itself is unchanged.");
                Assert.AreEqual(1, view.Layers.Count, "Layers.Build must NEVER have run for the cancelled style.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        [Test]
        public void AlreadyCancelledToken_LeavesPreviousStyleIntact()
        {
            var view = NewView(out var go);
            try
            {
                SpinToSucceeded(view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#00ff00")), "A"));
                PumpUntilSettled(view);
                Assert.AreEqual("A", view.StyleId);
                Material bgMaterialA = view.Layers[0].Material;

                using var cts = new CancellationTokenSource();
                cts.Cancel(); // already cancelled BEFORE SetStyle is even called
                UniTask restyleTask = view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#ff0000")), "B", cts.Token).Preserve();
                SpinToCompleted(restyleTask);

                Assert.IsTrue(restyleTask.Status == UniTaskStatus.Canceled || restyleTask.Status == UniTaskStatus.Faulted,
                    $"an already-cancelled token must abort SetStyle (status={restyleTask.Status}).");
                Assert.AreEqual("A", view.StyleId, "an already-cancelled restyle must leave the OLD identity intact.");
                Assert.AreSame(bgMaterialA, view.Layers[0].Material, "the OLD background layer/material must be intact.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }
    }
}
