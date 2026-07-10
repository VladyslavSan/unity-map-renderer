// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members (see docs/conventions.md).

using Unity.Mathematics;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// A single decoded glyph from a MapLibre glyph-PBF range: metrics + an optional one-channel SDF
    /// bitmap. <see cref="Width"/>/<see cref="Height"/> are the glyph's own metrics — they do NOT
    /// include the SDF buffer/padding border (see <see cref="GlyphSdf.Buffer"/>); the decoded
    /// <see cref="Bitmap"/> is the padded cell, sized <see cref="CellSize"/>.
    /// </summary>
    public readonly struct SdfGlyph
    {
        /// <summary>The Unicode codepoint this glyph represents (the glyph-PBF <c>id</c> field).</summary>
        public uint Codepoint { get; init; }

        /// <summary>Glyph width in pixels, WITHOUT the SDF buffer border.</summary>
        public int Width { get; init; }

        /// <summary>Glyph height in pixels, WITHOUT the SDF buffer border.</summary>
        public int Height { get; init; }

        /// <summary>Left side-bearing in pixels (may be negative).</summary>
        public int Left { get; init; }

        /// <summary>Distance from the baseline to the glyph's top in pixels (may be negative).</summary>
        public int Top { get; init; }

        /// <summary>Horizontal advance in pixels.</summary>
        public int Advance { get; init; }

        /// <summary>
        /// One-channel SDF bitmap, row-major, sized <see cref="CellSize"/> (glyph metrics padded by
        /// <see cref="GlyphSdf.Buffer"/> on every side). <c>null</c> for whitespace/zero-advance glyphs
        /// that carry no bitmap in the PBF.
        /// </summary>
        public byte[] Bitmap { get; init; }

        /// <summary>True when this glyph has a decoded SDF bitmap (false for e.g. space).</summary>
        public bool HasBitmap => Bitmap != null && Bitmap.Length > 0;

        /// <summary>The padded bitmap dimensions: glyph metrics plus the SDF buffer on every side.</summary>
        public int2 CellSize => new int2(Width + 2 * GlyphSdf.Buffer, Height + 2 * GlyphSdf.Buffer);
    }
}
