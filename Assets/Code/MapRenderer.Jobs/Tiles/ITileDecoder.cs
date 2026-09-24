using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Jobs.Tiles
{
    /// <summary>
    /// The tile-decode seam — <c>(TileId, bytes) → IDecodedTile</c>, selected by
    /// <see cref="TileEncoding"/> (the fetch's <see cref="TileResponse.Encoding"/>), not hardcoded to MVT.
    /// Non-obvious why: the <see cref="TileId"/> is a parameter so the buffers' tile address is stamped here,
    /// once; an address a consumer supplies later can pair a buffer with the wrong tile. The returned tile's
    /// owner is the <c>SharedDisposable{IDecodedTile}</c> minted around it, never a consumer.
    /// </summary>
    public interface ITileDecoder
    {
        /// <summary>Produces the tile at <paramref name="id"/>, taking ownership of nothing and handing
        /// ownership of the result to its caller. <b><paramref name="bytes"/> may be null</b> for a decoder
        /// that carries its own payload (GeoJSON slices a retained dataset); every decoder still needs
        /// <paramref name="id"/>, because the produced buffers stamp themselves with it.</summary>
        IDecodedTile Decode(TileId id, byte[] bytes);
    }

    /// <summary>The MVT decoder, wrapped behind <see cref="ITileDecoder"/>. This is the SOLE production
    /// <c>MvtDecoder.Decode(</c> call site, asserted structurally.</summary>
    public sealed class MvtTileDecoder : ITileDecoder
    {
        public IDecodedTile Decode(TileId id, byte[] bytes) =>
            MvtDecoder.Decode(id, bytes); // MvtTile is-a IDecodedTile
    }

    /// <summary>Resolves the <see cref="ITileDecoder"/> for a <see cref="TileEncoding"/>. No production
    /// dead branch: only <see cref="TileEncoding.Mvt"/> exists, and a non-MVT decoder is exercised
    /// by tests injecting a fake <see cref="ITileDecoder"/> directly rather than by a new enum
    /// member.</summary>
    public static class Decoders
    {
        /// <param name="encoding">Selects which decoder to build.</param>
        public static ITileDecoder ForEncoding(TileEncoding encoding) => encoding switch
        {
            // a shared singleton is a dev refinement, not required here
            TileEncoding.Mvt => new MvtTileDecoder(),
            _ => throw new System.NotSupportedException($"No decoder for encoding {encoding}"),
        };
    }
}
