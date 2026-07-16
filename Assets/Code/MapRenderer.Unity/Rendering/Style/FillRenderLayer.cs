using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Unity.Profiling;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Fill <see cref="ITileMeshRenderLayer"/>: a MapLibre <c>fill</c> layer as a runtime render object.
    /// Wraps the managed <see cref="Meshing.StyledFillTileBuilder"/> (unchanged) — this is pure indirection
    /// over the existing mesh-building code, so it is behaviour-preserving.
    /// Axes (design §"Axis pinning"): <see cref="RenderLayerBuild.TileMesh"/> / <see cref="DrawPersistence.Persistent"/>.
    /// </summary>
    internal sealed class FillRenderLayer : ITileMeshRenderLayer
    {
        // Nested under MapRenderer.View.ApplyZoom — the fill applier loop (expression eval → SetFloat/
        // SetColor), scales with fill-layer count. Preserved verbatim from the retired StyledLayerSet so the
        // S46 greppable-marker set is intact; it now fires once per fill layer (was once around the fill loop).
        private static readonly ProfilerMarker PmZoomFills =
            new ProfilerMarker(ProfilerCategory.Scripts, "MapRenderer.View.ApplyZoom.Fills");

        private readonly Fill.PaintProperties _paint;
        private readonly ZoomStyleApplier     _applier;

        public MapRenderer.Core.Style.StyleLayer StyleLayer  { get; }
        public RenderLayerBuild                  Build       => RenderLayerBuild.TileMesh;
        public DrawPersistence                   Persistence => DrawPersistence.Persistent;
        public int                               DrawIndex   { get; }
        public Material                          Material    { get; }

        private FillRenderLayer(
            Fill.StyleLayer layer, Material material, Fill.PaintProperties paint, ZoomStyleApplier applier,
            int drawIndex)
        {
            StyleLayer = layer;
            Material   = material;
            _paint     = paint;
            _applier   = applier;
            DrawIndex  = drawIndex;
        }

        /// <summary>
        /// Builds a fill render layer from a parsed <see cref="Fill.StyleLayer"/>, applying the initial
        /// zoom's uniforms. Returns <c>null</c> when the material set is unconfigured (the
        /// <see cref="Materials.MaterialFactory"/> warns and returns a null material) — the caller skips
        /// the layer, exactly as the old <c>StyledLayerSet.Build</c> did.
        /// </summary>
        public static FillRenderLayer TryCreate(
            Fill.StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom, int drawIndex)
        {
            Material mat = Materials.MaterialFactory.CreateFillMaterial(settings);
            if (mat == null) return null;

            Fill.PaintProperties paint = layer.Paint;
            var applier = new ZoomStyleApplier(mat);
            Materials.MaterialFactory.BindFillPaintToApplier(paint, applier, mat);
            applier.ApplyZoom(initialZoom);
            return new FillRenderLayer(layer, mat, paint, applier, drawIndex);
        }

        public void ApplyZoom(double zoom)
        {
            using (PmZoomFills.Auto())
                _applier.ApplyZoom(zoom);
        }

        public void WriteInto(
            Mesh.MeshData md, IReadOnlyList<ITileFeature> features, double zoom, double extent,
            TileId id, double3 tileOriginRender, IProjection projection, out int vertexCount, out Bounds bounds)
            => Meshing.StyledFillTileBuilder.WriteMeshData(
                md, features, _paint, zoom, extent, id, tileOriginRender, out vertexCount, out bounds, projection);

        public void Dispose() => RenderLayerSet.DestroyMaterialInstance(Material);
    }
}
