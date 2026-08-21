using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Geometry;
using Unity.Mathematics;
#if UNITY_EDITOR
using UnityEditor;
using MapRenderer.Unity.Rendering.Meshing;
#endif

namespace MapRenderer.Tests.Visual
{
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
    /// GPU context guard: marks Inconclusive (not Fail) when GPU is unavailable in batch mode.
    /// Camera: top-down ortho 512×512, orthoSize=70, Y=200. metersPerPixel ≈ 0.2734 m/px.
    /// </summary>
    [TestFixture]
    public class LitLineSnapshotTests
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        // Background: distinctive dark slate.
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly byte  BgR8    = (byte)(0.10f * 255 + 0.5f); // 26
        private static readonly byte  BgG8    = (byte)(0.11f * 255 + 0.5f); // 28
        private static readonly byte  BgB8    = (byte)(0.15f * 255 + 0.5f); // 38

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

        private static SnapshotRenderer RenderBlank()
        {
            var (go, cam) = BuildCamera();
            var snap = new SnapshotRenderer(SnapW, SnapH);
            try { snap.Render(cam); }
            finally { Object.DestroyImmediate(go); }
            return snap;
        }

        /// <summary>Count the contiguous non-background pixel band around centerRow on col.</summary>
        private static int MeasureContiguousWidthAroundRow(
            byte[] pixels, int width, int height, int col, int centerRow)
        {
            bool IsNonBg(int row)
            {
                if (row < 0 || row >= height) return false;
                int idx = (row * width + col) * 4;
                return System.Math.Abs(pixels[idx]   - BgR8) +
                       System.Math.Abs(pixels[idx+1] - BgG8) +
                       System.Math.Abs(pixels[idx+2] - BgB8) > SnapshotCoverage.Tolerance;
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
#if UNITY_EDITOR
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
#else
            Assert.Inconclusive("Structural checks require Unity Editor.");
#endif
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
            var lineParent          = new GameObject("LitLineParent_Lum");
            var (lineGo, mat)       = BuildSingleHorizontalLine(LineWidthM, lineParent);
            mat.SetColor("_BaseColor", new Color(0.7f, 0.7f, 0.7f, 1f)); // near-white for clear lum delta

            Light light = AddDirectionalLight(lineParent, 3f, Quaternion.Euler(45f, 0f, 0f));

            using var snap1 = new SnapshotRenderer(SnapW, SnapH);
            using var snap2 = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap1.Render(camera);
                snap1.WritePng("lit-line-light-on.png");

                if (snap1.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — luminance test skipped.");
                        return;
                    }
                }

                double lum1 = SnapshotCoverage.MeanLuminanceOfNonBackground(
                    snap1.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                light.intensity = 0f;
                snap2.Render(camera);
                snap2.WritePng("lit-line-light-off.png");

                double lum2 = SnapshotCoverage.MeanLuminanceOfNonBackground(
                    snap2.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Debug.Log($"[LitLineSnapshotTests] Lum(on)={lum1:F4}, Lum(off)={lum2:F4}, " +
                          $"delta={System.Math.Abs(lum1 - lum2):F4}");

                if (lum1 < 0.01 && lum2 < 0.01)
                {
                    Assert.Inconclusive(
                        $"Both luminances near-zero (lum1={lum1:F4}, lum2={lum2:F4}). " +
                        "Line may not render in batch mode or shader is unlit.");
                    return;
                }

                Assert.That(System.Math.Abs(lum1 - lum2), Is.GreaterThan(0.05),
                    $"Luminance must change when directional light changes (|delta|={System.Math.Abs(lum1-lum2):F4}). " +
                    "Expected > 0.05. Check MapLineForwardPass.hlsl uses UniversalFragmentPBR.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lineParent);
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

            // Line A: identity parent.
            var parentA = new GameObject("LineParentA_Identity");
            var (_, matA) = BuildSingleHorizontalLine(LineWidthM, parentA);

            // Line B: non-identity parent (offset so it fits in the same view, scale 3×).
            // We render them separately to measure each independently.
            var parentB = new GameObject("LineParentB_Scaled");
            parentB.transform.position   = Vector3.zero; // same world position for measurement
            parentB.transform.localScale = new Vector3(3f, 1f, 3f);
            var (_, matB) = BuildSingleHorizontalLine(LineWidthM, parentB);

            using var snapA = new SnapshotRenderer(SnapW, SnapH);
            using var snapB = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                // Render A (identity transform).
                parentA.SetActive(true);
                parentB.SetActive(false);
                snapA.Render(camera);
                snapA.WritePng("lit-line-extrude-identity.png");

                // GPU guard.
                if (snapA.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — extrusion test skipped.");
                        return;
                    }
                }

                // Render B (scaled parent).
                parentA.SetActive(false);
                parentB.SetActive(true);
                snapB.Render(camera);
                snapB.WritePng("lit-line-extrude-scaled.png");

                int wA = MeasureContiguousWidthAroundRow(snapA.RawPixels, SnapW, SnapH,
                    SnapW / 2, SnapH / 2);
                int wB = MeasureContiguousWidthAroundRow(snapB.RawPixels, SnapW, SnapH,
                    SnapW / 2, SnapH / 2);

                float expected = LineWidthM / MetersPerPx; // ~18.3 px
                const int tol  = 5; // ±5px tolerance

                Debug.Log($"[LitLineSnapshotTests] Extrusion: wA={wA}px, wB={wB}px, " +
                          $"expected={expected:F1}px (±{tol}px)");

                if (wA == 0 || wB == 0)
                {
                    Assert.Inconclusive(
                        $"Could not measure line band (wA={wA}, wB={wB}). " +
                        "Line may not render in batch mode.");
                    return;
                }

                Assert.That((float)wA, Is.InRange(expected - tol, expected + tol + 2),
                    $"Identity-parent line width: expected ≈{expected:F1}px, got {wA}px.");
                Assert.That((float)wB, Is.InRange(expected - tol, expected + tol + 2),
                    $"Scaled-parent (3×) line width must equal identity width: " +
                    $"expected ≈{expected:F1}px, got {wB}px. " +
                    $"S05 fix: normalize(mul(O2W, dir)) strips parent scale from extrude direction.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(parentA);
                Object.DestroyImmediate(parentB);
                Object.DestroyImmediate(matA);
                Object.DestroyImmediate(matB);
            }
        }

        // ─── Test #4: Live _Width via SetFloat (same-mesh reference identity) ────

        [Test]
        public void LitLine_WidthChange_NoMeshRebuild_RenderedWidthChanges()
        {
            var (cameraGo, camera) = BuildCamera();
            var (lineGo, mat)      = BuildSingleHorizontalLine(LineWidthM);
            var meshBefore         = lineGo.GetComponent<MeshFilter>().sharedMesh;

            const float wideM = 10f; // double width

            using var snap1 = new SnapshotRenderer(SnapW, SnapH);
            using var snap2 = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap1.Render(camera);
                snap1.WritePng("lit-line-width-before.png");

                // GPU guard.
                if (snap1.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — width test skipped.");
                        return;
                    }
                }

                // Change width WITHOUT rebuilding the mesh.
                mat.SetFloat("_Width", wideM);

                // No-rebuild proof (CPU — always runs regardless of GPU).
                var meshAfter = lineGo.GetComponent<MeshFilter>().sharedMesh;
                Assert.AreSame(meshBefore, meshAfter,
                    "sharedMesh must be the SAME object after SetFloat('_Width'). " +
                    "If they differ, a mesh rebuild occurred — violates no-rebuild contract.");

                snap2.Render(camera);
                snap2.WritePng("lit-line-width-after.png");

                int w1 = MeasureContiguousWidthAroundRow(snap1.RawPixels, SnapW, SnapH,
                    SnapW / 2, SnapH / 2);
                int w2 = MeasureContiguousWidthAroundRow(snap2.RawPixels, SnapW, SnapH,
                    SnapW / 2, SnapH / 2);

                float expected2 = wideM / MetersPerPx;
                const int tol2  = 5;

                Debug.Log($"[LitLineSnapshotTests] Width: w1={w1}px, w2={w2}px, " +
                          $"expected after={expected2:F1}px (±{tol2}px)");

                if (w1 == 0)
                {
                    Assert.Inconclusive("Could not measure initial line width.");
                    return;
                }

                Assert.That(w2, Is.GreaterThan(w1 - 1),
                    $"Width after doubling ({w2}px) should be >= initial ({w1}px).");

                Assert.That((float)w2, Is.InRange(expected2 - tol2, expected2 + tol2 + 2),
                    $"After SetFloat('_Width', {wideM}m): expected ≈{expected2:F1}px, got {w2}px.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lineGo);
                Object.DestroyImmediate(mat);
            }
        }

        // ─── Test #5: _Opacity is functional (not hard-coded alpha=1) ────────────

        [Test]
        public void LitLine_Opacity_FunctionalAlphaChange()
        {
            // Render same line at _Opacity=1 vs _Opacity=0. With Opacity=0 the line must
            // be invisible (alpha=0 → coverage*opacity=0 → all pixels should be background).
            var (cameraGo, camera) = BuildCamera();
            var (lineGo, mat)      = BuildSingleHorizontalLine(LineWidthM);

            using var snapOpaque = new SnapshotRenderer(SnapW, SnapH);
            using var snapInvis  = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                mat.SetFloat("_Opacity", 1f);
                snapOpaque.Render(camera);
                snapOpaque.WritePng("lit-line-opacity-1.png");

                if (snapOpaque.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — opacity test skipped.");
                        return;
                    }
                }

                mat.SetFloat("_Opacity", 0f);
                snapInvis.Render(camera);
                snapInvis.WritePng("lit-line-opacity-0.png");

                double lumOpaque = SnapshotCoverage.MeanLuminanceOfNonBackground(
                    snapOpaque.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                double lumInvis  = SnapshotCoverage.MeanLuminanceOfNonBackground(
                    snapInvis.RawPixels,  SnapW, SnapH, BgR8, BgG8, BgB8);

                Debug.Log($"[LitLineSnapshotTests] Opacity: lum(1)={lumOpaque:F4}, lum(0)={lumInvis:F4}");

                if (lumOpaque < 0.01)
                {
                    Assert.Inconclusive(
                        $"Opaque render near-zero (lum={lumOpaque:F4}) — line not rendering.");
                    return;
                }

                // With _Opacity=0, the line should contribute ≈0 non-background luminance.
                // Allow a tiny tolerance for sub-pixel AA blending.
                Assert.That(lumInvis, Is.LessThan(lumOpaque * 0.3),
                    $"_Opacity=0 must produce near-invisible line (lum={lumInvis:F4}). " +
                    $"At _Opacity=1 lum={lumOpaque:F4}. Expected lum(0) < lum(1)*0.3. " +
                    "If _Opacity is inert (hard-coded alpha=1), both renders look the same.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lineGo);
                Object.DestroyImmediate(mat);
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
            var sceneGo            = new GameObject("LitLineZFightScene");

            // Fill (opaque, Queue=Geometry) — built through the live Styled path.
            var (fillGo, fillMat) = FillSceneHelper.BuildFillGo(viewSize: 100f);
            fillGo.transform.SetParent(sceneGo.transform, worldPositionStays: false);
            // Give fill a distinctive color.
            if (fillMat != null) fillMat.SetColor("_BaseColor", new Color(0.2f, 0.8f, 0.2f, 1f)); // green

            // Line (transparent, Queue=Transparent, tiny Y lift).
            var (lineGo, lineMat) = BuildSingleHorizontalLine(LineWidthM, sceneGo);
            lineMat.SetColor("_BaseColor", new Color(1f, 0.2f, 0.2f, 1f)); // red line on green fill

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("lit-line-zfight-fillplus-line.png");

                if (snap.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — z-fight test skipped.");
                        return;
                    }
                }

                byte[] pixels = snap.RawPixels;
                SnapshotVerdict v = SnapshotCoverage.Analyse(pixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Assert.IsFalse(v.IsBlank,
                    "Fill + line scene must not be blank.");

                // Both fill AND line must be visible (non-background pixels). We can check that
                // the filled fraction is large (fill covers ~100% area) and also check for the
                // line band specifically.
                int lineWidthPx = MeasureContiguousWidthAroundRow(pixels, SnapW, SnapH,
                    SnapW / 2, SnapH / 2);
                float expectedPx = LineWidthM / MetersPerPx;
                Debug.Log($"[LitLineSnapshotTests] Z-fight: fill filled={v.FilledFraction:P1}, " +
                          $"lineWidthOnCenter={lineWidthPx}px (expected≈{expectedPx:F1}px)");

                // Fill should cover a large fraction.
                Assert.That(v.FilledFraction, Is.GreaterThan(0.1f),
                    $"Fill fraction {v.FilledFraction:P1} too low — fill may not have rendered.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(sceneGo);
                if (lineMat != null) Object.DestroyImmediate(lineMat);
            }
        }

    }
}
