using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Core.View.Camera
{
    /// <summary>
    /// S45: Engine-free pose math.
    ///
    /// Absorbed from <c>MapController.AltitudeForZoom</c> and <c>MapController.ApplyCameraTransform</c>
    /// (S42). Now lives in Core so headless tests can exercise the full pose computation.
    ///
    /// <para><b>Altitude formula (D1 — S42 D2):</b>
    ///   Web-Mercator ground resolution: metersPerPixel = EarthConstants.EquatorialCircumferenceMetres / (256 × 2^zoom).
    ///   For a perspective camera looking straight down:
    ///   altitude = (viewportHeightPx × metersPerPixel) / (2 × tan(verticalFovDeg/2)).
    ///   Earth equatorial circumference: see EarthConstants.EquatorialCircumferenceMetres.</para>
    ///
    /// <para><b>Pose formula (D6):</b>
    ///   Orbit on a sphere of radius=altitude around the look-at position.
    ///   Rot(heading, Y) × Rot(tilt, X) × (0, altitude, 0) gives the camera offset from the look-at.
    ///   At tilt=0 the inner rotation is identity → offset=(0,altitude,0) → camera directly above.
    ///   The up-vector is derived from the heading direction in the horizontal plane so LookRotation
    ///   is never fed collinear vectors even at tilt=0 (D6 degeneracy fix).</para>
    ///
    /// <para>Clean-room: standard web-map perspective framing + orbit-camera math.</para>
    /// </summary>
    public static class CameraPoseMath
    {
        // ── Constants — reference EarthConstants and WebMercator (single sources of truth, S62) ─────
        // Note: EarthCircumferenceMetres is kept as a literal in EarthConstants (NOT derived as 2π·A).
        private const double EarthCircumferenceMetres = EarthConstants.EquatorialCircumferenceMetres;
        private const double TilePixelSize            = WebMercator.TilePixelSize;

        // ── Altitude ↔ Zoom ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Web-Mercator ground resolution: metres per pixel at a given fractional zoom level.
        ///
        /// <para>Formula (S42 D2 / S43 D2): metersPerPixel = EarthCircumference / (TilePixelSize × 2^zoom).
        /// This is the single source of truth for the px→m conversion used everywhere (altitude
        /// calculation, line-width scaling, zoom-stability tests).</para>
        /// </summary>
        /// <param name="zoom">Fractional zoom level.</param>
        /// <returns>Ground resolution in metres per pixel.</returns>
        public static double MetersPerPixel(double zoom)
            => EarthCircumferenceMetres / (TilePixelSize * math.pow(2.0, zoom));

        /// <summary>
        /// Computes camera altitude in render-space metres from a fractional zoom level (S42 D2).
        ///
        /// <para>Exact same formula as the retired <c>MapController.AltitudeForZoom</c>; pinned by
        /// the ported <c>CameraTransformTests</c>.</para>
        /// </summary>
        /// <param name="zoom">Fractional zoom level.</param>
        /// <param name="viewportHeightPx">Deterministic reference viewport height.</param>
        /// <param name="verticalFovDeg">Vertical field-of-view in degrees.</param>
        /// <returns>Camera altitude in metres.</returns>
        public static double AltitudeForZoom(double zoom, double viewportHeightPx, double verticalFovDeg)
        {
            double metersPerPixel = MetersPerPixel(zoom);
            double halfFovRad     = Angle.FromDegrees(verticalFovDeg * 0.5).Radians;
            return (viewportHeightPx * metersPerPixel) / (2.0 * math.tan(halfFovRad));
        }

        /// <summary>
        /// Inverse of <see cref="AltitudeForZoom"/>: converts an altitude back to zoom.
        /// Used when a <see cref="CameraPropertiesUpdate"/> supplies <c>Distance</c> (D1 round-trip).
        /// </summary>
        public static double ZoomForDistance(double altitudeMetres, double viewportHeightPx, double verticalFovDeg)
        {
            double halfFovRad     = Angle.FromDegrees(verticalFovDeg * 0.5).Radians;
            double metersPerPixel = (2.0 * altitudeMetres * math.tan(halfFovRad)) / viewportHeightPx;
            // altitude = (vpH * mpp) / (2 * tan(fov/2))  →  mpp = altitude*2*tan(fov/2)/vpH
            // mpp = EarthCirc / (TilePx * 2^zoom)  →  zoom = log2(EarthCirc / (TilePx * mpp))
            if (metersPerPixel <= 0) return 0;
            return math.log2(EarthCircumferenceMetres / (TilePixelSize * metersPerPixel));
        }

        // ── Clip planes (derived from altitude — S42 D3) ──────────────────────────────────────────

        /// <summary>Near clip plane from altitude (S42 D3: near = altitude · 0.01, min 0.1).</summary>
        public static double NearClip(double altitude) => math.max(0.1, altitude * 0.01);

        /// <summary>Far clip plane from altitude (S42 D3: far = altitude · 4).</summary>
        public static double FarClip(double altitude) => altitude * 4.0;

        // ── Pose computation ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Computes camera position and forward/up vectors from a <see cref="CameraProperties"/>.
        ///
        /// <para><b>Coordinate convention:</b> right-handed, Y=up, X=east, Z=north; heading is CW from
        /// north. The camera orbits the look-at (origin under camera-relative rendering).</para>
        ///
        /// <para><b>Orbit geometry (closed form).</b> The pose is <c>Ry(heading)</c> applied to the
        /// heading-0 pose. At heading 0 the camera sits <b>south of</b> and above the look-at and looks
        /// <b>north</b> and down, so increasing tilt swings the view from straight-down toward the
        /// horizon in the bearing direction (at bearing 0, north recedes to the top of the screen — the
        /// MapLibre convention). With <c>sinT=tilt.Sin</c>, <c>cosT=tilt.Cos</c>, <c>sinH/cosH</c>:
        /// <code>
        ///   pos = (−alt·sinT·sinH,  alt·cosT,  −alt·sinT·cosH)   // camera, opposite the bearing dir
        ///   fwd = −pos/alt = (sinT·sinH, −cosT, sinT·cosH)       // toward look-at; |pos| ≡ alt
        ///   up  = (sinH·cosT,  sinT,  cosH·cosT)                 // world-up preserved (up.y = sinT ≥ 0)
        /// </code>
        /// </para>
        ///
        /// <para>This <c>up</c> is provably unit-length and orthogonal to <c>fwd</c> for <b>all</b>
        /// tilt∈[0,90] (the heading cross-terms cancel via sin²H+cos²H=1), so no Gram-Schmidt /
        /// degeneracy fallback is needed: at tilt=0 it is the non-degenerate heading-derived
        /// north-up <c>(sinH,0,cosH)</c>; at tilt=90 it is world-up <c>(0,1,0)</c>. Keeping
        /// <c>up.y=sinT≥0</c> is what prevents the image from flipping vertically as tilt→90.</para>
        /// </summary>
        /// <param name="altitude">Camera orbit radius in metres (from <see cref="AltitudeForZoom"/>).</param>
        /// <param name="heading">Camera bearing (constraint already enforced upstream).</param>
        /// <param name="tilt">Camera tilt angle (constraint already enforced upstream).</param>
        /// <param name="pos">Output: camera position offset from look-at (render-space).</param>
        /// <param name="fwd">Output: unit forward vector (toward look-at).</param>
        /// <param name="up">Output: camera up vector (world-up preserved; non-degenerate at tilt=0).</param>
        public static void ComputePose(double altitude,
            Angle                             heading, Angle       tilt,
            out double3                       pos,     out double3 fwd, out double3 up)
        {
            double sinH = heading.Sin;
            double cosH = heading.Cos;
            double sinT = tilt.Sin;
            double cosT = tilt.Cos;

            // Camera position: orbit offset on the side OPPOSITE the bearing direction (so the camera
            // looks toward the bearing). At tilt=0 this is (0, altitude, 0) — directly overhead.
            double px = -altitude * sinT * sinH;
            double py =  altitude * cosT;
            double pz = -altitude * sinT * cosH;
            pos = new double3(px, py, pz);

            // Forward = toward the look-at = -pos normalized. |pos| ≡ altitude analytically, but divide
            // by the computed length to stay robust to rounding.
            double len           = math.sqrt(px * px + py * py + pz * pz);
            if (len < 1e-10) len = 1e-10;
            fwd = new double3(-px / len, -py / len, -pz / len);

            // Camera up: world-up preserved through the tilt, rotated by heading. Closed form (always
            // unit-length and ⟂ fwd) — see method doc. up.y = sinT ≥ 0 keeps the sky up at every tilt.
            up = new double3(sinH * cosT, sinT, cosH * cosT);
        }

        // ── Interpolation helpers (D4) ──────────────────────────────────────────────────────────

        /// <summary>
        /// Interpolates heading using the shortest angular path (D4).
        /// 350 → 10 goes +20° (not −340°). Thin shim over <see cref="Angle.LerpShortest"/>.
        /// </summary>
        public static double LerpHeadingShortest(double from, double to, double t)
            => Angle.LerpShortest(Angle.FromDegrees(from), Angle.FromDegrees(to), t).Degrees;

        /// <summary>Linearly interpolates a scalar value.</summary>
        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        /// <summary>
        /// Interpolates two <see cref="CameraProperties"/> values at parameter <paramref name="t"/>
        /// according to D4 interpolation spaces:
        /// <list type="bullet">
        ///   <item>Zoom — zoom-space (not altitude).</item>
        ///   <item>LookAt lat/lon — linear (Web-Mercator; close-enough for the non-flyTo path).</item>
        ///   <item>Heading — shortest-angle wrap.</item>
        ///   <item>Tilt — linear.</item>
        /// </list>
        /// </summary>
        public static CameraProperties Interpolate(CameraProperties from, CameraProperties to, double t)
        {
            double zoom      = Lerp(from.Zoom, to.Zoom, t);
            double latitude  = Lerp(from.LookAt.Latitude, to.LookAt.Latitude, t);
            double longitude = Lerp(from.LookAt.Longitude, to.LookAt.Longitude, t);
            double altitude  = Lerp(from.LookAt.Altitude, to.LookAt.Altitude, t);
            double heading   = LerpHeadingShortest(from.Heading.Degrees, to.Heading.Degrees, t);
            double tilt      = Lerp(from.Tilt.Degrees, to.Tilt.Degrees, t);
            return new CameraProperties(
                new GeoCoordinate3D { Latitude = latitude, Longitude = longitude, Altitude = altitude }, zoom, heading,
                tilt);
        }
    }
}