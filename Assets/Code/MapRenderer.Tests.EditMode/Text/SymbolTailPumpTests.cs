// Unity EditMode only — needs a real Camera/Material/Shader + the internal SymbolSubsystem, and reads
// source files under Application.dataPath. NOT registered in core-tests.csproj.
//
// EditMode half of the pump-split: the 2 synchronous [Test] below are structural source-grep teeth with no
// async wait, so they belong here. The 3 [UnityTest] that yield-wait for the off-main worker/tail hop moved
// to MapRenderer.Tests.PlayMode.Text.SymbolTailPumpTests — a pre-existing EditMode flake (a yielded frame is
// instantaneous in EditMode, starving the ThreadPool; real PlayMode frames give it wall-clock).

using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
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
        //    partial symbols. F-1 pins the call COUNTS (each exactly once); this pins their ORDER — the actual
        //    partial-commit guard the A3 review flagged as covered only indirectly. Complements the behavioural
        //    F-3(b) (cancel OBSERVED inside a shape await); this pins the guard structurally + deterministically.
        [Test]
        public void RunTailAsync_CommitIsGatedBehindTheWholeLoopAndACtCheck()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
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
