namespace MapRenderer.Core.Style.Symbol
{
    /// <summary>
    /// The single source of the MapLibre <c>symbol</c> / <c>text-*</c>/<c>icon-*</c> style key strings —
    /// layout AND paint. Every other class in <c>Style.Symbol</c> references these constants; no
    /// <c>"text-…"</c>/<c>"icon-…"</c>/<c>"symbol-…"</c> string literal lives anywhere else (enforced by a
    /// test, mirroring <c>Style.Line</c>). Clean-room: keys from the public MapLibre Style Spec §symbol
    /// layer. The point <c>icon-*</c> layout/paint keys (§5.1 IN) are included; <c>icon-color</c> and the
    /// <c>icon-halo-*</c> trio are deferred to the SDF epic.
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
        public const string TextRadialOffset   = "text-radial-offset";
        public const string TextJustify        = "text-justify";
        public const string TextLineHeight     = "text-line-height";
        public const string TextLetterSpacing  = "text-letter-spacing";
        public const string TextTransform      = "text-transform";
        public const string TextRotationAlignment = "text-rotation-alignment";
        public const string TextPitchAlignment    = "text-pitch-alignment";
        public const string SymbolPlacement    = "symbol-placement";
        public const string SymbolSortKey      = "symbol-sort-key";
        public const string SymbolSpacing      = "symbol-spacing";
        public const string TextMaxAngle       = "text-max-angle";
        public const string TextKeepUpright    = "text-keep-upright";
        public const string TextAllowOverlap   = "text-allow-overlap";
        public const string TextIgnorePlacement = "text-ignore-placement";
        public const string TextPadding        = "text-padding";
        public const string IconImage          = "icon-image";
        public const string IconSize           = "icon-size";
        public const string IconOffset         = "icon-offset";
        public const string IconRotate         = "icon-rotate";
        public const string IconAnchor         = "icon-anchor";
        public const string IconRotationAlignment = "icon-rotation-alignment";
        public const string IconAllowOverlap   = "icon-allow-overlap";
        public const string IconIgnorePlacement = "icon-ignore-placement";
        public const string IconOptional       = "icon-optional";
        public const string TextOptional       = "text-optional";
        public const string IconPadding        = "icon-padding";

        // ── paint ──
        public const string TextColor          = "text-color";
        public const string TextOpacity        = "text-opacity";
        public const string TextHaloColor      = "text-halo-color";
        public const string TextHaloWidth      = "text-halo-width";
        public const string TextHaloBlur       = "text-halo-blur";
        public const string TextTranslate      = "text-translate";
        public const string TextTranslateAnchor = "text-translate-anchor";
        public const string IconOpacity        = "icon-opacity";

        // ── symbol-placement values (S20 is point-only; the others render unlabeled until a follow-up) ──
        public const string PlacementPoint      = "point";
        public const string PlacementLine       = "line";
        public const string PlacementLineCenter = "line-center";

        // ── text-anchor values (spec §symbol layout; center is the default/zero) ──
        public const string AnchorCenter      = "center";
        public const string AnchorLeft        = "left";
        public const string AnchorRight       = "right";
        public const string AnchorTop         = "top";
        public const string AnchorBottom      = "bottom";
        public const string AnchorTopLeft     = "top-left";
        public const string AnchorTopRight    = "top-right";
        public const string AnchorBottomLeft  = "bottom-left";
        public const string AnchorBottomRight = "bottom-right";

        // ── text-justify values (auto is the default/zero; resolves from anchor at layout time) ──
        public const string JustifyAuto   = "auto";
        public const string JustifyLeft   = "left";
        public const string JustifyCenter = "center";
        public const string JustifyRight  = "right";

        // ── text-transform values (none is the default/zero) ──
        public const string TransformNone      = "none";
        public const string TransformUppercase = "uppercase";
        public const string TransformLowercase = "lowercase";

        // ── text-translate-anchor values (map is the default/zero) ──
        public const string TranslateAnchorMap      = "map";
        public const string TranslateAnchorViewport = "viewport";

        // ── text-rotation-alignment / text-pitch-alignment values (auto is the default/zero) ──
        public const string AlignAuto     = "auto";
        public const string AlignMap      = "map";
        public const string AlignViewport = "viewport";
    }
}
