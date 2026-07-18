using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// I4: the seam that builds a sprite <see cref="ISpriteSource"/> from a style document's root
    /// <c>sprite</c> URL (<see cref="StyleDocument.Sprite"/>) — mirrors <see cref="GlyphSourceFactory"/>
    /// for glyphs. DIVERGES from <see cref="GlyphSourceFactory"/> on the missing-URL case: a style with no
    /// <c>sprite</c> is common (icons are optional, unlike glyphs), so a null/absent URL returns
    /// <c>null</c> here rather than throwing — callers treat a null source as "no sprites for this style"
    /// instead of a construction-time failure.
    /// </summary>
    internal static class SpriteSourceFactory
    {
        private static bool _warnedMissingUrl;

        /// <summary>
        /// Builds the sprite source for <paramref name="style"/>'s root <c>sprite</c> URL, or <c>null</c>
        /// if the style has none (logged once, process-wide).
        /// </summary>
        public static ISpriteSource Create(StyleDocument style)
        {
            string spriteUrl = style?.Sprite;
            if (string.IsNullOrEmpty(spriteUrl))
            {
                if (!_warnedMissingUrl)
                {
                    _warnedMissingUrl = true;
                    Debug.LogWarning("[SpriteSourceFactory] style has no 'sprite' URL — icons will not render.");
                }
                return null;
            }

            return new UnityWebRequestSpriteSource(spriteUrl);
        }
    }
}
