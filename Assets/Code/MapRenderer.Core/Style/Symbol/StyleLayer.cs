namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// A MapLibre <c>symbol</c> style layer: the generic <see cref="MapRenderer.Core.Style.StyleLayer"/>
    /// specialized with its typed, parsed <see cref="PaintProperties"/> and <see cref="LayoutProperties"/>.
    /// Built by <see cref="MapRenderer.Core.Style.StyleParser"/> for layers of type <c>"symbol"</c>. The
    /// typed views are parsed once (lazily, then cached) from the inherited raw <c>PaintJson</c>/
    /// <c>LayoutJson</c> — mirroring <c>Line.StyleLayer</c>/<c>Fill.StyleLayer</c>. TEXT-ONLY (icons later).
    /// </summary>
    public sealed class StyleLayer : MapRenderer.Core.Style.StyleLayer
    {
        private PaintProperties _paint;
        private LayoutProperties _layout;

        /// <summary>The parsed symbol paint properties (text-color/opacity, text-halo-*).</summary>
        public PaintProperties Paint => _paint ?? (_paint = new PaintProperties(PaintJson));

        /// <summary>The parsed symbol layout properties (text-field/font/size, symbol-sort-key, padding, …).</summary>
        public LayoutProperties Layout => _layout ?? (_layout = new LayoutProperties(LayoutJson));
    }
}
