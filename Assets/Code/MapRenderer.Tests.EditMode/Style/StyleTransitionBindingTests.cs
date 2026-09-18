// Unity EditMode only — real Materials via MaterialFactory / MapMaterialSet, real RenderLayerSet.
// NOT included in Tools/core-tests.
//
// Style-transitions epic, Stage 2 (T1: the transition inside the binding, all-survivors path).
// The applier-level and render-layer-level teeth from the stage plan's §3 — 2-9, 12-15, 18, 20, 22.
// Tooth 1 (materials/meshes/backend survive a real MapView.SetStyle) and tooth 19 (loaded tile meshes
// survive) need the real tile pipeline and live in Tiles/PreparedCacheTests.cs's restyle harness, the
// only fixture in the repo that drives MapView.SetStyle over real loaded tiles. Teeth 10, 11, 16, 17
// (the survivor gate itself) live in RestyleSurvivorGateTests.cs. Tooth 21 (the rendered-pixel gamma
// tooth) extends Visual/PaintColorRenderTests.cs.
//
// Colour fixture, deliberately NON-WHITE and non-symmetric (SSOT §3.3): A = #6699CC (0.4,0.6,0.8),
// B = #CC6633 (0.8,0.4,0.2), both alpha = 1. White is the identity under a double-apply and under a
// missing gamma conversion; equal alphas keep MixPremultiplied exact.
//
// D = StyleTransition duration; clocks are driven by passing NowSeconds straight into
// StyleFrameInputs/SetTransition — never Thread.Sleep (no Thread.Sleep in Unity tests).

using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Unity.Mathematics;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using CoreColor = MapRenderer.Core.Expressions.Color;

namespace MapRenderer.Tests.Style
{
    [TestFixture]
    public class StyleTransitionBindingTests
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
            var set = BuildFillSet(oldStyle);

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
            var set = BuildFillSet(oldStyle);
            const double duration = 1.0;
            set.TryRestyleInPlace(oldStyle, newStyle, new StyleTransition { DurationSeconds = duration }, 0.0);

            // Oracles, not a raw (float) cast: Material.SetColor/GetColor round-trips through a gamma
            // conversion this project does not otherwise invert in C#, so a raw cast can differ from the
            // material read-back by ~1 ULP — noise from the INSTRUMENT, not from ApplyZoom. A fresh build
            // of the SAME style goes through the identical round-trip, so it cancels exactly.
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
            var set = BuildFillSet(oldStyle, initialZoom: 0.0);
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
            var set = BuildFillSet(styleA);
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
            var set = BuildFillSet(oldStyle);
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
            var set = BuildFillSet(oldStyle);
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
            var set = BuildFillSet(oldStyle, initialZoom: 0.0);
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
            var set = BuildFillSet(oldStyle);
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

            // Oracle: a FRESH build straight onto newStyle never transitions (tooth 10's own guarantee) —
            // this is what "the same frame, as today's path" means, and it can't go stale the way a
            // captured-at-a-past-commit snapshot would.
            var oracleSet = BuildFillSet(newStyle);
            Color oracle = oracleSet[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);

            var set = BuildFillSet(oldStyle);
            Assert.IsTrue(set.TryRestyleInPlace(oldStyle, newStyle, StyleTransition.Instant, 0.0),
                "drive precondition: an Instant restyle must still take the in-place path.");
            Color restyled = set[0].Material.GetColor(ShaderProperties.PropertyId.BaseColor);

            AssertApprox(oracle, restyled, "Instant must match a fresh build of the SAME style, same frame.", 0f);
            Assert.AreEqual(0, set.TransitioningCount, "Instant must arm nothing.");
        }

        // ── 13-15. Alloc-free at every phase — the loop the 5 per-commit teeth cannot see ────
        // MinimalStyle() (the 5 existing per-commit teeth) has one Constant fill-color and arms no
        // transition, so those teeth measure a loop that iterates zero entries (stage 1 proved this by
        // putting a live `new float[4]` in it: all five stayed green). These three measure the SAME
        // ZoomStyleApplier.ApplyZoom with >=3 colour + >=2 float + >=1 device-px binding actually easing.

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
            try
            {
                applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.1)); // warm up
                Assert.That(() => applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.5)),
                    Is.Not.AllocatingGCMemory(),
                    "ApplyZoom mid-transition, mixed binding types, must not allocate.");
            }
            finally { Object.DestroyImmediate(mat); }
        }

        [Test]
        public void ApplyZoom_OnTheSettlingFrame_AllocatesNothing()
        {
            var applier = MixedApplier(out Material mat, retarget: true, now: 0.0, duration: 1.0);
            try
            {
                applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 0.9)); // warm up, still transitioning
                Assert.That(() => applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 1.0)), // t crosses 1 HERE
                    Is.Not.AllocatingGCMemory(),
                    "the frame that settles every binding (mutates each Origin to null) must not allocate.");
            }
            finally { Object.DestroyImmediate(mat); }
        }

        [Test]
        public void ApplyZoom_Settled_AllocatesNothing()
        {
            var applier = MixedApplier(out Material mat, retarget: true, now: 0.0, duration: 1.0);
            try
            {
                applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 10.0)); // settle
                Assert.That(() => applier.ApplyZoom(new StyleFrameInputs(0.0, 1.0, 11.0)),
                    Is.Not.AllocatingGCMemory(),
                    "post-settle, non-empty binding lists, must not allocate.");
            }
            finally { Object.DestroyImmediate(mat); }
        }

        // ── 18 / 20. Symbol paint / source changes still take the rebuild path ───────────────
        // (mesh-affecting and data-driven cases are in RestyleSurvivorGateTests.cs; these two need a
        // second Build call — the actual "today's path" MapView takes on refusal — to observe
        // the material reference actually moving.)

        /// <summary>
        /// UMR-147: <c>text-opacity</c> is the still-refusing symbol paint key this tooth's ORIGINAL intent
        /// (a symbol paint change generally takes the rebuild path) now needs — Constant <c>text-color</c>
        /// itself flips to the in-place path (see <see cref="SymbolPaintChange_TakesTheInPlacePath_MaterialSurvives"/>
        /// below), so it can no longer carry this guard. <c>text-opacity</c> ALWAYS bakes into the vertex
        /// stream (<c>SymbolPaint.Opacity</c>), so it is deliberately NOT a gate key (plan §6) — the
        /// Fork-A hazard class, approached from the refusing side. RED-verify: add <c>text-opacity</c> to
        /// <c>SurvivingLayerGate.TransitionablePaintKeys</c> — the gate then wrongly accepts.
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
        /// UMR-147 inverts the OLD verdict on this same fixture shape (a symbol <c>text-color</c> change)
        /// on purpose: a Constant <c>text-color</c> change now takes the IN-PLACE path (its previous
        /// justification — "deliberately not in TransitionablePaintKeys" — is exactly the premise this
        /// stage removes), and gains a NEW clause nothing else in the suite observes: the material
        /// reference must not move, which is what <c>MapView</c>'s skip of <c>Layers.Build</c> depends on.
        /// RED (clause 1): revert <c>SurvivingLayerGate</c>'s two new symbol keys — <c>TryRestyleInPlace</c>
        /// then refuses and the <c>IsTrue</c> fires. Clause 2 has no separate injection:
        /// <see cref="SymbolRenderLayer.WorldTextMaterial"/> has no setter, so no production code path can
        /// reassign it — this assertion pins that structural guarantee, not an injectable regression.
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
                "_TextColor uniform and SymbolRenderLayer.Restyle re-binds it (UMR-147).");
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
            var set = BuildFillSet(oldStyle);
            Material before = set[0].Material;

            // A source's tiles[] url lives on the ROOT, not inside a layer, so SurvivingLayerGate's own
            // root-minus-layers comparison (edit 9) already refuses here — TileManager.SourcesUnchanged
            // (edit 11/12) exists for the narrower case this document pair does NOT hit: a raw `sources`
            // object that stays byte-identical while a url source RESOLVES differently (a TileJSON fetch),
            // which needs a live TileManager to exercise and is out of scope for this fixture. Either
            // conjunct refusing is the observable contract this tooth pins: a source change rebuilds.
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
            var set = BuildFillSet(styleA1);
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
}
