// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using MapRenderer.Core.Text;
using MapRenderer.Unity.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// S18 Unity-side batch (Slice 4 integration) — <see cref="GlyphManager"/>'s fetch/decode/cache/atlas
    /// wiring, exercised against a fake in-memory <see cref="TestGlyphSource"/> serving the committed
    /// <c>Assets/Fixtures/glyphs/NotoSansRegular/*.bytes</c> fixtures (never the network).
    /// </summary>
    [TestFixture]
    public class GlyphManagerTests
    {
        // ── Fixture loader (walk-up from cwd then AppContext — works in Unity batch mode AND dotnet) ─

        private static byte[] LoadFixture(string fileName)
        {
            string[] starts = { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string p = Path.Combine(dir, "Assets", "Fixtures", "glyphs", "NotoSansRegular", fileName);
                    if (File.Exists(p)) return File.ReadAllBytes(p);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            throw new FileNotFoundException(
                $"{fileName} not found. Tried walking up from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        // =========================================================================================
        // Single-range fetch/decode/cache/atlas wiring: EnsureFontRangeAsync populates BOTH the
        // GlyphCache (decoded range, keyed by fontName+rangeStart) AND the shared GlyphAtlas (per-glyph
        // entries), from the real decoded fixture bytes -- and does not re-fetch on a repeat call.
        // =========================================================================================
        [Test]
        public async Task EnsureFontRangeAsync_FetchesDecodesCachesAndAppendsGlyphs()
        {
            byte[] latinBytes = LoadFixture("0-255.pbf.bytes");
            var ranges = new Dictionary<(string, int), byte[]> { [("Noto Sans Regular", 0)] = latinBytes };
            var source = TestGlyphSource.FromRanges(ranges);
            var manager = new GlyphManager(source);

            await manager.EnsureFontRangeAsync("Noto Sans Regular", 0);

            Assert.IsTrue(manager.Cache.TryGet("Noto Sans Regular", 0, out FontStackGlyphs glyphs),
                "the decoded range must be stored in the cache under (fontName, rangeStart)");
            Assert.IsTrue(glyphs.Glyphs.TryGetValue(65u, out SdfGlyph expectedA), "fixture precondition: 'A' must decode");

            Assert.IsTrue(manager.Atlas.TryGetEntry(65u, out GlyphAtlasEntry entry),
                "the decoded glyph must be appended to the shared atlas");
            Assert.AreEqual(expectedA.Advance, entry.Advance);
            Assert.AreEqual(expectedA.Left, entry.Left);
            Assert.AreEqual(expectedA.Top, entry.Top);

            Assert.AreEqual(1, source.FetchCount);

            // Repeat call: already cached -> must NOT re-fetch (keep-all-per-session, §6.4a).
            await manager.EnsureFontRangeAsync("Noto Sans Regular", 0);
            Assert.AreEqual(1, source.FetchCount, "an already-cached (fontName, rangeStart) must not be refetched");
        }

        // =========================================================================================
        // §6.3 policy via GlyphManager: a range the source reports absent is cached as an empty range
        // (never throws) so it is not refetched on every subsequent request.
        // =========================================================================================
        [Test]
        public async Task EnsureFontRangeAsync_AbsentRange_CachesEmptyAndDoesNotRefetch()
        {
            var source = TestGlyphSource.FromRanges(new Dictionary<(string, int), byte[]>()); // nothing served
            var manager = new GlyphManager(source);

            await manager.EnsureFontRangeAsync("Missing Font", 0);

            Assert.IsTrue(manager.Cache.TryGet("Missing Font", 0, out FontStackGlyphs glyphs),
                "an absent range must still be cached (as empty), not left unrecorded");
            Assert.AreEqual(0, glyphs.Glyphs.Count);
            Assert.AreEqual(1, source.FetchCount);

            await manager.EnsureFontRangeAsync("Missing Font", 0);
            Assert.AreEqual(1, source.FetchCount, "a cached-absent range must not be refetched");
        }

        // =========================================================================================
        // Font-stack fetch model (the resolver-tension fix, §5): EnsureFontStackRangeAsync fetches PER
        // INDIVIDUAL FONT NAME. A two-font stack where only the second font's fixture covers the
        // requested codepoint's range still resolves correctly via FontStackResolver afterward (T5,
        // exercised through the GlyphManager wiring rather than a hand-built GlyphCache).
        // =========================================================================================
        [Test]
        public async Task EnsureFontStackRangeAsync_TwoFontStack_FetchesEachNameAndResolvesWithFallback()
        {
            byte[] latinBytes = LoadFixture("0-255.pbf.bytes");
            byte[] arabicBytes = LoadFixture("1536-1791.pbf.bytes");
            var ranges = new Dictionary<(string, int), byte[]>
            {
                [("LatinFont", 0)] = latinBytes,
                [("ArabicFont", 1536)] = arabicBytes,
            };
            var source = TestGlyphSource.FromRanges(ranges);
            var manager = new GlyphManager(source);
            var stack = new FontStack { Names = new[] { "LatinFont", "ArabicFont" } };

            // 'A' (65) -> range 0: LatinFont has it, ArabicFont's range-0 fetch reports absent.
            await manager.EnsureFontStackRangeAsync(stack, (uint)'A');
            // U+0600 (Arabic Number Sign) -> range 1536: ArabicFont has it, LatinFont's range-1536 fetch reports absent.
            await manager.EnsureFontStackRangeAsync(stack, 0x0600u);

            Assert.AreEqual(4, source.FetchCount, "two codepoints x two stack font names = 4 individual fetches");

            FontStackResolver resolver = manager.CreateResolver(stack);

            GlyphResolution latinResolution = resolver.Resolve((uint)'A');
            Assert.IsTrue(latinResolution.Found);
            Assert.AreEqual("LatinFont", latinResolution.ResolvedFontName);

            GlyphResolution arabicResolution = resolver.Resolve(0x0600u);
            Assert.IsTrue(arabicResolution.Found, "the fallback font's glyph must resolve");
            Assert.AreEqual("ArabicFont", arabicResolution.ResolvedFontName);

            Assert.IsTrue(manager.Atlas.TryGetEntry((uint)'A', out _), "LatinFont's glyph must be in the shared atlas");
            Assert.IsTrue(manager.Atlas.TryGetEntry(0x0600u, out _), "ArabicFont's fallback glyph must be in the shared atlas");

            // Repeat: every (name, rangeStart) pair involved is now cached (including the two absent
            // misses) -> zero additional fetches.
            await manager.EnsureFontStackRangeAsync(stack, (uint)'A');
            await manager.EnsureFontStackRangeAsync(stack, 0x0600u);
            Assert.AreEqual(4, source.FetchCount, "every (fontName, rangeStart) pair -- hit or cached-absent -- must not be refetched");
        }

        // =========================================================================================
        // A codepoint absent from EVERY font's fetched range resolves to the defined not-found
        // outcome (§6.3), never a throw, once GlyphManager has ensured the relevant ranges.
        // =========================================================================================
        [Test]
        public async Task EnsureFontStackRangeAsync_CodepointInNoFont_ResolverReturnsNotFound()
        {
            byte[] latinBytes = LoadFixture("0-255.pbf.bytes");
            var ranges = new Dictionary<(string, int), byte[]> { [("LatinFont", 0)] = latinBytes };
            var source = TestGlyphSource.FromRanges(ranges);
            var manager = new GlyphManager(source);
            var stack = new FontStack { Names = new[] { "LatinFont" } };

            await manager.EnsureFontStackRangeAsync(stack, (uint)'A');

            FontStackResolver resolver = manager.CreateResolver(stack);
            GlyphResolution resolution = default;
            Assert.DoesNotThrow(() => resolution = resolver.Resolve(0x1F600u)); // an emoji codepoint, in no ensured range
            Assert.IsFalse(resolution.Found);
        }
    }
}
