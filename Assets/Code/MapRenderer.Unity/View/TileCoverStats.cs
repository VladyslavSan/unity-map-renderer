// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.
// No UnityEngine references — TileId (Core.Geo) + collections only.

using System.Collections.Generic;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>
    /// Pure cover-dims + zoom-span derivation over a selected tile cover — the near-field grid
    /// (<c>Columns</c>/<c>Rows</c>) and the zoom span (<c>MinZ</c>/<c>MaxZ</c>) that the mixed-zoom
    /// <see cref="FrustumTileSelector"/> output doesn't surface directly. The seam stays unchanged; zoom is
    /// derived from the cover's <see cref="TileId.Z"/> values here.
    ///
    /// <para><b>Columns/Rows count distinct X/Y at <c>MaxZ</c> only</b> — counting the whole cover mixes z
    /// and z−1 index spaces under the default <c>ScreenSpaceLodStrategy</c> mixed-zoom cover, so it is
    /// restricted to the finest level (the near-field rectangle); a single-zoom (<c>FlatLodStrategy</c>)
    /// cover collapses to the same thing. A <c>max(X) − min(X) + 1</c> shortcut is also wrong on its own
    /// terms — it breaks on an antimeridian-wrapped cover (X values <c>{0, 1, n-2, n-1}</c> read as huge).</para>
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
