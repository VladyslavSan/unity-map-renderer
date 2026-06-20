// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>S09 — color category: rgb/rgba constructors, to-rgba, CSS literal parsing (Style Spec "Color").</summary>
    [TestFixture]
    public class ColorTests
    {
        private static double[] Rgba(Value v)
        {
            Assert.AreEqual(ValueType.Color, v.Type);
            return v.AsColor().ToRgbaArray();
        }

        [Test]
        public void Rgb_Constructs()
        {
            var rgba = Rgba(Expr.Eval("[\"rgb\", 255, 128, 0]"));
            Assert.AreEqual(255.0, rgba[0], 1e-9);
            Assert.AreEqual(128.0, rgba[1], 1e-9);
            Assert.AreEqual(0.0, rgba[2], 1e-9);
            Assert.AreEqual(1.0, rgba[3], 1e-9);
        }

        [Test]
        public void Rgba_ConstructsWithAlpha()
        {
            var rgba = Rgba(Expr.Eval("[\"rgba\", 255, 0, 0, 0.5]"));
            Assert.AreEqual(255.0, rgba[0], 1e-9);
            Assert.AreEqual(0.5, rgba[3], 1e-9);
        }

        [Test]
        public void ToRgba_Decomposes()
        {
            var rgba = Rgba(Expr.Eval("[\"rgba\", 10, 20, 30, 0.5]"));
            // round-trip via to-rgba expression
            Value arr = Expr.Eval("[\"to-rgba\", [\"rgba\", 10, 20, 30, 0.5]]");
            var items = arr.AsArray();
            Assert.AreEqual(10.0, items[0].AsNumber(), 1e-9);
            Assert.AreEqual(20.0, items[1].AsNumber(), 1e-9);
            Assert.AreEqual(30.0, items[2].AsNumber(), 1e-9);
            Assert.AreEqual(0.5, items[3].AsNumber(), 1e-9);
        }

        [Test]
        public void Rgb_OutOfRangeChannel_IsError()
        {
            bool ok = Expr.TryEval("[\"rgb\", 300, 0, 0]", out _, out _);
            Assert.IsFalse(ok, "an out-of-range channel must be a spec error.");
        }

        [Test]
        public void Rgba_OutOfRangeAlpha_IsError()
        {
            bool ok = Expr.TryEval("[\"rgba\", 0, 0, 0, 2]", out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void HexParse()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"#00ff00\"]"));
            Assert.AreEqual(0.0, rgba[0], 1e-9);
            Assert.AreEqual(255.0, rgba[1], 1e-9);
            Assert.AreEqual(0.0, rgba[2], 1e-9);
        }

        [Test]
        public void ShortHexParse()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"#0f0\"]"));
            Assert.AreEqual(0.0, rgba[0], 1e-9);
            Assert.AreEqual(255.0, rgba[1], 1e-9);
            Assert.AreEqual(0.0, rgba[2], 1e-9);
        }

        [Test]
        public void NamedColorParse()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"blue\"]"));
            Assert.AreEqual(0.0, rgba[0], 1e-9);
            Assert.AreEqual(0.0, rgba[1], 1e-9);
            Assert.AreEqual(255.0, rgba[2], 1e-9);
        }

        [Test]
        public void RgbFunctionStringParse()
        {
            var rgba = Rgba(Expr.Eval("[\"to-color\", \"rgb(255, 0, 0)\"]"));
            Assert.AreEqual(255.0, rgba[0], 1e-9);
            Assert.AreEqual(0.0, rgba[1], 1e-9);
        }
    }
}
