using System.Collections.Generic;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Core.GeoJson
{
    /// <summary>
    /// RFC 7946 → <see cref="GeoJsonDataset"/>. Accepts a <c>FeatureCollection</c>, a bare <c>Feature</c>, or
    /// a bare geometry object as the root (RFC §3). Syntax errors surface as the underlying
    /// <see cref="JsonParseException"/>, unchanged; everything that is well-formed JSON but not well-formed
    /// GeoJSON raises <see cref="GeoJsonFormatException"/>.
    ///
    /// <para><b>The longitude-first swap happens HERE and nowhere else.</b> RFC 7946 §3.1.1 positions are
    /// <c>[longitude, latitude, (altitude)]</c>; they become latitude-first <see cref="GeoCoordinate"/>
    /// immediately, per the repo convention that the swap lives at the projection boundary. The third
    /// element is read past and discarded — the surface-only model <c>TileToGeoJob</c> also assumes.</para>
    ///
    /// <para><b>Ring winding is re-encoded from ROLE.</b> RFC §3.1.6 states role POSITIONALLY (first ring
    /// exterior, the rest holes) and prescribes the right-hand rule, but also directs parsers NOT to reject
    /// non-conforming winding — so authored winding carries no information. Downstream
    /// (<c>RingAssemblyJob</c>) classifies outer-vs-hole by SIGN, so role is re-encoded as winding here,
    /// while the per-polygon grouping is still in hand.</para>
    ///
    /// <para><b>Which sign, and why it is the mirror of the RFC's.</b> The target is the MVT convention in
    /// TILE space: exterior CW-on-screen = positive shoelace, holes negative — that is what makes a GeoJSON
    /// tile indistinguishable from an MVT tile downstream. <c>(lon, lat) → (u, v)</c> is orientation
    /// REVERSING (<c>u</c> grows with longitude, <c>v</c> SHRINKS with latitude), so a ring's shoelace sign
    /// flips on projection. To land positive in tile space the exterior must therefore be normalised
    /// NEGATIVE here in <c>(lon, lat)</c> — clockwise in lon/lat, i.e. the mirror image of the RFC's
    /// right-hand rule, which is exactly the rule the RFC says not to trust anyway.</para>
    ///
    /// <para><b>Rejected, loudly</b> (this source serves declarative fixtures, where silently rendering less
    /// is the worst outcome): a segment whose <c>|Δlon| &gt; 180°</c> (RFC §3.1.9 directs authors to split
    /// antimeridian-crossing geometry — interpreting such a segment literally draws it the long way around
    /// the world), <c>GeometryCollection</c>, a LinearRing with fewer than 4 positions or an unclosed one,
    /// coordinates outside <c>[−90, 90]</c> / <c>[−180, 180]</c>, and an <c>id</c> that is neither string nor
    /// number. Ignored per spec: <c>bbox</c>, foreign members, CRS (RFC §4 fixes it to WGS 84).</para>
    /// </summary>
    public static class GeoJsonParser
    {
        public static GeoJsonDataset Parse(string json) => Parse(JsonParser.Parse(json));

        public static GeoJsonDataset Parse(JsonValue root)
        {
            if (root == null || !root.IsObject)
                throw new GeoJsonFormatException("GeoJSON root must be a JSON object.");

            string type = root.GetString("type");
            if (type == null)
                throw new GeoJsonFormatException("GeoJSON root object has no \"type\" member.");

            var features = new List<GeoJsonFeature>();

            if (type == "FeatureCollection")
            {
                JsonValue items = root.Get("features");
                if (items == null || !items.IsArray)
                    throw new GeoJsonFormatException("FeatureCollection has no \"features\" array.");

                for (int i = 0; i < items.Items.Count; i++)
                    features.Add(ReadFeature(items.Items[i], i));
            }
            else if (type == "Feature")
            {
                features.Add(ReadFeature(root, 0));
            }
            else
            {
                // A bare geometry object as the root (RFC §3): no id, no properties.
                ReadGeometry(root, 0, out TileGeometryType geometryType,
                             out List<IReadOnlyList<GeoCoordinate>> paths, out List<int> ringCounts);

                features.Add(new GeoJsonFeature
                {
                    Id                = Value.Null,
                    Properties        = EmptyProperties,
                    GeometryType      = geometryType,
                    Paths             = paths,
                    PolygonRingCounts = ringCounts
                });
            }

            return new GeoJsonDataset { Features = features };
        }

        private static readonly Dictionary<string, Value> EmptyProperties = new Dictionary<string, Value>();

        // ── Feature ────────────────────────────────────────────────────────────────────────────────

        private static GeoJsonFeature ReadFeature(JsonValue feature, int featureIndex)
        {
            if (feature == null || !feature.IsObject)
                throw new GeoJsonFormatException($"GeoJSON feature {featureIndex} is not a JSON object.");

            string type = feature.GetString("type");
            if (type != "Feature")
                throw new GeoJsonFormatException(
                    $"GeoJSON feature {featureIndex} has type \"{type}\"; expected \"Feature\".");

            TileGeometryType geometryType = TileGeometryType.Unknown;
            var paths      = new List<IReadOnlyList<GeoCoordinate>>();
            var ringCounts = new List<int>();

            JsonValue geometry = feature.Get("geometry");

            // RFC §3.2 permits `"geometry": null` — an UNLOCATED feature. Valid input, so it must not throw;
            // it simply contributes no geometry to any tile.
            if (geometry != null && !geometry.IsNull)
                ReadGeometry(geometry, featureIndex, out geometryType, out paths, out ringCounts);

            return new GeoJsonFeature
            {
                Id                = ReadId(feature, featureIndex),
                Properties        = ReadProperties(feature, featureIndex),
                GeometryType      = geometryType,
                Paths             = paths,
                PolygonRingCounts = ringCounts
            };
        }

        private static Value ReadId(JsonValue feature, int featureIndex)
        {
            if (!feature.TryGet("id", out JsonValue raw))
                return Value.Null;

            switch (raw.Kind)
            {
                case JsonKind.String: return Value.String(raw.AsString());
                case JsonKind.Number: return Value.Number(raw.AsDouble());
                default:
                    throw new GeoJsonFormatException(
                        $"GeoJSON feature {featureIndex} has an \"id\" of kind {raw.Kind}; " +
                        "RFC 7946 §3.2 permits only a string or a number.");
            }
        }

        private static IReadOnlyDictionary<string, Value> ReadProperties(JsonValue feature, int featureIndex)
        {
            // `properties: null` is explicitly permitted (RFC §3.2) and yields an EMPTY map — never null,
            // because the `properties` expression consumes IFeature.Properties unconditionally.
            if (!feature.TryGet("properties", out JsonValue raw) || raw.IsNull)
                return EmptyProperties;

            if (!raw.IsObject)
                throw new GeoJsonFormatException(
                    $"GeoJSON feature {featureIndex} has \"properties\" of kind {raw.Kind}; " +
                    "expected an object or null.");

            var map = new Dictionary<string, Value>(raw.Members.Count);
            foreach (KeyValuePair<string, JsonValue> member in raw.Members)
                map[member.Key] = ToValue(member.Value);
            return map;
        }

        /// <summary>
        /// <see cref="JsonValue"/> → <see cref="Value"/>. Total and lossless: <c>JsonKind</c>'s six kinds map
        /// one-to-one onto <see cref="Value"/>'s Null/Bool/Number/String/Array/Object factories, so nested
        /// containers recurse structurally and there is no drop-vs-stringify decision to make.
        /// (<c>Value.OfColor</c> has no JSON counterpart — the map is one-directional, as it is for MVT.)
        /// </summary>
        private static Value ToValue(JsonValue json)
        {
            switch (json.Kind)
            {
                case JsonKind.Bool:   return Value.Bool(json.AsBool());
                case JsonKind.Number: return Value.Number(json.AsDouble());
                case JsonKind.String: return Value.String(json.AsString());

                case JsonKind.Array:
                {
                    var items = new List<Value>(json.Items.Count);
                    for (int i = 0; i < json.Items.Count; i++)
                        items.Add(ToValue(json.Items[i]));
                    return Value.Array(items);
                }

                case JsonKind.Object:
                {
                    var members = new Dictionary<string, Value>(json.Members.Count);
                    foreach (KeyValuePair<string, JsonValue> member in json.Members)
                        members[member.Key] = ToValue(member.Value);
                    return Value.Object(members);
                }

                default: return Value.Null;
            }
        }

        // ── Geometry ───────────────────────────────────────────────────────────────────────────────

        private static void ReadGeometry(
            JsonValue geometry, int featureIndex,
            out TileGeometryType geometryType,
            out List<IReadOnlyList<GeoCoordinate>> paths,
            out List<int> ringCounts)
        {
            if (!geometry.IsObject)
                throw new GeoJsonFormatException(
                    $"GeoJSON feature {featureIndex}: geometry is not a JSON object.");

            string kind = geometry.GetString("type");
            JsonValue coordinates = geometry.Get("coordinates");

            paths      = new List<IReadOnlyList<GeoCoordinate>>();
            ringCounts = new List<int>();

            switch (kind)
            {
                case "Point":
                    geometryType = TileGeometryType.Point;
                    paths.Add(new List<GeoCoordinate> { ReadPosition(coordinates, featureIndex) });
                    return;

                case "MultiPoint":
                    geometryType = TileGeometryType.Point;
                    foreach (JsonValue position in RequireArray(coordinates, featureIndex, "MultiPoint coordinates"))
                        paths.Add(new List<GeoCoordinate> { ReadPosition(position, featureIndex) });
                    return;

                case "LineString":
                    geometryType = TileGeometryType.LineString;
                    paths.Add(ReadLineString(coordinates, featureIndex));
                    return;

                case "MultiLineString":
                    geometryType = TileGeometryType.LineString;
                    foreach (JsonValue line in RequireArray(coordinates, featureIndex, "MultiLineString coordinates"))
                        paths.Add(ReadLineString(line, featureIndex));
                    return;

                case "Polygon":
                    geometryType = TileGeometryType.Polygon;
                    ReadPolygon(coordinates, featureIndex, paths, ringCounts);
                    return;

                case "MultiPolygon":
                    geometryType = TileGeometryType.Polygon;
                    foreach (JsonValue polygon in RequireArray(coordinates, featureIndex, "MultiPolygon coordinates"))
                        ReadPolygon(polygon, featureIndex, paths, ringCounts);
                    return;

                case "GeometryCollection":
                    // Fenced, with a citable reason: TileGeometryType is per-FEATURE and IFeature.GeometryType
                    // is singular, so a mixed-kind feature would make a per-ring type tag load-bearing again.
                    throw new GeoJsonFormatException(
                        $"GeoJSON feature {featureIndex}: GeometryCollection is not supported by this source " +
                        "(geometry type is a per-feature property; split it into separate features).");

                default:
                    throw new GeoJsonFormatException(
                        $"GeoJSON feature {featureIndex}: unknown geometry type \"{kind}\".");
            }
        }

        private static IReadOnlyList<JsonValue> RequireArray(JsonValue value, int featureIndex, string what)
        {
            if (value == null || !value.IsArray)
                throw new GeoJsonFormatException(
                    $"GeoJSON feature {featureIndex}: expected a JSON array for {what}.");
            return value.Items;
        }

        private static List<GeoCoordinate> ReadLineString(JsonValue value, int featureIndex)
        {
            IReadOnlyList<JsonValue> positions = RequireArray(value, featureIndex, "LineString coordinates");
            if (positions.Count < 2)
                throw new GeoJsonFormatException(
                    $"GeoJSON feature {featureIndex}: a LineString needs two or more positions " +
                    $"(RFC 7946 §3.1.4), found {positions.Count}.");

            var path = new List<GeoCoordinate>(positions.Count);
            for (int i = 0; i < positions.Count; i++)
                path.Add(ReadPosition(positions[i], featureIndex));

            RejectAntimeridianCrossing(path, featureIndex);
            return path;
        }

        private static void ReadPolygon(
            JsonValue value, int featureIndex,
            List<IReadOnlyList<GeoCoordinate>> paths, List<int> ringCounts)
        {
            IReadOnlyList<JsonValue> rings = RequireArray(value, featureIndex, "Polygon coordinates");

            // RFC §3.1: an empty coordinates array is an empty geometry. No ringCounts entry is added, so a
            // MultiPolygon containing an empty polygon ends up with FEWER PolygonRingCounts entries than the
            // RFC array had polygons. That is safe: the counts exist only to walk Paths in
            // polygon-sized strides (GeoJsonTileSlicer.ClipPolygons' `ring += ringCount`), and a zero-ring
            // polygon contributes nothing to Paths either way. The two lists index Paths, not the wire array.
            if (rings.Count == 0) return;

            var polygon = new List<List<GeoCoordinate>>(rings.Count);
            for (int i = 0; i < rings.Count; i++)
                polygon.Add(ReadLinearRing(rings[i], featureIndex));

            NormaliseRingWinding(polygon);

            for (int i = 0; i < polygon.Count; i++)
                paths.Add(polygon[i]);
            ringCounts.Add(polygon.Count);
        }

        /// <summary>
        /// Reads an RFC 7946 §3.1.6 LinearRing — four or more positions, first identical to last — and
        /// returns it IMPLICITLY CLOSED (the closing duplicate stripped), which is the convention
        /// production's MVT decoder (<c>MvtDecodeJob</c>) produces and every downstream
        /// stage assumes.
        /// </summary>
        private static List<GeoCoordinate> ReadLinearRing(JsonValue value, int featureIndex)
        {
            IReadOnlyList<JsonValue> positions = RequireArray(value, featureIndex, "a LinearRing");
            if (positions.Count < 4)
                throw new GeoJsonFormatException(
                    $"GeoJSON feature {featureIndex}: a LinearRing needs four or more positions " +
                    $"(RFC 7946 §3.1.6), found {positions.Count}.");

            var closed = new List<GeoCoordinate>(positions.Count);
            for (int i = 0; i < positions.Count; i++)
                closed.Add(ReadPosition(positions[i], featureIndex));

            GeoCoordinate first = closed[0];
            GeoCoordinate last  = closed[closed.Count - 1];
            if (first.Latitude != last.Latitude || first.Longitude != last.Longitude)
                throw new GeoJsonFormatException(
                    $"GeoJSON feature {featureIndex}: a LinearRing's first and last positions must be " +
                    "identical (RFC 7946 §3.1.6).");

            RejectAntimeridianCrossing(closed, featureIndex);

            closed.RemoveAt(closed.Count - 1);
            return closed;
        }

        /// <summary>
        /// Re-encodes positional ring role as winding: the exterior (ring 0, per RFC §3.1.6) is oriented to a
        /// NEGATIVE lon/lat shoelace and every hole to a positive one, so that after the
        /// orientation-reversing projection into tile space the exterior reads positive (CW on screen) and
        /// holes negative — the MVT convention <c>RingAssemblyJob</c> classifies against.
        ///
        /// <para>Reversal keeps vertex 0 in place and reverses the rest, which is what reversing the CLOSED
        /// RFC ring and re-stripping its duplicate produces. Reversing the whole stripped list would rotate
        /// the ring by one as well, so two inputs describing the same ring with opposite winding would not
        /// normalise to the same vertex sequence.</para>
        /// </summary>
        private static void NormaliseRingWinding(List<List<GeoCoordinate>> polygon)
        {
            for (int i = 0; i < polygon.Count; i++)
            {
                double shoelace   = LonLatShoelace(polygon[i]);
                bool wantNegative = i == 0;

                if (shoelace != 0.0 && (shoelace < 0.0) != wantNegative)
                    polygon[i].Reverse(1, polygon[i].Count - 1);
            }
        }

        /// <summary>Twice the signed area of a ring in the <c>(longitude, latitude)</c> plane, latitude up:
        /// positive = counterclockwise there.</summary>
        private static double LonLatShoelace(List<GeoCoordinate> ring)
        {
            double area = 0.0;
            for (int i = 0; i < ring.Count; i++)
            {
                GeoCoordinate a = ring[i];
                GeoCoordinate b = ring[(i + 1) % ring.Count];
                area += a.Longitude * b.Latitude - b.Longitude * a.Latitude;
            }
            return area;
        }

        // ── Positions ──────────────────────────────────────────────────────────────────────────────

        private static GeoCoordinate ReadPosition(JsonValue value, int featureIndex)
        {
            if (value == null || !value.IsArray || value.Items.Count < 2)
                throw new GeoJsonFormatException(
                    $"GeoJSON feature {featureIndex}: a position must be an array of at least two numbers " +
                    "(RFC 7946 §3.1.1).");

            JsonValue longitude = value.Items[0];
            JsonValue latitude  = value.Items[1];
            if (longitude.Kind != JsonKind.Number || latitude.Kind != JsonKind.Number)
                throw new GeoJsonFormatException(
                    $"GeoJSON feature {featureIndex}: a position's first two elements must be numbers " +
                    "(RFC 7946 §3.1.1).");

            // The lon-first → lat-first swap. It happens here and nowhere downstream.
            double lon = longitude.AsDouble();
            double lat = latitude.AsDouble();

            if (lat < -90.0 || lat > 90.0)
                throw new GeoJsonFormatException(
                    $"GeoJSON feature {featureIndex}: latitude {lat} is outside [-90, 90] (RFC 7946 §3.1.1).");
            if (lon < -180.0 || lon > 180.0)
                throw new GeoJsonFormatException(
                    $"GeoJSON feature {featureIndex}: longitude {lon} is outside [-180, 180] " +
                    "(RFC 7946 §3.1.1).");

            // Any third element (altitude) is read past and discarded: the surface-only GeoCoordinate model.
            return new GeoCoordinate { Latitude = lat, Longitude = lon };
        }

        private static void RejectAntimeridianCrossing(List<GeoCoordinate> path, int featureIndex)
        {
            for (int i = 1; i < path.Count; i++)
            {
                double deltaLon = path[i].Longitude - path[i - 1].Longitude;
                if (deltaLon > 180.0 || deltaLon < -180.0)
                    throw new GeoJsonFormatException(
                        $"GeoJSON feature {featureIndex}: segment {i - 1}→{i} crosses the antimeridian " +
                        $"(|Δlon| = {(deltaLon < 0.0 ? -deltaLon : deltaLon)} > 180). RFC 7946 §3.1.9 " +
                        "requires such geometry to be split by the author; this source does not split it.");
            }
        }
    }
}
