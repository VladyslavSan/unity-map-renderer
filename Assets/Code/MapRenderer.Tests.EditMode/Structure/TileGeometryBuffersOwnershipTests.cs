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
            "MvtLayer DecodeLayer(TileId id, ProtobufReader r, MvtPropertyStorage propertyStorage)";

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
        /// every feature's geometry command words directly off the wire into these three
        /// <c>Allocator.Persistent</c> native buffers and, since they are no longer handed off for the
        /// materializer to free, must free each itself — exactly once, on EVERY exit path, including a
        /// thrown <c>ArgumentException</c> from a mismatched kind column AND a thrown
        /// <c>InvalidOperationException</c> from a malformed varint in either count/fill loop (both re-read
        /// raw wire bytes, so both are reachable on a malformed tile). A missing free here is a leak no
        /// EditMode assertion can observe (worker-thread native memory) — this is why the try must open
        /// BEFORE the first allocation, not just wrap the materializer call: an allocation made outside the
        /// try leaks if a LATER loop throws, which is exactly the regression this pins.</summary>
        [Test]
        public void DecodeLayerFreesTheMvtCommandBuffersItBuilds_ExactlyOnceOnEveryExitPath()
        {
            string path = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs", "Mvt", "MvtDecoder.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string body = StripLineComments(
                ExtractMethodBody(File.ReadAllText(path), DecodeLayerSignatureAnchor, path));

            // Non-vacuity: a renamed method or a moved construct would make every count below trivially 0.
            Assert.Greater(body.Length, 0, "precondition: extracted a non-empty DecodeLayer body");
            StringAssert.Contains("MvtGeometryMaterializer", body,
                "precondition: the extracted body really does construct the materializer");

            foreach (string callForm in new[]
                     { "commands.Dispose()", "featOffsets.Dispose()", "featLengths.Dispose()" })
            {
                Assert.AreEqual(1, CountOccurrences(body, callForm),
                    $"DecodeLayer must free the native buffer it built through '{callForm}' exactly once.");
            }

            string normalised = NormaliseWhitespace(body);

            // Shape, not just count: the three frees must sit in a finally wrapping the materializer
            // construct+Materialize call, EACH guarded by IsCreated — a throw from the FIRST allocation (or
            // from the count loop, which runs between the first two allocations) leaves the others
            // un-allocated, and disposing a default NativeArray would itself throw without the guard.
            StringAssert.Contains(
                "finally { if (commands.IsCreated) commands.Dispose(); " +
                "if (featOffsets.IsCreated) featOffsets.Dispose(); " +
                "if (featLengths.IsCreated) featLengths.Dispose(); }",
                normalised,
                "the three frees must sit in a finally around the materializer construct+Materialize call, " +
                "each guarded by IsCreated.");

            // The regression this whole tooth exists to catch: an allocation made BEFORE the try leaks if a
            // later loop throws. Both the count loop and the fill loop re-read raw wire bytes via
            // ReadVarint, which throws on a malformed tile — so both loops, and every allocation, must be
            // INSIDE the try. Pinned structurally: `try {` must appear before the first native allocation.
            int firstAllocIndex = normalised.IndexOf("new NativeArray<int>(featCount", StringComparison.Ordinal);
            int tryIndex = normalised.IndexOf("try {", StringComparison.Ordinal);
            Assert.GreaterOrEqual(firstAllocIndex, 0,
                "precondition: expected to find DecodeLayer's featOffsets allocation " +
                "('new NativeArray<int>(featCount')");
            Assert.GreaterOrEqual(tryIndex, 0, "precondition: expected DecodeLayer to open a try block");
            Assert.Less(tryIndex, firstAllocIndex,
                "the try must open BEFORE the first native allocation (featOffsets) — an allocation made " +
                "outside the try is unreachable to the finally if a later loop throws, which is a native leak " +
                "no EditMode assertion besides this structural read can see.");
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
