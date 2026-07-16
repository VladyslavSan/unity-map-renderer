using MapRenderer.Core.Data;
using MapRenderer.Core.Mvt;

namespace MapRenderer.Core.Tiles
{
    /// <summary>
    /// Epic A / A6 (design §B-3): the tile-decode seam — <c>bytes → IDecodedTile</c>, selected by
    /// <see cref="TileEncoding"/> rather than hardcoded to MVT. This is the real structural change A6
    /// makes: <see cref="Processing.SharedTileDecode"/> (Unity) is injected with an <see cref="ITileDecoder"/>
    /// resolved from the fetch's <see cref="TileResponse.Encoding"/> instead of calling
    /// <see cref="MvtDecoder.Decode"/> directly.
    /// </summary>
    public interface ITileDecoder
    {
        IDecodedTile Decode(byte[] bytes);
    }

    /// <summary>The MVT decoder, wrapped behind <see cref="ITileDecoder"/>. This is the SOLE production
    /// <c>MvtDecoder.Decode(</c> call site after A6 (structurally asserted).</summary>
    public sealed class MvtTileDecoder : ITileDecoder
    {
        public IDecodedTile Decode(byte[] bytes) => MvtDecoder.Decode(bytes); // MvtTile is-a IDecodedTile
    }

    /// <summary>Resolves the <see cref="ITileDecoder"/> for a <see cref="TileEncoding"/>. No production
    /// dead branch: today only <see cref="TileEncoding.Mvt"/> exists (a non-MVT decoder is exercised by
    /// tests injecting a fake <see cref="ITileDecoder"/> directly, not a new enum member — design §B-3).</summary>
    public static class TileDecoders
    {
        public static ITileDecoder ForEncoding(TileEncoding encoding) => encoding switch
        {
            TileEncoding.Mvt => new MvtTileDecoder(), // a shared singleton is a dev refinement, not required here
            _ => throw new System.NotSupportedException($"No decoder for encoding {encoding}"),
        };
    }
}
