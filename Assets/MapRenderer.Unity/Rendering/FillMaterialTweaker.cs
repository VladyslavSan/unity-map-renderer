using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Unity.Rendering
{
    /// <summary>
    /// Fill material tweaker — the runtime render-state contract for a fill material (keyword sync is editor-
    /// side, in <c>FillShaderGUI</c>). STATIC. The painter contract adds the fill's OPAQUE blend on top of the
    /// shared base contract. (S58)
    /// </summary>
    public static class FillMaterialTweaker
    {
        /// <summary>
        /// Re-asserts the fill compositing contract after cloning a base <c>.mat</c> (replaces S57's
        /// magic-string <c>SetFloat</c> calls): shared base contract (no depth write, LEqual test, white
        /// identity) + OPAQUE <c>One/Zero</c> blend (each fill layer overwrites in painter's order — code-
        /// owned, overrides any stale premultiply blend a base .mat may carry). Render state + identity ONLY,
        /// not keyword sync (a clone inherits the base's import-baked keywords).
        /// </summary>
        public static void ApplyPainterContract(Material m)
        {
            BaseMaterialTweaker.ApplyBaseContract(m);
            m.SetBlend(BlendMode.One, BlendMode.Zero);
        }
    }
}
