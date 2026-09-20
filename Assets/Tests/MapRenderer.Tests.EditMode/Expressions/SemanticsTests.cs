// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;
    using ExprValueType = MapRenderer.Core.Expressions.ValueType;

    /// <summary>
    /// S11 — type-assertion operators (spec "Types / Assertion"):
    /// <c>boolean</c> / <c>number</c> / <c>string</c> / <c>object</c> / <c>array</c>.
    ///
    /// These are distinct from the <c>to-*</c> coercions:
    ///   - Assert: return the input if the type matches; error if it does not.
    ///   - Coerce (<c>to-number</c>, <c>to-string</c>, …): convert the value.
    ///
    /// Also verifies that <c>["number",["zoom"]]</c> parses and classifies as Zoom-kind — the key
    /// "legal wrapped zoom" form used by the per-frame interpolate path.
    ///
    /// Allocation test (no-GC sweep) is in <see cref="MapRenderer.Tests.Style.StylePropertyTests"/>
    /// which covers the full zoom-evaluation path including these assertion wrappers.
    /// </summary>
    [TestFixture]
    public class AssertionTests
    {
        // ── boolean ─────────────────────────────────────────────────────────────

        [Test]
        public void Boolean_TrueInput_ReturnsSame()
        {
            Value v = ExpressionParser.Parse("[\"boolean\", true]").Evaluate(default);
            Assert.AreEqual(ExprValueType.Boolean, v.Type);
            Assert.IsTrue(v.AsBool());
        }

        [Test]
        public void Boolean_FalseInput_ReturnsSame()
        {
            Value v = ExpressionParser.Parse("[\"boolean\", false]").Evaluate(default);
            Assert.AreEqual(ExprValueType.Boolean, v.Type);
            Assert.IsFalse(v.AsBool());
        }

        [Test]
        public void Boolean_NumberInput_ThrowsEvaluationError()
        {
            var expr = ExpressionParser.Parse("[\"boolean\", 42]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        [Test]
        public void Boolean_MultiArg_FirstMatchWins()
        {
            // Multi-arg first-match form: second arg is boolean.
            var expr = ExpressionParser.Parse("[\"boolean\", 42, true]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(ExprValueType.Boolean, v.Type);
            Assert.IsTrue(v.AsBool());
        }

        [Test]
        public void Boolean_MultiArg_NoneMatch_ThrowsEvaluationError()
        {
            var expr = ExpressionParser.Parse("[\"boolean\", 1, 2, 3]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        // ── number ──────────────────────────────────────────────────────────────

        [Test]
        public void Number_NumberInput_ReturnsSame()
        {
            Value v = ExpressionParser.Parse("[\"number\", 42]").Evaluate(default);
            Assert.AreEqual(ExprValueType.Number, v.Type);
            Assert.AreEqual(42.0, v.AsNumber(), 1e-15);
        }

        [Test]
        public void Number_StringInput_ThrowsEvaluationError()
        {
            var expr = ExpressionParser.Parse("[\"number\", \"hello\"]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        [Test]
        public void Number_MultiArg_FirstMatchWins()
        {
            var expr = ExpressionParser.Parse("[\"number\", \"hello\", 7]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(7.0, v.AsNumber(), 1e-15);
        }

        // ── string ──────────────────────────────────────────────────────────────

        [Test]
        public void String_StringInput_ReturnsSame()
        {
            Value v = ExpressionParser.Parse("[\"string\", \"hi\"]").Evaluate(default);
            Assert.AreEqual(ExprValueType.String, v.Type);
            Assert.AreEqual("hi", v.AsString());
        }

        [Test]
        public void String_NumberInput_ThrowsEvaluationError()
        {
            var expr = ExpressionParser.Parse("[\"string\", 99]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        // ── object ──────────────────────────────────────────────────────────────

        [Test]
        public void Object_ObjectInput_ReturnsSame()
        {
            var expr = ExpressionParser.Parse("[\"object\", {\"k\": 1}]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(ExprValueType.Object, v.Type);
        }

        [Test]
        public void Object_StringInput_ThrowsEvaluationError()
        {
            var expr = ExpressionParser.Parse("[\"object\", \"x\"]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        // ── array ───────────────────────────────────────────────────────────────

        [Test]
        public void Array_AnyElementType_AnyLength_Passes()
        {
            // ["array", v] — any element type, any length.
            var expr = ExpressionParser.Parse("[\"array\", [\"literal\", [1, 2, 3]]]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(ExprValueType.Array, v.Type);
            Assert.AreEqual(3, v.AsArray().Count);
        }

        [Test]
        public void Array_AnyElementType_NonArrayInput_Throws()
        {
            var expr = ExpressionParser.Parse("[\"array\", 42]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        [Test]
        public void Array_NumberElementType_AllNumbers_Passes()
        {
            var expr = ExpressionParser.Parse("[\"array\", \"number\", [\"literal\", [1, 2, 3]]]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(ExprValueType.Array, v.Type);
        }

        [Test]
        public void Array_NumberElementType_MixedElements_Throws()
        {
            // Evaluates ["array","number", literal [1, "x"]] — second element is string → error.
            var expr = ExpressionParser.Parse("[\"array\", \"number\", [\"literal\", [1, \"x\"]]]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        [Test]
        public void Array_NumberType_ExactLength_Passes()
        {
            var expr = ExpressionParser.Parse("[\"array\", \"number\", 2, [\"literal\", [10, 20]]]");
            Value v = expr.Evaluate(default);
            Assert.AreEqual(ExprValueType.Array, v.Type);
            Assert.AreEqual(2, v.AsArray().Count);
        }

        [Test]
        public void Array_ExactLength_WrongLength_Throws()
        {
            var expr = ExpressionParser.Parse("[\"array\", \"number\", 3, [\"literal\", [10, 20]]]");
            Assert.Throws<ExpressionEvaluationException>(() => expr.Evaluate(default));
        }

        // ── zoom wrapping ────────────────────────────────────────────────────────
        // ["number",["zoom"]] must parse (zoom is legal when inside a ramp input) and classify Zoom.

        [Test]
        public void Number_WrappingZoom_ParsesAsZoomKind()
        {
            // The assertion wraps zoom inside a ramp input slot — valid only with zoomAllowed=true.
            // ParseInputAllowingZoom calls ParseNode with zoomAllowed:true; the "number" assertion
            // must thread that flag through to the inner zoom arg.
            var expr = ExpressionParser.Parse(
                "[\"interpolate\", [\"linear\"], [\"number\", [\"zoom\"]], 5, 0.0, 10, 1.0]");
            Assert.AreEqual(ExpressionKind.Zoom, expr.Kind,
                "An interpolate whose input is [\"number\",[\"zoom\"]] must classify as Zoom-kind.");
        }

        [Test]
        public void Number_WrappingZoom_EvaluatesAtGivenZoom()
        {
            var expr = ExpressionParser.Parse(
                "[\"interpolate\", [\"linear\"], [\"number\", [\"zoom\"]], 5, 0.0, 10, 100.0]");
            // At zoom=7.5, t = (7.5-5)/(10-5) = 0.5, so value = 50.
            double v = expr.Evaluate(new EvaluationContext(7.5)).AsNumber();
            Assert.AreEqual(50.0, v, 1e-9);
        }

        [Test]
        public void Number_TopLevelZoom_WithoutRamp_ThrowsParseError()
        {
            // A bare ["number",["zoom"]] NOT inside a ramp input is NOT legal.
            Assert.Throws<ExpressionParseException>(
                () => ExpressionParser.Parse("[\"number\", [\"zoom\"]]"),
                "\"zoom\" must throw at parse when not inside a ramp input.");
        }

        // ── classification ────────────────────────────────────────────────────────

        [Test]
        public void Assert_Constant_ClassifiesConstant()
        {
            var expr = ExpressionParser.Parse("[\"number\", 5]");
            Assert.AreEqual(ExpressionKind.Constant, expr.Kind);
        }

        [Test]
        public void Assert_ResultType_IsAssertedType()
        {
            Assert.AreEqual(ExprValueType.Number,  ExpressionParser.Parse("[\"number\", 5]").ResultType);
            Assert.AreEqual(ExprValueType.Boolean, ExpressionParser.Parse("[\"boolean\", true]").ResultType);
            Assert.AreEqual(ExprValueType.String,  ExpressionParser.Parse("[\"string\", \"x\"]").ResultType);
            Assert.AreEqual(ExprValueType.Array,   ExpressionParser.Parse("[\"array\", [\"literal\", [1]]]").ResultType);
        }
    }
}

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;

    /// <summary>
    /// S09 — classification: each parsed expression reports Constant / Zoom / Feature / Composite. The
    /// brief's "constant / zoom / data-driven" maps to Constant / Zoom / Feature; Composite (depends on
    /// BOTH zoom and feature) is the fourth corner S11/S12 need to distinguish a per-frame uniform from a
    /// per-vertex attribute.
    /// </summary>
    [TestFixture]
    public class ClassificationTests
    {
        private static ExpressionKind Kind(string json) => Expr.Parse(json).Kind;

        [Test]
        public void Literal_IsConstant() => Assert.AreEqual(ExpressionKind.Constant, Kind("5"));

        [Test]
        public void PureArithmetic_IsConstant()
            => Assert.AreEqual(ExpressionKind.Constant, Kind("[\"+\", 1, 2]"));

        [Test]
        public void ZoomRamp_IsZoom()
        {
            // data-driven == "zoom" classification per the brief (camera expression).
            Assert.AreEqual(ExpressionKind.Zoom,
                Kind("[\"interpolate\", [\"linear\"], [\"zoom\"], 0, 0, 10, 100]"));
        }

        [Test]
        public void Get_IsFeature()
        {
            // data-driven == "Feature" classification.
            Assert.AreEqual(ExpressionKind.Feature, Kind("[\"get\", \"x\"]"));
        }

        [Test]
        public void Has_IsFeature() => Assert.AreEqual(ExpressionKind.Feature, Kind("[\"has\", \"x\"]"));

        [Test]
        public void GeometryType_IsFeature()
            => Assert.AreEqual(ExpressionKind.Feature, Kind("[\"geometry-type\"]"));

        [Test]
        public void Id_IsFeature() => Assert.AreEqual(ExpressionKind.Feature, Kind("[\"id\"]"));

        [Test]
        public void MixedZoomAndFeature_IsComposite()
        {
            // ["+", ["zoom-ramp"], ["get"]] would be invalid (zoom not at ramp input); instead use a ramp
            // over zoom whose stop OUTPUTS depend on a feature -> composite.
            string e = "[\"interpolate\", [\"linear\"], [\"zoom\"], " +
                       "0, [\"get\", \"a\"], 10, [\"get\", \"b\"]]";
            Assert.AreEqual(ExpressionKind.Composite, Kind(e));
        }

        [Test]
        public void FeatureDrivenComparison_IsFeature()
            => Assert.AreEqual(ExpressionKind.Feature, Kind("[\"==\", [\"get\", \"x\"], 5]"));

        [Test]
        public void Let_InheritsBodyKind()
        {
            // body uses a feature get -> Feature.
            Assert.AreEqual(ExpressionKind.Feature,
                Kind("[\"let\", \"x\", [\"get\", \"a\"], [\"var\", \"x\"]]"));
        }
    }
}

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.
//
// Regression for the "Expected color but found string" crash: production styles (OpenFreeMap "liberty")
// emit color expressions whose branch/stop literals are CSS color STRINGS, not pre-parsed colors. The
// spec's to-color coercion parses strings in color context; we apply it at every color seam
// (StyleProperty<Color> constant + zoom, interpolate/Ramps). These tests pin
// that a constant string, a step over string stops, and an interpolate over string stops all yield colors.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Style;
    using MapRenderer.Core.Expressions;

    [TestFixture]
    public class ColorCoercionTests
    {
        private static StyleProperty<Color> ColProp(string json)
            => new StyleProperty<Color>(
                MapRenderer.Core.Json.JsonParser.Parse(json), new Color(0, 0, 0, 1), v => v.AsColorCoerced());

        private static void AssertColor(Color c, double r, double g, double b, double a = 1.0, double tol = 1e-6)
        {
            Assert.That(c.R, Is.EqualTo(r).Within(tol), "R");
            Assert.That(c.G, Is.EqualTo(g).Within(tol), "G");
            Assert.That(c.B, Is.EqualTo(b).Within(tol), "B");
            Assert.That(c.A, Is.EqualTo(a).Within(tol), "A");
        }

        [Test]
        public void ConstantColorString_CoercesToColor()
        {
            // A bare CSS color string as a constant paint value (parsed as a String literal).
            var prop = ColProp("\"#ff0000\"");
            AssertColor(prop.Evaluate(0.0), 1, 0, 0);
        }

        [Test]
        public void Step_OverColorStringStops_CoercesToColor()
        {
            // step(zoom): black below 10, white at/above 10 — outputs are STRINGS.
            var prop = ColProp("[\"step\",[\"zoom\"],\"#000000\",10,\"#ffffff\"]");
            AssertColor(prop.Evaluate(5.0),  0, 0, 0);
            AssertColor(prop.Evaluate(12.0), 1, 1, 1);
        }

        [Test]
        public void Interpolate_OverColorStringStops_CoercesAndLerps()
        {
            // interpolate(linear, zoom): "#000000"→"#ffffff" across [0,10]; midpoint is mid-grey.
            var prop = ColProp("[\"interpolate\",[\"linear\"],[\"zoom\"],0,\"#000000\",10,\"#ffffff\"]");
            AssertColor(prop.Evaluate(0.0),  0, 0, 0);
            AssertColor(prop.Evaluate(10.0), 1, 1, 1);
            AssertColor(prop.Evaluate(5.0),  0.5, 0.5, 0.5);
        }
    }
}

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;

    /// <summary>
    /// S09 — error model. Acceptance: coercion / lookup / type failures surface as spec-defined errors
    /// through the evaluation boundary (TryEvaluate returns false), NEVER as an unhandled crash. Parse-time
    /// structural problems throw <see cref="ExpressionParseException"/> from the parser.
    /// </summary>
    [TestFixture]
    public class ExpressionErrorTests
    {
        // Each of these is an EVALUATION error: TryEvaluate returns false with a message, never throws.
        // Only the ops with NO per-op `_IsError` twin remain here (! and upcase). The coercion / lookup /
        // comparison / math / color rows were duplicates of the per-op error tests (same op + same error
        // condition, several byte-identical) and were retired to those files
        // (LiteralType/Lookup/Decision/MathOp/Color) — this stays the boundary-never-crashes table for the
        // two ops those files do not cover.
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

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;
    using MapRenderer.Core.Tiles;

    /// <summary>
    /// S09 — feature-data category: properties/geometry-type/id (Style Spec "Feature data"). Tested over
    /// synthetic <see cref="DictionaryFeature"/> features. Real decoded features (S39) are covered in
    /// <c>MapRenderer.Tests.Mvt.MvtPropertyDecodeTests</c> and <c>MapRenderer.Tests.Filters.PropertyFilterTests</c>.
    /// </summary>
    [TestFixture]
    public class FeatureDataTests
    {
        [TestCase(TileGeometryType.Point, "Point")]
        [TestCase(TileGeometryType.LineString, "LineString")]
        [TestCase(TileGeometryType.Polygon, "Polygon")]
        [TestCase(TileGeometryType.Unknown, "Unknown")]
        public void GeometryType(TileGeometryType geom, string expected)
        {
            var f = Expr.Feature(geom: geom);
            Assert.AreEqual(expected, Expr.Eval("[\"geometry-type\"]", f).AsString());
        }

        [Test]
        public void Id_Present()
        {
            var f = Expr.Feature(hasId: true, id: Value.Number(42));
            Assert.AreEqual(42.0, Expr.Eval("[\"id\"]", f).AsNumber());
        }

        [Test]
        public void Id_Absent_IsNull()
        {
            var f = Expr.Feature(hasId: false);
            Assert.AreEqual(ValueType.Null, Expr.Eval("[\"id\"]", f).Type);
        }

        [Test]
        public void Properties_ReturnsObject_And_GetReadsIt()
        {
            var f = Expr.Feature(Expr.Props(("k", Value.String("v"))));
            Value props = Expr.Eval("[\"properties\"]", f);
            Assert.AreEqual(ValueType.Object, props.Type);
            Assert.IsTrue(props.AsObject().ContainsKey("k"));
            // get via properties: ["get", "k", ["properties"]]
            Assert.AreEqual("v", Expr.Eval("[\"get\", \"k\", [\"properties\"]]", f).AsString());
        }

        [Test]
        public void Get_NoFeature_IsError()
        {
            bool ok = Expr.TryEval("[\"get\", \"x\"]", out _, out _, feature: null);
            Assert.IsFalse(ok, "feature-data with no feature in context must be a spec error, not a crash.");
        }

        [Test]
        public void GeometryType_DrivesMatch()
        {
            var f = Expr.Feature(geom: TileGeometryType.Polygon);
            string e = "[\"match\", [\"geometry-type\"], \"Polygon\", \"fill\", \"Point\", \"point\", \"other\"]";
            Assert.AreEqual("fill", Expr.Eval(e, f).AsString());
        }
    }
}

// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Tests/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using System.Collections.Generic;
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;
    using MapRenderer.Core.GeoJson;
    using MapRenderer.Core.Tiles;

    /// <summary>
    /// T3 (string→id key hoist) — a constant-key <c>get</c>/<c>has</c> node stays byte-identical for any
    /// feature that is NOT <see cref="IIndexedFeature"/>-capable: <see cref="GeoJsonFeature"/> (no key
    /// table — RFC 7946 has none), and the <c>DictionaryFeature</c>/<c>InMemoryTileFeature</c> test
    /// doubles. Production never builds a binding for these —
    /// none of their owning tile layers implement <see cref="IIndexedFeatureSource"/>, so
    /// <see cref="EvaluationContext.KeyBinding"/> is always null on their real call path — but the node's
    /// own capability gate (<c>is IIndexedFeature</c>) is the thing actually proven here, by supplying a
    /// (nonsense) non-null binding anyway: if the gate were dropped and the node took the int path
    /// unconditionally, evaluating against a non-<see cref="IIndexedFeature"/> feature would throw or
    /// misbehave, not fall back quietly.
    /// </summary>
    [TestFixture]
    public class FeatureKeyExpressionCapabilityTests
    {
        private static GeoJsonFeature MakeGeoJsonFeature(IReadOnlyDictionary<string, Value> properties)
            => new GeoJsonFeature
            {
                Id = Value.Null,
                Properties = properties,
                GeometryType = TileGeometryType.Point,
                Paths = null,
                PolygonRingCounts = null,
            };

        [Test]
        public void GeoJsonFeature_PresentKey_Get_ReturnsValue()
        {
            var feature = MakeGeoJsonFeature(new Dictionary<string, Value> { ["name"] = Value.String("Aruba") });
            Value result = ExpressionParser.Parse("[\"get\",\"name\"]").Evaluate(new EvaluationContext(0.0, feature));
            Assert.That(result.AsString(), Is.EqualTo("Aruba"));
        }

        [Test]
        public void GeoJsonFeature_AbsentKey_Get_ReturnsNull()
        {
            var feature = MakeGeoJsonFeature(new Dictionary<string, Value>());
            Value result = ExpressionParser.Parse("[\"get\",\"name\"]").Evaluate(new EvaluationContext(0.0, feature));
            Assert.That(result.Type, Is.EqualTo(ValueType.Null));
        }

        [Test]
        public void GeoJsonFeature_Has_PresentAndAbsent()
        {
            var feature = MakeGeoJsonFeature(new Dictionary<string, Value> { ["name"] = Value.String("Aruba") });
            Assert.That(
                ExpressionParser.Parse("[\"has\",\"name\"]").Evaluate(new EvaluationContext(0.0, feature)).AsBool(),
                Is.True);
            Assert.That(
                ExpressionParser.Parse("[\"has\",\"missing\"]").Evaluate(new EvaluationContext(0.0, feature)).AsBool(),
                Is.False);
        }

        /// <summary>
        /// RED-verify target: drop <c>FeatureKeyExpression.Evaluate</c>'s <c>is IIndexedFeature</c> gate
        /// (take the int path whenever <see cref="EvaluationContext.KeyBinding"/> is non-null, ignoring
        /// feature capability) and this reds — <see cref="GeoJsonFeature"/> is not
        /// <see cref="IIndexedFeature"/>, so the int branch has nothing to call.
        /// </summary>
        [Test]
        public void GeoJsonFeature_EvenWithANonNullBinding_StaysOnTheStringPath()
        {
            var feature = MakeGeoJsonFeature(new Dictionary<string, Value> { ["name"] = Value.String("Aruba") });
            Assert.That(feature, Is.Not.InstanceOf<IIndexedFeature>(),
                "precondition: GeoJsonFeature must not be index-capable, or this tooth proves nothing");

            // A binding a real bind site would never build for a GeoJSON source (no IIndexedFeatureSource
            // capability) — deliberately nonsense (slot 0 -> key index 999) so a wrongly-taken int path
            // would visibly misbehave rather than coincidentally answering right.
            var nonsenseBinding = new[] { 999 };
            Value result = ExpressionParser.Parse("[\"get\",\"name\"]")
                .Evaluate(new EvaluationContext(0.0, feature, nonsenseBinding));

            Assert.That(result.AsString(), Is.EqualTo("Aruba"),
                "the string path must still answer correctly even when (contrary to production) a binding is present");
        }

        [Test]
        public void DictionaryFeature_And_InMemoryTileFeature_AreNotIndexCapable()
        {
            Assert.That(new DictionaryFeature(), Is.Not.InstanceOf<IIndexedFeature>(),
                "production never builds a binding for a DictionaryFeature-backed source");
            Assert.That(new InMemoryTileFeature(), Is.Not.InstanceOf<IIndexedFeature>(),
                "production never builds a binding for an InMemoryTileFeature-backed source");
        }
    }
}

// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Tests/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using System.Collections.Generic;
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;
    using MapRenderer.Core.Json;
    using MapRenderer.Core.Tiles;

    /// <summary>
    /// T1a (string→id key hoist) — the structural proof that a constant-key <c>get</c>/<c>has</c> node
    /// takes the int-keyed <see cref="IIndexedFeature"/> path when it can, and the string
    /// <see cref="IFeature.TryGetProperty"/> path only when it can't. Uses a call-counting double so both
    /// halves assert a real call fired, not merely that the right VALUE came back — a shallow
    /// implementation that always falls to the string path would still return the right value here (the
    /// double answers the same content on either path) but would fail the call-count assertions.
    /// </summary>
    [TestFixture]
    public class FeatureKeyExpressionTests
    {
        /// <summary>Parses a JSON expression string, also yielding its key layout — the
        /// <see cref="ExpressionParser"/> layout-surfacing overload only takes a <see cref="JsonValue"/>.</summary>
        private static Expression ParseWithLayout(string json, out IReadOnlyList<string> keyLayout)
            => ExpressionParser.Parse(JsonParser.Parse(json), out keyLayout);

        /// <summary>An <see cref="IFeature"/> that also implements <see cref="IIndexedFeature"/>, counting
        /// which method actually fired.</summary>
        private sealed class RecordingIndexedFeature : IFeature, IIndexedFeature
        {
            public int ByNameCalls { get; private set; }
            public int ByIndexCalls { get; private set; }

            public TileGeometryType GeometryType => TileGeometryType.Unknown;
            public Value Id => Value.Null;
            public IReadOnlyDictionary<string, Value> Properties => EmptyProperties;
            private static readonly Dictionary<string, Value> EmptyProperties = new Dictionary<string, Value>();

            public bool TryGetProperty(string name, out Value value)
            {
                ByNameCalls++;
                value = Value.String("by-name:" + name);
                return true;
            }

            public bool TryGetPropertyByKeyIndex(int keyIndex, out Value value)
            {
                ByIndexCalls++;
                value = Value.String("by-index:" + keyIndex);
                return true;
            }
        }

        [Test]
        public void Evaluate_WithBindingAndIndexedFeature_TakesTheIntPath_NotTheStringPath()
        {
            var feature = new RecordingIndexedFeature();
            var expr = ParseWithLayout("[\"get\",\"k\"]", out var layout);
            Assert.That(layout, Has.Count.EqualTo(1), "precondition: one constant-key node -> one layout slot");

            var binding = new[] { 7 }; // slot 0 -> key index 7 (the value a bind site would have resolved)
            var ctx = new EvaluationContext(0.0, feature, binding);
            Value result = expr.Evaluate(ctx);

            Assert.That(feature.ByIndexCalls, Is.EqualTo(1),
                "TryGetPropertyByKeyIndex must fire when a binding and an IIndexedFeature are both present");
            Assert.That(feature.ByNameCalls, Is.EqualTo(0),
                "TryGetProperty(string) must NOT fire when the int path is taken");
            Assert.That(result.AsString(), Is.EqualTo("by-index:7"));
        }

        [Test]
        public void Evaluate_WithNullBinding_TakesTheStringPath_NotTheIntPath()
        {
            var feature = new RecordingIndexedFeature();
            var expr = ParseWithLayout("[\"get\",\"k\"]", out _);
            var ctx = new EvaluationContext(0.0, feature, keyBinding: null);
            Value result = expr.Evaluate(ctx);

            Assert.That(feature.ByNameCalls, Is.EqualTo(1),
                "TryGetProperty(string) must fire when no binding is supplied, even for an index-capable feature");
            Assert.That(feature.ByIndexCalls, Is.EqualTo(0),
                "TryGetPropertyByKeyIndex must NOT fire without a binding");
            Assert.That(result.AsString(), Is.EqualTo("by-name:k"));
        }

        [Test]
        public void Has_WithBinding_TakesTheIntPath()
        {
            var feature = new RecordingIndexedFeature();
            var expr = ParseWithLayout("[\"has\",\"k\"]", out var layout);
            var binding = new[] { 3 };
            var ctx = new EvaluationContext(0.0, feature, binding);
            Value result = expr.Evaluate(ctx);

            Assert.That(layout, Has.Count.EqualTo(1));
            Assert.That(feature.ByIndexCalls, Is.EqualTo(1));
            Assert.That(feature.ByNameCalls, Is.EqualTo(0));
            Assert.That(result.AsBool(), Is.True);
        }

        [Test]
        public void Evaluate_NegativeBoundIndex_MeansAbsent_AndNeverCallsTheStore()
        {
            // -1 is the bind site's "layer has no such key" sentinel (mirrors TryResolveKey returning
            // false) — the node must short-circuit to absent without ever calling TryGetPropertyByKeyIndex.
            var feature = new RecordingIndexedFeature();
            var expr = ParseWithLayout("[\"get\",\"k\"]", out _);
            var ctx = new EvaluationContext(0.0, feature, new[] { -1 });
            Value result = expr.Evaluate(ctx);

            Assert.That(feature.ByIndexCalls, Is.EqualTo(0));
            Assert.That(feature.ByNameCalls, Is.EqualTo(0));
            Assert.That(result.Type, Is.EqualTo(ValueType.Null));
        }
    }
}

// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.


namespace MapRenderer.Tests.Expressions
{
    using NUnit.Framework;
    using MapRenderer.Core.Expressions;

    /// <summary>
    /// S09 — literal / type category: literal values, typeof, and the to-* coercions, per the Style Spec
    /// "Types" section.
    /// </summary>
    [TestFixture]
    public class LiteralTypeTests
    {
        [Test]
        public void Literal_Number_PassesThrough()
        {
            Value v = Expr.Eval("[\"literal\", 5]");
            Assert.AreEqual(ValueType.Number, v.Type);
            Assert.AreEqual(5.0, v.AsNumber());
        }

        [Test]
        public void Literal_Array_IsArrayValue()
        {
            Value v = Expr.Eval("[\"literal\", [1, 2, 3]]");
            Assert.AreEqual(ValueType.Array, v.Type);
            Assert.AreEqual(3, v.AsArray().Count);
            Assert.AreEqual(2.0, v.AsArray()[1].AsNumber());
        }

        [Test]
        public void BareLiteralsParse()
        {
            Assert.AreEqual(true, Expr.Eval("true").AsBool());
            Assert.AreEqual("hi", Expr.Eval("\"hi\"").AsString());
            Assert.AreEqual(ValueType.Null, Expr.Eval("null").Type);
        }

        [TestCase("5", "number")]
        [TestCase("\"x\"", "string")]
        [TestCase("true", "boolean")]
        [TestCase("null", "null")]
        public void TypeOf_Scalars(string json, string expected)
        {
            Assert.AreEqual(expected, Expr.Eval($"[\"typeof\", {json}]").AsString());
        }

        [Test]
        public void TypeOf_Color()
        {
            Assert.AreEqual("color", Expr.Eval("[\"typeof\", [\"to-color\", \"#ff0000\"]]").AsString());
        }

        [Test]
        public void TypeOf_Array()
        {
            Assert.AreEqual("array", Expr.Eval("[\"typeof\", [\"literal\", [1, 2]]]").AsString());
        }

        // ---- to-number -----------------------------------------------------------------------------

        [Test]
        public void ToNumber_String() => Assert.AreEqual(3.5, Expr.Eval("[\"to-number\", \"3.5\"]").AsNumber());

        [Test]
        public void ToNumber_TrueIsOne() => Assert.AreEqual(1.0, Expr.Eval("[\"to-number\", true]").AsNumber());

        [Test]
        public void ToNumber_FalseAndNullAreZero()
        {
            Assert.AreEqual(0.0, Expr.Eval("[\"to-number\", false]").AsNumber());
            Assert.AreEqual(0.0, Expr.Eval("[\"to-number\", null]").AsNumber());
        }

        [Test]
        public void ToNumber_NonNumericString_IsError()
        {
            bool ok = Expr.TryEval("[\"to-number\", \"x\"]", out _, out string error);
            Assert.IsFalse(ok, "to-number of a non-numeric string must be a spec error, not a crash.");
            Assert.IsNotNull(error);
        }

        [Test]
        public void ToNumber_MultiArg_FirstSuccess()
        {
            // First arg fails ("x"), second succeeds ("7").
            Assert.AreEqual(7.0, Expr.Eval("[\"to-number\", \"x\", \"7\"]").AsNumber());
        }

        [Test]
        public void ToNumber_EmptyAndWhitespaceString_IsZero()
        {
            // Spec: a string converts via ECMAScript ToNumber; ToNumber("") and all-whitespace are 0,
            // NOT an error.
            Assert.AreEqual(0.0, Expr.Eval("[\"to-number\", \"\"]").AsNumber());
            Assert.AreEqual(0.0, Expr.Eval("[\"to-number\", \"   \"]").AsNumber());
        }

        // ---- to-boolean ----------------------------------------------------------------------------

        [TestCase("0", false)]
        [TestCase("2", true)]
        [TestCase("\"\"", false)]
        [TestCase("\"a\"", true)]
        [TestCase("null", false)]
        [TestCase("false", false)]
        [TestCase("true", true)]
        public void ToBoolean(string json, bool expected)
        {
            Assert.AreEqual(expected, Expr.Eval($"[\"to-boolean\", {json}]").AsBool());
        }

        // ---- to-string -----------------------------------------------------------------------------

        [Test]
        public void ToString_Null_IsEmpty() => Assert.AreEqual("", Expr.Eval("[\"to-string\", null]").AsString());

        [Test]
        public void ToString_Bool() => Assert.AreEqual("true", Expr.Eval("[\"to-string\", true]").AsString());

        [Test]
        public void ToString_IntegerNumber_NoTrailingPointZero()
            => Assert.AreEqual("5", Expr.Eval("[\"to-string\", 5]").AsString());

        [Test]
        public void ToString_FractionalNumber()
            => Assert.AreEqual("3.5", Expr.Eval("[\"to-string\", 3.5]").AsString());

        [Test]
        public void ToString_Color_IsRgba()
        {
            string s = Expr.Eval("[\"to-string\", [\"to-color\", \"#ff0000\"]]").AsString();
            Assert.AreEqual("rgba(255,0,0,1)", s);
        }

        // ---- to-color / to-rgba round-trip ---------------------------------------------------------

        [Test]
        public void ToColor_Hex_RoundTrips_Via_ToRgba()
        {
            Value rgba = Expr.Eval("[\"to-rgba\", [\"to-color\", \"#ff0000\"]]");
            var arr = rgba.AsArray();
            Assert.AreEqual(255.0, arr[0].AsNumber(), 1e-9);
            Assert.AreEqual(0.0, arr[1].AsNumber(), 1e-9);
            Assert.AreEqual(0.0, arr[2].AsNumber(), 1e-9);
            Assert.AreEqual(1.0, arr[3].AsNumber(), 1e-9);
        }

        [Test]
        public void ToColor_NonColorString_IsError()
        {
            bool ok = Expr.TryEval("[\"to-color\", \"not a color\"]", out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void ToRgba_OnNonColor_IsError()
        {
            bool ok = Expr.TryEval("[\"to-rgba\", 5]", out _, out _);
            Assert.IsFalse(ok);
        }
    }
}
