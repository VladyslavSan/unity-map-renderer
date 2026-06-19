// ShaderStructureTests.cs — S34 structural acceptance tests (pure System.IO, no Unity required).
//
// These tests encode the "teeth" of S34 acceptance criteria at the file-structure level:
//   • Required files exist in Assets/MapRenderer.Unity/Shaders/
//   • UCL attribution headers are present in all mirror-copied files
//   • InitializeStandardLitSurfaceData is used (not hand-assembled SurfaceData)
//   • CBUFFER (UnityPerMaterial) is declared in MapLitInput.hlsl (not MapLitCore.hlsl)
//   • _NORMALMAP / _METALLICSPECGLOSSMAP pragmas are present (required for teeth #1 and #3)
//   • THIRD-PARTY-NOTICES.txt has a UCL entry
//   • UCL license text file exists
//
// These run via `dotnet test Tools/core-tests` in ~0.1s without Unity.
// Complement to the Unity EditMode GPU tests in LitFillSnapshotTests.cs.

using System;
using System.IO;
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
        private static string MaterialsDir => Path.Combine(RepoRoot, "Assets", "MapRenderer.Unity", "Materials");

        // ── File existence ────────────────────────────────────────────────────

        [Test]
        public void ShaderFiles_AllRequiredFilesExist()
        {
            string[] required = new[]
            {
                "MapLitInput.hlsl",
                "MapLitCore.hlsl",
                "MapLitForwardPass.hlsl",
                "MapLitGBufferPass.hlsl",
                "MapShadowCasterPass.hlsl",
                "MapDepthOnlyPass.hlsl",
                "MapDepthNormalsPass.hlsl",
                "Fill_Input.hlsl",
                "Fill.shader",
            };

            foreach (var name in required)
            {
                string path = Path.Combine(ShadersDir, name);
                Assert.That(File.Exists(path), Is.True,
                    $"Required shader file missing: Shaders/{name}\n" +
                    $"(looked in: {ShadersDir})");
            }
        }

        [Test]
        public void ShaderFiles_FillShaderIsInShadersDirectory()
        {
            // S34 requires Fill.shader to live in Shaders/ not Materials/.
            string shadersPath  = Path.Combine(ShadersDir, "Fill.shader");
            Assert.That(File.Exists(shadersPath), Is.True,
                "Fill.shader must live in Assets/MapRenderer.Unity/Shaders/ (S34 structural requirement).");
        }

        // ── UCL attribution headers ───────────────────────────────────────────

        [Test]
        public void MapLitInput_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("MapLitInput.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapLitInput.hlsl must carry a UCL attribution header (S34 license requirement).");
            Assert.That(text, Does.Contain("Unity Technologies"),
                "MapLitInput.hlsl must attribute © Unity Technologies ApS.");
        }

        [Test]
        public void MapLitForwardPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("MapLitForwardPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapLitForwardPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void MapLitGBufferPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("MapLitGBufferPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapLitGBufferPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void MapShadowCasterPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("MapShadowCasterPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapShadowCasterPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void MapDepthOnlyPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("MapDepthOnlyPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapDepthOnlyPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void MapDepthNormalsPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile("MapDepthNormalsPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "MapDepthNormalsPass.hlsl must carry a UCL attribution header.");
        }

        // ── InitializeStandardLitSurfaceData usage ────────────────────────────
        // The decisive structural gate: SurfaceData must NOT be hand-assembled.

        [Test]
        public void MapLitInput_DefinesInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile("MapLitInput.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData"),
                "MapLitInput.hlsl must define InitializeStandardLitSurfaceData — " +
                "this is the function that populates SurfaceData correctly (tooth #1 gate).");
        }

        [Test]
        public void MapLitForwardPass_CallsInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile("MapLitForwardPass.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData("),
                "MapLitForwardPass.hlsl fragment must call InitializeStandardLitSurfaceData — " +
                "NEVER hand-assembled SurfaceData field-by-field (S34 acceptance tooth #4).");
        }

        [Test]
        public void MapLitGBufferPass_CallsInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile("MapLitGBufferPass.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData("),
                "MapLitGBufferPass.hlsl fragment must call InitializeStandardLitSurfaceData — " +
                "NEVER hand-assembled SurfaceData field-by-field (S34 acceptance tooth #4).");
        }

        [Test]
        public void MapLitForwardPass_ModulatesAlbedoAndAlpha_AfterInit()
        {
            string text = ReadShaderFile("MapLitForwardPass.hlsl");
            // The init-then-modulate pattern: call init first, then multiply.
            int initIdx    = text.IndexOf("InitializeStandardLitSurfaceData(", StringComparison.Ordinal);
            int albedoIdx  = text.IndexOf("surfaceData.albedo *= _MapColor.rgb", StringComparison.Ordinal);
            int alphaIdx   = text.IndexOf("surfaceData.alpha  *= _Opacity", StringComparison.Ordinal);

            Assert.That(initIdx,   Is.GreaterThanOrEqualTo(0), "InitializeStandardLitSurfaceData call not found.");
            Assert.That(albedoIdx, Is.GreaterThan(initIdx),
                "albedo modulation (surfaceData.albedo *= _MapColor.rgb) must appear AFTER InitializeStandardLitSurfaceData.");
            Assert.That(alphaIdx,  Is.GreaterThan(initIdx),
                "alpha modulation (surfaceData.alpha *= _Opacity) must appear AFTER InitializeStandardLitSurfaceData.");
        }

        // ── CBUFFER location ──────────────────────────────────────────────────
        // SRP Batcher requires CBUFFER in the input file, not scattered across passes.

        [Test]
        public void MapLitInput_DeclaresCBUFFERUnityPerMaterial()
        {
            string text = ReadShaderFile("MapLitInput.hlsl");
            Assert.That(text, Does.Contain("CBUFFER_START(UnityPerMaterial)"),
                "MapLitInput.hlsl must declare CBUFFER_START(UnityPerMaterial) — " +
                "this is the full URP Lit CBUFFER shape required by the SRP Batcher.");
        }

        [Test]
        public void MapLitInput_CBUFFERContainsFullURPLitProps()
        {
            string text = ReadShaderFile("MapLitInput.hlsl");
            // Spot-check a selection of URP Lit's CBUFFER members.
            string[] required = new[] {
                "_BaseMap_ST", "_BaseColor", "_SpecColor", "_EmissionColor",
                "_Cutoff", "_Smoothness", "_Metallic", "_BumpScale", "_OcclusionStrength",
                "_DetailAlbedoMapScale", "_DetailNormalMapScale",
                // Map additions:
                "_MapColor", "_Opacity"
            };
            foreach (var prop in required)
                Assert.That(text, Does.Contain(prop),
                    $"MapLitInput.hlsl CBUFFER must contain '{prop}' (full URP Lit shape + map additions).");
        }

        [Test]
        public void MapLitCore_DoesNOT_DeclareCBUFFER()
        {
            string text = ReadShaderFile("MapLitCore.hlsl");
            Assert.That(text, Does.Not.Contain("CBUFFER_START(UnityPerMaterial)"),
                "MapLitCore.hlsl must NOT declare UnityPerMaterial CBUFFER (S34 refactor moved it to " +
                "MapLitInput.hlsl). A duplicate CBUFFER declaration causes SRP Batcher layout mismatch.");
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

        // ── MeshBuilder UV/Tangent ────────────────────────────────────────────
        // Structural check: MeshBuilder.cs must have UV and tangent channel code.

        [Test]
        public void MeshBuilder_HasUVAndTangentChannels()
        {
            string path = Path.Combine(RepoRoot, "Assets", "MapRenderer.Unity", "MeshBuilder.cs");
            Assert.That(File.Exists(path), Is.True, "MeshBuilder.cs not found.");
            string text = File.ReadAllText(path, Encoding.UTF8);

            Assert.That(text, Does.Contain("SetUVs"),
                "MeshBuilder.Build() must call mesh.SetUVs() to provide UV0 for texture sampling.");
            Assert.That(text, Does.Contain("SetTangents"),
                "MeshBuilder.Build() must call mesh.SetTangents() for normal map tangent space.");
            Assert.That(text, Does.Contain("_uvs"),
                "MeshBuilder must maintain a _uvs list (UV0 channel).");
            Assert.That(text, Does.Contain("_tangents"),
                "MeshBuilder must maintain a _tangents list (tangent channel).");
        }

        // ── Helper ─────────────────────────────────────────────────────────────

        private static string ReadShaderFile(string filename)
        {
            string path = Path.Combine(ShadersDir, filename);
            Assert.That(File.Exists(path), Is.True, $"File not found: Shaders/{filename}");
            return File.ReadAllText(path, Encoding.UTF8);
        }
    }
}
