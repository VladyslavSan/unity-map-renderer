// InstanceStructShaderParityTests — bidirectional shader⇄struct parity guard (never-again guard).
//
// Runs engine-free via `dotnet test Tools/core-tests` in ~0.1s. Text-parses both sides:
//   • MapInstanceData.cs: struct field declarations (name + float-count)
//   • Line_LitInput.hlsl / Fill_LitInput.hlsl: UNITY_DOTS_INSTANCED_PROP entries in each DOTS block
//
// Forward (per-shader): every DOTS prop has a same-name struct field of matching float-count.
//   Removing _Width from the struct → fails over Line_LitInput.hlsl (the original bug direction).
//
// Reverse (union): every struct material-prop field appears in Fill∪Line DOTS names.
//   Adding a struct field no shader declares → fails reverse.
//
// Exact counts (lessons.md: assert the exact known total, never `>`):
//   struct material-prop fields == 29, Line DOTS props == 24, Fill DOTS props == 19.
//   A regex that silently matches nothing fails the exact count, not the forward/reverse check.
//
// History: introduced S76 to lock struct+shader counts and prevent re-introducing the BRG line-prop bug.
//   Counts dropped by 2 line props (31→29, 26→24) when _MetersPerPixel (S104) and _AaEdgeWidth (line-AA
//   removal, 0b910c7) were retired; the set-equality/forward/reverse checks confirm the removal is consistent.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class InstanceStructShaderParityTests
    {
        // Repo root and paths are now in ShaderPropertyParser.
        private static string RepoRoot     => ShaderPropertyParser.RepoRoot;
        private static string RenderingDir => Path.Combine(RepoRoot, "Assets", "MapRenderer.Unity", "Rendering");
        private static string MapFillDir   => Path.Combine(RepoRoot, "Assets", "MapRenderer.Unity", "Shaders", "Map", "Fill");
        private static string MapLineDir   => Path.Combine(RepoRoot, "Assets", "MapRenderer.Unity", "Shaders", "Map", "Line");

        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses MapInstanceData.cs for public struct field declarations:
        ///   public (float3x4|float4|float) _fieldName;
        ///   public (float3x4|float4|float) unity_fieldName;
        /// Returns a dictionary of {name → floatCount} excluding the unity_* transform fields.
        /// </summary>
        private static Dictionary<string, int> ParseStructMaterialFields(out int totalFieldCount)
        {
            string path = Path.Combine(RenderingDir, "Backend", "BRG", "MapInstanceData.cs");
            Assert.That(File.Exists(path), Is.True, $"MapInstanceData.cs not found at: {path}");
            string text = File.ReadAllText(path);

            // Longest-first alternation: float3x4 before float4 before float.
            // This prevents float from consuming the prefix of float4 or float3x4.
            var regex = new Regex(
                @"public\s+(float3x4|float4|float)\s+(_\w+|unity_\w+)\s*;",
                RegexOptions.Multiline);

            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            int allCount = 0;

            foreach (Match m in regex.Matches(text))
            {
                string typeName  = m.Groups[1].Value;
                string fieldName = m.Groups[2].Value;
                int    floats    = TypeToFloatCount(typeName);
                allCount++;

                // Exclude transform fields (unity_* prefix) from the material-prop map.
                if (fieldName.StartsWith("unity_", StringComparison.Ordinal))
                    continue;

                result[fieldName] = floats;
            }

            totalFieldCount = allCount;
            return result;
        }

        // ParseDotsProps and TypeToFloatCount are now in ShaderPropertyParser (shared with
        // MaterialPropertyRegistryParityTests). The private aliases below keep the call sites below
        // unchanged so this test class remains readable without requiring grep-and-replace.
        private static Dictionary<string, int> ParseDotsProps(string filePath)
            => ShaderPropertyParser.ParseDotsProps(filePath);

        private static int TypeToFloatCount(string typeName)
            => ShaderPropertyParser.TypeToFloatCount(typeName);

        // ── Exact count guards (lessons.md: assert exact, never >) ───────────────────────────

        /// <summary>
        /// Struct must have exactly 29 material-prop fields (excludes unity_* transforms).
        /// Delta from today: 19 pre-existing + 10 line props = 29 (was 12 line props before
        /// _MetersPerPixel (S104) and _AaEdgeWidth (line-AA removal) were retired).
        /// A vacuous regex (matches nothing) fails this immediately.
        /// </summary>
        [Test]
        public void StructMaterialFieldCount_IsExactly29()
        {
            var fields = ParseStructMaterialFields(out _);
            Assert.That(fields.Count, Is.EqualTo(29),
                $"MapInstanceData must have exactly 29 material-prop fields " +
                $"(14 common Lit+Opacity + 5 fill-only + 10 line-only). " +
                $"Found {fields.Count}: {string.Join(", ", fields.Keys)}");
        }

        /// <summary>Line_LitInput.hlsl DOTS block must have exactly 24 props.</summary>
        [Test]
        public void LineDotsPropCount_IsExactly24()
        {
            var props = ParseDotsProps(Path.Combine(MapLineDir, "Line_LitInput.hlsl"));
            Assert.That(props.Count, Is.EqualTo(24),
                $"Line_LitInput.hlsl DOTS block must have exactly 24 UNITY_DOTS_INSTANCED_PROP entries " +
                $"(13 common Lit + _Opacity + 10 line-specific). Found {props.Count}: {string.Join(", ", props.Keys)}");
        }

        /// <summary>Fill_LitInput.hlsl DOTS block must have exactly 19 props.</summary>
        [Test]
        public void FillDotsPropCount_IsExactly19()
        {
            var props = ParseDotsProps(Path.Combine(MapFillDir, "Fill_LitInput.hlsl"));
            Assert.That(props.Count, Is.EqualTo(19),
                $"Fill_LitInput.hlsl DOTS block must have exactly 19 UNITY_DOTS_INSTANCED_PROP entries " +
                $"(13 common Lit + _Opacity + 5 fill-specific). Found {props.Count}: {string.Join(", ", props.Keys)}");
        }

        // ── Forward parity: each DOTS prop → struct field of matching float-count ─────────────

        /// <summary>
        /// Every Line DOTS prop must have a same-name struct field with matching float-count.
        /// Removing _Width from MapInstanceData fails here (the original bug direction).
        /// </summary>
        [Test]
        public void Forward_LineDots_EachPropHasMatchingStructField()
        {
            var lineProps = ParseDotsProps(Path.Combine(MapLineDir, "Line_LitInput.hlsl"));
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
            var fillProps = ParseDotsProps(Path.Combine(MapFillDir, "Fill_LitInput.hlsl"));
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
            var lineProps    = ParseDotsProps(Path.Combine(MapLineDir, "Line_LitInput.hlsl"));
            var fillProps    = ParseDotsProps(Path.Combine(MapFillDir, "Fill_LitInput.hlsl"));

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
