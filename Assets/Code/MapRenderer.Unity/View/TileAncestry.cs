// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.

using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.View
{
    /// <summary>The one home for quadtree ancestry of <see cref="TileId"/>s. Zoom, x and y halve per level up.</summary>
    internal static class TileAncestry
    {
        /// <summary>The tile one zoom level up. The world tile has no parent and returns itself,
        /// so a walk must stop at <c>Z == 0</c>.</summary>
        public static TileId Parent(TileId tile)
            => tile.Z == 0 ? tile : new TileId { Z = tile.Z - 1, X = tile.X >> 1, Y = tile.Y >> 1 };

        /// <summary>The number of children a tile has.</summary>
        public const int ChildCount = 4;

        /// <summary>Child <paramref name="index"/> (0 to 3) of <paramref name="tile"/>:
        /// bit 0 picks the east half, bit 1 the south half.</summary>
        public static TileId Child(TileId tile, int index)
            => new TileId { Z = tile.Z + 1, X = tile.X * 2 + (index & 1), Y = tile.Y * 2 + (index >> 1) };

        /// <summary>Every strict ancestor of <paramref name="tile"/>, nearest first, ending at the world tile.
        /// An allocation-free <c>foreach</c> source.</summary>
        public static AncestorWalk Ancestors(TileId tile) => new AncestorWalk(tile);

        /// <summary>The struct enumerator behind <see cref="Ancestors"/>.</summary>
        public struct AncestorWalk
        {
            private TileId _current;

            internal AncestorWalk(TileId tile) => _current = tile;

            public TileId Current => _current;

            public AncestorWalk GetEnumerator() => this;

            public bool MoveNext()
            {
                if (_current.Z == 0) return false;
                _current = Parent(_current);
                return true;
            }
        }

        /// <summary>True iff <paramref name="descendant"/> lies strictly below <paramref name="ancestor"/>:
        /// a tile is never its own ancestor, and siblings are unrelated.</summary>
        public static bool IsStrictAncestor(TileId ancestor, TileId descendant)
        {
            int up = descendant.Z - ancestor.Z;
            return up > 0 && descendant.X >> up == ancestor.X && descendant.Y >> up == ancestor.Y;
        }
    }
}
