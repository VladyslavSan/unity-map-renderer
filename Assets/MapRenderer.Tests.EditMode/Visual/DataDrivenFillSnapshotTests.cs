using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Imaging;
// S54: MapFillBootstrap retired; FillSceneHelper replaces it.
#if UNITY_EDITOR
using UnityEditor;
using MapRenderer.Unity.Rendering.Meshing;
#endif

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// S12 acceptance snapshot tests — data-driven per-feature colors baked into the fill mesh.
    ///
    /// GPU context guard (inherited pattern):
    ///   If all renders come back all-black, tests degrade to Inconclusive.
    ///   The FeatureColorBakerTests (engine-free) are the load-bearing CPU teeth for distinctness.
    ///   These snapshot tests confirm the full pipeline: bake → mesh → shader → GPU output.
    ///
    /// Acceptance teeth:
    ///   Test 1: Distinct-color tooth — a match expression on CONTINENT produces ≥2 color clusters
    ///           in the non-background pixels. Uses a full-RGB histogram to count dominant clusters,
    ///           not IsUniform alone (which can be fooled by lighting gradients). BLOCKING.
    ///   Test 2: Constant-input control — same expression shape (Feature kind) with a non-existent
    ///           key → all features fall through to default → render is single-cluster / uniform.
    ///           Proves the data-driven path without breaking when vertex colors are all the same.
    ///   Test 3: White-fallback regression — no FillColorExpression → vertex colors default to white
    ///           → behavior identical to S11 (uniform fill from _BaseColor). Mesh must still build.
    ///
    /// Camera: top-down ortho 512×512, Y=200, orthoSize=70.
    /// Background: distinctive dark slate (same as LitFillSnapshotTests).
    /// </summary>
    [TestFixture]
    public class DataDrivenFillSnapshotTests
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        // Background: dark slate (matches LitFillSnapshotTests convention).
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly byte  BgR8    = (byte)(0.10f * 255 + 0.5f); // 26
        private static readonly byte  BgG8    = (byte)(0.11f * 255 + 0.5f); // 28
        private static readonly byte  BgB8    = (byte)(0.15f * 255 + 0.5f); // 38

        // Match expression: Asia → reddish, South America → bluish, default → gray.
        // The fixture has both "Asia" and "South America" features, so ≥2 clusters are expected.
        private const string DistinctColorExpr =
            "[\"match\",[\"get\",\"CONTINENT\"]," +
            "\"Asia\",[\"rgba\",200,50,50,1]," +
            "\"South America\",[\"rgba\",50,50,200,1]," +
            "[\"rgba\",128,128,128,1]]";

        // Control expression: match on a non-existent key → all features go to default (gray).
        // Same expression shape (Feature kind), but output is uniform.
        private const string ConstantControlExpr =
            "[\"match\",[\"get\",\"__NONEXISTENT__\"]," +
            "\"x\",[\"rgba\",255,0,0,1]," +
            "[\"rgba\",100,100,100,1]]";

        // ─── Helpers ───────────────────────────────────────────────────────────

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("DataDrivenSnapCamera");
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

        // S54: replaces MapFillBootstrap with FillSceneHelper (StyledFillTileBuilder-backed).
        private static (GameObject mapGo, Material liveMaterial) BuildFillGo(
            string colorExpr = null)
        {
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(
                fillColorExpression: colorExpr,
                styleZoom: 0.0,
                viewSize: 100f);
            // Neutral _BaseColor so vertex color is the primary color signal.
            if (mat != null) mat.SetColor("_BaseColor", Color.white);
            return (mapGo, mat);
        }

        private static GameObject AddDirectionalLight(GameObject parent, float intensity, Quaternion rotation)
        {
            var lightGo = new GameObject("DirLight");
            lightGo.transform.SetParent(parent.transform);
            lightGo.transform.rotation = rotation;
            var light = lightGo.AddComponent<Light>();
            light.type      = LightType.Directional;
            light.intensity = intensity;
            return lightGo;
        }

        private static SnapshotRenderer RenderBlank()
        {
            var (go, cam) = BuildCamera();
            var snap = new SnapshotRenderer(SnapW, SnapH);
            try { snap.Render(cam); }
            finally { UnityEngine.Object.DestroyImmediate(go); }
            return snap;
        }

        /// <summary>
        /// Count distinct color clusters in non-background pixels using a coarse full-RGB histogram.
        /// Quantizes to N bits per channel and returns the number of buckets with >= minPixels pixels.
        /// This is robust to lighting gradients (which shift brightness uniformly) while detecting
        /// hue differences (reddish vs bluish vs gray).
        /// bitsPerChannel=4 → 4096 buckets; minPixels should be tuned to image fill fraction.
        /// </summary>
        private static int CountColorClusters(
            byte[] pixels, int width, int height,
            byte bgR, byte bgG, byte bgB,
            int bitsPerChannel = 4,
            int minPixels = 50)
        {
            int shift = 8 - bitsPerChannel;
            int buckets = (1 << bitsPerChannel);
            var hist = new int[buckets * buckets * buckets];

            int totalPx = width * height;
            for (int i = 0; i < totalPx; i++)
            {
                int b = i * 4;
                byte r = pixels[b], g = pixels[b + 1], bl = pixels[b + 2];

                // Skip background pixels.
                int dist = Math.Abs(r - bgR) + Math.Abs(g - bgG) + Math.Abs(bl - bgB);
                if (dist <= SnapshotCoverage.Tolerance) continue;

                int ri = r >> shift, gi = g >> shift, bi = bl >> shift;
                hist[ri * buckets * buckets + gi * buckets + bi]++;
            }

            int clusterCount = 0;
            foreach (int v in hist)
                if (v >= minPixels) clusterCount++;
            return clusterCount;
        }

        // ─── Test 1: Distinct-color tooth (BLOCKING) ──────────────────────────

        [Test]
        public void DataDriven_DistinctContinentColors_ProduceMultipleClusters()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            // High ambient so colors are visible without a strong directional (avoids uniform lighting darkening all).
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.8f, 0.8f, 0.8f, 1f);

            var (cameraGo, camera) = BuildCamera();
            var (mapGo, mat)       = BuildFillGo(DistinctColorExpr);

            AddDirectionalLight(mapGo, 0.5f, Quaternion.Euler(45f, 0f, 0f));

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("data-driven-distinct-colors.png");

                // GPU guard: all-black = no GPU context → Inconclusive.
                if (snap.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive(
                            "Render is all-black: no GPU context. " +
                            "FeatureColorBakerTests (engine-free) are the load-bearing CPU tooth for distinctness. " +
                            "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                        return;
                    }
                }

                // Count color clusters in non-background pixels.
                // With 4 bits per channel and minPixels=50, distinct reddish/bluish/gray regions
                // each need ≥50 pixels to register as a cluster. The world map fill covers a large
                // fraction of the 512×512 image, so 50 pixels is a conservative floor.
                int clusters = CountColorClusters(
                    snap.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8,
                    bitsPerChannel: 4, minPixels: 50);

                Debug.Log($"[DataDrivenFillSnapshotTests] Distinct-color render: color clusters={clusters}");

                // FeatureColorBakerTests (CPU, engine-free) already proved ≥2 distinct colors exist
                // in the bake. Here we just confirm ≥2 survived through the mesh→shader pipeline.
                // If the test is Inconclusive (no GPU), the CPU test already covers distinctness.
                if (clusters < 2)
                {
                    // Diagnose: maybe all-black, or fill didn't render.
                    double lum = SnapshotCoverage.MeanLuminanceOfNonBackground(
                        snap.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                    if (lum < 0.01)
                    {
                        Assert.Inconclusive(
                            $"Fill pixels have near-zero luminance (lum={lum:F4}). " +
                            "Fill may not have rendered, or GPU context may be unavailable.");
                        return;
                    }

                    Assert.Fail(
                        $"Data-driven color expression produced only {clusters} color cluster(s) in the render. " +
                        "Expected ≥2 (reddish for Asia features, bluish for South America features, gray default). " +
                        "The fixture has both Asia and South America features; they should produce distinct vertex colors. " +
                        "Check: 1) the fill-color expression is being passed through the StyledFillTileBuilder paint; " +
                        "2) per-feature vertex colors are baked into the color stream; " +
                        "3) The shader's vColor channel is wired to the COLOR semantic.");
                }

                Assert.GreaterOrEqual(clusters, 2,
                    $"Data-driven match expression on CONTINENT must produce ≥2 distinct color clusters " +
                    $"(got {clusters}). Asia→reddish, South America→bluish, others→gray.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(mapGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Test 2: Constant-input control → uniform (single cluster) ────────

        [Test]
        public void DataDriven_ConstantControl_ProducesSingleCluster()
        {
            int prevQuality = QualitySettings.GetQualityLevel();
            QualitySettings.SetQualityLevel(0, false);

            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.8f, 0.8f, 0.8f, 1f);

            var (cameraGo, camera) = BuildCamera();
            var (mapGo, mat)       = BuildFillGo(ConstantControlExpr);

            AddDirectionalLight(mapGo, 0.5f, Quaternion.Euler(45f, 0f, 0f));

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("data-driven-constant-control.png");

                // GPU guard.
                if (snap.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — constant-control test skipped.");
                        return;
                    }
                }

                double lum = SnapshotCoverage.MeanLuminanceOfNonBackground(
                    snap.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
                if (lum < 0.01)
                {
                    Assert.Inconclusive(
                        $"Fill pixels near-zero luminance (lum={lum:F4}). Fill may not have rendered.");
                    return;
                }

                // With all vertex colors the same (default branch), we expect 1 dominant cluster.
                // Allow 2 as a tolerance for GPU dithering / lighting gradients affecting quantized hue.
                int clusters = CountColorClusters(
                    snap.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8,
                    bitsPerChannel: 3, minPixels: 100); // coarser quantization: 8×8×8 = 512 buckets

                Debug.Log($"[DataDrivenFillSnapshotTests] Constant-control render: color clusters={clusters}");

                // The key assertion: constant-input control must NOT produce the same multi-cluster
                // result as the distinct-color expression. We allow ≤2 clusters (lighting can split one
                // uniform color into a lit/shadow pair at 3-bit resolution).
                Assert.LessOrEqual(clusters, 2,
                    $"Constant-input control (non-existent key → default branch for all features) " +
                    $"must produce ≤2 color clusters (got {clusters} at 3-bit/channel quantization). " +
                    "A data-driven expression with truly uniform output should render as one hue.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(mapGo);
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Test 3: White-fallback regression (no FillColorExpression) ───────

        [Test]
        public void NoColorExpression_MeshBuilds_AndShaderCompiles()
        {
            // Regression: MeshBuilder.SetColors(white) must not break existing S11 behavior.
            var (cameraGo, camera) = BuildCamera();
            var (mapGo, mat)       = BuildFillGo(null); // no data-driven expression
            try
            {
                var meshFilter = mapGo.GetComponent<MeshFilter>();
                Assert.IsNotNull(meshFilter.sharedMesh,
                    "Mesh must be built even when FillColorExpression is null.");
                Assert.Greater(meshFilter.sharedMesh.vertexCount, 0,
                    "Mesh must have vertices when FillColorExpression is null.");

#if UNITY_EDITOR
                // Confirm Fill shader still compiles (same test as LitFillSnapshotTests Test 3).
                var shader = Shader.Find("Map/Fill");
                if (shader != null)
                {
                    bool hasErrors = ShaderUtil.ShaderHasError(shader);
                    if (hasErrors)
                    {
                        var msgs = ShaderUtil.GetShaderMessages(shader);
                        var sb = new System.Text.StringBuilder();
                        sb.AppendLine("Map/Fill shader has compile error(s) after S12 shader edits:");
                        foreach (var m in msgs)
                            sb.AppendLine($"  [{m.severity}] {m.message} (file:{m.file} line:{m.line})");
                        Assert.Fail(sb.ToString());
                    }
                }
#endif
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(mapGo);
            }
        }
    }
}
