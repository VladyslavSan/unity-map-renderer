using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Unity.Rendering
{
    /// <summary>
    /// Low-level typed setters/getters for a material's forward-pass render state. Maps Unity rendering
    /// enums → the underlying ShaderLab int properties
    /// (<c>_ZWrite</c>/<c>_ZTest</c>/<c>_Cull</c>/<c>_SrcBlend</c>/<c>_DstBlend</c>/<c>_BlendOp</c>).
    ///
    /// <para>Together with <see cref="ShaderProperties"/> this is the ONLY place the render-state
    /// property names are touched — the tweakers compose these, the GUI calls the tweakers (S58). All
    /// setters guard with <see cref="Material.HasProperty(string)"/> so they are no-ops on a material
    /// whose shader lacks the knob.</para>
    /// </summary>
    public static class MaterialRenderStateExtensions
    {
        // ── Depth write (ZWrite) ──
        public static void SetDepthWrite(this Material m, DepthWrite value)
        {
            if (m.HasProperty(ShaderProperties.ZWrite))
                m.SetFloat(ShaderProperties.ZWrite, (int)value);
        }

        public static DepthWrite GetDepthWrite(this Material m)
            => (DepthWrite)(int)m.GetFloat(ShaderProperties.ZWrite);

        // ── Depth test (ZTest) ──
        public static void SetDepthTest(this Material m, CompareFunction value)
        {
            if (m.HasProperty(ShaderProperties.ZTest))
                m.SetFloat(ShaderProperties.ZTest, (int)value);
        }

        public static CompareFunction GetDepthTest(this Material m)
            => (CompareFunction)(int)m.GetFloat(ShaderProperties.ZTest);

        // ── Cull ──
        public static void SetCull(this Material m, CullMode value)
        {
            if (m.HasProperty(ShaderProperties.CullMode))
                m.SetFloat(ShaderProperties.CullMode, (int)value);
        }

        public static CullMode GetCull(this Material m)
            => (CullMode)(int)m.GetFloat(ShaderProperties.CullMode);

        // ── Blend (RGB == alpha factors) ──
        public static void SetBlend(this Material m, BlendMode src, BlendMode dst)
        {
            if (m.HasProperty(ShaderProperties.SrcBlend)) m.SetFloat(ShaderProperties.SrcBlend, (int)src);
            if (m.HasProperty(ShaderProperties.DstBlend)) m.SetFloat(ShaderProperties.DstBlend, (int)dst);
        }

        /// <summary>Sets RGB and alpha blend factors independently.</summary>
        public static void SetBlend(this Material m, BlendMode srcRGB, BlendMode dstRGB, BlendMode srcA, BlendMode dstA)
        {
            if (m.HasProperty(ShaderProperties.SrcBlend))      m.SetFloat(ShaderProperties.SrcBlend, (int)srcRGB);
            if (m.HasProperty(ShaderProperties.DstBlend))      m.SetFloat(ShaderProperties.DstBlend, (int)dstRGB);
            if (m.HasProperty(ShaderProperties.SrcBlendAlpha)) m.SetFloat(ShaderProperties.SrcBlendAlpha, (int)srcA);
            if (m.HasProperty(ShaderProperties.DstBlendAlpha)) m.SetFloat(ShaderProperties.DstBlendAlpha, (int)dstA);
        }

        public static BlendMode GetSrcBlend(this Material m)
            => (BlendMode)(int)m.GetFloat(ShaderProperties.SrcBlend);

        public static BlendMode GetDstBlend(this Material m)
            => (BlendMode)(int)m.GetFloat(ShaderProperties.DstBlend);

        // ── Blend op ──
        public static void SetBlendOp(this Material m, BlendOp value)
        {
            if (m.HasProperty(ShaderProperties.BlendOp))
                m.SetFloat(ShaderProperties.BlendOp, (int)value);
        }

        public static BlendOp GetBlendOp(this Material m)
            => (BlendOp)(int)m.GetFloat(ShaderProperties.BlendOp);
    }
}
