// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// One content-rect copy instruction for the sprite-sheet repack: copy the <see cref="Width"/> ×
    /// <see cref="Height"/> block at (<see cref="SrcX"/>, <see cref="SrcY"/>) in the decoded source sheet to
    /// (<see cref="DstX"/>, <see cref="DstY"/>) in the repacked one, in top-left-origin sheet-texel space.
    /// The destination is the content position (past the border), never the padded cell's corner.
    /// </summary>
    public readonly struct SpriteBlit
    {
        /// <summary>Left texel offset of the block in the source sheet.</summary>
        public int SrcX { get; init; }

        /// <summary>Top texel offset of the block in the source sheet.</summary>
        public int SrcY { get; init; }

        /// <summary>Left texel offset the block lands at in the repacked sheet.</summary>
        public int DstX { get; init; }

        /// <summary>Top texel offset the block lands at in the repacked sheet.</summary>
        public int DstY { get; init; }

        /// <summary>Block width in texels.</summary>
        public int Width { get; init; }

        /// <summary>Block height in texels.</summary>
        public int Height { get; init; }
    }
}
