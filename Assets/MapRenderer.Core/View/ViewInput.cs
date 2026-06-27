using Unity.Mathematics;
using MapRenderer.Core.Geo;
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
    /// <para><b>S63 — interaction-point-aware (D4).</b> All gesture methods take an <see cref="IProjection"/>
    /// and operate on absolute screen positions (cursor + grabbed ground point), so the ground point under
    /// the cursor is pinned during the gesture. The projection layer owns all Mercator constants.</para>
    ///
    /// Conventions:
    ///   - <b>Zoom</b> to cursor: the earth point under the cursor stays pinned after zooming.
    ///     Patch carries Zoom + Longitude + Latitude (the new look-at that satisfies the pin invariant).
    ///     Zoom is clamped to [minZoom, maxZoom].
    ///   - <b>Pan</b> (anchored): the grabbed ground point at drag-start stays glued to the cursor.
    ///     Returns a Longitude + Latitude patch. The grabbed point is captured once by the caller
    ///     (<c>projection.ScreenToGround(P_start, vp, cam)</c>) and supplied every frame.
    ///   - <b>Tilt/bearing</b>: a screen-space drag delta maps to pitch (vertical) and bearing (horizontal)
    ///     in degrees, each clamped to a sane range. Projection-agnostic.
    /// </summary>
    public static class ViewInput
    {
        /// <summary>
        /// Zoom-to-cursor: applies a scroll delta so the earth point under <paramref name="cursorPx"/>
        /// remains under <paramref name="cursorPx"/> after the zoom.
        /// Screen convention: +x right, +y up, origin bottom-left.
        /// </summary>
        /// <returns>A patch that sets <see cref="CameraPropertiesUpdate.Zoom"/>,
        /// <see cref="CameraPropertiesUpdate.Longitude"/>, and <see cref="CameraPropertiesUpdate.Latitude"/>.</returns>
        public static CameraPropertiesUpdate ApplyZoom(IProjection projection, in CameraProperties current,
            double2 cursorPx, double2 viewportPx, double scrollDelta, double sensitivity,
            double minZoom, double maxZoom)
        {
            double zNew = current.Zoom + scrollDelta * sensitivity;
            if (zNew < minZoom) zNew = minZoom;
            if (zNew > maxZoom) zNew = maxZoom;

            // Capture the earth point under the cursor at the current zoom.
            GeoCoordinate3D groundUnderP = projection.ScreenToGround(cursorPx, viewportPx, in current);

            // Build a trial camera at the new zoom (same look-at and orientation).
            CameraProperties camTrial = new CameraProperties(
                current.LookAt, zNew, current.Heading.Degrees, current.Tilt.Degrees);

            // Find where the captured ground point appears in the trial camera.
            double2 Pq = projection.GroundToScreen(in groundUnderP, viewportPx, in camTrial);

            // Solve the new look-at that keeps groundUnderP under cursorPx:
            // S(centrePx + (Pq − cursor), camTrial) gives the new look-at exactly.
            GeoCoordinate3D lookAtAfter = projection.ScreenToGround(
                viewportPx * 0.5 + (Pq - cursorPx), viewportPx, in camTrial);

            return new CameraPropertiesUpdate
            {
                Zoom      = zNew,
                Longitude = WrapLon(lookAtAfter.Longitude),
                Latitude  = projection.ClampValidLatitude(lookAtAfter.Latitude)
            };
        }

        /// <summary>
        /// Anchored pan: keeps <paramref name="grabbedGround"/> (captured once at drag-start by the caller)
        /// pinned under <paramref name="cursorPx"/>. The camera bearing is accounted for by the projection.
        /// Screen convention: +x right, +y up, origin bottom-left.
        /// </summary>
        /// <returns>A patch that sets <see cref="CameraPropertiesUpdate.Longitude"/> and
        ///   <see cref="CameraPropertiesUpdate.Latitude"/>.</returns>
        public static CameraPropertiesUpdate ApplyPan(IProjection projection, in CameraProperties current,
            GeoCoordinate3D grabbedGround, double2 cursorPx, double2 viewportPx)
        {
            // Find where the grabbed ground currently projects on screen.
            double2 Pq = projection.GroundToScreen(in grabbedGround, viewportPx, in current);

            // Solve the new look-at that places the grabbed ground under cursorPx.
            GeoCoordinate3D lookAtAfter = projection.ScreenToGround(
                viewportPx * 0.5 + (Pq - cursorPx), viewportPx, in current);

            return new CameraPropertiesUpdate
            {
                Longitude = WrapLon(lookAtAfter.Longitude),
                Latitude  = projection.ClampValidLatitude(lookAtAfter.Latitude)
            };
        }

        /// <summary>
        /// Applies a screen-space drag to bearing (horizontal) and pitch (vertical). Pitch is clamped to
        /// [0, <paramref name="maxPitch"/>]; bearing wraps to [0,360).
        /// </summary>
        /// <returns>A patch that sets only <see cref="CameraPropertiesUpdate.Heading"/> and
        ///   <see cref="CameraPropertiesUpdate.Tilt"/>.</returns>
        public static CameraPropertiesUpdate ApplyTilt(in CameraProperties current, double dxPixels, double dyPixels,
                                                       double bearingSensitivity, double pitchSensitivity,
                                                       double maxPitch)
        {
            // Bearing: the ConstrainedAngle + Angle operator re-applies the [0,360) Wrap constraint.
            double bearing = (current.Heading + Angle.FromDegrees(dxPixels * bearingSensitivity)).Degrees;

            // Pitch: use a runtime Clamped(lo=0, hi=maxPitch) — distinct from the [0,90] Tilt type
            // preset; maxPitch may be narrower (e.g. 60°) and must not be silently widened to 90°.
            double pitch = ConstrainedAngle.Clamped(
                current.Tilt.Degrees + dyPixels * pitchSensitivity, 0.0, maxPitch).Degrees;

            return new CameraPropertiesUpdate { Heading = bearing, Tilt = pitch };
        }

        private static double WrapLon(double lon)
        {
            // Wrap into [-180, 180).
            lon = (lon + 180.0) % 360.0;
            if (lon < 0) lon += 360.0;
            return lon - 180.0;
        }
    }
}
