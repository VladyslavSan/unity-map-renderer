namespace MapRenderer.Core.Style.Fill
{
    /// <summary>
    /// A MapLibre <c>fill</c> style layer: the generic <see cref="MapRenderer.Core.Style.StyleLayer"/>
    /// specialized with its typed, parsed <see cref="PaintProperties"/>. Built by
    /// <see cref="MapRenderer.Core.Style.StyleParser"/> for layers of type <c>"fill"</c>. Fill has no
    /// layout keys today, so there is no LayoutProperties. The typed paint is parsed once (lazily, cached)
    /// from the inherited raw <c>PaintJson</c>.
    /// </summary>
    public sealed class StyleLayer : MapRenderer.Core.Style.StyleLayer
    {
        private PaintProperties _paint;

        /// <summary>The parsed fill paint properties (color, opacity, outline-color, antialias, …).</summary>
        public PaintProperties Paint => _paint ?? (_paint = new PaintProperties(PaintJson));
    }
}
