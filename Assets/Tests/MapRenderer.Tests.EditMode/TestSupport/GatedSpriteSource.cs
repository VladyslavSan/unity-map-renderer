// A test double, engine-free itself, but NOT added to Tools/core-tests/core-tests.csproj: its only
// production consumer (SymbolSubsystem) is engine-bound, so the tooth it serves is [UnityTest]-only.

using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Tests
{
    /// <summary>
    /// A road-shields test double: an <see cref="ISpriteSource"/> whose
    /// <see cref="FetchAsync"/> defers to a caller-supplied delegate — typically a held
    /// <c>UniTaskCompletionSource</c>'s <c>.Task</c>, so a test can hold a symbol build in the "sprite fetch
    /// still pending" state and release it on demand. Same shape as <see cref="TestGlyphSource"/>.
    /// </summary>
    public sealed class GatedSpriteSource : ISpriteSource
    {
        private readonly System.Func<CancellationToken, UniTask<SpriteResponse>> _fetch;

        public GatedSpriteSource(System.Func<CancellationToken, UniTask<SpriteResponse>> fetch)
        {
            _fetch = fetch ?? throw new System.ArgumentNullException(nameof(fetch));
        }

        public UniTask<SpriteResponse> FetchAsync(CancellationToken ct = default) => _fetch(ct);

        public void Dispose() { }
    }
}
