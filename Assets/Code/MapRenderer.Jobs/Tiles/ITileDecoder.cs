using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;
using MapRenderer.Jobs.Mvt;

namespace MapRenderer.Jobs.Tiles
{
    /// <summary>
    /// The tile-decode seam — <c>(TileId, bytes) → IDecodedTile</c>, selected by
    /// <see cref="TileEncoding"/> rather than hardcoded to MVT. <c>TileDecodeDispatch</c> (Unity) is handed
    /// an <see cref="ITileDecoder"/> resolved from the fetch's <see cref="TileResponse.Encoding"/> instead
    /// of calling <see cref="MvtDecoder.Decode"/> directly.
    ///
    /// <para><b>The <see cref="TileId"/> is a parameter, and that is the load-bearing part.</b> The decoded
    /// tile owns its geometry, so the buffers' tile address is stamped HERE, once, from the id the fetch
    /// already had. An address supplied later, by whichever consumer wanted geometry, is what lets a buffer
    /// be paired with the wrong tile. The returned tile owns <c>Allocator.Persistent</c> memory and is
    /// <c>IDisposable</c>; its owner is the <c>SharedDisposable{IDecodedTile}</c> minted around it, which
    /// frees it at the last reference — never a consumer.</para>
    /// </summary>
    public interface ITileDecoder
    {
        /// <summary>Produces the tile at <paramref name="id"/>, taking ownership of nothing and handing
        /// ownership of the result to its caller.
        ///
        /// <para><b><paramref name="bytes"/> may be null</b> for a decoder that carries its own payload — a
        /// source with no wire format at all (GeoJSON slices a retained, already-projected dataset). Such a
        /// decoder is a closure over that payload and ignores the parameter entirely. What it does NOT ignore
        /// is <paramref name="id"/>: the tile address is the one input every decoder needs, because it is
        /// what the produced buffers stamp themselves with, and it is why this seam — not an eagerly built
        /// tile — is the right place for a byte-less source to plug in.</para></summary>
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
