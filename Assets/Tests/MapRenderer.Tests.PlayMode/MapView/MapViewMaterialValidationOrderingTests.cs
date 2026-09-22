// TOCTOU-safe validation ordering. MapMaterialSet's base-material fields are live-mutable, and
// MapView.SetStyle awaits BuildSourceSpecs before its commit, so validating at SetStyle ENTRY would pass
// and a mutation during the await could still let a null base reach Layers.Build. SetStyle therefore
// validates a CAPTURED MapMaterialSet reference immediately before the synchronous commit, with no await
// between validate and use. PlayMode: the settle-poll yields real frames (never Thread.Sleep).

using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using static MapRenderer.Tests.SetStyleAtomicity; // shared scaffold: styles, GatedLoader, SpinTo*, AssertOldStyleIntact

namespace MapRenderer.Tests.PlayMode.MapViews
{
    [TestFixture]
    public class MapViewMaterialValidationOrderingTests : BaseTestFixture
    {
        /// <summary>A THROWAWAY MapMaterialSet (never the shared production asset — nulling a base field
        /// here must never mutate the committed asset other tests in the same batch also load).</summary>
        private static MapMaterialSet NewThrowawaySet()
        {
            var prod = MapMaterialSetTestUtil.Load();
            var set  = ScriptableObject.CreateInstance<MapMaterialSet>();
            set.FillMaterial    = prod.FillMaterial;
            set.LineMaterial    = prod.LineMaterial;
            // SymbolTextWorld is REQUIRED — else Validate() throws on it instead of the FillMaterial null
            // this test's commit-ordering tooth targets.
            set.SymbolTextWorld = prod.SymbolTextWorld;
            return set;
        }

        private static MapView NewView(out GameObject go, MapMaterialSet materialSet)
        {
            go = new GameObject("MapViewMaterialValidationOrdering");
            var view = go.AddComponent<MapView>();
            view.enabled = false; // manual-drive only — suppress the PlayerLoop's auto-LateUpdate (double-tick)
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
        public IEnumerator DelayedRestyle_NullingFillMaterialMidResolution_ThrowsAtCommit_BeforeMutation()
        {
            var materialSet = Track(NewThrowawaySet());
            var view = NewView(out var go, materialSet);
            Track(go);
            try
            {
                // Style A commits normally (all three bases assigned).
                yield return SpinToSucceeded(view.SetStyle(StyleParser.Parse(BackgroundOnlyStyle("#00ff00")), "A"));
                yield return PumpUntilSettled(view);
                Assert.AreEqual("A", view.StyleId);
                Material bgMaterialA = view.Layers[0].Material;
                Assert.IsNotNull(bgMaterialA);

                // Style B: the ONE await genuinely suspends on the gate.
                var gate = new GatedLoader();
                view.View.DocumentLoaderOverride = gate.Load;
                UniTask restyleTask = view.SetStyle(StyleParser.Parse(BackgroundPlusUrlSourceStyle), "B").Preserve();

                // Null FillMaterial mid-resolution — AFTER BuildSourceSpecs started, BEFORE it completes. A
                // "validate at SetStyle entry" would have already passed by now; the captured-set validate
                // runs AFTER this await, on a freshly-read reference, so it must still catch this.
                materialSet.FillMaterial = null;
                gate.Release();
                yield return SpinToCompleted(restyleTask);

                // The commit must THROW at Validate() — BEFORE Layers.Build/identity are mutated.
                Exception thrown = null;
                try { restyleTask.GetAwaiter().GetResult(); }
                catch (Exception ex) { thrown = ex; }
                Assert.IsNotNull(thrown, "a null base mid-resolution must make the commit THROW.");
                Assert.IsInstanceOf<InvalidOperationException>(thrown,
                    $"must throw MapMaterialSet.Validate()'s InvalidOperationException, not something else " +
                    $"(got {thrown.GetType()}: {thrown.Message}).");
                StringAssert.Contains("FillMaterial", thrown.Message);

                AssertOldStyleIntact(view, bgMaterialA,
                    because: "a failed commit must not mutate _style/StyleId or run Layers.Build.");
                Assert.AreEqual(1, view.Layers.Count, "the OLD (one-layer) RenderLayerSet must be untouched.");
            }
            finally
            {
                view.Teardown();
            }
        }
    }
}
