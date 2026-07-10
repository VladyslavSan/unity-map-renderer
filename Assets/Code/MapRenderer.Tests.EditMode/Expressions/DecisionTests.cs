// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>S09 — decision category: case/match/coalesce, comparisons, all/any/! (Style Spec "Decision").</summary>
    [TestFixture]
    public class DecisionTests
    {
        [Test]
        public void Case_SelectsFirstTrueBranch()
        {
            string e = "[\"case\", false, \"a\", true, \"b\", \"fallback\"]";
            Assert.AreEqual("b", Expr.Eval(e).AsString());
        }

        [Test]
        public void Case_FallbackWhenNoneMatch()
        {
            string e = "[\"case\", false, \"a\", false, \"b\", \"fallback\"]";
            Assert.AreEqual("fallback", Expr.Eval(e).AsString());
        }

        [Test]
        public void Match_LabelLists_AndDefault()
        {
            string e = "[\"match\", \"b\", [\"a\", \"b\"], \"AB\", \"c\", \"C\", \"DEF\"]";
            Assert.AreEqual("AB", Expr.Eval(e).AsString());
            string e2 = "[\"match\", \"z\", [\"a\", \"b\"], \"AB\", \"c\", \"C\", \"DEF\"]";
            Assert.AreEqual("DEF", Expr.Eval(e2).AsString());
        }

        [Test]
        public void Match_NumericLabels()
        {
            string e = "[\"match\", 2, 1, \"one\", 2, \"two\", \"other\"]";
            Assert.AreEqual("two", Expr.Eval(e).AsString());
        }

        [Test]
        public void Coalesce_SkipsNull()
        {
            string e = "[\"coalesce\", [\"get\", \"missing\"], \"default\"]";
            Assert.AreEqual("default", Expr.Eval(e, Expr.Feature()).AsString());
        }

        [Test]
        public void Coalesce_SkipsErroringBranch()
        {
            // First branch errors (length of a number); coalesce skips it and returns the literal.
            string e = "[\"coalesce\", [\"length\", 5], 99]";
            Assert.AreEqual(99.0, Expr.Eval(e).AsNumber());
        }

        // ---- comparisons ---------------------------------------------------------------------------

        [Test]
        public void Equality_Numbers()
        {
            Assert.IsTrue(Expr.Eval("[\"==\", 1, 1]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"==\", 1, 2]").AsBool());
            Assert.IsTrue(Expr.Eval("[\"!=\", 1, 2]").AsBool());
        }

        [Test]
        public void Equality_Strings()
        {
            Assert.IsTrue(Expr.Eval("[\"==\", \"a\", \"a\"]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"==\", \"a\", \"b\"]").AsBool());
        }

        [Test]
        public void Equality_AcrossTypes_IsFalse_NotError()
        {
            // No implicit coercion: a number and a string are never equal (deliberate — deep value-equality
            // checks the type first). This is a runtime false, not an error.
            Assert.IsFalse(Expr.Eval("[\"==\", 1, \"1\"]").AsBool());
            Assert.IsTrue(Expr.Eval("[\"!=\", 1, \"1\"]").AsBool());
        }

        [Test]
        public void OrderedComparisons_Numbers()
        {
            Assert.IsTrue(Expr.Eval("[\"<\", 1, 2]").AsBool());
            Assert.IsTrue(Expr.Eval("[\"<=\", 2, 2]").AsBool());
            Assert.IsTrue(Expr.Eval("[\">\", 3, 2]").AsBool());
            Assert.IsTrue(Expr.Eval("[\">=\", 2, 2]").AsBool());
            Assert.IsFalse(Expr.Eval("[\">\", 1, 2]").AsBool());
        }

        [Test]
        public void OrderedComparisons_Strings()
        {
            Assert.IsTrue(Expr.Eval("[\"<\", \"a\", \"b\"]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"<\", \"b\", \"a\"]").AsBool());
        }

        [Test]
        public void OrderedComparison_TypeMismatch_IsError()
        {
            bool ok = Expr.TryEval("[\"<\", 1, \"a\"]", out _, out _);
            Assert.IsFalse(ok, "comparing a number with a string must be a spec error.");
        }

        // ---- all / any / ! -------------------------------------------------------------------------

        [Test]
        public void All_TruthTable()
        {
            Assert.IsTrue(Expr.Eval("[\"all\", true, true]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"all\", true, false]").AsBool());
            Assert.IsTrue(Expr.Eval("[\"all\"]").AsBool()); // empty all -> true
        }

        [Test]
        public void Any_TruthTable()
        {
            Assert.IsTrue(Expr.Eval("[\"any\", false, true]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"any\", false, false]").AsBool());
            Assert.IsFalse(Expr.Eval("[\"any\"]").AsBool()); // empty any -> false
        }

        [Test]
        public void Not()
        {
            Assert.IsFalse(Expr.Eval("[\"!\", true]").AsBool());
            Assert.IsTrue(Expr.Eval("[\"!\", false]").AsBool());
        }

        [Test]
        public void All_ShortCircuits_SkipsLaterError()
        {
            // Second arg would error, but the first is false so all short-circuits to false.
            string e = "[\"all\", false, [\"<\", 1, \"a\"]]";
            Assert.IsFalse(Expr.Eval(e).AsBool());
        }
    }
}
