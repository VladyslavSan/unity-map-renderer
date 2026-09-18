// Unity EditMode: builds real RenderLayerSets and reads the pushed _Opacity back off the Material.
// NOT included in Tools/core-tests/core-tests.csproj (needs UnityEngine.Material + the material set).

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Style;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// The per-layer fade gate: a layer draws inside its <c>[minzoom, maxzoom)</c> and is gated out
    /// elsewhere, the gate eases rather than snapping, and fade 1 is the identity.
    ///
    /// <para>Reads the quantity itself — <c>_Opacity</c> off the layer's Material — which nothing in these
    /// tests writes. The C# half of the gate; the RENDERED half is
    /// <c>FillExtrusionDrawGateTests</c> and they join at <c>PaintsSomething</c>.</para>
    /// </summary>
    [TestFixture]
    public class LayerFadeGateTests
    {
        private const double UnboundedZoom = 3.0;

        /// <summary>Authored below 1 on purpose: at authored 1 the product authored x fade is
        /// invisible, because 1 * f(p) == f(p) for every candidate f.</summary>
        private const float AuthoredOpacity = 0.5f;

        // Bounds are declared once and every asserted zoom is derived from them, so no literal sits next to
        // a boundary where an off-by-one-zoom-level fix would still read correct.
        private const double BoundedMin = 13.0;
        private const double BoundedMax = 14.0;
        private const double LowerOnly  = 5.0;

        private static StyleDocument Style(params string[] layers)
            => StyleParser.Parse($@"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [ {string.Join(",", layers)} ]
}}");

        /// <summary>"viewport" as <c>fill-translate-anchor</c> encodes it — 1, the value "map" (0) cannot
        /// separate from any scaling of itself.</summary>
        private const float ViewportAnchor = 1f;

        private static string FillLayer(string id, string extra = "", string extraPaint = "")
            => $@"{{ ""id"": ""{id}"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""{id}"", {extra}
        ""paint"": {{ ""fill-color"": [""rgba"",102,153,204,1], ""fill-opacity"": {AuthoredOpacity}{extraPaint} }} }}";

        private static string ExtrusionLayer(string id, string extra = "")
            => $@"{{ ""id"": ""{id}"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""{id}"", {extra}
        ""paint"": {{ ""fill-extrusion-color"": [""rgba"",204,102,51,1],
                      ""fill-extrusion-opacity"": {AuthoredOpacity} }} }}";

        private static float OpacityOf(RenderLayerSet set, int i)
            => set[i].Material.GetFloat(ShaderProperties.PropertyId.Opacity);

        private static float AnchorOf(RenderLayerSet set, int i)
            => set[i].Material.GetFloat(ShaderProperties.Fill.PropertyId.FillTranslateAnchor);

        /// <summary>Drive one frame and settle any ease by pumping the clock well past the duration.</summary>
        private static void ApplySettled(RenderLayerSet set, double zoom)
        {
            set.ApplyZoom(new StyleFrameInputs(zoom, 1.0, 0.0));
            set.ApplyZoom(new StyleFrameInputs(zoom, 1.0, StyleTransition.Default.DurationSeconds * 4.0));
        }

        /// <summary>
        /// A layer's pushed <c>_Opacity</c> is its authored value inside <c>[minzoom, maxzoom)</c> and 0
        /// outside; an unbounded layer keeps its authored value at every zoom.
        ///
        /// <para>The <c>maxzoom</c> row is the discriminating one: <c>maxzoom</c> is EXCLUSIVE, so a fix
        /// that copies <c>SourceRegistry.AdmitsZoom</c>'s inclusive upper bound — a different quantity — is
        /// wrong by one zoom level and visible only there.</para>
        /// </summary>
        [Test]
        public void BoundedLayer_ReadsZeroOpacityOutsideItsZoomRange()
        {
            var set = new RenderLayerSet();
            set.Build(Style(
                    FillLayer("unbounded"),
                    FillLayer("lower", $@"""minzoom"": {LowerOnly},"),
                    FillLayer("both", $@"""minzoom"": {BoundedMin}, ""maxzoom"": {BoundedMax},")),
                UnboundedZoom, MapMaterialSetTestUtil.Load());

            const int unbounded = 0, lower = 1, both = 2;
            double insideBoth = (BoundedMin + BoundedMax) / 2.0;

            ApplySettled(set, UnboundedZoom);                       // below every bound
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, unbounded), 1e-6f,
                "an unbounded layer draws at every zoom — a gate that fires unconditionally reds here.");
            Assert.AreEqual(0f, OpacityOf(set, lower), 1e-6f, $"z{UnboundedZoom} is below minzoom {LowerOnly}.");
            Assert.AreEqual(0f, OpacityOf(set, both), 1e-6f, $"z{UnboundedZoom} is below minzoom {BoundedMin}.");

            ApplySettled(set, LowerOnly + 1.0);                     // inside `lower`, below `both`
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, lower), 1e-6f,
                $"z{LowerOnly + 1.0} is at or above minzoom {LowerOnly}, so the layer draws its authored value.");

            ApplySettled(set, insideBoth);                          // strictly inside [min, max)
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, both), 1e-6f,
                $"z{insideBoth} is inside [{BoundedMin}, {BoundedMax}).");

            ApplySettled(set, BoundedMax);                          // EXACTLY maxzoom — exclusive
            Assert.AreEqual(0f, OpacityOf(set, both), 1e-6f,
                $"maxzoom is EXCLUSIVE, so a layer with maxzoom {BoundedMax} must be gated out AT z{BoundedMax}. "
                + "Reading its authored value here means the bound was implemented as inclusive — the shape "
                + "SourceRegistry.AdmitsZoom uses for a DIFFERENT quantity (a source's data range).");
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, unbounded), 1e-6f,
                "the unbounded layer is unaffected at every zoom asserted above.");
        }

        /// <summary>
        /// A hidden layer is never BUILT, whatever zoom the set is built at — including one strictly inside
        /// its declared range, the discriminating value an implementation that consults only the zoom
        /// bounds passes everywhere else.
        /// </summary>
        [Test]
        public void HiddenLayer_IsNeverBuilt_AtAnyZoomIncludingInsideItsRange()
        {
            foreach (double buildZoom in new[] { 4.0, 10.0, 21.0 })
            {
                var set = new RenderLayerSet();
                set.Build(Style(
                        FillLayer("sibling"),
                        FillLayer("hidden",
                            @"""minzoom"": 5, ""maxzoom"": 20, ""layout"": {""visibility"": ""none""},")),
                    buildZoom, MapMaterialSetTestUtil.Load());

                // The sibling is what makes the count discriminating: 1 vs 2, not 0 vs 1.
                Assert.AreEqual(1, set.Count,
                    $"built at z{buildZoom}, only the sibling may take a slot — a visibility:none layer "
                    + "owns no slot, no material and no backend registration. z10 is INSIDE [5,20) and is "
                    + "the row that separates the visibility flag from the zoom bounds: a build that "
                    + "consults only the bounds keeps the hidden layer there.");
                Assert.AreEqual("sibling", set[0].StyleLayer.Id);
            }
        }

        /// <summary>
        /// Crossing a bound moves <c>_Opacity</c> continuously and recovers the authored value on the way
        /// back in. The only observer of <c>authored x fade</c> at <c>0 &lt; p &lt; 1</c>, where the
        /// composition is distinguishable from <c>authored x p^2</c> and friends.
        /// </summary>
        [Test]
        public void BoundaryCrossing_EasesTheOpacity_OutAndBackIn()
        {
            var set = new RenderLayerSet();
            set.Build(Style(FillLayer("bounded", $@"""minzoom"": {LowerOnly},")),
                LowerOnly + 1.0, MapMaterialSetTestUtil.Load());

            double d = StyleTransition.Default.DurationSeconds;
            double inside = LowerOnly + 1.0, outside = LowerOnly - 1.0;

            set.ApplyZoom(new StyleFrameInputs(inside, 1.0, 0.0));
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, 0), 1e-6f, "precondition: settled and in range.");

            // ── OUT leg: monotone decreasing, strictly interior, exact at the midpoint ──
            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, 0.0));   // arm the fade at t = 0
            float prev = OpacityOf(set, 0);
            bool sawInterior = false;
            for (int step = 1; step <= 4; step++)
            {
                set.ApplyZoom(new StyleFrameInputs(outside, 1.0, d * step / 5.0));
                float now = OpacityOf(set, 0);
                Assert.LessOrEqual(now, prev + 1e-6f, "the OUT leg must not increase.");
                if (now > 1e-6f && now < AuthoredOpacity - 1e-6f) sawInterior = true;
                prev = now;
            }
            Assert.IsTrue(sawInterior,
                "no intermediate frame landed strictly between 0 and the authored value — the gate SNAPPED "
                + "instead of easing.");

            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, d * 4.0));
            Assert.AreEqual(0f, OpacityOf(set, 0), 1e-6f, "the OUT leg must settle exactly on 0.");

            // ── Return leg: the midpoint pins the COMPOSITION, not just betweenness ──
            double armed = d * 4.0;
            set.ApplyZoom(new StyleFrameInputs(inside, 1.0, armed));
            set.ApplyZoom(new StyleFrameInputs(inside, 1.0, armed + d / 2.0));
            Assert.AreEqual(AuthoredOpacity * 0.5f, OpacityOf(set, 0), 1e-5f,
                "at half the duration the read must be authored x 0.5 = 0.25. smoothstep(0,1,0.5) is exactly "
                + "0.5, so this pins that fade appears EXACTLY ONCE in the product: a squared or "
                + "otherwise compounded fade reads 0.125 here while still passing every betweenness and "
                + "endpoint clause.");

            set.ApplyZoom(new StyleFrameInputs(inside, 1.0, armed + d * 4.0));
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, 0), 1e-6f,
                "coming back into range must recover the AUTHORED value exactly, not an eased approximation.");
        }

        /// <summary>
        /// A layer mid-fade still paints something. <c>PaintsSomething</c> — the predicate the
        /// per-slot draw gate reads (<c>ITileRenderBackend.SetLayerVisible</c>) — turns false only once the
        /// product of fade and authored opacity drops below one 8-bit step. A gate that fired when the
        /// fade STARTED would retire the draw while there was still something to blend.
        /// </summary>
        [Test]
        public void FadingLayer_IsNotAbsentUntilTheFadeSettles()
        {
            var set = new RenderLayerSet();
            set.Build(Style(FillLayer("bounded", $@"""minzoom"": {LowerOnly},")),
                LowerOnly + 1.0, MapMaterialSetTestUtil.Load());

            double d = StyleTransition.Default.DurationSeconds;
            double inside = LowerOnly + 1.0, outside = LowerOnly - 1.0;
            var fadeable = (IFadeableRenderLayer)set[0];

            set.ApplyZoom(new StyleFrameInputs(inside, 1.0, 0.0));
            Assert.IsTrue(fadeable.PaintsSomething,
                "precondition: a layer inside its range, at a showable opacity, must be drawn.");

            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, 0.0));     // arm the fade OUT
            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, d / 2.0)); // halfway through it

            float midFade = OpacityOf(set, 0);
            Assert.Greater(midFade, 0f,
                "fixture: the halfway frame must read strictly above 0, or there is no fade to measure.");
            Assert.Less(midFade, AuthoredOpacity,
                "fixture: the halfway frame must read strictly below the authored value, or the gate " +
                "snapped and 'mid-fade' is not the state under test.");
            Assert.IsTrue(fadeable.PaintsSomething,
                $"a layer at fade strictly between 0 and 1 (_Opacity {midFade}) must still be DRAWN. " +
                "Reading false here gates the draw out mid-transition, so the fade has nothing to blend and " +
                "the layer disappears the instant it leaves its zoom range.");

            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, d * 4.0));
            Assert.AreEqual(0f, OpacityOf(set, 0), 1e-6f, "fixture: the fade must have settled on 0.");
            Assert.IsFalse(fadeable.PaintsSomething,
                "once the fade has settled on 0 the layer must be gated out — otherwise the draw gate " +
                "never fires and every always-off layer keeps paying for its vertex stage and draw call.");
        }

        /// <summary>
        /// The gate reads fade TIMES the authored opacity, so a zoom-interpolated opacity that ramps
        /// through zero gates the layer out at the zooms where it shows nothing — with no zoom bounds
        /// anywhere, which is what makes the authored term the only thing that can have moved the predicate.
        /// </summary>
        [Test]
        public void ZoomInterpolatedOpacity_GatesTheLayerWhereItPaintsNothing()
        {
            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(@"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [ { ""id"": ""ramp"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""ramp"",
        ""paint"": { ""fill-color"": [""rgba"",102,153,204,1],
                     ""fill-opacity"": [""interpolate"",[""linear""],[""zoom""],5,0,15,1] } } ]
}"), 15.0, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(1, set.Count,
                "fixture: a ZOOM-dependent opacity must still be BUILT — only a CONSTANT transparent one " +
                "is refused at construction, and a refused layer would make the reads below vacuous.");
            var fadeable = (IFadeableRenderLayer)set[0];

            ApplySettled(set, 15.0);
            Assert.IsTrue(fadeable.PaintsSomething,
                $"at z15 the ramp reads its full authored opacity ({OpacityOf(set, 0)}), so the layer must " +
                "be drawn.");

            ApplySettled(set, 5.0);
            Assert.AreEqual(0f, OpacityOf(set, 0), 1e-6f,
                "fixture: the ramp must reach 0 at z5, or there is nothing for the gate to catch.");
            Assert.IsFalse(fadeable.PaintsSomething,
                "at z5 the layer paints nothing an 8-bit framebuffer can show, so its draw must not be " +
                "submitted. Reading true here means the gate looks at fade alone — the layer is " +
                "inside its (unbounded) zoom range, so fade is 1 and only the authored term differs.");
        }

        /// <summary>
        /// A feature-dependent opacity can never gate the layer out. No per-layer scalar represents it, so
        /// <c>MaterialFactory</c> binds a constant 1 and the layer stays submitted whatever its features
        /// carry. Without that the gate would read an unevaluable property and could retire a layer whose
        /// features are fully opaque.
        /// </summary>
        [Test]
        public void FeatureDependentOpacity_NeverGatesTheLayerOut()
        {
            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(@"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [ { ""id"": ""dd"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""dd"",
        ""paint"": { ""fill-color"": [""rgba"",102,153,204,1],
                     ""fill-opacity"": [""get"", ""op""] } } ]
}"), UnboundedZoom, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(1, set.Count,
                "fixture: a data-driven opacity must still be BUILT — the per-feature values are baked " +
                "into vertex alpha, and a refused layer would make the read below vacuous.");

            ApplySettled(set, UnboundedZoom);
            Assert.IsTrue(((IFadeableRenderLayer)set[0]).PaintsSomething,
                "a layer whose opacity varies PER FEATURE must always be submitted: one per-layer scalar " +
                "cannot stand for it, so the gate has to fail safe. Reading false here retires a whole " +
                "layer on a value that does not describe it.");
        }

        /// <summary>
        /// A layer built at a zoom where it is out of range reads 0 on its FIRST frame — it does not ease
        /// down from the authored value, which would flash the layer for the whole transition on load.
        /// </summary>
        [Test]
        public void OutOfRangeLayer_StartsHidden_NoFirstFrameFlash()
        {
            var set = new RenderLayerSet();
            set.Build(Style(FillLayer("in-range"), FillLayer("out-of-range", $@"""minzoom"": {BoundedMin},")),
                UnboundedZoom, MapMaterialSetTestUtil.Load());

            // Read BEFORE any ApplyZoom pumps the clock — this is the first-frame state.
            Assert.AreEqual(0f, OpacityOf(set, 1), 1e-6f,
                $"a layer with minzoom {BoundedMin} built at z{UnboundedZoom} must be seeded hidden. Reading "
                + "its authored value here means it starts visible and fades out — a flash on every load.");
            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, 0), 1e-6f,
                "its in-range twin, built in the same set, carries the authored value — so the row above is "
                + "a real gate and not a material that was never written.");
        }

        /// <summary>
        /// At fade 1 the authored value reaches the material untouched. Pins that fade is the
        /// IDENTITY at 1 — it does not separate authored x p from authored x p^2, which are the same number
        /// there; that is <see cref="BoundaryCrossing_EasesTheOpacity_OutAndBackIn"/>'s midpoint clause.
        /// </summary>
        [Test]
        public void FadeOne_LeavesTheAuthoredOpacityByteIdentical()
        {
            var set = new RenderLayerSet();
            set.Build(Style(FillLayer("unbounded")), UnboundedZoom, MapMaterialSetTestUtil.Load());
            ApplySettled(set, UnboundedZoom);

            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, 0), 1e-6f,
                $"an unbounded layer authoring fill-opacity {AuthoredOpacity} must push exactly that. This is "
                + "the only opacity row in the suite with a non-1 authored value, so a defect that is the "
                + "identity at 1 but not elsewhere — a clobbered LastPushed, a hard-coded 1, a fade that "
                + "is not the identity — is invisible everywhere else.");
        }

        /// <summary>
        /// Fill-extrusion gates INSTANTLY: ONE frame outside the range reads 0, with the gate transition
        /// left at production's default.
        ///
        /// <para>The only observer of <c>FillExtrusionRenderLayer.SetFade</c> substituting
        /// <c>StyleTransition.Instant</c> for the transition it is handed. Pass the argument through and
        /// this frame lands at <c>elapsed == 0</c>, where the ease returns false and the authored value
        /// survives — so it reads 0.5. That is not a cosmetic difference: the elevated contract blends
        /// One/Zero, so alpha is DISCARDED and an intermediate fade renders a fully solid building
        /// for the whole fade.</para>
        /// </summary>
        [Test]
        public void FillExtrusionLayer_GatesInstantly_NotOverTheTransition()
        {
            var set = new RenderLayerSet();
            set.Build(Style(ExtrusionLayer("ext", $@"""minzoom"": {BoundedMin}, ""maxzoom"": {BoundedMax},")),
                (BoundedMin + BoundedMax) / 2.0, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(AuthoredOpacity, OpacityOf(set, 0), 1e-6f,
                "precondition: built inside [min, max), so the authored opacity is on the material and the "
                + "layer was not skipped for a missing fill-extrusion base.");

            set.ApplyZoom(new StyleFrameInputs(BoundedMax, 1.0, 0.0));   // ONE frame, outside the range

            Assert.AreEqual(0f, OpacityOf(set, 0), 1e-6f,
                $"a fill-extrusion layer leaving [{BoundedMin}, {BoundedMax}) must reach 0 on the FIRST "
                + "frame. Reading the authored value means the gate is easing this kind — which paints a "
                + "solid building for the transition, because One/Zero blending discards alpha.");
        }

        /// <summary>
        /// A layer mid-fade scales ONLY its opacity. <c>fill-translate-anchor</c> — a second float binding
        /// on the same material — must still read its authored <c>viewport</c> while fade sits at 0.5.
        ///
        /// <para>The only observer of the applier's <c>ScaledByFade</c> flag. Without it every settled float
        /// on a fading layer is re-pushed multiplied by fade: the anchor drifts from viewport toward map
        /// for the length of the fade, and a building's height shrinks toward the ground with it. The
        /// device-pixel tests cannot see this — <c>_Width</c> and its family live in a separate binding list
        /// whose push loop carries no fade term at all.</para>
        /// </summary>
        [Test]
        public void FadingLayer_ScalesOnlyItsOpacity_NotItsOtherFloats()
        {
            var set = new RenderLayerSet();
            set.Build(Style(FillLayer("bounded", $@"""minzoom"": {LowerOnly},",
                    @", ""fill-translate-anchor"": ""viewport""")),
                LowerOnly + 1.0, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(ViewportAnchor, AnchorOf(set, 0), 1e-6f,
                "precondition: the authored anchor reached the material at bind time.");

            double d = StyleTransition.Default.DurationSeconds, outside = LowerOnly - 1.0;
            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, 0.0));        // arm the fade at t = 0
            set.ApplyZoom(new StyleFrameInputs(outside, 1.0, d / 2.0));    // half way through it

            Assert.AreEqual(AuthoredOpacity * 0.5f, OpacityOf(set, 0), 1e-5f,
                "precondition: fade must be strictly interior on this frame, or the clause below is "
                + "vacuous — it would be asserting the anchor against a fade of exactly 1.");

            Assert.AreEqual(ViewportAnchor, AnchorOf(set, 0), 1e-6f,
                "fill-translate-anchor is not an opacity, so the fade must leave it alone. Reading "
                + $"{ViewportAnchor * 0.5f} here means fade multiplies every float the layer pushes.");
        }
    }
}
