// Unity-only, NOT in core-tests.csproj — FixtureSpriteSource/SpriteSourceFactory/SpriteSheet all touch
// UnityEngine (Application.dataPath, Texture2D, Debug.LogWarning), which the fast dotnet-test loop
// (Tools/core-tests) cannot compile/run.

using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.TestTools;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Unity.Text;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Unity.Rendering.Source;

namespace MapRenderer.Tests.Text.Sprites
{
    /// <summary>
    /// I4 acceptance: <see cref="FixtureSpriteSource"/> serves the committed fixture sheet (mirrors
    /// <c>FixtureGlyphSource</c>'s role for glyphs), and <see cref="SpriteSourceFactory"/>'s
    /// missing-URL resilience seam (a style with no <c>sprite</c> URL returns <c>null</c> rather than
    /// throwing — icons are optional, unlike glyphs).
    /// </summary>
    [TestFixture]
    public class SpriteSourceTests
    {
        [Test]
        public void FixtureSpriteSource_FetchAsync_ReturnsIndexAndPngBytes()
        {
            using var source = new FixtureSpriteSource();

            // FixtureSpriteSource resolves via UniTask.FromResult -- already completed, so
            // GetAwaiter().GetResult() does not block (mirrors the synchronous-fixture-source pattern;
            // contrast the SwitchToThreadPool sources, which need a spin-wait).
            SpriteResponse response = source.FetchAsync().GetAwaiter().GetResult();

            Assert.IsTrue(response.HasData, "the committed fixture sheet must be found");
            Assert.IsNotNull(response.Png);
            Assert.Greater(response.Png.Length, 0, "fixture PNG bytes must be non-empty");

            SpriteIndex index = SpriteIndex.Parse(response.Json);
            Assert.AreEqual(3, index.Count, "fixture sprite.json declares 3 sprites (marker/star/dot)");
            Assert.IsTrue(index.TryGetSprite("marker", out _));
            Assert.IsTrue(index.TryGetSprite("star", out _));
            Assert.IsTrue(index.TryGetSprite("dot", out _));

            // The SOURCE dimensions, asserted directly off a decode. Not derivable from the bound sheet:
            // the repack packs the three indexed cells into a 64x26 output whatever the source height was,
            // because source height is not a packing lower bound (ShelfRectPacker's only lower bound is
            // width). A 64x128 fixture would therefore satisfy the repacked-size comparison below while
            // silently breaking every sheet coordinate the other icon tests hand-derive — so the height
            // tooth has to be taken here, on the decode itself.
            var decoded = new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, mipChain: false);
            try
            {
                // The static form of the `LoadImage` EXTENSION method — this file deliberately carries no
                // top-level `using UnityEngine;` (it qualifies its few engine references instead), and an
                // extension method cannot be reached through a qualified type name.
                Assert.IsTrue(UnityEngine.ImageConversion.LoadImage(decoded, response.Png),
                    "the fetched bytes must decode as a PNG");
                Assert.AreEqual(new int2(64, 64), new int2(decoded.width, decoded.height),
                    "the committed fixture sheet is 64x64 — the source the repack is planned over");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(decoded);
            }

            // Round-trip into a real SpriteSheet: what the sheet BINDS is the padded repack of that decode,
            // so the size to compare against is the planner's over the 64x64 source just proven above.
            var sheet = new SpriteSheet(response.Png, index);
            try
            {
                SpritePadPlan plan = SpriteSheetPadder.Plan(index, new int2(64, 64), padding: 1);
                Assert.AreEqual(plan.Size, sheet.View.Size,
                    "the bound sheet must be the padded repack of the 64x64 fixture decode");
            }
            finally
            {
                sheet.Dispose();
            }
        }

        [Test]
        public void SpriteSourceFactory_NullSpriteUrl_ReturnsNullAndWarnsOnce()
        {
            // The "warn once" latch is process-wide, and as of P2 the sprite fetch runs for far more styles
            // (it is no longer gated on a style having symbol layers — fill-pattern resolves against the same
            // sheet). Any earlier test that applies a sprite-less style consumes the one warning, so clear
            // the latch here rather than let this assertion depend on test order.
            SpriteSourceFactory.WarnedMissingUrl = false;

            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("SpriteSourceFactory"));

            ISpriteSource source = SpriteSourceFactory.Create(new StyleDocument { Sprite = null });

            Assert.IsNull(source, "a style with no sprite URL must yield a null source, not throw");
        }

        [Test]
        public void SpriteSourceFactory_WithSpriteUrl_ReturnsNonNullSource()
        {
            using ISpriteSource source =
                SpriteSourceFactory.Create(new StyleDocument { Sprite = "https://example.invalid/sprite" });

            Assert.IsNotNull(source, "a style with a sprite URL must yield a real source");
            Assert.IsInstanceOf<UnityWebRequestSpriteSource>(source);
        }
    }
}
