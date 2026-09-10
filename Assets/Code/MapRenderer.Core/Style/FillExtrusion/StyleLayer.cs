namespace MapRenderer.Core.Style.FillExtrusion
{
    /// <summary>
    /// A MapLibre <c>fill-extrusion</c> style layer: the generic
    /// <see cref="MapRenderer.Core.Style.StyleLayer"/> specialized with its typed, parsed
    /// <see cref="PaintProperties"/>. Built by <see cref="MapRenderer.Core.Style.StyleParser"/> for layers
    /// of type <c>"fill-extrusion"</c>, which parses the typed paint eagerly at construction.
    ///
    /// <para>No <c>Layout</c> property — <c>fill-extrusion-*</c> has no layout keys in the Style Spec,
    /// unlike <see cref="Fill.StyleLayer"/>'s single <c>fill-sort-key</c>.</para>
    /// </summary>
    public sealed class StyleLayer : MapRenderer.Core.Style.StyleLayer
    {
        /// <summary>The parsed fill-extrusion paint properties (height, base, color, opacity, …).</summary>
        public PaintProperties Paint { get; init; }
    }
}
