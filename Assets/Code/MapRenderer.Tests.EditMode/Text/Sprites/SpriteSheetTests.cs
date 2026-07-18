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
    /// I4 acceptance: <see cref="SpriteSheet"/> decodes the fixture sprite PNG and flips its rows so a
    /// top-left-origin sprite-JSON coord <c>(x,y)</c> reads back at <c>Texture2D.GetPixel(x,y)</c> — the
    /// SAME contract <see cref="GlyphAtlasTexture"/> establishes for the glyph atlas (see
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

                // marker (x0,y0,16x16) -- opaque red -- sample well inside the sprite's rect.
                AssertColorApprox(texture.GetPixel(8, 8), 255, 0, 0, 255,
                    "marker sprite must read red at its top-left-origin (8,8) after the row flip");

                // star (x16,y0,24x24) -- opaque green.
                AssertColorApprox(texture.GetPixel(28, 12), 0, 255, 0, 255,
                    "star sprite must read green at its top-left-origin (28,12) after the row flip");

                // dot (x0,y32,8x8) -- opaque blue.
                AssertColorApprox(texture.GetPixel(4, 36), 0, 0, 255, 255,
                    "dot sprite must read blue at its top-left-origin (4,36) after the row flip");

                // Anti-flip guard: (8,55) sits outside every sprite's rect and must be transparent -- if
                // the flip were backwards (or missing), Unity's un-flipped LoadImage decode would alias
                // this coordinate to the marker's opaque red (the marker lands near the BOTTOM of
                // un-flipped GetPixel space).
                AssertColorApprox(texture.GetPixel(8, 55), 0, 0, 0, 0,
                    "coordinate outside every sprite rect must read transparent -- an opaque/red read here means the row flip is backwards");
            }
            finally
            {
                sheet.Dispose();
            }
        }

        [Test]
        public void View_ReportsSheetSizeAndIndex()
        {
            SpriteSheet sheet = LoadFixtureSheet();
            try
            {
                SpriteAtlasView view = sheet.View;

                Assert.AreEqual(new int2(64, 64), view.Size);
                Assert.IsTrue(view.Index.TryGetSprite("marker", out _));
                Assert.IsTrue(view.Index.TryGetSprite("star", out _));
                Assert.IsTrue(view.Index.TryGetSprite("dot", out _));
            }
            finally
            {
                sheet.Dispose();
            }
        }
    }
}
