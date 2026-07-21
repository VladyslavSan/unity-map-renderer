// Epic A / A2 acceptance — plan §F tooth 14 (round-4 HIGH, TOCTOU-safe validation ordering): MapMaterialSet's
// base-material fields are live-mutable, and MapView.SetStyle awaits BuildSourceSpecs before its commit —
// validating at SetStyle ENTRY (round-3 placement) would pass, then a concurrent mutation during the await
// could still let a null base reach Layers.Build (the exact crash/leak DECISION 2 exists to prevent). The
// fix validates a CAPTURED MapMaterialSet reference immediately before the synchronous commit, with no await
// between validate and use.

using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class MapViewMaterialValidationOrderingTests
    {
        private static string BackgroundOnlyStyle(string colorHex) => $@"{{
            ""version"": 8,
            ""layers"": [ {{ ""id"": ""bg"", ""type"": ""background"",
                             ""paint"": {{ ""background-color"": ""{colorHex}"" }} }} ]
        }}";

        private const string BackgroundPlusUrlSourceStyle = @"{
            ""version"": 8,
            ""sources"": { ""s"": { ""type"": ""vector"", ""url"": ""https://example.com/tilejson.json"" } },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": ""#ff0000"" } },
                { ""id"": ""f"",  ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""countries"",
                  ""paint"": { ""fill-color"": ""#0000ff"" } }
            ]
        }";

        /// <summary>A THROWAWAY MapMaterialSet (never the shared production asset — nulling a base field
        /// here must never mutate the committed asset other tests in the same batch also load).</summary>
        private static MapMaterialSet NewThrowawaySet()
        {
            var prod = MapMaterialSetTestUtil.Load();
            var set  = ScriptableObject.CreateInstance<MapMaterialSet>();
            set.FillMaterial    = prod.FillMaterial;
            set.LineMaterial    = prod.LineMaterial;
            // Epic A / A1: SymbolTextWorld is REQUIRED (Codex #2) — else Validate() throws on it instead
            // of the FillMaterial null this test's commit-ordering tooth actually targets.
            set.SymbolTextWorld = prod.SymbolTextWorld;
            return set;
        }

        private static MapView NewView(out GameObject go, MapMaterialSet materialSet)
        {
            go = new GameObject("MapViewMaterialValidationOrdering");
            var view = go.AddComponent<MapView>();
            view.Config.MaterialSet = materialSet;
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64; view.Config.MaxMeshBuildsPerTick = 64;
            // Defensive (mirrors MapViewBackgroundRestyleTests): the "https://example.com/..." template is a
            // placeholder never meant to hit the network, even though this test's commit is expected to
            // throw BEFORE SetSources would ever construct a real source from it.
            view.View.TileSourceFactoryOverride = _ => TestDataSource.Absent();
            return view;
        }

        /// <summary>A <see cref="UniTaskCompletionSource{T}"/>-backed gated loader: <c>await</c>ing
        /// <see cref="Load"/> suspends entirely on the MAIN thread (no ThreadPool hop), so
        /// <see cref="Release"/> runs the REST of MapView.SetStyle's synchronous commit inline (Unity APIs
        /// are main-thread only) — mirrors production's UnityWebRequest-backed loader resuming on the main
        /// thread, unlike a ThreadPool-hopping stub.</summary>
        private sealed class GatedLoader
        {
            private readonly UniTaskCompletionSource<string> _tcs = new UniTaskCompletionSource<string>();
            public string TileJsonText = @"{ ""tilejson"":""3.0.0"", ""tiles"":[""https://example.com/{z}/{x}/{y}.pbf""], ""minzoom"":0, ""maxzoom"":0 }";

            public UniTask<string> Load(string uri, CancellationToken ct) => _tcs.Task;

            public void Release() => _tcs.TrySetResult(TileJsonText);
        }

        /// <summary>Spins to completion WITHOUT observing the result — used where the caller expects (and
        /// separately asserts on) a fault.</summary>
        private static void SpinToCompleted(UniTask task, int maxSpins = 20000)
        {
            var t = task.Preserve();
            int s = 0;
            while (!t.Status.IsCompleted() && s++ < maxSpins) Thread.Sleep(1);
        }

        /// <summary>Spins to completion and RE-THROWS on fault — used where the caller expects success.</summary>
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
        public void DelayedRestyle_NullingFillMaterialMidResolution_ThrowsAtCommit_BeforeMutation()
        {
            var materialSet = NewThrowawaySet();
            var view = NewView(out var go, materialSet);
            try
            {
                // Style A commits normally (all three bases assigned).
                SpinToSucceeded(view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#00ff00")), "A"));
                PumpUntilSettled(view);
                Assert.AreEqual("A", view.StyleId);
                Material bgMaterialA = view.Layers[0].Material;
                Assert.IsNotNull(bgMaterialA);

                // Style B: the ONE await genuinely suspends on the gate.
                var gate = new GatedLoader();
                view.View.DocumentLoaderOverride = gate.Load;
                UniTask restyleTask = view.SetStyle(StyleParser.Parse(BackgroundPlusUrlSourceStyle), "B").Preserve();

                // Null FillMaterial mid-resolution — AFTER BuildSourceSpecs started, BEFORE it completes.
                // round-3's "validate at SetStyle entry" would have already passed by this point; the
                // captured-set validate (round-4 fix) runs AFTER this await, on a freshly-read reference, so
                // it must still catch this.
                materialSet.FillMaterial = null;
                gate.Release();
                SpinToCompleted(restyleTask);

                // The commit must THROW at Validate() — BEFORE Layers.Build/identity are mutated.
                Exception thrown = null;
                try { restyleTask.GetAwaiter().GetResult(); }
                catch (Exception ex) { thrown = ex; }
                Assert.IsNotNull(thrown, "a null base mid-resolution must make the commit THROW.");
                Assert.IsInstanceOf<InvalidOperationException>(thrown,
                    $"must throw MapMaterialSet.Validate()'s InvalidOperationException, not something else " +
                    $"(got {thrown.GetType()}: {thrown.Message}).");
                StringAssert.Contains("FillMaterial", thrown.Message);

                Assert.AreEqual("A", view.StyleId,
                    "identity must be UNCHANGED by a failed commit — the throw happens before _style/StyleId mutate.");
                Assert.AreSame(bgMaterialA, view.Layers[0].Material,
                    "the OLD layers/materials must be UNCHANGED — Layers.Build must never have run.");
                Assert.AreEqual(1, view.Layers.Count, "the OLD (one-layer) RenderLayerSet must be untouched.");
            }
            finally
            {
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(materialSet);
            }
        }
    }
}
