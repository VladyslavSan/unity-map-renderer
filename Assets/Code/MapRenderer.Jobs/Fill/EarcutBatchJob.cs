using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The fill graph's earcut node: one polygon per <see cref="Execute"/> call, taking that polygon's
    /// <c>GetSubArray</c> view there and calling <see cref="EarcutJob.Execute"/> directly — <see cref="EarcutJob"/>'s
    /// fields are untouched, so its remaining caller (<c>BurstJobRunOffMainSpikeTests</c>) stays valid
    /// (job-scheduling-design.md §8 stage 1). <see cref="IJobParallelForDefer"/> since stage 6 — batched by
    /// <see cref="FillMeshGraph.EarcutPolygonBatch"/>, deferred over <see cref="FillMeshGraph"/>'s own
    /// <c>buffers.PerPolyOuterCount</c>, so the polygon count need not be known at schedule time.
    ///
    /// <para><b>Bit-exact regardless of which worker runs a polygon, or how many run at once</b>
    /// (job-scheduling-design.md §1): the construction is the slice derivation, not any assertion. Every
    /// index into <see cref="Buffers"/>' offset tables and flat columns is taken from CONSECUTIVE entries of
    /// a monotonic table (<c>FillSizingJob</c>'s own disjointness — see its type doc), so the bytes one
    /// polygon touches are a function of that polygon's own input alone.</para>
    ///
    /// <para><b>One field, one attribute (job-scheduling-design.md §8 stage 6, the A.2 branch).</b>
    /// <see cref="Buffers"/> is a struct nesting several <see cref="NativeList{T}"/> columns;
    /// <see cref="NativeDisableParallelForRestrictionAttribute"/> on this OUTER struct field is legal and
    /// suffices to write outside <c>index</c> through it — verified empirically (a throwaway probe that
    /// throws <c>InvalidOperationException</c> without the attribute — "is not declared [ReadOnly] in a
    /// IJobParallelFor job" — and runs clean with it). This is the SMALLER of two viable shapes; the
    /// alternative (declaring the 18 columns this job uses as the job's own deferred <see cref="NativeArray{T}"/>
    /// fields, split by read/write access) was probed first and also works, but costs more surface for no
    /// behavioural difference — the safety system already treats every column here as conservatively
    /// written, same as before this stage (<see cref="FillTriangulationBuffers"/>'s own doc). The attribute
    /// appears in this file and nowhere else (job-scheduling-design.md §7 rule 2).</para>
    ///
    /// <para><b>Reproduces <c>FillMeshPipeline.cs:384–461</c>'s per-polygon loop</b>: field-for-field the same
    /// <see cref="EarcutJob"/> population, over the flat scratch/output columns a prior sizing node
    /// (<c>FillSizingJob</c>) resized and a prior gather node (<see cref="FillGatherJob{TComparer}"/>)
    /// populated. Every column is resolved with <c>.AsArray()</c> <b>inside</b> <see cref="Execute"/> — never
    /// at schedule time — because the sizing node is what gives these lists their final length (the graph's
    /// <c>.AsArray()</c> rule, <c>FillMeshGraph.cs</c>'s doc); this holds under <see cref="IJobParallelForDefer"/>
    /// exactly as it did under the retired serial <see cref="IJob"/> — the resolution happens at EXECUTE
    /// time regardless of how many indices run, or when.</para>
    ///
    /// <para><b>No <see cref="Error"/> field.</b> The per-polygon capacity backstop this job used to write
    /// moved to <see cref="FillAggregateJob"/> (job-scheduling-design.md §8 stage 6, C.1) — a
    /// <see cref="NativeReference{T}"/> is not index-restricted, so a parallel writer would need its own
    /// disable attribute for a check that already has a cheaper, already-serial home downstream.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct EarcutBatchJob : IJobParallelForDefer
    {
        // ── Input ──────────────────────────────────────────────────────────────────────────────
        /// <summary>Hole count per polygon — <c>RingAssemblyJob.OutPolyHoleCount</c>, pre-sized and unresized
        /// for the graph's life, so a schedule-time field is safe here.</summary>
        [ReadOnly] public NativeArray<int> PolyHoleCount;

        /// <summary>The offset tables <c>FillSizingJob</c> sized, <c>FillGatherJob</c>'s
        /// <c>FlatPolyVerts</c>/<c>FlatSortedHoleCounts</c>/<c>PerPolyOuterCount</c> inputs, and this node's own
        /// <c>FlatIndexArrays</c>/scratch/per-polygon outputs — one field, not the individual list-per-column
        /// this job used to declare. See the type doc for why the disable attribute lives here.</summary>
        [NativeDisableParallelForRestriction]
        public FillTriangulationBuffers Buffers;

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
            NativeArray<double>  flatVx                   = Buffers.FlatVx.AsArray();
            NativeArray<double>  flatVy                   = Buffers.FlatVy.AsArray();
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
                Vx                   = flatVx.GetSubArray(sOff, sLen),
                Vy                   = flatVy.GetSubArray(sOff, sLen),
                Prev                 = flatPreviousIndex.GetSubArray(sOff, sLen),
                Next                 = flatNextIndex.GetSubArray(sOff, sLen),
                IsBridgeCopy         = flatIsBridge.GetSubArray(sOff, sLen),
                Removed              = flatRemoved.GetSubArray(sOff, sLen),
                IsEar                = flatIsEar.GetSubArray(sOff, sLen),
            }.Execute();
        }
    }
}
