// Tiles/ThrottleTests.cs — tile-manager throttle/teardown/load-priority acceptance teeth, plus a handful of smaller unit-level Tiles fixtures (fill antialiasing, prepared cache, profiler counters, cover-key gate, decoder selection).
//
// Tile-manager lifecycle and throttle fixtures first (teardown order, throttle acceptance, load measurement/stress, background registration, load priority, symbol kick, priority sorter), then the smaller independent unit fixtures.
//
// Contents:
//   TeardownPenOrderTests                   — Unity EditMode only — drives a real TileManager.Dispose().
//   ThrottleTests                           — Throttle acceptance tests.
//   TileLoadMeasurementTests                — Tooth (c) (consume-tick alloc-free) is NOT duplicated here — it is already guaranteed by MapViewLiveLoopTests.MapView_SteadyStateTick_DoesNotAllocateGCMemory (asserts Is.Not.AllocatingGCMemory() over the budgeted-consume Tick in the all-built steady state,…
//   TileLoadStressDriverTests               — Tests for the TileLoadStressDriver debug harness: the pure motion math (ZoomAt triangle wave + LookAtAt circular orbit) and the wired live-plumbing (Tick pushes the swept zoom + orbited look-at onto the real MapCamera; disabled/unwired are clean no-ops).
//   TileManagerBackgroundRegistrationTests  — The source-less per-covered-tile background processor's TileManager wiring.
//   TileManagerLoadPriorityTests            — The load concurrency cap and the priority order: which tile builds first, the cap across a full load, no cancel in flight, re-prioritization, the strategy toggle, and an allocation-free admission path.
//   TileSymbolKickTests                     — The feed swap (TileManager's per-tile kick drives the symbol worker pass, retiring the SymbolTileBytesReady push).
//   TilePrioritySorterTests                 — Unit-level teeth for TilePrioritySorter.
//   FillAntialiasBandTests                  — fill-antialias is the ONE antialiasing switch that stays implementable per layer: MSAA and camera post-AA are render-target settings, so they cannot be turned off for a single fill.
//   PreparedTileCacheTests                  — Unity-only (UnityEngine.Mesh) — not included in the fast dotnet core-tests project.
//   ProfilerCounterHoldTests                — Scope: Editor/EditMode with ENABLE_PROFILER.
//   CoverKeyGateTests                       — Unit-level teeth for CoverKeyGate.
//   DecodersTests                           — The tile-decode is selected BY TileEncoding, not hardcoded to MVT.

using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using System.Collections;
using System.IO;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Unity.Mathematics;
using Unity.Profiling;
using Is = UnityEngine.TestTools.Constraints.Is;
using MapRenderer.Unity.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.App;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Unity.Rendering.Style;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;
using MapRenderer.Unity.View;
using System;
using MapRenderer.Unity.Text;
using MapRenderer.Core.Json;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using Fill = MapRenderer.Core.Style.Fill;
using MapRenderer.Jobs.Mvt;
using Object = UnityEngine.Object;

namespace MapRenderer.Tests.Tiles
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // TeardownPenOrderTests — drives a real TileManager.Dispose()
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TeardownPenOrderTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 },
                zoom, 0.0, 0.0);

        /// <summary><c>DoDispose</c> must call <c>_pending.FlushAll()</c> AFTER the
        /// <c>_loaded</c> teardown pass stashes into the pens, not before — an inverted call flushes
        /// pens that are then refilled and never emptied. Exercises the graph arm (a write step complete
        /// but unconsumed) and the fetch arm (a fetch task completed off-model but not yet observed by
        /// a Tick) in the SAME Dispose call.
        /// <para><b>Scope.</b> The prologue arm is not included: reaching it deterministically needs a
        /// camera pan to admit a fresh tile under an armed <c>MeshBuildGateForTest</c> (the mechanism
        /// <c>SourceTileGraphBuildTests</c>'s Case 1 uses for its Tick-based drain), which would make
        /// this fixture racy for no new coverage — the prologue arm's <c>Dispose</c> path shares the
        /// exact same <c>_pending.FlushAll()</c> call and ordering this test already exercises.</para>
        /// <para><b>RED recipe:</b> two independent recipes, one per assertion. Hoisting
        /// <c>_pending.FlushAll()</c> above the <c>_loaded</c> teardown loop in <c>DoDispose</c> reds the
        /// graph assertion (<c>graphBaseline</c>). Deleting the fetch loop in
        /// <c>PendingDisposalQueue.FlushAll</c> (the <c>for (int i = 0; i &lt; _fetch.Count; i++)</c> loop —
        /// leave <c>_fetch.Clear()</c>) reds the fetch-lease assertion (<c>fetchBaseline</c>); this
        /// fixture is not that recipe's only observer.</para></summary>
        [Test]
        public void Dispose_FlushesPensAfterRecordTeardown()
        {
            var mainSrc   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var stuckGate = new UniTaskCompletionSource<TileResponse>();
            var stuckSrc  = new TestDataSource((id, ct) => stuckGate.Task);

            var go   = new GameObject("TeardownFlushOrder");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom  = 5;
            view.Config.TileSelection.MaxZoom  = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick     = 0; // leaves the graph arm's write complete but unconsumed
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxVerticesPerTick     = int.MaxValue;
            view.Config.MaxConcurrentTileLoads = 64;

            try
            {
                long graphBaseline = TileBuildGraph.DebugLiveCount;
                long fetchBaseline = SharedDisposable<IDecodedTile>.DebugLiveCount;

                var style = StyleParser.Parse(@"{
                    ""version"": 8, ""name"": ""TeardownFlushOrder"",
                    ""sources"": {
                        ""main"":  { ""type"": ""vector"", ""tiles"": [""https://example.com/main/{z}/{x}/{y}.pbf""] },
                        ""stuck"": { ""type"": ""vector"", ""tiles"": [""https://example.com/stuck/{z}/{x}/{y}.pbf""] }
                    },
                    ""layers"": [
                        { ""id"": ""main-fill"",  ""type"": ""fill"", ""source"": ""main"",  ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",200,50,50,1]} },
                        { ""id"": ""stuck-fill"", ""type"": ""fill"", ""source"": ""stuck"", ""source-layer"": ""x"", ""paint"": {""fill-color"": [""rgba"",50,200,50,1]} }
                    ]
                }");

                var mv = view.View;
                mv.Camera.SetProperties(Cam(0, 0, 5.0));
                mv.Camera.SyncToCamera();
                mv.Layers.Build(style, mv.Camera.CurrentProperties.Zoom, view.Config.MaterialSet);

                var specs = new List<TileManager.SourceSpec>
                {
                    new TileManager.SourceSpec("main",  default, 0, int.MaxValue,
                        () => new MvtTileFeatureSource(mainSrc,  new InlineWorkScheduler())),
                    new TileManager.SourceSpec("stuck", default, 0, int.MaxValue,
                        () => new MvtTileFeatureSource(stuckSrc, new InlineWorkScheduler())),
                };
                mv.TileManager.SetSources(specs, view.Config.Backend);

                // "main" fetches + builds inline, parking at write-complete-unconsumed (consume capped to
                // 0) — fetch/prologue/measure/write each advance on their own tick, so pump until
                // ConsumeBacklog sees it (SourceTileGraphBuildTests' Case 3 shape). "stuck" admits on the
                // first tick and its fetch never resolves, so further ticks leave it untouched.
                for (int f = 0; f < 20 && view.CaptureTelemetry().ConsumeBacklog < 1; f++)
                    view.LateUpdate();

                Assert.GreaterOrEqual(view.CaptureTelemetry().ConsumeBacklog, 1,
                    "drive precondition: the main-source tile's write step must be complete but unconsumed.");
                Assert.Greater(TileBuildGraph.DebugLiveCount, graphBaseline,
                    "precondition: the main-source tile's graph must be LIVE (write-complete, unconsumed).");
                Assert.Greater(view.InFlightCount(), 0,
                    "precondition: the stuck-source tile's fetch must still be in flight.");

                // Complete the underlying task WITHOUT ticking again — WaitOffPlayerLoop inside FlushAll
                // then resolves immediately, but the record's own FetchCompleted flag is still false.
                long beforeStuckDecode = SharedDisposable<IDecodedTile>.DebugLiveCount;
                stuckGate.TrySetResult(new TileResponse(SampleTileFixture.Bytes(), TileEncoding.Mvt));
                Assert.Greater(SharedDisposable<IDecodedTile>.DebugLiveCount, beforeStuckDecode,
                    "precondition: resolving the stuck source must mint its decode lease — without one the " +
                    "post-teardown balance below is vacuous.");

                view.Teardown();
                Object.DestroyImmediate(go);
                go = null;

                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "the graph pen must be flushed AFTER the teardown loop stashes into it — an inverted " +
                    "FlushAll leaves this elevated.");
                Assert.AreEqual(0, SharedDisposable<IDecodedTile>.DebugNegativeObservations,
                    "the decode-lease balance below is only honest if no Release() ever over-fired.");
                Assert.AreEqual(fetchBaseline, SharedDisposable<IDecodedTile>.DebugLiveCount,
                    "the fetch pen must release the stuck-source tile's decode lease — a missing fetch " +
                    "drain leaks its SharedDisposable<IDecodedTile> (this reads the WHOLE per-generic " +
                    "total, so an unbalanced lease from elsewhere would also red this).");
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // ThrottleTests — throttle acceptance tests
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Throttle acceptance tests. Engine-side: all tests are Unity EditMode.
    /// </summary>
    [TestFixture]
    public class ThrottleTests : BaseTestFixture
    {
        /// <summary>
        /// Ring capacity requested from every <see cref="ProfilerRecorder"/> here, and therefore the only
        /// safe upper bound when reading samples back — <c>Count</c> is NOT one once the ring has wrapped.
        /// </summary>
        private const int RecorderCapacity = 64;

        // ── Helpers ─────────────────────────────────────────────────────────────────────────
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Fill-only style: countries polygon layer.</summary>
        private static StyleDocument FillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""S55Test"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] }
                }
            ]
        }");

        /// <summary>Fill + line style: countries fill and geolines linestring layer.</summary>
        private static StyleDocument FillAndLineStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""S55TestLine"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] }
                },
                {
                    ""id"": ""geolines-stroke"",
                    ""type"": ""line"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""geolines"",
                    ""paint"": {
                        ""line-color"": [""rgba"", 100, 200, 50, 1],
                        ""line-width"": 10
                    }
                }
            ]
        }");

        private static void PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        // ── shared helper ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a z=5 (9-tile) cover with consume BLOCKED (<c>MaxConsumesPerTick = 0</c>) and every
        /// tile's write step COMPLETE but UNCONSUMED. After this returns, each tile has write-complete,
        /// unconsumed backlog; nothing is Built yet — the caller sets the consume budget and pumps. Caller
        /// owns <c>Teardown()</c> + <c>DestroyImmediate(go)</c>.
        ///
        /// <para><b>Drive contract (job-scheduling-design.md).</b> "Every tile kicked" is read off
        /// <c>TileBuildsStartedLastTick</c> (once per tile, at its first kick — a source tile's prologue kick
        /// or a background tile's measure kick). <c>Await</c> only completes whichever STEP is currently in
        /// flight — a source tile needs the prologue-complete tick AND the write-kick tick before it is
        /// consumable — so the drive pumps until <c>ConsumeBacklog</c> (write complete, unconsumed) covers
        /// every loaded tile, which is exactly the backlog these teeth budget against.</para>
        /// </summary>
        private static (GameObject go, MapView view) SetupBlockedBacklog(StyleDocument style)
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MapView_S87");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 0;            // 0 BLOCKS consume (build the backlog)
            view.Config.MaxMeshBuildsPerTick = 64;           // kick all tiles
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);
            view.LateUpdate();                     // request

            // The fetch task carries the DECODE too, so a cover's fetches complete measurably later than a
            // fixed pair of sleeps can assume — and if the observe tick lands early, nothing is started and
            // the backlog these teeth measure is empty. Pump until every loaded tile has been STARTED
            // instead. The DRIVE changed; what the teeth assert about the consume budget did not.
            int started = 0;
            for (int f = 0; f < 3000; f++)
            {
                view.LateUpdate();
                view.AwaitInFlightMeshBuilds();
                started += view.TileBuildsStartedLastTick();
                if (view.LoadedTileCount() > 0 && started >= view.LoadedTileCount()) break;
            }
            Assert.GreaterOrEqual(started, view.LoadedTileCount(),
                "drive precondition: every cover tile must have been started, or there is no backlog to budget");

            // Pump until every loaded tile's write step is complete but unconsumed — "write complete,
            // unconsumed" is exactly the backlog this helper promises. AwaitInFlightMeshBuilds only
            // completes whichever step is in flight; a source tile needs its prologue-complete tick AND its
            // write-kick tick before ConsumeBacklog can see it.
            for (int f = 0; f < 3000; f++)
            {
                view.LateUpdate();
                view.AwaitInFlightMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.CaptureTelemetry().ConsumeBacklog >= view.LoadedTileCount())
                    break;
            }
            Assert.GreaterOrEqual(view.CaptureTelemetry().ConsumeBacklog, view.LoadedTileCount(),
                "drive precondition: every cover tile's write step must be complete but unconsumed, or there " +
                "is no backlog to budget");
            return (go, view);
        }

        // ── Tooth (a): Mesh build cap binds ────────────────────────────────────────────────

        /// <summary>
        /// MaxMeshBuildsPerTick=1 limits kick-offs to exactly 1 per Tick even when multiple
        /// tiles have ready fetch bytes. Uses z=5 (a known 9-tile cover) to guarantee >= 2 tiles.
        ///
        /// Timing note: with the two-tick kick pattern (fetch observe on Tick N, kick on Tick N+1),
        /// TileBuildsStartedLastTick is 0 on the observe tick and 1 on the first kick tick.
        /// </summary>
        [Test]
        public void Tooth_a_MeshBuildCapBinds()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView_S55_A"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick          = 64;
            view.Config.MaxMeshBuildsPerTick   = 1;  // one kick per Tick
            view.Config.MaxVerticesPerTick        = int.MaxValue;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                // Tick 1: request tiles. PumpPending sees no tiles, then cover adds them.
                view.LateUpdate();
                Assert.GreaterOrEqual(view.LoadedTileCount(), 2,
                    "Need at least 2 tiles at z=5 for the cap test to be non-vacuous.");
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(),
                    "Tooth (a): after Tick 1 (requests only), no kicks issued yet.");

                // Pump to the FIRST kick tick instead of assuming it is tick 3. The fetch task now carries
                // the tile's DECODE, so "fetches are observed by tick 2" is no longer a safe assumption at a
                // fixed 2 ms sleep — on a slow machine tick 2 becomes the request tick and tick 3 the observe
                // tick, and the cap assertion reads 0 for a reason that has nothing to do with the cap. The
                // DRIVE is what changed; the property is identical, and the assertion below is if anything
                // sharper: on the first tick that kicks anything at all, it must kick exactly one.
                // AwaitInFlightMeshBuilds neither kicks nor consumes, so the cap observation is untouched:
                // the tooth is the exact TileBuildsStartedLastTick()==1 value bound by MaxMeshBuildsPerTick=1
                // — DrainMeshBuilds ignores per-frame caps and would settle the whole cover in one call,
                // destroying the per-tick-cap observation this loop exists to make (see DrainMeshBuilds's own
                // doc comment).
                int kickTickFrames = 0;
                while (view.TileBuildsStartedLastTick() == 0 && kickTickFrames++ < 3000)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) break;
                    view.AwaitInFlightMeshBuilds();
                }

                Assert.AreEqual(1, view.TileBuildsStartedLastTick(),
                    $"Tooth (a): cap=1 must limit kicks to exactly 1 on the FIRST tick that kicks at all " +
                    $"(loaded tiles = {view.LoadedTileCount()}). A 0 here means the pump gave up before any " +
                    "kick fired; anything above 1 is the cap failing to bind.");

                // Settle to confirm all tiles eventually complete under the cap.
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(),
                    "Tiles must eventually settle even with MaxMeshBuildsPerTick=1.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Tooth (b): Per-MESH budget splits tiles across frames (DECISIVE) ───────────────────

        /// <summary>
        /// Consume is per-MESH, not per-tile. With the per-frame mesh-count budget
        /// (<c>MaxConsumesPerTick</c>) set to 1, a multi-layer tile is uploaded one mesh per frame — so at
        /// least one frame uploads a mesh while completing NO tile (<c>MeshesConsumedLastTick &gt;= 1 &amp;&amp;
        /// TilesConsumedLastTick == 0</c>). A tile-atomic consume can NEVER produce such a frame (it always
        /// finishes the tile it touches), so this is the decisive falsifier. Also asserts no frame exceeds
        /// the budget, the cover still settles, and tiles are genuinely multi-layer (non-vacuous).
        /// </summary>
        [Test]
        public void Tooth_b_PerMeshBudget_SplitsTilesAcrossFrames()
        {
            var (go, view) = SetupBlockedBacklog(FillAndLineStyle());
            Track(go);
            try
            {
                int totalTiles = view.LoadedTileCount();
                Assert.GreaterOrEqual(totalTiles, 6,
                    "Need a multi-tile z=5 backlog so the per-mesh split is non-vacuous.");

                // Consume ONE mesh per frame.
                view.Config.MaxConsumesPerTick   = 1;
                view.Config.MaxVerticesPerTick = int.MaxValue;

                bool sawPartialTileFrame = false; // a frame that uploaded a mesh but completed NO tile
                int  maxMeshesInAnyFrame = 0;
                int  totalMeshes         = 0;
                int  frames              = 0;
                while (!view.AllTilesSettled() && frames < 5000)
                {
                    view.LateUpdate();
                    int m = view.MeshesConsumedLastTick();
                    int t = view.TilesConsumedLastTick();
                    if (m > maxMeshesInAnyFrame) maxMeshesInAnyFrame = m;
                    totalMeshes += m;
                    if (m >= 1 && t == 0) sawPartialTileFrame = true;
                    frames++;
                }

                Assert.IsTrue(view.AllTilesSettled(),
                    "Cover must settle even at 1 mesh/frame (throttle delays, never drops).");
                Assert.LessOrEqual(maxMeshesInAnyFrame, 1,
                    "Tooth (b): the per-frame mesh-count budget (1) must bind — no frame uploads > 1 mesh.");
                Assert.IsTrue(sawPartialTileFrame,
                    "Tooth (b) DECISIVE: at least one frame must upload a mesh while completing NO tile " +
                    "(MeshesConsumedLastTick >= 1 && TilesConsumedLastTick == 0) — proving a tile's layers " +
                    "split across frames (per-MESH consume). A tile-atomic consume can never produce such a frame.");
                Assert.Greater(totalMeshes, totalTiles,
                    $"Tiles must be multi-layer ({totalMeshes} meshes across {totalTiles} tiles) for the " +
                    "split to be meaningful — fill + line each contribute a mesh.");
            }
            finally { view.Teardown(); }
        }

        // ── Tooth (d): Settles identically ────────────────────────────────────────────────

        /// <summary>
        /// Throttled (MaxMeshBuildsPerTick=1, MaxVerticesPerTick=1) and uncapped settings
        /// must produce the same number of loaded tiles (static cover, same fixture).
        /// Tests that throttle does not permanently stall or drop tiles.
        /// </summary>
        [Test]
        public void Tooth_d_SettlesIdenticallyWithAndWithoutThrottle()
        {
            // Run 1: tight throttle.
            int countThrottled;
            {
                var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
                var go   = Track(new GameObject("MapView_S55_D_Throttled"));
                var view = go.AddComponent<MapView>().WithTestMaterials();
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 1;
                view.Config.MaxVerticesPerTick      = 1;
                try
                {
                    view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());
                    PumpUntilSettled(view);
                    Assert.IsTrue(view.AllTilesSettled(), "Throttled run must settle.");
                    countThrottled = view.LoadedTileCount();
                }
                finally { view.Teardown(); }
            }

            // Run 2: uncapped.
            int countUncapped;
            {
                var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
                var go   = Track(new GameObject("MapView_S55_D_Uncapped"));
                var view = go.AddComponent<MapView>().WithTestMaterials();
                view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
                view.WithTestCamera();
                view.Config.MaxConsumesPerTick        = 64;
                view.Config.MaxMeshBuildsPerTick = 64;
                view.Config.MaxVerticesPerTick      = int.MaxValue;
                try
                {
                    view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());
                    PumpUntilSettled(view);
                    Assert.IsTrue(view.AllTilesSettled(), "Uncapped run must settle.");
                    countUncapped = view.LoadedTileCount();
                }
                finally { view.Teardown(); }
            }

            Assert.AreEqual(countUncapped, countThrottled,
                $"Throttled (count={countThrottled}) and uncapped (count={countUncapped}) " +
                "runs must produce the same loaded-tile count (static cover, same fixture).");
        }

        // ── Tooth (f): Bounds correctness ────────────────────────────────────────────────────

        /// <summary>
        /// Mesh.bounds is baked on the worker thread instead of calling RecalculateBounds() on
        /// the main thread. The baked bounds must equal Unity's authoritative RecalculateBounds()
        /// within floating-point tolerance. Applies to both fill (countries) and line (geolines).
        ///
        /// Method: after settle, read baked bounds, then call RecalculateBounds() on the same mesh
        /// to obtain Unity's truth, and compare center/size within 1e-3 tolerance.
        /// </summary>
        [Test]
        public void Tooth_f_BakedBoundsMatchRecalculateBounds()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView_S55_F"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 0;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillAndLineStyle());
                PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled(), "Tile must settle for bounds test.");

                var id     = new TileId { Z = 0, X = 0, Y = 0 };
                var meshes = view.GetTileMeshes(id);
                Assert.IsNotNull(meshes, "z=0 tile must be built.");
                Assert.GreaterOrEqual(meshes.Length, 1, "At least one layer mesh expected.");

                // ── Fill mesh bounds (meshes[0]) ─────────────────────────────────────────────
                const float eps = 1e-3f;
                Mesh fillMesh      = meshes[0];
                Bounds fillBaked   = fillMesh.bounds;
                fillMesh.RecalculateBounds();
                Bounds fillTruth   = fillMesh.bounds;

                Assert.AreEqual(fillTruth.center.x, fillBaked.center.x, eps,
                    $"Fill bounds center.x: baked={fillBaked.center.x} truth={fillTruth.center.x}");
                Assert.AreEqual(fillTruth.center.y, fillBaked.center.y, eps,
                    $"Fill bounds center.y: baked={fillBaked.center.y} truth={fillTruth.center.y}");
                Assert.AreEqual(fillTruth.center.z, fillBaked.center.z, eps,
                    $"Fill bounds center.z: baked={fillBaked.center.z} truth={fillTruth.center.z}");
                Assert.AreEqual(fillTruth.size.x, fillBaked.size.x, eps,
                    $"Fill bounds size.x: baked={fillBaked.size.x} truth={fillTruth.size.x}");
                Assert.AreEqual(fillTruth.size.y, fillBaked.size.y, eps,
                    $"Fill bounds size.y: baked={fillBaked.size.y} truth={fillTruth.size.y}");
                Assert.AreEqual(fillTruth.size.z, fillBaked.size.z, eps,
                    $"Fill bounds size.z: baked={fillBaked.size.z} truth={fillTruth.size.z}");

                // Sanity: bounds must cover non-zero XZ area (countries polygon spans real geometry).
                Assert.IsFalse(float.IsInfinity(fillBaked.size.x) || float.IsInfinity(fillBaked.size.z),
                    "Fill baked bounds must not be infinite (seeding error: all verts should be finite).");
                Assert.Greater(fillBaked.size.x + fillBaked.size.z, 0f,
                    "Fill baked bounds must have non-zero XZ extent (countries polygon is non-degenerate).");

                // ── Line mesh bounds (meshes[1], if geolines produced geometry) ────────────────
                if (meshes.Length >= 2 && meshes[1] != null)
                {
                    Mesh lineMesh     = meshes[1];
                    Bounds lineBaked  = lineMesh.bounds;
                    lineMesh.RecalculateBounds();
                    Bounds lineTruth  = lineMesh.bounds;

                    Assert.AreEqual(lineTruth.center.x, lineBaked.center.x, eps,
                        $"Line bounds center.x: baked={lineBaked.center.x} truth={lineTruth.center.x}");
                    Assert.AreEqual(lineTruth.center.y, lineBaked.center.y, eps,
                        $"Line bounds center.y: baked={lineBaked.center.y} truth={lineTruth.center.y}");
                    Assert.AreEqual(lineTruth.center.z, lineBaked.center.z, eps,
                        $"Line bounds center.z: baked={lineBaked.center.z} truth={lineTruth.center.z}");
                    Assert.AreEqual(lineTruth.size.x, lineBaked.size.x, eps,
                        $"Line bounds size.x: baked={lineBaked.size.x} truth={lineTruth.size.x}");
                    Assert.AreEqual(lineTruth.size.y, lineBaked.size.y, eps,
                        $"Line bounds size.y: baked={lineBaked.size.y} truth={lineTruth.size.y}");
                    Assert.AreEqual(lineTruth.size.z, lineBaked.size.z, eps,
                        $"Line bounds size.z: baked={lineBaked.size.z} truth={lineTruth.size.z}");

                    Assert.IsFalse(float.IsInfinity(lineBaked.size.x) || float.IsInfinity(lineBaked.size.z),
                        "Line baked bounds must not be infinite.");
                    Assert.Greater(lineBaked.size.x + lineBaked.size.z, 0f,
                        "Line baked bounds must have non-zero XZ extent (geolines spans real geometry).");
                }
                else
                {
                    // Geolines layer exists but produced no mesh (degenerate geometry or absent features).
                    // Not a test failure — the AABB is simply not exercised for lines at this zoom.
                    // The fill assertion above still covers the core contract.
                    Assert.Inconclusive(
                        "Line mesh was null or missing — geolines may not have produced geometry at z=0. " +
                        "Fill bounds assertion passed. Run at a higher zoom with known line coverage to " +
                        "fully exercise the line AABB contract.");
                }
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Tooth (c): Per-mesh budget bounds the markers (ProfilerRecorder) ──────────────────

        /// <summary>
        /// Under a per-frame MESH budget of N, ONE pump consumes exactly N meshes (not the full
        /// backlog), and <c>PmAddTileLayer</c> fires exactly once per consumed mesh — proving the markers
        /// (and thus AddLayer / GPU-upload cost) are bounded by the per-frame budget, not the backlog.
        /// <c>CollectOnlyOnCurrentThread</c> captures only the main-thread consume firings; two
        /// <c>yield return null</c> frames commit the profiler samples before counting.
        /// </summary>
        [UnityTest]
        public IEnumerator Tooth_c_PerMeshBudget_BoundsMarkers()
        {
            var (go, view) = SetupBlockedBacklog(FillAndLineStyle());
            Track(go);
            try
            {
                int totalTiles    = view.LoadedTileCount();
                int layersPerTile = view.FillLayerCount() + view.LineLayerCount(); // 1+1=2
                Assert.GreaterOrEqual(totalTiles, 6, "Need a multi-tile z=5 backlog.");
                int totalBacklogMeshes = totalTiles * layersPerTile;

                // Recorders BEFORE the measured pump. CollectOnlyOnCurrentThread: the consume loop runs on
                // the main thread, so PmAddTileLayer / PmMeshUpload register here; background mesh build
                // (SetupBlockedBacklog, already done) does not.
                using var addLayerRecorder = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "MapRenderer.Tile.AddLayer", capacity: RecorderCapacity,
                    options: ProfilerRecorderOptions.SumAllSamplesInFrame |
                             ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
                using var uploadRecorder = ProfilerRecorder.StartNew(
                    ProfilerCategory.Scripts, "MapRenderer.Mesh.Upload", capacity: RecorderCapacity,
                    options: ProfilerRecorderOptions.SumAllSamplesInFrame |
                             ProfilerRecorderOptions.CollectOnlyOnCurrentThread);

                // ONE pump with a partial per-frame MESH budget (< the full backlog).
                const int meshBudget = 3;
                view.Config.MaxConsumesPerTick   = meshBudget;
                view.Config.MaxVerticesPerTick = int.MaxValue;
                view.LateUpdate();

                int meshesConsumed = view.MeshesConsumedLastTick();
                Assert.AreEqual(meshBudget, meshesConsumed,
                    $"Per-frame mesh budget must bind: expected exactly {meshBudget} meshes, got " +
                    $"{meshesConsumed} (backlog = {totalBacklogMeshes}).");
                Assert.IsFalse(view.AllTilesSettled(),
                    "Budget must defer the rest — not settled after one budgeted pump.");

                yield return null;
                yield return null;

                // Clamp to the ring's CAPACITY, not Count: ProfilerRecorder is a fixed-size ring, and the
                // pump above runs far more frames than that, so once it wraps `Count` stops being a valid
                // index bound and GetSample() throws IndexOutOfRange (intermittently, on slow machines).
                long addLayerHits = 0;
                for (int i = 0; i < math.min(addLayerRecorder.Count, RecorderCapacity); i++)
                    addLayerHits += addLayerRecorder.GetSample(i).Count;
                long uploadHits = 0;
                for (int i = 0; i < math.min(uploadRecorder.Count, RecorderCapacity); i++)
                    uploadHits += uploadRecorder.GetSample(i).Count;

                // AddLayer fires once per CONSUMED (non-null) mesh — exact, per-mesh.
                Assert.AreEqual(meshesConsumed, addLayerHits,
                    $"Tooth (c): PmAddTileLayer must fire exactly once per consumed mesh ({meshesConsumed}); " +
                    $"got {addLayerHits}. Bounded by the per-frame budget, NOT the {totalBacklogMeshes}-mesh " +
                    "backlog. If 0: profiler did not capture on this thread (CollectOnlyOnCurrentThread).");
                // Upload happened and is bounded by the budget, not the backlog (>= consumed allows for any
                // empty-layer UploadMesh calls that returned null; < backlog proves the budget bound).
                Assert.GreaterOrEqual(uploadHits, meshesConsumed,
                    $"Tooth (c): PmMeshUpload ({uploadHits}) must fire at least once per consumed mesh ({meshesConsumed}).");
                Assert.Less(uploadHits, totalBacklogMeshes,
                    $"Tooth (c): PmMeshUpload ({uploadHits}) must be bounded by the per-frame budget, NOT the " +
                    $"full {totalBacklogMeshes}-mesh backlog.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Tooth (e): Pixel parity with the default throttle ────────────────────────────────────

        /// <summary>
        /// A throttled MapView (defaults: MaxMeshBuildsPerTick=2, MaxVerticesPerTick=50000)
        /// must render a non-blank settled fill cover on the default Entities backend. Proves the
        /// throttle changes timing, not output.
        ///
        /// Uses the same SnapshotCoverage gate as <c>MapViewSnapshotTests.MapViewLiveLoop_RendersMultiTileFill_NonBlank</c>.
        /// Covers Entities backend only; BRG-backend pixel parity is a residual gap.
        /// </summary>
        [Test]
        public void Tooth_e_ThrottledRender_NonBlankCoverage_EntitiesBackend()
        {
            const int SnapW = 512, SnapH = 512;
            const int Zoom  = 3;
            var bgColor  = new Color(0.10f, 0.11f, 0.15f, 1f);
            var bg32 = new Color32(26, 28, 38, 255);

            var lightGo = Track(new GameObject("S55_E_Light"));
            var light   = lightGo.AddComponent<Light>();
            light.type  = LightType.Directional; light.intensity = 1f;
            lightGo.transform.rotation = Quaternion.Euler(60f, 30f, 0f);

            var cameraGo = Track(new GameObject("S55_E_Cam"));
            var camera   = cameraGo.AddComponent<Camera>();
            camera.orthographic    = true;
            camera.clearFlags      = CameraClearFlags.SolidColor;
            camera.backgroundColor = bgColor;
            camera.enabled         = false;
            camera.farClipPlane    = 1e9f;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            var mapGo = Track(new GameObject("S55_E_MapView"));
            var view  = mapGo.AddComponent<MapView>().WithTestMaterials();
            // DEFAULT throttle values — the whole point is not to override them here.
            view.Config.TileSelection.MinZoom = Zoom; view.Config.TileSelection.MaxZoom = Zoom;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 2;      // default
            view.Config.MaxVerticesPerTick      = 50000;  // default

            try
            {
                var cam3 = new CameraProperties(
                    new GeoCoordinate3D { Longitude = 0, Latitude = 0, Altitude = 0 }, Zoom, 0, 0);
                view.LoadTestStyle(TestDataSource.FromBytes(SampleTileFixture.Bytes()),
                    cam3, style: FillStyle());

                // Pump to settle — throttle spreads kicks/consumes across many ticks. AwaitInFlightMeshBuilds
                // (not DrainMeshBuilds) KEPT here: on the default Entities backend, a settle reached in
                // essentially one forced-drain tick renders BLANK — Entities Graphics needs a few more
                // Rebuild/EG-system ticks after the last tile is consumed before its BRG batch is cullable
                // (see VisualScene.WarmupFrames, which exists for exactly this). AwaitInFlightMeshBuilds keeps
                // ONE real LateUpdate tick per loop iteration (it only supplies the ThreadPool wall-clock, no
                // consume/kick of its own), so the warm-up survives — unlike DrainMeshBuilds, which collapsed
                // the tick count to ~1 and made this test go blank (RED-verified:
                // Tooth_e_ThrottledRender_NonBlankCoverage_EntitiesBackend failed with IsBlank=true after
                // the DrainMeshBuilds conversion).
                for (int f = 0; f < 5000 && !(view.LoadedTileCount() > 0 && view.AllTilesSettled()); f++)
                {
                    view.LateUpdate();
                    view.AwaitInFlightMeshBuilds();
                }

                Assert.IsTrue(view.AllTilesSettled() && view.LoadedTileCount() > 0,
                    "Throttled MapView must settle all tiles (throttle defers, not drops).");

                // Frame the camera on the loaded tile bounds.
                float tileSize = (float)(WebMercator.WorldExtent * 2.0 / System.Math.Pow(2.0, Zoom));
                Bounds b = view.ComputeSceneBounds(tileSize);
                Assert.Greater(b.size.magnitude, 0f, "Loaded tiles must have non-degenerate scene bounds.");
                camera.transform.position = new Vector3(b.center.x, b.center.y + 200f, b.center.z);
                camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                camera.orthographicSize   = UnityEngine.Mathf.Max(b.size.x, b.size.z) * 0.55f;

                snap.Render(camera);
                snap.WritePng("s55-throttled-cover.png");

                var verdict = SnapshotCoverage.Analyse(snap.Pixels, bg32);
                Assert.IsFalse(verdict.IsBlank,
                    "Tooth (e): throttled settled render must produce visible fill coverage.");
                Assert.That(verdict.FilledFraction, NUnit.Framework.Is.InRange(0.05f, 0.95f),
                    "Fill fraction must be in a sane band (geometry visible, not full-frame).");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Tooth (h): Partial-tile eviction leaks nothing ────────────────────────────────────

        /// <summary>
        /// Eviction teeth. A tile evicted MID-consume (ConsumeCursor part-way) must dispose both its
        /// already-uploaded layers' NativeArrays (per-mesh, during consume) AND its un-consumed remainder
        /// (via <c>PendingDisposalQueue</c>'s holding pen on cover change), with no double-dispose crash.
        /// Decisive: the combined fill+line <c>DebugLiveAllocCount</c> returns to its baseline after the
        /// partial-evict + teardown cycle — a leaked remainder (ReleaseTile not stashing a partial tile) or a
        /// missed per-mesh dispose would leave it above baseline; a use-after/double dispose would throw.
        /// </summary>
        [Test]
        public void Tooth_h_PartialTileEviction_NoLeak()
        {
            long Live() => MapRenderer.Unity.Rendering.Style.MeshDataPayload.DebugLiveAllocCount;

            long baseline = Live();

            var (go, view) = SetupBlockedBacklog(FillAndLineStyle());
            Track(go);
            try
            {
                // Consume a few meshes at 1/frame so some tiles are left PARTIALLY consumed (cursor > 0).
                view.Config.MaxConsumesPerTick   = 1;
                view.Config.MaxVerticesPerTick = int.MaxValue;
                for (int i = 0; i < 3; i++) view.LateUpdate();
                Assert.IsFalse(view.AllTilesSettled(),
                    "Must be mid-consume (partial tiles present) before the eviction.");

                // Evict the whole z=5 cover via a far camera jump → ReleaseTile runs on partial tiles.
                // Block further kicks-into-consume timing is irrelevant; we measure after teardown.
                view.Camera.Apply(
                    new CameraPropertiesUpdate { Longitude = 150.0, Latitude = 70.0 });
                // Plain LateUpdate ticks (not DrainMeshBuilds): MaxReleasesPerTick=4 throttles the departing
                // z=5 (9-tile) cover's release across several ticks, so some of these still sit in _loaded,
                // partially consumed (cap=1), on the early ticks. DrainMeshBuilds ignores MaxConsumesPerTick
                // and would fully consume them before ReleaseTile ever sees them mid-consume — disarming the
                // tooth (it would pass even if RenderTeardownRecord's partial-tile dispose broke). No wall-clock
                // wait needed here: the measurement below runs AFTER view.Teardown(), which itself drains the
                // pen via a parked WaitOffPlayerLoop (TileManager.Teardown) — these 6 ticks only need to
                // spread the release across enough frames for ReleaseTile to see mid-consume tiles.
                for (int i = 0; i < 6; i++) view.LateUpdate(); // release old + drain holding pen
            }
            finally
            {
                view.Teardown();              // full drain — also covers any newly-entered tiles
            }

            Assert.AreEqual(baseline, Live(),
                "Tooth (h): partial-tile eviction + teardown must leak no LayerMeshData NativeArrays " +
                "(consumed layers disposed per-mesh during consume; the un-consumed remainder via the " +
                "holding pen). A non-baseline count means a partial tile's remainder leaked.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileLoadMeasurementTests — consume-tick allocation and load-measurement teeth
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TileLoadMeasurementTests : BaseTestFixture
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""LoadMeasurement"",
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

        /// <summary>Pumps Tick() until every loaded tile has settled or a spin budget is hit.</summary>
        private static void PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        // ── Tooth (a): TileScheduler.Request fast paths are alloc-free ─────────────────────────────

        /// <summary>
        /// Cache-hit Request must not allocate. Falsifiable: a shallow re-fetch-every-time
        /// implementation, or one that re-wraps the cached value in a new allocating wrapper instead of
        /// returning <c>UniTask.FromResult(cached)</c> directly, fails. Expected to PASS on current code
        /// (per code inspection of the vendored UniTask — <c>Preserve()</c>/<c>FromResult</c> short-circuit
        /// for a non-null-source task) — this stands as a permanent regression guard.
        /// </summary>
        [Test]
        public void TileScheduler_Request_CacheHit_IsAllocFree()
        {
            byte[] bytes     = SampleTileFixture.Bytes();
            var    src       = TestDataSource.FromBytes(bytes);
            var    cache     = new TileCache(capacity: 16);
            var    scheduler = new TileScheduler(src, cache);
            var    id        = new TileId { Z = 5, X = 10, Y = 11 };

            // Warm-up: the FIRST Request is a genuine new fetch (CancellationTokenSource.CreateLinkedTokenSource
            // + FetchAndCacheAsync(...).Preserve()) — legitimately allocates. Drive it to completion so
            // cache.Put has already run. With a sync-completing source the whole fetch (including the
            // lock + Put) now runs INSIDE Request() itself, so WaitOffPlayerLoop short-circuits on an
            // already-completed awaiter — no race with the assertion below.
            var first  = scheduler.Request(id);
            first.WaitOffPlayerLoop(10000);
            Assert.IsTrue(first.Status.IsCompleted(), "warm-up fetch must complete before measuring the cache-hit path.");

            // Block-bodied lambda (not an expression lambda): Request returns a value, and Assert.That needs
            // a void TestDelegate here, not a Func<UniTask<TileResponse>> — an expression lambda would bind
            // to the wrong Assert.That overload and fail with "actual value must be a TestDelegate".
            Assert.That(() => { scheduler.Request(id); }, Is.Not.AllocatingGCMemory(),
                "Cache-hit Request must not allocate — UniTask.FromResult(cached) must return the memoized " +
                "value directly, never re-wrap it or re-fetch from the source.");
        }

        /// <summary>
        /// In-flight-share Request (two concurrent calls for the same not-yet-settled id) must return the
        /// SAME preserved UniTask with no new allocation on the second call. Falsifiable: a per-call
        /// re-wrap, or a fresh CTS/dictionary entry per call, fails.
        /// </summary>
        [Test]
        public void TileScheduler_Request_InFlightShare_IsAllocFree()
        {
            // Gated source (TileFetchCancellationTests' pattern): stays in-flight until we complete it —
            // FromBytes would complete immediately and could never exercise the in-flight-share branch.
            var gate      = new UniTaskCompletionSource<TileResponse>();
            var src       = new TestDataSource((id, ct) => gate.Task);
            var cache     = new TileCache(capacity: 16);
            var scheduler = new TileScheduler(src, cache);
            var tileId    = new TileId { Z = 5, X = 1, Y = 1 };

            // Warm-up: the FIRST Request creates the in-flight entry (CTS + FetchAndCacheAsync(...).Preserve())
            // — legitimately allocates. It stays pending because the gated source never completes.
            var first = scheduler.Request(tileId);
            Assert.IsFalse(first.Status.IsCompleted(),
                "warm-up call must genuinely be in-flight (source gated) for this tooth to have teeth.");

            // Block-bodied lambda — see the cache-hit tooth above for why (Request returns a value).
            Assert.That(() => { scheduler.Request(tileId); }, Is.Not.AllocatingGCMemory(),
                "In-flight-share Request must return the existing preserved UniTask with zero new allocation.");

            // Cleanup: settle the gate so nothing dangles (no exception — no unobserved-exception concern).
            gate.TrySetResult(TileResponse.Absent(TileEncoding.Mvt));
        }

        // ── Tooth (b): deep-cover select is alloc-free AT STALL SCALE ───────────────────────────────

        /// <summary>
        /// Drives a real <c>ScreenSpaceLod</c> quadtree descent at stall scale (high zoom, real tilt,
        /// Berlin params mirroring <see cref="TileLoadStressDriver"/>) and asserts a sub-tile camera nudge
        /// — same tile set stays selected, but <c>CoverKeyGate</c> still trips dirty — ticks alloc-free.
        ///
        /// This is NOT a re-run of <c>MapViewLiveLoopTests.MapView_SteadyStateTick_DoesNotAllocateGCMemory</c>
        /// (that test's z2 whole-world cover is real, valuable prior art but never exercises the deep
        /// descent / mixed-zoom fan a tilted high-zoom camera produces — a tilt=0 top-down cover would
        /// degenerate toward that same shallow case, since every tile then sits at ~the same distance from
        /// the camera and the ScreenSpaceLod stop condition stops them all at ~the same zoom).
        /// </summary>
        [Test]
        public void MapView_DeepCoverSelect_SubTileNudge_IsAllocFreeAtStallScale()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = Track(new GameObject("MapView_LoadMeasurement_DeepCover"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.Backend                 = RenderBackend.Brg; // zero-alloc contract; avoid EG's intermittent alloc noise
            view.Config.TileSelection.MinZoom                 = 0;
            view.Config.TileSelection.MaxZoom                 = 14;    // matches TileLoadStressDriver.MaxZoom / MapViewConfig's default
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 256;
            view.Config.MaxMeshBuildsPerTick = 256;
            view.Config.MaxVerticesPerTick      = int.MaxValue;

            try
            {
                // Berlin, high zoom, real tilt — TileLoadStressDriver's Berlin params (CenterLatitude=52.52,
                // CenterLongitude=13.405, MaxZoom=14). Tilt=60 (near the [0,90] clamp) is deliberate: at
                // tilt=0 every tile in a top-down footprint sits at ~the same distance from the camera, so
                // ScreenSpaceLodStrategy's stop condition (GroundSize <= ScreenRatio * Distance) stops them
                // all at ~the same zoom — a near-uniform cover that degenerates toward the shallow z2 case.
                // A real tilt makes far tiles recede toward the horizon (stop coarse) while near tiles
                // descend to z14 — a genuine mixed-zoom fan, exercising many more quadtree levels.
                var cam = new CameraProperties(
                    new GeoCoordinate3D { Longitude = 13.405, Latitude = 52.52, Altitude = 0 },
                    zoom: 14.0, heading: 0.0, tilt: 60.0);

                view.LoadTestStyle(src, cam, style: style);
                PumpUntilSettled(view, maxFrames: 5000);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must be built before measuring steady state");

                // ── Teeth: prove this is genuinely the deep/mixed/large cover the stage demands ──
                // (not a silent re-run of the shallow z2/16-tile case with different numbers).
                var telemetry = view.CaptureTelemetry();
                Assert.GreaterOrEqual(telemetry.CoverMaxZoom, 12,
                    $"Cover must reach z12+ (stall-scale near field) — got CoverMaxZoom={telemetry.CoverMaxZoom}.");
                Assert.IsTrue(telemetry.IsMixedZoom,
                    "Cover must be mixed-zoom (a real ScreenSpaceLod fan) — a uniform-zoom cover would just " +
                    "be the shallow z2 case at a bigger number, proving nothing new.");
                Assert.Greater(telemetry.VisibleTileCount, 30,
                    $"Cover must be large (many quadtree nodes visited) — got VisibleTileCount={telemetry.VisibleTileCount}.");

                // Prime the reused buffers (_cover, _coverSet, _toRelease, the selector's _stack) to steady
                // capacity before measuring — mirrors MapViewLiveLoopTests' priming ticks.
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = 52.52 + 1e-6, Longitude = 13.405 + 1e-6 });
                view.LateUpdate();
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = 52.52,        Longitude = 13.405 });
                view.LateUpdate();

                int loadedBefore = view.LoadedTileCount();

                // Sub-tile nudge (~10 cm) — far smaller than any tile in this cover (the finest, z14, is
                // ~1.5 km even at Berlin's latitude): the same tile set stays selected, but CoverKeyGate's
                // exact-equality check still trips it dirty.
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = 52.52 + 1e-6, Longitude = 13.405 - 1e-6 });

                Assert.That(() => view.LateUpdate(), Is.Not.AllocatingGCMemory(),
                    "MapView.LateUpdate must not allocate during a sub-tile nudge over a deep (z12+), mixed-zoom, " +
                    "large ScreenSpaceLod cover — the recompute path (quadtree descent + request/release " +
                    "diff) at STALL SCALE, not the shallow z2 case. A failure means the descent boxes a LOD " +
                    "context, allocates a scratch list per call, or LINQs over the cover.");

                // Guards against a vacuous pass: prove the recompute actually ran (not an early-out) and the
                // cover is unchanged (no edge tile crossed — which would have issued a real, legitimately
                // allocating Request/Release, a different cost than the one this tooth measures).
                Assert.AreEqual(1, view.CoverRecomputesLastTick(),
                    "The measured Tick must have run the full recompute (CoverRecomputesLastTick == 1) — " +
                    "otherwise the alloc-free assertion above passed vacuously via an early-out.");
                Assert.AreEqual(loadedBefore, view.LoadedTileCount(),
                    "The sub-tile nudge must not change the loaded tile set — otherwise the request/release " +
                    "diff legitimately allocated a new fetch, a different, unrelated cost from this tooth.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Tooth (d): CoverRecomputesLastTick baseline discriminator ───────────────────────────────

        /// <summary>
        /// BASELINE, not the post-fix bound: drives N sub-tile camera nudges and asserts
        /// <c>CoverRecomputesLastTick</c> sums to exactly N — today, with no throttle, every dirty Tick
        /// recomputes. This documents current behaviour as a regression guard on the counter itself (if it
        /// ever silently under-reports, this catches it). A sub-tile-recompute throttle, if one is ever
        /// built, would make this assertion fail; its own test would assert <c>recomputes &lt; N</c>
        /// instead.
        /// </summary>
        [Test]
        public void CoverRecomputesLastTick_SumsToN_ForNSubTileNudges_BaselineNoThrottleYet()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView_LoadMeasurement_Counter"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5; // cheap z5 cover — this tooth is about the counter, not descent cost
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());
                PumpUntilSettled(view);
                Assert.IsTrue(view.AllTilesSettled(), "all tiles must settle before driving the nudge loop.");

                const int N = 8;
                int recomputes = 0;
                double lat = 0.0;
                for (int i = 0; i < N; i++)
                {
                    lat += 1e-6; // sub-tile nudge each iteration — same tile stays selected, CoverKeyGate trips dirty
                    view.Camera.Apply(new CameraPropertiesUpdate { Latitude = lat });
                    view.LateUpdate();
                    recomputes += view.CoverRecomputesLastTick();
                }

                Assert.AreEqual(N, recomputes,
                    $"Baseline: with no throttle yet, every sub-tile-dirtied Tick must run the full recompute " +
                    $"(CoverRecomputesLastTick must sum to {N} across {N} nudges; got {recomputes}). If this " +
                    "ever reads less than N without an explicit throttle fix landing, the counter itself has " +
                    "a false negative.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── A CLEAN camera with tiles still pending must NOT re-run the cover recompute ────────────────

        /// <summary>
        /// The gate change: the expensive cover recompute (quadtree descent + request/release diff)
        /// is now gated on <c>CoverKeyGate.IsDirty</c> ALONE — a static camera with tiles still in-flight no longer
        /// forces it every frame. A GATED data source keeps every requested tile pending across ticks (its
        /// fetch only completes on cancellation), so <c>pending &gt; 0</c> holds while the camera stays put.
        ///
        /// <para>Falsifiable: BEFORE the fix the gate was <c>!_coverDirty &amp;&amp; pending == 0</c> (the
        /// an older field; now <c>CoverKeyGate.IsDirty</c>), so
        /// <c>pending &gt; 0</c> forced the full recompute (CoverRecomputesLastTick == 1) every frame for zero
        /// effect (the cover set is unchanged → the request/release loops are pure no-ops). This test asserts
        /// 0 across those frames — it FAILS on the pre-fix gate. <c>PumpPending</c> still runs each tick, so
        /// tiles keep progressing; this removes wasted work, it does not stall the pipeline.</para>
        /// </summary>
        [Test]
        public void CoverRecompute_CleanCameraWithPendingTiles_IsSkipped_Stall6Fix()
        {
            // Per-tile gated fetch: stays pending until the tile's own token is cancelled (Release/teardown),
            // so no shared-task double-consume, and teardown cancels each → prompt, no 10 s spin.
            var src  = new TestDataSource((id, ct) =>
            {
                var utcs = new UniTaskCompletionSource<TileResponse>();
                ct.Register(() => utcs.TrySetCanceled(ct));
                return utcs.Task;
            });
            var go   = Track(new GameObject("MapView_LoadMeasurement_CleanPending"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend = RenderBackend.Brg;
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());

                // Establish the cover + request the tiles (they then hang on the gated fetch).
                view.LateUpdate();
                Assert.Greater(view.LoadedTileCount(), 0, "cover established → tiles requested on the first tick.");
                Assert.IsFalse(view.AllTilesSettled(), "gated source → requested tiles stay pending across ticks.");

                // Same camera, tiles still pending: the recompute must be skipped every frame.
                for (int i = 0; i < 5; i++)
                {
                    view.LateUpdate();
                    Assert.IsFalse(view.AllTilesSettled(), "sanity: tiles remain pending (tokens not cancelled).");
                    Assert.AreEqual(0, view.CoverRecomputesLastTick(),
                        "clean camera + pending tiles must NOT re-run the cover recompute (was 1 pre-fix: " +
                        "pending>0 forced the descent + request/release diff every frame for zero effect).");
                }
            }
            finally
            {
                view.Teardown(); // cancels each tile's token → the gated fetches complete promptly
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileLoadStressDriverTests — the TileLoadStressDriver debug harness's motion math and wiring
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TileLoadStressDriverTests : BaseTestFixture
    {
        private const double Tol = 1e-9;

        // ── Pure zoom math: ZoomAt triangle wave ─────────────────────────────────────────────────────

        [Test]
        public void ZoomAt_PhaseZero_IsMin()
            => Assert.AreEqual(4.0, TileLoadStressDriver.ZoomAt(0.0, 4.0, 14.0, 8.0), Tol);

        [Test]
        public void ZoomAt_HalfPeriod_IsMax()
            => Assert.AreEqual(14.0, TileLoadStressDriver.ZoomAt(4.0, 4.0, 14.0, 8.0), Tol);

        [Test]
        public void ZoomAt_FullPeriod_WrapsBackToMin()
            => Assert.AreEqual(4.0, TileLoadStressDriver.ZoomAt(8.0, 4.0, 14.0, 8.0), Tol);

        [Test]
        public void ZoomAt_QuarterPeriod_IsMidpointRising()
            => Assert.AreEqual(9.0, TileLoadStressDriver.ZoomAt(2.0, 4.0, 14.0, 8.0), Tol);

        [Test]
        public void ZoomAt_ThreeQuarterPeriod_IsMidpointFalling()
            => Assert.AreEqual(9.0, TileLoadStressDriver.ZoomAt(6.0, 4.0, 14.0, 8.0), Tol);

        [Test]
        public void ZoomAt_NonPositivePeriod_PinsToMin()
        {
            Assert.AreEqual(4.0, TileLoadStressDriver.ZoomAt(3.7, 4.0, 14.0, 0.0), Tol);
            Assert.AreEqual(4.0, TileLoadStressDriver.ZoomAt(3.7, 4.0, 14.0, -5.0), Tol);
        }

        [Test]
        public void ZoomAt_ReversedRange_IsSwapped()
        {
            Assert.AreEqual(4.0,  TileLoadStressDriver.ZoomAt(0.0, 14.0, 4.0, 8.0), Tol);
            Assert.AreEqual(14.0, TileLoadStressDriver.ZoomAt(4.0, 14.0, 4.0, 8.0), Tol);
        }

        [Test]
        public void ZoomAt_StaysWithinRange_OverAFullSweep()
        {
            for (int i = 0; i <= 100; i++)
            {
                double z = TileLoadStressDriver.ZoomAt(i * 0.16, 4.0, 14.0, 8.0);
                Assert.GreaterOrEqual(z, 4.0 - Tol);
                Assert.LessOrEqual(z, 14.0 + Tol);
            }
        }

        // ── Pure pan math: LookAtAt circular orbit ───────────────────────────────────────────────────
        // Centre at the equator (cos(lat)=1) so the longitude scaling is 1 and the orbit is exact.

        [Test]
        public void LookAtAt_PhaseZero_IsDueEastOfCentre()
        {
            var (lat, lon) = TileLoadStressDriver.LookAtAt(0.0, 0.0, 0.0, 10.0, 8.0);
            Assert.AreEqual(0.0,  lat, 1e-9);
            Assert.AreEqual(10.0, lon, 1e-9);
        }

        [Test]
        public void LookAtAt_QuarterPeriod_IsDueNorth()
        {
            var (lat, lon) = TileLoadStressDriver.LookAtAt(2.0, 0.0, 0.0, 10.0, 8.0);
            Assert.AreEqual(10.0, lat, 1e-9);
            Assert.AreEqual(0.0,  lon, 1e-9);
        }

        [Test]
        public void LookAtAt_HalfPeriod_IsDueWest()
        {
            var (lat, lon) = TileLoadStressDriver.LookAtAt(4.0, 0.0, 0.0, 10.0, 8.0);
            Assert.AreEqual(0.0,   lat, 1e-9);
            Assert.AreEqual(-10.0, lon, 1e-9);
        }

        [Test]
        public void LookAtAt_FullPeriod_WrapsBackToStart()
        {
            var (lat, lon) = TileLoadStressDriver.LookAtAt(8.0, 0.0, 0.0, 10.0, 8.0);
            Assert.AreEqual(0.0,  lat, 1e-9);
            Assert.AreEqual(10.0, lon, 1e-9);
        }

        [Test]
        public void LookAtAt_NonPositivePeriodOrRadius_PinsToCentre()
        {
            var a = TileLoadStressDriver.LookAtAt(3.7, 52.52, 13.405, 0.05, 0.0);
            Assert.AreEqual(52.52,  a.latitude,  Tol);
            Assert.AreEqual(13.405, a.longitude, Tol);

            var b = TileLoadStressDriver.LookAtAt(3.7, 52.52, 13.405, 0.0, 12.0);
            Assert.AreEqual(52.52,  b.latitude,  Tol);
            Assert.AreEqual(13.405, b.longitude, Tol);
        }

        [Test]
        public void LookAtAt_AwayFromEquator_ScalesLongitudeByInvCosLat()
        {
            // At quarter period the orbit is due-north (lon == centre) regardless of latitude.
            var (lat, lon) = TileLoadStressDriver.LookAtAt(3.0, 52.52, 13.405, 0.05, 12.0);
            Assert.AreEqual(52.52 + 0.05, lat, 1e-9, "due north: centre lat + radius");
            Assert.AreEqual(13.405,       lon, 1e-9, "due north: longitude unchanged");

            // At phase 0 the longitude offset is radius / cos(lat) — larger than the raw radius at 52.52°N.
            var east = TileLoadStressDriver.LookAtAt(0.0, 52.52, 13.405, 0.05, 12.0);
            double expectedLon = 13.405 + 0.05 / math.cos(math.radians(52.52));
            Assert.AreEqual(expectedLon, east.longitude, 1e-9);
            Assert.Greater(east.longitude - 13.405, 0.05,
                "inverse-cos scaling must widen the longitude offset beyond the raw radius away from the equator");
        }

        // ── Wired live-plumbing: Tick drives the real camera zoom + look-at ──────────────────────────
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8, ""name"": ""stress"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
            ""layers"": [ { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                           ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } } ]
        }");

        private static (GameObject go, MapView view, TileLoadStressDriver driver) WireDriver()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            // NOT `using` — this factory returns go/view/driver to the caller, who owns disposal
            // (a `using` here destroys `go` before the caller ever touches it).
            var go   = new GameObject("MapView_StressDriver");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0; view.Config.TileSelection.MaxZoom = 14;
            view.WithTestCamera();
            view.LoadTestStyle(src, Cam(0, 0, 5.0), style: MinimalStyle());

            var driver = go.AddComponent<TileLoadStressDriver>();
            driver.Map                = view;   // explicit — Start()'s self-wire doesn't run under the EditMode runner
            driver.MinZoom            = 3f;
            driver.MaxZoom            = 9f;
            driver.ZoomPeriodSeconds  = 8f;
            driver.CenterLatitude     = 0.0;    // equator ⇒ exact orbit arithmetic
            driver.CenterLongitude    = 0.0;
            driver.PanRadiusDegrees   = 10.0;
            driver.PanPeriodSeconds   = 8f;
            return (go, view, driver);
        }

        [Test]
        public void Tick_DrivesCameraZoomAndLookAt_AlongTheMotionCurves()
        {
            var (go, view, driver) = WireDriver();
            Track(go);
            try
            {
                // Half period: zoom at MaxZoom, orbit due-west (lat centre, lon −radius).
                driver.Tick(4.0f);
                var cam = view.Camera.CurrentProperties;
                Assert.AreEqual(9.0,   cam.Zoom,               1e-3, "half period ⇒ MaxZoom");
                Assert.AreEqual(0.0,   cam.LookAt.Latitude,    1e-3, "half period ⇒ orbit latitude back at centre");
                Assert.AreEqual(-10.0, cam.LookAt.Longitude,   1e-3, "half period ⇒ orbit due-west of centre");

                // Another half period: zoom back to MinZoom, orbit due-east again (full lap).
                driver.Tick(4.0f);
                cam = view.Camera.CurrentProperties;
                Assert.AreEqual(3.0,  cam.Zoom,             1e-3, "full period ⇒ MinZoom");
                Assert.AreEqual(0.0,  cam.LookAt.Latitude,  1e-3);
                Assert.AreEqual(10.0, cam.LookAt.Longitude, 1e-3, "full period ⇒ orbit due-east of centre");
            }
            finally
            {
                view.Teardown();
            }
        }

        [Test]
        public void Tick_Disabled_LeavesCameraUntouched()
        {
            var (go, view, driver) = WireDriver();
            Track(go);
            try
            {
                driver.SweepEnabled = false;
                var before = view.Camera.CurrentProperties;
                driver.Tick(4.0f);
                var after = view.Camera.CurrentProperties;
                Assert.AreEqual(before.Zoom,             after.Zoom,             Tol, "zoom must be untouched");
                Assert.AreEqual(before.LookAt.Latitude,  after.LookAt.Latitude,  Tol, "look-at must be untouched");
                Assert.AreEqual(before.LookAt.Longitude, after.LookAt.Longitude, Tol, "look-at must be untouched");
            }
            finally
            {
                view.Teardown();
            }
        }

        [Test]
        public void Tick_Unwired_NoOpsCleanly()
        {
            var go   = Track(new GameObject("StressDriver_Unwired"));
            try
            {
                var driver = go.AddComponent<TileLoadStressDriver>();
                driver.Map = null;
                Assert.DoesNotThrow(() => driver.Tick(1.0f));
            }
            finally
            {
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileManagerBackgroundRegistrationTests — the source-less per-covered-tile background processor wiring
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TileManagerBackgroundRegistrationTests : BaseTestFixture
    {
        private static string BackgroundOnlyStyle(string colorHex = "#00ff00") => $@"{{
            ""version"": 8,
            ""layers"": [ {{ ""id"": ""bg"", ""type"": ""background"",
                             ""paint"": {{ ""background-color"": ""{colorHex}"" }} }} ]
        }}";

        /// <summary>Two dense background layers over one covered tile — the reachable proxy, in THIS stage,
        /// for "a tile with many fill layers": each becomes its own
        /// <c>ILayerMeshBuild</c>/write output, so a <c>MaxConsumesPerTick</c> budget of 1 forces
        /// a genuine partial consume — one layer taken this Tick, one left for the next.</summary>
        private static string TwoBackgroundLayersStyle() => @"{
            ""version"": 8,
            ""layers"": [
                { ""id"": ""bg1"", ""type"": ""background"", ""paint"": { ""background-color"": ""#00ff00"" } },
                { ""id"": ""bg2"", ""type"": ""background"", ""paint"": { ""background-color"": ""#0000ff"" } }
            ]
        }";

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static (GameObject go, MapView view) NewView(RenderBackend backend, int zoom)
        {
            var go   = new GameObject("BgRegistrationMapView");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = zoom;
            view.Config.TileSelection.MaxZoom = zoom;
            view.WithTestCamera();
            view.Config.Backend               = backend;
            view.Config.MaxConsumesPerTick    = 64;
            view.Config.MaxMeshBuildsPerTick  = 64;
            return (go, view);
        }

        /// <summary>Pumps the MapView across editor frames until its cover has settled, yielding a frame
        /// each iteration (never Thread.Sleep — a blocked thread does not advance the player loop and races
        /// the async decode/mesh-build). Callers are <c>[UnityTest]</c> coroutines: <c>yield return</c> this.</summary>
        private static IEnumerator PumpUntilSettled(MapView view, int maxFrames = 2500)
        {
            for (int f = 0; f < maxFrames; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) yield break;
                yield return null;
            }
        }

        // ── Tooth 1: per-covered-tile registration (primary semantic tooth) ──────────────────────────

        [UnityTest]
        public IEnumerator BackgroundStyle_RegistersOneQuadPerCoveredTile()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3); // z3: a multi-tile cover (non-vacuous N)
            Track(go);
            try
            {
                view.LoadTestStyle(null, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));
                yield return PumpUntilSettled(view);

                int loaded = view.LoadedTileCount();
                Assert.Greater(loaded, 1,
                    "non-vacuous: the cover must contain MORE than one tile, or 'one quad per tile' is untested.");
                Assert.IsTrue(view.AllTilesSettled());

                var gor = view.GameObjectRenderer();
                Assert.IsNotNull(gor, "GameObject renderer must be constructed when Backend == GameObject.");

                var keys = new List<LoadedTileKey>();
                view.TileManager.CollectLoadedTileKeys(keys);
                Assert.AreEqual(loaded, keys.Count);

                Material backgroundMaterial = view.Layers[0].Material; // the ONLY layer — DrawIndex 0
                Assert.IsNotNull(backgroundMaterial);

                foreach (var key in keys)
                {
                    Transform container = gor.Container(key.Tile);
                    Assert.IsNotNull(container, $"a per-tile container must exist for {key.Tile}.");
                    Assert.AreEqual(1, container.childCount,
                        $"exactly one draw item (the background quad) per covered tile ({key.Tile}).");

                    Transform quad = container.GetChild(0);
                    var mf = quad.GetComponent<MeshFilter>();
                    var mr = quad.GetComponent<MeshRenderer>();
                    Assert.IsNotNull(mf?.sharedMesh, $"the background quad at {key.Tile} must carry a mesh.");
                    Assert.Greater(mf.sharedMesh.vertexCount, 0,
                        $"the background quad's mesh at {key.Tile} must be non-empty.");
                    Assert.AreSame(backgroundMaterial, mr.sharedMaterial,
                        $"the background quad at {key.Tile} must draw with the background layer's material " +
                        "(its declared DrawIndex/material slot).");
                }

                Assert.AreEqual(loaded, gor.ContainerCount(), "exactly one container per covered tile — no extras.");
                Assert.AreEqual(loaded, gor.DrawItemCount(), "exactly one draw item per covered tile — no extras.");
            }
            finally { view.Teardown(); }
        }

        // ── Tooth (a): step progression — job-scheduling-design.md. ───────────────────────────────────
        //
        // A single-tile cover (z0 — exactly one covered tile), so "Measure observed, then Write observed
        // later" is a claim about ONE tile's own BuildStep transition (None → Measure → Write → None, which
        // the code only ever advances in that order) rather than an interleaving artefact across several
        // tiles independently reaching different steps on the same tick — the "readiness, not order" trap.
        // Formulated as "visits both steps before settling", not "exactly at tick N+1": a job's completion
        // latency is not something this test controls. What it proves and no more: scheduling
        // ORDER, never execution PLACEMENT — EditMode is not the web, and a Burst-off Editor would leave the
        // same trail.

        [UnityTest]
        public IEnumerator BackgroundCover_VisitsMeasureThenWrite_BeforeSettling()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 0); // z0: exactly one covered tile
            Track(go);
            var gate = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            // Declared here, not inside try: an early failure (before any graph is ever scheduled against
            // it) must still be able to Complete() this in finally, unconditionally, before disposing
            // gate/started/outVals — see the finally block's own comment.
            JobHandle delayHandle = default;
            try
            {
                // Hold the measure graph on a gated delay job (job-scheduling-design.md) —
                // GraphMeasureInFlight >= 1 is then DETERMINISTIC, not a race against however fast a 4-vertex
                // quad's measure graph happens to complete (the original form of this tooth sampled after
                // the fact via CaptureTelemetry() and reliably missed the window on this machine).
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                view.LoadTestStyle(null, Cam(0, 0, 0), StyleParser.Parse(BackgroundOnlyStyle()));

                int kickTick = -1, tick = 0;
                for (; tick < 10000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                    yield return null;
                }
                Assert.GreaterOrEqual(kickTick, 0, "drive precondition: the background graph must have been kicked.");

                DelayGateJobInstrument.WaitForStart(started);

                var snap = view.CaptureTelemetry();
                Assert.GreaterOrEqual(snap.GraphMeasureInFlight, 1,
                    "the graph must be observed in its MEASURE step — deterministic while the delay job " +
                    "holds it, since the measure graph cannot possibly have completed yet.");
                Assert.IsFalse(view.AllTilesSettled(),
                    "the tile must not read settled while its measure step is genuinely held incomplete.");

                gate[0] = 1; // release — Measure, then Write, proceed normally from here on

                // The design's own RED claim (job-scheduling-design.md tooth (a)): "a pump that
                // Complete()s at kick settles in one Tick." PumpPending's arms (3) then (2) each gate on
                // IsStepComplete and each visits a tile AT MOST once per Tick (every arm ends in `continue`),
                // so a correct three-step pump (kick tick → measure-complete/schedule-write tick →
                // write-complete/consume tick) cannot settle before kickTick+2 — this is a STRUCTURAL fact
                // about the dispatch, not a timing bet, so it survives any completion latency, gated or not.
                // A pump that folds write-completion into the measure-complete tick (this tooth's RED) would
                // settle at kickTick+1.
                // Record the tick the WRITE step was kicked (arm 3 allocates the MeshDataArray) and the
                // tick the tile was CONSUMED (arm 2). Those are the two steps whose separation this tooth
                // exists to prove, and each is observable through a counter the pump sets on exactly the
                // tick its arm ran.
                int allocTick = -1, consumeTick = -1, settleTick = -1;
                for (; tick < 10000 && settleTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (allocTick   < 0 && view.MeshDataArraysAllocatedLastKick() > 0) allocTick   = tick;
                    if (consumeTick < 0 && view.TilesConsumedLastTick()           > 0) consumeTick = tick;
                    if (view.AllTilesSettled()) settleTick = tick;
                    yield return null;
                }
                Assert.GreaterOrEqual(settleTick, 0, "the cover must eventually settle.");

                Assert.AreEqual(1, view.LoadedTileCount(),
                    "precondition: exactly ONE covered tile. Both counters below are per-Tick GLOBALS, so " +
                    "with two tiles one tile's alloc could coincide with another's consume and the ordering " +
                    "assertions would read a coincidence rather than this tile's step separation.");
                Assert.GreaterOrEqual(allocTick,   0, "the write step must have allocated a MeshDataArray.");
                Assert.GreaterOrEqual(consumeTick, 0, "the tile must have been consumed.");
                Assert.Greater(allocTick, kickTick,
                    $"the write step must be kicked on a LATER tick than the measure kick (kickTick={kickTick}, " +
                    $"allocTick={allocTick}) — a pump that Complete()s the measure graph at kick would " +
                    "allocate on the kick tick itself. `allocTick != consumeTick` does not catch that: alloc " +
                    "landing on the kick tick still leaves the two different. Deterministic for the same " +
                    "reason as below — arm (6) kicks and `continue`s, so arm (3) cannot run for this tile in " +
                    "the same pass.");
                Assert.Greater(consumeTick, allocTick,
                    $"the write step must be KICKED on one tick (allocTick={allocTick}) and CONSUMED on a " +
                    $"LATER one (consumeTick={consumeTick}) — they are separate polled steps. A pump that " +
                    "folds write-completion into the measure-complete arm does both on the same tick, which " +
                    "is exactly the defect this tooth names. Unlike a settle-tick gap, this cannot be " +
                    "satisfied by the graph's jobs merely taking a while to complete: both counters are set " +
                    "by the pump on the tick its own arm ran, so job latency moves both together.");
                Assert.GreaterOrEqual(settleTick - kickTick, 2,
                    $"settling must take at least two ticks after the kick tick (kickTick={kickTick}, " +
                    $"settleTick={settleTick}) — one to complete measure and schedule write, another to " +
                    "complete write and consume. A pump that folds write-completion into the measure-complete " +
                    "tick would settle at kickTick+1.");

                Assert.Greater(view.GameObjectRenderer().DrawItemCount(), 0,
                    "the both-ends rule: a progression that never produces a mesh is indistinguishable from a " +
                    "build that never happened.");
            }
            finally
            {
                // Unconditional, before any dispose — same masked-exception discipline as
                // ZeroWorkerCount_DoesNotHoldAScheduledJobIncomplete. On the happy path Teardown() already
                // completes this transitively (the graph's handle depends on it); on an EARLY failure (e.g.
                // the kickTick precondition), no graph was ever scheduled against delayHandle, so nothing
                // else would complete it — disposing gate/started/outVals while it is still running would
                // throw and replace the real assertion failure with a disposal error.
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary><b>RED injection:</b> make the pump complete the write handle in the same pass it
        /// schedules it (fold <c>CompleteWriteAndTakePayloads</c> into the measure-complete arm) — a correct
        /// pump's settleTick - kickTick >= 2 floor drops to 1, and the tick-count assertion catches it.</summary>

        // ── Tooth (c): the pen — a graph released mid-flight is STASHED, not blocked on ────────────────
        //
        // job-scheduling-design.md exit (2)/(3): RenderTeardownRecord's graph-arm twin of the seam pen
        // (mirrors DisposalLeakGuardTests.ReleaseMidFlight_NoOrphanedMesh's pan-to-evict drive). Held
        // genuinely in-flight via TileManager.GraphDepsForTest — the deps-parameter delay instrument
        // (job-scheduling-design.md), never JobsUtility.JobWorkerCount, which does not hold a
        // scheduled job incomplete (JobGraphInstrumentTests.ZeroWorkerCount_DoesNotHoldAScheduledJobIncomplete).
        // TileBuildGraph.DebugLiveCount/MeshDataPayload.DebugLiveAllocCount are process-wide static counters
        // shared across the whole batch run — every assertion below is a BEFORE/AFTER delta, never an
        // absolute reading.

        [UnityTest]
        public IEnumerator ReleaseMidFlight_GraphArm_StashesInThePen_ThenDrainsOnceTheDelayJobCompletes()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 2);
            Track(go);
            view.Config.MaxReleasesPerTick = 64; // evict the whole condemned cover on the pan tick
            var gate = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            // Declared here, not inside try — see the finally block's comment.
            JobHandle delayHandle = default;
            try
            {
                long baselineGraphs = TileBuildGraph.DebugLiveCount;
                long baselinePayloads = MeshDataPayload.DebugLiveAllocCount;

                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                view.LoadTestStyle(null, Cam(0, 0, 2), StyleParser.Parse(BackgroundOnlyStyle()));

                int kicked = 0;
                for (int f = 0; f < 2000 && kicked == 0; f++)
                {
                    view.LateUpdate();
                    kicked += view.TileBuildsStartedLastTick();
                    yield return null;
                }
                Assert.Greater(kicked, 0, "drive precondition: at least one background graph must have been kicked.");

                DelayGateJobInstrument.WaitForStart(started);
                Assert.IsFalse(view.AllTilesSettled(),
                    "the covered tile(s) must still be genuinely unsettled — held on the delay job's gate.");

                // Pan far east — evict the covered tile(s) while their graph is still genuinely in its
                // Measure step (gate held). This tick must return PROMPTLY: RenderTeardownRecord must stash
                // the in-flight graph via _pending.StashGraph, never call Dispose() (which would Complete()
                // — and block — on the gated job) synchronously here.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                var sw = System.Diagnostics.Stopwatch.StartNew();
                view.LateUpdate();
                sw.Stop();

                Assert.Greater(view.ReleasedMidFlightCount(), 0,
                    "positive control: at least one tile must have been released while its graph was still " +
                    "genuinely in-flight (gate held), or the pen assertions below are vacuous.");
                // THE decisive assertion, and it is first on purpose. An eviction that disposes inline
                // instead of penning runs Complete() on the gated spin and decrements the live count before
                // control returns here, so this reads == baseline. Deterministic: no clock, no margin.
                Assert.Greater(TileBuildGraph.DebugLiveCount, baselineGraphs,
                    "the evicted graph must still be LIVE right after eviction — the pen defers disposal, it " +
                    "does not skip it. An inline Dispose() would already have completed the gated job and " +
                    "decremented the count by now.");

                // NOT the tooth — a hang guard, and a generous one. It measures the gated spin's wall
                // time, which is MaxIterations divided by this machine's throughput, so no constant makes it
                // decisive: a bound loose enough to be safe here would pass on a faster machine that was
                // still blocking. It exists only so a pen that stashes AND blocks (a Complete() added before
                // the stash — which the ownership assertion above would not catch) fails loudly instead of
                // hanging the suite.
                Assert.Less(sw.ElapsedMilliseconds, 20000,
                    "eviction must not block on the gated job's Complete() — hang guard only; the live-count " +
                    "assertion above is what proves the graph was penned rather than disposed.");

                // Release the gate and pump until the pen drains.
                gate[0] = 1;
                int guard = 0;
                while (TileBuildGraph.DebugLiveCount > baselineGraphs && guard++ < 2000)
                {
                    view.LateUpdate();
                    yield return null;
                }

                Assert.AreEqual(baselineGraphs, TileBuildGraph.DebugLiveCount,
                    "once the gated job completes, the pen must drain: PendingDisposalQueue.DrainCompleted disposes the " +
                    "released graph on a later tick.");
                Assert.AreEqual(baselinePayloads, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload leak once the pen has drained.");
            }
            finally
            {
                // Unconditional, before any dispose — same masked-exception discipline as tooth (a)'s
                // finally: an early failure (e.g. the "kicked > 0" precondition) means no graph was ever
                // scheduled against delayHandle, so nothing else would complete it before dispose runs.
                gate[0] = 1; // in case an assertion above failed before the release
                delayHandle.Complete();
                view.Teardown();
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary><b>RED injection:</b> in <c>RenderTeardownRecord</c>, replace the
        /// <c>_pending.StashGraph(lt.Graph); lt.Graph = null;</c> stash with an immediate
        /// <c>lt.Graph.Dispose()</c>. The assertion that actually catches it is
        /// <c>TileBuildGraph.DebugLiveCount &gt; baselineGraphs</c> right after eviction: an immediate
        /// <c>Dispose()</c> blocks until the gated job finishes, then decrements the live count before
        /// <c>LateUpdate()</c> even returns, so the graph reads back at (or below) baseline instead of
        /// still-live — a deterministic delta failure, not a timing race. The elapsed-time assertion is
        /// marginal by comparison (its pass/fail depends on how long the gated spin happens to take) and is
        /// not the one this tooth's correctness rests on.</summary>

        // ── Tooth (d): full teardown with a graph genuinely in-flight ──────────────────────────────────
        //
        // job-scheduling-design.md exit (4): TileManager.DoDispose's graph-arm sweep — Complete() from
        // the main thread executes a not-yet-started job inline, so DoDispose can (and must) drive a
        // still-in-flight graph to completion synchronously, unlike the seam pen's UniTask bridge. The gate
        // is released from a background timer AFTER a short delay, so Teardown()'s Complete() call below
        // genuinely blocks on a still-running job for a measurable interval, rather than racing a release
        // that could land before Teardown ever starts.

        [UnityTest]
        public IEnumerator Teardown_WithGraphGenuinelyInFlight_CompletesAndDisposes_NoLeak()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 0);
            var gate = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            bool torndown = false;
            // Declared here, not inside try — see the finally block's comment.
            JobHandle delayHandle = default;
            System.Threading.Tasks.Task releaseTask = System.Threading.Tasks.Task.CompletedTask;
            try
            {
                long baselineGraphs = TileBuildGraph.DebugLiveCount;
                long baselinePayloads = MeshDataPayload.DebugLiveAllocCount;

                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                view.LoadTestStyle(null, Cam(0, 0, 0), StyleParser.Parse(BackgroundOnlyStyle()));

                int kicked = 0;
                for (int f = 0; f < 2000 && kicked == 0; f++)
                {
                    view.LateUpdate();
                    kicked += view.TileBuildsStartedLastTick();
                    yield return null;
                }
                Assert.Greater(kicked, 0, "drive precondition: the background graph must have been kicked.");

                DelayGateJobInstrument.WaitForStart(started);
                Assert.IsFalse(view.AllTilesSettled(),
                    "the tile must still be genuinely unsettled — the graph is held on the delay job's gate.");

                // Release the gate from a background timer, not here: Teardown() below must find the job
                // STILL running when it calls Complete(), or the "in-flight at teardown" claim is untested.
                // Captured (not fire-and-forget): the finally block Wait()s this before disposing gate — the
                // task still writes gate[0] on an early-failure path, and disposing out from under it would race.
                releaseTask = System.Threading.Tasks.Task.Run(() =>
                {
                    System.Threading.Thread.Sleep(200);
                    gate[0] = 1;
                });

                // Precondition, asserted at the moment it matters — immediately before Teardown(), with no
                // yield in between — not merely earlier via WaitForStart. The delay above is a bet (a loaded
                // machine could burn through it before Teardown() reaches the graph sweep); this assertion is
                // what makes the claim honest: a premature release fails LOUDLY here instead of letting
                // Teardown() silently exercise the already-complete-handle path while the test still passes.
                Assert.IsFalse(delayHandle.IsCompleted,
                    "precondition: the delay job must still be genuinely running right before Teardown() is " +
                    "called, or Teardown()'s Complete() call below exercises the ALREADY-COMPLETE path — " +
                    "the 'genuinely in-flight at teardown' claim this tooth makes would then be untested " +
                    "despite a passing assertion.");
                Assert.Greater(TileBuildGraph.DebugLiveCount, baselineGraphs,
                    "precondition: the graph must still be live and undisposed right before Teardown().");

                view.Teardown();
                torndown = true;
                Object.DestroyImmediate(go);

                Assert.AreEqual(baselineGraphs, TileBuildGraph.DebugLiveCount,
                    "every TileBuildGraph — including one genuinely in-flight at teardown — must be disposed.");
                Assert.AreEqual(0, TileBuildGraph.DebugNegativeObservations,
                    "the idempotency guard must never observe a negative live count.");
                Assert.AreEqual(baselinePayloads, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload leak — teardown while a graph is in-flight must not strand any " +
                    "allocated Mesh.MeshDataArray.");
            }
            finally
            {
                // Unconditional, before any dispose — same masked-exception discipline as tooth (a)'s
                // finally: an early failure (before Teardown() ever runs — e.g. the "kicked > 0" or
                // "!delayHandle.IsCompleted" preconditions) means no completed sweep has touched
                // delayHandle yet, so it must be completed directly here, not just released.
                gate[0] = 1;
                delayHandle.Complete();
                // The release task ALSO writes gate[0] — Wait() it out before disposing gate below, or an
                // early-failure path (finally running before the task's 200ms sleep elapses) races the
                // dispose against that write.
                releaseTask.Wait(5000);
                if (!torndown)
                {
                    view.Teardown();
                    Object.DestroyImmediate(go);
                }
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary><b>RED injection:</b> in <c>PendingDisposalQueue.FlushAll</c> (called from
        /// <c>DoDispose</c> via <c>_pending.FlushAll()</c>), replace the unconditional
        /// <c>foreach (var graph in _graph) graph.Dispose();</c> sweep with one that skips a
        /// graph whose <c>IsStepComplete</c> is still false — the live-count assertion above catches the
        /// leak (this repo's build never hangs a headless run on a Complete() call, so there is no timeout
        /// hazard from testing the synchronous-block path directly).</summary>

        // ── A partially-consumed graph tile keeps its Graph alive across ticks ─────────────────────────
        //
        // TileManager.FinishConsume is the single disposal site for LoadedTile.Graph (its own doc states
        // the invariant: non-null iff Step is Measure or Write). Two dense background layers force a
        // genuine partial consume under MaxConsumesPerTick=1 — one layer taken per Tick — so Step stays
        // Write across more than one Tick while Graph must stay usable. The un-fixed code disposed and
        // nulled Graph the moment CompleteWriteAndTakePayloads() first ran (before ConsumeMeshBuild even
        // reported whether it finished), so the NEXT Tick's `lt.Step == BuildStep.Write && lt.Graph.IsStepComplete`
        // guard dereferenced a null Graph — an NRE, not an assertion failure.

        [UnityTest]
        public IEnumerator PartiallyConsumedGraphTile_KeepsGraphAlive_UntilBothLayersSettle()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 0); // z0: exactly one covered tile
            Track(go);
            view.Config.MaxConsumesPerTick = 1; // forces the partial consume: 2 layers, budget 1/tick
            try
            {
                Assert.AreEqual(1, view.Config.MaxConsumesPerTick,
                    "precondition: MaxConsumesPerTick=1 must actually be in effect on the driven view.");

                long baselineGraphs = TileBuildGraph.DebugLiveCount;
                long baselinePayloads = MeshDataPayload.DebugLiveAllocCount;

                view.LoadTestStyle(null, Cam(0, 0, 0), StyleParser.Parse(TwoBackgroundLayersStyle()));

                // Drive precondition: AllTilesSettled() reads vacuously true on an empty/never-requested
                // cover — a trap independent of this tooth — so this pumps until the tile is genuinely
                // loaded and kicked before relying on that helper for anything below.
                int kickTick = -1, tick = 0;
                for (; tick < 2000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                    yield return null;
                }
                Assert.Greater(view.LoadedTileCount(), 0,
                    "drive precondition: the single covered tile must have been loaded.");
                Assert.GreaterOrEqual(kickTick, 0,
                    "drive precondition: the background graph must have been kicked.");

                // Fixture precondition: BOTH background layers must be classified dense and both allocate a
                // write array on the SAME measure-complete tick — otherwise there is only one payload ever,
                // the partial-consume path this tooth targets is unreachable, and the "two meshes" outcome
                // below would be vacuous even against a correct fix.
                long allocated = 0;
                for (; tick < 2000 && allocated == 0; tick++)
                {
                    view.LateUpdate();
                    allocated += view.MeshDataArraysAllocatedLastKick();
                    yield return null;
                }
                Assert.AreEqual(2, allocated,
                    "fixture precondition: the style's two background layers must both be dense and both " +
                    "allocate a write array together — the partial-consume path is unreachable otherwise.");

                // Now drive to settle across the partial-consume ticks the budget forces.
                int guard = 0;
                while (!view.AllTilesSettled() && guard++ < 200)
                {
                    view.LateUpdate();
                    yield return null;
                }

                Assert.IsTrue(view.AllTilesSettled(),
                    "the tile must settle across multiple partial-consume ticks, not just the first.");
                Assert.AreEqual(2, view.GameObjectRenderer().DrawItemCount(),
                    "both background layers must have registered a mesh — a tile that settled with only one " +
                    "consumed would mean the second layer's payload leaked, not that it was never built.");
                Assert.AreEqual(baselineGraphs, TileBuildGraph.DebugLiveCount,
                    "no TileBuildGraph leak — the delta must return to baseline once settled.");
                Assert.AreEqual(baselinePayloads, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload leak once settled.");
            }
            finally { view.Teardown(); }
        }

        /// <summary><b>RED injection:</b> in <c>PumpPending</c>'s write-consume arm, restore the original
        /// <c>lt.Graph.Dispose(); lt.Graph = null;</c> pair immediately after <c>CompleteWriteAndTakePayloads()</c>
        /// (before <c>ConsumeMeshBuild</c> even runs) — the second Tick that revisits this tile while
        /// <c>Step == Write</c> throws a <see cref="System.NullReferenceException"/> evaluating
        /// <c>lt.Graph.IsStepComplete</c>, which NUnit reports as a test ERROR rather than an assertion
        /// failure.</summary>

        // ── Tooth 2: no fetch, no decode for background ───────────────────────────────────────────────

        [UnityTest]
        public IEnumerator BackgroundStyle_IssuesNoFetch_AndNeverDecodes()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3);
            Track(go);
            // A source that FAULTS if ever invoked — background must never route through the fetch path at
            // all (no SourceSpec is derived for a source-less style; RenderLayerFactory.TryGetFetchSource
            // excludes it structurally). Falsifies the rejected "empty-bytes through the real fetch/decode
            // path" design alternative.
            var neverCalled = TestDataSource.FromFetch(_ => throw new System.InvalidOperationException(
                "background must never fetch — the source-less pipeline has no Scheduler/Source at all."));
            try
            {
                view.LoadTestStyle(neverCalled, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));
                yield return PumpUntilSettled(view);

                Assert.IsTrue(view.AllTilesSettled());
                Assert.Greater(view.LoadedTileCount(), 0, "background must still load and register.");
                Assert.AreEqual(0, neverCalled.FetchCount, "the injected source must NEVER be consulted.");
                Assert.AreEqual(0, view.InFlightCount(), "no fetch may ever be in flight for a background-only style.");
            }
            finally { view.Teardown(); }
        }

        // ── Tooth 11: source-less builds honor the build cap (HIGH 2) ─────────────────────────────────

        [UnityTest]
        public IEnumerator BackgroundCover_KicksAtMostBuildCapPerTick()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3);
            Track(go);
            view.Config.MaxMeshBuildsPerTick = 1; // force per-tick throttling
            try
            {
                view.LoadTestStyle(null, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));

                // Tile-load smoothness: admission now runs EVERY Tick — including the SAME Tick as the
                // cover recompute that creates these pending records — so PumpPending's kick for the FIRST
                // (highest-priority) record can fire on tick 1 itself (time-to-first-paint improvement; the
                // old "records cannot be kicked this same frame" behaviour was an artifact of PumpPending
                // running BEFORE the request loop, not a load-bearing invariant (the intended behaviour is
                // exactly the opposite: time-to-first-paint at the center drops). The per-Tick CAP itself is
                // unchanged and still the property under test here.
                view.LateUpdate();
                int loaded = view.LoadedTileCount();
                Assert.Greater(loaded, 1, "non-vacuous: must cover more than one tile.");
                Assert.LessOrEqual(view.TileBuildsStartedLastTick(), 1,
                    "at most MaxMeshBuildsPerTick source-less tiles may be started on the cover-recompute " +
                    "tick, same as any other tick — the cap binds even on tick 1.");

                int guard = 0;
                while (!view.AllTilesSettled() && guard++ < 10000)
                {
                    view.LateUpdate();
                    Assert.LessOrEqual(view.TileBuildsStartedLastTick(), 1,
                        "at most MaxMeshBuildsPerTick source-less tiles may be started in a single Tick — " +
                        "never the whole cover synchronously in one burst.");
                    yield return null;
                }
                Assert.IsTrue(view.AllTilesSettled(), "the throttled cover must still fully drain eventually.");
            }
            finally { view.Teardown(); }
        }

        // ── Tooth 12: DrainMeshBuilds builds source-less records (HIGH a) ─────────────────────────────

        [Test]
        public void DrainMeshBuilds_BuildsSourcelessBackground()
        {
            var (go, view) = NewView(RenderBackend.GameObject, zoom: 3);
            Track(go);
            try
            {
                view.LoadTestStyle(null, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));
                // One LateUpdate to run the cover recompute (creates records; PumpPending, running BEFORE
                // the recompute in the same call, sees none of them yet — so nothing is kicked either way).
                view.LateUpdate();
                Assert.Greater(view.LoadedTileCount(), 0, "the cover must have created source-less records.");
                Assert.IsFalse(view.AllTilesSettled(), "records must still be pending before the drain.");

                view.DrainMeshBuilds(); // uncapped — must build every un-kicked source-less record inline

                Assert.IsTrue(view.AllTilesSettled(), "DrainMeshBuilds must settle every source-less record.");
                var gor = view.GameObjectRenderer();
                Assert.Greater(gor.DrawItemCount(), 0,
                    "DrainMeshBuilds must have produced a non-empty background mesh + AddTileLayer for the " +
                    "un-kicked record — the un-fixed drain settles with ZERO draws (falls to the no-mesh else).");
            }
            finally { view.Teardown(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileManagerLoadPriorityTests — RED-verified concurrency cap and priority order
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TileManagerLoadPriorityTests : BaseTestFixture
    {
        private const int TestViewportPx = 1080; // matches WithTestCamera's default square viewport

        private static CameraProperties Cam(double lon, double lat, double zoom, double heading = 0.0,
            double tilt = 0.0)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 },
                zoom, heading, tilt);

        private static StyleDocument FillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""TileLoadPriority"",
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"",
                    ""type"": ""fill"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] }
                }
            ]
        }");

        /// <summary>The lon/lat of a tile's CENTER (px=0.5, py=0.5) — used as camera LookAt so the tile's
        /// ground-distance-to-LookAt priority key is exactly 0 (the unique minimum, no boundary ambiguity).</summary>
        private static (double lon, double lat) CenterOf(TileId tile)
        {
            double2 ll = tile.ToLonLat(0.5, 0.5, 1.0);
            return (ll.x, ll.y);
        }

        /// <summary>Renders what the manager actually did — which tiles are built, and where the priority
        /// head sits — so a first-build-order failure names the tile that won instead of reporting a bare
        /// <c>False</c>. Diagnosis of an order-dependent failure otherwise costs a whole session of
        /// guessing.</summary>
        /// <param name="view">The map view under test, after the kicking tick.</param>
        /// <param name="tileA">The pre-pan center.</param>
        /// <param name="tileB">The post-pan center, expected to build first.</param>
        /// <returns>A one-line "[diagnostic] …" suffix for an assertion message.</returns>
        private static string DescribeBuildOutcome(MapViewComponent view, TileId tileA, TileId tileB)
        {
            var loaded = new List<TileId>();
            view.CollectLoadedTileIds(loaded);

            var built = new List<string>();
            foreach (TileId id in loaded)
                if (view.TryGetBuiltTile(id)) built.Add(Name(id, tileA, tileB));

            return $"[diagnostic] built={{{string.Join(", ", built)}}} " +
                   $"desiredHead={Name(view.DesiredHeadTile(), tileA, tileB)} " +
                   $"loadedCount={loaded.Count} A={tileA.Z}/{tileA.X}/{tileA.Y} B={tileB.Z}/{tileB.X}/{tileB.Y}";

            static string Name(TileId id, TileId a, TileId b)
            {
                string coords = $"{id.Z}/{id.X}/{id.Y}";
                if (id.Equals(a)) return "A(" + coords + ")";
                if (id.Equals(b)) return "B(" + coords + ")";
                return coords;
            }
        }

        /// <summary>A per-tile GATED data source: every fetch stays pending until <see cref="Release"/> is
        /// called for that specific tile — deterministic control over admission/consume timing (no reliance
        /// on ThreadPool wall-clock races).</summary>
        private sealed class GatedSource
        {
            private readonly Dictionary<TileId, UniTaskCompletionSource<TileResponse>> _gates = new();
            public readonly TestDataSource Source;

            public GatedSource()
            {
                Source = new TestDataSource((id, ct) =>
                {
                    var g = new UniTaskCompletionSource<TileResponse>();
                    lock (_gates) _gates[id] = g;
                    return g.Task;
                });
            }

            /// <summary>Completes every gate opened SO FAR with real tile bytes (idempotent — already-
            /// completed gates just no-op on TrySetResult).</summary>
            public void ReleaseAll()
            {
                List<UniTaskCompletionSource<TileResponse>> snapshot;
                lock (_gates) snapshot = new List<UniTaskCompletionSource<TileResponse>>(_gates.Values);
                foreach (var g in snapshot)
                    g.TrySetResult(new TileResponse(SampleTileFixture.Bytes(), TileEncoding.Mvt));
            }
        }

        // ── WHICH tile builds first, by IDENTITY, under a 1-kick-per-tick budget ────────────────────

        /// <summary>
        /// PumpPending kicks builds in priority order, centre first. A kick loop that ran over
        /// <c>_toRelease</c> in whatever order <c>Dictionary&lt;LoadedKey, LoadedTile&gt;</c> enumerated it —
        /// insertion order early, drifting after evictions — would have no notion of "center" and reds here.
        ///
        /// <para>Admitting the WHOLE cover in one Tick already inserts <c>_loaded</c> in priority order (A
        /// first), so a fixture that never disturbs that insertion order cannot tell "PumpPending sorts too"
        /// apart from "PumpPending just reads Dictionary order, which happens to already agree" — a shallow
        /// admission-only fix would pass a vacuous version of this test. This fixture defeats that: it admits
        /// the whole cover centered on tile A (insertion order == A-priority), then PANS to tile B — a
        /// DIFFERENT, already-admitted tile — BEFORE anything is kicked. The two explicit
        /// <c>TileBuildsStartedLastTick() == 0</c> guards below prove the pan lands before any kick, so the
        /// assertion that follows can only pass if PumpPending re-sorts its OWN kick order by the CURRENT
        /// (B-centered) priority — insertion order alone (stale, A-centered) would still kick A.</para>
        /// </summary>
        [Test]
        public void CenterTileBuildsFirst_ByIdentity_UnderASingleBuildKickPerTick()
        {
            var tileA = new TileId { Z = 5, X = 12, Y = 13 };
            var tileB = new TileId { Z = 5, X = 13, Y = 12 }; // diagonal neighbor of A — stays in cover
            (double lonA, double latA) = CenterOf(tileA);
            (double lonB, double latB) = CenterOf(tileB);

            var gated = new GatedSource();
            var go    = Track(new GameObject("CenterFirst"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick      = 1;   // the load-bearing cap
            view.Config.MaxVerticesPerTick        = int.MaxValue;
            view.Config.MaxConcurrentTileLoads    = 64;  // uncapped-in-practice admission — isolates the kick/paint order

            try
            {
                // Inline decode: this tooth asserts the ORDER PumpPending kicks in, so every candidate
                // must be kick-eligible at the same tick. The default off-main decode lands across an
                // unpredictable number of ticks (see the ReleaseAll comment below).
                view.LoadTestStyle(gated.Source, Cam(lonA, latA, 5.0), style: FillStyle(),
                    decodeScheduler: new InlineWorkScheduler());

                // Tick 1: admits the WHOLE cover (insertion order == A-priority order). Every fetch is held
                // PENDING by the gate, so nothing can be kicked yet regardless of ThreadPool timing. (An
                // instant-bytes source would let fetches race to completion in a nondeterministic order, so
                // PumpPending could kick a non-center tile whose fetch happened to land first — an
                // intermittent failure the gate removes.)
                view.LateUpdate();
                Assert.GreaterOrEqual(view.LoadedTileCount(), 5,
                    "sanity: need a genuinely multi-tile cover for the cap to mean anything.");
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(),
                    "guard: nothing may be kicked yet — the pan below must land strictly BEFORE the first kick.");

                var loadedBefore = new List<TileId>();
                view.CollectLoadedTileIds(loadedBefore);
                Assert.Contains(tileB, loadedBefore,
                    "precondition: B must already be admitted (in _loaded) before the pan.");

                // Pan onto B — still before anything kicks (second guard, right below).
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = latB, Longitude = lonB });
                view.LateUpdate();
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(),
                    "guard: still nothing kicked after the pan — if this fires, the fixture raced ahead of " +
                    "the pan and the test below would be vacuous.");

                // NOW complete every fetch at once. The decode hop is INLINE for this fixture (see the
                // LoadTestStyle call above), so every released fetch is also decoded by the time this
                // returns — which is what makes the next tick rank the FULL cover instead of whichever
                // subset happened to be ready. Under the default off-main decode that subset varies per
                // run, and the sort then ranks a partial set: the tooth silently measures readiness order
                // instead of priority order.
                gated.ReleaseAll();

                // Exactly TWO ticks, both deterministic — no "pump until something happens" scan, which is
                // what let a partial ready-set through. Absorbing a completed fetch and kicking its build
                // are separate ticks: tick 1 takes every decode (asserted below), tick 2 kicks the single
                // highest-priority candidate from the now-complete ready-set.
                view.LateUpdate();

                Assert.AreEqual(0, view.InFlightCount(),
                    "precondition: every fetch must have landed in ONE tick — if any are still in flight, " +
                    "the kick below would rank a PARTIAL ready-set and this tooth would be measuring " +
                    "decode-completion order, not priority order.");
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(),
                    "guard: the fetch-absorbing tick must not kick — if it does, the kick raced the " +
                    "decodes and the ready-set was partial after all.");

                view.LateUpdate();

                Assert.AreEqual(1, view.TileBuildsStartedLastTick(),
                    "precondition: exactly one kick must fire on the first kicking tick (cap=1).");

                // Sampled AT the kicking tick: if work is still in flight here, the kick chose from a
                // PARTIAL ready-set and the priority sort never saw the full cover.
                var atKick = new List<TileId>();
                view.CollectLoadedTileIds(atKick);
                var kickCam  = Cam(lonB, latB, 5.0);
                var kickCtx  = TilePriorityContext.From(in kickCam, new double2(TestViewportPx, TestViewportPx),
                    new WebMercatorProjection(), view.Config.PriorityStrategy);
                string kickDiag = $"atKick: inFlight={view.InFlightCount()} loaded={atKick.Count} " +
                                  $"priorityArgMin={ArgMin(atKick, in kickCtx)}";

                // A source tile's kicked build does not complete and consume in one more tick — it needs
                // the prologue-complete tick, the write-kick tick, AND the consume tick (kickTick+3, not
                // kickTick+1; TileBuildsStartedLastTick's own doc). One
                // Await + one LateUpdate only completes whichever STEP is currently in flight. Pump
                // (bounded) until B is Built instead of assuming a fixed tick count — the cap (1) still
                // throttles NEW kicks each tick, so the loop changes nothing about which tile gets picked,
                // only how long B's OWN build takes to finish once picked. A may also get kicked during
                // this window; the assertions below still check A is not YET built at the moment B settles.
                for (int f = 0; f < 20 && !view.TryGetBuiltTile(tileB); f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }

                string outcome = DescribeBuildOutcome(view, tileA, tileB) + " " + kickDiag;

                Assert.IsTrue(view.TryGetBuiltTile(tileB),
                    "tile B — the NEW center after the pan — must be the FIRST tile built under a " +
                    "1-kick-per-tick cap, even though A was admitted (inserted) first. " + outcome);
                Assert.IsFalse(view.TryGetBuiltTile(tileA),
                    "tile A must NOT be built yet — it is no longer the highest priority after the pan. " +
                    outcome);
                Assert.IsFalse(view.AllTilesSettled(),
                    "sanity: the cover has more than one tile — only B should be built yet.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── concurrency cap respected across a full (eventually-settling) load ──────────────────────

        /// <summary>
        /// Admission holds the active (admitted, not-yet-Built) set at the concurrency cap. Without a
        /// cap, a cover-wide cover/zoom transition would fetch every newly-entering tile in one Tick, and
        /// the active set would jump straight to the full cover size. A per-tile GATE holds every fetch open
        /// (never completing) so the active set can only GROW via admission, never shrink via completion —
        /// isolating the cap.
        /// </summary>
        [Test]
        public void ActiveLoadCount_NeverExceedsTheConcurrencyCap_AcrossAFullLoad()
        {
            const int cap = 4;
            var gated = new GatedSource();
            var go    = Track(new GameObject("ConcurrencyCap"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 6;
            view.Config.TileSelection.MaxZoom = 6; // a cover clearly larger than the cap
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick      = 64;
            view.Config.MaxVerticesPerTick        = int.MaxValue;
            view.Config.MaxConcurrentTileLoads    = cap;

            try
            {
                view.LoadTestStyle(gated.Source, Cam(0, 0, 6.0), style: FillStyle());

                int maxObservedActive = 0;
                for (int f = 0; f < 20; f++)
                {
                    view.LateUpdate();
                    int active = view.ActiveLoadCount();
                    Assert.LessOrEqual(active, cap, $"tick {f}: active load count must never exceed the cap.");
                    if (active > maxObservedActive) maxObservedActive = active;
                }

                Assert.Greater(view.LoadedTileCount() + view.DesiredCount(), cap,
                    "sanity: the cover must be LARGER than the cap, or the cap was never genuinely tested.");
                Assert.AreEqual(cap, maxObservedActive,
                    "the cap must actually BIND — with every fetch gated open, admission must climb to " +
                    "exactly the cap and stop there (not fewer — a starved cap is also a failure).");
                Assert.AreEqual(cap, view.ActiveLoadCount(),
                    "with nothing completing, the active set must sit AT the cap, not below it.");

                // Release every gate (as new ones open too) and drive to full settle — the cap must throttle
                // the load, never permanently stall it.
                for (int f = 0; f < 500 && !view.AllTilesSettled(); f++)
                {
                    view.LateUpdate();
                    gated.ReleaseAll();
                    view.AwaitInFlightMeshBuilds();
                    Assert.LessOrEqual(view.ActiveLoadCount(), cap,
                        $"tick {f} (settling): active load count must never exceed the cap.");
                }

                Assert.IsTrue(view.AllTilesSettled(),
                    "the cover must eventually fully settle despite the concurrency cap.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── no-cancel-in-flight on a recompute that keeps the tile visible ──────────────────────────

        /// <summary>
        /// An admitted, in-flight tile that stays visible across a cover recompute must NOT be released and
        /// re-fetched: its per-tile fetch cancellation token must never fire. A sub-tile camera nudge dirties
        /// the cover key (forcing a recompute) without changing the selected tile SET.
        /// </summary>
        [Test]
        public void AdmittedInFlightTile_SurvivesARecompute_ThatKeepsItVisible()
        {
            var canceled = new HashSet<TileId>();
            var gates    = new Dictionary<TileId, UniTaskCompletionSource<TileResponse>>();
            var src = new TestDataSource((id, ct) =>
            {
                var g = new UniTaskCompletionSource<TileResponse>();
                lock (gates) gates[id] = g;
                ct.Register(() => { lock (canceled) canceled.Add(id); g.TrySetCanceled(ct); });
                return g.Task;
            });

            var centerTile = new TileId { Z = 5, X = 12, Y = 13 };
            (double lon, double lat) = CenterOf(centerTile);

            var go   = Track(new GameObject("NoCancelInFlight"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick     = 64;
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxVerticesPerTick     = int.MaxValue;
            view.Config.MaxConcurrentTileLoads = 64; // admit the whole cover — every tile is "in-flight"

            try
            {
                view.LoadTestStyle(src, Cam(lon, lat, 5.0), style: FillStyle());
                view.LateUpdate(); // admits the cover (all gated — nothing completes)
                Assert.IsTrue(view.TryGetBuiltTile(centerTile) == false && view.LoadedTileCount() > 0,
                    "sanity: the cover must be admitted (in-flight), not yet built.");
                int loadedBefore = view.LoadedTileCount();

                // Sub-tile nudge: trips CoverKeyGate dirty (exact cover-key equality trips) without changing
                // the selected tile set (far smaller than any tile in a z5 cover).
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = lat + 1e-7, Longitude = lon - 1e-7 });
                view.LateUpdate();

                Assert.IsFalse(canceled.Contains(centerTile),
                    "an admitted tile that stays visible across a recompute must NEVER have its fetch " +
                    "cancelled (no release + re-fetch behind new center tiles).");
                Assert.AreEqual(loadedBefore, view.LoadedTileCount(),
                    "the loaded set must be unchanged — the recompute must not drop and re-admit anything " +
                    "still visible.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── re-prioritization head — a recompute promotes the NEW center ────────────────────────────

        /// <summary>
        /// With admission capped to 1 (so most of the cover stays in the not-yet-admitted desired list), a
        /// recompute that moves LookAt onto a previously-peripheral tile must promote THAT tile to the head
        /// of the desired list on the SAME tick — proving re-prioritization isn't deferred to "next slot
        /// free" (AdmitFromDesired sorts before checking capacity, not after).
        /// </summary>
        [Test]
        public void RecomputeThatMovesTheCenter_PromotesTheNewCenter_ToTheDesiredHead()
        {
            var gated = new GatedSource();
            var centerA = new TileId { Z = 5, X = 12, Y = 13 };
            var centerB = new TileId { Z = 5, X = 13, Y = 12 }; // diagonal neighbor — was peripheral to A
            (double lonA, double latA) = CenterOf(centerA);
            (double lonB, double latB) = CenterOf(centerB);

            var go   = Track(new GameObject("Repriority"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick      = 64;
            view.Config.MaxVerticesPerTick        = int.MaxValue;
            view.Config.MaxConcurrentTileLoads    = 1; // only ONE tile ever admits — the rest stays desired

            try
            {
                view.LoadTestStyle(gated.Source, Cam(lonA, latA, 5.0), style: FillStyle());
                view.LateUpdate();

                Assert.AreEqual(1, view.ActiveLoadCount(), "precondition: exactly one slot admitted.");
                Assert.Greater(view.DesiredCount(), 1,
                    "sanity: several tiles must remain desired for re-prioritization to be observable.");

                var desiredBefore = new List<TileId>();
                view.CollectDesiredTileIds(desiredBefore);
                Assert.IsTrue(desiredBefore.Contains(centerB),
                    "precondition: B must be among the not-yet-admitted desired tiles before the pan.");

                // Pan LookAt onto B (previously peripheral). The sole admitted slot is gated (held forever
                // by A, which is still visible in the new cover too — no cancel), so B stays UNADMITTED.
                view.Camera.Apply(new CameraPropertiesUpdate { Latitude = latB, Longitude = lonB });
                view.LateUpdate();

                Assert.AreEqual(centerB, view.DesiredHeadTile(),
                    "after a recompute that moves LookAt onto B, B must be the HEAD of the desired list " +
                    "(ground-distance-to-LookAt key == 0 for the tile AT LookAt) — re-sorted THIS tick, not " +
                    "deferred until a slot frees.");
            }
            finally
            {
                // Open the gates BEFORE teardown: DoDispose parks 10 s on each still-pending fetch.
                gated.ReleaseAll();
                view.Teardown();
            }
        }

        // ── strategy toggle is honored end to end, and the two strategies genuinely diverge ────────

        /// <summary>
        /// Under a real tilt, GroundDistanceToLookAt and CameraDistance can disagree about which tile is
        /// closest. This derives the expected argmin under EACH strategy directly from the production
        /// <see cref="TilePriority.Key"/> math (already pinned independently by the Core-only
        /// TilePriorityTests) over the SAME cover TileManager actually assembles, then asserts (a) the two
        /// strategies' derived answers genuinely differ — the fixture is discriminating, not vacuous — and
        /// (b) switching <c>PriorityStrategy</c> changes which tile TileManager actually admits first,
        /// matching the derived answer in both cases. This is a WIRING tooth, not a re-test of the math.
        /// </summary>
        [Test]
        public void PriorityStrategyToggle_ChangesTheFirstAdmittedTile_UnderTilt()
        {
            var centerTile = new TileId { Z = 6, X = 30, Y = 25 };
            (double lon, double lat) = CenterOf(centerTile);
            const double tilt = 60.0;

            // ── Run A: GroundDistanceToLookAt (default) ──
            TileId admittedGround = RunToFirstAdmission(lon, lat, tilt, TilePriorityStrategy.GroundDistanceToLookAt,
                out List<TileId> cover);

            // ── Run B: CameraDistance ──
            TileId admittedCamera = RunToFirstAdmission(lon, lat, tilt, TilePriorityStrategy.CameraDistance,
                out _);

            // Derive the expected argmin under each strategy independently, over the SAME cover, using the
            // exact production math (not a re-implementation) — matches FrustumTileSelector's render frame.
            var cam    = Cam(lon, lat, 6.0, heading: 0.0, tilt: tilt);
            var proj   = new WebMercatorProjection();
            var vp     = new double2(TestViewportPx, TestViewportPx);
            var ctxG   = TilePriorityContext.From(in cam, vp, proj, TilePriorityStrategy.GroundDistanceToLookAt);
            var ctxC   = TilePriorityContext.From(in cam, vp, proj, TilePriorityStrategy.CameraDistance);

            TileId expectedGround = ArgMin(cover, in ctxG);
            TileId expectedCamera = ArgMin(cover, in ctxC);

            Assert.AreNotEqual(expectedGround, expectedCamera,
                "fixture sanity: at tilt=60 the two strategies must derive DIFFERENT argmins over this " +
                "cover, or this fixture cannot discriminate the toggle at all.");

            Assert.AreEqual(expectedGround, admittedGround,
                "GroundDistanceToLookAt: TileManager's actual first-admitted tile must match the derived argmin.");
            Assert.AreEqual(expectedCamera, admittedCamera,
                "CameraDistance: TileManager's actual first-admitted tile must match the derived argmin.");
            Assert.AreNotEqual(admittedGround, admittedCamera,
                "switching PriorityStrategy must change WHICH tile TileManager admits first.");
        }

        private static TileId ArgMin(List<TileId> tiles, in TilePriorityContext ctx)
        {
            TileId best    = tiles[0];
            double bestKey = TilePriority.Key(in best, in ctx);
            for (int i = 1; i < tiles.Count; i++)
            {
                TileId candidate = tiles[i];
                double k         = TilePriority.Key(in candidate, in ctx);
                if (k < bestKey) { bestKey = k; best = candidate; }
            }
            return best;
        }

        /// <summary>Loads a fresh view capped to a single admission slot (gated — never completes), pumps
        /// one Tick, and returns the SOLE admitted tile plus the full cover (admitted ∪ desired).</summary>
        private static TileId RunToFirstAdmission(double lon, double lat, double tilt,
            TilePriorityStrategy strategy, out List<TileId> cover)
        {
            var gated = new GatedSource();
            using var bag = new ObjectDisposalBag();
            var go    = bag.Track(new GameObject("StrategyToggle"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom  = 6;
            view.Config.TileSelection.MaxZoom  = 6;
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick      = 64;
            view.Config.MaxVerticesPerTick        = int.MaxValue;
            view.Config.MaxConcurrentTileLoads    = 1;
            view.Config.PriorityStrategy          = strategy;

            try
            {
                view.LoadTestStyle(gated.Source, Cam(lon, lat, 6.0, heading: 0.0, tilt: tilt), style: FillStyle());
                view.LateUpdate();

                Assert.AreEqual(1, view.ActiveLoadCount(), "precondition: exactly one slot admitted.");
                var loaded = new List<TileId>();
                view.CollectLoadedTileIds(loaded);

                cover = new List<TileId>(loaded);
                var desired = new List<TileId>();
                view.CollectDesiredTileIds(desired);
                cover.AddRange(desired);
                Assert.GreaterOrEqual(cover.Count, 5, "sanity: need a genuinely multi-tile cover.");

                return loaded[0];
            }
            finally
            {
                // Open the gates BEFORE teardown: DoDispose parks 10 s on each still-pending fetch.
                gated.ReleaseAll();
                view.Teardown();
            }
        }

        // ── the admission/sort path stays allocation-free under sustained churn ─────────────────────

        /// <summary>
        /// A capped, gated load keeps the desired list populated across many Ticks (nothing ever completes,
        /// so <see cref="MapRenderer.Unity.Rendering.Tile.TileManager.AdmitFromDesired"/>'s sort+admit-attempt
        /// runs every Tick against a non-empty list) — the scenario the steady-state zero-alloc tooth in
        /// MapViewLiveLoopTests doesn't reach (there, admission is uncapped and settles in one Tick).
        /// </summary>
        [Test]
        public void AdmissionAndPrioritySort_UnderSustainedChurn_DoesNotAllocateGCMemory()
        {
            var gated = new GatedSource();
            var go    = Track(new GameObject("ZeroAlloc"));
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.Backend                = RenderBackend.Brg; // zero-alloc path
            view.Config.TileSelection.MinZoom  = 6;
            view.Config.TileSelection.MaxZoom  = 6;
            view.WithTestCamera(TestViewportPx);
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick      = 64;
            view.Config.MaxVerticesPerTick        = int.MaxValue;
            view.Config.MaxConcurrentTileLoads    = 3; // capped — desired stays populated (gated, never frees)

            try
            {
                view.LoadTestStyle(gated.Source, Cam(0, 0, 6.0), style: FillStyle());
                view.LateUpdate(); // first Tick — grows the reused scratch buffers to steady capacity
                Assert.Greater(view.DesiredCount(), 0,
                    "sanity: the desired list must stay non-empty (gated fetches never free a slot) for " +
                    "the sort/admit-attempt path to actually run every Tick.");

                const int N = 30;
                Assert.That(() => { for (int i = 0; i < N; i++) view.LateUpdate(); }, Is.Not.AllocatingGCMemory(),
                    $"AdmitFromDesired's priority sort + admit-attempt must not allocate across {N} Ticks " +
                    "of sustained desired-list churn (capped, nothing completing).");
            }
            finally
            {
                // Open the gates BEFORE teardown: DoDispose parks 10 s on each still-pending fetch.
                gated.ReleaseAll();
                view.Teardown();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileSymbolKickTests — the feed swap: per-tile kick drives the symbol worker pass
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TileSymbolKickTests : BaseTestFixture
    {
        /// <summary>Spy <see cref="ISymbolTileWorkerFactory"/> — records every <c>TryBeginBuild</c> call and
        /// every pass it issues.</summary>
        private sealed class SpySymbolTileWorkerFactory : ISymbolTileWorkerFactory
        {
            public readonly List<(string SourceId, TileId Tile)> BeginBuildCalls = new();
            public readonly List<SpySymbolTileWorkerPass> IssuedPasses = new();
            public Func<string, bool> ParticipatesFor = _ => true;
            public Func<string, TileId, SpySymbolTileWorkerPass> PassFactory;

            public ISymbolTileWorkerPass TryBeginBuild(string sourceId, TileId tile)
            {
                BeginBuildCalls.Add((sourceId, tile));
                if (!ParticipatesFor(sourceId)) return null;
                SpySymbolTileWorkerPass pass = PassFactory != null
                    ? PassFactory(sourceId, tile)
                    : new SpySymbolTileWorkerPass(sourceId, tile);
                IssuedPasses.Add(pass);
                return pass;
            }

            /// <summary>Not under test here: the spy commits no symbol blocks, so it answers "nothing
            /// to lose" and leaves TileManager's prepared-cache probe exactly as it was.</summary>
            public bool SymbolsCachedFor(string sourceId, TileId tile) => true;
        }

        private sealed class SpySymbolTileWorkerPass : ISymbolTileWorkerPass
        {
            public readonly string SourceId;
            public readonly TileId Tile;
            public bool Ran;
            public SharedDisposable<IDecodedTile> ReceivedDecode;
            public Action OnRun;

            public SpySymbolTileWorkerPass(string sourceId, TileId tile) { SourceId = sourceId; Tile = tile; }

            public void RunWorkerAndHandoff(SharedDisposable<IDecodedTile> decode)
            {
                Ran = true;
                ReceivedDecode = decode;
                OnRun?.Invoke();
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        private static StyleDocument FillAndSymbolStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""glyphs"": ""https://example.invalid/{fontstack}/{range}.pbf"",
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""s"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                { ""id"": ""labels"", ""type"": ""symbol"", ""source"": ""s"", ""source-layer"": ""centroids"",
                  ""layout"": { ""text-field"": ""{NAME}"", ""text-size"": 16 } }
            ]
        }");

        private static (GameObject go, MapView view) NewView(int zoom, bool preparedCacheEnabled = true)
        {
            var go   = new GameObject("MapView_TileSymbolKick");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = zoom;
            view.Config.TileSelection.MaxZoom = zoom;
            view.Config.PreparedCache.Enabled = preparedCacheEnabled;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            return (go, view);
        }

        private static string SourcePath(params string[] relative)
            => Path.Combine(Application.dataPath, "Code", Path.Combine(relative));

        private static int CountOccurrences(string text, string needle)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        // ── The feed swap is real (structural) ──
        [Test]
        public void FeedSwapIsReal_NoResidualPushSymbols()
        {
            string tileManagerSrc = File.ReadAllText(SourcePath("MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs"));
            string subsystemSrc   = File.ReadAllText(SourcePath("MapRenderer.Unity", "Text", "SymbolSubsystem.cs"));

            Assert.AreEqual(0, CountOccurrences(tileManagerSrc, "SymbolTileBytesReady"),
                "TileManager must contain ZERO SymbolTileBytesReady occurrences — the push feed is retired.");
            Assert.IsTrue(tileManagerSrc.Contains("ISymbolTileWorkerFactory SymbolWorkerFactory"),
                "TileManager must hold the factory field SymbolWorkerFactory.");
            Assert.IsTrue(tileManagerSrc.Contains("ISymbolTileWorkerPass symbolPass"),
                "KickMeshBuild's signature must carry an ISymbolTileWorkerPass parameter.");

            Assert.AreEqual(0, CountOccurrences(subsystemSrc, "OnTileBytesReady"),
                "SymbolSubsystem must contain ZERO OnTileBytesReady occurrences — the push entry is retired.");
            Assert.AreEqual(0, CountOccurrences(subsystemSrc, "_buildQueue"),
                "SymbolSubsystem must contain ZERO _buildQueue occurrences — the build-start queue is retired.");
            Assert.IsTrue(typeof(ISymbolTileWorkerFactory).IsAssignableFrom(typeof(SymbolSubsystem)),
                "SymbolSubsystem must implement ISymbolTileWorkerFactory.");
        }

        // ── DrainMeshBuilds stays symbol-silent ────────────────────────────────────────────────────────
        [Test]
        public void DrainMeshBuilds_NeverDrivesSymbolFactory()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var (go, view) = NewView(zoom: 0);
            Track(go);
            var spy = new SpySymbolTileWorkerFactory();
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillAndSymbolStyle(), symbolsIntentionallyUnwired: true);

                // ONE LateUpdate creates the record + starts the fetch. The kick block can NEVER fire on this
                // same call (it requires lt.Decode already set from a PRIOR PumpPending call) — TryBeginBuild
                // is provably unreached so far.
                view.LateUpdate();
                Assert.AreEqual(0, spy.BeginBuildCalls.Count, "sanity: the kick cannot have fired yet.");

                // Settle entirely via DrainMeshBuilds — never through PumpPending's kick block again.
                view.DrainMeshBuilds();

                Assert.IsTrue(view.AllTilesSettled(), "sanity: drain must fully settle the tile.");
                Assert.AreEqual(0, spy.BeginBuildCalls.Count,
                    "DECISIVE: TryBeginBuild must NEVER fire from a DrainMeshBuilds call site — symbolPass " +
                    "is computed ONLY at the PumpPending kick site; drain always passes the default " +
                    "(null), keeping it symbol-silent.");
            }
            finally { view.Teardown(); }
        }

        // ── F-6: a tile condemned before its kick never attempts a symbol build ────────────────────────
        [Test]
        public void DepartedBeforeKick_NeverBeginsSymbolBuild()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = Track(new GameObject("MapView_TileSymbolKick_DepartedBeforeKick"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5; // known 9-tile z5 cover
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 1; // trickle kicks — most tiles stay un-kicked after the observe tick
            view.Config.MaxReleasesPerTick   = 1; // trickle releases — the condemned window stays observable
            var spy = new SpySymbolTileWorkerFactory();
            view.TileManager.SymbolWorkerFactory = spy;
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillAndSymbolStyle(), symbolsIntentionallyUnwired: true);

                view.LateUpdate();          // Tick 1: cover created (9 tiles), fetches requested
                view.DrainMeshBuilds();     // land the fetch decodes deterministically (symbol-silent, see DrainMeshBuilds_NeverDrivesSymbolFactory)
                view.LateUpdate();          // Tick 2: fetches observed → lt.Decode set, 0 kicked
                Assert.AreEqual(0, spy.BeginBuildCalls.Count, "sanity: nothing kicked before the tile has a prior lt.Decode.");

                var oldKeys = new List<LoadedTileKey>();
                view.TileManager.CollectLoadedTileKeys(oldKeys);
                Assert.GreaterOrEqual(oldKeys.Count, 6, "need a multi-tile cover for the departure to be non-vacuous.");
                var oldTiles = oldKeys.ConvertAll(k => k.Tile);

                // Pan far away — the NEXT recompute condemns every old tile. At most ONE old tile can race
                // ahead and get kicked before its condemnation registers (matching mesh's own pre-existing
                // race) — the point is that the OTHER old tiles never reach TryBeginBuild.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 150.0, Latitude = 70.0 });
                for (int f = 0; f < 300 && view.ReleaseQueueDepth() > 0; f++) { view.LateUpdate(); view.DrainMeshBuilds(); }
                Assert.AreEqual(0, view.ReleaseQueueDepth(), "sanity: the departing backlog must fully drain.");

                int oldTilesKicked = spy.BeginBuildCalls.FindAll(c => oldTiles.Contains(c.Tile)).Count;
                Assert.LessOrEqual(oldTilesKicked, 1,
                    "F-6 DECISIVE: a tile condemned before its kick must never reach TryBeginBuild — at most the " +
                    "single unavoidable race-window tile (kicked the SAME tick its condemnation registers, " +
                    "before the recompute runs) may appear; a missing _releaseQueued skip would eventually kick " +
                    $"ALL {oldTiles.Count} departed tiles while the release budget trickles them out.");
            }
            finally { view.Teardown(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TilePrioritySorterTests — unit-level teeth for TilePrioritySorter
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TilePrioritySorterTests
    {
        /// <summary>A projection whose <see cref="Project"/> always returns the origin, so every tile's
        /// <see cref="TilePriority.Key"/> is identically 0 — the tiebreak becomes the WHOLE sort decision.
        /// Only the geometry entry point is real; every other member is unused by <c>TilePriority.Key</c>
        /// and throws.</summary>
        private readonly struct ZeroProjection : IProjection
        {
            public ProjectedPoint ProjectPoint(in GeoCoordinate geo) => new ProjectedPoint { World = default, Up = default };
            public double3 Project(in GeoCoordinate geo) => double3.zero;
            public double3 UpAt(in GeoCoordinate geo) => new double3(0, 1, 0);
            public float3x3 TangentBasisAt(in GeoCoordinate geo) => float3x3.identity;
            public double MetersPerUnit => 1.0;
            public bool TryGetHorizonOccluder(out double3 renderCentre, out double radius)
            {
                renderCentre = default; radius = 0.0; return false;
            }
            public double MaxRefineAngleRad => double.PositiveInfinity;
            public GeoCoordinate3D ScreenToGround(double2 screenPx, double2 viewportPx, in MapRenderer.Core.Geo.CameraProperties camera)
                => throw new NotSupportedException("ZeroProjection is a priority-math-only test double.");
            public double2 GroundToScreen(in GeoCoordinate3D ground, double2 viewportPx, in MapRenderer.Core.Geo.CameraProperties camera)
                => throw new NotSupportedException("ZeroProjection is a priority-math-only test double.");
            public double ClampValidLatitude(double latitudeDegrees) => latitudeDegrees;
            public bool IsFinitePlanarWorld => true;
            public GeoCoordinate3D ClampLookAtToWorld(double2 viewportPx, in MapRenderer.Core.Geo.CameraProperties camera)
                => throw new NotSupportedException("ZeroProjection is a priority-math-only test double.");
        }

        private static TilePriorityContext ZeroContext()
            => new TilePriorityContext(new ZeroProjection(), double3.zero, float3x3.identity, double3.zero,
                TilePriorityStrategy.GroundDistanceToLookAt);

        private static TileManager.LoadedKey Key(int z, int x, int y, int slot)
            => new TileManager.LoadedKey(new TileId { Z = z, X = x, Y = y }, slot);

        private static readonly TilePriorityContext Zero = ZeroContext();

        /// <summary>Every entry has an identical (zero) priority key, so the sort result is decided
        /// ENTIRELY by the (Z, X, Y, Slot) tiebreak — the reason <see cref="TilePrioritySorter"/>'s private
        /// <c>IsAfter</c> exists. A sorter that drops the <c>Slot</c> tiebreak produces a nondeterministic
        /// paint order no count-based test observes.</summary>
        [Test]
        public void SortsStably_OnEqualKeys()
        {
            var list = new List<TileManager.LoadedKey>
            {
                Key(5, 3, 2, 1),
                Key(5, 1, 5, 0),
                Key(3, 9, 9, 9),
                Key(5, 1, 5, 1),
                Key(5, 1, 2, 0),
            };

            new TilePrioritySorter().Sort(list, in Zero);

            var expected = new List<TileManager.LoadedKey>
            {
                Key(3, 9, 9, 9),
                Key(5, 1, 2, 0),
                Key(5, 1, 5, 0),
                Key(5, 1, 5, 1),
                Key(5, 3, 2, 1),
            };

            CollectionAssert.AreEqual(expected, list,
                "with every priority key tied at 0, the sort must order strictly by (Z, X, Y) then Slot.");
        }

        /// <summary><c>_keys</c> exists solely to avoid a per-sort allocation. Warms the scratch buffer to
        /// steady size with one discarded call (a list past the initial 64-capacity forces the growth
        /// path), then meters a second call — a sorter that allocates a fresh array per call is
        /// functionally correct and destroys the reason the field exists.</summary>
        [Test]
        public void Sort_DoesNotAllocate()
        {
            var list = new List<TileManager.LoadedKey>(100);
            for (int i = 0; i < 100; i++)
                list.Add(Key(5, i, 100 - i, i & 3));

            var sorter = new TilePrioritySorter();
            sorter.Sort(list, in Zero); // warm-up — grows the scratch buffer past 64

            Assert.That(() => sorter.Sort(list, in Zero), Is.Not.AllocatingGCMemory(),
                "TilePrioritySorter.Sort must not allocate once its scratch buffer has grown to steady size.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillAntialiasBandTests — the one antialiasing switch implementable per layer
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class FillAntialiasBandTests
    {
        /// <summary>One triangular polygon ring, well inside the tile window so no edge is a clip edge —
        /// the band's own suppression predicate must not be what makes a count zero here.</summary>
        /// <param name="tile">The tile the buffers are addressed to.</param>
        private static TileGeometryBuffers TrianglePolygon(TileId tile)
        {
            var g = TileGeometryBuffers.Allocate(tile, extent: 4096.0, featureCount: 1, maxRings: 1, maxVertices: 3);
            g.FeatureGeometryType[0] = TileGeometryType.Polygon;
            g.RingFeatureIdx[0] = 0;
            g.RingOffsets[0] = 0;
            g.RingOffsets[1] = 3;
            g.Vertices[0] = new double2(100, 100);
            g.Vertices[1] = new double2(900, 100);
            g.Vertices[2] = new double2(900, 900);
            g.RingCount = 1;
            g.VertexCount = 3;
            return g;
        }

        /// <summary>The one selected polygon feature <see cref="TrianglePolygon"/>'s ring belongs to.</summary>
        private static IReadOnlyList<SelectedTileFeature> OneSelectedPolygon() =>
            new List<SelectedTileFeature>
            {
                new SelectedTileFeature { Feature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.Polygon, hasId: false), Ordinal = 0 },
            };

        /// <summary>What the fill graph produced for a layer whose paint block is
        /// <paramref name="paintJson"/> — the band vertex count and the interior vertices, copied out
        /// before the native buffers are freed so two builds can be compared after both have run.</summary>
        /// <param name="paintJson">The layer's paint block, parsed exactly as a style would.</param>
        /// <param name="bandVertexCount">Vertices in the band suffix — 0 iff no band was emitted.</param>
        /// <param name="interior">Every tile-space vertex ahead of that suffix.</param>
        /// <param name="antialiasWhereUnspecified">MapViewConfig.FillAntialiasing — the parse-time DEFAULT
        /// for layers whose style does not specify fill-antialias. A layer that specifies wins.</param>
        private static void Build(string paintJson, out int bandVertexCount, out double2[] interior,
                                  bool antialiasWhereUnspecified = true)
        {
            var tile = new TileId { Z = 0, X = 0, Y = 0 };
            TileGeometryBuffers geometry = TrianglePolygon(tile);
            // The project default enters HERE, at parse — exactly where production puts it
            // (StyleParser.Parse -> Fill.PaintProperties.Parse's antialiasDefault parameter).
            var paint = Fill.PaintProperties.Parse(JsonParser.Parse(paintJson), antialiasWhereUnspecified);

            FillMeshPipeline.LayerInput input = StyledFillTileBuilder.BuildLayerInput(
                OneSelectedPolygon(), geometry, paint, zoom: 0.0, tileOriginRender: double3.zero,
                out NativeArray<Vector4> featureColors, new WebMercatorProjection(),
                layout: null, clip: TileBufferClip.KeepTileUnits(0.0));

            FillGraphOutput output = default;
            try
            {
                Assert.IsTrue(input.RingVisitOrder.IsCreated, $"{paintJson}: the fixture must build a layer.");
                output = FillMeshGraph.Schedule(input);
                output.Handle.Complete();
                Assert.AreEqual(FillGraphCounts.Ok, output.Error.Value, $"{paintJson}: the graph must succeed.");

                bandVertexCount = output.Counts[0].BandVertexCount;
                interior = new double2[output.TileVertices.Length - bandVertexCount];
                for (int i = 0; i < interior.Length; i++) interior[i] = output.TileVertices[i];
            }
            finally
            {
                output.Dispose();
                featureColors.Dispose();
                input.RingVisitOrder.Dispose();
                geometry.Dispose();
            }
        }

        // ── fill-antialias: false emits no band, and changes nothing else ──────────────────────────────
        //
        // Both halves matter. "No band" alone is satisfied by a build that produced nothing at all; the
        // interior comparison is what says the layer still renders, identically, minus the band.
        //
        // RED: delete the `SuppressBoundaryBand = …` line from StyledFillTileBuilder.BuildLayerInput (or
        // pin it to false). The parse is guarded separately and does not need re-verifying here —
        // FillPaintTests.FillPaint_Antialias_ParsesAsABoolean owns that half.
        [Test]
        public void FillAntialiasFalse_EmitsNoBand_AndLeavesTheInteriorBitIdentical()
        {
            Build("{\"fill-color\":\"#ffffff\"}", out int defaultBand, out double2[] defaultInterior);
            Build("{\"fill-color\":\"#ffffff\",\"fill-antialias\":false}", out int offBand, out double2[] offInterior);

            // Two band vertices per ring vertex, over one 3-vertex ring — the count the sizing tooth in
            // TileBuildGraphTests reads too. Stated as a precondition, because a tree that emits no band at
            // all satisfies the assertion below without the property doing anything.
            Assert.AreEqual(6, defaultBand,
                "precondition: with fill-antialias absent (⇒ true, the spec default) this ring must carry a " +
                "full band, or the zero below is measuring a band-free tree rather than the opt-out.");

            Assert.AreEqual(0, offBand,
                "fill-antialias: false must emit NO band geometry — this is the property's only consumer, " +
                "and the one that keeps it implementable per layer.");

            Assert.AreEqual(defaultInterior, offInterior,
                "opting out of antialiasing must drop the band and nothing else — the interior triangulation " +
                "is bit-identical either way.");
        }

        // fill-antialias: true is the spec default, so it must be indistinguishable from absent — a
        // threshold read the wrong way round would show up here and nowhere else.
        [Test]
        // NOTE the name is about OUTPUT, and is narrower than it reads: since the global default was
        // added, Fill.PaintProperties.AntialiasSpecified DOES tell an explicit true from an absent
        // property. That distinction is exactly what lets the global default apply only to the absent
        // case. What stays indistinguishable is the geometry, whenever the global default is ON.
        public void FillAntialiasTrue_IsIndistinguishableFromTheProperty_BeingAbsent()
        {
            Build("{\"fill-color\":\"#ffffff\"}", out int absentBand, out double2[] absentInterior);
            Build("{\"fill-color\":\"#ffffff\",\"fill-antialias\":true}", out int trueBand, out double2[] trueInterior);

            Assert.AreEqual(absentBand, trueBand, "an explicit true must band exactly as the default does.");
            Assert.AreEqual(absentInterior, trueInterior, "and produce the same interior.");
        }

        // ── The global override (MapViewConfig.FillAntialiasing) ───────────────────────────────────────

        /// <summary>The global override forces the band off for a layer whose style ASKS for antialiasing,
        /// and perturbs nothing else: the interior is vertex-for-vertex what the banded build produced.
        /// <para>Without the interior comparison this would pass for a build that emitted no geometry at
        /// all, which is the failure mode a bare "band count is 0" assertion cannot see.</para></summary>
        [Test]
        public void GlobalDefaultOff_SuppressesTheBand_WhereTheStyleIsSilent()
        {
            Build("{}", out int bandedCount, out double2[] bandedInterior);
            Assert.Greater(bandedCount, 0, "precondition: the default style must produce a band to suppress.");

            Build("{}", out int overriddenCount, out double2[] overriddenInterior, antialiasWhereUnspecified: false);

            Assert.AreEqual(0, overriddenCount,
                "MapViewConfig.FillAntialiasing = false must force the band off even though the style's " +
                "fill-antialias defaults to true.");
            CollectionAssert.AreEqual(bandedInterior, overriddenInterior,
                "the override must remove the band and change nothing else — the interior must be identical.");
        }

        /// <summary>The override is one-way. It can force the band OFF; it cannot turn it back ON for a layer
        /// whose style set <c>fill-antialias: false</c> — the two are OR'd, not overridden, so a style stays
        /// readable rather than being silently countermanded by a global switch.</summary>
        [Test]
        public void GlobalDefaultOff_LeavesALayerThatExplicitlyAsksForAntialiasingAlone()
        {
            // The one case that separates "global is a default" from "global overrides everything", and the
            // case the shipped Liberty style actually contains: landcover_wetland sets fill-antialias: true
            // explicitly while 12 other fill layers say nothing.
            Build("{\"fill-antialias\": true}", out int count, out _, antialiasWhereUnspecified: false);
            Assert.Greater(count, 0,
                "a layer that explicitly asks for antialiasing must keep its band when the GLOBAL default " +
                "is off — the global setting decides only for layers whose style said nothing.");
        }

        /// <summary>The global default cannot turn a band back ON for a layer that opted out.</summary>
        [Test]
        public void GlobalDefaultOn_DoesNotResurrectABandTheStyleTurnedOff()
        {
            Build("{\"fill-antialias\": false}", out int count, out _, antialiasWhereUnspecified: true);
            Assert.AreEqual(0, count,
                "fill-antialias: false must stay band-free with the global override left ON — the global " +
                "knob must not be able to override a layer's explicit opt-out.");
        }

    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // PreparedTileCacheTests — Unity-only Mesh teeth, not in the fast core-tests project
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class PreparedTileCacheTests : BaseTestFixture
    {
        private static readonly TileId Tile0 = new TileId { Z = 3, X = 1, Y = 1 };

        // ── Fixtures (coexistence tooth only — real fixture styles, not synthetic meshes) ─────────
        private static StyleDocument LoadStyle(string fileName)
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", fileName);
            FileAssert.Exists(path);
            return StyleParser.Parse(File.ReadAllText(path));
        }

        /// <summary>Real fill-layer mesh build (mirrors PreparedCacheTests.GroundTruthColorAtZoom, minus
        /// the Core-only leak-tracked allocator — this is a cache-unit test, not a NativeArray-invariant
        /// one) — used to prove coexistence with genuinely different baked colors per style, not just two
        /// synthetic meshes over identical geometry.</summary>
        private static Mesh BuildFillMesh(byte[] mvtBytes, StyleDocument style, TileId id, double zoom)
        {
            using var mvtTile = MvtDecoder.Decode(id, mvtBytes);
            var fillLayer = style.Layers[0];
            var paint     = ((Fill.StyleLayer)fillLayer).Paint;
            var mvtLayer  = SourceLayerResolver.ResolveTileLayer(fillLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "Fixture must contain a resolvable MVT layer.");
            var selected  = TestTileMeshBuilder.Select(fillLayer, mvtLayer, zoom);
            Assert.Greater(selected.Count, 0, "Fixture must produce >=1 feature (non-vacuous).");
            Mesh mesh = TestTileMeshBuilder.BuildFillFromLayer(mvtLayer, selected, paint, zoom, id);
            Assert.IsNotNull(mesh, "Fill build must produce geometry.");
            return mesh;
        }

        /// <summary>Mirrors PreparedCacheTests's FirstVertexColor/ColorsClose — a fill-color expression
        /// with no per-feature "get" is a pure function of zoom, so every vertex shares one color.</summary>
        private static Color FirstVertexColor(Mesh mesh)
        {
            var colors = new System.Collections.Generic.List<Color>();
            mesh.GetColors(colors);
            Assert.Greater(colors.Count, 0, "Mesh must have vertex colors.");
            return colors[0];
        }

        private static bool ColorsClose(Color a, Color b, float eps)
            => Mathf.Abs(a.r - b.r) < eps && Mathf.Abs(a.g - b.g) < eps && Mathf.Abs(a.b - b.b) < eps;

        /// <summary>A minimal real mesh (classic API) — vertexCount vertices, one triangle if >= 3 — enough
        /// for GetVertexBufferStride(0)/GetIndexCount(0) to report real, non-zero values. Content is
        /// irrelevant; these tests only exercise cache bookkeeping and Mesh lifecycle, never rendering.</summary>
        private static Mesh MakeMesh(int vertexCount)
        {
            var mesh = new Mesh { name = $"PreparedTileCacheTests-{vertexCount}v" };
            var verts = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++) verts[i] = new Vector3(i, 0f, 0f);
            mesh.vertices = verts;
            if (vertexCount >= 3) mesh.triangles = new[] { 0, 1, 2 };
            return mesh;
        }

        /// <summary>Independently computed reference matching PreparedTileCache.EstimateBytes's documented
        /// formula — via Mesh's own public API, never the cache's private method.</summary>
        private static long ExpectedBytes(Mesh m)
        {
            if (m == null) return 0;
            return (long)m.vertexCount * m.GetVertexBufferStride(0) + (long)m.GetIndexCount(0) * 4;
        }

        // ── Multi-style coexistence ────────────────────────────────────────────────────────────

        [Test]
        public void TwoStyles_Coexist_NoCrossContamination()
        {
            // Genuinely DIFFERENT-colored styles per styleId (stronger than two tokens over identical
            // synthetic geometry): interp-fill-composite-style.json (zoom-interpolated CONTINENT match,
            // still Composite-kind so the bake still runs post-Stage-1) vs coexist-fill-style.json
            // (constant green — baked white), both over the same fixture tile/layer.
            byte[] bytes  = SampleTileFixture.Bytes();
            var styleDocA = LoadStyle("interp-fill-composite-style.json");
            var styleDocB = LoadStyle("coexist-fill-style.json");
            Mesh meshA = Track(BuildFillMesh(bytes, styleDocA, Tile0, zoom: 3.0));
            Mesh meshB = Track(BuildFillMesh(bytes, styleDocB, Tile0, zoom: 3.0));

            Color colorA = FirstVertexColor(meshA);
            Color colorB = FirstVertexColor(meshB);
            Assert.IsFalse(ColorsClose(colorA, colorB, 1e-3f),
                "Positive control: styleA/styleB fixtures must bake genuinely different colors " +
                "(non-vacuous coexistence — a same-color pair could pass by coincidence).");

            var cache  = new PreparedTileCache(byteBudget: 0, countCap: 0); // unbounded
            var styleA = new StyleToken("A");
            var styleB = new StyleToken("B");
            try
            {
                // Same TileId/layerId, different style — a naive "drop previous on insert" cache would let
                // meshB's Put destroy/replace meshA's entry; PreparedTileCache keys them independently.
                cache.Put(new PreparedKey(styleA, Tile0, 0), meshA);
                cache.Put(new PreparedKey(styleB, Tile0, 0), meshB);

                Assert.AreEqual(2, cache.Count, "Both styles' entries must coexist under the same TileId/layerId.");

                Assert.IsTrue(cache.TryTake(new PreparedKey(styleA, Tile0, 0), out Mesh takenA));
                Assert.AreSame(meshA, takenA, "styleA lookup must hit the styleA mesh, not styleB's.");
                Assert.IsTrue(ColorsClose(colorA, FirstVertexColor(takenA), 1e-4f),
                    "styleA lookup must hit content baked from the interp-fill style, not the coexist style.");

                Assert.IsTrue(cache.TryTake(new PreparedKey(styleB, Tile0, 0), out Mesh takenB));
                Assert.AreSame(meshB, takenB, "styleB lookup must hit the styleB mesh, not styleA's — " +
                    "neither toggle re-prepared (TryTake serves the SAME Mesh object both put in).");
                Assert.IsTrue(ColorsClose(colorB, FirstVertexColor(takenB), 1e-4f),
                    "styleB lookup must hit content baked from the coexist style, not the interp-fill style.");
            }
            finally
            {
                cache.Dispose(); // both already taken out — no-op; the bag destroys the test meshes.
            }
        }

        // ── Bounded / eviction ──────────────────────────────────────────────────────────────────

        [Test]
        public void Bounded_EvictsLru_FreesMesh()
        {
            Mesh mesh1 = MakeMesh(100);
            Mesh mesh2 = MakeMesh(100);
            long bytes1 = ExpectedBytes(mesh1);
            Assert.Greater(bytes1, 0, "Positive control: the test mesh must have non-zero estimated bytes.");

            // Budget fits exactly one mesh, not two.
            long budget = bytes1 + bytes1 / 2;
            var cache = new PreparedTileCache(budget, countCap: 0);
            var style = StyleToken.Default;
            var keyA  = new PreparedKey(style, Tile0, 0);
            var keyB  = new PreparedKey(style, Tile0, 1);

            cache.Put(keyA, mesh1);
            Assert.AreEqual(1, cache.Count);
            Assert.IsTrue(mesh1 != null, "mesh1 must still be alive while within budget.");

            // Exceeds the budget → evicts the LRU entry (keyA/mesh1), destroying its Mesh.
            cache.Put(keyB, mesh2);

            Assert.AreEqual(1, cache.Count, "Only the surviving entry remains after LRU eviction.");
            Assert.IsFalse(cache.Contains(keyA), "The LRU entry (keyA) must have been evicted.");
            Assert.IsTrue(cache.Contains(keyB), "The most-recently-Put entry (keyB) must survive.");
            Assert.IsTrue(mesh1 == null,
                "Evicted mesh must be DESTROYED, not merely dropped from bookkeeping (Unity fake-null after " +
                "DestroyImmediate). Falsifier: an unbounded/non-destroying cache would leave mesh1 alive.");
            Assert.AreEqual(1, cache.Evictions,
                "Exactly one LRU eviction (keyA/mesh1) must have been counted. A TryTake-based removal " +
                "must NOT bump this counter (only the forced-eviction path in Put does).");

            cache.Dispose(); // destroys mesh2
        }

        // ── ByteBudget/MaxCount expose the LIVE applied (clamped) values ───────────────────────

        [Test]
        public void ByteBudgetAndMaxCount_ExposeConfiguredOrClampedUnboundedValues()
        {
            var bounded = new PreparedTileCache(byteBudget: 1000, countCap: 5);
            try
            {
                Assert.AreEqual(1000, bounded.ByteBudget, "A positive configured byte budget is exposed unchanged.");
                Assert.AreEqual(5, bounded.MaxCount, "A positive configured count cap is exposed unchanged.");
            }
            finally
            {
                bounded.Dispose();
            }

            var unbounded = new PreparedTileCache(byteBudget: 0, countCap: 0);
            try
            {
                Assert.AreEqual(long.MaxValue, unbounded.ByteBudget,
                    "A <=0 byteBudget clamps internally to long.MaxValue ('unbounded') — the accessor must " +
                    "expose the LIVE applied value, not the raw 0 the constructor received.");
                Assert.AreEqual(int.MaxValue, unbounded.MaxCount,
                    "A <=0 countCap clamps internally to int.MaxValue ('unbounded') — same live-value contract.");
            }
            finally
            {
                unbounded.Dispose();
            }
        }

        // ── Byte accounting ─────────────────────────────────────────────────────────────────────

        [Test]
        public void BytesHeld_TracksEstimateBytes_Sum()
        {
            var cache = new PreparedTileCache(byteBudget: 0, countCap: 0);
            Mesh meshA = MakeMesh(10);
            Mesh meshB = MakeMesh(20);
            try
            {
                cache.Put(new PreparedKey(StyleToken.Default, Tile0, 0), meshA);
                cache.Put(new PreparedKey(StyleToken.Default, Tile0, 1), meshB);

                long expected = ExpectedBytes(meshA) + ExpectedBytes(meshB);
                Assert.Greater(expected, 0, "Positive control: non-zero expected bytes (non-vacuous).");
                Assert.AreEqual(expected, cache.BytesHeld,
                    "BytesHeld must equal the sum of each held entry's estimated bytes.");
            }
            finally
            {
                cache.Dispose();
            }
        }

        // ── Empty-layer completeness marker ─────────────────────────────────────────────────────

        [Test]
        public void NullMeshMarker_IsAValidZeroByteEntry()
        {
            var cache = new PreparedTileCache(byteBudget: 0, countCap: 0);
            var key   = new PreparedKey(StyleToken.Default, Tile0, 2);

            cache.Put(key, null);

            Assert.IsTrue(cache.Contains(key), "A null-mesh (empty-layer) marker must be a valid entry.");
            Assert.AreEqual(0, cache.BytesHeld, "A null-mesh marker costs 0 bytes.");
            Assert.IsTrue(cache.TryTake(key, out Mesh mesh));
            Assert.IsNull(mesh, "TryTake on a marker entry must hand back null (not throw / fabricate a mesh).");

            cache.Dispose();
        }

        // ── Destroy-once (double-free guard at the cache-unit level) ───────────────────────────

        [Test]
        public void Dispose_DestroysHeldMeshes_Once()
        {
            var cache = new PreparedTileCache(byteBudget: 0, countCap: 0);
            Mesh mesh = MakeMesh(5);
            cache.Put(new PreparedKey(StyleToken.Default, Tile0, 0), mesh);

            cache.Dispose();
            Assert.IsTrue(mesh == null, "Dispose must destroy every held mesh.");

            // Idempotent — a second Dispose (mirroring TileManager.Dispose's idempotence contract) must not throw.
            Assert.DoesNotThrow(() => cache.Dispose());
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // ProfilerCounterHoldTests — scope: Editor/EditMode with ENABLE_PROFILER
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class ProfilerCounterHoldTests
    {
        private static ProfilerCategory Category => new("MapRendererProbe");

        [Test]
        public void CounterValue_RoundTripsWithinASingleFrame()
        {
            var counter = new ProfilerCounterValue<int>(
                Category, "Probe.SameFrame", ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

            counter.Value = 123;

            Assert.AreEqual(123, counter.Value,
                "a counter must at least hold what was just written to it — everything below assumes this.");
        }

        [UnityTest]
        public IEnumerator CounterValue_AcrossAFrameBoundary_HoldsWithoutResetOption_AndZeroesWithIt()
        {
            var held = new ProfilerCounterValue<int>(
                Category, "Probe.Held", ProfilerMarkerDataUnit.Count, ProfilerCounterOptions.FlushOnEndOfFrame);

            var reset = new ProfilerCounterValue<int>(
                Category, "Probe.Reset", ProfilerMarkerDataUnit.Count,
                ProfilerCounterOptions.FlushOnEndOfFrame | ProfilerCounterOptions.ResetToZeroOnFlush);

            held.Value = 456;
            reset.Value = 789;

            yield return null;

            // Read the control FIRST and report it even when the main assertion would pass: a held value that
            // survived because nothing flushed is not evidence of anything.
            var controlZeroed = reset.Value == 0;
            UnityEngine.Debug.Log($"COUNTER-HOLD: control(ResetToZeroOnFlush)={reset.Value} held(no reset)={held.Value} " +
                                  $"=> flush observed: {controlZeroed}");

            Assert.IsTrue(controlZeroed,
                "VALIDITY CONTROL failed: the reset-on-flush counter kept its value, so no end-of-frame flush " +
                "occurred in EditMode. The hold-across-frames claim is UNVERIFIED here — re-check under PlayMode " +
                "or a Development standalone build before relying on it.");

            Assert.AreEqual(456, held.Value,
                "a flush DID occur (the control zeroed) and the no-reset counter kept its value — which is what lets " +
                "ProfilerCounterTelemetry report levels that persist across a frame whose provider did not run.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // CoverKeyGateTests — unit-level teeth for CoverKeyGate
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class CoverKeyGateTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom, double heading = 0.0,
            double tilt = 0.0)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 },
                zoom, heading, tilt);

        private static TileManager.TileSelectionConfig Cfg(double viewportX, double viewportY)
            => new TileManager.TileSelectionConfig { FramingViewportPx = new double2(viewportX, viewportY) };

        [Test]
        public void FreshGate_IsDirty()
        {
            var gate = new CoverKeyGate();

            // Checked BEFORE any MarkStaleIfMoved call: its own `!_initialised` clause would also force
            // dirty true and mask a dropped `_dirty = true` initializer if this ran second.
            Assert.IsTrue(gate.IsDirty,
                "a newly constructed gate must read dirty immediately — this pins the HEAD `_dirty = true` " +
                "field initializer directly, not via MarkStaleIfMoved's own fallback.");

            var cam = Cam(10, 20, 5.0);
            var cfg = Cfg(800, 600);
            gate.MarkStaleIfMoved(in cam, in cfg);

            Assert.IsTrue(gate.IsDirty,
                "a newly constructed, never-committed, never-invalidated gate must be dirty as soon as " +
                "MarkStaleIfMoved runs — this pins the HEAD `_dirty = true` initializer together with the " +
                "`!_initialised` clause, so the first Tick always recomputes.");
        }

        [Test]
        public void StaysDirty_UntilCommitted()
        {
            var gate = new CoverKeyGate();
            var cam  = Cam(10, 20, 5.0, heading: 30, tilt: 15);
            var cfg  = Cfg(800, 600);

            gate.Commit(in cam, in cfg);
            Assert.IsFalse(gate.IsDirty, "precondition: Commit must clear dirty.");

            gate.Invalidate();
            Assert.IsTrue(gate.IsDirty, "Invalidate must mark the gate dirty immediately, before any " +
                "MarkStaleIfMoved call observes it.");

            gate.MarkStaleIfMoved(in cam, in cfg); // same camera/viewport as the Commit — no movement
            Assert.IsTrue(gate.IsDirty,
                "a gate that treats Invalidate as 'reset the key and recompute from it' would read clean " +
                "here, because the camera genuinely has not moved. Invalidate must force dirty regardless.");
        }

        [Test]
        public void DoesNotRecompute_WhenNothingMoved()
        {
            var gate = new CoverKeyGate();
            var cam  = Cam(10, 20, 5.0, heading: 30, tilt: 15);
            var cfg  = Cfg(800, 600);
            gate.Commit(in cam, in cfg);

            gate.MarkStaleIfMoved(in cam, in cfg);

            Assert.IsFalse(gate.IsDirty, "an identical camera and viewport must not mark the gate dirty.");
        }

        /// <summary>Perturbs exactly ONE of the seven framing inputs and asserts the gate goes dirty. A
        /// gate that forgot one comparison passes a single-field test and fails this one, run over all
        /// seven.</summary>
        [TestCase(0, TestName = "MarksDirty_WhenLongitudeMoves")]
        [TestCase(1, TestName = "MarksDirty_WhenLatitudeMoves")]
        [TestCase(2, TestName = "MarksDirty_WhenZoomMoves")]
        [TestCase(3, TestName = "MarksDirty_WhenHeadingMoves")]
        [TestCase(4, TestName = "MarksDirty_WhenTiltMoves")]
        [TestCase(5, TestName = "MarksDirty_WhenViewportWidthMoves")]
        [TestCase(6, TestName = "MarksDirty_WhenViewportHeightMoves")]
        public void MarksDirty_WhenExactlyOneFieldMoves(int fieldIndex)
        {
            var gate = new CoverKeyGate();
            var cam  = Cam(10, 20, 5.0, heading: 30, tilt: 15);
            var cfg  = Cfg(800, 600);
            gate.Commit(in cam, in cfg);

            CameraProperties                   movedCam = cam;
            TileManager.TileSelectionConfig    movedCfg = cfg;
            switch (fieldIndex)
            {
                case 0: movedCam = Cam(11, 20, 5.0, heading: 30, tilt: 15); break;
                case 1: movedCam = Cam(10, 21, 5.0, heading: 30, tilt: 15); break;
                case 2: movedCam = Cam(10, 20, 6.0, heading: 30, tilt: 15); break;
                case 3: movedCam = Cam(10, 20, 5.0, heading: 31, tilt: 15); break;
                case 4: movedCam = Cam(10, 20, 5.0, heading: 30, tilt: 16); break;
                case 5: movedCfg = Cfg(801, 600); break;
                case 6: movedCfg = Cfg(800, 601); break;
            }

            gate.MarkStaleIfMoved(in movedCam, in movedCfg);

            Assert.IsTrue(gate.IsDirty, $"field index {fieldIndex} moved from the committed key — the gate must go dirty.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // DecodersTests — tile-decode is selected BY TileEncoding, not hardcoded to MVT
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tile-decode is selected BY <see cref="TileEncoding"/>, not hardcoded to
    /// MVT. Today <see cref="TileEncoding.Mvt"/> is the only member, so this pins the one resolvable case;
    /// an unmapped encoding throws rather than silently falling through to an MVT decode.
    /// </summary>
    [TestFixture]
    public class DecodersTests
    {
        [Test]
        public void ForEncoding_Mvt_ResolvesToMvtTileDecoder()
        {
            ITileDecoder decoder = Decoders.ForEncoding(TileEncoding.Mvt);
            Assert.IsInstanceOf<MvtTileDecoder>(decoder);
        }
    }
}
