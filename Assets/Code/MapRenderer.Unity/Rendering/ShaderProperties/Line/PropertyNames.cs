namespace MapRenderer.Unity.Rendering.ShaderProperties.Line
{
    /// <summary>
    /// Canonical string names for the <c>Map/Line</c>-specific shader properties — 10 CBUFFER members (in
    /// <c>Line_LitInput.hlsl</c> but not <c>Fill_LitInput.hlsl</c>) plus two editor-only keyword drivers
    /// declared in <c>Properties{}</c>. Non-local invariant: the two groups are separated by region
    /// markers, because the CBUFFER↔registry parity test reads only the CBUFFER region and must still hold
    /// bit-for-bit over every CBUFFER name. Shared properties live in
    /// <see cref="ShaderProperties.PropertyNames"/>; use <see cref="PropertyId"/> for
    /// <c>Material.Set/Get/Has</c> calls.
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
        // Nothing in HLSL reads these; only the derived keyword is (same shape as _ReceiveShadows).
        public const string EdgeAntialiasing = "_EdgeAntialiasing";

        // 0 = Default (no keyword — the plain straddle), 1 = Hard (_HAIRLINE_HARD),
        // 2 = SolidCore (_HAIRLINE_SOLID_CORE).
        public const string HairlineStrategy = "_HairlineStrategy";
    }
}
