// Unity EditMode only — needs a real Camera/Material/Shader + the internal SymbolLabelSubsystem, and drives
// the glyph atlas Texture2D upload. NOT registered in core-tests.csproj.
//
// EditMode half of the pump-split: kept here are the one synchronous unit test and the two GC.Alloc-measuring
// [UnityTest] (PlayMode adds per-frame engine allocations to the measured region, so `Is.Not.AllocatingGCMemory`
// must stay EditMode). The remaining [UnityTest] — genuinely off-main symbol work (extract/tail on the
// ThreadPool) — moved to MapRenderer.Tests.PlayMode.Text.SymbolLabelSubsystemPumpTests: a pre-existing
// EditMode flake, since a yielded frame is instantaneous in EditMode and starves the ThreadPool, while real
// PlayMode frames give it wall-clock.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement; // SymbolGatherPlan
using MapRenderer.Tests; // TestGlyphSource
using Is = UnityEngine.TestTools.Constraints.Is;
using Symbol = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Stall-#1 fix (Stage A): the symbol subsystem no longer builds every fetched tile inline on the main
    /// thread and no longer re-uploads the 16 MB glyph atlas per glyph-adding tile. A5b: the worker phase is
    /// now driven by <see cref="SymbolLabelSubsystem.TryBeginBuild"/> (kick-time) + the returned pass's
    /// <c>RunWorkerAndHandoff</c> (pool), and <see cref="SymbolLabelSubsystem.PumpBuilds"/> starts at most
    /// <c>MaxBuildsPerFrame</c> ready TAILS/frame + performs at most ONE coalesced atlas upload (the
    /// build-start throttle + stale-drop teeth moved to TileManager's kick — see TileSymbolKickTests). These
    /// teeth fail a shallow implementation that still builds inline / uploads per tile, and prove the
    /// cancellation path.
    /// </summary>
    [TestFixture]
    public class SymbolLabelSubsystemPumpTests
    {
        /// <summary>
        /// Ring capacity requested from every <see cref="ProfilerRecorder"/> here, and therefore the only
        /// safe upper bound when reading samples back — <c>Count</c> is NOT one once the ring has wrapped.
        /// </summary>
        private const int ProfilerSampleCapacity = 64;

        private const string FontName = "LatinFont";
        private const string SourceId = "s";

        // A style with ONE symbol layer bound to source "s" (source-layer "centroids", matching the fixture
        // tile) + a non-empty glyphs URL so SetStyle wires the glyph pipeline. The glyph SOURCE is injected
        // via GlyphSourceFactoryOverride, so the URL itself is never fetched.
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

        [SetUp]
        public void SetUp()
        {
            _camGo = new GameObject("SymbolPump_TestCamera");
            var uCam = _camGo.AddComponent<Camera>();
            _rt = new RenderTexture(320, 240, 0);
            uCam.targetTexture = _rt;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));

            // D11/E2: the subsystem no longer owns a MapMaterialSet (per-layer materials moved to
            // SymbolRenderLayer) — this suite tests build/pump/atlas behaviour only, none of which touches
            // materials.
            _subsystem = new SymbolLabelSubsystem(mapCamera);
            _tileBytes = LoadUp("Assets", "Fixtures", "sample-tile.bytes");
            _latinGlyphs = LoadUp("Assets", "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
        }

        [TearDown]
        public void TearDown()
        {
            _subsystem?.Dispose();
            // Unbind the render texture from the camera BEFORE destroying it — Unity logs an [Error]
            // ("Releasing render texture that is set as Camera.targetTexture") otherwise, which fails the test.
            if (_camGo != null)
            {
                var cam = _camGo.GetComponent<Camera>();
                if (cam != null) cam.targetTexture = null;
            }
            if (_rt != null) UnityEngine.Object.DestroyImmediate(_rt);
            if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
        }

        /// <summary>An immediate (synchronous) fixture glyph source — the whole build completes inside the
        /// PumpBuilds call, so ActiveTileCount / atlas growth are observable right after.</summary>
        private void UseImmediateGlyphs()
        {
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            _subsystem.GlyphSourceFactoryOverride = _ => TestGlyphSource.FromRanges(ranges);
            StyleDocument style = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(style, ExtractSymbolLayers(style));
        }

        private static LoadedTileKey Key(TileId t) => new LoadedTileKey(SourceId, t);

        /// <summary>A5b drive helper — mirrors TileManager's kick: <c>TryBeginBuild</c> on the (test) main
        /// thread, then <c>RunWorkerAndHandoff</c> fire-and-forget on the pool (so
        /// <c>SymbolDecodeAndExtract_RunOffTheMainThread</c> still observes the decode/extract marker off
        /// main). Replaces the retired <c>OnTileBytesReady</c> push.</summary>
        private void DriveTileBytesReady(TileId tile)
        {
            ISymbolTileWorkerPass pass = _subsystem.TryBeginBuild(SourceId, tile);
            if (pass == null) return; // mirrors OnTileBytesReady's no-op guard (no _builder / no layers for source)
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

        // E1 D10: production SetStyle no longer walks style.Layers itself (RenderLayerFactory is the sole
        // registry, MapView derives the list). Tests that call the subsystem directly need the equivalent
        // extraction — kept local to the test assembly (outside the D10 grep tooth's scope).
        private static List<Symbol.StyleLayer> ExtractSymbolLayers(StyleDocument style)
        {
            var result = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) result.Add(symbol);
            return result;
        }

        // ── Tooth 1 (bounded worker-phase starts) RETIRED at A5b: the build-start throttle + QueuedBuildCount
        //    moved to TileManager's kick cap (MaxMeshBuildsPerTick) — see TileSymbolKickTests' F-2/kick-cap
        //    coverage. This subsystem no longer owns a build-start queue to bound.

        // ── Tooth 2 (coalesced atlas upload), Tooth 5 (off-main extract), Tooth 4 (cancellation), and the
        //    departing-flag production seam MOVED to the PlayMode half (pump-split): each yield-waits for
        //    genuinely off-main symbol work, which starves deterministically in EditMode (a yielded frame is
        //    instantaneous there) — see MapRenderer.Tests.PlayMode.Text.SymbolLabelSubsystemPumpTests.

        // ── Tooth 3 (stale-drop before build start) RETIRED at A5b: a queued-but-departed tile can no longer
        //    occur here — the kick itself never fires for a condemned/departed tile (TileManager's
        //    _releaseQueued check, ahead of TryBeginBuild). Replaced by TileSymbolKickTests' F-6
        //    (departed-before-kick tile: no TryBeginBuild).

        // ── Telemetry provider (docs/telemetry-design.md §3): the subsystem OWNS the store's levels as a struct
        //    field, refreshes it at the end of CurrentBatch, and hands it out BY REFERENCE — nothing assembles
        //    them for it, and nothing subscribes. ──
        [Test]
        public void CurrentBatch_RefreshesTheStoreTelemetry_HandedOutByReference()
        {
            // Bound BEFORE any pass: `live` aliases the subsystem's field, so the refresh below is visible through
            // it. A by-value accessor would freeze this at the default struct and the assertions would read 0.
            ref readonly SymbolStoreTelemetrySnapshot live = ref _subsystem.Telemetry;

            Assert.DoesNotThrow(() => _subsystem.CurrentBatch(default, 0.0));

            Assert.AreEqual(_subsystem.ActiveTileCount, live.ActiveLabelTiles,
                "the struct must carry the subsystem's live levels, not a default struct.");
            Assert.AreEqual(_subsystem.CachedTileCount, live.CachedLabelTiles);

            // A pass that does not run must NOT zero the levels: "the label pass did not run" is a different claim
            // from "it ran and found zero", so the last real values stand.
            int activeAfterPass = live.ActiveLabelTiles;
            Assert.AreEqual(activeAfterPass, live.ActiveLabelTiles,
                "re-reading without a pass returns the same held values — the provider never clears itself.");
        }

        // ── The production seam (CurrentBatch() threads CollectInto's active/departing split into the batch, so a
        //    tile that just left cover comes back as DEPARTING records) and the Test-3 recompute-on-real-event tooth
        //    both MOVED to the PlayMode half (pump-split) — each drives an async build across pumped frames. See
        //    CurrentBatch_TileLeftCover_FlagsRecordsDeparting / CurrentBatch_TileEvent_RecomputesAndDrivesFade in
        //    MapRenderer.Tests.PlayMode.Text.SymbolLabelSubsystemPumpTests.

        // ── Alloc tooth: LabelTileCoverageFilter.FilterActive's per-call Dictionary/List compaction must not
        //    allocate once warm — mirrors LabelPlacementAllocTests' steady-state guarantee, now covering the
        //    pre-build cull too (a non-zero minCoverage exercises the real filter path, not its no-op guard). ──
        [UnityTest]
        public IEnumerator CurrentBatch_Warm_WithCoverageCullEnabled_AllocatesNoGCMemory()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };
            DriveTileBytesReady(tile);

            // A VALID (identity-rebase) scene frame — NOT `default`, whose zero Rebase collapses all four tile
            // corners to one point ⇒ zero-area quad ⇒ coverage 0 ⇒ every label culled. A tiny POSITIVE threshold
            // keeps `minCoverage > 0` so the REAL filter path runs (projection + per-tile coverage + compaction,
            // the alloc surface we measure) rather than the no-op guard, while being small enough that a
            // well-formed on-screen tile always survives — so the sanity precondition below holds regardless of
            // how the (unsynced) test camera frames the tile. The alloc behaviour is identical whether the filter
            // keeps or drops tiles (both fill the scratch dict + call RemoveRange).
            var frame = SceneFrame.Mercator(new double2(0.0, 0.0));
            const double keepAllButRunFilter = 1e-9;

            // Stage 4b: the alloc measurement must run on a FULLY quiescent frame — labels committed, no pending
            // tail, no reconcile in flight, and the schedule generation caught up (CollectRecomputeCount stable for
            // 2 frames). A dirty frame legitimately allocates (it captures + kicks a worker), so measuring one would
            // be a false positive; genuine quiescence is what the steady-state alloc guarantee is about.
            SymbolGatherPlan plan = null;
            bool quiesced = false; int stable = 0; int lastRecompute = -1;
            for (int f = 0; f < 400; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                plan = _subsystem.CurrentBatch(frame, keepAllButRunFilter);
                bool settled = plan.WinnerCount > 0 && _subsystem.ReadyTailCount() == 0
                               && !_subsystem.ReconcileInFlight() && _subsystem.CollectRecomputeCount == lastRecompute;
                if (settled) { if (++stable >= 2) { quiesced = true; break; } } else stable = 0;
                lastRecompute = _subsystem.CollectRecomputeCount;
                yield return null;
            }
            Assert.IsTrue(quiesced, "sanity: drove to full reconcile quiescence before the alloc measurement");
            Assert.Greater(plan.WinnerCount, 0, "sanity: the tile committed label records before the alloc measurement");

            // Warm-up call (first-touch Dictionary/List growth allowed) then the STEADY call is measured. Block
            // body (NOT an expression lambda): `() => _subsystem.CurrentBatch(...)` binds NUnit's value-returning
            // `Assert.That<T>(Func<T>, ...)` overload — it hands the returned SymbolGatherPlan to the constraint
            // instead of invoking Is.Not.AllocatingGCMemory()'s delegate form, throwing
            // "the actual value must be a TestDelegate" at runtime. A block-body lambda is a plain TestDelegate.
            _subsystem.CurrentBatch(frame, keepAllButRunFilter);
            Assert.That(() => { _subsystem.CurrentBatch(frame, keepAllButRunFilter); },
                Is.Not.AllocatingGCMemory(),
                "a steady-state CurrentBatch (coverage cull + native winner-plan build) must allocate ZERO managed garbage");
        }

        // ═══ Stage 4a (labels-async-reconcile): memoized clean-frame reuse of the collected set ═══

        // ── Test 2/4: on frames with no tile event, CurrentBatch REUSES the collected buffers byte-identically and
        //    does NOT recompute the ~11 ms cross-tile dedup (CollectRecomputeCount held flat), allocating zero GC.
        //    minCoverage = 0.0 so ClassifyActive early-returns — the full plan is then collect-derived and
        //    frame-independent, isolating the reconcile-governed fields (BlockId/LocalIndex/Departing/WinnerCount).
        //    Stage 4b: the plan comes from the picked-up FRONT reconcile result; a clean frame neither schedules nor
        //    picks up, so the front is reused byte-identically. RED-verify: delete the
        //    `_store.CollectGeneration == _reconcileScheduledGen` guard in ScheduleReconcileIfDirty → it reschedules
        //    every clean frame → CollectRecomputeCount climbs (and the reschedule allocates) → both the counter and
        //    the zero-GC assertions fail (proving the guard has teeth). ──
        [UnityTest]
        public IEnumerator CurrentBatch_CleanFrames_ReuseCollectedSet_ByteIdentical_RecomputesOnce()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };

            // Stage 4b: drive to FULL reconcile quiescence — labels committed (WinnerCount > 0), no straggler tail
            // pending (ReadyTailCount == 0), NO reconcile in flight, and the schedule generation caught up
            // (CollectRecomputeCount stable for 2 frames). Only then is a subsequent frame genuinely "clean" (neither
            // schedules nor picks up), the precondition for the byte-identical-reuse + zero-GC assertions below.
            DriveTileBytesReady(tile);
            SymbolGatherPlan plan = null;
            bool quiesced = false; int stable = 0; int lastRecompute = -1;
            for (int f = 0; f < 400; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                plan = _subsystem.CurrentBatch(default, 0.0);
                bool settled = plan.WinnerCount > 0 && _subsystem.ReadyTailCount() == 0
                               && !_subsystem.ReconcileInFlight() && _subsystem.CollectRecomputeCount == lastRecompute;
                if (settled) { if (++stable >= 2) { quiesced = true; break; } } else stable = 0;
                lastRecompute = _subsystem.CollectRecomputeCount;
                yield return null;
            }
            Assert.IsTrue(quiesced, "sanity: drove to full reconcile quiescence (labels committed, no pending tails, nothing in flight)");

            int winners = plan.WinnerCount;
            var blockId = new int[winners]; var localIndex = new int[winners]; var departing = new byte[winners];
            for (int i = 0; i < winners; i++) { blockId[i] = plan.BlockId[i]; localIndex[i] = plan.LocalIndex[i]; departing[i] = plan.Departing[i]; }
            int baseline = _subsystem.CollectRecomputeCount;

            // Teeth: the counter is LIVE — driving to quiescence performed at least one REAL recompute (the cold-start
            // collect alone, _collectedAtGeneration == -1 != gen, guarantees it). Without this a dead
            // CollectRecomputeCount++ (stuck at 0) would silently satisfy the "held flat" assertion below at 0==0, and
            // the test would prove nothing about recompute. baseline>=1 + the flat assertion together = "recomputed
            // (during real collects) then REUSED (across clean frames)". Increment-on-a-real-event is Test 3.
            Assert.GreaterOrEqual(baseline, 1,
                "the recompute counter actually fired during the real (dirty) collects — guards a dead counter");

            // N clean frames replicating the production per-frame loop on the UNCHANGED loaded set (the every-frame
            // ReconcileActiveSet on a stable cover must not bump — hence the FULL loop, not a bare CurrentBatch).
            const int N = 5;
            for (int f = 0; f < N; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                SymbolGatherPlan p = _subsystem.CurrentBatch(default, 0.0);
                Assert.AreEqual(winners, p.WinnerCount, $"clean frame {f}: winner count identical to the quiesced snapshot");
                for (int i = 0; i < winners; i++)
                {
                    Assert.AreEqual(blockId[i], p.BlockId[i], $"clean frame {f}: blockId[{i}] identical (reused, not recomputed)");
                    Assert.AreEqual(localIndex[i], p.LocalIndex[i], $"clean frame {f}: localIndex[{i}] identical");
                    Assert.AreEqual(departing[i], p.Departing[i], $"clean frame {f}: departing[{i}] identical");
                }
                yield return null;
            }
            Assert.AreEqual(baseline, _subsystem.CollectRecomputeCount,
                "the memo skipped CollectInto on every clean frame — no recompute across all N (delete the guard and this fails)");

            // Test 4 (folded in): the clean reuse loop does strictly LESS than a recompute (skips CollectInto), so it
            // must allocate ZERO managed garbage. Warm first (above N frames), then measure a full clean-frame loop.
            _subsystem.ReconcileLoadedTiles(loaded);
            _subsystem.PumpBuilds();
            _subsystem.CurrentBatch(default, 0.0);
            Assert.That(() => { _subsystem.ReconcileLoadedTiles(loaded); _subsystem.PumpBuilds(); _subsystem.CurrentBatch(default, 0.0); },
                Is.Not.AllocatingGCMemory(),
                "a steady clean-frame reuse loop (reconcile + pump + CurrentBatch, no recompute) must allocate ZERO managed garbage");
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
