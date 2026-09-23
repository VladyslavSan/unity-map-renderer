using UnityEngine;
using MapRenderer.Core.Style;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The seam that builds a sprite <see cref="ISpriteSource"/> from a style document's root
    /// <c>sprite</c> URL (<see cref="StyleDocument.Sprite"/>) — mirrors <see cref="GlyphSourceFactory"/>
    /// for glyphs. DIVERGES from <see cref="GlyphSourceFactory"/> on the missing-URL case: a style with no
    /// <c>sprite</c> is common (icons are optional, unlike glyphs), so a null/absent URL returns
    /// <c>null</c> here rather than throwing — callers treat a null source as "no sprites for this style"
    /// instead of a construction-time failure.
    /// </summary>
    internal static class SpriteSourceFactory
    {
        // Process-wide "warn once" latch. INTERNAL rather than private so a test can clear it in setup:
        // the sprite fetch is not gated on a style having symbol layers (fill-pattern resolves against the
        // same sheet), so this is reachable from many styles, and whether the latch is still unset by the
        // time any one test runs depends on test ORDER. Broadening private → internal is the conventions'
        // sanctioned test footprint; without it a test passes or fails according to what ran before it.
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
