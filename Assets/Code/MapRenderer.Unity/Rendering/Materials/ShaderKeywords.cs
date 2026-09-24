namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// Single source of truth for the shader-feature <b>keyword</b> names the map shaders declare
    /// (<c>#pragma shader_feature_local*</c> in Fill.shader / Line.shader). The editor keyword sync
    /// (<c>BaseShaderGUI.ValidateMaterial</c> and its overrides) derives them from a material's property
    /// values. The names live in the runtime assembly so the const strings are shareable. Only the keywords
    /// these shaders use are declared.
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

        // Surface / detail features — declared by Fill.shader AND Line.shader (:181-187), and
        // MapLine.mat carries _SURFACE_TYPE_TRANSPARENT as a live keyword.
        public const string NormalMap                = "_NORMALMAP";
        public const string ParallaxMap              = "_PARALLAXMAP";
        public const string DetailMulx2              = "_DETAIL_MULX2";
        public const string DetailScaled             = "_DETAIL_SCALED";
        public const string SurfaceTypeTransparent   = "_SURFACE_TYPE_TRANSPARENT";
        public const string AlphaTestOn              = "_ALPHATEST_ON";
        public const string AlphaPremultiplyOn       = "_ALPHAPREMULTIPLY_ON";
        public const string AlphaModulateOn          = "_ALPHAMODULATE_ON";

        // Line-only. Non-obvious why: a build strips a keyword variant no material declares, so the SHIPPING
        // (AA-on) variant carries no keyword; inverting the _OFF polarity would lose AA in a player build.
        public const string EdgeAntialiasingOff = "_EDGE_ANTIALIASING_OFF";

        // Hairline strategy — a keyword SET whose default member is `_` (no keyword). Same strip safety as
        // the _OFF polarity above: the shipping variant is the unnamed one. Never name the default.
        public const string HairlineHard = "_HAIRLINE_HARD";
        public const string HairlineSolidCore = "_HAIRLINE_SOLID_CORE";
    }
}
