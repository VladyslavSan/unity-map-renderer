// Engine-free: no UnityEngine dependency.

using System;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace MapRenderer.Core.Text
{
    /// <summary>
    /// BYO glyph-PBF fetch abstraction, distinct from <see cref="Data.IDataSource"/> (tile-keyed):
    /// glyph requests are keyed by a fontstack request-key string + 256-aligned range, not a
    /// <see cref="Geo.TileId"/>. The Unity-side <c>UnityWebRequestGlyphSource</c> implementation (mirrors
    /// <c>UnityWebRequestDataSource</c>) is a later, deferred batch — this interface is the seam it will
    /// implement.
    ///
    /// Core implementations use only the PlayerLoop-independent UniTask subset
    /// (<c>UniTask.RunOnThreadPool</c>/<c>SwitchToThreadPool</c>, <c>UniTaskCompletionSource</c>,
    /// <c>UniTask.FromResult</c>) exactly as <see cref="Data.IDataSource"/> does.
    /// </summary>
    public interface IGlyphSource : IDisposable
    {
        /// <summary>
        /// Fetches one glyph-PBF range's raw bytes. Returns a <see cref="GlyphRangeResponse"/> with
        /// <c>HasData=false</c> when the range is explicitly absent (e.g. 404/204); throws on a genuine
        /// error (network failure, I/O error, unexpected HTTP status).
        /// </summary>
        /// <param name="fontStack">
        /// The request-key string identifying the font(s) to fetch — either the full joined
        /// <see cref="FontStack.RequestToken"/> or a single font name, depending on the glyph host's
        /// convention (decided by the fetch-wiring layer, deferred).
        /// </param>
        /// <param name="rangeStart">The 256-aligned range base (see <see cref="FontStackResolver.ComputeRangeStart"/>).</param>
        UniTask<GlyphRangeResponse> FetchAsync(string fontStack, int rangeStart, CancellationToken ct = default);
    }
}
