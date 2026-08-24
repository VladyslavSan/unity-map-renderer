// Engine-free: no UnityEngine dependency.

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// MapLibre <c>text-transform</c>: a case transform applied to the resolved symbol string BEFORE shaping.
    /// <see cref="None"/> (the style-spec default) is the zero value so a stray <c>default(TextTransform)</c>
    /// degrades to "leave the text as-is".
    /// </summary>
    public enum TextTransform
    {
        None = 0,
        Uppercase,
        Lowercase,
    }

    public static class TextTransformExtensions
    {
        /// <summary>
        /// Apply the case transform. Uses invariant-culture casing (deterministic across locales — the
        /// clean-room choice; a Turkish-locale <c>ToUpper</c> would map 'i'→'İ' and make output
        /// machine-dependent). A null string passes through unchanged.
        /// </summary>
        public static string Apply(this TextTransform transform, string text)
        {
            if (text == null) return null;
            return transform switch
            {
                TextTransform.Uppercase => text.ToUpperInvariant(),
                TextTransform.Lowercase => text.ToLowerInvariant(),
                _ => text,
            };
        }
    }
}
