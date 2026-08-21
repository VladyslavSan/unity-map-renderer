// S46 acceptance tests — profiler marker wiring.
//
// Tooth 1a (greppable presence): verify every MapRenderer.* marker name is reachable via the
//   declared static fields (compile-time check; names are greppable in source).
// Tooth 1b (wired-not-dead): ProfilerRecorder reads sample count > 0 after driving a tile load,
//   proving MapRenderer.Meshing.StyledFillTileBuilder.WriteMeshData is wired on the live MapView path (not dead code).
// Tooth 2 (no behavior change): covered by the existing suite (MapViewLiveLoopTests, pipeline tests).
//
// ProfilerRecorder notes:
//   - Use ProfilerCategory.Scripts to match the explicit category on each ProfilerMarker constructor.
//   - EditMode headless: profiler samples may commit at frame boundaries. We use a [UnityTest]
//     coroutine with yield return null to advance at least one frame after the tile load settles,
//     then check accumulated sample count via ProfilerRecorder.Count.
//   - The recorder is started BEFORE the tile load so it captures the samples fired during BuildTile.

using System.Collections;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
// Marker-owning types reached by alias rather than by importing their whole namespace, so pulling in one
// const cannot drag a name that collides with the Core.* usings above. `MapView` is deliberately NOT aliased
// to MapViewComponent (as it once was) — the marker SSOT lives on the MapView class itself, and shadowing the
// name made `MapView.ProfilerMarkerNames` silently resolve to the wrong type.
using TileDecodeDispatch   = MapRenderer.Unity.Rendering.Tile.Processing.TileDecodeDispatch;
using FillMeshPipeline     = MapRenderer.Jobs.FillMeshPipeline;
using MvtDecoder           = MapRenderer.Jobs.Mvt.MvtDecoder;
using FillRenderLayer      = MapRenderer.Unity.Rendering.Style.FillRenderLayer;
using LineRenderLayer      = MapRenderer.Unity.Rendering.Style.LineRenderLayer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class ProfilerMarkerTests
    {
        /// <summary>
        /// Ring capacity requested from every <see cref="ProfilerRecorder"/> here, and therefore the only
        /// safe upper bound when reading samples back — <c>Count</c> is NOT one once the ring has wrapped.
        /// </summary>
        private const int RecorderCapacity = 64;

        // ── Helpers (mirrors MapViewLiveLoopTests; duplicated to keep test file self-contained) ──
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""Test"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": {
                        ""fill-color"": [""rgba"", 200, 50, 50, 1]
                    }
                }
            ]
        }");


        private static void PumpUntilSettled(MapViewComponent view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        // ── Tooth 1a: greppable presence — all expected marker names are declared ──
        //
        // This is a compile-time check: every entry below is a `const` reference into the owning type's
        // nested ProfilerMarkerNames (the SSOT), so deleting or renaming a production marker breaks THIS
        // FILE'S compile. The runtime "MapRenderer." prefix assertion is belt-and-suspenders on top.
        //
        // A bare string literal here would be worthless — it compiles whatever production does, so it can
        // (and did) outlive the marker it names. Three such literals were removed when the last declaration
        // sites moved to the SSOT pattern: "MapRenderer.Mesh.Build", "MapRenderer.Line.MeshBuild" and
        // "MapRenderer.Symbol.BatchBuild.SoA.Project" named no marker anywhere in production. NEVER add a
        // literal back — if a name has no const to point at, the marker does not exist.
        [Test]
        public void ProfilerMarkers_AllExpectedNamesAreReachable()
        {
            // Verify each marker name by constructing a new ProfilerMarker with the expected name
            // and checking the name round-trips. This also confirms Unity.Profiling is accessible.
            string[] expectedNames =
            {
                MapView.ProfilerMarkerNames.CameraAdvance,
                TileManager.ProfilerMarkerNames.CoverSelect,
                TileManager.ProfilerMarkerNames.FetchPoll,
                TileManager.ProfilerMarkerNames.SchedulerRequest,
                TileDecodeDispatch.ProfilerMarkerNames.TileDecode,
                StyledFillTileBuilder.ProfilerMarkerNames.WriteMeshData,
                TileManager.ProfilerMarkerNames.MeshUpload,
                MvtDecoder.ProfilerMarkerNames.Decode, // IR C1 P3: the marker follows the decode it brackets
                FillMeshPipeline.ProfilerMarkerNames.Clip,
                FillMeshPipeline.ProfilerMarkerNames.RingAssembly,
                FillMeshPipeline.ProfilerMarkerNames.Earcut,
                FillMeshPipeline.ProfilerMarkerNames.Project,
                // Per-frame MapView.LateUpdate sub-phases + EG-drive split (added to localise live zoom spikes).
                MapView.ProfilerMarkerNames.LateUpdate,
                MapView.ProfilerMarkerNames.SceneFrame,
                MapView.ProfilerMarkerNames.SymbolCollect,
                MapView.ProfilerMarkerNames.SymbolBatch,
                // Symbol-label markers below read their names from each type's nested ProfilerMarkerNames const
                // (SSOT), reached via InternalsVisibleTo — renaming a marker is a one-line edit at its source.
                SymbolLabelSubsystem.ProfilerMarkerNames.SymbolExtract,
                SymbolLabelSubsystem.ProfilerMarkerNames.AtlasUpload,
                SymbolLabelSubsystem.ProfilerMarkerNames.BatchCollect,
                SymbolLabelSubsystem.ProfilerMarkerNames.BatchCollectClassify,
                SymbolLabelSubsystem.ProfilerMarkerNames.BatchCollectDedup,
                SymbolLabelSubsystem.ProfilerMarkerNames.BatchSoA,
                LabelPlacementSystem.ProfilerMarkerNames.Gather,
                LabelPlacementSystem.ProfilerMarkerNames.Tick,
                LabelPlacementSystem.ProfilerMarkerNames.Project,
                LabelPlacementSystem.ProfilerMarkerNames.ProjectPositions,
                LabelPlacementSystem.ProfilerMarkerNames.Stage,
                LabelPlacementSystem.ProfilerMarkerNames.Collide,
                LabelPlacementSystem.ProfilerMarkerNames.CollideHarvest,
                LabelPlacementSystem.ProfilerMarkerNames.Emit,
                LabelPlacementSystem.ProfilerMarkerNames.EmitLoop,
                LabelPlacementSystem.ProfilerMarkerNames.EmitDecay,
                WorldLabelRenderer.ProfilerMarkerNames.EndFrame,
                MapView.ProfilerMarkerNames.ApplyZoom,
                FillRenderLayer.ProfilerMarkerNames.ApplyZoomFills,
                LineRenderLayer.ProfilerMarkerNames.ApplyZoomLines,
                LineRenderLayer.ProfilerMarkerNames.ApplyZoomLineDash,
                MapView.ProfilerMarkerNames.InstancedRebuild,
                MapView.ProfilerMarkerNames.ManagerTick,
                TileManager.ProfilerMarkerNames.MeshDataAllocate,
                TileManager.ProfilerMarkerNames.AddTileLayer,
                EntitiesTileRenderer.ProfilerMarkerNames.AddLayerRoot,
                EntitiesTileRenderer.ProfilerMarkerNames.AddLayerRegister,
                EntitiesTileRenderer.ProfilerMarkerNames.AddLayerParent,
                EntitiesTileRenderer.ProfilerMarkerNames.RootTransforms,
                EntitiesTileRenderer.ProfilerMarkerNames.InitGroup,
                EntitiesTileRenderer.ProfilerMarkerNames.SimGroup,
                EntitiesTileRenderer.ProfilerMarkerNames.PresGroup,
            };

            foreach (string name in expectedNames)
            {
                Assert.IsTrue(name.StartsWith("MapRenderer."),
                    $"Marker name '{name}' must be under the MapRenderer.* namespace (S46 acceptance).");
            }

            // Belt-and-suspenders: construct each marker (would throw if Unity.Profiling not available).
            foreach (string name in expectedNames)
            {
                // ProfilerMarker construction is allocation-free (struct init).
                var marker = new ProfilerMarker(ProfilerCategory.Scripts, name);
                // No assertion on the marker object itself — the compile-time reference to
                // ProfilerMarker is the real check. This loop just exercises the constructor.
                _ = marker;
            }
        }

        // ── Tooth 1b: wired-not-dead — ProfilerRecorder reads > 0 samples after a tile load ──
        //
        // Uses a [UnityTest] coroutine so we can yield a frame after the tile load completes,
        // giving the profiler a chance to commit the sample data. The recorder is started BEFORE
        // the tile load so it captures the MapRenderer.Meshing.StyledFillTileBuilder.WriteMeshData samples fired in BuildTile.
        //
        // S47 update: BuildMeshData (which fires PmBuildMesh) now runs on a ThreadPool thread inside
        // Task.Run. We must NOT use CollectOnlyOnCurrentThread — that would miss cross-thread samples.
        // ProfilerRecorderOptions.Default collects samples from all threads.
        [UnityTest]
        public IEnumerator ProfilerRecorder_BuildMarker_HasSamplesAfterTileLoad()
        {
            const string markerName    = StyledFillTileBuilder.ProfilerMarkerNames.WriteMeshData;
            const string bogusName     = "MapRenderer.__NoSuchMarker__";

            var go   = new GameObject("MapView_ProfilerTest");
            var view = go.AddComponent<MapViewComponent>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            // Start both recorders BEFORE the tile load — must be open when samples fire.
            // ProfilerCategory.Scripts matches the explicit category in each ProfilerMarker constructor.
            // Negative control (bogus name) verifies the count metric discriminates real hits from frames.
            // S47: use Default (not CollectOnlyOnCurrentThread) — build marker fires on ThreadPool.
            using var recorder      = ProfilerRecorder.StartNew(
                ProfilerCategory.Scripts, markerName, capacity: RecorderCapacity,
                options: ProfilerRecorderOptions.SumAllSamplesInFrame);
            using var bogusRecorder = ProfilerRecorder.StartNew(
                ProfilerCategory.Scripts, bogusName, capacity: RecorderCapacity,
                options: ProfilerRecorderOptions.SumAllSamplesInFrame);

            try
            {
                view.LoadTestStyle(TestDataSource.FromBytes(SampleTileFixture.Bytes()), Cam(0, 0, 0.0),
                    style: MinimalStyle());

                // Drive the tile load synchronously (FixtureSource returns immediately).
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(),
                    "Tiles must be settled before yielding — otherwise the marker may not have fired yet.");

                // Yield a couple of frames so the profiler can commit accumulated samples.
                yield return null;
                yield return null;

                // Sum marker invocations across all recorded frames.
                // ProfilerRecorderSample.Count is the number of times the marker Begin/End fired that frame.
                // This is the correct hit-count metric — recorder.Count alone is the buffer entry count (frames),
                // which would be non-zero even if the marker were never called.
                // Clamp to the ring's CAPACITY, not Count: ProfilerRecorder is a fixed-size ring, and the
                // pump above runs far more frames than that, so once it wraps `Count` stops being a valid
                // index bound and GetSample() throws IndexOutOfRange (intermittently, on slow machines).
                long realHits  = 0;
                for (int i = 0; i < math.min(recorder.Count, RecorderCapacity); i++)
                    realHits += recorder.GetSample(i).Count;

                long bogusHits = 0;
                for (int i = 0; i < math.min(bogusRecorder.Count, RecorderCapacity); i++)
                    bogusHits += bogusRecorder.GetSample(i).Count;

                // Negative control: a non-existent marker must read 0 invocations.
                // If bogusHits > 0 here, then `Count` counts frames, not firings — the metric is wrong.
                Assert.AreEqual(0L, bogusHits,
                    $"Negative-control recorder for '{bogusName}' should report 0 marker invocations " +
                    $"(got {bogusHits}). If non-zero, the sampled metric counts frames, not marker firings.");

                // Real marker: must have fired at least once during the tile load.
                Assert.Greater(realHits, 0L,
                    $"ProfilerRecorder for '{markerName}' collected {realHits} marker invocations after a tile load. " +
                    "Expected > 0 — the marker must be wired on the live MapView → StyledFillTileBuilder path. " +
                    "If this fails with 0: (a) check bogusHits == 0 (metric is discriminating), " +
                    "(b) confirm AllTilesSettled() returned true (load actually ran), " +
                    "(c) the profiler may not commit in this runner — run with PlayMode for reliable frame commits.");
            }
            finally
            {
                view.Teardown(); // dispose the backend world/BRG (OnDestroy does not fire on DestroyImmediate)
                Object.DestroyImmediate(go);
            }
        }
    }
}
