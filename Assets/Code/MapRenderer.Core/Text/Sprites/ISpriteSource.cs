// Engine-free: no UnityEngine dependency.

using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace MapRenderer.Core.Text.Sprites
{
    /// <summary>
    /// BYO sprite-sheet fetch abstraction. Unlike <see cref="IGlyphSource"/> (fontstack+range-keyed), a
    /// sprite sheet is one keyless pair (index JSON + PNG) per style, from the root <c>sprite</c> URL.
    /// <c>UnityWebRequestSpriteSource</c> implements it. Core implementations use only the
    /// PlayerLoop-independent UniTask subset, as <see cref="IGlyphSource"/> does.
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
