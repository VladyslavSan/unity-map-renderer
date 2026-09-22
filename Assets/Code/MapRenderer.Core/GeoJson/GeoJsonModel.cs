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
    ///
    /// <list type="table">
    /// <item><term>Point / MultiPoint</term><description><c>Point</c>; N paths of ONE coordinate each (the
    /// shape production's MVT decoder, <c>MvtDecodeJob</c>, produces for a MoveTo with
    /// count &gt; 1)</description></item>
    /// <item><term>LineString / MultiLineString</term><description><c>LineString</c>; N paths</description></item>
    /// <item><term>Polygon / MultiPolygon</term><description><c>Polygon</c>; rings concatenated, with
    /// <see cref="PolygonRingCounts"/> carrying the per-polygon grouping</description></item>
    /// </list>
    ///
    /// <para><b>Coordinates are geodetic and latitude-first</b> (<see cref="GeoCoordinate"/>). RFC 7946
    /// §3.1.1 positions are longitude-first on the wire; the swap happens once, in
    /// <see cref="GeoJsonParser"/>, and lon-first ordering never propagates past it.</para>
    ///
    /// <para><b>Ring winding encodes ROLE, not authorship.</b> The parser re-orients each polygon's rings so
    /// the exterior and its holes have opposite shoelace signs, with the exterior landing CW-on-screen
    /// (positive shoelace) once projected into tile space — see <see cref="GeoJsonParser"/>. RFC 7946
    /// §3.1.6 gives role POSITIONALLY and explicitly tells parsers not to reject non-conforming winding, so
    /// authored winding cannot be trusted; downstream (<c>RingAssemblyJob</c>) classifies by sign.</para>
    ///
    /// <para><b>It IS the evaluation surface</b> (<see cref="IFeature"/>), exactly as <c>MvtFeature</c> is
    /// for the other format. <c>Id</c>/<c>Properties</c>/<c>GeometryType</c> already had the
    /// interface's shape, so implementing it costs one forwarding method and saves the per-tile adapter a
    /// decoded GeoJSON layer would otherwise allocate one of per feature. Slicing carries the parsed feature
    /// BY REFERENCE into every tile it touches, so the filter and expression layers read the authored
    /// properties themselves — never a copy that could drift.</para>
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
