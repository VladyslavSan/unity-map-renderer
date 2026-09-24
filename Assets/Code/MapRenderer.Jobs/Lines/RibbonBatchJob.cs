using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's ribbon node: runs <see cref="RibbonJob"/> on one ring per <see cref="Execute"/>
    /// and writes raw, ring-local, unswapped output; <see cref="RibbonAggregateJob"/> swaps and rebases.
    /// Non-local invariant: it defers over <c>PerRingVertexCount</c>, the only column of length
    /// <c>ringCount</c> (offset tables are one longer), and each ring touches only its own consecutive
    /// offset-table entries, so the output is bit-exact under any worker schedule.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct RibbonBatchJob : IJobParallelForDefer
    {
        // ── Input (borrowed) ───────────────────────────────────────────────────────────────────
        [ReadOnly] public NativeList<double3> SubWorld;
        [ReadOnly] public NativeList<double3> SubUp;
        [ReadOnly] public NativeList<int>     RingSubOffsets; // sentinel layout, length = ringCount + 1

        public JoinType Join;
        public CapType  Cap;
        public double   MiterLimit;
        public int      RoundSegments;
        public double   RoundLimit;

        [NativeDisableParallelForRestriction]
        public RibbonBuffers Buffers;

        public void Execute(int r)
        {
            int rStart = RingSubOffsets[r];
            int m      = RingSubOffsets[r + 1] - rStart;
            int roundSegments = math.max(1, RoundSegments);

            NativeArray<int> ringVertexOffsets = Buffers.RingVertexOffsets.AsArray();
            NativeArray<int> ringIndexOffsets  = Buffers.RingIndexOffsets.AsArray();
            int vOff = ringVertexOffsets[r], vLen = ringVertexOffsets[r + 1] - vOff;
            int iOff = ringIndexOffsets[r],  iLen = ringIndexOffsets[r + 1] - iOff;

            NativeArray<LineRibbonVertex> flatVertices = Buffers.FlatVertices.AsArray();
            NativeArray<int>              flatIndices  = Buffers.FlatIndices.AsArray();
            NativeArray<int>              perRingVertexCount = Buffers.PerRingVertexCount.AsArray();
            NativeArray<int>              perRingIndexCount  = Buffers.PerRingIndexCount.AsArray();

            new RibbonJob
            {
                Points         = SubWorld.AsArray().GetSubArray(rStart, m),
                Ups            = SubUp.AsArray().GetSubArray(rStart, m),
                PointCount     = m,
                Join           = Join,
                Cap            = Cap,
                MiterLimit     = MiterLimit,
                RoundSegments  = roundSegments,
                RoundLimit     = RoundLimit,
                OutVertices    = flatVertices.GetSubArray(vOff, vLen),
                OutIndices     = flatIndices.GetSubArray(iOff, iLen),
                OutVertexCount = perRingVertexCount.GetSubArray(r, 1),
                OutIndexCount  = perRingIndexCount.GetSubArray(r, 1),
            }.Execute();
        }
    }
}
