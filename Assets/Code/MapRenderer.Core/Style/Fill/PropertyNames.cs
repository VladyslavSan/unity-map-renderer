namespace MapRenderer.Core.Style.Fill
{
    /// <summary>
    /// The single source of the fill style key strings — the MapLibre <c>fill-*</c> spec keys, plus the
    /// explicitly-marked <c>x-</c> engine extensions at the bottom. Every other class in
    /// <c>Style.Fill</c> references these constants; no <c>"fill-…"</c> literal lives elsewhere (enforced
    /// by a test). Clean-room: public Style Spec.
    /// </summary>
    public static class PropertyNames
    {
        /// <summary>The one <b>layout</b> key; everything below is paint.</summary>
        public const string FillSortKey         = "fill-sort-key";

        public const string FillColor           = "fill-color";
        public const string FillOpacity         = "fill-opacity";
        public const string FillOutlineColor    = "fill-outline-color";
        public const string FillAntialias       = "fill-antialias";
        public const string FillTranslate       = "fill-translate";
        public const string FillTranslateAnchor = "fill-translate-anchor";
        public const string FillPattern         = "fill-pattern";

        // ── Engine extensions (NOT MapLibre Style Spec) ───────────────────────────────────────────
        // Prefixed `x-` so they never collide with a future spec key; MapLibre ignores unknown keys.

        /// <summary>
        /// <c>x-fill-pattern-metres</c> — the pattern's TILING PERIOD in Web-Mercator metres: the world
        /// distance of ONE full repetition. Supplying it switches the layer to world-absolute sizing, so the
        /// pattern scales with the map like terrain. It is ONE key, not a mode plus a size, so the invalid
        /// combinations cannot be written. Absent or non-numeric ⇒
        /// <see cref="FillPatternSizing.ScreenRelative"/>, the Style Spec's behaviour.
        /// </summary>
        public const string FillPatternMetres     = "x-fill-pattern-metres";
    }
}
