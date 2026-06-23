using System;
using Unity.Mathematics;
using UnityEngine;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Unity
{
    /// <summary>
    /// Common contract for the GameObject-free, instanced tile-draw backends —
    /// <see cref="BrgTileRenderer"/> (raw BatchRendererGroup) and
    /// <see cref="EntitiesTileRenderer"/> (Entities Graphics). Both register one draw item per
    /// (tile, layer) mesh and are driven by a per-frame <see cref="Rebuild"/> that recomputes the
    /// floating-origin transforms. <c>TileManager</c> holds one of these (or none, on the GameObject
    /// path) and treats them uniformly through this interface; only construction differs.
    /// </summary>
    internal interface IInstancedTileBackend : IDisposable
    {
        /// <summary>
        /// Registers a tile-layer mesh as a draw item. <paramref name="materialIndex"/> indexes the
        /// flattened layer-material list (fills in declared order, then lines). <paramref name="tileId"/>
        /// identifies the owning tile — the <see cref="EntitiesTileRenderer"/> uses it to group a tile's
        /// layers under one named root entity in the Entities Hierarchy (the per-tile debug affordance);
        /// <see cref="BrgTileRenderer"/> has no per-entity hierarchy and ignores it. Returns a handle for
        /// <see cref="RemoveItem"/>.
        /// </summary>
        int AddTileLayer(Mesh mesh, double2 tileOriginMerc, int materialIndex, TileId tileId);

        /// <summary>Removes a previously registered draw item. Idempotent for unknown handles.</summary>
        void RemoveItem(int handle);

        /// <summary>
        /// Per-frame: recompute every draw item's object-to-world from <paramref name="sceneOrigin"/>
        /// (camera-relative floating origin) and refresh backend state for the upcoming render.
        /// </summary>
        void Rebuild(double2 sceneOrigin);

        /// <summary>
        /// XZ scene-space bounding box covering all live tile draw items (each draw item's translation,
        /// plus <paramref name="tileSizeWorld"/> for the tile's mesh extent beyond its origin). Used by
        /// tests to frame a camera that sees all rendered tiles — there are no GameObjects to bound.
        /// Returns <c>default</c> when empty.
        /// </summary>
        Bounds ComputeSceneBounds(float tileSizeWorld);
    }
}
