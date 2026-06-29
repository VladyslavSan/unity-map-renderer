namespace MapRenderer.Unity.Rendering.ShaderProperties
{
    /// <summary>
    /// Canonical string names for the shader properties shared by both <c>Map/Fill</c> and <c>Map/Line</c>
    /// (and the base URP Lit surface — S78). Every property in this class appears in both shaders'
    /// <c>Properties{}</c> blocks and/or the shared <c>CBUFFER_START(UnityPerMaterial)</c> segment.
    ///
    /// <para>Use <see cref="PropertyId"/> for <c>Material.Set/Get/Has</c> calls (cached int ids).
    /// Use this class only where the Unity API requires a string: <c>MaterialEditor.FindProperty</c>,
    /// serialisation.</para>
    ///
    /// <para>Layer-specific names live in <see cref="Line.PropertyNames"/> and
    /// <see cref="Fill.PropertyNames"/>.</para>
    /// </summary>
    public static class PropertyNames
    {
        // region: CBUFFER (UnityPerMaterial) — shared instanced members
        // These appear in BOTH Line_LitInput.hlsl and Fill_LitInput.hlsl CBUFFER blocks (after
        // stripping the SRP companion entries _*_ST / _*_TexelSize). 14 entries.
        public const string BaseColor            = "_BaseColor";
        public const string SpecColor            = "_SpecColor";
        public const string EmissionColor        = "_EmissionColor";
        public const string Cutoff               = "_Cutoff";
        public const string Smoothness           = "_Smoothness";
        public const string Metallic             = "_Metallic";
        public const string BumpScale            = "_BumpScale";
        public const string Parallax             = "_Parallax";
        public const string OcclusionStrength    = "_OcclusionStrength";
        public const string ClearCoatMask        = "_ClearCoatMask";
        public const string ClearCoatSmoothness  = "_ClearCoatSmoothness";
        public const string DetailAlbedoMapScale = "_DetailAlbedoMapScale";
        public const string DetailNormalMapScale = "_DetailNormalMapScale";
        public const string Opacity              = "_Opacity";

        // region: Render-state ShaderLab knobs — NOT in CBUFFER
        // Set via SetFloat with the int id; these are ShaderLab render-state properties, not
        // CBUFFER members. 9 entries.
        public const string ZWrite        = "_ZWrite";
        public const string ZTest         = "_ZTest";
        public const string CullMode      = "_Cull";
        public const string SrcBlend      = "_SrcBlend";
        public const string DstBlend      = "_DstBlend";
        public const string SrcBlendAlpha = "_SrcBlendAlpha";
        public const string DstBlendAlpha = "_DstBlendAlpha";
        public const string BlendOp       = "_BlendOp";
        public const string AlphaToMask   = "_AlphaToMask";

        // region: Textures / surface bookkeeping — Properties{} only, NOT in CBUFFER
        // These live in the shader Properties{} block for inspector display / keyword derivation
        // but are NOT instanced through the UnityPerMaterial CBUFFER. 18 entries.
        public const string Surface                 = "_Surface";
        public const string Blend                   = "_Blend";
        public const string AlphaClip               = "_AlphaClip";
        public const string ReceiveShadows          = "_ReceiveShadows";
        public const string BaseMap                 = "_BaseMap";
        public const string WorkflowMode            = "_WorkflowMode";
        public const string MetallicGlossMap        = "_MetallicGlossMap";
        public const string SpecGlossMap            = "_SpecGlossMap";
        public const string SmoothnessTextureChannel = "_SmoothnessTextureChannel";
        public const string BumpMap                 = "_BumpMap";
        public const string ParallaxMap             = "_ParallaxMap";
        public const string OcclusionMap            = "_OcclusionMap";
        public const string EmissionMap             = "_EmissionMap";
        public const string SpecularHighlights      = "_SpecularHighlights";
        public const string EnvironmentReflections  = "_EnvironmentReflections";
        public const string DetailMask              = "_DetailMask";
        public const string DetailAlbedoMap         = "_DetailAlbedoMap";
        public const string DetailNormalMap         = "_DetailNormalMap";
    }
}
