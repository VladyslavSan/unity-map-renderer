using System;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
// Alias, not a plain `using`: the namespace segment `Rendering` collides with a bare UnityEngine type in
// lookup — the CS0118 trap this repo documents at RenderLayerSet.cs.
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

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
        /// <para><b>The camera normalizes the WORLD, not the paint.</b> Dividing the altitude here is what
        /// makes ground geometry DPR-independent. A style's <c>px</c> values are LOGICAL px and are converted
        /// to their consumer's space exactly once, at <c>ZoomStyleApplier</c> via
        /// <see cref="MapRenderer.Core.View.DeviceScaling.LogicalToDevicePx"/> — never here, and never twice.</para>
        ///
        /// <para><b>Correction (S107) — the previous text was right when it was written.</b> This comment used
        /// to read <i>"PAINT is NOT DPI-scaled … never ÷DPR the paint path"</i>, and under S92 D1 that was
        /// TRUE: line width reached the shader through the <c>_MetersPerPixel</c> uniform, fed from
        /// <c>CameraPoseMath.MetersPerPixel(zoom)</c> — metres per LOGICAL pixel — so the altitude
        /// normalization alone carried the whole convention and a second division really would have
        /// double-applied. <c>d5406d40</c> deleted that uniform and resolved width against
        /// <c>_ScreenParams</c>, the PHYSICAL framebuffer, which silently rebased the entire line family from
        /// logical to device px. The claim outlived the code path it described, and stayed green because
        /// every test runs at DPR 1. Read it as a lesson about comments that name a mechanism, not a
        /// reversal of judgement.</para>
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
        /// World metres per DEVICE pixel at the look-at — the frame's view-independent ruler, MEASURED off
        /// this camera rather than assumed from a zoom formula. Pushed to the shader global
        /// <c>_MapFrameMetersPerDevicePixel</c> by <see cref="SyncToCamera"/>, where the line shader converts
        /// every <c>px</c>-valued width property with it.
        ///
        /// <para><b>The reference depth is the distance to the LOOK-AT</b>, which under camera-relative
        /// rendering is <c>|CameraRelativePosition|</c> — the orbit radius
        /// <see cref="CameraPoseMath.ComputeRelativePose"/> was handed. That depth and no other, because it is
        /// the only one the framing is defined at: <see cref="CameraPoseMath.AltitudeForZoom"/> frames the
        /// look-at and nothing else, so any other reference would make the constant disagree with the camera
        /// that produced it. It is also projection-agnostic (a distance and an angle — no Web-Mercator
        /// constant, no latitude) and constant under tilt, since the orbit radius is; tilt changes only where
        /// in the frame each depth lands.</para>
        ///
        /// <para><b><see cref="DevicePixelRatio"/> is absent on purpose, not by omission.</b>
        /// <see cref="ViewportPx"/> is already physical, and the ratio enters exactly once — through
        /// <see cref="ViewportLogicalPx"/> inside the altitude framing in <see cref="SyncToCamera"/>. Naming
        /// it a second time here is what would let the two halves disagree; this way they cannot, by
        /// construction. (Algebraically the result is <c>MetersPerPixel(zoom) · AltitudeMultiplier / dpr</c>
        /// whenever the 0.1 m altitude floor is not binding — identical to the zoom-formula push this
        /// replaced at the default multiplier of 1, and correct where that one silently was not.)</para>
        ///
        /// <para>The half-FOV goes through <see cref="Angle"/> rather than a bare <c>math.radians</c>: the
        /// degrees→radians conversion lives once, inside <c>Angle.cs</c>, and
        /// <see cref="CameraPoseMath.AltitudeForZoom"/> computes this identical quantity one property over
        /// with <c>Angle.FromDegrees(fov * 0.5).Radians</c>. Halving in DEGREES before the conversion, as it
        /// does, so the two are bit-identical and not merely equal.</para>
        ///
        /// <para>The <c>max(…, 1.0)</c> on the height is not arithmetic pedantry: this value is pushed into a
        /// PROCESS-wide shader global, a zero-height viewport would make it <c>+Inf</c>, and <c>+Inf</c> sails
        /// through the shader's <c>&gt; 1e-9</c> missing-push guard to size every line in the process. One
        /// device pixel is the smallest viewport that means anything, and the sibling
        /// <see cref="ViewportLogicalPx"/> already carries an equivalent unusable-input fallback.</para>
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
        /// the screen-space unit the label placement + coverage-cull passes measure in. One definition shared by
        /// its consumers (<c>SymbolLabelSubsystem.CurrentBatch</c>, <c>LabelPlacementSystem.Tick</c>, the altitude
        /// framing below, and <c>MapView.BuildTileSelectionConfig</c>'s framing viewport).
        /// <para>The division and its unusable-ratio fallback live in <see cref="DeviceScaling"/>, shared with the
        /// paint conversion — so an unconfigured ratio cannot frame the camera and scale the paint differently
        /// (S108 D9; before it, this member had no fallback at all and returned ±∞).</para></summary>
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
            // (raw from the camera); only this altitude term is normalized — and it reads the shared
            // ViewportLogicalPx rather than re-deriving it, so the altitude framing and the tile-cover
            // framing are one quantity (S108) and the unusable-ratio fallback is inherited, not repeated.
            double altitude = CameraPoseMath.AltitudeForZoom(CurrentProperties.Zoom, ViewportLogicalPx.y, CurrentProperties.VerticalFovDeg)
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

            // The frame's ruler, pushed as the LAST act of the commit — after CameraRelativePosition, which
            // MetresPerDevicePixel reads. Here rather than in RenderLayerSet.ApplyZoom because it is a CAMERA
            // quantity and this is the one site where the camera's actual scale is established; a render path
            // that builds a MapCamera therefore cannot forget it. (RenderLayerSet used to push a Web-Mercator
            // zoom formula that merely happened to agree at AltitudeMultiplier 1 — and any fixture that
            // rendered without calling ApplyZoom read whatever an earlier fixture had left in this PROCESS
            // global.)
            //
            // PROCESS state, and still shared: with N live MapViews the last SyncToCamera of the frame wins,
            // and now a MapCamera that is not the rendering camera writes it too. Same caveat as before,
            // slightly wider; a real fix is per-material or per-renderer state and is its own stage.
            Shader.SetGlobalFloat(ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel,
                                  (float)MetresPerDevicePixel);
        }
    }
}
