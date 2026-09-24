// Engine-free: no UnityEngine dependency. Uses Unity.Mathematics types only.
// Uses Unity.Mathematics.math for trigonometry (Burst intrinsifies math.log/tan/sin/cos/sqrt).
using System;
using Unity.Mathematics;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Earth-Centered, Earth-Fixed (ECEF) coordinate system — WGS-84 ellipsoid. Backs the Globe projection mode.
    /// <para>Render space swaps the ECEF axes: render = <c>double3(X_ecef, Z_ecef, Y_ecef)</c>
    /// (<c>docs/coordinates-and-projections.md</c>).</para>
    /// <para>Planar-only constants (<c>WorldExtent</c>, <c>MaxLatitude</c>) live only on
    /// <see cref="WebMercator"/>, never here.</para>
    /// </summary>
    public static class Ecef
    {
        // ── WGS-84 constants — reference EarthConstants (single source of truth) ─────────────────

        /// <summary>WGS-84 semi-major axis (equatorial radius), metres. References <see cref="EarthConstants.A"/>.</summary>
        public const double A = EarthConstants.A;

        /// <summary>WGS-84 flattening factor. References <see cref="EarthConstants.F"/>.</summary>
        public const double F = EarthConstants.F;

        /// <summary>First eccentricity squared: E2 = F*(2−F). References <see cref="EarthConstants.E2"/>.</summary>
        public const double E2 = EarthConstants.E2;

        // ── Forward projection ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Projects a geodetic point (WGS-84 lon, lat degrees + altitude metres) to
        /// render-space ECEF coordinates.
        ///
        /// <para>ECEF forward formula:
        /// <c>N = A / sqrt(1 − E2·sin²φ)</c><br/>
        /// <c>X = (N+h)·cosφ·cosλ ; Y = (N+h)·cosφ·sinλ ; Z = (N(1−E2)+h)·sinφ</c></para>
        /// </summary>
        public static double3 Forward(GeoCoordinate3D geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0;  // longitude radians
            double phi    = geo.Latitude * math.PI_DBL / 180.0;  // latitude radians
            double h      = geo.Altitude;

            double sinPhi = math.sin(phi);
            double cosPhi = math.cos(phi);
            double sinLam = math.sin(lambda);
            double cosLam = math.cos(lambda);

            double N = A / math.sqrt(1.0 - E2 * sinPhi * sinPhi);

            double X_ecef = (N + h) * cosPhi * cosLam;
            double Y_ecef = (N + h) * cosPhi * sinLam;
            double Z_ecef = (N * (1.0 - E2) + h) * sinPhi;

            // Axis-swap to render space: (X_ecef, Z_ecef, Y_ecef)
            return new double3(X_ecef, Z_ecef, Y_ecef);
        }

        /// <summary>
        /// Batch projects an array of geodetic points to render-space ECEF coordinates.
        /// Element-wise equal to scalar <see cref="Forward(GeoCoordinate3D)"/>, bit for bit
        /// (pinned by <c>ProjectionMathTests.Ecef_BatchEqualsScalar_Exact</c>).
        /// Allocates zero bytes (caller owns <paramref name="dst"/> span).
        /// </summary>
        public static void Forward(ReadOnlySpan<GeoCoordinate3D> src, Span<double3> dst)
        {
            for (int i = 0; i < src.Length; i++)
                dst[i] = Forward(src[i]);
        }

        // ── Tangent basis (ENU frame) ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the ENU (East-North-Up) tangent basis at the given surface position, axis-swapped to render
        /// space. Columns: c0=East, c1=Up, c2=North.
        /// <para>ECEF ENU unit vectors before the axis swap:<br/>
        /// <c>up = (cosφ·cosλ, cosφ·sinλ, sinφ)</c>, <c>east = (−sinλ, cosλ, 0)</c>,
        /// <c>north = cross(up, east)</c></para>
        /// </summary>
        public static float3x3 TangentBasis(GeoCoordinate geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0;
            double phi    = geo.Latitude * math.PI_DBL / 180.0;

            double sinPhi = math.sin(phi);
            double cosPhi = math.cos(phi);
            double sinLam = math.sin(lambda);
            double cosLam = math.cos(lambda);

            // ENU unit vectors in ECEF.
            double3 up   = new double3(cosPhi * cosLam, cosPhi * sinLam, sinPhi);
            double3 east = new double3(-sinLam, cosLam, 0.0);
            double3 north = math.cross(up, east);

            // Axis-swap to render space: ecef(X,Y,Z) → render(X, Z, Y). c0=East, c1=Up, c2=North.
            return new float3x3(
                new float3((float)east.x,  (float)east.z,  (float)east.y),
                new float3((float)up.x,    (float)up.z,    (float)up.y),
                new float3((float)north.x, (float)north.z, (float)north.y));
        }
    }
}
