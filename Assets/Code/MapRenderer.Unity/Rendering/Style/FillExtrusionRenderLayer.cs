using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Rendering;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Fill;
using MapRenderer.Jobs.Geometry;
using FillExtrusion = MapRenderer.Core.Style.FillExtrusion;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Rendering.Tile.Processing;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Fill-extrusion <see cref="ITileMeshRenderLayer"/>: a MapLibre <c>fill-extrusion</c> layer as a
    /// runtime render object.
    ///
    /// <para><b>S23 I2b — roof + wall mesh, dedicated shader.</b> Replaces I1's flat 2D placeholder
    /// (which reused <see cref="Meshing.StyledFillTileBuilder"/> and the FILL base material):
    /// <see cref="BuildGraphRequest"/> now delegates to <see cref="Meshing.StyledFillExtrusionTileBuilder"/>
    /// (roof cap + side walls, VS height extrusion along a per-vertex <c>sec φ</c>-baked extrude-up), and
    /// <see cref="TryCreate"/> clones the dedicated <c>Map/FillExtrusion</c> base material
    /// (<see cref="Materials.MapMaterialSet.FillExtrusionMaterial"/>) via
    /// <see cref="Materials.MaterialFactory.CreateFillExtrusionMaterial"/> instead of the flat FILL base.</para>
    ///
    /// <para><b>job-scheduling-design.md §8 stage 4:</b> the roof+walls mesh on the job graph.</para>
    ///
    /// Axes (design §"Axis pinning"): <see cref="RenderLayerBuild.TileMesh"/> /
    /// <see cref="DrawPersistence.Persistent"/>.
    /// </summary>
    internal sealed class FillExtrusionRenderLayer : ITileMeshRenderLayer
    {
        private readonly ZoomStyleApplier _applier;
        private readonly FillExtrusion.PaintProperties _paint;

        public MapRenderer.Core.Style.StyleLayer StyleLayer  { get; }
        public RenderLayerBuild                  Build       => RenderLayerBuild.TileMesh;
        public DrawPersistence                   Persistence => DrawPersistence.Persistent;
        public int                               DrawIndex   { get; }
        public LayerSubSlot                      MaterialSubSlot => LayerSubSlot.Base;
        public Material                          Material    { get; }

        private FillExtrusionRenderLayer(
            FillExtrusion.StyleLayer layer, Material material, ZoomStyleApplier applier, int drawIndex)
        {
            StyleLayer = layer;
            Material   = material;
            _applier   = applier;
            _paint     = layer.Paint;
            DrawIndex  = drawIndex;
        }

        /// <summary>
        /// Builds a fill-extrusion render layer from a parsed <see cref="FillExtrusion.StyleLayer"/>,
        /// applying the initial zoom's uniforms. Returns <c>null</c> when the material set is unconfigured
        /// (the <see cref="Materials.MaterialFactory"/> warns and returns a null material) — the caller
        /// skips the layer, exactly as <see cref="FillRenderLayer.TryCreate"/> does.
        /// </summary>
        /// <param name="layer">The parsed fill-extrusion style layer.</param>
        /// <param name="settings">The material set to clone the FILL-EXTRUSION base material from.</param>
        /// <param name="initialZoom">The zoom to seed the first uniform push at.</param>
        /// <param name="drawIndex">This layer's global draw slot (D7), threaded straight into the instance.</param>
        /// <returns>The new render layer, or <c>null</c> when the material set is unconfigured.</returns>
        public static FillExtrusionRenderLayer TryCreate(
            FillExtrusion.StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex)
        {
            Material mat = Materials.MaterialFactory.CreateFillExtrusionMaterial(settings);
            if (mat == null) return null;

            var applier = new ZoomStyleApplier(mat);
            Materials.MaterialFactory.BindFillExtrusionPaintToApplier(layer.Paint, applier, mat);
            // Seeded at dpr 1 — the live ratio arrives with the first ApplyZoom, before any frame draws
            // (RenderLayerSet.ApplyZoom's contract).
            applier.ApplyZoom(initialZoom, 1.0);
            return new FillExtrusionRenderLayer(layer, mat, applier, drawIndex);
        }

        public void ApplyZoom(double zoom, double devicePixelRatio) => _applier.ApplyZoom(zoom, devicePixelRatio);

        // job-scheduling-design.md §8 stage 4: mirrors FillRenderLayer.BuildGraphRequest's shape — the
        // graph's write step does the mesh write.
        public Meshing.ILayerMeshBuild BuildGraphRequest(
            IReadOnlyList<SelectedTileFeature> selected, TileGeometryBuffers geometry,
            in TileLayerProcessContext context, int materialIndex, string payloadName)
        {
            FillMeshPipeline.LayerInput input = Meshing.StyledFillExtrusionTileBuilder.BuildLayerInput(
                selected, geometry, _paint, context.Zoom, context.TileOriginRender,
                out var colors, out var bake,
                context.Projection, context.BufferClip, context.Buffers);
            // The relocated emptiness gate — see FillRenderLayer.BuildGraphRequest's own comment.
            if (!input.RingVisitOrder.IsCreated) return null;
            return Meshing.FillExtrusionLayerBuild.Rent(input, colors, bake, materialIndex, payloadName);
        }

        public void Dispose() => RenderLayerSet.DestroyMaterialInstance(Material);
    }
}
