namespace MapRenderer.Core.Style.Fill
{
    /// <summary>
    /// The single source of the MapLibre <c>fill-*</c> style key strings. Every other class in
    /// <c>Style.Fill</c> references these constants; no <c>"fill-…"</c> literal lives elsewhere (enforced
    /// by a test). Fill has no layout keys today, so all keys here are paint. Clean-room: public Style Spec.
    /// </summary>
    public static class PropertyNames
    {
        public const string FillColor           = "fill-color";
        public const string FillOpacity         = "fill-opacity";
        public const string FillOutlineColor    = "fill-outline-color";
        public const string FillAntialias       = "fill-antialias";
        public const string FillTranslate       = "fill-translate";
        public const string FillTranslateAnchor = "fill-translate-anchor";
        public const string FillPattern         = "fill-pattern";
    }
}
