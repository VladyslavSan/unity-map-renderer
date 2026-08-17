using System.Collections.Generic;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Jobs.Mvt
{
    /// <summary>
    /// <see cref="IMvtPropertyStore"/> backed by an already-resolved dictionary — today's behaviour,
    /// unchanged: the decoder expands a feature's tag pairs into a map eagerly, once, at decode time.
    /// Default storage (<see cref="MvtPropertyStorage.Dictionary"/>) and the equivalence oracle for
    /// <see cref="DensePropertyStore"/>.
    /// </summary>
    internal sealed class DictionaryPropertyStore : IMvtPropertyStore
    {
        private static readonly Dictionary<string, Value> Empty = new Dictionary<string, Value>();

        private readonly IReadOnlyDictionary<string, Value> _properties;

        /// <param name="properties">The feature's resolved property map; <c>null</c> is treated as empty.</param>
        public DictionaryPropertyStore(IReadOnlyDictionary<string, Value> properties) =>
            _properties = properties ?? Empty;

        public bool TryGet(string name, out Value value) => _properties.TryGetValue(name, out value);
        public IReadOnlyDictionary<string, Value> AsDictionary() => _properties;
        public int Count => _properties.Count;
    }
}
