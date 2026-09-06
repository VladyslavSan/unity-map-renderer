// Unity EditMode only — NativeArray/NativeList, Burst jobs. NOT registered in core-tests.csproj.
//
// job-scheduling-design.md §8 stage 1: a capacity smaller than the handed polygon count sets
// the error flag and writes nothing past capacity — the flag is MORE observable than the throw it replaces
// (FillMeshPipeline.EnsureCapacity), because this exact scenario is reachable in a unit test, unlike the
// synchronous pipeline's never-fired backstop.
//
// job-scheduling-design.md §8 stage 6, C.2: the offset-table monotonicity assertion. Defence-in-depth, NOT
// the parallel earcut's bit-exactness precondition (that is structural — see EarcutBatchJob's own doc); what
// it catches is a non-monotonic table surfacing as a player-build error code instead of an Editor-only
// GetSubArray bounds throw. This file already drives FillSizingJob with hand-built descriptors — the only
// route to this flag, since no REAL descriptor set can make the four offset tables non-monotonic (every
// premise the RED-verification below defeats is structural, not merely typical).

using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Fill;
namespace MapRenderer.Tests.Jobs
{
    [TestFixture]
    public class FillSizingJobTests
    {
        [Test]
        public void MaxPolygonsSmallerThanHandedPolyCount_SetsErrorFlag_AndWritesNothing()
        {
            var polyOuterIdx  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyHoleStart = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyHoleCount = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var holeRingIdxs  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyCountArr  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            polyCountArr[0] = 2; // handed 2 polygons...
            var holeCountArr = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var ringOffsets  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory); // never read on the error path

            FillTriangulationBuffers buffers = FillTriangulationBuffers.Allocate();
            var counts = new NativeArray<FillGraphCounts>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var error = new NativeReference<int>(Allocator.Persistent);

            try
            {
                new FillSizingJob
                {
                    PolyOuterRingIdx = polyOuterIdx, PolyHoleListStart = polyHoleStart, PolyHoleCount = polyHoleCount,
                    HoleRingIdxs = holeRingIdxs, PolyCountArr = polyCountArr, HoleCountArr = holeCountArr,
                    RingOffsets = ringOffsets,
                    MaxPolygons = 1, // deliberately smaller than the handed polygon count (2)
                    MaxHoles = 10,
                    Buffers = buffers,
                    Counts = counts, Error = error,
                }.Run();

                Assert.AreEqual(FillGraphCounts.ErrorPolygonCapacity, error.Value,
                    "a polygon count exceeding MaxPolygons must set ErrorPolygonCapacity");

                Assert.AreEqual(0, buffers.VertexOffsets.Length, "VertexOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.HoleCountOffsets.Length, "HoleCountOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.WorkOffsets.Length, "WorkOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.IndexOffsets.Length, "IndexOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.FlatPolyVerts.Length);
                Assert.AreEqual(0, buffers.FlatSortedHoleCounts.Length);
                Assert.AreEqual(0, buffers.FlatIndexArrays.Length);
                Assert.AreEqual(0, buffers.FlatWorkVerts.Length);
                Assert.AreEqual(0, buffers.FlatPreviousIndex.Length);
                Assert.AreEqual(0, buffers.FlatNextIndex.Length);
                Assert.AreEqual(0, buffers.FlatIsBridge.Length);
                Assert.AreEqual(0, buffers.FlatRemoved.Length);
                Assert.AreEqual(0, buffers.FlatIsEar.Length);
                Assert.AreEqual(0, buffers.PerPolyIndexCount.Length);
                Assert.AreEqual(0, buffers.PerPolyForceClip.Length);
                Assert.AreEqual(0, buffers.PerPolyMergedVertexCount.Length);
                Assert.AreEqual(0, buffers.PerPolyFeatureIndex.Length);
                Assert.AreEqual(0, buffers.PerPolyOuterCount.Length);
            }
            finally
            {
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose(); ringOffsets.Dispose();
                buffers.DisposeAfter(default(JobHandle)).Complete();
                counts.Dispose(); error.Dispose();
            }
        }

        /// <summary>The C.2 observing tooth's happy-path arm: two real (non-degenerate, no-hole) polygons
        /// within capacity produce four strictly-increasing offset tables and <see cref="FillGraphCounts.Ok"/>.
        /// RED-VERIFIED by hand (not left in this file): temporarily change <c>FillSizingJob.cs</c>'s
        /// <c>workOffsets.Add(workOffsets[pi] + workCap)</c> to <c>workOffsets.Add(pi &gt; 0 ? workOffsets[pi]
        /// : workOffsets[pi] + workCap)</c> — polygon 1's entry then equals polygon 0's (a zero-length slice)
        /// — and this test flips from <see cref="FillGraphCounts.Ok"/> to
        /// <see cref="FillGraphCounts.ErrorOffsetTableNotDisjoint"/>. Revert immediately after observing it.</summary>
        [Test]
        public void TwoValidPolygons_ProduceStrictlyIncreasingOffsetTables_NoErrorFlag()
        {
            // Two single-ring, no-hole polygons: ring 0 = [0,3), ring 1 = [3,6).
            var polyOuterIdx  = new NativeArray<int>(new[] { 0, 1 }, Allocator.Persistent);
            var polyHoleStart = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            var polyHoleCount = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            var holeRingIdxs  = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyCountArr  = new NativeArray<int>(new[] { 2 }, Allocator.Persistent);
            var holeCountArr  = new NativeArray<int>(new[] { 0 }, Allocator.Persistent);
            var ringOffsets   = new NativeArray<int>(new[] { 0, 3, 6 }, Allocator.Persistent);

            FillTriangulationBuffers buffers = FillTriangulationBuffers.Allocate();
            var counts = new NativeArray<FillGraphCounts>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var error = new NativeReference<int>(Allocator.Persistent);

            try
            {
                new FillSizingJob
                {
                    PolyOuterRingIdx = polyOuterIdx, PolyHoleListStart = polyHoleStart, PolyHoleCount = polyHoleCount,
                    HoleRingIdxs = holeRingIdxs, PolyCountArr = polyCountArr, HoleCountArr = holeCountArr,
                    RingOffsets = ringOffsets,
                    MaxPolygons = 2, MaxHoles = 1,
                    Buffers = buffers,
                    Counts = counts, Error = error,
                }.Run();

                Assert.AreEqual(FillGraphCounts.Ok, error.Value,
                    "two real, in-capacity polygons must produce strictly increasing offset tables");

                int[] vertexOffsets = buffers.VertexOffsets.AsArray().ToArray();
                int[] workOffsets   = buffers.WorkOffsets.AsArray().ToArray();
                for (int pi = 0; pi < 2; pi++)
                {
                    Assert.Greater(vertexOffsets[pi + 1], vertexOffsets[pi], $"VertexOffsets[{pi}] must be strictly increasing");
                    Assert.Greater(workOffsets[pi + 1], workOffsets[pi], $"WorkOffsets[{pi}] must be strictly increasing");
                }
            }
            finally
            {
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose(); ringOffsets.Dispose();
                buffers.DisposeAfter(default(JobHandle)).Complete();
                counts.Dispose(); error.Dispose();
            }
        }

        /// <summary>The finding that changed this stage's shape (job-scheduling-design.md §7 rule 2 and
        /// <see cref="FillSizingJob"/>'s own doc, both above <see cref="FillSizingJob.Execute"/>):
        /// <see cref="FillGatherJob{TComparer}"/> is the third node found bounding its own loop by a column
        /// this job's early return never touches, and the only one of the three that WRITES. A sizing-only
        /// arm (<see cref="TwoValidPolygons_ProduceStrictlyIncreasingOffsetTables_NoErrorFlag"/>) asserts what
        /// sizing writes, not what a downstream node reads — fill carried this exact defect through that arm,
        /// pre-existing and unfixed. So this drives all three graph nodes IN GRAPH ORDER over the SAME
        /// buffers — this job (undersized capacity) → the real <see cref="FillGatherJob{TComparer}"/> → the
        /// real <see cref="FillAggregateJob"/> — the whole mechanism the original bug lived in.
        ///
        /// <para>Real descriptors, not dummy ones: <see cref="FillGatherJob{TComparer}"/> dereferences
        /// <c>PolyOuterRingIdx</c>/<c>RingOffsets</c>/<c>RingFeatureIdx</c> BEFORE it ever touches a
        /// sizing-owned column, so a dummy descriptor set would throw for the wrong reason and this tooth
        /// would pass on a bystander fault.</para>
        ///
        /// <para><b>Be honest about what this detects.</b> Pre-fix, <see cref="FillGatherJob{TComparer}"/>
        /// writes out of bounds through an <c>.AsArray()</c> view and never resizes anything, and pre-fix
        /// <see cref="FillAggregateJob"/> only reads — so every state assertion below passes on the buggy code
        /// if the bounds check does not fire. This is a throw-detector wearing state assertions: the state
        /// assertions pin the contract that must survive the fix, the bounds check is what makes it RED. Does
        /// NOT assert <c>Assert.Throws</c> — that would pin the defect rather than the contract, and would
        /// invert the day the fix lands.</para>
        /// </summary>
        [Test]
        public void SizingCapacityOverrun_LeavesGatherAndAggregate_WithNothingToDo()
        {
#if !ENABLE_UNITY_COLLECTIONS_CHECKS
            Assert.Fail("ENABLE_UNITY_COLLECTIONS_CHECKS is not defined in this test assembly build — " +
                "without it, neither FillGatherJob's nor FillAggregateJob's managed-IL bounds check can fire, " +
                "and this tooth cannot detect the release-build OOB it exists to catch.");
#endif
            // Two real single-ring, no-hole polygons — same shape as TwoValidPolygons_… above — but a
            // MaxPolygons capacity too small to hold them, so this job's own early return fires and every
            // Buffers column is left at length 0 while the descriptor arrays below still report polyCount==2.
            var polyOuterIdx   = new NativeArray<int>(new[] { 0, 1 }, Allocator.Persistent);
            var polyHoleStart  = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            var polyHoleCount  = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);
            var holeRingIdxs   = new NativeArray<int>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var polyCountArr   = new NativeArray<int>(new[] { 2 }, Allocator.Persistent);
            var holeCountArr   = new NativeArray<int>(new[] { 0 }, Allocator.Persistent);
            var ringOffsets    = new NativeArray<int>(new[] { 0, 3, 6 }, Allocator.Persistent);
            var vertices       = new NativeArray<double2>(6, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var ringFeatureIdx = new NativeArray<int>(new[] { 0, 0 }, Allocator.Persistent);

            FillTriangulationBuffers buffers = FillTriangulationBuffers.Allocate();
            var counts = new NativeArray<FillGraphCounts>(1, Allocator.Persistent, NativeArrayOptions.ClearMemory);
            var error = new NativeReference<int>(Allocator.Persistent);

            var tileVertices     = new NativeList<double2>(Allocator.Persistent);
            var worldPositions   = new NativeList<double3>(Allocator.Persistent);
            var vertexUp         = new NativeList<double3>(Allocator.Persistent);
            var vertexEast       = new NativeList<double3>(Allocator.Persistent);
            var vertexFeatureIdx = new NativeList<int>(Allocator.Persistent);
            var triangleIndices  = new NativeList<int>(Allocator.Persistent);
            var geo              = new NativeList<GeoCoordinate>(Allocator.Persistent);

            try
            {
                new FillSizingJob
                {
                    PolyOuterRingIdx = polyOuterIdx, PolyHoleListStart = polyHoleStart, PolyHoleCount = polyHoleCount,
                    HoleRingIdxs = holeRingIdxs, PolyCountArr = polyCountArr, HoleCountArr = holeCountArr,
                    RingOffsets = ringOffsets,
                    MaxPolygons = 1, // deliberately smaller than the handed polygon count (2)
                    MaxHoles = 10,
                    Buffers = buffers,
                    Counts = counts, Error = error,
                }.Run();

                Assert.AreEqual(FillGraphCounts.ErrorPolygonCapacity, error.Value,
                    "precondition: sizing's own capacity check must fire so the downstream nodes below see a " +
                    "genuinely post-early-return buffers state");

                var comparer = new FillMeshPipeline.HoleRingComparer(vertices, ringOffsets);
                new FillGatherJob<FillMeshPipeline.HoleRingComparer>
                {
                    Vertices = vertices, RingOffsets = ringOffsets, RingFeatureIdx = ringFeatureIdx,
                    PolyOuterRingIdx = polyOuterIdx, PolyHoleListStart = polyHoleStart, PolyHoleCount = polyHoleCount,
                    HoleRingIdxs = holeRingIdxs,
                    Comparer = comparer,
                    Buffers = buffers,
                }.Run();

                new FillAggregateJob
                {
                    Buffers = buffers,
                    TileVertices = tileVertices, WorldPositions = worldPositions, VertexUp = vertexUp, VertexEast = vertexEast,
                    VertexFeatureIdx = vertexFeatureIdx, TriangleIndices = triangleIndices, Geo = geo,
                    Counts = counts, Error = error,
                }.Run();

                Assert.AreEqual(FillGraphCounts.ErrorPolygonCapacity, error.Value,
                    "sizing's own verdict must survive both downstream nodes untouched");

                Assert.AreEqual(0, buffers.VertexOffsets.Length, "VertexOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.HoleCountOffsets.Length, "HoleCountOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.WorkOffsets.Length, "WorkOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.IndexOffsets.Length, "IndexOffsets must be untouched past the capacity check");
                Assert.AreEqual(0, buffers.FlatPolyVerts.Length);
                Assert.AreEqual(0, buffers.FlatSortedHoleCounts.Length);
                Assert.AreEqual(0, buffers.FlatIndexArrays.Length);
                Assert.AreEqual(0, buffers.FlatWorkVerts.Length);
                Assert.AreEqual(0, buffers.FlatPreviousIndex.Length);
                Assert.AreEqual(0, buffers.FlatNextIndex.Length);
                Assert.AreEqual(0, buffers.FlatIsBridge.Length);
                Assert.AreEqual(0, buffers.FlatRemoved.Length);
                Assert.AreEqual(0, buffers.FlatIsEar.Length);
                Assert.AreEqual(0, buffers.PerPolyIndexCount.Length);
                Assert.AreEqual(0, buffers.PerPolyForceClip.Length);
                Assert.AreEqual(0, buffers.PerPolyMergedVertexCount.Length);
                Assert.AreEqual(0, buffers.PerPolyFeatureIndex.Length);
                Assert.AreEqual(0, buffers.PerPolyOuterCount.Length);

                Assert.AreEqual(0, tileVertices.Length, "FillAggregateJob must produce no vertices");
                Assert.AreEqual(0, worldPositions.Length);
                Assert.AreEqual(0, vertexUp.Length);
                Assert.AreEqual(0, vertexEast.Length);
                Assert.AreEqual(0, vertexFeatureIdx.Length);
                Assert.AreEqual(0, triangleIndices.Length, "FillAggregateJob must produce no indices");
                Assert.AreEqual(0, geo.Length);

                // Non-vacuity witness: the borrowed count sizing read is still 2 — without this the test would
                // pass identically on an input where there was nothing to iterate in the first place.
                Assert.AreEqual(2, polyCountArr[0]);
            }
            finally
            {
                polyOuterIdx.Dispose(); polyHoleStart.Dispose(); polyHoleCount.Dispose();
                holeRingIdxs.Dispose(); polyCountArr.Dispose(); holeCountArr.Dispose(); ringOffsets.Dispose();
                vertices.Dispose(); ringFeatureIdx.Dispose();
                buffers.DisposeAfter(default(JobHandle)).Complete();
                counts.Dispose(); error.Dispose();
                tileVertices.Dispose(); worldPositions.Dispose(); vertexUp.Dispose(); vertexEast.Dispose();
                vertexFeatureIdx.Dispose(); triangleIndices.Dispose(); geo.Dispose();
            }
        }

        /// <summary>job-scheduling-design.md's Burst-safety register — the
        /// <c>FillMeshGraphStructureTests.JobDebugger_IsEnabled_…</c>/<c>ProjectionManagedVersusBurstTests
        /// .Burst_IsEnabled_…</c> precedent, for the mechanism <see cref="SizingCapacityOverrun_LeavesGatherAndAggregate_WithNothingToDo"/>
        /// relies on: that tooth's RED is a bounds exception thrown from <see cref="FillGatherJob{TComparer}"/>'s
        /// <c>[BurstCompile]</c>d body, which carries its OWN safety-check setting independent of the test
        /// assembly's <c>ENABLE_UNITY_COLLECTIONS_CHECKS</c> define. RED-verify by hand, toggling
        /// Jobs ▸ Burst ▸ Safety Checks off — confirmed empirically that the other tooth goes green (i.e.
        /// vacuous) with this off.</summary>
        [Test]
        public void BurstSafetyChecks_AreEnabled_OrT1IsVacuous()
        {
            Assert.IsTrue(BurstCompiler.Options.EnableBurstSafetyChecks,
                "Jobs ▸ Burst ▸ Safety Checks is OFF — FillGatherJob's compiled body no longer raises the " +
                "bounds exception SizingCapacityOverrun_LeavesGatherAndAggregate_WithNothingToDo relies on to " +
                "detect the release-build OOB, so that tooth passes vacuously while this reads false.");
        }
    }
}
