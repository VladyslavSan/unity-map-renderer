// Unity EditMode only — NativeArray, Burst jobs. NOT registered in core-tests.csproj.
//
// job-scheduling-design.md §8 stage 1: not-completed-at-return, and scratch/output dispose-balance. The
// edge-check safety-system RED is NOT an automated test here — it is a recorded, once-executed manual run:
// drop one node's `deps`, run the parity tooth, observe the InvalidOperationException the safety system
// raises at Schedule (naming both jobs and the shared container), then restore.

using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Tests.TestSupport;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class FillMeshGraphSchedulingTests
    {
        private static TileGeometryBuffers SingleTrianglePolygon(TileId tile)
        {
            var g = TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 1, maxRings: 1, maxVertices: 3);
            g.FeatureGeometryType[0] = TileGeometryType.Polygon;
            g.RingOffsets[0] = 0;
            g.Vertices[0] = new double2(0, 0);
            g.Vertices[1] = new double2(10, 0);
            g.Vertices[2] = new double2(10, 10);
            g.RingFeatureIdx[0] = 0;
            g.RingOffsets[1] = 3;
            g.RingCount = 1;
            g.VertexCount = 3;
            return g;
        }

        /// <summary>A whole-tile triangle (unlike <see cref="SingleTrianglePolygon"/>'s tiny sliver) — large
        /// enough in angular extent that <see cref="SphericalProjection"/> genuinely subdivides it, so the
        /// curved arm's sub-chain is actually exercised, not merely entered.</summary>
        private static TileGeometryBuffers LargeTrianglePolygon(TileId tile)
        {
            var g = TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 1, maxRings: 1, maxVertices: 3);
            g.FeatureGeometryType[0] = TileGeometryType.Polygon;
            g.RingOffsets[0] = 0;
            g.Vertices[0] = new double2(0, 0);
            g.Vertices[1] = new double2(4096, 0);
            g.Vertices[2] = new double2(0, 4096);
            g.RingFeatureIdx[0] = 0;
            g.RingOffsets[1] = 3;
            g.RingCount = 1;
            g.VertexCount = 3;
            return g;
        }

        // ── (b) Not-completed-at-return ─────────────────────────────────────────────────────────────

        /// <summary>What this proves and no more: an unflushed job cannot have started, so this cannot
        /// false-red on a small fixture — and for the same reason it proves only that nothing completed
        /// SYNCHRONOUSLY, which is exactly the defect it names. Statement order is load-bearing: the poll
        /// must be immediate and precede <c>ScheduleBatchedJobs()</c>. Its honest complement is the
        /// structural check, <c>FillMeshGraphStructureTests</c> — this test alone could pass on a builder
        /// that happened to be slow enough not to finish before the poll runs, on THIS machine, THIS run.</summary>
        [Test]
        public void Schedule_ReturnsWithHandleNotCompleted_BeforeAnyFlush()
        {
            TileGeometryBuffers geometry = SingleTrianglePolygon(new TileId { Z = 0, X = 0, Y = 0 });
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
            };
            try
            {
                FillGraphOutput o = FillMeshGraph.Schedule(input);
                Assert.IsFalse(o.Handle.IsCompleted,
                    "the graph must not complete synchronously inside Schedule — a builder that Complete()s " +
                    "internally settles the tile in one step, defeating the whole point of a graph");
                JobHandle.ScheduleBatchedJobs();
                o.Handle.Complete();
                Assert.IsTrue(o.IsCreated);
                o.Dispose();
            }
            finally
            {
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }

        // ── (d) Scratch freed ────────────────────────────────────────────────────────────────────────

        /// <summary>Baseline/delta against the three static counters — same idiom
        /// <c>DisposalLeakGuardTests</c> uses against <c>MeshDataPayload.DebugLiveAllocCount</c>, since the
        /// counters are process-wide and other tests in the same batch run also touch them.
        /// <see cref="FillGraphOutput.DebugBufferDisposeNodes"/> vs. <see cref="FillGraphOutput.DebugBuffersAllocated"/>
        /// is a PAIRING check, not a live count — a worker-side <c>Dispose(handle)</c> node cannot decrement
        /// a managed counter on completion (it runs off the main thread).</summary>
        [Test]
        public void Dispose_ReturnsLiveOutputsToBaseline_AndBufferDisposeNodesPairWithAllocations()
        {
            TileGeometryBuffers geometry = SingleTrianglePolygon(new TileId { Z = 0, X = 0, Y = 0 });
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new WebMercatorProjection(), // was implicit (null ⇒ Mercator); now explicit
            };
            try
            {
                long liveBefore     = FillGraphOutput.DebugLiveCount;
                long allocBefore    = FillGraphOutput.DebugBuffersAllocated;
                long disposedBefore = FillGraphOutput.DebugBufferDisposeNodes;

                FillGraphOutput o = FillMeshGraph.Schedule(input);
                JobHandle.ScheduleBatchedJobs();
                o.Dispose(); // completes internally (Handle.Complete()), then frees every field

                Assert.AreEqual(liveBefore, FillGraphOutput.DebugLiveCount,
                    "every output container this call allocated must be freed by Dispose()");

                long allocAfter    = FillGraphOutput.DebugBuffersAllocated;
                long disposedAfter = FillGraphOutput.DebugBufferDisposeNodes;
                Assert.Greater(allocAfter, allocBefore,
                    "precondition: this Schedule call must actually have allocated scratch, or the pairing " +
                    "assertion below is vacuous");
                Assert.AreEqual(allocAfter - allocBefore, disposedAfter - disposedBefore,
                    "every scratch NativeList this call allocated must get exactly one Dispose(handle) node");
            }
            finally
            {
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }

        // ── (g) Counters still balance on a CURVED layer — job-scheduling-design.md §8 stage 4. ────────────

        /// <summary>Same baseline/delta idiom as the flat-arm test above, over a layer that genuinely takes
        /// the curved sub-chain (<see cref="FillMeshGraphSchedulingTests.LargeTrianglePolygon"/> +
        /// <see cref="SphericalProjection"/>) — the flat-arm test alone cannot catch a balance bug specific
        /// to the subdivide sub-chain, since that code path never runs there.</summary>
        [Test]
        public void Dispose_ReturnsLiveOutputsToBaseline_OnACurvedLayer()
        {
            TileGeometryBuffers geometry = LargeTrianglePolygon(new TileId { Z = 0, X = 0, Y = 0 });
            NativeArray<int> visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
            var input = new FillMeshPipeline.LayerInput
            {
                Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                Projection = new SphericalProjection(),
            };
            try
            {
                long liveBefore     = FillGraphOutput.DebugLiveCount;
                long allocBefore    = FillGraphOutput.DebugBuffersAllocated;
                long disposedBefore = FillGraphOutput.DebugBufferDisposeNodes;

                FillGraphOutput o = FillMeshGraph.Schedule(input);
                JobHandle.ScheduleBatchedJobs();
                o.Handle.Complete();
                // job-scheduling-design.md §3.7: TileVertices IS the post-subdivision column on the curved
                // arm now, so "genuinely subdivided" reads as "more vertices than the raw 3-vertex triangle".
                Assert.Greater(o.TileVertices.Length, 3, "precondition: the layer must genuinely subdivide");
                o.Dispose();

                Assert.AreEqual(liveBefore, FillGraphOutput.DebugLiveCount,
                    "every output container this call allocated — including the scattered post-subdivision " +
                    "columns — must be freed by Dispose()");

                long allocAfter    = FillGraphOutput.DebugBuffersAllocated;
                long disposedAfter = FillGraphOutput.DebugBufferDisposeNodes;
                Assert.AreEqual(allocAfter - allocBefore, disposedAfter - disposedBefore,
                    "every scratch NativeList this call allocated must get exactly one Dispose(handle) node, " +
                    "on the curved arm too");
            }
            finally
            {
                visitOrder.Dispose();
                geometry.Dispose();
            }
        }
    }
}
