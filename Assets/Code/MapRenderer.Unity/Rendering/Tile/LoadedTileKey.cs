using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>
    /// A-1 pull surface: a neutral <c>(source, tile)</c> membership record handed out by
    /// <see cref="TileManager.CollectLoadedTileKeys"/> so a consumer (the symbol-symbol subsystem) can PULL the
    /// currently-loaded tile set each frame and reconcile against it, instead of being pushed fragile
    /// release/restore lifecycle callbacks. A plain data DTO — carries no behaviour and no reference to the
    /// tile pipeline, so depending on it does not couple the consumer to <see cref="TileManager"/>'s internals.
    /// </summary>
    internal readonly struct LoadedTileKey
    {
        public readonly string SourceId;
        public readonly TileId Tile;
        public LoadedTileKey(string sourceId, TileId tile) { SourceId = sourceId; Tile = tile; }
    }
}
