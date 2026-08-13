// Unity EditMode only — reads source files under Application.dataPath. NOT registered in core-tests.csproj.

using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// IR stage B3: the structural teeth on the line consumer of Waist 1.
    ///
    /// <para><b>Landmine #5 — the fused-<c>RingAssemblyJob</c> fence.</b> <c>RingAssemblyJob</c> is a fused,
    /// <b>fill-only</b> stage: one pass applies fill's <c>rLen &lt; 3</c> filter, a degenerate-<b>area</b>
    /// filter, and exterior/hole classification. None of the three is meaningful for a line — a straight
    /// polyline has exactly zero area and would be dropped, and lines have no outer/hole concept at all. The
    /// behavioural falsifier is <c>StraightZeroAreaPolyline_StillRenders</c>; this is the structural one, and
    /// the two observe different things: a shortcut that reused the filter's <i>threshold</i> without naming
    /// the type would evade this file, and a shortcut that scheduled the job on a fixture whose geometry
    /// happens to survive would evade the behavioural one.</para>
    ///
    /// <para><b>Ownership, INVERTED by IR C1 P2.</b> The buffer is no longer transferred to the line builder —
    /// it is <b>BORROWED</b> from the pass-scoped <c>TileGeometryStore</c>, which lends the same instance to
    /// every style layer naming that source-layer. The builder must therefore mint <b>zero</b> buffers and
    /// dispose <b>zero</b>. A consumer that freed a borrowed buffer would free geometry its sibling layers are
    /// still reading, and on this path that second free is <b>loud</b>: the store hands back an array-backed
    /// buffer whose <c>Dispose</c> frees three real <c>NativeArray</c>s, and P2's R6 sweep measured the double
    /// free as <b>32 failures across 19 fixtures</b> — two with no line involvement at all, the
    /// heap-corruption signature. (The "disposing it is a silent no-op" caveat belongs to the
    /// <c>AsArray()</c>-view, list-backed mode, which line never borrows.) This instrument is structural not
    /// because the behavioural signal is absent, but because it names the rule at the source and fails
    /// deterministically on the one offending line instead of as a scatter of unrelated red fixtures.</para>
    /// </summary>
    [TestFixture]
    public class StyledLineBuilderStructureTests
    {
        private const string WriteMeshDataAnchor = "void WriteMeshData(";

        /// <summary>Types that belong to the FILL assembly path. Line must reference none of them.</summary>
        private static readonly string[] ForbiddenFillStageTokens =
        {
            "RingAssemblyJob", "EarcutJob", "RingClipJob", "FillMeshPipeline",
            "GlobeFillSubdivide", "PolygonAssembler", "AdoptClippedLists",
            "DegenerateThreshold", "SignedArea",
        };

        /// <summary>Tokens that must be PRESENT. Without them a renamed, gutted or deleted file would satisfy
        /// every "count is zero" claim above trivially — the easiest kind of tooth to make vacuous.</summary>
        // IR C1 P2: "MvtGeometryMaterializer" left this set with the mint it named. Its replacements are the
        // identifiers of the mechanism the file uses NOW — the borrowed buffer type and the ordinal join —
        // so a gutted or re-pointed file still cannot satisfy the zero-counts trivially.
        private static readonly string[] RequiredTokens =
        {
            "TileGeometryBuffers", "RingFeatureIdx", "FeatureGeometryType", "LineRibbonJob", "RingOffsets",
        };

        [Test]
        public void LineBuilderDoesNotTouchTheFillAssemblyStages()
        {
            string source = LineBuilderSource();
            string code   = StripLineComments(source);

            Assert.Greater(code.Trim().Length, 0, "precondition: the line builder source is non-empty");
            foreach (string token in RequiredTokens)
            {
                Assert.Greater(CountOccurrences(code, token), 0,
                    $"precondition: StyledLineTileBuilder must still reference '{token}' — without it this " +
                    "test's zero-counts would be satisfied by a gutted file");
            }

            foreach (string token in ForbiddenFillStageTokens)
            {
                Assert.AreEqual(0, CountOccurrences(code, token),
                    $"StyledLineTileBuilder must reference ZERO fill-assembly symbols — '{token}' found. " +
                    "Line iterates the shared buffer itself: it has no polygon or hole concept, and its " +
                    "filter is a COUNT threshold (>= 2), never an area threshold. A straight polyline has " +
                    "exactly zero signed area and RingAssemblyJob would drop it.");
            }
        }

        [Test]
        public void LineBuilderMintsNoBufferAndDisposesNone_ItBorrows()
        {
            string body = StripLineComments(ExtractMethodBody(LineBuilderSource(), WriteMeshDataAnchor));

            // Non-vacuity: an anchor miss would extract an empty or wrong body and pass every count below.
            Assert.Greater(body.Length, 0, "precondition: extracted a non-empty WriteMeshData body");
            StringAssert.Contains("LineRibbonJob", body,
                "precondition: the extracted body really is the one that builds the ribbon");
            StringAssert.Contains("geometry.RingFeatureIdx", body,
                "precondition: the extracted body really does READ the buffer it is claimed to borrow — " +
                "without this, a body that never touched geometry at all would satisfy both zero-counts");

            Assert.AreEqual(0, CountOccurrences(body, ".Materialize()"),
                "IR C1 P2 INVERTED this from 1 to 0. WriteMeshData must mint NOTHING: the source-layer's " +
                "buffer is materialized once per worker pass by TileGeometryStore and lent to every style " +
                "layer naming that source-layer. A mint here is the 61-decodes-per-pass regression B7 " +
                "retired, re-introduced one layer down.");
            Assert.AreEqual(0, CountOccurrences(body, "geometry.Dispose()"),
                "…and must free NOTHING. Disposing a BORROWED buffer frees geometry sibling layers are still " +
                "reading. That double free is LOUD here — the store lends an array-backed buffer, and R6 " +
                "measured it as 32 failures across 19 fixtures, two with no line involvement (heap " +
                "corruption). This tooth is structural to fail on the offending LINE, not because the " +
                "behavioural signal is missing.");
        }

        private static string LineBuilderSource()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Meshing",
                "StyledLineTileBuilder.cs");
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
