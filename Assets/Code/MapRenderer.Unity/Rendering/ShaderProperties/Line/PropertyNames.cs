namespace MapRenderer.Unity.Rendering.ShaderProperties.Line
{
    /// <summary>
    /// Canonical string names for the <c>Map/Line</c>-specific shader properties — the 12 names that
    /// appear in <c>Line_LitInput.hlsl</c>'s CBUFFER but not in <c>Fill_LitInput.hlsl</c>.
    ///
    /// <para>Shared properties (the 14 CBUFFER members common to both shaders, plus render-state and
    /// texture bookkeeping) live in <see cref="ShaderProperties.PropertyNames"/>.</para>
    ///
    /// <para>Use <see cref="PropertyId"/> for <c>Material.Set/Get/Has</c> calls.
    /// Use this class only where the Unity API requires a string: <c>MaterialEditor.FindProperty</c>.</para>
    /// </summary>
    public static class PropertyNames
    {
        // ── Style-bound (MapLibre line-* paint/layout; written by the styler) ──
        public const string Width               = "_Width";
        public const string Blur                = "_Blur";  // MapLibre line-blur (opt-in soft edge, NOT antialiasing)
        public const string GapWidth            = "_GapWidth";
        public const string LineTranslate       = "_LineTranslate";
        public const string LineTranslateAnchor = "_LineTranslateAnchor";
        public const string LinePattern         = "_LinePattern";
        public const string DashArray           = "_DashArray";
        public const string DashCount           = "_DashCount";
        public const string LineOffset          = "_LineOffset";

        // ── Internal render params (NOT style properties; the styler never writes these) ──
        public const string WidthIsPixels  = "_WidthIsPixels";
    }
}
