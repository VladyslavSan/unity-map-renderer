// Unity-only: render + Mesh-upload tests for the S54 canonical vertex layout.
// NOT included in Tools/core-tests/core-tests.csproj.

using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Imaging;
using MapRenderer.Core.Geometry;
using MapRenderer.Unity;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// S54 — canonical vertex layout + LineWidthColor flip teeth.
    ///
    /// Tooth A — LineWidthColor flip is FALSIFIABLE through the REAL stream-3 interleave:
    ///   • Color sub-tooth: a non-white per-vertex colour baked into <c>LineWidthColor.Color</c> shows
    ///     in the rendered pixel (lit Map/Line shader: albedo *= vColor.rgb * _BaseColor.rgb,
    ///     _BaseColor=white → vColor IS the signal). We bake CYAN (0,1,1,1) with WidthScale=2 and assert
    ///     the centre band is cyan-dominant (g>r AND b>r). A struct-order revert (the pre-flip
    ///     {WidthScale; Color}) with the canonical descriptors fixed would make the GPU read
    ///     vColor = (WidthScale=2→1, r, g, b) = (1,0,1,1) → MAGENTA (r max) with alpha still 1 (visible,
    ///     NOT all-black/Inconclusive) → g>r FAILS. That is a real falsification, not a degenerate skip.
    ///   • Width sub-tooth: a NON-UNIT WidthScale=2 baked into the stream doubles the measured band
    ///     width vs WidthScale=1 (±AA tolerance), proving stream-3 carries the width correctly. The
    ///     revert routes the colour's alpha (1.0) into WidthScale → wrong (≈unit) width → ratio fails.
    ///
    /// Tooth B — the "vertex buffer attributes supplied in non-standard order" warning is GONE:
    ///   capture all log warnings while uploading a fill mesh (StyledFillTileBuilder via FillSceneHelper)
    ///   AND a line mesh (SyntheticLineMesh); assert none contains "non-standard order". GPU-INDEPENDENT
    ///   (mesh upload only) — no Inconclusive guard. Reverting either descriptor reorder re-fires it.
    ///
    /// Camera: top-down ortho 512×512, Y=200, orthoSize=70. metersPerPixel ≈ 0.2734 m/px.
    /// </summary>
    [TestFixture]
    public class LineStreamLayoutTests
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly byte  BgR8    = (byte)(0.10f * 255 + 0.5f);
        private static readonly byte  BgG8    = (byte)(0.11f * 255 + 0.5f);
        private static readonly byte  BgB8    = (byte)(0.15f * 255 + 0.5f);

        private static float MetersPerPx => 2f * OrthoSz / SnapH;

        // ── Tooth A: color sub-tooth ─────────────────────────────────────────────

        [Test]
        public void LineWidthColor_BakedVertexColor_RendersThroughStream3()
        {
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.white;

            var (cameraGo, camera) = BuildCamera();

            // CYAN vertex colour (0,1,1,1) with NON-UNIT WidthScale=2 baked into stream-3.
            // _BaseColor=white so the baked vColor is the only colour signal.
            var pts  = new List<double2> { new double2(-40, 0), new double2(40, 0) };
            var mesh = SyntheticLineMesh.BuildFromPoints(pts, new Vector4(0f, 1f, 1f, 1f), 2f,
                JoinType.Miter, CapType.Butt);

            var lineGo = new GameObject("CyanLine");
            lineGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            var shader = Shader.Find("Map/Line") ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader) { name = "CyanLineMat" };
            mat.SetFloat("_Width",          6f);
            mat.SetFloat("_WidthIsPixels",  0f);
            mat.SetFloat("_MetersPerPixel", MetersPerPx);
            mat.SetColor("_BaseColor",       Color.white);  // identity → vColor is the signal
            mat.SetFloat("_Opacity",        1f);
            mat.SetFloat("_Blur",           1f);
            lineGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-stream3-cyan.png");

                if (snap.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — stream-3 colour test skipped.");
                        return;
                    }
                }

                // Find the line band centre on the centre column and read that pixel.
                int row = FindLineCenterRow(snap.RawPixels, SnapW, SnapH, SnapW / 2);
                if (row < 0)
                {
                    Assert.Inconclusive("Could not find line band — line may not render in batch mode.");
                    return;
                }

                int idx = (row * SnapW + SnapW / 2) * 4;
                byte r = snap.RawPixels[idx], g = snap.RawPixels[idx + 1], b = snap.RawPixels[idx + 2];
                Debug.Log($"[LineStreamLayout] centre pixel RGB=({r},{g},{b}) at row={row} (expect cyan: g,b > r)");

                // Cyan dominance: green AND blue clearly exceed red. A struct-order revert produces
                // magenta (r max) → this fails.
                Assert.That(g, Is.GreaterThan(r + 20),
                    $"Baked CYAN vertex colour must render green-dominant (g={g} > r={r}). " +
                    "If r dominates, stream-3 LineWidthColor channels are scrambled (the flip regressed).");
                Assert.That(b, Is.GreaterThan(r + 20),
                    $"Baked CYAN vertex colour must render blue-dominant (b={b} > r={r}).");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(lineGo);
                UnityEngine.Object.DestroyImmediate(mat);
                if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ── Tooth A: width sub-tooth (non-unit WidthScale through stream-3) ──────

        [Test]
        public void LineWidthColor_NonUnitWidthScale_DoublesBandWidth()
        {
            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.white;

            var (cameraGo, camera) = BuildCamera();

            var pts = new List<double2> { new double2(-40, 0), new double2(40, 0) };
            // Same base _Width; the only difference is the WidthScale baked into stream-3.
            var meshUnit   = SyntheticLineMesh.BuildFromPoints(pts, new Vector4(1f, 1f, 1f, 1f), 1f);
            var meshDouble = SyntheticLineMesh.BuildFromPoints(pts, new Vector4(1f, 1f, 1f, 1f), 2f);

            var goUnit   = MakeLineGo(meshUnit,   "WidthScale1");
            var goDouble = MakeLineGo(meshDouble, "WidthScale2");
            goDouble.go.SetActive(false); // render one at a time on the same camera

            using var snapUnit   = new SnapshotRenderer(SnapW, SnapH);
            using var snapDouble = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snapUnit.Render(camera);
                if (snapUnit.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — width-scale test skipped.");
                        return;
                    }
                }

                int widthUnit = MeasureBandWidth(snapUnit.RawPixels, SnapW, SnapH, SnapW / 2);

                goUnit.go.SetActive(false);
                goDouble.go.SetActive(true);
                snapDouble.Render(camera);
                int widthDouble = MeasureBandWidth(snapDouble.RawPixels, SnapW, SnapH, SnapW / 2);

                Debug.Log($"[LineStreamLayout] band width: unit={widthUnit}px, double={widthDouble}px " +
                          $"(expect ratio ≈ 2.0)");

                if (widthUnit <= 0 || widthDouble <= 0)
                {
                    Assert.Inconclusive("Could not measure line band(s) — line may not render in batch mode.");
                    return;
                }

                float ratio = (float)widthDouble / widthUnit;
                // ±AA tolerance: a few px of feather on each band; ratio must be clearly near 2, never near 1.
                Assert.That(ratio, Is.InRange(1.6f, 2.4f),
                    $"WidthScale=2 baked into stream-3 must roughly DOUBLE the band width " +
                    $"(unit={widthUnit}px, double={widthDouble}px, ratio={ratio:F2}). " +
                    "A ratio near 1.0 means WidthScale did not reach the shader — stream-3 interleave regressed.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                goUnit.Destroy();
                goDouble.Destroy();
                if (meshUnit   != null) UnityEngine.Object.DestroyImmediate(meshUnit);
                if (meshDouble != null) UnityEngine.Object.DestroyImmediate(meshDouble);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ── Tooth B: non-standard-order warning is gone (GPU-independent) ────────

        [Test]
        public void VertexUpload_DoesNotEmitNonStandardOrderWarning()
        {
            var captured = new List<string>();
            Application.LogCallback handler = (condition, stackTrace, type) =>
            {
                if (type == LogType.Warning || type == LogType.Error)
                    lock (captured) captured.Add(condition ?? string.Empty);
            };
            Application.logMessageReceived += handler;

            GameObject fillGo = null;
            Mesh lineMesh = null;
            try
            {
                // Fill upload via the live StyledFillTileBuilder path.
                var (go, _) = FillSceneHelper.BuildFillGo();
                fillGo = go;

                // Line upload via the live StyledLineTileBuilder path.
                lineMesh = SyntheticLineMesh.BuildFromPoints(
                    new List<double2> { new double2(-40, 0), new double2(40, 0) },
                    JoinType.Miter, CapType.Butt);

                Assert.IsNotNull(lineMesh, "Line mesh must upload.");

                var offenders = new StringBuilder();
                lock (captured)
                {
                    foreach (var msg in captured)
                        if (msg.IndexOf("non-standard order", StringComparison.OrdinalIgnoreCase) >= 0)
                            offenders.AppendLine(msg);
                }

                Assert.That(offenders.Length, Is.EqualTo(0),
                    "Mesh upload must NOT emit the 'vertex buffer attributes supplied in non-standard order' " +
                    "warning — both descriptor arrays are in ascending VertexAttribute order (S54). " +
                    "Captured offenders:\n" + offenders);
            }
            finally
            {
                Application.logMessageReceived -= handler;
                if (fillGo != null) UnityEngine.Object.DestroyImmediate(fillGo);
                if (lineMesh != null) UnityEngine.Object.DestroyImmediate(lineMesh);
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────

        private struct LineGo
        {
            public GameObject go;
            public Material mat;
            public void Destroy()
            {
                if (mat != null) UnityEngine.Object.DestroyImmediate(mat);
                if (go  != null) UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static LineGo MakeLineGo(Mesh mesh, string name)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var shader = Shader.Find("Map/Line") ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader) { name = name + "Mat" };
            mat.SetFloat("_Width",          6f);
            mat.SetFloat("_WidthIsPixels",  0f);
            mat.SetFloat("_MetersPerPixel", MetersPerPx);
            mat.SetColor("_BaseColor",       new Color(0.9f, 0.5f, 0.1f, 1f));
            mat.SetFloat("_Opacity",        1f);
            mat.SetFloat("_Blur",           1f);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return new LineGo { go = go, mat = mat };
        }

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("LineStreamCamera");
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

        private static bool IsNonBg(byte[] px, int width, int height, int col, int row)
        {
            if (row < 0 || row >= height) return false;
            int idx = (row * width + col) * 4;
            return Math.Abs(px[idx]   - BgR8) +
                   Math.Abs(px[idx+1] - BgG8) +
                   Math.Abs(px[idx+2] - BgB8) > SnapshotCoverage.Tolerance;
        }

        private static int FindLineCenterRow(byte[] px, int width, int height, int col)
        {
            int center = height / 2, seed = -1;
            for (int d = 0; d <= height / 2; d++)
            {
                if (IsNonBg(px, width, height, col, center - d)) { seed = center - d; break; }
                if (IsNonBg(px, width, height, col, center + d)) { seed = center + d; break; }
            }
            if (seed < 0) return -1;
            int top = seed; while (top - 1 >= 0     && IsNonBg(px, width, height, col, top - 1)) top--;
            int bot = seed; while (bot + 1 < height && IsNonBg(px, width, height, col, bot + 1)) bot++;
            return (top + bot) / 2;
        }

        private static int MeasureBandWidth(byte[] px, int width, int height, int col)
        {
            int center = height / 2, seed = -1;
            for (int d = 0; d <= height / 2; d++)
            {
                if (IsNonBg(px, width, height, col, center - d)) { seed = center - d; break; }
                if (IsNonBg(px, width, height, col, center + d)) { seed = center + d; break; }
            }
            if (seed < 0) return 0;
            int top = seed; while (top - 1 >= 0     && IsNonBg(px, width, height, col, top - 1)) top--;
            int bot = seed; while (bot + 1 < height && IsNonBg(px, width, height, col, bot + 1)) bot++;
            return bot - top + 1;
        }
    }
}
