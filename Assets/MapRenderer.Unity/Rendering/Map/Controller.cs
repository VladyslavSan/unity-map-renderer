using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// S45/S73: Thin input → <see cref="CameraPropertiesUpdate"/> patch translator. Supersedes the
    /// S42 god-MonoBehaviour: all pose math has moved to <see cref="CameraPoseMath"/> (Core) and
    /// <see cref="MapCamera"/> (Unity sync). This class reads <c>Mouse.current</c> /
    /// <c>Keyboard.current</c> and calls <see cref="View.Camera"/>.Apply with instant patches.
    ///
    /// <para><b>Input backend: new Input System (<c>UnityEngine.InputSystem</c>).</b>
    ///   Reads <c>Mouse.current</c> and <c>Keyboard.current</c> directly (no legacy
    ///   <c>UnityEngine.Input</c>). Controls: left-drag = pan, shift+drag = tilt, ctrl+drag = heading,
    ///   scroll = zoom-to-cursor, +/=/Q = zoom in, −/E = zoom out.</para>
    ///
    /// <para><b>Scroll normalization (S42 D4 preserved):</b> <see cref="WheelNotchUnits"/> = 120 so
    ///   one physical wheel notch ≈ 1 normalized unit.</para>
    ///
    /// <para><b>S63 — Interaction-point-aware gestures.</b> Zoom uses the cursor as the anchor point
    ///   (zoom-to-cursor); pan tracks the grabbed ground point that was under the cursor at drag-start
    ///   (anchored pan). Both delegate to <see cref="ViewInput"/> which carries zero Mercator constants.
    ///   Screen convention: +x right, +y up, origin bottom-left (Unity mouse position).</para>
    ///
    /// <para><b>S73 — Device-agnostic seam.</b> All gestures are translated into
    ///   <see cref="GestureIntent"/> values and dispatched through
    ///   <see cref="ViewInput.Apply(in GestureIntent, in ViewContext)"/>. Sensitivity is applied here
    ///   (before building the intent) so the seam sees device-independent magnitudes. Modifier precedence
    ///   per frame: shift → <see cref="GestureKind.TiltBy"/> (drag-Y only), else ctrl →
    ///   <see cref="GestureKind.HeadingBy"/> (drag-X only), else <see cref="GestureKind.PanToAnchor"/>.
    ///   Sign convention: <c>-delta.y</c> for tilt (drag-up → pitch decreases, toward overhead),
    ///   <c>+delta.x</c> for heading (drag-right → bearing increases).</para>
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
        [Tooltip("The MapView this controller drives (set by Bootstrapper.Wire at runtime).")]
        public MapViewComponent Map;

        [Tooltip("The camera this controller positions (set by Bootstrapper.Wire at runtime).")]
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

        [Header("Zoom clamp")]
        [Tooltip("Device-derived MIN-zoom floor — recomputed each frame from the LOGICAL viewport (S92 D2) so " +
                 "the most-zoomed-out level frames the whole world with a margin (no world-square grape). The " +
                 "serialized value is only a frame-0 seed; it is overwritten live in Update.")]
        public float MinZoom = 0f;
        public float MaxZoom = 22f;

        [Tooltip("Breathing-room margin (in zoom levels) for the device-derived MinZoom floor (S92 D2). " +
                 "0.5 ⇒ the world is ~1.4× smaller than the viewport at the floor.")]
        public float MinZoomMargin = 0.5f;

        // ── Camera framing (bridge — altitude derived from zoom, S42 D2) ──────────────────────────
        [Header("Camera framing — altitude derived from zoom (S42 D2)")]
        /// <summary>
        /// Device-independent scroll magnitude per physical wheel notch (new Input System).
        /// Mouse.current.scroll.y delivers ±120 per notch; dividing by this normalizes to ≈±1.0/notch.
        /// Trackpad delivers continuous fractional values that scale proportionally after normalization.
        /// </summary>
        public const float WheelNotchUnits = 120f;

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

            // Resolve the active projection once per frame.
            IProjection projection = Map.Camera.Projection;

            // The camera-interaction seam runs entirely in LOGICAL pixels (S92 D3): divide BOTH the viewport
            // and the cursor by DPR at their single read sites, so everything downstream — the ViewContext,
            // the gesture anchors, ScreenToGround/GroundToScreen — shares the render camera's logical basis
            // (MapCamera D1 frames vp/DPR). Config.DevicePixelRatio is the SAME source the render reads, so
            // reconstruction and render can't diverge → the anchored-pan pin holds under DPR≠1. Guard ≤0 → 1.
            double dpr = Map.Config.DevicePixelRatio > 0.0 ? Map.Config.DevicePixelRatio : 1.0;
            double2 vp = new double2(
                Camera != null ? Camera.pixelWidth  : Screen.width,
                Camera != null ? Camera.pixelHeight : Screen.height) / dpr;

            // Device-derived MIN-zoom floor from the (now logical) viewport (S92 D2): the most-zoomed-out level
            // frames the whole world with breathing room instead of shrinking to a useless world-square grape.
            MinZoom = (float)CameraPoseMath.MinZoomToFit(vp.x, vp.y, MinZoomMargin);

            // Build the per-frame view context (camera + live interaction viewport + projection).
            // The live viewport is used here so cursor positions and viewport are in the same pixel
            // scale — required by the B-ZOOMPIN / B-PAN pin invariants.
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
                double2 cursor   = new double2(mousePos.x, mousePos.y) / dpr; // logical px (S92 D3 seam)

                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f)
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

                // ── Left-drag: modifier decides the gesture (S73 D4) ─────────────────────────────
                // Modifier precedence (evaluated each frame):
                //   shift → TiltBy (drag-Y only; drag-X ignored; _dragging cleared for clean re-capture)
                //   ctrl  → HeadingBy (drag-X only; drag-Y ignored; _dragging cleared)
                //   else  → PanToAnchor (anchored pan, grabbed ground captured on first frame)
                //
                // Sign convention (pinned by CameraPropertiesTests.D6_TiltYSign_*):
                //   Tilt:    -delta.y  (drag-UP → pitch decreases, toward overhead)
                //   Heading: +delta.x  (drag-right → bearing increases)
                if (mouse.leftButton.isPressed)
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
                        // No modifier → PanToAnchor (anchored pan, S63).
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
            // Null-guarded separately from Mouse.current (each device can be absent independently).
            // ZoomAtAnchor at the viewport centre → exact centre-zoom feel (unchanged from S42).
            if (kb != null)
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
        /// Delegates to <see cref="MapCamera"/> using the Core pose math (S42 D1/D2/D3). Used by
        /// <c>MapRoot</c> for the frame-0 framing and by <c>CameraTransformTests</c>.
        ///
        /// <para>Made <c>public</c> so unit tests can call it directly; null-guards internally.</para>
        /// </summary>
        public void ApplyCameraTransform(CameraProperties props)
        {
            if (Camera == null) return;

            // A MapCamera drives the transform from its props on construction (Core pose math); this
            // one-shot wrapper frames the Unity camera for the given props (initial framing / tests). FOV
            // and viewport height come from the props / the camera, not from side config.
            _ = new MapCamera(Camera, props, AltitudeMultiplier);
        }

        /// <summary>
        /// S42/S45 bridge: computes camera altitude from zoom. Delegates to <see cref="CameraPoseMath"/>.
        /// Kept <c>public static</c> for <c>CameraTransformTests</c> backward compatibility (tooth 6).
        /// </summary>
        public static float AltitudeForZoom(double zoom, float viewportHeightPx, float verticalFovDeg)
            => (float)CameraPoseMath.AltitudeForZoom(zoom, viewportHeightPx, verticalFovDeg);
    }
}
