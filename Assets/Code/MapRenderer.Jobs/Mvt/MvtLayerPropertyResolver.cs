using System.Collections.Generic;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// Per-layer property-resolution tables shared by every feature's <see cref="IMvtPropertyStore"/> in
    /// that layer: the decoded Keys/Values lists plus a key→index map. Built ONCE by
    /// <see cref="MvtDecoder"/>, inside the decode, after the layer's key table is complete — never lazily
    /// on first read. A decoded tile is published across threads via <c>SharedDisposable{IDecodedTile}</c>;
    /// building the map on first use would be a write racing concurrent readers, the same publication
    /// hazard <see cref="MvtLayer.Geometry"/> documents for lazy per-layer geometry materialization.
    /// </summary>
    internal sealed class MvtLayerPropertyResolver
    {
        private readonly List<string> _keys;
        private readonly List<MvtValue> _values;
        private readonly Dictionary<string, int> _keyIndex;

        /// <param name="keys">The layer's key table (declaration order).</param>
        /// <param name="values">The layer's value table (declaration order).</param>
        /// <param name="keyIndex">Key string → index into <paramref name="keys"/>, built by the caller
        /// once <paramref name="keys"/> is complete.</param>
        public MvtLayerPropertyResolver(List<string> keys, List<MvtValue> values, Dictionary<string, int> keyIndex)
        {
            _keys = keys;
            _values = values;
            _keyIndex = keyIndex;
        }

        /// <summary>The layer's decoded value table, by index — shared (not copied) so a
        /// <see cref="DensePropertyStore"/> can index into it directly.</summary>
        public List<MvtValue> Values => _values;

        /// <summary>True and yields the key's table index when <paramref name="name"/> is one of this
        /// layer's keys.</summary>
        public bool TryGetKeyIndex(string name, out int keyIndex) => _keyIndex.TryGetValue(name, out keyIndex);

        /// <summary>
        /// Resolves a feature's raw (keyIdx,valIdx) tag pairs into a FRESH dictionary — the same
        /// skip-tolerant walk the pre-D1a <c>MvtDecoder.ResolveProperties</c> used (odd-length arrays stop
        /// at the last complete pair; an out-of-range key or value index skips that pair without throwing).
        /// Shared by the Dictionary store's eager build and the Dense store's cold
        /// <c>DensePropertyStore.AsDictionary</c>. Always allocates a new dictionary — never cached — so a
        /// caller may treat the result as its own.
        /// </summary>
        public Dictionary<string, Value> ResolveToDictionary(uint[] rawTags)
        {
            var result = new Dictionary<string, Value>();
            if (rawTags == null) return result;
            int pairCount = rawTags.Length / 2;
            for (int i = 0; i < pairCount; i++)
            {
                int keyIdx = (int)rawTags[i * 2];
                int valIdx = (int)rawTags[i * 2 + 1];
                if (keyIdx < 0 || keyIdx >= _keys.Count) continue;
                if (valIdx < 0 || valIdx >= _values.Count) continue;
                result[_keys[keyIdx]] = _values[valIdx].ToValue();
            }
            return result;
        }
    }
}
