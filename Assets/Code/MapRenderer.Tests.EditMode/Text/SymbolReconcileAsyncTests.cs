// Unity EditMode only — needs a real Camera + the internal SymbolSubsystem, drives the async off-main
// reconcile across pumped editor frames, and (T9) derefs a real native SymbolTileBlock through the gather.
// NOT registered in core-tests.csproj (the pure reconciler/pin-guard teeth are in SymbolReconcilerTests).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Collections;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests; // TestGlyphSource
using MapRenderer.Tests.Text.Placement; // SymbolBatchDiff — R1 memo tests (T2b/T5)
using Symbol = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Stage 4b (symbols-async-reconcile) — the OFF-MAIN reconcile state machine, driven end-to-end through the
    /// production <see cref="SymbolSubsystem"/>: T6 off-main, T7 one-in-flight + apply-stale + stale-front-held,
    /// T8 fault → no swap + reschedule, T10 restyle drains + releases front/back pins, T11 async-front == inline-collect
    /// oracle. T9 (store + gather) proves the pin prevents a native use-after-free. T7/T8/T9 are RED-verified.
    /// </summary>
    [TestFixture]
    public class SymbolReconcileAsyncTests
    {
        private const string FontName = "LatinFont";
        private const string SourceId = "s";

        private static readonly string StyleJson = @"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'layers': [
                { 'id':'labels', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':16, 'text-font':['LatinFont'] } }
            ]
        }".Replace('\'', '"');

        private GameObject _camGo;
        private RenderTexture _rt;
        private SymbolSubsystem _subsystem;
        private byte[] _tileBytes;
        private byte[] _latinGlyphs;
        // A gate a gated test hands the reconciler — Set in TearDown so a failed test never hangs the teardown
        // drain (SPEC A test note: a teardown while GateForTest is held must open the gate first).
        private ManualResetEventSlim _testGate;
        private SymbolGatherPlan _quiescedPlan;

        [SetUp]
        public void SetUp()
        {
            _camGo = new GameObject("SymbolReconcile_TestCamera");
            var uCam = _camGo.AddComponent<Camera>();
            _rt = new RenderTexture(320, 240, 0);
            uCam.targetTexture = _rt;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));
            _subsystem = new SymbolSubsystem(mapCamera);
            _tileBytes = LoadUp("Assets", "Fixtures", "sample-tile.bytes");
            _latinGlyphs = LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
        }

        [TearDown]
        public void TearDown()
        {
            _testGate?.Set(); // open any held reconcile gate FIRST so the Dispose drain can't hang
            _subsystem?.Dispose();
            _testGate = null;
            if (_camGo != null)
            {
                var cam = _camGo.GetComponent<Camera>();
                if (cam != null) cam.targetTexture = null;
            }
            if (_rt != null) UnityEngine.Object.DestroyImmediate(_rt);
            if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
        }

        private void UseImmediateGlyphs()
        {
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            _subsystem.GlyphSourceFactoryOverride = _ => TestGlyphSource.FromRanges(ranges);
            StyleDocument style = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(style, ExtractSymbolLayers(style));
        }

        private static LoadedTileKey Key(TileId t) => new LoadedTileKey(SourceId, t);

        private void DriveTileBytesReady(TileId tile)
        {
            ISymbolTileWorkerPass pass = _subsystem.TryBeginBuild(SourceId, tile);
            if (pass == null) return;
            // The drive helper mirrors TileManager.KickMeshBuild: the tile is decoded ON THE POOL, the kick
            // owns the ONE reference the lease is born with, and its `finally` is the matching release —
            // which is what frees the decoded tile's buffers unless a parked build acquired its own.
            byte[] bytes = _tileBytes;
            UniTask.RunOnThreadPool(() =>
            {
                var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(tile, bytes));
                try { pass.RunWorkerAndHandoff(decode); }
                finally { decode.Release(); }
            }).Forget();
        }

        private static List<Symbol.StyleLayer> ExtractSymbolLayers(StyleDocument style)
        {
            var result = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) result.Add(symbol);
            return result;
        }

        private static int DepartingSymbolCount(SymbolGatherPlan plan)
        {
            int n = 0;
            for (int i = 0; i < plan.WinnerCount; i++) if (plan.Departing[i] != 0) n++;
            return n;
        }

        // Pump the production per-frame loop until the reconcile is FULLY quiescent: symbols committed, no pending
        // tail, nothing in flight, and the schedule generation caught up (CollectRecomputeCount stable 2 frames).
        // Leaves the last plan in _quiescedPlan.
        private IEnumerator PumpToQuiescence(List<LoadedTileKey> loaded)
        {
            int stable = 0; int lastRecompute = -1;
            for (int f = 0; f < 400; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                _quiescedPlan = _subsystem.CurrentBatch(default, 0.0);
                bool settled = _quiescedPlan.WinnerCount > 0 && _subsystem.ReadyTailCount() == 0
                               && !_subsystem.ReconcileInFlight() && _subsystem.CollectRecomputeCount == lastRecompute;
                if (settled) { if (++stable >= 2) yield break; } else stable = 0;
                lastRecompute = _subsystem.CollectRecomputeCount;
                yield return null;
            }
            Assert.Fail("did not reach reconcile quiescence within the frame ceiling");
        }

        // ═══ T6: the cross-tile dedup runs OFF the main thread ═══

        [UnityTest]
        public IEnumerator Reconcile_RunsOffTheMainThread()
        {
            UseImmediateGlyphs();
            int mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });

            Assert.Greater(_subsystem.Reconciler().LastRunThreadId, 0, "sanity: a reconcile actually ran");
            Assert.AreNotEqual(mainThreadId, _subsystem.Reconciler().LastRunThreadId,
                "the cross-tile dedup ran OFF the main thread (a thread-pool worker) — the whole point of Stage 4b");
        }

        // ═══ T7: ONE reconcile in flight; the stale FRONT (unchanged in identity) is served while it is pending; the
        //         completed result is applied on pickup even though the store moved on (apply-stale), then a follow-up
        //         reconcile catches the display up. RED-verify: (a) a discard-on-generation-mismatch impl never
        //         applies the stale result → the stale-applied assertion fails; (b) a clear-front-on-schedule breaks
        //         the held-front assertions; the delta==1 pins one-in-flight coalescing. ═══

        [UnityTest]
        public IEnumerator Reconcile_OneInFlight_ServesStaleFront_AppliesStale_ThenReschedules()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            var loaded = new List<LoadedTileKey> { Key(tile) };
            yield return PumpToQuiescence(loaded);
            int aWinners = _quiescedPlan.WinnerCount;
            Assert.AreEqual(0, DepartingSymbolCount(_quiescedPlan), "front A is the active set (non-departing)");

            // Gate the NEXT reconcile so it parks in flight.
            var gate = new ManualResetEventSlim(false);
            _testGate = gate;
            _subsystem.Reconciler().GateForTest = gate;
            int recomputeBefore = _subsystem.CollectRecomputeCount;
            var empty = new List<LoadedTileKey>();

            // Gated frames. TWO distinct generation-advancing events fire WHILE the one reconcile is in flight, so its
            // captured snapshot is genuinely STALE vs the store's current state:
            //   f==0 → A leaves cover (schedules the gated reconcile off the A-DEPARTING snapshot),
            //   f==5 → A re-enters cover (store moves back to A-ACTIVE; the in-flight snapshot is now stale).
            // Throughout, the held FRONT is served unchanged — same WinnerCount AND the same per-record identity
            // (blockId/localIndex/departing), not merely the same count (item 5). Exactly ONE reconcile is scheduled.
            int[] idBlock = null, idLocal = null; byte[] idDep = null;
            for (int f = 0; f < 12; f++)
            {
                if (f == 0) _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);        // event 1: A departs
                else if (f == 5) _subsystem.ReconcileLoadedTiles(loaded, nowSeconds: 101.0);  // event 2: A re-enters (STALE now)
                else _subsystem.ReconcileLoadedTiles(f < 5 ? empty : loaded, nowSeconds: 101.0);
                _subsystem.PumpBuilds();
                SymbolGatherPlan held = _subsystem.CurrentBatch(default, 0.0);
                Assert.IsTrue(_subsystem.ReconcileInFlight(), $"frame {f}: the reconcile is gated in flight");
                Assert.AreEqual(aWinners, held.WinnerCount, $"frame {f}: the stale FRONT A is served unchanged while pending");
                Assert.AreEqual(0, DepartingSymbolCount(held), $"frame {f}: …still the active set (no premature departing swap)");
                if (f == 0) { idBlock = CopyInts(held.BlockId, aWinners); idLocal = CopyInts(held.LocalIndex, aWinners); idDep = CopyBytes(held.Departing, aWinners); }
                else
                    for (int i = 0; i < aWinners; i++)
                    {
                        Assert.AreEqual(idBlock[i], held.BlockId[i], $"frame {f}: held-front blockId[{i}] identity unchanged");
                        Assert.AreEqual(idLocal[i], held.LocalIndex[i], $"frame {f}: held-front localIndex[{i}] identity unchanged");
                        Assert.AreEqual(idDep[i], held.Departing[i], $"frame {f}: held-front departing[{i}] identity unchanged");
                    }
                yield return null;
            }
            Assert.AreEqual(recomputeBefore + 1, _subsystem.CollectRecomputeCount,
                "exactly ONE reconcile was scheduled across all gated frames (coalesced — never a second in-flight worker)");
            int recomputeAfterGate = _subsystem.CollectRecomputeCount;

            // Release the gate → the completed reconcile is applied on pickup. Its captured state is A-DEPARTING
            // (stale), so the front FLIPS TO DEPARTING even though the store is now A-ACTIVE — proving apply-stale,
            // not a recompute-to-current (a discard-on-mismatch impl would never show departing here).
            gate.Set();
            bool staleApplied = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded, nowSeconds: 101.0); // store stays A-active
                _subsystem.PumpBuilds();
                SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0);
                if (p.WinnerCount > 0 && DepartingSymbolCount(p) == p.WinnerCount) { staleApplied = true; break; }
                yield return null;
            }
            Assert.IsTrue(staleApplied,
                "apply-stale: the completed reconcile's STALE (A-departing) result was applied on pickup — NOT discarded/recomputed to the current A-active state");
            Assert.Greater(_subsystem.CollectRecomputeCount, recomputeAfterGate,
                "a follow-up reconcile was scheduled because the store generation advanced during the run (reschedule-after-stale-apply)");

            // The newer (A-active) state eventually appears as the reschedule catches the display up.
            bool caughtUp = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded, nowSeconds: 101.0);
                _subsystem.PumpBuilds();
                SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0);
                if (p.WinnerCount > 0 && DepartingSymbolCount(p) == 0) { caughtUp = true; break; }
                yield return null;
            }
            Assert.IsTrue(caughtUp, "the newer (A-active) state eventually appears after the reschedule catches up");
        }

        private static int[] CopyInts(NativeList<int> src, int n)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = src[i];
            return a;
        }

        private static byte[] CopyBytes(NativeList<byte> src, int n)
        {
            var a = new byte[n];
            for (int i = 0; i < n; i++) a[i] = src[i];
            return a;
        }

        // ═══ T8: a faulting reconcile (throws AFTER partially populating a MISALIGNED result — Output longer than
        //         BlockId) → the exception is directly OBSERVED (GetResult rethrows), NO swap (a swap would feed the
        //         misaligned partial buffer to SymbolGatherPlan.Build and crash), the old front is held, inFlight
        //         resets, and a later event reschedules + recovers. RED-verify: (a) swap-on-non-success → the
        //         misaligned back reaches the front → the held-front assertion fails (and Build would crash); (b) an
        //         impl that swallows the fault without GetResult → ReconcileFaultObserved stays false. ═══

        [UnityTest]
        public IEnumerator Reconcile_Faults_Observed_NoSwap_FrontHeld_ReschedulesOnNextEvent()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            var loaded = new List<LoadedTileKey> { Key(tile) };
            yield return PumpToQuiescence(loaded);
            int aWinners = _quiescedPlan.WinnerCount;
            int recomputeBefore = _subsystem.CollectRecomputeCount;
            Assert.IsFalse(_subsystem.ReconcileFaultObserved, "precondition: no fault observed yet");

            // Inject a fault into the NEXT reconcile, then a tile event (A leaves cover) to schedule it.
            _subsystem.Reconciler().FaultNextRun = true;
            var empty = new List<LoadedTileKey>();
            bool faultPicked = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                if (!_subsystem.ReconcileInFlight() && _subsystem.CollectRecomputeCount > recomputeBefore) { faultPicked = true; break; }
                yield return null;
            }
            Assert.IsTrue(faultPicked, "the faulting reconcile was scheduled and picked up (inFlight reset)");
            Assert.IsTrue(_subsystem.ReconcileFaultObserved,
                "the worker exception was OBSERVED directly (GetResult rethrew into the catch) — not merely inferred from inFlight");

            SymbolGatherPlan held = _subsystem.CurrentBatch(default, 0.0);
            Assert.AreEqual(aWinners, held.WinnerCount, "fault → NO swap; the old front A is held (the misaligned partial back never reached Build)");
            Assert.AreEqual(0, DepartingSymbolCount(held), "…and it is still the active set, not the faulted/empty back");

            // A NEW event (advance the clock past the departing grace → purge → gen bump) reschedules; the fault is
            // spent, so it succeeds and the front recovers (A purged from the set → empty).
            bool recovered = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 1000.0);
                _subsystem.PumpBuilds();
                SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0);
                if (p.WinnerCount == 0) { recovered = true; break; }
                yield return null;
            }
            Assert.IsTrue(recovered, "a later tile event rescheduled (fault-free) → the reconcile recovered and the front updated");
        }

        // ═══ T10: a restyle mid-flight drains the in-flight worker, releases the front + back snapshot pins (a block
        //          shared front+back refcounts to 2), and frees every block exactly once — no leak, no double-free,
        //          no missing-key throw. The gate is opened BEFORE SetStyle so the inline drain can't hang. ═══

        [UnityTest]
        public IEnumerator Restyle_DrainsInFlightReconcile_ReleasesFrontAndBackPins_NoLeak()
        {
            UseImmediateGlyphs();
            long before = SymbolTileBlock.DebugLiveAllocCount;
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });
            Assert.Greater(SymbolTileBlock.DebugLiveAllocCount, before, "sanity: the tile baked a live block (front pins it)");

            // Gate the next reconcile; a tile event schedules it — CaptureSnapshot pins the SAME block again (shared
            // front+back ⇒ pin count 2). The worker parks.
            var gate = new ManualResetEventSlim(false);
            _testGate = gate;
            _subsystem.Reconciler().GateForTest = gate;
            var empty = new List<LoadedTileKey>();
            for (int f = 0; f < 20 && !_subsystem.ReconcileInFlight(); f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                yield return null;
            }
            Assert.IsTrue(_subsystem.ReconcileInFlight(), "a reconcile is gated in flight (its back snapshot pins the shared block)");

            // Restyle. Open the gate FIRST (SPEC A test note) so DrainInFlightReconcile observes the worker instead
            // of hanging on it. SetStyle then runs the SPEC A protocol: cancel → drain → ReleasePins(front) +
            // ReleasePins(back) → Clear.
            gate.Set();
            StyleDocument restyle = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));
            yield return null; yield return null;

            Assert.AreEqual(before, SymbolTileBlock.DebugLiveAllocCount,
                "restyle drained the in-flight reconcile, released the front + back (shared, refcount 2) pins, and freed every block exactly once — no leak, no double-free");
        }

        // ═══ T10c: the drain must JOIN the worker. A restyle/teardown while the reconcile worker is PARKED MID-RUN
        //          must NOT free the block the worker still reads. A drain built on _reconcileHandle.GetResult()
        //          (WorkHandle<bool>'s equivalent of UniTask's GetAwaiter().GetResult()) THROWS "not yet completed"
        //          on a pending handle instead of blocking, so it returned WITHOUT joining and the following
        //          ReleasePins+Clear disposed the block under Run (native use-after-free) — the exact overlap T10
        //          avoids by opening the gate BEFORE SetStyle. Deterministic, no timing margin: with the worker
        //          parked at the gate, drive the SPEC A teardown core on a BACKGROUND thread — the fixed drain
        //          BLOCKS there (block stays alive), the broken drain returns and frees it — and read block
        //          liveness from the test thread while the worker is STILL parked (gate unset). The 2 s Wait is
        //          only an upper bound to tell "blocked" from "returned"; the liveness assert has no clock.
        //          RED-verify: revert DrainInFlightReconcile to call `_reconcileHandle.GetResult()` directly instead
        //          of `_reconcileHandle.ToUniTask().WaitOffPlayerLoop(...)` first → the teardown returns while the
        //          worker is parked and DebugLiveAllocCount drops → both asserts fail.
        [UnityTest]
        public IEnumerator TeardownWhileReconcileWorkerParked_JoinsWorker_DoesNotFreeBlockUnderIt()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });
            long liveWithBlock = SymbolTileBlock.DebugLiveAllocCount;
            Assert.Greater(liveWithBlock, 0, "sanity: the tile baked a live block the front pins");

            // Gate the next reconcile so its worker PARKS in flight — CaptureSnapshot pins the same block again
            // (shared front+back), and the worker holds that back pin while parked at the top of Run.
            var gate = new ManualResetEventSlim(false);
            _testGate = gate;
            _subsystem.Reconciler().GateForTest = gate;
            var empty = new List<LoadedTileKey>();
            for (int f = 0; f < 20 && !_subsystem.ReconcileInFlight(); f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                yield return null;
            }
            Assert.IsTrue(_subsystem.ReconcileInFlight(), "a reconcile is gated in flight (its back snapshot pins the shared block)");
            // Run consumes+nulls GateForTest immediately BEFORE gate.Wait() (SymbolReconciler.cs), so a null gate
            // proves the worker is PARKED inside Run, past every cancellation check, having read no columns yet.
            Assert.IsTrue(System.Threading.SpinWait.SpinUntil(() => _subsystem.Reconciler().GateForTest == null, 5000),
                "the reconcile worker reached the gate (parked inside Run)");

            // The SPEC A teardown core (cancel → drain → ReleasePins front+back → Clear) on a BACKGROUND thread.
            // Fixed drain: blocks on the parked worker HERE (never reaches ReleasePins). Broken drain: returns at
            // once, then ReleasePins drops the pin to 0 and the block is disposed while the worker is still parked.
            var teardown = System.Threading.Tasks.Task.Run(() => _subsystem.TeardownReconcileForTest());

            bool teardownReturnedWhileParked = teardown.Wait(2000); // upper bound: broken ≈ µs, fixed blocks until we set the gate
            long liveWhileWorkerParked = SymbolTileBlock.DebugLiveAllocCount; // worker still parked (gate unset) ⇒ no clock

            gate.Set();        // release the worker so the fixed teardown can join it and complete
            teardown.Wait();   // let the teardown finish in both builds (never hangs — the gate is open)

            Assert.IsFalse(teardownReturnedWhileParked,
                "the drain must BLOCK until the worker completes — it returned while the worker was still parked mid-Run, so it never joined it (GetResult throws on a pending task instead of waiting)");
            Assert.AreEqual(liveWithBlock, liveWhileWorkerParked,
                "the block a parked reconcile worker still reads must NOT be freed under it: a non-joining drain lets ReleasePins+Clear dispose it mid-Run (native use-after-free)");
        }

        // ═══ T10b: the SUCCESS-swap pin release (SymbolSubsystem.cs:798), independent of teardown. A rebuild
        //          defers the old block (pinned by the displayed front); when the next reconcile SUCCEEDS and swaps,
        //          the demoted old-front snapshot's ReleasePins frees that now-unreferenced block, returning to
        //          baseline WHILE the subsystem is still live. RED-verify: delete the success-branch ReleasePins
        //          (:798) → the demoted front never releases → the rebuilt-over block stays deferred → this times out. ═══

        [UnityTest]
        public IEnumerator SuccessfulSwap_ReleasesDemotedFrontPins_FreesRebuiltOverBlock()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(loaded);
            long liveWithOneBlock = SymbolTileBlock.DebugLiveAllocCount; // the front pins this tile's block

            // Rebuild the tile → a NEW block is baked; the OLD block is still referenced by the front snapshot, so
            // CompleteBuild's release of the entry's own reference leaves it alive (the snapshot's own reference
            // survives). PHASE 1: wait until the rebuild has committed and the old block is genuinely
            // referenced-but-alive (TWO live blocks) — so the phase-2 return-to-one is a REAL free, not a
            // trivially-already-true baseline.
            DriveTileBytesReady(tile);
            bool deferred = false;
            for (int f = 0; f < 300; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                if (SymbolTileBlock.DebugLiveAllocCount == liveWithOneBlock + 1) { deferred = true; break; }
                yield return null;
            }
            Assert.IsTrue(deferred, "sanity: the rebuild baked a new block and DEFERRED the old (pinned) one — two live blocks");

            // PHASE 2: the successful reconcile swap for the new generation demotes the old front and (:798)
            // releases its pins → the deferred old block frees, returning to ONE live block — with no restyle/teardown.
            bool freedBackToBaseline = false;
            for (int f = 0; f < 400; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                if (_subsystem.ReadyTailCount() == 0 && !_subsystem.ReconcileInFlight()
                    && SymbolTileBlock.DebugLiveAllocCount == liveWithOneBlock) { freedBackToBaseline = true; break; }
                yield return null;
            }
            Assert.IsTrue(freedBackToBaseline,
                "a SUCCESSFUL reconcile swap demoted the old front and released its pins (:798), freeing the deferred rebuilt-over block back to one live block — distinct from teardown");
        }

        // ═══ T8b: the FAULT-path pin release (SymbolSubsystem.cs:805). A fault captures a back snapshot that
        //          shares the displayed block with the front; the fault path must ReleasePins that FAILED back, or
        //          its pin LEAKS and the block can never be freed. A subsequent restyle (SPEC A drain+release+Clear)
        //          then frees everything back to baseline. RED-verify: delete the fault-branch ReleasePins (:805) →
        //          the leaked back-pin keeps the block pinned → restyle's Clear can't free it → leak (not baseline). ═══

        [UnityTest]
        public IEnumerator FaultPickup_ReleasesFailedBackPins_NoLeakAfterRestyle()
        {
            UseImmediateGlyphs();
            long before = SymbolTileBlock.DebugLiveAllocCount;
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });
            Assert.Greater(SymbolTileBlock.DebugLiveAllocCount, before, "sanity: the tile baked a live block (front pins it)");

            // Fault the next reconcile; a tile event (A departs) schedules it — the captured back snapshot shares the
            // tile's block with the front (refcount 2). On the fault pickup, :805 must ReleasePins the failed back.
            _subsystem.Reconciler().FaultNextRun = true;
            var empty = new List<LoadedTileKey>();
            bool faultObserved = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                if (_subsystem.ReconcileFaultObserved && !_subsystem.ReconcileInFlight()) { faultObserved = true; break; }
                yield return null;
            }
            Assert.IsTrue(faultObserved, "the fault was observed and its pickup completed");

            // Restyle → SPEC A drain + ReleasePins(front)+ReleasePins(back) + Clear. WITH :805 the shared block's back
            // pin was already released, so front-release + Clear free it exactly once → baseline. WITHOUT :805 the
            // leaked back pin keeps it pinned, so Clear's conditional flush must leave it → a leaked live block.
            StyleDocument restyle = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));
            yield return null; yield return null;
            Assert.AreEqual(before, SymbolTileBlock.DebugLiveAllocCount,
                "the fault path released the FAILED back snapshot's pins (:805) → restyle frees the block exactly once, no leak");
        }

        // ═══ T11: the async production path yields the SAME winner plan as the store's inline CollectInto oracle
        //          over the same quiesced state (minCoverage 0 ⇒ no coverage classify to perturb order). ═══

        [UnityTest]
        public IEnumerator ProductionFront_MatchesInlineCollectOracle()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });
            SymbolGatherPlan front = _subsystem.CurrentBatch(default, 0.0);

            var oBlk = new List<int>(); var oLoc = new List<int>(); var oDep = new List<byte>();
            _subsystem.Store().CollectInto(oBlk, oLoc, oDep, SymbolSubsystem.DedupEnabled, out int _);

            Assert.Greater(oBlk.Count, 0, "sanity: the oracle collected labels");
            Assert.AreEqual(oBlk.Count, front.WinnerCount, "async front winner count == inline collect oracle");
            for (int i = 0; i < oBlk.Count; i++)
            {
                Assert.AreEqual(oBlk[i], front.BlockId[i], $"blockId[{i}] mismatch (async front vs inline oracle)");
                Assert.AreEqual(oLoc[i], front.LocalIndex[i], $"localIndex[{i}] mismatch");
                Assert.AreEqual(oDep[i], front.Departing[i], $"departing[{i}] mismatch");
            }
        }

        // ═══ R1 T2b: a REAL front swap through the production subsystem must invalidate the gather memo — the
        //      stage's only end-to-end guard (T2a in SymbolGatherMemoTests only proves the version-consulted UNIT
        //      behaviour; only this proves the key is wired to a real reconcile swap). Three required properties
        //      (Codex SHOULD-FIX 1 — the original construction, kept as-is, was missing all three):
        //        (a) EVENT vs SWAP keying: immediately after the store event (BeginBuild/CompleteBuild), before
        //            the reconcile completes, the mirror must stay a memo HIT and still serve the OLD content —
        //            an implementation keyed on the tile EVENT (e.g. _store.CollectGeneration, the key the
        //            plan's design note originally proposed and the plan itself corrected — §"Key choice") would
        //            wrongly rebuild HERE instead of waiting for the swap.
        //        (b) The rebuild must land specifically on the frame PickupCompletedReconcile's swap actually
        //            happens (ReconcileInFlight() observed true, THEN the pickup call flips it false), not
        //            merely "sometime within a settle window".
        //        (c) The new content must be gather-VISIBLE-DIFFERENT at a FIXED WinnerCount (3.4's coupled
        //            constraint) — a byte-identical rebuild (this test's ORIGINAL approach, mirroring
        //            SuccessfulSwap_ReleasesDemotedFrontPins_FreesRebuiltOverBlock's T10b construction) can never
        //            fail SymbolBatchDiff regardless of memo correctness.
        //      The tile event commits the replacement DIRECTLY through Store() (BeginBuild+Bake+
        //      CompleteBuild — the SAME two store calls the real async worker path uses; MarkCollectDirty fires
        //      from inside them either way, so this still exercises the real front/back double-buffer + pickup +
        //      apply-stale state machine, only the MVT-decode step is bypassed). BOTH calls run in ONE
        //      synchronous block, no CurrentBatch poll between them, so their two independent CollectGeneration
        //      bumps (SymbolTileStore.cs:156,195 — BeginBuild and CompleteBuild each "always bump") coalesce
        //      into exactly ONE ScheduleReconcileIfDirty-observed event: a single, precisely-timed swap, which is
        //      what lets (a)/(b) assert an EXACT frame instead of tolerating the legitimate-but-unpredictable
        //      double-swap a DriveTileBytesReady-driven rebuild can trigger (see Memo_RestyleBetweenTicks_Invalidates'
        //      sibling test and this file's SuccessfulSwap_ReleasesDemotedFrontPins_FreesRebuiltOverBlock, both of
        //      which DO tolerate that). ═══

        [UnityTest]
        public IEnumerator Memo_RealFrontSwap_Invalidates()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(loaded);

            var harness = new LpsHarness();
            try
            {
                SymbolGatherPlan plan = _subsystem.CurrentBatch(default, 0.0); // the subsystem's ONE reused _gatherPlan
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the first gather is a heavy rebuild");
                int winnersBefore = plan.WinnerCount;
                Assert.Greater(winnersBefore, 0, "sanity: the tile produced at least one winner");
                var contentBefore = new SymbolBatch(); harness.Lps.CopyMirrorInto(contentBefore);

                // Steady state, no tile event — the memo must engage and stay engaged.
                for (int f = 0; f < 3; f++)
                {
                    _subsystem.ReconcileLoadedTiles(loaded);
                    _subsystem.PumpBuilds();
                    SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0);
                    harness.Lps.GatherIntoMirror(p);
                    Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, $"frame {f}: no tile event — the gather must stay a memo HIT");
                }

                // The real tile event: replace the SAME store key's content with a DIFFERENT, gather-visible
                // payload at the SAME winner count (Codex 1c) — synthetic symbols far from any coordinate the
                // real fixture decodes to, so FirstDifference below is unambiguous regardless of the fixture's
                // own content. BeginBuild+Bake+CompleteBuild run back-to-back (no CurrentBatch poll between
                // them — see the header comment's coalescing argument).
                var replacementBuffer = new SymbolTileBuffer();
                for (int i = 0; i < winnersBefore; i++)
                    AddPointSymbol(replacementBuffer, new double3(9000 + i * 10, 0, 9000), "replacement" + i, 900 + i, Tk(tile), 0.9f);
                int gen = _subsystem.Store().BeginBuild(StoreKey(tile));
                SymbolTileBlock newBlock = SymbolTileBlockBaker.Bake(
                    replacementBuffer, slotCount: 1, TileRenderOrigin.Project(tile, P));
                Assert.IsTrue(_subsystem.Store().CompleteBuild(StoreKey(tile), gen, newBlock), "sanity: replacement block committed");

                // (a) EVENT-keying check: the frame right after the store event (before any reconcile has had a
                // chance to complete) must still be a memo HIT serving the OLD content.
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                SymbolGatherPlan pEvent = _subsystem.CurrentBatch(default, 0.0);
                harness.Lps.GatherIntoMirror(pEvent);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount,
                    "immediately after the store event the mirror must still be a memo HIT — event-keyed (rather " +
                    "than swap-keyed) invalidation would wrongly rebuild here, before the reconcile has even completed");
                var contentAtEvent = new SymbolBatch(); harness.Lps.CopyMirrorInto(contentAtEvent);
                Assert.IsNull(SymbolBatchDiff.FirstDifference(contentBefore, contentAtEvent),
                    "the OLD front's content must still be served this frame — the replacement hasn't been picked up yet");

                // (b) poll until PickupCompletedReconcile's swap actually lands (ReconcileInFlight() observed
                // true beforehand, then the pickup call flips it false this frame), asserting the mirror stays
                // flat WHILE the worker is in flight and rebuilds EXACTLY on the swap frame — not merely
                // "eventually, within a settle window".
                bool sawInFlight = false, swappedThisFrame = false;
                for (int f = 0; f < 400 && !swappedThisFrame; f++)
                {
                    _subsystem.ReconcileLoadedTiles(loaded);
                    _subsystem.PumpBuilds();
                    bool inFlightBefore = _subsystem.ReconcileInFlight();
                    SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0); // PickupCompletedReconcile runs inside this call
                    harness.Lps.GatherIntoMirror(p);
                    if (inFlightBefore) sawInFlight = true;
                    if (harness.Lps.MirrorRebuildCount > 1)
                    {
                        swappedThisFrame = true;
                        Assert.IsTrue(sawInFlight,
                            "the swap must be preceded by an observed in-flight reconcile — proves the memo tracked the SWAP, not just the store event");
                    }
                    else
                    {
                        Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, $"frame {f}: still waiting for pickup — the mirror must stay a memo HIT until the swap lands");
                        yield return null;
                    }
                }
                Assert.IsTrue(swappedThisFrame, "the tile-replacement event must eventually land as a front swap");
                Assert.AreEqual(2, harness.Lps.MirrorRebuildCount,
                    "exactly ONE heavy rebuild for the single, precisely-timed swap — a missing version bump would leave this flat");

                SymbolGatherPlan finalPlan = _subsystem.CurrentBatch(default, 0.0);
                Assert.AreEqual(winnersBefore, finalPlan.WinnerCount, "coupled constraint: WinnerCount must be UNCHANGED across the swap");

                // (c) content genuinely changed — the memo, when it correctly invalidated, picked up the
                // REPLACEMENT, not stale OLD content held over from before the swap.
                var contentAfter = new SymbolBatch(); harness.Lps.CopyMirrorInto(contentAfter);
                Assert.IsNotNull(SymbolBatchDiff.FirstDifference(contentBefore, contentAfter),
                    "post-swap content must DIFFER from pre-swap content — a memo that failed to invalidate would still show the OLD payload");

                // Post-swap frames must go back to memo-hitting (no further event).
                int rebuildAfterSwap = harness.Lps.MirrorRebuildCount;
                for (int f = 0; f < 3; f++)
                {
                    _subsystem.ReconcileLoadedTiles(loaded);
                    _subsystem.PumpBuilds();
                    SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0);
                    harness.Lps.GatherIntoMirror(p);
                    Assert.AreEqual(rebuildAfterSwap, harness.Lps.MirrorRebuildCount, $"post-swap frame {f}: back to a memo HIT");
                }

                // Reference: an independent gather over the SAME final plan content — confirms the held mirror's
                // content is not just "different from before" but EXACTLY the replacement.
                var refHarness = new LpsHarness();
                try
                {
                    refHarness.Lps.GatherIntoMirror(finalPlan);
                    var got = new SymbolBatch(); harness.Lps.CopyMirrorInto(got);
                    var want = new SymbolBatch(); refHarness.Lps.CopyMirrorInto(want);
                    Assert.IsNull(SymbolBatchDiff.FirstDifference(want, got),
                        "post-swap the held-mirror system's content must match a fresh gather over the same plan");
                }
                finally { refHarness.Dispose(); }
            }
            finally { harness.Dispose(); }
        }

        // ═══ R1 T5: a restyle (SetStyle) between two production gathers on the SAME plan object must invalidate
        //      the memo, even though the front content collapses to EMPTY. Step 2.3's `_frontSetVersion++` is
        //      defense-in-depth (3.3's `plan.WinnerCount == _mirrorCount` predicate term is the actual crash-prevention
        //      for a release player) — with the bump present this test observes no throw and a clean rebuild; the
        //      RED-verify signal for a MISSING bump is a Debug.LogAssertion from AssertMemoPlanMatchesMirror
        //      (NUnit fails a test on an unexpected one), not a content diff or a throw — see the plan's T5 row. ═══

        [UnityTest]
        public IEnumerator Memo_RestyleBetweenTicks_Invalidates()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(loaded);

            var harness = new LpsHarness();
            try
            {
                SymbolGatherPlan plan = _subsystem.CurrentBatch(default, 0.0);
                harness.Lps.GatherIntoMirror(plan);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount, "sanity: the first gather is a heavy rebuild");
                Assert.Greater(plan.WinnerCount, 0, "sanity: the tile produced at least one winner before the restyle");

                // Restyle: SetStyle Clear()s _frontResult/_backResult — Step 2.3's bump site.
                StyleDocument restyle = StyleParser.Parse(StyleJson);
                _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));

                SymbolGatherPlan afterRestyle = _subsystem.CurrentBatch(default, 0.0); // SAME _gatherPlan object, now empty
                Assert.DoesNotThrow(() => harness.Lps.GatherIntoMirror(afterRestyle),
                    "a same-object, now-empty plan must rebuild cleanly (no out-of-range read) after a restyle");
                Assert.AreEqual(2, harness.Lps.MirrorRebuildCount,
                    "the restyle must invalidate the memo — a stale memo hit against a zero-length plan would either " +
                    "throw (checked NativeArray.Copy) or silently read garbage (release player)");
                Assert.AreEqual(0, afterRestyle.WinnerCount, "sanity: the front is empty after the restyle");
            }
            finally { harness.Dispose(); }
        }

        // ═══ T9: the pin prevents a native USE-AFTER-FREE — a block a reconcile result still references is NOT freed
        //         at a drop site while a snapshot references it; the gather derefs its NativeArrays safely; the
        //         snapshot's release frees it.
        //         RED-verify: revert the Release true-evict site's `?.Release()` to a direct `?.Value.Dispose()` →
        //         the block frees at the drop site (the "not freed" assertion fails) and the gather derefs freed
        //         memory. ═══

        private static readonly WebMercatorProjection P = new WebMercatorProjection();
        private static SymbolTileStore.Key StoreKey(TileId t) => new SymbolTileStore.Key("s", t);
        private static long Tk(TileId t) => SymbolTileKey.Pack(t);

        private static (List<SymbolQuad> Quads, float2 BoundsMin, float2 BoundsMax) OneQuad(float u) => (
            new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(u, u), UvBottomRight = new float2(u + 0.2f, u + 0.2f), LineIndex = 0,
                },
            },
            float2.zero, new float2(18f, 18f));

        private static void AddPointSymbol(SymbolTileBuffer buffer, double3 anchor, string text, int feature, long tileKey, float u)
        {
            var layout = OneQuad(u);
            TestSymbolTileBuffer.AddPoint(buffer, anchor, layout.Quads, layout.BoundsMin, layout.BoundsMax,
                text: text, textSizePx: 20f, paddingPx: 2f, featureIndex: feature, tileKey: tileKey, paint: SymbolPaint.Default);
        }

        [Test]
        public void Pin_PreventsNativeUseAfterFree_InGather()
        {
            var tile = new TileId { Z = 5, X = 16, Y = 16 };
            var store = new SymbolTileStore(cacheCap: 8);
            var buffer = new SymbolTileBuffer();
            AddPointSymbol(buffer, new double3(100, 0, 200), "a", 1, Tk(tile), 0.1f);
            int gen = store.BeginBuild(StoreKey(tile));
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, TileRenderOrigin.Project(tile, P));
            Assert.IsTrue(store.CompleteBuild(StoreKey(tile), gen, block), "sanity: block committed");
            long live0 = SymbolTileBlock.DebugLiveAllocCount;

            // Capture (pins the block) + run the reconciler → a result referencing the block via OrderedBlocks.
            var snapshot = new SymbolSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolReconciler();
            var result = new SymbolReconcileResult();
            reconciler.Run(snapshot, result);

            // A store drop site fires while the snapshot still references the block → the drop must NOT free it
            // (the snapshot's own reference survives), so the gather below can still deref its NativeArrays.
            store.Release(StoreKey(tile), transferredToCache: false); // active → true evict → releases the entry's own ref
            Assert.AreEqual(live0, SymbolTileBlock.DebugLiveAllocCount,
                "the pinned block is NOT freed at the drop site (deferred) — reverting to a direct Dispose fails HERE");

            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                var decisions = new List<byte>(result.BlockId.Count);
                for (int i = 0; i < result.BlockId.Count; i++) decisions.Add(SymbolTileCoverageFilter.Keep);
                plan.Build(result.BlockId, result.LocalIndex, result.IsDeparting, decisions, result.OrderedBlocks, winnerSetVersion: 0);
                harness.Lps.GatherIntoMirror(plan); // derefs the (still-alive, pinned) block's NativeArrays — a freed block here is a UAF
                var gathered = new SymbolBatch();
                harness.Lps.CopyMirrorInto(gathered);
                Assert.Greater(gathered.Count, 0, "the gather read the pinned block's records with no use-after-free");

                store.ReleasePins(snapshot); // last pin gone → the deferred dispose fires now
                Assert.AreEqual(live0 - 1, SymbolTileBlock.DebugLiveAllocCount,
                    "releasing the last pin frees the deferred block exactly once");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // Owns the LPS + throwaway Unity resources for the gather (mirrors SymbolGatherParityTests.LpsHarness).
        private sealed class LpsHarness : IDisposable
        {
            public readonly SymbolPlacementSystem Lps;
            private readonly GameObject _camGo;
            private readonly RenderTexture _rt;
            private readonly Material _baseMaterial;

            public LpsHarness()
            {
                _camGo = new GameObject("ReconcileUaf_TestCamera");
                var uCam = _camGo.AddComponent<Camera>();
                _rt = new RenderTexture(64, 64, 0);
                uCam.targetTexture = _rt;
                var mapCamera = new MapCamera(uCam, new CameraProperties(
                    new GeoCoordinate3D { Latitude = 0, Longitude = 0, Altitude = 0 }, zoom: 5.0, heading: 0.0, tilt: 0.0));
                _baseMaterial = new Material(Shader.Find("Map/Symbol/TextWorld"));
                Lps = new SymbolPlacementSystem(mapCamera, _baseMaterial);
            }

            public void Dispose()
            {
                Lps.Dispose();
                var cam = _camGo.GetComponent<Camera>();
                if (cam != null) cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(_camGo);
                UnityEngine.Object.DestroyImmediate(_rt);
                UnityEngine.Object.DestroyImmediate(_baseMaterial);
            }
        }

        private static byte[] LoadUp(params string[] relative)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    string p = Path.Combine(dir.FullName, Path.Combine(relative));
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = dir.Parent;
                }
            }
            throw new FileNotFoundException("fixture not found: " + Path.Combine(relative));
        }
    }
}
