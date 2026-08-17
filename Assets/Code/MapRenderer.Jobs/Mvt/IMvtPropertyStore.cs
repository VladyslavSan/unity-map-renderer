using System.Collections.Generic;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// A decoded feature's property bag, behind an interface with two implementations selected by
    /// <see cref="MvtPropertyStorage"/>: <see cref="DictionaryPropertyStore"/> (today's eager per-feature
    /// dictionary, the oracle) and <see cref="DensePropertyStore"/> (keeps MVT's dense (keyIdx,valIdx)
    /// tag-pair representation, resolving lazily). Both implementations must give byte-identical answers —
    /// pinned by the differential test in <c>DensePropertyStoreTests</c>.
    /// </summary>
    internal interface IMvtPropertyStore
    {
        /// <summary>True and yields the value when the feature has property <paramref name="name"/>. The
        /// hot single-key path (<c>CompiledFilter</c>/<c>FeatureSelector</c> call this per feature) — MUST
        /// be allocation-free on both implementations.</summary>
        bool TryGet(string name, out Value value);

        /// <summary>All properties as a read-only map. May allocate; used only by the rare
        /// <c>properties</c> expression and by <c>MvtFeature.Properties</c>.</summary>
        IReadOnlyDictionary<string, Value> AsDictionary();

        /// <summary>The number of properties.</summary>
        int Count { get; }
    }
}
