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
    /// IR stage B4: the fence that makes the Core/Jobs split enforceable rather than remembered.
    ///
    /// <para><b>This is a fence, not a migration target.</b> <c>MapRenderer.Core</c> references only
    /// <c>Unity.Mathematics</c> and <c>UniTask</c> today and has zero <c>NativeArray</c> uses, so nothing here
    /// had to be cleaned up. What this tooth guards is the <b>naive fix</b> B4 declined: adding
    /// <c>"Unity.Collections"</c> to <c>MapRenderer.Core.asmdef</c> so <c>SymbolFeatureExtractor</c> could read
    /// a <c>TileGeometryBuffers</c> without leaving Core. That one-line edit compiles, passes every existing
    /// test, and silently erases the split the epic's design section states — <i>Core keeps the managed
    /// evaluation surface and never reads coordinates; Jobs owns the blittable geometry</i>. B4 paid for the
    /// split by moving the extractor into <c>MapRenderer.Unity</c> instead (and 75 tests out of the fast
    /// <c>dotnet</c> loop); without this tooth that cost could be quietly refunded.</para>
    ///
    /// <para><b>The source scan is comment-stripped, and that is not optional.</b> Core carries a dozen prose
    /// mentions of <c>NativeArray</c>/<c>NativeList</c>/<c>Unity.Collections</c> — every one a doc comment
    /// saying "blittable, so a Burst job can hold it". A raw-text scan would be RED on landing.</para>
    /// </summary>
    [TestFixture]
    public class CoreAssemblyBoundaryTests
    {
        /// <summary>Code forms that mean "this file actually uses Unity.Collections", as opposed to naming it
        /// in prose.</summary>
        private static readonly string[] CollectionsCodeForms =
        {
            "using Unity.Collections", "NativeArray<", "NativeList<", "Unity.Collections.",
        };

        [Test]
        public void MapRendererCore_ReferencesNoUnityCollections()
        {
            // ── Clause 1: the asmdef.
            string coreAsmdefPath = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Core", "MapRenderer.Core.asmdef");
            Assert.IsTrue(File.Exists(coreAsmdefPath), $"expected the Core asmdef at {coreAsmdefPath}");
            string coreAsmdef = File.ReadAllText(coreAsmdefPath);

            // Non-vacuity: the file was really found and really parsed as a reference list — an empty or
            // unreadable asmdef would satisfy a "contains no Unity.Collections" claim trivially.
            List<string> coreReferences = ReferencesOf(coreAsmdef, coreAsmdefPath);
            Assert.GreaterOrEqual(coreReferences.Count, 2,
                "precondition: the Core asmdef must list at least two references — a parse that found none " +
                "would make the absence claim below vacuous");
            CollectionAssert.Contains(coreReferences, "Unity.Mathematics",
                "precondition: Unity.Mathematics is Core's math dependency and must be among the parsed " +
                "references — proof the parser sees real entries");

            foreach (string reference in coreReferences)
            {
                Assert.IsFalse(reference.Contains("Unity.Collections", StringComparison.Ordinal),
                    "MapRenderer.Core.asmdef must NOT reference Unity.Collections. Adding it is the one-line " +
                    "edit that would let coordinate-reading code stay in Core and erase the Core/Jobs split " +
                    "(Core keeps the managed evaluation surface; Jobs owns the blittable geometry). B4 moved " +
                    $"SymbolFeatureExtractor to MapRenderer.Unity rather than take it. Found: '{reference}'.");
            }

            // ── Clause 2: no source file under Core uses Unity.Collections in CODE.
            string coreRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Core");
            string[] coreFiles = Directory.GetFiles(coreRoot, "*.cs", SearchOption.AllDirectories);
            Assert.Greater(coreFiles.Length, 100,
                "precondition: the Core scan must visit a real corpus (>100 .cs files); a scan that visited " +
                "none would report 'no offenders' just as loudly");

            var offenders = new List<string>();
            foreach (string file in coreFiles)
            {
                string code = StripComments(File.ReadAllText(file));
                foreach (string form in CollectionsCodeForms)
                    if (code.Contains(form, StringComparison.Ordinal))
                        offenders.Add($"{file.Substring(coreRoot.Length)} ('{form}')");
            }

            // Non-vacuity (the positive control that matters): the SAME scan over MapRenderer.Jobs must find
            // Unity.Collections in both the asmdef and at least one source file. A zero over Core proves
            // nothing if the matcher cannot see the thing it reports as absent.
            string jobsAsmdefPath = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Jobs", "MapRenderer.Jobs.asmdef");
            Assert.IsTrue(File.Exists(jobsAsmdefPath), $"expected the Jobs asmdef at {jobsAsmdefPath}");
            List<string> jobsReferences = ReferencesOf(File.ReadAllText(jobsAsmdefPath), jobsAsmdefPath);
            CollectionAssert.Contains(jobsReferences, "Unity.Collections",
                "positive control: MapRenderer.Jobs.asmdef MUST reference Unity.Collections — if this fails, " +
                "the asmdef parser is broken and Core's clean result above means nothing");

            string jobsRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            int jobsSourceHits = 0;
            foreach (string file in Directory.GetFiles(jobsRoot, "*.cs", SearchOption.AllDirectories))
            {
                string code = StripComments(File.ReadAllText(file));
                foreach (string form in CollectionsCodeForms)
                    if (code.Contains(form, StringComparison.Ordinal)) { jobsSourceHits++; break; }
            }
            Assert.Greater(jobsSourceHits, 0,
                "positive control: the SAME comment-stripped source scan MUST find Unity.Collections code " +
                "forms under MapRenderer.Jobs — otherwise the scan is blind and Core's zero is meaningless");

            Assert.IsEmpty(offenders,
                "no .cs file under MapRenderer.Core may USE Unity.Collections (comment-stripped: Core " +
                "legitimately MENTIONS NativeArray in a dozen doc comments explaining blittability). " +
                $"Offenders: {string.Join(", ", offenders)}");
        }

        /// <summary>Pulls the <c>references</c> array out of an asmdef. Deliberately a narrow regex rather
        /// than a JSON parser — the test assembly has no JSON dependency, and the asmdef shape is fixed.</summary>
        private static List<string> ReferencesOf(string asmdef, string path)
        {
            Match block = Regex.Match(asmdef, @"""references""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
            Assert.IsTrue(block.Success, $"expected a 'references' array in {path}");

            var references = new List<string>();
            foreach (Match entry in Regex.Matches(block.Groups[1].Value, @"""([^""]+)"""))
                references.Add(entry.Groups[1].Value);
            return references;
        }

        /// <summary>Strips block comments and then everything from <c>//</c> (which covers <c>///</c>) to end
        /// of line. A grep guard, not a C# parser.</summary>
        private static string StripComments(string text)
        {
            string noBlocks = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
            string[] lines = noBlocks.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int idx = lines[i].IndexOf("//", StringComparison.Ordinal);
                if (idx >= 0) lines[i] = lines[i].Substring(0, idx);
            }
            return string.Join('\n', lines);
        }
    }
}
