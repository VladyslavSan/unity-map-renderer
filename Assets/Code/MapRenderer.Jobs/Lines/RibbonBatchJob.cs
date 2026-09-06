using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's ribbon node: one ring per <see cref="Execute"/> call, taking that ring's
    /// <c>GetSubArray</c> view of the subdivided world/up columns there, populating a
    /// <see cref="RibbonJob"/> and calling <see cref="RibbonJob.Execute"/> directly — that job's
    /// fields are untouched, so its existing callers stay valid. <see cref="IJobParallelForDefer"/> since
    /// stage 6 — batched by <see cref="LineMeshGraph.RibbonRingBatch"/>, deferred over
    /// <see cref="Buffers"/>' own <c>PerRingVertexCount</c> — the ONLY column in
    /// <see cref="RibbonBuffers"/> with length exactly <c>ringCount</c>; every offset table has length
    /// <c>ringCount + 1</c> (sentinel layout), so deferring over one of those would run
    /// <c>Execute(ringCount)</c> once too many (the earcut's <c>EarcutBatchJob</c> pattern verbatim — it
    /// defers over <c>PerPolyOuterCount</c> for the identical reason — job-scheduling-design.md §8 stage 6,
    /// E.2).
    ///
    /// <para><b>Writes the RAW per-ring output, unswapped and unrebased.</b> Each ring's vertices go to
    /// <see cref="RibbonBuffers.FlatVertices"/> at its own offset, byte for byte what
    /// <see cref="RibbonJob.Execute"/> produced; each ring's indices go to
    /// <see cref="RibbonBuffers.FlatIndices"/> RING-LOCAL (no <c>+offset</c> rebase) and UNSWAPPED. The
    /// 2nd/3rd winding swap and the running-offset rebase — <c>StyledLineTileBuilder.cs:398-403</c>'s rule —
    /// move to <see cref="RibbonAggregateJob"/>, which is what makes ring order (not scheduling order)
    /// the only thing that can affect final byte layout: relocating a formula is not changing it
    /// (job-scheduling-design.md §1).</para>
    ///
    /// <para><b>Bit-exact regardless of which worker runs a ring, or how many run at once</b>
    /// (job-scheduling-design.md §1): every index into <see cref="Buffers"/>' offset tables and flat columns
    /// is taken from CONSECUTIVE entries of a monotonic table (<see cref="RibbonSizingJob"/>'s own
    /// disjointness), so the bytes one ring touches are a function of that ring's own input alone.</para>
    ///
    /// <para><b>Zero allocation per ring</b> — the earcut's own <c>EarcutBatchJob</c> shape, not a scratch
    /// buffer: <see cref="RibbonJob.OutVertexCount"/>/<c>OutIndexCount</c> write straight into this
    /// ring's own single-element slice of <see cref="RibbonBuffers.PerRingVertexCount"/>/
    /// <c>PerRingIndexCount</c> via <c>GetSubArray(r, 1)</c>, the same way <c>EarcutBatchJob</c> writes
    /// <c>OutIndexCount</c> straight into <c>PerPolyIndexCount</c>. No per-<see cref="Execute"/>
    /// <see cref="Allocator.Temp"/> allocation.</para>
    ///
    /// <para><b>One field, one attribute (job-scheduling-design.md §8 stage 6, the A.2 branch, verified for
    /// the earcut and reused verbatim here).</b> <see cref="Buffers"/> is a struct nesting several
    /// <see cref="NativeList{T}"/> columns; <see cref="NativeDisableParallelForRestrictionAttribute"/> on
    /// this OUTER struct field is legal and suffices to write outside <c>index</c> through it. The attribute
    /// appears in this file and in <c>EarcutBatchJob.cs</c> — nowhere else (job-scheduling-design.md §7 rule
    /// 2, §8 stage 6's A0.3 resolution).</para>
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
