// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references.

using System.Collections.Generic;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// The algorithm-agnostic seam for "which tiles does the camera see", called once per frame
    /// (<see cref="FrustumTileSelector"/> by default). Non-local invariant: it stays generic, so tuning
    /// knobs belong on an implementation's constructor, never here or on <see cref="ViewContext"/>; the
    /// set may mix zoom levels (each <see cref="TileId"/> has its own <c>Z</c>), so a distance-LOD selector
    /// drops in; it returns only the sets, and the consumer owns transitions and parent/child provenance.
    /// An implementation may keep the last cover to damp flicker, so one instance serves one camera.
    /// </summary>
    public interface IVisibleTileSelector
    {
        /// <summary>
        /// Called once per frame. Clears <paramref name="selection"/> and refills it: the tiles the camera currently
        /// sees, and the tiles to prepare and keep ahead of a level switch. Allocation-free in steady state
        /// (no LINQ, no per-call heap allocation).
        /// </summary>
        /// <param name="view">Per-frame view context (camera pose + framing viewport + projection).</param>
        /// <param name="selection">Caller-owned result. Cleared then refilled.</param>
        void SelectVisibleTiles(in ViewContext view, TileSelection selection);
    }
}
