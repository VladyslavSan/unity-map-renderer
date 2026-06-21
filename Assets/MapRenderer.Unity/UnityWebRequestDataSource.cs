using System;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;

namespace MapRenderer.Unity
{
    /// <summary>
    /// S51: Production HTTP tile data source using <see cref="UnityWebRequest"/> and UniTask.
    /// Zero <c>Task</c> / <c>Task.Run</c> — fetches are fully async via <c>.ToUniTask()</c>.
    ///
    /// <para>
    /// URL template tokens: <c>{z}</c>, <c>{x}</c>, <c>{y}</c>.
    /// Example: <c>https://demotiles.maplibre.org/tiles/{z}/{x}/{y}.pbf</c>
    /// </para>
    ///
    /// <para>
    /// Y convention: XYZ (slippy-map / MapLibre default). TMS Y-flip is NOT applied.
    /// </para>
    ///
    /// <para>
    /// HTTP 404/204 → <see cref="TileResponse.Absent"/>; HTTP 5xx → throws
    /// <see cref="UnityWebRequestException"/>. <see cref="CancellationToken"/> is threaded through
    /// via <c>destroyCancellationToken</c> support in ToUniTask.
    /// </para>
    ///
    /// Clean-room: standard UnityWebRequest HTTP pattern; no MapLibre source read.
    /// </summary>
    public sealed class UnityWebRequestDataSource : IDataSource
    {
        private readonly string _urlTemplate;

        public TileEncoding Encoding => TileEncoding.Mvt;

        public UnityWebRequestDataSource(string urlTemplate)
        {
            _urlTemplate = urlTemplate ?? throw new ArgumentNullException(nameof(urlTemplate));
        }

        public async UniTask<TileResponse> FetchAsync(TileId coord, CancellationToken ct = default)
        {
            string url = BuildUrl(coord);

            using var req = UnityWebRequest.Get(url);
            req.downloadHandler = new DownloadHandlerBuffer();

            // .ToUniTask() hooks into the Unity PlayerLoop to poll the web request each frame.
            // CancellationToken is threaded through so destroyCancellationToken can abort the fetch.
            //
            // 404 / 204 — tile explicitly absent: ToUniTask() calls IsError() which returns true for
            // ProtocolError (any non-2xx response), so a 404 throws UnityWebRequestException BEFORE we
            // can inspect responseCode. Catch the exception early and map known absent codes to
            // TileResponse.Absent. All other errors (5xx, connection errors) re-throw so TileScheduler
            // can handle them (negative-cache prevention). OperationCanceledException is NOT a
            // UnityWebRequestException, so it propagates through and the scheduler's per-tile CTS works.
            try
            {
                await req.SendWebRequest().ToUniTask(cancellationToken: ct);
            }
            catch (UnityWebRequestException ex) when (
                ex.ResponseCode == (long)HttpStatusCode.NotFound ||
                ex.ResponseCode == (long)HttpStatusCode.NoContent)
            {
                return TileResponse.Absent(TileEncoding.Mvt);
            }

            // 204 No Content on a 2xx path (unusual but possible) — treat as absent.
            if (req.responseCode == (long)HttpStatusCode.NoContent)
                return TileResponse.Absent(TileEncoding.Mvt);

            return new TileResponse(req.downloadHandler.data, TileEncoding.Mvt);
        }

        private string BuildUrl(TileId coord)
            => _urlTemplate
                .Replace("{z}", coord.Z.ToString())
                .Replace("{x}", coord.X.ToString())
                .Replace("{y}", coord.Y.ToString());

        public void Dispose() { /* UnityWebRequest instances are per-request (using), no shared resource */ }
    }
}
