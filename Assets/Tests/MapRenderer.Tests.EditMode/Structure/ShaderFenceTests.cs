// Structure/ShaderFenceTests.cs — shader/material structural fences. Unity EditMode only — reads source
// files under Application.dataPath. NOT registered in core-tests.csproj.
//
// Contents:
//   InstanceStructShaderParityTests        — MapInstanceData struct <-> DOTS-instanced shader property parity.
//   MaterialPropertyRegistryParityTests    — the ShaderProperties registry vs shader Properties/CBUFFER/DOTS.
//   NoRawStringMaterialAccessGuardTests    — no raw-string Material property access anywhere in the repo.
//   ShaderStructureTests                   — fill/line shader pass attribute/keyword/render-state structure.
//   FillBandAttributeTests                 — fill-band forward/depth pass vertex-attribute parity.
//   CoreAssemblyBoundaryTests               — MapRenderer.Core stays engine-free; no UnityEngine/Unity.* crosses in.
//   StyleModelAssemblyBoundaryTests         — the style model lives in MapRenderer.Unity, not Core.
//   MdCitationFenceTests                   — every *.md citation in source resolves to a tracked file.
//   BakeIdentityShapeStructureTests        — the recorded shape of PreparedKey and TileBufferClip.
//   RenderLayerRegistryStructureTests      — RenderLayerFactory is the sole StyleLayer-subtype dispatch point.
//   SkyShaderStructureTests                — Map/Sky ships in every build; the sky writer never touches scene lighting.
//   HazeFogStructureTests                  — the build keeps the haze's fog variants; every map forward pass takes fog.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using MapRenderer.Unity.Rendering.Backend.BRG;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml.Linq;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Unity.Rendering.Meshing;
using System.Diagnostics;
using MapRenderer.Unity.Rendering.Tile;

namespace MapRenderer.Tests.Structure
{

    // ───────────────────────────────────────────────────────────────────────────────────
    // InstanceStructShaderParityTests — MapInstanceData struct <-> DOTS-instanced shader property parity
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class InstanceStructShaderParityTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reflects <see cref="MapInstanceData"/>'s public instance fields.
        /// Returns a dictionary of {name → floatCount} excluding the unity_* transform fields.
        /// </summary>
        private static Dictionary<string, int> ParseStructMaterialFields(out int totalFieldCount)
        {
            FieldInfo[] fields = typeof(MapInstanceData).GetFields(BindingFlags.Public | BindingFlags.Instance);

            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            int allCount = 0;

            foreach (FieldInfo field in fields)
            {
                allCount++;

                // Exclude transform fields (unity_* prefix) from the material-prop map.
                if (field.Name.StartsWith("unity_", StringComparison.Ordinal))
                    continue;

                result[field.Name] = ReflectedFieldFloatCount(field.FieldType);
            }

            totalFieldCount = allCount;
            return result;
        }

        /// <summary>
        /// Maps a reflected field's CLR type to a float count via <see cref="ShaderPropertyParser.TypeToFloatCount"/>.
        /// Unity.Mathematics struct types (float4, float3x4, …) report their HLSL-matching name directly;
        /// the C# <c>float</c> alias reflects as CLR "Single" and needs translating first.
        /// </summary>
        private static int ReflectedFieldFloatCount(Type fieldType)
        {
            string typeName = fieldType == typeof(float) ? "float" : fieldType.Name;
            return ShaderPropertyParser.TypeToFloatCount(typeName);
        }

        // A local alias for ShaderPropertyParser.ParseDotsProps (shared with
        // MaterialPropertyRegistryParityTests).
        private static Dictionary<string, int> ParseDotsProps(string filePath)
            => ShaderPropertyParser.ParseDotsProps(filePath);

        // ── Forward parity: each DOTS prop → struct field of matching float-count ─────────────

        /// <summary>
        /// Every DOTS prop of each kind must have a same-name struct field with matching float-count.
        /// Removing _Width from MapInstanceData fails the Line case (the original bug direction).
        /// </summary>
        [Test]
        [TestCase("Line_LitInput.hlsl", "Line", TestName = "Forward_DotsPropHasMatchingStructField(Line)")]
        [TestCase("Fill_LitInput.hlsl", "Fill", TestName = "Forward_DotsPropHasMatchingStructField(Fill)")]
        public void Forward_DotsPropHasMatchingStructField(string hlslFile, string kindLabel)
        {
            var dotsProps = ParseDotsProps(ShaderPropertyParser.MapShaderPath(hlslFile));
            var structFields = ParseStructMaterialFields(out _);

            var failures = new List<string>();
            foreach (var kv in dotsProps)
            {
                string name       = kv.Key;
                int    dotsFloats = kv.Value;
                if (!structFields.TryGetValue(name, out int structFloats))
                    failures.Add($"'{name}' is in {kindLabel} DOTS block but has no matching field in MapInstanceData.");
                else if (structFloats != dotsFloats)
                    failures.Add($"'{name}' float-count mismatch: {kindLabel} DOTS={dotsFloats}, struct field={structFloats}.");
            }

            Assert.That(failures, Is.Empty,
                $"{kindLabel} DOTS -> struct forward parity failed:\n" + string.Join("\n", failures));
        }

        // ── Reverse parity: each struct field → in Fill∪Line DOTS names ─────────────────────

        /// <summary>
        /// Every struct material-prop field must appear in the Fill∪Line DOTS union.
        /// Adding a struct field that no shader declares fails here (orphan field = dead layout slot).
        /// </summary>
        [Test]
        public void Reverse_EachStructField_AppearsInFillOrLineDots()
        {
            var structFields = ParseStructMaterialFields(out _);
            var lineProps    = ParseDotsProps(ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl"));
            var fillProps    = ParseDotsProps(ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));

            var failures = new List<string>();
            foreach (string name in structFields.Keys)
            {
                bool inLine = lineProps.ContainsKey(name);
                bool inFill = fillProps.ContainsKey(name);
                if (!inLine && !inFill)
                    failures.Add($"Struct field '{name}' is not declared in any shader DOTS block (orphan field).");
            }

            Assert.That(failures, Is.Empty,
                "Struct → Fill∪Line DOTS reverse parity failed (orphan struct fields):\n" +
                string.Join("\n", failures));
        }

        /// <summary>
        /// <c>Assets/link.xml</c> keeps every <see cref="MapInstanceData"/> field under managed stripping.
        /// No C# code reads those fields, so without the entry the linker can drop one and shift the SoA layout.
        /// </summary>
        [Test]
        public void LinkXml_PreservesEveryInstanceDataField()
        {
            Type type = typeof(MapInstanceData);
            var linker = XDocument.Load(Path.Combine(Application.dataPath, "link.xml"));
            bool preserved = linker.Descendants("type")
                .Any(t => (string)t.Parent.Attribute("fullname") == type.Assembly.GetName().Name
                       && (string)t.Attribute("fullname") == type.FullName
                       && ((string)t.Attribute("preserve") == "fields" || (string)t.Attribute("preserve") == "all"));

            Assert.That(preserved, Is.True,
                $"Assets/link.xml must hold <type fullname=\"{type.FullName}\" preserve=\"fields\"/> " +
                $"under <assembly fullname=\"{type.Assembly.GetName().Name}\">.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MaterialPropertyRegistryParityTests — the ShaderProperties registry vs shader Properties/CBUFFER/DOTS
    // ───────────────────────────────────────────────────────────────────────────────────

// Material Property Registry Parity: the PropertyNames regions (shared, line-only, fill-only), each shader's
// CBUFFER and DOTS block, and each .shader Properties{} superset agree, by set equality/partition — never a
// pinned count, so a vacuous (zero-match) regex still fails a real assertion instead of passing silently.
//

    [TestFixture]
    public class MaterialPropertyRegistryParityTests
    {
        // ── Paths ────────────────────────────────────────────────────────────────────────────────

        private static string RenderingDir        => ShaderPropertyParser.RenderingDir;
        private static string ShaderPropertiesDir => Path.Combine(RenderingDir, "ShaderProperties");
        private static string RenderingLineDir     => Path.Combine(ShaderPropertiesDir, "Line");
        private static string RenderingFillDir     => Path.Combine(ShaderPropertiesDir, "Fill");

        // ── Set/partition guards ─────────────────────────────────────────────────────────────────

        /// <summary>The whole file's const set equals the union of its three named regions (CBUFFER,
        /// render-state, textures/surface) — nothing declared outside a region, nothing double-counted.</summary>
        [Test]
        public void PropertyNamesTotalCount_EqualsSumOfNamedRegions()
        {
            string path = Path.Combine(ShaderPropertiesDir, "PropertyNames.cs");
            var all         = ShaderPropertyParser.ParseAllConstStringValues(path);
            var cbuffer     = ParseSharedCbufferRegion();
            var renderState = ShaderPropertyParser.ParseRegionValues(path,
                "// region: Render-state ShaderLab knobs");
            var textures    = ShaderPropertyParser.ParseRegionValues(path,
                "// region: Textures / surface bookkeeping");

            var union = new HashSet<string>(cbuffer);
            union.UnionWith(renderState);
            union.UnionWith(textures);

            Assert.That(all.Count, Is.EqualTo(cbuffer.Count + renderState.Count + textures.Count),
                "the three named regions must partition the file with no overlap — a name counted twice " +
                "means two regions claim it.");
            Assert.That(union, Is.EquivalentTo(all),
                "every const in the file must fall inside one of the three named regions.");
        }

        // ── Set equality: CBUFFER region == shader intersection ──────────────────────────────────

        [Test]
        public void SharedCbufferRegion_EqualsLineFillIntersection()
        {
            // The shared CBUFFER names in PropertyNames.cs are exactly the intersection of the two shader
            // CBUFFER sets.
            var shared = ParseSharedCbufferRegion();
            var lineCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl"));
            var fillCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));

            var sharedFromShaders = new HashSet<string>(lineCbuffer);
            sharedFromShaders.IntersectWith(fillCbuffer);

            var onlyInCs = new HashSet<string>(shared);
            onlyInCs.ExceptWith(sharedFromShaders);
            var onlyInShaders = new HashSet<string>(sharedFromShaders);
            onlyInShaders.ExceptWith(shared);

            Assert.That(onlyInCs, Is.Empty,
                $"PropertyNames CBUFFER region has props not in both shaders: {string.Join(", ", onlyInCs)}");
            Assert.That(onlyInShaders, Is.Empty,
                $"Shader intersection has props missing from PropertyNames CBUFFER region: {string.Join(", ", onlyInShaders)}");
        }

        // ── Union parity: shared ∪ line-only == Line CBUFFER (the big assertion) ─────────────────

        [Test]
        public void SharedUnionLineNames_EqualsLineCbuffer()
        {
            var shared = ParseSharedCbufferRegion();
            var lineNames = ParseLineCbufferRegion();
            var lineCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl"));

            var union = new HashSet<string>(shared);
            union.UnionWith(lineNames);

            var onlyInUnion = new HashSet<string>(union);
            onlyInUnion.ExceptWith(lineCbuffer);
            var onlyInCbuffer = new HashSet<string>(lineCbuffer);
            onlyInCbuffer.ExceptWith(union);

            Assert.That(onlyInUnion, Is.Empty,
                $"Props in (shared ∪ lineNames) but not in Line CBUFFER: {string.Join(", ", onlyInUnion)}");
            Assert.That(onlyInCbuffer, Is.Empty,
                $"Props in Line CBUFFER but not in (shared ∪ lineNames): {string.Join(", ", onlyInCbuffer)}");
        }

        [Test]
        public void SharedUnionFillNames_EqualsFillCbuffer()
        {
            var shared = ParseSharedCbufferRegion();
            var fillNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingFillDir, "PropertyNames.cs"));
            var fillCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));

            var union = new HashSet<string>(shared);
            union.UnionWith(fillNames);

            var onlyInUnion = new HashSet<string>(union);
            onlyInUnion.ExceptWith(fillCbuffer);
            var onlyInCbuffer = new HashSet<string>(fillCbuffer);
            onlyInCbuffer.ExceptWith(union);

            Assert.That(onlyInUnion, Is.Empty,
                $"Props in (shared ∪ fillNames) but not in Fill CBUFFER: {string.Join(", ", onlyInUnion)}");
            Assert.That(onlyInCbuffer, Is.Empty,
                $"Props in Fill CBUFFER but not in (shared ∪ fillNames): {string.Join(", ", onlyInCbuffer)}");
        }

        // ── Instanced subset: DOTS block == CBUFFER (cross-check) ───────────────────────────────

        /// <summary>Each kind's DOTS block keys must equal its CBUFFER set — every CBUFFER prop is
        /// instanced, and nothing is instanced that isn't a CBUFFER member.</summary>
        [Test]
        [TestCase("Line_LitInput.hlsl", "Line", TestName = "DotsKeys_EqualCbuffer(Line)")]
        [TestCase("Fill_LitInput.hlsl", "Fill", TestName = "DotsKeys_EqualCbuffer(Fill)")]
        public void DotsKeys_EqualCbuffer(string hlslFile, string kindLabel)
        {
            var dotsKeys = new HashSet<string>(
                ShaderPropertyParser.ParseDotsProps(ShaderPropertyParser.MapShaderPath(hlslFile)).Keys);
            var cbuffer = ShaderPropertyParser.ParseCbufferMembers(ShaderPropertyParser.MapShaderPath(hlslFile));

            var onlyInDots = new HashSet<string>(dotsKeys);
            onlyInDots.ExceptWith(cbuffer);
            var onlyInCbuffer = new HashSet<string>(cbuffer);
            onlyInCbuffer.ExceptWith(dotsKeys);

            Assert.That(onlyInDots, Is.Empty,
                $"Props in {kindLabel} DOTS block but not in {kindLabel} CBUFFER: {string.Join(", ", onlyInDots)}");
            Assert.That(onlyInCbuffer, Is.Empty,
                $"Props in {kindLabel} CBUFFER but not in {kindLabel} DOTS block: {string.Join(", ", onlyInCbuffer)}");
        }

        // ── Shader Properties{} superset: every registry name appears in the shader ─────────────

        /// <summary>Every name in the shared registry plus a kind's own registry appears in that kind's
        /// shader Properties{} block.</summary>
        [Test]
        [TestCase("Line", "Line.shader", TestName = "ShaderPropertiesBlock_ContainsAllRegistryNames(Line)")]
        [TestCase("Fill", "Fill.shader", TestName = "ShaderPropertiesBlock_ContainsAllRegistryNames(Fill)")]
        public void ShaderPropertiesBlock_ContainsAllRegistryNames(string kindLabel, string shaderFile)
        {
            var allShared = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(ShaderPropertiesDir, "PropertyNames.cs"));
            var kindNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(ShaderPropertiesDir, kindLabel, "PropertyNames.cs"));
            var shaderProps = ShaderPropertyParser.ParseShaderPropertiesBlock(
                ShaderPropertyParser.MapShaderPath(shaderFile));

            var allRegistry = new HashSet<string>(allShared);
            allRegistry.UnionWith(kindNames);

            var missing = new List<string>();
            foreach (string name in allRegistry)
                if (!shaderProps.Contains(name))
                    missing.Add(name);

            Assert.That(missing, Is.Empty,
                $"These registry names are missing from {shaderFile} Properties{{}}: " +
                string.Join(", ", missing));
        }

        // ── No cross-contamination: layer-specific names not in shared CBUFFER region ────────────

        /// <summary>A kind's own PropertyNames.cs never re-declares a name the shared CBUFFER region
        /// already carries — _BaseColor must be only in the shared file, never in Line's or Fill's.</summary>
        [Test]
        [TestCase("Line", TestName = "PropertyNames_HasNoDuplicatesInSharedCbuffer(Line)")]
        [TestCase("Fill", TestName = "PropertyNames_HasNoDuplicatesInSharedCbuffer(Fill)")]
        public void PropertyNames_HasNoDuplicatesInSharedCbuffer(string kindLabel)
        {
            var shared = ParseSharedCbufferRegion();
            var kindNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(ShaderPropertiesDir, kindLabel, "PropertyNames.cs"));

            var overlap = new HashSet<string>(kindNames);
            overlap.IntersectWith(shared);

            Assert.That(overlap, Is.Empty,
                $"ShaderProperties/{kindLabel}/PropertyNames.cs re-declares names that are already in the shared " +
                $"CBUFFER region (duplication violates the single-source-of-truth rule): {string.Join(", ", overlap)}");
        }

        // ── Shared helper ────────────────────────────────────────────────────────────────────────

        private static HashSet<string> ParseSharedCbufferRegion()
            => ShaderPropertyParser.ParseRegionValues(
                Path.Combine(ShaderPropertiesDir, "PropertyNames.cs"),
                "// region: CBUFFER (UnityPerMaterial) — shared instanced members");

        /// <summary>
        /// The CBUFFER region of the LINE registry. Line/PropertyNames.cs also declares editor-only keyword
        /// drivers (Properties{}-only, never CBUFFER members), exactly as the shared registry does for
        /// _ReceiveShadows, so the union parity assertion reads this region rather than every const in the
        /// file. <see cref="SharedUnionLineNames_EqualsLineCbuffer"/> pins shared ∪ lineNames == Line CBUFFER;
        /// <c>PropertyNames_HasNoDuplicatesInSharedCbuffer(Line)</c> pins that Line/PropertyNames.cs is
        /// disjoint from the shared region — together they force this region to equal Line CBUFFER minus
        /// the shared names.
        /// </summary>
        private static HashSet<string> ParseLineCbufferRegion()
            => ShaderPropertyParser.ParseRegionValues(
                Path.Combine(RenderingLineDir, "PropertyNames.cs"),
                "// region: CBUFFER (UnityPerMaterial) — line-only instanced members");
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // NoRawStringMaterialAccessGuardTests — no raw-string Material property access anywhere in the repo
    // ───────────────────────────────────────────────────────────────────────────────────

// No-Raw-String Material Access Guard: the six registry files exist, no prefixed or shim class name
// survives, no Material/Shader call takes a string literal, and every PropertyId uses PropertyNames.X.

    [TestFixture]
    public class NoRawStringMaterialAccessGuardTests
    {
        private static string RepoRoot      => ShaderPropertyParser.RepoRoot;
        private static string UnityDir      => ShaderPropertyParser.UnityAssemblyRoot;
        private static string RenderingDir  => ShaderPropertyParser.RenderingDir;
        private static string ShaderPropertiesDir => Path.Combine(RenderingDir, "ShaderProperties");

        // ── 1. Registry layout guard ─────────────────────────────────────────────────────────────

        [Test]
        public void RegistryFiles_AllSixExist()
        {
            var expected = new[]
            {
                Path.Combine(ShaderPropertiesDir, "PropertyNames.cs"),
                Path.Combine(ShaderPropertiesDir, "PropertyId.cs"),
                Path.Combine(ShaderPropertiesDir, "Line", "PropertyNames.cs"),
                Path.Combine(ShaderPropertiesDir, "Line", "PropertyId.cs"),
                Path.Combine(ShaderPropertiesDir, "Fill", "PropertyNames.cs"),
                Path.Combine(ShaderPropertiesDir, "Fill", "PropertyId.cs"),
            };

            var missing = expected.Where(p => !File.Exists(p)).ToList();
            Assert.That(missing, Is.Empty,
                $"Missing registry files:\n{string.Join("\n", missing)}");
        }

        // ── 2. Migration completeness: no old prefixed names, no compat class declaration ─────────

        [Test]
        public void NoOldPrefixedClassNames_InUnityAssembly()
        {
            // The prefixed class names are gone: LinePropertyId, FillPropertyId,
            // LinePropertyNames, FillPropertyNames. The namespace-carries-category pattern is used instead.
            var oldNames = new[] { @"\bLinePropertyId\b", @"\bFillPropertyId\b", @"\bLinePropertyNames\b", @"\bFillPropertyNames\b" };
            var badPattern = new Regex(string.Join("|", oldNames));

            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(UnityDir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (badPattern.IsMatch(text))
                    offenders.Add(file.Replace(RepoRoot, string.Empty));
            }

            Assert.That(offenders, Is.Empty,
                $"These files reference old prefixed class names (LinePropertyId / FillPropertyId / " +
                $"LinePropertyNames / FillPropertyNames). Migrate to ShaderProperties.Line.PropertyId / etc.:\n" +
                string.Join("\n", offenders));
        }

        [Test]
        public void NoCompatClassDeclaration_InUnityAssembly()
        {
            // "ShaderProperties" is a namespace segment only; no `class ShaderProperties` may exist.
            var classDeclaration = new Regex(@"\bclass\s+ShaderProperties\b");

            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(UnityDir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (classDeclaration.IsMatch(text))
                    offenders.Add(file.Replace(RepoRoot, string.Empty));
            }

            Assert.That(offenders, Is.Empty,
                $"These files declare 'class ShaderProperties'. ShaderProperties is now a namespace " +
                $"segment, not a type. Delete the class declaration:\n" +
                string.Join("\n", offenders));
        }

        // ── 3. No raw-string Material API guard ──────────────────────────────────────────────────

        [Test]
        public void NoRawStringMaterialApiCalls_InUnityAssembly()
        {
            // Get/GetGlobal/HasProperty flag only a call with EXACTLY one string-literal argument (Unity's
            // shape); Set/SetGlobal always takes two, so any string-opened Set call flags — arity is the tell.
            var badPattern = new Regex(
                @"\.Get(Global)?(Float|Color|Vector|Int|Texture)\s*\(\s*""[^""]*""\s*\)" +
                @"|\.Set(Global)?(Float|Color|Vector|Int|Texture)\s*\(\s*""" +
                @"|\.HasProperty\s*\(\s*""[^""]*""\s*\)");

            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(UnityDir, "*.cs", SearchOption.AllDirectories))
            {
                string[] lines = File.ReadAllLines(file);
                for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
                {
                    string line = lines[lineIdx];
                    if (badPattern.IsMatch(line))
                    {
                        offenders.Add($"{file.Replace(RepoRoot, string.Empty)}:{lineIdx + 1}: {line.Trim()}");
                    }
                }
            }

            Assert.That(offenders, Is.Empty,
                $"These lines use raw-string Material API calls. Replace with a cached int id from " +
                $"ShaderProperties.PropertyId / ShaderProperties.Line.PropertyId / ShaderProperties.Fill.PropertyId:\n" +
                string.Join("\n", offenders));
        }

        // ── 4. No bare string literals in PropertyId files ────────────────────────────────────────

        [Test]
        public void PropertyIdFiles_NoBareLiterals()
        {
            // Every PropertyId member is Shader.PropertyToID(PropertyNames.X): the parity tests parse only
            // PropertyNames, so a bare "_Foo" literal here would pass them all.
            var bareLiteralPattern = new Regex(@"Shader\.PropertyToID\s*\(\s*""");

            var propertyIdFiles = Directory.GetFiles(ShaderPropertiesDir, "PropertyId.cs", SearchOption.AllDirectories);

            Assert.That(propertyIdFiles.Length, Is.GreaterThan(0),
                $"No PropertyId.cs files found under {ShaderPropertiesDir}. Check that the registry restructure completed.");

            var offenders = new List<string>();
            foreach (string file in propertyIdFiles)
            {
                string[] lines = File.ReadAllLines(file);
                for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
                {
                    if (bareLiteralPattern.IsMatch(lines[lineIdx]))
                        offenders.Add($"{file.Replace(RepoRoot, string.Empty)}:{lineIdx + 1}: {lines[lineIdx].Trim()}");
                }
            }

            Assert.That(offenders, Is.Empty,
                $"These PropertyId.cs lines use bare string literals in Shader.PropertyToID(\"...\"). " +
                $"Replace with a PropertyNames.X reference:\n" +
                string.Join("\n", offenders));
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // ShaderStructureTests — fill/line shader pass attribute/keyword/render-state structure
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class ShaderStructureTests
    {
        private static string RepoRoot   => ShaderPropertyParser.RepoRoot;
        private static string ShadersDir => ShaderPropertyParser.ShadersDir;
        private static string CommonDir  => ShaderPropertyParser.CommonDir;
        private static string MapDir     => ShaderPropertyParser.MapDir;
        private static string MapFillDir => ShaderPropertyParser.MapFillDir;
        private static string MapLineDir => ShaderPropertyParser.MapLineDir;
        private static string MapExtrusionDir => ShaderPropertyParser.MapExtrusionDir;

        // ── File existence ────────────────────────────────────────────────────

        [Test]
        public void ShaderFiles_AllRequiredFilesExist()
        {
            // Checked by NAME: MapShaderPath resolves each to exactly one file under Shaders/Map/, whatever
            // the Lit/Unlit folder; the unlit twins are included.
            string[] required = new[]
            {
                // Fill — lit + shared
                "Fill.shader", "Fill_LitInput.hlsl", "Fill_VertexModify.hlsl", "Fill_LitForwardPass.hlsl",
                "Fill_LitGBufferPass.hlsl", "Fill_ShadowCasterPass.hlsl", "Fill_DepthOnlyPass.hlsl",
                "Fill_DepthNormalsPass.hlsl",
                // Fill — unlit twin
                "FillUnlit.shader", "Fill_UnlitInput.hlsl", "Fill_UnlitForwardPass.hlsl",
                // Line — lit
                "Line.shader", "Line_LitInput.hlsl", "Line_LitForwardPass.hlsl",
                // Line — unlit twin
                "LineUnlit.shader", "Line_UnlitInput.hlsl", "Line_UnlitForwardPass.hlsl",
                // FillExtrusion — lit + shared
                "FillExtrusion.shader", "FillExtrusion_LitInput.hlsl", "FillExtrusion_VertexModify.hlsl",
                "FillExtrusion_LitForwardPass.hlsl", "FillExtrusion_LitGBufferPass.hlsl",
                "FillExtrusion_ShadowCasterPass.hlsl", "FillExtrusion_DepthOnlyPass.hlsl",
                "FillExtrusion_DepthNormalsPass.hlsl",
                // FillExtrusion — unlit twin
                "FillExtrusionUnlit.shader", "FillExtrusion_UnlitInput.hlsl",
                "FillExtrusion_UnlitForwardPass.hlsl",
            };

            foreach (var name in required)
                FileAssert.Exists(ShaderPropertyParser.MapShaderPath(name));

            // Common/ reference template lives OUTSIDE Map/ (included by nobody) — checked explicitly.
            FileAssert.Exists(Path.Combine(CommonDir, "LitInput.Template.hlsl"));
        }

        [Test]
        public void ShaderFiles_FillAndLineLiveUnderMapLayer()
        {
            // Per-layer shaders live under Shaders/Map/<Layer>/; the layer is pinned, the Lit/Unlit
            // subfolder is not.
            Assert.That(ShaderPropertyParser.MapShaderPath("Fill.shader").Replace('\\', '/'),
                Does.Contain("/Map/Fill/"), "Fill.shader must live under Shaders/Map/Fill/.");
            Assert.That(ShaderPropertyParser.MapShaderPath("Line.shader").Replace('\\', '/'),
                Does.Contain("/Map/Line/"), "Line.shader must live under Shaders/Map/Line/.");
        }

        [Test]
        public void MapKindRoots_HoldOnlyGenuinelySharedIncludes()
        {
            // The kind ROOT (Map/<Kind>/, top level) holds EXACTLY the includes both Lit and Unlit reuse: the
            // vertex hook, two depth passes, and named extras (Fill's band coverage). No wildcard admits more.
            (string kindDir, string vertex, string[] alsoShared)[] kinds =
            {
                (MapFillDir,      "Fill_VertexModify.hlsl",          new[] { "Fill_BandCoverage.hlsl" }),
                (MapLineDir,      "Line_VertexExtrude.hlsl",         new string[0]),
                (MapExtrusionDir, "FillExtrusion_VertexModify.hlsl", new string[0]),
            };
            foreach (var (kindDir, vertex, alsoShared) in kinds)
            {
                string kind = Path.GetFileName(kindDir);
                var expected = new HashSet<string>(StringComparer.Ordinal)
                {
                    vertex, kind + "_DepthOnlyPass.hlsl", kind + "_DepthNormalsPass.hlsl",
                };
                expected.UnionWith(alsoShared);
                var actual = new HashSet<string>(
                    Directory.EnumerateFiles(kindDir, "*.*", SearchOption.TopDirectoryOnly)
                        .Select(Path.GetFileName)
                        .Where(n => n.EndsWith(".hlsl", StringComparison.Ordinal)
                                 || n.EndsWith(".shader", StringComparison.Ordinal)),
                    StringComparer.Ordinal);

                Assert.That(actual, Is.EquivalentTo(expected),
                    $"Map/{kind}/ (top level) must hold EXACTLY the genuinely-shared includes " +
                    $"[{string.Join(", ", expected)}]; found [{string.Join(", ", actual)}]. Lit/Unlit-only " +
                    "files belong in the Lit/ or Unlit/ subfolder, not the kind root.");
            }
        }

        [Test]
        public void ShaderFiles_FlatRootHoldsNoShaderSources()
        {
            // No .shader/.hlsl may remain loose at the Shaders/ root —
            // they all live under Common/ or Map/<Layer>/.
            var loose = Directory.EnumerateFiles(ShadersDir)
                .Where(p => p.EndsWith(".shader", StringComparison.Ordinal)
                         || p.EndsWith(".hlsl", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToArray();
            Assert.That(loose, Is.Empty,
                "Shaders/ root must contain no loose .shader/.hlsl (found: "
                + string.Join(", ", loose) + "). They belong under Common/ or Map/<Layer>/.");
        }

        [Test]
        public void ShaderFiles_CommonHoldsNoLiveShaderFiles()
        {
            // Common/ holds only LitInput.Template.hlsl (a reference template, included by nobody).
            // No .shader file and no non-template .hlsl file belongs there.
            var liveInCommon = Directory.EnumerateFiles(CommonDir)
                .Where(p => (p.EndsWith(".shader", StringComparison.Ordinal)
                          || p.EndsWith(".hlsl", StringComparison.Ordinal))
                          && !p.EndsWith(".Template.hlsl", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToArray();
            Assert.That(liveInCommon, Is.Empty,
                "Common/ must hold only .Template.hlsl files (reference templates, not compiled). " +
                "Found live shader files: " + string.Join(", ", liveInCommon));
        }

        // ── UCL attribution headers ───────────────────────────────────────────
        // One loop over every UCL-mirrored file, reporting every offender in one message, instead of one
        // method per file. The three *LitInput files also carry the © Unity Technologies ApS attribution
        // line; the pass files only need the license name.

        private static readonly (string File, bool ChecksAttribution)[] UclLicensedFiles =
        {
            ("Fill_LitInput.hlsl", true),
            ("Fill_LitForwardPass.hlsl", false),
            ("Fill_LitGBufferPass.hlsl", false),
            ("Fill_ShadowCasterPass.hlsl", false),
            ("Fill_DepthOnlyPass.hlsl", false),
            ("Fill_DepthNormalsPass.hlsl", false),
            ("Line_LitInput.hlsl", true),
            ("Line_LitForwardPass.hlsl", false),
            ("FillExtrusion_LitInput.hlsl", true),
            ("FillExtrusion_LitForwardPass.hlsl", false),
            ("FillExtrusion_LitGBufferPass.hlsl", false),
            ("FillExtrusion_ShadowCasterPass.hlsl", false),
            ("FillExtrusion_DepthOnlyPass.hlsl", false),
            ("FillExtrusion_DepthNormalsPass.hlsl", false),
        };

        [Test]
        public void UclLicensedFiles_HaveAttributionHeaders()
        {
            var offenders = new List<string>();
            foreach (var (file, checksAttribution) in UclLicensedFiles)
            {
                string text = ReadShaderFile(file);
                if (!text.Contains("Unity Companion License"))
                    offenders.Add($"{file}: missing the UCL attribution header.");
                if (checksAttribution && !text.Contains("Unity Technologies"))
                    offenders.Add($"{file}: missing the © Unity Technologies ApS attribution.");
            }

            Assert.That(offenders, Is.Empty,
                "Every UCL-mirrored shader file must carry a UCL attribution header:\n" +
                string.Join("\n", offenders));
        }

        // ── InitializeStandardLitSurfaceData usage ────────────────────────────
        // The decisive structural gate: SurfaceData must NOT be hand-assembled.

        /// <summary>The decisive structural gate: SurfaceData must NOT be hand-assembled. The definition
        /// site declares <c>InitializeStandardLitSurfaceData</c>; both fragment call sites invoke it.</summary>
        [Test]
        [TestCase("Fill_LitInput.hlsl", "InitializeStandardLitSurfaceData",
            "Fill_LitInput.hlsl must define InitializeStandardLitSurfaceData — this is the function that " +
            "populates SurfaceData correctly (tooth #1 gate).",
            TestName = "FillLit_UsesInitializeStandardLitSurfaceData(LitInput_Defines)")]
        [TestCase("Fill_LitForwardPass.hlsl", "InitializeStandardLitSurfaceData(",
            "Fill_LitForwardPass.hlsl fragment must call InitializeStandardLitSurfaceData — NEVER " +
            "hand-assembled SurfaceData field-by-field.",
            TestName = "FillLit_UsesInitializeStandardLitSurfaceData(ForwardPass_Calls)")]
        [TestCase("Fill_LitGBufferPass.hlsl", "InitializeStandardLitSurfaceData(",
            "Fill_LitGBufferPass.hlsl fragment must call InitializeStandardLitSurfaceData — NEVER " +
            "hand-assembled SurfaceData field-by-field.",
            TestName = "FillLit_UsesInitializeStandardLitSurfaceData(GBufferPass_Calls)")]
        public void FillLit_UsesInitializeStandardLitSurfaceData(string fileName, string requiredText, string message)
        {
            string text = ReadShaderFile(fileName);
            Assert.That(text, Does.Contain(requiredText), message);
        }

        [Test]
        public void FillLitForwardPass_ModulatesAlbedoAndAlpha_AfterInit()
        {
            string text = ReadShaderFile("Fill_LitForwardPass.hlsl");
            // The init-then-modulate pattern: call init first, then multiply.
            int initIdx    = text.IndexOf("InitializeStandardLitSurfaceData(", StringComparison.Ordinal);

            // Per-vertex color (vColor) drives albedo/alpha; the _BaseColor tint is applied
            // inside InitializeStandardLitSurfaceData (the redundant _MapColor tint was removed).
            int albedoIdx  = text.IndexOf("surfaceData.albedo *= ", StringComparison.Ordinal);
            int alphaIdx   = text.IndexOf("surfaceData.alpha  *= ", StringComparison.Ordinal);

            Assert.That(initIdx,   Is.GreaterThanOrEqualTo(0), "InitializeStandardLitSurfaceData call not found.");
            Assert.That(albedoIdx, Is.GreaterThan(initIdx),
                "albedo modulation (surfaceData.albedo *= ...) must appear AFTER InitializeStandardLitSurfaceData.");
            Assert.That(alphaIdx,  Is.GreaterThan(initIdx),
                "alpha modulation (surfaceData.alpha  *= ...) must appear AFTER InitializeStandardLitSurfaceData.");

            // Confirm the per-vertex color and _Opacity drive the modulation lines.
            Assert.That(text, Does.Contain("vColor.rgb"),
                "Forward pass must reference vColor.rgb in albedo modulation.");
            Assert.That(text, Does.Contain("_Opacity"),
                "Forward pass must reference _Opacity in alpha modulation.");
        }

        [Test]
        public void FillExtrusionShader_WiresCustomEditor()
        {
            // The shader's CustomEditor makes LitShaderGUI.ValidateMaterial run in the inspector and derive
            // _EMISSION/_NORMALMAP/… keywords; without it the GUI class never drives keyword sync.
            string text = ReadShaderFile("FillExtrusion.shader");
            Assert.That(text, Does.Contain("CustomEditor \"MapRenderer.Unity.Editor.FillExtrusionShaderGUI\""),
                "Map/FillExtrusion must declare CustomEditor \"MapRenderer.Unity.Editor.FillExtrusionShaderGUI\".");
        }

        // ── CBUFFER location ──────────────────────────────────────────────────
        // SRP Batcher requires CBUFFER in each layer's LitInput file.

        [Test]
        public void FillLitInput_DeclaresCBUFFERUnityPerMaterial()
        {
            string text = ReadShaderFile("Fill_LitInput.hlsl");
            Assert.That(text, Does.Contain("CBUFFER_START(UnityPerMaterial)"),
                "Fill_LitInput.hlsl must declare CBUFFER_START(UnityPerMaterial) — " +
                "this is the full URP Lit CBUFFER shape required by the SRP Batcher.");

            // ParseCbufferMembers strips _*_ST entries (the SRP companion set), so _BaseMap_ST needs its
            // own text check below; the registry-parity tests above pin the other members exactly, but
            // _BaseMap_ST is a URP Lit member no registry name carries, so it is pinned here by name.
            var members = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));
            string[] required =
            {
                "_BaseColor", "_SpecColor", "_EmissionColor", "_Cutoff", "_Smoothness", "_Metallic",
                "_BumpScale", "_OcclusionStrength", "_DetailAlbedoMapScale", "_DetailNormalMapScale",
                "_Opacity",
            };
            foreach (string name in required)
                Assert.That(members, Has.Member(name),
                    $"Fill_LitInput.hlsl CBUFFER must contain '{name}' (full URP Lit shape + fill paint additions).");

            var cbufferBlock = Regex.Match(text, @"CBUFFER_START\s*\(\s*UnityPerMaterial\s*\)(.*?)CBUFFER_END",
                RegexOptions.Singleline);
            Assert.That(cbufferBlock.Success, Is.True, "Fill_LitInput.hlsl must have a CBUFFER_START(UnityPerMaterial)…CBUFFER_END block.");
            Assert.That(cbufferBlock.Groups[1].Value, Does.Contain("float4 _BaseMap_ST;"),
                "Fill_LitInput.hlsl CBUFFER must declare 'float4 _BaseMap_ST;' — ParseCbufferMembers strips " +
                "_ST-suffixed entries, so this text check is the only place that pins it.");
        }

        // ── No stale includes in pass bodies ──────────────────────────────────
        // Pass bodies must NOT self-include their layer input (the .shader provides it).

        [Test]
        public void FillPassBodies_DoNotSelfIncludeLayerInput()
        {
            string[] passBodies = new[]
            {
                "Fill_LitForwardPass.hlsl",
                "Fill_LitGBufferPass.hlsl",
                "Fill_ShadowCasterPass.hlsl",
                "Fill_DepthOnlyPass.hlsl",
                "Fill_DepthNormalsPass.hlsl",
            };
            foreach (var file in passBodies)
            {
                string text = ReadShaderFile(file);
                Assert.That(text, Does.Not.Contain("#include \"Fill_LitInput.hlsl\""),
                    $"{file} must NOT self-include Fill_LitInput.hlsl — Fill.shader provides it.");
                Assert.That(text, Does.Not.Contain("MapLitInput.hlsl"),
                    $"{file} must not reference old MapLitInput.hlsl (stale after the rename).");
                Assert.That(text, Does.Not.Contain("MapLitCore.hlsl"),
                    $"{file} must not reference deleted MapLitCore.hlsl (dissolved).");
            }
        }

        [Test]
        public void LineLitForwardPass_DoesNotSelfIncludeLayerInput()
        {
            string text = ReadShaderFile("Line_LitForwardPass.hlsl");
            Assert.That(text, Does.Not.Contain("#include \"Line_LitInput.hlsl\""),
                "Line_LitForwardPass.hlsl must NOT self-include Line_LitInput.hlsl — Line.shader provides it.");
            Assert.That(text, Does.Not.Contain("MapLineInput.hlsl"),
                "Line_LitForwardPass.hlsl must not reference old MapLineInput.hlsl (stale after the rename).");
        }

        // ── Cross-folder includes from Map/ are self-containment's one sanctioned exception ────

        [Test]
        public void MapLayerFiles_ShareOnlyViaSanctionedInclude()
        {
            // Every `../` include must resolve on disk to the Map-root PixelsToWorld.hlsl or a file in the
            // SAME kind's tree. That blocks Common/, sibling layers, outside Map/, and dangling includes.
            var mapRootShared = new[]
            {
                Path.GetFullPath(Path.Combine(MapDir, "PixelsToWorld.hlsl")),
            };

            // AllDirectories: entry points + mode-specific .hlsl now live in each kind's Lit/ and Unlit/.
            foreach (string kindDir in new[] { MapFillDir, MapLineDir, MapExtrusionDir })
            foreach (string file in Directory.EnumerateFiles(kindDir, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".hlsl", StringComparison.Ordinal)
                         || p.EndsWith(".shader", StringComparison.Ordinal)))
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                Assert.That(text, Does.Not.Contain("Common/"),
                    $"{Path.GetFileName(file)} must not reach into Common/ (each layer is self-contained).");

                string fileDir = Path.GetDirectoryName(file);
                // #include_with_pragmas is a distinct directive (ShaderLab keyword-conditional include) —
                // matched too, so a cross-folder reach hiding behind it cannot slip the guard.
                foreach (Match m in Regex.Matches(text, "#include(?:_with_pragmas)?\\s+\"(\\.\\./[^\"]*)\""))
                {
                    string target = Path.GetFullPath(Path.Combine(fileDir, m.Groups[1].Value));
                    FileAssert.Exists(target, $"{Path.GetFileName(file)} includes '{m.Groups[1].Value}', which resolves to a " +
                        $"nonexistent file: {target}");
                    Assert.That(mapRootShared.Contains(target) || IsUnder(target, kindDir), Is.True,
                        $"{Path.GetFileName(file)} has an unsanctioned cross-folder include " +
                        $"'{m.Groups[1].Value}' (→ {target}). A `../` reach may only target the sanctioned " +
                        "Map-root shared include (PixelsToWorld.hlsl) or a shared include in the SAME kind " +
                        "root (the Lit/Unlit split).");
                }
            }
        }

        // ── The layer-fade draw gate ──────────────────────────────────────

        /// <summary>
        /// Every kind directory whose Input files declare <c>_Opacity</c> — the PREDICATE that decides which
        /// kinds the fade gate applies to. Today it enumerates to Fill, Line and FillExtrusion; Symbol is
        /// excluded because its Input files declare no <c>_Opacity</c>. Hard-coding the three would leave a
        /// fourth <c>_Opacity</c>-bearing kind silently unfenced, which is what this fence exists to prevent.
        /// </summary>
        private static string[] OpacityBearingKindDirs()
            => Directory.EnumerateDirectories(MapDir)
                .Where(d => Directory.EnumerateFiles(d, "*Input.hlsl", SearchOption.AllDirectories)
                    .Any(f => File.ReadAllText(f, Encoding.UTF8).Contains("_Opacity")))
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToArray();

        /// <summary>
        /// NO map fragment pass discards on the fade uniform. Fade is a per-slot DRAW gate
        /// (<c>ITileRenderBackend.SetLayerVisible</c>) — a gated layer submits no draw item, so the fragment
        /// never runs and a clip there is dead work. Limitation: most pass sites cannot rasterise in the
        /// shipped configuration, so this fence is their sole observer. It matches the <c>_Opacity</c>
        /// argument, so other <c>clip()</c>s pass, and strips comments first.
        /// </summary>
        [Test]
        public void NoMapPassBody_DiscardsOnTheLayerFadeUniform()
        {
            string[] kindDirs = OpacityBearingKindDirs();
            Assert.That(kindDirs, Is.Not.Empty,
                "no kind directory under Shaders/Map/ declares _Opacity — the predicate that selects which " +
                "kinds carry the fade gate matched nothing, so this fence would be vacuous.");

            foreach (string kindDir in kindDirs)
            foreach (string pass in Directory.EnumerateFiles(kindDir, "*Pass.hlsl", SearchOption.AllDirectories))
            {
                string body = ShaderPropertyParser.StripHlslComments(File.ReadAllText(pass, Encoding.UTF8));

                Assert.That(Regex.IsMatch(body, @"\bclip\s*\([^;]*\b_Opacity\b"), Is.False,
                    $"{Path.GetFileName(pass)} discards a fragment on _Opacity. The fade gate is a " +
                    "per-slot DRAW gate now (ITileRenderBackend.SetLayerVisible): a gated layer submits no " +
                    "draw item, so this clip runs only for layers that are supposed to be drawn.");

                Assert.That(body, Does.Not.Contain("MAP_CLIP_IF_ABSENT"),
                    $"{Path.GetFileName(pass)} calls MAP_CLIP_IF_ABSENT. No such macro is defined, so the " +
                    "pass would not compile; fade is decided at submission, not in the fragment.");
            }
        }

        /// <summary>
        /// Nothing under Shaders/ defines a fade-discard macro or its threshold. The companion fence
        /// above only reads pass BODIES, so a definition that nothing includes yet would sit unobserved
        /// until someone wired it up.
        /// </summary>
        [Test]
        public void NoShaderFile_DefinesAFadeDiscardMacro()
        {
            FileAssert.DoesNotExist(Path.Combine(MapDir, "LayerFade.hlsl"), "Shaders/Map/LayerFade.hlsl defines a fragment-side fade threshold. Fade is a " +
                "per-slot draw gate (ITileRenderBackend.SetLayerVisible) — see docs/tile-pipeline-design.md § \"The draw gate — a layer draws, or it is not submitted\".");

            foreach (string file in Directory.EnumerateFiles(
                         ShaderPropertyParser.ShadersDir, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".hlsl", StringComparison.Ordinal)
                              || f.EndsWith(".shader", StringComparison.Ordinal)))
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                Assert.That(text, Does.Not.Contain("MAP_CLIP_IF_ABSENT"),
                    $"{Path.GetFileName(file)} defines a fragment-side fade discard.");
                Assert.That(text, Does.Not.Contain("MAP_LAYER_FADE_EPSILON"),
                    $"{Path.GetFileName(file)} defines a second copy of the visibility threshold — " +
                    "ZoomStyleApplier.VisibleOpacityEpsilon is the only one.");
            }
        }

        // True when <c>path</c> lies inside directory <c>dir</c> (both normalised) — used to confine a
        // shader's cross-folder `../` includes to its own kind tree.
        private static bool IsUnder(string path, string dir)
        {
            string full = Path.GetFullPath(path).Replace('\\', '/');
            string root = Path.GetFullPath(dir).Replace('\\', '/').TrimEnd('/') + "/";
            return full.StartsWith(root, StringComparison.Ordinal);
        }

        // ── Shared px→world include ───────────────────────────────────────────

        /// <summary>
        /// <c>MapPixelsToWorld</c> is genuinely shared by every layer that converts a screen-pixel offset to
        /// world metres in the vertex shader: the shared file exists, every carrier includes it, and no
        /// carrier re-defines the function locally, which is the clause that catches re-duplication.
        /// </summary>
        [Test]
        public void SharedPixelsToWorldInclude_ReferencedByEveryCarrier()
        {
            string sharedPath = Path.Combine(MapDir, "PixelsToWorld.hlsl");
            FileAssert.Exists(sharedPath, "Shaders/Map/PixelsToWorld.hlsl must exist — the shared px→world include.");
            string sharedText = File.ReadAllText(sharedPath, Encoding.UTF8);
            Assert.That(sharedText, Does.Contain("float MapPixelsToWorld("),
                "Shaders/Map/PixelsToWorld.hlsl must define MapPixelsToWorld — that is the point of the hoist.");

            // Carriers, not a hardcoded count — translate appends FillExtrusion/FillExtrusion_VertexModify.hlsl.
            string[] carriers =
            {
                "Fill_VertexModify.hlsl",
                "Line_VertexExtrude.hlsl",
                "FillExtrusion_VertexModify.hlsl",
            };

            foreach (var file in carriers)
            {
                string text = ReadShaderFile(file);
                Assert.That(text, Does.Contain("#include \"../PixelsToWorld.hlsl\""),
                    $"{file} must include ../PixelsToWorld.hlsl (the sanctioned shared px→world include).");
                Assert.That(text, Does.Not.Contain("float MapPixelsToWorld("),
                    $"{file} must NOT locally re-define MapPixelsToWorld — it is shared via " +
                    "../PixelsToWorld.hlsl now; a local re-definition would silently re-duplicate it.");
            }
        }

        // ── The fill vertex hook has no early exit ────────────────────────────

        [Test]
        public void FillVertexModify_ContainsNoReturn()
        {
            // Non-obvious why: fill-translate is [0,0] on every shipped layer, so an early `return` in its guard
            // would make any code placed after it dead on almost every shipped vertex. The forbidden-token
            // check fails CLOSED.
            string code = ShaderPropertyParser.StripHlslComments(
                File.ReadAllText(ShaderPropertyParser.MapShaderPath("Fill_VertexModify.hlsl"), Encoding.UTF8));

            Assert.That(Regex.IsMatch(code, @"\breturn\b"), Is.False,
                "Fill_VertexModify.hlsl contains a `return`. The hook must have no early exit: with " +
                "fill-translate [0,0] on every shipped layer, anything placed after one is dead in the " +
                "only case that ships. Use an `if` block instead.");
        }

        // ── Shader feature pragmas (required for tooth #1 normal map, tooth #3 specular) ──

        /// <summary>Required shader-feature pragmas in Fill.shader: ForwardLit's normal map (tooth #1) and
        /// its metallic/spec-gloss map (tooth #3), each scoped from <c>Name "ForwardLit"</c>, and GBuffer's
        /// own normal map (deferred support), scoped from <c>Name "GBuffer"</c>. Every row's scope ends at
        /// the NEXT <c>Name "</c> marker (or EOF), so a pragma from a DIFFERENT pass cannot satisfy it.</summary>
        [Test]
        [TestCase("Name \"ForwardLit\"", "shader_feature_local _NORMALMAP",
            "Fill.shader ForwardLit pass must have '#pragma shader_feature_local _NORMALMAP'. Without it, " +
            "binding a normal map compiles to nothing and tooth #1 cannot pass.",
            TestName = "FillShader_HasRequiredPragma(ForwardLit_NormalMap)")]
        [TestCase("Name \"ForwardLit\"", "shader_feature_local_fragment _METALLICSPECGLOSSMAP",
            "Fill.shader must have '#pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP'. Without " +
            "it, binding a metallic map compiles to nothing and tooth #3 cannot pass.",
            TestName = "FillShader_HasRequiredPragma(MetallicSpecGlossMap)")]
        [TestCase("Name \"GBuffer\"", "shader_feature_local _NORMALMAP",
            "Fill.shader GBuffer pass must also have _NORMALMAP pragma.",
            TestName = "FillShader_HasRequiredPragma(GBuffer_NormalMap)")]
        public void FillShader_HasRequiredPragma(string scopeStartMarker, string requiredPragma, string message)
        {
            string text = ReadShaderFile("Fill.shader");
            int idx = text.IndexOf(scopeStartMarker, StringComparison.Ordinal);
            Assert.That(idx, Is.GreaterThanOrEqualTo(0), $"{scopeStartMarker} not found in Fill.shader.");

            // Bound the scope to THIS pass only — up to the next pass's Name marker, or EOF for the last
            // pass — so a pragma copied into a DIFFERENT pass cannot satisfy this row.
            int scopeStart = idx + scopeStartMarker.Length;
            int nextPass = text.IndexOf("Name \"", scopeStart, StringComparison.Ordinal);
            string scoped = nextPass >= 0 ? text.Substring(scopeStart, nextPass - scopeStart) : text.Substring(scopeStart);

            Assert.That(scoped, Does.Contain(requiredPragma), message);
        }

        // ── Shader declaration names (Map/<Layer>) ────────────────────────────────────

        [Test]
        public void Shaders_DeclareMapLayerNames()
        {
            Assert.That(ReadShaderFile("Fill.shader"), Does.Contain("Shader \"Map/Fill\""),
                "Fill.shader must declare Shader \"Map/Fill\".");
            Assert.That(ReadShaderFile("Line.shader"), Does.Contain("Shader \"Map/Line\""),
                "Line.shader must declare Shader \"Map/Line\".");
            Assert.That(ReadShaderFile("FillExtrusion.shader"), Does.Contain("Shader \"Map/FillExtrusion\""),
                "FillExtrusion.shader must declare Shader \"Map/FillExtrusion\".");
        }

        // ── License files ─────────────────────────────────────────────────────

        [Test]
        public void ThirdPartyNotices_HasUCLEntry()
        {
            string path = Path.Combine(RepoRoot, "THIRD-PARTY-NOTICES.txt");
            FileAssert.Exists(path);
            string text = File.ReadAllText(path, Encoding.UTF8);
            Assert.That(text, Does.Contain("Unity Companion License"),
                "THIRD-PARTY-NOTICES.txt must have a UCL entry (license requirement).");
            Assert.That(text, Does.Contain("Fill_LitInput.hlsl"),
                "THIRD-PARTY-NOTICES.txt UCL entry must list the mirrored files (e.g. Fill_LitInput.hlsl).");
            Assert.That(text, Does.Contain("FillExtrusion_LitInput.hlsl"),
                "THIRD-PARTY-NOTICES.txt UCL entry must list the FillExtrusion mirrored files.");
        }

        [Test]
        public void UnityCompanionLicenseFile_Exists()
        {
            // Resolved by name via AssetDatabase (move-proof): the file lives under
            // Assets/Code/ThirdParty/ today, but its exact folder isn't asserted here.
            string path = ShaderPropertyParser.ResolveAssetPathByName("UnityCompanionLicense.txt");
            FileAssert.Exists(path);
            string text = File.ReadAllText(path, Encoding.UTF8);
            Assert.That(text, Does.Contain("Unity Companion License"),
                "UnityCompanionLicense.txt must contain the actual UCL text.");
        }

        // ── Helper ─────────────────────────────────────────────────────────────

        // Reads a shader source file by NAME via MapShaderPath; only the layout tests (ShaderFiles_*,
        // MapKindRoots_HoldOnlyGenuinelySharedIncludes) assert folder structure.
        private static string ReadShaderFile(string filename)
            => File.ReadAllText(ShaderPropertyParser.MapShaderPath(filename), Encoding.UTF8);
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillBandAttributeTests — fill-band forward/depth pass vertex-attribute parity
    // ───────────────────────────────────────────────────────────────────────────────────

// FillBandAttributeTests — the fill boundary band's plumbing, checked by reading files.
// Limitation: an unbound TEXCOORDn semantic zero-fills silently, and in the four DEPTH-writing passes the
// band fragments are clipped, so a missing attribute there changes no pixel or depth sample. A missing
// depth clip does show: a coverage-0 band fragment under ZWrite On fattens the shadow silhouette.

    [TestFixture]
    public class FillBandAttributeTests : BaseTestFixture
    {
        /// <summary>The two fill entry points. Every pass body below is reached through one of them.</summary>
        private static readonly string[] FillShaders = { "Fill.shader", "FillUnlit.shader" };

        /// <summary>The band attribute's mesh slot. TexCoord1/TexCoord2 would silently feed the forward and
        /// GBuffer passes' lightmap UVs instead.</summary>
        private const string BandSemantic = "TEXCOORD3";

        /// <summary>One ShaderLab <c>Pass</c> block, reduced to what these teeth ask about.</summary>
        private readonly struct FillPass
        {
            /// <summary>The entry point that declares this pass, for failure messages.</summary>
            public readonly string Shader;

            /// <summary>File name of the pass body the block includes, e.g. <c>Fill_DepthOnlyPass.hlsl</c>.</summary>
            public readonly string BodyFile;

            /// <summary>True when the block hardcodes <c>ZWrite On</c> — the discriminator for "this pass
            /// writes depth", which is what makes clipping the band mandatory rather than optional.</summary>
            public readonly bool WritesDepth;

            /// <param name="shader">The declaring entry point.</param>
            /// <param name="bodyFile">The included pass-body file name.</param>
            /// <param name="writesDepth">Whether the block hardcodes <c>ZWrite On</c>.</param>
            public FillPass(string shader, string bodyFile, bool writesDepth)
            {
                Shader      = shader;
                BodyFile    = bodyFile;
                WritesDepth = writesDepth;
            }
        }

        /// <summary>
        /// Every <c>Pass</c> block of both fill entry points, paired with the pass body it includes —
        /// derived from the shaders themselves rather than listed here, so a seventh pass joins these
        /// teeth by existing. Each shader's block count is checked against its own <c>Name "..."</c> line
        /// count, and every block must resolve an <c>#include</c>: either regex missing a block FAILS the
        /// enumeration instead of silently dropping it (a dropped pass would otherwise turn every tooth
        /// below into a vacuous loop over fewer passes than the shader actually declares).
        /// </summary>
        private static List<FillPass> EnumerateFillPasses()
        {
            var passes = new List<FillPass>();
            foreach (string shaderName in FillShaders)
            {
                string text = File.ReadAllText(ShaderPropertyParser.MapShaderPath(shaderName));
                // Blocks start at a `Pass` line; the last one runs to end of file. Splitting on the keyword
                // is enough because a pass body's own braces never contain another `Pass` line.
                string[] blocks = Regex.Split(text, @"^\s*Pass\s*$", RegexOptions.Multiline);
                int nameLineCount = Regex.Matches(text, "^\\s*Name\\s+\"", RegexOptions.Multiline).Count;
                Assert.That(blocks.Length - 1, Is.EqualTo(nameLineCount),
                    $"{shaderName}: found {blocks.Length - 1} 'Pass' block(s) but {nameLineCount} 'Name \"...\"' " +
                    "line(s) — the block-split regex missed one.");

                for (int i = 1; i < blocks.Length; i++)   // [0] is everything before the first Pass
                {
                    Match include = Regex.Match(blocks[i], "#include\\s+\"(?:\\.\\./)?([A-Za-z0-9_]+Pass\\.hlsl)\"");
                    Assert.That(include.Success, Is.True,
                        $"{shaderName}: Pass block {i} has no #include \"...Pass.hlsl\" the enumerator can " +
                        "resolve — that would otherwise silently drop this pass from every tooth below.");
                    bool zWriteOn = Regex.IsMatch(blocks[i], @"^\s*ZWrite\s+On\s*$", RegexOptions.Multiline);
                    passes.Add(new FillPass(shaderName, include.Groups[1].Value, zWriteOn));
                }
            }
            return passes;
        }

        /// <summary>Reads a pass body by file name, with comments stripped so a mention in prose can never
        /// satisfy a check about code.</summary>
        /// <param name="bodyFile">Pass-body file name, as included by the shader.</param>
        private static string ReadPassBody(string bodyFile)
            => ShaderPropertyParser.StripHlslComments(
                   File.ReadAllText(ShaderPropertyParser.MapShaderPath(bodyFile)));

        // ── The semantic, and the hook that carries it ──────────────────────────────────────────

        /// <summary>
        /// Every fill pass declares the band attribute at the mesh's own slot and threads it through the
        /// vertex hook. This is the tooth a render cannot be: at <c>side = 0</c> a zero-filled unbound
        /// semantic and a correctly bound one produce identical pixels.
        /// </summary>
        [Test]
        public void EveryFillPass_DeclaresTheBandAttribute_AndThreadsItThroughTheHook()
        {
            List<FillPass> passes = EnumerateFillPasses();
            Assert.That(passes, Is.Not.Empty,
                "found no fill pass blocks across Fill.shader + FillUnlit.shader — a block regex that " +
                "drifted would make every assertion below vacuous.");

            foreach (string bodyFile in passes.Select(p => p.BodyFile).Distinct())
            {
                var semantics = ShaderPropertyParser.ParseStructSemantics(
                    ShaderPropertyParser.MapShaderPath(bodyFile), "Attributes");

                Assert.That(semantics, Does.Contain(BandSemantic),
                    $"{bodyFile}'s Attributes struct does not declare {BandSemantic}. An unbound semantic " +
                    "zero-fills SILENTLY — the compiler, every uniform-path test and every debug view read " +
                    "this as correct, which is why it is checked here and not by rendering.");

                Assert.That(ReadPassBody(bodyFile), Does.Match(@"MapVertexModify\([^;]*input\.band"),
                    $"{bodyFile} declares the band attribute but never hands it to MapVertexModify, so the " +
                    "fragment stage reads an uninitialised side.");
            }
        }

        // ── Shade it or clip it — never neither ─────────────────────────────────────────────────

        /// <summary>
        /// A fill pass either shades the band (multiplying its coverage into alpha) or clips it away —
        /// and which one is decided by whether the pass writes depth, not by a list kept here. A pass
        /// hardcoding <c>ZWrite On</c> must clip: a coverage-0 band fragment still writes depth under it,
        /// which would fatten the depth prepass and the shadow silhouette by the band's whole width.
        /// </summary>
        [Test]
        public void EveryFillPass_EitherShadesTheBandOrClipsIt_ByWhetherItWritesDepth()
        {
            List<FillPass> passes = EnumerateFillPasses();
            Assert.That(passes, Is.Not.Empty, "precondition: found no fill pass blocks.");
            Assert.That(passes.Any(p => p.WritesDepth), Is.True,
                "precondition: at least one depth-writing pass block (ShadowCaster/GBuffer/DepthOnly/" +
                "DepthNormals) must be detected by its hardcoded ZWrite On, or the `if` branch below never runs.");
            Assert.That(passes.Any(p => !p.WritesDepth), Is.True,
                "precondition: at least one non-depth-writing (shading) pass block must be detected, or the " +
                "`else` branch below never runs.");

            foreach (FillPass pass in passes)
            {
                string body     = ReadPassBody(pass.BodyFile);
                bool shades     = body.IndexOf("MapFillBandCoverage(input.side)", StringComparison.Ordinal) >= 0;
                // The whole ternary: the inverted `clip(input.side > 0 ? 1 : -1)` keeps only the band,
                // and a match on the condition alone reads it as correct.
                bool clips      = Regex.IsMatch(body,
                    @"clip\(\s*input\.side\s*>\s*0\.0\s*\?\s*-1\.0\s*:\s*1\.0\s*\)");
                string where    = $"{pass.Shader} -> {pass.BodyFile}";

                if (pass.WritesDepth)
                {
                    Assert.That(clips, Is.True,
                        $"{where} hardcodes ZWrite On but does not clip the band. A coverage-0 band fragment " +
                        "writes depth and casts a shadow, so the silhouette would grow by the band's width.");
                    Assert.That(shades, Is.False,
                        $"{where} is a depth-writing pass and must not shade the band.");
                }
                else
                {
                    Assert.That(shades, Is.True,
                        $"{where} shades pixels but never multiplies MapFillBandCoverage into alpha — the " +
                        "boundary would stay hard.");
                }
            }
        }

        /// <summary>
        /// Coverage is folded in AFTER the fill-pattern branch. That branch REPLACES alpha rather than
        /// modulating it, so coverage applied earlier is discarded on a patterned fill and its boundary
        /// silently stays hard — a defect no unpatterned fixture can see.
        /// </summary>
        [Test]
        public void ForwardPasses_ApplyBandCoverage_AfterThePatternBranch()
        {
            List<FillPass> forward = EnumerateFillPasses().Where(p => !p.WritesDepth).ToList();
            Assert.That(forward, Is.Not.Empty,
                "found no shading (non-depth-writing) fill pass blocks — without this the loop below " +
                "iterates nothing and passes vacuously, which is the one failure a green result cannot " +
                "distinguish itself from.");

            foreach (FillPass pass in forward)
            {
                string body = ReadPassBody(pass.BodyFile);
                int patternReplace = body.LastIndexOf("patternTexel.a * _Opacity", StringComparison.Ordinal);
                int coverage       = body.IndexOf("MapFillBandCoverage(input.side)", StringComparison.Ordinal);

                Assert.That(patternReplace, Is.GreaterThanOrEqualTo(0),
                    $"{pass.BodyFile} no longer carries the fill-pattern alpha replacement this tooth orders " +
                    "against — the ordering rule it encodes may no longer apply, or the search string drifted.");
                Assert.That(coverage, Is.GreaterThan(patternReplace),
                    $"{pass.BodyFile} multiplies the band coverage into alpha BEFORE the fill-pattern branch " +
                    "replaces it, so a patterned fill loses its boundary antialiasing silently.");
            }
        }

        // ── The formula is the shipped one ──────────────────────────────────────────────────────

        /// <summary>
        /// The fill's coverage AND the Visual snapshot probe's coverage are both the line path's outer-edge
        /// ramp, read verbatim from disk. Limitation: the gradient must stay EUCLIDEAN, and <c>fwidth</c>
        /// (Manhattan) agrees with it on every axis-aligned fixture, so only this tooth sees the swap. The
        /// one licensed difference is <c>absSide</c> in the line versus <c>side</c> in the fill and the
        /// probe, whose bands never go negative.
        /// </summary>
        [Test]
        public void FillBandCoverage_IsVerbatimTheShippedLineCoverageExpression()
        {
            string root = Directory.GetParent(Application.dataPath)!.FullName;
            string line = File.ReadAllText(ShaderPropertyParser.MapShaderPath("Line_VertexExtrude.hlsl"));
            string fill = File.ReadAllText(ShaderPropertyParser.MapShaderPath("Fill_BandCoverage.hlsl"));
            string probe = File.ReadAllText(Path.Combine(root,
                "Assets", "Tests", "MapRenderer.Tests.Visual", "FillBandCoverageProbe.shader"));

            const string gradient = "max(length(float2(ddx(side), ddy(side))), 1e-6)";
            const string coverage = "saturate((1.0 - absSide) / sideGrad)";

            Assert.That(line, Does.Contain(gradient),
                "Line_VertexExtrude.hlsl no longer computes the Euclidean side gradient this way; the fill " +
                "band and the visual probe are both reusing an expression the line path has stopped using.");
            Assert.That(line, Does.Contain(coverage),
                "Line_VertexExtrude.hlsl no longer computes the outer-edge coverage this way.");

            Assert.That(fill, Does.Contain(gradient),
                "Fill_BandCoverage.hlsl must carry the shipped Euclidean gradient verbatim — an fwidth here " +
                "renders a 45-degree silhouette WRONG while every axis-aligned fixture stays green — and " +
                "wrong in the unobvious direction: coverage is (1 - side) DIVIDED by the gradient, so an " +
                "over-read gradient makes coverage fall faster. Measured, the 45-degree ramp narrows to " +
                "0.80 px and the coverage at the boundary itself drops below 1.");
            Assert.That(fill, Does.Contain(coverage.Replace("absSide", "side")),
                "Fill_BandCoverage.hlsl must carry the shipped coverage expression, with the line's signed " +
                "absSide as the fill's unsigned side and nothing else changed.");

            Assert.That(probe, Does.Contain(gradient.Replace("side)", "input.side)")),
                "FillBandCoverageProbe.shader (the Visual/Meshing snapshot probe) must carry the shipped " +
                "gradient expression verbatim, or its comparison measures a formula the product no longer uses.");
            Assert.That(probe, Does.Contain(coverage),
                "FillBandCoverageProbe.shader must carry the shipped coverage expression verbatim.");
        }

        // ── The displacement and the coverage coordinate never multiply ─────────────────────────

        /// <summary>
        /// <c>Fill_VertexModify.hlsl</c>'s band displacement (<c>band.xy</c>) must NOT be scaled by the coverage
        /// coordinate <c>band.z</c>. Limitation: on the FLAT arm the product equals the correct value, so only
        /// the CURVED arm shows it, where subdivision lerps both halves and the strip pinches at every midpoint.
        /// The forbidden-token check fails CLOSED.
        /// </summary>
        [Test]
        public void BandDisplacement_IsNotScaledByTheCoverageCoordinate()
        {
            string code = ShaderPropertyParser.StripHlslComments(
                File.ReadAllText(ShaderPropertyParser.MapShaderPath("Fill_VertexModify.hlsl"), Encoding.UTF8));

            int anchor = code.IndexOf("MapPixelsToWorld(bandCenterWS", StringComparison.Ordinal);
            Assert.That(anchor, Is.GreaterThanOrEqualTo(0),
                "Fill_VertexModify.hlsl no longer measures the band displacement through " +
                "MapPixelsToWorld(bandCenterWS, ...) — this tooth reads that statement and can no longer " +
                "find it, so it is measuring nothing. Re-point it at the displacement site.");
            // The WHOLE statement, so a `* band.z` BEFORE the MapPixelsToWorld call is seen. No earlier ';'
            // gives start 0, so the window only widens.
            int start = code.LastIndexOf(';', anchor) + 1;
            int end   = code.IndexOf(';', anchor);
            Assert.That(end, Is.GreaterThan(start), "the displacement statement must be terminated.");
            string statement = code.Substring(start, end - start);

            Assert.That(Regex.IsMatch(statement, @"\bband\s*\.\s*z\b"), Is.False,
                "Fill_VertexModify.hlsl scales the band displacement by band.z. The displacement is band.xy " +
                "alone; band.z is only the coverage coordinate. Multiplying them is exact on the flat arm " +
                "(z is 0 or 1 there) and quadratic on the globe, where subdivision lerps both halves across " +
                $"a split band edge — a quarter-pixel pinch at every midpoint. Statement: {statement}");
        }

        // ── The band relies on Cull Back ────────────────────────────────────────────────────────

        /// <summary>
        /// Both committed fill materials cull back faces. <c>FillBandJob</c> emits a second, reverse-wound copy of a band
        /// triangle that can twist: Cull Back draws exactly one of the pair. With Cull Off both draw, and a translucent
        /// outline composites that triangle twice.
        /// </summary>
        [Test]
        public void ShippedFillMaterials_CullBack_SoATwistCoverDrawsOnce([Values] MapRenderer.Unity.Rendering.Materials.RenderMode mode)
        {
            Material fill = MapMaterialSetTestUtil.Load(mode).FillMaterial;

            Assert.That(fill.GetInt("_Cull"), Is.EqualTo((int)UnityEngine.Rendering.CullMode.Back),
                $"the committed {mode} fill material no longer culls back faces. The fill band relies on it: FillBandJob emits a " +
                "reverse-wound copy of a band triangle that can twist, and only Cull Back draws exactly one of the two.");
        }

        // ── The mesh actually carries it ────────────────────────────────────────────────────────

        /// <summary>
        /// The built fill mesh declares the band attribute on stream 1 with the stride that implies. The
        /// shader-side teeth above are all satisfied by declarations; this is the half that says the mesh
        /// supplies what they declare — the two ends of exactly the binding that zero-fills in silence.
        /// </summary>
        [Test]
        public void BuiltFillMesh_CarriesTheBandAttribute_OnTheSharedUvStream()
        {
            // FIRST, before the mesh is built: a struct that has grown makes GetVertexData<T>(1) throw
            // inside the builder, and a harness exception is not this tooth's message.
            Assert.That(UnsafeUtility.SizeOf<StyledFillTileBuilder.FillPatternUvBand>(), Is.EqualTo(20),
                "FillPatternUvBand must stay 20 B to match stream 1's descriptors. A field added here and " +
                "not to FillVertexDescriptors makes GetVertexData<FillPatternUvBand>(1) return a SHORT " +
                "array that the write job walks off the end of — bounds-checked in the Editor, silent in " +
                "a release player.");

            var (fillGo, material) = FillSceneHelper.BuildFillGo();
            Track(fillGo);
            Track(material);
            {
                Mesh mesh = fillGo.GetComponent<MeshFilter>().sharedMesh;
                Assert.That(mesh, Is.Not.Null, "precondition: the fixture layer must build a real mesh.");

                VertexAttributeDescriptor band = mesh.GetVertexAttributes()
                    .FirstOrDefault(a => a.attribute == VertexAttribute.TexCoord3);

                Assert.That(band.attribute, Is.EqualTo(VertexAttribute.TexCoord3),
                    "The fill mesh declares no TexCoord3 — the shaders' band attribute would zero-fill.");
                Assert.That(band.dimension, Is.EqualTo(3), "The band attribute is (dirEast, dirNorth, side).");
                Assert.That(band.format, Is.EqualTo(VertexAttributeFormat.Float32),
                    "The band attribute must be Float32 — the shaders read it as a float3.");
                Assert.That(band.stream, Is.EqualTo(1),
                    "The band attribute shares stream 1 with TexCoord0: the fill mesh already uses all four " +
                    "streams Unity allows, so it could not have one of its own.");
                Assert.That(mesh.GetVertexBufferStride(1), Is.EqualTo(20),
                    "Stream 1 is TexCoord0 (8 B) + TexCoord3 (12 B). A stride that is not 20 means the " +
                    "descriptors and the struct the write job casts the stream to have diverged.");
            }
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // CoreAssemblyBoundaryTests — MapRenderer.Core stays engine-free; no UnityEngine/Unity.* crosses in
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fence that makes the Core/Jobs split enforceable: Core keeps the managed evaluation surface and
    /// never reads coordinates; Jobs owns the blittable geometry. Adding <c>"Unity.Collections"</c> to
    /// <c>MapRenderer.Core.asmdef</c> compiles and passes every other test, so only this tooth sees it.
    /// The source scan strips comments, because Core's doc comments mention <c>NativeArray</c>.
    /// </summary>
    [TestFixture]
    public class CoreAssemblyBoundaryTests
    {
        /// <summary>Code forms that mean "this file actually uses Unity.Collections", as opposed to naming it
        /// in prose.</summary>
        private static readonly string[] CollectionsCodeForms =
        {
            "using Unity.Collections", "NativeArray<", "NativeList<", "Unity.Collections.",
        };

        [Test]
        public void MapRendererCore_ReferencesNoUnityCollections()
        {
            // ── Clause 1: the asmdef.
            string coreAsmdefPath = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Core", "MapRenderer.Core.asmdef");
            FileAssert.Exists(coreAsmdefPath);
            string coreAsmdef = File.ReadAllText(coreAsmdefPath);

            // Non-vacuity: the file was really found and really parsed as a reference list — an empty or
            // unreadable asmdef would satisfy a "contains no Unity.Collections" claim trivially.
            List<string> coreReferences = ReferencesOf(coreAsmdef, coreAsmdefPath);
            Assert.GreaterOrEqual(coreReferences.Count, 2,
                "precondition: the Core asmdef must list at least two references — a parse that found none " +
                "would make the absence claim below vacuous");
            CollectionAssert.Contains(coreReferences, "Unity.Mathematics",
                "precondition: Unity.Mathematics is Core's math dependency and must be among the parsed " +
                "references — proof the parser sees real entries");

            foreach (string reference in coreReferences)
            {
                Assert.IsFalse(reference.Contains("Unity.Collections", StringComparison.Ordinal),
                    "MapRenderer.Core.asmdef must NOT reference Unity.Collections. Adding it is the one-line " +
                    "edit that would let coordinate-reading code stay in Core and erase the Core/Jobs split " +
                    "(Core keeps the managed evaluation surface; Jobs owns the blittable geometry). The move " +
                    $"SymbolFeatureExtractor to MapRenderer.Unity rather than take it. Found: '{reference}'.");
            }

            // ── Clause 2: no source file under Core uses Unity.Collections in CODE.
            string coreRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Core");
            string[] coreFiles = Directory.GetFiles(coreRoot, "*.cs", SearchOption.AllDirectories);
            // Non-empty, not a file-count floor: Core is shrinking, and a floor would fail a successful
            // migration. This catches a coreRoot that resolves to an empty or wrong directory.
            Assert.IsNotEmpty(coreFiles,
                "precondition: the Core scan must have visited at least one .cs file — a scan that visited " +
                "none would report 'no offenders' just as loudly, and the Jobs positive control does not " +
                "cover this (it scans a different directory)");

            var offenders = new List<string>();
            foreach (string file in coreFiles)
            {
                string code = StripComments(File.ReadAllText(file));
                foreach (string form in CollectionsCodeForms)
                    if (code.Contains(form, StringComparison.Ordinal))
                        offenders.Add($"{file.Substring(coreRoot.Length)} ('{form}')");
            }

            // Positive control: the SAME scan over MapRenderer.Unity finds Unity.Collections in the asmdef
            // and in at least one source file, so the matcher can see what it reports absent from Core.
            string unityAsmdefPath = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "MapRenderer.Unity.asmdef");
            FileAssert.Exists(unityAsmdefPath);
            List<string> unityReferences = ReferencesOf(File.ReadAllText(unityAsmdefPath), unityAsmdefPath);
            CollectionAssert.Contains(unityReferences, "Unity.Collections",
                "positive control: MapRenderer.Unity.asmdef MUST reference Unity.Collections — if this fails, " +
                "the asmdef parser is broken and Core's clean result above means nothing");

            string unityRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity");
            int unitySourceHits = 0;
            foreach (string file in Directory.GetFiles(unityRoot, "*.cs", SearchOption.AllDirectories))
            {
                string code = StripComments(File.ReadAllText(file));
                foreach (string form in CollectionsCodeForms)
                    if (code.Contains(form, StringComparison.Ordinal)) { unitySourceHits++; break; }
            }
            Assert.Greater(unitySourceHits, 0,
                "positive control: the SAME comment-stripped source scan MUST find Unity.Collections code " +
                "forms under MapRenderer.Unity — otherwise the scan is blind and Core's zero is meaningless");

            Assert.IsEmpty(offenders,
                "no .cs file under MapRenderer.Core may USE Unity.Collections (comment-stripped: Core " +
                "legitimately MENTIONS NativeArray in a dozen doc comments explaining blittability). " +
                $"Offenders: {string.Join(", ", offenders)}");
        }

        /// <summary>Pulls the <c>references</c> array out of an asmdef. Deliberately a narrow regex rather
        /// than a JSON parser — the test assembly has no JSON dependency, and the asmdef shape is fixed.</summary>
        private static List<string> ReferencesOf(string asmdef, string path)
        {
            Match block = Regex.Match(asmdef, @"""references""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
            Assert.IsTrue(block.Success, $"expected a 'references' array in {path}");

            var references = new List<string>();
            foreach (Match entry in Regex.Matches(block.Groups[1].Value, @"""([^""]+)"""))
                references.Add(entry.Groups[1].Value);
            return references;
        }

        /// <summary>Strips block comments and then everything from <c>//</c> (which covers <c>///</c>) to end
        /// of line. A grep guard, not a C# parser.</summary>
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // StyleModelAssemblyBoundaryTests — the style model lives in MapRenderer.Unity, not Core
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The style model and parser live in <c>MapRenderer.Unity</c> (namespace <c>MapRenderer.Unity.Style</c>),
    /// not <c>MapRenderer.Core</c>. This pins both halves: the model is declared where the namespace says it
    /// is, and no type reflected out of the Core assembly has a namespace with a <c>Style</c> segment.
    /// </summary>
    [TestFixture]
    public class StyleModelAssemblyBoundaryTests
    {
        [Test]
        public void StyleParser_IsDeclaredInMapRendererUnity_UnderTheStyleNamespace()
        {
            Type styleParser = typeof(MapRenderer.Unity.Style.StyleParser);
            Assert.AreEqual("MapRenderer.Unity.Style", styleParser.Namespace,
                "StyleParser must be declared in the MapRenderer.Unity.Style namespace.");
            Assert.AreEqual(
                typeof(MapRenderer.Unity.Rendering.Tile.Processing.ITileFeatureSource).Assembly,
                styleParser.Assembly,
                "StyleParser must live in the MapRenderer.Unity assembly, alongside the rest of the product.");
        }

        [Test]
        public void NoTypeInMapRendererCore_HasANamespaceWithAStyleSegment()
        {
            Assembly coreAssembly = typeof(MapRenderer.Core.Geo.GeoCoordinate).Assembly;
            Type[] coreTypes = coreAssembly.GetTypes();
            Assert.Greater(coreTypes.Length, 100,
                "precondition: the Core assembly must reflect a real corpus (>100 types); an empty or wrong " +
                "assembly would report 'no offenders' just as loudly");

            List<string> offenders = coreTypes
                .Where(t => t.Namespace != null && t.Namespace.Split('.').Contains("Style"))
                .Select(t => t.FullName)
                .ToList();

            Assert.IsEmpty(offenders,
                "no type under MapRenderer.Core may have a namespace with a 'Style' segment — the style " +
                $"model lives in MapRenderer.Unity. Offenders: {string.Join(", ", offenders)}");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MdCitationFenceTests — every *.md citation in source resolves to a tracked file
    // ───────────────────────────────────────────────────────────────────────────────────

// Unity EditMode only — shells out to git and reads sources via ShaderPropertyParser.RepoRoot.
// Non-obvious why: comments are rejoined into blocks, because a hard-wrapped path splits across two `//`
// lines. Only a citation with a `*.md` FILENAME is checked. A renamed cited `.md` reds this; fix the citation.

    /// <summary>
    /// Tripwire: no committed <c>.cs</c>/<c>.hlsl</c>/<c>.shader</c>/<c>.sh</c>/<c>.py</c> file under
    /// <c>Assets/</c> or <c>Tools/</c> (excluding <c>ThirdParty/</c>) may cite a <c>*.md</c> filename this
    /// repo does not track (matched by basename, not full path). Checks only that a citation RESOLVES,
    /// never that it is accurate — see this file's header for the bare-pointer scope fence and what a red
    /// here means.
    /// </summary>
    [TestFixture]
    public class MdCitationFenceTests
    {
        private static readonly Regex CommentMarker = new Regex(@"^\s*(///|//|\*/?|#)\s?");
        private static readonly Regex MdToken = new Regex(@"[A-Za-z0-9._/~-]*[A-Za-z0-9_~-]\.md\b");
        private static readonly string[] SourceExtensions = { ".cs", ".hlsl", ".shader", ".sh", ".py" };

        [Test]
        public void NoCommittedFile_CitesAnUntrackedMdPath()
        {
            string repoRoot = ShaderPropertyParser.RepoRoot;

            // Citations resolve only against COMMITTED documents (an untracked .md exists on one machine),
            // but the scan also reads untracked sources, to see a phantom in the commit that adds it.
            var trackedMdBasenames = new HashSet<string>(
                Git(repoRoot, "ls-files").Where(p => p.EndsWith(".md", StringComparison.Ordinal))
                    .Select(Path.GetFileName),
                StringComparer.Ordinal);

            List<string> sourceFiles = GitSourceFiles(repoRoot).Where(p =>
                (p.StartsWith("Assets/", StringComparison.Ordinal) ||
                 p.StartsWith("Tools/", StringComparison.Ordinal)) &&
                SourceExtensions.Any(ext => p.EndsWith(ext, StringComparison.Ordinal)) &&
                !p.Contains("/ThirdParty/")).ToList();

            // Non-vacuity: a broken git call or an empty checkout would make the offender scan below
            // pass trivially.
            Assert.That(sourceFiles.Count, Is.GreaterThan(500),
                $"precondition: only found {sourceFiles.Count} source files under Assets/Tools — the git " +
                "ls-files call or the path filter is broken.");
            Assert.That(trackedMdBasenames.Count, Is.GreaterThan(10),
                $"precondition: only found {trackedMdBasenames.Count} tracked .md files — the git ls-files " +
                "call is broken, which would make every citation look like a phantom.");

            var offenders = new List<string>();
            foreach (string relPath in sourceFiles)
            {
                string[] lines = File.ReadAllLines(Path.Combine(repoRoot, relPath));
                int i = 0;
                while (i < lines.Length)
                {
                    if (!CommentMarker.IsMatch(lines[i])) { i++; continue; }
                    int blockStartLine = i + 1;
                    var block = new StringBuilder();
                    while (i < lines.Length && CommentMarker.IsMatch(lines[i]))
                    {
                        string seg = CommentMarker.Replace(lines[i], "").Trim();
                        if (block.Length > 0)
                        {
                            char last = block[block.Length - 1];
                            bool blockEndsJoin = last == '/' || last == '-';
                            bool segStartsJoin = seg.Length > 0 && (seg[0] == '/' || seg[0] == '-');
                            if (!blockEndsJoin && !segStartsJoin) block.Append(' ');
                        }
                        block.Append(seg);
                        i++;
                    }

                    // Exact basename match, NOT a suffix check, so a wrapped fragment cannot pass as a suffix of a
                    // real name. Non-obvious why: the scan reads THIS file, so no comment here names a fake file.
                    foreach (Match m in MdToken.Matches(block.ToString()))
                    {
                        string basename = Path.GetFileName(m.Value);
                        if (!trackedMdBasenames.Contains(basename))
                            offenders.Add($"{relPath}:{blockStartLine}: {m.Value}");
                    }
                }
            }

            Assert.That(offenders, Is.Empty,
                "Tripwire (resolvability only, not accuracy): these comments cite a .md filename this repo " +
                "does not track. Either the document moved/was renamed — update the citing comment to its " +
                "new path — or it never belonged here: state the fact inline and delete the pointer, " +
                "because the document may live only in a private sibling repo, or nowhere at all:\n" +
                string.Join("\n", offenders));
        }

        /// <summary>
        /// Tracked files plus untracked-but-not-ignored ones. The second half is load-bearing:
        /// <c>git ls-files</c> alone lists only what is committed, so a brand-new file is invisible to
        /// this scan until AFTER it lands — meaning the fence could never catch a phantom citation in
        /// the same commit that introduces it. This test was itself merged with exactly that defect.
        /// </summary>
        private static List<string> GitSourceFiles(string repoRoot)
        {
            var files = Git(repoRoot, "ls-files");
            files.AddRange(Git(repoRoot, "ls-files --others --exclude-standard"));
            // `ls-files` reports the INDEX, which lists a path deleted but not staged; reading it would throw,
            // and a missing file carries no citation, so it is dropped.
            return files.Where(p => File.Exists(Path.Combine(repoRoot, p))).ToList();
        }

        private static List<string> Git(string repoRoot, string args)
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using Process proc = Process.Start(psi);
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            Assert.That(proc.ExitCode, Is.EqualTo(0), $"git {args} failed — is this a git checkout?");
            return output.Split('\n').Where(l => l.Length > 0).ToList();
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // BakeIdentityShapeStructureTests — the recorded shape of PreparedKey and TileBufferClip
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A speed bump, not a ban. Pins the recorded shape of the bake-identity types. A
    /// <see cref="PreparedKey"/> is (Style, Tile, LayerId, Revision). <c>TileBufferClip.Equals</c> compares its
    /// state members by hand, so a new member without a term there would stop the bake-revision bump for it.
    /// A deliberate change updates this tooth together with that decision. Asserts the SHAPE, not a call count.
    /// </summary>
    [TestFixture]
    public class BakeIdentityShapeStructureTests
    {
        [Test]
        public void BakeIdentityTypes_HaveTheRecordedShape()
        {
            // Public|NonPublic — a non-public field is a legal way to add a discriminator.
            int keyFields = typeof(PreparedKey)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Length;
            Assert.AreEqual(4, keyFields,
                "PreparedKey is (Style, Tile, LayerId, Revision). A new field needs a term in Equals and " +
                "GetHashCode, and an update to this tooth.");

            // Fields, not properties: the two auto-properties are two backing fields, and a new property or a
            // non-public field of any kind adds one. The static Disabled and the const are not state.
            int clipMembers = typeof(MapRenderer.Core.Tiles.TileBufferClip)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Length;
            Assert.AreEqual(2, clipMembers,
                "TileBufferClip compares IsEnabled and KeepAtReferenceExtent in Equals and GetHashCode. A new " +
                "state member needs a term in both, or TileManager stops starting a new bake revision for it.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // RenderLayerRegistryStructureTests — RenderLayerFactory is the sole StyleLayer-subtype dispatch point
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>RenderLayerFactory</c> is the ONE registry mapping a <see cref="MapRenderer.Unity.Style.StyleLayer"/>
    /// subtype to its render layer. A grep guard: neither <c>MapView</c> nor <c>SymbolSubsystem</c> may
    /// re-dispatch on a <c>StyleLayer</c> subtype; they read the built <c>RenderLayerSet</c> instead.
    /// </summary>
    [TestFixture]
    public class RenderLayerRegistryStructureTests
    {
        // Matches a StyleLayer-subtype type-check ("is Fill.StyleLayer", "is not Line.StyleLayer"); only the
        // code form, so prose about the constraint is not flagged.
        private static readonly Regex Offender =
            new Regex(@"is\s+(\w+\.)*\s*(Fill|Line|Symbol)\.StyleLayer", RegexOptions.Compiled);

        [Test]
        public void MapView_NeverTypeSwitchesOnAStyleLayerSubtype()
        {
            string mapDir = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Map");
            string[] files = Directory.GetFiles(mapDir, "MapView*.cs");
            var names = new List<string>(Array.ConvertAll(files, Path.GetFileName));
            Assert.Contains("MapView.Style.cs", names, "the style partial must be in the scanned set, or its code is unchecked.");
            Assert.Contains("MapViewSourceSpecs.cs", names, "the source-spec file must be in the scanned set, or its code is unchecked.");
            foreach (string file in files) AssertNoOffendingPattern(file);
        }

        [Test]
        public void SymbolSubsystem_NeverTypeSwitchesOnAStyleLayerSubtype()
        {
            AssertNoOffendingPattern(Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Text", "SymbolSubsystem.cs"));
        }

        private static void AssertNoOffendingPattern(string file)
        {
            FileAssert.Exists(file);
            string text = File.ReadAllText(file);
            var offenders = Offender.Matches(text);

            Assert.AreEqual(0, offenders.Count,
                $"'{Path.GetFileName(file)}' must derive its layer-kind decisions from the built " +
                "RenderLayerSet, not re-dispatch on a Fill/Line/Symbol.StyleLayer subtype " +
                "(RenderLayerFactory is the sole registry). Offending text: " +
                (offenders.Count > 0 ? offenders[0].Value : string.Empty));
        }

        /// <summary><c>BackgroundRenderLayer</c> has no <c>SetVisible</c> gate: background is a per-covered-tile
        /// layer projected like fill/line, so the globe renders it curved instead of hiding it. The
        /// scan covers only <c>BackgroundRenderLayer.cs</c>; a <c>MapView</c> call would not compile without
        /// the method.</summary>
        [Test]
        public void BackgroundRenderLayer_HasNoMercatorVisibilityGate()
        {
            string file = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Layers", "BackgroundRenderLayer.cs");
            FileAssert.Exists(file);
            string text = File.ReadAllText(file);
            Assert.IsFalse(text.Contains("SetVisible("),
                "BackgroundRenderLayer.cs must contain ZERO 'SetVisible(' — the Mercator-only background " +
                "gate method is deleted; background is a backend-owned per-tile layer now.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SkyShaderStructureTests — Map/Sky ships in every build; the sky writer never touches scene lighting
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SkyShaderStructureTests
    {
        private static string SkyShaderPath => Path.Combine(ShaderPropertyParser.ShadersDir, "Sky", "Sky.shader");

        [Test]
        public void SkyShader_IsInAlwaysIncludedShaders()
        {
            // Non-obvious why: SkyGradient reaches the shader only by Shader.Find, and no asset references
            // it, so a player build strips it unless GraphicsSettings lists it.
            Match guid = Regex.Match(File.ReadAllText(SkyShaderPath + ".meta"), @"^guid:\s*([0-9a-f]{32})",
                                     RegexOptions.Multiline);
            Assert.That(guid.Success, "Sky.shader.meta has no guid.");

            string settings = File.ReadAllText(
                Path.Combine(ShaderPropertyParser.RepoRoot, "ProjectSettings", "GraphicsSettings.asset"));
            Match included = Regex.Match(settings, @"m_AlwaysIncludedShaders:\s*\n((?:\s+- .*\n)+)");
            Assert.That(included.Success, "GraphicsSettings.asset has no m_AlwaysIncludedShaders list.");
            Assert.That(included.Groups[1].Value, Does.Contain("guid: " + guid.Groups[1].Value),
                "Map/Sky is not in Always Included Shaders; a player build would render no sky.");
        }

        [Test]
        public void SkyGradient_NeverWritesSceneLighting()
        {
            // RenderSettings.skybox feeds the ambient convolution and the default reflection. The sky must
            // ride a camera Skybox component so lighting stays as MapHost set it.
            string path = Path.Combine(ShaderPropertyParser.RenderingDir, "Map", "SkyGradient.cs");
            var code = File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//"));
            string joined = string.Join("\n", code);

            Assert.That(joined, Does.Not.Contain("RenderSettings"), "SkyGradient.cs must not use RenderSettings.");
            Assert.That(joined, Does.Not.Contain("DynamicGI"), "SkyGradient.cs must not use DynamicGI.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // HazeFogStructureTests — the build keeps the haze's fog variants; every map forward pass takes fog
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class HazeFogStructureTests
    {
        [Test]
        public void FogStripping_KeepsTheHazeFogMode()
        {
            // Non-obvious why: automatic stripping keeps only the fog modes the build's scenes use, and no scene
            // enables fog, so a player build would strip the variants DistanceHaze switches on at runtime.
            string settings = File.ReadAllText(
                Path.Combine(ShaderPropertyParser.RepoRoot, "ProjectSettings", "GraphicsSettings.asset"));
            FogMode hazeMode = MapRenderer.Unity.Rendering.Map.DistanceHaze.HazeFogMode;
            string keepKey = hazeMode switch
            {
                FogMode.Linear             => "m_FogKeepLinear",
                FogMode.Exponential        => "m_FogKeepExp",
                FogMode.ExponentialSquared => "m_FogKeepExp2",
                _                          => throw new ArgumentOutOfRangeException(),
            };

            Assert.That(settings, Does.Match(@"\n  m_FogStripping: 1\r?\n"),
                "Fog stripping must be Custom (1); Automatic (0) strips the runtime haze variants.");
            Assert.That(settings, Does.Match($@"\n  {keepKey}: 1\r?\n"),
                $"{keepKey} must be 1: DistanceHaze uses {hazeMode} fog.");
        }

        [Test]
        public void EveryMapForwardPass_IncludesUrpFog()
        {
            var offenders = Directory.EnumerateFiles(
                    Path.Combine(ShaderPropertyParser.ShadersDir, "Map"), "*.shader", SearchOption.AllDirectories)
                .Where(path => File.ReadAllText(path).Contains("\"UniversalForward\""))
                .Where(path => !File.ReadAllText(path).Contains("ShaderLibrary/Fog.hlsl"))
                .Select(path => path.Replace(ShaderPropertyParser.RepoRoot, string.Empty))
                .ToList();

            Assert.That(offenders, Is.Empty,
                "These map shaders have a forward pass without URP Fog.hlsl, so the haze skips them:\n" +
                string.Join("\n", offenders));
        }
    }
}
