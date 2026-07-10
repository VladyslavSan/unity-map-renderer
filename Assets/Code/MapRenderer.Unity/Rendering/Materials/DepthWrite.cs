namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// ShaderLab depth-write as a typed value. Unity has no ZWrite enum — it exposes depth-write only as
    /// <c>UnityEngine.Rendering.DepthState.writeEnabled</c> (a bool), and ShaderLab's <c>ZWrite On/Off</c>
    /// is the material's <c>_ZWrite</c> float (1/0). We declare a typed On/Off whose values match those ints
    /// (so <c>(int)On == 1</c> writes straight to <c>_ZWrite</c>) and that reads consistently alongside the
    /// other typed render states (<c>CullMode</c>, <c>CompareFunction</c>, <c>BlendMode</c>).
    /// </summary>
    public enum DepthWrite
    {
        /// <summary>ZWrite Off — do not write to the depth buffer (painter's-algorithm layers).</summary>
        Off = 0,

        /// <summary>ZWrite On — write depth (opaque geometry).</summary>
        On = 1,
    }
}
