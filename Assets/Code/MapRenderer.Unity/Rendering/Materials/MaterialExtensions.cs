using UnityEngine;

namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// Extension helpers for <see cref="Material"/>.
    /// </summary>
    public static class MaterialExtensions
    {
        /// <summary>
        /// Clone <paramref name="source"/> into a new runtime <see cref="Material"/>.
        ///
        /// <para>In the <b>Editor</b> the clone is linked as a <b>Material Variant</b> of the source
        /// (<c>parent = source</c>), so editing the source asset's properties in the Inspector propagates
        /// live to the clone during Play — letting a maintainer tune shared styling without leaving Play
        /// mode (e.g. tweak the base line material's blur/blend and watch every line update).</para>
        ///
        /// <para>In a <b>build</b> (<c>UNITY_EDITOR</c> undefined) the parent link is NOT set: the result is
        /// a plain independent clone with no variant/asset dependency. Because <c>new Material(source)</c>
        /// copies all of <paramref name="source"/>'s current property values, the build clone is
        /// behaviour-identical to the Editor clone — only the live-edit linkage differs.</para>
        /// </summary>
        public static Material CloneWithParent(this Material source)
        {
            var clone = new Material(source);
#if UNITY_EDITOR
            clone.parent = source;
#endif
            return clone;
        }
    }
}
