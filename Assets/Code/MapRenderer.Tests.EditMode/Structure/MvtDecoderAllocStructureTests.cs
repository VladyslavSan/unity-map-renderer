// Unity EditMode only — reads source under Application.dataPath. NOT registered in core-tests.csproj.

using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// Perf-zoom-stutter Rank 2: pins that <c>MvtDecoder.ReadPackedUInt32</c> builds its exact-sized
    /// result with a two-pass count-then-fill, not a growing <c>List&lt;uint&gt;</c> + <c>ToArray()</c>
    /// duplicate. Called twice per feature (tags, geometry command stream), so the retired intermediate
    /// was geometry-proportional GC garbage on the worker-thread decode path.
    /// </summary>
    [TestFixture]
    public class MvtDecoderAllocStructureTests
    {
        private const string ReadPackedUInt32Anchor = "private static uint[] ReadPackedUInt32(ProtobufReader r)";

        [Test]
        public void ReadPackedUInt32_AllocatesNoListIntermediateAndNoToArrayDuplicate()
        {
            string body = StripLineComments(ExtractMethodBody(DecoderSource(), ReadPackedUInt32Anchor));

            // Non-vacuity: an anchor miss, or a renamed/gutted method, would extract an empty body and
            // satisfy every zero-count below trivially.
            Assert.Greater(body.Trim().Length, 0, "precondition: extracted a non-empty ReadPackedUInt32 body");
            StringAssert.Contains("ReadVarint", body,
                "precondition: the extracted body really does decode varints — without this a body that " +
                "never read anything would satisfy both zero-counts vacuously");
            StringAssert.Contains("new uint[", body,
                "precondition: the exact-sized fill array must be present — without this the zero-counts " +
                "below would pass on a body that dropped the packed read altogether");

            Assert.AreEqual(0, CountOccurrences(body, "new List<"),
                "ReadPackedUInt32 must not stage into a growing List<uint> — it is called twice per " +
                "feature (tags, geometry command stream), so the List's internal doubling arrays are " +
                "geometry-proportional GC garbage on the worker-thread decode path.");
            Assert.AreEqual(0, CountOccurrences(body, ".ToArray()"),
                "ReadPackedUInt32 must not ToArray() a List into the result — that duplicates the whole " +
                "buffer on top of the List's own growth arrays.");
        }

        private static string DecoderSource()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Jobs", "Mvt", "MvtDecoder.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            return File.ReadAllText(path);
        }

        private static string ExtractMethodBody(string source, string signatureAnchor)
        {
            int anchorIndex = source.IndexOf(signatureAnchor, StringComparison.Ordinal);
            Assert.GreaterOrEqual(anchorIndex, 0,
                $"expected to find the method-definition anchor '{signatureAnchor}'");

            int braceStart = source.IndexOf('{', anchorIndex);
            Assert.GreaterOrEqual(braceStart, 0, "expected an opening brace after the method signature");

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
            Assert.Less(i, source.Length, "unbalanced braces scanning the method body");

            return source.Substring(braceStart, i - braceStart + 1);
        }

        private static int CountOccurrences(string text, string token)
            => Regex.Matches(text, Regex.Escape(token)).Count;

        /// <summary>Strips everything from <c>//</c> to end of line, so a symbol merely NAMED in a comment is
        /// not counted as a reference. Deliberately narrow — a grep guard, not a C# parser.</summary>
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
