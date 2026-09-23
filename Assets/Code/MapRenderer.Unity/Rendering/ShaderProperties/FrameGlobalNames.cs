// Frame GLOBALS — OUTSIDE the parity-swept registry files. Non-local invariant: every name in
// PropertyNames.cs / Line/PropertyNames.cs / Fill/PropertyNames.cs is contractually a ShaderLab property
// declared in that layer's Properties{} block, and MaterialPropertyRegistryParityTests requires each one
// to appear there. A frame global is the opposite: pushed once per frame with Shader.SetGlobalFloat, and
// not a CBUFFER member. It must not be a Properties{} entry either: a ShaderLab property would serialise
// a per-material value into MapLine.mat that shadows the global. The file is named FrameGlobalNames.cs,
// not PropertyNames.cs, because the parity tests address the three registry files by explicit path.

namespace MapRenderer.Unity.Rendering.ShaderProperties
{
    /// <summary>
    /// Canonical string names for the map's per-frame shader GLOBALS — values pushed once per frame via
    /// <c>Shader.SetGlobalFloat</c> and read by every material that declares them, rather than bound per
    /// material. Use <see cref="FrameGlobalIds"/> for the actual <c>Shader.SetGlobal*</c> calls (cached
    /// int ids). See the file header for why these names do not live in <see cref="PropertyNames"/>.
    /// </summary>
    public static class FrameGlobalNames
    {
        /// <summary>World metres per DEVICE pixel — the line shader's view-independent ruler, pushed by
        /// <c>MapCamera.SyncToCamera</c> and declared outside the CBUFFER in <c>Line_LitInput.hlsl</c>, so it
        /// stays a true global under the SRP Batcher and BRG/DOTS instancing. The styled WIDTH family and the
        /// dash divisor read it; the AA straddle pad, the min-width floor and <c>line-translate</c> are
        /// sampling-grid quantities and keep the per-vertex <c>MapPixelsToWorld</c> measurement.</summary>
        public const string MapFrameMetersPerDevicePixel = "_MapFrameMetersPerDevicePixel";
    }
}
