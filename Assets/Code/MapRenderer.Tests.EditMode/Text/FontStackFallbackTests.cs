// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/Code/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using MapRenderer.Core.Text;

namespace MapRenderer.Tests.Text
{
    /// <summary>
    /// S18 Slice 4 — T5: font-stack fallback. <see cref="FontStackResolver"/> resolves a codepoint
    /// against an ordered <see cref="FontStack"/>, trying each font's decoded range (via
    /// <see cref="GlyphCache"/>) in order; the first font that has the codepoint wins. A codepoint in
    /// no font of the stack yields the defined not-found outcome (§6.3: notdef/skip, never throw).
    /// </summary>
    [TestFixture]
    public class FontStackFallbackTests
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

        private static FontStackGlyphs BuildHandGlyphs(string name, int rangeStart, int rangeEnd, uint codepoint, int advance)
        {
            var glyphs = new Dictionary<uint, SdfGlyph>
            {
                [codepoint] = new SdfGlyph
                {
                    Codepoint = codepoint,
                    Width = 1,
                    Height = 1,
                    Left = 0,
                    Top = 0,
                    Advance = advance,
                    Bitmap = null,
                },
            };
            return new FontStackGlyphs { Name = name, RangeStart = rangeStart, RangeEnd = rangeEnd, Glyphs = glyphs };
        }

        // =========================================================================================
        // Real-fixture fallback direction: a codepoint present ONLY in the second font's decoded
        // range (the first font has no decoded range at all covering this codepoint's bucket) must
        // resolve from the second font.
        // =========================================================================================
        [Test]
        public void Resolve_CodepointMissingFromFirstFont_ResolvesFromSecondFont()
        {
            FontStackGlyphs latin = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];
            FontStackGlyphs arabic = GlyphPbfDecoder.Decode(LoadFixture("1536-1791.pbf.bytes")).Stacks[0];

            var cache = new GlyphCache();
            cache.Store("LatinFont", latin.RangeStart, latin);     // only covers range 0 (bucket for codepoint 1536 is 1536)
            cache.Store("ArabicFont", arabic.RangeStart, arabic);  // covers range 1536

            var stack = new FontStack { Names = new[] { "LatinFont", "ArabicFont" } };
            var resolver = new FontStackResolver(stack, cache);

            // U+0600 (Arabic Number Sign) — present in the Arabic fixture, absent from the Latin one,
            // and the Latin font's cache has no decoded range at all for this codepoint's bucket.
            GlyphResolution resolution = resolver.Resolve(0x0600u);

            Assert.IsTrue(resolution.Found, "codepoint present in the fallback font must resolve");
            Assert.AreEqual("ArabicFont", resolution.ResolvedFontName, "must resolve from the SECOND (fallback) font, not the first");
            Assert.AreEqual(0x0600u, resolution.Glyph.Codepoint);
        }

        // =========================================================================================
        // Real-fixture: a codepoint present in the FIRST font resolves from the first font. (The
        // second font's cache has no entry at all for this codepoint's range bucket, so a resolver
        // that incorrectly skipped straight to fallback would return not-found here, not "LatinFont".)
        // =========================================================================================
        [Test]
        public void Resolve_CodepointPresentInFirstFont_ResolvesFromFirstFont()
        {
            FontStackGlyphs latin = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];

            var cache = new GlyphCache();
            cache.Store("LatinFont", latin.RangeStart, latin);
            // Deliberately: no cache entry for "ArabicFont" at range 0 — fallback has nothing to offer here.

            var stack = new FontStack { Names = new[] { "LatinFont", "ArabicFont" } };
            var resolver = new FontStackResolver(stack, cache);

            GlyphResolution resolution = resolver.Resolve((uint)'A');

            Assert.IsTrue(resolution.Found);
            Assert.AreEqual("LatinFont", resolution.ResolvedFontName);
            Assert.AreEqual((uint)'A', resolution.Glyph.Codepoint);
        }

        // =========================================================================================
        // Priority discriminator (the case a disjoint-fixture test cannot exercise): the SAME
        // codepoint exists in BOTH fonts at the same range, with distinguishable metrics. Only stack
        // order — first-font-wins — can be correct; a resolver that iterated in reverse, or picked
        // "any" match, would return Font2's advance here.
        // =========================================================================================
        [Test]
        public void Resolve_CodepointPresentInBothFonts_FirstFontInStackWins()
        {
            FontStackGlyphs font1Range0 = BuildHandGlyphs("Font1", 0, 255, 65u, advance: 11);
            FontStackGlyphs font2Range0 = BuildHandGlyphs("Font2", 0, 255, 65u, advance: 21);

            var cache = new GlyphCache();
            cache.Store("Font1", 0, font1Range0);
            cache.Store("Font2", 0, font2Range0);

            var stack = new FontStack { Names = new[] { "Font1", "Font2" } };
            var resolver = new FontStackResolver(stack, cache);

            GlyphResolution resolution = resolver.Resolve(65u);

            Assert.IsTrue(resolution.Found);
            Assert.AreEqual("Font1", resolution.ResolvedFontName, "the FIRST font in the stack must win, not the last/any match");
            Assert.AreEqual(11, resolution.Glyph.Advance, "the resolved glyph must be Font1's (advance 11), not Font2's (advance 21)");
        }

        // =========================================================================================
        // §6.3 locked policy: a codepoint absent from every font in the stack is a defined not-found
        // outcome (notdef/skip) — never a throw.
        // =========================================================================================
        [Test]
        public void Resolve_CodepointInNoFont_ReturnsNotFound_NeverThrows()
        {
            FontStackGlyphs latin = GlyphPbfDecoder.Decode(LoadFixture("0-255.pbf.bytes")).Stacks[0];
            FontStackGlyphs arabic = GlyphPbfDecoder.Decode(LoadFixture("1536-1791.pbf.bytes")).Stacks[0];

            var cache = new GlyphCache();
            cache.Store("LatinFont", latin.RangeStart, latin);
            cache.Store("ArabicFont", arabic.RangeStart, arabic);

            var stack = new FontStack { Names = new[] { "LatinFont", "ArabicFont" } };
            var resolver = new FontStackResolver(stack, cache);

            // codepoint 5000 -> bucket 4864, a range neither font's cache has anything for.
            GlyphResolution resolution = default;
            Assert.DoesNotThrow(() => resolution = resolver.Resolve(5000u));

            Assert.IsFalse(resolution.Found);
            Assert.IsNull(resolution.ResolvedFontName);
        }

        // =========================================================================================
        // Range math: rangeStart = (codepoint / 256) * 256 — the glyph-PBF 256-codepoint bucketing.
        // =========================================================================================
        [Test]
        public void ComputeRangeStart_Buckets256AlignedBase()
        {
            Assert.AreEqual(0, FontStackResolver.ComputeRangeStart(65u), "'A' (65) is in the 0-255 range");
            Assert.AreEqual(256, FontStackResolver.ComputeRangeStart(300u), "300 is in the 256-511 range");
            Assert.AreEqual(1536, FontStackResolver.ComputeRangeStart(1541u), "1541 is in the 1536-1791 range");
        }

        // =========================================================================================
        // IGlyphMetricsProvider delegation: a resolver plugs directly into ShapingRequest.Metrics,
        // with the same fallback-aware resolution (0 / false for a not-found codepoint).
        // =========================================================================================
        [Test]
        public void TryGetAdvance_DelegatesToFallbackAwareResolve()
        {
            FontStackGlyphs font1Range0 = BuildHandGlyphs("Font1", 0, 255, 65u, advance: 11);
            var cache = new GlyphCache();
            cache.Store("Font1", 0, font1Range0);

            var stack = new FontStack { Names = new[] { "Font1" } };
            IGlyphMetricsProvider metrics = new FontStackResolver(stack, cache);

            Assert.IsTrue(metrics.TryGetAdvance(65u, out float advance));
            Assert.AreEqual(11f, advance);

            Assert.IsFalse(metrics.TryGetAdvance(66u, out float missingAdvance));
            Assert.AreEqual(0f, missingAdvance);
        }
    }
}
