// job-scheduling-design.md §8 stage 5 (line graph) — acceptance teeth (c) and (f), which need the full
// TileManager/MapView pump (see LineGraphSchedulingTests.cs / LineGraphParityTests.cs for the rest).
// Mirrors SourceTileGraphBuildTests' drive shape and its own header rules: pump BEFORE testing
// AllTilesSettled(); every process-wide counter as a DELTA; never Await/Drain while a delay job holds a
// graph step.
//
// Style: geolines-stroke@0 only, on the geolines source-layer (LineString geometry — the SAME fixture/
// layer StyledLineBufferParityTests and ThrottleTests.FillAndLineStyle already use), so a mixed style's
// fill layer cannot satisfy either tooth's assertions for the wrong reason.

using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs.Lines;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class LineGraphKickTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument LineOnlyStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""geolines-stroke"", ""type"": ""line"", ""source"": ""maplibre"",
                    ""source-layer"": ""geolines"",
                    ""paint"": { ""line-color"": [""rgba"", 100, 200, 50, 1], ""line-width"": 10 }
                }
            ]
        }");

        // ── Tooth (c): the graph is scheduled by the pump, not executed inside the prologue ─────────

        /// <summary>
        /// (c) With <see cref="InlineWorkScheduler"/> injected on a LINE-ONLY style, the prologue's
        /// <c>WorkHandle</c> completes with the request's native columns and NO ribbon geometry, and the
        /// tile is at <c>BuildStep == Measure</c> after the kick Tick. Two assertions — the timing one
        /// alone does not back the title: (1) <see cref="MapViewTestExtensions.CaptureTelemetry"/>'s
        /// <c>GraphMeasureInFlight</c> moves only AFTER the hand-off tick, held open by
        /// <see cref="TileManager.GraphDepsForTest"/>; (2) <see cref="LineGraphOutput.DebugBuffersAllocated"/>
        /// — a MONOTONIC counter, never walked back — has NOT advanced at the moment the prologue hands
        /// over, so a prologue that scheduled-AND-completed the graph inline (returning a merely-BALANCED
        /// live count) cannot pass this by accident.
        /// </summary>
        [Test]
        public void LineLayer_GraphScheduledByThePump_NotExecutedInsideThePrologueBody()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("LineGraphKick_C");
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

                long buffersBaseline = LineGraphOutput.DebugBuffersAllocated;

                int caller = System.Environment.CurrentManagedThreadId;
                var spy = new RecordingWorkScheduler(new InlineWorkScheduler());
                view.TileManager.WorkScheduler = spy;

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: LineOnlyStyle());

                int kickTick = -1, tick = 0;
                for (; tick < 3000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                }
                Assert.GreaterOrEqual(kickTick, 0, "drive precondition: the tile's build must have been started " +
                    "(kickTick starts at -1, so this is load-bearing, not trivially true — it is what stops a " +
                    "never-kicked run from passing the assertions below).");
                Assert.GreaterOrEqual(spy.ScheduleCount, 1, "the prologue kick must go THROUGH the injected scheduler.");
                Assert.GreaterOrEqual(spy.BodyThreadIds.Count, 1, "the body must actually have run at least once.");
                foreach (int tid in spy.BodyThreadIds)
                    Assert.AreEqual(caller, tid, "under Inline the prologue body runs on the CALLING thread.");

                Assert.AreEqual(buffersBaseline, LineGraphOutput.DebugBuffersAllocated,
                    "the prologue body itself must not have scheduled the line measure graph — it only " +
                    "builds the request (BuildGraphRequest); LineMeshGraph.Schedule is the PUMP's job, next tick.");

                // Next tick: prologue-complete hands off to ScheduleMeasureFromDecode — the line graph is
                // genuinely scheduled now, held open by the still-gated delay job.
                view.LateUpdate();
                Assert.Greater(LineGraphOutput.DebugBuffersAllocated, buffersBaseline,
                    "a real line measure graph must have been scheduled by now — the geometry left the prologue body.");
                Assert.GreaterOrEqual(view.CaptureTelemetry().GraphMeasureInFlight, 1,
                    "the tile must be observed in its MEASURE step — deterministic under the still-held delay job.");
                Assert.IsFalse(view.AllTilesSettled(),
                    "the tile must not read settled while its measure step is genuinely held incomplete.");
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

        // ── Tooth (f): a line layer allocates no Mesh.MeshDataArray at kick ──────────────────────────

        /// <summary>
        /// (f) On a LINE-ONLY style, <see cref="MapViewTestExtensions.MeshDataArraysAllocatedLastKick"/> is
        /// 0 at the kick Tick and non-zero once the write step runs, and the tile still produces a mesh with
        /// real vertices. Complement of tooth (c): (c) observes inline execution on the GRAPH path; this one
        /// observes the ABSENCE of any kick-time allocation — job-scheduling-design.md §8 stage 5 Group B
        /// retired the seam arm entirely, so there is no second, synchronous-mesh-write path left for a line
        /// layer to fall back onto; this pins that the graph-arm path it actually takes allocates nothing at
        /// kick either.
        /// </summary>
        [Test]
        public void LineLayer_AllocatesNoMeshDataArrayAtKick_OnlyAtWrite()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("LineGraphKick_F");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            try
            {
                long payloadBaseline = MeshDataPayload.DebugLiveAllocCount;

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: LineOnlyStyle());

                int kickTick = -1, tick = 0;
                for (; tick < 3000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                }
                Assert.GreaterOrEqual(kickTick, 0, "drive precondition: the tile's build must have been started " +
                    "(kickTick starts at -1, so this is load-bearing, not trivially true — it is what stops a " +
                    "never-kicked run from passing the assertions below).");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "NO MeshDataArray at kick for a line-only style — line is graph-arm now, exactly like fill.");
                Assert.AreEqual(0, view.MeshDataArraysAllocatedLastKick(),
                    "the kick Tick itself allocates nothing — only a later write-kick does.");

                int writeTick = -1;
                for (; tick < 3000 && writeTick < 0; tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.MeshDataArraysAllocatedLastKick() > 0) writeTick = tick;
                }
                Assert.GreaterOrEqual(writeTick, 0, "the write step must eventually allocate.");
                Assert.AreEqual(1, view.MeshDataArraysAllocatedLastKick(),
                    "exactly one MeshDataArray on the write-kick tick — the one non-empty line layer.");

                for (; tick < 3000 && !view.AllTilesSettled(); tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }
                Assert.IsTrue(view.AllTilesSettled(), "the tile must eventually settle.");
                Assert.Greater(view.GameObjectRenderer().DrawItemCount(), 0,
                    "the both-ends rule: a progression that never produces a mesh is indistinguishable from a " +
                    "build that never happened.");
            }
            finally
            {
                // Unconditional — an assertion failure above must not leak the view or leave its process-wide
                // counters (MeshDataPayload.DebugLiveAllocCount et al.) elevated for whatever test runs next.
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
