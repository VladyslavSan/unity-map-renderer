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
    // Unity EditMode only. A 2-page glyph atlas must sample the Texture2DArray layer BillboardVertex.Page
    // selects: the same 'A' from page 0 and from page 1 must match in ink; always reading layer 0 blanks page 1.

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

            // (2) Forced multi-page: an atlas one 'A' cell in size, filled by a bitmap-less filler, so the
            // real 'A', appended second, overflows onto page 1.
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

            // Same glyph on another layer → near-identical coverage. The 20% tolerance absorbs AA noise; a wrong
            // layer blanks out or draws an unrelated bitmap.
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

    // Unity EditMode only — one REAL glyph ('A') through the REAL SymbolPlacementSystem, shader and uploaded
    // GlyphAtlasTexture, which synthetic-UV tests never touch. It checks (1) horizontal centring and (2) that
    // 'A' is wider at the bottom than at the apex; a flipped atlas UV inverts (2).
    //
    // Non-obvious why: the headless camera→RT readback lacks the on-screen backbuffer blit, so it is the
    // vertical MIRROR of what ships; the test un-mirrors it and never asserts absolute vertical position.
    // Tick() binds a persistent scene MeshRenderer, so the test renders with no manual attach. Do NOT use
    // Graphics.RenderMesh: an immediate-mode submission renders 0 px in headless EditMode.

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
            // Fixes pixelWidth/pixelHeight before the framing math reads them; SnapshotRenderer.Render swaps in
            // its own same-size RenderTexture and restores this one.
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

            // Anchor slightly NORTH of the look-at at the same X, so the glyph renders horizontally centred. An
            // offset of frac·altitude subtends atan(frac) on screen, so 0.02 keeps it well inside the frame.
            double altitude = uCam.transform.position.y;
            double3 anchorRender = frame.SceneOriginRender + new double3(0.0, 0.0, altitude * 0.02);

            var buffer = new SymbolTileBuffer();
            TestSymbolTileBuffer.AddPoint(buffer, anchorRender, quads, bounds.Min, bounds.Max,
                paint: SymbolPaint.Default, // black text; default halo is WHITE == background, so invisible here
                textSizePx: 220f, // large -- reliably legible at the readback resolution
                sortKey: 0f,
                featureIndex: 0,
                // A realistic containing tile keeps the AnchorLocal bake float32-safe; TileKey=0 is ~2e7 m
                // away.
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

                // Tick() already bound the mesh to a persistent scene MeshRenderer, so render straight away;
                // a manual attach would double-blend the SDF ink.
                snap.Render(uCam);

                Color32[] px = snap.Pixels.Pixels; // row-major, TOP-LEFT origin (SnapshotRenderer's doc'd convention)

                // Non-obvious why: the on-screen path blits URP's intermediate RT to the backbuffer, which flips
                // Y, and a direct camera→RT readback lacks that blit, so it is the vertical MIRROR of on-screen.
                // _ProjectionParams.x is -1 in both, so the shader cannot branch. Un-mirroring here makes the
                // check read the on-screen truth.
                FlipRowsVertically(px, Size, Size);

                AnalyzeInkRows(px, Size, Size, out int minRow, out int maxRow, out int minCol, out int maxCol,
                    out float topThirdAvgWidth, out float bottomThirdAvgWidth, out int inkPixelCount);

                Assert.Greater(inkPixelCount, 50,
                    "the rendered label must cover a meaningful number of pixels (not blank / GPU-context-failed).");

                // (1) Horizontal placement, the flip-invariant axis: the look-at's longitude projects to screen
                // centre X, so a gross X projection error would push the ink to an edge.
                float centerCol = (minCol + maxCol) * 0.5f;
                Assert.That(centerCol, Is.EqualTo(Size * 0.5f).Within(Size * 0.15f),
                    $"a label anchored at the look-at's longitude must render horizontally centered " +
                    $"(ink center col {centerCol:F1} of {Size}). (Absolute VERTICAL position is NOT asserted: " +
                    $"the off-screen RT readback carries Unity's render-to-texture Y-flip and on-screen vertical " +
                    $"placement is a verified-live eyeball item — see the un-mirror comment above.)");

                // (2) Orientation, the guard this test exists for: 'A' is wider at the bottom than at the apex. A
                // flipped atlas UV inverts this ratio.
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

    // Unity EditMode only. Four ASYMMETRIC fixture sprites through the REAL icon path to
    // Logs/snapshots/symbol-icons.png, so a flip or mirror is visible; it asserts all four colours render.

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

            // Reference: a REAL text 'A', known upright, at CENTER in the same frame. If 'A' is upright and an
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
            // Every symbol here is Point placement, so the collect's curved-then-points split cannot reorder them
            // (SymbolGatherParityTests covers the mixed case).
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

                // SetPixels32's bottom-left upload re-flips the mirrored RAW readback, so the PNG lands upright.
                // Feeding it an already-un-mirrored buffer would double-flip.
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

                // ORIENTATION GUARD: 'tri-up' is apex-at-TOP, so on screen its ink is NARROWER at the top. A
                // vertically flipped icon path (a wrong SpriteSheet row flip) inverts this.
                RedTriangleWidths(px, Size, Size, out float topWidth, out float bottomWidth);
                Assert.Greater(bottomWidth, topWidth * 1.5f,
                    $"the 'tri-up' icon must render apex-UP (upright, matching text): its bottom third " +
                    $"(width {bottomWidth:F1}px) must be clearly wider than its top third ({topWidth:F1}px). " +
                    $"A reversed ratio means the icon render path is vertically flipped (the I4 SpriteSheet " +
                    $"row-flip regression).");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        // The ICON shader's along-line TANGENT branch: the same NON-SQUARE quad on a HORIZONTAL and a VERTICAL
        // road must follow the road; a pass that ignores tangentOS renders two wide bars.
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

                // The UV rect comes FROM IconQuadLayout, so it tracks the production rect, on a WIDE cell whose
                // footprint makes the orientation legible.
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

                // Precondition: the horizontal case is the un-rotated wide bar. It passes with or without the
                // tangent branch, so the vertical arm is the tooth.
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
        // The SIGN of the two rotations. Non-obvious why: a bounding box is direction-blind (±90° give one tall
        // box, and 180° is its own inverse), so a sign error would misorient every arrow on a DIAGONAL road.
        // At 45° sign is observable, and the ink centroid about the anchor is rotation-equivariant.
        // The frame's sense of "positive" is CALIBRATED, not assumed: the road turns +45° CCW on the map, and a
        // map-aligned icon follows it, so arm B's measured turn IS the buffer's +45° CCW.
        //   A  road 0°,  icon-rotate 0°   — the un-rotated reference, p₀
        //   B  road 45°, icon-rotate 0°   — p₀ turned +45° (the calibration, and the tangent tooth)
        //   C  road 45°, icon-rotate 90°  — B→C isolates icon-rotate, CLOCKWISE per the style spec, so −90°
        // p₀ comes from the committed 'f-glyph': its ink centroid sits up-left of the cell centre (≈134°,
        // |p₀| ≈ 0.19·H). 'arrow-down' is nearly centroid-symmetric, so it carries no direction signal.
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

                // An upright "F" carries its ink up-left of centre. This pins the absolute frame: a mirrored icon
                // path would move the mass and invert every sign below.
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

                // CALIBRATION: the road swings 45° CCW on the map and the icon follows, which fixes the SENSE of
                // the angle measured below.
                AssertRotatedBy(baseline.CentroidFromViewportCentrePx, road45.CentroidFromViewportCentrePx, expectedDeg: 45f,
                    "calibration: the icon must follow its road, so this measures the buffer's rendering of " +
                    "+45° counter-clockwise on the map. Nothing below can be interpreted without it");

                // The two 45° renders differ ONLY in icon-rotate, which the style spec defines as CLOCKWISE,
                // so +90° must show as −90° in the calibrated sense.
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
        // Non-obvious why: the icon shader's map-pitch branch ships (along-line icons resolve to Map pitch and
        // take METRE corners), but every other map-pitch render binds text. Only icons carry a CPU-baked
        // icon-rotate (BillboardMath.BuildWorldQuad, y-DOWN) that the shader then maps into the ground frame:
        //     viewport:  off = Rot(cornerBaked, iconRotate)  then the shader rotates by the projected tangent
        //     map:       off = Rot(cornerBaked, iconRotate)  then the ground frame's x̂ IS the tangent
        // At tilt 0 the two must render alike. Road 45° and icon-rotate 90° make a sign or order error
        // visible, 'f-glyph' carries a direction signal, and PathUpRender = world up forces the GROUND branch.
        // The viewport twin differs only in ShapedSymbol.PitchAlignment. The COUNT test sees a uniform scale
        // error (as k²); the CENTROID test sees a mirror or rotation error.
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
        // SymbolBearing.MapAlignedSign — the map-BEARING sign, rendered at a heading. Non-obvious why: it is +1
        // because a map-aligned symbol turns exactly as the map turns on screen. At heading θ a map bearing β
        // lands at screen angle 90° − β + θ (CCW, y-up), so the map turns +θ CCW, and the staging frame is
        // positive-CCW (AlongLineIcon_IconRotateSign_TurnsTheIconClockwiseOnScreen pins that).
        // BillboardRotationRadians passes MapAlignedSign · θ, so at heading 45° the icon's ink must turn +45°.
        // 45° and the centroid are used for the icon-rotate tooth's reasons. Two probe arms render a
        // VIEWPORT-aligned icon due map-EAST, whose position is pure projection: the probe must swing +45°, so
        // a camera-side sign error reports as itself, and the symbol's turn must match the probe's.
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

                // At heading 0 the due-east probe must sit to the screen RIGHT, at a usable offset, before it
                // serves as a bearing reference.
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

                // The control: with camera pose and billboard construction identical, it attributes the 45°
                // turn above to the ALIGNMENT MODE alone.
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

        /// <summary>Both sign teeth measure about the anchor, which sits at the viewport centre: the camera looks
        /// straight down at <c>lookAt</c>, the path's arc midpoint. The "F"'s ink box is centred in its cell to
        /// 0.5 px of 32, so on the UN-ROTATED arm the ink box centre must land on the anchor.</summary>
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
        /// <paramref name="reference"/>, positive being counter-clockwise as the tangent arm calibrates, and
        /// that the length survives. 15° of slack: the failure modes are 90° apart.</summary>
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
        /// so the row term is negated on the way in. Plain <c>{ get; set; }</c>, not <c>init</c>: the test
        /// assembly has no IsExternalInit polyfill.</summary>
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

        // Renders ONE along-line icon with icon-rotate `iconRotateDeg` on a road at `lineAngleDeg` (0 = east,
        // 90 = north) through the real icon path, and measures its on-screen ink.
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

            // Web-Mercator up (0,1,0), set UNCONDITIONALLY: the viewport path ignores it, and it sends a map arm
            // down the GROUND branch rather than the camera-facing fallback, which coincides at tilt 0.
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

                // Row/col (rows growing DOWN) → a y-up frame about the viewport centre, where the anchor sits
                // (AssertAnchorIsTheViewportCentre). Pixel centres are index + 0.5, so the centre is (Size−1)/2.
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

        // Renders ONE POINT icon under a camera at `headingDeg`, anchored `anchorEastFractionOfAltitude` of the
        // altitude due map-EAST of the look-at, and measures its ink; the point-path sibling of the one above.
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

            // East = +X in render space. The camera orbits the look-at, so at tilt 0 its height is
            // transform.position.y whatever the heading.
            double altitude = uCam.transform.position.y;
            SymbolQuad cell = SignToothCell(sheet.View, halfExtentPx);

            // The cell is a fixed square with no skirt, so its bounds are the quad's raw min/max corners
            // (skirtPx 0 in IconQuadLayout.ToLayoutResult terms).
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

    // Unity EditMode only. Icon RESAMPLING teeth: a phase SWEEP, because a one-frame render cannot see "the
    // render changes when it should not".
    //
    // Non-obvious why: all four corners share one bitwise anchorLocal, so a quad is RIGID on screen and an
    // anchor error only translates it; interior warping is resampling. Nearest-neighbour is exact only at
    // INTEGER magnification (iconSize · dpr / pixelRatio, almost never an integer), and otherwise each texel
    // covers N or N+1 pixels by sub-pixel phase, so panning re-quantises the interior. At exactly 1× it is a
    // pixel-perfect blit, so the defect is invisible where it is easiest to look.

    // ───────────────────────────────────────────────────────────────────────────────────
    // SymbolIconResamplingTests — Unity EditMode only
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SymbolIconResamplingTests
    {
        private const int Size = 256;
        private const int SheetSize = 64;

        /// <summary>
        /// The magnification (device px per source texel) the NON-sweeping teeth render at: NON-INTEGER,
        /// because an integer one hides the defect, and 2.5 separates the filters furthest (see
        /// <see cref="MaxCentroidDeviationPx"/>). The sweeping teeth take it as a <c>[TestCase]</c>;
        /// <see cref="IconSilhouette_TracksSubPixelPhase_ForAFullBleedSprite"/> includes 1.0.
        /// </summary>
        private const float Magnification = 2.5f;

        /// <summary>Phase step of the sweep, device px. The sweep spans one FULL device pixel — a partial
        /// sweep could sit entirely inside one tread of the nearest-neighbour staircase and read smooth.</summary>
        private const float PhaseStepPx = 0.125f;

        /// <summary>
        /// The PRIMARY tooth, in device px: the least the ink is allowed to advance for one
        /// <see cref="PhaseStepPx"/> of quad shift: half the ideal step. Non-obvious why: it is primary because
        /// nearest-neighbour holds the ink still until a texel boundary (a step of 0.000 px), while bilinear
        /// advances ~0.125 px each step, an order of magnitude apart.
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
        /// The silhouette tooth's bounds, device px: every sweep step must fall between a QUARTER and THREE
        /// TIMES the ideal <see cref="PhaseStepPx"/>. A STAIRCASE holds still (0.000 px) and then jumps a whole
        /// pixel (8× the ideal), so it fails both sides.
        ///
        /// <para>Non-obvious why: <see cref="MinCentroidStepPx"/> does not fit here. A full-bleed sprite's ink
        /// is a slab whose only moving parts are its two one-texel edge ramps, and the sRGB readback reads
        /// alpha 0.5 as ink ≈ 0.265. That de-weights mid-ramp pixels, so the steps are uneven (0.062–0.218 px)
        /// even though their mean is exact. A wider border would smooth it.</para>
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
            // A ONE-TEXEL opaque column is the sharpest probe: nearest-neighbour renders it floor(M) or
            // floor(M)+1 px wide by phase, so its centroid can only sit on the pixel grid.
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

                // Ideal: the centroid advances by the phase shift (slope 1), compared against the ramp through
                // the sweep's own mean, which removes the anchor's absolute position.
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

                // Record the sweep always: the PASSING margins show whether the bounds are comfortable or a flake
                // waiting for a different GPU.
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
        // Tooth 2 — the atlas-bleed guard. Non-obvious why: bilinear over an edge-to-edge rect in a PACKED
        // sheet blends the neighbouring sprite. SpriteSheet's repack lays a ONE-TEXEL TRANSPARENT BORDER round
        // every sprite, so an edge tap reaches this sprite's own colour at alpha 0, not the neighbour.
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
        // Tooth U1 — the SILHOUETTE of a sprite whose ink touches all four rect edges (most of the shipped sheet).
        //
        // Non-obvious why: MSAA is off project-wide (RPAsset m_MSAA: 1, QualitySettings antiAliasing: 0), so the
        // silhouette IS the quad's polygon edge and only moves when an edge crosses a pixel centre; no sampler
        // can soften it. A one-texel transparent border makes it a texture ALPHA edge, and the quad grows by
        // that border so the ramp rasterizes; a padded atlas with a nominal quad draws no ramp. At 1.0, where
        // tooth 1 is vacuous, the polygon edge still snaps, so the two defects are distinct.
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
        // Tooth U2 — the ink SIZE must be nominal, and it fails both ways a border can be half-applied:
        //   * quad grown, UV left on the content → 8 texels over the 80 px quad: 50.0 px (GREW).
        //   * atlas padded, quad left nominal    → 10 texels in the 64 px quad: 32.0 px (SHRANK).
        //   * a half-texel UV inset              → 7 texels over 64 px: 45.714 px (W/(W-1) larger).
        // Non-obvious why: the sRGB readback puts "50% darkness" at alpha ≈ 0.79, which moves both edges by about
        // the whole signal, so this measures bar centroids, not a threshold width. Each one-texel bar's profile is
        // symmetric, so its centroid survives any monotone transfer curve, and the separation reads back
        // `texels × devicePxPerTexel`.
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
                // "Grow the quad but leave the UV on the content" makes the icon LARGER: 8 texels over 80 px
                // draw at 10 px each, so the bars sit 5 × 10 == 50 px apart.
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
        /// <see cref="InsetProbeRightBarTexel"/>, inset one row top and bottom. Two bars, not a block: their
        /// symmetric centroids ignore the transfer curve, and their TEXEL separation reads back the texel size.
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
        /// centroids, which a gamma-encoded framebuffer does not move. The bars are split at the midpoint of
        /// the ink's extent, in the transparent gutter between them.
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

            // A logical-px icon-offset is an exact DEVICE-px phase only at dpr 1; at dpr 2 the sweep would
            // quietly shrink to half a pixel.
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
    // TEXT sub-pixel stability teeth, the sibling of SymbolIconResamplingTests: a phase sweep, which a
    // one-frame render cannot replace.
    //
    // Non-obvious why: MSAA is off project-wide, so the SDF coverage ramp is text's only AA. A linear ramp of
    // exactly ONE device pixel is the unique width whose integer shifts partition unity, so a stroke's ink is
    // phase-invariant. A narrower ramp freezes and then jumps, so glyphs morph as the map pans.

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
        /// identical. The fragment reads distance as <c>(sample - iso) * screenPxRange</c>, so the matching
        /// slope makes the edge analytic, and the symmetric stem keeps the centroid off the transfer curve.
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

            // A logical-px text-offset is an exact DEVICE-px phase only at dpr 1; at dpr 2 the sweep would
            // quietly shrink to half a pixel.
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
