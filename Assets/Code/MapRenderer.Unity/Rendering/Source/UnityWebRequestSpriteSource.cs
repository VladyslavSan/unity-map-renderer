// Limitation: this always fetches the 1x sheet, never the "@2x" one; IconQuadLayout applies the per-sprite
// SpriteEntry.PixelRatio downstream.

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
    /// style spec's root <c>sprite</c> URL convention. <see cref="Unity.Text.SpriteSheet"/> decodes the PNG.
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

            // Same mapping as UnityWebRequestGlyphSource, except 404/204 -> null (the caller maps it to
            // Absent()). This covers FetchBytesAsync below too.
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
