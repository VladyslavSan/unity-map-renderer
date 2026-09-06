// Unity EditMode only — NativeArray, Burst jobs. NOT registered in core-tests.csproj.
//
// job-scheduling-design.md §8 stage 5 (the wall-job-graph stage), tooth (c) — every buffer
// FillExtrusionMeshGraph.Schedule allocates gets exactly one matching Dispose(handle) node. Mirrors
// FillMeshGraphSchedulingTests' own pairing tooth against FillGraphOutput, against FillExtrusionGraphOutput's
// own (separate) counter pair instead.

using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Unity.Rendering.Meshing;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class FillExtrusionMeshGraphSchedulingTests
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

        /// <summary>Pairing check (tooth (c)) — <see cref="FillExtrusionGraphOutput.DebugBuffersAllocated"/>
        /// vs. <see cref="FillExtrusionGraphOutput.DebugBufferDisposeNodes"/>, the same PAIRING idiom
        /// <c>FillGraphOutput</c>/<c>LineGraphOutput</c> already carry (a worker-side <c>Dispose(handle)</c>
        /// node cannot decrement a managed counter on completion, so balance is observable only as this
        /// pairing, never a live count). No non-vacuity clause here — deliberately: these are process-wide
        /// monotonic statics, so in a batch run any earlier test that drove this graph already satisfies
        /// "allocated more than zero". This tooth's teeth come entirely from the RED-verify (delete one
        /// <c>ScheduleDispose</c> call in <c>FillExtrusionMeshGraph</c>, confirm this test fails), not from
        /// the equality alone.
        ///
        /// <para><b>What <paramref name="clipped"/> buys, stated honestly.</b> Not a new counter: the clip
        /// arm adds NO <c>NewBuffer</c> allocation — its ping-pong buffers are raw <c>NativeArray</c>s,
        /// uncounted, exactly as the roof's are. What the second case buys is that the clip arm is
        /// EXECUTED and its dispose nodes reached, under the same disposal assertion; without it this tooth
        /// had only ever scheduled the <c>RingSelectJob</c> arm. The fixture is wholly inside
        /// <c>[0, 4096]</c>, so the clip takes <c>RingClipJob</c>'s verbatim fast path — intended: this
        /// tooth is about node/dispose pairing, not clipping arithmetic.</para></summary>
        /// <param name="clipped">Whether to schedule the clip arm (<c>true</c>) or the select arm.</param>
        [Test]
        public void Dispose_BufferDisposeNodesPairWithAllocations([Values(false, true)] bool clipped)
        {
            TileGeometryBuffers geometry = SingleTrianglePolygon(new TileId { Z = 0, X = 0, Y = 0 });
            NativeArray<int> visitOrder = default;
            NativeArray<Vector4> colors = default;
            NativeArray<Vector2> bake = default;
            try
            {
                visitOrder = TestTileMeshBuilder.FullVisitOrder(geometry);
                colors = new NativeArray<Vector4>(geometry.FeatureCount, Allocator.Persistent);
                bake   = new NativeArray<Vector2>(geometry.FeatureCount, Allocator.Persistent);
                var input = new FillMeshPipeline.LayerInput
                {
                    Geometry = geometry, RingVisitOrder = visitOrder, OriginRender = double3.zero,
                    Projection = new WebMercatorProjection(),
                    Clip = clipped ? TileBufferClip.KeepTileUnits(0.0) : TileBufferClip.Disabled,
                };

                long allocBefore    = FillExtrusionGraphOutput.DebugBuffersAllocated;
                long disposedBefore = FillExtrusionGraphOutput.DebugBufferDisposeNodes;

                FillExtrusionGraphOutput ext = FillExtrusionMeshGraph.Schedule(input, colors, bake);
                JobHandle.ScheduleBatchedJobs();
                ext.Dispose(); // completes internally (Handle.Complete()), then frees Roof + Walls

                long allocAfter    = FillExtrusionGraphOutput.DebugBuffersAllocated;
                long disposedAfter = FillExtrusionGraphOutput.DebugBufferDisposeNodes;
                Assert.AreEqual(allocAfter - allocBefore, disposedAfter - disposedBefore,
                    "every scratch NativeList this call allocated must get exactly one Dispose(handle) node");
            }
            finally
            {
                if (visitOrder.IsCreated) visitOrder.Dispose();
                if (colors.IsCreated) colors.Dispose();
                if (bake.IsCreated) bake.Dispose();
                geometry.Dispose();
            }
        }
    }
}
