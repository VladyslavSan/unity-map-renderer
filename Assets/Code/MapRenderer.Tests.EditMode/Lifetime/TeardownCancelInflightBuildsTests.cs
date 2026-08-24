// Teardown-cancel: zero-leak teardown on Stop while mesh builds are in-flight (design SSOT:
// teardown-cancel-inflight-builds-design.md). Drives load, kicks mesh builds, and tears down WITHOUT
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

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapView = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.Lifetime
{
    /// <summary>
    /// Teardown-cancel acceptance teeth: Tooth A (constraint 1 — a canceled build must not leak its
    /// kick-allocated native payload), Tooth B (constraint 2 + the actual stall symptom — teardown must
    /// return promptly even while a build is genuinely parked in-flight), Tooth C (regression guard — zero
    /// <see cref="VerifiedDisposable"/> finalizer leaks; NOT RED-verifiable headlessly, since
    /// <c>Teardown()</c> always runs to completion in this harness — see its own doc).
    /// </summary>
    [TestFixture]
    public class TeardownCancelInflightBuildsTests
    {
        private static CameraProperties Cam(double lon, double lat, double zoom)
            => new CameraProperties(new GeoCoordinate3D { Longitude = lon, Latitude = lat, Altitude = 0 }, zoom, 0, 0);

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
                }
            ]
        }");

        // ── Tooth A: canceled build leaks no native payload (constraint 1) ──────────────────────────

        /// <summary>
        /// Holds a kicked build genuinely in-flight via <see cref="TileManager.MeshBuildGateForTest"/> (set
        /// BEFORE any kick, so the worker parks before doing any work), confirms its kick-allocated payload
        /// sits undisposed, then tears down WITHOUT settling first. Asserts
        /// <see cref="MeshDataPayload.DebugLiveAllocCount"/> returns to baseline — the parked build must
        /// cancel-and-settle to a zero-vertex payload that the existing pen-drain funnel frees, not strand a
        /// native <c>Mesh.MeshDataArray</c>.
        ///
        /// The gate makes the cancellation branch DETERMINISTIC: without it, a fast worker could complete
        /// before Teardown() runs, so the assertion would hold without the build ever being cancelled — green
        /// whether or not a cancelled build settles correctly. Parking on the gate guarantees the build is
        /// still executing (RunWorkerPass parked on WaitHandle.WaitAny) when the lifetime token is cancelled,
        /// so the cancel-settle path is the ONLY way this payload gets freed.
        /// </summary>
        [Test]
        public void CanceledBuild_DisposesKickAllocatedPayload_CounterToBaseline()
        {
            long baseline = MeshDataPayload.DebugLiveAllocCount;

            var src   = TestDataSource.FromBytes(SampleTileFixture.Bytes());
            var go    = new GameObject("MapView_TeardownCancel_A");
            var view  = go.AddComponent<MapView>().WithTestMaterials();
            var style = MinimalStyle();
            view.Config.TileSelection.MinZoom = 5; view.Config.TileSelection.MaxZoom = 5;
            view.WithTestCamera();
            // MaxConsumesPerTick = 0: kicked builds are never consumed, so they (and their kick-allocated
            // payloads) stay genuinely undisposed right up to the moment Teardown() runs.
            view.Config.MaxConsumesPerTick = 0;
            view.Config.MaxMeshBuildsPerTick = 64;

            ManualResetEventSlim gate = new ManualResetEventSlim(false);

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);

                // Set the gate BEFORE any kick: the worker parks on it (jointly with the lifetime token) as
                // its very first statement, so the build below is held genuinely in-flight — still executing
                // when Teardown() cancels the token, forcing the cancel-settle path rather than racing to a
                // normal completion.
                view.TileManager.MeshBuildGateForTest = gate;

                // Kick-pump WITHOUT AwaitInFlightMeshBuilds: with the gate held, a kicked build's task never
                // completes, so awaiting it would park this thread for the full 10s timeout. Fetch completion
                // (which drives kick-eligibility) is independent of the gate, so plain LateUpdate ticks
                // observe at least one kick. One parked build is enough to make the assertion non-vacuous.
                int kicked = 0;
                for (int f = 0; f < 3000 && kicked == 0; f++)
                {
                    view.LateUpdate();
                    kicked += view.MeshBuildsKickedLastTick();
                }
                Assert.Greater(kicked, 0,
                    "drive precondition: at least one mesh build must have been kicked (and therefore parked " +
                    "on the gate) before teardown, or the cancel path below has nothing in-flight to race.");

                // Non-vacuity: DebugLiveAllocCount is bumped synchronously AT KICK, on the main thread
                // (MeshDataPayload.AllocateTracked, inside KickMeshBuild's prologue) — BEFORE the worker
                // parks on the gate. With MaxConsumesPerTick=0 nothing has been disposed yet, so the parked
                // build's payload sits live right now.
                long held = MeshDataPayload.DebugLiveAllocCount;
                Assert.Greater(held, baseline,
                    $"Non-vacuous precondition: the parked build's kick-allocated MeshDataArray must " +
                    $"sit undisposed right before teardown (baseline={baseline}, held={held}), or the " +
                    "cancel-path disposal assertion below is vacuous.");

                // Teardown WITHOUT settling first — the exact race: Stop while tiles are still loading. The
                // gate is NOT set: Teardown's lifetime-token cancel is what must release the parked worker.
                view.Teardown();
                Object.DestroyImmediate(go);
                go = null;

                long after = MeshDataPayload.DebugLiveAllocCount;
                Assert.AreEqual(baseline, after,
                    $"Teardown-cancel constraint 1: a build parked in-flight at teardown must still dispose its " +
                    $"kick-allocated MeshDataArray. baseline={baseline}, after Teardown={after}, delta=" +
                    $"{after - baseline}. A canceled build must settle to a zero-vertex payload " +
                    "(RunWorkerPass's unconditional settle loop) that the existing pen-drain \"if Succeeded, " +
                    "DisposeWholeResult\" funnel frees — never strand a native MeshData allocation.");
            }
            finally
            {
                // Release the gate (and any worker still parked on it) unconditionally so a RED failure never
                // hangs the test runner — a parked ThreadPool worker inside a still-running Teardown() would
                // otherwise block the process's clean shutdown.
                gate.Set();
                gate.Dispose();
                if (go != null)
                {
                    view.Teardown();
                    Object.DestroyImmediate(go);
                }
            }
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

            try
            {
                view.LoadTestStyle(src, Cam(0, 0, 5.0), style: style);

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
                    kicked += view.MeshBuildsKickedLastTick();
                }
                Assert.Greater(kicked, 0,
                    "drive precondition: at least one mesh build must have been kicked (and therefore parked " +
                    "on the gate) before teardown, or this test never held anything genuinely in-flight.");

                var sw = Stopwatch.StartNew();
                view.Teardown();
                sw.Stop();
                Object.DestroyImmediate(go);
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
                // Release the gate (and any worker still parked on it) unconditionally so a RED failure
                // never hangs the test runner — the 10s WaitOffPlayerLoop inside a still-running Teardown()
                // would otherwise leave a parked ThreadPool worker blocking the process's clean shutdown.
                gate.Set();
                gate.Dispose();
                if (go != null)
                {
                    view.Teardown();
                    Object.DestroyImmediate(go);
                }
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

                int kicked = 0;
                for (int f = 0; f < 3000; f++)
                {
                    view.AwaitInFlightMeshBuilds();
                    view.LateUpdate();
                    kicked += view.MeshBuildsKickedLastTick();
                    if (view.LoadedTileCount() > 0 && kicked >= view.LoadedTileCount()) break;
                }
                Assert.GreaterOrEqual(kicked, view.LoadedTileCount(),
                    "drive precondition: every cover tile's mesh build must have been kicked before teardown.");

                view.Teardown();
                Object.DestroyImmediate(go);
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
                    Object.DestroyImmediate(go);
                }
            }
        }
    }
}
