// Unity EditMode only — reads source files under Application.dataPath. NOT registered in core-tests.csproj.

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using Unity.Jobs.LowLevel.Unsafe;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// job-scheduling-design.md §8 stage 1: the structural complement to
    /// <c>FillMeshGraphSchedulingTests</c>'s not-completed-at-return tooth. That behavioural tooth cannot
    /// false-red on a tiny fixture (an unflushed job cannot have started), but for the same reason it proves
    /// only that nothing completed synchronously THIS RUN — it cannot see a <c>Complete()</c> call that
    /// happens to be a no-op on the fixture used. This file pins the SOURCE shape instead: no
    /// <c>Complete()</c>, no schedule-time <c>.AsArray()</c> (the stale-length hazard the graph's own doc
    /// names), no <c>.Run(</c>, no <c>IWorkScheduler</c> — and the required tokens guard against a renamed or
    /// gutted file trivially satisfying every "count is zero" claim (the same idiom
    /// <c>StyledLineBuilderStructureTests</c> uses).
    ///
    /// <para><b>Two identifier pins retired here, and what covers them now.</b> A prior version of
    /// <see cref="RequiredTokens"/> pinned the literal join call that closed the dependency-bug FillMeshGraph
    /// once shipped (<c>FillRingCountJob</c> writing the shared <c>Counts</c> array with no edge to the job
    /// that wrote it next), then — after that job was deleted — a literal <c>.Schedule(node3Handle)</c>
    /// match, then <c>Dispose(node7Handle)</c>. Both were pins on a LOCAL VARIABLE'S spelling, which breaks on
    /// a rename rather than on a regression — the weakest form of the check they stood in for. Neither is
    /// re-pinned under FillMeshGraph.cs's current local names. What covers each instead:
    /// <list type="bullet">
    /// <item><description>The missing-edge class of bug (a job scheduled with no dependency on the job that
    /// wrote a container it reads) is caught by Unity's own job safety system at <c>Schedule</c> — every
    /// <c>FillMeshGraphParityTests</c>/<c>FillMeshGraphGlobeParityTests</c> run schedules the REAL graph over
    /// real fixtures with the Editor's job debugger on, so a dropped edge throws
    /// <c>InvalidOperationException</c> there, not merely on a source-text pin.</description></item>
    /// <item><description>The dispose-node-per-column claim is covered behaviourally by
    /// <c>FillMeshGraphSchedulingTests.Dispose_ReturnsLiveOutputsToBaseline_AndBufferDisposeNodesPairWithAllocations</c>/
    /// <c>..._OnACurvedLayer</c>, which assert the allocate/dispose-node counters balance — a real per-container
    /// count, not a grep for one call site.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    [TestFixture]
    public class FillMeshGraphStructureTests
    {
        // Regex, not a literal — "Complete()" as a bare string misses "Handle.Complete ()" or a
        // line-wrapped call. `\.Run\(` stays a plain substring below; a synchronous dispatch site would not
        // plausibly be reformatted to dodge it, and IWorkScheduler/.AsArray( are identifiers/exact API calls.
        private static readonly Regex CompleteCallPattern = new Regex(@"\.Complete\s*\(");

        private static readonly string[] ForbiddenTokens =
        {
            ".AsArray(", ".Run(", "IWorkScheduler",
        };

        private static readonly string[] RequiredTokens =
        {
            "SizingJob", "FillGatherJob", "EarcutBatchJob", "AggregateJob",
            "RingAssemblyJob", "JobHandle",
            // job-scheduling-design.md §8 stage 4: the curved-arm sub-chain — its two nodes, and the
            // predicate (exactly WriteGeometry's) that decides which arm a layer takes.
            "GlobeFillSubdivideDispatch", "GlobeFillScatterJob", "MaxRefineAngleRad",
            // job-scheduling-design.md §7 rule 2: EarcutBatchJob has no loop of its own to bound, so neither
            // FillSizingJobTests nor any other RED tooth reds if its deferred-count SOURCE regresses from
            // buffers.PerPolyOuterCount (a sizing-owned column) to a borrowed count. This literal pins that
            // source structurally — the only thing that reds on that regression.
            "Schedule(buffers.PerPolyOuterCount, EarcutPolygonBatch, gathered)",
            // Retired two identifier pins that used to live here (a literal join call, then a literal
            // `.Schedule(node3Handle)`/`Dispose(node7Handle)` local-name match): a pin on a LOCAL VARIABLE'S
            // spelling is the weakest form of the check it stood in for, and the only one that breaks on a
            // rename rather than a real regression. What each guarded is now covered behaviourally instead —
            // see this file's own type doc.
        };

        [Test]
        public void FillMeshGraph_HasNoCompleteNoScheduleTimeAsArray_AndReferencesEveryNode()
        {
            string code = StripLineComments(FillMeshGraphSource());
            Assert.Greater(code.Trim().Length, 0, "precondition: FillMeshGraph.cs source is non-empty");

            foreach (string token in RequiredTokens)
                Assert.Greater(CountOccurrences(code, token), 0,
                    $"precondition: FillMeshGraph.cs must still reference '{token}' — without it this test's " +
                    "zero-counts below would be satisfied by a gutted or renamed file");

            foreach (string token in ForbiddenTokens)
                Assert.AreEqual(0, CountOccurrences(code, token),
                    $"FillMeshGraph.cs must contain ZERO occurrences of '{token}' — this graph builder never " +
                    "takes a schedule-time array view of a list it resizes, never dispatches synchronously, " +
                    "and never touches the managed-closure seam.");

            Assert.AreEqual(0, CompleteCallPattern.Matches(code).Count,
                "FillMeshGraph.cs must contain ZERO Complete() calls — this graph builder never completes " +
                "its own handle.");
        }

        // ── The attribute fence — job-scheduling-design.md §7 rule 2: "no [NativeDisableContainerSafetyRestriction]
        // in a graph builder EVER, and no [NativeDisableParallelForRestriction] before stage 6." Scoped to the
        // two directories §3.2 names as where graph builders and stream-write jobs live — not one hand-listed
        // file, so adding a job anywhere under either directory is covered by construction. Stage 6 sanctions
        // EXACTLY ONE occurrence of NativeDisableParallelForRestriction in EACH of two files (job-scheduling-
        // design.md §8 stage 6, A0.3/C.5/A.2/E.4): EarcutBatchJob.cs and RibbonBatchJob.cs, each a
        // field-level attribute on its single Buffers field. No other file, and no other count.
        // NativeDisableContainerSafetyRestriction stays forbidden with NO exceptions, anywhere, always.
        //
        // The exception is a (file → token → count) triple, not a bare filename: a bare filename would exempt
        // that file from EVERY forbidden token, at ANY count — which would have let NativeDisableContainerSafetyRestriction
        // through on EarcutBatchJob.cs too, the exact vacuity a bare-filename allowlist invites. This shape
        // was never live before this stage — the array was always empty, so the skip path had never executed.
        //
        // Matches the BARE identifier, not the bracketed short form: a fully-qualified attribute
        // (Unity.Collections.LowLevel.Unsafe.NativeDisableContainerSafetyRestriction) or an aliased one
        // carries no "[...]" literal and would silently pass a match on the short form — caught RED-verifying
        // this fence, when the fully-qualified form stayed green. The identifier cannot appear in compiling
        // C# except as this attribute (fully qualified, short form, or aliased), so the bare match is both
        // simpler and strictly more complete than the bracketed one it replaces.

        private static readonly string[] AttributeFenceForbiddenTokens =
        {
            "NativeDisableContainerSafetyRestriction", "NativeDisableParallelForRestriction",
        };

        private struct AttributeFenceException
        {
            public string FileName;
            public string Token;
            public int ExpectedCount;
        }

        /// <summary>Every entry pins a COUNT derived from the field list it corresponds to, not read off the
        /// file as "whatever it says today" — a pin that merely records the current count detects a LATER
        /// widening but blesses today's. EarcutBatchJob.cs's single <c>Buffers</c> field carries exactly one
        /// occurrence: red here means a written column was added to or removed from
        /// <see cref="EarcutBatchJob"/> in a way that changed its field shape — update this deliberately,
        /// never to whatever the file now says.</summary>
        private static readonly AttributeFenceException[] AttributeFenceAllowedFiles =
        {
            new AttributeFenceException
            {
                FileName = "EarcutBatchJob.cs", Token = "NativeDisableParallelForRestriction", ExpectedCount = 1,
            },
            new AttributeFenceException
            {
                FileName = "RibbonBatchJob.cs", Token = "NativeDisableParallelForRestriction", ExpectedCount = 1,
            },
        };

        [Test]
        public void GraphBuilderSources_CarryNeitherSafetyDisableAttribute()
        {
            string jobsDir    = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            string meshingDir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Meshing");
            Assert.IsTrue(Directory.Exists(jobsDir), $"expected directory to exist at {jobsDir}");
            Assert.IsTrue(Directory.Exists(meshingDir), $"expected directory to exist at {meshingDir}");

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(jobsDir, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(meshingDir, "*.cs", SearchOption.AllDirectories));

            Assert.GreaterOrEqual(files.Count, 10,
                "precondition: the enumeration must find a plausible number of production files, or a moved " +
                "directory / wrong Application.dataPath join would make every assertion below vacuous");
            Assert.IsTrue(files.Exists(f => Path.GetFileName(f) == "FillMeshGraph.cs"),
                "precondition: the enumeration must include a known graph builder file (FillMeshGraph.cs)");

            // Found-in-enumeration guard: a renamed sanctioned file would otherwise make its exception a
            // silent no-op that still reads green (the same vacuity class this file already guards for
            // FillMeshGraph.cs above).
            foreach (AttributeFenceException exception in AttributeFenceAllowedFiles)
                Assert.IsTrue(files.Exists(f => Path.GetFileName(f) == exception.FileName),
                    $"precondition: the enumeration must include the sanctioned exception file {exception.FileName} " +
                    "— a rename would otherwise make its exception a silent no-op");

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                string code = StripLineComments(File.ReadAllText(file));

                foreach (string token in AttributeFenceForbiddenTokens)
                {
                    int allowedCount = 0;
                    foreach (AttributeFenceException exception in AttributeFenceAllowedFiles)
                        if (exception.FileName == name && exception.Token == token) allowedCount = exception.ExpectedCount;

                    Assert.AreEqual(allowedCount, CountOccurrences(code, token),
                        $"{name} must carry exactly {allowedCount} occurrence(s) of {token} — job-scheduling-" +
                        "design.md §7 rule 2 confines it to the sanctioned (file, token, count) exceptions in " +
                        "AttributeFenceAllowedFiles.");
                }
            }
        }

        // ── The JobsDebugger precondition — job-scheduling-design.md §7's own qualification: the schedule-time
        // write-write detection every dependency-edge tooth in this epic relies on is CONTINGENT on this
        // toggle, not on being in the Editor. Nothing else observed it before this. ─────────────────────────

        [Test]
        public void JobDebugger_IsEnabled_OrEveryDependencyEdgeToothInThisEpicIsVacuous()
        {
            Assert.IsTrue(JobsUtility.JobDebuggerEnabled,
                "the Editor's job debugger is OFF. Every dependency-edge tooth recorded against a dropped " +
                "`deps` edge in this epic (job-scheduling-design.md §7 rule 1 — the missing-edge " +
                "InvalidOperationException at Schedule) proves nothing while this reads false: the " +
                "schedule-time write-write detection is contingent on this toggle, not on merely running in " +
                "the Editor.");
        }

        // ── The sizing-owned-buffers consumer fence: no fence can check WHICH value bounds a loop (that is
        // dataflow, not lexical) — the failure that actually recurred three times was a borrowed loop bound
        // (AggregateJob, RibbonAggregateJob, then FillGatherJob); a STALE CONSUMER SET is what let the
        // third instance hide, not what recurred, and that stale-set failure IS mechanical. This counts
        // nodes, not tokens, so it never looks at a BORROWED sizing-owned-buffers field
        // (RibbonBatchJob.RingSubOffsets, RibbonAggregateJob.RingFeature, EarcutBatchJob.PolyHoleCount)
        // and cannot cry wolf on any of them.

        // Regex on the type plus any identifier, not a literal including the field NAME: a bare-string token
        // ("TriangulationBuffers Buffers;") would let a node declaring "TriangulationBuffers
        // FillBuffers;" walk straight past this fence — the exact evasion a rename invites.
        private static readonly Regex[] SizingOwnedBuffersFieldPatterns =
        {
            new Regex(@"TriangulationBuffers\s+\w+;"), new Regex(@"RibbonBuffers\s+\w+;"),
        };

        /// <summary>Verified today: exactly these seven declare a sizing-owned buffers struct as a field.
        /// Red on an EIGHTH means a new node joined this family — check what column it bounds its own loop by
        /// against job-scheduling-design.md §7 rule 2 before adding it here.</summary>
        private static readonly string[] KnownSizingOwnedBuffersConsumers =
        {
            "SizingJob.cs", "FillGatherJob.cs", "EarcutBatchJob.cs", "AggregateJob.cs",
            "RibbonSizingJob.cs", "RibbonBatchJob.cs", "RibbonAggregateJob.cs",
        };

        [Test]
        public void GraphNodes_DeclaringASizingOwnedBufferStruct_MatchTheKnownConsumerSet()
        {
            string jobsDir    = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            string meshingDir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Meshing");
            Assert.IsTrue(Directory.Exists(jobsDir), $"expected directory to exist at {jobsDir}");
            Assert.IsTrue(Directory.Exists(meshingDir), $"expected directory to exist at {meshingDir}");

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(jobsDir, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(meshingDir, "*.cs", SearchOption.AllDirectories));
            Assert.GreaterOrEqual(files.Count, 10,
                "precondition: the enumeration must find a plausible number of production files, or a moved " +
                "directory / wrong Application.dataPath join would make the consumer set below vacuously small");

            var consumers = new List<string>();
            foreach (string file in files)
            {
                string code = StripLineComments(File.ReadAllText(file));
                foreach (Regex pattern in SizingOwnedBuffersFieldPatterns)
                    if (pattern.IsMatch(code)) { consumers.Add(Path.GetFileName(file)); break; }
            }
            consumers.Sort();

            var expected = new List<string>(KnownSizingOwnedBuffersConsumers);
            expected.Sort();
            CollectionAssert.AreEqual(expected, consumers,
                "the set of files declaring a TriangulationBuffers/RibbonBuffers field no longer " +
                "matches KnownSizingOwnedBuffersConsumers — job-scheduling-design.md §7 rule 2 requires every " +
                "node in this family to bound its own loop by a column the sizing job resizes, never a " +
                "borrowed count; verify the new/removed file against that rule before updating this list.");
        }

        private static string FillMeshGraphSource()
        {
            string path = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs", "Fill", "FillMeshGraph.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            return File.ReadAllText(path);
        }

        private static int CountOccurrences(string text, string token)
            => Regex.Matches(text, Regex.Escape(token)).Count;

        /// <summary>Strips everything from <c>//</c> (including <c>///</c> XML doc comments) to end of line,
        /// so a token merely NAMED in prose — e.g. this file's own doc, or <c>FillMeshGraph.cs</c>'s "no
        /// <c>.AsArray()</c> at schedule time" rule doc, or its "the <c>.Run(n)</c> this scheduled path
        /// replaces" note — is not counted as a reference. Matches <c>StyledLineBuilderStructureTests</c>'s
        /// idiom.</summary>
        private static string StripLineComments(string text)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int idx = lines[i].IndexOf("//", System.StringComparison.Ordinal);
                if (idx >= 0) lines[i] = lines[i].Substring(0, idx);
            }
            return string.Join('\n', lines);
        }
    }
}
