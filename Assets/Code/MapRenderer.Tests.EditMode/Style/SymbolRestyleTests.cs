// Unity EditMode only — real Materials via MaterialFactory / MapMaterialSet, real RenderLayerSet.
// NOT included in Tools/core-tests.
//
// UMR-147 (style-transitions epic, Stage 5 — T4: labels): symbol layers survive the restyle gate and
// EASE across a restyle, instead of keeping the previous style's colour forever. T3/T4a/T4b read a
// UNIFORM, so they cannot see a gamma error (the uniform is pre-upload) — SymbolHaloColorRenderTests'
// T7 is the pixel-level counterpart. T8a is the symbol applier's zero-alloc tooth: the epic's own
// alloc teeth build over a fill-only style and so iterate zero symbol bindings.

using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Tests.Style
{
    [TestFixture]
    public class SymbolRestyleTests
    {
        private const string SingleSymbolTemplate = @"{{
    ""version"": 8,
    ""layers"": [
        {{ ""id"": ""label0"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
          ""layout"": {{ ""text-field"": ""{{NAME}}"" }},
          ""paint"": {{ ""text-color"": ""{0}"" }} }}
    ]
}}";

        private static StyleDocument SingleSymbolStyle(string textColorHex)
            => StyleParser.Parse(string.Format(SingleSymbolTemplate, textColorHex));

        // #996633 -> #2288DD: no shared channel, none at 0/1 (plan §4 colour choice).
        private const float OldR = 0x99 / 255f, OldG = 0x66 / 255f, OldB = 0x33 / 255f;
        private const float NewR = 0x22 / 255f, NewG = 0x88 / 255f, NewB = 0xDD / 255f;

        /// <summary>
        /// <b>T3.</b> A surviving symbol layer's <c>_TextColor</c> uniform must reach the NEW colour after
        /// the transition settles — before UMR-147, <c>Restyle</c> was a documented no-op and the uniform
        /// held the previous style's colour forever. RED-verify: revert <c>SymbolRenderLayer.Restyle</c> to
        /// <c>=&gt; StyleLayer = layer</c> — the uniform holds #996633 and the R assertion fires first.
        /// </summary>
        [Test]
        public void SymbolRestyle_TextColorUniform_ReachesTheNewColour()
        {
            var oldStyle = SingleSymbolStyle("#996633");
            var newStyle = SingleSymbolStyle("#2288DD");
            var set = new RenderLayerSet();
            set.Build(oldStyle, 8.0, MapMaterialSetTestUtil.Load());
            try
            {
                Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                    "drive precondition: a Constant text-color-only change must take the in-place path.");

                set.ApplyZoom(new StyleFrameInputs(8.0, 1.0, StyleTransition.Default.DurationSeconds));

                var symbolLayer = (SymbolRenderLayer)set[0];
                Color c = symbolLayer.WorldTextMaterial.GetColor(Shader.PropertyToID("_TextColor"));
                Assert.That(c.r, Is.EqualTo(NewR).Within(1e-3f), "R must settle at the new colour.");
                Assert.That(c.g, Is.EqualTo(NewG).Within(1e-3f), "G must settle at the new colour.");
                Assert.That(c.b, Is.EqualTo(NewB).Within(1e-3f), "B must settle at the new colour.");
            }
            finally { set.Dispose(); }
        }

        /// <summary>
        /// <b>T4a.</b> Mid-transition, <c>_TextColor</c> must lie STRICTLY between the two endpoints on
        /// every channel — not merely differ from the target. RED-verify: delete
        /// <c>_applier.SetTransition(...)</c> from <c>Restyle</c> — the rebind is not a retarget, so the
        /// target is pushed immediately and the mid value EQUALS the target.
        /// </summary>
        [Test]
        public void SymbolRestyle_MidEase_TextColorLiesBetweenTheEndpoints()
        {
            var oldStyle = SingleSymbolStyle("#996633");
            var newStyle = SingleSymbolStyle("#2288DD");
            var set = new RenderLayerSet();
            set.Build(oldStyle, 8.0, MapMaterialSetTestUtil.Load());
            try
            {
                Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0));

                double mid = StyleTransition.Default.DurationSeconds / 2.0;
                set.ApplyZoom(new StyleFrameInputs(8.0, 1.0, mid));

                var symbolLayer = (SymbolRenderLayer)set[0];
                Color c = symbolLayer.WorldTextMaterial.GetColor(Shader.PropertyToID("_TextColor"));

                void AssertStrictlyBetween(float value, float a, float b, string channel)
                {
                    float lo = Mathf.Min(a, b), hi = Mathf.Max(a, b);
                    Assert.Greater(value, lo, $"{channel} must be strictly above the lower endpoint mid-ease.");
                    Assert.Less(value, hi, $"{channel} must be strictly below the upper endpoint mid-ease.");
                }
                AssertStrictlyBetween(c.r, OldR, NewR, "R");
                AssertStrictlyBetween(c.g, OldG, NewG, "G");
                AssertStrictlyBetween(c.b, OldB, NewB, "B");
            }
            finally { set.Dispose(); }
        }

        /// <summary>
        /// <b>T4b.</b> <see cref="RenderLayerSet.TransitioningCount"/> is <c>&gt; 0</c> mid-ease and
        /// <c>== 0</c> once settled — a SEPARATE injection from T4a's, because T4a's defect (an immediate
        /// snap) would also red this. RED-verify: make <c>SymbolRenderLayer.TransitioningCount</c> return
        /// <c>0</c> unconditionally — the mid-ease clause fires while T4a stays green.
        /// </summary>
        [Test]
        public void SymbolRestyle_TransitioningCount_RisesThenSettles()
        {
            var oldStyle = SingleSymbolStyle("#996633");
            var newStyle = SingleSymbolStyle("#2288DD");
            var set = new RenderLayerSet();
            set.Build(oldStyle, 8.0, MapMaterialSetTestUtil.Load());
            try
            {
                Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0));

                double mid = StyleTransition.Default.DurationSeconds / 2.0;
                set.ApplyZoom(new StyleFrameInputs(8.0, 1.0, mid));
                Assert.Greater(set.TransitioningCount, 0, "mid-ease, at least one binding must still be easing.");

                set.ApplyZoom(new StyleFrameInputs(8.0, 1.0, StyleTransition.Default.DurationSeconds));
                Assert.AreEqual(0, set.TransitioningCount, "settled at exactly the transition duration.");
            }
            finally { set.Dispose(); }
        }

        // ── T8a: the symbol applier's own zero-alloc tooth ────────────────────────────────────

        private const string ThreeSymbolLayersJson = @"{
    ""version"": 8,
    ""layers"": [
        { ""id"": ""label0"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
          ""layout"": { ""text-field"": ""{NAME}"" },
          ""paint"": { ""text-color"": ""#996633"", ""text-halo-color"": ""#808080"",
                       ""text-halo-width"": 1, ""text-halo-blur"": 0.5 } },
        { ""id"": ""label1"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
          ""layout"": { ""text-field"": ""{NAME}"" },
          ""paint"": { ""text-color"": ""#336699"", ""text-halo-color"": ""#808080"",
                       ""text-halo-width"": 1, ""text-halo-blur"": 0.5 } },
        { ""id"": ""label2"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""l"",
          ""layout"": { ""text-field"": ""{NAME}"" },
          ""paint"": { ""text-color"": ""#669933"", ""text-halo-color"": ""#808080"",
                       ""text-halo-width"": 1, ""text-halo-blur"": 0.5 } }
    ]
}";

        /// <summary>
        /// <b>T8a.</b> Three symbol layers' <c>ApplyZoom</c> must not allocate GC memory. Needed because the
        /// epic's five allocation teeth build over <c>MinimalStyle()</c> — one fill layer — so their sweep
        /// iterates ZERO symbol bindings and cannot see this path. RED-verify: add a USED allocation inside
        /// <c>SymbolRenderLayer.ApplyZoom</c> (assign a <c>new float[1]</c> to a static sink — an unused
        /// local is dead-store-eliminated and never fires).
        /// </summary>
        [Test]
        public void SymbolLayers_ApplyZoom_DoesNotAllocateGCMemory()
        {
            var style = StyleParser.Parse(ThreeSymbolLayersJson);
            var set = new RenderLayerSet();
            set.Build(style, 8.0, MapMaterialSetTestUtil.Load());
            try
            {
                Assert.AreEqual(3, set.Count, "precondition: all three symbol layers must take a slot.");
                Assert.IsNotNull(((SymbolRenderLayer)set[0]).WorldTextMaterial,
                    "precondition: MapMaterialSetTestUtil must assign SymbolTextWorld — otherwise every " +
                    "ApplyZoom below is a `_applier?.` no-op and this tooth measures nothing.");

                // Warm-up: JIT compilation and shader reflection must not count against the measurement.
                for (int w = 0; w < 64; w++)
                    set.ApplyZoom(new StyleFrameInputs(8.0 + (w % 8), 1.0, 0.0));

                double zoom = 8.0;
                Assert.That(() =>
                {
                    zoom += 0.1;
                    if (zoom > 16.0) zoom = 8.0;
                    set.ApplyZoom(new StyleFrameInputs(zoom, 1.0, 0.0));
                }, Is.Not.AllocatingGCMemory(),
                    "RenderLayerSet.ApplyZoom over three symbol layers must not allocate GC memory.");
            }
            finally { set.Dispose(); }
        }
    }
}
