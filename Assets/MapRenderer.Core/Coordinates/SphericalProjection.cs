// Engine-free: no UnityEngine dependency. The stateless ProjectPoint uses only Unity.Mathematics, so Burst
// compiles it transitively when ProjectPointsJob calls SphericalProjection.ProjectPoint(...).

using System;
using Unity.Mathematics;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Simple-sphere globe projection: geodetic → ECEF on a sphere of radius <see cref="Radius"/>
    /// (eccentricity ignored). The strictly-better WGS-84 <c>EllipsoidalProjection</c> is a future drop-in (a
    /// new <see cref="IProjection"/> impl, zero pipeline edits). ECEF axes are swapped to render space
    /// <c>(X, Z, Y)</c> (docs §7); the surface up is the radial normal (exactly unit by construction).
    ///
    /// <para>Implements the SAME <see cref="IProjection"/> as <see cref="WebMercatorProjection"/> — the second
    /// implementation that proves the projection abstraction genuinely serves both planar and globe. The
    /// geometry surface (<see cref="ProjectPoint"/> / <see cref="Project"/> / <see cref="UpAt"/>) is real and
    /// Burst-callable; the camera-interaction methods (screen↔ground) need a 3D globe-camera pose that is a
    /// separate future stage and throw until then.</para>
    /// </summary>
    public readonly struct SphericalProjection : IProjection
    {
        /// <summary>The globe's sphere radius, metres. Fixed to the WGS-84 semi-major axis so the globe matches
        /// the Web Mercator world scale (<see cref="WebMercator.R"/>). Projections are stateless — the radius is
        /// a constant, not per-instance config.</summary>
        public const double Radius = EarthConstants.A;

        // ── Projection math — the single shared kernel (stateless struct → usable in Burst & managed) ──

        /// <summary>
        /// The spherical projection kernel: geodetic → render-space ECEF position on the sphere of
        /// <see cref="Radius"/> + the radial surface up. STATELESS — the only input is the geodetic point;
        /// uses only <c>Unity.Mathematics</c>, so Burst compiles it when
        /// <c>ProjectPointsJob&lt;SphericalProjection&gt;</c> calls it through the generic constraint; the OOP
        /// <see cref="Project"/>/<see cref="UpAt"/> call it too.
        /// </summary>
        public ProjectedPoint ProjectPoint(in GeoCoordinate geo)
        {
            double lambda = geo.Longitude * math.PI_DBL / 180.0; // longitude, radians
            double phi    = geo.Latitude  * math.PI_DBL / 180.0; // latitude, radians

            double sinPhi = math.sin(phi), cosPhi = math.cos(phi);
            double sinLam = math.sin(lambda), cosLam = math.cos(lambda);

            // Radial (geodetic) normal in ECEF — unit by construction (no normalize needed).
            double upX = cosPhi * cosLam, upY = cosPhi * sinLam, upZ = sinPhi;
            double rr  = Radius; // surface point (no elevation); elevated GeoCoordinate3D is a future path

            // Axis-swap ECEF (X,Y,Z) → render (X,Z,Y) (docs §7), matching Ecef.Forward. Built via the double3
            // constructor (no double3 arithmetic ops), matching Ecef.Forward and the core-tests shim.
            return new ProjectedPoint
            {
                World = new double3(upX * rr, upZ * rr, upY * rr),
                Up    = new double3(upX,      upZ,      upY),
            };
        }

        // ── Geometry (build side; docs §6) — conveniences forwarding to the kernel ─────────────────

        /// <inheritdoc/>
        public double3 Project(in GeoCoordinate geo) => ProjectPoint(geo).World;

        /// <inheritdoc/>
        public double3 UpAt(in GeoCoordinate geo) => ProjectPoint(geo).Up;

        /// <inheritdoc/>
        public float3x3 TangentBasisAt(in GeoCoordinate geo) => Ecef.TangentBasis(geo); // render-space ENU at the point

        /// <inheritdoc/>
        public double MetersPerUnit => 1.0; // render units are ECEF metres

        /// <inheritdoc/>
        public bool ReversesWinding => true; // ECEF→render (X,Z,Y) axis-swap is a reflection → flips winding

        // ── Camera interaction (managed side) — globe camera is a future stage ────────────────────

        private const string CameraNotReady =
            "Globe (spherical) camera interaction requires a 3D globe-camera pose — a future stage. " +
            "Only geometry projection (ProjectPoint/Project/UpAt) is implemented.";

        /// <inheritdoc/>
        public GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in CameraProperties camera)
            => throw new NotSupportedException(CameraNotReady);

        /// <inheritdoc/>
        public double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in CameraProperties camera)
            => throw new NotSupportedException(CameraNotReady);

        /// <inheritdoc/>
        public double ClampValidLatitude(double latitudeDegrees) => math.clamp(latitudeDegrees, -90.0, 90.0);
    }
}
