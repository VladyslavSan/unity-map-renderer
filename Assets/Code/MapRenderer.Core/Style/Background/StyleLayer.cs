namespace MapRenderer.Core.Style.Background
{
    /// <summary>
    /// A MapLibre <c>background</c> style layer: the generic <see cref="MapRenderer.Core.Style.StyleLayer"/>
    /// specialized with its typed, parsed <see cref="PaintProperties"/>. Built by
    /// <see cref="MapRenderer.Core.Style.StyleParser"/> for layers of type <c>"background"</c>. Background
    /// has no layout keys and no <c>source</c>/<c>source-layer</c> by spec — the inherited base fields
    /// simply stay null, as before. The typed paint is parsed eagerly at construction.
    /// </summary>
    public sealed class StyleLayer : MapRenderer.Core.Style.StyleLayer
    {
        /// <summary>The parsed background paint properties (color, opacity, pattern).</summary>
        public PaintProperties Paint { get; init; }
    }
}
