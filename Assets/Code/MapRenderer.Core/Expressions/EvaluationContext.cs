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
        /// The string→id key hoist's resolved <c>slot→key-index</c> map for THIS call's compiled filter, or
        /// <c>null</c>. Per-<c>SelectFeatures</c>-call and thread-local by construction — never written onto
        /// the shared expression node or the cross-thread-published decoded tile (the exact race
        /// <c>MvtLayerPropertyResolver</c>'s own doc forbids) — so a null binding always means "use the
        /// string path", never "not yet resolved". <c>Ops.FeatureKeyExpression</c> is the sole reader; a
        /// slot index is meaningless against any binding other than the one built for the SAME compiled
        /// filter's key layout, so this field must never be threaded into an unrelated expression's
        /// evaluation (e.g. a <c>StyleProperty</c> paint expression) — no call site does that this stage.
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
