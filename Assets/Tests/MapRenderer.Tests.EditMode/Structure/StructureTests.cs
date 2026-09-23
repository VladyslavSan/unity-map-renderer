// Structure/StructureTests.cs — geometry/mesh-graph/job-scheduling structural fences: the neutral
// (format-free) geometry path, native buffer ownership across the MVT decode/materialize/symbol/line
// seams, the tile-processing dispatch fence, the fill mesh graph's job-scheduling shape, retired
// fill-pipeline symbols staying retired, the wall-job and ProjectPointsJob construction-site fences, the
// tile-build-graph per-kind-knowledge fence, the render-mode-neutral meshing fence, and the
// hand-enumerated GlobeFillVertexKey size guard. Unity EditMode only — reads source files under
// Application.dataPath. NOT registered in core-tests.csproj.
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
    /// Teeth <b>A</b> and <b>F</b> — the neutral geometry path carries no format, and the detached
    /// sidecar cannot come back.
    ///
    /// <para><b>Why the claim is shaped this way.</b> A tooth that asserts the neutral interface
    /// declares zero members is satisfiable by the very shape it means to prevent: a <b>sidecar</b>
    /// living beside it. So the tooth is the claim that makes a sidecar unavailable, not merely
    /// absent:</para>
    ///
    /// <list type="number">
    /// <item><b>Fenced by LOCATION, not by an allow-list of names.</b> Every production type whose name
    /// begins <c>Mvt</c>/<c>GeoJson</c>/<c>Mlt</c> must be declared under a decoder folder. An allow-list is
    /// what rotted last time — a new format-named type simply got added to it.</item>
    /// <item>No production type <i>outside</i> those folders may have a format-named type anywhere in a
    /// member signature.</item>
    /// <item>The neutral surfaces (<see cref="IFeature"/>, <see cref="ITileLayer"/>,
    /// <see cref="IDecodedTile"/>, <see cref="TileGeometryBuffers"/>) expose no member of
    /// array-of-integral (command-stream) shape.</item>
    /// </list>
    ///
    /// <para><b>Tooth F</b> is the same intent one level up: <c>ITileLayerProcessor.ProcessOnWorker</c> has
    /// exactly two parameters. Deliberately thin — its job is to make a future stage's "just pass a little
    /// sidecar through" cost a visible test edit.</para>
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
            // The GeoJSON *source* implementation, for the identical reason and in the identical place. It
            // must live in MapRenderer.Unity because ITileFeatureSource returns a UniTask, which
            // MapRenderer.Jobs does not reference — so "put it in a decoder folder" was not available, and
            // this entry is the placement being argued rather than assumed.
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

            // Non-vacuity, both halves: the scan really walked a corpus, and the matcher really fires on real
            // names. A reflection load that returned nothing, or a matcher that matched nothing, would report
            // "no offenders" just as loudly.
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
    /// Source-text structural disposal-discipline pins for the tile-geometry producer seam.
    ///
    /// <para>The graph's disposal discipline is a different mechanism (dispose NODES scheduled onto the
    /// job graph, not a <c>.Dispose()</c> call form in one function body) and is pinned elsewhere —
    /// <c>FillGraphOutput.DebugBuffersAllocated</c>/<c>DebugBufferDisposeNodes</c> pairing, exercised by
    /// the job-graph instrument tests.</para>
    ///
    /// <para>What this file pins: the MVT decode/materialize seam's ownership discipline —
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

        /// <summary>Re-point: <c>commands</c>/<c>featOffsets</c>/<c>featLengths</c> flipped from
        /// scratch <c>Materialize</c> minted and owned to a BORROWED constructor input —
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
        /// <c>Allocator.Persistent</c> native buffers and, since they are not handed off for the
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
        /// source" double-free guard, Model B), so its <c>finally</c>-block <c>tagWords.Dispose()</c> is a
        /// no-op on the success path and only actually frees the buffer if a LATER throw (there is none
        /// scheduled) landed between the adopt and return. This is exactly the
        /// "no IsCreated guard, Dispose() early-returns on default" idiom the geometry buffers already
        /// use.</para>
        ///
        /// <para><b>The two count-then-fill flattens live in one
        /// shared <c>FlattenFeatureColumn</c> helper, called twice.</b> The allocations this tooth pins
        /// (<c>new NativeArray&lt;int&gt;(featCount</c>) live
        /// in the HELPER, not the caller — so "an allocation made before <c>DecodeLayer</c>'s <c>try</c>
        /// leaks" is not provable by finding <c>try {</c> before an allocation token in
        /// <c>DecodeLayer</c>'s own body; that token is not there. The guard is re-expressed across
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

            // The named regression: dropping the null-on-transfer would leave `tagWords` non-default
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
    /// The structural teeth on the symbol consumer of Waist 1, plus the assembly-boundary fence
    /// the stage exists to establish.
    ///
    /// <para><b>The fused-<c>RingAssemblyJob</c> fence, symbol form.</b> Symbol has no polygon,
    /// hole, area or triangulation concept and rejects Polygon features outright, so every fill-assembly stage
    /// is off-limits to it. The behavioural half is impossible to write here (symbol never produces triangles),
    /// which is exactly why the fence is structural.</para>
    ///
    /// <para><b>Ownership is INVERTED.</b> Waist 1 does not <i>transfer</i> the buffer to
    /// <c>Extract</c> — <c>Extract</c> <b>BORROWS</b> it from the worker pass's <c>TileGeometryStore</c>,
    /// which lends the same instance to every symbol layer naming that source-layer and frees it at the end
    /// of the pass. So <c>Extract</c> must mint <b>zero</b> buffers and dispose <b>zero</b>. Freeing a
    /// borrowed buffer is <b>loud</b> on this path: the store lends an array-backed buffer whose
    /// <c>Dispose</c> frees three real <c>NativeArray</c>s, and a sweep measured that double free as
    /// <b>32 failures across 19 fixtures</b>, two of them with no line involvement at all — the
    /// heap-corruption signature. ("a mis-freed <c>AsArray()</c> view is a silent no-op" applies to the
    /// list-backed mode, which symbol never borrows.) The instrument is structural because it names the rule
    /// at the source and fails deterministically on the offending line, not because the behaviour goes
    /// unobserved.</para>
    ///
    /// <para>The one construction <c>Extract</c> may still make is its <b>private fallback store</b>, for the
    /// callers (tests, the demo path) that pass none. It is pinned at exactly one, outside every loop: a
    /// store built per feature would be the retired per-(layer, feature) mint, wearing a new name.</para>
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
        // "MvtGeometryMaterializer" and "GetOrMaterialize" are REPLACED here (not removed — a required set
        // with entries deleted is a disarmed test) by "tileLayer.Geometry", the expression the file reads
        // the borrowed buffer through.
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

            // The three store clauses are RETIRED WITH THEIR SUBJECT, not
            // dropped to make the file pass. They pinned (a) that Extract never disposes the caller's store,
            // (b) that it builds exactly one private fallback store, and (c) that the fallback is hoisted
            // above the try. There is no store: geometry belongs to the layer, so there is no borrowed
            // container to free, no fallback to hoist and no loop to hoist it out of. What the three of them
            // were collectively protecting — "this method mints nothing and frees nothing" — is stated
            // whole by the two zero-counts above, plus the clause below that the ONE remaining way to
            // reintroduce a private mint is absent.
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
        /// <c>Extract</c> holds three coordinate representations at once (<c>path</c>/<c>densePath</c>
        /// tile-local, <c>ups</c>/<c>lonLat</c> geodetic, <c>pathRender</c>/<c>anchor</c> projected), and the
        /// epsilons downstream are calibrated to tile-integer magnitude, so "just pass the projected one" is
        /// one wrong line away.
        /// <para><b>Stated limitation:</b> this is a call-form grep, not a C# parser — it pins the exact
        /// argument text of three call sites and nothing more. The differential oracle
        /// <c>SymbolPaths_FromTheSharedBuffer_MatchTheManagedDecodeOracle</c> does not reach these call sites:
        /// it transcribes the path read and never runs <c>Extract</c>'s consumers. So this grep is the fence's
        /// only direct instrument; the symbol suites that run <c>Extract</c> end to end cover it indirectly.</para>
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
    /// The structural teeth on the line consumer of Waist 1.
    ///
    /// <para><b>The fused-<c>RingAssemblyJob</c> fence.</b> <c>RingAssemblyJob</c> is a fused,
    /// <b>fill-only</b> stage: one pass applies fill's <c>rLen &lt; 3</c> filter, a degenerate-<b>area</b>
    /// filter, and exterior/hole classification. None of the three is meaningful for a line — a straight
    /// polyline has exactly zero area and would be dropped, and lines have no outer/hole concept at all. The
    /// behavioural falsifier is <c>StraightZeroAreaPolyline_StillRenders</c>; this is the structural one, and
    /// the two observe different things: a shortcut that reused the filter's <i>threshold</i> without naming
    /// the type would evade this file, and a shortcut that scheduled the job on a fixture whose geometry
    /// happens to survive would evade the behavioural one.</para>
    ///
    /// <para><b>Ownership is INVERTED.</b> The buffer is not transferred to the line builder —
    /// it is <b>BORROWED</b> from the pass-scoped <c>TileGeometryStore</c>, which lends the same instance to
    /// every style layer naming that source-layer. The builder must therefore mint <b>zero</b> buffers and
    /// dispose <b>zero</b>. A consumer that freed a borrowed buffer would free geometry its sibling layers are
    /// still reading, and on this path that second free is <b>loud</b>: the store hands back an array-backed
    /// buffer whose <c>Dispose</c> frees three real <c>NativeArray</c>s, and a sweep measured the double
    /// free as <b>32 failures across 19 fixtures</b> — two with no line involvement at all, the
    /// heap-corruption signature. (The "disposing it is a silent no-op" caveat belongs to the
    /// <c>AsArray()</c>-view, list-backed mode, which line never borrows.) This instrument is structural not
    /// because the behavioural signal is absent, but because it names the rule at the source and fails
    /// deterministically on the one offending line instead of as a scatter of unrelated red fixtures.</para>
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
        // "MvtGeometryMaterializer" left this set with the mint it named. Its replacements are the
        // identifiers of the mechanism the file uses NOW — the borrowed buffer type and the ordinal join —
        // so a gutted or re-pointed file still cannot satisfy the zero-counts trivially.
        //
        // RingFeatureIdx/FeatureGeometryType/RibbonJob/
        // RingOffsets retired from this SET (though the first three still appear in prose comments) — the
        // per-ring buffer read and the ribbon build both moved into the Burst job graph
        // (RingGatherJob/RibbonBatchJob, MapRenderer.Jobs/LineMeshGraph.cs), which this file no
        // longer touches by name; it schedules and completes the graph, then writes. The replacements name
        // THAT mechanism, so a gutted file still cannot satisfy the zero-counts trivially.
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
        /// Vestige sweep (ordinal 7): re-founded file-wide. The retired synchronous <c>WriteMeshData</c> was
        /// this tooth's whole subject — its per-ring loop was the only place a mint/dispose could have crept
        /// back in. <c>WriteMeshData</c> moved to the test assembly (zero production callers), so extracting
        /// its body is not meaningful; the fence now scans BOTH partial files that make up
        /// <c>StyledLineTileBuilder</c> (it is declared <c>partial</c> across
        /// <c>StyledLineTileBuilder.cs</c> and <c>StyledLineTileBuilder.WriteJob.cs</c>), comments stripped.
        /// This is STRICTLY STRONGER than the method-body version: a mint or a <c>geometry.Dispose()</c>
        /// ANYWHERE in the type now reds it, not only inside one method.
        /// </summary>
        [Test]
        public void LineBuilderMintsNoBufferAndDisposesNone_ItBorrows()
        {
            string body = StripLineComments(LineBuilderSource() + LineBuilderWriteJobSource());

            // Non-vacuity anchors, re-pointed from the retired method-body extraction: a wrong or gutted
            // scan would still need to explain away BOTH of these, present across the two files today.
            // LineMeshGraph.Schedule is NOT an anchor here — precondition 13 measured its one
            // occurrence to be INSIDE the method that moved out of production, so keeping it would red this
            // fence immediately against otherwise-correct code.
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

            // Three mint APIs exist on the buffer type: .Materialize(), TileGeometryBuffers.Allocate(...) and
            // TileGeometryBuffers.AdoptDerivedLists(...). Greping only the first would leave a per-layer
            // Allocate(...) clone (all-native, so also invisible to the runtime Is.Not.AllocatingGCMemory()
            // tooth) undetected — matched with the trailing '(' so both no-arg and arg'd call forms count.
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

        // WriteMeshData_StagesRibbonInNativeScratch_
        // NotManagedLists is RETIRED here, not "made to pass". Its subject was the cross-ring staging
        // accumulators (NativeList<LinePositionNormal>/<LineWidthColor>/<Vector2>) INSIDE
        // WriteMeshData's own per-ring loop, block-copied into the Mesh.MeshData at the end. B.5 deleted
        // that loop: the ribbon is now built by RibbonBatchJob (MapRenderer.Jobs/LineMeshGraph.cs) and
        // written straight into the Mesh.MeshData by LineStreamWriteJob — there is no cross-ring staging
        // step left anywhere for a managed List<> to sneak into.
        //
        // NOT because a managed List<> would fail to compile here: a `new List<T>()` local in WriteMeshData's
        // own (managed, non-Burst) C# body is perfectly legal — the deleted fence counted a token in a
        // METHOD BODY, not a field on a job struct, and even for a job struct the guard is a RUNTIME
        // reflection check at Schedule()/Run() (thrown only when Burst is actually invoked), not a compile
        // error. The real replacement coverage is a runtime measurement, not a compiler guarantee:
        // LineBuildAllocTests.WriteMeshData_OverAConstantStyle_AllocatesNoGCMemory (LineBuildAllocTests.cs:49)
        // is a live Is.Not.AllocatingGCMemory() check over this exact method, now routed through the graph —
        // Burst-toggle-independent (it measures the managed call site, not a job-compile artifact) and it
        // would catch a reintroduced managed List<> the same way it already catches any other GC-heap
        // regression on this path.

        // Vestige sweep (ordinal 7): WriteMeshData_DisposesNativeScratchViaUsing_NotHandRolledTryFinally is
        // RETIRED here, not re-founded. All five `using var` sites this tooth pinned were INSIDE the retired
        // synchronous WriteMeshData's own body — after it moved to the test assembly, this file has ZERO
        // `using var` sites left, so the tooth's own non-vacuity precondition
        // (`Assert.GreaterOrEqual(CountOccurrences(body, "using var "), 1)`) would fail on correct code if
        // re-founded file-wide. The `using`-based-disposal idiom left production entirely with the method
        // that used it; there is nothing left here to fence. (A weaker second reason also holds: BuildLayerInput's
        // deliberate `catch { …Dispose(); throw; }` would red a file-wide no-manual-`.Dispose()` clause — but
        // `finally {` is already zero file-wide and the `= default;` regex matches nothing, so this is not the
        // deciding reason, only a note against re-founding the clause as-is.) The property this tooth
        // protected — no handle stranded by a throw partway through construction — is now carried by the
        // language wherever a `using var` remains elsewhere in the codebase, and by the runtime
        // alloc/disposal suites (LineBuildAllocTests, DisposalLeakGuardTests) rather than by a structural
        // fence.

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
    /// Falsifiable acceptance tooth: a
    /// RED-verifiable structural pair proving <c>TileManager.KickMeshBuild</c> does not decode/dispatch
    /// directly — the shared decode + fan-out lives ONLY in
    /// <see cref="MapRenderer.Unity.Rendering.Tile.Processing.TileLayerProcessorRunner"/>.
    ///
    /// Against the pre-rewire source this test FAILS on both forbidden call forms (a direct
    /// <c>MvtDecoder.Decode(</c> and a direct mesh-write inside <c>KickMeshBuild</c>). An unused
    /// interface or a renamed local cannot pass: the assertions are over the CALL forms in the body.
    /// </summary>
    [TestFixture]
    public class TileProcessingStructureTests
    {
        private const string DecodeCallForm = "MvtDecoder.Decode(";
        // The runner's read of the caller-owned reference. `.Value` (SharedDisposable<IDecodedTile>,
        // successor to the earlier lease's `.Tile`) rather than `.GetOrDecode(` — the wrapper holds
        // an already-decoded tile, so reading it is a property access, not a call.
        private const string DecodeReadForm = "decode.Value";
        // `.WriteInto(` retired with the seam arm — the token
        // does not name anything a compiling program can contain, so the clause it fenced would be
        // unfalsifiable. Swapped, not deleted (first swap): `WriteMeshData(` became the direct-mesh-write
        // entry point a regression would now reach for, so fencing it continued to guard the SAME property
        // — KickMeshBuild delegates the mesh write entirely to the runner→graph pipeline, never calling a
        // builder's write method itself.
        //
        // `WriteMeshData(` was later retired for real — the synchronous public method moved to the test
        // assembly with zero production callers, so the token cannot appear in any compiling
        // PRODUCTION program and the clause it fenced would go unfalsifiable again. Swapped to
        // `TileBuilder.ScheduleWrite(` — the entry point a regression would now reach for.
        //
        // The token is `TileBuilder.ScheduleWrite(`, NEVER the bare `ScheduleWrite(`, and this is not
        // style: a bare `ScheduleWrite(` is a SUBSTRING of `CompleteMeasureAndScheduleWrite(` — the
        // CORRECT graph write kick, called from TileManager.cs outside KickMeshBuild's braces — and this
        // fence does not strip comments. `TileBuilder.ScheduleWrite(` matches all three
        // `Styled*TileBuilder.ScheduleWrite` call forms and cannot match the delegation call, so a future
        // "simplification" back to the bare token would silently red this fence the day the write kick
        // moves inside KickMeshBuild.
        private const string ScheduleWriteCallForm = "TileBuilder.ScheduleWrite(";
        private const string RunnerCallForm = "TileLayerProcessorRunner.RunWorkerPass(";

        // Anchors the method DEFINITION (return-type-prefixed), not one of KickMeshBuild's several call
        // sites elsewhere in TileManager.cs (which read just "KickMeshBuild(...)" with no return type
        // before them) — this substring is unique to the signature.
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

        /// <summary>The decode moved out of
        /// the runner into the caller-owned handle, and further still, to the source's
        /// <c>GetTile</c>. <c>RunWorkerPass</c> therefore decodes nothing and reads the caller's reference
        /// exactly once, via <c>decode.Value</c>. The invariant this test guards — the mesh
        /// cadence's OWN decode-once boundary — survives as "reads the lease exactly once, decodes nothing
        /// directly". There is no source-less worker pass — a background tile schedules its measure graph
        /// directly, with no runner entry of its own — so this test has no sourceless half to check.</summary>
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

        /// <summary>The primary structural delegation tooth (RED-verified
        /// against the previous <c>SymbolSubsystem</c>): the subsystem does not decode, extract, or
        /// shape symbols itself — that machinery lives in the processor contract. Against the un-rewired
        /// source (decode at <c>BuildTileAsync</c>'s old <c>:324</c>, <c>ExtractLayers</c> at <c>:325</c>,
        /// <c>ShapeAsync</c> at <c>:333</c>, no runner call) this test FAILS on all four forms — adding the
        /// interface/processor as an unused façade while <c>BuildTileAsync</c> keeps its inline decode/
        /// extract/shape (the "rename" non-implementation) cannot pass.
        /// <para>Glyph-fetch hoist: <c>ShapeAsync</c> was renamed to the synchronous <c>Shape</c>, so the
        /// call-form assertion checks BOTH <c>".ShapeAsync("</c> (must stay dead — the identifier does not
        /// exist) AND <c>"_builder.Shape("</c> (must also be absent — the shape loop still runs entirely
        /// inside <c>TileSymbolLayerProcessor.CompleteOnMain</c>, never called directly by the subsystem).
        /// Anchored on the concrete call form, not a bare <c>".Shape("</c> — the bare form would also ban the
        /// identifier from appearing in PROSE (a comment or XML doc explaining why it is not called), which
        /// is not what this tooth is checking. The two forms are independently meaningful:
        /// <c>".ShapeAsync("</c> does not contain <c>"_builder.Shape("</c>.</para></summary>
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

        /// <summary>The sole production <c>MvtDecoder.Decode(</c> call lives OUT of
        /// <c>MapRenderer.Unity</c> entirely, into <c>MvtTileDecoder</c>. The assembly-wide count under
        /// <c>MapRenderer.Unity</c> (including <c>TileDecodeDispatch.cs</c>, the sole permitted file) is ZERO
        /// — the decode left the assembly, it did not just move within it. The seam later moved again,
        /// out of <c>MapRenderer.Core</c> and into <c>MapRenderer.Jobs</c>; see
        /// <see cref="MapRendererJobs_DecodesMvtOnlyInsideMvtTileDecoder"/> for the positive half (together
        /// the two prove the sole-production-decode-site invariant across BOTH assembly moves).</summary>
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

            // TileDecodeDispatch.cs specifically: it reads through the injected ITileDecoder, not MvtDecoder
            // directly, and it is the ONE place a decode
            // is dispatched at all — the successor to SharedTileDecode.GetOrDecode's single call.
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

        /// <summary>The positive half of the sole-decode-site proof: the ONE production
        /// <c>MvtDecoder.Decode(</c> call site lives in
        /// <c>Tiles/ITileDecoder.cs</c> (<c>MvtTileDecoder.Decode</c>) and nowhere else — and
        /// <c>MapRenderer.Core</c> decodes <b>nowhere</b>. The positive count
        /// (not just a zero-elsewhere scan) proves the decode actually landed there, not merely that the
        /// assemblies it left went quiet. Widening this scan to <c>Assets/Code</c> would sweep the TEST
        /// assembly too, where <c>DecodeTests</c>/<c>MvtPropertyDecodeTests</c> legitimately call
        /// <c>MvtDecoder.Decode(</c> for fixture setup — so the per-assembly assertions (this one +
        /// <see cref="MapRendererUnity_DecodesMvtNowhere"/>) are narrower than one combined
        /// scan. RED on both clauses before the move: the file lived under Core, so the Jobs count was 0 and the Core
        /// scan found it.</summary>
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

        /// <summary>WriteInto-path neutralization, structural. The
        /// fill/line/symbol fan-out — from feature selection through mesh/symbol build — references NO MVT
        /// carrier type (<c>MvtTile</c>/<c>MvtFeature</c>/<c>MvtLayer</c>/<c>MvtDecoder</c>) by name; only the
        /// neutral <c>IDecodedTile</c>/<c>ITileLayer</c>/<c>IFeature</c>/<c>ITileDecoder</c> surface.
        /// Each of these 15 files would otherwise name an MVT carrier type. The set is
        /// still 15 files, re-derived not re-asserted — the symbol extractor moved from
        /// <c>MapRenderer.Core/Style/Symbol/</c> to <c>MapRenderer.Unity/Text/</c>, and
        /// <c>FeatureSelector</c> and <c>SourceLayerResolver</c> from <c>MapRenderer.Core</c> to
        /// <c>MapRenderer.Jobs/Tiles/</c>; none was added or removed by either.
        ///
        /// <para><b>Anti-vacuity, REPLACED.</b> A clause requiring the bare substring
        /// <c>MvtGeometry</c> in each codec file's RAW text has two failure modes: a rename makes
        /// <c>new MvtGeometryMaterializer(</c> satisfy it by substring on the line file, and counting
        /// comments lets an XML <c>see cref</c> satisfy it on the symbol file even after the last real
        /// <c>MvtGeometry.Decode</c> call is gone — so it cannot detect a gutted file. Tightening it to a
        /// word-boundary <c>\bMvtGeometry\b</c> does not fix this either, because its premise (<i>these
        /// files still decode geometry</i>) is expiring by design as consumers move onto the shared
        /// buffer: that form would go RED on the line file for a legitimate reason.
        /// It is therefore RE-POINTED at what is still true — a per-file REQUIRED-IDENTIFIER SET naming the
        /// geometry mechanism each file actually uses (matched comment-stripped, with word boundaries, so
        /// neither prose nor a longer identifier containing the token can satisfy it) plus the explicit
        /// negative that the retired decoder's bare name is gone. RED-verified against a gutted line file, a
        /// gutted symbol file, and a symbol file whose tokens survive only inside a block comment.</para></summary>
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

            // Anti-vacuity — REPLACED; see the XML doc above for why a substring
            // clause cannot detect a gutted file. Each geometry-obtaining file must name every
            // identifier of the mechanism it actually uses today, matched on comment-stripped text with word
            // boundaries.
            (string relativePath, string[] required)[] mechanisms =
            {
                // Both files stopped minting and now BORROW the source-layer buffer, so
                // "MvtGeometryMaterializer" is not an identifier either of them uses — it was REPLACED
                // (not dropped: a required-set with entries removed is a disarmed test) by the identifiers of
                // the mechanism they use instead. The store is gone too,
                // so symbol's `GetOrMaterialize` is replaced by `tileLayer` — the object it now reads the
                // buffer off. `Geometry` alone would be a poor token (it is a substring of
                // TileGeometryBuffers and matched with word boundaries would still be weak); `tileLayer` is
                // the identifier that only exists because the LAYER owns the geometry.
                //
                // RingFeatureIdx/RingOffsets/RibbonJob
                // retired from the LINE row (they survive only in prose comments now, stripped here) — the
                // per-ring buffer read and the ribbon build moved into the Burst job graph
                // (MapRenderer.Jobs/LineMeshGraph.cs), which StyledLineTileBuilder.cs does not name; it
                // schedules and completes that graph instead. Replaced by identifiers of THAT mechanism —
                // SymbolFeatureExtractor.cs is untouched by this stage and keeps its original set.
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

        /// <summary>Tooth 1 — the primary
        /// structural tooth): <c>TileManager</c> must be byte-agnostic — it names none of the five
        /// byte-centric tokens the pre-raise coordinator used (<c>TileResponse</c>/<c>IDataSource</c>/
        /// <c>TileScheduler</c>/any concrete lease type/standalone <c>TileCache</c>), and DOES name the raised
        /// seam (<c>ITileFeatureSource</c>/<c>SharedDisposable</c>). Genuinely RED before the raise: the
        /// file named all five tokens throughout.
        ///
        /// <c>TileCache</c> uses a WORD-BOUNDARY match, not the plain substring <see cref="CountOccurrences"/>
        /// every other tooth in this file uses — <c>TileManager.cs</c> keeps 19 <c>PreparedTileCache</c>
        /// references (the <c>_prepared</c> mesh/layer cache, left UNTOUCHED), and
        /// the substring <c>"TileCache"</c> embeds inside <c>"PreparedTileCache"</c> (…d|T… — both word
        /// characters, no boundary) — so a plain substring count would find those 19 kept references and could
        /// NEVER go green even after a flawless raise. <c>\bTileCache\b</c> excludes <c>PreparedTileCache</c>
        /// while still catching every standalone <c>TileCache</c> mention. The other four tokens have no
        /// such embedding collision, so they use the plain substring matcher.
        /// <para>The pipeline registry (<see cref="SourceRegistry"/>) is exactly the code
        /// that would plausibly reach for a byte fetcher, so the five forbidden-token checks now cover it
        /// too. The required-token check for <c>ITileFeatureSource</c> is scoped to <c>SourceRegistry.cs</c>
        /// alone — it holds all four remaining references; <c>TileManager.cs</c>'s own two are on a
        /// pass-through factory field, so anchoring there is one rename from vacuous.</para></summary>
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

        /// <summary><c>SourceRegistry</c>'s public surface is EXACTLY the ten members the
        /// plan enumerates — no getter-per-field creep, no <c>SourcePipeline</c> escaping. Reflection-based,
        /// not text-scraped: a grep on indentation over/undercounts (the private nested <c>SourcePipeline</c>
        /// class's own fields sit at the same indent and inflate a naive count to 18).
        /// <para><b>The bound has zero headroom by design.</b> An eleventh member is a finding that the seam
        /// failed, not a number to bump.</para></summary>
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

            // Matches SourcePipeline itself, an `out`/`ref` byref (`&`), an array, or any type nested inside
            // a generic (e.g. IReadOnlyList<SourcePipeline>) — not just a bare-name match, which a widened
            // (internal) SourcePipeline could dodge with any of those forms.
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

        /// <summary>Pins the off-PlayerLoop invariant
        /// <c>DrainMeshBuilds</c>'s spin-on-<c>IsCompleted</c> depends on — <c>MvtTileFeatureSource.GetTile</c>
        /// must introduce NO main-thread hop (completion stays off the PlayerLoop, supplied by the decode hop
        /// on the <c>HasData</c> path and by synchronous/inline completion otherwise — not by the fetch
        /// scheduler, which introduces no thread-pool hop of its own), or the drain-spin would never observe
        /// completion without pumping the PlayerLoop (the exact <c>KickMeshBuild</c> hazard this mirrors).
        /// Lightweight/structural — the behavioural net is the <c>DrainMeshBuilds</c> snapshot/leak exercise
        /// elsewhere in the suite.</summary>
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

        /// <summary>Rename-complete structural tooth: asserts zero occurrences,
        /// anywhere under <c>Assets/Code</c> or <c>Assets/Tests</c>, of the retired geometry-type enum's old
        /// name (the concatenated form built from "Mvt" + "GeometryType" — the renamed geometry-type enum
        /// now lives as <c>Core.Tiles.TileGeometryType</c>). Genuinely RED pre-rename: the old name was
        /// scattered across 25 files (Core/Unity/Tests). Self-reference trap: this test's OWN source file
        /// lives under <c>Assets/Tests</c>, one of the swept roots, so the search token is built by
        /// concatenation rather than written as a literal — writing the literal here (even in this very doc
        /// comment) would self-trip the assertion. Positive half: the new dedicated type file exists and the
        /// type resolves in <c>Core.Tiles</c> (compiling this test file at all proves the second half).</summary>
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
        //
        // Three clauses, one per funnel. Each pins a CHOKEPOINT rather than a call
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

        /// <summary>Funnel 1 (the record): <c>TileManager.DoDispose</c> must tear records down through
        /// <c>RenderTeardownRecord</c>, the one funnel cover-change, eviction and restyle already use — not
        /// through hand-rolled loops of its own. Genuinely RED against the previous source, where
        /// <c>DoDispose</c> walked <c>_loaded</c> THREE times (spin+dispose mesh builds, cancel+observe
        /// fetches, destroy meshes), called <c>DestroyTrackedMeshes</c> directly and never called
        /// <c>RenderTeardownRecord</c> at all. Comment-stripped, so the prose above the loop cannot satisfy
        /// any clause.
        ///
        /// <para><b>Strengthened.</b> The first form
        /// of this test counted call TEXT, so three real bypasses passed it: guarding the funnel call with
        /// <c>if (lt.Built)</c>, skipping records with a <c>continue</c>, and moving the sole
        /// <c>_loaded.Clear()</c> BEFORE the pass. Each left every count unchanged while dropping records
        /// whose in-flight fetch / mesh build was never stashed in the pens. The counts are now
        /// backed by <see cref="AssertUnconditionallyReachedOnce"/> (brace depth + statement start + no
        /// earlier jump) and by an ORDERING comparison on character offsets. All three bypasses are
        /// RED-verified against this form.</para></summary>
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
        /// unconditionally, and <c>_sources.Rebuild</c> must follow that clear block.
        /// <para><b>The clear-is-unconditional clause is the one that matters.</b> RED-verified (a
        /// ordering-inversion injection compiles and runs green against the BEHAVIOURAL test
        /// <c>RemovedSource_TilesDoNotSurviveARestyle</c>): the hazard — a stale slot-keyed
        /// entry outliving a re-slot — is prevented by these five clears being unconditional, not by
        /// their order relative to <c>Rebuild</c>. The ordering IS observable at runtime: <c>Rebuild</c>
        /// calls caller-supplied code twice while mid-rebuild (<c>SourceSpec.CreateSource</c> for a new
        /// pipeline, <c>ITileFeatureSource.Dispose</c> for a removed one), and
        /// <c>SourceRegistrySlotInvariantTests.Rebuild_CallerFactoryObservesLoadedClearedFirst</c> pins that
        /// window behaviourally through the former hook. This structural check stays alongside it because
        /// that hook only fires for a NEW or REMOVED pipeline — it says nothing about whether an
        /// unchanged pipeline's own clear is unconditional, which is this clause's whole job.</para>
        /// <para><b>Clause 1 is conservative by design.</b> "Unconditionally reached" rejects ANY earlier
        /// jump token (<c>return</c>/<c>continue</c>/<c>break</c>/<c>goto</c>) at any nesting depth, even
        /// one that plainly does not make the clear conditional (a <c>break</c> closing an unrelated loop
        /// above it). A future refactor that legitimately moves a jump-containing block above the clears
        /// will red this tooth with nothing actually broken — check the jump's owning loop before assuming
        /// a regression.</para>
        /// <para><b>Clause 2 is currently unreachable, RED-verified twice over, and kept.</b> The
        /// <c>hasBackground</c> loop's <c>break</c> always precedes <c>_sources.Rebuild</c>, so any clear
        /// moved after <c>Rebuild</c> trips clause 1 first — confirmed by injecting the ordering inversion
        /// on <c>_loaded.Clear()</c> and again, isolated, on <c>_desiredSet.Clear()</c>; both reds landed
        /// on clause 1, never clause 2. Clause 1 is what actually enforces the ordering today. Clause 2
        /// stays as defence-in-depth: if a later refactor moves the <c>hasBackground</c> computation into
        /// a helper call, that <c>break</c> disappears from this body and clause 2 becomes the only
        /// guard.</para></summary>
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
        /// handing a decode handle back to its CALLER. <c>DiscardFetchOutcome</c> returns
        /// <see langword="void"/>, so no discard SITE can bind, retain or re-consume what the fetch produced
        /// — caller-side ownership is compiler-enforced, not conventional. The two discard sites must call
        /// it and must NOT call the owning arm (<c>TileManager.TakeDecodeFromFetch</c>, a different file —
        /// see below). Genuinely RED against the previous source, which had ONE method,
        /// <c>ObserveFetchOutcome(req, logErrors:)</c>, returning the handle to all four callers — two of
        /// which threw it away.
        ///
        /// <para><b>Relocated.</b> The fetch pen and its discard funnel moved from
        /// <c>TileManager</c> into <see cref="PendingDisposalQueue"/> — same method, same invariant, new
        /// home. <c>TakeDecodeFromFetch</c> stayed behind on <c>TileManager</c>, so the "must not call the
        /// owning arm" clause is checked by absence rather than by reading a sibling file.</para>
        ///
        /// <para><b>Scope, narrowed.</b> <see langword="void"/> constrains
        /// the CALLER and nothing else: it does not stop <c>DiscardFetchOutcome</c>'s own body from
        /// retaining, re-publishing or simply forgetting what it observed. The body half is pinned
        /// separately below — the funnel must actually observe the outcome, so an EMPTY body fails (arm 2
        /// REQUIRED) — and the runtime release obligation hung here belongs to the leak teeth
        /// (<c>EagerDecodeOwnershipTests</c>), not a claim this text oracle makes.</para></summary>
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

            // The funnel's OWN body, not just its call sites (arm 2 REQUIRED): the whole point of routing
            // every abandonment through one method is that the method DOES something, and an empty body
            // passes every call-site clause above while re-opening the console flood. The observation
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
                "while leaving the abandoned task's fault unobserved — the UnityWebRequestException " +
                "console flood, back through the method that exists to prevent it.");

            // File-scoped, not per discard-site: TakeDecodeFromFetch stayed on TileManager, so a
            // CALL from here is impossible, but a COPY of its body pasted into this file is not — this
            // absence check is what actually rules that out.
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
        /// <c>PumpBuilds</c>' live drain, which CONSUMES entries rather than discarding them and is a
        /// different job. Genuinely RED against the previous source, which had three: the live drain plus two
        /// bare <c>while (TryDequeue(out _)) { }</c> loops in <c>SetStyle</c> and <c>DoDispose</c>, i.e. two
        /// future drop paths where there should be one.
        ///
        /// <para><b>Strengthened.</b> The per-site call clause counted
        /// text, so <c>if (cond) DrainAndDiscardParkedBuilds();</c> passed it. It now goes through
        /// <see cref="AssertUnconditionallyReachedOnce"/>.</para></summary>
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
        /// Funnel 4, updated for the WorkScheduler migration: the parked drain must dispatch through
        /// <c>WorkScheduler.Schedule</c>, and both of its ownership-guard releases must sit in a <c>finally</c>
        /// rather than a trailing statement.
        ///
        /// <para><b>What is NOT here, and why.</b> A count of
        /// <c>DecodeRef.Dispose()</c> spellings, justified by "the ct-drop is unreachable from a
        /// test", would be wrong on both counts: a count cannot tell a release that runs from one that is guarded, nested
        /// or jumped over, and the ct-drop IS reachable — reflecting the private CTS and cancelling WITHOUT
        /// draining produces exactly the interleaving a pool-vs-main race produces, which is what
        /// <c>SymbolParkedRedecodeTests.AParkedEntryCancelledWithoutADrain_…</c> now drives. The
        /// pre-handoff guard has a runtime tooth too
        /// (<c>AParkedEntryWhoseWorkerCannotBeBuilt_StillReleasesItsReference</c>), and the post-drain
        /// enqueue race — now made STRUCTURALLY impossible by the atomic park, so its runtime tooth is
        /// <c>AParkBlockedByTheAbandonDrainGate_AcquiresNothingUntilItHoldsTheGate</c>, the successor to the
        /// retired <c>AParkEnqueuedAfterItsCancellersDrain_…</c>. What is left here is the residue no
        /// runtime test can see.</para>
        ///
        /// <para><b>The old <c>cancellationToken:</c> clause retired, not weakened.</b> Under
        /// <c>UniTask.RunOnThreadPool</c>, passing <c>cancellationToken: captured.Ct</c> made UniTask skip the
        /// delegate entirely — and the delegate was the only release, so a skip would leak. Under
        /// <c>IWorkScheduler.Schedule</c> that hazard does not exist: the interface's own contract is that a
        /// body is never skipped based on its token (poll, not push — <c>IWorkScheduler.cs</c>), which
        /// <c>WorkSchedulerContractTests</c> pins directly. So the dispatch here MAY pass <c>captured.Ct</c> —
        /// it is inert for skip purposes, kept only for symmetry with <c>KickMeshBuild</c>'s idiom — and the
        /// in-lambda ct check is what actually guards the work, exactly as it did before.</para>
        ///
        /// <para><b>RED injections:</b> revert the dispatch to <c>UniTask.RunOnThreadPool</c>; or move either
        /// guard release out of its <c>finally</c> into a trailing statement.</para>
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
            // The dispatch must reach the INJECTED policy, so the body may not mint a scheduler of its own:
            // `new InlineWorkScheduler().Schedule(` hard-codes the policy while still reading like a dispatch,
            // and the substring check below is unanchored enough that a type merely NAMED *WorkScheduler would
            // satisfy it. Both construction forms are therefore banned outright.
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
            // This logic moved OFF RunWorkerAndHandoff and into TryParkBuild — the one gated site (see
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
        /// Both mouths that touch the parked
        /// queue's ownership — <c>TryParkBuild</c>'s ct-check+acquire+enqueue and
        /// <c>DrainAndDiscardParkedBuilds</c>'s dequeue+dispose — must lock the SAME <c>_parkGate</c>,
        /// unconditionally, or the exclusion the runtime rendezvous tooth
        /// (<c>SymbolParkedRedecodeTests.AParkBlockedByTheAbandonDrainGate_…</c>) proves is decorative: that
        /// tooth only observes the PARK side of the exclusion (it asserts the park has not acquired while the
        /// gate is held) — a drain that silently stopped taking the gate would still leave it green, because
        /// nothing in that tooth exercises the drain's own critical section concurrently with a park. This
        /// pin is what covers the residual gap accepted rather than adding a second runtime
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
        /// <c>KickMeshBuild</c> ACQUIRES its OWN reference in its
        /// main-thread prologue — it does not borrow the caller's and transfer it into the pool lambda —
        /// so it must release exactly the token it acquired, from that lambda's <c>finally</c>, and NEITHER
        /// caller may null the record's field around the call any more.
        ///
        /// <para><b>This tooth inverted again, and the inversion is the point.</b> The earlier form of this
        /// tooth demanded the caller null <c>lt.Decode</c> AFTER the call — the transfer-completes-on-return
        /// contract. The transfer is retired altogether: the record keeps its own reference for its WHOLE
        /// in-cover lifetime now (see the <c>LoadedTile.Decode</c> field doc), and
        /// <c>RenderTeardownRecord</c> (funnel 1) is its only release, kicked or not. A surviving
        /// <c>lt.Decode = null;</c> at either kick call site is the retired transfer shape leaking back in —
        /// it would desync <c>FetchCompleted &amp;&amp; Step != BuildStep.Prologue &amp;&amp; Decode == null</c> from the
        /// kicked state and re-observe the record's PRESERVED fetch task on the next tick.</para>
        ///
        /// <para>The body's `finally { decode.Release(); }`
        /// is gone — on SUCCESS the reference now TRANSFERS to the returned <c>TilePrologueOutput</c> (freed
        /// once handed to <c>TileBuildGraph.ScheduleMeasure</c>, or by a pen's <c>TilePrologueOutput.Dispose()</c>
        /// if it never gets that far); on a FAULT it is released by a guard `catch`. Both schedulers always
        /// run the body, so "transferred or released" is exhaustive — the two releases (the body-fault catch
        /// and the hand-off-fault catch around <c>IWorkScheduler.Schedule</c>) are now the SAME shape, one
        /// regex match each.</para>
        ///
        /// <para><b>RED injections:</b> re-add a caller-side <c>lt.Decode = null;</c> after either kick call;
        /// remove the prologue <c>decode.Acquire()</c> (a leak-balance regression the runtime
        /// <c>EagerDecodeOwnershipTests</c> teeth catch, not this one); or drop the `output.Decode = decode;`
        /// transfer on the success path (a leak — nothing frees the kick's reference at all); or remove
        /// either guard <c>catch</c> (a leak on that fault path).</para>
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
        /// The mesh-build kick migration: both <c>KickMeshBuild</c> and <c>KickSourcelessBackground</c> must
        /// dispatch through the injected <c>IWorkScheduler</c>, never <c>UniTask.RunOnThreadPool</c> directly
        /// — the WebGL fix (docs/web-target.md), since <c>RunOnThreadPool</c> never runs its delegate
        /// on a web player (no managed background threads). Structural companion to the runtime
        /// <c>InjectedScheduler_Runs…OnTheCallingThread</c> teeth, which prove the POLICY is honored; this one
        /// proves the PLACEMENT — that both kick sites reach the scheduler at all, not just one of them.
        ///
        /// <para>The <c>.Preserve()</c> check is scoped to the two kick bodies, not the whole file: the
        /// fetch's own <c>.Preserve()</c> (bucket B — <c>AdmitTile</c>) is untouched by this stage and must
        /// survive.</para>
        ///
        /// <para><b>RED injection:</b> revert either kick's dispatch back to
        /// <c>UniTask.RunOnThreadPool(…, configureAwait: false).Preserve()</c> — genuinely RED today (both
        /// kicks still use it pre-migration).</para>
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
            // The source-less
            // background kick left the seam entirely — it schedules FillMeshGraph directly and reaches no
            // IWorkScheduler.Schedule<T> call (tooth (e), MeshBuildWorkSchedulerTests). Only KickMeshBuild
            // (source tiles) still dispatches through the seam.
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
        /// <para><b>Structural, and this one really is.</b> <c>Release()</c> throws on an unbalanced release
        /// and <c>IDecodedTile.Dispose()</c> can throw, so the order decides whether the local is left
        /// holding a handle whose reference is already gone. No runtime tooth can see it: every caller
        /// passes a struct COPY and none writes it back, so <c>lt.Decode = null</c> is discarded even on the
        /// happy path — and <c>MethodInfo.Invoke</c> does not copy a by-ref argument back when the callee
        /// throws, so reflecting into the method directly cannot read the difference either (measured, not
        /// assumed). What is pinned here is the funnel's own discipline.</para>
        ///
        /// <para><b>Caller-side residue.</b> The two direct callers are
        /// <c>TileManager.RemoveAndTeardownRecord</c>, which drops the record from <c>_loaded</c> before
        /// calling this, and <c>DoDispose</c>, which does not. A throw mid-loop still abandons the records
        /// the loop never reached — that half stays open, in
        /// `docs/per-layer-tile-processing-design.md` § "Recorded limitations — two things no test can
        /// observe".</para>
        ///
        /// <para><b>RED injection:</b> swap the two statements back to
        /// <c>lt.Decode?.Release(); lt.Decode = null;</c>.</para>
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
        /// (inclusive of its outer braces) beginning at the first '{' at or after
        /// <paramref name="searchFrom"/>. <paramref name="blockEndIndex"/> is the index of the block's
        /// closing brace in <paramref name="source"/> — the ordering clause compares against it, since
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
        /// the whole enclosing method (arm 1 NIT 4: a token clause forbidding
        /// <c>cancellationToken:</c> anywhere in <c>PumpBuilds</c> would trip on an unrelated
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
        /// occurrence count does not give (four real bypasses passed the
        /// counts). Three conditions, all required:
        /// <list type="bullet">
        /// <item>brace depth 1 relative to <paramref name="block"/>'s own outer braces — so it is not nested
        /// inside an <c>if</c>/<c>try</c>/loop block of its own;</item>
        /// <item>it STARTS a statement (nearest preceding non-whitespace character is <c>;</c>, <c>{</c> or
        /// <c>}</c>) — which is what catches a BRACELESS guard, <c>if (c) Funnel();</c>, whose depth is
        /// still 1 and which the depth check alone therefore cannot see;</item>
        /// <item>no earlier <c>return</c>/<c>continue</c>/<c>break</c>/<c>goto</c> anywhere in the block can
        /// jump past it — arm 1's recorded gap, a <c>continue</c> that skips a record while keeping the call
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
        /// whether the per-record obligation inside the funnel is honoured, which is the runtime leak teeth
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
        /// <see cref="WriteIntoPath_ReferencesNoMvtCarrierTypes"/> must not be satisfiable by prose (the
        /// finding: the old clause passed on an XML <c>see cref</c> after the real call was gone). Additive:
        /// <see cref="StripLineComments"/> is shared by five other assertions and is not
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


    // ───────────────────────────────────────────────────────────────────────────────────
    // FillMeshGraphStructureTests — FillMeshGraph's source shape: no Complete/stale .AsArray()/.Run/IWorkScheduler
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The structural complement to
    /// <c>FillMeshGraphSchedulingTests</c>'s not-completed-at-return tooth. That behavioural tooth cannot
    /// false-red on a tiny fixture (an unflushed job cannot have started), but for the same reason it proves
    /// only that nothing completed synchronously THIS RUN — it cannot see a <c>Complete()</c> call that
    /// happens to be a no-op on the fixture used. This file pins the SOURCE shape instead: no
    /// <c>Complete()</c>, no schedule-time <c>.AsArray()</c> (the stale-length hazard the graph's own doc
    /// names), no <c>.Run(</c>, no <c>IWorkScheduler</c> — and the required tokens guard against a renamed or
    /// gutted file trivially satisfying every "count is zero" claim (the same idiom
    /// <c>StyledLineBuilderStructureTests</c> uses).
    ///
    /// <para><b>Two identifier pins retired here, and what covers them now.</b> A prior version of
    /// <see cref="RequiredTokens"/> pinned the literal join call that closed the dependency-bug FillMeshGraph
    /// once shipped (<c>FillRingCountJob</c> writing the shared <c>Counts</c> array with no edge to the job
    /// that wrote it next), then — after that job was deleted — a literal <c>.Schedule(node3Handle)</c>
    /// match, then <c>Dispose(node7Handle)</c>. Both were pins on a LOCAL VARIABLE'S spelling, which breaks on
    /// a rename rather than on a regression — the weakest form of the check they stood in for. Neither is
    /// re-pinned under FillMeshGraph.cs's current local names. What covers each instead:
    /// <list type="bullet">
    /// <item><description>The missing-edge class of bug (a job scheduled with no dependency on the job that
    /// wrote a container it reads) is caught by Unity's own job safety system at <c>Schedule</c> — every
    /// <c>FillMeshGraphParityTests</c>/<c>FillMeshGraphGlobeParityTests</c> run schedules the REAL graph over
    /// real fixtures with the Editor's job debugger on, so a dropped edge throws
    /// <c>InvalidOperationException</c> there, not merely on a source-text pin.</description></item>
    /// <item><description>The dispose-node-per-column claim is covered behaviourally by
    /// <c>FillMeshGraphSchedulingTests.Dispose_ReturnsLiveOutputsToBaseline_AndBufferDisposeNodesPairWithAllocations</c>/
    /// <c>..._OnACurvedLayer</c>, which assert the allocate/dispose-node counters balance — a real per-container
    /// count, not a grep for one call site.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    [TestFixture]
    public class FillMeshGraphStructureTests
    {
        // Regex, not a literal — "Complete()" as a bare string misses "Handle.Complete ()" or a
        // line-wrapped call. `\.Run\(` stays a plain substring below; a synchronous dispatch site would not
        // plausibly be reformatted to dodge it, and IWorkScheduler/.AsArray( are identifiers/exact API calls.
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
            // job-scheduling-design.md's rule 2: EarcutBatchJob has no loop of its own to bound, so neither
            // FillSizingJobTests nor any other RED tooth reds if its deferred-count SOURCE regresses from
            // buffers.PerPolyOuterCount (a sizing-owned column) to a borrowed count. This literal pins that
            // source structurally — the only thing that reds on that regression.
            "Schedule(buffers.PerPolyOuterCount, EarcutPolygonBatch, gathered)",
            // Two identifier pins are retired (a literal join call, then a literal
            // `.Schedule(node3Handle)`/`Dispose(node7Handle)` local-name match): a pin on a LOCAL VARIABLE'S
            // spelling is the weakest form of the check it stood in for, and the only one that breaks on a
            // rename rather than a real regression. What each guarded is now covered behaviourally instead —
            // see this file's own type doc.
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

        // ── The attribute fence — job-scheduling-design.md's rule 2: "no [NativeDisableContainerSafetyRestriction]
        // in a graph builder EVER." Scoped to the
        // two directories the design names as where graph builders and stream-write jobs live — not one
        // hand-listed file, so adding a job anywhere under either directory is covered. The design
        // sanctions EXACTLY ONE occurrence of NativeDisableParallelForRestriction in EACH of two files:
        // EarcutBatchJob.cs and RibbonBatchJob.cs, each a
        // field-level attribute on its single Buffers field. No other file, and no other count.
        // NativeDisableContainerSafetyRestriction stays forbidden with NO exceptions, anywhere, always.
        //
        // The exception is a (file → token → count) triple, not a bare filename: a bare filename would exempt
        // that file from EVERY forbidden token, at ANY count — which would have let NativeDisableContainerSafetyRestriction
        // through on EarcutBatchJob.cs too, the exact vacuity a bare-filename allowlist invites. This shape
        // was never live before this stage — the array was always empty, so the skip path had never executed.
        //
        // Matches the BARE identifier, not the bracketed short form: a fully-qualified attribute
        // (Unity.Collections.LowLevel.Unsafe.NativeDisableContainerSafetyRestriction) or an aliased one
        // carries no "[...]" literal and would silently pass a match on the short form — caught RED-verifying
        // this fence, when the fully-qualified form stayed green. The identifier cannot appear in compiling
        // C# except as this attribute (fully qualified, short form, or aliased), so the bare match is both
        // simpler and strictly more complete than the bracketed one it replaces.

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

        /// <summary>Every entry pins a COUNT derived from the field list it corresponds to, not read off the
        /// file as "whatever it says today" — a pin that merely records the current count detects a LATER
        /// widening but blesses today's. EarcutBatchJob.cs's single <c>Buffers</c> field carries exactly one
        /// occurrence: red here means a written column was added to or removed from
        /// <see cref="EarcutBatchJob"/> in a way that changed its field shape — update this with intent,
        /// never to whatever the file now says.</summary>
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

            // Found-in-enumeration guard: a renamed sanctioned file would otherwise make its exception a
            // silent no-op that still reads green (the same vacuity class this file already guards for
            // FillMeshGraph.cs above).
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

        // ── The JobsDebugger precondition — job-scheduling-design.md's own qualification: the schedule-time
        // write-write detection every dependency-edge tooth here relies on is CONTINGENT on this
        // toggle, not on being in the Editor. Nothing else observed it before this. ─────────────────────────

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

        // ── The sizing-owned-buffers consumer fence: no fence can check WHICH value bounds a loop (that is
        // dataflow, not lexical) — the failure that actually recurred three times was a borrowed loop bound
        // (AggregateJob, RibbonAggregateJob, then FillGatherJob); a STALE CONSUMER SET is what let the
        // third instance hide, not what recurred, and that stale-set failure IS mechanical. This counts
        // nodes, not tokens, so it never looks at a BORROWED sizing-owned-buffers field
        // (RibbonBatchJob.RingSubOffsets, RibbonAggregateJob.RingFeature, EarcutBatchJob.PolyHoleCount)
        // and cannot cry wolf on any of them.

        // Regex on the type plus any identifier, not a literal including the field NAME: a bare-string token
        // ("TriangulationBuffers Buffers;") would let a node declaring "TriangulationBuffers
        // FillBuffers;" walk straight past this fence — the exact evasion a rename invites.
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
    /// The stop-rule fence: once fill AND the extrusion
    /// roof are both graph-arm, nothing in production may still reference a retired symbol. The
    /// PREDICATE — every symbol whose only production caller was the synchronous fill pipeline
    /// (<c>FillMeshPipeline.Schedule</c>, its two synchronous mesh writers, or their globe/count-rule
    /// scaffolding), which lost that caller when the graph became the only mesher — not a hand-picked list:
    /// <see cref="ForbiddenPatterns"/> below must derive from re-reading the deletion rows
    /// each time a member is added there, not from what a prior pass happened to name.
    ///
    /// Identifier-anchored (lessons-learned "a source-text fence must match the IDENTIFIER") — scans every
    /// <c>.cs</c> file under <c>MapRenderer.Jobs/</c> and <c>MapRenderer.Unity/Rendering/</c>, comments
    /// stripped first so a HISTORICAL prose mention of a retired name (historical docs are full of them
    /// — "the synchronous entry point Run/RunTyped is retired with its callers X, Y") does not
    /// fail the fence; only a real reference in CODE does.
    ///
    /// <para><c>GlobeFillSubdivideDispatch.Run</c>/<c>RunTyped</c> stay banned throughout even though the
    /// class is not deleted — it is live at <c>GlobeFillSubdivider.cs:202</c> with a production caller,
    /// <c>FillMeshGraph.cs:287</c>, through its surviving <c>Schedule</c> entry point and its <c>Default*</c>
    /// constants — because only its synchronous <c>Run</c>/<c>RunTyped</c> members were retired,
    /// and nothing has ever given THIS pair a new caller.</para>
    ///
    /// <para><b>Tooth (b)'s hole:</b> <c>ProjectPointsJob&lt;TProj&gt;.Run(n)</c> itself stays <c>public</c>,
    /// so a caller that constructs the job directly and calls <c>.Run()</c> on the instance could still reach
    /// the kernel synchronously — a source-text regex cannot anchor that reliably, unlike every pattern in
    /// <see cref="ForbiddenPatterns"/> (a static <c>ClassName.Method(</c> call): an instance method called
    /// after a multi-line object-initializer block has no fixed textual shape to match. But the CONSTRUCTION
    /// SITE does: fencing "which files may name <c>ProjectPointsJob</c> at all" makes the `.Run` anchoring
    /// problem moot, because nothing outside those two files can construct the job to call anything on it —
    /// <see cref="ProjectPointsJobConstructionFenceTests"/> is that fence, the same exactly-N-named-files
    /// idiom <c>WallChainCallerFenceTests.WallQuadJob_AppearsInExactlyTheTwoExpectedProductionFiles</c>
    /// uses.</para>
    ///
    /// <para>The retired synchronous <c>WriteMeshData</c> methods (<c>StyledFillTileBuilder</c>,
    /// <c>StyledFillExtrusionTileBuilder</c>, <c>StyledLineTileBuilder</c>) also fall under the predicate —
    /// each has zero production callers, same as every symbol above.
    /// <see cref="WriteMeshDataCannotGrowBackIntoTheBuilderSources"/> is a DECLARATION-FORM assertion
    /// (<c>\bWriteMeshData\s*[(&lt;]</c>, anchoring the identifier immediately followed by an argument list
    /// or a generic-arity bracket), not a call-form pattern like the ones above: the risk here is the method
    /// growing BACK with a different modifier or arity (<c>internal static void WriteMeshData&lt;T&gt;()</c>
    /// would satisfy a call-form ban but still be exactly the regrowth this stage exists to prevent), and
    /// only the declaration form sees that. Scoped to the three builder source files rather than the whole
    /// scan above — <c>WriteMeshData</c> is not a globally-retired identifier the way the others are; it is
    /// retired only FROM these three declaration sites, and remains a legitimate name for the test
    /// assembly's own <c>SyncMeshWrite</c> methods and for historical prose everywhere else.</para>
    /// </summary>
    [TestFixture]
    public class FillMeshPipelineRetirementFenceTests
    {
        /// <summary>Every retired symbol (plus
        /// <c>ProjectionDispatch.Run</c>/<c>RunTyped</c>'s own re-retirement in the wall-job-graph stage —
        /// see the class doc) that STILL has no production caller, one pattern each — a call form where a
        /// bare identifier would false-positive on an unrelated member of the same name, a word-boundary bare
        /// match otherwise. <c>Run\s*\(</c> does NOT match <c>RunTyped(</c>, hence the separate
        /// <c>RunTyped</c> entries for both <c>GlobeFillSubdivideDispatch</c> and
        /// <c>ProjectionDispatch</c>.</summary>
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
    /// Tooth (d) — a caller fence for a claim
    /// no runtime test can make: Unity has no manual/event <c>JobHandle</c>, so a test cannot hold the wall
    /// graph unsatisfied to observe "still running", and the job safety system tracks the HANDLE rather than
    /// execution state, so reading a wall column early throws whether or not the job ran. Both readings this
    /// fence would otherwise want are unavailable — see <see cref="FillMeshPipelineRetirementFenceTests"/>'s
    /// own mould, which this one copies: identifier-anchored, comments stripped first (a HISTORICAL prose
    /// mention — historical docs are full of them — must not fail the fence; only a real reference in CODE
    /// does), scanning every <c>.cs</c> file under <c>MapRenderer.Jobs/</c> and
    /// <c>MapRenderer.Unity/Rendering/</c>, both <c>AllDirectories</c>, with the same <c>&gt;= 50</c>
    /// non-vacuity guard.
    ///
    /// <para>Two assertions, together with ordinal 5's compile-enforced loss of <c>BuildLayerInput</c>'s
    /// <c>out WallColumns</c> and tooth (b)'s deleted synchronous <c>ProjectionDispatch.Run</c>/<c>RunTyped</c>,
    /// make "the wall chain runs inside the prologue body" unrepresentable:</para>
    /// <list type="bullet">
    /// <item><c>StyledFillExtrusionTileBuilder.cs</c> — the prologue's own file — contains none of
    /// <c>WallQuadJob</c>, <c>RingSelectJob</c>, <c>TileToGeoJob</c>, <c>ProjectionDispatch</c>,
    /// <c>WriteWalls</c>.</item>
    /// <item><c>WallQuadJob</c> appears in EXACTLY TWO production files, named
    /// <c>StyledFillExtrusionTileBuilder.WallJob.cs</c> (its declaration) and <c>FillExtrusionMeshGraph.cs</c>
    /// (its sole scheduler) — a named count, not "at least one" or "not in file X": <c>WallQuadJob</c> stays
    /// nested in a partial file of <c>StyledFillExtrusionTileBuilder</c>, so a fence naming only the prologue
    /// file would be evaded by re-inlining the job's construction into a THIRD partial of the same type
    /// (the earlier three-bullet
    /// version was self-contradicting).</item>
    /// </list>
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
    /// Tooth (b)'s hole.
    /// <c>ProjectPointsJob&lt;TProj&gt;.Run(n)</c> stays <c>public</c> and cannot itself be source-text
    /// fenced (an instance method called after a multi-line object-initializer block has no fixed textual
    /// shape a regex can anchor — see <see cref="FillMeshPipelineRetirementFenceTests"/>'s own class doc).
    /// Fencing the CONSTRUCTION SITE instead closes the hole without needing to anchor <c>.Run</c> at all:
    /// if only <c>ProjectPointsJob.cs</c> (its own declaration) and <c>ProjectionDispatch.cs</c> (its sole
    /// scheduler) may name <c>ProjectPointsJob</c>, nothing else can construct an instance to call anything
    /// on it, <c>.Run()</c> included. Same idiom as
    /// <c>WallChainCallerFenceTests.WallQuadJob_AppearsInExactlyTheTwoExpectedProductionFiles</c>:
    /// identifier-anchored, comments stripped first (a HISTORICAL prose mention must not fail the fence, only
    /// a real reference in CODE does), scanning every <c>.cs</c> file under <c>MapRenderer.Jobs/</c> and
    /// <c>MapRenderer.Unity/Rendering/</c>, both <c>AllDirectories</c>, with the same <c>&gt;= 50</c>
    /// non-vacuity guard.
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
    /// <c>TileBuildGraph</c> retains no kind knowledge: every layer it touches is an
    /// <c>ILayerMeshBuild</c>, and this fence pins that the type cannot silently re-grow a per-kind
    /// dispatch (a <c>LayerRequestKind</c>-discriminated struct, per-kind factories, per-kind dispatch
    /// sites). Copies <see cref="WallChainCallerFenceTests"/>'s own mould
    /// exactly: same two scan roots, <c>AllDirectories</c>, comments stripped first, <c>&gt;= 50</c>
    /// non-vacuity floor.
    ///
    /// <para><b>Two assertions.</b> (a) is the total-footprint half — a fence over one file's CONTENTS is
    /// evaded by adding a partial, so this closes it at the declaration site instead of chasing file names.
    /// (b) is the content half — fourteen identifiers: the eleven pre-stage kind names, PLUS the three this
    /// stage creates (<see cref="FillLayerBuild"/>/<see cref="FillExtrusionLayerBuild"/>/<see cref="LineLayerBuild"/>).
    /// Without the three new names the fence would read green against
    /// <c>if (build is FillExtrusionLayerBuild ext)</c> — not the adversarial case, but what a fourth-kind
    /// stage reaches for by reflex. The graph may know only <c>ILayerMeshBuild</c>.</para>
    ///
    /// <para><b>Acknowledged limit — this does NOT close the evasion.</b> Neither assertion sees a
    /// HELPER-CLASS indirection: a new <c>TileBuildGraphKindDispatch.cs</c> holding the switch, called from
    /// <c>TileBuildGraph.cs</c>, passes both. No structure fence in this repo closes that shape, and
    /// inventing one here would be a regex chasing a design smell. What this buys is that kind knowledge
    /// cannot re-grow INSIDE the type, silently, which is how it grew the first time.</para>
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
    /// Unlit mode's HARD CONSTRAINT, made falsifiable: <b>mesh preparation never branches on render
    /// mode.</b> One builder produces one per-layer vertex/mesh layout; the Lit and Unlit shader twins both
    /// consume it. That is what makes unlit a material-set setting rather than a parallel pipeline — and it
    /// holds today only because nothing in the meshing path names
    /// <see cref="RenderMode"/> at all. This fixture is what notices if that stops being true.
    ///
    /// <para><b>The discriminator (fenced by LOCATION, and stated so a reader can tell an intentional
    /// exception from a regression).</b> The fence covers every source file that participates in turning
    /// decoded tile bytes into a mesh:</para>
    /// <list type="bullet">
    /// <item><c>MapRenderer.Jobs</c> — the whole assembly. It is the blittable data plane: decode, geometry,
    /// triangulation, the Burst job graph. No file in it has any business knowing a shading family.</item>
    /// <item><c>MapRenderer.Unity/Rendering/Meshing</c> — the styled tile builders and layer mesh builds,
    /// the types that own the vertex descriptors the constraint is about.</item>
    /// <item><c>MapRenderer.Unity/Rendering/Tile</c> — the tile pipeline that drives them (build graph,
    /// layer processors, prepared-tile cache).</item>
    /// </list>
    /// <para><c>MapRenderer.Core</c> is NOT listed: it is engine-free and its asmdef does not
    /// reference <c>MapRenderer.Unity</c>, so it cannot name the enum even if someone tried —
    /// <c>CoreAssemblyBoundaryTests</c> already fences that direction. Everything OUTSIDE the fence
    /// (<c>Rendering/Materials</c>, <c>Rendering/Map</c>, <c>MapRenderer.App</c>) may name the mode freely;
    /// choosing materials and bootstrapping lighting is exactly its job, and two of those folders are this
    /// fixture's positive control.</para>
    ///
    /// <para><b>Why a source scan rather than reflection over member signatures.</b> The hazard is a
    /// <i>branch</i> — <c>if (mode == RenderMode.Unlit) …</c> inside a build method — which changes no
    /// signature and so is invisible to reflection. A comment-stripped text scan sees method bodies.
    /// The repo's recorded lesson is that a text-matching tooth fails OPEN when a rename defeats its
    /// pattern, so the token is not a literal here: it is <c>typeof(RenderMode).Name</c>, read off the real
    /// type at run time. Rename the enum and the fence re-aims itself. Every way to reach the enum from a
    /// fenced file — a using alias, <c>using static</c>, a fully-qualified name, a <c>.RenderMode</c> read
    /// off a <c>MapMaterialSet</c> — writes that token into the file.</para>
    ///
    /// <para><b>Known limitation, and the direction it fails in.</b> Comments are stripped but string
    /// literals are not, so a fenced file that merely quoted the token in a message would trip the fence.
    /// That is a false RED — the safe direction, and cheaper to resolve than the false GREEN the
    /// alternative buys. The fence also cannot see a mode smuggled through an untyped proxy (a
    /// <c>bool isUnlit</c> parameter); nothing structural can. The byte-identity claim is the meshing
    /// constraint's other half, and <c>MapFillUnlitMaterialTests</c>' vertex-layout tooth covers it from
    /// the shader side.</para>
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

// The guard GlobeFillVertexKey's doc names: the key is enumerated FIELD BY FIELD, so a column added to
// GlobeFillVertex is NOT picked up automatically. Nothing would fail to compile — two vertices differing
// only in the new column would simply merge, silently. This is the tooth that makes that loud instead.
//
// It already caught one: the band branch added `Band` and the key had to be extended by hand.

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
