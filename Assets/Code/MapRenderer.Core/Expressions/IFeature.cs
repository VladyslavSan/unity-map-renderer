using System.Collections.Generic;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// The feature-data surface an expression can query: properties (<c>get</c>/<c>has</c>/<c>properties</c>),
    /// geometry type (<c>geometry-type</c>), and feature id (<c>id</c>). This is an abstraction so the
    /// expression engine and filter layer can be tested against synthetic features (see
    /// <see cref="DictionaryFeature"/>) as well as real decoded MVT features (<c>MvtFeature</c> in
    /// <c>MapRenderer.Core.Mvt</c> implements this directly — Epic A / A6 folded the retired
    /// <c>MvtFeatureAdapter</c> into it).
    /// </summary>
    public interface IFeature
    {
        /// <summary>The feature's geometry type. Maps to the spec strings Point/LineString/Polygon.</summary>
        TileGeometryType GeometryType { get; }

        /// <summary>True when the feature has an id (the <c>id</c> expression is an error otherwise — spec).</summary>
        bool HasId { get; }

        /// <summary>The feature id (string or number per MVT); valid only when <see cref="HasId"/>.</summary>
        Value Id { get; }

        /// <summary>True and yields the property value when the feature has property <paramref name="name"/>.</summary>
        bool TryGetProperty(string name, out Value value);

        /// <summary>All properties as a read-only map (backs the <c>properties</c> expression).</summary>
        IReadOnlyDictionary<string, Value> Properties { get; }
    }

    /// <summary>
    /// A simple in-memory <see cref="IFeature"/> built from a dictionary. Used by tests and by callers that
    /// already have decoded properties; engine-free.
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
