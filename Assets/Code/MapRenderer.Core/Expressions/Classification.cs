namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// How an expression's value depends on its inputs, which decides where to evaluate it. The spec's
    /// three-way scheme becomes four, so a pure-zoom expression (a per-frame uniform) differs from one that
    /// also reads the feature (a per-vertex attribute): <see cref="Constant"/> (evaluate once),
    /// <see cref="Zoom"/> (camera only), <see cref="Feature"/> (data-driven), <see cref="Composite"/> (both).
    /// </summary>
    public enum ExpressionKind
    {
        Constant,
        Zoom,
        Feature,
        Composite
    }

    public static class ExpressionKinds
    {
        /// <summary>
        /// Combine the kinds of sub-expressions: dependence on zoom and on feature data are each
        /// monotonic (once present they stay present), and Composite = both. This is the lattice join.
        /// </summary>
        public static ExpressionKind Combine(ExpressionKind a, ExpressionKind b)
        {
            bool zoom = DependsOnZoom(a) || DependsOnZoom(b);
            bool feature = DependsOnFeature(a) || DependsOnFeature(b);
            return Of(zoom, feature);
        }

        public static ExpressionKind Of(bool dependsOnZoom, bool dependsOnFeature)
        {
            if (dependsOnZoom && dependsOnFeature) return ExpressionKind.Composite;
            if (dependsOnZoom) return ExpressionKind.Zoom;
            if (dependsOnFeature) return ExpressionKind.Feature;
            return ExpressionKind.Constant;
        }

        public static bool DependsOnZoom(ExpressionKind k)
            => k == ExpressionKind.Zoom || k == ExpressionKind.Composite;

        public static bool DependsOnFeature(ExpressionKind k)
            => k == ExpressionKind.Feature || k == ExpressionKind.Composite;
    }
}
