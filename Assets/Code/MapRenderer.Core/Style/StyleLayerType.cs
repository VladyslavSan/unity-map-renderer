namespace MapRenderer.Core.Style
{
    /// <summary>
    /// The 10 MapLibre layer types (Style Spec, <c>layers[].type</c>) plus <see cref="Unknown"/> for
    /// forward-compat: an unrecognized <c>type</c> string parses to <see cref="Unknown"/> rather than
    /// throwing. Each later layer stage (S13 fill, S14 line, S15 circle, S16 background, …) owns the
    /// typed paint/layout parsing for its type; S08 only recognizes and dispatches on this enum.
    /// </summary>
    public enum StyleLayerType
    {
        Unknown = 0,
        Background,
        Fill,
        Line,
        Symbol,
        Circle,
        Heatmap,
        FillExtrusion,
        Raster,
        Hillshade,
        ColorRelief
    }

    public static class StyleLayerTypeExtensions
    {
        /// <summary>
        /// Maps a spec <c>type</c> string (hyphenated, e.g. <c>"fill-extrusion"</c>,
        /// <c>"color-relief"</c>) to <see cref="StyleLayerType"/>. Unknown / null / empty →
        /// <see cref="StyleLayerType.Unknown"/> (never throws).
        /// </summary>
        public static StyleLayerType ParseLayerType(string type)
        {
            switch (type)
            {
                case "background": return StyleLayerType.Background;
                case "fill": return StyleLayerType.Fill;
                case "line": return StyleLayerType.Line;
                case "symbol": return StyleLayerType.Symbol;
                case "circle": return StyleLayerType.Circle;
                case "heatmap": return StyleLayerType.Heatmap;
                case "fill-extrusion": return StyleLayerType.FillExtrusion;
                case "raster": return StyleLayerType.Raster;
                case "hillshade": return StyleLayerType.Hillshade;
                case "color-relief": return StyleLayerType.ColorRelief;
                default: return StyleLayerType.Unknown;
            }
        }
    }
}
