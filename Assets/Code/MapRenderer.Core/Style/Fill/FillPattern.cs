// Engine-free: no UnityEngine dependency.

using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Sprites;
using Unity.Mathematics;

namespace MapRenderer.Core.Style.Fill
{
    /// <summary>
    /// How a <c>fill-pattern</c>'s size is interpreted as the map zooms. The Style Spec only expresses
    /// <see cref="ScreenRelative"/>: a pattern is a sprite measured in screen pixels, so it keeps its apparent
    /// size while the ground scales. <see cref="WorldAbsolute"/> is an ENGINE EXTENSION that pins the pattern
    /// to a real ground size, so it scales with the map like terrain. A stock style always means
    /// <see cref="ScreenRelative"/>, the default.
    /// </summary>
    public enum FillPatternSizing
    {
        /// <summary>MapLibre semantics: the pattern holds a constant SCREEN size across zooms.</summary>
        ScreenRelative = 0,

        /// <summary>Engine extension: the pattern holds a constant WORLD size — its tiling period is a
        /// distance in world units, so it grows on screen as the map zooms in, like something painted on the
        /// ground rather than overlaid on the screen.</summary>
        WorldAbsolute = 1,
    }

    /// <summary>
    /// Resolves a <c>fill-pattern</c> sprite name against a sheet into the two values the fill shader samples
    /// with: the sprite's pixel <see cref="Resolution.Rect"/> in the sheet, and how many times the pattern
    /// <see cref="RepeatsPerWorldUnit">repeats per world unit</see>. It is pure spec arithmetic over a parsed
    /// <see cref="SpriteAtlasView"/>, with no engine type, so it is unit-testable without the Editor.
    /// </summary>
    public static class FillPattern
    {
        /// <summary>
        /// A resolved pattern: where the sprite sits in the sheet, and its tile-space repeat count.
        /// <see cref="Unresolved"/> (a zero-area <see cref="Rect"/>) is the canonical "declared but not
        /// resolvable" state — see <see cref="TryResolve"/>.
        /// </summary>
        public readonly struct Resolution
        {
            /// <summary>xy = sprite top-left in sheet pixels, zw = sprite size in sheet pixels.</summary>
            public double4 Rect { get; init; }

            /// <summary>The sprite's LOGICAL size in css pixels (sheet pixels ÷ pixelRatio), the same for an @1x
            /// or @2x sheet. Not a repeat count: a count needs a span, and a tile is the wrong span, because a
            /// tile's own zoom differs from the display zoom in a mixed-zoom cover or past the source's maxzoom.
            /// The sprite's own size lets the period come from the display zoom alone.</summary>
            public double2 LogicalSizePixels { get; init; }

            /// <summary>The zero-area rect the fill shader reads as "clip this fragment".</summary>
            public static Resolution Unresolved => default;

            /// <summary>True when this resolution names a real, non-degenerate sprite.</summary>
            public bool IsResolved => Rect.z > 0.0 && Rect.w > 0.0;

            /// <summary>Sprite height ÷ width. The perpendicular period scales by this so a non-square
            /// sprite keeps its proportions instead of being squashed.</summary>
            public double Aspect => LogicalSizePixels.y / LogicalSizePixels.x;
        }

        /// <summary>
        /// Pattern repetitions per WORLD UNIT, for each axis: the value the shader multiplies the mesh's
        /// world-space pattern coordinate by, equal to <c>1 / period</c>. There is no tile in this calculation:
        /// a tile's zoom is not <c>floor(displayZoom)</c> in a mixed-zoom cover or past the source's maxzoom.
        /// <b>ScreenRelative</b>: the period is <c>logicalPixels × metresPerPixel(displayZoom)</c>, continuous in
        /// zoom. <b>WorldAbsolute</b>: the period is <paramref name="worldPeriodMetres"/> at every zoom.
        /// <para>Limitation: each tile's pattern starts at that tile's own origin, so unless the period divides
        /// the tile's span there is a phase step at tile edges (docs/fill-parity-design.md).</para>
        /// </summary>
        /// <param name="worldPeriodMetres">
        /// WorldAbsolute only. The tiling period: the world distance one full repetition spans, along the
        /// sprite's width axis. At 1, the pattern coordinate advances by exactly one repetition per world
        /// unit. World units are Web-Mercator metres — the units the mesh, tile origins and camera all use.
        /// A non-positive value falls back to ScreenRelative rather than dividing by zero.
        /// </param>
        public static double2 RepeatsPerWorldUnit(
            in Resolution resolution, FillPatternSizing sizing, double displayZoom, double worldPeriodMetres)
        {
            if (!resolution.IsResolved)
                return default;

            double periodX = sizing == FillPatternSizing.WorldAbsolute && worldPeriodMetres > 0.0
                ? worldPeriodMetres
                // Metres per screen pixel × the sprite's pixel width = the world distance it covers at its authored
                // size. GroundResolution is the single source of that conversion.
                : resolution.LogicalSizePixels.x * WebMercator.GroundResolution(displayZoom);

            if (periodX <= 0.0)
                return default;

            double periodY = periodX * resolution.Aspect;
            return new double2(1.0 / periodX, 1.0 / periodY);
        }

        /// <summary>
        /// Resolves <paramref name="patternName"/> against <paramref name="atlas"/>. Returns
        /// <see langword="false"/> with <see cref="Resolution.Unresolved"/> for no pattern, no sheet yet (it
        /// arrives asynchronously, after the material), a name absent from the sheet, or a degenerate rect. The
        /// caller binds the rect either way, so the shader clips: per the Style Spec an unresolved pattern is
        /// NOT painted, and does not fall back to <c>fill-color</c> (spec default: opaque black).
        /// </summary>
        public static bool TryResolve(string patternName, SpriteAtlasView atlas, out Resolution resolution)
        {
            resolution = Resolution.Unresolved;

            if (string.IsNullOrEmpty(patternName) || atlas?.Index == null)
                return false;
            if (!atlas.Index.TryGetSprite(patternName, out SpriteEntry sprite))
                return false;
            if (sprite.Width <= 0 || sprite.Height <= 0)
                return false;

            // An @2x sheet stores a 16-css-px sprite as 32 sheet px; the pixelRatio divisor keeps the screen
            // size. An explicit "pixelRatio": 0 parses through, and would give an infinite repeat count.
            double pixelRatio = sprite.PixelRatio > 0f ? sprite.PixelRatio : 1.0;
            double logicalWidth  = sprite.Width  / pixelRatio;
            double logicalHeight = sprite.Height / pixelRatio;

            resolution = new Resolution
            {
                Rect              = new double4(sprite.X, sprite.Y, sprite.Width, sprite.Height),
                LogicalSizePixels = new double2(logicalWidth, logicalHeight),
            };
            return true;
        }
    }
}
