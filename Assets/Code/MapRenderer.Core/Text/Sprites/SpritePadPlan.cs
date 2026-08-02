// Engine-free: no UnityEngine dependency.
// Construction convention: object initializer with named members.

using System.Collections.Generic;
using Unity.Mathematics;

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// The result of planning a padded repack of a sprite sheet (<c>SpriteSheetPadder.Plan</c>): the
    /// repacked sheet's dimensions, the derived name → <see cref="SpriteEntry"/> index whose content rects
    /// sit at their NEW positions, and the copy instructions that move the pixels there.
    ///
    /// <para>Pure rect maths — no pixels. <c>SpriteSheetComposer.Compose</c> executes the
    /// <see cref="Blits"/> and fills the border around each destination rect.</para>
    /// </summary>
    public sealed class SpritePadPlan
    {
        /// <summary>Repacked sheet dimensions in texels.</summary>
        public int2 Size { get; init; }

        /// <summary>
        /// The derived index: every source name, its content rect relocated, <see cref="SpriteEntry.Padding"/>
        /// set to the border actually laid down (degenerate sprites pass through untouched with
        /// <c>Padding == 0</c>).
        /// </summary>
        public SpriteIndex Index { get; init; }

        /// <summary>One copy instruction per distinct source rect — aliased names share one blit.</summary>
        public IReadOnlyList<SpriteBlit> Blits { get; init; }

        /// <summary>
        /// Texels of border laid down around EVERY blit's destination rect — the width
        /// <c>SpriteSheetComposer</c> fills, and the value every packed <see cref="SpriteEntry.Padding"/>
        /// carries. <c>0</c> on the no-repack fallback plan.
        /// </summary>
        public int Padding { get; init; }
    }
}
