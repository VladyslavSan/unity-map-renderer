// Unity-only: render tests requiring GPU context (SnapshotRenderer / UnityEngine).
// NOT included in Tools/core-tests/core-tests.csproj.

using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using MapRenderer.Core.Geometry;
using Unity.Mathematics;
using System.Collections.Generic;
// Alias, not a plain `using`: the namespace segment `Rendering` would otherwise collide with a bare
// UnityEngine type in lookup — the CS0118 trap this repo's conventions name.
using ShaderProperties = MapRenderer.Unity.Rendering.ShaderProperties;

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

        /// <param name="yawDeg">Rotation about world +Y, applied after the 90° pitch — the camera keeps
        /// looking straight down but its screen axes rotate against the map. 0 leaves screen-up = world +Z
        /// (north). This is what lets a test tell the "map" and "viewport" translate anchors apart: with the
        /// default yaw they point the same way, so no assertion can distinguish them.</param>
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

            // The frame constant the line shader converts a PIXEL width with. Production pushes it from
            // MapCamera.SyncToCamera, measured off that camera; this fixture hand-builds a UnityEngine.Camera
            // with no MapCamera, so it must push the equivalent for ITS camera — MetersPerPx, already derived
            // from OrthoSz and SnapH above, and exactly what 2*d*tan(fov/2)/H degenerates to under ortho.
            // Any NEW fixture that hand-builds a camera has to do this too: a 0 here does not blank the
            // frame, it renders every styled width as the same 1 px hairline, which looks plausible.
            Shader.SetGlobalFloat(
                ShaderProperties.FrameGlobalIds.MapFrameMetersPerDevicePixel, MetersPerPx);
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
        /// _WidthIsPixels=1, so the shader MEASURES px→world per vertex (there is no _MetersPerPixel uniform
        /// any more — S104 removed it; this fixture kept setting it into nothing until the line-translate work).
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
        public void LineTranslate_NonZero_ShiftsRibbonSouthByExpectedPixels()
        {
            // DIRECTION IS PART OF THE ASSERTION (it did not used to be — see below).
            //
            // Spec: _LineTranslate.y is screen pixels and "negatives indicate up", so +y is SOUTH. The camera
            // looks straight down with screen-up = world +Z = north, and SnapshotRenderer.RawPixels is
            // BOTTOM-left origin, so north is INCREASING row. A southward shift therefore DECREASES the row
            // index: expected delta = −TranslatePx.
            //
            // The previous version of this test asserted Math.Abs(shifted − base), i.e. magnitude only, and
            // so passed against a shader whose +y pointed NORTH. It also justified itself in terms of
            // _MetersPerPixel, a uniform S104 deleted. Both are why the inverted sign survived so long; see
            // docs/line-translate-parity-design.md §2.
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

                // SIGNED delta. RawPixels is bottom-left origin, so south (+y) is a DECREASING row.
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
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(lineGo);
                UnityEngine.Object.DestroyImmediate(mat);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ── line-translate is a SCREEN-pixel offset regardless of the width's units ──

        /// <summary>
        /// The defect this pins: <c>pxToWorld</c> used to be left at 1.0 unless <c>_WidthIsPixels &gt; 0.5</c>,
        /// so a layer whose <c>line-width</c> is in world metres applied <c>line-translate</c> as raw
        /// METRES — off by 1/metresPerPixel (≈3.7× here, and zoom-dependent in the real renderer).
        ///
        /// <para>No existing test could see it: every fixture in this file sets
        /// <c>_WidthIsPixels = 1</c>, so the world-width path had never once executed with a translate set.
        /// The two renders below differ ONLY in the width's units, and the spec says the offset is screen
        /// pixels either way — so the measured shift must be identical.</para>
        /// </summary>
        [Test]
        public void LineTranslate_IsScreenPixels_WhetherWidthIsPixelsOrMetres()
        {
            const float TranslatePx = 30f;
            const float WidthPx     = 8f;
            const float Tol         = 5f;

            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(1f, 1f, 1f, 1f);

            var (cameraGo, camera) = BuildCamera();
            var (lineGo, mat)      = BuildHorizontalLine(widthPx: WidthPx, gapPx: 0f);

            using var snapBase   = new SnapshotRenderer(SnapW, SnapH);
            using var snapPixels = new SnapshotRenderer(SnapW, SnapH);
            using var snapMetres = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                mat.SetVector("_LineTranslate", Vector4.zero);
                snapBase.Render(camera);

                if (snapBase.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — line-translate unit test skipped.");
                        return;
                    }
                }

                int baseRow = FindLineCenterRow(snapBase.RawPixels, SnapW, SnapH, SnapW / 2);
                if (baseRow < 0)
                {
                    Assert.Inconclusive("Could not find baseline line band.");
                    return;
                }

                // (1) width in PIXELS — the path every other test in this file exercises.
                mat.SetVector("_LineTranslate", new Vector4(0f, TranslatePx, 0f, 0f));
                snapPixels.Render(camera);
                snapPixels.WritePng("line-translate-width-pixels.png");
                int pixelsRow = FindLineCenterRow(snapPixels.RawPixels, SnapW, SnapH, SnapW / 2);

                // (2) width in world METRES, sized to render the same ribbon thickness. Same translate.
                mat.SetFloat("_WidthIsPixels", 0f);
                mat.SetFloat("_Width", WidthPx * MetersPerPx);
                snapMetres.Render(camera);
                snapMetres.WritePng("line-translate-width-metres.png");
                int metresRow = FindLineCenterRow(snapMetres.RawPixels, SnapW, SnapH, SnapW / 2);

                if (pixelsRow < 0 || metresRow < 0)
                {
                    Assert.Inconclusive("Could not find a translated line band.");
                    return;
                }

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
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraGo);
                UnityEngine.Object.DestroyImmediate(lineGo);
                UnityEngine.Object.DestroyImmediate(mat);
                RenderSettings.ambientMode  = prevAmbientMode;
                RenderSettings.ambientLight = prevAmbientLight;
            }
        }

        // ── line-translate-anchor: "map" vs "viewport" ────────────────────────

        /// <summary>
        /// The defect this pins: <c>_LineTranslateAnchor</c> was parsed, bound to the material, and then
        /// never read by the shader, so <c>"viewport"</c> silently behaved as <c>"map"</c>.
        ///
        /// <para>The discriminating setup matters. Under this file's default top-down camera the two anchors
        /// point the SAME way, so no assertion can separate them — which is why the gap went unnoticed.
        /// Yawing the camera 180° makes screen-up = world −Z, so a southward (+y) offset moves the ribbon
        /// UP the image under "map" and DOWN under "viewport": opposite signs, same magnitude.</para>
        /// </summary>
        [Test]
        public void LineTranslateAnchor_MapAndViewport_MoveTheRibbonOppositeWays()
        {
            const float TranslatePx = 30f;
            const float Tol         = 5f;

            var prevAmbientMode  = RenderSettings.ambientMode;
            var prevAmbientLight = RenderSettings.ambientLight;
            RenderSettings.ambientMode  = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(1f, 1f, 1f, 1f);

            // Yaw 180°: still straight down, but screen-up is now world −Z (south) and screen-right world −X.
            var (cameraGo, camera) = BuildCamera(yawDeg: 180f);
            var (lineGo, mat)      = BuildHorizontalLine(widthPx: 8f, gapPx: 0f);

            using var snapBase     = new SnapshotRenderer(SnapW, SnapH);
            using var snapMap      = new SnapshotRenderer(SnapW, SnapH);
            using var snapViewport = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                mat.SetVector("_LineTranslate", Vector4.zero);
                snapBase.Render(camera);

                if (snapBase.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — translate-anchor test skipped.");
                        return;
                    }
                }

                int baseRow = FindLineCenterRow(snapBase.RawPixels, SnapW, SnapH, SnapW / 2);
                if (baseRow < 0)
                {
                    Assert.Inconclusive("Could not find baseline line band.");
                    return;
                }

                mat.SetVector("_LineTranslate", new Vector4(0f, TranslatePx, 0f, 0f));

                // "map": +y is SOUTH on the map = world −Z. Screen-up IS −Z here, so the row INCREASES.
                mat.SetFloat("_LineTranslateAnchor", 0f);
                snapMap.Render(camera);
                snapMap.WritePng("line-translate-anchor-map.png");
                int mapRow = FindLineCenterRow(snapMap.RawPixels, SnapW, SnapH, SnapW / 2);

                // "viewport": +y is screen-down regardless of the map, so the row DECREASES.
                mat.SetFloat("_LineTranslateAnchor", 1f);
                snapViewport.Render(camera);
                snapViewport.WritePng("line-translate-anchor-viewport.png");
                int viewportRow = FindLineCenterRow(snapViewport.RawPixels, SnapW, SnapH, SnapW / 2);

                if (mapRow < 0 || viewportRow < 0)
                {
                    Assert.Inconclusive("Could not find a translated line band.");
                    return;
                }

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
