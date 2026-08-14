// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geometry;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using Line = MapRenderer.Core.Style.Line;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// S14 / S60 — <see cref="Line.PaintProperties"/> / <see cref="Line.LayoutProperties"/>:
    /// classification, pinned values, translate-array parse, anchor encoding, pattern-name capture,
    /// join/cap layout parse (now typed enums), and inert-fallback.
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    ///
    /// S60 changes:
    ///   • line-color/opacity/width/blur/gap-width/offset: single <c>StyleProperty&lt;T&gt;</c>
    ///     (no more DataDrivenX / XKind fields; ColorKind etc. are convenience aliases for .Kind).
    ///   • Data-driven gate: null check → <c>.DependsOnFeature</c>.
    ///   • line-translate: ONE <c>StyleProperty&lt;double2&gt;</c>; access via <c>.Translate.Evaluate(0.0).x/y</c>.
    ///   • line-translate-anchor: <c>StyleProperty&lt;float&gt;</c>.
    ///   • line-join / line-cap: <c>JoinType</c> / <c>CapType</c> enums on LayoutProperties.
    /// </summary>
    [TestFixture]
    public class LinePaintTests
    {
        private static StyleLayer MakeLineLayer(string paintJson, string layoutJson = null,
            string sourceLayer = "roads")
        {
            return new StyleLayer
            {
                Id          = "test-line",
                LayerType   = StyleLayerType.Line,
                SourceLayer = sourceLayer,
                PaintJson   = paintJson  != null ? JsonParser.Parse(paintJson)  : null,
                LayoutJson  = layoutJson != null ? JsonParser.Parse(layoutJson) : null,
            };
        }

        // ── #1: Constant line-color → Constant kind, pinned RGB ─────────────────

        [Test]
        public void LinePaint_ConstantColor_ClassifiesAsConstant()
        {
            var layer = MakeLineLayer("{\"line-color\":[\"rgba\",255,0,0,1]}");
            var lp    = new Line.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.ColorKind,
                "An rgba(...) literal must classify as Constant.");
            Assert.IsFalse(lp.Color.DependsOnFeature,
                "Constant color must not depend on feature.");
            Assert.IsFalse(lp.IsInertFallback,
                "A layer with line-color set is not inert.");

            // Pinned: rgba(255,0,0,1) → R=1, G=0, B=0.
            var c = lp.Color.Evaluate(0.0);
            Assert.AreEqual(1.0, c.R, 1e-4, "Red channel must be 1.0 for rgba(255,0,0,1).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Green channel must be 0.0.");
            Assert.AreEqual(0.0, c.B, 1e-4, "Blue channel must be 0.0.");
        }

        // ── #1: Zoom-dependent line-width → Zoom kind, sampled values pinned ────

        [Test]
        public void LinePaint_ZoomWidth_ClassifiesAsZoom_SampledValuesPinned()
        {
            const string paintJson =
                "{\"line-width\":[\"interpolate\",[\"linear\"],[\"zoom\"],5,2.0,15,10.0]}";
            var layer = MakeLineLayer(paintJson);
            var lp    = new Line.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Zoom, lp.WidthKind,
                "A zoom-interpolate expression must classify as Zoom.");
            Assert.IsFalse(lp.Width.DependsOnFeature, "Zoom width must not depend on feature.");

            float v5 = lp.Width.Evaluate(5.0);
            Assert.AreEqual(2.0f, v5, 0.01f, "At zoom=5, interpolated width must be 2.0.");

            float v15 = lp.Width.Evaluate(15.0);
            Assert.AreEqual(10.0f, v15, 0.01f, "At zoom=15, interpolated width must be 10.0.");

            float v10 = lp.Width.Evaluate(10.0);
            Assert.Greater(v10, 2.0f, "At zoom=10, width must be > 2.0.");
            Assert.Less(v10, 10.0f, "At zoom=10, width must be < 10.0.");
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
            var lp    = new Line.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Feature, lp.ColorKind,
                "A [\"get\",...] match expression must classify as Feature.");
            Assert.IsTrue(lp.Color.DependsOnFeature,
                "Data-driven color must DependsOnFeature.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        // ── Absent line-color → Constant kind (spec default #000000) ────────────

        [Test]
        public void LinePaint_AbsentColor_UsesSpecDefault_Black()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new Line.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.ColorKind,
                "Absent line-color must use spec default (Constant kind).");
            Assert.IsFalse(lp.Color.DependsOnFeature);

            var c = lp.Color.Evaluate(0.0);
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
            var lp    = new Line.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.OpacityKind);
            float v = lp.Opacity.Evaluate(0.0);
            Assert.AreEqual(1.0f, v, 1e-6f, "Default line-opacity must be 1.0.");
        }

        // ── Absent line-width → Constant 1.0 ───────────────────────────────────

        [Test]
        public void LinePaint_AbsentWidth_UsesSpecDefault_One()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new Line.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.WidthKind);
            float v = lp.Width.Evaluate(0.0);
            Assert.AreEqual(1.0f, v, 1e-6f, "Default line-width must be 1.0.");
        }

        // ── Absent line-blur → Constant 0 ──────────────────────────────────────

        [Test]
        public void LinePaint_AbsentBlur_UsesSpecDefault_Zero()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new Line.PaintProperties(layer);

            float v = lp.Blur.Evaluate(0.0);
            Assert.AreEqual(0.0f, v, 1e-6f, "Default line-blur must be 0.0.");
        }

        // ── Absent line-gap-width → Constant 0 ─────────────────────────────────

        [Test]
        public void LinePaint_AbsentGapWidth_UsesSpecDefault_Zero()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new Line.PaintProperties(layer);

            float v = lp.GapWidth.Evaluate(0.0);
            Assert.AreEqual(0.0f, v, 1e-6f, "Default line-gap-width must be 0.0.");
        }

        // ── line-gap-width present → parsed ────────────────────────────────────

        [Test]
        public void LinePaint_GapWidth_Present_Parsed()
        {
            var layer = MakeLineLayer("{\"line-gap-width\":8.0}");
            var lp    = new Line.PaintProperties(layer);

            float v = lp.GapWidth.Evaluate(0.0);
            Assert.AreEqual(8.0f, v, 1e-6f, "line-gap-width must be 8.0.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        // ── line-translate [16, -8] → double2 pinned ───────────────────────────

        [Test]
        public void LinePaint_Translate_ComponentsArePinned()
        {
            var layer = MakeLineLayer("{\"line-translate\":[16,-8]}");
            var lp    = new Line.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.TranslateKind);
            var t = lp.Translate.Evaluate(0.0);
            Assert.AreEqual(16.0, t.x, 1e-6, "line-translate x must be 16.");
            Assert.AreEqual(-8.0, t.y, 1e-6, "line-translate y must be -8.");
        }

        // ── line-translate-anchor "viewport" → 1.0 ─────────────────────────────

        [Test]
        public void LinePaint_TranslateAnchorViewport_IsOne()
        {
            var layer = MakeLineLayer("{\"line-translate-anchor\":\"viewport\"}");
            var lp    = new Line.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.TranslateAnchorKind);
            float v = lp.TranslateAnchor.Evaluate(0.0);
            Assert.AreEqual(1.0f, v, 1e-6f, "line-translate-anchor 'viewport' must encode as 1.0.");
        }

        // ── line-translate-anchor "map" → 0.0 ──────────────────────────────────

        [Test]
        public void LinePaint_TranslateAnchorMap_IsZero()
        {
            var layer = MakeLineLayer("{\"line-translate-anchor\":\"map\"}");
            var lp    = new Line.PaintProperties(layer);

            float v = lp.TranslateAnchor.Evaluate(0.0);
            Assert.AreEqual(0.0f, v, 1e-6f, "line-translate-anchor 'map' must encode as 0.0.");
        }

        // ── #D4: line-join, line-cap from layout (now typed enums) ──────────────

        [Test]
        public void LinePaint_LayoutJoinCap_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-join\":\"round\",\"line-cap\":\"square\"}");
            var lo    = new Line.LayoutProperties(layer);

            Assert.AreEqual(JoinType.Round,  lo.Join, "line-join='round' must parse to JoinType.Round.");
            Assert.AreEqual(CapType.Square, lo.Cap,  "line-cap='square' must parse to CapType.Square.");
        }

        [Test]
        public void LinePaint_LayoutJoinBevel_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-join\":\"bevel\"}");
            var lo    = new Line.LayoutProperties(layer);

            Assert.AreEqual(JoinType.Bevel, lo.Join, "line-join='bevel' must parse to JoinType.Bevel.");
        }

        [Test]
        public void LinePaint_LayoutCapRound_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-cap\":\"round\"}");
            var lo    = new Line.LayoutProperties(layer);

            Assert.AreEqual(CapType.Round, lo.Cap, "line-cap='round' must parse to CapType.Round.");
        }

        [Test]
        public void LinePaint_LayoutMiterLimit_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-miter-limit\":5.0}");
            var lo    = new Line.LayoutProperties(layer);

            Assert.AreEqual(5.0, lo.MiterLimit, 1e-6, "line-miter-limit must be parsed from layout.");
        }

        [Test]
        public void LinePaint_LayoutRoundLimit_Parsed()
        {
            var layer = MakeLineLayer("{}", "{\"line-round-limit\":1.2}");
            var lo    = new Line.LayoutProperties(layer);

            Assert.AreEqual(1.2, lo.RoundLimit, 1e-6, "line-round-limit must be parsed from layout.");
        }

        [Test]
        public void LinePaint_AbsentLayout_UsesSpecDefaults()
        {
            var layer = MakeLineLayer("{}");
            var lo    = new Line.LayoutProperties(layer);

            Assert.AreEqual(JoinType.Miter, lo.Join,       "Default line-join must be JoinType.Miter.");
            Assert.AreEqual(CapType.Butt,   lo.Cap,        "Default line-cap must be CapType.Butt.");
            Assert.AreEqual(2.0,            lo.MiterLimit, 1e-6, "Default miter-limit must be 2.0.");
            Assert.AreEqual(1.05,           lo.RoundLimit, 1e-6, "Default round-limit must be 1.05.");
        }

        // ── S44: line-offset ─────────────────────────────────────────────────────

        [Test]
        public void LinePaint_Offset_ConstantValue_IsParsed()
        {
            var layer = MakeLineLayer("{\"line-offset\":5}");
            var lp    = new Line.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, lp.OffsetKind,
                "A numeric line-offset must classify as Constant.");
            float v = lp.Offset.Evaluate(0.0);
            Assert.AreEqual(5.0f, v, 1e-6f, "line-offset must evaluate to 5.0.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        [Test]
        public void LinePaint_Offset_Absent_DefaultsToZero()
        {
            var layer = MakeLineLayer("{}");
            var lp    = new Line.PaintProperties(layer);

            float v = lp.Offset.Evaluate(0.0);
            Assert.AreEqual(0.0f, v, 1e-6f, "Absent line-offset must default to 0.0.");
        }

        [Test]
        public void LinePaint_Offset_NegativeValue_IsParsed()
        {
            var layer = MakeLineLayer("{\"line-offset\":-3}");
            var lp    = new Line.PaintProperties(layer);

            float v = lp.Offset.Evaluate(0.0);
            Assert.AreEqual(-3.0f, v, 1e-6f, "Negative line-offset must parse correctly.");
        }

        [Test]
        public void LinePaint_Offset_ZoomInterpolate_ClassifiesAsZoom()
        {
            var json  = "{\"line-offset\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0,14,8]}";
            var layer = MakeLineLayer(json);
            var lp    = new Line.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Zoom, lp.OffsetKind,
                "A zoom-interpolate line-offset must classify as Zoom kind.");
        }

        // ── #5: line-pattern hook → PatternName is set ──────────────────────────

        [Test]
        public void LinePaint_PatternName_IsParsed()
        {
            var layer = MakeLineLayer("{\"line-pattern\":\"road_shield\"}");
            var lp    = new Line.PaintProperties(layer);

            Assert.AreEqual("road_shield", lp.PatternName, "line-pattern must be read as PatternName.");
            Assert.IsFalse(lp.IsInertFallback);
        }

        // ── Null paint → IsInertFallback ────────────────────────────────────────

        [Test]
        public void LinePaint_NullPaint_IsInertFallback()
        {
            var layer = MakeLineLayer(null);
            var lp    = new Line.PaintProperties(layer);

            Assert.IsTrue(lp.IsInertFallback, "A layer with null Paint must be IsInertFallback.");
        }

        // ── Null layer → ArgumentNullException ─────────────────────────────────

        [Test]
        public void LinePaint_NullLayer_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => new Line.PaintProperties((StyleLayer)null),
                "PaintProperties((StyleLayer)null) must throw ArgumentNullException.");
        }

        // ── S60: PropertyNames value constants ──────────────────────────────────

        [Test]
        public void PropertyNames_ValueConstants_AreCorrect()
        {
            Assert.AreEqual("butt",   Line.PropertyNames.CapButt);
            Assert.AreEqual("round",  Line.PropertyNames.CapRound);
            Assert.AreEqual("square", Line.PropertyNames.CapSquare);
            Assert.AreEqual("miter",  Line.PropertyNames.JoinMiter);
            Assert.AreEqual("round",  Line.PropertyNames.JoinRound);
            Assert.AreEqual("bevel",  Line.PropertyNames.JoinBevel);
        }
    }
}
