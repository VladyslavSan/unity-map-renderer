// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

// S60: DataDrivenPaintEvaluator deleted; these tests rehomed to StyleProperty<T>.
// Tests preserved verbatim under the same names for pass-count parity.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>
    /// S12 / S60 — Retained for pass-count continuity. Tests the bake path of
    /// <see cref="StyleProperty{T}"/> (Evaluate(zoom, feature) / TryEvaluate), which replaces the
    /// deleted <c>DataDrivenPaintEvaluator</c>. All data-driven + composite coverage is here.
    /// </summary>
    [TestFixture]
    public class DataDrivenPaintEvaluatorTests
    {
        private static StyleProperty<Color> ColProp(string json)
            => new StyleProperty<Color>(
                MapRenderer.Core.Json.JsonParser.Parse(json), new Color(0, 0, 0, 1), v => v.AsColorCoerced());

        private static StyleProperty<float> NumProp(string json)
            => new StyleProperty<float>(
                MapRenderer.Core.Json.JsonParser.Parse(json), 0f, v => (float)v.AsNumber());

        private static DictionaryFeature MakeFeature(params (string key, string val)[] props)
        {
            var d = new Dictionary<string, Value>();
            foreach (var (k, v) in props)
                d[k] = Value.String(v);
            return new DictionaryFeature(d);
        }

        private static DictionaryFeature MakeFeatureNum(params (string key, double val)[] props)
        {
            var d = new Dictionary<string, Value>();
            foreach (var (k, v) in props)
                d[k] = Value.Number(v);
            return new DictionaryFeature(d);
        }

        private const string MatchExpr =
            "[\"match\",[\"get\",\"CONTINENT\"]," +
            "\"Asia\",[\"rgba\",200,50,50,1]," +
            "\"South America\",[\"rgba\",50,50,200,1]," +
            "[\"rgba\",128,128,128,1]]";

        // ── 1. Feature-kind match → distinct colors ───────────────────────────

        [Test]
        public void FeatureKind_Match_TwoFeatures_DistinctColors()
        {
            var prop = ColProp(MatchExpr);
            Assert.AreEqual(ExpressionKind.Feature, prop.Kind, "Match on get() must be Feature kind.");

            var asia = MakeFeature(("CONTINENT", "Asia"));
            var sam  = MakeFeature(("CONTINENT", "South America"));

            prop.TryEvaluate(0.0, asia, out Color cAsia);
            prop.TryEvaluate(0.0, sam,  out Color cSam);

            Assert.Greater(cAsia.R, cAsia.B + 0.3, $"Asia must be reddish (R={cAsia.R:F3}, B={cAsia.B:F3}).");
            Assert.Greater(cSam.B,  cSam.R  + 0.3, $"S.America must be bluish (R={cSam.R:F3}, B={cSam.B:F3}).");
            Assert.AreNotEqual(cAsia, cSam, "Two features with different CONTINENT must have distinct colors.");
        }

        [Test]
        public void FeatureKind_Match_DefaultBranch_IsGray()
        {
            var prop = ColProp(MatchExpr);
            var eur  = MakeFeature(("CONTINENT", "Europe"));

            prop.TryEvaluate(0.0, eur, out Color c);
            Assert.AreEqual(128.0 / 255.0, c.R, 0.01, "Default branch: R must be ~0.502 (128/255).");
            Assert.AreEqual(128.0 / 255.0, c.G, 0.01);
            Assert.AreEqual(128.0 / 255.0, c.B, 0.01);
        }

        // ── 2. Constant-input control → uniform color ─────────────────────────

        [Test]
        public void ConstantControl_SameColorForAllFeatures()
        {
            const string controlExpr =
                "[\"match\",[\"get\",\"__NONEXISTENT__\"]," +
                "\"x\",[\"rgba\",255,0,0,1]," +
                "[\"rgba\",64,64,64,1]]";

            var prop = ColProp(controlExpr);
            var f1 = MakeFeature(("CONTINENT", "Asia"));
            var f2 = MakeFeature(("CONTINENT", "South America"));
            var f3 = new DictionaryFeature();

            prop.TryEvaluate(0.0, f1, out Color c1);
            prop.TryEvaluate(0.0, f2, out Color c2);
            prop.TryEvaluate(0.0, f3, out Color c3);

            Assert.AreEqual(c1, c2, "Constant-control: all features must produce the same color (c1==c2).");
            Assert.AreEqual(c2, c3, "Constant-control: all features must produce the same color (c2==c3).");
            Assert.AreEqual(64.0 / 255.0, c1.R, 0.01, "Control color R must be 64/255.");
        }

        // ── 3. Composite × zoom: interpolate zoom over feature-dependent stops ─

        [Test]
        public void Composite_InterpolateZoom_FeatureColorStops_DistinctAtSameZoom()
        {
            const string compositeExpr =
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0,[\"match\",[\"get\",\"CONTINENT\"],\"Asia\",[\"rgba\",200,50,50,1],[\"rgba\",50,50,200,1]]," +
                "8,[\"match\",[\"get\",\"CONTINENT\"],\"Asia\",[\"rgba\",220,20,20,1],[\"rgba\",20,20,220,1]]]";

            var prop = ColProp(compositeExpr);
            Assert.AreEqual(ExpressionKind.Composite, prop.Kind,
                "Interpolate over zoom with feature-dependent stops must be Composite kind.");

            var asia = MakeFeature(("CONTINENT", "Asia"));
            var sam  = MakeFeature(("CONTINENT", "South America"));

            prop.TryEvaluate(0.0, asia, out Color asiaZ0);
            prop.TryEvaluate(0.0, sam,  out Color samZ0);
            Assert.AreEqual(200.0 / 255.0, asiaZ0.R, 0.01, "Asia at z=0: R must be 200/255.");
            Assert.AreEqual(50.0  / 255.0, asiaZ0.B, 0.01, "Asia at z=0: B must be 50/255.");
            Assert.AreEqual(50.0  / 255.0, samZ0.R,  0.01, "S.America at z=0: R must be 50/255.");
            Assert.AreEqual(200.0 / 255.0, samZ0.B,  0.01, "S.America at z=0: B must be 200/255.");

            prop.TryEvaluate(8.0, asia, out Color asiaZ8);
            prop.TryEvaluate(8.0, sam,  out Color samZ8);
            Assert.AreEqual(220.0 / 255.0, asiaZ8.R, 0.01, "Asia at z=8: R must be 220/255.");
            Assert.AreEqual(20.0  / 255.0, asiaZ8.B, 0.01, "Asia at z=8: B must be 20/255.");
            Assert.AreEqual(20.0  / 255.0, samZ8.R,  0.01, "S.America at z=8: R must be 20/255.");
            Assert.AreEqual(220.0 / 255.0, samZ8.B,  0.01, "S.America at z=8: B must be 220/255.");

            prop.TryEvaluate(4.0, asia, out Color asiaZ4);
            Assert.AreEqual(210.0 / 255.0, asiaZ4.R, 0.01, "Asia at z=4 (mid): R must be 210/255.");
            Assert.AreEqual(35.0  / 255.0, asiaZ4.G, 0.01, "Asia at z=4 (mid): G must be 35/255.");
            Assert.AreEqual(35.0  / 255.0, asiaZ4.B, 0.01, "Asia at z=4 (mid): B must be 35/255.");

            prop.TryEvaluate(4.0, sam, out Color samZ4);
            Assert.AreEqual(35.0  / 255.0, samZ4.R, 0.01, "S.America at z=4: R must be 35/255.");
            Assert.AreEqual(210.0 / 255.0, samZ4.B, 0.01, "S.America at z=4: B must be 210/255.");

            Assert.AreNotEqual(asiaZ4, samZ4, "Composite at zoom 4: Asia and S.America must differ.");
        }

        // ── 4. Data-driven opacity: numeric interpolate over ["get","fid"] ─────

        [Test]
        public void FeatureKind_NumericStep_OpacityPerFeature()
        {
            const string stepExpr = "[\"step\",[\"get\",\"fid\"],0.3,10,0.7,100,1.0]";
            var prop = NumProp(stepExpr);
            Assert.AreEqual(ExpressionKind.Feature, prop.Kind);

            var low  = MakeFeatureNum(("fid", 5.0));
            var mid  = MakeFeatureNum(("fid", 50.0));
            var high = MakeFeatureNum(("fid", 150.0));

            prop.TryEvaluate(0.0, low,  out float opLow);
            prop.TryEvaluate(0.0, mid,  out float opMid);
            prop.TryEvaluate(0.0, high, out float opHigh);

            Assert.AreEqual(0.3f, opLow,  1e-6f, "fid=5 must yield opacity 0.3.");
            Assert.AreEqual(0.7f, opMid,  1e-6f, "fid=50 must yield opacity 0.7.");
            Assert.AreEqual(1.0f, opHigh, 1e-6f, "fid=150 must yield opacity 1.0.");

            Assert.AreNotEqual(opLow, opMid,  "Opacity must vary per feature (low vs mid).");
            Assert.AreNotEqual(opMid, opHigh, "Opacity must vary per feature (mid vs high).");
        }

        // ── 5. All ExpressionKinds accepted — no throw at construction or bake ─

        [Test]
        public void AllKinds_ConstructionDoesNotThrow()
        {
            Assert.DoesNotThrow(() => ColProp("\"#ff0000\""), "Constant-kind must not throw.");
            Assert.DoesNotThrow(
                () => ColProp("[\"interpolate\",[\"linear\"],[\"zoom\"],0,\"#ff0000\",8,\"#0000ff\"]"),
                "Zoom-kind must not throw.");
            Assert.DoesNotThrow(
                () => ColProp("[\"get\",\"width\"]"),
                "Feature-kind must not throw at construction.");
            Assert.DoesNotThrow(
                () => ColProp("[\"interpolate\",[\"linear\"],[\"zoom\"],5,[\"get\",\"w\"],10,\"#ff0000\"]"),
                "Composite-kind must not throw at construction.");
        }

        // ── 6. Missing property → match default ───────────────────────────────

        [Test]
        public void MissingProperty_FallsToMatchDefault()
        {
            var prop       = ColProp(MatchExpr);
            var noContinent = new DictionaryFeature();

            prop.TryEvaluate(0.0, noContinent, out Color c);
            Assert.AreEqual(128.0 / 255.0, c.R, 0.01, "Missing property must fall through to default gray R.");
            Assert.AreEqual(128.0 / 255.0, c.G, 0.01);
            Assert.AreEqual(128.0 / 255.0, c.B, 0.01);
        }
    }
}
