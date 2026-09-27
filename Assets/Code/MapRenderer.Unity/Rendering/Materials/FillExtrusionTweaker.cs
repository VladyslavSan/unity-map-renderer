using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// Fill-extrusion material tweaker — the runtime render-state contract for a 3D building material.
    /// STATIC, applied ONCE at creation. Fill-extrusion always writes depth AND always blends: at opacity 1
    /// the blend is a plain overwrite, so it never needs a separate opaque path. See
    /// <c>docs/depth-and-render-regimes-design.md</c> § 6 (E).
    /// </summary>
    public static class FillExtrusionTweaker
    {
        /// <summary>
        /// Re-asserts the fill-extrusion contract after cloning the base <c>.mat</c>: depth WRITE on,
        /// <c>LEqual</c> test, straight alpha blend, transparent surface keyword OFF. The keyword stays OFF
        /// so URP's SSAO, decals and screen-space shadows keep running as they would for an opaque draw;
        /// the forward passes bypass URP's own keyword-gated alpha output and write their own instead.
        /// </summary>
        /// <param name="m">The cloned fill-extrusion material to apply the contract to.</param>
        public static void ApplyContract(Material m)
        {
            m.SetDepthWrite(DepthWrite.On);
            m.SetDepthTest(CompareFunction.LessEqual);
            m.SetBlend(FillTweaker.DefaultSrcRGBBlend, FillTweaker.DefaultDstRGBBlend,
                FillTweaker.DefaultSrcAlphaBlend, FillTweaker.DefaultDstAlphaBlend);
            m.DisableKeyword(FillTweaker.SurfaceTypeTransparentKeyword);
        }
    }
}
