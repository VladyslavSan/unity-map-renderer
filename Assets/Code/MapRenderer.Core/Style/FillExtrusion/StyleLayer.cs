namespace MapRenderer.Core.Style.FillExtrusion
{
    /// <summary>
    /// A MapLibre <c>fill-extrusion</c> style layer: the generic
    /// <see cref="MapRenderer.Core.Style.StyleLayer"/> specialized with its typed, parsed
    /// <see cref="PaintProperties"/>. Built by <see cref="MapRenderer.Core.Style.StyleParser"/> for layers
    /// of type <c>"fill-extrusion"</c>, which parses the typed paint eagerly at construction. It has no
    /// <c>Layout</c> property, because the Style Spec defines no <c>fill-extrusion</c> layout keys.
    /// </summary>
    public sealed class StyleLayer : MapRenderer.Core.Style.StyleLayer
    {
        /// <summary>The parsed fill-extrusion paint properties (height, base, color, opacity, …).</summary>
        public PaintProperties Paint { get; init; }
    }
}
