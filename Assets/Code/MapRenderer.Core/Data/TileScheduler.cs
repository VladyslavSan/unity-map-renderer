using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;

namespace MapRenderer.Core.Data
{
    /// <summary>
    /// Coordinates tile fetching from an <see cref="IDataSource"/> with an LRU <see cref="TileCache"/>
    /// and in-flight deduplication.
    /// <para>
    /// <b>In-flight deduplication:</b> concurrent requests for the same tile share one fetch.
    /// </para>
    /// <para>
    /// <b>Per-tile cancellation:</b> <see cref="Release"/> cancels the tile's fetch. If the source
    /// ignores cancellation and completes anyway, the result is discarded, not cached.
    /// </para>
    /// <para>
    /// <b>Thread-safety:</b> one lock guards the cache, the in-flight map, and the CTS map. The
    /// scheduler never switches threads itself. Post-fetch bookkeeping runs on the thread that
    /// finished the fetch; <see cref="Release"/> and <c>Dispose</c> run on their caller's thread.
    /// The lock serializes all of them.
    /// </para>
    /// <para>
    /// <b>Negative caching:</b> an absent response is not written to the LRU cache. It is suppressed
    /// for a short, injectable-clock TTL, then re-fetched. Rationale: <c>docs/async-architecture.md</c>.
    /// </para>
    /// <para>
    /// <b>Dispose ownership:</b> the scheduler does not own the injected <see cref="IDataSource"/> or
    /// <see cref="TileCache"/> — the caller does. Disposing the scheduler cancels in-flight fetches but
    /// does not wait for them to finish.
    /// </para>
    /// </summary>
    public sealed class TileScheduler : VerifiedDisposable
    {
        private readonly IDataSource                                _source;
        private readonly TileCache                                  _cache;
        // .Preserve() lets multiple callers await the same task.
        private readonly Dictionary<TileId, UniTask<TileResponse>>  _inFlight;
        // Per-tile CTS: created on Request. Disposed by Release(), by Dispose(), or by the fetch if it
        // finishes first.
        private readonly Dictionary<TileId, CancellationTokenSource> _cts;
        // Negative cache: absent tile id maps to the time it may be re-fetched.
        private readonly Dictionary<TileId, DateTime>               _absentUntil;
        private readonly Func<DateTime>                             _clock;
        private readonly TimeSpan                                   _negativeTtl;
        private readonly object                                     _lock = new object();

        /// <summary>Default negative-cache TTL for absent tiles (HTTP 404/204, missing file).</summary>
        public static readonly TimeSpan DefaultNegativeTtl = TimeSpan.FromSeconds(5);

        /// <param name="source">Tile byte source. NOT owned/disposed by the scheduler (see class docs).</param>
        /// <param name="cache">LRU cache for present tiles. NOT owned/disposed by the scheduler.</param>
        /// <param name="negativeTtl">
        /// How long an absent (HasData=false) response suppresses re-fetch. Defaults to
        /// <see cref="DefaultNegativeTtl"/>. Pass <see cref="TimeSpan.Zero"/> to disable negative caching
        /// (every request re-fetches absent tiles).
        /// </param>
        /// <param name="clock">
        /// Injectable clock for the negative-cache TTL. Defaults to <c>() =&gt; DateTime.UtcNow</c>.
        /// Tests pass a fake clock to advance time deterministically.
        /// </param>
        public TileScheduler(
            IDataSource source,
            TileCache cache,
            TimeSpan? negativeTtl = null,
            Func<DateTime> clock = null)
        {
            _source      = source ?? throw new ArgumentNullException(nameof(source));
            _cache       = cache  ?? throw new ArgumentNullException(nameof(cache));
            _inFlight    = new Dictionary<TileId, UniTask<TileResponse>>();
            _cts         = new Dictionary<TileId, CancellationTokenSource>();
            _absentUntil = new Dictionary<TileId, DateTime>();
            _negativeTtl = negativeTtl ?? DefaultNegativeTtl;
            _clock       = clock ?? (() => DateTime.UtcNow);
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
        public UniTask<TileResponse> Request(TileId id, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            lock (_lock)
            {
                if (_cache.TryGet(id, out var cached))
                    return UniTask.FromResult(cached);

                if (_absentUntil.TryGetValue(id, out var until))
                {
                    if (_clock() < until)
                        return UniTask.FromResult(TileResponse.Absent(_source.Encoding));
                    // Expired — clear it and fall through to fetch.
                    _absentUntil.Remove(id);
                }

                if (_inFlight.TryGetValue(id, out var existing))
                    return existing;

                // Linked so Release() and the caller's own cancellation both work.
                var tileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _cts[id] = tileCts;

                var fetchTask = FetchAndCacheAsync(id, tileCts).Preserve();
                // A source that completes synchronously can finish and clean up before this line runs.
                // Only register the task if our CTS is still current — otherwise it already cleaned up.
                if (_cts.TryGetValue(id, out var stillRegistered) && stillRegistered == tileCts)
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
                // Clear suppression too, so a released tile re-fetches next time.
                _absentUntil.Remove(id);
                if (_cts.TryGetValue(id, out ctsToCancel))
                    _cts.Remove(id);
            }
            // Cancel and dispose outside the lock, to avoid holding the lock during Cancel().
            if (ctsToCancel != null)
            {
                ctsToCancel.Cancel();
                ctsToCancel.Dispose();
            }
        }

        // -----------------------------------------------------------------------------------------
        // Internal
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Fetches the tile, then updates the cache under lock. Every step after the fetch checks
        /// that this CTS is still the tile's registered fetch. If <see cref="Release"/> or
        /// <c>Dispose</c> removed it first, this method skips the cache write and skips disposing
        /// the CTS itself — the remover already owns that — but still returns or rethrows as normal.
        /// </summary>
        private async UniTask<TileResponse> FetchAndCacheAsync(TileId id, CancellationTokenSource tileCts)
        {
            TileResponse response = default;
            try
            {
                response = await _source.FetchAsync(id, tileCts.Token);
            }
            catch
            {
                bool removedByMe = false;
                lock (_lock)
                {
                    if (_cts.TryGetValue(id, out var currentCts) && currentCts == tileCts)
                    {
                        _inFlight.Remove(id);
                        _cts.Remove(id);
                        removedByMe = true;
                    }
                }
                // Skip if Release() or Dispose() removed it first — they own disposing it, and
                // disposing it again would race their Cancel() into an ObjectDisposedException.
                if (removedByMe) tileCts.Dispose();
                throw;
            }

            // No thread hop: this runs on the thread that finished the fetch.
            bool ownsCts = false;
            lock (_lock)
            {
                if (_cts.TryGetValue(id, out var currentCts) && currentCts == tileCts)
                {
                    if (response.HasData)
                    {
                        _cache.Put(id, response);
                    }
                    else if (_negativeTtl > TimeSpan.Zero)
                    {
                        // Absent — negative cache instead of the LRU.
                        _absentUntil[id] = _clock() + _negativeTtl;
                    }
                    _inFlight.Remove(id);
                    _cts.Remove(id);
                    ownsCts = true;
                }
            }

            // Skip if Release() or Dispose() removed it first — they own disposing it, and
            // disposing it again would race their Cancel() into an ObjectDisposedException.
            if (ownsCts) tileCts.Dispose();
            return response;
        }

        protected override void DoDispose()
        {
            List<CancellationTokenSource> ctsToDispose = null;
            lock (_lock)
            {
                if (_cts.Count > 0)
                {
                    ctsToDispose = new List<CancellationTokenSource>(_cts.Values);
                    _cts.Clear();
                    _inFlight.Clear();
                }
                _absentUntil.Clear();
            }
            if (ctsToDispose != null)
            {
                foreach (var cts in ctsToDispose)
                {
                    try { cts.Cancel(); } catch { /* best-effort */ }
                    try { cts.Dispose(); } catch { /* best-effort */ }
                }
            }
        }
    }
}
