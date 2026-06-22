namespace MapRenderer.Unity.Rendering
{
    /// <summary>
    /// Single source of truth for the shader-feature <b>keyword</b> names the map shaders declare
    /// (<c>#pragma shader_feature_local*</c> in Fill.shader / Line.shader).
    ///
    /// <para>These are derived from a material's property values by the editor keyword sync
    /// (<c>BaseShaderGUI.ValidateMaterial</c> + <c>LitShaderGUI.ValidateMaterial</c>) — the clean-room
    /// replacement for URP's editor-only <c>SetMaterialKeywords</c>. The names live in the runtime assembly
    /// (not the Editor) so the const strings are shareable. We declare ONLY the keywords our shaders actually
    /// use; this is not a copy of URP's <c>ShaderKeywordStrings</c> (S58).</para>
    /// </summary>
    public static class ShaderKeywords
    {
        // Lit shading-model features (both Fill and Line declare these).
        public const string Emission                        = "_EMISSION";
        public const string MetallicSpecGlossMap            = "_METALLICSPECGLOSSMAP";
        public const string OcclusionMap                    = "_OCCLUSIONMAP";
        public const string SmoothnessTextureAlbedoChannelA = "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A";
        public const string SpecularHighlightsOff           = "_SPECULARHIGHLIGHTS_OFF";
        public const string EnvironmentReflectionsOff       = "_ENVIRONMENTREFLECTIONS_OFF";
        public const string SpecularSetup                   = "_SPECULAR_SETUP";
        public const string ReceiveShadowsOff               = "_RECEIVE_SHADOWS_OFF";

        // Fill-only features (Line.shader does not declare these — flat +Y normal, no detail/parallax/surface-type).
        public const string NormalMap                = "_NORMALMAP";
        public const string ParallaxMap              = "_PARALLAXMAP";
        public const string DetailMulx2              = "_DETAIL_MULX2";
        public const string DetailScaled             = "_DETAIL_SCALED";
        public const string SurfaceTypeTransparent   = "_SURFACE_TYPE_TRANSPARENT";
        public const string AlphaTestOn              = "_ALPHATEST_ON";
        public const string AlphaPremultiplyOn       = "_ALPHAPREMULTIPLY_ON";
        public const string AlphaModulateOn          = "_ALPHAMODULATE_ON";
    }
}
