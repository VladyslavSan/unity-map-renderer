// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Tests/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests.Expressions
{
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
