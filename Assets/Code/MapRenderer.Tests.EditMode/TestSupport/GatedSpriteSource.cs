// A test double, so it lives in the test assembly rather than in engine-free Core. Engine-free itself
// (mirrors TestGlyphSource.cs's delegate-ctor shape), but NOT added to Tools/core-tests/core-tests.csproj —
// its only production consumer (SymbolSubsystem) is engine-bound, so the tooth it serves is [UnityTest]-only.

using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Text.Sprites;

namespace MapRenderer.Tests
{
    /// <summary>
    /// D6 (road-shields, docs/road-shields-design.md §3 D6) test double: an <see cref="ISpriteSource"/> whose
    /// <see cref="FetchAsync"/> defers entirely to a caller-supplied delegate — typically a held
    /// <c>UniTaskCompletionSource</c>'s <c>.Task</c>, so a test can drive a symbol build INTO the "sprite fetch
    /// still pending" state and then release it on demand. Mirrors <see cref="TestGlyphSource"/>'s delegate-ctor
    /// shape and the gated-glyph pattern already used by <c>SymbolSubsystemPumpTests</c>
    /// (<c>RestyleMidBuild_CancelsInFlightBuild_Silently_NoDisposedStateTouch</c>).
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
