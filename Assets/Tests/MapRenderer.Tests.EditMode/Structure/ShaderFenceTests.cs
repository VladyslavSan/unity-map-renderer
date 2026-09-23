// Structure/ShaderFenceTests.cs — shader/material structural fences: DOTS-instancing struct<->shader
// parity, the ShaderProperties registry's four-way parity, the no-raw-string Material access guard,
// fill/line shader pass structure, the fill-band vertex-attribute parity, the Core-assembly engine-free
// boundary, the *.md citation fence, PreparedKey's field-count shape, and the render-layer factory's
// sole-dispatch-point guard. Unity EditMode only — reads source files under Application.dataPath. NOT
// registered in core-tests.csproj.
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using MapRenderer.Unity.Rendering.Backend.BRG;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
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

        // ParseDotsProps is in ShaderPropertyParser (shared with MaterialPropertyRegistryParityTests).
        // The private alias below keeps the call sites below unchanged so this test class remains
        // readable without requiring grep-and-replace.
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
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // MaterialPropertyRegistryParityTests — the ShaderProperties registry vs shader Properties/CBUFFER/DOTS
    // ───────────────────────────────────────────────────────────────────────────────────

// Material Property Registry Parity Tests — runs in the Unity EditMode test assembly
// (moved from Tools/core-tests, which used test-binary-relative path arithmetic that broke on the
// Assets/Code/ folder move). Paths are now resolved via ShaderPropertyParser's
// AssetDatabase-anchored helpers — move-proof.
//
// Validates the four-way set equality:
//   • sharedCBUFFER (from ShaderProperties/PropertyNames.cs CBUFFER region, 14)
//   • ShaderProperties/Line/PropertyNames.cs (12 line-only: 10 CBUFFER + 2 editor-only keyword drivers),
//     ShaderProperties/Fill/PropertyNames.cs (7 fill-only)
//   • Line_LitInput.hlsl CBUFFER (24 after stripping companions)
//   • Fill_LitInput.hlsl CBUFFER (21 after stripping companions)
//   (line counts dropped by 2 — _MetersPerPixel + _AaEdgeWidth retired)
//   • Line/Fill DOTS blocks (must equal their respective CBUFFER sets)
//   • Line.shader / Fill.shader Properties{} blocks (must be supersets of the registry)
//
// Exact counts: assert exact, never >. Fail loud if a regex silently matches nothing.
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
            // Guards the region split itself: SharedUnionLineNames_EqualsLineCbuffer now reads only the
            // CBUFFER region, so without an exact count here a real CBUFFER property could be dropped into
            // the editor-only region and silently escape the parity assertion.
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
            // The 14 shared CBUFFER names in PropertyNames.cs must be exactly the intersection of
            // the two shader CBUFFER sets. A mismatch means a prop was declared shared but is only
            // in one shader (or vice versa).
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

// No-Raw-String Material Access Guard — runs in the Unity EditMode test assembly (moved from
// Tools/core-tests, which used test-binary-relative path arithmetic that broke on the Assets/Code/
// folder move). Paths are now resolved via ShaderPropertyParser's AssetDatabase-anchored helpers.
//
// Structural guards that enforce:
//   1. Registry layout: six registry files exist under Rendering/ShaderProperties/{,Line,Fill}/.
//      ShaderPropertiesCompat.cs is gone. The old flat-directory files are gone.
//   2. Migration completeness: no file under MapRenderer.Unity uses the old prefixed class names
//      (LinePropertyId, FillPropertyId, LinePropertyNames, FillPropertyNames) or the compat-shim
//      class declaration (class ShaderProperties). ShaderPropertiesToken is gone.
//   3. No raw-string Material API: no file under Assets/Code/MapRenderer.Unity/**/*.cs uses
//      .Set/Get(Float|Color|Vector|Int|Texture)("...") or .HasProperty("...") string-literal forms.
//   4. No bare literals in PropertyId files: every PropertyId.cs member is defined as
//      Shader.PropertyToID(PropertyNames.X), never Shader.PropertyToID("_Foo").

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
            // The compat shim's class declaration must not exist anywhere. The token "ShaderProperties"
            // now only appears as a namespace segment (ShaderProperties.PropertyId.X etc.) — never as a
            // class declaration (class ShaderProperties).
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
            // Flag any line matching the raw-string Material API patterns:
            //   • .SetFloat("   .SetColor("   .SetVector("   .SetInt("   .SetTexture("
            //   • .GetFloat("   .GetColor("   .GetVector("   .GetInt("   .GetTexture("
            //   • .HasProperty("
            // …and their Shader.Set/GetGlobal* counterparts. The global forms went uncovered until
            // this stage simply because nothing wrote a shader global; the frame constant
            // _MapFrameMetersPerDevicePixel is the first, and line-pattern will add a second. A
            // global's name is exactly as easy to typo as a material property's, and a typo'd global reads
            // back 0 in silence.
            // Every call site uses a cached int id from ShaderProperties.PropertyId /
            // ShaderProperties.Line.PropertyId / ShaderProperties.Fill.PropertyId (or, for globals,
            // ShaderProperties.FrameGlobalIds), so there should be ZERO matching lines. No whole-file
            // exclusions (BaseShaderGUI is fully migrated).
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
            // Every member of every PropertyId.cs under ShaderProperties/ must be defined as
            // Shader.PropertyToID(PropertyNames.X) — never Shader.PropertyToID("_Foo").
            // A bare literal would bypass the single-source-of-truth guarantee (parity tests parse
            // PropertyNames, not PropertyId — so a hardcoded "_Foo" in PropertyId.cs passes every
            // existing parity test today but silently breaks the registry contract).
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
            // Checked by NAME (move-proof): MapShaderPath asserts each resolves to exactly one file somewhere
            // under Shaders/Map/, regardless of the Lit/Unlit folder split. Includes the unlit twins so the
            // new tree is fully pinned, not half-covered.
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
            // Per-layer shaders live under Shaders/Map/<Layer>/, not the flat Shaders/ root. Asserted
            // on the resolved path so the Lit/Unlit leaf (Map/Fill/Lit/Fill.shader) still counts as "under
            // the Fill layer" — the layer is pinned, the mode subfolder is not.
            Assert.That(ShaderPropertyParser.MapShaderPath("Fill.shader").Replace('\\', '/'),
                Does.Contain("/Map/Fill/"), "Fill.shader must live under Shaders/Map/Fill/.");
            Assert.That(ShaderPropertyParser.MapShaderPath("Line.shader").Replace('\\', '/'),
                Does.Contain("/Map/Line/"), "Line.shader must live under Shaders/Map/Line/.");
        }

        [Test]
        public void MapKindRoots_HoldOnlyGenuinelySharedIncludes()
        {
            // The Lit/Unlit split: the two .shader entry points and their mode-specific .hlsl
            // moved into Lit/ and Unlit/; the kind ROOT (Map/<Kind>/, top level only) must hold EXACTLY the
            // includes BOTH twins reuse — the vertex hook + the two depth passes, plus whatever else a kind
            // genuinely shares (Fill also shares its boundary-band coverage between the Lit and Unlit
            // forward passes). This is the structural contract the split created; without this tooth
            // someone could drop an unlit input back into the kind root and nothing would notice. Every
            // entry is named, so admitting a shared file is a deliberate edit here, not a widened wildcard.
            // RED-verify by moving any file up one level.
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
            // fill-extrusion-vertical-gradient is NOT rendered: the maintainer abandoned the
            // fake-AO wall-base darkening in favour of real ambient + shadows/SSAO (Core still PARSES the
            // property — this is a shader-side removal only). This is a regression tooth: the darkening must
            // stay gone and cannot creep back. Structural (headless cannot render fragments) — RED-verify by
            // re-adding the helper or the vColor fold.
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
            // The material editor must be WIRED, not just authored — the shader's CustomEditor is what
            // makes LitShaderGUI.ValidateMaterial run in the inspector (deriving _EMISSION/_NORMALMAP/… from
            // material properties). Without this line the GUI class exists but never drives keyword sync.
            // RED-verify by deleting the CustomEditor directive.
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
            // Each layer folder is self-contained (no reach into Common/). There are exactly two
            // KINDS of sanctioned cross-folder `../` reach, both validated STRUCTURALLY (resolve on disk),
            // not by a string allow-list:
            //   (1) the ONE sanctioned Map-root shared include — PixelsToWorld.hlsl (the shared
            //       px→world, reached from each kind's vertex file); and
            //   (2) the Lit/Unlit split — a .shader / mode-specific .hlsl in Map/<Kind>/{Lit,
            //       Unlit}/ reaches ONE level up to its OWN kind root for the three shared includes
            //       (<Kind>_VertexModify/VertexExtrude, _DepthOnlyPass, _DepthNormalsPass).
            // Every `../` include must resolve to a real file that is EITHER the Map-root shared include OR
            // still inside the SAME kind's tree. That blocks a reach into Common/, into a SIBLING layer, or
            // outside Map/ — the reaches this test exists to catch — and also catches a dangling include
            // that resolves to nothing.
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
        /// never runs and a clip there is dead work.
        ///
        /// <para>Exempt from the length limit for a limitation no rendered tooth can observe: 11 of the 18
        /// pass sites cannot rasterise in the shipped configuration (Forward+, queue >= 3000,
        /// CastShadows.Off on every kind but fill-extrusion), so a clip re-added to one of them stays
        /// invisible until a configuration flip makes the pass live. This fence is their sole observer. It
        /// matches the ARGUMENT (<c>_Opacity</c>), so the line-coverage and pattern-cutout
        /// <c>clip()</c>s are untouched, and it strips comments first, so a commented-out clip does not
        /// fail it.</para>
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
        /// world metres in the vertex shader. Reaching into <c>Common/</c> is forbidden, so before this include existed
        /// this was DUPLICATED per layer instead — sentinel-pinned copies verified byte-identical by the
        /// (now-retired) <c>SharedShaderBlocks_AreIdenticalAcrossLayers</c>. That test only ever caught
        /// drift between existing copies; it said nothing if a copy came back after the hoist. This is the
        /// positive replacement: the shared file exists and every carrier includes it, AND — the clause that
        /// actually catches re-duplication — no carrier locally re-defines the function.
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
            // Fill_VertexModify.hlsl's fill-translate guard is an `if` block, not an early `return`.
            // fill-translate is [0,0] on every shipped layer, so that branch is taken for essentially
            // every vertex drawn — an early return there would make "below the return" a place where code
            // looks correct, compiles, and never runs in production. The boundary band's displacement is
            // exactly the code that would go there. This forbids the shape from returning; a
            // forbidden-token check fails CLOSED, so it cannot pass by matching nothing.
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

        // Reads a shader source file by NAME (move-proof: resolved anywhere under Shaders/Map/ via
        // ShaderPropertyParser.MapShaderPath). Content tests below don't care which Lit/Unlit folder a file
        // sits in — only the dedicated layout tests (ShaderFiles_*, MapKindRoots_HoldOnlyTheSharedThree)
        // assert folder structure.
        private static string ReadShaderFile(string filename)
            => File.ReadAllText(ShaderPropertyParser.MapShaderPath(filename), Encoding.UTF8);
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillBandAttributeTests — fill-band forward/depth pass vertex-attribute parity
    // ───────────────────────────────────────────────────────────────────────────────────

// FillBandAttributeTests.cs — the fill boundary band's plumbing, checked where a rendered test cannot.
//
// WHY THESE ARE FILE-READING TESTS AND NOT RENDERS. A TEXCOORDn semantic binds to the MESH's
// VertexAttributeDescriptor index, globally — not to a pass's own Attributes struct — and an UNBOUND
// semantic silently zero-fills rather than failing to compile.
//
// The unqualified form of that claim — "no render can tell the six passes apart" — was TRUE ONLY WHILE
// every vertex carried side = 0, i.e. before the band node existed: back then a pass that forgot the
// attribute rendered exactly like one that has it, because the zero-fill WAS the correct value. Now that
// real band geometry ships, a missing attribute in one of the two FORWARD passes is render-visible. What
// stays invisible, and is what still justifies reading declarations rather than pixels, is the four
// DEPTH-writing passes: their band fragments are clipped, so a missing attribute there changes no pixel
// and no depth sample either way, and the only instrument that can see it is one that reads the file.
//
// The same holds for the depth clip itself. A band fragment at coverage 0 under a pass's hardcoded
// ZWrite On writes depth and casts a shadow, so a missing clip fattens the shadow silhouette — and now
// that band fragments exist, that is a live defect rather than a latent one.

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
                // The whole ternary, not just its opening: `clip(input.side > 0 ? 1 : -1)` is the
                // inversion that discards the fill INTERIOR and keeps only the band, and a match on
                // the condition alone reads it as correct.
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
        /// The fill's coverage is the line path's shipped outer-edge ramp, not a re-derivation, and both
        /// files are read here so it stays that way. The gradient in particular must remain EUCLIDEAN:
        /// <c>fwidth</c> is the Manhattan length, over-reads by up to 1.41x on a diagonal silhouette, and
        /// agrees exactly with the Euclidean form on every axis-aligned fixture — so no axis-aligned tooth
        /// anywhere can catch that substitution and this one must.
        ///
        /// <para>The line file writes <c>absSide</c> where the fill writes <c>side</c>: the line's band
        /// straddles its centre so its coordinate is signed, while the fill's band lies wholly outside the
        /// boundary and never goes negative. That single substitution is applied here and is the only
        /// licensed difference between the two expressions.</para>
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
        /// <c>Fill_VertexModify.hlsl</c>'s band displacement must NOT be scaled by <c>band.z</c>.
        ///
        /// <para>The attribute's two halves are independent: <c>band.xy</c> IS the displacement in device
        /// pixels (miter factor in its magnitude) and <c>band.z</c> is only the coverage coordinate. A
        /// product of the two is indistinguishable from the correct expression on the FLAT arm, where the
        /// band job emits <c>z</c> in <c>{0,1}</c> and <c>xy == 0</c> whenever <c>z == 0</c> — so every flat
        /// tooth, every golden and both frozen digests stay green either way. On the CURVED arm
        /// <c>GlobeFillSubdivideJob</c> splits a band edge at its midpoint and lerps BOTH halves, so the
        /// product is quadratic in the split parameter: the strip pinches to a quarter pixel at every
        /// midpoint and compounds with depth, exactly where the shipped globe scene lives.</para>
        ///
        /// <para>A forbidden-token check, so it fails CLOSED: it cannot pass by matching nothing. RED-verify
        /// by restoring the <c>* band.z</c> factor on the <c>positionOS +=</c> line.</para>
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
            // The WHOLE statement, not the tail after the anchor: a `* band.z` written BEFORE the
            // MapPixelsToWorld call is the form a restore would most naturally take, and a window that
            // starts at the anchor cannot see it. No earlier ';' gives -1 → start 0, so the window only
            // ever widens — this never fails open.
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
    /// The fence that makes the Core/Jobs split enforceable rather than remembered.
    ///
    /// <para><b>This is a fence, not a migration target.</b> <c>MapRenderer.Core</c> references only
    /// <c>Unity.Mathematics</c> and <c>UniTask</c> today and has zero <c>NativeArray</c> uses, so nothing here
    /// had to be cleaned up. What this tooth guards is the <b>naive fix</b> that was declined: adding
    /// <c>"Unity.Collections"</c> to <c>MapRenderer.Core.asmdef</c> so <c>SymbolFeatureExtractor</c> could read
    /// a <c>TileGeometryBuffers</c> without leaving Core. That one-line edit compiles, passes every existing
    /// test, and silently erases the split the design states — <i>Core keeps the managed
    /// evaluation surface and never reads coordinates; Jobs owns the blittable geometry</i>. The move paid for the
    /// split by moving the extractor into <c>MapRenderer.Unity</c> instead (and 75 tests out of the fast
    /// <c>dotnet</c> loop); without this tooth that cost could be quietly refunded.</para>
    ///
    /// <para><b>The source scan is comment-stripped, and that is not optional.</b> Core carries a dozen prose
    /// mentions of <c>NativeArray</c>/<c>NativeList</c>/<c>Unity.Collections</c> — every one a doc comment
    /// saying "blittable, so a Burst job can hold it". A raw-text scan would be RED on landing.</para>
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
            // Not a file-count floor: Core is a legacy assembly the roadmap is shrinking
            // (ARCHITECTURE.md's module boundaries), so a threshold here would eventually fail a SUCCESSFUL migration and
            // invite lowering the number, which quietly weakens this fence. Non-vacuity only needs "the
            // scan actually visited files" — the Jobs positive control below already proves the matcher
            // can see Unity.Collections when it's really there; this precondition covers the one hazard
            // that control cannot: coreRoot resolving to an empty (or wrong) directory, which would make
            // the "no offenders" result below true for the wrong reason.
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

            // Non-vacuity (the positive control that matters): the SAME scan over MapRenderer.Jobs must find
            // Unity.Collections in both the asmdef and at least one source file. A zero over Core proves
            // nothing if the matcher cannot see the thing it reports as absent.
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

// Unity EditMode only — shells out to git and reads source files via ShaderPropertyParser.RepoRoot.
// NOT registered in core-tests.csproj.
//
// Prevention tooth. Block reconstruction is ported from the census script that found the
// original 21 phantom names / 51 sites: this repo hard-wraps comments — a real path can split across
// two `//` lines, which a line-bound scan both invents phantoms from (by splitting a real path) and
// hides real ones behind (by never rejoining them).
//
// Scope: only a citation that carries an actual `*.md` FILENAME is checked. A bare pointer with no
// filename (`§4`, `design doc §6`, `S20 stage doc §6`) is out of scope — deciding which
// document "design doc" means is a judgment call, not a mechanical sweep, and is not this tooth's job.
//
// This tooth pins the CURRENT set of cited documents: renaming, moving, or deleting a `.md` file that
// source cites reds this test. That is doing its job — update the citing comment(s) to the new name (or
// state the fact inline and drop the pointer, per this ticket's own repair rule), it is not a false alarm.

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

            // The two lists are drawn from different populations. A citation resolves only
            // against COMMITTED documents — an untracked local .md exists on one machine and nowhere
            // else, which is the defect class this fence exists for. But the scan must read untracked
            // sources too, or it cannot see a phantom in the commit that introduces one.
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

                    // Exact basename match only — NOT a suffix check. A wrapped path split into a
                    // bare fragment must not then pass by matching as a suffix of a real name such as
                    // "meshing-design.md"; that hole sits directly in the path of the likeliest phantom.
                    // This comment names no phantom filename: the scan below reads THIS
                    // file too, so an illustrative fake name here would fail the fence it documents.
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
            // `ls-files` reports the INDEX, which still lists a path deleted in the working tree but not
            // yet staged — reading it below would throw. A path missing from disk carries no citation to
            // check, so dropping it here loses no coverage.
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
    /// E1 (the render-layer model): <c>RenderLayerFactory</c> is
    /// the ONE registry mapping a <see cref="MapRenderer.Core.Style.StyleLayer"/> subtype to its runtime
    /// render layer. This is a grep guard, modeled on
    /// <see cref="MapRenderer.Tests.Text.Placement.SymbolPlacementStructureTests"/>'s <c>AddTileLayer</c>
    /// guard: neither <c>MapView</c> nor <c>SymbolSubsystem</c> may re-dispatch on a
    /// <c>Fill</c>/<c>Line</c>/<c>Symbol</c> <c>StyleLayer</c> subtype — they derive "which sources to
    /// fetch" / "which layers are mine" from the already-built <c>RenderLayerSet</c> instead (the three
    /// scattered switches, now killed to one).
    /// </summary>
    [TestFixture]
    public class RenderLayerRegistryStructureTests
    {
        // Matches a StyleLayer-subtype type-check ("is Fill.StyleLayer", "is MapRenderer.Core.Style.Symbol.StyleLayer",
        // "is not Line.StyleLayer") — a re-dispatch outside the one registry. Deliberately narrow (the CALL
        // form only) so prose in a doc comment discussing the constraint is not itself flagged.
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

        /// <summary>The Mercator-only background gate — a <c>SetVisible</c>
        /// method on <c>BackgroundRenderLayer</c> that <c>MapView</c> toggled via
        /// <c>b.SetVisible(!curvedGround)</c> — is DELETED; background is now a per-covered-tile TileMesh
        /// layer projected through the same IProjection fill/line use, so the globe renders a correctly
        /// curved background instead of hiding it entirely.
        ///
        /// <para>Scoped to <c>BackgroundRenderLayer.cs</c> (the gate's own file), not a grep of
        /// <c>MapView.cs</c> for ANY <c>SetVisible(</c>: the method being gone is compile-backed (a MapView
        /// call could not resolve without it), and this narrow scope can't be falsely tripped by an unrelated
        /// future <c>SetVisible</c> call elsewhere in <c>MapView</c>.</para></summary>
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
}
