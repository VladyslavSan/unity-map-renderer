// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — TileId (Core.Geo) + collections only.

using System.Collections.Generic;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// Pure derivation of the near-field grid (<c>Columns</c>/<c>Rows</c>) and zoom span (<c>MinZ</c>/<c>MaxZ</c>)
    /// of a <see cref="FrustumTileSelector"/> cover, from its <see cref="TileId.Z"/> values.
    /// Columns/Rows count distinct X/Y at <c>MaxZ</c> only, because a mixed-zoom cover mixes z and z−1 index
    /// spaces. A <c>max(X) − min(X) + 1</c> shortcut breaks on an antimeridian-wrapped cover
    /// (<c>{0, 1, n-2, n-1}</c>).
    /// </summary>
    public static class TileCoverStats
    {
        /// <summary>
        /// Computes cover dims + zoom span with zero per-call allocation. <paramref name="distinctX"/> /
        /// <paramref name="distinctY"/> are caller-owned reused buffers (cleared and refilled here) — the
        /// steady-state no-GC contract; the caller reuses the same two sets across ticks/captures.
        /// </summary>
        public static (int Columns, int Rows, int MinZ, int MaxZ) Compute(
            IReadOnlyList<TileId> cover, HashSet<int> distinctX, HashSet<int> distinctY)
        {
            if (cover.Count == 0) return (0, 0, 0, 0);

            int minZ = int.MaxValue, maxZ = int.MinValue;
            for (int i = 0; i < cover.Count; i++)
            {
                int z = cover[i].Z;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }

            distinctX.Clear();
            distinctY.Clear();
            for (int i = 0; i < cover.Count; i++)
            {
                TileId t = cover[i];
                if (t.Z != maxZ) continue; // near-field grid: only the finest level
                distinctX.Add(t.X);
                distinctY.Add(t.Y);
            }

            return (distinctX.Count, distinctY.Count, minZ, maxZ);
        }
    }
}
