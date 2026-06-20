// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>
    /// S09 — classification: each parsed expression reports Constant / Zoom / Feature / Composite. The
    /// brief's "constant / zoom / data-driven" maps to Constant / Zoom / Feature; Composite (depends on
    /// BOTH zoom and feature) is the fourth corner S11/S12 need to distinguish a per-frame uniform from a
    /// per-vertex attribute.
    /// </summary>
    [TestFixture]
    public class ClassificationTests
    {
        private static ExpressionKind Kind(string json) => Expr.Parse(json).Kind;

        [Test]
        public void Literal_IsConstant() => Assert.AreEqual(ExpressionKind.Constant, Kind("5"));

        [Test]
        public void PureArithmetic_IsConstant()
            => Assert.AreEqual(ExpressionKind.Constant, Kind("[\"+\", 1, 2]"));

        [Test]
        public void ZoomRamp_IsZoom()
        {
            // data-driven == "zoom" classification per the brief (camera expression).
            Assert.AreEqual(ExpressionKind.Zoom,
                Kind("[\"interpolate\", [\"linear\"], [\"zoom\"], 0, 0, 10, 100]"));
        }

        [Test]
        public void Get_IsFeature()
        {
            // data-driven == "Feature" classification.
            Assert.AreEqual(ExpressionKind.Feature, Kind("[\"get\", \"x\"]"));
        }

        [Test]
        public void Has_IsFeature() => Assert.AreEqual(ExpressionKind.Feature, Kind("[\"has\", \"x\"]"));

        [Test]
        public void GeometryType_IsFeature()
            => Assert.AreEqual(ExpressionKind.Feature, Kind("[\"geometry-type\"]"));

        [Test]
        public void Id_IsFeature() => Assert.AreEqual(ExpressionKind.Feature, Kind("[\"id\"]"));

        [Test]
        public void MixedZoomAndFeature_IsComposite()
        {
            // ["+", ["zoom-ramp"], ["get"]] would be invalid (zoom not at ramp input); instead use a ramp
            // over zoom whose stop OUTPUTS depend on a feature -> composite.
            string e = "[\"interpolate\", [\"linear\"], [\"zoom\"], " +
                       "0, [\"get\", \"a\"], 10, [\"get\", \"b\"]]";
            Assert.AreEqual(ExpressionKind.Composite, Kind(e));
        }

        [Test]
        public void FeatureDrivenComparison_IsFeature()
            => Assert.AreEqual(ExpressionKind.Feature, Kind("[\"==\", [\"get\", \"x\"], 5]"));

        [Test]
        public void Let_InheritsBodyKind()
        {
            // body uses a feature get -> Feature.
            Assert.AreEqual(ExpressionKind.Feature,
                Kind("[\"let\", \"x\", [\"get\", \"a\"], [\"var\", \"x\"]]"));
        }
    }
}
