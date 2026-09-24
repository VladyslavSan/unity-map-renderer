// Zero-leak teardown on Stop while mesh builds are in flight (docs/async-architecture.md, disposal and
// cancellation contract): it kicks builds and tears down WITHOUT settling first.
// Non-obvious why: this is EditMode because the TEST thread must time a direct synchronous Teardown(); in
// PlayMode a stall freezes the PlayerLoop.
// Limitation: the EditMode drain is symbol-silent, so SymbolTileBlock is not covered.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.Lifetime
{
    /// <summary>
    /// Teardown returns promptly while a build is parked in flight
    /// (<see cref="Teardown_WhileBuildParked_ReturnsPromptly"/>), and leaves zero
    /// <see cref="VerifiedDisposable"/> finalizer leaks (<see cref="Teardown_MidFlight_NoVerifiedDisposableLeaks"/>,
    /// a regression guard that is not RED-verifiable headlessly). The "Gap" comment below names the teardown
    /// state no test observes.
    /// </summary>
    [TestFixture]
    public class TeardownCancelInflightBuildsTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        // The LINE layer is vestigial for this file's purposes, but is kept: both tests still exercise a
        // two-layer style, matching every other cover fixture in this test family.
        private static StyleDocument MinimalStyle() => StyleParser.Parse(@"{
            ""version"": 8,
            ""name"": ""TeardownCancel"",
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
                },
                {
                    ""id"": ""countries-line"",
                    ""type"": ""line"",
                    ""source"": ""maplibre"",
                    ""source-layer"": ""countries""
                }
            ]
        }");

        // ── Gap: teardown with a LIVE Mesh.MeshDataArray is unobserved ──────────────────────────────

        // Limitation: no test observes teardown while a Mesh.MeshDataArray is LIVE; that state exists only in
        // the WRITE step, and SourceTileGraphBuildTests covers the allocate-then-free round trip on eviction.
        // Hazard: releasing the gate with no join while the token is live lets a worker run a REAL kick after
        // the test returns and move the static counters the next test reads; Teardown() must cancel first.

        /// <summary>Wraps a real <see cref="IWorkScheduler"/> and signals <see cref="BodyDone"/> AFTER the
        /// dispatched body returns (success, fault, or early cancellation-exit alike — the <c>finally</c>
        /// fires regardless). Lets a test POSITIVELY confirm a released worker has actually finished running
        /// before the test method itself returns, rather than inferring it from elapsed time or from the
        /// lifetime token having been cancelled first — "probably already done" is not a join.</summary>
        private sealed class BodyCompletionWorkScheduler : IWorkScheduler
        {
            private readonly IWorkScheduler _inner;
            public readonly ManualResetEventSlim BodyDone = new(false);
            public BodyCompletionWorkScheduler(IWorkScheduler inner) => _inner = inner;
            public bool RunsInline => _inner.RunsInline;
            public WorkHandle<T> Schedule<T>(Func<CancellationToken, T> body, CancellationToken ct = default)
                => _inner.Schedule(c => { try { return body(c); } finally { BodyDone.Set(); } }, ct);
        }

        // ── Prompt teardown while a build is genuinely parked in-flight (constraint 2) ────

        /// <summary>
        /// Holds a kicked build genuinely in-flight via <see cref="TileManager.MeshBuildGateForTest"/> (set
        /// BEFORE any kick, so the worker parks jointly on the gate and the lifetime token before doing any
        /// work), then times <c>Teardown()</c>. Asserts elapsed time is far below the 10s
        /// <c>WaitOffPlayerLoop</c> budget the un-fixed drain would block for — this is the only
        /// headless-observable symptom of the actual bug (Stop stalling/leaking while tiles load).
        /// </summary>
        [Test]
        public void Teardown_WhileBuildParked_ReturnsPromptly()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_TeardownCancel_B");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 0;
            view.Config.MaxMeshBuildsPerTick = 64;

            ManualResetEventSlim gate = new ManualResetEventSlim(false);
            // The production ThreadPool scheduler, wrapped only to observe when the released worker's body
            // returns (see the class doc).
            var scheduler = new BodyCompletionWorkScheduler(new ThreadPoolWorkScheduler());

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);
                view.TileManager.WorkScheduler = scheduler;

                // Set the gate BEFORE any kick: every worker parks on it first, so the first kicked tile stays
                // in flight until Teardown() runs.
                view.TileManager.MeshBuildGateForTest = gate;

                // Pump WITHOUT AwaitInFlightMeshBuilds: a gated build never completes, so awaiting it times out.
                // Fetches do not wait on the gate, so plain LateUpdate ticks reach a kick.
                int kicked = 0;
                for (int f = 0; f < 3000 && kicked == 0; f++)
                {
                    view.LateUpdate();
                    kicked += view.TileBuildsStartedLastTick();
                }
                Assert.Greater(kicked, 0,
                    "drive precondition: at least one mesh build must have been kicked (and therefore parked " +
                    "on the gate) before teardown, or this test never held anything genuinely in-flight.");

                var sw = Stopwatch.StartNew();
                view.Teardown();
                sw.Stop();
                UnityEngine.Object.DestroyImmediate(go);
                go = null;

                // Teardown must cancel the parked build before a pen drain waits WaitOffPlayerLoop(10000) on it.
                // The 2s budget tolerates a loaded machine and still fails a multi-second stall.
                Assert.Less(sw.ElapsedMilliseconds, 2000,
                    $"Teardown-cancel constraint 2: Teardown() took {sw.ElapsedMilliseconds}ms while a mesh " +
                    "build was genuinely parked in-flight. The lifetime token must be cancelled BEFORE either " +
                    "pen drain waits on the stashed task — cancelling after (or not at all) reproduces the " +
                    "~10s-per-stashed-task stall this stage exists to close.");
            }
            finally
            {
                // Teardown() FIRST (if the try threw early): a worker parked on the gate then wakes to a cancelled
                // token and aborts, instead of running an unawaited kick (the hazard noted above).
                if (go != null)
                {
                    view.Teardown();
                    UnityEngine.Object.DestroyImmediate(go);
                }
                // Release the gate unconditionally so a RED never hangs the runner. It is NOT disposed: a worker
                // that just woke may still touch it, so GC reclaims it.
                gate.Set();

                // Wait (bounded) until the released worker's body returns, so no thread still moves the static
                // counters the NEXT test reads. 5s is headroom; a timeout means a worker hung, which fails.
                if (!scheduler.BodyDone.Wait(TimeSpan.FromSeconds(5)))
                    Assert.Fail("the released worker's body never signalled completion within 5s — a hang, " +
                        "not the prompt-cancellation this tooth exists to prove.");
                scheduler.BodyDone.Dispose();
            }
        }

        // ── Zero VerifiedDisposable leaks (regression guard, NOT RED-verifiable headlessly) ──

        /// <summary>
        /// Regression guard, not a RED-verifiable tooth: the production leak's proximate cause is Unity
        /// aborting <c>OnDestroy</c> mid-block, which no headless test reproduces — <c>Teardown()</c> always
        /// runs to completion here, so every disposable's <c>Dispose()</c> always executes and this is 0
        /// whether or not the fix is present. Kept as a cheap net against a FUTURE disposal-order
        /// regression (e.g. a disposable created after the point <c>DoDispose</c> stops disposing things).
        /// </summary>
        [Test]
        public void Teardown_MidFlight_NoVerifiedDisposableLeaks()
        {
            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_TeardownCancel_C");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            view.Config.MaxConsumesPerTick = 0;
            view.Config.MaxMeshBuildsPerTick = 64;

            var captured = new List<string>();
            System.Action<string> original = VerifiedDisposable.LeakReporter;
            VerifiedDisposable.LeakReporter = msg => captured.Add(msg);

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);

                // TileBuildsStartedLastTick counts tiles once, at their first kick, and is the one this drive means.
                int started = 0;
                for (int f = 0; f < 3000; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    started += view.TileBuildsStartedLastTick();
                    if (view.LoadedTileCount() > 0 && started >= view.LoadedTileCount()) break;
                }
                Assert.GreaterOrEqual(started, view.LoadedTileCount(),
                    "drive precondition: every cover tile's mesh build must have been started before teardown.");

                view.Teardown();
                UnityEngine.Object.DestroyImmediate(go);
                go = null;

                System.GC.Collect();
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect();

                Assert.IsEmpty(captured,
                    "Regression guard: zero VerifiedDisposable finalizer leaks expected after a mid-flight " +
                    $"Teardown(). Captured: [{string.Join("; ", captured)}]. This test does NOT reproduce the " +
                    "production Unity-aborts-OnDestroy-mid-block leak (Teardown() always runs to completion " +
                    "headlessly) — it only guards against a future disposal-order regression.");
            }
            finally
            {
                VerifiedDisposable.LeakReporter = original;
                if (go != null)
                {
                    view.Teardown();
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
        }
    }
}
