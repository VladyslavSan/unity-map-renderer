// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// All decoded glyphs for one font stack's requested codepoint range (one glyph-PBF
    /// <c>fontstack</c> message, e.g. the "Noto Sans Regular, ..." stack's "0-255" range).
    /// </summary>
    public sealed class FontStackGlyphs
    {
        /// <summary>The comma-separated font-stack name as declared in the PBF (e.g. "Noto Sans Regular, ...").</summary>
        public string Name { get; init; }

        /// <summary>Inclusive start codepoint of the requested range (parsed from the PBF "start-end" range string).</summary>
        public int RangeStart { get; init; }

        /// <summary>Inclusive end codepoint of the requested range (parsed from the PBF "start-end" range string).</summary>
        public int RangeEnd { get; init; }

        /// <summary>Decoded glyphs keyed by Unicode codepoint.</summary>
        public IReadOnlyDictionary<uint, SdfGlyph> Glyphs { get; init; }
    }
}
