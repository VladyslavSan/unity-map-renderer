// Style/StyleTransitionTests.cs — Unity EditMode only (real Materials, real RenderLayerSet; not in core-tests).
// Bare `Color`/`Object` here are UnityEngine's, so Core-expressions-side Style files stay separate (CS0104).
//
// Contents:
//   StyleTransitionBindingTests  — the transition inside the binding, all-survivors path.
//   ZoomStyleApplierTests        — ZoomStyleApplier integration: no mesh rebuild, ordered queues, zero-GC hot path.
//   SymbolRestyleTests           — symbol layers survive the restyle gate and ease colour across a restyle.
//   LayerFadeViewTests           — the fade gate as driven by a real MapViewComponent.
//   RestyleCommitAtomicityTests  — commit atomicity over MapView.SetStyle's full-rebuild arm.

using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Unity.Mathematics;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Tiles;      // IDecodedTile
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using CoreColor = MapRenderer.Core.Expressions.Color;
using System.Collections.Generic;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Geometry;
using MapRenderer.Unity.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Unity.Rendering.Tile;
using MapViewComponent = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using CommitPhase = MapRenderer.Unity.Rendering.Map.MapView.CommitPhase;

namespace MapRenderer.Tests.Style
{

    // ───────────────────────────────────────────────────────────────────────────────────
    // StyleTransitionBindingTests — the transition inside the binding, all-survivors path
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class StyleTransitionBindingTests : BaseTestFixture
    {
        private static readonly CoreColor ColorA = new CoreColor(0.4, 0.6, 0.8, 1.0); // #6699CC
        private static readonly CoreColor ColorB = new CoreColor(0.8, 0.4, 0.2, 1.0); // #CC6633

        private const string FillTemplate = @"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://x/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [
        {{ ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"",
          ""paint"": {{ {0} }} }}
    ]
}}";

        private static StyleDocument FillStyle(string paintJson) => StyleParser.Parse(string.Format(FillTemplate, paintJson));

        private static RenderLayerSet BuildFillSet(StyleDocument style, double initialZoom = 0.0)
        {
            // NOT `using`: this factory returns the set, and the caller owns disposal. A `using` here
            // would dispose it before the caller touches it.
            var set = new RenderLayerSet();
            set.Build(style, initialZoom, MapMaterialSetTestUtil.Load());
            return set;
        }

        private static string Rgba(CoreColor c) => $"[\"rgba\",{c.R * 255},{c.G * 255},{c.B * 255},{c.A}]";

        // ── 2. Eased, not linear ──────────────────────────────────────────────────────────────

        [Test]
        public void ColorTransition_QuarterPoint_IsEasedNotLinear()
        {
            var oldStyle = FillStyle($@"""fill-color"": {Rgba(ColorA)}");
            var newStyle = FillStyle($@"""fill-color"": {Rgba(ColorB)}");
            using var set = BuildFillSet(oldStyle);

            Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle,
                new StyleTransition { DurationSeconds = 1.0, DelaySeconds = 0.0 }, nowSeconds: 0.0),
                "drive precondition: A->B must take the in-place path.");
            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.25));

            Color pushed = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);
            Color eased = ZoomStyleApplier.ToUnityColor(CoreColor.MixPremultiplied(ColorA, ColorB, 0.15625));
            Color linear = ZoomStyleApplier.ToUnityColor(new CoreColor(
                Lin(ColorA.R, ColorB.R, 0.25), Lin(ColorA.G, ColorB.G, 0.25),
                Lin(ColorA.B, ColorB.B, 0.25), Lin(ColorA.A, ColorB.A, 0.25)));
            Color a = ZoomStyleApplier.ToUnityColor(ColorA);
            Color b = ZoomStyleApplier.ToUnityColor(ColorB);

            // Clause order is deliberate: the equality clause first, so a shadowing regression in the
            // inequality clauses cannot mask a broken equality.
            AssertApprox(eased, pushed, "t=0.25 must equal MixPremultiplied(A,B,smoothstep(0.25)).");
            Assert.IsFalse(NearlyEqual(pushed, linear), "t=0.25 must NOT equal the linear (non-eased) mix.");
            Assert.IsFalse(NearlyEqual(pushed, a), "t=0.25 must have moved off the origin.");
            Assert.IsFalse(NearlyEqual(pushed, b), "t=0.25 must not have reached the target yet.");
        }

        private static double Lin(double x, double y, double t) => x + (y - x) * t;

        private static bool NearlyEqual(Color a, Color b, float eps = 1e-4f)
            => Mathf.Abs(a.r - b.r) < eps && Mathf.Abs(a.g - b.g) < eps &&
               Mathf.Abs(a.b - b.b) < eps && Mathf.Abs(a.a - b.a) < eps;

        private static void AssertApprox(Color expected, Color actual, string message, float eps = 1e-4f)
        {
            Assert.AreEqual(expected.r, actual.r, eps, message + " (R)");
            Assert.AreEqual(expected.g, actual.g, eps, message + " (G)");
            Assert.AreEqual(expected.b, actual.b, eps, message + " (B)");
            Assert.AreEqual(expected.a, actual.a, eps, message + " (A)");
        }

        // ── 3. Endpoints are bit-exact ────────────────────────────────────────────────────────

        [Test]
        public void Transition_Endpoints_AreExact()
        {
            var oldStyle = FillStyle($@"""fill-color"": {Rgba(ColorA)}");
            var newStyle = FillStyle($@"""fill-color"": {Rgba(ColorB)}");
            using var set = BuildFillSet(oldStyle);
            const double duration = 1.0;
            set.TryRestyleInPlace(oldStyle, newStyle, new StyleTransition { DurationSeconds = duration }, 0.0);

            // Oracles, not a raw cast: SetColor/GetColor round-trips through a gamma conversion (~1 ULP of
            // instrument noise). A fresh build of the SAME style takes the identical round-trip.
            Color oracleA = BuildFillSet(oldStyle)[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);
            Color oracleB = BuildFillSet(newStyle)[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);

            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.0)); // t == 0 exactly
            Color atStart = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);
            Assert.AreEqual(oracleA.r, atStart.r, 0f, "t=0 R must be bit-exact origin.");
            Assert.AreEqual(oracleA.g, atStart.g, 0f, "t=0 G must be bit-exact origin.");
            Assert.AreEqual(oracleA.b, atStart.b, 0f, "t=0 B must be bit-exact origin.");
            Assert.AreEqual(oracleA.a, atStart.a, 0f, "t=0 A must be bit-exact origin.");

            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, duration)); // t >= 1
            Color atEnd = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);
            Assert.AreEqual(oracleB.r, atEnd.r, 0f, "t>=1 R must be bit-exact target.");
            Assert.AreEqual(oracleB.g, atEnd.g, 0f, "t>=1 G must be bit-exact target.");
            Assert.AreEqual(oracleB.b, atEnd.b, 0f, "t>=1 B must be bit-exact target.");
            Assert.AreEqual(oracleB.a, atEnd.a, 0f, "t>=1 A must be bit-exact target.");
            Assert.AreEqual(0, set.TransitioningCount, "settled after t>=1.");
        }

        // ── 4. Zoom change mid-transition moves both endpoints ───────────────────────────────

        private const string ZoomColorExprA =
            "[\"interpolate\",[\"linear\"],[\"zoom\"],0,[\"rgba\",102,153,204,1],10,[\"rgba\",0,0,0,1]]";
        private const string ZoomColorExprB =
            "[\"interpolate\",[\"linear\"],[\"zoom\"],0,[\"rgba\",204,102,51,1],10,[\"rgba\",255,255,255,1]]";

        [Test]
        public void Transition_ZoomChangeMidway_MovesBothEndpoints()
        {
            var oldStyle = FillStyle($@"""fill-color"": {ZoomColorExprA}");
            var newStyle = FillStyle($@"""fill-color"": {ZoomColorExprB}");
            using var set = BuildFillSet(oldStyle, initialZoom: 0.0);
            set.TryRestyleInPlace(oldStyle, newStyle, new StyleTransition { DurationSeconds = 1.0 }, 0.0);

            // z1 at bind time is irrelevant to the endpoints (they are re-evaluated at the LIVE zoom every
            // frame) — sample straight at z2, t=0.5.
            set.ApplyZoom(new StyleFrameInputs(5.0, 1.0, 0.5));
            Color pushed = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);

            var originAtZ2 = new StyleProperty<CoreColor>(JsonParser.Parse(ZoomColorExprA), default,
                v => v.AsColorCoerced());
            var targetAtZ2 = new StyleProperty<CoreColor>(JsonParser.Parse(ZoomColorExprB), default,
                v => v.AsColorCoerced());
            Color expected = ZoomStyleApplier.ToUnityColor(CoreColor.MixPremultiplied(
                originAtZ2.Evaluate(5.0), targetAtZ2.Evaluate(5.0), math.smoothstep(0.0, 1.0, 0.5)));

            AssertApprox(expected, pushed, "mid-transition at a MOVED zoom must re-evaluate BOTH endpoints there.");
        }

        // ── 5. Retarget mid-transition is continuous ─────────────────────────────────────────

        [Test]
        public void Retarget_MidTransition_IsContinuous()
        {
            var styleA = FillStyle($@"""fill-color"": {Rgba(ColorA)}");
            var styleB = FillStyle($@"""fill-color"": {Rgba(ColorB)}");
            var styleC = FillStyle(@"""fill-color"": [""rgba"",0,255,0,1]");
            using var set = BuildFillSet(styleA);
            set.TryRestyleInPlace(styleA, styleB, new StyleTransition { DurationSeconds = 1.0 }, 0.0);

            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.4));
            Color lastPrePush = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);

            Assert.IsTrue(set.TryRestyleInPlace(styleB, styleC, new StyleTransition { DurationSeconds = 1.0 }, 0.4),
                "drive precondition: the interrupting B->C restyle must also take the in-place path.");
            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.4)); // first post-retarget push, same instant
            Color firstPostPush = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);

            Assert.AreEqual(lastPrePush.r, firstPostPush.r, 0f, "interrupt must be bit-continuous (R).");
            Assert.AreEqual(lastPrePush.g, firstPostPush.g, 0f, "interrupt must be bit-continuous (G).");
            Assert.AreEqual(lastPrePush.b, firstPostPush.b, 0f, "interrupt must be bit-continuous (B).");

            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 1.4)); // settle onto C
            Color settled = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);
            Color expectedC = new Color(0f, 1f, 0f, 1f);
            AssertApprox(expectedC, settled, "must reach C after the second transition settles.");
        }

        // ── 6 / 7. Discrete binding never fractional; a discriminant-only restyle arms nothing ──

        [Test]
        public void DiscreteBinding_IsNeverFractional()
        {
            var oldStyle = FillStyle(@"""fill-color"": [""rgba"",255,255,255,1], ""fill-translate-anchor"": ""map""");
            var newStyle = FillStyle(@"""fill-color"": [""rgba"",255,255,255,1], ""fill-translate-anchor"": ""viewport""");
            using var set = BuildFillSet(oldStyle);
            const double duration = 1.0;
            set.TryRestyleInPlace(oldStyle, newStyle, new StyleTransition { DurationSeconds = duration }, 0.0);

            double[] samples = { -0.1, 0.0, 0.25, 0.5, 1.0 };
            foreach (double t in samples)
            {
                set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, t * duration));
                float v = set[0].Material.GetFloat(ShaderProperties.Fill.PropertyId.FillTranslateAnchor);
                Assert.IsTrue(v == 0f || v == 1f, $"translate-anchor must never be fractional (t={t}, got {v}).");
                if (t < 0.0) Assert.AreEqual(0f, v, "before StartSeconds (delay window) must hold the origin.");
                else Assert.AreEqual(1f, v, $"at/after StartSeconds (t={t}) must already be the target.");
            }
        }

        [Test]
        public void DiscriminantOnlyRestyle_ArmsNothing()
        {
            var oldStyle = FillStyle(@"""fill-color"": [""rgba"",255,255,255,1], ""fill-translate-anchor"": ""map""");
            var newStyle = FillStyle(@"""fill-color"": [""rgba"",255,255,255,1], ""fill-translate-anchor"": ""viewport""");
            using var set = BuildFillSet(oldStyle);
            const double now = 10.0;
            Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, now),
                "drive precondition: a discriminant-only restyle must still take the in-place path.");
            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, now)); // the frame SetStyle itself would drive

            Assert.AreEqual(0, set.TransitioningCount, "a discriminant-only restyle must arm nothing.");
            Assert.AreEqual(1f, set[0].Material.GetFloat(ShaderProperties.Fill.PropertyId.FillTranslateAnchor),
                "the uniform must already be at its new value.");
        }

        // ── 8. Delay holds the origin and follows zoom ───────────────────────────────────────

        [Test]
        public void Delay_HoldsOrigin_AndFollowsZoom()
        {
            var oldStyle = FillStyle($@"""fill-color"": {ZoomColorExprA}");
            var newStyle = FillStyle($@"""fill-color"": {ZoomColorExprB}");
            using var set = BuildFillSet(oldStyle, initialZoom: 0.0);
            set.TryRestyleInPlace(oldStyle, newStyle,
                new StyleTransition { DurationSeconds = 1.0, DelaySeconds = 0.5 }, nowSeconds: 0.0);

            // Still inside the delay window (0.25 < 0.5); zoom has moved to z2 — the ORIGIN must follow it.
            set.ApplyZoom(new StyleFrameInputs(5.0, 1.0, 0.25));
            Color pushed = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);

            var originExpr = new StyleProperty<CoreColor>(JsonParser.Parse(ZoomColorExprA), default,
                v => v.AsColorCoerced());
            Color expected = ZoomStyleApplier.ToUnityColor(originExpr.Evaluate(5.0));
            AssertApprox(expected, pushed, "mid-delay must hold the ORIGIN, evaluated at the LIVE zoom.");
        }

        // ── 9. Settled bindings are not re-pushed ────────────────────────────────────────────

        [Test]
        public void SettledBindings_AreNotRepushed()
        {
            var oldStyle = FillStyle($@"""fill-color"": {Rgba(ColorA)}");
            var newStyle = FillStyle($@"""fill-color"": {Rgba(ColorB)}");
            using var set = BuildFillSet(oldStyle);
            // Instant: settles in the SAME call, so the very next ApplyZoom is a settled-constant frame.
            set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Instant, 0.0);
            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.0));

            var sentinel = new Color(0.123f, 0.456f, 0.789f, 1f);
            set[0].Material.SetColor(ShaderProperties.PropertyId.BaseColor, sentinel);
            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 1.0)); // settled constant: must be skipped

            Color after = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);
            AssertApprox(sentinel, after, "a settled Constant binding must not be re-pushed.");
        }

        // ── 12. Instant matches the unanimated path ──────────────────────────────────────────

        [Test]
        public void InstantTransition_MatchesTheUnanimatedPath()
        {
            var oldStyle = FillStyle($@"""fill-color"": {Rgba(ColorA)}");
            var newStyle = FillStyle($@"""fill-color"": {Rgba(ColorB)}");

            // Oracle: a FRESH build straight onto newStyle does not transition, and unlike a captured
            // snapshot it cannot go stale.
            using var oracleSet = BuildFillSet(newStyle);
            Color oracle = oracleSet[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);

            using var set = BuildFillSet(oldStyle);
            Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Instant, 0.0),
                "drive precondition: an Instant restyle must still take the in-place path.");
            Color restyled = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);

            AssertApprox(oracle, restyled, "Instant must match a fresh build of the SAME style, same frame.", 0f);
            Assert.AreEqual(0, set.TransitioningCount, "Instant must arm nothing.");
        }

        // ── 13-15. Alloc-free at every phase — the loop the 5 per-commit teeth cannot see ────
        // MinimalStyle() arms no transition; these run ApplyZoom with >=3 colour, >=2 float, >=1 device-px easing.

        private static ZoomStyleApplier MixedApplier(out Material mat, bool retarget, double now,
            double duration = 1.0)
        {
            mat = MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load());
            var applier = new ZoomStyleApplier(mat);
            applier.BindColor(new StyleProperty<CoreColor>(ColorA), ShaderProperties.PropertyId.BaseColor);
            applier.BindColor(new StyleProperty<CoreColor>(ColorA), ShaderProperties.Fill.PropertyId.FillOutlineColor);
            applier.BindColor(new StyleProperty<CoreColor>(ColorA), ShaderProperties.PropertyId.EmissionColor);
            applier.BindFloat(new StyleProperty<float>(0.2f), ShaderProperties.PropertyId.Opacity);
            applier.BindFloat(new StyleProperty<float>(0.3f), ShaderProperties.PropertyId.Smoothness);
            applier.BindDevicePixelVector(new StyleProperty<double2>(new double2(1.0, 2.0)),
                ShaderProperties.Fill.PropertyId.FillTranslate);

            if (retarget)
            {
                applier.SetTransition(new StyleTransition { DurationSeconds = duration }, now);
                applier.BindColor(new StyleProperty<CoreColor>(ColorB), ShaderProperties.PropertyId.BaseColor);
                applier.BindColor(new StyleProperty<CoreColor>(ColorB), ShaderProperties.Fill.PropertyId.FillOutlineColor);
                applier.BindColor(new StyleProperty<CoreColor>(ColorB), ShaderProperties.PropertyId.EmissionColor);
                applier.BindFloat(new StyleProperty<float>(0.8f), ShaderProperties.PropertyId.Opacity);
                applier.BindFloat(new StyleProperty<float>(0.9f), ShaderProperties.PropertyId.Smoothness);
                applier.BindDevicePixelVector(new StyleProperty<double2>(new double2(3.0, 4.0)),
                    ShaderProperties.Fill.PropertyId.FillTranslate);
            }
            return applier;
        }

        [Test]
        public void ApplyZoom_MidTransition_AllocatesNothing()
        {
            var applier = MixedApplier(out Material mat, retarget: true, now: 0.0, duration: 1.0);
            Track(mat);
            applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.1)); // warm up
            Assert.That(() => applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.5)),
                Is.Not.AllocatingGCMemory(),
                "ApplyZoom mid-transition, mixed binding types, must not allocate.");
        }

        [Test]
        public void ApplyZoom_OnTheSettlingFrame_AllocatesNothing()
        {
            var applier = MixedApplier(out Material mat, retarget: true, now: 0.0, duration: 1.0);
            Track(mat);
            applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.9)); // warm up, still transitioning
            Assert.That(() => applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 1.0)), // t crosses 1 HERE
                Is.Not.AllocatingGCMemory(),
                "the frame that settles every binding (mutates each Origin to null) must not allocate.");
        }

        [Test]
        public void ApplyZoom_Settled_AllocatesNothing()
        {
            var applier = MixedApplier(out Material mat, retarget: true, now: 0.0, duration: 1.0);
            Track(mat);
            applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 10.0)); // settle
            Assert.That(() => applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 11.0)),
                Is.Not.AllocatingGCMemory(),
                "post-settle, non-empty binding lists, must not allocate.");
        }

        // ── 18 / 20. Symbol paint / source changes still take the rebuild path ───────────────
        // These two need a second Build call, the path MapView takes on refusal, to see the material move.

        /// <summary>
        /// A <c>text-opacity</c> change refuses and rebuilds: it bakes into the vertex stream
        /// (<c>SymbolPaint.Opacity</c>), so it is NOT a gate key. Constant <c>text-color</c> takes the
        /// in-place path instead (<see cref="SymbolPaintChange_TakesTheInPlacePath_MaterialSurvives"/>).
        /// Adding <c>text-opacity</c> to <c>SurvivingLayerGate.TransitionablePaintKeys</c> reds this.
        /// </summary>
        [Test]
        public void SymbolPaintChange_TakesTheRebuildPath()
        {
            // Plain concatenation, not string.Format — a text-field placeholder ("{name}") and this
            // template's own JSON braces would otherwise need escaping against each other.
            string Doc(string opacity) => @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""label0"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""label0"",
          ""layout"": { ""text-field"": ""{name}"" },
          ""paint"": { ""text-opacity"": " + opacity + @" } }
    ]
}";
            var oldStyle = StyleParser.Parse(Doc("1"));
            var newStyle = StyleParser.Parse(Doc("0.5"));

            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a text-opacity change must fail the gate — it always bakes into the vertex stream, so it " +
                "is not a gate key and must move the layer signature.");
        }

        /// <summary>
        /// A Constant symbol <c>text-color</c> change takes the IN-PLACE path, because the key IS in
        /// <c>TransitionablePaintKeys</c>. Only this tooth sees that the material reference does not move,
        /// which <c>MapView</c>'s skip of <c>Layers.Build</c> depends on. Limitation: that clause has no RED
        /// injection, because <see cref="SymbolRenderLayer.WorldTextMaterial"/> has no setter.
        /// </summary>
        [Test]
        public void SymbolPaintChange_TakesTheInPlacePath_MaterialSurvives()
        {
            // Plain concatenation, not string.Format — a text-field placeholder ("{name}") and this
            // template's own JSON braces would otherwise need escaping against each other.
            string Doc(string textColor) => @"{
    ""version"": 8, ""name"": ""T"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""label0"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""label0"",
          ""layout"": { ""text-field"": ""{name}"" },
          ""paint"": { ""text-color"": """ + textColor + @""" } }
    ]
}";
            var oldStyle = StyleParser.Parse(Doc("#000000"));
            var newStyle = StyleParser.Parse(Doc("#ffffff"));
            var set = BuildSymbolSet(oldStyle);

            Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, 0.0),
                "a Constant text-color change must take the IN-PLACE path — text-color rides the " +
                "_TextColor uniform and SymbolRenderLayer.Restyle re-binds it.");
            // Not Material identity (WorldTextMaterial has no setter — unbreakable). StyleLayer moving
            // forward is the breakable half: see TryRestyleInPlace's own doc for why a stale StyleLayer matters.
            Assert.AreSame(newStyle.Layers[0], set[0].StyleLayer,
                "Restyle must move StyleLayer forward to the new document's layer object.");
        }

        private static RenderLayerSet BuildSymbolSet(StyleDocument style, double initialZoom = 0.0)
        {
            var set = new RenderLayerSet();
            set.Build(style, initialZoom, MapMaterialSetTestUtil.Load());
            return set;
        }

        private const string SourceChangeTemplate = @"{{
    ""version"": 8, ""name"": ""T"",
    ""sources"": {{ ""s"": {{ ""type"": ""vector"", ""tiles"": [""https://{0}/{{z}}/{{x}}/{{y}}.pbf""] }} }},
    ""layers"": [
        {{ ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"",
          ""paint"": {{ ""fill-color"": [""rgba"",102,153,204,1] }} }}
    ]
}}";

        [Test]
        public void SourceChangeRestyle_TakesTheRebuildPath()
        {
            var oldStyle = StyleParser.Parse(string.Format(SourceChangeTemplate, "a"));
            var newStyle = StyleParser.Parse(string.Format(SourceChangeTemplate, "different-host"));
            using var set = BuildFillSet(oldStyle);
            Material before = set[0].Material;

            // A source's tiles[] url lives on the ROOT, so the gate's root comparison refuses here: a source
            // change rebuilds.
            Assert.IsFalse(WholeDocumentGate.AllLayersSurvive(oldStyle, newStyle),
                "a changed source url must refuse the gate.");
            set.Build(newStyle, 0.0, MapMaterialSetTestUtil.Load()); // today's path, as MapView takes it
            Assert.AreNotSame(before, set[0].Material, "a rebuild must mint a new material instance.");
        }

        // ── 22. A repeated in-place restyle keeps pairing every layer ────────────────────────

        [Test]
        public void RepeatedInPlaceRestyle_StillPairsEveryLayer()
        {
            var styleA1 = FillStyle($@"""fill-color"": {Rgba(ColorA)}");
            var styleB = FillStyle($@"""fill-color"": {Rgba(ColorB)}");
            var styleA2 = FillStyle($@"""fill-color"": {Rgba(ColorA)}");
            using var set = BuildFillSet(styleA1);
            Material mat0 = set[0].Material;

            Assert.IsTrue(set.TryRestyleInPlace(styleA1, styleB, StyleTransition.Default, 0.0),
                "drive precondition: the first A1->B restyle must take the in-place path.");
            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.0));
            Assert.AreSame(mat0, set[0].Material, "first restyle must keep the material.");
            Assert.Greater(set.TransitioningCount, 0, "first restyle must have armed a transition.");

            Assert.IsTrue(set.TryRestyleInPlace(styleB, styleA2, StyleTransition.Default, 1.0),
                "drive precondition: the second B->A2 restyle must ALSO take the in-place path.");
            set.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 1.0));
            Assert.AreSame(mat0, set[0].Material, "second restyle must ALSO keep the material.");
            Assert.Greater(set.TransitioningCount, 0, "second restyle must ALSO have armed a transition.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // ZoomStyleApplierTests — ZoomStyleApplier integration: no mesh rebuild, ordered queues, zero-GC hot path
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="ZoomStyleApplier"/> over the live <see cref="RenderLayerSet"/> / <see cref="MaterialFactory"/>
    /// path: no mesh rebuild across zooms, premultiplied-alpha colour uniforms, distinct per-layer Materials
    /// with ordered queues, and zero GC bytes for <c>ApplyZoom</c> over a SWEEPING zoom.
    /// </summary>
    [TestFixture]
    public class ZoomStyleApplierTests : BaseTestFixture
    {
        // ── Helper: build a two-layer styled set (one fill, one line) ───────────────

        // Fill layer first (declared index 0 = bottom), line layer second (index 1 = top).
        private const string TwoLayerStyleJson = @"{
    ""version"": 8,
    ""name"": ""ZoomStyleApplierTest"",
    ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://x/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""fill0"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""fill0"",
          ""paint"": { ""fill-color"": [""rgba"",255,255,255,1] } },
        { ""id"": ""line1"", ""type"": ""line"", ""source"": ""s"", ""source-layer"": ""line1"",
          ""paint"": { ""line-color"": [""rgba"",255,0,0,1] } }
    ]
}";

        private static RenderLayerSet BuildTwoLayerSet(double initialZoom = 0.0)
        {
            var style = StyleParser.Parse(TwoLayerStyleJson);
            var set = new RenderLayerSet();
            set.Build(style, initialZoom, MapMaterialSetTestUtil.Load());
            return set;
        }

        // ── 1. No mesh rebuild ────────────────────────────────────────────────────

        [Test]
        public void ApplyZoom_LineWidth_MeshReferenceUnchanged_WhileWidthChanges()
        {
            // Build-once / restyle-via-uniforms: across ApplyZoom calls the MeshFilter's mesh reference stays
            // the same while _Width tracks the zoom expression.
            Material lineMat = Track(MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load()));

            var lineMesh = Track(SyntheticLineMesh.BuildFromPoints(
                new List<double2> { new double2(-40, 0), new double2(40, 0) },
                JoinType.Miter, CapType.Butt));
            var go = Track(new GameObject("ZoomApplier_LineMesh"));
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = lineMesh;
            Mesh meshBefore = mf.sharedMesh;

            {
                // Create a zoom-interpolated width expression.
                var widthSp = new StyleProperty<float>(
                    JsonParser.Parse("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]"),
                    0f, v => (float)v.AsNumber());
                var applier = new ZoomStyleApplier(lineMat);
                applier.BindFloat(widthSp, ShaderProperties.Line.PropertyId.Width);

                applier.ApplyZoom(new StyleFrameInputs(5.0, 1.0, 0.0));
                float widthAtZ5 = lineMat.GetFloat("_Width");
                applier.ApplyZoom(new StyleFrameInputs(15.0, 1.0, 0.0));
                float widthAtZ15 = lineMat.GetFloat("_Width");

                // Mesh must be the SAME reference after multiple ApplyZoom calls.
                Assert.IsTrue(ReferenceEquals(meshBefore, mf.sharedMesh),
                    "sharedMesh reference must not change after ApplyZoom — build-once, restyle path.");

                // Width must have changed.
                Assert.AreEqual(2.0f, widthAtZ5, 0.01f, "Width at zoom=5 must equal lower stop (2.0).");
                Assert.AreEqual(20.0f, widthAtZ15, 0.01f, "Width at zoom=15 must equal upper stop (20.0).");
            }
        }

        // ── 2. Premultiplied-alpha color matches evaluator ────────────────────────

        [Test]
        public void ApplyZoom_Color_MatchesPremultipliedAlphaEvaluator()
        {
            Material fillMat = Track(MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load()));
            {
                // rgba(0,0,0,0) → rgba(255,255,255,1) — the canonical premult discriminating case.
                var colorSp = new StyleProperty<CoreColor>(
                    JsonParser.Parse("[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                    "0,[\"rgba\",0,0,0,0],1,[\"rgba\",255,255,255,1]]"),
                    default, v => v.AsColorCoerced());
                var applier = new ZoomStyleApplier(fillMat);
                applier.BindColor(colorSp, ShaderProperties.PropertyId.BaseColor);

                // Apply at zoom=0.5 (t=0.5 between stops 0 and 1).
                applier.ApplyZoom(new StyleFrameInputs(0.5, 1.0, 0.0));
                Color unity = fillMat.GetColor("_BaseColor");

                // Derive the expected value from the Core evaluator with the SAME converter.
                CoreColor core = colorSp.Evaluate(0.5);
                Color expected = ZoomStyleApplier.ToUnityColor(core);

                Assert.AreEqual(expected.r, unity.r, 0.01f,
                    "R channel from material must match Core evaluator (premultiplied-alpha).");
                Assert.AreEqual(expected.g, unity.g, 0.01f, "G channel.");
                Assert.AreEqual(expected.b, unity.b, 0.01f, "B channel.");
                Assert.AreEqual(expected.a, unity.a, 0.01f, "Alpha channel.");

                // Spec correctness: R must be near 1.0 (premult gives R=255/255=1), not 0.5 (straight lerp).
                Assert.Greater(unity.r, 0.9f,
                    "Premultiplied-alpha: R at t=0.5 for transparent→opaque must be near 1.0, not 0.5.");
            }
        }

        // ── 3. Per-layer distinct Material instances ─────────────────────────────

        [Test]
        public void RenderLayerSet_PerLayerMaterials_AreDistinctInstances()
        {
            using var set = BuildTwoLayerSet();
            Assert.AreEqual(2, set.Count, "Expected two render layers (one fill, one line).");
            Assert.IsInstanceOf<MapRenderer.Core.Style.Fill.StyleLayer>(set[0].StyleLayer, "Layer 0 is the fill.");
            Assert.IsInstanceOf<MapRenderer.Core.Style.Line.StyleLayer>(set[1].StyleLayer, "Layer 1 is the line.");

            Material m0 = set[0].Material;
            Material m1 = set[1].Material;

            Assert.IsFalse(ReferenceEquals(m0, m1),
                "Each layer must have its own distinct Material instance (never shared).");
        }

        [Test]
        public void RenderLayerSet_DeclaredOrder_QueueIsStrictlyIncreasing()
        {
            using var set = BuildTwoLayerSet();

            // Declared order: fill (index 0) then line (index 1). A single monotonic draw index is assigned
            // across ALL layers, so the fill's queue must be lower than the line's.
            int q0 = set[0].Material.renderQueue;
            int q1 = set[1].Material.renderQueue;

            Assert.Less(q0, q1,
                "Layer 0 (fill, declared first) must have a lower render queue than layer 1 (line) " +
                "— painter's algorithm: declared-first = bottom.");
            Assert.GreaterOrEqual(q0, LayerDrawOrder.TransparentBandStart,
                "Both queues must be in the transparent band.");
            Assert.GreaterOrEqual(q1, LayerDrawOrder.TransparentBandStart);
        }

        // ── 4. No per-frame GC allocation (SWEEPING zoom) ─────────────────────────
        // A SWEPT zoom runs the full interpolation path; a constant one could let the JIT hoist it.

        [Test]
        public void ApplyZoom_SweepingZoom_FloatBinding_AllocatesZeroGCMemory()
        {
            Material lineMat = Track(MaterialFactory.CreateLineMaterial(MapMaterialSetTestUtil.Load()));
            {
                var widthSp = new StyleProperty<float>(
                    JsonParser.Parse("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]"),
                    0f, v => (float)v.AsNumber());
                var applier = new ZoomStyleApplier(lineMat);
                applier.BindFloat(widthSp, ShaderProperties.Line.PropertyId.Width);
                // Also exercise the device-pixel float loop — without this binding it iterates ZERO
                // elements here, so a used allocation there could not be caught.
                var devicePixelSp = new StyleProperty<float>(
                    JsonParser.Parse("[\"interpolate\",[\"linear\"],[\"zoom\"],5,1.0,15,4.0]"),
                    0f, v => (float)v.AsNumber());
                applier.BindDevicePixelFloat(devicePixelSp, Shader.PropertyToID("_HaloWidthPx"));

                // Warm up: ensure JIT compilation and shader reflection are done before measuring.
                for (int w = 0; w < 20; w++)
                    applier.ApplyZoom(new StyleFrameInputs(5.0 + (w % 10) * 1.0, 1.0, 0.0));

                // ── Measure: swept zoom, must be alloc-free. ──
                double sweepZoom = 5.0;
                Assert.That(() =>
                {
                    // Use a closure-captured local that changes each call so the sweep is genuinely variable.
                    sweepZoom += 0.1;
                    if (sweepZoom > 15.0) sweepZoom = 5.0;
                    applier.ApplyZoom(new StyleFrameInputs(sweepZoom, 1.0, 0.0));
                }, Is.Not.AllocatingGCMemory(),
                    "ZoomStyleApplier.ApplyZoom (float binding, sweeping zoom) must not allocate GC memory. " +
                    "A failure indicates a Value[], boxing, or per-frame allocation escaped the hot path.");
            }
        }

        [Test]
        public void ApplyZoom_SweepingZoom_ColorBinding_AllocatesZeroGCMemory()
        {
            Material fillMat = Track(MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load()));
            {
                var colorSp = new StyleProperty<CoreColor>(
                    JsonParser.Parse("[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                    "5,[\"rgb\",255,0,0],15,[\"rgb\",0,0,255]]"),
                    default, v => v.AsColorCoerced());
                var applier = new ZoomStyleApplier(fillMat);
                applier.BindColor(colorSp, ShaderProperties.PropertyId.BaseColor);
                // Also exercise the device-pixel float loop — see the same binding in
                // ApplyZoom_SweepingZoom_FloatBinding_AllocatesZeroGCMemory for why.
                var devicePixelSp = new StyleProperty<float>(
                    JsonParser.Parse("[\"interpolate\",[\"linear\"],[\"zoom\"],5,1.0,15,4.0]"),
                    0f, v => (float)v.AsNumber());
                applier.BindDevicePixelFloat(devicePixelSp, Shader.PropertyToID("_HaloWidthPx"));

                for (int w = 0; w < 20; w++)
                    applier.ApplyZoom(new StyleFrameInputs(5.0 + (w % 10) * 1.0, 1.0, 0.0));

                double sweepZoom = 5.0;
                Assert.That(() =>
                {
                    sweepZoom += 0.1;
                    if (sweepZoom > 15.0) sweepZoom = 5.0;
                    applier.ApplyZoom(new StyleFrameInputs(sweepZoom, 1.0, 0.0));
                }, Is.Not.AllocatingGCMemory(),
                    "ZoomStyleApplier.ApplyZoom (color binding, sweeping zoom) must not allocate GC memory. " +
                    "A failure means rgb() stop outputs were not constant-folded to LiteralExpression.");
            }
        }

        /// <summary>
        /// Unlike the row above, this drives the binding
        /// <see cref="MaterialFactory.BindFillPaintToApplier"/> ACTUALLY creates for a zoom-interpolate
        /// <c>fill-color</c>, not a hand-built <see cref="StyleProperty{CoreColor}"/> — so the alloc-free
        /// claim is measured on the production binding, not a stand-in for it.
        /// </summary>
        [Test]
        public void BindFillPaint_ZoomFillColor_ApplyZoomAllocatesZeroGCMemory()
        {
            Material fillMat = Track(MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load()));
            {
                var paint = TestStyle.FillPaint("{\"fill-color\":[\"interpolate\",[\"linear\"],[\"zoom\"],5,[\"rgb\",255,0,0],15,[\"rgb\",0,0,255]]}");
                Assert.IsTrue(paint.Color.IsZoomDependent, "precondition: fill-color must be Zoom-kind.");

                var applier = new ZoomStyleApplier(fillMat);
                MaterialFactory.BindFillPaintToApplier(paint, applier, fillMat);

                for (int w = 0; w < 20; w++)
                    applier.ApplyZoom(new StyleFrameInputs(5.0 + (w % 10) * 1.0, 1.0, 0.0));

                double sweepZoom = 5.0;
                Assert.That(() =>
                {
                    sweepZoom += 0.1;
                    if (sweepZoom > 15.0) sweepZoom = 5.0;
                    applier.ApplyZoom(new StyleFrameInputs(sweepZoom, 1.0, 0.0));
                }, Is.Not.AllocatingGCMemory(),
                    "MaterialFactory.BindFillPaintToApplier's fill-color binding must not allocate GC memory " +
                    "on the swept-zoom ApplyZoom path.");
            }
        }
        // ── The _Opacity uniform half of data-driven fill-opacity ─────────────────

        /// <summary>
        /// A CONSTANT fill-opacity rides the <c>_Opacity</c> uniform. Pairs with
        /// <c>FillSortKeyAndOpacityTests.ConstantOpacity_IsNotBaked</c>, which asserts the other half (that
        /// it is NOT also baked into vertex alpha) — on its own that test would pass on a build that dropped
        /// fill-opacity entirely, so this is what makes the pair discriminating.
        /// </summary>
        [Test]
        public void BindFillPaint_ConstantOpacity_LandsOnTheOpacityUniform()
        {
            Material fillMat = Track(MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load()));
            var paint = TestStyle.FillPaint(@"{""fill-opacity"": 0.5}");
            var applier = new ZoomStyleApplier(fillMat);
            MaterialFactory.BindFillPaintToApplier(paint, applier, fillMat);
            applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.0));

            Assert.AreEqual(0.5f, fillMat.GetFloat("_Opacity"), 1e-4f,
                "a constant fill-opacity must be bound as the _Opacity uniform.");
        }

        /// <summary>
        /// A DATA-DRIVEN fill-opacity is baked per-feature into the COLOR stream, so <c>_Opacity</c> must be
        /// pinned to 1: the fragment computes <c>alpha *= vColor.a * _Opacity</c>, and leaving the base
        /// material's inherited value there would multiply the opacity in twice. Mirrors the convention
        /// <c>BindLinePaintToApplier</c> uses for data-driven line-width.
        /// </summary>
        [Test]
        public void BindFillPaint_DataDrivenOpacity_PinsTheUniformToOneSoItCannotDoubleApply()
        {
            Material fillMat = Track(MaterialFactory.CreateFillMaterial(MapMaterialSetTestUtil.Load()));
            fillMat.SetFloat("_Opacity", 0.25f); // a stale/inherited value the bind must overwrite

            var paint = TestStyle.FillPaint(@"{""fill-opacity"": [""get"", ""op""]}");
            Assert.IsTrue(paint.Opacity.DependsOnFeature, "precondition: this expression is data-driven");

            var applier = new ZoomStyleApplier(fillMat);
            MaterialFactory.BindFillPaintToApplier(paint, applier, fillMat);
            applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.0));

            Assert.AreEqual(1f, fillMat.GetFloat("_Opacity"), 1e-4f,
                "a data-driven fill-opacity is baked per-feature, so _Opacity must be pinned to 1. " +
                "Any other value double-applies the opacity in the fragment.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolRestyleTests — symbol layers survive the restyle gate and ease colour across a restyle
    // ───────────────────────────────────────────────────────────────────────────────────

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

        // #996633 -> #2288DD: no shared channel, none at 0/1.
        private const float OldR = 0x99 / 255f, OldG = 0x66 / 255f, OldB = 0x33 / 255f;
        private const float NewR = 0x22 / 255f, NewG = 0x88 / 255f, NewB = 0xDD / 255f;

        /// <summary>
        /// A surviving symbol layer's <c>_TextColor</c> uniform must reach the NEW colour after
        /// the transition settles — with <c>Restyle</c> a no-op the uniform
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
                Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0),
                    "drive precondition: a Constant text-color-only change must take the in-place path.");

                set.ApplyZoom(new StyleFrameInputs(8.0, 1.0, StyleTransition.Default.DurationSeconds));

                var symbolLayer = (SymbolRenderLayer)set[0];
                Color c = symbolLayer.WorldTextMaterial.GetColor(Shader.PropertyToID("_TextColor"));
                Assert.That(c.r, Is.EqualTo(NewR).Within(1e-3f), "R must settle at the new colour.");
                Assert.That(c.g, Is.EqualTo(NewG).Within(1e-3f), "G must settle at the new colour.");
                Assert.That(c.b, Is.EqualTo(NewB).Within(1e-3f), "B must settle at the new colour.");
        }

        /// <summary>
        /// Mid-transition, <c>_TextColor</c> must lie STRICTLY between the two endpoints on
        /// every channel — not merely differ from the target. RED-verify: delete
        /// <c>_applier.SetTransition(...)</c> from <c>Restyle</c> — the rebind is not a retarget, so the
        /// target is pushed immediately and the mid value EQUALS the target.
        /// </summary>
        [Test]
        public void SymbolRestyle_MidEase_TextColorLiesBetweenTheEndpoints()
        {
            var oldStyle = SingleSymbolStyle("#996633");
            var newStyle = SingleSymbolStyle("#2288DD");
            using var set = new RenderLayerSet();
            set.Build(oldStyle, 8.0, MapMaterialSetTestUtil.Load());
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

        /// <summary>
        /// <see cref="RenderLayerSet.TransitioningCount"/> is <c>&gt; 0</c> mid-ease and
        /// <c>== 0</c> once settled — a SEPARATE injection from
        /// <see cref="SymbolRestyle_MidEase_TextColorLiesBetweenTheEndpoints"/>'s, because that test's defect
        /// (an immediate snap) would also red this. RED-verify: make <c>SymbolRenderLayer.TransitioningCount</c>
        /// return <c>0</c> unconditionally — the mid-ease clause fires while the mid-ease colour test stays green.
        /// </summary>
        [Test]
        public void SymbolRestyle_TransitioningCount_RisesThenSettles()
        {
            var oldStyle = SingleSymbolStyle("#996633");
            var newStyle = SingleSymbolStyle("#2288DD");
            using var set = new RenderLayerSet();
            set.Build(oldStyle, 8.0, MapMaterialSetTestUtil.Load());
                Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Default, nowSeconds: 0.0));

                double mid = StyleTransition.Default.DurationSeconds / 2.0;
                set.ApplyZoom(new StyleFrameInputs(8.0, 1.0, mid));
                Assert.Greater(set.TransitioningCount, 0, "mid-ease, at least one binding must still be easing.");

                set.ApplyZoom(new StyleFrameInputs(8.0, 1.0, StyleTransition.Default.DurationSeconds));
                Assert.AreEqual(0, set.TransitioningCount, "settled at exactly the transition duration.");
        }

        // ── the symbol applier's own zero-alloc tooth ─────────────────────────────────────────

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
        /// Three symbol layers' <c>ApplyZoom</c> must not allocate GC memory. Needed because the
        /// five allocation teeth build over <c>MinimalStyle()</c> — one fill layer — so their sweep
        /// iterates ZERO symbol bindings and cannot see this path. RED-verify: add a USED allocation inside
        /// <c>SymbolRenderLayer.ApplyZoom</c> (assign a <c>new float[1]</c> to a static sink — an unused
        /// local is dead-store-eliminated and never fires).
        /// </summary>
        [Test]
        public void SymbolLayers_ApplyZoom_DoesNotAllocateGCMemory()
        {
            var style = StyleParser.Parse(ThreeSymbolLayersJson);
            using var set = new RenderLayerSet();
            set.Build(style, 8.0, MapMaterialSetTestUtil.Load());
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
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // LayerFadeViewTests — the fade gate as driven by a real MapViewComponent
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fade gate as driven by a real <see cref="MapViewComponent"/>: the transition it uses is
    /// threaded from the view, and crossing a zoom bound rebuilds nothing.
    /// </summary>
    [TestFixture]
    public class LayerFadeViewTests : BaseTestFixture
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
        /// <c>StyleTransition</c> into it every frame; every other fade tooth passes with a hard-coded default.
        /// At <c>Instant</c> the gate settles in the FIRST <c>ApplyZoom</c>, which the 0.30 s default cannot.
        /// </summary>
        [Test]
        public void GateTransition_IsThreadedFromMapView()
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
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
            }
        }

        /// <summary>
        /// End-to-end, through the real view and the real Entities backend: a layer settled out of its zoom
        /// range submits NO draw item, and a layer mid-fade still submits one. Non-local invariant: the draw
        /// gate is pushed beside <c>RenderLayerSet.ApplyZoom</c>, so the fade and the backend gate agree within
        /// ONE frame. <c>NowSecondsOverride</c> drives the clock, so the mid-fade frame is not a race.
        /// </summary>
        [Test]
        public void GatedLayer_SubmitsNoDraw_WhileAFadingOneStillDoes()
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
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
            }
        }

        /// <summary>
        /// Crossing a layer's <c>minzoom</c> rebuilds no tile and destroys no mesh. Limitation: this is a
        /// design fence with no single-site RED. It fails only if the gate filters
        /// <c>TileManager.ComputeDenseLayerIds</c> on zoom; its green does not show that the gate works.
        /// </summary>
        [Test]
        public void ZoomCrossing_RebuildsNoTile()
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
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
            }
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // RestyleCommitAtomicityTests — commit atomicity over MapView.SetStyle's full-rebuild arm
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    internal class RestyleCommitAtomicityTests : BaseTestFixture
    {
        /// <summary>Fixture-private, so <c>Assert.Throws&lt;ProbeAbort&gt;</c> cannot be satisfied by an
        /// unrelated throw from material validation, the parser, or a decode fault.</summary>
        private sealed class ProbeAbort : System.Exception { }

        // ── Fixtures ──────────────────────────────────────────────────────────────────────────

        /// <summary>Three fill layers over ONE vector source, at slots [a, b, c].</summary>
        private static StyleDocument ThreeFillsAbc() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""a"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 0, 0, 1] } },
                { ""id"": ""b"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 200, 0, 1] } },
                { ""id"": ""c"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 0, 200, 1] } }
            ]
        }");

        /// <summary><see cref="ThreeFillsAbc"/> with layer <c>b</c> given a filter. Mesh-affecting, so
        /// <c>SurvivingLayerGate.LayerSurvives</c> refuses and the REBUILD arm (the only arm with probes) is
        /// taken; the SOURCE set is identical, which is what makes the retry-absorption hazard live. The filter is
        /// <c>["has","NAME"]</c> — every country feature in the sample tile carries NAME, so <c>b</c> stays
        /// drawable and the mesh count does not silently change.</summary>
        private static StyleDocument ThreeFillsAbc_FilterChangedOnB() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""a"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 0, 0, 1] } },
                { ""id"": ""b"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""filter"": [""has"", ""NAME""], ""paint"": { ""fill-color"": [""rgba"", 0, 200, 0, 1] } },
                { ""id"": ""c"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 0, 200, 1] } }
            ]
        }");

        /// <summary>F2 — a fill-extrusion layer BEFORE a fill layer, same source/source-layer. With
        /// <c>MapMaterialSet.FillExtrusionMaterial</c> null, <c>Build</c> skips the extrusion layer.</summary>
        private static StyleDocument ExtrusionThenFillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""extrusion-layer"", ""type"": ""fill-extrusion"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-extrusion-height"": 50 } },
                { ""id"": ""shape-layer"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        /// <summary>A view wired like <c>RestyleHarness.NewRestyleView</c> but over a caller-owned
        /// <see cref="MapMaterialSet"/> the test may mutate — never the committed production asset
        /// <c>WithTestMaterials</c> assigns.</summary>
        private static MapViewComponent NewExtrusionView(MapMaterialSet matSet, out GameObject go)
        {
            go = new GameObject("MapView_MaterialLever");
            var view = go.AddComponent<MapViewComponent>();
            view.Config.MaterialSet = matSet;
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0;
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(SampleTileFixture.Bytes());
            view.View.Camera.SetProperties(RestyleHarness.Cam(10, 10, 4.0));
            view.View.Camera.SyncToCamera();
            return view;
        }

        /// <summary>Installs a probe that throws at exactly <paramref name="target"/>.</summary>
        private static void AbortAt(MapViewComponent view, CommitPhase target)
            => view.View.CommitProbe = phase => { if (phase == target) throw new ProbeAbort(); };

        /// <summary>Live layer materials — slots whose <c>Material</c> is non-null under Unity's fake-null
        /// equality. A plain read of the production slot list, not a derived accessor.</summary>
        private static int LiveMaterialCount(MapViewComponent view)
        {
            int live = 0;
            for (int i = 0; i < view.Layers.Count; i++)
                if (view.Layers[i].Material != null) live++;
            return live;
        }

        // ── the old style stays fully live ────────────────────────────────────────────────────

        /// <summary>An abort at <c>IdentityCommitted</c> must leave the PREVIOUS style fully live —
        /// its layers, materials, baked meshes and cache token. Non-local invariant: this is the only phase
        /// that can assert it, because every later phase runs after <c>Layers.Build</c> destroys the old
        /// materials. Slot ids compare against A's ids captured before the call, not the moved <c>_style</c>.
        /// RED: call <c>Layers.Build</c> when <c>!inPlace</c> just before <c>_style = style;</c>.</summary>
        [Test]
        public void AbortAtIdentityCommit_LeavesTheOldStyleFullyLive()
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc(), "A"));
                RestyleHarness.PumpUntilSettled(view);

                Assert.AreEqual(3, view.Layers.Count,
                    "drive precondition: A's three fill layers must all build.");
                int count = view.Layers.Count;
                var layersBefore    = new IRenderLayer[count];
                var materialsBefore = new Material[count];
                var slotIdsBefore   = new string[count];
                for (int i = 0; i < count; i++)
                {
                    layersBefore[i]    = view.Layers[i];
                    materialsBefore[i] = view.Layers[i].Material;
                    slotIdsBefore[i]   = view.Layers[i].StyleLayer?.Id;
                    Assert.IsFalse(materialsBefore[i] == null,
                        $"drive precondition: slot {i} must hold a live Material before the restyle.");
                }
                Mesh[] meshesBefore = view.GetTileMeshes(RestyleHarness.TrackedTile);
                Assert.IsNotNull(meshesBefore, "drive precondition: the tracked tile must be built.");
                Assert.Greater(meshesBefore.Length, 0,
                    "drive precondition: the tracked tile must hold baked meshes.");
                var backendBefore = view.EntitiesRenderer();
                Assert.IsNotNull(backendBefore,
                    "drive precondition: NewRestyleView leaves Config.Backend at Entities, so this is non-null.");
                var tokenBefore = view.TileManager.CurrentStyle;

                AbortAt(view, CommitPhase.IdentityCommitted);
                Assert.Throws<ProbeAbort>(
                    () => RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc_FilterChangedOnB(), "B")),
                    "the probe must abort the restyle at IdentityCommitted.");
                view.View.CommitProbe = null;

                for (int i = 0; i < count; i++)
                    Assert.IsFalse(materialsBefore[i] == null,
                        $"slot {i}: A's Material must still be ALIVE after an abort at IdentityCommitted.");
                Assert.AreEqual(count, view.Layers.Count,
                    "the layer set must be untouched — the abort precedes Layers.Build.");
                for (int i = 0; i < count; i++)
                {
                    Assert.AreSame(layersBefore[i], view.Layers[i],
                        $"slot {i}: the render layer instance must be the same object.");
                    Assert.AreEqual(slotIdsBefore[i], view.Layers[i].StyleLayer?.Id,
                        $"slot {i}: the slot must still carry A's style layer, not B's.");
                }
                Mesh[] meshesAfter = view.GetTileMeshes(RestyleHarness.TrackedTile);
                Assert.IsNotNull(meshesAfter, "A's tracked tile must still be loaded.");
                Assert.AreEqual(meshesBefore.Length, meshesAfter.Length,
                    "A's baked meshes must still be present, none added or removed.");
                for (int i = 0; i < meshesBefore.Length; i++)
                    Assert.AreSame(meshesBefore[i], meshesAfter[i],
                        $"mesh {i}: A's baked Mesh instance must survive the aborted restyle.");
                Assert.AreSame(backendBefore, view.EntitiesRenderer(),
                    "the backend instance must not be rebuilt by an aborted restyle.");
                Assert.AreEqual(tokenBefore, view.TileManager.CurrentStyle,
                    "the prepared-cache token must still be A's.");
            }
            finally
            {
                view.View.CommitProbe = null;
                view.Teardown();
            }
        }

        // ── an aborted rebuild is not absorbed by the retry ───────────────────────────────────

        /// <summary>An abort at <c>MaterialMemoWritten</c> must not be absorbed by the NEXT call's
        /// in-place gate. The MaterialSet lever is the only one that moves <c>MaterialSnapshot</c> under
        /// byte-identical style content, which makes the memo the deciding conjunct. Clause 5 reads the cache
        /// token, which encodes the layer-numbering fold: red under absorption, green under a real rebuild.
        /// </summary>
        [Test]
        public void AbortAtMaterialMemoWrite_IsNotAbsorbedByTheRetryInPlaceGate()
        {
            var litSet = MapMaterialSetTestUtil.Load();
            var matSet = Track(ScriptableObject.CreateInstance<MapMaterialSet>());
            matSet.FillMaterial          = litSet.FillMaterial;
            matSet.LineMaterial          = litSet.LineMaterial;
            matSet.SymbolTextWorld       = litSet.SymbolTextWorld;
            matSet.FillExtrusionMaterial = litSet.FillMaterial; // assigned — both layers build on first load

            var view = NewExtrusionView(matSet, out var go);
            Track(go);
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A"));
                RestyleHarness.PumpUntilSettled(view);
                Assert.AreEqual(2, view.Layers.Count,
                    "drive precondition: extrusion+fill must both be present, at slots [0,1].");
                var tokenBefore = view.TileManager.CurrentStyle;

                // Same MapMaterialSet reference, one field nulled in place: MaterialSnapshot now differs
                // from the memo, so the next call must take the rebuild arm.
                matSet.FillExtrusionMaterial = null;
                AbortAt(view, CommitPhase.MaterialMemoWritten);
                Assert.Throws<ProbeAbort>(
                    () => RestyleHarness.SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A")),
                    "the probe must abort the restyle at MaterialMemoWritten.");
                view.View.CommitProbe = null;

                // The retry: same content, same id, same (already nulled) material set.
                RestyleHarness.SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A"));

                Assert.AreEqual(1, view.Layers.Count,
                    "clause 4: the extrusion layer must be GONE — the current MapMaterialSet no longer " +
                    "supplies its material, so a real rebuild drops it. Reading 2 means the retry was " +
                    "absorbed by the in-place gate.");
                Assert.AreNotEqual(tokenBefore, view.TileManager.CurrentStyle,
                    "clause 5: the cache token must be rewritten — its layer-numbering fold moved 2 -> 1. " +
                    "An unchanged token means the retry never reached the token write.");
            }
            finally
            {
                view.View.CommitProbe = null;
                view.Teardown();
            }
        }
        /// <summary>An abort at any phase from <c>LayersBuilt</c> on must not be absorbed by the
        /// next call's in-place gate: the live layers are the aborted call's, so the retry would compare the
        /// new document against itself. At <c>LayersBuilt</c> both the token and mesh clauses catch it; at
        /// later phases the token has moved, so the mesh clause catches it alone.
        /// </summary>
        [TestCase(CommitPhase.LayersBuilt)]
        [TestCase(CommitPhase.StyleTokenWritten)]
        [TestCase(CommitPhase.SymbolStyleApplied)]
        public void AbortedRebuild_IsNotAbsorbedByTheRetryInPlaceGate(CommitPhase abortAt)
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc(), "A"));
                RestyleHarness.PumpUntilSettled(view);

                Assert.AreEqual(3, view.Layers.Count,
                    "drive precondition: A's three fill layers must all build.");
                Mesh[] meshesBefore = view.GetTileMeshes(RestyleHarness.TrackedTile);
                Assert.IsNotNull(meshesBefore, "drive precondition: the tracked tile must be built.");
                Assert.Greater(meshesBefore.Length, 0,
                    "drive precondition: the tracked tile must hold baked meshes.");
                foreach (Mesh m in meshesBefore)
                    Assert.IsFalse(m == null, "drive precondition: A's baked meshes must all be alive.");
                var tokenBefore = view.TileManager.CurrentStyle;

                AbortAt(view, abortAt);
                Assert.Throws<ProbeAbort>(
                    () => RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc_FilterChangedOnB(), "B")),
                    $"the probe must abort the restyle at {abortAt}.");
                view.View.CommitProbe = null;

                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc_FilterChangedOnB(), "B"));
                RestyleHarness.PumpUntilSettled(view);

                foreach (Mesh m in meshesBefore)
                    Assert.IsTrue(m == null,
                        $"mesh clause ({abortAt}): every Mesh baked under A must be DESTROYED by the retry's " +
                        "rebuild. A surviving one means the retry was absorbed by the in-place gate and the " +
                        "old geometry is now drawn under the new style.");
                Assert.AreNotEqual(tokenBefore, view.TileManager.CurrentStyle,
                    $"token clause ({abortAt}): the prepared-cache token must have been rewritten by the retry.");

                Assert.IsTrue(view.TryGetBuiltTile(RestyleHarness.TrackedTile),
                    "positive control: the retry must actually rebuild the tracked tile, not merely destroy.");
                Mesh[] meshesAfter = view.GetTileMeshes(RestyleHarness.TrackedTile);
                Assert.IsNotNull(meshesAfter, "positive control: the rebuilt tile must expose meshes.");
                Assert.Greater(meshesAfter.Length, 0, "positive control: the rebuilt tile must be non-empty.");
                foreach (Mesh m in meshesAfter)
                {
                    Assert.IsFalse(m == null, "positive control: every rebuilt Mesh must be alive.");
                    foreach (Mesh old in meshesBefore)
                        Assert.AreNotSame(old, m, "positive control: no rebuilt Mesh may be one of A's.");
                }
            }
            finally
            {
                view.View.CommitProbe = null;
                view.Teardown();
            }
        }

        // ── no husk, no leak, no double-free ──────────────────────────────────────────────────

        /// <summary>An abort inside <c>TileManager.SetSources</c>' per-record teardown loop must leave
        /// no HALF-torn-down record behind (C1), and must leak nothing (C2). <c>SetSources</c> drops each
        /// record from <c>_loaded</c> BEFORE tearing it down, so unreached records stay INTACT and C1 inspects
        /// real entries. Non-obvious why: C2 cannot stand in for C1, because a husk's second
        /// <c>decode.Release()</c> moves no counter and asserts only through <c>System.Diagnostics.Debug</c>.
        /// </summary>
        [Test]
        public void AbortMidRecordTeardown_LeavesNoHuskAndLeaksNothing()
        {
            int  meshBaseline   = RestyleHarness.CountMeshObjects();
            long decodeBaseline = SharedDisposable<IDecodedTile>.DebugLiveCount;

            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            System.Exception teardownFault = null;
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc(), "A"));
                RestyleHarness.PumpUntilSettled(view);

                var loadedBefore = new List<TileId>();
                view.CollectLoadedTileIds(loadedBefore);
                Assert.Greater(loadedBefore.Count, 0,
                    "drive precondition: the teardown loop runs once per LOADED record, so the cover must " +
                    "be non-empty or this tooth is about nothing.");
                Mesh[] meshesBefore = view.GetTileMeshes(RestyleHarness.TrackedTile);
                Assert.IsNotNull(meshesBefore, "drive precondition: the tracked tile must be built.");
                Assert.Greater(meshesBefore.Length, 0,
                    "drive precondition: the tracked tile must hold baked meshes.");
                foreach (Mesh m in meshesBefore)
                    Assert.IsFalse(m == null,
                        "drive precondition: a settled record must expose only LIVE meshes, or C1a cannot " +
                        "tell a destroyed one from a never-built one.");

                int records = 0;
                view.View.CommitProbe = phase =>
                {
                    if (phase == CommitPhase.SourcesTeardownRecord && records++ == 0) throw new ProbeAbort();
                };
                Assert.Throws<ProbeAbort>(
                    () => RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc_FilterChangedOnB(), "B")),
                    "the probe must abort the restyle on the FIRST record teardown.");
                view.View.CommitProbe = null;

                var loadedAfter = new List<TileId>();
                view.CollectLoadedTileIds(loadedAfter);

                // C1a — never half a record: nothing still in _loaded may expose a DESTROYED mesh.
                foreach (TileId id in loadedAfter)
                {
                    Mesh[] meshes = view.GetTileMeshes(id);
                    if (meshes == null) continue;
                    for (int i = 0; i < meshes.Length; i++)
                        Assert.IsFalse(meshes[i] == null,
                            $"C1a: tile {id.Z}/{id.X}/{id.Y} is still in _loaded but its mesh {i} is " +
                            "DESTROYED — a half-torn-down husk a later frame and DoDispose both read.");
                }

                // C1b — some records remain (anti-vacuity for C1a), and strictly fewer than before (the
                // husk shapes C1a cannot see: a null mesh array, or a zero-length one).
                Assert.Greater(loadedAfter.Count, 0,
                    "C1b: the records the abort never reached must still be in _loaded — otherwise C1a " +
                    "quantified over the empty set and this tooth inspected no record at all.");
                Assert.Less(loadedAfter.Count, loadedBefore.Count,
                    "C1b: the aborted teardown must have REMOVED the record it tore down — an unchanged " +
                    "count means it left a husk.");
            }
            finally
            {
                view.View.CommitProbe = null;
                try { view.Teardown(); }
                catch (System.Exception ex) { teardownFault = ex; }
                Object.DestroyImmediate(go);
            }

            // C2 — nothing leaks. Outside the try/finally so a body failure is never masked by these.
            Assert.IsNull(teardownFault,
                $"C2: teardown after an aborted restyle must not throw (got {teardownFault}).");
            Assert.AreEqual(0, SharedDisposable<IDecodedTile>.DebugNegativeObservations,
                "C2: no decode handle may be released more times than it was leased.");
            Assert.AreEqual(decodeBaseline, SharedDisposable<IDecodedTile>.DebugLiveCount,
                "C2: every decode lease taken during this test must be released by teardown.");
            Assert.LessOrEqual(RestyleHarness.CountMeshObjects(), meshBaseline,
                "C2: the mesh count must return to baseline — an aborted restyle must leak no Mesh.");
        }

        // ── the no-blank-window bound ─────────────────────────────────────────────────────────

        /// <summary>Across the rebuild-arm commit sequence, the live layer-material count never drops
        /// below <c>min(N_old, N_new)</c>, a LOWER BOUND at every phase. Both sets are not held at once,
        /// because <c>RenderLayerSet.Build</c> calls <c>ClearLayers()</c> first. Here N_old == N_new == 3.
        /// Clause 1 is the only observer of a <c>Layers.Build(null, …)</c> before the
        /// <c>MaterialMemoWritten</c> probe, whose end state is identical.</summary>
        [Test]
        public void EveryCommitPhase_KeepsAtLeastTheSmallerLiveMaterialCount()
        {
            var view = RestyleHarness.NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc(), "A"));
                RestyleHarness.PumpUntilSettled(view);

                Assert.AreEqual(3, view.Layers.Count,
                    "drive precondition: A's three fill layers must all build.");
                Assert.AreEqual(3, LiveMaterialCount(view),
                    "drive precondition: all three slots must hold a live Material before the restyle.");
                var loaded = new List<TileId>();
                view.CollectLoadedTileIds(loaded);
                Assert.Greater(loaded.Count, 0,
                    "drive precondition: SourcesTeardownRecord fires once per LOADED record, so clause 2 " +
                    "needs a non-empty cover.");

                var seen = new List<(CommitPhase Phase, int Live)>();
                view.View.CommitProbe = phase => seen.Add((phase, LiveMaterialCount(view)));
                RestyleHarness.SpinToCompleted(view.SetStyle(ThreeFillsAbc_FilterChangedOnB(), "B"));
                RestyleHarness.PumpUntilSettled(view);
                view.View.CommitProbe = null;

                // Clause 1 — the bound, at EVERY recorded entry (SourcesTeardownRecord records several).
                foreach (var entry in seen)
                    Assert.GreaterOrEqual(entry.Live, 3,
                        $"no-blank-window: live layer materials fell to {entry.Live} at {entry.Phase} — the " +
                        "commit must never hold fewer than min(N_old, N_new) == 3 live materials.");

                // Clause 2 — anti-vacuity: the probe fired, at every phase the enum declares. A seventh
                // CommitPhase added without a tooth reds here.
                var distinct = new HashSet<CommitPhase>();
                foreach (var entry in seen) distinct.Add(entry.Phase);
                foreach (CommitPhase phase in System.Enum.GetValues(typeof(CommitPhase)))
                    Assert.IsTrue(distinct.Contains(phase),
                        $"anti-vacuity: no probe was recorded at {phase} — clause 1 says nothing about a " +
                        "phase it never observed.");
                Assert.AreEqual(System.Enum.GetValues(typeof(CommitPhase)).Length, distinct.Count,
                    "anti-vacuity: the recorded phases must be exactly the declared CommitPhase set.");

                // Clause 3 — anti-vacuity: the bound is a fact about the commit, not about an empty set.
                Assert.AreEqual(3, view.Layers.Count,
                    "anti-vacuity: the rebuild must end with three slots, so the >= 3 bound is not met " +
                    "trivially by an empty layer set.");
            }
            finally
            {
                view.View.CommitProbe = null;
                view.Teardown();
            }
        }
    }
}
