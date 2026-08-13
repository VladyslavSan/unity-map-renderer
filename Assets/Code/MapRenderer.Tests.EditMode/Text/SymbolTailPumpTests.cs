// Unity EditMode only — needs a real Camera/Material/Shader + the internal SymbolLabelSubsystem, and reads
// source files under Application.dataPath. NOT registered in core-tests.csproj.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// Epic A / A5a-A5b: the acceptance teeth for the worker-phase / tail
    /// split — the worker phase (<see cref="SymbolLabelSubsystem.TryBeginBuild"/>'s returned pass) stops
    /// after the pool-side extract and hands a ready tail (via the A5b pool→main handoff) to
    /// <see cref="SymbolLabelSubsystem.PumpBuilds"/>' budgeted tail-start loop (<c>RunTailAsync</c>), rather
    /// than shaping + committing inline. These teeth fail a shallow split — a rename that leaves the tail
    /// running inline, an unbudgeted tail loop, or a split that breaks the partial-commit guard. Reuses
    /// <see cref="SymbolLabelSubsystemPumpTests"/>' fixture/glyph-source harness (a single symbol layer over
    /// the fixture's "centroids", <see cref="TestGlyphSource"/> injected via
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

        // ── F-1: the split is real (structural) — re-expressed at A5b (BuildTileAsync no longer exists;
        //    the worker-pass call now lives in SymbolTileWorkerPass.RunWorkerAndHandoff, not a coroutine
        //    method this grep-narrow tool can anchor on by signature). Genuinely RED pre-A5a: both call
        //    forms lived inside one method's body, and RunTailAsync did not exist. ───────────────────────
        [Test]
        public void RunWorkerAndHandoff_NeverShapesOrCommits_RunTailAsync_DoesBothExactlyOnce()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolLabelSubsystem.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            const string runWorkerAndHandoffAnchor = "void RunWorkerAndHandoff(SharedDisposable<IDecodedTile> decode)";
            const string runTailAsyncAnchor        = "UniTaskVoid RunTailAsync(";
            const string shapeCallForm             = ".CompleteOnMainAsync(";
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
        //    is reached only AFTER the whole per-layer CompleteOnMainAsync loop AND a trailing ct check, so a
        //    cancel landing mid-loop (between processor tails k and k+1, or after the last one) never commits
        //    partial labels. F-1 pins the call COUNTS (each exactly once); this pins their ORDER — the actual
        //    partial-commit guard the A3 review flagged as covered only indirectly. Complements the behavioural
        //    F-3(b) (cancel OBSERVED inside a shape await); this pins the guard structurally + deterministically.
        [Test]
        public void RunTailAsync_CommitIsGatedBehindTheWholeLoopAndACtCheck()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolLabelSubsystem.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);
            string tailBody = ExtractMethodBody(source, "UniTaskVoid RunTailAsync(", path);

            // LAST shape call = the loop's final iteration; the ct check + commit must both follow it.
            int lastShapeIdx = tailBody.LastIndexOf(".CompleteOnMainAsync(", StringComparison.Ordinal);
            int ctCheckIdx   = tailBody.IndexOf(".ThrowIfCancellationRequested(", StringComparison.Ordinal);
            int commitIdx    = tailBody.IndexOf(".CompleteBuild(", StringComparison.Ordinal);

            Assert.GreaterOrEqual(lastShapeIdx, 0, "sanity: the per-layer shape loop call must be present.");
            Assert.GreaterOrEqual(ctCheckIdx, 0,
                "RunTailAsync must call ThrowIfCancellationRequested() — the trailing guard that catches a " +
                "cancel landing after the loop's shaping but before the commit.");
            Assert.GreaterOrEqual(commitIdx, 0, "sanity: the CompleteBuild commit call must be present.");

            Assert.Less(lastShapeIdx, ctCheckIdx,
                "the ct check must come AFTER the whole per-layer shape loop (its LAST CompleteOnMainAsync) — " +
                "the commit is gated behind the WHOLE loop clearing cancellation first, so a mid-loop cancel " +
                "never commits partial labels (A3 follow-up).");
            Assert.Less(ctCheckIdx, commitIdx,
                "CompleteBuild must come AFTER the ct check — partial labels never reach the commit on a cancel.");

            Assert.IsTrue(tailBody.Contains("catch (OperationCanceledException"),
                "RunTailAsync must catch OperationCanceledException — a cancel OBSERVED inside a shape await " +
                "(F-3(b)) unwinds before the commit, silently (no partial commit on that path either).");
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
