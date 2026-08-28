using System.Collections.Generic;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// The <see cref="IMvtPropertyStore"/> Null Object — every query answers "absent". It is the default
    /// <see cref="MvtFeature.Store"/>, which is what lets that field be non-nullable (and its readers
    /// guard-free): a feature the decoder never gave a real store, or a test double built without one,
    /// holds this instead of null and simply has no properties. Stateless, so one shared instance serves
    /// all such features.
    /// </summary>
    internal sealed class EmptyPropertyStore : IMvtPropertyStore
    {
        /// <summary>The shared instance — the store is stateless, so every empty feature can share it.</summary>
        internal static readonly EmptyPropertyStore Instance = new EmptyPropertyStore();

        private static readonly IReadOnlyDictionary<string, Value> Empty = new Dictionary<string, Value>();

        private EmptyPropertyStore() { }

        /// <summary>Always absent.</summary>
        public bool TryGet(string name, out Value value) { value = Value.Null; return false; }

        /// <summary>Always absent.</summary>
        public bool TryGetByKeyIndex(int keyIndex, out Value value) { value = Value.Null; return false; }

        /// <summary>The shared empty map.</summary>
        public IReadOnlyDictionary<string, Value> AsDictionary() => Empty;

        /// <summary>Always zero.</summary>
        public int Count => 0;
    }
}
