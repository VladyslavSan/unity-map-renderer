namespace MapRenderer.Unity.Rendering.ShaderProperties.Fill
{
    /// <summary>
    /// Canonical string names for the <c>Map/Fill</c>-specific shader properties — the names that
    /// appear in <c>Fill_LitInput.hlsl</c>'s CBUFFER but not in <c>Line_LitInput.hlsl</c>.
    ///
    /// <para>Shared properties live in <see cref="ShaderProperties.PropertyNames"/>.</para>
    ///
    /// <para>Use <see cref="PropertyId"/> for <c>Material.Set/Get/Has</c> calls.
    /// Use this class only where the Unity API requires a string: <c>MaterialEditor.FindProperty</c>.</para>
    /// </summary>
    public static class PropertyNames
    {
        public const string FillOutlineColor    = "_FillOutlineColor";
        public const string FillAntialias       = "_FillAntialias";
        public const string FillTranslate       = "_FillTranslate";
        public const string FillTranslateAnchor = "_FillTranslateAnchor";
        public const string FillPattern         = "_FillPattern";

        // ── fill-pattern sampling ─────────────────────────────────────────────────────────────────
        // Deliberately NOT named after a MapLibre style term: the styling layer binds `fill-X` → `_X`,
        // so a `_Pattern*` name that mirrored a spec key could be silently overwritten by a style
        // (the `_Blur`/`line-blur` collision). `fill-pattern` itself already owns `_FillPattern`.
        //
        // The pattern SHEET (`_PatternMap`) is a texture, not a CBUFFER member, so it lives in
        // <see cref="TexturePropertyId"/> — this class must stay exactly the fill-only CBUFFER set, an
        // invariant the structural parity tests enforce (SharedUnionFillNames_EqualsFillCbuffer).

        /// <summary>xy = sprite top-left in sheet pixels, zw = sprite size in sheet pixels.
        /// A ZERO-AREA rect (zw == 0) is the canonical "declared but unresolved" state.</summary>
        public const string PatternRect         = "_PatternRect";

        /// <summary>Pattern repeats across one tile edge.</summary>
        public const string PatternScale        = "_PatternScale";
    }
}
