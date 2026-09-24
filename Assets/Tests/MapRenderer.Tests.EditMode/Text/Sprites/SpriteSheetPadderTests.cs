// Text/Sprites/SpriteSheetPadderTests.cs — sprite-index JSON parsing and the padded repack's pixel (composer) and rect (padder) halves (fast lane: engine-free, compiled by Tools/core-tests too).
//
// Index parsing first, then the two repack halves (pixel, rect).
//
// Contents:
//   SpriteIndexTests          — a MapLibre sprite JSON index parses into name-keyed SpriteEntry values; malformed/wrong-shaped input never throws (forward-compat posture, mirrors StyleParserTests).
//   SpriteSheetComposerTests  — Teeth for the padded repack's PIXEL half (SpriteSheetComposer).
//   SpriteSheetPadderTests    — Teeth for the padded repack's RECT half (SpriteSheetPadder): every sprite ends up with its own one-texel border that no neighbour's content can reach into, the plan is byte-for-byte reproducible regardless of dictionary insertion order, aliased names stay…

using System;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Text.Sprites;
using Unity.Mathematics;
using System.Collections.Generic;
using System.Text;


namespace MapRenderer.Tests.Text.Sprites
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // SpriteIndexTests — a MapLibre sprite JSON index parses into name-keyed entries
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A MapLibre sprite JSON index parses into name-keyed <see cref="SpriteEntry"/>
    /// values; malformed/wrong-shaped input never throws (forward-compat posture, mirrors
    /// StyleParserTests).
    /// </summary>
    [TestFixture]
    public class SpriteIndexTests
    {
        // ---- fixture loader (walk-up; works under Unity batch mode AND dotnet test) ---------------
        private static string LoadFixtureText(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Assets", "Fixtures", fileName);
                    if (File.Exists(candidate))
                        return File.ReadAllText(candidate);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found (cwd={Directory.GetCurrentDirectory()}," +
                $" base={AppContext.BaseDirectory})");
        }

        [Test]
        public void KnownNames_ParseExactRectAndPixelRatio()
        {
            var index = SpriteIndex.Parse(LoadFixtureText("sprites/sample-sprite.json"));

            Assert.IsTrue(index.TryGetSprite("star", out var star));
            Assert.AreEqual(16, star.X);
            Assert.AreEqual(0, star.Y);
            Assert.AreEqual(24, star.Width);
            Assert.AreEqual(24, star.Height);
            Assert.AreEqual(2f, star.PixelRatio);
            Assert.IsFalse(star.Sdf);

            Assert.IsTrue(index.TryGetSprite("marker", out var marker));
            Assert.AreEqual(0, marker.X);
            Assert.AreEqual(0, marker.Y);
            Assert.AreEqual(16, marker.Width);
            Assert.AreEqual(16, marker.Height);
            Assert.AreEqual(1f, marker.PixelRatio);

            Assert.IsTrue(index.TryGetSprite("dot", out var dot));
            Assert.AreEqual(0, dot.X);
            Assert.AreEqual(32, dot.Y);
            Assert.AreEqual(8, dot.Width);
            Assert.AreEqual(8, dot.Height);
        }

        [Test]
        public void MemberNotObject_Skipped()
        {
            var index = SpriteIndex.Parse("{\"foo\":42}");

            Assert.IsFalse(index.TryGetSprite("foo", out _));
            Assert.AreEqual(0, index.Count);
        }

        [Test]
        public void UnknownName_NotFound()
        {
            var index = SpriteIndex.Parse(LoadFixtureText("sprites/sample-sprite.json"));

            Assert.IsFalse(index.TryGetSprite("nope", out _));
        }

        [Test]
        public void Malformed_DoesNotThrow_EmptyIndex()
        {
            SpriteIndex index = null;
            Assert.DoesNotThrow(() => index = SpriteIndex.Parse("{ not json"));
            Assert.AreEqual(0, index.Count);
        }

        [Test]
        public void RootNotObject_EmptyIndex()
        {
            Assert.AreEqual(0, SpriteIndex.Parse("[]").Count);
            Assert.AreEqual(0, SpriteIndex.Parse("42").Count);
        }

        [Test]
        public void ParsedIndex_ReportsZeroPadding_AndEnumeratesEveryEntry()
        {
            // A published sheet reserves no inter-sprite padding, so a raw Parse must never claim any: the
            // whole padded-repack contract hangs on `Padding` meaning "border that ACTUALLY exists here".
            var index = SpriteIndex.Parse(LoadFixtureText("sprites/sample-sprite.json"));

            Assert.AreEqual(index.Count, index.Entries.Count, "Entries must expose the whole index");
            foreach (var kv in index.Entries)
                Assert.AreEqual(0, kv.Value.Padding, $"'{kv.Key}': a parsed sheet has no border");
        }

        [Test]
        public void Sdf_And_DefaultPixelRatio_ReadThrough()
        {
            var index = SpriteIndex.Parse("{\"a\":{\"x\":1,\"y\":2,\"width\":3,\"height\":4,\"sdf\":true}}");

            Assert.IsTrue(index.TryGetSprite("a", out var a));
            Assert.IsTrue(a.Sdf);
            Assert.AreEqual(1f, a.PixelRatio);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SpriteSheetComposerTests — the padded repack's pixel half
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Teeth for the padded repack's PIXEL half (<see cref="SpriteSheetComposer"/>): a sprite's content
    /// survives the move <b>byte-for-byte</b>, and the border is <b>alpha 0 with the content's own RGB</b>.
    /// <c>NoDarkFringe</c> pins the second rule: bilinear interpolates RGB and alpha independently, so a
    /// <c>(0,0,0,0)</c> border drags black into every edge pixel.
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // SpriteSheetPadderTests — the padded repack's rect half
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Teeth for the padded repack's RECT half (<see cref="SpriteSheetPadder"/>): every sprite ends up with
    /// its own one-texel border that no neighbour's content can reach into, the plan is byte-for-byte
    /// reproducible regardless of dictionary insertion order, aliased names stay aliased, and a malformed
    /// sheet's degenerate entries survive untouched. Two content rects one texel apart would SHARE a border
    /// texel, which the separation tooth rules out.
    /// </summary>
    [TestFixture]
    public class SpriteSheetPadderTests
    {
        private const int Padding = 1;

        // ── C4 — separation ───────────────────────────────────────────────────────────────────────────

        [Test]
        public void EveryPlacedRect_IsSeparatedByAtLeastTwoTexels_AndKeptOffTheSheetEdge()
        {
            // 264 sprites — the shipped style's sheet count, so the packer is exercised at its real scale.
            var sourceSize = new int2(1024, 1024);
            SpriteIndex index = SpriteIndex.Parse(SyntheticSheetJson(264));
            Assert.AreEqual(264, index.Count, "precondition: the synthetic index must hold all 264 sprites");

            SpritePadPlan plan = SpriteSheetPadder.Plan(index, sourceSize, Padding);

            var placed = new List<SpriteEntry>();
            foreach (var kv in plan.Index.Entries)
            {
                Assert.AreEqual(Padding, kv.Value.Padding, $"'{kv.Key}' must be padded");
                placed.Add(kv.Value);
            }

            foreach (SpriteEntry e in placed)
            {
                Assert.GreaterOrEqual(e.X, Padding, "content must keep the border clear of the left edge");
                Assert.GreaterOrEqual(e.Y, Padding, "content must keep the border clear of the top edge");
                Assert.LessOrEqual(e.X + e.Width, plan.Size.x - Padding, "…and of the right edge");
                Assert.LessOrEqual(e.Y + e.Height, plan.Size.y - Padding, "…and of the bottom edge");
            }

            // Distinct DESTINATIONS only: aliased names legitimately share one rect (C6 pins that).
            var distinct = new List<SpriteEntry>();
            foreach (SpriteEntry e in placed)
            {
                bool duplicate = false;
                foreach (SpriteEntry d in distinct)
                    if (d.X == e.X && d.Y == e.Y && d.Width == e.Width && d.Height == e.Height) { duplicate = true; break; }
                if (!duplicate) distinct.Add(e);
            }

            for (int i = 0; i < distinct.Count; i++)
            {
                for (int j = i + 1; j < distinct.Count; j++)
                {
                    int gap = ChebyshevGap(distinct[i], distinct[j]);
                    Assert.GreaterOrEqual(gap, 2 * Padding,
                        $"content rects ({distinct[i].X},{distinct[i].Y},{distinct[i].Width},{distinct[i].Height}) and " +
                        $"({distinct[j].X},{distinct[j].Y},{distinct[j].Width},{distinct[j].Height}) are only {gap} " +
                        $"texels apart — each sprite needs {Padding} texel(s) of border it does NOT share.");
                }
            }
        }

        // ── C5 — determinism ──────────────────────────────────────────────────────────────────────────

        [Test]
        public void Plan_IsDeterministic_AcrossRepeatedCallsAndAcrossDictionaryInsertionOrder()
        {
            var sourceSize = new int2(1024, 1024);
            string forward = SyntheticSheetJson(64);
            string reversed = SyntheticSheetJson(64, reverseMemberOrder: true);

            SpritePadPlan a = SpriteSheetPadder.Plan(SpriteIndex.Parse(forward), sourceSize, Padding);
            SpritePadPlan b = SpriteSheetPadder.Plan(SpriteIndex.Parse(forward), sourceSize, Padding);
            // Same sprites, opposite JSON member order ⇒ opposite Dictionary insertion order. A plan that
            // walked the dictionary and packed in enumeration order would diverge here.
            SpritePadPlan c = SpriteSheetPadder.Plan(SpriteIndex.Parse(reversed), sourceSize, Padding);

            AssertPlansIdentical(a, b, "repeated Plan() over the same parsed index");
            AssertPlansIdentical(a, c, "Plan() over the same sprites parsed in the opposite member order");
        }

        // ── C6 — aliases stay aliased ─────────────────────────────────────────────────────────────────

        [Test]
        public void TwoNamesOnOneSourceRect_ShareOneBlitAndOneDestination()
        {
            SpriteIndex index = SpriteIndex.Parse(
                "{\"alpha\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                "\"beta\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                "\"gamma\":{\"x\":16,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}}");

            SpritePadPlan plan = SpriteSheetPadder.Plan(index, new int2(64, 64), Padding);

            Assert.AreEqual(2, plan.Blits.Count, "two distinct source rects ⇒ two blits, not three");

            Assert.IsTrue(plan.Index.TryGetSprite("alpha", out SpriteEntry alpha));
            Assert.IsTrue(plan.Index.TryGetSprite("beta", out SpriteEntry beta));
            Assert.IsTrue(plan.Index.TryGetSprite("gamma", out SpriteEntry gamma));

            Assert.AreEqual(alpha.X, beta.X, "aliases must resolve to the SAME destination");
            Assert.AreEqual(alpha.Y, beta.Y, "aliases must resolve to the SAME destination");
            Assert.IsTrue(alpha.X != gamma.X || alpha.Y != gamma.Y, "distinct source rects must not collide");
        }

        // ── C7 — coverage + field preservation ────────────────────────────────────────────────────────

        [Test]
        public void EverySourceName_SurvivesWithItsSizeRatioAndSdfIntact()
        {
            SpriteIndex index = SpriteIndex.Parse(
                "{\"marker\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                "\"star\":{\"x\":16,\"y\":0,\"width\":24,\"height\":24,\"pixelRatio\":2}," +
                "\"glyph\":{\"x\":0,\"y\":32,\"width\":8,\"height\":8,\"pixelRatio\":1,\"sdf\":true}}");

            SpritePadPlan plan = SpriteSheetPadder.Plan(index, new int2(64, 64), Padding);

            Assert.AreEqual(index.Count, plan.Index.Count, "no sprite may be dropped by the repack");
            foreach (var kv in index.Entries)
            {
                Assert.IsTrue(plan.Index.TryGetSprite(kv.Key, out SpriteEntry derived), $"'{kv.Key}' missing");
                Assert.AreEqual(kv.Value.Width, derived.Width, $"'{kv.Key}' width");
                Assert.AreEqual(kv.Value.Height, derived.Height, $"'{kv.Key}' height");
                Assert.AreEqual(kv.Value.PixelRatio, derived.PixelRatio, $"'{kv.Key}' pixelRatio");
                Assert.AreEqual(kv.Value.Sdf, derived.Sdf, $"'{kv.Key}' sdf");
                Assert.AreEqual(Padding, derived.Padding, $"'{kv.Key}' padding");
            }

            // Every blit must carry a real content rect that lands inside the planned sheet.
            foreach (SpriteBlit blit in plan.Blits)
            {
                Assert.Greater(blit.Width, 0);
                Assert.Greater(blit.Height, 0);
                Assert.LessOrEqual(blit.DstX + blit.Width, plan.Size.x);
                Assert.LessOrEqual(blit.DstY + blit.Height, plan.Size.y);
            }
        }

        // ── C8 — degenerates pass through ─────────────────────────────────────────────────────────────

        [Test]
        public void DegenerateAndOutOfBoundsSprites_PassThroughUnpaddedAndUnmoved()
        {
            SpriteIndex index = SpriteIndex.Parse(
                "{\"ok\":{\"x\":0,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}," +
                "\"zeroWidth\":{\"x\":0,\"y\":0,\"width\":0,\"height\":8,\"pixelRatio\":1}," +
                "\"offSheet\":{\"x\":60,\"y\":0,\"width\":16,\"height\":16,\"pixelRatio\":1}}");

            SpritePadPlan plan = SpriteSheetPadder.Plan(index, new int2(64, 64), Padding);

            Assert.IsTrue(plan.Index.TryGetSprite("zeroWidth", out SpriteEntry zero));
            Assert.AreEqual(0, zero.Width);
            Assert.AreEqual(0, zero.Padding, "a degenerate sprite has no border — nothing was copied for it");
            Assert.AreEqual(0, zero.X);
            Assert.AreEqual(0, zero.Y);

            Assert.IsTrue(plan.Index.TryGetSprite("offSheet", out SpriteEntry off));
            Assert.AreEqual(60, off.X, "an out-of-bounds rect is left exactly where the JSON put it");
            Assert.AreEqual(0, off.Padding);

            Assert.AreEqual(1, plan.Blits.Count, "only the one packable sprite is copied");
        }

        [Test]
        public void ASourceRectThatOVERFLOWS_PassesThroughUnpadded_AndDoesNotPoisonTheValidSprites()
        {
            // Non-obvious why: this malformed rect parses and `X + Width` wraps, so a naive bound lets its blit
            // throw out of the SpriteSheet constructor, and one bad JSON line removes EVERY icon on the map.
            SpriteIndex index = SpriteIndex.Parse(
                "{\"ok\":{\"x\":0,\"y\":0,\"width\":4,\"height\":4,\"pixelRatio\":1}," +
                "\"overflowX\":{\"x\":2147483647,\"y\":0,\"width\":1,\"height\":1,\"pixelRatio\":1}," +
                "\"overflowY\":{\"x\":0,\"y\":2147483647,\"width\":1,\"height\":1,\"pixelRatio\":1}}");
            Assert.AreEqual(int.MaxValue, index.Entries["overflowX"].X,
                "precondition: the malformed X must survive the parse — otherwise this tooth is vacuous");

            var sourceSize = new int2(8, 8);
            SpritePadPlan plan = SpriteSheetPadder.Plan(index, sourceSize, Padding);

            // Treated like every other out-of-bounds entry: left exactly where the JSON put it, unpadded.
            Assert.IsTrue(plan.Index.TryGetSprite("overflowX", out SpriteEntry overflowX));
            Assert.AreEqual(int.MaxValue, overflowX.X, "an overflowing rect is left exactly as authored");
            Assert.AreEqual(0, overflowX.Padding, "no border was laid down for it, so none may be claimed");
            Assert.IsTrue(plan.Index.TryGetSprite("overflowY", out SpriteEntry overflowY));
            Assert.AreEqual(0, overflowY.Padding, "…on the Y axis too");

            // …and it must not cost the VALID sprite its border.
            Assert.IsTrue(plan.Index.TryGetSprite("ok", out SpriteEntry ok));
            Assert.AreEqual(Padding, ok.Padding, "the one packable sprite must still be padded");
            Assert.AreEqual(1, plan.Blits.Count, "only the packable sprite is copied");

            // The decisive half: composition must actually run. Before the fix this throws
            // ArgumentOutOfRangeException("srcOffset ('-4') must be a non-negative value").
            var src = new byte[sourceSize.x * sourceSize.y * 4];
            var dst = new byte[plan.Size.x * plan.Size.y * 4];
            Assert.DoesNotThrow(() => SpriteSheetComposer.Compose(src, sourceSize.x, sourceSize.y, plan, dst),
                "a malformed sprite rect must never abort composition — that would strand the sheet with no " +
                "texture and no index, i.e. no icons at all.");
        }

        // ── the fallback: a sheet that cannot fit renders as it does today, not at all ─────────────────

        [Test]
        public void WhenTheCellsCannotFit_PlanFallsBackToTheSourceSheetUnchanged()
        {
            // 8192 + 2*padding exceeds the 8192 cap in BOTH dimensions, so no width in the doubling search
            // can hold it.
            var sourceSize = new int2(SpriteSheetPadder.MaxSheetDimension, SpriteSheetPadder.MaxSheetDimension);
            SpriteIndex index = SpriteIndex.Parse(
                $"{{\"huge\":{{\"x\":0,\"y\":0,\"width\":{sourceSize.x},\"height\":{sourceSize.y},\"pixelRatio\":1}}}}");

            SpritePadPlan plan = SpriteSheetPadder.Plan(index, sourceSize, Padding);

            Assert.AreEqual(sourceSize, plan.Size, "the fallback keeps the source sheet's dimensions");
            Assert.IsTrue(plan.Index.TryGetSprite("huge", out SpriteEntry huge));
            Assert.AreEqual(0, huge.X);
            Assert.AreEqual(0, huge.Y);
            Assert.AreEqual(0, huge.Padding, "no border was laid down, so none may be claimed");
            Assert.AreEqual(1, plan.Blits.Count);
            Assert.AreEqual(sourceSize.x, plan.Blits[0].Width, "the fallback blit copies the whole sheet");
            Assert.AreEqual(sourceSize.y, plan.Blits[0].Height);
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Chebyshev (L∞) gap between two rects: the number of texels separating them along whichever axis
        /// separates them most. Negative when they overlap on both axes.
        /// </summary>
        private static int ChebyshevGap(in SpriteEntry a, in SpriteEntry b)
        {
            int gapX = math.max(a.X - (b.X + b.Width), b.X - (a.X + a.Width));
            int gapY = math.max(a.Y - (b.Y + b.Height), b.Y - (a.Y + a.Height));
            return math.max(gapX, gapY);
        }

        /// <summary>
        /// A sheet of <paramref name="count"/> sprites of assorted sizes and pixel ratios, laid out on a
        /// 48-texel grid inside 1024×1024 so every rect is in bounds. <paramref name="reverseMemberOrder"/>
        /// emits the SAME sprites in the opposite JSON order, which is the only lever a test has on
        /// <c>SpriteIndex</c>'s internal dictionary insertion order.
        /// </summary>
        private static string SyntheticSheetJson(int count, bool reverseMemberOrder = false)
        {
            const int columns = 20;
            const int stride = 48;
            var members = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                int column = i % columns;
                int row = i / columns;
                int width = 8 + i % 17;   // co-prime-ish strides so sizes (and therefore shelves) vary
                int height = 8 + i % 13;
                int pixelRatio = i % 3 == 0 ? 2 : 1;
                members.Add(
                    $"\"sprite_{i:D3}\":{{\"x\":{column * stride},\"y\":{row * stride},\"width\":{width}," +
                    $"\"height\":{height},\"pixelRatio\":{pixelRatio}}}");
            }

            if (reverseMemberOrder) members.Reverse();

            var json = new StringBuilder("{");
            for (int i = 0; i < members.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(members[i]);
            }
            return json.Append('}').ToString();
        }

        private static void AssertPlansIdentical(SpritePadPlan expected, SpritePadPlan actual, string what)
        {
            Assert.AreEqual(expected.Size, actual.Size, $"{what}: sheet size");
            Assert.AreEqual(expected.Blits.Count, actual.Blits.Count, $"{what}: blit count");
            for (int i = 0; i < expected.Blits.Count; i++)
            {
                SpriteBlit e = expected.Blits[i], a = actual.Blits[i];
                Assert.AreEqual(e.SrcX, a.SrcX, $"{what}: blit {i} SrcX");
                Assert.AreEqual(e.SrcY, a.SrcY, $"{what}: blit {i} SrcY");
                Assert.AreEqual(e.DstX, a.DstX, $"{what}: blit {i} DstX");
                Assert.AreEqual(e.DstY, a.DstY, $"{what}: blit {i} DstY");
                Assert.AreEqual(e.Width, a.Width, $"{what}: blit {i} Width");
                Assert.AreEqual(e.Height, a.Height, $"{what}: blit {i} Height");
            }

            Assert.AreEqual(expected.Index.Count, actual.Index.Count, $"{what}: index count");
            foreach (var kv in expected.Index.Entries)
            {
                Assert.IsTrue(actual.Index.TryGetSprite(kv.Key, out SpriteEntry a), $"{what}: '{kv.Key}' missing");
                Assert.AreEqual(kv.Value.X, a.X, $"{what}: '{kv.Key}' X");
                Assert.AreEqual(kv.Value.Y, a.Y, $"{what}: '{kv.Key}' Y");
                Assert.AreEqual(kv.Value.Padding, a.Padding, $"{what}: '{kv.Key}' Padding");
            }
        }
    }
}
