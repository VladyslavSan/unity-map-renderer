// Unity EditMode only — reads source files under Application.dataPath. NOT registered in core-tests.csproj.

using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// Source-text structural disposal-discipline pins for the tile-geometry producer seam.
    ///
    /// <para><b>job-scheduling-design.md §8 stage 4 Group B (retired):</b> this file used to also pin
    /// <c>FillMeshPipeline.Schedule</c>'s and its private <c>DeriveVisitedRings</c>' ring-stage disposal
    /// discipline (IR stages B1/B2/B7) — both methods are deleted with the synchronous fill pipeline. The
    /// graph's disposal discipline is a different mechanism entirely (dispose NODES scheduled onto the job
    /// graph, not a `.Dispose()` call form in one function body) and is pinned elsewhere
    /// (<c>FillGraphOutput.DebugBuffersAllocated</c>/<c>DebugBufferDisposeNodes</c> pairing, exercised by the
    /// job-graph instrument tests) — not a like-for-like source-text re-pin here.</para>
    ///
    /// <para>What remains: the MVT decode/materialize seam's ownership discipline, untouched by Group B —
    /// <c>MvtGeometryMaterializer.Materialize</c> mints the ring stage exactly once and frees it only on its
    /// own throw path (ownership transfers to the caller on success); <c>MvtDecoder.DecodeLayer</c> flattens
    /// and frees its own MVT command/tag buffers exactly once on every exit path, with the double-free guard
    /// on each adopted buffer (null-on-transfer) pinned by shape, not just count; and
    /// <c>FlattenFeatureColumn</c> publishes each <c>ref</c> output before the first operation that can
    /// throw. See each test's own doc for why structural rather than behavioural: a leak or a use-after-free
    /// in <c>Allocator.Persistent</c> worker-thread memory is invisible to the behavioural corpus, and
    /// several of these were measured, not assumed — the whole gate stayed green with the real defect
    /// injected.</para>
    /// </summary>
    [TestFixture]
    public class TileGeometryBuffersOwnershipTests
    {
        // Anchors the method DEFINITION (return-type-prefixed), which is unique in the file.
        private const string MaterializeSignatureAnchor = "TileGeometryBuffers Materialize(";
        private const string DecodeLayerSignatureAnchor =
            "MvtLayer DecodeLayer(TileId id, ProtobufReader r)";
        private const string FlattenFeatureColumnSignatureAnchor = "void FlattenFeatureColumn(";

        private const string BufferDisposeCallForm = "geometry.Dispose()";
        private const string AllocateCallForm      = "TileGeometryBuffers.Allocate(";
        private const string AdoptCallForm         = "TileGeometryBuffers.AdoptDerivedLists(";

        [Test]
        public void MaterializeMintsTheRingStageOnceAndFreesItOnlyOnItsOwnThrowPath()
        {
            // The other end of the mint: the materializer mints once and, on the success path, frees
            // nothing. Its single geometry.Dispose() belongs to the EnsureCapacity throw path, where the
            // buffer never escapes; a Dispose() that drifted onto the success path would return a freed
            // buffer to the caller, so the count alone must not be allowed to carry this claim.
            string materializeBody = StripLineComments(
                ExtractMethodBody(MaterializerSource(), MaterializeSignatureAnchor, MaterializerPath()));
            Assert.Greater(materializeBody.Length, 0, "precondition: extracted a non-empty Materialize body");

            Assert.AreEqual(1, CountOccurrences(materializeBody, AllocateCallForm),
                $"Materialize must mint the array-backed ring stage exactly once via '{AllocateCallForm}'.");
            Assert.AreEqual(0, CountOccurrences(materializeBody, AdoptCallForm),
                $"'{AdoptCallForm}' is a consumer-side derive and belongs to a fill mesher, not to a producer.");
            Assert.AreEqual(1, CountOccurrences(materializeBody, BufferDisposeCallForm),
                $"Materialize must free the buffer it minted exactly once — on the throw path only. Ownership " +
                "of a returned buffer TRANSFERS to the caller.");
            StringAssert.Contains("catch { geometry.Dispose(); throw; }", NormaliseWhitespace(materializeBody),
                "the materializer's single geometry.Dispose() must sit in the EnsureCapacity catch, where the " +
                "buffer never escapes — on the success path it would hand the caller a freed buffer.");
        }

        /// <summary>B2 T5a → 2a re-point: <c>commands</c>/<c>featOffsets</c>/<c>featLengths</c> flipped from
        /// scratch <c>Materialize</c> minted and owned to a BORROWED constructor input — since 2a,
        /// <c>MvtDecoder.DecodeLayer</c> flattens them directly off the wire and owns disposal (see
        /// <see cref="DecodeLayerFreesTheMvtCommandBuffersItBuilds_ExactlyOnceOnEveryExitPath"/>, the other
        /// end of this move). A dispose reappearing here would fault the second of two <c>Materialize()</c>
        /// calls on one instance (<c>Materialize_TransfersOwnership_AndMintsAFreshBufferPerCall</c> does
        /// exactly that) and double-free once the caller also disposes. <c>ringCountArr</c>/<c>vertCountArr</c>
        /// are still local scratch <c>Materialize</c> mints and owns itself, unaffected by the move.</summary>
        [Test]
        public void MaterializeFreesOnlyItsOwnOutputScratch_AndNeverTheBorrowedMvtCommandBuffers()
        {
            string body = StripLineComments(
                ExtractMethodBody(MaterializerSource(), MaterializeSignatureAnchor, MaterializerPath()));

            // Non-vacuity: a renamed method or a moved decode would make every count below trivially 0.
            Assert.Greater(body.Length, 0, "precondition: extracted a non-empty Materialize body");
            StringAssert.Contains("MvtDecodeJob", body,
                "precondition: the extracted body really is the one that runs the MVT decode");

            foreach (string callForm in new[] { "ringCountArr.Dispose()", "vertCountArr.Dispose()" })
            {
                Assert.AreEqual(1, CountOccurrences(body, callForm),
                    $"Materialize must free its own output scratch through '{callForm}' exactly once.");
            }

            foreach (string callForm in new[]
                     { "commands.Dispose()", "featOffsets.Dispose()", "featLengths.Dispose()" })
            {
                Assert.AreEqual(0, CountOccurrences(body, callForm),
                    $"Materialize must NOT call '{callForm}' — since 2a these three are BORROWED constructor " +
                    "inputs the caller owns and frees, not scratch Materialize mints itself.");
            }
        }

        /// <summary>2a: the other end of the ownership move above. <c>MvtDecoder.DecodeLayer</c> flattens
        /// every feature's geometry command words directly off the wire into three
        /// <c>Allocator.Persistent</c> native buffers and, since they are no longer handed off for the
        /// materializer to free, must free each itself — exactly once, on EVERY exit path, including a
        /// thrown <c>ArgumentException</c> from a mismatched kind column AND a thrown
        /// <c>InvalidOperationException</c> from a malformed varint in either count/fill loop (both re-read
        /// raw wire bytes, so both are reachable on a malformed tile). A missing free here is a leak no
        /// EditMode assertion can observe (worker-thread native memory) — this is why an allocation made
        /// outside the try leaks if a LATER loop throws, which is exactly the regression this pins.
        ///
        /// <para>Native-tag-storage stage: the SAME try/finally now also owns three more buffers —
        /// <c>tagWords</c>/<c>tagOffsets</c>/<c>tagLengths</c>, the tag-word twin of the geometry flatten,
        /// built and disposed by the exact same technique. <c>tagWords</c> is different from the other five:
        /// it is not scratch, it is TRANSFERRED to <c>MvtLayer.FeatureTagWords</c> via
        /// <c>AdoptFeatureTagWords</c> and the local nulled immediately after (the "transfer nulls the
        /// source" double-free guard — <c>MEMORY.md</c>, Model B), so its <c>finally</c>-block
        /// <c>tagWords.Dispose()</c> is a no-op on the success path and only actually frees the buffer if a
        /// LATER throw (there is none scheduled) landed between the adopt and return. This is exactly the
        /// "no IsCreated guard, Dispose() early-returns on default" idiom the geometry buffers already
        /// use.</para>
        ///
        /// <para><b>Post-Stage-1 readability refactor: the two count-then-fill flattens moved into one
        /// shared <c>FlattenFeatureColumn</c> helper, called twice.</b> The allocations this tooth used to
        /// find directly in <c>DecodeLayer</c>'s body (<c>new NativeArray&lt;int&gt;(featCount</c>) now live
        /// in the HELPER, not the caller — so "an allocation made before <c>DecodeLayer</c>'s <c>try</c>
        /// leaks" is no longer provable by finding <c>try {</c> before an allocation token in
        /// <c>DecodeLayer</c>'s own body; that token isn't there anymore. The guard is re-expressed across
        /// BOTH ends of the call, and is NOT weaker — each half closes a distinct way the leak-safety
        /// contract could break:
        /// <list type="bullet">
        /// <item>the CALL-SITE half (still in <c>DecodeLayer</c>'s body): both
        /// <c>FlattenFeatureColumn(…)</c> calls pass all three outputs with the <c>ref</c> keyword (a
        /// <c>ref</c>→<c>out</c> regression changes this call-site text too — C# requires the modifier to
        /// match at both ends — so this still catches it) AND both calls sit BETWEEN <c>try {</c> and
        /// <c>finally {</c>, closing the "a call drifted outside the try" hole a bare "after <c>try {</c>"
        /// check would miss;</item>
        /// <item>the HELPER-BODY half (new — <c>FlattenFeatureColumnPublishesEachRefOutputBeforeItCanThrow</c>
        /// below): even with <c>ref</c> at both ends and both calls inside the try, a helper that stages its
        /// allocations into LOCALS and assigns the <c>ref</c> params only at the very end would still leak on
        /// a throw between the count loop and that final assignment — invisible to every assertion above,
        /// since none of them reads INSIDE the helper. Pinned by reading the helper's own body: each
        /// <c>ref</c> output is assigned (published) exactly once, and the FIRST one is assigned before the
        /// first <c>ReadVarint</c> call that could throw.</item>
        /// </list>
        /// </para></summary>
        [Test]
        public void DecodeLayerFreesTheMvtCommandBuffersItBuilds_ExactlyOnceOnEveryExitPath()
        {
            string path = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs", "Mvt", "MvtDecoder.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);
            string body = StripLineComments(
                ExtractMethodBody(source, DecodeLayerSignatureAnchor, path));

            // Non-vacuity: a renamed method or a moved construct would make every count below trivially 0.
            Assert.Greater(body.Length, 0, "precondition: extracted a non-empty DecodeLayer body");
            StringAssert.Contains("MvtGeometryMaterializer", body,
                "precondition: the extracted body really does construct the materializer");
            StringAssert.Contains("AdoptFeatureTagWords", body,
                "precondition: the extracted body really does adopt the flattened tag words");

            foreach (string callForm in new[]
                     {
                         "tagWords.Dispose()", "tagOffsets.Dispose()", "tagLengths.Dispose()",
                         "commands.Dispose()", "featOffsets.Dispose()", "featLengths.Dispose()",
                         "values.Dispose()",
                     })
            {
                Assert.AreEqual(1, CountOccurrences(body, callForm),
                    $"DecodeLayer must free the native buffer it built through '{callForm}' exactly once.");
            }

            string normalised = NormaliseWhitespace(body);

            // Shape, not just count: all seven frees must sit in ONE finally wrapping both flattens plus the
            // materializer construct+Materialize call. No IsCreated guard — a throw from the FIRST
            // allocation (or from a count loop, which runs between two allocations) leaves the rest
            // un-allocated, and NativeArray.Dispose() early-returns on a default value
            // (docs/lessons-learned.md), so the finally frees whatever was allocated and no-ops on the rest.
            // The value-table stage added `values` (the native table materialized from the transient
            // sortValues, see AdoptValues below) as the seventh buffer this same finally guards.
            StringAssert.Contains(
                "finally { tagWords.Dispose(); " +
                "tagOffsets.Dispose(); " +
                "tagLengths.Dispose(); " +
                "commands.Dispose(); " +
                "featOffsets.Dispose(); " +
                "featLengths.Dispose(); " +
                "values.Dispose(); }",
                normalised,
                "all seven frees must sit in ONE finally around both flattens and the materializer " +
                "construct+Materialize call.");

            // ── Call-site half of the leak-safety guard (see the class doc's "readability refactor" note) ──
            const string tagCall  = "FlattenFeatureColumn(r, tagStart, tagEnd, featCount, " +
                                     "ref tagOffsets, ref tagLengths, ref tagWords);";
            const string geomCall = "FlattenFeatureColumn(r, geomStart, geomEnd, featCount, " +
                                     "ref featOffsets, ref featLengths, ref commands);";
            StringAssert.Contains(tagCall, normalised,
                "the tag flatten must call FlattenFeatureColumn passing all three outputs BY REF — an " +
                "out param would only publish to the caller on normal return, stranding whatever the " +
                "helper had already allocated if a later loop inside it throws.");
            StringAssert.Contains(geomCall, normalised,
                "the geometry flatten must call FlattenFeatureColumn passing all three outputs BY REF, " +
                "for the same reason as the tag call above.");

            int tryIndex     = normalised.IndexOf("try {", StringComparison.Ordinal);
            int finallyIndex = normalised.IndexOf("finally {", StringComparison.Ordinal);
            int tagCallIndex  = normalised.IndexOf(tagCall, StringComparison.Ordinal);
            int geomCallIndex = normalised.IndexOf(geomCall, StringComparison.Ordinal);
            Assert.GreaterOrEqual(tryIndex, 0, "precondition: expected DecodeLayer to open a try block");
            Assert.GreaterOrEqual(finallyIndex, 0, "precondition: expected DecodeLayer to have a finally block");
            // Bracketing, not just "after try {": a call that drifted PAST the finally would still be
            // "after try {" and would pass a weaker check while every buffer it allocates leaks — the six
            // Dispose()-count assertions above cannot catch that either, since they read the WHOLE body, not
            // where in it the calls sit relative to the finally.
            Assert.That(tagCallIndex, Is.InRange(tryIndex, finallyIndex),
                "the tag FlattenFeatureColumn call must sit BETWEEN try { and finally { — outside that " +
                "window its allocations are unreachable to the finally that frees them.");
            Assert.That(geomCallIndex, Is.InRange(tryIndex, finallyIndex),
                "the geometry FlattenFeatureColumn call must sit BETWEEN try { and finally {, for the same " +
                "reason as the tag call above.");

            // Plan §8 R1's named regression: dropping the null-on-transfer would leave `tagWords` non-default
            // after the adopt, so the finally's `tagWords.Dispose()` would free the buffer the layer JUST
            // adopted — a use-after-free for every store/resolver in the layer. Pinned by shape, since the
            // count/finally assertions above cannot distinguish "nulled after adopt" from "adopted, not nulled".
            StringAssert.Contains("AdoptFeatureTagWords(tagWords); tagWords = default;", normalised,
                "the adopted local must be nulled immediately after AdoptFeatureTagWords — the double-free " +
                "guard that keeps the finally's unconditional tagWords.Dispose() from freeing the buffer the " +
                "layer just took ownership of.");

            // Value-table stage: the same double-free guard for the values buffer AdoptValues transfers.
            StringAssert.Contains("AdoptValues(values, valueStrings); values = default;", normalised,
                "the adopted `values` local must be nulled immediately after AdoptValues — the same " +
                "double-free guard as tagWords, keeping the finally's unconditional values.Dispose() from " +
                "freeing the buffer the layer just took ownership of.");

            // Native-column decoupling: the per-feature (offset,count) columns are now ADOPTED by the layer
            // (the resolver borrows them), not transient — so they need the same null-on-transfer guard, or
            // the finally's unconditional tagOffsets/tagLengths.Dispose() would free the columns the layer
            // just took ownership of (UAF for every store reading a slice by ordinal).
            StringAssert.Contains(
                "AdoptFeatureTagColumns(tagOffsets, tagLengths); tagOffsets = default; tagLengths = default;",
                normalised,
                "the adopted `tagOffsets`/`tagLengths` locals must be nulled immediately after " +
                "AdoptFeatureTagColumns — the same double-free guard as tagWords/values.");
        }

        /// <summary>
        /// Helper-body half of the leak-safety guard above: pins that <c>FlattenFeatureColumn</c> itself
        /// cannot reintroduce the leak the <c>ref</c>-vs-<c>out</c> call-site check cannot see on its own —
        /// a helper that stages its three outputs into LOCALS and only assigns its <c>ref</c> params at the
        /// very end would still strand whatever it had allocated if a throw (a malformed tile's
        /// <c>ReadVarint</c>) landed before that final assignment, no matter how the call site looks.
        /// </summary>
        [Test]
        public void FlattenFeatureColumnPublishesEachRefOutputBeforeItCanThrow()
        {
            string path = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs", "Mvt", "MvtDecoder.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);
            string body = StripLineComments(
                ExtractMethodBody(source, FlattenFeatureColumnSignatureAnchor, path));

            // Non-vacuity: a renamed/moved helper would make every claim below trivially true (or the
            // anchor lookup itself would already have failed inside ExtractMethodBody).
            Assert.Greater(body.Length, 0, "precondition: extracted a non-empty FlattenFeatureColumn body");
            StringAssert.Contains("ReadVarint", body,
                "precondition: the extracted body really does read varints — without this every index-order " +
                "assertion below would compare against an absent token and could pass vacuously.");

            // Each ref output must be assigned (published to the caller) exactly once — not staged into a
            // local first and copied to the ref param only at the end, which would defeat the whole reason
            // the call site passes by ref instead of by out.
            foreach (string publish in new[]
                     { "offsets = new NativeArray<int>(", "lengths = new NativeArray<int>(", "words = new NativeArray<uint>(" })
            {
                Assert.AreEqual(1, CountOccurrences(body, publish),
                    $"FlattenFeatureColumn must publish its ref output through '{publish}' exactly once — " +
                    "directly, not via a local copied back later.");
            }

            // The FIRST publish (offsets) must happen before the FIRST varint read that can throw — so a
            // throw from the very first count loop still leaves the caller holding a valid `offsets`
            // reference via ref, exactly the property the call-site ref check depends on existing.
            int firstPublishIndex = body.IndexOf("offsets = new NativeArray<int>(", StringComparison.Ordinal);
            int firstReadVarintIndex = body.IndexOf("ReadVarint(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(firstPublishIndex, 0,
                "precondition: expected to find the 'offsets' publish statement");
            Assert.GreaterOrEqual(firstReadVarintIndex, 0,
                "precondition: expected to find at least one ReadVarint call (the throwing operation this " +
                "whole tooth exists to guard against)");
            Assert.Less(firstPublishIndex, firstReadVarintIndex,
                "the 'offsets' ref param must be published BEFORE the first ReadVarint call that can throw " +
                "— publishing after would leave the caller's local unset (still default) if that first " +
                "read faults, defeating the ref-not-out leak-safety contract from the call site.");
        }

        private static string MaterializerPath() => Path.Combine(
            Application.dataPath, "Code", "MapRenderer.Jobs", "Mvt", "MvtGeometryMaterializer.cs");

        private static string MaterializerSource()
        {
            string path = MaterializerPath();
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            return File.ReadAllText(path);
        }

        private static string ExtractMethodBody(string source, string signatureAnchor, string path)
        {
            int anchorIndex = source.IndexOf(signatureAnchor, StringComparison.Ordinal);
            Assert.GreaterOrEqual(anchorIndex, 0,
                $"expected to find the unique method-definition anchor '{signatureAnchor}' in {path}");

            int braceStart = source.IndexOf('{', anchorIndex);
            Assert.GreaterOrEqual(braceStart, 0, $"expected an opening brace after the method signature in {path}");

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
            Assert.Less(i, source.Length, $"unbalanced braces scanning the method body in {path}");

            return source.Substring(braceStart, i - braceStart + 1);
        }

        private static int CountOccurrences(string text, string callForm)
            => Regex.Matches(text, Regex.Escape(callForm)).Count;

        /// <summary>Strips everything from <c>//</c> to end of line, so a call form merely NAMED in a comment
        /// is not counted as a call site (and, for the forbidden forms, so a comment cannot red the test).
        /// Deliberately narrow — a grep guard, not a C# parser.</summary>
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

        /// <summary>Collapses every whitespace run to one space, so a call-form SHAPE (rather than a count)
        /// can be asserted without pinning the formatter's line breaks.</summary>
        private static string NormaliseWhitespace(string text)
            => Regex.Replace(text, @"\s+", " ");
    }
}
