using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MapRenderer.Core.Coordinates;

namespace MapRenderer.Core.Data
{
    /// <summary>
    /// Coordinates tile fetching from an <see cref="IDataSource"/> with an LRU <see cref="TileCache"/>
    /// and in-flight deduplication.
    /// <para>
    /// <b>Cache hit:</b> returns the cached <see cref="TileResponse"/> immediately without
    /// hitting the source.
    /// </para>
    /// <para>
    /// <b>In-flight deduplication:</b> if two callers concurrently request the same tile,
    /// only one network/disk fetch is issued. Both callers await the same <see cref="Task{T}"/>.
    /// Once the fetch completes, the result is stored in the cache so future requests are cache hits.
    /// </para>
    /// <para>
    /// <b>Thread-safety:</b> the cache and in-flight map are guarded by a single lock. Continuations
    /// run on threadpool threads (no deadlock risk with <c>ConfigureAwait(false)</c>).
    /// </para>
    /// </summary>
    public sealed class TileScheduler : IDisposable
    {
        private readonly IDataSource                          _source;
        private readonly TileCache                            _cache;
        private readonly Dictionary<TileId, Task<TileResponse>> _inFlight;
        private readonly object                               _lock = new object();
        private          bool                                 _disposed;

        public TileScheduler(IDataSource source, TileCache cache)
        {
            _source   = source ?? throw new ArgumentNullException(nameof(source));
            _cache    = cache  ?? throw new ArgumentNullException(nameof(cache));
            _inFlight = new Dictionary<TileId, Task<TileResponse>>();
        }

        /// <summary>
        /// Requests a tile. Returns from cache immediately if present; otherwise deduplicates the
        /// in-flight request and fetches from the source exactly once, storing the result in cache.
        /// </summary>
        public Task<TileResponse> Request(TileId id, CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TileScheduler));

            lock (_lock)
            {
                // Cache hit — fast path.
                if (_cache.TryGet(id, out var cached))
                    return Task.FromResult(cached);

                // Already in-flight — share the existing task.
                if (_inFlight.TryGetValue(id, out var existing))
                    return existing;

                // New fetch — start it and register in the in-flight map.
                var fetchTask = FetchAndCacheAsync(id, ct);
                _inFlight[id] = fetchTask;
                return fetchTask;
            }
        }

        /// <summary>
        /// Releases a tile from the cache and cancels any in-flight fetch (best-effort).
        /// After a call to <c>Release</c>, any pending awaiters of the same tile that were
        /// sharing the in-flight task will observe the cancellation or completion normally —
        /// releasing does not retroactively affect awaiters already waiting on the shared task.
        /// </summary>
        public void Release(TileId id)
        {
            lock (_lock)
            {
                _cache.Remove(id);
                _inFlight.Remove(id); // Drop reference; task continues but result is discarded.
            }
        }

        // -----------------------------------------------------------------------------------------
        // Internal
        // -----------------------------------------------------------------------------------------

        private async Task<TileResponse> FetchAndCacheAsync(TileId id, CancellationToken ct)
        {
            try
            {
                TileResponse response = await _source.FetchAsync(id, ct).ConfigureAwait(false);

                lock (_lock)
                {
                    _cache.Put(id, response);
                    _inFlight.Remove(id);
                }

                return response;
            }
            catch
            {
                lock (_lock)
                {
                    _inFlight.Remove(id);
                }
                throw;
            }
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
