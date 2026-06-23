using NUnit.Framework;
using UnityEngine;
using MapRenderer.Unity;

namespace MapRenderer.Tests.EditMode
{
    /// <summary>
    /// Contract for <see cref="MaterialExtensions.CloneWithParent"/> — the per-layer material cloning
    /// primitive behind <see cref="MapMaterialSet"/> (S57). A clone must be an independent Material
    /// that copies the source's values; in the Editor it must also be a Material Variant of the source so
    /// live base-material edits propagate during Play.
    /// </summary>
    public class MaterialExtensionsTests
    {
        private static Material MakeMaterial()
        {
            // Use the live line shader (transparent) if present, else any available shader — the test only
            // needs a concrete material; it asserts cloning behaviour, not shader specifics.
            var shader = Shader.Find("Map/Line") ?? Shader.Find("Sprites/Default");
            Assert.IsNotNull(shader, "No shader available to construct a test material.");
            return new Material(shader);
        }

        [Test]
        public void CloneWithParent_ReturnsDistinctMaterial_WithSameShader()
        {
            var src = MakeMaterial();
            var clone = src.CloneWithParent();

            Assert.AreNotSame(src, clone, "Clone must be a new Material instance, not the source.");
            Assert.AreEqual(src.shader, clone.shader, "Clone must keep the source's shader.");

            Object.DestroyImmediate(clone);
            Object.DestroyImmediate(src);
        }

        [Test]
        public void CloneWithParent_CopiesSourcePropertyValues()
        {
            var src = MakeMaterial();
            const string prop = "_Blur";
            if (src.HasProperty(prop))
                src.SetFloat(prop, 0.42f);

            var clone = src.CloneWithParent();

            if (src.HasProperty(prop))
                Assert.AreEqual(0.42f, clone.GetFloat(prop), 1e-5f,
                    "Clone must copy the source's current property values (new Material(source) semantics).");

            Object.DestroyImmediate(clone);
            Object.DestroyImmediate(src);
        }

#if UNITY_EDITOR
        [Test]
        public void CloneWithParent_InEditor_LinksCloneAsVariantOfSource()
        {
            var src = MakeMaterial();
            var clone = src.CloneWithParent();

            Assert.AreEqual(src, clone.parent,
                "In the Editor the clone must be a Material Variant of the source so base edits propagate live.");

            Object.DestroyImmediate(clone);
            Object.DestroyImmediate(src);
        }
#endif
    }
}
