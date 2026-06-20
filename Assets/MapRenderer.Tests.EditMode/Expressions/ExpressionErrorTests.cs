// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>
    /// S09 — error model. Acceptance: coercion / lookup / type failures surface as spec-defined errors
    /// through the evaluation boundary (TryEvaluate returns false), NEVER as an unhandled crash. Parse-time
    /// structural problems throw <see cref="ExpressionParseException"/> from the parser.
    /// </summary>
    [TestFixture]
    public class ExpressionErrorTests
    {
        // Each of these is an EVALUATION error: TryEvaluate returns false with a message, never throws.
        [TestCase("[\"to-number\", \"abc\"]")]
        [TestCase("[\"to-color\", \"not-a-color\"]")]
        [TestCase("[\"to-rgba\", 5]")]
        [TestCase("[\"at\", 9, [\"literal\", [1, 2]]]")]
        [TestCase("[\"at\", -1, [\"literal\", [1, 2]]]")]
        [TestCase("[\"length\", 5]")]
        [TestCase("[\"<\", 1, \"a\"]")]
        [TestCase("[\"+\", 1, \"x\"]")]
        [TestCase("[\"rgb\", 999, 0, 0]")]
        [TestCase("[\"rgba\", 0, 0, 0, 5]")]
        [TestCase("[\"!\", 5]")]                 // ! on a non-boolean
        [TestCase("[\"upcase\", 5]")]            // upcase on a non-string
        public void EvaluationError_ReturnsFalse_NeverThrows(string json)
        {
            // Must not throw an ExpressionEvaluationException out of the boundary.
            bool ok = true;
            string error = null;
            Assert.DoesNotThrow(() =>
            {
                ok = Expr.TryEval(json, out _, out error);
            });
            Assert.IsFalse(ok, $"expected a spec error result for: {json}");
            Assert.IsNotNull(error);
        }

        // Each of these is a PARSE error: thrown from the parser (structurally invalid).
        [TestCase("[\"unknown-op\", 1]")]
        [TestCase("[\"var\", \"x\"]")]            // unbound var
        [TestCase("[\"zoom\"]")]                  // mis-placed zoom
        [TestCase("[\"get\"]")]                   // wrong arity
        [TestCase("[]")]                          // empty array
        [TestCase("[\"step\", 1, \"d\", 5, \"a\", 5, \"b\"]")] // non-ascending stops
        public void ParseError_Throws(string json)
        {
            Assert.Throws<ExpressionParseException>(() => Expr.Parse(json));
        }
    }
}
