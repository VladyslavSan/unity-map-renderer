// S79 No-Raw-String Material Access Guard — engine-free, ~0.1s via `dotnet test Tools/core-tests`.
//
// Structural guards that enforce:
//   1. Registry layout: six registry files exist under Rendering/ShaderProperties/{,Line,Fill}/.
//      ShaderPropertiesCompat.cs is gone. The old flat-directory files are gone.
//   2. Migration completeness: no file under MapRenderer.Unity uses the old prefixed class names
//      (LinePropertyId, FillPropertyId, LinePropertyNames, FillPropertyNames) or the compat-shim
//      class declaration (class ShaderProperties). ShaderPropertiesToken is gone.
//   3. No raw-string Material API: no file under Assets/MapRenderer.Unity/**/*.cs uses
//      .Set/Get(Float|Color|Vector|Int|Texture)("...") or .HasProperty("...") string-literal forms.
//   4. No bare literals in PropertyId files: every PropertyId.cs member is defined as
//      Shader.PropertyToID(PropertyNames.X), never Shader.PropertyToID("_Foo").

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class NoRawStringMaterialAccessGuardTests
    {
        private static string RepoRoot      => ShaderPropertyParser.RepoRoot;
        private static string UnityDir      => Path.Combine(RepoRoot, "Assets", "MapRenderer.Unity");
        private static string RenderingDir  => Path.Combine(UnityDir, "Rendering");
        private static string ShaderPropertiesDir => Path.Combine(RenderingDir, "ShaderProperties");

        // ── 1. Registry layout guard ─────────────────────────────────────────────────────────────

        [Test]
        public void ShaderPropertiesCs_DoesNotExist()
        {
            // The old flat class must be gone.
            string oldFile = Path.Combine(RenderingDir, "ShaderProperties.cs");
            Assert.That(File.Exists(oldFile), Is.False,
                $"ShaderProperties.cs still exists at {oldFile}. It must be deleted.");
        }

        [Test]
        public void ShaderPropertiesCompatCs_DoesNotExist()
        {
            // The transitional compat shim must be gone after S79.
            string compatFile = Path.Combine(RenderingDir, "ShaderPropertiesCompat.cs");
            Assert.That(File.Exists(compatFile), Is.False,
                $"ShaderPropertiesCompat.cs still exists at {compatFile}. Delete it after BaseShaderGUI is migrated.");
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
            // After S79, the prefixed class names are gone: LinePropertyId, FillPropertyId,
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
                $"These files declare 'class ShaderProperties'. After S79, ShaderProperties is a namespace " +
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
                $"These files reference ShaderPropertiesToken. It should no longer exist after S79:\n" +
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
            // After S79 migration every call site uses a cached int id from ShaderProperties.PropertyId /
            // ShaderProperties.Line.PropertyId / ShaderProperties.Fill.PropertyId, so there should be
            // ZERO matching lines. No whole-file exclusions (BaseShaderGUI is fully migrated in S79).
            var badPattern = new Regex(
                @"\.(Set|Get)(Float|Color|Vector|Int|Texture)\s*\(\s*""|\.HasProperty\s*\(\s*""");

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
}
