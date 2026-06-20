namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// How an expression's value depends on its inputs — the classification S11/S12 use to decide where to
    /// evaluate it. This is the spec's "kind" of a property value, with the brief's three-way scheme
    /// (constant / zoom / data-driven) refined to a four-way lattice so a pure-zoom expression (→ per-frame
    /// uniform, S11) is distinguished from one that also depends on the feature (→ per-vertex attribute,
    /// S12):
    ///   - <see cref="Constant"/>  : no zoom, no feature — evaluate once.
    ///   - <see cref="Zoom"/>      : depends on camera zoom only ("camera expression").
    ///   - <see cref="Feature"/>   : depends on feature data only ("source"/"data-driven" expression).
    ///   - <see cref="Composite"/> : depends on both zoom and feature ("composite expression").
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
