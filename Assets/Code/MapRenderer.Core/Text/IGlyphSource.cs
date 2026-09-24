// Engine-free: no UnityEngine dependency.

using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// BYO glyph-PBF fetch abstraction, distinct from the tile-keyed <see cref="Data.IDataSource"/>: a glyph
    /// request is keyed by a fontstack string + 256-aligned range, not a <see cref="Geo.TileId"/>. The Unity
    /// side implements it as <c>UnityWebRequestGlyphSource</c>. A Core implementation uses only the
    /// PlayerLoop-independent UniTask subset (<c>SwitchToThreadPool</c>, <c>UniTaskCompletionSource</c>,
    /// <c>UniTask.FromResult</c>), as <see cref="Data.IDataSource"/> does.
    /// </summary>
    public interface IGlyphSource : IDisposable
    {
        /// <summary>
        /// Fetches one glyph-PBF range's raw bytes. Returns a <see cref="GlyphRangeResponse"/> with
        /// <c>HasData=false</c> when the range is explicitly absent (e.g. 404/204); throws on a genuine
        /// error (network failure, I/O error, unexpected HTTP status).
        /// </summary>
        /// <param name="fontStack">
        /// The font(s) to fetch: the joined <see cref="FontStack.RequestToken"/> or, as
        /// <c>GlyphManager</c> passes, a single font name.
        /// </param>
        /// <param name="rangeStart">The 256-aligned range base (see <see cref="FontStackResolver.ComputeRangeStart"/>).</param>
        UniTask<GlyphRangeResponse> FetchAsync(string fontStack, int rangeStart, CancellationToken ct = default);
    }
}
