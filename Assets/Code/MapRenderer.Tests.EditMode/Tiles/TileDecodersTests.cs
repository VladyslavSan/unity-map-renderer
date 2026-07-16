// Engine-free: compiled verbatim by both the Unity EditMode runner and Tools/core-tests.
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using NUnit.Framework;
using MapRenderer.Core.Data;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// Epic A / A6 (plan §F-4): the tile-decode is selected BY <see cref="TileEncoding"/>, not hardcoded to
    /// MVT. Today <see cref="TileEncoding.Mvt"/> is the only member, so this pins the one resolvable case;
    /// an unmapped encoding throws rather than silently falling through to an MVT decode.
    /// </summary>
    [TestFixture]
    public class TileDecodersTests
    {
        [Test]
        public void ForEncoding_Mvt_ResolvesToMvtTileDecoder()
        {
            ITileDecoder decoder = TileDecoders.ForEncoding(TileEncoding.Mvt);
            Assert.IsInstanceOf<MvtTileDecoder>(decoder);
        }
    }
}
