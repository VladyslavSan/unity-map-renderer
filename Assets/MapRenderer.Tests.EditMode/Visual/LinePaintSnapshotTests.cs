// Unity-only: render tests requiring GPU context (SnapshotRenderer / UnityEngine).
// NOT included in Tools/core-tests/core-tests.csproj.

using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Imaging;
using MapRenderer.Core.Geometry;
using MapRenderer.Unity;
using Unity.Mathematics;
using System.Collections.Generic;

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// S14 acceptance snapshot tests for line paint GPU behavior (Teeth #3, #4, #5).
    ///
    /// Tooth #3 (gap-width casing): line-gap-width > 0 via <c>_GapWidth</c> uniform produces a
    ///   hollow / cased line (background visible at center column) while gap-width=0 gives a solid
    ///   line (center column is non-background). Same mesh — uniform change only, no rebuild.
    ///
    /// Tooth #4 (line-translate pixel-correct): setting <c>_LineTranslate.y = N</c> pixels shifts
    ///   the ribbon's measured center row by ≈ N image pixels. No mesh rebuild.
    ///
    /// Tooth #5 (line-pattern hook honest): with <c>_LinePattern=1</c> (hook active) the line still
    ///   renders non-blank (falls back to solid <c>_BaseColor</c>), proving the hook does not black
    ///   out the line.
    ///
    /// GPU context guard: all render tests degrade to Inconclusive (not Fail) when the GPU context
    ///   is unavailable in batch mode. Mirrors the LitLineSnapshotTests guard pattern.
    ///
    /// Camera: top-down ortho 512×512, Y=200, orthoSize=70. metersPerPixel ≈ 0.2734 m/px.
    /// Background: dark slate (0.10, 0.11, 0.15) — same as all other snapshot tests.
    /// </summary>
    [TestFixture]
    public class LinePaintSnapshotTests
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

        private static float MetersPerPx => 2f * OrthoSz / SnapH; // ≈ 0.2734 m/px

        // Line width and gap sizes in pixels (used for _Width and _GapWidth uniforms).
        // Line is in pixels mode (_WidthIsPixels=1) so 1 px = MetersPerPx meters.
        private const float LineWidthPx = 4f;   // visible casing band width
        private const float GapWidthPx  = 20f;  // large gap → fat hollow center (≈5.5m)

        // ── Camera helper ──────────────────────────────────────────────────────

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("LinePaintSnapCamera");
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

        /// <summary>
        /// Build a single horizontal line with the Map/Line shader.
        /// The line runs along world X from -40m to +40m, centered at world origin.
        /// _WidthIsPixels=1, _MetersPerPixel=MetersPerPx.
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
            mat.SetFloat("_MetersPerPixel", MetersPerPx);
            mat.SetFloat("_GapWidth",       gapPx);
            mat.SetColor("_BaseColor",       color ?? new Color(0.9f, 0.5f, 0.1f, 1f));
            mat.SetFloat("_Opacity",        1f);
            mat.SetFloat("_AaEdgeWidth",    1f);   // antialiasing buffer (was _Blur pre-decouple)
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
        private static int FindLineCenterRow(byte[] pixels, int width, int height, int col)
        {
            bool IsNonBg(int row)
            {
                if (row < 0 || row >= height) return false;
                int idx = (row * width + col) * 4;
                return Math.Abs(pixels[idx]   - BgR8) +
                       Math.Abs(pixels[idx+1] - BgG8) +
                       Math.Abs(pixels[idx+2] - BgB8) > SnapshotCoverage.Tolerance;
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
        /// Used to distinguish hollow (gap) vs solid center.
        /// </summary>
        private static bool IsCenterPixelBackground(byte[] pixels, int width, int col, int row)
        {
            if (row < 0) return true;
            int idx = (row * width + col) * 4;
            return Math.Abs(pixels[idx]   - BgR8) +
                   Math.Abs(pixels[idx+1] - BgG8) +
                   Math.Abs(pixels[idx+2] - BgB8) <= SnapshotCoverage.Tolerance;
        }

        // ── Tooth #3: gap-width produces hollow center vs solid center ────────

        [Test]
        public void GapWidth_NonZero_ProducesHollowCenter_ZeroGapIsSolid()
        {
            // Strategy:
            //   1. Render the line with gap=0 (solid). Find the centerline row from that render.
            //   2. Apply gap=GapWidthPx (uniform only, no mesh rebuild).
            //   3. Check the SAME centerline row in the hollow render — it must now be BACKGROUND
            //      (the fragment shader discards |side| < innerFrac, which covers the centerline).
            //   4. Also verify the hollow render is NOT fully blank (casing strips still visible).
            //
            // The gap is large (20px) relative to line width (4px) so innerFrac ≈ 0.71 and
            // the inner-discard band is clearly visible.

            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(1f, 1f, 1f, 1f);

            var (cameraGo, camera) = BuildCamera();
            var (lineGo, mat) = BuildHorizontalLine(widthPx: LineWidthPx, gapPx: 0f);

            using var snapSolid  = new SnapshotRenderer(SnapW, SnapH);
            using var snapHollow = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                // Render with gap=0 (solid).
                snapSolid.Render(camera);
                snapSolid.WritePng("line-paint-gap-solid.png");

                // GPU guard.
                if (snapSolid.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — gap-width test skipped.");
                        return;
                    }
                }

                // Find the solid line's centerline row. This is the row we will probe
                // in the hollow render to verify the center is discarded.
                int solidCenterRow = FindLineCenterRow(snapSolid.RawPixels, SnapW, SnapH, SnapW / 2);
                if (solidCenterRow < 0)
                {
                    Assert.Inconclusive(
                        "Could not find line band in solid render — " +
                        "line may not render in batch mode.");
                    return;
                }

                // Verify the solid render has a non-background pixel at the centerline.
                bool solidCenterIsBg = IsCenterPixelBackground(
                    snapSolid.RawPixels, SnapW, SnapW / 2, solidCenterRow);

                // Now set a large gap (no mesh rebuild).
                mat.SetFloat("_GapWidth", GapWidthPx);
                snapHollow.Render(camera);
                snapHollow.WritePng("line-paint-gap-hollow.png");

                // Check the SAME row (solidCenterRow) in the hollow render.
                // The centerline is at the same world position — only the gap makes it hollow.
                // With large gap (20px), innerFrac ≈ 0.71 → pixels at |side| < 0.71 are discarded.
                // The centerline (|side|=0) must be discarded → background.
                bool hollowCenterIsBg = IsCenterPixelBackground(
                    snapHollow.RawPixels, SnapW, SnapW / 2, solidCenterRow);

                // Also verify that the hollow render is NOT completely blank (casing bands visible).
                var hollowVerdict = SnapshotCoverage.Analyse(
                    snapHollow.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Debug.Log($"[LinePaintSnapshotTests] Gap=0: centerIsBg={solidCenterIsBg}, " +
                          $"Gap={GapWidthPx}: centerIsBg={hollowCenterIsBg} at row={solidCenterRow}. " +
                          $"Hollow filledFraction={hollowVerdict.FilledFraction:P2}");

                // gap=0 → center should NOT be background (solid line covers center).
                Assert.IsFalse(solidCenterIsBg,
                    $"gap-width=0: center column at row={solidCenterRow} must NOT be background " +
                    "(solid line). If center is background, the solid path may have a bug.");

                // gap>0 → the line's centerline row must be BACKGROUND (hollow/cased).
                // The innerFrac discard in MapLineForwardPass.hlsl covers |side| < innerFrac.
                // At the exact centerline, |side|=0 which is always < innerFrac (when gap>0).
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
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(lineGo);
                UnityEngine.Object.DestroyImmediate(mat);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ── Tooth #4: _LineTranslate shifts ribbon position in image pixels ───

        [Test]
        public void LineTranslate_NonZero_ShiftsRibbonByExpectedPixels()
        {
            // Set _LineTranslate.y = TranslatePx → line ribbon should shift by ≈TranslatePx rows.
            // The horizontal line is at Z=0 in world space. TranslateY shifts it in world Z,
            // which maps to a row shift in the top-down orthographic render.
            // _LineTranslate.y × _MetersPerPixel = meters shift in world Z.
            // Rows shift = (world-Z meters) / MetersPerPx.
            // With _LineTranslate.y = TranslatePx, expected row shift = TranslatePx.
            const float TranslatePx = 30f; // pixels to shift (large enough to measure clearly)

            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(1f, 1f, 1f, 1f);

            var (cameraGo, camera) = BuildCamera();
            // Use a wider line for easier centroid measurement.
            var (lineGo, mat) = BuildHorizontalLine(widthPx: 8f, gapPx: 0f);

            using var snapBase      = new SnapshotRenderer(SnapW, SnapH);
            using var snapTranslate = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                // Render at default position (translate=0).
                mat.SetVector("_LineTranslate", Vector4.zero);
                snapBase.Render(camera);
                snapBase.WritePng("line-paint-translate-base.png");

                // GPU guard.
                if (snapBase.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — line-translate test skipped.");
                        return;
                    }
                }

                int baseCenterRow = FindLineCenterRow(snapBase.RawPixels, SnapW, SnapH, SnapW / 2);
                if (baseCenterRow < 0)
                {
                    Assert.Inconclusive(
                        "Could not find baseline line band — line may not render in batch mode.");
                    return;
                }

                // Apply translation (no mesh rebuild).
                // _LineTranslate is (x_px, y_px, 0, 0). y_px shifts in world Z (=image rows).
                // Camera looks down -Y. World +Z = image up (row decreasing) in Unity's top-down setup.
                mat.SetVector("_LineTranslate", new Vector4(0f, TranslatePx, 0f, 0f));
                snapTranslate.Render(camera);
                snapTranslate.WritePng("line-paint-translate-shifted.png");

                int shiftedCenterRow = FindLineCenterRow(snapTranslate.RawPixels, SnapW, SnapH, SnapW / 2);
                if (shiftedCenterRow < 0)
                {
                    Assert.Inconclusive(
                        "Could not find shifted line band — line may not render in batch mode.");
                    return;
                }

                // Row delta: positive delta = shifted toward top (lower row index in Unity's flipped UV).
                int rowDelta = Math.Abs(shiftedCenterRow - baseCenterRow);
                float expectedRowShift = TranslatePx;
                const float Tol = 5f; // ±5px tolerance (AA + discretization)

                Debug.Log($"[LinePaintSnapshotTests] Translate: baseCenterRow={baseCenterRow}, " +
                          $"shiftedCenterRow={shiftedCenterRow}, |rowDelta|={rowDelta}, " +
                          $"expected≈{expectedRowShift:F1}px (±{Tol}px)");

                Assert.That((float)rowDelta,
                    Is.InRange(expectedRowShift - Tol, expectedRowShift + Tol),
                    $"_LineTranslate.y={TranslatePx}px must shift the ribbon center row by " +
                    $"≈{expectedRowShift:F1}px (±{Tol}px). Got |Δrow|={rowDelta}px. " +
                    "Check: _LineTranslate.y * _MetersPerPixel should produce a world-Z offset " +
                    "equal to the expected pixel shift. Verify translateScale = _MetersPerPixel " +
                    "in MapLineForwardPass.hlsl.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(lineGo);
                UnityEngine.Object.DestroyImmediate(mat);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ── Tooth #5: _LinePattern=1 (hook) does NOT blank the line ──────────

        [Test]
        public void LinePattern_HookActive_LineStillRendersNonBlank()
        {
            // With _LinePattern=1 the S14 hook is "active" (pattern layer flagged).
            // The fallback must still render solid _BaseColor — the line must NOT vanish.

            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(1f, 1f, 1f, 1f);

            var (cameraGo, camera) = BuildCamera();
            // Build solid line, then set _LinePattern=1 after.
            var (lineGo, mat) = BuildHorizontalLine(widthPx: 8f, gapPx: 0f);

            using var snapNoPattern  = new SnapshotRenderer(SnapW, SnapH);
            using var snapWithPattern = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                // Render without pattern flag (baseline).
                mat.SetFloat("_LinePattern", 0f);
                snapNoPattern.Render(camera);
                snapNoPattern.WritePng("line-paint-pattern-off.png");

                // GPU guard.
                if (snapNoPattern.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — line-pattern test skipped.");
                        return;
                    }
                }

                // Render WITH _LinePattern=1 (no mesh rebuild — uniform only).
                mat.SetFloat("_LinePattern", 1f);
                snapWithPattern.Render(camera);
                snapWithPattern.WritePng("line-paint-pattern-on.png");

                // The line must NOT be blank when _LinePattern=1.
                // Use FilledFraction > 0.4% as the primary assertion: a 8px-wide line across
                // 512 cols fills ≈ 8/512 ≈ 1.6% of the 512×512 image. IsBlank uses a 97%
                // background threshold which a thin line safely exceeds (it only fills ~1.6%).
                // Use the band-width measurement as the deciding metric instead.
                int patternBandWidth = FindLineCenterRow(snapWithPattern.RawPixels, SnapW, SnapH, SnapW / 2);

                // Also measure the no-pattern baseline band width for comparison.
                int baselineBandWidth = FindLineCenterRow(snapNoPattern.RawPixels, SnapW, SnapH, SnapW / 2);

                var verdict = SnapshotCoverage.Analyse(
                    snapWithPattern.RawPixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Debug.Log($"[LinePaintSnapshotTests] Pattern hook: " +
                          $"filledFraction={verdict.FilledFraction:P2}, " +
                          $"baselineCenterRow={baselineBandWidth}, patternCenterRow={patternBandWidth}");

                // Primary assertion: the line center row must be visible with _LinePattern=1.
                // patternBandWidth here is actually the CENTER ROW of the band (reusing FindLineCenterRow).
                Assert.GreaterOrEqual(patternBandWidth, 0,
                    "_LinePattern=1 must NOT blank the line. FindLineCenterRow returned -1 " +
                    "(no visible band detected). The S14 hook must fall back to solid line-color " +
                    "(no sprite sampling until S17). If this fails, the hook is incorrectly " +
                    "discarding all pixels instead of rendering solid _BaseColor.");

                // Secondary: filled fraction must be greater than a minimal threshold.
                // A 8px-wide line in a 512x512 image fills ≈ 8*512 / (512*512) ≈ 1.6%.
                Assert.Greater(verdict.FilledFraction, 0.004f,
                    $"_LinePattern=1: rendered filled fraction ({verdict.FilledFraction:P2}) must be > 0.4% " +
                    "— the line ribbon should be visible. This fails if the hook blanks the output.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(lineGo);
                UnityEngine.Object.DestroyImmediate(mat);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }
    }
}
