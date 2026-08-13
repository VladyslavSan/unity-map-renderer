// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using Unity.Mathematics;

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// I3 — the read-only sprite-sheet view <c>SymbolFeatureExtractor</c> consumes to
    /// resolve <c>icon-image</c> names to sheet rects and lay out icon quads: the icon analogue of
    /// <see cref="IGlyphAtlasView"/>. Unlike the glyph atlas (which grows/packs at runtime), a sprite sheet
    /// is a single pre-baked image decoded once (I4), so this is a plain carrier over an already-parsed
    /// <see cref="SpriteIndex"/> plus the sheet's pixel dimensions — no packer/grow machinery.
    /// </summary>
    public sealed class SpriteAtlasView
    {
        /// <summary>The parsed name → <see cref="SpriteEntry"/> lookup for this sheet.</summary>
        public SpriteIndex Index { get; init; }

        /// <summary>Sheet pixel dimensions — the UV-normalization denominator (mirrors <see cref="IGlyphAtlasView.Size"/>).</summary>
        public int2 Size { get; init; }
    }
}
