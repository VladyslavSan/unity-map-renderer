// Text/SymbolTileStoreTests.cs — the symbol subsystem pump (worker/tail split), its work scheduler, SymbolTileStore's warm/evict lifecycle, and a text-quad-layout allocation regression pin.
//
// The three pump-related fixtures first (subsystem pump, work scheduler, tail pump), then the store lifecycle fixture, then the standalone allocation regression pin.
//
// Contents:
//   SymbolSubsystemPumpTests           — Stall-#1 fix (Stage A): the symbol subsystem no longer builds every fetched tile inline on the main thread and no longer re-uploads the 16 MB glyph atlas per glyph-adding tile.
//   SymbolSubsystemWorkSchedulerTests  — ReconcileDispatch_* proves T2 (the WebGL-only permanent placement wedge at :899): under Inline the pickup lands ONE CurrentBatch call later than the schedule (PickupCompletedReconcile runs BEFORE ScheduleReconcileIfDirty inside CurrentBatch), so the tooth…
//   SymbolTailPumpTests                — Epic A / A5a-A5b: the acceptance teeth for the worker-phase / tail split — the worker phase (TryBeginBuild's returned pass) stops after the pool-side extract and hands a ready tail (via the A5b pool→main handoff) to PumpBuilds' budgeted tail-start loop…
//   SymbolTileStoreTests               — The symbol-lifecycle fix (zoom-out-then-in "no symbols" bug): SymbolTileStore keeps a released-to-cache tile's symbols WARM and restores them on a prepared-cache hit (which does not re-fetch), while a truly-evicted tile drops them.
//   TextQuadLayoutAllocTests           — S19 Slice 4: the layout hot path (steady, single-line, no-wrap) must allocate ZERO managed garbage once the caller's output List capacity has stabilized -- see TextQuadLayout's class doc for why (an in-place List index write per emitted quad, no auxiliary…

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
using MapRenderer.Unity.View;
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
using System.Threading;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Tests.Text.Placement; // TestSymbolTileBuffer
using System.Reflection;
using System.Text.RegularExpressions;
using MapRenderer.Core.Style.Symbol;
using StyleLayer = MapRenderer.Core.Style.StyleLayer;
using TextAnchor = MapRenderer.Core.Text.TextAnchor;


namespace MapRenderer.Tests.Text
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolSubsystemPumpTests — no inline main-thread build, no per-glyph atlas re-upload
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stall-#1 fix (Stage A): the symbol subsystem no longer builds every fetched tile inline on the main
    /// thread and no longer re-uploads the 16 MB glyph atlas per glyph-adding tile. A5b: the worker phase is
    /// now driven by <see cref="SymbolSubsystem.TryBeginBuild"/> (kick-time) + the returned pass's
    /// <c>RunWorkerAndHandoff</c> (pool), and <see cref="SymbolSubsystem.PumpBuilds"/> starts at most
    /// <c>MaxBuildsPerFrame</c> ready TAILS/frame + performs at most ONE coalesced atlas upload (the
    /// build-start throttle + stale-drop teeth moved to TileManager's kick — see TileSymbolKickTests). These
    /// teeth fail a shallow implementation that still builds inline / uploads per tile, and prove the
    /// cancellation path.
    /// </summary>
    [TestFixture]
    public class SymbolSubsystemPumpTests
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
        private SymbolSubsystem _subsystem;
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
            _subsystem = new SymbolSubsystem(mapCamera);
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
        //    instantaneous there) — see MapRenderer.Tests.PlayMode.Text.SymbolSubsystemPumpTests.

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

            Assert.AreEqual(_subsystem.ActiveTileCount, live.ActiveSymbolTiles,
                "the struct must carry the subsystem's live levels, not a default struct.");
            Assert.AreEqual(_subsystem.CachedTileCount, live.CachedSymbolTiles);

            // A pass that does not run must NOT zero the levels: "the symbol pass did not run" is a different claim
            // from "it ran and found zero", so the last real values stand.
            int activeAfterPass = live.ActiveSymbolTiles;
            Assert.AreEqual(activeAfterPass, live.ActiveSymbolTiles,
                "re-reading without a pass returns the same held values — the provider never clears itself.");
        }

        // ── The production seam (CurrentBatch() threads CollectInto's active/departing split into the batch, so a
        //    tile that just left cover comes back as DEPARTING records) and the Test-3 recompute-on-real-event tooth
        //    both MOVED to the PlayMode half (pump-split) — each drives an async build across pumped frames. See
        //    CurrentBatch_TileLeftCover_FlagsRecordsDeparting / CurrentBatch_TileEvent_RecomputesAndDrivesFade in
        //    MapRenderer.Tests.PlayMode.Text.SymbolSubsystemPumpTests.

        // ── Alloc tooth: SymbolTileCoverageFilter.FilterActive's per-call Dictionary/List compaction must not
        //    allocate once warm — mirrors SymbolPlacementAllocTests' steady-state guarantee, now covering the
        //    pre-build cull too (a non-zero minCoverage exercises the real filter path, not its no-op guard). ──
        [UnityTest]
        public IEnumerator CurrentBatch_Warm_WithCoverageCullEnabled_AllocatesNoGCMemory()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };
            DriveTileBytesReady(tile);

            // A VALID (identity-rebase) scene frame — NOT `default`, whose zero Rebase collapses all four tile
            // corners to one point ⇒ zero-area quad ⇒ coverage 0 ⇒ every symbol culled. A tiny POSITIVE threshold
            // keeps `minCoverage > 0` so the REAL filter path runs (projection + per-tile coverage + compaction,
            // the alloc surface we measure) rather than the no-op guard, while being small enough that a
            // well-formed on-screen tile always survives — so the sanity precondition below holds regardless of
            // how the (unsynced) test camera frames the tile. The alloc behaviour is identical whether the filter
            // keeps or drops tiles (both fill the scratch dict + call RemoveRange).
            var frame = SceneFrame.Mercator(new double2(0.0, 0.0));
            const double keepAllButRunFilter = 1e-9;

            // Stage 4b: the alloc measurement must run on a FULLY quiescent frame — symbols committed, no pending
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

        // ═══ Stage 4a (symbols-async-reconcile): memoized clean-frame reuse of the collected set ═══

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

            // Stage 4b: drive to FULL reconcile quiescence — symbols committed (WinnerCount > 0), no straggler tail
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolSubsystemWorkSchedulerTests — the CurrentBatch pickup-vs-schedule ordering wedge
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolSubsystemWorkSchedulerTests
    {
        private const string SourceId = "s";
        private const string FontName = "LatinFont";
        private static readonly TileId Tile0 = new TileId { Z = 3, X = 0, Y = 0 };
        private static readonly WebMercatorProjection Projection = new WebMercatorProjection();

        // Icon+text style (mirrors SymbolParkedRedecodeTests): a root `sprite` URL gives the parked-drain
        // tooth something to park ON, matching production's actual park trigger.
        private static readonly string StyleJson = @"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'sprite': 'https://example.invalid/sprite',
            'layers': [
                { 'id':'labels', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':16, 'text-font':['LatinFont'], 'icon-image':'marker' } }
            ]
        }".Replace('\'', '"');

        // Text-only, no `sprite` key (mirrors SymbolReconcileAsyncTests' style): the reconcile tooth commits
        // its tile directly through the store and never touches the sprite/glyph fetch, so this avoids
        // FetchSpriteSheetAsync ever constructing a REAL production ISpriteSource against the invalid URL.
        private static readonly string TextOnlyStyleJson = @"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'layers': [
                { 'id':'labels', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'{NAME}', 'text-size':16, 'text-font':['LatinFont'] } }
            ]
        }".Replace('\'', '"');

        private GameObject _camGo;
        private RenderTexture _rt;
        private MapCamera _mapCamera;
        private SymbolSubsystem _subsystem;
        private byte[] _tileBytes;
        private byte[] _latinGlyphs;
        // SpritesSettled's deadline clock (mirrors SymbolParkedRedecodeTests' _simulatedNow) — frozen at
        // construction, then advanced PAST SpriteFetchDeadlineSeconds to trip the deadline fallback
        // deterministically, with no dependence on a real UniTask continuation ever actually resuming.
        private double _simulatedNow;

        [SetUp]
        public void SetUp()
        {
            _camGo = new GameObject("SymbolSubsystemWorkScheduler_TestCamera");
            var uCam = _camGo.AddComponent<Camera>();
            _rt = new RenderTexture(320, 240, 0);
            uCam.targetTexture = _rt;
            _mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));

            _simulatedNow = 1000.0; // arbitrary non-zero start; advanced explicitly where needed
            _tileBytes   = ReadBytes("Fixtures", "sample-tile.bytes");
            _latinGlyphs = ReadBytes("Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes");
        }

        [TearDown]
        public void TearDown()
        {
            _subsystem?.Dispose();
            if (_camGo != null)
            {
                var cam = _camGo.GetComponent<Camera>();
                if (cam != null) cam.targetTexture = null;
            }
            if (_rt != null) UnityEngine.Object.DestroyImmediate(_rt);
            if (_camGo != null) UnityEngine.Object.DestroyImmediate(_camGo);
        }

        private static byte[] ReadBytes(params string[] rel)
        {
            var parts = new string[rel.Length + 1];
            parts[0] = Application.dataPath;
            Array.Copy(rel, 0, parts, 1, rel.Length);
            return File.ReadAllBytes(Path.Combine(parts));
        }

        private static List<Symbol.StyleLayer> ExtractSymbolLayers(StyleDocument style)
        {
            var result = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) result.Add(symbol);
            return result;
        }

        private SymbolSubsystem NewSubsystem(Func<CancellationToken, UniTask<SpriteResponse>> spriteFetch)
        {
            var subsystem = new SymbolSubsystem(_mapCamera) { NowSecondsOverride = () => _simulatedNow };
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            subsystem.GlyphSourceFactoryOverride  = _ => TestGlyphSource.FromRanges(ranges);
            subsystem.SpriteSourceFactoryOverride = _ => new GatedSpriteSource(spriteFetch);
            StyleDocument style = StyleParser.Parse(StyleJson);
            subsystem.SetStyle(style, ExtractSymbolLayers(style));
            return subsystem;
        }

        // ── :643 — the parked-build drain ─────────────────────────────────────────────────────────────

        /// <summary>T1: the WebGL-only unrecoverable decode leak. <c>PumpBuilds</c>' parked-queue drain
        /// dispatches through <see cref="SymbolSubsystem.WorkScheduler"/>
        /// — under <see cref="InlineWorkScheduler"/> the dispatched body (the ONLY release for the decode
        /// reference the park took) runs synchronously, on the calling thread, before <c>PumpBuilds</c>
        /// returns.
        ///
        /// <para><b>Why the release itself, not an empty queue.</b> Under Inline the entry is already
        /// dequeued from <c>_pendingSpriteQueue</c> by the time <c>Schedule</c> is even called — an
        /// empty-queue assertion would pass whether or not the dispatched body (and its release) ever ran.
        /// <see cref="LeaseProbeDecoder.DisposedCount"/> is read synchronously, with NO yield between it and
        /// <c>PumpBuilds()</c> returning, which is exactly what makes it discriminate: under the (reverted)
        /// <c>UniTask.RunOnThreadPool</c> shape the pool thread has not run yet at that point even on
        /// desktop, so this reading is false-negative-proof against "ran eventually on the pool" — it can
        /// only pass if the body ran INSIDE the call.</para>
        ///
        /// <para><b>RED injection:</b> revert the parked-drain dispatch in <c>PumpBuilds</c> to
        /// <c>UniTask.RunOnThreadPool(…).Forget()</c> — <c>spy.ScheduleCount</c> stays 0 (never reaches the
        /// injected scheduler at all) and <c>probe.DisposedCount</c> stays 0 immediately after
        /// <c>PumpBuilds()</c> returns (the pool body has not run synchronously).</para></summary>
        [Test]
        public void ParkedDrain_DispatchesThroughWorkScheduler_AndReleasesTheDecodeSynchronously_UnderInline()
        {
            int caller = Environment.CurrentManagedThreadId;
            var gate  = new UniTaskCompletionSource<SpriteResponse>(); // held pending for the whole test
            var probe = new LeaseProbeDecoder();
            _subsystem = NewSubsystem(_ => gate.Task); // sprite fetch gated open -> TryBeginBuild parks

            ISymbolTileWorkerPass pass = _subsystem.TryBeginBuild(SourceId, Tile0);
            Assert.IsNotNull(pass, "sanity: the style names this source, so the kick must produce a pass");

            var lease = new SharedDisposable<IDecodedTile>(probe.Decode(Tile0, _tileBytes));
            pass.RunWorkerAndHandoff(lease); // parks: acquires its OWN reference, enqueues
            lease.Release(); // the kick's own reference — mirrors production's kick lambda `finally`

            Assert.AreEqual(1, _subsystem.PendingSpriteCount(),
                "PRECONDITION: a build must actually be sitting in the pending-sprite queue.");
            Assert.AreEqual(0, probe.DisposedCount,
                "PRECONDITION: the parked reference is the only one left, and it is still ALIVE.");

            // Trip SpritesSettled via its DEADLINE fallback rather than resolving `gate` — deterministic and
            // synchronous, with no dependence on a real UniTask continuation ever actually resuming (the
            // gated fetch stays pending for the rest of the test).
            _simulatedNow += SymbolSubsystem.SpriteFetchDeadlineSeconds + 1.0;

            var spy = new RecordingWorkScheduler(new InlineWorkScheduler());
            _subsystem.WorkScheduler = spy;

            _subsystem.PumpBuilds(); // drains the parked queue -> dispatches through spy -> Inline runs body HERE

            Assert.AreEqual(1, spy.ScheduleCount,
                "the parked drain must dispatch through the injected scheduler EXACTLY once.");
            Assert.AreEqual(1, spy.BodyThreadIds.Count, "the dispatched body must actually have run once.");
            Assert.AreEqual(caller, spy.BodyThreadIds[0],
                "under Inline the body must run on the CALLING thread, synchronously inside PumpBuilds.");

            Assert.AreEqual(1, probe.DisposedCount,
                "the decode reference the park took must be RELEASED — read synchronously, with no yield, " +
                "immediately after PumpBuilds() returns. This is the leak T1 exists to close: the dispatched " +
                "body's `finally` is the ONLY release for this reference, so a scheduler that failed to run " +
                "it (or ran it later, off this call) leaks it exactly as UniTask.RunOnThreadPool does on web.");
            Assert.AreEqual(0, probe.UnbalancedCount, "…exactly once — not a double release.");
            Assert.AreEqual(0, _subsystem.PendingSpriteCount(), "sanity: the queue drained.");

            gate.TrySetResult(new SpriteResponse { HasData = false }); // tidy: never leave a gate hanging
        }

        // ── :899 — the cross-tile reconcile ───────────────────────────────────────────────────────────

        /// <summary>T2: the WebGL-only permanent placement wedge. <c>ScheduleReconcileIfDirty</c>
        /// dispatches through <see cref="SymbolSubsystem.WorkScheduler"/> —
        /// under Inline the reconcile body runs synchronously, on the calling thread, inside the SAME
        /// <c>CurrentBatch</c> call that scheduled it.
        ///
        /// <para><b>Why two <c>CurrentBatch</c> calls.</b> <c>CurrentBatch</c> runs
        /// <c>PickupCompletedReconcile</c> BEFORE <c>ScheduleReconcileIfDirty</c>, so even though the Inline
        /// dispatch completes synchronously, the just-scheduled reconcile is only picked up on the NEXT
        /// call — <c>_reconcileInFlight</c> stays true across the frame that scheduled it. Asserting only one
        /// call would either miss the in-flight window entirely or (if asserted wrong) demand a same-frame
        /// pickup that would be a behaviour change outside this stage's scope.</para>
        ///
        /// <para><b>RED injection:</b> revert <c>ScheduleReconcileIfDirty</c>'s dispatch to
        /// <c>UniTask.RunOnThreadPool(…).Preserve()</c> — <c>spy.ScheduleCount</c> stays 0 and
        /// <c>Reconciler().LastRunThreadId</c> is never the calling thread (it runs on a pool thread, later,
        /// if at all).</para></summary>
        [Test]
        public void ReconcileDispatch_ThroughWorkScheduler_ClearsInFlight_AndPicksUpOneFrameLater_UnderInline()
        {
            int caller = Environment.CurrentManagedThreadId;
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            _subsystem = new SymbolSubsystem(_mapCamera);
            _subsystem.GlyphSourceFactoryOverride = _ => TestGlyphSource.FromRanges(ranges);
            StyleDocument style = StyleParser.Parse(TextOnlyStyleJson);
            _subsystem.SetStyle(style, ExtractSymbolLayers(style));

            // Commit a tile directly through the store (bypasses MVT decode / the async build tail entirely
            // — same construction SymbolReconcileAsyncTests' Memo_RealFrontSwap_Invalidates uses), which is
            // enough to make CollectGeneration dirty and drive ScheduleReconcileIfDirty on the next
            // CurrentBatch. Only the RECONCILE dispatch is under test here, not the build pipeline.
            var buffer = new SymbolTileBuffer();
            AddPointSymbol(buffer, new double3(100, 0, 200), "a", 1, SymbolTileKey.Pack(Tile0), 0.1f);
            var key = new SymbolTileStore.Key(SourceId, Tile0);
            int gen = _subsystem.Store().BeginBuild(key);
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(
                buffer, slotCount: 1, TileRenderOrigin.Project(Tile0, Projection));
            Assert.IsTrue(_subsystem.Store().CompleteBuild(key, gen, block), "sanity: the block committed");

            var loaded = new List<LoadedTileKey> { new LoadedTileKey(SourceId, Tile0) };
            _subsystem.ReconcileLoadedTiles(loaded);

            var spy = new RecordingWorkScheduler(new InlineWorkScheduler());
            _subsystem.WorkScheduler = spy;

            // Frame 1: Pickup is a no-op (nothing in flight yet); Schedule sees the dirty generation and
            // dispatches — under Inline, synchronously, inside this very call.
            SymbolGatherPlan afterSchedule = _subsystem.CurrentBatch(default, 0.0);

            Assert.AreEqual(1, spy.ScheduleCount, "the reconcile must dispatch through the injected scheduler.");
            Assert.AreEqual(1, spy.BodyThreadIds.Count, "the dispatched body must actually have run once.");
            Assert.AreEqual(caller, spy.BodyThreadIds[0],
                "under Inline the reconcile body must run on the CALLING thread, synchronously inside CurrentBatch.");
            Assert.AreEqual(caller, _subsystem.Reconciler().LastRunThreadId,
                "sanity: SymbolReconciler.Run itself observed the calling thread too.");
            Assert.IsTrue(_subsystem.ReconcileInFlight(),
                "frame 1: PickupCompletedReconcile runs BEFORE ScheduleReconcileIfDirty inside CurrentBatch, " +
                "so the just-completed (already-terminal, under Inline) reconcile is still flagged in-flight " +
                "until the NEXT call's pickup — this is NOT a bug this stage introduces.");
            Assert.AreEqual(0, afterSchedule.WinnerCount,
                "sanity: nothing has been picked up into the front result yet.");

            // Frame 2: Pickup now sees the (already terminal) handle and applies it.
            SymbolGatherPlan afterPickup = _subsystem.CurrentBatch(default, 0.0);
            Assert.IsFalse(_subsystem.ReconcileInFlight(),
                "frame 2: the pickup must have applied the Inline-completed reconcile and cleared in-flight.");
            Assert.AreEqual(1, afterPickup.WinnerCount,
                "the committed symbol must have been picked up into the front result — proves the swap " +
                "actually applied, not merely that the flag cleared.");
        }

        // ── Glyph-fetch hoist (T5c) ────────────────────────────────────────────────────────────────

        // A literal (non-templated) text-field: TextFieldResolver returns a template VERBATIM whenever it
        // contains no '{' — so every one of the fixture's ~248 "centroids" features resolves to this SAME
        // mixed-direction string, regardless of its own NAME property. 'A' (strong LTR) + U+0628 Arabic beh
        // (strong RTL) is the single-run-bidi combination CodepointTextShaper rejects
        // (NotSupportedException) — the same trigger StyledSymbolTileBuilderTests' mixed-direction tooth
        // uses. Existing only to make T5c's shape-loop-ran/-didn't-run distinction observable (see below).
        // This is a BORROWED precondition, not a guarantee — see Precondition_ShapingTheMixedDirectionTextStillThrows.
        private const string MixedDirectionText = "Aب";
        private static readonly string MixedDirectionStyleJson = (@"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'layers': [
                { 'id':'labels', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'" + MixedDirectionText + @"', 'text-size':16, 'text-font':['LatinFont'] } }
            ]
        }").Replace('\'', '"');

        /// <summary>T5c: the genuinely NEW risk the hoist introduces — <c>RunTailAsync</c> now suspends at a
        /// NEW position (the build-wide glyph-range ensure step, BEFORE the shape loop) and needs its own
        /// cancellation guard there. Drives a build past its worker step into the tail, where a GATED glyph
        /// source parks it in the ensure step; cancels the build's scope (a restyle, mirroring production);
        /// then releases the gate with a NORMAL (non-cancelled) response, so the suspended
        /// <c>EnsureGlyphRangesAsync</c> await returns CLEANLY and the pre-loop
        /// <c>tail.Ct.ThrowIfCancellationRequested()</c> is the only thing standing between the already-
        /// cancelled token and the shape loop.
        ///
        /// <para><b>Why not release the gate as cancelled (the naive, and FIRST-WRITTEN, version of this
        /// tooth).</b> Doing so makes the awaited call ITSELF throw the <see cref="OperationCanceledException"/>
        /// — control never reaches the pre-loop check's line at all, so its presence or absence is invisible.
        /// Worse: even releasing normally, <c>RunTailAsync</c>'s OLDER, pre-existing TRAILING ct check (after
        /// the shape loop, before the commit) is a second, redundant safety net for the exact same outcome
        /// ("nothing committed, <c>CancelledBuildCount</c> bumped") — so asserting only the FINAL outcome
        /// cannot tell "the pre-loop guard fired" apart from "the shape loop ran to completion and the
        /// TRAILING guard caught it instead". Both naive designs are RED-VERIFIED VACUOUS below; this is why
        /// <see cref="MixedDirectionStyleJson"/> exists: it makes "did the shape loop actually run" itself
        /// observable. Shape's per-symbol <c>catch (Exception ex) when (!(ex is OperationCanceledException) &amp;&amp;
        /// !ct.IsCancellationRequested)</c> filter is FALSE whenever <c>ct</c> is already cancelled — so if the
        /// shape loop runs at all here, the mixed-direction throw escapes UNCAUGHT into <c>RunTailAsync</c>'s
        /// generic <c>catch (Exception ex)</c> branch instead of its <c>catch (OperationCanceledException)</c>
        /// branch, and <see cref="SymbolSubsystem.CancelledBuildCount"/> stays at 0 instead of becoming 1.
        /// That is the discriminator this tooth actually asserts.</para>
        ///
        /// <para>The decode-disposal assertion is a bundled sanity check, not evidence for the guard itself
        /// — the decode's one and only release already happened at the worker step
        /// (<see cref="TileSymbolLayerProcessor.ProcessOnWorker"/>'s caller releases it right after handing
        /// off), well before the tail's suspension. It just confirms this restructure did not somehow
        /// disturb that unrelated lifetime.</para>
        ///
        /// <para><b>RED injection:</b> delete the <c>tail.Ct.ThrowIfCancellationRequested()</c>
        /// <c>RunTailAsync</c> calls right after <c>await _builder.EnsureGlyphRangesAsync(...)</c> — the
        /// shape loop then runs on an already-cancelled token, the mixed-direction throw escapes uncaught,
        /// and <c>CancelledBuildCount</c> stays 0 (a warning is logged instead).</para>
        ///
        /// <para><b>Borrowed precondition.</b> This tooth's discriminating power depends on shaping
        /// <see cref="MixedDirectionText"/> still throwing today — a production LIMITATION, not a guarantee.
        /// If mixed-direction shaping is ever implemented, both unwind paths converge on the same
        /// <c>CancelledBuildCount</c> outcome again and this tooth silently reverts to exactly the vacuity it
        /// was rewritten to fix. <see cref="Precondition_ShapingTheMixedDirectionTextStillThrows"/> pins that
        /// precondition directly, so a future implementer hits a loud, named failure pointing HERE instead of
        /// a green suite hiding a hollow tooth.</para></summary>
        [Test]
        public void CancelDuringGlyphPrepare_UnwindsBeforeShapeOrCommit_ReleasesTheDecodeExactlyOnce()
        {
            var gate = new UniTaskCompletionSource<GlyphRangeResponse>();
            _subsystem = new SymbolSubsystem(_mapCamera) { NowSecondsOverride = () => _simulatedNow };
            _subsystem.GlyphSourceFactoryOverride = _ => new TestGlyphSource((fontStack, rangeStart, ct) => gate.Task);
            // No 'sprite' key -> settles immediately, no park.
            StyleDocument style = StyleParser.Parse(MixedDirectionStyleJson);
            _subsystem.SetStyle(style, ExtractSymbolLayers(style));

            var key = new SymbolTileStore.Key(SourceId, Tile0);
            var probe = new LeaseProbeDecoder();
            ISymbolTileWorkerPass pass = _subsystem.TryBeginBuild(SourceId, Tile0);
            Assert.IsNotNull(pass, "sanity: the style names this source, so the kick must produce a pass");

            var lease = new SharedDisposable<IDecodedTile>(probe.Decode(Tile0, _tileBytes));
            pass.RunWorkerAndHandoff(lease); // worker step: extract, enqueue the ready tail — decode's last use
            lease.Release(); // the kick's own reference — mirrors production's kick lambda `finally`
            Assert.AreEqual(1, probe.DisposedCount, "PRECONDITION: the worker step already released the decode.");

            _subsystem.PumpBuilds(); // drains the handoff, starts RunTailAsync — parks on the gated ensure step
            Assert.AreEqual(0, _subsystem.CancelledBuildCount, "not cancelled yet (parked on the gated glyph fetch).");
            Assert.IsNull(_subsystem.Store().DebugBlockFor(key), "PRECONDITION: nothing committed while parked.");

            // Restyle mid-tail: cancels the in-flight build's token scope (does not by itself wake the await).
            StyleDocument restyle = StyleParser.Parse(MixedDirectionStyleJson);
            _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));
            // Release the gate NORMALLY (not cancelled) — EnsureGlyphRangesAsync returns cleanly, so the ONLY
            // thing that can still stop this (already-cancelled) build before the shape loop is the pre-loop
            // ct check under test.
            gate.TrySetResult(new GlyphRangeResponse(_latinGlyphs));

            Assert.AreEqual(1, _subsystem.CancelledBuildCount,
                "the pre-loop ct check must fire BEFORE the shape loop — if the shape loop ran instead, the " +
                "mixed-direction throw would escape into the generic catch and this would stay 0.");
            Assert.IsNull(_subsystem.Store().DebugBlockFor(key), "a cancelled ensure step must commit nothing.");
            Assert.AreEqual(1, probe.DisposedCount, "…still exactly once — the tail's cancel path touches no decode.");
            Assert.AreEqual(0, probe.UnbalancedCount, "no double release / leak on the cancel path.");
        }

        /// <summary>Self-announcing precondition for
        /// <see cref="CancelDuringGlyphPrepare_UnwindsBeforeShapeOrCommit_ReleasesTheDecodeExactlyOnce"/>:
        /// pins that shaping <see cref="MixedDirectionText"/> still throws (today: CodepointTextShaper's
        /// single-run-bidi rejection) — the borrowed production limitation that tooth's discriminator
        /// depends on. If this ever goes red, mixed-direction shaping has been implemented and the OTHER
        /// tooth has silently gone hollow (both its unwind paths would then converge on the same
        /// <c>CancelledBuildCount</c> outcome) — it needs a new discriminator, not a re-run.</summary>
        [Test]
        public void Precondition_ShapingTheMixedDirectionTextStillThrows()
        {
            using var manager = new GlyphManager(TestGlyphSource.FromRanges(new Dictionary<(string, int), byte[]>()));
            var builder = new StyledSymbolTileBuilder(manager);
            var symbols = new List<Symbol.SymbolFeature>
            {
                new Symbol.SymbolFeature
                {
                    Text = MixedDirectionText,
                    Placement = SymbolPlacement.Point,
                    LayoutOptions = TextLayoutOptions.Default,
                    TextSizePx = 16f,
                },
            };
            var layer = new StyledSymbolTileBuilder.ExtractedLayer(0, new FontStack { Names = new[] { FontName } }, symbols);
            var output = new SymbolTileBuffer();

            builder.Shape(new List<StyledSymbolTileBuilder.ExtractedLayer> { layer }, output);

            Assert.AreEqual(1, builder.SkippedSymbolCount,
                $"shaping '{MixedDirectionText}' must still throw today — it is the precondition " +
                $"{nameof(CancelDuringGlyphPrepare_UnwindsBeforeShapeOrCommit_ReleasesTheDecodeExactlyOnce)} " +
                "depends on. If this fails, mixed-direction shaping has been implemented and that tooth needs " +
                "a new discriminator.");
            StringAssert.Contains("NotSupportedException", builder.LastSkipReason);
        }

        // Two style layers over the SAME source-layer with DIFFERENT text-font names — two distinct
        // (fontName, rangeStart) keys from the fixture's Latin-range text alone (matches
        // GlyphPrepareBeforeShapeTests.EveryGlyphFetchPrecedesTheFirstShapedSymbol's fixture shape). Both use
        // MixedDirectionText so a shaped symbol leaves a DETECTABLE trace (builder.SkippedSymbolCount) even
        // though the production dispatch path exposes no other window onto its internal, per-build buffer.
        private static readonly string TwoFontMixedDirectionStyleJson = (@"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'layers': [
                { 'id':'labels-a', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'" + MixedDirectionText + @"', 'text-size':16, 'text-font':['FontA'] } },
                { 'id':'labels-b', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'" + MixedDirectionText + @"', 'text-size':16, 'text-font':['FontB'] } }
            ]
        }").Replace('\'', '"');

        /// <summary>T1's behavioural claim, driven through the PRODUCTION dispatch path
        /// (<c>TryBeginBuild</c> → <c>RunWorkerAndHandoff</c> → <c>PumpBuilds</c> → <c>RunTailAsync</c>), not
        /// <c>BuildAsync</c> — which <see cref="GlyphPrepareBeforeShapeTests.EveryGlyphFetchPrecedesTheFirstShapedSymbol"/>
        /// drives, but which has ZERO production callers (production runs through
        /// <see cref="TileSymbolLayerProcessor"/>/<c>SymbolSubsystem.RunTailAsync</c>). Without this, the only
        /// BEHAVIOURAL fetch-precedes-shape tooth exercises a path production never takes, and production
        /// ordering is pinned only by source-grep teeth that can go stale silently.
        ///
        /// <para>The production tail's buffer is never exposed to a test, so this reads
        /// <see cref="StyledSymbolTileBuilder.SkippedSymbolCount"/> (via <see cref="SymbolSubsystemTestExtensions.Builder"/>)
        /// as the "has a symbol been shaped yet" signal instead of a buffer's <c>Symbols.Count</c> — both
        /// layers' text is <see cref="MixedDirectionText"/>, so every attempted shape increments it
        /// (<see cref="Precondition_ShapingTheMixedDirectionTextStillThrows"/> pins that this still holds).
        /// </para>
        ///
        /// <para><b>RED injection:</b> restore the interleave inside <c>RunTailAsync</c> — collect+ensure+shape
        /// one processor at a time instead of collect-all/ensure-all/shape-all. Layer B's fetch then observes
        /// <c>SkippedSymbolCount &gt; 0</c> (layer A already shaped-and-failed).</para></summary>
        [Test]
        public void EveryGlyphFetchPrecedesTheFirstShapedSymbol_ThroughProductionDispatch()
        {
            var fetches = new List<(string FontName, int RangeStart, int SkippedAtFetchTime)>();
            _subsystem = new SymbolSubsystem(_mapCamera) { NowSecondsOverride = () => _simulatedNow };
            _subsystem.GlyphSourceFactoryOverride = _ => new TestGlyphSource((fontName, rangeStart, ct) =>
            {
                fetches.Add((fontName, rangeStart, _subsystem.Builder().SkippedSymbolCount));
                return UniTask.FromResult(GlyphRangeResponse.Absent()); // absent is fine: shaping fails on the
                                                                          // bidi check, before glyph resolution
            });
            StyleDocument style = StyleParser.Parse(TwoFontMixedDirectionStyleJson); // no 'sprite' key -> no park
            _subsystem.SetStyle(style, ExtractSymbolLayers(style));

            var probe = new LeaseProbeDecoder();
            ISymbolTileWorkerPass pass = _subsystem.TryBeginBuild(SourceId, Tile0);
            Assert.IsNotNull(pass, "sanity: the style names this source, so the kick must produce a pass");

            var lease = new SharedDisposable<IDecodedTile>(probe.Decode(Tile0, _tileBytes));
            pass.RunWorkerAndHandoff(lease); // worker step: extract both layers, enqueue the ready tail
            lease.Release();

            // TestGlyphSource resolves via UniTask.FromResult, so the whole tail runs synchronously to
            // completion inside this one call — a vacuity trap in its own right
            // (a fixture lacking the keys an oracle reads makes it vacuous),
            // which is why the evidence below is captured DURING execution (the fetches list), not inferred
            // from "PumpBuilds returned", and why the two layers use DIFFERENT font names.
            _subsystem.PumpBuilds();

            Assert.GreaterOrEqual(fetches.Count, 2,
                "sanity: two differently-named font stacks over Latin text must drive at least two distinct " +
                "(fontName, rangeStart) fetches — a fixture that drives none proves nothing.");
            foreach (var fetch in fetches)
                Assert.AreEqual(0, fetch.SkippedAtFetchTime,
                    $"fetch of ({fetch.FontName}, {fetch.RangeStart}) observed a symbol already shaped(-and-" +
                    "failed) — every fetch must happen BEFORE the build shapes its first symbol.");
            Assert.Greater(_subsystem.Builder().SkippedSymbolCount, 0,
                "sanity: the build must actually attempt to shape (and fail on) the mixed-direction symbols, " +
                "or the zero-at-fetch-time check above is vacuously true.");
        }

        // ── Shared symbol-buffer helper (mirrors SymbolReconcileAsyncTests') ──────────────────────────

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

        private static void AddPointSymbol(SymbolTileBuffer buffer, double3 anchor, string text,
            int feature, long tileKey, float u)
        {
            var layout = OneQuad(u);
            TestSymbolTileBuffer.AddPoint(buffer, anchor, layout.Quads, layout.BoundsMin, layout.BoundsMax,
                text: text, textSizePx: 20f, paddingPx: 2f, featureIndex: feature, tileKey: tileKey, paint: SymbolPaint.Default);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolTailPumpTests — the worker/tail split's structural (non-async) half
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Epic A / A5a-A5b: the acceptance teeth for the worker-phase / tail
    /// split — the worker phase (<see cref="SymbolSubsystem.TryBeginBuild"/>'s returned pass) stops
    /// after the pool-side extract and hands a ready tail (via the A5b pool→main handoff) to
    /// <see cref="SymbolSubsystem.PumpBuilds"/>' budgeted tail-start loop (<c>RunTailAsync</c>), rather
    /// than shaping + committing inline. These teeth fail a shallow split — a rename that leaves the tail
    /// running inline, an unbudgeted tail loop, or a split that breaks the partial-commit guard. Both teeth
    /// here are structural (source-grep) and need no fixture/camera/subsystem harness — the behavioural
    /// [UnityTest] teeth that DO (and drive <see cref="SymbolSubsystemPumpTests"/>' fixture/glyph-source
    /// harness) live in the PlayMode half.
    /// </summary>
    [TestFixture]
    public class SymbolTailPumpTests
    {
        // ── F-1: the split is real (structural) — re-expressed at A5b (BuildTileAsync no longer exists;
        //    the worker-pass call now lives in SymbolTileWorkerPass.RunWorkerAndHandoff, not a coroutine
        //    method this grep-narrow tool can anchor on by signature). Genuinely RED pre-A5a: both call
        //    forms lived inside one method's body, and RunTailAsync did not exist. ───────────────────────
        [Test]
        public void RunWorkerAndHandoff_NeverShapesOrCommits_RunTailAsync_DoesBothExactlyOnce()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);

            const string runWorkerAndHandoffAnchor = "void RunWorkerAndHandoff(SharedDisposable<IDecodedTile> decode)";
            const string runTailAsyncAnchor        = "UniTaskVoid RunTailAsync(";
            const string shapeCallForm             = ".CompleteOnMain(";
            const string commitCallForm            = ".CompleteBuild(";

            string workerBody = ExtractMethodBody(source, runWorkerAndHandoffAnchor, path);
            string tailBody   = ExtractMethodBody(source, runTailAsyncAnchor, path);

            Assert.AreEqual(0, CountOccurrences(workerBody, shapeCallForm),
                $"RunWorkerAndHandoff must contain ZERO '{shapeCallForm}' call sites — the per-layer shape " +
                "loop lives only in RunTailAsync (A5a's split, preserved by A5b's feed swap).");
            Assert.AreEqual(0, CountOccurrences(workerBody, commitCallForm),
                $"RunWorkerAndHandoff must contain ZERO '{commitCallForm}' call sites — the commit lives " +
                "only in RunTailAsync too.");
            Assert.AreEqual(1, CountOccurrences(tailBody, shapeCallForm),
                $"RunTailAsync must call '{shapeCallForm}' exactly once — the per-layer shape loop (A5a's split).");
            Assert.AreEqual(1, CountOccurrences(tailBody, commitCallForm),
                $"RunTailAsync must call '{commitCallForm}' exactly once — the sole commit site after the split.");
        }

        // ── A3 merge-step follow-up: the commit-gating ORDER in RunTailAsync — the sole CompleteBuild commit
        //    is reached only AFTER the whole per-layer CompleteOnMain loop AND a trailing ct check, so a
        //    cancel landing mid-loop (between processor tails k and k+1, or after the last one) never commits
        //    partial symbols. F-1 pins the call COUNTS (each exactly once); this pins their ORDER — the actual
        //    partial-commit guard the A3 review flagged as covered only indirectly. Complements the behavioural
        //    F-3(b) (cancel OBSERVED inside a shape await); this pins the guard structurally + deterministically.
        //    Glyph-fetch hoist: RunTailAsync now ALSO has a ct check right after its EnsureGlyphRangesAsync
        //    await, BEFORE the shape loop — so ".ThrowIfCancellationRequested(" now occurs TWICE in the tail
        //    body. The trailing (partial-commit) guard we pin here is the LAST occurrence, not the first.
        [Test]
        public void RunTailAsync_CommitIsGatedBehindTheWholeLoopAndACtCheck()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);
            string tailBody = ExtractMethodBody(source, "UniTaskVoid RunTailAsync(", path);

            // LAST shape call = the loop's final iteration; the trailing ct check + commit must both follow
            // it. LAST ct check (not first) = the trailing partial-commit guard — the new pre-loop ct check
            // the glyph-fetch hoist added sits BEFORE the shape loop and must not be mistaken for it.
            int lastShapeIdx = tailBody.LastIndexOf(".CompleteOnMain(", StringComparison.Ordinal);
            int ctCheckIdx   = tailBody.LastIndexOf(".ThrowIfCancellationRequested(", StringComparison.Ordinal);
            int commitIdx    = tailBody.IndexOf(".CompleteBuild(", StringComparison.Ordinal);

            Assert.GreaterOrEqual(lastShapeIdx, 0, "sanity: the per-layer shape loop call must be present.");
            Assert.GreaterOrEqual(ctCheckIdx, 0,
                "RunTailAsync must call ThrowIfCancellationRequested() — the trailing guard that catches a " +
                "cancel landing after the loop's shaping but before the commit.");
            Assert.GreaterOrEqual(commitIdx, 0, "sanity: the CompleteBuild commit call must be present.");

            Assert.Less(lastShapeIdx, ctCheckIdx,
                "the trailing ct check must come AFTER the whole per-layer shape loop (its LAST CompleteOnMain) " +
                "— the commit is gated behind the WHOLE loop clearing cancellation first, so a mid-loop cancel " +
                "never commits partial labels (A3 follow-up).");
            Assert.Less(ctCheckIdx, commitIdx,
                "CompleteBuild must come AFTER the ct check — partial labels never reach the commit on a cancel.");

            Assert.IsTrue(tailBody.Contains("catch (OperationCanceledException"),
                "RunTailAsync must catch OperationCanceledException — a cancel OBSERVED inside a shape await " +
                "(F-3(b)) unwinds before the commit, silently (no partial commit on that path either).");
        }

        // ── Glyph-fetch hoist (T4/T5a/T5b) ────────────────────────────────────────────────────────────

        /// <summary>T4 structural half: <c>RunTailAsync</c> now awaits exactly ONCE — the build-wide glyph-
        /// range ensure step — and that ONE await comes BEFORE the per-layer shape loop starts. Complements
        /// <c>GlyphPrepareBeforeShapeTests.TheMainTailHasNoSuspensionPoint</c>'s reflection half (a different
        /// instrument reading a different thing, whose blind spot does not transfer).
        /// <para><b>RED injection:</b> add a second <c>await UniTask.Yield();</c> anywhere inside
        /// <c>RunTailAsync</c>'s shape loop — the count assertion reddens.</para></summary>
        [Test]
        public void RunTailAsync_AwaitsOnlyTheGlyphPrepare_BeforeTheShapeLoop()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);
            string tailBody = ExtractMethodBody(source, "UniTaskVoid RunTailAsync(", path);

            const string awaitForm = "await ";
            int awaitCount = CountOccurrences(tailBody, awaitForm);
            Assert.AreEqual(1, awaitCount,
                "RunTailAsync must await EXACTLY ONCE — the glyph-range ensure step, the tail's sole " +
                "surviving suspension point. The per-layer shape loop (CompleteOnMain) must never be awaited.");

            int awaitIdx = tailBody.IndexOf(awaitForm, StringComparison.Ordinal);
            int firstShapeIdx = tailBody.IndexOf(".CompleteOnMain(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(awaitIdx, 0, "sanity: the await must be present.");
            Assert.GreaterOrEqual(firstShapeIdx, 0, "sanity: the shape loop call must be present.");
            Assert.Less(awaitIdx, firstShapeIdx,
                "the ONE await (the glyph-range ensure) must come BEFORE the shape loop starts — the whole " +
                "point of the hoist is that shaping never suspends.");
        }

        /// <summary>T5a: the collect step must run on MAIN, after the worker step, inside <c>RunTailAsync</c>
        /// — never migrated into the worker pass, where it would run off-main against a glyph
        /// cache/atlas that is main-thread-only. Reads TWO source files: the worker step
        /// (<c>TileSymbolLayerProcessor.ProcessOnWorker</c>) and the pass's owner
        /// (<c>SymbolSubsystem.RunSymbolWorkerAndHandoff</c>) must both call it ZERO times; the tail
        /// (<c>RunTailAsync</c>) must call it EXACTLY once.
        /// <para><b>RED injection:</b> move the <c>CollectRequiredRanges</c> call from <c>RunTailAsync</c>
        /// into <c>TileSymbolLayerProcessor.ProcessOnWorker</c> — the worker-body count goes from 0 to 1 and
        /// the tail-body count goes from 1 to 0.</para></summary>
        [Test]
        public void CollectRequiredRanges_RunsOnlyInsideRunTailAsync_NeverOnTheWorker()
        {
            string subsystemPath = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            string processorPath = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "Processing",
                "TileSymbolLayerProcessor.cs");
            FileAssert.Exists(subsystemPath);
            FileAssert.Exists(processorPath);
            string subsystemSource = File.ReadAllText(subsystemPath);
            string processorSource = File.ReadAllText(processorPath);

            string tailBody   = ExtractMethodBody(subsystemSource, "UniTaskVoid RunTailAsync(", subsystemPath);
            string workerOwnerBody = ExtractMethodBody(
                subsystemSource,
                "void RunSymbolWorkerAndHandoff(",
                subsystemPath);
            string processOnWorkerBody = ExtractMethodBody(
                processorSource,
                "void ProcessOnWorker(IDecodedTile tile, in TileLayerProcessContext context)",
                processorPath);

            const string collectCallForm = "CollectRequiredRanges(";

            Assert.AreEqual(0, CountOccurrences(processOnWorkerBody, collectCallForm),
                $"TileSymbolLayerProcessor.ProcessOnWorker must contain ZERO '{collectCallForm}' calls — the " +
                "collect step runs on MAIN, after the worker step, never on the worker itself (against a " +
                "glyph cache/atlas that is main-thread-only).");
            Assert.AreEqual(0, CountOccurrences(workerOwnerBody, collectCallForm),
                $"SymbolSubsystem.RunSymbolWorkerAndHandoff must contain ZERO '{collectCallForm}' calls — " +
                "same reason: this runs on the worker side of the split.");
            Assert.AreEqual(1, CountOccurrences(tailBody, collectCallForm),
                $"RunTailAsync must call '{collectCallForm}' exactly once — the sole main-thread collect site.");
        }

        /// <summary>T5b: <c>ReadySymbolTail</c> must never start carrying a
        /// <see cref="SharedDisposable{T}"/>&lt;<see cref="IDecodedTile"/>&gt; — its decode reference's
        /// lifetime ends at the worker step (the parked-drain body's <c>finally</c> / the kick lambda's
        /// release), before a <see cref="SymbolSubsystem.ReadySymbolTail"/> even exists. If a future change
        /// makes the tail carry one, the "cancelled build releases its decode exactly once" property would
        /// need its OWN tooth — this one exists to make sure nobody adds that field without noticing.
        /// <para><b>RED injection:</b> add a <c>SharedDisposable&lt;IDecodedTile&gt;</c>-typed field or
        /// property to <c>ReadySymbolTail</c>.</para></summary>
        [Test]
        public void ReadySymbolTail_NeverCarriesADecodeReference()
        {
            Type tailType = typeof(SymbolSubsystem).GetNestedType("ReadySymbolTail", BindingFlags.NonPublic);
            Assert.IsNotNull(tailType, "sanity: SymbolSubsystem.ReadySymbolTail must exist as a nested type.");

            Type decodeRefType = typeof(SharedDisposable<IDecodedTile>);
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (FieldInfo field in tailType.GetFields(all))
                Assert.AreNotEqual(decodeRefType, field.FieldType,
                    $"ReadySymbolTail must never declare a {decodeRefType.Name}-typed field ('{field.Name}') " +
                    "— its decode reference's lifetime ends at the worker step, before the tail exists.");
            foreach (PropertyInfo prop in tailType.GetProperties(all))
                Assert.AreNotEqual(decodeRefType, prop.PropertyType,
                    $"ReadySymbolTail must never declare a {decodeRefType.Name}-typed property ('{prop.Name}') " +
                    "— its decode reference's lifetime ends at the worker step, before the tail exists.");
        }

        /// <summary>Extracts the brace-balanced body (inclusive of the outer braces) of the method whose
        /// definition contains <paramref name="signatureAnchor"/>, by scanning forward from the first '{'
        /// after the anchor and counting nesting depth. Mirrors
        /// <c>Structure.TileProcessingStructureTests.ExtractMethodBody</c> — a deliberately narrow tool for a
        /// call-form-narrow grep guard, not a C# parser.</summary>
        private static string ExtractMethodBody(string source, string signatureAnchor, string path)
        {
            int anchorIndex = source.IndexOf(signatureAnchor, StringComparison.Ordinal);
            Assert.GreaterOrEqual(anchorIndex, 0,
                $"expected to find the unique method-definition anchor '{signatureAnchor}' in {path}");

            int braceStart = source.IndexOf('{', anchorIndex);
            Assert.GreaterOrEqual(braceStart, 0, $"expected an opening brace after the method signature in {path}");

            int depth = 0;
            int i = braceStart;
            for (; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) break;
                }
            }
            Assert.Less(i, source.Length, $"unbalanced braces scanning the method body in {path}");

            return source.Substring(braceStart, i - braceStart + 1);
        }

        private static int CountOccurrences(string text, string callForm)
            => Regex.Matches(text, Regex.Escape(callForm)).Count;
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolTileStoreTests — a released-to-cache tile's symbols stay warm; a truly-evicted tile drops them
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The symbol-lifecycle fix (zoom-out-then-in "no symbols" bug): <see cref="SymbolTileStore"/> keeps a
    /// released-to-cache tile's symbols WARM and restores them on a prepared-cache hit (which does not re-fetch),
    /// while a truly-evicted tile drops them. Also covers the async race — a tile released WHILE its build is in
    /// flight must still get its symbols (into the cached side), and a superseded build must be discarded.
    ///
    /// <para><b>Reader cutover (4.2).</b> The reconciler now reads a tile's baked <see cref="SymbolTileBlock"/>,
    /// not a managed symbol list — <see cref="SymbolTileStore.CaptureSnapshot"/> skips any entry whose
    /// <c>Block</c> is null, so every commit a test wants <see cref="Collect"/>/<c>CollectInto</c> to see now goes
    /// through <see cref="Commit"/>, which bakes a REAL block. <see cref="_blockSources"/> maps a baked block back
    /// to the exact <see cref="SymbolTileBuffer"/> it was baked from purely so <see cref="Collect"/> can hand
    /// back the same <see cref="ShapedSymbol"/> values the pre-cutover managed-list <c>CollectInto</c> overload
    /// did — this is reading the winner's actual committed content (a genuine input for a `FeatureIndex`/value
    /// assertion), never a restatement of a separately-computed oracle index (the one place that pattern would be
    /// vacuous — the interned-dedup oracle test below — compares the reconciler's own (blockId, localIndex) arrays
    /// against an INDEPENDENT string-keyed oracle's, not via this registry).</para>
    /// </summary>
    [TestFixture]
    public class SymbolTileStoreTests
    {
        // A leaked SymbolTileBlock holds DebugLiveAllocCount elevated permanently — the counter is
        // decremented only in Dispose, never by a finalizer, so this delta is deterministic rather than
        // GC-timing-dependent. A test that bakes a block and never disposes it is caught here — real
        // blocks via DebugLiveAllocCount, and FakeDisposableBlock via the same mirrored counter below.
        private long _liveBlocks;
        private int _liveFakes;
        [SetUp] public void BaselineBlocks()
        {
            _liveBlocks = SymbolTileBlock.DebugLiveAllocCount;
            _liveFakes = FakeDisposableBlock.LiveCount;
        }
        [TearDown] public void NoLeakedBlocks()
        {
            Assert.AreEqual(_liveBlocks, SymbolTileBlock.DebugLiveAllocCount,
                "this test baked a block it never disposed — release the snapshot and Clear() the store");
            Assert.AreEqual(_liveFakes, FakeDisposableBlock.LiveCount,
                "this test committed a FakeDisposableBlock it never disposed — Clear() the store");
        }

        private static SymbolTileStore.Key Key(string source, int x)
            => new SymbolTileStore.Key(source, new TileId { Z = 5, X = x, Y = 0 });

        // A distinct symbol list (identity via FeatureIndex) so assertions can pin WHICH tile's symbols came back.
        // Distinct AnchorRender per marker: the reconciler's dedup key includes the anchor, and every OTHER field
        // here defaults identically (Placement=Point, Text=null, IconImage=null, MaterialIndex=0) across every
        // marker — without a distinct anchor, two DIFFERENT tiles' Symbols(n) calls would collide onto the SAME
        // DedupKey and dedup would (correctly, but unintentionally) collapse them to one, breaking every
        // lifecycle test that expects N independent tiles' symbols all to collect.
        private static SymbolTileBuffer Symbols(int marker)
            => TestSymbolTileBuffer.Point(new double3(marker * 10_000.0, 0, marker * 10_000.0), null, float2.zero, float2.zero,
                featureIndex: marker);

        // Test-only: a baked block's source buffer, so Collect() can hand back the SAME ShapedSymbol values
        // CollectInto used to hand back as managed-object references (see the type doc — not used for the oracle
        // test).
        private readonly Dictionary<SymbolTileBlock, SymbolTileBuffer> _blockSources = new();

        // Bakes a REAL block for `buffer` and commits it — the reconciler cutover means every commit a test wants
        // CaptureSnapshot/CollectInto to see needs a real SymbolTileBlock (a block-less entry is no longer
        // collected — SymbolTileStore.CaptureSnapshot's `Block == null` guard). UMR-87: Bake no longer interns
        // (it copies the ids ShapedSymbol already carries), so there is no table to share here any more.
        private bool Commit(SymbolTileStore store, SymbolTileStore.Key key, int gen, SymbolTileBuffer buffer)
        {
            SymbolTileBlock block = SymbolTileBlockBaker.Bake(buffer, slotCount: 1, double3.zero);
            bool committed = store.CompleteBuild(key, gen, block);
            _blockSources[block] = buffer;
            return committed;
        }

        // Reader cutover: routes through the plan-aware CollectInto (the only overload left) and materializes the
        // SAME ShapedSymbol values the pre-cutover managed-list overload returned, via _blockSources.
        private List<ShapedSymbol> Collect(SymbolTileStore store)
            => CollectWithActiveCount(store, CrossTileSymbolKey.CanonicalGridMeters, out _);

        // As Collect, but also returns the active/departing split — the plan-aware CollectInto's out param.
        private List<ShapedSymbol> CollectWithActiveCount(SymbolTileStore store, double gate, out int activeCount)
        {
            var blockId = new List<int>(); var localIndex = new List<int>(); var isDeparting = new List<byte>();
            store.CollectInto(blockId, localIndex, isDeparting, gate, out activeCount);
            var output = new List<ShapedSymbol>(blockId.Count);
            for (int i = 0; i < blockId.Count; i++)
                output.Add(_blockSources[store.OrderedBlocks[blockId[i]]].Symbols[localIndex[i]]);
            return output;
        }

        // ── THE bug: build → release-to-cache → (out of cover, not rendered) → cache HIT restore → back. ──
        [Test]
        public void ReleaseToCacheThenRestore_KeepsSymbols()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);

            int gen = store.BeginBuild(key);
            Commit(store, key, gen, Symbols(1));
            Assert.AreEqual(1, Collect(store).Count, "an active tile's labels render");

            store.Release(key, transferredToCache: true);
            Assert.AreEqual(0, Collect(store).Count, "a cached (out-of-cover) tile does NOT render — but is kept warm");

            store.Restore(key); // prepared-cache HIT — no fetch, so no rebuild; symbols must come from the store
            List<ShapedSymbol> back = Collect(store);
            Assert.AreEqual(1, back.Count, "the cache hit restores the kept-warm labels (the zoom-out-then-in fix)");
            Assert.AreEqual(1, back[0].FeatureIndex, "it is the SAME tile's labels");
            store.Clear();
        }

        // ── A true eviction (cache disabled / not built) drops the symbols — a later restore finds nothing. ──
        [Test]
        public void ReleaseWithoutCache_DropsSymbols()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            Commit(store, key, store.BeginBuild(key), Symbols(1));

            store.Release(key, transferredToCache: false);
            store.Restore(key); // nothing was kept — a no-op
            Assert.AreEqual(0, Collect(store).Count, "a truly-evicted tile's labels are gone, not resurrected");
        }

        // ── The async race: a tile RELEASED-to-cache while its build is still in flight must still capture its
        //    symbols (into the cached entry) so a later hit restores them — not lose them permanently. ──
        [Test]
        public void ReleasedMidBuild_StillCapturesSymbolsIntoCache()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);

            int gen = store.BeginBuild(key);            // build starts
            store.Release(key, transferredToCache: true); // tile leaves cover BEFORE the (awaited) build completes
            Assert.IsTrue(Commit(store, key, gen, Symbols(1)),
                "a build completing after a release-to-cache must still commit (into the cached entry)");
            Assert.AreEqual(0, Collect(store).Count, "still out of cover → not rendered yet");

            store.Restore(key);
            Assert.AreEqual(1, Collect(store).Count, "…and the cache hit then shows the labels captured mid-flight");
            store.Clear();
        }

        // ── A superseded build (the tile was re-fetched, a newer BeginBuild took the slot) is discarded. ──
        [Test]
        public void SupersededBuild_IsDiscarded()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);

            int gen1 = store.BeginBuild(key);
            int gen2 = store.BeginBuild(key); // a newer fetch/build for the same tile takes the slot

            Assert.IsFalse(Commit(store, key, gen1, Symbols(1)), "the stale (superseded) build must not commit");
            Assert.AreEqual(0, Collect(store).Count, "…so the stale labels never render");

            Assert.IsTrue(Commit(store, key, gen2, Symbols(2)), "the current build commits");
            Assert.AreEqual(2, Collect(store)[0].FeatureIndex, "and only its labels render");
            store.Clear();
        }

        // ── FIFO cap: the cached side is bounded — the OLDEST-released tile is evicted first, so it cannot be
        //    restored while the newer ones can (sized to the mesh cache's cap, symbols outlive their meshes). ──
        [Test]
        public void CachedSide_EvictsOldestReleasedFirst()
        {
            var store = new SymbolTileStore(cacheCap: 2);
            var a = Key("src", 1); var b = Key("src", 2); var c = Key("src", 3);
            Commit(store, a, store.BeginBuild(a), Symbols(1));
            Commit(store, b, store.BeginBuild(b), Symbols(2));
            Commit(store, c, store.BeginBuild(c), Symbols(3));

            store.Release(a, true); // oldest released
            store.Release(b, true);
            store.Release(c, true); // over cap (2) → evicts a (the oldest)
            Assert.AreEqual(2, store.CachedTileCount, "cap of 2 bounds the cached side");

            store.Restore(a);
            Assert.AreEqual(0, Collect(store).Count, "the oldest-released tile was evicted — nothing to restore");
            store.Restore(b); store.Restore(c);
            var back = Collect(store);
            Assert.AreEqual(2, back.Count, "the two newest cached tiles restore fine");
            store.Clear();
        }

        // ── Multi-source isolation (why we DON'T evict by bare tileId): same TileId, different source are
        //    independent — releasing/restoring one never touches the other. ──
        [Test]
        public void SameTileDifferentSource_AreIndependent()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var a = new SymbolTileStore.Key("srcA", new TileId { Z = 5, X = 7, Y = 7 });
            var b = new SymbolTileStore.Key("srcB", new TileId { Z = 5, X = 7, Y = 7 }); // same tile, other source
            Commit(store, a, store.BeginBuild(a), Symbols(10));
            Commit(store, b, store.BeginBuild(b), Symbols(20));

            store.Release(a, true);
            store.Release(b, true);
            store.Restore(a); // restore ONLY source A

            var back = Collect(store);
            Assert.AreEqual(1, back.Count, "only source A's labels are active");
            Assert.AreEqual(10, back[0].FeatureIndex, "and they are A's, not B's — keys are (source, tile)");
            store.Clear();
        }

        // ═══ A-1: the PULL/reconcile model (replaces the release/restore push-callbacks) ═══

        private static List<SymbolTileStore.Key> Loaded(params SymbolTileStore.Key[] keys)
            => new List<SymbolTileStore.Key>(keys);

        // ── THE zoom-out-then-in bug, now via reconcile: build (loaded) → reconcile without it (leaves cover,
        //    kept warm) → reconcile with it again (cache-hit re-entry) restores the symbols. No callbacks. ──
        [Test]
        public void Reconcile_LeaveThenReenter_RestoresSymbols()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            Commit(store, key, store.BeginBuild(key), Symbols(1));
            Assert.AreEqual(1, Collect(store).Count, "active tile renders");

            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true); // tile left cover
            Assert.AreEqual(0, Collect(store).Count, "out of cover → not rendered");
            Assert.AreEqual(1, store.CachedTileCount, "…but kept warm");

            store.ReconcileActiveSet(Loaded(key), keepWarmOnRelease: true); // cache-hit re-entry (no re-fetch)
            List<ShapedSymbol> back = Collect(store);
            Assert.AreEqual(1, back.Count, "reconcile restores the kept-warm labels on re-entry");
            Assert.AreEqual(1, back[0].FeatureIndex, "the SAME tile's labels");
            store.Clear();
        }

        // ── keepWarmOnRelease:false (mesh cache disabled) → a released tile is dropped, not kept warm, so a
        //    later re-entry has nothing to restore (a re-fetch would rebuild it via BeginBuild instead). ──
        [Test]
        public void Reconcile_ReleaseWithoutKeepWarm_Drops()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            Commit(store, key, store.BeginBuild(key), Symbols(1));

            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: false);
            Assert.AreEqual(0, store.CachedTileCount, "cache disabled → not kept warm");
            store.ReconcileActiveSet(Loaded(key), keepWarmOnRelease: false);
            Assert.AreEqual(0, Collect(store).Count, "nothing to restore — a revisit must re-fetch/rebuild");
        }

        // ── Idempotent: reconciling twice with the same loaded set moves nothing (self-healing, no churn). ──
        [Test]
        public void Reconcile_SameSetTwice_IsNoOp()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            Commit(store, a, store.BeginBuild(a), Symbols(1));
            Commit(store, b, store.BeginBuild(b), Symbols(2));

            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true);
            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true);
            Assert.AreEqual(2, store.ActiveTileCount, "both stay active");
            Assert.AreEqual(0, store.CachedTileCount, "nothing released");
            Assert.AreEqual(2, Collect(store).Count);
            store.Clear();
        }

        // ── A loaded tile with no symbol entry yet (still fetching) is left untouched — its build is kicked by
        //    the bytes-ready push, not by reconcile. Reconcile must not fabricate an entry for it. ──
        [Test]
        public void Reconcile_LoadedButUnbuilt_LeavesForBytesPush()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var pending = Key("src", 9);
            store.ReconcileActiveSet(Loaded(pending), keepWarmOnRelease: true);
            Assert.AreEqual(0, store.ActiveTileCount, "reconcile does not build — it only moves existing entries");
            Assert.AreEqual(0, store.CachedTileCount);
        }

        // ── BeginBuild keeps an existing tile's stale symbols visible through a rebuild (no empty flash) — the
        //    symbols only swap when the new build commits. ──
        [Test]
        public void BeginBuild_Rebuild_KeepsStaleSymbolsUntilCommit()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            Commit(store, key, store.BeginBuild(key), Symbols(1));

            int gen2 = store.BeginBuild(key); // a rebuild starts (e.g. zoom re-fetch)
            Assert.AreEqual(1, Collect(store).Count, "the old labels keep rendering during the rebuild — no flash");
            Assert.AreEqual(1, Collect(store)[0].FeatureIndex, "…and they are still the OLD labels");

            Commit(store, key, gen2, Symbols(2));
            Assert.AreEqual(2, Collect(store)[0].FeatureIndex, "only when the rebuild commits do they swap");
            store.Clear();
        }

        // ── Clear (restyle) drops everything, active and cached. ──
        [Test]
        public void Clear_DropsActiveAndCached()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            Commit(store, a, store.BeginBuild(a), Symbols(1));
            Commit(store, b, store.BeginBuild(b), Symbols(2));
            store.Release(b, true); // b cached, a active

            store.Clear();
            Assert.AreEqual(0, store.ActiveTileCount);
            Assert.AreEqual(0, store.CachedTileCount);
            store.Restore(b);
            Assert.AreEqual(0, Collect(store).Count, "a restyle purges both sides");
        }

        // ═══ Retain-as-departing: a tile leaving cover keeps its symbols COLLECTED (fading) for a grace window ═══

        // ── A released tile is stamped departing and STILL collected (so its symbols fade out, not pop) until the
        //    grace window elapses and a later reconcile purges it — the symbol staying warm on the cached side. ──
        [Test]
        public void Departing_ReleaseWithGrace_CollectedThenPurgedAfterWindow()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            Commit(store, key, store.BeginBuild(key), Symbols(1));

            // Leaves cover at t=10 with a 0.5s grace → kept warm AND stamped departing.
            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "the released tile is departing");
            Assert.AreEqual(0, store.ActiveTileCount, "…and no longer active");
            List<ShapedSymbol> during = Collect(store);
            Assert.AreEqual(1, during.Count, "a departing tile is STILL collected (its labels fade out, not pop)");
            Assert.AreEqual(1, during[0].FeatureIndex, "…and they are its own labels");

            // A reconcile still inside the window keeps it departing + collected.
            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 10.3, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "still within the grace window");
            Assert.AreEqual(1, Collect(store).Count, "…still collected");

            // Past the window → purged. The symbols stay WARM (a cache hit still restores them) but are no longer
            // collected as departing (by now they have fully faded, so this is not a pop).
            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 10.6, departingGraceSeconds: 0.5);
            Assert.AreEqual(0, store.DepartingTileCount, "grace elapsed → purged");
            Assert.AreEqual(0, Collect(store).Count, "…no longer collected");
            Assert.AreEqual(1, store.CachedTileCount, "but still kept warm for a cache-hit re-entry");
            store.Clear();
        }

        // ── A tile that re-enters cover WITHIN the grace window is restored to active (fades back in), not left
        //    departing — the RemoveCached chokepoint clears the stamp (the departing ⊆ cached invariant). ──
        [Test]
        public void Departing_ReEntryWithinGrace_RestoredActive()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            Commit(store, key, store.BeginBuild(key), Symbols(1));
            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "departing after leaving cover");

            store.ReconcileActiveSet(Loaded(key), keepWarmOnRelease: true, nowSeconds: 10.2, departingGraceSeconds: 0.5);
            Assert.AreEqual(0, store.DepartingTileCount, "re-entry within grace clears the departing stamp");
            Assert.AreEqual(1, store.ActiveTileCount, "…and the tile is active again");
            Assert.AreEqual(1, Collect(store).Count, "…rendered as a normal active label (fades back in)");
            store.Clear();
        }

        // ── With no grace window (the default / cache-disabled path) a release retains nothing — pre-fade behaviour. ──
        [Test]
        public void Departing_GraceZero_NoRetention()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            Commit(store, key, store.BeginBuild(key), Symbols(1));
            store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true); // grace defaults to 0 → feature off
            Assert.AreEqual(0, store.DepartingTileCount, "grace 0 ⇒ nothing retained");
            Assert.AreEqual(0, Collect(store).Count, "a released tile is not collected");
            store.Clear();
        }

        // ── CollectInto's out-activeCount splits active symbols (first) from departing symbols (appended last) — the
        //    split the batch builder reads to flag which records fade out. ──
        [Test]
        public void Departing_CollectInto_SplitsActiveFromDeparting()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            Commit(store, a, store.BeginBuild(a), Symbols(1));
            Commit(store, b, store.BeginBuild(b), Symbols(2));
            // b leaves cover with grace → departing; a stays active.
            store.ReconcileActiveSet(Loaded(a), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);

            List<ShapedSymbol> output = CollectWithActiveCount(store, CrossTileSymbolKey.CanonicalGridMeters, out int activeCount);
            Assert.AreEqual(2, output.Count, "both the active and the departing label are collected");
            Assert.AreEqual(1, activeCount, "exactly one is active; the departing come after the split");
            Assert.AreEqual(1, output[0].FeatureIndex, "active label first");
            Assert.AreEqual(2, output[activeCount].FeatureIndex, "departing label after the split");
            store.Clear();
        }

        // ── A departing POINT symbol whose cross-tile identity is already shown by an ACTIVE symbol is NOT collected
        //    twice (no ghost fade under it) — the multi-source / dedup-winner case the per-record flag must handle. ──
        [Test]
        public void Departing_ClaimedByActiveSymbol_NotDoubleCollected()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            // Same anchor (0)/material (0)/text → the SAME cross-tile identity, so the active copy claims it.
            var la = TestSymbolTileBuffer.Point(default, null, float2.zero, float2.zero, featureIndex: 1, text: "x");
            var lb = TestSymbolTileBuffer.Point(default, null, float2.zero, float2.zero, featureIndex: 2, text: "x");
            Commit(store, a, store.BeginBuild(a), la);
            Commit(store, b, store.BeginBuild(b), lb);
            store.ReconcileActiveSet(Loaded(a), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "b is departing");

            List<ShapedSymbol> output = CollectWithActiveCount(store, 1.0, out int activeCount); // dedup ON
            Assert.AreEqual(1, output.Count, "the active copy shows; the departing twin is skipped (no double-draw)");
            Assert.AreEqual(1, activeCount, "…and it is the active one (nothing appended after the split)");
            Assert.AreEqual(1, output[0].FeatureIndex, "the surviving label is the active tile's");
            store.Clear();
        }

        // ═══ Stage 3: the SEAMLESS same-cell HOLD across a zoom step (the fixed-grid pivot) ═══

        // ── THE pivot-defining tooth: an ACTIVE point symbol at z=9 and a DEPARTING point symbol with the SAME text
        //    and IDENTICAL AnchorRender at z=8 key on the SAME fixed CanonicalGridMeters cell — so the departing
        //    copy is claim-skipped (a seamless hold across the zoom step), NOT emitted as a fading duplicate. Under
        //    the superseded per-zoom grid (MetersPerPixel(tileZoom)) the two bands' grids differ for the same
        //    anchor → different cells → the claim-skip would MISS → a transient fading duplicate on every zoom step.
        //    RED-verified by pointing the store's grid input back at CameraPoseMath.MetersPerPixel((int)(TileKey>>44)). ──
        [Test]
        public void DepartingCrossBand_ClaimSkipped_UnderFixedGrid()
        {
            // 5000 m is large enough that a per-zoom grid WOULD split z8 (~305.7 m/px) vs z9 (~152.9 m/px) for this
            // same anchor (round(5000/305.7)=16 ≠ round(5000/152.9)=33) — so the fixed grid is what makes the hold work.
            double3 anchor = new double3(5000.0, 0, 5000.0);
            var tileActive = new TileId { Z = 9, X = 256, Y = 256 };
            var tileDeparting = new TileId { Z = 8, X = 128, Y = 128 };
            var activeBuffer = new SymbolTileBuffer();
            var active = ParityPoint(activeBuffer, anchor, 0, "Hold", null, 1, tileActive);
            var departingBuffer = new SymbolTileBuffer();
            ParityPoint(departingBuffer, anchor, 0, "Hold", null, 2, tileDeparting);

            var store = new SymbolTileStore(cacheCap: 8);
            var kActive = new SymbolTileStore.Key("src", tileActive);
            var kDeparting = new SymbolTileStore.Key("src", tileDeparting);
            Commit(store, kActive, store.BeginBuild(kActive), activeBuffer);
            Commit(store, kDeparting, store.BeginBuild(kDeparting), departingBuffer);

            // Release the z=8 tile (not in the loaded set) with grace → cached + departing; z=9 stays active.
            store.ReconcileActiveSet(Loaded(kActive), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "the z=8 tile is departing");

            List<ShapedSymbol> output = CollectWithActiveCount(store, 1.0, out int activeCount); // gate ON (magnitude ignored)
            Assert.AreEqual(1, output.Count, "the departing copy shares the active cell (fixed grid) → claim-skipped, no fading duplicate");
            Assert.AreEqual(1, activeCount, "…and nothing is appended after the active split");
            // ShapedSymbol is a struct — no reference identity (the pre-migration Assert.AreSame compared
            // managed-object references). FeatureIndex distinguishes the active (1) from the departing (2)
            // copy — the one field this fixture sets differently between them.
            Assert.AreEqual(active.FeatureIndex, output[0].FeatureIndex, "the surviving copy is the active (z=9) one");
            store.Clear();
        }

        // ── Companion sanity: the same-band hold (both z=9, co-located) — a same-z re-tiling never changed grids,
        //    so this held even under the superseded per-zoom design; it pins the general claim-skip. ──
        [Test]
        public void DepartingSameBand_ClaimSkipped()
        {
            double3 anchor = new double3(5000.0, 0, 5000.0);
            var tileActive = new TileId { Z = 9, X = 256, Y = 256 };
            var tileDeparting = new TileId { Z = 9, X = 257, Y = 256 }; // same band, different tile
            var activeBuffer = new SymbolTileBuffer();
            var active = ParityPoint(activeBuffer, anchor, 0, "Hold", null, 1, tileActive);
            var departingBuffer = new SymbolTileBuffer();
            ParityPoint(departingBuffer, anchor, 0, "Hold", null, 2, tileDeparting);

            var store = new SymbolTileStore(cacheCap: 8);
            var kActive = new SymbolTileStore.Key("src", tileActive);
            var kDeparting = new SymbolTileStore.Key("src", tileDeparting);
            Commit(store, kActive, store.BeginBuild(kActive), activeBuffer);
            Commit(store, kDeparting, store.BeginBuild(kDeparting), departingBuffer);
            store.ReconcileActiveSet(Loaded(kActive), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(1, store.DepartingTileCount, "the second z=9 tile is departing");

            List<ShapedSymbol> output = CollectWithActiveCount(store, 1.0, out int activeCount);
            Assert.AreEqual(1, output.Count, "co-located same-band departing copy is claim-skipped");
            Assert.AreEqual(1, activeCount, "…nothing appended after the split");
            Assert.AreEqual(active.FeatureIndex, output[0].FeatureIndex, "the active copy survives");
            store.Clear();
        }

        // ═══ I6: icon cross-tile identity dedup (the I5b-deferred gap this stage closes) ═══

        // A point symbol pinned to a fixed co-located anchor (double3.zero for every call — "co-located" needs no
        // coordinate math since every test here only varies text/iconImage/tile) — mirrors CrossTileIdentityTests'
        // PointSymbol helper, extended with an iconImage param.
        // Appends one point (or icon, via `iconImage`) symbol into `buffer` and returns the resulting record.
        private static ShapedSymbol AddSymbol(SymbolTileBuffer buffer, int layer, string text, string iconImage, int feature, TileId tile)
        {
            TestSymbolTileBuffer.AddPoint(buffer, default, null, float2.zero, float2.zero,
                text: text, iconImage: iconImage, materialIndex: layer, featureIndex: feature, tileKey: SymbolTileKey.Pack(tile));
            return buffer.Symbols[buffer.Symbols.Count - 1];
        }

        private List<ShapedSymbol> CollectQuantized(SymbolTileStore store, double q)
            => CollectWithActiveCount(store, q, out _);

        // ── ★ RED pre-fix: two co-located icons (same anchor cell + layer, text=null, DISTINCT icon-image) used
        //    to collide into one (symbol.Text == null for both ⇒ the pre-I6 4-arg key ignored the icon). Now both
        //    survive CollectInto. ──
        [Test]
        public void IconDedup_DistinctIconImage_SameCellAndLayer_BothSurvive()
        {
            const double q = 50.0;
            var tile = new TileId { Z = 12, X = 3, Y = 4 };
            var buffer = new SymbolTileBuffer();
            AddSymbol(buffer, 0, null, "sprite-a", 1, tile);
            AddSymbol(buffer, 0, null, "sprite-b", 2, tile);

            var store = new SymbolTileStore(cacheCap: 8);
            var key = new SymbolTileStore.Key("src", tile);
            Commit(store, key, store.BeginBuild(key), buffer);

            Assert.AreEqual(2, CollectQuantized(store, q).Count, "distinct icon-image at the same cell are NOT merged");
            store.Clear();
        }

        // ── Same icon-image in a parent + child tile still dedups to ONE (the seamless-swap property icons
        //    now share with text). ──
        [Test]
        public void IconDedup_SameIconImage_ParentAndChildTile_DedupsToOne()
        {
            const double q = 50.0;
            var parent = new TileId { Z = 10, X = 500, Y = 400 };
            var child = new TileId { Z = 11, X = 1000, Y = 800 };
            var parentBuffer = new SymbolTileBuffer();
            AddSymbol(parentBuffer, 0, null, "sprite-a", 1, parent);
            var childBuffer = new SymbolTileBuffer();
            AddSymbol(childBuffer, 0, null, "sprite-a", 2, child);

            var store = new SymbolTileStore(cacheCap: 8);
            var kParent = new SymbolTileStore.Key("src", parent);
            var kChild = new SymbolTileStore.Key("src", child);
            Commit(store, kParent, store.BeginBuild(kParent), parentBuffer);
            Commit(store, kChild, store.BeginBuild(kChild), childBuffer);

            List<ShapedSymbol> output = CollectQuantized(store, q);
            Assert.AreEqual(1, output.Count, "the same icon in a parent+child tile collapses to one");
            // ShapedSymbol is a struct — FeatureIndex (2 = child, 1 = parent) is the field this fixture varies.
            Assert.AreEqual(2, output[0].FeatureIndex, "the finest (child) tile's label wins");
            store.Clear();
        }

        // ── Byte-identical control: the pre-I6 text-dedup case, unaffected by the icon change — a text symbol at
        //    the same cell/layer in a parent+child tile still dedups to one, finest-zoom wins. ──
        [Test]
        public void TextDedup_SameTextAndCell_ParentAndChildTile_DedupsToOne_Unaffected()
        {
            const double q = 50.0;
            var parent = new TileId { Z = 10, X = 500, Y = 400 };
            var child = new TileId { Z = 11, X = 1000, Y = 800 };
            var parentBuffer = new SymbolTileBuffer();
            AddSymbol(parentBuffer, 0, "Metropolis", null, 1, parent);
            var childBuffer = new SymbolTileBuffer();
            AddSymbol(childBuffer, 0, "Metropolis", null, 2, child);

            var store = new SymbolTileStore(cacheCap: 8);
            var kParent = new SymbolTileStore.Key("src", parent);
            var kChild = new SymbolTileStore.Key("src", child);
            Commit(store, kParent, store.BeginBuild(kParent), parentBuffer);
            Commit(store, kChild, store.BeginBuild(kChild), childBuffer);

            List<ShapedSymbol> output = CollectQuantized(store, q);
            Assert.AreEqual(1, output.Count, "text dedup is unchanged by I6");
            Assert.AreEqual(2, output[0].FeatureIndex, "the finest (child) tile's label wins");
            store.Clear();
        }

        // ═══ Symbol-label perf Phase 1 / Stage 1 (design §4, §5 B): Block dispose lifecycle ═══
        //
        // SymbolTileStore.Entry.Block is held as plain System.IDisposable (pure dispose-once lifetime
        // bookkeeping — see SymbolTileStore's Entry doc); a fake counter stands in for the real
        // native-array-backed SymbolTileBlock in every test below that does NOT also call
        // CaptureSnapshot/CollectInto (which cast to the concrete type — see SymbolTileBlock's Commit
        // helper, used instead wherever a test needs the store to actually collect the tile).

        private sealed class FakeDisposableBlock : IDisposable
        {
            // Mirrors SymbolTileBlock.DebugLiveAllocCount's idiom so the fixture's leak-guard TearDown can
            // catch an abandoned fake commit too — every DisposeCount assertion in this fixture is 0 or 1,
            // so decrementing unconditionally on every Dispose() call stays correct.
            internal static int LiveCount;
            public int DisposeCount;
            public FakeDisposableBlock() => LiveCount++;
            public void Dispose() { DisposeCount++; LiveCount--; }
        }

        // ── (a) Commit-overwrite disposes exactly the OLD block, once — the new one stays live. ──
        [Test]
        public void Block_CommitOverwrite_DisposesOldBlockOnce()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            var oldBlock = new FakeDisposableBlock();
            var newBlock = new FakeDisposableBlock();

            store.CompleteBuild(key, store.BeginBuild(key), oldBlock);
            Assert.AreEqual(0, oldBlock.DisposeCount, "the first commit's block is not disposed yet");

            store.CompleteBuild(key, store.BeginBuild(key), newBlock); // a rebuild commits over it
            Assert.AreEqual(1, oldBlock.DisposeCount, "commit-overwrite disposes the OLD block exactly once");
            Assert.AreEqual(0, newBlock.DisposeCount, "the new block stays live — it is now the entry's block");
            store.Clear();
        }

        // ── A superseded build's block never lands — CompleteBuild disposes the CALLER's block on the
        //    non-commit path (the caller never re-owns it). ──
        [Test]
        public void Block_SupersededCommit_DisposesPassedInBlock()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            int gen1 = store.BeginBuild(key);
            store.BeginBuild(key); // a newer build takes the slot — gen1 is now stale

            var staleBlock = new FakeDisposableBlock();
            bool committed = store.CompleteBuild(key, gen1, staleBlock);
            Assert.IsFalse(committed, "the superseded build must not commit");
            Assert.AreEqual(1, staleBlock.DisposeCount, "…and its block is disposed — never leaked, never landed");
        }

        // ── (b) FIFO eviction over cap disposes the evicted (oldest-released) tile's block. ──
        [Test]
        public void Block_FifoEvictOverCap_DisposesEvictedBlock()
        {
            var store = new SymbolTileStore(cacheCap: 2);
            var a = Key("src", 1); var b = Key("src", 2); var c = Key("src", 3);
            var blockA = new FakeDisposableBlock();
            store.CompleteBuild(a, store.BeginBuild(a), blockA);
            store.CompleteBuild(b, store.BeginBuild(b), new FakeDisposableBlock());
            store.CompleteBuild(c, store.BeginBuild(c), new FakeDisposableBlock());

            store.Release(a, true); // oldest released
            store.Release(b, true);
            Assert.AreEqual(0, blockA.DisposeCount, "still within the cap (2) — not evicted yet");

            store.Release(c, true); // over cap (2) → evicts a (the oldest)
            Assert.AreEqual(1, blockA.DisposeCount, "FIFO eviction disposes the evicted tile's block");
            store.Clear(); // b (cached) and c (active) are still live — evicting only dropped a
        }

        // ── (c) Release true-eviction (cache disabled / not built) disposes the block outright. Covers BOTH
        //    Release branches: the tile still active at release time, and a stale cached copy. ──
        [Test]
        public void Block_ReleaseWithoutCache_FromActive_DisposesBlock()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            var block = new FakeDisposableBlock();
            store.CompleteBuild(key, store.BeginBuild(key), block);

            store.Release(key, transferredToCache: false); // true eviction — the tile was active
            Assert.AreEqual(1, block.DisposeCount, "a true eviction (from active) disposes the block");
        }

        [Test]
        public void Block_ReleaseWithoutCache_FromStaleCached_DisposesBlock()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            var block = new FakeDisposableBlock();
            store.CompleteBuild(key, store.BeginBuild(key), block);
            store.Release(key, transferredToCache: true); // → cached side first

            store.Release(key, transferredToCache: false); // a stale cached copy → dropped (cache-disabled path)
            Assert.AreEqual(1, block.DisposeCount, "a true eviction (from a stale cached copy) disposes the block");
        }

        // ── (d) Clear disposes every ACTIVE and CACHED entry's block. ──
        [Test]
        public void Block_Clear_DisposesActiveAndCachedBlocks()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            var blockA = new FakeDisposableBlock(); var blockB = new FakeDisposableBlock();
            store.CompleteBuild(a, store.BeginBuild(a), blockA);
            store.CompleteBuild(b, store.BeginBuild(b), blockB);
            store.Release(b, true); // b cached, a stays active

            store.Clear();
            Assert.AreEqual(1, blockA.DisposeCount, "Clear disposes the active entry's block");
            Assert.AreEqual(1, blockB.DisposeCount, "Clear disposes the cached entry's block");
        }

        // ── NO dispose on a BeginBuild pull-to-active (stale-survives — the rebuild keeps the old block alive
        //    until its OWN commit, per Block_CommitOverwrite_DisposesOldBlockOnce above). ──
        [Test]
        public void Block_BeginBuildPull_DoesNotDisposeSurvivingBlock()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            var block = new FakeDisposableBlock();
            store.CompleteBuild(key, store.BeginBuild(key), block);

            store.BeginBuild(key); // a rebuild starts (e.g. zoom re-fetch) — pulls the SAME entry active again
            Assert.AreEqual(0, block.DisposeCount, "BeginBuild's stale-survives pull must not dispose the block");
            store.Clear();
        }

        // ── NO dispose on a Restore cache-hit move (the entry — and its block — just moves back to active). ──
        [Test]
        public void Block_RestoreCacheHit_DoesNotDisposeBlock()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            var block = new FakeDisposableBlock();
            store.CompleteBuild(key, store.BeginBuild(key), block);
            store.Release(key, transferredToCache: true); // → cached, block kept warm

            store.Restore(key); // cache-hit move back to active
            Assert.AreEqual(0, block.DisposeCount, "a cache-hit Restore must not dispose the moved block");
            store.Clear();
        }

        // ── Resident-graph shed (4.4b): a structural guard, not a behavioural one — Entry must never re-root a
        //    managed symbol graph. Reflects the private nested Entry type (there is no production seam for this;
        //    reflection into this assembly's internals is the house instrument for a private-type structural
        //    check) and fails the gate if a future edit adds back a List/array-typed field, catching a re-root
        //    of the resident graph BEFORE the [Explicit] GC-mark experiment (which does not run in the gate)
        //    would have been the only thing to notice it. ──
        [Test]
        public void Entry_DeclaresNoManagedCollectionOrReferenceArrayField()
        {
            Type entryType = typeof(SymbolTileStore).GetNestedType("Entry", BindingFlags.NonPublic);
            Assert.IsNotNull(entryType, "SymbolTileStore.Entry must exist — the target of this structural guard");

            foreach (FieldInfo field in entryType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
            {
                Type fieldType = field.FieldType;
                bool isArray = fieldType.IsArray;
                bool isManagedCollection = !isArray && fieldType != typeof(string)
                    && typeof(System.Collections.IEnumerable).IsAssignableFrom(fieldType);
                Assert.IsFalse(isArray || isManagedCollection,
                    $"SymbolTileStore.Entry.{field.Name} is a {fieldType.Name} — a managed collection or " +
                    "reference-array field re-roots the resident label graph 4.4b shed (the former Entry.Symbols " +
                    "managed list of per-label carriers). A per-tile managed collection belongs on the baked " +
                    "native block, never back on this entry.");
            }
        }

        // ═══ Stage 2 (symbols-async-reconcile): interned-id dedup key parity vs an independent STRING oracle ═══
        //
        // The invariant tooth: swapping the dedup key from a string CrossTileSymbolKey to an integer DedupKey
        // (via the store's SymbolStringTable) must produce BYTE-IDENTICAL winners AND emission ORDER (and the
        // plan-aware (BlockId, LocalIndex, IsDeparting) arrays). The oracle below reimplements the dedup with
        // the STRING CrossTileSymbolKey.For — NOT the code under test — so a divergence between the interned
        // path and the string partition fails element-by-element.

        private const double ParityQ = 50.0;

        // UMR-87: ShapedSymbol carries only the INTERNED TextId/IconImageId now — the oracle below must stay
        // string-keyed (independent of SymbolStringTable.Intern, which is exactly what its RED-verification
        // targets), so ParityPoint/ParityCurved stash each symbol's raw text/icon here for Oracle() to read
        // back, instead of a now-nonexistent symbol.Text/.IconImage.
        private readonly Dictionary<ShapedSymbol, string> _parityText = new Dictionary<ShapedSymbol, string>();
        private readonly Dictionary<ShapedSymbol, string> _parityIcon = new Dictionary<ShapedSymbol, string>();

        // Appends one point (or icon, via `icon`) symbol into `buffer` and returns the resulting record.
        private ShapedSymbol ParityPoint(SymbolTileBuffer buffer, double3 anchor, int layer, string text, string icon, int feature, TileId tile)
        {
            TestSymbolTileBuffer.AddPoint(buffer, anchor, null, float2.zero, float2.zero,
                text: text, iconImage: icon, materialIndex: layer, featureIndex: feature, tileKey: SymbolTileKey.Pack(tile));
            ShapedSymbol symbol = buffer.Symbols[buffer.Symbols.Count - 1];
            _parityText[symbol] = text; _parityIcon[symbol] = icon;
            return symbol;
        }

        private ShapedSymbol ParityCurved(SymbolTileBuffer buffer, string text, int feature, TileId tile)
        {
            TestSymbolTileBuffer.AddCurved(buffer, null, null, null,
                placement: SymbolPlacement.LineCenter, text: text, materialIndex: 0, featureIndex: feature, tileKey: SymbolTileKey.Pack(tile));
            ShapedSymbol symbol = buffer.Symbols[buffer.Symbols.Count - 1];
            _parityText[symbol] = text; _parityIcon[symbol] = null;
            return symbol;
        }

        private struct OracleResult
        {
            public List<ShapedSymbol> Output;
            public int ActiveCount;
            public List<int> BlockId;
            public List<int> LocalIndex;
            public List<byte> IsDeparting;
        }

        // Independent string-keyed reimplementation of the store's dedup — the same scan order, the same
        // finest-zoom rule, the same per-tile blockId assignment — but keyed on the STRING CrossTileSymbolKey.
        // activeTiles/departingTiles are in the SAME order the store enumerates _active / _departing.
        // Stage 3: the oracle grids on the fixed CrossTileSymbolKey.CanonicalGridMeters (mirroring the store, which
        // no longer takes a caller grid) — so parity holds by construction, still proving interned-key ==
        // string-key under the SAME grid. `q` is passed through unchanged as the store's dedup GATE (magnitude
        // irrelevant now); it is not the oracle grid. UMR-87: reads `_parityText`/`_parityIcon` (ParityPoint/
        // ParityCurved's side table), never symbol.TextId/IconImageId — see the field doc above.
        private OracleResult Oracle(
            List<List<ShapedSymbol>> activeTiles, List<List<ShapedSymbol>> departingTiles, double q)
        {
            var output = new List<ShapedSymbol>();
            var blockIds = new List<int>();
            var localIndices = new List<int>();
            var isDeparting = new List<byte>();
            var dedup = new Dictionary<CrossTileSymbolKey, (ShapedSymbol symbol, int z, long tileKey, int blockId, int localIndex)>();
            int nextBlock = 0;

            foreach (List<ShapedSymbol> tile in activeTiles)
            {
                if (tile == null) continue;
                int myBlock = nextBlock++;
                for (int i = 0; i < tile.Count; i++)
                {
                    ShapedSymbol symbol = tile[i];
                    if (symbol.Placement != SymbolPlacement.Point)
                    {
                        output.Add(symbol); blockIds.Add(myBlock); localIndices.Add(i); isDeparting.Add(0);
                        continue;
                    }
                    var key = CrossTileSymbolKey.For(symbol.AnchorRender, symbol.MaterialIndex, _parityText[symbol], _parityIcon[symbol], CrossTileSymbolKey.CanonicalGridMeters);
                    int z = (int)(symbol.TileKey >> 44);
                    if (!dedup.TryGetValue(key, out var cur) || z > cur.z || (z == cur.z && symbol.TileKey < cur.tileKey))
                        dedup[key] = (symbol, z, symbol.TileKey, myBlock, i);
                }
            }
            foreach (var kv in dedup)
            {
                output.Add(kv.Value.symbol); blockIds.Add(kv.Value.blockId); localIndices.Add(kv.Value.localIndex); isDeparting.Add(0);
            }
            int activeCount = output.Count;

            foreach (List<ShapedSymbol> tile in departingTiles)
            {
                if (tile == null) continue;
                int myBlock = nextBlock++;
                for (int i = 0; i < tile.Count; i++)
                {
                    ShapedSymbol symbol = tile[i];
                    if (symbol.Placement == SymbolPlacement.Point)
                    {
                        var key = CrossTileSymbolKey.For(symbol.AnchorRender, symbol.MaterialIndex, _parityText[symbol], _parityIcon[symbol], CrossTileSymbolKey.CanonicalGridMeters);
                        if (dedup.ContainsKey(key)) continue;
                        dedup[key] = (symbol, 0, symbol.TileKey, myBlock, i);
                    }
                    output.Add(symbol); blockIds.Add(myBlock); localIndices.Add(i); isDeparting.Add(1);
                }
            }

            return new OracleResult
            {
                Output = output, ActiveCount = activeCount,
                BlockId = blockIds, LocalIndex = localIndices, IsDeparting = isDeparting,
            };
        }

        // ── THE tooth: a multi-tile fixture exercising every dedup path, asserted element-by-element (winners
        //    AND order AND the plan arrays) against the independent string oracle. RED-verified by making
        //    SymbolStringTable.Intern return a constant (collapsing distinct text/icons → spurious merges). ──
        [Test]
        public void InternedDedup_MatchesStringOracle_MultiTile_DuplicateTextAcrossTiles()
        {
            // Cells: each coordinate is a multiple of 4 (a CanonicalGridMeters=4 cell centre), thousands of metres
            // apart, so they stay in distinct fixed-grid cells and ≤1 m of jitter stays in-cell.
            double3 cellShared = new double3(ParityQ * 100.0, 0, ParityQ * 100.0);
            double3 cellB = new double3(ParityQ * 200.0, 0, ParityQ * 200.0);
            double3 cellIcon = new double3(ParityQ * 300.0, 0, ParityQ * 300.0);
            double3 cellUnique = new double3(ParityQ * 400.0, 0, ParityQ * 400.0);

            var tileP = new TileId { Z = 10, X = 500, Y = 400 };   // parent
            var tileC = new TileId { Z = 11, X = 1000, Y = 800 };  // child of P
            var tileM = new TileId { Z = 12, X = 3, Y = 4 };       // the "many kinds" tile
            var tileDep = new TileId { Z = 11, X = 1000, Y = 801 }; // departing

            // (a) same text + same cell across two tiles at different z → merge, finest z (child) wins. The 1 m
            // jitter is well inside the 4 m cell (unlike +2 m, which lands on the cell boundary at 5002).
            var pSymbols = new SymbolTileBuffer();
            var parentShared = ParityPoint(pSymbols, cellShared + new double3(1, 0, 1), 0, "Shared", null, 1, tileP);
            var cSymbols = new SymbolTileBuffer();
            var childShared = ParityPoint(cSymbols, cellShared, 0, "Shared", null, 2, tileC);
            // (d) a curved symbol rides through undeduped; (b) distinct text same cell; (c) distinct icons co-located.
            var mSymbols = new SymbolTileBuffer();
            var curved = ParityCurved(mSymbols, "Road", 3, tileM);
            var bAlpha = ParityPoint(mSymbols, cellB, 0, "Alpha", null, 4, tileM);
            var bBeta = ParityPoint(mSymbols, cellB, 0, "Beta", null, 5, tileM);         // distinct text, same cell → no merge
            var icoA = ParityPoint(mSymbols, cellIcon, 0, null, "ico-a", 6, tileM);
            var icoB = ParityPoint(mSymbols, cellIcon, 0, null, "ico-b", 7, tileM);       // distinct icon, same cell → no merge
            // (e) a departing entry: one symbol shares the active winner's identity (skipped), one is unique (appended).
            var depSymbols = new SymbolTileBuffer();
            var depShared = ParityPoint(depSymbols, cellShared, 0, "Shared", null, 8, tileDep); // claimed by active → skipped
            var depUnique = ParityPoint(depSymbols, cellUnique, 0, "Unique", null, 9, tileDep); // not claimed → appended departing

            var store = new SymbolTileStore(cacheCap: 16);
            var kP = new SymbolTileStore.Key("src", tileP);
            var kC = new SymbolTileStore.Key("src", tileC);
            var kM = new SymbolTileStore.Key("src", tileM);
            var kDep = new SymbolTileStore.Key("src", tileDep);

            // BeginBuild order fixes _active insertion (enumeration) order: P, C, M, Dep.
            Commit(store, kP, store.BeginBuild(kP), pSymbols);
            Commit(store, kC, store.BeginBuild(kC), cSymbols);
            Commit(store, kM, store.BeginBuild(kM), mSymbols);
            Commit(store, kDep, store.BeginBuild(kDep), depSymbols);
            // Release Dep (not in the loaded set) with grace → cached + departing; P/C/M stay active in order.
            store.ReconcileActiveSet(
                new List<SymbolTileStore.Key> { kP, kC, kM },
                keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            Assert.AreEqual(3, store.ActiveTileCount, "P, C, M active");
            Assert.AreEqual(1, store.DepartingTileCount, "Dep is departing");

            OracleResult oracle = Oracle(
                new List<List<ShapedSymbol>> { pSymbols.Symbols, cSymbols.Symbols, mSymbols.Symbols },
                new List<List<ShapedSymbol>> { depSymbols.Symbols },
                ParityQ);

            // Precondition: the fixture actually exercises the paths (not a degenerate all-merge/all-distinct set).
            // ShapedSymbol's default structural equality (no field is a reference type other than the interned
            // strings) makes Contains() a genuine per-field comparison against the exact record each var captured.
            Assert.IsTrue(oracle.Output.Contains(childShared), "sanity: the child (finest) copy is the shared-cell winner");
            Assert.IsFalse(oracle.Output.Contains(parentShared), "the coarser parent copy loses the merge");
            Assert.IsFalse(oracle.Output.Contains(depShared), "the departing copy of the shared identity is skipped");
            Assert.IsTrue(oracle.Output.Contains(bAlpha) && oracle.Output.Contains(bBeta), "distinct text both survive");
            Assert.IsTrue(oracle.Output.Contains(icoA) && oracle.Output.Contains(icoB), "distinct icons both survive");

            // Reader cutover (4.2): the only surviving CollectInto overload is the plan-aware one, and it has no
            // managed symbol list to hand back — winner identity is (BlockId, LocalIndex). Comparing these
            // element-by-element against the INDEPENDENT string oracle's own (blockId, localIndex) arrays IS the
            // parity check (both assign blockId in the SAME per-tile scan order, localIndex == raw list position
            // by the null-slot invariant) — not a restatement of anything CollectInto itself computed.
            var pBlock = new List<int>();
            var pLocal = new List<int>();
            var pDep = new List<byte>();
            store.CollectInto(pBlock, pLocal, pDep, ParityQ, out int planActive);
            Assert.AreEqual(oracle.Output.Count, pBlock.Count, "plan-aware total count matches the oracle");
            Assert.AreEqual(oracle.ActiveCount, planActive, "plan-aware active split matches the oracle");
            for (int i = 0; i < oracle.Output.Count; i++)
            {
                Assert.AreEqual(oracle.BlockId[i], pBlock[i], $"blockId mismatch at index {i}");
                Assert.AreEqual(oracle.LocalIndex[i], pLocal[i], $"localIndex mismatch at index {i}");
                Assert.AreEqual(oracle.IsDeparting[i], pDep[i], $"isDeparting mismatch at index {i}");
            }
            store.Clear();
        }

        // ═══ Stage 4a (symbols-async-reconcile): collect-generation invalidation completeness + purity ═══
        //
        // The memo (SymbolSubsystem.CurrentBatch reuses its collected buffers when CollectGeneration is
        // unchanged) is only correct if EVERY collect-relevant mutation bumps the generation (a missed bump = stale
        // symbols on screen) AND a stable cover / no-op mutation does NOT bump (else the memo never fires). These pin
        // both directions + the purity premise the reuse rests on. Each Y case is its own assertion — omit that one
        // MarkCollectDirty() and the case goes RED (the parameterized completeness test IS the RED harness).

        // The Y rows of the invalidation map (§1) — each MUST bump the collect generation.
        public enum BumpCase
        {
            BeginBuildNew,               // §1 #1 — first-seen tile pulled active
            BeginBuildRefetchCached,     // §1 #1 — re-fetch of a cached/departing tile (symbols move to active)
            CompleteBuildCommit,         // §1 #2 — symbol content lands
            ReleaseTrue,                 // §1 #3 — active tile released to warm cache
            ReleaseFalseTrueEvict,       // §1 #3b — active tile true-evicted (cache disabled)
            ReleaseFalseStaleCachedDrop, // §1 #3c — a stale cached copy dropped
            RestoreReal,                 // §1 #4 — cached → active (cache hit)
            PurgeExpiredDeparting,       // §1 #7 — an expired departing key removed
            Clear,                       // §1 #8 — whole set → empty
        }

        [Test]
        public void CollectGeneration_BumpsOnEveryMutation([Values] BumpCase mutation)
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            int g0;
            switch (mutation)
            {
                case BumpCase.BeginBuildNew:
                    g0 = store.CollectGeneration;
                    store.BeginBuild(key);
                    break;
                case BumpCase.BeginBuildRefetchCached:
                    Commit(store, key, store.BeginBuild(key), Symbols(1));
                    store.Release(key, transferredToCache: true); // → cached
                    g0 = store.CollectGeneration;
                    store.BeginBuild(key); // re-fetch pulls the cached tile (with its symbols) back onto the active side
                    break;
                case BumpCase.CompleteBuildCommit:
                    int gen = store.BeginBuild(key);
                    g0 = store.CollectGeneration;
                    Commit(store, key, gen, Symbols(1)); // commit path
                    break;
                case BumpCase.ReleaseTrue:
                    Commit(store, key, store.BeginBuild(key), Symbols(1));
                    g0 = store.CollectGeneration;
                    store.Release(key, transferredToCache: true);
                    break;
                case BumpCase.ReleaseFalseTrueEvict:
                    Commit(store, key, store.BeginBuild(key), Symbols(1));
                    g0 = store.CollectGeneration;
                    store.Release(key, transferredToCache: false); // active branch, true eviction
                    break;
                case BumpCase.ReleaseFalseStaleCachedDrop:
                    Commit(store, key, store.BeginBuild(key), Symbols(1));
                    store.Release(key, transferredToCache: true); // → cached (stale copy)
                    g0 = store.CollectGeneration;
                    store.Release(key, transferredToCache: false); // stale-cached-drop branch
                    break;
                case BumpCase.RestoreReal:
                    Commit(store, key, store.BeginBuild(key), Symbols(1));
                    store.Release(key, transferredToCache: true); // → cached
                    g0 = store.CollectGeneration;
                    store.Restore(key); // cache hit
                    break;
                case BumpCase.PurgeExpiredDeparting:
                    Commit(store, key, store.BeginBuild(key), Symbols(1));
                    store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
                    Assert.AreEqual(1, store.DepartingTileCount, "precondition: the tile is departing");
                    g0 = store.CollectGeneration;
                    // Second reconcile past the grace window: nothing releases/restores (active empty, loaded empty), so
                    // the ONLY collect-relevant change is PurgeExpiredDeparting removing the expired departing key.
                    store.ReconcileActiveSet(Loaded(), keepWarmOnRelease: true, nowSeconds: 11.0, departingGraceSeconds: 0.5);
                    Assert.AreEqual(0, store.DepartingTileCount, "precondition: the departing key was purged");
                    break;
                case BumpCase.Clear:
                    Commit(store, key, store.BeginBuild(key), Symbols(1));
                    g0 = store.CollectGeneration;
                    store.Clear();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
            Assert.AreNotEqual(g0, store.CollectGeneration,
                $"{mutation} changes the collected set → it MUST bump the collect generation (a missed bump = stale labels)");
            store.Clear(); // some BumpCase branches leave a real block committed/cached — always safe to Clear
        }

        // The N rows of §1 — a no-op / superseded mutation must NOT bump (else a stable cover dirties every frame and
        // the memo never fires). ReconcileActiveSet idempotency is elevated to its own headline tooth below.
        public enum NoBumpCase
        {
            CompleteBuildSuperseded, // §1 #2b — a stale build's commit is discarded, no state change
            RestoreNoOp,             // §1 #4b — nothing cached to restore
            ReleaseNoOpEdge,         // §1 #3d — key neither active nor cached, transferredToCache=true
        }

        [Test]
        public void CollectGeneration_HoldsOnNoOpMutation([Values] NoBumpCase mutation)
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var key = Key("src", 1);
            int g0;
            switch (mutation)
            {
                case NoBumpCase.CompleteBuildSuperseded:
                    int gen1 = store.BeginBuild(key);
                    store.BeginBuild(key); // a newer build supersedes gen1
                    g0 = store.CollectGeneration;
                    Assert.IsFalse(Commit(store, key, gen1, Symbols(1)), "precondition: the stale build is discarded");
                    break;
                case NoBumpCase.RestoreNoOp:
                    g0 = store.CollectGeneration;
                    store.Restore(key); // nothing cached → no move
                    break;
                case NoBumpCase.ReleaseNoOpEdge:
                    g0 = store.CollectGeneration;
                    store.Release(key, transferredToCache: true); // not active, transferredToCache=true → neither branch
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
            }
            Assert.AreEqual(g0, store.CollectGeneration,
                $"{mutation} changes no collected state → it must NOT bump (a stable cover must not dirty the memo)");
        }

        // ── THE headline tooth: ReconcileActiveSet runs EVERY frame; on a stable loaded set it moves nothing and MUST
        //    NOT bump — else the memo dirties every frame and the entire CPU win silently evaporates (a regression here
        //    leaves the render byte-identical, caught by nothing else but this + the subsystem's recompute counter). ──
        [Test]
        public void ReconcileActiveSet_IdempotentCall_DoesNotBumpGeneration()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2);
            Commit(store, a, store.BeginBuild(a), Symbols(1));
            Commit(store, b, store.BeginBuild(b), Symbols(2));

            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);
            int g0 = store.CollectGeneration;
            // Same loaded set, a later `now` still inside any grace (nothing to purge — nothing is departing anyway).
            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true, nowSeconds: 10.2, departingGraceSeconds: 0.5);
            Assert.AreEqual(g0, store.CollectGeneration,
                "a second reconcile with the SAME loaded set moves nothing → the collect generation must be unchanged");
            Assert.AreEqual(2, store.ActiveTileCount, "…and both tiles stay active");
            store.Clear();
        }

        // ── CollectInto is a PURE function of the tile set (Stage 3): two back-to-back collects with no mutation
        //    between produce element-identical outputs (+ reference-identical OrderedBlocks) — the premise the memo's
        //    clean-frame reuse rests on (gen-unchanged ⇒ state-unchanged ⇒ collect-identical ⇒ safe to reuse). ──
        // Reader cutover: this test calls the plan-aware CollectInto, which runs CaptureSnapshot — a FakeDisposableBlock
        // committed here would throw at CaptureSnapshot's hard cast, so every block under test is a REAL baked one
        // (the count/identity assertions below hold regardless — a real block is still one distinct reference per
        // tile, so the OrderedBlocks reference-identity check is unaffected).
        [Test]
        public void CollectInto_IsPureFunction_TwoCallsNoMutation_ElementIdentical()
        {
            var store = new SymbolTileStore(cacheCap: 8);
            var a = Key("src", 1); var b = Key("src", 2); var dep = Key("src", 3);
            Commit(store, a, store.BeginBuild(a), Symbols(1));
            Commit(store, b, store.BeginBuild(b), Symbols(2));
            Commit(store, dep, store.BeginBuild(dep), Symbols(3));
            // dep leaves cover with grace → departing (exercises the AppendDeparting branch too); a/b stay active.
            store.ReconcileActiveSet(Loaded(a, b), keepWarmOnRelease: true, nowSeconds: 10.0, departingGraceSeconds: 0.5);

            var blk1 = new List<int>(); var loc1 = new List<int>(); var d1 = new List<byte>();
            store.CollectInto(blk1, loc1, d1, ParityQ, out int active1);
            var blocks1 = new List<SymbolTileBlock>(store.OrderedBlocks); // snapshot before the second collect refills it

            var blk2 = new List<int>(); var loc2 = new List<int>(); var d2 = new List<byte>();
            store.CollectInto(blk2, loc2, d2, ParityQ, out int active2);

            Assert.AreEqual(active1, active2, "same active split");
            Assert.AreEqual(blk1.Count, blk2.Count, "same emitted count");
            Assert.Greater(blk1.Count, 0, "sanity: the fixture actually emits labels");
            for (int i = 0; i < blk1.Count; i++)
            {
                Assert.AreEqual(blk1[i], blk2[i], $"blockId identical at {i}");
                Assert.AreEqual(loc1[i], loc2[i], $"localIndex identical at {i}");
                Assert.AreEqual(d1[i], d2[i], $"isDeparting identical at {i}");
            }
            Assert.AreEqual(blocks1.Count, store.OrderedBlocks.Count, "OrderedBlocks count identical");
            for (int i = 0; i < blocks1.Count; i++)
                Assert.AreSame(blocks1[i], store.OrderedBlocks[i], $"OrderedBlocks[{i}] reference-identical");
            store.Clear();
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TextQuadLayoutAllocTests — the steady single-line no-wrap layout hot path allocates zero garbage
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class TextQuadLayoutAllocTests
    {
        // =========================================================================================
        // Steady no-wrap path: a short run with no whitespace at all -- the word-wrap lookahead
        // (MeasureRange) branch is never even entered, so this isolates the plain per-glyph placement
        // + per-line justify bake-in + final block-shift pass, all of which are in-place List writes.
        // =========================================================================================
        [Test]
        public void Layout_SteadyNoWrapPath_IntoCallerBuffer_AllocatesNoGCMemory()
        {
            var atlas = new GlyphAtlas();
            for (uint codepoint = 1; codepoint <= 5; codepoint++)
            {
                atlas.Append(MakeSyntheticGlyph(codepoint, width: 10, height: 12, advance: 14), 0);
            }

            var glyphs = new List<PositionedGlyph>();
            for (uint codepoint = 1; codepoint <= 5; codepoint++)
            {
                glyphs.Add(new PositionedGlyph { AtlasCodepoint = codepoint, XAdvance = 14f, Cluster = (int)codepoint - 1 });
            }
            var run = new ShapedRun { Glyphs = glyphs, Direction = TextDirection.LeftToRight };

            var options = new TextLayoutOptions
            {
                Anchor = TextAnchor.Center,
                Offset = float2.zero,
                RadialOffset = 0f,
                Justify = TextJustify.Auto,
                MaxWidthEm = 10f, // default -- wide enough that this 5-glyph run never wraps
                LineHeightEm = 1.2f,
                LetterSpacingEm = 0f,
            };
            var output = new List<SymbolQuad>(8);

            // Warm-up: the FIRST call legitimately allocates (List<T>'s backing array grows from
            // empty). Stabilizes `output`'s capacity so the measured call below reuses it.
            TextQuadLayout.Layout(run, atlas, in options, output);

            // Block-bodied lambda (not an expression lambda): the overload returns a value
            // (TextLayoutBounds), and Assert.That needs a void TestDelegate here.
            Assert.That(() => { TextQuadLayout.Layout(run, atlas, in options, output); }, Is.Not.AllocatingGCMemory(),
                "Layout(..., output) must not allocate on the steady no-wrap path once `output`'s capacity " +
                "has stabilized from the warm-up call -- no whitespace glyph in this run means the word-wrap " +
                "lookahead branch (MeasureRange) is never entered at all.");
        }

        private static SdfGlyph MakeSyntheticGlyph(uint codepoint, int width, int height, int advance)
        {
            int2 cellSize = new int2(width + 2 * GlyphSdf.Buffer, height + 2 * GlyphSdf.Buffer);
            return new SdfGlyph
            {
                Codepoint = codepoint,
                Width = width,
                Height = height,
                Left = 0,
                Top = 0,
                Advance = advance,
                Bitmap = new byte[cellSize.x * cellSize.y],
            };
        }
    }
}
