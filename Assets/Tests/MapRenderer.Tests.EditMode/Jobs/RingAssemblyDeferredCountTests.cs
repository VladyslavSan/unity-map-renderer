// Unity EditMode only — NativeArray/NativeList, Burst jobs. NOT registered in core-tests.csproj.
//
// The load-bearing claim: RingAssemblyJob.RingOffsets, taken as list.AsDeferredJobArray() BEFORE the
// list is populated, resolves to the list's EXECUTE-time length (after a preceding scheduled job populates
// it), not its length at the moment AsDeferredJobArray() was called. This is documented Unity behaviour, but
// the graph's whole deferred-count mechanism depends on it, so it is proven here rather than assumed.

using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
namespace MapRenderer.Tests.Jobs
{
    [TestFixture]
    public class RingAssemblyDeferredCountTests
    {
        /// <summary>Trivial preceding job: populates the ring-offsets list AFTER the test has already taken
        /// its <c>AsDeferredJobArray()</c> view and built the (already-scheduled) consumer job around it.</summary>
        private struct SeedRingOffsetsJob : IJob
        {
            public NativeList<int> RingOffsets;
            public void Execute()
            {
                RingOffsets.Add(0);
                RingOffsets.Add(3); // one ring spanning [0, 3) — a single triangle
            }
        }

        [Test]
        public void RingCountFromOffsetsLength_ResolvesAtExecuteTime_NotAtDeferredViewCaptureTime()
        {
            var vertices = new NativeArray<double2>(3, Allocator.Persistent);
            vertices[0] = new double2(0.0, 0.0);
            vertices[1] = new double2(10.0, 0.0);
            vertices[2] = new double2(10.0, 10.0);

            var ringFeatureIdx = new NativeArray<int>(1, Allocator.Persistent);
            ringFeatureIdx[0] = 0;
            var featureKinds = new NativeArray<TileGeometryType>(1, Allocator.Persistent);
            featureKinds[0] = TileGeometryType.Polygon;

            // Empty at construction time — RingAssemblyJob's deferred view is captured over THIS emptiness.
            var ringOffsetsList = new NativeList<int>(Allocator.Persistent);
            Assert.AreEqual(0, ringOffsetsList.Length, "precondition: the list is empty when the deferred view is taken");

            var polyOuterIdx  = new NativeArray<int>(1, Allocator.Persistent);
            var polyHoleStart = new NativeArray<int>(1, Allocator.Persistent);
            var polyHoleCount = new NativeArray<int>(1, Allocator.Persistent);
            var holeRingIdxs  = new NativeArray<int>(1, Allocator.Persistent);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var holeCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            JobHandle seedHandle = new SeedRingOffsetsJob { RingOffsets = ringOffsetsList }.Schedule();

            var assemblyJob = new RingAssemblyJob
            {
                Vertices = vertices,
                RingOffsets = ringOffsetsList.AsDeferredJobArray(), // captured while the list is still empty
                RingFeatureIdx = ringFeatureIdx,
                RingCount = -1, // deliberately wrong sentinel: proves the bool steers away from this field
                RingCountFromOffsetsLength = true,
                FeatureGeometryType = featureKinds,
                OutPolyOuterRingIdx = polyOuterIdx, OutPolyHoleListStart = polyHoleStart, OutPolyHoleCount = polyHoleCount,
                OutHoleRingIdxs = holeRingIdxs, OutPolygonCount = polyCountArr, OutHoleCount = holeCountArr,
            };

            JobHandle handle = assemblyJob.Schedule(seedHandle);
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();

            Assert.AreEqual(1, polyCountArr[0],
                "the single seeded triangle ring must assemble into exactly one polygon — a count of 0 " +
                "means RingOffsets.Length resolved to the list's EMPTY capture-time state, not its " +
                "execute-time state after SeedRingOffsetsJob ran; a crash/garbage count means it read " +
                "RingCount (-1) instead of deriving from RingOffsets.Length.");
            Assert.AreEqual(0, holeCountArr[0], "no holes in a single-ring layer");

            vertices.Dispose(); ringFeatureIdx.Dispose(); featureKinds.Dispose(); ringOffsetsList.Dispose();
            polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose(); holeRingIdxs.Dispose();
            polyCountArr.Dispose(); holeCountArr.Dispose();
        }
    }
}
