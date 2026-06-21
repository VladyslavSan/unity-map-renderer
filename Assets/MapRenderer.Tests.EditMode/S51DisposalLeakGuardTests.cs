// S51 Acceptance Tooth 5 — Disposal/leak guard (DECISIVE).
//
// Drives load→release of N tiles including the race (release a tile whose tessellation
// completed but wasn't consumed) and asserts zero orphaned Mesh objects.
//
// TessellationResult holds only managed arrays (no NativeArray today), so NativeArray
// leak detection is vacuously zero. The meaningful assertion is zero orphaned Mesh:
//   - count Mesh objects before + after, assert delta == 0.
//   - A tile released mid-flight MUST NOT leave a Mesh behind (ReleaseTile removes the tile from
//     _loaded; PumpPendingBuilds iterates _loaded, so the released tile is absent from the next
//     snapshot and ConsumeTessellationTask is never reached for it — no Mesh created).
//   - A tile built then released MUST have its Mesh destroyed.
//
// This test is Unity-only (uses MonoBehaviour, Object.FindObjectsOfTypeAll, Mesh creation).
// It does NOT compile in the headless dotnet-test path (excluded from core-tests.csproj).

using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S51 acceptance tooth 5: disposal/leak guard for load→release race.
    ///
    /// Tests that:
    ///   (a) A tile built normally (fetch + tessellate + consume) and then released destroys its Mesh.
    ///   (b) A tile released mid-flight (tessellation started, not yet consumed) leaves zero orphaned Mesh
    ///       after the tessellation UniTask completes.
    ///   (c) Zero net Mesh objects after the full cycle (meshCountBefore == meshCountAfter for the
    ///       tile-owned Meshes).
    /// </summary>
    [TestFixture]
    public class S51DisposalLeakGuardTests
    {
        private static byte[] FixtureBytes()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            Assert.IsTrue(File.Exists(path), $"Fixture missing: {path}");
            return File.ReadAllBytes(path);
        }

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new LookAtPoint(lon, lat, 0), zoom, 0, 0);

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
        /// In-memory source returning the fixture bytes synchronously.
        /// S51: FetchAsync returns UniTask&lt;TileResponse&gt; (no Task).
        /// </summary>
        private sealed class FixtureSource : IDataSource
        {
            private readonly byte[] _bytes;
            public FixtureSource(byte[] bytes) { _bytes = bytes; }
            public TileEncoding Encoding => TileEncoding.Mvt;
            public UniTask<TileResponse> FetchAsync(TileId id, CancellationToken ct = default)
                => UniTask.FromResult(new TileResponse(_bytes, TileEncoding.Mvt));
            public void Dispose() { }
        }

        /// <summary>
        /// Pumps Tick until all tiles settle or maxFrames is reached.
        /// </summary>
        private static void PumpUntilSettled(MapView view, int maxFrames = 500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.Tick();
                if (view.LoadedTileCount > 0 && view.AllTilesSettled())
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
            var src   = new FixtureSource(FixtureBytes());
            var go    = new GameObject("MapView_LeakGuard_A");
            var view  = go.AddComponent<MapView>();
            var style = MinimalStyle();
            view.MinZoom = 0; view.MaxZoom = 0;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;

            // Baseline: count Meshes before the map load.
            int meshBefore = CountMeshObjects();

            view.Initialise(src, Cam(0, 0, 0.0), ownsSource: false, style: style);

            // Drive load: fetch → tessellate → consume (creates Mesh + GameObject).
            PumpUntilSettled(view);
            Assert.IsTrue(view.AllTilesSettled(), "Tiles must settle before testing leak guard.");
            Assert.IsTrue(view.TryGetBuiltTile(new TileId(0, 0, 0), out _),
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
        /// The race: request tiles, kick tessellation tasks, then pan far away so tiles are
        /// released while their tessellation is still in-flight (or just completed). After
        /// the tessellation tasks complete, no Meshes must be created for the evicted tiles.
        ///
        /// This is the precise "race" described in S51 tooth 5: "release a tile whose tessellation
        /// result completed but wasn't consumed". ReleaseTile removes the tile from _loaded; the next
        /// PumpPendingBuilds snapshot (foreach over _loaded) excludes the released tile, so
        /// ConsumeTessellationTask is never called for it — no Mesh is created.
        ///
        /// Positive control: the test uses MaxBuildsPerTick=0 during the eviction window to FORCE the
        /// mid-flight state deterministically. Phase-1 (fetch→kick) still runs, but Phase-2 (consume)
        /// is gated by MaxBuildsPerTick, so the tessellation task is in-flight when the tile is evicted.
        /// After the eviction Tick, MaxBuildsPerTick is restored so the new cover can settle normally.
        /// The test asserts view.ReleasedMidFlightCount > 0 to prove the race genuinely occurred
        /// (i.e., at least one tile had HasTessellationTask && !Built when released).
        /// </summary>
        [Test]
        public void ReleaseMidFlight_NoOrphanedMesh()
        {
            var src   = new FixtureSource(FixtureBytes());
            var go    = new GameObject("MapView_LeakGuard_B");
            var view  = go.AddComponent<MapView>();
            var style = MinimalStyle();
            view.MinZoom = 5; view.MaxZoom = 5;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            // MaxBuildsPerTick = 0 BEFORE initialise: prevents Phase-2 (consume) from running.
            // Tessellation tasks are KICKED (Phase-1) but never consumed, guaranteeing the tiles
            // are in HasTessellationTask=true, Built=false state when we evict them.
            view.MaxBuildsPerTick = 0;

            int meshBefore = CountMeshObjects();

            try
            {
                // Load initial cover at lon=0, z=5.
                view.Initialise(src, Cam(0, 0, 5.0), ownsSource: false, style: style);

                // First Tick: tiles enter cover + fetch kicks (FixtureSource sync → completes immediately).
                view.Tick();
                // Brief sleep so ThreadPool tessellation tasks can start (MaxBuildsPerTick=0 won't consume them).
                Thread.Sleep(5);
                // Second Tick: fetch complete → tessellation tasks are kicked (Phase-1). Still not consumed.
                view.Tick();

                // Pan far east — before tessellation results are consumed.
                // MaxBuildsPerTick=0 guarantees tiles are still in-flight (HasTessellationTask && !Built).
                view.Camera.Apply(new CameraPropertiesUpdate { Lon = 170 }, CameraAnimation.Instant);
                view.Tick(); // cover recompute → evicts original tiles while they are in-flight

                // ── Positive control: at least one tile must have been released mid-flight ──────
                // This is the decisive assertion that separates "race exercised" from "vacuous pass".
                // ReleasedMidFlightCount is incremented in ReleaseTile when HasTessellationTask &&
                // !Built — meaning the tessellation was in-flight at the moment of release.
                Assert.Greater(view.ReleasedMidFlightCount, 0,
                    "Positive control: at least one tile must have been released while its tessellation " +
                    "was still in-flight (HasTessellationTask && !Built at the time of ReleaseTile). " +
                    "If this is 0, the race did not occur — MaxBuildsPerTick=0 did not prevent consumption, " +
                    "or no tiles had tessellation tasks by the eviction Tick. The leak-guard assertion " +
                    "would be vacuously true (nothing was built → nothing to orphan).");

                // Restore MaxBuildsPerTick so the new cover settles normally.
                view.MaxBuildsPerTick = 64;

                // Let the tessellation UniTasks for the evicted tiles complete on the ThreadPool.
                // Then pump the new cover until it settles.
                PumpUntilSettled(view, maxFrames: 500);

                // The original tiles were evicted before tessellation was consumed.
                // ReleaseTile removed them from _loaded. PumpPendingBuilds iterates _loaded each Tick,
                // so the evicted tiles are absent from every subsequent snapshot — ConsumeTessellationTask
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
                    $"Baseline: {meshBefore}, Released mid-flight: {view.ReleasedMidFlightCount}, " +
                    $"After settle: {meshAfterSettle}, After destroy: {meshAfterDestroy}. " +
                    "ReleaseTile must remove evicted tiles from _loaded so PumpPendingBuilds excludes them " +
                    "from the next iteration snapshot — ConsumeTessellationTask must never be called for " +
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

        // ── Tooth 5c: DrainTessellation then release — no orphaned Mesh ──────────────────────

        /// <summary>
        /// Use DrainTessellation() to force settle, verify a Mesh was created (real load),
        /// then destroy the MapView and verify zero orphaned Meshes.
        ///
        /// Exercises the full pipeline: fetch → tessellate → consume (via DrainTessellation) →
        /// destroy (via OnDestroy). Both consumption paths (Tick/PumpPendingBuilds and DrainTessellation)
        /// are covered by Tooth5a and Tooth5c respectively.
        /// </summary>
        [Test]
        public void DrainThenDestroy_NoOrphanedMesh()
        {
            var src   = new FixtureSource(FixtureBytes());
            var go    = new GameObject("MapView_LeakGuard_C");
            var view  = go.AddComponent<MapView>();
            var style = MinimalStyle();
            view.MinZoom = 0; view.MaxZoom = 0;
            view.PadFactor = 1f; view.ViewportAspect = 1f;
            view.MaxBuildsPerTick = 64;

            int meshBefore = CountMeshObjects();

            try
            {
                view.Initialise(src, Cam(0, 0, 0.0), ownsSource: false, style: style);
                view.Tick(); // kick fetch + tessellation

                // DrainTessellation: synchronous settle — waits for tessellation UniTasks to complete.
                view.DrainTessellation();
                Assert.IsTrue(view.AllTilesSettled(),
                    "DrainTessellation must settle all tiles (tooth 5c positive control).");
                Assert.IsTrue(view.TryGetBuiltTile(new TileId(0, 0, 0), out _),
                    "z0/0/0 tile must be built by DrainTessellation (real load required).");

                int meshAfterLoad = CountMeshObjects();
                Assert.Greater(meshAfterLoad - meshBefore, 0,
                    "DrainTessellation must produce at least one Mesh (positive control for leak check).");

                // Release: Teardown (destroys Mesh assets) then destroy the GameObject.
                // (See BuildAndRelease_NoOrphanedMesh for why Teardown() is called explicitly.)
                view.Teardown();
                Object.DestroyImmediate(go);
                go = null;

                int meshAfterDestroy = CountMeshObjects();
                Assert.LessOrEqual(meshAfterDestroy, meshBefore,
                    $"Orphaned Meshes after DrainTessellation + MapView.Teardown. " +
                    $"Baseline: {meshBefore}, After drain: {meshAfterLoad}, " +
                    $"After destroy: {meshAfterDestroy}. " +
                    "All Meshes created by DrainTessellation→ConsumeTessellationTask must be " +
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
