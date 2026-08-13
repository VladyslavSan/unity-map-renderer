using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs;
using MapRenderer.Jobs.Mvt;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// IR C1 P3, teeth <b>A</b> and <b>F</b> — the neutral geometry path carries no format, and the detached
    /// sidecar cannot come back.
    ///
    /// <para><b>What this replaces, and why the claim had to change.</b> IR B5's tooth asserted that
    /// <c>ITileFeature</c> — an interface declaring zero members — declared zero members; C1 P1 deleted that
    /// interface, and the rebased version asserted that <see cref="IFeature"/> exposed no integral array.
    /// Both were satisfiable by the very shape they were meant to prevent: a <b>sidecar</b>. B5's own
    /// <c>IMvtGeometryCarrier</c> satisfied "the neutral interface is empty" trivially, by living beside it.
    /// P3 deleted the sidecar (geometry belongs to the layer now), so the tooth is restated as the claim that
    /// makes the sidecar unavailable rather than merely absent:</para>
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
                "what rotted in IR B5, because a new format-named type was simply added to it. " +
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
                "signature. This is what B5 CLAIMED and did not deliver: its tooth asserted an interface was " +
                "empty, which a sidecar interface satisfies trivially. " +
                $"Offenders: {string.Join(", ", offenders)}");
        }

        [Test]
        public void TheNeutralSurfaces_ExposeNoCommandStream()
        {
            // Non-vacuity FIRST: the surfaces are the real ones, not empty or wrong types. Every member the
            // design says survives must be here, or the absence assertions below assert about nothing.
            foreach (string surviving in new[]
                     { nameof(IFeature.GeometryType), nameof(IFeature.HasId), nameof(IFeature.Id),
                       nameof(IFeature.TryGetProperty), nameof(IFeature.Properties) })
                Assert.IsNotEmpty(
                    typeof(IFeature).GetMember(surviving, BindingFlags.Public | BindingFlags.Instance),
                    $"'{surviving}' must be on IFeature — it is the kind gate and filter surface every " +
                    "consumer reads. Without it this fixture would be asserting over an empty type.");
            Assert.IsNotEmpty(typeof(ITileLayer).GetMember(nameof(ITileLayer.Geometry)),
                "precondition: ITileLayer must expose Geometry — the whole point of P3 is that the LAYER " +
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

        /// <summary>IR C1 P1/P3: the zero-member <c>ITileFeature</c> and the sidecar
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

        /// <summary>IR C1 P3, tooth F: the detached sidecar cannot come back by accretion. Deliberately
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
                "ProcessOnWorker takes exactly (IDecodedTile, in TileLayerProcessContext). B7's third " +
                "parameter — a pass-scoped TileGeometryStore — was the detached sidecar this epic removed: " +
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
            Assert.IsTrue(Directory.Exists(codeRoot), $"expected the source root at {codeRoot}");

            foreach (string assemblyDir in new[] { "MapRenderer.Core", "MapRenderer.Jobs", "MapRenderer.Unity" })
            {
                string root = Path.Combine(codeRoot, assemblyDir);
                Assert.IsTrue(Directory.Exists(root), $"expected {root} to exist");
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
}
