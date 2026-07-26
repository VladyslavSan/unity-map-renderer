// A test double, so it lives in the test assembly rather than in engine-free Core (where it shipped
// until the test-only-code cleanup). Engine-free, so core-tests compiles it too.

using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// A simple in-memory <see cref="IFeature"/> built from a dictionary — lets the expression engine and
    /// filter layer be exercised against synthetic features instead of decoded MVT ones.
    /// </summary>
    public sealed class DictionaryFeature : IFeature
    {
        private readonly Dictionary<string, Value> _properties;

        public TileGeometryType GeometryType { get; }
        public bool HasId { get; }
        public Value Id { get; }

        public DictionaryFeature(
            IReadOnlyDictionary<string, Value> properties = null,
            TileGeometryType geometryType = TileGeometryType.Unknown,
            bool hasId = false,
            Value id = default)
        {
            _properties = new Dictionary<string, Value>();
            if (properties != null)
                foreach (var kv in properties)
                    _properties[kv.Key] = kv.Value;
            GeometryType = geometryType;
            HasId = hasId;
            Id = hasId ? id : Value.Null;
        }

        public bool TryGetProperty(string name, out Value value)
            => _properties.TryGetValue(name ?? string.Empty, out value);

        public IReadOnlyDictionary<string, Value> Properties => _properties;
    }
}
