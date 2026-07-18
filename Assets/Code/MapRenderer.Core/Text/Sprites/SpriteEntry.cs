// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.
// NOT a blittable job type: this is a parse-time name-keyed lookup value living inside
// MapRenderer.Core.Text.Sprites.SpriteIndex's managed Dictionary — it never crosses into a
// NativeArray/Burst job, so it carries no BLITTABLE-field constraint (contrast GlyphAtlasEntry).

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// One sprite's location + metadata inside a MapLibre sprite sheet, as parsed from the sprite
    /// JSON index (e.g. <c>sprite.json</c> alongside <c>sprite.png</c>). Pixel rect is in the sprite
    /// sheet's own pixel space; <see cref="PixelRatio"/> distinguishes @1x/@2x sheets.
    /// </summary>
    public readonly struct SpriteEntry
    {
        /// <summary>Left pixel offset of this sprite within the sprite sheet image.</summary>
        public int X { get; init; }

        /// <summary>Top pixel offset of this sprite within the sprite sheet image.</summary>
        public int Y { get; init; }

        /// <summary>Sprite width in pixels.</summary>
        public int Width { get; init; }

        /// <summary>Sprite height in pixels.</summary>
        public int Height { get; init; }

        /// <summary>Device pixel ratio the sheet was rendered at (e.g. 2 for an @2x sheet).</summary>
        public float PixelRatio { get; init; }

        /// <summary>True when the sprite is a signed-distance-field icon (tintable), not a raw bitmap.</summary>
        public bool Sdf { get; init; }
    }
}
