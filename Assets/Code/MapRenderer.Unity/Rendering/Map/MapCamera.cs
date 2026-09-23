using System;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;
using MapRenderer.Unity.View.Camera;
// Alias, not a plain `using`: the namespace segment `Rendering` collides with a bare UnityEngine type in
// lookup — the CS0118 trap this repo documents at RenderLayerSet.cs.
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

namespace MapRenderer.Unity.Rendering.Map
{
    /// <summary>
    /// The map camera — a wrapper around a non-null <see cref="UnityEngine.Camera"/> holding the current
    /// <see cref="CameraProperties"/> and driving the camera transform from them.
    /// <see cref="Apply"/>/<see cref="SetProperties"/> only update <see cref="CurrentProperties"/>;
    /// <see cref="SyncToCamera"/> propagates it to the Unity camera once per frame — a Unity-camera-matrix
    /// reader (e.g. symbol screen-space placement) must run AFTER it in the same <c>MapView.LateUpdate</c>.
    /// </summary>
    public sealed class MapCamera
    {
        // ── Wrapped Unity camera (never null — ctor-enforced) ───────────────────────────────────────
        public readonly UnityEngine.Camera Camera;

        // ── Active projection (default WebMercator; injectable for tests and the globe) ───────────────
        public IProjection Projection { get; }

        /// <summary>Far-plane policy — set by MapView from config so the render far matches the tile selector's
        /// (a mismatch over/under-selects). Defaults to the planar geometry-aware far.</summary>
        internal IFarPlanePolicy FarPlanePolicy { get; set; } = new GeometryAwareFarPlane();

        /// <summary>Altitude multiplier for art-direction (default 1).</summary>
        public readonly float AltitudeMultiplier;

        /// <summary>
        /// Device pixel ratio (physical ÷ logical px). The altitude is framed from the logical viewport
        /// (<see cref="ViewportPx"/>.y ÷ this), so ground geometry renders at a DPR-independent size. Default 1,
        /// refreshed live from <c>MapViewComponent.Config.DevicePixelRatio</c> before
        /// <see cref="SyncToCamera"/>. Non-local invariant: a style's <c>px</c> values convert to device
        /// space exactly once, at <c>ZoomStyleApplier</c> — never here.
        /// </summary>
        public double DevicePixelRatio;

        /// <summary>
        /// The camera's position relative to the floating origin (== <see cref="CameraPoseMath.ComputeRelativePose"/>'s
        /// <c>pos</c>, computed each <see cref="SyncToCamera"/>). The single owner of this value —
        /// <c>MapView.BuildSceneFrame</c> folds it into <c>SceneFrame</c>, never a <c>transform.position</c>
        /// round-trip.
        /// </summary>
        public double3 CameraRelativePosition { get; private set; }

        /// <summary>
        /// World metres per DEVICE pixel at the look-at — pushed to the shader global
        /// <c>_MapFrameMetersPerDevicePixel</c> by <see cref="SyncToCamera"/>. The reference depth and the absent
        /// DPR are in docs/line-rendering-design.md § "Where the constant comes from — the camera, measured".
        /// Non-local invariants: the half-FOV halves in DEGREES before the <see cref="Angle"/> conversion, as
        /// <see cref="CameraPoseMath.AltitudeForZoom"/> does, so the two are bit-identical; the 1 device px
        /// height floor keeps a zero-height viewport from pushing <c>+Inf</c> into the process-wide shader global.
        /// </summary>
        public double MetresPerDevicePixel =>
            2.0 * math.length(CameraRelativePosition)
                * math.tan(Angle.FromDegrees(CurrentProperties.VerticalFovDeg * 0.5).Radians)
                / math.max(ViewportPx.y, 1.0);

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
        /// the screen-space unit the symbol placement + coverage-cull passes measure in. One definition shared by
        /// its consumers (<c>SymbolSubsystem.CurrentBatch</c>, <c>SymbolPlacementSystem.Tick</c>, the altitude
        /// framing below, and <c>MapView.BuildTileSelectionConfig</c>'s framing viewport).
        /// <para>The division and its unusable-ratio fallback live in <see cref="DeviceScaling"/>, shared with the
        /// paint conversion — so an unconfigured ratio cannot frame the camera and scale the paint differently
        /// — see <see cref="DeviceScaling"/>.</para></summary>
        public double2 ViewportLogicalPx => DeviceScaling.DeviceToLogicalPx(ViewportPx, DevicePixelRatio);

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

        /// <summary>The camera orbit altitude in render metres for the current properties+viewport — the LOGICAL
        /// viewport height (÷DPR brings the camera ~DPR× closer on a high-DPI panel, for DPR-independent size),
        /// the <see cref="AltitudeMultiplier"/>, and a 0.1 m floor. Computed on demand so readers do not depend on
        /// a <see cref="SyncToCamera"/> having run; <see cref="SyncToCamera"/> reads the same property.</summary>
        internal double CurrentAltitudeMetres
        {
            get
            {
                double altitude = CameraPoseMath.AltitudeForZoom(
                    CurrentProperties.Zoom, ViewportLogicalPx.y, CurrentProperties.VerticalFovDeg) * AltitudeMultiplier;
                return altitude < 0.1 ? 0.1 : altitude;
            }
        }

        /// <summary>The camera far-clip distance in render metres — the injected <see cref="FarPlanePolicy"/> over
        /// <see cref="CurrentAltitudeMetres"/> and the current tilt/FOV/aspect. This is the SAME value
        /// <see cref="SyncToCamera"/> writes to <c>Camera.farClipPlane</c>, but computed from the properties on
        /// demand, so a reader (the symbol far-distance cull) gets the correct far even when no SyncToCamera has run
        /// this frame — the raw <c>Camera.farClipPlane</c> would still hold Unity's default until then.</summary>
        internal double CurrentFarMetres => FarPlanePolicy.FarMetres(
            CurrentAltitudeMetres, CurrentProperties.Tilt.Value, CurrentProperties.VerticalFovDeg, Camera.aspect);

        /// <summary>
        /// Propagate <see cref="CurrentProperties"/> to the wrapped Unity camera (transform + FOV + clip) —
        /// the single per-frame commit, the first step of <c>MapView.LateUpdate</c>. Idempotent: pushing the same
        /// state twice is harmless (no dirty tracking). This is the "camera committed for this frame" point —
        /// any Unity-camera-matrix consumer (symbol screen-space placement) must run AFTER it, later in the same
        /// <c>MapView.LateUpdate</c>.
        /// </summary>
        public void SyncToCamera()
        {
            double altitude = CurrentAltitudeMetres;

            CameraPoseMath.ComputeRelativePose(altitude,
                                       CurrentProperties.Heading.Value,
                                       CurrentProperties.Tilt.Value,
                                       out double3 pos,
                                       out double3 fwd,
                                       out double3 up);

            // Single owner: store the relative pose BEFORE pushing it to the transform, so every reader
            // gets the same value — never a transform.position round-trip.
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

            Camera.nearClipPlane = math.max(0.1f, (float)CameraPoseMath.NearClip(altitude));
            // The injected far policy (shared with the tile selector) uses identical inputs per projection,
            // so render far == selection far; CurrentFarMetres exposes the same value to the symbol cull.
            Camera.farClipPlane  = (float)CurrentFarMetres;

            // The frame's ruler, pushed as the LAST act of the commit, after CameraRelativePosition, which
            // MetresPerDevicePixel reads. Pushed here, not in RenderLayerSet.ApplyZoom, because it is a
            // CAMERA quantity established at this one site — a render path that builds a MapCamera cannot
            // forget it. Limitation no test can observe: this is PROCESS-global shader state, so with N
            // live MapViews the last SyncToCamera of the frame wins, including one not doing the rendering.
            Shader.SetGlobalFloat(ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel,
                                  (float)MetresPerDevicePixel);
        }
    }
}
