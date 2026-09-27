// Filter dialect routing, legacy/expression selection equivalence, and per-operator legacy selection.
// Engine-free: Tools/core-tests also compiles this file, so add no UnityEngine or NativeArray reference.
//
// Contents:
//   FilterDialectTests      — IsExpressionFilter's dialect routing, especially the ambiguous overlapping
//                             operators (==, !=, <, <=, >, >=, in, has, all, any) where routing depends on
//                             operand form.
//   FilterEquivalenceTests  — for each legacy filter and its hand-written expression equivalent, the two
//                             select IDENTICAL subsets over the same feature set.
//   LegacyFilterTests       — per-operator concrete subset selection over a fixed DictionaryFeature set,
//                             asserting exact matching indices, including missing-property cases.

using System.Collections.Generic;
using MapRenderer.Core.Filters;
using MapRenderer.Core.Json;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests.Filters
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // FilterDialectTests — legacy-vs-expression dialect routing
    // ───────────────────────────────────────────────────────────────────────────────────

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

        /// <summary>Every dialect-routing case, one row per filter shape: the always-legacy and
        /// always-expression operators, and the ambiguous overlapping operators (==, !=, &lt;, &lt;=,
        /// &gt;, &gt;=, in, has, all, any) where routing depends on operand form.</summary>
        [Test]
        [TestCase("[\"!has\",\"k\"]", false, "always-legacy", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(NotHas)")]
        [TestCase("[\"!in\",\"k\",1,2]", false, "always-legacy", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(NotIn)")]
        [TestCase("[\"none\",[\"==\",\"a\",1]]", false, "always-legacy", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(None)")]
        [TestCase("[\"get\",\"k\"]", true, "always-expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Get)")]
        [TestCase("[\"!\",[\"has\",\"k\"]]", true, "always-expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Not)")]
        [TestCase("[\"match\",[\"get\",\"k\"],1,true,false]", true, "always-expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Match)")]
        [TestCase("[\"+\",1,2]", true, "always-expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(MathPlus)")]
        [TestCase("[\"geometry-type\"]", true, "always-expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(GeometryType)")]
        [TestCase("[\"id\"]", true, "always-expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(IdExpr)")]
        [TestCase("[\"zoom\"]", true, "always-expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Zoom)")]
        [TestCase("[\"concat\",\"a\",\"b\"]", true, "always-expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Concat)")]
        [TestCase("[\"literal\",[1,2,3]]", true, "always-expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Literal)")]
        [TestCase("[\"to-number\",[\"get\",\"x\"]]", true, "always-expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(ToNumber)")]
        [TestCase("[\"==\",\"key\",\"value\"]", false, "== bare string key, scalar value -> legacy", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Eq_BareKeyValue)")]
        [TestCase("[\"==\",[\"get\",\"key\"],\"value\"]", true, "== first operand is an array -> expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Eq_ExpressionOperand)")]
        [TestCase("[\"==\",\"key\",[\"literal\",\"v\"]]", true, "== second operand is an array -> expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Eq_ExpressionValueSide)")]
        [TestCase("[\"==\",\"area\",100]", false, "== legacy numeric comparison", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Eq_NumericValue)")]
        [TestCase("[\"!=\",\"k\",1]", false, "!= bare key/value -> legacy", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Neq_BareKeyValue)")]
        [TestCase("[\"!=\",[\"get\",\"k\"],1]", true, "!= expression operand -> expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Neq_ExprOperand)")]
        [TestCase("[\"<\",\"area\",10]", false, "< bare key -> legacy", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Lt_BareKey)")]
        [TestCase("[\"<=\",\"area\",10]", false, "<= bare key -> legacy", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Lte_BareKey)")]
        [TestCase("[\">\" ,\"area\",10]", false, "> bare key -> legacy", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Gt_BareKey)")]
        [TestCase("[\">=\",\"area\",10]", false, ">= bare key -> legacy", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Gte_BareKey)")]
        [TestCase("[\"<\",[\"get\",\"area\"],10]", true, "< expression key -> expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Lt_ExpressionKey)")]
        [TestCase("[\"in\",\"key\",\"v1\",\"v2\"]", false, "in bare key + scalars -> legacy membership", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(In_BareKeyScalars)")]
        [TestCase("[\"in\",[\"get\",\"k\"],[\"literal\",[\"v1\",\"v2\"]]]", true, "in expression needle -> expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(In_ExprNeedle)")]
        [TestCase("[\"in\",\"needle\",[\"literal\",[\"v1\"]]]", true, "in array haystack -> expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(In_ArrayHaystack)")]
        [TestCase("[\"has\",\"key\"]", false, "has single bare string key -> legacy existence check", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Has_SingleBareStringKey)")]
        [TestCase("[\"has\",\"key\",[\"properties\"]]", true, "has two args (object arg) -> expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Has_TwoArgs)")]
        [TestCase("[\"all\",[\"==\",\"a\",1],[\"has\",\"b\"]]", false, "all with legacy child filters -> legacy", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(All_WithLegacyChildFilters)")]
        [TestCase("[\"all\",[\"==\",[\"get\",\"a\"],1]]", true, "all with an expression-only child op -> expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(All_WithExpressionChildFilters)")]
        [TestCase("[\"any\",[\"==\",\"x\",\"y\"],[\"has\",\"z\"]]", false, "any with legacy child filters -> legacy", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Any_WithLegacyChildFilters)")]
        [TestCase("[\"any\",[\"!\",[\"has\",\"x\"]]]", true, "any with an expression child -> expression", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(Any_WithExpressionChild)")]
        [TestCase("[\"all\"]", true, "all with no children -> expression (AllExpression of 0 args = true)", TestName = "IsExpressionFilter_ClassifiesByOperatorAndOperandShape(All_EmptyArgs)")]
        public void IsExpressionFilter_ClassifiesByOperatorAndOperandShape(string json, bool expectedIsExpression, string note)
        {
            Assert.AreEqual(expectedIsExpression, IsExpr(json), $"{json} ({note})");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FilterEquivalenceTests — legacy and expression forms select identical subsets
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load-bearing equivalence acceptance tooth: for each legacy filter and its hand-written
    /// expression equivalent, assert IDENTICAL selected subsets over the same feature set.
    /// This confirms that translate-then-parse produces the same result as writing the expression directly,
    /// and that one expression engine backs both paths.
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
            }, TileGeometryType.Polygon),
            // 1: Polygon, area=50, name="water"
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["area"] = Value.Number(50),
                ["name"] = Value.String("water"),
            }, TileGeometryType.Polygon),
            // 2: LineString, name="highway"
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["name"] = Value.String("highway"),
            }, TileGeometryType.LineString),
            // 3: Point, name="poi"
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["name"] = Value.String("poi"),
            }, TileGeometryType.Point),
            // 4: Polygon, area=200, no name, hasId=true id=42
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["area"] = Value.Number(200),
            }, TileGeometryType.Polygon, hasId: true, id: Value.Number(42)),
            // 5: no properties
            new DictionaryFeature(null, TileGeometryType.Unknown),
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

        /// <summary>Every legacy filter and its hand-written expression equivalent select IDENTICAL subsets
        /// over the fixed <see cref="Features"/> set — $type, $id, has/!has, ==/!=, comparisons, in/!in,
        /// and the all/any/none combinators.</summary>
        [Test]
        [TestCase("[\"==\",\"$type\",\"Polygon\"]", "[\"==\",[\"geometry-type\"],\"Polygon\"]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(EqType)")]
        [TestCase("[\"!=\",\"$type\",\"Polygon\"]", "[\"!=\",[\"geometry-type\"],\"Polygon\"]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(NeqType)")]
        [TestCase("[\"in\",\"$type\",\"Polygon\",\"LineString\"]", "[\"in\",[\"geometry-type\"],[\"literal\",[\"Polygon\",\"LineString\"]]]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(InType)")]
        [TestCase("[\"!in\",\"$type\",\"Polygon\"]", "[\"!\",[\"in\",[\"geometry-type\"],[\"literal\",[\"Polygon\"]]]]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(NotInType)")]
        [TestCase("[\"==\",\"$id\",42]", "[\"==\",[\"id\"],42]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(EqId)")]
        [TestCase("[\"in\",\"$id\",42,99]", "[\"in\",[\"id\"],[\"literal\",[42,99]]]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(InId)")]
        // Expression "has" with a bare string is also valid.
        [TestCase("[\"has\",\"area\"]", "[\"has\",\"area\"]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(Has)")]
        [TestCase("[\"!has\",\"name\"]", "[\"!\",[\"has\",\"name\"]]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(NotHas)")]
        [TestCase("[\"==\",\"name\",\"road\"]", "[\"==\",[\"get\",\"name\"],\"road\"]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(EqProperty)")]
        [TestCase("[\"!=\",\"name\",\"road\"]", "[\"!=\",[\"get\",\"name\"],\"road\"]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(NeqProperty)")]
        [TestCase("[\"<\",\"area\",100]", "[\"<\",[\"get\",\"area\"],100]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(Lt)")]
        [TestCase("[\"<=\",\"area\",100]", "[\"<=\",[\"get\",\"area\"],100]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(Lte)")]
        [TestCase("[\">\" ,\"area\",100]", "[\">\", [\"get\",\"area\"],100]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(Gt)")]
        [TestCase("[\">=\",\"area\",100]", "[\">=\",[\"get\",\"area\"],100]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(Gte)")]
        [TestCase("[\"in\",\"name\",\"road\",\"water\"]", "[\"in\",[\"get\",\"name\"],[\"literal\",[\"road\",\"water\"]]]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(In)")]
        [TestCase("[\"!in\",\"name\",\"road\",\"water\"]", "[\"!\",[\"in\",[\"get\",\"name\"],[\"literal\",[\"road\",\"water\"]]]]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(NotIn)")]
        [TestCase("[\"all\",[\"==\",\"$type\",\"Polygon\"],[\">\",\"area\",50]]", "[\"all\",[\"==\",[\"geometry-type\"],\"Polygon\"],[\">\", [\"get\",\"area\"],50]]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(All)")]
        [TestCase("[\"any\",[\"==\",\"name\",\"road\"],[\"==\",\"name\",\"water\"]]", "[\"any\",[\"==\",[\"get\",\"name\"],\"road\"],[\"==\",[\"get\",\"name\"],\"water\"]]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(Any)")]
        [TestCase("[\"none\",[\"==\",\"$type\",\"Polygon\"]]", "[\"!\",[\"any\",[\"==\",[\"geometry-type\"],\"Polygon\"]]]", TestName = "LegacyAndExpressionEquivalents_SelectIdenticalSubsets(None)")]
        public void LegacyAndExpressionEquivalents_SelectIdenticalSubsets(string legacyJson, string expressionJson)
            => AssertEquivalent(legacyJson, expressionJson);

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

    // ───────────────────────────────────────────────────────────────────────────────────
    // LegacyFilterTests — per-operator exact subset selection
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-operator concrete subset selection tests over a fixed set of DictionaryFeature instances.
    /// Each test asserts the EXACT matching indices (not just a count) and includes missing-property cases.
    /// </summary>
    [TestFixture]
    internal class LegacyFilterTests
    {
        // ── Feature set ──────────────────────────────────────────────────────────────────────────
        // Index  type           area  name       id
        // 0      Polygon        50    "alpha"    —
        // 1      Polygon       100    "beta"     id=1
        // 2      Polygon       200    "gamma"    id=2
        // 3      LineString      —    "delta"    id=10
        // 4      Point           —    "epsilon"  —
        // 5      (no props)      —    —          —   (missing-property sentinel)

        private static readonly DictionaryFeature[] Features = new[]
        {
            // 0
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["area"] = Value.Number(50),
                ["name"] = Value.String("alpha"),
            }, TileGeometryType.Polygon),
            // 1
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["area"] = Value.Number(100),
                ["name"] = Value.String("beta"),
            }, TileGeometryType.Polygon, hasId: true, id: Value.Number(1)),
            // 2
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["area"] = Value.Number(200),
                ["name"] = Value.String("gamma"),
            }, TileGeometryType.Polygon, hasId: true, id: Value.Number(2)),
            // 3
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["name"] = Value.String("delta"),
            }, TileGeometryType.LineString, hasId: true, id: Value.Number(10)),
            // 4
            new DictionaryFeature(new Dictionary<string, Value>
            {
                ["name"] = Value.String("epsilon"),
            }, TileGeometryType.Point),
            // 5 — no properties
            new DictionaryFeature(null, TileGeometryType.Unknown),
        };

        private static List<int> Select(string filterJson)
        {
            var filter = CompiledFilter.Compile(MapRenderer.Core.Json.JsonParser.Parse(filterJson));
            var matches = new List<int>();
            for (int i = 0; i < Features.Length; i++)
                if (filter.Matches(Features[i]))
                    matches.Add(i);
            return matches;
        }

        // ── null / absent filter ─────────────────────────────────────────────────────────────────

        [Test]
        public void NullFilter_MatchesAll()
        {
            var filter = CompiledFilter.Compile(null);
            for (int i = 0; i < Features.Length; i++)
                Assert.IsTrue(filter.Matches(Features[i]), $"feature {i} should match null filter");
        }

        [Test]
        public void BoolTrue_MatchesAll()
        {
            var filter = CompiledFilter.Compile(MapRenderer.Core.Json.JsonValue.OfBool(true));
            for (int i = 0; i < Features.Length; i++)
                Assert.IsTrue(filter.Matches(Features[i]), $"feature {i} should match true filter");
        }

        [Test]
        public void BoolFalse_MatchesNone()
        {
            var filter = CompiledFilter.Compile(MapRenderer.Core.Json.JsonValue.OfBool(false));
            for (int i = 0; i < Features.Length; i++)
                Assert.IsFalse(filter.Matches(Features[i]), $"feature {i} should not match false filter");
        }

        /// <summary>One row per selectivity case pinned across $type, $id, has/!has, comparisons, in/!in,
        /// all/any/none, and nesting — each asserts the EXACT matching index set, not just a count.</summary>
        private static IEnumerable<TestCaseData> ExactIndexCases()
        {
            yield return new TestCaseData("[\"==\",\"$type\",\"Polygon\"]", new[] { 0, 1, 2 })
                .SetName("Select_ReturnsExactIndices(EqType_Polygon)");
            yield return new TestCaseData("[\"==\",\"$type\",\"LineString\"]", new[] { 3 })
                .SetName("Select_ReturnsExactIndices(EqType_LineString)");
            yield return new TestCaseData("[\"==\",\"$type\",\"Point\"]", new[] { 4 })
                .SetName("Select_ReturnsExactIndices(EqType_Point)");
            yield return new TestCaseData("[\"!=\",\"$type\",\"Polygon\"]", new[] { 3, 4, 5 })
                .SetName("Select_ReturnsExactIndices(NotEqType_Polygon)");
            yield return new TestCaseData("[\"in\",\"$type\",\"Polygon\",\"LineString\"]", new[] { 0, 1, 2, 3 })
                .SetName("Select_ReturnsExactIndices(InType_PolygonLineString)");
            // Excludes only feature 4 (Point); feature 5 is Unknown, which != Point/LineString/Polygon, so
            // !in ["Point"] includes it too.
            yield return new TestCaseData("[\"!in\",\"$type\",\"Point\"]", new[] { 0, 1, 2, 3, 5 })
                .SetName("Select_ReturnsExactIndices(NotInType_Point)");
            yield return new TestCaseData("[\"==\",\"$id\",1]", new[] { 1 })
                .SetName("Select_ReturnsExactIndices(EqId_NumericId)");
            yield return new TestCaseData("[\"in\",\"$id\",1,2]", new[] { 1, 2 })
                .SetName("Select_ReturnsExactIndices(InId)");
            yield return new TestCaseData("[\"has\",\"area\"]", new[] { 0, 1, 2 })
                .SetName("Select_ReturnsExactIndices(Has_ExistingProperty)");
            yield return new TestCaseData("[\"has\",\"nonexistent\"]", new int[0])
                .SetName("Select_ReturnsExactIndices(Has_MissingProperty)");
            yield return new TestCaseData("[\"!has\",\"area\"]", new[] { 3, 4, 5 })
                .SetName("Select_ReturnsExactIndices(NotHas_ExistingProperty)");
            yield return new TestCaseData("[\"==\",\"name\",\"beta\"]", new[] { 1 })
                .SetName("Select_ReturnsExactIndices(EqProperty_StringMatch)");
            yield return new TestCaseData("[\"<\",\"area\",100]", new[] { 0 }) // area=50 < 100
                .SetName("Select_ReturnsExactIndices(LessThan_Area)");
            yield return new TestCaseData("[\"<=\",\"area\",100]", new[] { 0, 1 }) // 50<=100 AND 100<=100
                .SetName("Select_ReturnsExactIndices(LessThanOrEqual_Area_BoundaryIsInclusive)");
            yield return new TestCaseData("[\">=\",\"area\",100]", new[] { 1, 2 }) // 100>=100 AND 200>=100
                .SetName("Select_ReturnsExactIndices(GreaterThanOrEqual_Area)");
            yield return new TestCaseData("[\">=\",\"area\",200]", new[] { 2 }) // 200>=200
                .SetName("Select_ReturnsExactIndices(GreaterThanOrEqual_Boundary_IsInclusive)");
            yield return new TestCaseData("[\">\",\"area\",200]", new int[0]) // nothing > 200
                .SetName("Select_ReturnsExactIndices(StrictGreaterThan_Boundary_ExcludesEqual)");
            yield return new TestCaseData("[\"in\",\"name\",\"alpha\",\"gamma\"]", new[] { 0, 2 })
                .SetName("Select_ReturnsExactIndices(In_StringSet)");
            // Polygon AND area > 50.
            yield return new TestCaseData("[\"all\",[\"==\",\"$type\",\"Polygon\"],[\">\",\"area\",50]]", new[] { 1, 2 })
                .SetName("Select_ReturnsExactIndices(All_TwoConditions)");
            // name=alpha OR name=gamma.
            yield return new TestCaseData("[\"any\",[\"==\",\"name\",\"alpha\"],[\"==\",\"name\",\"gamma\"]]", new[] { 0, 2 })
                .SetName("Select_ReturnsExactIndices(Any_TwoConditions)");
            yield return new TestCaseData("[\"any\"]", new int[0])
                .SetName("Select_ReturnsExactIndices(Any_EmptyArgs)");
            // none of Polygon -> same as !Polygon -> LineString, Point, Unknown.
            yield return new TestCaseData("[\"none\",[\"==\",\"$type\",\"Polygon\"]]", new[] { 3, 4, 5 })
                .SetName("Select_ReturnsExactIndices(None_ExcludesAllMatching)");
            // any( all(Polygon, area>50), type==Point ).
            yield return new TestCaseData(
                    "[\"any\",[\"all\",[\"==\",\"$type\",\"Polygon\"],[\">\",\"area\",50]],[\"==\",\"$type\",\"Point\"]]",
                    new[] { 1, 2, 4 })
                .SetName("Select_ReturnsExactIndices(Nested_AllInsideAny)");
            // all( none(type==Point), has(name) ) -> non-Point with name: 0,1,2 (Polygon), 3 (LineString).
            yield return new TestCaseData(
                    "[\"all\",[\"none\",[\"==\",\"$type\",\"Point\"]],[\"has\",\"name\"]]", new[] { 0, 1, 2, 3 })
                .SetName("Select_ReturnsExactIndices(Nested_NoneInsideAll)");
        }

        [Test]
        [TestCaseSource(nameof(ExactIndexCases))]
        public void Select_ReturnsExactIndices(string filterJson, int[] expected)
        {
            var result = Select(filterJson);
            Assert.That(result, Is.EqualTo(expected));
        }

        // ── $id ──────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void EqId_AbsentId_NoMatch()
        {
            // feature 0 and 4 and 5 have no id -> id expression yields Null
            var result = Select("[\"==\",\"$id\",1]");
            Assert.IsFalse(result.Contains(0), "feature 0 (no id) should not match $id==1");
            Assert.IsFalse(result.Contains(4), "feature 4 (no id) should not match $id==1");
        }

        // ── has / !has / == / != ─────────────────────────────────────────────────────────────────

        [Test]
        public void EqProperty_MissingProperty_NoMatch()
        {
            // feature 5 has no properties; get("name") -> null; null != "delta" -> false
            var result = Select("[\"==\",\"name\",\"delta\"]");
            Assert.IsFalse(result.Contains(5), "feature 5 (no props) should not match name==delta");
        }

        [Test]
        public void NotEqProperty_StringMismatch()
        {
            var result = Select("[\"!=\",\"name\",\"beta\"]");
            // Feature 5 has no name: Null equals no String, so "!=" is true for the missing property.
            Assert.IsTrue(result.Contains(0));
            Assert.IsFalse(result.Contains(1), "feature 1 name=beta should not match !=beta");
        }

        // ── < / <= / > / >= ──────────────────────────────────────────────────────────────────────

        [Test]
        public void Comparison_MissingProperty_NoMatch()
        {
            // feature 3 has no area; get("area") -> null; Compare(null, 100) -> type mismatch error
            // -> TryEvaluate returns false -> Matches returns false
            var result = Select("[\"<\",\"area\",100]");
            Assert.IsFalse(result.Contains(3), "feature 3 (no area) should not match area < 100");
            Assert.IsFalse(result.Contains(5), "feature 5 (no props) should not match area < 100");
        }

        // ── in / !in ─────────────────────────────────────────────────────────────────────────────

        [Test]
        public void In_MissingProperty_NoMatch()
        {
            var result = Select("[\"in\",\"area\",50,100]");
            Assert.IsFalse(result.Contains(3), "feature 3 (no area) should not match in [50,100]");
            Assert.IsFalse(result.Contains(5), "feature 5 (no props) should not match in [50,100]");
        }

        [Test]
        public void NotIn_StringSet_ExcludesMatchingFeatures()
        {
            var result = Select("[\"!in\",\"name\",\"alpha\",\"gamma\"]");
            // name=beta, name=delta, name=epsilon, no-name(5) -> beta(1), delta(3), epsilon(4), 5
            // but feature 5 has null name which is not in ["alpha","gamma"], so it passes !in
            Assert.IsTrue(result.Contains(1), "beta should pass !in [alpha,gamma]");
            Assert.IsFalse(result.Contains(0), "alpha should NOT pass !in [alpha,gamma]");
            Assert.IsFalse(result.Contains(2), "gamma should NOT pass !in [alpha,gamma]");
        }

        // ── all / any / none ─────────────────────────────────────────────────────────────────────

        [Test]
        public void All_EmptyArgs_MatchesAll()
        {
            var result = Select("[\"all\"]");
            Assert.That(result.Count, Is.EqualTo(Features.Length));
        }

        [Test]
        public void None_EmptyArgs_MatchesAll()
        {
            // none[] -> !(any[]) -> !false -> true -> all
            var result = Select("[\"none\"]");
            Assert.That(result.Count, Is.EqualTo(Features.Length));
        }
    }
}
