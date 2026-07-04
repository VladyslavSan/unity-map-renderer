// Engine-free: no UnityEngine dependency. Implements IProjection for the planar Web Mercator case.
// All Mercator formulas are delegated to WebMercator statics — no formula duplication.

using Unity.Mathematics;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.Geo
{
    /// <summary>
    /// Web Mercator implementation of <see cref="IProjection"/>.
    /// Composes <see cref="WebMercator"/> statics; the only home of the pixel↔ground math for the
    /// planar case. The Mercator forward/inverse formulas live on <see cref="WebMercator"/>; this
    /// class applies the viewport + camera-pose arithmetic on top.
    ///
    /// <para><b>Screen convention (D2/T0):</b> +x right, +y up, origin bottom-left — matches Unity's
    /// <c>Mouse.current.position</c>. Bearing (heading) rotates the pixel offset into (east, north)
    /// Mercator axes: <c>east = sx·cosH + sy·sinH</c>, <c>north = −sx·sinH + sy·cosH</c>.</para>
    ///
    /// <para><b>Overhead-correct (D5):</b> <see cref="ScreenToGround"/> is exact for <c>tilt=0</c>;
    /// the tilted ray-cast is a future internal upgrade of the same method, not a new interface member.</para>
    /// </summary>
    public readonly struct WebMercatorProjection : IProjection
    {
        // ── Projection math — the single shared kernel (stateless struct → usable in Burst & managed) ──

        /// <summary>
        /// The Web Mercator projection kernel: geodetic → render-space position + surface up. STATELESS —
        /// the only input is the geodetic point (the EPSG:3857 radius is the fixed <see cref="WebMercator.R"/>).
        /// Uses only <c>Unity.Mathematics</c> via <see cref="WebMercator.Forward"/> (the single-sourced
        /// formula), so Burst compiles it when <c>ProjectPointsJob&lt;WebMercatorProjection&gt;</c> calls it
        /// through the generic constraint; the OOP <see cref="Project"/>/<see cref="UpAt"/> call it too.
        /// Mercator up is the constant planar +Y.
        /// </summary>
        public ProjectedPoint ProjectPoint(in GeoCoordinate geo)
            => new ProjectedPoint
            {
                World = WebMercator.Forward(new GeoCoordinate3D { Latitude = geo.Latitude, Longitude = geo.Longitude, Altitude = 0.0 }),
                Up    = new double3(0.0, 1.0, 0.0),
            };

        // ── Geometry (build side; docs §6) — conveniences forwarding to the kernel ─────────────────

        /// <inheritdoc/>
        public double3 Project(in GeoCoordinate geo) => ProjectPoint(geo).World;

        /// <inheritdoc/>
        public double3 UpAt(in GeoCoordinate geo) => new double3(0.0, 1.0, 0.0);

        /// <inheritdoc/>
        public float3x3 TangentBasisAt(in GeoCoordinate geo) => WebMercator.TangentBasis(geo); // constant east/up/north

        /// <inheritdoc/>
        public double MetersPerUnit => 1.0; // render units are Mercator metres

        /// <inheritdoc/>
        public bool ReversesWinding => false; // planar mapping preserves the tessellation winding

        /// <inheritdoc/>
        public bool TryGetHorizonOccluder(out double3 renderCentre, out double radius)
        {
            // A finite atlas sheet: it neither self-occludes nor repeats — one flat rectangle, no wrap.
            renderCentre = default; radius = 0.0; return false;
        }

        // ── Camera interaction (managed side) ───────────────────────────────────────────────────────

        /// <inheritdoc/>
        public GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in CameraProperties camera)
        {
            // Clamp the look-at latitude before projecting to Mercator to avoid pole singularities.
            GeoCoordinate3D clampedLookAt = new GeoCoordinate3D
            {
                Longitude = camera.LookAt.Longitude,
                Latitude  = ClampValidLatitude(camera.LookAt.Latitude),
                Altitude  = 0.0
            };
            double2 centreMerc = WebMercator.FromLonLat(clampedLookAt);
            double  mpp        = WebMercator.GroundResolution(camera.Zoom);

            // Pixel offset from screen centre (screen space: +x right, +y up).
            double2 off = screenPx - viewportPx * 0.5;
            double  sx  = off.x;
            double  sy  = off.y;

            // Rotate into Mercator (east, north) axes using the camera bearing.
            // Locked rotation (D2.4 / T0): east = sx·cosH + sy·sinH; north = −sx·sinH + sy·cosH.
            double cH    = camera.Heading.Value.Cos;
            double sH    = camera.Heading.Value.Sin;
            double east  =  sx * cH + sy * sH;
            double north = -sx * sH + sy * cH;

            double2 groundMerc = centreMerc + new double2(east, north) * mpp;
            double2 ll         = WebMercator.ToLonLat(groundMerc.x, groundMerc.y);
            return new GeoCoordinate3D { Longitude = ll.x, Latitude = ll.y, Altitude = 0.0 };
        }

        /// <inheritdoc/>
        public double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in CameraProperties camera)
        {
            // Clamp the look-at latitude, same as ScreenToGround (pole-correctness on both paths).
            GeoCoordinate3D clampedLookAt = new GeoCoordinate3D
            {
                Longitude = camera.LookAt.Longitude,
                Latitude  = ClampValidLatitude(camera.LookAt.Latitude),
                Altitude  = 0.0
            };
            double2 centreMerc = WebMercator.FromLonLat(clampedLookAt);
            double2 groundMerc = WebMercator.FromLonLat(new GeoCoordinate3D
            {
                Longitude = ground.Longitude,
                Latitude  = ground.Latitude,
                Altitude  = 0.0
            });
            double mpp = WebMercator.GroundResolution(camera.Zoom);

            // Mercator offset divided by mpp gives the (east, north) pixel offset.
            // Use * (1/mpp) because the shim's double2 does not expose operator/(double2, double).
            double2 d     = (groundMerc - centreMerc) * (1.0 / mpp);
            double  e     = d.x;
            double  n     = d.y;

            // Inverse rotation Rot(+H): sx = e·cosH − n·sinH; sy = e·sinH + n·cosH.
            double cH = camera.Heading.Value.Cos;
            double sH = camera.Heading.Value.Sin;
            double sx = e * cH - n * sH;
            double sy = e * sH + n * cH;

            return new double2(sx, sy) + viewportPx * 0.5;
        }

        /// <inheritdoc/>
        public double ClampValidLatitude(double latitudeDegrees)
            => math.clamp(latitudeDegrees, -WebMercator.MaxLatitude, WebMercator.MaxLatitude);
    }
}
