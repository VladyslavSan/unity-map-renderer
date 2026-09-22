// Unity EditMode only — NativeArray, Burst jobs, UnityEngine.Application. NOT registered in core-tests.csproj.
//
// The compile checkpoint (job-scheduling-design.md): three Burst behaviours the fill graph's
// shape depends on, none previously exercised in this repo:
//   (i)   GetSubArray + a nested `new EarcutJob{...}.Execute()` call, inside another job's Execute — EarcutBatchJob.
//   (ii)  NativeSortExtension.Sort<int, TComparer> over a generic comparer holding two NativeArray fields,
//         inside a job — FillGatherJob<TComparer>.
//   (iii) One struct implementing BOTH IJobParallelFor and IJobParallelForDefer (two JobProducerTypes over one
//         Execute(int index)) — TileToGeoJob / ProjectPointsJob<TProj>.
// A green numeric assertion here is NOT the verdict: CompileSynchronously=true falls back to managed IL on a
// Burst compile failure, so these tests can pass while Burst never compiled the job. The verdict is the log
// grep job-scheduling-design.md's Safety section mandates (run-tests.sh's own log, greped for Burst errors) — this
// file only supplies the numeric correctness half.

using System.IO;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Projection;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Tests.TestSupport;
using static MapRenderer.Tests.Meshing.EarcutJobGatherHarness;

namespace MapRenderer.Tests.Jobs
{
    [TestFixture]
    public class FillGraphBurstProbeTests
    {
        private const string WaterFixture = "water-real-stockholm-archipelago-9-282-150.pbf.bytes";

        private static byte[] LoadFixture(string name)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", name);
            FileAssert.Exists(path);
            return File.ReadAllBytes(path);
        }

        // ── Shared setup moved to EarcutJobGatherHarness.BuildGatherState — the real per-polygon ─────────
        // ── gather chain, now also driving the re-homed corpus and full-pipeline teeth. ───────────────────

        // ── (1) EarcutBatchJob — unknown (i): GetSubArray + nested EarcutJob.Execute() inside a job ────────

        /// <summary>A dispatch parity oracle, not a kernel oracle: both arms below call <c>EarcutJob</c>
        /// itself, so a defect inside <c>EarcutJob</c> reproduces identically on both sides and this test
        /// cannot see it (RED-verified: a transposed vertex here stayed green). Kernel correctness is
        /// pinned by <c>WaterTriangulationTests</c>' 8-tile corpus sweep instead (property coverage against
        /// geometric ground truth, not arm agreement with a managed twin).</summary>
        [Test]
        public void EarcutBatchJob_MatchesPerPolygonEarcutJobRun()
        {
            byte[] mvtBytes = LoadFixture(WaterFixture);
            var tileId = new TileId { Z = 9, X = 282, Y = 150 };
            using var mvtTile = MvtDecoder.Decode(tileId, mvtBytes);
            var layer = mvtTile.GetLayer("water");
            Assert.IsNotNull(layer, "water layer present");

            TileGeometryBuffers geometry = layer.Geometry; // BORROWED
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);

            BuildGatherState(geometry, visitOrder, out TileGeometryBuffers derived, out GatherState s);
            try
            {
                int polyCount = s.PolyCount;
                bool anyMultiHole = false;
                for (int pi = 0; pi < polyCount; pi++)
                    if (s.PolyHoleCount[pi] > 1) anyMultiHole = true;
                Assert.IsTrue(anyMultiHole, "precondition: at least one water polygon has >=2 holes " +
                    "(exercises the hole sort inside the gather step this test's inputs came from)");

                // ── Reference: per-polygon EarcutJob.Run(), one fresh buffers/output set. ─────────────
                var refIdx      = new NativeArray<int>(s.Buffers.IndexOffsets[polyCount],     Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var refIndexCounts   = new NativeArray<int>(polyCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                var refForce    = new NativeArray<int>(polyCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                var refMergedVC = new NativeArray<int>(polyCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var refVisits = new NativeArray<long>(polyCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                var refV = new NativeArray<double2>(s.Buffers.WorkOffsets[polyCount], Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var refPrev = new NativeArray<int>(s.Buffers.WorkOffsets[polyCount], Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var refNext = new NativeArray<int>(s.Buffers.WorkOffsets[polyCount], Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var refBridge = new NativeArray<bool>(s.Buffers.WorkOffsets[polyCount], Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var refRemoved = new NativeArray<bool>(s.Buffers.WorkOffsets[polyCount], Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var refIsEar = new NativeArray<bool>(s.Buffers.WorkOffsets[polyCount], Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

                NativeArray<double2> flatVertsArr = s.Buffers.FlatPolyVerts.AsArray();
                NativeArray<int>     flatHoleArr  = s.Buffers.FlatSortedHoleCounts.AsArray();

                for (int pi = 0; pi < polyCount; pi++)
                {
                    int sOff = s.Buffers.WorkOffsets[pi], sLen = s.Buffers.WorkOffsets[pi + 1] - sOff;
                    new EarcutJob
                    {
                        PolyVertices     = flatVertsArr.GetSubArray(s.Buffers.VertexOffsets[pi], s.Buffers.VertexOffsets[pi + 1] - s.Buffers.VertexOffsets[pi]),
                        OuterCount       = s.Buffers.PerPolyOuterCount[pi],
                        SortedHoleCounts = flatHoleArr.GetSubArray(s.Buffers.HoleCountOffsets[pi], s.Buffers.HoleCountOffsets[pi + 1] - s.Buffers.HoleCountOffsets[pi]),
                        HoleCount        = s.PolyHoleCount[pi],
                        OutIndices       = refIdx.GetSubArray(s.Buffers.IndexOffsets[pi], s.Buffers.IndexOffsets[pi + 1] - s.Buffers.IndexOffsets[pi]),
                        OutIndexOffset   = 0,
                        OutIndexCount        = refIndexCounts.GetSubArray(pi, 1),
                        OutForceClipCount    = refForce.GetSubArray(pi, 1),
                        OutMergedVertexCount = refMergedVC.GetSubArray(pi, 1),
                        OutCandidateVisits   = refVisits.GetSubArray(pi, 1),
                        Verts = refV.GetSubArray(sOff, sLen),
                        Prev = refPrev.GetSubArray(sOff, sLen), Next = refNext.GetSubArray(sOff, sLen),
                        IsBridgeCopy = refBridge.GetSubArray(sOff, sLen), Removed = refRemoved.GetSubArray(sOff, sLen),
                        IsEar = refIsEar.GetSubArray(sOff, sLen),
                    }.Run();
                }

                // ── Under test: EarcutBatchJob, one loop, one Execute() call, fresh buffers/output set. ──
                var batchIdx      = new NativeList<int>(Allocator.Persistent);
                batchIdx.Resize(s.Buffers.IndexOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
                var batchIndexCounts   = new NativeList<int>(Allocator.Persistent); batchIndexCounts.Resize(polyCount, NativeArrayOptions.ClearMemory);
                var batchForce    = new NativeList<int>(Allocator.Persistent); batchForce.Resize(polyCount, NativeArrayOptions.ClearMemory);
                var batchMergedVC = new NativeList<int>(Allocator.Persistent); batchMergedVC.Resize(polyCount, NativeArrayOptions.UninitializedMemory);
                var batchV = new NativeList<double2>(Allocator.Persistent); batchV.Resize(s.Buffers.WorkOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
                var batchPrev = new NativeList<int>(Allocator.Persistent); batchPrev.Resize(s.Buffers.WorkOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
                var batchNext = new NativeList<int>(Allocator.Persistent); batchNext.Resize(s.Buffers.WorkOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
                var batchBridge = new NativeList<bool>(Allocator.Persistent); batchBridge.Resize(s.Buffers.WorkOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
                var batchRemoved = new NativeList<bool>(Allocator.Persistent); batchRemoved.Resize(s.Buffers.WorkOffsets[polyCount], NativeArrayOptions.UninitializedMemory);
                var batchIsEar = new NativeList<bool>(Allocator.Persistent); batchIsEar.Resize(s.Buffers.WorkOffsets[polyCount], NativeArrayOptions.UninitializedMemory);

                // The read side (VertexOffsets/HoleCountOffsets/WorkOffsets/IndexOffsets/FlatPolyVerts/
                // FlatSortedHoleCounts/PerPolyOuterCount) comes from s.Buffers (gather already populated it);
                // the write side is this test's own fresh "batch*" set, so the batch arm never shares
                // buffers with the per-polygon reference arm above.
                // Every TriangulationBuffers field must be a VALID container when the job schedules — Unity's
                // safety system rejects an uncreated NativeList<T> field even when Execute() never reads it
                // (confirmed empirically here: PerPolyFeatureIndex, unused by EarcutBatchJob, still had to be
                // set or scheduling threw). So PerPolyFeatureIndex is carried over too, even though this job
                // never touches it.
                var batchBuffers = new TriangulationBuffers
                {
                    VertexOffsets = s.Buffers.VertexOffsets, HoleCountOffsets = s.Buffers.HoleCountOffsets,
                    WorkOffsets = s.Buffers.WorkOffsets, IndexOffsets = s.Buffers.IndexOffsets,
                    FlatPolyVerts = s.Buffers.FlatPolyVerts, FlatSortedHoleCounts = s.Buffers.FlatSortedHoleCounts,
                    PerPolyOuterCount = s.Buffers.PerPolyOuterCount, PerPolyFeatureIndex = s.Buffers.PerPolyFeatureIndex,
                    FlatIndexArrays = batchIdx,
                    FlatWorkVerts = batchV,
                    FlatPreviousIndex = batchPrev, FlatNextIndex = batchNext,
                    FlatIsBridge = batchBridge, FlatRemoved = batchRemoved, FlatIsEar = batchIsEar,
                    PerPolyIndexCount = batchIndexCounts, PerPolyForceClip = batchForce, PerPolyMergedVertexCount = batchMergedVC,
                };

                new EarcutBatchJob
                {
                    PolyHoleCount = s.PolyHoleCount,
                    Buffers = batchBuffers,
                }.Schedule(s.Buffers.PerPolyOuterCount, 1, default).Complete();

                for (int pi = 0; pi < polyCount; pi++)
                {
                    // The capacity backstop this job used to write lives in AggregateJob — checked here directly
                    // instead, against the same bound
                    // (WorkOffsets[pi+1] - WorkOffsets[pi]) AggregateJob now compares against.
                    int sLen = s.Buffers.WorkOffsets[pi + 1] - s.Buffers.WorkOffsets[pi];
                    Assert.LessOrEqual(batchMergedVC[pi], sLen, $"polygon {pi}: the earcut batch must not overrun buffers on real corpus input");

                    Assert.AreEqual(refIndexCounts[pi], batchIndexCounts[pi], $"polygon {pi}: OutIndexCount");
                    Assert.AreEqual(refMergedVC[pi], batchMergedVC[pi], $"polygon {pi}: OutMergedVertexCount");
                    Assert.AreEqual(refForce[pi], batchForce[pi], $"polygon {pi}: OutForceClipCount");

                    // Content, not just the summary scalars — both arms allocate a full index array per
                    // polygon; comparing only counts would leave a transposed/mis-offset index undetected.
                    int idxOff = s.Buffers.IndexOffsets[pi], idxLen = refIndexCounts[pi];
                    for (int i = 0; i < idxLen; i++)
                        Assert.AreEqual(refIdx[idxOff + i], batchIdx[idxOff + i], $"polygon {pi}: OutIndices[{i}]");
                }

                refIdx.Dispose(); refIndexCounts.Dispose(); refForce.Dispose(); refMergedVC.Dispose();
                refVisits.Dispose();
                refV.Dispose(); refPrev.Dispose(); refNext.Dispose();
                refBridge.Dispose(); refRemoved.Dispose(); refIsEar.Dispose();
                batchIdx.Dispose(); batchIndexCounts.Dispose(); batchForce.Dispose(); batchMergedVC.Dispose();
                batchV.Dispose(); batchPrev.Dispose(); batchNext.Dispose();
                batchBridge.Dispose(); batchRemoved.Dispose(); batchIsEar.Dispose();
            }
            finally
            {
                s.Dispose();
                derived.Dispose();
                visitOrder.Dispose();
            }
        }

        // ── (2) FillGatherJob — unknown (ii): NativeSortExtension.Sort<int, TComparer> inside a job ────────

        [Test]
        public void FillGatherJob_MatchesManagedPopulatePass()
        {
            byte[] mvtBytes = LoadFixture(WaterFixture);
            var tileId = new TileId { Z = 9, X = 282, Y = 150 };
            using var mvtTile = MvtDecoder.Decode(tileId, mvtBytes);
            var layer = mvtTile.GetLayer("water");
            Assert.IsNotNull(layer, "water layer present");

            TileGeometryBuffers geometry = layer.Geometry; // BORROWED
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);

            BuildGatherState(geometry, visitOrder, out TileGeometryBuffers derived, out GatherState s);
            try
            {
                int polyCount = s.PolyCount;

                // ── Managed re-derivation of FillMeshPipeline.cs:384–421's populate pass, plain C# — no job. ──
                var refFeatureIdx = new int[polyCount];
                var refOuterCount = new int[polyCount];
                var refPolyVerts  = new double2[s.Buffers.VertexOffsets[polyCount]];
                var refHoleCounts    = new int[s.Buffers.HoleCountOffsets[polyCount]];
                var holeRIs       = new int[math.max(1, s.HoleRingIdxs.Length)];

                for (int pi = 0; pi < polyCount; pi++)
                {
                    int outerRi = s.PolyOuterIdx[pi];
                    refFeatureIdx[pi] = derived.RingFeatureIdx[outerRi];
                    int outerStart = derived.RingOffsets[outerRi];
                    int outerLen   = derived.RingOffsets[outerRi + 1] - outerStart;
                    refOuterCount[pi] = outerLen;
                    int holeCount = s.PolyHoleCount[pi];
                    int hStart    = s.PolyHoleStart[pi];

                    for (int hi = 0; hi < holeCount; hi++)
                        holeRIs[hi] = s.HoleRingIdxs[hStart + hi];

                    if (holeCount > 1)
                    {
                        var comparer = new FillMeshPipeline.HoleRingComparer(derived.Vertices, derived.RingOffsets);
                        System.Array.Sort(holeRIs, 0, holeCount, comparer);
                    }

                    int vertBase = s.Buffers.VertexOffsets[pi];
                    for (int i = 0; i < outerLen; i++)
                        refPolyVerts[vertBase + i] = derived.Vertices[outerStart + i];

                    int vPos = outerLen;
                    int holeCountBase = s.Buffers.HoleCountOffsets[pi];
                    for (int hi = 0; hi < holeCount; hi++)
                    {
                        int hri    = holeRIs[hi];
                        int hBegin = derived.RingOffsets[hri];
                        int hLen   = derived.RingOffsets[hri + 1] - hBegin;
                        refHoleCounts[holeCountBase + hi] = hLen;
                        for (int i = 0; i < hLen; i++)
                            refPolyVerts[vertBase + (vPos++)] = derived.Vertices[hBegin + i];
                    }
                }

                for (int pi = 0; pi < polyCount; pi++)
                {
                    Assert.AreEqual(refFeatureIdx[pi], s.Buffers.PerPolyFeatureIndex[pi], $"polygon {pi}: PerPolyFeatureIndex");
                    Assert.AreEqual(refOuterCount[pi], s.Buffers.PerPolyOuterCount[pi], $"polygon {pi}: PerPolyOuterCount");
                }
                for (int i = 0; i < refPolyVerts.Length; i++)
                    Assert.AreEqual(refPolyVerts[i], s.Buffers.FlatPolyVerts[i], $"FlatPolyVerts[{i}]");
                for (int i = 0; i < refHoleCounts.Length; i++)
                    Assert.AreEqual(refHoleCounts[i], s.Buffers.FlatSortedHoleCounts[i], $"FlatSortedHoleCounts[{i}]");
            }
            finally
            {
                s.Dispose();
                derived.Dispose();
                visitOrder.Dispose();
            }
        }

        // ── (3) TileToGeoJob + ProjectPointsJob<TProj> — unknown (iii): dual IJobParallelFor / ─────────────
        // ── IJobParallelForDefer on one struct, scheduled through the deferred-count overload. ─────────────

        /// <summary>Trivial preceding job: resizes the deferred lists from a fixed-length source — the
        /// "count not known at Schedule time" shape the deferred overload targets.</summary>
        private struct SeedListsJob : IJob
        {
            [ReadOnly] public NativeArray<double2> Source;
            public NativeList<double2>       TileCoords;
            public NativeList<GeoCoordinate> Geo;
            public NativeList<double3>       World;
            public NativeList<double3>       Normals;

            public void Execute()
            {
                int n = Source.Length;
                TileCoords.ResizeUninitialized(n);
                for (int i = 0; i < n; i++) TileCoords[i] = Source[i];
                Geo.ResizeUninitialized(n);
                World.ResizeUninitialized(n);
                Normals.ResizeUninitialized(n);
            }
        }

        [Test]
        public void DeferredTileToGeoAndProject_MatchRunN()
        {
            var tile = new TileId { Z = 9, X = 282, Y = 150 };
            const double extent = 4096.0;
            const int n = 5;

            var source = new NativeArray<double2>(n, Allocator.Persistent);
            for (int i = 0; i < n; i++) source[i] = new double2(100.0 * i, 200.0 * (n - i));

            // ── Reference: today's .Run(n) dispatch. ──────────────────────────────────────────────
            var refGeo   = new NativeArray<GeoCoordinate>(n, Allocator.Persistent);
            new TileToGeoJob { Tile = tile, Extent = extent, TileCoords = source, OutGeo = refGeo }.Run(n);
            var refWorld = new NativeArray<double3>(n, Allocator.Persistent);
            var refUp    = new NativeArray<double3>(n, Allocator.Persistent);
            new ProjectPointsJob<WebMercatorProjection>
            {
                Projection = default, OriginWorld = double3.zero,
                Points = refGeo, WorldPositions = refWorld, Normals = refUp,
            }.Run(n);

            // ── Under test: the IJobParallelForDefer Schedule(list, batch, deps) overload. ────────────
            var tileCoordsList = new NativeList<double2>(Allocator.Persistent);
            var geoList        = new NativeList<GeoCoordinate>(Allocator.Persistent);
            var worldList      = new NativeList<double3>(Allocator.Persistent);
            var normalsList    = new NativeList<double3>(Allocator.Persistent);

            JobHandle seedHandle = new SeedListsJob
            {
                Source = source, TileCoords = tileCoordsList, Geo = geoList, World = worldList, Normals = normalsList,
            }.Schedule();

            JobHandle geoHandle = new TileToGeoJob
            {
                Tile = tile, Extent = extent,
                TileCoords = tileCoordsList.AsDeferredJobArray(), OutGeo = geoList.AsDeferredJobArray(),
            }.Schedule(tileCoordsList, 64, seedHandle);

            JobHandle projHandle = new ProjectPointsJob<WebMercatorProjection>
            {
                Projection = default, OriginWorld = double3.zero,
                Points = geoList.AsDeferredJobArray(),
                WorldPositions = worldList.AsDeferredJobArray(), Normals = normalsList.AsDeferredJobArray(),
            }.Schedule(geoList, 64, geoHandle);

            JobHandle.ScheduleBatchedJobs();
            projHandle.Complete();

            Assert.AreEqual(n, geoList.Length, "the deferred count must resolve to the seeded length");
            for (int i = 0; i < n; i++)
            {
                Assert.AreEqual(refGeo[i].Latitude, geoList[i].Latitude, 1e-9, $"geo[{i}].Latitude");
                Assert.AreEqual(refGeo[i].Longitude, geoList[i].Longitude, 1e-9, $"geo[{i}].Longitude");
                Assert.AreEqual((double)refWorld[i].x, (double)worldList[i].x, 1e-9, $"world[{i}].x");
                Assert.AreEqual((double)refWorld[i].y, (double)worldList[i].y, 1e-9, $"world[{i}].y");
                Assert.AreEqual((double)refWorld[i].z, (double)worldList[i].z, 1e-9, $"world[{i}].z");
                Assert.AreEqual((double)refUp[i].y, (double)normalsList[i].y, 1e-9, $"normal[{i}].y");
            }

            source.Dispose(); refGeo.Dispose(); refWorld.Dispose(); refUp.Dispose();
            tileCoordsList.Dispose(); geoList.Dispose(); worldList.Dispose(); normalsList.Dispose();
        }
    }
}
