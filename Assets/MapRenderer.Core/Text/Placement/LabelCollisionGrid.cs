// Engine-free: no UnityEngine dependency. TOP-LEVEL `using Unity.Mathematics;` (unqualified float2) — the
// namespace-collision trap documented in LabelBox.cs's header.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Placement
{
    /// <summary>
    /// A reusable uniform screen-space grid that accelerates <see cref="LabelCollision.SelectSurvivors"/>
    /// from O(n²) to ~O(n·k) (k = local label density). It is a **pure prefilter**: it only narrows WHICH
    /// already-placed blockers a candidate is overlap-tested against — the accept/reject decision stays the
    /// exact same <see cref="LabelCollision.Overlaps"/> call, so the grid-accelerated survivor set is
    /// bit-identical to the brute-force one (locked by a differential test).
    ///
    /// <para><b>Why this exists.</b> The Slice-2 brute-force greedy scanned all prior survivors per
    /// candidate; at zoom-14-big-city label counts (thousands) that O(n²) pass measured ~100 ms
    /// (<c>MapRenderer.Symbol.Collide</c>). MapLibre itself uses a grid index for collision.</para>
    ///
    /// <para><b>Zero per-frame GC (T4).</b> Caller-owned + reused across frames; the backing arrays grow
    /// only on warm-up (a larger viewport / more labels than ever seen) and are reset in place otherwise.
    /// A blocker is stored as one linked-list node PER grid cell its AABB covers (a wide label lands in
    /// several cells); duplicate hits across cells are harmless — <see cref="LabelCollision.Overlaps"/> is
    /// idempotent and the query breaks on the first overlap.</para>
    /// </summary>
    public sealed class LabelCollisionGrid
    {
        // Target cell edge in logical px — roughly one text label tall; a label spans a small handful of
        // cells. Not load-bearing for correctness (insert + query use the SAME mapping), only for speed.
        private const float TargetCellPx = 64f;
        // Cap either grid dimension so a pathological span (should not happen — candidates are viewport-culled
        // before collision) can't allocate/clear a huge cell array; the cell size is enlarged to fit instead.
        private const int MaxGridDim = 512;

        private int[] _cellHead = System.Array.Empty<int>(); // per-cell head node index, or -1
        private int[] _nodeBox = System.Array.Empty<int>();  // node → sorted box index
        private int[] _nodeNext = System.Array.Empty<int>(); // node → next node in the same cell, or -1
        private int _nodeCount;

        private int _gridW = 1;
        private int _gridH = 1;
        private float _minX;
        private float _minY;
        private float _invCell = 1f / TargetCellPx;

        /// <summary>
        /// (Re)initialise the grid to bound <paramref name="boxes"/><c>[0..count)</c> and clear all cells.
        /// One O(n) pass for the AABB + one O(cells) clear; both reuse the backing arrays.
        /// </summary>
        public void Reset(LabelBox[] boxes, int count)
        {
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                LabelBox b = boxes[i];
                if (b.Min.x < minX) minX = b.Min.x;
                if (b.Min.y < minY) minY = b.Min.y;
                if (b.Max.x > maxX) maxX = b.Max.x;
                if (b.Max.y > maxY) maxY = b.Max.y;
            }

            _minX = minX;
            _minY = minY;
            float spanX = math.max(0f, maxX - minX);
            float spanY = math.max(0f, maxY - minY);

            float cell = TargetCellPx;
            // Enlarge the cell if the span would exceed the grid-dimension cap (keeps the cell array bounded).
            float need = math.max(spanX / MaxGridDim, spanY / MaxGridDim);
            if (need > cell) cell = need;
            _invCell = 1f / cell;

            _gridW = math.max(1, (int)(spanX * _invCell) + 1);
            _gridH = math.max(1, (int)(spanY * _invCell) + 1);

            int cells = _gridW * _gridH;
            if (_cellHead.Length < cells) _cellHead = new int[cells];
            for (int c = 0; c < cells; c++) _cellHead[c] = -1;
            _nodeCount = 0;
        }

        private int CellX(float x)
        {
            int cx = (int)((x - _minX) * _invCell);
            return cx < 0 ? 0 : (cx >= _gridW ? _gridW - 1 : cx);
        }

        private int CellY(float y)
        {
            int cy = (int)((y - _minY) * _invCell);
            return cy < 0 ? 0 : (cy >= _gridH ? _gridH - 1 : cy);
        }

        /// <summary>True if <paramref name="box"/> overlaps any inserted blocker. Tests ONLY the boxes in the
        /// cells <paramref name="box"/>'s AABB covers (the same cell mapping <see cref="Insert"/> uses).</summary>
        public bool OverlapsAny(in LabelBox box, LabelBox[] boxes)
        {
            int cx0 = CellX(box.Min.x), cx1 = CellX(box.Max.x);
            int cy0 = CellY(box.Min.y), cy1 = CellY(box.Max.y);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                int rowBase = cy * _gridW;
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    for (int node = _cellHead[rowBase + cx]; node != -1; node = _nodeNext[node])
                    {
                        if (LabelCollision.Overlaps(in box, in boxes[_nodeBox[node]])) return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Insert an already-placed blocker (identified by its sorted index) into every cell its AABB
        /// covers, so later candidates in those cells overlap-test against it.</summary>
        public void Insert(int sortedBoxIndex, in LabelBox box)
        {
            int cx0 = CellX(box.Min.x), cx1 = CellX(box.Max.x);
            int cy0 = CellY(box.Min.y), cy1 = CellY(box.Max.y);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                int rowBase = cy * _gridW;
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    int node = _nodeCount;
                    if (node >= _nodeBox.Length) GrowNodes(node + 1);
                    int cell = rowBase + cx;
                    _nodeBox[node] = sortedBoxIndex;
                    _nodeNext[node] = _cellHead[cell];
                    _cellHead[cell] = node;
                    _nodeCount = node + 1;
                }
            }
        }

        private void GrowNodes(int min)
        {
            int cap = _nodeBox.Length == 0 ? 64 : _nodeBox.Length;
            while (cap < min) cap *= 2;
            System.Array.Resize(ref _nodeBox, cap);
            System.Array.Resize(ref _nodeNext, cap);
        }
    }
}
