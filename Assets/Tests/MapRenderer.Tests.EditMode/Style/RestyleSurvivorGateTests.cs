// Unity EditMode only — real Materials via MaterialFactory / MapMaterialSet, real RenderLayerSet.
// NOT included in Tools/core-tests.
//
// Style-transitions epic, Stage 2. The gate teeth from the stage plan's §3 — 10, 11, 16, 17.

using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Tests.Style
{
    [TestFixture]
    public class RestyleSurvivorGateTests
    {
        private const string FillTemplate = @"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [
        {{ ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"", {0}
          ""paint"": {{ {1} }} }}
    ]
}}";

        private static StyleDocument FillStyle(string paintJson, string extraLayerJson = "")
            => StyleParser.Parse(string.Format(FillTemplate, extraLayerJson, paintJson));

        // ── The draw-gate keys are freely transitionable ─────────────────────────────────────

        // A layer with NO paint member at all — the shape that takes Signature's FIRST early return, and
        // therefore the only shape that can see whether the draw-gate reduction runs before it.
        private static StyleDocument NoPaintStyle(string extraLayerJson) => StyleParser.Parse($@"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [ {{ ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0""{extraLayerJson} }} ]
}}");

        /// <summary>
        /// A restyle differing only in <c>minzoom</c>/<c>maxzoom</c>/<c>layout.visibility</c> survives in
        /// place, INCLUDING for a layer with no <c>paint</c> block — which takes an early return in
        /// <c>Signature</c> and is skipped entirely if the reduction is written after it. In
        /// <c>liberty.json</c> that is 7 layers, 6 of them bounded, so the wrong placement misses the
        /// majority of the affected set.
        /// </summary>
        /// <param name="oldExtra">The old layer's members after its id/type/source.</param>
        /// <param name="newExtra">The new layer's members, differing only as the case describes.</param>
        [TestCase(@", ""minzoom"": 5", @", ""minzoom"": 7", true,
                  TestName = "a_MinzoomOnly_NoPaint_Survives")]
        [TestCase(@", ""maxzoom"": 12", @", ""maxzoom"": 14", true,
                  TestName = "a2_MaxzoomOnly_NoPaint_Survives")]
        [TestCase(@", ""minzoom"": 5, ""filter"": [""=="", ""class"", ""a""]",
                  @", ""minzoom"": 7, ""filter"": [""=="", ""class"", ""b""]", false,
                  TestName = "c_MinzoomPlusFilter_NoPaint_Refuses")]
        [TestCase(@", ""maxzoom"": 12, ""filter"": [""=="", ""class"", ""a""]",
                  @", ""maxzoom"": 14, ""filter"": [""=="", ""class"", ""b""]", false,
                  TestName = "c2_MaxzoomPlusFilter_NoPaint_Refuses")]
        [TestCase(@"", @", ""layout"": {""visibility"": ""none""}", true,
                  TestName = "d_LayoutAppearsCarryingOnlyVisibility_Survives")]
        [TestCase(@"", @", ""layout"": {""line-cap"": ""round""}", false,
                  TestName = "e_LayoutAppearsCarryingAnotherKey_Refuses")]
        public void DrawGateRestyle_SurvivesTheGate_EvenWithNoPaintBlock(
            string oldExtra, string newExtra, bool expectedSurvives)
        {
            StyleDocument oldStyle = NoPaintStyle(oldExtra), newStyle = NoPaintStyle(newExtra);
            Assert.AreEqual(expectedSurvives, WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                expectedSurvives
                    ? "minzoom/maxzoom/layout.visibility are read per frame off IRenderLayer.StyleLayer, "
                      + "which Restyle moves forward, and no mesh or PreparedKey depends on them — so this "
                      + "pair must survive in place. A refusal means the reduction never ran: written after "
                      + "Signature's early returns it is skipped for exactly this no-paint shape."
                    : "this pair differs in more than the three draw-gate keys, so the gate must still "
                      + "refuse. Accepting it means the reduction strips too much — dropping `layout` "
                      + "wholesale rather than only when stripping `visibility` empties it would make EVERY "
                      + "layout change survivable, a far worse hole than the one being closed.");
        }

        /// <summary>The same <c>minzoom</c>-only change WITH a paint block — the control that isolates the
        /// no-paint case above to <c>Signature</c>'s early-return path, since this one passes under either
        /// placement of the reduction.</summary>
        [Test]
        public void DrawGateRestyle_WithAPaintBlock_Survives()
        {
            var oldStyle = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1]", @"""minzoom"": 5,");
            var newStyle = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1]", @"""minzoom"": 7,");
            Assert.IsTrue(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a minzoom-only change on a layer that DOES carry paint must survive — this passes under "
                + "either placement of the reduction, which is what makes it the control.");
        }

        // ── 10. First style load never transitions ───────────────────────────────────────────

        [Test]
        public void FirstStyleLoad_NeverTransitions()
        {
            var style = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1]");
            var set = new RenderLayerSet();
            set.Build(style, 0.0, MapMaterialSetTestUtil.Load());

            Assert.AreEqual(0, set.TransitioningCount, "the very first build must arm no transition.");
            Color pushed = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);
            Assert.AreEqual(new Color(0.4f, 0.6f, 0.8f, 1f).r, pushed.r, 1e-4f,
                "frame 0 must already read the style's own value — nothing to ease from.");
        }

        // ── 11. Default duration is 300ms, from the API, never the style ─────────────────────

        [Test]
        public void DefaultDuration_Is300ms_FromTheApi_NotTheStyle()
        {
            var oldStyle = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1]");
            var newStyle = FillStyle(@"""fill-color"": [""rgba"",204,102,51,1]");
            var set = new RenderLayerSet();
            set.Build(oldStyle, 0.0, MapMaterialSetTestUtil.Load());

            Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                "drive precondition: a paint-only, gate-eligible restyle must take the in-place path.");

            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.15));
            Assert.Greater(set.TransitioningCount, 0, "mid-way through the default duration, still easing.");

            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.30));
            Assert.AreEqual(0, set.TransitioningCount, "settled at exactly the default duration (300ms).");

            // Second clause (decision 0a): a style document carrying a root `transition` block AND a
            // `fill-color-transition` key must produce IDENTICAL timing, because nothing parses either —
            // MapView.StyleTransition is the only source. The extra keys sit on BOTH sides (not just the
            // new style) so the gate's own "unknown root/paint key" fail-closed rule (correct, unrelated
            // to this clause) does not refuse before the timing question is even reached. There is no
            // parse site to point a RED injection at: this clause is a guard against a future regression
            // (someone adding one), not a property provable today (see plan §3 tooth 11 and §4).
            string WithTransitionHints(string fillColorRgba) => @"{
    ""version"": 8, ""name"": ""T"", ""transition"": { ""duration"": 5000, ""delay"": 5000 },
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"",
          ""paint"": { ""fill-color"": " + fillColorRgba + @",
                       ""fill-color-transition"": { ""duration"": 5000 } } }
    ]
}";
            var oldStyleWithHints = StyleParser.Parse(WithTransitionHints(@"[""rgba"",102,153,204,1]"));
            var newStyleWithHints = StyleParser.Parse(WithTransitionHints(@"[""rgba"",204,102,51,1]"));
            var set2 = new RenderLayerSet();
            set2.Build(oldStyleWithHints, 0.0, MapMaterialSetTestUtil.Load());
            Assert.IsTrue(set2.TryRestyleInPlace(oldStyleWithHints, newStyleWithHints, StyleTransition.Default, 0.0),
                "drive precondition: the extra keys are identical on both sides, so the gate must still accept.");
            set2.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.30));
            Assert.AreEqual(0, set2.TransitioningCount,
                "a root/per-property transition block in the STYLE must be inert — timing comes only from " +
                "MapView.StyleTransition (decision 0a); a 5s duration in the JSON must not extend this " +
                "300ms settle.");
        }

        // ── 16. A mesh-affecting restyle takes the rebuild path ──────────────────────────────

        private const string PaintOnly = @"""fill-color"": [""rgba"",102,153,204,1]";

        [Test]
        public void MeshAffectingRestyle_Filter_TakesTheRebuildPath()
        {
            var oldStyle = FillStyle(PaintOnly, @"""filter"": [""=="", ""class"", ""a""],");
            var newStyle = FillStyle(PaintOnly, @"""filter"": [""=="", ""class"", ""b""],");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a changed filter must move the layer signature and refuse the gate.");
        }

        [Test]
        public void MeshAffectingRestyle_SourceLayer_TakesTheRebuildPath()
        {
            var oldStyle = StyleParser.Parse(@"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [ { ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"",
        ""paint"": { ""fill-color"": [""rgba"",102,153,204,1] } } ]
}");
            var newStyle = StyleParser.Parse(@"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [ { ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill1"",
        ""paint"": { ""fill-color"": [""rgba"",102,153,204,1] } } ]
}");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a changed source-layer must move the layer signature and refuse the gate.");
        }

        [Test]
        public void MeshAffectingRestyle_FillAntialias_TakesTheRebuildPath()
        {
            var oldStyle = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1], ""fill-antialias"": true");
            var newStyle = FillStyle(@"""fill-color"": [""rgba"",102,153,204,1], ""fill-antialias"": false");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "fill-antialias selects a meshing path (SSOT §1.1) — it must stay inside the signature.");
        }

        // ── 17. A data-driven paint change takes the rebuild path ────────────────────────────

        [Test]
        public void DataDrivenPaintChange_TakesTheRebuildPath()
        {
            var oldStyle = FillStyle(@"""fill-color"": [""get"",""c1""]");
            var newStyle = FillStyle(@"""fill-color"": [""get"",""c2""]");

            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "two DIFFERENT data-driven fill-colors both skip the bind — without clause (c) the gate " +
                "would accept and every tile would keep the previous style's baked colour forever.");
        }

        // ── UMR-147: symbol layers survive and ease across a restyle (T1, T2a–e) ─────────────

        private const string SymbolTemplate = @"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [
        {{ ""id"": ""label0"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""label0"",
          ""layout"": {{ ""text-field"": ""{{NAME}}"" }},
          ""paint"": {{ {0} }} }}
    ]
}}";

        private static StyleDocument SymbolStyleDoc(string paintJson)
            => StyleParser.Parse(string.Format(SymbolTemplate, paintJson));

        /// <summary>
        /// <b>T1.</b> The real shipped liberty → liberty-night pair must survive the gate once text-color
        /// and text-halo-color are transitionable — this is the stage's actual deliverable (§0). RED-verify
        /// (two injections, either alone leaves the other's RED unproven): remove text-halo-color from
        /// TransitionablePaintKeys (18 refuse); separately remove text-color (20 refuse).
        /// </summary>
        [Test]
        public void DayNightPair_SurvivesTheRestyleGate()
        {
            var day   = SymbolTestFixtures.LibertyDoc();
            var night = SymbolTestFixtures.LibertyNightDoc();
            Assert.IsTrue(WholeDocumentGate.AllLayersSurvive(day, night),
                "the real shipped liberty -> liberty-night pair, differing only in symbol text-color and " +
                "text-halo-color, must survive the gate — this is the stage's deliverable.");
        }

        /// <summary><b>T2a.</b> A synthetic pair differing only in a Constant text-color survives — T2b's
        /// non-vacuity control (same fixture shape, opposite verdict). RED-verify: remove text-color from
        /// TransitionablePaintKeys.</summary>
        [Test]
        public void SymbolLayer_ConstantTextColorChange_Survives()
        {
            var oldStyle = SymbolStyleDoc(@"""text-color"": ""#996633""");
            var newStyle = SymbolStyleDoc(@"""text-color"": ""#2288DD""");
            Assert.IsTrue(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a Constant text-color change must survive — the applier re-binds the uniform on Restyle.");
        }

        /// <summary><b>T2b.</b> A Zoom-kind text-color change — different stop outputs on each side — must
        /// be REFUSED: the in-place path never re-bakes the vertex COLOR stream a non-Constant text-color
        /// lives in. RED-verify: change the gate's text-color arm from RidesUniform(kind) to
        /// !DependsOnFeature(kind).</summary>
        [Test]
        public void SymbolLayer_ZoomKindTextColorChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(
                @"""text-color"": [""interpolate"",[""linear""],[""zoom""],5,""#000000"",15,""#996633""]");
            var newStyle = SymbolStyleDoc(
                @"""text-color"": [""interpolate"",[""linear""],[""zoom""],5,""#000000"",15,""#2288DD""]");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "freeing text-color above Constant requires the in-place path to re-bake the vertex COLOR " +
                "stream — MapView's SymbolSubsystem.SetStyle skip and _symbolStyleLayers assume it never has to.");
        }

        /// <summary><b>T2c.</b> A Zoom-kind text-halo-color change — different stops on each side — must
        /// be REFUSED, on exactly the same grounds as T2b's text-color. This arm asserted the OPPOSITE while
        /// the whole halo trio bound to per-layer uniforms; once the halo became GEOMETRY, a non-Constant
        /// text-halo-color bakes into the vertex COLOR stream of a second glyph run, which the in-place path
        /// never re-bakes. RED-verify: widen the gate's symbol-colour arm from RidesUniform(kind) to
        /// !DependsOnFeature(kind).</summary>
        [Test]
        public void SymbolLayer_ZoomKindHaloColorChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(
                @"""text-halo-color"": [""interpolate"",[""linear""],[""zoom""],5,""#000000"",15,""#808080""]");
            var newStyle = SymbolStyleDoc(
                @"""text-halo-color"": [""interpolate"",[""linear""],[""zoom""],5,""#000000"",15,""#4099C0""]");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "freeing text-halo-color above Constant requires the in-place path to re-bake the halo run's " +
                "vertex COLOR stream — the same carrier argument that refuses a Zoom-kind text-color (T2b).");
        }

        /// <summary><b>T2c2.</b> The Constant control for T2c — same fixture shape, opposite verdict, so
        /// T2c's refusal is not vacuous. RED-verify: remove text-halo-color from
        /// TransitionablePaintKeys.</summary>
        [Test]
        public void SymbolLayer_ConstantHaloColorChange_Survives()
        {
            var oldStyle = SymbolStyleDoc(@"""text-halo-color"": ""#808080""");
            var newStyle = SymbolStyleDoc(@"""text-halo-color"": ""#4099C0""");
            Assert.IsTrue(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a Constant text-halo-color change must survive — it rides _HaloColor, which Restyle re-binds.");
        }

        /// <summary><b>T2d.</b> A data-driven text-halo-color change must be REFUSED — the gate guard and
        /// BindTextPaint's !DependsOnFeature guard are exact complements. RED-verify: drop the
        /// !DependsOnFeature half of the default arm.</summary>
        [Test]
        public void SymbolLayer_DataDrivenHaloColorChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(@"""text-halo-color"": [""get"",""hc1""]");
            var newStyle = SymbolStyleDoc(@"""text-halo-color"": [""get"",""hc2""]");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "two DIFFERENT data-driven text-halo-colors both skip the uniform bind — without the guard " +
                "every symbol layer would keep the previous style's halo forever.");
        }

        /// <summary><b>T2e.</b> A text-halo-width change — a key deliberately NOT freed — must still
        /// refuse, guarding against a future "make the gate pass" by adding keys.</summary>
        [Test]
        public void SymbolLayer_HaloWidthChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(@"""text-halo-width"": 1");
            var newStyle = SymbolStyleDoc(@"""text-halo-width"": 3");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "text-halo-width is not a gate key — a change to it must move the layer signature.");
        }

        /// <summary>
        /// A Constant <c>text-color</c> pair differing ONLY in alpha must be REFUSED — only RGB rides the
        /// <c>_TextColor</c> uniform; alpha travels by the vertex COLOR stream
        /// (<c>SymbolFeatureExtractor.EvaluatePaint</c>'s <c>textRgba.w</c>), which the in-place path never
        /// re-bakes. RED-verify: drop the <c>ConstantAlphaMatches</c> conjunct from
        /// <c>SurvivingLayerGate.FreelyTransitionableKeys</c>.
        /// </summary>
        /// <summary>
        /// The halo twin of <see cref="SymbolLayer_AlphaOnlyTextColorChange_IsRefused"/>. text-halo-color's
        /// alpha rides the opacity stream AND decides whether a halo run is emitted at all
        /// (<c>WorldSymbolRenderer.Emit</c>), so an alpha-only change must refuse even though both sides are
        /// Constant. RED-verify: restrict <c>ConstantAlphaMatches</c> back to text-color only.
        /// </summary>
        [Test]
        public void SymbolLayer_AlphaOnlyHaloColorChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(@"""text-halo-color"": ""#808080""");
            var newStyle = SymbolStyleDoc(@"""text-halo-color"": [""rgba"",128,128,128,0.5]");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "an alpha-only text-halo-color change must refuse — the uniform is RGB-only, and the alpha " +
                "also gates whether the halo run exists.");
        }

        [Test]
        public void SymbolLayer_AlphaOnlyTextColorChange_IsRefused()
        {
            var oldStyle = SymbolStyleDoc(@"""text-color"": ""#000000""");
            var newStyle = SymbolStyleDoc(@"""text-color"": [""rgba"",0,0,0,0.5]");
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "an alpha-only text-color change must refuse — the two colours differ ONLY in the " +
                "component the in-place path cannot move (the uniform is RGB-only).");
        }
    }
}
