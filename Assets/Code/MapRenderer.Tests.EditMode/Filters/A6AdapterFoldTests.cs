// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Expressions;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Tests.Filters
{
    /// <summary>
    /// Epic A / A6 (plan §F-5, load-bearing byte-parity guard): <c>MvtFeature</c> now implements
    /// <see cref="IFeature"/> directly (the retired <c>MvtFeatureAdapter</c> folded in verbatim — design
    /// §B-2). This test pins that fold against the adapter's EXACT documented semantics (not just "some
    /// reasonable behaviour"):
    /// <list type="bullet">
    ///   <item><c>Id</c>: <c>HasId ? Value.Number((double)Id) : Value.Null</c> — the uint64→double
    ///     narrowing (MvtFeatureAdapter.cs:42, now deleted).</item>
    ///   <item><c>TryGetProperty</c>: the null-guard fallback — a null/absent <c>Properties</c> dictionary
    ///     or missing key returns <c>false</c> + <c>Value.Null</c>, never throws.</item>
    ///   <item><c>Properties</c>: the field, or an empty dictionary when the field is null.</item>
    ///   <item><c>GeometryType</c>/<c>HasId</c>: straight pass-through.</item>
    /// </list>
    /// A loose reimpl (e.g. dropping the HasId guard, or a different id narrowing) fails this test —
    /// filter/paint parity depends on this fold being byte-exact.
    /// </summary>
    [TestFixture]
    public class A6AdapterFoldTests
    {
        [Test]
        public void Id_HasId_ReturnsNumberOfTheUint64NarrowedToDouble()
        {
            var feature = new MvtFeature { HasId = true, Id = 42UL };
            IFeature asFeature = feature;

            Assert.AreEqual(Value.Number(42.0), asFeature.Id,
                "HasId=true must return Value.Number((double)Id) — the uint64->double narrowing.");
        }

        [Test]
        public void Id_NoId_ReturnsNull()
        {
            var feature = new MvtFeature { HasId = false, Id = 7UL };
            IFeature asFeature = feature;

            Assert.AreEqual(Value.Null, asFeature.Id,
                "HasId=false must return Value.Null regardless of the raw Id field (id=0 is a valid id per " +
                "the MVT spec — HasId, not a zero-check, gates presence).");
        }

        [Test]
        public void Id_ZeroIsAValidId_NotConfusedWithAbsent()
        {
            var feature = new MvtFeature { HasId = true, Id = 0UL };
            IFeature asFeature = feature;

            Assert.AreEqual(Value.Number(0.0), asFeature.Id,
                "id=0 with HasId=true is a valid id (MVT spec) — must resolve to Value.Number(0), not Value.Null.");
        }

        [Test]
        public void TryGetProperty_PresentKey_ReturnsTrueAndValue()
        {
            var feature = new MvtFeature
            {
                Properties = new Dictionary<string, Value> { ["NAME"] = Value.String("Aruba") },
            };
            IFeature asFeature = feature;

            bool found = asFeature.TryGetProperty("NAME", out Value value);

            Assert.IsTrue(found);
            Assert.AreEqual(Value.String("Aruba"), value);
        }

        [Test]
        public void TryGetProperty_MissingKey_ReturnsFalseAndNull()
        {
            var feature = new MvtFeature
            {
                Properties = new Dictionary<string, Value> { ["NAME"] = Value.String("Aruba") },
            };
            IFeature asFeature = feature;

            bool found = asFeature.TryGetProperty("ABBREV", out Value value);

            Assert.IsFalse(found, "a missing key must return false, not throw.");
            Assert.AreEqual(Value.Null, value);
        }

        [Test]
        public void TryGetProperty_NullPropertiesDictionary_ReturnsFalseAndNull_NeverThrows()
        {
            var feature = new MvtFeature { Properties = null };
            IFeature asFeature = feature;

            bool found = asFeature.TryGetProperty("NAME", out Value value);

            Assert.IsFalse(found,
                "a null Properties dictionary must be guarded, not throw (MvtFeatureAdapter's null-guard).");
            Assert.AreEqual(Value.Null, value);
        }

        [Test]
        public void Properties_NullField_ExposesEmptyDictionary_NotNull()
        {
            var feature = new MvtFeature { Properties = null };
            IFeature asFeature = feature;

            Assert.IsNotNull(asFeature.Properties,
                "Properties must never be null (adapter fallback to an empty dictionary).");
            Assert.AreEqual(0, asFeature.Properties.Count);
        }

        [Test]
        public void Properties_NonNullField_ExposesTheSameEntries()
        {
            var backing = new Dictionary<string, Value> { ["CONTINENT"] = Value.String("Africa") };
            var feature = new MvtFeature { Properties = backing };
            IFeature asFeature = feature;

            Assert.AreEqual(1, asFeature.Properties.Count);
            Assert.AreEqual(Value.String("Africa"), asFeature.Properties["CONTINENT"]);
        }

        [Test]
        public void GeometryType_PassesThroughUnchanged()
        {
            var feature = new MvtFeature { GeometryType = TileGeometryType.Polygon };
            IFeature asFeature = feature;

            Assert.AreEqual(TileGeometryType.Polygon, asFeature.GeometryType);
        }

        [Test]
        public void HasId_PassesThroughUnchanged()
        {
            var withId = new MvtFeature { HasId = true, Id = 1 };
            var withoutId = new MvtFeature { HasId = false };

            Assert.IsTrue(((IFeature)withId).HasId);
            Assert.IsFalse(((IFeature)withoutId).HasId);
        }
    }
}
