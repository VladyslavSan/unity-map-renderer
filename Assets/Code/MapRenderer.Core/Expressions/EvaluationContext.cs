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

        public EvaluationContext(double zoom, IFeature feature = null)
        {
            Zoom = zoom;
            Feature = feature;
        }

        /// <summary>A context with a feature but no meaningful zoom (constant/data-driven evaluation).</summary>
        public static EvaluationContext ForFeature(IFeature feature) => new EvaluationContext(0.0, feature);
    }
}
