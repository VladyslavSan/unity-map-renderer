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
//   MdCitationFenceTests                   — every *.md citation in source resolves to a tracked file.
//   PreparedKeyShapeStructureTests         — PreparedKey's recorded field-count shape.
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

        // ── Exact count guards (assert exact, never >) ───────────────────────────────────────

        /// <summary>
        /// Struct must have exactly 31 material-prop fields (excludes unity_* transforms).
        /// Breakdown: 21 non-line (14 common Lit+Opacity + 7 fill-only) + 10 line-only = 31 (was 12
        /// line-only before _MetersPerPixel and _AaEdgeWidth were retired).
        /// A vacuous regex (matches nothing) fails this immediately.
        /// </summary>
        [Test]
        public void StructMaterialFieldCount_IsExactly31()
        {
            var fields = ParseStructMaterialFields(out _);
            Assert.That(fields.Count, Is.EqualTo(31),
                $"MapInstanceData must have exactly 31 material-prop fields " +
                $"(14 common Lit+Opacity + 7 fill-only + 10 line-only). " +
                $"Found {fields.Count}: {string.Join(", ", fields.Keys)}");
        }

        /// <summary>Line_LitInput.hlsl DOTS block must have exactly 24 props.</summary>
        [Test]
        public void LineDotsPropCount_IsExactly24()
        {
            var props = ParseDotsProps(ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl"));
            Assert.That(props.Count, Is.EqualTo(24),
                $"Line_LitInput.hlsl DOTS block must have exactly 24 UNITY_DOTS_INSTANCED_PROP entries " +
                $"(13 common Lit + _Opacity + 10 line-specific). Found {props.Count}: {string.Join(", ", props.Keys)}");
        }

        /// <summary>Fill_LitInput.hlsl DOTS block must have exactly 21 props.</summary>
        [Test]
        public void FillDotsPropCount_IsExactly21()
        {
            var props = ParseDotsProps(ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));
            Assert.That(props.Count, Is.EqualTo(21),
                $"Fill_LitInput.hlsl DOTS block must have exactly 21 UNITY_DOTS_INSTANCED_PROP entries " +
                $"(13 common Lit + _Opacity + 7 fill-specific). Found {props.Count}: {string.Join(", ", props.Keys)}");
        }

        // ── Forward parity: each DOTS prop → struct field of matching float-count ─────────────

        /// <summary>
        /// Every Line DOTS prop must have a same-name struct field with matching float-count.
        /// Removing _Width from MapInstanceData fails here (the original bug direction).
        /// </summary>
        [Test]
        public void Forward_LineDots_EachPropHasMatchingStructField()
        {
            var lineProps = ParseDotsProps(ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl"));
            var structFields = ParseStructMaterialFields(out _);

            var failures = new List<string>();
            foreach (var kv in lineProps)
            {
                string name       = kv.Key;
                int    dotsFloats = kv.Value;
                if (!structFields.TryGetValue(name, out int structFloats))
                    failures.Add($"'{name}' is in Line DOTS block but has no matching field in MapInstanceData.");
                else if (structFloats != dotsFloats)
                    failures.Add($"'{name}' float-count mismatch: Line DOTS={dotsFloats}, struct field={structFloats}.");
            }

            Assert.That(failures, Is.Empty,
                "Line DOTS → struct forward parity failed:\n" + string.Join("\n", failures));
        }

        /// <summary>
        /// Every Fill DOTS prop must have a same-name struct field with matching float-count.
        /// </summary>
        [Test]
        public void Forward_FillDots_EachPropHasMatchingStructField()
        {
            var fillProps = ParseDotsProps(ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));
            var structFields = ParseStructMaterialFields(out _);

            var failures = new List<string>();
            foreach (var kv in fillProps)
            {
                string name       = kv.Key;
                int    dotsFloats = kv.Value;
                if (!structFields.TryGetValue(name, out int structFloats))
                    failures.Add($"'{name}' is in Fill DOTS block but has no matching field in MapInstanceData.");
                else if (structFloats != dotsFloats)
                    failures.Add($"'{name}' float-count mismatch: Fill DOTS={dotsFloats}, struct field={structFloats}.");
            }

            Assert.That(failures, Is.Empty,
                "Fill DOTS → struct forward parity failed:\n" + string.Join("\n", failures));
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
// CBUFFER and DOTS block, and each .shader Properties{} superset agree. Exact counts fail a regex that matches nothing.
//

    [TestFixture]
    public class MaterialPropertyRegistryParityTests
    {
        // ── Paths ────────────────────────────────────────────────────────────────────────────────

        private static string RenderingDir        => ShaderPropertyParser.RenderingDir;
        private static string ShaderPropertiesDir => Path.Combine(RenderingDir, "ShaderProperties");
        private static string RenderingLineDir     => Path.Combine(ShaderPropertiesDir, "Line");
        private static string RenderingFillDir     => Path.Combine(ShaderPropertiesDir, "Fill");

        // ── Exact count guards ───────────────────────────────────────────────────────────────────

        [Test]
        public void LineCbufferCount_IsExactly24()
        {
            var set = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl"));
            Assert.That(set.Count, Is.EqualTo(24),
                $"Line_LitInput.hlsl CBUFFER (after companion strip) must have exactly 24 members. " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void FillCbufferCount_IsExactly21()
        {
            var set = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));
            Assert.That(set.Count, Is.EqualTo(21),
                $"Fill_LitInput.hlsl CBUFFER (after companion strip) must have exactly 21 members. " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void SharedCbufferRegionCount_IsExactly14()
        {
            var set = ParseSharedCbufferRegion();
            Assert.That(set.Count, Is.EqualTo(14),
                $"PropertyNames.cs CBUFFER region must have exactly 14 const string values. " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void LinePropertyNamesCount_IsExactly12()
        {
            var set = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingLineDir, "PropertyNames.cs"));
            Assert.That(set.Count, Is.EqualTo(12),
                $"ShaderProperties/Line/PropertyNames.cs must have exactly 12 const string values " +
                $"(10 CBUFFER members + 2 editor-only keyword drivers). " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void LineCbufferRegionCount_IsExactly10()
        {
            // Guards the region split: SharedUnionLineNames_EqualsLineCbuffer reads only the CBUFFER region,
            // so a CBUFFER property moved into the editor-only region would escape parity without this count.
            var set = ParseLineCbufferRegion();
            Assert.That(set.Count, Is.EqualTo(10),
                $"Line/PropertyNames.cs CBUFFER region must have exactly 10 const string values. " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void FillPropertyNamesCount_IsExactly7()
        {
            var set = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingFillDir, "PropertyNames.cs"));
            Assert.That(set.Count, Is.EqualTo(7),
                $"ShaderProperties/Fill/PropertyNames.cs must have exactly 7 const string values. " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
        }

        [Test]
        public void PropertyNamesTotalCount_IsExactly41()
        {
            var set = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(ShaderPropertiesDir, "PropertyNames.cs"));
            Assert.That(set.Count, Is.EqualTo(41),
                $"ShaderProperties/PropertyNames.cs must have exactly 41 const string values in total " +
                $"(14 CBUFFER + 9 render-state + 18 textures/surface). " +
                $"Found {set.Count}: {string.Join(", ", set.OrderBy(s => s))}");
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

        [Test]
        public void LineDots_EqualsLineCbuffer()
        {
            // Line DOTS block keys must equal the Line CBUFFER set (both 24, every CBUFFER prop is instanced).
            var lineDotsKeys = new HashSet<string>(
                ShaderPropertyParser.ParseDotsProps(ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl")).Keys);
            var lineCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Line_LitInput.hlsl"));

            var onlyInDots = new HashSet<string>(lineDotsKeys);
            onlyInDots.ExceptWith(lineCbuffer);
            var onlyInCbuffer = new HashSet<string>(lineCbuffer);
            onlyInCbuffer.ExceptWith(lineDotsKeys);

            Assert.That(onlyInDots, Is.Empty,
                $"Props in Line DOTS block but not in Line CBUFFER: {string.Join(", ", onlyInDots)}");
            Assert.That(onlyInCbuffer, Is.Empty,
                $"Props in Line CBUFFER but not in Line DOTS block: {string.Join(", ", onlyInCbuffer)}");
        }

        [Test]
        public void FillDots_EqualsFillCbuffer()
        {
            var fillDotsKeys = new HashSet<string>(
                ShaderPropertyParser.ParseDotsProps(ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl")).Keys);
            var fillCbuffer = ShaderPropertyParser.ParseCbufferMembers(
                ShaderPropertyParser.MapShaderPath("Fill_LitInput.hlsl"));

            var onlyInDots = new HashSet<string>(fillDotsKeys);
            onlyInDots.ExceptWith(fillCbuffer);
            var onlyInCbuffer = new HashSet<string>(fillCbuffer);
            onlyInCbuffer.ExceptWith(fillDotsKeys);

            Assert.That(onlyInDots, Is.Empty,
                $"Props in Fill DOTS block but not in Fill CBUFFER: {string.Join(", ", onlyInDots)}");
            Assert.That(onlyInCbuffer, Is.Empty,
                $"Props in Fill CBUFFER but not in Fill DOTS block: {string.Join(", ", onlyInCbuffer)}");
        }

        // ── Shader Properties{} superset: every registry name appears in the shader ─────────────

        [Test]
        public void LineShaderPropertiesBlock_ContainsAllRegistryNames()
        {
            var allShared = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(ShaderPropertiesDir, "PropertyNames.cs"));
            var lineNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingLineDir, "PropertyNames.cs"));
            var lineShaderProps = ShaderPropertyParser.ParseShaderPropertiesBlock(
                ShaderPropertyParser.MapShaderPath("Line.shader"));

            var allRegistry = new HashSet<string>(allShared);
            allRegistry.UnionWith(lineNames);

            var missing = new List<string>();
            foreach (string name in allRegistry)
                if (!lineShaderProps.Contains(name))
                    missing.Add(name);

            Assert.That(missing, Is.Empty,
                $"These registry names are missing from Line.shader Properties{{}}: " +
                string.Join(", ", missing));
        }

        [Test]
        public void FillShaderPropertiesBlock_ContainsAllRegistryNames()
        {
            var allShared = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(ShaderPropertiesDir, "PropertyNames.cs"));
            var fillNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingFillDir, "PropertyNames.cs"));
            var fillShaderProps = ShaderPropertyParser.ParseShaderPropertiesBlock(
                ShaderPropertyParser.MapShaderPath("Fill.shader"));

            var allRegistry = new HashSet<string>(allShared);
            allRegistry.UnionWith(fillNames);

            var missing = new List<string>();
            foreach (string name in allRegistry)
                if (!fillShaderProps.Contains(name))
                    missing.Add(name);

            Assert.That(missing, Is.Empty,
                $"These registry names are missing from Fill.shader Properties{{}}: " +
                string.Join(", ", missing));
        }

        // ── No cross-contamination: layer-specific names not in shared CBUFFER region ────────────

        [Test]
        public void LinePropertyNames_HasNoDuplicatesInSharedCbuffer()
        {
            // _BaseColor must be only in shared PropertyNames, not re-declared in Line/PropertyNames.
            var shared = ParseSharedCbufferRegion();
            var lineNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingLineDir, "PropertyNames.cs"));

            var overlap = new HashSet<string>(lineNames);
            overlap.IntersectWith(shared);

            Assert.That(overlap, Is.Empty,
                $"ShaderProperties/Line/PropertyNames.cs re-declares names that are already in the shared CBUFFER region " +
                $"(duplication violates the single-source-of-truth rule): {string.Join(", ", overlap)}");
        }

        [Test]
        public void FillPropertyNames_HasNoDuplicatesInSharedCbuffer()
        {
            var shared = ParseSharedCbufferRegion();
            var fillNames = ShaderPropertyParser.ParseAllConstStringValues(
                Path.Combine(RenderingFillDir, "PropertyNames.cs"));

            var overlap = new HashSet<string>(fillNames);
            overlap.IntersectWith(shared);

            Assert.That(overlap, Is.Empty,
                $"ShaderProperties/Fill/PropertyNames.cs re-declares names that are already in the shared CBUFFER region: " +
                string.Join(", ", overlap));
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
        /// file. <see cref="LineCbufferRegionCount_IsExactly10"/> pins the region's size.
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
        public void ShaderPropertiesCs_DoesNotExist()
        {
            // The old flat class must be gone.
            string oldFile = Path.Combine(RenderingDir, "ShaderProperties.cs");
            FileAssert.DoesNotExist(oldFile, $"ShaderProperties.cs still exists at {oldFile}. It must be deleted.");
        }

        [Test]
        public void ShaderPropertiesCompatCs_DoesNotExist()
        {
            // The transitional compat shim must be gone.
            string compatFile = Path.Combine(RenderingDir, "ShaderPropertiesCompat.cs");
            FileAssert.DoesNotExist(compatFile, $"ShaderPropertiesCompat.cs still exists at {compatFile}. Delete it after BaseShaderGUI is migrated.");
        }

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

        [Test]
        public void NoShaderPropertiesToken_InUnityAssembly()
        {
            // ShaderPropertiesToken was the sentinel type inside the compat shim. It must not exist.
            var badPattern = new Regex(@"\bShaderPropertiesToken\b");

            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(UnityDir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (badPattern.IsMatch(text))
                    offenders.Add(file.Replace(RepoRoot, string.Empty));
            }

            Assert.That(offenders, Is.Empty,
                $"These files reference ShaderPropertiesToken. It should no longer exist:\n" +
                string.Join("\n", offenders));
        }

        // ── 3. No raw-string Material API guard ──────────────────────────────────────────────────

        [Test]
        public void NoRawStringMaterialApiCalls_InUnityAssembly()
        {
            // Flag any Set/Get(Float|Color|Vector|Int|Texture)(" or HasProperty(" call, including the
            // Shader.*Global* forms (a typo'd global reads back 0 in silence). No file is excluded.
            var badPattern = new Regex(
                @"\.(Set|Get)(Global)?(Float|Color|Vector|Int|Texture)\s*\(\s*""|\.HasProperty\s*\(\s*""");

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
        public void ShaderFiles_OldCommonFilesAreGone()
        {
            // The old Common/ framework (used by Fill only) lives in Map/Fill/.
            // Confirm the old paths no longer exist.
            (string dir, string name)[] gone = new[]
            {
                (CommonDir, "MapLitInput.hlsl"),
                (CommonDir, "MapLitCore.hlsl"),
                (CommonDir, "MapLitForwardPass.hlsl"),
                (CommonDir, "MapLitGBufferPass.hlsl"),
                (CommonDir, "MapShadowCasterPass.hlsl"),
                (CommonDir, "MapDepthOnlyPass.hlsl"),
                (CommonDir, "MapDepthNormalsPass.hlsl"),
            };

            foreach (var (dir, name) in gone)
            {
                string path = Path.Combine(dir, name);
                FileAssert.DoesNotExist(path, $"Old shader file still present (should have been moved): {Path.GetFileName(dir)}/{name}");
            }
        }

        [Test]
        public void ShaderFiles_OldLineFilesAreGone()
        {
            // Line files are Line_LitInput.hlsl / Line_LitForwardPass.hlsl.
            FileAssert.DoesNotExist(Path.Combine(MapLineDir, "MapLineInput.hlsl"), "Old MapLineInput.hlsl still present (should be Line_LitInput.hlsl).");
            FileAssert.DoesNotExist(Path.Combine(MapLineDir, "MapLineForwardPass.hlsl"), "Old MapLineForwardPass.hlsl still present (should be Line_LitForwardPass.hlsl).");
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

        [Test]
        public void FillLitInput_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("Fill_LitInput.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_LitInput.hlsl must carry a UCL attribution header (license requirement).");
            Assert.That(text, Does.Contain("Unity Technologies"),
                "Fill_LitInput.hlsl must attribute © Unity Technologies ApS.");
        }

        [Test]
        public void FillLitForwardPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("Fill_LitForwardPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_LitForwardPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillLitGBufferPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("Fill_LitGBufferPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_LitGBufferPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillShadowCasterPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("Fill_ShadowCasterPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_ShadowCasterPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillDepthOnlyPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("Fill_DepthOnlyPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_DepthOnlyPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillDepthNormalsPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("Fill_DepthNormalsPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_DepthNormalsPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void LineLitInput_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("Line_LitInput.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Line_LitInput.hlsl must carry a UCL attribution header.");
            Assert.That(text, Does.Contain("Unity Technologies"),
                "Line_LitInput.hlsl must attribute © Unity Technologies ApS.");
        }

        [Test]
        public void LineLitForwardPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("Line_LitForwardPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Line_LitForwardPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillExtrusionLitInput_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("FillExtrusion_LitInput.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "FillExtrusion_LitInput.hlsl must carry a UCL attribution header.");
            Assert.That(text, Does.Contain("Unity Technologies"),
                "FillExtrusion_LitInput.hlsl must attribute © Unity Technologies ApS.");
        }

        [Test]
        public void FillExtrusionLitForwardPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("FillExtrusion_LitForwardPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "FillExtrusion_LitForwardPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillExtrusionLitGBufferPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("FillExtrusion_LitGBufferPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "FillExtrusion_LitGBufferPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillExtrusionShadowCasterPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("FillExtrusion_ShadowCasterPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "FillExtrusion_ShadowCasterPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillExtrusionDepthOnlyPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("FillExtrusion_DepthOnlyPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "FillExtrusion_DepthOnlyPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillExtrusionDepthNormalsPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("FillExtrusion_DepthNormalsPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "FillExtrusion_DepthNormalsPass.hlsl must carry a UCL attribution header.");
        }

        // ── InitializeStandardLitSurfaceData usage ────────────────────────────
        // The decisive structural gate: SurfaceData must NOT be hand-assembled.

        [Test]
        public void FillLitInput_DefinesInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile("Fill_LitInput.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData"),
                "Fill_LitInput.hlsl must define InitializeStandardLitSurfaceData — " +
                "this is the function that populates SurfaceData correctly (tooth #1 gate).");
        }

        [Test]
        public void FillLitForwardPass_CallsInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile("Fill_LitForwardPass.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData("),
                "Fill_LitForwardPass.hlsl fragment must call InitializeStandardLitSurfaceData — " +
                "NEVER hand-assembled SurfaceData field-by-field.");
        }

        [Test]
        public void FillLitGBufferPass_CallsInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile("Fill_LitGBufferPass.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData("),
                "Fill_LitGBufferPass.hlsl fragment must call InitializeStandardLitSurfaceData — " +
                "NEVER hand-assembled SurfaceData field-by-field.");
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
        public void FillExtrusionVerticalGradient_NotRendered_NoWallDarkeningFold()
        {
            // fill-extrusion-vertical-gradient is parsed but NOT rendered: real ambient + shadows/SSAO replace
            // the fake-AO wall darkening. Structural, because headless cannot render fragments.
            string vmod = ReadShaderFile("FillExtrusion_VertexModify.hlsl");
            Assert.That(vmod, Does.Not.Contain("FillExtrusionVerticalGradientFactor"),
                "FillExtrusion_VertexModify.hlsl must NOT define the abandoned vertical-gradient factor helper.");

            foreach (string pass in new[] { "FillExtrusion_LitForwardPass.hlsl", "FillExtrusion_LitGBufferPass.hlsl" })
            {
                string text = ReadShaderFile(pass);
                // The base per-vertex colour must survive; only the darkening fold on top of it is removed.
                Assert.That(text, Does.Contain("output.vColor = input.color;"),
                    $"{pass}: the per-vertex vColor assignment must remain.");
                Assert.That(text, Does.Not.Contain("FillExtrusionVerticalGradientFactor"),
                    $"{pass}: the vertical-gradient darkening fold must NOT be applied to vColor.");
            }
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
        }

        [Test]
        public void FillLitInput_CBUFFERContainsFullURPLitProps()
        {
            string text = ReadShaderFile("Fill_LitInput.hlsl");
            // Spot-check a selection of URP Lit's CBUFFER members.
            string[] required = new[] {
                "_BaseMap_ST", "_BaseColor", "_SpecColor", "_EmissionColor",
                "_Cutoff", "_Smoothness", "_Metallic", "_BumpScale", "_OcclusionStrength",
                "_DetailAlbedoMapScale", "_DetailNormalMapScale",
                // Map addition (_MapColor is collapsed into _BaseColor):
                "_Opacity"
            };
            foreach (var prop in required)
                Assert.That(text, Does.Contain(prop),
                    $"Fill_LitInput.hlsl CBUFFER must contain '{prop}' (full URP Lit shape + fill paint additions).");
        }

        [Test]
        public void CommonDir_DoesNotContainMapLitCore()
        {
            // MapLitCore.hlsl is dissolved (its CBUFFER lives in Fill_LitInput.hlsl;
            // the MapVertexModify forward declaration is no longer needed; MapEdgeAA was dead).
            string path = Path.Combine(CommonDir, "MapLitCore.hlsl");
            FileAssert.DoesNotExist(path, "Common/MapLitCore.hlsl must not exist (dissolved: CBUFFER moved to " +
                "Fill_LitInput.hlsl, forward-declaration hack removed, MapEdgeAA deleted as dead).");
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

        // ── MapEdgeAA deleted ─────────────────────────────────────────────────

        [Test]
        public void Shaders_NoMapEdgeAAReferences()
        {
            // MapEdgeAA was dead (zero call sites) and is deleted along with MapLitCore.hlsl.
            foreach (var file in Directory.EnumerateFiles(ShadersDir, "*.hlsl", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(ShadersDir, "*.shader", SearchOption.AllDirectories)))
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                Assert.That(text, Does.Not.Contain("MapEdgeAA"),
                    $"{Path.GetFileName(file)} references MapEdgeAA — it was deleted as a dead helper.");
            }
        }

        // ── Shader feature pragmas (required for tooth #1 normal map, tooth #3 specular) ──

        [Test]
        public void FillShader_ForwardLit_HasNormalMapPragma()
        {
            string text = ReadShaderFile("Fill.shader");
            Assert.That(text, Does.Contain("shader_feature_local _NORMALMAP"),
                "Fill.shader ForwardLit pass must have '#pragma shader_feature_local _NORMALMAP'. " +
                "Without it, binding a normal map compiles to nothing and tooth #1 cannot pass.");
        }

        [Test]
        public void FillShader_HasMetallicSpecGlossMapPragma()
        {
            string text = ReadShaderFile("Fill.shader");
            Assert.That(text, Does.Contain("shader_feature_local_fragment _METALLICSPECGLOSSMAP"),
                "Fill.shader must have '#pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP'. " +
                "Without it, binding a metallic map compiles to nothing and tooth #3 cannot pass.");
        }

        [Test]
        public void FillShader_GBuffer_HasNormalMapPragma()
        {
            string text = ReadShaderFile("Fill.shader");
            // GBuffer pass also needs _NORMALMAP for correct deferred normal-map support.
            int gBufferIdx = text.IndexOf("Name \"GBuffer\"", StringComparison.Ordinal);
            Assert.That(gBufferIdx, Is.GreaterThanOrEqualTo(0), "GBuffer pass not found in Fill.shader.");
            string afterGBuffer = text.Substring(gBufferIdx);
            Assert.That(afterGBuffer, Does.Contain("shader_feature_local _NORMALMAP"),
                "Fill.shader GBuffer pass must also have _NORMALMAP pragma.");
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
        /// teeth by existing. The count is asserted at each call site: a regex that stopped matching
        /// would otherwise turn every tooth below into a vacuous loop over nothing.
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
                for (int i = 1; i < blocks.Length; i++)   // [0] is everything before the first Pass
                {
                    Match include = Regex.Match(blocks[i], "#include\\s+\"(?:\\.\\./)?([A-Za-z0-9_]+Pass\\.hlsl)\"");
                    if (!include.Success) continue;
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
            Assert.That(passes.Count, Is.EqualTo(8),
                "Expected 8 fill pass blocks across Fill.shader + FillUnlit.shader; found " +
                $"{passes.Count} [{string.Join(", ", passes.Select(p => p.Shader + ":" + p.BodyFile))}]. " +
                "A block regex that drifted would make every assertion below vacuous.");

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
            Assert.That(passes.Count, Is.EqualTo(8), "precondition: all 8 fill pass blocks must be found.");
            Assert.That(passes.Count(p => p.WritesDepth), Is.EqualTo(6),
                "precondition: the 6 depth-writing pass blocks (ShadowCaster/GBuffer/DepthOnly/DepthNormals " +
                "across both entry points) must be detected by their hardcoded ZWrite On.");

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
            Assert.That(forward.Count, Is.EqualTo(2),
                $"Expected the 2 shading fill passes (Fill.shader ForwardLit + FillUnlit.shader Unlit); " +
                $"found {forward.Count}. Without this the loop below iterates nothing and passes vacuously, " +
                "which is the one failure a green result cannot distinguish itself from.");

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
        /// The fill's coverage is the line path's outer-edge ramp, read from both files. Limitation: the
        /// gradient must stay EUCLIDEAN, and <c>fwidth</c> (Manhattan) agrees with it on every axis-aligned
        /// fixture, so only this tooth sees the swap. The one licensed difference is <c>absSide</c> in the
        /// line versus <c>side</c> in the fill, whose band never goes negative.
        /// </summary>
        [Test]
        public void FillBandCoverage_IsVerbatimTheShippedLineCoverageExpression()
        {
            string line = File.ReadAllText(ShaderPropertyParser.MapShaderPath("Line_VertexExtrude.hlsl"));
            string fill = File.ReadAllText(ShaderPropertyParser.MapShaderPath("Fill_BandCoverage.hlsl"));

            const string gradient = "max(length(float2(ddx(side), ddy(side))), 1e-6)";
            const string coverage = "saturate((1.0 - absSide) / sideGrad)";

            Assert.That(line, Does.Contain(gradient),
                "Line_VertexExtrude.hlsl no longer computes the Euclidean side gradient this way; the fill " +
                "band is now reusing an expression the line path has stopped using.");
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

            // Positive control: the SAME scan over MapRenderer.Jobs finds Unity.Collections in the asmdef and
            // in at least one source file, so the matcher can see what it reports absent from Core.
            string jobsAsmdefPath = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Jobs", "MapRenderer.Jobs.asmdef");
            FileAssert.Exists(jobsAsmdefPath);
            List<string> jobsReferences = ReferencesOf(File.ReadAllText(jobsAsmdefPath), jobsAsmdefPath);
            CollectionAssert.Contains(jobsReferences, "Unity.Collections",
                "positive control: MapRenderer.Jobs.asmdef MUST reference Unity.Collections — if this fails, " +
                "the asmdef parser is broken and Core's clean result above means nothing");

            string jobsRoot = Path.Combine(Application.dataPath, "Code", "MapRenderer.Jobs");
            int jobsSourceHits = 0;
            foreach (string file in Directory.GetFiles(jobsRoot, "*.cs", SearchOption.AllDirectories))
            {
                string code = StripComments(File.ReadAllText(file));
                foreach (string form in CollectionsCodeForms)
                    if (code.Contains(form, StringComparison.Ordinal)) { jobsSourceHits++; break; }
            }
            Assert.Greater(jobsSourceHits, 0,
                "positive control: the SAME comment-stripped source scan MUST find Unity.Collections code " +
                "forms under MapRenderer.Jobs — otherwise the scan is blind and Core's zero is meaningless");

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
    // PreparedKeyShapeStructureTests — PreparedKey's recorded field-count shape
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A speed bump, not a ban. Pins the recorded 3-field shape of
    /// <see cref="PreparedKey"/> — <c>Style</c> already partitions the cache, so a second discriminator
    /// would change no outcome at a data-plane cost. A deliberate fourth field
    /// (<c>park/umr-113-bake-revision</c>'s <c>Revision</c> is the known candidate) updates this tooth
    /// together with that decision, not around it. Asserts the SHAPE (arity), not a call-site count.
    /// </summary>
    [TestFixture]
    public class PreparedKeyShapeStructureTests
    {
        [Test]
        public void PreparedKey_HasExactlyThreeFields()
        {
            // Public|NonPublic — a non-public fourth field is a legal way to implement the rejected fork,
            // and Public-only would not see it.
            int count = typeof(PreparedKey)
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Length;
            Assert.AreEqual(3, count,
                "PreparedKey must stay a 3-field key (Style, Tile, LayerId) — a recorded decision: " +
                "Style already partitions the cache, so a second discriminator would change no outcome at " +
                "a data-plane cost. Landing a fourth field on purpose (park/umr-113-bake-revision's " +
                "Revision is the known candidate)? Update this tooth together with that decision, not " +
                "instead of it.");
        }

        [Test]
        public void EveryPreparedKeyConstruction_TakesExactlyThreeArguments()
        {
            string codeDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Assets", "Code");
            int found = 0;
            foreach (string file in Directory.GetFiles(codeDir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                // Naive comma counting — every call site today is a simple 3-arg form (no nested parens, no
                // object initializer). An initializer-form call would silently defeat this arity check.
                foreach (Match m in Regex.Matches(text, "new " + nameof(PreparedKey) + @"\(([^)]*)\)"))
                {
                    found++;
                    int argCount = m.Groups[1].Value.Split(',').Length;
                    Assert.AreEqual(3, argCount,
                        $"{file}: '{m.Value}' does not take 3 arguments — PreparedKey's arity changed. " +
                        "If this is a deliberate fourth field (see PreparedKey_HasExactlyThreeFields for " +
                        "the recorded decision it revises), update this tooth's expected arity together " +
                        "with it, not around it.");
                }
            }
            // A required-token check like the loop above fails OPEN on zero matches — a factory method or
            // an object-initializer construction would retire it silently. Guard the pattern still fires.
            Assert.Greater(found, 0, $"no 'new {nameof(PreparedKey)}(' call sites found under {codeDir} — " +
                "the arity check above never ran. PreparedKey construction moved to a form this regex " +
                "cannot see (a factory method, an object initializer) — update the tooth to match it.");
        }
    }


    // ───────────────────────────────────────────────────────────────────────────────────
    // RenderLayerRegistryStructureTests — RenderLayerFactory is the sole StyleLayer-subtype dispatch point
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>RenderLayerFactory</c> is the ONE registry mapping a <see cref="MapRenderer.Core.Style.StyleLayer"/>
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
            AssertNoOffendingPattern(Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Map", "MapView.cs"));
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

        /// <summary><c>RenderLayerBuild.ViewGeometry</c> is REMOVED, not left
        /// dead. Compile-enforced (the enum member no longer exists, so any surviving reference is a compile
        /// error) — this grep is a redundant, documentation-grade guard over the production assembly.</summary>
        [Test]
        public void RenderLayerBuild_ViewGeometry_HasNoProductionReferences()
        {
            string root = Path.Combine(Application.dataPath, "Code", "MapRenderer.Unity");
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                Assert.IsFalse(text.Contains("RenderLayerBuild.ViewGeometry"),
                    $"'{file}' references the REMOVED RenderLayerBuild.ViewGeometry member (" +
                    "background's build kind collapsed to TileMesh).");
            }
        }

        /// <summary><c>BackgroundRenderLayer</c> has no <c>SetVisible</c> gate: background is a per-covered-tile
        /// TileMesh layer projected like fill/line, so the globe renders it curved instead of hiding it. The
        /// scan covers only <c>BackgroundRenderLayer.cs</c>; a <c>MapView</c> call would not compile without
        /// the method.</summary>
        [Test]
        public void BackgroundRenderLayer_HasNoMercatorVisibilityGate()
        {
            string file = Path.Combine(
                Application.dataPath, "Code", "MapRenderer.Unity", "Rendering", "Style", "BackgroundRenderLayer.cs");
            FileAssert.Exists(file);
            string text = File.ReadAllText(file);
            Assert.IsFalse(text.Contains("SetVisible("),
                "BackgroundRenderLayer.cs must contain ZERO 'SetVisible(' — the Mercator-only background " +
                "gate method is deleted; background is a backend-owned per-tile TileMesh layer now.");
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
