using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Web Mercator (EPSG:3857). SPHERICAL projection (radius R = WGS84 semi-major axis) applied to
    /// WGS84 geodetic lon/lat — eccentricity is intentionally ignored. Units: meters.
    ///
    /// <para><b>Single source of the Mercator forward formula.</b> The literal
    /// <c>math.log(math.tan(π/4 + lat/2))</c> appears ONLY in <see cref="Forward(GeoCoordinate3D)"/>.
    /// All other callers delegate to it.</para>
    ///
    /// <para><b>Render-space axis convention:</b> east=+X, altitude=+Y, north=+Z.
    /// <c>Forward</c> returns <c>double3(mercX, altitude, mercY)</c>.</para>
    /// </summary>
    public static class WebMercator
    {
        // ── Constants ────────────────────────────────────────────────────────────────────────────

        /// <summary>Spherical Earth radius used by EPSG:3857. Metres. References EarthConstants.A.</summary>
        public const double R = EarthConstants.A;

        /// <summary>
        /// Half-width of the Web Mercator plane in metres (~20037508.34 m).
        /// Planar-only: lives on WebMercator, never on Ecef (honesty boundary).
        /// </summary>
        public const double WorldExtent = math.PI_DBL * R;

        /// <summary>
        /// Mercator projection latitude limit in degrees.
        /// Planar-only: lives on WebMercator, never on Ecef (honesty boundary).
        /// </summary>
        public const double MaxLatitude = 85.05112878;

        /// <summary>
        /// Tile size in pixels (Web Mercator slippy tiles). Planar/tiling-only — lives on WebMercator,
        /// the single source of truth.
        /// </summary>
        public const double TilePixelSize = 512.0;

        // ── Forward projection (the single source of the Mercator literal) ────────────────────────

        /// <summary>
        /// Projects a geodetic point to render-space coordinates.
        /// Render axes: east=+X, altitude=+Y, north=+Z.
        /// Returns <c>double3(mercX, altitude, mercY)</c>.
        ///
        /// <para>This is the ONLY method in the codebase that may contain the Mercator forward
        /// literal <c>math.log(math.tan(π/4 + lat/2))</c> (T2 structural test).</para>
        ///
        /// <para>Uses <c>Unity.Mathematics.math</c> — Burst intrinsifies log/tan/sin/cos/sqrt,
        /// so this is safe to call from Burst jobs.</para>
        /// </summary>
        public static double3 Forward(GeoCoordinate3D geo)
        {
            double latitude  = geo.Latitude  * math.PI_DBL / 180.0;
            double longitude = geo.Longitude * math.PI_DBL / 180.0;

            double x = R * longitude;
            double y = R * math.log(math.tan(math.PI_DBL / 4.0 + latitude / 2.0));

            return new double3(x, geo.Altitude, y);
        }

        /// <summary>
        /// Batch projects an array of geodetic points to render-space coordinates.
        /// Element-wise equivalent to scalar <see cref="Forward(GeoCoordinate3D)"/> (T4: exact equality).
        /// Allocates zero bytes (caller owns <paramref name="dst"/> span).
        /// </summary>
        public static void Forward(ReadOnlySpan<GeoCoordinate3D> src, Span<double3> dst)
        {
            for (int i = 0; i < src.Length; i++)
                dst[i] = Forward(src[i]);
        }

        /// <summary>
        /// Returns the constant ENU tangent basis for Web Mercator in render space.
        /// <para>East=(1,0,0), Up=(0,1,0), North=(0,0,1) — constant for all surface points
        /// (planar projection, no curvature).</para>
        /// <para>Column convention: c0=East, c1=Up, c2=North.</para>
        /// </summary>
        public static float3x3 TangentBasis(GeoCoordinate geo)
        {
            // Constant: the Mercator plane is flat, so the tangent frame is the same everywhere.
            // c0 = East = (1,0,0), c1 = Up = (0,1,0), c2 = North = (0,0,1)
            return new float3x3(
                new float3(1f, 0f, 0f), // c0 = East
                new float3(0f, 1f, 0f), // c1 = Up
                new float3(0f, 0f, 1f)  // c2 = North
            );
        }

        // ── Ground resolution ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Mercator-plane metres per screen pixel at the given fractional zoom level.
        /// Single source of the <c>circumference / (TilePixelSize · 2^zoom)</c> formula —
        /// used by the camera interaction layer to convert pixel offsets to ground distances.
        /// </summary>
        public static double GroundResolution(double zoom)
            => EarthConstants.EquatorialCircumferenceMetres / (TilePixelSize * math.pow(2.0, zoom));

        // ── Legacy 2D helpers (retained for existing planar callers) ─────────────────────────────

        /// <summary>
        /// Projects (lon, lat) in degrees to Web Mercator meters as <c>double2(x, y)</c>.
        /// Delegates to <see cref="Forward(GeoCoordinate3D)"/> so the Mercator literal lives
        /// in exactly one method (T2). Return value is bit-identical to the pre-refactor inline.
        /// </summary>
        public static double2 FromLonLat(GeoCoordinate3D geoCoordinate)
        {
            double3 p = Forward(geoCoordinate);
            return new double2(p.x, p.z);
        }

        /// <summary>Inverse: Web Mercator meters → (lon, lat) in degrees.</summary>
        public static double2 ToLonLat(double x, double y)
        {
            double lon = (x / R) * 180.0 / math.PI_DBL;

            double lat = (2.0 * math.atan(math.exp(y / R)) - math.PI_DBL / 2.0) * 180.0 / math.PI_DBL;

            return new double2(lon, lat);
        }
    }
}