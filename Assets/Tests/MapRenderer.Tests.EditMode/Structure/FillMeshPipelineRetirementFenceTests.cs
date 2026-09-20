// Unity EditMode only — reads source files under Application.dataPath. NOT registered in core-tests.csproj.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// job-scheduling-design.md §8 stage 4 Group B, B.9 — the stop-rule fence: once fill AND the extrusion
    /// roof are both graph-arm, nothing in production may still reference a symbol Group B retired. The
    /// PREDICATE — every symbol whose only production caller was the synchronous fill pipeline
    /// (<c>FillMeshPipeline.Schedule</c>, its two synchronous mesh writers, or their globe/count-rule
    /// scaffolding), which lost that caller when the graph became the only mesher — not a hand-picked list:
    /// <see cref="ForbiddenPatterns"/> below must derive from re-reading the plan's B.1–B.5 deletion rows
    /// each time a member is added there, not from what a prior pass happened to name.
    ///
    /// Identifier-anchored (lessons-learned "a source-text fence must match the IDENTIFIER") — scans every
    /// <c>.cs</c> file under <c>MapRenderer.Jobs/</c> and <c>MapRenderer.Unity/Rendering/</c>, comments
    /// stripped first so a HISTORICAL prose mention of a retired name (this epic's docs are full of them,
    /// deliberately — "the synchronous entry point Run/RunTyped is retired with its callers X, Y") does not
    /// fail the fence; only a real reference in CODE does.
    ///
    /// <para><b>2026-09-04.</b> <c>ProjectionDispatch.Run</c>/<c>RunTyped</c> went through both halves of
    /// this predicate in one day. Group B retired them with their only caller at the time,
    /// <c>FillMeshPipeline.Schedule</c>. The wall-job stage then gave them a new, different caller:
    /// <c>StyledFillExtrusionTileBuilder.WriteWalls</c> dispatched its projection step through
    /// <c>ProjectionDispatch.Run</c> because that call site sat inside the prologue's off-main
    /// <c>IWorkScheduler</c> worker body, where job-scheduling-design.md §4 rule 1 made <c>.Schedule()</c>
    /// illegal there (the job system schedules from the main thread only) — so both patterns were REMOVED
    /// from the list below for that stage. The wall-job-GRAPH stage (same day) retired <c>WriteWalls</c>
    /// itself: the wall chain moved to <c>FillExtrusionMeshGraph.Schedule</c>, which schedules the wall
    /// projection step the same way the roof always has — there is no longer an off-main call site needing
    /// <c>.Run()</c>. Both patterns are RE-ADDED below; the predicate again classifies them as retired, and
    /// this is the fence tracking its own rule a second time, not weakening or over-correcting it.
    /// <c>GlobeFillSubdivideDispatch.Run</c>/<c>RunTyped</c> stay banned throughout — NOT because the class is
    /// deleted (it is not: it is live at <c>GlobeFillSubdivider.cs:202</c> with a production caller,
    /// <c>FillMeshGraph.cs:287</c>, through its surviving <c>Schedule</c> entry point and its <c>Default*</c>
    /// constants), but because only its synchronous <c>Run</c>/<c>RunTyped</c> members were retired with
    /// Group B — the same predicate as <c>ProjectionDispatch</c>, never landing on a different verdict because
    /// nothing has ever given THIS pair a new caller.</para>
    ///
    /// <para><b>Tooth (b)'s hole, CLOSED (wall-job-graph stage, review R3):</b>
    /// <c>ProjectPointsJob&lt;TProj&gt;.Run(n)</c> itself stays <c>public</c>, so a caller that constructs the
    /// job directly and calls <c>.Run()</c> on the instance could still reach the kernel synchronously — a
    /// source-text regex cannot anchor that reliably, unlike every pattern in <see cref="ForbiddenPatterns"/>
    /// (a static <c>ClassName.Method(</c> call): an instance method called after a multi-line
    /// object-initializer block has no fixed textual shape to match. But the CONSTRUCTION SITE does: fencing
    /// "which files may name <c>ProjectPointsJob</c> at all" makes the `.Run` anchoring problem moot, because
    /// nothing outside those two files can construct the job to call anything on it —
    /// <see cref="ProjectPointsJobConstructionFenceTests"/> is that fence, the same exactly-N-named-files
    /// idiom <c>WallChainCallerFenceTests.WallQuadJob_AppearsInExactlyTheTwoExpectedProductionFiles</c>
    /// uses.</para>
    ///
    /// <para><b>2026-09-05 (vestige sweep).</b> Predicate extension: the retired synchronous
    /// <c>WriteMeshData</c> methods (<c>StyledFillTileBuilder</c>, <c>StyledFillExtrusionTileBuilder</c>,
    /// <c>StyledLineTileBuilder</c>) join the predicate — each had zero production callers, same as every
    /// symbol above, and moved verbatim to <c>MapRenderer.Tests</c>'s <c>SyncMeshWrite</c>.
    /// <see cref="WriteMeshDataCannotGrowBackIntoTheBuilderSources"/> is a DECLARATION-FORM assertion
    /// (<c>\bWriteMeshData\s*[(&lt;]</c>, anchoring the identifier immediately followed by an argument list
    /// or a generic-arity bracket), not a call-form pattern like the ones above: the risk here is the method
    /// growing BACK with a different modifier or arity (<c>internal static void WriteMeshData&lt;T&gt;()</c>
    /// would satisfy a call-form ban but still be exactly the regrowth this stage exists to prevent), and
    /// only the declaration form sees that. Scoped to the three builder source files rather than the whole
    /// scan above — <c>WriteMeshData</c> is not a globally-retired identifier the way the others are; it is
    /// retired only FROM these three declaration sites, and remains a legitimate name for the test
    /// assembly's own <c>SyncMeshWrite</c> methods and for historical prose everywhere else.</para>
    /// </summary>
    [TestFixture]
    public class FillMeshPipelineRetirementFenceTests
    {
        /// <summary>Every symbol retired (job-scheduling-design.md §8 stage 4 B.1–B.5, plus
        /// <c>ProjectionDispatch.Run</c>/<c>RunTyped</c>'s own re-retirement in the wall-job-graph stage —
        /// see the class doc) that STILL has no production caller, one pattern each — a call form where a
        /// bare identifier would false-positive on an unrelated member of the same name, a word-boundary bare
        /// match otherwise. <c>Run\s*\(</c> deliberately does NOT match <c>RunTyped(</c>, hence the separate
        /// <c>RunTyped</c> entries for both <c>GlobeFillSubdivideDispatch</c> and
        /// <c>ProjectionDispatch</c>.</summary>
        private static readonly (string Label, Regex Pattern)[] ForbiddenPatterns =
        {
            ("FillMeshPipeline.Schedule(",        new Regex(@"\bFillMeshPipeline\.Schedule\s*\(")),
            ("TileMeshBuffers",                    new Regex(@"\bTileMeshBuffers\b")),
            ("WriteGlobeSubdivided",               new Regex(@"\bWriteGlobeSubdivided\b")),
            ("WriteGlobeRoof",                     new Regex(@"\bWriteGlobeRoof\b")),
            ("WriteFlatRoof",                      new Regex(@"\bWriteFlatRoof\b")),
            ("GlobeFillSubdivideDispatch.Run(",     new Regex(@"\bGlobeFillSubdivideDispatch\.Run\s*\(")),
            ("GlobeFillSubdivideDispatch.RunTyped", new Regex(@"\bGlobeFillSubdivideDispatch\.RunTyped\b")),
            ("ProjectionDispatch.Run(",             new Regex(@"\bProjectionDispatch\.Run\s*\(")),
            ("ProjectionDispatch.RunTyped",         new Regex(@"\bProjectionDispatch\.RunTyped\b")),
            ("CountsFromArrayLength",               new Regex(@"\bCountsFromArrayLength\b")),
            ("SrcVertCount",                        new Regex(@"\bSrcVertCount\b")),
            ("SrcIndexCount",                       new Regex(@"\bSrcIndexCount\b")),
        };

        [Test]
        public void ProductionSources_ContainNoRetiredSynchronousPipelineReferences()
        {
            string jobsDir   = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            string unityDir  = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering");
            Assert.IsTrue(Directory.Exists(jobsDir), $"expected directory to exist: {jobsDir}");
            Assert.IsTrue(Directory.Exists(unityDir), $"expected directory to exist: {unityDir}");

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(jobsDir, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(unityDir, "*.cs", SearchOption.AllDirectories));

            // Non-vacuity: guards a path typo silently scanning zero files.
            Assert.GreaterOrEqual(files.Count, 50,
                $"precondition: expected to scan at least 50 .cs files under MapRenderer.Jobs/ + " +
                $"MapRenderer.Unity/Rendering/ (found {files.Count}) — a path typo would silently scan nothing.");

            var violations = new List<string>();
            foreach (string path in files)
            {
                string stripped = StripLineComments(File.ReadAllText(path));
                foreach ((string label, Regex pattern) in ForbiddenPatterns)
                {
                    if (pattern.IsMatch(stripped))
                        violations.Add($"{path}: '{label}'");
                }
            }

            Assert.IsEmpty(violations,
                "production source under MapRenderer.Jobs/ or MapRenderer.Unity/Rendering/ still references a " +
                "symbol Group B retired — the graph is the only mesher now:\n" +
                string.Join("\n", violations));
        }

        /// <summary>Vestige sweep, 2026-09-05: the retired synchronous <c>WriteMeshData</c> methods cannot
        /// grow back into the three builder source files that used to declare them. Identifier-anchored,
        /// declaration-form (<c>\bWriteMeshData\s*[(&lt;]</c>) rather than the call-form patterns above — see
        /// the class doc's dated paragraph for why: the risk is the method growing back with a different
        /// modifier or arity, which only the declaration form sees.</summary>
        [Test]
        public void WriteMeshDataCannotGrowBackIntoTheBuilderSources()
        {
            string meshingDir = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Meshing");
            Assert.IsTrue(Directory.Exists(meshingDir), $"expected directory to exist: {meshingDir}");

            string[] files =
            {
                Path.Combine(meshingDir, "StyledFillTileBuilder.cs"),
                Path.Combine(meshingDir, "StyledFillExtrusionTileBuilder.cs"),
                Path.Combine(meshingDir, "StyledLineTileBuilder.cs"),
            };
            var declarationPattern = new Regex(@"\bWriteMeshData\s*[(<]");

            var violations = new List<string>();
            foreach (string path in files)
            {
                Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
                string stripped = StripLineComments(File.ReadAllText(path));
                if (declarationPattern.IsMatch(stripped))
                    violations.Add(path);
            }

            Assert.IsEmpty(violations,
                "a builder source file still declares WriteMeshData — the synchronous convenience method " +
                "was retired with zero production callers and moved to MapRenderer.Tests.SyncMeshWrite; it " +
                "must not grow back here under any modifier or arity:\n" + string.Join("\n", violations));
        }

        /// <summary>Strips everything from <c>//</c> to end of line — the same narrow grep-guard idiom
        /// <c>TileGeometryBuffersOwnershipTests</c> uses. Also removes every <c>///</c> XML-doc line (a
        /// <c>///</c> line IS a <c>//</c> line syntactically, so this is the same pass): this repo's docs
        /// deliberately keep past-tense prose naming these retired symbols as the reason they were retired,
        /// and that history is not what this fence exists to police.</summary>
        private static string StripLineComments(string text)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int idx = lines[i].IndexOf("//", StringComparison.Ordinal);
                if (idx >= 0) lines[i] = lines[i].Substring(0, idx);
            }
            return string.Join('\n', lines);
        }
    }
}
