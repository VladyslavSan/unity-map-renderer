// Frame GLOBALS — OUTSIDE the parity-swept registry files.
//
// Every name in PropertyNames.cs / Line/PropertyNames.cs / Fill/PropertyNames.cs is contractually a
// ShaderLab property declared in that layer's Properties{} block — MaterialPropertyRegistryParityTests
// unions the registry constants and requires each one to appear there. A frame global is the opposite
// kind of thing: it is pushed once per frame with Shader.SetGlobalFloat, is NOT a Properties{} entry
// (a ShaderLab property would serialise a per-material value into MapLine.mat that shadows the global),
// and is NOT a UnityPerMaterial CBUFFER member. Folding these names back into the registry would red
// three structural tests and hand the fill layer a property it has no use for.
//
// The file is named FrameGlobalNames.cs, not PropertyNames.cs, so the exemption is legible: the parity
// tests address the three registry files by explicit path, and
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
        /// <summary>World metres per DEVICE pixel for this frame — the line shader's view-independent
        /// ruler. MEASURED off the live camera at the look-at and pushed by
        /// <c>MapCamera.SyncToCamera</c> (<c>MapCamera.MetresPerDevicePixel</c>); declared in
        /// <c>Line_LitInput.hlsl</c> outside the CBUFFER, which is what keeps it a true global under the SRP
        /// Batcher and BRG/DOTS instancing.
        ///
        /// <para><b>Who reads it:</b> the styled WIDTH family — <c>line-width</c>, <c>line-gap-width</c>,
        /// <c>line-offset</c> — and the dash divisor. A styled <c>N px</c> fixes a WORLD size once and the
        /// perspective divide renders it; see <c>docs/line-rendering-design.md</c>. The AA straddle pad, the
        /// min-width floor and <c>line-translate</c> do <b>not</b> read it: those are sampling-grid
        /// quantities and keep the per-vertex <c>MapPixelsToWorld</c> measurement.</para></summary>
        public const string MapFrameMetersPerDevicePixel = "_MapFrameMetersPerDevicePixel";
    }
}
