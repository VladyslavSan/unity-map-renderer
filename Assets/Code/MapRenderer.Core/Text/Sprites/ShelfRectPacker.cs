// Engine-free: no UnityEngine dependency.

using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// Deterministic next-fit-decreasing-height <b>shelf</b> rectangle packing of axis-aligned cells into the
    /// smallest power-of-two-widened sheet it can. A shelf layout is disjoint by its rule, which matters more
    /// here than the few percent MaxRects/skyline would gain. Cells sort by height desc, width desc, then input
    /// index asc; that total order lets the caller's own ordering decide ties, so two runs cannot differ.
    /// </summary>
    public static class ShelfRectPacker
    {
        /// <summary>
        /// Packs <paramref name="cells"/> (texel sizes, top-left-origin output positions). Sheet width is the
        /// first of <c>{W0, 2·W0, 4·W0, …}</c> — with <c>W0 = max(minWidth, widest cell)</c> — whose packed
        /// height fits <paramref name="maxDimension"/>; the search stops once the width itself would exceed
        /// <paramref name="maxDimension"/>.
        /// </summary>
        /// <param name="cells">Cell sizes in texels. Non-positive extents are rejected.</param>
        /// <param name="minWidth">Lower bound on the sheet width (typically the source sheet's width).</param>
        /// <param name="maxDimension">Hard cap on both output dimensions (typically the GPU's max texture size).</param>
        /// <param name="positions">Receives each cell's top-left position, indexed in INPUT order. Must be at
        /// least <c>cells.Count</c> long.</param>
        /// <param name="size">Receives the packed sheet size; <c>default</c> when packing fails.</param>
        /// <returns>False when the cells cannot fit inside <paramref name="maxDimension"/> — the caller must
        /// then fall back rather than emit an unusable sheet.</returns>
        public static bool TryPack(
            IReadOnlyList<int2> cells, int minWidth, int maxDimension, int2[] positions, out int2 size)
        {
            size = default;
            if (cells == null || positions == null || positions.Length < cells.Count)
                return false;
            if (cells.Count == 0)
                return false;

            int widestCell = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                if (cells[i].x <= 0 || cells[i].y <= 0)
                    return false;
                widestCell = math.max(widestCell, cells[i].x);
            }

            int[] order = SortedOrder(cells);

            int startWidth = math.max(minWidth, widestCell);
            for (int width = startWidth; width <= maxDimension; width *= 2)
            {
                int packedHeight = LayOutShelves(cells, order, width, positions);
                if (packedHeight <= maxDimension)
                {
                    size = new int2(width, packedHeight);
                    return true;
                }
            }

            return false;
        }

        /// <summary>Indices of <paramref name="cells"/> ordered height desc, width desc, input index asc.</summary>
        private static int[] SortedOrder(IReadOnlyList<int2> cells)
        {
            var order = new int[cells.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;

            System.Array.Sort(order, (a, b) =>
            {
                if (cells[a].y != cells[b].y) return cells[b].y - cells[a].y; // taller first
                if (cells[a].x != cells[b].x) return cells[b].x - cells[a].x; // then wider first
                return a - b;                                                 // then input order (total)
            });
            return order;
        }

        /// <summary>
        /// Walks the ordered cells laying shelves, writing each cell's position into
        /// <paramref name="positions"/> at its INPUT index. Returns the total packed height. Because the walk
        /// is height-descending, a shelf's height is its first cell's height.
        /// </summary>
        private static int LayOutShelves(IReadOnlyList<int2> cells, int[] order, int width, int2[] positions)
        {
            int shelfY = 0;
            int shelfHeight = 0;
            int x = 0;

            for (int i = 0; i < order.Length; i++)
            {
                int2 cell = cells[order[i]];
                if (x > 0 && x + cell.x > width)
                {
                    shelfY += shelfHeight;
                    shelfHeight = 0;
                    x = 0;
                }

                positions[order[i]] = new int2(x, shelfY);
                x += cell.x;
                shelfHeight = math.max(shelfHeight, cell.y);
            }

            return shelfY + shelfHeight;
        }
    }
}
