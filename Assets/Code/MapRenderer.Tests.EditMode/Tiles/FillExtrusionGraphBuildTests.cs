// job-scheduling-design.md §8 stage 4, teeth (b) and (c) — the extrusion layer takes the graph arm in
// production (one mesh per layer, slot-joined) and its owned columns (WallColumns, the request) are freed
// at every step. Mirrors SourceTileGraphBuildTests' drive shape (same file's own header rules apply here:
// pump BEFORE testing AllTilesSettled(); every process-wide counter as a DELTA; never Await/Drain while a
// delay job holds a graph step).
//
// Style: fill@0, fill-extrusion@1 (constant height — teeth (b)/(c) are about allocation/scheduling/disposal
// STRUCTURE, not the bake; tooth (a) already covers the data-driven bake byte-for-byte), line@2 (matches no
// LineString geometry on this polygon-only fixture — the "graph request that produces zero vertices" case
// rides along; UPDATED job-scheduling-design.md §8 stage 5: line joined the graph arm too, so this is no
// longer the seam-arm slot the original comment named — it is now just another empty graph-arm layer,
// exactly like a fill layer with no matching features).

using System;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class FillExtrusionGraphBuildTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MixedStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] }
                },
                {
                    ""id"": ""countries-extrusion"", ""type"": ""fill-extrusion"", ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-extrusion-height"": 50 }
                },
                {
                    ""id"": ""countries-line"", ""type"": ""line"", ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""line-color"": ""#000000"" }
                }
            ]
        }");

        /// <summary>
        /// (b) The extrusion layer takes the graph arm in production: NO kick-time <c>MeshDataArray</c> at
        /// all — fill, fill-extrusion AND line (job-scheduling-design.md §8 stage 5) are all graph-arm now,
        /// so nothing allocates until a write step runs. The measure step genuinely builds
        /// wall geometry (<see cref="StyledFillExtrusionTileBuilder.WallColumns.DebugTotalCreated"/>
        /// advances — job-scheduling-design.md §8 stage 5, the wall-job-graph stage moved this out of the
        /// prologue into <c>FillExtrusionMeshGraph.Schedule</c>), the measure graph is genuinely scheduled,
        /// and the write step allocates exactly TWO arrays (fill + extrusion) — settling to two real meshes;
        /// the empty line layer's graph produces zero vertices and gets no write step, counted nowhere.
        ///
        /// <para><b>RED 1 (retired, job-scheduling-design.md §8 stage 5 Group B):</b> used to remove
        /// <c>IGraphInputRenderLayer</c> from <c>FillExtrusionRenderLayer</c> to fall the layer back to the
        /// seam arm, reading a kick-time allocation delta of 1 instead of 0. Group B deleted both the
        /// interface (merged into <c>ITileMeshRenderLayer</c>, D1) and the seam arm itself — there is no
        /// longer a second arm to fall back to, so this injection point no longer exists. The property it
        /// guarded — no kick-time allocation for ANY layer — is what tooth (f)'s sibling in
        /// <c>LineGraphKickTests</c> and this test's own assertion below still pin.</para>
        /// <para><b>RED 2 (review B2):</b> in <c>FillExtrusionLayerBuild.TryScheduleWrite</c> route the
        /// extrusion build through <c>StyledFillTileBuilder.ScheduleWrite</c> instead of its own — the
        /// mesh/material-slot counts above do NOT move (fill's write is not self-guarding, so it still
        /// allocates, still draws), so only the TexCoord4 assertion below reds: fill's descriptor set never
        /// declares TexCoord3/4, so a mis-routed extrusion mesh ends up with neither.</para>
        /// </summary>
        [Test]
        public void ExtrusionLayer_OneMeshPerLayer_WriteAllocatesExactlyTwo()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("FillExtrusionGraphBuild_B");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0; // z0: exactly one covered tile
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;

            try
            {
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                long payloadBaseline = MeshDataPayload.DebugLiveAllocCount;
                long wallsBaseline   = StyledFillExtrusionTileBuilder.WallColumns.DebugTotalCreated;

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: MixedStyle());

                int kickTick = -1, tick = 0;
                for (; tick < 3000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                }
                Assert.GreaterOrEqual(kickTick, 0, "drive precondition: the tile's build must have been started.");

                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "NO MeshDataArray at kick — fill, fill-extrusion and line are all graph-arm now " +
                    "(job-scheduling-design.md §8 stage 5); nothing allocates until a write step runs.");

                // "Kicked" only means the prologue task was DISPATCHED, not that it ran — no WorkScheduler
                // override here (unlike SourceTileGraphBuildTests' tooth (b), which forces InlineWorkScheduler
                // to make the body synchronous), so the prologue genuinely runs off-thread. Await it before
                // the next tick schedules the measure graph.
                view.AwaitInFlightMeshBuilds();

                // Next tick: prologue-complete hands off to the measure graph, held open by the still-gated
                // delay. job-scheduling-design.md §8 stage 5 (the wall-job-graph stage): WallColumns.Allocate()
                // moved from BuildLayerInput (the prologue, awaited above) into FillExtrusionMeshGraph.Schedule
                // (the measure step, scheduled by THIS LateUpdate) — so the wall-columns witness reads after
                // this call now, not after the prologue await; it no longer observes anything BuildLayerInput
                // itself does.
                view.LateUpdate();
                Assert.GreaterOrEqual(view.CaptureTelemetry().GraphMeasureInFlight, 1,
                    "a real measure graph must be scheduled and held in flight by the delay job.");
                Assert.Greater(StyledFillExtrusionTileBuilder.WallColumns.DebugTotalCreated, wallsBaseline,
                    "the measure step must have scheduled FillExtrusionMeshGraph for the extrusion layer — " +
                    "real wall columns exist.");

                gate[0] = 1; // release — measure completes, write is scheduled
                int writeTick = -1;
                for (; tick < 3000 && writeTick < 0; tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.MeshDataArraysAllocatedLastKick() > 0) writeTick = tick;
                }
                Assert.GreaterOrEqual(writeTick, 0, "the write step must eventually allocate.");
                Assert.AreEqual(2, view.MeshDataArraysAllocatedLastKick(),
                    "exactly two MeshDataArrays on the write-kick tick — fill and fill-extrusion (non-empty " +
                    "graph layers only; the empty line slot is pass-through and counts nowhere).");

                for (; tick < 3000 && !view.AllTilesSettled(); tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }
                Assert.IsTrue(view.AllTilesSettled(), "the tile must eventually settle.");

                var id = new MapRenderer.Core.Geo.TileId { Z = 0, X = 0, Y = 0 };
                Mesh[] meshes = view.GetTileMeshes(id);
                Assert.AreEqual(2, meshes?.Length ?? 0,
                    "two real meshes settle: fill's roof and the extrusion's roof+walls — the empty line " +
                    "layer contributes none.");
                // Pins that the graph selected the EXTRUSION writer, not just that two meshes exist (review
                // B2): only StyledFillExtrusionTileBuilder's descriptor set declares TexCoord3/4
                // (ExtrudeAndBake); fill's does not. A count-only check cannot see a mis-route — routing
                // FillExtrusion through StyledFillTileBuilder.ScheduleWrite still allocates two arrays, two
                // material slots, and draws something, so this is the only assertion here that a mis-route
                // actually moves.
                Assert.IsTrue(meshes.Any(m => m.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord4)),
                    "one of the two meshes must carry the extrusion-only TexCoord4 stream (BakedBaseHeight) " +
                    "— proves the write dispatch actually reached StyledFillExtrusionTileBuilder.ScheduleWrite, " +
                    "not just that some mesh with the right material count exists.");
                int[] materialIndices = view.GetTileMaterialIndices(id);
                Assert.AreEqual(2, materialIndices.Length);
                Assert.AreNotEqual(materialIndices[0], materialIndices[1],
                    "the two meshes must draw at two DISTINCT material slots — fill and extrusion never share one.");
                Assert.Greater(view.GameObjectRenderer().DrawItemCount(), 0,
                    "the both-ends rule: a progression that never produces a mesh is indistinguishable from a " +
                    "build that never happened.");
            }
            finally
            {
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary>
        /// (c) The extrusion columns are freed on a normal build-then-teardown cycle — every
        /// <see cref="StyledFillExtrusionTileBuilder.WallColumns"/> the measure step allocated
        /// (<see cref="StyledFillExtrusionTileBuilder.WallColumns.DebugTotalCreated"/> advancing is the
        /// non-vacuity witness: a "back to zero" live count on a counter that never moved proves nothing) is
        /// disposed by teardown, alongside the request and graph counters fill already pins.
        ///
        /// <para><b>RED (re-pointed again, the per-layer build-object stage — <c>Walls</c> is now owned
        /// whole, as part of <c>FillExtrusionGraphOutput</c>, by <c>FillExtrusionLayerBuild</c>, so the
        /// previous recipe's target, <c>TileBuildGraph.Dispose()</c>'s own teardown loop, no longer touches
        /// any kind's fields at all):</b> drop <c>_ext.Dispose();</c> from
        /// <c>FillExtrusionLayerBuild.Dispose()</c> → <c>WallColumns.DebugLiveCount</c> stays elevated above
        /// baseline after teardown.</para>
        ///
        /// <para><b>Arm-agnostic (historical — job-scheduling-design.md §8 stage 5 Group B retired the
        /// seam arm):</b> before Group B this test passed under EITHER arm — the seam-arm
        /// <c>WriteInto</c>/<c>WriteMeshData</c> path reached <c>WallColumns.Allocate()</c> through the same
        /// shared <c>BuildLayerInput</c> the graph arm uses, so both built and freed columns identically.
        /// There is only the graph arm left now, so this tooth pins DISPOSAL on the sole remaining path —
        /// tooth (b) is what pins that it is the graph arm reached in production.</para>
        /// </summary>
        [Test]
        public void ExtrusionColumns_AreFreed_OnNormalBuildThenTeardown()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("FillExtrusionGraphBuild_C");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            try
            {
                long wallsLiveBaseline  = StyledFillExtrusionTileBuilder.WallColumns.DebugLiveCount;
                long wallsTotalBaseline = StyledFillExtrusionTileBuilder.WallColumns.DebugTotalCreated;
                long requestsBaseline   = LayerMeshBuildCounters.DebugLiveBuilds;
                long payloadBaseline    = MeshDataPayload.DebugLiveAllocCount;
                long graphBaseline      = TileBuildGraph.DebugLiveCount;

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: MixedStyle());

                // LoadedTileCount() > 0 is load-bearing, not decoration (ThrottleTests.cs:613's own idiom):
                // the bare !AllTilesSettled() predicate is evaluated BEFORE the first LateUpdate(), and with
                // an empty cover "all tiles settled" is vacuously true — the loop would run zero iterations
                // and never actually pump a build. Caught by this tooth's own witness reading a flat 0.
                for (int f = 0; f < 3000 && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }
                Assert.IsTrue(view.LoadedTileCount() > 0 && view.AllTilesSettled(),
                    "drive precondition: the tile must settle before teardown — LoadedTileCount() > 0 rules " +
                    "out a vacuous settle (nothing ever loaded) satisfying AllTilesSettled() on its own.");
                Assert.Greater(StyledFillExtrusionTileBuilder.WallColumns.DebugTotalCreated, wallsTotalBaseline,
                    "non-vacuity witness: real wall columns must have been allocated during this build, or " +
                    "the 'back to zero' reading below proves nothing.");

                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                go = null;

                Assert.AreEqual(wallsLiveBaseline, StyledFillExtrusionTileBuilder.WallColumns.DebugLiveCount,
                    "every WallColumns this build allocated must be freed by teardown — back to baseline.");
                Assert.AreEqual(requestsBaseline, LayerMeshBuildCounters.DebugLiveBuilds,
                    "every build's own request columns must be freed by teardown — back to baseline.");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "every MeshDataArray must be freed by teardown — back to baseline.");
                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "every TileBuildGraph must be freed by teardown — back to baseline.");
            }
            finally
            {
                if (go != null)
                {
                    view.Teardown();
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
        }
    }
}
