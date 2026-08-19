// Unity EditMode only.

using NUnit.Framework;
using MapRenderer.Unity.Rendering.Map;

namespace MapRenderer.Tests.Structure
{
    /// <summary>
    /// The load-bearing guard for the Dense property-storage win: <see cref="MapViewConfig"/> is the ONE
    /// production knob that <c>MapView.BuildSourceSpecs</c> resolves into the MVT decode path's
    /// <c>MvtPropertyStorage</c> argument, and the sole production <c>MvtTileFeatureSource</c> construction
    /// passes that resolved value explicitly. Nothing else in production reaches Dense — every other decode
    /// call site (the ~55 no-arg test call sites, and the API defaults themselves) stays Dictionary by
    /// design, as the maintainer-requested A/B oracle baseline (see <c>MvtPropertyStorage</c>). This tooth is
    /// the ONLY thing that reds if the production default silently reverts to Dictionary: the decode-side
    /// alloc tooth <c>Decode_SampleTile_DenseStorage_AllocatesFarUnderDictionary</c>
    /// (<c>DecodeGeometryFlattenAllocTests</c>) calls <c>Decode(..., Dense)</c> directly and stays green
    /// regardless of what production wires up.
    /// </summary>
    [TestFixture]
    public class ProductionPropertyStorageDefaultTests
    {
        [Test]
        public void ProductionConfig_DefaultsToDensePropertyStorage()
        {
            var config = new MapViewConfig();

            Assert.That(config.PropertyStorage, Is.EqualTo(PropertyStorageMode.Dense),
                "production decode must default to Dense — reverting this silently un-does the ~300 KB/tile " +
                "decode-allocation win the flatten stage measured, since Dense is reached ONLY through this " +
                "default (MapView.BuildSourceSpecs resolves it, and it is the sole production caller).");
        }
    }
}
