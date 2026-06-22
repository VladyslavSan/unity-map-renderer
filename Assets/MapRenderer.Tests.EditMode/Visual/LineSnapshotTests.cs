using System.IO;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Imaging;
using MapRenderer.Core.Geometry;
using System.Collections.Generic;
using Unity.Mathematics;
// S54: LineBootstrap and LineMeshBuilder retired; SyntheticLineMesh replaces them.

namespace MapRenderer.Tests.Visual
{
    /// <summary>
    /// Headless visual snapshot tests for the S05 GPU-driven line renderer.
    ///
    /// Three discriminating checks that coverage fraction alone cannot provide:
    ///   1. Width measurement: render a horizontal line; sample a perpendicular scanline; count
    ///      contiguous non-background pixels; assert ≈ widthMeters/metersPerPixel ± tolerance.
    ///   2. No-rebuild proof: render; change _Width on the material (no LineMeshBuilder re-run,
    ///      no new Mesh); re-render; assert measured width changed proportionally.
    ///   3. AA ~1px: assert the fill→background transition spans ≤ 3px (transition band).
    ///
    /// GPU-context guard: if both the line render AND a blank-control render come back all-black,
    /// marks <c>Inconclusive</c> (no GPU context) rather than failing.
    ///
    /// Camera: top-down ortho at (0,200,0) looking down (−Y), orthographicSize=70, 512×512.
    /// metersPerPixel = 2·70/512 ≈ 0.2734 m/px.
    ///
    /// Line: horizontal at z=0, x=[−40,40], Width=5m.
    /// Expected width in pixels: 5 / (2·70/512) = 5·512/(140) ≈ 18.3px.
    /// </summary>
    [TestFixture]
    public class LineSnapshotTests
    {
        private const int  SnapW = 512;
        private const int  SnapH = 512;

        // Camera parameters (match WorldFillSnapshotTests pattern).
        private const float OrthoSize = 70f;
        private const float CamY      = 200f;

        // Background: distinctive dark slate (same as WorldFillSnapshotTests — not black).
        private static readonly Color  BgColor = new Color(0.10f, 0.11f, 0.15f, 1f);
        private static readonly byte   BgR8    = (byte)(0.10f * 255 + 0.5f); // 26
        private static readonly byte   BgG8    = (byte)(0.11f * 255 + 0.5f); // 28
        private static readonly byte   BgB8    = (byte)(0.15f * 255 + 0.5f); // 38

        // Line: horizontal segment centred at world origin.
        private const float LineWidthMeters    = 5f;
        private const float LineWidthDoubleM   = 10f; // for no-rebuild test

        // Width measurement tolerance: ±4px to absorb AA feather + GPU rounding.
        private const int   WidthTolPx  = 4;

        // metersPerPixel for this camera/resolution.
        private static float MetersPerPx => 2f * OrthoSize / SnapH; // ~0.2734

        // Expected pixel width for a given width in meters.
        private static float ExpectedPxWidth(float widthM) => widthM / MetersPerPx;

        // ─── Build helpers ──────────────────────────────────────────────────────────────────

        private static (GameObject go, Camera camera) BuildCamera()
        {
            var go     = new GameObject("LineSnapCamera");
            var camera = go.AddComponent<Camera>();
            camera.transform.position = new Vector3(0f, CamY, 0f);
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            camera.orthographic       = true;
            camera.orthographicSize   = OrthoSize;
            camera.farClipPlane       = 1000f;
            camera.clearFlags         = CameraClearFlags.SolidColor;
            camera.backgroundColor    = BgColor;
            camera.enabled            = false;
            return (go, camera);
        }

        /// <summary>
        /// Build ALL golden test lines (horizontal + L-shape + diagonal) using SyntheticLineMesh.
        /// S54: replaces LineBootstrap + LineMeshBuilder.
        /// </summary>
        private static GameObject BuildLineScene(float widthMeters, out Material mat,
            JoinType join = JoinType.Miter, CapType cap = CapType.Butt)
        {
            var mesh = SyntheticLineMesh.BuildGoldenShapes(join, cap);
            var go = new GameObject("LineTest");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var shader = Shader.Find("MapRenderer/Line") ?? Shader.Find("Sprites/Default");
            mat = new Material(shader) { name = "LineTestMat" };
            mat.SetFloat("_Width",         widthMeters);
            mat.SetFloat("_WidthIsPixels", 0f);
            mat.SetFloat("_MetersPerPixel", MetersPerPx);
            mat.SetColor("_BaseColor",      new Color(0.9f, 0.5f, 0.1f, 1f));
            mat.SetFloat("_Opacity",       1f);
            mat.SetFloat("_Blur",          1f);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>
        /// Build a SINGLE horizontal line for width-measurement tests.
        /// S54: replaces LineMeshBuilder with SyntheticLineMesh.
        /// </summary>
        private static GameObject BuildSingleHorizontalLine(float widthMeters, out Material mat)
        {
            var pts = new List<double2>
            {
                new double2(-40, 0),
                new double2( 40, 0),
            };
            var mesh = SyntheticLineMesh.BuildFromPoints(pts, JoinType.Miter, CapType.Butt);

            var go = new GameObject("HLine");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;

            var shader = Shader.Find("MapRenderer/Line") ?? Shader.Find("Sprites/Default");
            mat = new Material(shader) { name = "HLineMat" };
            mat.SetFloat("_Width",         widthMeters);
            mat.SetFloat("_WidthIsPixels", 0f);
            mat.SetFloat("_MetersPerPixel", MetersPerPx);
            mat.SetColor("_BaseColor",      new Color(0.9f, 0.5f, 0.1f, 1f));
            mat.SetFloat("_Opacity",       1f);
            mat.SetFloat("_Blur",          1f);
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        // ─── Test 1: Renders line + writes PNG + passes coverage ───────────────────────────

        [Test]
        public void RendersLine_WritesPng_PassesCoverage()
        {
            var (cameraGo, camera) = BuildCamera();
            var lineGo             = BuildLineScene(LineWidthMeters, out _);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);

                // Write PNG artefact.
                string pngPath = snap.WritePng("line-golden.png");
                Assert.IsTrue(File.Exists(pngPath),
                    $"line-golden.png must exist: {pngPath}");
                Assert.That(new FileInfo(pngPath).Length, Is.GreaterThan(500L),
                    "PNG must be non-trivial (>500 bytes).");

                // GPU-context guard.
                if (snap.IsAllBlack())
                {
                    using var blankSnap = RenderBlank();
                    if (blankSnap.IsAllBlack())
                    {
                        Assert.Inconclusive(
                            "Both line render and blank-control are all-black: no GPU context " +
                            "in EditMode batchmode. Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                        return;
                    }
                }

                byte[] pixels = snap.RawPixels;
                SnapshotVerdict v = SnapshotCoverage.Analyse(pixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Assert.IsFalse(v.IsBlank,
                    "Line render must not be blank (no mesh built or camera misaligned).");
                Assert.That(v.FilledFraction, Is.InRange(0.01f, 0.50f),
                    $"Line fill fraction {v.FilledFraction:P1} must be in [1%,50%]. " +
                    "A line should cover a thin band, not the whole frame.");

                Assert.IsTrue(v.Passes(minFill: 0.01f, maxFill: 0.50f, minBuckets: 2),
                    $"Line render should pass the coverage gate. " +
                    $"fill={v.FilledFraction:P1}, buckets={v.DistinctRegionBucketsHit}.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lineGo);
            }
        }

        // ─── Test 2: Width measurement ──────────────────────────────────────────────────────

        [Test]
        public void LineWidth_MeasuredOnPerpendicular_MatchesExpected()
        {
            // Use a SINGLE horizontal line for this measurement test to avoid the L-shape and
            // diagonal segments crossing the same scanline and inflating the span measurement.
            var (cameraGo, camera) = BuildCamera();
            var lineGo             = BuildSingleHorizontalLine(LineWidthMeters, out var mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);

                // GPU-context guard.
                if (snap.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — re-run as PlayMode.");
                        return;
                    }
                }

                snap.WritePng("line-width-measurement.png");

                // The horizontal line is at world z=0 → image centre row ≈ SnapH/2 = 256.
                // Measure the contiguous width at column SnapW/2.
                // Use MeasureContiguousLineWidthOnColumn to count only the tight band around the
                // center row (avoids counting gaps between multiple lines on the same scanline).
                int colX      = SnapW / 2;
                int centerRow = SnapH / 2;
                int measuredPx = MeasureContiguousWidthAroundRow(snap.RawPixels, SnapW, SnapH,
                                                                  colX, centerRow);

                float expected = ExpectedPxWidth(LineWidthMeters);

                Assert.That((float)measuredPx,
                    Is.InRange(expected - WidthTolPx, expected + WidthTolPx + 2),
                    $"Single horizontal line width on column {colX}: " +
                    $"expected ≈{expected:F1}px (±{WidthTolPx}px), measured {measuredPx}px. " +
                    $"metersPerPx={MetersPerPx:F4}, widthM={LineWidthMeters}m.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lineGo);
                Object.DestroyImmediate(mat);
            }
        }

        // ─── Test 3: No-rebuild proof ───────────────────────────────────────────────────────

        [Test]
        public void WidthChange_NoMeshRebuild_RenderedWidthChanges()
        {
            // Use a single horizontal line so the scanline only sees one band.
            var (cameraGo, camera)  = BuildCamera();
            var lineGo              = BuildSingleHorizontalLine(LineWidthMeters, out var mat);

            using var snap1 = new SnapshotRenderer(SnapW, SnapH);
            using var snap2 = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                // Render at original width.
                snap1.Render(camera);

                // GPU-context guard on snap1.
                if (snap1.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — re-run as PlayMode.");
                        return;
                    }
                }

                snap1.WritePng("line-width-before.png");

                // Change _Width on the MATERIAL WITHOUT rebuilding the mesh.
                // This is the "no-rebuild" proof: only the material uniform changes.
                mat.SetFloat("_Width", LineWidthDoubleM);

                // Render at new width.
                snap2.Render(camera);
                snap2.WritePng("line-width-after.png");

                int centerRow = SnapH / 2;
                int w1 = MeasureContiguousWidthAroundRow(snap1.RawPixels, SnapW, SnapH,
                                                          SnapW / 2, centerRow);
                int w2 = MeasureContiguousWidthAroundRow(snap2.RawPixels, SnapW, SnapH,
                                                          SnapW / 2, centerRow);

                // The width should have approximately doubled (within tolerance).
                float expectedW2 = ExpectedPxWidth(LineWidthDoubleM);
                Assert.That((float)w2,
                    Is.InRange(expectedW2 - WidthTolPx * 2, expectedW2 + WidthTolPx * 2 + 2),
                    $"After no-rebuild width change to {LineWidthDoubleM}m: " +
                    $"expected ≈{expectedW2:F1}px, measured {w2}px. " +
                    $"Before width: {w1}px.");

                // Also assert w2 > w1 (wider line after doubling width).
                Assert.That(w2, Is.GreaterThan(w1 - 1),
                    $"Width after doubling ({w2}px) should be greater than before ({w1}px).");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lineGo);
                Object.DestroyImmediate(mat);
            }
        }

        // ─── Test 4: AA transition width ───────────────────────────────────────────────────

        [Test]
        public void LineEdge_AATransition_IsAtMostThreePixelsWide()
        {
            // Render a single horizontal line and locate the top edge.
            // Walk from fully-lit pixels outward until background is reached.
            // Assert the transition spans ≤ 3px (fwidth ~1px + blur=1 tolerance).
            var (cameraGo, camera) = BuildCamera();
            var lineGo             = BuildSingleHorizontalLine(LineWidthMeters, out var mat);

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);

                // GPU-context guard.
                if (snap.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive("No GPU context — re-run as PlayMode.");
                        return;
                    }
                }

                snap.WritePng("line-aa-edge.png");

                // Scan column SnapW/2 (centre of line), find the top edge of the line band.
                int col       = SnapW / 2;
                int centerRow = SnapH / 2;

                // Find the top boundary of the contiguous line band.
                int topEdge = FindTopEdgeOfBand(snap.RawPixels, SnapW, SnapH, col, centerRow);

                Assert.That(topEdge, Is.GreaterThanOrEqualTo(0),
                    "Expected to find a non-background pixel band on the centre column.");

                // Measure the AA transition: walk from (topEdge-1) upward until background.
                // Count pixels that are between background and full-lit (partial alpha = AA feather).
                int transitionPx = MeasureTransitionBandAboveEdge(
                    snap.RawPixels, SnapW, SnapH, col, topEdge);

                Assert.That(transitionPx, Is.LessThanOrEqualTo(3),
                    $"AA transition at top edge of line (col={col}, topEdge row={topEdge}): " +
                    $"{transitionPx}px wide. Expected ≤ 3px for 1px fwidth AA.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(lineGo);
                Object.DestroyImmediate(mat);
            }
        }

        // ─── Test 5a: Non-Butt cap snapshot (Round + Square caps) ─────────────────────

        /// <summary>
        /// Snapshot test for Round caps on the golden multi-line scene.
        ///
        /// Both BuildLineScene and BuildSingleHorizontalLine previously hard-coded CapType.Butt /
        /// JoinType.Miter, leaving cap geometry unexercised in the visual path. This test renders
        /// the same golden shapes (h-line, L-shape, diagonal) with Round caps and verifies the PNG
        /// is non-trivial and passes the coverage gate.
        ///
        /// Uses the multi-line scene (same as RendersLine_WritesPng_PassesCoverage) so that fill
        /// fraction is comfortably above the 3% blank threshold that a single thin line approaches.
        ///
        /// GPU-context: marks Inconclusive if no GPU context (same as other snapshot tests).
        /// </summary>
        [Test]
        public void RoundCapScene_RendersLine_WritesPng_PassesCoverage()
        {
            var (cameraGo, camera) = BuildCamera();

            // Build all golden shapes with Round cap + Round join.
            var go = BuildLineScene(LineWidthMeters, out _, JoinType.Round, CapType.Round);
            go.name = "LineTestRoundCap";

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);

                // GPU-context guard.
                if (snap.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive(
                            "No GPU context (all-black render) — re-run as PlayMode: " +
                            "./Tools/run-tests.sh PlayMode");
                        return;
                    }
                }

                string pngPath = snap.WritePng("line-round-cap.png");
                Assert.IsTrue(File.Exists(pngPath),
                    $"line-round-cap.png must exist: {pngPath}");
                Assert.That(new FileInfo(pngPath).Length, Is.GreaterThan(500L),
                    "Round-cap PNG must be non-trivial (>500 bytes).");

                byte[] pixels = snap.RawPixels;
                SnapshotVerdict v = SnapshotCoverage.Analyse(pixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Assert.IsFalse(v.IsBlank,
                    "Round-cap line render must not be blank (multi-line scene should give >3% fill).");
                Assert.That(v.FilledFraction, Is.InRange(0.01f, 0.50f),
                    $"Round-cap fill fraction {v.FilledFraction:P1} must be in [1%,50%].");
                Assert.IsTrue(v.Passes(minFill: 0.01f, maxFill: 0.50f, minBuckets: 2),
                    $"Round-cap render must pass coverage gate. " +
                    $"fill={v.FilledFraction:P1}, buckets={v.DistinctRegionBucketsHit}.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SquareCapScene_RendersLine_WritesPng_PassesCoverage()
        {
            var (cameraGo, camera) = BuildCamera();

            // Build all golden shapes with Square cap + Miter join.
            var go = BuildLineScene(LineWidthMeters, out _, JoinType.Miter, CapType.Square);
            go.name = "LineTestSquareCap";

            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);

                // GPU-context guard.
                if (snap.IsAllBlack())
                {
                    using var blank = RenderBlank();
                    if (blank.IsAllBlack())
                    {
                        Assert.Inconclusive(
                            "No GPU context (all-black render) — re-run as PlayMode: " +
                            "./Tools/run-tests.sh PlayMode");
                        return;
                    }
                }

                string pngPath = snap.WritePng("line-square-cap.png");
                Assert.IsTrue(File.Exists(pngPath),
                    $"line-square-cap.png must exist: {pngPath}");
                Assert.That(new FileInfo(pngPath).Length, Is.GreaterThan(500L),
                    "Square-cap PNG must be non-trivial (>500 bytes).");

                byte[] pixels = snap.RawPixels;
                SnapshotVerdict v = SnapshotCoverage.Analyse(pixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Assert.IsFalse(v.IsBlank,
                    "Square-cap line render must not be blank (multi-line scene should give >3% fill).");
                Assert.That(v.FilledFraction, Is.InRange(0.01f, 0.50f),
                    $"Square-cap fill fraction {v.FilledFraction:P1} must be in [1%,50%].");
                Assert.IsTrue(v.Passes(minFill: 0.01f, maxFill: 0.50f, minBuckets: 2),
                    $"Square-cap render must pass coverage gate. " +
                    $"fill={v.FilledFraction:P1}, buckets={v.DistinctRegionBucketsHit}.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
                Object.DestroyImmediate(go);
            }
        }

        // ─── Test 5: Blank control ──────────────────────────────────────────────────────────

        [Test]
        public void BlankRender_FailsCoverageGate()
        {
            var (cameraGo, camera) = BuildCamera();
            using var snap = new SnapshotRenderer(SnapW, SnapH);
            try
            {
                snap.Render(camera);

                if (snap.IsAllBlack())
                {
                    Assert.Inconclusive(
                        "Blank-control render is all-black — no GPU context. " +
                        "Re-run as PlayMode: ./Tools/run-tests.sh PlayMode");
                    return;
                }

                byte[] pixels = snap.RawPixels;
                SnapshotVerdict v = SnapshotCoverage.Analyse(pixels, SnapW, SnapH, BgR8, BgG8, BgB8);

                Assert.IsTrue(v.IsBlank,
                    $"Blank render (no line) should be detected as blank. " +
                    $"fill={v.FilledFraction:P1}, bg={v.BackgroundFraction:P1}.");
                Assert.IsFalse(v.Passes(minFill: 0.01f, maxFill: 0.50f, minBuckets: 2),
                    "Blank render must fail the coverage gate — validates the gate has teeth.");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo);
            }
        }

        // ─── Helpers ───────────────────────────────────────────────────────────────────────

        private static SnapshotRenderer RenderBlank()
        {
            var (go, cam) = BuildCamera();
            var snap = new SnapshotRenderer(SnapW, SnapH);
            try { snap.Render(cam); }
            finally { Object.DestroyImmediate(go); }
            return snap;
        }

        /// <summary>
        /// Count contiguous non-background pixels along a vertical column.
        ///
        /// Scans from the top of the column down, finds the first and last non-background pixel,
        /// and returns (last − first + 1). Returns 0 if no non-background pixel is found.
        ///
        /// "Non-background" = Manhattan distance from (BgR8, BgG8, BgB8) > Tolerance(15).
        /// </summary>
        private static int MeasureLineWidthOnColumn(byte[] pixels, int width, int height, int col)
        {
            int first = -1;
            int last  = -1;

            for (int row = 0; row < height; row++)
            {
                int idx = (row * width + col) * 4;
                byte r  = pixels[idx];
                byte g  = pixels[idx + 1];
                byte b  = pixels[idx + 2];
                int dist = System.Math.Abs(r - BgR8) + System.Math.Abs(g - BgG8) + System.Math.Abs(b - BgB8);
                if (dist > SnapshotCoverage.Tolerance)
                {
                    if (first < 0) first = row;
                    last = row;
                }
            }

            if (first < 0) return 0;
            return last - first + 1;
        }

        /// <summary>
        /// Find the topmost row of the contiguous non-background band that contains <paramref name="centerRow"/>.
        /// Returns the top row index of the band, or -1 if no band is found.
        /// </summary>
        private static int FindTopEdgeOfBand(byte[] pixels, int width, int height, int col, int centerRow)
        {
            bool IsNonBg(int row)
            {
                if (row < 0 || row >= height) return false;
                int idx = (row * width + col) * 4;
                int dist = System.Math.Abs(pixels[idx]     - BgR8) +
                           System.Math.Abs(pixels[idx + 1] - BgG8) +
                           System.Math.Abs(pixels[idx + 2] - BgB8);
                return dist > SnapshotCoverage.Tolerance;
            }

            // Find a seed in the band.
            int seed = centerRow;
            if (!IsNonBg(seed))
            {
                bool found = false;
                for (int delta = 1; delta <= 16; delta++)
                {
                    if (IsNonBg(centerRow - delta)) { seed = centerRow - delta; found = true; break; }
                    if (IsNonBg(centerRow + delta)) { seed = centerRow + delta; found = true; break; }
                }
                if (!found) return -1;
            }

            // Walk upward to the top of the band.
            int top = seed;
            while (top - 1 >= 0 && IsNonBg(top - 1))
                top--;

            return top;
        }

        /// <summary>
        /// Measure the width of the partial-alpha AA transition band above the line's top edge.
        ///
        /// Starting from <paramref name="topEdgeRow"/> - 1 (one row above the full-lit band),
        /// counts how many rows going upward are "partial": brighter than background but below
        /// the full-lit brightness of the interior. These are the anti-aliased feather pixels.
        ///
        /// Returns 0 if the transition is a hard 1px step (ideal), ≤ 3 is acceptable for
        /// fragment-shader AA with blur=1.
        /// </summary>
        private static int MeasureTransitionBandAboveEdge(
            byte[] pixels, int width, int height, int col, int topEdgeRow)
        {
            if (topEdgeRow <= 0) return 0;

            // Sample a "full brightness" reference from the interior (a few rows below topEdge).
            int interiorRow = topEdgeRow + 2;
            if (interiorRow >= height) interiorRow = topEdgeRow;
            int interiorIdx  = (interiorRow * width + col) * 4;
            int interiorBrightness = pixels[interiorIdx] + pixels[interiorIdx + 1] + pixels[interiorIdx + 2];

            // Threshold: a pixel is "partial" (AA feather) if its brightness is between bg and
            // interior level. We use half the interior-to-background difference.
            int bgBrightness   = BgR8 + BgG8 + BgB8;
            int halfRange      = System.Math.Max((interiorBrightness - bgBrightness) / 2, 10);
            int partialThresh  = bgBrightness + halfRange; // above this → clearly lit

            int count = 0;
            for (int row = topEdgeRow - 1; row >= 0; row--)
            {
                int idx        = (row * width + col) * 4;
                int brightness = pixels[idx] + pixels[idx + 1] + pixels[idx + 2];
                int distFromBg = brightness - bgBrightness;

                // If clearly background, stop.
                if (distFromBg <= 5) break;

                // If still clearly lit (> halfRange above bg), this isn't a transition pixel.
                if (brightness >= partialThresh)
                {
                    // Still in full-lit zone — extend the edge estimate upward (shouldn't normally happen).
                    count++;
                    if (count > 10) break; // safety valve
                    continue;
                }

                // Partial pixel — AA feather.
                count++;
            }

            return count;
        }

        /// <summary>
        /// Count contiguous non-background pixels along a COLUMN, starting from <paramref name="centerRow"/>
        /// and expanding outward. Only the unbroken band touching <c>centerRow</c> is counted.
        ///
        /// "Non-background" = Manhattan distance from (BgR8, BgG8, BgB8) > SnapshotCoverage.Tolerance.
        ///
        /// This avoids the inflated measurement that <see cref="MeasureLineWidthOnColumn"/> produces when
        /// multiple parallel lines (e.g. horizontal + L-shape) lie on the same pixel column.
        /// </summary>
        private static int MeasureContiguousWidthAroundRow(
            byte[] pixels, int width, int height, int col, int centerRow)
        {
            // Helper: is pixel at (col, row) non-background?
            bool IsNonBg(int row)
            {
                if (row < 0 || row >= height) return false;
                int idx = (row * width + col) * 4;
                byte r   = pixels[idx];
                byte g   = pixels[idx + 1];
                byte b   = pixels[idx + 2];
                int dist = System.Math.Abs(r - BgR8) +
                           System.Math.Abs(g - BgG8) +
                           System.Math.Abs(b - BgB8);
                return dist > SnapshotCoverage.Tolerance;
            }

            // If the center row is background, search outward ±16px to find the nearest
            // non-background pixel (accounts for sub-pixel centering).
            int seed = centerRow;
            if (!IsNonBg(seed))
            {
                bool found = false;
                for (int delta = 1; delta <= 16; delta++)
                {
                    if (IsNonBg(centerRow - delta)) { seed = centerRow - delta; found = true; break; }
                    if (IsNonBg(centerRow + delta)) { seed = centerRow + delta; found = true; break; }
                }
                if (!found) return 0;
            }

            // Expand upward from seed.
            int top = seed;
            while (top - 1 >= 0 && IsNonBg(top - 1))
                top--;

            // Expand downward from seed.
            int bottom = seed;
            while (bottom + 1 < height && IsNonBg(bottom + 1))
                bottom++;

            return bottom - top + 1;
        }
    }
}
