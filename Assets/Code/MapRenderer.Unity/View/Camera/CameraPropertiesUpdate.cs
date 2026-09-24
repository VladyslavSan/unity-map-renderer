using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View.Camera
{
    /// <summary>
    /// A nullable PATCH struct: <see cref="ApplyTo"/> applies only its non-null fields to the current
    /// <see cref="CameraProperties"/>. Engine-free. A <c>struct</c>, so the Duration==0 (jumpTo) fast path
    /// allocates nothing on the heap. Zoom is the only scale field, because altitude derives from it
    /// (<see cref="CameraPoseMath.AltitudeForZoom"/>).
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