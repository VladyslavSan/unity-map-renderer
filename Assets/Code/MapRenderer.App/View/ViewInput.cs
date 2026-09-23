using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Camera;

namespace MapRenderer.App.View
{
    /// <summary>
    /// Engine-free, allocation-free input → <see cref="CameraPropertiesUpdate"/> patch producers, so the gesture
    /// logic is unit-testable headless. Each patch sets only the fields it changes; the rest stay null.
    /// Zoom and pan pin a ground point under the cursor through an <see cref="IProjection"/>: zoom keeps the
    /// point under the cursor and clamps to [minZoom, maxZoom]; pan keeps the drag-start ground point (captured
    /// once by the caller) under the cursor. Heading and tilt patches are independent of each other.
    /// </summary>
    public static class ViewInput
    {
        /// <summary>
        /// The single device-agnostic dispatch: maps a <see cref="GestureIntent"/> to a
        /// <see cref="CameraPropertiesUpdate"/> patch, with the per-frame camera, viewport and projection from
        /// <paramref name="view"/>. Every input source calls it, so the mapping lives here, not in the source.
        /// </summary>
        public static CameraPropertiesUpdate Apply(in GestureIntent intent, in ViewContext view)
            => intent.Kind switch
            {
                GestureKind.ZoomAtAnchor => ApplyZoom(view.Projection, view.Camera,
                                                      intent.AnchorPx, view.ViewportPx,
                                                      intent.ZoomDelta, 1.0,
                                                      intent.MinZoom, intent.MaxZoom),
                GestureKind.PanToAnchor  => ApplyPan(view.Projection, view.Camera,
                                                     intent.GrabbedGround, intent.CursorPx,
                                                     view.ViewportPx),
                GestureKind.HeadingBy    => ApplyHeadingDelta(view.Camera, intent.HeadingDeltaDeg),
                GestureKind.TiltBy       => ApplyTiltDelta(view.Camera, intent.TiltDeltaDeg, intent.MaxPitch),
                _                        => default,
            };

        /// <summary>
        /// Heading-only patch: rotates bearing by <paramref name="headingDeltaDeg"/> degrees, re-applying
        /// the [0, 360) <see cref="MapRenderer.Core.Geo.AngleConstraint.Wrap"/> constraint from the
        /// <c>ConstrainedAngle</c> model. <b>Tilt is not set</b> (stays <c>null</c> on the patch).
        /// </summary>
        /// <returns>A patch with only <see cref="CameraPropertiesUpdate.Heading"/> set.</returns>
        public static CameraPropertiesUpdate ApplyHeadingDelta(in CameraProperties current, double headingDeltaDeg)
            => new CameraPropertiesUpdate
            {
                Heading = (current.Heading + Angle.FromDegrees(headingDeltaDeg)).Degrees,
            };

        /// <summary>
        /// Tilt-only patch: pitches the camera by <paramref name="tiltDeltaDeg"/> degrees, clamped to
        /// [0, <paramref name="maxPitch"/>] via <see cref="ConstrainedAngle.Clamped"/>. <b>Heading is
        /// not set</b> (stays <c>null</c> on the patch). The runtime <paramref name="maxPitch"/> is a
        /// distinct, potentially narrower limit than the [0, 90] type invariant of the Tilt preset.
        /// </summary>
        /// <returns>A patch with only <see cref="CameraPropertiesUpdate.Tilt"/> set.</returns>
        public static CameraPropertiesUpdate ApplyTiltDelta(in CameraProperties current, double tiltDeltaDeg, double maxPitch)
            => new CameraPropertiesUpdate
            {
                Tilt = ConstrainedAngle.Clamped(current.Tilt.Degrees + tiltDeltaDeg, 0.0, maxPitch).Degrees,
            };

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

            // Bound the look-at to the finite world sheet (identity on a cyclic/globe projection) instead of
            // wrapping longitude — the world is a finite atlas sheet, not an endlessly repeating strip.
            CameraProperties camClamp = new CameraProperties(
                lookAtAfter, zNew, current.Heading.Degrees, current.Tilt.Degrees, current.VerticalFovDeg);
            GeoCoordinate3D bounded = projection.ClampLookAtToWorld(viewportPx, in camClamp);

            return new CameraPropertiesUpdate
            {
                Zoom      = zNew,
                Longitude = bounded.Longitude,
                // ClampValidLatitude stays the outer pole guard: redundant on the finite sheet (already
                // bounded above), load-bearing on the cyclic globe (ClampLookAtToWorld is the identity there).
                Latitude  = projection.ClampValidLatitude(bounded.Latitude)
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

            // Bound the look-at to the finite world sheet (identity on a cyclic/globe projection) instead of
            // wrapping longitude — the world is a finite atlas sheet, not an endlessly repeating strip.
            CameraProperties camClamp = new CameraProperties(
                lookAtAfter, current.Zoom, current.Heading.Degrees, current.Tilt.Degrees, current.VerticalFovDeg);
            GeoCoordinate3D bounded = projection.ClampLookAtToWorld(viewportPx, in camClamp);

            return new CameraPropertiesUpdate
            {
                Longitude = bounded.Longitude,
                Latitude  = projection.ClampValidLatitude(bounded.Latitude)
            };
        }
    }
}
