// A test double, so it lives in the test assembly rather than in engine-free Core. Engine-free, so
// core-tests compiles it too.

using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests
{
    /// <summary>
    /// An in-memory <see cref="IFeature"/> built from a dictionary, for expression and filter tests. Its
    /// optional <see cref="Geometry"/> command stream (<see cref="ITileCommandStreamFeature"/>) also feeds
    /// the mesh builders through <c>InMemoryTileLayer</c>. A null stream materializes as zero commands, so
    /// the feature contributes no ring, as a real geometry-less feature does.
    /// </summary>
    public sealed class DictionaryFeature : IFeature, ITileCommandStreamFeature
    {
        private readonly Dictionary<string, Value> _properties;

        public TileGeometryType GeometryType { get; }
        public Value Id { get; }

        /// <summary>MVT command-stream geometry, or null when this double is only standing in for the
        /// property bag.</summary>
        public uint[] Geometry { get; }

        public DictionaryFeature(
            IReadOnlyDictionary<string, Value> properties = null,
            TileGeometryType geometryType = TileGeometryType.Unknown,
            bool hasId = false,
            Value id = default,
            uint[] geometry = null)
        {
            Geometry = geometry;
            _properties = new Dictionary<string, Value>();
            if (properties != null)
                foreach (var kv in properties)
                    _properties[kv.Key] = kv.Value;
            GeometryType = geometryType;
            Id = hasId ? id : Value.Null;
        }

        public bool TryGetProperty(string name, out Value value)
            => _properties.TryGetValue(name ?? string.Empty, out value);

        public IReadOnlyDictionary<string, Value> Properties => _properties;
    }
}
