using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's ribbon sizing node: per ring, sizes
    /// <see cref="RibbonJob.MaxVertexCount"/>/<see cref="RibbonJob.MaxIndexCount"/> off THAT ring's own
    /// point count, and prefix-sums into <see cref="RibbonBuffers.RingVertexOffsets"/>/
    /// <see cref="RibbonBuffers.RingIndexOffsets"/>, resizing the flat output columns and the per-ring
    /// real-count columns to match.
    ///
    /// <para><b>Ring-to-ring disjointness is structural</b>, as it is in the earcut:
    /// <see cref="RibbonBatchJob.Execute"/> takes consecutive-entry slices over a monotonic table.
    /// <b>Each ring's capacity is floored at 1</b>, because <see cref="RibbonJob.MaxVertexCount"/> returns 0
    /// for a ring with fewer than 2 points. Without the floor such a ring contributes zero growth to its
    /// offset-table entry, which the monotonicity check below would flag as non-disjoint for what is only an
    /// empty ring.</para>
    ///
    /// <para><b>The monotonicity check is defence-in-depth, not the parallel ribbon's precondition</b> —
    /// the line twin of <see cref="SizingJob"/>'s. It turns a non-monotonic table into a player-build error
    /// code instead of an Editor-only <c>GetSubArray</c> bounds throw.
    ///
    /// <b>Its early return protects the WHOLE downstream chain.</b> It returns before all four
    /// <c>Resize</c> calls, so <see cref="RibbonBuffers.PerRingVertexCount"/>/<c>PerRingIndexCount</c> stay
    /// at length 0. <see cref="RibbonBatchJob"/> is deferred over that same list and runs zero batches, but
    /// that alone does not protect <see cref="RibbonAggregateJob"/> one node further downstream, which must
    /// also bound its own loop by that list's length rather than a borrowed ring count.</para>
    ///
    /// <para><b>The <see cref="RoundSegments"/> floor at 1 lives here</b>, because sizing needs the floored
    /// value. <see cref="RibbonBatchJob"/> re-derives the identical floor from the same input, a pure
    /// recomputation rather than a second source of truth.</para>
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
