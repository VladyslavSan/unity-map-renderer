using System;

namespace MapRenderer.Core.View.Camera
{
    /// <summary>
    /// S45: Engine-free pose math.
    ///
    /// Absorbed from <c>MapController.AltitudeForZoom</c> and <c>MapController.ApplyCameraTransform</c>
    /// (S42). Now lives in Core so headless tests can exercise the full pose computation.
    ///
    /// <para><b>Altitude formula (D1 — S42 D2):</b>
    ///   Web-Mercator ground resolution: metersPerPixel = 40075016.686 / (256 × 2^zoom).
    ///   For a perspective camera looking straight down:
    ///   altitude = (viewportHeightPx × metersPerPixel) / (2 × tan(verticalFovDeg/2)).
    ///   Earth equatorial circumference: 40075016.686 m (IAU/WGS-84, clean-room constant).</para>
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
        // ── Constants (clean-room: IAU/WGS-84 earth circumference) ─────────────────────────────
        private const double EarthCircumferenceMetres = 40075016.686;
        private const double TilePixelSize            = 256.0;

        // ── Altitude ↔ Zoom ──────────────────────────────────────────────────────────────────────

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
            double metersPerPixel = EarthCircumferenceMetres / (TilePixelSize * Math.Pow(2.0, zoom));
            double halfFovRad     = verticalFovDeg * 0.5 * Math.PI / 180.0;
            return (viewportHeightPx * metersPerPixel) / (2.0 * Math.Tan(halfFovRad));
        }

        /// <summary>
        /// Inverse of <see cref="AltitudeForZoom"/>: converts an altitude back to zoom.
        /// Used when a <see cref="CameraPropertiesUpdate"/> supplies <c>Distance</c> (D1 round-trip).
        /// </summary>
        public static double ZoomForDistance(double altitudeMetres, double viewportHeightPx, double verticalFovDeg)
        {
            double halfFovRad     = verticalFovDeg * 0.5 * Math.PI / 180.0;
            double metersPerPixel = (2.0 * altitudeMetres * Math.Tan(halfFovRad)) / viewportHeightPx;
            // altitude = (vpH * mpp) / (2 * tan(fov/2))  →  mpp = altitude*2*tan(fov/2)/vpH
            // mpp = EarthCirc / (TilePx * 2^zoom)  →  zoom = log2(EarthCirc / (TilePx * mpp))
            if (metersPerPixel <= 0) return 0;
            return Math.Log(EarthCircumferenceMetres / (TilePixelSize * metersPerPixel), 2.0);
        }

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
        /// <param name="headingDeg">Camera bearing, degrees CW from north.</param>
        /// <param name="tiltDeg">Camera pitch from straight-down, degrees.</param>
        /// <param name="pos">Output: camera position offset from look-at (render-space).</param>
        /// <param name="fwd">Output: unit forward vector (toward look-at).</param>
        /// <param name="up">Output: camera up vector (deterministic, heading-derived).</param>
        public static void ComputePose(double altitude,
                                       double headingDeg, double tiltDeg,
                                       out Double3 pos, out Double3 fwd, out Double3 up)
        {
            double headRad = headingDeg * Math.PI / 180.0;
            double tiltRad = tiltDeg   * Math.PI / 180.0;

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
            double sinH = Math.Sin(headRad);
            double cosH = Math.Cos(headRad);
            double sinT = Math.Sin(tiltRad);
            double cosT = Math.Cos(tiltRad);

            double px = altitude * sinT * sinH;
            double py = altitude * cosT;
            double pz = altitude * sinT * cosH;

            pos = new Double3(px, py, pz);

            // Forward vector: from camera toward look-at (= origin of orbit) = -pos.normalized
            double len = Math.Sqrt(px * px + py * py + pz * pz);
            if (len < 1e-10) len = 1e-10;
            fwd = new Double3(-px / len, -py / len, -pz / len);

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
            double upX = sinH;  // = sin(heading)
            double upY = 0.0;
            double upZ = cosH;  // = cos(heading)

            // Gram-Schmidt: orthogonalize upHint against fwd to get the true camera up.
            // up = normalize(upHint - (upHint·fwd)*fwd)
            double dot = upX * fwd.X + upY * fwd.Y + upZ * fwd.Z;
            double orthX = upX - dot * fwd.X;
            double orthY = upY - dot * fwd.Y;
            double orthZ = upZ - dot * fwd.Z;
            double orthLen = Math.Sqrt(orthX * orthX + orthY * orthY + orthZ * orthZ);
            if (orthLen < 1e-10)
            {
                // Extremely unlikely fallback (upHint parallel to fwd): use world +Y as hint.
                orthX = -fwd.X * (-fwd.Y);
                orthY = 1.0 - fwd.Y * fwd.Y;
                orthZ = -fwd.Z * (-fwd.Y);
                orthLen = Math.Sqrt(orthX * orthX + orthY * orthY + orthZ * orthZ);
                if (orthLen < 1e-10) orthLen = 1.0;
            }
            up = new Double3(orthX / orthLen, orthY / orthLen, orthZ / orthLen);
        }

        // ── Interpolation helpers (D4) ──────────────────────────────────────────────────────────

        /// <summary>
        /// Interpolates heading using the shortest angular path (D4).
        /// 350 → 10 goes +20° (not −340°).
        /// </summary>
        public static double LerpHeadingShortest(double from, double to, double t)
        {
            double diff = ((to - from + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
            return CameraProperties.NormalizeHeading(from + diff * t);
        }

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
            double zoom    = Lerp(from.Zoom, to.Zoom, t);
            double lon     = Lerp(from.LookAt.Lon, to.LookAt.Lon, t);
            double lat     = Lerp(from.LookAt.Lat, to.LookAt.Lat, t);
            double alt     = Lerp(from.LookAt.Alt, to.LookAt.Alt, t);
            double heading = LerpHeadingShortest(from.Heading, to.Heading, t);
            double tilt    = Lerp(from.Tilt, to.Tilt, t);
            return new CameraProperties(new LookAtPoint(lon, lat, alt), zoom, heading, tilt);
        }
    }

    /// <summary>
    /// Minimal engine-free 3-component double vector. Used by <see cref="CameraPoseMath"/> to stay
    /// engine-free while computing camera positions and orientation vectors.
    ///
    /// <para>Only arithmetic the pose math actually needs is implemented (lengths, dot product).
    /// The Unity layer converts these to <c>Vector3</c> / <c>Quaternion</c>.</para>
    /// </summary>
    public readonly struct Double3
    {
        public readonly double X, Y, Z;

        public Double3(double x, double y, double z)
        {
            X = x; Y = y; Z = z;
        }

        public static Double3 Zero => new Double3(0, 0, 0);

        public double LengthSquared => X * X + Y * Y + Z * Z;
        public double Length        => Math.Sqrt(LengthSquared);

        public Double3 Normalized
        {
            get
            {
                double len = Length;
                if (len < 1e-10) return Zero;
                return new Double3(X / len, Y / len, Z / len);
            }
        }

        public override string ToString() => $"({X:F4}, {Y:F4}, {Z:F4})";
    }
}
