// Engine-free: no UnityEngine dependency.

using MapRenderer.Core.Geo;
using MapRenderer.Core.Text.Sprites;
using Unity.Mathematics;

namespace MapRenderer.Core.Style.Fill
{
    /// <summary>
    /// How a <c>fill-pattern</c>'s size is interpreted as the map zooms.
    ///
    /// <para>The Style Spec only expresses <see cref="ScreenRelative"/> — a MapLibre pattern is a sprite
    /// measured in screen pixels, so it keeps its apparent size while the ground beneath it scales.
    /// <see cref="WorldAbsolute"/> is an ENGINE EXTENSION with no spec equivalent: the pattern is pinned to a
    /// real ground size, so it scales with the map like terrain. A stock MapLibre style therefore always
    /// means <see cref="ScreenRelative"/>, and that is the default.</para>
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
    /// Resolves a <c>fill-pattern</c> sprite name against a sheet into the two values the fill shader
    /// samples with: the sprite's pixel <see cref="Resolution.Rect"/> in the sheet, and how many times the
    /// pattern <see cref="RepeatsPerWorldUnit">repeats per world unit</see>.
    ///
    /// <para>Split out of the Unity binding layer because it is pure spec arithmetic over an already-parsed
    /// <see cref="SpriteAtlasView"/> — no material, no texture, no engine type — so it is unit-testable
    /// without the Editor.</para>
    ///
    /// <para>Clean-room: semantics from the public MapLibre Style Spec. No MapLibre source read.</para>
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

            /// <summary>The sprite's LOGICAL size in css pixels (sheet pixels ÷ pixelRatio) — its size as
            /// the style author sees it, independent of whether the sheet is @1x or @2x.
            ///
            /// <para>Deliberately not a repeat count. A count is only meaningful relative to some span, and
            /// the span this used to assume — one tile — is the wrong frame: a tile's world size depends on
            /// ITS zoom, which differs from the display zoom whenever the cover is mixed-zoom or the source
            /// is being overzoomed past its maxzoom (routine — OpenFreeMap stops at z14 while the camera goes
            /// well past it). Keeping the sprite's own size here lets the period be derived from the DISPLAY
            /// zoom alone, which is knowable per frame.</para></summary>
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
        /// Pattern repetitions per WORLD UNIT, for each axis — the value the shader multiplies the mesh's
        /// world-space pattern coordinate by. Equivalently <c>1 / period</c>, where the period is the world
        /// distance one full repetition spans.
        ///
        /// <para><b>There is no tile in this calculation, and that is the point.</b> The previous form
        /// returned repeats across one tile and scaled by <c>2^frac(displayZoom)</c> to correct for the
        /// tile's on-screen magnification — which silently assumed a tile's own zoom is
        /// <c>floor(displayZoom)</c>. That assumption fails in two routine cases: a mixed-zoom cover
        /// (<c>ScreenSpaceLod</c>, the default) and, far more importantly, ANY zoom past the source's
        /// maxzoom, where the same z14 tiles are stretched across display zooms 14→18+ and the correction was
        /// off by up to 16×. Both disappear once the coordinate is world-space and the period is derived from
        /// the display zoom alone.</para>
        ///
        /// <para><b>ScreenRelative</b> (the Style Spec's meaning): the sprite should occupy its own pixel size
        /// on screen, so the period is <c>logicalPixels × metresPerPixel(displayZoom)</c> — continuous in
        /// zoom, hence no stepping and no per-zoom-level snap.</para>
        ///
        /// <para><b>WorldAbsolute</b> (engine extension): the period IS
        /// <paramref name="worldPeriodMetres"/>, independent of zoom entirely.</para>
        ///
        /// <para>What this does NOT address: each tile's pattern still starts at that tile's own origin, so
        /// unless the period divides the tile's span there is a phase step at tile edges. Removing it needs
        /// each tile's GLOBAL origin in the shader — see docs/fill-parity-design.md §6b.</para>
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
                // Metres per screen pixel at this zoom × the sprite's own pixel width = the world distance the
                // sprite should cover to appear at its authored size. GroundResolution is the single source of
                // that conversion (it already backs the camera's pixel↔ground maths).
                : resolution.LogicalSizePixels.x * WebMercator.GroundResolution(displayZoom);

            if (periodX <= 0.0)
                return default;

            double periodY = periodX * resolution.Aspect;
            return new double2(1.0 / periodX, 1.0 / periodY);
        }

        /// <summary>
        /// Resolves <paramref name="patternName"/> against <paramref name="atlas"/>.
        ///
        /// <para>Returns <see langword="false"/> with <see cref="Resolution.Unresolved"/> for every way this
        /// can fail to name a drawable sprite — no pattern declared, no sheet yet (the sheet is fetched
        /// asynchronously and arrives after the layer's material is built), a name absent from the sheet, or
        /// a sprite with a degenerate rect. The caller binds the degenerate rect either way, so the shader
        /// clips: per the Style Spec a fill layer whose pattern cannot be resolved is <b>not painted</b> —
        /// notably it does NOT fall back to <c>fill-color</c>, whose spec default is opaque black.</para>
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

            // The sprite's LOGICAL size: an @2x sheet stores a 16-css-px sprite as 32 sheet px, so the
            // pixelRatio divisor is what keeps a pattern the same on-screen size across sheet densities.
            // Guarded against a malformed sheet declaring pixelRatio 0 (SpriteIndex defaults it to 1, but
            // an explicit "pixelRatio": 0 parses through) — a zero divisor would yield an infinite repeat
            // count and blow up the sampler.
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
