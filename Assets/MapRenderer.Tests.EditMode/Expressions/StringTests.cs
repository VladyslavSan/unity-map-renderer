// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>S09 — string category: concat/upcase/downcase (Style Spec "String").</summary>
    [TestFixture]
    public class StringTests
    {
        [Test]
        public void Concat_JoinsStrings()
            => Assert.AreEqual("foobar", Expr.Eval("[\"concat\", \"foo\", \"bar\"]").AsString());

        [Test]
        public void Concat_CoercesNonStrings()
        {
            // concat coerces each argument to string: 1 -> "1", true -> "true".
            Assert.AreEqual("a1true", Expr.Eval("[\"concat\", \"a\", 1, true]").AsString());
        }

        [Test]
        public void Upcase() => Assert.AreEqual("ABC", Expr.Eval("[\"upcase\", \"abc\"]").AsString());

        [Test]
        public void Downcase() => Assert.AreEqual("abc", Expr.Eval("[\"downcase\", \"ABC\"]").AsString());
    }
}
