using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// <see cref="ISpriteSource"/> implementation over <see cref="HttpTransport"/>. Fetches the sheet's
    /// index JSON and PNG bytes from <c>{baseUrl}{suffix}.json</c> / <c>{baseUrl}{suffix}.png</c>, per the
    /// style spec's root <c>sprite</c> URL convention. A non-empty <paramref name="suffix"/> (e.g.
    /// <c>"@2x"</c>) that 404s falls back to the plain <c>{baseUrl}</c> sheet, never leaving icons absent
    /// just because the requested density is missing. A 5xx or a cancel on the suffixed sheet propagates
    /// instead — only an explicit absent (404/204) falls back. <see cref="Unity.Text.SpriteSheet"/> decodes
    /// the PNG.
    /// </summary>
    public sealed class SpriteSheetSource : ISpriteSource
    {
        private readonly string _baseUrl;
        private readonly string _suffix;

        public SpriteSheetSource(string baseUrl, string suffix = "")
        {
            _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
            _suffix  = suffix ?? string.Empty;
        }

        public async UniTask<SpriteResponse> FetchAsync(CancellationToken ct = default)
        {
            SpriteResponse resp = await FetchAtAsync(_suffix, ct);
            if (resp.HasData || _suffix.Length == 0) return resp;

            // The requested density is absent — fall back to the plain 1x sheet rather than no icons at all.
            return await FetchAtAsync(string.Empty, ct);
        }

        private async UniTask<SpriteResponse> FetchAtAsync(string suffix, CancellationToken ct)
        {
            string jsonText = await HttpTransport.FetchTextAsync(_baseUrl + suffix + ".json", ct);
            if (jsonText == null) return SpriteResponse.Absent();

            byte[] pngBytes = await HttpTransport.FetchAsync(_baseUrl + suffix + ".png", ct);
            if (pngBytes == null) return SpriteResponse.Absent();

            return new SpriteResponse { Json = jsonText, Png = pngBytes, HasData = true };
        }

        public void Dispose() { /* no managed resources to release */ }
    }
}
