using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Text;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// <see cref="IGlyphSource"/> over <see cref="HttpTransport"/>. Fills the style's <c>glyphs</c>
    /// template and fetches raw bytes; <see cref="GlyphPbfDecoder"/> decodes them. <c>{fontstack}</c> is
    /// URL-escaped (font names contain spaces); <c>{range}</c> is the inclusive 256-codepoint span
    /// <c>"{rangeStart}-{rangeStart+255}"</c>.
    /// </summary>
    public sealed class TemplatedGlyphSource : IGlyphSource
    {
        private readonly string _urlTemplate;

        public TemplatedGlyphSource(string urlTemplate)
        {
            _urlTemplate = urlTemplate ?? throw new ArgumentNullException(nameof(urlTemplate));
        }

        public async UniTask<GlyphRangeResponse> FetchAsync(string fontStack, int rangeStart, CancellationToken ct = default)
        {
            string url    = BuildUrl(fontStack, rangeStart);
            byte[] bytes  = await HttpTransport.FetchAsync(url, ct);
            return bytes == null ? GlyphRangeResponse.Absent() : new GlyphRangeResponse(bytes);
        }

        private string BuildUrl(string fontStack, int rangeStart)
        {
            string range = $"{rangeStart}-{rangeStart + 255}";
            return _urlTemplate
                .Replace("{fontstack}", Uri.EscapeDataString(fontStack ?? string.Empty))
                .Replace("{range}", range);
        }

        public void Dispose() { /* no managed resources to release */ }
    }
}
