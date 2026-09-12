using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Tests
{
    /// <summary>
    /// I4 test/demo-only <see cref="ISpriteSource"/>: serves the committed fixture sprite sheet at
    /// <c>Assets/Fixtures/sprites/sample-sprite.{json,png}</c> (the same fixture <c>SpriteIndexTests</c>/
    /// <c>SpriteSheetTests</c> load), so a future icon eyeball demo can render REAL sprite icons with no
    /// network dependency. NOT a production data source — production wires the real
    /// <c>UnityWebRequestSpriteSource</c> against a style's <c>sprite</c> URL, via <c>SpriteSourceFactory</c>.
    /// </summary>
    public sealed class FixtureSpriteSource : ISpriteSource
    {
        public UniTask<SpriteResponse> FetchAsync(CancellationToken ct = default)
        {
            string jsonPath = Path.Combine(Application.dataPath, "Fixtures", "sprites", "sample-sprite.json");
            string pngPath = Path.Combine(Application.dataPath, "Fixtures", "sprites", "sample-sprite.png");

            if (!File.Exists(jsonPath) || !File.Exists(pngPath))
            {
                return UniTask.FromResult(SpriteResponse.Absent());
            }

            string json = File.ReadAllText(jsonPath);
            byte[] png = File.ReadAllBytes(pngPath);
            return UniTask.FromResult(new SpriteResponse { Json = json, Png = png, HasData = true });
        }

        public void Dispose()
        {
        }
    }
}
