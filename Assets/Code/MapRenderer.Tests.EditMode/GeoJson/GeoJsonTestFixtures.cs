// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, native-container, or MonoBehaviour references.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.GeoJson;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Shared, engine-free plumbing for the GeoJSON S1 teeth: JSON text builders (invariant-culture, so a
    /// comma-decimal locale cannot silently produce different JSON), the parse→project→slice one-liner, and
    /// shoelace helpers over the slicer's <c>IReadOnlyList&lt;double2&gt;</c> output.
    /// </summary>
    internal static class GeoJsonTestFixtures
    {
        // ── JSON builders ──────────────────────────────────────────────────────────────────────────

        /// <summary>A single RFC 7946 position, <c>[longitude, latitude]</c> — wire order, so the tests
        /// exercise the lon-first → lat-first swap rather than assuming it away.</summary>
        public static string Position(double longitude, double latitude)
            => $"[{Num(longitude)},{Num(latitude)}]";

        /// <summary>A JSON array of RFC 7946 positions from flat longitude/latitude pairs.</summary>
        public static string Positions(params double[] lonLatPairs)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < lonLatPairs.Length; i += 2)
            {
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(Num(lonLatPairs[i])).Append(',').Append(Num(lonLatPairs[i + 1])).Append(']');
            }
            return sb.Append(']').ToString();
        }

        /// <summary>An axis-aligned lon/lat rectangle as a closed RFC LinearRing, wound counterclockwise in
        /// lon/lat (the RFC §3.1.6 right-hand rule) when <paramref name="rfcWound"/>, reversed otherwise.
        /// </summary>
        public static string RectangleRing(
            double westLon, double southLat, double eastLon, double northLat, bool rfcWound = true)
        {
            double[] ccw =
            {
                westLon, southLat, eastLon, southLat, eastLon, northLat,
                westLon, northLat, westLon, southLat
            };
            double[] cw =
            {
                westLon, southLat, westLon, northLat, eastLon, northLat,
                eastLon, southLat, westLon, southLat
            };
            return Positions(rfcWound ? ccw : cw);
        }

        public static string Feature(string geometryType, string coordinates, string properties = "null",
                                     string idMember = "")
            => $"{{\"type\":\"Feature\",{idMember}\"properties\":{properties}," +
               $"\"geometry\":{{\"type\":\"{geometryType}\",\"coordinates\":{coordinates}}}}}";

        public static string Collection(params string[] features)
            => $"{{\"type\":\"FeatureCollection\",\"features\":[{string.Join(",", features)}]}}";

        private static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        // ── Slicing ────────────────────────────────────────────────────────────────────────────────

        public static GeoJsonSliceOptions Options(double extent, double bufferAtReferenceExtent)
            => new GeoJsonSliceOptions
            {
                Extent                  = extent,
                BufferAtReferenceExtent = bufferAtReferenceExtent,
                SimplifyTolerance       = 0.0
            };

        /// <summary>Unbuffered options at the default extent — the setting under which two neighbouring
        /// tiles partition a straddling polygon instead of overlapping it.</summary>
        public static GeoJsonSliceOptions Unbuffered
            => Options(GeoJsonSliceOptions.DefaultExtent, 0.0);

        public static TileSlice Slice(string json, TileId tile, GeoJsonSliceOptions options)
            => GeoJsonTileSlicer.Slice(
                GeoJsonProjectedDataset.Project(GeoJsonParser.Parse(json)), tile, options);

        public static TileId Tile(int z, int x, int y) => new TileId { Z = z, X = x, Y = y };

        /// <summary>The tile a geodetic point falls in at <paramref name="z"/> — computed straight off the
        /// unit square, so it goes through neither the forward nor the inverse tile-local formula.</summary>
        public static TileId TileOf(double longitude, double latitude, int z)
        {
            double2 unit = WebMercatorTiling.UnitSquareFromLonLat(
                new GeoCoordinate { Latitude = latitude, Longitude = longitude });
            double n = math.pow(2.0, z);
            return new TileId { Z = z, X = (int)math.floor(unit.x * n), Y = (int)math.floor(unit.y * n) };
        }

        // ── Ring measurements ──────────────────────────────────────────────────────────────────────

        /// <summary>Twice the signed area (shoelace) of a tile-space ring. Positive = CW on screen = the MVT
        /// exterior convention; see <c>Geometry/SignedArea.cs</c> for the sign table.</summary>
        public static double Shoelace(IReadOnlyList<double2> ring)
        {
            double area = 0.0;
            for (int i = 0; i < ring.Count; i++)
            {
                double2 a = ring[i];
                double2 b = ring[(i + 1) % ring.Count];
                area += a.x * b.y - b.x * a.y;
            }
            return area;
        }

        public static double Area(IReadOnlyList<double2> ring) => math.abs(Shoelace(ring)) * 0.5;
    }
}
