using MapRenderer.Core.Geo;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>Packs a tile address into a stable, unique <c>long</c> (z in the high bits, then y, then x) —
    /// an opaque S20 tiebreak key, not a coordinate. Valid for z ≤ 19 (x,y &lt; 2^22).
    /// <para>Lifted out of <c>SymbolFeatureExtractor</c> (tile-geometry IR B4): it is a tile-key codec, not
    /// symbol extraction, and Core consumers (<see cref="LabelTileCoverageFilter"/>) still need it after the
    /// extractor moved to <c>MapRenderer.Unity.Text</c>.</para></summary>
    public static class LabelTileKey
    {
        /// <summary>Packs a tile address into a stable, unique <c>long</c> (z in the high bits, then y, then
        /// x) — an opaque S20 tiebreak key, not a coordinate. Valid for z ≤ 19 (x,y &lt; 2^22).</summary>
        public static long Pack(in TileId tile)
        {
            return ((long)tile.Z << 44) | ((long)(tile.Y & 0x3FFFFF) << 22) | (long)(tile.X & 0x3FFFFF);
        }

        /// <summary>Inverse of <see cref="Pack"/>: unpacks a tile key back to its <see cref="TileId"/>
        /// (z/x/y). Used by the label tile-coverage pre-cull to recover a tile's corners from a batch record.</summary>
        public static TileId Unpack(long key)
        {
            return new TileId { Z = (int)(key >> 44), Y = (int)((key >> 22) & 0x3FFFFF), X = (int)(key & 0x3FFFFF) };
        }
    }
}
