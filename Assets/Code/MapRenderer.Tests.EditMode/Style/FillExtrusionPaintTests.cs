// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests. Deliberately has
// NO MvtDecoder/TestDecodedTiles dependency (unlike FillPaintTests) so it can run in the fast dotnet loop.
//
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// S23 I1 — <see cref="FillExtrusion.PaintProperties"/>: classification + spec defaults, mirroring
    /// <c>FillPaintTests</c>. Also pins <see cref="StyleParser"/>'s <c>"fill-extrusion"</c> dispatch to the
    /// typed <see cref="FillExtrusion.StyleLayer"/> subclass.
    /// </summary>
    [TestFixture]
    public class FillExtrusionPaintTests
    {
        private static StyleLayer MakeLayer(string paintJson, string sourceLayer = "buildings")
        {
            return new StyleLayer
            {
                Id          = "test-fill-extrusion",
                LayerType   = StyleLayerType.FillExtrusion,
                SourceLayer = sourceLayer,
                PaintJson   = paintJson != null ? JsonParser.Parse(paintJson) : null,
            };
        }

        // ── height ────────────────────────────────────────────────────────────

        [Test]
        public void Height_ConstantValue_ClassifiesAsConstantAndPins()
        {
            var layer = MakeLayer("{\"fill-extrusion-height\":42}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.HeightKind, "a numeric literal must classify as Constant.");
            Assert.AreEqual(42f, fp.Height.Evaluate(0.0), 1e-4f, "fill-extrusion-height must pin to its literal value.");
            Assert.IsFalse(fp.IsInertFallback, "a layer with fill-extrusion-height set is not inert.");
        }

        [Test]
        public void Height_Absent_UsesSpecDefault_Zero()
        {
            var layer = MakeLayer("{}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.HeightKind, "absent fill-extrusion-height must use spec default (Constant kind).");
            Assert.AreEqual(0f, fp.Height.Evaluate(0.0), 1e-6f, "default fill-extrusion-height must be 0.");
            Assert.IsTrue(fp.IsInertFallback, "a layer with no paint properties set must be flagged IsInertFallback.");
        }

        [Test]
        public void Height_ZoomExpression_ClassifiesAsZoom()
        {
            const string paintJson =
                "{\"fill-extrusion-height\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0,16,50]}";
            var layer = MakeLayer(paintJson);
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Zoom, fp.HeightKind, "a zoom-interpolate expression must classify as Zoom.");
            Assert.AreEqual(0f, fp.Height.Evaluate(10.0), 0.01f);
            Assert.AreEqual(50f, fp.Height.Evaluate(16.0), 0.01f);
        }

        [Test]
        public void Height_DataDriven_ClassifiesAsFeature()
        {
            const string paintJson = "{\"fill-extrusion-height\":[\"get\",\"height\"]}";
            var layer = MakeLayer(paintJson);
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Feature, fp.HeightKind, "a [\"get\",...] expression must classify as Feature.");
            Assert.IsTrue(fp.Height.DependsOnFeature);
        }

        // ── base ──────────────────────────────────────────────────────────────

        [Test]
        public void Base_ConstantValue_ClassifiesAsConstantAndPins()
        {
            var layer = MakeLayer("{\"fill-extrusion-base\":5}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.BaseKind);
            Assert.AreEqual(5f, fp.Base.Evaluate(0.0), 1e-4f, "fill-extrusion-base must pin to its literal value.");
        }

        [Test]
        public void Base_Absent_UsesSpecDefault_Zero()
        {
            var layer = MakeLayer("{}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(0f, fp.Base.Evaluate(0.0), 1e-6f, "default fill-extrusion-base must be 0.");
        }

        [Test]
        public void Base_ZoomExpression_ClassifiesAsZoom()
        {
            const string paintJson =
                "{\"fill-extrusion-base\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0,16,4]}";
            var layer = MakeLayer(paintJson);
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Zoom, fp.BaseKind, "a zoom-interpolate expression must classify as Zoom.");
            Assert.AreEqual(0f, fp.Base.Evaluate(10.0), 0.01f);
            Assert.AreEqual(4f, fp.Base.Evaluate(16.0), 0.01f);
        }

        [Test]
        public void Base_DataDriven_ClassifiesAsFeature()
        {
            const string paintJson = "{\"fill-extrusion-base\":[\"get\",\"min_height\"]}";
            var layer = MakeLayer(paintJson);
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Feature, fp.BaseKind, "a [\"get\",...] expression must classify as Feature.");
            Assert.IsTrue(fp.Base.DependsOnFeature);
        }

        // ── color ─────────────────────────────────────────────────────────────

        [Test]
        public void Color_Constant_ClassifiesAsConstantAndPins()
        {
            var layer = MakeLayer("{\"fill-extrusion-color\":[\"rgba\",200,100,50,1]}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.ColorKind);
            var c = fp.Color.Evaluate(0.0);
            Assert.AreEqual(200.0 / 255.0, c.R, 1e-3, "Red channel must match rgba(200,100,50,1).");
            Assert.AreEqual(100.0 / 255.0, c.G, 1e-3, "Green channel must match rgba(200,100,50,1).");
            Assert.AreEqual(50.0 / 255.0, c.B, 1e-3, "Blue channel must match rgba(200,100,50,1).");
        }

        [Test]
        public void Color_Absent_UsesSpecDefault_Black()
        {
            var layer = MakeLayer("{}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.ColorKind, "absent fill-extrusion-color must use spec default (Constant kind).");
            var c = fp.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Default fill-extrusion-color R must be 0 (black).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Default fill-extrusion-color G must be 0 (black).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Default fill-extrusion-color B must be 0 (black).");
            Assert.AreEqual(1.0, c.A, 1e-4, "Default fill-extrusion-color must be fully opaque.");
        }

        [Test]
        public void Color_ZoomExpression_ClassifiesAsZoom()
        {
            const string paintJson =
                "{\"fill-extrusion-color\":[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "10,[\"rgba\",255,0,0,1],16,[\"rgba\",0,0,255,1]]}";
            var layer = MakeLayer(paintJson);
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Zoom, fp.ColorKind, "a zoom-interpolate expression must classify as Zoom.");

            var atMin = fp.Color.Evaluate(10.0);
            Assert.AreEqual(1.0, atMin.R, 1e-3, "At zoom=10, interpolated color must be the first stop (red).");
            Assert.AreEqual(0.0, atMin.B, 1e-3);

            var atMax = fp.Color.Evaluate(16.0);
            Assert.AreEqual(0.0, atMax.R, 1e-3, "At zoom=16, interpolated color must be the second stop (blue).");
            Assert.AreEqual(1.0, atMax.B, 1e-3);
        }

        [Test]
        public void Color_DataDriven_ClassifiesAsFeature()
        {
            const string paintJson =
                "{\"fill-extrusion-color\":[\"match\",[\"get\",\"type\"]," +
                "\"residential\",[\"rgba\",200,50,50,1]," +
                "[\"rgba\",128,128,128,1]]}";
            var layer = MakeLayer(paintJson);
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Feature, fp.ColorKind, "a [\"match\",[\"get\",...],...] expression must classify as Feature.");
            Assert.IsTrue(fp.Color.DependsOnFeature);
        }

        // ── opacity ───────────────────────────────────────────────────────────

        [Test]
        public void Opacity_Absent_UsesSpecDefault_One()
        {
            var layer = MakeLayer("{}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.OpacityKind);
            Assert.AreEqual(1.0f, fp.Opacity.Evaluate(0.0), 1e-6f, "default fill-extrusion-opacity must be 1.0.");
        }

        [Test]
        public void Opacity_Constant_Pins()
        {
            var layer = MakeLayer("{\"fill-extrusion-opacity\":0.5}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(0.5f, fp.Opacity.Evaluate(0.0), 1e-6f);
        }

        [Test]
        public void Opacity_ZoomExpression_ClassifiesAsZoom()
        {
            const string paintJson =
                "{\"fill-extrusion-opacity\":[\"interpolate\",[\"linear\"],[\"zoom\"],10,0.2,16,1.0]}";
            var layer = MakeLayer(paintJson);
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Zoom, fp.OpacityKind, "a zoom-interpolate expression must classify as Zoom.");
            Assert.AreEqual(0.2f, fp.Opacity.Evaluate(10.0), 0.01f);
            Assert.AreEqual(1.0f, fp.Opacity.Evaluate(16.0), 0.01f);
        }

        [Test]
        public void Opacity_DataDriven_ClassifiesAsFeature()
        {
            const string paintJson = "{\"fill-extrusion-opacity\":[\"get\",\"opacity\"]}";
            var layer = MakeLayer(paintJson);
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Feature, fp.OpacityKind, "a [\"get\",...] expression must classify as Feature.");
            Assert.IsTrue(fp.Opacity.DependsOnFeature);
        }

        // ── vertical-gradient ─────────────────────────────────────────────────

        [Test]
        public void VerticalGradient_Absent_UsesSpecDefault_True()
        {
            var layer = MakeLayer("{}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(1.0f, fp.VerticalGradient.Evaluate(0.0), 1e-6f,
                "default fill-extrusion-vertical-gradient must encode 'true' as 1.0.");
        }

        [Test]
        public void VerticalGradient_False_EncodesAsZero()
        {
            var layer = MakeLayer("{\"fill-extrusion-vertical-gradient\":false}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(0.0f, fp.VerticalGradient.Evaluate(0.0), 1e-6f,
                "fill-extrusion-vertical-gradient:false must encode as 0.0.");
        }

        // ── translate-anchor ──────────────────────────────────────────────────

        [Test]
        public void TranslateAnchor_Viewport_IsOne()
        {
            var layer = MakeLayer("{\"fill-extrusion-translate-anchor\":\"viewport\"}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(1.0f, fp.TranslateAnchor.Evaluate(0.0), 1e-6f,
                "fill-extrusion-translate-anchor 'viewport' must encode as 1.0.");
        }

        [Test]
        public void TranslateAnchor_AbsentDefaultsToMap_IsZero()
        {
            var layer = MakeLayer("{}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(0.0f, fp.TranslateAnchor.Evaluate(0.0), 1e-6f,
                "absent fill-extrusion-translate-anchor must default to 'map' (0.0).");
        }

        // ── translate (I2b — parsed through the expression engine; mirrors the Height teeth above) ──

        [Test]
        public void Translate_ConstantValue_ClassifiesAsConstantAndPins()
        {
            // A bare [x,y] array — the common constant form real styles use — must parse (via
            // WrapBareArrayLiterals) rather than throw "operator must be a string".
            var layer = MakeLayer("{\"fill-extrusion-translate\":[12,-4]}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.TranslateKind);
            var t = fp.Translate.Evaluate(0.0);
            Assert.AreEqual(12.0, t.x, 1e-6, "fill-extrusion-translate.x must pin to its literal value.");
            Assert.AreEqual(-4.0, t.y, 1e-6, "fill-extrusion-translate.y must pin to its literal value.");
            Assert.IsFalse(fp.IsInertFallback, "a layer with fill-extrusion-translate set is not inert.");
        }

        [Test]
        public void Translate_Absent_UsesSpecDefault_Zero()
        {
            var layer = MakeLayer("{}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.TranslateKind);
            var t = fp.Translate.Evaluate(0.0);
            Assert.AreEqual(0.0, t.x, 1e-6);
            Assert.AreEqual(0.0, t.y, 1e-6);
        }

        [Test]
        public void Translate_ZoomExpression_ClassifiesAsZoom_NotCollapsedToDefault()
        {
            // The array-valued stop outputs must be wrapped ["literal", [...]] per the Style Spec — the
            // engine's InterpolateExpression.Lerp handles ValueType.Array element-wise (Ops/Ramps.cs).
            const string paintJson =
                "{\"fill-extrusion-translate\":[\"interpolate\",[\"linear\"],[\"zoom\"]," +
                "10,[\"literal\",[0,0]],16,[\"literal\",[20,-10]]]}";
            var layer = MakeLayer(paintJson);
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Zoom, fp.TranslateKind,
                "a zoom-interpolate translate must classify as Zoom, not collapse to a Constant [0,0].");

            var atMin = fp.Translate.Evaluate(10.0);
            Assert.AreEqual(0.0, atMin.x, 0.01);
            Assert.AreEqual(0.0, atMin.y, 0.01);

            var atMax = fp.Translate.Evaluate(16.0);
            Assert.AreEqual(20.0, atMax.x, 0.01,
                "at zoom=16 the interpolated translate must reach its second stop, NOT stay at [0,0].");
            Assert.AreEqual(-10.0, atMax.y, 0.01);
        }

        [Test]
        public void Translate_DataDriven_FallsBackToDefault()
        {
            // fill-extrusion-translate is a layer-level property; a data-driven value is spec-invalid and
            // must fall back to the default rather than throw at bind time (mirrors VerticalGradient).
            var layer = MakeLayer("{\"fill-extrusion-translate\":[\"get\",\"offset\"]}");
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, fp.TranslateKind,
                "a data-driven translate must fall back to the Constant default, not classify as Feature.");
            var t = fp.Translate.Evaluate(0.0);
            Assert.AreEqual(0.0, t.x, 1e-6);
            Assert.AreEqual(0.0, t.y, 1e-6);
        }

        // ── inert fallback / null paint ───────────────────────────────────────

        [Test]
        public void NullPaint_IsInertFallback()
        {
            var layer = MakeLayer(null);
            var fp    = new FillExtrusion.PaintProperties(layer);

            Assert.IsTrue(fp.IsInertFallback, "a layer with null Paint must be IsInertFallback.");
        }

        // ── StyleParser dispatch (T4 Core half) ──────────────────────────────────

        [Test]
        public void StyleParser_ParseLayer_FillExtrusion_YieldsTypedSubclass()
        {
            const string json = @"{
              ""version"": 8,
              ""sources"": { ""s"": { ""type"": ""vector"", ""url"": ""u"" } },
              ""layers"": [
                { ""id"": ""buildings-3d"", ""type"": ""fill-extrusion"", ""source"": ""s"", ""source-layer"": ""buildings"",
                  ""paint"": { ""fill-extrusion-height"": 30 } }
              ]
            }";

            var doc = StyleParser.Parse(json);

            Assert.AreEqual(1, doc.Layers.Count);
            Assert.AreEqual(StyleLayerType.FillExtrusion, doc.Layers[0].LayerType);
            Assert.IsInstanceOf<FillExtrusion.StyleLayer>(doc.Layers[0],
                "'fill-extrusion' must dispatch to its typed subclass, like fill/line/symbol/background.");

            var fe = (FillExtrusion.StyleLayer)doc.Layers[0];
            Assert.AreEqual(30f, fe.Paint.Height.Evaluate(0.0), 1e-4f,
                "the typed subclass's lazily-parsed Paint must read the real paint sub-tree.");
        }
    }
}
