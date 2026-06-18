using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Coordinates
{
    /// <summary>
    /// Web Mercator (EPSG:3857). SPHERICAL projection (radius R = WGS84 semi-major axis) applied to
    /// WGS84 geodetic lon/lat — eccentricity is intentionally ignored (see
    /// docs/coordinates-and-projections.md §2). Units: meters.
    /// </summary>
    public static class WebMercator
    {
        public const double R = 6378137.0;
        public const double WorldExtent = Math.PI * R; // ~20037508.342789 m (half-width)

        public static double2 FromLonLat(double lonDeg, double latDeg)
        {
            double lon = lonDeg * Math.PI / 180.0;
            double lat = latDeg * Math.PI / 180.0;
            double x = R * lon;
            double y = R * Math.Log(Math.Tan(Math.PI / 4.0 + lat / 2.0));
            return new double2(x, y);
        }

        public static double2 ToLonLat(double x, double y)
        {
            double lon = (x / R) * 180.0 / Math.PI;
            double lat = (2.0 * Math.Atan(Math.Exp(y / R)) - Math.PI / 2.0) * 180.0 / Math.PI;
            return new double2(lon, lat);
        }
    }
}
