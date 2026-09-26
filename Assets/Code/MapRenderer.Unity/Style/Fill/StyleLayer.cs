namespace MapRenderer.Unity.Style.Fill
{
    /// <summary>
    /// A MapLibre <c>fill</c> style layer: the generic <see cref="MapRenderer.Unity.Style.StyleLayer"/>
    /// specialized with its typed, parsed <see cref="PaintProperties"/>. Built by
    /// <see cref="MapRenderer.Unity.Style.StyleParser"/> for layers of type <c>"fill"</c>, which parses the
    /// typed paint and layout eagerly at construction.
    /// </summary>
    public sealed class StyleLayer : MapRenderer.Unity.Style.StyleLayer
    {
        /// <summary>The parsed fill paint properties (color, opacity, outline-color, antialias, …).</summary>
        public PaintProperties Paint { get; init; }

        /// <summary>The parsed fill layout properties (<c>fill-sort-key</c>).</summary>
        public LayoutProperties Layout { get; init; }
    }
}
