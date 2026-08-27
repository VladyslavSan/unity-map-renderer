// NOT compiled by Tools/core-tests: MvtModels.cs (MvtFeature) is not registered there — the tile-decode
// seam note in core-tests.csproj lists what MvtDecoder/MvtModels pull in. Do NOT add any UnityEngine,
// MeshBuilder, NativeArray, or MonoBehaviour references — it still runs headless under Unity EditMode.

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
    ///   <item><c>TryGetProperty</c>: the null-guard fallback — an unset <see cref="MvtFeature.Store"/> or
    ///     a missing key returns <c>false</c> + <c>Value.Null</c>, never throws.</item>
    ///   <item><c>Properties</c>: an empty dictionary when <see cref="MvtFeature.Store"/> is unset.</item>
    ///   <item><c>GeometryType</c>/<c>HasId</c>: straight pass-through.</item>
    /// </list>
    /// A loose reimpl (e.g. dropping the HasId guard, or a different id narrowing) fails this test —
    /// filter/paint parity depends on this fold being byte-exact.
    ///
    /// <para>The three tests over a POPULATED property bag (<c>TryGetProperty_PresentKey</c>,
    /// <c>TryGetProperty_MissingKey</c>, <c>Properties_NonNullField</c>) build a
    /// <see cref="DictionaryFeature"/> instead of an <see cref="MvtFeature"/> — <see cref="MvtFeature.Store"/>
    /// is internal and can only legitimately hold a real <see cref="MapRenderer.Jobs.Mvt.DensePropertyStore"/>
    /// (a view over decoded <c>NativeArray</c> tag words), which this engine-free-styled file cannot hand-roll.
    /// They therefore exercise the <see cref="IFeature"/> contract via the double, not
    /// <see cref="MvtFeature"/>'s own forwarding — the store-forwarding this file used to pin over a
    /// hand-built dictionary is instead covered, over 1000+ real decoded (layer, feature, key) combinations,
    /// by <c>DensePropertyStoreTests.TryGetProperty_AgreesWithResolveToDictionary_ForEveryKeyAndEveryFeature_AcrossAllLayers</c>.
    /// The two null/empty-bag tests below stay on <see cref="MvtFeature"/> itself (no populated bag needed),
    /// so the null-guard pin they exist for is unaffected.</para>
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
            IFeature feature = new DictionaryFeature(
                new Dictionary<string, Value> { ["NAME"] = Value.String("Aruba") });

            bool found = feature.TryGetProperty("NAME", out Value value);

            Assert.IsTrue(found);
            Assert.AreEqual(Value.String("Aruba"), value);
        }

        [Test]
        public void TryGetProperty_MissingKey_ReturnsFalseAndNull()
        {
            IFeature feature = new DictionaryFeature(
                new Dictionary<string, Value> { ["NAME"] = Value.String("Aruba") });

            bool found = feature.TryGetProperty("ABBREV", out Value value);

            Assert.IsFalse(found, "a missing key must return false, not throw.");
            Assert.AreEqual(Value.Null, value);
        }

        [Test]
        public void TryGetProperty_NoStore_ReturnsFalseAndNull_NeverThrows()
        {
            var feature = new MvtFeature(); // Store left unset
            IFeature asFeature = feature;

            bool found = asFeature.TryGetProperty("NAME", out Value value);

            Assert.IsFalse(found,
                "an unset Store must be guarded, not throw (MvtFeatureAdapter's null-guard).");
            Assert.AreEqual(Value.Null, value);
        }

        [Test]
        public void Properties_NoStore_ExposesEmptyDictionary_NotNull()
        {
            var feature = new MvtFeature(); // Store left unset
            IFeature asFeature = feature;

            Assert.IsNotNull(asFeature.Properties,
                "Properties must never be null (adapter fallback to an empty dictionary).");
            Assert.AreEqual(0, asFeature.Properties.Count);
        }

        [Test]
        public void Properties_NonNullField_ExposesTheSameEntries()
        {
            IFeature feature = new DictionaryFeature(
                new Dictionary<string, Value> { ["CONTINENT"] = Value.String("Africa") });

            Assert.AreEqual(1, feature.Properties.Count);
            Assert.AreEqual(Value.String("Africa"), feature.Properties["CONTINENT"]);
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
