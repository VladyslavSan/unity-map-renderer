using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// S45: Thin input → <see cref="CameraPropertiesUpdate"/> patch translator. Supersedes the
    /// S42 god-MonoBehaviour: all pose math has moved to <see cref="CameraPoseMath"/> (Core) and
    /// <see cref="MapCamera"/> (Unity sync). This class reads <c>Mouse.current</c> /
    /// <c>Keyboard.current</c> and calls <see cref="View.Camera"/>.Apply with instant patches.
    ///
    /// <para><b>Input backend: new Input System (<c>UnityEngine.InputSystem</c>).</b>
    ///   Reads <c>Mouse.current</c> and <c>Keyboard.current</c> directly (no legacy
    ///   <c>UnityEngine.Input</c>). Controls: left-drag = pan, scroll = zoom, right-drag = tilt/bearing,
    ///   +/=/Q = zoom in, −/E = zoom out.</para>
    ///
    /// <para><b>Scroll normalization (S42 D4 preserved):</b> <see cref="WheelNotchUnits"/> = 120 so
    ///   one physical wheel notch ≈ 1 normalized unit.</para>
    ///
    /// <para><b>S63 — Interaction-point-aware gestures.</b> Zoom uses the cursor as the anchor point
    ///   (zoom-to-cursor); pan tracks the grabbed ground point that was under the cursor at drag-start
    ///   (anchored pan). Both delegate to <see cref="ViewInput"/> which carries zero Mercator constants.
    ///   Screen convention: +x right, +y up, origin bottom-left (Unity mouse position) — the former
    ///   <c>−delta.y</c> negation hack for pan is removed; tilt keeps its <c>−delta.y</c> unchanged.</para>
    ///
    /// <para><b>D5 — Ordering:</b> this Update only queues patches on the camera system. The actual
    ///   camera advance + tile loop runs in <see cref="View.Update"/> (via
    ///   <see cref="View.UpdateFrame"/>). Unity does NOT guarantee the ordering of sibling
    ///   MonoBehaviour Updates, but the result is still correct: patches set the instant-path
    ///   current props which MapView.Advance picks up either this frame or next. For deterministic
    ///   ordering with no per-frame latency, <see cref="Map"/> should wire the camera via
    ///   <see cref="View.SetCamera"/> and call <see cref="ApplyCameraTransform"/> from within a
    ///   controlled context — used by tests.</para>
    ///
    /// <para><b>Camera-transform helpers:</b>
    ///   <see cref="ApplyCameraTransform(CameraProperties)"/> and <see cref="AltitudeForZoom"/> are
    ///   thin delegates to the Core pose math, used by <c>MapRoot</c> frame-0 framing and by
    ///   <c>CameraTransformTests</c>.</para>
    ///
    /// <para>Allocation-free <see cref="Update"/>: struct patches, no LINQ, no closures.</para>
    /// </summary>
    public sealed class Controller : MonoBehaviour
    {
        [Tooltip("The MapView this controller drives (set by MapRoot.Wire at runtime).")]
        public MapView Map;

        [Tooltip("The camera this controller positions (set by MapRoot.Wire at runtime).")]
        public Camera Camera;

        // ── Sensitivity (new Input System calibration) ────────────────────────────────────────────
        [Header("Sensitivity (new Input System — see class doc for calibration notes)")]
        [Tooltip("Zoom sensitivity. One normalized scroll unit (= one wheel notch via WheelNotchUnits) " +
                 "is multiplied by this to get zoom delta. Default 0.25 → 0.25 zoom levels per notch.")]
        public float ZoomSensitivity = 0.25f;

        [Tooltip("Keyboard zoom step in zoom-levels per second. Applied via Time.deltaTime.")]
        public float KeyboardZoomStep = 2.0f;

        [Tooltip("Bearing sensitivity (raw px/frame → degrees). 0.3 matches the legacy feel.")]
        public float BearingSensitivity = 0.3f;

        [Tooltip("Pitch sensitivity (raw px/frame → degrees). 0.3 matches the legacy feel.")]
        public float PitchSensitivity = 0.3f;

        public float MaxPitch = 60f;

        [Header("Zoom clamp")] public float MinZoom = 0f;
        public                        float MaxZoom = 22f;

        // ── Camera framing (bridge — altitude derived from zoom, S42 D2) ──────────────────────────
        [Header("Camera framing — altitude derived from zoom (S42 D2)")]
        /// <summary>
        /// Device-independent scroll magnitude per physical wheel notch (new Input System).
        /// Mouse.current.scroll.y delivers ±120 per notch; dividing by this normalizes to ≈±1.0/notch.
        /// Trackpad delivers continuous fractional values that scale proportionally after normalization.
        /// </summary>
        public const float WheelNotchUnits = 120f;

        [Tooltip("Deterministic viewport height fed to the altitude formula (not Camera.pixelHeight, " +
                 "which is non-reproducible in headless/test mode).")]
        public float ReferenceViewportHeightPx = 1080f;

        [Tooltip("Vertical field-of-view for the perspective camera (degrees).")]
        public float VerticalFovDeg = 60f;

        [Tooltip("Optional multiplier on the derived altitude (default 1).")]
        public float AltitudeMultiplier = 1f;

        // ── Pan drag state (S63 anchored pan) ─────────────────────────────────────────────────────
        private GeoCoordinate3D _grabbedGround;
        private bool            _dragging;

        // ── Update: produce CameraPropertiesUpdate patches ────────────────────────────────────────

        private void Update()
        {
            if (Map == null || Map.Camera == null) return;

            CameraPropertiesUpdate patch     = default;
            bool                   anyChange = false;

            // Resolve the active projection and viewport once per frame.
            IProjection projection = Map.Camera.Projection;
            double2     vp         = new double2(
                Camera != null ? Camera.pixelWidth  : Screen.width,
                Camera != null ? Camera.pixelHeight : Screen.height);

            // ── Zoom (scroll wheel / trackpad) ───────────────────────────────────────────────────
            // Null-guard Mouse.current (absent in headless / test builds — no NRE).
            var mouse = Mouse.current;
            if (mouse != null)
            {
                // Read cursor position once (used by both scroll-zoom and pan).
                Vector2 mousePos = mouse.position.ReadValue();
                double2 cursor   = new double2(mousePos.x, mousePos.y);

                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f)
                {
                    // Normalize: divide raw scroll by WheelNotchUnits so one wheel notch ≈ 1.0.
                    float normalizedScroll = scroll / WheelNotchUnits;
                    CameraPropertiesUpdate z = ViewInput.ApplyZoom(
                        projection, Map.Camera.CurrentProperties, cursor, vp,
                        normalizedScroll, ZoomSensitivity, MinZoom, MaxZoom);
                    patch.Zoom      = z.Zoom;
                    patch.Longitude = z.Longitude;
                    patch.Latitude  = z.Latitude;
                    anyChange       = true;
                }

                // ── Pan (left-drag, S63 anchored pan) ────────────────────────────────────────────
                // Capture the grabbed ground point on the first frame of the press; hold it for the
                // duration of the drag. The anchored ApplyPan keeps the grabbed point glued to the
                // cursor — no delta, no negation hack. Screen convention +y-up is already correct here.
                if (mouse.leftButton.isPressed)
                {
                    if (!_dragging)
                    {
                        // First frame: capture the earth point under the cursor.
                        _grabbedGround = projection.ScreenToGround(cursor, vp, Map.Camera.CurrentProperties);
                        _dragging      = true;
                    }

                    CameraPropertiesUpdate pan = ViewInput.ApplyPan(
                        projection, Map.Camera.CurrentProperties, _grabbedGround, cursor, vp);
                    patch.Longitude = pan.Longitude;
                    patch.Latitude  = pan.Latitude;
                    anyChange       = true;
                }
                else
                {
                    _dragging = false;
                }

                // ── Tilt / bearing (right-drag) ─────────────────────────────────────────────────
                // Sign convention: Mouse.current.delta.y is +up in the new Input System. Negating
                // delta.y makes drag-UP decrease pitch (camera tilts toward overhead). This matches
                // the S50 D6 pinned tilt-Y tests in CameraPropertiesTests. Tilt is NOT reworked in
                // S63 (no interaction-point anchor needed); only the 'in CameraProperties' overload
                // is consumed here. Do NOT remove the negation — it is a separate, pinned sign choice.
                if (mouse.rightButton.isPressed)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    if (delta.x != 0f || delta.y != 0f)
                    {
                        CameraPropertiesUpdate tilt = ViewInput.ApplyTilt(
                            Map.Camera.CurrentProperties, delta.x, -delta.y,
                            BearingSensitivity, PitchSensitivity, MaxPitch);
                        patch.Heading = tilt.Heading;
                        patch.Tilt    = tilt.Tilt;
                        anyChange     = true;
                    }
                }
            }

            // ── Keyboard zoom (+/= / Q → zoom in; − / E → zoom out) ─────────────────────────────
            // Null-guarded separately from Mouse.current (each device can be absent independently).
            // Routes through ApplyZoom with the screen centre as the interaction point → exact
            // centre-zoom (today's keyboard feel). The lon/lat in the returned patch equal the current
            // look-at, so merging them into patch is safe.
            var kb = Keyboard.current;
            if (kb != null)
            {
                float kbStep  = KeyboardZoomStep * Time.deltaTime;
                bool  zoomIn  = kb.equalsKey.isPressed || kb.numpadPlusKey.isPressed  || kb.qKey.isPressed;
                bool  zoomOut = kb.minusKey.isPressed  || kb.numpadMinusKey.isPressed || kb.eKey.isPressed;
                if (zoomIn || zoomOut)
                {
                    double kbDelta = zoomIn ? kbStep : -kbStep;
                    CameraPropertiesUpdate kbz = ViewInput.ApplyZoom(
                        projection, Map.Camera.CurrentProperties,
                        vp * 0.5, vp, kbDelta, 1.0, MinZoom, MaxZoom);
                    patch.Zoom      = kbz.Zoom;
                    patch.Longitude = kbz.Longitude;
                    patch.Latitude  = kbz.Latitude;
                    anyChange       = true;
                }
            }

            if (anyChange)
                Map.Camera.Apply(patch, CameraAnimation.Instant);
        }

        // ── Camera-transform bridge (CameraTransformTests + MapRoot frame-0 framing) ─────────────

        /// <summary>
        /// Positions/orients the driven <see cref="Camera"/> from a <see cref="CameraProperties"/>.
        /// Delegates to <see cref="MapCamera"/> using the Core pose math (S42 D1/D2/D3). Used by
        /// <c>MapRoot</c> for the frame-0 framing and by <c>CameraTransformTests</c>.
        ///
        /// <para>Made <c>public</c> so unit tests can call it directly; null-guards internally.</para>
        /// </summary>
        public void ApplyCameraTransform(CameraProperties props)
        {
            if (Camera == null) return;

            // Delegate to MapCamera (Core pose math) via a temporary MapCamera wrapper.
            var mc = new MapCamera(Camera, ReferenceViewportHeightPx, VerticalFovDeg);
            mc.AltitudeMultiplier = AltitudeMultiplier;

            mc.ApplyCameraProperties(props);
        }

        /// <summary>
        /// S42/S45 bridge: computes camera altitude from zoom. Delegates to <see cref="CameraPoseMath"/>.
        /// Kept <c>public static</c> for <c>CameraTransformTests</c> backward compatibility (tooth 6).
        /// </summary>
        public static float AltitudeForZoom(double zoom, float viewportHeightPx, float verticalFovDeg)
            => (float)CameraPoseMath.AltitudeForZoom(zoom, viewportHeightPx, verticalFovDeg);
    }
}
