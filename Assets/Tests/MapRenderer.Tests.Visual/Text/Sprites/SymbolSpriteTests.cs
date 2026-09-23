// Sprite-atlas GPU/visual acceptance tests.
//
// docs/test-conventions.md: text's sprite-atlas sub-area lives in Text/Sprites/.
//
// Contents:
//   SymbolAtlasMultiPageRenderSnapshotTests  — Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
//   SymbolAtlasOrientationSnapshotTests      — Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
//   SymbolIconRenderSnapshotTests            — Unity EditMode only — off-screen GPU render of the REAL icon draw path to a PNG artifact.
//   SymbolIconResamplingTests                — Unity EditMode only — off-screen GPU renders of the REAL icon draw path.
//   SymbolTextResamplingTests                — Unity EditMode only — an off-screen GPU render of the REAL text draw path.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Tests.Text.Placement
{
    // Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
    // NOT registered in core-tests.csproj.
    //
    // A 2-page glyph atlas must render glyphs from BOTH pages correctly —
    // the Texture2DArray sample must actually read the layer BillboardVertex.Page selects, not silently fall
    // back to layer 0 (which would render page-0's leftover/garbage content, or nothing, at a page-1 UV).
    //
    // Approach (mirrors SymbolAtlasOrientationSnapshotTests' real-pipeline, off-screen-render, ink-pixel-
    // count style): render the SAME real fixture glyph ('A') twice, at the SAME anchor/text-size —
    //   (1) from a normal single-page atlas, where it packs onto Page 0 (the existing, already-proven path);
    //   (2) from a FIXED atlas sized to exactly one glyph cell, pre-filled by a dummy filler glyph so the
    //       real 'A' overflows onto Page 1.
    // If the Texture2DArray upload/vertex Page/shader array-sample chain is wired correctly, both renders
    // must produce near-identical ink coverage (same bitmap, different array layer). If the shader/vertex
    // path silently samples layer 0 regardless of Page, the Page-1 render would show page 0's filler content
    // (blank, since the filler has no bitmap) instead of 'A' — inkPage1 would collapse to ~0, failing the
    // "meaningful ink" assertion below.

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolAtlasMultiPageRenderSnapshotTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolAtlasMultiPageRenderSnapshotTests
    {
        private const int Size = 512;
        private const byte InkThreshold = 200; // background is white (255); black fill is well below this

        private static byte[] LoadFixtureBytes(string fileName)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", fileName));

        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;

            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0;
                if (_atlas.TryGetEntry(0, codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        [Test]
        public void RealAtlasGlyph_ForcedOntoPage1_RendersWithComparableInkCoverageToPage0()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadFixtureBytes("0-255.pbf.bytes")).Stacks[0];
            SdfGlyph a = stack.Glyphs[65u]; // 'A'

            // (1) Baseline: normal single-page (grow-mode) atlas — 'A' packs at Page 0.
            var page0Atlas = new GlyphAtlas();
            GlyphAtlasEntry page0Entry = page0Atlas.Append(a, 0);
            Assert.AreEqual(0, page0Entry.Page, "fixture precondition: a fresh grow-mode atlas never pages");
            Assert.AreEqual(1, page0Atlas.PageCount);

            // (2) Forced multi-page: a FIXED atlas sized to exactly one 'A' cell. A filler glyph (no
            // bitmap — its content is irrelevant, only its cell size matters) fills page 0 completely, so
            // the real 'A' — appended second — overflows onto page 1.
            int2 cellA = a.CellSize;
            var page1Atlas = new GlyphAtlas(width: cellA.x, fixedHeight: cellA.y);
            var filler = new SdfGlyph { Codepoint = 0xFFFEu, Width = a.Width, Height = a.Height, Left = 0, Top = 0, Advance = 0, Bitmap = null };
            page1Atlas.Append(filler, 0);
            GlyphAtlasEntry page1Entry = page1Atlas.Append(a, 0);
            Assert.AreEqual(1, page1Entry.Page, "fixture precondition: 'A' must have overflowed onto page 1");
            Assert.AreEqual(2, page1Atlas.PageCount);

            int inkPage0 = RenderGlyphAndCountInk(page0Atlas, a);
            int inkPage1 = RenderGlyphAndCountInk(page1Atlas, a);

            Assert.Greater(inkPage0, 50, "DIAGNOSTIC baseline: the page-0 glyph must render a meaningful number of ink pixels");
            Assert.Greater(inkPage1, 50,
                "the page-1 glyph must render a meaningful number of ink pixels -- if this is ~0, the array sample is " +
                "reading an empty/wrong Texture2DArray layer instead of layer 1 (BillboardVertex.Page not reaching the fragment).");

            // Numeric ink-coverage bound: same glyph, same anchor/size, different array layer -> near-identical
            // coverage. A generous tolerance (not a byte-exact snapshot) absorbs anti-aliasing / rasterization
            // noise between the two independent draws while still catching "wrong layer" (which would either
            // blank out or draw a completely different, unrelated bitmap).
            Assert.That((float)inkPage1, Is.EqualTo((float)inkPage0).Within(inkPage0 * 0.2f),
                $"page-1 ink coverage ({inkPage1}px) must be comparable to page-0 ({inkPage0}px) -- same bitmap, " +
                $"different Texture2DArray layer.");
        }

        /// <summary>Lays out, uploads, ticks, and off-screen-renders a single glyph from <paramref name="atlas"/>
        /// (which must already have <paramref name="glyph"/> appended), returning the rendered ink-pixel count.</summary>
        private static int RenderGlyphAndCountInk(GlyphAtlas atlas, SdfGlyph glyph)
        {
            using var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);

            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var quads = new List<SymbolQuad>();
            TextLayoutBounds bounds = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, quads);

            using var bag = new ObjectDisposalBag();
            var camGo = bag.Track(new GameObject("SymbolMultiPage_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 }, zoom: 8.0, heading: 0.0, tilt: 0.0));

            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                Rebase = float3x3.identity,
            };

            double altitude = uCam.transform.position.y;
            double3 anchorRender = frame.SceneOriginRender + new double3(0.0, 0.0, altitude * 0.02);

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, bounds.Min, bounds.Max,
                paint: SymbolPaint.Default,
                textSizePx: 220f,
                sortKey: 0f,
                featureIndex: 0,
                // a realistic containing tile keeps the world-anchored bake float32-safe
                // (TileKey=0 is ~2e7m away — see SymbolAtlasOrientationSnapshotTests' identical note).
                tileKey: TestTileKeys.PackedContaining(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14));

            // point text now draws through the world path — pass the world base too.
            using var system = new SymbolPlacementSystem(mapCamera, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            using var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            {
                // Duplicate Tick — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount, "DIAGNOSTIC precondition: the label's anchor must not be culled");

                snap.Render(uCam);
                Color32[] px = snap.Pixels.Pixels; // row-major, top-left origin

                int inkPixelCount = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].r < InkThreshold) inkPixelCount++;
                }
                return inkPixelCount;
            }
        }
    }

    // Unity EditMode only — real Camera/RenderTexture/Material/Mesh, off-screen GPU render + CPU readback.
    // NOT registered in core-tests.csproj.
    //
    // Synthetic-UV tests (BillboardMathTests/SymbolBillboardJobTests) cannot
    // catch a vertical flip because they never touch the REAL uploaded atlas texture or the REAL shader. This
    // test renders one REAL glyph ('A', from the committed NotoSansRegular fixture — the SAME glyph
    // GlyphAtlasTextureTests/SdfDistanceFieldTests already prove is present) through the REAL
    // SymbolPlacementSystem + Map/Symbol shader + GlyphAtlasTexture, reads the framebuffer back, and checks:
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
    // branch -- see Symbol_ForwardPass's header). On-screen is the ground truth (verified live: symbols upright
    // and tracking their features under pan/zoom), so this test UN-MIRRORS the readback before the orientation
    // check. Absolute VERTICAL position is therefore NOT asserted here -- it's a verified-live eyeball item;
    // only the flip-invariant horizontal axis (1) and the un-mirrored orientation (2) are machine-checked.
    //
    // SUBMISSION PATH: system.Tick() renders through a
    // REAL persistent scene MeshRenderer now -- SymbolPlacementSystem's demo-path fallback SymbolSlotPresenter
    // creates a hidden GameObject with a MeshFilter/MeshRenderer bound to the built Mesh/Material as PART OF
    // Tick() itself, so this test calls snap.Render(uCam) directly with no manual attach. Do NOT route it
    // back through Graphics.RenderMesh: an immediate-mode submission renders 0 px in headless EditMode (a
    // harness limitation, same bucket as the Entities-Graphics gotcha), while a persistent MeshRenderer is
    // redrawn by Unity like any scene object.
    // The Map/Symbol vertex shader ignores the object-to-world/VP transform entirely (screen-space px -> clip
    // via _ScreenParamsLogical), so the hidden presenter's identity transform is inert.

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolAtlasOrientationSnapshotTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolAtlasOrientationSnapshotTests : BaseTestFixture
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

            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0;
                if (_atlas.TryGetEntry(0, codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
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
            atlas.Append(stack.Glyphs[65u], 0);
            using var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);

            // 2. Real shaping + layout pipeline (not a hand-built SymbolQuad).
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(atlas) });
            var quads = new List<SymbolQuad>();
            TextLayoutBounds bounds = TextQuadLayout.Layout(run, atlas, TextLayoutOptions.Default, quads);

            // 3. Overhead camera: white background so black text ('ink') is trivially distinguishable by
            //    RGB (NOT alpha -- blending over an opaque clear always yields alpha=1 in the readback).
            var camGo = Track(new GameObject("SymbolOrientation_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            // Deterministic pixelWidth/pixelHeight BEFORE any framing/placement math reads them (mirrors
            // CameraTransformTests.CreatePair) -- SnapshotRenderer.Render swaps its own same-size
            // RenderTexture in temporarily and restores this one afterward.
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 }, zoom: 8.0, heading: 0.0, tilt: 0.0));

            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                Rebase = float3x3.identity,
            };

            // Anchor slightly NORTH of the look-at (positive local Z). The longitude is unchanged, so the
            // anchor stays at the look-at's X → the glyph renders horizontally centered (assertion 1). The
            // small north offset keeps it comfortably inside the frame (its exact VERTICAL landing is not
            // asserted — the off-screen RT readback mirrors it vertically; see the file header). The fraction
            // is deliberately tiny: for a straight-down camera a ground point offset by frac*altitude subtends
            // screen angle atan(frac), and a fraction picked without checking this (an earlier 0.3) can push
            // the anchor off-frame regardless of RenderTexture Size — 0.02 keeps it safely inside with margin.
            double altitude = uCam.transform.position.y;
            double3 anchorRender = frame.SceneOriginRender + new double3(0.0, 0.0, altitude * 0.02);

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, bounds.Min, bounds.Max,
                paint: SymbolPaint.Default, // black text; default halo is WHITE == background, so invisible here
                textSizePx: 220f, // large -- reliably legible at the readback resolution
                sortKey: 0f,
                featureIndex: 0,
                // TileKey=0 (tile 0/0/0) is ~2e7m from this mid-latitude anchor —
                // float32-unsafe for the world-anchored AnchorLocal bake (jitter/vanish on-screen). A
                // realistic containing tile keeps the bake within one tile span (float32-safe).
                tileKey: TestTileKeys.PackedContaining(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14));

            // point text now draws through the world path — pass the world base too.
            using var system = new SymbolPlacementSystem(mapCamera, worldTextBase: new Material(Shader.Find("Map/Symbol/TextWorld")));
            using var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            {
                // Duplicate Tick — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                system.Tick(in frame, plan.Build(buffer), atlasTexture);
                Assert.AreEqual(1, system.LastQuadCount,
                    "DIAGNOSTIC precondition: the label's anchor must NOT be culled (LastQuadCount should be " +
                    "1, matching the single glyph quad) -- if this is 0, the failure is a projection/culling " +
                    "bug, not a rendering bug.");

                // Tick() already bound the built Mesh/Material to a real persistent scene MeshRenderer
                // (the demo fallback SymbolSlotPresenter) — render straight away, no manual attach (see the
                // file header's SUBMISSION PATH paragraph; a double-attach would double-blend the SDF ink).
                snap.Render(uCam);

                Color32[] px = snap.Pixels.Pixels; // row-major, TOP-LEFT origin (SnapshotRenderer's doc'd convention)

                // Un-mirror the off-screen readback to the ON-SCREEN orientation before analysis. This is a
                // headless camera→RenderTexture readback, which carries Unity's well-known render-to-texture
                // vertical flip: the SHIPPING on-screen path renders to URP's intermediate RT and blits to the
                // backbuffer (that blit flips Y), whereas a direct camera→RT readback lacks that blit, so it is
                // the vertical MIRROR of what ships on screen. On-screen is the ground truth (verified live in
                // the demo: symbols render upright and track their features correctly under pan/zoom), and
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
        }

        /// <summary>Vertically mirrors an RGBA32 row-major buffer in place (row r ↔ row height-1-r) — used to
        /// un-mirror the off-screen snapshot readback into the on-screen orientation before analysis (see the
        /// call site: Unity's render-to-texture vertical flip).</summary>
        private static void FlipRowsVertically(Color32[] rgba, int width, int height)
        {
            var tmp = new Color32[width];
            for (int r = 0; r < height / 2; r++)
            {
                int top = r * width;
                int bot = (height - 1 - r) * width;
                System.Array.Copy(rgba, top, tmp, 0, width);
                System.Array.Copy(rgba, bot, rgba, top, width);
                System.Array.Copy(tmp, 0, rgba, bot, width);
            }
        }

        /// <summary>Scans a pixel buffer (row-major, top-left origin) for "ink" pixels (notably darker
        /// than the white background) and reports the ink row/column span plus the average per-row horizontal
        /// extent in the top and bottom thirds of that span.</summary>
        private static void AnalyzeInkRows(
            Color32[] rgba, int width, int height,
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
                    byte r = rgba[row * width + col].r;
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

    // Unity EditMode only — off-screen GPU render of the REAL icon draw path to a PNG artifact. NOT registered
    // in core-tests.csproj (needs Camera/RenderTexture/Material/Texture2D/SpriteSheet).
    //
    // This is the machine-checkable form of the "on-screen eyeball": it renders four DISTINCT, deliberately
    // ASYMMETRIC demo sprites (a committed fixture — an up-triangle, an "F", a down-arrow, a ring) through the REAL
    // SymbolPlacementSystem.Tick → Map/Symbol/IconWorld shader → row-flipped SpriteSheet texture, reads the framebuffer
    // back, and writes Logs/snapshots/symbol-icons.png. Asymmetric shapes make any vertical flip / horizontal
    // mirror visible (a symmetric square could not). The test asserts the frame is non-blank and that the four
    // icons' saturated colors are all present (each distinct sprite actually sampled); the human-facing check is
    // the saved PNG.
    //
    // READBACK IS VERTICALLY MIRRORED vs ON-SCREEN (Unity's render-to-texture Y-flip; the shipping path's
    // backbuffer blit flips Y, a direct camera→RT readback lacks it — see SymbolAtlasOrientationSnapshotTests'
    // header). So this un-mirrors the readback before writing the PNG, so the artifact matches on-screen truth.

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolIconRenderSnapshotTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolIconRenderSnapshotTests : BaseTestFixture
    {
        private const int Size = 512;

        private static byte[] LoadFixtureBytes(string file)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "sprites", file));

        private static string LoadFixtureText(string file)
            => File.ReadAllText(Path.Combine(Application.dataPath, "Fixtures", "sprites", file));

        private static byte[] LoadGlyphFixture(string file)
            => File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", file));

        // Minimal metrics provider over one already-appended atlas (mirrors SymbolAtlasOrientationSnapshotTests).
        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;
            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0;
                if (_atlas.TryGetEntry(0, codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f; return false;
            }
        }

        [Test]
        public void DemoIcons_RenderThroughRealIconPath_WritesPng()
        {
            // 1. Real sprite sheet via the LoadImage path (LoadImage + the row-flip that matches the glyph-atlas
            //    orientation contract) — the committed asymmetric demo fixture.
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            using var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            int2 sheetSize = sheet.View.Size;

            // 2. Overhead camera, white background so the saturated icon colors stand out.
            var camGo = Track(new GameObject("IconRender_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 },
                zoom: 8.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                Rebase = float3x3.identity,
            };

            // Icons are drawn with vertex color = white so SAMPLE(_MainTex) * color shows the sprite's true
            // RGBA (SymbolPaint.Default is BLACK text ink — it would render every icon black).
            var whitePaint = new SymbolPaint
            {
                TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f,
            };

            // Spread the four sprites across the frame (ground offsets from the look-at; fraction kept modest so
            // they stay comfortably on-frame under the straight-down camera — see the 'A' orientation test).
            double altitude = uCam.transform.position.y;
            double k = altitude * 0.20; // spread the icons toward the corners, clear of the central reference 'A'
            var placements = new (string name, double east, double north)[]
            {
                ("tri-up",     -k,  k),
                ("f-glyph",     k,  k),
                ("arrow-down", -k, -k),
                ("ring",        k, -k),
            };

            // a realistic containing tile keeps the world-anchored bake float32-safe
            // (TileKey=0 is ~2e7m away — see SymbolAtlasOrientationSnapshotTests' identical note).
            long tileKey = TestTileKeys.PackedContaining(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14);

            var buffer = new SymbolTileBuffer();
            for (int i = 0; i < placements.Length; i++)
            {
                (string name, double east, double north) = placements[i];
                // The REPACKED index — SpriteSheet relocates every sprite into its own padded cell, so the
                // raw parsed rect no longer describes the texture being bound below.
                Assert.IsTrue(sheet.View.Index.TryGetSprite(name, out SpriteEntry entry),
                    $"fixture must define sprite '{name}'");
                SymbolQuad quad = IconQuadLayout.Layout(
                    entry, sheetSize, iconSize: 2.0f, MapRenderer.Core.Text.TextAnchor.Center, float2.zero);

                // Inlined IconQuadLayout.ToLayoutResult's own bounds maths (min/max corner ± the skirt).
                float skirtPx = IconQuadLayout.SkirtPx(entry, 2.0f);
                var skirt = new float2(skirtPx, skirtPx);
                float2 boundsMin = math.min(quad.TopLeft, quad.BottomRight) + skirt;
                float2 boundsMax = math.max(quad.TopLeft, quad.BottomRight) - skirt;
                var quads = new List<SymbolQuad> { quad };
                TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender + new double3(east, 0.0, north),
                    quads, boundsMin, boundsMax,
                    kind: SymbolKind.Icon,
                    paint: whitePaint,
                    textSizePx: TextQuadLayout.OneEm, // scale 1 — matches the real StyledSymbolTileBuilder icon path
                    iconImage: name,                  // the cross-tile identity discriminant
                    allowOverlap: true,               // render diagnostic — never collision-cull
                    sortKey: 0f,
                    featureIndex: i,
                    tileKey: tileKey);
            }

            // Reference: a REAL text glyph 'A' (known upright on-screen — pinned by SymbolAtlasOrientation-
            // SnapshotTests) rendered at CENTER, so the icons' orientation can be compared against a
            // ground-truth glyph in the same frame (same camera, same un-mirror). If 'A' is upright and an
            // icon is not, the icon path has a real flip.
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadGlyphFixture("0-255.pbf.bytes")).Stacks[0];
            var glyphAtlas = new GlyphAtlas();
            glyphAtlas.Append(stack.Glyphs[65u], 0);
            using var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(glyphAtlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(glyphAtlas) });
            var glyphQuads = new List<SymbolQuad>();
            TextLayoutBounds glyphBounds = TextQuadLayout.Layout(run, glyphAtlas, TextLayoutOptions.Default, glyphQuads);
            TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender, glyphQuads, glyphBounds.Min, glyphBounds.Max,
                paint: SymbolPaint.Default, // black 'A' on white — the upright reference
                textSizePx: 90f,
                allowOverlap: true,
                sortKey: 0f,
                featureIndex: 99,
                tileKey: tileKey);

            // Point text and icons draw through the world path — the ONLY draw path; there are no screen
            // materials.
            using var system = new SymbolPlacementSystem(
                mapCamera,
                new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            using var snap = new SnapshotRenderer(Size, Size);
            // Every symbol here is Point placement (icons carry Kind = Icon, not Placement = Line), so the
            // production collect's curved-then-points split cannot reorder them — see
            // SymbolGatherParityTests for the mixed-kind (curved + point) case where the split reorders.
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            {
                // Duplicate Tick — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlasTexture, deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                system.Tick(in frame, plan.Build(buffer), atlasTexture, deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                Assert.AreEqual(buffer.Symbols.Count, plan.CollectedCount,
                    "precondition: the cross-tile dedup (fixed 4 m grid) must not merge any of these — a short " +
                    "count here would show up below as missing ink rather than as a placement bug.");
                Assert.AreEqual(5, system.LastQuadCount,
                    "all four icon quads + the reference 'A' glyph quad must place (each a 1-quad point candidate; none culled).");

                snap.Render(uCam);

                Color32[] raw = snap.Pixels.Pixels; // row-major (the raw camera→RT readback).

                // Write the human-facing artifact so it matches the ON-SCREEN orientation. The raw readback is
                // the vertical MIRROR of on-screen (Unity's render-to-texture Y-flip); SetPixels32's
                // bottom-left-origin upload re-flips it, so EncodeToPNG here lands on-screen-upright. (Feeding it
                // an already-un-mirrored buffer would double-flip — the trap this comment guards against.)
                string dir = SnapshotRenderer.GetSnapshotsDir();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "symbol-icons.png");
                var outTex = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, mipChain: false));
                outTex.SetPixels32(raw);
                outTex.Apply(updateMipmaps: false);
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(outTex));
                TestContext.Out.WriteLine($"wrote icon render snapshot: {path}");

                // Analyze the ON-SCREEN frame (un-mirror the readback) — the SAME frame SymbolAtlasOrientation-
                // SnapshotTests uses to prove text is upright, so the icon orientation is compared like-for-like.
                Color32[] px = (Color32[])raw.Clone();
                FlipRowsVertically(px, Size, Size);

                // Machine check: each distinct sprite actually sampled → its dominant hue must be present.
                // (red triangle, green F, blue arrow, orange ring — count pixels clearly of each hue.)
                CountHues(px, Size, Size, out int red, out int green, out int blue, out int orange);
                Assert.Greater(red, 100, "the red up-triangle sprite must be visible (sampled).");
                Assert.Greater(green, 100, "the green 'F' sprite must be visible (sampled).");
                Assert.Greater(blue, 100, "the blue down-arrow sprite must be visible (sampled).");
                Assert.Greater(orange, 60, "the orange ring sprite must be visible (sampled).");

                // ORIENTATION GUARD (this is the tooth the on-screen eyeball was owed for): the 'tri-up' sprite
                // is an apex-at-TOP triangle, so on screen (the un-mirrored buffer) its ink must be NARROWER at
                // the top than the bottom. A vertically-flipped icon path (the exact bug the SpriteSheet row-flip
                // caused) inverts this — the triangle would point down and this assertion goes RED.
                RedTriangleWidths(px, Size, Size, out float topWidth, out float bottomWidth);
                Assert.Greater(bottomWidth, topWidth * 1.5f,
                    $"the 'tri-up' icon must render apex-UP (upright, matching text): its bottom third " +
                    $"(width {bottomWidth:F1}px) must be clearly wider than its top third ({topWidth:F1}px). " +
                    $"A reversed ratio means the icon render path is vertically flipped (the I4 SpriteSheet " +
                    $"row-flip regression).");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // The ICON shader's along-line TANGENT branch, the one thing no CPU readback can prove:
        // the rotation happens in the vertex shader, from the projected world Tangent. So render the SAME
        // deliberately NON-SQUARE icon quad as an along-line icon on a HORIZONTAL road and on a VERTICAL
        // one, and assert the ink's long axis follows the road. Against an icon pass that ignores
        // tangentOS both renders are identical wide bars — RED on the vertical case.
        //
        // Not a golden: the assertion is a RELATION between two renders of the same content, so it needs no
        // committed pixel signature and cannot enshrine a wrong rotation the way a minted golden could.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        // Half-extents chosen 4:1 so the aspect flip dwarfs any AA-level jitter at either orientation.
        private const float LineIconHalfWidthPx = 100f;
        private const float LineIconHalfHeightPx = 25f;

        [Test]
        public void AlongLineIcon_RotatesToTheLineTangent_InkLongAxisFollowsTheRoad()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            using var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            {
                Assert.IsTrue(sheet.View.Index.TryGetSprite("arrow-down", out SpriteEntry entry),
                    "precondition: the demo fixture must define the asymmetric 'arrow-down' sprite");

                // The sprite's real UV rect, taken FROM IconQuadLayout rather than restated here, on a
                // deliberately WIDE cell — the cell footprint, not the sprite's aspect, is what makes the
                // orientation legible. Restating the formula would mean this fixture silently disagrees with
                // the production rect whenever that rect changes (it now spans the sprite's padded cell).
                SymbolQuad laidOut = IconQuadLayout.Layout(
                    entry, sheet.View.Size, 1f, MapRenderer.Core.Text.TextAnchor.Center, float2.zero);
                var cell = new SymbolQuad
                {
                    TopLeft = new float2(-LineIconHalfWidthPx, LineIconHalfHeightPx),
                    BottomRight = new float2(LineIconHalfWidthPx, -LineIconHalfHeightPx),
                    UvTopLeft = laidOut.UvTopLeft,
                    UvBottomRight = laidOut.UvBottomRight,
                    LineIndex = 0,
                };

                SymbolInk horizontal = MeasureAlongLineIconInk(
                    sheet, cell, "arrow-down", lineAngleDeg: 0f, iconRotateDeg: 0f);
                SymbolInk vertical = MeasureAlongLineIconInk(
                    sheet, cell, "arrow-down", lineAngleDeg: 90f, iconRotateDeg: 0f);

                // Precondition: the horizontal case IS the un-rotated shape, so it must read as a wide bar.
                // (This arm alone cannot discriminate — it passes with or without the tangent branch — which
                // is precisely why the vertical arm is the tooth.)
                Assert.Greater(horizontal.BoxWidth, horizontal.BoxHeight * 2,
                    $"precondition: on a horizontal road the icon must read WIDE " +
                    $"({horizontal.BoxWidth}x{horizontal.BoxHeight} px).");

                Assert.Greater(vertical.BoxHeight, vertical.BoxWidth * 2,
                    $"on a VERTICAL road the same icon must read TALL " +
                    $"({vertical.BoxWidth}x{vertical.BoxHeight} px). " +
                    $"A wide bar here means the icon pass ignored tangentOS and drew the quad unrotated — " +
                    $"every one-way arrow would point screen-right regardless of the road it sits on.");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // The SIGN of the two rotations, which the aspect tooth above cannot see. A bounding box
        // is direction-blind: a quad turned +90° and one turned −90° are the same tall box, and 180° (what
        // `road_one_way_arrow_opposite` asks for) is its own inverse. A sign error therefore survives both
        // the tangent tooth and the icon-rotate teeth while misorienting every arrow on a DIAGONAL road —
        // the common case in real OSM data. This tooth renders at 45°, where sign IS observable, and
        // measures WHERE the ink mass sits rather than how big its box is.
        //
        // ── WHY A CENTROID, AND WHY 45° ──────────────────────────────────────────────────────────────
        // Every rotation in this pipeline acts on the cell's corner offsets ABOUT THE LABEL ANCHOR, so the
        // ink centroid taken about that anchor is an exactly rotation-equivariant observable: rotating the
        // draw by φ rotates that vector by φ, no matter what the shape is. An ink bounding box is not — it
        // only records extent. 45° is the smallest road bearing at which +φ and −φ are distinguishable.
        //
        // ── THE FRAME, CALIBRATED RATHER THAN ASSUMED ────────────────────────────────────────────────
        // Which way "positive" turns in the analysed framebuffer depends on the readback's row order and on
        // whether Unity flipped the projection for the render target — the exact convention this codebase
        // has been burned by before, and NOT something to assume. So the tangent arm calibrates it, using a
        // rotation whose physical sense is known independently of any frame:
        //   · the test road turns from due-EAST to NORTH-EAST — i.e. +45° COUNTER-CLOCKWISE on the map
        //     (east = +X, north = +Z, camera north-up at heading 0);
        //   · a map-aligned line icon follows its road (that is the feature, and the shader derives the
        //     angle in the very frame it applies it, so it holds whatever the frame is);
        //   · therefore whatever signed rotation the ink centroid shows between those two renders IS the
        //     buffer's representation of +45° counter-clockwise on the map.
        // Every claim below is then read off that calibration, which is what makes them frame-independent.
        //
        // ── THE ARMS ─────────────────────────────────────────────────────────────────────────────────
        //   A  road 0°,  icon-rotate 0°   — the un-rotated reference, p₀
        //   B  road 45°, icon-rotate 0°   — must be p₀ turned +45° (the calibration, and the tangent tooth)
        //   C  road 45°, icon-rotate 90°  — differs from B by icon-rotate ALONE, so B→C isolates it.
        // MapLibre defines icon-rotate as "rotates the icon CLOCKWISE", so B→C must be −90° in the sense
        // calibrated above. 90° is used deliberately: 180°, the value `road_one_way_arrow_opposite` asks
        // for and the only value the existing icon-rotate teeth use, is its own inverse and so can never
        // expose a sign error.
        //
        // ── p₀'s OWN DIRECTION (derived from the committed fixture, not from a run) ───────────────────
        // The icon path renders a sprite upright and un-mirrored — pinned by the apex-UP 'tri-up' assertion
        // in the test above, in this same buffer. 'f-glyph' is an "F", so its ink mass sits toward the TOP
        // and the LEFT of its cell: on the committed sprite the ink centroid is at cell px (13.42, 13.33)
        // against a 32-px cell centre of (15.5, 15.5). Scaled onto a square cell of half-extent H that is
        // (−0.130·H, +0.136·H) — up-left, ≈134°, |p₀| ≈ 0.19·H.
        //
        // SPRITE: 'f-glyph', not the 'arrow-down' the aspect test uses. Measured on the committed fixture,
        // arrow-down's ink centroid sits 0.25 px (of 32) from its cell centre — the shape is very nearly
        // centroid-symmetric, so it carries no usable direction signal for a centroid measure however far
        // it is rotated. The "F" is the fixture's genuinely two-dimensional asymmetry, and a DIAGONAL p₀ is
        // what makes the arms land on clearly separated axes.
        //
        // Not a golden: every assertion is either a derived quadrant or a RELATION between two renders of
        // the same content, so no committed pixel signature can enshrine a wrong rotation.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        private const string SignToothSprite = "f-glyph";
        private const float SignToothHalfExtentPx = 100f;

        [Test]
        public void AlongLineIcon_TangentSign_InkTurnsTheSameWayTheRoadTurns()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            using var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            {
                SymbolQuad cell = SignToothCell(sheet.View, SignToothHalfExtentPx);
                SymbolInk baseline = MeasureAlongLineIconInk(
                    sheet, cell, SignToothSprite, lineAngleDeg: 0f, iconRotateDeg: 0f);
                SymbolInk road45 = MeasureAlongLineIconInk(
                    sheet, cell, SignToothSprite, lineAngleDeg: 45f, iconRotateDeg: 0f);

                AssertAnchorIsTheViewportCentre(baseline);

                // The DERIVED baseline direction: an upright, un-mirrored "F" carries its ink mass up and to
                // the left of its cell centre. Asserting it pins the absolute frame — a mirrored icon path
                // would put the mass on the other side, and would also silently invert every sign below.
                float2 p0 = baseline.CentroidFromViewportCentrePx;
                Assert.Less(p0.x, -6f,
                    $"the un-rotated 'F' must carry its ink mass LEFT of the anchor (offset {p0} px). " +
                    $"A positive x here means the icon path draws the sprite horizontally MIRRORED.");
                Assert.Greater(p0.y, 6f,
                    $"the un-rotated 'F' must carry its ink mass ABOVE the anchor (offset {p0} px). " +
                    $"A negative y here means the icon path draws the sprite vertically flipped.");

                AssertRotatedBy(p0, road45.CentroidFromViewportCentrePx, expectedDeg: 45f,
                    "the projected line TANGENT turns the icon the wrong way: the road turned 45° " +
                    "counter-clockwise (east → north-east) and the icon must turn with it. A reversed sign " +
                    "here mirrors every one-way arrow across its road on every diagonal — while leaving the " +
                    "0°/90° aspect tooth above perfectly green, since a mirrored bar is the same bar");
            }
        }

        [Test]
        public void AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            using var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            {
                SymbolQuad cell = SignToothCell(sheet.View, SignToothHalfExtentPx);
                SymbolInk baseline = MeasureAlongLineIconInk(
                    sheet, cell, SignToothSprite, lineAngleDeg: 0f, iconRotateDeg: 0f);
                SymbolInk road45 = MeasureAlongLineIconInk(
                    sheet, cell, SignToothSprite, lineAngleDeg: 45f, iconRotateDeg: 0f);
                SymbolInk road45Rotated90 = MeasureAlongLineIconInk(
                    sheet, cell, SignToothSprite, lineAngleDeg: 45f, iconRotateDeg: 90f);

                AssertAnchorIsTheViewportCentre(baseline);

                // CALIBRATION, not a duplicate of the tangent tooth: this is what fixes the SENSE of the
                // measured angle below, by pinning the buffer's +45° against a rotation whose physical
                // direction is known — the road swinging 45° counter-clockwise on the map, which the icon
                // follows. Without it a "+90°" reading below could not be called clockwise or otherwise.
                AssertRotatedBy(baseline.CentroidFromViewportCentrePx, road45.CentroidFromViewportCentrePx, expectedDeg: 45f,
                    "calibration: the icon must follow its road, so this measures the buffer's rendering of " +
                    "+45° counter-clockwise on the map. Nothing below can be interpreted without it");

                // The two 45° renders differ ONLY in icon-rotate, so this delta IS the icon-rotate term.
                // MapLibre: "icon-rotate — Rotates the icon CLOCKWISE." Clockwise is negative in the sense
                // just calibrated, so +90° of icon-rotate must show as −90°.
                AssertRotatedBy(road45.CentroidFromViewportCentrePx, road45Rotated90.CentroidFromViewportCentrePx,
                    expectedDeg: -90f,
                    "icon-rotate turns the icon the WRONG WAY: MapLibre defines icon-rotate as clockwise, " +
                    "and so does every doc comment on the value's way in (SymbolFeature.IconRotateRadians, " +
                    "SymbolStageInputs, CandidateEmit.ExtraRotationRadians) — but a positive icon-rotate is " +
                    "rendering counter-clockwise. Note 180°, the only value the other icon-rotate tests use " +
                    "and the value road_one_way_arrow_opposite asks for, is its own inverse and cannot show " +
                    "this. The sense conversion is SymbolBearing.IconRotationRadians, the ONE flip point, " +
                    "shared with the point path — a regression there breaks both paths at once");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // The map-PITCHED along-line ICON arm, rendered.
        //
        // WHY THIS EXISTS. `Map/Symbol/Icon/SymbolIconWorld_ForwardPass.hlsl` carries a map-pitch branch, and
        // that branch is LIVE IN PRODUCTION TODAY: `AlignmentResolution.ResolvePitch(Auto, Auto, LineCenter)`
        // resolves to Map, `SymbolFeatureExtractor` stamps it onto the along-line ICON SymbolFeature, and
        // `StageCurved` is shared across AtlasKind — so `road_one_way_arrow` / `road_one_way_arrow_opposite`
        // ship with AlignFlags bit2 and METRE corner offsets. Every other map-pitch rendered tooth binds
        // Map/Symbol/TextWorld and SymbolKind.Text, so without this pair the icon copy of the branch was
        // compiled and shipped but never rendered by any test. `round-caps-never-rendered` is this repo's
        // recorded cost of shipping exactly that shape.
        //
        // WHAT IS ICON-SPECIFIC ABOUT IT, i.e. why the text teeth do not cover it. Only the ICON arm carries a
        // CPU-baked constant rotation: `CandidateEmit.ExtraRotationRadians` (icon-rotate) is applied to the
        // corners by BillboardMath.BuildWorldQuad in its y-DOWN frame, and the shader THEN maps those already-
        // rotated corners into the ground frame (x̂, ŷ). Curved text leaves that constant at 0, so no text tooth
        // exercises the composition at all. The claim under test is that the two rotations compose the same way
        // on both branches:
        //     viewport:  off = Rot(cornerBaked, iconRotate)  then the shader rotates by the projected tangent
        //     map:       off = Rot(cornerBaked, iconRotate)  then the ground frame's x̂ IS the tangent
        // — so at tilt 0 the total must be identical, and a swapped composition order or a flipped rotate sign
        // must break it.
        //
        // WHY THESE PARAMETERS, none of them free choices:
        //   · tilt 0 — the ONLY pose where a map-pitched quad and a viewport one must agree exactly: the ground
        //     plane is ⊥ the view axis, so `cornerPx · metresPerLogicalPixel` metres projects to exactly
        //     `cornerPx` logical px. (Same calibration MapPitchedGlyphSizeTiltZeroTests rests on.)
        //   · road 45° — a sign or composition error is INVISIBLE on a screen-axis-aligned road; this file's own
        //     sign teeth are at 45° for that reason: a sign measurement at 0° reads a false agreement.
        //   · icon-rotate 90°, NOT 180° — 180° is its own inverse, so the only value production styles use
        //     cannot expose a sign error.
        //   · sprite 'f-glyph' — 'arrow-down' is very nearly centroid-symmetric (0.25 px of 32 from its cell
        //     centre), so it carries no usable direction signal however far it is rotated.
        //   · PathUpRender is set to world up by the harness, so the map arm takes SymbolWorldGroundFrame's
        //     GROUND branch. Without it the arm would still pass at tilt 0 — via the camera-facing fallback,
        //     which coincides there — while never exercising the branch this tooth exists for.
        //
        // THE REFERENCE IS THE VIEWPORT ARM, which shares no code with the map branch: SymbolWorldIsMapPitched
        // sends the two down mutually exclusive paths. The twins differ in EXACTLY ONE FIELD,
        // ShapedSymbol.PitchAlignment. A reference drawn from the arm under test cancels the defect it is
        // meant to expose.
        //
        // Two [Test] methods, never one with two clauses — NUnit throws on the first failure, so a second
        // clause would never run. The COUNT clause is the only one
        // that can see a uniform scale error (it reads k as k²); the CENTROID clause is the only one that can
        // see a mirror or a rotation error (a mirror is an isometry, so the count is structurally blind to it).
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Ink-count agreement bound for the map-vs-viewport icon twins. Same reasoning as
        /// <c>MapPitchedGlyphSizeTiltZeroTests</c>: a uniform scale error reads as its square here, so 2 %
        /// brackets a 1 % scale error, while the failures this exists for are gross — metres reinterpreted as
        /// pixels is a ~mpp× quad. The slack absorbs only the sub-pixel disagreement between two arithmetically
        /// different routes to one clip position (world displace → MVP, versus MVP → clip add).</summary>
        private const double MapPitchIconCountTolerance = 0.02;

        /// <summary>Ink-centroid agreement bound in px, for the same twins. A flipped rotate sign or a swapped
        /// composition order moves an `f-glyph` centroid by tens of pixels at this cell size, so the
        /// discrimination is large against this bound — the RED-verify records the achieved number.</summary>
        private const double MapPitchIconCentroidTolerancePx = 1.5;

        [Test]
        public void MapPitchedAlongLineIcon_AtTiltZero_MatchesViewport_InkCount()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            using var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            {
                SymbolQuad cell = SignToothCell(sheet.View, SignToothHalfExtentPx);
                SymbolInk viewport = MeasureAlongLineIconInk(sheet, cell, SignToothSprite,
                    lineAngleDeg: 45f, iconRotateDeg: 90f, pitchAlignment: AlignmentMode.Viewport);
                SymbolInk map = MeasureAlongLineIconInk(sheet, cell, SignToothSprite,
                    lineAngleDeg: 45f, iconRotateDeg: 90f, pitchAlignment: AlignmentMode.Map);

                double ratio = (double)map.InkCount / viewport.InkCount;
                TestContext.Out.WriteLine(
                    $"W2 icon map-vs-viewport (road 45 deg, icon-rotate 90 deg): map ink={map.InkCount} px, " +
                    $"viewport ink={viewport.InkCount} px, ratio={ratio:F5}");

                Assert.That(ratio, Is.EqualTo(1.0).Within(MapPitchIconCountTolerance),
                    $"at tilt 0 a map-PITCHED along-line icon must cover the same ink as its viewport twin — " +
                    $"map {map.InkCount} px, viewport {viewport.InkCount} px, ratio {ratio:F5}. The two labels " +
                    "differ in exactly one field, PitchAlignment. A gross ratio means the icon shader's map " +
                    "branch fed its METRE corner offsets into the logical-pixel formula (a ~mpp-times quad) " +
                    "or collapsed the quad; a clean 2x or 0.5x is a dropped or doubled DevicePixelRatio. " +
                    "road_one_way_arrow ships through this branch TODAY.");
            }
        }

        [Test]
        public void MapPitchedAlongLineIcon_AtTiltZero_MatchesViewport_InkCentroid()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            using var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            {
                SymbolQuad cell = SignToothCell(sheet.View, SignToothHalfExtentPx);
                SymbolInk viewport = MeasureAlongLineIconInk(sheet, cell, SignToothSprite,
                    lineAngleDeg: 45f, iconRotateDeg: 90f, pitchAlignment: AlignmentMode.Viewport);
                SymbolInk map = MeasureAlongLineIconInk(sheet, cell, SignToothSprite,
                    lineAngleDeg: 45f, iconRotateDeg: 90f, pitchAlignment: AlignmentMode.Map);

                // Non-vacuity: the ink must actually carry a direction. A centroid sitting on the anchor
                // would make "the two centroids agree" true for any rotation whatsoever.
                float2 v = viewport.CentroidFromViewportCentrePx;
                Assert.That(math.length(v), Is.GreaterThan(6f),
                    $"precondition: the reference arm's ink centroid sits {math.length(v):F2} px from the " +
                    "anchor — too close to carry a direction, so an agreement between the twins would be " +
                    "vacuous. 'f-glyph' is used precisely because it is two-dimensionally asymmetric.");

                float2 delta = map.CentroidFromViewportCentrePx - v;
                TestContext.Out.WriteLine(
                    $"W2 icon map-vs-viewport (road 45 deg, icon-rotate 90 deg): map centroid " +
                    $"{map.CentroidFromViewportCentrePx} px, viewport {v} px, delta {math.length(delta):F3} px");

                Assert.That(math.length(delta), Is.LessThan(MapPitchIconCentroidTolerancePx),
                    $"at tilt 0 a map-PITCHED along-line icon must land where its viewport twin does — " +
                    $"centroids {map.CentroidFromViewportCentrePx} vs {v} px about the anchor, " +
                    $"{math.length(delta):F3} px apart. This is the ONLY tooth that observes the CPU-baked " +
                    "icon-rotate composing with the shader's ground frame: a flipped rotate sign, or applying " +
                    "the rotation after the ground frame instead of before it, moves the centroid by tens of " +
                    "pixels here while leaving the ink COUNT at exactly 1.0000 (both are isometries). Road 45 " +
                    "deg and icon-rotate 90 deg are load-bearing — 0 deg and 180 deg cannot discriminate " +
                    "either error.");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // SymbolBearing.MapAlignedSign — the map-BEARING sign, the last of this file's three rotation signs.
        // It was documented as "not headlessly testable … chosen, not derived … deferred to an eyeball
        // pass", on the grounds that every headless test runs at bearing 0 where map- and viewport-alignment
        // coincide. That premise is wrong: a headless camera takes a heading like any other, and the sibling
        // constant IconRotationRadians — which carried the same "assumed correct" status until it was
        // rendered at a discriminating angle — turned out to be inverted by exactly 180°. So this renders it.
        //
        // ── THE DERIVATION (written from the contract BEFORE the render, per the icon-sign method) ────
        // SymbolBearing's stated contract: "+1 = a map heading of θ (CW from north) turns map-aligned symbols
        // by +θ in the screen's (y-up) frame." What SHOULD happen is fixed independently by what
        // rotation-alignment:map MEANS — the symbol is glued to the map plane, so it turns exactly as the map
        // turns on screen, no more and no less. Following that through:
        //   · CameraProperties.Heading is degrees CW from north, and CameraPoseMath derives the camera's
        //     up-vector from the heading direction in the horizontal plane (at heading 0 the camera sits
        //     south of the look-at, looking north). So at heading θ, screen-UP is map bearing θ and
        //     screen-RIGHT is bearing θ+90.
        //   · A map direction of bearing β therefore lands at screen angle 90° − β + θ, measured CCW from
        //     screen-right in a y-up frame (check: β = θ+90 → 0°, β = θ → 90°). Map-EAST, β = 90°, lands at
        //     θ — so raising the heading to θ turns everything drawn on the map by +θ COUNTER-CLOCKWISE on
        //     screen, and a map-aligned symbol must turn +θ with it.
        //   · The staging frame the sign feeds is positive-CCW-on-screen. That is MEASURED, not assumed —
        //     it is what AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen above pins.
        //   · BillboardRotationRadians hands the quad MapAlignedSign · θ. Matching +θ ⇒ MapAlignedSign = +1.
        // DERIVED EXPECTATION: at heading 45° the map-aligned icon's ink turns +45° (counter-clockwise on
        // screen) — the same way, and by the same amount, as the map underneath it.
        //
        // ── WHY 45°, AND WHY A CENTROID ──────────────────────────────────────────────────────────────
        // Same reasons as the icon-rotate tooth above: 0° cannot separate map from viewport alignment at
        // all, 180° is its own inverse, an axis-aligned bearing cannot separate +θ from −θ, and an ink
        // BOUNDING BOX is direction-blind while the ink centroid taken about the anchor is exactly
        // rotation-equivariant.
        //
        // ── THE MAP'S OWN TURN, MEASURED RATHER THAN ASSUMED ─────────────────────────────────────────
        // "The symbol turns +45°" is only half the contract; the other half is "…the same way the map does",
        // and asserting that against a NUMBER would smuggle the camera derivation in as an assumption. So
        // two extra arms render a VIEWPORT-aligned probe icon at a deliberately off-centre anchor, placed
        // due map-EAST of the look-at. Its quad never rotates, so its ink box centre tracks its anchor, and
        // where that anchor lands on screen IS the camera's rendering of map-east — pure projection, with no
        // symbol-rotation math in it at all. The tooth then asserts BOTH that the probe swings +45° (the
        // camera derivation, so a camera-side sign error reports as itself rather than as a symbol bug) and
        // that the symbol's turn MATCHES the probe's within a few degrees (the contract proper).
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        private const float MapBearingToothHeadingDeg = 45f;

        /// <summary>The map-east probe's anchor offset, as a fraction of the camera's altitude — ~133 px
        /// from the viewport centre at this fixture's 60° FOV and 512 px square target. Far enough out that
        /// the ~1 px gap between the "F"'s ink box centre and its cell centre is under a degree of bearing
        /// error, close enough that the probe stays comfortably on-frame at every heading.</summary>
        private const double MapEastProbeAnchorFractionOfAltitude = 0.30;

        /// <summary>Small enough that the off-centre probe never approaches the frame edge, and small enough
        /// that its ink box centre is a tight proxy for its anchor.</summary>
        private const float MapEastProbeHalfExtentPx = 30f;

        [Test]
        public void MapAlignedPointIcon_TurnsWithTheMap_UnderAnActiveBearing()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            using var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            {
                SymbolInk northUp = MeasurePointIconInk(sheet, SignToothHalfExtentPx,
                    headingDeg: 0f, AlignmentMode.Map, anchorEastFractionOfAltitude: 0.0);
                SymbolInk underBearing = MeasurePointIconInk(sheet, SignToothHalfExtentPx,
                    headingDeg: MapBearingToothHeadingDeg, AlignmentMode.Map, anchorEastFractionOfAltitude: 0.0);
                SymbolInk mapEastNorthUp = MeasurePointIconInk(sheet, MapEastProbeHalfExtentPx,
                    headingDeg: 0f, AlignmentMode.Viewport, MapEastProbeAnchorFractionOfAltitude);
                SymbolInk mapEastUnderBearing = MeasurePointIconInk(sheet, MapEastProbeHalfExtentPx,
                    headingDeg: MapBearingToothHeadingDeg, AlignmentMode.Viewport, MapEastProbeAnchorFractionOfAltitude);

                AssertAnchorIsTheViewportCentre(northUp);

                // Same absolute-frame pin as the tangent tooth: an upright, un-mirrored "F" carries its ink
                // mass up and to the LEFT. It fixes which way the buffer's y runs before any angle is read.
                float2 unrotated = northUp.CentroidFromViewportCentrePx;
                Assert.Less(unrotated.x, -6f,
                    $"the un-rotated 'F' must carry its ink mass LEFT of the anchor (offset {unrotated} px).");
                Assert.Greater(unrotated.y, 6f,
                    $"the un-rotated 'F' must carry its ink mass ABOVE the anchor (offset {unrotated} px).");

                // The probe's anchor at heading 0: due map-east, so it must sit to the screen RIGHT. This
                // also proves the offset actually reached the frame at a usable size, before it is used as
                // a bearing reference.
                float2 mapEastAtNorthUpPx = InkBoxCentreFromViewportCentrePx(mapEastNorthUp);
                Assert.Greater(mapEastAtNorthUpPx.x, 60f,
                    $"precondition: at heading 0 (north up) an anchor due map-EAST of the look-at must land " +
                    $"well to the RIGHT of the viewport centre — measured {mapEastAtNorthUpPx} px.");
                Assert.Less(math.abs(mapEastAtNorthUpPx.y), 20f,
                    $"precondition: at heading 0 that same anchor must land level with the viewport centre " +
                    $"— measured {mapEastAtNorthUpPx} px.");

                float2 mapEastUnderBearingPx = InkBoxCentreFromViewportCentrePx(mapEastUnderBearing);
                AssertRotatedBy(mapEastAtNorthUpPx, mapEastUnderBearingPx, expectedDeg: MapBearingToothHeadingDeg,
                    "CAMERA, not SymbolBearing: raising the heading to 45° (CW from north) must swing the map " +
                    "45° COUNTER-CLOCKWISE on screen, because heading θ puts map bearing θ at screen-up. This " +
                    "arm contains no label-rotation math — only where the camera projects an off-centre " +
                    "anchor — so a failure HERE is a camera-pose finding and says nothing about MapAlignedSign");

                float symbolTurnDeg = SignedRotationDeg(unrotated, underBearing.CentroidFromViewportCentrePx);
                float mapTurnDeg = SignedRotationDeg(mapEastAtNorthUpPx, mapEastUnderBearingPx);

                AssertRotatedBy(unrotated, underBearing.CentroidFromViewportCentrePx,
                    expectedDeg: MapBearingToothHeadingDeg,
                    "SymbolBearing.MapAlignedSign turns map-aligned labels the WRONG WAY: a heading of 45° " +
                    "(CW from north) must turn a rotation-alignment:map label +45° counter-clockwise on " +
                    "screen, with the map. A -1 sign turns it 45° the other way — 90° of error, invisible at " +
                    "bearing 0 (where map and viewport alignment coincide) and invisible at 180° (its own " +
                    "inverse), which is why this renders at 45");

                // The contract proper — "glued to the map" is a RELATION, and this is the only assertion
                // that states it without routing through a derived number.
                Assert.Less(math.abs(symbolTurnDeg - mapTurnDeg), 8f,
                    $"rotation-alignment:map means the label is glued to the map plane, so its on-screen " +
                    $"turn must equal the map's: the label turned {symbolTurnDeg:F1}° while the map turned " +
                    $"{mapTurnDeg:F1}° under the same 45° heading.");
            }
        }

        [Test]
        public void ViewportAlignedPointIcon_StaysUnturned_UnderAnActiveBearing()
        {
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            using var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            {
                SymbolInk northUp = MeasurePointIconInk(sheet, SignToothHalfExtentPx,
                    headingDeg: 0f, AlignmentMode.Viewport, anchorEastFractionOfAltitude: 0.0);
                SymbolInk underBearing = MeasurePointIconInk(sheet, SignToothHalfExtentPx,
                    headingDeg: MapBearingToothHeadingDeg, AlignmentMode.Viewport, anchorEastFractionOfAltitude: 0.0);

                AssertAnchorIsTheViewportCentre(northUp);

                // The control that makes the tooth above attributable: it shows the 45° turn measured there
                // comes from the ALIGNMENT MODE and nothing else — not from the camera pose, not from the
                // world-billboard construction, both of which are identical across these two renders.
                AssertRotatedBy(northUp.CentroidFromViewportCentrePx, underBearing.CentroidFromViewportCentrePx,
                    expectedDeg: 0f,
                    "rotation-alignment:viewport must ignore the map bearing entirely — the billboard stays " +
                    "screen-fixed however the map is turned under it. A non-zero turn here means the bearing " +
                    "is leaking past BillboardRotationRadians' alignment branch");
            }
        }

        /// <summary>The sign teeth's sprite and cell: a SQUARE cell (so the sprite is not distorted and the
        /// rendered angles are the cell's own), large enough that the ~0.19-of-half-extent centroid offset is
        /// tens of px — far above rasterization jitter.</summary>
        private static SymbolQuad SignToothCell(SpriteAtlasView view, float halfExtentPx)
        {
            // The REPACKED view, and IconQuadLayout's own UV rect rather than a restatement of it: the sprite
            // is relocated by SpriteSheet's padded repack, and its drawn rect spans its padded cell.
            Assert.IsTrue(view.Index.TryGetSprite(SignToothSprite, out SpriteEntry entry),
                $"precondition: the demo fixture must define the two-dimensionally asymmetric '{SignToothSprite}' sprite");
            SymbolQuad laidOut = IconQuadLayout.Layout(
                entry, view.Size, 1f, MapRenderer.Core.Text.TextAnchor.Center, float2.zero);
            return new SymbolQuad
            {
                TopLeft = new float2(-halfExtentPx, halfExtentPx),
                BottomRight = new float2(halfExtentPx, -halfExtentPx),
                UvTopLeft = laidOut.UvTopLeft,
                UvBottomRight = laidOut.UvBottomRight,
                LineIndex = 0,
            };
        }

        /// <summary>Both sign teeth measure about the symbol's anchor, which this fixture puts at the viewport
        /// centre: the camera looks straight down at <c>lookAt</c>, and the symbol's single cell sits at the
        /// path's arc midpoint, which IS <c>lookAt</c> (the path is lookAt ± dir·halfLen and the anchor is
        /// segment 0 at t = 0.5). Confirmed rather than assumed — the cell is symmetric about the anchor and
        /// the "F"'s ink box is centred in its cell to within 0.5 px of 32, so on the UN-ROTATED arm the ink
        /// box centre must land on the anchor.</summary>
        private static void AssertAnchorIsTheViewportCentre(SymbolInk baseline)
        {
            float2 boxCentreFromAnchor = InkBoxCentreFromViewportCentrePx(baseline);
            Assert.Less(math.length(boxCentreFromAnchor), 12f,
                $"precondition: the un-rotated icon's ink box must be centred on the label anchor (the " +
                $"viewport centre) — measured {math.length(boxCentreFromAnchor):F1} px away at " +
                $"{boxCentreFromAnchor}. Every offset in this test is taken about that point.");
        }

        /// <summary>Where the ink BOX's centre sits relative to the viewport centre, in the same y-up screen
        /// frame — the difference of the carrier's two offsets, since both are taken from the same centroid.
        /// For an UN-ROTATED symbol this locates the symbol's anchor on screen (the box is centred on it),
        /// which is what lets a render at an off-centre anchor report where the camera put that anchor.</summary>
        private static float2 InkBoxCentreFromViewportCentrePx(SymbolInk ink)
            => ink.CentroidFromViewportCentrePx - ink.CentroidFromInkBoxPx;

        /// <summary>Asserts <paramref name="rotated"/> is <paramref name="expectedDeg"/> around from
        /// <paramref name="reference"/>, positive being counter-clockwise in the analysed buffer (which the
        /// tangent arm calibrates against a known counter-clockwise turn on the map). The rotation is an
        /// isometry about the anchor, so the length must survive it too. 15° of slack: the failure modes
        /// this separates are 90° apart, while rasterizing a rotated shape moves a ~19 px centroid by well
        /// under a pixel.</summary>
        private static void AssertRotatedBy(float2 reference, float2 rotated, float expectedDeg, string because)
        {
            float measuredDeg = SignedRotationDeg(reference, rotated);
            string seen = $"expected {expectedDeg:F0}° but the ink turned {measuredDeg:F1}° " +
                          $"({reference} px → {rotated} px, both about the anchor).";
            Assert.Less(math.abs(measuredDeg - expectedDeg), 15f, $"{because} — {seen}");
            Assert.That(math.length(rotated), Is.EqualTo(math.length(reference)).Within(30f).Percent,
                $"a rotation about the anchor preserves the offset's LENGTH — {seen}");
        }

        /// <summary>The signed angle from <paramref name="reference"/> to <paramref name="rotated"/> in
        /// (−180, 180] degrees, positive counter-clockwise in the analysed (y-up) buffer.</summary>
        private static float SignedRotationDeg(float2 reference, float2 rotated)
        {
            float deg = math.degrees(math.atan2(rotated.y, rotated.x) - math.atan2(reference.y, reference.x));
            return deg - 360f * math.round(deg / 360f);
        }

        /// <summary>One icon render — along-line or point — measured on the un-mirrored (on-screen)
        /// framebuffer. Both offsets are in a Y-UP screen frame (x right, y up); screen rows grow downward,
        /// so the row term is negated on the way in.
        /// <para>Plain <c>{ get; set; }</c>, not <c>init</c>: MapRenderer.Tests.EditMode has no
        /// IsExternalInit polyfill of its own — the same call this assembly's other test-owned carriers make
        /// (see <c>NonMvtDecoderFanOutTests.FixtureTileLayer</c>).</para></summary>
        private struct SymbolInk
        {
            /// <summary>Ink bounding-box size in px — the direction-BLIND measure.</summary>
            public int BoxWidth { get; set; }
            public int BoxHeight { get; set; }

            /// <summary>Total inked pixels. A uniform scale error `k` shows up as `k²` here,
            /// and it is the ONLY one of these measures that can see one (a box is quantised to whole pixels
            /// and both centroids are scale-free about their own reference).</summary>
            public int InkCount { get; set; }

            /// <summary>Ink centroid minus the VIEWPORT CENTRE — which is the symbol's anchor on every arm
            /// that anchors at the look-at, making this the direction-BEARING measure a rotation about the
            /// anchor acts on exactly. Named for the fixed reference rather than for the anchor because the
            /// map-bearing tooth also renders a DELIBERATELY off-centre anchor, where the two differ.</summary>
            public float2 CentroidFromViewportCentrePx { get; set; }

            /// <summary>Ink centroid minus its own bounding-box centre. Reference-free (it needs no camera
            /// assumption), which is what makes it the cross-check that locates the anchor: subtracting it
            /// from <see cref="CentroidFromViewportCentrePx"/> leaves the ink box's own offset from the
            /// viewport centre (<see cref="InkBoxCentreFromViewportCentrePx"/>).</summary>
            public float2 CentroidFromInkBoxPx { get; set; }
        }

        // Renders ONE along-line icon of sprite `iconImage`, carrying icon-rotate `iconRotateDeg`, on a road
        // at `lineAngleDeg` (0 = east/screen-horizontal, 90 = north/screen-vertical at heading 0) through the
        // real SymbolPlacementSystem → Map/Symbol/IconWorld, and measures its on-screen ink.
        private static SymbolInk MeasureAlongLineIconInk(SpriteSheet sheet, in SymbolQuad cell,
            string iconImage, float lineAngleDeg, float iconRotateDeg,
            AlignmentMode pitchAlignment = AlignmentMode.Viewport)
        {
            using var bag = new ObjectDisposalBag();
            var camGo = bag.Track(new GameObject("AlongLineIconTangent_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 },
                zoom: 12.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };
            long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 14);

            // A road through the look-at at the requested bearing (same construction as
            // WorldCurvedAbRenderSnapshotTests.ShortLineAt — east = +X, north = +Z at zero heading/tilt).
            double altitude = uCam.transform.position.y;
            double rad = math.radians(lineAngleDeg);
            double3 dir = new double3(math.cos(rad), 0.0, math.sin(rad));
            double halfLen = altitude * 0.02;

            // The per-vertex surface normal. Web-Mercator, so up IS (0,1,0). Set UNCONDITIONALLY — it is
            // unread on the viewport path (so no existing arm moves), and it is what makes a map-pitched arm
            // take SymbolWorldGroundFrame's GROUND branch rather than its camera-facing fallback. Without it
            // the map arm would still pass at tilt 0 (the two frames coincide there) while never exercising
            // the branch the tooth exists for.
            var worldUp = new double3(0.0, 1.0, 0.0);

            var buffer = new SymbolTileBuffer();
            var path = new[] { frame.SceneOriginRender - dir * halfLen, frame.SceneOriginRender + dir * halfLen };
            var pathUp = new[] { worldUp, worldUp };
            var anchors = new[] { new LineAnchor(0, 0.5f) };
            var glyphs = new List<CurvedGlyph> { new CurvedGlyph { ArcCenter = 0f, Cell = cell } };
            TestSymbolTileBuffer.AddCurved(buffer, glyphs, anchors, path, pathUp,
                placement: SymbolPlacement.LineCenter,
                up: worldUp,
                iconImage: iconImage,
                kind: SymbolKind.Icon,
                // The ONLY field the map/viewport twins below differ in.
                pitchAlignment: pitchAlignment,
                iconRotateRadians: math.radians(iconRotateDeg),
                // White vertex color so SAMPLE(_MainTex) * color shows the sprite's own hue (SymbolPaint.Default
                // is black text ink — see the four-icon test above).
                paint: new SymbolPaint
                {
                    TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f,
                },
                textSizePx: TextQuadLayout.OneEm, // scale 1 — the cell's baked px ARE screen px
                maxAngleDeg: 180f,
                keepUpright: false,
                allowOverlap: true, // render diagnostic — never collision-cull
                sortKey: 0f,
                featureIndex: 0,
                tileKey: tileKey);

            using var system = new SymbolPlacementSystem(
                mapCamera,
                new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            // A real (if tiny) glyph atlas is REQUIRED even for an icon-only scene: SymbolPlacementSystem
            // gates its whole staging pass on `atlas?.Texture != null`, so a null one stages nothing at all.
            using GlyphAtlasTexture atlasTexture = BuildTinyGlyphAtlasTexture();
            using var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            {
                // Duplicate Tick — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlasTexture,
                    deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                system.Tick(in frame, plan.Build(buffer), atlasTexture,
                    deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                Assert.AreEqual(1, system.LastQuadCount,
                    $"DIAGNOSTIC precondition ({lineAngleDeg} deg): the along-line icon must place exactly one quad.");

                snap.Render(uCam);

                Color32[] px = (Color32[])snap.Pixels.Pixels.Clone();
                WorldSymbolInkAnalysis.FlipRowsVertically(px, Size, Size); // readback is mirrored vs on-screen
                WorldSymbolInkAnalysis.AnalyzeInk(px, Size, Size,
                    out int minRow, out int maxRow, out int minCol, out int maxCol,
                    out float centroidRow, out float centroidCol, out int ink);
                Assert.Greater(ink, 200, $"({lineAngleDeg} deg) the icon must render meaningful ink, not a blank frame.");

                // Row/col (top-left origin, rows growing DOWN) → a y-up screen frame about the viewport
                // centre, which is where this fixture's camera puts the symbol anchor (see the sign tooth's
                // reference-point precondition, which is what proves it rather than assuming it). Pixel
                // centres are index+0.5, so the centre of a Size-wide viewport is index (Size−1)/2.
                float viewportCentre = (Size - 1) * 0.5f;
                float2 inkBoxCentre = new float2((minCol + maxCol) * 0.5f, (minRow + maxRow) * 0.5f);
                var measured = new SymbolInk
                {
                    BoxWidth = maxCol - minCol + 1,
                    BoxHeight = maxRow - minRow + 1,
                    InkCount = ink,
                    CentroidFromViewportCentrePx = new float2(centroidCol - viewportCentre, viewportCentre - centroidRow),
                    CentroidFromInkBoxPx = new float2(centroidCol - inkBoxCentre.x, inkBoxCentre.y - centroidRow),
                };
                TestContext.Out.WriteLine(
                    $"along-line icon '{iconImage}' @ road {lineAngleDeg} deg, icon-rotate {iconRotateDeg} deg: " +
                    $"ink box {measured.BoxWidth}x{measured.BoxHeight} px ({ink} px), " +
                    $"centroid {measured.CentroidFromViewportCentrePx} px from the viewport centre, " +
                    $"{measured.CentroidFromInkBoxPx} px from the ink box centre");
                return measured;
            }
        }

        // Renders ONE POINT icon of sprite `SignToothSprite` in a square cell of `halfExtentPx`, with the
        // given `rotationAlignment`, under a camera at `headingDeg`, anchored `anchorEastFractionOfAltitude`
        // of the camera's altitude due map-EAST of the look-at (0 = at the look-at, i.e. the viewport
        // centre) — through the real SymbolPlacementSystem → Map/Symbol/IconWorld — and measures its
        // on-screen ink. The along-line sibling above shares everything but the symbol: this one carries a
        // Layout + AnchorRender (the point path) instead of a PathRender + CurvedGlyphs, and a heading
        // instead of a road bearing.
        private static SymbolInk MeasurePointIconInk(SpriteSheet sheet, float halfExtentPx,
            float headingDeg, AlignmentMode rotationAlignment, double anchorEastFractionOfAltitude)
        {
            using var bag = new ObjectDisposalBag();
            var camGo = bag.Track(new GameObject("MapAlignedPointIcon_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var lookAt = new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 };
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = lookAt.Latitude, Longitude = lookAt.Longitude, Altitude = 0.0 },
                zoom: 12.0, heading: headingDeg, tilt: 0.0));
            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(lookAt),
                Rebase = float3x3.identity,
            };
            long tileKey = TestTileKeys.PackedContaining(lookAt, zoom: 14);

            // East = +X in render space (the same convention the along-line road above is built on). The
            // camera orbits the look-at, so at tilt 0 its height above it is transform.position.y whatever
            // the heading.
            double altitude = uCam.transform.position.y;
            SymbolQuad cell = SignToothCell(sheet.View, halfExtentPx);

            // The cell's footprint is a deliberately fixed square, not the sprite's own rect, so it carries no
            // skirt to remove — skirtPx 0 reduces IconQuadLayout.ToLayoutResult's own bounds maths to the
            // quad's raw min/max corner.
            var cellQuads = new List<SymbolQuad> { cell };
            float2 cellBoundsMin = math.min(cell.TopLeft, cell.BottomRight);
            float2 cellBoundsMax = math.max(cell.TopLeft, cell.BottomRight);
            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer,
                frame.SceneOriginRender + new double3(altitude * anchorEastFractionOfAltitude, 0.0, 0.0),
                cellQuads, cellBoundsMin, cellBoundsMax,
                kind: SymbolKind.Icon,
                iconImage: SignToothSprite,
                rotationAlignment: rotationAlignment,
                // White vertex color so SAMPLE(_MainTex) * color shows the sprite's own hue (SymbolPaint.Default
                // is black text ink — see the four-icon test above).
                paint: new SymbolPaint
                {
                    TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f,
                },
                textSizePx: TextQuadLayout.OneEm, // scale 1 — the cell's baked px ARE screen px
                allowOverlap: true, // render diagnostic — never collision-cull
                sortKey: 0f,
                featureIndex: 0,
                tileKey: tileKey);

            using var system = new SymbolPlacementSystem(
                mapCamera,
                new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            // A real (if tiny) glyph atlas is REQUIRED even for an icon-only scene — see the along-line
            // sibling's identical note.
            using GlyphAtlasTexture atlasTexture = BuildTinyGlyphAtlasTexture();
            using var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            {
                // Duplicate Tick — the collision verdict is harvested one Tick late.
                system.Tick(in frame, plan.Build(buffer), atlasTexture,
                    deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                system.Tick(in frame, plan.Build(buffer), atlasTexture,
                    deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                Assert.AreEqual(1, system.LastQuadCount,
                    $"DIAGNOSTIC precondition (heading {headingDeg} deg, {rotationAlignment}): the point icon " +
                    $"must place exactly one quad.");

                snap.Render(uCam);

                Color32[] px = (Color32[])snap.Pixels.Pixels.Clone();
                WorldSymbolInkAnalysis.FlipRowsVertically(px, Size, Size); // readback is mirrored vs on-screen
                WorldSymbolInkAnalysis.AnalyzeInk(px, Size, Size,
                    out int minRow, out int maxRow, out int minCol, out int maxCol,
                    out float centroidRow, out float centroidCol, out int ink);
                Assert.Greater(ink, 200,
                    $"(heading {headingDeg} deg) the icon must render meaningful ink, not a blank frame.");

                float viewportCentre = (Size - 1) * 0.5f;
                float2 inkBoxCentre = new float2((minCol + maxCol) * 0.5f, (minRow + maxRow) * 0.5f);
                var measured = new SymbolInk
                {
                    BoxWidth = maxCol - minCol + 1,
                    BoxHeight = maxRow - minRow + 1,
                    InkCount = ink,
                    CentroidFromViewportCentrePx = new float2(centroidCol - viewportCentre, viewportCentre - centroidRow),
                    CentroidFromInkBoxPx = new float2(centroidCol - inkBoxCentre.x, inkBoxCentre.y - centroidRow),
                };
                TestContext.Out.WriteLine(
                    $"point icon '{SignToothSprite}' @ heading {headingDeg} deg, {rotationAlignment}, anchor " +
                    $"{anchorEastFractionOfAltitude:F2}·altitude east: ink box {measured.BoxWidth}x{measured.BoxHeight} px " +
                    $"({ink} px), centroid {measured.CentroidFromViewportCentrePx} px from the viewport centre, " +
                    $"ink box centre {InkBoxCentreFromViewportCentrePx(measured)} px from it");
                return measured;
            }
        }

        // The minimum a Tick needs to stage anything at all (see the gate note above) — one real glyph
        // uploaded to a GlyphAtlasTexture. Nothing in the icon scene ever samples it.
        private static GlyphAtlasTexture BuildTinyGlyphAtlasTexture()
        {
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadGlyphFixture("0-255.pbf.bytes")).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u], 0);
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            return texture;
        }

        /// <summary>Vertically mirrors a row-major pixel buffer in place (row r ↔ row height-1-r).</summary>
        private static void FlipRowsVertically(Color32[] rgba, int width, int height)
        {
            var tmp = new Color32[width];
            for (int r = 0; r < height / 2; r++)
            {
                int top = r * width;
                int bot = (height - 1 - r) * width;
                System.Array.Copy(rgba, top, tmp, 0, width);
                System.Array.Copy(rgba, bot, rgba, top, width);
                System.Array.Copy(tmp, 0, rgba, bot, width);
            }
        }

        /// <summary>Tally pixels whose color is dominantly red / green / blue / orange (each demo sprite's hue),
        /// on the white background. A hue counts when its channel(s) clearly dominate and it is not near-white.</summary>
        private static void CountHues(Color32[] rgba, int width, int height,
            out int red, out int green, out int blue, out int orange)
        {
            red = green = blue = orange = 0;
            for (int i = 0; i < width * height; i++)
            {
                Color32 c = rgba[i];
                int r = c.r, g = c.g, bl = c.b;
                if (r > 230 && g > 230 && bl > 230) continue; // white background
                if (r > 150 && g < 120 && bl < 120) red++;
                else if (g > 140 && r < 130 && bl < 130) green++;
                else if (bl > 150 && r < 130 && g < 150) blue++;
                else if (r > 180 && g > 110 && g < 200 && bl < 100) orange++;
            }
        }

        /// <summary>Average horizontal extent of the RED (tri-up) sprite's ink in the top third vs the bottom
        /// third of its row span. Red is unique to the up-triangle, so no masking of other sprites is needed.</summary>
        private static void RedTriangleWidths(Color32[] rgba, int width, int height, out float topWidth, out float bottomWidth)
        {
            var rowMin = new int[height];
            var rowMax = new int[height];
            var rowHas = new bool[height];
            for (int r = 0; r < height; r++) { rowMin[r] = int.MaxValue; rowMax[r] = int.MinValue; }

            int minRow = int.MaxValue, maxRow = int.MinValue;
            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    Color32 c = rgba[row * width + col];
                    if (c.r > 150 && c.g < 120 && c.b < 120) // red (tri-up)
                    {
                        rowHas[row] = true;
                        if (col < rowMin[row]) rowMin[row] = col;
                        if (col > rowMax[row]) rowMax[row] = col;
                        if (row < minRow) minRow = row;
                        if (row > maxRow) maxRow = row;
                    }
                }
            }

            if (maxRow < minRow) { topWidth = 0f; bottomWidth = 0f; return; }
            int span = maxRow - minRow + 1;
            int third = math.max(1, span / 3);
            topWidth = AvgWidth(rowMin, rowMax, rowHas, minRow, minRow + third);
            bottomWidth = AvgWidth(rowMin, rowMax, rowHas, maxRow - third, maxRow);
        }

        private static float AvgWidth(int[] rowMin, int[] rowMax, bool[] rowHas, int startRow, int endRow)
        {
            float sum = 0f; int count = 0;
            for (int r = startRow; r <= endRow; r++)
            {
                if (!rowHas[r]) continue;
                sum += rowMax[r] - rowMin[r] + 1; count++;
            }
            return count == 0 ? 0f : sum / count;
        }
    }

    // Unity EditMode only — off-screen GPU renders of the REAL icon draw path. NOT registered in
    // core-tests.csproj (needs Camera/RenderTexture/Material/Texture2D/SpriteSheet).
    //
    // These are the teeth the icon path was owed for RESAMPLING — how the sprite sheet's texels are mapped onto
    // device pixels. Every pre-existing icon test renders ONE static frame, so none of them can see a defect
    // whose whole signature is "the render changes when it should not". That is why the bug below shipped.
    //
    // The reported symptom was "pixels inside the icon warp while zooming/panning". The geometry cannot produce
    // that: BillboardMath.BuildWorldQuad gives all four corners the SAME bitwise anchorLocal plus static
    // per-corner Offset, so a quad is RIGID in screen space — an anchor precision error TRANSLATES an icon and
    // can never deform its interior. Interior deformation therefore has to be resampling, and it was:
    // SpriteSheet bound the sheet with FilterMode.Point.
    //
    // Nearest-neighbour is exact only at INTEGER magnification. An icon's magnification is
    // `iconSize * dpr / pixelRatio` — the sheet is always fetched @1x and dpr is Screen.dpi/160, so it is
    // essentially never an integer. At a non-integer magnification each source texel covers either N or N+1
    // device pixels, and WHICH depends on the quad's sub-pixel phase, so panning re-quantises the icon's
    // interior every frame. Note the trap in that: at exactly 1x, Point sampling is a pixel-perfect blit, so the
    // defect is INVISIBLE at the one setting anyone would eyeball first.

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolIconResamplingTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolIconResamplingTests
    {
        private const int Size = 256;
        private const int SheetSize = 64;

        /// <summary>
        /// The magnification (device px per source texel) the NON-sweeping teeth render at. Deliberately
        /// NON-INTEGER — at an integer magnification nearest-neighbour is exact and the interior-resampling
        /// defect does not exist. 2.5 is chosen over, say, 1.4 because it separates the two filters
        /// furthest: see <see cref="MaxCentroidDeviationPx"/>.
        ///
        /// <para>The sweeping teeth take their magnification as a <c>[TestCase]</c> parameter instead: the
        /// whole defect family is "which magnification you happen to be at", so a tooth pinned at a single
        /// one is weak. Tooth 1 sweeps only non-integer values (its defect is absent at integer
        /// magnification); <see cref="IconSilhouette_TracksSubPixelPhase_ForAFullBleedSprite"/> deliberately
        /// INCLUDES 1.0, where tooth 1 is vacuous and the silhouette defect is at its sharpest.</para>
        /// </summary>
        private const float Magnification = 2.5f;

        /// <summary>Phase step of the sweep, device px. The sweep spans one FULL device pixel — a partial
        /// sweep could sit entirely inside one tread of the nearest-neighbour staircase and read smooth.</summary>
        private const float PhaseStepPx = 0.125f;

        /// <summary>
        /// The PRIMARY tooth, in device px: the least the ink is allowed to advance for one
        /// <see cref="PhaseStepPx"/> of quad shift. Set to half the ideal step, so it states "the icon moves
        /// when the map moves" with a wide tolerance on HOW MUCH.
        ///
        /// <para>Chosen over a deviation-from-ramp bound (kept below as a secondary check) because it
        /// separates the two filters by an order of magnitude rather than a factor of 1.4. Nearest-neighbour
        /// does not move the ink AT ALL until the phase crosses a texel boundary, so its worst step is
        /// exactly <c>0.000</c> px — measured, against the un-fixed tree: the centroid sat on 129.000 px for
        /// three consecutive phases. Bilinear advances ~0.125 px every step.</para>
        /// </summary>
        private const float MinCentroidStepPx = 0.5f * PhaseStepPx;

        /// <summary>
        /// Secondary bound, device px: worst deviation of the measured centroid from the ideal ramp. Weaker
        /// than <see cref="MinCentroidStepPx"/> — measured at 0.2125 px against the un-fixed tree, so it
        /// clears this bound by only 1.4x. It is retained because it catches a defect the step test cannot:
        /// ink that advances smoothly but at the WRONG RATE.
        /// </summary>
        private const float MaxCentroidDeviationPx = 0.15f;

        /// <summary>
        /// The silhouette tooth's bounds, device px: every step of the sweep must fall between a QUARTER and
        /// THREE TIMES the ideal <see cref="PhaseStepPx"/>. Two-sided, and deliberately NOT
        /// <see cref="MinCentroidStepPx"/> — that bound is calibrated for tooth 1's probe, and cannot be
        /// reused here for a structural reason:
        ///
        /// <para>Tooth 1's ink IS its ramp (a one-texel stripe), so its centroid tracks the quad's phase
        /// almost exactly — measured min step 0.092–0.117 px against an ideal 0.125. A full-bleed sprite's
        /// ink is a SLAB whose only moving parts are the two one-texel ramps at its edges, and the readback
        /// does NOT weight those ramps by their coverage. The project renders in Linear colour space into an
        /// sRGB ARGB32 target, so the measured <c>ink = (255 - r)/255</c> is a NONLINEAR function of alpha:
        /// a half-covered pixel (alpha 0.5) reads back ink ≈ 0.265, not 0.5 — the same transfer curve
        /// <see cref="IconInk_RendersAtItsNominalSize_NotTheInsetMagnifiedSize"/>'s measurement is
        /// deliberately built to be immune to (it uses symmetric centroids for exactly this reason). The
        /// curve strongly de-weights every mid-ramp pixel, which sharpens the effective ink profile back
        /// toward the hard edge the border exists to soften, so the centroid's per-step advance is uneven
        /// even though its MEAN rate is exact. Measured after the fix: min 0.062–0.106, max 0.171–0.218
        /// across magnifications 1.0/1.37/2.5/4.0. The residual scales with a ONE-texel ramp being only
        /// <c>M</c> device px wide; a wider border would smooth it, and that is deliberately out of scope.</para>
        ///
        /// <para>What the tooth must discriminate is the STAIRCASE, and a staircase's signature is
        /// two-sided: the centroid holds EXACTLY still and then jumps a WHOLE pixel. Measured against the
        /// un-fixed tree, at every magnification: min step exactly <c>0.0000</c> px (fails the lower bound
        /// outright) and max step exactly <c>1.0000</c> px, 8× the ideal (fails the upper bound by 2.7×).
        /// Both bounds therefore separate fixed from un-fixed absolutely, with ~1.7–2.0× margin on the
        /// passing side.</para>
        /// </summary>
        private const float MinSilhouetteStepPx = 0.25f * PhaseStepPx;

        /// <summary>See <see cref="MinSilhouetteStepPx"/> — the upper half of the same two-sided bound.
        /// A whole-pixel jump (the staircase's tread-and-riser) is 8× <see cref="PhaseStepPx"/>.</summary>
        private const float MaxSilhouetteStepPx = 3f * PhaseStepPx;

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Tooth 1 — sub-pixel phase stability.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        [TestCase(1.37f)]
        [TestCase(2.5f)]
        [TestCase(3.25f)]
        public void IconInterior_TracksSubPixelPhaseSmoothly_DoesNotSnapToTheTexelGrid(float magnification)
        {
            // A ONE-TEXEL-wide opaque column is the sharpest probe available: at a non-integer magnification
            // nearest-neighbour renders it as either floor(M) or floor(M)+1 device px wide depending on
            // phase, so its centroid can only sit on the destination pixel grid.
            var index = SpriteIndex.Parse(
                $"{{\"stripe\":{{\"x\":0,\"y\":0,\"width\":{SheetSize},\"height\":{SheetSize},\"pixelRatio\":1}}}}");
            using var sheet = new SpriteSheet(BuildStripeSheetPng(), index);
            using GlyphAtlasTexture glyphAtlas = BuildMinimalGlyphAtlas();
            {
                AssertSheetDecodedAsAuthored(sheet);
                // The REPACKED index — SpriteSheet relocates every sprite into its own padded cell, so the
                // raw parsed rect no longer describes the bound texture.
                Assert.IsTrue(sheet.View.Index.TryGetSprite("stripe", out SpriteEntry entry));

                var phasesPx = new float[8];
                for (int i = 0; i < phasesPx.Length; i++) phasesPx[i] = i * PhaseStepPx;
                var centroids = new float[phasesPx.Length];

                for (int i = 0; i < phasesPx.Length; i++)
                {
                    // IconQuadLayout multiplies icon-offset by icon-size, so pre-divide to land on an exact
                    // device-px shift. The fixture's dpr is 1 (asserted below), so logical px ARE device px.
                    float2 offset = new float2(phasesPx[i] / magnification, 0f);
                    SymbolQuad quad = IconQuadLayout.Layout(
                        entry, sheet.View.Size, magnification, MapRenderer.Core.Text.TextAnchor.Center, offset);
                    centroids[i] = RenderAndMeasureInkCentroidX(sheet, glyphAtlas, quad, "stripe");
                }

                // Ideal: the ink's centroid advances by exactly the phase shift. Fit is not needed — the
                // slope is known to be 1 — so compare against the ramp anchored at the sweep's own mean,
                // which removes the arbitrary absolute position of the anchor's projection.
                float meanCentroid = 0f, meanPhase = 0f;
                for (int i = 0; i < phasesPx.Length; i++) { meanCentroid += centroids[i]; meanPhase += phasesPx[i]; }
                meanCentroid /= phasesPx.Length;
                meanPhase /= phasesPx.Length;

                float worst = 0f;
                int worstAt = 0;
                float smallestStep = float.MaxValue;
                int smallestStepAt = 0;
                var report = new System.Text.StringBuilder();
                for (int i = 0; i < phasesPx.Length; i++)
                {
                    float expected = meanCentroid + (phasesPx[i] - meanPhase);
                    float deviation = math.abs(centroids[i] - expected);
                    float step = i == 0 ? float.NaN : centroids[i] - centroids[i - 1];
                    report.Append($"\n  phase {phasesPx[i]:F3}px → centroid {centroids[i]:F4}px " +
                                  $"(expected {expected:F4}, off by {deviation:F4}" +
                                  (i == 0 ? ")" : $", step {step:+0.0000;-0.0000})"));
                    if (deviation > worst) { worst = deviation; worstAt = i; }
                    if (i > 0 && step < smallestStep) { smallestStep = step; smallestStepAt = i; }
                }

                // Record the sweep unconditionally, not only on failure: the PASSING margins are what tell
                // the next person whether these bounds are comfortable or a flake waiting for a different
                // GPU, and they cost nothing to keep in the results XML.
                TestContext.Out.WriteLine($"phase sweep (magnification {magnification}):{report}");
                TestContext.Out.WriteLine(
                    $"  smallest step {smallestStep:F4}px (bound {MinCentroidStepPx}, ideal {PhaseStepPx}) | " +
                    $"worst ramp deviation {worst:F4}px (bound {MaxCentroidDeviationPx})");

                // PRIMARY: the ink must MOVE for every sub-pixel step. A zero step is the nearest-neighbour
                // staircase's tread — the icon frozen against a map that is still panning.
                Assert.Greater(smallestStep, MinCentroidStepPx,
                    $"the icon's interior must ADVANCE for every sub-pixel shift of the quad. Smallest " +
                    $"advance was {smallestStep:F4}px at phase {phasesPx[smallestStepAt]:F3}px (bound " +
                    $"{MinCentroidStepPx}px, ideal {PhaseStepPx}px). A step at or near ZERO means the ink " +
                    $"snapped to the sheet's texel grid and held still, then jumped — which on screen is the " +
                    $"icon's interior warping as the map pans.{report}");

                // SECONDARY: smooth but at the wrong rate — a defect the step bound above cannot see.
                Assert.Less(worst, MaxCentroidDeviationPx,
                    $"the icon's ink must advance at the SAME rate as the quad, not merely advance. Worst " +
                    $"deviation from the ideal ramp {worst:F4}px at phase {phasesPx[worstAt]:F3}px " +
                    $"(bound {MaxCentroidDeviationPx}px).{report}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Tooth 2 — the atlas-bleed guard.
        //
        // This one is GREEN under nearest-neighbour and exists to fence the FIX: a bare switch to bilinear
        // over an edge-to-edge UV rect in a PACKED sheet turns it RED, because a tap at the rect's boundary
        // blends the sprite packed next door.
        //
        // What keeps it green is now the ONE-TEXEL TRANSPARENT BORDER SpriteSheet's repack lays around every
        // sprite: the sprites are physically separated in the bound texture, so an edge tap reaches the
        // border (this sprite's own colour at alpha 0) rather than the neighbour. That is a better reason
        // than the half-texel inset it replaced — the inset merely kept the sampler away from the seam, at
        // the cost of never drawing the sprite's outer half-texel.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        [Test]
        public void IconSampling_NeverBleedsTheNeighbouringSprite_AcrossThePackedSheetBoundary()
        {
            // Two sprites sharing an internal edge, in maximally-separated hues so any blend is unambiguous.
            var index = SpriteIndex.Parse(
                "{\"left\":{\"x\":0,\"y\":0,\"width\":32,\"height\":64,\"pixelRatio\":1}," +
                "\"right\":{\"x\":32,\"y\":0,\"width\":32,\"height\":64,\"pixelRatio\":1}}");
            using var sheet = new SpriteSheet(BuildTwoSpriteSheetPng(), index);
            using GlyphAtlasTexture glyphAtlas = BuildMinimalGlyphAtlas();
            {
                // The repacked index: the raw parsed rect describes the FETCHED sheet, not the bound one.
                Assert.IsTrue(sheet.View.Index.TryGetSprite("left", out SpriteEntry left));

                SymbolQuad quad = IconQuadLayout.Layout(
                    left, sheet.View.Size, Magnification, MapRenderer.Core.Text.TextAnchor.Center, float2.zero);

                Color32[] px = RenderIcon(sheet, glyphAtlas, quad, "left", SymbolPaintWhite(), Color.black, out _);

                // The left sprite is pure RED. Any pixel where blue leads red carries ink from the RIGHT
                // sprite — impossible unless the sampler reached across the rect boundary.
                int bledPixels = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    if (px[i].b > px[i].r + 24) bledPixels++;
                }

                Assert.Zero(bledPixels,
                    $"{bledPixels} pixels carry the NEIGHBOURING sprite's blue. In the FETCHED sheet these " +
                    $"two sprites abut with a zero-pixel gap, so a bilinear tap at the rect boundary would " +
                    $"blend them. SpriteSheet's one-texel transparent border is what prevents it — if this " +
                    $"went red, the repack stopped separating sprites (or the UV rect grew past its own " +
                    $"border into the next cell).");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Tooth U1 — the SILHOUETTE, for a sprite with no transparent margin of its own.
        //
        // Nothing above covers the actual reported defect. Tooth 1 probes a one-texel stripe INSIDE a sprite
        // that carries a transparent margin, so it can only see interior resampling. This one probes the
        // OUTER boundary of a sprite whose ink touches all four rect edges — which is what 228 of the 264
        // sprites in the shipped style's sheet look like.
        //
        // For such a sprite the silhouette IS the quad's polygon edge. MSAA is off project-wide
        // (Assets/Settings/RPAsset.asset m_MSAA: 1, ProjectSettings/QualitySettings.asset antiAliasing: 0),
        // so a pixel is covered iff its centre falls inside the quad — one binary sample. Every interior
        // texel being uniformly opaque, the ink is exactly the covered rectangle, its centroid is that
        // rectangle's centre, and it MOVES ONLY WHEN AN EDGE CROSSES A PIXEL CENTRE: several 0.000 steps,
        // then a whole-pixel jump. No sampler setting can help, because bilinear can only soften an edge it
        // has a transparent texel to ramp into.
        //
        // The fix is content: a one-texel transparent border in the sheet turns the silhouette into a
        // texture ALPHA edge, and the quad grows by exactly that border so the ramp is actually rasterized.
        // A "pad the atlas but leave the quad nominal" implementation stays RED here — nothing draws the
        // skirt, so no ramp reaches the framebuffer.
        //
        // Run at magnification 1.0 as well, where tooth 1 is vacuous (nearest-neighbour is a pixel-perfect
        // blit, no filter defect exists) and this one is at its sharpest — the polygon edge still snaps.
        // That case alone proves the two defects are different.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        [TestCase(1.0f)]
        [TestCase(1.37f)]
        [TestCase(2.5f)]
        [TestCase(4.0f)]
        public void IconSilhouette_TracksSubPixelPhase_ForAFullBleedSprite(float magnification)
        {
            var index = SpriteIndex.Parse(
                "{\"solid\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                "\"neighbour\":{\"x\":16,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}}");
            using var sheet = new SpriteSheet(BuildFullBleedSheetPng(), index);
            using GlyphAtlasTexture glyphAtlas = BuildMinimalGlyphAtlas();
            {
                Assert.IsTrue(sheet.View.Index.TryGetSprite("solid", out SpriteEntry entry));

                // Fixture guard — these fixtures are vertically ASYMMETRIC, so a mis-oriented author would
                // present as a resampling verdict rather than a broken fixture (it did, once).
                Assert.AreEqual(1f, sheet.Texture.GetPixel(entry.X, entry.Y).a, 1e-3f,
                    "the sprite must be FULL-BLEED: its top-left content texel must be opaque.");
                Assert.AreEqual(1f, sheet.Texture.GetPixel(
                    entry.X + entry.Width - 1, entry.Y + entry.Height - 1).a, 1e-3f,
                    "the sprite must be FULL-BLEED: its bottom-right content texel must be opaque.");

                var phasesPx = new float[8];
                for (int i = 0; i < phasesPx.Length; i++) phasesPx[i] = i * PhaseStepPx;
                var centroids = new float[phasesPx.Length];

                for (int i = 0; i < phasesPx.Length; i++)
                {
                    float2 offset = new float2(phasesPx[i] / magnification, 0f);
                    SymbolQuad quad = IconQuadLayout.Layout(
                        entry, sheet.View.Size, magnification, MapRenderer.Core.Text.TextAnchor.Center, offset);
                    centroids[i] = RenderAndMeasureInkCentroidX(sheet, glyphAtlas, quad, "solid");
                }

                float smallestStep = float.MaxValue, largestStep = float.MinValue;
                int smallestStepAt = 0, largestStepAt = 0;
                var report = new System.Text.StringBuilder();
                for (int i = 0; i < phasesPx.Length; i++)
                {
                    float step = i == 0 ? float.NaN : centroids[i] - centroids[i - 1];
                    report.Append($"\n  phase {phasesPx[i]:F3}px → centroid {centroids[i]:F4}px" +
                                  (i == 0 ? "" : $" (step {step:+0.0000;-0.0000})"));
                    if (i > 0 && step < smallestStep) { smallestStep = step; smallestStepAt = i; }
                    if (i > 0 && step > largestStep) { largestStep = step; largestStepAt = i; }
                }

                TestContext.Out.WriteLine($"full-bleed silhouette sweep (magnification {magnification}):{report}");
                TestContext.Out.WriteLine(
                    $"  smallest step {smallestStep:F4}px (bound {MinSilhouetteStepPx}) | " +
                    $"largest step {largestStep:F4}px (bound {MaxSilhouetteStepPx}) | ideal {PhaseStepPx}");

                // The staircase's TREAD: the outline frozen against a map that is still panning.
                Assert.Greater(smallestStep, MinSilhouetteStepPx,
                    $"a FULL-BLEED icon's silhouette must ADVANCE for every sub-pixel shift of the quad. " +
                    $"Smallest advance was {smallestStep:F4}px at phase {phasesPx[smallestStepAt]:F3}px " +
                    $"(bound {MinSilhouetteStepPx}px, ideal {PhaseStepPx}px). A step at or near ZERO means " +
                    $"the silhouette is still the quad's POLYGON edge getting one binary coverage " +
                    $"sample.{report}");

                // The staircase's RISER: all the motion of a whole tread released in one frame.
                Assert.Less(largestStep, MaxSilhouetteStepPx,
                    $"a FULL-BLEED icon's silhouette must advance SMOOTHLY, not in jumps. Largest advance " +
                    $"was {largestStep:F4}px at phase {phasesPx[largestStepAt]:F3}px (bound " +
                    $"{MaxSilhouetteStepPx}px, ideal {PhaseStepPx}px). A step near a WHOLE PIXEL means the " +
                    $"outline held still and then snapped — the other half of the same staircase.{report}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // Tooth U2 — the ink SIZE must be nominal.
        //
        // The half-texel UV inset made a sprite's content render W/(W-1) larger than its quad implies
        // (+14.3% at an 8px rect). Retiring it is half the fix; the other half is that the quad grows by
        // exactly the border, so the ink lands at its nominal size. This tooth fails in BOTH directions,
        // because the border can be half-applied either way round — and note that neither shallow
        // implementation is the "shrink" the naming might suggest:
        //   * quad grown, UV left on the content → 8 content texels stretched over the 80px padded quad,
        //     10 px/texel: 50.0 px. The icon GREW.
        //   * atlas padded, quad left nominal    → 10 padded texels squeezed into the 64px nominal quad,
        //     6.4 px/texel: 32.0 px. The icon SHRANK (and the skirt is never rasterized — U1 catches that
        //     one too).
        //   * the retired inset still applied    → 7 texels over 64px, 9.143 px/texel: 45.714 px.
        //
        // MEASUREMENT — NOT a coverage-threshold width. The readback is gamma-encoded (the
        // project renders in Linear colour space into an sRGB ARGB32 target), so a "50% darkness" crossing
        // does NOT sit at 50% coverage: it sits at alpha ≈ 0.79, which pulls BOTH edges inward by a
        // magnification-proportional amount comparable to the whole signal. Instead the fixture carries two
        // one-texel bars near its edges and the tooth measures the DISTANCE BETWEEN THEIR INK CENTROIDS.
        // Each bar's rendered profile is symmetric about the bar's centre, and any monotone transfer curve
        // applied to a symmetric profile leaves it symmetric — so each centroid is transfer-independent, and
        // so is their separation. It is also a direct scale measurement: the separation is a fixed number of
        // TEXELS, so it reads back exactly `texels × devicePxPerTexel`.
        // ══════════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Sheet-rect size of the U2 fixture sprite. 8 px is where the retired inset's W/(W-1)
        /// error was largest (+14.3 %).</summary>
        private const int InsetProbeSpriteSize = 8;

        /// <summary>Texel columns the two probe bars sit on, inside an 8-texel sprite.</summary>
        private const int InsetProbeLeftBarTexel = 1;
        private const int InsetProbeRightBarTexel = 6;

        /// <summary>icon-size for the U2 render. pixelRatio 1 and dpr 1 ⇒ magnification 8.</summary>
        private const float InsetProbeIconSize = 8f;

        [Test]
        public void IconInk_RendersAtItsNominalSize_NotTheInsetMagnifiedSize()
        {
            var index = SpriteIndex.Parse(
                $"{{\"bars\":{{\"x\":0,\"y\":0,\"width\":{InsetProbeSpriteSize}," +
                $"\"height\":{InsetProbeSpriteSize},\"pixelRatio\":1}}}}");
            using var sheet = new SpriteSheet(BuildTwoBarSheetPng(), index);
            using GlyphAtlasTexture glyphAtlas = BuildMinimalGlyphAtlas();
            {
                Assert.IsTrue(sheet.View.Index.TryGetSprite("bars", out SpriteEntry entry));

                // Fixture guard — as above: the bars must be where this test believes, in the sheet as bound.
                int midRow = entry.Y + InsetProbeSpriteSize / 2;
                Assert.AreEqual(1f, sheet.Texture.GetPixel(entry.X + InsetProbeLeftBarTexel, midRow).a, 1e-3f,
                    "the LEFT probe bar must be opaque in the bound sheet.");
                Assert.AreEqual(1f, sheet.Texture.GetPixel(entry.X + InsetProbeRightBarTexel, midRow).a, 1e-3f,
                    "the RIGHT probe bar must be opaque in the bound sheet.");
                Assert.AreEqual(0f, sheet.Texture.GetPixel(
                    entry.X + (InsetProbeLeftBarTexel + InsetProbeRightBarTexel) / 2, midRow).a, 1e-3f,
                    "the gutter between the bars must be transparent.");

                SymbolQuad quad = IconQuadLayout.Layout(
                    entry, sheet.View.Size, InsetProbeIconSize,
                    MapRenderer.Core.Text.TextAnchor.Center, float2.zero);
                float separation = RenderAndMeasureBarSeparationPx(sheet, glyphAtlas, quad, "bars");

                // Nominal: the bars are (6 - 1) == 5 texels apart, and one texel must draw at exactly
                // `icon-size × dpr / pixelRatio` == 8 device px. 5 × 8 == 40.
                const float nominal = (InsetProbeRightBarTexel - InsetProbeLeftBarTexel) * InsetProbeIconSize;
                // Where the retired half-texel inset put it: the quad's 64 px spanned only W-1 == 7 texels,
                // so a texel drew at 64/7 px and the bars sat 5 × 64/7 == 45.71 px apart.
                const float insetMagnified = nominal * InsetProbeSpriteSize / (InsetProbeSpriteSize - 1f);
                // Where "grow the quad but leave the UV on the content" would put it. Note the direction:
                // that implementation makes the icon LARGER, not smaller. The quad still grows to the padded
                // 10 texels' worth of px (80), but only the 8 CONTENT texels are sampled across it, so a
                // texel draws at 80/8 == 10 px and the bars sit 5 × 10 == 50 px apart.
                const float uvNotWidened =
                    nominal * (InsetProbeSpriteSize + 2f) / InsetProbeSpriteSize;
                // …and the other way round — "pad the atlas but leave the quad nominal": the 10 padded
                // texels are squeezed into the unchanged 64 px quad, i.e. 5 × 64/10 == 32 px.
                const float quadNotWidened =
                    nominal * InsetProbeSpriteSize / (InsetProbeSpriteSize + 2f);

                TestContext.Out.WriteLine(
                    $"bar separation {separation:F3}px — nominal {nominal:F3}, inset-magnified " +
                    $"{insetMagnified:F3}, uv-not-widened {uvNotWidened:F3}, quad-not-widened " +
                    $"{quadNotWidened:F3}");

                Assert.AreEqual(nominal, separation, 0.6f,
                    $"the icon's ink must draw at its NOMINAL size. Measured {separation:F3}px between the " +
                    $"two probe bars; nominal is {nominal:F3}px. {insetMagnified:F3}px means the half-texel " +
                    $"UV inset is still magnifying the content; {uvNotWidened:F3}px means the quad was grown " +
                    $"by the border but the UV rect was left on the content, so the content was stretched " +
                    $"across the padded quad and the icon GREW; {quadNotWidened:F3}px means the reverse — " +
                    $"the UV rect widened onto the border while the quad stayed nominal, so the icon SHRANK.");
            }
        }

        // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A minimal one-glyph atlas. Required even though nothing here renders TEXT:
        /// <c>SymbolPlacementSystem.TickCore</c> gates its whole placement pass on
        /// <c>atlas?.Texture != null</c>, so a null glyph atlas silently places ZERO icons. An empty
        /// <see cref="GlyphAtlas"/> will not do either — <see cref="GlyphAtlasTexture.Upload"/> no-ops at
        /// <c>Size.y == 0</c> and leaves the texture null, which trips the same gate.
        /// </summary>
        private static GlyphAtlasTexture BuildMinimalGlyphAtlas()
        {
            byte[] pbf = File.ReadAllBytes(Path.Combine(
                Application.dataPath, "Fixtures", "glyphs", "NotoSansRegular", "0-255.pbf.bytes"));
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(pbf).Stacks[0];
            var atlas = new GlyphAtlas();
            atlas.Append(stack.Glyphs[65u], 0); // 'A' — never drawn; it exists only to make the atlas non-empty
            var texture = new GlyphAtlasTexture();
            texture.Upload(atlas);
            Assert.IsNotNull(texture.Texture, "precondition: the glyph atlas texture must exist or no icon places.");
            return texture;
        }

        /// <summary>
        /// A 64x64 sheet: RGB is white EVERYWHERE and only alpha carries the shape, so bilinear taps across
        /// the stripe's edge interpolate between white and white — no dark fringe to bias the centroid, and
        /// nothing that depends on how the PNG encoder treats RGB under a zero alpha. The stripe is one
        /// texel wide, and VERTICAL, which makes it invariant under SpriteSheet's row flip.
        /// </summary>
        private static byte[] BuildStripeSheetPng()
        {
            using var bag = new ObjectDisposalBag();
            var tex = bag.Track(new Texture2D(SheetSize, SheetSize, TextureFormat.RGBA32, mipChain: false));
            var pixels = new Color32[SheetSize * SheetSize];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 0);

            const int stripeX = SheetSize / 2;
            for (int y = 2; y < SheetSize - 2; y++) // transparent margin top and bottom
                pixels[y * SheetSize + stripeX] = new Color32(255, 255, 255, 255);

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
            byte[] png = ImageConversion.EncodeToPNG(tex);
            return png;
        }

        /// <summary>
        /// The U1 fixture: a 64×64 sheet carrying <c>solid</c> at (0,0,16,16) — <b>fully opaque and
        /// uniformly black, ink touching all four rect edges</b> — abutted at (16,0,16,16) by an opaque
        /// neighbour in a different hue. Uniform RGB so nothing biases the centroid by colour; black-on-white
        /// so <see cref="RenderAndMeasureInkCentroidX"/> applies verbatim. The rest of the sheet is
        /// transparent, which is irrelevant: only <c>solid</c> is ever drawn.
        /// </summary>
        private static byte[] BuildFullBleedSheetPng()
        {
            var pixels = NewTransparentSheet();
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++) SetSpriteJsonTexel(pixels, x, y, new Color32(0, 0, 0, 255));
                for (int x = 16; x < 32; x++) SetSpriteJsonTexel(pixels, x, y, new Color32(0, 0, 255, 255));
            }
            return EncodeSheetPng(pixels);
        }

        /// <summary>
        /// The U2 fixture: an 8×8 sprite at (0,0) that is transparent except for two ONE-TEXEL opaque black
        /// bars at texel columns <see cref="InsetProbeLeftBarTexel"/> and
        /// <see cref="InsetProbeRightBarTexel"/>, inset one row top and bottom so the sprite carries a
        /// margin on every side. Two narrow bars rather than one solid block on purpose: their centroids are
        /// symmetric (hence transfer-curve-independent) and their separation is a fixed count of TEXELS, so
        /// the render reads back the drawn texel size directly.
        /// </summary>
        private static byte[] BuildTwoBarSheetPng()
        {
            var pixels = NewTransparentSheet();
            for (int y = 1; y < InsetProbeSpriteSize - 1; y++)
            {
                SetSpriteJsonTexel(pixels, InsetProbeLeftBarTexel, y, new Color32(0, 0, 0, 255));
                SetSpriteJsonTexel(pixels, InsetProbeRightBarTexel, y, new Color32(0, 0, 0, 255));
            }
            return EncodeSheetPng(pixels);
        }

        private static Color32[] NewTransparentSheet()
        {
            var pixels = new Color32[SheetSize * SheetSize];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 0);
            return pixels;
        }

        /// <summary>
        /// Writes one texel addressed in the SPRITE-JSON space (top-left origin) into a buffer destined for
        /// <see cref="Texture2D.SetPixels32"/>, which indexes BOTTOM-left. The two fixtures above are
        /// vertically asymmetric, so — unlike the stripe and left/right fixtures, which are invariant under
        /// the flip and can be written naively — they must go through this conversion or the sprite rects
        /// their JSON declares address empty space and the icon renders nothing.
        /// </summary>
        private static void SetSpriteJsonTexel(Color32[] pixels, int x, int yJson, Color32 color)
            => pixels[(SheetSize - 1 - yJson) * SheetSize + x] = color;

        private static byte[] EncodeSheetPng(Color32[] pixels)
        {
            using var bag = new ObjectDisposalBag();
            var tex = bag.Track(new Texture2D(SheetSize, SheetSize, TextureFormat.RGBA32, mipChain: false));
            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
            byte[] png = ImageConversion.EncodeToPNG(tex);
            return png;
        }

        /// <summary>Left half opaque RED, right half opaque BLUE — two sprites sharing an internal edge.</summary>
        private static byte[] BuildTwoSpriteSheetPng()
        {
            using var bag = new ObjectDisposalBag();
            var tex = bag.Track(new Texture2D(SheetSize, SheetSize, TextureFormat.RGBA32, mipChain: false));
            var pixels = new Color32[SheetSize * SheetSize];
            for (int y = 0; y < SheetSize; y++)
                for (int x = 0; x < SheetSize; x++)
                    pixels[y * SheetSize + x] = x < SheetSize / 2
                        ? new Color32(255, 0, 0, 255)
                        : new Color32(0, 0, 255, 255);

            tex.SetPixels32(pixels);
            tex.Apply(updateMipmaps: false);
            byte[] png = ImageConversion.EncodeToPNG(tex);
            return png;
        }

        /// <summary>
        /// Guards the synthetic fixture itself: proves the PNG round-trip, SpriteSheet's row flip and its
        /// padded repack left the one-texel stripe exactly where this test believes it is. Coordinates are
        /// read through the REPACKED entry — the sprite is relocated, so a hand-written sheet coordinate
        /// would now be checking the wrong texel. Without this, a mangled fixture would present as a
        /// resampling verdict.
        /// </summary>
        private static void AssertSheetDecodedAsAuthored(SpriteSheet sheet)
        {
            Assert.IsTrue(sheet.View.Index.TryGetSprite("stripe", out SpriteEntry entry));
            Assert.AreEqual(SheetSize, entry.Width, "the repack must not resize the sprite's content rect");

            int stripeX = entry.X + SheetSize / 2;
            int midY = entry.Y + SheetSize / 2;
            Assert.AreEqual(1f, sheet.Texture.GetPixel(stripeX, midY).a, 1e-3f,
                "the stripe column must be opaque after the PNG round-trip, the row flip and the repack.");
            Assert.AreEqual(0f, sheet.Texture.GetPixel(stripeX - 1, midY).a, 1e-3f,
                "the stripe must be exactly ONE texel wide — its left neighbour must be transparent.");
            Assert.AreEqual(0f, sheet.Texture.GetPixel(stripeX + 1, midY).a, 1e-3f,
                "the stripe must be exactly ONE texel wide — its right neighbour must be transparent.");
        }

        // ── render + measure ──────────────────────────────────────────────────────────────────────────

        private static SymbolPaint SymbolPaintWhite() => new SymbolPaint
        {
            TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f,
        };

        /// <summary>
        /// Renders one icon and returns the X centroid of its ink, in device px, weighted by per-pixel
        /// darkness against the white background. A centroid is deliberately preferred over an ink MASS or a
        /// measured WIDTH: it is normalised, so a monotonic transfer curve on the readback (sRGB vs linear)
        /// cannot move it the way it moves a raw sum, and it states the property under test directly — where
        /// the icon's content actually sits.
        /// </summary>
        private static float RenderAndMeasureInkCentroidX(
            SpriteSheet sheet, GlyphAtlasTexture glyphAtlas, in SymbolQuad quad, string iconName)
        {
            Color32[] px = RenderIcon(sheet, glyphAtlas, quad, iconName, SymbolPaint.Default, Color.white, out int width);

            double weighted = 0.0, total = 0.0;
            for (int p = 0; p < px.Length; p++)
            {
                double ink = (255.0 - px[p].r) / 255.0; // black ink on white
                if (ink <= 0.004) continue;           // ignore readback noise
                weighted += ink * (p % width);
                total += ink;
            }

            Assert.Greater(total, 1.0, "the icon must actually have drawn — no ink found in the frame.");
            return (float)(weighted / total);
        }

        /// <summary>
        /// Renders the two-bar fixture and returns the device-px distance between the two bars' ink
        /// centroids. Each bar's rendered profile is symmetric about the bar's own centre, and a monotone
        /// transfer curve on the readback maps a symmetric profile to a symmetric profile — so both centroids
        /// (and therefore the separation) are independent of whether the framebuffer is gamma-encoded. The
        /// split point is the midpoint of the ink's total extent, which sits in the wide transparent gutter
        /// between the bars.
        /// </summary>
        private static float RenderAndMeasureBarSeparationPx(
            SpriteSheet sheet, GlyphAtlasTexture glyphAtlas, in SymbolQuad quad, string iconName)
        {
            Color32[] px = RenderIcon(sheet, glyphAtlas, quad, iconName, SymbolPaint.Default, Color.white, out int width);

            var column = new double[width];
            for (int p = 0; p < px.Length; p++)
            {
                double ink = (255.0 - px[p].r) / 255.0; // black ink on white
                if (ink <= 0.004) continue;           // ignore readback noise
                column[p % width] += ink;
            }

            int first = -1, last = -1;
            for (int x = 0; x < width; x++)
            {
                if (column[x] <= 0.0) continue;
                if (first < 0) first = x;
                last = x;
            }
            Assert.GreaterOrEqual(first, 0, "the icon must actually have drawn — no ink found in the frame.");

            int split = (first + last) / 2;
            Assert.AreEqual(0.0, column[split], 1e-9,
                "precondition: the split column must fall in the transparent gutter BETWEEN the two bars — " +
                "ink there means the bars merged and their centroids are no longer separable.");

            double leftWeighted = 0.0, leftTotal = 0.0, rightWeighted = 0.0, rightTotal = 0.0;
            for (int x = 0; x < width; x++)
            {
                if (column[x] <= 0.0) continue;
                if (x < split) { leftWeighted += column[x] * x; leftTotal += column[x]; }
                else { rightWeighted += column[x] * x; rightTotal += column[x]; }
            }

            Assert.Greater(leftTotal, 1.0, "the LEFT probe bar must have drawn.");
            Assert.Greater(rightTotal, 1.0, "the RIGHT probe bar must have drawn.");
            return (float)(rightWeighted / rightTotal - leftWeighted / leftTotal);
        }

        /// <summary>
        /// Drives ONE icon through the real SymbolPlacementSystem → Map/Symbol/IconWorld path and returns the
        /// raw RGBA32 readback. The readback is the vertical mirror of on-screen (Unity's render-to-texture
        /// Y-flip), which is irrelevant to every measurement here — all of them are horizontal.
        /// </summary>
        private static Color32[] RenderIcon(
            SpriteSheet sheet, GlyphAtlasTexture glyphAtlas, in SymbolQuad quad, string iconName,
            SymbolPaint paint, Color background, out int width)
        {
            using var bag = new ObjectDisposalBag();
            var camGo = bag.Track(new GameObject("IconResampling_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = background;

            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 },
                zoom: 8.0, heading: 0.0, tilt: 0.0));

            // The sweep converts a logical-px icon-offset into an exact DEVICE-px phase, which only holds at
            // dpr 1. Assert it rather than assume it: a fixture default of 2 would halve every phase step and
            // quietly weaken the tooth into a sweep of half a pixel.
            Assert.AreEqual(1.0, mapCamera.DevicePixelRatio, 1e-9,
                "this fixture converts logical px to device px 1:1 — it requires dpr 1.");

            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(
                    new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                Rebase = float3x3.identity,
            };

            // A realistic containing tile keeps the world-anchored bake float32-safe (TileKey=0 is ~2e7 m
            // away) — the same note SymbolIconRenderSnapshotTests carries.
            long tileKey = TestTileKeys.PackedContaining(
                new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14);

            // Skirt 0 here, so the inline bounds formula (IconQuadLayout.ToLayoutResult's own maths) reduces
            // to the quad's raw min/max corner.
            var quads = new List<SymbolQuad> { quad };
            float2 boundsMin = math.min(quad.TopLeft, quad.BottomRight);
            float2 boundsMax = math.max(quad.TopLeft, quad.BottomRight);
            var buffer = new SymbolTileBuffer();
            // 0: the collision box is inert here (one symbol, AllowOverlap) — only quads[0], the padded quad,
            // reaches the framebuffer, and that is what every measurement reads.
            TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender, quads, boundsMin, boundsMax,
                kind: SymbolKind.Icon,
                paint: paint,
                textSizePx: TextQuadLayout.OneEm, // scale 1 — the real StyledSymbolTileBuilder icon path
                iconImage: iconName,
                allowOverlap: true,
                sortKey: 0f,
                featureIndex: 0,
                tileKey: tileKey);

            using var system = new SymbolPlacementSystem(
                mapCamera,
                new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            using var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            {
                // The collision verdict is harvested one Tick late — hence the duplicate tick.
                system.Tick(in frame, plan.Build(buffer), glyphAtlas, deltaTime: float.PositiveInfinity,
                    spriteTexture: sheet.Texture);
                system.Tick(in frame, plan.Build(buffer), glyphAtlas, deltaTime: float.PositiveInfinity,
                    spriteTexture: sheet.Texture);
                Assert.AreEqual(1, system.LastQuadCount, "the single icon quad must place (not culled).");

                snap.Render(uCam);

                width = snap.Width;
                return (Color32[])snap.Pixels.Pixels.Clone();
            }
        }
    }

    // Unity EditMode only — an off-screen GPU render of the REAL text draw path. NOT registered in
    // core-tests.csproj (needs Camera/RenderTexture/Material/Texture2D).
    //
    // The teeth the TEXT path was owed for sub-pixel stability, the sibling of SymbolIconResamplingTests.
    // Every other text render fixture draws ONE static frame, so none of them can see a defect whose whole
    // signature is "the render changes when it should not". That is why the regression below shipped.
    //
    // MSAA is off project-wide (Assets/Settings/RPAsset.asset m_MSAA: 1, ProjectSettings/QualitySettings.asset
    // antiAliasing: 0), so the SDF fragment's own coverage ramp is the ONLY antialiasing text has. A linear
    // ramp of exactly ONE device pixel is the unique width whose sampled ink is invariant to sub-pixel phase:
    // its integer shifts are a partition of unity, so the ink a stroke deposits is the same wherever the pixel
    // grid falls. Narrow the ramp and the grid starts to matter — the ink freezes for several sub-pixel steps
    // and then jumps, which on screen is a label whose glyphs morph as the map pans.

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolTextResamplingTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolTextResamplingTests : BaseTestFixture
    {
        private const int Size = 256;

        /// <summary>Phase step of the sweep, device px. Eight steps span one FULL device pixel — a partial
        /// sweep could sit entirely inside one tread of the staircase and read smooth.</summary>
        private const float PhaseStepPx = 0.125f;

        /// <summary>
        /// The two-sided bound's lower half, device px: every step of the sweep must advance the ink by at
        /// least a QUARTER of the ideal <see cref="PhaseStepPx"/>. This is the staircase's TREAD — the glyph
        /// frozen against a map that is still panning.
        ///
        /// <para>Measured smallest step: <c>0.0091px</c> at band 0.4 (the regression), <c>0.0883px</c>
        /// at band 1.0. Ideal is a uniform 0.125px.</para>
        /// </summary>
        private const float MinCentroidStepPx = 0.25f * PhaseStepPx;

        /// <summary>
        /// The upper half of the same two-sided bound — the staircase's RISER, all the motion of a whole
        /// tread released in one step.
        ///
        /// <para>Measured largest step: <c>0.2873px</c> at band 0.4, <c>0.2111px</c> at band 1.0.
        /// Both bounds are a fixed ratio of the sample pitch, not values fitted to a run — widening
        /// either to pass would defeat the tooth.</para>
        /// </summary>
        private const float MaxCentroidStepPx = 3f * PhaseStepPx;

        // ── the probe glyph ───────────────────────────────────────────────────────────────────────────

        /// <summary>Cell size of the synthetic glyph, in atlas texels. Its own metrics are this less the SDF
        /// buffer border on each side.</summary>
        private const int ProbeCellPx = 24;

        /// <summary>Width of the probe's vertical stem, in texels. Wide enough that the two edge ramps never
        /// meet, narrow enough that the whole cell holds the field's full range.</summary>
        private const int ProbeStemPx = 6;

        /// <summary>The probe's codepoint — 'I', so the real shaper resolves it like any other glyph.</summary>
        private const uint ProbeCodepoint = 'I';

        [Test]
        public void GlyphEdge_TracksSubPixelPhase_DoesNotFreezeAndJump()
        {
            var atlas = new GlyphAtlas();
            atlas.Append(BuildStemGlyph(), fontId: 0);
            var atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(atlas);
            try
            {
                Assert.IsNotNull(atlasTexture.Texture,
                    "precondition: the glyph atlas texture must exist or nothing places.");
                AssertProbeFieldIsLinear(atlas);

                var phasesPx = new float[8];
                for (int i = 0; i < phasesPx.Length; i++) phasesPx[i] = i * PhaseStepPx;
                var centroids = new float[phasesPx.Length];
                for (int i = 0; i < phasesPx.Length; i++)
                    centroids[i] = RenderAndMeasureInkCentroidX(atlas, atlasTexture, phasesPx[i]);

                float smallestStep = float.MaxValue, largestStep = float.MinValue;
                int smallestStepAt = 0, largestStepAt = 0;
                var report = new System.Text.StringBuilder();
                for (int i = 0; i < phasesPx.Length; i++)
                {
                    float step = i == 0 ? float.NaN : centroids[i] - centroids[i - 1];
                    report.Append($"\n  phase {phasesPx[i]:F3}px → centroid {centroids[i]:F4}px" +
                                  (i == 0 ? "" : $" (step {step:+0.0000;-0.0000})"));
                    if (i > 0 && step < smallestStep) { smallestStep = step; smallestStepAt = i; }
                    if (i > 0 && step > largestStep) { largestStep = step; largestStepAt = i; }
                }

                // Record the sweep unconditionally: the PASSING margins are what tell the next person whether
                // these bounds are comfortable or a flake waiting for a different GPU.
                TestContext.Out.WriteLine($"glyph-edge phase sweep:{report}");
                TestContext.Out.WriteLine(
                    $"  smallest step {smallestStep:F4}px (bound {MinCentroidStepPx}) | " +
                    $"largest step {largestStep:F4}px (bound {MaxCentroidStepPx}) | ideal {PhaseStepPx}");

                Assert.Greater(smallestStep, MinCentroidStepPx,
                    $"a glyph's ink must ADVANCE for every sub-pixel shift of its quad. Smallest advance was " +
                    $"{smallestStep:F4}px at phase {phasesPx[smallestStepAt]:F3}px (bound " +
                    $"{MinCentroidStepPx}px, ideal {PhaseStepPx}px). A step at or near ZERO means the coverage " +
                    $"ramp is too narrow for a pixel centre to land in it, so the ink holds still — on screen, " +
                    $"a label whose strokes freeze and then snap as the map pans.{report}");

                Assert.Less(largestStep, MaxCentroidStepPx,
                    $"a glyph's ink must advance SMOOTHLY, not in jumps. Largest advance was " +
                    $"{largestStep:F4}px at phase {phasesPx[largestStepAt]:F3}px (bound {MaxCentroidStepPx}px, " +
                    $"ideal {PhaseStepPx}px). A large step is the other half of the same staircase — the ink " +
                    $"releases a whole tread's worth of motion in one frame.{report}");
            }
            finally
            {
                atlasTexture.Dispose();
            }
        }

        /// <summary>
        /// The band width lives in TWO places — the shader's declared default and the committed material —
        /// and only the material's value ships. The sweep above renders a material built straight from the
        /// shader, so it pins the default; this pins the committed asset to the same value. A value fixed in
        /// one copy and left stale in the other is what produced the regression.
        /// </summary>
        [Test]
        public void ShippedMaterial_CarriesTheSameAaBandAsTheShaderDefault()
        {
            int band = Shader.PropertyToID("_SdfAaDevicePx");
            Material shipped = MapMaterialSetTestUtil.Load().SymbolTextWorld;
            var fresh = Track(new Material(Shader.Find("Map/Symbol/TextWorld")));
            Assert.AreEqual(fresh.GetFloat(band), shipped.GetFloat(band), 1e-4f,
                $"the committed MapSymbolTextWorld material's _SdfAaDevicePx ({shipped.GetFloat(band)}) " +
                $"must match the shader's declared default ({fresh.GetFloat(band)}) — the sweep above only " +
                $"proves the DEFAULT antialiases, and the material is what the map actually draws with.");
        }

        // ── fixture ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A synthetic glyph whose field is an EXACT linear ramp in x: a vertical stem
        /// <see cref="ProbeStemPx"/> texels wide, centred in a <see cref="ProbeCellPx"/> cell, every row
        /// identical. The shipped fragment recovers a signed device-px distance as
        /// <c>(sample - iso) * screenPxRange</c>, so authoring the field with the matching slope makes the
        /// rendered edge position analytic — and a stem is horizontally symmetric, which is what keeps the
        /// ink centroid independent of the readback's transfer curve.
        /// </summary>
        private static SdfGlyph BuildStemGlyph()
        {
            var bitmap = new byte[ProbeCellPx * ProbeCellPx];
            for (int x = 0; x < ProbeCellPx; x++)
            {
                byte value = FieldByteAt(x + 0.5f);
                for (int y = 0; y < ProbeCellPx; y++) bitmap[y * ProbeCellPx + x] = value;
            }

            return new SdfGlyph
            {
                Codepoint = ProbeCodepoint,
                Width = ProbeCellPx - 2 * GlyphSdf.Buffer,
                Height = ProbeCellPx - 2 * GlyphSdf.Buffer,
                Left = 0,
                Top = -(ProbeCellPx - 2 * GlyphSdf.Buffer),
                Advance = ProbeCellPx,
                Bitmap = bitmap,
            };
        }

        /// <summary>The field's encoded value at a cell coordinate: the iso level offset by the signed
        /// distance to the stem edge, scaled by the bake's texel range.</summary>
        private static byte FieldByteAt(float cellX)
        {
            float signedPx = 0.5f * ProbeStemPx - math.abs(cellX - 0.5f * ProbeCellPx);
            float value = ProbeIso + signedPx / ProbeRangeTexels;
            return (byte)math.clamp(math.round(value * 255f), 0f, 255f);
        }

        /// <summary>The material's <c>_SdfEdge</c> — the on-disk fontnik iso (191/255), which the shader's
        /// own default carries.</summary>
        private const float ProbeIso = 0.75f;

        /// <summary>The material's <c>_SdfRangeTexels</c> — how many texels one unit of field value spans.</summary>
        private const float ProbeRangeTexels = 8f;

        /// <summary>
        /// Fixture guard: proves the authored field really does cross the iso where this test believes, and
        /// really is linear around the crossing. Without it a mis-scaled field would present as a resampling
        /// verdict rather than a broken probe.
        /// </summary>
        private static void AssertProbeFieldIsLinear(GlyphAtlas atlas)
        {
            Assert.IsTrue(atlas.TryGetEntry(0, ProbeCodepoint, out GlyphAtlasEntry entry),
                "the probe glyph must be in the atlas.");
            Assert.AreEqual(new int2(ProbeCellPx, ProbeCellPx), entry.CellSize,
                "the probe cell must be exactly the authored size — every distance below is in its texels.");

            byte[] page = atlas.PagePixels(entry.Page);
            int row = entry.AtlasOrigin.y + ProbeCellPx / 2;
            int left = entry.AtlasOrigin.x;
            int isoTexel = (ProbeCellPx - ProbeStemPx) / 2; // the texel whose LEFT boundary is the stem edge

            // The two texels straddling the stem's left edge sit half a texel either side of it, so a linear
            // field puts their mean exactly on the iso.
            int below = page[row * atlas.Size.x + left + isoTexel - 1];
            int above = page[row * atlas.Size.x + left + isoTexel];
            Assert.AreEqual(math.round(ProbeIso * 255f), 0.5f * (below + above), 1f,
                $"the field must cross the iso at the stem's edge — the straddling texels read {below}/{above}.");
            Assert.AreEqual(0, page[row * atlas.Size.x + left],
                "the field must have reached its outside clamp by the cell's edge — otherwise the quad's own " +
                "polygon edge, not the field, bounds the ink.");
        }

        // ── render + measure ──────────────────────────────────────────────────────────────────────────

        /// <summary>Minimal metrics over an already-appended atlas — enough to drive the real
        /// <see cref="CodepointTextShaper"/>/<see cref="TextQuadLayout"/> pipeline for one glyph.</summary>
        private sealed class AtlasMetrics : IGlyphMetricsProvider
        {
            private readonly IGlyphAtlasView _atlas;
            public AtlasMetrics(IGlyphAtlasView atlas) => _atlas = atlas;

            public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
            {
                fontId = 0;
                if (_atlas.TryGetEntry(0, codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f;
                return false;
            }
        }

        /// <summary>
        /// Renders the probe glyph shifted by <paramref name="phasePx"/> device px and returns the X centroid
        /// of its ink, weighted by per-pixel darkness against the white background. A centroid over a
        /// symmetric probe is preferred over an ink MASS: the readback is gamma-encoded, and a monotone
        /// transfer curve maps a symmetric profile to a symmetric profile but changes a raw sum.
        /// </summary>
        private static float RenderAndMeasureInkCentroidX(
            GlyphAtlas atlas, GlyphAtlasTexture atlasTexture, float phasePx)
        {
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest
            {
                Text = char.ConvertFromUtf32((int)ProbeCodepoint),
                Metrics = new AtlasMetrics(atlas),
            });

            // text-offset is in EMS and the render below is at scale 1 (textSizePx == OneEm), so dividing the
            // device-px phase by OneEm lands an exact device-px shift. dpr 1 is asserted at the render.
            var options = new TextLayoutOptions
            {
                Anchor = MapRenderer.Core.Text.TextAnchor.Center,
                Offset = new float2(phasePx / TextQuadLayout.OneEm, 0f),
                Justify = TextJustify.Auto,
                MaxWidthEm = TextLayoutOptions.Default.MaxWidthEm,
                LineHeightEm = TextLayoutOptions.Default.LineHeightEm,
            };
            var quads = new List<SymbolQuad>();
            TextLayoutBounds bounds = TextQuadLayout.Layout(run, atlas, options, quads);
            Assert.AreEqual(1, quads.Count, "the probe must lay out exactly one glyph quad.");

            Color32[] px = RenderText(atlasTexture, quads, bounds, out int width);

            double weighted = 0.0, total = 0.0;
            for (int p = 0; p < px.Length; p++)
            {
                double ink = (255.0 - px[p].r) / 255.0; // black ink on white
                if (ink <= 0.004) continue;           // ignore readback noise
                weighted += ink * (p % width);
                total += ink;
            }

            Assert.Greater(total, 1.0, "the glyph must actually have drawn — no ink found in the frame.");
            return (float)(weighted / total);
        }

        /// <summary>
        /// Drives one text symbol through the real SymbolPlacementSystem → Map/Symbol/TextWorld path and
        /// returns the raw RGBA32 readback. The readback is the vertical mirror of on-screen (Unity's
        /// render-to-texture Y-flip), which is irrelevant here — the measurement is horizontal.
        /// </summary>
        private static Color32[] RenderText(
            GlyphAtlasTexture atlasTexture, List<SymbolQuad> quads, TextLayoutBounds bounds, out int width)
        {
            using var bag = new ObjectDisposalBag();
            var camGo = bag.Track(new GameObject("TextResampling_TestCamera"));
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;

            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 },
                zoom: 8.0, heading: 0.0, tilt: 0.0));

            // The sweep converts a logical-px text-offset into an exact DEVICE-px phase, which only holds at
            // dpr 1. Assert it rather than assume it: a fixture default of 2 would halve every phase step and
            // quietly weaken the tooth into a sweep of half a pixel.
            Assert.AreEqual(1.0, mapCamera.DevicePixelRatio, 1e-9,
                "this fixture converts logical px to device px 1:1 — it requires dpr 1.");

            var frame = new SceneFrame
            {
                SceneOriginRender = mapCamera.Projection.Project(
                    new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                Rebase = float3x3.identity,
            };

            // A realistic containing tile keeps the world-anchored bake float32-safe (TileKey=0 is ~2e7 m
            // away) — the same note SymbolIconResamplingTests carries.
            long tileKey = TestTileKeys.PackedContaining(
                new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14);

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, frame.SceneOriginRender, quads, bounds.Min, bounds.Max,
                kind: SymbolKind.Text,
                paint: SymbolPaint.Default, // opaque black text, halo width 0 — no halo run is emitted
                textSizePx: TextQuadLayout.OneEm, // scale 1: one atlas texel draws at one device px
                allowOverlap: true,
                sortKey: 0f,
                featureIndex: 0,
                tileKey: tileKey);

            using var system = new SymbolPlacementSystem(
                mapCamera, new Material(Shader.Find("Map/Symbol/TextWorld")));
            using var snap = new SnapshotRenderer(Size, Size);
            using var plan = new TestSymbolPlan(mapCamera.Projection);
            {
                // The collision verdict is harvested one Tick late — hence the duplicate tick.
                system.Tick(in frame, plan.Build(buffer), atlasTexture, deltaTime: float.PositiveInfinity);
                system.Tick(in frame, plan.Build(buffer), atlasTexture, deltaTime: float.PositiveInfinity);
                Assert.AreEqual(1, system.LastQuadCount, "the single glyph quad must place (not culled).");

                snap.Render(uCam);

                width = snap.Width;
                return (Color32[])snap.Pixels.Pixels.Clone();
            }
        }
    }
}
