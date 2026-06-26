using System;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

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
    ///     <c>Altitude</c> is reserved for terrain; pass <c>0</c> until S25.</item>
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
        /// The point the camera orbits around (lon, lat, altitude).
        /// Altitude is reserved (terrain seam, S25). Use <c>0</c> for now.
        /// </summary>
        public readonly GeoCoordinate3D LookAt;

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
        /// <summary>
        /// Web-Mercator latitude limit. Thin alias for <see cref="WebMercator.MaxLatitude"/>;
        /// single source of truth lives on WebMercator (S62).
        /// </summary>
        public const double MaxMercatorLat = WebMercator.MaxLatitude;

        // ── Construction ─────────────────────────────────────────────────────────────────────
        public CameraProperties(GeoCoordinate3D lookAt, double zoom, double heading, double tilt)
        {
            LookAt  = lookAt;
            Zoom    = zoom;
            Heading = NormalizeHeading(heading);
            Tilt    = tilt;
        }

        /// <summary>Default (un-initialized) camera at origin, zoom 0, no heading/tilt.</summary>
        public static CameraProperties Default => new CameraProperties(new GeoCoordinate3D(), 0, 0, 0);

        // ── Derived helpers ───────────────────────────────────────────────────────────────────

        /// <summary>Integer tile zoom for tile selection: floor(Zoom), min 0.</summary>
        public int IntegerZoom => math.max(0, (int)math.floor(Zoom));

        /// <summary>
        /// Center clamped to Mercator bounds, projected to Web-Mercator meters.
        /// </summary>
        public double2 CenterMercator()
        {
            double latitude = math.max(-MaxMercatorLat, math.min(MaxMercatorLat, LookAt.Latitude));
            return WebMercator.FromLonLat(new GeoCoordinate3D { Latitude = latitude, Longitude = LookAt.Longitude });
        }

        // ── Utilities ────────────────────────────────────────────────────────────────────────

        /// <summary>Normalizes a heading value into [0, 360).</summary>
        public static double NormalizeHeading(double h)
        {
            h %= 360.0;
            if (h < 0) h += 360.0;
            return h;
        }

        public override string ToString()
        {
            return $"CameraProperties(LookAt={LookAt}, z={Zoom:F3}, heading={{Heading:F1}}, tilt={{Tilt:F1}})";
        }
    }
}