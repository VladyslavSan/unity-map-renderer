using System;
using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Core.GeoJson
{
    /// <summary>Thrown when input violates RFC 7946, or uses a construct this source does not
    /// support (a GeometryCollection, an antimeridian-crossing segment). Loud beats silently-wrong: this
    /// source exists to serve declarative fixtures, where malformed input must fail, not render less.</summary>
    public sealed class GeoJsonFormatException : Exception
    {
        public GeoJsonFormatException(string message) : base(message) { }
    }

    /// <summary>
    /// One parsed RFC 7946 feature, still geodetic, with Multi* flattened as in MVT: points and lines become N
    /// paths; polygons concatenate rings, grouped by <see cref="PolygonRingCounts"/>. Non-local invariant:
    /// coordinates are latitude-first (<see cref="GeoJsonParser"/> swaps RFC §3.1.1 once), and ring winding
    /// encodes role (exterior CW-on-screen in tile space, holes opposite) for <c>RingAssemblyJob</c>. It is the
    /// <see cref="IFeature"/> itself, carried by reference into every tile, so filters read authored properties.
    /// </summary>
    public sealed class GeoJsonFeature : IFeature
    {
        /// <summary>The RFC <c>id</c>, string or number (RFC 7946 §3.2), or <see cref="Value.Null"/> when the
        /// RFC <c>id</c> member is absent. Stored as a <see cref="Value"/>: a string id keeps its exact text,
        /// a numeric id is a <c>Value.Number</c> (double) — so, as with an MVT id, an integer above 2⁵³
        /// narrows.</summary>
        public Value Id { get; init; }

        /// <summary>Never null — <c>properties: null</c> yields an EMPTY map, because
        /// <c>IFeature.Properties</c> is consumed unconditionally by the <c>properties</c> expression.</summary>
        public IReadOnlyDictionary<string, Value> Properties { get; init; }

        public TileGeometryType GeometryType { get; init; }

        /// <summary>Rings (polygons), polylines (lines), or single-coordinate paths (points).</summary>
        public IReadOnlyList<IReadOnlyList<GeoCoordinate>> Paths { get; init; }

        /// <summary>Rings per polygon — one entry per polygon of a (Multi)Polygon, summing to
        /// <c>Paths.Count</c>; EMPTY for points and lines. The slicer needs this to drop a polygon's holes
        /// when its exterior clips away: a surviving orphan hole would become the feature's FIRST ring, set
        /// the exterior sign itself, and render as an inverted patch.</summary>
        public IReadOnlyList<int> PolygonRingCounts { get; init; }

        /// <summary>The one <see cref="IFeature"/> member the parsed shape did not already have. Forwards to
        /// <see cref="Properties"/>, which the parser guarantees non-null.</summary>
        public bool TryGetProperty(string name, out Value value)
        {
            if (Properties != null && name != null && Properties.TryGetValue(name, out value))
                return true;
            value = Value.Null;
            return false;
        }
    }

    /// <summary>A parsed RFC 7946 document, flattened to features. A bare geometry or a single Feature root
    /// yields a one-feature dataset (RFC §3).</summary>
    public sealed class GeoJsonDataset
    {
        public IReadOnlyList<GeoJsonFeature> Features { get; init; }
    }
}
