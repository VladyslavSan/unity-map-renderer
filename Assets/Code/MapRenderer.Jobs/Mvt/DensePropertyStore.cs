using System.Collections.Generic;
using Unity.Collections;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// <see cref="IMvtPropertyStore"/> that keeps MVT's dense representation: a view into the owning layer's
    /// shared tag-word buffer, resolved through <see cref="MvtLayerPropertyResolver"/> on demand.
    /// <see cref="TryGet"/> is the hot path and allocates nothing; <see cref="AsDictionary"/> is cold.
    /// Non-local invariant: the store is a view, so a read after <see cref="MvtLayer.Dispose"/> is a
    /// use-after-free; no store, feature or resolver is read after its layer is disposed.
    /// </summary>
    internal sealed class DensePropertyStore : IMvtPropertyStore
    {
        private readonly MvtLayerPropertyResolver _resolver;
        private readonly int _ordinal;

        /// <param name="resolver">The owning layer's shared Keys/Values/key-index/tag-word tables AND the
        /// per-feature (offset,count) columns this store's slice is read from.</param>
        /// <param name="ordinal">This feature's layer ordinal. The store resolves its slice through
        /// <see cref="MvtLayerPropertyResolver.TryGetFeatureSlice"/> on demand and never copies it.</param>
        public DensePropertyStore(MvtLayerPropertyResolver resolver, int ordinal)
        {
            _resolver = resolver;
            _ordinal = ordinal;
        }

        /// <summary>
        /// Walks this feature's pairs backward and returns the last pair in tag order whose key index
        /// matches and whose value index is in range. Non-obvious why: this matches the forward,
        /// last-write-wins dictionary build, which skips a pair with an out-of-range value index.
        /// </summary>
        public bool TryGet(string name, out Value value)
        {
            if (_resolver.TryGetKeyIndex(name, out int keyIdx))
                return TryGetByKeyIndex(keyIdx, out value);
            value = Value.Null;
            return false;
        }

        /// <summary>
        /// The int-keyed twin of <see cref="TryGet"/>, for a caller that already resolved
        /// <paramref name="keyIndex"/> (the key hoist in <c>FeatureSelector</c>). It holds the one copy of
        /// the backward tag-pair scan, so <see cref="TryGet"/> and its differential oracle share the logic.
        /// </summary>
        public bool TryGetByKeyIndex(int keyIndex, out Value value)
        {
            if (!_resolver.TryGetFeatureSlice(_ordinal, out int tagOffset, out int tagCount))
            {
                value = Value.Null;
                return false;
            }
            NativeArray<MvtValueNative> values = _resolver.Values;
            var words = _resolver.TagWords;
            int pairCount = tagCount / 2;
            for (int i = pairCount - 1; i >= 0; i--)
            {
                if ((int)words[tagOffset + i * 2] != keyIndex) continue;
                int valIdx = (int)words[tagOffset + i * 2 + 1];
                if (valIdx < 0 || valIdx >= values.Length) continue;
                value = values[valIdx].ToValue(_resolver.ValueStrings);
                return true;
            }
            value = Value.Null;
            return false;
        }

        public IReadOnlyDictionary<string, Value> AsDictionary()
            => _resolver.TryGetFeatureSlice(_ordinal, out int offset, out int count)
                ? _resolver.ResolveToDictionary(offset, count)
                : new Dictionary<string, Value>();

        public int Count => AsDictionary().Count;
    }
}
