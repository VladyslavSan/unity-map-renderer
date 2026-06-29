using UnityEngine;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// S72: A dev/authoring surface that two-way binds Zoom / Tilt / Heading sliders to the live
    /// camera. A <b>thin shuttle</b>: it owns no reconcile logic — every frame it hands its serialized
    /// floats to the pure engine-free <see cref="CameraSliderBinding.Reconcile"/>, applies the returned
    /// patch through the existing write seam, and writes the returned display values back into the fields.
    ///
    /// <para><b>Kept out of <c>Controller</c>:</b> S72 is the Editor/authoring input source only; runtime
    /// gestures (mouse+keyboard → S73, touch → S74) are independent sources over the same seam, so this is
    /// a separate component and does not touch the pan/zoom <see cref="Controller"/>.</para>
    ///
    /// <para><b>Play-mode only (decision 6):</b> <see cref="MapView.Camera"/> is constructed only on the
    /// runtime <c>Bootstrapper.Wire</c>/<c>Start</c> path, so in edit mode it is null. <see cref="Update"/>
    /// null-guards <see cref="Map"/>/<see cref="MapView.Camera"/> and no-ops cleanly when unwired.</para>
    /// </summary>
    public sealed class CameraControlPanel : MonoBehaviour
    {
        [Tooltip("The MapView whose live camera these sliders drive (set in the Inspector).")]
        public MapView Map;

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

        // ── Baseline: the last values the panel synced (decision 3), in the same units as the fields.
        // Compared against the fields to tell a user drag from camera self-motion. Float-precision is
        // fine: CameraSliderBinding compares with an epsilon that absorbs the double→float serialization
        // round-trip (the field-vs-baseline guard never compares against the live camera).
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
            // Play-mode only: null-guard until the bootstrapper wires the camera (edit mode has no CameraSystem).
            if (Map == null || Map.Camera == null) return;

            CameraProperties camera = Map.Camera.CurrentProperties;

            // First wired frame: ADOPT the camera — pull all three sliders (and the baseline) to it so the
            // serialized Inspector defaults are NOT misread as user edits and do not stomp the frame-0
            // framing the bootstrapper set. After this, fields == baseline ⇒ the reconcile below is idle.
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
                Map.Camera.Apply(result.Patch, CameraAnimation.Instant);

            // Write the reconciled display values back into the serialized sliders, and store the baseline.
            Zoom      = (float)result.Display.Zoom;
            Tilt      = (float)result.Display.Tilt;
            Heading   = (float)result.Display.Heading;
            _baseline = result.Baseline;

            // Debug readouts — display-only, always follow the camera (never written back). Distance is the
            // metres view of the reconciled zoom; lat/lon are the look-at this panel never moves (pan owns it).
            Distance  = (float)CameraPoseMath.AltitudeForZoom(
                result.Display.Zoom, Map.Camera.ReferenceViewportHeightPx, Map.Camera.VerticalFovDeg);
            Latitude  = camera.LookAt.Latitude;
            Longitude = camera.LookAt.Longitude;
        }
    }
}
