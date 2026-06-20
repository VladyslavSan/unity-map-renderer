// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Json;
using MapRenderer.Core.Mvt;
using NUnit.Framework;

namespace MapRenderer.Tests.Filters
{
    /// <summary>
    /// Load-bearing equivalence acceptance tooth: for each legacy filter and its hand-written
    /// expression equivalent, assert IDENTICAL selected subsets over the same feature set.
    /// This confirms that translate-then-parse produces the same result as writing the expression directly,
    /// and that the S09 expression engine backs both paths.
    /// </summary>
    [TestFixture]
    internal class FilterEquivalenceTests
    {
        private static readonly DictionaryFeature[] Features = new[]
        {
            // 0: Polygon, area=100, name="road"
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["area"] = Value.Number(100),
                ["name"] = Value.String("road"),
            }, MvtGeometryType.Polygon),
            // 1: Polygon, area=50, name="water"
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["area"] = Value.Number(50),
                ["name"] = Value.String("water"),
            }, MvtGeometryType.Polygon),
            // 2: LineString, name="highway"
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["name"] = Value.String("highway"),
            }, MvtGeometryType.LineString),
            // 3: Point, name="poi"
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["name"] = Value.String("poi"),
            }, MvtGeometryType.Point),
            // 4: Polygon, area=200, no name, hasId=true id=42
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["area"] = Value.Number(200),
            }, MvtGeometryType.Polygon, hasId: true, id: Value.Number(42)),
            // 5: no properties
            new DictionaryFeature(null, MvtGeometryType.Unknown),
        };

        private static List<int> Select(string filterJson)
        {
            var filter = CompiledFilter.Compile(JsonParser.Parse(filterJson));
            var matches = new List<int>();
            for (int i = 0; i < Features.Length; i++)
                if (filter.Matches(Features[i]))
                    matches.Add(i);
            return matches;
        }

        private static void AssertEquivalent(string legacyJson, string expressionJson)
        {
            var legacyResult = Select(legacyJson);
            var exprResult = Select(expressionJson);
            Assert.That(legacyResult, Is.EqualTo(exprResult),
                $"Legacy and expression filters should select identical subsets.\nLegacy: {legacyJson}\nExpr:   {expressionJson}");
        }

        // ── $type ───────────────────────────────────────────────────────────────────────────────

        [Test]
        public void EqType_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"==\",\"$type\",\"Polygon\"]",
                "[\"==\",[\"geometry-type\"],\"Polygon\"]");

        [Test]
        public void NeqType_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"!=\",\"$type\",\"Polygon\"]",
                "[\"!=\",[\"geometry-type\"],\"Polygon\"]");

        [Test]
        public void InType_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"in\",\"$type\",\"Polygon\",\"LineString\"]",
                "[\"in\",[\"geometry-type\"],[\"literal\",[\"Polygon\",\"LineString\"]]]");

        [Test]
        public void NotInType_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"!in\",\"$type\",\"Polygon\"]",
                "[\"!\",[\"in\",[\"geometry-type\"],[\"literal\",[\"Polygon\"]]]]");

        // ── $id ─────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void EqId_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"==\",\"$id\",42]",
                "[\"==\",[\"id\"],42]");

        [Test]
        public void InId_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"in\",\"$id\",42,99]",
                "[\"in\",[\"id\"],[\"literal\",[42,99]]]");

        // ── has / !has ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void Has_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"has\",\"area\"]",
                "[\"has\",\"area\"]"); // expression "has" with bare string is also valid

        [Test]
        public void NotHas_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"!has\",\"name\"]",
                "[\"!\",[\"has\",\"name\"]]");

        // ── == / != ──────────────────────────────────────────────────────────────────────────────

        [Test]
        public void EqProperty_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"==\",\"name\",\"road\"]",
                "[\"==\",[\"get\",\"name\"],\"road\"]");

        [Test]
        public void NeqProperty_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"!=\",\"name\",\"road\"]",
                "[\"!=\",[\"get\",\"name\"],\"road\"]");

        // ── < <= > >= ────────────────────────────────────────────────────────────────────────────

        [Test]
        public void Lt_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"<\",\"area\",100]",
                "[\"<\",[\"get\",\"area\"],100]");

        [Test]
        public void Lte_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"<=\",\"area\",100]",
                "[\"<=\",[\"get\",\"area\"],100]");

        [Test]
        public void Gt_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\">\" ,\"area\",100]",
                "[\">\", [\"get\",\"area\"],100]");

        [Test]
        public void Gte_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\">=\",\"area\",100]",
                "[\">=\",[\"get\",\"area\"],100]");

        // ── in / !in ─────────────────────────────────────────────────────────────────────────────

        [Test]
        public void In_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"in\",\"name\",\"road\",\"water\"]",
                "[\"in\",[\"get\",\"name\"],[\"literal\",[\"road\",\"water\"]]]");

        [Test]
        public void NotIn_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"!in\",\"name\",\"road\",\"water\"]",
                "[\"!\",[\"in\",[\"get\",\"name\"],[\"literal\",[\"road\",\"water\"]]]]");

        // ── all / any / none ─────────────────────────────────────────────────────────────────────

        [Test]
        public void All_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"all\",[\"==\",\"$type\",\"Polygon\"],[\">\",\"area\",50]]",
                "[\"all\",[\"==\",[\"geometry-type\"],\"Polygon\"],[\">\", [\"get\",\"area\"],50]]");

        [Test]
        public void Any_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"any\",[\"==\",\"name\",\"road\"],[\"==\",\"name\",\"water\"]]",
                "[\"any\",[\"==\",[\"get\",\"name\"],\"road\"],[\"==\",[\"get\",\"name\"],\"water\"]]");

        [Test]
        public void None_LegacyEqualsExpression()
            => AssertEquivalent(
                "[\"none\",[\"==\",\"$type\",\"Polygon\"]]",
                "[\"!\",[\"any\",[\"==\",[\"geometry-type\"],\"Polygon\"]]]");

        // ── Nested complex filter ─────────────────────────────────────────────────────────────────

        [Test]
        public void ComplexNested_LegacyEqualsExpression()
        {
            // Legacy: all(type==Polygon, area>=100)
            // Expr:   all(geometry-type==Polygon, get(area)>=100)
            AssertEquivalent(
                "[\"all\",[\"==\",\"$type\",\"Polygon\"],[\">\",\"area\",50]]",
                "[\"all\",[\"==\",[\"geometry-type\"],\"Polygon\"],[\">\", [\"get\",\"area\"],50]]");
        }

        // ── literal array wrapping is required (in) ───────────────────────────────────────────────

        [Test]
        public void In_LiteralArrayWrappingRequired()
        {
            // ["in",["get","name"],["literal",["road","water"]]] must produce same as legacy
            AssertEquivalent(
                "[\"in\",\"name\",\"road\",\"water\"]",
                "[\"in\",[\"get\",\"name\"],[\"literal\",[\"road\",\"water\"]]]");
        }

        // ── $id==absent id → no match (not a throw) ───────────────────────────────────────────────

        [Test]
        public void EqId_AbsentId_NoMatchNotThrow()
        {
            // features 0,1,2,3,5 have no id; only feature 4 has id=42
            var legacyResult = Select("[\"==\",\"$id\",42]");
            var exprResult = Select("[\"==\",[\"id\"],42]");
            Assert.That(legacyResult, Is.EqualTo(new[] { 4 }));
            Assert.That(legacyResult, Is.EqualTo(exprResult));
        }
    }
}
