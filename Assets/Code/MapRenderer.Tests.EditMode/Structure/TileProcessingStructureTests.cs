// Unity EditMode only — reads source files under Application.dataPath. NOT registered in core-tests.csproj.

using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// Epic A / A1 (falsifiable acceptance tooth #1): a
    /// RED-verifiable structural pair proving <c>TileManager.KickMeshBuild</c> no longer decodes/dispatches
    /// directly — the shared decode + fan-out now lives ONLY in
    /// <see cref="MapRenderer.Unity.Rendering.Tile.Processing.TileLayerProcessorRunner"/>.
    ///
    /// Against the pre-A1 source this test FAILS on both forbidden call forms (a direct
    /// <c>MvtDecoder.Decode(</c> and a direct <c>.WriteInto(</c> inside <c>KickMeshBuild</c>) — recorded as
    /// the RED observation before the production rewire. Adding an unused interface or merely renaming the
    /// local cannot pass: the assertions are over the CALL forms actually present in the method body.
    /// </summary>
    [TestFixture]
    public class TileProcessingStructureTests
    {
        private const string DecodeCallForm = "MvtDecoder.Decode(";
        // The runner's read of the caller-owned reference. `.Value` (R2: SharedDisposable<IDecodedTile>,
        // successor to the R1-era lease's `.Tile`) rather than `.GetOrDecode(` since D1 — the wrapper holds
        // an already-decoded tile, so reading it is a property access, not a call.
        private const string DecodeReadForm = "decode.Value";
        private const string WriteIntoCallForm = ".WriteInto(";
        private const string RunnerCallForm = "TileLayerProcessorRunner.RunWorkerPass(";

        // Anchors the method DEFINITION (return-type-prefixed), not one of KickMeshBuild's several call
        // sites elsewhere in TileManager.cs (which read just "KickMeshBuild(...)" with no return type
        // before them) — this substring is unique to the signature.
        private const string KickMeshBuildSignatureAnchor = "UniTask<MeshBuildResult> KickMeshBuild(";

        [Test]
        public void TileManager_DelegatesDecodeAndLayerWritesToTheProcessorRunner()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            string body = ExtractMethodBody(source, KickMeshBuildSignatureAnchor, path);

            int decodeCalls    = CountOccurrences(body, DecodeCallForm);
            int writeIntoCalls = CountOccurrences(body, WriteIntoCallForm);
            int runnerCalls    = CountOccurrences(body, RunnerCallForm);

            Assert.AreEqual(0, decodeCalls,
                $"TileManager.KickMeshBuild must contain ZERO direct '{DecodeCallForm}' call sites — the " +
                "decode is the runner's job now (Epic A / A1: one MVT decode per mesh worker pass, owned by " +
                "TileLayerProcessorRunner).");
            Assert.AreEqual(0, writeIntoCalls,
                $"TileManager.KickMeshBuild must contain ZERO direct '{WriteIntoCallForm}' call sites — " +
                "per-layer WriteInto now happens inside TileMeshLayerProcessor.ProcessOnWorker, invoked " +
                "through the runner.");
            Assert.AreEqual(1, runnerCalls,
                $"TileManager.KickMeshBuild must call '{RunnerCallForm}' exactly once — the single " +
                "decode-once fan-out point for this worker pass.");
        }

        /// <summary>Epic A / A2 (design §B Q1, plan §F tooth 2 structural companion): the source-less
        /// worker pass must NEVER decode — a source-less style layer has no MVT bytes at all. Against a
        /// deliberately-wrong impl that decodes anyway (e.g. the rejected "empty-bytes through the real
        /// fetch/decode path" alternative) this assertion fails.</summary>
        [Test]
        public void RunSourcelessWorkerPass_DoesNotDecode()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "Processing",
                "TileLayerProcessorRunner.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            const string signatureAnchor = "IRenderLayerPayload[] RunSourcelessWorkerPass(";
            string body = ExtractMethodBody(source, signatureAnchor, path);

            int decodeCalls = CountOccurrences(body, DecodeCallForm);
            Assert.AreEqual(0, decodeCalls,
                $"RunSourcelessWorkerPass must contain ZERO '{DecodeCallForm}' call sites — the source-less " +
                "arm never fetches or decodes MVT bytes (Epic A / A2 design §B Q1).");
        }

        /// <summary>Epic A / A4 (design §B Q1-Q3, plan §E-3): re-scoped again — A4 moved the decode out of
        /// the runner into the caller-owned handle, and D1 moved it further still, to the source's
        /// <c>GetTile</c>. <c>RunWorkerPass</c> therefore decodes nothing and reads the caller's reference
        /// exactly once, via <c>decode.Value</c> (R2). The invariant this test has guarded since A1/A3 — the mesh
        /// cadence's OWN decode-once boundary — survives as "reads the lease exactly once, decodes nothing
        /// directly". The source-less pass still never touches either form (unchanged from A2).</summary>
        [Test]
        public void TileLayerProcessorRunner_MeshWorkerPass_ReadsTheSharedDecode_AndNeverDecodesItself()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "Processing",
                "TileLayerProcessorRunner.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            const string workerPassAnchor = "IRenderLayerPayload[] RunWorkerPass(";
            const string sourcelessAnchor = "IRenderLayerPayload[] RunSourcelessWorkerPass(";

            string workerPassBody = ExtractMethodBody(source, workerPassAnchor, path);
            string sourcelessBody = ExtractMethodBody(source, sourcelessAnchor, path);

            Assert.AreEqual(0, CountOccurrences(workerPassBody, DecodeCallForm),
                $"RunWorkerPass must contain ZERO direct '{DecodeCallForm}' calls — the decode happens in " +
                "TileDecodeDispatch, before the lease exists; the runner only reads it.");
            Assert.AreEqual(1, CountOccurrences(workerPassBody, DecodeReadForm),
                $"RunWorkerPass must read '{DecodeReadForm}' exactly once — the mesh worker pass's own " +
                "read of the caller's lease. More than one read is more than one place a released lease " +
                "can be observed, and the fan-out is supposed to happen through the single tile it returns.");
            Assert.AreEqual(0, CountOccurrences(sourcelessBody, DecodeCallForm),
                $"RunSourcelessWorkerPass must contain ZERO '{DecodeCallForm}' calls — the source-less arm " +
                "never decodes MVT bytes (unchanged from A2).");
            Assert.AreEqual(0, CountOccurrences(sourcelessBody, DecodeReadForm),
                $"RunSourcelessWorkerPass must contain ZERO '{DecodeReadForm}' reads — it has no bytes " +
                "and no lease to read (unchanged from A2).");
        }

        /// <summary>Epic A / A3 (plan §F tooth 1 — primary structural delegation tooth, RED-verified
        /// against the pre-A3 <c>SymbolSubsystem</c>): the subsystem no longer decodes, extracts, or
        /// shapes symbols itself — that machinery moved into the processor contract. Against the un-rewired
        /// source (decode at <c>BuildTileAsync</c>'s old <c>:324</c>, <c>ExtractLayers</c> at <c>:325</c>,
        /// <c>ShapeAsync</c> at <c>:333</c>, no runner call) this test FAILS on all four forms — adding the
        /// interface/processor as an unused façade while <c>BuildTileAsync</c> keeps its inline decode/
        /// extract/shape (the "rename" non-implementation) cannot pass.</summary>
        [Test]
        public void SymbolSubsystem_DelegatesDecodeExtractAndShapeToTheProcessorMachinery()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            const string extractLayersCallForm = ".ExtractLayers(";
            const string shapeAsyncCallForm    = ".ShapeAsync(";
            const string runSymbolCallForm     = "TileLayerProcessorRunner.RunSymbolWorkerPass(";

            Assert.AreEqual(0, CountOccurrences(source, DecodeCallForm),
                $"SymbolSubsystem.cs must contain ZERO direct '{DecodeCallForm}' call sites — the " +
                "symbol decode now lives in TileLayerProcessorRunner.RunSymbolWorkerPass (Epic A / A3).");
            Assert.AreEqual(0, CountOccurrences(source, extractLayersCallForm),
                $"SymbolSubsystem.cs must contain ZERO direct '{extractLayersCallForm}' call sites — " +
                "per-layer extraction now happens inside TileSymbolLayerProcessor.ProcessOnWorker.");
            Assert.AreEqual(0, CountOccurrences(source, shapeAsyncCallForm),
                $"SymbolSubsystem.cs must contain ZERO direct '{shapeAsyncCallForm}' call sites — " +
                "per-layer shaping now happens inside TileSymbolLayerProcessor.CompleteOnMainAsync.");
            Assert.AreEqual(1, CountOccurrences(source, runSymbolCallForm),
                $"SymbolSubsystem.cs must call '{runSymbolCallForm}' exactly once — the single " +
                "decode-once fan-out point for the symbol worker pass.");
        }

        /// <summary>Epic A / A6 (plan §E-13 — re-scoped again from A4's form, GENUINELY RED against
        /// pre-A6 source): A6 moved the sole production <c>MvtDecoder.Decode(</c> call OUT of
        /// <c>MapRenderer.Unity</c> entirely, into <c>MvtTileDecoder</c>. The assembly-wide count under
        /// <c>MapRenderer.Unity</c> (including <c>TileDecodeDispatch.cs</c>, the sole permitted file) is ZERO
        /// — the decode left the assembly, it did not just move within it. <b>IR C1 moved the seam again</b>,
        /// out of <c>MapRenderer.Core</c> and into <c>MapRenderer.Jobs</c>; see
        /// <see cref="MapRendererJobs_DecodesMvtOnlyInsideMvtTileDecoder"/> for the positive half (together
        /// the two prove the sole-production-decode-site invariant across BOTH assembly moves).</summary>
        [Test]
        public void MapRendererUnity_DecodesMvtNowhere()
        {
            string root = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity");
            Assert.IsTrue(Directory.Exists(root), $"expected directory to exist at {root}");

            string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            Assert.Greater(files.Length, 0, $"expected at least one .cs file under {root}");

            var offenders = new System.Collections.Generic.List<string>();
            foreach (string file in files)
            {
                if (CountOccurrences(StripLineComments(File.ReadAllText(file)), DecodeCallForm) > 0)
                    offenders.Add(file);
            }

            Assert.IsEmpty(offenders,
                $"no file under MapRenderer.Unity may call '{DecodeCallForm}' — Epic A / A6 relocated the " +
                $"sole production decode site to MvtTileDecoder in Core; found it in: {string.Join(", ", offenders)}");

            // TileDecodeDispatch.cs specifically: it reads through the injected ITileDecoder, not MvtDecoder
            // directly (the A4→A6 handoff this test's name change records), and it is the ONE place a decode
            // is dispatched at all — the D1 successor to SharedTileDecode.GetOrDecode's single call.
            string dispatchPath = Path.Combine(
                root, "Rendering", "Tile", "Processing", "TileDecodeDispatch.cs");
            Assert.IsTrue(File.Exists(dispatchPath), $"expected source file to exist at {dispatchPath}");
            string dispatchSource = StripLineComments(File.ReadAllText(dispatchPath));
            Assert.AreEqual(0, CountOccurrences(dispatchSource, DecodeCallForm),
                $"TileDecodeDispatch.cs must contain ZERO '{DecodeCallForm}' call sites post-A6.");
            Assert.AreEqual(1, CountOccurrences(dispatchSource, "decoder.Decode("),
                "TileDecodeDispatch.cs must call 'decoder.Decode(' exactly once — the injected ITileDecoder " +
                "is the sole read (Epic A / A6), and the single dispatch is what makes 'exactly one decode " +
                "per fetched tile' a property of one method rather than of every source remembering it.");
        }

        /// <summary>Epic A / A6 (plan §E-13 — the positive half of the sole-decode-site proof), <b>rebased
        /// to MapRenderer.Jobs by IR C1</b>: the ONE production <c>MvtDecoder.Decode(</c> call site lives in
        /// <c>Tiles/ITileDecoder.cs</c> (<c>MvtTileDecoder.Decode</c>) and nowhere else — and
        /// <c>MapRenderer.Core</c>, which used to host it, now decodes <b>nowhere</b>. The positive count
        /// (not just a zero-elsewhere scan) proves the decode actually landed there, not merely that the
        /// assemblies it left went quiet. Widening this scan to <c>Assets/Code</c> would sweep the TEST
        /// assembly too, where <c>DecodeTests</c>/<c>MvtPropertyDecodeTests</c> legitimately call
        /// <c>MvtDecoder.Decode(</c> for fixture setup — so the per-assembly assertions (this one +
        /// <see cref="MapRendererUnity_DecodesMvtNowhere"/>) are deliberately narrower than one combined
        /// scan. RED pre-C1 on both clauses: the file lived under Core, so the Jobs count was 0 and the Core
        /// scan found it.</summary>
        [Test]
        public void MapRendererJobs_DecodesMvtOnlyInsideMvtTileDecoder()
        {
            string jobsRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            Assert.IsTrue(Directory.Exists(jobsRoot), $"expected directory to exist at {jobsRoot}");

            string[] files = Directory.GetFiles(jobsRoot, "*.cs", SearchOption.AllDirectories);
            Assert.Greater(files.Length, 0, $"expected at least one .cs file under {jobsRoot}");

            var offenders = new System.Collections.Generic.List<string>();
            int tileDecoderCount = 0;
            foreach (string file in files)
            {
                string fileName = Path.GetFileName(file);
                int count = CountOccurrences(StripLineComments(File.ReadAllText(file)), DecodeCallForm);
                // MvtDecoder.cs DEFINES Decode (the method body), it does not CALL the qualified
                // "MvtDecoder.Decode(" form on itself — excluded from the scan, not a call site.
                if (fileName == "MvtDecoder.cs") continue;
                if (fileName == "ITileDecoder.cs") { tileDecoderCount = count; continue; }
                if (count > 0) offenders.Add(file);
            }

            Assert.IsEmpty(offenders,
                $"only Tiles/ITileDecoder.cs may call '{DecodeCallForm}' under MapRenderer.Jobs; " +
                $"found it in: {string.Join(", ", offenders)}");
            Assert.AreEqual(1, tileDecoderCount,
                $"Tiles/ITileDecoder.cs must call '{DecodeCallForm}' exactly once — MvtTileDecoder.Decode, " +
                "the sole production decode call site (Epic A / A6; rehomed to Jobs by IR C1).");

            // IR C1's own half: the assembly the seam LEFT must be silent. Without this clause a
            // half-finished move — the decoder copied into Jobs while Core keeps a caller — would pass.
            string coreRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Core");
            Assert.IsTrue(Directory.Exists(coreRoot), $"expected directory to exist at {coreRoot}");
            string[] coreFiles = Directory.GetFiles(coreRoot, "*.cs", SearchOption.AllDirectories);
            Assert.Greater(coreFiles.Length, 100,
                "precondition: the Core scan must visit a real corpus (>100 .cs files); a scan that visited " +
                "none would report 'no offenders' just as loudly");

            var coreOffenders = new System.Collections.Generic.List<string>();
            foreach (string file in coreFiles)
                if (CountOccurrences(StripLineComments(File.ReadAllText(file)), DecodeCallForm) > 0)
                    coreOffenders.Add(file);

            Assert.IsEmpty(coreOffenders,
                $"no file under MapRenderer.Core may call '{DecodeCallForm}' — IR C1 moved the whole tile-" +
                "decode seam out of Core (ARCHITECTURE.md §2: the product is Unity + Jobs; Core is a " +
                $"convenience, never the preferred home for logic). Found it in: {string.Join(", ", coreOffenders)}");
        }

        /// <summary>Epic A / A6 (plan §E-13, F-1 — WriteInto-path neutralization, structural). The
        /// fill/line/symbol fan-out — from feature selection through mesh/symbol build — references NO MVT
        /// carrier type (<c>MvtTile</c>/<c>MvtFeature</c>/<c>MvtLayer</c>/<c>MvtDecoder</c>) by name; only the
        /// neutral <c>IDecodedTile</c>/<c>ITileLayer</c>/<c>IFeature</c>/<c>ITileDecoder</c> surface (§B-1,
        /// §B-3). Genuinely RED pre-A6: every one of these 15 files named an MVT carrier type. The set is
        /// still 15 files, re-derived not re-asserted — IR B4 MOVED the symbol extractor from
        /// <c>MapRenderer.Core/Style/Symbol/</c> to <c>MapRenderer.Unity/Text/</c>, and IR C1 moved
        /// <c>FeatureSelector</c> and <c>SourceLayerResolver</c> from <c>MapRenderer.Core</c> to
        /// <c>MapRenderer.Jobs/Tiles/</c>; none was added or removed by either.
        ///
        /// <para><b>Anti-vacuity, REPLACED in IR B4.</b> The clause used to require the bare substring
        /// <c>MvtGeometry</c> in each codec file's RAW text. Both halves broke: B2's rename made
        /// <c>new MvtGeometryMaterializer(</c> satisfy it by substring on the line file, and the count included
        /// comments, so B4's removal of the last real <c>MvtGeometry.Decode</c> call left the symbol file
        /// satisfied by an XML <c>see cref</c>. It could no longer detect a gutted file — and it could not
        /// merely be tightened, because its premise (<i>these files still decode geometry</i>) is expiring by
        /// design as this epic moves every consumer onto the shared buffer: a word-boundary
        /// <c>\bMvtGeometry\b</c> would go RED on the line file for a legitimate reason.
        /// It is therefore RE-POINTED at what is still true — a per-file REQUIRED-IDENTIFIER SET naming the
        /// geometry mechanism each file actually uses (matched comment-stripped, with word boundaries, so
        /// neither prose nor a longer identifier containing the token can satisfy it) plus the explicit
        /// negative that the retired decoder's bare name is gone. RED-verified against a gutted line file, a
        /// gutted symbol file, and a symbol file whose tokens survive only inside a block comment.</para></summary>
        [Test]
        public void WriteIntoPath_ReferencesNoMvtCarrierTypes()
        {
            string unityRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity");
            // IR C1: FeatureSelector and SourceLayerResolver left MapRenderer.Core for MapRenderer.Jobs
            // along with the rest of the decode seam. Same two files, same claim, new root.
            string jobsRoot  = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");

            (string root, string relativePath)[] scanned =
            {
                (unityRoot, Path.Combine("Rendering", "Tile", "Processing", "ITileLayerProcessor.cs")),
                (unityRoot, Path.Combine("Rendering", "Tile", "Processing", "TileMeshLayerProcessor.cs")),
                (unityRoot, Path.Combine("Rendering", "Tile", "Processing", "TileSymbolLayerProcessor.cs")),
                (unityRoot, Path.Combine("Rendering", "Tile", "Processing", "TileBackgroundLayerProcessor.cs")),
                (unityRoot, Path.Combine("Rendering", "Style", "ITileMeshRenderLayer.cs")),
                (unityRoot, Path.Combine("Rendering", "Style", "FillRenderLayer.cs")),
                (unityRoot, Path.Combine("Rendering", "Style", "LineRenderLayer.cs")),
                (unityRoot, Path.Combine("Rendering", "Meshing", "StyledFillTileBuilder.cs")),
                (unityRoot, Path.Combine("Rendering", "Meshing", "StyledLineTileBuilder.cs")),
                (unityRoot, Path.Combine("Text", "StyledSymbolTileBuilder.cs")),
                (unityRoot, Path.Combine("Rendering", "Tile", "Processing", "TileLayerProcessorRunner.cs")),
                (unityRoot, Path.Combine("Rendering", "Tile", "Processing", "TileDecodeDispatch.cs")),
                (jobsRoot,  Path.Combine("Tiles", "FeatureSelector.cs")),
                (jobsRoot,  Path.Combine("Tiles", "SourceLayerResolver.cs")),
                (unityRoot, Path.Combine("Text", "SymbolFeatureExtractor.cs")),
            };
            Assert.AreEqual(15, scanned.Length, "the F-1 file set is pinned at 15 files (plan §E-13).");

            string[] mvtCarrierTokens = { "MvtTile", "MvtFeature", "MvtLayer", "MvtDecoder" };
            var offenders = new System.Collections.Generic.List<string>();

            foreach ((string root, string relativePath) in scanned)
            {
                string path = Path.Combine(root, relativePath);
                Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
                string text = File.ReadAllText(path);
                foreach (string token in mvtCarrierTokens)
                    if (CountOccurrences(text, token) > 0)
                        offenders.Add($"{relativePath} ('{token}')");
            }

            Assert.IsEmpty(offenders,
                "the WriteInto-path fan-out must reference ZERO MVT carrier types (MvtTile/MvtFeature/" +
                "MvtLayer/MvtDecoder) — only the neutral IDecodedTile/ITileLayer/IFeature/ITileDecoder " +
                $"surface (Epic A / A6). Offenders: {string.Join(", ", offenders)}");

            // Anti-vacuity — REPLACED in tile-geometry IR B4; see the XML doc above for why the previous
            // clause could no longer detect a gutted file. Each geometry-obtaining file must name every
            // identifier of the mechanism it actually uses today, matched on comment-stripped text with word
            // boundaries.
            (string relativePath, string[] required)[] mechanisms =
            {
                // IR C1 P2: both files stopped minting and now BORROW the source-layer buffer, so
                // "MvtGeometryMaterializer" is no longer an identifier either of them uses — it was REPLACED
                // (not dropped: a required-set with entries removed is a disarmed test) by the identifiers of
                // the mechanism they use instead. IR C1 P3 does the same one step further: the store is gone,
                // so symbol's `GetOrMaterialize` is replaced by `tileLayer` — the object it now reads the
                // buffer off. `Geometry` alone would be a poor token (it is a substring of
                // TileGeometryBuffers and matched with word boundaries would still be weak); `tileLayer` is
                // the identifier that only exists because the LAYER owns the geometry.
                (Path.Combine("Rendering", "Meshing", "StyledLineTileBuilder.cs"),
                    new[] { "TileGeometryBuffers", "RingFeatureIdx", "RingOffsets", "LineRibbonJob" }),
                (Path.Combine("Text", "SymbolFeatureExtractor.cs"),
                    new[] { "TileGeometryBuffers", "RingFeatureIdx", "tileLayer", "RingOffsets",
                            "LineAnchorPlacement" }),
            };

            foreach ((string relativePath, string[] required) in mechanisms)
            {
                string path = Path.Combine(unityRoot, relativePath);
                Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
                string code = StripComments(File.ReadAllText(path));

                foreach (string identifier in required)
                {
                    Assert.IsTrue(ContainsIdentifier(code, identifier),
                        $"{relativePath} must name '{identifier}' — this file still obtains geometry, now by " +
                        $"BORROWING the shared buffer, and gutting it would drop all {required.Length} of " +
                        "this file's required identifiers. WHY THIS CLAUSE CHANGED " +
                        "(IR B4): the previous one counted the bare substring 'MvtGeometry' in RAW text. B2's " +
                        "rename satisfied it through 'MvtGeometryMaterializer' (a longer identifier " +
                        "containing the token) and B4's removal of the last MvtGeometry.Decode call left it " +
                        "satisfied by an XML doc comment — its premise ('these files still decode geometry') " +
                        "expired by design as this epic moved the consumers onto the shared buffer. Cost, " +
                        "accepted deliberately: a rename of the mechanism makes this go RED loudly, whereas " +
                        "the old clause's rename made it go green silently.");
                }

                // The other half of the same intent, made explicit: the retired managed decoder is GONE from
                // both files — precisely what the old clause could no longer tell you.
                Assert.IsFalse(ContainsIdentifier(code, "MvtGeometry"),
                    $"{relativePath} must NOT name the bare identifier 'MvtGeometry' — the managed reference " +
                    "decoder has zero production callers as of IR B4. Word-boundary matched, so a longer " +
                    "identifier merely CONTAINING the token would not be a hit (e.g. " +
                    "'MvtGeometryMaterializer', which both files named until IR C1 P2 moved them off minting " +
                    "and onto the borrowed buffer); comment-stripped, so a doc comment cannot satisfy or " +
                    "violate it.");
            }
        }

        /// <summary>Epic A / A4 (plan §F-2, symbol-body count): the symbol cadence no longer decodes for
        /// itself — it reads the shared entry exactly once, same as the mesh cadence.</summary>
        [Test]
        public void RunSymbolWorkerPass_DoesNotDecodeItself_ReadsTheSharedEntry()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "Processing",
                "TileLayerProcessorRunner.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            const string signatureAnchor = "void RunSymbolWorkerPass(";
            string body = ExtractMethodBody(source, signatureAnchor, path);

            Assert.AreEqual(0, CountOccurrences(body, DecodeCallForm),
                $"RunSymbolWorkerPass must contain ZERO direct '{DecodeCallForm}' calls — the decode happens " +
                "in TileDecodeDispatch, before the lease exists.");
            Assert.AreEqual(1, CountOccurrences(body, DecodeReadForm),
                $"RunSymbolWorkerPass must read '{DecodeReadForm}' exactly once — the symbol cadence's " +
                "own read of the caller's lease.");
        }

        /// <summary>Epic A / A7 (tooth 1 — the primary
        /// structural tooth): <c>TileManager</c> must be byte-agnostic — it names none of the five
        /// byte-centric tokens the pre-raise coordinator used (<c>TileResponse</c>/<c>IDataSource</c>/
        /// <c>TileScheduler</c>/any concrete lease type/standalone <c>TileCache</c>), and DOES name the raised
        /// seam (<c>ITileFeatureSource</c>/<c>SharedDisposable</c>). Genuinely RED pre-A7: the file named
        /// all five tokens throughout (7/4/3/5/4 occurrences respectively, verified against the pre-A7
        /// commit).
        ///
        /// <c>TileCache</c> uses a WORD-BOUNDARY match, not the plain substring <see cref="CountOccurrences"/>
        /// every other A6 tooth in this file uses — <c>TileManager.cs</c> keeps 19 <c>PreparedTileCache</c>
        /// references (the S82 <c>_prepared</c> mesh/layer cache, plan §D explicitly leaves it UNTOUCHED), and
        /// the substring <c>"TileCache"</c> embeds inside <c>"PreparedTileCache"</c> (…d|T… — both word
        /// characters, no boundary) — so a plain substring count would find those 19 kept references and could
        /// NEVER go green even after a flawless A7. <c>\bTileCache\b</c> excludes <c>PreparedTileCache</c>
        /// while still catching every standalone <c>TileCache</c> mention. The other four tokens have no
        /// such embedding collision, so they use the plain substring matcher.</summary>
        [Test]
        public void TileManager_ConsumesNoByteSource()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            Assert.AreEqual(0, CountOccurrences(source, "TileResponse"),
                "TileManager.cs must contain ZERO 'TileResponse' occurrences — the byte-response type is now " +
                "an MvtTileFeatureSource-internal detail (Epic A / A7).");
            Assert.AreEqual(0, CountOccurrences(source, "IDataSource"),
                "TileManager.cs must contain ZERO 'IDataSource' occurrences — the byte fetcher is wrapped " +
                "BELOW the raised ITileFeatureSource seam, never named at the coordinator.");
            Assert.AreEqual(0, CountOccurrences(source, "TileScheduler"),
                "TileManager.cs must contain ZERO 'TileScheduler' occurrences — scheduling now lives inside " +
                "MvtTileFeatureSource.");
            Assert.AreEqual(0, CountOccurrences(source, "SharedTileDecode"),
                "TileManager.cs must contain ZERO 'SharedTileDecode' occurrences — the type is deleted, and " +
                "a surviving mention would be a stale comment describing a lifetime model that no longer exists.");
            Assert.AreEqual(0, CountOccurrences(source, "DecodedTileLease"),
                "TileManager.cs must contain ZERO 'DecodedTileLease' occurrences either — the coordinator " +
                "holds only the polymorphic SharedDisposable<IDecodedTile>, never a concrete lease " +
                "implementation (R2 deleted DecodedTileLease outright). Widened from the SharedTileDecode " +
                "clause rather than replacing it: naming a concrete lease type would re-create exactly the " +
                "coupling the old clause forbade.");

            int standaloneTileCacheCount = Regex.Matches(source, @"\bTileCache\b").Count;
            Assert.AreEqual(0, standaloneTileCacheCount,
                "TileManager.cs must contain ZERO STANDALONE 'TileCache' occurrences (word-boundary match, " +
                "excluding the 19 kept 'PreparedTileCache' references, which plan §D leaves untouched) — the " +
                "byte-level LRU cache now lives inside MvtTileFeatureSource.");

            Assert.Greater(CountOccurrences(source, "ITileFeatureSource"), 0,
                "TileManager.cs must reference ITileFeatureSource — the raised seam it now holds instead of " +
                "IDataSource/TileScheduler.");
            Assert.Greater(CountOccurrences(source, "SharedDisposable"), 0,
                "TileManager.cs must reference SharedDisposable — R2's decode-provisioning reference count " +
                "(SharedDisposable<IDecodedTile>) it threads through the mesh/symbol kick, and whose " +
                "reference it owns and releases. Successor to the R1-era IDecodedTileHandle clause.");
        }

        /// <summary>Epic A / A7 (plan §F tooth 6, §G-1): pins the pool-completion invariant
        /// <c>DrainMeshBuilds</c>'s spin-on-<c>IsCompleted</c> depends on — <c>MvtTileFeatureSource.GetTile</c>
        /// must introduce NO main-thread hop (the scheduler's own continuation already ends on the pool), or
        /// the drain-spin would never observe completion without pumping the PlayerLoop (the exact A5b
        /// <c>KickMeshBuild</c> hazard this mirrors). Lightweight/structural — the behavioural net is the
        /// <c>DrainMeshBuilds</c> snapshot/leak exercise elsewhere in the suite (plan §F-5).</summary>
        [Test]
        public void MvtTileFeatureSource_GetTile_NeverHopsToMainThread()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "Processing",
                "MvtTileFeatureSource.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            Assert.AreEqual(0, CountOccurrences(source, "SwitchToMainThread"),
                "MvtTileFeatureSource.cs must contain ZERO 'SwitchToMainThread' occurrences — GetTile's " +
                "continuation must stay on the pool (the scheduler's own SwitchToThreadPool already handles " +
                "this), preserving the configureAwait:false pool-completion invariant DrainMeshBuilds' spin " +
                "depends on (Epic A / A7 §G-1).");
        }

        /// <summary>Epic A / A6.1 (plan §F-1 — rename-complete structural tooth): asserts zero occurrences,
        /// anywhere under <c>Assets/Code</c>, of the retired geometry-type enum's old name (the concatenated
        /// form built from "Mvt" + "GeometryType" — the renamed geometry-type enum now lives as
        /// <c>Core.Tiles.TileGeometryType</c>). Genuinely RED pre-rename: the old name was scattered across
        /// 25 files (Core/Unity/Tests). Self-reference trap: this test's OWN source file lives under
        /// <c>Assets/Code</c>, so the search token is built by concatenation rather than written as a
        /// literal — writing the literal here (even in this very doc comment) would self-trip the assertion.
        /// Positive half: the new dedicated type file exists and the type resolves in <c>Core.Tiles</c>
        /// (compiling this test file at all proves the second half).</summary>
        [Test]
        public void GeometryTypeEnum_RenameIsComplete_NoOldTokenSurvives()
        {
            string root = Path.Combine(Application.dataPath, "Code");
            Assert.IsTrue(Directory.Exists(root), $"expected directory to exist at {root}");

            string oldTypeName = "Mvt" + "GeometryType";

            string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            Assert.Greater(files.Length, 0, $"expected at least one .cs file under {root}");

            var offenders = new System.Collections.Generic.List<string>();
            foreach (string file in files)
            {
                if (CountOccurrences(File.ReadAllText(file), oldTypeName) > 0)
                    offenders.Add(file);
            }

            Assert.IsEmpty(offenders,
                $"no file under {root} may reference the retired geometry-type enum name — Epic A / A6.1 " +
                $"renamed it to Core.Tiles.TileGeometryType; found the old name in: {string.Join(", ", offenders)}");

            string newTypePath = Path.Combine(root, "MapRenderer.Core", "Tiles", "TileGeometryType.cs");
            Assert.IsTrue(File.Exists(newTypePath), $"expected the renamed type's dedicated file at {newTypePath}");
        }

        // ── D0: the decode-abandonment funnels ───────────────────────────────────────────────
        //
        // Three clauses, one per funnel D0 created or adopted. Each pins a CHOKEPOINT rather than a call
        // form: the next stage hangs a per-record / per-entry release obligation on these methods, and an
        // obligation is only as strong as the guarantee that nothing bypasses the method carrying it. A
        // funnel with no structural pin is a convention.

        private const string TileManagerPathTail       = "TileManager.cs";
        private const string RenderTeardownCallForm    = "RenderTeardownRecord(";
        private const string DestroyTrackedMeshesForm  = "DestroyTrackedMeshes(";

        // The teardown pass is matched by PATTERN, not by the literal "foreach (var kv in _loaded)" it used
        // to be: a reformat or a renamed loop variable is not a semantic change and must not red the tooth
        // (arm 1 NIT 2). What the pattern still requires is the part that carries the claim — ONE foreach
        // over _loaded.
        private const string LoadedForeachPattern = @"foreach\s*\(\s*var\s+\w+\s+in\s+_loaded\s*\)";

        /// <summary>D0 (funnel 1 — the record): <c>TileManager.DoDispose</c> must tear records down through
        /// <c>RenderTeardownRecord</c>, the one funnel cover-change, eviction and restyle already use — not
        /// through hand-rolled loops of its own. Genuinely RED against the pre-D0 source, where
        /// <c>DoDispose</c> walked <c>_loaded</c> THREE times (spin+dispose mesh builds, cancel+observe
        /// fetches, destroy meshes), called <c>DestroyTrackedMeshes</c> directly and never called
        /// <c>RenderTeardownRecord</c> at all. Comment-stripped, so the prose above the loop cannot satisfy
        /// any clause.
        ///
        /// <para><b>Strengthened in the D0 fix pass (arm 2 REQUIRED / arm 1 followUp 1).</b> The first form
        /// of this test counted call TEXT, so three real bypasses passed it: guarding the funnel call with
        /// <c>if (lt.Built)</c>, skipping records with a <c>continue</c>, and moving the sole
        /// <c>_loaded.Clear()</c> BEFORE the pass. Each left every count unchanged while dropping records
        /// whose in-flight fetch / mesh build was never stashed in the S48/S84 pens. The counts are now
        /// backed by <see cref="AssertUnconditionallyReachedOnce"/> (brace depth + statement start + no
        /// earlier jump) and by an ORDERING comparison on character offsets. All three bypasses are
        /// RED-verified against this form.</para></summary>
        [Test]
        public void TileManagerDoDispose_TearsDownEveryRecordThroughTheSingleFunnel()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", TileManagerPathTail);
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");

            string body = StripComments(
                ExtractMethodBody(File.ReadAllText(path), "protected override void DoDispose()", path));

            // ONE pass over _loaded — and the pass itself is reached on every path through DoDispose.
            Assert.AreEqual(1, Regex.Matches(body, LoadedForeachPattern).Count,
                "TileManager.DoDispose must walk _loaded EXACTLY once — one pass, one funnel call per " +
                "record. Three passes (the pre-D0 shape) is three places a later obligation can be missed.");
            AssertUnconditionallyReachedOnce(body, LoadedForeachPattern,
                "the single _loaded teardown pass in TileManager.DoDispose",
                "a pass reached only under a condition (or behind an early return) is a teardown that can " +
                "skip every record at once.");

            // Nothing reaches AROUND the funnel: a record whose meshes are destroyed here never had its
            // in-flight fetch / mesh build stashed in the S48/S84 pens.
            Assert.AreEqual(0, CountOccurrences(body, DestroyTrackedMeshesForm),
                $"TileManager.DoDispose must contain ZERO direct '{DestroyTrackedMeshesForm}' calls — " +
                "destroying a record's meshes is only PART of what the funnel does; a hand-rolled destroy " +
                "leaves the record's fetch and mesh-build tasks unstashed and unobserved.");

            // Exactly one funnel call, reached on every iteration of that pass.
            Assert.AreEqual(1, CountOccurrences(body, RenderTeardownCallForm),
                $"TileManager.DoDispose must call '{RenderTeardownCallForm}' EXACTLY once — teardown is the " +
                "fourth mouth of the single record-teardown funnel, not a fourth teardown path. A " +
                "per-record obligation added to RenderTeardownRecord must reach the records that are still " +
                "loaded when the manager is disposed.");

            Match pass = Regex.Match(body, LoadedForeachPattern);
            string passBody = ExtractBlockAfter(body, pass.Index + pass.Length, path, out int passEndIndex);
            AssertUnconditionallyReachedOnce(passBody, Regex.Escape(RenderTeardownCallForm),
                $"'{RenderTeardownCallForm}' inside DoDispose's single _loaded pass",
                "this is the clause that fails for `if (lt.Built) RenderTeardownRecord(ref lt);` and for a " +
                "`continue` that skips a record — both keep the call textually present while abandoning " +
                "records whose mesh build / fetch was never stashed.");

            // Cleared exactly once, unconditionally, and AFTER the pass — compared by OFFSET, because the
            // counts above are identical whichever side of the loop the clear sits on.
            Assert.AreEqual(1, CountOccurrences(body, "_loaded.Clear()"),
                "TileManager.DoDispose must clear _loaded EXACTLY once — a second clear would mean records " +
                "left the map by some route other than the funnel.");
            AssertUnconditionallyReachedOnce(body, Regex.Escape("_loaded.Clear()"),
                "'_loaded.Clear()' in TileManager.DoDispose",
                "a conditional clear leaves torn-down records in the map after teardown.");
            Assert.Greater(body.IndexOf("_loaded.Clear()", StringComparison.Ordinal), passEndIndex,
                "TileManager.DoDispose must clear _loaded AFTER the single teardown pass, not before — " +
                "asserted on character offsets because both counts are 1 either way. A clear that runs " +
                "FIRST empties the map, so the pass then walks nothing and every unsettled record's mesh " +
                "build and fetch is dropped without ever reaching the funnel.");
        }

        /// <summary>D0 (funnel 2 — the abandoned fetch): the discard arm must be structurally incapable of
        /// handing a decode handle back to its CALLER. <c>DiscardFetchOutcome</c> returns
        /// <see langword="void"/>, so no discard SITE can bind, retain or re-consume what the fetch produced
        /// — caller-side ownership is compiler-enforced, not conventional. The two discard sites must call
        /// it and must NOT call the owning arm (<c>TakeDecodeFromFetch</c>). Genuinely RED against the
        /// pre-D0 source, which had ONE method, <c>ObserveFetchOutcome(req, logErrors:)</c>, returning the
        /// handle to all four callers — two of which threw it away.
        ///
        /// <para><b>Scope, narrowed in the D0 fix pass (arm 2 NIT 1).</b> <see langword="void"/> constrains
        /// the CALLER and nothing else: it does not stop <c>DiscardFetchOutcome</c>'s own body from
        /// retaining, re-publishing or simply forgetting what it observed. The body half is pinned
        /// separately below — the funnel must actually observe the outcome, so an EMPTY body fails (arm 2
        /// REQUIRED) — and the runtime release obligation that D1 will hang here is D1's leak teeth
        /// (T-D1…T-D6), not a claim this text oracle makes.</para></summary>
        [Test]
        public void TheFetchDiscardFunnel_CannotHandBackADecodeHandle()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", TileManagerPathTail);
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);
            string stripped = StripComments(source);

            Assert.AreEqual(0, CountOccurrences(stripped, "ObserveFetchOutcome"),
                "TileManager.cs must contain ZERO 'ObserveFetchOutcome' occurrences — the one method whose " +
                "bool parameter was ALMOST the own-vs-discard discriminator is split into two whose " +
                "signatures cannot be confused.");
            Assert.AreEqual(1, CountOccurrences(stripped, "private void DiscardFetchOutcome("),
                "TileManager.cs must declare 'private void DiscardFetchOutcome(' exactly once — the VOID " +
                "return type is the enforcement. The moment this method hands something back it stops " +
                "being a funnel and becomes a fourth thing to remember at every drop site.");
            Assert.AreEqual(0, CountOccurrences(stripped, "SharedDisposable<IDecodedTile> DiscardFetchOutcome("),
                "DiscardFetchOutcome must NEVER return SharedDisposable<IDecodedTile> — see above; this " +
                "clause is the one that fails if a later change 'just returns it for convenience'.");

            // The funnel's OWN body, not just its call sites (arm 2 REQUIRED): the whole point of routing
            // every abandonment through one method is that the method DOES something, and an empty body
            // passes every call-site clause above while re-opening the S84 console flood. The observation
            // must be the try's unconditional statement — a guarded GetResult observes only some outcomes.
            string funnelBody = StripComments(
                ExtractMethodBody(source, "private void DiscardFetchOutcome(", path));
            AssertUnconditionallyReachedOnce(funnelBody, @"\btry\b",
                "the try block in DiscardFetchOutcome",
                "the observation must run guarded against the fault it exists to swallow, on every call.");

            Match tryKeyword = Regex.Match(funnelBody, @"\btry\b");
            string tryBody = ExtractBlockAfter(funnelBody, tryKeyword.Index + tryKeyword.Length, path, out _);
            AssertUnconditionallyReachedOnce(tryBody, @"\w+\s*\.\s*GetAwaiter\(\)\s*\.\s*GetResult\(\)",
                "the fetch-outcome observation (`…GetAwaiter().GetResult()`) inside DiscardFetchOutcome",
                "an EMPTY (or conditionally-observing) funnel body satisfies every call-site clause above " +
                "while leaving the abandoned task's fault unobserved — the S84 UnityWebRequestException " +
                "console flood, back through the method that exists to prevent it.");

            foreach (string discardSiteAnchor in new[]
                     { "private void DrainPendingFetchDisposal(", "protected override void DoDispose()" })
            {
                string body = StripComments(ExtractMethodBody(source, discardSiteAnchor, path));
                Assert.Greater(CountOccurrences(body, "DiscardFetchOutcome("), 0,
                    $"the fetch-abandonment site anchored at '{discardSiteAnchor}' must observe its fetch " +
                    "outcomes through DiscardFetchOutcome — an unobserved fetch task is the S84 " +
                    "UnityWebRequestException console flood. STRUCTURAL HINT, stated as one: this is a " +
                    "PRESENCE count, not a reachability proof, and unlike the other two funnels it cannot " +
                    "be strengthened into one — both sites call the funnel from inside a pen-drain loop " +
                    "that legitimately `continue`s past a task that has not completed yet, so 'reached on " +
                    "every iteration' is not a property this site HAS. What every pen entry is eventually " +
                    "observed is D1's runtime leak teeth to prove, not this clause's.");
                Assert.AreEqual(0, CountOccurrences(body, "TakeDecodeFromFetch("),
                    $"the fetch-abandonment site anchored at '{discardSiteAnchor}' must NOT call " +
                    "TakeDecodeFromFetch — that is the arm whose caller OWNS the handle, and this site has " +
                    "nobody to hand it to.");
            }
        }

        /// <summary>D0 (funnel 4 — the parked symbol entry): every site that DISCARDS parked builds must go
        /// through <c>SymbolSubsystem.DrainAndDiscardParkedBuilds</c>. Exactly two
        /// <c>_pendingSpriteQueue.TryDequeue(</c> sites may exist in the file — the funnel, and
        /// <c>PumpBuilds</c>' live drain, which CONSUMES entries rather than discarding them and is a
        /// different job. Genuinely RED against the pre-D0 source, which had three: the live drain plus two
        /// bare <c>while (TryDequeue(out _)) { }</c> loops in <c>SetStyle</c> and <c>DoDispose</c>, i.e. two
        /// future drop paths where there should be one.
        ///
        /// <para><b>Strengthened in the D0 fix pass (arm 2 REQUIRED).</b> The per-site call clause counted
        /// text, so <c>if (cond) DrainAndDiscardParkedBuilds();</c> passed it. It now goes through
        /// <see cref="AssertUnconditionallyReachedOnce"/>.</para></summary>
        [Test]
        public void TheParkedBuildPurge_HasExactlyOneDiscardFunnel()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            const string dequeueForm = "_pendingSpriteQueue.TryDequeue(";
            const string funnelCall  = "DrainAndDiscardParkedBuilds()";

            Assert.AreEqual(2, CountOccurrences(StripComments(source), dequeueForm),
                $"SymbolSubsystem.cs must contain EXACTLY two '{dequeueForm}' sites — the discard " +
                "funnel and PumpBuilds' live drain. A third is a new drop path that a later per-entry " +
                "obligation would not know about.");

            string funnelBody = StripComments(
                ExtractMethodBody(source, "private void DrainAndDiscardParkedBuilds(", path));
            Assert.AreEqual(1, CountOccurrences(funnelBody, dequeueForm),
                $"DrainAndDiscardParkedBuilds must be the one that dequeues — it must contain the " +
                $"'{dequeueForm}' loop itself, not delegate it.");

            foreach (string purgeSiteAnchor in new[]
                     { "void SetStyle(StyleDocument", "protected override void DoDispose()" })
            {
                string body = StripComments(ExtractMethodBody(source, purgeSiteAnchor, path));
                Assert.AreEqual(0, CountOccurrences(body, dequeueForm),
                    $"the parked-build purge site anchored at '{purgeSiteAnchor}' must NOT dequeue the " +
                    "parked queue inline — that is what made it a second drop path.");
                AssertUnconditionallyReachedOnce(body, Regex.Escape(funnelCall),
                    $"'{funnelCall}' at the parked-build purge site anchored at '{purgeSiteAnchor}'",
                    "parked builds die with the old style scope and with teardown, and both deaths run the " +
                    "same code — unconditionally, or the entries a later per-entry obligation covers are " +
                    "the ones that survive a restyle. `if (cond) DrainAndDiscardParkedBuilds();` passed " +
                    "the first form of this clause (arm 2 REQUIRED); it is RED against this one.");
            }
        }

        /// <summary>
        /// D1 fix (funnel 4): the parked drain's dispatch must take <b>no</b> cancellation token, and both
        /// of its ownership-guard releases must sit in a <c>finally</c> rather than a trailing statement.
        ///
        /// <para><b>What is NOT here any more, and why.</b> This tooth used to count
        /// <c>DecodeRef.Dispose()</c> spellings and justify itself with "the ct-drop is unreachable from a
        /// test". Both were wrong. A count cannot tell a release that runs from one that is guarded, nested
        /// or jumped over, and the ct-drop IS reachable — reflecting the private CTS and cancelling WITHOUT
        /// draining produces exactly the interleaving a pool-vs-main race produces, which is what
        /// <c>SymbolParkedRedecodeTests.AParkedEntryCancelledWithoutADrain_…</c> now drives. The
        /// pre-handoff guard has a runtime tooth too
        /// (<c>AParkedEntryWhoseWorkerCannotBeBuilt_StillReleasesItsReference</c>), and the post-drain
        /// enqueue race — R1: now made STRUCTURALLY impossible by the atomic park, so its runtime tooth is
        /// <c>AParkBlockedByTheAbandonDrainGate_AcquiresNothingUntilItHoldsTheGate</c>, the successor to the
        /// retired <c>AParkEnqueuedAfterItsCancellersDrain_…</c>. What is left here is the residue no
        /// runtime test can see.</para>
        ///
        /// <para><b>The <c>cancellationToken:</c> clause is the load-bearing one</b>, and it is genuinely
        /// structural: passing <c>cancellationToken: captured.Ct</c> makes UniTask skip the delegate
        /// entirely, and the delegate is the only release for a dispatched entry — but reaching that state
        /// needs a cancellation landing between the drain's own check and the dispatch, which is the one
        /// window a test cannot force. It is narrowed to the DISPATCH CALL now (arm 1 NIT 4): the old form
        /// forbade the token anywhere in <c>PumpBuilds</c> and would have tripped on an unrelated future
        /// dispatch, for the wrong reason.</para>
        ///
        /// <para><b>RED injections:</b> restore <c>cancellationToken: captured.Ct</c> on the dispatch; or
        /// move either guard release out of its <c>finally</c> into a trailing statement.</para>
        /// </summary>
        [Test]
        public void TheParkedDrainsDispatch_TakesNoCancellationToken_AndItsGuardReleasesSitInFinallys()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            string pumpBody   = StripComments(ExtractMethodBody(source, "public void PumpBuilds()", path));
            string funnelBody = StripComments(
                ExtractMethodBody(source, "private void DrainAndDiscardParkedBuilds(", path));

            // ── The dispatch, narrowed to its own argument list ──────────────────────────────────────
            int dispatchIndex = pumpBody.IndexOf("UniTask.RunOnThreadPool(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(dispatchIndex, 0,
                "PumpBuilds must still dispatch the parked drain's worker phase through " +
                "UniTask.RunOnThreadPool — a direct call would put a full extract on the frame thread.");
            string dispatchCall = ExtractParenAfter(pumpBody, dispatchIndex, path);
            Assert.AreEqual(0, CountOccurrences(dispatchCall, "cancellationToken:"),
                "PumpBuilds' parked dispatch must NOT pass a cancellationToken to RunOnThreadPool. A " +
                "cancelled token makes UniTask skip the delegate, and the delegate's `finally` is the only " +
                "release site for a dispatched entry's reference — the exact hazard that got option T " +
                "rejected, reached again by the eager decode and handled here. The in-lambda ct check inside " +
                "the try is the equivalent guard and keeps the finally reachable.");

            // ── PLACEMENT, not presence: the two guard releases are in `finally` blocks ───────────────
            int releasesInFinallys = 0;
            foreach (Match keyword in Regex.Matches(pumpBody, @"\bfinally\b"))
            {
                string block = ExtractBlockAfter(pumpBody, keyword.Index, path, out _);
                if (CountOccurrences(block, "captured.Decode.Release()") > 0) releasesInFinallys++;
            }
            Assert.AreEqual(2, releasesInFinallys,
                "BOTH of the dispatched-entry's release sites must sit in a `finally`, not as a trailing " +
                "statement: the worker delegate's (the mesh/symbol pass can throw) and the dequeue→" +
                "worker-start ownership guard's (processor construction over a stale layer index can throw, " +
                "and at that point the queue is no longer an owner and the delegate does not exist yet). A " +
                "presence count keeps the same number when a release is moved out of its finally — this " +
                "does not.");

            // ── The ct-drop's release is inside the ct-drop, not merely somewhere in the method ───────
            int ctDrop = pumpBody.IndexOf("if (pending.Ct.IsCancellationRequested)", StringComparison.Ordinal);
            Assert.GreaterOrEqual(ctDrop, 0, "PumpBuilds' parked drain must still check the entry's token");
            string ctDropBlock = ExtractBlockAfter(pumpBody, ctDrop, path, out _);
            Assert.AreEqual(1, CountOccurrences(ctDropBlock, "pending.Decode.Release()"),
                "…and the ct-drop must release inside its own block. The entry leaves the queue there and " +
                "reaches no dispatch, so its reference is released HERE or nowhere. (The runtime tooth for " +
                "this mouth is SymbolParkedRedecodeTests.AParkedEntryCancelledWithoutADrain_…; this clause " +
                "pins the placement that a cancellation-timing test cannot.)");

            Assert.AreEqual(1, CountOccurrences(funnelBody, "Decode.Release()"),
                "…and the discard funnel releases exactly once per dequeued entry (its runtime tooth lives " +
                "in SymbolParkedRedecodeTests; the clause is here so the mouths are pinned together).");

            // ── The park side: the acquire→enqueue window has a release that does not need a consumer ──
            // R1: this logic moved OFF RunWorkerAndHandoff and into TryParkBuild — the one gated site (see
            // TryParkBuildAndTheAbandonDrain_LockTheSameParkGate for the gate itself); this clause keeps its
            // original narrow claim, just re-anchored to where the acquire+enqueue now lives.
            string parkBuild = StripComments(
                ExtractMethodBody(source, "internal bool TryParkBuild(", path));
            int enqueue = parkBuild.IndexOf("_pendingSpriteQueue.Enqueue(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(enqueue, 0, "TryParkBuild must enqueue its entry");
            Assert.IsTrue(Regex.IsMatch(parkBuild.Substring(enqueue), @"catch\s*\{[^}]*decode\.Release\(\)"),
                "Acquire() increments BEFORE the enqueue, so an enqueue that throws leaves a reference no " +
                "queue entry and no consumer will ever own — the enclosing catch only logs. The enqueue must " +
                "sit in a try whose catch releases. Structural because the only way to make " +
                "ConcurrentQueue.Enqueue throw is to exhaust memory.");
        }

        /// <summary>
        /// R1 (decode-refcount plan §6, F1's structural companion): both mouths that touch the parked
        /// queue's ownership — <c>TryParkBuild</c>'s ct-check+acquire+enqueue and
        /// <c>DrainAndDiscardParkedBuilds</c>'s dequeue+dispose — must lock the SAME <c>_parkGate</c>,
        /// unconditionally, or the exclusion the runtime rendezvous tooth
        /// (<c>SymbolParkedRedecodeTests.AParkBlockedByTheAbandonDrainGate_…</c>) proves is decorative: that
        /// tooth only observes the PARK side of the exclusion (it asserts the park has not acquired while the
        /// gate is held) — a drain that silently stopped taking the gate would still leave it green, because
        /// nothing in that tooth exercises the drain's own critical section concurrently with a park. This
        /// pin is what covers the residual gap the plan (§6/F1) accepted rather than adding a second runtime
        /// tooth: forcing the two to race deterministically needs the same <c>internal</c> seam the
        /// maintainer declined (Option 2). A presence count would pass for <c>if (cond) lock (_parkGate)</c>,
        /// so this reuses <see cref="AssertUnconditionallyReachedOnce"/> exactly as every other funnel in
        /// this file does, rather than a bare occurrence count.
        ///
        /// <para><b>RED injection:</b> remove <c>lock (_parkGate)</c> from either method.</para>
        /// </summary>
        [Test]
        public void TryParkBuildAndTheAbandonDrain_LockTheSameParkGate()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            const string gatePattern = @"lock\s*\(\s*_parkGate\s*\)";

            string parkBody  = StripComments(ExtractMethodBody(source, "internal bool TryParkBuild(", path));
            string drainBody = StripComments(
                ExtractMethodBody(source, "private void DrainAndDiscardParkedBuilds(", path));

            AssertUnconditionallyReachedOnce(parkBody, gatePattern,
                "'lock (_parkGate)' in TryParkBuild",
                "the ct-check and the Acquire() must run behind the SAME gate the abandon-drain takes, or the " +
                "park can acquire in the window between a canceller's cancel and its drain — the reopened " +
                "mouth-5 window the gate exists to close.");
            AssertUnconditionallyReachedOnce(drainBody, gatePattern,
                "'lock (_parkGate)' in DrainAndDiscardParkedBuilds",
                "without this gate a park mid-acquire and a drain mid-dequeue can interleave arbitrarily — " +
                "the runtime rendezvous tooth only observes the PARK side of that exclusion, so this " +
                "structural pin is what covers the drain side.");
        }

        /// <summary>
        /// R1 fix (decode-refcount plan §3 R1): <c>KickMeshBuild</c> ACQUIRES its OWN reference in its
        /// main-thread prologue — it no longer borrows the caller's and transfers it into the pool lambda —
        /// so it must release exactly the token it acquired, from that lambda's <c>finally</c>, and NEITHER
        /// caller may null the record's field around the call any more.
        ///
        /// <para><b>This tooth inverted again, and the inversion is R1's whole point.</b> The D1 form of this
        /// tooth demanded the caller null <c>lt.Decode</c> AFTER the call — the transfer-completes-on-return
        /// contract. R1 retires the transfer altogether: the record keeps its own reference for its WHOLE
        /// in-cover lifetime now (see the <c>LoadedTile.Decode</c> field doc), and
        /// <c>RenderTeardownRecord</c> (funnel 1) is its only release, kicked or not. A surviving
        /// <c>lt.Decode = null;</c> at either kick call site is the retired transfer shape leaking back in —
        /// it would desync <c>FetchCompleted &amp;&amp; !HasMeshBuild &amp;&amp; Decode == null</c> from the
        /// kicked state and re-observe the record's PRESERVED fetch task on the next tick.</para>
        ///
        /// <para><b>RED injections:</b> re-add a caller-side <c>lt.Decode = null;</c> after either kick call;
        /// remove the prologue <c>decode.Acquire()</c> (a leak-balance regression the runtime
        /// <c>EagerDecodeOwnershipTests</c> teeth catch, not this one); or move <c>decode.Release()</c> out
        /// of the lambda's <c>finally</c>; or remove the guard <c>catch</c> around the pool hand-off (a leak
        /// on a synchronous <c>RunOnThreadPool</c> throw).</para>
        /// </summary>
        [Test]
        public void KickMeshBuild_AcquiresItsOwnReference_AndNeitherCallerNullsTheRecordsField()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source   = File.ReadAllText(path);
            string stripped = StripComments(source);
            string body     = StripComments(
                ExtractMethodBody(source, "UniTask<MeshBuildResult> KickMeshBuild(", path));

            Assert.AreEqual(1, CountOccurrences(body, "decode.Acquire()"),
                "KickMeshBuild must call 'decode.Acquire()' exactly once — its OWN reference, taken in the " +
                "main-thread prologue before the pool lambda exists. R1 retires the transfer: the record no " +
                "longer hands this reference over, so the kick must take a separate one of its own.");
            Assert.AreEqual(2, CountOccurrences(body, "decode.Release()"),
                "KickMeshBuild must contain EXACTLY TWO 'decode.Release()' — the pool lambda's `finally` " +
                "(success path) and the guard `catch` around the Acquire()->RunOnThreadPool hand-off (which " +
                "can throw synchronously — OOM — before the lambda runs). They are mutually exclusive by " +
                "control flow, so each reference is released exactly once; a THIRD would be a real double-free.");
            Assert.AreEqual(1, CountOccurrences(body, "finally"),
                "…the success release is a `finally`, not a trailing statement — the mesh pass can throw, and " +
                "a release it skips leaks the kick's own token on the fault path.");
            Assert.IsTrue(Regex.IsMatch(body, @"catch\s*\{\s*decode\.Release\(\);\s*throw;\s*\}"),
                "…and the SECOND release is a guard `catch { decode.Release(); throw; }` around the " +
                "Acquire()->RunOnThreadPool hand-off (mirrors TryParkBuild): a synchronous hand-off throw " +
                "frees the kick's own reference instead of leaking it. Remove the rethrow and it swallows the " +
                "fault; remove the release and it leaks (the lambda's finally never runs).");

            Assert.AreEqual(3, CountOccurrences(stripped, "KickMeshBuild("),
                "TileManager.cs must contain exactly three 'KickMeshBuild(' occurrences: the definition and " +
                "its two call sites. A third call site is a new owner-to-callee relationship these clauses " +
                "would not be checking.");
            Assert.AreEqual(2, CountOccurrences(stripped, "KickMeshBuild(lt, id, lt.Decode"),
                "…and BOTH call sites must feed the RECORD's field, never a bare local. A bare local is the " +
                "one owner no funnel can see: if the prologue throws, the local unwinds carrying the kick's " +
                "own reference before it was even acquired — this pins that the kick reads FROM the record's " +
                "field, R1's ownership question notwithstanding.");

            foreach (string callerAnchor in new[]
                     { "void DrainMeshBuilds(CameraProperties", "private int PumpPending(" })
            {
                string callerBody = StripComments(ExtractMethodBody(source, callerAnchor, path));
                int call = callerBody.IndexOf("KickMeshBuild(lt, id, lt.Decode", StringComparison.Ordinal);
                Assert.GreaterOrEqual(call, 0,
                    $"the caller anchored at '{callerAnchor}' must kick through lt.Decode");
                Assert.AreEqual(0, CountOccurrences(callerBody, "lt.Decode = null"),
                    $"'{callerAnchor}' must NOT null lt.Decode anywhere around its KickMeshBuild call — R1 " +
                    "retired the transfer: the record keeps its own reference for its whole in-cover " +
                    "lifetime, and RenderTeardownRecord (funnel 1) is the only release. A null-out here is " +
                    "the retired transfer shape leaking back in.");
            }
        }

        /// <summary>
        /// D1 fix: <c>RenderTeardownRecord</c> DISARMS before it fires — <c>lt.Decode</c> is nulled before
        /// <c>Release()</c> is called, not after.
        ///
        /// <para><b>Structural, and this one really is.</b> <c>Release()</c> throws on an unbalanced release
        /// and <c>IDecodedTile.Dispose()</c> can throw, so the order decides whether the local is left
        /// holding a handle whose reference is already gone. No runtime tooth can see it: all three callers
        /// (<c>SetSources</c>, <c>ReleaseTile</c>, <c>DoDispose</c>) pass a struct COPY and none writes it
        /// back on the throwing path, so the record inside <c>_loaded</c> is left armed under BOTH orderings
        /// — and <c>MethodInfo.Invoke</c> does not copy a by-ref argument back when the callee throws, so
        /// reflecting into the method directly cannot read the difference either (measured, not assumed).
        /// What is pinned here is the funnel's own discipline; the caller-side residue is recorded as an
        /// open finding rather than hidden behind a green count.</para>
        ///
        /// <para><b>RED injection:</b> swap the two statements back to
        /// <c>lt.Decode?.Release(); lt.Decode = null;</c>.</para>
        /// </summary>
        [Test]
        public void RenderTeardownRecord_NullsTheRecordsHandleBeforeItReleases()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string body = StripComments(ExtractMethodBody(
                File.ReadAllText(path), "private void RenderTeardownRecord(ref LoadedTile", path));

            Match disarm  = Regex.Match(body, @"lt\.Decode\s*=\s*null\s*;");
            Match release = Regex.Match(body, @"\.Release\(\)");
            Assert.IsTrue(disarm.Success,  "RenderTeardownRecord must null the record's decode handle");
            Assert.IsTrue(release.Success, "RenderTeardownRecord must release the record's decode handle");
            Assert.Less(disarm.Index, release.Index,
                "RenderTeardownRecord must NULL lt.Decode BEFORE releasing. R2: SharedDisposable's Release() " +
                "is undefended (a DEBUG assertion only, not a throw), but the tile's Dispose() can still " +
                "throw; with the null-out afterwards a throw there leaves the field armed with a reference " +
                "that is already released, so a retry releases it a second time — a silent double-release " +
                "rather than a loud fault. Same 'a transfer nulls the source' rule the mesh lifetime uses, " +
                "applied to the ORDER of the two effects and not just their presence.");
        }

        /// <summary>Extracts the brace-balanced body (inclusive of the outer braces) of the method whose
        /// definition contains <paramref name="signatureAnchor"/>, by scanning forward from the first '{'
        /// after the anchor and counting nesting depth. A simple, deliberately narrow tool for a
        /// call-form-narrow grep guard — not a C# parser.</summary>
        private static string ExtractMethodBody(string source, string signatureAnchor, string path)
        {
            int anchorIndex = source.IndexOf(signatureAnchor, StringComparison.Ordinal);
            Assert.GreaterOrEqual(anchorIndex, 0,
                $"expected to find the unique method-definition anchor '{signatureAnchor}' in {path}");

            return ExtractBlockAfter(source, anchorIndex, path, out _);
        }

        /// <summary>The brace-scanning half of <see cref="ExtractMethodBody"/>, shared so a LOOP body or a
        /// <c>try</c> block can be extracted exactly the way a method body is: the brace-balanced block
        /// (inclusive of its outer braces) beginning at the first '{' at or after
        /// <paramref name="searchFrom"/>. <paramref name="blockEndIndex"/> is the index of the block's
        /// closing brace in <paramref name="source"/> — the D0 ordering clause compares against it, since
        /// "before or after the loop" is an offset question, not a counting one.</summary>
        private static string ExtractBlockAfter(string source, int searchFrom, string path, out int blockEndIndex)
        {
            int braceStart = source.IndexOf('{', searchFrom);
            Assert.GreaterOrEqual(braceStart, 0,
                $"expected an opening brace at or after offset {searchFrom} in {path}");

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
            Assert.Less(i, source.Length, $"unbalanced braces scanning the block in {path}");

            blockEndIndex = i;
            return source.Substring(braceStart, i - braceStart + 1);
        }

        /// <summary>The paren-balanced argument list of the call whose <c>(</c> is the first at or after
        /// <paramref name="searchFrom"/> — the same tool as <see cref="ExtractBlockAfter"/>, one bracket
        /// kind over. It exists so a clause about ONE call site is scoped to that call site instead of to
        /// the whole enclosing method (arm 1 NIT 4: the token clause used to forbid
        /// <c>cancellationToken:</c> anywhere in <c>PumpBuilds</c> and would have tripped on an unrelated
        /// future dispatch).</summary>
        private static string ExtractParenAfter(string source, int searchFrom, string path)
        {
            int start = source.IndexOf('(', searchFrom);
            Assert.GreaterOrEqual(start, 0, $"expected an opening paren at or after offset {searchFrom} in {path}");

            int depth = 0;
            int i = start;
            for (; i < source.Length; i++)
            {
                if (source[i] == '(') depth++;
                else if (source[i] == ')')
                {
                    depth--;
                    if (depth == 0) break;
                }
            }
            Assert.Less(i, source.Length, $"unbalanced parens scanning the call in {path}");
            return source.Substring(start, i - start + 1);
        }

        /// <summary>Asserts that <paramref name="pattern"/> occurs in <paramref name="block"/> exactly once
        /// as an <b>unconditionally reached</b> statement — the property a funnel needs and that a bare
        /// occurrence count does not give (the D0 fix pass exists because four real bypasses passed the
        /// counts). Three conditions, all required:
        /// <list type="bullet">
        /// <item>brace depth 1 relative to <paramref name="block"/>'s own outer braces — so it is not nested
        /// inside an <c>if</c>/<c>try</c>/loop block of its own;</item>
        /// <item>it STARTS a statement (nearest preceding non-whitespace character is <c>;</c>, <c>{</c> or
        /// <c>}</c>) — which is what catches a BRACELESS guard, <c>if (c) Funnel();</c>, whose depth is
        /// still 1 and which the depth check alone therefore cannot see;</item>
        /// <item>no earlier <c>return</c>/<c>continue</c>/<c>break</c>/<c>goto</c> anywhere in the block can
        /// jump past it — arm 1's followUp 1, a <c>continue</c> that skips a record while keeping the call
        /// textually present.</item>
        /// </list>
        /// <paramref name="pattern"/> must match from the FIRST character of the statement, or the
        /// statement-start condition can never hold (e.g. use <c>\w+\s*\.\s*Foo\(\)</c>, not
        /// <c>\.Foo\(\)</c>).
        ///
        /// <para><b>What it cannot prove, said plainly rather than implied by silence.</b> This is a text
        /// shape, not a control-flow proof. An exception thrown by an earlier statement, a jump inside a
        /// nested block, a brace inside a string literal, and a funnel body that does nothing at all are all
        /// invisible to it — the last of those is why the discard funnel gets a separate body clause. It
        /// pins that the call SITE cannot be bypassed by the three edits above; it says nothing about
        /// whether the per-record obligation inside the funnel is honoured, which is D1's runtime leak teeth
        /// to establish.</para></summary>
        private static void AssertUnconditionallyReachedOnce(string block, string pattern, string what, string why)
        {
            int textual = Regex.Matches(block, pattern).Count;
            int reached = 0;
            foreach (Match match in Regex.Matches(block, pattern))
                if (IsUnconditionallyReached(block, match.Index)) reached++;

            Assert.AreEqual(1, reached,
                $"{what} must be reached UNCONDITIONALLY exactly once — brace depth 1 in its own block, at " +
                $"the start of a statement, with no earlier return/continue/break/goto. {why} " +
                $"(textual occurrences: {textual}; of those, unconditionally reached: {reached}. Two " +
                "different numbers mean the call is PRESENT but guarded, nested or jumped over — the " +
                "bypass class this clause was added to catch.)");
        }

        /// <summary>The three conditions <see cref="AssertUnconditionallyReachedOnce"/> documents, applied
        /// to one match position.</summary>
        private static bool IsUnconditionallyReached(string block, int index)
        {
            int depth = 0;
            for (int i = 0; i < index; i++)
            {
                if (block[i] == '{') depth++;
                else if (block[i] == '}') depth--;
            }
            if (depth != 1) return false;

            int preceding = index - 1;
            while (preceding >= 0 && char.IsWhiteSpace(block[preceding])) preceding--;
            if (preceding < 0) return false;
            char c = block[preceding];
            if (c != ';' && c != '{' && c != '}') return false;

            return !Regex.IsMatch(block.Substring(0, index), @"\b(return|continue|break|goto)\b");
        }

        private static int CountOccurrences(string text, string callForm)
            => Regex.Matches(text, Regex.Escape(callForm)).Count;

        /// <summary><see cref="StripLineComments"/> plus block comments — the anti-vacuity clause in
        /// <see cref="WriteIntoPath_ReferencesNoMvtCarrierTypes"/> must not be satisfiable by prose (B3's
        /// finding: the old clause passed on an XML <c>see cref</c> after the real call was gone). Additive,
        /// deliberately: <see cref="StripLineComments"/> is shared by five other assertions and is not
        /// modified. Measured at the time of writing: neither target file contains a block comment, so the
        /// block-comment half is defence against the NEXT instance rather than the current one — which is why
        /// it carries its own RED row rather than being assumed armed. Inherits
        /// <see cref="StripLineComments"/>' known narrowness: a <c>"http://…"</c> literal eats the rest of
        /// its line.</summary>
        private static string StripComments(string text)
            => StripLineComments(Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline));

        /// <summary>Whole-identifier presence. The <c>\b</c> anchors make <c>MvtGeometry</c> NOT match inside
        /// <c>MvtGeometryMaterializer</c> — the substring collision that disarmed the previous clause.</summary>
        private static bool ContainsIdentifier(string text, string identifier)
            => Regex.IsMatch(text, $@"\b{Regex.Escape(identifier)}\b");

        /// <summary>Strips everything from <c>//</c> (which also covers doc-comment <c>///</c>) to the end
        /// of each line — a narrow guard against the assembly-wide decode-site scans
        /// (<see cref="MapRendererUnity_DecodesMvtNowhere"/>/<see cref="MapRendererJobs_DecodesMvtOnlyInsideMvtTileDecoder"/>)
        /// false-counting a PROSE mention of the call form (e.g. a doc comment naming
        /// <c>MvtDecoder.Decode(</c>) as a real call site. Deliberately narrow (no block-comment or
        /// string-literal awareness) — matches this file's existing "call-form-narrow grep guard, not a C#
        /// parser" scope.</summary>
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
