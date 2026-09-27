using System;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// The single HTTP transport over <see cref="UnityWebRequest"/> and UniTask (no <c>Task.Run</c>).
    /// HTTP 404/204 → <c>null</c> (absent); 5xx and connection errors throw
    /// <see cref="UnityWebRequestException"/>. The <see cref="CancellationToken"/> aborts the fetch.
    /// </summary>
    internal static class HttpTransport
    {
        // Process-wide request count; the offline test asserts a file:// chain issues none.
        // Interlocked, because a request can be issued off the main thread.
        private static int _requestCount;
        internal static int DebugRequestCount => Volatile.Read(ref _requestCount);

        /// <summary>Fetches raw bytes from <paramref name="uri"/>. Returns <c>null</c> for a 204/404
        /// response; throws for any other error.</summary>
        public static async UniTask<byte[]> FetchAsync(string uri, CancellationToken ct)
        {
            using var req = UnityWebRequest.Get(uri);
            req.downloadHandler = new DownloadHandlerBuffer();
            return await SendAsync(req, ct) ? req.downloadHandler.data : null;
        }

        /// <summary>Fetches text from <paramref name="uri"/>. Returns <c>null</c> for a 204/404
        /// response; throws for any other error.</summary>
        public static async UniTask<string> FetchTextAsync(string uri, CancellationToken ct)
        {
            using var req = UnityWebRequest.Get(uri);
            req.downloadHandler = new DownloadHandlerBuffer();
            return await SendAsync(req, ct) ? req.downloadHandler.text : null;
        }

        /// <summary>Sends <paramref name="req"/>. Returns <c>false</c> for a 204/404 response (absent);
        /// throws for any other error. The single copy of the HTTP status mapping every public method
        /// shares.</summary>
        private static async UniTask<bool> SendAsync(UnityWebRequest req, CancellationToken ct)
        {
            Interlocked.Increment(ref _requestCount);

            // ToUniTask() throws on any non-2xx before responseCode is readable, so 404/204 map to absent in
            // the catch. Other errors re-throw to the caller; OperationCanceledException passes through.
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
