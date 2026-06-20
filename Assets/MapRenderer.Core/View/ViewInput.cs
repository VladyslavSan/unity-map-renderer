using System;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// Pure (engine-free, allocation-free) input → <see cref="ViewState"/> mutation helpers. Factored out
    /// of <c>MapController</c> so the pan/zoom/tilt logic is unit-testable headless — reading
    /// <c>UnityEngine.Input</c> in <c>Update()</c> directly would have zero test coverage.
    ///
    /// Conventions:
    ///   - <b>Zoom</b> from scroll: positive scroll zooms in (z increases), with a sensitivity factor.
    ///     Zoom is clamped to [minZoom, maxZoom].
    ///   - <b>Pan</b> from a screen-space drag delta: converts a pixel delta to a lon/lat delta using the
    ///     ground resolution at the current zoom/latitude (so a drag moves the map by the dragged amount,
    ///     not a fixed lon/lat step). Drag-right moves the map center west (content follows the cursor).
    ///   - <b>Tilt/bearing</b>: a screen-space drag delta maps to pitch (vertical) and bearing (horizontal)
    ///     in degrees, each clamped to a sane range.
    /// </summary>
    public static class ViewInput
    {
        /// <summary>Web-Mercator world circumference in tile pixels at zoom z is 256·2^z (256-px tiles).</summary>
        public const double TilePixelSize = 256.0;

        /// <summary>
        /// Applies a scroll delta to the view's zoom. <paramref name="scrollDelta"/> is the raw scroll
        /// (e.g. <c>Input.mouseScrollDelta.y</c>); <paramref name="sensitivity"/> scales it to zoom levels.
        /// Result is clamped to [<paramref name="minZoom"/>, <paramref name="maxZoom"/>].
        /// </summary>
        public static ViewState ApplyZoom(ViewState v, double scrollDelta, double sensitivity,
                                          double minZoom, double maxZoom)
        {
            double z = v.Zoom + scrollDelta * sensitivity;
            if (z < minZoom) z = minZoom;
            if (z > maxZoom) z = maxZoom;
            return v.WithZoom(z);
        }

        /// <summary>
        /// Converts a screen-space drag (in pixels) to a new center lon/lat. A drag moves the map content
        /// with the cursor: dragging right (positive <paramref name="dxPixels"/>) shifts the center west.
        /// The pixel→degree conversion uses the ground resolution at the current zoom and latitude.
        /// </summary>
        public static ViewState ApplyPan(ViewState v, double dxPixels, double dyPixels)
        {
            double n = Math.Pow(2.0, v.Zoom);
            double worldPixels = TilePixelSize * n;          // pixels spanning 360° of longitude
            double degPerPixelLon = 360.0 / worldPixels;

            // Latitude degrees-per-pixel shrinks toward the poles (Mercator). Use the local cos(lat)
            // factor so vertical drags track the cursor at the current latitude.
            double latRad = Clamp(v.CenterLat, -ViewState.MaxMercatorLat, ViewState.MaxMercatorLat) * Math.PI / 180.0;
            double degPerPixelLat = degPerPixelLon * Math.Cos(latRad);

            // Drag-right (dx>0) → center moves west (−lon). Drag-down (screen dy>0) → center moves north.
            double newLon = v.CenterLon - dxPixels * degPerPixelLon;
            double newLat = v.CenterLat + dyPixels * degPerPixelLat;

            newLon = WrapLon(newLon);
            newLat = Clamp(newLat, -ViewState.MaxMercatorLat, ViewState.MaxMercatorLat);
            return v.WithCenter(newLon, newLat);
        }

        /// <summary>
        /// Applies a screen-space drag to bearing (horizontal) and pitch (vertical). Pitch is clamped to
        /// [0, <paramref name="maxPitch"/>]; bearing wraps to [0,360).
        /// </summary>
        public static ViewState ApplyTilt(ViewState v, double dxPixels, double dyPixels,
                                          double bearingSensitivity, double pitchSensitivity,
                                          double maxPitch)
        {
            double bearing = v.BearingDeg + dxPixels * bearingSensitivity;
            double pitch   = v.PitchDeg   + dyPixels * pitchSensitivity;

            bearing %= 360.0;
            if (bearing < 0) bearing += 360.0;
            pitch = Clamp(pitch, 0.0, maxPitch);
            return v.WithOrientation(bearing, pitch);
        }

        private static double Clamp(double x, double lo, double hi)
            => x < lo ? lo : (x > hi ? hi : x);

        private static double WrapLon(double lon)
        {
            // Wrap into [-180, 180).
            lon = (lon + 180.0) % 360.0;
            if (lon < 0) lon += 360.0;
            return lon - 180.0;
        }
    }
}
