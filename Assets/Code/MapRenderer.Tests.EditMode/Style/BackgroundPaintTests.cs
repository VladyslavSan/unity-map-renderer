// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Style;
using Background = MapRenderer.Core.Style.Background;

namespace MapRenderer.Tests
{
    /// <summary>
    /// E3 — <see cref="Background.PaintProperties"/>: spec defaults, explicit parse, and zoom
    /// classification (the Fill pattern, <see cref="FillPaintTests"/>).
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    /// </summary>
    [TestFixture]
    public class BackgroundPaintTests
    {
        private static StyleLayer MakeBackgroundLayer(string paintJson)
        {
            return new StyleLayer
            {
                Id        = "test-background",
                LayerType = StyleLayerType.Background,
                PaintJson = paintJson != null ? JsonParser.Parse(paintJson) : null,
            };
        }

        // ── Defaults when paint is absent ────────────────────────────────────────

        [Test]
        public void BackgroundPaint_AbsentPaint_UsesSpecDefaults()
        {
            var layer = MakeBackgroundLayer("{}");
            var bp    = new Background.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Constant, bp.Color.Kind);
            var c = bp.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4, "Default background-color R must be 0 (black).");
            Assert.AreEqual(0.0, c.G, 1e-4, "Default background-color G must be 0 (black).");
            Assert.AreEqual(0.0, c.B, 1e-4, "Default background-color B must be 0 (black).");
            Assert.AreEqual(1.0, c.A, 1e-4, "Default background-color A must be 1 (opaque).");

            Assert.AreEqual(1.0f, bp.Opacity.Evaluate(0.0), 1e-6f, "Default background-opacity must be 1.0.");
            Assert.IsNull(bp.PatternName, "Default background-pattern must be null.");
            Assert.IsTrue(bp.IsInertFallback, "A layer with no paint properties set must be IsInertFallback.");
        }

        [Test]
        public void BackgroundPaint_NullPaint_IsInertFallback()
        {
            var layer = MakeBackgroundLayer(null);
            var bp    = new Background.PaintProperties(layer);

            Assert.IsTrue(bp.IsInertFallback, "A layer with null Paint must be IsInertFallback.");
        }

        // ── Explicit parse ────────────────────────────────────────────────────────

        [Test]
        public void BackgroundPaint_ExplicitColorString_Parses()
        {
            var layer = MakeBackgroundLayer("{\"background-color\":\"#ff0000\"}");
            var bp    = new Background.PaintProperties(layer);

            var c = bp.Color.Evaluate(0.0);
            Assert.AreEqual(1.0, c.R, 1e-4, "Red channel must be 1.0 for #ff0000.");
            Assert.AreEqual(0.0, c.G, 1e-4);
            Assert.AreEqual(0.0, c.B, 1e-4);
            Assert.IsFalse(bp.IsInertFallback);
        }

        [Test]
        public void BackgroundPaint_ExplicitColorRgbaArray_Parses()
        {
            var layer = MakeBackgroundLayer("{\"background-color\":[\"rgba\",0,255,0,1]}");
            var bp    = new Background.PaintProperties(layer);

            var c = bp.Color.Evaluate(0.0);
            Assert.AreEqual(0.0, c.R, 1e-4);
            Assert.AreEqual(1.0, c.G, 1e-4, "Green channel must be 1.0 for rgba(0,255,0,1).");
            Assert.AreEqual(0.0, c.B, 1e-4);
        }

        [Test]
        public void BackgroundPaint_ExplicitOpacityNumber_Parses()
        {
            var layer = MakeBackgroundLayer("{\"background-opacity\":0.5}");
            var bp    = new Background.PaintProperties(layer);

            Assert.AreEqual(0.5f, bp.Opacity.Evaluate(0.0), 1e-6f);
            Assert.IsFalse(bp.IsInertFallback);
        }

        [Test]
        public void BackgroundPaint_ExplicitPatternString_Parses()
        {
            var layer = MakeBackgroundLayer("{\"background-pattern\":\"stripes\"}");
            var bp    = new Background.PaintProperties(layer);

            Assert.AreEqual("stripes", bp.PatternName);
            Assert.IsFalse(bp.IsInertFallback);
        }

        // ── Zoom classification ──────────────────────────────────────────────────

        [Test]
        public void BackgroundPaint_ZoomColor_ClassifiesAsZoom_AndEvaluatesDifferentlyAcrossZooms()
        {
            const string paintJson =
                "{\"background-color\":[\"interpolate\",[\"exponential\",1],[\"zoom\"]," +
                "0,\"#000000\",10,\"#ffffff\"]}";
            var layer = MakeBackgroundLayer(paintJson);
            var bp    = new Background.PaintProperties(layer);

            Assert.AreEqual(ExpressionKind.Zoom, bp.Color.Kind,
                "A zoom-interpolate background-color must classify as Zoom.");

            var atZero = bp.Color.Evaluate(0.0);
            var atTen  = bp.Color.Evaluate(10.0);
            Assert.AreEqual(0.0, atZero.R, 1e-4, "At zoom=0, background-color must be black.");
            Assert.AreEqual(1.0, atTen.R, 1e-4, "At zoom=10, background-color must be white.");
            Assert.AreNotEqual(atZero.R, atTen.R, "A zoom-dependent background-color must evaluate differently at two zooms.");
        }
    }
}
