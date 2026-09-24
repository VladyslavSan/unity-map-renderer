// The single test double for IDataSource. It stays engine-free because Tools/core-tests compiles it for
// TileSchedulerOrderingTests; core-tests.csproj is not glob-based, so it carries a <Compile Include> entry.

using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;

namespace MapRenderer.Tests
{
    /// <summary>
    /// An <see cref="IDataSource"/> test double over one fetch delegate: <see cref="FromBytes"/>,
    /// <see cref="Absent"/>, <see cref="FromFetch"/>, or the ctor, whose delegate also sees the
    /// <see cref="CancellationToken"/>. <see cref="FetchCount"/>, <see cref="WasDisposed"/> and
    /// <see cref="DisposeCount"/> are thread-safe, because the live loop fetches from the thread pool.
    /// </summary>
    public sealed class TestDataSource : IDataSource
    {
        private readonly Func<TileId, CancellationToken, UniTask<TileResponse>> _fetch;
        private int _fetchCount;
        private int _disposeCount;

        public TileEncoding Encoding { get; }

        /// <summary>Number of <see cref="FetchAsync"/> calls (thread-safe).</summary>
        public int FetchCount => Volatile.Read(ref _fetchCount);

        /// <summary>True once <see cref="Dispose"/> has been called at least once.</summary>
        public bool WasDisposed => Volatile.Read(ref _disposeCount) > 0;

        /// <summary>Number of <see cref="Dispose"/> calls.</summary>
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public TestDataSource(Func<TileId, CancellationToken, UniTask<TileResponse>> fetch,
                              TileEncoding encoding = TileEncoding.Mvt)
        {
            _fetch   = fetch ?? throw new ArgumentNullException(nameof(fetch));
            Encoding = encoding;
        }

        // ── Convenience factories ──────────────────────────────────────────────────────────────

        /// <summary>Serves the same bytes for every tile id.</summary>
        public static TestDataSource FromBytes(byte[] bytes, TileEncoding encoding = TileEncoding.Mvt)
            => new TestDataSource((_, __) => UniTask.FromResult(new TileResponse(bytes, encoding)), encoding);

        /// <summary>Reports every tile as absent (<c>HasData=false</c>).</summary>
        public static TestDataSource Absent(TileEncoding encoding = TileEncoding.Mvt)
            => new TestDataSource((_, __) => UniTask.FromResult(TileResponse.Absent(encoding)), encoding);

        /// <summary>A per-coord fetch delegate that does not need the cancellation token.</summary>
        public static TestDataSource FromFetch(Func<TileId, UniTask<TileResponse>> fetch,
                                               TileEncoding encoding = TileEncoding.Mvt)
        {
            if (fetch == null) throw new ArgumentNullException(nameof(fetch));
            return new TestDataSource((id, _) => fetch(id), encoding);
        }

        // ── IDataSource ────────────────────────────────────────────────────────────────────────

        public UniTask<TileResponse> FetchAsync(TileId coord, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _fetchCount);
            return _fetch(coord, ct);
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
