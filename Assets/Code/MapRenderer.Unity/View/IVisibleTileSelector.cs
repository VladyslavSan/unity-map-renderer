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
    /// drops in; it returns only the set, and the consumer owns transitions and parent/child provenance.
    /// </summary>
    public interface IVisibleTileSelector
    {
        /// <summary>
        /// Called once per frame. Clears <paramref name="reuseBuffer"/> and refills it with the tiles the
        /// camera currently sees. Allocation-free in steady state (no LINQ, no per-call heap allocation).
        /// </summary>
        /// <param name="view">Per-frame view context (camera pose + framing viewport + projection).</param>
        /// <param name="reuseBuffer">Caller-owned result buffer. Cleared then refilled.</param>
        void SelectVisibleTiles(in ViewContext view, List<TileId> reuseBuffer);
    }
}
