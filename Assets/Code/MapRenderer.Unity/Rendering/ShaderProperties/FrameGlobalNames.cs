// Frame GLOBALS — deliberately OUTSIDE the parity-swept registry files (S110).
//
// Every name in PropertyNames.cs / Line/PropertyNames.cs / Fill/PropertyNames.cs is contractually a
// ShaderLab property declared in that layer's Properties{} block — MaterialPropertyRegistryParityTests
// unions the registry constants and requires each one to appear there. A frame global is the opposite
// kind of thing: it is pushed once per frame with Shader.SetGlobalFloat, is NOT a Properties{} entry
// (a ShaderLab property would serialise a per-material value into MapLine.mat that silently shadows the
// global), and is NOT a UnityPerMaterial CBUFFER member. Folding these names back into the registry
// would red three structural tests and hand the fill layer a property it has no use for.
//
// The file is named FrameGlobalNames.cs, not PropertyNames.cs, so the exemption is legible rather than
// accidental: the parity tests address the three registry files by explicit path, and
// NoRawStringMaterialAccessGuardTests.PropertyIdFiles_NoBareLiterals globs PropertyId.cs recursively.

namespace MapRenderer.Unity.Rendering.ShaderProperties
{
    /// <summary>
    /// Canonical string names for the map's per-frame shader GLOBALS — values pushed once per frame via
    /// <c>Shader.SetGlobalFloat</c> and read by every material that declares them, rather than bound per
    /// material.
    ///
    /// <para>Use <see cref="FrameGlobalIds"/> for the actual <c>Shader.SetGlobal*</c> calls (cached int
    /// ids). See the file header for why these names do not live in <see cref="PropertyNames"/>.</para>
    /// </summary>
    public static class FrameGlobalNames
    {
        /// <summary>World metres per DEVICE pixel for this frame — the view-independent ruler the line
        /// shader's dash parameterisation divides by. Pushed by
        /// <c>RenderLayerSet.ApplyZoom</c> as <c>CameraPoseMath.MetersPerPixel(zoom) / dpr</c>; declared in
        /// <c>Line_LitInput.hlsl</c> outside the CBUFFER. The width/gap/offset/translate family deliberately
        /// does NOT read it — those stay on the per-vertex <c>MapPixelsToWorld</c> measurement (S104).</summary>
        public const string MapFrameMetersPerDevicePixel = "_MapFrameMetersPerDevicePixel";
    }
}
