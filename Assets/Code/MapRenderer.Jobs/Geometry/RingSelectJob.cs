using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Jobs.Geometry
{
    /// <summary>
    /// Burst job: copies the rings named by <see cref="RingVisitOrder"/> out of a <b>borrowed</b> tile-geometry
    /// buffer into fresh length-authoritative lists, <b>in visit order</b> — the clip-disabled twin of
    /// <see cref="RingClipJob"/>.
    ///
    /// <para><b>Why it exists.</b> Since IR B7 a consumer no longer controls what a materializer decodes; it
    /// controls which of the shared buffer's rings it visits, and in what order. When the tile-buffer clip is
    /// disabled there is nothing to clip, but the selection and the ordering still have to happen — so this
    /// job is exactly <see cref="RingClipJob"/>'s bbox-inside fast path (append the ring's vertices, push the
    /// offset, push the feature index) with the clipping removed.</para>
    ///
    /// <para><b>Verbatim copy, no filtering.</b> Every visited ring is emitted, including a ring with fewer
    /// than 3 vertices and including an empty one: this stage's output ring count is reported all the way out
    /// all the way out to the graph's own ring count, and the length filters belong to the consumers
    /// (<see cref="RingAssemblyJob"/>'s <c>&lt; 3</c>, line's <c>&lt; 2</c>). A filter here would silently
    /// change a reported count and, worse, would make this job and the clip path disagree about what "the
    /// rings I asked for" means.</para>
    ///
    /// <para><b>Coordinate space.</b> Pure copy — input and output are the same tile-space
    /// <c>double2</c>, same winding, same values. Does NOT reference <c>MapRenderer.Core</c> code (same rule
    /// as <see cref="RingClipJob"/>).</para>
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
