// Teardown-cancel: zero-leak teardown on Stop while mesh builds are in-flight (see the disposal &
// cancellation contract, docs/async-architecture.md). Drives load, kicks mesh builds, and tears down WITHOUT
// settling first — the exact race the maintainer reproduced (Stop while tiles are still loading).
//
// This is EditMode, and deliberately so: the un-fixed stall must be measured by the TEST thread, which
// requires Teardown() to be a direct synchronous call. EditMode's model does exactly that (OnDestroy does
// not fire under DestroyImmediate headlessly, so tests call view.Teardown() explicitly, same as
// DisposalLeakGuardTests). In PlayMode a 10s block would freeze the PlayerLoop and a yield-pump could not
// observe it.
//
// Coverage gap (recorded, not closed here): the EditMode drain is symbol-silent, so the symbol-side leak
// vector (SymbolTileBlock) is not exercised by this file — the maintainer's in-Editor Stop check
// covers it.

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
    /// Teardown-cancel acceptance teeth: Tooth B (constraint 2 + the actual stall symptom — teardown must
    /// return promptly even while a build is genuinely parked in-flight), Tooth C (regression guard — zero
    /// <see cref="VerifiedDisposable"/> finalizer leaks; NOT RED-verifiable headlessly, since
    /// <c>Teardown()</c> always runs to completion in this harness — see its own doc). Tooth A (constraint
    /// 1 — a canceled build must not leak its kick-allocated native payload) is RETIRED as of
    /// job-scheduling-design.md §8 stage 5 — see the comment where it used to live, just below.
    /// </summary>
    [TestFixture]
    public class TeardownCancelInflightBuildsTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

        // The LINE layer here is now VESTIGIAL for this file's purposes (job-scheduling-design.md §8 stage
        // 5 moved it onto the graph arm too, retiring the only tooth — A, below — that needed it on the
        // seam arm) but is kept: Tooth B/C still exercise a two-layer style, matching every other cover
        // fixture in this test family, and removing it would be an unrelated fixture change with no tooth
        // asking for it.
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

        // ── Tooth A: RETIRED (job-scheduling-design.md §8 stage 5) ──────────────────────────────────

        // CanceledBuild_DisposesKickAllocatedPayload_CounterToBaseline retired here. Its own doc (kept in
        // history) already named this exact fork: "This claim expires when line moves onto the graph too
        // (stage 5) — at that point this tooth needs a new seam-arm kind, or retirement." There is no other
        // seam-arm kind left — line was the last one (PassThrough/Prebuilt itself retires with it) — so
        // "a new seam-arm kind" is not an available option; this is the sanctioned retirement.
        //
        // Its precondition ("the parked build's kick-allocated MeshDataArray sits undisposed right before
        // teardown") is now PERMANENTLY vacuous: every layer kind is graph-arm, and a graph-arm layer
        // allocates NO MeshDataArray at kick at all (tooth (f) in the line-graph plan observes this
        // directly).
        //
        // CORRECTED (review): the real, non-vacuous round-trip for "a MeshDataPayload gets allocated then
        // freed" is SourceTileGraphBuildTests.cs:533-568 (ReleasedCompleteButUnconsumed_Write_StashesInThePen_
        // ThenDrains — `Assert.Greater(MeshDataPayload.DebugLiveAllocCount, payloadBaseline, "non-vacuous
        // precondition: the completed write must hold a real allocated payload.")` at :546, `AreEqual` back
        // to baseline at :568) — NOT Teardown_WhileGenuinelyInMeasure_DrainsThePen_WithoutSettling (:592),
        // which was cited here first: that test's own payload assertion message says "there is none here —
        // the write step never ran", i.e. baseline==baseline with nothing allocated in between — a real tooth
        // for TileBuildGraph.DebugLiveCount (graph INSTANCES; that claim it still legitimately covers), but
        // vacuous for MeshDataArrays specifically — the counter this retired tooth existed to pin.
        //
        // The honest gap, stated rather than papered over: teardown while a Mesh.MeshDataArray is genuinely
        // LIVE has NO observing tooth anywhere in this repo. :533-568's round-trip is an EVICTION path (a
        // camera pan past the cover), not TEARDOWN. Post-stage-5 that state is only reachable during the
        // WRITE step, and job-scheduling-design.md's stage 2 recorded finding #2 already flagged the
        // write-in-flight release path as unobserved and deferred it — this retirement does not create that
        // gap (the retired tooth parked before any work and never covered it either), it just stops implying
        // the gap is closed.
        //
        // The mechanism that actually explains why retiring this ONE tooth cleared two seemingly-unrelated
        // failures elsewhere (a SharedDisposable<IDecodedTile> finalizer leak, and an unrelated fill-only
        // test's TileBuildGraph.DebugLiveCount reading elevated) is separate from the vacuous-citation issue
        // above: this tooth's OWN `finally` released its gate (`gate.Set()`) and disposed it AFTER the try
        // block's precondition assertion had already failed — meaning the lifetime token was still LIVE
        // (uncancelled) at that point, so the newly-unblocked worker went on to do a REAL mesh-build kick,
        // UNAWAITED, after this test method had already returned. That kick moved process-wide static
        // counters (MeshDataPayload.DebugLiveAllocCount / TileBuildGraph.DebugLiveCount) during whatever test
        // ran next — this file's ~40 baseline-then-delta assertions across the suite are exactly what that
        // corrupts. The `gate.Set(); gate.Dispose();`-with-no-wait SHAPE survives in Tooth B below (see its
        // own fix) — it is a real, independent hazard, but it is NOT what explained these two failures, since
        // by the time Tooth B's finally releases its gate, Teardown() (which cancels the token FIRST) has
        // already run.

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

        // ── Tooth B: prompt teardown while a build is genuinely parked in-flight (constraint 2) ────

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
            // Real ThreadPool dispatch (TileManager's own production default) wrapped ONLY to observe when
            // the released worker's body actually returns — see the class's own doc for why this, not a
            // timing inference, is what closes the un-awaited-worker hazard a retired sibling tooth exposed.
            var scheduler = new BodyCompletionWorkScheduler(new ThreadPoolWorkScheduler());

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);
                view.TileManager.WorkScheduler = scheduler;

                // Set the gate BEFORE any kick: every mesh-build worker parks on it (jointly with the
                // lifetime token) as its very first statement, so the first tile kicked below is held
                // genuinely in-flight rather than racing to completion before Teardown() runs.
                view.TileManager.MeshBuildGateForTest = gate;

                // Kick-pump WITHOUT AwaitInFlightMeshBuilds: with the gate held, a kicked build's task never
                // completes, so awaiting it would park this thread for the full 10s timeout and then throw
                // TimeoutException. Fetch completion (which drives whether a tile becomes kick-eligible) is
                // independent of the mesh-build gate, so plain repeated LateUpdate ticks are enough to
                // observe at least one kick.
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

                // The un-fixed drain blocks WaitOffPlayerLoop(10000) per stashed task; the fix must cancel
                // the parked build before either pen drain waits on it, so teardown completes in well under
                // a second. A generous budget (2s) keeps this robust on a loaded CI box while still failing
                // hard against a 10s (or even a several-second) stall.
                Assert.Less(sw.ElapsedMilliseconds, 2000,
                    $"Teardown-cancel constraint 2: Teardown() took {sw.ElapsedMilliseconds}ms while a mesh " +
                    "build was genuinely parked in-flight. The lifetime token must be cancelled BEFORE either " +
                    "pen drain waits on the stashed task — cancelling after (or not at all) reproduces the " +
                    "~10s-per-stashed-task stall this stage exists to close.");
            }
            finally
            {
                // Teardown() FIRST (only reached here if the try block threw before its own happy-path
                // Teardown() call already ran) — it cancels the lifetime token, so a worker still parked on
                // the gate wakes into an ALREADY-cancelled token and aborts without doing real work, rather
                // than waking to a live token and running a genuine mesh-build kick UNAWAITED after this
                // method returns — the exact hazard that made a retired sibling tooth in this file corrupt
                // two unrelated tests' process-wide counters (see that tooth's own retirement note, above).
                if (go != null)
                {
                    view.Teardown();
                    UnityEngine.Object.DestroyImmediate(go);
                }
                // Release the gate (and any worker still parked on it) unconditionally so a RED failure
                // never hangs the test runner. NOT disposed: a worker that just woke may still reference it
                // briefly, and disposing a ManualResetEventSlim a concurrent thread might still touch is
                // its own hazard — let GC reclaim it instead of racing a Dispose() against that reference.
                gate.Set();

                // POSITIVE confirmation, not an inference: block (bounded) until the released worker's body
                // has actually returned, so this method cannot return while a background thread might still
                // be touching process-wide counters (MeshDataPayload.DebugLiveAllocCount et al.) that the
                // NEXT test's own baseline-then-delta assertions read. A healthy worker signals in
                // milliseconds (cancellation makes it an early no-op) — 5s is generous headroom, not a
                // measured figure; a timeout here means a worker genuinely hung, which is itself a defect
                // worth surfacing rather than silently ignoring.
                if (!scheduler.BodyDone.Wait(TimeSpan.FromSeconds(5)))
                    Assert.Fail("the released worker's body never signalled completion within 5s — a hang, " +
                        "not the prompt-cancellation this tooth exists to prove.");
                scheduler.BodyDone.Dispose();
            }
        }

        // ── Tooth C: zero VerifiedDisposable leaks (regression guard, NOT RED-verifiable headlessly) ──

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
