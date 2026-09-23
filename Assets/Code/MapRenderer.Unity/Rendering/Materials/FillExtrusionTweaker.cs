using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// Fill-extrusion material tweaker — the runtime render-state contract for a 3D building material. STATIC.
    /// Fill-extrusion is the first <b>elevated-3D</b> layer, so unlike the FILL/LINE painter contracts
    /// (transparent, <c>ZWrite Off</c>) it WRITES depth and composites opaque: overlapping buildings — and a
    /// single building's own near/far walls, which share one mesh and cannot be back-to-front sorted — occlude
    /// via the depth buffer instead of draw order. See <c>docs/depth-and-render-regimes-design.md</c>.
    /// </summary>
    public static class FillExtrusionTweaker
    {
        /// <summary>
        /// Re-asserts the elevated-3D contract after cloning the base <c>.mat</c>: depth WRITE on,
        /// <c>LEqual</c> test, opaque <c>One/Zero</c> blend, white <c>_BaseColor</c> identity, and the
        /// transparent surface keyword DISABLED. Cull is left to the <c>.mat</c> (Back, calibrated for
        /// stock Cull Back in <c>StyledFillExtrusionTileBuilder</c>). <c>MaterialFactory.CreateFillExtrusionMaterial</c>
        /// applies it in place of its counterpart, <see cref="FillTweaker.ApplyPainterContract"/>.
        /// </summary>
        /// <param name="m">The cloned fill-extrusion material to re-assert the contract on.</param>
        public static void ApplyElevatedContract(Material m)
        {
            m.SetDepthWrite(DepthWrite.On);
            m.SetDepthTest(CompareFunction.LessEqual);
            m.SetBlend(BlendMode.One, BlendMode.Zero, BlendMode.One, BlendMode.Zero);
            m.SetColor(ShaderProperties.PropertyId.BaseColor, Color.white);
            m.DisableKeyword(FillTweaker.SurfaceTypeTransparentKeyword);
        }
    }
}
