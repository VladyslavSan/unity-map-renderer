// Cached ids for the frame GLOBALS — deliberately OUTSIDE the parity-swept registry files (S110).
//
// Same reasoning as FrameGlobalNames.cs: these names are frame globals pushed with Shader.SetGlobalFloat,
// deliberately absent from both shaders' Properties{} blocks and from CBUFFER_START(UnityPerMaterial).
// The registry-parity contract is "every registry name is a ShaderLab property on both layers", which a
// global is not — so folding this into ShaderProperties/PropertyNames.cs (or Line/PropertyNames.cs) would
// red the exact-count and Properties{}-union structural tests.
//
// NOT named PropertyId.cs on purpose: NoRawStringMaterialAccessGuardTests.PropertyIdFiles_NoBareLiterals
// globs that filename recursively under ShaderProperties/. The distinct name keeps the exemption legible.

using UnityEngine;

namespace MapRenderer.Unity.Rendering.ShaderProperties
{
    /// <summary>
    /// Cached <c>Shader.PropertyToID</c> integer ids for every name in <see cref="FrameGlobalNames"/>.
    /// Use these for all <c>Shader.SetGlobal*</c> calls.
    /// </summary>
    public static class FrameGlobalIds
    {
        /// <inheritdoc cref="FrameGlobalNames.MapFrameMetersPerDevicePixel"/>
        public static readonly int MapFrameMetersPerDevicePixel =
            Shader.PropertyToID(FrameGlobalNames.MapFrameMetersPerDevicePixel);
    }
}
