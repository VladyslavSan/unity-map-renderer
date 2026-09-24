using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The fill graph's earcut node: one polygon per <see cref="Execute"/>, calling
    /// <see cref="EarcutJob.Execute"/> on its <c>GetSubArray</c> views, deferred over
    /// <c>buffers.PerPolyOuterCount</c>. It is bit-exact on any worker, because every index comes from
    /// consecutive entries of a monotonic table. Non-local invariant: the one
    /// <see cref="NativeDisableParallelForRestrictionAttribute"/> on <see cref="Buffers"/> covers its nested
    /// columns, and each column resolves <c>.AsArray()</c> inside <see cref="Execute"/>, after sizing.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct EarcutBatchJob : IJobParallelForDefer
    {
        // ── Input ──────────────────────────────────────────────────────────────────────────────
        /// <summary>Hole count per polygon — <c>RingAssemblyJob.OutPolyHoleCount</c>, pre-sized and unresized
        /// for the graph's life, so a schedule-time field is safe here.</summary>
        [ReadOnly] public NativeArray<int> PolyHoleCount;

        /// <summary>The offset tables <c>SizingJob</c> sized, <c>FillGatherJob</c>'s
        /// <c>FlatPolyVerts</c>/<c>FlatSortedHoleCounts</c>/<c>PerPolyOuterCount</c> inputs, and this node's
        /// own index/scratch/per-polygon outputs. See the type doc for why the disable attribute lives
        /// here.</summary>
        [NativeDisableParallelForRestriction]
        public TriangulationBuffers Buffers;

        public void Execute(int pi)
        {
            NativeArray<int> vertexOffsets       = Buffers.VertexOffsets.AsArray();
            NativeArray<int> holeCountOffsets    = Buffers.HoleCountOffsets.AsArray();
            NativeArray<int> workOffsets       = Buffers.WorkOffsets.AsArray();
            NativeArray<int> indexOffsets        = Buffers.IndexOffsets.AsArray();
            NativeArray<int> perPolyOuterCount = Buffers.PerPolyOuterCount.AsArray();

            NativeArray<double2> flatPolyVerts            = Buffers.FlatPolyVerts.AsArray();
            NativeArray<int>     flatSortedHoleCounts     = Buffers.FlatSortedHoleCounts.AsArray();
            NativeArray<int>     flatIndexArrays          = Buffers.FlatIndexArrays.AsArray();
            NativeArray<double2> flatWorkVerts            = Buffers.FlatWorkVerts.AsArray();
            NativeArray<int>     flatPreviousIndex        = Buffers.FlatPreviousIndex.AsArray();
            NativeArray<int>     flatNextIndex            = Buffers.FlatNextIndex.AsArray();
            NativeArray<bool>    flatIsBridge             = Buffers.FlatIsBridge.AsArray();
            NativeArray<bool>    flatRemoved              = Buffers.FlatRemoved.AsArray();
            NativeArray<bool>    flatIsEar                = Buffers.FlatIsEar.AsArray();
            NativeArray<int>     perPolyIndexCount        = Buffers.PerPolyIndexCount.AsArray();
            NativeArray<int>     perPolyForceClip         = Buffers.PerPolyForceClip.AsArray();
            NativeArray<int>     perPolyMergedVertexCount = Buffers.PerPolyMergedVertexCount.AsArray();

            int outerLen  = perPolyOuterCount[pi];
            int holeCount = PolyHoleCount[pi];
            int sOff = workOffsets[pi], sLen = workOffsets[pi + 1] - sOff;

            new EarcutJob
            {
                PolyVertices         = flatPolyVerts.GetSubArray(vertexOffsets[pi], vertexOffsets[pi + 1] - vertexOffsets[pi]),
                OuterCount           = outerLen,
                SortedHoleCounts     = flatSortedHoleCounts.GetSubArray(holeCountOffsets[pi], holeCountOffsets[pi + 1] - holeCountOffsets[pi]),
                HoleCount            = holeCount,
                OutIndices           = flatIndexArrays.GetSubArray(indexOffsets[pi], indexOffsets[pi + 1] - indexOffsets[pi]),
                OutIndexOffset       = 0,
                OutIndexCount        = perPolyIndexCount.GetSubArray(pi, 1),
                OutForceClipCount    = perPolyForceClip.GetSubArray(pi, 1),
                OutMergedVertexCount = perPolyMergedVertexCount.GetSubArray(pi, 1),
                Verts                = flatWorkVerts.GetSubArray(sOff, sLen),
                Prev                 = flatPreviousIndex.GetSubArray(sOff, sLen),
                Next                 = flatNextIndex.GetSubArray(sOff, sLen),
                IsBridgeCopy         = flatIsBridge.GetSubArray(sOff, sLen),
                Removed              = flatRemoved.GetSubArray(sOff, sLen),
                IsEar                = flatIsEar.GetSubArray(sOff, sLen),
            }.Execute();
        }
    }
}
