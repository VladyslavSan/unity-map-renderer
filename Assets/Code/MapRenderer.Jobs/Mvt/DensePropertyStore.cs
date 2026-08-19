using System.Collections.Generic;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// <see cref="IMvtPropertyStore"/> that keeps MVT's dense representation instead of expanding it: a
    /// feature's raw (keyIdx,valIdx) tag pairs, retained instead of discarded after decode, resolved
    /// against the layer's shared <see cref="MvtLayerPropertyResolver"/> only on demand.
    /// <see cref="TryGet"/> — the hot single-key path — allocates nothing: it maps the queried name to a
    /// key index via the layer's shared map, then scans this feature's own (typically few) tag pairs.
    /// <see cref="AsDictionary"/> is the cold path (the rare <c>properties</c> expression / test code),
    /// materializing a fresh dictionary on every call.
    /// </summary>
    internal sealed class DensePropertyStore : IMvtPropertyStore
    {
        private readonly uint[] _rawTags;
        private readonly MvtLayerPropertyResolver _resolver;

        /// <param name="rawTags">This feature's (keyIdx,valIdx) pairs, as decoded.</param>
        /// <param name="resolver">The owning layer's shared Keys/Values/key-index tables.</param>
        public DensePropertyStore(uint[] rawTags, MvtLayerPropertyResolver resolver)
        {
            _rawTags = rawTags ?? System.Array.Empty<uint>();
            _resolver = resolver;
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
            {
                List<MvtValue> values = _resolver.Values;
                int pairCount = _rawTags.Length / 2;
                for (int i = pairCount - 1; i >= 0; i--)
                {
                    if ((int)_rawTags[i * 2] != keyIdx) continue;
                    int valIdx = (int)_rawTags[i * 2 + 1];
                    if (valIdx < 0 || valIdx >= values.Count) continue;
                    value = values[valIdx].ToValue();
                    return true;
                }
            }
            value = Value.Null;
            return false;
        }

        public IReadOnlyDictionary<string, Value> AsDictionary() => _resolver.ResolveToDictionary(_rawTags);

        public int Count => AsDictionary().Count;
    }
}
