// Stage-2 acceptance: TileManager's two mesh-build kicks (KickMeshBuild, KickSourcelessBackground) dispatch
// through the injected IWorkScheduler policy — the actual WebGL blank-map fix (docs/web-target.md).
// T1/T2 drive the real MapView cover→build→consume loop with an InlineWorkScheduler injected via the
// TileManager.WorkScheduler test seam, and observe BOTH ends: that Schedule was reached (placement) and
// where the body ran (policy) — a test that only checks one of those cannot tell "converted" from "present
// but unused" apart. T5 pins the MeshBuildGateForTest/WorkScheduler mutual-exclusion guard (§3 of the
// migration plan) that keeps a misconfigured Inline+gate combination from ever reaching a hang. T6 (added
// with the SymbolSubsystem WorkScheduler stage) closes a gap in T1/T2/T5: all three always drive a style
// with no symbol layers, so `symbolPass` inside KickMeshBuild's body is always null and
// `symbolPass?.RunWorkerAndHandoff(decode)` never actually executes — the POPULATED path was untested
// end to end under Inline.
//
// RecordingWorkScheduler lives in TestSupport/ (shared with SymbolSubsystemWorkSchedulerTests) rather than
// as a private nested class here, so the two suites share one spy instead of drifting into two subtly
// different copies.

using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;

namespace MapRenderer.Tests.Tiles
{
    [TestFixture]
    public class MeshBuildWorkSchedulerTests
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
        }

        private sealed class SpySymbolTileWorkerPass : ISymbolTileWorkerPass
        {
            // job-scheduling-design.md §8 stage 3 tooth (e): a bool can't tell one run from two — the exact
            // defect this tooth exists to catch (the symbol pass moved earlier relative to the Burst chain,
            // so the risk is now running it TWICE per tile, not zero times). A count can.
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

        // ── T1: the byte-fetching kick (KickMeshBuild) ────────────────────────────────────────────────

        /// <summary>The headline tooth: under an injected <see cref="InlineWorkScheduler"/>,
        /// <c>KickMeshBuild</c> runs its body on the CALLING thread with zero dispatch — the WebGL-correct
        /// behaviour, impossible under <see cref="ThreadPoolWorkScheduler"/>.
        ///
        /// <para><b>RED injection:</b> revert <c>KickMeshBuild</c>'s dispatch to
        /// <c>UniTask.RunOnThreadPool(…, configureAwait:false).Preserve()</c> — the kick never reaches the
        /// injected scheduler, so <c>ScheduleCount</c> stays 0 and clause 1 fails.</para></summary>
        [Test]
        public void InjectedScheduler_RunsTheMeshBuildKick_OnTheCallingThread()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MeshBuildWorkScheduler_T1");
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

                // The fetch is still real UniTask I/O and needs its wall-clock; under Inline the build
                // handles are already terminal by the time AwaitInFlightMeshBuilds reaches them, so that
                // park is a no-op short-circuit. Checked AFTER the pump (not as a loop guard): before the
                // first LateUpdate the cover hasn't been requested yet, so LoadedTileCount() == 0 and
                // AllTilesSettled() is vacuously true — a guard-first loop would never run the pump at all.
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
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }

        // ── T2: the source-less kick (KickSourcelessBackground) ───────────────────────────────────────

        // job-scheduling-design.md §8 stage 2 / E1 (resolved by reordering, option C): the background kick
        // now schedules the FillMeshGraph directly and reaches no IWorkScheduler.Schedule<T> call on EITHER
        // projection — the graph serves both arms. A tooth that only drove Mercator would leave the shipped
        // globe scene's client untested, which is the hole option C was chosen to avoid.
        private static readonly IProjection[] BackgroundProjectionCases = { null, new SphericalProjection() };

        /// <summary>The sourceless-background sibling of T1 — retires
        /// <c>InjectedScheduler_RunsTheSourcelessBackgroundKick_OnTheCallingThread</c> (which asserted
        /// <c>spy.ScheduleCount &gt;= 1</c>), because leaving the seam is exactly what this stage does at
        /// this site. Asserts BOTH ends — the suite's own rule: <c>ScheduleCount == 0</c> alone cannot tell
        /// "absent" from "never kicked", so the settle + registered-mesh check must accompany it.
        ///
        /// <para><b>RED injection:</b> restore the seam call in <c>KickSourcelessBackground</c> (schedule the
        /// graph inside <c>WorkScheduler.Schedule</c>) — <c>ScheduleCount</c> becomes 1.</para></summary>
        [Test]
        public void SourcelessBackgroundKick_ReachesNoWorkScheduler_AndStillRegistersItsMesh(
            [ValueSource(nameof(BackgroundProjectionCases))] IProjection projection)
        {
            var go   = new GameObject("MeshBuildWorkScheduler_T2");
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
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }

        // ── T5: the MeshBuildGateForTest / WorkScheduler mutual-exclusion guard ───────────────────────

        /// <summary>§3 of the migration plan: arming <see cref="TileManager.MeshBuildGateForTest"/> while
        /// <see cref="TileManager.WorkScheduler"/> is an <see cref="InlineWorkScheduler"/> (or the symmetric
        /// order) must throw at the SETTER, before any tile work exists — under Inline the kick body (and
        /// its gate park) runs on the calling/main thread, and the only release
        /// (<c>_lifetimeCts.Cancel()</c> in teardown) is itself main-thread work that could never run, so an
        /// un-guarded combination is a guaranteed hang inside the batch gate.
        ///
        /// <para>No pump runs here at all, and the gate is constructed <c>initialState: true</c> — belt and
        /// braces, so even a mis-scoped RED injection cannot park the main thread (the plan's mandatory
        /// RED-verify safety note).</para>
        ///
        /// <para><b>RED injection:</b> remove either guard clause from the
        /// <see cref="TileManager.WorkScheduler"/>/<see cref="TileManager.MeshBuildGateForTest"/> setters —
        /// the corresponding <c>Assert.Throws</c> fails.</para></summary>
        [Test]
        public void MeshBuildGateForTest_AndTheInlineScheduler_AreMutuallyExclusive()
        {
            var go   = new GameObject("MeshBuildWorkScheduler_T5");
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

                // R1 review finding: the guard must read IWorkScheduler.RunsInline, not `is
                // InlineWorkScheduler` — a decorator (RecordingWorkScheduler, T1/T2's own spy) wrapping an
                // Inline scheduler is just as deadlock-prone, and a concrete-type check would silently miss
                // it. The gate is still armed from the clause above.
                Assert.Throws<InvalidOperationException>(
                    () => tm.WorkScheduler = new RecordingWorkScheduler(new InlineWorkScheduler()),
                    "selecting a WRAPPED Inline scheduler (RunsInline == true via forwarding) while the gate " +
                    "is armed must throw at the setter just as the bare InlineWorkScheduler does — a " +
                    "concrete-type check on the setter would miss this.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }

        // ── T6: a POPULATED symbol pass under Inline — T1/T2/T5 only ever drive symbolPass == null ────

        /// <summary>The missing integration tooth: T1, T2 and T5 above all drive a style/view with no
        /// <c>TileManager.SymbolWorkerFactory</c> set, so <c>symbolPass</c> inside <c>KickMeshBuild</c>'s
        /// body is always <c>null</c> and <c>symbolPass?.RunWorkerAndHandoff(decode)</c> never actually
        /// executes anything — the POPULATED
        /// path was untested end to end under Inline. This wires a spy factory so <c>TryBeginBuild</c>
        /// returns a real (non-null) pass and asserts its <c>RunWorkerAndHandoff</c> actually ran, on the
        /// CALLING thread, inside the SAME Inline-dispatched kick as the mesh pass.
        ///
        /// <para><b>RED injection:</b> revert <c>KickMeshBuild</c>'s dispatch to
        /// <c>UniTask.RunOnThreadPool(…).Preserve()</c> — the symbol pass never reaches the injected
        /// scheduler at all (dead on WebGL), so <c>Ran</c> stays false.</para></summary>
        [Test]
        public void InjectedScheduler_RunsAPopulatedSymbolPass_OnTheCallingThread()
        {
            var src  = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go   = new GameObject("MeshBuildWorkScheduler_T6");
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
                    // job-scheduling-design.md §8 stage 3 tooth (e): the symbol pass moved earlier relative
                    // to the Burst chain — it must still run EXACTLY ONCE per issued pass, not merely "at
                    // least once" (§5(e)'s RED: a second invocation from the prologue-complete arm).
                    Assert.AreEqual(1, pass.RunCount,
                        "a populated symbol pass's RunWorkerAndHandoff must run EXACTLY ONCE — it rides " +
                        "inside KickMeshBuild's one-shot worker pass, and a second call anywhere on the mesh " +
                        "build path would double-run it.");
                }
                Assert.Greater(ranCount, 0,
                    "at least one issued pass must actually have RUN — a tooth over zero ran passes would " +
                    "vacuously pass the per-thread-id check above.");
            }
            finally { view.Teardown(); UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
