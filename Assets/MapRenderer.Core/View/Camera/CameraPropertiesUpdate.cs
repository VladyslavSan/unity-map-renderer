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
    /// <para>Usage — build a patch and hand it to <see cref="ApplyTo"/> (or the MapCamera.Apply seam):</para>
    /// <code>
    ///   // Change only tilt (all other fields kept):
    ///   next = new CameraPropertiesUpdate { Tilt = 45 }.ApplyTo(current);
    ///
    ///   // Pan (lon+lat):
    ///   next = new CameraPropertiesUpdate { Longitude = 13.4, Latitude = 52.5 }.ApplyTo(current);
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

        // ── Zoom ──────────────────────────────────────────────────────────────────────────────
        /// <summary>New zoom level (canonical). Null = keep current.</summary>
        public double? Zoom;

        // ── Orientation ───────────────────────────────────────────────────────────────────────
        /// <summary>New heading (bearing), degrees CW from north. Null = keep current.</summary>
        public double? Heading;

        /// <summary>New tilt (pitch from straight-down), degrees. Null = keep current.</summary>
        public double? Tilt;

        /// <summary>True if no field has been set (no-op patch).</summary>
        public bool IsEmpty =>
            Longitude == null && Latitude == null && Altitude == null &&
            Zoom      == null &&
            Heading   == null && Tilt == null;

        /// <summary>
        /// Merges this patch over <paramref name="current"/>, returning the next camera state. Fields left
        /// null in the patch are taken from <paramref name="current"/>; the lens (FOV) is carried through.
        /// </summary>
        public CameraProperties ApplyTo(in CameraProperties current)
        {
            double latitude  = Latitude  ?? current.LookAt.Latitude;
            double longitude = Longitude ?? current.LookAt.Longitude;
            double altitude  = Altitude  ?? current.LookAt.Altitude;
            double heading   = Heading   ?? current.Heading.Degrees;
            double tilt      = Tilt      ?? current.Tilt.Degrees;
            double zoom      = Zoom      ?? current.Zoom;

            return new CameraProperties(
                new GeoCoordinate3D { Latitude = latitude, Longitude = longitude, Altitude = altitude }, zoom,
                heading, tilt, current.VerticalFovDeg);
        }
    }
}