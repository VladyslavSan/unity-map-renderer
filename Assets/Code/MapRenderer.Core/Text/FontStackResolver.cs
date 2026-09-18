// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Resolves a codepoint against an ordered <see cref="FontStack"/>: per-glyph fallback across the
    /// stack (T5) — the first font (in <see cref="FontStack.Names"/> order) whose decoded range
    /// contains the codepoint wins; a codepoint present in no font's range yields the defined
    /// not-found outcome (§6.3: notdef/skip, never throw). Range math is
    /// <c>rangeStart = (codepoint / 256) * 256</c>, the glyph-PBF 256-codepoint range convention.
    ///
    /// <para>
    /// Testable without a live fetch: takes a <see cref="GlyphCache"/> of already-decoded ranges as
    /// input; the actual fetch wiring (populating that cache from <see cref="IGlyphSource"/>) is the
    /// deferred Unity <c>GlyphManager</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Cache-key note:</b> <see cref="FontStack.RequestToken"/> is the joined-whole-stack glyph-PBF
    /// fetch key (the real MapLibre wire convention — a single glyph host may merge several fonts'
    /// glyphs server-side into one PBF keyed by the full joined stack; the committed fixtures decode
    /// this way). This resolver instead looks up the cache **per individual font name** in
    /// <see cref="FontStack.Names"/>, because per-glyph client-side fallback requires each font's own
    /// decoded glyphs to try in order — a self-hosted glyph directory that only serves single-font
    /// ranges (not arbitrary joined combinations) needs exactly this. Bridging "one fetch per joined
    /// stack" vs. "one fetch per individual font, resolved client-side" is a decision for the deferred
    /// fetch-wiring layer (Unity <c>GlyphManager</c>), not this resolver.
    /// </para>
    ///
    /// <para>
    /// Also implements <see cref="IGlyphMetricsProvider"/> so a resolver instance can back a
    /// <see cref="ShapingRequest.Metrics"/> directly (fallback-aware advances for the shaper), without
    /// coupling this class to shaping beyond that one small delegation.
    /// </para>
    /// </summary>
    public sealed class FontStackResolver : IGlyphMetricsProvider
    {
        private readonly FontStack _fontStack;
        private readonly GlyphCache _cache;
        private readonly GlyphAtlas _atlas;

        /// <param name="atlas">The atlas whose font-id table stamps <see cref="PositionedGlyph.FontId"/>.
        /// Null only for a metrics-only test: every id then resolves to 0, which is correct for a
        /// single-face fixture and wrong for a mixed one — production passes the real atlas
        /// (<c>GlyphManager.CreateResolver</c>).</param>
        public FontStackResolver(FontStack fontStack, GlyphCache cache, GlyphAtlas atlas = null)
        {
            _fontStack = fontStack ?? throw new ArgumentNullException(nameof(fontStack));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _atlas = atlas;
        }

        /// <summary>The 256-aligned range base containing <paramref name="codepoint"/>.</summary>
        public static int ComputeRangeStart(uint codepoint) => (int)((codepoint / 256u) * 256u);

        /// <summary>
        /// Resolves <paramref name="codepoint"/> against the stack in order, returning the first font's
        /// glyph that has it, or the defined not-found outcome (never throws).
        /// </summary>
        public GlyphResolution Resolve(uint codepoint)
        {
            int rangeStart = ComputeRangeStart(codepoint);
            IReadOnlyList<string> names = _fontStack.Names;
            if (names != null)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    string fontName = names[i];
                    if (fontName == null) continue;

                    if (_cache.TryGet(fontName, rangeStart, out FontStackGlyphs range)
                        && range.Glyphs != null
                        && range.Glyphs.TryGetValue(codepoint, out SdfGlyph glyph))
                    {
                        return new GlyphResolution
                        {
                            Found = true,
                            Glyph = glyph,
                            ResolvedFontName = fontName,
                        };
                    }
                }
            }

            return GlyphResolution.NotFound();
        }

        /// <summary>
        /// <see cref="IGlyphMetricsProvider"/>: the fallback-resolved glyph's advance AND the atlas id of
        /// the face that answered, or false (advance 0, id 0) when the codepoint is not found anywhere in
        /// the stack.
        /// </summary>
        public bool TryResolveGlyph(uint codepoint, out float advance, out int fontId)
        {
            GlyphResolution resolution = Resolve(codepoint);
            advance = resolution.Found ? resolution.Glyph.Advance : 0f;
            fontId  = resolution.Found && _atlas != null ? _atlas.FontId(resolution.ResolvedFontName) : 0;
            return resolution.Found;
        }
    }
}
