namespace MapRenderer.Core.Style.Line
{
    /// <summary>
    /// A MapLibre <c>line</c> style layer: the generic <see cref="MapRenderer.Core.Style.StyleLayer"/>
    /// specialized with its typed, parsed <see cref="PaintProperties"/> and <see cref="LayoutProperties"/>.
    /// Built by <see cref="MapRenderer.Core.Style.StyleParser"/> for layers of type <c>"line"</c>. The typed
    /// views are parsed once (lazily, then cached) from the inherited raw <c>PaintJson</c>/<c>LayoutJson</c>.
    /// </summary>
    public sealed class StyleLayer : MapRenderer.Core.Style.StyleLayer
    {
        private PaintProperties _paint;
        private LayoutProperties _layout;

        /// <summary>The parsed line paint properties (color, width, opacity, dasharray, …).</summary>
        public PaintProperties Paint => _paint ?? (_paint = new PaintProperties(PaintJson));

        /// <summary>The parsed line layout properties (join, cap, miter-limit, round-limit).</summary>
        public LayoutProperties Layout => _layout ?? (_layout = new LayoutProperties(LayoutJson));
    }
}
