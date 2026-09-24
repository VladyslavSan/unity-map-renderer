namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// The constant-key <c>get</c>/<c>has</c> fast form (<c>["get","name"]</c>, key a bare JSON string).
    /// <see cref="_slot"/> is its index in the parser's key layout, which <c>FeatureSelector</c> resolves once
    /// per layer into <see cref="EvaluationContext.KeyBinding"/>. With a binding and an
    /// <see cref="IIndexedFeature"/>, <see cref="Evaluate"/> reads <c>binding[_slot]</c> with no hash;
    /// otherwise it falls back to <see cref="IFeature.TryGetProperty(string, out Value)"/>, same result.
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

            // The int path needs a resolved key layout and an index-capable feature. Read binding[_slot], never
            // binding.Length: a rented buffer may be longer than this filter's layout.
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
