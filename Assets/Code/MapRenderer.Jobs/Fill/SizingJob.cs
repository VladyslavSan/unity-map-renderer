using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Fill
{
    /// <summary>
    /// The fill graph's sizing node: reproduces <c>FillMeshPipeline.cs:312–371</c>'s earcut-scratch sizing
    /// pass — the four prefix-sum offset tables plus every flat scratch/output list downstream nodes
    /// (<see cref="FillGatherJob{TComparer}"/>, <see cref="EarcutBatchJob"/>, <see cref="AggregateJob"/>)
    /// consume, sized once and up front (job-scheduling-design.md §3.2, §8 stage 1) and held as one
    /// <see cref="TriangulationBuffers"/> field (§8 stage 4's R2 reshape). A single-threaded job may resize a list
    /// it owns.
    ///
    /// <para><b>Also reports the ring-count statistic.</b> <see cref="RingOffsets"/> is already a borrowed
    /// input here (needed to compute each polygon's outer/hole vertex counts), so <see cref="Counts"/>[0]'s
    /// <see cref="FillGraphCounts.RingCount"/> is a one-line read of a value this node already holds — no
    /// dedicated node/edge, unlike the deleted <c>FillRingCountJob</c> whose sole product this was.</para>
    ///
    /// <para><b>Schedulable standalone</b> — its capacities are plain <c>int</c> fields, not derived from a
    /// borrowed input, so a test can hand it an undersized <see cref="MaxPolygons"/>/<see cref="MaxHoles"/>
    /// and observe the error flag without building a whole graph.</para>
    ///
    /// <para><b>Error, not a throw.</b> <see cref="Error"/> is set — never
    /// <see cref="FillMeshPipeline.EnsureCapacity"/>'s throw, which a Burst job cannot raise — and every
    /// output list is left at length 0 (nothing written past capacity): <see cref="Execute"/> returns before
    /// touching any of them.</para>
    ///
    /// <para><b>Clear vs. uninitialised is reproduced exactly</b> (must match, or output differs silently):
    /// <c>Buffers.FlatSortedHoleCounts</c>, <c>Buffers.PerPolyIndexCount</c> and <c>Buffers.PerPolyForceClip</c>
    /// are <see cref="NativeArrayOptions.ClearMemory"/> (<c>FillMeshPipeline.cs:356,368,369</c>); every other
    /// flat list is <see cref="NativeArrayOptions.UninitializedMemory"/>.</para>
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

                // Mirrors FillMeshPipeline.cs's scratch-capacity sizing exactly — see that method's doc for
                // the base/split-headroom rationale.
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

            // Defence-in-depth, NOT the parallel earcut's precondition (job-scheduling-design.md §8 stage 6,
            // C.2 — see this job's own type doc). A non-monotonic table here would surface only as an
            // Editor-only GetSubArray bounds throw downstream; this turns that into an error code compiled
            // into every build. Strictly increasing is right: workCap >= 16 (baseCap + splitBudget*2 with
            // splitBudget >= 8, above), idxCap >= 3 (above, explicitly), sortedHoleLen >= 1 (above,
            // explicitly), and polyVC >= 3 because RingAssemblyJob.cs skips any ring with rLen < 3 — so
            // outerLen >= 3 is guaranteed before this job ever sees it.
            //
            // This return (and the two capacity returns above) protects the WHOLE downstream chain: every
            // column a downstream node bounds its own loop by stays at length 0 (the four offset tables above
            // may already be non-empty on THIS return — they are filled before this check — but no node
            // bounds a loop by one of them). Every node that holds a sizing-owned buffer struct
            // (TriangulationBuffers/RibbonBuffers) bounds its own loop — or its deferred count — by a
            // column its sizing job resizes, never a borrowed count that job's early return does not touch.
            // Nodes past the aggregate bound by columns the AGGREGATE sizes, and inherit emptiness through
            // it. A downstream node re-introducing a borrowed count as its loop bound would silently reopen
            // the release-build OOB this stage found and fixed — FillSizingJobTests
            // .SizingCapacityOverrun_LeavesGatherAndAggregate_WithNothingToDo is the observing tooth for
            // FillGatherJob.Execute and AggregateJob.Execute; EarcutBatchJob has no loop of its own to
            // bound (deferred over buffers.PerPolyOuterCount) — FillMeshGraphStructureTests pins that
            // deferred-count source structurally instead.
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
