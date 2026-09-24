using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's ribbon aggregate node: in ring order, appends each ring's real vertex and index span
    /// from <see cref="RibbonBuffers"/> to the output lists, with the 2nd/3rd winding swap and the offset
    /// rebase. Non-obvious why: the <c>MaxOutputVertices</c> stop lives here, not in the parallel node,
    /// because only a serial walk stops at the same ring every run, which keeps the output bit-exact.
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
            // No Error reset: it starts zeroed and RibbonSizingJob, which runs first, shares it, so a reset
            // would discard a sizing error.

            NativeList<int> ringVertexOffsets = Buffers.RingVertexOffsets;
            NativeList<int> ringIndexOffsets  = Buffers.RingIndexOffsets;
            NativeList<LineRibbonVertex> flatVertices = Buffers.FlatVertices;
            NativeList<int>              flatIndices  = Buffers.FlatIndices;
            NativeList<int> perRingVertexCount = Buffers.PerRingVertexCount;
            NativeList<int> perRingIndexCount  = Buffers.PerRingIndexCount;

            // Bound by PerRingVertexCount's own length, never the borrowed RingSubOffsets, which a sizing
            // early return leaves stale; a player build has no indexer bounds check to catch that.
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
