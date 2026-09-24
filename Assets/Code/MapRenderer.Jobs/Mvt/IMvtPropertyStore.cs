using System.Collections.Generic;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// A decoded feature's property bag; the sole implementation is <see cref="DensePropertyStore"/>.
    /// <see cref="DensePropertyStore.TryGet"/> (the backward tag-pair scan) and
    /// <see cref="DensePropertyStore.AsDictionary"/> (the forward <c>ResolveToDictionary</c> walk) are two
    /// independent implementations of the same resolve, kept in agreement by the differential test in
    /// <c>DensePropertyStoreTests</c>.
    /// </summary>
    internal interface IMvtPropertyStore
    {
        /// <summary>True and yields the value when the feature has property <paramref name="name"/>. The
        /// hot single-key path (<c>CompiledFilter</c>/<c>FeatureSelector</c> call this per feature) — MUST
        /// be allocation-free.</summary>
        bool TryGet(string name, out Value value);

        /// <summary>
        /// The int-keyed twin of <see cref="TryGet"/>: true and yields the value when the feature has a
        /// tag pair whose key index is <paramref name="keyIndex"/> — the layer's own declaration-order key
        /// table index, resolved once per layer by the string→id key hoist rather than per feature.
        /// </summary>
        bool TryGetByKeyIndex(int keyIndex, out Value value);

        /// <summary>All properties as a read-only map. May allocate; used only by the rare
        /// <c>properties</c> expression and by <c>MvtFeature.Properties</c>.</summary>
        IReadOnlyDictionary<string, Value> AsDictionary();

        /// <summary>The number of properties.</summary>
        int Count { get; }
    }
}
