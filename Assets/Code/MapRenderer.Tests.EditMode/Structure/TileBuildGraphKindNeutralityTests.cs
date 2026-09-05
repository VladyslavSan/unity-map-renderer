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
    /// The per-layer build-object stage's own T2 — <c>TileBuildGraph</c> retains no kind knowledge: every
    /// layer it touches is an <c>ILayerMeshBuild</c>, and this fence pins that the type cannot silently
    /// re-grow a per-kind dispatch the way it once held one (a <c>LayerRequestKind</c>-discriminated struct,
    /// three factories, two dispatch sites). Copies <see cref="WallChainCallerFenceTests"/>'s own mould
    /// exactly: same two scan roots, <c>AllDirectories</c>, comments stripped first, <c>&gt;= 50</c>
    /// non-vacuity floor.
    ///
    /// <para><b>Two assertions.</b> (a) is the total-footprint half — a fence over one file's CONTENTS is
    /// evaded by adding a partial, so this closes it at the declaration site instead of chasing file names.
    /// (b) is the content half — fourteen identifiers: the eleven pre-stage kind names, PLUS the three this
    /// stage creates (<see cref="FillLayerBuild"/>/<see cref="FillExtrusionLayerBuild"/>/<see cref="LineLayerBuild"/>).
    /// Without the three new names the fence would read green against
    /// <c>if (build is FillExtrusionLayerBuild ext)</c> — not the adversarial case, but what a fourth-kind
    /// stage reaches for by reflex. The graph may know only <c>ILayerMeshBuild</c>.</para>
    ///
    /// <para><b>Acknowledged limit — this does NOT close the evasion.</b> Neither assertion sees a
    /// HELPER-CLASS indirection: a new <c>TileBuildGraphKindDispatch.cs</c> holding the switch, called from
    /// <c>TileBuildGraph.cs</c>, passes both. No structure fence in this repo closes that shape, and
    /// inventing one here would be a regex chasing a design smell. What this buys is that kind knowledge
    /// cannot re-grow INSIDE the type, silently, which is how it grew the first time.</para>
    /// </summary>
    [TestFixture]
    public class TileBuildGraphKindNeutralityTests
    {
        private const string TargetFileName = "TileBuildGraph.cs";

        private static readonly Regex ClassDeclaration = new(
            @"\b(?:internal|public)?\s*(partial\s+)?(sealed\s+)?class\s+TileBuildGraph\b");

        private static readonly string[] ForbiddenKindIdentifiers =
        {
            // The eleven pre-stage names.
            "FillMeshGraph", "LineMeshGraph", "FillExtrusionMeshGraph",
            "FillGraphOutput", "LineGraphOutput", "FillExtrusionGraphOutput",
            "StyledFillTileBuilder", "StyledLineTileBuilder", "StyledFillExtrusionTileBuilder",
            "WallColumns", "LineLayerInput",
            // R2: the three this stage creates — without these the fence is blind to the reflex re-growth.
            "FillLayerBuild", "FillExtrusionLayerBuild", "LineLayerBuild",
        };

        [Test]
        public void TileBuildGraph_IsDeclaredExactlyOnce_AndNeverAsPartial()
        {
            List<string> files = ScanFiles();

            var matchingFiles = new List<string>();
            bool anyPartial = false;
            foreach (string path in files)
            {
                string stripped = StripLineComments(File.ReadAllText(path));
                foreach (Match m in ClassDeclaration.Matches(stripped))
                {
                    matchingFiles.Add(Path.GetFileName(path));
                    if (m.Groups[1].Success) anyPartial = true;
                }
            }

            Assert.AreEqual(1, matchingFiles.Count,
                $"expected exactly ONE declaration of `class TileBuildGraph` across both scan roots, found " +
                $"{matchingFiles.Count} ({string.Join(", ", matchingFiles)}) — a second file declaring it " +
                "(e.g. a `partial` split) is how a fence over one file's contents gets evaded.");
            Assert.AreEqual(TargetFileName, matchingFiles[0],
                $"the one declaration must live in {TargetFileName}, not a renamed/relocated file.");
            Assert.IsFalse(anyPartial,
                "TileBuildGraph must never be declared `partial` — forbidding it here closes the evasion at " +
                "the source rather than chasing file names.");
        }

        [Test]
        public void TileBuildGraph_ReferencesNoneOfTheFourteenKindIdentifiers()
        {
            List<string> files = ScanFiles();
            string targetPath = null;
            foreach (string path in files)
            {
                if (Path.GetFileName(path) == TargetFileName) { targetPath = path; break; }
            }
            Assert.IsNotNull(targetPath, $"precondition: expected to find {TargetFileName} under the scanned roots.");

            string stripped = StripLineComments(File.ReadAllText(targetPath));
            var violations = new List<string>();
            foreach (string identifier in ForbiddenKindIdentifiers)
            {
                if (Regex.IsMatch(stripped, @"\b" + identifier + @"\b"))
                    violations.Add(identifier);
            }

            Assert.IsEmpty(violations,
                $"{TargetFileName} still references kind-specific type(s): " + string.Join(", ", violations) +
                ". TileBuildGraph must know only ILayerMeshBuild — every kind-specific call belongs inside " +
                "that kind's own build type.");
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
            // WallChainCallerFenceTests' own scan.
            Assert.GreaterOrEqual(files.Count, 50,
                $"precondition: expected to scan at least 50 .cs files under MapRenderer.Jobs/ + " +
                $"MapRenderer.Unity/Rendering/ (found {files.Count}) — a path typo would silently scan nothing.");
            return files;
        }

        /// <summary>Strips everything from <c>//</c> to end of line, including <c>///</c> XML-doc lines — the
        /// same idiom <see cref="WallChainCallerFenceTests"/>'s own copy uses, for the same reason: this
        /// repo's docs deliberately keep past-tense prose naming retired call shapes, and that history is not
        /// what this fence exists to police.</summary>
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
