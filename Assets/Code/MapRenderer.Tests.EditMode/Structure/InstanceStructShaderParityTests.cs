// InstanceStructShaderParityTests — bidirectional shader⇄struct parity guard (never-again guard).
//
// Runs in the Unity EditMode test assembly (moved from Tools/core-tests). Compares both sides:
//   • MapInstanceData.cs: struct field declarations (name + float-count), read via REFLECTION
//     (typeof(MapInstanceData), granted access by MapRenderer.Unity's
//     [InternalsVisibleTo("MapRenderer.Tests.EditMode")]) — not a text-parse of the .cs file.
//   • Line_LitInput.hlsl / Fill_LitInput.hlsl: UNITY_DOTS_INSTANCED_PROP entries in each DOTS block
//     (still text-parsed via ShaderPropertyParser.ParseDotsProps — HLSL has no reflection surface).
//
// Forward (per-shader): every DOTS prop has a same-name struct field of matching float-count.
//   Removing _Width from the struct → fails over Line_LitInput.hlsl (the original bug direction).
//
// Reverse (union): every struct material-prop field appears in Fill∪Line DOTS names.
//   Adding a struct field no shader declares → fails reverse.
//
// Exact counts (lessons.md: assert the exact known total, never `>`):
//   struct material-prop fields == 31, Line DOTS props == 24, Fill DOTS props == 21.
//   A regex/reflection pass that silently matches nothing fails the exact count, not the
//   forward/reverse check.
//
// History: introduced S76 to lock struct+shader counts and prevent re-introducing the BRG line-prop bug.
//   Counts dropped by 2 line props (struct 33→31, Line DOTS 26→24) when _MetersPerPixel (S104) and
//   _AaEdgeWidth (line-AA removal, 0b910c7) were retired; the set-equality/forward/reverse checks confirm it.
//   Migrated out of Tools/core-tests (folder-move path breakage) with the struct-side text-parse
//   replaced by reflection.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using MapRenderer.Unity.Rendering.Backend.BRG;

namespace MapRenderer.Tests.Structure
{
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

        // ── Exact count guards (lessons.md: assert exact, never >) ───────────────────────────

        /// <summary>
        /// Struct must have exactly 31 material-prop fields (excludes unity_* transforms).
        /// Breakdown: 21 non-line (14 common Lit+Opacity + 7 fill-only) + 10 line-only = 31 (was 12
        /// line-only before _MetersPerPixel (S104) and _AaEdgeWidth (line-AA removal) were retired).
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
}
