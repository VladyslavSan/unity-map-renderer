namespace MapRenderer.Unity.Rendering.ShaderProperties.Line
{
    /// <summary>
    /// Canonical string names for the <c>Map/Line</c>-specific shader properties — 12 in total: the 10
    /// that appear in <c>Line_LitInput.hlsl</c>'s CBUFFER but not in <c>Fill_LitInput.hlsl</c>, plus two
    /// editor-only keyword drivers that are declared in <c>Properties{}</c> and are NOT CBUFFER members.
    ///
    /// <para>The two are separated by region markers, the same shape the shared registry uses to let the
    /// editor-only <c>_ReceiveShadows</c> sit beside the instanced names. The CBUFFER↔registry parity test
    /// reads the CBUFFER region only, so it still holds bit-for-bit over every CBUFFER name.</para>
    ///
    /// <para>Shared properties (the 14 CBUFFER members common to both shaders, plus render-state and
    /// texture bookkeeping) live in <see cref="ShaderProperties.PropertyNames"/>.</para>
    ///
    /// <para>Use <see cref="PropertyId"/> for <c>Material.Set/Get/Has</c> calls.
    /// Use this class only where the Unity API requires a string: <c>MaterialEditor.FindProperty</c>.</para>
    /// </summary>
    public static class PropertyNames
    {
        // region: CBUFFER (UnityPerMaterial) — line-only instanced members
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

        // region: Editor-only keyword drivers — Properties{} only, NOT in the CBUFFER
        // Nothing in HLSL reads these; only the keyword the editor sync derives from them is read.
        // Same shape as the shared registry's _ReceiveShadows.
        public const string EdgeAntialiasing = "_EdgeAntialiasing";

        // 0 = Default (no keyword — today's straddle), 1 = Hard (_HAIRLINE_HARD),
        // 2 = SolidCore (_HAIRLINE_SOLID_CORE).
        public const string HairlineStrategy = "_HairlineStrategy";
    }
}
