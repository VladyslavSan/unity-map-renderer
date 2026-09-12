// Unity EditMode only — needs a real Camera + the internal SymbolSubsystem. SymbolSubsystem's own two
// off-main dispatch sites (PumpBuilds's parked-build drain, ScheduleReconcileIfDirty's cross-tile reconcile)
// dispatch through the injected SymbolSubsystem.WorkScheduler policy — the WebGL fix for the last pair of
// raw UniTask.RunOnThreadPool sites in the tile pipeline (docs/web-target.md). Mirrors
// MeshBuildWorkSchedulerTests' shape for TileManager's own two kicks, sharing its RecordingWorkScheduler spy
// (TestSupport/) so the two suites cannot drift into two subtly different instruments.
//
// ParkedDrain_* proves T1 (the WebGL-only unrecoverable decode leak at :643): the parked-drain body is the
// ONLY release for the decode reference it took at park time, so the tooth must observe the RELEASE ITSELF
// (LeaseProbeDecoder.DisposedCount), synchronously, immediately after PumpBuilds() returns — an empty-queue
// reading would pass vacuously, since the entry is already dequeued by the time Schedule is even called.
//
// ReconcileDispatch_* proves T2 (the WebGL-only permanent placement wedge at :899): under Inline the pickup
// lands ONE CurrentBatch call later than the schedule (PickupCompletedReconcile runs BEFORE
// ScheduleReconcileIfDirty inside CurrentBatch), so the tooth drives two calls and asserts the in-flight
// flag at each.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Cysharp.Threading.Tasks;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.View.Camera;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Tests.Text.Placement; // TestSymbolTileBuffer
using Symbol = MapRenderer.Core.Style.Symbol;

namespace MapRenderer.Tests.Text
{
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
                buffer, slotCount: 1, TileRenderOrigin.Project(Tile0, Projection), new SymbolStringTable());
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
            // (parity-oracle-vacuous-when-fixture-lacks-filter-keys / commits-inside-PumpBuilds-is-green-today),
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
}
