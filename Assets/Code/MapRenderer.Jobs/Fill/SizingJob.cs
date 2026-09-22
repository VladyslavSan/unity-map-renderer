using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The fill graph's sizing node: the four prefix-sum offset tables, plus every flat scratch and output
    /// list that <see cref="FillGatherJob{TComparer}"/>, <see cref="EarcutBatchJob"/> and
    /// <see cref="AggregateJob"/> consume, sized once and up front. A single-threaded job may resize a list
    /// it owns. It also reports <see cref="FillGraphCounts.RingCount"/>, a one-line read of
    /// <see cref="RingOffsets"/>, which it already borrows.
    ///
    /// <para><b>Schedulable standalone</b> — its capacities are plain <c>int</c> fields, not derived from a
    /// borrowed input, so a test can hand it an undersized <see cref="MaxPolygons"/>/<see cref="MaxHoles"/>
    /// and observe the error flag without building a whole graph.</para>
    ///
    /// <para><b>Error, not a throw</b>, which a Burst job cannot raise. On overrun <see cref="Execute"/>
    /// returns before touching any output list, so every one stays at length 0.</para>
    ///
    /// <para><b>Clear vs. uninitialised must match, or the output differs silently.</b>
    /// <c>Buffers.FlatSortedHoleCounts</c>, <c>Buffers.PerPolyIndexCount</c> and
    /// <c>Buffers.PerPolyForceClip</c> are <see cref="NativeArrayOptions.ClearMemory"/>; every other flat
    /// list is <see cref="NativeArrayOptions.UninitializedMemory"/>.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct SizingJob : IJob
    {
        // ── Input — RingAssemblyJob's polygon descriptors and counts ──────────────────────────────
        [ReadOnly] public NativeArray<int> PolyOuterRingIdx;
        [ReadOnly] public NativeArray<int> PolyHoleListStart;
        [ReadOnly] public NativeArray<int> PolyHoleCount;
        [ReadOnly] public NativeArray<int> HoleRingIdxs;
        [ReadOnly] public NativeArray<int> PolyCountArr; // [0]
        [ReadOnly] public NativeArray<int> HoleCountArr; // [0]

        /// <summary>The derived ring buffer's offsets — needed to compute each polygon's outer/hole vertex
        /// counts.</summary>
        [ReadOnly] public NativeArray<int> RingOffsets;

        /// <summary>Upper bound on polygons this layer's flat scratch was allocated for.</summary>
        public int MaxPolygons;

        /// <summary>Upper bound on total holes this layer's flat scratch was allocated for.</summary>
        public int MaxHoles;

        /// <summary>The offset tables, flat scratch/output columns and per-polygon columns this node sizes
        /// and every downstream node reads or fills.</summary>
        public TriangulationBuffers Buffers;

        public NativeArray<FillGraphCounts> Counts;

        /// <summary>Set on capacity overrun.</summary>
        public NativeReference<int> Error;

        public void Execute()
        {
            int polyCount  = PolyCountArr[0];
            int totalHoles = HoleCountArr[0];

            if (polyCount > MaxPolygons)
            {
                Error.Value = FillGraphCounts.ErrorPolygonCapacity;
                return;
            }
            if (totalHoles > MaxHoles)
            {
                Error.Value = FillGraphCounts.ErrorHoleCapacity;
                return;
            }

            {
                FillGraphCounts c = Counts[0];
                c.HoleCount = totalHoles;
                c.RingCount = math.max(0, RingOffsets.Length - 1);
                Counts[0] = c;
            }

            NativeList<int> vertexOffsets    = Buffers.VertexOffsets;
            NativeList<int> holeCountOffsets = Buffers.HoleCountOffsets;
            NativeList<int> workOffsets = Buffers.WorkOffsets;
            NativeList<int> indexOffsets     = Buffers.IndexOffsets;

            vertexOffsets.Add(0); holeCountOffsets.Add(0); workOffsets.Add(0); indexOffsets.Add(0);

            for (int pi = 0; pi < polyCount; pi++)
            {
                int outerRi   = PolyOuterRingIdx[pi];
                int outerLen  = RingOffsets[outerRi + 1] - RingOffsets[outerRi];
                int holeCount = PolyHoleCount[pi];
                int hStart    = PolyHoleListStart[pi];

                int holeVertTotal = 0;
                for (int hi = 0; hi < holeCount; hi++)
                {
                    int hri = HoleRingIdxs[hStart + hi];
                    holeVertTotal += RingOffsets[hri + 1] - RingOffsets[hri];
                }

                int polyVC        = outerLen + holeVertTotal;
                int baseCap       = polyVC + holeCount * 2;
                int splitBudget   = math.min(EarcutJob.MaxSplits, math.max(8, holeCount * 4));
                int workCap       = baseCap + splitBudget * 2;
                int idxCap        = workCap > 2 ? (workCap - 2) * 3 : 3;
                int sortedHoleLen = holeCount > 0 ? holeCount : 1;

                vertexOffsets.Add(vertexOffsets[pi] + polyVC);
                holeCountOffsets.Add(holeCountOffsets[pi] + sortedHoleLen);
                workOffsets.Add(workOffsets[pi] + workCap);
                indexOffsets.Add(indexOffsets[pi] + idxCap);
            }

            // Defence-in-depth. A non-monotonic table would otherwise surface only as an Editor-only
            // GetSubArray bounds throw downstream; this turns it into an error code compiled into every
            // build. Strictly increasing holds: workCap >= 16, idxCap >= 3, sortedHoleLen >= 1, and
            // polyVC >= 3 because RingAssemblyJob skips any ring shorter than 3 vertices.
            //
            // This return, and the two capacity returns above, protect the whole downstream chain: every
            // column a downstream node bounds its own loop by stays at length 0. The four offset tables may
            // already be non-empty here, but no node bounds a loop by one of them.
            for (int pi = 0; pi < polyCount; pi++)
            {
                if (vertexOffsets[pi + 1] <= vertexOffsets[pi] || holeCountOffsets[pi + 1] <= holeCountOffsets[pi] ||
                    workOffsets[pi + 1] <= workOffsets[pi] || indexOffsets[pi + 1] <= indexOffsets[pi])
                {
                    Error.Value = FillGraphCounts.ErrorOffsetTableNotDisjoint;
                    return;
                }
            }

            Buffers.FlatPolyVerts.Resize(vertexOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
            Buffers.FlatSortedHoleCounts.Resize(holeCountOffsets[polyCount], NativeArrayOptions.ClearMemory);
            Buffers.FlatIndexArrays.Resize(indexOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
            Buffers.FlatWorkVerts.Resize(workOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
            Buffers.FlatPreviousIndex.Resize(workOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
            Buffers.FlatNextIndex.Resize(workOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
            Buffers.FlatIsBridge.Resize(workOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
            Buffers.FlatRemoved.Resize(workOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
            Buffers.FlatIsEar.Resize(workOffsets[polyCount], NativeArrayOptions.UninitializedMemory);

            Buffers.PerPolyIndexCount.Resize(polyCount, NativeArrayOptions.ClearMemory);
            Buffers.PerPolyForceClip.Resize(polyCount, NativeArrayOptions.ClearMemory);
            Buffers.PerPolyMergedVertexCount.Resize(polyCount, NativeArrayOptions.UninitializedMemory);
            Buffers.PerPolyFeatureIndex.Resize(polyCount, NativeArrayOptions.UninitializedMemory);
            Buffers.PerPolyOuterCount.Resize(polyCount, NativeArrayOptions.UninitializedMemory);
        }
    }
}
