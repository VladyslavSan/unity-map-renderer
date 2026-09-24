// Tiles/SourceTileGraphBuildTests.cs — cache/scheduler wiring, the source tile graph build, fill-extrusion graph build, and the background-quad projection teeth.
//
// Roughly pipeline order: the cache/scheduler/LOD wiring fixtures, then the two graph-build fixtures (source tile, fill-extrusion), then the background-quad projection fixture. EagerDecodeOwnershipTests, originally packed here, was pulled back out to stand alone: its nested ProbeFeatureSource owns a static IWorkScheduler (a live concurrency primitive), which the process-state stay-alone rule covers.
//
// Contents:
//   MeshBuildWorkSchedulerTests        — RecordingWorkScheduler lives in TestSupport/ (shared with SymbolSubsystemWorkSchedulerTests) rather than as a private nested class here, so the two suites share one spy instead of drifting into two subtly different copies.
//   PreparedCacheSymbolCoverageTests   — EditMode, not PlayMode: the "EditMode settle is symbol-silent" rule is about DrainMeshBuilds (see TileSymbolKickTests' header).
//   PreparedCacheTests                 — Uses interp-fill-style.json (a zoom-interpolate fill-color) as its style fixture.
//   ProjectedAreaLodWiringTests        — T-AGGR-WIRED: MapView must build a ProjectedAreaLodStrategy carrying the Inspector's aggressiveness value, and rebuild the selector when that value changes.
//   SourceTileGraphBuildTests          — Fixture: SampleTileFixture + a fill layer on countries.
//   FillExtrusionGraphBuildTests       — Style: fill@0, fill-extrusion@1 (constant height — teeth (b)/(c) are about allocation/scheduling/disposal STRUCTURE, not the bake; tooth (a) already covers the data-driven bake byte-for-byte), line@2 (matches no LineString geometry on this polygon-only…
//   TileBackgroundQuadProjectionTests  — Globe curvature (the projection payoff) and synthetic ring encoding.

using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using System.IO;
using Cysharp.Threading.Tasks;
using MapRenderer.Unity.View.Camera;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Rendering.Materials;
using Unity.Mathematics;
using MapRenderer.Unity.View;
using SelectorInputs = MapRenderer.Unity.Rendering.Map.MapView.SelectorInputs;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Jobs.Fill;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Unity.Rendering.Style;
using System.Linq;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Geometry;
using IFeature = MapRenderer.Core.Expressions.IFeature; // aliased: a plain using would make
using Object = UnityEngine.Object;


namespace MapRenderer.Tests.Tiles
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // MeshBuildWorkSchedulerTests — shares one RecordingWorkScheduler spy with SymbolSubsystemWorkSchedulerTests
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class MeshBuildWorkSchedulerTests : BaseTestFixture
    {
        /// <summary>Spy <see cref="ISymbolTileWorkerFactory"/> — issues a pass that records the thread its
        /// <c>RunWorkerAndHandoff</c> ran on. Mirrors <c>TileSymbolKickTests</c>' spy shape; a separate,
        /// smaller copy because that fixture's spy never needs thread identity.</summary>
        private sealed class SpySymbolTileWorkerFactory : ISymbolTileWorkerFactory
        {
            public readonly List<SpySymbolTileWorkerPass> IssuedPasses = new();

            public ISymbolTileWorkerPass TryBeginBuild(string sourceId, TileId tile)
            {
                var pass = new SpySymbolTileWorkerPass();
                IssuedPasses.Add(pass);
                return pass;
            }

            /// <summary>Not under test here: the spy commits no symbol blocks, so it answers "nothing
            /// to lose" and leaves TileManager's prepared-cache probe exactly as it was.</summary>
            public bool SymbolsCachedFor(string sourceId, TileId tile) => true;
        }

        private sealed class SpySymbolTileWorkerPass : ISymbolTileWorkerPass
        {
            // A count, not a bool: the symbol pass runs before the Burst chain, so the risk is running it
            // TWICE per tile, not zero times. A bool cannot tell one run from two.
            public int RunCount;
            public bool Ran => RunCount > 0;
            public int RunThreadId = -1;

            public void RunWorkerAndHandoff(SharedDisposable<IDecodedTile> decode)
            {
                RunCount++;
                RunThreadId = Environment.CurrentManagedThreadId;
            }
        }

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Fill-only style over a real MVT fixture — same shape as the other tile-pipeline
        /// fixtures (ThrottleTests), so the fetch is genuine UniTask I/O and the mesh-build kick is the only
        /// thing under test.</summary>
        private static StyleDocument FillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] }
                }
            ]
        }");

        private static string BackgroundOnlyStyle() => @"{
            ""version"": 8,
            ""layers"": [ { ""id"": ""bg"", ""type"": ""background"",
                             ""paint"": { ""background-color"": ""#00ff00"" } } ]
        }";

        // ── the byte-fetching kick (KickMeshBuild) ────────────────────────────────────────────────────

        /// <summary>Under an injected <see cref="InlineWorkScheduler"/>, <c>KickMeshBuild</c> runs its body
        /// on the CALLING thread with zero dispatch — the WebGL-correct behaviour, impossible under
        /// <see cref="ThreadPoolWorkScheduler"/>. A kick that bypasses the injected scheduler (a direct
        /// <c>UniTask.RunOnThreadPool</c>) leaves <c>ScheduleCount</c> at 0 and fails here.</summary>
        [Test]
        public void InjectedScheduler_RunsTheMeshBuildKick_OnTheCallingThread()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MeshBuildWorkScheduler_T1"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                int caller = Environment.CurrentManagedThreadId;
                var spy    = new RecordingWorkScheduler(new InlineWorkScheduler());
                view.TileManager.WorkScheduler = spy;

                // Checked AFTER the pump, not as a loop guard: before the first LateUpdate the cover is not
                // requested, so AllTilesSettled() is vacuously true and a guard-first loop never pumps.
                for (int f = 0; f < 3000; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) break;
                }

                Assert.Greater(view.LoadedTileCount(), 0, "drive precondition: the cover must have loaded tiles.");
                Assert.IsTrue(view.AllTilesSettled(), "drive precondition: the cover must fully settle.");
                Assert.GreaterOrEqual(spy.ScheduleCount, 1,
                    "the mesh-build kick must go THROUGH the injected scheduler.");
                Assert.GreaterOrEqual(spy.BodyThreadIds.Count, 1,
                    "the body must actually have run at least once — a tooth that iterates zero entries " +
                    "would vacuously pass the per-thread-id check below.");
                foreach (int tid in spy.BodyThreadIds)
                    Assert.AreEqual(caller, tid,
                        "the kick body must run on the CALLING thread under Inline, with zero dispatch — " +
                        "the WebGL-correct behaviour.");

                Assert.Greater(view.GameObjectRenderer().DrawItemCount(), 0,
                    "positive control: at least one Mesh must have been produced, or a tooth that passes on " +
                    "an empty cover proves nothing.");
            }
            finally { view.Teardown(); }
        }

        // ── the source-less kick (KickSourcelessBackground) ───────────────────────────────────────────

        // The background kick schedules the FillMeshGraph directly on BOTH projections. Driving only
        // Mercator would leave the shipped globe scene's client untested.
        private static readonly IProjection[] BackgroundProjectionCases = { null, new SphericalProjection() };

        /// <summary>The sourceless-background sibling of
        /// <c>InjectedScheduler_RunsTheMeshBuildKick_OnTheCallingThread</c>. The background kick does not
        /// go through the work-scheduler seam, so <c>spy.ScheduleCount</c> stays 0. <c>ScheduleCount == 0</c>
        /// alone cannot tell "absent" from "never kicked", so the settle + registered-mesh check accompanies
        /// it.</summary>
        [Test]
        public void SourcelessBackgroundKick_ReachesNoWorkScheduler_AndStillRegistersItsMesh(
            [ValueSource(nameof(BackgroundProjectionCases))] IProjection projection)
        {
            var go = Track(new GameObject("MeshBuildWorkScheduler_T2"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 3;
            view.Config.TileSelection.MaxZoom = 3;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera(projection: projection);
            try
            {
                view.LoadTestStyle(null, Cam(0, 0, 3), StyleParser.Parse(BackgroundOnlyStyle()));

                var spy = new RecordingWorkScheduler(new InlineWorkScheduler());
                view.TileManager.WorkScheduler = spy;

                // Checked AFTER the pump, not as a loop guard: before the first LateUpdate the cover hasn't
                // been requested yet, so LoadedTileCount() == 0 and AllTilesSettled() is vacuously true.
                int guard = 0;
                while (guard++ < 10000)
                {
                    view.LateUpdate();
                    if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) break;
                }

                Assert.Greater(view.LoadedTileCount(), 0, "drive precondition: the cover must have loaded tiles.");
                Assert.IsTrue(view.AllTilesSettled(), "drive precondition: the cover must fully settle.");

                Assert.AreEqual(0, spy.ScheduleCount,
                    "the source-less kick must reach NO IWorkScheduler.Schedule<T> call — it schedules the " +
                    "graph directly.");
                Assert.Greater(view.GameObjectRenderer().DrawItemCount(), 0,
                    "positive control: the tile must have settled WITH its mesh registered — 'absent' and " +
                    "'never kicked' are indistinguishable without this.");
            }
            finally { view.Teardown(); }
        }

        // ── the MeshBuildGateForTest / WorkScheduler mutual-exclusion guard ───────────────────────────

        /// <summary>Arming <see cref="TileManager.MeshBuildGateForTest"/> while
        /// <see cref="TileManager.WorkScheduler"/> is an <see cref="InlineWorkScheduler"/> (or the symmetric
        /// order) must throw at the SETTER. Non-obvious why: under Inline the gate park runs on the main
        /// thread, and its only release (teardown's <c>_lifetimeCts.Cancel()</c>) is main-thread work too, so
        /// the combination hangs. No pump runs, and the gate starts <c>initialState: true</c>, so a broken
        /// guard cannot park the main thread.</summary>
        [Test]
        public void MeshBuildGateForTest_AndTheInlineScheduler_AreMutuallyExclusive()
        {
            var go = Track(new GameObject("MeshBuildWorkScheduler_T5"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.WithTestCamera(); // constructs View (and with it TileManager) — no style, no pump.
            try
            {
                var tm = view.TileManager;

                tm.WorkScheduler = new InlineWorkScheduler();
                Assert.Throws<InvalidOperationException>(
                    () => tm.MeshBuildGateForTest = new ManualResetEventSlim(true),
                    "arming the gate while WorkScheduler is Inline must throw at the setter.");

                tm.WorkScheduler = WorkSchedulerFactory.ForCurrentPlatform(); // reset before the symmetric order
                tm.MeshBuildGateForTest = new ManualResetEventSlim(true);
                Assert.Throws<InvalidOperationException>(
                    () => tm.WorkScheduler = new InlineWorkScheduler(),
                    "selecting Inline while the gate is armed must throw at the setter (the symmetric order).");

                // The guard reads IWorkScheduler.RunsInline: a decorator wrapping Inline deadlocks just the same,
                // and a concrete-type check misses it. The gate is still armed from the clause above.
                Assert.Throws<InvalidOperationException>(
                    () => tm.WorkScheduler = new RecordingWorkScheduler(new InlineWorkScheduler()),
                    "selecting a WRAPPED Inline scheduler (RunsInline == true via forwarding) while the gate " +
                    "is armed must throw at the setter just as the bare InlineWorkScheduler does — a " +
                    "concrete-type check on the setter would miss this.");
            }
            finally { view.Teardown(); }
        }

        // ── a POPULATED symbol pass under Inline — the tests above only drive symbolPass == null ──────

        /// <summary>The tests above set no <c>TileManager.SymbolWorkerFactory</c>, so <c>symbolPass</c> in
        /// <c>KickMeshBuild</c> is always <c>null</c>. This wires a spy factory so <c>TryBeginBuild</c>
        /// returns a real pass, and asserts its <c>RunWorkerAndHandoff</c> ran once, on the CALLING thread,
        /// inside the SAME Inline-dispatched kick as the mesh pass.</summary>
        [Test]
        public void InjectedScheduler_RunsAPopulatedSymbolPass_OnTheCallingThread()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("MeshBuildWorkScheduler_T6"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();
            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                int caller = Environment.CurrentManagedThreadId;
                var symbolSpy = new SpySymbolTileWorkerFactory();
                view.TileManager.SymbolWorkerFactory = symbolSpy;
                view.TileManager.WorkScheduler = new InlineWorkScheduler();

                for (int f = 0; f < 3000; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) break;
                }

                Assert.Greater(view.LoadedTileCount(), 0, "drive precondition: the cover must have loaded tiles.");
                Assert.IsTrue(view.AllTilesSettled(), "drive precondition: the cover must fully settle.");
                Assert.Greater(symbolSpy.IssuedPasses.Count, 0,
                    "drive precondition: TryBeginBuild must have been called at least once, issuing a " +
                    "POPULATED (non-null) pass — every prior WorkScheduler tooth drives symbolPass == null " +
                    "and cannot see this path at all.");

                int ranCount = 0;
                foreach (SpySymbolTileWorkerPass pass in symbolSpy.IssuedPasses)
                {
                    if (!pass.Ran) continue;
                    ranCount++;
                    Assert.AreEqual(caller, pass.RunThreadId,
                        "a populated symbol pass's RunWorkerAndHandoff must run on the CALLING thread under " +
                        "Inline — it rides inside the SAME dispatched body as the mesh pass " +
                        "(symbolPass?.RunWorkerAndHandoff(decode), called from KickMeshBuild).");
                    // EXACTLY ONCE per issued pass, not "at least once": a second invocation from the
                    // prologue-complete arm would double-run it.
                    Assert.AreEqual(1, pass.RunCount,
                        "a populated symbol pass's RunWorkerAndHandoff must run EXACTLY ONCE — it rides " +
                        "inside KickMeshBuild's one-shot worker pass, and a second call anywhere on the mesh " +
                        "build path would double-run it.");
                }
                Assert.Greater(ranCount, 0,
                    "at least one issued pass must actually have RUN — a tooth over zero ran passes would " +
                    "vacuously pass the per-thread-id check above.");
            }
            finally { view.Teardown(); }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // PreparedCacheSymbolCoverageTests — EditMode settle is symbol-silent (see TileSymbolKickTests)
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class PreparedCacheSymbolCoverageTests : BaseTestFixture
    {
        private const string SourceId = "maplibre";
        private const string FontName = "LatinFont";

        // Same z=4 tile the rest of the prepared-cache teeth track — well inside a tile, no boundary edge case.
        private static readonly TileId TrackedTile = new TileId { Z = 4, X = 8, Y = 7 };

        private static readonly SymbolTileStore.Key TrackedKey =
            new SymbolTileStore.Key(SourceId, TrackedTile);

        /// <summary>Fill + symbol over the same source — the shape every real style has, and the one the
        /// existing prepared-cache restyle teeth deliberately lack.</summary>
        private static StyleDocument FillAndLabelStyleA() => StyleParser.Parse(@"{
            ""version"": 8,
            ""glyphs"": ""https://example.invalid/{fontstack}/{range}.pbf"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                { ""id"": ""labels"", ""type"": ""symbol"", ""source"": ""maplibre"", ""source-layer"": ""centroids"",
                  ""layout"": { ""text-field"": ""{NAME}"", ""text-size"": 16, ""text-font"": [""LatinFont""] } }
            ]
        }");

        /// <summary>A different style — different layer set, so the content token differs and the restyle
        /// takes the full-rebuild arm (which is what Clears the symbol store).</summary>
        private static StyleDocument OtherStyleB() => StyleParser.Parse(@"{
            ""version"": 8,
            ""glyphs"": ""https://example.invalid/{fontstack}/{range}.pbf"",
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 10, 10, 200, 1] } }
            ]
        }");

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Blocking, main-thread-only wait for a <see cref="UniTask"/> (mirrors
        /// <c>PreparedCacheTests.SpinToCompleted</c>).</summary>
        private static void SpinToCompleted(UniTask task, int timeoutMs = 20000)
        {
            var t = task.Preserve();
            t.WaitOffPlayerLoop(timeoutMs);
            t.GetAwaiter().GetResult();
        }

        /// <summary>Settles the cover with <c>AwaitInFlightMeshBuilds</c>, not <c>DrainMeshBuilds</c>: the
        /// symbol pass rides the mesh kick task, so only the awaiting form lets a label build land.</summary>
        private static void PumpUntilSettled(MapView view, int maxTicks = 2000)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                view.LateUpdate();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) return;
                view.AwaitInFlightMeshBuilds();
            }
        }

        private static SymbolTileStore Store(MapView view) => view.View.SymbolSubsystem.Store();

        /// <summary>Pumps until the tracked tile has a committed symbol block, and reports whether it got one.
        /// Bounded — a fixture that never commits must fail loud, not spin.</summary>
        private static bool PumpUntilLabelsCommitted(MapView view, int maxTicks = 600)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                if (Store(view).DebugBlockFor(TrackedKey) != null) return true;
                view.LateUpdate();
                view.AwaitInFlightMeshBuilds();
            }
            return Store(view).DebugBlockFor(TrackedKey) != null;
        }

        /// <summary>A view on the real <c>MapView.SetStyle</c> path with a fixture glyph source, so the
        /// symbol pipeline is LIVE (no network) and a label build can actually commit.</summary>
        private static MapView NewLabelledRestyleView(byte[] bytes, out GameObject go)
        {
            go = new GameObject("MapView_UMR139_SymbolCoverage");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0; // uncapped — synchronous whole-cover eviction+transfer
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(bytes);

            byte[] latinGlyphs = File.ReadAllBytes(
                Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes"));
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = latinGlyphs };
            view.View.SymbolSubsystem.GlyphSourceFactoryOverride = _ => TestGlyphSource.FromRanges(ranges);

            // Seed the camera BEFORE any SetStyle: SetStyle builds the render layers at the CURRENT zoom.
            view.View.Camera.SetProperties(Cam(10, 10, 4.0));
            view.View.Camera.SyncToCamera();
            return view;
        }

        private static void PanOutOfCover(MapView view)
            => view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });

        private static void PanBackIntoCover(MapView view)
            => view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });

        /// <summary>A prepared-cache hit must never re-show a tile with its labels missing. A
        /// full-rebuild restyle Clears the symbol store while the mesh cache keeps its entries, so the
        /// pan-back tile came back as geometry only — and a hit is never pumped again, so the loss was
        /// permanent.</summary>
        [Test]
        public void StyleRoundTrip_AfterPanOut_PanBackKeepsItsLabels()
        {
            var view = NewLabelledRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                SpinToCompleted(view.SetStyle(FillAndLabelStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");
                Assert.IsTrue(PumpUntilLabelsCommitted(view),
                    "drive precondition: the fixture must actually commit labels under A — without this the " +
                    "whole tooth is vacuous (it would 'pass' against a symbol pipeline that never runs).");

                PanOutOfCover(view);
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must leave cover.");
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred entries into the cache.");

                SpinToCompleted(view.SetStyle(OtherStyleB(), "B"));
                PumpUntilSettled(view);
                SpinToCompleted(view.SetStyle(FillAndLabelStyleA(), "A"));
                Assert.IsNull(Store(view).DebugBlockFor(TrackedKey),
                    "drive precondition: the restyle must have dropped the warm symbol block — that asymmetry " +
                    "against the surviving mesh cache entry IS the trigger under test.");

                PanBackIntoCover(view);
                // Bounded, not one tick: a refused hit re-shows via fetch+build, which needs several.
                for (int f = 0; f < 600 && !view.TryGetBuiltTile(TrackedTile); f++)
                {
                    view.LateUpdate();
                    view.AwaitInFlightMeshBuilds();
                }
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must re-show at all.");

                Assert.IsTrue(PumpUntilLabelsCommitted(view),
                    "DECISIVE: the re-shown tile must end with symbol coverage. Serving it from the prepared " +
                    "mesh cache marks it Built+FetchCompleted, so PumpPending skips it forever and the symbol " +
                    "kick never fires — geometry returns and every label is gone, permanently.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>The non-degeneracy companion: within ONE style the warm symbol store makes a pan-back a
        /// FULL hit, so tightening the hit predicate must not cost it. Refusing every symbol-bearing hit
        /// passes the tooth above and fails this one.</summary>
        [Test]
        public void PanBackWithinOneStyle_StillServesAFullCacheHit()
        {
            var view = NewLabelledRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                SpinToCompleted(view.SetStyle(FillAndLabelStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");
                Assert.IsTrue(PumpUntilLabelsCommitted(view), "drive precondition: labels must commit under A.");

                PanOutOfCover(view);
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must leave cover.");
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred entries into the cache.");
                Assert.IsNotNull(Store(view).DebugBlockFor(TrackedKey),
                    "drive precondition: no restyle happened, so the symbol block must still be warm.");

                int hitsBefore = view.PreparedCacheHits();
                PanBackIntoCover(view);
                view.LateUpdate();

                Assert.Greater(view.PreparedCacheHits(), hitsBefore,
                    "a within-style pan-back must still register a cache hit — the symbol store kept this " +
                    "tile's block warm, so nothing is lost by serving it.");
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(), "a cache hit must not start a build.");
                Assert.IsNotNull(Store(view).DebugBlockFor(TrackedKey),
                    "the hit must restore the warm block, not drop it.");
            }
            finally
            {
                view.Teardown();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // PreparedCacheTests — over a zoom-interpolate fill-color style fixture
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class PreparedCacheTests : BaseTestFixture
    {
        // z=4 tile containing (lon=10, lat=10) — verified to sit comfortably inside a tile, away from any
        // tile-boundary floating-point edge case.
        private static readonly TileId TrackedTile = new TileId { Z = 4, X = 8, Y = 7 };

        private static StyleDocument InterpFillStyle()
        {
            string path = Path.Combine(Application.dataPath, "Fixtures", "interp-fill-style.json");
            FileAssert.Exists(path);
            return StyleParser.Parse(File.ReadAllText(path));
        }

        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Deterministically settles the cover without Thread.Sleep: each tick kicks builds, then
        /// <c>DrainMeshBuilds</c> spins the kicked ThreadPool builds to completion, so the next tick consumes
        /// them. No frame yielding — EditMode needs immediate Object.Destroy semantics for the lifetime teeth.</summary>
        private static void PumpUntilSettled(MapView view, int maxTicks = 2000)
        {
            for (int f = 0; f < maxTicks; f++)
            {
                view.LateUpdate();
                view.DrainMeshBuilds();
                if (view.LoadedTileCount() > 0 && view.AllTilesSettled())
                    return;
            }
        }

        private static int CountMeshObjects() => Resources.FindObjectsOfTypeAll<Mesh>().Length;

        // ── Cached meshes: live while held, destroyed exactly once ────────────────────────────

        [Test]
        public void CachedMeshes_LiveWhileHeld_DestroyedOnce()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go = Track(new GameObject("MapView_S82_LiveThenDestroyed"));
            var view     = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // uncapped — this cache test asserts synchronous whole-cover eviction+transfer

            int meshBefore = CountMeshObjects();

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile));
                Mesh trackedMesh = view.GetTileMeshes(TrackedTile)[0];
                Assert.Greater(CountMeshObjects() - meshBefore, 0, "Real load must create Mesh objects (non-vacuous).");

                // Evict — transfers to the cache (well within the default byte budget / count cap; nothing
                // else competes for eviction here).
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "TrackedTile must leave the cover.");
                PumpUntilSettled(view);

                // POSITIVE CONTROL: the cached mesh is STILL ALIVE. LRU eviction destroying a mesh is
                // covered in PreparedTileCacheTests.Bounded_EvictsLru_FreesMesh.
                Assert.IsTrue(trackedMesh != null,
                    "POSITIVE CONTROL: an evicted-but-cached mesh must remain ALIVE (the cache holds it — " +
                    "proving retention, not that release already destroyed it).");
            }
            finally
            {
                view.Teardown();
            }

            // DECISIVE: Teardown (TileManager.Dispose -> _prepared.Dispose()) must destroy the still-cached
            // mesh — exactly once (the POSITIVE CONTROL above already rules out an earlier premature destroy).
            int meshAfterTeardown = CountMeshObjects();
            Assert.LessOrEqual(meshAfterTeardown, meshBefore,
                "After Teardown, mesh count must return to baseline — the PreparedTileCache's held mesh must " +
                "be destroyed exactly once, not leaked and not double-freed.");
        }

        // ── Backend reuse on a hit, across all three backends ──────────────────────────────────
        // EditMode only: BRG/GameObject do not settle a first visit under the manual PlayMode drive.

        [TestCase(RenderBackend.Entities)]
        [TestCase(RenderBackend.Brg)]
        [TestCase(RenderBackend.GameObject)]
        public void BackendReuse_OnHit_SnapshotStaysPopulated(RenderBackend backend)
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go = Track(new GameObject($"MapView_S82_BackendReuse_{backend}"));
            var view     = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.Config.Backend = backend;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // uncapped — this cache test asserts synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), $"[{backend}] TrackedTile must be built on first visit.");

                // Captured BEFORE eviction: mesh identity is the only check a disguised re-fetch/re-mesh
                // cannot satisfy (see the DECISIVE clause below).
                Mesh[] originalMeshes = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(originalMeshes);
                Assert.GreaterOrEqual(originalMeshes.Length, 1);
                Mesh originalMesh = originalMeshes[0];
                int hitsBefore = view.PreparedCacheHits();

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), $"[{backend}] TrackedTile must leave the cover.");
                PumpUntilSettled(view);

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();
                int kicks = view.TileBuildsStartedLastTick();
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile),
                    $"[{backend}] TrackedTile must be re-built (from cache) on revisit — falsifier: a backend " +
                    "that assumed it owned/destroyed the mesh on RemoveItem would draw nothing here.");
                Assert.AreEqual(0, kicks, $"[{backend}] the revisit must be a cache hit (no re-mesh build).");

                Mesh[] meshes = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshes);
                Assert.GreaterOrEqual(meshes.Length, 1);
                Assert.IsTrue(meshes[0] != null, $"[{backend}] the re-added mesh must be alive.");

                // DECISIVE: kicks == 0 also holds for a miss whose kick is deferred to the NEXT tick. Mesh
                // IDENTITY across the revisit, plus the cache's own hit counter, tells a real hit apart.
                Assert.AreSame(originalMesh, meshes[0],
                    $"[{backend}] the revisit mesh must be the SAME instance as before eviction — a genuine " +
                    "prepared-cache hit reuses the held Mesh, it never rebuilds an equivalent one.");
                Assert.Greater(view.PreparedCacheHits(), hitsBefore,
                    $"[{backend}] the revisit must register on the cache's own hit counter.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Enabled = false: no probe, no transfer ─────────────────────────────────────────────

        /// <summary>
        /// With <see cref="MapRenderer.Unity.Rendering.Map.PreparedTileCacheConfig.Enabled"/> = false, the
        /// cache must be entirely bypassed: a revisit is ALWAYS a miss/re-prepare (never a hit), and a
        /// released tile's meshes are DESTROYED immediately rather than transferred/kept alive — the exact
        /// the disabled behaviour. Falsifier: a probe/transfer that ignores the toggle would register a hit
        /// and/or leave the evicted mesh alive (as the ENABLED teeth prove it does when true).
        /// </summary>
        [Test]
        public void CacheDisabled_Revisit_AlwaysReprepares_NoTransfer()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var style    = InterpFillStyle();
            var src      = TestDataSource.FromBytes(bytes);
            var go = Track(new GameObject("MapView_S82_CacheDisabled"));
            var view     = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            // Set BEFORE WithTestCamera() — that call constructs MapView/TileManager, which reads
            // PreparedCache.Enabled once at construction.
            view.Config.PreparedCache.Enabled = false;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick        = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick      = int.MaxValue;
            view.Config.MaxReleasesPerTick        = 0; // uncapped — this cache test asserts synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "TrackedTile must be built on first visit.");
                Assert.Greater(view.PreparedCacheMisses(), 0,
                    "The probe still counts a miss when disabled (it never finds anything cached).");
                Assert.AreEqual(0, view.PreparedCacheHits(), "Disabled must never register a hit.");

                Mesh originalMesh = view.GetTileMeshes(TrackedTile)[0];
                Assert.IsNotNull(originalMesh);

                // Evict: pan far away — the tile leaves the cover.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "TrackedTile must leave the cover.");

                // DECISIVE (no transfer): disabled destroys the released mesh immediately, like
                // DestroyTrackedMeshes. Immediate Object.Destroy is why this test is EditMode-only.
                Assert.IsTrue(originalMesh == null,
                    "DECISIVE: with the cache disabled, a released tile's mesh must be destroyed immediately " +
                    "(no transfer-to-cache) — a live mesh here would mean Enabled=false failed to gate the " +
                    "transfer site in ReleaseTile.");

                PumpUntilSettled(view);

                // Revisit: pan back to the same tile.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();
                PumpUntilSettled(view);

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "TrackedTile must be rebuilt on revisit.");
                Mesh revisitMesh = view.GetTileMeshes(TrackedTile)[0];
                Assert.IsNotNull(revisitMesh);
                Assert.AreNotSame(originalMesh, revisitMesh,
                    "A disabled cache can never hand back the original mesh (it was destroyed on release) — " +
                    "the revisit must be a genuinely fresh prepare.");
                Assert.AreEqual(0, view.PreparedCacheHits(), "Disabled must never register a hit, ever.");
                Assert.Greater(view.PreparedCacheMisses(), 1,
                    "The revisit must register a SECOND miss (re-prepared again), not a hit.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── (g)'s revisit clause: a two-mesh tile, cache hit, no re-kick ────────────────────────

        /// <summary>
        /// A two-mesh (fill + line) tile evicted to the cache and revisited is a PURE cache hit:
        /// <see cref="TileManager.BuildTileFromCache"/> is synchronous admission-time work, so
        /// <c>TileBuildsStartedLastTick()</c> reads 0 on the tick the tile is built again. That catches a
        /// cache that silently RE-BUILT, which produces the same mesh and hit counts.
        /// </summary>
        [Test]
        public void TwoMeshTile_RevisitAfterEviction_IsACacheHit_WithNoReKick()
        {
            byte[] bytes = SampleTileFixture.Bytes();
            var src      = TestDataSource.FromBytes(bytes);
            var style    = StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
                ""layers"": [
                    { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                      ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                    { ""id"": ""geolines-stroke"", ""type"": ""line"", ""source"": ""maplibre"",
                      ""source-layer"": ""geolines"",
                      ""paint"": { ""line-color"": [""rgba"", 100, 200, 50, 1], ""line-width"": 10 } }
                ]
            }");
            var go = Track(new GameObject("MapView_S89_TwoMeshRevisit"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0; // uncapped — synchronous whole-cover eviction+transfer

            try
            {
                view.LoadTestStyle(src, Cam(10, 10, 4.0), style: style);
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must build on first visit.");
                Assert.AreEqual(2, view.GetTileMeshes(TrackedTile)?.Length ?? 0,
                    "drive precondition: both layers must produce a real mesh — the revisit clause needs a " +
                    "genuinely two-mesh tile, not one with an empty line layer.");

                // Evict — transfers both meshes to the cache.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "the tile must leave the cover.");
                PumpUntilSettled(view);

                int hitsBefore = view.PreparedCacheHits();

                // Revisit — one deterministic tick: BuildTileFromCache runs synchronously on admission.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile),
                    "a cache hit must build the tile SYNCHRONOUSLY on admission — no fetch, no kick, no " +
                    "async pipeline (BuildTileFromCache's own contract).");
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(),
                    "no re-kick on a hit — the falsifier: a shallow cache that silently RE-BUILT instead of " +
                    "transferring would start a build on this exact tick.");
                // Greater-than, not +1: the whole cover re-enters on one pan, and every evicted tile in it
                // registers a hit on this tick. TrackedTile's own hit is pinned by the other clauses.
                Assert.Greater(view.PreparedCacheHits(), hitsBefore,
                    "the revisit must register at least one cache hit — including TrackedTile's own.");
                Assert.AreEqual(2, view.GetTileMeshes(TrackedTile)?.Length ?? 0,
                    "the cache hit must restore BOTH meshes — a shallow cache that only remembered one " +
                    "layer would show here.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── The prepared cache survives a restyle ──────────────────────────────────────────────
        // These drive MapView.SetStyle: LoadTestStyle never sets CurrentStyle, so it cannot reach the purge.

        private static StyleDocument TwoLayerStyleA() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                { ""id"": ""geolines-stroke"", ""type"": ""line"", ""source"": ""maplibre"",
                  ""source-layer"": ""geolines"",
                  ""paint"": { ""line-color"": [""rgba"", 100, 200, 50, 1], ""line-width"": 10 } }
            ]
        }");

        /// <summary>A style entirely unrelated to A — the "B" arm of an A→B→A drive.</summary>
        private static StyleDocument SingleLayerStyleB() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""other"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""other-fill"", ""type"": ""fill"", ""source"": ""other"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 5, 5, 5, 1] } }
            ]
        }");

        /// <summary>A single FILL layer at id "shape-layer" — the closed-hole drive's first arm.</summary>
        private static StyleDocument SingleFillLayerStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""shape-layer"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        /// <summary>The SAME id "shape-layer", now a LINE layer — same ordered layer id, a different TYPE at
        /// the same index. The closed-hole drive's returning arm.</summary>
        private static StyleDocument SingleLineLayerStyle_SameId() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""shape-layer"", ""type"": ""line"", ""source"": ""maplibre"",
                  ""source-layer"": ""geolines"",
                  ""paint"": { ""line-color"": [""rgba"", 100, 200, 50, 1], ""line-width"": 10 } }
            ]
        }");

        /// <summary>Blocking, main-thread-only wait for a <see cref="UniTask"/> (mirrors
        /// <c>GeoJsonSourceTests.SpinToCompleted</c>).</summary>
        private static void SpinToCompleted(UniTask task, int timeoutMs = 20000)
        {
            var t = task.Preserve();
            t.WaitOffPlayerLoop(timeoutMs);
            t.GetAwaiter().GetResult();
        }

        /// <summary>Wires a view for the real <see cref="MapView.SetStyle"/> path (never LoadTestStyle) — a
        /// tiles[]-only vector source needs no TileJSON fetch, so SetStyle never actually awaits network.</summary>
        private static MapView NewRestyleView(byte[] bytes, out GameObject go)
        {
            go = new GameObject("MapView_UMR95_Restyle");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0; // uncapped — synchronous whole-cover eviction+transfer
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(bytes);
            // Seed the camera BEFORE any SetStyle: SetStyle builds the render layers at the CURRENT zoom.
            view.View.Camera.SetProperties(Cam(10, 10, 4.0));
            view.View.Camera.SyncToCamera();
            return view;
        }

        /// <summary>A style round-trip re-shows without a rebuild. RED (DERIVED, not run): a
        /// <c>SetSources</c> that purged unconditionally would make the pan-back a MISS.</summary>
        [Test]
        public void StyleRoundTrip_AfterPanOut_ReShowsWithoutRebuild()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");

                // Pan out of cover — transfers A's meshes into the cache.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                Assert.IsFalse(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must leave cover.");
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred entries into the cache.");

                SpinToCompleted(view.SetStyle(SingleLayerStyleB(), "B"));
                PumpUntilSettled(view);

                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));
                int hitsBefore = view.PreparedCacheHits();

                // Pan back — one LateUpdate: a hit is synchronous admission-time work.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "the returning style must re-show without a rebuild.");
                Assert.AreEqual(0, view.TileBuildsStartedLastTick(), "a cache hit must not start a build.");
                Assert.Greater(view.PreparedCacheHits(), hitsBefore, "the revisit must register a cache hit.");
                Assert.AreEqual(2, view.GetTileMeshes(TrackedTile)?.Length ?? 0, "both layers' meshes must be restored.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>The hole a review found in the id-based design: with the cache token derived from
        /// style CONTENT (not the caller-asserted <c>styleId</c>, a file path), a same-styleId return with a
        /// layer-TYPE swap at the same index now mints a DIFFERENT token, so the probe misses instead of
        /// serving a fill mesh under a line-typed layer. RED: derive the token from <c>StyleId</c> alone.</summary>
        [Test]
        public void SameIdReturn_WithLayerTypeSwapAtSameIndex_NeverServesTheStaleBake()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                SpinToCompleted(view.SetStyle(SingleFillLayerStyle(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred the fill mesh into the cache.");

                SpinToCompleted(view.SetStyle(SingleLayerStyleB(), "B"));
                PumpUntilSettled(view);

                // Same styleId "A" again — same one layer id ("shape-layer"), now a LINE layer. The content
                // digest differs (the type changed), so this mints a fresh token, never seen before.
                SpinToCompleted(view.SetStyle(SingleLineLayerStyle_SameId(), "A"));

                int hitsBeforeRevisit   = view.PreparedCacheHits();
                int missesBeforeRevisit = view.PreparedCacheMisses();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.AreEqual(hitsBeforeRevisit, view.PreparedCacheHits(),
                    "a layer-type swap at the same index under a repeated styleId must never register a hit " +
                    "— the fill mesh baked under the OLD content must not be handed to the new line layer.");
                // Positive companion: the negative assertion above passes just as well if the pan-back admits
                // NOTHING at all. Pin that it genuinely re-admits under a MISS (a real rebuild), not silence.
                Assert.Greater(view.PreparedCacheMisses(), missesBeforeRevisit,
                    "the revisit must register a miss — the line layer must actually rebuild, not merely fail to hit.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>A fill-extrusion layer BEFORE a fill layer, same source/source-layer. With
        /// <c>MapMaterialSet.FillExtrusionMaterial</c> null, <c>RenderLayerSet.Build</c> skips the extrusion
        /// layer, so the fill layer bakes at dense index 0 — see the field-mutation teeth below.</summary>
        private static StyleDocument ExtrusionThenFillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""extrusion-layer"", ""type"": ""fill-extrusion"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-extrusion-height"": 50 } },
                { ""id"": ""shape-layer"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        /// <summary>The fix this stage lands: a MaterialSet FIELD mutated IN PLACE (a swap of REFERENCE is
        /// out of scope — both committed sets configure every material, so the only reference swap this repo
        /// can perform moves no layer) can still shift the dense layer numbering under UNCHANGED style
        /// content and id. RED: fold only content into the token, not the built numbering.</summary>
        [Test]
        public void FillExtrusionMaterialAssignedInPlace_BetweenStyleLoads_NeverServesAStaleHit()
        {
            var litSet = MapMaterialSetTestUtil.Load();
            var matSet = Track(ScriptableObject.CreateInstance<MapMaterialSet>());
            matSet.FillMaterial    = litSet.FillMaterial;
            matSet.LineMaterial    = litSet.LineMaterial;
            matSet.SymbolTextWorld = litSet.SymbolTextWorld;
            // FillExtrusionMaterial left null — the extrusion layer is skipped on the first load below.

            var go = Track(new GameObject("MapView_UMR95_MaterialFieldMutation"));
            var view = go.AddComponent<MapView>();
            view.Config.MaterialSet = matSet;
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0; // uncapped — synchronous whole-cover eviction+transfer
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(SampleTileFixture.Bytes());
            view.View.Camera.SetProperties(Cam(10, 10, 4.0));
            view.View.Camera.SyncToCamera();

            try
            {
                SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile),
                    "drive precondition: the fill layer (extrusion skipped) must build on first visit.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred the fill mesh into the cache.");

                // Same reference, a field mutated IN PLACE — the pin does not fire, but the extrusion layer
                // now builds too, shifting the fill layer from dense index 0 to dense index 1.
                matSet.FillExtrusionMaterial = litSet.FillMaterial;
                SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A")); // SAME content, SAME id

                int hitsBeforeRevisit = view.PreparedCacheHits();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.AreEqual(hitsBeforeRevisit, view.PreparedCacheHits(),
                    "a MaterialSet field mutation that shifts the dense layer numbering, under unchanged " +
                    "style content and id, must never register a hit — a shifted fill mesh would otherwise " +
                    "be served under what is now the extrusion layer's slot.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>The DECREASE-direction counterpart to
        /// <see cref="FillExtrusionMaterialAssignedInPlace_BetweenStyleLoads_NeverServesAStaleHit"/>: starts
        /// with BOTH layers built (dense ids extrusion=0, fill=1), then NULLS
        /// <c>FillExtrusionMaterial</c> so the extrusion layer drops out and the fill layer's dense id shifts
        /// DOWN to 0. RED: drop <c>LayerNumbering(Layers)</c> from the digest fold (<c>MapView.cs</c>).</summary>
        [Test]
        public void FillExtrusionMaterialNulledInPlace_BetweenStyleLoads_NeverServesAStaleHit()
        {
            var litSet = MapMaterialSetTestUtil.Load();
            var matSet = Track(ScriptableObject.CreateInstance<MapMaterialSet>());
            matSet.FillMaterial          = litSet.FillMaterial;
            matSet.LineMaterial          = litSet.LineMaterial;
            matSet.SymbolTextWorld       = litSet.SymbolTextWorld;
            matSet.FillExtrusionMaterial = litSet.FillMaterial; // assigned — both layers build on first load.

            var go = Track(new GameObject("MapView_UMR95_MaterialFieldMutation_Decrease"));
            var view = go.AddComponent<MapView>();
            view.Config.MaterialSet = matSet;
            view.Config.TileSelection.MinZoom = 4; view.Config.TileSelection.MaxZoom = 4;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick   = 64;
            view.Config.MaxMeshBuildsPerTick = 64;
            view.Config.MaxVerticesPerTick   = int.MaxValue;
            view.Config.MaxReleasesPerTick   = 0; // uncapped — synchronous whole-cover eviction+transfer
            view.View.TileSourceFactoryOverride = _ => TestDataSource.FromBytes(SampleTileFixture.Bytes());
            view.View.Camera.SetProperties(Cam(10, 10, 4.0));
            view.View.Camera.SyncToCamera();

            try
            {
                SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile),
                    "drive precondition: both the extrusion and fill layers must build on first visit.");
                Assert.AreEqual(2, view.Layers.Count,
                    "drive precondition: extrusion+fill must both be present, at dense ids [0,1].");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                PumpUntilSettled(view);
                Assert.GreaterOrEqual(view.CaptureTelemetry().PreparedCacheEntryCount, 2,
                    "drive precondition: the pan-out must have transferred BOTH dense ids' meshes " +
                    "(extrusion=0, fill=1) into the cache.");

                // Same reference, field mutated IN PLACE: the pin does not fire, the extrusion layer drops out,
                // and the fill layer shifts from dense index 1 DOWN to 0 — a PREFIX of the cached ids.
                matSet.FillExtrusionMaterial = null;
                SpinToCompleted(view.SetStyle(ExtrusionThenFillStyle(), "A")); // SAME content, SAME id
                Assert.AreEqual(1, view.Layers.Count,
                    "drive precondition: the extrusion layer must actually drop out after nulling its material.");

                int hitsBeforeRevisit   = view.PreparedCacheHits();
                int missesBeforeRevisit = view.PreparedCacheMisses();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.AreEqual(hitsBeforeRevisit, view.PreparedCacheHits(),
                    "a MaterialSet field mutation that shifts the dense layer numbering DOWN, under " +
                    "unchanged style content and id, must never register a hit — the surviving id is a " +
                    "PREFIX of the still-cached ids, so the fill layer must not be served the extrusion " +
                    "layer's stale mesh.");
                Assert.Greater(view.PreparedCacheMisses(), missesBeforeRevisit,
                    "the revisit must register a miss — the fill layer must actually rebuild, not merely " +
                    "fail to hit.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary><c>MapViewConfig.FillAntialiasing</c> bakes into VERTICES
        /// (<c>StyledFillTileBuilder</c>'s boundary band), not a uniform — a toggle changes neither the
        /// style's Root bytes nor the built layer numbering, so it must be folded into the token explicitly.
        /// RED: drop the <c>|aa=</c> component from the digest fold.</summary>
        [Test]
        public void FillAntialiasingToggle_BetweenStyleLoads_NeverServesAStaleHit()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                view.Config.FillAntialiasing = true;
                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");

                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170.0, Latitude = -60.0 });
                view.LateUpdate();
                PumpUntilSettled(view);
                Assert.Greater(view.CaptureTelemetry().PreparedCacheEntryCount, 0,
                    "drive precondition: the pan-out must have transferred entries into the cache.");

                // Same content, same id — only the config knob differs.
                view.Config.FillAntialiasing = false;
                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));

                int hitsBeforeRevisit = view.PreparedCacheHits();
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 10.0, Latitude = 10.0 });
                view.LateUpdate();

                Assert.AreEqual(hitsBeforeRevisit, view.PreparedCacheHits(),
                    "a FillAntialiasing toggle, under unchanged style content and id, must never register a " +
                    "hit — the AA-banded mesh baked before the toggle must not be served after it.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Style transitions that need real tiles and a backend (the rest are RenderLayerSet-level) ──
        // The RenderLayerSet-level tests are StyleTransitionBindingTests and RestyleSurvivorGateTests.

        private static StyleDocument TwoLayerStyleARecolored() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 10, 10, 200, 1] } },
                { ""id"": ""geolines-stroke"", ""type"": ""line"", ""source"": ""maplibre"",
                  ""source-layer"": ""geolines"",
                  ""paint"": { ""line-color"": [""rgba"", 10, 200, 10, 1], ""line-width"": 10 } }
            ]
        }");

        /// <summary>A paint-only restyle keeps every layer's material, the drawn
        /// tile meshes, and the backend instance — the in-place path never calls Layers.Build/SetSources.
        /// Anti-vacuity: the drawn set is non-empty (both layers' meshes are present before AND after).</summary>
        [Test]
        public void PaintOnlyRestyle_KeepsMaterialsMeshesAndBackend()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");

                int layerCount = view.Layers.Count;
                Assert.AreEqual(2, layerCount, "drive precondition: both layers must have taken a slot.");
                var materialsBefore = new Material[layerCount];
                for (int i = 0; i < layerCount; i++) materialsBefore[i] = view.Layers[i].Material;

                Mesh[] meshesBefore = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshesBefore, "drive precondition: the tracked tile must have drawn meshes.");
                Assert.AreEqual(2, meshesBefore.Length, "drive precondition: dense layers x tiles = 2 x 1.");

                // NewRestyleView never sets Config.Backend, so the default (RenderBackend.Entities) applies.
                var backendBefore = view.EntitiesRenderer();
                Assert.IsNotNull(backendBefore, "drive precondition: a backend must be active.");

                SpinToCompleted(view.SetStyle(TwoLayerStyleARecolored(), "A")); // paint-only: same id, in place

                for (int i = 0; i < layerCount; i++)
                    Assert.AreSame(materialsBefore[i], view.Layers[i].Material,
                        $"layer {i}'s material must survive a paint-only restyle.");

                Mesh[] meshesAfter = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshesAfter, "the tracked tile must still have drawn meshes.");
                Assert.AreEqual(meshesBefore.Length, meshesAfter.Length, "the drawn set must not shrink or grow.");
                for (int i = 0; i < meshesBefore.Length; i++)
                    Assert.AreSame(meshesBefore[i], meshesAfter[i], $"mesh {i} must be the SAME instance.");

                Assert.AreSame(backendBefore, view.EntitiesRenderer(),
                    "the backend instance must survive a paint-only restyle.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>Tooth 19: after an in-place restyle, GetTileMeshes returns the SAME objects (not merely
        /// equal-content ones) and TileManager.CurrentStyle is unchanged — moving the token would invalidate
        /// prepared-cache entries the gate has just proven are still exactly right.</summary>
        [Test]
        public void PaintOnlyRestyle_KeepsLoadedTileMeshes()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                SpinToCompleted(view.SetStyle(TwoLayerStyleA(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: A must build on first visit.");

                Mesh[] meshesBefore = view.GetTileMeshes(TrackedTile);
                var tokenBefore = view.TileManager.CurrentStyle;

                SpinToCompleted(view.SetStyle(TwoLayerStyleARecolored(), "A"));

                Mesh[] meshesAfter = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshesAfter);
                Assert.AreEqual(meshesBefore.Length, meshesAfter.Length);
                for (int i = 0; i < meshesBefore.Length; i++)
                    Assert.AreSame(meshesBefore[i], meshesAfter[i], $"mesh {i} must be the SAME instance.");
                Assert.AreEqual(tokenBefore, view.TileManager.CurrentStyle,
                    "CurrentStyle must not move on an in-place restyle — it partitions meshes by " +
                    "mesh-affecting content, which the gate has just proven byte-identical.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // The fence pair. Both need a BACKGROUND layer: without one, SourceRegistry's
        // background-identity reuse has no observer, and a fresh SourcePipeline per call would still pass.

        private static StyleDocument BackgroundAndFillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": [""rgba"", 20, 20, 20, 1] } },
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }
            ]
        }");

        /// <summary>Same layers, background RECOLORED (still transitionable — an in-place restyle).</summary>
        private static StyleDocument BackgroundRecoloredAndFillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""bg"", ""type"": ""background"", ""paint"": { ""background-color"": [""rgba"", 90, 90, 90, 1] } },
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 10, 10, 200, 1] } }
            ]
        }");

        /// <summary>Same fill layer, background REMOVED — a partial-survival restyle whose diff drops the
        /// synthetic source-less pipeline (SourcesUnchanged's pre-diff "true" goes stale exactly here).</summary>
        private static StyleDocument FillOnlyStyle_BackgroundRemoved() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                  ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 10, 10, 200, 1] } }
            ]
        }");

        /// <summary>A restyle whose SOURCES are unchanged (paint-only, background included) must keep
        /// every loaded record — both the fill layer's AND the background's own per-tile quad — by Mesh
        /// IDENTITY. Anti-vacuity: the drawn set is non-empty both before and after.</summary>
        [Test]
        public void UnchangedSources_KeepEveryRecord()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                SpinToCompleted(view.SetStyle(BackgroundAndFillStyle(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must build on first visit.");

                Mesh[] meshesBefore = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshesBefore, "drive precondition: the tracked tile must have drawn meshes.");
                Assert.AreEqual(2, meshesBefore.Length, "drive precondition: background + fill = 2 dense layers.");
                var backendBefore = view.EntitiesRenderer();

                SpinToCompleted(view.SetStyle(BackgroundRecoloredAndFillStyle(), "A"));

                Mesh[] meshesAfter = view.GetTileMeshes(TrackedTile);
                Assert.IsNotNull(meshesAfter, "the tracked tile must still have drawn meshes — not blanked.");
                Assert.AreEqual(meshesBefore.Length, meshesAfter.Length, "the drawn set must not shrink or grow.");
                for (int i = 0; i < meshesBefore.Length; i++)
                    Assert.AreSame(meshesBefore[i], meshesAfter[i], $"mesh {i} (incl. the background's) must be the SAME instance.");
                Assert.AreSame(backendBefore, view.EntitiesRenderer(), "the backend instance must survive too.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>Removing the BACKGROUND layer (sources otherwise untouched) departs the synthetic
        /// source-less pipeline — its record must be torn down, while the surviving fill layer's own record
        /// keeps its Mesh identity. This pairs with <c>UnchangedSources_KeepEveryRecord</c>: an
        /// implementation that keeps every record unconditionally (ignoring the departed-pipeline case)
        /// passes that test but fails here.</summary>
        [Test]
        public void BackgroundRemovedRestyle_TearsDownOnlyTheBackgroundRecord()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            try
            {
                SpinToCompleted(view.SetStyle(BackgroundAndFillStyle(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must build on first visit.");

                // Background is declared FIRST, so it takes slot 0 and the fill slot 1 — but GetTileMeshes
                // returns CONSUME order, so find each mesh by material index, never by position.
                Mesh[] meshesBefore = view.GetTileMeshes(TrackedTile);
                int[]  indicesBefore = view.GetTileMaterialIndices(TrackedTile);
                Assert.AreEqual(2, meshesBefore.Length, "drive precondition: background + fill = 2 dense layers.");
                int fillSlotIndexBefore = System.Array.IndexOf(indicesBefore, 1);
                int bgSlotIndexBefore   = System.Array.IndexOf(indicesBefore, 0);
                Assert.GreaterOrEqual(fillSlotIndexBefore, 0, "drive precondition: slot 1 (the fill layer) must have a mesh.");
                Assert.GreaterOrEqual(bgSlotIndexBefore, 0, "drive precondition: slot 0 (the background) must have a mesh.");
                Mesh fillMeshBefore = meshesBefore[fillSlotIndexBefore];
                Mesh bgMeshBefore   = meshesBefore[bgSlotIndexBefore];

                SpinToCompleted(view.SetStyle(FillOnlyStyle_BackgroundRemoved(), "A"));

                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "the fill layer must still be drawn — not evicted wholesale.");
                Mesh[] meshesAfter = view.GetTileMeshes(TrackedTile);
                int[]  indicesAfter = view.GetTileMaterialIndices(TrackedTile);
                Assert.AreEqual(1, meshesAfter.Length,
                    "GetTileMeshes must show only the surviving fill layer — NOTE this alone would also read " +
                    "'1' if the background's record were merely orphaned (its OLD pipeline slot number falls " +
                    "outside the NEW, shrunk _sources.Count either way), so it is not decisive by itself.");
                Assert.AreEqual(1, indicesAfter[0], "the surviving mesh must still be at slot 1 — the fill layer's slot never moved.");
                Assert.AreSame(fillMeshBefore, meshesAfter[0],
                    "the surviving fill layer's Mesh must be the SAME instance — its pipeline never departed.");
                Assert.IsTrue(bgMeshBefore == null, // Unity fake-null: decisive where the count above is not —
                    "the background's Mesh must be ACTUALLY DESTROYED, not merely orphaned in _loaded and " +
                    "hidden from GetTileMeshes by its old pipeline slot falling outside the new source count.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── the BRG re-stamp is mandatory ──────────────────────────────────────────────────────────

        private static StyleDocument ThreeFillLayersAbc() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""a"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 0, 0, 1] } },
                { ""id"": ""b"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 200, 0, 1] } },
                { ""id"": ""c"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 0, 200, 1] } }
            ]
        }");

        private static StyleDocument ThreeFillLayersReorderedCab() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""c"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 0, 200, 1] } },
                { ""id"": ""a"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 0, 0, 1] } },
                { ""id"": ""b"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 200, 0, 1] } }
            ]
        }");

        /// <summary>MANDATORY: under
        /// <c>RenderBackend.Brg</c>, a reorder restyle must move the emitted draw order too, not just the
        /// Material.renderQueue values — BRG caches its own copy (<c>DrawItem.LayerRenderQueue</c>) and only
        /// <c>SetLayerMaterials</c>'s explicit re-stamp updates it. Slots never move on a reorder (a=0, b=1,
        /// c=2 throughout); only which slot draws FIRST changes, to match the NEW declared order (c,a,b).</summary>
        [Test]
        public void ReorderRestyle_BrgEmitOrder_MatchesTheNewDeclaredOrder()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            view.Config.Backend = RenderBackend.Brg;
            try
            {
                SpinToCompleted(view.SetStyle(ThreeFillLayersAbc(), "A"));
                PumpUntilSettled(view);
                Assert.IsTrue(view.TryGetBuiltTile(TrackedTile), "drive precondition: the tile must build on first visit.");
                Assert.AreEqual(3, view.Layers.Count, "drive precondition: all three fill layers must have taken a slot.");
                int itemsBefore = view.TileManager.BrgRenderer.DrawItemCount();
                Assert.Greater(itemsBefore, 0, "drive precondition: the settled cover must have registered draw items.");

                SpinToCompleted(view.SetStyle(ThreeFillLayersReorderedCab(), "A"));
                view.LateUpdate(); // drives Rebuild, which re-sorts _sortedItems from the (re-stamped) queues

                var brg = view.TileManager.BrgRenderer;
                Assert.IsNotNull(brg, "drive precondition: the BRG backend must be active.");
                Assert.AreEqual(itemsBefore, brg.DrawItemCount(),
                    "a reorder must tear NOTHING down — every draw item registered before it must still be registered.");

                // Rebuild sorts by renderQueue ALONE and a layer's draw items all share its queue, so each
                // layer is one contiguous RUN at ANY cover size — read run order, not the first 3 positions.
                var runOrder = new List<int>(3);
                for (int i = 0; i < brg.DrawItemCount(); i++)
                {
                    int materialIndex = brg.MaterialIndexAtSorted(i);
                    if (runOrder.Count == 0 || runOrder[runOrder.Count - 1] != materialIndex)
                        runOrder.Add(materialIndex);
                }
                CollectionAssert.AreEqual(new[] { 2, 0, 1 }, runOrder,
                    "the emitted runs must be (c, a, b) — the NEW declared order — one contiguous run per layer.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>Same three fill layers, "b" REMOVED — a partial-survival restyle that tombstones slot 1
        /// while leaving slots 0 and 2 alive.</summary>
        private static StyleDocument ThreeFillLayersAcRemovedB() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""a"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 0, 0, 1] } },
                { ""id"": ""c"", ""type"": ""fill"", ""source"": ""maplibre"", ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 0, 0, 200, 1] } }
            ]
        }");

        /// <summary>A mesh build KICKED before a removal restyle must not register its payload at
        /// the slot the restyle retired. Tombstoning preserves slot WIDTH, so the consume guard's count check
        /// cannot see the retirement. Drives the build to "complete but unconsumed" (MaxConsumesPerTick=0 +
        /// AwaitInFlightMeshBuilds, never PumpUntilSettled), restyles, and only then consumes.</summary>
        /// <remarks>Runs on the DEFAULT (Entities) backend. BRG throws on the retired slot's null material
        /// inside <c>AddTileLayer</c>, which aborts the test BEFORE its assertion. Entities registers silently
        /// from a <c>default</c> material id, so the bad state stays observable to the assertion.</remarks>
        [Test]
        public void MidFlightBuild_ConsumedAfterARemovalRestyle_NeverRegistersTheRetiredSlot()
        {
            var view = NewRestyleView(SampleTileFixture.Bytes(), out var go);
            Track(go);
            view.Config.MaxConsumesPerTick = 0; // hold every built payload UNCONSUMED across the restyle
            try
            {
                SpinToCompleted(view.SetStyle(ThreeFillLayersAbc(), "A"));
                for (int f = 0; f < 3000; f++)
                {
                    view.LateUpdate();
                    view.AwaitInFlightMeshBuilds(); // neither kicks nor consumes — the backlog survives it
                    if (view.LoadedTileCount() > 0 &&
                        view.CaptureTelemetry().ConsumeBacklog >= view.LoadedTileCount()) break;
                }
                Assert.GreaterOrEqual(view.CaptureTelemetry().ConsumeBacklog, view.LoadedTileCount(),
                    "drive precondition: every cover tile must be BUILT but UNCONSUMED — that mid-flight " +
                    "state is the whole tooth; a settled cover cannot express it.");
                Assert.AreEqual(3, view.Layers.Count, "drive precondition: all three fill layers must have taken a slot.");

                SpinToCompleted(view.SetStyle(ThreeFillLayersAcRemovedB(), "A"));
                Assert.AreEqual(3, view.Layers.Count, "the slot WIDTH must not shrink — that is why a count check cannot catch this.");
                Assert.IsNull(view.Layers[1].StyleLayer, "drive precondition: slot 1 must hold a tombstone (in-place retirement, not a rebuild).");

                view.Config.MaxConsumesPerTick = 64;
                PumpUntilSettled(view);

                int[] indices = view.GetTileMaterialIndices(TrackedTile);
                Assert.IsNotNull(indices, "the tracked tile must still register its SURVIVING layers — non-vacuity.");
                CollectionAssert.Contains(indices, 0, "slot 0 (\"a\") survived the restyle and must still register.");
                CollectionAssert.Contains(indices, 2, "slot 2 (\"c\") survived the restyle and must still register.");
                Assert.IsNotNull(view.EntitiesRenderer(), "drive precondition: the Entities backend must be active.");
                CollectionAssert.DoesNotContain(indices, 1,
                    "a payload built for the RETIRED slot 1 must be freed without registering — registering it " +
                    "points a live draw item at an unregistered material id (and NREs outright on BRG).");
            }
            finally
            {
                view.Teardown();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // ProjectedAreaLodWiringTests — MapView rebuilds the LOD selector when aggressiveness changes
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// T-AGGR-WIRED: <see cref="MapView"/> must build a <see cref="ProjectedAreaLodStrategy"/> carrying the
    /// Inspector's aggressiveness value, and rebuild the selector when that value changes.
    /// </summary>
    public class ProjectedAreaLodWiringTests : BaseTestFixture
    {
        // Same fixture pose as TiltCoverGrowthTests/ProjectedAreaLodTests (globe z13 1600x900 tilt 60) —
        // aggressiveness 2.0 there measures 22, aggressiveness 1.0 measures 39.
        private static readonly double2 Viewport = new double2(1600.0, 900.0);

        private static CameraProperties FixtureCam()
            => new CameraProperties(
                new GeoCoordinate3D { Longitude = 13.405, Latitude = 52.52, Altitude = 0.0 }, 13.0, 0.0, 60.0);

        /// <summary>
        /// Config selects <see cref="TileLodMode.ProjectedArea"/> at aggressiveness 2.0; the selector
        /// <see cref="MapView"/> built reads back the 2.0 cover (22), not the ctor default's 39. A second
        /// selection at a changed value pins the knob's place in the <c>_selectorInputs</c> rebuild key.
        /// </summary>
        [Test]
        public void ProjectedAreaAggressiveness_ReachesTheSelector_ThroughMapView()
        {
            var go = Track(new GameObject("MapView_UMR125_AggrWired"));
            var view = go.AddComponent<MapView>();
            try
            {
                view.WithTestCamera(projection: new SphericalProjection());
                view.Config.TileSelection.LodMode                     = TileLodMode.ProjectedArea;
                view.Config.TileSelection.GlobeFarPlaneCap            = 8.0;
                view.Config.TileSelection.ProjectedAreaAggressiveness = 2.0;

                view.LateUpdate(); // EnsureSelector() must build ProjectedAreaLodStrategy(2.0)

                var probe = new ViewContext
                {
                    Camera     = FixtureCam(),
                    ViewportPx = Viewport,
                    Projection = new SphericalProjection(),
                };
                var cover = new List<TileId>();
                view.View.TileManager.Selector.SelectVisibleTiles(in probe, cover);

                Assert.AreEqual(22, cover.Count,
                    "aggressiveness 2.0 must reach the strategy through MapView's wiring (measured cover 22); "
                  + "39 means the ctor default (1.0) silently applied instead.");

                // Changing the knob alone (nothing else) must rebuild the selector — the _selectorInputs key.
                view.Config.TileSelection.ProjectedAreaAggressiveness = 1.0;
                view.LateUpdate();
                view.View.TileManager.Selector.SelectVisibleTiles(in probe, cover);
                Assert.AreEqual(39, cover.Count,
                    "changing ProjectedAreaAggressiveness alone must rebuild the selector; a stale cover here "
                  + "means the knob is missing from the _selectorInputs rebuild key.");
            }
            finally { view.Teardown(); }
        }

        /// <summary>
        /// <see cref="SelectorInputs.Equals(SelectorInputs)"/> is hand-written field-by-field (not
        /// a tuple — see its summary for why), which means a NINTH field added later can be silently left
        /// out of the comparison. Changing each field ALONE from a baseline must flip <c>Equals</c> to
        /// false — a field missing from the comparison passes vacuously here instead.
        /// </summary>
        [Test]
        public void SelectorInputsEquals_DistinguishesEveryField()
        {
            var baseline = new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0, maxZoom: 14,
                onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0);
            Assert.IsTrue(baseline.Equals(baseline), "sanity: an instance must equal itself");

            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: true, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 14, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "Globe");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.ScreenSpaceLod,
                minZoom: 0, maxZoom: 14, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "Lod");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 1,
                maxZoom: 14, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "MinZoom");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 15, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "MaxZoom");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 14, onScreenPx: 256, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "OnScreenPx");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 14, onScreenPx: 512, mercFarCap: 5.0, globeFarCap: 8.0, areaAggressiveness: 1.0)), "MercFarCap");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 14, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 9.0, areaAggressiveness: 1.0)), "GlobeFarCap");
            Assert.IsFalse(baseline.Equals(new SelectorInputs(globe: false, lod: TileLodMode.Flat, minZoom: 0,
                maxZoom: 14, onScreenPx: 512, mercFarCap: 4.0, globeFarCap: 8.0, areaAggressiveness: 2.0)), "AreaAggressiveness");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SourceTileGraphBuildTests — SampleTileFixture + a fill layer on countries
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SourceTileGraphBuildTests : BaseTestFixture
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        /// <summary>Fill-only style over a real MVT fixture on `countries` — the fixture this whole file
        /// shares.</summary>
        private static StyleDocument FillStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""sources"": {
                ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] }
            },
            ""layers"": [
                {
                    ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                    ""source-layer"": ""countries"",
                    ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] }
                }
            ]
        }");

        // ── Three-step progression through Prologue → Measure → Write ────────────────────────────

        /// <summary>
        /// A source tile's kicked build is a THREE-STEP polled progression: Prologue (a managed
        /// <c>IWorkScheduler</c> body) → Measure → Write (both graph jobs). The test holds the PROLOGUE on
        /// <see cref="TileManager.MeshBuildGateForTest"/> and the MEASURE step on
        /// <see cref="TileManager.GraphDepsForTest"/>, then releases them in sequence, so each step is
        /// observable alone. A pump that folds Measure into the prologue-complete arm settles one tick early.
        /// </summary>
        [Test]
        public void SourceTile_ProgressesThroughPrologueThenMeasureThenWrite_BeforeSettling()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("SourceTileGraphBuild_A"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0; // z0: exactly one covered tile
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;
            var meshGate = new ManualResetEventSlim(false);

            try
            {
                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;

                // Armed BEFORE the kick: the worker parks on it as its first statement, so the prologue is
                // held in flight from the moment it is kicked.
                view.TileManager.MeshBuildGateForTest = meshGate;

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillStyle());

                int kickTick = -1, tick = 0;
                for (; tick < 3000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                }
                Assert.GreaterOrEqual(kickTick, 0,
                    "drive precondition: the source tile's build must have been started before anything " +
                    "below can observe a step.");

                var snap = view.CaptureTelemetry();
                Assert.GreaterOrEqual(snap.PrologueInFlight, 1,
                    "the tile must be observed in its PROLOGUE step — deterministic while the gate holds " +
                    "the worker before it does any work.");
                Assert.AreEqual(0, snap.GraphMeasureInFlight,
                    "no graph may exist yet — the prologue has not handed off to ScheduleMeasureFromDecode.");

                // AwaitInFlightMeshBuilds parks on the WorkHandle only while Step is Prologue, so it cannot
                // race the hand-off or block on the still-gated measure delay.
                meshGate.Set();
                view.AwaitInFlightMeshBuilds();
                view.LateUpdate();
                tick++; // the prologue-complete tick — without it settleTick-kickTick under-reports by one

                snap = view.CaptureTelemetry();
                Assert.AreEqual(0, snap.PrologueInFlight,
                    "the prologue must have completed and handed off to the graph by now.");
                Assert.GreaterOrEqual(snap.GraphMeasureInFlight, 1,
                    "the tile must be observed in its MEASURE step — deterministic under the still-held " +
                    "delay job (GraphDepsForTest), since the measure graph cannot possibly have completed yet.");
                Assert.IsFalse(view.AllTilesSettled(),
                    "the tile must not read settled while its measure step is genuinely held incomplete.");

                // The tick floor is structural, not a timing bet: each pump arm `continue`s at most once
                // per tile per Tick, so it holds at any completion latency.
                gate[0] = 1;

                int settleTick = -1;
                for (; tick < 3000 && settleTick < 0; tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.AllTilesSettled()) settleTick = tick;
                }
                Assert.GreaterOrEqual(settleTick, 0, "the tile must eventually settle.");
                Assert.GreaterOrEqual(settleTick - kickTick, 3,
                    $"settling must take at least THREE ticks after the kick tick (kickTick={kickTick}, " +
                    $"settleTick={settleTick}) — kick@K (Prologue) → prologue-complete@K+1 (schedules " +
                    "Measure) → write-kick@K+2 (Measure complete, schedules Write) → consume@K+3 (Write " +
                    "complete). A pump that folds the measure-schedule into the prologue-complete arm would " +
                    "settle at kickTick+2, matching the RED this tooth exists to catch.");
                Assert.Greater(view.GameObjectRenderer().DrawItemCount(), 0,
                    "the both-ends rule: a progression that never produces a mesh is indistinguishable from " +
                    "a build that never happened.");
            }
            finally
            {
                // Unconditional, before any dispose: an early failure still releases both holds first.
                meshGate.Set();
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                meshGate.Dispose();
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        // ── The Burst work is not inside the prologue body, and fills allocate nothing at kick ──────

        /// <summary>
        /// The prologue body does managed work only (selection, paint bake, <c>BuildGraphRequest</c>): no
        /// <see cref="Mesh.MeshDataArray"/> is allocated at kick for a fill-only style. The PUMP hands the
        /// prologue's output to <c>TileBuildGraph.ScheduleMeasureFromDecode</c> on the NEXT tick, so
        /// <see cref="FillGraphOutput.DebugLiveCount"/> moves only then. <see cref="TileManager.GraphDepsForTest"/>
        /// holds the graph open, so the check does not race the graph's completion.
        /// </summary>
        [Test]
        public void SourceTile_BurstWorkNotInsideThePrologueBody_AndFillsAllocateNothingAtKick()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("SourceTileGraphBuild_B"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
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

                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long graphOutputBaseline = FillGraphOutput.DebugLiveCount;

                int caller = Environment.CurrentManagedThreadId;
                var spy    = new RecordingWorkScheduler(new InlineWorkScheduler());
                view.TileManager.WorkScheduler = spy;

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillStyle());

                int kickTick = -1, tick = 0;
                for (; tick < 3000 && kickTick < 0; tick++)
                {
                    view.LateUpdate();
                    if (view.TileBuildsStartedLastTick() > 0) kickTick = tick;
                }
                Assert.GreaterOrEqual(kickTick, 0,
                    "drive precondition: the source tile's prologue must have been started.");

                Assert.GreaterOrEqual(spy.ScheduleCount, 1,
                    "the prologue kick must go THROUGH the injected scheduler — the seam still carries it.");
                Assert.GreaterOrEqual(spy.BodyThreadIds.Count, 1,
                    "the body must actually have run at least once, or the per-thread-id check below is vacuous.");
                foreach (int tid in spy.BodyThreadIds)
                    Assert.AreEqual(caller, tid,
                        "under Inline the prologue body runs on the CALLING thread, with zero dispatch.");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataArray may be allocated at kick for a fill-only style — the graph arm " +
                    "allocates in its write step, not at kick.");

                // Next tick: the prologue-complete arm hands the geometry to a real Burst measure graph,
                // held open by the still-gated delay job.
                view.LateUpdate();
                Assert.Greater(FillGraphOutput.DebugLiveCount, graphOutputBaseline,
                    "a real measure graph must have been scheduled by now — the geometry left the prologue body.");
                Assert.AreEqual(0, view.MeshDataArraysAllocatedLastKick(),
                    "the measure step allocates no MeshDataArray of its own — only the write step does.");

                // Release the delay — the write step allocates exactly one array (one non-empty fill layer).
                gate[0] = 1;
                int writeTick = -1;
                for (; tick < 3000 && writeTick < 0; tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.MeshDataArraysAllocatedLastKick() > 0) writeTick = tick;
                }
                Assert.GreaterOrEqual(writeTick, 0, "the write step must eventually allocate.");
                Assert.AreEqual(1, view.MeshDataArraysAllocatedLastKick(),
                    "exactly one MeshDataArray must be allocated on the write-kick tick — one per non-empty " +
                    "fill layer.");

                for (; tick < 3000 && !view.AllTilesSettled(); tick++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }
                Assert.IsTrue(view.AllTilesSettled(), "the tile must eventually settle.");
                Assert.Greater(view.GameObjectRenderer().DrawItemCount(), 0,
                    "the both-ends rule: a progression that never produces a mesh is indistinguishable from " +
                    "a build that never happened.");
            }
            finally
            {
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        // ── The pen at every step, and teardown ──────────────────────────────────────────────────

        /// <summary>
        /// Case 1 (Prologue): a tile released while held in its PROLOGUE step on
        /// <see cref="TileManager.MeshBuildGateForTest"/>. No graph exists yet; the pen is
        /// <c>TilePrologueOutput.Dispose()</c> via <c>PendingDisposalQueue.DrainCompleted</c>'s <c>WorkHandle</c>
        /// sweep once the worker produces a result. <see cref="LayerMeshBuildCounters.DebugTotalBuildsCreated"/>
        /// is the non-vacuity witness: it advances only after the gate opens and <c>BuildGraphRequest</c> runs.
        /// </summary>
        [Test]
        public void ReleasedMidFlight_Prologue_StashesInThePen_ThenDrainsOnceTheWorkerCompletes()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("SourceTileGraphBuild_D_Prologue"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5; // NOT z0 — a z0 cover does not change under this pan, so
                                                    // nothing would ever be evicted.
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxReleasesPerTick     = 64; // evict the whole condemned cover on the pan tick
            view.WithTestCamera();

            var meshGate = new ManualResetEventSlim(false);

            try
            {
                long graphBaseline       = TileBuildGraph.DebugLiveCount;
                long requestsBaseline    = LayerMeshBuildCounters.DebugLiveBuilds;
                long totalCreatedBefore  = LayerMeshBuildCounters.DebugTotalBuildsCreated;
                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long graphOutputBaseline = FillGraphOutput.DebugLiveCount;
                long negativesBaseline   = TileBuildGraph.DebugNegativeObservations;

                view.TileManager.MeshBuildGateForTest = meshGate; // armed BEFORE any kick

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                for (int f = 0; f < 3000 && view.CaptureTelemetry().PrologueInFlight < 1; f++)
                    view.LateUpdate();
                Assert.GreaterOrEqual(view.CaptureTelemetry().PrologueInFlight, 1,
                    "drive precondition: the tile must be genuinely held in its PROLOGUE step before the pan.");

                // Evict while the prologue is gated shut. Bounded pump, not one tick: eviction can miss the
                // first LateUpdate() after the pan, and the gate stays held throughout.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                for (int f = 0; f < 3000 && view.ReleasedMidFlightCount() == 0; f++)
                    view.LateUpdate();

                Assert.Greater(view.ReleasedMidFlightCount(), 0,
                    "positive control: the tile must have been released while its prologue task was still " +
                    "genuinely in-flight (gate held), or the pen assertions below are vacuous.");
                // This precondition makes the witness below non-vacuous. The Measure case cannot hold it,
                // so it drops the witness.
                Assert.AreEqual(totalCreatedBefore, LayerMeshBuildCounters.DebugTotalBuildsCreated,
                    "the counter must NOT have advanced while the gate is shut — this case's witness below " +
                    "depends on the shared gate blocking every cover worker. Under InlineWorkScheduler the " +
                    "rest of the cover completes during the wait and the witness becomes vacuous, which is " +
                    "exactly why the Measure case dropped it. It also depends on THIS fixture's style " +
                    "declaring no `background` layer — KickSourcelessBackground runs on the main thread " +
                    "ungated by MeshBuildGateForTest and, since LayerMeshBuildCounters counts every build " +
                    "unconditionally, a background tile's build would advance " +
                    "this counter regardless of the gate; FillStyle() never declares one, so that path never " +
                    "runs here.");

                // The WorkHandle runs on after its record left _loaded; DrainCompleted disposes its result.
                // The poll cannot Await (no record), so a generous bound gives the worker real wall-clock.
                meshGate.Set();

                for (int f = 0; f < 100_000 && LayerMeshBuildCounters.DebugTotalBuildsCreated <= totalCreatedBefore; f++)
                    view.LateUpdate();
                Assert.Greater(LayerMeshBuildCounters.DebugTotalBuildsCreated, totalCreatedBefore,
                    "the released worker must have built a real ILayerMeshBuild — the non-vacuity witness: " +
                    "this counter can only advance once the gate opens, since the columns it counts do not " +
                    "exist before the worker runs.");

                for (int f = 0; f < 100_000 && LayerMeshBuildCounters.DebugLiveBuilds > requestsBaseline; f++)
                    view.LateUpdate();

                Assert.AreEqual(requestsBaseline, LayerMeshBuildCounters.DebugLiveBuilds,
                    "once the released prologue's result is drained, its build's own columns must be " +
                    "disposed — back to baseline.");
                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "no TileBuildGraph is ever created for a tile evicted before its prologue completed — " +
                    "it never reaches ScheduleMeasureFromDecode.");
                Assert.AreEqual(graphOutputBaseline, FillGraphOutput.DebugLiveCount,
                    "no measure graph is ever scheduled for a tile evicted before its prologue completed.");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload was ever allocated for this tile.");
                Assert.AreEqual(negativesBaseline, TileBuildGraph.DebugNegativeObservations,
                    "the idempotency guard must never have observed a negative live count.");
            }
            finally
            {
                meshGate.Set();
                view.Teardown();
                meshGate.Dispose();
            }
        }

        /// <summary>
        /// Case 2 (Measure): a tile released while held in its MEASURE step on
        /// <see cref="TileManager.GraphDepsForTest"/>; the prologue runs INLINE. <c>RenderTeardownRecord</c>
        /// stashes the live <see cref="TileBuildGraph"/> via <c>_pending.StashGraph</c>, because disposing it
        /// inline would <c>Complete()</c> — and block on — the gated job.
        /// </summary>
        /// <remarks>Limitation: no "a real request was created" witness survives this multi-tile cover.
        /// <see cref="LayerMeshBuildCounters.DebugTotalBuildsCreated"/> is process-wide, and under
        /// <c>InlineWorkScheduler</c> every other cover tile's prologue advances it during the drive. A
        /// single-tile cover, or a count scoped to the released tile's own graph, would close this.</remarks>
        [Test]
        public void ReleasedMidFlight_Measure_StashesInThePen_ThenDrainsOnceTheDelayJobCompletes()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("SourceTileGraphBuild_D_Measure"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5; // NOT z0 — see the Prologue case's own comment.
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxReleasesPerTick     = 64;
            view.WithTestCamera();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;

            try
            {
                long graphBaseline       = TileBuildGraph.DebugLiveCount;
                long requestsBaseline    = LayerMeshBuildCounters.DebugLiveBuilds;
                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long negativesBaseline   = TileBuildGraph.DebugNegativeObservations;

                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;
                view.TileManager.WorkScheduler     = new InlineWorkScheduler();

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                for (int f = 0; f < 3000 && view.CaptureTelemetry().GraphMeasureInFlight < 1; f++)
                    view.LateUpdate();
                Assert.GreaterOrEqual(view.CaptureTelemetry().GraphMeasureInFlight, 1,
                    "drive precondition: the tile must be genuinely held in its MEASURE step before the pan.");

                // Bounded pump, not a single tick — see the Prologue case's own comment on why (observed
                // flaky at one tick). The gate stays held throughout: nothing here lets the measure job proceed.
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                for (int f = 0; f < 3000 && view.ReleasedMidFlightCount() == 0; f++)
                    view.LateUpdate();

                Assert.Greater(view.ReleasedMidFlightCount(), 0,
                    "positive control: the tile must have been released while its measure step was still " +
                    "genuinely in-flight (gate held), or the pen assertions below are vacuous.");
                Assert.Greater(TileBuildGraph.DebugLiveCount, graphBaseline,
                    "the evicted graph must still be LIVE right after eviction — the pen defers disposal, it " +
                    "does not skip it.");

                gate[0] = 1; // release — the pen's Dispose() can now Complete() without blocking

                // Bounded but generous — see the Prologue case's own comment on why this cannot Await.
                for (int f = 0; f < 100_000 && TileBuildGraph.DebugLiveCount > graphBaseline; f++)
                    view.LateUpdate();

                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "once the gated job completes, the pen must drain: PendingDisposalQueue.DrainCompleted disposes the " +
                    "released graph on a later tick.");
                Assert.AreEqual(requestsBaseline, LayerMeshBuildCounters.DebugLiveBuilds,
                    "the graph's own builds' columns must be freed with it.");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload leak once the pen has drained — the write step never ran.");
                Assert.AreEqual(negativesBaseline, TileBuildGraph.DebugNegativeObservations,
                    "the idempotency guard must never have observed a negative live count.");
            }
            finally
            {
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary>
        /// Case 3 (Write): a tile released once its WRITE step is COMPLETE but UNCONSUMED
        /// (<c>MaxConsumesPerTick = 0</c>), which <c>RenderTeardownRecord</c> also pens. The handle is complete
        /// at release, so <see cref="MapViewTestExtensions.ReleasedMidFlightCount"/> does not count it.
        /// The post-pan cover is served absent. With consume blocked, a served tile parks at
        /// Write and holds a live graph in its record, which reads as a pen leak.
        /// </summary>
        [Test]
        public void ReleasedCompleteButUnconsumed_Write_StashesInThePen_ThenDrains()
        {
            byte[] bytes  = SampleTileFixture.Bytes();
            bool   panned = false;
            var src = TestDataSource.FromFetch(_ => UniTask.FromResult(Volatile.Read(ref panned)
                ? TileResponse.Absent(TileEncoding.Mvt)
                : new TileResponse(bytes, TileEncoding.Mvt)));
            var go = Track(new GameObject("SourceTileGraphBuild_D_Write"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5; // NOT z0 — see the Prologue case's own comment.
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxConsumesPerTick     = 0; // block consume — leave the write complete but untaken
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxReleasesPerTick     = 64;
            view.WithTestCamera();

            try
            {
                long graphBaseline       = TileBuildGraph.DebugLiveCount;
                long requestsBaseline    = LayerMeshBuildCounters.DebugLiveBuilds;
                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long negativesBaseline   = TileBuildGraph.DebugNegativeObservations;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                // Pump until the write step is complete but unconsumed — ConsumeBacklog sees it.
                for (int f = 0; f < 3000 && view.CaptureTelemetry().ConsumeBacklog < 1; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }
                Assert.GreaterOrEqual(view.CaptureTelemetry().ConsumeBacklog, 1,
                    "drive precondition: the tile's write step must be complete but unconsumed before the pan.");
                Assert.Greater(MeshDataPayload.DebugLiveAllocCount, payloadBaseline,
                    "non-vacuous precondition: the completed write must hold a real allocated payload.");

                Volatile.Write(ref panned, true);
                view.Camera.Apply(new CameraPropertiesUpdate { Longitude = 170 });
                view.LateUpdate();

                // Alone this holds even without eviction. Paired with the AreEqual below it is sound: with
                // consume blocked, an un-evicted graph has no path to disposal and fails there.
                Assert.Greater(TileBuildGraph.DebugLiveCount, graphBaseline,
                    "the evicted graph must still be LIVE right after eviction — this case's pen defers " +
                    "disposal to the drain, exactly like the genuinely-in-flight cases.");

                // A fixed pump with no early exit, so the new cover settles before the count is read.
                for (int f = 0; f < 200; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }

                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "the pen must drain the complete-but-unconsumed graph — PendingDisposalQueue.DrainCompleted disposes it " +
                    "(a Burst job cannot fault, so IsStepComplete is the only gate; the pen itself needs no wait).");
                Assert.AreEqual(requestsBaseline, LayerMeshBuildCounters.DebugLiveBuilds,
                    "the graph's own builds' columns must be freed with it.");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "the completed-but-never-taken write payload must be freed too — the pen disposes " +
                    "whatever CompleteWriteAndTakePayloads never claimed.");
                Assert.AreEqual(negativesBaseline, TileBuildGraph.DebugNegativeObservations,
                    "the idempotency guard must never have observed a negative live count.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>
        /// The Write case's hold, but parked by <see cref="TileManager.SetSources"/> with an EMPTY source
        /// list: zero cover, so no other job can flush the batch queue. The drain works when the parked
        /// graph's <see cref="TileBuildGraph.IsStepComplete"/> is already true at parking time.
        /// </summary>
        [Test]
        public void ParkedGraph_AlreadyComplete_DrainsWithoutAnyOtherJobScheduled()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("SourceTileGraphBuild_ParkedGraphDrain"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5;
            view.Config.TileSelection.MaxZoom = 5;
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxConsumesPerTick     = 0; // block consume — leave the write complete but untaken
            view.Config.MaxMeshBuildsPerTick   = 64;
            view.Config.MaxReleasesPerTick     = 64;
            view.WithTestCamera();

            try
            {
                long graphBaseline = TileBuildGraph.DebugLiveCount;

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                // Pump until the write step is complete but unconsumed — same precondition as the Write case.
                for (int f = 0; f < 3000 && view.CaptureTelemetry().ConsumeBacklog < 1; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                }
                Assert.GreaterOrEqual(view.CaptureTelemetry().ConsumeBacklog, 1,
                    "drive precondition: the tile's write step must be complete but unconsumed " +
                    "before eviction.");

                // Evict via the RenderTeardownRecord funnel with ZERO replacement sources, so nothing is
                // scheduled again for the rest of this test.
                view.TileManager.SetSources(Array.Empty<TileManager.SourceSpec>(), view.Config.Backend);

                Assert.Greater(TileBuildGraph.DebugLiveCount, graphBaseline,
                    "positive control: the evicted graph must still be LIVE right after eviction — the pen " +
                    "defers disposal to the drain.");

                // Small, bounded pump — nothing left in this test can ever schedule a new job, so a fixed
                // small budget is exactly as conclusive as a much larger one for this (already-complete) case.
                for (int f = 0; f < 1000 && TileBuildGraph.DebugLiveCount > graphBaseline; f++)
                    view.LateUpdate();

                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "the pen must drain a parked graph whose handle is already complete, with no other tick " +
                    "activity required to notice it.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>
        /// Case 4 (Teardown): the Measure case's hold, but the tile is torn down through
        /// <see cref="MapView.Teardown"/> without settling or leaving cover.
        /// <see cref="TileManager.DoDispose"/>'s graph pen, fed by the same <c>RenderTeardownRecord</c>
        /// funnel, disposes the in-flight graph rather than leaking it.
        /// </summary>
        [Test]
        public void Teardown_WhileGenuinelyInMeasure_DrainsThePen_WithoutSettling()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("SourceTileGraphBuild_D_Teardown");
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;

            try
            {
                long graphBaseline     = TileBuildGraph.DebugLiveCount;
                long requestsBaseline  = LayerMeshBuildCounters.DebugLiveBuilds;
                long payloadBaseline   = MeshDataPayload.DebugLiveAllocCount;
                long negativesBaseline = TileBuildGraph.DebugNegativeObservations;

                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;
                view.TileManager.WorkScheduler     = new InlineWorkScheduler();

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: FillStyle());

                for (int f = 0; f < 3000 && view.CaptureTelemetry().GraphMeasureInFlight < 1; f++)
                    view.LateUpdate();
                Assert.GreaterOrEqual(view.CaptureTelemetry().GraphMeasureInFlight, 1,
                    "drive precondition: the tile must be genuinely held in its MEASURE step before teardown.");

                // Release first: Complete()ing a gated job on main burns the whole spin bound. The tile is
                // still torn down WITHOUT settling or being consumed, which is the claim.
                gate[0] = 1;
                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                go = null;

                Assert.AreEqual(graphBaseline, TileBuildGraph.DebugLiveCount,
                    "DoDispose must dispose every graph still live at teardown — including one whose measure " +
                    "step was genuinely in flight, through the same pen funnel every other abandonment path " +
                    "uses.");
                Assert.AreEqual(requestsBaseline, LayerMeshBuildCounters.DebugLiveBuilds,
                    "the graph's own builds' columns must be freed with it.");
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "no MeshDataPayload leak — teardown of an in-flight graph must not strand any allocated " +
                    "Mesh.MeshDataArray (there is none here — the write step never ran).");
                Assert.AreEqual(negativesBaseline, TileBuildGraph.DebugNegativeObservations,
                    "the idempotency guard must never have observed a negative live count.");
            }
            finally
            {
                // Unconditional, before any dispose — an early failure (before Teardown() ever ran) means
                // no completed sweep has touched delayHandle yet.
                gate[0] = 1;
                delayHandle.Complete();
                if (go != null)
                {
                    view.Teardown();
                    UnityEngine.Object.DestroyImmediate(go);
                }
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        // ── A mixed fill + line tile: CompleteWriteAndTakePayloads joins each payload at ITS OWN slot ──

        /// <summary>
        /// Fill AND line both on <c>countries</c>: polygon rings drop at the line builder's
        /// <c>LineString</c> gate, so the line layer settles as an EMPTY request with no write step.
        /// <c>CompleteWriteAndTakePayloads</c> hands back one slot per REQUEST, but <c>ConsumeMeshBuild</c>
        /// tracks only a slot whose <c>Upload()</c> returned a real <see cref="Mesh"/>. A wrong-slot join is
        /// inert with one non-empty layer; the real-join case below catches it.
        /// </summary>
        [Test]
        public void MixedTile_FillAndEmptyLine_EmptyLineLayer_StillTakesASlot()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("SourceTileGraphBuild_G_EmptyLine"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            var style = StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
                ""layers"": [
                    { ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                      ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } },
                    { ""id"": ""countries-line"", ""type"": ""line"", ""source"": ""maplibre"",
                      ""source-layer"": ""countries"" }
                ]
            }");

            try
            {
                long payloadBaseline = MeshDataPayload.DebugLiveAllocCount;
                var tileId = new TileId { Z = 0, X = 0, Y = 0 };

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);
                for (int f = 0; f < 3000; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) break;
                }
                Assert.Greater(view.LoadedTileCount(), 0, "drive precondition: the tile must have loaded.");
                Assert.IsTrue(view.AllTilesSettled(), "drive precondition: the tile must settle.");

                Assert.AreEqual(1, view.GetTileMeshes(tileId)?.Length ?? 0,
                    "only the fill layer's REAL mesh may be tracked — the empty line layer's Upload() " +
                    "returns null, and ConsumeMeshBuild never calls AddTileLayer for a null mesh.");
                CollectionAssert.AreEqual(new[] { 0 }, view.GetTileMaterialIndices(tileId),
                    "the one tracked mesh must be the FILL layer's — material index 0, its declared position.");
                // Pins the fill layer's consume-disposal. The empty line never reaches the write step, so it
                // has no allocation to leak.
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "no leak: the fill layer's write-step array must be disposed at consume, and the empty " +
                    "line layer's request (settled with no write step at all — no array to leak) must not " +
                    "leave the counter elevated either.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>Fill on <c>countries</c> + line on <c>geolines</c> (the fixture's 6-feature line
        /// layer): both layers produce a real mesh, and each dense request lands at ITS OWN material index.
        /// Runs for both declaration orderings, so a bug that shows for one arrangement cannot hide.</summary>
        /// <param name="lineFirst"><see langword="false"/>: fill declared first (material index 0), line
        /// second (index 1). <see langword="true"/>: the reverse.</param>
        /// <remarks>A join that writes every payload at index 0 shows only in the fill@0/line@1 ordering:
        /// the line's payload overwrites the fill's, so the mesh count drops to 1.</remarks>
        [Test]
        public void MixedTile_FillAndLine_RealJoin_BothOrderings(
            [Values(false, true)] bool lineFirst)
        {
            const string fillLayer = @"{ ""id"": ""countries-fill"", ""type"": ""fill"", ""source"": ""maplibre"",
                ""source-layer"": ""countries"", ""paint"": { ""fill-color"": [""rgba"", 200, 50, 50, 1] } }";
            const string lineLayer = @"{ ""id"": ""geolines-stroke"", ""type"": ""line"", ""source"": ""maplibre"",
                ""source-layer"": ""geolines"",
                ""paint"": { ""line-color"": [""rgba"", 100, 200, 50, 1], ""line-width"": 10 } }";
            string layers = lineFirst ? lineLayer + "," + fillLayer : fillLayer + "," + lineLayer;
            var style = StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
                ""layers"": [" + layers + @"]
            }");

            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("SourceTileGraphBuild_G_RealJoin"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 0;
            view.Config.TileSelection.MaxZoom = 0;
            view.Config.Backend               = RenderBackend.GameObject;
            view.WithTestCamera();

            try
            {
                var tileId = new TileId { Z = 0, X = 0, Y = 0 };

                view.LoadTestStyle(src, Cam(0, 0, 0.0), style: style);
                for (int f = 0; f < 3000; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    if (view.LoadedTileCount() > 0 && view.AllTilesSettled()) break;
                }
                Assert.Greater(view.LoadedTileCount(), 0, "drive precondition: the tile must have loaded.");
                Assert.IsTrue(view.AllTilesSettled(), "drive precondition: the tile must settle.");

                Assert.AreEqual(2, view.GetTileMeshes(tileId)?.Length ?? 0,
                    $"both layers must produce a non-empty mesh (lineFirst={lineFirst}) — fill on countries, " +
                    "line on geolines (a real 6-feature line layer, unlike the empty-line case above).");
                // Material index == declared position whatever kind sits there, so always [0, 1]. The bug
                // caught is a wrong VALUE at a slot, or a missing slot.
                CollectionAssert.AreEqual(new[] { 0, 1 }, view.GetTileMaterialIndices(tileId),
                    $"MaterialIndices must join in DECLARED order (lineFirst={lineFirst}) — a permutation " +
                    "bug (writing every payload slot at index 0 instead of the request's own index) would " +
                    "corrupt this in the fill@0/line@1 ordering specifically.");
            }
            finally
            {
                view.Teardown();
            }
        }

        // ── Budget rule (job-scheduling-design.md): admission is tiles-per-Tick, and a step ─────────────
        // transition of an already-admitted tile is uncharged ──────────────────────────────────────────

        /// <summary>
        /// With <c>MaxMeshBuildsPerTick = 1</c> and a multi-tile cover, some Tick both completes a
        /// write-step transition for an admitted tile AND admits a fresh one. A budget that charged the
        /// transition would make that impossible: one unit, two claimants. Inline decode guarantees
        /// same-tick eligibility, so the scan measures budget behaviour, not decode-completion order.
        /// </summary>
        [Test]
        public void WriteTransitionTick_CanAlsoAdmitAFreshTile_UnderCapOne()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("SourceTileGraphBuild_BudgetAdmitsWhileWriting"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5; // known >= 9-tile cover
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxMeshBuildsPerTick  = 1;             // a charged transition could not share this cap
            view.Config.MaxConsumesPerTick    = 0;             // consume blocked — candidates never run dry
            view.Config.MaxVerticesPerTick    = int.MaxValue;
            view.WithTestCamera();

            try
            {
                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long graphOutputBaseline = FillGraphOutput.DebugLiveCount;

                // Inline decode gives same-tick eligibility; without it the scan measures decode order.
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle(),
                    decodeScheduler: new InlineWorkScheduler());

                // Bounded scan. No DrainMeshBuilds/PumpUntilSettled: drain ignores every per-tick cap and
                // would destroy the observation.
                bool anyStarted = false, anyAllocated = false, conjunctionSeen = false;
                for (int t = 0; t < 200; t++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    int  started   = view.TileBuildsStartedLastTick();
                    long allocated = view.MeshDataArraysAllocatedLastKick();
                    Assert.LessOrEqual(started, 1,
                        $"the cap must still bind at MaxMeshBuildsPerTick=1 on every scanned Tick (tick {t}).");
                    if (started   > 0) anyStarted   = true;
                    if (allocated > 0) anyAllocated = true;
                    if (started > 0 && allocated > 0) conjunctionSeen = true;
                }

                Assert.GreaterOrEqual(view.LoadedTileCount(), 3,
                    "non-vacuity: a 1- or 2-tile cover cannot produce the conjunction.");
                Assert.IsTrue(anyStarted,
                    "drive precondition: at least one scanned Tick must have started a tile, or this scan is dead.");
                Assert.IsTrue(anyAllocated,
                    "drive precondition: at least one scanned Tick must have completed a write transition, or " +
                    "this scan is dead.");
                Assert.IsTrue(conjunctionSeen,
                    "at least one Tick must both admit a fresh tile AND complete a write transition at cap=1 " +
                    "— impossible under the deleted two-units-per-tile rule, where a write kick and a fresh " +
                    "admission competed for the same single unit.");

                view.Teardown();
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "teardown of this unsettled cover (consume blocked) must not leak any allocated MeshDataArray.");
                Assert.AreEqual(graphOutputBaseline, FillGraphOutput.DebugLiveCount,
                    "teardown of this unsettled cover must not leak any live measure/write graph output.");
            }
            finally
            {
                view.Teardown();
            }
        }

        /// <summary>
        /// With <c>MaxMeshBuildsPerTick = 1</c>, one Tick can complete write-step transitions for TWO OR
        /// MORE admitted tiles. It holds three tiles in MEASURE, then completes them in one
        /// <see cref="MapViewTestExtensions.AwaitInFlightMeshBuilds"/> call, so one <c>LateUpdate</c>
        /// write-kicks them all. It reads <see cref="MapViewTestExtensions.MeshDataArraysAllocatedLastKick"/>,
        /// one per tile because <see cref="FillStyle"/> declares one fill layer.
        /// </summary>
        [Test]
        public void OneTick_CanCompleteTwoWriteTransitions_UnderCapOne()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("SourceTileGraphBuild_BudgetTwoWritesOneTick"));
            var view = go.AddComponent<MapView>().WithTestMaterials();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.Config.Backend               = RenderBackend.GameObject;
            view.Config.MaxMeshBuildsPerTick  = 1;
            view.Config.MaxConsumesPerTick    = 0;
            view.Config.MaxVerticesPerTick    = int.MaxValue;
            view.WithTestCamera();

            var gate    = new NativeArray<int>(1, Allocator.Persistent);
            var started = new NativeArray<int>(1, Allocator.Persistent);
            var outVals = new NativeArray<int>(2, Allocator.Persistent);
            JobHandle delayHandle = default;

            try
            {
                long payloadBaseline     = MeshDataPayload.DebugLiveAllocCount;
                long graphOutputBaseline = FillGraphOutput.DebugLiveCount;

                delayHandle = new SpinUntilGateJob
                    { Gate = gate, Started = started, Out = outVals, MaxIterations = 2_000_000_000 }.Schedule();
                JobHandle.ScheduleBatchedJobs();
                view.TileManager.GraphDepsForTest = delayHandle;
                // Non-obvious why: no Await is allowed while the gate holds a graph step, so a ThreadPool
                // prologue gets no wall-clock and the bounded loop can exhaust before three land.
                view.TileManager.WorkScheduler = new InlineWorkScheduler();

                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: FillStyle());

                // Gate shut — held tiles pile up in MEASURE instead of racing through it. No Await/Drain
                // while the gate holds a graph step; a plain bounded LateUpdate()-only pump.
                for (int f = 0; f < 3000 && view.CaptureTelemetry().GraphMeasureInFlight < 3; f++)
                    view.LateUpdate();
                Assert.GreaterOrEqual(view.CaptureTelemetry().GraphMeasureInFlight, 3,
                    "drive precondition: at least three tiles must be genuinely held in MEASURE before the " +
                    "gate opens — reachable under both rules (admission is 1/Tick either way, and the " +
                    "prologue hand-off is uncharged before and after), so a RED here is a property failure, " +
                    "never a precondition failure.");

                gate[0] = 1;
                delayHandle.Complete();
                view.AwaitInFlightMeshBuilds(); // completes every held graph — all held tiles measure-complete at once

                view.LateUpdate();

                Assert.GreaterOrEqual(view.MeshDataArraysAllocatedLastKick(), 2,
                    "at least TWO tiles must complete their write transition on this ONE Tick at cap=1 — " +
                    "impossible under the deleted rule, where one budget unit buys one charged write kick " +
                    "per Tick.");

                view.Teardown();
                Assert.AreEqual(payloadBaseline, MeshDataPayload.DebugLiveAllocCount,
                    "teardown of this unsettled cover (write-kicked, consume blocked) must not leak any " +
                    "allocated MeshDataArray.");
                Assert.AreEqual(graphOutputBaseline, FillGraphOutput.DebugLiveCount,
                    "teardown of this unsettled cover must not leak any live measure/write graph output.");
            }
            finally
            {
                gate[0] = 1;
                delayHandle.Complete();
                view.Teardown();
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillExtrusionGraphBuildTests — fill/fill-extrusion/line layers over one polygon-only style
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class FillExtrusionGraphBuildTests : BaseTestFixture
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
        /// No kick-time <c>MeshDataArray</c> for fill, fill-extrusion or line. The measure step builds wall
        /// geometry in <c>FillExtrusionMeshGraph.Schedule</c>
        /// (<see cref="StyledFillExtrusionTileBuilder.WallColumns.DebugTotalCreated"/> advances). The write
        /// step allocates exactly TWO arrays (fill + extrusion); the empty line layer gets no write step.
        /// A TexCoord4 check pins the extrusion writer, which the mesh counts cannot tell from fill's.
        /// </summary>
        [Test]
        public void ExtrusionLayer_OneMeshPerLayer_WriteAllocatesExactlyTwo()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go = Track(new GameObject("FillExtrusionGraphBuild_B"));
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
                    "(job-scheduling-design.md); nothing allocates until a write step runs.");

                // "Kicked" means DISPATCHED: with no scheduler override the prologue runs off-thread, so
                // await it before the next tick schedules the measure graph.
                view.AwaitInFlightMeshBuilds();

                // This tick schedules the measure step, where FillExtrusionMeshGraph.Schedule allocates
                // WallColumns, so the wall-columns witness reads after it.
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
                // Only StyledFillExtrusionTileBuilder's descriptor set declares TexCoord4. A mis-route
                // through fill's writer keeps every count above, so only this assertion moves.
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
                gate.Dispose();
                started.Dispose();
                outVals.Dispose();
            }
        }

        /// <summary>
        /// Every <see cref="StyledFillExtrusionTileBuilder.WallColumns"/> the measure step allocated is
        /// disposed by teardown, alongside the request and graph counters. <c>FillExtrusionLayerBuild</c>
        /// owns the walls and releases them in its <c>Dispose()</c>.
        /// <see cref="StyledFillExtrusionTileBuilder.WallColumns.DebugTotalCreated"/> advancing is the
        /// non-vacuity witness.
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

                // LoadedTileCount() > 0 is load-bearing: before the first LateUpdate() the cover is empty,
                // so AllTilesSettled() is vacuously true and the loop would never pump.
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileBackgroundQuadProjectionTests — globe curvature and synthetic ring encoding
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TileBackgroundQuadProjectionTests : BaseTestFixture
    {
        // Non-obvious why: z3 gives a 45° span, which forces globe subdivision yet fits DefaultMaxDepth (5).
        // This quad IS the full tile, and at z0 its edges hit the depth cap before reaching the 3° threshold.
        private static readonly TileId CoarseTile = new TileId { Z = 3, X = 4, Y = 3 };

        /// <summary>Drives the real graph-arm path — <see cref="BackgroundQuad"/> +
        /// <see cref="TileBuildGraph"/>, as <c>TileManager.KickSourcelessBackground</c> does — and returns
        /// the uploaded mesh (caller destroys it) plus its bake origin. Positions are
        /// ORIGIN-RELATIVE, so a caller reconstructing absolute positions must add the origin back.</summary>
        private static (Mesh mesh, double3 origin) BuildViaProcessor(IProjection projection)
        {
            double3 origin = TileRenderOrigin.Project(CoarseTile, projection);
            var context = new TileLayerProcessContext
            {
                Tile = CoarseTile, Zoom = CoarseTile.Z, TileOriginRender = origin, Projection = projection,
                // Production's default FillTileBufferClip (0.0) is an ENABLED zero-margin clip; bare
                // `default` (Disabled) would drive RingSelectJob instead of production's RingClipJob.
                BufferClip = TileBufferClip.KeepTileUnits(0.0),
            };

            TileGeometryBuffers quad = BackgroundQuad.MintFullExtentGeometry(CoarseTile);
            FillMeshPipeline.LayerInput input = BackgroundQuad.BuildLayerInput(
                in context, in quad, out NativeArray<int> visitOrder, out NativeArray<Vector4> featureColors);
            // BuildLayerInput's out visitOrder is folded into input.RingVisitOrder already — mirrors
            // KickSourcelessBackground's own use of this method.

            var build = FillLayerBuild.Rent(input, featureColors, materialIndex: 0, payloadName: "bg");
            TileBuildGraph graph = TileBuildGraph.ScheduleMeasure(new ILayerMeshBuild[] { build }, quad);
            graph.Complete();
            graph.CompleteMeasureAndScheduleWrite(out _);
            MeshDataPayload[] payloads = graph.CompleteWriteAndTakePayloads();
            Mesh mesh = payloads[0].Upload();
            payloads[0].Dispose(); // no-op after Upload — belt-and-braces, mirrors production consume
            graph.Dispose();
            return (mesh, origin);
        }

        /// <summary>
        /// The background quad built from the processor's corners is the SAME geometry as
        /// <see cref="FullExtentRingCommandStream"/>, a frozen MVT encoding of the full-extent ring. Both
        /// are materialized and compared element-wise. Non-obvious why: a corner-value check cannot see ring
        /// COUNT, ring OFFSETS, the trailing SENTINEL or the kind COLUMN, and an independent oracle cannot be
        /// satisfied by transcribing the implementation.
        /// </summary>
        [Test]
        public void SyntheticRing_MaterializesIdenticallyToTheRetiredCommandStream()
        {
            double extent = BackgroundQuad.Extent;
            Assert.AreEqual(FullExtentRingCommandStream.Extent, extent,
                "precondition: the frozen command stream must use the processor's extent, or the element-wise " +
                "comparison below compares rings at two different scales");

            // The legacy arm: the exact bytes production used to hand-author.
            var legacyFeature = new DictionaryFeature(properties: null, geometryType: TileGeometryType.Polygon, hasId: false, geometry: FullExtentRingCommandStream.Commands);
            TileGeometryBuffers legacy = MvtGeometryMaterializerTestFactory.Materialize(
                CoarseTile, extent,
                new[] { legacyFeature.GeometryType },
                new[] { legacyFeature.Geometry });

            // The live arm: PRODUCTION's corner data and kind column, through production's path producer.
            // A test-owned copy of the corners would compare the fixture with itself.
            TileGeometryBuffers current = new PathGeometryMaterializer(
                CoarseTile, extent,
                BackgroundQuad.FullExtentRingKinds,
                BackgroundQuad.FullExtentRingPaths).Materialize();

            try
            {
                // Non-vacuity: both arms really produced a ring, so an all-default comparison cannot pass.
                Assert.IsTrue(legacy.IsCreated, "precondition: the legacy command stream materialized");
                Assert.IsTrue(current.IsCreated, "precondition: the live background geometry materialized");
                Assert.AreEqual(1, legacy.RingCount, "precondition: the legacy stream is exactly one ring");
                Assert.AreEqual(4, legacy.VertexCount, "precondition: …of exactly four vertices");

                Assert.AreEqual(legacy.RingCount, current.RingCount,
                    "ring COUNT must match — a flatten that emitted two rings, or none, shows only here");
                Assert.AreEqual(legacy.VertexCount, current.VertexCount, "vertex COUNT must match");
                Assert.AreEqual(legacy.FeatureCount, current.FeatureCount, "feature COUNT must match");
                Assert.AreEqual(legacy.Extent, current.Extent, "both must describe the same extent");

                for (int f = 0; f < legacy.FeatureCount; f++)
                    Assert.AreEqual(legacy.FeatureGeometryType[f], current.FeatureGeometryType[f],
                        $"FeatureGeometryType[{f}] — the kind column drives every consumer's ring gate");

                // RingCount + 1 offsets: the trailing SENTINEL is included deliberately. Nothing else in this
                // fixture would notice its absence, and every downstream span read depends on it.
                for (int r = 0; r <= legacy.RingCount; r++)
                    Assert.AreEqual(legacy.RingOffsets[r], current.RingOffsets[r],
                        $"RingOffsets[{r}] (index {legacy.RingCount} is the trailing sentinel)");

                for (int r = 0; r < legacy.RingCount; r++)
                    Assert.AreEqual(legacy.RingFeatureIdx[r], current.RingFeatureIdx[r],
                        $"RingFeatureIdx[{r}] — the join a consumer colours through");

                for (int v = 0; v < legacy.VertexCount; v++)
                {
                    Assert.AreEqual(legacy.Vertices[v].x, current.Vertices[v].x, 1e-12,
                        $"Vertices[{v}].x — corner ORDER is load-bearing, not just membership");
                    Assert.AreEqual(legacy.Vertices[v].y, current.Vertices[v].y, 1e-12,
                        $"Vertices[{v}].y");
                }
            }
            finally
            {
                legacy.Dispose();
                current.Dispose();
            }
        }


        [Test]
        public void BackgroundQuad_FlatOnMercator_NoSubdivision()
        {
            var (mesh, origin) = BuildViaProcessor(new WebMercatorProjection());
            Track(mesh);
            {
                Assert.IsNotNull(mesh, "Mercator background must produce geometry.");
                var verts = new List<Vector3>();
                mesh.GetVertices(verts);

                Assert.AreEqual(4, verts.Count,
                    "Mercator (MaxRefineAngleRad == +∞) must write the flat 4-corner quad — no subdivision.");
                foreach (var v in verts)
                    Assert.AreEqual(0f, v.y, 1e-3f, "Mercator stored positions must be coplanar y≈0 (origin-relative).");

                // Every background vertex is opaque white, the value a constant "#ffffff" fill-color gives.
                // Nothing else watches it, so a wrong literal or stray alpha would tint the background silently.
                var colors = new List<Color>();
                mesh.GetColors(colors);
                Assert.AreEqual(verts.Count, colors.Count,
                    "every background vertex must carry a colour — the fill stream is not optional here");
                foreach (Color c in colors)
                    Assert.AreEqual(new Color(1f, 1f, 1f, 1f), c,
                        "background vertex colour must be exactly opaque white (linear == sRGB for white); " +
                        "the material uniform supplies the actual background colour.");

                AssertBoundsMatchesVertexEnvelope(mesh, verts);
                // RTC contract: origin-relative positions stay within about the TILE's own extent. A z3
                // tile is physically large, so the bound scales with the tile, not a small constant.
                double tileSizeWorld = WebMercator.WorldExtent * 2.0 / math.pow(2.0, CoarseTile.Z);
                foreach (var v in verts)
                    Assert.Less(((Vector3)v).magnitude, (float)(tileSizeWorld * 2.0),
                        "Mercator stored positions must be origin-relative (bounded to ~the tile's own extent), " +
                        "not world/circumference-scale (a missing origin subtraction would show ~40,075,017 m).");
            }
        }

        /// <summary>Reads the uploaded INDEX buffer to prove subdivision TOPOLOGY: (a) every vertex is
        /// triangle-referenced, (b) all 4 projected tile corners are present (catches a wrong TileId), and
        /// (c) every triangle edge subtends ≤ the angular threshold on the sphere (catches an unsplit quad).
        /// Also checks the <see cref="Mesh.bounds"/> envelope.</summary>
        [Test]
        public void BackgroundQuad_CurvesOnSphere_SubdividedTopology()
        {
            var projection = new SphericalProjection();
            var (mesh, origin) = BuildViaProcessor(projection);
            Track(mesh);
            {
                Assert.IsNotNull(mesh, "globe background must produce geometry.");
                var verts = new List<Vector3>();
                mesh.GetVertices(verts);
                var tris = new List<int>();
                mesh.GetTriangles(tris, 0);

                Assert.Greater(verts.Count, 4,
                    "the globe patch must subdivide beyond the flat 4-corner quad (GlobeFillSubdivideJob emits " +
                    "fresh verts per source triangle).");
                Assert.Greater(tris.Count, 0, "the uploaded mesh must carry a non-empty index buffer.");
                Assert.AreEqual(0, tris.Count % 3, "index buffer must be a whole number of triangles.");

                // (a) every emitted vertex is referenced by SOME triangle — defeats "append midpoints
                // without rewiring indices" (dangling verts that inflate the count but aren't rendered).
                var referenced = new bool[verts.Count];
                foreach (int idx in tris) referenced[idx] = true;
                Assert.IsTrue(referenced.All(r => r),
                    "every uploaded vertex must be referenced by the index buffer — a dangling (unreferenced) " +
                    "vertex means it was appended without rewiring triangle indices.");

                // Absolute (world/ECEF) positions: stored positions are ORIGIN-RELATIVE, so
                // reconstruct absolute BEFORE any sphere-space (radius/angle) test.
                var absolute = verts.Select(v => new double3(v.x, v.y, v.z) + origin).ToArray();
                foreach (var a in absolute)
                    Assert.That(math.length(a), Is.EqualTo(SphericalProjection.Radius).Within(SphericalProjection.Radius * 1e-6),
                        "every absolute vertex must lie ON the sphere of SphericalProjection.Radius.");

                // (b) all 4 projected tile corners for CoarseTile are present among the vertices.
                double2[] cornersTileLocal =
                {
                    new double2(0, 0), new double2(BackgroundQuad.Extent, 0),
                    new double2(BackgroundQuad.Extent, BackgroundQuad.Extent),
                    new double2(0, BackgroundQuad.Extent),
                };
                foreach (double2 tc in cornersTileLocal)
                {
                    double2 lonLat = CoarseTile.ToLonLat(tc.x, tc.y, BackgroundQuad.Extent);
                    double3 expectedAbs = projection.Project(new GeoCoordinate { Latitude = lonLat.y, Longitude = lonLat.x });
                    bool found = absolute.Any(a => math.distance(a, expectedAbs) < SphericalProjection.Radius * 1e-4);
                    Assert.IsTrue(found, $"projected tile corner {tc} (lon/lat {lonLat}) must be present among the " +
                                          "uploaded vertices — a wrong TileId would project different corners.");
                }

                // (c) every triangle edge subtends ≤ the angular threshold. A vertex count > 4 does not
                // prove split FACES; an unsplit quad's chord edges exceed this.
                double maxAllowedRad = GlobeFillSubdivideDispatch.DefaultMaxEdgeAngleRad * 1.10; // 10% float slack
                int edgesChecked = 0;
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    AssertEdgeWithinThreshold(absolute[i0], absolute[i1], maxAllowedRad, ref edgesChecked);
                    AssertEdgeWithinThreshold(absolute[i1], absolute[i2], maxAllowedRad, ref edgesChecked);
                    AssertEdgeWithinThreshold(absolute[i2], absolute[i0], maxAllowedRad, ref edgesChecked);
                }
                Assert.Greater(edgesChecked, 0, "must have checked at least one triangle edge.");

                AssertBoundsMatchesVertexEnvelope(mesh, verts);
            }
        }

        private static void AssertEdgeWithinThreshold(double3 a, double3 b, double maxAllowedRad, ref int edgesChecked)
        {
            double cosAngle = math.dot(a, b) / (math.length(a) * math.length(b));
            cosAngle = math.clamp(cosAngle, -1.0, 1.0);
            double angle = math.acos(cosAngle);
            Assert.LessOrEqual(angle, maxAllowedRad,
                "every indexed triangle edge must subtend <= the subdivision angular threshold on the sphere " +
                "— the faces must hug the sphere, not chord straight through it.");
            edgesChecked++;
        }

        /// <summary>Both projections: the uploaded <see cref="Mesh.bounds"/> equals the origin-relative
        /// vertex envelope and encapsulates every stored vertex.</summary>
        private static void AssertBoundsMatchesVertexEnvelope(Mesh mesh, List<Vector3> verts)
        {
            Vector3 min = verts[0], max = verts[0];
            foreach (var v in verts) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            Vector3 expectedCenter = (min + max) * 0.5f;
            Vector3 expectedSize   = max - min;

            Assert.That(mesh.bounds.center.x, Is.EqualTo(expectedCenter.x).Within(1e-2f));
            Assert.That(mesh.bounds.center.y, Is.EqualTo(expectedCenter.y).Within(1e-2f));
            Assert.That(mesh.bounds.center.z, Is.EqualTo(expectedCenter.z).Within(1e-2f));
            Assert.That(mesh.bounds.size.x, Is.EqualTo(expectedSize.x).Within(1e-2f));
            Assert.That(mesh.bounds.size.y, Is.EqualTo(expectedSize.y).Within(1e-2f));
            Assert.That(mesh.bounds.size.z, Is.EqualTo(expectedSize.z).Within(1e-2f));

            Bounds expanded = mesh.bounds;
            expanded.Expand(1e-2f);
            foreach (var v in verts)
                Assert.IsTrue(expanded.Contains(v), $"mesh.bounds must encapsulate every stored vertex ({v}).");
        }

        /// <summary>
        /// The kind column and the path list are joined BY POSITION, so a length mismatch would mis-classify
        /// every ring rather than fail. <c>PathGeometryMaterializer</c> validates that <b>before</b> it
        /// allocates: after <c>Allocate</c>, four <c>Allocator.Persistent</c> arrays exist and a throw would
        /// strand them. Production always passes matched lists, so only this direct test reaches the path.
        /// </summary>
        [Test]
        public void PathMaterializer_KindColumnShorterThanPaths_ThrowsBeforeAllocating()
        {
            var paths = new List<IReadOnlyList<IReadOnlyList<double2>>>
            {
                new[] { new[] { new double2(0, 0), new double2(1, 0), new double2(1, 1) } },
                new[] { new[] { new double2(2, 2), new double2(3, 2), new double2(3, 3) } },
            };
            // One kind for two features — the desync.
            var kinds = new List<TileGeometryType> { TileGeometryType.Polygon };

            // Non-vacuity: the matched pair really does materialize, so the throw below is attributable to the
            // mismatch and not to some other defect in the fixture.
            var matchedKinds = new List<TileGeometryType>
                { TileGeometryType.Polygon, TileGeometryType.Polygon };
            TileGeometryBuffers ok = new PathGeometryMaterializer(
                CoarseTile, 4096.0, matchedKinds, paths).Materialize();
            try
            {
                Assert.IsTrue(ok.IsCreated, "precondition: matched kinds/paths must materialize");
                Assert.AreEqual(2, ok.RingCount, "precondition: both features' rings are present");
            }
            finally { ok.Dispose(); }

            // Fully qualified: `using System;` would make `Object` ambiguous with UnityEngine.Object at this
            // file's existing call sites.
            var ex = Assert.Throws<System.ArgumentException>(
                () => new PathGeometryMaterializer(CoarseTile, 4096.0, kinds, paths).Materialize(),
                "a kind column shorter than the path list must be rejected, not silently mis-joined");
            Assert.That(ex.Message, Does.Contain("one entry per feature"),
                "the message must name the contract that was violated, so a wiring error is diagnosable");
        }
    }
}
