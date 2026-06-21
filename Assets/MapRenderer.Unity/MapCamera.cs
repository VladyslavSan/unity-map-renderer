using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity
{
    /// <summary>
    /// S45: Thin Unity sync layer. Reads the Core-computed pose from <see cref="CameraSystem"/> and
    /// applies it to a <see cref="UnityEngine.Camera"/> transform.
    ///
    /// <para><b>Responsibility split:</b>
    /// <list type="bullet">
    ///   <item><see cref="CameraSystem"/> (Core) — owns the canonical camera state, animation, and
    ///     pose math. Engine-free; headless-testable.</item>
    ///   <item><see cref="MapCamera"/> (Unity) — converts Core <see cref="Double3"/> position/up/fwd
    ///     to Unity <c>Vector3</c>/<c>Quaternion</c>, sets <c>Camera.transform</c>, FOV, and clip
    ///     planes. No logic — only conversion and assignment.</item>
    /// </list>
    /// </para>
    ///
    /// <para><b>Camera placement (D6 — absorbed from MapController.ApplyCameraTransform):</b>
    ///   At tilt=0: camera directly above look-at at altitude, forward=(0,-1,0).
    ///   At tilt&gt;0: orbits toward the horizon per the Core pose formula.
    ///   Bearing rotates in the horizontal plane. The Core pose already computes a deterministic
    ///   heading-derived up-vector (D6b fix), so LookRotation is never given degenerate inputs.</para>
    ///
    /// <para><b>Clip planes (S42 D3, absorbed):</b> near = altitude×0.01 (min 0.1); far = altitude×4.</para>
    ///
    /// <para>Framing constants (<see cref="ReferenceViewportHeightPx"/>, <see cref="VerticalFovDeg"/>)
    /// match those on the <see cref="CameraSystem"/> — set by <see cref="MapRoot.Wire"/>.</para>
    ///
    /// <para>Not a MonoBehaviour. <see cref="Sync"/> is called by <see cref="MapView.UpdateFrame"/>
    /// (D5 — deterministic, first step of MapView.Update).</para>
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

        /// <summary>
        /// Vertical FOV for the perspective camera (degrees). Pushed to <c>Camera.fieldOfView</c>
        /// each <see cref="Sync"/>.
        /// </summary>
        public float VerticalFovDeg;

        /// <summary>Optional altitude multiplier for art-direction (default 1). Same as S42 AltitudeMultiplier.</summary>
        public float AltitudeMultiplier = 1f;

        // ── Construction ──────────────────────────────────────────────────────────────────────────
        public MapCamera(UnityEngine.Camera camera,
                         float referenceViewportHeightPx = 1080f,
                         float verticalFovDeg             = 60f)
        {
            _camera                 = camera;
            ReferenceViewportHeightPx = referenceViewportHeightPx;
            VerticalFovDeg          = verticalFovDeg;
        }

        // ── Sync ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Syncs <see cref="UnityEngine.Camera.transform"/> from <paramref name="sys"/>'s current pose.
        /// Called by <see cref="MapView.UpdateFrame"/> after <see cref="CameraSystem.Advance"/>.
        ///
        /// <para>Converts Core <see cref="Double3"/> vectors to Unity <c>Vector3</c> and applies
        /// <c>Quaternion.LookRotation(fwd, up)</c>. Sets perspective, FOV, and clip planes.</para>
        /// </summary>
        public void Sync(CameraSystem sys) => Sync(sys, default);

        /// <summary>
        /// Camera-relative sync: positions the camera at the orbit pose PLUS the look-at's render-space
        /// ground position <paramref name="lookAtRenderOffset"/> = (lookAtMercator − sceneOrigin), mapped
        /// east→+X / north→+Z. This is what makes panning work: the look-at moves over the (stable,
        /// scene-origin-relative) tile field, so the camera tracks it every frame instead of being pinned
        /// to the render origin. It is rebase-invariant — camera-minus-tile = orbitOffset + lookAtMerc −
        /// tileMerc carries no sceneOrigin term, so a scene-origin rebase shifts camera and tiles together
        /// with no visible snap. Offset defaults to zero (look-at at the render origin) for callers/tests
        /// that don't supply a scene origin.
        /// </summary>
        public void Sync(CameraSystem sys, double2 lookAtRenderOffset)
        {
            if (_camera == null || sys == null) return;
            SyncFromProperties(sys.Current,
                               (double)ReferenceViewportHeightPx,
                               (double)VerticalFovDeg,
                               (double)AltitudeMultiplier,
                               lookAtRenderOffset);
        }

        /// <summary>
        /// Syncs the camera from explicit <see cref="CameraProperties"/> (called by tests via the
        /// static <see cref="ApplyCameraTransform"/> bridge-method on <see cref="MapController"/>).
        /// </summary>
        public void SyncFromProperties(CameraProperties props,
                                       double viewportHeightPx,
                                       double fovDeg,
                                       double altMultiplier = 1.0,
                                       double2 lookAtRenderOffset = default)
        {
            if (_camera == null) return;

            double altitude = CameraPoseMath.AltitudeForZoom(props.Zoom, viewportHeightPx, fovDeg)
                              * altMultiplier;
            if (altitude < 0.1) altitude = 0.1;

            CameraPoseMath.ComputePose(altitude,
                                       props.Heading,
                                       props.Tilt,
                                       out Double3 pos,
                                       out Double3 fwd,
                                       out Double3 up);

            // Convert to Unity types and apply.
            _camera.orthographic = false;
            _camera.fieldOfView  = (float)fovDeg;

            // Camera-relative: add the look-at's render-space ground position (east→+X, north→+Z) so the
            // camera tracks the panned look-at over the stable tile field. Zero offset = look-at at origin.
            _camera.transform.position = new Vector3(
                (float)(pos.X + lookAtRenderOffset.x),
                (float)pos.Y,
                (float)(pos.Z + lookAtRenderOffset.y));

            // LookRotation(forward, up): forward = direction the camera looks (toward look-at).
            // Core already orthogonalized up vs. fwd (Gram-Schmidt) so this is always valid.
            _camera.transform.rotation = Quaternion.LookRotation(
                new Vector3((float)fwd.X, (float)fwd.Y, (float)fwd.Z),
                new Vector3((float)up.X,  (float)up.Y,  (float)up.Z));

            // Clip planes: scale with altitude so world is not clipped at z2, precision OK at z16.
            _camera.nearClipPlane = Mathf.Max(0.1f, (float)CameraSystem.NearClip(altitude));
            _camera.farClipPlane  =                  (float)CameraSystem.FarClip(altitude);
        }
    }
}
