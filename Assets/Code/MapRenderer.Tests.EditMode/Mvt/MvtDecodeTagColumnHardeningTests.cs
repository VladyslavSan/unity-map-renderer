// EditMode only (decodes a real MVT fixture through MvtDecoder, which lives in MapRenderer.Jobs and is not
// compiled by the Tools/core-tests seam).

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Mvt
{
    /// <summary>
    /// N2 (whole-epic-review hardening, folded into the VM-wiring stage) — regression tooth for
    /// <see cref="MvtDecoder.ValidateTagSliceColumnsMatchFeatureCount"/>: the tag-slice columns' feature-
    /// count lockstep check must both (a) actually throw on a mismatch and (b) run BEFORE
    /// <c>MvtLayer.AdoptGeometry</c> inside <c>MvtDecoder.DecodeLayer</c> — the ordering that keeps a
    /// mismatch from leaking the just-adopted geometry buffer (the layer is not yet reachable from
    /// <c>tile.Layers</c> at that point, so <c>Decode</c>'s catch cannot free it; see the call site's
    /// comment in <c>MvtDecoder.cs</c>).
    ///
    /// <para>A REAL mismatch is unreachable through <see cref="MvtDecoder.Decode"/> — <c>FlattenFeatureColumn</c>
    /// builds both tag-slice columns at the feature count by construction — so (a) is pinned directly
    /// against the check (broadened to <c>internal</c> for exactly this), and (b) is pinned by scanning
    /// <c>MvtDecoder.cs</c>'s own source for the two calls' relative order, the same source-scan technique
    /// <c>Structure/NeutralGeometryPathTests</c> already uses for a fence a reflection-only assertion
    /// cannot express.</para>
    /// </summary>
    [TestFixture]
    public class MvtDecodeTagColumnHardeningTests
    {
        // ── (a) the check itself throws on mismatch, not on match ──────────────────────────────

        [Test]
        public void ValidateTagSliceColumnsMatchFeatureCount_ThrowsOnMismatch()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MvtDecoder.ValidateTagSliceColumnsMatchFeatureCount(
                    tagOffsetsLength: 3, tagLengthsLength: 3, featCount: 4, layerName: "roads"));
            StringAssert.Contains("roads", ex.Message);
        }

        [Test]
        public void ValidateTagSliceColumnsMatchFeatureCount_ThrowsWhenOnlyLengthsColumnMismatches()
        {
            Assert.Throws<InvalidOperationException>(() =>
                MvtDecoder.ValidateTagSliceColumnsMatchFeatureCount(
                    tagOffsetsLength: 4, tagLengthsLength: 3, featCount: 4, layerName: "roads"));
        }

        [Test]
        public void ValidateTagSliceColumnsMatchFeatureCount_DoesNotThrow_WhenBothColumnsMatch()
        {
            Assert.DoesNotThrow(() =>
                MvtDecoder.ValidateTagSliceColumnsMatchFeatureCount(
                    tagOffsetsLength: 4, tagLengthsLength: 4, featCount: 4, layerName: "roads"));
        }

        // ── (b) the check runs BEFORE AdoptGeometry, in DecodeLayer's own source ───────────────

        /// <summary>Non-vacuity + the ordering claim in one: both calls must actually be found (proving the
        /// scan is looking at the real method), and the validate call's position must precede the adopt
        /// call's. RED-verify: temporarily move the <c>ValidateTagSliceColumnsMatchFeatureCount(...)</c>
        /// statement to after <c>layer.AdoptGeometry(...)</c> in <c>MvtDecoder.cs</c> — this test goes red;
        /// restore and it is green again.</summary>
        [Test]
        public void DecodeLayer_ValidatesTagSliceColumns_BeforeAdoptingGeometry()
        {
            string decoderSource = ReadMvtDecoderSource();

            // The exact declaration text, not just "DecodeLayer(" — that substring also matches the call
            // site inside Decode(), which appears EARLIER in the file than the method it calls.
            string decodeLayerBody = ExtractMethodBody(decoderSource, "static MvtLayer DecodeLayer(");
            Assert.IsNotEmpty(decodeLayerBody, "precondition: DecodeLayer's body must be found in the source scan");

            int validateIndex = decodeLayerBody.IndexOf("ValidateTagSliceColumnsMatchFeatureCount(", StringComparison.Ordinal);
            int adoptGeometryIndex = decodeLayerBody.IndexOf(".AdoptGeometry(", StringComparison.Ordinal);

            Assert.That(validateIndex, Is.GreaterThanOrEqualTo(0),
                "precondition: DecodeLayer must call ValidateTagSliceColumnsMatchFeatureCount — if this " +
                "scan finds nothing, the ordering assertion below asserts about nothing");
            Assert.That(adoptGeometryIndex, Is.GreaterThanOrEqualTo(0),
                "precondition: DecodeLayer must call AdoptGeometry — same non-vacuity concern");

            Assert.That(validateIndex, Is.LessThan(adoptGeometryIndex),
                "the tag-slice column validation must run BEFORE AdoptGeometry: a throw after AdoptGeometry " +
                "would leak the just-adopted geometry buffer, because the layer is not yet reachable from " +
                "tile.Layers and Decode's catch can only free what IS reachable");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        private static string ReadMvtDecoderSource()
        {
            string path = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs", "Mvt", "MvtDecoder.cs");
            Assert.IsTrue(File.Exists(path), $"expected {path} to exist");
            return File.ReadAllText(path);
        }

        /// <summary>Extracts the brace-matched body of the first method whose declaration contains
        /// <paramref name="declarationText"/> — a plain brace counter from the method's own opening brace,
        /// good enough for a single well-formed C# source file with no string/char literals containing
        /// unbalanced braces (true of this file). The caller passes enough of the declaration (return type
        /// + name + open paren) to distinguish it from a mere call site earlier in the file.</summary>
        private static string ExtractMethodBody(string source, string declarationText)
        {
            int nameIndex = source.IndexOf(declarationText, StringComparison.Ordinal);
            if (nameIndex < 0) return string.Empty;

            int openBrace = source.IndexOf('{', nameIndex);
            if (openBrace < 0) return string.Empty;

            int depth = 0;
            for (int i = openBrace; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(openBrace, i - openBrace + 1);
                }
            }
            return string.Empty;
        }
    }
}
