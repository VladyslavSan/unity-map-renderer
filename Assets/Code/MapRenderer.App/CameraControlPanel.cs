using UnityEngine;
using MapRenderer.App.View.Camera;
using MapRenderer.Core.Geo;

using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.App
{
    /// <summary>
    /// A dev/authoring surface that two-way binds Zoom / Tilt / Heading sliders to the live camera. It owns no
    /// reconcile logic: each frame it hands its fields to <see cref="CameraSliderBinding.Reconcile"/>, applies
    /// the returned patch through the camera write seam, and writes the display values back into the fields.
    /// It is separate from <see cref="Controller"/> because runtime gestures are independent sources over the
    /// same seam. Play-mode only: <see cref="Update"/> no-ops while <see cref="MapView.Camera"/> is unwired.
    /// </summary>
    public sealed class CameraControlPanel : MonoBehaviour
    {
        // Wired at runtime by MapHost — a runtime reference, not a serialized Inspector field.
        public MapViewComponent Map { get; set; }

        [Header("Camera (two-way bound to the live camera)")]
        [Range(0f, 24f)]
        [Tooltip("Fractional zoom level (0 = world, ~14 = streets). Two-way bound to the live camera.")]
        public float Zoom = 14f;

        [Range(0f, 90f)]
        [Tooltip("Camera tilt in degrees (0 = top-down, 90 = horizon). Two-way bound; constrained by the model.")]
        public float Tilt;

        [Range(0f, 360f)]
        [Tooltip("Camera heading in degrees CW from north. Two-way bound; wrapped by the model.")]
        public float Heading;

        [Header("Debug readouts (live — overwritten each frame, edits don't stick)")]
        [Tooltip("Camera distance in metres, derived from zoom. Read-only readout.")]
        public float Distance;

        [Tooltip("Look-at latitude (WGS-84 degrees). Read-only readout.")]
        public double Latitude;

        [Tooltip("Look-at longitude (WGS-84 degrees). Read-only readout.")]
        public double Longitude;

        // The last synced values; a field that differs from them is a user drag, not camera motion.
        // Float precision suffices: the reconcile epsilon absorbs the double→float round-trip.
        private SliderValues _baseline;
        private bool         _baselineInitialised;

        private void Update() => Tick();

        /// <summary>
        /// One reconcile pass. <c>internal</c> so EditMode tests can drive it deterministically (the
        /// MonoBehaviour game loop does not run under the EditMode test runner). Production calls it from
        /// <see cref="Update"/>; it is not part of the public surface.
        /// </summary>
        internal void Tick()
        {
            // Play-mode only: null-guard until the bootstrapper wires the camera (edit mode has no MapCamera).
            if (Map == null || Map.Camera == null) return;

            CameraProperties camera = Map.Camera.CurrentProperties;

            // First wired frame: adopt the camera into the sliders and baseline, so the Inspector
            // defaults are not misread as user edits that stomp the bootstrapper's frame-0 framing.
            if (!_baselineInitialised)
            {
                Zoom    = (float)camera.Zoom;
                Tilt    = (float)camera.Tilt.Degrees;
                Heading = (float)camera.Heading.Degrees;
                _baseline = new SliderValues
                {
                    Zoom = camera.Zoom, Tilt = camera.Tilt.Degrees, Heading = camera.Heading.Degrees,
                };
                _baselineInitialised = true;
            }

            var fields = new SliderValues { Zoom = Zoom, Tilt = Tilt, Heading = Heading };

            ReconcileResult result = CameraSliderBinding.Reconcile(in fields, in _baseline, in camera);

            if (result.HasPatch)
                Map.Camera.Apply(result.Patch);

            // Write the reconciled display values back into the serialized sliders, and store the baseline.
            Zoom      = (float)result.Display.Zoom;
            Tilt      = (float)result.Display.Tilt;
            Heading   = (float)result.Display.Heading;
            _baseline = result.Baseline;

            // Debug readouts — display-only, always follow the camera (never written back). Distance is the
            // metres view of the reconciled zoom; lat/lon are the look-at this panel never moves (pan owns it).
            Distance  = (float)CameraPoseMath.AltitudeForZoom(
                result.Display.Zoom, Map.Camera.ViewportPx.y, Map.Camera.CurrentProperties.VerticalFovDeg);
            Latitude  = camera.LookAt.Latitude;
            Longitude = camera.LookAt.Longitude;
        }
    }
}
