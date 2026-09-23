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
    /// One parsed RFC 7946 feature, still geodetic. Multi* geometries are FLATTENED here, because
    /// <see cref="TileGeometryType"/> — like the MVT spec it mirrors — draws no Multi* distinction:
    /// <list type="table">
    /// <item><term>Point / MultiPoint</term><description><c>Point</c>; N paths of one coordinate each, the
    /// shape <c>MvtDecodeJob</c> produces for a MoveTo with count &gt; 1</description></item>
    /// <item><term>LineString / MultiLineString</term><description><c>LineString</c>; N paths</description></item>
    /// <item><term>Polygon / MultiPolygon</term><description><c>Polygon</c>; rings concatenated, with
    /// <see cref="PolygonRingCounts"/> carrying the per-polygon grouping</description></item>
    /// </list>
    /// <para>The three paragraphs below are non-local invariants. Coordinates are geodetic and latitude-first
    /// (<see cref="GeoCoordinate"/>): RFC 7946 §3.1.1 positions are longitude-first on the wire, and
    /// <see cref="GeoJsonParser"/> swaps them once.</para>
    /// <para>Ring winding encodes ROLE, not authorship. RFC 7946 §3.1.6 gives role by position and tells
    /// parsers not to reject non-conforming winding, so the parser re-orients each polygon's rings: the
    /// exterior and its holes get opposite shoelace signs, with the exterior CW-on-screen in tile space.
    /// <c>RingAssemblyJob</c> classifies by sign.</para>
    /// <para>It IS the evaluation surface (<see cref="IFeature"/>), as <c>MvtFeature</c> is for MVT, so no
    /// per-feature adapter is allocated. Slicing carries the feature BY REFERENCE into every tile it touches,
    /// so filters and expressions read the authored properties, never a copy that could drift.</para>
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
