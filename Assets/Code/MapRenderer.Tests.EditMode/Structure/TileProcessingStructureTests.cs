// Unity EditMode only — reads source files under Application.dataPath. NOT registered in core-tests.csproj.

using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// Epic A / A1 (docs/per-layer-tile-processing-a1-plan.md "A1's falsifiable acceptance tooth", #1): a
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
        private const string GetOrDecodeCallForm = ".GetOrDecode(";
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

        /// <summary>Epic A / A4 (design §B Q1-Q3, plan §E-3): re-scoped again — A4 moves the decode itself
        /// out of the runner and into the caller-owned <see cref="MapRenderer.Unity.Rendering.Tile.Processing.SharedTileDecode"/>,
        /// so <c>RunWorkerPass</c> no longer decodes for itself at all; it reads the shared entry exactly
        /// once via <c>.GetOrDecode(</c>. The invariant this test has guarded since A1/A3 — the mesh
        /// cadence's OWN decode-once boundary — survives as "reads the shared decode exactly once, decodes
        /// nothing directly". The source-less pass still never touches either call form (unchanged from
        /// A2).</summary>
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
                $"RunWorkerPass must contain ZERO direct '{DecodeCallForm}' calls — A4 moved the decode " +
                "into SharedTileDecode.GetOrDecode; the runner only reads it now.");
            Assert.AreEqual(1, CountOccurrences(workerPassBody, GetOrDecodeCallForm),
                $"RunWorkerPass must call '{GetOrDecodeCallForm}' exactly once — the mesh worker pass's own " +
                "read of the shared decode.");
            Assert.AreEqual(0, CountOccurrences(sourcelessBody, DecodeCallForm),
                $"RunSourcelessWorkerPass must contain ZERO '{DecodeCallForm}' calls — the source-less arm " +
                "never decodes MVT bytes (unchanged from A2).");
            Assert.AreEqual(0, CountOccurrences(sourcelessBody, GetOrDecodeCallForm),
                $"RunSourcelessWorkerPass must contain ZERO '{GetOrDecodeCallForm}' calls — it has no bytes " +
                "and no shared decode to read (unchanged from A2).");
        }

        /// <summary>Epic A / A3 (plan §F tooth 1 — primary structural delegation tooth, RED-verified
        /// against the pre-A3 <c>SymbolLabelSubsystem</c>): the subsystem no longer decodes, extracts, or
        /// shapes labels itself — that machinery moved into the processor contract. Against the un-rewired
        /// source (decode at <c>BuildTileAsync</c>'s old <c>:324</c>, <c>ExtractLayers</c> at <c>:325</c>,
        /// <c>ShapeAsync</c> at <c>:333</c>, no runner call) this test FAILS on all four forms — adding the
        /// interface/processor as an unused façade while <c>BuildTileAsync</c> keeps its inline decode/
        /// extract/shape (the "rename" non-implementation) cannot pass.</summary>
        [Test]
        public void SymbolSubsystem_DelegatesDecodeExtractAndShapeToTheProcessorMachinery()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolLabelSubsystem.cs");
            Assert.IsTrue(File.Exists(path), $"expected source file to exist at {path}");
            string source = File.ReadAllText(path);

            const string extractLayersCallForm = ".ExtractLayers(";
            const string shapeAsyncCallForm    = ".ShapeAsync(";
            const string runSymbolCallForm     = "TileLayerProcessorRunner.RunSymbolWorkerPass(";

            Assert.AreEqual(0, CountOccurrences(source, DecodeCallForm),
                $"SymbolLabelSubsystem.cs must contain ZERO direct '{DecodeCallForm}' call sites — the " +
                "symbol decode now lives in TileLayerProcessorRunner.RunSymbolWorkerPass (Epic A / A3).");
            Assert.AreEqual(0, CountOccurrences(source, extractLayersCallForm),
                $"SymbolLabelSubsystem.cs must contain ZERO direct '{extractLayersCallForm}' call sites — " +
                "per-layer extraction now happens inside TileSymbolLayerProcessor.ProcessOnWorker.");
            Assert.AreEqual(0, CountOccurrences(source, shapeAsyncCallForm),
                $"SymbolLabelSubsystem.cs must contain ZERO direct '{shapeAsyncCallForm}' call sites — " +
                "per-layer shaping now happens inside TileSymbolLayerProcessor.CompleteOnMainAsync.");
            Assert.AreEqual(1, CountOccurrences(source, runSymbolCallForm),
                $"SymbolLabelSubsystem.cs must call '{runSymbolCallForm}' exactly once — the single " +
                "decode-once fan-out point for the symbol worker pass.");
        }

        /// <summary>Epic A / A6 (plan §E-13 — re-scoped again from A4's form, GENUINELY RED against
        /// pre-A6 source): A6 moves the sole production <c>MvtDecoder.Decode(</c> call OUT of
        /// <c>MapRenderer.Unity</c> entirely — into <c>MvtTileDecoder</c> in Core (<c>Tiles/ITileDecoder.cs</c>).
        /// After A6 the assembly-wide count under <c>MapRenderer.Unity</c> (including
        /// <c>SharedTileDecode.cs</c>, A4's sole permitted file) is ZERO — the decode left the assembly, it
        /// did not just move within it. See <see cref="MapRendererCore_DecodesMvtOnlyInsideMvtTileDecoder"/>
        /// for the corresponding Core-side positive assertion (together the two prove the sole-production-
        /// decode-site invariant across the assembly move — plan §E-13 option (b)).</summary>
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

            // SharedTileDecode.cs specifically: it reads through the injected ITileDecoder now, not
            // MvtDecoder directly — the A4→A6 handoff this test's name change records.
            string sharedTileDecodePath = Path.Combine(
                root, "Rendering", "Tile", "Processing", "SharedTileDecode.cs");
            Assert.IsTrue(File.Exists(sharedTileDecodePath), $"expected source file to exist at {sharedTileDecodePath}");
            string sharedTileDecodeSource = StripLineComments(File.ReadAllText(sharedTileDecodePath));
            Assert.AreEqual(0, CountOccurrences(sharedTileDecodeSource, DecodeCallForm),
                $"SharedTileDecode.cs must contain ZERO '{DecodeCallForm}' call sites post-A6.");
            Assert.AreEqual(1, CountOccurrences(sharedTileDecodeSource, "_decoder.Decode("),
                "SharedTileDecode.cs must call '_decoder.Decode(' exactly once — the injected ITileDecoder " +
                "is now the sole read (Epic A / A6).");
        }

        /// <summary>Epic A / A6 (plan §E-13 — the Core-side positive half of the sole-decode-site proof):
        /// the ONE production <c>MvtDecoder.Decode(</c> call site now lives in <c>Tiles/ITileDecoder.cs</c>
        /// (<c>MvtTileDecoder.Decode</c>), and nowhere else under <c>MapRenderer.Core</c>. The positive count
        /// (not just a zero-elsewhere scan) proves the decode actually landed there, not merely that
        /// <c>SharedTileDecode.cs</c> went quiet. Widening this scan to <c>Assets/Code</c> instead of Core
        /// specifically would sweep the TEST assembly too, where <c>DecodeTests</c>/<c>MvtPropertyDecodeTests</c>
        /// legitimately call <c>MvtDecoder.Decode(</c> for fixture setup — so the two per-assembly
        /// assertions (this one + <see cref="MapRendererUnity_DecodesMvtNowhere"/>) are deliberately narrower
        /// than one combined scan. RED pre-A6 (<c>SharedTileDecode</c>, in Unity, decodes; no
        /// <c>Tiles/ITileDecoder.cs</c> exists yet).</summary>
        [Test]
        public void MapRendererCore_DecodesMvtOnlyInsideMvtTileDecoder()
        {
            string root = Path.Combine(Application.dataPath, "Code", "MapRenderer.Core");
            Assert.IsTrue(Directory.Exists(root), $"expected directory to exist at {root}");

            string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            Assert.Greater(files.Length, 0, $"expected at least one .cs file under {root}");

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
                $"only Tiles/ITileDecoder.cs may call '{DecodeCallForm}' under MapRenderer.Core; " +
                $"found it in: {string.Join(", ", offenders)}");
            Assert.AreEqual(1, tileDecoderCount,
                $"Tiles/ITileDecoder.cs must call '{DecodeCallForm}' exactly once — MvtTileDecoder.Decode, " +
                "the sole production decode call site after Epic A / A6.");
        }

        /// <summary>Epic A / A6 (plan §E-13, F-1 — WriteInto-path neutralization, structural). The
        /// fill/line/symbol fan-out — from feature selection through mesh/label build — references NO MVT
        /// carrier type (<c>MvtTile</c>/<c>MvtFeature</c>/<c>MvtLayer</c>/<c>MvtDecoder</c>) by name; only the
        /// neutral <c>IDecodedTile</c>/<c>ITileLayer</c>/<c>ITileFeature</c>/<c>ITileDecoder</c> surface (§B-1,
        /// §B-3). Anti-vacuity: <c>MvtGeometry</c> — the shared geometry-COMMAND codec, NOT a tile-decode
        /// carrier (§B) — must still be referenced at the line/symbol codec sites, so a file that was simply
        /// gutted (losing its geometry decode along with the MVT carrier types) does not vacuously pass.
        /// Genuinely RED pre-A6: every one of these 15 files names an MVT carrier type today.</summary>
        [Test]
        public void WriteIntoPath_ReferencesNoMvtCarrierTypes()
        {
            string unityRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity");
            string coreRoot  = Path.Combine(Application.dataPath, "Code", "MapRenderer.Core");

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
                (unityRoot, Path.Combine("Rendering", "Tile", "Processing", "SharedTileDecode.cs")),
                (coreRoot,  Path.Combine("Filters", "FeatureSelector.cs")),
                (coreRoot,  Path.Combine("Style", "SourceLayerResolver.cs")),
                (coreRoot,  Path.Combine("Style", "Symbol", "SymbolFeatureExtractor.cs")),
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
                "MvtLayer/MvtDecoder) — only the neutral IDecodedTile/ITileLayer/ITileFeature/ITileDecoder " +
                $"surface (Epic A / A6). Offenders: {string.Join(", ", offenders)}");

            // Anti-vacuity: MvtGeometry (the geometry-COMMAND codec, format-shared and NOT MVT-tile-coupled)
            // must still be present at the line/symbol codec sites.
            string lineBuilderText = File.ReadAllText(
                Path.Combine(unityRoot, "Rendering", "Meshing", "StyledLineTileBuilder.cs"));
            string symbolExtractorText = File.ReadAllText(
                Path.Combine(coreRoot, "Style", "Symbol", "SymbolFeatureExtractor.cs"));
            Assert.Greater(CountOccurrences(lineBuilderText, "MvtGeometry"), 0,
                "StyledLineTileBuilder.cs must still reference MvtGeometry — only the tile-decode is " +
                "neutralized, the geometry-command codec is retained (plan §D).");
            Assert.Greater(CountOccurrences(symbolExtractorText, "MvtGeometry"), 0,
                "SymbolFeatureExtractor.cs must still reference MvtGeometry — only the tile-decode is " +
                "neutralized, the geometry-command codec is retained (plan §D).");
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
                $"RunSymbolWorkerPass must contain ZERO direct '{DecodeCallForm}' calls — A4 moved the " +
                "decode into SharedTileDecode.GetOrDecode; the deletion target named by the A4 handoff note.");
            Assert.AreEqual(1, CountOccurrences(body, GetOrDecodeCallForm),
                $"RunSymbolWorkerPass must call '{GetOrDecodeCallForm}' exactly once — the symbol cadence's " +
                "own read of the shared decode.");
        }

        /// <summary>Epic A / A7 (docs/per-layer-tile-processing-a7-plan.md §F tooth 1 — the primary
        /// structural tooth): <c>TileManager</c> must be byte-agnostic — it names none of the five
        /// byte-centric tokens the pre-raise coordinator used (<c>TileResponse</c>/<c>IDataSource</c>/
        /// <c>TileScheduler</c>/<c>SharedTileDecode</c>/standalone <c>TileCache</c>), and DOES name the raised
        /// seam (<c>ITileFeatureSource</c>/<c>IDecodedTileHandle</c>). Genuinely RED pre-A7: the file named
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
                "TileManager.cs must contain ZERO 'SharedTileDecode' occurrences — the coordinator holds only " +
                "the polymorphic IDecodedTileHandle now, never the concrete MVT handle type.");

            int standaloneTileCacheCount = Regex.Matches(source, @"\bTileCache\b").Count;
            Assert.AreEqual(0, standaloneTileCacheCount,
                "TileManager.cs must contain ZERO STANDALONE 'TileCache' occurrences (word-boundary match, " +
                "excluding the 19 kept 'PreparedTileCache' references, which plan §D leaves untouched) — the " +
                "byte-level LRU cache now lives inside MvtTileFeatureSource.");

            Assert.Greater(CountOccurrences(source, "ITileFeatureSource"), 0,
                "TileManager.cs must reference ITileFeatureSource — the raised seam it now holds instead of " +
                "IDataSource/TileScheduler.");
            Assert.Greater(CountOccurrences(source, "IDecodedTileHandle"), 0,
                "TileManager.cs must reference IDecodedTileHandle — the polymorphic decode-provisioning " +
                "handle it now threads through the mesh/symbol kick instead of SharedTileDecode.");
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

        /// <summary>Extracts the brace-balanced body (inclusive of the outer braces) of the method whose
        /// definition contains <paramref name="signatureAnchor"/>, by scanning forward from the first '{'
        /// after the anchor and counting nesting depth. A simple, deliberately narrow tool for a
        /// call-form-narrow grep guard — not a C# parser.</summary>
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

        /// <summary>Strips everything from <c>//</c> (which also covers doc-comment <c>///</c>) to the end
        /// of each line — a narrow guard against the assembly-wide decode-site scans
        /// (<see cref="MapRendererUnity_DecodesMvtNowhere"/>/<see cref="MapRendererCore_DecodesMvtOnlyInsideMvtTileDecoder"/>)
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
