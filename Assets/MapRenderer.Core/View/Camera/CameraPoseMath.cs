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
        /// <para><b>Coordinate convention:</b> +Y is up, camera orbits the look-at origin.</para>
        ///
        /// <para><b>D6a — Overhead degeneracy fix:</b> at tilt=0, the camera is directly above;
        /// LookRotation(-offset.normalized, upHint) would be fed (0,-1,0) as forward. We derive
        /// <c>upHint</c> from the heading direction in the horizontal plane so it is NEVER
        /// anti-parallel to the world-up direction, giving deterministic north-up at any bearing.</para>
        /// </summary>
        /// <param name="altitude">Camera orbit radius in metres (from <see cref="AltitudeForZoom"/>).</param>
        /// <param name="heading">Camera bearing (constraint already enforced upstream).</param>
        /// <param name="tilt">Camera tilt angle (constraint already enforced upstream).</param>
        /// <param name="pos">Output: camera position offset from look-at (render-space).</param>
        /// <param name="fwd">Output: unit forward vector (toward look-at).</param>
        /// <param name="up">Output: camera up vector (deterministic, heading-derived).</param>
        public static void ComputePose(double altitude,
            Angle                             heading, Angle       tilt,
            out double3                       pos,     out double3 fwd, out double3 up)
        {
            // Orbit offset from the look-at: Ry(heading) · Rx(tilt) · (0, altitude, 0).
            //
            // Standard Unity/right-handed frame: Y=up, X=east, Z=north; heading is CW from north so
            // east is the +heading direction.
            //
            // Rx(tilt)·(0, alt, 0) = (0, alt·cosT, alt·sinT)        — tilts toward +Z (north at heading=0).
            // Ry(heading)·(0, alt·cosT, alt·sinT), with Ry = [cosH 0 sinH; 0 1 0; -sinH 0 cosH]:
            //   x = alt·sinT·sinH   ← east  (at heading=90, tilt>0: camera moves east)
            //   y = alt·cosT        ← up    (= altitude at tilt=0 → directly overhead; ≥0 for tilt∈[0,90])
            //   z = alt·sinT·cosH   ← north (at heading=0, tilt>0: camera moves toward north)
            double sinH = heading.Sin;
            double cosH = heading.Cos;
            double sinT = tilt.Sin;
            double cosT = tilt.Cos;

            double px = altitude * sinT * sinH;
            double py = altitude * cosT;
            double pz = altitude * sinT * cosH;

            pos = new double3(px, py, pz);

            // Forward vector: from camera toward look-at (= origin of orbit) = -pos.normalized
            double len           = math.sqrt(px * px + py * py + pz * pz);
            if (len < 1e-10) len = 1e-10;
            fwd = new double3(-px / len, -py / len, -pz / len);

            // Up vector (D6b — heading-derived, non-degenerate at tilt=0):
            // The camera's "up" should point roughly north-rotated-by-heading in the horizontal plane.
            // At heading=0: north is +Z, so up ≈ (0, 0, 1) transformed by heading.
            // upHint = Ry(heading) * (0, 0, 1) = (sinH, 0, cosH)  — a horizontal vector pointing
            //   in the heading direction rotated 90° back (i.e., the north direction in the heading frame).
            // This is never anti-parallel to fwd because fwd has a -Y component (camera looking down)
            // while upHint is purely horizontal. At tilt=0, fwd=(0,-1,0) and upHint=(sinH,0,cosH) are
            // orthogonal — LookRotation gets well-behaved vectors.
            //
            // Geometric meaning: camera's up = "which way does north project on the camera's image plane".
            // At heading=0: north (+Z) is projected up. At heading=90: east (+X) is up on screen.
            double upX = sinH; // = sin(heading)
            double upY = 0.0;
            double upZ = cosH; // = cos(heading)

            // Gram-Schmidt: orthogonalize upHint against fwd to get the true camera up.
            // up = normalize(upHint - (upHint·fwd)*fwd)
            double dot     = upX * fwd.x + upY * fwd.y + upZ * fwd.z;
            double orthX   = upX         - dot * fwd.x;
            double orthY   = upY         - dot * fwd.y;
            double orthZ   = upZ         - dot * fwd.z;
            double orthLen = math.sqrt(orthX * orthX + orthY * orthY + orthZ * orthZ);
            if (orthLen < 1e-10)
            {
                // Extremely unlikely fallback (upHint parallel to fwd): use world +Y as hint.
                orthX   = -fwd.x * (-fwd.y);
                orthY   = 1.0 - fwd.y * fwd.y;
                orthZ   = -fwd.z * (-fwd.y);
                orthLen = math.sqrt(orthX * orthX + orthY * orthY + orthZ * orthZ);
                if (orthLen < 1e-10) orthLen = 1.0;
            }

            up = new double3(orthX / orthLen, orthY / orthLen, orthZ / orthLen);
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