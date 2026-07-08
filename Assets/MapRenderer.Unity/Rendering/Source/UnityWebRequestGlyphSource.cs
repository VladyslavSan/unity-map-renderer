using System;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;
using MapRenderer.Core.Text;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// S18 Unity-side batch: <see cref="IGlyphSource"/> implementation over <see cref="UnityWebRequest"/>,
    /// mirroring <see cref="UnityWebRequestDataSource"/> almost exactly (cancellation, absent-vs-error
    /// mapping, UniTask). Builds the fetch URL from a glyph-PBF <c>glyphs</c> template
    /// (<c>{fontstack}/{range}.pbf</c> tokens, per the MapLibre Style Spec) and fetches raw bytes — it
    /// does NOT decode (that is <see cref="GlyphPbfDecoder"/>'s job, run by the Unity <c>GlyphManager</c>).
    ///
    /// <para>
    /// <c>{fontstack}</c> is URL-escaped (a font name commonly contains spaces, e.g. "Noto Sans Regular")
    /// before substitution; <c>{range}</c> becomes the inclusive 256-codepoint span
    /// <c>"{rangeStart}-{rangeStart+255}"</c> (the glyph-PBF range convention).
    /// </para>
    ///
    /// Clean-room: standard UnityWebRequest HTTP pattern (same as <see cref="UnityWebRequestDataSource"/>);
    /// no MapLibre source read.
    /// </summary>
    public sealed class UnityWebRequestGlyphSource : IGlyphSource
    {
        private readonly string _urlTemplate;

        public UnityWebRequestGlyphSource(string urlTemplate)
        {
            _urlTemplate = urlTemplate ?? throw new ArgumentNullException(nameof(urlTemplate));
        }

        public async UniTask<GlyphRangeResponse> FetchAsync(string fontStack, int rangeStart, CancellationToken ct = default)
        {
            string url = BuildUrl(fontStack, rangeStart);

            using var req = UnityWebRequest.Get(url);
            req.downloadHandler = new DownloadHandlerBuffer();

            // Same absent-vs-error mapping as UnityWebRequestDataSource: 404/204 -> Absent(); a
            // cancellation racing the request can surface as a generic UnityWebRequestException rather
            // than OperationCanceledException, so re-map it when the token requested cancellation; any
            // other error (5xx, connection failure) re-throws.
            try
            {
                await req.SendWebRequest().ToUniTask(cancellationToken: ct);
            }
            catch (UnityWebRequestException ex) when (
                ex.ResponseCode == (long)HttpStatusCode.NotFound ||
                ex.ResponseCode == (long)HttpStatusCode.NoContent)
            {
                return GlyphRangeResponse.Absent();
            }
            catch (UnityWebRequestException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            if (req.responseCode == (long)HttpStatusCode.NoContent)
                return GlyphRangeResponse.Absent();

            return new GlyphRangeResponse(req.downloadHandler.data);
        }

        private string BuildUrl(string fontStack, int rangeStart)
        {
            string range = $"{rangeStart}-{rangeStart + 255}";
            return _urlTemplate
                .Replace("{fontstack}", Uri.EscapeDataString(fontStack ?? string.Empty))
                .Replace("{range}", range);
        }

        public void Dispose() { /* UnityWebRequest instances are per-request (using), no shared resource */ }
    }
}
