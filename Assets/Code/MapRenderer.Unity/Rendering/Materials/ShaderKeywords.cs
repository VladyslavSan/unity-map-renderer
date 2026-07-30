namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// Single source of truth for the shader-feature <b>keyword</b> names the map shaders declare
    /// (<c>#pragma shader_feature_local*</c> in Fill.shader / Line.shader).
    ///
    /// <para>These are derived from a material's property values by the editor keyword sync
    /// (<c>BaseShaderGUI.ValidateMaterial</c> → <c>LitShaderGUI.ValidateMaterial</c> →
    /// <c>LineShaderGUI.ValidateMaterial</c>, each adding the level's own keywords) — the clean-room
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

        // Surface / detail features. NOT fill-only, despite what this comment used to say: Line.shader
        // declares _NORMALMAP, _PARALLAXMAP, _DETAIL_MULX2, _DETAIL_SCALED and _SURFACE_TYPE_TRANSPARENT
        // too (:181-187), and MapLine.mat carries _SURFACE_TYPE_TRANSPARENT as a live keyword.
        public const string NormalMap                = "_NORMALMAP";
        public const string ParallaxMap              = "_PARALLAXMAP";
        public const string DetailMulx2              = "_DETAIL_MULX2";
        public const string DetailScaled             = "_DETAIL_SCALED";
        public const string SurfaceTypeTransparent   = "_SURFACE_TYPE_TRANSPARENT";
        public const string AlphaTestOn              = "_ALPHATEST_ON";
        public const string AlphaPremultiplyOn       = "_ALPHAPREMULTIPLY_ON";
        public const string AlphaModulateOn          = "_ALPHAMODULATE_ON";

        // Line-only features (Fill.shader does not declare these).
        // _OFF polarity is deliberate: shader_feature_local variants are stripped from a player build
        // unless some material in the build declares the keyword, so the SHIPPING (AA-on) variant is the
        // one that carries no keyword and can never be stripped. Inverting it would make AA work in the
        // Editor and silently vanish in a build.
        public const string EdgeAntialiasingOff = "_EDGE_ANTIALIASING_OFF";

        // Hairline strategy — a keyword SET whose default member is `_` (no keyword). Same strip-safety
        // argument as _OFF polarity above, reached differently: the shipping variant is the unnamed one, so
        // it carries nothing that could be stripped from a player build. Never make the default a named
        // keyword.
        public const string HairlineHard = "_HAIRLINE_HARD";
        public const string HairlineSolidCore = "_HAIRLINE_SOLID_CORE";
    }
}
