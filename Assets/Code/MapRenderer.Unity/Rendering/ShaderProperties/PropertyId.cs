using UnityEngine;

namespace MapRenderer.Unity.Rendering.ShaderProperties
{
    /// <summary>
    /// Cached <c>Shader.PropertyToID</c> integer ids for every property in <see cref="PropertyNames"/>.
    /// Use these for all <c>Material.SetFloat/SetColor/SetVector/SetTexture/HasProperty/GetFloat/…</c>
    /// calls — they eliminate the per-call string hash lookup that the string overloads incur.
    ///
    /// <para>Ids are computed once via <see langword="static"/> field initializers (Unity guarantees
    /// <c>Shader.PropertyToID</c> is safe to call from any thread after the engine is initialised).</para>
    ///
    /// <para>For APIs that require a string (<c>MaterialEditor.FindProperty</c>, serialisation) use
    /// <see cref="PropertyNames"/> directly.</para>
    /// </summary>
    public static class PropertyId
    {
        // ── CBUFFER (UnityPerMaterial) — shared instanced members ──
        public static readonly int BaseColor            = Shader.PropertyToID(PropertyNames.BaseColor);
        public static readonly int SpecColor            = Shader.PropertyToID(PropertyNames.SpecColor);
        public static readonly int EmissionColor        = Shader.PropertyToID(PropertyNames.EmissionColor);
        public static readonly int Cutoff               = Shader.PropertyToID(PropertyNames.Cutoff);
        public static readonly int Smoothness           = Shader.PropertyToID(PropertyNames.Smoothness);
        public static readonly int Metallic             = Shader.PropertyToID(PropertyNames.Metallic);
        public static readonly int BumpScale            = Shader.PropertyToID(PropertyNames.BumpScale);
        public static readonly int Parallax             = Shader.PropertyToID(PropertyNames.Parallax);
        public static readonly int OcclusionStrength    = Shader.PropertyToID(PropertyNames.OcclusionStrength);
        public static readonly int ClearCoatMask        = Shader.PropertyToID(PropertyNames.ClearCoatMask);
        public static readonly int ClearCoatSmoothness  = Shader.PropertyToID(PropertyNames.ClearCoatSmoothness);
        public static readonly int DetailAlbedoMapScale = Shader.PropertyToID(PropertyNames.DetailAlbedoMapScale);
        public static readonly int DetailNormalMapScale = Shader.PropertyToID(PropertyNames.DetailNormalMapScale);
        public static readonly int Opacity              = Shader.PropertyToID(PropertyNames.Opacity);

        // ── Render-state ShaderLab knobs — NOT in CBUFFER ──
        public static readonly int ZWrite        = Shader.PropertyToID(PropertyNames.ZWrite);
        public static readonly int ZTest         = Shader.PropertyToID(PropertyNames.ZTest);
        public static readonly int CullMode      = Shader.PropertyToID(PropertyNames.CullMode);
        public static readonly int SrcBlend      = Shader.PropertyToID(PropertyNames.SrcBlend);
        public static readonly int DstBlend      = Shader.PropertyToID(PropertyNames.DstBlend);
        public static readonly int SrcBlendAlpha = Shader.PropertyToID(PropertyNames.SrcBlendAlpha);
        public static readonly int DstBlendAlpha = Shader.PropertyToID(PropertyNames.DstBlendAlpha);
        public static readonly int BlendOp       = Shader.PropertyToID(PropertyNames.BlendOp);
        public static readonly int AlphaToMask   = Shader.PropertyToID(PropertyNames.AlphaToMask);

        // ── Textures / surface bookkeeping — Properties{} only, NOT in CBUFFER ──
        public static readonly int Surface                  = Shader.PropertyToID(PropertyNames.Surface);
        public static readonly int Blend                    = Shader.PropertyToID(PropertyNames.Blend);
        public static readonly int AlphaClip                = Shader.PropertyToID(PropertyNames.AlphaClip);
        public static readonly int ReceiveShadows           = Shader.PropertyToID(PropertyNames.ReceiveShadows);
        public static readonly int BaseMap                  = Shader.PropertyToID(PropertyNames.BaseMap);
        public static readonly int WorkflowMode             = Shader.PropertyToID(PropertyNames.WorkflowMode);
        public static readonly int MetallicGlossMap         = Shader.PropertyToID(PropertyNames.MetallicGlossMap);
        public static readonly int SpecGlossMap             = Shader.PropertyToID(PropertyNames.SpecGlossMap);
        public static readonly int SmoothnessTextureChannel = Shader.PropertyToID(PropertyNames.SmoothnessTextureChannel);
        public static readonly int BumpMap                  = Shader.PropertyToID(PropertyNames.BumpMap);
        public static readonly int ParallaxMap              = Shader.PropertyToID(PropertyNames.ParallaxMap);
        public static readonly int OcclusionMap             = Shader.PropertyToID(PropertyNames.OcclusionMap);
        public static readonly int EmissionMap              = Shader.PropertyToID(PropertyNames.EmissionMap);
        public static readonly int SpecularHighlights       = Shader.PropertyToID(PropertyNames.SpecularHighlights);
        public static readonly int EnvironmentReflections   = Shader.PropertyToID(PropertyNames.EnvironmentReflections);
        public static readonly int DetailMask               = Shader.PropertyToID(PropertyNames.DetailMask);
        public static readonly int DetailAlbedoMap          = Shader.PropertyToID(PropertyNames.DetailAlbedoMap);
        public static readonly int DetailNormalMap          = Shader.PropertyToID(PropertyNames.DetailNormalMap);
    }
}
