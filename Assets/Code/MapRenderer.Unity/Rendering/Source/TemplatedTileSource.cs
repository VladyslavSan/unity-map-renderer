using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The production <see cref="IDataSource"/>: resolves a <see cref="TileId"/> to a fetch URI via
    /// <see cref="TileUrlTemplate"/>, then fetches bytes over <see cref="FileTransport"/> or
    /// <see cref="HttpTransport"/> depending on scheme. Non-local invariant: a <c>file://</c> template
    /// never touches HTTP — pinned by <c>SetStyle_FileUriChain_RendersOffline_ZeroNetwork</c>.
    /// </summary>
    internal sealed class TemplatedTileSource : IDataSource
    {
        private readonly TileUrlTemplate _address;
        private readonly bool            _local;

        /// <summary>The encoding of the bytes this source returns, set once at construction.</summary>
        public TileEncoding Encoding { get; }

        public TemplatedTileSource(TileUrlTemplate address, TileEncoding encoding)
        {
            _address = address;
            _local   = MapUri.IsFile(address.Template);
            Encoding = encoding;
        }

        /// <summary>Resolves <paramref name="id"/> to a URI and fetches it over the transport its scheme
        /// selects. Returns <see cref="TileResponse.Absent"/> when the transport returns <c>null</c>.</summary>
        public async UniTask<TileResponse> FetchAsync(TileId id, CancellationToken ct = default)
        {
            string uri   = _address.Resolve(id);
            byte[] bytes = _local
                ? await FileTransport.FetchOffMainAsync(uri, ct)
                : await HttpTransport.FetchAsync(uri, ct);

            return bytes == null ? TileResponse.Absent(Encoding) : new TileResponse(bytes, Encoding);
        }

        /// <summary>No managed resources to release — <see cref="HttpTransport"/>/<see cref="FileTransport"/>
        /// are static and own nothing per instance.</summary>
        public void Dispose() { }
    }
}
