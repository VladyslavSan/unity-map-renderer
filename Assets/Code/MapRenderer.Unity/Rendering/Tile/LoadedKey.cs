using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>Composite key for the multi-source loaded table — value-type + <see cref="System.IEquatable{T}"/> avoids boxing on every Dictionary probe.</summary>
    internal readonly struct LoadedKey : System.IEquatable<LoadedKey>
    {
        public readonly TileId Tile;
        public readonly int    Slot;

        public LoadedKey(TileId tile, int slot)
        {
            Tile = tile;
            Slot = slot;
        }

        public          bool Equals(LoadedKey other) => Slot == other.Slot && Tile.Equals(other.Tile);
        public override bool Equals(object    obj)   => obj is LoadedKey o && Equals(o);

        public override int GetHashCode()
        {
            unchecked
            {
                return Tile.GetHashCode() * 31 + Slot;
            }
        }
    }
}
