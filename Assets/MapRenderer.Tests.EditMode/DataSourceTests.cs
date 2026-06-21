// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.
//
// S51: migrated from Task/TaskCompletionSource to UniTask/UniTaskCompletionSource.
// HttpDataSource tests removed (HttpDataSource deleted from Core; HTTP moved to Unity layer
// as UnityWebRequestDataSource). Scheduler tests converted to async Task + await.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Mvt;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S03 acceptance tests: BYO data-source interface, LRU cache, scheduler deduplication,
    /// byte-identity (FileDataSource only — HttpDataSource moved to Unity layer in S51),
    /// absent-vs-error, and cancellation.
    ///
    /// All tests are deterministic and offline (no public endpoints, no Thread.Sleep).
    /// Timing is controlled via <see cref="UniTaskCompletionSource{T}"/>.
    ///
    /// S51: UniTask replaces Task throughout. Tests that called scheduler.Request().GetAwaiter().GetResult()
    /// are now async Task + await (UniTask.GetAwaiter().GetResult() is NOT a blocking wait).
    /// </summary>
    [TestFixture]
    public class DataSourceTests
    {
        // -----------------------------------------------------------------------------------------
        // Fixture loading — walks up from the current assembly to find Assets/Fixtures/
        // -----------------------------------------------------------------------------------------

        private static byte[] LoadFixtureBytes()
        {
            // Try several starting points so the fixture is found whether we are running under Unity
            // batch mode (cwd = project root, AppContext.BaseDirectory = Editor install dir) or
            // under `dotnet test` (AppContext.BaseDirectory is near the repo root).
            string[] starts = new[]
            {
                Directory.GetCurrentDirectory(),         // Unity batch-mode cwd = project root
                AppContext.BaseDirectory,                // dotnet test output dir
            };

            foreach (string start in starts)
            {
                string dir = start;
                for (int i = 0; i < 16 && dir != null; i++)
                {
                    string candidate = Path.Combine(dir, "Assets", "Fixtures", "sample-tile.bytes");
                    if (File.Exists(candidate))
                        return File.ReadAllBytes(candidate);
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }

            throw new FileNotFoundException(
                $"sample-tile.bytes not found. Tried walking up 16 levels from cwd={Directory.GetCurrentDirectory()}" +
                $" and AppContext.BaseDirectory={AppContext.BaseDirectory}");
        }

        // -----------------------------------------------------------------------------------------
        // 1. Byte identity: FileDataSource serves correct bytes
        //    (HttpDataSource removed from Core in S51 — HTTP moved to UnityWebRequestDataSource)
        // -----------------------------------------------------------------------------------------

        [Test]
        public async Task ByteIdentity_FileSource_ReturnsSameBytesAsFixture()
        {
            byte[] fixtureBytes = LoadFixtureBytes();
            var tileId = new TileId(0, 0, 0);

            // Write fixture into a temp dir so FileDataSource can read it.
            string tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string tilePath = Path.Combine(tempRoot, "0", "0", "0.mvt");
            Directory.CreateDirectory(Path.GetDirectoryName(tilePath));
            File.WriteAllBytes(tilePath, fixtureBytes);

            try
            {
                TileResponse fileResponse;
                using (var fileSource = new FileDataSource(tempRoot))
                {
                    fileResponse = await fileSource.FetchAsync(tileId);
                }

                Assert.IsTrue(fileResponse.HasData,  "FileDataSource: HasData must be true");
                Assert.AreEqual(TileEncoding.Mvt, fileResponse.Encoding, "File Encoding");

                // Byte-for-byte identical
                Assert.AreEqual(fixtureBytes.Length, fileResponse.Bytes.Length, "File byte count");
                CollectionAssert.AreEqual(fixtureBytes, fileResponse.Bytes, "File bytes match");

                // Decodes to the same layer/feature structure as the raw fixture
                MvtTile fileTile = MvtDecoder.Decode(fileResponse.Bytes);
                Assert.Greater(fileTile.GetLayer("countries").Features.Count, 0,
                    "countries layer must have features");
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
        }

        // -----------------------------------------------------------------------------------------
        // 2. Absent vs error
        // -----------------------------------------------------------------------------------------

        [Test]
        public async Task FileSource_MissingFile_ReturnsHasDataFalse()
        {
            string emptyRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyRoot);
            try
            {
                using var source = new FileDataSource(emptyRoot);
                var response = await source.FetchAsync(new TileId(5, 10, 15));
                Assert.IsFalse(response.HasData,   "Missing file → HasData must be false");
                Assert.IsNull(response.Bytes,       "Missing file → Bytes must be null");
                Assert.AreEqual(TileEncoding.Mvt, response.Encoding);
            }
            finally
            {
                Directory.Delete(emptyRoot, recursive: true);
            }
        }

        // -----------------------------------------------------------------------------------------
        // 3. Cache LRU semantics
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Cache_LRU_Evicts_LeastRecentlyUsed()
        {
            // Capacity 2: put A, B, C → A evicted, B and C survive.
            var cache = new TileCache(capacity: 2);
            var idA = new TileId(0, 0, 0);
            var idB = new TileId(0, 1, 0);
            var idC = new TileId(0, 2, 0);

            cache.Put(idA, MakeResponse(1));
            cache.Put(idB, MakeResponse(2));
            cache.Put(idC, MakeResponse(3));  // evicts A (LRU)

            Assert.AreEqual(2, cache.Count, "Count must equal capacity after overflow");
            Assert.IsFalse(cache.ContainsKey(idA), "A (LRU) must have been evicted");
            Assert.IsTrue(cache.ContainsKey(idB),  "B must survive");
            Assert.IsTrue(cache.ContainsKey(idC),  "C (MRU) must survive");
        }

        [Test]
        public void Cache_LRU_TryGet_PromotesMRU_ProtectsFromEviction()
        {
            // A, B in capacity-2 cache. TryGet(B) makes B MRU. Put(C) → A is evicted (not B).
            var cache = new TileCache(capacity: 2);
            var idA = new TileId(0, 0, 0);
            var idB = new TileId(0, 1, 0);
            var idC = new TileId(0, 2, 0);

            cache.Put(idA, MakeResponse(1));
            cache.Put(idB, MakeResponse(2));

            // Access B → B becomes MRU; A is now LRU.
            bool hit = cache.TryGet(idB, out _);
            Assert.IsTrue(hit, "TryGet(B) must return true");

            // Insert C → A (LRU) must be evicted, B (MRU) survives.
            cache.Put(idC, MakeResponse(3));

            Assert.AreEqual(2, cache.Count);
            Assert.IsFalse(cache.ContainsKey(idA), "A must be evicted (LRU after B was promoted)");
            Assert.IsTrue(cache.ContainsKey(idB),  "B must survive (was promoted to MRU)");
            Assert.IsTrue(cache.ContainsKey(idC),  "C must survive (just inserted)");
        }

        [Test]
        public void Cache_CountAndCapacity_AreCorrect()
        {
            var cache = new TileCache(capacity: 3);
            Assert.AreEqual(3, cache.Capacity);
            Assert.AreEqual(0, cache.Count);

            cache.Put(new TileId(0, 0, 0), MakeResponse(1));
            Assert.AreEqual(1, cache.Count);

            cache.Put(new TileId(0, 1, 0), MakeResponse(2));
            cache.Put(new TileId(0, 2, 0), MakeResponse(3));
            Assert.AreEqual(3, cache.Count);

            // Over capacity: count stays at capacity.
            cache.Put(new TileId(0, 3, 0), MakeResponse(4));
            Assert.AreEqual(3, cache.Count);
        }

        // -----------------------------------------------------------------------------------------
        // 4. Scheduler deduplication — deterministic, no Thread.Sleep
        // -----------------------------------------------------------------------------------------

        [Test]
        public async Task Scheduler_Dedupe_ConcurrentRequests_IssueSingleFetch()
        {
            var tcs  = new UniTaskCompletionSource<TileResponse>();
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return tcs.Task;
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(1, 2, 3);

            // Issue two concurrent requests before the fetch completes.
            var req1 = scheduler.Request(tileId);
            var req2 = scheduler.Request(tileId);

            // Only ONE fetch should have been issued.
            Assert.AreEqual(1, fetchCount, "Exactly one fetch should be in-flight for the same tile");

            // Complete the fetch.
            var expectedResponse = MakeResponse(42);
            tcs.TrySetResult(expectedResponse);

            // Both awaiters get the same bytes.
            var res1 = await req1;
            var res2 = await req2;

            Assert.AreEqual(1, fetchCount, "Fetch count must still be 1 after both awaiters resolve");
            Assert.IsTrue(res1.HasData && res2.HasData);
            Assert.AreEqual(expectedResponse.Bytes[0], res1.Bytes[0]);
            Assert.AreEqual(expectedResponse.Bytes[0], res2.Bytes[0]);
        }

        [Test]
        public async Task Scheduler_AfterFetch_CacheHit_NoSecondFetch()
        {
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return UniTask.FromResult(MakeResponse(7));
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(2, 3, 4);

            // First request — triggers a fetch.
            var res1 = await scheduler.Request(tileId);
            Assert.AreEqual(1, fetchCount);
            Assert.IsTrue(res1.HasData);

            // Second request — must come from cache (fetch count unchanged).
            var res2 = await scheduler.Request(tileId);
            Assert.AreEqual(1, fetchCount, "Second request for the same tile must be a cache hit");
            Assert.AreEqual(res1.Bytes[0], res2.Bytes[0]);
        }

        // -----------------------------------------------------------------------------------------
        // 5. Scheduler — cancellation and release semantics
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// When the source's UniTask is cancelled, the scheduler's awaiter observes
        /// OperationCanceledException.
        /// </summary>
        [Test]
        public async Task Scheduler_SourceCancellation_Propagates_ToAwaiter()
        {
            var tcs        = new UniTaskCompletionSource<TileResponse>();
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return tcs.Task;  // Returns pending UniTask; will be cancelled below.
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(3, 3, 3);

            // Issue a request; fetch is now in-flight (tcs.Task is pending).
            var requestTask = scheduler.Request(tileId);
            Assert.AreEqual(1, fetchCount, "Exactly one fetch should be in-flight");

            // Simulate the source cancelling.
            tcs.TrySetCanceled();

            // The awaiter must propagate OperationCanceledException.
            try
            {
                await requestTask;
                Assert.Fail("Cancelled source UniTask must propagate as OperationCanceledException to the awaiter");
            }
            catch (OperationCanceledException)
            {
                // Expected — test passes.
            }
        }

        [Test]
        public async Task Scheduler_Release_DropsReference_SubsequentRequestReFetches()
        {
            // Release() drops the cache entry and the in-flight reference. After release,
            // a new Request() must trigger a fresh fetch (not serve a stale cached result).
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return UniTask.FromResult(MakeResponse((byte)(fetchCount * 10)));
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(4, 4, 4);

            // First fetch — populates cache.
            await scheduler.Request(tileId);
            Assert.AreEqual(1, fetchCount, "First request triggers one fetch");

            // Release — evicts from cache.
            scheduler.Release(tileId);

            // Second request after release — must re-fetch (cache was evicted).
            await scheduler.Request(tileId);
            Assert.AreEqual(2, fetchCount, "Request after Release must re-fetch (not serve cache)");
        }

        // -----------------------------------------------------------------------------------------
        // S04 Batch A additions: per-tile cancellation, sync-completion guard
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Release() cancels the in-flight fetch (via per-tile CTS) and the result is NOT cached.
        /// </summary>
        [Test]
        public async Task Release_CancelsInFlightFetch_TileNotCached()
        {
            // Source blocks until manually signalled, to model a real in-flight HTTP fetch.
            var tcs   = new UniTaskCompletionSource<TileResponse>();
            int fetchCount = 0;
            var fakeSource = new FakeDataSourceCt((id, ct) =>
            {
                Interlocked.Increment(ref fetchCount);
                // Register a callback: when ct is cancelled, cancel the TCS so the blocked
                // fetch completes (with cancellation) and doesn't hang the test.
                ct.Register(() => tcs.TrySetCanceled());
                return tcs.Task;
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(5, 5, 5);

            // Start the fetch — it blocks.
            var pendingTask = scheduler.Request(tileId);
            Assert.AreEqual(1, fetchCount, "One fetch should be in-flight");

            // Release cancels the in-flight fetch.
            scheduler.Release(tileId);

            // Wait for the pending task to observe the cancellation.
            try { await pendingTask; }
            catch (OperationCanceledException) { /* expected */ }

            // The ORIGINAL cache must NOT contain the tile (cancelled result must be discarded).
            Assert.IsFalse(cache.TryGet(tileId, out _),
                "Original cache must NOT contain the tile after Release cancels the in-flight fetch");
            Assert.AreEqual(0, scheduler.InFlightCount,
                "In-flight map must be empty after the cancelled fetch completes");
        }

        /// <summary>
        /// A fetch completing SUCCESSFULLY after Release() must NOT populate the original cache.
        /// This exercises the CTS-identity guard in FetchAndCacheAsync: when Release() removes the CTS
        /// before the fetch completes, the guard detects the mismatch and skips TileCache.Put.
        /// </summary>
        [Test]
        public async Task Release_SourceIgnoresToken_CompletesSuccessfully_ResultNotCached()
        {
            // Source does NOT honour the cancellation token — it blocks and eventually returns
            // a successful result regardless of cancellation.
            var tcs        = new UniTaskCompletionSource<TileResponse>();
            var fakeSource = new FakeDataSource(id => tcs.Task);

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(7, 7, 7);

            // Start the fetch — blocks on tcs.
            var pendingTask = scheduler.Request(tileId);

            // Release: removes the CTS from _cts, invalidating the CTS-identity guard.
            scheduler.Release(tileId);

            // Now the source "returns" successfully (ignoring cancellation).
            tcs.TrySetResult(MakeResponse(55));

            // Await the pending task — it completes normally (no exception), because the source
            // did not honour the token.
            await pendingTask;

            // The CTS-identity guard must have detected the mismatch and skipped Put.
            Assert.IsFalse(cache.TryGet(tileId, out _),
                "Cache must NOT contain the tile when the source ignores the token and completes " +
                "successfully after Release() — exercises the CTS-identity guard.");
            Assert.AreEqual(0, scheduler.InFlightCount,
                "In-flight map must be empty after the late-completing fetch returns.");
        }

        /// <summary>
        /// When a source returns an already-completed UniTask (UniTask.FromResult), the sync-completion
        /// path must NOT leave a stale in-flight entry after Request() returns.
        /// </summary>
        [Test]
        public async Task SyncCompletingSource_NoStaleInFlightEntry()
        {
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                // Sync completion: returns an already-resolved UniTask.
                return UniTask.FromResult(MakeResponse(77));
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(6, 6, 6);

            // Start the fetch (sync-completing source).
            var fetchTask = scheduler.Request(tileId);

            // Await completion so the async continuation has run.
            await fetchTask;

            // After completion the in-flight map must be empty for this tile.
            Assert.AreEqual(0, scheduler.InFlightCount,
                "In-flight map must be empty after a sync-completing fetch completes. " +
                "A stale entry means the UniTask.SwitchToThreadPool guard is missing or broken.");
        }

        // -----------------------------------------------------------------------------------------
        // S06 Batch A: negative-caching policy (item b) — fake clock, no wall-clock sleeps
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// An absent tile (HasData=false) is NOT re-fetched within the negative-cache TTL.
        /// </summary>
        [Test]
        public async Task NegativeCache_AbsentTile_NotRefetchedWithinTtl()
        {
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return UniTask.FromResult(TileResponse.Absent(TileEncoding.Mvt));
            });

            var now    = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clock  = new FakeClock(now);
            var cache  = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache,
                negativeTtl: TimeSpan.FromSeconds(5), clock: clock.Now);
            var tileId = new TileId(9, 1, 1);

            // First request — issues a fetch that reports absent.
            var r1 = await scheduler.Request(tileId);
            Assert.IsFalse(r1.HasData, "First response must be absent");
            Assert.AreEqual(1, fetchCount, "First request issues exactly one fetch");

            // Advance the clock but stay inside the TTL.
            clock.Advance(TimeSpan.FromSeconds(2));

            // Second request — must be served from the negative cache, NOT re-fetched.
            var r2 = await scheduler.Request(tileId);
            Assert.IsFalse(r2.HasData, "Second response must still be absent");
            Assert.AreEqual(1, fetchCount,
                "Absent tile must NOT be re-fetched within the negative-cache TTL (item b).");

            // The absent tile must NOT have been written to the LRU cache.
            Assert.IsFalse(cache.ContainsKey(tileId),
                "Absent tile must not be stored in the LRU TileCache (no indefinite caching).");
        }

        /// <summary>
        /// After the negative-cache TTL expires, an absent tile IS re-fetched.
        /// </summary>
        [Test]
        public async Task NegativeCache_RefetchesAfterTtlExpiry()
        {
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return UniTask.FromResult(TileResponse.Absent(TileEncoding.Mvt));
            });

            var now    = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clock  = new FakeClock(now);
            var cache  = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache,
                negativeTtl: TimeSpan.FromSeconds(5), clock: clock.Now);
            var tileId = new TileId(9, 2, 2);

            await scheduler.Request(tileId);
            Assert.AreEqual(1, fetchCount, "First request issues one fetch");

            // Advance past the TTL.
            clock.Advance(TimeSpan.FromSeconds(6));

            await scheduler.Request(tileId);
            Assert.AreEqual(2, fetchCount,
                "Absent tile must be re-fetched once the negative-cache TTL has expired (item b).");
        }

        /// <summary>
        /// A negative TTL of zero disables negative caching entirely.
        /// </summary>
        [Test]
        public async Task NegativeCache_ZeroTtl_AlwaysRefetches()
        {
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return UniTask.FromResult(TileResponse.Absent(TileEncoding.Mvt));
            });

            var clock  = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var cache  = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache,
                negativeTtl: TimeSpan.Zero, clock: clock.Now);
            var tileId = new TileId(9, 3, 3);

            await scheduler.Request(tileId);
            await scheduler.Request(tileId);
            Assert.AreEqual(2, fetchCount,
                "With negativeTtl == TimeSpan.Zero, an absent tile is re-fetched every request.");
        }

        // -----------------------------------------------------------------------------------------
        // S06 Batch A: Dispose ownership (item c)
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Dispose must NOT dispose the injected IDataSource — the scheduler is a non-owning coordinator.
        /// </summary>
        [Test]
        public void Dispose_DoesNotDisposeInjectedSource()
        {
            var spySource = new DisposeSpySource();
            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(spySource, cache);

            scheduler.Dispose();

            Assert.IsFalse(spySource.WasDisposed,
                "TileScheduler.Dispose must NOT dispose the injected IDataSource — the caller owns it " +
                "(it may be shared across schedulers or outlive one). See item c.");
        }

        /// <summary>
        /// Dispose cancels an in-flight fetch (its per-tile CTS is cancelled) so a source honouring the
        /// token observes cancellation.
        /// </summary>
        [Test]
        public async Task Dispose_CancelsInFlightFetch()
        {
            var tcs        = new UniTaskCompletionSource<TileResponse>();
            bool cancellationObserved = false;
            var fakeSource = new FakeDataSourceCt((id, ct) =>
            {
                ct.Register(() => { cancellationObserved = true; tcs.TrySetCanceled(); });
                return tcs.Task;
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(8, 8, 8);

            var pending = scheduler.Request(tileId);
            Assert.AreEqual(1, scheduler.InFlightCount, "One fetch should be in-flight");

            scheduler.Dispose();

            // The in-flight CTS was cancelled by Dispose; the source observes it.
            try { await pending; }
            catch (OperationCanceledException) { /* expected */ }

            Assert.IsTrue(cancellationObserved,
                "Dispose must cancel the in-flight fetch's per-tile CTS (the source observed cancellation).");
        }

        /// <summary>A clock whose value is advanced explicitly by the test (no wall-clock dependency).</summary>
        private sealed class FakeClock
        {
            private DateTime _now;
            public FakeClock(DateTime start) { _now = start; }
            public DateTime Now() => _now;
            public void Advance(TimeSpan by) { _now += by; }
        }

        /// <summary>Data source that records whether Dispose was called on it.</summary>
        private sealed class DisposeSpySource : IDataSource
        {
            public bool WasDisposed { get; private set; }
            public TileEncoding Encoding => TileEncoding.Mvt;
            // S51: returns UniTask<TileResponse> (was Task<TileResponse>).
            public UniTask<TileResponse> FetchAsync(TileId id, CancellationToken ct = default)
                => UniTask.FromResult(TileResponse.Absent(TileEncoding.Mvt));
            public void Dispose() { WasDisposed = true; }
        }

        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        private static TileResponse MakeResponse(byte seed)
            => new TileResponse(new byte[] { seed, (byte)(seed + 1), (byte)(seed + 2) }, TileEncoding.Mvt);

        // -----------------------------------------------------------------------------------------
        // Fake data sources for scheduler tests
        // -----------------------------------------------------------------------------------------

        private sealed class FakeDataSource : IDataSource
        {
            private readonly Func<TileId, UniTask<TileResponse>> _fetch;

            public TileEncoding Encoding => TileEncoding.Mvt;

            public FakeDataSource(Func<TileId, UniTask<TileResponse>> fetch)
            {
                _fetch = fetch;
            }

            // S51: returns UniTask<TileResponse> (was Task<TileResponse>).
            public UniTask<TileResponse> FetchAsync(TileId id, CancellationToken ct = default)
                => _fetch(id);

            public void Dispose() { }
        }

        /// <summary>
        /// Fake data source variant that exposes the CancellationToken to the fetch delegate,
        /// so tests can register cancellation callbacks.
        /// </summary>
        private sealed class FakeDataSourceCt : IDataSource
        {
            private readonly Func<TileId, CancellationToken, UniTask<TileResponse>> _fetch;

            public TileEncoding Encoding => TileEncoding.Mvt;

            public FakeDataSourceCt(Func<TileId, CancellationToken, UniTask<TileResponse>> fetch)
            {
                _fetch = fetch;
            }

            // S51: returns UniTask<TileResponse> (was Task<TileResponse>).
            public UniTask<TileResponse> FetchAsync(TileId id, CancellationToken ct = default)
                => _fetch(id, ct);

            public void Dispose() { }
        }
    }
}
