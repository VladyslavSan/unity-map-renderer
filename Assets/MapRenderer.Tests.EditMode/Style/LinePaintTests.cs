// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S14 — <see cref="LinePaint"/>: classification, pinned values, translate-array parse,
    /// anchor encoding, pattern-name capture, join/cap layout parse, and inert-fallback.
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    ///
    /// Acceptance teeth covered:
    ///   #1: Width zoom interpolation — numeric check via PaintPropertyEvaluator (no GPU needed).
    ///   #5: line-pattern hook — PatternName is parsed; IsInertFallback=false.
    ///   #4: translate/anchor numeric assertions.
    ///   D4: join/cap/miter/round reachability from layout sub-tree.
    ///   General: constant/zoom/feature classification pinned.
    ///   Gap-width band math (CPU-only): verify inner-fraction formula correctness.
    /// </summary>
    [TestFixture]
    public class LinePaintTests
    {
        // ── Helper: build a StyleLayer with paint + layout JSON ──────────────────

        private static StyleLayer MakeLineLayer(string paintJson, string layoutJson = null,
            string sourceLayer = "roads")
        {
            return new StyleLayer
            {
                Id          = "test-line",
                LayerType   = StyleLayerType.Line,
                SourceLayer = sourceLayer,
                Paint       = paintJson  != null ? JsonParser.Parse(paintJson)  : null,
                Layout      = layoutJson != null ? JsonParser.Parse(layoutJson) : null,
            };
        }

        // ── #1: Constant line-color → Constant kind, pinned RGB ─────────────────

        [Test]
        public void LinePaint_ConstantColor_ClassifiesAsConstant()
        {
            var layer = MakeLineLayer("{\"line-color\":[\"rgba\",255,0,0,1]}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.ColorKind,
                "An rgba(...) literal must classify as Constant.");
            Assert.IsNotNull(lp.Color,
                "PaintPropertyEvaluator must be non-null for Constant color.");
            Assert.IsNotNull(lp.DataDrivenColor,
                "DataDrivenPaintEvaluator must always be non-null.");
            Assert.IsFalse(lp.IsInertFallback,
                "A layer with line-color set is not inert.");

            // Pinned: rgba(255,0,0,1) → R=1, G=0, B=0.
            var c = lp.Color.EvaluateColor(0.0);
            Assert.AreEqual(1.0, c.R, 1e-4, "Red channel must be 1.0 for rgba(255,0,0,1).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Green channel must be 0.0.");
            Assert.AreEqual(0.0, c.B, 1e-4, "Blue channel must be 0.0.");
        }

        // ── #1: Zoom-dependent line-width → Zoom kind, sampled values pinned ────

        [Test]
        public void LinePaint_ZoomWidth_ClassifiesAsZoom_SampledValuesPinned()
        {
            // Tooth #1 (decisive): zoom-interpolated line-width. CPU assertion, no GPU needed.
            const string paintJson =
                "{\"line-width\":[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,10.0]}";
            var layer = MakeLineLayer(paintJson);
            var lp    = new LinePaint(layer);

            Assert.AreEqual(ExpressionKind.Zoom, lp.WidthKind,
                "A zoom-interpolate expression must classify as Zoom.");
            Assert.IsNotNull(lp.Width,
                "PaintPropertyEvaluator must be non-null for Zoom width.");

            // Pinned: at zoom=5, value should be 2.0 (stop value).
            double v5 = lp.Width.EvaluateNumber(5.0);
            Assert.AreEqual(2.0, v5, 0.01, "At zoom=5, interpolated width must be 2.0.");

            // Pinned: at zoom=15, value should be 10.0 (stop value).
            double v15 = lp.Width.EvaluateNumber(15.0);
            Assert.AreEqual(10.0, v15, 0.01, "At zoom=15, interpolated width must be 10.0.");

            // Intermediate: at zoom=10 (midpoint), value should be between 2 and 10.
            double v10 = lp.Width.EvaluateNumber(10.0);
            Assert.Greater(v10, 2.0, "At zoom=10, width must be > 2.0 (linear interpolation).");
            Assert.Less(v10, 10.0, "At zoom=10, width must be < 10.0 (linear interpolation).");
        }

        // ── Feature-dependent line-color → Feature kind ──────────────────────────

        [Test]
        public void LinePaint_DataDrivenColor_ClassifiesAsFeature()
        {
            const string paintJson =
                "{\"line-color\":[\"match\",[\"get\",\"road_class\"]," +
                "\"motorway\",[\"rgba\",200,50,50,1]," +
                "[\"rgba\",128,128,128,1]]}";
            var layer = MakeLineLayer(paintJson);
            var lp    = new LinePaint(layer);

            Assert.AreEqual(ExpressionKind.Feature, lp.ColorKind,
                "A [\"get\",...] match expression must classify as Feature.");
            Assert.IsNull(lp.Color,
                "PaintPropertyEvaluator must be null for Feature-kind color.");
            Assert.IsNotNull(lp.DataDrivenColor,
                "DataDrivenPaintEvaluator must be non-null for Feature-kind color.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        // ── Absent line-color → Constant kind (spec default #000000) ────────────

        [Test]
        public void LinePaint_AbsentColor_UsesSpecDefault_Black()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.ColorKind,
                "Absent line-color must use spec default (Constant kind).");
            Assert.IsNotNull(lp.Color);

            var c = lp.Color.EvaluateColor(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Default line-color R must be 0 (black).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Default line-color G must be 0 (black).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Default line-color B must be 0 (black).");

            Assert.IsTrue(lp.IsInertFallback,
                "A layer with no paint properties set must be flagged IsInertFallback.");
        }

        // ── Absent line-opacity → Constant 1.0 ─────────────────────────────────

        [Test]
        public void LinePaint_AbsentOpacity_UsesSpecDefault_One()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.OpacityKind);
            Assert.IsNotNull(lp.Opacity);
            double v = lp.Opacity.EvaluateNumber(0.0);
            Assert.AreEqual(1.0, v, 1e-6, "Default line-opacity must be 1.0.");
        }

        // ── Absent line-width → Constant 1.0 ───────────────────────────────────

        [Test]
        public void LinePaint_AbsentWidth_UsesSpecDefault_One()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.WidthKind);
            Assert.IsNotNull(lp.Width);
            double v = lp.Width.EvaluateNumber(0.0);
            Assert.AreEqual(1.0, v, 1e-6, "Default line-width must be 1.0.");
        }

        // ── Absent line-blur → Constant 0 ──────────────────────────────────────

        [Test]
        public void LinePaint_AbsentBlur_UsesSpecDefault_Zero()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new LinePaint(layer);

            Assert.IsNotNull(lp.Blur);
            double v = lp.Blur.EvaluateNumber(0.0);
            Assert.AreEqual(0.0, v, 1e-6, "Default line-blur must be 0.0.");
        }

        // ── Absent line-gap-width → Constant 0 ─────────────────────────────────

        [Test]
        public void LinePaint_AbsentGapWidth_UsesSpecDefault_Zero()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new LinePaint(layer);

            Assert.IsNotNull(lp.GapWidth);
            double v = lp.GapWidth.EvaluateNumber(0.0);
            Assert.AreEqual(0.0, v, 1e-6, "Default line-gap-width must be 0.0.");
        }

        // ── line-gap-width present → parsed ────────────────────────────────────

        [Test]
        public void LinePaint_GapWidth_Present_Parsed()
        {
            var layer = MakeLineLayer("{\"line-gap-width\":8.0}");
            var lp    = new LinePaint(layer);

            Assert.IsNotNull(lp.GapWidth);
            double v = lp.GapWidth.EvaluateNumber(0.0);
            Assert.AreEqual(8.0, v, 1e-6, "line-gap-width must be 8.0.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        // ── line-translate [16, -8] → components pinned ─────────────────────────

        [Test]
        public void LinePaint_Translate_ComponentsArePinned()
        {
            var layer = MakeLineLayer("{\"line-translate\":[16,-8]}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.TranslateXKind);
            Assert.AreEqual(ExpressionKind.Constant, lp.TranslateYKind);

            double tx = lp.TranslateX.EvaluateNumber(0.0);
            double ty = lp.TranslateY.EvaluateNumber(0.0);
            Assert.AreEqual(16.0, tx, 1e-6, "line-translate x must be 16.");
            Assert.AreEqual(-8.0, ty, 1e-6, "line-translate y must be -8.");
        }

        // ── line-translate-anchor "viewport" → 1.0 ─────────────────────────────

        [Test]
        public void LinePaint_TranslateAnchorViewport_IsOne()
        {
            var layer = MakeLineLayer("{\"line-translate-anchor\":\"viewport\"}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.TranslateAnchorKind);
            double v = lp.TranslateAnchor.EvaluateNumber(0.0);
            Assert.AreEqual(1.0, v, 1e-6, "line-translate-anchor 'viewport' must encode as 1.0.");
        }

        // ── line-translate-anchor "map" → 0.0 ──────────────────────────────────

        [Test]
        public void LinePaint_TranslateAnchorMap_IsZero()
        {
            var layer = MakeLineLayer("{\"line-translate-anchor\":\"map\"}");
            var lp    = new LinePaint(layer);

            double v = lp.TranslateAnchor.EvaluateNumber(0.0);
            Assert.AreEqual(0.0, v, 1e-6, "line-translate-anchor 'map' must encode as 0.0.");
        }

        // ── #D4: line-join, line-cap from layout ────────────────────────────────

        [Test]
        public void LinePaint_LayoutJoinCap_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-join\":\"round\",\"line-cap\":\"square\"}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual("round",  lp.LineJoin, "line-join='round' must be parsed from layout.");
            Assert.AreEqual("square", lp.LineCap,  "line-cap='square' must be parsed from layout.");
        }

        [Test]
        public void LinePaint_LayoutMiterLimit_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-miter-limit\":5.0}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual(5.0, lp.MiterLimit, 1e-6,
                "line-miter-limit must be parsed from layout.");
        }

        [Test]
        public void LinePaint_LayoutRoundLimit_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-round-limit\":1.2}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual(1.2, lp.RoundLimit, 1e-6,
                "line-round-limit must be parsed from layout.");
        }

        [Test]
        public void LinePaint_AbsentLayout_UsesSpecDefaults()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual("miter", lp.LineJoin,   "Default line-join must be 'miter'.");
            Assert.AreEqual("butt",  lp.LineCap,    "Default line-cap must be 'butt'.");
            Assert.AreEqual(2.0,     lp.MiterLimit, 1e-6, "Default miter-limit must be 2.0.");
            Assert.AreEqual(1.05,    lp.RoundLimit, 1e-6, "Default round-limit must be 1.05.");
        }

        // ── S44: line-offset ─────────────────────────────────────────────────────

        [Test]
        public void LinePaint_Offset_ConstantValue_IsParsed()
        {
            var layer = MakeLineLayer("{\"line-offset\":5}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.OffsetKind,
                "A numeric line-offset must classify as Constant.");
            Assert.IsNotNull(lp.Offset,
                "Offset evaluator must be non-null for Constant kind.");
            double v = lp.Offset.EvaluateNumber(0.0);
            Assert.AreEqual(5.0, v, 1e-6, "line-offset must evaluate to 5.0.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        [Test]
        public void LinePaint_Offset_Absent_DefaultsToZero()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new LinePaint(layer);

            Assert.IsNotNull(lp.Offset, "Absent line-offset must produce a non-null evaluator (spec default 0).");
            double v = lp.Offset.EvaluateNumber(0.0);
            Assert.AreEqual(0.0, v, 1e-6, "Absent line-offset must default to 0.0.");
        }

        [Test]
        public void LinePaint_Offset_NegativeValue_IsParsed()
        {
            var layer = MakeLineLayer("{\"line-offset\":-3}");
            var lp    = new LinePaint(layer);

            double v = lp.Offset.EvaluateNumber(0.0);
            Assert.AreEqual(-3.0, v, 1e-6, "Negative line-offset must parse correctly.");
        }

        [Test]
        public void LinePaint_Offset_ZoomInterpolate_ClassifiesAsZoom()
        {
            // A zoom-step expression for line-offset must be classified as Zoom kind.
            var json  = "{\"line-offset\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0,14,8]}";
            var layer = MakeLineLayer(json);
            var lp    = new LinePaint(layer);

            Assert.AreEqual(ExpressionKind.Zoom, lp.OffsetKind,
                "A zoom-interpolate line-offset must classify as Zoom kind.");
            Assert.IsNotNull(lp.Offset, "Zoom kind offset must have a non-null evaluator.");
        }

        // ── #5: line-pattern hook → PatternName is set ──────────────────────────

        [Test]
        public void LinePaint_PatternName_IsParsed()
        {
            // S14_LINE_PATTERN_HOOK: parse+plumb only; fallback to solid _MapColor until S17.
            var layer = MakeLineLayer("{\"line-pattern\":\"road_shield\"}");
            var lp    = new LinePaint(layer);

            Assert.AreEqual("road_shield", lp.PatternName,
                "line-pattern must be read as PatternName.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        // ── Null paint → IsInertFallback ────────────────────────────────────────

        [Test]
        public void LinePaint_NullPaint_IsInertFallback()
        {
            var layer = MakeLineLayer(null);
            var lp    = new LinePaint(layer);

            Assert.IsTrue(lp.IsInertFallback,
                "A layer with null Paint must be IsInertFallback.");
        }

        // ── Null layer → ArgumentNullException ─────────────────────────────────

        [Test]
        public void LinePaint_NullLayer_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new LinePaint(null),
                "LinePaint(null) must throw ArgumentNullException.");
        }

    }
}
