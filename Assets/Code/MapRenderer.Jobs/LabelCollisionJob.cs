using MapRenderer.Core.Text.Placement;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs
{
    /// <summary>
    /// B-4a: the Burst-compiled port of the grid-accelerated
    /// <see cref="LabelCollision.SelectSurvivors(LabelCandidate[],int,LabelBox[],int,bool[],LabelCollisionGrid)"/>
    /// — the serial greedy survivor selection, run as a single <see cref="IJob"/> over NATIVE data instead of the
    /// managed path. Behaviourally IDENTICAL to the managed reference (same heapsort placement order, same
    /// <see cref="LabelCollision.Overlaps"/> decision, same uniform-grid prefilter) — a differential test locks it
    /// bit-for-bit over adversarial inputs. It is ONE job, not a parallel fan-out: the greedy pass is inherently
    /// serial (each placement depends on all prior survivors); the grid keeps it ~O(n·k).
    ///
    /// <para><b>The grid is PRE-SIZED by the caller</b> (<see cref="LabelCollisionGridSizing"/>) because a Burst job
    /// cannot grow a <see cref="NativeArray{T}"/> mid-run: the managed grid resized its node arrays inside Insert,
    /// which is illegal here. The caller computes the grid dims on the main thread, fills <see cref="CellHead"/>
    /// with -1 (length <c>GridW*GridH</c>), and sizes <see cref="NodeBox"/>/<see cref="NodeNext"/> to the exact
    /// upper bound (sum over boxes of the cells each AABB covers). Under-sizing would drop blocker inserts → a
    /// candidate wouldn't be blocked → wrong survivor set, so the sizing mirrors this job's cell mapping exactly.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct LabelCollisionJob : IJob
    {
        // ── Candidates (sorted IN PLACE into placement order) + boxes (read-only) ────────────────────────────
        public NativeArray<LabelCandidate> Candidates; // [0..CandidateCount) — reordered by the placement sort
        public int CandidateCount;
        [ReadOnly] public NativeArray<LabelBox> Boxes; // the flat box pool; NOT reordered (grid stores box indices)
        public int BoxCount;

        // ── Output: survivor flag per SORTED candidate position (identity = Candidates[i].LabelIndex) + count ──
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
            Sort(); // heapsort Candidates[0..CandidateCount) into placement order (mirrors LabelCollision.Sort)

            int nodeCount = 0, survivors = 0;
            for (int i = 0; i < CandidateCount; i++)
            {
                LabelCandidate c = Candidates[i];
                int start = c.BoxStart, end = c.BoxStart + c.BoxCount;

                // Zoom-gated OUT: a suppressed candidate (owning layer outside the live camera zoom's minzoom/maxzoom)
                // is ABSENT for collision — never placed, never a blocker — so it neither wins nor blocks the winner
                // while it eases to 0 (still emitted, fading, by the caller). Mirrors LabelCollision.SelectSurvivors.
                if (c.Suppressed) { Survivors[i] = 0; continue; }

                // Place if it ignores collision, OR none of its boxes overlaps an already-placed blocker. Test ALL
                // boxes first (all-or-nothing) — no box is inserted until the whole candidate wins (no self-block).
                bool place = c.AllowOverlap;
                if (!place)
                {
                    place = true;
                    for (int b = start; b < end; b++)
                        if (OverlapsAny(b)) { place = false; break; }
                }

                Survivors[i] = (byte)(place ? 1 : 0);
                if (place)
                {
                    survivors++;
                    if (!c.IgnorePlacement)
                        for (int b = start; b < end; b++) nodeCount = Insert(b, nodeCount);
                }
            }
            OutSurvivorCount[0] = survivors;
        }

        // ── Grid query/insert (mirrors LabelCollisionGrid.OverlapsAny / Insert over the native arrays) ────────
        private bool OverlapsAny(int boxIndex)
        {
            LabelBox box = Boxes[boxIndex];
            int cx0 = CellX(box.Min.x), cx1 = CellX(box.Max.x);
            int cy0 = CellY(box.Min.y), cy1 = CellY(box.Max.y);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                int rowBase = cy * GridW;
                for (int cx = cx0; cx <= cx1; cx++)
                    for (int node = CellHead[rowBase + cx]; node != -1; node = NodeNext[node])
                    {
                        LabelBox other = Boxes[NodeBox[node]]; // NativeArray indexer returns by value — need a local to pass `in`
                        if (LabelCollision.Overlaps(in box, in other)) return true;
                    }
            }
            return false;
        }

        private int Insert(int boxIndex, int nodeCount)
        {
            LabelBox box = Boxes[boxIndex];
            int cx0 = CellX(box.Min.x), cx1 = CellX(box.Max.x);
            int cy0 = CellY(box.Min.y), cy1 = CellY(box.Max.y);
            for (int cy = cy0; cy <= cy1; cy++)
            {
                int rowBase = cy * GridW;
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    int cell = rowBase + cx;
                    NodeBox[nodeCount]  = boxIndex;
                    NodeNext[nodeCount] = CellHead[cell];
                    CellHead[cell]      = nodeCount;
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

        // ── In-place heapsort by LabelCollision.ComparePlacementOrder (mirrors LabelCollision.Sort/SiftDown) ──
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
                    LabelCandidate cc = Candidates[child], cc1 = Candidates[child + 1];
                    if (LabelCollision.ComparePlacementOrder(in cc, in cc1) < 0) child++;
                }
                LabelCandidate cr = Candidates[root], ck = Candidates[child];
                if (LabelCollision.ComparePlacementOrder(in cr, in ck) >= 0) break;
                (Candidates[root], Candidates[child]) = (Candidates[child], Candidates[root]);
                root = child;
            }
        }
    }

    /// <summary>
    /// Main-thread sizing for <see cref="LabelCollisionJob"/>'s pre-allocated grid — computes the grid dimensions
    /// and the exact node-storage upper bound BEFORE the job is scheduled (a Burst job cannot grow its arrays).
    /// The cell mapping here is BIT-IDENTICAL to <see cref="LabelCollisionJob"/>'s (and to the managed
    /// <c>LabelCollisionGrid</c>) — TargetCellPx / MaxGridDim / the CellX/CellY clamp must stay in lockstep with
    /// both, or the node bound under-counts and inserts drop (the differential test over adversarial inputs — wide
    /// boxes, dense clusters at the grid-dim boundary — is the net that catches drift).
    /// </summary>
    public static class LabelCollisionGridSizing
    {
        private const float TargetCellPx = 64f;
        private const int   MaxGridDim   = 512;

        public struct Dims
        {
            public float MinX, MinY, InvCell;
            public int   W, H;
        }

        /// <summary>The grid dims bounding <paramref name="boxes"/><c>[0..count)</c> — mirrors <c>LabelCollisionGrid.Reset</c>.</summary>
        public static Dims ComputeDims(NativeArray<LabelBox> boxes, int count)
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

        /// <summary>The exact upper bound on grid nodes: every box (whether or not it ends up inserted) covers
        /// <c>(cellsX·cellsY)</c> cells — summed, this bounds <see cref="LabelCollisionJob.NodeBox"/>'s length.</summary>
        public static int NodeUpperBound(NativeArray<LabelBox> boxes, int count, in Dims d)
        {
            int total = 0;
            for (int i = 0; i < count; i++)
            {
                LabelBox b = boxes[i];
                int cx0 = CellX(b.Min.x, d), cx1 = CellX(b.Max.x, d);
                int cy0 = CellY(b.Min.y, d), cy1 = CellY(b.Max.y, d);
                total += (cx1 - cx0 + 1) * (cy1 - cy0 + 1);
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
