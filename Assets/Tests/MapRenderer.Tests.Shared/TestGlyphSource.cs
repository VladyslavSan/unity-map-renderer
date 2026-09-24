// The IGlyphSource counterpart to TestDataSource, with a thread-safe fetch count. It stays engine-free
// because Tools/core-tests compiles it for GlyphManagerTests (a <Compile Include> in core-tests.csproj).

using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Text;

namespace MapRenderer.Tests
{
    /// <summary>
    /// A fake in-memory <see cref="IGlyphSource"/>: serves committed fixture bytes keyed by
    /// <c>(fontStack, rangeStart)</c>, never touches the network. Mirrors <see cref="TestDataSource"/>'s
    /// shape (thin wrapper over one fetch delegate; a convenience factory for the common
    /// dictionary-backed case).
    /// </summary>
    public sealed class TestGlyphSource : IGlyphSource
    {
        private readonly Func<string, int, CancellationToken, UniTask<GlyphRangeResponse>> _fetch;
        private int _fetchCount;
        private int _disposeCount;

        /// <summary>Number of <see cref="FetchAsync"/> calls (thread-safe).</summary>
        public int FetchCount => Volatile.Read(ref _fetchCount);

        /// <summary>Number of <see cref="Dispose"/> calls.</summary>
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public TestGlyphSource(Func<string, int, CancellationToken, UniTask<GlyphRangeResponse>> fetch)
        {
            _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        }

        /// <summary>
        /// Serves the given <c>(fontStack, rangeStart) -&gt; bytes</c> map; any other key is reported
        /// absent (mirrors a 404/204 range) rather than throwing.
        /// </summary>
        public static TestGlyphSource FromRanges(IReadOnlyDictionary<(string fontStack, int rangeStart), byte[]> ranges)
        {
            if (ranges == null) throw new ArgumentNullException(nameof(ranges));
            return new TestGlyphSource((fontStack, rangeStart, _) =>
                ranges.TryGetValue((fontStack, rangeStart), out byte[] bytes)
                    ? UniTask.FromResult(new GlyphRangeResponse(bytes))
                    : UniTask.FromResult(GlyphRangeResponse.Absent()));
        }

        public UniTask<GlyphRangeResponse> FetchAsync(string fontStack, int rangeStart, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _fetchCount);
            return _fetch(fontStack, rangeStart, ct);
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
