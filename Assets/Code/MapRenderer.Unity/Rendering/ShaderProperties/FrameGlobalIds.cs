// Frame-global ids stay out of the parity-swept registry files; FrameGlobalNames.cs says why.
// Not named PropertyId.cs, which PropertyIdFiles_NoBareLiterals globs under ShaderProperties/.

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
