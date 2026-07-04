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
    /// <b>Cache hit:</b> returns the cached <see cref="TileResponse"/> immediately without
    /// hitting the source.
    /// </para>
    /// <para>
    /// <b>In-flight deduplication:</b> if two callers concurrently request the same tile,
    /// only one network/disk fetch is issued. Both callers await the same preserved
    /// <see cref="UniTask{T}"/> (.Preserve() allows multiple awaiters on a UniTask struct).
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
    /// Continuations run on threadpool threads (UniTask default for RunOnThreadPool completions).
    /// </para>
    /// <para>
    /// <b>Negative caching (S06 gated item b):</b> a fetch that reports <c>HasData=false</c> (HTTP
    /// 404/204, missing file) is NOT written to the LRU <see cref="TileCache"/>. Instead it is recorded
    /// in a small scheduler-level negative cache with a <b>short TTL</b> (<see cref="_negativeTtl"/>).
    /// Within the TTL a re-request returns an absent response without hitting the source; after the TTL
    /// expires the tile is re-fetched. This avoids two failure modes: (1) caching an absent tile in the
    /// LRU with no expiry, where a transient 404 sticks until LRU eviction; and (2) skipping the cache
    /// entirely, which re-fetches a permanently-missing <i>visible</i> tile every single frame. A short
    /// TTL is the correct middle ground — it lets a recovered tile re-appear while suppressing per-frame
    /// re-fetch storms. The clock is injectable so tests advance time deterministically (no wall-clock
    /// sleeps).
    /// </para>
    /// <para>
    /// <b>Dispose ownership (S06 gated item c):</b> the scheduler is a <i>non-owning</i> coordinator. It
    /// does NOT dispose the injected <see cref="IDataSource"/> or <see cref="TileCache"/> — the caller
    /// that constructed and injected them owns their lifetime (they may be shared across schedulers or
    /// outlive one). <see cref="Dispose"/> cancels + disposes the per-tile CTSs and clears the maps, but
    /// does NOT block to drain/await in-flight fetches: a blocking drain in Dispose risks deadlock when
    /// called from the main thread while a continuation needs it. In-flight fetches are cancelled
    /// (best-effort) and their late completions are harmless (the CTS-identity guard skips the cache).
    /// A future caller that genuinely needs to await outstanding fetches should add an explicit
    /// <c>DrainAsync()</c>, not overload <c>Dispose</c>.
    /// </para>
    ///
    /// S51: migrated from Task&lt;TileResponse&gt; to UniTask&lt;TileResponse&gt;.
    /// No Task.Run, no Task.FromResult, no interop extensions.
    /// </summary>
    public sealed class TileScheduler : VerifiedDisposable
    {
        private readonly IDataSource                                _source;
        private readonly TileCache                                  _cache;
        // .Preserve() is called before storing so multiple callers can await the same UniTask struct.
        private readonly Dictionary<TileId, UniTask<TileResponse>>  _inFlight;
        // Per-tile CTS — created on Request, cancelled on Release, disposed on completion.
        private readonly Dictionary<TileId, CancellationTokenSource> _cts;
        // Negative cache: absent tile id → wall-clock instant after which it may be re-fetched.
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
                // Cache hit — fast path.
                if (_cache.TryGet(id, out var cached))
                    return UniTask.FromResult(cached);

                // Negative-cache hit — a recent fetch reported this tile absent and the TTL has not
                // expired. Return an absent response without re-issuing a fetch. (S06 gated item b.)
                if (_absentUntil.TryGetValue(id, out var until))
                {
                    if (_clock() < until)
                        return UniTask.FromResult(TileResponse.Absent(_source.Encoding));
                    // TTL expired — drop the stale entry and fall through to re-fetch.
                    _absentUntil.Remove(id);
                }

                // Already in-flight — share the existing preserved UniTask.
                if (_inFlight.TryGetValue(id, out var existing))
                    return existing;

                // New fetch — create a per-tile CTS linked to the caller's token so both
                // Release() and caller cancellation can abort the fetch.
                var tileCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _cts[id] = tileCts;

                // Start the fetch. .Preserve() allows multiple callers to await the same UniTask struct.
                // Invariant: _inFlight[id] is assigned BEFORE FetchAndCacheAsync can observe the
                // Remove / Put path, because after the source fetch the async method hops to the
                // ThreadPool (UniTask.SwitchToThreadPool) before touching _inFlight. This guarantees
                // the assignment below completes before the Remove runs, even when the source returns
                // an already-completed UniTask (sync-completion in-flight leak guard).
                var fetchTask = FetchAndCacheAsync(id, tileCts).Preserve();
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
                _absentUntil.Remove(id);   // a released tile should re-fetch on next request, not stay suppressed
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

        private async UniTask<TileResponse> FetchAndCacheAsync(TileId id, CancellationTokenSource tileCts)
        {
            TileResponse response = default;
            try
            {
                // Fetch first — this increments any caller-side fetchCount immediately, preserving
                // the existing test invariant (fetchCount == 1 right after Request returns).
                response = await _source.FetchAsync(id, tileCts.Token);
            }
            catch
            {
                bool removedByMe = false;
                lock (_lock)
                {
                    // Only clean up if we are still the registered fetch for this tile.
                    if (_cts.TryGetValue(id, out var currentCts) && currentCts == tileCts)
                    {
                        _inFlight.Remove(id);
                        _cts.Remove(id);
                        removedByMe = true;
                    }
                }
                // Disposal ownership follows removal: only the caller that removed the CTS disposes it. If
                // Release() removed it instead (removedByMe stays false), it owns Cancel()+Dispose() — we must
                // NOT dispose, or we race its Cancel() into an ObjectDisposedException.
                if (removedByMe) tileCts.Dispose();
                throw;
            }

            // Sync-completion guard: force continuation onto the ThreadPool AFTER the fetch but
            // BEFORE the lock+cache work. This guarantees:
            // 1. _inFlight[id] = fetchTask has been assigned before the Remove runs (even when
            //    the source returns an already-completed UniTask — UniTask.FromResult sync-completion).
            // 2. No deadlock when the test calls .GetAwaiter().GetResult() on the main thread:
            //    UniTask.SwitchToThreadPool forces the ThreadPool, so the continuation runs freely.
            await UniTask.SwitchToThreadPool();

            bool ownsCts = false;
            lock (_lock)
            {
                // Only cache the result if this CTS is still the registered one for this tile.
                // If Release() was called between fetch-start and fetch-complete, _cts[id] was
                // removed (and the old CTS cancelled + disposed), so this guard skips caching.
                if (_cts.TryGetValue(id, out var currentCts) && currentCts == tileCts)
                {
                    if (response.HasData)
                    {
                        // Present tile → LRU cache (the well-tested path).
                        _cache.Put(id, response);
                    }
                    else if (_negativeTtl > TimeSpan.Zero)
                    {
                        // Absent tile (404/204/missing) → short-TTL negative cache, NOT the LRU.
                        // Avoids caching an absent tile indefinitely while suppressing per-frame
                        // re-fetch of a permanently-missing visible tile. (S06 gated item b.)
                        _absentUntil[id] = _clock() + _negativeTtl;
                    }
                    // else: negative caching disabled (ttl == 0) — absent tile is neither cached
                    // nor suppressed; the next request re-fetches.
                    _inFlight.Remove(id);
                    _cts.Remove(id);
                    ownsCts = true;
                }
                // else: tile was released while in-flight — discard result, don't cache.
            }

            // Disposal ownership follows removal (see the catch above): if Release() removed this CTS, it owns
            // Cancel()+Dispose(); disposing here too would race its Cancel() into an ObjectDisposedException.
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
            // Ownership (S06 gated item c — DECIDED): TileScheduler is a NON-OWNING coordinator. It does
            // NOT dispose the injected IDataSource or TileCache — the caller owns their lifetime (they may
            // be shared across schedulers or outlive one). Dispose cancels + disposes the per-tile CTSs
            // and clears the maps, but does NOT block to drain/await in-flight fetches (a blocking drain in
            // Dispose risks deadlock from the main thread). In-flight fetches are cancelled best-effort and
            // their late completions are harmless (the CTS-identity guard skips the cache). A future caller
            // needing to await outstanding work should add an explicit DrainAsync(), not overload Dispose.
        }
    }
}
