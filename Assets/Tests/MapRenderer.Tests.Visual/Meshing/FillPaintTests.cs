// Fill-paint and boundary-band pixel-color GPU/visual acceptance tests.
//
// The three-way split follows TWO using collisions, not the line cap: `CameraProperties`
// (MapRenderer.Core.Geo vs UnityEngine.Rendering) and bare `Object` (System.Object vs
// UnityEngine.Object) — both CS0104. Within that constraint each file below groups
// its dominant fill sub-area.
// This file: UnityEngine.Rendering importers that also import System (data-driven
// per-feature color, FillPaint-driven rendering, and the boundary band's own pixels).
//
// Contents:
//   DataDrivenFillSnapshotTests  — acceptance snapshot tests — data-driven per-feature colors baked into the fill mesh.
//   FillPaintSnapshotTests       — snapshot tests for FillPaint-driven rendering behavior.
//   FillBoundaryBandRenderTests  — Unity EditMode only — the outward boundary band, observed in rendered pixels.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using MapRenderer.Unity.Rendering.Meshing;
using MapRenderer.Core.Style;
using Unity.Mathematics;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests.Visual
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // DataDrivenFillSnapshotTests — acceptance snapshot tests
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acceptance snapshot tests — data-driven per-feature colors baked into the fill mesh.
    ///
    /// The DataDrivenColorBakeTests (Unity EditMode) are the load-bearing CPU teeth for distinctness.
    /// These snapshot tests confirm the full pipeline: bake → mesh → shader → GPU output.
    ///
    /// Acceptance teeth:
    ///   Test 1: Distinct-color tooth — a match expression on CONTINENT produces ≥2 color clusters
    ///           in the non-background pixels. Uses a full-RGB histogram to count dominant clusters,
    ///           not IsUniform alone (which can be fooled by lighting gradients). BLOCKING.
    ///   Test 2: Constant-input control — same expression shape (Feature kind) with a non-existent
    ///           key → all features fall through to default → render is single-cluster / uniform.
    ///           Proves the data-driven path without breaking when vertex colors are all the same.
    ///   Test 3: White-fallback regression — no FillColorExpression → vertex colors default to white
    ///           → a uniform fill from _BaseColor. Mesh must still build.
    ///
    /// Camera: top-down ortho 512×512, Y=200, orthoSize=70.
    /// Background: distinctive dark slate (same as LitFillSnapshotTests).
    /// </summary>
    [TestFixture]
    public class DataDrivenFillSnapshotTests : VisualTestFixture
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;

        // Background: dark slate (matches LitFillSnapshotTests convention).
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32  = new Color32(26, 28, 38, 255); // BgColor, byte-quantised

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

        /// <summary>Top-down orthographic snapshot camera — see <see cref="VisualTestFixture.BuildCamera"/>.</summary>
        private (GameObject go, Camera camera) BuildCamera() => BuildCamera(new CameraSettings
        {
            ViewSize   = new float2(OrthoSz * 2f, OrthoSz * 2f),
            Background = BgColor,
        });

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


        /// <summary>
        /// Count distinct color clusters in non-background pixels using a coarse full-RGB histogram.
        /// Quantizes to N bits per channel and returns the number of buckets with >= minPixels pixels.
        /// This is robust to lighting gradients (which shift brightness uniformly) while detecting
        /// hue differences (reddish vs bluish vs gray).
        /// bitsPerChannel=4 → 4096 buckets; minPixels should be tuned to image fill fraction.
        /// </summary>
        /// <summary>Chebyshev radius, in pixels, of the antialiased silhouette rim
        /// <see cref="CountColorClusters"/> excludes — see its body for why 2 and not 1.</summary>
        private const int RimRadius = 2;

        private static int CountColorClusters(
            Frame frame, Color32 bg,
            int bitsPerChannel = 4,
            int minPixels = 50)
        {
            int width = frame.Width, height = frame.Height;
            Color32[] pixels = frame.Pixels;
            int shift = 8 - bitsPerChannel;
            int buckets = (1 << bitsPerChannel);
            var hist = new int[buckets * buckets * buckets];

            bool IsBackground(int x, int y)
            {
                Color32 px = pixels[y * width + x];
                return Math.Abs(px.r - bg.r) + Math.Abs(px.g - bg.g) + Math.Abs(px.b - bg.b)
                       <= SnapshotCoverage.Tolerance;
            }

            int totalPx = width * height;
            for (int i = 0; i < totalPx; i++)
            {
                Color32 px = pixels[i];
                byte r = px.r, g = px.g, bl = px.b;

                // Skip background pixels.
                int dist = Math.Abs(r - bg.r) + Math.Abs(g - bg.g) + Math.Abs(bl - bg.b);
                if (dist <= SnapshotCoverage.Tolerance) continue;

                // Skip the silhouette rim. A fill boundary is antialiased now, so pixels NEAR the background
                // are BLENDS of a fill colour and the background — not colours the expression produced, which
                // is the only thing this function counts. Without this a single uniform hue reads as several
                // clusters purely because its outline is soft. The radius is 2, not 1: the band is one DEVICE
                // pixel measured perpendicular to the edge, which spans two pixels of a diagonal silhouette,
                // and a corpus coastline is diagonal nearly everywhere.
                int ix = i % width, iy = i / width;
                bool nearBackground = false;
                for (int dy = -RimRadius; dy <= RimRadius && !nearBackground; dy++)
                for (int dx = -RimRadius; dx <= RimRadius && !nearBackground; dx++)
                {
                    int nx = ix + dx, ny = iy + dy;
                    nearBackground = nx < 0 || ny < 0 || nx >= width || ny >= height || IsBackground(nx, ny);
                }
                if (nearBackground) continue;

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
            Track(cameraGo);
            var (mapGo, mat)       = BuildFillGo(DistinctColorExpr);
            Track(mapGo);

            AddDirectionalLight(mapGo, 0.5f, Quaternion.Euler(45f, 0f, 0f));

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("data-driven-distinct-colors.png");

                // Count color clusters in non-background pixels.
                // With 4 bits per channel and minPixels=50, distinct reddish/bluish/gray regions
                // each need ≥50 pixels to register as a cluster. The world map fill covers a large
                // fraction of the 512×512 image, so 50 pixels is a conservative floor.
                int clusters = CountColorClusters(
                    snap.Pixels, Bg32,
                    bitsPerChannel: 4, minPixels: 50);

                Debug.Log($"[DataDrivenFillSnapshotTests] Distinct-color render: color clusters={clusters}");

                // DataDrivenColorBakeTests (CPU, Unity EditMode) already proved ≥2 distinct colors exist
                // in the bake. Here we just confirm ≥2 survived through the mesh→shader pipeline.
                if (clusters < 2)
                {
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
            Track(cameraGo);
            var (mapGo, mat)       = BuildFillGo(ConstantControlExpr);
            Track(mapGo);

            AddDirectionalLight(mapGo, 0.5f, Quaternion.Euler(45f, 0f, 0f));

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("data-driven-constant-control.png");

                double lum = SnapshotCoverage.MeanLuminanceOfNonBackground(snap.Pixels, Bg32);
                if (lum < 0.01)
                {
                    Assert.Fail(
                        $"Fill pixels near-zero luminance (lum={lum:F4}). Fill may not have rendered.");
                }

                // With all vertex colors the same (default branch), we expect 1 dominant cluster.
                // Allow 2 as a tolerance for GPU dithering / lighting gradients affecting quantized hue.
                int clusters = CountColorClusters(
                    snap.Pixels, Bg32,
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
                QualitySettings.SetQualityLevel(prevQuality, false);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ─── Test 3: White-fallback regression (no FillColorExpression) ───────

        [Test]
        public void NoColorExpression_MeshBuilds_AndShaderCompiles()
        {
            // Regression: MeshBuilder.SetColors(white) must not break the uniform-fill behaviour.
            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (mapGo, mat)       = BuildFillGo(null); // no data-driven expression
            Track(mapGo);
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
                        sb.AppendLine("Map/Fill shader has compile error(s) after the shader edits:");
                        foreach (var m in msgs)
                            sb.AppendLine($"  [{m.severity}] {m.message} (file:{m.file} line:{m.line})");
                        Assert.Fail(sb.ToString());
                    }
                }
#endif
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillPaintSnapshotTests — snapshot tests for FillPaint-driven rendering behavior.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot tests for FillPaint-driven rendering behavior.
    ///
    /// These are Unity-only tests (use UnityEngine.Mesh, rendering, etc.).
    /// The CPU-side counterparts live in FillPaintTests.cs (also Unity EditMode only, not shared with
    /// dotnet core-tests — see that file's header).
    ///
    /// Acceptance teeth:
    ///   #3: Opacity no-rebuild — changing _Opacity changes rendered alpha/brightness WITHOUT
    ///       rebuilding the mesh. Asserts SAME mesh instance reference + vertexCount unchanged +
    ///       RGB-toward-background (lower composite luminance at opacity=0 than at opacity=1).
    ///
    ///   #6: Non-white _BaseColor gamma calibration — baked vertex colors (sRGB via Core) are
    ///       linearized before Mesh.SetColors. With _BaseColor=white, the channel multiply
    ///       is identity and the rendered color matches the baked vertex color (in linear space).
    ///       With _BaseColor=gray (0.5,0.5,0.5 sRGB), the composite is darkened. This test
    ///       confirms the vertex color × _BaseColor product is lower than vertex color alone —
    ///       verifying the GPU multiply is in the correct (linear) color space.
    ///
    ///   #7: MeshBuilder.SetColors linearizes — with a data-driven red vertex color, the rendered
    ///       R channel must be < raw sRGB R=1.0 (it's linearized, ~0.21 in linear), whereas if
    ///       linearization were skipped, the vertex color would be sRGB=1.0 and still render as 1.0.
    ///       This test is necessarily loose (GPU rendering can't give exact float values), but
    ///       verifies the linearization is at least applied in the right direction.
    ///
    /// Camera: top-down ortho 512×512, Y=200, orthoSize=70.
    /// Background: dark slate (matches DataDrivenFillSnapshotTests and LitFillSnapshotTests).
    /// </summary>
    [TestFixture]
    public class FillPaintSnapshotTests : BaseTestFixture
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32 = new Color32(26, 28, 38, 255);

        // ── Camera helper ──────────────────────────────────────────────────────

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("FillPaintSnapCamera");
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


        // ── Build a fill mesh with a given color expression and material setup ──

        private static (GameObject go, Mesh mesh, Material mat) BuildFillWithColor(
            string colorExpr, Action<Material> matSetup = null)
        {
            var (mapGo, mat) = FillSceneHelper.BuildFillGo(
                fillColorExpression: colorExpr,
                styleZoom: 0.0,
                viewSize: 100f);
            if (mat != null) matSetup?.Invoke(mat);
            var mf = mapGo.GetComponent<MeshFilter>();
            return (mapGo, mf != null ? mf.sharedMesh : null, mat);
        }

        // ── #3: Opacity no-rebuild ─────────────────────────────────────────────

        [Test]
        public void Opacity_NoRebuild_MeshUnchanged_And_LuminanceChanges()
        {
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(1f, 1f, 1f, 1f); // full ambient

            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);

            // Build with opacity=1 (opaque, _BaseColor=green so fill is visible).
            var (mapGo, meshAtBuild, mat) = BuildFillWithColor(null, m =>
            {
                m.SetColor("_BaseColor", Color.green);
                m.SetFloat("_Opacity", 1f);
            });
            Track(mapGo);

            if (meshAtBuild == null)
                Assert.Fail("Mesh not built — fixture may be missing.");

            int vertexCountAtBuild = meshAtBuild.vertexCount;

            // Render with opacity=1 to get a baseline.
            using var snapOpaque = new SnapshotRenderer(SnapW, SnapH);
            snapOpaque.Render(camera);
            snapOpaque.WritePng("fill-paint-opacity1.png");

            // NOW: change _Opacity to 0 via material uniform only — NO mesh rebuild.
            mat.SetFloat("_Opacity", 0f);

            // Assert the SAME mesh instance is still assigned (no rebuild happened).
            var mfAfter = mapGo.GetComponent<MeshFilter>();
            Assert.AreSame(meshAtBuild, mfAfter.sharedMesh,
                "Changing _Opacity must NOT rebuild the mesh (same Mesh instance must remain).");
            Assert.AreEqual(vertexCountAtBuild, mfAfter.sharedMesh.vertexCount,
                "vertexCount must be unchanged after opacity-only restyle.");

            using var snapTransparent = new SnapshotRenderer(SnapW, SnapH);
            snapTransparent.Render(camera);
            snapTransparent.WritePng("fill-paint-opacity0.png");

            // STRENGTHENED: this used to assert only "not BRIGHTER at opacity 0" (lum ≤ lum + 0.05), which
            // passed whether or not opacity did anything — a fill rendering fully solid satisfies it. That
            // weakness was load-bearing: fill materials were opaque-surface-typed, so URP's
            // `OutputAlpha(color.a, IsSurfaceTypeTransparent())` forced alpha to 1 and _Opacity had NO visual
            // effect. The old comment here ("the opaque queue may not produce transparency") documented the
            // bug rather than the intent. Now that fills declare _SURFACE_TYPE_TRANSPARENT, opacity 0 must be
            // genuinely INVISIBLE, which is a claim only a working alpha path can satisfy.
            var opaqueVerdict = SnapshotCoverage.Analyse(snapOpaque.Pixels, Bg32);
            var invisibleVerdict = SnapshotCoverage.Analyse(snapTransparent.Pixels, Bg32);

            Debug.Log($"[FillPaintSnapshotTests] filled: opacity=1 {opaqueVerdict.FilledFraction:P2}, " +
                      $"opacity=0 {invisibleVerdict.FilledFraction:P2}");

            Assert.Greater(opaqueVerdict.FilledFraction, 0.02f,
                "precondition: the fill must actually cover the frame at _Opacity=1.");
            Assert.Greater(invisibleVerdict.BackgroundFraction, 0.99f,
                $"_Opacity=0 must render NOTHING — background was {invisibleVerdict.BackgroundFraction:P2}, " +
                $"filled {invisibleVerdict.FilledFraction:P2}. A filled frame here means the fragment's alpha " +
                "is being discarded (opaque surface type), so fill-opacity, fill-color alpha and fill-pattern " +
                "alpha masks are all inert.");

            RenderSettings.ambientMode  = prevAmbientMode;
            RenderSettings.ambientLight = prevAmbientLight;
        }

        // ── #6: Non-white _BaseColor gamma calibration ─────────────────────────

        [Test]
        public void BaseColor_Gray_DarkensRenderVsWhite()
        {
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(1f, 1f, 1f, 1f);

            // The camera is shared across both builds below, so it gets its OWN bag, outliving either.
            using var cameraBag = new ObjectDisposalBag();
            var (cameraGo, camera) = BuildCamera();
            cameraBag.Track(cameraGo);

            double lumWhite;
            {
                // Its own scope: mapGoWhite must be fully torn down before mapGoGray is built below, or
                // both would render into the gray snapshot (nothing here asserts "only one map is alive").
                using var bagWhite = new ObjectDisposalBag();

                // Render with _BaseColor=white (neutral — vertex color drives output).
                var (mapGoWhite, meshWhite, matWhite) = BuildFillWithColor(null, m =>
                {
                    m.SetColor("_BaseColor", Color.white);
                    m.SetFloat("_Opacity",  1f);
                });
                bagWhite.Track(mapGoWhite);

                if (meshWhite == null)
                    Assert.Fail("Mesh not built — fixture may be missing.");

                using var snapWhite = new SnapshotRenderer(SnapW, SnapH);
                snapWhite.Render(camera);
                snapWhite.WritePng("fill-paint-mapcolor-white.png");

                lumWhite = SnapshotCoverage.MeanLuminanceOfNonBackground(snapWhite.Pixels, Bg32);
            }

            double lumGray;
            {
                using var bagGray = new ObjectDisposalBag();

                // Render with _BaseColor=gray (0.5 sRGB). In linear space: Unity linearizes
                // material.SetColor → 0.5 sRGB ≈ 0.214 linear. Multiply with vertex color
                // (white.linear = 1.0) → 0.214. Output should be darker than white _BaseColor case.
                var (mapGoGray, meshGray, matGray) = BuildFillWithColor(null, m =>
                {
                    m.SetColor("_BaseColor", new Color(0.5f, 0.5f, 0.5f, 1f));
                    m.SetFloat("_Opacity",  1f);
                });
                bagGray.Track(mapGoGray);

                if (meshGray == null)
                    Assert.Fail("Gray _BaseColor mesh not built.");

                using var snapGray = new SnapshotRenderer(SnapW, SnapH);
                snapGray.Render(camera);
                snapGray.WritePng("fill-paint-mapcolor-gray.png");

                lumGray = SnapshotCoverage.MeanLuminanceOfNonBackground(snapGray.Pixels, Bg32);
            }

            Debug.Log($"[FillPaintSnapshotTests] _BaseColor=white lum={lumWhite:F4}, _BaseColor=gray lum={lumGray:F4}");

            // Gray _BaseColor must produce a darker render than white (GPU multiply darkens).
            // We use a modest margin to handle lighting/ambient variation.
            Assert.Less(lumGray, lumWhite,
                $"_BaseColor=gray (0.5 sRGB) must produce a darker render than _BaseColor=white. " +
                $"white lum={lumWhite:F4}, gray lum={lumGray:F4}. " +
                "If gray ≥ white, the _BaseColor uniform is not driving the albedo correctly.");

            RenderSettings.ambientMode  = prevAmbientMode;
            RenderSettings.ambientLight = prevAmbientLight;
        }

        // ── #7: StyledFillTileBuilder linearizes vertex colors ─────────────────
        // StyledFillTileBuilder stores colors in stream-3 via SetVertexBufferData<Vector4>,
        // so we read them back with Mesh.GetColors (reads the COLOR attribute on any stream).

        [Test]
        public void DataDrivenVertexColor_IsLinearized_BeforeSetColors()
        {
            // Verify that StyledFillTileBuilder.BuildMeshData() applied Color.linear to baked
            // vertex colors. Inspect the mesh Color stream directly.
            //
            // A baked sRGB (127/255≈0.498, 0, 0, 1):
            //   sRGB R ≈ 0.498 → linear R ≈ ((0.498+0.055)/1.055)^2.4 ≈ 0.212.
            //
            // We build a mesh with a constant baked color of (127,0,0,1) and verify the mesh's
            // stored Color-stream R is ≈ 0.212, not ≈ 0.498.

            const string halfRedExpr =
                "[\"match\",[\"get\",\"__NEVER_MATCHES__\"]," +
                "\"x\",[\"rgba\",255,0,0,1]," +   // unreachable
                "[\"rgba\",127,0,0,1]]";           // default: r=127/255≈0.498, g=0, b=0

            var (mapGo, mesh, mat) = BuildFillWithColor(halfRedExpr, m =>
            {
                m.SetColor("_BaseColor", Color.white); // neutral
                m.SetFloat("_Opacity", 1f);
            });
            Track(mapGo);

            {
                if (mesh == null)
                {
                    Assert.Fail("Mesh not built for linearization test.");
                }

                // StyledFillTileBuilder bakes the linearized fill color into the COLOR vertex
                // stream (stream-3). Mesh.GetColors reads the COLOR attribute regardless of which
                // stream it lives on, so it reflects the raw stored float values (NOT re-gamma'd).
                var colorList = new System.Collections.Generic.List<Color>();
                mesh.GetColors(colorList);
                if (colorList.Count == 0)
                {
                    Assert.Fail("No Color-stream data in mesh (expected at least one vertex).");
                }

                float storedR = colorList[0].r; // Color.r = Red channel

                const float srgbR = 127f / 255f; // ≈ 0.498
                const float expectedLinearR = 0.212f;
                float distToSrgb   = Math.Abs(storedR - srgbR);
                float distToLinear = Math.Abs(storedR - expectedLinearR);

                Debug.Log($"[FillPaintSnapshotTests] Stored R={storedR:F4}, sRGB={srgbR:F4}, expectedLinear={expectedLinearR:F4}");

                Assert.Less(distToLinear, distToSrgb,
                    $"StyledFillTileBuilder must linearize vertex colors off the main thread (D2 fix). " +
                    $"Stored R={storedR:F4} should be closer to linear ({expectedLinearR:F4}) than sRGB ({srgbR:F4}). " +
                    $"distToLinear={distToLinear:F4}, distToSrgb={distToSrgb:F4}. " +
                    "If distToSrgb < distToLinear, Color.linear was not applied before stream assembly.");
            }
        }
    }

    // Unity EditMode only — the outward boundary band, observed in rendered pixels.
    //
    // The band's whole justification is a placement claim, and a placement claim is only settled on a frame:
    // the ramp must lie OUTSIDE the boundary, so the interior keeps full coverage and two abutting fills still
    // leave zero background weight. The job-level fixture (FillBandJobTests) proves the geometry and the
    // attribute; these prove what the geometry was for.
    //
    // Frame geometry, borrowed from GeoJsonFillVisualProofTests: at tilt 0 a tile projects to exactly 512
    // device px whatever the viewport is, so rendering one tile at SnapPx = 512 makes tile-local unit-square
    // coordinates the same thing as frame-fraction coordinates.

    // ───────────────────────────────────────────────────────────────────────────────────
    // FillBoundaryBandRenderTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    internal class FillBoundaryBandRenderTests
    {
        private static readonly TileId BandTile = new TileId { Z = 6, X = 40, Y = 25 };
        private const int SnapPx = 512;

        private const string NoGpuMessage =
            "Scene render is all-background: no GPU context in batch EditMode. " +
            "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode";

        /// <summary>A pixel classified against this scene's two extremes.</summary>
        private enum Ink
        {
            /// <summary>Indistinguishable from the background.</summary>
            Background,

            /// <summary>Indistinguishable from a fully-covered fill pixel.</summary>
            Full,

            /// <summary>Strictly between the two — a partially-covered boundary pixel, which is exactly what
            /// the band exists to produce and what a hard rasterizer never produces.</summary>
            Graded,
        }

        /// <summary>Tile-local unit-square point → lon/lat.</summary>
        /// <param name="x">Tile-local x in [0,1].</param>
        /// <param name="y">Tile-local y in [0,1] — grows SOUTHWARD.</param>
        /// <returns>The (longitude, latitude) of that point.</returns>
        private static double2 LonLat(double x, double y) => BandTile.ToLonLat(x, y, 1.0);

        /// <summary>The scene camera, centred on the tile.</summary>
        /// <returns>The look-at coordinate.</returns>
        private static GeoCoordinate3D LookAt()
        {
            double2 centre = LonLat(0.5, 0.5);
            return new GeoCoordinate3D { Longitude = centre.x, Latitude = centre.y, Altitude = 0.0 };
        }

        /// <summary>One GeoJSON polygon feature from tile-local unit-square corners, closed here.</summary>
        /// <param name="corners">The ring's corners in tile-local unit coordinates, unclosed.</param>
        /// <returns>The feature's JSON.</returns>
        private static string PolygonFeature(params double2[] corners)
        {
            var flat = new List<double>();
            foreach (double2 c in corners) { double2 ll = LonLat(c.x, c.y); flat.Add(ll.x); flat.Add(ll.y); }
            double2 first = LonLat(corners[0].x, corners[0].y);
            flat.Add(first.x); flat.Add(first.y);
            return GeoJsonTestFixtures.Feature("Polygon", $"[{GeoJsonTestFixtures.Positions(flat.ToArray())}]");
        }

        /// <summary>Renders one fill layer over the given features.</summary>
        /// <param name="opacity">The layer's <c>fill-opacity</c>.</param>
        /// <param name="features">The source's polygon features.</param>
        /// <returns>The rendered frame.</returns>
        private static VisualFrame Render(double opacity, params string[] features)
        {
            using var scene = VisualScene.New()
                .Source("shapes", GeoJson.FeatureCollection(GeoJsonTestFixtures.Collection(features)))
                .Layer(VisualLayer.Fill("shapes-fill").Source("shapes").Color("#ffffff").Opacity(opacity))
                .Camera(LookAt(), zoom: BandTile.Z);
            return scene.Render(SnapPx);
        }

        /// <summary>Classifies every pixel against the frame's background and its own brightest pixel — the
        /// fully-covered reference, taken from the SAME frame so shading, opacity and colour management never
        /// have to be modelled.</summary>
        /// <param name="frame">The rendered frame.</param>
        /// <param name="classes">Per-pixel classification, row-major from the bottom-left.</param>
        /// <returns>False when the frame carries no ink at all (no GPU context).</returns>
        private static bool Classify(VisualFrame frame, out Ink[] classes)
        {
            Color32[] px = frame.Pixels.Pixels;
            int n = frame.Width * frame.Height;
            classes = new Ink[n];

            // The background is the frame's own corner pixel; the full-coverage reference is its brightest.
            var background = new double3(px[0].r / 255.0, px[0].g / 255.0, px[0].b / 255.0);
            var full = background;
            double bestDistance = 0.0;
            for (int i = 0; i < n; i++)
            {
                var c = new double3(px[i].r / 255.0, px[i].g / 255.0, px[i].b / 255.0);
                double d = math.length(c - background);
                if (d > bestDistance) { bestDistance = d; full = c; }
            }
            if (bestDistance < 0.05) return false;

            // One LSB of an 8-bit channel is 1/255; the band is a real ramp, so three of them is a
            // comfortable floor that still cannot absorb a partially-covered pixel.
            double tolerance = 3.0 / 255.0 * math.sqrt(3.0);
            for (int i = 0; i < n; i++)
            {
                var c = new double3(px[i].r / 255.0, px[i].g / 255.0, px[i].b / 255.0);
                if (math.length(c - background) <= tolerance) classes[i] = Ink.Background;
                else if (math.length(c - full) <= tolerance) classes[i] = Ink.Full;
                else classes[i] = Ink.Graded;
            }
            return true;
        }

        /// <summary>A compact description of what actually reached the frame — the class histogram, the two
        /// reference colours, and a scanline across the polygon's left boundary. Carried in the failure
        /// message so a red here says WHAT rendered, not merely that the count was wrong.</summary>
        /// <param name="frame">The rendered frame.</param>
        /// <param name="classes">Its classification.</param>
        /// <returns>A one-line diagnostic.</returns>
        private static string Diagnose(VisualFrame frame, Ink[] classes)
        {
            Color32[] px = frame.Pixels.Pixels;
            int bg = 0, full = 0, graded = 0;
            foreach (Ink c in classes)
            {
                if (c == Ink.Background) bg++; else if (c == Ink.Full) full++; else graded++;
            }
            var scan = new System.Text.StringBuilder();
            int row = frame.Height / 2;
            for (int x = 120; x < 140; x++)
            {
                Color32 c = px[row * frame.Width + x];
                scan.Append($" {x}:{c.r},{c.g},{c.b}");
            }
            Color32 corner = px[0];
            var cornerPx = $"{corner.r},{corner.g},{corner.b}";

            var meshes = new System.Text.StringBuilder();
            if (frame.MapView != null)
                foreach (UnityEngine.MeshFilter mf in frame.MapView.GetComponentsInChildren<UnityEngine.MeshFilter>(true))
                    if (mf.sharedMesh != null)
                        meshes.Append($" {mf.gameObject.name}:v{mf.sharedMesh.vertexCount}/i{mf.sharedMesh.GetIndexCount(0)}");

            return $"[bg={bg} full={full} graded={graded} corner=({cornerPx}) meshes:{meshes} scan(row {row}):{scan}]";
        }

        /// <summary>Counts graded pixels in the whole frame.</summary>
        /// <param name="classes">A classification from <see cref="Classify"/>.</param>
        /// <returns>The count.</returns>
        private static int GradedCount(Ink[] classes)
        {
            int count = 0;
            foreach (Ink c in classes) if (c == Ink.Graded) count++;
            return count;
        }

        // ── The boundary is soft, the interior is not ──────────────────────────────────────────────────
        //
        // RED: against the tree before the band node, EVERY count below is 0 — a hard rasterizer emits no
        // partially-covered pixel at all. RED for the placement half: displace the band inward and graded
        // pixels appear INSIDE the boundary, which the interior assertion catches.
        [Test]
        public void ASquareFillHasGradedBoundaryPixels_AndAnUngradedInterior()
        {
            const double lo = 0.25, hi = 0.75;
            VisualFrame frame = Render(1.0, PolygonFeature(
                new double2(lo, lo), new double2(lo, hi), new double2(hi, hi), new double2(hi, lo)));
            if (!Classify(frame, out Ink[] classes)) Assert.Ignore(NoGpuMessage);

            int graded = GradedCount(classes);
            Assert.Greater(graded, 0,
                "a fill silhouette must produce partially-covered pixels. Zero means the band never reached " +
                "the frame — the state this whole stage exists to leave. " + Diagnose(frame, classes));

            // The interior, well inside the boundary: [0.35,0.65]² of the tile. Not one graded pixel may be
            // here. This is the lemma's own precondition — coverage stays exactly 1 everywhere the hard fill
            // already was — and it is what an inward-displaced band breaks first.
            int lo35 = (int)(0.35 * SnapPx), hi65 = (int)(0.65 * SnapPx);
            for (int y = lo35; y < hi65; y++)
                for (int x = lo35; x < hi65; x++)
                    Assert.AreEqual(Ink.Full, classes[y * frame.Width + x],
                        $"interior pixel ({x},{y}) is not fully covered. The band must lie strictly OUTSIDE " +
                        "the boundary; a ramp reaching inward is the placement the mechanism forbids.");

            // And no graded pixel may sit far outside it either: the band is ONE device pixel, so nothing
            // beyond a few px of the boundary may be partially covered.
            int outerLo = (int)(lo * SnapPx) - 6, outerHi = (int)(hi * SnapPx) + 6;
            for (int y = 0; y < frame.Height; y++)
                for (int x = 0; x < frame.Width; x++)
                {
                    bool nearBoundary = x >= outerLo && x <= outerHi && y >= outerLo && y <= outerHi;
                    if (!nearBoundary)
                        Assert.AreNotEqual(Ink.Graded, classes[y * frame.Width + x],
                            $"pixel ({x},{y}) is graded but lies more than 6 px outside the polygon — the band " +
                            "is one device pixel wide, not a halo.");
                }
        }

        /// <summary>The band's PERPENDICULAR width, in device pixels, read off a rendered silhouette.
        ///
        /// <para>Total alpha-weighted coverage over the frame exceeds the hard silhouette's area by exactly
        /// the ramp's integral: an outward ramp of perpendicular width <c>w</c> that falls linearly 1 → 0
        /// contributes <c>perimeter × w / 2</c>. So <c>w = 2 × (Σ coverage − hard area) / perimeter</c> —
        /// sub-pixel, no threshold, and it reads the physical quantity the mechanism specifies rather than a
        /// pixel count that quantisation rounds.</para></summary>
        /// <param name="frame">The rendered frame.</param>
        /// <param name="hardAreaPx">The silhouette's analytic area, device px².</param>
        /// <param name="perimeterPx">Its analytic perimeter, device px.</param>
        /// <param name="plateauX">Column of a SINGLY-covered interior pixel — the coverage-1 reference. The
        /// frame centre is wrong for an abutting pair: it lands on the seam, which is double-coated at
        /// <c>fill-opacity &lt; 1</c>, and every single-coated pixel would then read coverage &lt; 1.</param>
        /// <param name="plateauY">Row of that pixel.</param>
        /// <returns>The measured perpendicular ramp width, device px.</returns>
        private static double MeasuredRampWidthPx(
            VisualFrame frame, double hardAreaPx, double perimeterPx, int plateauX, int plateauY)
        {
            Frame px = frame.Pixels;
            float3 background = PixelCoverage.BackgroundLinear(px);
            float3 plateau = PixelCoverage.SampleLinearBox(px, plateauX, plateauY, 4);

            double total = 0.0;
            for (int y = 0; y < frame.Height; y++)
                for (int x = 0; x < frame.Width; x++)
                    total += PixelCoverage.CoverageAt(px, x, y, background, plateau);

            return 2.0 * (total - hardAreaPx) / perimeterPx;
        }

        // ── The ramp is ONE device pixel, on a diagonal silhouette as much as on an axis-aligned one ────
        //
        // The shipped coverage divides by a EUCLIDEAN gradient. fwidth is Manhattan (|ddx| + |ddy|), which
        // OVER-READS the gradient by up to sqrt(2) — and because coverage is (1 - side) DIVIDED by it, an
        // over-read gradient makes coverage fall FASTER: on a 45° silhouette the transition narrows to
        // 1/sqrt(2) ~ 0.707 device px, and coverage at the boundary itself drops from 1 to 0.707, an
        // under-inked seam. (An axis-aligned edge is unaffected: there ddx or ddy is zero and the two
        // gradients agree bit for bit — which is exactly why the diagonal arm is the one that discriminates
        // and why an axis-aligned fixture cannot stand in for it.)
        //
        // RED: ① against the tree before the band node, both widths are 0; ② swap
        // length(float2(ddx, ddy)) for fwidth in Fill_BandCoverage.hlsl and the DIAMOND arm reds while the
        // SQUARE arm stays green. Two earlier forms of this tooth survived that injection and are recorded
        // because each looked convincing: counting "graded" PIXELS is quantised far too coarsely to separate
        // 1.0 px from 0.707 px, and a [0.7, 1.3] bound on this same integral passes 0.707 by two thousandths.
        //
        // The LOWER bound is the discriminating one and it is what the analysis fixes: Manhattan renders
        // 1/sqrt(2) = 0.707 of the true width, so anything at or below ~0.8 must fail. The upper bound is a
        // sanity rail, not a discriminator, and is left slack: the diagonal arm reads a little over 1 because
        // a diamond's four rasterised corners add ink the perimeter model does not account for.
        [Test]
        public void TheRampIsOneDevicePixelWide_OnADiagonalSilhouetteAsWellAsAnAxisAlignedOne(
            [Values(false, true)] bool diagonal)
        {
            const double c = 0.5, r = 0.25;
            VisualFrame frame;
            double hardAreaPx, perimeterPx;
            if (diagonal)
            {
                // A diamond: same centre, same circumradius, every edge at 45° on screen. Diagonals are
                // 2r of the tile, so the area is d²/2 and each side is r·sqrt(2) of the tile.
                frame = Render(1.0, PolygonFeature(
                    new double2(c, c - r), new double2(c - r, c), new double2(c, c + r), new double2(c + r, c)));
                double diagonalPx = 2.0 * r * SnapPx;
                hardAreaPx  = diagonalPx * diagonalPx / 2.0;
                perimeterPx = 4.0 * r * math.sqrt(2.0) * SnapPx;
            }
            else
            {
                frame = Render(1.0, PolygonFeature(
                    new double2(c - r, c - r), new double2(c - r, c + r), new double2(c + r, c + r), new double2(c + r, c - r)));
                double sidePx = 2.0 * r * SnapPx;
                hardAreaPx  = sidePx * sidePx;
                perimeterPx = 4.0 * sidePx;
            }
            if (!Classify(frame, out _)) Assert.Ignore(NoGpuMessage);

            double width = MeasuredRampWidthPx(frame, hardAreaPx, perimeterPx, frame.Width / 2, frame.Height / 2);
            UnityEngine.Debug.Log($"[FillBoundaryBand] measured ramp width: diagonal={diagonal} w={width:F4} px");

            Assert.Greater(width, 0.85,
                $"the band must be ONE device pixel wide; measured {width:F3} px. Zero means it is not there " +
                "at all (the state before the band node existed); ~0.707 on the diagonal arm means the " +
                "coverage gradient went Manhattan.");
            Assert.Less(width, 1.30,
                $"the band must be ONE device pixel wide; measured {width:F3} px — wider means the ramp is " +
                "reaching past the single pixel the mechanism specifies.");
        }

        // ── The lemma, on the composited frame ────────────────────────────────────────────────────────
        //
        // Two ABUTTING translucent polygons in ONE layer. Background weight along their shared edge must stay
        // 0 — it is 0 today only because hard rasterization gives one of them full coverage, and it stays 0
        // only while the ramp lies strictly outside each boundary. A ramp placed inside leaves
        // (1-a_A)(1-a_B) > 0 and a background trench opens along every shared edge and tile seam.
        //
        // RED, and the honest limits of it. Give the INTERIOR a varying `side` (so its coverage stops
        // reading exactly 1) and this reds — that is the property the lemma actually rests on. The obvious
        // injection, "displace the band inward", does NOT red it and is recorded here so nobody re-derives
        // it: moving the band's outer ring inward leaves earcut's interior triangles covering the boundary
        // at coverage 1 underneath, so the band merely double-coats and no background is exposed. The inset
        // variant this tooth is meant to have killed shrinks the INTERIOR RING, which is a design change to
        // a different node, not a one-line defect in this one.
        //
        // The first assertion is a PRECONDITION, not decoration: without it this tooth passes on a tree with
        // no band at all — a hard silhouette also leaves no background at a shared edge — which is how it sat
        // green through four gate runs while the band was rendering nothing.
        [Test]
        public void AbuttingPolygonsInOneLayerLeaveNoBackgroundAlongTheirSharedEdge()
        {
            const double lo = 0.25, mid = 0.5, hi = 0.75;
            const double opacity = 0.3;
            VisualFrame frame = Render(opacity,
                PolygonFeature(new double2(lo, lo), new double2(lo, hi), new double2(mid, hi), new double2(mid, lo)),
                PolygonFeature(new double2(mid, lo), new double2(mid, hi), new double2(hi, hi), new double2(hi, lo)));

            Color32[] px = frame.Pixels.Pixels;
            var background = new double3(px[0].r / 255.0, px[0].g / 255.0, px[0].b / 255.0);

            double Inkiness(int x, int y)
            {
                Color32 c = px[y * frame.Width + x];
                return math.length(new double3(c.r / 255.0, c.g / 255.0, c.b / 255.0) - background);
            }

            // A row through the middle of both polygons, and a single-covered reference well inside the left
            // one. Both come from THIS frame, so opacity and shading need no model.
            int row = SnapPx / 2;
            double reference = Inkiness((int)(0.35 * SnapPx), row);
            Assert.Greater(reference, 0.05, NoGpuMessage);

            // Precondition: the pair's OUTER silhouette must actually be banded, or this fixture is
            // measuring a hard-rasterized frame and the seam claim below is vacuous.
            double pairAreaPx      = (hi - lo) * (hi - lo) * SnapPx * SnapPx;
            double pairPerimeterPx = 4.0 * (hi - lo) * SnapPx;
            double outerRampPx = MeasuredRampWidthPx(
                frame, pairAreaPx, pairPerimeterPx, (int)(0.35 * SnapPx), row);
            Assert.Greater(outerRampPx, 0.4,
                $"precondition: the abutting pair's outer silhouette must carry a real band; measured " +
                $"{outerRampPx:F3} px. A hard silhouette leaves no background at a shared edge either, so " +
                "without this the seam assertion below is satisfied by the very state this tooth exists to reject.");

            int seamLo = (int)(mid * SnapPx) - 4, seamHi = (int)(mid * SnapPx) + 4;
            double peak = 0.0;
            for (int x = seamLo; x <= seamHi; x++)
            {
                double ink = Inkiness(x, row);
                peak = math.max(peak, ink);
                Assert.GreaterOrEqual(ink, reference - 0.02,
                    $"seam pixel ({x},{row}) is closer to the background than a singly-covered pixel — the " +
                    "background is showing through where two fills abut, which is the artefact that rejected " +
                    "every inset placement.");
            }

            // ── The residual rim, bounded against a PREDICTED value ───────────────────────────────────
            //
            // A tile seam is suppressed exactly (both endpoints on one window line). Two polygons abutting
            // INSIDE one tile share no such predicate: each one's band ramps across the other's interior and
            // the pair composites twice. That is over-ink, never a trench — the assertions above are what say
            // so — and it was accepted rather than closed with an intra-layer edge hash, which would still
            // miss a shared boundary expressed with different vertex counts on the two sides. These two
            // assertions are what observe that acceptance, so "we accepted a rim there" is not a claim
            // nothing checks.
            //
            // The bound is derived, not measured-then-recorded (a bound no bad port fails). A singly-covered
            // pixel is S = f·C + (1−f)·B, so compositing the same source over it again gives
            // D = f·C + (1−f)·S = S + (1−f)(S−B) — ink exactly (2−f)× the reference. The probe fixture
            // measured this ratio at 1.5026 against a predicted 1.5000 at fill-opacity 0.5, which is what
            // says the compositing is linear in the space sampled here rather than assuming it.
            Assert.Greater(peak, reference + 0.05,
                $"the rim this bound exists to bound is not there: peak seam ink {peak:F3} against a " +
                $"singly-covered {reference:F3}. Either the band stopped reaching across the shared edge — " +
                "in which case this bound passes over nothing — or a suppression predicate meant for tile " +
                "seams has started firing on a real shared edge.");

            Assert.LessOrEqual(peak, reference * (2.0 - opacity) + 0.03,
                $"peak seam ink {peak:F3} exceeds double compositing of the same source " +
                $"({reference * (2.0 - opacity):F3} = (2−f)× the singly-covered {reference:F3} at f = " +
                $"{opacity}). The accepted residual is ONE band overlapping one interior; anything past this " +
                "is a second overlap or a band wider than the one pixel it is specified to be.");
        }
    }
}
