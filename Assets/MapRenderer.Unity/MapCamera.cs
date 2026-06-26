using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity
{
    /// <summary>
    /// S45/S52: Thin Unity binding. Applies a Core <see cref="CameraProperties"/> to a
    /// <see cref="UnityEngine.Camera"/> transform. The geo-aware camera <em>model</em> lives in Core
    /// (<see cref="CameraSystem"/> + <see cref="CameraPoseMath"/>); this is the only piece allowed to
    /// touch <c>UnityEngine.Camera</c>.
    ///
    /// <para><b>Camera-relative rendering (S52):</b> the scene origin tracks the look-at every frame, so
    /// the look-at always sits at the render origin. The camera therefore <b>orbits the origin</b> — its
    /// render position is purely the orbit pose (altitude/heading/tilt) from <see cref="CameraPoseMath"/>,
    /// with no floating-origin term. That is why <see cref="ApplyCameraProperties"/> is a pure function of
    /// <see cref="CameraProperties"/> and needs nothing from the scene: positioning the tiles relative to
    /// the look-at is the tile layer's job (<see cref="FloatingOrigin"/>), not the camera's.</para>
    ///
    /// <para><b>Placement:</b> at tilt=0 the camera is directly above the look-at (origin) at altitude,
    /// looking straight down; at tilt&gt;0 it orbits toward the horizon; bearing rotates in the horizontal
    /// plane. The Core pose computes a deterministic heading-derived up-vector so LookRotation is never
    /// fed degenerate inputs. Clip planes: near = altitude·0.01 (min 0.1); far = altitude·4.</para>
    ///
    /// <para>Not a MonoBehaviour. <see cref="ApplyCameraProperties"/> is called by
    /// <see cref="MapView.UpdateFrame"/> after the camera model is advanced (D5).</para>
    /// </summary>
    public sealed class MapCamera
    {
        // ── Unity camera to drive ─────────────────────────────────────────────────────────────────
        private readonly UnityEngine.Camera _camera;

        // ── Framing parameters (match CameraSystem) ───────────────────────────────────────────────
        /// <summary>
        /// Deterministic reference viewport height fed to the altitude formula. Use a fixed constant
        /// (e.g. 1080), NOT <c>Camera.pixelHeight</c> — pixel height is non-reproducible in headless.
        /// </summary>
        public float ReferenceViewportHeightPx;

        /// <summary>Vertical FOV for the perspective camera (degrees). Pushed to <c>Camera.fieldOfView</c>.</summary>
        public float VerticalFovDeg;

        /// <summary>Optional altitude multiplier for art-direction (default 1). Same as S42 AltitudeMultiplier.</summary>
        public float AltitudeMultiplier = 1f;

        // ── Construction ──────────────────────────────────────────────────────────────────────────
        public MapCamera(UnityEngine.Camera camera,
                         float referenceViewportHeightPx = 1080f,
                         float verticalFovDeg             = 60f)
        {
            _camera                   = camera;
            ReferenceViewportHeightPx = referenceViewportHeightPx;
            VerticalFovDeg            = verticalFovDeg;
        }

        // ── Apply ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Positions and orients the Unity camera from <paramref name="props"/>. The camera orbits the
        /// render origin (the look-at, by the camera-relative-rendering invariant), so this is a pure
        /// function of the camera properties — no scene/floating-origin input.
        ///
        /// <para>Derives altitude from zoom, computes the orbit pose (position/forward/up) via
        /// <see cref="CameraPoseMath"/>, then sets transform, FOV, and altitude-scaled clip planes.
        /// No-op if the wrapped camera is null (headless/no-camera path).</para>
        /// </summary>
        public void ApplyCameraProperties(CameraProperties props)
        {
            if (_camera == null) return;

            double altitude = CameraPoseMath.AltitudeForZoom(props.Zoom, ReferenceViewportHeightPx, VerticalFovDeg)
                              * AltitudeMultiplier;
            if (altitude < 0.1) altitude = 0.1;

            CameraPoseMath.ComputePose(altitude,
                                       props.Heading,
                                       props.Tilt,
                                       out double3 pos,
                                       out double3 fwd,
                                       out double3 up);

            _camera.orthographic = false;
            _camera.fieldOfView  = VerticalFovDeg;

            // Orbit pose around the render origin (the look-at sits at origin under camera-relative
            // rendering, so there is no scene-origin term here).
            _camera.transform.position = new Vector3((float)pos.x, (float)pos.y, (float)pos.z);

            // LookRotation(forward, up): forward = direction the camera looks (toward the look-at/origin).
            // Core already orthogonalized up vs. fwd (Gram-Schmidt) so this is always valid.
            _camera.transform.rotation = Quaternion.LookRotation(
                new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                new Vector3((float)up.x,  (float)up.y,  (float)up.z));

            // Clip planes: scale with altitude so the world is not clipped at z2, precision OK at z16.
            _camera.nearClipPlane = Mathf.Max(0.1f, (float)CameraPoseMath.NearClip(altitude));
            _camera.farClipPlane  =                  (float)CameraPoseMath.FarClip(altitude);
        }
    }
}
