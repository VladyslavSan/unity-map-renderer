using System.Collections.Generic;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;

namespace MapRenderer.Unity.Rendering.Tile
{
    /// <summary>Sorts a <see cref="TileManager.LoadedKey"/> list ascending by <see cref="TilePriority.Key"/>,
    /// reusing a scratch buffer across calls so steady-state sorting allocates nothing.</summary>
    internal sealed class TilePrioritySorter
    {
        /// <summary>Reused insertion-sort scratch for <see cref="Sort"/>'s priority keys — grown, never shrunk.</summary>
        private double[] _keys = new double[64];

        /// <summary>Sorts <paramref name="list"/> in place by priority key.</summary>
        public void Sort(List<TileManager.LoadedKey> list, in TilePriorityContext ctx)
        {
            int n = list.Count;
            if (_keys.Length < n)
                _keys = new double[math.max(n, _keys.Length * 2)];

            double[] keys = _keys;
            for (int i = 0; i < n; i++)
            {
                TileId tile = list[i].Tile;
                keys[i] = TilePriority.Key(in tile, in ctx);
            }

            for (int i = 1; i < n; i++)
            {
                double                 k    = keys[i];
                TileManager.LoadedKey  item = list[i];
                int                    j    = i - 1;
                while (j >= 0 && IsAfter(keys[j], list[j], k, item))
                {
                    keys[j + 1] = keys[j];
                    list[j + 1] = list[j];
                    j--;
                }

                keys[j + 1] = k;
                list[j + 1] = item;
            }
        }

        /// <summary>True iff (keyA, a) sorts strictly after (keyB, b) — smaller priority key first, then
        /// TileId (Z, X, Y), then Slot (mirrors <see cref="TilePriority.SortByPriority"/>'s tiebreak,
        /// extended for per-source Slot: a tile drawn from N sources can appear up to N times).</summary>
        private static bool IsAfter(double keyA, TileManager.LoadedKey a, double keyB, TileManager.LoadedKey b)
        {
            if (keyA != keyB) return keyA > keyB;
            if (a.Tile.Z != b.Tile.Z) return a.Tile.Z > b.Tile.Z;
            if (a.Tile.X != b.Tile.X) return a.Tile.X > b.Tile.X;
            if (a.Tile.Y != b.Tile.Y) return a.Tile.Y > b.Tile.Y;
            return a.Slot > b.Slot;
        }
    }
}
