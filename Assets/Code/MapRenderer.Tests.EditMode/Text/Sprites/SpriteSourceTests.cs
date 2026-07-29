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

namespace MapRenderer.Tests
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

            // Round-trip into a real SpriteSheet: proves the fetched bytes are a decodable 64x64 PNG,
            // not just non-empty.
            var sheet = new SpriteSheet(response.Png, index);
            try
            {
                Assert.AreEqual(new int2(64, 64), sheet.View.Size);
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
