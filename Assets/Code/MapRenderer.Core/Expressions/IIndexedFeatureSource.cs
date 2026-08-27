namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// The capability probe a per-layer bind site (<c>FeatureSelector</c>) uses to decide whether it can
    /// hoist a filter's key lookups: implemented by a tile layer that carries a key table, exposing its
    /// <see cref="IFeatureKeyResolver"/> only when one exists. Probed with <c>as</c>, never assumed — a
    /// layer that does not implement this (GeoJSON, any future format with no key table) simply has no
    /// binding built for it, so its features stay on the string path.
    /// </summary>
    public interface IIndexedFeatureSource
    {
        /// <summary>This layer's key resolver, or <c>null</c> when the layer is not index-capable
        /// (GeoJSON). <c>MvtLayer</c> always answers non-null — every MVT layer carries a key table.
        /// </summary>
        IFeatureKeyResolver KeyResolver { get; }
    }
}
