using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// A neutral <c>(source, tile)</c> membership record handed out by
    /// <see cref="TileManager.CollectLoadedTileKeys"/> so a consumer (the symbol subsystem) can PULL the
    /// currently-loaded tile set each frame and reconcile against it, instead of being pushed fragile
    /// release/restore lifecycle callbacks. A plain data DTO — carries no behaviour and no reference to the
    /// tile pipeline, so depending on it does not couple the consumer to <see cref="TileManager"/>'s internals.
    /// </summary>
    internal readonly struct LoadedTileKey
    {
        public readonly string SourceId;
        public readonly TileId Tile;

        /// <summary>True while the record serves a revealed tile. A loaded record that does not keeps its symbols warm, uncollected.</summary>
        public readonly bool Shown;

        public LoadedTileKey(string sourceId, TileId tile, bool shown) { SourceId = sourceId; Tile = tile; Shown = shown; }
    }
}
