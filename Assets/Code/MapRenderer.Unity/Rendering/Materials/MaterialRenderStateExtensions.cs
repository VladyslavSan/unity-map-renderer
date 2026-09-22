using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// Low-level typed setters/getters for a material's forward-pass render state. Maps Unity rendering
    /// enums → the underlying ShaderLab int properties
    /// (<c>_ZWrite</c>/<c>_ZTest</c>/<c>_Cull</c>/<c>_SrcBlend</c>/<c>_DstBlend</c>/<c>_BlendOp</c>).
    ///
    /// <para>Together with <see cref="ShaderProperties.PropertyId"/> this is the ONLY place the render-state
    /// property ids are touched — the tweakers compose these, the GUI calls the tweakers. All
    /// setters guard with <see cref="Material.HasProperty(int)"/> so they are no-ops on a material
    /// whose shader lacks the knob.</para>
    /// </summary>
    public static class MaterialRenderStateExtensions
    {
        // ── Depth write (ZWrite) ──
        public static void SetDepthWrite(this Material m, DepthWrite value)
        {
            if (m.HasProperty(ShaderProperties.PropertyId.ZWrite))
                m.SetFloat(ShaderProperties.PropertyId.ZWrite, (int)value);
        }

        public static DepthWrite GetDepthWrite(this Material m)
            => (DepthWrite)(int)m.GetFloat(ShaderProperties.PropertyId.ZWrite);

        // ── Depth test (ZTest) ──
        public static void SetDepthTest(this Material m, CompareFunction value)
        {
            if (m.HasProperty(ShaderProperties.PropertyId.ZTest))
                m.SetFloat(ShaderProperties.PropertyId.ZTest, (int)value);
        }

        public static CompareFunction GetDepthTest(this Material m)
            => (CompareFunction)(int)m.GetFloat(ShaderProperties.PropertyId.ZTest);

        // ── Cull ──
        public static void SetCull(this Material m, CullMode value)
        {
            if (m.HasProperty(ShaderProperties.PropertyId.CullMode))
                m.SetFloat(ShaderProperties.PropertyId.CullMode, (int)value);
        }

        public static CullMode GetCull(this Material m)
            => (CullMode)(int)m.GetFloat(ShaderProperties.PropertyId.CullMode);

        // ── Blend (RGB == alpha factors) ──
        public static void SetBlend(this Material m, BlendMode src, BlendMode dst)
        {
            if (m.HasProperty(ShaderProperties.PropertyId.SrcBlend)) m.SetFloat(ShaderProperties.PropertyId.SrcBlend, (int)src);
            if (m.HasProperty(ShaderProperties.PropertyId.DstBlend)) m.SetFloat(ShaderProperties.PropertyId.DstBlend, (int)dst);
        }

        /// <summary>Sets RGB and alpha blend factors independently.</summary>
        public static void SetBlend(this Material m, BlendMode srcRGB, BlendMode dstRGB, BlendMode srcA, BlendMode dstA)
        {
            if (m.HasProperty(ShaderProperties.PropertyId.SrcBlend))      m.SetFloat(ShaderProperties.PropertyId.SrcBlend, (int)srcRGB);
            if (m.HasProperty(ShaderProperties.PropertyId.DstBlend))      m.SetFloat(ShaderProperties.PropertyId.DstBlend, (int)dstRGB);
            if (m.HasProperty(ShaderProperties.PropertyId.SrcBlendAlpha)) m.SetFloat(ShaderProperties.PropertyId.SrcBlendAlpha, (int)srcA);
            if (m.HasProperty(ShaderProperties.PropertyId.DstBlendAlpha)) m.SetFloat(ShaderProperties.PropertyId.DstBlendAlpha, (int)dstA);
        }

        public static BlendMode GetSrcBlend(this Material m)
            => (BlendMode)(int)m.GetFloat(ShaderProperties.PropertyId.SrcBlend);

        public static BlendMode GetDstBlend(this Material m)
            => (BlendMode)(int)m.GetFloat(ShaderProperties.PropertyId.DstBlend);

        // ── Blend op ──
        public static void SetBlendOp(this Material m, BlendOp value)
        {
            if (m.HasProperty(ShaderProperties.PropertyId.BlendOp))
                m.SetFloat(ShaderProperties.PropertyId.BlendOp, (int)value);
        }

        public static BlendOp GetBlendOp(this Material m)
            => (BlendOp)(int)m.GetFloat(ShaderProperties.PropertyId.BlendOp);
    }
}
