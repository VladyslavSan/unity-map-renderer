// Epic A / A5a-A5b acceptance teeth — the PlayMode half of the pump-split (pre-existing EditMode flake: a
// [UnityTest] yielded frame is instantaneous in EditMode, starving the off-main worker/tail hop; real
// PlayMode frames give the ThreadPool wall-clock, making the wait deterministic). These 3 [UnityTest] drive
// SymbolLabelSubsystem directly (no MapView, no enabled=false / WithTestMaterials — this fixture never used
// them) and assert the budgeted tail-start loop + restyle-drop/cancel behaviour over the pool→main handoff.
// The 2 synchronous [Test] (structural source-grep teeth, no async wait) stay in the EditMode half.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.View.Camera;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapRenderer.Tests; // TestGlyphSource
using Symbol = MapRenderer.Core.Style.Symbol;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.PlayMode.Text
{
    /// <summary>
    /// Epic A / A5a-A5b: the acceptance teeth for the worker-phase / tail split (PlayMode half — see the
    /// EditMode <c>SymbolTailPumpTests</c> for the 2 synchronous structural teeth that stayed). Reuses
    /// <see cref="MapRenderer.Tests.Text.SymbolLabelSubsystemPumpTests"/>' fixture/glyph-source harness (a
    /// single symbol layer over the fixture's "centroids", <see cref="TestGlyphSource"/> injected via
    /// <see cref="SymbolLabelSubsystem.GlyphSourceFactoryOverride"/>).
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
        private SymbolLabelSubsystem _subsystem;
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

        /// <summary>A5b drive helper — mirrors TileManager's kick: <c>TryBeginBuild</c> on the (test) main
        /// thread, then <c>RunWorkerAndHandoff</c> fire-and-forget on the pool. Replaces the retired
        /// <c>OnTileBytesReady</c> push.</summary>
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

        // ── F-2: the tail budget gates tail starts (behavioural, PRIMARY drive) ───────────────────────
        // A5b: worker-phase starts are no longer gated by PumpBuilds at all — DriveTileBytesReady (mirroring
        // TileManager's kick) fires both worker phases immediately, off any pump. So this drives BOTH worker
        // phases up front, yields frames with NO pump until both land in the pool→main handoff
        // (ReadyTailCount == 2 — a player-loop continuation, not a pump product), THEN flips the tail budget
        // down and proves the tail-start loop is gated (the knob's sole remaining role, §Q4).
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

        // ── F-3(a) + F-4: restyle between the worker phase landing and its tail starting drops the ready
        //    tail (E-1.5) — no partial commit, no stale start, no unexpected log. ─────────────────────────
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

        // ── F-3(b): the finer variant — cancel DURING the tail loop (after the tail has STARTED and parked
        //    on a gated glyph fetch), not just between phases. Partial labels must never reach CompleteBuild:
        //    the whole-loop-then-ct-check-then-commit order (moved verbatim from pre-A5a BuildTileAsync)
        //    still gates the commit. ───────────────────────────────────────────────────────────────────────
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
