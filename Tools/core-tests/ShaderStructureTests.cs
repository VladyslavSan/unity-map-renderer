// ShaderStructureTests.cs — structural acceptance tests (pure System.IO, no Unity required).
//
// These tests encode the "teeth" of the shader-tree structure at the file level:
//   • Required files exist in the S56 layout: Shaders/Common/ (shared lit framework) +
//     Shaders/Map/<Layer>/ (per-geometry shaders); the flat Shaders/ root holds no .shader/.hlsl.
//   • UCL attribution headers are present in all mirror-copied Common files
//   • InitializeStandardLitSurfaceData is used (not hand-assembled SurfaceData)
//   • CBUFFER (UnityPerMaterial) is declared in MapLitInput.hlsl (not MapLitCore.hlsl)
//   • _NORMALMAP / _METALLICSPECGLOSSMAP pragmas are present (required for teeth #1 and #3)
//   • THIRD-PARTY-NOTICES.txt has a UCL entry
//   • UCL license text file exists
//
// These run via `dotnet test Tools/core-tests` in ~0.1s without Unity.
// Complement to the Unity EditMode GPU tests in LitFillSnapshotTests.cs.
//
// History: originally S34 (flat Shaders/). Updated for S58 (the redundant _MapColor tint was
// collapsed into _BaseColor) and S56 (the tree was reorganized into Common/ + Map/<Layer>/).

using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace MapRenderer.Tests
{
    [TestFixture]
    public class ShaderStructureTests
    {
        // Resolve repo root relative to the running test binary (Tools/core-tests/bin/Debug/net10.0/).
        private static string RepoRoot => Path.GetFullPath(
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../.."));

        private static string ShadersDir => Path.Combine(RepoRoot, "Assets", "MapRenderer.Unity", "Shaders");
        private static string CommonDir  => Path.Combine(ShadersDir, "Common");
        private static string MapFillDir => Path.Combine(ShadersDir, "Map", "Fill");
        private static string MapLineDir => Path.Combine(ShadersDir, "Map", "Line");

        // ── File existence (S56 layout) ───────────────────────────────────────

        [Test]
        public void ShaderFiles_AllRequiredFilesExist()
        {
            (string dir, string name)[] required = new[]
            {
                (CommonDir,  "MapLitInput.hlsl"),
                (CommonDir,  "MapLitCore.hlsl"),
                (CommonDir,  "MapLitForwardPass.hlsl"),
                (CommonDir,  "MapLitGBufferPass.hlsl"),
                (CommonDir,  "MapShadowCasterPass.hlsl"),
                (CommonDir,  "MapDepthOnlyPass.hlsl"),
                (CommonDir,  "MapDepthNormalsPass.hlsl"),
                (MapFillDir, "Fill.shader"),
                (MapFillDir, "Fill_Input.hlsl"),
                (MapLineDir, "Line.shader"),
                (MapLineDir, "MapLineForwardPass.hlsl"),
                (MapLineDir, "MapLineInput.hlsl"),
            };

            foreach (var (dir, name) in required)
            {
                string path = Path.Combine(dir, name);
                Assert.That(File.Exists(path), Is.True,
                    $"Required shader file missing: {Path.GetFileName(dir)}/{name}\n" +
                    $"(looked in: {dir})");
            }
        }

        [Test]
        public void ShaderFiles_FillAndLineLiveUnderMapLayer()
        {
            // S56 requires per-layer shaders under Shaders/Map/<Layer>/, not the flat Shaders/ root.
            Assert.That(File.Exists(Path.Combine(MapFillDir, "Fill.shader")), Is.True,
                "Fill.shader must live in Assets/MapRenderer.Unity/Shaders/Map/Fill/ (S56 layout).");
            Assert.That(File.Exists(Path.Combine(MapLineDir, "Line.shader")), Is.True,
                "Line.shader must live in Assets/MapRenderer.Unity/Shaders/Map/Line/ (S56 layout).");
        }

        [Test]
        public void ShaderFiles_FlatRootHoldsNoShaderSources()
        {
            // S56 tooth: after the reorg, no .shader/.hlsl may remain loose at the Shaders/ root —
            // they all live under Common/ or Map/<Layer>/.
            var loose = Directory.EnumerateFiles(ShadersDir)
                .Where(p => p.EndsWith(".shader", StringComparison.Ordinal)
                         || p.EndsWith(".hlsl", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToArray();
            Assert.That(loose, Is.Empty,
                "Shaders/ root must contain no loose .shader/.hlsl after S56 (found: "
                + string.Join(", ", loose) + "). They belong under Common/ or Map/<Layer>/.");
        }

        // ── UCL attribution headers ───────────────────────────────────────────

        [Test]
        public void MapLitInput_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(CommonDir, "MapLitInput.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapLitInput.hlsl must carry a UCL attribution header (license requirement).");
            Assert.That(text, Does.Contain("Unity Technologies"),
                "MapLitInput.hlsl must attribute © Unity Technologies ApS.");
        }

        [Test]
        public void MapLitForwardPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(CommonDir, "MapLitForwardPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapLitForwardPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void MapLitGBufferPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(CommonDir, "MapLitGBufferPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapLitGBufferPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void MapShadowCasterPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(CommonDir, "MapShadowCasterPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapShadowCasterPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void MapDepthOnlyPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(CommonDir, "MapDepthOnlyPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapDepthOnlyPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void MapDepthNormalsPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(CommonDir, "MapDepthNormalsPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapDepthNormalsPass.hlsl must carry a UCL attribution header.");
        }

        // ── InitializeStandardLitSurfaceData usage ────────────────────────────
        // The decisive structural gate: SurfaceData must NOT be hand-assembled.

        [Test]
        public void MapLitInput_DefinesInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile(CommonDir, "MapLitInput.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData"),
                "MapLitInput.hlsl must define InitializeStandardLitSurfaceData — " +
                "this is the function that populates SurfaceData correctly (tooth #1 gate).");
        }

        [Test]
        public void MapLitForwardPass_CallsInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile(CommonDir, "MapLitForwardPass.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData("),
                "MapLitForwardPass.hlsl fragment must call InitializeStandardLitSurfaceData — " +
                "NEVER hand-assembled SurfaceData field-by-field (S34 acceptance tooth #4).");
        }

        [Test]
        public void MapLitGBufferPass_CallsInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile(CommonDir, "MapLitGBufferPass.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData("),
                "MapLitGBufferPass.hlsl fragment must call InitializeStandardLitSurfaceData — " +
                "NEVER hand-assembled SurfaceData field-by-field (S34 acceptance tooth #4).");
        }

        [Test]
        public void MapLitForwardPass_ModulatesAlbedoAndAlpha_AfterInit()
        {
            string text = ReadShaderFile(CommonDir, "MapLitForwardPass.hlsl");
            // The init-then-modulate pattern: call init first, then multiply.
            int initIdx    = text.IndexOf("InitializeStandardLitSurfaceData(", StringComparison.Ordinal);

            // Post-S58: per-vertex color (vColor) drives albedo/alpha; the _BaseColor tint is applied
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

        // ── CBUFFER location ──────────────────────────────────────────────────
        // SRP Batcher requires CBUFFER in the input file, not scattered across passes.

        [Test]
        public void MapLitInput_DeclaresCBUFFERUnityPerMaterial()
        {
            string text = ReadShaderFile(CommonDir, "MapLitInput.hlsl");
            Assert.That(text, Does.Contain("CBUFFER_START(UnityPerMaterial)"),
                "MapLitInput.hlsl must declare CBUFFER_START(UnityPerMaterial) — " +
                "this is the full URP Lit CBUFFER shape required by the SRP Batcher.");
        }

        [Test]
        public void MapLitInput_CBUFFERContainsFullURPLitProps()
        {
            string text = ReadShaderFile(CommonDir, "MapLitInput.hlsl");
            // Spot-check a selection of URP Lit's CBUFFER members.
            string[] required = new[] {
                "_BaseMap_ST", "_BaseColor", "_SpecColor", "_EmissionColor",
                "_Cutoff", "_Smoothness", "_Metallic", "_BumpScale", "_OcclusionStrength",
                "_DetailAlbedoMapScale", "_DetailNormalMapScale",
                // Map addition (post-S58: _MapColor was collapsed into _BaseColor):
                "_Opacity"
            };
            foreach (var prop in required)
                Assert.That(text, Does.Contain(prop),
                    $"MapLitInput.hlsl CBUFFER must contain '{prop}' (full URP Lit shape + map additions).");
        }

        [Test]
        public void MapLitCore_DoesNOT_DeclareCBUFFER()
        {
            string text = ReadShaderFile(CommonDir, "MapLitCore.hlsl");
            Assert.That(text, Does.Not.Contain("CBUFFER_START(UnityPerMaterial)"),
                "MapLitCore.hlsl must NOT declare UnityPerMaterial CBUFFER (S34 refactor moved it to " +
                "MapLitInput.hlsl). A duplicate CBUFFER declaration causes SRP Batcher layout mismatch.");
        }

        // ── Shader feature pragmas (required for tooth #1 normal map, tooth #3 specular) ──

        [Test]
        public void FillShader_ForwardLit_HasNormalMapPragma()
        {
            string text = ReadShaderFile(MapFillDir, "Fill.shader");
            Assert.That(text, Does.Contain("shader_feature_local _NORMALMAP"),
                "Fill.shader ForwardLit pass must have '#pragma shader_feature_local _NORMALMAP'. " +
                "Without it, binding a normal map compiles to nothing and tooth #1 cannot pass.");
        }

        [Test]
        public void FillShader_HasMetallicSpecGlossMapPragma()
        {
            string text = ReadShaderFile(MapFillDir, "Fill.shader");
            Assert.That(text, Does.Contain("shader_feature_local_fragment _METALLICSPECGLOSSMAP"),
                "Fill.shader must have '#pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP'. " +
                "Without it, binding a metallic map compiles to nothing and tooth #3 cannot pass.");
        }

        [Test]
        public void FillShader_GBuffer_HasNormalMapPragma()
        {
            string text = ReadShaderFile(MapFillDir, "Fill.shader");
            // GBuffer pass also needs _NORMALMAP for correct deferred normal-map support.
            int gBufferIdx = text.IndexOf("Name \"GBuffer\"", StringComparison.Ordinal);
            Assert.That(gBufferIdx, Is.GreaterThanOrEqualTo(0), "GBuffer pass not found in Fill.shader.");
            string afterGBuffer = text.Substring(gBufferIdx);
            Assert.That(afterGBuffer, Does.Contain("shader_feature_local _NORMALMAP"),
                "Fill.shader GBuffer pass must also have _NORMALMAP pragma.");
        }

        // ── Shader declaration names (S56 rename: MapRenderer/<Layer> → Map/<Layer>) ──

        [Test]
        public void Shaders_DeclareMapLayerNames()
        {
            Assert.That(ReadShaderFile(MapFillDir, "Fill.shader"), Does.Contain("Shader \"Map/Fill\""),
                "Fill.shader must declare Shader \"Map/Fill\" (S56 rename).");
            Assert.That(ReadShaderFile(MapLineDir, "Line.shader"), Does.Contain("Shader \"Map/Line\""),
                "Line.shader must declare Shader \"Map/Line\" (S56 rename).");
        }

        // ── License files ─────────────────────────────────────────────────────

        [Test]
        public void ThirdPartyNotices_HasUCLEntry()
        {
            string path = Path.Combine(RepoRoot, "THIRD-PARTY-NOTICES.txt");
            Assert.That(File.Exists(path), Is.True, "THIRD-PARTY-NOTICES.txt must exist.");
            string text = File.ReadAllText(path, Encoding.UTF8);
            Assert.That(text, Does.Contain("Unity Companion License"),
                "THIRD-PARTY-NOTICES.txt must have a UCL entry (S34 license requirement).");
            Assert.That(text, Does.Contain("MapLitInput.hlsl"),
                "THIRD-PARTY-NOTICES.txt UCL entry must list the mirrored files (e.g. MapLitInput.hlsl).");
        }

        [Test]
        public void UnityCompanionLicenseFile_Exists()
        {
            string path = Path.Combine(RepoRoot, "Assets", "ThirdParty", "UnityCompanionLicense.txt");
            Assert.That(File.Exists(path), Is.True,
                "Assets/ThirdParty/UnityCompanionLicense.txt must exist (committed UCL text, S34 requirement).");
            string text = File.ReadAllText(path, Encoding.UTF8);
            Assert.That(text, Does.Contain("Unity Companion License"),
                "UnityCompanionLicense.txt must contain the actual UCL text.");
        }

        // ── Helper ─────────────────────────────────────────────────────────────

        private static string ReadShaderFile(string dir, string filename)
        {
            string path = Path.Combine(dir, filename);
            Assert.That(File.Exists(path), Is.True, $"File not found: {Path.GetFileName(dir)}/{filename}");
            return File.ReadAllText(path, Encoding.UTF8);
        }
    }
}
