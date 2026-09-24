using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The seam that builds a sprite <see cref="ISpriteSource"/> from a style document's root
    /// <c>sprite</c> URL (<see cref="StyleDocument.Sprite"/>) — mirrors <see cref="GlyphSourceFactory"/>
    /// for glyphs, except that a missing URL returns <c>null</c> instead of throwing. Icons are
    /// optional, so callers treat a null source as "no sprites for this style".
    /// </summary>
    internal static class SpriteSourceFactory
    {
        // Process-wide "warn once" latch. Internal so a test can clear it in setup; otherwise a
        // test's result depends on which styles earlier tests loaded.
        internal static bool WarnedMissingUrl;

        /// <summary>
        /// Builds the sprite source for <paramref name="style"/>'s root <c>sprite</c> URL, or <c>null</c>
        /// if the style has none (logged once, process-wide).
        /// </summary>
        public static ISpriteSource Create(StyleDocument style)
        {
            string spriteUrl = style?.Sprite;
            if (string.IsNullOrEmpty(spriteUrl))
            {
                if (!WarnedMissingUrl)
                {
                    WarnedMissingUrl = true;
                    Debug.LogWarning("[SpriteSourceFactory] style has no 'sprite' URL — icons will not render.");
                }
                return null;
            }

            return new UnityWebRequestSpriteSource(spriteUrl);
        }
    }
}
