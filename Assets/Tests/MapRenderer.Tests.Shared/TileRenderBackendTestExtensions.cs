// Unity EditMode and PlayMode: test-assembly helper over the tile backend contract.
//
// Non-obvious why: C# finds extension methods only through the call site's enclosing namespaces, so this file
// uses the parent namespace MapRenderer.Tests, which every test namespace can see.

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Unity.Rendering.Backend;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;

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

        /// <summary>Removes one draw item. Production removes in batches only, so a single removal is a batch of one.</summary>
        internal static void RemoveItem(this ITileRenderBackend backend, int handle)
            => backend.RemoveItems(new[] { handle });

        /// <summary>
        /// XZ scene-space bounding box covering all live tile draw items (each item's translation from the last <c>Rebuild</c>, plus
        /// <paramref name="tileSizeWorld"/> for the tile's mesh extent beyond its origin), to frame a camera on every rendered tile.
        /// Returns a zero-sized box at the origin when empty.
        /// </summary>
        internal static Bounds ComputeSceneBounds(this ITileRenderBackend backend, float tileSizeWorld)
        {
            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minZ = float.MaxValue;
            float maxZ = float.MinValue;
            foreach (int handle in LiveHandles(backend))
            {
                (float x, float z) = TranslationOf(backend, handle);
                if (float.IsNaN(x)) continue;
                if (x < minX) minX = x;
                if (x + tileSizeWorld > maxX) maxX = x + tileSizeWorld;
                if (z < minZ) minZ = z;
                if (z + tileSizeWorld > maxZ) maxZ = z + tileSizeWorld;
            }

            if (minX == float.MaxValue) return new Bounds(Vector3.zero, Vector3.zero);
            return new Bounds(new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f), new Vector3(maxX - minX, 1f, maxZ - minZ));
        }

        private static IEnumerable<int> LiveHandles(ITileRenderBackend backend) => backend switch
        {
            BrgTileRenderer brg            => brg._items.Keys,
            EntitiesTileRenderer entities  => entities._items.Keys,
            GameObjectTileRenderer objects => objects._items.Keys,
            _ => throw new System.ArgumentException($"no handle list for {backend.GetType().Name}"),
        };

        private static (float x, float z) TranslationOf(ITileRenderBackend backend, int handle) => backend switch
        {
            BrgTileRenderer brg            => brg.GetInstanceTranslation(handle),
            EntitiesTileRenderer entities  => entities.GetInstanceTranslation(handle),
            GameObjectTileRenderer objects => objects.GetInstanceTranslation(handle),
            _ => throw new System.ArgumentException($"no translation for {backend.GetType().Name}"),
        };
    }
}
