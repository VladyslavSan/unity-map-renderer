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
        // Prefixed `x-` so they can never collide with a key the spec adds later, and so a reader can tell
        // spec from extension without consulting the spec. MapLibre itself ignores unknown keys, so a style
        // carrying these still renders in stock MapLibre — just without the extension.

        /// <summary>
        /// <c>x-fill-pattern-metres</c> — the pattern's TILING PERIOD in world units (Web-Mercator metres):
        /// the world distance spanned by ONE full repetition of the sprite. Supplying it switches the layer to
        /// world-absolute sizing, so the pattern scales with the map like terrain instead of holding a
        /// constant screen size. At a period of 1, pattern UV advances by exactly 1 per world unit.
        ///
        /// <para>Deliberately ONE key rather than a mode plus a size: world sizing is meaningless without a
        /// size, and a size is meaningless under screen sizing, so folding them together makes the invalid
        /// combination unrepresentable. Absent (or non-numeric) ⇒
        /// <see cref="FillPatternSizing.ScreenRelative"/>, the Style Spec's behaviour.</para>
        /// </summary>
        public const string FillPatternMetres     = "x-fill-pattern-metres";
    }
}
