// Engine-free: compiled by both the Unity EditMode runner and the fast dotnet core-tests project.

using System.Collections.Generic;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.View;

namespace MapRenderer.Tests
{
    /// <summary>Lets a test read only the cover of a selection, through the selector seam.</summary>
    internal static class VisibleTileSelectorTestExtensions
    {
        /// <summary>Selects, then copies the cover into <paramref name="cover"/>. The preload and keep sets
        /// are dropped.</summary>
        public static void SelectCover(this IVisibleTileSelector selector, in ViewContext view, List<TileId> cover)
        {
            var selection = new TileSelection();
            selector.SelectVisibleTiles(in view, selection);
            cover.Clear();
            cover.AddRange(selection.Cover);
        }
    }
}
