using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Core.Data
{
    /// <summary>
    /// Fetches tile bytes from an HTTP(S) endpoint. An <see cref="HttpClient"/> is injected at
    /// construction so tests can supply a stub <see cref="HttpMessageHandler"/> without hitting
    /// the network. The caller owns the <see cref="HttpClient"/> lifetime when injecting one;
    /// when using the default ctor the instance owns it.
    /// <para>
    /// URL template tokens: <c>{z}</c>, <c>{x}</c>, <c>{y}</c>.
    /// Example: <c>https://tiles.example.com/tiles/{z}/{x}/{y}.mvt</c>
    /// </para>
    /// <para>
    /// Y convention: XYZ (slippy-map / MapLibre default). TMS Y-flip is NOT applied.
    /// </para>
    /// </summary>
    public sealed class HttpDataSource : IDataSource
    {
        private readonly HttpClient  _client;
        private readonly string      _urlTemplate;
        private readonly bool        _ownsClient;

        public TileEncoding Encoding => TileEncoding.Mvt;

        /// <summary>
        /// Constructs with an injected <see cref="HttpClient"/>. Caller owns the client lifetime.
        /// </summary>
        public HttpDataSource(HttpClient client, string urlTemplate)
        {
            _client      = client      ?? throw new ArgumentNullException(nameof(client));
            _urlTemplate = urlTemplate ?? throw new ArgumentNullException(nameof(urlTemplate));
            _ownsClient  = false;
        }

        /// <summary>
        /// Constructs with an owned <see cref="HttpClient"/> (no custom handler; production use).
        /// </summary>
        public HttpDataSource(string urlTemplate)
            : this(new HttpClient(), urlTemplate)
        {
            _ownsClient = true;
        }

        public async Task<TileResponse> FetchAsync(TileId coord, CancellationToken ct = default)
        {
            string url = BuildUrl(coord);
            using (HttpResponseMessage response = await _client.GetAsync(url, ct).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound ||
                    response.StatusCode == HttpStatusCode.NoContent)
                {
                    return TileResponse.Absent(TileEncoding.Mvt);
                }

                response.EnsureSuccessStatusCode();

                byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return new TileResponse(bytes, TileEncoding.Mvt);
            }
        }

        private string BuildUrl(TileId coord)
            => _urlTemplate
                .Replace("{z}", coord.Z.ToString())
                .Replace("{x}", coord.X.ToString())
                .Replace("{y}", coord.Y.ToString());

        public void Dispose()
        {
            if (_ownsClient)
                _client.Dispose();
        }
    }
}
