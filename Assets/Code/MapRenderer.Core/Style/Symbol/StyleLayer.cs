namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// A MapLibre <c>symbol</c> style layer: the generic <see cref="MapRenderer.Core.Style.StyleLayer"/>
    /// specialized with its typed, parsed <see cref="PaintProperties"/> and <see cref="LayoutProperties"/>.
    /// Built by <see cref="MapRenderer.Core.Style.StyleParser"/> for layers of type <c>"symbol"</c>, which
    /// parses the typed views eagerly at construction — mirroring <c>Line.StyleLayer</c>/
    /// <c>Fill.StyleLayer</c>. TEXT-ONLY (icons later).
    /// </summary>
    public sealed class StyleLayer : MapRenderer.Core.Style.StyleLayer
    {
        /// <summary>The parsed symbol paint properties (text-color/opacity, text-halo-*).</summary>
        public PaintProperties Paint { get; init; }

        /// <summary>The parsed symbol layout properties (text-field/font/size, symbol-sort-key, padding, …).</summary>
        public LayoutProperties Layout { get; init; }
    }
}
