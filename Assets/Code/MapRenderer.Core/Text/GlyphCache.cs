// Engine-free: no UnityEngine dependency.

using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Keep-all-per-session glyph-range cache: a decoded <see cref="FontStackGlyphs"/>
    /// range is fetched once and reused for the rest of the session. No eviction — a basemap uses few
    /// ranges in practice; revisit only if memory shows up.
    /// </summary>
    public sealed class GlyphCache
    {
        private readonly Dictionary<GlyphRangeKey, FontStackGlyphs> _ranges = new Dictionary<GlyphRangeKey, FontStackGlyphs>();

        /// <summary>Looks up a previously stored decoded range by request key + range base.</summary>
        public bool TryGet(string fontStack, int rangeStart, out FontStackGlyphs glyphs)
            => _ranges.TryGetValue(new GlyphRangeKey { FontStack = fontStack, RangeStart = rangeStart }, out glyphs);

        /// <summary>Stores (or overwrites) a decoded range under its request key + range base.</summary>
        public void Store(string fontStack, int rangeStart, FontStackGlyphs glyphs)
            => _ranges[new GlyphRangeKey { FontStack = fontStack, RangeStart = rangeStart }] = glyphs;
    }
}
