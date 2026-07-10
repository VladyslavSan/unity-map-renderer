// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members (see docs/conventions.md).

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The result of <see cref="FontStackResolver.Resolve"/>: the resolved glyph and which font in the
    /// stack it came from, or a defined not-found outcome (S18 §6.3, locked: notdef/skip, never throw).
    /// </summary>
    public readonly struct GlyphResolution
    {
        /// <summary>True when some font in the stack had the requested codepoint.</summary>
        public bool Found { get; init; }

        /// <summary>The resolved glyph. Default (all-zero) when <see cref="Found"/> is false.</summary>
        public SdfGlyph Glyph { get; init; }

        /// <summary>
        /// The name of the first stack font (in <see cref="FontStack.Names"/> order) that had the
        /// glyph. <c>null</c> when <see cref="Found"/> is false.
        /// </summary>
        public string ResolvedFontName { get; init; }

        /// <summary>The defined not-found outcome (§6.3: notdef/skip) — never throw.</summary>
        public static GlyphResolution NotFound() => default;
    }
}
