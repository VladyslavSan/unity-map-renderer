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
        /// Re-asserts the fill compositing contract after cloning a base <c>.mat</c>: shared base contract
        /// (no depth write, LEqual test, white identity) + straight ALPHA blend (each fill layer composites
        /// in painter's order — code-owned, overriding any stale blend a base .mat may carry).
        ///
        /// <para>Also enables <c>_SURFACE_TYPE_TRANSPARENT</c>. That is a keyword, and this class otherwise
        /// leaves keywords to the import-baked ones — but this one is not styling, it is the other half of
        /// the blend above. URP 17 deprecated the <c>_Surface</c> property: <c>IsSurfaceTypeTransparent()</c>
        /// reads the KEYWORD and falls back to a hardcoded 0 (see URP's <c>Shaders/Utils/SurfaceType.hlsl</c>),
        /// and <c>Fill_LitForwardPass</c> ends with <c>color.a = OutputAlpha(color.a,
        /// IsSurfaceTypeTransparent())</c> — which returns 1.0 for an opaque surface. Without the keyword the
        /// fragment's alpha is discarded and the blend set here is a no-op: fills render solid however
        /// transparent their colour, opacity or pattern says they are.</para>
        ///
        /// <para>The keyword is also baked into <c>MapFill.mat</c>, and that is load-bearing rather than
        /// redundant: <c>shader_feature_local_fragment</c> variants are stripped from player builds unless a
        /// material in the build declares the keyword, so enabling it only at runtime would work in the
        /// Editor and silently fall back to the opaque variant in a build.</para>
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
