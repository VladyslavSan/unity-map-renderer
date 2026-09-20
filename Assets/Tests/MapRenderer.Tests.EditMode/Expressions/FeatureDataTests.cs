// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests.Expressions
{
    /// <summary>
    /// S09 — feature-data category: properties/geometry-type/id (Style Spec "Feature data"). Tested over
    /// synthetic <see cref="DictionaryFeature"/> features. Real decoded features (S39) are covered in
    /// <c>MapRenderer.Tests.Mvt.MvtPropertyDecodeTests</c> and <c>MapRenderer.Tests.Filters.PropertyFilterTests</c>.
    /// </summary>
    [TestFixture]
    public class FeatureDataTests
    {
        [TestCase(TileGeometryType.Point, "Point")]
        [TestCase(TileGeometryType.LineString, "LineString")]
        [TestCase(TileGeometryType.Polygon, "Polygon")]
        [TestCase(TileGeometryType.Unknown, "Unknown")]
        public void GeometryType(TileGeometryType geom, string expected)
        {
            var f = Expr.Feature(geom: geom);
            Assert.AreEqual(expected, Expr.Eval("[\"geometry-type\"]", f).AsString());
        }

        [Test]
        public void Id_Present()
        {
            var f = Expr.Feature(hasId: true, id: Value.Number(42));
            Assert.AreEqual(42.0, Expr.Eval("[\"id\"]", f).AsNumber());
        }

        [Test]
        public void Id_Absent_IsNull()
        {
            var f = Expr.Feature(hasId: false);
            Assert.AreEqual(ValueType.Null, Expr.Eval("[\"id\"]", f).Type);
        }

        [Test]
        public void Properties_ReturnsObject_And_GetReadsIt()
        {
            var f = Expr.Feature(Expr.Props(("k", Value.String("v"))));
            Value props = Expr.Eval("[\"properties\"]", f);
            Assert.AreEqual(ValueType.Object, props.Type);
            Assert.IsTrue(props.AsObject().ContainsKey("k"));
            // get via properties: ["get", "k", ["properties"]]
            Assert.AreEqual("v", Expr.Eval("[\"get\", \"k\", [\"properties\"]]", f).AsString());
        }

        [Test]
        public void Get_NoFeature_IsError()
        {
            bool ok = Expr.TryEval("[\"get\", \"x\"]", out _, out _, feature: null);
            Assert.IsFalse(ok, "feature-data with no feature in context must be a spec error, not a crash.");
        }

        [Test]
        public void GeometryType_DrivesMatch()
        {
            var f = Expr.Feature(geom: TileGeometryType.Polygon);
            string e = "[\"match\", [\"geometry-type\"], \"Polygon\", \"fill\", \"Point\", \"point\", \"other\"]";
            Assert.AreEqual("fill", Expr.Eval(e, f).AsString());
        }
    }
}
