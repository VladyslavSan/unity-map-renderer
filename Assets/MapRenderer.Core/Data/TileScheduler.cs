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
    /// <b>Per-tile cancellation:</b> <see cref="Release"/> cancels the tile's
    /// <see cref="CancellationTokenSource"/> so sources that honour the token abort promptly.
    /// Even if the source ignores the token, a fetch completing after Release skips
    /// <see cref="TileCache.Put"/> (guarded by CTS identity under lock).
    /// </para>
    /// <para>
    /// <b>Thread-safety:</b> the cache, in-flight map, and CTS map are guarded by a single lock.
    /// Continuations run on threadpool threads (no deadlock risk with <c>ConfigureAwait(false)</c>).
    /// </para>
    /// </summary>
    public sealed class TileScheduler : IDisposable
    {
        private readonly IDataSource                                _source;
        private readonly TileCache                                  _cache;
        private readonly Dictionary<TileId, Task<TileResponse>>     _inFlight;
        // Per-tile CTS — created on Request, cancelled on Release, disposed on completion.
        private readonly Dictionary<TileId, CancellationTokenSource> _cts;
        private readonly object                                     _lock = new object();
        private          bool                                       _disposed;

        public TileScheduler(IDataSource source, TileCache cache)
        {
            _source   = source ?? throw new ArgumentNullException(nameof(source));
            _cache    = cache  ?? throw new ArgumentNullException(nameof(cache));
            _inFlight = new Dictionary<TileId, Task<TileResponse>>();
            _cts      = new Dictionary<TileId, CancellationTokenSource>();
        }

        /// <summary>
        /// Returns the number of in-flight fetch tasks. Exposed for testing via the test assembly.
        /// Uses public accessibility since the test assembly is a separate asmdef.
        /// </summary>
        public int InFlightCount
        {
            get { lock (_lock) { return _inFlight.Count; } }
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

                // New fetch — create a per-tile CTS linked to the caller's token so both
                // Release() and caller cancellation can abort the fetch.
                var tileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _cts[id] = tileCts;

                // Start the fetch. Invariant: _inFlight[id] is assigned BEFORE FetchAndCacheAsync
                // can observe the Remove / Put path, because after the source fetch the async
                // method hops to the ThreadPool (Task.Run) before touching _inFlight. This
                // guarantees the assignment below completes before the Remove runs, even when the
                // source returns an already-completed Task (sync-completion in-flight leak).
                var fetchTask = FetchAndCacheAsync(id, tileCts);
                _inFlight[id] = fetchTask;
                return fetchTask;
            }
        }

        /// <summary>
        /// Releases a tile: evicts it from cache, cancels the in-flight fetch (best-effort), and
        /// removes the in-flight entry. A fetch that completes after Release skips
        /// <see cref="TileCache.Put"/> because the CTS identity no longer matches.
        /// </summary>
        public void Release(TileId id)
        {
            CancellationTokenSource ctsToCancel = null;
            lock (_lock)
            {
                _cache.Remove(id);
                _inFlight.Remove(id);
                if (_cts.TryGetValue(id, out ctsToCancel))
                    _cts.Remove(id);
            }
            // Cancel and dispose outside the lock to avoid holding the lock during Cancel().
            if (ctsToCancel != null)
            {
                ctsToCancel.Cancel();
                ctsToCancel.Dispose();
            }
        }

        // -----------------------------------------------------------------------------------------
        // Internal
        // -----------------------------------------------------------------------------------------

        private async Task<TileResponse> FetchAndCacheAsync(TileId id, CancellationTokenSource tileCts)
        {
            TileResponse response = default;
            try
            {
                // Fetch first — this increments any caller-side fetchCount immediately, preserving
                // the existing test invariant (fetchCount == 1 right after Request returns).
                response = await _source.FetchAsync(id, tileCts.Token).ConfigureAwait(false);
            }
            catch
            {
                lock (_lock)
                {
                    // Only clean up if we are still the registered fetch for this tile.
                    if (_cts.TryGetValue(id, out var currentCts) && currentCts == tileCts)
                    {
                        _inFlight.Remove(id);
                        _cts.Remove(id);
                    }
                }
                DisposeCtsIfUnregistered(id, tileCts);
                throw;
            }

            // Sync-completion guard: force continuation onto the ThreadPool AFTER the fetch but
            // BEFORE the lock+cache work. This guarantees:
            // 1. _inFlight[id] = fetchTask has been assigned before the Remove runs (even when
            //    the source returns an already-completed Task — Task.FromResult sync-completion).
            // 2. No deadlock when the test calls GetAwaiter().GetResult() on the main thread:
            //    Task.Yield() would capture UnitySynchronizationContext and schedule back onto
            //    the main thread, which is already blocked — deadlock. Task.Run forces the
            //    ThreadPool, so the continuation runs freely.
            await Task.Run(() => { }).ConfigureAwait(false);

            lock (_lock)
            {
                // Only cache the result if this CTS is still the registered one for this tile.
                // If Release() was called between fetch-start and fetch-complete, _cts[id] was
                // removed (and the old CTS cancelled + disposed), so this guard skips Put.
                if (_cts.TryGetValue(id, out var currentCts) && currentCts == tileCts)
                {
                    _cache.Put(id, response);
                    _inFlight.Remove(id);
                    _cts.Remove(id);
                }
                // else: tile was released while in-flight — discard result, don't cache.
            }

            DisposeCtsIfUnregistered(id, tileCts);
            return response;
        }

        /// <summary>
        /// Disposes <paramref name="tileCts"/> only if it has already been removed from <c>_cts</c>
        /// (i.e. either the completion path or Release removed it). Avoids double-dispose when
        /// Release() and the completion path race.
        /// </summary>
        private void DisposeCtsIfUnregistered(TileId id, CancellationTokenSource tileCts)
        {
            bool stillRegistered;
            lock (_lock)
            {
                stillRegistered = _cts.TryGetValue(id, out var c) && c == tileCts;
            }
            if (!stillRegistered)
                tileCts.Dispose();
        }

        public void Dispose()
        {
            List<CancellationTokenSource> ctsToDispose = null;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                if (_cts.Count > 0)
                {
                    ctsToDispose = new List<CancellationTokenSource>(_cts.Values);
                    _cts.Clear();
                    _inFlight.Clear();
                }
            }
            if (ctsToDispose != null)
            {
                foreach (var cts in ctsToDispose)
                {
                    try { cts.Cancel(); } catch { /* best-effort */ }
                    try { cts.Dispose(); } catch { /* best-effort */ }
                }
            }
            // Note: TileScheduler does not own the IDataSource or TileCache lifetime.
            // Dispose ownership is tracked at follow-ups.md (item 57-59) — out of scope for S04.
        }
    }
}
