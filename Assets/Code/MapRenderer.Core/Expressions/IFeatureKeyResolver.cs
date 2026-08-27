namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// The per-layer key→index resolve step the string→id key hoist runs once, instead of per feature.
    /// Format-neutral by design (no MVT/GeoJSON type named here) — implemented by
    /// <c>MvtLayerPropertyResolver</c> (<c>MapRenderer.Jobs.Mvt</c>), the only format with a key table to
    /// resolve against.
    /// </summary>
    public interface IFeatureKeyResolver
    {
        /// <summary>True and yields the key's table index when <paramref name="name"/> is one of the
        /// owning layer's keys — the same presence contract <c>MvtLayerPropertyResolver.TryGetKeyIndex</c>
        /// already has.</summary>
        bool TryResolveKey(string name, out int keyIndex);
    }
}
