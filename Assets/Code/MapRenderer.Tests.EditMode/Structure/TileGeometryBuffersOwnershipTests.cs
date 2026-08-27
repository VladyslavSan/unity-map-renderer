// Unity EditMode only — reads source files under Application.dataPath. NOT registered in core-tests.csproj.

using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// IR stage B1: the single-owner disposal tooth for the ring stage of
    /// <c>FillMeshPipeline.Schedule</c> — nothing may free those buffers behind
    /// <c>TileGeometryBuffers</c>'s back, and every exit path must free them.
    ///
    /// <para><b>B2 re-point.</b> The ring stage is now <b>minted by a materializer and owned by
    /// <c>Schedule</c></b>: <c>MvtGeometryMaterializer.Materialize</c> allocates it and transfers it.
    /// The mint is therefore pinned at <i>both</i> ends — zero <c>Allocate</c> call sites inside
    /// <c>Schedule</c> (an <c>Allocate</c> reappearing there means the seam was bypassed) and exactly one
    /// inside <c>Materialize</c> — so the total across the two files is still exactly one array-backed
    /// mint.</para>
    ///
    /// <para><b>B7 re-point — the direction of ownership flipped.</b> <c>Schedule</c>'s input is now
    /// <b>BORROWED</b>: the store that minted it owns it, and several fill layers run against the same one.
    /// What <c>Schedule</c> owns is the buffer <c>DeriveVisitedRings</c> builds for it. So the claims split
    /// across two bodies — <c>Schedule</c> must free its DERIVED buffer on every exit and must contain
    /// <b>zero</b> disposes of <c>input.Geometry</c>; <c>DeriveVisitedRings</c> must adopt exactly once and
    /// free nothing it hands over. A re-introduced <c>input.Geometry.Dispose()</c> is the single most likely
    /// B7 regression and the one this file exists to catch by reading.</para>
    ///
    /// <para><b>Why structural rather than behavioural.</b> The two disposal directions are not symmetrically
    /// observable — and <b>neither</b> is caught behaviourally, which B1 measured rather than assumed.
    /// Disposing an <c>AsArray()</c> view is a <b>silent no-op</b> under Collections 6.5.0, not a throw: with
    /// the backing-mode discriminator inverted, the lists leaked, nothing threw, and the whole behavioural
    /// clip corpus stayed green — only <c>TileGeometryBuffersTests</c>'
    /// <c>AdoptDerivedLists_ThenDispose_FreesTheBackingLists_AndNeverTheViews</c> failed. A <i>leak</i> — a missing
    /// <c>Dispose()</c> on the <c>polyCount == 0</c> early exit — is <c>Allocator.Persistent</c> memory that no
    /// EditMode assertion can see; Unity surfaces it only as a non-deterministic leak message at domain
    /// reload. This is the only deterministic instrument for the leak direction, and it is RED-verified by
    /// deleting that early exit's <c>Dispose()</c>.</para>
    ///
    /// <para>Assertions are over <b>dispose call forms</b>, not bare identifiers: <c>outVerts</c> and friends
    /// legitimately survive as locals inside the derive, handed to <c>AdoptDerivedLists</c>. A bare-identifier
    /// assertion would red on correct code.</para>
    /// </summary>
    [TestFixture]
    public class TileGeometryBuffersOwnershipTests
    {
        // Anchors the method DEFINITION (return-type-prefixed), which is unique in the file.
        private const string ScheduleSignatureAnchor    = "TileMeshBuffers Schedule(LayerInput";
        private const string DeriveSignatureAnchor      = "TileGeometryBuffers DeriveVisitedRings(LayerInput";
        private const string MaterializeSignatureAnchor = "TileGeometryBuffers Materialize(";
        private const string DecodeLayerSignatureAnchor =
            "MvtLayer DecodeLayer(TileId id, ProtobufReader r)";
        private const string FlattenFeatureColumnSignatureAnchor = "void FlattenFeatureColumn(";

        private const string BufferDisposeCallForm = "geometry.Dispose()";
        private const string AllocateCallForm      = "TileGeometryBuffers.Allocate(";
        private const string AdoptCallForm         = "TileGeometryBuffers.AdoptDerivedLists(";
        private const string BorrowedDisposeCallForm = "input.Geometry.Dispose()";
        private const string LegacyHelperName      = "DisposeRingStage";

        /// <summary>The dispose call forms of every buffer B1 promoted onto <c>TileGeometryBuffers</c>. Each
        /// one appearing inside <c>Schedule</c> would mean the ring stage is freed behind the struct's
        /// back.</summary>
        private static readonly string[] ForbiddenDisposeCallForms =
        {
            "tileVerts.Dispose()",
            "ringOffsets.Dispose()",
            "ringFeatIdx.Dispose()",
            // B7: the derive stage's three length-authoritative lists. They are HANDED to
            // AdoptDerivedLists, which takes ownership — disposing one here would free it twice.
            "outVerts.Dispose()",
            "outOffsets.Dispose()",
            "outFeatIdx.Dispose()",
        };

        [Test]
        public void SchedulesRingStageIsFreedOnlyThroughTheBuffer()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Jobs", "FillMeshPipeline.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            string body = StripLineComments(ExtractMethodBody(source, ScheduleSignatureAnchor, path));
            Assert.Greater(body.Length, 0, "precondition: extracted a non-empty Schedule body");

            // B2: the array-backed mint moved OUT of Schedule and behind the materializer seam. Schedule
            // only ever receives a buffer; since B7 it does not even mint the derived one itself.
            Assert.AreEqual(0, CountOccurrences(body, AllocateCallForm),
                $"Schedule must no longer mint the array-backed ring stage — a '{AllocateCallForm}' here " +
                "means the ITileGeometryMaterializer seam was bypassed.");
            Assert.AreEqual(0, CountOccurrences(body, AdoptCallForm),
                $"the list-backed mint belongs to DeriveVisitedRings, not to Schedule — an '{AdoptCallForm}' " +
                "here means the derive was inlined and the two ownership stories merged back together.");

            // B7 — THE claim of this stage: Schedule's input is BORROWED. Zero disposes of it, on any path.
            // Re-adding one is a use-after-free for every subsequent fill layer sharing the store's buffer,
            // and under Collections 6.5.0 it does not reliably throw.
            Assert.AreEqual(0, CountOccurrences(body, BorrowedDisposeCallForm),
                $"Schedule must contain ZERO '{BorrowedDisposeCallForm}' call sites — the shared buffer is " +
                "owned by the TileGeometryStore that minted it and BORROWED here. Several fill layers run " +
                "against the same buffer in one worker pass; disposing it frees the others' geometry.");

            // Three frees, one per place the DERIVED buffer stops being needed: the EnsureCapacity throw path
            // (B3), the polyCount == 0 early exit, and the normal post-earcut path. Reconciled against the
            // landed code — if a fourth appears, either an exit path was added or something is freed twice.
            // (Pre-B7 there were four: the clip handover disposed the pre-clip buffer, which is now the
            // borrowed one and must never be freed here.)
            Assert.AreEqual(3, CountOccurrences(body, BufferDisposeCallForm),
                $"Schedule must free its DERIVED buffer through '{BufferDisposeCallForm}' exactly three " +
                "times — the EnsureCapacity throw path and both exit paths. A missing one is an " +
                "Allocator.Persistent leak no behavioural test can observe.");

            // B3: one of the three is the THROW path's, and it must stay on the throw path — a Dispose that
            // drifted onto the success path would hand Stage 3 a freed buffer. Shape, not count: the count
            // above cannot tell the two apart. (Same idiom as the Materialize assertion below.)
            StringAssert.Contains(
                "catch { geometry.Dispose(); polyOuterIdx.Dispose(); polyHoleStart.Dispose(); " +
                "polyHoleCount.Dispose(); holeRingIdxs.Dispose(); throw; }",
                NormaliseWhitespace(body),
                "Schedule's EnsureCapacity backstop must free the buffer AND the four Stage-2 arrays on the " +
                "throw path — the buffer is already live when the backstop runs, so an unguarded throw " +
                "strands all five.");

            // The hand-rolled discriminator helper B1 replaced is gone, from the whole file.
            Assert.AreEqual(0, CountOccurrences(StripLineComments(source), LegacyHelperName),
                $"'{LegacyHelperName}' must no longer exist — TileGeometryBuffers owns the backing-mode " +
                "discriminator now.");

            foreach (string callForm in ForbiddenDisposeCallForms)
            {
                Assert.AreEqual(0, CountOccurrences(body, callForm),
                    $"Schedule must contain ZERO '{callForm}' call sites — the ring-stage buffers are owned " +
                    "by TileGeometryBuffers and freed only through it.");
            }

            // ── The other end of the move: the materializer mints once and, on the success path, frees
            // nothing. Its single geometry.Dispose() belongs to the EnsureCapacity throw path, where the
            // buffer never escapes; a Dispose() that drifted onto the success path would return a freed
            // buffer to Schedule, so the count alone must not be allowed to carry this claim.
            string materializeBody = StripLineComments(
                ExtractMethodBody(MaterializerSource(), MaterializeSignatureAnchor, MaterializerPath()));
            Assert.Greater(materializeBody.Length, 0, "precondition: extracted a non-empty Materialize body");

            Assert.AreEqual(1, CountOccurrences(materializeBody, AllocateCallForm),
                $"Materialize must mint the array-backed ring stage exactly once via '{AllocateCallForm}'.");
            Assert.AreEqual(0, CountOccurrences(materializeBody, AdoptCallForm),
                $"'{AdoptCallForm}' is the consumer-side derive and belongs to the fill pipeline, not to a producer.");
            Assert.AreEqual(1, CountOccurrences(materializeBody, BufferDisposeCallForm),
                $"Materialize must free the buffer it minted exactly once — on the throw path only. Ownership " +
                "of a returned buffer TRANSFERS to the caller.");
            StringAssert.Contains("catch { geometry.Dispose(); throw; }", NormaliseWhitespace(materializeBody),
                "the materializer's single geometry.Dispose() must sit in the EnsureCapacity catch, where the " +
                "buffer never escapes — on the success path it would hand Schedule a freed buffer.");
        }

        /// <summary>B3 → B7: the per-feature kind column reaches the derived buffer, from the SOURCE.
        ///
        /// <para><b>Why structural — measured, not assumed.</b> B3's RED sweep injected the real defect (the
        /// handover passing <c>default</c> instead of the column) and the <b>entire gate stayed green,
        /// 2250/2250</b>: the column was write-only after the clip, because <c>RingAssemblyJob</c> was
        /// deliberately not gated on it. B7 changes that — the job now HAS a kind gate, so a dropped column
        /// makes every ring read <c>Unknown</c> and fill renders nothing, which every fill snapshot sees. The
        /// call-site claim is kept anyway, because "some other test happens to cover it" is exactly the
        /// reasoning that lost B3 a stage.</para>
        ///
        /// <para>The mechanism moved from <b>transfer</b> to <b>copy</b>: the source is BORROWED now, so it
        /// cannot be released from. <c>TileGeometryBuffersTests</c>' companion tooth pins that mechanism
        /// (the copy is a distinct allocation and the source survives); this pins the <i>call site</i> — that
        /// what the derive hands over is the source's own column and not <c>default</c>.</para></summary>
        [Test]
        public void DeriveVisitedRingsCopiesTheSourcesKindColumnIntoTheDerivedBuffer()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Jobs", "FillMeshPipeline.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");

            string body = StripLineComments(
                ExtractMethodBody(File.ReadAllText(path), DeriveSignatureAnchor, path));

            // Non-vacuity: an anchor miss or a moved derive would make every claim below trivially true.
            Assert.Greater(body.Length, 0, "precondition: extracted a non-empty DeriveVisitedRings body");
            StringAssert.Contains("RingClipJob", body,
                "precondition: the extracted body really is the one that runs the clip branch");
            StringAssert.Contains("RingSelectJob", body,
                "precondition: …and the clip-disabled branch. Collapsing the two into one clip call with a " +
                "full-extent window is NOT equivalent: the clip's emit dedups repeated vertices and closes " +
                "the ring, which moves a boundary-touching ring's geometry.");

            Assert.AreEqual(1, CountOccurrences(body, AdoptCallForm),
                $"the derive must mint its list-backed buffer exactly once via '{AdoptCallForm}'.");

            StringAssert.Contains(
                "return TileGeometryBuffers.AdoptDerivedLists( source.Tile, source.Extent, " +
                "source.FeatureGeometryType, outVerts, outOffsets, outFeatIdx);",
                NormaliseWhitespace(body),
                "the derived buffer's kind column must come from the SOURCE's column — passing `default` " +
                "would leave every ring reading Unknown, and its tile/extent must come off the source buffer " +
                "rather than from a second copy a stage could substitute a constant for.");

            Assert.AreEqual(0, CountOccurrences(body, BorrowedDisposeCallForm),
                $"the derive BORROWS its input — zero '{BorrowedDisposeCallForm}' call sites.");
            Assert.AreEqual(0, CountOccurrences(body, "source.Dispose()"),
                "…and zero 'source.Dispose()' call sites, for the same reason under the local's own name.");

            foreach (string callForm in ForbiddenDisposeCallForms)
            {
                Assert.AreEqual(0, CountOccurrences(body, callForm),
                    $"DeriveVisitedRings must contain ZERO '{callForm}' call sites — those three lists are " +
                    "HANDED to AdoptDerivedLists, which takes ownership of them.");
            }
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
            // valuesScratch, see AdoptValues below) as the seventh buffer this same finally guards.
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
            Application.dataPath, "Code", "MapRenderer.Jobs", "MvtGeometryMaterializer.cs");

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
