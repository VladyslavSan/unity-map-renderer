// Text/SymbolPumpTests.cs — the pump-split's PlayMode half: symbol subsystem and tail pumping over real frames.
//
// Both settle genuinely off-main symbol work (extract/tail on the ThreadPool) over real PlayMode frames,
// which a yielded EditMode frame cannot give wall-clock to (a pre-existing EditMode flake this half avoids).
// Both carry ordinary per-test GameObject/Camera/RenderTexture [SetUp]/[TearDown], not process state.
//
// Contents:
//   SymbolSubsystemPumpTests  — PlayMode half of the pump-split — [UnityTest] yield-waits for off-main symbol extract/tail work.
//   SymbolTailPumpTests       — the PlayMode half of the pump-split, driving SymbolSubsystem directly.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement; // SymbolGatherPlan
using MapRenderer.Tests; // TestGlyphSource
using Symbol = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;


namespace MapRenderer.Tests.PlayMode.Text
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolSubsystemPumpTests — PlayMode half of the pump-split — off-main symbol extract/tail work
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The symbol subsystem builds no fetched tile inline on the main thread, and re-uploads the 16 MB
    /// glyph atlas at most once per pump. The worker phase runs through
    /// <see cref="SymbolSubsystem.TryBeginBuild"/> (kick-time) plus the returned pass's
    /// <c>RunWorkerAndHandoff</c> (pool), and <see cref="SymbolSubsystem.PumpBuilds"/> starts at most
    /// <c>MaxBuildsPerFrame</c> ready TAILS per frame. The build-start throttle and stale-drop teeth live
    /// with TileManager's kick — see TileSymbolKickTests. These teeth fail a shallow implementation that
    /// builds inline or uploads per tile, and prove the cancellation path.
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

            // The subsystem owns no MapMaterialSet (per-layer materials moved to
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

        /// <summary>Drive helper — mirrors TileManager's kick: <c>TryBeginBuild</c> on the (test) main
        /// thread, then <c>RunWorkerAndHandoff</c> fire-and-forget on the pool, so
        /// <c>SymbolDecodeAndExtract_RunOffTheMainThread</c> observes the decode/extract marker off
        /// main.</summary>
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

        // Production SetStyle does not walk style.Layers itself (RenderLayerFactory is the sole
        // registry, MapView derives the list). Tests that call the subsystem directly need the equivalent
        // extraction — kept local to the test assembly, outside the grep tooth's scope.
        private static List<Symbol.StyleLayer> ExtractSymbolLayers(StyleDocument style)
        {
            var result = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) result.Add(symbol);
            return result;
        }

        // ── Tooth 2: coalesced atlas upload — A's glyphs upload exactly once; a same-glyph B uploads zero ──
        // Builds are genuinely async (decode+extract on a worker, shape back on main via
        // SwitchToMainThread), so this is a [UnityTest] that yields — each `yield return null` ticks the
        // player loop, the only way an off-thread build's main-thread continuation resumes.
        [UnityTest]
        public IEnumerator PumpBuilds_CoalescesAtlasUpload_OncePerNewGlyphSet_ZeroWhenNoNewGlyphs()
        {
            UseImmediateGlyphs();
            var a = new TileId { Z = 3, X = 0, Y = 0 };
            var b = new TileId { Z = 3, X = 1, Y = 0 };
            var loadedA  = new List<LoadedTileKey> { Key(a) };
            var loadedAB = new List<LoadedTileKey> { Key(a), Key(b) };

            // Build A — its glyphs land in the atlas exactly once; PumpBuilds coalesces the upload, so the TOTAL
            // uploads across the whole build must be exactly 1 (the old per-tile path uploaded per commit).
            DriveTileBytesReady(a);
            int uploadsA = 0;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loadedA);
                _subsystem.PumpBuilds();
                uploadsA += _subsystem.AtlasUploadsLastPump;
                yield return null;
            }
            Assert.Greater(SymbolCount(), 0, "sanity: A's async build actually committed labels (main-thread hop resumed).");
            Assert.AreEqual(1, uploadsA, "A's new glyphs upload exactly ONCE across the whole build (coalesced), not per tile.");

            // Build B (same bytes → same glyphs, already cached) — ZERO new atlas uploads.
            DriveTileBytesReady(b);
            int uploadsB = 0;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loadedAB);
                _subsystem.PumpBuilds();
                uploadsB += _subsystem.AtlasUploadsLastPump;
                yield return null;
            }
            Assert.AreEqual(0, uploadsB, "B adds no new glyphs → zero atlas uploads (coalesced/deduped).");
        }

        // A "has anything committed yet" poll: the winner COUNT off the store's plan-aware CollectInto shim.
        private int SymbolCount() => _subsystem.CollectedWinnerCount();

        // ── Tooth 5: the symbol EXTRACT marker must fire OFF the main thread ──
        // A main-thread-only recorder on MapRenderer.Symbol.Extract must read ZERO across a full build,
        // while an all-thread recorder reads >=1. A regression that drops SwitchToThreadPool (extract back
        // on main) flips mainHits to >0 and fails this.
        [UnityTest]
        public IEnumerator SymbolExtract_RunsOffTheMainThread()
        {
            UseImmediateGlyphs();
            var tile   = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };

            using var mainOnly  = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "MapRenderer.Symbol.Extract",
                ProfilerSampleCapacity, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            using var anyThread = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "MapRenderer.Symbol.Extract",
                ProfilerSampleCapacity, ProfilerRecorderOptions.SumAllSamplesInFrame);

            DriveTileBytesReady(tile);
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                yield return null;
                if (SymbolCount() > 0) break; // build committed → the decode marker has fired
            }
            yield return null; yield return null; // let the profiler commit accumulated samples

            // ProfilerRecorder is a fixed-capacity RING (64 above). Once more than `capacity` frames have
            // been sampled the ring wraps and `Count` is no longer a safe index bound — GetSample(capacity)
            // throws IndexOutOfRange. The 200-frame drive loop above reaches that on a slow/loaded machine,
            // which is what made this test fail intermittently while the product was fine. Clamp to the
            // capacity we asked for; the samples we drop are the oldest, and both recorders sum the SAME
            // marker, so the main-vs-any comparison stays apples-to-apples.
            long mainHits = 0, anyHits = 0;
            for (int i = 0; i < math.min(mainOnly.Count,  ProfilerSampleCapacity); i++) mainHits += mainOnly.GetSample(i).Count;
            for (int i = 0; i < math.min(anyThread.Count, ProfilerSampleCapacity); i++) anyHits += anyThread.GetSample(i).Count;

            Assert.Greater(anyHits, 0, "sanity: the symbol extract marker fired at all (the async build ran to completion).");
            Assert.AreEqual(0, mainHits,
                "MapRenderer.Symbol.Extract must NOT fire on the main thread — Stage B runs the feature " +
                "extract on the thread pool. A regression that drops SwitchToThreadPool fails this.");
        }

        // ── Tooth 4: cancellation — a build suspended in glyph-fetch is cancelled by a restyle, silently ──
        // The build hops to the pool (decode+extract) then back to main (SwitchToMainThread) before it
        // reaches the gated glyph fetch, so this must pump frames ([UnityTest]) to get the build parked on
        // the gate, then restyle + release the gate as cancelled.
        [UnityTest]
        public IEnumerator RestyleMidBuild_CancelsInFlightBuild_Silently_NoDisposedStateTouch()
        {
            // A GATED glyph source: the first fetch suspends on this UTCS until the test releases it.
            var gate = new UniTaskCompletionSource<GlyphRangeResponse>();
            _subsystem.GlyphSourceFactoryOverride = _ => new TestGlyphSource((fontStack, rangeStart, ct) => gate.Task);
            StyleDocument style = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(style, ExtractSymbolLayers(style));

            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            _subsystem.ReconcileLoadedTiles(new List<LoadedTileKey> { Key(tile) });
            DriveTileBytesReady(tile);

            // Pump frames so the build starts, hops the pool (decode+extract), returns to main, and parks on the
            // gated glyph fetch — the tail's prepare step (EnsureGlyphRangesAsync), now BEFORE the shape loop.
            for (int f = 0; f < 60; f++) { _subsystem.PumpBuilds(); yield return null; }
            Assert.AreEqual(0, _subsystem.CancelledBuildCount, "not cancelled yet (parked on the gated glyph fetch).");

            // Restyle mid-build: cancels the in-flight build's token, clears the store, disposes the pipeline.
            StyleDocument restyle = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));
            // Release the gate as CANCELLED — the suspended fetch throws OperationCanceledException, which
            // unwinds the build BEFORE it touches the (now disposed) glyph manager / atlas / store.
            gate.TrySetCanceled();

            for (int f = 0; f < 60; f++) { if (_subsystem.CancelledBuildCount == 1) break; _subsystem.PumpBuilds(); yield return null; }
            Assert.AreEqual(1, _subsystem.CancelledBuildCount, "cancelled build counted, not faulted.");
            Assert.AreEqual(0, _subsystem.ActiveTileCount, "no stale labels committed after restyle.");
        }

        // ── The production seam: CurrentBatch() threads CollectInto's active/departing split into the batch, so a
        //    tile that just left cover comes back as DEPARTING records (which the placement layer fades out). Guards
        //    the connector BETWEEN the two unit-tested sides (store split + placement fade) — where a future cleanup
        //    could silently drop `activeCount` with every other test still green. [UnityTest] because the build hops
        //    to the thread pool then back to main, so its symbols commit only across pumped frames. ──
        [UnityTest]
        public IEnumerator CurrentBatch_TileLeftCover_FlagsRecordsDeparting()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };

            // Build the tile active — pump frames until its (async) build commits symbol records into the plan.
            DriveTileBytesReady(tile);
            SymbolGatherPlan active = null;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                active = _subsystem.CurrentBatch(default, 0.0);
                if (active.WinnerCount > 0) break;
                yield return null;
            }
            Assert.Greater(active.WinnerCount, 0, "sanity: the active tile committed label records");
            Assert.AreEqual(0, DepartingSymbolCount(active), "nothing is departing while the tile is in cover");

            // The tile leaves cover at t=100 → released to the warm cache AND retained as departing (grace applies
            // because the prepared mesh cache is enabled by default).
            _subsystem.ReconcileLoadedTiles(new List<LoadedTileKey>(), nowSeconds: 100.0);
            Assert.AreEqual(0, _subsystem.ActiveTileCount, "the tile left cover");
            Assert.AreEqual(1, _subsystem.DepartingTileCount, "…and is departing (fading out), not dropped");

            // The departing set arrives via the off-main reconcile (1–4 frames later, apply-stale) — pump
            // until the FRONT buffer reflects it (every record departing). A broken pickup/swap never flips the
            // front, so this loop times out and the assertion below fails (the tooth still bites).
            SymbolGatherPlan departing = null;
            bool flipped = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(new List<LoadedTileKey>(), nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                departing = _subsystem.CurrentBatch(default, 0.0);
                if (departing.WinnerCount > 0 && DepartingSymbolCount(departing) == departing.WinnerCount) { flipped = true; break; }
                yield return null;
            }
            Assert.IsTrue(flipped,
                "the departing set was picked up and every record flagged departing → the placement layer fades them out instead of popping");
        }

        // ── Test 3: a REAL tile event dirties the store → CurrentBatch recomputes (CollectRecomputeCount incremented)
        //    AND the plan changes (every record now departing) — the pair to the EditMode half's clean-frame-reuse
        //    tooth, catching a degenerate "never recompute" memo that byte-identity alone would pass. Mirrors
        //    CurrentBatch_TileLeftCover_FlagsRecordsDeparting's departing tooth. ──
        [UnityTest]
        public IEnumerator CurrentBatch_TileEvent_RecomputesAndDrivesFade()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };

            DriveTileBytesReady(tile);
            SymbolGatherPlan active = null;
            bool quiesced = false;
            for (int f = 0; f < 300; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                active = _subsystem.CurrentBatch(default, 0.0);
                if (active.WinnerCount > 0 && _subsystem.ReadyTailCount() == 0) { quiesced = true; break; }
                yield return null;
            }
            Assert.IsTrue(quiesced, "sanity: drove to quiescence");
            Assert.Greater(active.WinnerCount, 0, "the active tile committed label records");
            Assert.AreEqual(0, DepartingSymbolCount(active), "nothing departing while the tile is in cover");
            int recomputesBefore = _subsystem.CollectRecomputeCount;

            // The real event: the tile leaves cover at t=100 → released to the warm cache AND departing-stamped
            // (the store bumps its collect generation). CurrentBatch must SCHEDULE a fresh reconcile
            // (CollectRecomputeCount climbs) and — a few frames later, apply-stale — the picked-up front CHANGES to
            // all-departing. Pump until the front reflects it; a degenerate never-reschedule memo, or a broken
            // pickup/swap, leaves the front unchanged → this loop times out and fails.
            _subsystem.ReconcileLoadedTiles(new List<LoadedTileKey>(), nowSeconds: 100.0);
            SymbolGatherPlan departing = null;
            bool flipped = false;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(new List<LoadedTileKey>(), nowSeconds: 100.0);
                _subsystem.PumpBuilds();
                departing = _subsystem.CurrentBatch(default, 0.0);
                if (departing.WinnerCount > 0 && DepartingSymbolCount(departing) == departing.WinnerCount) { flipped = true; break; }
                yield return null;
            }
            Assert.Greater(_subsystem.CollectRecomputeCount, recomputesBefore,
                "a real tile event dirtied the store → CurrentBatch scheduled a fresh reconcile (catches a degenerate never-reschedule memo)");
            Assert.IsTrue(flipped,
                "…and the picked-up front CHANGED — every record now flagged departing (drives the fade-out, not a stale reuse)");
        }

        private static int DepartingSymbolCount(SymbolGatherPlan plan)
        {
            int n = 0;
            for (int i = 0; i < plan.WinnerCount; i++) if (plan.Departing[i] != 0) n++;
            return n;
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
    // SymbolTailPumpTests — PlayMode half of the pump-split, driving SymbolSubsystem directly
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The acceptance teeth for the worker-phase / tail split (PlayMode half — see the EditMode
    /// <c>SymbolTailPumpTests</c> for the synchronous structural teeth). Reuses
    /// <see cref="MapRenderer.Tests.Text.SymbolSubsystemPumpTests"/>' fixture/glyph-source harness (a
    /// single symbol layer over the fixture's "centroids", <see cref="TestGlyphSource"/> injected via
    /// <see cref="SymbolSubsystem.GlyphSourceFactoryOverride"/>).
    /// </summary>
    [TestFixture]
    public class SymbolTailPumpTests
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

        [SetUp]
        public void SetUp()
        {
            _camGo = new GameObject("SymbolTailPump_TestCamera");
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

        /// <summary>An immediate (synchronous) fixture glyph source — a started tail completes its shape
        /// step without any real suspension, so a tail's commit is observable right after it is pumped.</summary>
        private void UseImmediateGlyphs()
        {
            var ranges = new Dictionary<(string, int), byte[]> { [(FontName, 0)] = _latinGlyphs };
            _subsystem.GlyphSourceFactoryOverride = _ => TestGlyphSource.FromRanges(ranges);
            StyleDocument style = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(style, ExtractSymbolLayers(style));
        }

        private static LoadedTileKey Key(TileId t) => new LoadedTileKey(SourceId, t);

        private static List<Symbol.StyleLayer> ExtractSymbolLayers(StyleDocument style)
        {
            var result = new List<Symbol.StyleLayer>();
            foreach (StyleLayer layer in style.Layers)
                if (layer is Symbol.StyleLayer symbol) result.Add(symbol);
            return result;
        }

        /// <summary>Drive helper — mirrors TileManager's kick: <c>TryBeginBuild</c> on the (test) main
        /// thread, then <c>RunWorkerAndHandoff</c> fire-and-forget on the pool.</summary>
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

        // ── The tail budget gates tail starts (behavioural, PRIMARY drive) ────────────────────────────
        // PumpBuilds gates no worker-phase start — DriveTileBytesReady (mirroring TileManager's kick) fires
        // both worker phases immediately, off any pump. So this drives BOTH worker phases up front, yields
        // frames with NO pump until both land in the pool→main handoff (ReadyTailCount == 2 — a player-loop
        // continuation, not a pump product), THEN flips the tail budget down and proves the tail-start loop
        // is gated, which is the knob's only role.
        [UnityTest]
        public IEnumerator PumpBuilds_TailStartsAreBudgeted_BacklogDrainsOnePumpAtATime()
        {
            UseImmediateGlyphs();
            var tileA = new TileId { Z = 3, X = 0, Y = 0 };
            var tileB = new TileId { Z = 3, X = 1, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tileA), Key(tileB) };

            _subsystem.ReconcileLoadedTiles(loaded);
            DriveTileBytesReady(tileA);
            DriveTileBytesReady(tileB);

            // Yield frames WITHOUT pumping — both worker phases run on the pool (fire-and-forget from
            // DriveTileBytesReady, mirroring TileManager's kick task) and land in the handoff queue via the
            // player loop, never via PumpBuilds.
            for (int f = 0; f < 200 && _subsystem.ReadyTailCount() < 2; f++) yield return null;
            Assert.AreEqual(2, _subsystem.ReadyTailCount(), "both worker phases landed in the pool→main handoff");
            Assert.AreEqual(0, _subsystem.TailsStartedLastPump, "no PumpBuilds call has run yet");

            // Flip the budget down to 1 and pump: the tail-start loop is gated — only ONE tail starts per pump.
            _subsystem.MaxBuildsPerFrame = 1;
            _subsystem.PumpBuilds();
            Assert.AreEqual(1, _subsystem.TailsStartedLastPump, "only ONE tail started under the budget");
            Assert.AreEqual(1, _subsystem.ReadyTailCount(), "the other stays queued — the backlog drains, not bursts");

            _subsystem.PumpBuilds();
            Assert.AreEqual(1, _subsystem.TailsStartedLastPump, "second pump starts the remaining tail");
            Assert.AreEqual(0, _subsystem.ReadyTailCount(), "backlog fully drained");
        }

        // ── A restyle between the worker phase landing and its tail starting drops the ready tail — no
        //    partial commit, no stale start, no unexpected log. ────────────────────────────────────────────
        [UnityTest]
        public IEnumerator SetStyle_BetweenWorkerAndTail_DropsReadyTail_NoCommit_NeverStarts()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };

            DriveTileBytesReady(tile);
            _subsystem.ReconcileLoadedTiles(loaded);
            _subsystem.PumpBuilds(); // starts the worker phase; nothing is ready to tail in this same call

            // Yield until the worker phase lands its hop — ready, but NOT started (no further PumpBuilds calls).
            for (int f = 0; f < 200 && _subsystem.ReadyTailCount() < 1; f++) yield return null;
            Assert.AreEqual(1, _subsystem.ReadyTailCount(), "sanity: worker phase landed, tail is ready but not started");
            Assert.AreEqual(0, _subsystem.CancelledBuildCount, "not cancelled yet");

            // Restyle: cancels the build's token, clears the store AND drops the ready tail (E-1.5).
            StyleDocument restyle = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));

            Assert.AreEqual(0, _subsystem.ReadyTailCount(), "restyle drops the ready tail — it never gets to commit");
            Assert.AreEqual(0, _subsystem.ActiveTileCount, "no labels committed for the old-style build");

            // Pump on — nothing from the old style ever starts.
            for (int f = 0; f < 5; f++) { _subsystem.PumpBuilds(); yield return null; }
            Assert.AreEqual(0, _subsystem.TailsStartedLastPump, "the dropped tail never starts (F-4)");
            Assert.AreEqual(0, _subsystem.ActiveTileCount, "still no stale labels committed after restyle");
        }

        // ── The finer variant — cancel DURING the tail loop (after the tail has STARTED and parked on a
        //    gated glyph fetch), not just between phases. Partial symbols must never reach CompleteBuild:
        //    the whole-loop-then-ct-check-then-commit order gates it. ─────────────────────────────────────
        [UnityTest]
        public IEnumerator SetStyle_DuringTailExecution_CancelsSilently_NoPartialCommit()
        {
            // A GATED glyph source: the first fetch suspends on this UTCS until the test releases it.
            var gate = new UniTaskCompletionSource<GlyphRangeResponse>();
            _subsystem.GlyphSourceFactoryOverride = _ => new TestGlyphSource((fontStack, rangeStart, ct) => gate.Task);
            StyleDocument style = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(style, ExtractSymbolLayers(style));

            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            _subsystem.ReconcileLoadedTiles(new List<LoadedTileKey> { Key(tile) });
            DriveTileBytesReady(tile);

            // Pump frames until a tail has actually STARTED (its worker phase landed on an earlier pump, and
            // a later pump drained it into RunTailAsync, which is now parked on the gated glyph fetch).
            bool tailStarted = false;
            for (int f = 0; f < 60 && !tailStarted; f++)
            {
                _subsystem.PumpBuilds();
                if (_subsystem.TailsStartedLastPump > 0) tailStarted = true;
                yield return null;
            }
            Assert.IsTrue(tailStarted, "sanity: the tail actually started (parked on the gated glyph fetch)");
            Assert.AreEqual(0, _subsystem.CancelledBuildCount, "not cancelled yet (parked on the gated glyph fetch)");

            // Restyle mid-TAIL: cancels the in-flight tail's token, clears the store, disposes the pipeline.
            StyleDocument restyle = StyleParser.Parse(StyleJson);
            _subsystem.SetStyle(restyle, ExtractSymbolLayers(restyle));
            // Release the gate as CANCELLED — the suspended fetch throws OperationCanceledException, which
            // unwinds the tail BEFORE it reaches the ct check / commit (or touches the now-disposed pipeline).
            gate.TrySetCanceled();

            for (int f = 0; f < 60 && _subsystem.CancelledBuildCount == 0; f++) { _subsystem.PumpBuilds(); yield return null; }
            Assert.AreEqual(1, _subsystem.CancelledBuildCount, "the tail's cancellation counted, not faulted");
            Assert.AreEqual(0, _subsystem.ActiveTileCount, "partial labels never committed for the cancelled tail");
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
