namespace MapRenderer.Core.Style.Line
{
    /// <summary>
    /// The single source of the MapLibre <c>line-*</c> style key strings — paint AND layout. Every other
    /// class in <c>Style.Line</c> references these constants; no <c>"line-…"</c> string literal lives
    /// anywhere else (enforced by a test). Clean-room: keys from the public MapLibre Style Spec §line layer.
    /// </summary>
    public static class PropertyNames
    {
        // ── paint ──
        public const string LineColor           = "line-color";
        public const string LineOpacity         = "line-opacity";
        public const string LineWidth           = "line-width";
        public const string LineBlur            = "line-blur";
        public const string LineGapWidth        = "line-gap-width";
        public const string LineOffset          = "line-offset";
        public const string LineTranslate       = "line-translate";
        public const string LineTranslateAnchor = "line-translate-anchor";
        public const string LinePattern         = "line-pattern";
        public const string LineDasharray       = "line-dasharray";

        // ── layout ──
        public const string LineJoin            = "line-join";
        public const string LineCap             = "line-cap";
        public const string LineMiterLimit      = "line-miter-limit";
        public const string LineRoundLimit      = "line-round-limit";

        // ── line-cap values ──
        public const string CapButt   = "butt";
        public const string CapRound  = "round";
        public const string CapSquare = "square";

        // ── line-join values ──
        public const string JoinMiter = "miter";
        public const string JoinRound = "round";  // intentionally same string as CapRound; kept separate per domain
        public const string JoinBevel = "bevel";
    }
}
