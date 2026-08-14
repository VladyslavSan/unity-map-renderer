// Epic A / A2 acceptance — plan §F tooth 10 (MED 4 / HIGH b): a restyle keeps the PREVIOUS background +
// identity valid until the synchronous commit. A2 removed E3's always-on background presenter, so background
// now depends on TileManager.SetSources — if identity/layer mutation ran BEFORE the one await
// (BuildSourceSpecs), a delayed or cancelled restyle would blank the background and/or report the NEW
// identity while still rendering the OLD tiles. The fix (MapView.SetStyle, §E step 3) moves ALL mutation
// into the synchronous post-await commit. PlayMode: settle-polls yield real frames (never Thread.Sleep).

using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Map;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using static MapRenderer.Tests.SetStyleAtomicity; // shared scaffold: styles, GatedLoader, SpinTo*, AssertOldStyleIntact

namespace MapRenderer.Tests.PlayMode.MapViews
{
    [TestFixture]
    public class MapViewBackgroundRestyleTests
    {
        private static MapView NewView(out GameObject go)
        {
            go = new GameObject("MapViewBackgroundRestyle");
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
            view.WithTestMaterials();
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
        public IEnumerator DelayedRestyle_KeepsPreviousBackgroundRendered_UntilCommit()
        {
            var view = NewView(out var go);
            try
            {
                // Style A: background-only (no fetching layer, so its own SetStyle commits synchronously).
                yield return SpinToSucceeded(view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#00ff00")), "A"));
                yield return PumpUntilSettled(view);
                Assert.AreEqual("A", view.StyleId);
                Material bgMaterialA = view.Layers[0].Material;
                Assert.IsNotNull(bgMaterialA);

                // Style B: background + a url-source fill — the ONE await genuinely suspends on the gate.
                var gate = new GatedLoader();
                view.View.DocumentLoaderOverride = gate.Load;
                UniTask restyleTask = view.SetStyle(StyleParser.Parse(BackgroundPlusUrlSourceStyle), "B").Preserve();

                // Mid-resolution: OLD identity/layers/background must still be live — nothing mutated yet.
                AssertOldStyleIntact(view, bgMaterialA, because: "nothing may mutate before the commit (no blank window).");
                Assert.AreEqual(1, view.Layers.Count, "the OLD (one-layer) RenderLayerSet must be untouched.");

                // Release the gate — resolution completes, the commit runs.
                gate.Release();
                yield return SpinToSucceeded(restyleTask);
                yield return PumpUntilSettled(view);

                Assert.AreEqual("B", view.StyleId, "after commit, the NEW identity must be reported.");
                Assert.AreEqual(2, view.Layers.Count, "after commit, the NEW (two-layer) RenderLayerSet is live.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        [UnityTest]
        public IEnumerator CancelledDuringResolution_LeavesPreviousStyleIntact()
        {
            var view = NewView(out var go);
            try
            {
                yield return SpinToSucceeded(view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#00ff00")), "A"));
                yield return PumpUntilSettled(view);
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
                yield return SpinToCompleted(restyleTask);

                Assert.IsTrue(restyleTask.Status == UniTaskStatus.Canceled || restyleTask.Status == UniTaskStatus.Faulted,
                    $"a cancelled restyle's task must not succeed (status={restyleTask.Status}).");
                AssertOldStyleIntact(view, bgMaterialA, because: "a cancelled restyle must leave the OLD style intact (no destroyed-material draw).");
                Assert.AreSame(layersRef, view.Layers, "the RenderLayerSet instance itself is unchanged.");
                Assert.AreEqual(1, view.Layers.Count, "Layers.Build must NEVER have run for the cancelled style.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }

        [UnityTest]
        public IEnumerator AlreadyCancelledToken_LeavesPreviousStyleIntact()
        {
            var view = NewView(out var go);
            try
            {
                yield return SpinToSucceeded(view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#00ff00")), "A"));
                yield return PumpUntilSettled(view);
                Assert.AreEqual("A", view.StyleId);
                Material bgMaterialA = view.Layers[0].Material;

                using var cts = new CancellationTokenSource();
                cts.Cancel(); // already cancelled BEFORE SetStyle is even called
                UniTask restyleTask = view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#ff0000")), "B", cts.Token).Preserve();
                yield return SpinToCompleted(restyleTask);

                Assert.IsTrue(restyleTask.Status == UniTaskStatus.Canceled || restyleTask.Status == UniTaskStatus.Faulted,
                    $"an already-cancelled token must abort SetStyle (status={restyleTask.Status}).");
                AssertOldStyleIntact(view, bgMaterialA, because: "an already-cancelled restyle must leave the OLD style intact.");
            }
            finally { view.Teardown(); Object.DestroyImmediate(go); }
        }
    }
}
