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
    /// Epic A / A6 (plan §F-5, load-bearing byte-parity guard): <c>MvtFeature</c> implements
    /// <see cref="IFeature"/> directly (the retired <c>MvtFeatureAdapter</c> folded in verbatim — design
    /// §B-2). This test pins that implementation against its EXACT documented semantics (not just "some
    /// reasonable behaviour"):
    /// <list type="bullet">
    ///   <item><c>TryGetProperty</c>: a feature with no real property store (its <see cref="MvtFeature.Store"/>
    ///     defaulted to the <c>EmptyPropertyStore</c> Null Object), or a missing key, returns <c>false</c> +
    ///     <c>Value.Null</c> — never throws.</item>
    ///   <item><c>Properties</c>: an empty dictionary for a feature whose <see cref="MvtFeature.Store"/> is
    ///     the default Null Object.</item>
    ///   <item><c>GeometryType</c>: straight pass-through.</item>
    /// </list>
    ///
    /// <para>Feature <c>Id</c> is not exercised here: since the id-representation cleanup deleted
    /// <c>IFeature.HasId</c>, <c>MvtFeature.Id</c> is a plain stored <see cref="Value"/> (a direct
    /// auto-property, no fold to pin), and the decode-level narrowing (present id →
    /// <c>Value.Number((double)ReadVarint())</c>, absent field-1 → <c>Value.Null</c>, id 0 → a valid
    /// <c>Value.Number(0)</c>) is pinned in <c>MvtPropertyDecodeTests</c>.</para>
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
    /// The two empty-bag tests below stay on <see cref="MvtFeature"/> itself (no populated bag needed),
    /// so the Null-Object pin they exist for is unaffected.</para>
    /// </summary>
    [TestFixture]
    public class A6AdapterFoldTests
    {
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
            var feature = new MvtFeature(); // Store defaults to the EmptyPropertyStore Null Object
            IFeature asFeature = feature;

            bool found = asFeature.TryGetProperty("NAME", out Value value);

            Assert.IsFalse(found,
                "with no real store the default EmptyPropertyStore answers 'absent' — never throws.");
            Assert.AreEqual(Value.Null, value);
        }

        [Test]
        public void Properties_NoStore_ExposesEmptyDictionary_NotNull()
        {
            var feature = new MvtFeature(); // Store defaults to the EmptyPropertyStore Null Object
            IFeature asFeature = feature;

            Assert.IsNotNull(asFeature.Properties,
                "Properties must never be null (the default EmptyPropertyStore exposes an empty dictionary).");
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
    }
}
