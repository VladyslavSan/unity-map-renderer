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
    /// An implementation may keep the last cover to damp flicker, so one instance serves one camera.
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

        /// <summary>The target level the last <see cref="SelectVisibleTiles"/> call used, or a <see cref="TargetLevel.Level"/> of -1
        /// before the first. <see cref="TileManager"/> reads it to derive the tiles to prepare ahead of a level switch.</summary>
        TargetLevel LastTarget { get; }
    }

    /// <summary>The near-field level of one selection and the zoom it came from.</summary>
    public readonly struct TargetLevel
    {
        /// <summary>The level in use, held inside the zoom-level hysteresis (not the floor of the camera zoom), or -1
        /// when nothing was selected yet. Level 0 is a real level.</summary>
        public int Level { get; init; }

        /// <summary>The continuous level the camera asked for: camera zoom plus the selection offset.</summary>
        public double Continuous { get; init; }

        /// <summary>The selector's lower clamp for <see cref="Level"/>.</summary>
        public int MinLevel { get; init; }

        /// <summary>The selector's upper clamp for <see cref="Level"/>.</summary>
        public int MaxLevel { get; init; }

    }
}
