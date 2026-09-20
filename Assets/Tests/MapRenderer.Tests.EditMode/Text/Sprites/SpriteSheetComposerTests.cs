// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Tests.Text.Sprites
{
    /// <summary>
    /// Teeth for the padded repack's PIXEL half (<see cref="SpriteSheetComposer"/>). Two properties carry
    /// the whole fix: a sprite's content must survive the move <b>byte-for-byte</b> (a repack that resampled
    /// would be a silent quality regression nobody would trace back here), and the manufactured border must
    /// be <b>alpha 0 with the content's own RGB</b> — the rule that turns the silhouette into a ramp bilinear
    /// can antialias WITHOUT ringing a dark halo around every icon.
    ///
    /// <para><c>NoDarkFringe</c> is the direct tooth for that second rule: a border written with a plain
    /// <c>Array.Clear</c> (i.e. <c>(0,0,0,0)</c>) satisfies "alpha 0" and still fails, because bilinear
    /// interpolates RGB and alpha independently and would drag black into every edge pixel.</para>
    /// </summary>
    [TestFixture]
    public class SpriteSheetComposerTests
    {
        private const int Padding = 1;

        // ── C9 — content bytes survive verbatim ───────────────────────────────────────────────────────

        [Test]
        public void EverySpritesContent_IsCopiedByteForByte()
        {
            var sourceSize = new int2(64, 64);
            SpriteIndex index = SpriteIndex.Parse(
                "{\"marker\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                "\"star\":{\"x\":16,\"y\":0,\"width\":24,\"height\":24,\"pixelRatio\":2}," +
                "\"dot\":{\"x\":0,\"y\":32,\"width\":8,\"height\":8,\"pixelRatio\":1}}");

            byte[] src = NoiseSheet(sourceSize);
            SpritePadPlan plan = SpriteSheetPadder.Plan(index, sourceSize, Padding);
            byte[] dst = Compose(src, sourceSize, plan);

            foreach (var kv in index.Entries)
            {
                Assert.IsTrue(plan.Index.TryGetSprite(kv.Key, out SpriteEntry moved));
                for (int row = 0; row < kv.Value.Height; row++)
                {
                    for (int column = 0; column < kv.Value.Width; column++)
                    {
                        (byte r, byte g, byte b, byte a) expected =
                            Texel(src, sourceSize.x, kv.Value.X + column, kv.Value.Y + row);
                        (byte r, byte g, byte b, byte a) actual =
                            Texel(dst, plan.Size.x, moved.X + column, moved.Y + row);
                        Assert.AreEqual(expected, actual,
                            $"'{kv.Key}' content texel ({column},{row}) changed in the repack");
                    }
                }
            }
        }

        // ── C10 — border alpha is 0 on all eight bands ────────────────────────────────────────────────

        [Test]
        public void TheBorderAroundEverySprite_IsFullyTransparent()
        {
            var sourceSize = new int2(64, 64);
            SpriteIndex index = SpriteIndex.Parse(
                "{\"a\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                "\"b\":{\"x\":16,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                "\"c\":{\"x\":32,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}}");

            byte[] src = OpaqueSheet(sourceSize, 200, 100, 50); // fully opaque everywhere: full-bleed sprites
            SpritePadPlan plan = SpriteSheetPadder.Plan(index, sourceSize, Padding);
            byte[] dst = Compose(src, sourceSize, plan);

            foreach (var kv in plan.Index.Entries)
            {
                SpriteEntry e = kv.Value;
                for (int dy = -Padding; dy < e.Height + Padding; dy++)
                {
                    for (int dx = -Padding; dx < e.Width + Padding; dx++)
                    {
                        bool inside = dx >= 0 && dx < e.Width && dy >= 0 && dy < e.Height;
                        (byte r, byte g, byte b, byte a) texel = Texel(dst, plan.Size.x, e.X + dx, e.Y + dy);
                        if (inside)
                            Assert.AreEqual(255, texel.a, $"'{kv.Key}' content ({dx},{dy}) must stay opaque");
                        else
                            Assert.AreEqual(0, texel.a,
                                $"'{kv.Key}' border ({dx},{dy}) must be fully transparent — it IS the ramp");
                    }
                }
            }
        }

        // ── C11 — no dark fringe ──────────────────────────────────────────────────────────────────────

        [Test]
        public void TheBorderCarriesTheContentsRgb_NotBlack()
        {
            var sourceSize = new int2(32, 32);
            SpriteIndex index = SpriteIndex.Parse(
                "{\"red\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}}");

            byte[] src = OpaqueSheet(sourceSize, 255, 0, 0);
            SpritePadPlan plan = SpriteSheetPadder.Plan(index, sourceSize, Padding);
            byte[] dst = Compose(src, sourceSize, plan);

            Assert.IsTrue(plan.Index.TryGetSprite("red", out SpriteEntry e));
            for (int dy = -Padding; dy < e.Height + Padding; dy++)
            {
                for (int dx = -Padding; dx < e.Width + Padding; dx++)
                {
                    if (dx >= 0 && dx < e.Width && dy >= 0 && dy < e.Height) continue;
                    Assert.AreEqual(((byte)255, (byte)0, (byte)0, (byte)0),
                        Texel(dst, plan.Size.x, e.X + dx, e.Y + dy),
                        $"border texel ({dx},{dy}) must be (255,0,0,0). A plain cleared border reads " +
                        $"(0,0,0,0) — same alpha, but bilinear interpolates RGB independently, so it draws " +
                        $"a black fringe all the way round the icon.");
                }
            }
        }

        // ── C12 — corner replication ──────────────────────────────────────────────────────────────────

        [Test]
        public void EachBorderCorner_ReplicatesTheDiagonalContentCorner()
        {
            var sourceSize = new int2(32, 32);
            SpriteIndex index = SpriteIndex.Parse(
                "{\"quad\":{\"x\":0,\"y\":0,\"width\":8,\"height\":8,\"pixelRatio\":1}}");

            // Four distinct hues at the content's four corners — so a corner that replicated the WRONG
            // neighbour (or a fixed one) reads a different hue rather than a coincidentally equal one.
            byte[] src = OpaqueSheet(sourceSize, 10, 10, 10);
            WriteTexel(src, sourceSize.x, 0, 0, 255, 0, 0);
            WriteTexel(src, sourceSize.x, 7, 0, 0, 255, 0);
            WriteTexel(src, sourceSize.x, 0, 7, 0, 0, 255);
            WriteTexel(src, sourceSize.x, 7, 7, 255, 255, 0);

            SpritePadPlan plan = SpriteSheetPadder.Plan(index, sourceSize, Padding);
            byte[] dst = Compose(src, sourceSize, plan);

            Assert.IsTrue(plan.Index.TryGetSprite("quad", out SpriteEntry e));
            AssertTexel(dst, plan.Size.x, e.X - 1, e.Y - 1, 255, 0, 0, 0, "top-left border corner");
            AssertTexel(dst, plan.Size.x, e.X + e.Width, e.Y - 1, 0, 255, 0, 0, "top-right border corner");
            AssertTexel(dst, plan.Size.x, e.X - 1, e.Y + e.Height, 0, 0, 255, 0, "bottom-left border corner");
            AssertTexel(dst, plan.Size.x, e.X + e.Width, e.Y + e.Height, 255, 255, 0, 0, "bottom-right border corner");

            // …and the EDGE bands replicate the adjacent edge pixel, not the corner.
            AssertTexel(dst, plan.Size.x, e.X - 1, e.Y, 255, 0, 0, 0, "left band beside the red corner");
            AssertTexel(dst, plan.Size.x, e.X + 1, e.Y - 1, 10, 10, 10, 0, "top band above an interior pixel");
        }

        // ── everything not covered stays transparent black ────────────────────────────────────────────

        [Test]
        public void SheetAreaOutsideEveryCell_IsClearedToTransparentBlack()
        {
            var sourceSize = new int2(32, 32);
            SpriteIndex index = SpriteIndex.Parse(
                "{\"only\":{\"x\":0,\"y\":0,\"width\":8,\"height\":8,\"pixelRatio\":1}}");

            byte[] src = OpaqueSheet(sourceSize, 255, 255, 255);
            SpritePadPlan plan = SpriteSheetPadder.Plan(index, sourceSize, Padding);
            byte[] dst = Compose(src, sourceSize, plan);

            Assert.IsTrue(plan.Index.TryGetSprite("only", out SpriteEntry e));
            int touched = 0;
            for (int y = 0; y < plan.Size.y; y++)
            {
                for (int x = 0; x < plan.Size.x; x++)
                {
                    bool inCell = x >= e.X - Padding && x < e.X + e.Width + Padding
                                  && y >= e.Y - Padding && y < e.Y + e.Height + Padding;
                    if (inCell) { touched++; continue; }
                    Assert.AreEqual(((byte)0, (byte)0, (byte)0, (byte)0), Texel(dst, plan.Size.x, x, y),
                        $"({x},{y}) is outside every cell and must be untouched");
                }
            }

            Assert.AreEqual((8 + 2 * Padding) * (8 + 2 * Padding), touched, "exactly one padded cell is written");
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────────────

        private static byte[] Compose(byte[] src, int2 sourceSize, SpritePadPlan plan)
        {
            var dst = new byte[plan.Size.x * plan.Size.y * 4];
            SpriteSheetComposer.Compose(src, sourceSize.x, sourceSize.y, plan, dst);
            return dst;
        }

        /// <summary>A sheet whose every texel is distinct, so a content copy that shifted by one row/column
        /// (or resampled) cannot read back equal by luck.</summary>
        private static byte[] NoiseSheet(int2 size)
        {
            var pixels = new byte[size.x * size.y * 4];
            for (int y = 0; y < size.y; y++)
            {
                for (int x = 0; x < size.x; x++)
                {
                    int i = (y * size.x + x) * 4;
                    pixels[i] = (byte)(x * 7 + 13);
                    pixels[i + 1] = (byte)(y * 11 + 29);
                    pixels[i + 2] = (byte)(x * y + 3);
                    pixels[i + 3] = (byte)(255 - (x + y) % 256);
                }
            }
            return pixels;
        }

        private static byte[] OpaqueSheet(int2 size, byte r, byte g, byte b)
        {
            var pixels = new byte[size.x * size.y * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = 255;
            }
            return pixels;
        }

        private static void WriteTexel(byte[] pixels, int width, int x, int y, byte r, byte g, byte b)
        {
            int i = (y * width + x) * 4;
            pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = 255;
        }

        private static (byte r, byte g, byte b, byte a) Texel(byte[] pixels, int width, int x, int y)
        {
            int i = (y * width + x) * 4;
            return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
        }

        private static void AssertTexel(
            byte[] pixels, int width, int x, int y, byte r, byte g, byte b, byte a, string what)
            => Assert.AreEqual((r, g, b, a), Texel(pixels, width, x, y), $"{what} at ({x},{y})");
    }
}
