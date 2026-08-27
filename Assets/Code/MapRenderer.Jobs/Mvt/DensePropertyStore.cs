using System.Collections.Generic;
using Unity.Collections;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// <see cref="IMvtPropertyStore"/> that keeps MVT's dense representation instead of expanding it: a
    /// VIEW — <c>(offset, count)</c> — into the owning layer's shared tag-word buffer
    /// (<see cref="MvtLayerPropertyResolver.TagWords"/>), resolved against the layer's shared
    /// <see cref="MvtLayerPropertyResolver"/> only on demand. <see cref="TryGet"/> — the hot single-key
    /// path — allocates nothing: it maps the queried name to a key index via the layer's shared map, then
    /// scans its (typically few) tag pairs within that view.
    /// <see cref="AsDictionary"/> is the cold path (the rare <c>properties</c> expression / test code),
    /// materializing a fresh dictionary on every call.
    ///
    /// <para><b>Lifetime.</b> Because this is a view rather than an owner, a store's (and therefore its
    /// <see cref="MvtFeature"/>'s) readable lifetime is bounded by its owning <see cref="MvtLayer"/>'s: once
    /// <see cref="MvtLayer.Dispose"/> frees <see cref="MvtLayer.FeatureTagWords"/>, reading through this
    /// store is a use-after-free on the underlying native buffer, not merely a double-free risk. No store,
    /// feature or resolver may be read once its owning layer has been disposed.</para>
    /// </summary>
    internal sealed class DensePropertyStore : IMvtPropertyStore
    {
        private readonly MvtLayerPropertyResolver _resolver;
        private readonly int _tagOffset;
        private readonly int _tagCount;

        /// <param name="resolver">The owning layer's shared Keys/Values/key-index/tag-words tables.</param>
        /// <param name="tagOffset">Start index of this feature's (keyIdx,valIdx) pairs into
        /// <see cref="MvtLayerPropertyResolver.TagWords"/>.</param>
        /// <param name="tagCount">Word count of this feature's slice (not pair count).</param>
        public DensePropertyStore(MvtLayerPropertyResolver resolver, int tagOffset, int tagCount)
        {
            _resolver = resolver;
            _tagOffset = tagOffset;
            _tagCount = tagCount;
        }

        /// <summary>
        /// Skip-tolerant like the eager resolve it replaces: walks this feature's pairs BACKWARD from the
        /// end, returning the value of the first pair found (i.e. the LAST in tag order) whose key index
        /// matches AND whose value index is in range. That mirrors the forward walk's last-write-wins
        /// overwrite of a dictionary keyed by string — a pair with a matching key index but an
        /// out-of-range value index is skipped rather than adopted, exactly as the forward build would
        /// leave an earlier valid value for that key in place instead of clobbering it with an invalid one.
        /// </summary>
        public bool TryGet(string name, out Value value)
        {
            if (_resolver.TryGetKeyIndex(name, out int keyIdx))
                return TryGetByKeyIndex(keyIdx, out value);
            value = Value.Null;
            return false;
        }

        /// <summary>
        /// The int-keyed twin of <see cref="TryGet"/>, extracted so a caller that already resolved
        /// <paramref name="keyIndex"/> (the string→id key hoist — the filter-selection bind step in
        /// <c>FeatureSelector</c>) skips the name→index <see cref="MvtLayerPropertyResolver.TryGetKeyIndex"/>
        /// lookup. Holds the same backward tag-pair scan as <see cref="TryGet"/> verbatim — one copy of the
        /// scan, so both callers, and the differential oracle covering <see cref="TryGet"/>, exercise
        /// identical logic.
        /// </summary>
        public bool TryGetByKeyIndex(int keyIndex, out Value value)
        {
            NativeArray<MvtValueNative> values = _resolver.Values;
            var words = _resolver.TagWords;
            int pairCount = _tagCount / 2;
            for (int i = pairCount - 1; i >= 0; i--)
            {
                if ((int)words[_tagOffset + i * 2] != keyIndex) continue;
                int valIdx = (int)words[_tagOffset + i * 2 + 1];
                if (valIdx < 0 || valIdx >= values.Length) continue;
                value = values[valIdx].ToValue(_resolver.ValueStrings);
                return true;
            }
            value = Value.Null;
            return false;
        }

        public IReadOnlyDictionary<string, Value> AsDictionary() => _resolver.ResolveToDictionary(_tagOffset, _tagCount);

        public int Count => AsDictionary().Count;
    }
}
