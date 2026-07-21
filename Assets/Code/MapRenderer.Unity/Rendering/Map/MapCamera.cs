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
    /// <para><b>State vs. propagation are separated.</b> <see cref="Apply"/> / <see cref="SetProperties"/>
    /// only mutate <see cref="CurrentProperties"/> — the live "most recent state" — and do NOT touch the Unity
    /// camera. The transform is propagated ONCE per frame by <see cref="SyncToCamera"/> (the first step of
    /// <c>MapView.LateUpdate</c>), so many setters in a frame collapse to a single commit from the final merged
    /// state. <see cref="SyncToCamera"/> is the "camera committed for this frame" point: anything reading the
    /// Unity camera matrix (e.g. label screen-space placement) MUST run AFTER it, sequenced in the same
    /// <c>MapView.LateUpdate</c> — which is exactly the ordered camera→tiles→labels pipeline there. Smooth,
    /// animated control is a separate <c>CameraController</c> (future), layered on top.</para>
    ///
    /// <para><b>Camera-relative rendering (S52):</b> the scene origin tracks the look-at, so the camera
    /// orbits the origin and its transform is a pure function of <see cref="CameraProperties"/> — recomputed
    /// once per frame at the commit. At tilt=0 the camera sits directly above the look-at looking
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
        public readonly UnityEngine.Camera Camera;

        // ── Active projection (S63: default WebMercator; injectable for tests / future globe) ─────────
        public IProjection Projection { get; }

        /// <summary>Far-plane policy — set by MapView from config so the render far matches the tile selector's
        /// (a mismatch over/under-selects). Defaults to the planar geometry-aware far.</summary>
        internal IFarPlanePolicy FarPlanePolicy { get; set; } = new GeometryAwareFarPlane();

        /// <summary>Altitude multiplier for art-direction (default 1).</summary>
        public readonly float AltitudeMultiplier;

        /// <summary>
        /// Device pixel ratio (physical ÷ logical px, S92 D1). The altitude is framed from the <b>logical</b>
        /// viewport (<see cref="ViewportPx"/>.y ÷ this) so the map is the right size — and DPR-independent — on
        /// a high-DPI panel, matching the tile selector's logical framing. Default 1; refreshed live from
        /// <c>MapViewComponent.Config.DevicePixelRatio</c> each frame before <see cref="SyncToCamera"/>.
        ///
        /// <para><b>PAINT is NOT DPI-scaled</b> (this is the crux — S92 D1): only the altitude term divides by
        /// DPR. A <c>stylePx</c>-wide line still renders <c>stylePx</c> logical px because the DPI-normalized
        /// camera makes ground-per-physical-pixel <c>mpp/DPR</c>. Never <c>÷DPR</c> the paint path — that
        /// double-applies (the <c>GroundResolution</c>/<c>MetersPerPixel</c> freeze).</para>
        /// </summary>
        public double DevicePixelRatio;

        /// <summary>
        /// The camera's position relative to the floating origin (== <see cref="CameraPoseMath.ComputeRelativePose"/>'s
        /// <c>pos</c>, computed each <see cref="SyncToCamera"/>). The single owner of this value —
        /// <c>MapView.BuildSceneFrame</c> folds it into <c>SceneFrame</c>, never a <c>transform.position</c>
        /// round-trip.
        /// </summary>
        public double3 CameraRelativePosition { get; private set; }

        public MapCamera(UnityEngine.Camera camera,
                         CameraProperties initial,
                         float       altitudeMultiplier = 1f,
                         IProjection projection         = null,
                         double      devicePixelRatio   = 1.0)
        {
            Camera = camera != null ? camera
                : throw new ArgumentNullException(nameof(camera), "MapCamera requires a real UnityEngine.Camera.");
            CurrentProperties           = initial;
            AltitudeMultiplier = altitudeMultiplier;
            Projection         = projection ?? new WebMercatorProjection();
            DevicePixelRatio   = devicePixelRatio;
            SyncToCamera(); // seed the transform at construction so frame-0 is valid before the first commit
        }

        /// <summary>The current camera properties.</summary>
        public CameraProperties CurrentProperties { get; private set; }

        /// <summary>Live viewport size in pixels, straight from the wrapped camera (the camera IS the
        /// viewport — no fallback).</summary>
        public double2 ViewportPx => new double2(Camera.pixelWidth, Camera.pixelHeight);

        /// <summary>Logical (DPR-normalized) viewport size — <see cref="ViewportPx"/> ÷ <see cref="DevicePixelRatio"/>,
        /// the screen-space unit the label placement + coverage-cull passes measure in. One definition shared by
        /// both consumers (<c>SymbolLabelSubsystem.CurrentBatch</c> and <c>LabelPlacementSystem.Tick</c>).</summary>
        public double2 ViewportLogicalPx => ViewportPx / DevicePixelRatio;

        /// <summary>
        /// Merge <paramref name="update"/> over the current properties. Updates <see cref="CurrentProperties"/>
        /// immediately; does NOT touch the Unity camera — the transform is propagated once per frame by
        /// <see cref="SyncToCamera"/>.
        /// </summary>
        public void Apply(CameraPropertiesUpdate update)
        {
            CurrentProperties = update.ApplyTo(CurrentProperties);
        }

        /// <summary>Jump the current properties to an absolute <paramref name="props"/> state (the absolute
        /// counterpart to <see cref="Apply"/>). Updates <see cref="CurrentProperties"/> immediately; the Unity
        /// camera is propagated once per frame by <see cref="SyncToCamera"/>.</summary>
        public void SetProperties(CameraProperties props)
        {
            CurrentProperties = props;
        }

        /// <summary>
        /// Propagate <see cref="CurrentProperties"/> to the wrapped Unity camera (transform + FOV + clip) —
        /// the single per-frame commit, the first step of <c>MapView.LateUpdate</c>. Idempotent: pushing the same
        /// state twice is harmless (no dirty tracking). This is the "camera committed for this frame" point —
        /// any Unity-camera-matrix consumer (label screen-space placement) must run AFTER it, later in the same
        /// <c>MapView.LateUpdate</c>.
        /// </summary>
        public void SyncToCamera()
        {
            // Frame from the LOGICAL viewport height (S92 D1): ÷DPR brings the camera ~DPR× closer on a
            // high-DPI panel so the map is the right size and DPR-independent. ViewportPx stays physical
            // (raw from the camera); only this altitude term is normalized. Guard a non-positive DPR → 1.
            double dpr      = DevicePixelRatio > 0.0 ? DevicePixelRatio : 1.0;
            double altitude = CameraPoseMath.AltitudeForZoom(CurrentProperties.Zoom, ViewportPx.y / dpr, CurrentProperties.VerticalFovDeg)
                              * AltitudeMultiplier;
            if (altitude < 0.1) altitude = 0.1;

            CameraPoseMath.ComputeRelativePose(altitude,
                                       CurrentProperties.Heading.Value,
                                       CurrentProperties.Tilt.Value,
                                       out double3 pos,
                                       out double3 fwd,
                                       out double3 up);

            // Single owner: store the relative pose BEFORE pushing it to the transform, so every reader
            // (MapView.BuildSceneFrame included) takes the same value the transform gets — never a
            // transform.position round-trip.
            CameraRelativePosition = pos;

            Camera.orthographic = false;
            Camera.fieldOfView  = (float)CurrentProperties.VerticalFovDeg;

            // Orbit pose around the render origin (the look-at sits at origin under camera-relative
            // rendering, so there is no scene-origin term here).
            Camera.transform.position = new Vector3((float)pos.x, (float)pos.y, (float)pos.z);

            // LookRotation(forward, up): Core already orthogonalized up vs. fwd (closed-form, unit-length).
            Camera.transform.rotation = Quaternion.LookRotation(
                new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                new Vector3((float)up.x,  (float)up.y,  (float)up.z));

            Camera.nearClipPlane = Mathf.Max(0.1f, (float)CameraPoseMath.NearClip(altitude));
            // The injected far policy (shared with the tile selector, per projection) — geometry-aware for the
            // flat atlas, ray-sphere for the globe. Both use identical inputs, so render far == selection far.
            Camera.farClipPlane  = (float)FarPlanePolicy.FarMetres(
                altitude, CurrentProperties.Tilt.Value, CurrentProperties.VerticalFovDeg, Camera.aspect);
        }
    }
}
