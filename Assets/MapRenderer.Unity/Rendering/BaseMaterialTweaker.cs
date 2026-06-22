using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Unity.Rendering
{
    /// <summary>
    /// Base material tweaker — the minimal <b>runtime</b> render-state contract shared by every map material.
    /// Deliberately small: full material setup (keyword sync, all the URP-Lit surface inputs) lives editor-side
    /// in the shader GUIs (<c>BaseShaderGUI</c>/<c>LitShaderGUI</c>); at runtime we only re-assert the generic
    /// states a cloned <c>.mat</c> must carry — depth write/test and the colour identity — leaving the per-type
    /// blend to <see cref="FillMaterialTweaker"/> / <see cref="LineMaterialTweaker"/>. (S58)
    /// </summary>
    public static class BaseMaterialTweaker
    {
        /// <summary>
        /// Re-asserts the render-state + color-identity contract common to all map materials: painter's-
        /// algorithm depth state (no depth write, LEqual test) + white <c>_BaseColor</c> identity (per-layer
        /// color rides the vertex bake for data-driven layers, or the applier binds <c>_BaseColor</c> for
        /// constant layers). Blend is left to the feature tweaker (fill = opaque, line = alpha).
        /// </summary>
        public static void ApplyBaseContract(Material m)
        {
            m.SetDepthWrite(DepthWrite.Off);
            m.SetDepthTest(CompareFunction.LessEqual);
            m.SetColor(ShaderProperties.BaseColor, Color.white);
        }
    }
}
