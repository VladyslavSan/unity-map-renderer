// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Tests/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.GeoJson;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests.Expressions
{
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
