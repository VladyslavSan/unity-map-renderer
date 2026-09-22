using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// Line material tweaker — the runtime render-state contract for a line material (keyword sync is editor-
    /// side, in <c>LineShaderGUI</c>). STATIC. The painter contract adds the line's STRAIGHT-ALPHA blend on top
    /// of the shared base contract.
    /// </summary>
    public static class LineTweaker
    {
        /// <summary>
        /// Re-asserts the line compositing contract after cloning a base <c>.mat</c>: shared base contract
        /// (no depth write, LEqual test, white identity) + STRAIGHT-ALPHA <c>SrcAlpha/OneMinusSrcAlpha</c>
        /// blend. Code-owned: it overrides the stale premultiplied <c>_SrcBlend=One</c> that URP's
        /// ValidateMaterial bakes into the base .mat. Render state + identity ONLY, not keyword sync.
        /// </summary>
        public static void ApplyPainterContract(Material m)
        {
            BaseTweaker.ApplyBaseContract(m);
            m.SetBlend(BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, BlendMode.One, BlendMode.OneMinusSrcAlpha);
        }
    }
}
