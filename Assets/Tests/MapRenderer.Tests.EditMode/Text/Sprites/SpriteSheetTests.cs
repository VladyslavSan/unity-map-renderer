// Text/Sprites/SpriteSheetTests.cs — the sprite-fetch gate, sheet decode/repack, and sprite-source fixture/factory teeth.
//
// Fetch gating, then sheet decode, then source.
//
// Contents:
//   SpriteFetchGatingTests  — the sprite sheet must be fetched for a style that has no symbol layers.
//   SpriteSheetTests        — SpriteSheet decodes the fixture sprite PNG, repacks it with a one-texel transparent border per sprite, and flips its rows so a top-left-origin sprite-JSON coord (x,y) — of the repacked index — reads back at Texture2D.GetPixel(x,y).
//   SpriteSourceTests       — FixtureSpriteSource serves the committed fixture sheet (mirrors FixtureGlyphSource's role for glyphs), and SpriteSourceFactory's missing-URL resilience seam (a style with no sprite URL returns null rather than throwing — icons are optional,…

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Style;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Core.Style.Symbol;
using System;
using System.IO;
using Unity.Mathematics;
using MapRenderer.Core.Text.Sprites;
using System.Text.RegularExpressions;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Unity.Rendering.Source;
using Object = UnityEngine.Object;


namespace MapRenderer.Tests.Text.Sprites
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // SpriteFetchGatingTests — the sprite sheet is fetched even for a style with no symbol layers
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sprite sheet must be fetched for a style that has <b>no symbol layers</b>.
    ///
    /// <para><c>SymbolSubsystem.SetStyle</c> must not return early on "no symbol layers — stay idle"
    /// before kicking off the fetch: <c>fill-pattern</c> resolves against the same sheet, so a style with
    /// pattern fills and no symbol layers would never fetch a sheet if that early return fired — every
    /// pattern layer would stay unresolved and clip forever, no error, no warning, just missing fills.</para>
    ///
    /// <para>Liberty hides this (it has symbol layers), which is exactly why it needs its own tooth.</para>
    /// </summary>
    [TestFixture]
    public class SpriteFetchGatingTests : BaseTestFixture
    {
        private const string SymbolFreePatternStyle = @"{
            ""version"": 8,
            ""sprite"": ""https://example.invalid/sprite"",
            ""sources"": { ""src"": { ""type"": ""vector"", ""tiles"": [""https://example.invalid/{z}/{x}/{y}.pbf""] } },
            ""layers"": [
                { ""id"": ""plazas"", ""type"": ""fill"", ""source"": ""src"", ""source-layer"": ""transportation"",
                  ""paint"": { ""fill-pattern"": ""marker"" } }
            ]
        }";

        [UnityTest]
        public IEnumerator StyleWithNoSymbolLayers_StillFetchesTheSpriteSheet()
        {
            var go  = Track(new GameObject("SpriteFetchGatingHost"));
            var cam = go.AddComponent<Camera>();
            var mapCamera = new MapCamera(cam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));
            using var subsystem = new SymbolSubsystem(mapCamera);
            {
                int fetches = 0;
                subsystem.SpriteSourceFactoryOverride = _ =>
                {
                    fetches++;
                    return new FixtureSpriteSource();
                };

                // No symbol layers at all — the case an early return on "no symbol layers" would swallow.
                var style = StyleParser.Parse(SymbolFreePatternStyle);
                subsystem.SetStyle(style, System.Array.Empty<SymbolStyle.StyleLayer>());

                Assert.IsFalse(subsystem.HasSymbolLayers,
                    "precondition: this style must genuinely have no symbol layers, or the test proves nothing");
                Assert.AreEqual(1, fetches,
                    "the sprite sheet must be fetched even with zero symbol layers — fill-pattern layers " +
                    "resolve against the same sheet. A 0 here means the no-symbol-layers early return has " +
                    "moved back above the fetch and pattern fills will silently never paint.");

                // The fixture source completes synchronously, but the decode hops to the main thread — pump a
                // few frames so the sheet actually lands rather than asserting on the in-flight state.
                for (int i = 0; i < 8 && subsystem.SpriteAtlas == null; i++) yield return null;

                Assert.IsNotNull(subsystem.SpriteAtlas,
                    "the fetched sheet must reach SpriteAtlas — that is what RenderLayerSet.SetSprites pushes " +
                    "to the fill layers.");
                Assert.IsTrue(
                    MapRenderer.Core.Style.Fill.FillPattern.TryResolve("marker", subsystem.SpriteAtlas, out _),
                    "the fixture sheet's 'marker' sprite must resolve through the delivered atlas");
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SpriteSheetTests — decode, repack with a one-texel border, and flip rows
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SpriteSheet"/> decodes the fixture sprite PNG, repacks it with a one-texel
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

    // ───────────────────────────────────────────────────────────────────────────────────
    // SpriteSourceTests — the fixture source and the missing-URL resilience seam
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="FixtureSpriteSource"/> serves the committed fixture sheet (mirrors
    /// <c>FixtureGlyphSource</c>'s role for glyphs), and <see cref="SpriteSourceFactory"/>'s
    /// missing-URL resilience seam (a style with no <c>sprite</c> URL returns <c>null</c> rather than
    /// throwing — icons are optional, unlike glyphs).
    /// </summary>
    [TestFixture]
    public class SpriteSourceTests : BaseTestFixture
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
            var decoded = Track(new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, mipChain: false));
            {
                // The static form of the `LoadImage` EXTENSION method — this file carries no
                // top-level `using UnityEngine;` (it qualifies its few engine references instead), and an
                // extension method cannot be reached through a qualified type name.
                Assert.IsTrue(UnityEngine.ImageConversion.LoadImage(decoded, response.Png),
                    "the fetched bytes must decode as a PNG");
                Assert.AreEqual(new int2(64, 64), new int2(decoded.width, decoded.height),
                    "the committed fixture sheet is 64x64 — the source the repack is planned over");
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
            // The "warn once" latch is process-wide, and the sprite fetch runs for far more styles
            // (it is not gated on a style having symbol layers — fill-pattern resolves against the same
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
