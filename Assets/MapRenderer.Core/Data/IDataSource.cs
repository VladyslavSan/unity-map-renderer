using System;
using System.Threading;
using System.Threading.Tasks;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Core.Data
{
    /// <summary>
    /// Declares how the bytes produced by this source are encoded.
    /// Extensible: new types (GeoJSON, MLT, raster) are added here later.
    /// </summary>
    public enum TileEncoding
    {
        Mvt
    }

    /// <summary>
    /// The response from a single tile fetch.
    /// <see cref="HasData"/> is false when the tile is explicitly absent (HTTP 204/404, missing file)
    /// as opposed to a thrown error (network failure, HTTP 5xx, IO error).
    /// </summary>
    public readonly struct TileResponse
    {
        public readonly byte[]       Bytes;
        public readonly TileEncoding Encoding;
        public readonly bool         HasData;

        public TileResponse(byte[] bytes, TileEncoding encoding)
        {
            Bytes    = bytes;
            Encoding = encoding;
            HasData  = true;
        }

        /// <summary>Creates an absent-tile response (HTTP 204/404 or missing file).</summary>
        public static TileResponse Absent(TileEncoding encoding)
            => new TileResponse(encoding);

        private TileResponse(TileEncoding encoding)
        {
            Bytes    = null;
            Encoding = encoding;
            HasData  = false;
        }
    }

    /// <summary>
    /// BYO data-source abstraction. HTTP, local files, PMTiles, in-memory tiles — all look
    /// identical to the pipeline.
    /// </summary>
    public interface IDataSource : IDisposable
    {
        TileEncoding Encoding { get; }

        /// <summary>
        /// Fetches tile bytes. Returns a <see cref="TileResponse"/> with <c>HasData=false</c>
        /// when the tile is explicitly absent (e.g. 404/204 or missing file). Throws on a
        /// genuine error (network failure, I/O error, unexpected HTTP status).
        /// </summary>
        Task<TileResponse> FetchAsync(TileId coord, CancellationToken ct = default);
    }
}
