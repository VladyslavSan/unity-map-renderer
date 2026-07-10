namespace MapRenderer.Unity.Rendering.ShaderProperties.Fill
{
    /// <summary>
    /// Canonical string names for the <c>Map/Fill</c>-specific shader properties — the 5 names that
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
    }
}
