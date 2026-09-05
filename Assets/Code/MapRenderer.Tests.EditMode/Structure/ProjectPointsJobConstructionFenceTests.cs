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
    /// job-scheduling-design.md §8 stage 5 (the wall-job-graph stage), tooth (b)'s hole — review R3.
    /// <c>ProjectPointsJob&lt;TProj&gt;.Run(n)</c> stays <c>public</c> and cannot itself be source-text
    /// fenced (an instance method called after a multi-line object-initializer block has no fixed textual
    /// shape a regex can anchor — see <see cref="FillMeshPipelineRetirementFenceTests"/>'s own class doc).
    /// Fencing the CONSTRUCTION SITE instead closes the hole without needing to anchor <c>.Run</c> at all:
    /// if only <c>ProjectPointsJob.cs</c> (its own declaration) and <c>ProjectionDispatch.cs</c> (its sole
    /// scheduler) may name <c>ProjectPointsJob</c>, nothing else can construct an instance to call anything
    /// on it, <c>.Run()</c> included. Same idiom as
    /// <c>WallChainCallerFenceTests.WallQuadJob_AppearsInExactlyTheTwoExpectedProductionFiles</c>:
    /// identifier-anchored, comments stripped first (a HISTORICAL prose mention must not fail the fence, only
    /// a real reference in CODE does), scanning every <c>.cs</c> file under <c>MapRenderer.Jobs/</c> and
    /// <c>MapRenderer.Unity/Rendering/</c>, both <c>AllDirectories</c>, with the same <c>&gt;= 50</c>
    /// non-vacuity guard.
    /// </summary>
    [TestFixture]
    public class ProjectPointsJobConstructionFenceTests
    {
        private static readonly string[] ExpectedFiles =
        {
            "ProjectPointsJob.cs", "ProjectionDispatch.cs",
        };

        [Test]
        public void ProjectPointsJob_AppearsInExactlyTheTwoExpectedProductionFiles()
        {
            string jobsDir  = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            string unityDir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering");
            Assert.IsTrue(Directory.Exists(jobsDir), $"expected directory to exist: {jobsDir}");
            Assert.IsTrue(Directory.Exists(unityDir), $"expected directory to exist: {unityDir}");

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(jobsDir, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(unityDir, "*.cs", SearchOption.AllDirectories));

            // Non-vacuity: guards a path typo silently scanning zero files — same floor as the sibling fences.
            Assert.GreaterOrEqual(files.Count, 50,
                $"precondition: expected to scan at least 50 .cs files under MapRenderer.Jobs/ + " +
                $"MapRenderer.Unity/Rendering/ (found {files.Count}) — a path typo would silently scan nothing.");

            var actual = new List<string>();
            foreach (string path in files)
            {
                string stripped = StripLineComments(File.ReadAllText(path));
                if (Regex.IsMatch(stripped, @"\bProjectPointsJob\b"))
                    actual.Add(Path.GetFileName(path));
            }
            actual.Sort();

            var expected = new List<string>(ExpectedFiles);
            expected.Sort();

            Assert.AreEqual(expected, actual,
                "ProjectPointsJob must appear in exactly its declaring file and its sole scheduler — a THIRD " +
                "file referencing it means something else can now construct the job directly and reach " +
                "Run(n) synchronously, exactly the hole this fence exists to close. A MISSING expected file " +
                "means the job or its scheduler was renamed without updating this fence.");
        }

        /// <summary>Strips everything from <c>//</c> to end of line, including <c>///</c> XML-doc lines —
        /// the same idiom every sibling fence in this directory uses.</summary>
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
