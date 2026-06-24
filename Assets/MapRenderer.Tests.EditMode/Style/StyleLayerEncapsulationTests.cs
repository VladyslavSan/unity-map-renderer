// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Reflection;
using NUnit.Framework;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// S60 acceptance criterion 1: raw JSON fields are encapsulated.
    /// Asserts that <see cref="StyleLayer.PaintJson"/>, <see cref="StyleLayer.LayoutJson"/>, and
    /// <see cref="StyleLayer.Raw"/> are NOT public (they are internal), while
    /// <see cref="StyleLayer.Filter"/> remains public.
    ///
    /// Uses reflection so that an accidental <c>public</c> revert is a compile-time miss but a test fail.
    /// </summary>
    [TestFixture]
    public class StyleLayerEncapsulationTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        [Test]
        public void PaintJson_IsNotPublic()
        {
            var fi = typeof(StyleLayer).GetField("PaintJson", AnyInstance);
            Assert.IsNotNull(fi, "PaintJson field must exist on StyleLayer.");
            Assert.IsFalse(fi.IsPublic,
                "PaintJson must NOT be public (S60: raw JSON encapsulated as internal).");
        }

        [Test]
        public void LayoutJson_IsNotPublic()
        {
            var fi = typeof(StyleLayer).GetField("LayoutJson", AnyInstance);
            Assert.IsNotNull(fi, "LayoutJson field must exist on StyleLayer.");
            Assert.IsFalse(fi.IsPublic,
                "LayoutJson must NOT be public (S60: raw JSON encapsulated as internal).");
        }

        [Test]
        public void Raw_IsNotPublic()
        {
            var fi = typeof(StyleLayer).GetField("Raw", AnyInstance);
            Assert.IsNotNull(fi, "Raw field must exist on StyleLayer.");
            Assert.IsFalse(fi.IsPublic,
                "Raw must NOT be public (S60: raw JSON encapsulated as internal).");
        }

        [Test]
        public void Filter_IsPublic()
        {
            var fi = typeof(StyleLayer).GetField("Filter", AnyInstance);
            Assert.IsNotNull(fi, "Filter field must exist on StyleLayer.");
            Assert.IsTrue(fi.IsPublic,
                "Filter MUST remain public (S10 owns its parse; FeatureSelector reads it directly).");
        }
    }
}
