// Line-paint and vertex-layout GPU/visual acceptance tests. Split by the CS0104 `CameraProperties` and bare
// `Object` collisions: this file holds the UnityEngine.Rendering importers that also import System.
//
// Contents:
//   LineStreamLayoutTests           — canonical vertex layout + LineWidthColor flip teeth.
//   LinePaintSnapshotTests          — acceptance snapshot tests for line paint GPU behavior (Teeth #3, #4, #5).
//   LineProbeSymmetrySnapshotTests  — Same load-bearing production-seam fixture shape as LineDashSnapshotTests, for _Width/_LineOffset arriving in device px via MaterialFactory.BindDevicePixelFloat.

using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using MapRenderer.Core.Geometry;
using MapRenderer.Unity.Rendering.Meshing;
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;
using System.Globalization;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Style;

namespace MapRenderer.Tests.Visual
{
    // Unity-only: render + Mesh-upload tests for the canonical vertex layout.
    // NOT included in Tools/core-tests/core-tests.csproj.

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineStreamLayoutTests — canonical vertex layout + LineWidthColor flip teeth.
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Canonical vertex layout + LineWidthColor teeth. Tooth A renders through the REAL stream-3 interleave:
    /// a baked CYAN colour must read cyan-dominant, and WidthScale=2 must double the band. A {WidthScale;
    /// Color} struct order would read MAGENTA and a unit width. Tooth B uploads a fill and a line mesh and
    /// asserts no "non-standard order" warning (GPU-independent). Camera: top-down ortho 512×512, Y=200,
    /// orthoSize=70, 0.2734 m/px.
    /// </summary>
    [TestFixture]
    public class LineStreamLayoutTests : BaseTestFixture
    {
        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32 = new Color32(26, 28, 38, 255);

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
            Track(cameraGo);

            // CYAN vertex colour (0,1,1,1) with NON-UNIT WidthScale=2 baked into stream-3.
            // _BaseColor=white so the baked vColor is the only colour signal.
            var pts  = new List<double2> { new double2(-40, 0), new double2(40, 0) };
            var mesh = Track(SyntheticLineMesh.BuildFromPoints(pts, new Vector4(0f, 1f, 1f, 1f), 2f,
                JoinType.Miter, CapType.Butt));

            var lineGo = Track(new GameObject("CyanLine"));
            lineGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            var shader = Shader.Find("Map/Line") ?? Shader.Find("Sprites/Default");
            var mat = Track(new Material(shader) { name = "CyanLineMat" });
            mat.SetFloat("_Width",          6f);
            mat.SetFloat("_WidthIsPixels",  0f);
            mat.SetColor("_BaseColor",       Color.white);  // identity → vColor is the signal
            mat.SetFloat("_Opacity",        1f);
            lineGo.AddComponent<MeshRenderer>().sharedMaterial = mat;

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);
                snap.WritePng("line-stream3-cyan.png");

                // Find the line band centre on the centre column and read that pixel.
                int row = FindLineCenterRow(snap.Pixels, SnapW / 2);
                if (row < 0)
                {
                    Assert.Fail("Could not find line band — line may not render in batch mode.");
                }

                Color32 centrePx = snap.Pixels[SnapW / 2, row];
                byte r = centrePx.r, g = centrePx.g, b = centrePx.b;
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
            Track(cameraGo);

            var pts = new List<double2> { new double2(-40, 0), new double2(40, 0) };
            // Same base _Width; the only difference is the WidthScale baked into stream-3.
            var meshUnit   = Track(SyntheticLineMesh.BuildFromPoints(pts, new Vector4(1f, 1f, 1f, 1f), 1f));
            var meshDouble = Track(SyntheticLineMesh.BuildFromPoints(pts, new Vector4(1f, 1f, 1f, 1f), 2f));

            var goUnit   = MakeLineGo(meshUnit,   "WidthScale1");
            var goDouble = MakeLineGo(meshDouble, "WidthScale2");
            goDouble.go.SetActive(false); // render one at a time on the same camera

            using var snapUnit   = new SnapshotRenderer(SnapW, SnapH);
            using var snapDouble = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snapUnit.Render(camera);

                int widthUnit = MeasureBandWidth(snapUnit.Pixels, SnapW / 2);

                goUnit.go.SetActive(false);
                goDouble.go.SetActive(true);
                snapDouble.Render(camera);
                int widthDouble = MeasureBandWidth(snapDouble.Pixels, SnapW / 2);

                Debug.Log($"[LineStreamLayout] band width: unit={widthUnit}px, double={widthDouble}px " +
                          $"(expect ratio ≈ 2.0)");

                float ratio = (float)widthDouble / widthUnit;
                // ±AA tolerance: a few px of feather on each band; ratio must be clearly near 2, never near 1.
                Assert.That(ratio, Is.InRange(1.6f, 2.4f),
                    $"WidthScale=2 baked into stream-3 must roughly DOUBLE the band width " +
                    $"(unit={widthUnit}px, double={widthDouble}px, ratio={ratio:F2}). " +
                    "A ratio near 1.0 means WidthScale did not reach the shader — stream-3 interleave regressed.");
            }
            finally
            {
                goUnit.Destroy();
                goDouble.Destroy();
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

            try
            {
                // Fill upload via the live StyledFillTileBuilder path.
                var (go, _) = FillSceneHelper.BuildFillGo();
                Track(go);

                // Line upload via the live StyledLineTileBuilder path.
                Mesh lineMesh = Track(SyntheticLineMesh.BuildFromPoints(
                    new List<double2> { new double2(-40, 0), new double2(40, 0) },
                    JoinType.Miter, CapType.Butt));

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
                    "warning — both descriptor arrays are in ascending VertexAttribute order. " +
                    "Captured offenders:\n" + offenders);
            }
            finally
            {
                Application.logMessageReceived -= handler;
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
            mat.SetColor("_BaseColor",       new Color(0.9f, 0.5f, 0.1f, 1f));
            mat.SetFloat("_Opacity",        1f);
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

        private static bool IsNonBg(Frame frame, int col, int row)
        {
            if (row < 0 || row >= frame.Height) return false;
            Color32 px = frame[col, row];
            return Math.Abs(px.r - Bg32.r) +
                   Math.Abs(px.g - Bg32.g) +
                   Math.Abs(px.b - Bg32.b) > SnapshotCoverage.Tolerance;
        }

        private static int FindLineCenterRow(Frame frame, int col)
        {
            int height = frame.Height;
            int center = height / 2, seed = -1;
            for (int d = 0; d <= height / 2; d++)
            {
                if (IsNonBg(frame, col, center - d)) { seed = center - d; break; }
                if (IsNonBg(frame, col, center + d)) { seed = center + d; break; }
            }
            if (seed < 0) return -1;
            int top = seed; while (top - 1 >= 0     && IsNonBg(frame, col, top - 1)) top--;
            int bot = seed; while (bot + 1 < height && IsNonBg(frame, col, bot + 1)) bot++;
            return (top + bot) / 2;
        }

        private static int MeasureBandWidth(Frame frame, int col)
        {
            int height = frame.Height;
            int center = height / 2, seed = -1;
            for (int d = 0; d <= height / 2; d++)
            {
                if (IsNonBg(frame, col, center - d)) { seed = center - d; break; }
                if (IsNonBg(frame, col, center + d)) { seed = center + d; break; }
            }
            if (seed < 0) return 0;
            int top = seed; while (top - 1 >= 0     && IsNonBg(frame, col, top - 1)) top--;
            int bot = seed; while (bot + 1 < height && IsNonBg(frame, col, bot + 1)) bot++;
            return bot - top + 1;
        }
    }

    // Unity-only: render tests requiring GPU context (SnapshotRenderer / UnityEngine).
    // NOT included in Tools/core-tests/core-tests.csproj.

    // ───────────────────────────────────────────────────────────────────────────────────
    // LinePaintSnapshotTests — acceptance snapshot tests for line paint GPU behavior (Teeth #3
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Acceptance snapshot tests for line paint, each a uniform change on one mesh: <c>_GapWidth</c> &gt; 0
    /// hollows the centre (Tooth #3), <c>_LineTranslate.y = N</c> shifts the ribbon ≈ N px (#4), and
    /// <c>_LinePattern=1</c> still renders a solid line (#5). Camera: top-down ortho 512×512, Y=200,
    /// orthoSize=70, 0.2734 m/px, over the dark-slate background.
    /// </summary>
    [TestFixture]
    public class LinePaintSnapshotTests : VisualTestFixture
    {
        protected override RenderState State => new RenderState
        {
            AmbientMode  = AmbientMode.Flat,
            AmbientLight = Color.white,
        };

        private const int   SnapW   = 512;
        private const int   SnapH   = 512;
        private const float OrthoSz = 70f;
        private const float CamY    = 200f;

        // Background: distinctive dark slate.
        private static readonly Color BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly Color32 Bg32 = new Color32(26, 28, 38, 255);

        private static float MetersPerPx => 2f * OrthoSz / SnapH; // ≈ 0.2734 m/px

        // Line width and gap sizes in pixels (used for _Width and _GapWidth uniforms).
        // Line is in pixels mode (_WidthIsPixels=1) so 1 px = MetersPerPx meters.
        private const float LineWidthPx = 4f;   // visible casing band width
        private const float GapWidthPx  = 20f;  // large gap → fat hollow center (≈5.5m)

        // ── Camera helper ──────────────────────────────────────────────────────

        /// <param name="yawDeg">Rotation about world +Y after the 90° pitch; 0 keeps screen-up = north. A
        /// non-zero yaw separates the "map" and "viewport" translate anchors.</param>
        private static (GameObject go, Camera camera) BuildCamera(float yawDeg = 0f)
        {
            var go     = new GameObject("LinePaintSnapCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, yawDeg, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = OrthoSz;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;

            // Non-local invariant: MapCamera.SyncToCamera pushes the frame constant for PIXEL widths, and this
            // hand-built camera has no MapCamera, so it pushes MetersPerPx itself. Any fixture that hand-builds
            // a camera must do this too: a 0 renders every width as a plausible 1 px hairline.
            Shader.SetGlobalFloat(
                ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel, MetersPerPx);
            return (go, camera);
        }

        /// <summary>
        /// Build a single horizontal line with the Map/Line shader.
        /// The line runs along world X from -40m to +40m, centered at world origin.
        /// _WidthIsPixels=1, so the shader MEASURES px→world per vertex (there is no _MetersPerPixel uniform
        /// any more).
        /// Returns (GameObject, live material). Caller must DestroyImmediate both.
        /// </summary>
        private static (GameObject go, Material mat) BuildHorizontalLine(
            float widthPx = LineWidthPx, float gapPx = 0f,
            Color? color = null, float linePattern = 0f)
        {
            var pts = new List<double2>
            {
                new double2(-40, 0),
                new double2( 40, 0),
            };
            var mesh = SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt);

            var go = new GameObject("HLine_LinePaintSnap");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var shader = Shader.Find("Map/Line") ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader) { name = "LinePaintSnapMat" };

            // Pixel-width mode so widthPx / gapPx map directly to shader pixels.
            mat.SetFloat("_Width",          widthPx);
            mat.SetFloat("_WidthIsPixels",  1f);
            mat.SetFloat("_GapWidth",       gapPx);
            mat.SetColor("_BaseColor",       color ?? new Color(0.9f, 0.5f, 0.1f, 1f));
            mat.SetFloat("_Opacity",        1f);
            mat.SetVector("_LineTranslate", Vector4.zero);
            mat.SetFloat("_LinePattern",    linePattern);

            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return (go, mat);
        }

        // ── Pixel-column center-row finder ─────────────────────────────────────

        /// <summary>
        /// Find the center row of the non-background line band on a given column.
        /// Returns -1 if no band is found.
        /// </summary>
        private static int FindLineCenterRow(Frame frame, int col)
        {
            int height = frame.Height;
            bool IsNonBg(int row)
            {
                if (row < 0 || row >= height) return false;
                Color32 px = frame[col, row];
                return Math.Abs(px.r - Bg32.r) +
                       Math.Abs(px.g - Bg32.g) +
                       Math.Abs(px.b - Bg32.b) > SnapshotCoverage.Tolerance;
            }

            // Scan from the center outward to find the band.
            int centerRow = height / 2;
            int seed = -1;
            for (int d = 0; d <= height / 2; d++)
            {
                if (IsNonBg(centerRow - d)) { seed = centerRow - d; break; }
                if (IsNonBg(centerRow + d)) { seed = centerRow + d; break; }
            }
            if (seed < 0) return -1;

            // Walk to find band top and bottom.
            int top = seed; while (top - 1 >= 0     && IsNonBg(top - 1)) top--;
            int bot = seed; while (bot + 1 < height && IsNonBg(bot + 1)) bot++;
            return (top + bot) / 2;
        }

        /// <summary>
        /// Test whether the center pixel (col, centerRow) is background.
        /// Distinguishes hollow (gap) vs solid center.
        /// </summary>
        private static bool IsCenterPixelBackground(Frame frame, int col, int row)
        {
            if (row < 0) return true;
            Color32 px = frame[col, row];
            return Math.Abs(px.r - Bg32.r) +
                   Math.Abs(px.g - Bg32.g) +
                   Math.Abs(px.b - Bg32.b) <= SnapshotCoverage.Tolerance;
        }

        // ── Tooth #3: gap-width produces hollow center vs solid center ────────

        [Test]
        public void GapWidth_NonZero_ProducesHollowCenter_ZeroGapIsSolid()
        {
            // The solid render's centre row must be BACKGROUND once the gap is set, while the casing stays
            // visible. A 20 px gap on a 4 px line gives innerFrac ≈ 0.71, a clear discard band.

            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (lineGo, mat) = BuildHorizontalLine(widthPx: LineWidthPx, gapPx: 0f);
            Track(lineGo);
            Track(mat);
            Track(lineGo.GetComponent<MeshFilter>().sharedMesh);

            using var snapSolid  = new SnapshotRenderer(SnapW, SnapH);
            using var snapHollow = new SnapshotRenderer(SnapW, SnapH);
            {
                // Render with gap=0 (solid).
                snapSolid.Render(camera);
                snapSolid.WritePng("line-paint-gap-solid.png");

                // Find the solid line's centerline row. This is the row we will probe
                // in the hollow render to verify the center is discarded.
                int solidCenterRow = FindLineCenterRow(snapSolid.Pixels, SnapW / 2);

                // Verify the solid render has a non-background pixel at the centerline.
                bool solidCenterIsBg = IsCenterPixelBackground(snapSolid.Pixels, SnapW / 2, solidCenterRow);

                // Now set a large gap (no mesh rebuild).
                mat.SetFloat("_GapWidth", GapWidthPx);
                snapHollow.Render(camera);
                snapHollow.WritePng("line-paint-gap-hollow.png");

                // The same row in the hollow render: the centreline (|side| = 0) is inside innerFrac, so it is
                // discarded to background.
                bool hollowCenterIsBg = IsCenterPixelBackground(snapHollow.Pixels, SnapW / 2, solidCenterRow);

                // Also verify that the hollow render is NOT completely blank (casing bands visible).
                var hollowVerdict = SnapshotCoverage.Analyse(snapHollow.Pixels, Bg32);

                Debug.Log($"[LinePaintSnapshotTests] Gap=0: centerIsBg={solidCenterIsBg}, " +
                          $"Gap={GapWidthPx}: centerIsBg={hollowCenterIsBg} at row={solidCenterRow}. " +
                          $"Hollow filledFraction={hollowVerdict.FilledFraction:P2}");

                // gap=0 → center should NOT be background (solid line covers center).
                Assert.IsFalse(solidCenterIsBg,
                    $"gap-width=0: center column at row={solidCenterRow} must NOT be background " +
                    "(solid line). If center is background, the solid path may have a bug.");

                // gap > 0: MapLineForwardPass.hlsl discards |side| < innerFrac, and the centreline has
                // |side| = 0, so its row must be BACKGROUND.
                Assert.IsTrue(hollowCenterIsBg,
                    $"gap-width={GapWidthPx}px: centerline row={solidCenterRow} must be " +
                    "BACKGROUND in the hollow render. The fragment shader should discard pixels " +
                    "where |side| < innerFrac (computed from _GapWidth). " +
                    "If the center is not background, the gap-width inner-clip path in " +
                    "MapLineForwardPass.hlsl is not working (check gapM>1e-6 branch and " +
                    "innerAA = smoothstep(..., absSide - innerFrac) discard logic).");

                // The casing bands must still be visible (proves the line didn't vanish entirely).
                Assert.Greater(hollowVerdict.FilledFraction, 0.002f,
                    $"gap-width={GapWidthPx}px: hollow render must still have visible casing " +
                    $"(filledFraction={hollowVerdict.FilledFraction:P2} must be > 0.2%). " +
                    "With gap=20px and width=4px, the outer casing bands should be visible.");
            }
        }

        // ── Tooth #4: _LineTranslate shifts ribbon position in image pixels ───

        [Test]
        public void LineTranslate_NonZero_ShiftsRibbonSouthByExpectedPixels()
        {
            // Non-obvious why: +y is SOUTH ("negatives indicate up") and Pixels is bottom-up with north up-screen,
            // so the SIGNED delta is −TranslatePx. A magnitude-only check passes a shader whose +y points north.
            // See docs/line-translate-parity-design.md.
            const float TranslatePx = 30f; // pixels to shift (large enough to measure clearly)

            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            // Use a wider line for easier centroid measurement.
            var (lineGo, mat) = BuildHorizontalLine(widthPx: 8f, gapPx: 0f);
            Track(lineGo);
            Track(mat);
            Track(lineGo.GetComponent<MeshFilter>().sharedMesh);

            using var snapBase      = new SnapshotRenderer(SnapW, SnapH);
            using var snapTranslate = new SnapshotRenderer(SnapW, SnapH);
            {
                // Render at default position (translate=0).
                mat.SetVector("_LineTranslate", Vector4.zero);
                snapBase.Render(camera);
                snapBase.WritePng("line-paint-translate-base.png");

                int baseCenterRow = FindLineCenterRow(snapBase.Pixels, SnapW / 2);

                // Apply the translation, no mesh rebuild: _LineTranslate is (x_px, y_px, 0, 0), and y_px shifts
                // along world Z, which is image rows here.
                mat.SetVector("_LineTranslate", new Vector4(0f, TranslatePx, 0f, 0f));
                snapTranslate.Render(camera);
                snapTranslate.WritePng("line-paint-translate-shifted.png");

                int shiftedCenterRow = FindLineCenterRow(snapTranslate.Pixels, SnapW / 2);

                // SIGNED delta. The frame is bottom-left origin, so south (+y) is a DECREASING row.
                int rowDelta = shiftedCenterRow - baseCenterRow;
                const float ExpectedRowShift = -TranslatePx;
                const float Tol = 5f; // ±5px tolerance (AA + discretization)

                Debug.Log($"[LinePaintSnapshotTests] Translate: baseCenterRow={baseCenterRow}, " +
                          $"shiftedCenterRow={shiftedCenterRow}, Δrow={rowDelta} (signed), " +
                          $"expected≈{ExpectedRowShift:F1}px (±{Tol}px)");

                Assert.That((float)rowDelta,
                    Is.InRange(ExpectedRowShift - Tol, ExpectedRowShift + Tol),
                    $"_LineTranslate.y={TranslatePx}px must shift the ribbon center row by " +
                    $"≈{ExpectedRowShift:F1}px (±{Tol}px) — SOUTH, i.e. toward row 0. Got Δrow={rowDelta}px. " +
                    $"A delta of ≈+{TranslatePx} means the sign is inverted (+y treated as north); a delta " +
                    "far larger than this means the screen-pixel→world conversion was skipped.");
            }
        }

        // ── line-translate is a SCREEN-pixel offset regardless of the width's units ──

        /// <summary>
        /// <c>line-translate</c> is screen pixels whatever the width's units, so two renders differing only in
        /// <c>_WidthIsPixels</c> must shift identically. A <c>pxToWorld</c> left at 1.0 for metre widths would
        /// apply the translate as raw METRES (≈3.7× off here). The other fixtures all set pixel widths.
        /// </summary>
        [Test]
        public void LineTranslate_IsScreenPixels_WhetherWidthIsPixelsOrMetres()
        {
            const float TranslatePx = 30f;
            const float WidthPx     = 8f;
            const float Tol         = 5f;

            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            var (lineGo, mat) = BuildHorizontalLine(widthPx: WidthPx, gapPx: 0f);
            Track(lineGo);
            Track(mat);
            Track(lineGo.GetComponent<MeshFilter>().sharedMesh);

            using var snapBase   = new SnapshotRenderer(SnapW, SnapH);
            using var snapPixels = new SnapshotRenderer(SnapW, SnapH);
            using var snapMetres = new SnapshotRenderer(SnapW, SnapH);
            {
                mat.SetVector("_LineTranslate", Vector4.zero);
                snapBase.Render(camera);

                int baseRow = FindLineCenterRow(snapBase.Pixels, SnapW / 2);

                // (1) width in PIXELS — the path every other test in this file exercises.
                mat.SetVector("_LineTranslate", new Vector4(0f, TranslatePx, 0f, 0f));
                snapPixels.Render(camera);
                snapPixels.WritePng("line-translate-width-pixels.png");
                int pixelsRow = FindLineCenterRow(snapPixels.Pixels, SnapW / 2);

                // (2) width in world METRES, sized to render the same ribbon thickness. Same translate.
                mat.SetFloat("_WidthIsPixels", 0f);
                mat.SetFloat("_Width", WidthPx * MetersPerPx);
                snapMetres.Render(camera);
                snapMetres.WritePng("line-translate-width-metres.png");
                int metresRow = FindLineCenterRow(snapMetres.Pixels, SnapW / 2);

                int pixelsDelta = pixelsRow - baseRow;
                int metresDelta = metresRow - baseRow;

                Debug.Log($"[LinePaintSnapshotTests] translate units: baseRow={baseRow}, " +
                          $"pxWidthΔ={pixelsDelta}, metreWidthΔ={metresDelta}, expected≈{-TranslatePx}");

                Assert.That((float)metresDelta, Is.InRange(-TranslatePx - Tol, -TranslatePx + Tol),
                    $"line-translate is defined in SCREEN PIXELS, so a world-metre line-width must shift by " +
                    $"the same ≈{-TranslatePx}px. Got Δrow={metresDelta}. A delta of MAGNITUDE around " +
                    $"{TranslatePx / MetersPerPx:F0} (either sign) means the px→world conversion was skipped " +
                    "and the offset was applied as raw world metres.");

                Assert.That((float)metresDelta, Is.InRange(pixelsDelta - Tol, pixelsDelta + Tol),
                    $"the width's UNITS must not change where line-translate puts the ribbon: " +
                    $"pixels-width Δ={pixelsDelta}, metres-width Δ={metresDelta}.");
            }
        }

        // ── line-translate-anchor: "map" vs "viewport" ────────────────────────

        /// <summary>
        /// The shader must read <c>_LineTranslateAnchor</c>, or <c>"viewport"</c> behaves as <c>"map"</c>.
        /// Non-obvious why: under a default top-down camera the two anchors point the SAME way. The 180° yaw makes
        /// screen-up = world −Z, so a +y offset moves the ribbon UP under "map" and DOWN under "viewport".
        /// </summary>
        [Test]
        public void LineTranslateAnchor_MapAndViewport_MoveTheRibbonOppositeWays()
        {
            const float TranslatePx = 30f;
            const float Tol         = 5f;

            // Yaw 180°: still straight down, but screen-up is now world −Z (south) and screen-right world −X.
            var (cameraGo, camera) = BuildCamera(yawDeg: 180f);
            Track(cameraGo);
            var (lineGo, mat) = BuildHorizontalLine(widthPx: 8f, gapPx: 0f);
            Track(lineGo);
            Track(mat);
            Track(lineGo.GetComponent<MeshFilter>().sharedMesh);

            using var snapBase     = new SnapshotRenderer(SnapW, SnapH);
            using var snapMap      = new SnapshotRenderer(SnapW, SnapH);
            using var snapViewport = new SnapshotRenderer(SnapW, SnapH);
            {
                mat.SetVector("_LineTranslate", Vector4.zero);
                snapBase.Render(camera);

                int baseRow = FindLineCenterRow(snapBase.Pixels, SnapW / 2);

                mat.SetVector("_LineTranslate", new Vector4(0f, TranslatePx, 0f, 0f));

                // "map": +y is SOUTH on the map = world −Z. Screen-up IS −Z here, so the row INCREASES.
                mat.SetFloat("_LineTranslateAnchor", 0f);
                snapMap.Render(camera);
                snapMap.WritePng("line-translate-anchor-map.png");
                int mapRow = FindLineCenterRow(snapMap.Pixels, SnapW / 2);

                // "viewport": +y is screen-down regardless of the map, so the row DECREASES.
                mat.SetFloat("_LineTranslateAnchor", 1f);
                snapViewport.Render(camera);
                snapViewport.WritePng("line-translate-anchor-viewport.png");
                int viewportRow = FindLineCenterRow(snapViewport.Pixels, SnapW / 2);

                int mapDelta      = mapRow - baseRow;
                int viewportDelta = viewportRow - baseRow;

                Debug.Log($"[LinePaintSnapshotTests] anchor (yaw 180°): baseRow={baseRow}, " +
                          $"mapΔ={mapDelta} (expect≈+{TranslatePx}), viewportΔ={viewportDelta} " +
                          $"(expect≈{-TranslatePx})");

                Assert.That((float)mapDelta, Is.InRange(TranslatePx - Tol, TranslatePx + Tol),
                    $"anchor \"map\": +y is south on the MAP, which is screen-up under a 180° yaw, so the row " +
                    $"must increase by ≈{TranslatePx}. Got Δrow={mapDelta}.");

                Assert.That((float)viewportDelta, Is.InRange(-TranslatePx - Tol, -TranslatePx + Tol),
                    $"anchor \"viewport\": +y is screen-down whatever the map is doing, so the row must " +
                    $"decrease by ≈{TranslatePx}. Got Δrow={viewportDelta}. A value matching the \"map\" " +
                    $"delta ({mapDelta}) means _LineTranslateAnchor is being ignored by the shader.");
            }
        }

        // ── Tooth #5: _LinePattern=1 (hook) does NOT blank the line ──────────

        [Test]
        public void LinePattern_HookActive_LineStillRendersNonBlank()
        {
            // With _LinePattern=1 the pattern hook is "active" (pattern layer flagged).
            // The fallback must still render solid _BaseColor — the line must NOT vanish.

            var (cameraGo, camera) = BuildCamera();
            Track(cameraGo);
            // Build solid line, then set _LinePattern=1 after.
            var (lineGo, mat) = BuildHorizontalLine(widthPx: 8f, gapPx: 0f);
            Track(lineGo);
            Track(mat);
            Track(lineGo.GetComponent<MeshFilter>().sharedMesh);

            using var snapNoPattern  = new SnapshotRenderer(SnapW, SnapH);
            using var snapWithPattern = new SnapshotRenderer(SnapW, SnapH);
            {
                // Render without pattern flag (baseline).
                mat.SetFloat("_LinePattern", 0f);
                snapNoPattern.Render(camera);
                snapNoPattern.WritePng("line-paint-pattern-off.png");

                // Render WITH _LinePattern=1 (no mesh rebuild — uniform only).
                mat.SetFloat("_LinePattern", 1f);
                snapWithPattern.Render(camera);
                snapWithPattern.WritePng("line-paint-pattern-on.png");

                // The line must NOT be blank when _LinePattern=1. An 8 px line fills only ~1.6%, so IsBlank
                // cannot judge it; the band-width measurement decides instead.
                int patternBandWidth = FindLineCenterRow(snapWithPattern.Pixels, SnapW / 2);

                // Also measure the no-pattern baseline band width for comparison.
                int baselineBandWidth = FindLineCenterRow(snapNoPattern.Pixels, SnapW / 2);

                var verdict = SnapshotCoverage.Analyse(snapWithPattern.Pixels, Bg32);

                Debug.Log($"[LinePaintSnapshotTests] Pattern hook: " +
                          $"filledFraction={verdict.FilledFraction:P2}, " +
                          $"baselineCenterRow={baselineBandWidth}, patternCenterRow={patternBandWidth}");

                // Primary assertion: the line center row must be visible with _LinePattern=1.
                // patternBandWidth here is actually the CENTER ROW of the band (reusing FindLineCenterRow).
                Assert.GreaterOrEqual(patternBandWidth, 0,
                    "_LinePattern=1 must NOT blank the line. FindLineCenterRow returned -1 " +
                    "(no visible band detected). The hook must fall back to solid line-color " +
                    "(no sprite sampling yet). If this fails, the hook is incorrectly " +
                    "discarding all pixels instead of rendering solid _BaseColor.");

                // Secondary: filled fraction must be greater than a minimal threshold.
                // A 8px-wide line in a 512x512 image fills ≈ 8*512 / (512*512) ≈ 1.6%.
                Assert.Greater(verdict.FilledFraction, 0.004f,
                    $"_LinePattern=1: rendered filled fraction ({verdict.FilledFraction:P2}) must be > 0.4% " +
                    "— the line ribbon should be visible. This fails if the hook blanks the output.");
            }
        }
    }

    // Unity EditMode only — real MapCamera + Camera/RenderTexture, off-screen GPU render + CPU readback.
    // NOT registered in Tools/core-tests/core-tests.csproj.
    //
    // The RENDERED teeth for MapPixelsToWorld's DIRECTION SYMMETRY, measured at the source.
    //
    // Non-obvious why: a ONE-SIDED probe steps along the SIGNED dirWS, so it carries its own foreshortening,
    // and opposite directions get rulers (1+e)/(1−e) apart, e = 0.02·tan(fov/2)·dot(dirWS, fwd). A station's
    // two ribbon vertices carry opposite extrudeN, so they would hit exactly that. The edge offsets would be
    // +H·k(1+e) and −H·k(1−e): their world separation stays 2Hk, but the centre moves e·H·k. The screen width
    // is not a null (the projection is nonlinear), so these teeth assert the RATIO of the world offsets.
    // Like LineDashSnapshotTests, every arm drives the PRODUCTION seam (style JSON → RenderLayerSet →
    // ApplyZoom), so _Width and _LineOffset arrive in DEVICE px, and the real MapCamera pushes the frame constant.

    // ───────────────────────────────────────────────────────────────────────────────────
    // LineProbeSymmetrySnapshotTests — Same load-bearing production-seam fixture shape as LineDashSnapshotTests
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class LineProbeSymmetrySnapshotTests
    {
        private const int    Size      = 512;
        private const double Zoom      = 8.0;

        /// <summary>T7's pose. across·fwd = sin 55° = 0.81915, so e = 0.02·tan30°·0.81915 = 0.009458753 and
        /// the two rulers sit (1+e)/(1−e) = 1.0190982 apart.</summary>
        private const double TiltDeg = 55.0;

        private const double RoadHalfLengthM = 80_000.0;
        private const double RoadStationM    =  2_000.0;

        // T4's receding road reaches both frame edges at tilt 55 (ground z ≈ −75 km to +890 km; the horizon
        // is off-screen). Stations are cosmetic: widthWorld is constant, so its chord is exact.
        private const double RecedingRoadFromM    =  -90_000.0;
        private const double RecedingRoadToM      = 950_000.0;
        private const double RecedingRoadStationM =   10_000.0;

        // ── The scene ────────────────────────────────────────────────────────────────────────────

        private sealed class ProbeScene : IDisposable
        {
            public TiltedGroundScene Scene;
            public RenderLayerSet    Layers;

            public Camera    UnityCamera => Scene.UnityCamera;
            public MapCamera MapCam      => Scene.MapCam;

            /// <summary>The styled line material, straight off the production render layer.</summary>
            public Material Material => Layers[0].Material;

            public void Dispose()
            {
                Layers?.Dispose();
                Scene?.Dispose();
            }
        }

        /// <summary>line-width and line-offset are plain CONSTANTS on purpose: a feature-dependent value is
        /// bound as a constant 1 through the same device-px path, which would silently make every
        /// measurement here about something else.</summary>
        private static string StyleJson(double styledWidthPx, double lineOffsetPx)
        {
            string width  = styledWidthPx.ToString("R", CultureInfo.InvariantCulture);
            string offset = lineOffsetPx.ToString("R", CultureInfo.InvariantCulture);
            return
                "{ \"version\": 8," +
                "  \"sources\": { \"s\": { \"type\": \"vector\", \"tiles\": [\"https://x/{z}/{x}/{y}.pbf\"] } }," +
                "  \"layers\": [" +
                "    { \"id\": \"probe-road\", \"type\": \"line\", \"source\": \"s\", \"source-layer\": \"l\"," +
                "      \"paint\": { \"line-color\": [\"rgba\", 242, 153, 38, 1]," +
                "                   \"line-width\": " + width + "," +
                "                   \"line-offset\": " + offset + " } }" +
                "  ] }";
        }

        /// <summary>Builds the styled <see cref="RenderLayerSet"/> and its preconditions FIRST, and
        /// <see cref="TiltedGroundScene.Create"/> LAST. Non-local invariant: <c>Create</c> mutates
        /// PROCESS-GLOBAL state that only <c>Dispose</c> restores, so a precondition failing after it would
        /// leak that state into every later lit fixture in the batch. No precondition reads the scene.</summary>
        private static ProbeScene BuildScene(double tiltDeg, double styledWidthPx, double lineOffsetPx)
        {
            const double Dpr = 1.0;

            var set = new RenderLayerSet();
            set.Build(StyleParser.Parse(StyleJson(styledWidthPx, lineOffsetPx)), Zoom,
                      MapMaterialSetTestUtil.Load());
            Assert.That(set.Count, Is.EqualTo(1), "the probe-road style must yield exactly one render layer.");
            Assert.IsNotNull(set[0].Material, "Map/Line base material must be configured for this fixture.");

            set.ApplyZoom(new StyleFrameInputs(Zoom, Dpr, 0.0));

            Material mat = set[0].Material;
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.DashCount), Is.EqualTo(0f),
                "precondition: _DashCount must be 0. A dashed band has ON/OFF runs, and the coverage " +
                "integral below would sum a dash boundary as if it were the ribbon edge.");
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.Width),
                Is.EqualTo((float)(styledWidthPx * Dpr)).Within(1e-3f),
                $"precondition: _Width must reach the shader in DEVICE px ({styledWidthPx}×dpr).");
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.LineOffset),
                Is.EqualTo((float)(lineOffsetPx * Dpr)).Within(1e-3f),
                $"precondition: _LineOffset must reach the shader in DEVICE px ({lineOffsetPx}×dpr).");
            Assert.That(mat.GetFloat(ShaderProperties.Line.PropertyId.WidthIsPixels), Is.EqualTo(1f),
                "precondition: _WidthIsPixels must be 1, or pxToWorld is a literal 1.0 and MapPixelsToWorld " +
                "is never called for the width family — the whole measurement would be inert.");

            // The AA straddle must be LIVE: the half-sum estimator is exact only for a clamped one-device-pixel
            // ramp, which AA-off or a hairline keyword would reshape.
            Assert.That(mat.IsKeywordEnabled("_EDGE_ANTIALIASING_OFF"), Is.False,
                "precondition: the AA straddle must be live — the estimator assumes a 1 device-px ramp.");
            Assert.That(mat.IsKeywordEnabled("_HAIRLINE_SOLID_CORE"), Is.False,
                "precondition: _HAIRLINE_SOLID_CORE clamps the rendered band and rescales coverage.");
            Assert.That(mat.IsKeywordEnabled("_HAIRLINE_HARD"), Is.False,
                "precondition: _HAIRLINE_HARD narrows the ramp toward a step.");

            var scene = TiltedGroundScene.Create(new TiltedGroundSceneConfig { TiltDegrees = tiltDeg });

            return new ProbeScene
            {
                Scene  = scene,
                Layers = set,
            };
        }

        /// <summary>Builds the east–west road, renders it once, and hands the caller the scene and the raw
        /// pixels. Everything the measurement needs is read off that one render.
        ///
        /// <para>ORDERING: <see cref="TiltedGroundScene.Create"/> sets the lit ambient up INSIDE the scene
        /// construction <c>BuildScene</c> calls. Nothing between the two reads ambient state.</para></summary>
        private static void WithRenderedRoad(
            double tiltDeg, double styledWidthPx, double lineOffsetPx, string pngName,
            Action<ProbeScene, Frame> measure)
        {
            using var snap  = new SnapshotRenderer(Size, Size);
            using var probe = BuildScene(tiltDeg, styledWidthPx, lineOffsetPx);

            using var bag = new ObjectDisposalBag();
            var pts = new List<double2>();
            for (double x = -RoadHalfLengthM; x <= RoadHalfLengthM + 1e-6; x += RoadStationM)
                pts.Add(new double2(x, 0.0));
            Mesh mesh = bag.Track(SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt));
            var go = bag.Track(new GameObject("Probe_Road"));
            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = probe.Material;
            {
                probe.Scene.Render(snap);
                snap.WritePng(pngName);

                // At heading 0, screen-y depends on world z alone, so a probe row inverts to one z on the
                // constant-z ribbon edges. Asserted rather than assumed.
                Vector3 originSp = probe.UnityCamera.WorldToScreenPoint(Vector3.zero);
                Vector3 refSp    = probe.UnityCamera.WorldToScreenPoint(new Vector3(10_000f, 0f, 0f));
                Assert.That(originSp.y, Is.EqualTo(refSp.y).Within(0.05),
                    "precondition: the road's centreline must project onto a single screen ROW, or a " +
                    "silhouette screen-y does not invert to a single world z.");

                measure(probe, snap.Pixels);
            }
        }

        /// <summary>The T4 variant: a road running NORTH–SOUTH, i.e. away from the camera, so one render
        /// carries a wide range of view depths on a band whose across-axis is world east — perpendicular to
        /// the view azimuth, hence unforeshortened and vertical on screen at the centre column. Shares
        /// <see cref="BuildScene"/> and the lighting recipe with <see cref="WithRenderedRoad"/>; only the
        /// geometry and the centreline precondition differ.</summary>
        private static void WithRenderedRecedingRoad(
            double tiltDeg, double styledWidthPx, string pngName, Action<ProbeScene, Frame> measure)
        {
            using var snap  = new SnapshotRenderer(Size, Size);
            using var probe = BuildScene(tiltDeg, styledWidthPx, lineOffsetPx: 0.0);

            using var bag = new ObjectDisposalBag();
            var pts = new List<double2>();
            for (double z = RecedingRoadFromM; z <= RecedingRoadToM + 1e-6; z += RecedingRoadStationM)
                pts.Add(new double2(0.0, z));
            Mesh mesh = bag.Track(SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt));
            var go = bag.Track(new GameObject("Probe_RecedingRoad"));
            go.AddComponent<MeshFilter>().sharedMesh       = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = probe.Material;
            {
                probe.Scene.Render(snap);
                snap.WritePng(pngName);

                // At heading 0 the x = 0 road is the vertical centre column, so a HORIZONTAL cut's coverage
                // integral is the rendered width. Asserted, not assumed.
                Vector3 nearSp = probe.UnityCamera.WorldToScreenPoint(new Vector3(0f, 0f, 0f));
                Vector3 farSp  = probe.UnityCamera.WorldToScreenPoint(new Vector3(0f, 0f, 200_000f));
                Assert.That(nearSp.x, Is.EqualTo(farSp.x).Within(0.05),
                    "precondition: the receding road must project onto a single screen COLUMN, or a " +
                    "horizontal cut is not perpendicular to it and the integral is not the width.");

                measure(probe, snap.Pixels);
            }
        }

        // ── The measurement primitive: a per-column coverage integral on a PINNED lattice ─────────
        //
        // Sample j reports coverage at SCREEN-Y j + 0.5, bottom-up with no flip; a HIGHER row is the FAR edge.
        //
        // Non-obvious why: per column and edge, take an anchor ~4 px inside (coverage ≥ 0.99), a sample ~4 px
        // outside (≤ 0.01), and S = Σ coverage over the CLOSED window, ANCHOR INCLUDED. For a clamped
        // one-device-pixel ramp, Σ_{j≥a} clamp(P − j − 0.5, 0, 1) telescopes to P − a − 0.5 at every phase:
        //     paddedSilhouetteScreenY(far)  = (jAnchor + 0.5) + S_far
        //     paddedSilhouetteScreenY(near) = (jAnchor + 0.5) − S_near
        // Excluding the anchor lands half a pixel short; T-S1c clause 1 pins which surface this recovers.
        // A 0.5-CROSSING estimator is biased by up to 0.086 px, identically in every column at heading 0.
        // The plateau is LOCAL per edge and column: the PBR shading differs between edges at ~78 km and
        // ~200 km, and a global plateau's 0.5 % error sums to 0.30 px, the whole budget.

        /// <summary>Default anchor inset — inside / outer sample outside, from the detected edge. Non-obvious
        /// why: it is only a cap, because the two half-sums must not share a row and a cross-view band renders
        /// <c>cos θ</c> thin (T-S2's is 8 rows), so each arm derives its own via <see cref="InsetForBand"/>.
        /// Widening the disjointness assertion instead would void the estimator.</summary>
        private const int EdgeInsetPx = 4;
        private const int PlateauRows = 6;  // tight, running INWARD from the anchor, the anchor included

        /// <summary>The largest inset (capped at <see cref="EdgeInsetPx"/>) that still leaves the two
        /// half-sum windows DISJOINT on a band spanning <paramref name="bottomRow"/>..<paramref name="topRow"/>:
        /// disjointness needs <c>2·inset &lt; top − bottom</c>. <see cref="AssertWindowIsWellFormed"/> checks
        /// the other two conditions, so an inset too small for the ramp fails instead of biasing the sum.</summary>
        private static int InsetForBand(int bottomRow, int topRow)
            => math.min(EdgeInsetPx, (topRow - bottomRow - 1) / 2);

        private const int CentreColumn = 256;

        // ── HOW WIDE THE COLUMN SWEEP MAY BE, and why it is not the whole band ───────────────────
        //
        // Non-obvious why: a tilted arm sweeps only ±10 columns, because off-centre by `sx` a world step along ±Z
        // changes depth, so the projection slides radially and refPx gains ≈ |sx|·e px. The silhouettes bow
        // with |sx|, and R varies about quadratically (3.4e-6 px⁻² at T-S1, 6.6e-5 at T-S2), because one
        // column cuts the two edges at different road x. Within ±10 columns the residual is 3.4e-4, far
        // under ±0.005. The TOP-DOWN arm has e = 0, so it keeps the full 200-column sweep.
        private const int TiltedSweepHalfWidth = 10;
        private const int TopDownSweepFrom     = 156;  // the central 200 columns, clear of the butt caps
        private const int TopDownSweepTo       = 355;

        private readonly struct ColumnSilhouettes
        {
            /// <summary>Padded-silhouette SCREEN-Y of the far (up-screen, north) edge.</summary>
            public readonly double FarScreenY;

            /// <summary>Padded-silhouette SCREEN-Y of the near (down-screen, south) edge.</summary>
            public readonly double NearScreenY;

            public ColumnSilhouettes(double farScreenY, double nearScreenY)
            {
                FarScreenY = farScreenY; NearScreenY = nearScreenY;
            }

            public double SeparationPx => FarScreenY - NearScreenY;
        }

        /// <summary>The most saturated pixel anywhere in frame — a coarse plateau, used ONLY to find the
        /// band's rows. Every summed coverage value uses a local per-edge plateau instead.</summary>
        private static float3 CoarsePlateau(Frame pixels, float3 background)
        {
            float3 best     = background;
            float  bestDist = 0f;
            for (int row = 0; row < Size; row++)
            for (int column = 0; column < Size; column++)
            {
                float3 c    = PixelCoverage.SampleLinear(pixels, column, row);
                float  dist = math.distancesq(c, background);
                if (dist > bestDist) { bestDist = dist; best = c; }
            }
            return best;
        }

        private static ColumnSilhouettes MeasureColumn(
            Frame pixels, int column, float3 background, float3 coarsePlateau)
            => MeasureColumn(pixels, column, background, coarsePlateau, EdgeInsetPx);

        /// <summary>The band's contiguous ≥0.5-coverage row run on one column. Split out of
        /// <see cref="MeasureColumn"/> so an arm can size its anchor inset from the band it is about to
        /// measure, with one implementation of "where the band is".</summary>
        private static (int bottomRow, int topRow) BandRowSpan(
            Frame pixels, int column, float3 background, float3 coarsePlateau)
        {
            int topRow = -1, bottomRow = -1, covered = 0;
            for (int row = 0; row < Size; row++)
            {
                if (PixelCoverage.CoverageAt(pixels, column, row, background, coarsePlateau) < 0.5f)
                    continue;
                if (bottomRow < 0) bottomRow = row;
                topRow = row;
                covered++;
            }
            Assert.That(bottomRow, Is.GreaterThanOrEqualTo(0),
                $"column {column}: no rows reach 0.5 coverage — the band did not render here.");
            Assert.That(covered, Is.EqualTo(topRow - bottomRow + 1),
                $"column {column}: the ≥0.5 coverage run must be CONTIGUOUS ({covered} covered rows spanning " +
                $"{bottomRow}..{topRow}). A gap means something other than one solid band is in frame.");
            return (bottomRow, topRow);
        }

        private static ColumnSilhouettes MeasureColumn(
            Frame pixels, int column, float3 background, float3 coarsePlateau, int edgeInsetPx)
        {
            var (bottomRow, topRow) = BandRowSpan(pixels, column, background, coarsePlateau);

            int anchorFar  = topRow    - edgeInsetPx;
            int outerFar   = topRow    + edgeInsetPx;
            int anchorNear = bottomRow + edgeInsetPx;
            int outerNear  = bottomRow - edgeInsetPx;

            // A sample shared by both half-sums adds a pixel per shared row. The inset comes from InsetForBand,
            // so this fires only when a band is too thin for any inset.
            Assert.That(anchorNear, Is.LessThan(anchorFar),
                $"column {column}: the two ±{edgeInsetPx} px windows must be DISJOINT; the band spans " +
                $"{bottomRow}..{topRow}, which is too thin to inset both anchors.");

            float3 plateauFar = PixelCoverage.PlateauOnColumn(
                pixels, column,
                math.max(anchorFar - (PlateauRows - 1), bottomRow + 1), anchorFar, background);
            float3 plateauNear = PixelCoverage.PlateauOnColumn(
                pixels, column,
                anchorNear, math.min(anchorNear + (PlateauRows - 1), topRow - 1), background);

            double sFar = 0.0;
            for (int row = anchorFar; row <= outerFar; row++)
                sFar += PixelCoverage.CoverageAt(pixels, column, row, background, plateauFar);

            double sNear = 0.0;
            for (int row = outerNear; row <= anchorNear; row++)
                sNear += PixelCoverage.CoverageAt(pixels, column, row, background, plateauNear);

            AssertWindowIsWellFormed(pixels, column, "far",  anchorFar,  outerFar,  background, plateauFar);
            AssertWindowIsWellFormed(pixels, column, "near", anchorNear, outerNear, background, plateauNear);

            return new ColumnSilhouettes(
                farScreenY:  (anchorFar  + 0.5) + sFar,
                nearScreenY: (anchorNear + 0.5) - sNear);
        }

        /// <summary>The estimator's two standing assumptions, checked rather than assumed: the anchor is
        /// fully covered and the outer sample is fully clear, so the whole ramp lies inside the window.</summary>
        private static void AssertWindowIsWellFormed(
            Frame pixels, int column, string edge, int anchor, int outer, float3 background, float3 plateau)
        {
            float atAnchor = PixelCoverage.CoverageAt(pixels, column, anchor, background, plateau);
            float atOuter  = PixelCoverage.CoverageAt(pixels, column, outer,  background, plateau);
            if (atAnchor >= 0.99f && atOuter <= 0.01f) return;

            int from = math.min(anchor, outer), to = math.max(anchor, outer);
            float[] profile = PixelCoverage.CoverageProfileOnColumn(
                pixels, column, from, to, background, plateau);
            Assert.Fail(
                $"column {column}, {edge} edge: the coverage window [{from}..{to}] is not well formed — " +
                $"anchor[{anchor}] = {atAnchor:F4} (want ≥ 0.99), outer[{outer}] = {atOuter:F4} " +
                $"(want ≤ 0.01). The half-sum estimator is exact only when the whole ramp lies strictly " +
                $"inside the window. Profile: {PixelCoverage.FormatProfile(profile, from)}");
        }

        /// <summary>World z of the ground line that projects onto <paramref name="screenY"/>, plus the local
        /// scale there in metres per screen pixel (a central difference across one pixel).</summary>
        private static (double worldZ, double metresPerPixel) SolveEdge(
            Camera cam, double screenY, double loMetres, double hiMetres)
        {
            double z     = GroundRowSolver.SolveWorldZForRow(cam, screenY,        loMetres, hiMetres);
            double above = GroundRowSolver.SolveWorldZForRow(cam, screenY + 0.5,  loMetres, hiMetres);
            double below = GroundRowSolver.SolveWorldZForRow(cam, screenY - 0.5,  loMetres, hiMetres);
            return (z, above - below);
        }

        // ── T-S1: the ribbon's two edges must be equidistant from the centreline ─────────────────

        private const double TS1WidthPx = 120.0;

        /// <summary>
        /// <b>T-S1.</b> An unoffset ribbon's two padded silhouettes must sit at equal WORLD distance either
        /// side of the centreline: <c>R = zFar / |zNear| == 1</c>. It pins the <c>cos θ</c> world-width
        /// framing (a per-vertex width reads 125.77 px against [68, 73]) and the band's uniformity; R is a
        /// coarse bound that catches a sign flip or an order-1 asymmetry.
        ///
        /// <para>Limitation: the styled width uses the one frame constant, so R == 1 holds for it
        /// unconditionally. Only the per-vertex AA pad still takes opposite-signed probes, and a one-sided
        /// probe moves R by just <c>2·e·p/(H+p)</c> ≈ 2.7e-4 here, 18× under ±0.005. A pad-sized effect needs
        /// a 1–2 px band, which this ±4 px half-sum estimator cannot measure, so no render pins direction
        /// symmetry at pad amplitude.</para>
        /// </summary>
        [Test]
        public void RibbonEdges_AreEquidistantFromTheCentreline_UnderTilt()
        {
            WithRenderedRoad(TiltDeg, TS1WidthPx, lineOffsetPx: 0.0, "s111-ts1-symmetry-tilt55.png",
                (scene, pixels) =>
                {
                    float3 background = PixelCoverage.BackgroundLinear(pixels);
                    float3 coarse     = CoarsePlateau(pixels, background);
                    Assert.That(math.distance(coarse, background), Is.GreaterThan(0.02f),
                        "T-S1: the road did not render — nothing is measurable.");

                    var centre = MeasureColumn(pixels, CentreColumn, background, coarse);
                    var (zFar,  farMetresPerPx)  =
                        SolveEdge(scene.UnityCamera, centre.FarScreenY,  -60_000.0, +60_000.0);
                    var (zNear, nearMetresPerPx) =
                        SolveEdge(scene.UnityCamera, centre.NearScreenY, -60_000.0, +60_000.0);

                    TestContext.WriteLine(
                        $"T-S1: centre column silhouettes screen-y {centre.FarScreenY:F4} / " +
                        $"{centre.NearScreenY:F4} (separation {centre.SeparationPx:F4} px); world z " +
                        $"{zFar:F1} / {zNear:F1} m; local scale {farMetresPerPx:F1} / {nearMetresPerPx:F1} m/px");

                    // Non-obvious why: a styled px width fixes a WORLD width at the look-at, and this cross-view
                    // road foreshortens to 120·cos 55° + 1 = 69.83 px to first order. The ~0.9 px excess is
                    // expected: the edges and their AA pads sit at different depths (~78 km and ~200 km).
                    Assert.That(centre.SeparationPx, Is.InRange(68.0, 73.0),
                        $"T-S1 framing: the padded band renders {centre.SeparationPx:F2} px thick, expected " +
                        "in [68, 73] (120·cos 55° + 1 = 69.83 to first order, 70.73 measured). A reading " +
                        "near 121 means the band holds a constant DEVICE width under tilt, which is wrong: " +
                        "a styled width fixes a WORLD width. If the fixture will not frame, RE-DERIVE from the " +
                        "pose; never widen the window to reach it.");

                    // Only ±10 columns (see TiltedSweepHalfWidth). The sweep is a uniformity PRECONDITION, not
                    // an error bar: every column lands on the same sub-pixel phase.
                    var ratios = new List<double>();
                    double minFar = double.MaxValue, maxFar = double.MinValue;
                    double minNear = double.MaxValue, maxNear = double.MinValue;
                    for (int column = CentreColumn - TiltedSweepHalfWidth;
                         column <= CentreColumn + TiltedSweepHalfWidth; column++)
                    {
                        var s = MeasureColumn(pixels, column, background, coarse);
                        double far  = GroundRowSolver.SolveWorldZForRow(
                            scene.UnityCamera, s.FarScreenY,  -60_000.0, +60_000.0);
                        double near = GroundRowSolver.SolveWorldZForRow(
                            scene.UnityCamera, s.NearScreenY, -60_000.0, +60_000.0);
                        ratios.Add(far / math.abs(near));
                        minFar  = math.min(minFar,  s.FarScreenY);  maxFar  = math.max(maxFar,  s.FarScreenY);
                        minNear = math.min(minNear, s.NearScreenY); maxNear = math.max(maxNear, s.NearScreenY);
                    }

                    double minRatio = double.MaxValue, maxRatio = double.MinValue, sum = 0.0;
                    foreach (double r in ratios)
                    {
                        minRatio = math.min(minRatio, r);
                        maxRatio = math.max(maxRatio, r);
                        sum += r;
                    }
                    double ratio = sum / ratios.Count;

                    TestContext.WriteLine(
                        $"T-S1: {ratios.Count} columns swept; R ∈ [{minRatio:F6}, {maxRatio:F6}], mean " +
                        $"{ratio:F6}; absolute silhouette spread far {maxFar - minFar:F3} px, near " +
                        $"{maxNear - minNear:F3} px");

                    Assert.That(maxRatio - minRatio, Is.LessThan(0.002),
                        $"T-S1 uniformity: R varies {maxRatio - minRatio:F6} over ±{TiltedSweepHalfWidth} " +
                        "columns, where the modelled residual of the radial refPx term is 3.4e-4 and " +
                        "per-column quantisation is ~3e-4. More than that means the band is not uniform " +
                        "where it is being measured, and the derived expectation does not apply.");

                    // A COARSE SANITY BOUND only (see the summary): a pass is no evidence the w-ratio is intact.
                    Assert.That(ratio, Is.EqualTo(1.0).Within(0.005),
                        $"GROSS ASYMMETRY (the width path cannot cause this — see the summary): on " +
                        $"the CENTRE COLUMN the ribbon's far (north) silhouette sits " +
                        $"{zFar:F1} m from the centreline and its near (south) silhouette " +
                        $"{math.abs(zNear):F1} m, a ratio of {zFar / math.abs(zNear):F6}; the asserted value " +
                        $"is the MEAN over the swept columns, {ratio:F6} — do not divide the two world " +
                        "figures above and expect it. The two edges of one band must be equidistant. " +
                        "MapPixelsToWorld " +
                        "probes ONE-SIDED along the signed dirWS, so the far edge's probe steps AWAY from " +
                        "the camera and measures a span carrying its own foreshortening (ruler ×(1+e)) " +
                        "while the near edge's steps toward it (×(1−e)); e = 0.02·tan(fov/2)·(across·fwd) = " +
                        "0.0094588 here, so R = (1+e)/(1−e) = 1.019098. Multiplying the measured span by " +
                        "clipRef.w/clipCenter.w divides that factor back out identically for both signs.");
                });
        }

        // ── T-S1c: what surface the estimator recovers, and that the fix is inert where e ≡ 0 ────

        /// <summary>
        /// <b>T-S1c — calibration and control.</b> The same fixture TOP-DOWN, where the padded silhouettes sit
        /// at ±(W/2 + 0.5) px. Clause 1: the half-sum reads 121.0, the PADDED silhouette (120.0 means a dropped
        /// anchor, 122.0 a doubly counted one). Clause 2: e ≡ 0 here, so R reads 1.000. Limitation: both edges
        /// share one depth, so a common error such as the index→screen-y +0.5 cancels in both clauses.
        /// </summary>
        [Test]
        public void Estimator_RecoversThePaddedSilhouette_AndTheFixIsInert_TopDown()
        {
            WithRenderedRoad(0.0, TS1WidthPx, lineOffsetPx: 0.0, "s111-ts1c-calibration-topdown.png",
                (scene, pixels) =>
                {
                    float3 background = PixelCoverage.BackgroundLinear(pixels);
                    float3 coarse     = CoarsePlateau(pixels, background);
                    Assert.That(math.distance(coarse, background), Is.GreaterThan(0.02f),
                        "T-S1c: the road did not render — nothing is measurable.");

                    var separations = new List<double>();
                    var ratios      = new List<double>();
                    double minFar = double.MaxValue, maxFar = double.MinValue;
                    double minNear = double.MaxValue, maxNear = double.MinValue;
                    for (int column = TopDownSweepFrom; column <= TopDownSweepTo; column++)
                    {
                        var s = MeasureColumn(pixels, column, background, coarse);
                        separations.Add(s.SeparationPx);
                        double far  = GroundRowSolver.SolveWorldZForRow(
                            scene.UnityCamera, s.FarScreenY,  -60_000.0, +60_000.0);
                        double near = GroundRowSolver.SolveWorldZForRow(
                            scene.UnityCamera, s.NearScreenY, -60_000.0, +60_000.0);
                        ratios.Add(far / math.abs(near));
                        minFar  = math.min(minFar,  s.FarScreenY);  maxFar  = math.max(maxFar,  s.FarScreenY);
                        minNear = math.min(minNear, s.NearScreenY); maxNear = math.max(maxNear, s.NearScreenY);
                    }

                    double separation = Mean(separations);
                    double ratio      = Mean(ratios);
                    TestContext.WriteLine(
                        $"T-S1c: {separations.Count} columns; padded separation mean {separation:F4} px " +
                        $"(styled width {TS1WidthPx}); R mean {ratio:F6}; absolute silhouette spread far " +
                        $"{maxFar - minFar:F4} px, near {maxNear - minNear:F4} px");

                    // Top-down, e ≡ 0 exactly, so unlike T-S1 the absolute silhouettes ARE column-uniform:
                    // the radial refPx term is proportional to e. A spread here is estimator noise.
                    Assert.That(maxFar - minFar, Is.LessThan(0.05),
                        $"T-S1c uniformity: the far silhouette varies {maxFar - minFar:F4} px across columns " +
                        "where the geometry is identical. A PRECONDITION that the band is horizontal and " +
                        "uniform — not an error bar.");
                    Assert.That(maxNear - minNear, Is.LessThan(0.05),
                        $"T-S1c uniformity: the near silhouette varies {maxNear - minNear:F4} px.");

                    Assert.That(separation, Is.EqualTo(121.0).Within(0.15),
                        $"CALIBRATION: the estimator recovered a {separation:F4} px separation on a " +
                        $"{TS1WidthPx} px styled band viewed top-down, where the two PADDED silhouettes sit " +
                        "at exactly ±(W/2 + 0.5) = ±60.5 px. 121.0 means the half-sum lands on the padded " +
                        "silhouette, which is the surface T-S1 and T-S2 derive their expectations on. 120.0 " +
                        "would mean it landed on the styled / 0.5-coverage edge (the anchor dropped from one " +
                        "half-sum, or a stray −0.5); 122.0 would mean an anchor was counted in BOTH " +
                        "half-sums. Those are convention faults, not tolerance problems — do not widen this.");

                    Assert.That(ratio, Is.EqualTo(1.0).Within(0.005),
                        $"THIS ARM IS THE CONTROL: top-down, across ⊥ fwd, so e ≡ 0, the w-ratio is exactly " +
                        $"1.0 and the ribbon is symmetric before AND after the ruler change. R = {ratio:F6}. A move here " +
                        "means the fix is not inert where it must be.");
                });
        }

        // ── T-S2: line-offset must not leak into the half-width ──────────────────────────────────

        private const double TS2WidthPx  =  24.0;
        private const double TS2OffsetPx = 160.0;

        /// <summary>
        /// <b>T-S2 — the UNBOUNDED consumer.</b> The ribbon's WORLD half-width must not depend on its
        /// <c>_LineOffset</c>. Both station vertices take the same offset, so per-vertex rulers would put
        /// <c>e·L</c> into the half-width, unbounded in L. In the ratio centre/half,
        /// <c>(L + e·H)/(H + e·L)</c>, the ruler k cancels; with no leak the ratio is L/(W/2).
        ///
        /// <para>Non-obvious why: the pad is subtracted, because the width and offset use the frame constant
        /// but the AA pad is per-vertex, so they differ by <c>1/cos θ</c> (~7 % on a 24 px band). The pad is
        /// read off the live camera at the unshifted centreline, where the shader measures it. Limitation: this
        /// is a WORLD-space coupling; the band's device width moves with the offset, since the band sits at
        /// another depth.</para>
        /// </summary>
        [Test]
        public void OffsetRibbon_HalfWidthDoesNotTrackTheOffset_UnderTilt()
        {
            WithRenderedRoad(TiltDeg, TS2WidthPx, TS2OffsetPx, "s111-ts2-offset-halfwidth-tilt55.png",
                (scene, pixels) =>
                {
                    float3 background = PixelCoverage.BackgroundLinear(pixels);
                    float3 coarse     = CoarsePlateau(pixels, background);
                    Assert.That(math.distance(coarse, background), Is.GreaterThan(0.02f),
                        "T-S2: the road did not render — nothing is measurable.");

                    // Non-obvious why: the bracket is asymmetric, because past the camera plane (z ≈ −165 501 m)
                    // WorldToScreenPoint returns a mirrored y, which would void the bisection silently.
                    const double Lo = -60_000.0, Hi = +200_000.0;

                    // The inset comes from the rendered band (8 rows here), where ±4 px windows would overlap.
                    // The asserted ratio L/H is dimensionless, so the inset does not enter it.
                    var (bandBottom, bandTop) = BandRowSpan(pixels, CentreColumn, background, coarse);
                    int inset = InsetForBand(bandBottom, bandTop);
                    TestContext.WriteLine(
                        $"T-S2: centre band spans rows {bandBottom}..{bandTop} ({bandTop - bandBottom + 1} " +
                        $"rows); anchor inset {inset} px (default {EdgeInsetPx})");
                    Assert.That(inset, Is.GreaterThanOrEqualTo(2),
                        $"T-S2: the band spans only {bandTop - bandBottom + 1} rows, leaving an anchor inset " +
                        $"of {inset} px — too thin to contain the one-device-pixel ramp with any margin. " +
                        "RAISE the styled width in this arm and re-derive; do not shrink the inset further.");

                    var centre = MeasureColumn(pixels, CentreColumn, background, coarse, inset);
                    var (zFarCentre,  farMetresPerPx)  = SolveEdge(scene.UnityCamera, centre.FarScreenY,  Lo, Hi);
                    var (zNearCentre, nearMetresPerPx) = SolveEdge(scene.UnityCamera, centre.NearScreenY, Lo, Hi);

                    // ── Subtract the AA pad (see the summary): MapPixelsToWorld along `across` IS
                    // d(world z)/d(screen y) at the unshifted centreline, which SolveEdge reads off the camera. ──
                    double centreScreenY = scene.UnityCamera.WorldToScreenPoint(Vector3.zero).y;
                    var (_, centrelineMetresPerPx) = SolveEdge(scene.UnityCamera, centreScreenY, Lo, Hi);
                    double halfPadWorld = 0.5 * math.abs(centrelineMetresPerPx);

                    TestContext.WriteLine(
                        $"T-S2: centre column silhouettes screen-y {centre.FarScreenY:F4} / " +
                        $"{centre.NearScreenY:F4} (separation {centre.SeparationPx:F4} px); world z " +
                        $"{zFarCentre:F1} / {zNearCentre:F1} m; local scale {farMetresPerPx:F1} / " +
                        $"{nearMetresPerPx:F1} m/px");

                    Assert.That(centre.SeparationPx, Is.InRange(6.5, 11.0),
                        $"T-S2 framing: the padded band renders {centre.SeparationPx:F2} px thick, expected " +
                        "in [6.5, 11]. RE-DERIVED, with the arithmetic, for the world-width model: " +
                        "the styled half-width is a fixed 24/2 × K = 12 K metres (K = 305.75 m/device px at " +
                        "this pose) and the pad ~0.5 × K/cos 55° , so the band is ~3.93 km wide in the world " +
                        "wherever it sits. It is offset 160 K = 48.9 km NORTH, which at tilt 55° puts it at " +
                        "view depth 135.6 + 0.819×48.9 = 175.6 km against the look-at's 135.6 km — a factor " +
                        "of 1.295 further away — so it renders 14.8/1.295 ≈ 11.4 px before the ground " +
                        "plane's own cos 55° foreshortening of the across-axis brings it to ~8.8 (measured " +
                        "8.794). The window brackets that; it MOVES with the offset by construction, which " +
                        "is why the tooth below measures a dimensionless ratio instead. " +
                        "If the fixture will not frame, change L and RE-DERIVE; never widen the tolerance.");

                    var ratios = new List<double>();
                    for (int column = CentreColumn - TiltedSweepHalfWidth;
                         column <= CentreColumn + TiltedSweepHalfWidth; column++)
                    {
                        var s = MeasureColumn(pixels, column, background, coarse, inset);
                        double far  = GroundRowSolver.SolveWorldZForRow(scene.UnityCamera, s.FarScreenY,  Lo, Hi);
                        double near = GroundRowSolver.SolveWorldZForRow(scene.UnityCamera, s.NearScreenY, Lo, Hi);
                        ratios.Add(0.5 * (far + near) / (0.5 * (far - near) - halfPadWorld));
                    }

                    double minRatio = double.MaxValue, maxRatio = double.MinValue;
                    foreach (double r in ratios)
                    {
                        minRatio = math.min(minRatio, r);
                        maxRatio = math.max(maxRatio, r);
                    }
                    double ratio = Mean(ratios);

                    double centreWorld     = 0.5 * (zFarCentre + zNearCentre);
                    double paddedHalfWorld = 0.5 * (zFarCentre - zNearCentre);
                    double halfWorld       = paddedHalfWorld - halfPadWorld;
                    double expected        = TS2OffsetPx / (TS2WidthPx * 0.5);
                    TestContext.WriteLine(
                        $"T-S2: {ratios.Count} columns; centre/half ∈ [{minRatio:F6}, {maxRatio:F6}], mean " +
                        $"{ratio:F6} (want {expected:F6}); centre column world centre {centreWorld:F1} m, " +
                        $"padded half {paddedHalfWorld:F1} m − pad {halfPadWorld:F1} m = styled half " +
                        $"{halfWorld:F1} m");

                    Assert.That(maxRatio - minRatio, Is.LessThan(0.05),
                        $"T-S2 uniformity: centre/half varies {maxRatio - minRatio:F6} over " +
                        $"±{TiltedSweepHalfWidth} columns, where the radial refPx term contributes 6.6e-3 " +
                        "and edge-localisation noise on a 7.5 km half-width contributes ~0.015. A " +
                        "PRECONDITION that the band is uniform where it is measured, not an error bar.");

                    Assert.That(ratio, Is.EqualTo(expected).Within(2.5).Percent,
                        $"THE OFFSET LEAK: the offset ribbon's world centre sits {centreWorld:F1} m out with " +
                        $"a STYLED half-width of {halfWorld:F1} m, a ratio of {ratio:F6} where L/(W/2) = " +
                        $"{expected:F3} — the half-width must not depend on the offset at all. _LineOffset is " +
                        "applied as a COMMON world vector to both station vertices; if each were to measure " +
                        "its own signed ruler, an offset of L device px would leak e·L into the HALF-width — " +
                        "12.11 % at this pose, and unbounded in L, because nothing bounds it by the styled " +
                        "width. This is the consumer whose error is NOT bounded by the styled width.");
                });
        }

        // ── T4: renderedWidthPx × depth is constant along a receding road ────────────────────────

        private const double T4WidthPx = 40.0;

        /// <summary>The rows swept. Chosen from the pose rather than by eye: at tilt 55 with fov 60 the frame
        /// spans view depths of roughly 74 km (bottom) to 890 km (top), and rows 140…380 cover a ≥ 2× range
        /// while keeping the widest band (bottom, ~55 px) and the narrowest (top, ~24 px) both comfortably
        /// measurable. The sweep straddles the look-at row (256), where the rendered width is the styled
        /// width exactly.</summary>
        private static readonly int[] T4Rows = { 140, 200, 260, 320, 380 };

        /// <summary>Rendered band width in device px on one screen ROW: the coverage integral across the
        /// whole row. A one-device-pixel ramp CENTRED on the styled edge integrates to the styled width at any
        /// phase, so the AA pad adds nothing. The plateau is LOCAL to the row, because the PBR shading
        /// differs between rows at very different depths.</summary>
        private static double MeasureRowWidthPx(Frame pixels, int row, float3 background)
        {
            float3 plateau = background;
            float  best    = 0f;
            for (int column = 0; column < Size; column++)
            {
                float3 sample = PixelCoverage.SampleLinear(pixels, column, row);
                float  dist   = math.distancesq(sample, background);
                if (dist > best) { best = dist; plateau = sample; }
            }
            Assert.That(math.distance(plateau, background), Is.GreaterThan(0.02f),
                $"row {row}: nothing on this row is distinguishable from the background — the road did not " +
                "render here, so no width is measurable.");

            double sum = 0.0;
            for (int column = 0; column < Size; column++)
                sum += PixelCoverage.CoverageAt(pixels, column, row, background, plateau);
            return sum;
        }

        /// <summary>View depth (== <c>clip.w</c> for a standard perspective projection) of the ground point
        /// that projects onto <paramref name="screenY"/> on the road's own column.</summary>
        private static double DepthAtRow(Camera cam, double screenY, out double worldZ)
        {
            worldZ = GroundRowSolver.SolveWorldZForRow(cam, screenY, -120_000.0, +2_000_000.0);
            Vector3 p = new Vector3(0f, 0f, (float)worldZ);
            return Vector3.Dot(p - cam.transform.position, cam.transform.forward);
        }

        /// <summary>
        /// <b>T4 — the world-width model.</b> On a road RECEDING from a tilted camera,
        /// <c>renderedWidthPx × depth</c> is CONSTANT along its length.
        ///
        /// <para>Non-obvious why: a world half-width <c>h</c> at depth <c>d</c> renders
        /// <c>h · viewportPx.y / (2·d·tan(fov/2))</c> px, so the product is free of depth; `line-width: N px`
        /// fixes <c>h</c> once, at the look-at. A per-vertex px→world width (including the shader's
        /// <c>MapPixelsToWorld</c> missing-push fallback) holds <c>widthPx</c> constant, so the product spreads
        /// by the full depth ratio (&gt; 2×). The near/far width ratio is only recorded.</para>
        /// </summary>
        [Test]
        public void RenderedWidthTimesDepth_IsConstantAlongARecedingRoad_UnderTilt()
        {
            WithRenderedRecedingRoad(TiltDeg, T4WidthPx, "s116-t4-width-times-depth-tilt55.png",
                (scene, pixels) =>
                {
                    float3 background = PixelCoverage.BackgroundLinear(pixels);

                    var widths   = new List<double>();
                    var depths   = new List<double>();
                    var products = new List<double>();
                    var report   = new System.Text.StringBuilder();

                    foreach (int row in T4Rows)
                    {
                        // Frame row j reports the shader at SCREEN-Y j + 0.5 (bottom-up, no flip).
                        double depth = DepthAtRow(scene.UnityCamera, row + 0.5, out double worldZ);
                        double width = MeasureRowWidthPx(pixels, row, background);
                        widths.Add(width);
                        depths.Add(depth);
                        products.Add(width * depth);
                        report.Append(
                            $"[row {row}] z {worldZ / 1000.0:F1} km, depth {depth / 1000.0:F1} km, " +
                            $"width {width:F3} px, w·d {width * depth / 1000.0:F1} → ");
                    }

                    double minProduct = double.MaxValue, maxProduct = double.MinValue;
                    double minDepth   = double.MaxValue, maxDepth   = double.MinValue;
                    double minWidth   = double.MaxValue, maxWidth   = double.MinValue;
                    for (int i = 0; i < products.Count; i++)
                    {
                        minProduct = math.min(minProduct, products[i]);
                        maxProduct = math.max(maxProduct, products[i]);
                        minDepth   = math.min(minDepth,   depths[i]);
                        maxDepth   = math.max(maxDepth,   depths[i]);
                        minWidth   = math.min(minWidth,   widths[i]);
                        maxWidth   = math.max(maxWidth,   widths[i]);
                    }
                    double productSpread = maxProduct / minProduct;
                    double depthSpread   = maxDepth   / minDepth;
                    double widthSpread   = maxWidth   / minWidth;

                    TestContext.WriteLine(
                        $"T4: {report}depth spread {depthSpread:F3}×, WIDTH spread {widthSpread:F3}× " +
                        $"(near/far — cross-check against the reference's ~3.375 at max pitch), " +
                        $"w·d spread {productSpread:F5}×");

                    // Vacuity guards. Both are preconditions on the FIXTURE, not results.
                    Assert.That(depthSpread, Is.GreaterThan(2.0),
                        $"T4: the swept rows span only a {depthSpread:F3}× depth range. Below 2× the " +
                        "constant-device-width model and the constant-world-width model are too close to " +
                        "tell apart, and the tooth would pass under both.");
                    Assert.That(minWidth, Is.GreaterThan(4.0),
                        $"T4: the narrowest measured band is {minWidth:F2} px. Below a few pixels the " +
                        "coverage integral stops resolving the silhouette and the product is noise.");

                    // 1.005 against a measured 1.00022, while a per-vertex width reads 2.290×. The bound is
                    // tight so a PARTIAL compensation, which sits between the two, also fails.
                    Assert.That(productSpread, Is.LessThan(1.005),
                        $"THE WIDTH MODEL: renderedWidthPx × depth varies by {productSpread:F5}× over a " +
                        $"{depthSpread:F3}× depth range. It must be constant to ~1 %, because a styled px " +
                        $"width fixes a WORLD width once and the perspective divide alone renders it. A " +
                        $"spread approaching the depth spread ({depthSpread:F3}×) means the width is being " +
                        "held constant in DEVICE pixels at every depth — a per-vertex px→world conversion " +
                        "causes this. Measured widths: " +
                        $"{minWidth:F2}…{maxWidth:F2} px. {report}");
                });
        }

        private static double Mean(List<double> values)
        {
            double sum = 0.0;
            foreach (double v in values) sum += v;
            return sum / values.Count;
        }
    }
}
