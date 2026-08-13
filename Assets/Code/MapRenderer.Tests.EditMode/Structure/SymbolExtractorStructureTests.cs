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
    /// IR stage B4: the structural teeth on the symbol consumer of Waist 1, plus the assembly-boundary fence
    /// the stage exists to establish.
    ///
    /// <para><b>Landmine #5 — the fused-<c>RingAssemblyJob</c> fence, symbol form.</b> Symbol has no polygon,
    /// hole, area or triangulation concept and rejects Polygon features outright, so every fill-assembly stage
    /// is off-limits to it. The behavioural half is impossible to write here (symbol never produces triangles),
    /// which is exactly why the fence is structural.</para>
    ///
    /// <para><b>Ownership, INVERTED by IR C1 P2.</b> Waist 1 no longer <i>transfers</i> the buffer to
    /// <c>Extract</c> — <c>Extract</c> <b>BORROWS</b> it from the worker pass's <c>TileGeometryStore</c>,
    /// which lends the same instance to every symbol layer naming that source-layer and frees it at the end
    /// of the pass. So <c>Extract</c> must mint <b>zero</b> buffers and dispose <b>zero</b>. Freeing a
    /// borrowed buffer is <b>loud</b> on this path: the store lends an array-backed buffer whose
    /// <c>Dispose</c> frees three real <c>NativeArray</c>s, and P2's R6 sweep measured that double free as
    /// <b>32 failures across 19 fixtures</b>, two of them with no line involvement at all — the
    /// heap-corruption signature. (B1's "a mis-freed <c>AsArray()</c> view is a silent no-op" applies to the
    /// list-backed mode, which symbol never borrows.) The instrument is structural because it names the rule
    /// at the source and fails deterministically on the offending line, not because the behaviour goes
    /// unobserved.</para>
    ///
    /// <para>The one construction <c>Extract</c> may still make is its <b>private fallback store</b>, for the
    /// callers (tests, the demo path) that pass none. It is pinned at exactly one, outside every loop: a
    /// store built per feature would be the per-(layer, feature) mint B7 retired, wearing a new name.</para>
    ///
    /// <para>Assertions are comment-stripped greps over call forms, not a C# parser. That limitation is stated
    /// in each message rather than papered over.</para>
    /// </summary>
    [TestFixture]
    public class SymbolExtractorStructureTests
    {
        private const string ExtractAnchor = "public static void Extract(";

        /// <summary>Types that belong to the FILL assembly path. Symbol must reference none of them.</summary>
        private static readonly string[] ForbiddenFillStageTokens =
        {
            "RingAssemblyJob", "EarcutJob", "RingClipJob", "FillMeshPipeline",
            "GlobeFillSubdivide", "PolygonAssembler", "AdoptClippedLists",
            "DegenerateThreshold", "SignedArea",
        };

        /// <summary>Tokens that must be PRESENT. Without them a renamed, gutted or deleted file would satisfy
        /// every "count is zero" claim below trivially — the easiest kind of tooth to make vacuous.</summary>
        // IR C1 P2 dropped "MvtGeometryMaterializer" (the mint it named) for the identifiers of the
        // mechanism the file uses NOW. IR C1 P3 does the same again, one step further: the store is gone, so
        // "GetOrMaterialize" is REPLACED (not removed — a required set with entries deleted is a disarmed
        // test) by "tileLayer.Geometry", the expression the file reads the borrowed buffer through.
        private static readonly string[] RequiredTokens =
        {
            "TileGeometryBuffers", "tileLayer.Geometry", "RingFeatureIdx", "RingOffsets",
            "LineAnchorPlacement", "EmitAtAnchor",
        };

        // ── T4: the ownership contract ──────────────────────────────────────────────────────────────

        [Test]
        public void SymbolExtractMintsNoBufferAndDisposesNone_ItBorrows()
        {
            string body = StripComments(ExtractMethodBody(ExtractorSource(), ExtractAnchor));

            // Non-vacuity: an anchor miss would extract an empty or wrong body and pass every count below.
            Assert.Greater(body.Trim().Length, 0, "precondition: extracted a non-empty Extract body");
            StringAssert.Contains("LineAnchorPlacement", body,
                "precondition: the extracted body really is the one that places anchors");
            StringAssert.Contains("EmitAtAnchor", body,
                "precondition: the extracted body really is the one that emits labels");
            StringAssert.Contains("geometry.RingFeatureIdx", body,
                "precondition: the extracted body really does READ the buffer it is claimed to borrow — " +
                "a body that never touched geometry would satisfy both zero-counts trivially");

            Assert.AreEqual(0, CountOccurrences(body, ".Materialize()"),
                "IR C1 P2 INVERTED this from 1 to 0. Extract must mint NOTHING: the source-layer's buffer is " +
                "materialized once inside the DECODE (IR C1 P3) and owned by the decoded layer, which lends " +
                "it to every consumer naming that source-layer — across both cadences of a kick.");
            Assert.AreEqual(0, CountOccurrences(body, "geometry.Dispose()"),
                "…and must free NOTHING. Disposing a BORROWED buffer frees geometry sibling layers are still " +
                "reading. That double free is LOUD here — the layer lends an array-backed buffer, and R6 " +
                "measured it as 32 failures across 19 fixtures, two with no line involvement (heap " +
                "corruption). This tooth is structural to fail on the offending LINE, not because the " +
                "behavioural signal is missing.");

            // IR C1 P3 — the three store clauses that used to sit here are RETIRED WITH THEIR SUBJECT, not
            // dropped to make the file pass. They pinned (a) that Extract never disposes the caller's store,
            // (b) that it builds exactly one private fallback store, and (c) that the fallback is hoisted
            // above the try. There is no store: geometry belongs to the layer, so there is no borrowed
            // container to free, no fallback to hoist and no loop to hoist it out of. What the three of them
            // were collectively protecting — "this method mints nothing and frees nothing" — is stated
            // whole by the two zero-counts above, plus the clause below that the ONE remaining way to
            // reintroduce a private mint is absent.
            Assert.AreEqual(0, CountOccurrences(body, "new MvtGeometryMaterializer("),
                "Extract must construct NO producer of its own. This is the only remaining shape of the " +
                "per-(layer, feature) mint the epic retired: with the store gone, a private materializer is " +
                "how a consumer would silently stop sharing the layer's buffer — and it is OUTPUT-NEUTRAL, " +
                "so no behavioural test in the suite would notice.");
            Assert.AreEqual(1, CountOccurrences(NormaliseWhitespace(body),
                    "TileGeometryBuffers geometry = tileLayer.Geometry;"),
                "…and it must obtain the buffer in exactly one way: read once off the resolved layer. " +
                "(Whitespace-normalised grep, not a parser: it pins the exact form, so a second, differently " +
                "sourced buffer in the same body is visible.)");
        }

        // ── T7: landmine #5, plus the Q2 claim that no production code decodes MVT geometry any more ──

        [Test]
        public void SymbolExtractorTouchesNoFillAssemblyStage_AndNoProductionCodeDecodesMvtGeometry()
        {
            string code = StripComments(ExtractorSource());

            Assert.Greater(code.Trim().Length, 0, "precondition: the symbol extractor source is non-empty");
            foreach (string token in RequiredTokens)
            {
                Assert.Greater(CountOccurrences(code, token), 0,
                    $"precondition: SymbolFeatureExtractor must still reference '{token}' — without it this " +
                    "test's zero-counts would be satisfied by a gutted file");
            }

            foreach (string token in ForbiddenFillStageTokens)
            {
                Assert.AreEqual(0, CountOccurrences(code, token),
                    $"SymbolFeatureExtractor must reference ZERO fill-assembly symbols — '{token}' found. " +
                    "Symbol has no polygon, hole or area concept: it rejects Polygon outright, a Point " +
                    "feature's 1-point path has no area at all, and a straight road has exactly zero.");
            }

            // Clause 2 — Q2: MvtGeometry.Decode has no production call site left anywhere.
            const string decodeCallForm = "MvtGeometry.Decode(";
            var offenders = new List<string>();
            foreach (string assembly in new[] { "MapRenderer.Core", "MapRenderer.Jobs", "MapRenderer.Unity" })
            {
                string root = Path.Combine(Application.dataPath, "Code", assembly);
                Assert.IsTrue(Directory.Exists(root), $"expected production assembly root at {root}");
                foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                    if (CountOccurrences(StripComments(File.ReadAllText(file)), decodeCallForm) > 0)
                        offenders.Add(file.Substring(Application.dataPath.Length));
            }

            // Positive control: a zero across three assemblies means nothing if the matcher is broken.
            string testRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Tests.EditMode");
            int testAssemblyHits = 0;
            foreach (string file in Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories))
                testAssemblyHits += CountOccurrences(StripComments(File.ReadAllText(file)), decodeCallForm);
            Assert.Greater(testAssemblyHits, 10,
                $"precondition (positive control): the SAME scan over the test assembly must find " +
                $"'{decodeCallForm}' many times — it is the differential oracle B3 and B4 measure against. " +
                $"Found {testAssemblyHits}; a low number means the matcher, not production, is what changed.");

            Assert.IsEmpty(offenders,
                $"ZERO production files may call '{decodeCallForm}' — B4 retired the last one " +
                "(SymbolFeatureExtractor). The TYPE stays: it is the spec transcription, the parity oracle " +
                $"MvtDecodeJob is measured against, and Tools/core-tests' ground truth. Offenders: " +
                string.Join(", ", offenders));
        }

        // ── T-fence: THE NAMED FENCE, as a structural clause on the moved extractor ─────────────────

        /// <summary>
        /// T-fence — the three tile-space consumers inside <c>Extract</c> are handed TILE-SPACE identifiers.
        /// <c>Extract</c> holds three coordinate representations at once (<c>path</c>/<c>densePath</c>
        /// tile-local, <c>ups</c>/<c>lonLat</c> geodetic, <c>pathRender</c>/<c>anchor</c> projected), and the
        /// epsilons downstream are calibrated to tile-integer magnitude, so "just pass the projected one" is
        /// one wrong line away.
        /// <para><b>Stated limitation:</b> this is a call-form grep, not a C# parser — it pins the exact
        /// argument text of three call sites and nothing more. The fence is primarily carried by T1, which
        /// compares tile-local coordinates element-wise.</para>
        /// </summary>
        [Test]
        public void SymbolExtractHandsTileSpacePathsToTheTileSpaceConsumers()
        {
            string body = StripComments(ExtractMethodBody(ExtractorSource(), ExtractAnchor));
            string flat = NormaliseWhitespace(body);

            // Non-vacuity: all three call forms must be present at all, or every claim below is about a
            // method that does not make these calls.
            foreach (string callForm in new[]
                     { "LineCurvatureSubdivision.Subdivide(", "LineAnchorPlacement.Compute(", "KeepAnchorsInsideTile(" })
            {
                Assert.Greater(CountOccurrences(flat, callForm), 0,
                    $"precondition: Extract must still call '{callForm}' — otherwise this fence guards nothing");
            }

            foreach (string tileSpaceCall in new[]
                     {
                         "LineCurvatureSubdivision.Subdivide(path, ups, maxRefineAngleRad)",
                         "LineAnchorPlacement.Compute(densePath, spacingTileUnits, placement)",
                         "LineAnchorPlacement.Compute(path, 0.0, SymbolPlacement.LineCenter)",
                         "KeepAnchorsInsideTile(anchors, densePath, extent)",
                     })
            {
                StringAssert.Contains(tileSpaceCall, flat,
                    $"'{tileSpaceCall}' must be handed the TILE-SPACE path (THE NAMED FENCE: " +
                    "TileGeometryBuffers.Vertices is tile-local double2 in [0, Extent], Y-down, never " +
                    "geodetic and never projected). Feeding it pathRender/textPathRender/iconPathRender/ups " +
                    "would compile and silently corrupt the tile-scale epsilons downstream.");
            }

            // …and the projected/geodetic names never appear INSIDE those three call forms.
            foreach (Match call in Regex.Matches(
                         flat, @"(?:LineCurvatureSubdivision\.Subdivide|LineAnchorPlacement\.Compute|KeepAnchorsInsideTile)\([^)]*\)"))
            {
                foreach (string projected in new[] { "pathRender", "textPathRender", "iconPathRender", "ProjectPath(" })
                {
                    StringAssert.DoesNotContain(projected, call.Value,
                        $"a projected/render-space identifier ('{projected}') appears inside the tile-space " +
                        $"call '{call.Value}' — THE NAMED FENCE. (Grep, not a parser: it inspects the " +
                        "argument text between the call's parentheses.)");
                }
            }
        }

        // ── Shared helpers ──────────────────────────────────────────────────────────────────────────

        private static string ExtractorSource()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolFeatureExtractor.cs");
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

        /// <summary>Strips block comments and then everything from <c>//</c> (which covers <c>///</c>) to end
        /// of line, so a symbol merely NAMED in prose is not counted as a reference. Deliberately narrow — a
        /// grep guard, not a C# parser (a <c>"http://…"</c> literal would eat the rest of its line).</summary>
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

        private static string NormaliseWhitespace(string text) => Regex.Replace(text, @"\s+", " ");
    }
}
