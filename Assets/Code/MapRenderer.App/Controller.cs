using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.App.View;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Camera;

using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.App
{
    /// <summary>
    /// Thin input → <see cref="CameraPropertiesUpdate"/> patch translator over the new Input System
    /// (<c>Mouse.current</c>, <c>Keyboard.current</c>). Controls: left-drag = pan, shift+drag = tilt,
    /// ctrl+drag = heading, scroll = zoom-to-cursor, +/=/Q = zoom in, −/E = zoom out. Each gesture becomes a
    /// <see cref="GestureIntent"/> for <see cref="ViewInput"/>, with sensitivity applied here first. Update
    /// allocates nothing in steady state.
    /// Non-local invariant: <see cref="Update"/> only queues patches, and <see cref="MapView.LateUpdate"/>
    /// folds them into one camera snapshot in the same frame, because Unity runs every LateUpdate after every Update.
    /// </summary>
    public sealed class Controller : MonoBehaviour
    {
        // Wired at runtime by MapHost — runtime references, not authoring data, so auto-properties (not
        // serialized Inspector fields: a serialized slot would just show a dead value MapHost overwrites on Start).
        public MapViewComponent Map { get; set; }
        public Camera Camera { get; set; }

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

        [Header("Zoom clamp")]
        [Tooltip("Device-derived MIN-zoom floor — recomputed each frame from the LOGICAL viewport so " +
                 "the most-zoomed-out level frames the whole world with a margin (no world-square grape). The " +
                 "serialized value is only a frame-0 seed; it is overwritten live in Update.")]
        public float MinZoom = 0f;
        public float MaxZoom = 22f;

        [Tooltip("Breathing-room margin (in zoom levels) for the device-derived MinZoom floor. " +
                 "0.5 ⇒ the world is ~1.4× smaller than the viewport at the floor.")]
        public float MinZoomMargin = 0.5f;

        // ── Camera framing (bridge — altitude derived from zoom) ──────────────────────────────────
        [Header("Camera framing — altitude derived from zoom")]
        /// <summary>
        /// Device-independent scroll magnitude per physical wheel notch (new Input System).
        /// Mouse.current.scroll.y delivers ±120 per notch; dividing by this normalizes to ≈±1.0/notch.
        /// Trackpad delivers continuous fractional values that scale proportionally after normalization.
        /// </summary>
        public const float WheelNotchUnits = 120f;

        [Tooltip("Optional multiplier on the derived altitude (default 1).")]
        public float AltitudeMultiplier = 1f;

        // ── Pan drag state (anchored pan) ─────────────────────────────────────────────────────────
        private GeoCoordinate3D _grabbedGround;
        private bool            _dragging;

        // ── Update: produce CameraPropertiesUpdate patches ────────────────────────────────────────

        private void Update()
        {
            if (Map == null || Map.Camera == null) return;

            CameraPropertiesUpdate patch     = default;
            bool                   anyChange = false;

            // Resolve the active projection once per frame.
            IProjection projection = Map.Camera.Projection;

            // Non-local invariant: the interaction seam runs in LOGICAL pixels, converted here by the same
            // DeviceScaling.DeviceToLogicalPx that MapCamera.ViewportLogicalPx frames from, so input and render
            // cannot diverge. This is not MapCamera.ViewportPx because this component's Camera may be null,
            // and the Screen.width/height fallback has no MapCamera counterpart.
            double dpr = Map.Config.DevicePixelRatio;
            double2 vp = DeviceScaling.DeviceToLogicalPx(new double2(
                Camera != null ? Camera.pixelWidth  : Screen.width,
                Camera != null ? Camera.pixelHeight : Screen.height), dpr);

            // Min-zoom floor from the logical viewport, keyed on the projection: a finite Mercator sheet fills
            // the viewport, a cyclic globe fits with margin.
            MinZoom = (float)CameraPoseMath.MinZoomFloor(projection, vp.x, vp.y, MinZoomMargin);

            // The live viewport puts the cursor and viewport in the same pixel scale, which the zoom-pin and
            // pan-pin invariants require.
            var view = new ViewContext
            {
                Camera     = Map.Camera.CurrentProperties,
                ViewportPx = vp,
                Projection = projection,
            };

            // ── Keyboard modifier state (read early; reused in the drag block below) ────────────
            // Null-guarded separately from Mouse.current (each device can be absent independently).
            var kb = Keyboard.current;

            // ── Zoom (scroll wheel / trackpad) ───────────────────────────────────────────────────
            // Null-guard Mouse.current (absent in headless / test builds — no NRE).
            var mouse = Mouse.current;
            if (mouse != null)
            {
                // Read cursor position once (used by both scroll-zoom and drag).
                Vector2 mousePos = mouse.position.ReadValue();
                double2 cursor   = DeviceScaling.DeviceToLogicalPx(new double2(mousePos.x, mousePos.y), dpr); // logical px

                // Non-obvious why: the new Input System reads the OS-level mouse over any Editor panel or app, so
                // without this gate an Inspector scroll or trackpad momentum would zoom the map. Ongoing drags are
                // exempt (see the leftButton block), so a drag that starts in-view can continue past the edge.
                bool pointerInViewport = cursor.x >= 0.0 && cursor.y >= 0.0 && cursor.x < vp.x && cursor.y < vp.y;
                bool acceptPointerInput = Application.isFocused && pointerInViewport;

                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f && acceptPointerInput)
                {
                    // Normalize: divide raw scroll by WheelNotchUnits so one wheel notch ≈ 1.0.
                    float normalizedScroll = scroll / WheelNotchUnits;
                    // ZoomAtAnchor: pinned-cursor zoom via the device-agnostic seam.
                    var zi = GestureIntent.ZoomAt(cursor, normalizedScroll * ZoomSensitivity, MinZoom, MaxZoom);
                    CameraPropertiesUpdate z = ViewInput.Apply(zi, view);
                    patch.Zoom      = z.Zoom;
                    patch.Longitude = z.Longitude;
                    patch.Latitude  = z.Latitude;
                    anyChange       = true;
                }

                // ── Left-drag: modifier decides the gesture ──────────────────────────────────────
                // shift → tilt (-delta.y: drag-up → overhead), else ctrl → heading (+delta.x), else anchored pan.
                if (mouse.leftButton.isPressed && (_dragging || acceptPointerInput))
                {
                    bool shift = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
                    bool ctrl  = kb != null && (kb.leftCtrlKey.isPressed  || kb.rightCtrlKey.isPressed);

                    Vector2 delta = mouse.delta.ReadValue();

                    if (shift)
                    {
                        // shift+drag → TiltBy (pitch only; heading unchanged).
                        // Clear _dragging so a subsequent plain pan re-captures _grabbedGround cleanly.
                        _dragging = false;
                        var ti = GestureIntent.TiltBy(-delta.y * PitchSensitivity, MaxPitch);
                        CameraPropertiesUpdate tp = ViewInput.Apply(ti, view);
                        patch.Tilt = tp.Tilt;
                        anyChange  = true;
                    }
                    else if (ctrl)
                    {
                        // ctrl+drag → HeadingBy (bearing only; tilt unchanged).
                        _dragging = false;
                        var hi = GestureIntent.HeadingBy(delta.x * BearingSensitivity);
                        CameraPropertiesUpdate hp = ViewInput.Apply(hi, view);
                        patch.Heading = hp.Heading;
                        anyChange     = true;
                    }
                    else
                    {
                        // No modifier → PanToAnchor (anchored pan).
                        // Capture the grabbed ground point on the first frame of the press.
                        if (!_dragging)
                        {
                            _grabbedGround = projection.ScreenToGround(cursor, vp, Map.Camera.CurrentProperties);
                            _dragging      = true;
                        }

                        // The globe uses a BOUNDED rotation solve — the planar affine anchored pan diverges near
                        // the sphere's limb (spins the earth at low zoom). The planar path keeps ViewInput.ApplyPan.
                        if (projection is SphericalProjection sphere)
                        {
                            GeoCoordinate3D newLookAt = sphere.PanLookAtForGrab(
                                _grabbedGround, cursor, vp, Map.Camera.CurrentProperties);
                            patch.Longitude = newLookAt.Longitude;
                            patch.Latitude  = newLookAt.Latitude;
                        }
                        else
                        {
                            var pi = GestureIntent.Pan(_grabbedGround, cursor);
                            CameraPropertiesUpdate pan = ViewInput.Apply(pi, view);
                            patch.Longitude = pan.Longitude;
                            patch.Latitude  = pan.Latitude;
                        }
                        anyChange = true;
                    }
                }
                else
                {
                    _dragging = false;
                }
            }

            // ── Keyboard zoom (+/= / Q → zoom in; − / E → zoom out) ─────────────────────────────
            // Zooms at the viewport centre. Gated on app focus only, not on the pointer position.
            if (kb != null && Application.isFocused)
            {
                float kbStep  = KeyboardZoomStep * Time.deltaTime;
                bool  zoomIn  = kb.equalsKey.isPressed || kb.numpadPlusKey.isPressed  || kb.qKey.isPressed;
                bool  zoomOut = kb.minusKey.isPressed  || kb.numpadMinusKey.isPressed || kb.eKey.isPressed;
                if (zoomIn || zoomOut)
                {
                    double kbDelta = zoomIn ? kbStep : -kbStep;
                    var kbi = GestureIntent.ZoomAt(vp * 0.5, kbDelta, MinZoom, MaxZoom);
                    CameraPropertiesUpdate kbz = ViewInput.Apply(kbi, view);
                    patch.Zoom      = kbz.Zoom;
                    patch.Longitude = kbz.Longitude;
                    patch.Latitude  = kbz.Latitude;
                    anyChange       = true;
                }
            }

            if (anyChange)
                Map.Camera.Apply(patch);
        }

        // ── Camera-transform bridge (CameraTransformTests + MapRoot frame-0 framing) ─────────────

        /// <summary>
        /// Positions/orients the driven <see cref="Camera"/> from a <see cref="CameraProperties"/>.
        /// Delegates to <see cref="MapCamera"/> using the Core pose math. Used by <c>MapRoot</c> for the
        /// frame-0 framing and by <c>CameraTransformTests</c>.
        ///
        /// <para>Made <c>public</c> so unit tests can call it directly; null-guards internally.</para>
        /// </summary>
        public void ApplyCameraTransform(CameraProperties props)
        {
            if (Camera == null) return;

            // A MapCamera sets the transform from its props on construction; FOV and viewport height come
            // from the props and the camera, not from side config.
            _ = new MapCamera(Camera, props, AltitudeMultiplier);
        }
    }
}
