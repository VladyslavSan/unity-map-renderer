// Text/SymbolTileStoreTests.cs — the symbol subsystem pump (worker/tail split), its work scheduler, SymbolTileStore's warm/evict lifecycle, and a text-quad-layout allocation regression pin.
//
// The three pump-related fixtures first (subsystem pump, work scheduler, tail pump), then the store lifecycle fixture, then the standalone allocation regression pin.
//
// Contents:
//   SymbolSubsystemPumpTests           — the symbol subsystem builds a fetched tile off the main thread and does
//                                        not re-upload the 16 MB glyph atlas per glyph-adding tile.
//   SymbolSubsystemWorkSchedulerTests  — ReconcileDispatch_* guards the WebGL-only permanent placement wedge: under Inline the pickup lands ONE CurrentBatch call later than the schedule (PickupCompletedReconcile runs BEFORE ScheduleReconcileIfDirty inside CurrentBatch), so the tooth…
//   SymbolTailPumpTests                — The acceptance teeth for the worker-phase / tail split — the worker phase (TryBeginBuild's returned pass) stops after the pool-side extract and hands a ready tail (via the pool→main handoff) to PumpBuilds' budgeted tail-start loop…
//   SymbolTileStoreTests               — The symbol-lifecycle fix (zoom-out-then-in "no symbols" bug): SymbolTileStore keeps a released-to-cache tile's symbols WARM and restores them on a prepared-cache hit (which does not re-fetch), while a truly-evicted tile drops them.
//   TextQuadLayoutAllocTests           — The layout hot path (steady, single-line, no-wrap) must allocate ZERO managed garbage once the caller's output List capacity has stabilized -- see TextQuadLayout's class doc for why (an in-place List index write per emitted quad, no auxiliary…

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
    /// The symbol subsystem builds a fetched tile off the main thread and does not re-upload the 16 MB glyph
    /// atlas per glyph-adding tile. <see cref="SymbolSubsystem.TryBeginBuild"/> starts the worker phase and the
    /// pass's <c>RunWorkerAndHandoff</c> runs it on the pool. <see cref="SymbolSubsystem.PumpBuilds"/> starts
    /// at most <c>MaxBuildsPerFrame</c> ready tails per frame and makes at most ONE coalesced atlas upload.
    /// The build-start throttle and stale-drop teeth live in TileSymbolKickTests.
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

        // ONE symbol layer on source "s" (source-layer "centroids", as in the fixture tile) plus a glyphs URL so
        // SetStyle wires the glyph pipeline. GlyphSourceFactoryOverride injects the source; the URL is never fetched.
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

            // The subsystem owns no MapMaterialSet (per-layer materials live on SymbolRenderLayer), so this
            // suite tests build/pump/atlas behaviour only.
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

        /// <summary>Drive helper — mirrors TileManager's kick: <c>TryBeginBuild</c> on the (test) main
        /// thread, then <c>RunWorkerAndHandoff</c> fire-and-forget on the pool (so
        /// <c>SymbolDecodeAndExtract_RunOffTheMainThread</c> still observes the decode/extract marker off
        /// main). Replaces the retired <c>OnTileBytesReady</c> push.</summary>
        private void DriveTileBytesReady(TileId tile)
        {
            ISymbolTileWorkerPass pass = _subsystem.TryBeginBuild(SourceId, tile);
            if (pass == null) return; // mirrors OnTileBytesReady's no-op guard (no _builder / no layers for source)
            // Mirrors TileManager.KickMeshBuild: the pool decodes the tile and the kick owns the lease's ONE reference.
            // Its `finally` releases it, which frees the decoded buffers unless a parked build acquired its own.
            byte[] bytes = _tileBytes;
            UniTask.RunOnThreadPool(() =>
            {
                var decode = new SharedDisposable<IDecodedTile>(new MvtTileDecoder().Decode(tile, bytes));
                try { pass.RunWorkerAndHandoff(decode); }
                finally { decode.Release(); }
            }).Forget();
        }

        // Production SetStyle does not walk style.Layers (RenderLayerFactory is the sole registry, MapView derives
        // the list), so direct subsystem tests need this local extraction, outside the grep tooth's scope.
        private static List<Symbol.StyleLayer> ExtractSymbolLayers(StyleDocument style)
        {
            var result = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) result.Add(symbol);
            return result;
        }

        // ── The build-start throttle lives in TileManager's kick cap (MaxMeshBuildsPerTick), covered by
        //    TileSymbolKickTests. This subsystem owns no build-start queue to bound.

        // ── The atlas-upload, off-main-extract, cancellation and departing-flag teeth live in the PlayMode
        //    SymbolSubsystemPumpTests: each waits on off-main work, and an EditMode yield is instantaneous.

        // ── Stale-drop before build start cannot occur here: TileManager's _releaseQueued check stops the kick
        //    for a departed tile ahead of TryBeginBuild. TileSymbolKickTests pins that no TryBeginBuild runs.

        // ── Telemetry provider (docs/telemetry-design.md): the subsystem OWNS the store's levels as a struct
        //    field, refreshes it at the end of CurrentBatch and hands it out BY REFERENCE; nothing subscribes. ──
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

        // ── PlayMode SymbolSubsystemPumpTests holds CurrentBatch_TileLeftCover_FlagsRecordsDeparting (departing
        //    split) and CurrentBatch_TileEvent_RecomputesAndDrivesFade: each drives an async build across frames.

        // ── Alloc tooth: SymbolTileCoverageFilter.FilterActive's Dictionary/List compaction must not allocate once
        //    warm. A non-zero minCoverage runs the real filter path, not its no-op guard. ──
        [UnityTest]
        public IEnumerator CurrentBatch_Warm_WithCoverageCullEnabled_AllocatesNoGCMemory()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };
            DriveTileBytesReady(tile);

            // A VALID (identity-rebase) frame: `default`'s zero Rebase collapses the tile to a zero-area quad and culls
            // everything. A tiny POSITIVE threshold runs the real filter path yet keeps any well-formed on-screen tile.
            var frame = SceneFrame.Mercator(new double2(0.0, 0.0));
            const double keepAllButRunFilter = 1e-9;

            // Measure only a FULLY quiescent frame (no pending tail, no reconcile in flight, recompute count stable
            // for 2 frames): a dirty frame legitimately allocates when it captures and kicks a worker.
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

            // Warm up once, then measure the steady call. The lambda needs a block body: an expression lambda binds
            // NUnit's `Assert.That<T>(Func<T>)` overload and throws "the actual value must be a TestDelegate".
            _subsystem.CurrentBatch(frame, keepAllButRunFilter);
            Assert.That(() => { _subsystem.CurrentBatch(frame, keepAllButRunFilter); },
                Is.Not.AllocatingGCMemory(),
                "a steady-state CurrentBatch (coverage cull + native winner-plan build) must allocate ZERO managed garbage");
        }

        // ═══ Memoized clean-frame reuse of the collected set ═══════════════════════════════════════

        // ── Clean frames (no tile event) reuse the collected set byte-identically, with no recompute and zero GC.
        //    Non-local invariant: a clean frame neither schedules nor picks up a reconcile, so the FRONT result
        //    stands; ScheduleReconcileIfDirty's `CollectGeneration == _reconcileScheduledGen` guard holds that.
        //    minCoverage 0 makes ClassifyActive early-return, so the plan is frame-independent. ──
        [UnityTest]
        public IEnumerator CurrentBatch_CleanFrames_ReuseCollectedSet_ByteIdentical_RecomputesOnce()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };

            // Drive to FULL reconcile quiescence: only then does a frame neither schedule nor pick up, which the
            // byte-identical-reuse and zero-GC asserts below need.
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

            // The counter must be LIVE: the cold-start collect recomputes at least once. Without this, a dead counter
            // stuck at 0 would pass the "held flat" assert below at 0 == 0.
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

        // Text-only, no `sprite` key: the reconcile tooth commits its tile through the store, so this keeps
        // FetchSpriteSheetAsync from constructing a REAL ISpriteSource against the invalid URL.
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
        // SpritesSettled's deadline clock: frozen at construction, then advanced PAST SpriteFetchDeadlineSeconds to
        // trip the deadline fallback without depending on a real UniTask continuation resuming.
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

        // ── PumpBuilds' parked-build drain ────────────────────────────────────────────────────────────

        /// <summary>Guards the WebGL-only unrecoverable decode leak. <c>PumpBuilds</c>' parked-queue drain
        /// dispatches through <see cref="SymbolSubsystem.WorkScheduler"/>. Under <see cref="InlineWorkScheduler"/>
        /// the dispatched body, the ONLY release of the park's decode reference, runs synchronously on the
        /// calling thread before <c>PumpBuilds</c> returns.</summary>
        /// <remarks>Non-obvious why: the test reads the release, not an empty queue. Under Inline the entry
        /// leaves <c>_pendingSpriteQueue</c> before <c>Schedule</c> runs, so an empty queue proves nothing. A
        /// read of <see cref="LeaseProbeDecoder.DisposedCount"/> with no yield after <c>PumpBuilds()</c> passes
        /// only if the body ran INSIDE the call, not later on the pool.</remarks>
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

            // Trip SpritesSettled via its DEADLINE fallback, not by resolving `gate`: that is synchronous and needs
            // no real UniTask continuation to resume (the gated fetch stays pending).
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
                "immediately after PumpBuilds() returns. This is the leak this test exists to close: the dispatched " +
                "body's `finally` is the ONLY release for this reference, so a scheduler that failed to run " +
                "it (or ran it later, off this call) leaks it exactly as UniTask.RunOnThreadPool does on web.");
            Assert.AreEqual(0, probe.UnbalancedCount, "…exactly once — not a double release.");
            Assert.AreEqual(0, _subsystem.PendingSpriteCount(), "sanity: the queue drained.");

            gate.TrySetResult(new SpriteResponse { HasData = false }); // tidy: never leave a gate hanging
        }

        // ── ScheduleReconcileIfDirty — the cross-tile reconcile ───────────────────────────────────────

        /// <summary>Guards the WebGL-only permanent placement wedge. <c>ScheduleReconcileIfDirty</c>
        /// dispatches through <see cref="SymbolSubsystem.WorkScheduler"/>. Under Inline the reconcile body runs
        /// synchronously on the calling thread, inside the SAME <c>CurrentBatch</c> call that scheduled it.</summary>
        /// <remarks>Non-local invariant: <c>CurrentBatch</c> runs <c>PickupCompletedReconcile</c> BEFORE
        /// <c>ScheduleReconcileIfDirty</c>, so even an Inline reconcile is picked up on the NEXT call, and
        /// <c>_reconcileInFlight</c> stays true across the frame that scheduled it. Hence two calls.</remarks>
        [Test]
        public void ReconcileDispatch_ThroughWorkScheduler_ClearsInFlight_AndPicksUpOneFrameLater_UnderInline()
        {
            int caller = Environment.CurrentManagedThreadId;
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            _subsystem = new SymbolSubsystem(_mapCamera);
            _subsystem.GlyphSourceFactoryOverride = _ => TestGlyphSource.FromRanges(ranges);
            StyleDocument style = StyleParser.Parse(TextOnlyStyleJson);
            _subsystem.SetStyle(style, ExtractSymbolLayers(style));

            // Commit a tile directly through the store (no MVT decode, no build tail): that dirties CollectGeneration
            // and drives ScheduleReconcileIfDirty on the next CurrentBatch. Only the RECONCILE dispatch is under test.
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

        // ── Glyph-fetch hoist: a cancel at the glyph-prepare await ─────────────────────────────────

        // Non-obvious why: a literal text-field (no '{') resolves VERBATIM for every "centroids" feature, and 'A'
        // + U+0628 is single-run bidi, which CodepointTextShaper rejects (NotSupportedException). That throw makes
        // "the shape loop ran" observable in the glyph-prepare cancel tooth below. It is a BORROWED
        // precondition: Precondition_ShapingTheMixedDirectionTextStillThrows pins it.
        private const string MixedDirectionText = "Aب";
        private static readonly string MixedDirectionStyleJson = (@"{
            'version': 8,
            'glyphs': 'https://example.invalid/{fontstack}/{range}.pbf',
            'layers': [
                { 'id':'labels', 'type':'symbol', 'source':'s', 'source-layer':'centroids',
                  'layout': { 'text-field':'" + MixedDirectionText + @"', 'text-size':16, 'text-font':['LatinFont'] } }
            ]
        }").Replace('\'', '"');

        /// <summary><c>RunTailAsync</c> suspends in the build-wide glyph-range ensure step, BEFORE the shape
        /// loop, and needs its own cancellation guard there. A GATED glyph source parks the tail in that step, a
        /// restyle cancels the build, and the gate then releases NORMALLY. Only the pre-loop
        /// <c>tail.Ct.ThrowIfCancellationRequested()</c> then stops the cancelled token reaching the loop.</summary>
        /// <remarks>Non-obvious why: a cancelled release throws from the await and hides the pre-loop check, and the
        /// trailing ct check before commit yields the same final outcome. Shape's per-symbol catch filter is false on a
        /// cancelled <c>ct</c>, so if the loop runs, the <see cref="MixedDirectionText"/> throw reaches the generic
        /// catch and <see cref="SymbolSubsystem.CancelledBuildCount"/> stays 0. The tooth goes vacuous if that text
        /// ever shapes; <see cref="Precondition_ShapingTheMixedDirectionTextStillThrows"/> pins it.</remarks>
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
            // Release the gate NORMALLY: EnsureGlyphRangesAsync returns cleanly, so only the pre-loop ct check
            // under test can stop this cancelled build before the shape loop.
            gate.TrySetResult(new GlyphRangeResponse(_latinGlyphs));

            Assert.AreEqual(1, _subsystem.CancelledBuildCount,
                "the pre-loop ct check must fire BEFORE the shape loop — if the shape loop ran instead, the " +
                "mixed-direction throw would escape into the generic catch and this would stay 0.");
            Assert.IsNull(_subsystem.Store().DebugBlockFor(key), "a cancelled ensure step must commit nothing.");
            Assert.AreEqual(1, probe.DisposedCount, "…still exactly once — the tail's cancel path touches no decode.");
            Assert.AreEqual(0, probe.UnbalancedCount, "no double release / leak on the cancel path.");
        }

        /// <summary>Precondition for
        /// <see cref="CancelDuringGlyphPrepare_UnwindsBeforeShapeOrCommit_ReleasesTheDecodeExactlyOnce"/>:
        /// shaping <see cref="MixedDirectionText"/> throws (CodepointTextShaper's single-run-bidi rejection), and
        /// that tooth's discriminator depends on it. If this goes red, both unwind paths of that tooth reach the
        /// same <c>CancelledBuildCount</c>, so it is hollow and needs a new discriminator.</summary>
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

        // Two style layers on the SAME source-layer with DIFFERENT text-font names give two distinct (fontName,
        // rangeStart) keys. Both use MixedDirectionText, so each shaped symbol bumps builder.SkippedSymbolCount.
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

        /// <summary>The fetch-precedes-shape claim, driven through the PRODUCTION dispatch path
        /// (<c>TryBeginBuild</c> → <c>RunWorkerAndHandoff</c> → <c>PumpBuilds</c> → <c>RunTailAsync</c>). The
        /// <c>BuildAsync</c> path that
        /// <see cref="GlyphPrepareBeforeShapeTests.EveryGlyphFetchPrecedesTheFirstShapedSymbol"/> drives has ZERO
        /// production callers. A collect+ensure+shape interleave per processor fails this test.</summary>
        /// <remarks>Non-obvious why: a test cannot see the production tail's buffer, so
        /// <see cref="StyledSymbolTileBuilder.SkippedSymbolCount"/>
        /// (via <see cref="SymbolSubsystemTestExtensions.Builder"/>) is the "a symbol was shaped" signal:
        /// every <see cref="MixedDirectionText"/> shape attempt increments it.</remarks>
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

            // TestGlyphSource resolves via UniTask.FromResult, so the whole tail runs synchronously inside this call.
            // The fetches list captures the evidence DURING execution; "PumpBuilds returned" proves nothing.
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
    /// The worker-phase / tail split: the pass from <see cref="SymbolSubsystem.TryBeginBuild"/> stops after the
    /// pool-side extract and hands a ready tail to <see cref="SymbolSubsystem.PumpBuilds"/>' budgeted tail-start
    /// loop (<c>RunTailAsync</c>) instead of shaping and committing inline. Both teeth here are structural
    /// (source-grep); the behavioural [UnityTest] teeth live in the PlayMode half.
    /// </summary>
    [TestFixture]
    public class SymbolTailPumpTests
    {
        // ── The split is real (structural): SymbolTileWorkerPass.RunWorkerAndHandoff neither shapes nor commits,
        //    and RunTailAsync does each exactly once. ─────────────────────────────────────────────────
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
                "loop lives only in RunTailAsync (the worker/tail split).");
            Assert.AreEqual(0, CountOccurrences(workerBody, commitCallForm),
                $"RunWorkerAndHandoff must contain ZERO '{commitCallForm}' call sites — the commit lives " +
                "only in RunTailAsync too.");
            Assert.AreEqual(1, CountOccurrences(tailBody, shapeCallForm),
                $"RunTailAsync must call '{shapeCallForm}' exactly once — the per-layer shape loop.");
            Assert.AreEqual(1, CountOccurrences(tailBody, commitCallForm),
                $"RunTailAsync must call '{commitCallForm}' exactly once — the sole commit site after the split.");
        }

        // ── The commit-gating ORDER in RunTailAsync: the sole CompleteBuild comes only AFTER the whole per-layer
        //    CompleteOnMain loop AND a trailing ct check, so a mid-loop cancel never commits partial symbols.
        //    Non-obvious why: RunTailAsync also checks ct right after its EnsureGlyphRangesAsync await, BEFORE the
        //    loop, so the trailing guard pinned here is the LAST ".ThrowIfCancellationRequested(" in the body.
        [Test]
        public void RunTailAsync_CommitIsGatedBehindTheWholeLoopAndACtCheck()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);
            string tailBody = ExtractMethodBody(source, "UniTaskVoid RunTailAsync(", path);

            // LAST shape call = the loop's final iteration; the trailing ct check and commit must both follow it.
            // The LAST ct check is the trailing guard; the pre-loop ct check sits BEFORE the shape loop.
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
                "never commits partial labels (follow-up).");
            Assert.Less(ctCheckIdx, commitIdx,
                "CompleteBuild must come AFTER the ct check — partial labels never reach the commit on a cancel.");

            Assert.IsTrue(tailBody.Contains("catch (OperationCanceledException"),
                "RunTailAsync must catch OperationCanceledException — a cancel OBSERVED inside a shape await " +
                "unwinds before the commit, silently (no partial commit on that path either).");
        }

        // ── Glyph-fetch hoist: the tail's await, its collect site, its fields ─────────────────────────

        /// <summary>Structural half: <c>RunTailAsync</c> awaits exactly ONCE, in the build-wide glyph-range
        /// ensure step, BEFORE the per-layer shape loop starts. Complements the reflection half,
        /// <c>GlyphPrepareBeforeShapeTests.TheMainTailHasNoSuspensionPoint</c>, whose blind spot differs.</summary>
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

        /// <summary>The collect step must run on MAIN, after the worker step, inside <c>RunTailAsync</c>
        /// — never in the worker pass, where it would run off-main against a main-thread-only glyph
        /// cache/atlas. The worker step (<c>TileSymbolLayerProcessor.ProcessOnWorker</c>) and the pass's owner
        /// (<c>SymbolSubsystem.RunSymbolWorkerAndHandoff</c>) must both call it ZERO times; the tail
        /// (<c>RunTailAsync</c>) must call it EXACTLY once.</summary>
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

        /// <summary><c>ReadySymbolTail</c> must never carry a
        /// <see cref="SharedDisposable{T}"/>&lt;<see cref="IDecodedTile"/>&gt; — its decode reference's
        /// lifetime ends at the worker step (the parked-drain body's <c>finally</c> / the kick lambda's
        /// release), before a <see cref="SymbolSubsystem.ReadySymbolTail"/> exists. A tail that carried one
        /// would need its OWN "cancelled build releases its decode exactly once" tooth.</summary>
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
        /// <c>Structure.TileProcessingStructureTests.ExtractMethodBody</c> — a narrow tool for a
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
    /// <see cref="SymbolTileStore"/> keeps a released-to-cache tile's symbols WARM and restores them on a
    /// prepared-cache hit (which does not re-fetch), while a truly-evicted tile drops them (the zoom-out-then-in
    /// "no symbols" case). A tile released WHILE its build is in flight still gets its symbols (into the cached
    /// side), and a superseded build is discarded.
    /// </summary>
    /// <remarks>Non-obvious why: <see cref="SymbolTileStore.CaptureSnapshot"/> skips an entry whose <c>Block</c>
    /// is null, so every commit goes through <see cref="Commit"/>, which bakes a REAL block.
    /// <see cref="_blockSources"/> maps a block back to its source buffer only so <see cref="Collect"/> returns
    /// the winner's committed values.</remarks>
    [TestFixture]
    public class SymbolTileStoreTests
    {
        // A leaked SymbolTileBlock keeps DebugLiveAllocCount raised: only Dispose decrements it (no finalizer), so
        // the delta does not depend on GC timing. FakeDisposableBlock mirrors that with its own counter.
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

        // Identity via FeatureIndex pins WHICH tile's symbols came back. Every other dedup-key field defaults
        // identically, so each marker needs a distinct AnchorRender, or dedup would collapse two tiles to one.
        private static SymbolTileBuffer Symbols(int marker)
            => TestSymbolTileBuffer.Point(new double3(marker * 10_000.0, 0, marker * 10_000.0), null, float2.zero, float2.zero,
                featureIndex: marker);

        // Test-only: a baked block's source buffer, so Collect() can hand back ShapedSymbol values.
        // Production CollectInto emits blockId/localIndex arrays instead.
        private readonly Dictionary<SymbolTileBlock, SymbolTileBuffer> _blockSources = new();

        // Bakes a REAL block for `buffer` and commits it: CaptureSnapshot skips a block-less entry. Bake does not
        // intern (it copies the ids ShapedSymbol already carries), so there is no table to share here.
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

        // ═══ The PULL/reconcile model (replaces the release/restore push-callbacks) ════════

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

            // Past the window → purged. The symbols stay WARM (a cache hit still restores them) but are not
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

        // ═══ The SEAMLESS same-cell HOLD across a zoom step (the fixed-grid pivot) ════════════

        // ── THE pivot-defining tooth: an ACTIVE point symbol at z=9 and a DEPARTING point symbol with the SAME text
        //    and IDENTICAL AnchorRender at z=8 key on the SAME fixed CanonicalGridMeters cell — so the departing
        //    copy is claim-skipped (a seamless hold across the zoom step), NOT emitted as a fading duplicate.
        //    Non-obvious why: a per-zoom grid would put the two bands in different cells, so every zoom step
        //    would emit a fading duplicate. ──
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
            // ShapedSymbol is a struct, so there is no reference identity. FeatureIndex, the one field that
            // differs, tells the active (1) copy from the departing (2) one.
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

        // ═══ Icon cross-tile identity dedup ═══

        // Appends one point (or icon, via `iconImage`) symbol at the fixed anchor double3.zero into `buffer` and
        // returns the record. Every test here varies only text/iconImage/tile.
        private static ShapedSymbol AddSymbol(SymbolTileBuffer buffer, int layer, string text, string iconImage, int feature, TileId tile)
        {
            TestSymbolTileBuffer.AddPoint(buffer, default, null, float2.zero, float2.zero,
                text: text, iconImage: iconImage, materialIndex: layer, featureIndex: feature, tileKey: SymbolTileKey.Pack(tile));
            return buffer.Symbols[buffer.Symbols.Count - 1];
        }

        private List<ShapedSymbol> CollectQuantized(SymbolTileStore store, double q)
            => CollectWithActiveCount(store, q, out _);

        // ── Two co-located icons (same anchor cell + layer, text=null, DISTINCT icon-image) both survive
        //    CollectInto: the dedup key includes the icon image. ──
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

        // ── Byte-identical control: the old text-dedup case, unaffected by the icon change — a text symbol at
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
            Assert.AreEqual(1, output.Count, "text dedup is unchanged");
            Assert.AreEqual(2, output[0].FeatureIndex, "the finest (child) tile's label wins");
            store.Clear();
        }

        // ═══ Block dispose lifecycle ══════════════════════════════════════════════════════════
        // SymbolTileStore.Entry.Block is a plain System.IDisposable, so a fake counter stands in for the real
        // SymbolTileBlock in every test below that does not collect. Non-obvious why: SymbolSnapshot.Add, which
        // CaptureSnapshot calls, casts to the concrete type, so a test that collects commits a real block via Commit.

        private sealed class FakeDisposableBlock : IDisposable
        {
            // Mirrors SymbolTileBlock.DebugLiveAllocCount so the leak-guard TearDown catches an abandoned fake too.
            // Every DisposeCount here is 0 or 1, so an unconditional decrement per Dispose() stays correct.
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

        // ── Resident-graph shed (structural): Entry must never re-root a managed symbol graph, so reflection on the
        //    private Entry type fails the gate on a List/array-typed field. Non-obvious why: the [Explicit] GC-mark
        //    experiment does not run in the gate, so no other check sees a re-root. ──
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

        // ═══ Interned-id dedup key parity vs an independent STRING oracle ══════════════════════════════════════
        // The interned DedupKey must match a STRING CrossTileSymbolKey.For oracle: same winners, order and plan arrays.

        private const double ParityQ = 50.0;

        // ShapedSymbol carries only INTERNED ids, and the oracle must stay independent of SymbolStringTable.Intern,
        // so ParityPoint/ParityCurved stash each symbol's raw text/icon here for Oracle() to read back.
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

        // Independent reimplementation of the store's dedup (same scan order, finest-zoom rule, per-tile blockId
        // and CanonicalGridMeters grid), keyed on the STRING CrossTileSymbolKey via `_parityText`/`_parityIcon`.
        // Non-local invariant: activeTiles/departingTiles follow the order the store enumerates _active/_departing.
        // `q` is only the store's dedup gate, not the oracle grid.
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
        //    AND order AND the plan arrays) against the independent string oracle. ──
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

            // Precondition: the fixture exercises the paths (not an all-merge/all-distinct set). ShapedSymbol's
            // structural equality makes Contains() a per-field comparison against the record each var captured.
            Assert.IsTrue(oracle.Output.Contains(childShared), "sanity: the child (finest) copy is the shared-cell winner");
            Assert.IsFalse(oracle.Output.Contains(parentShared), "the coarser parent copy loses the merge");
            Assert.IsFalse(oracle.Output.Contains(depShared), "the departing copy of the shared identity is skipped");
            Assert.IsTrue(oracle.Output.Contains(bAlpha) && oracle.Output.Contains(bBeta), "distinct text both survive");
            Assert.IsTrue(oracle.Output.Contains(icoA) && oracle.Output.Contains(icoB), "distinct icons both survive");

            // Winner identity is (BlockId, LocalIndex). Both sides assign blockId in the same per-tile scan order and
            // localIndex is the raw list position, so equality with the oracle's arrays IS the parity check.
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

        // ═══ Collect-generation invalidation completeness + purity ═══════════════════════════════════════
        // Non-local invariant: the SymbolSubsystem.CurrentBatch memo is correct only if EVERY collect-relevant
        // mutation bumps CollectGeneration (a missed bump shows stale symbols) and a stable cover or no-op mutation
        // does NOT (else the memo never fires). Each case is its own assertion; a missing MarkCollectDirty() reds it.

        // The Y rows of the invalidation map — each MUST bump the collect generation.
        public enum BumpCase
        {
            BeginBuildNew,               // #1 — first-seen tile pulled active
            BeginBuildRefetchCached,     // #1 — re-fetch of a cached/departing tile (symbols move to active)
            CompleteBuildCommit,         // #2 — symbol content lands
            ReleaseTrue,                 // #3 — active tile released to warm cache
            ReleaseFalseTrueEvict,       // #3b — active tile true-evicted (cache disabled)
            ReleaseFalseStaleCachedDrop, // #3c — a stale cached copy dropped
            RestoreReal,                 // #4 — cached → active (cache hit)
            PurgeExpiredDeparting,       // #7 — an expired departing key removed
            Clear,                       // #8 — whole set → empty
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

        // The N rows — a no-op / superseded mutation must NOT bump (else a stable cover dirties every frame and
        // the memo never fires). ReconcileActiveSet idempotency is elevated to its own headline tooth below.
        public enum NoBumpCase
        {
            CompleteBuildSuperseded, // #2b — a stale build's commit is discarded, no state change
            RestoreNoOp,             // #4b — nothing cached to restore
            ReleaseNoOpEdge,         // #3d — key neither active nor cached, transferredToCache=true
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

        // ── THE headline tooth: ReconcileActiveSet runs EVERY frame; on a stable loaded set it MUST NOT bump, or the
        //    memo dirties every frame. The render stays identical, so only this and the recompute counter see it. ──
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

        // ── CollectInto is a PURE function of the tile set: two back-to-back collects with no mutation
        //    between produce element-identical outputs (+ reference-identical OrderedBlocks) — the premise the memo's
        //    clean-frame reuse rests on. Non-obvious why: every block is a REAL baked one, because a
        //    FakeDisposableBlock would throw at the hard cast in SymbolSnapshot.Add. ──
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
        // Steady no-wrap path: a run with no whitespace never enters the word-wrap lookahead (MeasureRange), so this
        // isolates per-glyph placement, per-line justify and the final block shift, all in-place List writes.
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
