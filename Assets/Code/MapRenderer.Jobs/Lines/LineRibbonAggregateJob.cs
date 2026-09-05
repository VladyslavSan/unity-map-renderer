using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geometry;

namespace MapRenderer.Jobs.Lines
{
    /// <summary>
    /// The line graph's ribbon aggregate node (job-scheduling-design.md §8 stage 6, E.2): walks rings IN
    /// RING ORDER, appending each ring's REAL (not oversized-capacity) vertex/index span from
    /// <see cref="LineRibbonBuffers"/>' flat columns into the graph's actual output lists — the retired
    /// serial <see cref="LineRibbonBatchJob"/>'s per-ring append loop, moved here verbatim, including the
    /// 2nd/3rd winding swap (<c>StyledLineTileBuilder.cs:398-403</c>) and the running <c>offset</c> rebase.
    /// Relocating a formula is not changing it (job-scheduling-design.md §1) — <see cref="LineRibbonBatchJob"/>
    /// itself now writes RAW, ring-local, unswapped bytes; this node is where they become the graph's real,
    /// globally-offset, winding-correct output.
    ///
    /// <para><b>The overflow rule moves here too, unchanged</b> (job-scheduling-design.md §8 stage 6, E.3):
    /// the retired serial job's <c>MaxOutputVertices</c> check broke out of the ring loop — an
    /// order-dependent early stop. Parallel rings all compute regardless in
    /// <see cref="LineRibbonBatchJob"/>; this serial node applies the IDENTICAL test in the IDENTICAL ring
    /// order and stops at the identical ring, which is what keeps this bit-exact. Never evaluated in the
    /// parallel node.</para>
    ///
    /// <para>The three <c>Clear()</c> calls belong to this node now, not the parallel one. No unconditional
    /// <see cref="LineGraphCounts.Ok"/> reset — see <see cref="Execute"/>'s own comment for why.</para>
    ///
    /// <para><b>Bounds its own loop by <c>PerRingVertexCount.Length</c>, never a borrowed ring count</b> — see
    /// <see cref="Execute"/>'s own comment for the mechanism. This was a real bug shipped and fixed during
    /// this stage: a borrowed count (<c>RingSubOffsets.Length - 1</c>) does not shrink when
    /// <see cref="LineRibbonSizingJob"/> returns early, so this job would otherwise index a length-0
    /// <c>PerRingVertexCount</c>/<c>PerRingIndexCount</c> — silent, since <see cref="NativeList{T}"/>'s
    /// indexer bounds check is <c>[Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]</c> and compiled OUT of a
    /// release player build.</para>
    /// </summary>
    [BurstCompile(CompileSynchronously = true, OptimizeFor = OptimizeFor.Performance)]
    internal struct LineRibbonAggregateJob : IJob
    {
        [ReadOnly] public NativeList<int> RingFeature; // length = ringCount

        public LineRibbonBuffers Buffers;
        public int MaxOutputVertices;

        public NativeList<LineRibbonVertex> OutVertices;
        public NativeList<int>              OutVertexFeatureIdx;
        public NativeList<int>              OutIndices;
        public NativeReference<int>         Error;

        public void Execute()
        {
            OutVertices.Clear();
            OutVertexFeatureIdx.Clear();
            OutIndices.Clear();
            // No unconditional Error.Value = Ok reset here — LineGraphOutput.AllocateError() already hands
            // out a zero-initialised NativeReference<int> (Ok == 0), and LineRibbonSizingJob shares this SAME
            // reference: an unconditional reset here would silently discard a sizing-stage error, since this
            // node runs AFTER it in the chain (mirrors FillAggregateJob's own posture — neither fill node
            // ever resets FillGraphOutput's Error to Ok, they only conditionally SET it).

            NativeList<int> ringVertexOffsets = Buffers.RingVertexOffsets;
            NativeList<int> ringIndexOffsets  = Buffers.RingIndexOffsets;
            NativeList<LineRibbonVertex> flatVertices = Buffers.FlatVertices;
            NativeList<int>              flatIndices  = Buffers.FlatIndices;
            NativeList<int> perRingVertexCount = Buffers.PerRingVertexCount;
            NativeList<int> perRingIndexCount  = Buffers.PerRingIndexCount;

            // Bound by PerRingVertexCount's OWN length, never RingSubOffsets.Length - 1 — a borrowed input
            // LineRibbonSizingJob's own early return (a monotonicity failure, before its Resize calls) never
            // touches. That early return leaves PerRingVertexCount/PerRingIndexCount at length 0; bounding by
            // RingSubOffsets would still loop `ringCount` times over a zero-length list — NativeList's
            // indexer bounds check is [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")], compiled OUT of a
            // release player build, so that read is not a throw there, it is Ptr[index] past the allocation.
            int ringCount = perRingVertexCount.Length;

            for (int r = 0; r < ringCount; r++)
            {
                int nv = perRingVertexCount[r];
                int ni = perRingIndexCount[r];
                if (nv == 0 || ni == 0) continue;

                if (OutVertices.Length + nv > MaxOutputVertices)
                {
                    Error.Value = LineGraphCounts.ErrorLineVertexCapacity;
                    break;
                }

                int featIdx = RingFeature[r];
                int vOff = ringVertexOffsets[r];
                int iOff = ringIndexOffsets[r];
                int offset = OutVertices.Length;

                for (int k = 0; k < nv; k++)
                {
                    OutVertices.Add(flatVertices[vOff + k]);
                    OutVertexFeatureIdx.Add(featIdx);
                }

                // Reverse triangle winding at this GPU-index boundary — mirrors the fill reversal; see
                // LineRibbonBatchJob's own doc for why this formula lives here now, not there.
                for (int k = 0; k + 2 < ni; k += 3)
                {
                    OutIndices.Add(offset + flatIndices[iOff + k + 0]);
                    OutIndices.Add(offset + flatIndices[iOff + k + 2]); // 2nd/3rd
                    OutIndices.Add(offset + flatIndices[iOff + k + 1]); // swapped
                }
            }
        }
    }
}
