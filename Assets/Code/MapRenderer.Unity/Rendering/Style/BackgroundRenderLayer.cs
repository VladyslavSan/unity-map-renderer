using UnityEngine;
using MapRenderer.Core.Rendering;
using MapRenderer.Unity.Common;
using Background = MapRenderer.Core.Style.Background;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Background <see cref="IRenderLayer"/>: a style's <c>background</c> layer as a runtime render object
    /// (the render-layer model). Epic A / A2: background is now a source-less
    /// per-covered-tile <see cref="RenderLayerBuild.TileMesh"/> layer — <see cref="Tile.Processing.TileBackgroundLayerProcessor"/>
    /// synthesizes one full-tile-extent quad per tile in the camera cover, projected through the same
    /// <see cref="MapRenderer.Core.Geo.IProjection"/> fill/line use, and registered with the active
    /// <see cref="Backend.ITileRenderBackend"/> at this layer's <see cref="DrawIndex"/> — replacing E3's
    /// interim self-owned world-cap quad (one static <see cref="Mesh"/> + a persistent <see cref="GameObject"/>
    /// /<see cref="MeshRenderer"/>, gated to Mercator-only via <c>SetVisible</c>). This layer no longer owns
    /// any geometry/scene object — it owns ONLY the material; the backend owns every per-tile mesh, so the
    /// Mercator-only gate is gone (the globe now renders a correctly curved background across the covered
    /// tile pyramid — design §C).
    ///
    /// <para>Owns: a material clone (a FILL-base clone at its global queue — the fill shader's flat lit path
    /// IS the ground look, <see cref="Materials.MaterialFactory.CreateBackgroundMaterial"/>). <see cref="Persistence"/>
    /// stays <see cref="DrawPersistence.Persistent"/> (the backend redraws each tile's quad every camera
    /// render with no orchestrator, same as fill/line).</para>
    ///
    /// <para>Coplanar y=0 with fills/lines BY DESIGN: every flat layer is ZWrite-off
    /// (<see cref="Materials.BaseTweaker.ApplyBaseContract"/>), so painter order via <c>renderQueue</c> alone
    /// decides the composite.</para>
    /// </summary>
    internal sealed class BackgroundRenderLayer : IRenderLayer
    {
        public MapRenderer.Core.Style.StyleLayer StyleLayer  { get; }
        public RenderLayerBuild                  Build       => RenderLayerBuild.TileMesh;
        public DrawPersistence                   Persistence => DrawPersistence.Persistent;
        public int                               DrawIndex   { get; }
        public LayerSubSlot                      MaterialSubSlot => LayerSubSlot.Base;
        public Material                          Material    { get; } // owned fill-base clone; null iff FillMaterial unassigned (slot kept, never shows)

        private readonly ZoomStyleApplier _applier; // null iff Material null

        private BackgroundRenderLayer(
            MapRenderer.Core.Style.StyleLayer layer, Material material, ZoomStyleApplier applier, int drawIndex)
        {
            StyleLayer = layer;
            Material   = material;
            _applier   = applier;
            DrawIndex  = drawIndex;
        }

        /// <summary>Never returns null (the Symbol Create pattern, <see cref="SymbolRenderLayer.Create"/>):
        /// background always takes its declared slot so the layers above it keep their queues regardless of
        /// material config; unconfigured ⇒ <see cref="Material"/> null (<see cref="Materials.MaterialFactory"/>
        /// warns), no geometry, never shows. Takes no Hierarchy <c>parent</c> — A2: background owns no scene
        /// GameObject any more (the backend owns every per-tile quad), so only the GameObject-bearing symbol
        /// arm of <see cref="RenderLayerFactory"/> forwards one.</summary>
        public static BackgroundRenderLayer Create(
            Background.StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex)
        {
            Material mat = Materials.MaterialFactory.CreateBackgroundMaterial(settings);
            if (mat == null)
                return new BackgroundRenderLayer(layer, null, null, drawIndex);

            Background.PaintProperties paint = layer.Paint;
            var applier = new ZoomStyleApplier(mat);
            Materials.MaterialFactory.BindBackgroundPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(initialZoom);

            return new BackgroundRenderLayer(layer, mat, applier, drawIndex);
        }

        public void ApplyZoom(double zoom) => _applier?.ApplyZoom(zoom); // zoom-expression background-color/opacity

        /// <summary>A2: no GameObject/Mesh to destroy — background owns only its material.</summary>
        public void Dispose()
        {
            Material.DestroySafely();
        }
    }
}
