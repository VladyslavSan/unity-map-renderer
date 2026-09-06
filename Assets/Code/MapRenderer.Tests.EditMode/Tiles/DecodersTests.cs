// Unity EditMode only. It exercises MapRenderer.Jobs.Tiles (the tile-decode seam, moved out of
// Core), which Tools/core-tests does not compile — this file is not registered there.

using NUnit.Framework;
using MapRenderer.Core.Data;
using MapRenderer.Jobs.Tiles;

namespace MapRenderer.Tests.Tiles
{
    /// <summary>
    /// Epic A / A6 (plan §F-4): the tile-decode is selected BY <see cref="TileEncoding"/>, not hardcoded to
    /// MVT. Today <see cref="TileEncoding.Mvt"/> is the only member, so this pins the one resolvable case;
    /// an unmapped encoding throws rather than silently falling through to an MVT decode.
    /// </summary>
    [TestFixture]
    public class DecodersTests
    {
        [Test]
        public void ForEncoding_Mvt_ResolvesToMvtTileDecoder()
        {
            ITileDecoder decoder = Decoders.ForEncoding(TileEncoding.Mvt);
            Assert.IsInstanceOf<MvtTileDecoder>(decoder);
        }
    }
}
