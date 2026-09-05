using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

// Generic Burst jobs must be registered so IL2CPP/AOT compiles the concrete instantiation the graph uses in
// production (the Editor JIT-compiles it on first use without this — same rule as ProjectPointsJob.cs).
[assembly: RegisterGenericJobType(typeof(MapRenderer.Jobs.Fill.FillGatherJob<MapRenderer.Jobs.Fill.FillMeshPipeline.HoleRingComparer>))]

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The fill graph's gather node: reproduces <c>FillMeshPipeline.cs:384–421</c>'s populate pass — per
    /// polygon, sort its hole rings (only when <c>holeCount &gt; 1</c>, the same skip as the synchronous
    /// pipeline) and copy outer + hole vertices into the polygon's flat scratch slice
    /// (job-scheduling-design.md §3.2, §8 stage 1).
    ///
    /// <para><b>Comparer as a generic type parameter, not a fixed field type.</b> Production always
    /// instantiates <see cref="FillGatherJob{TComparer}"/> with <c>FillMeshPipeline.HoleRingComparer</c> — the
    /// SAME struct <c>FillMeshPipeline.Schedule</c> sorts with, so the two arms share one copy of the ordering.
    /// The type parameter exists so a test can substitute a graph-local perturbed comparer to RED-verify the
    /// parity tooth's ordering claim without touching the shared comparer both arms read
    /// (<c>FillMeshGraphParityTests</c>'s hazard note).</para>
    ///
    /// <para>The reused hole-index buffer is an <see cref="Allocator.Temp"/> <see cref="NativeArray{T}"/>
    /// allocated <b>inside</b> <see cref="Execute"/>, sized from a first pass over
    /// <see cref="PolyHoleCount"/> — the job-scratch rule (<c>Allocator.Temp</c> may not be a job field).</para>
    ///
    /// <para><b>Bounds its own loop by <c>Buffers.PerPolyOuterCount.Length</c>, never a borrowed polygon
    /// count</b> — see <see cref="Execute"/>'s own comment for the mechanism, mirroring
    /// <see cref="FillAggregateJob"/>'s own doc. This was a real, pre-existing bug found and fixed alongside
    /// the identical defect in <see cref="FillAggregateJob"/>: a borrowed count does not shrink when
    /// <see cref="FillSizingJob"/> returns early, so this job would otherwise write past a length-0
    /// <c>PerPolyFeatureIndex</c>/<c>PerPolyOuterCount</c> — silent, since <see cref="NativeArray{T}"/>'s
    /// indexer bounds check is <c>[Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]</c> and compiled OUT of a
    /// release player build.</para>
    /// </summary>
    /// <typeparam name="TComparer">The hole-ordering comparer — a stateless struct over ring indices.</typeparam>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct FillGatherJob<TComparer> : IJob
        where TComparer : struct, IComparer<int>
    {
        // ── Input — the derived ring buffer (borrowed) ────────────────────────────────────────────
        [ReadOnly] public NativeArray<double2> Vertices;
        [ReadOnly] public NativeArray<int>     RingOffsets;
        [ReadOnly] public NativeArray<int>     RingFeatureIdx;

        // ── Input — RingAssemblyJob's polygon descriptors (unmodified node, pre-sized arrays) ─────
        [ReadOnly] public NativeArray<int> PolyOuterRingIdx;
        [ReadOnly] public NativeArray<int> PolyHoleListStart;
        [ReadOnly] public NativeArray<int> PolyHoleCount;
        [ReadOnly] public NativeArray<int> HoleRingIdxs;

        /// <summary><c>[ReadOnly]</c> here propagates down to <typeparamref name="TComparer"/>'s own nested
        /// containers (<c>HoleRingComparer</c>'s two <see cref="NativeArray{T}"/> fields) — needed because
        /// <see cref="Vertices"/>/<see cref="RingOffsets"/> and the comparer's fields are the SAME underlying
        /// allocation (the comparer is built from this job's own arrays): without it the safety system sees
        /// one read-only and one implicitly-writable alias of that allocation and throws at schedule time
        /// (<c>InvalidOperationException</c>, "two containers may not be the same (aliasing)") — caught by
        /// <c>FillGraphBurstProbeTests</c>.</summary>
        [ReadOnly] public TComparer Comparer;

        /// <summary>Reads <c>VertexOffsets</c>/<c>HoleCountOffsets</c> (resolved inside <see cref="Execute"/>, the
        /// graph's <c>.AsArray()</c> rule); fills <c>FlatPolyVerts</c>/<c>FlatSortedHoleCounts</c>/
        /// <c>PerPolyFeatureIndex</c> (<c>FillMeshPipeline.cs:387</c>'s capture) and <c>PerPolyOuterCount</c>
        /// (<see cref="EarcutBatchJob"/> needs it alongside <see cref="PolyHoleCount"/> to populate
        /// <c>EarcutJob.OuterCount</c>; nothing upstream reports it per-polygon — only the outer ring INDEX,
        /// <see cref="PolyOuterRingIdx"/>).</summary>
        public FillTriangulationBuffers Buffers;

        public void Execute()
        {
            NativeArray<int>     vertexOffsets       = Buffers.VertexOffsets.AsArray();
            NativeArray<int>     holeCountOffsets    = Buffers.HoleCountOffsets.AsArray();
            NativeArray<double2> flatPolyVerts     = Buffers.FlatPolyVerts.AsArray();
            NativeArray<int>     flatSortedHoleCounts = Buffers.FlatSortedHoleCounts.AsArray();
            NativeArray<int>     perPolyFeatureIndex = Buffers.PerPolyFeatureIndex.AsArray();
            NativeArray<int>     perPolyOuterCount = Buffers.PerPolyOuterCount.AsArray();

            // Bound by PerPolyOuterCount's OWN length, never a borrowed polygon count — FillSizingJob's own
            // capacity/monotonicity early returns leave every Buffers column at length 0 without touching a
            // borrowed count, so bounding by one here would still loop over a zero-length list (see
            // FillSizingJob's and FillAggregateJob's own docs for the mechanism and history).
            int polyCount = perPolyOuterCount.Length;

            int maxHoleCount = 0;
            for (int pi = 0; pi < polyCount; pi++) maxHoleCount = math.max(maxHoleCount, PolyHoleCount[pi]);
            var holeRIs = new NativeArray<int>(math.max(1, maxHoleCount), Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for (int pi = 0; pi < polyCount; pi++)
            {
                int outerRi    = PolyOuterRingIdx[pi];
                perPolyFeatureIndex[pi] = RingFeatureIdx[outerRi];
                int outerStart = RingOffsets[outerRi];
                int outerLen   = RingOffsets[outerRi + 1] - outerStart;
                perPolyOuterCount[pi] = outerLen;
                int holeCount  = PolyHoleCount[pi];
                int hStart     = PolyHoleListStart[pi];

                for (int hi = 0; hi < holeCount; hi++)
                    holeRIs[hi] = HoleRingIdxs[hStart + hi];

                if (holeCount > 1)
                    holeRIs.GetSubArray(0, holeCount).Sort(Comparer);

                var polyVerts        = flatPolyVerts.GetSubArray(vertexOffsets[pi], vertexOffsets[pi + 1] - vertexOffsets[pi]);
                var sortedHoleCounts = flatSortedHoleCounts.GetSubArray(holeCountOffsets[pi], holeCountOffsets[pi + 1] - holeCountOffsets[pi]);

                for (int i = 0; i < outerLen; i++)
                    polyVerts[i] = Vertices[outerStart + i];

                int vPos = outerLen;
                for (int hi = 0; hi < holeCount; hi++)
                {
                    int hri    = holeRIs[hi];
                    int hBegin = RingOffsets[hri];
                    int hLen   = RingOffsets[hri + 1] - hBegin;
                    sortedHoleCounts[hi] = hLen;
                    for (int i = 0; i < hLen; i++)
                        polyVerts[vPos++] = Vertices[hBegin + i];
                }
            }

            holeRIs.Dispose();
        }
    }
}
