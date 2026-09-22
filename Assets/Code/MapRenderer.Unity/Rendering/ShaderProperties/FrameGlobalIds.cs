// Cached ids for the frame GLOBALS — OUTSIDE the parity-swept registry files. See FrameGlobalNames.cs for
// why: a frame global is not a ShaderLab property on either layer, so folding these into
// ShaderProperties/PropertyNames.cs would red the exact-count and Properties{}-union structural tests.
//
// NOT named PropertyId.cs: NoRawStringMaterialAccessGuardTests.PropertyIdFiles_NoBareLiterals globs that
// filename recursively under ShaderProperties/. The distinct name keeps the exemption legible.

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
