using MapRenderer.Core.Style;
using Fill = MapRenderer.Core.Style.Fill;
using Line = MapRenderer.Core.Style.Line;

namespace MapRenderer.Unity.Rendering.Style
{
    /// <summary>
    /// Maps a parsed <see cref="StyleLayer"/> subtype to its runtime <see cref="IRenderLayer"/>. This is the
    /// ONE registry the render pipeline consults: adding a static layer type (e.g. fill-extrusion) is one
    /// new <see cref="IRenderLayer"/> class + one arm here — the layer set, the backends, and the tile
    /// consume loop need no edits (S89 extensibility tooth).
    ///
    /// <para>Layer types with no static-geometry render (background / raster / symbol / and, for now,
    /// fill-extrusion) return <c>null</c> — they take no slot in the ordered <see cref="RenderLayerSet"/>,
    /// exactly as the retired <c>StyledLayerSet.Build</c> skipped them.</para>
    /// </summary>
    internal static class RenderLayerFactory
    {
        /// <summary>Creates the render layer for <paramref name="layer"/>, or <c>null</c> when the type is
        /// not a static render layer, or the material set is unconfigured (the concrete
        /// <c>TryCreate</c> returns null and the factory propagates it).</summary>
        public static IRenderLayer Create(
            StyleLayer layer, Materials.MapMaterialSet settings, double initialZoom)
            => layer switch
            {
                Fill.StyleLayer f => FillRenderLayer.TryCreate(f, settings, initialZoom),
                Line.StyleLayer l => LineRenderLayer.TryCreate(l, settings, initialZoom),
                _                 => null,
            };
    }
}
