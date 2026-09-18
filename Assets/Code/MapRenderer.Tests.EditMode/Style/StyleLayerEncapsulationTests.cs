// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Reflection;
using NUnit.Framework;
using MapRenderer.Core.Style;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// S60 acceptance criterion 1: raw JSON fields are encapsulated, with one deliberate exception.
    /// <see cref="StyleLayer.Raw"/> was widened to public in the style-transitions epic (Stage 2): the
    /// restyle survivor gate lives outside <c>MapRenderer.Core</c> and must compare the whole raw layer
    /// object, including unknown/forward-compat keys the typed <c>Paint</c>/<c>Layout</c> views drop.
    /// <see cref="StyleLayer.Filter"/> remains public for the same pre-existing reason (S10).
    /// <c>PaintJson</c>/<c>LayoutJson</c> no longer exist (UMR-108) — nothing left to assert for them.
    /// </summary>
    [TestFixture]
    public class StyleLayerEncapsulationTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        [Test]
        public void Raw_IsPublic()
        {
            var fi = typeof(StyleLayer).GetField("Raw", AnyInstance);
            Assert.IsNotNull(fi, "Raw field must exist on StyleLayer.");
            Assert.IsTrue(fi.IsPublic,
                "Raw must be public (Stage 2: SurvivingLayerGate compares it from outside Core).");
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
