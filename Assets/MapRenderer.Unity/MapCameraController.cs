using UnityEngine;
using MapRenderer.Core.View;

namespace MapRenderer.Unity
{
    /// <summary>
    /// Translates user input (pan / zoom / tilt) into <see cref="ViewState"/> mutations and drives the
    /// <see cref="MapView"/>, applying bearing/pitch to the Unity camera transform. A thin shell over the
    /// pure, unit-tested <see cref="ViewInput"/> helpers — all the math lives there so it has headless
    /// coverage; this class only reads <c>UnityEngine.Input</c> and pushes the result.
    ///
    /// <para><b>Input backend (S06): legacy <c>UnityEngine.Input</c>.</b> The project's
    /// <c>activeInputHandler</c> is "Both", so the legacy module is always compiled and adds no package
    /// dependency. Controls: left-drag / WASD = pan, scroll = zoom, right-drag = tilt (vertical → pitch,
    /// horizontal → bearing).</para>
    ///
    /// <para>Bearing/pitch live on the CAMERA transform only — tile/scene-root transforms stay
    /// translation-only (preserving +Y fill normals), per the S06 scope decision.</para>
    ///
    /// <para>Allocation-free <see cref="Update"/>: no LINQ, no closures, no per-frame allocation.</para>
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class MapCameraController : MonoBehaviour
    {
        [Tooltip("The MapView this controller drives.")]
        public MapView Map;

        [Header("Sensitivity")]
        public float ZoomSensitivity    = 0.25f;
        public float BearingSensitivity = 0.3f;
        public float PitchSensitivity   = 0.3f;
        public float MaxPitch           = 60f;

        [Header("Zoom clamp")]
        public float MinZoom = 0f;
        public float MaxZoom = 22f;

        [Header("Camera framing (render space, metres)")]
        [Tooltip("Camera distance above the map plane in render-space metres.")]
        public float CameraHeight = 1500f;

        private Camera _camera;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        private void Update()
        {
            if (Map == null) return;

            ViewState v = Map.View;

            // ── Zoom (scroll) ──
            float scroll = Input.mouseScrollDelta.y;
            if (scroll != 0f)
                v = ViewInput.ApplyZoom(v, scroll, ZoomSensitivity, MinZoom, MaxZoom);

            // ── Pan (left-drag) ──
            if (Input.GetMouseButton(0))
            {
                float dx = Input.GetAxis("Mouse X");
                float dy = Input.GetAxis("Mouse Y");
                if (dx != 0f || dy != 0f)
                    v = ViewInput.ApplyPan(v, dx, dy);
            }

            // ── Tilt / bearing (right-drag) ──
            if (Input.GetMouseButton(1))
            {
                float dx = Input.GetAxis("Mouse X");
                float dy = Input.GetAxis("Mouse Y");
                if (dx != 0f || dy != 0f)
                    v = ViewInput.ApplyTilt(v, dx, dy, BearingSensitivity, PitchSensitivity, MaxPitch);
            }

            Map.SetView(v);
            ApplyCameraTransform(v);
        }

        /// <summary>
        /// Positions/orients the camera from the view's bearing/pitch. The camera looks at the scene
        /// origin (render space ~0,0,0 after rebasing) from above, tilted by pitch and rotated by bearing.
        /// Translation-only tile transforms mean ALL orientation lives here.
        /// </summary>
        private void ApplyCameraTransform(ViewState v)
        {
            float bearing = (float)v.BearingDeg;
            float pitch   = (float)v.PitchDeg;

            // Orbit the camera around the scene origin: rotate by bearing about +Y, tilt by pitch.
            Quaternion rot = Quaternion.Euler(90f - pitch, bearing, 0f);
            Vector3 offset = rot * (Vector3.up * CameraHeight);

            transform.position = offset;
            transform.rotation = Quaternion.LookRotation(-offset.normalized, Vector3.up);
        }
    }
}
