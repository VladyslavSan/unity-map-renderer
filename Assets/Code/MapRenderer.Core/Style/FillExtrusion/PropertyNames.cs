namespace MapRenderer.Core.Style.FillExtrusion
{
    /// <summary>
    /// The single source of the fill-extrusion style key strings — the MapLibre <c>fill-extrusion-*</c>
    /// spec keys. Every other class in <c>Style.FillExtrusion</c> references these constants. Clean-room:
    /// public Style Spec.
    /// </summary>
    public static class PropertyNames
    {
        /// <summary>The extruded geometry's height in metres, measured from <see cref="FillExtrusionBase"/>.</summary>
        public const string FillExtrusionHeight          = "fill-extrusion-height";

        /// <summary>The extruded geometry's base height in metres above ground.</summary>
        public const string FillExtrusionBase             = "fill-extrusion-base";

        /// <summary>The extruded geometry's base color.</summary>
        public const string FillExtrusionColor            = "fill-extrusion-color";

        /// <summary>The extruded geometry's opacity multiplier.</summary>
        public const string FillExtrusionOpacity          = "fill-extrusion-opacity";

        /// <summary>Pixel-space [x, y] translation offset. Parsed through the expression engine
        /// (<see cref="PaintProperties.Translate"/>) so a zoom/interpolate expression classifies
        /// instead of collapsing to [0,0]; rendered via the shared <c>PixelsToWorld</c> px→world
        /// measurement.</summary>
        public const string FillExtrusionTranslate        = "fill-extrusion-translate";

        /// <summary>Coordinate space for <see cref="FillExtrusionTranslate"/>: <c>"map"</c> or <c>"viewport"</c>.</summary>
        public const string FillExtrusionTranslateAnchor  = "fill-extrusion-translate-anchor";

        /// <summary>Whether a vertical shading gradient is applied to the sides of the extruded geometry.</summary>
        public const string FillExtrusionVerticalGradient = "fill-extrusion-vertical-gradient";
    }
}
