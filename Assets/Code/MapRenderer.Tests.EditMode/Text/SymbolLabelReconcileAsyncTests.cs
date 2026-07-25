// Unity EditMode only — needs a real Camera + the internal SymbolLabelSubsystem, drives the async off-main
// reconcile across pumped editor frames, and (T9) derefs a real native SymbolTileLabelBlock through the gather.
// NOT registered in core-tests.csproj (the pure reconciler/pin-guard teeth are in SymbolLabelReconcilerTests).

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
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests; // TestGlyphSource
using MapRenderer.Tests.Text.Placement; // SymbolLabelBatchDiff — R1 memo tests (T2b/T5)
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Stage 4b (labels-async-reconcile) — the OFF-MAIN reconcile state machine, driven end-to-end through the
    /// production <see cref="SymbolLabelSubsystem"/>: T6 off-main, T7 one-in-flight + apply-stale + stale-front-held,
    /// T8 fault → no swap + reschedule, T10 restyle drains + releases front/back pins, T11 async-front == inline-collect
    /// oracle. T9 (store + gather) proves the pin prevents a native use-after-free. T7/T8/T9 are RED-verified.
    /// </summary>
    [TestFixture]
    public class SymbolLabelReconcileAsyncTests
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
        private SymbolLabelSubsystem _subsystem;
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
            _subsystem = new SymbolLabelSubsystem(mapCamera);
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
            UniTask.RunOnThreadPool(() => pass.RunWorkerAndHandoff(new SharedTileDecode(_tileBytes, new MvtTileDecoder()))).Forget();
        }

        private static List<Symbol.StyleLayer> ExtractSymbolLayers(StyleDocument style)
        {
            var result = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) result.Add(symbol);
            return result;
        }

        private static int DepartingRecordCount(SymbolGatherPlan plan)
        {
            int n = 0;
            for (int i = 0; i < plan.WinnerCount; i++) if (plan.Departing[i] != 0) n++;
            return n;
        }

        // Pump the production per-frame loop until the reconcile is FULLY quiescent: labels committed, no pending
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
                bool settled = _quiescedPlan.WinnerCount > 0 && _subsystem.ReadyTailCount == 0
                               && !_subsystem.ReconcileInFlightForTest && _subsystem.CollectRecomputeCount == lastRecompute;
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

            Assert.Greater(_subsystem.ReconcilerForTest.LastRunThreadId, 0, "sanity: a reconcile actually ran");
            Assert.AreNotEqual(mainThreadId, _subsystem.ReconcilerForTest.LastRunThreadId,
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
            Assert.AreEqual(0, DepartingRecordCount(_quiescedPlan), "front A is the active set (non-departing)");

            // Gate the NEXT reconcile so it parks in flight.
            var gate = new ManualResetEventSlim(false);
            _testGate = gate;
            _subsystem.ReconcilerForTest.GateForTest = gate;
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
                Assert.IsTrue(_subsystem.ReconcileInFlightForTest, $"frame {f}: the reconcile is gated in flight");
                Assert.AreEqual(aWinners, held.WinnerCount, $"frame {f}: the stale FRONT A is served unchanged while pending");
                Assert.AreEqual(0, DepartingRecordCount(held), $"frame {f}: …still the active set (no premature departing swap)");
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
                if (p.WinnerCount > 0 && DepartingRecordCount(p) == p.WinnerCount) { staleApplied = true; break; }
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
                if (p.WinnerCount > 0 && DepartingRecordCount(p) == 0) { caughtUp = true; break; }
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
        //         impl that swallows the fault without GetResult → ReconcileFaultObservedForTest stays false. ═══

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
            Assert.IsFalse(_subsystem.ReconcileFaultObservedForTest, "precondition: no fault observed yet");

            // Inject a fault into the NEXT reconcile, then a tile event (A leaves cover) to schedule it.
            _subsystem.ReconcilerForTest.FaultNextRun = true;
            var empty = new List<LoadedTileKey>();
            bool faultPicked = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                if (!_subsystem.ReconcileInFlightForTest && _subsystem.CollectRecomputeCount > recomputeBefore) { faultPicked = true; break; }
                yield return null;
            }
            Assert.IsTrue(faultPicked, "the faulting reconcile was scheduled and picked up (inFlight reset)");
            Assert.IsTrue(_subsystem.ReconcileFaultObservedForTest,
                "the worker exception was OBSERVED directly (GetResult rethrew into the catch) — not merely inferred from inFlight");

            SymbolGatherPlan held = _subsystem.CurrentBatch(default, 0.0);
            Assert.AreEqual(aWinners, held.WinnerCount, "fault → NO swap; the old front A is held (the misaligned partial back never reached Build)");
            Assert.AreEqual(0, DepartingRecordCount(held), "…and it is still the active set, not the faulted/empty back");

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
            long before = SymbolTileLabelBlock.DebugLiveAllocCount;
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });
            Assert.Greater(SymbolTileLabelBlock.DebugLiveAllocCount, before, "sanity: the tile baked a live block (front pins it)");

            // Gate the next reconcile; a tile event schedules it — CaptureSnapshot pins the SAME block again (shared
            // front+back ⇒ pin count 2). The worker parks.
            var gate = new ManualResetEventSlim(false);
            _testGate = gate;
            _subsystem.ReconcilerForTest.GateForTest = gate;
            var empty = new List<LoadedTileKey>();
            for (int f = 0; f < 20 && !_subsystem.ReconcileInFlightForTest; f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                yield return null;
            }
            Assert.IsTrue(_subsystem.ReconcileInFlightForTest, "a reconcile is gated in flight (its back snapshot pins the shared block)");

            // Restyle. Open the gate FIRST (SPEC A test note) so DrainInFlightReconcile observes the worker instead
            // of hanging on it. SetStyle then runs the SPEC A protocol: cancel → drain → ReleasePins(front) +
            // ReleasePins(back) → Clear.
            gate.Set();
            StyleDocument restyle = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));
            yield return null; yield return null;

            Assert.AreEqual(before, SymbolTileLabelBlock.DebugLiveAllocCount,
                "restyle drained the in-flight reconcile, released the front + back (shared, refcount 2) pins, and freed every block exactly once — no leak, no double-free");
        }

        // ═══ T10b: the SUCCESS-swap pin release (SymbolLabelSubsystem.cs:798), independent of teardown. A rebuild
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
            long liveWithOneBlock = SymbolTileLabelBlock.DebugLiveAllocCount; // the front pins this tile's block

            // Rebuild the tile → a NEW block is baked; the OLD block is pinned by the front snapshot, so
            // CompleteBuild's DisposeOrDefer DEFERS it. PHASE 1: wait until the rebuild has committed and the old
            // block is genuinely deferred-but-alive (TWO live blocks) — so the phase-2 return-to-one is a REAL
            // free of a deferred block, not a trivially-already-true baseline.
            DriveTileBytesReady(tile);
            bool deferred = false;
            for (int f = 0; f < 300; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                if (SymbolTileLabelBlock.DebugLiveAllocCount == liveWithOneBlock + 1) { deferred = true; break; }
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
                if (_subsystem.ReadyTailCount == 0 && !_subsystem.ReconcileInFlightForTest
                    && SymbolTileLabelBlock.DebugLiveAllocCount == liveWithOneBlock) { freedBackToBaseline = true; break; }
                yield return null;
            }
            Assert.IsTrue(freedBackToBaseline,
                "a SUCCESSFUL reconcile swap demoted the old front and released its pins (:798), freeing the deferred rebuilt-over block back to one live block — distinct from teardown");
        }

        // ═══ T8b: the FAULT-path pin release (SymbolLabelSubsystem.cs:805). A fault captures a back snapshot that
        //          shares the displayed block with the front; the fault path must ReleasePins that FAILED back, or
        //          its pin LEAKS and the block can never be freed. A subsequent restyle (SPEC A drain+release+Clear)
        //          then frees everything back to baseline. RED-verify: delete the fault-branch ReleasePins (:805) →
        //          the leaked back-pin keeps the block pinned → restyle's Clear can't free it → leak (not baseline). ═══

        [UnityTest]
        public IEnumerator FaultPickup_ReleasesFailedBackPins_NoLeakAfterRestyle()
        {
            UseImmediateGlyphs();
            long before = SymbolTileLabelBlock.DebugLiveAllocCount;
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            DriveTileBytesReady(tile);
            yield return PumpToQuiescence(new List<LoadedTileKey> { Key(tile) });
            Assert.Greater(SymbolTileLabelBlock.DebugLiveAllocCount, before, "sanity: the tile baked a live block (front pins it)");

            // Fault the next reconcile; a tile event (A departs) schedules it — the captured back snapshot shares the
            // tile's block with the front (refcount 2). On the fault pickup, :805 must ReleasePins the failed back.
            _subsystem.ReconcilerForTest.FaultNextRun = true;
            var empty = new List<LoadedTileKey>();
            bool faultObserved = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(empty, nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                _subsystem.CurrentBatch(default, 0.0);
                if (_subsystem.ReconcileFaultObservedForTest && !_subsystem.ReconcileInFlightForTest) { faultObserved = true; break; }
                yield return null;
            }
            Assert.IsTrue(faultObserved, "the fault was observed and its pickup completed");

            // Restyle → SPEC A drain + ReleasePins(front)+ReleasePins(back) + Clear. WITH :805 the shared block's back
            // pin was already released, so front-release + Clear free it exactly once → baseline. WITHOUT :805 the
            // leaked back pin keeps it pinned, so Clear's conditional flush must leave it → a leaked live block.
            StyleDocument restyle = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));
            yield return null; yield return null;
            Assert.AreEqual(before, SymbolTileLabelBlock.DebugLiveAllocCount,
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

            var oOut = new List<LabelInstance>(); var oBlk = new List<int>();
            var oLoc = new List<int>(); var oDep = new List<byte>();
            _subsystem.StoreForTest.CollectInto(oOut, oBlk, oLoc, oDep, SymbolLabelSubsystem.DedupEnabled, out int _);

            Assert.Greater(oOut.Count, 0, "sanity: the oracle collected labels");
            Assert.AreEqual(oOut.Count, front.WinnerCount, "async front winner count == inline collect oracle");
            for (int i = 0; i < oOut.Count; i++)
            {
                Assert.AreEqual(oBlk[i], front.BlockId[i], $"blockId[{i}] mismatch (async front vs inline oracle)");
                Assert.AreEqual(oLoc[i], front.LocalIndex[i], $"localIndex[{i}] mismatch");
                Assert.AreEqual(oDep[i], front.Departing[i], $"departing[{i}] mismatch");
            }
        }

        // ═══ R1 T2b: a REAL front swap through the production subsystem must invalidate the gather memo — the
        //      stage's only end-to-end guard (T2a in LabelGatherMemoTests only proves the version-consulted UNIT
        //      behaviour; only this proves the key is wired to a real reconcile swap). Three required properties
        //      (Codex SHOULD-FIX 1 — the original construction, kept as-is, was missing all three):
        //        (a) EVENT vs SWAP keying: immediately after the store event (BeginBuild/CompleteBuild), before
        //            the reconcile completes, the mirror must stay a memo HIT and still serve the OLD content —
        //            an implementation keyed on the tile EVENT (e.g. _store.CollectGeneration, the key the
        //            plan's design note originally proposed and the plan itself corrected — §"Key choice") would
        //            wrongly rebuild HERE instead of waiting for the swap.
        //        (b) The rebuild must land specifically on the frame PickupCompletedReconcile's swap actually
        //            happens (ReconcileInFlightForTest observed true, THEN the pickup call flips it false), not
        //            merely "sometime within a settle window".
        //        (c) The new content must be gather-VISIBLE-DIFFERENT at a FIXED WinnerCount (3.4's coupled
        //            constraint) — a byte-identical rebuild (this test's ORIGINAL approach, mirroring
        //            SuccessfulSwap_ReleasesDemotedFrontPins_FreesRebuiltOverBlock's T10b construction) can never
        //            fail SymbolLabelBatchDiff regardless of memo correctness.
        //      The tile event commits the replacement DIRECTLY through StoreForTest (BeginBuild+Bake+
        //      CompleteBuild — the SAME two store calls the real async worker path uses; MarkCollectDirty fires
        //      from inside them either way, so this still exercises the real front/back double-buffer + pickup +
        //      apply-stale state machine, only the MVT-decode step is bypassed). BOTH calls run in ONE
        //      synchronous block, no CurrentBatch poll between them, so their two independent CollectGeneration
        //      bumps (SymbolTileLabelStore.cs:156,195 — BeginBuild and CompleteBuild each "always bump") coalesce
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
                var contentBefore = new SymbolLabelBatch(); harness.Lps.CopyMirrorInto(contentBefore);

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
                // payload at the SAME winner count (Codex 1c) — synthetic labels far from any coordinate the
                // real fixture decodes to, so FirstDifference below is unambiguous regardless of the fixture's
                // own content. BeginBuild+Bake+CompleteBuild run back-to-back (no CurrentBatch poll between
                // them — see the header comment's coalescing argument).
                var newLabels = new List<LabelInstance>(winnersBefore);
                for (int i = 0; i < winnersBefore; i++)
                    newLabels.Add(PointLabel(new double3(9000 + i * 10, 0, 9000), "replacement" + i, 900 + i, Tk(tile), 0.9f));
                int gen = _subsystem.StoreForTest.BeginBuild(StoreKey(tile));
                SymbolTileLabelBlock newBlock = SymbolTileLabelBlockBaker.Bake(newLabels, slotCount: 1, TileRenderOrigin.Project(tile, P));
                Assert.IsTrue(_subsystem.StoreForTest.CompleteBuild(StoreKey(tile), gen, newLabels, newBlock), "sanity: replacement block committed");

                // (a) EVENT-keying check: the frame right after the store event (before any reconcile has had a
                // chance to complete) must still be a memo HIT serving the OLD content.
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                SymbolGatherPlan pEvent = _subsystem.CurrentBatch(default, 0.0);
                harness.Lps.GatherIntoMirror(pEvent);
                Assert.AreEqual(1, harness.Lps.MirrorRebuildCount,
                    "immediately after the store event the mirror must still be a memo HIT — event-keyed (rather " +
                    "than swap-keyed) invalidation would wrongly rebuild here, before the reconcile has even completed");
                var contentAtEvent = new SymbolLabelBatch(); harness.Lps.CopyMirrorInto(contentAtEvent);
                Assert.IsNull(SymbolLabelBatchDiff.FirstDifference(contentBefore, contentAtEvent),
                    "the OLD front's content must still be served this frame — the replacement hasn't been picked up yet");

                // (b) poll until PickupCompletedReconcile's swap actually lands (ReconcileInFlightForTest observed
                // true beforehand, then the pickup call flips it false this frame), asserting the mirror stays
                // flat WHILE the worker is in flight and rebuilds EXACTLY on the swap frame — not merely
                // "eventually, within a settle window".
                bool sawInFlight = false, swappedThisFrame = false;
                for (int f = 0; f < 400 && !swappedThisFrame; f++)
                {
                    _subsystem.ReconcileLoadedTiles(loaded);
                    _subsystem.PumpBuilds();
                    bool inFlightBefore = _subsystem.ReconcileInFlightForTest;
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
                var contentAfter = new SymbolLabelBatch(); harness.Lps.CopyMirrorInto(contentAfter);
                Assert.IsNotNull(SymbolLabelBatchDiff.FirstDifference(contentBefore, contentAfter),
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
                    var got = new SymbolLabelBatch(); harness.Lps.CopyMirrorInto(got);
                    var want = new SymbolLabelBatch(); refHarness.Lps.CopyMirrorInto(want);
                    Assert.IsNull(SymbolLabelBatchDiff.FirstDifference(want, got),
                        "post-swap the held-mirror system's content must match a fresh gather over the same plan");
                }
                finally { refHarness.Dispose(); }
            }
            finally { harness.Dispose(); }
        }

        // ═══ R1 T5: a restyle (SetStyle) between two production gathers on the SAME plan object must invalidate
        //      the memo, even though the front content collapses to EMPTY. Step 2.3's `_frontSetVersion++` is
        //      defense-in-depth (3.3's `plan.WinnerCount == _mCount` predicate term is the actual crash-prevention
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
        //         at a drop site while pinned; the gather derefs its NativeArrays safely; the pin release frees it.
        //         RED-verify: revert the Release true-evict site's DisposeOrDefer to a direct Dispose → the block
        //         frees at the drop site (the "not freed" assertion fails) and the gather derefs freed memory. ═══

        private static readonly WebMercatorProjection P = new WebMercatorProjection();
        private static SymbolTileLabelStore.Key StoreKey(TileId t) => new SymbolTileLabelStore.Key("s", t);
        private static long Tk(TileId t) => Symbol.SymbolFeatureExtractor.PackTileKey(t);

        private static TextLayoutResult OneQuad(float u) => new TextLayoutResult
        {
            Quads = new List<SymbolQuad>
            {
                new SymbolQuad
                {
                    TopLeft = new float2(-6f, 18f), BottomRight = new float2(12f, 0f),
                    UvTopLeft = new float2(u, u), UvBottomRight = new float2(u + 0.2f, u + 0.2f), LineIndex = 0,
                },
            },
            BoundsMin = float2.zero, BoundsMax = new float2(18f, 18f), LineCount = 1,
        };

        private static LabelInstance PointLabel(double3 anchor, string text, int feature, long tileKey, float u)
            => new LabelInstance
            {
                AnchorRender = anchor, Placement = SymbolPlacement.Point, Layout = OneQuad(u), Paint = LabelPaint.Default,
                TextSizePx = 20f, PaddingPx = 2f, SortKey = 0f, Text = text, FeatureIndex = feature, TileKey = tileKey,
            };

        [Test]
        public void Pin_PreventsNativeUseAfterFree_InGather()
        {
            var tile = new TileId { Z = 5, X = 16, Y = 16 };
            var store = new SymbolTileLabelStore(cacheCap: 8);
            var labels = new List<LabelInstance> { PointLabel(new double3(100, 0, 200), "a", 1, Tk(tile), 0.1f) };
            int gen = store.BeginBuild(StoreKey(tile));
            SymbolTileLabelBlock block = SymbolTileLabelBlockBaker.Bake(labels, slotCount: 1, TileRenderOrigin.Project(tile, P));
            Assert.IsTrue(store.CompleteBuild(StoreKey(tile), gen, labels, block), "sanity: block committed");
            long live0 = SymbolTileLabelBlock.DebugLiveAllocCount;

            // Capture (pins the block) + run the reconciler → a result referencing the block via OrderedBlocks.
            var snapshot = new SymbolLabelSnapshot();
            store.CaptureSnapshot(snapshot);
            var reconciler = new SymbolLabelReconciler();
            var result = new SymbolLabelReconcileResult();
            reconciler.Run(snapshot, result);

            // A store drop site fires while the snapshot pins the block → DisposeOrDefer must DEFER (not free it),
            // so the gather below can still deref its NativeArrays.
            store.Release(StoreKey(tile), transferredToCache: false); // active → true evict → DisposeOrDefer(block)
            Assert.AreEqual(live0, SymbolTileLabelBlock.DebugLiveAllocCount,
                "the pinned block is NOT freed at the drop site (deferred) — reverting to a direct Dispose fails HERE");

            var harness = new LpsHarness();
            var plan = new SymbolGatherPlan();
            try
            {
                var decisions = new List<byte>(result.Output.Count);
                for (int i = 0; i < result.Output.Count; i++) decisions.Add(LabelTileCoverageFilter.Keep);
                plan.Build(result.BlockId, result.LocalIndex, result.Output, result.IsDeparting, decisions, result.OrderedBlocks, winnerSetVersion: 0);
                harness.Lps.GatherIntoMirror(plan); // derefs the (still-alive, pinned) block's NativeArrays — a freed block here is a UAF
                var gathered = new SymbolLabelBatch();
                harness.Lps.CopyMirrorInto(gathered);
                Assert.Greater(gathered.Count, 0, "the gather read the pinned block's records with no use-after-free");

                store.ReleasePins(snapshot); // last pin gone → the deferred dispose fires now
                Assert.AreEqual(live0 - 1, SymbolTileLabelBlock.DebugLiveAllocCount,
                    "releasing the last pin frees the deferred block exactly once");
            }
            finally { plan.Dispose(); harness.Dispose(); store.Clear(); }
        }

        // Owns the LPS + throwaway Unity resources for the gather (mirrors SymbolGatherParityTests.LpsHarness).
        private sealed class LpsHarness : IDisposable
        {
            public readonly LabelPlacementSystem Lps;
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
                Lps = new LabelPlacementSystem(mapCamera, _baseMaterial);
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
