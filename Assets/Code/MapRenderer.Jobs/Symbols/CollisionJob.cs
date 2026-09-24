using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Symbols
{
    /// <summary>
    /// The Burst-compiled, grid-accelerated greedy survivor selection. It sorts <see cref="Candidates"/> into
    /// placement order, then places each candidate iff none of its boxes overlaps an already-placed blocker.
    /// It is one serial job, because each placement depends on all prior survivors.
    /// Non-local invariant: the caller pre-sizes the grid with this job's cell mapping (<see cref="CollisionGridSizing"/>)
    /// and fills <see cref="CellHead"/> with -1, because a Burst job cannot grow its arrays.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct CollisionJob : IJob
    {
        // ── Candidates (sorted IN PLACE into placement order) + boxes (read-only) ────────────────────────────
        public NativeArray<SymbolCandidate> Candidates; // [0..CandidateCount) — reordered by the placement sort
        public int CandidateCount;
        [ReadOnly] public NativeArray<SymbolBox> Boxes; // the flat box pool; NOT reordered (grid stores box indices)
        public int BoxCount;

        // ── Output: survivor flag per SORTED candidate position (identity = Candidates[i].SymbolIndex) + count ──
        [WriteOnly] public NativeArray<byte> Survivors;        // 1 = placed, 0 = dropped; length >= CandidateCount
        [WriteOnly] public NativeArray<int>  OutSurvivorCount; // length 1

        // ── Native uniform grid, pre-sized by the caller (Burst cannot resize) ───────────────────────────────
        public NativeArray<int> CellHead; // per-cell head node index or -1; length GridW*GridH (caller fills -1)
        public NativeArray<int> NodeBox;  // node -> box index; length >= node upper bound
        public NativeArray<int> NodeNext; // node -> next node in the same cell, or -1
        public float GridMinX, GridMinY, GridInvCell;
        public int   GridW, GridH;

        public void Execute()
        {
            Sort(); // heapsort Candidates[0..CandidateCount) into placement order

            int nodeCount = 0, survivors = 0;
            for (int i = 0; i < CandidateCount; i++)
            {
                SymbolCandidate c = Candidates[i];
                int start = c.BoxStart, end = c.BoxStart + c.BoxCount;

                // A zoom-gated candidate (layer outside minzoom/maxzoom) is ABSENT for collision: never placed,
                // never a blocker, while the caller still emits it fading to 0.
                if (c.Suppressed) { Survivors[i] = 0; continue; }

                // Place if it ignores collision, OR none of its REQUIRED boxes overlaps an already-placed blocker.
                // Non-obvious why: all boxes are tested before any is inserted, because a centred pair's two
                // boxes always overlap, so inserting one half first would make the pair block itself.
                bool place = c.AllowOverlap;
                byte dropped = 0;
                if (!place)
                {
                    place = true;
                    for (int b = start; b < end; b++)
                    {
                        if (!OverlapsAny(b)) continue;
                        int bit = 1 << (b - start);
                        if ((c.OptionalBoxMask & bit) == 0) { place = false; break; }
                        dropped |= (byte)bit;
                    }
                }

                Survivors[i] = (byte)(place ? 1 : 0);
                if (place)
                {
                    survivors++;
                    if (!c.IgnorePlacement)
                        for (int b = start; b < end; b++)
                        {
                            if (dropped != 0 && (dropped & (1 << (b - start))) != 0) continue;
                            nodeCount = Insert(b, nodeCount);
                        }
                }

                if (c.OptionalBoxMask != 0)
                {
                    c.DroppedBoxMask = place ? dropped : (byte)0; // a dropped candidate records no per-half verdict
                    Candidates[i] = c; // NativeArray indexer returns by value — write the mutated copy back
                }
            }
            OutSurvivorCount[0] = survivors;
        }

        // ── Grid query/insert over the pre-sized native arrays ──────────────────────────────────────────────
        private bool OverlapsAny(int boxIndex)
        {
            SymbolBox box = Boxes[boxIndex];
            int cx0 = CellX(box.Min.x), cx1 = CellX(box.Max.x);
            int cy0 = CellY(box.Min.y), cy1 = CellY(box.Max.y);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                int rowBase = cy * GridW;
                for (int cx = cx0; cx <= cx1; cx++)
                    for (int node = CellHead[rowBase + cx]; node != -1; node = NodeNext[node])
                    {
                        SymbolBox other = Boxes[NodeBox[node]]; // NativeArray indexer returns by value — need a local to pass `in`
                        if (SymbolCollision.Overlaps(in box, in other)) return true;
                    }
            }
            return false;
        }

        private int Insert(int boxIndex, int nodeCount)
        {
            SymbolBox box = Boxes[boxIndex];
            int cx0 = CellX(box.Min.x), cx1 = CellX(box.Max.x);
            int cy0 = CellY(box.Min.y), cy1 = CellY(box.Max.y);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                int rowBase = cy * GridW;
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    // Non-obvious why: managed sizing and this Burst insert can truncate a cell-boundary coordinate
                    // one cell apart, so this last-resort guard skips the node (a slightly permissive survivor)
                    // instead of an out-of-range write. nodeCount still advances so the pass stays deterministic.
                    if (nodeCount < NodeBox.Length)
                    {
                        int cell = rowBase + cx;
                        NodeBox[nodeCount]  = boxIndex;
                        NodeNext[nodeCount] = CellHead[cell];
                        CellHead[cell]      = nodeCount;
                    }
                    nodeCount++;
                }
            }
            return nodeCount;
        }

        private int CellX(float x)
        {
            int cx = (int)((x - GridMinX) * GridInvCell);
            return cx < 0 ? 0 : (cx >= GridW ? GridW - 1 : cx);
        }

        private int CellY(float y)
        {
            int cy = (int)((y - GridMinY) * GridInvCell);
            return cy < 0 ? 0 : (cy >= GridH ? GridH - 1 : cy);
        }

        // ── In-place heapsort by SymbolCollision.ComparePlacementOrder (mirrors SymbolCollision.Sort/SiftDown) ──
        private void Sort()
        {
            int n = CandidateCount;
            for (int root = n / 2 - 1; root >= 0; root--) SiftDown(root, n);
            for (int end = n - 1; end > 0; end--)
            {
                (Candidates[0], Candidates[end]) = (Candidates[end], Candidates[0]);
                SiftDown(0, end);
            }
        }

        private void SiftDown(int root, int n)
        {
            while (true)
            {
                int child = 2 * root + 1;
                if (child >= n) break;
                if (child + 1 < n)
                {
                    SymbolCandidate cc = Candidates[child], cc1 = Candidates[child + 1];
                    if (SymbolCollision.ComparePlacementOrder(in cc, in cc1) < 0) child++;
                }
                SymbolCandidate cr = Candidates[root], ck = Candidates[child];
                if (SymbolCollision.ComparePlacementOrder(in cr, in ck) >= 0) break;
                (Candidates[root], Candidates[child]) = (Candidates[child], Candidates[root]);
                root = child;
            }
        }
    }

    /// <summary>
    /// Main-thread sizing for <see cref="CollisionJob"/>'s pre-allocated grid — computes the grid dimensions
    /// and the exact node-storage upper bound BEFORE the job is scheduled (a Burst job cannot grow its arrays).
    /// The cell mapping here is BIT-IDENTICAL to <see cref="CollisionJob"/>'s own <c>CellX</c>/<c>CellY</c> —
    /// TargetCellPx / MaxGridDim / the clamp must stay in lockstep, or the node bound under-counts and inserts
    /// drop.
    /// </summary>
    public static class CollisionGridSizing
    {
        private const float TargetCellPx = 64f;
        private const int   MaxGridDim   = 512;

        public struct Dims
        {
            public float MinX, MinY, InvCell;
            public int   W, H;
        }

        /// <summary>The grid dims bounding <paramref name="boxes"/><c>[0..count)</c>.</summary>
        public static Dims ComputeDims(NativeArray<SymbolBox> boxes, int count)
        {
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                SymbolBox b = boxes[i];
                if (b.Min.x < minX) minX = b.Min.x;
                if (b.Min.y < minY) minY = b.Min.y;
                if (b.Max.x > maxX) maxX = b.Max.x;
                if (b.Max.y > maxY) maxY = b.Max.y;
            }

            float spanX = math.max(0f, maxX - minX);
            float spanY = math.max(0f, maxY - minY);
            float cell  = TargetCellPx;
            float need  = math.max(spanX / MaxGridDim, spanY / MaxGridDim);
            if (need > cell) cell = need;
            float invCell = 1f / cell;

            return new Dims
            {
                MinX = minX, MinY = minY, InvCell = invCell,
                W = math.max(1, (int)(spanX * invCell) + 1),
                H = math.max(1, (int)(spanY * invCell) + 1),
            };
        }

        /// <summary>An upper bound on grid nodes: the sum, over every box, of the cells it covers — this bounds
        /// <see cref="CollisionJob.NodeBox"/>'s length. Non-obvious why: each cell span widens by 2 (one cell
        /// per side), because this main-thread mapping and the Burst <see cref="CollisionJob.Insert"/> can
        /// truncate a cell-boundary coordinate one cell apart, and an under-count is an out-of-range write.</summary>
        public static int NodeUpperBound(NativeArray<SymbolBox> boxes, int count, in Dims d)
        {
            int total = 0;
            for (int i = 0; i < count; i++)
            {
                SymbolBox b = boxes[i];
                int cx0 = CellX(b.Min.x, d), cx1 = CellX(b.Max.x, d);
                int cy0 = CellY(b.Min.y, d), cy1 = CellY(b.Max.y, d);
                total += (cx1 - cx0 + 3) * (cy1 - cy0 + 3); // +1 exact span, +2 for the ±1-cell Mono/Burst drift margin
            }
            return total;
        }

        /// <summary>The node upper bound counted the way <see cref="CollisionJob"/> actually INSERTS —
        /// per CANDIDATE box-reference, not per unique box. It equals <see cref="NodeUpperBound"/> when the
        /// candidate ranges tile disjointly (<see cref="SymbolCandidate.TryFindRangeTilingViolation"/>) and
        /// exceeds it when two candidates share a box. Non-obvious why: the bound then does not depend on the
        /// disjointness invariant. Ranges clamp to <c>[0,boxCount)</c>; the ±1-cell margin matches
        /// <see cref="NodeUpperBound"/>.</summary>
        public static int NodeUpperBoundByCandidates(NativeArray<SymbolCandidate> candidates, int candidateCount,
            NativeArray<SymbolBox> boxes, int boxCount, in Dims d)
        {
            int total = 0;
            for (int i = 0; i < candidateCount; i++)
            {
                SymbolCandidate c = candidates[i];
                int lo = math.max(0, c.BoxStart);
                int hi = math.min(boxCount, c.BoxStart + c.BoxCount);
                for (int b = lo; b < hi; b++)
                {
                    SymbolBox bx = boxes[b];
                    int cx0 = CellX(bx.Min.x, d), cx1 = CellX(bx.Max.x, d);
                    int cy0 = CellY(bx.Min.y, d), cy1 = CellY(bx.Max.y, d);
                    total += (cx1 - cx0 + 3) * (cy1 - cy0 + 3); // +1 exact span, +2 for the ±1-cell Mono/Burst drift margin
                }
            }
            return total;
        }

        private static int CellX(float x, in Dims d)
        {
            int cx = (int)((x - d.MinX) * d.InvCell);
            return cx < 0 ? 0 : (cx >= d.W ? d.W - 1 : cx);
        }

        private static int CellY(float y, in Dims d)
        {
            int cy = (int)((y - d.MinY) * d.InvCell);
            return cy < 0 ? 0 : (cy >= d.H ? d.H - 1 : cy);
        }
    }
}
