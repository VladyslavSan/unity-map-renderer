// ShaderStructureTests.cs — structural acceptance tests (pure System.IO, no Unity required).
//
// These tests encode the "teeth" of the shader-tree structure at the file level:
//   • Required files exist in the S66 layout: Shaders/Common/ (reference template only) +
//     Shaders/Map/<Layer>/ (per-geometry, self-contained shaders)
//   • UCL attribution headers are present in all mirror-copied files
//   • InitializeStandardLitSurfaceData is used (not hand-assembled SurfaceData)
//   • CBUFFER (UnityPerMaterial) is declared in each layer's <Layer>_LitInput.hlsl
//   • _NORMALMAP / _METALLICSPECGLOSSMAP pragmas are present (required for teeth #1 and #3)
//   • THIRD-PARTY-NOTICES.txt has a UCL entry
//   • UCL license text file exists
//
// These run via `dotnet test Tools/core-tests` in ~0.1s without Unity.
// Complement to the Unity EditMode GPU tests in LitFillSnapshotTests.cs.
//
// History: originally S34 (flat Shaders/). Updated for S58 (the redundant _MapColor tint was
// collapsed into _BaseColor), S56 (reorg into Common/ + Map/<Layer>/), and S66 (each layer is
// self-contained; Common/ holds only LitInput.Template.hlsl; files renamed to <Layer>_<Pass>).

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

        // ── File existence (S66 layout) ───────────────────────────────────────

        [Test]
        public void ShaderFiles_AllRequiredFilesExist()
        {
            (string dir, string name)[] required = new[]
            {
                // Fill layer — self-contained under Map/Fill/
                (MapFillDir, "Fill.shader"),
                (MapFillDir, "Fill_LitInput.hlsl"),
                (MapFillDir, "Fill_VertexModify.hlsl"),
                (MapFillDir, "Fill_LitForwardPass.hlsl"),
                (MapFillDir, "Fill_LitGBufferPass.hlsl"),
                (MapFillDir, "Fill_ShadowCasterPass.hlsl"),
                (MapFillDir, "Fill_DepthOnlyPass.hlsl"),
                (MapFillDir, "Fill_DepthNormalsPass.hlsl"),
                // Line layer — self-contained under Map/Line/
                (MapLineDir, "Line.shader"),
                (MapLineDir, "Line_LitInput.hlsl"),
                (MapLineDir, "Line_LitForwardPass.hlsl"),
                // Common/ — reference template only, no .shader includes it
                (CommonDir,  "LitInput.Template.hlsl"),
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
        public void ShaderFiles_OldCommonFilesAreGone()
        {
            // S66: the old Common/ framework (used by Fill only) was moved into Map/Fill/.
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
                Assert.That(File.Exists(path), Is.False,
                    $"Old pre-S66 shader file still present (should have been moved): {Path.GetFileName(dir)}/{name}");
            }
        }

        [Test]
        public void ShaderFiles_OldLineFilesAreGone()
        {
            // S66: Line files renamed to Line_LitInput.hlsl / Line_LitForwardPass.hlsl.
            Assert.That(File.Exists(Path.Combine(MapLineDir, "MapLineInput.hlsl")), Is.False,
                "Old pre-S66 MapLineInput.hlsl still present (should be Line_LitInput.hlsl).");
            Assert.That(File.Exists(Path.Combine(MapLineDir, "MapLineForwardPass.hlsl")), Is.False,
                "Old pre-S66 MapLineForwardPass.hlsl still present (should be Line_LitForwardPass.hlsl).");
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

        [Test]
        public void ShaderFiles_CommonHoldsNoLiveShaderFiles()
        {
            // S66: Common/ holds only LitInput.Template.hlsl (a reference template, included by nobody).
            // No .shader file and no non-template .hlsl file belongs there.
            var liveInCommon = Directory.EnumerateFiles(CommonDir)
                .Where(p => (p.EndsWith(".shader", StringComparison.Ordinal)
                          || p.EndsWith(".hlsl", StringComparison.Ordinal))
                          && !p.EndsWith(".Template.hlsl", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToArray();
            Assert.That(liveInCommon, Is.Empty,
                "Common/ must hold only .Template.hlsl files (reference templates, not compiled) after S66. " +
                "Found live shader files: " + string.Join(", ", liveInCommon));
        }

        // ── UCL attribution headers ───────────────────────────────────────────

        [Test]
        public void FillLitInput_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_LitInput.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_LitInput.hlsl must carry a UCL attribution header (license requirement).");
            Assert.That(text, Does.Contain("Unity Technologies"),
                "Fill_LitInput.hlsl must attribute © Unity Technologies ApS.");
        }

        [Test]
        public void FillLitForwardPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_LitForwardPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_LitForwardPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillLitGBufferPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_LitGBufferPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_LitGBufferPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillShadowCasterPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_ShadowCasterPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_ShadowCasterPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillDepthOnlyPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_DepthOnlyPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_DepthOnlyPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void FillDepthNormalsPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_DepthNormalsPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Fill_DepthNormalsPass.hlsl must carry a UCL attribution header.");
        }

        [Test]
        public void LineLitInput_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(MapLineDir, "Line_LitInput.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Line_LitInput.hlsl must carry a UCL attribution header.");
            Assert.That(text, Does.Contain("Unity Technologies"),
                "Line_LitInput.hlsl must attribute © Unity Technologies ApS.");
        }

        [Test]
        public void LineLitForwardPass_HasUCLAttributionHeader()
        {
            string text = ReadShaderFile(MapLineDir, "Line_LitForwardPass.hlsl");
            Assert.That(text, Does.Contain("Unity Companion License"),
                "Line_LitForwardPass.hlsl must carry a UCL attribution header.");
        }

        // ── InitializeStandardLitSurfaceData usage ────────────────────────────
        // The decisive structural gate: SurfaceData must NOT be hand-assembled.

        [Test]
        public void FillLitInput_DefinesInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_LitInput.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData"),
                "Fill_LitInput.hlsl must define InitializeStandardLitSurfaceData — " +
                "this is the function that populates SurfaceData correctly (tooth #1 gate).");
        }

        [Test]
        public void FillLitForwardPass_CallsInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_LitForwardPass.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData("),
                "Fill_LitForwardPass.hlsl fragment must call InitializeStandardLitSurfaceData — " +
                "NEVER hand-assembled SurfaceData field-by-field (S34 acceptance tooth #4).");
        }

        [Test]
        public void FillLitGBufferPass_CallsInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_LitGBufferPass.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData("),
                "Fill_LitGBufferPass.hlsl fragment must call InitializeStandardLitSurfaceData — " +
                "NEVER hand-assembled SurfaceData field-by-field (S34 acceptance tooth #4).");
        }

        [Test]
        public void FillLitForwardPass_ModulatesAlbedoAndAlpha_AfterInit()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_LitForwardPass.hlsl");
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
        // SRP Batcher requires CBUFFER in each layer's LitInput file.

        [Test]
        public void FillLitInput_DeclaresCBUFFERUnityPerMaterial()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_LitInput.hlsl");
            Assert.That(text, Does.Contain("CBUFFER_START(UnityPerMaterial)"),
                "Fill_LitInput.hlsl must declare CBUFFER_START(UnityPerMaterial) — " +
                "this is the full URP Lit CBUFFER shape required by the SRP Batcher.");
        }

        [Test]
        public void FillLitInput_CBUFFERContainsFullURPLitProps()
        {
            string text = ReadShaderFile(MapFillDir, "Fill_LitInput.hlsl");
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
                    $"Fill_LitInput.hlsl CBUFFER must contain '{prop}' (full URP Lit shape + fill paint additions).");
        }

        [Test]
        public void CommonDir_DoesNotContainMapLitCore()
        {
            // S66: MapLitCore.hlsl was dissolved (its CBUFFER moved to Fill_LitInput.hlsl in S34;
            // the MapVertexModify forward declaration is no longer needed; MapEdgeAA was dead).
            string path = Path.Combine(CommonDir, "MapLitCore.hlsl");
            Assert.That(File.Exists(path), Is.False,
                "Common/MapLitCore.hlsl must not exist after S66 (dissolved: CBUFFER moved to " +
                "Fill_LitInput.hlsl, forward-declaration hack removed, MapEdgeAA deleted as dead).");
        }

        // ── No stale includes in pass bodies ──────────────────────────────────
        // S66: pass bodies must NOT self-include their layer input (the .shader provides it).

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
                string text = ReadShaderFile(MapFillDir, file);
                Assert.That(text, Does.Not.Contain("#include \"Fill_LitInput.hlsl\""),
                    $"{file} must NOT self-include Fill_LitInput.hlsl — Fill.shader provides it (S66 rule).");
                Assert.That(text, Does.Not.Contain("MapLitInput.hlsl"),
                    $"{file} must not reference old MapLitInput.hlsl (stale after S66 rename).");
                Assert.That(text, Does.Not.Contain("MapLitCore.hlsl"),
                    $"{file} must not reference deleted MapLitCore.hlsl (dissolved in S66).");
            }
        }

        [Test]
        public void LineLitForwardPass_DoesNotSelfIncludeLayerInput()
        {
            string text = ReadShaderFile(MapLineDir, "Line_LitForwardPass.hlsl");
            Assert.That(text, Does.Not.Contain("#include \"Line_LitInput.hlsl\""),
                "Line_LitForwardPass.hlsl must NOT self-include Line_LitInput.hlsl — Line.shader provides it (S66 rule).");
            Assert.That(text, Does.Not.Contain("MapLineInput.hlsl"),
                "Line_LitForwardPass.hlsl must not reference old MapLineInput.hlsl (stale after S66 rename).");
        }

        // ── No cross-folder Common/ includes from Map/ ────────────────────────

        [Test]
        public void MapLayerFiles_DoNotIncludeCommonFolder()
        {
            // S66: no file under Map/ should reach into Common/ via ../../Common/...
            foreach (var file in Directory.EnumerateFiles(MapFillDir, "*.hlsl")
                .Concat(Directory.EnumerateFiles(MapFillDir, "*.shader"))
                .Concat(Directory.EnumerateFiles(MapLineDir, "*.hlsl"))
                .Concat(Directory.EnumerateFiles(MapLineDir, "*.shader")))
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                Assert.That(text, Does.Not.Contain("Common/"),
                    $"{Path.GetFileName(file)} must not reach into Common/ (S66: each layer is self-contained).");
            }
        }

        // ── MapEdgeAA deleted ─────────────────────────────────────────────────

        [Test]
        public void Shaders_NoMapEdgeAAReferences()
        {
            // S66: MapEdgeAA was confirmed dead (zero call sites) and deleted with MapLitCore.hlsl.
            foreach (var file in Directory.EnumerateFiles(ShadersDir, "*.hlsl", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(ShadersDir, "*.shader", SearchOption.AllDirectories)))
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                Assert.That(text, Does.Not.Contain("MapEdgeAA"),
                    $"{Path.GetFileName(file)} references MapEdgeAA — it was deleted as a dead helper in S66.");
            }
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
            Assert.That(text, Does.Contain("Fill_LitInput.hlsl"),
                "THIRD-PARTY-NOTICES.txt UCL entry must list the mirrored files (e.g. Fill_LitInput.hlsl).");
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
