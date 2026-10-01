// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references.

using System.Collections.Generic;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// The tile sets one <see cref="IVisibleTileSelector"/> call produces. The consumer owns it and the selector
    /// refills it every call. <see cref="Cover"/> is what the camera sees. <see cref="Preload"/> and
    /// <see cref="Keep"/> are tiles outside the cover that a small zoom change would put in it.
    /// </summary>
    public sealed class TileSelection
    {
        /// <summary>The tiles the camera sees now. The set may mix zoom levels.</summary>
        public List<TileId> Cover { get; } = new List<TileId>(64);

        /// <summary>Tiles outside <see cref="Cover"/> to prepare, because the zoom is within the preload
        /// lead of showing them.</summary>
        public List<TileId> Preload { get; } = new List<TileId>(64);

        /// <summary>Tiles outside <see cref="Cover"/> to keep once prepared: the preload lead plus the
        /// zoom-level hysteresis.</summary>
        public List<TileId> Keep { get; } = new List<TileId>(64);

        /// <summary>Empties the three sets and keeps their capacity.</summary>
        public void Clear()
        {
            Cover.Clear();
            Preload.Clear();
            Keep.Clear();
        }
    }
}
