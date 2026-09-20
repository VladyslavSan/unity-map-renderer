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
    /// job-scheduling-design.md §8 stage 5 (the wall-job-graph stage), tooth (d) — a caller fence for a claim
    /// no runtime test can make: Unity has no manual/event <c>JobHandle</c>, so a test cannot hold the wall
    /// graph unsatisfied to observe "still running", and the job safety system tracks the HANDLE rather than
    /// execution state, so reading a wall column early throws whether or not the job ran. Both readings this
    /// fence would otherwise want are unavailable — see <see cref="FillMeshPipelineRetirementFenceTests"/>'s
    /// own mould, which this one copies: identifier-anchored, comments stripped first (a HISTORICAL prose
    /// mention — this epic's docs are full of them — must not fail the fence; only a real reference in CODE
    /// does), scanning every <c>.cs</c> file under <c>MapRenderer.Jobs/</c> and
    /// <c>MapRenderer.Unity/Rendering/</c>, both <c>AllDirectories</c>, with the same <c>&gt;= 50</c>
    /// non-vacuity guard.
    ///
    /// <para>Two assertions, together with ordinal 5's compile-enforced loss of <c>BuildLayerInput</c>'s
    /// <c>out WallColumns</c> and tooth (b)'s deleted synchronous <c>ProjectionDispatch.Run</c>/<c>RunTyped</c>,
    /// make "the wall chain runs inside the prologue body" unrepresentable:</para>
    /// <list type="bullet">
    /// <item><c>StyledFillExtrusionTileBuilder.cs</c> — the prologue's own file — contains none of
    /// <c>WallQuadJob</c>, <c>RingSelectJob</c>, <c>TileToGeoJob</c>, <c>ProjectionDispatch</c>,
    /// <c>WriteWalls</c>.</item>
    /// <item><c>WallQuadJob</c> appears in EXACTLY TWO production files, named
    /// <c>StyledFillExtrusionTileBuilder.WallJob.cs</c> (its declaration) and <c>FillExtrusionMeshGraph.cs</c>
    /// (its sole scheduler) — a named count, not "at least one" or "not in file X": <c>WallQuadJob</c> stays
    /// nested in a partial file of <c>StyledFillExtrusionTileBuilder</c>, so a fence naming only the prologue
    /// file would be evaded by re-inlining the job's construction into a THIRD partial of the same type
    /// (R4, job-scheduling-design.md §5(d)'s own note on the plan's earlier, self-contradicting three-bullet
    /// version).</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class WallChainCallerFenceTests
    {
        private const string PrologueFileName = "StyledFillExtrusionTileBuilder.cs";

        private static readonly string[] PrologueForbiddenIdentifiers =
        {
            "WallQuadJob", "RingSelectJob", "TileToGeoJob", "ProjectionDispatch", "WriteWalls",
        };

        private static readonly string[] ExpectedWallQuadJobFiles =
        {
            "StyledFillExtrusionTileBuilder.WallJob.cs", "FillExtrusionMeshGraph.cs",
        };

        [Test]
        public void Prologue_ReferencesNoneOfTheWallChainNodes()
        {
            List<string> files = ScanFiles();
            string prologuePath = null;
            foreach (string path in files)
            {
                if (Path.GetFileName(path) == PrologueFileName) { prologuePath = path; break; }
            }
            Assert.IsNotNull(prologuePath, $"precondition: expected to find {PrologueFileName} under the scanned roots.");

            string stripped = StripLineComments(File.ReadAllText(prologuePath));
            var violations = new List<string>();
            foreach (string identifier in PrologueForbiddenIdentifiers)
            {
                if (Regex.IsMatch(stripped, @"\b" + identifier + @"\b"))
                    violations.Add(identifier);
            }

            Assert.IsEmpty(violations,
                $"{PrologueFileName} — the prologue's own file — still references the wall chain directly: " +
                string.Join(", ", violations) + ". The wall chain must be reachable only through " +
                "FillExtrusionMeshGraph.Schedule, never inline in the prologue body.");
        }

        [Test]
        public void WallQuadJob_AppearsInExactlyTheTwoExpectedProductionFiles()
        {
            List<string> files = ScanFiles();

            var actual = new List<string>();
            foreach (string path in files)
            {
                string stripped = StripLineComments(File.ReadAllText(path));
                if (Regex.IsMatch(stripped, @"\bWallQuadJob\b"))
                    actual.Add(Path.GetFileName(path));
            }
            actual.Sort();

            var expected = new List<string>(ExpectedWallQuadJobFiles);
            expected.Sort();

            Assert.AreEqual(expected, actual,
                "WallQuadJob must appear in exactly its declaring file and its sole scheduler — a THIRD file " +
                "referencing it (e.g. a re-inlined construction back into the prologue's partial-class family) " +
                "means the wall chain became reachable from somewhere this fence does not expect, and a " +
                "MISSING expected file means the job was renamed or its scheduler changed without updating " +
                "this fence.");
        }

        private static List<string> ScanFiles()
        {
            string jobsDir  = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            string unityDir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering");
            Assert.IsTrue(Directory.Exists(jobsDir), $"expected directory to exist: {jobsDir}");
            Assert.IsTrue(Directory.Exists(unityDir), $"expected directory to exist: {unityDir}");

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(jobsDir, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(unityDir, "*.cs", SearchOption.AllDirectories));

            // Non-vacuity: guards a path typo silently scanning zero files — same floor as
            // FillMeshPipelineRetirementFenceTests' own scan.
            Assert.GreaterOrEqual(files.Count, 50,
                $"precondition: expected to scan at least 50 .cs files under MapRenderer.Jobs/ + " +
                $"MapRenderer.Unity/Rendering/ (found {files.Count}) — a path typo would silently scan nothing.");
            return files;
        }

        /// <summary>Strips everything from <c>//</c> to end of line, including <c>///</c> XML-doc lines — the
        /// same idiom <see cref="FillMeshPipelineRetirementFenceTests"/>'s own copy uses, for the same
        /// reason: this repo's docs deliberately keep past-tense prose naming retired call shapes, and that
        /// history is not what this fence exists to police.</summary>
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
