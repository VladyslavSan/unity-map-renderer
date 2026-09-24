// Fill-paint and boundary-band pixel-color GPU/visual tests. The file split follows two CS0104 collisions
// (`CameraProperties`, bare `Object`); this file holds the importers of both UnityEngine.Rendering and System.
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
    /// Data-driven per-feature fill colors survive bake → mesh → shader → GPU output; the CPU teeth for
    /// distinctness are DataDrivenColorBakeTests. A match on CONTINENT renders ≥2 clusters, counted by a full-RGB
    /// histogram because IsUniform alone is fooled by lighting gradients. A match on a missing key renders one
    /// hue, and no FillColorExpression still builds the mesh (white vertex colors).
    /// Camera: top-down ortho 512×512, Y=200, orthoSize=70; background dark slate.
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


        /// <summary>Chebyshev radius, in pixels, of the antialiased silhouette rim
        /// <see cref="CountColorClusters"/> excludes — see its body for why 2 and not 1.</summary>
        private const int RimRadius = 2;

        /// <summary>
        /// Count distinct color clusters in non-background pixels using a coarse full-RGB histogram.
        /// Quantizes to N bits per channel and returns the number of buckets with >= minPixels pixels.
        /// This is robust to lighting gradients (which shift brightness uniformly) while detecting
        /// hue differences (reddish vs bluish vs gray).
        /// bitsPerChannel=4 → 4096 buckets; minPixels should be tuned to image fill fraction.
        /// </summary>
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

                // Skip the antialiased rim: its pixels blend fill and background, so one hue would read as several
                // clusters. Non-obvious why: the radius is 2, as a one-pixel band spans two pixels of a diagonal edge.
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

                // Each reddish/bluish/gray region needs ≥50 pixels to count as a cluster. The world fill covers
                // a large fraction of the 512×512 frame, so 50 pixels is a low floor.
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

                // Constant input must not give the distinct-color result. ≤2 clusters passes: at 3-bit resolution
                // lighting can split one uniform color into a lit/shadow pair.
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
    /// FillPaint-driven rendering, observed in rendered frames and the built mesh. Changing <c>_Opacity</c>
    /// changes the render without a mesh rebuild. A gray <c>_BaseColor</c> renders darker than white, so the
    /// vertex color × <c>_BaseColor</c> multiply applies. Baked sRGB vertex colors are linearized before they
    /// reach the mesh COLOR stream.
    /// Camera: top-down ortho 512×512, Y=200, orthoSize=70; background dark slate.
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

            // Opacity 0 must be INVISIBLE, not merely "not brighter": an opaque-surface fill passes the weaker
            // check, as URP's OutputAlpha forces alpha to 1 without _SURFACE_TYPE_TRANSPARENT.
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

                // Unity linearizes SetColor: 0.5 sRGB ≈ 0.214 linear, times the white vertex color (1.0) is
                // 0.214, so this render must be darker than the white _BaseColor one.
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
        // Colors sit on stream 3 (SetVertexBufferData<Vector4>); Mesh.GetColors reads COLOR on any stream.

        [Test]
        public void DataDrivenVertexColor_IsLinearized_BeforeSetColors()
        {
            // A baked sRGB R of 127/255 ≈ 0.498 linearizes to ((0.498+0.055)/1.055)^2.4 ≈ 0.212, so the
            // mesh COLOR stream must hold R ≈ 0.212, not ≈ 0.498.

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

                // Mesh.GetColors returns the stored floats of the COLOR stream, not re-gamma'd.
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

    // Unity EditMode only — the outward boundary band, observed in rendered pixels. The ramp must lie OUTSIDE
    // the boundary, so the interior keeps full coverage and abutting fills leave zero background weight.
    // Non-obvious why: only a frame settles that placement; FillBandJobTests covers the geometry. At tilt 0 a
    // tile projects to 512 device px, so at SnapPx = 512 tile-local unit coordinates are frame fractions.

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
        // A hard rasterizer grades no pixel; a band displaced inward grades pixels the interior check covers.
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

            // No graded pixel in the interior [0.35,0.65]²: coverage stays 1 wherever the hard fill was (the
            // lemma's precondition), which an inward-displaced band breaks first.
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

        /// <summary>The band's PERPENDICULAR width, in device pixels, read off a rendered silhouette. An outward
        /// ramp of width <c>w</c> falling linearly 1 → 0 adds <c>perimeter × w / 2</c> to the hard area's coverage,
        /// so <c>w = 2 × (Σ coverage − hard area) / perimeter</c>: sub-pixel, with no threshold and no pixel count
        /// that quantisation rounds.</summary>
        /// <param name="frame">The rendered frame.</param>
        /// <param name="hardAreaPx">The silhouette's analytic area, device px².</param>
        /// <param name="perimeterPx">Its analytic perimeter, device px.</param>
        /// <param name="plateauX">Column of a SINGLY-covered interior pixel, the coverage-1 reference. Not the frame
        /// centre of an abutting pair: that seam is double-coated at <c>fill-opacity &lt; 1</c>.</param>
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
        // Non-obvious why: coverage divides by a EUCLIDEAN gradient; fwidth (|ddx| + |ddy|) over-reads it by up
        // to sqrt(2) on a 45° edge, narrowing the ramp to ~0.707 px. On an axis-aligned edge the two agree, so
        // only the diamond arm discriminates, and only the lower bound does: the upper bound is a slack rail, as
        // a diamond's rasterised corners add ink the perimeter model omits. A graded-pixel count cannot separate
        // 1.0 px from 0.707 px, and a 0.7 lower bound passes 0.707.
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
        // Two ABUTTING translucent polygons in ONE layer leave zero background weight along their shared edge only
        // while each ramp lies strictly outside its boundary; a ramp inside leaves (1-a_A)(1-a_B) > 0, a trench.
        // Limitation: a band moved inward does not red this, as earcut's interior still covers the boundary at
        // coverage 1; an interior whose coverage drops below 1 does. The ramp-width precondition stops a hard,
        // band-less silhouette from passing, since it also shows no background at a shared edge.
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
            // See docs/fill-boundary-antialiasing-design.md § "The residual rim, accepted (maintainer call)".
            // Non-obvious why: S = f·C + (1−f)·B composited again gives D = S + (1−f)(S−B), ink (2−f)× the
            // reference, so the bound is derived, not measured. A probe measured 1.5026 against a predicted 1.5000
            // at fill-opacity 0.5, so linear compositing in the sampled space is checked, not assumed.
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
