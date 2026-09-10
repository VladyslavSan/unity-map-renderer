namespace MapRenderer.Core.Style.Line
{
    /// <summary>
    /// A MapLibre <c>line</c> style layer: the generic <see cref="MapRenderer.Core.Style.StyleLayer"/>
    /// specialized with its typed, parsed <see cref="PaintProperties"/> and <see cref="LayoutProperties"/>.
    /// Built by <see cref="MapRenderer.Core.Style.StyleParser"/> for layers of type <c>"line"</c>, which
    /// parses the typed views eagerly at construction.
    /// </summary>
    public sealed class StyleLayer : MapRenderer.Core.Style.StyleLayer
    {
        /// <summary>The parsed line paint properties (color, width, opacity, dasharray, …).</summary>
        public PaintProperties Paint { get; init; }

        /// <summary>The parsed line layout properties (join, cap, miter-limit, round-limit).</summary>
        public LayoutProperties Layout { get; init; }
    }
}
