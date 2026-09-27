// Limitation: this always fetches the 1x sheet, never the "@2x" one; IconQuadLayout applies the per-sprite
// SpriteEntry.PixelRatio downstream.

using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Unity.Rendering.Source
{
    /// <summary>
    /// <see cref="ISpriteSource"/> implementation over <see cref="HttpTransport"/>. Fetches the sheet's
    /// index JSON and PNG bytes from <c>{baseUrl}.json</c> / <c>{baseUrl}.png</c>, per the style spec's
    /// root <c>sprite</c> URL convention. <see cref="Unity.Text.SpriteSheet"/> decodes the PNG.
    /// </summary>
    public sealed class SpriteSheetSource : ISpriteSource
    {
        private readonly string _baseUrl;

        public SpriteSheetSource(string baseUrl)
        {
            _baseUrl = baseUrl ?? throw new ArgumentNullException(nameof(baseUrl));
        }

        public async UniTask<SpriteResponse> FetchAsync(CancellationToken ct = default)
        {
            string jsonText = await HttpTransport.FetchTextAsync(_baseUrl + ".json", ct);
            if (jsonText == null) return SpriteResponse.Absent();

            byte[] pngBytes = await HttpTransport.FetchAsync(_baseUrl + ".png", ct);
            if (pngBytes == null) return SpriteResponse.Absent();

            return new SpriteResponse { Json = jsonText, Png = pngBytes, HasData = true };
        }

        public void Dispose() { /* no managed resources to release */ }
    }
}
