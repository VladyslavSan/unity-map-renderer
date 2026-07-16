using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;
using MapRenderer.Core.Style;
using MapRenderer.Core.View;
using MapRenderer.Core.View.Camera;
using BrgTileRenderer = MapRenderer.Unity.Rendering.Backend.BRG.TileRenderer;
using EntitiesTileRenderer = MapRenderer.Unity.Rendering.Backend.Entities.TileRenderer;
using GameObjectTileRenderer = MapRenderer.Unity.Rendering.Backend.GameObjects.TileRenderer;
using MapRenderer.Unity.Rendering.Materials;
using MapRenderer.Unity.Rendering.Style;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using MapView = MapRenderer.Unity.Rendering.Map.MapView;
using MapViewComponent = MapRenderer.Unity.Rendering.Map.MapViewComponent;
using MapCamera = MapRenderer.Unity.Rendering.Map.MapCamera;

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
        // ── Test camera rig ─────────────────────────────────────────────────────────────────────
        // MapCamera wraps a NON-NULL UnityEngine.Camera and the camera IS the viewport, so a headless
        // MapView test needs a real camera with a deterministic pixel size. This wires an offscreen,
        // never-rendered camera whose square RenderTexture gives ViewportPx = (px, px) — reproducing the
        // old `FallbackAspect = 1f` square viewport so tile selection is unchanged. The camera is parented
        // to the view (destroyed with it); the RenderTexture is shared (the camera never renders to it).
        private static RenderTexture _testViewportRt;

        /// <summary>Wire a deterministic square (px×px) offscreen camera into <paramref name="view"/> so its
        /// tile loop has a non-null camera / viewport. Chains after <c>AddComponent&lt;MapView&gt;()</c>.</summary>
        public static MapViewComponent WithTestCamera(this MapViewComponent view, int px = 1080)
        {
            if (_testViewportRt == null || _testViewportRt.width != px)
                _testViewportRt = new RenderTexture(px, px, 0);

            var camGo = new GameObject("TestViewportCamera");
            camGo.transform.SetParent(view.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.enabled       = false;             // never renders — only supplies a deterministic ViewportPx
            cam.targetTexture = _testViewportRt;

            view.SetCamera(new MapCamera(cam, CameraProperties.Default));
            return view;
        }

        /// <summary>
        /// Test-only: wire an explicit data source + style synchronously, without <see cref="MapView.SetStyle"/>'s
        /// async style/TileJSON fetch. Builds the layers, then drives the SAME production
        /// <see cref="TileManager.SetSources"/> entry MapView.SetStyle uses — pointing every rendered source-id
        /// at the one injected <paramref name="source"/>. No test-only seam on production TileManager. Requires
        /// the camera to be wired first (<see cref="WithTestCamera"/>).
        /// </summary>
        internal static void LoadTestStyle(this MapViewComponent view, IDataSource source,
            CameraProperties initialView, StyleDocument style = null)
        {
            MapView mv = view.View;
            mv.Camera.SetProperties(initialView);
            mv.Camera.SyncToCamera(); // production commits in LateUpdate; tests drive it explicitly at the seed
            mv.Layers.Build(style, mv.Camera.CurrentProperties.Zoom, view.Config.MaterialSet);

            // One SourceSpec per distinct rendered (fill/line/symbol) source-id, each creating the injected
            // source. Zoom range left wide-open (cover is already zoom-clamped by the selector). Key is
            // irrelevant — tests wire once, never restyle-diff.
            var specs = new List<TileManager.SourceSpec>();
            var seen  = new HashSet<string>();
            var layers = mv.Layers.Layers;
            // Epic A / A2: mirrors the production skip (MapView.BuildSourceSpecs, post-A2) — only a layer
            // with a non-empty StyleLayer.Source fetches; background is source-less by design (no Build-kind
            // check any more — ViewGeometry is REMOVED).
            for (int i = 0; i < layers.Count; i++)
                if (!string.IsNullOrEmpty(layers[i].StyleLayer?.Source)) AddSpec(layers[i].StyleLayer.Source);
            mv.TileManager.SetSources(specs, view.Config.Backend);

            void AddSpec(string sid)
            {
                sid ??= string.Empty;
                // Epic A / A7: the raised seam — wrap the injected byte source into the MVT feature source,
                // mirroring MapView.BuildSourceSpecs' production wrap.
                if (seen.Add(sid))
                    specs.Add(new TileManager.SourceSpec(
                        sid, default, 0, int.MaxValue, () => new MvtTileFeatureSource(source)));
            }
        }

        /// <summary>Number of fill render layers MapView built from the style. Counts by Core style type over
        /// the one ordered render-layer list — the production type carries no fill/line discriminator (the
        /// unification's point), so the fill/line split lives here in the test assembly.</summary>
        public static int FillLayerCount(this MapViewComponent view)
            => CountLayersOfType<MapRenderer.Core.Style.Fill.StyleLayer>(view);

        /// <summary>Number of line render layers MapView built from the style (see <see cref="FillLayerCount"/>).</summary>
        public static int LineLayerCount(this MapViewComponent view)
            => CountLayersOfType<MapRenderer.Core.Style.Line.StyleLayer>(view);

        private static int CountLayersOfType<T>(MapViewComponent view)
        {
            var layers = view.Layers?.Layers;
            if (layers == null) return 0;
            int n = 0;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].StyleLayer is T) n++;
            return n;
        }

        // ── Tile lifecycle observability (forwarded to the TileManager) ──────────────────────────

        /// <summary>Number of currently loaded (or loading) tiles.</summary>
        public static int LoadedTileCount(this MapViewComponent view) => view.TileManager != null ? view.TileManager.LoadedTileCount : 0;

        /// <summary>The scheduler's in-flight fetch count.</summary>
        public static int InFlightCount(this MapViewComponent view) => view.TileManager != null ? view.TileManager.InFlightCount : 0;

        /// <summary>Number of tiles released while their mesh build was still in-flight.</summary>
        public static int ReleasedMidFlightCount(this MapViewComponent view) => view.TileManager != null ? view.TileManager.ReleasedMidFlightCount : 0;

        /// <summary>S84: number of tiles released while their FETCH was still in-flight.</summary>
        public static int ReleasedMidFetchCount(this MapViewComponent view) => view.TileManager != null ? view.TileManager.ReleasedMidFetchCount : 0;

        /// <summary>S95: number of times the FULL cover recompute (select descent + request/release diff)
        /// actually ran in the most recent Tick — 0 on an early-out Tick, else 1. Sum across N sub-tile
        /// camera nudges to discriminate "recomputed every dirty tick" (today's behaviour) from a future
        /// throttle.</summary>
        public static int CoverRecomputesLastTick(this MapViewComponent view) => view.TileManager != null ? view.TileManager.CoverRecomputesLastTick : 0;

        /// <summary>S55: mesh build kicks issued in the most recent Tick.</summary>
        public static int MeshBuildsKickedLastTick(this MapViewComponent view) => view.TileManager != null ? view.TileManager.MeshBuildsKickedLastTick : 0;
        /// <summary>S55: sum of vertex counts consumed in the most recent Tick.</summary>
        public static int VerticesConsumedLastTick(this MapViewComponent view) => view.TileManager != null ? view.TileManager.VerticesConsumedLastTick : 0;
        /// <summary>S55/S87: number of tiles that reached Built (fully consumed) in the most recent Tick.</summary>
        public static int TilesConsumedLastTick(this MapViewComponent view) => view.TileManager != null ? view.TileManager.TilesConsumedLastTick : 0;
        /// <summary>S87: number of layer MESHES uploaded + registered in the most recent Tick (the per-frame mesh-count budget observable).</summary>
        public static int MeshesConsumedLastTick(this MapViewComponent view) => view.TileManager != null ? view.TileManager.MeshesConsumedLastTick : 0;

        /// <summary>Stall #2: (tile, source) records fully released in the most recent Tick's DrainReleaseQueue.</summary>
        public static int TilesReleasedLastTick(this MapViewComponent view) => view.TileManager != null ? view.TileManager.TilesReleasedLastTick : 0;
        /// <summary>Stall #2: current deferred-release backlog depth (records that left cover and await drain).</summary>
        public static int ReleaseQueueDepth(this MapViewComponent view) => view.TileManager != null ? view.TileManager.ReleaseQueueDepth : 0;
        /// <summary>Stall #2: batched DestroyEntity structural changes in the Entities backend's LAST RemoveItems call (0 or 1); 0 if not on the Entities backend.</summary>
        public static int DestroyEntityBatchesLastRemove(this MapViewComponent view) => view.TileManager?.EntitiesRenderer != null ? view.TileManager.EntitiesRenderer.DestroyEntityBatchesLastRemove : 0;
        /// <summary>Stall #2: entities destroyed by the Entities backend's last RemoveItems batch (layers + any emptied root).</summary>
        public static int EntitiesDestroyedLastRemove(this MapViewComponent view) => view.TileManager?.EntitiesRenderer != null ? view.TileManager.EntitiesRenderer.EntitiesDestroyedLastRemove : 0;
        /// <summary>Stall #3: live EG-registered meshes on the Entities backend (inc per AddTileLayer, dec per remove); -1 if not on the Entities backend.</summary>
        public static int RegisteredMeshCount(this MapViewComponent view) => view.TileManager?.EntitiesRenderer != null ? view.TileManager.EntitiesRenderer.RegisteredMeshCount : -1;

        /// <summary>S82: cumulative PreparedTileCache hit count (a revisit/style-toggle that skipped
        /// decode/build/upload).</summary>
        public static int PreparedCacheHits(this MapViewComponent view) => view.TileManager != null ? view.TileManager.PreparedCacheHits : 0;
        /// <summary>S82: cumulative PreparedTileCache miss count (a cover-entry that genuinely re-prepared).</summary>
        public static int PreparedCacheMisses(this MapViewComponent view) => view.TileManager != null ? view.TileManager.PreparedCacheMisses : 0;

        /// <summary>S85: the pull-based tile/render telemetry snapshot.</summary>
        public static TileTelemetrySnapshot CaptureTelemetry(this MapViewComponent view)
            => view.TileManager != null ? view.TileManager.CaptureTelemetry() : default;

        /// <summary>True when the tile is loaded AND produced geometry. Backend-agnostic.</summary>
        public static bool TryGetBuiltTile(this MapViewComponent view, TileId id) => view.TileManager != null && view.TileManager.TryGetBuiltTile(id);

        /// <summary>The Mesh assets built for a loaded tile (one per rendered layer), or null if not built.</summary>
        public static Mesh[] GetTileMeshes(this MapViewComponent view, TileId id) => view.TileManager?.GetTileMeshes(id);

        /// <summary>Scene-space bounds of the live tiles (for framing a snapshot camera). <paramref name="tileSizeWorld"/>
        /// is the tile's world extent at the current zoom.</summary>
        public static Bounds ComputeSceneBounds(this MapViewComponent view, float tileSizeWorld)
            => view.TileManager != null ? view.TileManager.ComputeSceneBounds(tileSizeWorld) : default;

        /// <summary>True once every loaded tile has finished building (or is definitively absent).</summary>
        public static bool AllTilesSettled(this MapViewComponent view) => view.TileManager == null || view.TileManager.AllTilesSettled();

        /// <summary>Deterministic drain — blocks until all in-flight fetch and mesh build complete, then
        /// consumes their results synchronously. After this returns, <see cref="AllTilesSettled"/> is true.</summary>
        public static void DrainMeshBuilds(this MapViewComponent view)
        {
            if (view.Camera == null || view.TileManager == null) return;
            view.TileManager.DrainMeshBuilds(view.Camera.CurrentProperties);
        }

        // ── Backend handles (null unless the matching backend is selected and Initialise has run) ─

        /// <summary>The live BRG renderer; lets tests read instance buffer state without GPU readback.</summary>
        public static BrgTileRenderer BrgRenderer(this MapViewComponent view) => view.TileManager?.BrgRenderer;

        /// <summary>The live Entities-Graphics renderer.</summary>
        public static EntitiesTileRenderer EntitiesRenderer(this MapViewComponent view) => view.TileManager?.EntitiesRenderer;

        /// <summary>The live GameObject renderer; lets tests read the per-tile GameObject Hierarchy.</summary>
        public static GameObjectTileRenderer GameObjectRenderer(this MapViewComponent view) => view.TileManager?.GameObjectRenderer;

        /// <summary>
        /// Assigns the committed production <see cref="MapMaterialSet"/> so the view can build per-layer
        /// materials. Required since S58 retired the <c>Shader.Find</c> fallback — without a config the
        /// factory returns null and <see cref="RenderLayerSet"/> builds zero layers. Returns the view for
        /// chaining: <c>go.AddComponent&lt;MapView&gt;().WithTestMaterials()</c>.
        /// </summary>
        public static MapViewComponent WithTestMaterials(this MapViewComponent view)
        {
            view.Config.MaterialSet = MapMaterialSetTestUtil.Load();
            return view;
        }
    }
}
