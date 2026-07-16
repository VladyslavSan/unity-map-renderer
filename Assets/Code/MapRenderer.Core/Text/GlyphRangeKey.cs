// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Cache key for one decoded glyph-PBF range: the request key string that identified the fetch
    /// (an individual font name, per <see cref="FontStackResolver"/>'s per-font cache lookups — see its
    /// class doc for why this differs from <see cref="FontStack.RequestToken"/>) plus the 256-aligned
    /// range base (<see cref="FontStackResolver.ComputeRangeStart"/>). Mirrors the
    /// <see cref="Geo.TileId"/> value-equality pattern so it drops into a <c>Dictionary</c>/<c>HashSet</c>
    /// without boxing.
    /// </summary>
    public readonly struct GlyphRangeKey : IEquatable<GlyphRangeKey>
    {
        /// <summary>The request key string this range was fetched/cached under (see class doc).</summary>
        public string FontStack { get; init; }

        /// <summary>The 256-aligned range base (e.g. 0, 256, 1536 — matches the glyph-PBF range convention).</summary>
        public int RangeStart { get; init; }

        public bool Equals(GlyphRangeKey other)
            => RangeStart == other.RangeStart && string.Equals(FontStack, other.FontStack, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is GlyphRangeKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (FontStack != null ? StringComparer.Ordinal.GetHashCode(FontStack) : 0);
                h = h * 31 + RangeStart;
                return h;
            }
        }

        public static bool operator ==(GlyphRangeKey a, GlyphRangeKey b) => a.Equals(b);
        public static bool operator !=(GlyphRangeKey a, GlyphRangeKey b) => !a.Equals(b);

        public override string ToString() => $"{FontStack}/{RangeStart}";
    }
}
