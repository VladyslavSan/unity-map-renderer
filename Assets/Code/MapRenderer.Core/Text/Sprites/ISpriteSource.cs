// Engine-free: no UnityEngine dependency.

using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// BYO sprite-sheet fetch abstraction, distinct from <see cref="IGlyphSource"/> (fontstack+range-keyed):
    /// a sprite sheet is a single, keyless pair of resources (index JSON + PNG) per style, per the MapLibre
    /// Style Spec's root <c>sprite</c> URL. The Unity-side <c>UnityWebRequestSpriteSource</c> implementation
    /// mirrors <c>UnityWebRequestGlyphSource</c> — this interface is the seam it implements.
    ///
    /// Core implementations use only the PlayerLoop-independent UniTask subset
    /// (<c>UniTask.RunOnThreadPool</c>/<c>SwitchToThreadPool</c>, <c>UniTaskCompletionSource</c>,
    /// <c>UniTask.FromResult</c>) exactly as <see cref="IGlyphSource"/> does.
    /// </summary>
    public interface ISpriteSource : IDisposable
    {
        /// <summary>
        /// Fetches the sheet's index JSON + PNG bytes. Returns a <see cref="SpriteResponse"/> with
        /// <c>HasData=false</c> when the sheet is explicitly absent (e.g. 404/204); throws on a genuine
        /// error (network failure, I/O error, unexpected HTTP status).
        /// </summary>
        UniTask<SpriteResponse> FetchAsync(CancellationToken ct = default);
    }
}
