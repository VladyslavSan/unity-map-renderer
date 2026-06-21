using System;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Core.View
{
    /// <summary>
    /// Pure (engine-free, allocation-free) input → <see cref="CameraPropertiesUpdate"/> patch helpers.
    /// Factored out of <c>MapController</c> so the pan/zoom/tilt logic is unit-testable headless — reading
    /// <c>UnityEngine.Input</c> in <c>Update()</c> directly would have zero test coverage.
    ///
    /// <para><b>S50 — patch producers (D2).</b> Each method takes the current
    /// <see cref="CameraProperties"/> and returns a <see cref="CameraPropertiesUpdate"/> patch that sets
    /// only the fields it changes (the rest stay null). This aligns the whole input path with the S45
    /// patch model — <c>MapController</c> merges the returned patch directly, no parallel mutation path.</para>
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
        /// Applies a scroll delta to the camera's zoom. <paramref name="scrollDelta"/> is the raw scroll
        /// (e.g. <c>Input.mouseScrollDelta.y</c>); <paramref name="sensitivity"/> scales it to zoom levels.
        /// Result is clamped to [<paramref name="minZoom"/>, <paramref name="maxZoom"/>].
        /// </summary>
        /// <returns>A patch that sets only <see cref="CameraPropertiesUpdate.Zoom"/>.</returns>
        public static CameraPropertiesUpdate ApplyZoom(CameraProperties current, double scrollDelta,
                                                       double sensitivity, double minZoom, double maxZoom)
        {
            double z = current.Zoom + scrollDelta * sensitivity;
            if (z < minZoom) z = minZoom;
            if (z > maxZoom) z = maxZoom;
            return new CameraPropertiesUpdate { Zoom = z };
        }

        /// <summary>
        /// Converts a screen-space drag (in pixels) to a new center lon/lat. A drag moves the map content
        /// with the cursor: dragging right (positive <paramref name="dxPixels"/>) shifts the center west.
        /// The pixel→degree conversion uses the ground resolution at the current zoom and latitude.
        /// </summary>
        /// <returns>A patch that sets only <see cref="CameraPropertiesUpdate.Lon"/> and
        ///   <see cref="CameraPropertiesUpdate.Lat"/>.</returns>
        public static CameraPropertiesUpdate ApplyPan(CameraProperties current, double dxPixels, double dyPixels)
        {
            double n = Math.Pow(2.0, current.Zoom);
            double worldPixels = TilePixelSize * n;          // pixels spanning 360° of longitude
            double degPerPixelLon = 360.0 / worldPixels;

            // Latitude degrees-per-pixel shrinks toward the poles (Mercator). Use the local cos(lat)
            // factor so vertical drags track the cursor at the current latitude.
            double curLat = current.LookAt.Lat;
            double latRad = Clamp(curLat, -CameraProperties.MaxMercatorLat, CameraProperties.MaxMercatorLat)
                            * Math.PI / 180.0;
            double degPerPixelLat = degPerPixelLon * Math.Cos(latRad);

            // Drag-right (dx>0) → center moves west (−lon). Drag-down (screen dy>0) → center moves north.
            double newLon = current.LookAt.Lon - dxPixels * degPerPixelLon;
            double newLat = curLat + dyPixels * degPerPixelLat;

            newLon = WrapLon(newLon);
            newLat = Clamp(newLat, -CameraProperties.MaxMercatorLat, CameraProperties.MaxMercatorLat);
            return new CameraPropertiesUpdate { Lon = newLon, Lat = newLat };
        }

        /// <summary>
        /// Applies a screen-space drag to bearing (horizontal) and pitch (vertical). Pitch is clamped to
        /// [0, <paramref name="maxPitch"/>]; bearing wraps to [0,360).
        /// </summary>
        /// <returns>A patch that sets only <see cref="CameraPropertiesUpdate.Heading"/> and
        ///   <see cref="CameraPropertiesUpdate.Tilt"/>.</returns>
        public static CameraPropertiesUpdate ApplyTilt(CameraProperties current, double dxPixels, double dyPixels,
                                                       double bearingSensitivity, double pitchSensitivity,
                                                       double maxPitch)
        {
            double bearing = current.Heading + dxPixels * bearingSensitivity;
            double pitch   = current.Tilt    + dyPixels * pitchSensitivity;

            bearing %= 360.0;
            if (bearing < 0) bearing += 360.0;
            pitch = Clamp(pitch, 0.0, maxPitch);
            return new CameraPropertiesUpdate { Heading = bearing, Tilt = pitch };
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
