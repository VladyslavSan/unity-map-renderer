// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — Unity.Mathematics + IProjection only.

using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// Per-frame <b>view context</b> — everything that describes "what the camera sees right now": the
    /// camera pose, the framing viewport, and the active pixel↔ground projection. Bundled into one carrier
    /// so the per-frame seams that consume it (<see cref="IVisibleTileSelector"/> for tile selection, and
    /// the camera-interaction ops) share a single type rather than three loose parameters. Not algorithm
    /// config: this is view STATE (what the camera sees), never an algorithm knob (how tiles are chosen) —
    /// tuning constants live on the concrete selector's constructor, not here. <see cref="Projection"/> is
    /// the swappable pixel↔ground service (Web-Mercator or globe); it belongs with the per-frame view
    /// inputs because it changes at runtime and every selector needs it. Passed by <c>in</c>: it
    /// carries a managed <see cref="IProjection"/> reference, so it is a small managed carrier for this
    /// once-per-frame seam, NOT a Burst/blittable struct.
    /// </summary>
    public readonly struct ViewContext
    {
        /// <summary>Camera pose: look-at, zoom, heading, tilt.</summary>
        public CameraProperties Camera { get; init; }

        /// <summary>
        /// The viewport size in pixels. Usage depends on the consumer:
        /// <list type="bullet">
        ///   <item>Camera-interaction seam — the LIVE interaction viewport
        ///     (<c>Camera.pixelWidth, Camera.pixelHeight</c>); must share the cursor positions' pixel scale
        ///     so the pin invariants hold.</item>
        ///   <item>Tile-selection seam — the framing viewport, the camera's live pixel size
        ///     (<c>MapCamera.ViewportPx</c>); the camera IS the viewport
        ///     (see <see cref="IVisibleTileSelector"/>).</item>
        /// </list>
        /// The type is usage-neutral; each consumer fills this field with the appropriate scale.
        /// </summary>
        public double2 ViewportPx { get; init; }

        /// <summary>The active pixel↔ground projection service (Web-Mercator or globe).</summary>
        public IProjection Projection { get; init; }
    }
}
