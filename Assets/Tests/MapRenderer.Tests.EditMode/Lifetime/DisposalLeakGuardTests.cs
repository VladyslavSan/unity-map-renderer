// Load→release of N tiles, including release of a built-but-unconsumed tile: zero orphaned Meshes, and
// MeshDataPayload.DebugLiveAllocCount back to baseline, with a deliberate leak as the positive control.

using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Unity.View.Camera;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
namespace MapRenderer.Tests.Lifetime
{
    /// <summary>
    /// Disposal/leak guard for the load→release race: a released built tile destroys its Mesh, a tile
    /// released mid-flight leaves no orphaned Mesh once its build completes, and the full cycle nets zero
    /// tile-owned Meshes.
    /// </summary>
    [TestFixture]
    public class DisposalLeakGuardTests : BaseTestFixture
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""LeakGuard"",
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


        /// <summary>
        /// Pumps Tick until all tiles settle or maxFrames is reached.
        /// </summary>
        private static void PumpUntilSettled(MapView view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        /// <summary>
        /// Counts Mesh objects currently alive in the scene (excluding those owned by the test
        /// infrastructure). Used to detect orphaned/leaked meshes.
        /// </summary>
        private static int CountMeshObjects()
        {
            // Resources.FindObjectsOfTypeAll counts all alive Mesh objects (including hidden/inactive).
            // This over-counts by editor built-in meshes, but we compare delta, not absolute count.
            return Resources.FindObjectsOfTypeAll<Mesh>().Length;
        }

        // ── Build then release — no orphaned Mesh ───────────────────────────────────

        /// <summary>
        /// Build N tiles to completion, record Mesh count delta, then destroy the MapView.
        /// After destruction, mesh count must not have increased (all Meshes disposed by OnDestroy).
        ///
        /// This is a real load then a real release.
        /// </summary>
        [Test]
        public void BuildAndRelease_NoOrphanedMesh()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_LeakGuard_A");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            // Baseline: count Meshes before the map load.
            int meshBefore = CountMeshObjects();

            view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);

            // Drive load: fetch → build → consume (creates Mesh + GameObject).
            PumpUntilSettled(view);
            Assert.IsTrue(view.AllTilesSettled(), "Tiles must settle before testing leak guard.");
            Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                "z0/0/0 tile must be built (real load required for meaningful leak test).");

            // Record how many Meshes were created during the load.
            int meshAfterLoad = CountMeshObjects();
            int createdCount  = meshAfterLoad - meshBefore;
            Assert.Greater(createdCount, 0,
                "At least one Mesh must have been created during tile load " +
                "(otherwise the test doesn't exercise real geometry and the leak check is vacuous).");

            // Headless DestroyImmediate does not fire OnDestroy, so call Teardown() (what OnDestroy delegates
            // to) explicitly; it must destroy all tile Meshes.
            view.Teardown();
            Object.DestroyImmediate(go);

            int meshAfterDestroy = CountMeshObjects();

            // After Teardown no Mesh is orphaned: the count must not exceed the baseline.
            Assert.LessOrEqual(meshAfterDestroy, meshBefore,
                $"Orphaned Meshes detected after MapView.Teardown. " +
                $"Baseline: {meshBefore}, After load: {meshAfterLoad} (+{createdCount}), " +
                $"After destroy: {meshAfterDestroy}. " +
                "Teardown must explicitly destroy all tile Mesh assets (Unity does not do so when " +
                "the containing GameObject is destroyed). Zero orphaned Mesh after real load + full release.");
        }

        // ── Play-mode Stop race: teardown after the Entities World was disposed first ─────────
        // Non-local invariant: on Play-mode Stop Unity disposes the Entities World BEFORE OnDestroy, so a
        // backend RemoveItems that touched the dead EntityManager would throw out of DoDispose and strand the
        // whole graph. This disposes the World under the live map; Teardown must still complete. RED: drop
        // RemoveItems' _world.IsCreated guard (leak assertions red), then also Teardown's catch (it throws).

        /// <summary>
        /// Teardown must complete — no throw, tile Meshes released to baseline, zero VerifiedDisposable
        /// finalizer leaks — even when Unity has already disposed the Entities World before OnDestroy (the
        /// Play-mode Stop ordering). Exercises the RemoveItems guard end-to-end plus the MapView.Teardown /
        /// DoDispose defense-in-depth so no single fault can strand the graph again.
        /// </summary>
        [Test]
        public void Teardown_AfterEntitiesWorldDisposed_ReleasesGraph_NoThrow_NoLeaks()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_LeakGuard_WorldGone");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.Backend = MapRenderer.Unity.Rendering.Map.RenderBackend.Entities; // the backend Unity tears down first

            World prevDefault = World.DefaultGameObjectInjectionWorld; // captured before the backend hijacks it
            var captured = new List<string>();
            System.Action<string> original = VerifiedDisposable.LeakReporter;
            VerifiedDisposable.LeakReporter = msg => captured.Add(msg);

            int meshBefore = CountMeshObjects();
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                    "Non-vacuous: the Entities path must load + settle a tile so the record holds real entities + Meshes.");

                int createdCount = CountMeshObjects() - meshBefore;
                Assert.Greater(createdCount, 0, "Non-vacuous: a real load must create Mesh objects.");

                var ent = view.EntitiesRenderer();
                Assert.IsNotNull(ent, "Entities renderer must be constructed when Backend == Entities.");
                Assert.Greater(ent.DrawItemCount(), 0,
                    "Non-vacuous: at least one tile-layer entity must exist, so RemoveItems has real handles to touch _em with.");

                // Simulate Unity's Play-mode Stop: dispose the Entities World out from under the still-live map.
                World world = ent._em.World;
                Assert.IsTrue(world.IsCreated, "precondition: World alive before external disposal.");
                world.Dispose();
                Assert.IsFalse(world.IsCreated, "precondition: World is dead before Teardown — the state teardown must tolerate.");

                // The symptom fix: Teardown must NOT throw despite the dead World, and must run to completion.
                Assert.DoesNotThrow(() => view.Teardown(),
                    "Teardown must tolerate an already-disposed Entities World (the Play-mode Stop race) instead " +
                    "of throwing out of DoDispose and stranding the subsystem graph.");
                Object.DestroyImmediate(go);
                go = null;

                int meshAfter = CountMeshObjects();
                Assert.LessOrEqual(meshAfter, meshBefore,
                    $"Tile Mesh assets must be released even when the World died first. Baseline {meshBefore}, " +
                    $"after load +{createdCount}, after teardown {meshAfter}. A throw out of the record-teardown " +
                    "loop would skip DestroyTrackedMeshes and strand these meshes.");

                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect();
                Assert.IsEmpty(captured,
                    "Zero VerifiedDisposable finalizer leaks: with the World-disposed-first race handled, every " +
                    $"subsystem still reaches its Dispose(). Captured: [{string.Join("; ", captured)}].");
            }
            finally
            {
                VerifiedDisposable.LeakReporter = original;
                if (go != null) { view.Teardown(); Object.DestroyImmediate(go); }
                // The external World disposal made the backend's own DefaultGameObjectInjectionWorld restore a
                // no-op (it is guarded by _world.IsCreated); restore it so later Entities tests see a clean global.
                if (World.DefaultGameObjectInjectionWorld == null || !World.DefaultGameObjectInjectionWorld.IsCreated)
                    World.DefaultGameObjectInjectionWorld = prevDefault;
            }
        }

        // ── Release mid-flight — no orphaned Mesh ──────────────────────────────────

        /// <summary>
        /// Holds the tiles' measure step in flight on a gated delay job (<see cref="TileManager.GraphDepsForTest"/>),
        /// pans away so they are released while held, then opens the gate: no Mesh may be created for them,
        /// because ReleaseTile removes them from <c>_loaded</c> and ConsumeMeshBuild never sees them. The pan
        /// waits for <c>GraphMeasureInFlight &gt;= 1</c> with the gate closed, so the race is deterministic.
        /// </summary>
        [Test]
        public void ReleaseMidFlight_NoOrphanedMesh()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_LeakGuard_B");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            // MaxConsumesPerTick = 0 BEFORE initialise: prevents the consume from running while the delay
            // job holds the measure step, so nothing races the deterministic gate below.
            view.Config.MaxConsumesPerTick = 0;
            view.Config.MaxMeshBuildsPerTick = 64;
            // Evict the WHOLE condemned cover on the pan tick — the leak assertions are over the whole
            // cover either way, and releasing all of it removes any ordering dependence.
            view.Config.MaxReleasesPerTick = 64;

            int meshBefore = CountMeshObjects();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            // Outside the try, so finally can Complete() it before disposing gate/started/outVals.
            JobHandle delayHandle = default;

            try
            {
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                // Load initial cover at lon=0, z=5.
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);

                // LateUpdate ONLY: an Await would Complete() the held graph. The prologue still runs and
                // hands off to ScheduleMeasure, where the delay job's Gate blocks the measure jobs.
                for (int f = 0; f < 3000 && view.CaptureTelemetry().GraphMeasureInFlight < 1; f++)
                    view.LateUpdate();
                DelayGateJobInstrument.WaitForStart(started);
                Assert.GreaterOrEqual(view.CaptureTelemetry().GraphMeasureInFlight, 1,
                    "drive precondition: at least one tile's measure step must be genuinely held in-flight " +
                    "before the pan, or the eviction below has nothing in-flight to race.");

                // Pan far east — while the measure step is still genuinely held (gate untouched).
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                view.LateUpdate(); // cover recompute → evicts original tiles while genuinely in-flight

                // ── Positive control: at least one tile must have been released mid-flight ──────
                // The gate is still closed, so the released tile's graph cannot have completed.
                Assert.Greater(view.ReleasedMidFlightCount(), 0,
                    "Positive control: at least one tile must have been released while its measure step " +
                    "was still genuinely in-flight (the held delay job guarantees !IsStepComplete at " +
                    "release time). If this is 0, no tile reached the graph before the pan.");

                // Release the gate, restore budgets, and let the new cover settle normally.
                gate[0] = 1;
                view.Config.MaxConsumesPerTick = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                PumpUntilSettled(view, maxFrames: 500);

                // The evicted tiles left _loaded, so ConsumeMeshBuild never ran for them; only the new
                // cover tiles can have created Meshes.
                int meshAfterSettle = CountMeshObjects();

                // Release the new cover by running Teardown then destroying the MapView.
                // (See BuildAndRelease_NoOrphanedMesh for why Teardown() is called explicitly.)
                view.Teardown();
                Object.DestroyImmediate(go);
                go = null; // prevent double-destroy in finally

                int meshAfterDestroy = CountMeshObjects();

                // After full teardown: mesh count must return to baseline.
                Assert.LessOrEqual(meshAfterDestroy, meshBefore,
                    $"Orphaned Meshes after mid-flight release race + MapView.OnDestroy. " +
                    $"Baseline: {meshBefore}, Released mid-flight: {view.ReleasedMidFlightCount()}, " +
                    $"After settle: {meshAfterSettle}, After destroy: {meshAfterDestroy}. " +
                    "ReleaseTile must remove evicted tiles from _loaded so PumpPending excludes them " +
                    "from the next iteration snapshot — ConsumeMeshBuild must never be called for " +
                    "them. OnDestroy must destroy all remaining tile Meshes. Zero orphaned Mesh required.");
            }
            finally
            {
                // Unconditional, before any dispose — an early failure (before Teardown() completes it
                // transitively) leaves nothing else to complete this held handle.
                gate[0] = 1;
                delayHandle.Complete();
                if (go != null)
                {
                    view.Teardown();
                    Object.DestroyImmediate(go);
                }
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        // ── NativeArray positive control: a deliberate leak produces non-zero counter ─────────────

        /// <summary>
        /// Non-vacuous positive control: allocate a <see cref="StyledFillTileBuilder.LayerMeshData"/>
        /// (backed by NativeArrays) via <see cref="StyledFillTileBuilder.BuildMeshData"/> and do NOT
        /// dispose it. Asserts <see cref="MeshDataPayload.DebugLiveAllocCount"/> is
        /// non-zero, proving the counter has teeth — an intentionally-leaked NativeArray is detected.
        ///
        /// The payload is disposed at test end so it does not pollute subsequent tests.
        /// </summary>
        [Test]
        public void NativeArray_PositiveControl_LeakedAlloc_CounterNonZero()
        {
            byte[] bytes    = SampleTileFixture.Bytes();
            var    tileId   = new TileId { Z = 0, X = 0, Y = 0 };
            using var mvtTile = MvtDecoder.Decode(tileId, bytes);
            var style       = MinimalStyle();
            var fillLayer   = style.Layers[0];
            var paint       = ((Fill.StyleLayer)fillLayer).Paint;
            var mvtLayer    = SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);

            Assert.IsNotNull(mvtLayer, "Fixture must contain a resolvable MVT layer");
            var features    = TestTileMeshBuilder.Select(fillLayer, mvtLayer, 0.0);
            Assert.Greater(features.Count, 0, "Fixture must produce at least one feature");

            var (bMin, _) = new TileId { Z = 0, X = 0, Y = 0 }.MercatorBounds();
            var tileOrigin = new double2(bMin.x, bMin.y);

            long countBefore = MeshDataPayload.DebugLiveAllocCount;

            // Allocate a tracked writable array + write real geometry — do NOT apply/dispose it.
            var mda = MeshDataPayload.AllocateTracked(1);
            TileGeometryBuffers geometry = mvtLayer.Geometry; // BORROWED — the tile owns it
            int vc; Bounds b;
            SyncMeshWrite.Fill(
                mda[0], features, geometry, paint, 0.0,
                new double3(tileOrigin.x, 0.0, tileOrigin.y), out vc, out b);

            Assert.Greater(vc, 0,
                "Positive control requires geometry (vertices written). " +
                "If no geometry was produced the counter test would be vacuous.");

            var leaked = new MeshDataPayload(mda, vc, b, "leak-positive-control", materialIndex: 0);

            long countAfterAlloc = MeshDataPayload.DebugLiveAllocCount;

            // Assert the counter reflects the un-disposed allocation.
            Assert.Greater(countAfterAlloc, countBefore,
                $"Positive control FAILED: DebugLiveAllocCount did not increase after AllocateTracked " +
                $"(before={countBefore}, after={countAfterAlloc}). The leak guard is vacuous — a " +
                "deliberately-leaked writable MeshDataArray must be detected. " +
                "Check that Interlocked.Increment is called in MeshDataPayload.AllocateTracked.");

            // Clean up: dispose the leaked payload so it doesn't affect subsequent tests.
            leaked.Dispose();

            long countAfterDispose = MeshDataPayload.DebugLiveAllocCount;
            Assert.AreEqual(countBefore, countAfterDispose,
                $"After explicit Dispose, counter must return to baseline " +
                $"(baseline={countBefore}, after dispose={countAfterDispose}).");
        }

        // ── Pooling MeshDataPayload must not make this counter lie ───────────────────────────────

        /// <summary>
        /// <see cref="MeshDataPayload.DebugLiveAllocCount"/> counts live NATIVE <c>MeshDataArray</c>s, not pooled
        /// wrappers. Several rent→reset→dispose cycles through <see cref="MeshDataPayloadPool"/> reuse an
        /// instance, and the counter returns to baseline after every one.
        /// </summary>
        [Test]
        public void PooledCycle_LiveAllocCounter_ReturnsToBaseline_AfterEveryDispose()
        {
            long baseline = MeshDataPayload.DebugLiveAllocCount;

            for (int i = 0; i < 5; i++)
            {
                Mesh.MeshDataArray mda = MeshDataPayload.AllocateTracked(1);
                Assert.Greater(MeshDataPayload.DebugLiveAllocCount, baseline,
                    $"iteration {i}: AllocateTracked must increment the counter regardless of pooling");

                MeshDataPayload payload = MeshDataPayloadPool.Rent();
                payload.Reset(mda, 0, default, "pool-leak-guard-probe", 0);
                payload.Dispose();

                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount,
                    $"iteration {i}: Dispose must free the tracked array and return the counter to baseline — " +
                    "pooling the wrapper instance must not change what the counter measures (live native " +
                    "arrays, not live instances).");
            }
        }

        /// <summary>
        /// The positive-control half of the same guard: a POOLED-BUT-UNDISPOSED instance must still be
        /// counted as a live leak. Guards specifically against a pooling implementation that retired the
        /// counter early — e.g. at Rent/Reset time — instead of at the point the native array is actually
        /// freed, which would make a real leak through the pooled path invisible.
        /// </summary>
        [Test]
        public void PooledInstance_UndisposedLeak_StillProducesNonZeroCounter()
        {
            long baseline = MeshDataPayload.DebugLiveAllocCount;

            Mesh.MeshDataArray mda = MeshDataPayload.AllocateTracked(1);
            MeshDataPayload leaked = MeshDataPayloadPool.Rent();
            leaked.Reset(mda, 0, default, "pool-leak-positive-control", 0);

            Assert.Greater(MeshDataPayload.DebugLiveAllocCount, baseline,
                "a pooled-but-undisposed instance must still be counted as a live leak — pooling the wrapper " +
                "must not retire the counter before the native array is actually freed.");

            // Clean up so this doesn't pollute subsequent tests.
            leaked.Dispose();
            Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount);
        }

        /// <summary>Two unfiltered fill layers over the SAME source-layer — a real, minimal multi-layer
        /// tile: <c>ConsumeMeshBuild</c>'s dense payload array gets length &gt;= 2, and (with the fixture's
        /// real geometry) both payloads are non-empty, so a per-mesh consume budget of 1 genuinely stops
        /// mid-tile rather than free-riding past an empty layer.</summary>
        private static StyleDocument TwoFillLayerStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""LeakGuardTwoLayer"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill-a"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] }
                },
                {
                    ""id"": ""countries-fill-b"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 50, 200, 50, 1] }
                }
            ]
        }");

        /// <summary>
        /// <see cref="MeshDataPayload.Dispose"/> returns the instance to <see cref="MeshDataPayloadPool"/> at once,
        /// so <c>ConsumeMeshBuild</c> nulls each slot as it disposes it; otherwise <c>DisposeWholePayloads</c>'s
        /// later sweep would free a recycled instance. With one consume per tick, the test rents between
        /// payload 0 and payload 1, standing in for a concurrent build, and its array must survive the
        /// sweep until the test disposes it.
        /// </summary>
        [Test]
        public void PooledPayload_RentedDuringAPartialConsume_SurvivesUntilThisCallerDisposesIt()
        {
            long baseline = MeshDataPayload.DebugLiveAllocCount;

            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView_PoolRaceRegression"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = TwoFillLayerStyle();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 0; // block consume — build the backlog first
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);

                // One Await completes only the current STEP; pump until ConsumeBacklog (write complete,
                // unconsumed) sees the tile.
                int started = 0;
                for (int f = 0; f < 3000; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    started += view.TileBuildsStartedLastTick();
                    if (view.LoadedTileCount() > 0 && started >= view.LoadedTileCount()) break;
                }
                Assert.AreEqual(1, view.LoadedTileCount(),
                    "precondition: a z=0 cover with a single tile keeps the whole scenario to ONE mesh-build " +
                    "result, so no other tile's payload activity can interleave with the probe below.");
                Assert.GreaterOrEqual(started, 1, "drive precondition: the tile's mesh build must have been started");

                for (int f = 0; f < 3000 && view.CaptureTelemetry().ConsumeBacklog < 1; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }
                Assert.GreaterOrEqual(view.CaptureTelemetry().ConsumeBacklog, 1,
                    "drive precondition: the tile's write step must be complete but unconsumed before the " +
                    "budgeted tick below, or there is nothing for it to partially consume.");

                // Consume exactly ONE mesh this tick. Two non-empty fill layers ⇒ ConsumeMeshBuild's budgeted
                // loop must stop after payload 0 — the tile is not yet complete.
                view.Config.MaxConsumesPerTick = 1;
                view.LateUpdate();

                var tileId = new TileId { Z = 0, X = 0, Y = 0 };
                Assert.AreEqual(1, view.MeshesConsumedLastTick(), "precondition: exactly one payload consumed this tick");
                Assert.AreEqual(0, view.TilesConsumedLastTick(), "precondition: the tile must NOT be complete yet");
                Assert.IsFalse(view.TryGetBuiltTile(tileId), "precondition: the tile must not be Built yet");

                // The probe stands in for a concurrent build's Rent()+Reset(). With one tile and one thread, it
                // receives the instance ConsumeMeshBuild just disposed.
                MeshDataPayload probe = MeshDataPayloadPool.Rent();
                // Non-vacuity: Dispose()/Upload() keep VertexCount, so the recycled fill payload still reads
                // non-zero before Reset(); a zero means the probe got an unrelated stub.
                Assert.Greater(probe.VertexCount, 0,
                    "non-vacuity precondition: the rented instance must be the fill payload ConsumeMeshBuild " +
                    "just disposed, not an unrelated pooled stub — otherwise this tooth cannot discriminate " +
                    "the TileManager.cs fix at all");
                Mesh.MeshDataArray probeMda = MeshDataPayload.AllocateTracked(1);
                probe.Reset(probeMda, 0, default, "pool-race-probe", -7);

                // Relative to baseline: production frees payloads 0 and 1, and only this probe's own array
                // stays live until the test disposes it.
                long expectedAfterSettle = baseline + 1; // baseline (0 outstanding) + this probe's own array

                // Resume: the tick that completes the tile is when DisposeWholePayloads sweeps; a slot left
                // non-null would free the probe here.
                view.Config.MaxConsumesPerTick = 64;
                for (int f = 0; f < 500; f++)
                {
                    view.LateUpdate();
                    view.DrainMeshBuilds();
                    if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) break;
                }
                Assert.IsTrue(view.AllTilesSettled(), "the cover must still settle after the probe's injection");

                Assert.AreEqual(expectedAfterSettle, MeshDataPayload.DebugLiveAllocCount,
                    "a payload rented DURING a partial consume must not be silently freed by that tile's own " +
                    "completion — this would mean TileManager still held a stale reference to an " +
                    "already-recycled payload (what `payloads[slot] = null` after each Dispose() call " +
                    "in ConsumeMeshBuild exists to prevent). RED-verified by removing either " +
                    "`payloads[slot] = null;` assignment in TileManager.ConsumeMeshBuild.");

                probe.Dispose();
                Assert.AreEqual(baseline, MeshDataPayload.DebugLiveAllocCount, "no leaks after full cleanup");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── NativeArray race: mid-flight release disposes NativeArrays ─────────────────

        /// <summary>
        /// A tile released while its measure step is held (the <see cref="ReleaseMidFlight_NoOrphanedMesh"/>
        /// gate) has every native resource disposed through the pen once the step completes. A fill layer
        /// allocates no <c>MeshDataArray</c> at kick, so non-vacuity reads the SUM of three live counters with
        /// the gate closed, and all three must return to baseline.
        /// </summary>
        [Test]
        public void NativeArray_ReleaseMidFlight_NoLeakedNativeArray()
        {
            long payloadBefore = MeshDataPayload.DebugLiveAllocCount;
            long graphOutputBefore = FillGraphOutput.DebugLiveCount;
            long buildGraphBefore = TileBuildGraph.DebugLiveCount;

            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_NativeArrayLeak_Race");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            // MaxConsumesPerTick = 0: prevents consume from racing the deterministic gate below.
            view.Config.MaxConsumesPerTick = 0;
            view.Config.MaxMeshBuildsPerTick = 64;
            // Evict the WHOLE condemned cover on the pan tick — the leak assertions are over the whole
            // cover either way, and releasing all of it removes any ordering dependence.
            view.Config.MaxReleasesPerTick = 64;

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

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);

                // Pump LateUpdate ONLY — no Await, which would Complete() the held graph and burn the whole
                // spin bound (NIT 3).
                for (int f = 0; f < 3000 && view.CaptureTelemetry().GraphMeasureInFlight < 1; f++)
                    view.LateUpdate();
                DelayGateJobInstrument.WaitForStart(started);
                Assert.GreaterOrEqual(view.CaptureTelemetry().GraphMeasureInFlight, 1,
                    "drive precondition: at least one tile's measure step must be genuinely held in-flight " +
                    "before the pan, or the eviction below has nothing in-flight to race.");

                // Pan far east — while the measure step is still genuinely held (gate untouched).
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                view.LateUpdate(); // cover recompute → evicts tiles → stashes in the pen

                // Positive control: at least one tile must have been released mid-flight — deterministic
                // now, the same as ReleaseMidFlight_NoOrphanedMesh's own positive control.
                Assert.Greater(view.ReleasedMidFlightCount(), 0,
                    "Positive control: at least one tile must have been released while its measure step " +
                    "was still genuinely in-flight (the held delay job guarantees !IsStepComplete). If this " +
                    "is 0, no tile reached the graph before the pan.");

                // ── Non-vacuous holding-pen assertion ─────────────────────────────────────────────
                // Read with the gate CLOSED; do NOT call LateUpdate() here, because it drains the pen.
                long payloadHeld = MeshDataPayload.DebugLiveAllocCount;
                long graphOutputHeld = FillGraphOutput.DebugLiveCount;
                long buildGraphHeld = TileBuildGraph.DebugLiveCount;
                long heldDelta = (payloadHeld - payloadBefore) + (graphOutputHeld - graphOutputBefore) +
                                  (buildGraphHeld - buildGraphBefore);

                Assert.Greater(heldDelta, 0,
                    $"Non-vacuous leak guard (DECISIVE): the sum of the three live-count deltas " +
                    $"(payload {payloadHeld - payloadBefore}, FillGraphOutput {graphOutputHeld - graphOutputBefore}, " +
                    $"TileBuildGraph {buildGraphHeld - buildGraphBefore}) must be > 0 while the stashed tile's " +
                    "native resources sit undisposed in the pen — a fill layer allocates no MeshDataPayload at " +
                    "kick any more, so TileBuildGraph/FillGraphOutput are what a fill-only style's " +
                    "held tile actually shows live.");

                // Release the gate and restore budgets so the new cover can settle.
                gate[0] = 1;
                view.Config.MaxConsumesPerTick = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                // Pump until the cover settles; each Tick's PendingDisposalQueue.DrainCompleted() disposes the
                // released tiles' resources as their held step completes.
                PumpUntilSettled(view, maxFrames: 500);

                // Final drain: ensure all pending disposal tasks have been processed.
                // (Teardown() spins them to completion; calling it here before asserting the counters.)
                view.Teardown();
                Object.DestroyImmediate(go);
                go = null;

                long payloadAfter = MeshDataPayload.DebugLiveAllocCount;
                long graphOutputAfter = FillGraphOutput.DebugLiveCount;
                long buildGraphAfter = TileBuildGraph.DebugLiveCount;
                Assert.AreEqual(payloadBefore, payloadAfter,
                    $"DECISIVE: MeshDataPayload.DebugLiveAllocCount must return to baseline after " +
                    $"load+mid-flight-release cycle. Baseline: {payloadBefore}, after: {payloadAfter}.");
                Assert.AreEqual(graphOutputBefore, graphOutputAfter,
                    $"FillGraphOutput.DebugLiveCount must return to baseline after load+mid-flight-release " +
                    $"cycle. Baseline: {graphOutputBefore}, after: {graphOutputAfter}.");
                Assert.AreEqual(buildGraphBefore, buildGraphAfter,
                    $"TileBuildGraph.DebugLiveCount must return to baseline after load+mid-flight-release " +
                    $"cycle. Baseline: {buildGraphBefore}, after: {buildGraphAfter}. Check: (a) the pen in " +
                    "RenderTeardownRecord, (b) PendingDisposalQueue.DrainCompleted() called in Tick, (c) Teardown() spins+" +
                    "disposes pending tasks.");
            }
            finally
            {
                gate[0] = 1;
                delayHandle.Complete();
                if (go != null)
                {
                    view.Teardown();
                    Object.DestroyImmediate(go);
                }
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        // ── NativeArray consume: normal consume disposes NativeArrays ────────────────

        /// <summary>
        /// Consume-path NativeArray balance: build tiles to completion (normal consume path),
        /// then teardown. Asserts <see cref="MeshDataPayload.DebugLiveAllocCount"/>
        /// returns to baseline after the full load+destroy cycle.
        /// </summary>
        [Test]
        public void NativeArray_BuildAndRelease_NoLeakedNativeArray()
        {
            long countBefore = MeshDataPayload.DebugLiveAllocCount;

            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_NativeArrayLeak_Consume");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(), "Tiles must settle before testing NativeArray balance.");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built (real load required for meaningful leak test).");

                // ConsumeMeshBuild disposes the LayerMeshData arrays right after UploadMesh, so the counter is
                // already at baseline.
                long countAfterConsume = MeshDataPayload.DebugLiveAllocCount;
                Assert.AreEqual(countBefore, countAfterConsume,
                    $"After normal consume (ConsumeMeshBuild), NativeArray counter must equal baseline. " +
                    $"Baseline={countBefore}, After consume={countAfterConsume}. " +
                    "ConsumeMeshBuild must dispose all LayerMeshData in its finally block.");

                view.Teardown();
                Object.DestroyImmediate(go);
                go = null;

                long countAfterTeardown = MeshDataPayload.DebugLiveAllocCount;
                Assert.AreEqual(countBefore, countAfterTeardown,
                    $"After Teardown, NativeArray counter must equal baseline. " +
                    $"Baseline={countBefore}, After teardown={countAfterTeardown}.");
            }
            finally
            {
                if (go != null)
                {
                    view.Teardown();
                    Object.DestroyImmediate(go);
                }
            }
        }

        // ── Tooth 5c — DrainMeshBuilds then release — no orphaned Mesh ──────────────────────

        /// <summary>
        /// Use DrainMeshBuilds() to force settle, verify a Mesh was created (real load),
        /// then destroy the MapView and verify zero orphaned Meshes.
        ///
        /// Exercises the full pipeline: fetch → build → consume (via DrainMeshBuilds) →
        /// destroy (via OnDestroy). Both consumption paths (Tick/PumpPending and DrainMeshBuilds)
        /// are covered by <see cref="BuildAndRelease_NoOrphanedMesh"/> and this test respectively.
        /// </summary>
        [Test]
        public void DrainThenDestroy_NoOrphanedMesh()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_LeakGuard_C");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            int meshBefore = CountMeshObjects();

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);
                view.LateUpdate(); // kick fetch + mesh build

                // DrainMeshBuilds: synchronous settle — waits for mesh build UniTasks to complete.
                view.DrainMeshBuilds();
                Assert.IsTrue(view.AllTilesSettled(),
                    "DrainMeshBuilds must settle all tiles (tooth 5c positive control).");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId { Z = 0, X = 0, Y = 0 }),
                    "z0/0/0 tile must be built by DrainMeshBuilds (real load required).");

                int meshAfterLoad = CountMeshObjects();
                Assert.Greater(meshAfterLoad - meshBefore, 0,
                    "DrainMeshBuilds must produce at least one Mesh (positive control for leak check).");

                // Release: Teardown (destroys Mesh assets) then destroy the GameObject.
                // (See BuildAndRelease_NoOrphanedMesh for why Teardown() is called explicitly.)
                view.Teardown();
                Object.DestroyImmediate(go);
                go = null;

                int meshAfterDestroy = CountMeshObjects();
                Assert.LessOrEqual(meshAfterDestroy, meshBefore,
                    $"Orphaned Meshes after DrainMeshBuilds + MapView.Teardown. " +
                    $"Baseline: {meshBefore}, After drain: {meshAfterLoad}, " +
                    $"After destroy: {meshAfterDestroy}. " +
                    "All Meshes created by DrainMeshBuilds→ConsumeMeshBuild must be " +
                    "destroyed by Teardown. Zero orphaned Mesh required.");
            }
            finally
            {
                if (go != null)
                {
                    view.Teardown();
                    Object.DestroyImmediate(go);
                }
            }
        }
    }
}
