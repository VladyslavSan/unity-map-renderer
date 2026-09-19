// Unity EditMode only — needs a real Camera/Material/Shader + the internal SymbolSubsystem, and reads
// source files under Application.dataPath. NOT registered in core-tests.csproj.
//
// EditMode half of the pump-split: the synchronous [Test] below are structural source-grep (+ one reflection)
// teeth with no async wait, so they belong here. The 3 [UnityTest] that yield-wait for the off-main
// worker/tail hop moved to MapRenderer.Tests.PlayMode.Text.SymbolTailPumpTests — a pre-existing EditMode
// flake (a yielded frame is instantaneous in EditMode, starving the ThreadPool; real PlayMode frames give it
// wall-clock).

using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Text;

namespace MapRenderer.Tests.Text
{
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
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
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
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
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
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
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
            Assert.IsTrue(File.Exists(subsystemPath), $"expected source file to exist at {subsystemPath}");
            Assert.IsTrue(File.Exists(processorPath), $"expected source file to exist at {processorPath}");
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
}
