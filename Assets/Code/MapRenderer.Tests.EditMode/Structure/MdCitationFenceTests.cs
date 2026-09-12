// Unity EditMode only — shells out to git and reads source files via ShaderPropertyParser.RepoRoot.
// NOT registered in core-tests.csproj.
//
// UMR-128 prevention tooth. Block reconstruction is ported from the census script that found the
// original 21 phantom names / 51 sites: this repo hard-wraps comments — a real path can split across
// two `//` lines, which a line-bound scan both invents phantoms from (by splitting a real path) and
// hides real ones behind (by never rejoining them).
//
// Scope: only a citation that carries an actual `*.md` FILENAME is checked. A bare pointer with no
// filename (`§4`, `design doc §6`, `S20 stage doc §6`) is deliberately out of scope — deciding which
// document "design doc" means is a judgment call, not a mechanical sweep, and is not this tooth's job.
//
// This tooth pins the CURRENT set of cited documents: renaming, moving, or deleting a `.md` file that
// source cites reds this test. That is doing its job — update the citing comment(s) to the new name (or
// state the fact inline and drop the pointer, per this ticket's own repair rule), it is not a false alarm.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// Tripwire: no committed <c>.cs</c>/<c>.hlsl</c>/<c>.shader</c>/<c>.sh</c>/<c>.py</c> file under
    /// <c>Assets/</c> or <c>Tools/</c> (excluding <c>ThirdParty/</c>) may cite a <c>*.md</c> filename this
    /// repo does not track (matched by basename, not full path). Checks only that a citation RESOLVES,
    /// never that it is accurate — see this file's header for the bare-pointer scope fence and what a red
    /// here means.
    /// </summary>
    [TestFixture]
    public class MdCitationFenceTests
    {
        private static readonly Regex CommentMarker = new Regex(@"^\s*(///|//|\*/?|#)\s?");
        private static readonly Regex MdToken = new Regex(@"[A-Za-z0-9._/~-]*[A-Za-z0-9_~-]\.md\b");
        private static readonly string[] SourceExtensions = { ".cs", ".hlsl", ".shader", ".sh", ".py" };

        [Test]
        public void NoCommittedFile_CitesAnUntrackedMdPath()
        {
            string repoRoot = ShaderPropertyParser.RepoRoot;

            // The two lists are deliberately drawn from different populations. A citation resolves only
            // against COMMITTED documents — an untracked local .md exists on one machine and nowhere
            // else, which is the defect class this fence exists for. But the scan must read untracked
            // sources too, or it cannot see a phantom in the commit that introduces one.
            var trackedMdBasenames = new HashSet<string>(
                Git(repoRoot, "ls-files").Where(p => p.EndsWith(".md", StringComparison.Ordinal))
                    .Select(Path.GetFileName),
                StringComparer.Ordinal);

            List<string> sourceFiles = GitSourceFiles(repoRoot).Where(p =>
                (p.StartsWith("Assets/", StringComparison.Ordinal) ||
                 p.StartsWith("Tools/", StringComparison.Ordinal)) &&
                SourceExtensions.Any(ext => p.EndsWith(ext, StringComparison.Ordinal)) &&
                !p.Contains("/ThirdParty/")).ToList();

            // Non-vacuity: a broken git call or an empty checkout would make the offender scan below
            // pass trivially.
            Assert.That(sourceFiles.Count, Is.GreaterThan(500),
                $"precondition: only found {sourceFiles.Count} source files under Assets/Tools — the git " +
                "ls-files call or the path filter is broken.");
            Assert.That(trackedMdBasenames.Count, Is.GreaterThan(10),
                $"precondition: only found {trackedMdBasenames.Count} tracked .md files — the git ls-files " +
                "call is broken, which would make every citation look like a phantom.");

            var offenders = new List<string>();
            foreach (string relPath in sourceFiles)
            {
                string[] lines = File.ReadAllLines(Path.Combine(repoRoot, relPath));
                int i = 0;
                while (i < lines.Length)
                {
                    if (!CommentMarker.IsMatch(lines[i])) { i++; continue; }
                    int blockStartLine = i + 1;
                    var block = new StringBuilder();
                    while (i < lines.Length && CommentMarker.IsMatch(lines[i]))
                    {
                        string seg = CommentMarker.Replace(lines[i], "").Trim();
                        if (block.Length > 0)
                        {
                            char last = block[block.Length - 1];
                            bool blockEndsJoin = last == '/' || last == '-';
                            bool segStartsJoin = seg.Length > 0 && (seg[0] == '/' || seg[0] == '-');
                            if (!blockEndsJoin && !segStartsJoin) block.Append(' ');
                        }
                        block.Append(seg);
                        i++;
                    }

                    // Exact basename match only — NOT a suffix check. A wrapped path split into a
                    // bare fragment must not then pass by matching as a suffix of a real name such as
                    // "meshing-design.md"; that hole sits directly in the path of the likeliest phantom.
                    // This comment deliberately names no phantom filename: the scan below reads THIS
                    // file too, so an illustrative fake name here would fail the fence it documents.
                    foreach (Match m in MdToken.Matches(block.ToString()))
                    {
                        string basename = Path.GetFileName(m.Value);
                        if (!trackedMdBasenames.Contains(basename))
                            offenders.Add($"{relPath}:{blockStartLine}: {m.Value}");
                    }
                }
            }

            Assert.That(offenders, Is.Empty,
                "Tripwire (resolvability only, not accuracy): these comments cite a .md filename this repo " +
                "does not track. Either the document moved/was renamed — update the citing comment to its " +
                "new path — or it never belonged here: state the fact inline and delete the pointer, " +
                "because the document may live only in a private sibling repo, or nowhere at all:\n" +
                string.Join("\n", offenders));
        }

        /// <summary>
        /// Tracked files plus untracked-but-not-ignored ones. The second half is load-bearing:
        /// <c>git ls-files</c> alone lists only what is committed, so a brand-new file is invisible to
        /// this scan until AFTER it lands — meaning the fence could never catch a phantom citation in
        /// the same commit that introduces it. This test was itself merged with exactly that defect.
        /// </summary>
        private static List<string> GitSourceFiles(string repoRoot)
        {
            var files = Git(repoRoot, "ls-files");
            files.AddRange(Git(repoRoot, "ls-files --others --exclude-standard"));
            return files;
        }

        private static List<string> Git(string repoRoot, string args)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using Process proc = Process.Start(psi);
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            Assert.That(proc.ExitCode, Is.EqualTo(0), $"git {args} failed — is this a git checkout?");
            return output.Split('\n').Where(l => l.Length > 0).ToList();
        }
    }
}
