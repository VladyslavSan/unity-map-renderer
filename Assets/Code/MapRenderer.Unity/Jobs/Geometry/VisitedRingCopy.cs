using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace MapRenderer.Unity.Jobs.Geometry
{
    /// <summary>
    /// The one schedule site for <c>RingClipJob</c> / <c>RingSelectJob</c>: the fill and
    /// fill-extrusion graphs both copy their visited rings through this select-or-clip branch. The returned
    /// handle covers both the written output lists AND the freed clip scratch, so a caller cannot drop the
    /// dispose. The caller owns the three output lists.
    /// </summary>
    internal static class VisitedRingCopy
    {
        /// <summary>Main-thread pre-pass over borrowed inputs only, never over a job output — the longest
        /// visited ring and the total visited-vertex count, both needed to size the copy step.</summary>
        /// <param name="visit">Ring indices to visit, not <c>0..ringCount</c>.</param>
        /// <param name="maxRingLen">Longest visited ring's vertex count.</param>
        /// <param name="totalVerts">Sum of every visited ring's vertex count.</param>
        internal static void Measure(
            TileGeometryBuffers source, NativeArray<int> visit, out int maxRingLen, out int totalVerts)
        {
            maxRingLen = 0;
            totalVerts = 0;
            for (int k = 0; k < visit.Length; k++)
            {
                int ri  = visit[k];
                int len = source.RingOffsets[ri + 1] - source.RingOffsets[ri];
                maxRingLen  = math.max(maxRingLen, len);
                totalVerts += len;
            }
        }

        /// <summary>Schedules <c>RingClipJob</c> when <paramref name="clipEnabled"/>, else
        /// <c>RingSelectJob</c>, over <paramref name="source"/>'s visited rings. The clip arm's
        /// ping-pong buffers are raw <c>Allocator.Persistent</c> arrays, disposed by the returned
        /// handle — they sit outside the caller's own <c>NewBuffer</c>/<c>ScheduleDispose</c> pairing count.</summary>
        /// <param name="maxRingLen">From <see cref="Measure"/>; sizes the clip arm's ping-pong buffers.</param>
        /// <param name="deps">Upstream dependency both arms schedule against.</param>
        internal static JobHandle Schedule(
            TileGeometryBuffers source, NativeArray<int> visit, int maxRingLen,
            bool clipEnabled, double2 clipMin, double2 clipMax,
            NativeList<double2> outVertices, NativeList<int> outRingOffsets, NativeList<int> outRingFeatureIdx,
            JobHandle deps)
        {
            if (clipEnabled)
            {
                int bufferCap = math.max(1, maxRingLen * RingClipJob.BufferLengthMultiplier);
                var bufferA = new NativeArray<double2>(bufferCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var bufferB = new NativeArray<double2>(bufferCap, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                JobHandle copied = new RingClipJob
                {
                    Vertices = source.Vertices, RingOffsets = source.RingOffsets, RingFeatureIdx = source.RingFeatureIdx,
                    RingVisitOrder = visit, ClipMin = clipMin, ClipMax = clipMax,
                    BufferA = bufferA, BufferB = bufferB,
                    OutVertices = outVertices, OutRingOffsets = outRingOffsets, OutRingFeatureIdx = outRingFeatureIdx,
                }.Schedule(deps);

                return JobHandle.CombineDependencies(copied, bufferA.Dispose(copied), bufferB.Dispose(copied));
            }

            return new RingSelectJob
            {
                Vertices = source.Vertices, RingOffsets = source.RingOffsets, RingFeatureIdx = source.RingFeatureIdx,
                RingVisitOrder = visit,
                OutVertices = outVertices, OutRingOffsets = outRingOffsets, OutRingFeatureIdx = outRingFeatureIdx,
            }.Schedule(deps);
        }
    }
}
