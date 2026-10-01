using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>Sorts a <see cref="LoadedKey"/> list ascending by <see cref="TilePriority.Key"/>,
    /// reusing a scratch buffer across calls so steady-state sorting allocates nothing.</summary>
    internal sealed class TilePrioritySorter
    {
        /// <summary>Reused insertion-sort scratch for <see cref="Sort"/>'s priority keys, cleared and refilled each call.</summary>
        private readonly List<double> _keys = new(64);

        /// <summary>Sorts <paramref name="list"/> in place by priority key.</summary>
        public void Sort(List<LoadedKey> list, in TilePriorityContext ctx)
        {
            int n = list.Count;
            _keys.Clear();
            for (int i = 0; i < n; i++)
            {
                TileId tile = list[i].Tile;
                _keys.Add(TilePriority.Key(in tile, in ctx));
            }

            for (int i = 1; i < n; i++)
            {
                double    key  = _keys[i];
                LoadedKey item = list[i];
                int       j    = i - 1;
                while (j >= 0 && IsAfter(_keys[j], list[j], key, item))
                {
                    _keys[j + 1] = _keys[j];
                    list[j + 1]  = list[j];
                    j--;
                }

                _keys[j + 1] = key;
                list[j + 1]  = item;
            }
        }

        /// <summary>True iff (keyA, a) sorts strictly after (keyB, b) — smaller priority key first, then
        /// TileId (Z, X, Y), then Slot DESCENDING (mirrors <see cref="TilePriority.SortByPriority"/>'s tiebreak, extended for
        /// per-source Slot: a tile drawn from N sources can appear up to N times). The background slot is last, so it goes first.</summary>
        private static bool IsAfter(double keyA, LoadedKey a, double keyB, LoadedKey b)
        {
            if (keyA != keyB) return keyA > keyB;
            if (a.Tile.Z != b.Tile.Z) return a.Tile.Z > b.Tile.Z;
            if (a.Tile.X != b.Tile.X) return a.Tile.X > b.Tile.X;
            if (a.Tile.Y != b.Tile.Y) return a.Tile.Y > b.Tile.Y;
            return a.Slot < b.Slot;
        }
    }
}
