namespace MapRenderer.Unity.Rendering
{
    /// <summary>
    /// Single source of truth for the shader <b>property</b> names used by the map shaders
    /// (<c>Map/Fill</c>, <c>Map/Line</c>).
    ///
    /// <para>Referenced by the typed render-state layer (<see cref="MaterialRenderStateExtensions"/>),
    /// the runtime material tweakers (<see cref="BaseMaterialTweaker"/> / <c>Fill</c> / <c>Line</c>), and the
    /// editor ShaderGUI keyword sync — so a bare <c>"_ZWrite"</c> literal lives in exactly one place. Lives in
    /// the runtime assembly (<c>MapRenderer.Unity</c>) so both runtime and editor code share it (S58).</para>
    /// </summary>
    public static class ShaderProperties
    {
        // ── Forward-pass render state (ShaderLab [_…] knobs; NOT in the UnityPerMaterial CBUFFER) ──
        public const string ZWrite        = "_ZWrite";
        public const string ZTest         = "_ZTest";
        public const string CullMode      = "_Cull";
        public const string SrcBlend      = "_SrcBlend";
        public const string DstBlend      = "_DstBlend";
        public const string SrcBlendAlpha = "_SrcBlendAlpha";
        public const string DstBlendAlpha = "_DstBlendAlpha";
        public const string BlendOp       = "_BlendOp";
        public const string AlphaToMask   = "_AlphaToMask";
        
        // ── Surface-option bookkeeping (kept for .mat compatibility; no auto queue resolution in S58) ──
        public const string Surface        = "_Surface";
        public const string Blend          = "_Blend";
        public const string AlphaClip      = "_AlphaClip";
        public const string Cutoff         = "_Cutoff";
        public const string ReceiveShadows = "_ReceiveShadows";

        // ── Map paint / color identity ──
        public const string Opacity   = "_Opacity";
        public const string BaseColor = "_BaseColor";
        public const string BaseMap   = "_BaseMap";

        // ── Line-specific ──
        public const string Width                = "_Width";
        public const string WidthIsPixels        = "_WidthIsPixels";
        public const string MetersPerPixel       = "_MetersPerPixel";
        public const string Blur                 = "_Blur";
        public const string GapWidth             = "_GapWidth";
        public const string LineTranslate        = "_LineTranslate";
        public const string LineTranslateAnchor  = "_LineTranslateAnchor";
        public const string LinePattern          = "_LinePattern";
        public const string DashArray            = "_DashArray";
        public const string DashCount            = "_DashCount";
        public const string LineOffset           = "_LineOffset";

        // ── Fill-specific ──
        public const string FillOutlineColor    = "_FillOutlineColor";
        public const string FillAntialias        = "_FillAntialias";
        public const string FillTranslate        = "_FillTranslate";
        public const string FillTranslateAnchor  = "_FillTranslateAnchor";
        public const string FillPattern          = "_FillPattern";

        // ── Lit (full URP Lit surface inputs — S58 Q1 = full fidelity) ──
        public const string WorkflowMode             = "_WorkflowMode";
        public const string Metallic                 = "_Metallic";
        public const string MetallicGlossMap         = "_MetallicGlossMap";
        public const string SpecColor                = "_SpecColor";
        public const string SpecGlossMap             = "_SpecGlossMap";
        public const string Smoothness               = "_Smoothness";
        public const string SmoothnessTextureChannel = "_SmoothnessTextureChannel";
        public const string BumpMap                  = "_BumpMap";
        public const string BumpScale                = "_BumpScale";
        public const string Parallax                 = "_Parallax";
        public const string ParallaxMap              = "_ParallaxMap";
        public const string OcclusionStrength        = "_OcclusionStrength";
        public const string OcclusionMap             = "_OcclusionMap";
        public const string EmissionColor            = "_EmissionColor";
        public const string EmissionMap              = "_EmissionMap";
        public const string SpecularHighlights       = "_SpecularHighlights";
        public const string EnvironmentReflections   = "_EnvironmentReflections";
        public const string ClearCoatMask            = "_ClearCoatMask";
        public const string ClearCoatSmoothness      = "_ClearCoatSmoothness";
        public const string DetailMask               = "_DetailMask";
        public const string DetailAlbedoMap          = "_DetailAlbedoMap";
        public const string DetailNormalMap          = "_DetailNormalMap";
        public const string DetailAlbedoMapScale     = "_DetailAlbedoMapScale";
        public const string DetailNormalMapScale     = "_DetailNormalMapScale";
    }
}
