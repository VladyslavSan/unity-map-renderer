// Editor-only (NOT engine-free) text-parsing helpers for shader/registry file inspection.
// Used by InstanceStructShaderParityTests, MaterialPropertyRegistryParityTests, and
// NoRawStringMaterialAccessGuardTests. Not registered in Tools/core-tests — see
// EngineFreeShaderPaths for the engine-free equivalent that project uses instead.
//
// Path resolution is move-proof: everything is anchored via UnityEditor.AssetDatabase (GUID-based
// lookup + the MapRenderer.Unity.asmdef location), not relative filesystem arithmetic off a test
// binary's location. Moving Assets/Code/MapRenderer.Unity as a whole keeps every derived path correct.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Static text-parsing helpers for validating shader + registry file structure, plus the
    /// move-proof path anchors those parsers read from.
    /// </summary>
    internal static class ShaderPropertyParser
    {
        // ── Repository / assembly anchors ────────────────────────────────────────────────────────

        /// <summary>
        /// Absolute path to the repository root. <see cref="Application.dataPath"/> is the project's
        /// <c>Assets/</c> folder; its parent is the Unity project root, which is also the repo root
        /// here (a stable Unity anchor, unlike relative arithmetic off a test binary's location).
        /// Only needed for files that live outside <c>Assets/</c> (e.g. THIRD-PARTY-NOTICES.txt).
        /// </summary>
        public static string RepoRoot => Directory.GetParent(Application.dataPath).FullName;

        /// <summary>
        /// Absolute path to the <c>MapRenderer.Unity</c> assembly root, resolved by locating its
        /// asmdef via <see cref="AssetDatabase"/> — move-proof against the whole assembly being
        /// relocated (e.g. the Assets/Code/ move). All shader/rendering sub-paths are expressed
        /// relative to this anchor.
        /// </summary>
        public static string UnityAssemblyRoot => ResolveUnityAssemblyRoot();

        public static string ShadersDir   => Path.Combine(UnityAssemblyRoot, "Shaders");
        public static string RenderingDir => Path.Combine(UnityAssemblyRoot, "Rendering");
        public static string MapDir       => Path.Combine(ShadersDir, "Map");
        public static string MapFillDir   => Path.Combine(ShadersDir, "Map", "Fill");
        public static string MapLineDir   => Path.Combine(ShadersDir, "Map", "Line");
        public static string MapExtrusionDir => Path.Combine(ShadersDir, "Map", "FillExtrusion");
        public static string CommonDir    => Path.Combine(ShadersDir, "Common");

        /// <summary>
        /// Resolves a shader source file (<c>.hlsl</c>/<c>.shader</c>) to its absolute path by filename,
        /// searching anywhere under <c>Shaders/Map/</c> — move-proof against the Lit/Unlit folder split (and
        /// any future reorg). Shader filenames are unique across the Map tree, so exactly one match is
        /// expected; a count ≠ 1 fails loudly (a duplicate or a missing file, not a silent wrong pick). Use
        /// this for CONTENT reads (a test that inspects a file's text); the few tests whose subject is the
        /// folder LAYOUT name their structure directly instead.
        /// </summary>
        public static string MapShaderPath(string fileName)
        {
            string[] matches = Directory.GetFiles(MapDir, fileName, SearchOption.AllDirectories);
            Assert.That(matches.Length, Is.EqualTo(1),
                $"Expected exactly one '{fileName}' under Shaders/Map/; found {matches.Length}" +
                (matches.Length > 1 ? " [" + string.Join(", ", matches) + "]" : "") + ".");
            return matches[0];
        }

        private static string ResolveUnityAssemblyRoot()
        {
            foreach (string guid in AssetDatabase.FindAssets("MapRenderer.Unity t:AssemblyDefinitionAsset"))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(assetPath) == "MapRenderer.Unity.asmdef")
                    return Path.Combine(RepoRoot, Path.GetDirectoryName(assetPath));
            }

            Assert.Fail("Could not locate MapRenderer.Unity.asmdef via AssetDatabase.FindAssets — " +
                "has the assembly been renamed or removed?");
            return null;
        }

        /// <summary>
        /// Resolves a single asset's absolute path by exact filename via <see cref="AssetDatabase"/>
        /// (move-proof against folder reorganizations). Use for files whose containing folder isn't
        /// itself asserted by structural tests (e.g. license text files).
        /// </summary>
        public static string ResolveAssetPathByName(string fileName)
        {
            string searchTerm = Path.GetFileNameWithoutExtension(fileName);
            foreach (string guid in AssetDatabase.FindAssets(searchTerm))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(assetPath) == fileName)
                    return Path.Combine(RepoRoot, assetPath);
            }

            Assert.Fail($"Could not locate asset by name via AssetDatabase: {fileName}");
            return null;
        }

        // ── DOTS block parser (moved here from InstanceStructShaderParityTests) ──────────────────

        /// <summary>
        /// Parses a <c>*_LitInput.hlsl</c> file for <c>UNITY_DOTS_INSTANCED_PROP</c> entries within the
        /// <c>UNITY_DOTS_INSTANCING_START…END</c> block.
        /// Returns a dictionary of {name → floatCount}.
        /// </summary>
        public static Dictionary<string, int> ParseDotsProps(string filePath)
        {
            Assert.That(File.Exists(filePath), Is.True, $"Shader file not found: {filePath}");
            string text = File.ReadAllText(filePath);

            var blockMatch = Regex.Match(
                text,
                @"UNITY_DOTS_INSTANCING_START\s*\(\s*\w+\s*\)(.*?)UNITY_DOTS_INSTANCING_END\s*\(\s*\w+\s*\)",
                RegexOptions.Singleline);

            Assert.That(blockMatch.Success, Is.True,
                $"No UNITY_DOTS_INSTANCING_START…END block found in: {Path.GetFileName(filePath)}");

            string dotsBlock = blockMatch.Groups[1].Value;

            // Longest type alternative first (float4 before float).
            var propRegex = new Regex(
                @"UNITY_DOTS_INSTANCED_PROP\(\s*(float4|float)\s*,\s*(_\w+)\s*\)");

            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match m in propRegex.Matches(dotsBlock))
            {
                string typeName = m.Groups[1].Value;
                string propName = m.Groups[2].Value;
                result[propName] = TypeToFloatCount(typeName);
            }
            return result;
        }

        // ── CBUFFER parser ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses the <c>CBUFFER_START(UnityPerMaterial)…CBUFFER_END</c> block from a <c>*_LitInput.hlsl</c>
        /// file. Extracts typed member declarations and strips the SRP companion ignore-set:
        /// names matching <c>_*_ST</c> or <c>_*_TexelSize</c>. The macro token
        /// <c>UNITY_TEXTURE_STREAMING_DEBUG_VARS</c> does not match the type-prefix regex and is
        /// naturally excluded.
        /// </summary>
        public static HashSet<string> ParseCbufferMembers(string hlslPath)
        {
            Assert.That(File.Exists(hlslPath), Is.True, $"HLSL file not found: {hlslPath}");
            string text = File.ReadAllText(hlslPath);

            var blockMatch = Regex.Match(
                text,
                @"CBUFFER_START\s*\(\s*UnityPerMaterial\s*\)(.*?)CBUFFER_END",
                RegexOptions.Singleline);

            Assert.That(blockMatch.Success, Is.True,
                $"No CBUFFER_START(UnityPerMaterial)…CBUFFER_END block found in: {Path.GetFileName(hlslPath)}");

            string cbuffer = blockMatch.Groups[1].Value;

            // Longest-first alternation so half4/half3/half2 are matched before half; float4 before float.
            var memberRegex = new Regex(
                @"(?:half4|half3|half2|float4|float3|float2|half|float)\s+(_\w+)\s*;");

            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in memberRegex.Matches(cbuffer))
            {
                string name = m.Groups[1].Value;
                // Strip SRP companion entries.
                if (name.EndsWith("_ST", StringComparison.Ordinal)
                    || name.EndsWith("_TexelSize", StringComparison.Ordinal))
                    continue;
                result.Add(name);
            }
            return result;
        }

        // ── PropertyNames.cs region parser ──────────────────────────────────────────────────────

        /// <summary>
        /// Parses a <c>PropertyNames.cs</c> file and returns the <c>"_X"</c> values from the region
        /// delimited by <paramref name="regionMarker"/>. The region starts on the line immediately
        /// following the marker and ends at the next line starting with <c>// region:</c> or at end
        /// of file. Fails the test loudly if the marker is not found (a typo would silently miscount).
        /// </summary>
        public static HashSet<string> ParseRegionValues(string csPath, string regionMarker)
        {
            Assert.That(File.Exists(csPath), Is.True, $"C# registry file not found: {csPath}");
            string[] lines = File.ReadAllLines(csPath);

            int startLine = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(regionMarker))
                {
                    startLine = i + 1;
                    break;
                }
            }

            Assert.That(startLine, Is.GreaterThan(0),
                $"Region marker '{regionMarker}' not found verbatim in {Path.GetFileName(csPath)}. " +
                "A marker typo would silently miscount the CBUFFER set — fix the marker or the file.");

            var constValueRegex = new Regex(@"public\s+const\s+string\s+\w+\s*=\s*""(_\w+)""\s*;");
            var result = new HashSet<string>(StringComparer.Ordinal);

            for (int i = startLine; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                // Stop at the next region marker.
                if (line.StartsWith("// region:", StringComparison.Ordinal))
                    break;

                var m = constValueRegex.Match(lines[i]);
                if (m.Success)
                    result.Add(m.Groups[1].Value);
            }

            return result;
        }

        /// <summary>
        /// Parses ALL <c>public const string X = "_Y";</c> entries in a <c>PropertyNames.cs</c>
        /// (or any similar registry file), regardless of region. Returns the values (the <c>"_Y"</c> strings).
        /// </summary>
        public static HashSet<string> ParseAllConstStringValues(string csPath)
        {
            Assert.That(File.Exists(csPath), Is.True, $"C# registry file not found: {csPath}");
            string text = File.ReadAllText(csPath);

            var constValueRegex = new Regex(@"public\s+const\s+string\s+\w+\s*=\s*""(_\w+)""\s*;");
            var result = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match m in constValueRegex.Matches(text))
                result.Add(m.Groups[1].Value);

            return result;
        }

        // ── Shader Properties{} block parser ────────────────────────────────────────────────────

        /// <summary>
        /// Parses the <c>Properties { … }</c> block from a Unity <c>.shader</c> file and returns
        /// the set of property names (the <c>_XxxName</c> identifiers).
        /// </summary>
        public static HashSet<string> ParseShaderPropertiesBlock(string shaderPath)
        {
            Assert.That(File.Exists(shaderPath), Is.True, $"Shader file not found: {shaderPath}");
            string text = File.ReadAllText(shaderPath);

            // Find Properties { ... } — capture from '{' to the matching '}'.
            int propStart = text.IndexOf("Properties", StringComparison.Ordinal);
            Assert.That(propStart, Is.GreaterThanOrEqualTo(0),
                $"'Properties' keyword not found in: {Path.GetFileName(shaderPath)}");

            int braceOpen = text.IndexOf('{', propStart);
            Assert.That(braceOpen, Is.GreaterThan(propStart),
                $"Properties opening '{{' not found in: {Path.GetFileName(shaderPath)}");

            int depth = 1;
            int i = braceOpen + 1;
            while (i < text.Length && depth > 0)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}') depth--;
                i++;
            }
            string block = text.Substring(braceOpen + 1, i - braceOpen - 2);

            // Match property names: optional [Attribute] prefix, then _PropName(.
            var propRegex = new Regex(@"(?:\[[^\]]*\]\s*)*(_\w+)\s*\(");
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in propRegex.Matches(block))
                result.Add(m.Groups[1].Value);

            return result;
        }

        // ── HLSL struct semantics parser ────────────────────────────────────────────────────────

        /// <summary>
        /// Parses a named <c>struct { … };</c> declaration from an HLSL file and returns the set of
        /// vertex-semantic tokens (the <c>SEMANTIC</c> in <c>: SEMANTIC;</c>) its fields declare. Used by
        /// the shared-vertex-layout structural teeth: a shader's <c>Attributes</c> semantics must be a
        /// subset of what the mesh builder actually emits (<c>VertexAttributeDescriptor</c> streams), so a
        /// shader cannot silently require an attribute no builder writes.
        /// </summary>
        public static HashSet<string> ParseStructSemantics(string hlslPath, string structName)
        {
            Assert.That(File.Exists(hlslPath), Is.True, $"HLSL file not found: {hlslPath}");
            string text = File.ReadAllText(hlslPath);

            var structMatch = Regex.Match(
                text,
                $@"struct\s+{Regex.Escape(structName)}\s*\{{(.*?)\}}\s*;",
                RegexOptions.Singleline);

            Assert.That(structMatch.Success, Is.True,
                $"No 'struct {structName} {{ … }};' found in: {Path.GetFileName(hlslPath)}");

            string body = structMatch.Groups[1].Value;
            var semanticRegex = new Regex(@":\s*(\w+)\s*;");

            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in semanticRegex.Matches(body))
                result.Add(m.Groups[1].Value);
            return result;
        }

        // ── Shared utility ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Strips <c>//</c> line comments and <c>/* … */</c> block comments from HLSL source, so a
        /// structural grep-tooth scans CODE only and never self-documenting prose. A shader that names a
        /// forbidden token to explain its ABSENCE ("no <c>UniversalFragmentPBR</c>/<c>SAMPLE_GI</c> —
        /// UniversalFragmentUnlit composes the colour instead") is good documentation and must not trip a
        /// "references no lighting call" tooth; a naive <c>text.Contains(token)</c> would false-positive on
        /// it. Not a full preprocessor — it does not model string literals (these HLSL files carry none),
        /// which is why it is scoped to token-absence scans, not lexing.
        /// </summary>
        /// <param name="text">Raw HLSL source.</param>
        /// <returns>The source with comment spans replaced by a single space (offsets not preserved).</returns>
        public static string StripHlslComments(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"//[^\n]*", " ");
            return text;
        }

        /// <summary>
        /// Maps an HLSL/GLSL type name to a float count.
        /// Supports both <c>float*</c> (DOTS block + struct) and <c>half*</c> (CBUFFER) variants.
        /// </summary>
        public static int TypeToFloatCount(string typeName)
        {
            switch (typeName)
            {
                case "float3x4":             return 12;
                case "float4": case "half4": return 4;
                case "float3": case "half3": return 3;
                case "float2": case "half2": return 2;
                case "float":  case "half":  return 1;
                default:
                    Assert.Fail($"Unknown type in shader/struct declaration: '{typeName}'");
                    return 0;
            }
        }
    }
}
