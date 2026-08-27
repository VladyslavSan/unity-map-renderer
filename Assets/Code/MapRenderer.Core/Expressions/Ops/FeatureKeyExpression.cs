namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// The constant-key <c>get</c>/<c>has</c> fast form: <c>["get","name"]</c> / <c>["has","name"]</c>
    /// where the key is a bare JSON string, parsed by <see cref="ExpressionParser"/> instead of the
    /// dynamic-key <c>FeatureDataExpression</c> closure. Carries <see cref="_slot"/>, its position in the
    /// owning <see cref="ExpressionParser"/>'s key layout — the string→id key hoist's per-filter index a
    /// bind site (<c>FeatureSelector</c>) resolves once per layer into <see cref="EvaluationContext.KeyBinding"/>,
    /// instead of this node hashing <see cref="_key"/> on every feature.
    ///
    /// <para><b>Two paths, same result.</b> With a non-null binding AND an <see cref="IIndexedFeature"/>
    /// feature, <see cref="Evaluate"/> reads <c>binding[_slot]</c> (O(1), no hash) and answers via
    /// <see cref="IIndexedFeature.TryGetPropertyByKeyIndex"/>. Otherwise it falls back to
    /// <see cref="IFeature.TryGetProperty(string, out Value)"/> — byte-identical to what the retired
    /// closure computed, and the ONLY path for GeoJSON/test doubles, none of which implement
    /// <see cref="IIndexedFeature"/>.</para>
    /// </summary>
    public sealed class FeatureKeyExpression : Expression
    {
        private readonly string _key;
        private readonly int _slot;
        private readonly bool _isHas;

        /// <param name="key">The literal key name (the string fallback path, and the name resolved into
        /// <see cref="_slot"/> by a bind site).</param>
        /// <param name="slot">This node's position in its <see cref="ExpressionParser"/> instance's key
        /// layout — the index into <see cref="EvaluationContext.KeyBinding"/> this node reads.</param>
        /// <param name="isHas">True for <c>has</c> (returns a presence bool); false for <c>get</c> (returns
        /// the value, or <see cref="Value.Null"/> when absent).</param>
        public FeatureKeyExpression(string key, int slot, bool isHas)
        {
            _key = key;
            _slot = slot;
            _isHas = isHas;
        }

        public override ValueType ResultType => _isHas ? ValueType.Boolean : ValueType.Value;
        public override ExpressionKind Kind => ExpressionKind.Feature;

        public override Value Evaluate(in EvaluationContext context)
        {
            if (context.Feature == null)
                throw new ExpressionEvaluationException(
                    _isHas ? "has: no feature in context." : "get: no feature in context.");

            // The int path: only taken when a bind site resolved this filter's key layout AND the feature
            // is index-capable. binding[_slot] is a SLOT read, never binding.Length (the oversized-array
            // pooling rule) — a rented buffer may be longer than this filter's layout.
            if (context.KeyBinding != null && context.Feature is IIndexedFeature indexed)
            {
                int keyIndex = context.KeyBinding[_slot];
                if (keyIndex < 0)
                    return _isHas ? Value.Bool(false) : Value.Null;
                bool foundByIndex = indexed.TryGetPropertyByKeyIndex(keyIndex, out Value indexedValue);
                return _isHas ? Value.Bool(foundByIndex) : (foundByIndex ? indexedValue : Value.Null);
            }

            // The string path — byte-identical to the retired per-feature closure.
            bool found = context.Feature.TryGetProperty(_key, out Value value);
            return _isHas ? Value.Bool(found) : (found ? value : Value.Null);
        }
    }
}
