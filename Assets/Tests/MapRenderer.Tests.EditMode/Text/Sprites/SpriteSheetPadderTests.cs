// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Unity.Mathematics;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Tests.Text.Sprites
{
    /// <summary>
    /// Teeth for the padded repack's RECT half (<see cref="SpriteSheetPadder"/>): every sprite ends up with
    /// its own one-texel border that no neighbour's content can reach into, the plan is byte-for-byte
    /// reproducible regardless of dictionary insertion order, aliased names stay aliased, and a malformed
    /// sheet's degenerate entries survive untouched.
    ///
    /// <para>The separation tooth
    /// (<see cref="EveryPlacedRect_IsSeparatedByAtLeastTwoTexels_AndKeptOffTheSheetEdge"/>) is
    /// the one that makes the border REAL: a plan that placed cells correctly but let two content rects sit
    /// one texel apart would give them a SHARED border texel, and the whole point is that each sprite's
    /// ramp is its
    /// own.</para>
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
            // `x: 2147483647, width: 1` is malformed but PARSEABLE — SpriteIndex.Parse tolerates a bad sheet
            // rather than throwing, so this reaches the planner in production. In 32-bit signed arithmetic
            // `X + Width` wraps to int.MinValue, which sails past a naive `<= sourceSize.x` bound; the entry
            // is then packed, its blit copies from a NEGATIVE byte offset, and Buffer.BlockCopy throws — out
            // of the SpriteSheet constructor, before either the texture or the index is installed. One
            // malformed line of sprite JSON would make EVERY icon on the map disappear.
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
