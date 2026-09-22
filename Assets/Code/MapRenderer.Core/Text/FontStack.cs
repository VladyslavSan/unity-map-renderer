// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System.Collections.Generic;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// A resolved <c>text-font</c> stack (an ordered list of font names). The clean-room shaper
    /// (<see cref="CodepointTextShaper"/>) does not consult it; advances come from
    /// <see cref="ShapingRequest.Metrics"/> instead. <see cref="RequestToken"/> is the glyph-PBF
    /// <c>{fontstack}</c> fetch key; per-glyph fallback lives in <see cref="FontStackResolver"/>.
    /// </summary>
    public sealed class FontStack
    {
        /// <summary>Ordered font names as declared in the style's <c>text-font</c> array.</summary>
        public IReadOnlyList<string> Names { get; init; }

        /// <summary>
        /// The MapLibre glyph-PBF <c>{fontstack}</c> request token: the ordered font names joined with
        /// <c>", "</c> (comma + space) — confirmed against the committed glyph-PBF fixtures, whose
        /// decoded multi-font <see cref="FontStackGlyphs.Name"/> uses this exact join (e.g.
        /// "Noto Sans Regular, Noto Naskh Arabic Regular, ..."). Not URL-encoded — that is the fetch
        /// layer's job (deferred to the Unity <c>GlyphManager</c>). Computed on demand, not an
        /// <c>init</c> property, so a <c>Names</c>-only object initializer still constructs it.
        /// </summary>
        public string RequestToken => BuildRequestToken(Names);

        /// <summary>Builds the glyph-PBF <c>{fontstack}</c> request token from an ordered font-name list.</summary>
        public static string BuildRequestToken(IReadOnlyList<string> names)
        {
            if (names == null || names.Count == 0) return string.Empty;
            if (names.Count == 1) return names[0] ?? string.Empty;
            return string.Join(", ", names);
        }
    }
}
