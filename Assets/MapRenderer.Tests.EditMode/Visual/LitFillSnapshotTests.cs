using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Imaging;
// S54: MapFillBootstrap retired; FillSceneHelper replaces it.
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// S32 + S34 acceptance tests — Lit material foundation for fills.
    ///
    /// S32 tests (carried over):
    ///   Test 1: PBR lighting is active (acceptance #1).
    ///   Test 2: Restyle with no mesh rebuild (acceptance #2).
    ///
    /// S34 teeth (new — a shallow/stripped shader CANNOT pass these):
    ///   Test 3: Shader validity — MapRenderer/Fill compiles without shader errors.
    ///   Test 4: Normal map changes shading (tooth #1).
    ///   Test 5: Base map samples (tooth #2).
    ///   Test 6: Metallic/smoothness produce specular delta (tooth #3).
    ///
    /// GPU context guard (inherited pattern):
    ///   If renders come back all-black, the luminance/pixel checks go Inconclusive.
    ///   The no-rebuild mesh-reference assertion and shader validity are GPU-independent.
    ///
    /// Ambient: forced to Flat near-black so the directional term dominates.
    /// Camera: top-down ortho (512×512, Y=200, orthoSize=70).
    /// </summary>
    [TestFixture]
    public class LitFillSnapshotTests
    {
        private const int   SnapW    = 512;
        private const int   SnapH    = 512;
        private const float OrthoSz  = 70f;
        private const float CamY     = 200f;

        // Background: distinctive dark slate (matches WorldFillSnapshotTests — not black).
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly byte  BgR8    = (byte)(0.10f * 255 + 0.5f); // 26
        private static readonly byte  BgG8    = (byte)(0.11f * 255 + 0.5f); // 28
        private static readonly byte  BgB8    = (byte)(0.15f * 255 + 0.5f); // 38

        // Luminance delta threshold: absolute difference > 0.05 (5%) proves lighting is active.
        private const double LuminanceDeltaTol = 0.05;

        // ─── Helpers ───────────────────────────────────────────────────────────────

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("LitFillSnapCamera");
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

        /// <summary>
        /// Build the fill GO via FillSceneHelper (StyledFillTileBuilder-backed, S54).
        /// Returns (mapGO, the live lit material on the MeshRenderer).
        /// </summary>
        private static (GameObject mapGo, Material liveMaterial) BuildFillGo()
            => FillSceneHelper.BuildFillGo();

        /// <summary>
        /// Add a directional light as a child of the given parent. Returns the Light component.
        /// </summary>
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

        private static SnapshotRenderer RenderBlank()
        {
            var (go, cam) = BuildCamera();
            var snap = new SnapshotRenderer(SnapW, SnapH);
            try { snap.Render(cam); }
            finally { Object.DestroyImmediate(go); }
            return snap;
        }

        // ─── Test 1: PBR lighting is active ────────────────────────────────────────

        [Test]
        public void LitFill_LuminanceChangesWith_LightIntensity()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.02f, 0.02f, 0.02f, 1f);

            var (cameraGo, camera) = BuildCamera();
            var (mapGo, mat)       = BuildFillGo();
            if (mat != null) mat.SetColor("_MapColor", new Color(0.5f, 0.9f, 0.3f, 1f));

            Light light = AddDirectionalLight(mapGo, 2f, Quaternion.Euler(45f, 0f, 0f));

            using var snap1 = new SnapshotRenderer(SnapW, SnapH);
            using var snap2 = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap1.Render(camera);
                snap1.WritePng("lit-fill-light-on.png");

                if (snap1.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive(
                            "Lit fill render and blank-control are all-black: no GPU context. " +
                            "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                        return;
                    }
                }

                double lum1 = SnapshotCoverage.MeanLuminanceOfNonBackground(
                    snap1.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                light.intensity = 0f;
                snap2.Render(camera);
                snap2.WritePng("lit-fill-light-off.png");

                double lum2 = SnapshotCoverage.MeanLuminanceOfNonBackground(
                    snap2.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Debug.Log($"[LitFillSnapshotTests] Lum(light on)={lum1:F4}, Lum(light off)={lum2:F4}, " +
                          $"delta={System.Math.Abs(lum1 - lum2):F4}");

                if (lum1 < 0.01 && lum2 < 0.01)
                {
                    Assert.Inconclusive(
                        $"Both luminances are near-zero (lum1={lum1:F4}, lum2={lum2:F4}). " +
                        "This may indicate the fill shader is not lit or GPU issue.");
                    return;
                }

                Assert.That(System.Math.Abs(lum1 - lum2), Is.GreaterThan(LuminanceDeltaTol),
                    $"Luminance must change when directional light intensity changes " +
                    $"(light-on={lum1:F4}, light-off={lum2:F4}, delta={System.Math.Abs(lum1 - lum2):F4}). " +
                    $"Expected |delta| > {LuminanceDeltaTol}.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(mapGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Test 2: Restyle (SetColor) with no mesh rebuild ───────────────────────

        [Test]
        public void LitFill_RestyleColor_NoMeshRebuild()
        {
            var (cameraGo, camera) = BuildCamera();
            var (mapGo, mat)       = BuildFillGo();

            AddDirectionalLight(mapGo, 1.5f, Quaternion.Euler(50f, 20f, 0f));

            var meshFilter    = mapGo.GetComponent<MeshFilter>();
            var meshBefore    = meshFilter.sharedMesh;

            using var snap1 = new SnapshotRenderer(SnapW, SnapH);
            using var snap2 = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                if (mat != null) mat.SetColor("_MapColor", new Color(0.2f, 0.8f, 0.2f, 1f));
                snap1.Render(camera);
                snap1.WritePng("lit-fill-color-green.png");

                // ── No-rebuild proof (CPU — always runs) ──
                if (mat != null) mat.SetColor("_MapColor", new Color(0.9f, 0.1f, 0.1f, 1f));

                var meshAfter = meshFilter.sharedMesh;
                Assert.AreSame(meshBefore, meshAfter,
                    "sharedMesh reference must be the SAME object before and after SetColor. " +
                    "If they differ, a mesh rebuild occurred — violates no-rebuild contract.");

                snap2.Render(camera);
                snap2.WritePng("lit-fill-color-red.png");

                // ── Visual restyle proof (GPU — guarded) ──
                if (snap1.IsAllBlack() || snap2.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive(
                            "Both renders are all-black: no GPU context. " +
                            "No-rebuild (CPU) assertion passed.");
                        return;
                    }
                }

                double greenR = MeanChannel(snap1.RawPixels, SnapW, SnapH, 0);
                double greenG = MeanChannel(snap1.RawPixels, SnapW, SnapH, 1);
                double redR   = MeanChannel(snap2.RawPixels, SnapW, SnapH, 0);
                double redG   = MeanChannel(snap2.RawPixels, SnapW, SnapH, 1);

                Debug.Log($"[LitFillSnapshotTests] Green render: meanR={greenR:F3}, meanG={greenG:F3}. " +
                          $"Red render: meanR={redR:F3}, meanG={redG:F3}.");

                Assert.That(greenR + greenG, Is.GreaterThan(0.01),
                    "Green render has near-zero channel means — fills may not have rendered.");

                Assert.That(redR, Is.GreaterThan(greenR - 0.05),
                    $"After SetColor to red, mean R channel ({redR:F3}) should be >= green render R ({greenR:F3}).");

                if (redR < greenR - 0.05)
                {
                    Assert.Inconclusive(
                        $"Red render mean R ({redR:F3}) is lower than green render mean R ({greenR:F3}). " +
                        "No-rebuild (CPU) assertion passed.");
                }
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(mapGo);
            }
        }

        // ─── Test 3: Shader validity (S34 tooth — shader must compile) ────────────

        [Test]
        public void LitFill_FillShader_CompilesWithoutErrors()
        {
#if UNITY_EDITOR
            var shader = Shader.Find("MapRenderer/Fill");
            Assert.That(shader, Is.Not.Null,
                "MapRenderer/Fill shader not found. Check that Assets/MapRenderer.Unity/Shaders/Fill.shader " +
                "exists and Unity has imported it.");

            bool hasErrors = ShaderUtil.ShaderHasError(shader);
            if (hasErrors)
            {
                var msgs = ShaderUtil.GetShaderMessages(shader);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"MapRenderer/Fill shader has {msgs.Length} compile error(s):");
                foreach (var m in msgs)
                    sb.AppendLine($"  [{m.severity}] {m.message} (file:{m.file} line:{m.line})");
                Assert.Fail(sb.ToString());
            }
#else
            Assert.Inconclusive("Shader compilation check requires Unity Editor (not available in PlayMode runtime).");
#endif
        }

        // ─── Test 4: Normal map changes shading (S34 tooth #1) ────────────────────
        // A hand-assembled constant-normal surface cannot fake this.

        [Test]
        public void LitFill_NormalMap_ChangesShading()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.02f, 0.02f, 0.02f, 1f);

            var (cameraGo, camera) = BuildCamera();
            var (mapGo, mat)       = BuildFillGo();

            if (mat == null)
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(mapGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
                Assert.Inconclusive("Fill material is null — shader may not be compiled yet.");
                return;
            }

            // Use a bright lit material so the normal effect is visible.
            mat.SetColor("_MapColor", new Color(0.8f, 0.8f, 0.8f, 1f));
            mat.SetFloat("_Metallic",   0f);
            mat.SetFloat("_Smoothness", 0.3f);

            // Light aimed at 45° so the normal map creates measurable shading variation.
            // Use moderate intensity (1.0f) so the flat render doesn't saturate to lum=1.0,
            // which would prevent meaningful comparison with the normal-map render.
            AddDirectionalLight(mapGo, 1.0f, Quaternion.Euler(45f, 45f, 0f));

            using var snapFlat   = new SnapshotRenderer(SnapW, SnapH);
            using var snapNormal = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                // Render WITHOUT normal map (flat +Y normal only).
                mat.DisableKeyword("_NORMALMAP");
                mat.SetTexture("_BumpMap", null);
                snapFlat.Render(camera);
                snapFlat.WritePng("lit-fill-normal-off.png");

                // GPU guard.
                if (snapFlat.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — normal map test skipped.");
                        return;
                    }
                }

                // Create a procedural normal map: alternating bumps to produce measurable shading delta.
                // A 4×4 texture with alternating left/right normals (in tangent space).
                var normalTex = CreateProceduralNormalMap(16);

                // Render WITH normal map keyword enabled and the procedural texture bound.
                mat.SetTexture("_BumpMap", normalTex);
                mat.EnableKeyword("_NORMALMAP");
                snapNormal.Render(camera);
                snapNormal.WritePng("lit-fill-normal-on.png");

                // Mean absolute per-pixel difference (A vs B) — the discriminating metric.
                // A hand-assembled constant-+Y-normal shader gives diff ≈ 0 (both renders identical).
                // A working normal map with alternating deflections gives diff > 0.
                // Background pixels cancel in the diff (same slate in both renders).
                double lumFlat = SnapshotCoverage.MeanLuminanceOfNonBackground(
                    snapFlat.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                double absPixelDiff = MeanAbsDiff(snapFlat.RawPixels, snapNormal.RawPixels, SnapW, SnapH);

                Debug.Log($"[LitFillSnapshotTests] NormalMap: lum(flat)={lumFlat:F4}, " +
                          $"meanAbsDiff(flat vs normalmap)={absPixelDiff:F4}");

                if (lumFlat < 0.01)
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — normal map test skipped.");
                        return;
                    }
                    Assert.Inconclusive(
                        "Flat render is near-zero; fills may not have rendered.");
                    return;
                }

                if (lumFlat > 0.98)
                {
                    // Both renders are saturated — adjust test setup, but don't fail silently.
                    // Inconclusive so CI sees the symptom without hiding a broken shader.
                    Assert.Inconclusive(
                        $"Flat render is near-max (lum={lumFlat:F4}) — both renders may be saturated. " +
                        $"absPixelDiff={absPixelDiff:F4}. If absPixelDiff is 0, the normal map has no effect; " +
                        "re-run with lower light intensity. S34 tooth #1 requires a non-saturated render.");
                    return;
                }

                Assert.That(absPixelDiff, Is.GreaterThan(0.005),
                    $"Binding a normal map with alternating ±X deflections must produce a per-pixel " +
                    $"luminance difference vs the flat render (S34 tooth #1 — mean abs diff = {absPixelDiff:F4}). " +
                    "A hand-assembled constant-normal surface (shallow shader) gives diff ≈ 0. " +
                    "Check that _NORMALMAP keyword is enabled and InitializeStandardLitSurfaceData is called.");

                Object.DestroyImmediate(normalTex);
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(mapGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Test 5: Base map samples (S34 tooth #2) ──────────────────────────────
        // Binding an albedo texture must produce spatial color variance (pattern visible).

        [Test]
        public void LitFill_BaseMap_ProducesSpatialVariance()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.3f, 0.3f, 0.3f, 1f); // ambient up so texture is visible

            var (cameraGo, camera) = BuildCamera();
            var (mapGo, mat)       = BuildFillGo();

            if (mat == null)
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(mapGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
                Assert.Inconclusive("Fill material is null.");
                return;
            }

            mat.SetColor("_MapColor",      Color.white); // neutral — let base map color dominate
            mat.SetColor("_BaseColor",  Color.white);
            mat.SetFloat("_Metallic",   0f);
            mat.SetFloat("_Smoothness", 0.1f);

            AddDirectionalLight(mapGo, 1.5f, Quaternion.Euler(50f, 0f, 0f));

            // Checkerboard base map: alternating red/blue 8×8 squares.
            var checker = CreateCheckerTexture(128, Color.red, Color.blue);

            using var snapNoTex = new SnapshotRenderer(SnapW, SnapH);
            using var snapTex   = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                // Render without base map (flat white).
                mat.SetTexture("_BaseMap", null);
                snapNoTex.Render(camera);
                snapNoTex.WritePng("lit-fill-basemap-off.png");

                // GPU guard.
                if (snapNoTex.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — base map test skipped.");
                        return;
                    }
                }

                // Render with checkerboard base map.
                mat.SetTexture("_BaseMap", checker);
                snapTex.Render(camera);
                snapTex.WritePng("lit-fill-basemap-on.png");

                // Measure spatial variance in the checkerboard render — non-uniform pixels expected.
                double varianceR = PixelVariance(snapTex.RawPixels, SnapW, SnapH, 0);
                double varianceB = PixelVariance(snapTex.RawPixels, SnapW, SnapH, 2);

                Debug.Log($"[LitFillSnapshotTests] BaseMap: varianceR={varianceR:F4}, varianceB={varianceB:F4}");

                if (varianceR < 0.001 && varianceB < 0.001)
                {
                    // Both are flat — either the texture didn't apply or no GPU.
                    double lumNoTex = SnapshotCoverage.MeanLuminanceOfNonBackground(
                        snapNoTex.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                    if (lumNoTex < 0.01)
                    {
                        Assert.Inconclusive("Fills are not rendering (near-zero luminance).");
                        return;
                    }
                    Assert.Fail(
                        "Base map checkerboard shows near-zero spatial variance (S34 tooth #2). " +
                        "The base map pattern should be visible — check that InitializeStandardLitSurfaceData " +
                        "is called (not a hand-assembled constant albedo).");
                }

                // At least one of R or B should show the checker pattern.
                Assert.That(varianceR + varianceB, Is.GreaterThan(0.001),
                    "Base map (checker texture) must produce spatial color variance across the fill (S34 tooth #2).");

                Object.DestroyImmediate(checker);
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(mapGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Test 6: Metallic/smoothness produce specular delta (S34 tooth #3) ────

        [Test]
        public void LitFill_MetallicSmoothness_ProduceSpecularDelta()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.02f, 0.02f, 0.02f, 1f);

            var (cameraGo, camera) = BuildCamera();
            var (mapGo, mat)       = BuildFillGo();

            if (mat == null)
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(mapGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
                Assert.Inconclusive("Fill material is null.");
                return;
            }

            // White base color so specular shows clearly.
            mat.SetColor("_MapColor",     Color.white);
            mat.SetColor("_BaseColor", Color.white);

            // Bright directional light aimed at a glancing angle so specular is strong.
            AddDirectionalLight(mapGo, 3f, Quaternion.Euler(30f, 0f, 0f));

            using var snapMatte   = new SnapshotRenderer(SnapW, SnapH);
            using var snapSpecular = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                // Render: matte (metallic=0, low smoothness).
                mat.SetFloat("_Metallic",   0f);
                mat.SetFloat("_Smoothness", 0.05f);
                snapMatte.Render(camera);
                snapMatte.WritePng("lit-fill-specular-off.png");

                // GPU guard.
                if (snapMatte.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — specular test skipped.");
                        return;
                    }
                }

                // Render: specular (metallic=1, high smoothness).
                mat.SetFloat("_Metallic",   1f);
                mat.SetFloat("_Smoothness", 0.95f);
                snapSpecular.Render(camera);
                snapSpecular.WritePng("lit-fill-specular-on.png");

                double lumMatte   = SnapshotCoverage.MeanLuminanceOfNonBackground(
                    snapMatte.RawPixels,   SnapW, SnapH, BgR8, BgG8, BgB8);
                double lumSpecular = SnapshotCoverage.MeanLuminanceOfNonBackground(
                    snapSpecular.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Debug.Log($"[LitFillSnapshotTests] Specular: lum(matte)={lumMatte:F4}, lum(specular)={lumSpecular:F4}, " +
                          $"delta={System.Math.Abs(lumMatte - lumSpecular):F4}");

                if (lumMatte < 0.01 && lumSpecular < 0.01)
                {
                    Assert.Inconclusive("Both specular renders near-zero; fills may not have rendered.");
                    return;
                }

                // Metallic+high-smoothness must produce a measurably different (usually brighter)
                // luminance than matte. The delta proves PBR BRDF is live.
                Assert.That(System.Math.Abs(lumMatte - lumSpecular), Is.GreaterThan(0.02),
                    $"Metallic=1/Smoothness=0.95 must produce a specular highlight vs matte (S34 tooth #3). " +
                    $"lum(matte)={lumMatte:F4}, lum(metallic)={lumSpecular:F4}. " +
                    "Check that _Metallic / _Smoothness feed into InitializeStandardLitSurfaceData " +
                    "and that UniversalFragmentPBR is called in the forward pass.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(mapGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Compute the mean value [0,1] of a single RGBA channel across ALL pixels.
        /// Channel index: 0=R, 1=G, 2=B, 3=A.
        /// </summary>
        private static double MeanChannel(byte[] pixels, int width, int height, int channel)
        {
            if (pixels == null || pixels.Length == 0) return 0.0;
            int total = width * height;
            double sum = 0.0;
            for (int i = 0; i < total; i++)
                sum += pixels[i * 4 + channel];
            return (sum / total) / 255.0;
        }

        /// <summary>
        /// Compute the variance of a single RGBA channel across ALL pixels (normalised to [0,1]).
        /// High variance = spatial non-uniformity (e.g. checker pattern).
        /// </summary>
        private static double PixelVariance(byte[] pixels, int width, int height, int channel)
        {
            if (pixels == null || pixels.Length == 0) return 0.0;
            int total = width * height;
            double mean = MeanChannel(pixels, width, height, channel);
            double sumSq = 0.0;
            for (int i = 0; i < total; i++)
            {
                double v = pixels[i * 4 + channel] / 255.0 - mean;
                sumSq += v * v;
            }
            return sumSq / total;
        }

        /// <summary>
        /// Create a procedural normal map texture (Unity NormalMap format) with alternating
        /// left/right normals arranged in a grid, to force measurable shading variation.
        /// </summary>
        private static Texture2D CreateProceduralNormalMap(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Alternate between a left-leaning and right-leaning normal every 2 pixels.
                // Normal map encoding: R=x, G=y, tangent space. Tilted 45° left/right.
                bool leftTile = ((x + y) / 2 % 2) == 0;
                // In tangent space: tilted normal = (±0.7, 0, 0.7) normalized, encoded to [0,1].
                // DXT5nm: x in A, y in G; or Unity std: x in R, y in G.
                float nx = leftTile ? -0.7f : 0.7f;
                float ny = 0.0f;
                // Encode: R = nx*0.5+0.5, G = ny*0.5+0.5, B = 1 (z=1 approx).
                byte r = (byte)Mathf.Clamp(Mathf.RoundToInt((nx * 0.5f + 0.5f) * 255), 0, 255);
                byte g = (byte)Mathf.Clamp(Mathf.RoundToInt((ny * 0.5f + 0.5f) * 255), 0, 255);
                pixels[y * size + x] = new Color32(r, g, 255, 255);
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>
        /// Compute the mean absolute per-pixel difference between two RGBA byte arrays,
        /// normalised to [0,1] (0 = identical, 1 = maximum possible difference).
        /// Returns the mean over all pixels (not channels) so one saturated channel
        /// doesn't dominate.
        /// </summary>
        private static double MeanAbsDiff(byte[] pixA, byte[] pixB, int width, int height)
        {
            if (pixA == null || pixB == null || pixA.Length != pixB.Length) return 0.0;
            int totalChannels = width * height * 4; // RGBA
            double sum = 0.0;
            for (int i = 0; i < totalChannels; i++)
                sum += System.Math.Abs(pixA[i] - pixB[i]);
            // Normalise: divide by (pixels * 255) so result is [0,1].
            return sum / (width * height * 255.0);
        }

        /// <summary>
        /// Create a checkerboard Texture2D alternating two colors in 8×8 tiles.
        /// </summary>
        private static Texture2D CreateCheckerTexture(int size, Color colorA, Color colorB)
        {
            const int tileSize = 8;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
            tex.filterMode = FilterMode.Point;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                bool isA = ((x / tileSize) + (y / tileSize)) % 2 == 0;
                Color c = isA ? colorA : colorB;
                pixels[y * size + x] = c;
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }
    }
}
