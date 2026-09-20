// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>S09 — variable binding: let/var (Style Spec "Variable binding").</summary>
    [TestFixture]
    public class LetVarTests
    {
        [Test]
        public void Let_BindsAndVarResolves()
        {
            // let x = 5 in x + 1
            string e = "[\"let\", \"x\", 5, [\"+\", [\"var\", \"x\"], 1]]";
            Assert.AreEqual(6.0, Expr.Eval(e).AsNumber(), 1e-9);
        }

        [Test]
        public void Let_MultipleBindings()
        {
            string e = "[\"let\", \"a\", 2, \"b\", 3, [\"*\", [\"var\", \"a\"], [\"var\", \"b\"]]]";
            Assert.AreEqual(6.0, Expr.Eval(e).AsNumber(), 1e-9);
        }

        [Test]
        public void Let_LaterBindingReferencesEarlier()
        {
            // a=2, b=a+1 -> 3 ; result a+b = 5
            string e = "[\"let\", \"a\", 2, \"b\", [\"+\", [\"var\", \"a\"], 1], " +
                       "[\"+\", [\"var\", \"a\"], [\"var\", \"b\"]]]";
            Assert.AreEqual(5.0, Expr.Eval(e).AsNumber(), 1e-9);
        }

        [Test]
        public void Let_NestedShadowing()
        {
            // outer x=1; inner let shadows x=10; inner body uses 10.
            string e = "[\"let\", \"x\", 1, " +
                       "[\"let\", \"x\", 10, [\"var\", \"x\"]]]";
            Assert.AreEqual(10.0, Expr.Eval(e).AsNumber(), 1e-9);
        }

        [Test]
        public void Var_Unbound_IsParseError()
        {
            Assert.Throws<ExpressionParseException>(() => Expr.Parse("[\"var\", \"nope\"]"));
        }
    }
}
