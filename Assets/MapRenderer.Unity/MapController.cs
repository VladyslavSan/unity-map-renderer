using UnityEngine;
using UnityEngine.InputSystem;
using MapRenderer.Core.View;

namespace MapRenderer.Unity
{
    /// <summary>
    /// S42: Translates user input (pan / zoom / tilt) into <see cref="ViewState"/> mutations and drives
    /// the <see cref="MapView"/>, applying bearing/pitch/altitude to the driven camera's transform.
    /// A thin shell over the pure, unit-tested <see cref="ViewInput"/> helpers — all the math lives there
    /// so it has headless coverage; this class only reads input and pushes the result.
    ///
    /// <para><b>Input backend: new Input System (<c>UnityEngine.InputSystem</c>).</b> The project's
    /// <c>activeInputHandler</c> is 1 (new only). Reads <c>Mouse.current</c> and <c>Keyboard.current</c>
    /// directly (immediate-mode polling; no InputActions asset). Controls: left-drag = pan, scroll = zoom,
    /// right-drag = tilt (vertical → pitch, horizontal → bearing), +/=/Q = zoom in, -/E = zoom out.</para>
    ///
    /// <para><b>Scroll normalization (S42, D4):</b>
    ///   Mouse.current.scroll.y delivers ±120 units/notch for a physical wheel; trackpads deliver
    ///   continuous, lower-magnitude values. We normalize by <see cref="WheelNotchUnits"/> (= 120)
    ///   so that one physical notch ≈ 1.0 normalized unit and trackpad continuous scroll scales
    ///   proportionally. <see cref="ZoomSensitivity"/> then converts normalized units to zoom levels.
    ///   Default: 0.25/notch → comfortable step that works the same on wheel and trackpad.</para>
    ///
    /// <para><b>Altitude-from-zoom (S42, D2):</b>
    ///   Camera altitude is derived from <c>v.Zoom</c> using the standard Web-Mercator relation:
    ///   <c>metersPerPixel = 40075016.686 / (256 * 2^zoom)</c>; for a perspective camera,
    ///   <c>altitude = (viewportHeightPx * metersPerPixel) / (2 * tan(verticalFOV/2))</c>.
    ///   <see cref="ReferenceViewportHeightPx"/> is a serialized deterministic height (not
    ///   <c>Camera.pixelHeight</c>, which is non-reproducible headless) so unit tests can pin the
    ///   exact formula output. An optional <see cref="AltitudeMultiplier"/> scales the result.</para>
    ///
    /// <para><b>Camera placement (S42, D1):</b>
    ///   pitch=0 ⇒ camera directly above origin (position.y = altitude), forward ≈ (0,-1,0).
    ///   Increasing pitch tilts toward the horizon; bearing rotates about +Y.</para>
    ///
    /// <para><b>Clip planes (S42, D3):</b>
    ///   near = altitude × 0.01; far = altitude × 4 (covers tilt up to ~75° with headroom).
    ///   Scales with altitude so the world is not clipped at zoom 2 and precision is sane at zoom 16.</para>
    ///
    /// <para>Lives on <b>MapRoot</b> (not on the Camera); drives the <see cref="Camera"/> field's
    /// transform. Both <see cref="Camera"/> and <see cref="Map"/> are set by the wire-up bootstrap at
    /// runtime via <see cref="MapRoot.Wire"/>. Do NOT put this component on the Camera GameObject.</para>
    ///
    /// <para>Bearing/pitch live on the CAMERA transform only — tile/scene-root transforms stay
    /// translation-only (preserving +Y fill normals), per the S06 scope decision.</para>
    ///
    /// <para>Allocation-free <see cref="Update"/>: no LINQ, no closures, no per-frame allocation.</para>
    /// </summary>
    public sealed class MapController : MonoBehaviour
    {
        [Tooltip("The MapView this controller drives (set by MapRoot.Wire at runtime).")]
        public MapView Map;

        [Tooltip("The camera this controller positions (set by MapRoot.Wire at runtime).")]
        public Camera Camera;

        // ── Sensitivity (new Input System calibration — see class doc) ───────────────────────────
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

        public float MaxPitch           = 60f;

        [Header("Zoom clamp")]
        public float MinZoom = 0f;
        public float MaxZoom = 22f;

        // ── Camera framing — altitude is DERIVED from zoom (S42 D2) ─────────────────────────────
        [Header("Camera framing — altitude derived from zoom (S42 D2)")]

        /// <summary>
        /// Device-independent scroll magnitude per physical wheel notch (new Input System).
        /// Mouse.current.scroll.y delivers ±120 per notch; dividing by this normalizes to ≈±1.0/notch.
        /// Trackpad delivers continuous fractional values that scale proportionally after normalization.
        /// </summary>
        public const float WheelNotchUnits = 120f;

        [Tooltip("Deterministic viewport height fed to the altitude formula (not Camera.pixelHeight, " +
                 "which is non-reproducible in headless/test mode). Affects the absolute altitude; " +
                 "monotonicity holds for any positive value.")]
        public float ReferenceViewportHeightPx = 1080f;

        [Tooltip("Vertical field-of-view for the perspective camera (degrees). " +
                 "Pushed to Camera.fieldOfView each frame.")]
        public float VerticalFovDeg = 60f;

        [Tooltip("Optional multiplier on the derived altitude (default 1). Tunable for art direction " +
                 "without changing the framing formula.")]
        public float AltitudeMultiplier = 1f;

        // ── Web-Mercator framing constant ─────────────────────────────────────────────────────────
        // Clean-room: standard web-map relation. Earth equatorial circumference in metres (IAU/WGS-84).
        private const double EarthCircumferenceMetres = 40075016.686;

        // ── Private state ─────────────────────────────────────────────────────────────────────────
        // (none needed beyond inspector fields; ViewState is value-pulled from MapView each frame)

        private void Update()
        {
            if (Map == null || Camera == null) return;

            ViewState v = Map.View;

            // ── Zoom (scroll wheel / trackpad) ────────────────────────────────────────────────────
            // Null-guard Mouse.current (absent in headless / test builds — no NRE).
            var mouse = Mouse.current;
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f)
                {
                    // Normalize: divide raw scroll by WheelNotchUnits so one physical wheel notch ≈ 1.0;
                    // trackpad continuous values scale proportionally. Then apply ZoomSensitivity.
                    float normalizedScroll = scroll / WheelNotchUnits;
                    v = ViewInput.ApplyZoom(v, normalizedScroll, ZoomSensitivity, MinZoom, MaxZoom);
                }

                // ── Pan (left-drag) ───────────────────────────────────────────────────────────────
                // delta.ReadValue() = raw accumulated pixel displacement this frame (no smoothing).
                if (mouse.leftButton.isPressed)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    if (delta.x != 0f || delta.y != 0f)
                        v = ViewInput.ApplyPan(v, delta.x, delta.y);
                }

                // ── Tilt / bearing (right-drag) ───────────────────────────────────────────────────
                if (mouse.rightButton.isPressed)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    if (delta.x != 0f || delta.y != 0f)
                        v = ViewInput.ApplyTilt(v, delta.x, delta.y, BearingSensitivity, PitchSensitivity, MaxPitch);
                }
            }

            // ── Keyboard zoom (+/= / Q  →  zoom in;  -  / E  →  zoom out) ───────────────────────
            // Null-guarded separately from Mouse.current (each device can be absent independently).
            var kb = Keyboard.current;
            if (kb != null)
            {
                float kbStep = KeyboardZoomStep * Time.deltaTime;
                bool zoomIn  = kb.equalsKey.isPressed || kb.numpadPlusKey.isPressed  || kb.qKey.isPressed;
                bool zoomOut = kb.minusKey.isPressed  || kb.numpadMinusKey.isPressed || kb.eKey.isPressed;
                if (zoomIn)
                    v = ViewInput.ApplyZoom(v, kbStep,  1.0f, MinZoom, MaxZoom);
                else if (zoomOut)
                    v = ViewInput.ApplyZoom(v, -kbStep, 1.0f, MinZoom, MaxZoom);
            }

            Map.SetView(v);
            ApplyCameraTransform(v);
        }

        /// <summary>
        /// Positions/orients the driven <see cref="Camera"/> from the view state (D1 + D2 + D3, S42).
        ///
        /// <para><b>D1 — Overhead at pitch 0:</b> camera is placed at <c>(0, altitude, 0)</c> looking
        /// straight down. Increasing pitch tilts toward the horizon; bearing rotates about +Y. The
        /// orbit formula: <c>offset = Rot(bearing,Y) * Rot(pitch,X) * (0, altitude, 0)</c>; at pitch=0
        /// the inner rotation is identity so offset = (0, altitude, 0) and forward = (0, -1, 0).</para>
        ///
        /// <para><b>D2 — Altitude from zoom:</b> see <see cref="AltitudeForZoom"/>.</para>
        ///
        /// <para><b>D3 — Clip planes:</b> near = altitude × 0.01, far = altitude × 4. Covers tilt up to
        /// ~75° (MaxPitch = 60°; at 60° the slant distance is altitude / cos(60°) = 2 × altitude, well
        /// inside the 4× far plane).</para>
        ///
        /// <para>Made <c>public</c> so unit tests can call it directly; null-guards internally.</para>
        /// </summary>
        public void ApplyCameraTransform(ViewState v)
        {
            if (Camera == null) return;

            // Set up perspective.
            Camera.orthographic = false;
            Camera.fieldOfView  = VerticalFovDeg;

            float altitude = AltitudeForZoom(v.Zoom, ReferenceViewportHeightPx, VerticalFovDeg)
                             * AltitudeMultiplier;

            // Clamp altitude to a sensible minimum so clip planes don't collapse.
            if (altitude < 0.1f) altitude = 0.1f;

            float bearing = (float)v.BearingDeg;
            float pitch   = (float)v.PitchDeg;

            // Orbit on a sphere of radius = altitude around the scene origin.
            // AngleAxis(bearing, up):  rotate about +Y (clockwise = east when bearing > 0)
            // AngleAxis(pitch, right): tilt from overhead toward the horizon
            // At pitch=0, Rot(pitch,X) is identity → offset = (0, altitude, 0) → y > 0, forward = (0,-1,0)
            Quaternion orient = Quaternion.AngleAxis(bearing, Vector3.up)
                              * Quaternion.AngleAxis(pitch,   Vector3.right);
            Vector3 offset    = orient * (Vector3.up * altitude);

            Camera.transform.position = offset;
            // Look at origin from the offset position. LookRotation(-offset.normalized) sets
            // forward = (origin - camera_pos).normalized, i.e. pointing toward the origin.
            Camera.transform.rotation = Quaternion.LookRotation(-offset.normalized, Vector3.up);

            // D3 — Clip planes scale with altitude (world not clipped at z2; precision OK at z16).
            Camera.nearClipPlane = Mathf.Max(0.1f, altitude * 0.01f);
            Camera.farClipPlane  = altitude * 4f;
        }

        /// <summary>
        /// Computes camera altitude in render-space metres from a fractional zoom level (S42 D2).
        ///
        /// <para>Web-Mercator ground resolution: <c>metersPerPixel = 40075016.686 / (256 × 2^zoom)</c>.
        /// For a perspective camera with vertical FOV looking straight down at the map plane,
        /// the altitude that frames exactly <c>viewportHeightPx</c> pixels of ground is:
        /// <c>altitude = (viewportHeightPx × metersPerPixel) / (2 × tan(verticalFovDeg/2))</c>.</para>
        ///
        /// <para>Made <c>public static</c> (pure, no side effects) so unit tests can pin the exact
        /// formula and verify monotonicity/magnitude against hand-computed expected values.</para>
        /// </summary>
        /// <param name="zoom">Fractional zoom level (MapLibre semantics; higher = more zoomed in).</param>
        /// <param name="viewportHeightPx">Deterministic viewport height in pixels (use
        ///   <see cref="ReferenceViewportHeightPx"/>, not <c>Camera.pixelHeight</c>).</param>
        /// <param name="verticalFovDeg">Vertical field-of-view in degrees.</param>
        /// <returns>Camera altitude in metres (same units as Web-Mercator render space).</returns>
        public static float AltitudeForZoom(double zoom, float viewportHeightPx, float verticalFovDeg)
        {
            double metersPerPixel = EarthCircumferenceMetres / (ViewInput.TilePixelSize * System.Math.Pow(2.0, zoom));
            double halfFovRad     = verticalFovDeg * 0.5 * System.Math.PI / 180.0;
            double altitude       = (viewportHeightPx * metersPerPixel) / (2.0 * System.Math.Tan(halfFovRad));
            return (float)altitude;
        }
    }
}
