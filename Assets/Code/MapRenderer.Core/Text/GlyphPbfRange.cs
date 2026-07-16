// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// The decoded result of one glyph-PBF range fetch (<c>{fontstack}/{range}.pbf</c>): a top-level
    /// <c>glyphs</c> message, which is a list of per-font-stack glyph sets (the PBF may carry more than
    /// one stack, though a single-stack request is the common case).
    /// </summary>
    public sealed class GlyphPbfRange
    {
        /// <summary>The decoded font stacks (top-level glyph-PBF <c>stacks</c> field, repeated).</summary>
        public IReadOnlyList<FontStackGlyphs> Stacks { get; init; }
    }
}
