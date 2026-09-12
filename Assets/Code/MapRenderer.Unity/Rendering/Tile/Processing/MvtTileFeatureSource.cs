using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Data;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;

namespace MapRenderer.Unity.Rendering.Tile.Processing
{
    /// <summary>
    /// Epic A / A7 (design §B): the MVT implementation of the raised <see cref="ITileFeatureSource"/> seam —
    /// byte-fetch (<see cref="IDataSource"/>), scheduling/caching (<see cref="TileScheduler"/>/
    /// <see cref="TileCache"/>), and <see cref="ITileDecoder"/> resolution all live HERE now, encapsulated
    /// behind <see cref="GetTile"/>; the coordinator (<c>TileManager</c>) never names any of them. Wraps the
    /// UNCHANGED <see cref="TileScheduler"/> — this is a boundary re-seam, not a fetch-behaviour change (§D).
    ///
    /// <para><see cref="GetTile"/> fetches, then DECODES — once, on the pool, through
    /// <see cref="TileDecodeDispatch.DecodeAsync"/> — and hands back a <see cref="SharedDisposable{T}"/>
    /// carrying the caller's one reference. The lazy handle it used to mint is gone: a decode that only
    /// happens when somebody reads is a decode whose drop paths free nothing, which is the leak the
    /// reference count replaces.</para>
    ///
    /// Internal (not public): constructed only from <c>MapView.BuildSourceSpecs</c> (the one production site
    /// that names this type) and from the test assembly via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal sealed class MvtTileFeatureSource : ITileFeatureSource
    {
        private readonly IDataSource    _byteSource;
        private readonly bool           _ownsByteSource;
        private readonly TileScheduler  _scheduler;
        private readonly TileCache      _cache;
        private readonly IWorkScheduler _workScheduler;

        /// <param name="byteSource">The byte fetcher. Owned (disposed on <see cref="Dispose"/>) iff
        /// <paramref name="ownsByteSource"/> — mirrors the exact ownership <c>TileManager.SetSources</c> used
        /// to apply itself before the raise.</param>
        /// <param name="workScheduler">The execution policy the decode hop runs under — see
        /// <see cref="TileDecodeDispatch.DecodeAsync"/>.</param>
        /// <param name="cacheCapacity">LRU capacity for the internal <see cref="TileCache"/> — moved here from
        /// the old <c>TileManager.SetSources</c>'s <c>new TileCache(capacity: 256)</c>.</param>
        /// <param name="ownsByteSource">Whether this source disposes <paramref name="byteSource"/>.</param>
        public MvtTileFeatureSource(IDataSource byteSource, IWorkScheduler workScheduler,
            int cacheCapacity = 256, bool ownsByteSource = true)
        {
            _byteSource     = byteSource;
            _ownsByteSource = ownsByteSource;
            _cache          = new TileCache(cacheCapacity);
            _scheduler      = new TileScheduler(byteSource, _cache);
            _workScheduler  = workScheduler;
        }

        /// <summary>Fetch via the (unchanged) scheduler, then decode the bytes through
        /// <see cref="TileDecodeDispatch.DecodeAsync"/> — absent (<c>!HasData</c>) maps to a null handle, the
        /// coordinator's null-for-absent contract. This method never hops back to the main thread: the
        /// real invariant the drain-spin needs is completion staying OFF the PlayerLoop (see
        /// <see cref="TileDecodeDispatch"/>'s class doc), which <see cref="TileDecodeDispatch.DecodeAsync"/>
        /// supplies on the <c>HasData</c> path and synchronous/inline completion supplies otherwise — not
        /// the fetch scheduler ending on a thread-pool hop, which it no longer does.</summary>
        public async UniTask<SharedDisposable<IDecodedTile>> GetTile(TileId id, CancellationToken ct = default)
        {
            TileResponse resp = await _scheduler.Request(id, ct);
            // IR C1 P3: the tile address goes IN here, at the only decode site, and is never supplied again.
            // Everything downstream reads it off the decoded buffer instead of carrying its own copy.
            return (resp.HasData && resp.Bytes != null)
                ? await TileDecodeDispatch.DecodeAsync(
                    id, resp.Bytes, Decoders.ForEncoding(resp.Encoding), _workScheduler)
                : null;
        }

        public void Release(TileId id) => _scheduler.Release(id);

        public int InFlightCount => _scheduler.InFlightCount;

        /// <summary>Exact teardown ownership <c>SourceRegistry</c>'s restyle diff and <c>Dispose</c> apply:
        /// the scheduler always disposes (non-owning of source/cache, but its own CTSs/maps are this
        /// source's to free); the byte source disposes only if this instance owns it.</summary>
        public void Dispose()
        {
            _scheduler.Dispose();
            if (_ownsByteSource) _byteSource?.Dispose();
        }
    }
}
