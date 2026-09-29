// Text/Sprites/SpriteSheetTests.cs — the sprite-fetch gate, sheet decode/repack, and sprite-source fixture/factory teeth.
//
// Fetch gating, then sheet decode, then source.
//
// Contents:
//   SpriteFetchGatingTests          — the sprite sheet must be fetched for a style that has no symbol layers.
//   SpriteSheetTests                — SpriteSheet decodes the fixture sprite PNG, repacks it with a one-texel transparent border per sprite, and flips its rows so a top-left-origin sprite-JSON coord (x,y) — of the repacked index — reads back at Texture2D.GetPixel(x,y).
//   SpriteSourceTests               — FixtureSpriteSource serves the committed fixture sheet (mirrors FixtureGlyphSource's role for glyphs), and SpriteSourceFactory's missing-URL resilience seam (a style with no sprite entries returns an empty list rather than throwing — icons are optional,…
//   SpriteSourceFactoryRatioTests   — the @2x suffix decision from the device pixel ratio, and SpriteSheetSource's 404-on-@2x fallback to 1x.
//   DevicePixelRatioWiringTests     — MapView.Config.DevicePixelRatio reaches SymbolSubsystem's sprite-source factory.

using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Style;
using MapRenderer.Unity.Rendering.Map;
using MapRenderer.Unity.Rendering.Tile; // UniTaskParkExtensions.WaitOffPlayerLoop
using MapRenderer.Unity.Text;
using SymbolStyle = MapRenderer.Unity.Style.Symbol;
using System;
using System.IO;
using Unity.Mathematics;
using MapRenderer.Core.Text.Sprites;
using MapRenderer.Unity.Text.Sprites;
using System.Text.RegularExpressions;
using MapRenderer.Unity.Text.Placement;
using MapRenderer.Unity.Rendering.Source;
using MapRenderer.Tests.DataSources;
using Object = UnityEngine.Object;


namespace MapRenderer.Tests.Text.Sprites
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // SpriteFetchGatingTests — the sprite sheet is fetched even for a style with no symbol layers
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sprite sheet must be fetched for a style that has <b>no symbol layers</b>.
    /// <c>fill-pattern</c> resolves against the same sheet, so an early "no symbol layers" return in
    /// <c>SymbolSubsystem.SetStyle</c> before the fetch leaves every pattern layer unresolved, with no error.
    /// Liberty has symbol layers and hides this, so it needs its own tooth.
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
                subsystem.SpriteSourceFactoryOverride = (_, _) =>
                {
                    fetches++;
                    return new[] { ("default", (ISpriteSource)new FixtureSpriteSource()) };
                };

                // No symbol layers at all — the case an early return on "no symbol layers" would swallow.
                var style = TestStyle.Document(SymbolFreePatternStyle);
                subsystem.SetStyle(style, System.Array.Empty<SymbolStyle.StyleLayer>());

                Assert.IsFalse(subsystem.HasSymbolLayers,
                    "precondition: this style must genuinely have no symbol layers, or the test proves nothing");
                Assert.AreEqual(1, fetches,
                    "the sprite sheet must be fetched even with zero symbol layers — fill-pattern layers " +
                    "resolve against the same sheet. A 0 here means the no-symbol-layers early return has " +
                    "moved back above the fetch and pattern fills will silently never paint.");

                // The fixture source completes synchronously, but the decode hops to the main thread — pump a
                // few frames so the sheet actually lands rather than asserting on the in-flight state.
                for (var settle = SettleTimeout.Start(); settle.Running && subsystem.SpriteAtlas == null; ) yield return null;

                Assert.IsNotNull(subsystem.SpriteAtlas,
                    "the fetched sheet must reach SpriteAtlas — that is what RenderLayerSet.SetSprites pushes " +
                    "to the fill layers.");
                Assert.IsTrue(
                    MapRenderer.Unity.Style.Fill.FillPattern.TryResolve("marker", subsystem.SpriteAtlas, out _),
                    "the fixture sheet's 'marker' sprite must resolve through the delivered atlas");
            }
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SpriteSheetMergeTests — array-form root `sprite`: N sheets merge into one atlas
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class SpriteSheetMergeTests : BaseTestFixture
    {
        /// <summary>A tiny solid-color sheet with one sprite named "x" covering the whole image.</summary>
        private static (byte[] Png, string Json) BuildOneSpriteFixture(int size, Color32 color)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false);
            try
            {
                var pixels = new Color32[size * size];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
                tex.SetPixels32(pixels);
                tex.Apply(updateMipmaps: false);
                byte[] png = tex.EncodeToPNG();
                string json = $"{{\"x\":{{\"width\":{size},\"height\":{size},\"x\":0,\"y\":0,\"pixelRatio\":1}}}}";
                return (png, json);
            }
            finally
            {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void TwoSheets_DefaultUnprefixed_OtherIdPrefixed_DistinctRects()
        {
            (byte[] defaultPng, string defaultJson) = BuildOneSpriteFixture(4, new Color32(255, 0, 0, 255));
            (byte[] aPng, string aJson) = BuildOneSpriteFixture(6, new Color32(0, 255, 0, 255));

            var sheets = new (string Id, byte[] Png, SpriteIndex Index)[]
            {
                ("default", defaultPng, SpriteIndex.Parse(defaultJson)),
                ("a", aPng, SpriteIndex.Parse(aJson)),
            };

            using var sheet = new SpriteSheet(sheets);

            Assert.IsTrue(sheet.View.Index.TryGetSprite("x", out SpriteEntry defaultEntry),
                "the default sheet's name must stay unprefixed");
            Assert.IsTrue(sheet.View.Index.TryGetSprite("a:x", out SpriteEntry aEntry),
                "the non-default sheet's name must be prefixed 'id:name'");
            Assert.IsFalse(sheet.View.Index.TryGetSprite("default:x", out _),
                "the default id must never itself be used as a prefix");

            Assert.AreEqual(4, defaultEntry.Width);
            Assert.AreEqual(6, aEntry.Width);
            Assert.AreNotEqual((defaultEntry.X, defaultEntry.Y), (aEntry.X, aEntry.Y),
                "the two sheets must occupy distinct, non-overlapping rects, not one overwriting the other");
        }

        [UnityTest]
        public IEnumerator OneSheetMissing_TheOtherSheetsIconStillWorks()
        {
            var go  = Track(new GameObject("SpriteMergeMissingHost"));
            var cam = go.AddComponent<Camera>();
            var mapCamera = new MapCamera(cam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));
            using var subsystem = new SymbolSubsystem(mapCamera);

            subsystem.SpriteSourceFactoryOverride = (_, _) => new (string, ISpriteSource)[]
            {
                ("default", new FixtureSpriteSource()),
                ("missing", new GatedSpriteSource(_ => UniTask.FromResult(SpriteResponse.Absent()))),
            };

            var style = TestStyle.Document("{\"version\":8,\"layers\":[]}");
            subsystem.SetStyle(style, System.Array.Empty<SymbolStyle.StyleLayer>());

            for (var settle = SettleTimeout.Start(); settle.Running && subsystem.SpriteAtlas == null; ) yield return null;

            Assert.IsNotNull(subsystem.SpriteAtlas,
                "the surviving sheet must still reach SpriteAtlas even though the other sheet was missing");
            Assert.IsTrue(subsystem.SpriteAtlas.Index.TryGetSprite("marker", out _),
                "the working sheet's icon must resolve — a missing sheet must drop only its own names");
        }

        [UnityTest]
        public IEnumerator OneSheetThrows_TheOtherSheetsIconStillWorks()
        {
            var go  = Track(new GameObject("SpriteMergeThrowHost"));
            var cam = go.AddComponent<Camera>();
            var mapCamera = new MapCamera(cam, new CameraProperties(
                new GeoCoordinate3D { Latitude = 0.0, Longitude = 0.0, Altitude = 0.0 },
                zoom: 5.0, heading: 0.0, tilt: 0.0));
            using var subsystem = new SymbolSubsystem(mapCamera);

            subsystem.SpriteSourceFactoryOverride = (_, _) => new (string, ISpriteSource)[]
            {
                ("default", new FixtureSpriteSource()),
                ("broken", new GatedSpriteSource(_ => throw new InvalidOperationException("simulated fetch failure"))),
            };

            var style = TestStyle.Document("{\"version\":8,\"layers\":[]}");
            subsystem.SetStyle(style, System.Array.Empty<SymbolStyle.StyleLayer>());

            for (var settle = SettleTimeout.Start(); settle.Running && subsystem.SpriteAtlas == null; ) yield return null;

            Assert.IsNotNull(subsystem.SpriteAtlas,
                "the surviving sheet must still reach SpriteAtlas even though the other sheet's fetch threw");
            Assert.IsTrue(subsystem.SpriteAtlas.Index.TryGetSprite("marker", out _),
                "the working sheet's icon must resolve — a per-sheet fetch exception must drop only that sheet");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SpriteSheetTests — decode, repack with a one-texel border, and flip rows
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SpriteSheet"/> decodes the fixture sprite PNG, repacks it with a one-texel
    /// transparent border per sprite, and flips its rows so a top-left-origin sprite-JSON coord
    /// <c>(x,y)</c> — of the <b>repacked</b> index — reads back at <c>Texture2D.GetPixel(x,y)</c>, the same
    /// contract <see cref="GlyphAtlasTexture"/> holds for the glyph atlas. A wrong flip makes the color pins
    /// read transparent instead of the fixture's marker/star/dot colors.
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

                // The repack relocates every sprite. Reading the colour pins through the derived index keeps
                // this an ORIENTATION tooth instead of a packing-layout tooth.
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

                // Anti-flip guard: the dot's 10-texel cell mirrors into the 26-texel sheet's transparent empty
                // region. A backwards or doubled row flip reads the dot's opaque blue here instead.
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

                // Hand-derived cells (rect + 1 border texel per side): star 26, marker 18, dot 10. Width is
                // max(source 64, widest cell 26) == 64; 26 + 18 + 10 == 54 fits ONE shelf, 26 tall.
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
    /// missing-URL resilience seam (a style with no <c>sprite</c> entries returns an empty list rather
    /// than throwing — icons are optional, unlike glyphs).
    /// </summary>
    [TestFixture]
    public class SpriteSourceTests : BaseTestFixture
    {
        [Test]
        public void FixtureSpriteSource_FetchAsync_ReturnsIndexAndPngBytes()
        {
            using var source = new FixtureSpriteSource();

            // FixtureSpriteSource resolves via UniTask.FromResult, so GetAwaiter().GetResult() does not block.
            SpriteResponse response = source.FetchAsync().GetAwaiter().GetResult();

            Assert.IsTrue(response.HasData, "the committed fixture sheet must be found");
            Assert.IsNotNull(response.Png);
            Assert.Greater(response.Png.Length, 0, "fixture PNG bytes must be non-empty");

            SpriteIndex index = SpriteIndex.Parse(response.Json);
            Assert.AreEqual(3, index.Count, "fixture sprite.json declares 3 sprites (marker/star/dot)");
            Assert.IsTrue(index.TryGetSprite("marker", out _));
            Assert.IsTrue(index.TryGetSprite("star", out _));
            Assert.IsTrue(index.TryGetSprite("dot", out _));

            // The SOURCE size, asserted on the decode. Non-obvious why: the repack output is 64x26 whatever the
            // source height, because ShelfRectPacker bounds only width, so a 64x128 fixture passes the size
            // check below yet breaks every sheet coordinate the other icon tests hand-derive.
            var decoded = Track(new UnityEngine.Texture2D(2, 2, UnityEngine.TextureFormat.RGBA32, mipChain: false));
            {
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
        public void SpriteSourceFactory_NoSpriteEntries_ReturnsEmptyListAndWarnsOnce()
        {
            // The "warn once" latch is process-wide, and any earlier test that applies a sprite-less style
            // consumes the one warning. Clearing it here keeps this assertion independent of test order.
            SpriteSourceFactory.WarnedMissingUrl = false;

            LogAssert.Expect(UnityEngine.LogType.Warning, new Regex("SpriteSourceFactory"));

            var sources = SpriteSourceFactory.Create(new StyleDocument(), 1.0);

            Assert.AreEqual(0, sources.Count, "a style with no sprite entries must yield an empty list, not throw");
        }

        [Test]
        public void SpriteSourceFactory_WithSpriteUrl_ReturnsOneSource()
        {
            var sources = SpriteSourceFactory.Create(
                new StyleDocument { Sprites = { new SpriteReference { Id = "default", Url = "https://example.invalid/sprite" } } },
                1.0);

            Assert.AreEqual(1, sources.Count, "a style with one sprite entry must yield one source");
            Assert.AreEqual("default", sources[0].Id);
            using ISpriteSource source = sources[0].Source;
            Assert.IsNotNull(source, "a style with a sprite URL must yield a real source");
            Assert.IsInstanceOf<SpriteSheetSource>(source);
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // SpriteSourceFactoryRatioTests — the @2x suffix decision, and its 404 fallback to 1x
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SpriteSourceFactory.Create"/> picks the sprite sheet's density suffix from the device
    /// pixel ratio: at or above <see cref="SpriteSourceFactory.TwoXThreshold"/> (1.5) requests <c>@2x</c>;
    /// an implausible ratio sanitizes to 1x, through the same <c>DeviceScaling.SafeRatio</c> fallback
    /// framing uses. <see cref="SpriteSheetSource"/> falls back to the plain 1x sheet only when the @2x
    /// sheet is explicitly absent (404/204) — a 5xx or a cancel propagates instead.
    /// </summary>
    [TestFixture]
    public class SpriteSourceFactoryRatioTests
    {
        /// <summary>Serves every request from a caller-supplied responder, recording each requested path.
        /// Runs until <c>Stop()</c>ped — unlike a single-shot loopback, the fallback arm needs to answer
        /// TWO requests.</summary>
        private static HttpListener StartPathAwareServer(
            int port, Func<string, (int Status, byte[] Body)> respond, List<string> requestedPaths)
        {
            var hl = new HttpListener();
            hl.Prefixes.Add($"http://127.0.0.1:{port}/");
            hl.Start();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    while (true)
                    {
                        HttpListenerContext ctx = hl.GetContext();
                        string path = ctx.Request.Url.AbsolutePath;
                        lock (requestedPaths) requestedPaths.Add(path);
                        (int status, byte[] body) = respond(path);
                        ctx.Response.StatusCode = status;
                        if (body != null && body.Length > 0)
                        {
                            ctx.Response.ContentLength64 = body.Length;
                            ctx.Response.OutputStream.Write(body, 0, body.Length);
                        }
                        ctx.Response.OutputStream.Close();
                        ctx.Response.Close();
                    }
                }
                catch (HttpListenerException) { /* stopped between requests */ }
                catch (ObjectDisposedException) { }
            });
            return hl;
        }

        /// <summary>Fetches once against a 404-everything server and hands the FIRST requested path to
        /// <paramref name="onPath"/> — enough to see which suffix the factory chose.</summary>
        private static IEnumerator FirstRequestedPathAsync(double ratio, Action<string> onPath)
        {
            int port = HttpTileSourceTests.FindFreePort();
            var requestedPaths = new List<string>();
            var hl = StartPathAwareServer(port, _ => (404, null), requestedPaths);

            Exception caught = null;
            try
            {
                var sources = SpriteSourceFactory.Create(
                    new StyleDocument { Sprites = { new SpriteReference { Id = "default", Url = $"http://127.0.0.1:{port}/sprite" } } }, ratio);
                using ISpriteSource source = sources[0].Source;
                yield return source.FetchAsync()
                    .ContinueWith((Action<SpriteResponse>)(_ => { }))
                    .ToCoroutine(ex => { caught = ex; });
            }
            finally { try { hl.Stop(); } catch { } try { hl.Close(); } catch { } }

            Assert.IsNull(caught, $"a 404 must not throw. Exception: {caught?.Message}");
            Assert.Greater(requestedPaths.Count, 0, "at least one request must have been made");
            onPath(requestedPaths[0]);
        }

        /// <summary>The FIRST path requested reflects the ratio's chosen suffix: at or above the threshold
        /// requests <c>@2x</c>; below it, or at a NaN/implausible ratio DeviceScaling.SafeRatio sanitizes
        /// to 1x, requests the plain sheet.</summary>
        [UnityTest]
        public IEnumerator FetchAsync_RequestsTheSuffixTheRatioChooses()
        {
            (double Ratio, bool ExpectsTwoX, string Why)[] cases =
            {
                (1.0, false, "ratio 1.0"),
                (2.0, true, "ratio 2.0"),
                (SpriteSourceFactory.TwoXThreshold - 0.01, false, "just below the threshold"),
                (SpriteSourceFactory.TwoXThreshold, true, "exactly at the threshold"),
                (double.NaN, false, "a NaN ratio, sanitized to 1x"),
                (100.0, false, "an implausibly high ratio, sanitized to 1x"),
            };

            foreach (var c in cases)
            {
                string firstPath = null;
                yield return FirstRequestedPathAsync(c.Ratio, p => firstPath = p);
                Assert.AreEqual(c.ExpectsTwoX, firstPath.EndsWith("@2x.json"),
                    $"{c.Why}: expected @2x={c.ExpectsTwoX}, first request was '{firstPath}'");
            }
        }

        /// <summary>A 200 on the @2x sheet must be used AS-IS — nothing may also request the 1x sheet, or
        /// accept its DIFFERENT content. Distinct content per suffix is essential: an implementation that
        /// always fetches the 1x sheet regardless of suffix would otherwise pass every other test here.</summary>
        [UnityTest]
        public IEnumerator FetchAsync_2xSheetSucceeds_NeverFallsBackTo1x()
        {
            int port = HttpTileSourceTests.FindFreePort();
            byte[] twoXJson = System.Text.Encoding.UTF8.GetBytes("{\"only-in-2x\":{\"width\":1,\"height\":1,\"x\":0,\"y\":0,\"pixelRatio\":2}}");
            byte[] oneXJson = System.Text.Encoding.UTF8.GetBytes("{\"only-in-1x\":{\"width\":1,\"height\":1,\"x\":0,\"y\":0,\"pixelRatio\":1}}");
            byte[] png = File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "sprites", "sample-sprite.png"));

            var requestedPaths = new List<string>();
            var hl = StartPathAwareServer(port, path =>
            {
                if (path.EndsWith(".png")) return (200, png);
                return (200, path.Contains("@2x") ? twoXJson : oneXJson);
            }, requestedPaths);

            SpriteResponse response = default;
            Exception caught = null;
            try
            {
                var sources = SpriteSourceFactory.Create(
                    new StyleDocument { Sprites = { new SpriteReference { Id = "default", Url = $"http://127.0.0.1:{port}/sprite" } } }, 2.0);
                using ISpriteSource source = sources[0].Source;
                yield return source.FetchAsync()
                    .ContinueWith((Action<SpriteResponse>)(r => { response = r; }))
                    .ToCoroutine(ex => { caught = ex; });
            }
            finally { try { hl.Stop(); } catch { } try { hl.Close(); } catch { } }

            Assert.IsNull(caught, $"a successful @2x fetch must not throw. Exception: {caught?.Message}");
            Assert.IsTrue(requestedPaths.TrueForAll(p => p.Contains("@2x")),
                $"a successful @2x sheet must never also request the 1x sheet: {string.Join(", ", requestedPaths)}");
            Assert.IsTrue(response.HasData, "the @2x fetch must succeed");
            Assert.AreEqual(System.Text.Encoding.UTF8.GetString(twoXJson), response.Json,
                "the response must carry the @2x sheet's OWN content, not the 1x sheet's different content");
        }

        /// <summary>A 404 on the @2x sheet falls back to the plain 1x sheet, and the caller sees a
        /// successful response built from the FALLBACK content — reusing the committed sprite fixture, the
        /// same one <see cref="FixtureSpriteSource"/> serves.</summary>
        [UnityTest]
        public IEnumerator FetchAsync_2xSheet404s_FallsBackToThe1xSheet()
        {
            int port = HttpTileSourceTests.FindFreePort();
            byte[] fixtureJson = File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "sprites", "sample-sprite.json"));
            byte[] fixturePng  = File.ReadAllBytes(Path.Combine(Application.dataPath, "Fixtures", "sprites", "sample-sprite.png"));
            var requestedPaths = new List<string>();
            var hl = StartPathAwareServer(port, path =>
            {
                if (path.Contains("@2x")) return (404, null);
                return (200, path.EndsWith(".json") ? fixtureJson : fixturePng);
            }, requestedPaths);

            SpriteResponse response = default;
            Exception caught = null;
            try
            {
                var sources = SpriteSourceFactory.Create(
                    new StyleDocument { Sprites = { new SpriteReference { Id = "default", Url = $"http://127.0.0.1:{port}/sprite" } } }, 2.0);
                using ISpriteSource source = sources[0].Source;
                yield return source.FetchAsync()
                    .ContinueWith((Action<SpriteResponse>)(r => { response = r; }))
                    .ToCoroutine(ex => { caught = ex; });
            }
            finally { try { hl.Stop(); } catch { } try { hl.Close(); } catch { } }

            Assert.IsNull(caught, $"a 404-then-fallback must not throw. Exception: {caught?.Message}");
            Assert.IsTrue(requestedPaths.Exists(p => p.Contains("@2x")), "the @2x sheet must be requested first");
            Assert.IsTrue(requestedPaths.Exists(p => !p.Contains("@2x")), "the plain 1x sheet must be requested as a fallback");
            Assert.IsTrue(response.HasData, "the fallback 1x fetch must succeed and report HasData");
            Assert.AreEqual(System.Text.Encoding.UTF8.GetString(fixtureJson), response.Json,
                "the response must carry the FALLBACK (1x) sheet's content");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // DevicePixelRatioWiringTests — MapView.Config.DevicePixelRatio -> SymbolSubsystem
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>MapView.Config.DevicePixelRatio</c> reaches <c>SymbolSubsystem</c>'s sprite-source
    /// factory, through the property <c>MapView.SetStyle</c> sets right before calling
    /// <c>SymbolSubsystem.SetStyle</c>.</summary>
    [TestFixture]
    public class DevicePixelRatioWiringTests : BaseTestFixture
    {
        [Test]
        public void SetStyle_PassesConfiguredDevicePixelRatio_ToTheSpriteSourceFactory()
        {
            var go = Track(new GameObject("DevicePixelRatioWiring"));
            var view = go.AddComponent<MapViewComponent>().WithTestMaterials();
            view.WithTestCamera();
            view.Config.DevicePixelRatio = 2.0;

            double? seenRatio = null;
            view.View.SymbolSubsystem.SpriteSourceFactoryOverride = (styleDoc, ratio) =>
            {
                seenRatio = ratio;
                return null; // no sprite fetch needed — this test only checks what the factory would receive
            };

            string styleJson = @"{
                ""version"": 8,
                ""layers"": [ { ""id"": ""bg"", ""type"": ""background"",
                                ""paint"": { ""background-color"": ""#ff0000"" } } ]
            }";
            var task = view.SetStyle(TestStyle.Document(styleJson), "ratio-wiring").Preserve();
            task.WaitOffPlayerLoop(5000);
            task.GetAwaiter().GetResult();

            Assert.AreEqual(2.0, seenRatio,
                "SymbolSubsystem's sprite-source factory must see the configured DevicePixelRatio");
        }
    }
}
