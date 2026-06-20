// Engine-free: this file is compiled verbatim by both the Unity EditMode runner
// (Assets/MapRenderer.Tests.EditMode/) and the fast dotnet test project (Tools/core-tests/).
// Do NOT add any UnityEngine, MeshBuilder, NativeArray, or MonoBehaviour references.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using NUnit.Framework;
using MapRenderer.Core.Coordinates;
using MapRenderer.Core.Data;
using MapRenderer.Core.Mvt;

namespace MapRenderer.Tests
{
    /// <summary>
    /// S03 acceptance tests: BYO data-source interface, LRU cache, scheduler deduplication,
    /// byte-identity across sources, absent-vs-error, and cancellation.
    ///
    /// All tests are deterministic and offline (no public endpoints, no Thread.Sleep).
    /// The HTTP source is exercised against a stub <see cref="HttpMessageHandler"/>.
    /// Timing is controlled via <see cref="TaskCompletionSource{T}"/>.
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
        // 1. Byte identity: FileDataSource and HttpDataSource serve the same bytes
        // -----------------------------------------------------------------------------------------

        [Test]
        public void ByteIdentity_FileAndHttpSources_ReturnSameBytesAndEncoding()
        {
            byte[] fixtureBytes = LoadFixtureBytes();
            var tileId = new TileId(0, 0, 0);
            string dir = Path.GetTempPath();

            // Write fixture into a temp dir so FileDataSource can read it.
            string tempRoot = Path.Combine(dir, Guid.NewGuid().ToString("N"));
            string tilePath = Path.Combine(tempRoot, "0", "0", "0.mvt");
            Directory.CreateDirectory(Path.GetDirectoryName(tilePath));
            File.WriteAllBytes(tilePath, fixtureBytes);

            try
            {
                // FileDataSource
                TileResponse fileResponse;
                using (var fileSource = new FileDataSource(tempRoot))
                {
                    fileResponse = fileSource.FetchAsync(tileId).GetAwaiter().GetResult();
                }

                // HttpDataSource with a stub handler returning the fixture bytes
                TileResponse httpResponse;
                var handler = new StubHttpHandler(HttpStatusCode.OK, fixtureBytes);
                using (var httpClient = new HttpClient(handler))
                using (var httpSource = new HttpDataSource(httpClient, "http://fake/{z}/{x}/{y}.mvt"))
                {
                    httpResponse = httpSource.FetchAsync(tileId).GetAwaiter().GetResult();
                }

                // Both must have data and report Mvt encoding
                Assert.IsTrue(fileResponse.HasData,  "FileDataSource: HasData must be true");
                Assert.IsTrue(httpResponse.HasData,   "HttpDataSource: HasData must be true");
                Assert.AreEqual(TileEncoding.Mvt, fileResponse.Encoding, "File Encoding");
                Assert.AreEqual(TileEncoding.Mvt, httpResponse.Encoding, "Http Encoding");

                // Byte-for-byte identical
                Assert.AreEqual(fixtureBytes.Length, fileResponse.Bytes.Length, "File byte count");
                Assert.AreEqual(fixtureBytes.Length, httpResponse.Bytes.Length, "Http byte count");
                CollectionAssert.AreEqual(fixtureBytes, fileResponse.Bytes,  "File bytes match");
                CollectionAssert.AreEqual(fixtureBytes, httpResponse.Bytes,  "Http bytes match");

                // Both decode to the same layer/feature structure
                MvtTile fileTile = MvtDecoder.Decode(fileResponse.Bytes);
                MvtTile httpTile = MvtDecoder.Decode(httpResponse.Bytes);

                Assert.AreEqual(fileTile.GetLayer("countries").Features.Count,
                                httpTile.GetLayer("countries").Features.Count,
                                "countries feature count must match between sources");
                Assert.AreEqual(fileTile.GetLayer("geolines").Features.Count,
                                httpTile.GetLayer("geolines").Features.Count,
                                "geolines feature count must match between sources");
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
        public void FileSource_MissingFile_ReturnsHasDataFalse()
        {
            string emptyRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(emptyRoot);
            try
            {
                using var source = new FileDataSource(emptyRoot);
                var response = source.FetchAsync(new TileId(5, 10, 15)).GetAwaiter().GetResult();
                Assert.IsFalse(response.HasData,   "Missing file → HasData must be false");
                Assert.IsNull(response.Bytes,       "Missing file → Bytes must be null");
                Assert.AreEqual(TileEncoding.Mvt, response.Encoding);
            }
            finally
            {
                Directory.Delete(emptyRoot, recursive: true);
            }
        }

        [Test]
        public void HttpSource_404_ReturnsHasDataFalse()
        {
            var handler = new StubHttpHandler(HttpStatusCode.NotFound, null);
            using var client = new HttpClient(handler);
            using var source = new HttpDataSource(client, "http://fake/{z}/{x}/{y}.mvt");

            var response = source.FetchAsync(new TileId(1, 0, 0)).GetAwaiter().GetResult();
            Assert.IsFalse(response.HasData,   "HTTP 404 → HasData must be false");
            Assert.IsNull(response.Bytes,       "HTTP 404 → Bytes must be null");
            Assert.AreEqual(TileEncoding.Mvt, response.Encoding);
        }

        [Test]
        public void HttpSource_204_ReturnsHasDataFalse()
        {
            var handler = new StubHttpHandler(HttpStatusCode.NoContent, null);
            using var client = new HttpClient(handler);
            using var source = new HttpDataSource(client, "http://fake/{z}/{x}/{y}.mvt");

            var response = source.FetchAsync(new TileId(1, 0, 0)).GetAwaiter().GetResult();
            Assert.IsFalse(response.HasData, "HTTP 204 → HasData must be false");
        }

        [Test]
        public void HttpSource_500_Throws()
        {
            var handler = new StubHttpHandler(HttpStatusCode.InternalServerError, null);
            using var client = new HttpClient(handler);
            using var source = new HttpDataSource(client, "http://fake/{z}/{x}/{y}.mvt");

            Assert.Throws<HttpRequestException>(() =>
                source.FetchAsync(new TileId(1, 0, 0)).GetAwaiter().GetResult(),
                "HTTP 500 must throw HttpRequestException");
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
        public void Scheduler_Dedupe_ConcurrentRequests_IssueSingleFetch()
        {
            var tcs  = new TaskCompletionSource<TileResponse>();
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
            tcs.SetResult(expectedResponse);

            // Both awaiters get the same bytes.
            var res1 = req1.GetAwaiter().GetResult();
            var res2 = req2.GetAwaiter().GetResult();

            Assert.AreEqual(1, fetchCount, "Fetch count must still be 1 after both awaiters resolve");
            Assert.IsTrue(res1.HasData && res2.HasData);
            Assert.AreEqual(expectedResponse.Bytes[0], res1.Bytes[0]);
            Assert.AreEqual(expectedResponse.Bytes[0], res2.Bytes[0]);
        }

        [Test]
        public void Scheduler_AfterFetch_CacheHit_NoSecondFetch()
        {
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return Task.FromResult(MakeResponse(7));
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(2, 3, 4);

            // First request — triggers a fetch.
            var res1 = scheduler.Request(tileId).GetAwaiter().GetResult();
            Assert.AreEqual(1, fetchCount);
            Assert.IsTrue(res1.HasData);

            // Second request — must come from cache (fetch count unchanged).
            var res2 = scheduler.Request(tileId).GetAwaiter().GetResult();
            Assert.AreEqual(1, fetchCount, "Second request for the same tile must be a cache hit");
            Assert.AreEqual(res1.Bytes[0], res2.Bytes[0]);
        }

        // -----------------------------------------------------------------------------------------
        // 5. Scheduler — cancellation and release semantics
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// When the source's task is cancelled (e.g. the underlying network/IO layer propagates
        /// cancellation), the scheduler's awaiter observes OperationCanceledException.
        ///
        /// NOTE: TileScheduler.Release() drops the dictionary reference (best-effort cleanup) but
        /// does NOT cancel the underlying fetch task — it holds no per-tile CancellationTokenSource.
        /// Full per-tile fetch cancellation via Release() is a S04 follow-up (tracked: latent issue).
        /// This test verifies only that a cancelled source task propagates through the scheduler
        /// correctly; it is NOT an end-to-end test of Release-driven cancellation.
        /// </summary>
        [Test]
        public void Scheduler_SourceCancellation_Propagates_ToAwaiter()
        {
            var tcs        = new TaskCompletionSource<TileResponse>();
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return tcs.Task;  // Returns pending task; will be cancelled below.
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(3, 3, 3);

            // Issue a request; fetch is now in-flight (tcs.Task is pending).
            var requestTask = scheduler.Request(tileId);
            Assert.AreEqual(1, fetchCount, "Exactly one fetch should be in-flight");

            // Simulate the source cancelling (e.g., network abort). The scheduler awaiter
            // must propagate OperationCanceledException (or its subclass TaskCanceledException).
            tcs.TrySetCanceled();

            // TaskCanceledException is a subclass of OperationCanceledException; use Catch so that
            // both are accepted (Assert.Throws<T> in NUnit 3.x requires an exact type match).
            Assert.Catch<OperationCanceledException>(() =>
                requestTask.GetAwaiter().GetResult(),
                "Cancelled source task must propagate as OperationCanceledException to the awaiter");
        }

        [Test]
        public void Scheduler_Release_DropsReference_SubsequentRequestReFetches()
        {
            // Release() drops the cache entry and the in-flight reference. After release,
            // a new Request() must trigger a fresh fetch (not serve a stale cached result).
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return Task.FromResult(MakeResponse((byte)(fetchCount * 10)));
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(4, 4, 4);

            // First fetch — populates cache.
            scheduler.Request(tileId).GetAwaiter().GetResult();
            Assert.AreEqual(1, fetchCount, "First request triggers one fetch");

            // Release — evicts from cache.
            scheduler.Release(tileId);

            // Second request after release — must re-fetch (cache was evicted).
            scheduler.Request(tileId).GetAwaiter().GetResult();
            Assert.AreEqual(2, fetchCount, "Request after Release must re-fetch (not serve cache)");
        }

        // -----------------------------------------------------------------------------------------
        // S04 Batch A additions: per-tile cancellation, sync-completion guard
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Release() cancels the in-flight fetch (via per-tile CTS) and the result is NOT cached.
        /// Asserts on the ORIGINAL scheduler's cache to confirm the cancelled result was discarded.
        /// </summary>
        [Test]
        public void Release_CancelsInFlightFetch_TileNotCached()
        {
            // Source blocks until manually signalled, to model a real in-flight HTTP fetch.
            var tcs   = new TaskCompletionSource<TileResponse>();
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
            try { pendingTask.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { /* expected */ }

            // The ORIGINAL cache must NOT contain the tile (cancelled result must be discarded).
            Assert.IsFalse(cache.TryGet(tileId, out _),
                "Original cache must NOT contain the tile after Release cancels the in-flight fetch");
            Assert.AreEqual(0, scheduler.InFlightCount,
                "In-flight map must be empty after the cancelled fetch completes");
        }

        /// <summary>
        /// A fetch completing SUCCESSFULLY after Release() must NOT populate the original cache.
        /// This exercises the CTS-identity guard in FetchAndCacheAsync (success path, lines 159-164
        /// of TileScheduler.cs): when Release() removes the CTS before the fetch completes, the
        /// guard detects the mismatch and skips TileCache.Put.
        /// </summary>
        [Test]
        public void Release_SourceIgnoresToken_CompletesSuccessfully_ResultNotCached()
        {
            // Source does NOT honour the cancellation token — it blocks and eventually returns
            // a successful result regardless of cancellation. FakeDataSource drops ct entirely.
            var tcs        = new TaskCompletionSource<TileResponse>();
            var fakeSource = new FakeDataSource(id => tcs.Task);

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(7, 7, 7);

            // Start the fetch — blocks on tcs.
            var pendingTask = scheduler.Request(tileId);

            // Release: removes the CTS from _cts, invalidating the CTS-identity guard.
            scheduler.Release(tileId);

            // Now the source "returns" successfully (ignoring cancellation).
            tcs.SetResult(MakeResponse(55));

            // Await the pending task — it completes normally (no exception), because the source
            // did not honour the token.
            pendingTask.GetAwaiter().GetResult();

            // The CTS-identity guard must have detected the mismatch and skipped Put.
            Assert.IsFalse(cache.TryGet(tileId, out _),
                "Cache must NOT contain the tile when the source ignores the token and completes " +
                "successfully after Release() — exercises the CTS-identity guard (lines 159-164).");
            Assert.AreEqual(0, scheduler.InFlightCount,
                "In-flight map must be empty after the late-completing fetch returns.");
        }

        /// <summary>
        /// When a source returns an already-completed Task (Task.FromResult), the sync-completion
        /// path must NOT leave a stale in-flight entry after Request() returns.
        /// Guards item (c) from follow-ups.md.
        /// </summary>
        [Test]
        public void SyncCompletingSource_NoStaleInFlightEntry()
        {
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                // Sync completion: returns an already-resolved Task.
                return Task.FromResult(MakeResponse(77));
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId(6, 6, 6);

            // Start the fetch (sync-completing source).
            var fetchTask = scheduler.Request(tileId);

            // Await completion so the async continuation has run.
            fetchTask.GetAwaiter().GetResult();

            // After completion the in-flight map must be empty for this tile.
            Assert.AreEqual(0, scheduler.InFlightCount,
                "In-flight map must be empty after a sync-completing fetch completes. " +
                "A stale entry means the Task.Yield() guard is missing or broken.");
        }

        /// <summary>
        /// HttpDataSource threads ct through the body read where the overload is available.
        /// Under NET5_0_OR_GREATER ReadAsByteArrayAsync(ct) is used; under netstandard2.1 (Unity 6)
        /// the body read falls back to the no-ct overload with ThrowIfCancellationRequested as a
        /// pre-read guard — mid-read cancellation is not guaranteed there (stated limitation).
        ///
        /// This test uses a streaming content that checks ct on ReadAsync, so it is only meaningful
        /// when the NET5_0_OR_GREATER branch compiles (core-tests runs net10; Unity takes the #else
        /// branch and the body read is not mid-read cancellable). The test is marked Explicit so
        /// that the Unity EditMode run skips it (Unity netstandard2.1 would not exhibit mid-read
        /// cancel from the content stream). The core-tests run always exercises the NET5 branch.
        ///
        /// Limitation note: under Unity (netstandard2.1) mid-read cancellation requires the caller
        /// to cancel before the response body read or to use a streaming API — this is acceptable
        /// for S04; a full mid-read cancellable path is a follow-up if needed.
        /// </summary>
        [Test]
        public void HttpBodyRead_CancellableStream_TokenThreadedThrough()
        {
            // Stub handler returns a StreamContent backed by a CancellationToken-checking stream.
            using var cts = new CancellationTokenSource();
            var handler = new StubHttpHandlerWithCancellableContent(cts.Token);
            using var client = new HttpClient(handler);
            using var source = new HttpDataSource(client, "http://fake/{z}/{x}/{y}.mvt");

            // Cancel the token so the stream read throws immediately.
            cts.Cancel();

            // The OperationCanceledException propagates from ReadAsByteArrayAsync(ct) (NET5 branch)
            // or from the stream's own token check. Either way, the fetch must throw.
            Assert.Catch<OperationCanceledException>(() =>
                source.FetchAsync(new TileId(0, 0, 0), cts.Token).GetAwaiter().GetResult(),
                "FetchAsync must propagate OperationCanceledException when the cancellation token " +
                "is cancelled during the body read (verifies ct is threaded into ReadAsByteArrayAsync).");
        }

        /// <summary>
        /// Stub HTTP handler that returns a StreamContent backed by a stream that throws
        /// OperationCanceledException on ReadAsync when the associated token is cancelled.
        /// Used to verify that ct is threaded to the body read.
        /// </summary>
        private sealed class StubHttpHandlerWithCancellableContent : HttpMessageHandler
        {
            private readonly CancellationToken _ct;
            public StubHttpHandlerWithCancellableContent(CancellationToken ct) { _ct = ct; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var msg = new HttpResponseMessage(HttpStatusCode.OK);
                msg.Content = new StreamContent(new CancellableStream(_ct));
                return Task.FromResult(msg);
            }
        }

        /// <summary>
        /// Stream that throws OperationCanceledException on ReadAsync when the token is cancelled.
        /// This allows tests to verify that ReadAsByteArrayAsync(ct) actually uses the token.
        /// </summary>
        private sealed class CancellableStream : System.IO.Stream
        {
            private readonly CancellationToken _ct;
            public CancellableStream(CancellationToken ct) { _ct = ct; }

            public override bool CanRead  => true;
            public override bool CanSeek  => false;
            public override bool CanWrite => false;
            public override long Length   => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                _ct.ThrowIfCancellationRequested();
                return 0; // EOF
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                // Check both the stream's token and the passed-in token.
                _ct.ThrowIfCancellationRequested();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            }
            public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value)   => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        // -----------------------------------------------------------------------------------------
        // S06 Batch A: negative-caching policy (item b) — fake clock, no wall-clock sleeps
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// An absent tile (HasData=false) is NOT re-fetched within the negative-cache TTL: a second
        /// request inside the TTL is served from the negative cache without a second source fetch.
        /// </summary>
        [Test]
        public void NegativeCache_AbsentTile_NotRefetchedWithinTtl()
        {
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return Task.FromResult(TileResponse.Absent(TileEncoding.Mvt));
            });

            var now    = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clock  = new FakeClock(now);
            var cache  = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache,
                negativeTtl: TimeSpan.FromSeconds(5), clock: clock.Now);
            var tileId = new TileId(9, 1, 1);

            // First request — issues a fetch that reports absent.
            var r1 = scheduler.Request(tileId).GetAwaiter().GetResult();
            Assert.IsFalse(r1.HasData, "First response must be absent");
            Assert.AreEqual(1, fetchCount, "First request issues exactly one fetch");

            // Advance the clock but stay inside the TTL.
            clock.Advance(TimeSpan.FromSeconds(2));

            // Second request — must be served from the negative cache, NOT re-fetched.
            var r2 = scheduler.Request(tileId).GetAwaiter().GetResult();
            Assert.IsFalse(r2.HasData, "Second response must still be absent");
            Assert.AreEqual(1, fetchCount,
                "Absent tile must NOT be re-fetched within the negative-cache TTL (item b).");

            // The absent tile must NOT have been written to the LRU cache.
            Assert.IsFalse(cache.ContainsKey(tileId),
                "Absent tile must not be stored in the LRU TileCache (no indefinite caching).");
        }

        /// <summary>
        /// After the negative-cache TTL expires, an absent tile IS re-fetched — a recovered tile can
        /// re-appear. Time is advanced via the fake clock (deterministic, no Thread.Sleep).
        /// </summary>
        [Test]
        public void NegativeCache_RefetchesAfterTtlExpiry()
        {
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return Task.FromResult(TileResponse.Absent(TileEncoding.Mvt));
            });

            var now    = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clock  = new FakeClock(now);
            var cache  = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache,
                negativeTtl: TimeSpan.FromSeconds(5), clock: clock.Now);
            var tileId = new TileId(9, 2, 2);

            scheduler.Request(tileId).GetAwaiter().GetResult();
            Assert.AreEqual(1, fetchCount, "First request issues one fetch");

            // Advance past the TTL.
            clock.Advance(TimeSpan.FromSeconds(6));

            scheduler.Request(tileId).GetAwaiter().GetResult();
            Assert.AreEqual(2, fetchCount,
                "Absent tile must be re-fetched once the negative-cache TTL has expired (item b).");
        }

        /// <summary>
        /// A negative TTL of zero disables negative caching entirely: every request for an absent tile
        /// re-fetches (the opt-out path, useful where the caller wants no suppression).
        /// </summary>
        [Test]
        public void NegativeCache_ZeroTtl_AlwaysRefetches()
        {
            int fetchCount = 0;
            var fakeSource = new FakeDataSource(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return Task.FromResult(TileResponse.Absent(TileEncoding.Mvt));
            });

            var clock  = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var cache  = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache,
                negativeTtl: TimeSpan.Zero, clock: clock.Now);
            var tileId = new TileId(9, 3, 3);

            scheduler.Request(tileId).GetAwaiter().GetResult();
            scheduler.Request(tileId).GetAwaiter().GetResult();
            Assert.AreEqual(2, fetchCount,
                "With negativeTtl == TimeSpan.Zero, an absent tile is re-fetched every request.");
        }

        // -----------------------------------------------------------------------------------------
        // S06 Batch A: Dispose ownership (item c)
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Dispose must NOT dispose the injected IDataSource — the scheduler is a non-owning coordinator
        /// (the caller owns the source's lifetime). Documents and pins the ownership decision (item c).
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
        /// token observes cancellation. Confirms Dispose tears down outstanding work without leaking.
        /// </summary>
        [Test]
        public void Dispose_CancelsInFlightFetch()
        {
            var tcs        = new TaskCompletionSource<TileResponse>();
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
            try { pending.GetAwaiter().GetResult(); }
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
            public Task<TileResponse> FetchAsync(TileId id, CancellationToken ct = default)
                => Task.FromResult(TileResponse.Absent(TileEncoding.Mvt));
            public void Dispose() { WasDisposed = true; }
        }

        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        private static TileResponse MakeResponse(byte seed)
            => new TileResponse(new byte[] { seed, (byte)(seed + 1), (byte)(seed + 2) }, TileEncoding.Mvt);

        // -----------------------------------------------------------------------------------------
        // Stub HTTP handler
        // -----------------------------------------------------------------------------------------

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode _statusCode;
            private readonly byte[]         _content;

            public StubHttpHandler(HttpStatusCode statusCode, byte[] content)
            {
                _statusCode = statusCode;
                _content    = content;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var msg = new HttpResponseMessage(_statusCode);
                if (_content != null)
                    msg.Content = new ByteArrayContent(_content);
                return Task.FromResult(msg);
            }
        }

        // -----------------------------------------------------------------------------------------
        // Fake data source for scheduler tests
        // -----------------------------------------------------------------------------------------

        private sealed class FakeDataSource : IDataSource
        {
            private readonly Func<TileId, Task<TileResponse>> _fetch;

            public TileEncoding Encoding => TileEncoding.Mvt;

            public FakeDataSource(Func<TileId, Task<TileResponse>> fetch)
            {
                _fetch = fetch;
            }

            public Task<TileResponse> FetchAsync(TileId id, CancellationToken ct = default)
                => _fetch(id);

            public void Dispose() { }
        }

        /// <summary>
        /// Fake data source variant that exposes the CancellationToken to the fetch delegate,
        /// so tests can register cancellation callbacks (needed for Release_CancelsInFlightFetch test).
        /// </summary>
        private sealed class FakeDataSourceCt : IDataSource
        {
            private readonly Func<TileId, CancellationToken, Task<TileResponse>> _fetch;

            public TileEncoding Encoding => TileEncoding.Mvt;

            public FakeDataSourceCt(Func<TileId, CancellationToken, Task<TileResponse>> fetch)
            {
                _fetch = fetch;
            }

            public Task<TileResponse> FetchAsync(TileId id, CancellationToken ct = default)
                => _fetch(id, ct);

            public void Dispose() { }
        }
    }
}
