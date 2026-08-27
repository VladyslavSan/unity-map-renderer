namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// The int-keyed twin of <see cref="IFeature.TryGetProperty(string, out Value)"/>: implemented only by
    /// a feature whose layer resolved key names to table indices once, up front (the string→id key hoist),
    /// so a per-feature read can skip the name lookup. Probed with <c>as</c> by
    /// <see cref="Ops.FeatureKeyExpression"/> — the sole caller — which falls back to the string path when
    /// a feature does not implement this (GeoJSON, test doubles).
    /// </summary>
    public interface IIndexedFeature
    {
        /// <summary>True and yields the value when this feature has a property at
        /// <paramref name="keyIndex"/> — an index into its OWNING LAYER's key table, never a value this
        /// feature invents on its own. A negative or otherwise unresolved index (the layer has no such
        /// key) must return false, mirroring <see cref="IFeature.TryGetProperty"/>'s absent-key
        /// contract.</summary>
        bool TryGetPropertyByKeyIndex(int keyIndex, out Value value);
    }
}
