using System.Collections.Generic;
using Unity.Collections;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// Per-layer property-resolution tables shared by every feature's <see cref="IMvtPropertyStore"/> in
    /// that layer: the decoded Keys/Values lists, a key→index map, and the layer's flattened tag words. Built
    /// ONCE by <see cref="MvtDecoder"/>, inside the decode, after the layer's key table is complete — never
    /// lazily on first read. A decoded tile is published across threads via <c>SharedDisposable{IDecodedTile}</c>;
    /// building the map on first use would be a write racing concurrent readers, the same publication
    /// hazard <see cref="MvtLayer.Geometry"/> documents for lazy per-layer geometry materialization.
    /// </summary>
    internal sealed class MvtLayerPropertyResolver : IFeatureKeyResolver, INativeFilterColumns
    {
        private readonly List<string> _keys;
        private readonly NativeArray<MvtValueNative> _values;
        private readonly string[] _valueStrings;
        private readonly Dictionary<string, int> _keyIndex;
        private readonly NativeArray<uint> _tagWords;
        private readonly NativeArray<int> _tagOffsets;
        private readonly NativeArray<int> _tagLengths;

        /// <param name="keys">The layer's key table (declaration order).</param>
        /// <param name="values">The layer's value table (declaration order) — BORROWED: owned by
        /// <see cref="MvtLayer.Values"/>, freed by <see cref="MvtLayer.Dispose"/>. This resolver never
        /// disposes it and must not be read once the owning layer has.</param>
        /// <param name="valueStrings">The layer's value-string side table — <see cref="MvtValueNative.ToValue"/>
        /// indexes into it for every <see cref="ValueType.String"/> entry.</param>
        /// <param name="keyIndex">Key string → index into <paramref name="keys"/>, built by the caller
        /// once <paramref name="keys"/> is complete.</param>
        /// <param name="tagWords">Every feature's (keyIdx,valIdx) pairs, concatenated in feature order —
        /// BORROWED: owned by <see cref="MvtLayer.FeatureTagWords"/>, freed by <see cref="MvtLayer.Dispose"/>.
        /// This resolver never disposes it and must not be read once the owning layer has (see
        /// <see cref="TagWords"/>).</param>
        /// <param name="tagOffsets">Per-feature start index into <paramref name="tagWords"/>, by ordinal —
        /// BORROWED: owned by <see cref="MvtLayer.FeatureTagOffsets"/>, freed by <see cref="MvtLayer.Dispose"/>.</param>
        /// <param name="tagLengths">Per-feature word count into <paramref name="tagWords"/>, by ordinal —
        /// BORROWED: owned by <see cref="MvtLayer.FeatureTagLengths"/>, freed by <see cref="MvtLayer.Dispose"/>.</param>
        public MvtLayerPropertyResolver(
            List<string> keys, NativeArray<MvtValueNative> values, string[] valueStrings,
            Dictionary<string, int> keyIndex, NativeArray<uint> tagWords,
            NativeArray<int> tagOffsets, NativeArray<int> tagLengths)
        {
            _keys = keys;
            _values = values;
            _valueStrings = valueStrings;
            _keyIndex = keyIndex;
            _tagWords = tagWords;
            _tagOffsets = tagOffsets;
            _tagLengths = tagLengths;
        }

        /// <summary>The native-column capability read of a feature's tag slice, by layer ordinal — the seam
        /// a Burst evaluator uses instead of reaching into a <see cref="DensePropertyStore"/>. Also the
        /// single source the store's own managed reads resolve their slice through, so the (offset,count)
        /// lives in one place.</summary>
        public bool TryGetFeatureSlice(int featureOrdinal, out int offset, out int count)
        {
            if ((uint)featureOrdinal >= (uint)_tagOffsets.Length)
            {
                offset = 0;
                count = 0;
                return false;
            }
            offset = _tagOffsets[featureOrdinal];
            count = _tagLengths[featureOrdinal];
            return true;
        }

        /// <summary>The layer's decoded value table, by index — shared (not copied) so a
        /// <see cref="DensePropertyStore"/> can index into it directly.</summary>
        public NativeArray<MvtValueNative> Values => _values;

        /// <summary>The layer's value-string side table — the index space a string-typed
        /// <see cref="MvtValueNative"/> resolves against, forwarded so a <see cref="DensePropertyStore"/>
        /// can pass it to <see cref="MvtValueNative.ToValue"/>.</summary>
        public string[] ValueStrings => _valueStrings;

        /// <summary>Every feature's (keyIdx,valIdx) tag pairs in this layer, concatenated. BORROWED from
        /// <see cref="MvtLayer.FeatureTagWords"/> — do not dispose, and do not read past the owning layer's
        /// <see cref="MvtLayer.Dispose"/>.</summary>
        public NativeArray<uint> TagWords => _tagWords;

        /// <summary>Per-feature start index into <see cref="TagWords"/>, by ordinal — forwarded the same way
        /// as <see cref="TagWords"/> itself, so a batched native-filter job can read the whole column instead
        /// of calling <see cref="TryGetFeatureSlice"/> once per feature. BORROWED from
        /// <see cref="MvtLayer.FeatureTagOffsets"/>.</summary>
        public NativeArray<int> TagOffsets => _tagOffsets;

        /// <summary>Per-feature word count into <see cref="TagWords"/>, by ordinal — the twin of
        /// <see cref="TagOffsets"/>. BORROWED from <see cref="MvtLayer.FeatureTagLengths"/>.</summary>
        public NativeArray<int> TagLengths => _tagLengths;

        /// <summary>True and yields the key's table index when <paramref name="name"/> is one of this
        /// layer's keys.</summary>
        public bool TryGetKeyIndex(string name, out int keyIndex) => _keyIndex.TryGetValue(name, out keyIndex);

        /// <summary>The string→id key hoist's format-neutral seam — forwards to <see cref="TryGetKeyIndex"/>
        /// verbatim, so a per-layer bind site (<c>FeatureSelector</c>) can resolve a filter's key layout
        /// without naming this MVT-specific type.</summary>
        bool IFeatureKeyResolver.TryResolveKey(string name, out int keyIndex) => TryGetKeyIndex(name, out keyIndex);

        /// <summary>
        /// Resolves a feature's (keyIdx,valIdx) tag-word slice into a FRESH dictionary — the same
        /// skip-tolerant walk the pre-D1a <c>MvtDecoder.ResolveProperties</c> used (odd-length slices stop
        /// at the last complete pair; an out-of-range key or value index skips that pair without throwing).
        /// The cold path behind <see cref="DensePropertyStore.AsDictionary"/> — and, being an independent
        /// forward walk of the same tag words <see cref="DensePropertyStore.TryGetByKeyIndex"/> scans
        /// backward, the differential oracle <c>DensePropertyStoreTests</c> pairs against it. Always
        /// allocates a new dictionary — never cached — so a caller may treat the result as its own.
        /// </summary>
        /// <param name="offset">Start index of this feature's pairs into <see cref="TagWords"/>.</param>
        /// <param name="count">Word count of this feature's slice (not pair count — halved below).</param>
        public Dictionary<string, Value> ResolveToDictionary(int offset, int count)
        {
            var result = new Dictionary<string, Value>();
            int pairCount = count / 2;
            for (int i = 0; i < pairCount; i++)
            {
                int keyIdx = (int)_tagWords[offset + i * 2];
                int valIdx = (int)_tagWords[offset + i * 2 + 1];
                if (keyIdx < 0 || keyIdx >= _keys.Count) continue;
                if (valIdx < 0 || valIdx >= _values.Length) continue;
                result[_keys[keyIdx]] = _values[valIdx].ToValue(_valueStrings);
            }
            return result;
        }
    }
}
