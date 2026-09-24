// Unity touch source: thin EnhancedTouch adapter over the GestureIntent seam. Delegates all disambiguation
// to TouchGestureRecognizer and all camera math to ViewInput.Apply.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using Unity.Mathematics;
using MapRenderer.App.View;
using MapRenderer.Unity.View;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View.Camera;

// Alias the Core TouchPhase to avoid CS0104 ambiguity with UnityEngine.InputSystem.TouchPhase.
using CoreTouchPhase = MapRenderer.App.View.TouchPhase;
// Alias EnhancedTouch.Touch to avoid CS0104 ambiguity with UnityEngine.Touch.
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.App
{
    /// <summary>
    /// A thin EnhancedTouch adapter, the touch sibling of <see cref="Controller"/>: each frame it turns
    /// <see cref="Touch.activeTouches"/> into <see cref="TouchSample"/>s, runs <see cref="TouchGestureRecognizer.Recognize"/>,
    /// folds the intents through <see cref="ViewInput.Apply(in GestureIntent, in ViewContext)"/> into one patch for
    /// <see cref="MapCamera.Apply(CameraPropertiesUpdate)"/>, and allocates nothing in steady state.
    /// <see cref="OnEnable"/> enables <see cref="EnhancedTouchSupport"/>; without it, no touches arrive.
    /// </summary>
    public sealed class TouchController : MonoBehaviour
    {
        // ── References (set at runtime during wiring) ────────────────────────────────────────────────
        // Wired by MapHost on Start, so not serialized: an Inspector slot would only show a dead value.
        public MapViewComponent Map { get; set; }
        public Camera camera { get; set; }

        // ── Sensitivity ───────────────────────────────────────────────────────────────────────────
        [Header("Sensitivity")]
        [Tooltip("log2(d/d0) multiplier → zoom-level delta per pinch unit.")]
        public float ZoomSensitivity = 1.0f;

        [Tooltip("Degrees of heading change per degree of inter-finger-angle change.")]
        public float BearingSensitivity = 0.5f;

        [Tooltip("Degrees of tilt change per LOGICAL pixel of vertical centroid movement (density-independent).")]
        public float PitchSensitivity = 0.2f;

        // ── Clamps ────────────────────────────────────────────────────────────────────────────────
        [Header("Clamps")]
        public float MaxPitch = 60f;
        public float MinZoom  = 0f;
        public float MaxZoom  = 22f;

        [Tooltip("Breathing-room margin (zoom levels) for the cyclic-globe min-zoom floor; ignored on the " +
                 "finite Mercator sheet, which fills the viewport at margin 0. Mirrors Controller.MinZoomMargin.")]
        public float MinZoomMargin = 0.5f;

        // ── Disambiguation thresholds (LOGICAL px — constant physical size) ──────────────────────────
        [Header("Disambiguation thresholds (logical px)")]
        [Tooltip("Inter-finger distance change (logical px) needed to classify as a pinch.")]
        public float PinchDistanceThresholdPx = 10f;

        [Tooltip("Inter-finger angle change (degrees) needed to classify as a twist.")]
        public float TwistAngleThresholdDeg = 5f;

        [Tooltip("Vertical centroid displacement (logical px) needed to classify as a tilt drag.")]
        public float TiltCentroidThresholdPx = 10f;

        // ── Internal state ────────────────────────────────────────────────────────────────────────
        private TouchGestureRecognizer   _recognizer;
        private readonly List<TouchSample>    _samples = new List<TouchSample>();
        private readonly List<GestureIntent>  _intents = new List<GestureIntent>();

        // The MinZoom floor last baked into _recognizer; NaN forces the first-frame build. A rebuild allocates,
        // so Update rebuilds only when the floor moves (viewport resize, projection swap).
        private double _recognizerMinZoomFloor = double.NaN;

        // ── Lifecycle ─────────────────────────────────────────────────────────────────────────────

        private void OnEnable()
        {
            EnhancedTouchSupport.Enable();
            RebuildRecognizer();
        }

        private void OnDisable()
        {
            EnhancedTouchSupport.Disable();
        }

        // Rebuild the recognizer from the serialized config (called on Enable so Inspector edits
        // during play are picked up on next enable cycle).
        private void RebuildRecognizer()
        {
            // The seam is fed LOGICAL px, so thresholds and pitch sensitivity are already
            // density-independent: normalization happens ONCE, at the position basis.
            var cfg = new TouchGestureConfig
            {
                ZoomSensitivity            = ZoomSensitivity,
                BearingSensitivity         = BearingSensitivity,
                PitchSensitivity           = PitchSensitivity,
                MinZoom                    = MinZoom,
                MaxZoom                    = MaxZoom,
                MaxPitch                   = MaxPitch,
                PinchDistanceThresholdPx   = PinchDistanceThresholdPx,
                TwistAngleThresholdDeg     = TwistAngleThresholdDeg,
                TiltCentroidThresholdPx    = TiltCentroidThresholdPx,
            };
            _recognizer = new TouchGestureRecognizer(cfg);
        }

        // ── Update: sample → recognize → fold → apply ────────────────────────────────────────────

        private void Update()
        {
            if (Map == null || Map.Camera == null) return;

            // Non-local invariant: as in Controller.Update, the viewport and every contact convert to LOGICAL px
            // via the DeviceScaling.DeviceToLogicalPx that MapCamera.ViewportLogicalPx frames from; otherwise
            // pinch and pan drift off the fingers on a high-DPI panel. This component's camera may be null,
            // hence the Screen.width/height fallback.
            double dpr = Map.Config.DevicePixelRatio;
            double2 vp = DeviceScaling.DeviceToLogicalPx(new double2(
                camera != null ? camera.pixelWidth  : Screen.width,
                camera != null ? camera.pixelHeight : Screen.height), dpr);

            // The pinch floor tracks the live viewport and projection, as the desktop Controller's does;
            // otherwise touch pinch floors at the stale value baked in OnEnable.
            double floor = CameraPoseMath.MinZoomFloor(Map.Camera.Projection, vp.x, vp.y, MinZoomMargin);
            if (floor != _recognizerMinZoomFloor)
            {
                _recognizerMinZoomFloor = floor;
                MinZoom = (float)floor;
                RebuildRecognizer();
            }

            var view = new ViewContext
            {
                Camera     = Map.Camera.CurrentProperties,
                ViewportPx = vp,
                Projection = Map.Camera.Projection,
            };

            // Convert EnhancedTouch contacts to engine-free TouchSamples.
            _samples.Clear();
            var activeTouches = Touch.activeTouches;
            for (int i = 0; i < activeTouches.Count; i++)
            {
                Touch t = activeTouches[i];
                CoreTouchPhase corePh = MapCorePhase(t.phase);
                if (corePh < 0) continue; // skip unknown phases

                var pos = t.screenPosition; // Vector2, device (physical) px
                _samples.Add(new TouchSample
                {
                    FingerId   = t.finger.index,
                    PositionPx = DeviceScaling.DeviceToLogicalPx(new double2(pos.x, pos.y), dpr), // logical px
                    Phase      = corePh,
                });
            }

            // Recognize gestures (engine-free brain).
            _recognizer.Recognize(_samples, in view, _intents);

            if (_intents.Count == 0) return;

            // One frame emits Pan alone, TiltBy alone, or ZoomAtAnchor (Zoom/Lon/Lat) with HeadingBy, so no
            // field is written twice.
            CameraPropertiesUpdate patch = default;
            for (int i = 0; i < _intents.Count; i++)
            {
                CameraPropertiesUpdate p = ViewInput.Apply(_intents[i], view);
                // Merge non-null fields (last writer wins per field; gestures are disjoint).
                if (p.Longitude.HasValue) patch.Longitude = p.Longitude;
                if (p.Latitude.HasValue)  patch.Latitude  = p.Latitude;
                if (p.Zoom.HasValue)      patch.Zoom      = p.Zoom;
                if (p.Heading.HasValue)   patch.Heading   = p.Heading;
                if (p.Tilt.HasValue)      patch.Tilt      = p.Tilt;
            }

            if (!patch.IsEmpty)
                Map.Camera.Apply(patch);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Maps an <see cref="UnityEngine.InputSystem.TouchPhase"/> to the engine-free
        /// Core <see cref="CoreTouchPhase"/>. Returns <c>-1</c> (cast to enum) for phases that
        /// should be skipped (None).
        /// </summary>
        private static CoreTouchPhase MapCorePhase(UnityEngine.InputSystem.TouchPhase phase)
        {
            switch (phase)
            {
                case UnityEngine.InputSystem.TouchPhase.Began:
                    return CoreTouchPhase.Began;
                case UnityEngine.InputSystem.TouchPhase.Moved:
                case UnityEngine.InputSystem.TouchPhase.Stationary:
                    return CoreTouchPhase.Moved;
                case UnityEngine.InputSystem.TouchPhase.Ended:
                case UnityEngine.InputSystem.TouchPhase.Canceled:
                    return CoreTouchPhase.Ended;
                default:
                    // None or unknown — signal to skip.
                    return (CoreTouchPhase)(-1);
            }
        }
    }
}
