// Unity EditMode ONLY: it uses Is.Not.AllocatingGCMemory(), because GC.GetAllocatedBytesForCurrentThread()
// reads 0 in this runner. Non-obvious why: the measured delegate is warmed first to keep one-time JIT out.

using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>
    /// H4 — <see cref="FunctionExpression.Evaluate"/> rents a per-thread buffer from
    /// <see cref="EvalArgBuffers"/> for its evaluated arguments instead of allocating a fresh
    /// <c>new Value[]</c> on every call — the hottest managed allocation during feature selection
    /// (features × filter-nodes, per tile, off-main). This tooth pins that the steady-state evaluation
    /// of an operator tree allocates nothing.
    /// </summary>
    /// <remarks>
    /// The fixture is a NESTED pure-arithmetic tree so evaluation is re-entrant — the outer <c>+</c> node's
    /// buffer is live while the inner <c>*</c> and <c>-</c> nodes rent their own — which is exactly the case a
    /// single shared buffer would corrupt and a per-call <c>new Value[]</c> would allocate three times over.
    /// Every node here is a <see cref="FunctionExpression"/> (variadic <c>+</c>/<c>*</c>, binary <c>-</c>); the
    /// leaves are literals (no allocation), and the impls return <see cref="Value"/> structs (no boxing).
    /// </remarks>
    [TestFixture]
    public class FunctionExpressionAllocationTests
    {
        [Test]
        public void Evaluate_NestedOperatorTree_AllocatesNoGCMemory()
        {
            // 3 FunctionExpression nodes: (2*3) + (10-4) == 12. Pre-fix: 3 `new Value[]` per Evaluate.
            Expression expr = Expr.Parse("[\"+\", [\"*\", 2, 3], [\"-\", 10, 4]]");
            var ctx = new EvaluationContext(0.0);
            Assert.That(expr.Evaluate(ctx).AsNumber(), Is.EqualTo(12.0), "fixture sanity: (2*3)+(10-4)");

            // Warm the EXACT measured delegate (JIT + the thread's buffer pool). A real per-call allocation is
            // still caught; only the one-time JIT leaves the window.
            TestDelegate act = () => expr.Evaluate(ctx);
            for (int w = 0; w < 50; w++) act();
            Assert.That(act, Is.Not.AllocatingGCMemory(),
                "a nested operator tree must evaluate without allocating: each FunctionExpression rents its " +
                "argument buffer from the per-thread EvalArgBuffers free-list instead of `new Value[]`.");
        }

        [Test]
        public void Evaluate_VariadicManyArgs_AllocatesNoGCMemory()
        {
            // A wider arity than the nested tree, so a rented buffer must be sized to the largest request and
            // still be reused (the pool converges to max-arity buffers).
            Expression expr = Expr.Parse("[\"+\", 1, 2, 3, 4, 5, 6, 7, 8]");
            var ctx = new EvaluationContext(0.0);
            Assert.That(expr.Evaluate(ctx).AsNumber(), Is.EqualTo(36.0), "fixture sanity: sum 1..8");

            TestDelegate act = () => expr.Evaluate(ctx);
            for (int w = 0; w < 50; w++) act();
            Assert.That(act, Is.Not.AllocatingGCMemory(),
                "an 8-argument variadic must evaluate without allocating once its buffer is pooled.");
        }
    }
}
