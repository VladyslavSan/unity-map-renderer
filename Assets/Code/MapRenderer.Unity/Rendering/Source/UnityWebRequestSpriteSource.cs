// @2x/hi-DPI sprite-sheet URL selection (MapLibre's "sprite@2x.json"/"sprite@2x.png" convention) is
// v1-simplified to always fetch the 1x sheet (base + ".json" / base + ".png") — per-sprite pixelRatio
// scaling (SpriteEntry.PixelRatio) is handled downstream in IconQuadLayout, not by picking a different
// sheet resolution here. Deferred: a device-DPI-aware @2x sheet fetch, mirroring how glyph-PBF fetching
// has no DPI concept at all.

using System;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// <see cref="ISpriteSource"/> implementation over <see cref="UnityWebRequest"/>, mirroring
    /// <see cref="UnityWebRequestGlyphSource"/> (cancellation, absent-vs-error mapping, UniTask). Fetches
    /// the sheet's index JSON and PNG bytes from <c>{baseUrl}.json</c> / <c>{baseUrl}.png</c>, per the
    /// MapLibre Style Spec's root <c>sprite</c> URL convention — it does NOT decode the PNG (that is
    /// <see cref="Unity.Text.SpriteSheet"/>'s job, run by the Unity-side sprite manager).
    ///
    /// Clean-room: standard UnityWebRequest HTTP pattern (same as <see cref="UnityWebRequestGlyphSource"/>);
    /// no MapLibre source read.
    /// </summary>
    public sealed class UnityWebRequestSpriteSource : ISpriteSource
    {
        private readonly string _baseUrl;

        public UnityWebRequestSpriteSource(string baseUrl)
        {
            _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        }

        public async UniTask<SpriteResponse> FetchAsync(CancellationToken ct = default)
        {
            string jsonText = await FetchTextAsync(_baseUrl + ".json", ct);
            if (jsonText == null) return SpriteResponse.Absent();

            byte[] pngBytes = await FetchBytesAsync(_baseUrl + ".png", ct);
            if (pngBytes == null) return SpriteResponse.Absent();

            return new SpriteResponse { Json = jsonText, Png = pngBytes, HasData = true };
        }

        private static async UniTask<string> FetchTextAsync(string url, CancellationToken ct)
        {
            using var req = UnityWebRequest.Get(url);
            req.downloadHandler = new DownloadHandlerBuffer();

            // Same absent-vs-error mapping as UnityWebRequestGlyphSource: 404/204 -> null (caller maps to
            // Absent()); a cancellation racing the request can surface as a generic UnityWebRequestException
            // rather than OperationCanceledException, so re-map it when the token requested cancellation;
            // any other error (5xx, connection failure) re-throws.
            try
            {
                await req.SendWebRequest().ToUniTask(cancellationToken: ct);
            }
            catch (UnityWebRequestException ex) when (
                ex.ResponseCode == (long)HttpStatusCode.NotFound ||
                ex.ResponseCode == (long)HttpStatusCode.NoContent)
            {
                return null;
            }
            catch (UnityWebRequestException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            if (req.responseCode == (long)HttpStatusCode.NoContent)
                return null;

            return req.downloadHandler.text;
        }

        private static async UniTask<byte[]> FetchBytesAsync(string url, CancellationToken ct)
        {
            using var req = UnityWebRequest.Get(url);
            req.downloadHandler = new DownloadHandlerBuffer();

            try
            {
                await req.SendWebRequest().ToUniTask(cancellationToken: ct);
            }
            catch (UnityWebRequestException ex) when (
                ex.ResponseCode == (long)HttpStatusCode.NotFound ||
                ex.ResponseCode == (long)HttpStatusCode.NoContent)
            {
                return null;
            }
            catch (UnityWebRequestException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            if (req.responseCode == (long)HttpStatusCode.NoContent)
                return null;

            return req.downloadHandler.data;
        }

        public void Dispose() { /* UnityWebRequest instances are per-request (using), no shared resource */ }
    }
}
