using System;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The single HTTP transport over <see cref="UnityWebRequest"/> and UniTask (no <c>Task.Run</c>).
    /// HTTP 404/204 → <c>null</c> (absent); 5xx, a connection error, and (on <see cref="FetchAsync"/> only)
    /// a request that exceeds <see cref="TimeoutSeconds"/> all throw <see cref="UnityWebRequestException"/>.
    /// The <see cref="CancellationToken"/> aborts the fetch.
    /// </summary>
    internal static class HttpTransport
    {
        // Process-wide request count; the offline test asserts a file:// chain issues none.
        // Interlocked, because a request can be issued off the main thread.
        private static int _requestCount;
        internal static int DebugRequestCount => Volatile.Read(ref _requestCount);

        /// <summary>The <see cref="FetchAsync"/> deadline (UMR-150): tile/glyph/sprite-image byte fetches
        /// hold an admission slot, so a hung endpoint must not hold it forever. One band with
        /// <c>SymbolSubsystem.SpriteFetchDeadlineSeconds</c>. <see cref="FetchTextAsync"/> (one-shot
        /// documents: style/TileJSON/GeoJSON) has none — a large document on a slow link must keep loading.</summary>
        internal const int TimeoutSeconds = 8;

        /// <summary>Fetches raw bytes from <paramref name="uri"/>. Returns <c>null</c> for a 204/404
        /// response; throws for any other error, including exceeding <see cref="TimeoutSeconds"/>.</summary>
        public static async UniTask<byte[]> FetchAsync(string uri, CancellationToken ct)
        {
            using var req = UnityWebRequest.Get(uri);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = TimeoutSeconds; // the slot-holding byte path only — see the constant's doc.
            return await SendAsync(req, ct) ? req.downloadHandler.data : null;
        }

        /// <summary>Fetches text from <paramref name="uri"/>. Returns <c>null</c> for a 204/404
        /// response; throws for any other error. No deadline — see <see cref="TimeoutSeconds"/>.</summary>
        public static async UniTask<string> FetchTextAsync(string uri, CancellationToken ct)
        {
            using var req = UnityWebRequest.Get(uri);
            req.downloadHandler = new DownloadHandlerBuffer();
            return await SendAsync(req, ct) ? req.downloadHandler.text : null;
        }

        /// <summary>Sends <paramref name="req"/>. Returns <c>false</c> for a 204/404 response (absent);
        /// throws for any other error. The single copy of the HTTP status mapping both public methods share.</summary>
        private static async UniTask<bool> SendAsync(UnityWebRequest req, CancellationToken ct)
        {
            Interlocked.Increment(ref _requestCount);

            // ToUniTask() throws before responseCode is readable, so 404/204 map to absent below. Every
            // other error — including FetchAsync's timeout — re-throws like a 5xx; cancellation passes through.
            try
            {
                await req.SendWebRequest().ToUniTask(cancellationToken: ct);
            }
            catch (UnityWebRequestException ex) when (
                ex.ResponseCode == (long)HttpStatusCode.NotFound ||
                ex.ResponseCode == (long)HttpStatusCode.NoContent)
            {
                return false;
            }
            catch (UnityWebRequestException) when (ct.IsCancellationRequested)
            {
                // A mid-flight cancel can surface as a generic "Unknown Error" UnityWebRequestException.
                // Re-map it, so the caller swallows it instead of logging a fault.
                throw new OperationCanceledException(ct);
            }

            // 204 No Content on a 2xx path (unusual but possible) — treat as absent.
            return req.responseCode != (long)HttpStatusCode.NoContent;
        }
    }
}
