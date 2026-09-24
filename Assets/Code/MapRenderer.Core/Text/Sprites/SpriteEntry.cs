// Not a blittable job type: a parse-time value in SpriteIndex's managed Dictionary that never crosses
// into a NativeArray/Burst job, so it has no blittable-field constraint (contrast GlyphAtlasEntry).

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// One sprite's location + metadata inside a MapLibre sprite sheet, as parsed from the sprite
    /// JSON index (e.g. <c>sprite.json</c> alongside <c>sprite.png</c>). Pixel rect is in the sprite
    /// sheet's own pixel space; <see cref="PixelRatio"/> distinguishes @1x/@2x sheets. The rect is always the
    /// CONTENT rect, never a padded cell; <see cref="Padding"/> is the border around it (0 from
    /// <see cref="SpriteIndex.Parse"/>, the laid-down border after <c>SpriteSheetPadder.Plan</c>).
    /// </summary>
    public readonly struct SpriteEntry
    {
        /// <summary>Left pixel offset of this sprite's CONTENT within the sprite sheet image.</summary>
        public int X { get; init; }

        /// <summary>Top pixel offset of this sprite's CONTENT within the sprite sheet image.</summary>
        public int Y { get; init; }

        /// <summary>Sprite width in pixels.</summary>
        public int Width { get; init; }

        /// <summary>Sprite height in pixels.</summary>
        public int Height { get; init; }

        /// <summary>Device pixel ratio the sheet was rendered at (e.g. 2 for an @2x sheet).</summary>
        public float PixelRatio { get; init; }

        /// <summary>True when the sprite is a signed-distance-field icon (tintable), not a raw bitmap.</summary>
        public bool Sdf { get; init; }

        /// <summary>
        /// Texels of border available on EACH side of the content rect in the sheet this entry indexes.
        /// <c>0</c> on a raw parsed index (no border exists in a published sheet); <c>1</c> after
        /// <c>SpriteSheetPadder</c> has repacked the sheet with a transparent one-texel border.
        /// <c>IconQuadLayout</c> grows the drawn quad by exactly this border so the ramp lands OUTSIDE the
        /// ink — the icon's on-screen ink size is unaffected by it.
        /// </summary>
        public int Padding { get; init; }
    }
}
