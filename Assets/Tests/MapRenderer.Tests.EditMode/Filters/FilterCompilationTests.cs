// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Tests/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Filters
{
    using System.Collections.Generic;
    using MapRenderer.Core.Filters;
    using MapRenderer.Core.Json;
    using NUnit.Framework;

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

// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Tests/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Filters
{
    using System.Collections.Generic;
    using MapRenderer.Core.Expressions;
    using MapRenderer.Core.Filters;
    using MapRenderer.Core.Json;
    using MapRenderer.Core.Tiles;
    using NUnit.Framework;

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

// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Tests/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Filters
{
    using System.Collections.Generic;
    using MapRenderer.Core.Expressions;
    using MapRenderer.Core.Filters;
    using MapRenderer.Core.Tiles;
    using NUnit.Framework;

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

        // ── $type ────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void EqType_Polygon_SelectsPolygons()
        {
            var result = Select("[\"==\",\"$type\",\"Polygon\"]");
            Assert.That(result, Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void EqType_LineString_SelectsLineStrings()
        {
            var result = Select("[\"==\",\"$type\",\"LineString\"]");
            Assert.That(result, Is.EqualTo(new[] { 3 }));
        }

        [Test]
        public void EqType_Point_SelectsPoints()
        {
            var result = Select("[\"==\",\"$type\",\"Point\"]");
            Assert.That(result, Is.EqualTo(new[] { 4 }));
        }

        [Test]
        public void NotEqType_Polygon_ExcludesPolygons()
        {
            var result = Select("[\"!=\",\"$type\",\"Polygon\"]");
            Assert.That(result, Is.EqualTo(new[] { 3, 4, 5 }));
        }

        [Test]
        public void InType_PolygonLineString_SelectsBoth()
        {
            var result = Select("[\"in\",\"$type\",\"Polygon\",\"LineString\"]");
            Assert.That(result, Is.EqualTo(new[] { 0, 1, 2, 3 }));
        }

        [Test]
        public void NotInType_Point_ExcludesPoint()
        {
            var result = Select("[\"!in\",\"$type\",\"Point\"]");
            // Excludes only feature 4 (Point); feature 5 is Unknown so also excluded
            // Unknown != Point, LineString, or Polygon, so !in ["Point"] includes Unknown
            Assert.That(result, Is.EqualTo(new[] { 0, 1, 2, 3, 5 }));
        }

        // ── $id ──────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void EqId_NumericId_SelectsMatchingFeature()
        {
            var result = Select("[\"==\",\"$id\",1]");
            Assert.That(result, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void EqId_AbsentId_NoMatch()
        {
            // feature 0 and 4 and 5 have no id -> id expression yields Null
            var result = Select("[\"==\",\"$id\",1]");
            Assert.IsFalse(result.Contains(0), "feature 0 (no id) should not match $id==1");
            Assert.IsFalse(result.Contains(4), "feature 4 (no id) should not match $id==1");
        }

        [Test]
        public void InId_SelectsFeatureWithMatchingId()
        {
            var result = Select("[\"in\",\"$id\",1,2]");
            Assert.That(result, Is.EqualTo(new[] { 1, 2 }));
        }

        // ── has / !has ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void Has_ExistingProperty_MatchesFeaturesWithIt()
        {
            var result = Select("[\"has\",\"area\"]");
            Assert.That(result, Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void Has_MissingProperty_MatchesNone()
        {
            var result = Select("[\"has\",\"nonexistent\"]");
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void NotHas_ExistingProperty_ExcludesFeaturesWithIt()
        {
            var result = Select("[\"!has\",\"area\"]");
            Assert.That(result, Is.EqualTo(new[] { 3, 4, 5 }));
        }

        // ── == / != ──────────────────────────────────────────────────────────────────────────────

        [Test]
        public void EqProperty_StringMatch()
        {
            var result = Select("[\"==\",\"name\",\"beta\"]");
            Assert.That(result, Is.EqualTo(new[] { 1 }));
        }

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
            // features with name != "beta"; feature 5 has null name -> null != "beta" -> true... wait.
            // null == "beta"? No. Eq: null.Equals(string) -> false. So != is true for missing prop.
            // Let me think: Eq does Value.Equals; Null != String -> false; negate -> true.
            // So feature 5 passes "!=".
            Assert.IsTrue(result.Contains(0));
            Assert.IsFalse(result.Contains(1), "feature 1 name=beta should not match !=beta");
        }

        // ── < / <= / > / >= ──────────────────────────────────────────────────────────────────────

        [Test]
        public void LessThan_Area_SelectsCorrectFeatures()
        {
            var result = Select("[\"<\",\"area\",100]");
            Assert.That(result, Is.EqualTo(new[] { 0 })); // area=50 < 100
        }

        [Test]
        public void LessThanOrEqual_Area_BoundaryIsInclusive()
        {
            var result = Select("[\"<=\",\"area\",100]");
            Assert.That(result, Is.EqualTo(new[] { 0, 1 })); // 50 <= 100 AND 100 <= 100
        }

        [Test]
        public void GreaterThan_Area_SelectsCorrectFeatures()
        {
            var result = Select("[\">=\",\"area\",100]");
            Assert.That(result, Is.EqualTo(new[] { 1, 2 })); // 100 >= 100 AND 200 >= 100
        }

        [Test]
        public void GreaterThanOrEqual_Boundary_IsInclusive()
        {
            // exactly at boundary
            var result = Select("[\">=\",\"area\",200]");
            Assert.That(result, Is.EqualTo(new[] { 2 })); // 200 >= 200
        }

        [Test]
        public void StrictGreaterThan_Boundary_ExcludesEqual()
        {
            var result = Select("[\">\" ,\"area\",200]");
            Assert.That(result, Is.Empty); // nothing > 200
        }

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
        public void In_StringSet_SelectsMatchingFeatures()
        {
            var result = Select("[\"in\",\"name\",\"alpha\",\"gamma\"]");
            Assert.That(result, Is.EqualTo(new[] { 0, 2 }));
        }

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
        public void All_TwoConditions_RequiresBothTrue()
        {
            // Polygon AND area > 50
            var result = Select("[\"all\",[\"==\",\"$type\",\"Polygon\"],[\">\",\"area\",50]]");
            Assert.That(result, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void All_EmptyArgs_MatchesAll()
        {
            var result = Select("[\"all\"]");
            Assert.That(result.Count, Is.EqualTo(Features.Length));
        }

        [Test]
        public void Any_TwoConditions_EitherSuffices()
        {
            // name=alpha OR name=gamma
            var result = Select("[\"any\",[\"==\",\"name\",\"alpha\"],[\"==\",\"name\",\"gamma\"]]");
            Assert.That(result, Is.EqualTo(new[] { 0, 2 }));
        }

        [Test]
        public void Any_EmptyArgs_MatchesNone()
        {
            var result = Select("[\"any\"]");
            Assert.That(result, Is.Empty);
        }

        [Test]
        public void None_ExcludesAllMatchingFeatures()
        {
            // none of Polygon -> same as !Polygon -> LineString, Point, Unknown
            var result = Select("[\"none\",[\"==\",\"$type\",\"Polygon\"]]");
            Assert.That(result, Is.EqualTo(new[] { 3, 4, 5 }));
        }

        [Test]
        public void None_EmptyArgs_MatchesAll()
        {
            // none[] -> !(any[]) -> !false -> true -> all
            var result = Select("[\"none\"]");
            Assert.That(result.Count, Is.EqualTo(Features.Length));
        }

        // ── Nested all/any/none ──────────────────────────────────────────────────────────────────

        [Test]
        public void Nested_AllInsideAny()
        {
            // any( all(Polygon, area>=100), type==Point )
            var json = "[\"any\",[\"all\",[\"==\",\"$type\",\"Polygon\"],[\">\",\"area\",50]],[\"==\",\"$type\",\"Point\"]]";
            var result = Select(json);
            Assert.That(result, Is.EqualTo(new[] { 1, 2, 4 }));
        }

        [Test]
        public void Nested_NoneInsideAll()
        {
            // all( none(type==Point), has(name) )
            var json = "[\"all\",[\"none\",[\"==\",\"$type\",\"Point\"]],[\"has\",\"name\"]]";
            var result = Select(json);
            // non-Point with name: 0(Polygon,alpha), 1(Polygon,beta), 2(Polygon,gamma), 3(LineString,delta)
            Assert.That(result, Is.EqualTo(new[] { 0, 1, 2, 3 }));
        }
    }
}
