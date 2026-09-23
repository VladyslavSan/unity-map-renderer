using UnityEngine;

namespace MapRenderer.Unity.Rendering.Materials
{
    /// <summary>
    /// Extension helpers for <see cref="Material"/>.
    /// </summary>
    public static class MaterialExtensions
    {
        /// <summary>
        /// Clones <paramref name="source"/> into a new runtime <see cref="Material"/>. In the Editor the
        /// clone is linked as a Material Variant of the source (<c>parent = source</c>), so editing the
        /// source's properties in the Inspector propagates live to the clone during Play. In a build the
        /// parent link is not set — a plain independent clone, behaviour-identical because
        /// <c>new Material(source)</c> copies every current property value; only the live-edit link differs.
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
