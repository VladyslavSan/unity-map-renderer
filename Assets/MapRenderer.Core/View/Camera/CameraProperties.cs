using System;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Core.View.Camera
{
    /// <summary>
    /// S45/S50: Canonical, immutable camera state — the single camera-state type across Core, Unity,
    /// and tests.
    ///
    /// <para><b>D1 — Zoom is canonical.</b> Distance and altitude are derived via the altitude-from-zoom
    /// formula (Web-Mercator perspective framing). Callers may supply <see cref="Distance"/>; it is
    /// immediately converted back to zoom so the round-trip is exact.</para>
    ///
    /// <para><b>Field semantics:</b>
    /// <list type="bullet">
    ///   <item><see cref="LookAt"/> — geographic coordinate (WGS-84) the camera orbits around.
    ///     <c>Alt</c> is reserved for terrain; pass <c>0</c> until S25.</item>
    ///   <item><see cref="Zoom"/> — fractional MapLibre zoom. Higher = more zoomed in (smaller
    ///     ground footprint). Canonical; drives distance/altitude.</item>
    ///   <item><see cref="Heading"/> — camera bearing, degrees CW from north. [0, 360).</item>
    ///   <item><see cref="Tilt"/> — camera pitch away from straight-down, degrees [0, ~85].</item>
    /// </list>
    /// </para>
    ///
    /// <para>Engine-free: only <c>Unity.Mathematics</c> types permitted.</para>
    /// </summary>
    public readonly struct CameraProperties
    {
        // ── Geographic anchor ────────────────────────────────────────────────────────────────────
        /// <summary>
        /// The point the camera orbits around (lon, lat, alt).
        /// Alt is reserved (terrain seam, S25). Use <c>0</c> for now.
        /// </summary>
        public readonly LookAtPoint LookAt;

        // ── Canonical zoom (D1) ───────────────────────────────────────────────────────────────
        /// <summary>
        /// Fractional MapLibre zoom level. Higher = zoomed in. <b>Canonical</b>; distance/altitude
        /// are derived. See <see cref="CameraPoseMath.AltitudeForZoom"/> for the derivation.
        /// </summary>
        public readonly double Zoom;

        // ── Orientation ───────────────────────────────────────────────────────────────────────
        /// <summary>Camera bearing, degrees CW from north. Normalized to [0, 360).</summary>
        public readonly double Heading;

        /// <summary>Camera tilt from straight-down, degrees. [0, maxTilt].</summary>
        public readonly double Tilt;

        // ── Constants ─────────────────────────────────────────────────────────────────────────
        /// <summary>Web-Mercator latitude limit (±85.05112878°).</summary>
        public const double MaxMercatorLat = 85.05112878;

        // ── Construction ─────────────────────────────────────────────────────────────────────
        public CameraProperties(LookAtPoint lookAt, double zoom, double heading, double tilt)
        {
            LookAt  = lookAt;
            Zoom    = zoom;
            Heading = NormalizeHeading(heading);
            Tilt    = tilt;
        }

        /// <summary>Default (un-initialized) camera at origin, zoom 0, no heading/tilt.</summary>
        public static CameraProperties Default
            => new CameraProperties(new LookAtPoint(0, 0, 0), 0, 0, 0);

        // ── Derived helpers ───────────────────────────────────────────────────────────────────

        /// <summary>Integer tile zoom for tile selection: floor(Zoom), min 0.</summary>
        public int IntegerZoom => Math.Max(0, (int)Math.Floor(Zoom));

        /// <summary>
        /// Center clamped to Mercator bounds, projected to Web-Mercator meters.
        /// </summary>
        public Unity.Mathematics.double2 CenterMercator()
        {
            double lat = Math.Max(-MaxMercatorLat, Math.Min(MaxMercatorLat, LookAt.Lat));
            return WebMercator.FromLonLat(LookAt.Lon, lat);
        }

        // ── With* helpers (return new instances; value-type semantics) ────────────────────────

        public CameraProperties WithLookAt(LookAtPoint p)
            => new CameraProperties(p, Zoom, Heading, Tilt);

        public CameraProperties WithZoom(double zoom)
            => new CameraProperties(LookAt, zoom, Heading, Tilt);

        public CameraProperties WithHeading(double heading)
            => new CameraProperties(LookAt, Zoom, heading, Tilt);

        public CameraProperties WithTilt(double tilt)
            => new CameraProperties(LookAt, Zoom, Heading, tilt);

        public CameraProperties WithOrientation(double heading, double tilt)
            => new CameraProperties(LookAt, Zoom, heading, tilt);

        // ── Utilities ────────────────────────────────────────────────────────────────────────

        /// <summary>Normalizes a heading value into [0, 360).</summary>
        public static double NormalizeHeading(double h)
        {
            h %= 360.0;
            if (h < 0) h += 360.0;
            return h;
        }

        public override string ToString()
            => $"CameraProperties(lon={LookAt.Lon:F5}, lat={LookAt.Lat:F5}, z={Zoom:F3}, " +
               $"heading={Heading:F1}, tilt={Tilt:F1})";
    }

    /// <summary>
    /// Geographic anchor point (lon, lat, alt). <c>Alt</c> is reserved for the terrain seam (S25).
    /// </summary>
    public readonly struct LookAtPoint
    {
        /// <summary>Longitude, degrees [-180, 180].</summary>
        public readonly double Lon;
        /// <summary>Latitude, degrees. Clamped to Mercator limit by callers.</summary>
        public readonly double Lat;
        /// <summary>Altitude above ground in metres. Reserved; use 0 until S25.</summary>
        public readonly double Alt;

        public LookAtPoint(double lon, double lat, double alt = 0.0)
        {
            Lon = lon;
            Lat = lat;
            Alt = alt;
        }

        public override string ToString() => $"({Lon:F5}, {Lat:F5}, alt={Alt:F1})";
    }
}
