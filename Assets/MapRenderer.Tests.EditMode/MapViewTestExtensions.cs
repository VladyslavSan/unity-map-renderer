using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapView = MapRenderer.Unity.Rendering.Map.MapView;

namespace MapRenderer.Tests
{
    /// <summary>
    /// Test-only accessors that peek at <see cref="MapView"/> internals (granted via
    /// <c>InternalsVisibleTo</c> on MapRenderer.Unity) WITHOUT adding test-support members to MapView's
    /// production API. The test-support surface lives here, in the test assembly, where it belongs.
    ///
    /// Most accessors forward through <see cref="MapView.TileManager"/> (the lifecycle owner) — MapView no
    /// longer mirrors them. Members that were properties on MapView are methods here (extension members
    /// cannot be properties), so the call sites read <c>view.LoadedTileCount()</c> etc.
    /// </summary>
    internal static class MapViewTestExtensions
    {
        /// <summary>Number of fill render bundles MapView built from the style.</summary>
        public static int FillLayerCount(this MapView view) => view.Layers.FillCount;

        /// <summary>Number of line render bundles MapView built from the style.</summary>
        public static int LineLayerCount(this MapView view) => view.Layers.LineCount;

        // ── Tile lifecycle observability (forwarded to the TileManager) ──────────────────────────

        /// <summary>Number of currently loaded (or loading) tiles.</summary>
        public static int LoadedTileCount(this MapView view) => view.TileManager != null ? view.TileManager.LoadedTileCount : 0;

        /// <summary>The scheduler's in-flight fetch count.</summary>
        public static int InFlightCount(this MapView view) => view.TileManager != null ? view.TileManager.InFlightCount : 0;

        /// <summary>Number of tiles released while their tessellation was still in-flight.</summary>
        public static int ReleasedMidFlightCount(this MapView view) => view.TileManager != null ? view.TileManager.ReleasedMidFlightCount : 0;

        /// <summary>True when the tile is loaded AND produced geometry. Backend-agnostic.</summary>
        public static bool TryGetBuiltTile(this MapView view, TileId id) => view.TileManager != null && view.TileManager.TryGetBuiltTile(id);

        /// <summary>The Mesh assets built for a loaded tile (one per rendered layer), or null if not built.</summary>
        public static Mesh[] GetTileMeshes(this MapView view, TileId id) => view.TileManager?.GetTileMeshes(id);

        /// <summary>Scene-space bounds of the live tiles (for framing a snapshot camera). <paramref name="tileSizeWorld"/>
        /// is the tile's world extent at the current zoom.</summary>
        public static Bounds ComputeSceneBounds(this MapView view, float tileSizeWorld)
            => view.TileManager != null ? view.TileManager.ComputeSceneBounds(tileSizeWorld) : default;

        /// <summary>True once every loaded tile has finished building (or is definitively absent).</summary>
        public static bool AllTilesSettled(this MapView view) => view.TileManager == null || view.TileManager.AllTilesSettled();

        /// <summary>Deterministic drain — blocks until all in-flight fetch and tessellation complete, then
        /// consumes their results synchronously. After this returns, <see cref="AllTilesSettled"/> is true.</summary>
        public static void DrainTessellation(this MapView view)
        {
            if (view.Camera == null || view.TileManager == null) return;
            view.TileManager.DrainTessellation(view.Camera.CurrentProperties);
        }

        // ── Backend handles (null unless the matching backend is selected and Initialise has run) ─

        /// <summary>The live BRG renderer; lets tests read instance buffer state without GPU readback.</summary>
        public static BrgTileRenderer BrgRenderer(this MapView view) => view.TileManager?.BrgRenderer;

        /// <summary>The live Entities-Graphics renderer.</summary>
        public static EntitiesTileRenderer EntitiesRenderer(this MapView view) => view.TileManager?.EntitiesRenderer;

        /// <summary>The live GameObject renderer; lets tests read the per-tile GameObject Hierarchy.</summary>
        public static GameObjectTileRenderer GameObjectRenderer(this MapView view) => view.TileManager?.GameObjectRenderer;

        /// <summary>
        /// Assigns the committed production <see cref="MapMaterialSet"/> so the view can build per-layer
        /// materials. Required since S58 retired the <c>Shader.Find</c> fallback — without a config the
        /// factory returns null and <see cref="StyledLayerSet"/> builds zero layers. Returns the view for
        /// chaining: <c>go.AddComponent&lt;MapView&gt;().WithTestMaterials()</c>.
        /// </summary>
        public static MapView WithTestMaterials(this MapView view)
        {
            view.MaterialSet = MapMaterialSetTestUtil.Load();
            return view;
        }
    }
}
