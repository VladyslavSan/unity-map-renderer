// Unity EditMode only — drives the REAL MapViewComponent.SetStyle path through RestyleHarness.
// NOT included in Tools/core-tests.

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// The fade gate as driven by a real <see cref="MapViewComponent"/>: the transition it uses is
    /// threaded from the view, and crossing a zoom bound rebuilds nothing.
    /// </summary>
    [TestFixture]
    public class LayerFadeViewTests
    {
        // The harness seeds the camera at z4, so a layer bounded below at 4.5 starts GATED OUT and the
        // crossing below is a real 0 -> 1 transition rather than a no-op.
        private const double BoundMin        = 4.5;
        private const double ZoomInsideBound = 4.9;
        private const float  AuthoredOpacity = 0.5f;

        private static StyleDocument BoundedFillStyle() => StyleParser.Parse($@"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""maplibre"": {{ ""type"": ""vector"", ""tiles"": [""https://example.invalid/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [ {{ ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"",
        ""minzoom"": {BoundMin},
        ""paint"": {{ ""fill-color"": [""rgba"",102,153,204,1], ""fill-opacity"": {AuthoredOpacity} }} }} ]
}}");

        private static float Opacity(MapViewComponent view)
            => view.Layers[0].Material.GetFloat(ShaderProperties.PropertyId.Opacity);

        /// <summary>
        /// <c>StyleFrameInputs.Transition</c> drives the gate, and <c>MapView</c> passes its own
        /// <c>StyleTransition</c> into it every frame. Without this tooth a hard-coded default inside
        /// <c>RenderLayerSet</c> and a genuinely threaded value are indistinguishable — every other fade
        /// tooth passes either way.
        ///
        /// <para>The asserted quantity is how many frames the gate needs to settle: at
        /// <c>Instant</c> the ease has zero duration and lands on its target in the FIRST
        /// <c>ApplyZoom</c>, which the 0.30 s default cannot do in one frame at any plausible clock.</para>
        /// </summary>
        [Test]
        public void GateTransition_IsThreadedFromMapView()
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                view.View.StyleTransition = StyleTransition.Instant;
                RestyleHarness.SpinToCompleted(view.SetStyle(BoundedFillStyle(), "A"));

                Assert.AreEqual(0f, Opacity(view), 1e-6f,
                    $"drive precondition: the camera is seeded at z4, below minzoom {BoundMin}, so the " +
                    "layer must start gated out — otherwise the crossing below is not a real transition.");

                // ONE frame across the bound.
                view.View.Camera.Apply(new CameraPropertiesUpdate { Zoom = ZoomInsideBound });
                view.LateUpdate();

                Assert.AreEqual(AuthoredOpacity, Opacity(view), 1e-6f,
                    "with MapView.StyleTransition = Instant the gate must reach its endpoint in a SINGLE " +
                    "frame. Reading 0 here means the 0.30 s default was used instead — i.e. RenderLayerSet " +
                    "hard-codes StyleTransition.Default rather than taking the value MapView threads into " +
                    "StyleFrameInputs.Transition.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// End-to-end, through the real view and the real Entities backend: a layer settled out of its zoom
        /// range submits NO draw item, and a layer mid-fade still submits one.
        ///
        /// <para>Exempt from the length limit for an ordering fact no single body shows: the draw gate is
        /// pushed beside <c>RenderLayerSet.ApplyZoom</c>, so the two halves — the fade this reads and
        /// the gate the backend applies — must agree within ONE frame. The clock is driven through
        /// <c>NowSecondsOverride</c> rather than wall time, so the mid-fade frame is a chosen instant and
        /// not a race.</para>
        /// </summary>
        [Test]
        public void GatedLayer_SubmitsNoDraw_WhileAFadingOneStillDoes()
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                double now = 0.0;
                view.View.NowSecondsOverride = () => now;
                RestyleHarness.SpinToCompleted(view.SetStyle(BoundedFillStyle(), "A"));
                RestyleHarness.PumpUntilSettled(view);

                var backend = view.EntitiesRenderer();
                Assert.IsNotNull(backend, "drive precondition: the Entities backend must be active.");
                Assert.Greater(backend.ItemCountAtSlot(0), 0,
                    "anti-vacuity: slot 0 must own at least one draw item, or 'no item is drawn' is " +
                    "trivially true of a slot that never loaded.");

                // Seeded at z4, below minzoom 4.5 — settled at fade 0 with no ease to wait for.
                Assert.AreEqual(0f, Opacity(view), 1e-6f, "drive precondition: the layer starts gated out.");
                Assert.IsFalse(backend.AllItemsDrawnAtSlot(0),
                    "a layer settled outside its zoom range must submit no draw item. Submitting it and " +
                    "discarding the fragment is the retired mechanism — it still paid for the vertex " +
                    "stage and the draw call on every frame.");

                // Cross the bound with the DEFAULT (non-instant) transition and stop halfway through it.
                double d = StyleTransition.Default.DurationSeconds;
                view.View.StyleTransition = StyleTransition.Default;
                view.View.Camera.Apply(new CameraPropertiesUpdate { Zoom = ZoomInsideBound });
                view.LateUpdate();          // arms the fade at now = 0
                now = d / 2.0;
                view.LateUpdate();          // halfway

                float midFade = Opacity(view);
                Assert.Greater(midFade, 0f,
                    "fixture: the halfway frame must read strictly above 0, or there is no fade here.");
                Assert.Less(midFade, AuthoredOpacity,
                    "fixture: the halfway frame must read strictly below the authored value.");
                Assert.IsTrue(backend.AllItemsDrawnAtSlot(0),
                    $"a layer mid-fade (_Opacity {midFade}) must still submit its draw item. A gate that " +
                    "fires on anything but a SETTLED zero removes the draw while the fade still has " +
                    "something to blend, and the layer blinks in instead of fading in.");

                now = d * 4.0;
                view.LateUpdate();
                Assert.AreEqual(AuthoredOpacity, Opacity(view), 1e-6f, "fixture: the fade must have settled.");
                Assert.IsTrue(backend.AllItemsDrawnAtSlot(0),
                    "a layer settled INSIDE its range must draw — a one-way gate would strand it.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>
        /// Crossing a layer's <c>minzoom</c> rebuilds no tile and destroys no mesh.
        ///
        /// <para><b>A DESIGN FENCE, not a tooth on new code.</b> Fade is a pure material uniform — it
        /// touches no <c>PreparedKey</c> and no mesh — so no implementation following this stage's design
        /// can fail it, and it has no single-site RED. It fails the day someone re-implements the gate the
        /// rejected way, by filtering <c>TileManager.ComputeDenseLayerIds</c> on the zoom predicate. Never
        /// read its green as evidence that the gate itself works.</para>
        /// </summary>
        [Test]
        public void ZoomCrossing_RebuildsNoTile()
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(BoundedFillStyle(), "A"));
                RestyleHarness.PumpUntilSettled(view);

                Assert.Greater(view.LoadedTileCount(), 0,
                    "anti-vacuity: the cover must be non-empty BEFORE the crossing, or 'nothing was " +
                    "rebuilt' is trivially true of a cover that never settled.");
                Assert.IsTrue(view.TryGetBuiltTile(RestyleHarness.TrackedTile),
                    "anti-vacuity: the tracked tile must be built before the crossing.");

                Mesh before      = view.GetTileMeshes(RestyleHarness.TrackedTile)[0];
                int  meshesBefore = RestyleHarness.CountMeshObjects();
                int  tilesBefore  = view.LoadedTileCount();

                // Cross the bound WITHOUT leaving the tile: zoom only, same centre, same z4 cover.
                view.View.Camera.Apply(new CameraPropertiesUpdate { Zoom = ZoomInsideBound });
                view.LateUpdate();
                RestyleHarness.PumpUntilSettled(view);

                Assert.AreEqual(tilesBefore, view.LoadedTileCount(),
                    "crossing a layer's minzoom must not change the loaded cover.");
                Assert.AreSame(before, view.GetTileMeshes(RestyleHarness.TrackedTile)[0],
                    "the tracked tile's Mesh must be the SAME object. A new instance means the tile was " +
                    "re-meshed by a zoom crossing, which is the rejected design where the gate filters " +
                    "ComputeDenseLayerIds instead of scaling a uniform.");
                Assert.AreEqual(meshesBefore, RestyleHarness.CountMeshObjects(),
                    "no Mesh may be created or destroyed by a zoom crossing.");
            }
            finally
            {
                view.Teardown();
                Object.DestroyImmediate(go);
            }
        }
    }
}
