// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Json;
using Fill = MapRenderer.Core.Style.Fill;

namespace MapRenderer.Tests.Style
{
    /// <summary>
    /// P3 — <see cref="Fill.LayoutProperties"/>: parsing <c>fill-sort-key</c>.
    ///
    /// <para>The ordering behaviour it drives is pinned engine-side by <c>FillSortKeySnapshotTests</c>;
    /// this fixture covers the parse, the default, and the zoom/feature capability that decides whether the
    /// key is evaluated per feature at build time or could be hoisted.</para>
    ///
    /// Engine-free (no UnityEngine). Runs in BOTH dotnet core-tests AND Unity EditMode.
    /// </summary>
    [TestFixture]
    public class FillLayoutTests
    {
        [Test]
        public void SortKey_Absent_DefaultsToZeroAndFlagsDefault()
        {
            var layout = new Fill.LayoutProperties((JsonValue)null);

            Assert.IsTrue(layout.SortKeyIsDefault,
                "an absent fill-sort-key must be flagged so the builder can skip the sort entirely — that " +
                "is what keeps existing fill meshes byte-identical.");
            Assert.AreEqual(0f, layout.SortKey.Evaluate(0.0), 1e-9, "spec default is 0");
        }

        [Test]
        public void SortKey_EmptyLayoutObject_IsStillDefault()
        {
            var layout = new Fill.LayoutProperties(JsonParser.Parse("{}"));
            Assert.IsTrue(layout.SortKeyIsDefault, "a layout object without the key is the same as no layout");
        }

        [Test]
        public void SortKey_Constant_IsParsedAndNotFlaggedDefault()
        {
            var layout = new Fill.LayoutProperties(JsonParser.Parse(@"{""fill-sort-key"": 7}"));

            Assert.IsFalse(layout.SortKeyIsDefault, "an explicit key must NOT be treated as absent");
            Assert.AreEqual(7f, layout.SortKey.Evaluate(0.0), 1e-9);
            Assert.AreEqual(ExpressionKind.Constant, layout.SortKey.Kind);
        }

        [Test]
        public void SortKey_ZoomExpression_IsZoomDependent()
        {
            var layout = new Fill.LayoutProperties(JsonParser.Parse(
                @"{""fill-sort-key"": [""interpolate"", [""linear""], [""zoom""], 0, 0, 10, 100]}"));

            Assert.IsFalse(layout.SortKeyIsDefault);
            Assert.IsTrue(layout.SortKey.IsZoomDependent, "a zoom expression must classify as zoom-dependent");
            Assert.AreEqual(0f,   layout.SortKey.Evaluate(0.0),  1e-4);
            Assert.AreEqual(100f, layout.SortKey.Evaluate(10.0), 1e-4);
        }

        [Test]
        public void SortKey_FeatureExpression_DependsOnFeature()
        {
            var layout = new Fill.LayoutProperties(JsonParser.Parse(
                @"{""fill-sort-key"": [""get"", ""rank""]}"));

            Assert.IsTrue(layout.SortKey.DependsOnFeature,
                "a data-driven sort key must be evaluated per FEATURE — the builder relies on this to sort " +
                "features against each other rather than hoisting one value for the layer.");
        }

        [Test]
        public void StyleLayer_ExposesParsedLayout()
        {
            var style = MapRenderer.Core.Style.StyleParser.Parse(@"{
                ""version"": 8,
                ""sources"": { ""s"": { ""type"": ""vector"", ""tiles"": [""https://e.invalid/{z}/{x}/{y}.pbf""] } },
                ""layers"": [ { ""id"": ""f"", ""type"": ""fill"", ""source"": ""s"", ""source-layer"": ""l"",
                                ""layout"": { ""fill-sort-key"": 3 } } ]
            }");

            var fillLayer = (Fill.StyleLayer)style.Layers[0];
            Assert.IsFalse(fillLayer.Layout.SortKeyIsDefault, "the parser must route layout onto the typed layer");
            Assert.AreEqual(3f, fillLayer.Layout.SortKey.Evaluate(0.0), 1e-9);
        }
    }
}
