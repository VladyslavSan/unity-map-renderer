namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// The inputs an expression evaluates against: the current map <see cref="Zoom"/> (for the <c>zoom</c>
    /// expression / ramp inputs) and the <see cref="Feature"/> being styled (for feature-data expressions).
    /// Either may be absent depending on context — evaluating a feature-data expression with a null feature
    /// is an evaluation error, surfaced through the boundary.
    /// </summary>
    public readonly struct EvaluationContext
    {
        public double Zoom { get; }
        public IFeature Feature { get; }

        /// <summary>
        /// The key hoist's resolved <c>slot→key-index</c> map for THIS call's compiled filter, or <c>null</c>.
        /// It is per-<c>SelectFeatures</c>-call and thread-local, never stored on the shared expression node or
        /// the cross-thread decoded tile (the race <c>MvtLayerPropertyResolver</c> forbids), so <c>null</c> means
        /// "use the string path". <c>Ops.FeatureKeyExpression</c> is the sole reader: a slot index means nothing
        /// outside this filter's key layout, so never pass it into another expression's evaluation.
        /// </summary>
        public int[] KeyBinding { get; }

        public EvaluationContext(double zoom, IFeature feature = null, int[] keyBinding = null)
        {
            Zoom = zoom;
            Feature = feature;
            KeyBinding = keyBinding;
        }

        /// <summary>A context with a feature but no meaningful zoom (constant/data-driven evaluation).</summary>
        public static EvaluationContext ForFeature(IFeature feature) => new EvaluationContext(0.0, feature);
    }
}
