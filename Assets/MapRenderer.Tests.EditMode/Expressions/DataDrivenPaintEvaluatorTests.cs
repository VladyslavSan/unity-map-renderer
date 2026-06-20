// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>
    /// S12 — <see cref="DataDrivenPaintEvaluator"/>: data-driven and composite expression evaluation.
    ///
    /// Covers:
    ///   1. Pure Feature-kind: match on a feature property produces distinct colors per feature.
    ///   2. Constant-input control: same expression shape, input that resolves the same for all
    ///      features → identical colors (proves the bake path, not just parse success).
    ///   3. Composite × zoom: interpolate over zoom with feature-dependent color stops — evaluated at
    ///      multiple (zoom, feature) pairs, asserted against hand-computed spec values.
    ///   4. Data-driven numeric opacity: interpolate over ["get","fid"] → per-feature distinct numbers.
    ///   5. All ExpressionKinds accepted (no throw for Feature / Composite kinds at construction).
    ///   6. Missing property falls through to match default.
    /// </summary>
    [TestFixture]
    public class DataDrivenPaintEvaluatorTests
    {
        // ── Helpers — synthetic features ─────────────────────────────────────────

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

        // sRGB red and blue colors for the match expression:
        //   Asia     → rgba(200, 50, 50,  1)   (reddish)
        //   S.America → rgba(50,  50, 200, 1)  (bluish)
        //   default  → rgba(128, 128, 128, 1)  (gray)
        private const string MatchExpr =
            "[\"match\",[\"get\",\"CONTINENT\"]," +
            "\"Asia\",[\"rgba\",200,50,50,1]," +
            "\"South America\",[\"rgba\",50,50,200,1]," +
            "[\"rgba\",128,128,128,1]]";

        // ── 1. Feature-kind match → distinct colors ───────────────────────────

        [Test]
        public void FeatureKind_Match_TwoFeatures_DistinctColors()
        {
            var ev = new DataDrivenPaintEvaluator(MatchExpr);
            Assert.AreEqual(ExpressionKind.Feature, ev.Kind, "Match on get() must be Feature kind.");

            var asia   = MakeFeature(("CONTINENT", "Asia"));
            var sam    = MakeFeature(("CONTINENT", "South America"));

            Color cAsia = ev.EvaluateColor(0.0, asia);
            Color cSam  = ev.EvaluateColor(0.0, sam);

            // Asia → reddish: R > B
            Assert.Greater(cAsia.R, cAsia.B + 0.3,
                $"Asia feature must produce a reddish color (R={cAsia.R:F3}, B={cAsia.B:F3}).");

            // S.America → bluish: B > R
            Assert.Greater(cSam.B, cSam.R + 0.3,
                $"South America feature must produce a bluish color (R={cSam.R:F3}, B={cSam.B:F3}).");

            // Must be distinct (the core tooth: ≥2 features render in distinct colors)
            Assert.AreNotEqual(cAsia, cSam,
                "Two features with different CONTINENT must evaluate to distinct colors.");
        }

        [Test]
        public void FeatureKind_Match_DefaultBranch_IsGray()
        {
            var ev  = new DataDrivenPaintEvaluator(MatchExpr);
            var eur = MakeFeature(("CONTINENT", "Europe")); // not in match arms

            Color c = ev.EvaluateColor(0.0, eur);

            // Default → rgba(128,128,128,1) → r≈g≈b≈0.502
            Assert.AreEqual(128.0 / 255.0, c.R, 0.01, "Default branch: R must be ~0.502 (128/255).");
            Assert.AreEqual(128.0 / 255.0, c.G, 0.01, "Default branch: G must be ~0.502 (128/255).");
            Assert.AreEqual(128.0 / 255.0, c.B, 0.01, "Default branch: B must be ~0.502 (128/255).");
        }

        // ── 2. Constant-input control → uniform color ─────────────────────────

        [Test]
        public void ConstantControl_SameColorForAllFeatures()
        {
            // Match on a key that exists on no feature → always falls through to default.
            // Same expression shape (Feature kind), but result is uniform across all features.
            const string controlExpr =
                "[\"match\",[\"get\",\"__NONEXISTENT__\"]," +
                "\"x\",[\"rgba\",255,0,0,1]," +
                "[\"rgba\",64,64,64,1]]";

            var ev = new DataDrivenPaintEvaluator(controlExpr);

            var f1 = MakeFeature(("CONTINENT", "Asia"));
            var f2 = MakeFeature(("CONTINENT", "South America"));
            var f3 = new DictionaryFeature(); // empty props

            Color c1 = ev.EvaluateColor(0.0, f1);
            Color c2 = ev.EvaluateColor(0.0, f2);
            Color c3 = ev.EvaluateColor(0.0, f3);

            Assert.AreEqual(c1, c2, "Constant-control: all features must produce the same color (c1==c2).");
            Assert.AreEqual(c2, c3, "Constant-control: all features must produce the same color (c2==c3).");
            // All gray (64/255 ≈ 0.251)
            Assert.AreEqual(64.0 / 255.0, c1.R, 0.01, "Control color R must be 64/255.");
        }

        // ── 3. Composite × zoom: interpolate zoom over feature-dependent stops ─

        [Test]
        public void Composite_InterpolateZoom_FeatureColorStops_DistinctAtSameZoom()
        {
            // ["interpolate",["linear"],["zoom"],
            //   0, ["match",["get","CONTINENT"],"Asia",[rgba(200,50,50,1)],[rgba(50,50,200,1)]],
            //   8, ["match",["get","CONTINENT"],"Asia",[rgba(220,20,20,1)],[rgba(20,20,220,1)]]
            // ]
            // Composite kind: depends on both zoom AND feature.
            const string compositeExpr =
                "[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "0,[\"match\",[\"get\",\"CONTINENT\"],\"Asia\",[\"rgba\",200,50,50,1],[\"rgba\",50,50,200,1]]," +
                "8,[\"match\",[\"get\",\"CONTINENT\"],\"Asia\",[\"rgba\",220,20,20,1],[\"rgba\",20,20,220,1]]]";

            var ev = new DataDrivenPaintEvaluator(compositeExpr);
            Assert.AreEqual(ExpressionKind.Composite, ev.Kind,
                "Interpolate over zoom with feature-dependent stops must be Composite kind.");

            var asia = MakeFeature(("CONTINENT", "Asia"));
            var sam  = MakeFeature(("CONTINENT", "South America"));

            // At zoom 0: Asia → (200,50,50)/255, S.America → (50,50,200)/255
            Color asiaZ0 = ev.EvaluateColor(0.0, asia);
            Color samZ0  = ev.EvaluateColor(0.0, sam);
            Assert.AreEqual(200.0 / 255.0, asiaZ0.R, 0.01, "Asia at z=0: R must be 200/255.");
            Assert.AreEqual(50.0  / 255.0, asiaZ0.B, 0.01, "Asia at z=0: B must be 50/255.");
            Assert.AreEqual(50.0  / 255.0, samZ0.R,  0.01, "S.America at z=0: R must be 50/255.");
            Assert.AreEqual(200.0 / 255.0, samZ0.B,  0.01, "S.America at z=0: B must be 200/255.");

            // At zoom 8: Asia → (220,20,20)/255, S.America → (20,20,220)/255
            Color asiaZ8 = ev.EvaluateColor(8.0, asia);
            Color samZ8  = ev.EvaluateColor(8.0, sam);
            Assert.AreEqual(220.0 / 255.0, asiaZ8.R, 0.01, "Asia at z=8: R must be 220/255.");
            Assert.AreEqual(20.0  / 255.0, asiaZ8.B, 0.01, "Asia at z=8: B must be 20/255.");
            Assert.AreEqual(20.0  / 255.0, samZ8.R,  0.01, "S.America at z=8: R must be 20/255.");
            Assert.AreEqual(220.0 / 255.0, samZ8.B,  0.01, "S.America at z=8: B must be 220/255.");

            // At zoom 4 (midpoint, t=0.5 with linear interpolation):
            // Asia stops: (200/255, 50/255, 50/255, 1.0) and (220/255, 20/255, 20/255, 1.0)
            // Both alpha=1, so premult reduces to straight lerp.
            // R = 200/255*0.5 + 220/255*0.5 = 210/255 ≈ 0.8235
            // G = 50/255*0.5  + 20/255*0.5  = 35/255  ≈ 0.1373
            // B = 50/255*0.5  + 20/255*0.5  = 35/255  ≈ 0.1373
            Color asiaZ4 = ev.EvaluateColor(4.0, asia);
            Assert.AreEqual(210.0 / 255.0, asiaZ4.R, 0.01,
                "Asia at z=4 (mid): R must be lerp(200,220)/255 = 210/255.");
            Assert.AreEqual(35.0  / 255.0, asiaZ4.G, 0.01,
                "Asia at z=4 (mid): G must be lerp(50,20)/255 = 35/255.");
            Assert.AreEqual(35.0  / 255.0, asiaZ4.B, 0.01,
                "Asia at z=4 (mid): B must be lerp(50,20)/255 = 35/255.");

            // S.America at zoom 4:
            // R = lerp(50,20)/255 = 35/255, B = lerp(200,220)/255 = 210/255
            Color samZ4 = ev.EvaluateColor(4.0, sam);
            Assert.AreEqual(35.0  / 255.0, samZ4.R, 0.01,
                "S.America at z=4 (mid): R must be 35/255.");
            Assert.AreEqual(210.0 / 255.0, samZ4.B, 0.01,
                "S.America at z=4 (mid): B must be 210/255.");

            // The non-separable composite: two features, same zoom → still distinct colors.
            Assert.AreNotEqual(asiaZ4, samZ4, "Composite at zoom 4: Asia and S.America must differ.");
        }

        // ── 4. Data-driven opacity: numeric interpolate over ["get","fid"] ─────

        [Test]
        public void FeatureKind_NumericStep_OpacityPerFeature()
        {
            // ["step", ["get","fid"], 0.3, 10, 0.7, 100, 1.0]
            // fid < 10 → 0.3, fid in [10,100) → 0.7, fid >= 100 → 1.0
            const string stepExpr =
                "[\"step\",[\"get\",\"fid\"],0.3,10,0.7,100,1.0]";

            var ev = new DataDrivenPaintEvaluator(stepExpr);
            Assert.AreEqual(ExpressionKind.Feature, ev.Kind);

            var low  = MakeFeatureNum(("fid", 5.0));
            var mid  = MakeFeatureNum(("fid", 50.0));
            var high = MakeFeatureNum(("fid", 150.0));

            double opLow  = ev.EvaluateNumber(0.0, low);
            double opMid  = ev.EvaluateNumber(0.0, mid);
            double opHigh = ev.EvaluateNumber(0.0, high);

            Assert.AreEqual(0.3, opLow,  1e-9, "fid=5 must yield opacity 0.3 (below first stop).");
            Assert.AreEqual(0.7, opMid,  1e-9, "fid=50 must yield opacity 0.7 (between stops).");
            Assert.AreEqual(1.0, opHigh, 1e-9, "fid=150 must yield opacity 1.0 (above last stop).");

            // Per-feature variation — three distinct values.
            Assert.AreNotEqual(opLow, opMid,  "Opacity must vary per feature (low vs mid).");
            Assert.AreNotEqual(opMid, opHigh, "Opacity must vary per feature (mid vs high).");
        }

        // ── 5. All ExpressionKinds accepted — no throw ────────────────────────

        [Test]
        public void AllKinds_ConstructionDoesNotThrow()
        {
            // Constant
            Assert.DoesNotThrow(() => new DataDrivenPaintEvaluator("\"#ff0000\""),
                "Constant-kind must not throw.");

            // Zoom
            Assert.DoesNotThrow(
                () => new DataDrivenPaintEvaluator(
                    "[\"interpolate\",[\"linear\"],[\"zoom\"],0,\"#ff0000\",8,\"#0000ff\"]"),
                "Zoom-kind must not throw.");

            // Feature
            Assert.DoesNotThrow(
                () => new DataDrivenPaintEvaluator("[\"get\",\"width\"]"),
                "Feature-kind must not throw.");

            // Composite
            Assert.DoesNotThrow(
                () => new DataDrivenPaintEvaluator(
                    "[\"interpolate\",[\"linear\"],[\"zoom\"],5,[\"get\",\"w\"],10,5.0]"),
                "Composite-kind must not throw.");
        }

        // ── 6. Missing property → match default (no crash) ───────────────────

        [Test]
        public void MissingProperty_FallsToMatchDefault()
        {
            var ev = new DataDrivenPaintEvaluator(MatchExpr);
            var noContinent = new DictionaryFeature(); // empty props — no CONTINENT key

            // Should not throw, should return the default gray
            Color c = ev.EvaluateColor(0.0, noContinent);
            Assert.AreEqual(128.0 / 255.0, c.R, 0.01, "Missing property must fall through to default gray R.");
            Assert.AreEqual(128.0 / 255.0, c.G, 0.01, "Missing property must fall through to default gray G.");
            Assert.AreEqual(128.0 / 255.0, c.B, 0.01, "Missing property must fall through to default gray B.");
        }
    }
}
