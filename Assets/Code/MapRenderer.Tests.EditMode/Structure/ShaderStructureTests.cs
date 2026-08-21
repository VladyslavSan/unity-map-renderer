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
//   • Shaders/Map/PixelsToWorld.hlsl (S23 I2a) is the one sanctioned cross-folder include — every carrier
//     references it via ../PixelsToWorld.hlsl and none locally re-defines MapPixelsToWorld
//
// Runs in the Unity EditMode test assembly (moved from Tools/core-tests, which used
// test-binary-relative path arithmetic that broke on the Assets/Code/ folder move). Paths are now
// resolved via ShaderPropertyParser's AssetDatabase-anchored helpers — move-proof.
// Complement to the Unity EditMode GPU tests in LitFillSnapshotTests.cs.
//
// History: originally S34 (flat Shaders/). Updated for S58 (the redundant _MapColor tint was
// collapsed into _BaseColor), S56 (reorg into Common/ + Map/<Layer>/), S66 (each layer is
// self-contained; Common/ holds only LitInput.Template.hlsl; files renamed to <Layer>_<Pass>), and
// S23 I2a (the shared px→world measurement was hoisted out of per-layer sentinel-pinned copies into
// Shaders/Map/PixelsToWorld.hlsl — the one sanctioned exception to self-containment).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace MapRenderer.Tests.Structure
{
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

        // ── File existence (S66 layout) ───────────────────────────────────────

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
                // FillExtrusion (S23 I2b) — lit + shared
                "FillExtrusion.shader", "FillExtrusion_LitInput.hlsl", "FillExtrusion_VertexModify.hlsl",
                "FillExtrusion_LitForwardPass.hlsl", "FillExtrusion_LitGBufferPass.hlsl",
                "FillExtrusion_ShadowCasterPass.hlsl", "FillExtrusion_DepthOnlyPass.hlsl",
                "FillExtrusion_DepthNormalsPass.hlsl",
                // FillExtrusion — unlit twin
                "FillExtrusionUnlit.shader", "FillExtrusion_UnlitInput.hlsl",
                "FillExtrusion_UnlitForwardPass.hlsl",
            };

            foreach (var name in required)
                Assert.That(File.Exists(ShaderPropertyParser.MapShaderPath(name)), Is.True,
                    $"Required shader file missing under Shaders/Map/: {name}");

            // Common/ reference template lives OUTSIDE Map/ (included by nobody) — checked explicitly.
            Assert.That(File.Exists(Path.Combine(CommonDir, "LitInput.Template.hlsl")), Is.True,
                "Common/LitInput.Template.hlsl (reference template) must exist.");
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
            // S56 requires per-layer shaders under Shaders/Map/<Layer>/, not the flat Shaders/ root. Asserted
            // on the resolved path so the Lit/Unlit leaf (Map/Fill/Lit/Fill.shader) still counts as "under
            // the Fill layer" — the layer is pinned, the mode subfolder is not.
            Assert.That(ShaderPropertyParser.MapShaderPath("Fill.shader").Replace('\\', '/'),
                Does.Contain("/Map/Fill/"), "Fill.shader must live under Shaders/Map/Fill/ (S56 layout).");
            Assert.That(ShaderPropertyParser.MapShaderPath("Line.shader").Replace('\\', '/'),
                Does.Contain("/Map/Line/"), "Line.shader must live under Shaders/Map/Line/ (S56 layout).");
        }

        [Test]
        public void MapKindRoots_HoldOnlyTheSharedThree()
        {
            // The unlit epic's Lit/Unlit split: the two .shader entry points and their mode-specific .hlsl
            // moved into Lit/ and Unlit/; the kind ROOT (Map/<Kind>/, top level only) must now hold EXACTLY
            // the three genuinely-shared includes — the vertex hook + the two depth passes both twins reuse.
            // This is the structural contract the split created; without this tooth someone could drop an
            // unlit input back into the kind root and nothing would notice. RED-verify by moving any file up
            // one level.
            (string kindDir, string vertex)[] kinds =
            {
                (MapFillDir,      "Fill_VertexModify.hlsl"),
                (MapLineDir,      "Line_VertexExtrude.hlsl"),
                (MapExtrusionDir, "FillExtrusion_VertexModify.hlsl"),
            };
            foreach (var (kindDir, vertex) in kinds)
            {
                string kind = Path.GetFileName(kindDir);
                var expected = new HashSet<string>(StringComparer.Ordinal)
                {
                    vertex, kind + "_DepthOnlyPass.hlsl", kind + "_DepthNormalsPass.hlsl",
                };
                var actual = new HashSet<string>(
                    Directory.EnumerateFiles(kindDir, "*.*", SearchOption.TopDirectoryOnly)
                        .Select(Path.GetFileName)
                        .Where(n => n.EndsWith(".hlsl", StringComparison.Ordinal)
                                 || n.EndsWith(".shader", StringComparison.Ordinal)),
                    StringComparer.Ordinal);

                Assert.That(actual, Is.EquivalentTo(expected),
                    $"Map/{kind}/ (top level) must hold EXACTLY the shared three includes " +
                    $"[{string.Join(", ", expected)}]; found [{string.Join(", ", actual)}]. Lit/Unlit-only " +
                    "files belong in the Lit/ or Unlit/ subfolder, not the kind root.");
            }
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
                "NEVER hand-assembled SurfaceData field-by-field (S34 acceptance tooth #4).");
        }

        [Test]
        public void FillLitGBufferPass_CallsInitializeStandardLitSurfaceData()
        {
            string text = ReadShaderFile("Fill_LitGBufferPass.hlsl");
            Assert.That(text, Does.Contain("InitializeStandardLitSurfaceData("),
                "Fill_LitGBufferPass.hlsl fragment must call InitializeStandardLitSurfaceData — " +
                "NEVER hand-assembled SurfaceData field-by-field (S34 acceptance tooth #4).");
        }

        [Test]
        public void FillLitForwardPass_ModulatesAlbedoAndAlpha_AfterInit()
        {
            string text = ReadShaderFile("Fill_LitForwardPass.hlsl");
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

        [Test]
        public void FillExtrusionVerticalGradient_NotRendered_NoWallDarkeningFold()
        {
            // fill-extrusion-vertical-gradient is deliberately NOT rendered: the maintainer abandoned the
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
            // I5: the material editor must be WIRED, not just authored — the shader's CustomEditor is what
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
                string text = ReadShaderFile(file);
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
            string text = ReadShaderFile("Line_LitForwardPass.hlsl");
            Assert.That(text, Does.Not.Contain("#include \"Line_LitInput.hlsl\""),
                "Line_LitForwardPass.hlsl must NOT self-include Line_LitInput.hlsl — Line.shader provides it (S66 rule).");
            Assert.That(text, Does.Not.Contain("MapLineInput.hlsl"),
                "Line_LitForwardPass.hlsl must not reference old MapLineInput.hlsl (stale after S66 rename).");
        }

        // ── Cross-folder includes from Map/ are self-containment's one sanctioned exception ────

        [Test]
        public void MapLayerFiles_ShareOnlyViaSanctionedInclude()
        {
            // S66: each layer folder is self-contained (no reach into Common/). There are exactly two
            // sanctioned cross-folder `../` reaches, both validated STRUCTURALLY (resolve on disk), not by a
            // string allow-list:
            //   (1) S23 I2a — ../PixelsToWorld.hlsl, the shared px→world at the Map root, reached from each
            //       kind's vertex file; and
            //   (2) the unlit epic's Lit/Unlit split — a .shader / mode-specific .hlsl in Map/<Kind>/{Lit,
            //       Unlit}/ reaches ONE level up to its OWN kind root for the three shared includes
            //       (<Kind>_VertexModify/VertexExtrude, _DepthOnlyPass, _DepthNormalsPass).
            // Every `../` include must resolve to a real file that is EITHER the Map-root PixelsToWorld.hlsl
            // OR still inside the SAME kind's tree. That blocks a reach into Common/, into a SIBLING layer,
            // or outside Map/ — the reaches this test exists to catch — and now also catches a dangling
            // include that resolves to nothing.
            string pixelsToWorld = Path.GetFullPath(Path.Combine(MapDir, "PixelsToWorld.hlsl"));

            // AllDirectories: entry points + mode-specific .hlsl now live in each kind's Lit/ and Unlit/.
            foreach (string kindDir in new[] { MapFillDir, MapLineDir, MapExtrusionDir })
            foreach (string file in Directory.EnumerateFiles(kindDir, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".hlsl", StringComparison.Ordinal)
                         || p.EndsWith(".shader", StringComparison.Ordinal)))
            {
                string text = File.ReadAllText(file, Encoding.UTF8);
                Assert.That(text, Does.Not.Contain("Common/"),
                    $"{Path.GetFileName(file)} must not reach into Common/ (S66: each layer is self-contained).");

                string fileDir = Path.GetDirectoryName(file);
                // #include_with_pragmas is a distinct directive (ShaderLab keyword-conditional include) —
                // matched too, so a cross-folder reach hiding behind it cannot slip the guard.
                foreach (Match m in Regex.Matches(text, "#include(?:_with_pragmas)?\\s+\"(\\.\\./[^\"]*)\""))
                {
                    string target = Path.GetFullPath(Path.Combine(fileDir, m.Groups[1].Value));
                    Assert.That(File.Exists(target), Is.True,
                        $"{Path.GetFileName(file)} includes '{m.Groups[1].Value}', which resolves to a " +
                        $"nonexistent file: {target}");
                    Assert.That(target == pixelsToWorld || IsUnder(target, kindDir), Is.True,
                        $"{Path.GetFileName(file)} has an unsanctioned cross-folder include " +
                        $"'{m.Groups[1].Value}' (→ {target}). A `../` reach may only target ../PixelsToWorld.hlsl " +
                        "(S23 I2a) or a shared include in the SAME kind root (the Lit/Unlit split).");
                }
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

        // ── Shared px→world include (S23 I2a) ─────────────────────────────────

        /// <summary>
        /// <c>MapPixelsToWorld</c> is genuinely shared by every layer that converts a screen-pixel offset to
        /// world metres in the vertex shader. S66 forbade reaching into <c>Common/</c>, so before S23 I2a
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
            Assert.That(File.Exists(sharedPath), Is.True,
                "Shaders/Map/PixelsToWorld.hlsl must exist — the S23 I2a shared px→world include.");
            string sharedText = File.ReadAllText(sharedPath, Encoding.UTF8);
            Assert.That(sharedText, Does.Contain("float MapPixelsToWorld("),
                "Shaders/Map/PixelsToWorld.hlsl must define MapPixelsToWorld — that is the point of the hoist.");

            // Carriers, not a hardcoded count — I2b appends FillExtrusion/FillExtrusion_VertexModify.hlsl.
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
                    $"{file} must include ../PixelsToWorld.hlsl (the sanctioned S23 I2a shared px→world include).");
                Assert.That(text, Does.Not.Contain("float MapPixelsToWorld("),
                    $"{file} must NOT locally re-define MapPixelsToWorld — it is shared via " +
                    "../PixelsToWorld.hlsl now; a local re-definition would silently re-duplicate it.");
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

        // ── Shader declaration names (S56 rename: MapRenderer/<Layer> → Map/<Layer>) ──

        [Test]
        public void Shaders_DeclareMapLayerNames()
        {
            Assert.That(ReadShaderFile("Fill.shader"), Does.Contain("Shader \"Map/Fill\""),
                "Fill.shader must declare Shader \"Map/Fill\" (S56 rename).");
            Assert.That(ReadShaderFile("Line.shader"), Does.Contain("Shader \"Map/Line\""),
                "Line.shader must declare Shader \"Map/Line\" (S56 rename).");
            Assert.That(ReadShaderFile("FillExtrusion.shader"), Does.Contain("Shader \"Map/FillExtrusion\""),
                "FillExtrusion.shader must declare Shader \"Map/FillExtrusion\" (S23 I2b).");
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
            Assert.That(text, Does.Contain("FillExtrusion_LitInput.hlsl"),
                "THIRD-PARTY-NOTICES.txt UCL entry must list the FillExtrusion mirrored files (S23 I2b).");
        }

        [Test]
        public void UnityCompanionLicenseFile_Exists()
        {
            // Resolved by name via AssetDatabase (move-proof): the file lives under
            // Assets/Code/ThirdParty/ today, but its exact folder isn't asserted here.
            string path = ShaderPropertyParser.ResolveAssetPathByName("UnityCompanionLicense.txt");
            Assert.That(File.Exists(path), Is.True,
                "UnityCompanionLicense.txt must exist (committed UCL text, S34 requirement).");
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
}
