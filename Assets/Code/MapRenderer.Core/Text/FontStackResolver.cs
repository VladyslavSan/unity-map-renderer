// Engine-free: no UnityEngine dependency.

using System;
using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Resolves a codepoint against an ordered <see cref="FontStack"/> with per-glyph fallback: the first
    /// font in <see cref="FontStack.Names"/> order whose range (<c>(codepoint / 256) * 256</c>) holds it
    /// wins, else notdef/skip. It reads the <see cref="GlyphCache"/> per font name, not per joined
    /// <see cref="FontStack.RequestToken"/>, because client-side fallback tries each font in order. It
    /// backs <see cref="ShapingRequest.Metrics"/>.
    /// </summary>
    public sealed class FontStackResolver : IGlyphMetricsProvider
    {
        private readonly FontStack _fontStack;
        private readonly GlyphCache _cache;
        private readonly GlyphAtlas _atlas;

        /// <param name="atlas">The atlas whose font ids stamp <see cref="PositionedGlyph.FontId"/>. Null
        /// only in a metrics-only test, where every id is 0 (wrong for a mixed-face fixture).</param>
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
