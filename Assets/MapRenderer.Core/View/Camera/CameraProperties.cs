using System;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Core.View.Camera
{
    /// <summary>
    /// S45/S50: Canonical, immutable camera state — the single camera-state type across Core, Unity,
    /// and tests.
    ///
    /// <para><b>D1 — Zoom is canonical.</b> Distance/altitude are derived from zoom via the
    /// altitude-from-zoom formula (Web-Mercator perspective framing); see <see cref="CameraPoseMath"/>.</para>
    ///
    /// <para><b>Field semantics:</b>
    /// <list type="bullet">
    ///   <item><see cref="LookAt"/> — geographic coordinate (WGS-84) the camera orbits around.
    ///     <c>Altitude</c> is reserved for terrain; pass <c>0</c> until S25.</item>
    ///   <item><see cref="Zoom"/> — fractional MapLibre zoom. Higher = more zoomed in (smaller
    ///     ground footprint). Canonical; drives distance/altitude.</item>
    ///   <item><see cref="Heading"/> — camera bearing, degrees CW from north.
    ///     <see cref="ConstrainedAngle"/> Wrap to <c>[0, 360)</c>.</item>
    ///   <item><see cref="Tilt"/> — camera tilt relative to the surface normal at LookAt (§7).
    ///     <c>tilt=0</c> is top-down; <c>tilt=90</c> is parallel to the surface (horizon).
    ///     <see cref="ConstrainedAngle"/> Clamp to <c>[0, 90]</c>.</item>
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
        /// <summary>
        /// Camera bearing, degrees CW from north. <see cref="ConstrainedAngle"/> Wrap to
        /// <c>[0, 360)</c>. The stored value is always in range; any heading supplied to
        /// the constructor is normalized on entry.
        /// </summary>
        public readonly ConstrainedAngle Heading;

        /// <summary>
        /// Camera tilt, degrees, relative to the surface normal at <see cref="LookAt"/>
        /// (§7 locked definition). <c>tilt=0</c> ⇒ camera forward is the inverse of the earth
        /// normal at LookAt (top-down on Mercator); <c>tilt=90</c> ⇒ forward is parallel to the
        /// surface (horizon). <see cref="ConstrainedAngle"/> Clamp to <c>[0, 90]</c>.
        /// </summary>
        public readonly ConstrainedAngle Tilt;

        /// <summary>
        /// Vertical field of view in degrees — the perspective lens. Part of the camera state: it drives
        /// the zoom→altitude framing and is pushed to the Unity camera's <c>fieldOfView</c>. Unlike the
        /// viewport pixel size (a live measurement read from the camera), FOV is a genuine parameter.
        /// </summary>
        public readonly double VerticalFovDeg;

        // ── Constants ─────────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Web-Mercator latitude limit. Thin alias for <see cref="WebMercator.MaxLatitude"/>;
        /// single source of truth lives on WebMercator (S62).
        /// </summary>
        public const double MaxMercatorLat = WebMercator.MaxLatitude;

        // ── Construction ─────────────────────────────────────────────────────────────────────
        public CameraProperties(GeoCoordinate3D lookAt, double zoom, double heading, double tilt,
                                double verticalFovDeg = 60.0)
        {
            LookAt         = lookAt;
            Zoom           = zoom;
            Heading        = ConstrainedAngle.Heading(heading);
            Tilt           = ConstrainedAngle.Tilt(tilt);
            VerticalFovDeg = verticalFovDeg;
        }

        /// <summary>Default (un-initialized) camera at origin, zoom 0, no heading/tilt, default 60° FOV.</summary>
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

        public override string ToString()
        {
            return $"CameraProperties(LookAt={LookAt}, z={Zoom:F3}, heading={Heading.Degrees:F1}, tilt={Tilt.Degrees:F1}, fov={VerticalFovDeg:F1})";
        }
    }
}