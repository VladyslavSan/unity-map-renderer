namespace MapRenderer.Core.Expressions.Ops
{
    /// <summary>
    /// <c>["zoom"]</c>: the current map zoom level. A camera (Zoom-kind) leaf. Per spec, <c>zoom</c> may
    /// only appear as the input to a top-level <c>step</c>/<c>interpolate</c> (validated by the parser); at
    /// evaluation time it simply yields the context zoom.
    /// </summary>
    public sealed class ZoomExpression : Expression
    {
        public override ValueType ResultType => ValueType.Number;
        public override ExpressionKind Kind => ExpressionKind.Zoom;
        public override Value Evaluate(in EvaluationContext context) => Value.Number(context.Zoom);
    }

    /// <summary>
    /// A feature-data leaf (<c>get</c>/<c>has</c>/<c>properties</c>/<c>geometry-type</c>/<c>id</c>): depends
    /// on the feature, so it is classified <see cref="ExpressionKind.Feature"/>. The evaluation delegate
    /// reads the context's feature; a null feature is an evaluation error.
    /// </summary>
    public sealed class FeatureDataExpression : Expression
    {
        public delegate Value Impl(in EvaluationContext context);

        private readonly Impl _impl;
        private readonly ValueType _resultType;
        private readonly ExpressionKind _kind;

        // dependsOnFeature defaults true; the two-arg get(obj,key) form (object not feature) is built as a
        // FunctionExpression instead, so this node is only used for the feature-reading forms.
        public FeatureDataExpression(Impl impl, ValueType resultType, ExpressionKind kind = ExpressionKind.Feature)
        {
            _impl = impl;
            _resultType = resultType;
            _kind = kind;
        }

        public override ValueType ResultType => _resultType;
        public override ExpressionKind Kind => _kind;

        public override Value Evaluate(in EvaluationContext context) => _impl(context);
    }
}
