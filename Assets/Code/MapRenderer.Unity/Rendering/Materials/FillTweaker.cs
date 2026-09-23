using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// Fill material tweaker — the runtime render-state contract for a fill material (styling keyword sync is
    /// editor-side, in <c>FillShaderGUI</c>). STATIC. The painter contract adds the fill's ALPHA blend and
    /// transparent surface type on top of the shared base contract.
    /// </summary>
    public static class FillTweaker
    {
        public const BlendMode DefaultSrcRGBBlend   = BlendMode.SrcAlpha;
        public const BlendMode DefaultDstRGBBlend   = BlendMode.OneMinusSrcAlpha;
        public const BlendMode DefaultSrcAlphaBlend = BlendMode.One;
        public const BlendMode DefaultDstAlphaBlend = BlendMode.OneMinusSrcAlpha;


        /// <summary>
        /// Re-asserts the fill compositing contract: shared base contract (no depth write, LEqual test,
        /// white identity) + straight ALPHA blend, plus the <c>_SURFACE_TYPE_TRANSPARENT</c> keyword —
        /// without it the fragment's alpha is discarded and fills render solid regardless of colour,
        /// opacity or pattern. Non-local invariant: the keyword is also baked into <c>MapFill.mat</c>,
        /// because <c>shader_feature_local_fragment</c> variants are stripped from player builds unless a
        /// material in the build declares it — enabling it only at runtime falls back to opaque in a build.
        /// </summary>
        public static void ApplyPainterContract(Material m)
        {
            BaseTweaker.ApplyBaseContract(m);
            m.SetBlend(DefaultSrcRGBBlend, DefaultDstRGBBlend, DefaultSrcAlphaBlend, DefaultDstAlphaBlend);
            m.EnableKeyword(SurfaceTypeTransparentKeyword);
        }

        /// <summary>URP's transparent-surface shader feature — see <see cref="ApplyPainterContract"/>.</summary>
        public const string SurfaceTypeTransparentKeyword = "_SURFACE_TYPE_TRANSPARENT";
    }
}
