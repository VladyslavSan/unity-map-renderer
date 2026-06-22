using UnityEngine;
using UnityEngine.InputSystem;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity
{
    /// <summary>
    /// S45: Thin input → <see cref="CameraPropertiesUpdate"/> patch translator. Supersedes the
    /// S42 god-MonoBehaviour: all pose math has moved to <see cref="CameraPoseMath"/> (Core) and
    /// <see cref="MapCamera"/> (Unity sync). This class reads <c>Mouse.current</c> /
    /// <c>Keyboard.current</c> and calls <see cref="MapView.Camera"/>.Apply with instant patches.
    ///
    /// <para><b>Input backend: new Input System (<c>UnityEngine.InputSystem</c>).</b>
    ///   Reads <c>Mouse.current</c> and <c>Keyboard.current</c> directly (no legacy
    ///   <c>UnityEngine.Input</c>). Controls: left-drag = pan, scroll = zoom, right-drag = tilt/bearing,
    ///   +/=/Q = zoom in, −/E = zoom out.</para>
    ///
    /// <para><b>Scroll normalization (S42 D4 preserved):</b> <see cref="WheelNotchUnits"/> = 120 so
    ///   one physical wheel notch ≈ 1 normalized unit.</para>
    ///
    /// <para><b>D5 — Ordering:</b> this Update only queues patches on the camera system. The actual
    ///   camera advance + tile loop runs in <see cref="MapView.Update"/> (via
    ///   <see cref="MapView.UpdateFrame"/>). Unity does NOT guarantee the ordering of sibling
    ///   MonoBehaviour Updates, but the result is still correct: patches set the instant-path
    ///   current props which MapView.Advance picks up either this frame or next. For deterministic
    ///   ordering with no per-frame latency, <see cref="Map"/> should wire the camera via
    ///   <see cref="MapView.SetCamera"/> and call <see cref="ApplyCameraTransform"/> from within a
    ///   controlled context — used by tests.</para>
    ///
    /// <para><b>Camera-transform helpers:</b>
    ///   <see cref="ApplyCameraTransform(CameraProperties)"/> and <see cref="AltitudeForZoom"/> are
    ///   thin delegates to the Core pose math, used by <c>MapRoot</c> frame-0 framing and by
    ///   <c>CameraTransformTests</c>.</para>
    ///
    /// <para>Allocation-free <see cref="Update"/>: struct patches, no LINQ, no closures.</para>
    /// </summary>
    public sealed class MapController : MonoBehaviour
    {
        [Tooltip("The MapView this controller drives (set by MapRoot.Wire at runtime).")]
        public MapView Map;

        [Tooltip("The camera this controller positions (set by MapRoot.Wire at runtime).")]
        public Camera Camera;

        // ── Sensitivity (new Input System calibration) ────────────────────────────────────────────
        [Header("Sensitivity (new Input System — see class doc for calibration notes)")]

        [Tooltip("Zoom sensitivity. One normalized scroll unit (= one wheel notch via WheelNotchUnits) " +
                 "is multiplied by this to get zoom delta. Default 0.25 → 0.25 zoom levels per notch.")]
        public float ZoomSensitivity    = 0.25f;

        [Tooltip("Keyboard zoom step in zoom-levels per second. Applied via Time.deltaTime.")]
        public float KeyboardZoomStep   = 2.0f;

        [Tooltip("Bearing sensitivity (raw px/frame → degrees). 0.3 matches the legacy feel.")]
        public float BearingSensitivity = 0.3f;

        [Tooltip("Pitch sensitivity (raw px/frame → degrees). 0.3 matches the legacy feel.")]
        public float PitchSensitivity   = 0.3f;

        public float MaxPitch = 60f;

        [Header("Zoom clamp")]
        public float MinZoom = 0f;
        public float MaxZoom = 22f;

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

        // ── Update: produce CameraPropertiesUpdate patches ────────────────────────────────────────

        private void Update()
        {
            if (Map == null || Map.Camera == null) return;

            CameraPropertiesUpdate patch = default;
            bool anyChange = false;

            // ── Zoom (scroll wheel / trackpad) ───────────────────────────────────────────────────
            // Null-guard Mouse.current (absent in headless / test builds — no NRE).
            var mouse = Mouse.current;
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f)
                {
                    // Normalize: divide raw scroll by WheelNotchUnits so one wheel notch ≈ 1.0.
                    float normalizedScroll = scroll / WheelNotchUnits;
                    double newZoom = (Map.Camera.CurrentProperties.Zoom + normalizedScroll * ZoomSensitivity);
                    newZoom = System.Math.Max(MinZoom, System.Math.Min(MaxZoom, newZoom));
                    patch.Zoom = newZoom;
                    anyChange  = true;
                }

                // ── Pan (left-drag, D6a — Y-sign correct for new Input System) ─────────────────
                // Mouse.current.delta.y is +up in the new Input System. ViewInput.ApplyPan (shared,
                // engine-free) treats dy>0 as a downward drag (screen +y → +lat). So we negate delta.y
                // here at the translator: a +Y drag (drag UP) → ApplyPan(..., -dy) → center moves SOUTH
                // = content follows the cursor. The negation keeps ViewInput unchanged (it also runs the
                // core-tests suite, where the D6a tests pin this exact sign flip).
                if (mouse.leftButton.isPressed)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    if (delta.x != 0f || delta.y != 0f)
                    {
                        CameraPropertiesUpdate pan = ViewInput.ApplyPan(Map.Camera.CurrentProperties, delta.x, -delta.y);
                        if (pan.Lon.HasValue) { patch.Lon = pan.Lon; anyChange = true; }
                        if (pan.Lat.HasValue) { patch.Lat = pan.Lat; anyChange = true; }
                    }
                }

                // ── Tilt / bearing (right-drag, D6 — Y-sign matches the pan convention) ───────────
                // Same sign flip as pan: Mouse.current.delta.y is +up in the new Input System, but
                // ViewInput.ApplyTilt adds dy·sensitivity to the current tilt. Negating delta.y means a
                // +Y drag (drag UP) → ApplyTilt(..., -dy) → pitch DECREASES → camera tilts toward overhead
                // (content follows the cursor). Pinned headless by the D6 tilt-Y tests in
                // CameraPropertiesTests. (Final direction is a maintainer play-test call per S50 D6.)
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
                        anyChange = true;
                    }
                }
            }

            // ── Keyboard zoom (+/= / Q → zoom in; − / E → zoom out) ─────────────────────────────
            // Null-guarded separately from Mouse.current (each device can be absent independently).
            var kb = Keyboard.current;
            if (kb != null)
            {
                float kbStep = KeyboardZoomStep * Time.deltaTime;
                bool zoomIn  = kb.equalsKey.isPressed || kb.numpadPlusKey.isPressed  || kb.qKey.isPressed;
                bool zoomOut = kb.minusKey.isPressed  || kb.numpadMinusKey.isPressed || kb.eKey.isPressed;
                if (zoomIn || zoomOut)
                {
                    double base_ = patch.Zoom ?? Map.Camera.CurrentProperties.Zoom;
                    double delta = zoomIn ? kbStep : -kbStep;
                    double newZoom = System.Math.Max(MinZoom, System.Math.Min(MaxZoom, base_ + delta));
                    patch.Zoom = newZoom;
                    anyChange  = true;
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
