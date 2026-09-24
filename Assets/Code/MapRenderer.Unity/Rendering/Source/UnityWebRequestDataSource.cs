using System;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Data;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// Production HTTP tile data source over <see cref="UnityWebRequest"/> and UniTask (no <c>Task.Run</c>).
    /// The URL template takes <c>{z}</c>, <c>{x}</c>, <c>{y}</c> in the XYZ convention; no TMS Y-flip.
    /// HTTP 404/204 → <see cref="TileResponse.Absent"/>; 5xx and connection errors throw
    /// <see cref="UnityWebRequestException"/>. The <see cref="CancellationToken"/> aborts the fetch.
    /// </summary>
    public sealed class UnityWebRequestDataSource : IDataSource
    {
        private readonly string _urlTemplate;

        public TileEncoding Encoding => TileEncoding.Mvt;

        // Process-wide construction count; the offline test asserts a file:// style constructs none.
        // Interlocked, because the ctor can run off the main thread.
        private static int _debugConstructedCount;
        internal static int DebugConstructedCount => System.Threading.Volatile.Read(ref _debugConstructedCount);

        public UnityWebRequestDataSource(string urlTemplate)
        {
            _urlTemplate = urlTemplate ?? throw new ArgumentNullException(nameof(urlTemplate));
            System.Threading.Interlocked.Increment(ref _debugConstructedCount);
        }

        public async UniTask<TileResponse> FetchAsync(TileId coord, CancellationToken ct = default)
        {
            string url = BuildUrl(coord);

            using var req = UnityWebRequest.Get(url);
            req.downloadHandler = new DownloadHandlerBuffer();

            // ToUniTask() throws on any non-2xx before responseCode is readable, so 404/204 map to Absent in
            // the catch. Other errors re-throw to TileScheduler; OperationCanceledException passes through.
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
            catch (UnityWebRequestException) when (ct.IsCancellationRequested)
            {
                // A mid-flight cancel can surface as a generic "Unknown Error" UnityWebRequestException.
                // Re-map it, so the scheduler and TileManager swallow it instead of logging a fault.
                throw new OperationCanceledException(ct);
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
