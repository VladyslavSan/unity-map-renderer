// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// S60 — <see cref="StyleProperty{T}"/>: parse, classify, evaluate (uniform + bake), and
    /// GC-allocation gate. Rehomes S11 PaintPropertyEvaluatorTests and S12 DataDrivenPaintEvaluatorTests
    /// onto the single collapsed type.
    ///
    /// Covers:
    ///   1. Constant vs Zoom classification.
    ///   2. Numeric interpolation sampled at stop zooms and a mid-zoom (spec-derived math).
    ///   3. Color interpolation with premultiplied-alpha over zoom.
    ///   4. Feature / Composite input → Evaluate(zoom) throws (deferred to S12 guard).
    ///   5. No-GC sweep gate (Core-side): evaluating a zoom sweep in a tight loop allocates 0 bytes.
    ///   6. Bake-path (Evaluate(zoom,feature)): all ExpressionKinds accepted; no throw.
    ///   7. Data-driven match expression → distinct colors per feature.
    ///   8. Absent property → DefaultValue, Kind == Constant (no JSON round-trip).
    /// </summary>
    [TestFixture]
    public class StylePropertyTests
    {
        // ── Helpers ─────────────────────────────────────────────────────────────────────────────

        private static StyleProperty<float> NumProp(string json)
            => new StyleProperty<float>(MapRenderer.Core.Json.JsonParser.Parse(json), 0f, v => (float)v.AsNumber());

        private static StyleProperty<Color> ColProp(string json)
            => new StyleProperty<Color>(MapRenderer.Core.Json.JsonParser.Parse(json), new Color(0,0,0,1), v => v.AsColorCoerced());

        private static DictionaryFeature MakeFeature(params (string key, string val)[] props)
        {
            var d = new System.Collections.Generic.Dictionary<string, Value>();
            foreach (var (k, v) in props)
                d[k] = Value.String(v);
            return new DictionaryFeature(d);
        }

        // ── 1. Classification ─────────────────────────────────────────────────────────────────

        [Test]
        public void Constant_LiteralNumber_IsConstantKind()
        {
            var prop = NumProp("5.0");
            Assert.AreEqual(ExpressionKind.Constant, prop.Kind);
            Assert.IsFalse(prop.IsZoomDependent);
        }

        [Test]
        public void Zoom_InterpolateWithZoom_IsZoomKind()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,1.0,15,20.0]");
            Assert.AreEqual(ExpressionKind.Zoom, prop.Kind);
            Assert.IsTrue(prop.IsZoomDependent);
        }

        // ── 2. Numeric interpolation ──────────────────────────────────────────────────────────

        [Test]
        public void Zoom_WidthInterpolate_AtLowStop_ReturnsStoValue()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(2.0f, prop.Evaluate(5.0), 1e-6f);
        }

        [Test]
        public void Zoom_WidthInterpolate_AtHighStop_ReturnsHighValue()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(20.0f, prop.Evaluate(15.0), 1e-6f);
        }

        [Test]
        public void Zoom_WidthInterpolate_AtMidZoom_ReturnsLinearInterpolation()
        {
            // At zoom=10, t = (10-5)/(15-5) = 0.5, value = 2.0 + 0.5*(20.0-2.0) = 11.0
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(11.0f, prop.Evaluate(10.0), 1e-4f);
        }

        [Test]
        public void Zoom_WidthInterpolate_BelowFirstStop_ClampsToFirst()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(2.0f, prop.Evaluate(0.0), 1e-6f);
        }

        [Test]
        public void Zoom_WidthInterpolate_AboveLastStop_ClampsToLast()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");
            Assert.AreEqual(20.0f, prop.Evaluate(20.0), 1e-6f);
        }

        // ── 3. Color interpolation with premultiplied-alpha ───────────────────────────────────
        //
        // Same hand-derived values as the retired PaintPropertyEvaluatorTests — byte-identity pin.

        [Test]
        public void Zoom_ColorInterpolate_PremultAlpha_TransparentToOpaque()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",0,0,0,0], 1, [\"rgba\",255,255,255,1]]");

            Color c = prop.Evaluate(0.5);
            double[] rgba = c.ToRgbaArray();

            Assert.AreEqual(255.0, rgba[0], 0.5, "Premult alpha: R at t=0.5 must be 255, not 127.5.");
            Assert.AreEqual(255.0, rgba[1], 0.5, "G must equal R for white.");
            Assert.AreEqual(255.0, rgba[2], 0.5, "B must equal R for white.");
            Assert.AreEqual(0.5,   rgba[3], 1e-6, "Alpha at t=0.5 must be 0.5.");
        }

        [Test]
        public void Zoom_ColorInterpolate_PremultAlpha_DivergenceFromStraightLerp()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",0,0,0,0], 1, [\"rgba\",255,255,255,1]]");

            Color c = prop.Evaluate(0.5);
            double r255 = c.ToRgbaArray()[0];
            Assert.Greater(r255, 200.0,
                "Premult interpolation must give R>200 (vs straight lerp's 127.5) for transparent→opaque.");
        }

        [Test]
        public void Zoom_ColorInterpolate_AlphaOne_ReducesToStraightLerp()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"to-color\", \"#000000\"], 1, [\"to-color\", \"#ffffff\"]]");

            Color c = prop.Evaluate(0.5);
            double[] rgba = c.ToRgbaArray();
            Assert.AreEqual(127.5, rgba[0], 1e-6, "At alpha=1, premult reduces to straight sRGB lerp.");
            Assert.AreEqual(127.5, rgba[1], 1e-6);
            Assert.AreEqual(127.5, rgba[2], 1e-6);
            Assert.AreEqual(1.0,   rgba[3], 1e-9);
        }

        [Test]
        public void Zoom_ColorInterpolate_PartialAlpha_PremultResult()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0, [\"rgba\",200,0,0,0.5], 1, [\"rgba\",0,0,200,1.0]]");

            Color c = prop.Evaluate(0.5);
            double[] rgba = c.ToRgbaArray();

            Assert.AreEqual(0.75, rgba[3], 1e-6, "Alpha must be linear lerp of 0.5 and 1.0 at t=0.5.");
            Assert.AreEqual(66.67, rgba[0], 0.5, "R channel via premult lerp.");
            Assert.AreEqual(133.33, rgba[2], 0.5, "B channel via premult lerp.");
        }

        // ── 4. Feature / Composite → Evaluate(zoom) throws ──────────────────────────────────

        [Test]
        public void FeatureKind_Evaluate_Zoom_Throws()
        {
            // Construction does NOT throw (unlike old PaintPropertyEvaluator).
            var prop = NumProp("[\"get\", \"width\"]");
            Assert.AreEqual(ExpressionKind.Feature, prop.Kind);
            Assert.IsTrue(prop.DependsOnFeature);

            Assert.Throws<ArgumentException>(
                () => prop.Evaluate(0.0),
                "Feature-kind expressions must throw on Evaluate(zoom) — use Evaluate(zoom,feature) instead.");
        }

        [Test]
        public void CompositeKind_Evaluate_Zoom_Throws()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,[\"get\",\"w\"],10,5.0]");
            Assert.AreEqual(ExpressionKind.Composite, prop.Kind);

            Assert.Throws<ArgumentException>(
                () => prop.Evaluate(0.0),
                "Composite-kind expressions must throw on Evaluate(zoom).");
        }

        // ── 5. No-GC sweep gate ──────────────────────────────────────────────────────────────

        [Test]
        public void ZoomInterpolateNumber_SweepingZoom_AllocatesZeroBytes()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,20.0]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "Zoom number interpolation must not allocate any GC heap memory per call.");
        }

        [Test]
        public void ZoomInterpolateColor_SweepingZoom_AllocatesZeroBytes()
        {
            var prop = ColProp(
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "5,[\"rgb\",255,0,0],15,[\"rgb\",0,0,255]]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "Zoom color interpolation must not allocate any GC heap memory per call.");
        }

        [Test]
        public void ZoomInterpolateNumber_WrappedWithNumberAssertion_AllocatesZeroBytes()
        {
            var prop = NumProp("[\"interpolate\",[\"linear\"],[\"number\",[\"zoom\"]],5,2.0,15,20.0]");

            for (int w = 0; w < 50; w++)
                prop.Evaluate(5.0 + (w % 10) * 1.0);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                double zoom = 5.0 + (i % 100) * 0.1;
                prop.Evaluate(zoom);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.AreEqual(0L, after - before,
                "AssertExpression wrapping zoom must not allocate.");
        }

        [Test]
        public void Constant_LiteralNumber_EvaluatesIgnoringZoom()
        {
            var prop = NumProp("7.5");
            Assert.AreEqual(7.5f, prop.Evaluate(0.0), 1e-12f);
            Assert.AreEqual(7.5f, prop.Evaluate(99.0), 1e-12f);
        }

        // ── 6. Bake-path: all ExpressionKinds accepted by Evaluate(zoom, feature) ──────────

        [Test]
        public void AllKinds_BakePath_DoesNotThrow()
        {
            var f = MakeFeature(("width", "5"));

            // Constant (use numeric literal, not string literal)
            var c = NumProp("1.0");
            Assert.DoesNotThrow(() => c.Evaluate(0.0, null));

            // Zoom
            var z = NumProp("[\"interpolate\",[\"linear\"],[\"zoom\"],0,1.0,8,5.0]");
            Assert.DoesNotThrow(() => z.Evaluate(0.0, null));

            // Feature — does NOT throw when using TryEvaluate (bake path)
            var feat = new StyleProperty<float>(
                MapRenderer.Core.Json.JsonParser.Parse("[\"to-number\",[\"get\",\"width\"]]"),
                0f, v => (float)v.AsNumber());
            Assert.DoesNotThrow(() => feat.TryEvaluate(0.0, f, out _));
        }

        // ── 7. Data-driven match → distinct colors per feature ───────────────────────────────

        private const string MatchExpr =
            "[\"match\",[\"get\",\"CONTINENT\"]," +
            "\"Asia\",[\"rgba\",200,50,50,1]," +
            "\"South America\",[\"rgba\",50,50,200,1]," +
            "[\"rgba\",128,128,128,1]]";

        [Test]
        public void FeatureKind_Match_TwoFeatures_DistinctColors()
        {
            var prop = ColProp(MatchExpr);
            Assert.AreEqual(ExpressionKind.Feature, prop.Kind);
            Assert.IsTrue(prop.DependsOnFeature);

            var asia = MakeFeature(("CONTINENT", "Asia"));
            var sam  = MakeFeature(("CONTINENT", "South America"));

            prop.TryEvaluate(0.0, asia, out Color cAsia);
            prop.TryEvaluate(0.0, sam,  out Color cSam);

            Assert.Greater(cAsia.R, cAsia.B + 0.3, "Asia must be reddish.");
            Assert.Greater(cSam.B,  cSam.R  + 0.3, "South America must be bluish.");
            Assert.AreNotEqual(cAsia, cSam, "Two different continents must produce distinct colors.");
        }

        [Test]
        public void FeatureKind_Match_DefaultBranch_IsGray()
        {
            var prop = ColProp(MatchExpr);
            var eur  = MakeFeature(("CONTINENT", "Europe"));

            prop.TryEvaluate(0.0, eur, out Color c);
            Assert.AreEqual(128.0 / 255.0, c.R, 0.01, "Default branch: R must be ~128/255.");
            Assert.AreEqual(128.0 / 255.0, c.G, 0.01);
            Assert.AreEqual(128.0 / 255.0, c.B, 0.01);
        }

        [Test]
        public void MissingProperty_FallsToMatchDefault()
        {
            var prop       = ColProp(MatchExpr);
            var noContinent = new DictionaryFeature();

            prop.TryEvaluate(0.0, noContinent, out Color c);
            Assert.AreEqual(128.0 / 255.0, c.R, 0.01);
            Assert.AreEqual(128.0 / 255.0, c.G, 0.01);
            Assert.AreEqual(128.0 / 255.0, c.B, 0.01);
        }

        // ── 8. Absent property → DefaultValue, Constant kind ────────────────────────────────

        [Test]
        public void AbsentProperty_ConstantCtor_UsesDefaultValue()
        {
            var prop = new StyleProperty<float>(42.5f);
            Assert.AreEqual(ExpressionKind.Constant, prop.Kind);
            Assert.IsFalse(prop.DependsOnFeature);
            Assert.AreEqual(42.5f, prop.Evaluate(0.0), 1e-12f);
            Assert.AreEqual(42.5f, prop.DefaultValue, 1e-12f);
        }

        [Test]
        public void AbsentColorProperty_ConstantCtor_ReturnsDefault()
        {
            var defaultColor = new Color(1f, 0f, 0f, 1f);
            var prop = new StyleProperty<Color>(defaultColor);
            Assert.AreEqual(ExpressionKind.Constant, prop.Kind);
            Color c = prop.Evaluate(0.0);
            Assert.AreEqual(defaultColor.R, c.R, 1e-6f);
            Assert.AreEqual(defaultColor.G, c.G, 1e-6f);
            Assert.AreEqual(defaultColor.B, c.B, 1e-6f);
        }
    }
}
