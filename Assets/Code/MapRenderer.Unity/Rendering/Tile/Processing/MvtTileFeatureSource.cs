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
    /// The MVT <see cref="ITileFeatureSource"/>: byte-fetch (<see cref="IDataSource"/>), scheduling/caching
    /// (<see cref="TileScheduler"/>/<see cref="TileCache"/>) and <see cref="ITileDecoder"/> resolution live
    /// behind <see cref="GetTile"/>. It decodes once on the pool (<see cref="TileDecodeDispatch.DecodeAsync"/>)
    /// and returns a <see cref="SharedDisposable{T}"/> with the caller's one reference. It is never lazy:
    /// a lazy handle would have drop paths that free nothing.
    /// </summary>
    internal sealed class MvtTileFeatureSource : ITileFeatureSource
    {
        private readonly IDataSource    _byteSource;
        private readonly bool           _ownsByteSource;
        private readonly TileScheduler  _scheduler;
        private readonly TileCache      _cache;
        private readonly IWorkScheduler _workScheduler;

        /// <param name="byteSource">The byte fetcher. Owned (disposed on <see cref="Dispose"/>) iff
        /// <paramref name="ownsByteSource"/>.</param>
        /// <param name="workScheduler">The execution policy the decode hop runs under — see
        /// <see cref="TileDecodeDispatch.DecodeAsync"/>.</param>
        /// <param name="cacheCapacity">LRU capacity for the internal <see cref="TileCache"/>.</param>
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

        /// <summary>Fetches via the scheduler, then decodes through
        /// <see cref="TileDecodeDispatch.DecodeAsync"/>; absent (<c>!HasData</c>) maps to a null handle. It
        /// never hops back to the main thread. The drain-spin needs completion off the PlayerLoop (see
        /// <see cref="TileDecodeDispatch"/>): the decode hop gives that on the <c>HasData</c> path, and inline
        /// completion gives it otherwise.</summary>
        public async UniTask<SharedDisposable<IDecodedTile>> GetTile(TileId id, CancellationToken ct = default)
        {
            TileResponse resp = await _scheduler.Request(id, ct);
            // The tile address goes IN here, at the only decode site, and is never supplied again.
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
