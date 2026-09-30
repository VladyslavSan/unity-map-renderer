// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.

using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>The one home for quadtree ancestry of <see cref="TileId"/>s. Zoom, x and y halve per level up.</summary>
    internal static class TileAncestry
    {
        /// <summary>The tile one zoom level up. The world tile has no parent and returns itself, so a walk must stop at <c>Z == 0</c>.</summary>
        public static TileId Parent(TileId tile)
            => tile.Z == 0 ? tile : new TileId { Z = tile.Z - 1, X = tile.X >> 1, Y = tile.Y >> 1 };

        /// <summary>True iff <paramref name="descendant"/> lies strictly below <paramref name="ancestor"/>: a tile is never its own ancestor, and siblings are unrelated.</summary>
        public static bool IsStrictAncestor(TileId ancestor, TileId descendant)
        {
            int up = descendant.Z - ancestor.Z;
            return up > 0 && descendant.X >> up == ancestor.X && descendant.Y >> up == ancestor.Y;
        }
    }
}
