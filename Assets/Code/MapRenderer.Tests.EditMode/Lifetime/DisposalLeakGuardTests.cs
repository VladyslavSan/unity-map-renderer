// S51 Acceptance Tooth 5 — Disposal/leak guard (DECISIVE).
//
// Drives load→release of N tiles including the race (release a tile whose mesh build
// completed but wasn't consumed) and asserts zero orphaned Mesh objects.
//
// S48 extension: MeshBuildResult now holds NativeArray-backed LayerMeshData payloads.
// The NativeArray leak guard is NON-VACUOUS:
//   - MeshDataPayload.DebugLiveAllocCount tracks live allocations.
//   - A positive counter after a full cycle means NativeArrays were produced but not Disposed.
//   - A deliberately-leaked NativeArray MUST produce a non-zero counter (positive control).
//
// Meaningful assertions:
//   - Mesh delta: zero orphaned Mesh after load+release (carried over from S51).
//   - NativeArray balance: DebugLiveAllocCount == 0 after every load+release cycle.
//   - Positive control: a deliberately-leaked LayerMeshData produces DebugLiveAllocCount > 0.
//   - Race path: mid-flight-released tile's NativeArrays are disposed via _pendingDisposal.
//
// This test is Unity-only (uses MonoBehaviour, Object.FindObjectsOfTypeAll, Mesh creation,
// NativeArray). It does NOT compile in the headless dotnet-test path (excluded from core-tests.csproj).

using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Jobs;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;
namespace MapRenderer.Tests
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
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
                Thread.Sleep(1);
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

        // ── Tooth 5b: Release mid-flight — no orphaned Mesh ──────────────────────────────────

        /// <summary>
        /// The race: request tiles, kick mesh build tasks, then pan far away so tiles are
        /// released while their mesh build is still in-flight (or just completed). After
        /// the mesh build tasks complete, no Meshes must be created for the evicted tiles.
        ///
        /// This is the precise "race" described in S51 tooth 5: "release a tile whose mesh build
        /// result completed but wasn't consumed". ReleaseTile removes the tile from _loaded; the next
        /// PumpPending snapshot (foreach over _loaded) excludes the released tile, so
        /// ConsumeMeshBuild is never called for it — no Mesh is created.
        ///
        /// Positive control: the test uses MaxConsumesPerTick=0 during the eviction window to FORCE the
        /// mid-flight state deterministically. The build (fetch→kick) still runs, but the consume
        /// is gated by MaxConsumesPerTick, so the mesh build task is in-flight when the tile is evicted.
        /// After the eviction Tick, MaxConsumesPerTick is restored so the new cover can settle normally.
        /// The test asserts view.ReleasedMidFlightCount() > 0 to prove the race genuinely occurred
        /// (i.e., at least one tile had HasMeshBuild && !Built when released).
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
            // MaxConsumesPerTick = 0 BEFORE initialise: prevents the consume from running.
            // Mesh build tasks are KICKED (build) but never consumed, guaranteeing the tiles
            // are in HasMeshBuild=true, Built=false state when we evict them.
            view.Config.MaxConsumesPerTick = 0;
            view.Config.MaxMeshBuildsPerTick = 64;
            // Evict the WHOLE condemned cover on the pan tick. At the default budget of 4 the release order
            // decides whether the tiles that happen to be released are the ones whose build is still running,
            // which makes the positive control below a coin flip once fetch completion is spread out by the
            // decode. Releasing all of them removes the ordering dependence without weakening anything: the
            // leak assertions are over the whole cover either way.
            view.Config.MaxReleasesPerTick = 64;

            int meshBefore = CountMeshObjects();

            try
            {
                // Load initial cover at lon=0, z=5.
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);

                // First Tick: tiles enter cover + fetch kicks (FixtureSource sync → completes immediately).
                view.LateUpdate();
                // The fetch task carries the tile's DECODE now, so a fixed 5 ms sleep no longer guarantees
                // the fetches are observable by the next Tick — and if they are not, nothing is kicked, no
                // tile is ever in-flight, and the positive control below reads 0 for a DRIVE reason rather
                // than a behaviour one. Pump until EVERY cover tile has been kicked instead. Waiting for the
                // first kick is not enough: fetches now complete at spread-out times, so the first kick tick
                // may dispatch one or two tiles while the release budget below evicts a different, never-
                // kicked handful — and the positive control reads 0 with every build still in flight.
                // Property unchanged; only the drive is.
                int kicked = 0;
                for (int f = 0; f < 3000; f++)
                {
                    Thread.Sleep(1);
                    view.LateUpdate();
                    kicked += view.MeshBuildsKickedLastTick();
                    if (view.LoadedTileCount() > 0 && kicked >= view.LoadedTileCount()) break;
                }
                Assert.GreaterOrEqual(kicked, view.LoadedTileCount(),
                    "drive precondition: every cover tile's mesh build must have been kicked before the pan, " +
                    "or the eviction has no in-flight build to race");

                // Pan far east — before mesh build results are consumed.
                // MaxConsumesPerTick=0 guarantees tiles are still in-flight (HasMeshBuild && !Built).
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                view.LateUpdate(); // cover recompute → evicts original tiles while they are in-flight

                // ── Positive control: at least one tile must have been released mid-flight ──────
                // This is the decisive assertion that separates "race exercised" from "vacuous pass".
                // ReleasedMidFlightCount is incremented in ReleaseTile when HasMeshBuild &&
                // !Built — meaning the mesh build was in-flight at the moment of release.
                Assert.Greater(view.ReleasedMidFlightCount(), 0,
                    "Positive control: at least one tile must have been released while its mesh build " +
                    "was still in-flight (HasMeshBuild && !Built at the time of ReleaseTile). " +
                    "If this is 0, the race did not occur — MaxConsumesPerTick=0 did not prevent consumption, " +
                    "or no tiles had mesh build tasks by the eviction Tick. The leak-guard assertion " +
                    "would be vacuously true (nothing was built → nothing to orphan).");

                // Restore MaxConsumesPerTick so the new cover settles normally.
                view.Config.MaxConsumesPerTick = 64;
                view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

                // Let the mesh build UniTasks for the evicted tiles complete on the ThreadPool.
                // Then pump the new cover until it settles.
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
                if (go != null)
                {
                    view.Teardown();
                    Object.DestroyImmediate(go);
                }
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
            var paint       = new Fill.PaintProperties(fillLayer);
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
            StyledFillTileBuilder.WriteMeshData(
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

        // ── Tooth 5-NativeArray-Race: mid-flight release disposes NativeArrays ─────────────────

        /// <summary>
        /// S48 DECISIVE race test: a tile released mid-flight (mesh build in-flight, NativeArrays
        /// not yet produced at release time) must have its NativeArrays disposed via the
        /// <c>_pendingDisposal</c> holding pen after the mesh build task completes.
        ///
        /// Asserts <see cref="MeshDataPayload.DebugLiveAllocCount"/> returns to
        /// baseline after the full race cycle, proving no NativeArray leak.
        /// </summary>
        [Test]
        public void NativeArray_ReleaseMidFlight_NoLeakedNativeArray()
        {
            long countBefore = MeshDataPayload.DebugLiveAllocCount;

            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_NativeArrayLeak_Race");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            // MaxConsumesPerTick = 0: prevents the consume — mesh build tasks are kicked but
            // not consumed, ensuring HasMeshBuild=true when tiles are evicted.
            view.Config.MaxConsumesPerTick = 0;
            view.Config.MaxMeshBuildsPerTick = 64;
            // Evict the WHOLE condemned cover on the pan tick. At the default budget of 4 the release order
            // decides whether the tiles that happen to be released are the ones whose build is still running,
            // which makes the positive control below a coin flip once fetch completion is spread out by the
            // decode. Releasing all of them removes the ordering dependence without weakening anything: the
            // leak assertions are over the whole cover either way.
            view.Config.MaxReleasesPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);

                // First Tick: tiles enter cover + fetch kicks (FixtureSource sync → immediate).
                view.LateUpdate();
                // The fetch task carries the tile's DECODE now, so a fixed 5 ms sleep no longer guarantees
                // the fetches are observable by the next Tick — and if they are not, nothing is kicked, no
                // tile is ever in-flight, and the positive control below reads 0 for a DRIVE reason rather
                // than a behaviour one. Pump until EVERY cover tile has been kicked instead. Waiting for the
                // first kick is not enough: fetches now complete at spread-out times, so the first kick tick
                // may dispatch one or two tiles while the release budget below evicts a different, never-
                // kicked handful — and the positive control reads 0 with every build still in flight.
                // Property unchanged; only the drive is.
                int kicked = 0;
                for (int f = 0; f < 3000; f++)
                {
                    Thread.Sleep(1);
                    view.LateUpdate();
                    kicked += view.MeshBuildsKickedLastTick();
                    if (view.LoadedTileCount() > 0 && kicked >= view.LoadedTileCount()) break;
                }
                Assert.GreaterOrEqual(kicked, view.LoadedTileCount(),
                    "drive precondition: every cover tile's mesh build must have been kicked before the pan, " +
                    "or the eviction has no in-flight build to race");

                // Pan far east — evicting the original tiles while mesh build is in-flight.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                view.LateUpdate(); // cover recompute → evicts tiles → stashes in _pendingDisposal

                // Positive control: at least one tile must have been released mid-flight.
                Assert.Greater(view.ReleasedMidFlightCount(), 0,
                    "Positive control: at least one tile must have been released while its mesh build " +
                    "was still in-flight. If this is 0, the race did not occur and the NativeArray balance " +
                    "assertion would be vacuously true.");

                // ── Non-vacuous holding-pen assertion (DECISIVE — acceptance tooth #4) ────────────
                // Spin on the ThreadPool until the stashed mesh build task completes and allocates
                // its NativeArray payload (Interlocked.Increment inside BuildMeshData, line 318 of
                // StyledFillTileBuilder). While the payload sits undisposed in _pendingDisposal,
                // DebugLiveAllocCount must be > countBefore — proving a real allocation passed
                // through the holding pen.
                //
                // CRITICAL: do NOT call Tick() here. Tick() calls DrainPendingDisposal() which
                // would dispose the payload and decrement the counter before we can observe it —
                // defeating the purpose of this assertion.
                //
                // The spin mirrors the DrainMeshBuilds / Teardown patterns (Thread.Sleep(1) to
                // yield real CPU time to the ThreadPool; bounded by spins < 10000 to avoid infinite
                // wait on unexpected failure).
                long held = countBefore;
                {
                    int spins = 0;
                    while ((held = MeshDataPayload.DebugLiveAllocCount) <= countBefore
                           && spins++ < 10000)
                        Thread.Sleep(1);
                }

                Assert.Greater(held, countBefore,
                    $"Non-vacuous leak guard (DECISIVE): DebugLiveAllocCount must be > countBefore " +
                    $"(baseline={countBefore}, held={held}) while the stashed mesh build payload sits " +
                    "undisposed in _pendingDisposal. This proves a real NativeArray allocation passed " +
                    "through the holding pen — deleting _pendingDisposal.Add in ReleaseTile or the " +
                    "disposal in DrainPendingDisposal would not make this assertion vacuous. " +
                    "If this fails with held==countBefore, the z5 fixture produced no geometry " +
                    "(confirm NativeArray_PositiveControl_LeakedAlloc_CounterNonZero still passes).");

                // Restore MaxConsumesPerTick so the new cover can settle.
                view.Config.MaxConsumesPerTick = 64;
                view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

                // Let the ThreadPool mesh build tasks complete, then pump until the new cover settles.
                // DrainPendingDisposal() is called inside each Tick — released tiles' NativeArrays are
                // disposed as their tasks complete.
                PumpUntilSettled(view, maxFrames: 500);

                // Final drain: ensure all pending disposal tasks have been processed.
                // (Teardown() spins them to completion; calling it here before asserting the counter.)
                view.Teardown();
                Object.DestroyImmediate(go);
                go = null;

                long countAfter = MeshDataPayload.DebugLiveAllocCount;
                Assert.AreEqual(countBefore, countAfter,
                    $"S48 DECISIVE: DebugLiveAllocCount must return to baseline after load+mid-flight-release cycle. " +
                    $"Baseline: {countBefore}, After cycle: {countAfter}. " +
                    $"Delta of {countAfter - countBefore} means {countAfter - countBefore} LayerMeshData payload(s) " +
                    "were allocated but not Disposed. Check: (a) _pendingDisposal holding pen in ReleaseTile, " +
                    "(b) DrainPendingDisposal() called in Tick, (c) Teardown() spins+disposes pending tasks.");
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
