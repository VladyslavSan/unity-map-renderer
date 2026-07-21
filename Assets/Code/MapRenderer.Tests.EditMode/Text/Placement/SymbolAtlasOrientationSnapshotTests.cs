// Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
// NOT registered in core-tests.csproj.
//
// S20 plan Risk de-risk (HIGH): synthetic-UV tests (BillboardMathTests/SymbolBillboardJobTests) cannot
// catch a vertical flip because they never touch the REAL uploaded atlas texture or the REAL shader. This
// test renders one REAL glyph ('A', from the committed NotoSansRegular fixture — the SAME glyph
// GlyphAtlasTextureTests/SdfDistanceFieldTests already prove is present) through the REAL
// LabelPlacementSystem + Map/Symbol shader + GlyphAtlasTexture, reads the framebuffer back, and checks:
//   1. Horizontal placement: the anchor sits at the look-at's longitude → the glyph's ink must be roughly
//      horizontally centered. This is the position axis the readback CAN assert reliably (see below).
//   2. Orientation: 'A' has a narrow apex at its OWN top and a wide crossbar/legs at its OWN bottom -- the
//      rendered glyph's bottom third must be measurably WIDER than its top third. A flipped atlas UV
//      (texture row order vs. sample convention disagreeing) inverts this -- the guard this test exists for.
//
// READBACK IS VERTICALLY MIRRORED vs ON-SCREEN. This is a headless camera→RenderTexture readback; the
// shipping on-screen path renders through URP's intermediate RT and blits to the backbuffer (that blit
// flips Y), which a direct camera→RT readback lacks -- so the readback is the vertical mirror of what ships
// (Unity's well-known render-to-texture flip; _ProjectionParams.x is -1 in both paths so the shader cannot
// branch -- see Symbol_ForwardPass's header). On-screen is the ground truth (verified live: labels upright
// and tracking their features under pan/zoom), so this test UN-MIRRORS the readback before the orientation
// check. Absolute VERTICAL position is therefore NOT asserted here -- it's a verified-live eyeball item;
// only the flip-invariant horizontal axis (1) and the un-mirrored orientation (2) are machine-checked.
//
// SUBMISSION PATH (E2, the render-layer model): system.Tick() renders through a
// REAL persistent scene MeshRenderer now -- LabelPlacementSystem's demo-path fallback LabelSlotPresenter
// creates a hidden GameObject with a MeshFilter/MeshRenderer bound to the built Mesh/Material as PART OF
// Tick() itself, so this test calls snap.Render(uCam) directly with no manual attach. This replaces the
// pre-E2 Graphics.RenderMesh submission, which rendered 0 px in headless EditMode (a harness limitation --
// same bucket as the Entities-Graphics gotcha -- confirmed with a minimal repro: a
// plain quad + the built-in URP Unlit shader submitted the same immediate-mode way also rendered nothing
// headless); a persistent MeshRenderer has no such limitation -- Unity redraws it like any scene object.
// The Map/Symbol vertex shader ignores the object-to-world/VP transform entirely (screen-space px -> clip
// via _ScreenParamsLogical), so the hidden presenter's identity transform is inert.

using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.View.Camera;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolAtlasOrientationSnapshotTests
    {
        private const int Size = 512;

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        /// <summary>Minimal <see cref="IGlyphMetricsProvider"/> over a single already-appended atlas —
        /// enough to drive the real <see cref="CodepointTextShaper"/>/<see cref="TextQuadLayout"/> pipeline
        /// for this one-glyph test without the full font-stack-resolver machinery.</summary>
        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;

            public bool TryGetAdvance(uint codepoint, out float advance)
            {
                if (_atlas.TryGetEntry(codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        [Test]
        public void RealAtlasGlyph_RendersUprightAndNearExpectedScreenPosition()
        {
            // 1. Real SDF atlas, real fixture glyph 'A' (65) -- the same glyph already proven present by
            //    GlyphAtlasTextureTests/SdfDistanceFieldTests.
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u]);
            var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);

            // 2. Real shaping + layout pipeline (not a hand-built SymbolQuad).
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            TextLayoutResult layout = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default);

            // 3. Overhead camera: white background so black text ('ink') is trivially distinguishable by
            //    RGB (NOT alpha -- blending over an opaque clear always yields alpha=1 in the readback).
            var camGo = new GameObject("SymbolOrientation_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            // Deterministic pixelWidth/pixelHeight BEFORE any framing/placement math reads them (mirrors
            // CameraTransformTests.CreatePair) -- SnapshotRenderer.Render swaps its own same-size
            // RenderTexture in temporarily and restores this one afterward.
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 }, zoom: 8.0, heading: 0.0, tilt: 0.0));

            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                float3x3.identity);

            // Anchor slightly NORTH of the look-at (positive local Z). The longitude is unchanged, so the
            // anchor stays at the look-at's X → the glyph renders horizontally centered (assertion 1). The
            // small north offset keeps it comfortably inside the frame (its exact VERTICAL landing is not
            // asserted — the off-screen RT readback mirrors it vertically; see the file header). The fraction
            // is deliberately tiny: for a straight-down camera a ground point offset by frac*altitude subtends
            // screen angle atan(frac), and a fraction picked without checking this (an earlier 0.3) can push
            // the anchor off-frame regardless of RenderTexture Size — 0.02 keeps it safely inside with margin.
            double altitude = uCam.transform.position.y;
            double3 anchorRender = frame.SceneOriginRender + new double3(0.0, 0.0, altitude * 0.02);

            var label = new LabelInstance
            {
                AnchorRender = anchorRender,
                Layout = layout,
                Paint = LabelPaint.Default, // black text; default halo is WHITE == background, so invisible here
                TextSizePx = 220f, // large -- reliably legible at the readback resolution
                SortKey = 0f,
                FeatureIndex = 0,
                // Epic A / A1 Risk R1: TileKey=0 (tile 0/0/0) is ~2e7m from this mid-latitude anchor —
                // float32-unsafe for the world-anchored AnchorLocal bake (jitter/vanish on-screen). A
                // realistic containing tile keeps the bake within one tile span (float32-safe).
                TileKey = TestTileKeys.PackedContaining(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14),
            };

            // Epic A / A1: point text now draws through the world path — pass the world base too (D7).
            var system = new LabelPlacementSystem(mapCamera, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            var snap = new SnapshotRenderer(Size, Size);
            try
            {
                system.Tick(in frame, new[] { label }, atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount,
                    "DIAGNOSTIC precondition: the label's anchor must NOT be culled (LastQuadCount should be " +
                    "1, matching the single glyph quad) -- if this is 0, the failure is a projection/culling " +
                    "bug, not a rendering bug.");

                // E2: Tick() already bound the built Mesh/Material to a real persistent scene MeshRenderer
                // (the demo fallback LabelSlotPresenter) — render straight away, no manual attach (see the
                // file header's SUBMISSION PATH paragraph; a double-attach would double-blend the SDF ink).
                snap.Render(uCam);

                byte[] px = snap.RawPixels; // RGBA32, row-major, TOP-LEFT origin (SnapshotRenderer's doc'd convention)

                // Un-mirror the off-screen readback to the ON-SCREEN orientation before analysis. This is a
                // headless camera→RenderTexture readback, which carries Unity's well-known render-to-texture
                // vertical flip: the SHIPPING on-screen path renders to URP's intermediate RT and blits to the
                // backbuffer (that blit flips Y), whereas a direct camera→RT readback lacks that blit, so it is
                // the vertical MIRROR of what ships on screen. On-screen is the ground truth (verified live in
                // the demo: labels render upright and track their features correctly under pan/zoom), and
                // _ProjectionParams.x is -1 in BOTH paths so the shader cannot branch (see Symbol_ForwardPass's
                // header). Un-mirror here so the orientation check below reads the on-screen truth; a regression
                // that broke the on-screen flip would mirror this buffer and flip the result, so the guard bites.
                FlipRowsVertically(px, Size, Size);

                AnalyzeInkRows(px, Size, Size, out int minRow, out int maxRow, out int minCol, out int maxCol,
                    out float topThirdAvgWidth, out float bottomThirdAvgWidth, out int inkPixelCount);

                Assert.Greater(inkPixelCount, 50,
                    "the rendered label must cover a meaningful number of pixels (not blank / GPU-context-failed).");

                // (1) Horizontal placement (flip-INVARIANT — the RT quirk is vertical-only, so this is the
                // position axis the off-screen readback CAN assert reliably): the anchor sits at the look-at's
                // longitude, which projects to screen-center X, so the glyph's ink must be roughly horizontally
                // centered. A gross X projection error (wrong sign / offset) would push it to an edge.
                float centerCol = (minCol + maxCol) * 0.5f;
                Assert.That(centerCol, Is.EqualTo(Size * 0.5f).Within(Size * 0.15f),
                    $"a label anchored at the look-at's longitude must render horizontally centered " +
                    $"(ink center col {centerCol:F1} of {Size}). (Absolute VERTICAL position is NOT asserted: " +
                    $"the off-screen RT readback carries Unity's render-to-texture Y-flip and on-screen vertical " +
                    $"placement is a verified-live eyeball item — see the un-mirror comment above.)");

                // (2) Orientation (un-mirrored to on-screen): 'A' is narrow at its own top (apex) and wide at
                // its own bottom (crossbar/legs) -- the bottom third of the rendered glyph must be measurably
                // wider. THIS is the guard this test exists for: a flipped atlas UV (texture row order vs. sample
                // convention disagreeing) inverts this ratio without SnapshotRenderer's vertical mirror hiding it.
                Assert.Greater(bottomThirdAvgWidth, topThirdAvgWidth * 1.3f,
                    $"'A' must render upright: its bottom third (crossbar/legs, avg width " +
                    $"{bottomThirdAvgWidth:F1}px) must be meaningfully WIDER than its top third (the apex, " +
                    $"avg width {topThirdAvgWidth:F1}px). A near-equal or reversed ratio means the atlas " +
                    $"UV is vertically flipped relative to the uploaded texture's row order.");
            }
            finally
            {
                snap.Dispose();
                system.Dispose(); // destroys the fallback presenter BEFORE its mesh (LabelPlacementSystem's own ordering)
                atlasTexture.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        /// <summary>Vertically mirrors an RGBA32 row-major buffer in place (row r ↔ row height-1-r) — used to
        /// un-mirror the off-screen snapshot readback into the on-screen orientation before analysis (see the
        /// call site: Unity's render-to-texture vertical flip).</summary>
        private static void FlipRowsVertically(byte[] rgba, int width, int height)
        {
            int stride = width * 4;
            var tmp = new byte[stride];
            for (int r = 0; r < height / 2; r++)
            {
                int top = r * stride;
                int bot = (height - 1 - r) * stride;
                System.Array.Copy(rgba, top, tmp, 0, stride);
                System.Array.Copy(rgba, bot, rgba, top, stride);
                System.Array.Copy(tmp, 0, rgba, bot, stride);
            }
        }

        /// <summary>Scans an RGBA32 buffer (row-major, top-left origin) for "ink" pixels (notably darker
        /// than the white background) and reports the ink row/column span plus the average per-row horizontal
        /// extent in the top and bottom thirds of that span.</summary>
        private static void AnalyzeInkRows(
            byte[] rgba, int width, int height,
            out int minRow, out int maxRow, out int minCol, out int maxCol,
            out float topThirdAvgWidth, out float bottomThirdAvgWidth, out int inkPixelCount)
        {
            const byte InkThreshold = 200; // background is white (255); black fill is well below this

            var rowMinCol = new int[height];
            var rowMaxCol = new int[height];
            var rowHasInk = new bool[height];
            for (int r = 0; r < height; r++)
            {
                rowMinCol[r] = int.MaxValue;
                rowMaxCol[r] = int.MinValue;
            }

            minRow = int.MaxValue;
            maxRow = int.MinValue;
            minCol = int.MaxValue;
            maxCol = int.MinValue;
            inkPixelCount = 0;

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int idx = (row * width + col) * 4;
                    byte r = rgba[idx];
                    if (r >= InkThreshold) continue;

                    inkPixelCount++;
                    rowHasInk[row] = true;
                    if (col < rowMinCol[row]) rowMinCol[row] = col;
                    if (col > rowMaxCol[row]) rowMaxCol[row] = col;
                    if (row < minRow) minRow = row;
                    if (row > maxRow) maxRow = row;
                    if (col < minCol) minCol = col;
                    if (col > maxCol) maxCol = col;
                }
            }

            if (inkPixelCount == 0)
            {
                minCol = 0;
                maxCol = 0;
                topThirdAvgWidth = 0f;
                bottomThirdAvgWidth = 0f;
                return;
            }

            int span = maxRow - minRow + 1;
            int thirdSize = math.max(1, span / 3);
            int topEnd = minRow + thirdSize;
            int bottomStart = maxRow - thirdSize;

            topThirdAvgWidth = AverageRowWidth(rowMinCol, rowMaxCol, rowHasInk, minRow, topEnd);
            bottomThirdAvgWidth = AverageRowWidth(rowMinCol, rowMaxCol, rowHasInk, bottomStart, maxRow);
        }

        private static float AverageRowWidth(int[] rowMinCol, int[] rowMaxCol, bool[] rowHasInk, int startRow, int endRow)
        {
            float sum = 0f;
            int count = 0;
            for (int r = startRow; r <= endRow; r++)
            {
                if (!rowHasInk[r]) continue;
                sum += rowMaxCol[r] - rowMinCol[r] + 1;
                count++;
            }
            return count == 0 ? 0f : sum / count;
        }
    }
}
