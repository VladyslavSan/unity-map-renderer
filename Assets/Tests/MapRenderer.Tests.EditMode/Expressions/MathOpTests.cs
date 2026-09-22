// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using NUnit.Framework;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>Math category (Style Spec "Math").</summary>
    [TestFixture]
    public class MathOpTests
    {
        private static double N(string json) => Expr.Eval(json).AsNumber();

        [Test]
        public void Arithmetic()
        {
            Assert.AreEqual(6.0, N("[\"+\", 1, 2, 3]"), 1e-9);
            Assert.AreEqual(24.0, N("[\"*\", 2, 3, 4]"), 1e-9);
            Assert.AreEqual(-1.0, N("[\"-\", 2, 3]"), 1e-9);
            Assert.AreEqual(-5.0, N("[\"-\", 5]"), 1e-9);          // unary negation
            Assert.AreEqual(2.5, N("[\"/\", 5, 2]"), 1e-9);
            Assert.AreEqual(1.0, N("[\"%\", 7, 3]"), 1e-9);
            Assert.AreEqual(8.0, N("[\"^\", 2, 3]"), 1e-9);
        }

        [Test]
        public void MinMax()
        {
            Assert.AreEqual(1.0, N("[\"min\", 3, 1, 2]"), 1e-9);
            Assert.AreEqual(3.0, N("[\"max\", 3, 1, 2]"), 1e-9);
        }

        [Test]
        public void Rounding()
        {
            Assert.AreEqual(3.0, N("[\"abs\", -3]"), 1e-9);
            Assert.AreEqual(3.0, N("[\"floor\", 3.7]"), 1e-9);
            Assert.AreEqual(4.0, N("[\"ceil\", 3.2]"), 1e-9);
            Assert.AreEqual(3.0, N("[\"round\", 2.5]"), 1e-9);     // half away from zero
            Assert.AreEqual(-3.0, N("[\"round\", -2.5]"), 1e-9);
        }

        [Test]
        public void Trig()
        {
            Assert.AreEqual(0.0, N("[\"sin\", 0]"), 1e-9);
            Assert.AreEqual(1.0, N("[\"cos\", 0]"), 1e-9);
            Assert.AreEqual(0.0, N("[\"tan\", 0]"), 1e-9);
        }

        [Test]
        public void Logs()
        {
            Assert.AreEqual(0.0, N("[\"ln\", 1]"), 1e-9);
            Assert.AreEqual(2.0, N("[\"log10\", 100]"), 1e-9);
            Assert.AreEqual(3.0, N("[\"log2\", 8]"), 1e-9);
            Assert.AreEqual(2.0, N("[\"sqrt\", 4]"), 1e-9);
        }

        [Test]
        public void Constants()
        {
            Assert.AreEqual(Math.E, N("[\"e\"]"), 1e-12);
            Assert.AreEqual(Math.PI, N("[\"pi\"]"), 1e-12);
            Assert.AreEqual(Math.Log(2.0), N("[\"ln2\"]"), 1e-12);
        }

        [Test]
        public void NonNumberOperand_IsError()
        {
            bool ok = Expr.TryEval("[\"+\", 1, \"x\"]", out _, out _);
            Assert.IsFalse(ok, "math on a non-number must be a spec error.");
        }
    }
}
