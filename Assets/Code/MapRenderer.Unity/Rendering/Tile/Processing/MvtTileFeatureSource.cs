using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Tiles;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A / A7 (design §B): the MVT implementation of the raised <see cref="ITileFeatureSource"/> seam —
    /// byte-fetch (<see cref="IDataSource"/>), scheduling/caching (<see cref="TileScheduler"/>/
    /// <see cref="TileCache"/>), and <see cref="ITileDecoder"/> resolution all live HERE now, encapsulated
    /// behind <see cref="GetTile"/>; the coordinator (<c>TileManager</c>) never names any of them. Wraps the
    /// UNCHANGED <see cref="TileScheduler"/> — this is a boundary re-seam, not a fetch-behaviour change (§D).
    ///
    /// <para><see cref="GetTile"/> mints a <see cref="SharedTileDecode"/> — the A4/A5b decode-once-shared,
    /// LAZY handle (§B decision) — relocated verbatim from the old <c>TileManager</c> fetch-observe site.
    /// No decode happens here; the handle decodes on its first <c>GetOrDecode()</c> caller, off-main, exactly
    /// as before the raise.</para>
    ///
    /// Internal (not public): constructed only from <c>MapView.BuildSourceSpecs</c> (the one production site
    /// that names this type) and from the test assembly via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal sealed class MvtTileFeatureSource : ITileFeatureSource
    {
        private readonly IDataSource   _byteSource;
        private readonly bool          _ownsByteSource;
        private readonly TileScheduler _scheduler;
        private readonly TileCache     _cache;

        /// <param name="byteSource">The byte fetcher. Owned (disposed on <see cref="Dispose"/>) iff
        /// <paramref name="ownsByteSource"/> — mirrors the exact ownership <c>TileManager.SetSources</c> used
        /// to apply itself before the raise.</param>
        /// <param name="cacheCapacity">LRU capacity for the internal <see cref="TileCache"/> — moved here from
        /// the old <c>TileManager.SetSources</c>'s <c>new TileCache(capacity: 256)</c>.</param>
        public MvtTileFeatureSource(IDataSource byteSource, int cacheCapacity = 256, bool ownsByteSource = true)
        {
            _byteSource     = byteSource;
            _ownsByteSource = ownsByteSource;
            _cache          = new TileCache(cacheCapacity);
            _scheduler      = new TileScheduler(byteSource, _cache);
        }

        /// <summary>Relocated verbatim from the old <c>TileManager</c> fetch-observe mint: fetch via the
        /// (unchanged) scheduler, then wrap present bytes in a lazy <see cref="SharedTileDecode"/> — absent
        /// (<c>!HasData</c>) maps to a null handle, the coordinator's null-for-absent contract. This method
        /// never hops back to the main thread — the scheduler's own continuation already ends on the pool
        /// (its internal thread-pool switch), and this method adds only a synchronous mint on top, so the
        /// drain-spin's <c>configureAwait:false</c> pool-completion invariant is preserved (§G-1).</summary>
        public async UniTask<IDecodedTileHandle> GetTile(TileId id, CancellationToken ct = default)
        {
            TileResponse resp = await _scheduler.Request(id, ct);
            return (resp.HasData && resp.Bytes != null)
                ? new SharedTileDecode(resp.Bytes, TileDecoders.ForEncoding(resp.Encoding))
                : null;
        }

        public void Release(TileId id) => _scheduler.Release(id);

        public int InFlightCount => _scheduler.InFlightCount;

        /// <summary>Exact teardown ownership the old <c>TileManager.SetSources</c>/<c>DisposePipelines</c>
        /// applied: the scheduler always disposes (non-owning of source/cache, but its own CTSs/maps are
        /// this source's to free); the byte source disposes only if this instance owns it.</summary>
        public void Dispose()
        {
            _scheduler.Dispose();
            if (_ownsByteSource) _byteSource?.Dispose();
        }
    }
}
