using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Style;
#if UNITY_EDITOR
using UnityEditor;
using MapRenderer.Unity.Rendering.Meshing;
#endif
// S54: MapFillBootstrap retired; build helpers migrated to FillSceneHelper.

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// S13 snapshot tests for FillPaint-driven rendering behavior.
    ///
    /// These are Unity-only tests (use UnityEngine.Mesh, rendering, etc.).
    /// The engine-free counterparts live in FillPaintTests.cs (shared with dotnet core-tests).
    ///
    /// Acceptance teeth:
    ///   #3: Opacity no-rebuild — changing _Opacity changes rendered alpha/brightness WITHOUT
    ///       rebuilding the mesh. Asserts SAME mesh instance reference + vertexCount unchanged +
    ///       RGB-toward-background (lower composite luminance at opacity=0 than at opacity=1).
    ///
    ///   #6: Non-white _BaseColor gamma calibration — baked vertex colors (sRGB via Core) are
    ///       linearized before Mesh.SetColors (D2 fix). With _BaseColor=white, the channel multiply
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
    /// GPU context guard: all snapshot tests degrade to Inconclusive if render is all-black.
    ///
    /// Camera: top-down ortho 512×512, Y=200, orthoSize=70.
    /// Background: dark slate (matches DataDrivenFillSnapshotTests and LitFillSnapshotTests).
    /// </summary>
    [TestFixture]
    public class FillPaintSnapshotTests
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly byte  BgR8    = (byte)(0.10f * 255 + 0.5f); // 26
        private static readonly byte  BgG8    = (byte)(0.11f * 255 + 0.5f); // 28
        private static readonly byte  BgB8    = (byte)(0.15f * 255 + 0.5f); // 38

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

        private static SnapshotRenderer RenderBlank()
        {
            var (go, cam) = BuildCamera();
            var snap = new SnapshotRenderer(SnapW, SnapH);
            try { snap.Render(cam); }
            finally { UnityEngine.Object.DestroyImmediate(go); }
            return snap;
        }

        // ── Build a fill mesh with a given color expression and material setup ──
        // S54: replaces MapFillBootstrap with FillSceneHelper (StyledFillTileBuilder-backed).

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

            // Build with opacity=1 (opaque, _BaseColor=green so fill is visible).
            var (mapGo, meshAtBuild, mat) = BuildFillWithColor(null, m =>
            {
                m.SetColor("_BaseColor", Color.green);
                m.SetFloat("_Opacity", 1f);
            });

            if (meshAtBuild == null)
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(mapGo);
                Assert.Inconclusive("Mesh not built — fixture may be missing.");
                return;
            }

            int vertexCountAtBuild = meshAtBuild.vertexCount;

            // Render with opacity=1 to get a baseline.
            using var snapOpaque = new SnapshotRenderer(SnapW, SnapH);
            snapOpaque.Render(camera);
            snapOpaque.WritePng("fill-paint-opacity1.png");

            // GPU guard.
            if (snapOpaque.IsAllBlack())
            {
                using var blank = RenderBlank();
                if (blank.IsAllBlack())
                {
                    UnityEngine.Object.DestroyImmediate(cameraGo);
                    UnityEngine.Object.DestroyImmediate(mapGo);
                    Assert.Inconclusive("No GPU context. Opacity no-rebuild test skipped.");
                    return;
                }
            }

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
            var opaqueVerdict = SnapshotCoverage.Analyse(
                snapOpaque.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
            var invisibleVerdict = SnapshotCoverage.Analyse(
                snapTransparent.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);

            Debug.Log($"[FillPaintSnapshotTests] filled: opacity=1 {opaqueVerdict.FilledFraction:P2}, " +
                      $"opacity=0 {invisibleVerdict.FilledFraction:P2}");

            Assert.Greater(opaqueVerdict.FilledFraction, 0.02f,
                "precondition: the fill must actually cover the frame at _Opacity=1.");
            Assert.Greater(invisibleVerdict.BackgroundFraction, 0.99f,
                $"_Opacity=0 must render NOTHING — background was {invisibleVerdict.BackgroundFraction:P2}, " +
                $"filled {invisibleVerdict.FilledFraction:P2}. A filled frame here means the fragment's alpha " +
                "is being discarded (opaque surface type), so fill-opacity, fill-color alpha and fill-pattern " +
                "alpha masks are all inert.");

            UnityEngine.Object.DestroyImmediate(cameraGo);
            UnityEngine.Object.DestroyImmediate(mapGo);
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

            var (cameraGo, camera) = BuildCamera();

            // Render with _BaseColor=white (neutral — vertex color drives output).
            var (mapGoWhite, meshWhite, matWhite) = BuildFillWithColor(null, m =>
            {
                m.SetColor("_BaseColor", Color.white);
                m.SetFloat("_Opacity",  1f);
            });

            if (meshWhite == null)
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(mapGoWhite);
                Assert.Inconclusive("Mesh not built — fixture may be missing.");
                return;
            }

            using var snapWhite = new SnapshotRenderer(SnapW, SnapH);
            snapWhite.Render(camera);
            snapWhite.WritePng("fill-paint-mapcolor-white.png");

            if (snapWhite.IsAllBlack())
            {
                using var blank = RenderBlank();
                if (blank.IsAllBlack())
                {
                    UnityEngine.Object.DestroyImmediate(cameraGo);
                    UnityEngine.Object.DestroyImmediate(mapGoWhite);
                    Assert.Inconclusive("No GPU context. _BaseColor gamma test skipped.");
                    return;
                }
            }

            UnityEngine.Object.DestroyImmediate(mapGoWhite);

            // Render with _BaseColor=gray (0.5 sRGB). In linear space: Unity linearizes
            // material.SetColor → 0.5 sRGB ≈ 0.214 linear. Multiply with vertex color
            // (white.linear = 1.0) → 0.214. Output should be darker than white _BaseColor case.
            var (mapGoGray, meshGray, matGray) = BuildFillWithColor(null, m =>
            {
                m.SetColor("_BaseColor", new Color(0.5f, 0.5f, 0.5f, 1f));
                m.SetFloat("_Opacity",  1f);
            });

            if (meshGray == null)
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(mapGoGray);
                Assert.Inconclusive("Gray _BaseColor mesh not built.");
                return;
            }

            using var snapGray = new SnapshotRenderer(SnapW, SnapH);
            snapGray.Render(camera);
            snapGray.WritePng("fill-paint-mapcolor-gray.png");

            double lumWhite = SnapshotCoverage.MeanLuminanceOfNonBackground(
                snapWhite.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);
            double lumGray  = SnapshotCoverage.MeanLuminanceOfNonBackground(
                snapGray.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);

            Debug.Log($"[FillPaintSnapshotTests] _BaseColor=white lum={lumWhite:F4}, _BaseColor=gray lum={lumGray:F4}");

            // Gray _BaseColor must produce a darker render than white (GPU multiply darkens).
            // We use a modest margin to handle lighting/ambient variation.
            Assert.Less(lumGray, lumWhite,
                $"_BaseColor=gray (0.5 sRGB) must produce a darker render than _BaseColor=white. " +
                $"white lum={lumWhite:F4}, gray lum={lumGray:F4}. " +
                "If gray ≥ white, the _BaseColor uniform is not driving the albedo correctly.");

            UnityEngine.Object.DestroyImmediate(cameraGo);
            UnityEngine.Object.DestroyImmediate(mapGoGray);
            RenderSettings.ambientMode  = prevAmbientMode;
            RenderSettings.ambientLight = prevAmbientLight;
        }

        // ── #7: StyledFillTileBuilder linearizes vertex colors (D2 fix) ────────
        // S54: migrated from MapFillBootstrap / MeshBuilder to StyledFillTileBuilder.
        // StyledFillTileBuilder stores colors in stream-3 via SetVertexBufferData<Vector4>,
        // so we read them back with Mesh.GetColors (reads the COLOR attribute on any stream).

        [Test]
        public void DataDrivenVertexColor_IsLinearized_BeforeSetColors()
        {
            // Verify that StyledFillTileBuilder.BuildMeshData() applied Color.linear to baked
            // vertex colors (D2 gamma fix). Inspect the mesh Color stream directly.
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

            try
            {
                if (mesh == null)
                {
                    Assert.Inconclusive("Mesh not built for linearization test.");
                    return;
                }

                // S54: StyledFillTileBuilder bakes the linearized fill color into the COLOR vertex
                // stream (stream-3). Mesh.GetColors reads the COLOR attribute regardless of which
                // stream it lives on, so it reflects the raw stored float values (NOT re-gamma'd).
                var colorList = new System.Collections.Generic.List<Color>();
                mesh.GetColors(colorList);
                if (colorList.Count == 0)
                {
                    Assert.Inconclusive("No Color-stream data in mesh (expected at least one vertex).");
                    return;
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
            finally
            {
                UnityEngine.Object.DestroyImmediate(mapGo);
            }
        }
    }
}
