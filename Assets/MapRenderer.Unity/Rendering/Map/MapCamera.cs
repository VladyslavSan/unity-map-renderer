using System;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View.Camera;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// The map camera — a neat wrapper around a <b>non-null</b> <see cref="UnityEngine.Camera"/> that holds
    /// the current <see cref="CameraProperties"/> and drives the camera transform from them. Correct by
    /// construction: the wrapped camera is never null, so there is no fallback path for aspect / viewport /
    /// pose. (S89: absorbed the former Core <c>CameraSystem</c>; there is no separate model↔binding split.)
    ///
    /// <para><b>Control is instant.</b> <see cref="Apply"/> merges a <see cref="CameraPropertiesUpdate"/>
    /// patch over the current state and re-drives the transform. Smooth, animated, per-property control is
    /// the job of a separate <c>CameraController</c> (future), layered on top of this.</para>
    ///
    /// <para><b>Camera-relative rendering (S52):</b> the scene origin tracks the look-at, so the camera
    /// orbits the origin and its transform is a pure function of <see cref="CameraProperties"/> — recomputed
    /// on <see cref="Apply"/>, not per frame. At tilt=0 the camera sits directly above the look-at looking
    /// straight down; tilt&gt;0 orbits toward the horizon; heading rotates in the horizontal plane. Clip
    /// planes scale with altitude (near = altitude·0.01 min 0.1; far = altitude·4).</para>
    ///
    /// <para><b>Framing:</b> the zoom→altitude formula uses the camera's <b>live</b> pixel height
    /// (<see cref="ViewportPx"/>.y) so a given zoom renders tiles at their native resolution on any window
    /// size (slippy-map convention). The FOV lens is camera <b>state</b>
    /// (<see cref="CameraProperties.VerticalFovDeg"/>) — a genuine parameter, not a measurement — pushed to
    /// the Unity camera on each sync; the viewport height comes from the camera, not from side config.</para>
    ///
    /// <para>Not a MonoBehaviour.</para>
    /// </summary>
    public sealed class MapCamera
    {
        // ── Wrapped Unity camera (never null — ctor-enforced) ───────────────────────────────────────
        private readonly UnityEngine.Camera _camera;

        // ── Active projection (S63: default WebMercator; injectable for tests / future globe) ─────────
        public IProjection Projection { get; }

        /// <summary>Altitude multiplier for art-direction (default 1).</summary>
        public readonly float AltitudeMultiplier;

        // ── Current state (includes the FOV lens — CameraProperties.VerticalFovDeg) ──────────────────
        private CameraProperties _current;

        public MapCamera(UnityEngine.Camera camera,
                         CameraProperties initial,
                         float       altitudeMultiplier = 1f,
                         IProjection projection         = null)
        {
            _camera = camera != null ? camera
                : throw new ArgumentNullException(nameof(camera), "MapCamera requires a real UnityEngine.Camera.");
            _current           = initial;
            AltitudeMultiplier = altitudeMultiplier;
            Projection         = projection ?? new WebMercatorProjection();
            SyncTransform();
        }

        /// <summary>The current camera properties.</summary>
        public CameraProperties CurrentProperties => _current;

        /// <summary>Live viewport size in pixels, straight from the wrapped camera (the camera IS the
        /// viewport — no fallback).</summary>
        public double2 ViewportPx => new double2(_camera.pixelWidth, _camera.pixelHeight);

        /// <summary>
        /// Merge <paramref name="update"/> over the current properties and re-drive the transform (instant).
        /// An empty patch still re-syncs the transform (cheap, pure function of the props).
        /// </summary>
        public void Apply(CameraPropertiesUpdate update)
        {
            _current = update.ApplyTo(_current);
            SyncTransform();
        }

        /// <summary>Jump the camera to an absolute <paramref name="props"/> state and re-drive the transform.
        /// The absolute counterpart to <see cref="Apply"/> (used to seed the initial view).</summary>
        public void SetProperties(CameraProperties props)
        {
            _current = props;
            SyncTransform();
        }

        // ── Drive the Unity camera transform from the current props ─────────────────────────────────
        private void SyncTransform()
        {
            double altitude = CameraPoseMath.AltitudeForZoom(_current.Zoom, ViewportPx.y, _current.VerticalFovDeg)
                              * AltitudeMultiplier;
            if (altitude < 0.1) altitude = 0.1;

            CameraPoseMath.ComputePose(altitude,
                                       _current.Heading.Value,
                                       _current.Tilt.Value,
                                       out double3 pos,
                                       out double3 fwd,
                                       out double3 up);

            _camera.orthographic = false;
            _camera.fieldOfView  = (float)_current.VerticalFovDeg;

            // Orbit pose around the render origin (the look-at sits at origin under camera-relative
            // rendering, so there is no scene-origin term here).
            _camera.transform.position = new Vector3((float)pos.x, (float)pos.y, (float)pos.z);

            // LookRotation(forward, up): Core already orthogonalized up vs. fwd (closed-form, unit-length).
            _camera.transform.rotation = Quaternion.LookRotation(
                new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                new Vector3((float)up.x,  (float)up.y,  (float)up.z));

            _camera.nearClipPlane = Mathf.Max(0.1f, (float)CameraPoseMath.NearClip(altitude));
            _camera.farClipPlane  =                  (float)CameraPoseMath.FarClip(altitude);
        }
    }
}
