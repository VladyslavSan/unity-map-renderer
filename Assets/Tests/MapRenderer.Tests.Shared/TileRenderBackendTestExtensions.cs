// Unity EditMode and PlayMode: test-assembly helper over the tile backend contract.
//
// Non-obvious why: C# finds extension methods only through the call site's enclosing namespaces, so this file
// uses the parent namespace MapRenderer.Tests, which every test namespace can see.

using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Rendering.Backend;

namespace MapRenderer.Tests
{
    internal static class TileRenderBackendTestExtensions
    {
        /// <summary>Registers a tile layer and shows it at once, because <c>AddTileLayer</c> registers hidden.
        /// Returns the draw-item handle.</summary>
        internal static int AddShownTileLayer(
            this ITileRenderBackend backend, Mesh mesh, double3 tileOriginRender, int materialIndex, TileId tileId)
        {
            int handle = backend.AddTileLayer(mesh, tileOriginRender, materialIndex, tileId);
            backend.SetItemsVisible(new[] { handle }, true);
            return handle;
        }
    }
}
