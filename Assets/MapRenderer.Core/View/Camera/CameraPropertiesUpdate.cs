using MapRenderer.Core.Geo;

namespace MapRenderer.Core.View.Camera
{
    /// <summary>
    /// S45 D2: A nullable PATCH struct. Every field is optional; only non-null fields are applied
    /// to the current <see cref="CameraProperties"/> by <see cref="CameraSystem.Apply"/>.
    ///
    /// <para><b>Value type / zero allocation:</b> this is a <c>struct</c> — passing it on the
    /// Duration==0 (instant/jumpTo) fast path allocates zero bytes on the heap. No boxing unless
    /// the caller stores it as an interface (avoid).</para>
    ///
    /// <para><b>Either Zoom or Distance may be set</b> (D1): supplying <see cref="Distance"/>
    /// converts to zoom on apply so round-trips are exact. Both fields set is valid — last write wins
    /// (Zoom overrides Distance).</para>
    ///
    /// <para>Usage:</para>
    /// <code>
    ///   // Change only tilt (all other fields kept):
    ///   cameraSystem.Apply(new CameraPropertiesUpdate { Tilt = 45 }, CameraAnimation.Instant);
    ///
    ///   // Pan (lon+lat) smoothly:
    ///   cameraSystem.Apply(new CameraPropertiesUpdate { Lon = 13.4, Lat = 52.5 }, new CameraAnimation(1.5));
    /// </code>
    ///
    /// <para>Engine-free; no UnityEngine dependency.</para>
    /// </summary>
    public struct CameraPropertiesUpdate
    {
        // ── LookAt fields ────────────────────────────────────────────────────────────────────
        /// <summary>New LookAt latitude (degrees). Null = keep current.</summary>
        public double? Latitude;

        /// <summary>New LookAt longitude (degrees). Null = keep current.</summary>
        public double? Longitude;

        /// <summary>New LookAt altitude (metres, reserved). Null = keep current.</summary>
        public double? Altitude;

        // ── Zoom / distance ───────────────────────────────────────────────────────────────────
        /// <summary>New zoom level (canonical, D1). Null = keep current (use Distance if set).</summary>
        public double? Zoom;

        /// <summary>
        /// New camera distance in render-space metres (converted to canonical zoom on apply).
        /// Null = keep current. Ignored when <see cref="Zoom"/> is also set.
        /// </summary>
        public double? Distance;

        // ── Orientation ───────────────────────────────────────────────────────────────────────
        /// <summary>New heading (bearing), degrees CW from north. Null = keep current.</summary>
        public double? Heading;

        /// <summary>New tilt (pitch from straight-down), degrees. Null = keep current.</summary>
        public double? Tilt;

        /// <summary>True if no field has been set (no-op patch).</summary>
        public bool IsEmpty =>
            Longitude == null && Latitude == null && Altitude == null &&
            Zoom      == null && Distance == null &&
            Heading   == null && Tilt     == null;

        /// <summary>
        /// Merges this patch over <paramref name="current"/>. Fields left null in the patch are
        /// taken from <paramref name="current"/>. Distance is converted to zoom if Zoom is absent.
        /// </summary>
        public CameraProperties ApplyTo(CameraProperties current,
            double                                       referenceViewportHeightPx,
            double                                       verticalFovDeg)
        {
            double latitude  = Latitude  ?? current.LookAt.Latitude;
            double longitude = Longitude ?? current.LookAt.Longitude;
            double altitude  = Altitude  ?? current.LookAt.Altitude;
            double heading   = Heading   ?? current.Heading;
            double tilt      = Tilt      ?? current.Tilt;

            // Zoom wins over Distance if both are set.
            double zoom;
            if (Zoom.HasValue)
                zoom = Zoom.Value;
            else if (Distance.HasValue)
                zoom = CameraPoseMath.ZoomForDistance(Distance.Value, referenceViewportHeightPx, verticalFovDeg);
            else
                zoom = current.Zoom;

            return new CameraProperties(
                new GeoCoordinate3D { Latitude = latitude, Longitude = longitude, Altitude = altitude }, zoom,
                heading, tilt);
        }
    }
}