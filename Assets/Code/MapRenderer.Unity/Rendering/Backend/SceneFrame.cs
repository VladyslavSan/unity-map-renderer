using Unity.Mathematics;

namespace MapRenderer.Unity.Rendering.Backend
{
    /// <summary>
    /// The per-frame scene frame the render backends place tiles relative to — Level 2 of the two-level
    /// RTC (<see cref="MapRenderer.Unity.View.FloatingOrigin"/>). Bundles <see cref="SceneOriginRender"/> and
    /// <see cref="Rebase"/> so one camera-orbit pose works for both the plane and the globe. A backend places
    /// each tile at <c>FloatingOrigin.TileToSceneRebased(tileOriginRender, SceneOriginRender, Rebase)</c>
    /// with orientation <c>Rebase</c>; for Mercator this reduces to a translation-only placement.
    /// </summary>
    internal readonly struct SceneFrame
    {
        /// <summary>The look-at projected into render space — the point the camera orbits this frame.</summary>
        public double3 SceneOriginRender { get; init; }

        /// <summary>Render→look-at-local-ENU rotation (identity for Mercator); also each tile's orientation.
        /// <b>Always set it</b> — an object initializer that omits it yields the ZERO matrix, not the identity,
        /// which collapses every tile to the origin. <see cref="Mercator"/> is the identity-rebase shorthand.</summary>
        public float3x3 Rebase { get; init; }

        /// <summary>
        /// The camera's position relative to this frame's floating origin — <c>MapCamera.CameraRelativePosition</c>
        /// / <c>CameraPoseMath.ComputeRelativePose</c>'s <c>pos</c>; the horizon occluder and the camera share
        /// this frame. Zero on frames built without a camera pose (the many placement-only call sites, and
        /// <see cref="Mercator"/>) — they don't feed the symbol horizon cull, so leaving it unset is correct.
        /// </summary>
        public double3 CameraRelativePosition { get; init; }

        /// <summary>
        /// The identity-rebase frame for a planar Web-Mercator scene origin: render origin
        /// <c>(mercX, 0, mercZ)</c>, <c>Rebase = float3x3.identity</c>. This is the frame that makes the
        /// backends' rebased placement collapse to the <c>TileLocalToScene</c> translation.
        /// </summary>
        public static SceneFrame Mercator(double2 sceneOriginMerc)
            => new SceneFrame
            {
                SceneOriginRender = new double3(sceneOriginMerc.x, 0.0, sceneOriginMerc.y),
                Rebase            = float3x3.identity,
            };
    }
}
