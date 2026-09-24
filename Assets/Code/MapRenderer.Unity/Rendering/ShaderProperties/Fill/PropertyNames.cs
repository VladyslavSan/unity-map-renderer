namespace MapRenderer.Unity.Rendering.ShaderProperties.Fill
{
    /// <summary>
    /// Canonical string names for the <c>Map/Fill</c>-specific shader properties — the names that
    /// appear in <c>Fill_LitInput.hlsl</c>'s CBUFFER but not in <c>Line_LitInput.hlsl</c>. Shared
    /// properties live in <see cref="ShaderProperties.PropertyNames"/>. Use <see cref="PropertyId"/> for
    /// <c>Material.Set/Get/Has</c> calls; use this class only where the Unity API requires a string
    /// (<c>MaterialEditor.FindProperty</c>).
    /// </summary>
    public static class PropertyNames
    {
        public const string FillOutlineColor    = "_FillOutlineColor";
        public const string FillAntialias       = "_FillAntialias";
        public const string FillTranslate       = "_FillTranslate";
        public const string FillTranslateAnchor = "_FillTranslateAnchor";
        public const string FillPattern         = "_FillPattern";

        // ── fill-pattern sampling ─────────────────────────────────────────────────────────────────
        // The pattern sheet `_PatternMap` is a texture, not a CBUFFER member, so it lives in TexturePropertyId.

        /// <summary>xy = sprite top-left in sheet pixels, zw = sprite size in sheet pixels.
        /// A ZERO-AREA rect (zw == 0) is the canonical "declared but unresolved" state.</summary>
        public const string PatternRect         = "_PatternRect";

        /// <summary>Pattern repeats per world unit (xy).</summary>
        public const string PatternScale        = "_PatternScale";
    }
}
