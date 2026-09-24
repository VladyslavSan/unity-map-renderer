// Structure/StructureTests.cs — geometry/mesh-graph/job-scheduling structural fences. Unity EditMode only —
// reads source files under Application.dataPath. NOT registered in core-tests.csproj.
//
// Contents:
//   NeutralGeometryPathTests                — the neutral geometry path carries no format, no sidecar can come back.
//   TileGeometryBuffersOwnershipTests       — the MVT decode/materialize seam's native buffer ownership.
//   SymbolExtractorStructureTests           — SymbolFeatureExtractor mints/disposes exactly the buffers it owns.
//   StyledLineBuilderStructureTests         — the line builder never frees geometry it only borrows.
//   TileProcessingStructureTests            — TileManager.KickMeshBuild does not decode/dispatch directly.
//   FillMeshGraphStructureTests             — FillMeshGraph's source shape: no Complete/stale .AsArray()/.Run/IWorkScheduler.
//   FillMeshPipelineRetirementFenceTests    — retired fill-pipeline symbols never regrow a caller.
//   WallChainCallerFenceTests               — the wall-job chain cannot re-inline into the extrusion prologue.
//   ProjectPointsJobConstructionFenceTests  — only its declaration + scheduler may construct ProjectPointsJob.
//   TileBuildGraphKindNeutralityTests       — TileBuildGraph retains no per-kind (fill/line/extrusion) knowledge.
//   RenderModeMeshingFenceTests             — mesh preparation never branches on render mode (Lit vs Unlit).
//   GlobeFillVertexKeySizeTests             — GlobeFillVertexKey is hand-enumerated field by field; a new GlobeFillVertex column is not picked up automatically.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Geometry;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Tile.Processing;
using System.Text.RegularExpressions;
using System.Linq;
using MapRenderer.Unity.Rendering.Tile;
using Unity.Jobs.LowLevel.Unsafe;
using RenderMode = MapRenderer.Unity.Rendering.Materials.RenderMode;
using Unity.Collections.LowLevel.Unsafe;
using MapRenderer.Jobs.Fill;

namespace MapRenderer.Tests.Structure
{

    // ───────────────────────────────────────────────────────────────────────────────────
    // NeutralGeometryPathTests — the neutral geometry path carries no format, no sidecar can come back
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The neutral geometry path carries no format, and no sidecar can come back. Format-named types
    /// (<c>Mvt</c>/<c>GeoJson</c>/<c>Mlt</c>) are fenced by LOCATION to decoder folders, not by an allow-list;
    /// no other type names one in a signature; the neutral surfaces expose no command-stream member; and
    /// <c>ITileLayerProcessor.ProcessOnWorker</c> keeps exactly two parameters.
    /// </summary>
    [TestFixture]
    public class NeutralGeometryPathTests
    {
        /// <summary>The three production assemblies, reached through one type each rather than by name — a
        /// typo in an assembly-name string would silently scan nothing.</summary>
        private static Assembly[] ProductionAssemblies => new[]
        {
            typeof(IFeature).Assembly,             // MapRenderer.Core
            typeof(MvtFeature).Assembly,           // MapRenderer.Jobs
            typeof(ITileFeatureSource).Assembly,   // MapRenderer.Unity
        };

        /// <summary>A production type name that announces a wire format. Matched at the START of the name so
        /// an unrelated word ending in one of these cannot be a false positive.</summary>
        private static bool IsFormatNamed(Type t)
            => t != null && (t.Name.StartsWith("Mvt", StringComparison.Ordinal)
                          || t.Name.StartsWith("GeoJson", StringComparison.Ordinal)
                          || t.Name.StartsWith("Mlt", StringComparison.Ordinal));

        /// <summary>The FOLDERS a format-named production type is allowed to live in — the decoders and their
        /// producers. Defined as source-tree locations, so a new MVT-named type anywhere else fails without
        /// anyone having to remember to update a list of names.</summary>
        private static readonly string[] DecoderFolders =
        {
            Path.Combine("Code", "MapRenderer.Jobs", "Mvt"),
            Path.Combine("Code", "MapRenderer.Jobs", "Tiles"),
            Path.Combine("Code", "MapRenderer.Core", "GeoJson"),
        };

        /// <summary>Individual decoder/producer files that sit beside, not inside, a decoder folder.</summary>
        private static readonly string[] DecoderFiles =
        {
            Path.Combine("Code", "MapRenderer.Jobs", "MvtDecodeJob.cs"),
            Path.Combine("Code", "MapRenderer.Jobs", "MvtGeometryMaterializer.cs"),
            // The MVT *source* implementation: it resolves the MVT decoder for a fetch and is by definition
            // format-specific — the polymorphic seam it satisfies (ITileFeatureSource) is not.
            Path.Combine("Code", "MapRenderer.Unity", "Rendering", "Tile", "Processing", "MvtTileFeatureSource.cs"),
            // The GeoJSON *source* lives in MapRenderer.Unity: ITileFeatureSource returns a UniTask, which
            // MapRenderer.Jobs does not reference, so a decoder folder is not available to it.
            Path.Combine("Code", "MapRenderer.Unity", "Rendering", "Tile", "Processing", "GeoJsonTileFeatureSource.cs"),
        };

        // ── Tooth A ───────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void EveryFormatNamedProductionType_IsDeclaredInsideADecoderFolder()
        {
            Dictionary<string, string> declarationFile = SourceDeclarationIndex();

            var offenders = new List<string>();
            var inFolder  = new List<string>();
            int scanned   = 0;

            foreach (Assembly assembly in ProductionAssemblies)
                foreach (Type t in assembly.GetTypes())
                {
                    scanned++;
                    if (!IsFormatNamed(t)) continue;
                    if (t.IsNested) continue; // judged by its declaring type, which the scan already sees

                    if (!declarationFile.TryGetValue(t.Name, out string relativePath))
                    {
                        offenders.Add($"{t.FullName} (no source declaration found — compiler-generated?)");
                        continue;
                    }

                    if (IsUnderDecoderLocation(relativePath)) inFolder.Add($"{t.Name} → {relativePath}");
                    else offenders.Add($"{t.FullName} declared at {relativePath}");
                }

            // Non-vacuity: the scan walked a real corpus and the matcher fires on real names; an empty load
            // or a dead matcher would also report "no offenders".
            Assert.Greater(scanned, 200, "precondition: the scan must visit a real corpus of production types");
            Assert.IsNotEmpty(inFolder,
                "precondition: the matcher must find format-named types INSIDE the decoder folders — if it " +
                "finds none, it is matching nothing and clause 1 asserts nothing");

            Assert.IsEmpty(offenders,
                "every production type whose name announces a wire format must be declared under a decoder " +
                "folder (MapRenderer.Jobs/Mvt, MapRenderer.Jobs/Tiles, MapRenderer.Core/GeoJson, or the two " +
                "named producer files). Fenced by LOCATION, not by an allow-list of names: an allow-list is " +
                "what rotted, because a new format-named type was simply added to it. " +
                $"Offenders: {string.Join(", ", offenders)}");
        }

        [Test]
        public void NoProductionTypeOutsideTheDecoderFolders_HasAFormatNamedTypeInAMemberSignature()
        {
            Dictionary<string, string> declarationFile = SourceDeclarationIndex();

            var offenders = new List<string>();
            int membersScanned = 0;

            foreach (Assembly assembly in ProductionAssemblies)
                foreach (Type t in assembly.GetTypes())
                {
                    if (t.IsNested) continue;
                    if (declarationFile.TryGetValue(t.Name, out string path) && IsUnderDecoderLocation(path))
                        continue; // a decoder may name its own format freely

                    foreach (MemberInfo member in t.GetMembers(
                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        membersScanned++;
                        foreach (Type signatureType in SignatureTypes(member))
                            if (IsFormatNamed(signatureType))
                                offenders.Add($"{t.FullName}.{member.Name} : {signatureType.Name}");
                    }
                }

            Assert.Greater(membersScanned, 1000,
                "precondition: the member scan must visit a real corpus, or 'no offenders' means nothing");

            Assert.IsEmpty(offenders,
                "no production type outside the decoder folders may name a format type in a member " +
                "signature. This is what the earlier tooth CLAIMED and did not deliver: its tooth asserted an interface was " +
                "empty, which a sidecar interface satisfies trivially. " +
                $"Offenders: {string.Join(", ", offenders)}");
        }

        [Test]
        public void TheNeutralSurfaces_ExposeNoCommandStream()
        {
            // Non-vacuity FIRST: the surfaces are the real ones, not empty or wrong types. Every member the
            // design says survives must be here, or the absence assertions below assert about nothing.
            foreach (string surviving in new[]
                     { nameof(IFeature.GeometryType), nameof(IFeature.Id),
                       nameof(IFeature.TryGetProperty), nameof(IFeature.Properties) })
                Assert.IsNotEmpty(
                    typeof(IFeature).GetMember(surviving, BindingFlags.Public | BindingFlags.Instance),
                    $"'{surviving}' must be on IFeature — it is the kind gate and filter surface every " +
                    "consumer reads. Without it this fixture would be asserting over an empty type.");
            Assert.IsNotEmpty(typeof(ITileLayer).GetMember(nameof(ITileLayer.Geometry)),
                "precondition: ITileLayer must expose Geometry — the whole point is that the LAYER " +
                "owns its coordinates, so a scan that could not see it is scanning the wrong type");

            var offenders = new List<string>();
            foreach (Type surface in new[]
                     { typeof(IFeature), typeof(ITileLayer), typeof(IDecodedTile), typeof(TileGeometryBuffers) })
                foreach (MemberInfo member in surface.GetMembers(
                             BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    foreach (Type t in SignatureTypes(member))
                    {
                        // An array of an integral primitive IS an encoded command stream, whatever it is
                        // called. bool/char/float/double are excluded: none can carry MVT command words.
                        if (t.IsArray && t.GetElementType() is { IsPrimitive: true } element
                            && element != typeof(bool) && element != typeof(char)
                            && element != typeof(float) && element != typeof(double))
                            offenders.Add($"{surface.Name}.{member.Name} : {t.Name}");
                    }

            Assert.IsEmpty(offenders,
                "the neutral geometry path — IFeature, ITileLayer, IDecodedTile, TileGeometryBuffers — must " +
                "expose NO integral-array member. Such a member is a format's wire encoding leaking onto the " +
                "neutral surface, which is what forced every other source format to transcode into MVT " +
                $"command streams just to be a feature. Offenders: {string.Join(", ", offenders)}");
        }

        /// <summary>The zero-member <c>ITileFeature</c> and the sidecar
        /// <c>IMvtGeometryCarrier</c> are both deleted, and neither may come back. A tooth on the DELETION,
        /// not on emptiness — "this interface declares nothing" is a test that the interface should not
        /// exist, so the honest form is to assert it does not.</summary>
        [Test]
        public void TheRetiredFeatureSidecars_AreGone()
        {
            var found = new List<string>();
            int scanned = 0;
            bool sawATileInterface = false;

            foreach (Assembly assembly in ProductionAssemblies)
                foreach (Type t in assembly.GetTypes())
                {
                    scanned++;
                    // Exact names, not substrings: ITileFeatureSource is a live and unrelated seam.
                    if (t.Name == "ITileFeature" || t.Name == "IMvtGeometryCarrier")
                        found.Add($"{t.FullName} in {assembly.GetName().Name}");
                    if (t == typeof(ITileLayer)) sawATileInterface = true;
                }

            Assert.Greater(scanned, 200, "precondition: the scan must visit a real corpus of production types");
            Assert.IsTrue(sawATileInterface,
                "precondition: the scan must be able to see ITileLayer — proof the matcher looks at real " +
                "tile-surface types and not at an empty type list");

            Assert.IsEmpty(found,
                "no production assembly may declare 'ITileFeature' (zero members — a name for a role, not a " +
                "contract) or 'IMvtGeometryCarrier' (the sidecar that let a wire encoding ride alongside the " +
                $"neutral surface). Found: {string.Join(", ", found)}");
        }

        // ── Tooth F ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>Tooth F: the detached sidecar cannot come back by accretion. Deliberately
        /// thin — its whole job is to make "just thread one more thing through the worker seam" cost a
        /// visible test edit, because that accretion is exactly how <c>TileGeometryStore</c>'s mispairing
        /// hazard got in.</summary>
        [Test]
        public void ProcessOnWorker_HasExactlyTwoParameters_TheTileAndItsContext()
        {
            Type processorInterface = typeof(ITileFeatureSource).Assembly
                .GetType("MapRenderer.Unity.Rendering.Tile.Processing.ITileLayerProcessor", throwOnError: true);

            MethodInfo process = processorInterface.GetMethod(
                "ProcessOnWorker", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(process, "precondition: ITileLayerProcessor must declare ProcessOnWorker");

            ParameterInfo[] parameters = process.GetParameters();
            Assert.AreEqual(2, parameters.Length,
                "ProcessOnWorker takes exactly (IDecodedTile, in TileLayerProcessContext). A third " +
                "parameter — a pass-scoped TileGeometryStore — was the detached sidecar that was removed: " +
                "geometry belongs to the LAYER now, so a processor has nothing extra to be handed. A third " +
                "parameter of ANY type re-opens that shape. Found: " +
                string.Join(", ", System.Array.ConvertAll(parameters, p => $"{p.ParameterType.Name} {p.Name}")));

            Assert.AreEqual(typeof(IDecodedTile), parameters[0].ParameterType,
                "the first parameter is the decoded tile itself");
            Assert.IsTrue(parameters[1].ParameterType.IsByRef,
                "the second is the process context, passed `in` (a readonly struct, per the conventions)");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>Maps a top-level type NAME to the repo-relative path of the file that declares it, by
        /// scanning the three production source trees. Source-scanned rather than reflected because .NET
        /// exposes no declaration path, and location is the fence.</summary>
        private static Dictionary<string, string> SourceDeclarationIndex()
        {
            var index = new Dictionary<string, string>();
            string codeRoot = Path.Combine(Application.dataPath, "Code");
            DirectoryAssert.Exists(codeRoot);

            foreach (string assemblyDir in new[] { "MapRenderer.Core", "MapRenderer.Jobs", "MapRenderer.Unity" })
            {
                string root = Path.Combine(codeRoot, assemblyDir);
                DirectoryAssert.Exists(root);
                foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    string text = File.ReadAllText(file);
                    string relative = file.Substring(Application.dataPath.Length).TrimStart('/', '\\');
                    foreach (System.Text.RegularExpressions.Match m in
                             System.Text.RegularExpressions.Regex.Matches(
                                 text, @"^\s*(?:public|internal)\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+|readonly\s+|unsafe\s+)*(?:class|struct|interface|enum)\s+(\w+)",
                                 System.Text.RegularExpressions.RegexOptions.Multiline))
                        index[m.Groups[1].Value] = relative;
                }
            }

            Assert.Greater(index.Count, 200,
                "precondition: the source index must find a real corpus of declarations, or the location " +
                "fence resolves nothing and reports no offenders");
            return index;
        }

        private static bool IsUnderDecoderLocation(string relativePath)
        {
            foreach (string folder in DecoderFolders)
                if (relativePath.Replace('\\', '/').Contains(folder.Replace('\\', '/') + "/")) return true;
            foreach (string file in DecoderFiles)
                if (relativePath.Replace('\\', '/').EndsWith(file.Replace('\\', '/'), StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>Every type that appears in a member's signature — property type, field type, method
        /// return and parameter types. A member hides an encoding just as well behind a parameter as behind
        /// a return value.</summary>
        private static IEnumerable<Type> SignatureTypes(MemberInfo member)
        {
            switch (member)
            {
                case PropertyInfo p:
                    yield return p.PropertyType;
                    break;
                case FieldInfo f:
                    yield return f.FieldType;
                    break;
                case MethodBase m:
                    if (m is MethodInfo mi) yield return mi.ReturnType;
                    foreach (ParameterInfo parameter in m.GetParameters())
                        yield return parameter.ParameterType;
                    break;
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileGeometryBuffersOwnershipTests — the MVT decode/materialize seam's native buffer ownership
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Source-text disposal pins for the MVT decode/materialize seam: <c>Materialize</c> mints once and frees
    /// only on its throw path; <c>DecodeLayer</c> frees its command/tag buffers once on every exit, with
    /// null-on-transfer for adopted ones; <c>FlattenFeatureColumn</c> publishes each <c>ref</c> output before
    /// it can throw. Limitation: a leak in <c>Allocator.Persistent</c> worker memory is invisible to behaviour.
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
            // The single geometry.Dispose() belongs to the EnsureCapacity throw path; on the success path it
            // would return a freed buffer, so the count alone cannot carry this claim.
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

        /// <summary><c>commands</c>/<c>featOffsets</c>/<c>featLengths</c> are BORROWED constructor inputs that
        /// <c>MvtDecoder.DecodeLayer</c> frees
        /// (<see cref="DecodeLayerFreesTheMvtCommandBuffersItBuilds_ExactlyOnceOnEveryExitPath"/>). A dispose
        /// here would fault a second <c>Materialize()</c> call and double-free. <c>ringCountArr</c>/
        /// <c>vertCountArr</c> are <c>Materialize</c>'s own scratch.</summary>
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

        /// <summary><c>MvtDecoder.DecodeLayer</c> frees each <c>Allocator.Persistent</c> command and tag buffer it
        /// flattens exactly once on EVERY exit path, including a malformed-tile throw. <c>tagWords</c> transfers to
        /// the layer and is nulled, so its <c>finally</c> dispose is a no-op. Both <c>FlattenFeatureColumn</c>
        /// calls pass <c>ref</c> outputs between <c>try {</c> and <c>finally {</c>; the helper-body half is
        /// <see cref="FlattenFeatureColumnPublishesEachRefOutputBeforeItCanThrow"/>.</summary>
        [Test]
        public void DecodeLayerFreesTheMvtCommandBuffersItBuilds_ExactlyOnceOnEveryExitPath()
        {
            string path = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs", "Mvt", "MvtDecoder.cs");
            FileAssert.Exists(path);
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

            // Shape, not just count: all seven frees (including `values`) sit in ONE finally around both
            // flattens and Materialize. No IsCreated guard: Dispose() on a default value is a no-op.
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
            // Bracketing, not just "after try {": a call PAST the finally leaks, and the Dispose() counts
            // read the whole body, not where the calls sit.
            Assert.That(tagCallIndex, Is.InRange(tryIndex, finallyIndex),
                "the tag FlattenFeatureColumn call must sit BETWEEN try { and finally { — outside that " +
                "window its allocations are unreachable to the finally that frees them.");
            Assert.That(geomCallIndex, Is.InRange(tryIndex, finallyIndex),
                "the geometry FlattenFeatureColumn call must sit BETWEEN try { and finally {, for the same " +
                "reason as the tag call above.");

            // Without null-on-transfer the finally frees the `tagWords` the layer JUST adopted (a UAF). Pinned
            // by shape: the counts cannot tell "nulled after adopt" from "adopted, not nulled".
            StringAssert.Contains("AdoptFeatureTagWords(tagWords); tagWords = default;", normalised,
                "the adopted local must be nulled immediately after AdoptFeatureTagWords — the double-free " +
                "guard that keeps the finally's unconditional tagWords.Dispose() from freeing the buffer the " +
                "layer just took ownership of.");

            // Value-table stage: the same double-free guard for the values buffer AdoptValues transfers.
            StringAssert.Contains("AdoptValues(values, valueStrings); values = default;", normalised,
                "the adopted `values` local must be nulled immediately after AdoptValues — the same " +
                "double-free guard as tagWords, keeping the finally's unconditional values.Dispose() from " +
                "freeing the buffer the layer just took ownership of.");

            // The layer ADOPTS the (offset,count) columns too, so they need the same null-on-transfer, or the
            // finally frees columns every store reads by ordinal.
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
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);
            string body = StripLineComments(
                ExtractMethodBody(source, FlattenFeatureColumnSignatureAnchor, path));

            // Non-vacuity: a renamed/moved helper would make every claim below trivially true (or the
            // anchor lookup itself would already have failed inside ExtractMethodBody).
            Assert.Greater(body.Length, 0, "precondition: extracted a non-empty FlattenFeatureColumn body");
            StringAssert.Contains("ReadVarint", body,
                "precondition: the extracted body really does read varints — without this every index-order " +
                "assertion below would compare against an absent token and could pass vacuously.");

            // Each ref output is assigned exactly once, not staged in a local and copied at the end, which
            // would defeat passing by ref.
            foreach (string publish in new[]
                     { "offsets = new NativeArray<int>(", "lengths = new NativeArray<int>(", "words = new NativeArray<uint>(" })
            {
                Assert.AreEqual(1, CountOccurrences(body, publish),
                    $"FlattenFeatureColumn must publish its ref output through '{publish}' exactly once — " +
                    "directly, not via a local copied back later.");
            }

            // The FIRST publish (offsets) precedes the first varint read that can throw, so the caller holds
            // `offsets` even if the first count loop throws.
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
            FileAssert.Exists(path);
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolExtractorStructureTests — SymbolFeatureExtractor mints and disposes exactly the buffers it owns
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Structural teeth on the symbol consumer of the shared geometry. Symbol has no polygon, hole or area
    /// concept, so no fill-assembly stage (<c>RingAssemblyJob</c>) may run for it. <c>Extract</c> BORROWS
    /// <c>tileLayer.Geometry</c>, which the decoded layer owns, so it mints and disposes <b>zero</b> buffers.
    /// Limitation: the assertions are comment-stripped greps over call forms, not a C# parser.
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
        // "tileLayer.Geometry" is the expression the file reads the borrowed buffer through; a required set
        // with entries deleted is a disarmed test.
        private static readonly string[] RequiredTokens =
        {
            "TileGeometryBuffers", "tileLayer.Geometry", "RingFeatureIdx", "RingOffsets",
            "LineAnchorPlacement", "EmitAtAnchor",
        };

        // ── the ownership contract ──────────────────────────────────────────────────────────────────

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
                "This INVERTED from 1 to 0. Extract must mint NOTHING: the source-layer's buffer is " +
                "materialized once inside the DECODE and owned by the decoded layer, which lends " +
                "it to every consumer naming that source-layer — across both cadences of a kick.");
            Assert.AreEqual(0, CountOccurrences(body, "geometry.Dispose()"),
                "…and must free NOTHING. Disposing a BORROWED buffer frees geometry sibling layers are still " +
                "reading. That double free is LOUD here — the layer lends an array-backed buffer, and a sweep " +
                "measured it as 32 failures across 19 fixtures, two with no line involvement (heap " +
                "corruption). This tooth is structural to fail on the offending LINE, not because the " +
                "behavioural signal is missing.");

            // Geometry belongs to the layer, so "this method mints nothing and frees nothing" is the two
            // zero-counts above plus this clause: the one remaining way to mint privately is absent.
            Assert.AreEqual(0, CountOccurrences(body, "new MvtGeometryMaterializer("),
                "Extract must construct NO producer of its own. This is the only remaining shape of the " +
                "retired per-(layer, feature) mint: with the store gone, a private materializer is " +
                "how a consumer would silently stop sharing the layer's buffer — and it is OUTPUT-NEUTRAL, " +
                "so no behavioural test in the suite would notice.");
            Assert.AreEqual(1, CountOccurrences(NormaliseWhitespace(body),
                    "TileGeometryBuffers geometry = tileLayer.Geometry;"),
                "…and it must obtain the buffer in exactly one way: read once off the resolved layer. " +
                "(Whitespace-normalised grep, not a parser: it pins the exact form, so a second, differently " +
                "sourced buffer in the same body is visible.)");
        }

        // ── the fused-job fence, plus the claim that no production code decodes MVT geometry ──────────

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

            // Clause 2: MvtGeometry.Decode has no production call site left anywhere.
            const string decodeCallForm = "MvtGeometry.Decode(";
            var offenders = new List<string>();
            foreach (string assembly in new[] { "MapRenderer.Core", "MapRenderer.Jobs", "MapRenderer.Unity" })
            {
                string root = Path.Combine(Application.dataPath, "Code", assembly);
                DirectoryAssert.Exists(root);
                foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                    if (CountOccurrences(StripComments(File.ReadAllText(file)), decodeCallForm) > 0)
                        offenders.Add(file.Substring(Application.dataPath.Length));
            }

            // Positive control: a zero across three assemblies means nothing if the matcher is broken.
            string testRoot = Path.Combine(Application.dataPath, "Tests", "MapRenderer.Tests.EditMode");
            DirectoryAssert.Exists(testRoot);
            int testAssemblyHits = 0;
            foreach (string file in Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories))
                testAssemblyHits += CountOccurrences(StripComments(File.ReadAllText(file)), decodeCallForm);
            Assert.Greater(testAssemblyHits, 10,
                $"precondition (positive control): the SAME scan over the test assembly must find " +
                $"'{decodeCallForm}' many times — it is the differential oracle the line and symbol teeth measure against. " +
                $"Found {testAssemblyHits}; a low number means the matcher, not production, is what changed.");

            Assert.IsEmpty(offenders,
                $"ZERO production files may call '{decodeCallForm}' — the last one is retired " +
                "(SymbolFeatureExtractor). The TYPE stays: it is the spec transcription, the parity oracle " +
                $"MvtDecodeJob is measured against, and Tools/core-tests' ground truth. Offenders: " +
                string.Join(", ", offenders));
        }

        // ── THE NAMED FENCE, as a structural clause on the extractor ────────────────────────────────

        /// <summary>
        /// The three tile-space consumers inside <c>Extract</c> are handed TILE-SPACE identifiers.
        /// <c>Extract</c> holds tile-local, geodetic and projected coordinates at once, and the downstream
        /// epsilons are calibrated to tile-integer magnitude. Limitation: a call-form grep of three call sites,
        /// not a parser; <c>SymbolPaths_FromTheSharedBuffer_MatchTheManagedDecodeOracle</c> never runs them.
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
            FileAssert.Exists(path);
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // StyledLineBuilderStructureTests — the line builder never frees geometry it only borrows
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Structural teeth on the line consumer of the shared geometry. <c>RingAssemblyJob</c> is fill-only (its
    /// area filter drops a straight polyline); <c>StraightZeroAreaPolyline_StillRenders</c> is the behavioural
    /// half. The builder BORROWS the layer's geometry, which sibling layers still read, so it mints and
    /// disposes <b>zero</b> buffers; this names the rule at the offending line.
    /// </summary>
    [TestFixture]
    public class StyledLineBuilderStructureTests
    {
        /// <summary>Types that belong to the FILL assembly path. Line must reference none of them.</summary>
        private static readonly string[] ForbiddenFillStageTokens =
        {
            "RingAssemblyJob", "EarcutJob", "RingClipJob", "FillMeshPipeline",
            "GlobeFillSubdivide", "PolygonAssembler", "AdoptClippedLists",
            "DegenerateThreshold", "SignedArea",
        };

        /// <summary>Tokens that must be PRESENT. Without them a renamed, gutted or deleted file would satisfy
        /// every "count is zero" claim above trivially — the easiest kind of tooth to make vacuous.</summary>
        // The required tokens name the mechanism the file uses: the borrowed buffer type, the ordinal join,
        // and the job graph it schedules (the ring read and ribbon build live in MapRenderer.Jobs/LineMeshGraph.cs).
        private static readonly string[] RequiredTokens =
        {
            "TileGeometryBuffers", "LineMeshGraph", "LineGraphOutput", "new LayerInput", "LineStreamWriteJob",
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

        /// <summary>
        /// Scans BOTH partial files of <c>StyledLineTileBuilder</c> (<c>StyledLineTileBuilder.cs</c> and
        /// <c>StyledLineTileBuilder.WriteJob.cs</c>), comments stripped, so a mint or a
        /// <c>geometry.Dispose()</c> ANYWHERE in the type reds it.
        /// </summary>
        [Test]
        public void LineBuilderMintsNoBufferAndDisposesNone_ItBorrows()
        {
            string body = StripLineComments(LineBuilderSource() + LineBuilderWriteJobSource());

            // Non-vacuity anchors present across the two files. LineMeshGraph.Schedule is NOT one: its only
            // occurrence is in the method that moved to the test assembly.
            Assert.GreaterOrEqual(CountOccurrences(body, "BuildLayerInput("), 1,
                "precondition: the builder still passes the borrowed `geometry` on to build the graph's " +
                "input — without this, a gutted file would satisfy both zero-counts below vacuously");
            Assert.GreaterOrEqual(CountOccurrences(body, "new LayerInput"), 1,
                "precondition: the builder still constructs a LayerInput from the borrowed geometry — " +
                "without this, a gutted file would satisfy both zero-counts below vacuously. (Not the bare " +
                "word `LayerInput`: it is a substring of `BuildLayerInput` — both the method's own name and " +
                "its return-type token in the signature — so a stub with a gutted body but an intact " +
                "signature would satisfy that trivially and make the check no longer independent of the " +
                "`BuildLayerInput(` anchor above. `new LayerInput` appears only at the real construction " +
                "site, `return new LayerInput { ... }`, deep in the body, which a gutted implementation " +
                "cannot reach.)");

            // All three mint APIs (.Materialize(, TileGeometryBuffers.Allocate(, .AdoptDerivedLists(): a native
            // Allocate clone is also invisible to Is.Not.AllocatingGCMemory().
            foreach (string mintToken in new[]
                     { ".Materialize(", "TileGeometryBuffers.Allocate(", "TileGeometryBuffers.AdoptDerivedLists(" })
            {
                Assert.AreEqual(0, CountOccurrences(body, mintToken),
                    $"This INVERTED from 1 to 0. StyledLineTileBuilder must mint NOTHING — " +
                    $"'{mintToken}' found. The source-layer's buffer is materialized once per worker pass by " +
                    "TileGeometryStore and lent to every style layer naming that source-layer. A mint here is " +
                    "the 61-decodes-per-pass regression that was retired, re-introduced one layer down.");
            }
            Assert.AreEqual(0, CountOccurrences(body, "geometry.Dispose()"),
                "…and must free NOTHING. Disposing a BORROWED buffer frees geometry sibling layers are still " +
                "reading. That double free is LOUD here — the store lends an array-backed buffer, and a sweep " +
                "measured it as 32 failures across 19 fixtures, two with no line involvement (heap " +
                "corruption). This tooth is structural to fail on the offending LINE, not because the " +
                "behavioural signal is missing.");
        }

        // RibbonBatchJob builds the ribbon and LineStreamWriteJob writes it straight into Mesh.MeshData; a
        // managed List<> is caught by LineBuildAllocTests.WriteMeshData_OverAConstantStyle_AllocatesNoGCMemory.

        // StyledLineTileBuilder has no `using var` site left to fence; LineBuildAllocTests/DisposalLeakGuardTests
        // carry the no-stranded-handle property.

        private static string LineBuilderSource()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Meshing",
                "StyledLineTileBuilder.cs");
            FileAssert.Exists(path);
            return File.ReadAllText(path);
        }

        /// <summary>The type's second partial file — the write-step
        /// job lives here. Ordinal 7's re-founded fence must scan both.</summary>
        private static string LineBuilderWriteJobSource()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Meshing",
                "StyledLineTileBuilder.WriteJob.cs");
            FileAssert.Exists(path);
            return File.ReadAllText(path);
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


    // ───────────────────────────────────────────────────────────────────────────────────
    // TileProcessingStructureTests — TileManager.KickMeshBuild does not decode/dispatch directly
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>TileManager.KickMeshBuild</c> does not decode or write meshes directly; the shared decode and
    /// fan-out live ONLY in <see cref="MapRenderer.Unity.Rendering.Tile.Processing.TileLayerProcessorRunner"/>.
    /// The assertions read the CALL forms in the body, so an unused interface or a renamed local cannot pass.
    /// </summary>
    [TestFixture]
    public class TileProcessingStructureTests
    {
        private const string DecodeCallForm = "MvtDecoder.Decode(";
        // The runner's read of the caller-owned SharedDisposable<IDecodedTile>: it holds a decoded tile, so
        // the read is the property `.Value`, not a call.
        private const string DecodeReadForm = "decode.Value";
        // The direct mesh-write entry point a regression would reach for. Non-obvious why: a bare
        // `ScheduleWrite(` also matches `CompleteMeasureAndScheduleWrite(`, the correct delegation call.
        private const string ScheduleWriteCallForm = "TileBuilder.ScheduleWrite(";
        private const string RunnerCallForm = "TileLayerProcessorRunner.RunWorkerPass(";

        // Anchors the method DEFINITION: the return-type prefix makes it unique, unlike the call sites.
        private const string KickMeshBuildSignatureAnchor = "WorkHandle<Processing.TilePrologueOutput> KickMeshBuild(";

        [Test]
        public void TileManager_DelegatesDecodeAndLayerWritesToTheProcessorRunner()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs");
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);

            string body = ExtractMethodBody(source, KickMeshBuildSignatureAnchor, path);

            int decodeCalls        = CountOccurrences(body, DecodeCallForm);
            int scheduleWriteCalls = CountOccurrences(body, ScheduleWriteCallForm);
            int runnerCalls        = CountOccurrences(body, RunnerCallForm);

            Assert.AreEqual(0, decodeCalls,
                $"TileManager.KickMeshBuild must contain ZERO direct '{DecodeCallForm}' call sites — the " +
                "decode is the runner's job now (one MVT decode per mesh worker pass, owned by " +
                "TileLayerProcessorRunner).");
            Assert.AreEqual(0, scheduleWriteCalls,
                $"TileManager.KickMeshBuild must contain ZERO direct '{ScheduleWriteCallForm}' call sites — " +
                "the mesh write happens inside TileBuildGraph's write step (job-scheduling-design.md's " +
                "write step), reached only through TileMeshLayerProcessor.BuildGraphRequest + the runner, never " +
                "by KickMeshBuild calling a builder's ScheduleWrite directly.");
            Assert.AreEqual(1, runnerCalls,
                $"TileManager.KickMeshBuild must call '{RunnerCallForm}' exactly once — the single " +
                "decode-once fan-out point for this worker pass.");
        }

        /// <summary>The source's <c>GetTile</c> decodes, so <c>RunWorkerPass</c> decodes nothing and reads the
        /// caller's reference exactly once, via <c>decode.Value</c>: the mesh cadence's decode-once boundary.
        /// A background tile has no runner entry, so there is no sourceless half.</summary>
        [Test]
        public void TileLayerProcessorRunner_MeshWorkerPass_ReadsTheSharedDecode_AndNeverDecodesItself()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "Processing",
                "TileLayerProcessorRunner.cs");
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);

            const string workerPassAnchor = "TilePrologueOutput RunWorkerPass(";

            string workerPassBody = ExtractMethodBody(source, workerPassAnchor, path);

            Assert.AreEqual(0, CountOccurrences(workerPassBody, DecodeCallForm),
                $"RunWorkerPass must contain ZERO direct '{DecodeCallForm}' calls — the decode happens in " +
                "TileDecodeDispatch, before the lease exists; the runner only reads it.");
            Assert.AreEqual(1, CountOccurrences(workerPassBody, DecodeReadForm),
                $"RunWorkerPass must read '{DecodeReadForm}' exactly once — the mesh worker pass's own " +
                "read of the caller's lease. More than one read is more than one place a released lease " +
                "can be observed, and the fan-out is supposed to happen through the single tile it returns.");
        }

        /// <summary>The subsystem does not decode, extract or shape symbols itself; that machinery lives in the
        /// processor contract, and an unused façade beside inline calls cannot pass. Both
        /// <c>".ShapeAsync("</c> and <c>"_builder.Shape("</c> must be absent: shaping runs in
        /// <c>TileSymbolLayerProcessor.CompleteOnMain</c>. The concrete call form lets prose name it.</summary>
        [Test]
        public void SymbolSubsystem_DelegatesDecodeExtractAndShapeToTheProcessorMachinery()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);

            const string extractLayersCallForm = ".ExtractLayers(";
            const string shapeAsyncCallForm    = ".ShapeAsync(";
            const string shapeCallForm         = "_builder.Shape(";
            const string runSymbolCallForm     = "TileLayerProcessorRunner.RunSymbolWorkerPass(";

            Assert.AreEqual(0, CountOccurrences(source, DecodeCallForm),
                $"SymbolSubsystem.cs must contain ZERO direct '{DecodeCallForm}' call sites — the " +
                "symbol decode now lives in TileLayerProcessorRunner.RunSymbolWorkerPass.");
            Assert.AreEqual(0, CountOccurrences(source, extractLayersCallForm),
                $"SymbolSubsystem.cs must contain ZERO direct '{extractLayersCallForm}' call sites — " +
                "per-layer extraction now happens inside TileSymbolLayerProcessor.ProcessOnWorker.");
            Assert.AreEqual(0, CountOccurrences(source, shapeAsyncCallForm),
                $"SymbolSubsystem.cs must contain ZERO direct '{shapeAsyncCallForm}' call sites — the " +
                "identifier no longer exists (renamed to Shape by the glyph-fetch hoist).");
            Assert.AreEqual(0, CountOccurrences(source, shapeCallForm),
                $"SymbolSubsystem.cs must contain ZERO direct '{shapeCallForm}' call sites — " +
                "per-layer shaping still happens inside TileSymbolLayerProcessor.CompleteOnMain, never " +
                "called directly by the subsystem.");
            Assert.AreEqual(1, CountOccurrences(source, runSymbolCallForm),
                $"SymbolSubsystem.cs must call '{runSymbolCallForm}' exactly once — the single " +
                "decode-once fan-out point for the symbol worker pass.");
        }

        /// <summary><c>MapRenderer.Unity</c> contains ZERO <c>MvtDecoder.Decode(</c> calls, including
        /// <c>TileDecodeDispatch.cs</c>; the sole production call is in <c>MvtTileDecoder</c>. With
        /// <see cref="MapRendererJobs_DecodesMvtOnlyInsideMvtTileDecoder"/> this pins the one decode site.</summary>
        [Test]
        public void MapRendererUnity_DecodesMvtNowhere()
        {
            string root = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity");
            DirectoryAssert.Exists(root);

            string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
            Assert.Greater(files.Length, 0, $"expected at least one .cs file under {root}");

            var offenders = new System.Collections.Generic.List<string>();
            foreach (string file in files)
            {
                if (CountOccurrences(StripLineComments(File.ReadAllText(file)), DecodeCallForm) > 0)
                    offenders.Add(file);
            }

            Assert.IsEmpty(offenders,
                $"no file under MapRenderer.Unity may call '{DecodeCallForm}' — the " +
                $"sole production decode site is MvtTileDecoder in Core; found it in: {string.Join(", ", offenders)}");

            // TileDecodeDispatch.cs, the ONE place a decode is dispatched, reads through the injected
            // ITileDecoder, not MvtDecoder.
            string dispatchPath = Path.Combine(
                root, "Rendering", "Tile", "Processing", "TileDecodeDispatch.cs");
            FileAssert.Exists(dispatchPath);
            string dispatchSource = StripLineComments(File.ReadAllText(dispatchPath));
            Assert.AreEqual(0, CountOccurrences(dispatchSource, DecodeCallForm),
                $"TileDecodeDispatch.cs must contain ZERO '{DecodeCallForm}' call sites.");
            Assert.AreEqual(1, CountOccurrences(dispatchSource, "decoder.Decode("),
                "TileDecodeDispatch.cs must call 'decoder.Decode(' exactly once — the injected ITileDecoder " +
                "is the sole read, and the single dispatch is what makes 'exactly one decode " +
                "per fetched tile' a property of one method rather than of every source remembering it.");
        }

        /// <summary>The positive half: the ONE production <c>MvtDecoder.Decode(</c> call is in
        /// <c>Tiles/ITileDecoder.cs</c> (<c>MvtTileDecoder.Decode</c>), and <c>MapRenderer.Core</c> decodes
        /// <b>nowhere</b>. The scans stay per assembly, because test fixtures call <c>MvtDecoder.Decode(</c>
        /// legitimately (see <see cref="MapRendererUnity_DecodesMvtNowhere"/>).</summary>
        [Test]
        public void MapRendererJobs_DecodesMvtOnlyInsideMvtTileDecoder()
        {
            string jobsRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            DirectoryAssert.Exists(jobsRoot);

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
                "the sole production decode call site, rehomed to Jobs.");

            // The seam's own half: the assembly the seam LEFT must be silent. Without this clause a
            // half-finished move — the decoder copied into Jobs while Core keeps a caller — would pass.
            string coreRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Core");
            DirectoryAssert.Exists(coreRoot);
            string[] coreFiles = Directory.GetFiles(coreRoot, "*.cs", SearchOption.AllDirectories);
            Assert.Greater(coreFiles.Length, 100,
                "precondition: the Core scan must visit a real corpus (>100 .cs files); a scan that visited " +
                "none would report 'no offenders' just as loudly");

            var coreOffenders = new System.Collections.Generic.List<string>();
            foreach (string file in coreFiles)
                if (CountOccurrences(StripLineComments(File.ReadAllText(file)), DecodeCallForm) > 0)
                    coreOffenders.Add(file);

            Assert.IsEmpty(coreOffenders,
                $"no file under MapRenderer.Core may call '{DecodeCallForm}' — the whole tile-" +
                "decode seam moved out of Core (ARCHITECTURE.md § \"Module boundaries\": the product is Unity + Jobs; " +
                $"Core is legacy, not a destination for new code). Found it in: {string.Join(", ", coreOffenders)}");
        }

        /// <summary>The fill/line/symbol fan-out (15 files) names NO MVT carrier type (<c>MvtTile</c>/
        /// <c>MvtFeature</c>/<c>MvtLayer</c>/<c>MvtDecoder</c>), only the neutral surface. Anti-vacuity: each
        /// geometry file must name every identifier of the mechanism it uses, comment-stripped with word
        /// boundaries, so neither prose nor a longer identifier satisfies it.</summary>
        [Test]
        public void WriteIntoPath_ReferencesNoMvtCarrierTypes()
        {
            string unityRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity");
            // FeatureSelector and SourceLayerResolver left MapRenderer.Core for MapRenderer.Jobs
            // along with the rest of the decode seam. Same two files, same claim, new root.
            string jobsRoot  = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");

            (string root, string relativePath)[] scanned =
            {
                (unityRoot, Path.Combine("Rendering", "Tile", "Processing", "ITileLayerProcessor.cs")),
                (unityRoot, Path.Combine("Rendering", "Tile", "Processing", "TileMeshLayerProcessor.cs")),
                (unityRoot, Path.Combine("Rendering", "Tile", "Processing", "TileSymbolLayerProcessor.cs")),
                (unityRoot, Path.Combine("Rendering", "Tile", "Processing", "BackgroundQuad.cs")),
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
            Assert.AreEqual(15, scanned.Length, "the F-1 file set is pinned at 15 files.");

            string[] mvtCarrierTokens = { "MvtTile", "MvtFeature", "MvtLayer", "MvtDecoder" };
            var offenders = new System.Collections.Generic.List<string>();

            foreach ((string root, string relativePath) in scanned)
            {
                string path = Path.Combine(root, relativePath);
                FileAssert.Exists(path);
                string text = File.ReadAllText(path);
                foreach (string token in mvtCarrierTokens)
                    if (CountOccurrences(text, token) > 0)
                        offenders.Add($"{relativePath} ('{token}')");
            }

            Assert.IsEmpty(offenders,
                "the WriteInto-path fan-out must reference ZERO MVT carrier types (MvtTile/MvtFeature/" +
                "MvtLayer/MvtDecoder) — only the neutral IDecodedTile/ITileLayer/IFeature/ITileDecoder " +
                $"surface. Offenders: {string.Join(", ", offenders)}");

            // Anti-vacuity: a raw substring cannot detect a gutted file, so each file names its mechanism's
            // identifiers, matched comment-stripped with word boundaries.
            (string relativePath, string[] required)[] mechanisms =
            {
                // Both files BORROW the layer's buffer; `tileLayer` exists only because the LAYER owns the
                // geometry. The line row names the job graph it schedules (MapRenderer.Jobs/LineMeshGraph.cs).
                (Path.Combine("Rendering", "Meshing", "StyledLineTileBuilder.cs"),
                    new[] { "TileGeometryBuffers", "LineMeshGraph", "LineGraphOutput", "LayerInput" }),
                (Path.Combine("Text", "SymbolFeatureExtractor.cs"),
                    new[] { "TileGeometryBuffers", "RingFeatureIdx", "tileLayer", "RingOffsets",
                            "LineAnchorPlacement" }),
            };

            foreach ((string relativePath, string[] required) in mechanisms)
            {
                string path = Path.Combine(unityRoot, relativePath);
                FileAssert.Exists(path);
                string code = StripComments(File.ReadAllText(path));

                foreach (string identifier in required)
                {
                    Assert.IsTrue(ContainsIdentifier(code, identifier),
                        $"{relativePath} must name '{identifier}' — this file still obtains geometry, now by " +
                        $"BORROWING the shared buffer, and gutting it would drop all {required.Length} of " +
                        "this file's required identifiers. WHY THIS CLAUSE CHANGED " +
                        ": the previous one counted the bare substring 'MvtGeometry' in RAW text. A " +
                        "rename satisfied it through 'MvtGeometryMaterializer' (a longer identifier " +
                        "containing the token) and removing the last MvtGeometry.Decode call left it " +
                        "satisfied by an XML doc comment — its premise ('these files still decode geometry') " +
                        "expired as the consumers moved onto the shared buffer. Cost, " +
                        "accepted deliberately: a rename of the mechanism makes this go RED loudly, whereas " +
                        "the old clause's rename made it go green silently.");
                }

                // The other half of the same intent, made explicit: the retired managed decoder is GONE from
                // both files — what the old clause could not tell you.
                Assert.IsFalse(ContainsIdentifier(code, "MvtGeometry"),
                    $"{relativePath} must NOT name the bare identifier 'MvtGeometry' — the managed reference " +
                    "decoder has zero production callers. Word-boundary matched, so a longer " +
                    "identifier merely CONTAINING the token would not be a hit (e.g. " +
                    "'MvtGeometryMaterializer', which both files named until they moved off minting " +
                    "and onto the borrowed buffer); comment-stripped, so a doc comment cannot satisfy or " +
                    "violate it.");
            }
        }

        /// <summary>The symbol cadence does not decode for
        /// itself — it reads the shared entry exactly once, same as the mesh cadence.</summary>
        [Test]
        public void RunSymbolWorkerPass_DoesNotDecodeItself_ReadsTheSharedEntry()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "Processing",
                "TileLayerProcessorRunner.cs");
            FileAssert.Exists(path);
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

        /// <summary><c>TileManager</c> and <see cref="SourceRegistry"/> are byte-agnostic: they name none of
        /// <c>TileResponse</c>/<c>IDataSource</c>/<c>TileScheduler</c>/a concrete lease type/standalone
        /// <c>TileCache</c>, and <c>SourceRegistry.cs</c> names <c>ITileFeatureSource</c>. <c>TileCache</c> is
        /// matched on word boundaries, because the substring also sits inside <c>PreparedTileCache</c>.</summary>
        [Test]
        public void TileManager_ConsumesNoByteSource()
        {
            string tileManagerPath = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs");
            string registryPath = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "SourceRegistry.cs");
            FileAssert.Exists(tileManagerPath);
            FileAssert.Exists(registryPath);
            string tileManagerSource = File.ReadAllText(tileManagerPath);
            string registrySource    = File.ReadAllText(registryPath);

            foreach (var (name, source) in new[] { ("TileManager.cs", tileManagerSource), ("SourceRegistry.cs", registrySource) })
            {
                Assert.AreEqual(0, CountOccurrences(source, "TileResponse"),
                    $"{name} must contain ZERO 'TileResponse' occurrences — the byte-response type is now " +
                    "an MvtTileFeatureSource-internal detail.");
                Assert.AreEqual(0, CountOccurrences(source, "IDataSource"),
                    $"{name} must contain ZERO 'IDataSource' occurrences — the byte fetcher is wrapped " +
                    "BELOW the raised ITileFeatureSource seam, never named at the coordinator.");
                Assert.AreEqual(0, CountOccurrences(source, "TileScheduler"),
                    $"{name} must contain ZERO 'TileScheduler' occurrences — scheduling now lives inside " +
                    "MvtTileFeatureSource.");
                Assert.AreEqual(0, CountOccurrences(source, "SharedTileDecode"),
                    $"{name} must contain ZERO 'SharedTileDecode' occurrences — the type is deleted, and " +
                    "a surviving mention would be a stale comment describing a lifetime model that no longer exists.");
                Assert.AreEqual(0, CountOccurrences(source, "DecodedTileLease"),
                    $"{name} must contain ZERO 'DecodedTileLease' occurrences either — the coordinator " +
                    "holds only the polymorphic SharedDisposable<IDecodedTile>, never a concrete lease " +
                    "implementation (DecodedTileLease was deleted outright). Widened from the SharedTileDecode " +
                    "clause rather than replacing it: naming a concrete lease type would re-create exactly the " +
                    "coupling the old clause forbade.");

                int standaloneTileCacheCount = Regex.Matches(source, @"\bTileCache\b").Count;
                Assert.AreEqual(0, standaloneTileCacheCount,
                    $"{name} must contain ZERO STANDALONE 'TileCache' occurrences (word-boundary match, " +
                    "excluding the 19 kept 'PreparedTileCache' references) — the " +
                    "byte-level LRU cache now lives inside MvtTileFeatureSource.");
            }

            Assert.Greater(CountOccurrences(registrySource, "ITileFeatureSource"), 0,
                "SourceRegistry.cs must reference ITileFeatureSource — the raised seam it now holds instead " +
                "of IDataSource/TileScheduler.");
            Assert.Greater(CountOccurrences(tileManagerSource, "SharedDisposable"), 0,
                "TileManager.cs must reference SharedDisposable — the decode-provisioning reference count " +
                "(SharedDisposable<IDecodedTile>) it threads through the mesh/symbol kick, and whose " +
                "reference it owns and releases. Successor to the IDecodedTileHandle clause.");
        }

        /// <summary><c>SourceRegistry</c>'s public surface is EXACTLY ten members, with no <c>SourcePipeline</c>
        /// escaping; reflection, because the nested class's fields fool an indentation grep. The bound has
        /// zero headroom: an eleventh member means the seam failed.</summary>
        [Test]
        public void SourceRegistry_SurfaceIsExactlyTenMembers()
        {
            Type type = typeof(SourceRegistry);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            PropertyInfo[] properties = type.GetProperties(flags);
            MethodInfo[]   methods    = type.GetMethods(flags).Where(m => !m.IsSpecialName).ToArray();

            Assert.AreEqual(10, properties.Length + methods.Length,
                $"SourceRegistry's public surface must be EXACTLY 10 members — found " +
                $"{properties.Length} properties ({string.Join(", ", properties.Select(p => p.Name))}) + " +
                $"{methods.Length} methods ({string.Join(", ", methods.Select(m => m.Name))}). An eleventh member " +
                "is a STOP-and-report finding (tile-pipeline-design.md § \"The source registry's narrow surface\"), not a tooth to widen.");

            // Matches SourcePipeline itself, a byref (`&`), an array, or a generic argument, which a bare-name
            // match would let a widened SourcePipeline dodge.
            static bool Mentions(Type t) => t.Name.TrimEnd('&') == "SourcePipeline"
                || (t.HasElementType && Mentions(t.GetElementType()))
                || t.GetGenericArguments().Any(Mentions);

            foreach (PropertyInfo p in properties)
                Assert.IsFalse(Mentions(p.PropertyType),
                    $"{p.Name} must not expose SourcePipeline — it is private for a reason.");
            foreach (MethodInfo m in methods)
            {
                Assert.IsFalse(Mentions(m.ReturnType),
                    $"{m.Name} must not return SourcePipeline — it is private for a reason.");
                foreach (ParameterInfo p in m.GetParameters())
                    Assert.IsFalse(Mentions(p.ParameterType),
                        $"{m.Name}'s parameter '{p.Name}' must not take a SourcePipeline — it is private for a reason.");
            }
        }

        /// <summary>Non-local invariant: <c>DrainMeshBuilds</c> spins on <c>IsCompleted</c>, so
        /// <c>MvtTileFeatureSource.GetTile</c> must introduce NO main-thread hop, or the spin never sees
        /// completion without pumping the PlayerLoop. Structural; the <c>DrainMeshBuilds</c> snapshot/leak
        /// tests are the behavioural net.</summary>
        [Test]
        public void MvtTileFeatureSource_GetTile_NeverHopsToMainThread()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "Processing",
                "MvtTileFeatureSource.cs");
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);

            Assert.AreEqual(0, CountOccurrences(source, "SwitchToMainThread"),
                "MvtTileFeatureSource.cs must contain ZERO 'SwitchToMainThread' occurrences — GetTile's " +
                "continuation must stay off the PlayerLoop (supplied by TileDecodeDispatch.DecodeAsync on " +
                "the HasData path, and by synchronous/inline completion otherwise — not by the fetch " +
                "scheduler, which introduces no thread-pool hop of its own), preserving the off-PlayerLoop " +
                "invariant DrainMeshBuilds' spin depends on.");
        }

        /// <summary>No file under <c>Assets/Code</c> or <c>Assets/Tests</c> names the old geometry-type enum
        /// ("Mvt" + "GeometryType"); it is <c>Core.Tiles.TileGeometryType</c>. Non-obvious why: this file is
        /// swept too, so the token is concatenated and no comment here may spell it. Positive half: the type's
        /// file exists and it resolves in <c>Core.Tiles</c>.</summary>
        [Test]
        public void GeometryTypeEnum_RenameIsComplete_NoOldTokenSurvives()
        {
            string codeRoot = Path.Combine(Application.dataPath, "Code");
            DirectoryAssert.Exists(codeRoot);
            string testsRoot = Path.Combine(Application.dataPath, "Tests");
            DirectoryAssert.Exists(testsRoot);

            string oldTypeName = "Mvt" + "GeometryType";

            string[] files = Directory.GetFiles(codeRoot, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
                .ToArray();
            Assert.Greater(files.Length, 0, $"expected at least one .cs file under {codeRoot} or {testsRoot}");

            var offenders = new System.Collections.Generic.List<string>();
            foreach (string file in files)
            {
                if (CountOccurrences(File.ReadAllText(file), oldTypeName) > 0)
                    offenders.Add(file);
            }

            Assert.IsEmpty(offenders,
                $"no file under {codeRoot} or {testsRoot} may reference the retired geometry-type enum name " +
                $"— it was renamed to Core.Tiles.TileGeometryType; found the old name in: " +
                $"{string.Join(", ", offenders)}");

            string newTypePath = Path.Combine(codeRoot, "MapRenderer.Core", "Tiles", "TileGeometryType.cs");
            FileAssert.Exists(newTypePath);
        }

        // ── The decode-abandonment funnels ───────────────────────────────────────────────────
        // Each funnel is a CHOKEPOINT: its release obligation holds only if nothing bypasses it.

        private const string TileManagerPathTail       = "TileManager.cs";
        private const string RenderTeardownCallForm    = "RenderTeardownRecord(";
        private const string DestroyTrackedMeshesForm  = "DestroyTrackedMeshes(";

        // Matched by PATTERN, so a reformat or a renamed loop variable does not red it; it still requires
        // ONE foreach over _loaded.
        private const string LoadedForeachPattern = @"foreach\s*\(\s*var\s+\w+\s+in\s+_loaded\s*\)";

        /// <summary>Funnel 1 (the record): <c>TileManager.DoDispose</c> must tear records down through
        /// <c>RenderTeardownRecord</c>, the one funnel cover-change, eviction and restyle already use — not
        /// through hand-rolled loops of its own. Comment-stripped. Counts alone pass a guarded call, a
        /// <c>continue</c> or an early <c>_loaded.Clear()</c>, so <see cref="AssertUnconditionallyReachedOnce"/>
        /// and an ORDERING check on offsets back them.</summary>
        [Test]
        public void TileManagerDoDispose_TearsDownEveryRecordThroughTheSingleFunnel()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", TileManagerPathTail);
            FileAssert.Exists(path);

            string body = StripComments(
                ExtractMethodBody(File.ReadAllText(path), "protected override void DoDispose()", path));

            // ONE pass over _loaded — and the pass itself is reached on every path through DoDispose.
            Assert.AreEqual(1, Regex.Matches(body, LoadedForeachPattern).Count,
                "TileManager.DoDispose must walk _loaded EXACTLY once — one pass, one funnel call per " +
                "record. Three passes (the older shape) is three places a later obligation can be missed.");
            AssertUnconditionallyReachedOnce(body, LoadedForeachPattern,
                "the single _loaded teardown pass in TileManager.DoDispose",
                "a pass reached only under a condition (or behind an early return) is a teardown that can " +
                "skip every record at once.");

            // Nothing reaches AROUND the funnel: a record whose meshes are destroyed here never had its
            // in-flight fetch / mesh build stashed in the pens.
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

        /// <summary><c>SetSources</c> must clear every slot-keyed collection
        /// (<c>_loaded</c>, <c>_releaseQueue</c>, <c>_releaseQueued</c>, <c>_desired</c>, <c>_desiredSet</c>)
        /// unconditionally, and <c>_sources.Rebuild</c> must follow that clear block. The unconditional clears
        /// stop a stale slot-keyed entry outliving a re-slot. Limitation: clause 1 rejects ANY earlier jump token,
        /// even an unrelated <c>break</c>, so check the jump's owning loop before assuming a regression. Clause 2
        /// can only fail once the <c>hasBackground</c> <c>break</c> leaves this body; until then clause 1
        /// catches a moved clear first.</summary>
        [Test]
        public void TileManagerSetSources_ClearsSlotKeyedStateUnconditionally_ThenRebuildsSources()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", TileManagerPathTail);
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);

            string body = StripComments(
                ExtractMethodBody(source, "internal void SetSources(", path));

            const string rebuildForm = "_sources.Rebuild(";
            string[] clearForms =
            {
                "_loaded.Clear()", "_releaseQueue.Clear()", "_releaseQueued.Clear()",
                "_desired.Clear()", "_desiredSet.Clear()",
            };

            int lastClearIndex = -1;
            foreach (string clearForm in clearForms)
            {
                Assert.AreEqual(1, CountOccurrences(body, clearForm),
                    $"TileManager.SetSources must call '{clearForm}' EXACTLY once.");
                AssertUnconditionallyReachedOnce(body, Regex.Escape(clearForm),
                    $"'{clearForm}' in TileManager.SetSources",
                    "a conditional clear can leave a slot-keyed entry alive across the registry rebuild. " +
                    "This clause, not the ordering below, " +
                    "is what actually guards against that.");
                lastClearIndex = Math.Max(lastClearIndex, body.IndexOf(clearForm, StringComparison.Ordinal));
            }

            Assert.AreEqual(1, CountOccurrences(body, rebuildForm),
                $"TileManager.SetSources must call '{rebuildForm}' EXACTLY once.");
            Assert.Greater(body.IndexOf(rebuildForm, StringComparison.Ordinal), lastClearIndex,
                $"TileManager.SetSources must call '{rebuildForm}' AFTER every slot-keyed clear — a rebuild " +
                "that runs first re-slots the registry before the old state referencing the OLD slots is " +
                "dropped.");
        }

        /// <summary>Funnel 2 (the abandoned fetch): the discard arm must be structurally incapable of
        /// handing a decode handle back to its CALLER: <see cref="PendingDisposalQueue"/>'s
        /// <c>DiscardFetchOutcome</c> returns <see langword="void"/>. The discard sites must call it, not
        /// <c>TileManager.TakeDecodeFromFetch</c>. The funnel's body must observe the outcome; the release
        /// itself belongs to <c>EagerDecodeOwnershipTests</c>.</summary>
        [Test]
        public void TheFetchDiscardFunnel_CannotHandBackADecodeHandle()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "PendingDisposalQueue.cs");
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);
            string stripped = StripComments(source);

            Assert.AreEqual(0, CountOccurrences(stripped, "ObserveFetchOutcome"),
                "PendingDisposalQueue.cs must contain ZERO 'ObserveFetchOutcome' occurrences — the one " +
                "method whose bool parameter was ALMOST the own-vs-discard discriminator is split into two " +
                "whose signatures cannot be confused.");
            Assert.AreEqual(1, CountOccurrences(stripped, "private void DiscardFetchOutcome("),
                "PendingDisposalQueue.cs must declare 'private void DiscardFetchOutcome(' exactly once — " +
                "the VOID return type is the enforcement. The moment this method hands something back it " +
                "stops being a funnel and becomes a fourth thing to remember at every drop site.");
            Assert.AreEqual(0, CountOccurrences(stripped, "SharedDisposable<IDecodedTile> DiscardFetchOutcome("),
                "DiscardFetchOutcome must NEVER return SharedDisposable<IDecodedTile> — see above; this " +
                "clause is the one that fails if a later change 'just returns it for convenience'.");

            // The funnel's OWN body: an empty one passes every call-site clause. The observation must be the
            // try's unconditional statement, because a guarded GetResult observes only some outcomes.
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
                "while leaving the abandoned task's fault unobserved — the UnityWebRequestException " +
                "console flood, back through the method that exists to prevent it.");

            // File-scoped: TakeDecodeFromFetch lives on TileManager, and this absence check rules out a copy
            // of it pasted into this file.
            Assert.AreEqual(0, CountOccurrences(stripped, "TakeDecodeFromFetch("),
                "PendingDisposalQueue.cs must contain ZERO 'TakeDecodeFromFetch(' occurrences — that " +
                "method is TileManager's owning-arm entry point; this file must only ever discard.");

            foreach (string discardSiteAnchor in new[]
                     { "public void DrainCompleted(", "public void FlushAll(" })
            {
                string body = StripComments(ExtractMethodBody(source, discardSiteAnchor, path));
                Assert.Greater(CountOccurrences(body, "DiscardFetchOutcome("), 0,
                    $"the fetch-abandonment site anchored at '{discardSiteAnchor}' must observe its fetch " +
                    "outcomes through DiscardFetchOutcome — an unobserved fetch task is the " +
                    "UnityWebRequestException console flood. STRUCTURAL HINT, stated as one: this is a " +
                    "PRESENCE count, not a reachability proof, and unlike the other two funnels it cannot " +
                    "be strengthened into one — both sites call the funnel from inside a pen-drain loop " +
                    "that legitimately `continue`s past a task that has not completed yet, so 'reached on " +
                    "every iteration' is not a property this site HAS. What every pen entry is eventually " +
                    "observed is the runtime leak teeth to prove, not this clause's.");
            }
        }

        /// <summary>Funnel 4 (the parked symbol entry): every site that DISCARDS parked builds must go
        /// through <c>SymbolSubsystem.DrainAndDiscardParkedBuilds</c>. Exactly two
        /// <c>_pendingSpriteQueue.TryDequeue(</c> sites may exist in the file — the funnel, and
        /// <c>PumpBuilds</c>' live drain, which CONSUMES entries. Each call goes through
        /// <see cref="AssertUnconditionallyReachedOnce"/>, so a guarded call does not pass.</summary>
        [Test]
        public void TheParkedBuildPurge_HasExactlyOneDiscardFunnel()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            FileAssert.Exists(path);
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
        /// The parked drain dispatches through <c>WorkScheduler.Schedule</c>, and both ownership-guard releases
        /// sit in a <c>finally</c>. The runtime cases live in <c>SymbolParkedRedecodeTests</c>; this is the
        /// residue they cannot see. Passing <c>captured.Ct</c> is harmless: <c>IWorkScheduler</c> does not skip
        /// a body on its token (<c>WorkSchedulerContractTests</c>), and the in-lambda check guards the work.
        /// </summary>
        [Test]
        public void TheParkedDrainsDispatch_GoesThroughTheWorkScheduler_AndItsGuardReleasesSitInFinallys()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            FileAssert.Exists(path);
            string source = File.ReadAllText(path);

            string pumpBody   = StripComments(ExtractMethodBody(source, "public void PumpBuilds()", path));
            string funnelBody = StripComments(
                ExtractMethodBody(source, "private void DrainAndDiscardParkedBuilds(", path));

            // ── The dispatch, narrowed to its own argument list ──────────────────────────────────────
            Assert.AreEqual(0, CountOccurrences(pumpBody, "UniTask.RunOnThreadPool"),
                "PumpBuilds must no longer dispatch the parked drain's worker phase through raw " +
                "UniTask.RunOnThreadPool — it is dead on WebGL (docs/web-target.md).");
            // The dispatch must reach the INJECTED policy: a body-minted `new InlineWorkScheduler()` still reads
            // like a dispatch and passes the substring check below, so both construction forms are banned.
            Assert.AreEqual(0, CountOccurrences(pumpBody, "new InlineWorkScheduler"),
                "PumpBuilds must dispatch through the injected WorkScheduler property, never a locally " +
                "constructed InlineWorkScheduler — that hard-codes the policy and defeats platform selection.");
            Assert.AreEqual(0, CountOccurrences(pumpBody, "new ThreadPoolWorkScheduler"),
                "PumpBuilds must dispatch through the injected WorkScheduler property, never a locally " +
                "constructed ThreadPoolWorkScheduler — WorkSchedulerFactory is the only construction site.");
            int dispatchIndex = pumpBody.IndexOf("WorkScheduler.Schedule(", StringComparison.Ordinal);
            Assert.GreaterOrEqual(dispatchIndex, 0,
                "PumpBuilds must dispatch the parked drain's worker phase through WorkScheduler.Schedule — a " +
                "direct call would put a full extract on the frame thread, and a raw " +
                "UniTask.RunOnThreadPool would be dead on WebGL.");
            string dispatchCall = ExtractParenAfter(pumpBody, dispatchIndex, path);
            Assert.IsTrue(CountOccurrences(dispatchCall, "captured.Ct") > 0,
                "the dispatch must forward captured.Ct to Schedule — mirrors KickMeshBuild's idiom. Unlike " +
                "UniTask.RunOnThreadPool's cancellationToken:, IWorkScheduler.Schedule never skips the body " +
                "based on this token (WorkSchedulerContractTests pins that contract directly), so passing it " +
                "here is inert for skip purposes; the in-lambda ct check below is what actually guards.");

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
            // It lives in TryParkBuild, the one gated site (TryParkBuildAndTheAbandonDrain_LockTheSameParkGate).
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
        /// <c>TryParkBuild</c> and <c>DrainAndDiscardParkedBuilds</c> both lock the SAME <c>_parkGate</c>,
        /// unconditionally (<see cref="AssertUnconditionallyReachedOnce"/>). Limitation: the runtime tooth
        /// <c>SymbolParkedRedecodeTests.AParkBlockedByTheAbandonDrainGate_…</c> sees only the PARK side, so this
        /// is the only observer of a drain that stops taking the gate.
        /// </summary>
        [Test]
        public void TryParkBuildAndTheAbandonDrain_LockTheSameParkGate()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs");
            FileAssert.Exists(path);
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
        /// Non-local invariant: <c>KickMeshBuild</c> ACQUIRES its OWN reference in its prologue; on success it
        /// transfers to the returned <c>TilePrologueOutput</c>, on a fault a guard <c>catch</c> releases it. The
        /// record keeps its own reference for its whole in-cover life, released only by
        /// <c>RenderTeardownRecord</c>, so no caller may null <c>lt.Decode</c> around the call.
        /// </summary>
        [Test]
        public void KickMeshBuild_AcquiresItsOwnReference_AndNeitherCallerNullsTheRecordsField()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs");
            FileAssert.Exists(path);
            string source   = File.ReadAllText(path);
            string stripped = StripComments(source);
            string body     = StripComments(
                ExtractMethodBody(source, KickMeshBuildSignatureAnchor, path));

            Assert.AreEqual(1, CountOccurrences(body, "decode.Acquire()"),
                "KickMeshBuild must call 'decode.Acquire()' exactly once — its OWN reference, taken in the " +
                "main-thread prologue before the scheduler call exists. The transfer is retired: the record no " +
                "longer hands this reference over, so the kick must take a separate one of its own.");
            Assert.AreEqual(2, CountOccurrences(body, "decode.Release()"),
                "KickMeshBuild must contain EXACTLY TWO 'decode.Release()' — one guard `catch` around the " +
                "worker-pass body (a fault there releases instead of transferring) and one guard `catch` " +
                "around the Acquire()->IWorkScheduler.Schedule hand-off (which can throw synchronously — OOM " +
                "— before the body runs). They are mutually exclusive by control flow, so each reference is " +
                "released exactly once; a THIRD would be a real double-free.");
            Assert.AreEqual(0, CountOccurrences(body, "finally"),
                "…job-scheduling-design.md: there is no `finally` any more — on success the " +
                "reference TRANSFERS to the returned TilePrologueOutput instead of being released here.");
            Assert.AreEqual(2, Regex.Matches(body, @"catch\s*\{\s*decode\.Release\(\);\s*throw;\s*\}").Count,
                "…and BOTH releases are guard `catch { decode.Release(); throw; }` blocks (mirrors " +
                "TryParkBuild): a fault frees the kick's own reference instead of leaking it. Remove either " +
                "rethrow and it swallows the fault; remove either release and it leaks.");
            Assert.AreEqual(1, CountOccurrences(body, "output.Decode = decode"),
                "KickMeshBuild must transfer the reference to the output exactly once, on the success path — " +
                "'output.Decode = decode;' — or nothing ever frees the kick's own reference on a successful " +
                "build (a leak the two guard catches above cannot see, since neither fires).");

            Assert.AreEqual(3, CountOccurrences(stripped, "KickMeshBuild("),
                "TileManager.cs must contain exactly three 'KickMeshBuild(' occurrences: the definition and " +
                "its two call sites. A third call site is a new owner-to-callee relationship these clauses " +
                "would not be checking.");
            Assert.AreEqual(2, CountOccurrences(stripped, "KickMeshBuild(lt, id, lt.Decode"),
                "…and BOTH call sites must feed the RECORD's field, never a bare local. A bare local is the " +
                "one owner no funnel can see: if the prologue throws, the local unwinds carrying the kick's " +
                "own reference before it was even acquired — this pins that the kick reads FROM the record's " +
                "field, the ownership question notwithstanding.");

            foreach (string callerAnchor in new[]
                     { "void DrainMeshBuilds(CameraProperties", "private int PumpPending(" })
            {
                string callerBody = StripComments(ExtractMethodBody(source, callerAnchor, path));
                int call = callerBody.IndexOf("KickMeshBuild(lt, id, lt.Decode", StringComparison.Ordinal);
                Assert.GreaterOrEqual(call, 0,
                    $"the caller anchored at '{callerAnchor}' must kick through lt.Decode");
                Assert.AreEqual(0, CountOccurrences(callerBody, "lt.Decode = null"),
                    $"'{callerAnchor}' must NOT null lt.Decode anywhere around its KickMeshBuild call — the change " +
                    "retired the transfer: the record keeps its own reference for its whole in-cover " +
                    "lifetime, and RenderTeardownRecord (funnel 1) is the only release. A null-out here is " +
                    "the retired transfer shape leaking back in.");
            }
        }

        /// <summary>
        /// The mesh-build kicks never call <c>UniTask.RunOnThreadPool</c>, which never runs its delegate on a
        /// web player (docs/web-target.md). The runtime <c>InjectedScheduler_Runs…OnTheCallingThread</c> teeth
        /// pin the POLICY; this pins the PLACEMENT. The <c>.Preserve()</c> check covers only the kick bodies,
        /// because the fetch in <c>AdmitTile</c> keeps its own.
        /// </summary>
        [Test]
        public void MeshBuildKicks_DispatchThroughTheWorkScheduler()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs");
            FileAssert.Exists(path);
            string source   = File.ReadAllText(path);
            string stripped = StripComments(source);

            // Counted on STRIPPED text, not raw source — a comment merely mentioning either token (e.g. a
            // "why not RunOnThreadPool" explanation) must not satisfy or defeat these counts.
            Assert.AreEqual(0, CountOccurrences(stripped, "UniTask.RunOnThreadPool"),
                "TileManager.cs must contain ZERO 'UniTask.RunOnThreadPool' call sites — the mesh-build kick " +
                "still on the seam (KickMeshBuild) dispatches through IWorkScheduler, the only policy that " +
                "runs on a WebGL player.");
            // The source-less background kick schedules FillMeshGraph directly (MeshBuildWorkSchedulerTests),
            // so only KickMeshBuild dispatches through the seam.
            Assert.AreEqual(1, CountOccurrences(stripped, "WorkScheduler.Schedule("),
                "TileManager.cs must contain exactly one 'WorkScheduler.Schedule(' call site — KickMeshBuild. " +
                "KickSourcelessBackground no longer uses the seam at all.");

            string meshBuildBody = StripComments(
                ExtractMethodBody(source, KickMeshBuildSignatureAnchor, path));

            // A whole-file count of one is satisfied by any single call, including a wrong one — name the
            // site: the remaining call must be INSIDE KickMeshBuild, not merely somewhere.
            Assert.AreEqual(1, CountOccurrences(meshBuildBody, "WorkScheduler.Schedule("),
                "the one remaining 'WorkScheduler.Schedule(' call site must be inside KickMeshBuild's own " +
                "method body.");

            Assert.AreEqual(0, CountOccurrences(meshBuildBody, ".Preserve()"),
                "KickMeshBuild must not call .Preserve() — WorkHandle<T>.GetResult() is repeatable once " +
                "terminal with no recycling hazard (the backing completion source never recycles), so the " +
                "wrapper this stage retires from KickMeshBuild is genuinely unnecessary, not just deleted.");

            // Tooth (h): BuildStep.Seam is renamed to BuildStep.Prologue
            // throughout; a survivor is either a stale rename or a live reference to the retired member name.
            Assert.AreEqual(0, CountOccurrences(stripped, "BuildStep.Seam"),
                "TileManager.cs must contain ZERO 'BuildStep.Seam' occurrences — the member was renamed to " +
                "BuildStep.Prologue (job-scheduling-design.md); a survivor names a member that no " +
                "longer exists.");
        }

        /// <summary>
        /// <c>RenderTeardownRecord</c> DISARMS before it fires — <c>lt.Decode</c> is nulled before
        /// <c>Release()</c> is called, not after.
        ///
        /// Limitation: no runtime tooth sees the order, because callers pass a struct COPY and
        /// <c>MethodInfo.Invoke</c> does not copy a by-ref argument back on a throw. <c>Release()</c> and
        /// <c>Dispose()</c> can throw, so the order decides whether a handle outlives its reference. See
        /// docs/per-layer-tile-processing-design.md § "Recorded limitations — two things no test can observe".
        /// </summary>
        [Test]
        public void RenderTeardownRecord_NullsTheRecordsHandleBeforeItReleases()
        {
            string path = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Tile", "TileManager.cs");
            FileAssert.Exists(path);
            string body = StripComments(ExtractMethodBody(
                File.ReadAllText(path), "private void RenderTeardownRecord(ref LoadedTile", path));

            Match disarm  = Regex.Match(body, @"lt\.Decode\s*=\s*null\s*;");
            Match release = Regex.Match(body, @"\.Release\(\)");
            Assert.IsTrue(disarm.Success,  "RenderTeardownRecord must null the record's decode handle");
            Assert.IsTrue(release.Success, "RenderTeardownRecord must release the record's decode handle");
            Assert.Less(disarm.Index, release.Index,
                "RenderTeardownRecord must NULL lt.Decode BEFORE releasing. SharedDisposable's Release() " +
                "is undefended (a DEBUG assertion only, not a throw), but the tile's Dispose() can still " +
                "throw; with the null-out afterwards a throw there leaves the field armed with a reference " +
                "that is already released, so a retry releases it a second time — a silent double-release " +
                "rather than a loud fault. Same 'a transfer nulls the source' rule the mesh lifetime uses, " +
                "applied to the ORDER of the two effects and not just their presence.");
        }

        /// <summary>Extracts the brace-balanced body (inclusive of the outer braces) of the method whose
        /// definition contains <paramref name="signatureAnchor"/>, by scanning forward from the first '{'
        /// after the anchor and counting nesting depth. A simple, narrow tool for a
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
        /// (with its outer braces) starting at the first '{' at or after <paramref name="searchFrom"/>.
        /// <paramref name="blockEndIndex"/> is the closing brace's offset, for the ordering clauses.</summary>
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
        /// <paramref name="searchFrom"/>, like <see cref="ExtractBlockAfter"/> for parentheses. It scopes a
        /// clause to ONE call site rather than the whole enclosing method.</summary>
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
        /// as an <b>unconditionally reached</b> statement: at brace depth 1, STARTING a statement (which
        /// catches a braceless <c>if (c) Funnel();</c>), with no earlier jump token in the block. The pattern
        /// must match from the statement's first character. Limitation: a text shape, not control flow; an
        /// earlier throw, a string-literal brace or an empty funnel body pass it.</summary>
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

        /// <summary><see cref="StripLineComments"/> plus block comments, so prose cannot satisfy the
        /// anti-vacuity clause in <see cref="WriteIntoPath_ReferencesNoMvtCarrierTypes"/>. Inherits its
        /// narrowness: a <c>"http://…"</c> literal eats the rest of its line.</summary>
        private static string StripComments(string text)
            => StripLineComments(Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline));

        /// <summary>Whole-identifier presence. The <c>\b</c> anchors make <c>MvtGeometry</c> NOT match inside
        /// <c>MvtGeometryMaterializer</c> — the substring collision that disarmed the previous clause.</summary>
        private static bool ContainsIdentifier(string text, string identifier)
            => Regex.IsMatch(text, $@"\b{Regex.Escape(identifier)}\b");

        /// <summary>Strips everything from <c>//</c> (which also covers doc-comment <c>///</c>) to the end
        /// of each line, so a PROSE mention of a call form does not count as a call site in the decode-site
        /// scans. Limitation: no block-comment or string-literal awareness.</summary>
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


    // ───────────────────────────────────────────────────────────────────────────────────
    // FillMeshGraphStructureTests — FillMeshGraph's source shape: no Complete/stale .AsArray()/.Run/IWorkScheduler
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pins FillMeshGraph's SOURCE shape: no <c>Complete()</c>, no schedule-time <c>.AsArray()</c>, no
    /// <c>.Run(</c>, no <c>IWorkScheduler</c>, plus required tokens against a gutted file. The behavioural
    /// not-completed tooth cannot see a <c>Complete()</c> that is a no-op on its fixture. A dropped edge
    /// throws under the job debugger in the parity tests; dispose nodes are balanced by
    /// <c>FillMeshGraphSchedulingTests</c>.
    /// </summary>
    [TestFixture]
    public class FillMeshGraphStructureTests
    {
        // A regex, because a literal "Complete()" misses "Handle.Complete ()" or a wrapped call; the other
        // tokens are exact identifiers or API calls.
        private static readonly Regex CompleteCallPattern = new Regex(@"\.Complete\s*\(");

        private static readonly string[] ForbiddenTokens =
        {
            ".AsArray(", ".Run(", "IWorkScheduler",
        };

        private static readonly string[] RequiredTokens =
        {
            "SizingJob", "FillGatherJob", "EarcutBatchJob", "AggregateJob",
            "RingAssemblyJob", "JobHandle",
            // The curved-arm sub-chain — its two nodes, and the
            // predicate (exactly WriteGeometry's) that decides which arm a layer takes.
            "GlobeFillSubdivideDispatch", "GlobeFillScatterJob", "MaxRefineAngleRad",
            // EarcutBatchJob has no loop to bound, so only this literal reds if its deferred count stops
            // coming from the sizing-owned buffers.PerPolyOuterCount.
            "Schedule(buffers.PerPolyOuterCount, EarcutPolygonBatch, gathered)",
            // No token pins a LOCAL VARIABLE'S spelling: that breaks on a rename, not a regression.
        };

        [Test]
        public void FillMeshGraph_HasNoCompleteNoScheduleTimeAsArray_AndReferencesEveryNode()
        {
            string code = StripLineComments(FillMeshGraphSource());
            Assert.Greater(code.Trim().Length, 0, "precondition: FillMeshGraph.cs source is non-empty");

            foreach (string token in RequiredTokens)
                Assert.Greater(CountOccurrences(code, token), 0,
                    $"precondition: FillMeshGraph.cs must still reference '{token}' — without it this test's " +
                    "zero-counts below would be satisfied by a gutted or renamed file");

            foreach (string token in ForbiddenTokens)
                Assert.AreEqual(0, CountOccurrences(code, token),
                    $"FillMeshGraph.cs must contain ZERO occurrences of '{token}' — this graph builder never " +
                    "takes a schedule-time array view of a list it resizes, never dispatches synchronously, " +
                    "and never touches the managed-closure seam.");

            Assert.AreEqual(0, CompleteCallPattern.Matches(code).Count,
                "FillMeshGraph.cs must contain ZERO Complete() calls — this graph builder never completes " +
                "its own handle.");
        }

        // ── The attribute fence over the graph-builder and stream-write directories: no
        // NativeDisableContainerSafetyRestriction anywhere, and NativeDisableParallelForRestriction exactly once
        // each in EarcutBatchJob.cs and RibbonBatchJob.cs. Non-obvious why: an exception is a (file, token,
        // count) triple, as a bare filename exempts every token; the BARE identifier catches qualified forms.

        private static readonly string[] AttributeFenceForbiddenTokens =
        {
            "NativeDisableContainerSafetyRestriction", "NativeDisableParallelForRestriction",
        };

        private struct AttributeFenceException
        {
            public string FileName;
            public string Token;
            public int ExpectedCount;
        }

        /// <summary>Each entry's COUNT derives from the job's field list, not from the file's current text:
        /// each <c>Buffers</c> field carries one occurrence. Red here means the field shape changed; update the
        /// count with intent.</summary>
        private static readonly AttributeFenceException[] AttributeFenceAllowedFiles =
        {
            new AttributeFenceException
            {
                FileName = "EarcutBatchJob.cs", Token = "NativeDisableParallelForRestriction", ExpectedCount = 1,
            },
            new AttributeFenceException
            {
                FileName = "RibbonBatchJob.cs", Token = "NativeDisableParallelForRestriction", ExpectedCount = 1,
            },
        };

        [Test]
        public void GraphBuilderSources_CarryNeitherSafetyDisableAttribute()
        {
            string jobsDir    = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            string meshingDir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Meshing");
            DirectoryAssert.Exists(jobsDir);
            DirectoryAssert.Exists(meshingDir);

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(jobsDir, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(meshingDir, "*.cs", SearchOption.AllDirectories));

            Assert.GreaterOrEqual(files.Count, 10,
                "precondition: the enumeration must find a plausible number of production files, or a moved " +
                "directory / wrong Application.dataPath join would make every assertion below vacuous");
            Assert.IsTrue(files.Exists(f => Path.GetFileName(f) == "FillMeshGraph.cs"),
                "precondition: the enumeration must include a known graph builder file (FillMeshGraph.cs)");

            // Each sanctioned file must be found, or a rename turns its exception into a silent no-op.
            foreach (AttributeFenceException exception in AttributeFenceAllowedFiles)
                Assert.IsTrue(files.Exists(f => Path.GetFileName(f) == exception.FileName),
                    $"precondition: the enumeration must include the sanctioned exception file {exception.FileName} " +
                    "— a rename would otherwise make its exception a silent no-op");

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                string code = StripLineComments(File.ReadAllText(file));

                foreach (string token in AttributeFenceForbiddenTokens)
                {
                    int allowedCount = 0;
                    foreach (AttributeFenceException exception in AttributeFenceAllowedFiles)
                        if (exception.FileName == name && exception.Token == token) allowedCount = exception.ExpectedCount;

                    Assert.AreEqual(allowedCount, CountOccurrences(code, token),
                        $"{name} must carry exactly {allowedCount} occurrence(s) of {token} — job-scheduling-design.md " +
                        "§ \"Safety — making the Editor's check sufficient\" rule 2 confines it to the sanctioned " +
                        "(file, token, count) exceptions in AttributeFenceAllowedFiles.");
                }
            }
        }

        // ── The JobsDebugger precondition: every dependency-edge tooth's schedule-time write-write detection
        //    depends on this toggle, not on being in the Editor. ─────────────────────────

        [Test]
        public void JobDebugger_IsEnabled_OrEveryDependencyEdgeToothInThisEpicIsVacuous()
        {
            Assert.IsTrue(JobsUtility.JobDebuggerEnabled,
                "the Editor's job debugger is OFF. Every dependency-edge tooth recorded against a dropped " +
                "`deps` edge (job-scheduling-design.md — the missing-edge " +
                "InvalidOperationException at Schedule) proves nothing while this reads false: the " +
                "schedule-time write-write detection is contingent on this toggle, not on merely running in " +
                "the Editor.");
        }

        // ── The sizing-owned-buffers consumer fence. Limitation: which value bounds a loop is dataflow, not
        //    lexical; this pins the consumer SET, counting nodes rather than tokens, so borrowed fields pass. ──

        // The type plus ANY identifier, so a renamed field ("TriangulationBuffers FillBuffers;") still matches.
        private static readonly Regex[] SizingOwnedBuffersFieldPatterns =
        {
            new Regex(@"TriangulationBuffers\s+\w+;"), new Regex(@"RibbonBuffers\s+\w+;"),
        };

        /// <summary>Verified today: exactly these seven declare a sizing-owned buffers struct as a field.
        /// Red on an EIGHTH means a new node joined this family — check what column it bounds its own loop by
        /// against job-scheduling-design.md's rule 2 before adding it here.</summary>
        private static readonly string[] KnownSizingOwnedBuffersConsumers =
        {
            "SizingJob.cs", "FillGatherJob.cs", "EarcutBatchJob.cs", "AggregateJob.cs",
            "RibbonSizingJob.cs", "RibbonBatchJob.cs", "RibbonAggregateJob.cs",
        };

        [Test]
        public void GraphNodes_DeclaringASizingOwnedBufferStruct_MatchTheKnownConsumerSet()
        {
            string jobsDir    = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            string meshingDir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Meshing");
            DirectoryAssert.Exists(jobsDir);
            DirectoryAssert.Exists(meshingDir);

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(jobsDir, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(meshingDir, "*.cs", SearchOption.AllDirectories));
            Assert.GreaterOrEqual(files.Count, 10,
                "precondition: the enumeration must find a plausible number of production files, or a moved " +
                "directory / wrong Application.dataPath join would make the consumer set below vacuously small");

            var consumers = new List<string>();
            foreach (string file in files)
            {
                string code = StripLineComments(File.ReadAllText(file));
                foreach (Regex pattern in SizingOwnedBuffersFieldPatterns)
                    if (pattern.IsMatch(code)) { consumers.Add(Path.GetFileName(file)); break; }
            }
            consumers.Sort();

            var expected = new List<string>(KnownSizingOwnedBuffersConsumers);
            expected.Sort();
            CollectionAssert.AreEqual(expected, consumers,
                "the set of files declaring a TriangulationBuffers/RibbonBuffers field no longer matches " +
                "KnownSizingOwnedBuffersConsumers — the bounding rule in job-scheduling-design.md § \"Safety — making the " +
                "Editor's check sufficient\" requires every node in this family to bound its own loop by a column the sizing " +
                "job resizes, never a borrowed count; verify the new/removed file against that rule before updating this list.");
        }

        private static string FillMeshGraphSource()
        {
            string path = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs", "Fill", "FillMeshGraph.cs");
            FileAssert.Exists(path);
            return File.ReadAllText(path);
        }

        private static int CountOccurrences(string text, string token)
            => Regex.Matches(text, Regex.Escape(token)).Count;

        /// <summary>Strips everything from <c>//</c> (including <c>///</c> XML doc comments) to end of line,
        /// so a token merely NAMED in prose — e.g. this file's own doc, or <c>FillMeshGraph.cs</c>'s "no
        /// <c>.AsArray()</c> at schedule time" rule doc, or its "the <c>.Run(n)</c> this scheduled path
        /// replaces" note — is not counted as a reference. Matches <c>StyledLineBuilderStructureTests</c>'s
        /// idiom.</summary>
        private static string StripLineComments(string text)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int idx = lines[i].IndexOf("//", System.StringComparison.Ordinal);
                if (idx >= 0) lines[i] = lines[i].Substring(0, idx);
            }
            return string.Join('\n', lines);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillMeshPipelineRetirementFenceTests — retired fill-pipeline symbols never regrow a caller
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// No production code references a symbol whose only caller was the synchronous fill pipeline. Stop
    /// rule: <see cref="ForbiddenPatterns"/> derives from that predicate, not a hand-picked list. The scan is
    /// identifier-anchored over <c>MapRenderer.Jobs/</c> and <c>MapRenderer.Unity/Rendering/</c>, comments
    /// stripped. <c>ProjectPointsJob.Run</c> cannot be anchored, so
    /// <see cref="ProjectPointsJobConstructionFenceTests"/> fences its construction site instead.
    /// </summary>
    [TestFixture]
    public class FillMeshPipelineRetirementFenceTests
    {
        /// <summary>One pattern per retired symbol with no production caller: a call form where a bare name
        /// would hit an unrelated member, a word-boundary match otherwise. <c>Run\s*\(</c> does NOT match
        /// <c>RunTyped(</c>, hence the separate <c>RunTyped</c> entries.</summary>
        private static readonly (string Label, Regex Pattern)[] ForbiddenPatterns =
        {
            ("FillMeshPipeline.Schedule(",        new Regex(@"\bFillMeshPipeline\.Schedule\s*\(")),
            ("TileMeshBuffers",                    new Regex(@"\bTileMeshBuffers\b")),
            ("WriteGlobeSubdivided",               new Regex(@"\bWriteGlobeSubdivided\b")),
            ("WriteGlobeRoof",                     new Regex(@"\bWriteGlobeRoof\b")),
            ("WriteFlatRoof",                      new Regex(@"\bWriteFlatRoof\b")),
            ("GlobeFillSubdivideDispatch.Run(",     new Regex(@"\bGlobeFillSubdivideDispatch\.Run\s*\(")),
            ("GlobeFillSubdivideDispatch.RunTyped", new Regex(@"\bGlobeFillSubdivideDispatch\.RunTyped\b")),
            ("ProjectionDispatch.Run(",             new Regex(@"\bProjectionDispatch\.Run\s*\(")),
            ("ProjectionDispatch.RunTyped",         new Regex(@"\bProjectionDispatch\.RunTyped\b")),
            ("CountsFromArrayLength",               new Regex(@"\bCountsFromArrayLength\b")),
            ("SrcVertCount",                        new Regex(@"\bSrcVertCount\b")),
            ("SrcIndexCount",                       new Regex(@"\bSrcIndexCount\b")),
        };

        [Test]
        public void ProductionSources_ContainNoRetiredSynchronousPipelineReferences()
        {
            string jobsDir   = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            string unityDir  = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering");
            DirectoryAssert.Exists(jobsDir);
            DirectoryAssert.Exists(unityDir);

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(jobsDir, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(unityDir, "*.cs", SearchOption.AllDirectories));

            // Non-vacuity: guards a path typo silently scanning zero files.
            Assert.GreaterOrEqual(files.Count, 50,
                $"precondition: expected to scan at least 50 .cs files under MapRenderer.Jobs/ + " +
                $"MapRenderer.Unity/Rendering/ (found {files.Count}) — a path typo would silently scan nothing.");

            var violations = new List<string>();
            foreach (string path in files)
            {
                string stripped = StripLineComments(File.ReadAllText(path));
                foreach ((string label, Regex pattern) in ForbiddenPatterns)
                {
                    if (pattern.IsMatch(stripped))
                        violations.Add($"{path}: '{label}'");
                }
            }

            Assert.IsEmpty(violations,
                "production source under MapRenderer.Jobs/ or MapRenderer.Unity/Rendering/ still references a " +
                "a retired symbol mesher — the graph is the only mesher now:\n" +
                string.Join("\n", violations));
        }

        /// <summary>The retired synchronous <c>WriteMeshData</c> methods cannot grow back into the three
        /// builder source files. Identifier-anchored, declaration-form
        /// (<c>\bWriteMeshData\s*[(&lt;]</c>) rather than the call-form patterns above — see the class doc's
        /// last paragraph for why: the risk is the method growing back with a different modifier or arity,
        /// which only the declaration form sees.</summary>
        [Test]
        public void WriteMeshDataCannotGrowBackIntoTheBuilderSources()
        {
            string meshingDir = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Meshing");
            DirectoryAssert.Exists(meshingDir);

            string[] files =
            {
                Path.Combine(meshingDir, "StyledFillTileBuilder.cs"),
                Path.Combine(meshingDir, "StyledFillExtrusionTileBuilder.cs"),
                Path.Combine(meshingDir, "StyledLineTileBuilder.cs"),
            };
            var declarationPattern = new Regex(@"\bWriteMeshData\s*[(<]");

            var violations = new List<string>();
            foreach (string path in files)
            {
                FileAssert.Exists(path);
                string stripped = StripLineComments(File.ReadAllText(path));
                if (declarationPattern.IsMatch(stripped))
                    violations.Add(path);
            }

            Assert.IsEmpty(violations,
                "a builder source file still declares WriteMeshData — the synchronous convenience method " +
                "was retired with zero production callers and moved to MapRenderer.Tests.SyncMeshWrite; it " +
                "must not grow back here under any modifier or arity:\n" + string.Join("\n", violations));
        }

        /// <summary>Strips everything from <c>//</c> to end of line — the same narrow grep-guard idiom
        /// <c>TileGeometryBuffersOwnershipTests</c> uses. Also removes every <c>///</c> XML-doc line (a
        /// <c>///</c> line IS a <c>//</c> line syntactically, so this is the same pass): this repo's docs
        /// keep past-tense prose naming these retired symbols as the reason they were retired,
        /// and that history is not what this fence exists to police.</summary>
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // WallChainCallerFenceTests — the wall-job chain cannot re-inline into the extrusion prologue
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The wall chain cannot run inside the extrusion prologue. Limitation: no runtime test can see it, as
    /// Unity has no manual <c>JobHandle</c>. So the prologue file names none of the wall-chain types, and
    /// <c>WallQuadJob</c> appears in EXACTLY its declaring partial and <c>FillExtrusionMeshGraph.cs</c>, so a
    /// third partial cannot re-inline it. Same scan as <see cref="FillMeshPipelineRetirementFenceTests"/>.
    /// </summary>
    [TestFixture]
    public class WallChainCallerFenceTests
    {
        private const string PrologueFileName = "StyledFillExtrusionTileBuilder.cs";

        private static readonly string[] PrologueForbiddenIdentifiers =
        {
            "WallQuadJob", "RingSelectJob", "TileToGeoJob", "ProjectionDispatch", "WriteWalls",
        };

        private static readonly string[] ExpectedWallQuadJobFiles =
        {
            "StyledFillExtrusionTileBuilder.WallJob.cs", "FillExtrusionMeshGraph.cs",
        };

        [Test]
        public void Prologue_ReferencesNoneOfTheWallChainNodes()
        {
            List<string> files = ScanFiles();
            string prologuePath = null;
            foreach (string path in files)
            {
                if (Path.GetFileName(path) == PrologueFileName) { prologuePath = path; break; }
            }
            Assert.IsNotNull(prologuePath, $"precondition: expected to find {PrologueFileName} under the scanned roots.");

            string stripped = StripLineComments(File.ReadAllText(prologuePath));
            var violations = new List<string>();
            foreach (string identifier in PrologueForbiddenIdentifiers)
            {
                if (Regex.IsMatch(stripped, @"\b" + identifier + @"\b"))
                    violations.Add(identifier);
            }

            Assert.IsEmpty(violations,
                $"{PrologueFileName} — the prologue's own file — still references the wall chain directly: " +
                string.Join(", ", violations) + ". The wall chain must be reachable only through " +
                "FillExtrusionMeshGraph.Schedule, never inline in the prologue body.");
        }

        [Test]
        public void WallQuadJob_AppearsInExactlyTheTwoExpectedProductionFiles()
        {
            List<string> files = ScanFiles();

            var actual = new List<string>();
            foreach (string path in files)
            {
                string stripped = StripLineComments(File.ReadAllText(path));
                if (Regex.IsMatch(stripped, @"\bWallQuadJob\b"))
                    actual.Add(Path.GetFileName(path));
            }
            actual.Sort();

            var expected = new List<string>(ExpectedWallQuadJobFiles);
            expected.Sort();

            Assert.AreEqual(expected, actual,
                "WallQuadJob must appear in exactly its declaring file and its sole scheduler — a THIRD file " +
                "referencing it (e.g. a re-inlined construction back into the prologue's partial-class family) " +
                "means the wall chain became reachable from somewhere this fence does not expect, and a " +
                "MISSING expected file means the job was renamed or its scheduler changed without updating " +
                "this fence.");
        }

        private static List<string> ScanFiles()
        {
            string jobsDir  = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            string unityDir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering");
            DirectoryAssert.Exists(jobsDir);
            DirectoryAssert.Exists(unityDir);

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(jobsDir, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(unityDir, "*.cs", SearchOption.AllDirectories));

            // Non-vacuity: guards a path typo silently scanning zero files — same floor as
            // FillMeshPipelineRetirementFenceTests' own scan.
            Assert.GreaterOrEqual(files.Count, 50,
                $"precondition: expected to scan at least 50 .cs files under MapRenderer.Jobs/ + " +
                $"MapRenderer.Unity/Rendering/ (found {files.Count}) — a path typo would silently scan nothing.");
            return files;
        }

        /// <summary>Strips everything from <c>//</c> to end of line, including <c>///</c> XML-doc lines — the
        /// same idiom <see cref="FillMeshPipelineRetirementFenceTests"/>'s own copy uses, for the same
        /// reason: this repo's docs keep past-tense prose naming retired call shapes, and that
        /// history is not what this fence exists to police.</summary>
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // ProjectPointsJobConstructionFenceTests — only its declaration + scheduler may construct ProjectPointsJob
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>ProjectPointsJob&lt;TProj&gt;.Run(n)</c> is public and no regex can anchor an instance call, so the
    /// CONSTRUCTION SITE is fenced: only <c>ProjectPointsJob.cs</c> and <c>ProjectionDispatch.cs</c> may name
    /// <c>ProjectPointsJob</c>. Same scan as
    /// <c>WallChainCallerFenceTests.WallQuadJob_AppearsInExactlyTheTwoExpectedProductionFiles</c>.
    /// </summary>
    [TestFixture]
    public class ProjectPointsJobConstructionFenceTests
    {
        private static readonly string[] ExpectedFiles =
        {
            "ProjectPointsJob.cs", "ProjectionDispatch.cs",
        };

        [Test]
        public void ProjectPointsJob_AppearsInExactlyTheTwoExpectedProductionFiles()
        {
            string jobsDir  = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            string unityDir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering");
            DirectoryAssert.Exists(jobsDir);
            DirectoryAssert.Exists(unityDir);

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(jobsDir, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(unityDir, "*.cs", SearchOption.AllDirectories));

            // Non-vacuity: guards a path typo silently scanning zero files — same floor as the sibling fences.
            Assert.GreaterOrEqual(files.Count, 50,
                $"precondition: expected to scan at least 50 .cs files under MapRenderer.Jobs/ + " +
                $"MapRenderer.Unity/Rendering/ (found {files.Count}) — a path typo would silently scan nothing.");

            var actual = new List<string>();
            foreach (string path in files)
            {
                string stripped = StripLineComments(File.ReadAllText(path));
                if (Regex.IsMatch(stripped, @"\bProjectPointsJob\b"))
                    actual.Add(Path.GetFileName(path));
            }
            actual.Sort();

            var expected = new List<string>(ExpectedFiles);
            expected.Sort();

            Assert.AreEqual(expected, actual,
                "ProjectPointsJob must appear in exactly its declaring file and its sole scheduler — a THIRD " +
                "file referencing it means something else can now construct the job directly and reach " +
                "Run(n) synchronously, exactly the hole this fence exists to close. A MISSING expected file " +
                "means the job or its scheduler was renamed without updating this fence.");
        }

        /// <summary>Strips everything from <c>//</c> to end of line, including <c>///</c> XML-doc lines —
        /// the same idiom every sibling fence in this directory uses.</summary>
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // TileBuildGraphKindNeutralityTests — TileBuildGraph retains no per-kind (fill/line/extrusion) knowledge
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>TileBuildGraph</c> knows only <c>ILayerMeshBuild</c>: (a) its declaration footprint, so a partial
    /// cannot evade it, and (b) none of fourteen kind identifiers, including <see cref="FillLayerBuild"/>/
    /// <see cref="FillExtrusionLayerBuild"/>/<see cref="LineLayerBuild"/>. Limitation: a helper class holding
    /// the switch, called from <c>TileBuildGraph.cs</c>, passes both.
    /// </summary>
    [TestFixture]
    public class TileBuildGraphKindNeutralityTests
    {
        private const string TargetFileName = "TileBuildGraph.cs";

        private static readonly Regex ClassDeclaration = new(
            @"\b(?:internal|public)?\s*(partial\s+)?(sealed\s+)?class\s+TileBuildGraph\b");

        private static readonly string[] ForbiddenKindIdentifiers =
        {
            // The eleven pre-stage names.
            "FillMeshGraph", "LineMeshGraph", "FillExtrusionMeshGraph",
            "FillGraphOutput", "LineGraphOutput", "FillExtrusionGraphOutput",
            "StyledFillTileBuilder", "StyledLineTileBuilder", "StyledFillExtrusionTileBuilder",
            "WallColumns", "LayerInput",
            // The three created here — without these the fence is blind to the reflex re-growth.
            "FillLayerBuild", "FillExtrusionLayerBuild", "LineLayerBuild",
        };

        [Test]
        public void TileBuildGraph_IsDeclaredExactlyOnce_AndNeverAsPartial()
        {
            List<string> files = ScanFiles();

            var matchingFiles = new List<string>();
            bool anyPartial = false;
            foreach (string path in files)
            {
                string stripped = StripLineComments(File.ReadAllText(path));
                foreach (Match m in ClassDeclaration.Matches(stripped))
                {
                    matchingFiles.Add(Path.GetFileName(path));
                    if (m.Groups[1].Success) anyPartial = true;
                }
            }

            Assert.AreEqual(1, matchingFiles.Count,
                $"expected exactly ONE declaration of `class TileBuildGraph` across both scan roots, found " +
                $"{matchingFiles.Count} ({string.Join(", ", matchingFiles)}) — a second file declaring it " +
                "(e.g. a `partial` split) is how a fence over one file's contents gets evaded.");
            Assert.AreEqual(TargetFileName, matchingFiles[0],
                $"the one declaration must live in {TargetFileName}, not a renamed/relocated file.");
            Assert.IsFalse(anyPartial,
                "TileBuildGraph must never be declared `partial` — forbidding it here closes the evasion at " +
                "the source rather than chasing file names.");
        }

        [Test]
        public void TileBuildGraph_ReferencesNoneOfTheFourteenKindIdentifiers()
        {
            List<string> files = ScanFiles();
            string targetPath = null;
            foreach (string path in files)
            {
                if (Path.GetFileName(path) == TargetFileName) { targetPath = path; break; }
            }
            Assert.IsNotNull(targetPath, $"precondition: expected to find {TargetFileName} under the scanned roots.");

            string stripped = StripLineComments(File.ReadAllText(targetPath));
            var violations = new List<string>();
            foreach (string identifier in ForbiddenKindIdentifiers)
            {
                if (Regex.IsMatch(stripped, @"\b" + identifier + @"\b"))
                    violations.Add(identifier);
            }

            Assert.IsEmpty(violations,
                $"{TargetFileName} still references kind-specific type(s): " + string.Join(", ", violations) +
                ". TileBuildGraph must know only ILayerMeshBuild — every kind-specific call belongs inside " +
                "that kind's own build type.");
        }

        private static List<string> ScanFiles()
        {
            string jobsDir  = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            string unityDir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering");
            DirectoryAssert.Exists(jobsDir);
            DirectoryAssert.Exists(unityDir);

            var files = new List<string>();
            files.AddRange(Directory.GetFiles(jobsDir, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.GetFiles(unityDir, "*.cs", SearchOption.AllDirectories));

            // Non-vacuity: guards a path typo silently scanning zero files — same floor as
            // WallChainCallerFenceTests' own scan.
            Assert.GreaterOrEqual(files.Count, 50,
                $"precondition: expected to scan at least 50 .cs files under MapRenderer.Jobs/ + " +
                $"MapRenderer.Unity/Rendering/ (found {files.Count}) — a path typo would silently scan nothing.");
            return files;
        }

        /// <summary>Strips everything from <c>//</c> to end of line, including <c>///</c> XML-doc lines — the
        /// same idiom <see cref="WallChainCallerFenceTests"/>'s own copy uses, for the same reason: this
        /// repo's docs keep past-tense prose naming retired call shapes, and that history is not
        /// what this fence exists to police.</summary>
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // RenderModeMeshingFenceTests — mesh preparation never branches on render mode (Lit vs Unlit)
    // ───────────────────────────────────────────────────────────────────────────────────

// Disambiguate from UnityEngine.RenderMode (Canvas) — the map's render mode is the material-set one.

    /// <summary>
    /// <b>Mesh preparation never branches on render mode</b>: no file in <c>MapRenderer.Jobs</c>,
    /// <c>Rendering/Meshing</c> or <c>Rendering/Tile</c> names <see cref="RenderMode"/>; material and lighting
    /// folders may. A comment-stripped source scan sees a branch that reflection cannot, and the token is
    /// <c>typeof(RenderMode).Name</c>, so a rename re-aims it. Limitation: a quoted string reds falsely, and an
    /// untyped proxy (<c>bool isUnlit</c>) passes.
    /// </summary>
    [TestFixture]
    public class RenderModeMeshingFenceTests
    {
        /// <summary>The mesh-preparation source roots, repo-relative to <c>Assets/</c>, each with the
        /// minimum file count that proves the scan actually reached a corpus. A folder rename or a moved
        /// tree reds this fixture instead of silently scanning nothing — the fence fails closed.</summary>
        private static readonly (string Path, int MinFiles)[] FencedRoots =
        {
            (Path.Combine("Code", "MapRenderer.Jobs"),                          40),
            (Path.Combine("Code", "MapRenderer.Unity", "Rendering", "Meshing"),  10),
            (Path.Combine("Code", "MapRenderer.Unity", "Rendering", "Tile"),     15),
        };

        /// <summary>The positive control: the roots that legitimately DO name the render mode — material
        /// selection and the host's lighting bootstrap. If the same scan finds nothing here it is blind, and
        /// the zero it reports over the fenced roots means nothing.</summary>
        private static readonly string[] ControlRoots =
        {
            Path.Combine("Code", "MapRenderer.Unity", "Rendering", "Materials"),
            Path.Combine("Code", "MapRenderer.App"),
        };

        [Test]
        public void NoMeshPreparationSource_NamesTheRenderModeEnum()
        {
            // Derived, never a literal: a rename of the enum re-aims the scan instead of defeating it.
            string token = typeof(RenderMode).Name;

            var offenders = new List<string>();
            int scanned   = 0;

            foreach ((string relativeRoot, int minFiles) in FencedRoots)
            {
                string root = Path.Combine(Application.dataPath, relativeRoot);
                DirectoryAssert.Exists(root, $"precondition: the fenced mesh-preparation root '{relativeRoot}' must exist. If the " +
                    "tree moved, re-aim this fence — do not delete the entry.");

                string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
                Assert.GreaterOrEqual(files.Length, minFiles,
                    $"precondition: '{relativeRoot}' must contain at least {minFiles} .cs files; a scan " +
                    "that visited a near-empty tree would report 'no offenders' just as loudly");

                foreach (string file in files)
                {
                    scanned++;
                    if (StripComments(File.ReadAllText(file)).Contains(token, StringComparison.Ordinal))
                        offenders.Add(file.Substring(Application.dataPath.Length).TrimStart('/', '\\'));
                }
            }

            // Positive control — the SAME comment-stripped matcher, over the folders whose job IS the mode.
            var controlHits = new List<string>();
            foreach (string relativeRoot in ControlRoots)
            {
                string root = Path.Combine(Application.dataPath, relativeRoot);
                DirectoryAssert.Exists(root, $"precondition: the control root '{relativeRoot}' must exist — it is what proves the " +
                    "matcher can see the token it reports as absent above");

                foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                    if (StripComments(File.ReadAllText(file)).Contains(token, StringComparison.Ordinal))
                        controlHits.Add(file.Substring(Application.dataPath.Length).TrimStart('/', '\\'));
            }

            Assert.IsNotEmpty(controlHits,
                $"positive control: the comment-stripped scan MUST find '{token}' in code under " +
                $"{string.Join(" or ", ControlRoots)} — material selection and the lighting bootstrap are " +
                "where the mode belongs. Finding none means the matcher is blind and the fenced roots' " +
                "clean result below proves nothing.");

            TestContext.WriteLine(
                $"[RenderModeMeshingFence] token='{token}' fencedFilesScanned={scanned} " +
                $"controlHits={controlHits.Count} ({string.Join(", ", controlHits)})");

            Assert.IsEmpty(offenders,
                $"no mesh-preparation source may name '{token}'. Unlit mode's hard constraint is that " +
                "the per-layer vertex/mesh layout is byte-identical between the Lit and Unlit twins, so " +
                "mesh preparation NEVER branches on render mode — each builder produces one layout and both " +
                "shaders consume it. A meshing/geometry/tile-pipeline file that can see the mode is one " +
                "edit away from forking the layout, which turns unlit from a material-set setting into a " +
                "parallel pipeline. Choose materials by mode outside the fence (Rendering/Materials, " +
                $"Rendering/Map, MapRenderer.App). Offenders: {string.Join(", ", offenders)}");
        }

        /// <summary>Strips block comments and then everything from <c>//</c> (which covers <c>///</c>) to end
        /// of line, so a doc comment explaining the constraint cannot trip the fence it documents. A grep
        /// guard, not a C# parser — same shape as <c>CoreAssemblyBoundaryTests</c>.</summary>
        /// <param name="text">The full source text of one <c>.cs</c> file.</param>
        /// <returns>The same text with comment spans removed.</returns>
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
    }

// GlobeFillVertexKey is enumerated FIELD BY FIELD, so a new GlobeFillVertex column is not picked up and
// two vertices differing only in it merge silently. This tooth makes that loud.

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeFillVertexKeySizeTests — GlobeFillVertexKey's hand-enumerated field coverage
    // ───────────────────────────────────────────────────────────────────────────────────

    public sealed class GlobeFillVertexKeySizeTests
    {
        /// <summary>The bytes GlobeFillVertexKey covers, field by field: World+Up+East (3 x double3),
        /// Tile (double2), Band (float3), Feature (int).</summary>
        private const int CoveredBytes = 3 * 24 + 16 + 12 + 4;

        [Test]
        public void EveryFieldOfTheVertexParticipatesInItsKey()
        {
            Assert.AreEqual(CoveredBytes, UnsafeUtility.SizeOf<GlobeFillVertex>(),
                "GlobeFillVertex's size no longer matches the fields GlobeFillVertexKey hashes. A column was " +
                "added or removed. The key is hand-enumerated, so it does NOT pick that up: extend " +
                "GlobeFillVertexKey's ctor, Equals and GetHashCode with the new column and update CoveredBytes. " +
                "Leaving it means two vertices differing ONLY in the new column merge into one — for the band " +
                "attribute that collapses the antialiasing skirt, with nothing failing to compile.");
        }
    }
}
