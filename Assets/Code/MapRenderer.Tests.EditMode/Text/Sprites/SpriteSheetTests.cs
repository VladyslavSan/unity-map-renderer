// Unity-only, NOT in core-tests.csproj — this test touches UnityEngine.Texture2D, which the fast
// dotnet-test loop (Tools/core-tests) cannot compile/run. Complements SpriteIndexTests (engine-free JSON
// parsing, covered by both runners) by exercising the REAL Texture2D decode + row-flip path (SpriteSheet).

using System;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Unity.Text;

namespace MapRenderer.Tests
{
    /// <summary>
    /// I4 acceptance: <see cref="SpriteSheet"/> decodes the fixture sprite PNG, repacks it with a one-texel
    /// transparent border per sprite, and flips its rows so a top-left-origin sprite-JSON coord
    /// <c>(x,y)</c> — of the <b>repacked</b> index — reads back at <c>Texture2D.GetPixel(x,y)</c>. That is
    /// the SAME contract <see cref="GlyphAtlasTexture"/> establishes for the glyph atlas (see
    /// <see cref="SpriteSheet"/>'s orientation-contract doc). The color pins below are the ultimate check:
    /// if the flip direction is wrong, they read transparent/black instead of the fixture's marker/star/dot
    /// colors.
    /// </summary>
    [TestFixture]
    public class SpriteSheetTests
    {
        // Small tolerance for byte-channel color compares (PNG decode / GetPixels32 round-trip is exact in
        // practice, but a tolerance avoids pinning to bit-exact channel values incidentally).
        private const int ColorTolerance = 5;

        // ---- fixture loader (walk-up; works under Unity batch mode) -------------------------------
        private static string LoadFixturePath(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Assets", "Fixtures", "sprites", fileName);
                    if (File.Exists(candidate))
                        return candidate;
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found (cwd={Directory.GetCurrentDirectory()}," +
                $" base={AppContext.BaseDirectory})");
        }

        private static SpriteSheet LoadFixtureSheet()
        {
            string json = File.ReadAllText(LoadFixturePath("sample-sprite.json"));
            byte[] png = File.ReadAllBytes(LoadFixturePath("sample-sprite.png"));
            var index = SpriteIndex.Parse(json);
            return new SpriteSheet(png, index);
        }

        private static void AssertColorApprox(Color32 actual, byte r, byte g, byte b, byte a, string message)
        {
            Assert.LessOrEqual(Math.Abs(actual.r - r), ColorTolerance, $"{message} (r={actual.r})");
            Assert.LessOrEqual(Math.Abs(actual.g - g), ColorTolerance, $"{message} (g={actual.g})");
            Assert.LessOrEqual(Math.Abs(actual.b - b), ColorTolerance, $"{message} (b={actual.b})");
            Assert.LessOrEqual(Math.Abs(actual.a - a), ColorTolerance, $"{message} (a={actual.a})");
        }

        [Test]
        public void Texture_TopLeftCoordsMatchGetPixel_OrientationPin()
        {
            SpriteSheet sheet = LoadFixtureSheet();
            try
            {
                Texture2D texture = sheet.Texture;
                SpriteIndex index = sheet.View.Index;

                // The repack relocates every sprite, so the colour pins must be read at the sprite's NEW
                // rect. Reading them through the derived index (rather than at hand-written coordinates) is
                // what keeps this an ORIENTATION tooth instead of a packing-layout tooth.
                Assert.IsTrue(index.TryGetSprite("marker", out SpriteEntry marker));
                Assert.IsTrue(index.TryGetSprite("star", out SpriteEntry star));
                Assert.IsTrue(index.TryGetSprite("dot", out SpriteEntry dot));

                // marker (16x16) -- opaque red -- sample well inside the sprite's rect.
                AssertColorApprox(texture.GetPixel(marker.X + 8, marker.Y + 8), 255, 0, 0, 255,
                    "marker sprite must read red 8 texels inside its repacked rect");

                // star (24x24) -- opaque green.
                AssertColorApprox(texture.GetPixel(star.X + 12, star.Y + 12), 0, 255, 0, 255,
                    "star sprite must read green 12 texels inside its repacked rect");

                // dot (8x8) -- opaque blue.
                AssertColorApprox(texture.GetPixel(dot.X + 4, dot.Y + 4), 0, 0, 255, 255,
                    "dot sprite must read blue 4 texels inside its repacked rect");

                // Anti-flip guard: the VERTICAL MIRROR of the dot sample. The dot's cell is only 10 texels
                // tall in a 26-texel sheet, so its mirror lands in the sheet's empty region, which the
                // composer clears to transparent. If the row flip were backwards (or doubled), this
                // coordinate would alias the dot's opaque blue instead.
                int mirroredY = sheet.View.Size.y - 1 - (dot.Y + 4);
                Assert.AreNotEqual(dot.Y + 4, mirroredY, "the mirror must not coincide with the sample itself");
                AssertColorApprox(texture.GetPixel(dot.X + 4, mirroredY), 0, 0, 0, 0,
                    "the vertical mirror of the dot sample must read transparent -- a blue read here means " +
                    "the row flip is backwards");
            }
            finally
            {
                sheet.Dispose();
            }
        }

        [Test]
        public void View_ReportsRepackedSheetSizeAndIndex()
        {
            SpriteSheet sheet = LoadFixtureSheet();
            try
            {
                SpriteAtlasView view = sheet.View;

                // Hand-derived, not copied off a run. Cells (rect + 1 texel of border per side): star 26x26,
                // marker 18x18, dot 10x10. Shelf width starts at max(source 64, widest cell 26) == 64;
                // height-descending they lay on ONE shelf as 26 + 18 + 10 == 54 <= 64, so the packed sheet is
                // 64 wide and one shelf (the tallest cell, 26) tall.
                var expectedSize = new int2(64, 26);
                Assert.AreEqual(expectedSize, view.Size, "repacked sheet size");

                // …and the sheet must actually be bound at the size the planner produced for this index.
                SpritePadPlan plan = SpriteSheetPadder.Plan(
                    SpriteIndex.Parse(File.ReadAllText(LoadFixturePath("sample-sprite.json"))),
                    new int2(64, 64), padding: 1);
                Assert.AreEqual(plan.Size, view.Size, "SpriteSheet must bind the planned sheet size");

                Assert.IsTrue(view.Index.TryGetSprite("marker", out _));
                Assert.IsTrue(view.Index.TryGetSprite("star", out _));
                Assert.IsTrue(view.Index.TryGetSprite("dot", out _));
            }
            finally
            {
                sheet.Dispose();
            }
        }

        /// <summary>
        /// U3 — the border is REAL in the bound texture, not merely promised by the index: every one of the
        /// eight neighbour texels around a sprite's content rect is alpha 0 carrying the RGB of the content
        /// texel it abuts. Alpha 0 alone is not enough — bilinear interpolates RGB and alpha independently,
        /// so a zeroed RGB would ring a black fringe around the icon.
        /// </summary>
        [Test]
        public void EverySprite_IsSurroundedByAOneTexelTransparentBorderCarryingItsOwnRgb()
        {
            SpriteSheet sheet = LoadFixtureSheet();
            try
            {
                Texture2D texture = sheet.Texture;
                foreach (var kv in sheet.View.Index.Entries)
                {
                    SpriteEntry e = kv.Value;
                    Assert.AreEqual(1, e.Padding, $"'{kv.Key}' must report its one-texel border");

                    for (int dy = -1; dy <= e.Height; dy++)
                    {
                        for (int dx = -1; dx <= e.Width; dx++)
                        {
                            if (dx >= 0 && dx < e.Width && dy >= 0 && dy < e.Height) continue;

                            Color border = texture.GetPixel(e.X + dx, e.Y + dy);
                            Color content = texture.GetPixel(
                                e.X + Mathf.Clamp(dx, 0, e.Width - 1), e.Y + Mathf.Clamp(dy, 0, e.Height - 1));

                            Assert.AreEqual(0f, border.a, 1e-3f,
                                $"'{kv.Key}' border texel ({dx},{dy}) must be fully transparent");
                            AssertColorApprox(border, (byte)(content.r * 255f), (byte)(content.g * 255f),
                                (byte)(content.b * 255f), 0,
                                $"'{kv.Key}' border texel ({dx},{dy}) must replicate the adjacent content RGB");
                        }
                    }
                }
            }
            finally
            {
                sheet.Dispose();
            }
        }
    }
}
