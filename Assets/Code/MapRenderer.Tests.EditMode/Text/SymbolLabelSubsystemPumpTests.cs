// Unity EditMode only — needs a real Camera/Material/Shader + the internal SymbolLabelSubsystem, and drives
// the glyph atlas Texture2D upload. NOT registered in core-tests.csproj.

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
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Tiles;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapRenderer.Tests; // TestGlyphSource
using Is = UnityEngine.TestTools.Constraints.Is;
using Symbol = MapRenderer.Core.Style.Symbol;

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
            UniTask.RunOnThreadPool(() => pass.RunWorkerAndHandoff(new SharedTileDecode(_tileBytes, new MvtTileDecoder()))).Forget();
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

        // ── Tooth 2: coalesced atlas upload — A's glyphs upload exactly once; a same-glyph B uploads zero ──
        // Stage B makes builds genuinely async (decode+extract on a worker, shape back on main via
        // SwitchToMainThread), so this is a [UnityTest] that yields — each `yield return null` ticks the editor
        // player loop, the only way an off-thread build's main-thread continuation resumes in EditMode.
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
            Assert.Greater(LabelCount(), 0, "sanity: A's async build actually committed labels (main-thread hop resumed).");
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

        private int LabelCount()
        {
            var labels = new List<LabelInstance>();
            _subsystem.CollectInto(labels);
            return labels.Count;
        }

        // ── Tooth 5 (Stage B): the decode+extract marker must fire OFF the main thread ──
        // The core Stage-B claim (and the project principle: do off-main everything that can be). A
        // main-thread-only recorder on MapRenderer.Symbol.TileDecode must read ZERO across a full build,
        // while an all-thread recorder reads >=1. A regression that drops SwitchToThreadPool (decode back on
        // main) flips mainHits to >0 and fails this.
        [UnityTest]
        public IEnumerator SymbolDecodeAndExtract_RunOffTheMainThread()
        {
            UseImmediateGlyphs();
            var tile   = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };

            using var mainOnly  = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "MapRenderer.Symbol.TileDecode",
                64, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            using var anyThread = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "MapRenderer.Symbol.TileDecode",
                64, ProfilerRecorderOptions.SumAllSamplesInFrame);

            DriveTileBytesReady(tile);
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                yield return null;
                if (LabelCount() > 0) break; // build committed → the decode marker has fired
            }
            yield return null; yield return null; // let the profiler commit accumulated samples

            long mainHits = 0, anyHits = 0;
            for (int i = 0; i < mainOnly.Count;  i++) mainHits += mainOnly.GetSample(i).Count;
            for (int i = 0; i < anyThread.Count; i++) anyHits += anyThread.GetSample(i).Count;

            Assert.Greater(anyHits, 0, "sanity: the symbol decode marker fired at all (the async build ran to completion).");
            Assert.AreEqual(0, mainHits,
                "MapRenderer.Symbol.TileDecode must NOT fire on the main thread — Stage B runs decode + feature " +
                "extract on the thread pool. A regression that drops SwitchToThreadPool fails this.");
        }

        // ── Tooth 3 (stale-drop before build start) RETIRED at A5b: a queued-but-departed tile can no longer
        //    occur here — the kick itself never fires for a condemned/departed tile (TileManager's
        //    _releaseQueued check, ahead of TryBeginBuild). Replaced by TileSymbolKickTests' F-6
        //    (departed-before-kick tile: no TryBeginBuild).

        // ── Tooth 4: cancellation — a build suspended in glyph-fetch is cancelled by a restyle, silently ──
        // Stage B: the build now hops to the pool (decode+extract) then back to main (SwitchToMainThread)
        // before it reaches the gated glyph fetch, so this must pump editor frames ([UnityTest]) to get the
        // build parked on the gate, then restyle + release the gate as cancelled.
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
            // gated glyph fetch inside ShapeAsync.
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
        //    to the thread pool then back to main, so its labels commit only across pumped editor frames. ──
        [UnityTest]
        public IEnumerator CurrentBatch_TileLeftCover_FlagsRecordsDeparting()
        {
            UseImmediateGlyphs();
            var tile = new TileId { Z = 3, X = 0, Y = 0 };
            var loaded = new List<LoadedTileKey> { Key(tile) };

            // Build the tile active — pump frames until its (async) build commits label records into the batch.
            DriveTileBytesReady(tile);
            SymbolLabelBatch active = null;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                active = _subsystem.CurrentBatch(default, 0.0);
                if (active.Count > 0) break;
                yield return null;
            }
            Assert.Greater(active.Count, 0, "sanity: the active tile committed label records");
            Assert.AreEqual(0, DepartingRecordCount(active), "nothing is departing while the tile is in cover");

            // The tile leaves cover at t=100 → released to the warm cache AND retained as departing (grace applies
            // because the prepared mesh cache is enabled by default). CurrentBatch is rebuilt BELOW — read `active`
            // (the same reused batch instance) only before that.
            _subsystem.ReconcileLoadedTiles(new List<LoadedTileKey>(), nowSeconds: 100.0);
            Assert.AreEqual(0, _subsystem.ActiveTileCount, "the tile left cover");
            Assert.AreEqual(1, _subsystem.DepartingTileCount, "…and is departing (fading out), not dropped");

            SymbolLabelBatch departing = _subsystem.CurrentBatch(default, 0.0);
            Assert.Greater(departing.Count, 0, "the departing tile's labels are still collected (so they can fade)");
            Assert.AreEqual(departing.Count, DepartingRecordCount(departing),
                "…and every record is flagged departing → the placement layer fades them out instead of popping");
        }

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

            SymbolLabelBatch batch = null;
            for (int f = 0; f < 200; f++)
            {
                _subsystem.ReconcileLoadedTiles(loaded);
                _subsystem.PumpBuilds();
                batch = _subsystem.CurrentBatch(frame, keepAllButRunFilter);
                if (batch.Count > 0) break;
                yield return null;
            }
            Assert.Greater(batch.Count, 0, "sanity: the tile committed label records before the alloc measurement");

            // Warm-up call (first-touch Dictionary/List growth allowed) then the STEADY call is measured. Block
            // body (NOT an expression lambda): `() => _subsystem.CurrentBatch(...)` binds NUnit's value-returning
            // `Assert.That<T>(Func<T>, ...)` overload — it hands the returned SymbolLabelBatch to the constraint
            // instead of invoking Is.Not.AllocatingGCMemory()'s delegate form, throwing
            // "the actual value must be a TestDelegate" at runtime. A block-body lambda is a plain TestDelegate.
            _subsystem.CurrentBatch(frame, keepAllButRunFilter);
            Assert.That(() => { _subsystem.CurrentBatch(frame, keepAllButRunFilter); },
                Is.Not.AllocatingGCMemory(),
                "a steady-state CurrentBatch (coverage cull enabled) must allocate ZERO managed garbage");
        }

        private static int DepartingRecordCount(SymbolLabelBatch batch)
        {
            int n = 0;
            for (int i = 0; i < batch.Count; i++) if (batch.RecordDeparting[i]) n++;
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
}
