using UnityEngine;
using UnityEngine.Rendering;

namespace MapRenderer.Unity.Rendering
{
    /// <summary>
    /// ShaderLab depth-write as a typed value. Unity ships no enum for ZWrite (it is On/Off in
    /// ShaderLab ⇔ 1/0 as a material float), so we declare one. Values match the ShaderLab ints.
    /// </summary>
    public enum DepthWrite
    {
        /// <summary>ZWrite Off — do not write to the depth buffer (painter's-algorithm layers).</summary>
        Off = 0,

        /// <summary>ZWrite On — write depth (opaque geometry).</summary>
        On = 1,
    }

    /// <summary>
    /// A declarative bundle of a material's forward-pass render state, expressible as data and applied
    /// in one call via <see cref="ApplyTo"/>. Reuses Unity's rendering enums
    /// (<see cref="CompareFunction"/> for ZTest, <see cref="CullMode"/>, <see cref="BlendMode"/>,
    /// <see cref="BlendOp"/>) so callers never write magic ints. Composed by the material tweakers (S58).
    /// </summary>
    public struct MapRenderState
    {
        /// <summary>ZWrite.</summary>
        public DepthWrite DepthWrite;

        /// <summary>ZTest comparison.</summary>
        public CompareFunction DepthTest;

        /// <summary>Cull mode.</summary>
        public CullMode Cull;

        /// <summary>Source blend factor (RGB and alpha).</summary>
        public BlendMode SrcBlend;

        /// <summary>Destination blend factor (RGB and alpha).</summary>
        public BlendMode DstBlend;

        /// <summary>Source blend factor (RGB and alpha).</summary>
        public BlendMode SrcBlendAlpha;

        /// <summary>Destination blend factor (RGB and alpha).</summary>
        public BlendMode DstBlendAlpha;

        /// <summary>Blend operation.</summary>
        public BlendOp BlendOp;

        /// <summary>Writes every field onto <paramref name="material"/> via the typed setters.</summary>
        public void ApplyTo(Material material)
        {
            material.SetDepthWrite(DepthWrite);
            material.SetDepthTest(DepthTest);
            material.SetCull(Cull);
            material.SetBlend(SrcBlend, DstBlend, SrcBlendAlpha, DstBlendAlpha);
            material.SetBlendOp(BlendOp);
        }
    }
}