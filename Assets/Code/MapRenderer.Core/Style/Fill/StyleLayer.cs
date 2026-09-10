namespace MapRenderer.Core.Style.Fill
{
    /// <summary>
    /// A MapLibre <c>fill</c> style layer: the generic <see cref="MapRenderer.Core.Style.StyleLayer"/>
    /// specialized with its typed, parsed <see cref="PaintProperties"/>. Built by
    /// <see cref="MapRenderer.Core.Style.StyleParser"/> for layers of type <c>"fill"</c>. The typed paint and
    /// layout are each parsed once (lazily, cached) from the inherited raw <c>PaintJson</c>/<c>LayoutJson</c>.
    /// </summary>
    public sealed class StyleLayer : MapRenderer.Core.Style.StyleLayer
    {
        private PaintProperties  _paint;
        private LayoutProperties _layout;

        /// <summary>The parsed fill paint properties (color, opacity, outline-color, antialias, …).</summary>
        /// <summary>What <c>fill-antialias</c> means for THIS layer when its paint block omits it —
        /// supplied by <see cref="MapRenderer.Core.Style.StyleParser"/> from the host's configuration.
        /// Must be set before <see cref="Paint"/> is first read, since that parse is cached.</summary>
        public bool AntialiasDefault { get; set; } = true;

        /// <summary>The parsed fill paint properties (color, opacity, outline-color, antialias, …).</summary>
        public PaintProperties Paint => _paint ?? (_paint = new PaintProperties(PaintJson, AntialiasDefault));

        /// <summary>The parsed fill layout properties (<c>fill-sort-key</c>).</summary>
        public LayoutProperties Layout => _layout ?? (_layout = new LayoutProperties(LayoutJson));
    }
}
