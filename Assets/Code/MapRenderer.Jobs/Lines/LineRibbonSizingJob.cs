using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's ribbon sizing node (job-scheduling-design.md §8 stage 6, E.2): per ring, sizes
    /// <see cref="LineRibbonJob.MaxVertexCount"/>/<see cref="LineRibbonJob.MaxIndexCount"/> off THAT ring's
    /// own point count — tighter than the shared scratch the retired serial <see cref="LineRibbonBatchJob"/>
    /// sized to the corpus's global max — and prefix-sums into <see cref="LineRibbonBuffers.RingVertexOffsets"/>/
    /// <see cref="LineRibbonBuffers.RingIndexOffsets"/>, resizing the flat oversized-per-ring output columns
    /// and the per-ring real-count columns to match.
    ///
    /// <para><b>Ring-to-ring disjointness is structural</b> (job-scheduling-design.md §1), for the identical
    /// reason it is in the earcut: <see cref="LineRibbonBatchJob.Execute"/> takes consecutive-entry slices
    /// over a monotonic table. <b>Floors each ring's capacity at 1</b>, matching the retired serial code's own
    /// <c>math.max(1, maxV)</c>/<c>math.max(1, maxI)</c> scratch-array sizing (<see cref="LineRibbonJob.MaxVertexCount"/>
    /// returns 0 for a degenerate ring with fewer than 2 points) — without the floor, a degenerate ring would
    /// contribute zero growth to its offset-table entry, which the monotonicity assertion below would then
    /// (correctly) flag as non-disjoint for a case that is actually just an empty ring, not a capacity
    /// defect.</para>
    ///
    /// <para><b>Monotonicity assertion — defence-in-depth, NOT the parallel ribbon's bit-exactness
    /// precondition</b> (mirrors <see cref="FillSizingJob"/>'s own C.2 assertion and its doc, word for word):
    /// disjointness is structural and no edit here can make two rings' slices overlap. What this catches
    /// instead: a non-monotonic table surfacing as a player-build error code instead of an Editor-only
    /// <c>GetSubArray</c> bounds throw. Strictly increasing holds here because every entry's growth is
    /// floored at 1, above.
    ///
    /// <b>This early return protects the WHOLE downstream chain, not only <see cref="LineRibbonBatchJob"/>'s
    /// own <c>GetSubArray</c> call.</b> It returns before any of its four <c>Resize</c> calls, so
    /// <see cref="LineRibbonBuffers.PerRingVertexCount"/>/<c>PerRingIndexCount</c> stay at length 0.
    /// <see cref="LineRibbonBatchJob"/> is deferred over that same list, so it correctly runs zero batches —
    /// but that alone does not protect <see cref="LineRibbonAggregateJob"/>, one node further downstream,
    /// unless IT ALSO bounds its own loop by that list's length rather than a borrowed ring count the early
    /// return never touches (a real bug this stage shipped and fixed — see
    /// <see cref="LineRibbonAggregateJob"/>'s own doc). Both must hold for this sentence to be true.</para>
    ///
    /// <para><b>RoundSegments floor at 1 moves here</b> from the retired serial <see cref="LineRibbonBatchJob"/>
    /// — sizing needs the floored value to size correctly, and the parallel ribbon node re-derives the
    /// identical floor from the same <see cref="RoundSegments"/> input independently (a pure, side-effect-free
    /// recomputation, not a second source of truth).</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct LineRibbonSizingJob : IJob
    {
        [ReadOnly] public NativeList<int> RingSubOffsets; // sentinel layout, length = ringCount + 1
        public int RoundSegments;

        public LineRibbonBuffers Buffers;
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
                int maxV = math.max(1, LineRibbonJob.MaxVertexCount(m, roundSegments));
                int maxI = math.max(1, LineRibbonJob.MaxIndexCount(m, roundSegments));

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
