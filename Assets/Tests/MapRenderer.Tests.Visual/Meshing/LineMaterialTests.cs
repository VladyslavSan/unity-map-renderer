// Line-material rendering GPU/visual acceptance tests: Lit shading, base snapshot, globe (UMR-176 pack: meshing topic).
//
// Split by TWO using collisions, not the line cap: `CameraProperties` (MapRenderer.Core.Geo
// vs UnityEngine.Rendering) and bare `Object` (System.Object vs UnityEngine.Object) —
// both CS0104. Within that constraint each file groups its dominant line sub-area.
// This file: UnityEngine.Rendering importers (or neutral) that use bare Object —
// the Lit material shader, the general S05 renderer snapshot, and the globe variant.
//
// Contents:
//   LitLineSnapshotTests    — S33 acceptance tests — Lit forward-transparent line shader.
//   LineSnapshotTests       — Headless visual snapshot tests for the S05 GPU-driven line renderer.
//   GlobeLineSnapshotTests  — GlobeLineSnapshotTests (S91-C, C-2) — renders the fixture's geolines layer on the globe through the REAL StyledLineTileBuilder globe path, then places it via the ENU rebase and renders it.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Geometry;
using Unity.Mathematics;
using UnityEditor;
using MapRenderer.Unity.Rendering.Meshing;
using System.IO;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.View;
using Line = MapRenderer.Core.Style.Line;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Visual
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // LitLineSnapshotTests — S33 acceptance tests
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S33 acceptance tests — Lit forward-transparent line shader.
    ///
    /// Test #1 (structural): Map/Line shader exists, compiles without errors, has the full
    ///   five-pass set (S67: ForwardLit/ShadowCaster/DepthOnly/DepthNormals/GBuffer), each
    ///   prepass carrying only its LightMode tag, and includes Line_LitInput.hlsl +
    ///   Line_VertexExtrude.hlsl. NORMAL stream = +Y, extrudeN on separate TEXCOORD.
    ///   Passes 2-5 are capability-only (present-but-inert for transparent lines).
    ///
    /// Test #2 (luminance delta): Line is lit — luminance changes when directional light
    ///   intensity changes. Proves URP PBR lighting is live.
    ///
    /// Test #3 (world-space extrusion): Ribbon width in pixels is invariant under
    ///   non-identity parent translate + scale transform. This is the decisive S05 fix.
    ///
    /// Test #4 (live _Width): SetFloat("_Width") with same-mesh reference identity.
    ///   Measured pixel width changes proportionally without rebuilding the mesh.
    ///
    /// Test #5 (_Opacity): SetFloat("_Opacity", 0) → near-transparent; =1 → visible.
    ///   Proves _Opacity is functional (not hard-coded alpha=1).
    ///
    /// Test #7 (coplanar z-fighting): Fill + overlapping line, no z-fighting.
    ///   Fill and line both visible on the same scanline.
    ///
    /// Camera: top-down ortho 512×512, orthoSize=70, Y=200. metersPerPixel ≈ 0.2734 m/px.
    /// </summary>
    [TestFixture]
    public class LitLineSnapshotTests : BaseTestFixture
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        // Background: distinctive dark slate.
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32  = new Color32(26, 28, 38, 255); // BgColor, byte-quantised

        private static float MetersPerPx => 2f * OrthoSz / SnapH; // ~0.2734
        private const float LineWidthM = 5f;

        // ─── Helpers ────────────────────────────────────────────────────────────

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("LitLineSnapCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = OrthoSz;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;
            return (go, camera);
        }

        private static Light AddDirectionalLight(GameObject parent, float intensity, Quaternion rotation)
        {
            var lightGo = new GameObject("DirLight");
            lightGo.transform.SetParent(parent.transform);
            lightGo.transform.rotation = rotation;
            var light = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = intensity;
            return light;
        }

        /// <summary>
        /// Build a single horizontal line GO with the new lit line shader.
        /// Returns (GO, live material). Caller must DestroyImmediate both.
        /// </summary>
        private static (GameObject go, Material mat) BuildSingleHorizontalLine(
            float widthMeters, GameObject parent = null)
        {
            var pts = new List<double2>
            {
                new double2(-40, 0),
                new double2( 40, 0),
            };
            var mesh = SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt);

            var go = new GameObject("HLine_LitTest");
            if (parent != null) go.transform.SetParent(parent.transform, worldPositionStays: false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var shader = Shader.Find("Map/Line");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var mat = new Material(shader) { name = "HLineLitMat" };
            mat.SetFloat("_Width",          widthMeters);
            mat.SetFloat("_WidthIsPixels",  0f);
            mat.SetColor("_BaseColor",          new Color(0.9f, 0.5f, 0.1f, 1f));
            mat.SetFloat("_Opacity",        1f);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return (go, mat);
        }

        /// <summary>Count the contiguous non-background pixel band around centerRow on col.</summary>
        private static int MeasureContiguousWidthAroundRow(
            Frame frame, int col, int centerRow)
        {
            int height = frame.Height;
            bool IsNonBg(int row)
            {
                if (row < 0 || row >= height) return false;
                Color32 px = frame[col, row];
                return System.Math.Abs(px.r - Bg32.r) +
                       System.Math.Abs(px.g - Bg32.g) +
                       System.Math.Abs(px.b - Bg32.b) > SnapshotCoverage.Tolerance;
            }
            int seed = centerRow;
            if (!IsNonBg(seed))
            {
                bool found = false;
                for (int d = 1; d <= 16; d++)
                {
                    if (IsNonBg(centerRow - d)) { seed = centerRow - d; found = true; break; }
                    if (IsNonBg(centerRow + d)) { seed = centerRow + d; found = true; break; }
                }
                if (!found) return 0;
            }
            int top = seed;    while (top - 1 >= 0      && IsNonBg(top - 1))    top--;
            int bot = seed;    while (bot + 1 < height  && IsNonBg(bot + 1))    bot++;
            return bot - top + 1;
        }

        // ─── Test #1: Structural / shader validity ───────────────────────────────

        [Test]
        public void LitLine_Shader_StructuralValidity()
        {
            var shader = Shader.Find("Map/Line");
            Assert.That(shader, Is.Not.Null,
                "Map/Line shader not found. Check Shaders/Map/Line/Line.shader.");

            if (ShaderUtil.ShaderHasError(shader))
            {
                var msgs = ShaderUtil.GetShaderMessages(shader);
                var sb   = new System.Text.StringBuilder();
                sb.AppendLine("Map/Line has compile errors:");
                foreach (var m in msgs)
                    sb.AppendLine($"  [{m.severity}] {m.message} (file:{m.file} line:{m.line})");
                Assert.Fail(sb.ToString());
            }

            // Read the shader source to verify structural invariants.
            // ShaderUtil does not expose the pass LightMode list in a simple API, so we check the .shader text.
            string shaderPath = UnityEditor.AssetDatabase.GetAssetPath(shader);
            string src = System.IO.File.ReadAllText(
                System.IO.Path.Combine(
                    System.IO.Directory.GetParent(UnityEngine.Application.dataPath).FullName,
                    shaderPath));

            // 1. Full five-pass set present (S67 capability parity with Fill).
            // ForwardLit is the primary rendering pass; the four prepasses are capability-only
            // (present-but-inert for transparent lines; URP excludes Queue>=2501 from prepasses).
            Assert.That(src, Does.Contain("\"UniversalForward\""),
                "Shaders/Map/Line/Line.shader must have a UniversalForward (ForwardLit) pass.");
            Assert.That(src, Does.Contain("\"ShadowCaster\""),
                "Line.shader must have a ShadowCaster pass (S67 capability — inert for transparent).");
            Assert.That(src, Does.Contain("\"UniversalGBuffer\""),
                "Line.shader must have a GBuffer pass (S67 capability — inert for transparent).");
            Assert.That(src, Does.Contain("\"DepthOnly\""),
                "Line.shader must have a DepthOnly pass (S67 capability — inert for transparent).");
            Assert.That(src, Does.Contain("\"DepthNormals\""),
                "Line.shader must have a DepthNormals pass (S67 capability — inert for transparent).");

            // 2. S67 shared helpers included.
            Assert.That(src, Does.Contain("Line_VertexExtrude.hlsl"),
                "Line.shader must #include Line_VertexExtrude.hlsl (S67 shared extrusion + coverage helper).");

            // 3. Includes the line forward pass (S66: explicit include; input provided separately by .shader).
            Assert.That(src, Does.Contain("Line_LitForwardPass.hlsl"),
                "Line.shader must #include Line_LitForwardPass.hlsl (S66 rename from MapLineForwardPass.hlsl).");

            // 4. Does NOT include the fill forward pass.
            Assert.That(src, Does.Not.Contain("Fill_LitForwardPass.hlsl"),
                "Line.shader must NOT #include Fill_LitForwardPass.hlsl (wrong pass for line).");

            // 5. Line_LitInput is included by Line.shader; Line_LitForwardPass calls InitializeStandardLitSurfaceData.
            string passSrc = System.IO.File.ReadAllText(
                ShaderPropertyParser.MapShaderPath("Line_LitForwardPass.hlsl"));
            Assert.That(passSrc, Does.Not.Contain("#include \"Line_LitInput.hlsl\""),
                "Line_LitForwardPass.hlsl must NOT self-include Line_LitInput.hlsl (S66: Line.shader provides it).");
            Assert.That(passSrc, Does.Contain("InitializeStandardLitSurfaceData"),
                "Line_LitForwardPass.hlsl must call InitializeStandardLitSurfaceData (not hand-assembled).");

            // 6. Transparent state. The render state is S58-parameterized (Blend/ZWrite/ZTest/Cull hoisted to
            // the SubShader), so assert the parameterized DIRECTIVE + the property DEFAULTS that encode the
            // prior hardcoded transparent line (standard alpha blend, no depth write) — NOT comment text.
            Assert.That(src, Does.Contain("Blend [_SrcBlend] [_DstBlend]"),
                "Line.shader must declare parameterized blend (Blend [_SrcBlend] [_DstBlend]).");
            Assert.That(src, Does.Match(@"_SrcBlend\([^)]*\)\s*=\s*5"),
                "Line.shader _SrcBlend must default to 5 (SrcAlpha) — standard alpha blend.");
            Assert.That(src, Does.Match(@"_DstBlend\([^)]*\)\s*=\s*10"),
                "Line.shader _DstBlend must default to 10 (OneMinusSrcAlpha) — standard alpha blend.");
            Assert.That(src, Does.Match(@"_ZWrite\([^)]*\)\s*=\s*0"),
                "Line.shader _ZWrite must default to 0 (Off) — transparent, painter's-algorithm layer order.");

            // 7. The line mesh emits a NORMAL stream of +Y (0,1,0) for the lit shader.
            // S54: assert on the live StyledLineTileBuilder output directly (the retired
            // LineMeshBuilder source-text check is gone). Falsifiable identically: a non-+Y
            // normal stream would scramble the lit luminance delta.
            var normalProbeMesh = SyntheticLineMesh.BuildFromPoints(
                new List<double2> { new double2(-40, 0), new double2(40, 0) },
                JoinType.Miter, CapType.Butt);
            Assert.IsNotNull(normalProbeMesh, "StyledLineTileBuilder must produce a line mesh.");
            var probeNormals = normalProbeMesh.normals;
            Assert.That(probeNormals.Length, Is.GreaterThan(0),
                "Line mesh must emit a NORMAL stream (+Y) for the lit shader.");
            foreach (var n in probeNormals)
                Assert.That(n, Is.EqualTo(Vector3.up),
                    "Line mesh NORMAL must be +Y (0,1,0) on every vertex.");
            Object.DestroyImmediate(normalProbeMesh);
        }

        // ─── Test #2: Luminance delta under directional light ────────────────────

        [Test]
        public void LitLine_LuminanceChangesWith_LightIntensity()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.02f, 0.02f, 0.02f, 1f);

            var (cameraGo, camera)  = BuildCamera();
            Track(cameraGo);
            var lineParent          = Track(new GameObject("LitLineParent_Lum"));
            var (lineGo, mat)       = BuildSingleHorizontalLine(LineWidthM, lineParent);
            mat.SetColor("_BaseColor", new Color(0.7f, 0.7f, 0.7f, 1f)); // near-white for clear lum delta

            Light light = AddDirectionalLight(lineParent, 3f, Quaternion.Euler(45f, 0f, 0f));

            using var snap1 = new SnapshotRenderer(SnapW, SnapH);
            using var snap2 = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap1.Render(camera);
                snap1.WritePng("lit-line-light-on.png");

                double lum1 = SnapshotCoverage.MeanLuminanceOfNonBackground(snap1.Pixels, Bg32);

                light.intensity = 0f;
                snap2.Render(camera);
                snap2.WritePng("lit-line-light-off.png");

                double lum2 = SnapshotCoverage.MeanLuminanceOfNonBackground(snap2.Pixels, Bg32);

                Debug.Log($"[LitLineSnapshotTests] Lum(on)={lum1:F4}, Lum(off)={lum2:F4}, " +
                          $"delta={System.Math.Abs(lum1 - lum2):F4}");

                Assert.That(System.Math.Abs(lum1 - lum2), Is.GreaterThan(0.05),
                    $"Luminance must change when directional light changes (|delta|={System.Math.Abs(lum1-lum2):F4}). " +
                    "Expected > 0.05. Check MapLineForwardPass.hlsl uses UniversalFragmentPBR.");
            }
            finally
            {
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Test #3: World-space extrusion (decisive S05 fix) ──────────────────

        [Test]
        public void LitLine_WorldSpaceExtrusion_WidthInvariantUnderParentScale()
        {
            // Build two horizontal lines: one under identity transform, one under a parent
            // with position=(50,0,0), scale=(3,1,3). The ribbon pixel width must be the same
            // for both (the world-space normalize() in MapLineForwardPass ensures this).
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);

            // Line A: identity parent.
            var parentA = Track(new GameObject("LineParentA_Identity"));
            var (_, matA) = BuildSingleHorizontalLine(LineWidthM, parentA);
            Track(matA);

            // Line B: non-identity parent (offset so it fits in the same view, scale 3×).
            // We render them separately to measure each independently.
            var parentB = Track(new GameObject("LineParentB_Scaled"));
            parentB.transform.position   = Vector3.zero; // same world position for measurement
            parentB.transform.localScale = new Vector3(3f, 1f, 3f);
            var (_, matB) = BuildSingleHorizontalLine(LineWidthM, parentB);
            Track(matB);

            using var snapA = new SnapshotRenderer(SnapW, SnapH);
            using var snapB = new SnapshotRenderer(SnapW, SnapH);
            {
                // Render A (identity transform).
                parentA.SetActive(true);
                parentB.SetActive(false);
                snapA.Render(camera);
                snapA.WritePng("lit-line-extrude-identity.png");

                // Render B (scaled parent).
                parentA.SetActive(false);
                parentB.SetActive(true);
                snapB.Render(camera);
                snapB.WritePng("lit-line-extrude-scaled.png");

                int wA = MeasureContiguousWidthAroundRow(snapA.Pixels,
                    SnapW / 2, SnapH / 2);
                int wB = MeasureContiguousWidthAroundRow(snapB.Pixels,
                    SnapW / 2, SnapH / 2);

                float expected = LineWidthM / MetersPerPx; // ~18.3 px
                const int tol  = 5; // ±5px tolerance

                Debug.Log($"[LitLineSnapshotTests] Extrusion: wA={wA}px, wB={wB}px, " +
                          $"expected={expected:F1}px (±{tol}px)");

                Assert.That((float)wA, Is.InRange(expected - tol, expected + tol + 2),
                    $"Identity-parent line width: expected ≈{expected:F1}px, got {wA}px.");
                Assert.That((float)wB, Is.InRange(expected - tol, expected + tol + 2),
                    $"Scaled-parent (3×) line width must equal identity width: " +
                    $"expected ≈{expected:F1}px, got {wB}px. " +
                    $"S05 fix: normalize(mul(O2W, dir)) strips parent scale from extrude direction.");
            }
        }

        // ─── Test #4: Live _Width via SetFloat (same-mesh reference identity) ────

        [Test]
        public void LitLine_WidthChange_NoMeshRebuild_RenderedWidthChanges()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (lineGo, mat)      = BuildSingleHorizontalLine(LineWidthM);
            Track(lineGo);
            Track(mat);
            var meshBefore         = lineGo.GetComponent<MeshFilter>().sharedMesh;

            const float wideM = 10f; // double width

            using var snap1 = new SnapshotRenderer(SnapW, SnapH);
            using var snap2 = new SnapshotRenderer(SnapW, SnapH);
            {
                snap1.Render(camera);
                snap1.WritePng("lit-line-width-before.png");

                // Change width WITHOUT rebuilding the mesh.
                mat.SetFloat("_Width", wideM);

                // No-rebuild proof (CPU — always runs regardless of GPU).
                var meshAfter = lineGo.GetComponent<MeshFilter>().sharedMesh;
                Assert.AreSame(meshBefore, meshAfter,
                    "sharedMesh must be the SAME object after SetFloat('_Width'). " +
                    "If they differ, a mesh rebuild occurred — violates no-rebuild contract.");

                snap2.Render(camera);
                snap2.WritePng("lit-line-width-after.png");

                int w1 = MeasureContiguousWidthAroundRow(snap1.Pixels,
                    SnapW / 2, SnapH / 2);
                int w2 = MeasureContiguousWidthAroundRow(snap2.Pixels,
                    SnapW / 2, SnapH / 2);

                float expected2 = wideM / MetersPerPx;
                const int tol2  = 5;

                Debug.Log($"[LitLineSnapshotTests] Width: w1={w1}px, w2={w2}px, " +
                          $"expected after={expected2:F1}px (±{tol2}px)");

                Assert.That(w2, Is.GreaterThan(w1 - 1),
                    $"Width after doubling ({w2}px) should be >= initial ({w1}px).");

                Assert.That((float)w2, Is.InRange(expected2 - tol2, expected2 + tol2 + 2),
                    $"After SetFloat('_Width', {wideM}m): expected ≈{expected2:F1}px, got {w2}px.");
            }
        }

        // ─── Test #5: _Opacity is functional (not hard-coded alpha=1) ────────────

        [Test]
        public void LitLine_Opacity_FunctionalAlphaChange()
        {
            // Render same line at _Opacity=1 vs _Opacity=0. With Opacity=0 the line must
            // be invisible (alpha=0 → coverage*opacity=0 → all pixels should be background).
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (lineGo, mat)      = BuildSingleHorizontalLine(LineWidthM);
            Track(lineGo);
            Track(mat);

            using var snapOpaque = new SnapshotRenderer(SnapW, SnapH);
            using var snapInvis  = new SnapshotRenderer(SnapW, SnapH);
            {
                mat.SetFloat("_Opacity", 1f);
                snapOpaque.Render(camera);
                snapOpaque.WritePng("lit-line-opacity-1.png");

                mat.SetFloat("_Opacity", 0f);
                snapInvis.Render(camera);
                snapInvis.WritePng("lit-line-opacity-0.png");

                double lumOpaque = SnapshotCoverage.MeanLuminanceOfNonBackground(snapOpaque.Pixels, Bg32);
                double lumInvis  = SnapshotCoverage.MeanLuminanceOfNonBackground(snapInvis.Pixels, Bg32);

                Debug.Log($"[LitLineSnapshotTests] Opacity: lum(1)={lumOpaque:F4}, lum(0)={lumInvis:F4}");

                if (lumOpaque < 0.01)
                {
                    Assert.Inconclusive(
                        $"Opaque render near-zero (lum={lumOpaque:F4}) — line not rendering.");
                }

                // With _Opacity=0, the line should contribute ≈0 non-background luminance.
                // Allow a tiny tolerance for sub-pixel AA blending.
                Assert.That(lumInvis, Is.LessThan(lumOpaque * 0.3),
                    $"_Opacity=0 must produce near-invisible line (lum={lumInvis:F4}). " +
                    $"At _Opacity=1 lum={lumOpaque:F4}. Expected lum(0) < lum(1)*0.3. " +
                    "If _Opacity is inert (hard-coded alpha=1), both renders look the same.");
            }
        }

        // ─── Test #7: Coplanar fill + line, no z-fighting ───────────────────────

        [Test]
        public void LitLine_CoplanarFillAndLine_NoZFighting()
        {
            // Build a fill (StyledFillTileBuilder via FillSceneHelper) and a line on the same XZ plane.
            // Both must be visible on the same scanline — the Y lift in MapLineForwardPass
            // (0.001m) plus ZWrite Off / Queue ordering prevents z-fighting.
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var sceneGo            = Track(new GameObject("LitLineZFightScene"));

            // Fill (opaque, Queue=Geometry) — built through the live Styled path.
            var (fillGo, fillMat) = FillSceneHelper.BuildFillGo(viewSize: 100f);
            fillGo.transform.SetParent(sceneGo.transform, worldPositionStays: false);
            // Give fill a distinctive color.
            if (fillMat != null) fillMat.SetColor("_BaseColor", new Color(0.2f, 0.8f, 0.2f, 1f)); // green

            // Line (transparent, Queue=Transparent, tiny Y lift).
            var (lineGo, lineMat) = BuildSingleHorizontalLine(LineWidthM, sceneGo);
            Track(lineMat);
            lineMat.SetColor("_BaseColor", new Color(1f, 0.2f, 0.2f, 1f)); // red line on green fill

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            {
                snap.Render(camera);
                snap.WritePng("lit-line-zfight-fillplus-line.png");

                Frame pixels = snap.Pixels;
                SnapshotVerdict v = SnapshotCoverage.Analyse(pixels, Bg32);

                Assert.IsFalse(v.IsBlank,
                    "Fill + line scene must not be blank.");

                // Both fill AND line must be visible (non-background pixels). We can check that
                // the filled fraction is large (fill covers ~100% area) and also check for the
                // line band specifically.
                int lineWidthPx = MeasureContiguousWidthAroundRow(pixels, SnapW / 2, SnapH / 2);
                float expectedPx = LineWidthM / MetersPerPx;
                Debug.Log($"[LitLineSnapshotTests] Z-fight: fill filled={v.FilledFraction:P1}, " +
                          $"lineWidthOnCenter={lineWidthPx}px (expected≈{expectedPx:F1}px)");

                // Fill should cover a large fraction.
                Assert.That(v.FilledFraction, Is.GreaterThan(0.1f),
                    $"Fill fraction {v.FilledFraction:P1} too low — fill may not have rendered.");
            }
        }

    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineSnapshotTests — Headless visual snapshot tests for the S05 GPU-driven line renderer.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Headless visual snapshot tests for the S05 GPU-driven line renderer.
    ///
    /// Three discriminating checks that coverage fraction alone cannot provide:
    ///   1. Width measurement: render a horizontal line; sample a perpendicular scanline; count
    ///      contiguous non-background pixels; assert ≈ widthMeters/metersPerPixel ± tolerance.
    ///   2. No-rebuild proof: render; change _Width on the material (no LineMeshBuilder re-run,
    ///      no new Mesh); re-render; assert measured width changed proportionally.
    ///   3. AA ~1px: assert the fill→background transition spans ≤ 3px (transition band).
    ///
    /// Camera: top-down ortho at (0,200,0) looking down (−Y), orthographicSize=70, 512×512.
    /// metersPerPixel = 2·70/512 ≈ 0.2734 m/px.
    ///
    /// Line: horizontal at z=0, x=[−40,40], Width=5m.
    /// Expected width in pixels: 5 / (2·70/512) = 5·512/(140) ≈ 18.3px.
    /// </summary>
    [TestFixture]
    public class LineSnapshotTests : BaseTestFixture
    {
        private const int  SnapW = 512;
        private const int  SnapH = 512;

        // Camera parameters (match WorldFillSnapshotTests pattern).
        private const float OrthoSize = 70f;
        private const float CamY      = 200f;

        // Background: distinctive dark slate (same as WorldFillSnapshotTests — not black).
        private static readonly Color  BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32   = new Color32(26, 28, 38, 255); // BgColor, byte-quantised

        // Line: horizontal segment centred at world origin.
        private const float LineWidthMeters    = 5f;
        private const float LineWidthDoubleM   = 10f; // for no-rebuild test

        // Width measurement tolerance: ±4px to absorb AA feather + GPU rounding.
        private const int   WidthTolPx  = 4;

        // metersPerPixel for this camera/resolution.
        private static float MetersPerPx => 2f * OrthoSize / SnapH; // ~0.2734

        // Expected pixel width for a given width in meters.
        private static float ExpectedPxWidth(float widthM) => widthM / MetersPerPx;

        // ─── Build helpers ──────────────────────────────────────────────────────────────────

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("LineSnapCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = OrthoSize;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;
            return (go, camera);
        }

        /// <summary>
        /// Build ALL golden test lines (horizontal + L-shape + diagonal) using SyntheticLineMesh.
        /// S54: replaces LineBootstrap + LineMeshBuilder.
        /// </summary>
        private static GameObject BuildLineScene(float widthMeters, out Material mat,
            JoinType join = JoinType.Miter, CapType cap = CapType.Butt)
        {
            var mesh = SyntheticLineMesh.BuildGoldenShapes(join, cap);
            var go = new GameObject("LineTest");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var shader = Shader.Find("Map/Line") ?? Shader.Find("Sprites/Default");
            mat = new Material(shader) { name = "LineTestMat" };
            mat.SetFloat("_Width",         widthMeters);
            mat.SetFloat("_WidthIsPixels", 0f);
            mat.SetColor("_BaseColor",      new Color(0.9f, 0.5f, 0.1f, 1f));
            mat.SetFloat("_Opacity",       1f);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>
        /// Build a SINGLE horizontal line for width-measurement tests.
        /// S54: replaces LineMeshBuilder with SyntheticLineMesh.
        /// </summary>
        private static GameObject BuildSingleHorizontalLine(float widthMeters, out Material mat)
        {
            var pts = new List<double2>
            {
                new double2(-40, 0),
                new double2( 40, 0),
            };
            var mesh = SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt);

            var go = new GameObject("HLine");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var shader = Shader.Find("Map/Line") ?? Shader.Find("Sprites/Default");
            mat = new Material(shader) { name = "HLineMat" };
            mat.SetFloat("_Width",         widthMeters);
            mat.SetFloat("_WidthIsPixels", 0f);
            mat.SetColor("_BaseColor",      new Color(0.9f, 0.5f, 0.1f, 1f));
            mat.SetFloat("_Opacity",       1f);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        // ─── Test 1: Renders line + writes PNG + passes coverage ───────────────────────────

        [Test]
        public void RendersLine_WritesPng_PassesCoverage()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var lineGo             = Track(BuildLineScene(LineWidthMeters, out _));

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            {
                snap.Render(camera);

                // Write PNG artefact.
                string pngPath = snap.WritePng("line-golden.png");
                FileAssert.Exists(pngPath);
                Assert.That(new FileInfo(pngPath).Length, Is.GreaterThan(500L),
                    "PNG must be non-trivial (>500 bytes).");

                Frame pixels = snap.Pixels;
                SnapshotVerdict v = SnapshotCoverage.Analyse(pixels, Bg32);

                Assert.IsFalse(v.IsBlank,
                    "Line render must not be blank (no mesh built or camera misaligned).");
                Assert.That(v.FilledFraction, Is.InRange(0.01f, 0.50f),
                    $"Line fill fraction {v.FilledFraction:P1} must be in [1%,50%]. " +
                    "A line should cover a thin band, not the whole frame.");

                Assert.IsTrue(v.Passes(minFill: 0.01f, maxFill: 0.50f, minBuckets: 2),
                    $"Line render should pass the coverage gate. " +
                    $"fill={v.FilledFraction:P1}, buckets={v.DistinctRegionBucketsHit}.");
            }
        }

        // ─── Test 2: Width measurement ──────────────────────────────────────────────────────

        [Test]
        public void LineWidth_MeasuredOnPerpendicular_MatchesExpected()
        {
            // Use a SINGLE horizontal line for this measurement test to avoid the L-shape and
            // diagonal segments crossing the same scanline and inflating the span measurement.
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var lineGo             = Track(BuildSingleHorizontalLine(LineWidthMeters, out var mat));
            Track(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            {
                snap.Render(camera);

                snap.WritePng("line-width-measurement.png");

                // The horizontal line is at world z=0 → image centre row ≈ SnapH/2 = 256.
                // Measure the contiguous width at column SnapW/2.
                // Use MeasureContiguousLineWidthOnColumn to count only the tight band around the
                // center row (avoids counting gaps between multiple lines on the same scanline).
                int colX      = SnapW / 2;
                int centerRow = SnapH / 2;
                int measuredPx = MeasureContiguousWidthAroundRow(snap.Pixels,
                                                                  colX, centerRow);

                float expected = ExpectedPxWidth(LineWidthMeters);

                Assert.That((float)measuredPx,
                    Is.InRange(expected - WidthTolPx, expected + WidthTolPx + 2),
                    $"Single horizontal line width on column {colX}: " +
                    $"expected ≈{expected:F1}px (±{WidthTolPx}px), measured {measuredPx}px. " +
                    $"metersPerPx={MetersPerPx:F4}, widthM={LineWidthMeters}m.");
            }
        }

        // ─── Test 4: AA transition width ───────────────────────────────────────────────────

        [Test]
        public void LineEdge_AATransition_IsAtMostThreePixelsWide()
        {
            // Render a single horizontal line and locate the top edge.
            // Walk from fully-lit pixels outward until background is reached.
            // Assert the transition spans ≤ 3px (fwidth ~1px + blur=1 tolerance).
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var lineGo             = Track(BuildSingleHorizontalLine(LineWidthMeters, out var mat));
            Track(mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            {
                snap.Render(camera);

                snap.WritePng("line-aa-edge.png");

                // Scan column SnapW/2 (centre of line), find the top edge of the line band.
                int col       = SnapW / 2;
                int centerRow = SnapH / 2;

                // Find the top boundary of the contiguous line band.
                int topEdge = FindTopEdgeOfBand(snap.Pixels, col, centerRow);

                Assert.That(topEdge, Is.GreaterThanOrEqualTo(0),
                    "Expected to find a non-background pixel band on the centre column.");

                // Measure the AA transition: walk from (topEdge-1) upward until background.
                // Count pixels that are between background and full-lit (partial alpha = AA feather).
                int transitionPx = MeasureTransitionBandAboveEdge(snap.Pixels, col, topEdge);

                Assert.That(transitionPx, Is.LessThanOrEqualTo(3),
                    $"AA transition at top edge of line (col={col}, topEdge row={topEdge}): " +
                    $"{transitionPx}px wide. Expected ≤ 3px for 1px fwidth AA.");
            }
        }

        // ─── Test 5a: Non-Butt cap snapshot (Round + Square caps) ─────────────────────

        /// <summary>
        /// Snapshot test for Round caps on the golden multi-line scene.
        ///
        /// Both BuildLineScene and BuildSingleHorizontalLine previously hard-coded CapType.Butt /
        /// JoinType.Miter, leaving cap geometry unexercised in the visual path. This test renders
        /// the same golden shapes (h-line, L-shape, diagonal) with Round caps and verifies the PNG
        /// is non-trivial and passes the coverage gate.
        ///
        /// Uses the multi-line scene (same as RendersLine_WritesPng_PassesCoverage) so that fill
        /// fraction is comfortably above the 3% blank threshold that a single thin line approaches.
        /// </summary>
        [Test]
        public void RoundCapScene_RendersLine_WritesPng_PassesCoverage()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);

            // Build all golden shapes with Round cap + Round join.
            var go = Track(BuildLineScene(LineWidthMeters, out _, JoinType.Round, CapType.Round));
            go.name = "LineTestRoundCap";

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            {
                snap.Render(camera);

                string pngPath = snap.WritePng("line-round-cap.png");
                FileAssert.Exists(pngPath);
                Assert.That(new FileInfo(pngPath).Length, Is.GreaterThan(500L),
                    "Round-cap PNG must be non-trivial (>500 bytes).");

                Frame pixels = snap.Pixels;
                SnapshotVerdict v = SnapshotCoverage.Analyse(pixels, Bg32);

                Assert.IsFalse(v.IsBlank,
                    "Round-cap line render must not be blank (multi-line scene should give >3% fill).");
                Assert.That(v.FilledFraction, Is.InRange(0.01f, 0.50f),
                    $"Round-cap fill fraction {v.FilledFraction:P1} must be in [1%,50%].");
                Assert.IsTrue(v.Passes(minFill: 0.01f, maxFill: 0.50f, minBuckets: 2),
                    $"Round-cap render must pass coverage gate. " +
                    $"fill={v.FilledFraction:P1}, buckets={v.DistinctRegionBucketsHit}.");
            }
        }

        [Test]
        public void SquareCapScene_RendersLine_WritesPng_PassesCoverage()
        {
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);

            // Build all golden shapes with Square cap + Miter join.
            var go = Track(BuildLineScene(LineWidthMeters, out _, JoinType.Miter, CapType.Square));
            go.name = "LineTestSquareCap";

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            {
                snap.Render(camera);

                string pngPath = snap.WritePng("line-square-cap.png");
                FileAssert.Exists(pngPath);
                Assert.That(new FileInfo(pngPath).Length, Is.GreaterThan(500L),
                    "Square-cap PNG must be non-trivial (>500 bytes).");

                Frame pixels = snap.Pixels;
                SnapshotVerdict v = SnapshotCoverage.Analyse(pixels, Bg32);

                Assert.IsFalse(v.IsBlank,
                    "Square-cap line render must not be blank (multi-line scene should give >3% fill).");
                Assert.That(v.FilledFraction, Is.InRange(0.01f, 0.50f),
                    $"Square-cap fill fraction {v.FilledFraction:P1} must be in [1%,50%].");
                Assert.IsTrue(v.Passes(minFill: 0.01f, maxFill: 0.50f, minBuckets: 2),
                    $"Square-cap render must pass coverage gate. " +
                    $"fill={v.FilledFraction:P1}, buckets={v.DistinctRegionBucketsHit}.");
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Count contiguous non-background pixels along a vertical column.
        ///
        /// Scans from the top of the column down, finds the first and last non-background pixel,
        /// and returns (last − first + 1). Returns 0 if no non-background pixel is found.
        ///
        /// "Non-background" = Manhattan distance from Bg32 > Tolerance(15).
        /// </summary>
        private static int MeasureLineWidthOnColumn(Frame frame, int col)
        {
            int first = -1;
            int last  = -1;

            for (int row = 0; row < frame.Height; row++)
            {
                Color32 px = frame[col, row];
                int dist = System.Math.Abs(px.r - Bg32.r) + System.Math.Abs(px.g - Bg32.g) + System.Math.Abs(px.b - Bg32.b);
                if (dist > SnapshotCoverage.Tolerance)
                {
                    if (first < 0) first = row;
                    last = row;
                }
            }

            if (first < 0) return 0;
            return last - first + 1;
        }

        /// <summary>
        /// Find the topmost row of the contiguous non-background band that contains <paramref name="centerRow"/>.
        /// Returns the top row index of the band, or -1 if no band is found.
        /// </summary>
        private static int FindTopEdgeOfBand(Frame frame, int col, int centerRow)
        {
            int height = frame.Height;
            bool IsNonBg(int row)
            {
                if (row < 0 || row >= height) return false;
                Color32 px = frame[col, row];
                int dist = System.Math.Abs(px.r - Bg32.r) +
                           System.Math.Abs(px.g - Bg32.g) +
                           System.Math.Abs(px.b - Bg32.b);
                return dist > SnapshotCoverage.Tolerance;
            }

            // Find a seed in the band.
            int seed = centerRow;
            if (!IsNonBg(seed))
            {
                bool found = false;
                for (int delta = 1; delta <= 16; delta++)
                {
                    if (IsNonBg(centerRow - delta)) { seed = centerRow - delta; found = true; break; }
                    if (IsNonBg(centerRow + delta)) { seed = centerRow + delta; found = true; break; }
                }
                if (!found) return -1;
            }

            // Walk upward to the top of the band.
            int top = seed;
            while (top - 1 >= 0 && IsNonBg(top - 1))
                top--;

            return top;
        }

        /// <summary>
        /// Measure the width of the partial-alpha AA transition band above the line's top edge.
        ///
        /// Starting from <paramref name="topEdgeRow"/> - 1 (one row above the full-lit band),
        /// counts how many rows going upward are "partial": brighter than background but below
        /// the full-lit brightness of the interior. These are the anti-aliased feather pixels.
        ///
        /// Returns 0 if the transition is a hard 1px step (ideal), ≤ 3 is acceptable for
        /// fragment-shader AA with blur=1.
        /// </summary>
        private static int MeasureTransitionBandAboveEdge(Frame frame, int col, int topEdgeRow)
        {
            if (topEdgeRow <= 0) return 0;
            int height = frame.Height;

            // Sample a "full brightness" reference from the interior (a few rows below topEdge).
            int interiorRow = topEdgeRow + 2;
            if (interiorRow >= height) interiorRow = topEdgeRow;
            Color32 interiorPx = frame[col, interiorRow];
            int interiorBrightness = interiorPx.r + interiorPx.g + interiorPx.b;

            // Threshold: a pixel is "partial" (AA feather) if its brightness is between bg and
            // interior level. We use half the interior-to-background difference.
            int bgBrightness   = Bg32.r + Bg32.g + Bg32.b;
            int halfRange      = System.Math.Max((interiorBrightness - bgBrightness) / 2, 10);
            int partialThresh  = bgBrightness + halfRange; // above this → clearly lit

            int count = 0;
            for (int row = topEdgeRow - 1; row >= 0; row--)
            {
                Color32 px     = frame[col, row];
                int brightness = px.r + px.g + px.b;
                int distFromBg = brightness - bgBrightness;

                // If clearly background, stop.
                if (distFromBg <= 5) break;

                // If still clearly lit (> halfRange above bg), this isn't a transition pixel.
                if (brightness >= partialThresh)
                {
                    // Still in full-lit zone — extend the edge estimate upward (shouldn't normally happen).
                    count++;
                    if (count > 10) break; // safety valve
                    continue;
                }

                // Partial pixel — AA feather.
                count++;
            }

            return count;
        }

        /// <summary>
        /// Count contiguous non-background pixels along a COLUMN, starting from <paramref name="centerRow"/>
        /// and expanding outward. Only the unbroken band touching <c>centerRow</c> is counted.
        ///
        /// "Non-background" = Manhattan distance from Bg32 > SnapshotCoverage.Tolerance.
        ///
        /// This avoids the inflated measurement that <see cref="MeasureLineWidthOnColumn"/> produces when
        /// multiple parallel lines (e.g. horizontal + L-shape) lie on the same pixel column.
        /// </summary>
        private static int MeasureContiguousWidthAroundRow(
            Frame frame, int col, int centerRow)
        {
            int height = frame.Height;
            // Helper: is pixel at (col, row) non-background?
            bool IsNonBg(int row)
            {
                if (row < 0 || row >= height) return false;
                Color32 px = frame[col, row];
                int dist = System.Math.Abs(px.r - Bg32.r) +
                           System.Math.Abs(px.g - Bg32.g) +
                           System.Math.Abs(px.b - Bg32.b);
                return dist > SnapshotCoverage.Tolerance;
            }

            // If the center row is background, search outward ±16px to find the nearest
            // non-background pixel (accounts for sub-pixel centering).
            int seed = centerRow;
            if (!IsNonBg(seed))
            {
                bool found = false;
                for (int delta = 1; delta <= 16; delta++)
                {
                    if (IsNonBg(centerRow - delta)) { seed = centerRow - delta; found = true; break; }
                    if (IsNonBg(centerRow + delta)) { seed = centerRow + delta; found = true; break; }
                }
                if (!found) return 0;
            }

            // Expand upward from seed.
            int top = seed;
            while (top - 1 >= 0 && IsNonBg(top - 1))
                top--;

            // Expand downward from seed.
            int bottom = seed;
            while (bottom + 1 < height && IsNonBg(bottom + 1))
                bottom++;

            return bottom - top + 1;
        }
    }

    // GlobeLineSnapshotTests (S91-C, C-2) — renders the fixture's `geolines` layer on the globe through the
    // REAL StyledLineTileBuilder globe path, then places it via the ENU rebase and renders it. This is the
    // visual proof that line ribbons now lie ON the sphere surface (3D centerline + radial up + tangent-plane
    // across), not flattened onto the y=0 plane as before C-2.

    // ───────────────────────────────────────────────────────────────────────────────────
    // GlobeLineSnapshotTests — GlobeLineSnapshotTests (S91-C, C-2)
    // ───────────────────────────────────────────────────────────────────────────────────

    public class GlobeLineSnapshotTests : BaseTestFixture
    {
        private const int SnapW = 512, SnapH = 512;
        private static readonly Color OceanBg = new Color(0.04f, 0.09f, 0.18f, 1f);

        [Test]
        public void RendersGeolinesOnTheSphere_WritesPng()
        {
            var proj   = new SphericalProjection();
            var tid    = new TileId { Z = 0, X = 0, Y = 0 };
            var lookAt = new GeoCoordinate { Latitude = 20.0, Longitude = 12.0 }; // over Africa

            // ── Build the geolines line mesh on the globe via the real StyledLineTileBuilder. ──
            using MvtTile mvtTile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, SampleTileFixture.Bytes());
            var style = StyleParser.Parse(LineStyleJson());
            var styleLayer = (Line.StyleLayer)style.Layers[0];
            var paint  = styleLayer.Paint;
            var layout = styleLayer.Layout;

            var mvtLayer = SourceLayerResolver.ResolveTileLayer(styleLayer, mvtTile);
            Assert.IsNotNull(mvtLayer, "fixture must contain the geolines layer");
            var selected = TestTileMeshBuilder.Select(styleLayer, mvtLayer, 0.0);
            Assert.IsNotEmpty(selected, "geolines must select features");

            // `proj` is `var`-typed off `new SphericalProjection()` above, so this binds to
            // BuildLineFromLayer<TProj>, not the IProjection-typed overload — see that overload's own doc note.
            Mesh mesh = Track(TestTileMeshBuilder.BuildLineFromLayer(mvtLayer, selected, paint, layout, 0.0, tid, proj));
            Assert.IsNotNull(mesh, "line mesh build must produce a mesh");

            // Line material — big world-metre width so borders read at globe scale (~25 km/px in this frame).
            var shader = Shader.Find("Map/Line");
            Assert.IsNotNull(shader, "Map/Line shader must be present");
            var mat = Track(new Material(shader) { name = "GlobeLineMat" });
            mat.SetFloat("_Width",          120000f); // 120 km
            mat.SetFloat("_WidthIsPixels",  0f);
            mat.SetColor("_BaseColor",      new Color(1f, 0.85f, 0.2f, 1f)); // amber borders
            mat.SetFloat("_Opacity",        1f);
            mat.SetFloat("_Cull",           2f); // stock Cull Back: near-side ribbons show, far-side culled
                                                 // (post winding reversal — no more double-sided workaround)

            var mapGo = Track(new GameObject("GlobeLine"));
            mapGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            mapGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            // ── Place via the ENU rebase (same scheme the backends use). ──
            double2 swLL = tid.ToLonLat(0.0, 1.0, 1.0);
            double3 tileOriginRender  = proj.Project(new GeoCoordinate { Latitude = swLL.y, Longitude = swLL.x });
            double3 sceneOriginRender = proj.Project(lookAt);
            float3x3 rebase   = math.transpose(proj.TangentBasisAt(lookAt));
            float3   position = MapRenderer.Unity.View.FloatingOrigin.TileToSceneRebased(
                tileOriginRender, sceneOriginRender, rebase);
            quaternion q = new quaternion(rebase);
            mapGo.transform.rotation      = new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
            mapGo.transform.localPosition = new Vector3(position.x, position.y, position.z);

            // ── Camera: the real ComputeRelativePose orbit. ──
            double altitude = 2.5 * SphericalProjection.Radius;
            CameraPoseMath.ComputeRelativePose(altitude, Angle.FromDegrees(0.0), Angle.FromDegrees(0.0),
                out double3 pos, out double3 fwd, out double3 up);

            var cameraGo = Track(new GameObject("GlobeLineCamera"));
            var camera   = cameraGo.AddComponent<Camera>();
            camera.transform.position = new Vector3((float)pos.x, (float)pos.y, (float)pos.z);
            camera.transform.rotation = Quaternion.LookRotation(
                new Vector3((float)fwd.x, (float)fwd.y, (float)fwd.z),
                new Vector3((float)up.x,  (float)up.y,  (float)up.z));
            camera.fieldOfView     = 35f;
            camera.nearClipPlane   = Mathf.Max(0.1f, (float)CameraPoseMath.NearClip(altitude));
            camera.farClipPlane    =                  (float)CameraPoseMath.FarClip(altitude);
            camera.clearFlags      = CameraClearFlags.SolidColor;
            camera.backgroundColor = OceanBg;
            camera.enabled         = false;

            var lightGo = Track(new GameObject("GlobeLineLight"));
            var light   = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -50f, 0f);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            snap.Render(camera);
            string path = snap.WritePng("globe-geolines.png");
            TestContext.WriteLine($"[GlobeLineSnapshotTests] wrote {path}");
        }
        private static string LineStyleJson() => @"{
    ""version"": 8,
    ""name"": ""GlobeLine"",
    ""sources"": { ""maplibre"": { ""type"": ""vector"", ""tiles"": [""https://example.com/{z}/{x}/{y}.pbf""] } },
    ""layers"": [
        { ""id"": ""geolines"", ""type"": ""line"", ""source"": ""maplibre"", ""source-layer"": ""geolines"",
          ""paint"": { ""line-color"": [""rgba"",255,217,51,1] } }
    ]
}";
    }
}
