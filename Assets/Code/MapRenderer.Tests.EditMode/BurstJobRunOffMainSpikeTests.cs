// S89 D2 contract guard: the low-risk vertical slice runs the Burst geometry kernels SYNCHRONOUSLY on the
// existing UniTask.RunOnThreadPool worker via IJob.Run() / IJobParallelFor.Run() — NOT scheduled on Unity's
// job workers. That keeps the kick/consume/holding-pen lifecycle unchanged (jobs run where managed
// mesh build runs today) while moving the mesh-build geometry off managed List<>/array allocation onto
// NativeArrays (the actual GC win). This is load-bearing: if a Unity upgrade makes Burst .Run() throw off a
// raw threadpool thread (thread affinity), the whole approach breaks and this test names it precisely.
//
// (Established as a spike: the geometry kernels take CALLER-provided Persistent scratch — no internal
// Allocator.Temp — so allocating NativeArrays and .Run()-ing them off the main thread is safe. The GC win
// does not depend on Burst engaging; even as managed IL the NativeArray outputs still allocate no GC.)

using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Geometry;
using MapRenderer.Jobs;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class BurstJobRunOffMainSpikeTests
    {
        private static T RunOnWorker<T>(Func<T> body, out int workerThreadId, out Exception workerEx)
        {
            Exception ex = null;
            int tid = -1;
            T result = default;
            var task = UniTask.RunOnThreadPool(() =>
            {
                tid = Thread.CurrentThread.ManagedThreadId;
                try { result = body(); }
                catch (Exception e) { ex = e; }
            }, configureAwait: false).Preserve();

            int spins = 0;
            while (!task.Status.IsCompleted() && spins++ < 20000) Thread.Sleep(1);

            workerThreadId = tid;
            workerEx = ex;
            return result;
        }

        [Test]
        public void ProjectJob_RunOnThreadPoolWorker_ProducesSameOutputAsMainThread()
        {
            int mainTid = Thread.CurrentThread.ManagedThreadId;

            // Reference: run on the main thread.
            var refOut = new NativeArray<double3>(3, Allocator.Persistent);
            var refUp  = new NativeArray<double3>(3, Allocator.Persistent);
            try
            {
                var pts = new NativeArray<GeoCoordinate>(3, Allocator.Persistent);
                pts[0] = new GeoCoordinate { Latitude = 0,   Longitude = 0 };
                pts[1] = new GeoCoordinate { Latitude = 45,  Longitude = 30 };
                pts[2] = new GeoCoordinate { Latitude = -60, Longitude = 179 };
                new ProjectPointsJob<WebMercatorProjection>
                {
                    Projection = new WebMercatorProjection(),
                    OriginWorld = new double3(0.0, 0.0, 0.0),
                    Points = pts, WorldPositions = refOut, Normals = refUp,
                }.Run(3);
                pts.Dispose();

                var workerOut = RunOnWorker(() =>
                {
                    var outArr = new NativeArray<double3>(3, Allocator.Persistent);
                    var outUp  = new NativeArray<double3>(3, Allocator.Persistent);
                    var wpts = new NativeArray<GeoCoordinate>(3, Allocator.Persistent);
                    wpts[0] = new GeoCoordinate { Latitude = 0,   Longitude = 0 };
                    wpts[1] = new GeoCoordinate { Latitude = 45,  Longitude = 30 };
                    wpts[2] = new GeoCoordinate { Latitude = -60, Longitude = 179 };
                    new ProjectPointsJob<WebMercatorProjection>
                    {
                        Projection = new WebMercatorProjection(),
                        OriginWorld = new double3(0.0, 0.0, 0.0),
                        Points = wpts, WorldPositions = outArr, Normals = outUp,
                    }.Run(3);
                    wpts.Dispose();
                    outUp.Dispose();
                    return outArr;
                }, out int workerTid, out Exception ex);

                Assert.IsNull(ex, $"ProjectPointsJob<WebMercatorProjection>.Run() THREW off a RunOnThreadPool worker: {ex}");
                Assert.AreNotEqual(mainTid, workerTid, "spike must actually run off the main thread");

                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        Assert.AreEqual(refOut[i].x, workerOut[i].x, 1e-9f, $"v[{i}].x off-main == main");
                        Assert.AreEqual(refOut[i].y, workerOut[i].y, 1e-9f, $"v[{i}].y off-main == main");
                        Assert.AreEqual(refOut[i].z, workerOut[i].z, 1e-9f, $"v[{i}].z off-main == main");
                    }
                }
                finally { if (workerOut.IsCreated) workerOut.Dispose(); }
            }
            finally { refOut.Dispose(); refUp.Dispose(); }
        }

        [Test]
        public void EarcutJob_RunOnThreadPoolWorker_TriangulatesSquare()
        {
            int mainTid = Thread.CurrentThread.ManagedThreadId;

            var (idxCount, workerTid, ex) = RunOnWorkerEarcut();

            Assert.IsNull(ex, $"EarcutJob.Run() THREW off a RunOnThreadPool worker: {ex}");
            Assert.AreNotEqual(mainTid, workerTid, "spike must actually run off the main thread");
            Assert.AreEqual(6, idxCount, "a CCW square triangulates to 2 triangles (6 indices) off-main");
        }

        [Test]
        public void LineRibbonJob_RunOnThreadPoolWorker_UsesInternalTempSafely()
        {
            // LineRibbonJob allocates Allocator.Temp INTERNALLY (dedup/along/cumDist scratch), unlike the fill
            // kernels which take caller scratch. Temp on a raw threadpool thread is the exact off-main risk —
            // this proves it works there before the live line path depends on it.
            int mainTid = Thread.CurrentThread.ManagedThreadId;

            int vc = -1, ic = -1;
            var result = RunOnWorker(() =>
            {
                var pts   = new NativeArray<double3>(3, Allocator.Persistent);
                var ups   = new NativeArray<double3>(3, Allocator.Persistent);
                var outV  = new NativeArray<LineRibbonVertex>(LineRibbonJob.MaxVertexCount(3, 4), Allocator.Persistent);
                var outI  = new NativeArray<int>(LineRibbonJob.MaxIndexCount(3, 4), Allocator.Persistent);
                var vcArr = new NativeArray<int>(1, Allocator.Persistent);
                var icArr = new NativeArray<int>(1, Allocator.Persistent);
                try
                {
                    pts[0] = new double3(0, 0, 0);
                    pts[1] = new double3(10, 0, 0);
                    pts[2] = new double3(20, 0, 5);
                    for (int i = 0; i < 3; i++) ups[i] = new double3(0, 1, 0);
                    new LineRibbonJob
                    {
                        Points = pts, Ups = ups, PointCount = 3,
                        Join = JoinType.Miter, Cap = CapType.Butt, MiterLimit = 2.0, RoundSegments = 4,
                        OutVertices = outV, OutIndices = outI,
                        OutVertexCount = vcArr, OutIndexCount = icArr,
                    }.Run();
                    return (vcArr[0], icArr[0]);
                }
                finally
                {
                    pts.Dispose(); ups.Dispose(); outV.Dispose(); outI.Dispose(); vcArr.Dispose(); icArr.Dispose();
                }
            }, out int workerTid, out Exception ex);

            vc = result.Item1; ic = result.Item2;
            Assert.IsNull(ex, $"LineRibbonJob.Run() THREW off a worker (Allocator.Temp off-thread?): {ex}");
            Assert.AreNotEqual(mainTid, workerTid, "spike must actually run off the main thread");
            Assert.Greater(vc, 0, "3-point polyline must produce line vertices off-main");
            Assert.Greater(ic, 0, "3-point polyline must produce indices off-main");
        }

        private static (int idxCount, int workerTid, Exception ex) RunOnWorkerEarcut()
        {
            int idxCount = -1;
            var result = RunOnWorker(() =>
            {
                // CCW unit square, no holes: polyVC=4 → scratchCap=4, idxCap=6.
                var verts   = new NativeArray<double2>(4, Allocator.Persistent);
                var holeCnt = new NativeArray<int>(1, Allocator.Persistent);
                var outIdx  = new NativeArray<int>(6, Allocator.Persistent);
                var outCnt  = new NativeArray<int>(1, Allocator.Persistent);
                var force   = new NativeArray<int>(1, Allocator.Persistent);
                var vx = new NativeArray<double>(4, Allocator.Persistent);
                var vy = new NativeArray<double>(4, Allocator.Persistent);
                var prev = new NativeArray<int>(4, Allocator.Persistent);
                var next = new NativeArray<int>(4, Allocator.Persistent);
                var isBridge = new NativeArray<bool>(4, Allocator.Persistent);
                var removed  = new NativeArray<bool>(4, Allocator.Persistent);
                var isEar    = new NativeArray<bool>(4, Allocator.Persistent);
                try
                {
                    verts[0] = new double2(0, 0);
                    verts[1] = new double2(1, 0);
                    verts[2] = new double2(1, 1);
                    verts[3] = new double2(0, 1);

                    new EarcutJob
                    {
                        PolyVertices = verts, OuterCount = 4,
                        SortedHoleCounts = holeCnt, HoleCount = 0,
                        OutIndices = outIdx, OutIndexOffset = 0,
                        OutIndexCount = outCnt, OutForceClipCount = force,
                        Vx = vx, Vy = vy, Prev = prev, Next = next,
                        IsBridgeCopy = isBridge, Removed = removed, IsEar = isEar,
                    }.Run();

                    return outCnt[0];
                }
                finally
                {
                    verts.Dispose(); holeCnt.Dispose(); outIdx.Dispose(); outCnt.Dispose(); force.Dispose();
                    vx.Dispose(); vy.Dispose(); prev.Dispose(); next.Dispose();
                    isBridge.Dispose(); removed.Dispose(); isEar.Dispose();
                }
            }, out int workerTid, out Exception ex);

            idxCount = result;
            return (idxCount, workerTid, ex);
        }
    }
}
