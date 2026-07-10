// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Json;
using NUnit.Framework;

namespace MapRenderer.Tests.Filters
{
    /// <summary>
    /// Pins each dialect-routing case — especially the ambiguous overlapping operators
    /// (==, !=, &lt;, &lt;=, &gt;, &gt;=, in, has, all, any) where routing depends on operand form.
    /// </summary>
    [TestFixture]
    internal class FilterDialectTests
    {
        // Helper: parse a JSON string and call IsExpressionFilter.
        private static bool IsExpr(string json)
            => FilterDialect.IsExpressionFilter(JsonParser.Parse(json));

        // ── Always-legacy operators ───────────────────────────────────────────────────────────────

        [Test] public void NotHas_IsLegacy() => Assert.IsFalse(IsExpr("[\"!has\",\"k\"]"));
        [Test] public void NotIn_IsLegacy() => Assert.IsFalse(IsExpr("[\"!in\",\"k\",1,2]"));
        [Test] public void None_IsLegacy() => Assert.IsFalse(IsExpr("[\"none\",[\"==\",\"a\",1]]"));

        // ── Always-expression operators ──────────────────────────────────────────────────────────

        [Test] public void Get_IsExpression() => Assert.IsTrue(IsExpr("[\"get\",\"k\"]"));
        [Test] public void Not_IsExpression() => Assert.IsTrue(IsExpr("[\"!\",[\"has\",\"k\"]]"));
        [Test] public void Match_IsExpression() => Assert.IsTrue(IsExpr("[\"match\",[\"get\",\"k\"],1,true,false]"));
        [Test] public void MathPlus_IsExpression() => Assert.IsTrue(IsExpr("[\"+\",1,2]"));
        [Test] public void GeometryType_IsExpression() => Assert.IsTrue(IsExpr("[\"geometry-type\"]"));
        [Test] public void IdExpr_IsExpression() => Assert.IsTrue(IsExpr("[\"id\"]"));
        [Test] public void Zoom_IsExpression() => Assert.IsTrue(IsExpr("[\"zoom\"]"));
        [Test] public void Concat_IsExpression() => Assert.IsTrue(IsExpr("[\"concat\",\"a\",\"b\"]"));
        [Test] public void Literal_IsExpression() => Assert.IsTrue(IsExpr("[\"literal\",[1,2,3]]"));
        [Test] public void ToNumber_IsExpression() => Assert.IsTrue(IsExpr("[\"to-number\",[\"get\",\"x\"]]"));

        // ── == disambiguation ────────────────────────────────────────────────────────────────────

        [Test]
        public void Eq_BareKeyValue_IsLegacy()
        {
            // ["==","key","value"] — legacy form (bare string key, scalar value)
            Assert.IsFalse(IsExpr("[\"==\",\"key\",\"value\"]"));
        }

        [Test]
        public void Eq_ExpressionOperand_IsExpression()
        {
            // ["==",["get","key"],"value"] — operand is an array -> expression
            Assert.IsTrue(IsExpr("[\"==\",[\"get\",\"key\"],\"value\"]"));
        }

        [Test]
        public void Eq_ExpressionValueSide_IsExpression()
        {
            // ["==","key",["literal","v"]] — second operand is an array -> expression
            Assert.IsTrue(IsExpr("[\"==\",\"key\",[\"literal\",\"v\"]]"));
        }

        [Test]
        public void Eq_NumericValue_IsLegacy()
        {
            // ["==","area",100] — legacy numeric comparison
            Assert.IsFalse(IsExpr("[\"==\",\"area\",100]"));
        }

        // ── != disambiguation ────────────────────────────────────────────────────────────────────

        [Test] public void Neq_BareKeyValue_IsLegacy() => Assert.IsFalse(IsExpr("[\"!=\",\"k\",1]"));
        [Test] public void Neq_ExprOperand_IsExpression() => Assert.IsTrue(IsExpr("[\"!=\",[\"get\",\"k\"],1]"));

        // ── < <= > >= disambiguation ─────────────────────────────────────────────────────────────

        [Test] public void Lt_BareKey_IsLegacy() => Assert.IsFalse(IsExpr("[\"<\",\"area\",10]"));
        [Test] public void Lte_BareKey_IsLegacy() => Assert.IsFalse(IsExpr("[\"<=\",\"area\",10]"));
        [Test] public void Gt_BareKey_IsLegacy() => Assert.IsFalse(IsExpr("[\">\" ,\"area\",10]"));
        [Test] public void Gte_BareKey_IsLegacy() => Assert.IsFalse(IsExpr("[\">=\",\"area\",10]"));

        [Test]
        public void Lt_ExpressionKey_IsExpression()
            => Assert.IsTrue(IsExpr("[\"<\",[\"get\",\"area\"],10]"));

        // ── in disambiguation ────────────────────────────────────────────────────────────────────

        [Test]
        public void In_BareKeyScalars_IsLegacy()
        {
            // ["in","key","v1","v2"] — legacy membership
            Assert.IsFalse(IsExpr("[\"in\",\"key\",\"v1\",\"v2\"]"));
        }

        [Test]
        public void In_ExprNeedle_IsExpression()
        {
            // ["in",["get","k"],["literal",["v1","v2"]]] — needle is expression
            Assert.IsTrue(IsExpr("[\"in\",[\"get\",\"k\"],[\"literal\",[\"v1\",\"v2\"]]]"));
        }

        [Test]
        public void In_ArrayHaystack_IsExpression()
        {
            // ["in","needle",["literal",["v1"]]] — haystack is array -> expression
            Assert.IsTrue(IsExpr("[\"in\",\"needle\",[\"literal\",[\"v1\"]]]"));
        }

        // ── has disambiguation ───────────────────────────────────────────────────────────────────

        [Test]
        public void Has_SingleBareStringKey_IsLegacy()
        {
            // ["has","key"] — legacy existence check
            Assert.IsFalse(IsExpr("[\"has\",\"key\"]"));
        }

        [Test]
        public void Has_TwoArgs_IsExpression()
        {
            // ["has","key",["properties"]] — expression form (object arg)
            Assert.IsTrue(IsExpr("[\"has\",\"key\",[\"properties\"]]"));
        }

        // ── all/any disambiguation ───────────────────────────────────────────────────────────────

        [Test]
        public void All_WithLegacyChildFilters_IsLegacy()
        {
            // Children are legacy filter arrays: ["==","a",1], ["has","b"]
            Assert.IsFalse(IsExpr("[\"all\",[\"==\",\"a\",1],[\"has\",\"b\"]]"));
        }

        [Test]
        public void All_WithExpressionChildFilters_IsExpression()
        {
            // Child contains expression-only op: ["==",["get","a"],1]
            Assert.IsTrue(IsExpr("[\"all\",[\"==\",[\"get\",\"a\"],1]]"));
        }

        [Test]
        public void Any_WithLegacyChildFilters_IsLegacy()
        {
            Assert.IsFalse(IsExpr("[\"any\",[\"==\",\"x\",\"y\"],[\"has\",\"z\"]]"));
        }

        [Test]
        public void Any_WithExpressionChild_IsExpression()
        {
            Assert.IsTrue(IsExpr("[\"any\",[\"!\",[\"has\",\"x\"]]]"));
        }

        [Test]
        public void All_EmptyArgs_IsExpression()
        {
            // ["all"] with no children -> treated as expression (AllExpression of 0 args = true)
            Assert.IsTrue(IsExpr("[\"all\"]"));
        }
    }
}
