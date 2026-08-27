using Unity.Collections;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// The native-column capability a Burst filter evaluator reads a feature's inputs through — so a deep
    /// evaluator depends on THIS, not on a concrete <see cref="IMvtPropertyStore"/> implementation. A
    /// source that carries MVT's dense native representation (today only <see cref="MvtLayerPropertyResolver"/>,
    /// but the seam is the extension point for future native tile sources) advertises it; a source that does
    /// not simply has no native fast-path built for it and stays on the managed <see cref="IMvtPropertyStore"/>
    /// path. Probed as a capability, exactly like <see cref="MapRenderer.Core.Expressions.IIndexedFeatureSource"/>
    /// gates the key hoist — never by testing the concrete store type.
    /// </summary>
    internal interface INativeFilterColumns
    {
        /// <summary>The layer's flattened (keyIdx,valIdx) tag-word pairs, concatenated in feature order.</summary>
        NativeArray<uint> TagWords { get; }

        /// <summary>The layer's decoded value table, indexed by valIdx.</summary>
        NativeArray<MvtValueNative> Values { get; }

        /// <summary>This feature's slice into <see cref="TagWords"/>: its start index and word count (not
        /// pair count), by the feature's layer ordinal. False for an out-of-range ordinal.</summary>
        bool TryGetFeatureSlice(int featureOrdinal, out int offset, out int count);
    }
}
