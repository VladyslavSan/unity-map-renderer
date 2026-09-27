using System;
using System.Collections.Generic;
using UnityEngine;
using MapRenderer.Unity.Style;
using MapRenderer.Unity.View;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The seam that builds one sprite <see cref="ISpriteSource"/> per entry in a style document's root
    /// <c>sprite</c> (<see cref="StyleDocument.Sprites"/>) — mirrors <see cref="GlyphSourceFactory"/> for
    /// glyphs, except a style with no sprite entries returns an empty list instead of throwing. Icons are
    /// optional, so callers treat an empty list as "no sprites for this style". Also picks the <c>@2x</c>
    /// suffix from the device pixel ratio, alike for every entry; <see cref="SpriteSheetSource"/> falls
    /// back to 1x on a 404.
    /// </summary>
    internal static class SpriteSourceFactory
    {
        // Process-wide "warn once" latch. Internal so a test can clear it in setup; otherwise a
        // test's result depends on which styles earlier tests loaded.
        internal static bool WarnedMissingUrl;

        /// <summary>The ratio at or above which the <c>@2x</c> sheet is requested instead of the 1x one.</summary>
        internal const double TwoXThreshold = 1.5;

        /// <summary>
        /// Builds one source per <see cref="StyleDocument.Sprites"/> entry, in declared order — empty if
        /// the style has none (logged once, process-wide). <paramref name="devicePixelRatio"/> selects the
        /// <c>@2x</c> suffix at or above <see cref="TwoXThreshold"/>; an implausible ratio (NaN, or
        /// outside <see cref="DeviceScaling"/>'s band) sanitizes to 1x, the same fallback framing uses.
        /// </summary>
        public static IReadOnlyList<(string Id, ISpriteSource Source)> Create(StyleDocument style, double devicePixelRatio)
        {
            if (style == null || style.Sprites.Count == 0)
            {
                if (!WarnedMissingUrl)
                {
                    WarnedMissingUrl = true;
                    Debug.LogWarning("[SpriteSourceFactory] style has no 'sprite' entries — icons will not render.");
                }
                return Array.Empty<(string, ISpriteSource)>();
            }

            string suffix = DeviceScaling.SafeRatio(devicePixelRatio) >= TwoXThreshold ? "@2x" : "";
            var result = new (string Id, ISpriteSource Source)[style.Sprites.Count];
            for (int i = 0; i < style.Sprites.Count; i++)
            {
                SpriteReference reference = style.Sprites[i];
                result[i] = (reference.Id, new SpriteSheetSource(reference.Url, suffix));
            }
            return result;
        }
    }
}
