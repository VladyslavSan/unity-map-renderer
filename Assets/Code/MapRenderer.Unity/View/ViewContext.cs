// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — Unity.Mathematics + IProjection only.

using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// Per-frame <b>view context</b>: the camera pose, the framing viewport and the active pixel↔ground
    /// projection, shared by <see cref="IVisibleTileSelector"/> and the camera-interaction ops. It is view
    /// STATE, never an algorithm knob; tuning constants live on the concrete selector's constructor.
    /// Passed by <c>in</c>. It holds a managed <see cref="IProjection"/>, so it is not blittable.
    /// </summary>
    public readonly struct ViewContext
    {
        /// <summary>Camera pose: look-at, zoom, heading, tilt.</summary>
        public CameraProperties Camera { get; init; }

        /// <summary>
        /// The viewport size in pixels, in the scale each consumer needs. The camera-interaction seam uses
        /// the live viewport (<c>Camera.pixelWidth, Camera.pixelHeight</c>), which must share the cursor
        /// pixel scale so the pin invariants hold. The tile-selection seam uses the framing viewport
        /// (<c>MapCamera.ViewportPx</c>; see <see cref="IVisibleTileSelector"/>).
        /// </summary>
        public double2 ViewportPx { get; init; }

        /// <summary>The active pixel↔ground projection service (Web-Mercator or globe).</summary>
        public IProjection Projection { get; init; }
    }
}
