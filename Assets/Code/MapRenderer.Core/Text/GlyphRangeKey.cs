// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// Cache key for one decoded glyph-PBF range: the individual font name it was fetched under (see
    /// <see cref="FontStackResolver"/>) plus the 256-aligned range base
    /// (<see cref="FontStackResolver.ComputeRangeStart"/>). It has value equality, like
    /// <see cref="Geo.TileId"/>, so it keys a <c>Dictionary</c>/<c>HashSet</c> without boxing.
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
