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
    /// The fill graph's gather node: per polygon, sort its hole rings (when <c>holeCount &gt; 1</c>) and copy
    /// outer + hole vertices into the polygon's flat slice. The comparer is a type parameter so a test can
    /// substitute a perturbed one; production uses <c>FillMeshPipeline.HoleRingComparer</c>. The hole-index
    /// buffer is <see cref="Allocator.Temp"/>, allocated inside <see cref="Execute"/>, because a Temp container
    /// may not be a job field.
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

        // ── Input — RingAssemblyJob's polygon descriptors (pre-sized arrays) ─────────────────────
        [ReadOnly] public NativeArray<int> PolyOuterRingIdx;
        [ReadOnly] public NativeArray<int> PolyHoleListStart;
        [ReadOnly] public NativeArray<int> PolyHoleCount;
        [ReadOnly] public NativeArray<int> HoleRingIdxs;

        /// <summary><c>[ReadOnly]</c> is load-bearing: it propagates to <typeparamref name="TComparer"/>'s
        /// nested containers, which are the SAME allocation as
        /// <see cref="Vertices"/>/<see cref="RingOffsets"/>. Without it the safety system sees one read-only
        /// and one implicitly-writable alias of that allocation and throws at schedule time.</summary>
        [ReadOnly] public TComparer Comparer;

        /// <summary>Reads <c>VertexOffsets</c>/<c>HoleCountOffsets</c>, resolved inside <see cref="Execute"/>
        /// under the graph's <c>.AsArray()</c> rule. Fills <c>FlatPolyVerts</c>/<c>FlatSortedHoleCounts</c>/
        /// <c>PerPolyFeatureIndex</c>, and <c>PerPolyOuterCount</c>, which nothing upstream reports per
        /// polygon and <see cref="EarcutBatchJob"/> needs.</summary>
        public TriangulationBuffers Buffers;

        public void Execute()
        {
            NativeArray<int>     vertexOffsets       = Buffers.VertexOffsets.AsArray();
            NativeArray<int>     holeCountOffsets    = Buffers.HoleCountOffsets.AsArray();
            NativeArray<double2> flatPolyVerts     = Buffers.FlatPolyVerts.AsArray();
            NativeArray<int>     flatSortedHoleCounts = Buffers.FlatSortedHoleCounts.AsArray();
            NativeArray<int>     perPolyFeatureIndex = Buffers.PerPolyFeatureIndex.AsArray();
            NativeArray<int>     perPolyOuterCount = Buffers.PerPolyOuterCount.AsArray();

            // Bound by PerPolyOuterCount's OWN length, never a borrowed polygon count: SizingJob's capacity
            // early-return leaves every Buffers column at length 0 and a borrowed count stale.
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
