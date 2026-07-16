// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// One request to <see cref="ITextShaper.Shape"/>: a source string, the resolved font stack it
    /// should be shaped against (currently unused by <see cref="CodepointTextShaper"/> — carried
    /// through for Slice 4), and the glyph-advance source.
    /// </summary>
    public readonly struct ShapingRequest
    {
        /// <summary>The source text (UTF-16 <see cref="string"/>) to shape, in logical (reading) order.</summary>
        public string Text { get; init; }

        /// <summary>The resolved <c>text-font</c> stack (see <see cref="FontStack"/>).</summary>
        public FontStack FontStack { get; init; }

        /// <summary>Advance lookup, keyed by atlas (presentation-form) codepoint.</summary>
        public IGlyphMetricsProvider Metrics { get; init; }
    }
}
