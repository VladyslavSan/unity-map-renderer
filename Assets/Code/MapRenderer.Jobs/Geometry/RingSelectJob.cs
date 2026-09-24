using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Geometry
{
    /// <summary>
    /// Burst job: copies the rings named by <see cref="RingVisitOrder"/> out of a <b>borrowed</b> tile-geometry
    /// buffer into fresh length-authoritative lists, <b>in visit order</b> — the clip-disabled twin of
    /// <see cref="RingClipJob"/>, equal to its bbox-inside fast path. Tile space, winding and values pass
    /// through unchanged. Non-local invariant: it emits every visited ring, short or empty ones included,
    /// because the graph reports this ring count and the length filters belong to the consumers.
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    public struct RingSelectJob : IJob
    {
        // ── Input (borrowed — never written, never disposed here) ──────────────────────────────
        [ReadOnly] public NativeArray<double2> Vertices;
        [ReadOnly] public NativeArray<int>     RingOffsets;    // length = ringCount + 1 (sentinel)
        [ReadOnly] public NativeArray<int>     RingFeatureIdx; // which feature each ring belongs to

        /// <summary>Ring indices into <see cref="RingOffsets"/>, in the order the consumer wants them
        /// visited. Its length IS the output ring count.</summary>
        [ReadOnly] public NativeArray<int> RingVisitOrder;

        // ── Output ─────────────────────────────────────────────────────────────────────────────
        // Same start+sentinel layout the decode stage produces, so RingAssemblyJob consumes them unchanged.
        public NativeList<double2> OutVertices;
        public NativeList<int>     OutRingOffsets;
        public NativeList<int>     OutRingFeatureIdx;

        public void Execute()
        {
            OutVertices.Clear();
            OutRingOffsets.Clear();
            OutRingFeatureIdx.Clear();
            OutRingOffsets.Add(0);

            for (int k = 0; k < RingVisitOrder.Length; k++)
            {
                int ri     = RingVisitOrder[k];
                int rStart = RingOffsets[ri];
                int rLen   = RingOffsets[ri + 1] - rStart;

                for (int i = 0; i < rLen; i++)
                    OutVertices.Add(Vertices[rStart + i]);

                OutRingOffsets.Add(OutVertices.Length);
                OutRingFeatureIdx.Add(RingFeatureIdx[ri]);
            }
        }
    }
}
