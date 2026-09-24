using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's ribbon sizing node: per ring, prefix-sums <see cref="RibbonJob.MaxVertexCount"/>
    /// and <see cref="RibbonJob.MaxIndexCount"/> into the offset tables and resizes the flat and per-ring
    /// columns. <see cref="RoundSegments"/> and each ring's capacity floor at 1; an empty ring still grows.
    /// Non-local invariant: a non-monotonic table sets an error and returns before any <c>Resize</c>, so
    /// the per-ring count columns stay at length 0 and every downstream node bounds its loop to nothing.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct RibbonSizingJob : IJob
    {
        [ReadOnly] public NativeList<int> RingSubOffsets; // sentinel layout, length = ringCount + 1
        public int RoundSegments;

        public RibbonBuffers Buffers;
        public NativeReference<int> Error;

        public void Execute()
        {
            int ringCount = RingSubOffsets.Length - 1;
            int roundSegments = math.max(1, RoundSegments);

            NativeList<int> ringVertexOffsets = Buffers.RingVertexOffsets;
            NativeList<int> ringIndexOffsets  = Buffers.RingIndexOffsets;
            ringVertexOffsets.Add(0);
            ringIndexOffsets.Add(0);

            for (int r = 0; r < ringCount; r++)
            {
                int m = RingSubOffsets[r + 1] - RingSubOffsets[r];
                int maxV = math.max(1, RibbonJob.MaxVertexCount(m, roundSegments));
                int maxI = math.max(1, RibbonJob.MaxIndexCount(m, roundSegments));

                ringVertexOffsets.Add(ringVertexOffsets[r] + maxV);
                ringIndexOffsets.Add(ringIndexOffsets[r] + maxI);
            }

            for (int r = 0; r < ringCount; r++)
            {
                if (ringVertexOffsets[r + 1] <= ringVertexOffsets[r] || ringIndexOffsets[r + 1] <= ringIndexOffsets[r])
                {
                    Error.Value = LineGraphCounts.ErrorOffsetTableNotDisjoint;
                    return;
                }
            }

            Buffers.FlatVertices.Resize(ringVertexOffsets[ringCount], NativeArrayOptions.UninitializedMemory);
            Buffers.FlatIndices.Resize(ringIndexOffsets[ringCount], NativeArrayOptions.UninitializedMemory);
            Buffers.PerRingVertexCount.Resize(ringCount, NativeArrayOptions.ClearMemory);
            Buffers.PerRingIndexCount.Resize(ringCount, NativeArrayOptions.ClearMemory);
        }
    }
}
