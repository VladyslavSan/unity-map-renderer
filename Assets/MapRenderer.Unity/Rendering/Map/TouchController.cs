// Unity touch source (S74): thin EnhancedTouch adapter over the S73 GestureIntent seam.
// Sits alongside Controller.cs (the desktop source). Delegates ALL disambiguation to
// TouchGestureRecognizer (Core) and ALL camera math to ViewInput.Apply — re-implements neither.
//
// T-TOUCHSTACK guard: see TouchInputStackTests.cs (mirrors MapControllerInputTests).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using Unity.Mathematics;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

// Alias the Core TouchPhase to avoid CS0104 ambiguity with UnityEngine.InputSystem.TouchPhase.
using CoreTouchPhase = MapRenderer.Core.View.TouchPhase;
// Alias EnhancedTouch.Touch to avoid CS0104 ambiguity with UnityEngine.Touch.
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// S74: thin Unity EnhancedTouch adapter — the touch sibling of <see cref="Controller"/>.
    ///
    /// <para>Reads <see cref="Touch.activeTouches"/> each frame, converts them to engine-free
    /// <see cref="TouchSample"/>s, feeds them to <see cref="TouchGestureRecognizer.Recognize"/>,
    /// and folds each emitted <see cref="GestureIntent"/> through
    /// <see cref="ViewInput.Apply(in GestureIntent, in ViewContext)"/> into one
    /// <see cref="CameraPropertiesUpdate"/>, then calls
    /// <see cref="MapCamera.Apply(CameraPropertiesUpdate)"/>.
    ///
    /// <para><b>Input backend: new Input System / EnhancedTouch</b>. Uses
    /// <see cref="Touch.activeTouches"/> exclusively — zero legacy UnityEngine.Input API.
    /// <see cref="EnhancedTouchSupport.Enable()"/> is called in <see cref="OnEnable"/> —
    /// without this, <c>Touch.activeTouches</c> is always empty and touch silently does nothing.</para>
    ///
    /// <para><b>Zero seam edits (T-NOSEAM):</b> this class references
    /// <see cref="GestureIntent"/>, <see cref="ViewInput.Apply"/>, and <see cref="ViewContext"/>
    /// verbatim; it does not redefine them, add intent kinds, or re-implement gesture math.</para>
    ///
    /// <para>Allocation-free <see cref="Update"/>: reused <c>List&lt;T&gt;</c> fields,
    /// no LINQ, no closures.</para>
    /// </summary>
    public sealed class TouchController : MonoBehaviour
    {
        // ── References (set by Bootstrapper.Wire) ────────────────────────────────────────────────
        [Tooltip("The MapView this touch controller drives (set by Bootstrapper.Wire at runtime).")]
        public MapViewComponent Map;

        [Tooltip("The camera providing the live viewport (set by Bootstrapper.Wire at runtime).")]
        public new Camera camera;

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

        // ── Disambiguation thresholds (LOGICAL px — constant physical size, S92 touch-DPI closure) ────
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
            // The seam is fed LOGICAL px (Update divides by DevicePixelRatio), so thresholds and pitch
            // sensitivity are already density-independent — normalization happens ONCE, at the position basis,
            // mirroring the mouse seam (S92 touch-DPI closure). No per-density threshold scaling here.
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

            // Build per-frame view context (same pattern as Controller.Update). The interaction seam runs in
            // LOGICAL pixels (S92 D3): divide the viewport AND every touch contact by DPR so anchors and the
            // render camera (MapCamera D1 frames vp/DPR) share one basis — else pinch/pan drifts off the
            // fingers on a high-DPI panel. Density normalization lives ONLY here now; the recognizer's
            // thresholds are plain logical px, mirroring the mouse seam. Guard ≤0 → 1.
            double dpr = Map.Config.DevicePixelRatio > 0.0 ? Map.Config.DevicePixelRatio : 1.0;
            double2 vp = new double2(
                camera != null ? camera.pixelWidth  : Screen.width,
                camera != null ? camera.pixelHeight : Screen.height) / dpr;

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
                    PositionPx = new double2(pos.x, pos.y) / dpr, // logical px (S92 D3 seam)
                    Phase      = corePh,
                });
            }

            // Recognize gestures (engine-free brain).
            _recognizer.Recognize(_samples, in view, _intents);

            if (_intents.Count == 0) return;

            // Fold each intent through ViewInput.Apply into one CameraPropertiesUpdate.
            // The fold is a non-colliding field union: each intent kind touches distinct fields
            // (Pan→Lon/Lat; ZoomAtAnchor→Zoom/Lon/Lat; HeadingBy→Heading; TiltBy→Tilt).
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
