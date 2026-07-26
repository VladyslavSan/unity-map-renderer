using System.Collections.Generic;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Core.Expressions
{
    /// <summary>
    /// The feature-data surface an expression can query: properties (<c>get</c>/<c>has</c>/<c>properties</c>),
    /// geometry type (<c>geometry-type</c>), and feature id (<c>id</c>). An abstraction so the expression
    /// engine and filter layer can run against any property source, not just a decoded MVT feature
    /// (<c>MvtFeature</c> in <c>MapRenderer.Core.Mvt</c> implements this directly — Epic A / A6 folded the
    /// retired <c>MvtFeatureAdapter</c> into it).
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
}
