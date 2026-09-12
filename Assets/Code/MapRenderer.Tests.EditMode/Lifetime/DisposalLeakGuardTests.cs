// S51 Acceptance Tooth 5 — Disposal/leak guard (DECISIVE).
//
// Drives load→release of N tiles including the race (release a tile whose mesh build
// completed but wasn't consumed) and asserts zero orphaned Mesh objects.
//
// S48 extension: a tile's mesh build settles into a dense MeshDataPayload[], each slot a
// NativeArray-backed payload.
// The NativeArray leak guard is NON-VACUOUS:
//   - MeshDataPayload.DebugLiveAllocCount tracks live allocations.
//   - A positive counter after a full cycle means NativeArrays were produced but not Disposed.
//   - A deliberately-leaked NativeArray MUST produce a non-zero counter (positive control).
//
// Meaningful assertions:
//   - Mesh delta: zero orphaned Mesh after load+release (carried over from S51).
//   - NativeArray balance: DebugLiveAllocCount == 0 after every load+release cycle.
//   - Positive control: a deliberately-leaked LayerMeshData produces DebugLiveAllocCount > 0.
//   - Race path: mid-flight-released tile's NativeArrays are disposed via PendingDisposalQueue.
//
// This test is Unity-only (uses MonoBehaviour, Object.FindObjectsOfTypeAll, Mesh creation,
// NativeArray). It does NOT compile in the headless dotnet-test path (excluded from core-tests.csproj).

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
using MapRenderer.Core.View.Camera;
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
    /// S51 acceptance tooth 5: disposal/leak guard for load→release race.
    ///
    /// Tests that:
    ///   (a) A tile built normally (fetch + build + consume) and then released destroys its Mesh.
    ///   (b) A tile released mid-flight (mesh build started, not yet consumed) leaves zero orphaned Mesh
    ///       after the mesh build UniTask completes.
    ///   (c) Zero net Mesh objects after the full cycle (meshCountBefore == meshCountAfter for the
    ///       tile-owned Meshes).
    /// </summary>
    [TestFixture]
    public class DisposalLeakGuardTests
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

        // ── Tooth 5a: Build then release — no orphaned Mesh ───────────────────────────────────

        /// <summary>
        /// Build N tiles to completion, record Mesh count delta, then destroy the MapView.
        /// After destruction, mesh count must not have increased (all Meshes disposed by OnDestroy).
        ///
        /// This is a real load then a real release (tooth 5 requirement: "must exercise a real
        /// load then a real release").
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

            // Release: teardown must destroy all tile Meshes, then destroy the GameObject.
            //
            // NOTE: In Unity EditMode (no [ExecuteAlways] attribute on MapView), MonoBehaviour.OnDestroy
            // is NOT triggered when Object.DestroyImmediate(go) is called from a headless test.
            // The production cleanup contract lives in MapView.Teardown(), which OnDestroy delegates to
            // in Play mode. Here we call Teardown() explicitly before DestroyImmediate so the test
            // exercises the real cleanup path that production code relies on.
            view.Teardown();
            Object.DestroyImmediate(go);

            int meshAfterDestroy = CountMeshObjects();

            // After Teardown, mesh count must return to (at most) the baseline.
            // We allow meshAfterDestroy == meshBefore (all created Meshes destroyed).
            // The assertion is: no Meshes orphaned — count must not exceed baseline.
            Assert.LessOrEqual(meshAfterDestroy, meshBefore,
                $"Orphaned Meshes detected after MapView.Teardown. " +
                $"Baseline: {meshBefore}, After load: {meshAfterLoad} (+{createdCount}), " +
                $"After destroy: {meshAfterDestroy}. " +
                "Teardown must explicitly destroy all tile Mesh assets (Unity does not do so when " +
                "the containing GameObject is destroyed). Zero orphaned Mesh after real load + full release.");
        }

        // ── Play-mode Stop race: teardown after the Entities World was disposed first ─────────
        //
        // The real MapDemo Stop leak. On Play-mode Stop, Unity disposes the Entities World (this backend's
        // MapEntitiesWorld) BEFORE MapViewComponent.OnDestroy runs. The record-teardown loop then called into
        // the backend's RemoveItems, which touched the deallocated EntityManager and threw straight out of
        // DoDispose — stranding _prepared, the backend, the pipelines, and (via MapView.Teardown)
        // Layers/SymbolPlacementSystem/Symbols: the whole-graph "finalized without Dispose()" flood, independent of whether
        // any tile was still loading (which is why an idle Stop leaked too). This drives the exact ordering by
        // disposing the World out from under the live map, then asserts Teardown runs to completion.
        //
        // Unlike the mid-flight teardown tooth (which cannot reproduce the symptom headlessly — Teardown
        // always ran to completion there), this one DOES: the trigger is a dead World, reproducible directly.
        // RED-verify: (1) delete RemoveItems' _world.IsCreated guard, keep MapView.Teardown's per-subsystem
        // catch → DoDispose dies at RemoveItems before DestroyTrackedMeshes / _prepared / _instanced /
        // pipelines; the catch swallows the throw so DoesNotThrow still passes, but the Mesh-baseline and the
        // VerifiedDisposable-leak assertions go RED (tile meshes stranded; PreparedTileCache, the backend, the
        // scheduler and pipelines never disposed). (2) additionally delete MapView.Teardown's catch → Teardown
        // itself throws and DoesNotThrow goes RED (Layers/SymbolPlacementSystem/Symbols strand too — the original flood).

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

        // ── Tooth 5b: Release mid-flight — no orphaned Mesh ──────────────────────────────────

        /// <summary>
        /// The race: request tiles, hold their mesh build's measure step genuinely in-flight on a gated
        /// delay job, then pan far away so tiles are released while still held. After the delay job is
        /// released, no Meshes must be created for the evicted tiles.
        ///
        /// This is the precise "race" described in S51 tooth 5: "release a tile whose mesh build
        /// result completed but wasn't consumed". ReleaseTile removes the tile from _loaded; the next
        /// PumpPending snapshot (foreach over _loaded) excludes the released tile, so
        /// ConsumeMeshBuild is never called for it — no Mesh is created.
        ///
        /// <para><b>Before/after (job-scheduling-design.md §8 stage 3, R1a).</b> Before: mid-flight by
        /// TIMING — a kick-count loop, then pan, racing however fast the seam's managed build happened to
        /// run. After: DETERMINISTIC. Source tiles now reach the graph's MEASURE step too (via the
        /// prologue-complete hand-off), so <see cref="TileManager.GraphDepsForTest"/> — the same
        /// production-legitimate deps-parameter seam stage 2 introduced for background tiles — can hold a
        /// SOURCE tile's measure step genuinely in-flight. The drive pumps <c>LateUpdate()</c> ONLY (no
        /// <c>Await</c> — it would <c>Complete()</c> the held graph and burn the whole spin bound, NIT 3)
        /// until <c>GraphMeasureInFlight &gt;= 1</c>, asserted as the drive precondition, THEN pans. Because
        /// the gate is still closed at that point, the graph provably cannot have completed — the positive
        /// control below is now guaranteed by construction, not a coin flip against machine speed.</para>
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
            // Declared here, not inside try — an early failure must still be able to Complete() this in
            // finally, unconditionally, before disposing gate/started/outVals (mirrors
            // TileManagerBackgroundRegistrationTests' own delay-job discipline).
            JobHandle delayHandle = default;

            try
            {
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                // Load initial cover at lon=0, z=5.
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);

                // Pump LateUpdate ONLY — no Await, which would Complete() the held graph. The tile's own
                // prologue (a managed IWorkScheduler body, not a job) still runs and hands off to
                // ScheduleMeasure; from there the delay job's Gate blocks every downstream measure job.
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
                // Deterministic now: the gate is still closed, so the released tile's graph provably
                // cannot have completed — ReleasedMidFlightCount() > 0 is guaranteed, not raced.
                Assert.Greater(view.ReleasedMidFlightCount(), 0,
                    "Positive control: at least one tile must have been released while its measure step " +
                    "was still genuinely in-flight (the held delay job guarantees !IsStepComplete at " +
                    "release time). If this is 0, no tile reached the graph before the pan.");

                // Release the gate, restore budgets, and let the new cover settle normally.
                gate[0] = 1;
                view.Config.MaxConsumesPerTick = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                PumpUntilSettled(view, maxFrames: 500);

                // The original tiles were evicted before mesh build was consumed.
                // ReleaseTile removed them from _loaded. PumpPending iterates _loaded each Tick,
                // so the evicted tiles are absent from every subsequent snapshot — ConsumeMeshBuild
                // is never called for them. → No Mesh was created for the evicted tiles.
                // → Only the new cover tiles (if any) created Meshes.
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

        // ── Tooth 5-NativeArray-Positive: deliberate leak produces non-zero counter ─────────────

        /// <summary>
        /// S48 Non-vacuous positive control: deliberately allocate a <see cref="StyledFillTileBuilder.LayerMeshData"/>
        /// (backed by NativeArrays) via <see cref="StyledFillTileBuilder.BuildMeshData"/> and do NOT
        /// dispose it. Asserts <see cref="MeshDataPayload.DebugLiveAllocCount"/> is
        /// non-zero, proving the counter has teeth — a deliberately-leaked NativeArray is detected.
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

            // Allocate a tracked writable array + write real geometry — deliberately do NOT apply/dispose.
            var mda = MeshDataPayload.AllocateTracked(1);
            TileGeometryBuffers geometry = mvtLayer.Geometry; // BORROWED (IR C1 P3) — the tile owns it
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
                $"S48 positive control FAILED: DebugLiveAllocCount did not increase after AllocateTracked " +
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

        // ── perf/gc-elimination Stage A: pooling MeshDataPayload must not make this counter lie ──

        /// <summary>
        /// Leak-guard regression for meshing follow-ups Stage A (pooling the payload WRAPPER):
        /// <see cref="MeshDataPayload.DebugLiveAllocCount"/> counts live NATIVE <c>MeshDataArray</c>s, not
        /// live wrapper instances — pooling the wrapper must not change that. Runs several rent→reset→dispose
        /// cycles (through <see cref="MeshDataPayloadPool"/> directly, so a reused — not freshly-minted —
        /// instance is exercised on the later iterations) and asserts the counter returns to baseline after
        /// every one.
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
        /// perf/gc-elimination Stage A — the deterministic, single-threaded regression tooth for the
        /// <c>TileManager.cs</c> fix discovered while pooling <see cref="MeshDataPayload"/> (outside the
        /// original plan's file scope; see the Stage A dev report). Pooling means <see cref="MeshDataPayload.Dispose"/>
        /// hands its instance back to a shared <see cref="MeshDataPayloadPool"/> the moment it runs — so once
        /// <c>ConsumeMeshBuild</c>'s per-payload loop disposes a slot, that slot's OLD reference is no longer
        /// safe for anything to touch again: a subsequent <c>Rent()</c> (by this test, standing in for a
        /// concurrent build) can receive and <c>Reset()</c> it before <c>DisposeWholePayloads</c>'s later
        /// unconditional sweep — over the SAME dense payload array (job-scheduling-design.md §8 stage 3: the
        /// array <c>TileBuildGraph.CompleteWriteAndTakePayloads</c> hands back) — would otherwise reach
        /// it a second time. <c>TileManager.ConsumeMeshBuild</c> now nulls each slot the instant it disposes
        /// it, specifically so that sweep can never touch a recycled instance.
        ///
        /// <para>Drive: a single tile, two non-empty fill layers, consume throttled to exactly one mesh per
        /// tick (<c>MaxConsumesPerTick = 1</c>) so the tile's OWN <c>LateUpdate()</c> stops mid-array —
        /// after disposing payload 0 but before payload 1, i.e. strictly before the unconditional sweep
        /// runs for this tile. At that exact point this test rents from the shared pool (standing in for a
        /// concurrent build) and marks the rented instance as its own. The cover is then allowed to finish
        /// settling. Pre-fix, the stale slot reference would let the finishing sweep silently free this
        /// test's array out from under it; post-fix the slot is null and the sweep skips it — this test's
        /// array survives until the test itself disposes it.</para>
        /// </summary>
        [Test]
        public void PooledPayload_RentedDuringAPartialConsume_SurvivesUntilThisCallerDisposesIt()
        {
            long baseline = MeshDataPayload.DebugLiveAllocCount;

            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_PoolRaceRegression");
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

                // A single Await only completes whichever STEP is currently in flight — this tile needs its
                // prologue-complete tick AND its write-kick tick before it is consumable, so the drive pumps
                // until ConsumeBacklog (write complete, unconsumed) sees it, not just until it was started.
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

                // The probe: rent from the shared pool right here, standing in for a concurrent build's
                // Rent()+Reset(). Single-threaded + nothing else touches this pool during the tick above (the
                // single-tile precondition rules out another tile's payload disposal interleaving), so this
                // reliably receives the instance ConsumeMeshBuild's per-payload loop just disposed.
                MeshDataPayload probe = MeshDataPayloadPool.Rent();
                // Non-vacuity: Dispose()/Upload() never clear VertexCount, so if this Rent() really did
                // receive the instance ConsumeMeshBuild's per-payload loop just disposed (a real fill layer
                // over the fixture), it still carries that layer's non-zero vertex count here — BEFORE this
                // test's own Reset() below overwrites it. A zero here means the probe missed (an unrelated,
                // freshly-minted or already-Reset stub), which would make the assertion below vacuous.
                Assert.Greater(probe.VertexCount, 0,
                    "non-vacuity precondition: the rented instance must be the fill payload ConsumeMeshBuild " +
                    "just disposed, not an unrelated pooled stub — otherwise this tooth cannot discriminate " +
                    "the TileManager.cs fix at all");
                Mesh.MeshDataArray probeMda = MeshDataPayload.AllocateTracked(1);
                probe.Reset(probeMda, 0, default, "pool-race-probe", -7);

                // The remaining, still-pending layer (payload 1) has its OWN legitimate array, freed by its
                // OWN Upload/Dispose when the resumed pump below consumes it — that decrement is expected
                // and is NOT what this tooth is probing for. The discriminating expectation is relative to
                // baseline, not to the count right after Reset(): once the resumed pump finishes, exactly
                // TWO arrays should have been freed by production code (payload 0's, already counted above,
                // and payload 1's, about to happen) and ONE should remain live — this probe's own, which
                // nothing in production may touch until THIS test calls Dispose() on it.
                long expectedAfterSettle = baseline + 1; // baseline (0 outstanding) + this probe's own array

                // Resume: finish this tile's remaining payload and let the cover settle — the tick that
                // completes the tile is exactly when a pre-fix TileManager would sweep the (still-referenced)
                // first slot a second time via DisposeWholePayloads.
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
                Object.DestroyImmediate(go);
            }
        }

        // ── Tooth 5-NativeArray-Race: mid-flight release disposes NativeArrays ─────────────────

        /// <summary>
        /// S48 DECISIVE race test: a tile released mid-flight (measure step genuinely held in-flight,
        /// nothing consumed) must have every native resource it holds disposed via the pen after the held
        /// step completes.
        ///
        /// <para><b>Before/after (job-scheduling-design.md §8 stage 3, R1a).</b> Before: mid-flight by
        /// TIMING (kick-count loop then pan), and the single non-vacuity reading was
        /// <c>MeshDataPayload.DebugLiveAllocCount</c> — bumped synchronously at kick under the OLD seam
        /// model. After: DETERMINISTIC, via the same <c>GraphDepsForTest</c> gate as
        /// <see cref="ReleaseMidFlight_NoOrphanedMesh"/> (see that tooth's doc for why source tiles can be
        /// held this way now). The single-counter reading no longer works UNCHANGED: a fill layer allocates
        /// NO <c>MeshDataArray</c> at kick any more (stall #5 — it is the graph arm), so for a fill-only
        /// style <c>MeshDataPayload.DebugLiveAllocCount</c> alone would sit at baseline throughout and the
        /// old assertion would be vacuously true for the wrong reason. The non-vacuity reading is now the
        /// SUM of three deltas — <c>MeshDataPayload.DebugLiveAllocCount</c>, <c>FillGraphOutput.DebugLiveCount</c>,
        /// <c>TileBuildGraph.DebugLiveCount</c> — read causally (while the gate is still closed, not
        /// hopefully after a race), and all three must return to baseline once the pen drains.</para>
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

                // ── Non-vacuous holding-pen assertion (DECISIVE — acceptance tooth #4) ────────────
                // Causal, not hopeful: read while the gate is STILL CLOSED, so the evicted tile's native
                // resources provably cannot have been disposed yet — CRITICAL: do NOT call LateUpdate()
                // here, it would drain the pen and defeat this assertion.
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
                    "kick any more (stall #5), so TileBuildGraph/FillGraphOutput are what a fill-only style's " +
                    "held tile actually shows live.");

                // Release the gate and restore budgets so the new cover can settle.
                gate[0] = 1;
                view.Config.MaxConsumesPerTick = 64;
                view.Config.MaxMeshBuildsPerTick = 64;

                // Let the released graph complete, then pump until the new cover settles.
                // PendingDisposalQueue.DrainCompleted() is called inside each Tick — released tiles' native resources are
                // disposed as their held step completes.
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
                    $"S48 DECISIVE: MeshDataPayload.DebugLiveAllocCount must return to baseline after " +
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

        // ── Tooth 5-NativeArray-Consume: normal consume disposes NativeArrays ────────────────

        /// <summary>
        /// S48 consume-path NativeArray balance: build tiles to completion (normal consume path),
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

                // At this point: LayerMeshData NativeArrays were allocated (in BuildMeshData) and
                // should have been disposed (in ConsumeMeshBuild's finally block after UploadMesh).
                // Counter must already be at baseline (consume-path disposes immediately after upload).
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

        // ── Tooth 5c: DrainMeshBuilds then release — no orphaned Mesh ──────────────────────

        /// <summary>
        /// Use DrainMeshBuilds() to force settle, verify a Mesh was created (real load),
        /// then destroy the MapView and verify zero orphaned Meshes.
        ///
        /// Exercises the full pipeline: fetch → build → consume (via DrainMeshBuilds) →
        /// destroy (via OnDestroy). Both consumption paths (Tick/PumpPending and DrainMeshBuilds)
        /// are covered by Tooth5a and Tooth5c respectively.
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
