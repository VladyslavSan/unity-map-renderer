using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's ribbon aggregate node: walks rings IN RING ORDER, appending each ring's REAL (not
    /// oversized-capacity) vertex and index span from <see cref="RibbonBuffers"/>' flat columns into the
    /// graph's output lists, applying the 2nd/3rd winding swap and the running <c>offset</c> rebase.
    /// <see cref="RibbonBatchJob"/> writes RAW, ring-local, unswapped bytes; this node is where they become
    /// the graph's globally-offset, winding-correct output.
    ///
    /// <para><b>The overflow rule lives here, not in the parallel node.</b> The
    /// <c>MaxOutputVertices</c> check breaks out of the ring loop, which is an order-dependent early stop.
    /// Parallel rings all compute regardless; this serial node applies the test in ring order and stops at
    /// the same ring every run, which is what keeps the output bit-exact.</para>
    ///
    /// <para>No unconditional <see cref="LineGraphCounts.Ok"/> reset — see <see cref="Execute"/>'s own
    /// comment for why.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct RibbonAggregateJob : IJob
    {
        [ReadOnly] public NativeList<int> RingFeature; // length = ringCount

        public RibbonBuffers Buffers;
        public int MaxOutputVertices;

        public NativeList<LineRibbonVertex> OutVertices;
        public NativeList<int>              OutVertexFeatureIdx;
        public NativeList<int>              OutIndices;
        public NativeReference<int>         Error;

        public void Execute()
        {
            OutVertices.Clear();
            OutVertexFeatureIdx.Clear();
            OutIndices.Clear();
            // No unconditional Error.Value = Ok reset here: AllocateError already hands out a
            // zero-initialised reference, RibbonSizingJob shares that SAME reference, and this node runs
            // after it — a reset would discard a sizing-stage error.

            NativeList<int> ringVertexOffsets = Buffers.RingVertexOffsets;
            NativeList<int> ringIndexOffsets  = Buffers.RingIndexOffsets;
            NativeList<LineRibbonVertex> flatVertices = Buffers.FlatVertices;
            NativeList<int>              flatIndices  = Buffers.FlatIndices;
            NativeList<int> perRingVertexCount = Buffers.PerRingVertexCount;
            NativeList<int> perRingIndexCount  = Buffers.PerRingIndexCount;

            // Bound by PerRingVertexCount's OWN length, never RingSubOffsets.Length - 1 — a borrowed input
            // that RibbonSizingJob's early return leaves stale while both per-ring columns are length 0. The
            // indexer bounds check is [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")], gone in a player.
            int ringCount = perRingVertexCount.Length;

            for (int r = 0; r < ringCount; r++)
            {
                int nv = perRingVertexCount[r];
                int ni = perRingIndexCount[r];
                if (nv == 0 || ni == 0) continue;

                if (OutVertices.Length + nv > MaxOutputVertices)
                {
                    Error.Value = LineGraphCounts.ErrorLineVertexCapacity;
                    break;
                }

                int featIdx = RingFeature[r];
                int vOff = ringVertexOffsets[r];
                int iOff = ringIndexOffsets[r];
                int offset = OutVertices.Length;

                for (int k = 0; k < nv; k++)
                {
                    OutVertices.Add(flatVertices[vOff + k]);
                    OutVertexFeatureIdx.Add(featIdx);
                }

                // Reverse triangle winding at this GPU-index boundary, mirroring the fill reversal.
                for (int k = 0; k + 2 < ni; k += 3)
                {
                    OutIndices.Add(offset + flatIndices[iOff + k + 0]);
                    OutIndices.Add(offset + flatIndices[iOff + k + 2]); // 2nd/3rd
                    OutIndices.Add(offset + flatIndices[iOff + k + 1]); // swapped
                }
            }
        }
    }
}
