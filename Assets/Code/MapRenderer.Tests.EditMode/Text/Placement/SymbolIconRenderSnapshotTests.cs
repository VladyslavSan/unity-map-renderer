// Unity EditMode only — off-screen GPU render of the REAL icon draw path to a PNG artifact. NOT registered
// in core-tests.csproj (needs Camera/RenderTexture/Material/Texture2D/SpriteSheet).
//
// This is the machine-checkable form of the I5b/I6 "on-screen eyeball": it renders four DISTINCT, deliberately
// ASYMMETRIC demo sprites (a committed fixture — an up-triangle, an "F", a down-arrow, a ring) through the REAL
// LabelPlacementSystem.Tick → Map/Symbol/IconWorld shader → row-flipped SpriteSheet texture, reads the framebuffer
// back, and writes Logs/snapshots/symbol-icons.png. Asymmetric shapes make any vertical flip / horizontal
// mirror visible (a symmetric square could not). The test asserts the frame is non-blank and that the four
// icons' saturated colors are all present (each distinct sprite actually sampled); the human-facing check is
// the saved PNG.
//
// READBACK IS VERTICALLY MIRRORED vs ON-SCREEN (Unity's render-to-texture Y-flip; the shipping path's
// backbuffer blit flips Y, a direct camera→RT readback lacks it — see SymbolAtlasOrientationSnapshotTests'
// header). So this un-mirrors the readback before writing the PNG, so the artifact matches on-screen truth.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Text;
using MapRenderer.Core.Text.Placement;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Core.View.Camera;
using MapRenderer.Tests.Visual;
using MapRenderer.Unity.Rendering.Backend;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;

namespace MapRenderer.Tests.Text.Placement
{
    [TestFixture]
    public class SymbolIconRenderSnapshotTests
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
            public bool TryGetAdvance(uint codepoint, out float advance)
            {
                if (_atlas.TryGetEntry(codepoint, out GlyphAtlasEntry e)) { advance = e.Advance; return true; }
                advance = 0f; return false;
            }
        }

        [Test]
        public void DemoIcons_RenderThroughRealIconPath_WritesPng()
        {
            // 1. Real sprite sheet via the I4 path (LoadImage + the row-flip that matches the glyph-atlas
            //    orientation contract) — the committed asymmetric demo fixture.
            SpriteIndex index = SpriteIndex.Parse(LoadFixtureText("demo-icons.json"));
            var sheet = new SpriteSheet(LoadFixtureBytes("demo-icons.png"), index);
            int2 sheetSize = sheet.View.Size;

            // 2. Overhead camera, white background so the saturated icon colors stand out.
            var camGo = new GameObject("IconRender_TestCamera");
            var uCam = camGo.AddComponent<Camera>();
            uCam.targetTexture = new RenderTexture(Size, Size, 0);
            uCam.clearFlags = CameraClearFlags.SolidColor;
            uCam.backgroundColor = Color.white;
            var mapCamera = new MapCamera(uCam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 30.0, Longitude = 30.0, Altitude = 0.0 },
                zoom: 8.0, heading: 0.0, tilt: 0.0));
            var frame = new SceneFrame(
                mapCamera.Projection.Project(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }),
                float3x3.identity);

            // Icons are drawn with vertex color = white so SAMPLE(_MainTex) * color shows the sprite's true
            // RGBA (LabelPaint.Default is BLACK text ink — it would render every icon black).
            var whitePaint = new LabelPaint
            {
                TextColor = new float4(1f, 1f, 1f, 1f), Opacity = 1f,
                HaloColor = default, HaloWidthPx = 0f, HaloBlurPx = 0f,
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

            // Epic A / A1 Risk R1: a realistic containing tile keeps the world-anchored bake float32-safe
            // (TileKey=0 is ~2e7m away — see SymbolAtlasOrientationSnapshotTests' identical note).
            long tileKey = TestTileKeys.PackedContaining(new GeoCoordinate { Latitude = 30.0, Longitude = 30.0 }, zoom: 14);

            var labels = new List<LabelInstance>();
            for (int i = 0; i < placements.Length; i++)
            {
                (string name, double east, double north) = placements[i];
                Assert.IsTrue(index.TryGetSprite(name, out SpriteEntry entry), $"fixture must define sprite '{name}'");
                SymbolQuad quad = IconQuadLayout.Layout(
                    entry, sheetSize, iconSize: 2.0f, MapRenderer.Core.Text.TextAnchor.Center, float2.zero);
                labels.Add(new LabelInstance
                {
                    AnchorRender = frame.SceneOriginRender + new double3(east, 0.0, north),
                    Layout = IconQuadLayout.ToLayoutResult(quad),
                    Kind = LabelKind.Icon,
                    Paint = whitePaint,
                    TextSizePx = TextQuadLayout.OneEm, // scale 1 — matches the real StyledSymbolTileBuilder icon path
                    IconImage = name,                  // the I6 cross-tile identity discriminant
                    AllowOverlap = true,               // render diagnostic — never collision-cull
                    SortKey = 0f,
                    FeatureIndex = i,
                    TileKey = tileKey,
                });
            }

            // Reference: a REAL text glyph 'A' (known upright on-screen — pinned by SymbolAtlasOrientation-
            // SnapshotTests) rendered at CENTER, so the icons' orientation can be compared against a
            // ground-truth glyph in the same frame (same camera, same un-mirror). If 'A' is upright and an
            // icon is not, the icon path has a real flip.
            FontStackGlyphs stack = GlyphPbfDecoder.Decode(LoadGlyphFixture("0-255.pbf.bytes")).Stacks[0];
            var glyphAtlas = new GlyphAtlas();
            glyphAtlas.Append(stack.Glyphs[65u]);
            GlyphAtlasTexture atlasTexture = new GlyphAtlasTexture();
            atlasTexture.Upload(glyphAtlas);
            var shaper = new CodepointTextShaper();
            ShapedRun run = shaper.Shape(new ShapingRequest { Text = "A", Metrics = new AtlasMetrics(glyphAtlas) });
            TextLayoutResult glyphLayout = TextQuadLayout.Layout(run, glyphAtlas, TextLayoutOptions.Default);
            labels.Add(new LabelInstance
            {
                AnchorRender = frame.SceneOriginRender,
                Layout = glyphLayout,
                Paint = LabelPaint.Default, // black 'A' on white — the upright reference
                TextSizePx = 90f,
                AllowOverlap = true,
                SortKey = 0f,
                FeatureIndex = 99,
                TileKey = tileKey,
            });

            // Epic A / A1: point text + icons draw through the world path (D7) — the ONLY draw path since
            // commit 1 retired the screen materials/path.
            var system = new LabelPlacementSystem(
                mapCamera,
                new Material(Shader.Find("Map/Symbol/TextWorld")),
                new Material(Shader.Find("Map/Symbol/IconWorld")));
            // The realistic z14 tileKey above (Risk R1) is finer than this z8 camera's view — disable the
            // orthogonal tile-coverage pre-cull (see SymbolAtlasOrientationSnapshotTests' identical note).
            system.MinTileScreenCoverage = 0.0;
            var snap = new SnapshotRenderer(Size, Size);
            try
            {
                system.Tick(in frame, labels, atlasTexture, deltaTime: float.PositiveInfinity, spriteTexture: sheet.Texture);
                Assert.AreEqual(5, system.LastQuadCount,
                    "all four icon quads + the reference 'A' glyph quad must place (each a 1-quad point candidate; none culled).");

                snap.Render(uCam);
                if (snap.IsAllBlack())
                    Assert.Inconclusive("render is all-black — no GPU context in this batch session (see SnapshotRenderer.IsAllBlack).");

                byte[] raw = snap.RawPixels; // RGBA32, row-major (the raw camera→RT readback).

                // Write the human-facing artifact so it matches the ON-SCREEN orientation. The raw readback is
                // the vertical MIRROR of on-screen (Unity's render-to-texture Y-flip); LoadRawTextureData's
                // bottom-left-origin upload re-flips it, so EncodeToPNG here lands on-screen-upright. (Feeding it
                // an already-un-mirrored buffer would double-flip — the trap this comment guards against.)
                string dir = SnapshotRenderer.GetSnapshotsDir();
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "symbol-icons.png");
                var outTex = new Texture2D(Size, Size, TextureFormat.RGBA32, mipChain: false);
                outTex.LoadRawTextureData(raw);
                outTex.Apply(updateMipmaps: false);
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(outTex));
                Object.DestroyImmediate(outTex);
                TestContext.Out.WriteLine($"wrote icon render snapshot: {path}");

                // Analyze the ON-SCREEN frame (un-mirror the readback) — the SAME frame SymbolAtlasOrientation-
                // SnapshotTests uses to prove text is upright, so the icon orientation is compared like-for-like.
                byte[] px = (byte[])raw.Clone();
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
            finally
            {
                snap.Dispose();
                system.Dispose();
                atlasTexture.Dispose();
                sheet.Dispose();
                Object.DestroyImmediate(camGo);
            }
        }

        /// <summary>Vertically mirrors an RGBA32 row-major buffer in place (row r ↔ row height-1-r).</summary>
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

        /// <summary>Tally pixels whose color is dominantly red / green / blue / orange (each demo sprite's hue),
        /// on the white background. A hue counts when its channel(s) clearly dominate and it is not near-white.</summary>
        private static void CountHues(byte[] rgba, int width, int height,
            out int red, out int green, out int blue, out int orange)
        {
            red = green = blue = orange = 0;
            for (int i = 0; i < width * height; i++)
            {
                int b = i * 4;
                int r = rgba[b], g = rgba[b + 1], bl = rgba[b + 2];
                if (r > 230 && g > 230 && bl > 230) continue; // white background
                if (r > 150 && g < 120 && bl < 120) red++;
                else if (g > 140 && r < 130 && bl < 130) green++;
                else if (bl > 150 && r < 130 && g < 150) blue++;
                else if (r > 180 && g > 110 && g < 200 && bl < 100) orange++;
            }
        }

        /// <summary>Average horizontal extent of the RED (tri-up) sprite's ink in the top third vs the bottom
        /// third of its row span. Red is unique to the up-triangle, so no masking of other sprites is needed.</summary>
        private static void RedTriangleWidths(byte[] rgba, int width, int height, out float topWidth, out float bottomWidth)
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
                    int b = (row * width + col) * 4;
                    if (rgba[b] > 150 && rgba[b + 1] < 120 && rgba[b + 2] < 120) // red (tri-up)
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
}
