namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// The single source of the MapLibre <c>symbol</c> / <c>text-*</c> style key strings — layout AND paint.
    /// Every other class in <c>Style.Symbol</c> references these constants; no <c>"text-…"</c>/<c>"symbol-…"</c>
    /// string literal lives anywhere else (enforced by a test, mirroring <c>Style.Line</c>). Clean-room: keys
    /// from the public MapLibre Style Spec §symbol layer. TEXT-ONLY — <c>icon-*</c> keys are a later stage.
    /// </summary>
    public static class PropertyNames
    {
        // ── layout ──
        public const string TextField          = "text-field";
        public const string TextFont           = "text-font";
        public const string TextSize           = "text-size";
        public const string TextMaxWidth       = "text-max-width";
        public const string TextAnchor         = "text-anchor";
        public const string TextOffset         = "text-offset";
        public const string TextJustify        = "text-justify";
        public const string SymbolPlacement    = "symbol-placement";
        public const string SymbolSortKey      = "symbol-sort-key";
        public const string TextAllowOverlap   = "text-allow-overlap";
        public const string TextIgnorePlacement = "text-ignore-placement";
        public const string TextPadding        = "text-padding";

        // ── paint ──
        public const string TextColor          = "text-color";
        public const string TextOpacity        = "text-opacity";
        public const string TextHaloColor      = "text-halo-color";
        public const string TextHaloWidth      = "text-halo-width";
        public const string TextHaloBlur       = "text-halo-blur";

        // ── symbol-placement values (S20 is point-only; the others render unlabeled until a follow-up) ──
        public const string PlacementPoint      = "point";
        public const string PlacementLine       = "line";
        public const string PlacementLineCenter = "line-center";
    }
}
