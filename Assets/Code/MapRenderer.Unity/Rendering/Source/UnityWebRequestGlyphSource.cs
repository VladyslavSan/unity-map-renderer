using System;
using System.Net;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine.Networking;
using MapRenderer.Core.Text;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// <see cref="IGlyphSource"/> over <see cref="UnityWebRequest"/>, with the same cancellation and
    /// absent-vs-error mapping as <see cref="UnityWebRequestDataSource"/>. Fills the style's <c>glyphs</c>
    /// template and fetches raw bytes; <see cref="GlyphPbfDecoder"/> decodes them. <c>{fontstack}</c> is
    /// URL-escaped (font names contain spaces); <c>{range}</c> is the inclusive 256-codepoint span
    /// <c>"{rangeStart}-{rangeStart+255}"</c>.
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

            // Same mapping as UnityWebRequestDataSource: 404/204 -> Absent(), a cancel surfacing as a
            // generic UnityWebRequestException -> OperationCanceledException, other errors re-throw.
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
