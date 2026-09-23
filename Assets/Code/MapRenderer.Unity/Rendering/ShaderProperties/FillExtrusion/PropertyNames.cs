namespace MapRenderer.Unity.Rendering.ShaderProperties.FillExtrusion
{
    /// <summary>
    /// Canonical string names for the <c>Map/FillExtrusion</c>-specific shader properties — the names that
    /// appear in <c>FillExtrusion_LitInput.hlsl</c>'s CBUFFER but not in <c>Fill_LitInput.hlsl</c> or
    /// <c>Line_LitInput.hlsl</c>. Shared properties live in <see cref="ShaderProperties.PropertyNames"/>.
    /// Use <see cref="PropertyId"/> for <c>Material.Set/Get/Has</c> calls; use this class only where the
    /// Unity API requires a string.
    /// </summary>
    public static class PropertyNames
    {
        /// <summary>fill-extrusion-height, constant/zoom path (the uniform; data-driven bakes per-vertex
        /// instead). NOT named <c>_Height</c>, which stays free for a generic height property.</summary>
        public const string ExtrusionHeight = "_ExtrusionHeight";

        /// <summary>fill-extrusion-base, constant/zoom path (the uniform; data-driven bakes per-vertex
        /// instead).</summary>
        public const string ExtrusionBase = "_ExtrusionBase";

        /// <summary>fill-extrusion-translate: xy = pixel offset (world/viewport per
        /// <see cref="FillExtrusionTranslateAnchor"/>). NOT named <c>_FillTranslate</c> —
        /// a fill-extrusion material must never read fill's translate uniform (the two layers are
        /// independently styled, even though the shaders share <c>../PixelsToWorld.hlsl</c>).</summary>
        public const string FillExtrusionTranslate = "_FillExtrusionTranslate";

        /// <summary>fill-extrusion-translate-anchor: 0 = map world-space, 1 = viewport screen-space.</summary>
        public const string FillExtrusionTranslateAnchor = "_FillExtrusionTranslateAnchor";
    }
}
