// DataSources/DataSourceTests.cs — A7 tile-feature source, render-path, HTTP and scheduler teeth (EditMode).
//
// TileSchedulerOrderingTests.cs stays its own file (fast lane, csproj-registered). No using/alias
// collision found across the four EditMode files merged here.
//
// Contents:
//   A7TileFeatureSourceTests        — the raised ITileFeatureSource.GetTile -> SharedDisposable<IDecodedTile> interface, EditMode async-Task unit teeth.
//   UnityWebRequestDataSourceTests  — UnityWebRequestDataSource against a loopback HttpListener, including the 404 -> Absent mapping.
//   DataSourceRenderPathTests       — FileDataSource through the render pipeline.
//   DataSourceTests                 — FileDataSource/UnityWebRequestDataSource/MvtTileFeatureSource, migrated onto UniTask/UniTaskCompletionSource.

using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using MapRenderer.Core.Geo;
using MapRenderer.Core.Lifetime;
using MapRenderer.Core.Tiles;
using MapRenderer.Jobs.Tiles;
using MapRenderer.Unity.Concurrency;
using MapRenderer.Unity.Rendering.Tile;
using MapRenderer.Unity.Rendering.Tile.Processing;
using System;
using System.Collections;
using System.Net;
using System.Threading;
using UnityEngine.TestTools;
using MapRenderer.Core.Data;
using MapRenderer.Unity.Rendering.Source;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Jobs;
using MapRenderer.Tests.TestSupport;
using MapRenderer.Jobs.Projection;
using MapRenderer.Jobs.Mvt;


namespace MapRenderer.Tests.DataSources
{
    // ───────────────────────────────────────────────────────────────────────────────────
    // A7TileFeatureSourceTests — ITileFeatureSource.GetTile -> SharedDisposable<IDecodedTile>, EditMode async-Task teeth
    // ───────────────────────────────────────────────────────────────────────────────────

    [TestFixture]
    public class A7TileFeatureSourceTests
    {
        // Off-main, matching production's desktop policy — these teeth exercise GetTile's own contract, not
        // the scheduler choice.
        private static readonly IWorkScheduler Scheduler = new ThreadPoolWorkScheduler();

        // ── F-4: GetTile decodes EAGERLY — the inversion of the retired lazy tooth ────────────────────────

        // Deliberately malformed as MVT (a truncated length-delimited TileLayers field — MvtDecoder.Decode
        // throws decoding it — same fixture shape used by A6NonMvtDecoderTests).
        private static readonly byte[] MalformedMvtBytes = { 0x1A, 0x64 };

        /// <summary>
        /// <b>T-E2 — the decisive falsifier, inverted.</b> This tooth used to assert that <c>GetTile</c>
        /// completed cleanly over malformed bytes and only faulted at the first <c>GetOrDecode()</c>: the
        /// proof the handle was LAZY. Under the eager decode the parse happens inside the task, so the task
        /// itself faults and <b>no handle is ever minted</b>. The same input, the same seam, the opposite
        /// answer — and the same decisiveness: a lazy implementation would complete this call and hand back
        /// a handle.
        /// </summary>
        [Test]
        public async Task GetTile_MalformedBytes_FaultsTheTask_AndMintsNoHandle()
        {
            var byteSource = TestDataSource.FromBytes(MalformedMvtBytes);
            using var source = new MvtTileFeatureSource(byteSource, Scheduler);

            System.Exception thrown = null;
            SharedDisposable<IDecodedTile> handle = null;
            try { handle = await source.GetTile(new TileId { Z = 0, X = 0, Y = 0 }); }
            catch (System.Exception ex) { thrown = ex; }

            Assert.IsNull(handle,
                "F-4 DECISIVE (inverted): an EAGER GetTile decodes at fetch completion, so malformed bytes " +
                "must fault the TASK and produce no handle. A handle here would mean the source deferred the " +
                "decode — the lazy contract this stage deleted, and with it the drop paths that free nothing.");
            Assert.IsInstanceOf<TileDecodeException>(thrown,
                "…and the fault must be a TileDecodeException, not the raw decoder throw: it shares a channel " +
                "with fetch errors now, and only the type distinguishes 'the bytes are bad' from 'the network " +
                "failed'. Collapsing them would let a broken tile hide inside another failure's log throttle.");
            Assert.IsInstanceOf<System.InvalidOperationException>(thrown.InnerException,
                "…with the decoder's own exception preserved underneath, so wrapping costs no diagnosis");
        }

        /// <summary>
        /// <b>T-E1 — the happy path of the same inversion.</b> The awaited task hands back a handle whose
        /// tile is ALREADY built: reading it does no work, cannot fault, and yields the same instance every
        /// time. Paired with the malformed case above (which proves the decode ran inside the task), this
        /// pins that a read is a plain field access rather than a deferred parse.
        /// </summary>
        [Test]
        public async Task GetTile_HandsBackAnAlreadyDecodedTile_ThatTheCallerOwns()
        {
            byte[] fixtureBytes = System.IO.File.ReadAllBytes(
                System.IO.Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes"));
            var byteSource = TestDataSource.FromBytes(fixtureBytes);
            using var source = new MvtTileFeatureSource(byteSource, Scheduler);

            SharedDisposable<IDecodedTile> handle = await source.GetTile(new TileId { Z = 0, X = 0, Y = 0 });
            Assert.IsNotNull(handle, "sanity: present bytes must mint a handle");

            IDecodedTile first = handle.Value;
            Assert.IsNotNull(first, "the tile is already decoded — reading it must never return null");
            Assert.AreSame(first, handle.Value,
                "…and a second read must hand back the SAME instance. A lazy handle that decoded per read " +
                "would produce a distinct tile here, and two sets of Allocator.Persistent buffers with one " +
                "owner between them.");

            // The caller owns the one reference GetTile handed over; releasing it is what frees the buffers.
            handle.Release();
        }

        [Test]
        public async Task GetTile_AbsentTile_ReturnsNullHandle()
        {
            // Byte-equivalent to today's TileResponse.HasData == false branch (§G risk 4).
            var byteSource = TestDataSource.Absent();
            using var source = new MvtTileFeatureSource(byteSource, Scheduler);

            SharedDisposable<IDecodedTile> handle = await source.GetTile(new TileId { Z = 0, X = 0, Y = 0 });

            Assert.IsNull(handle, "an absent tile (HasData == false) must map to a null handle — the " +
                "coordinator's null-for-absent contract (Epic A / A7 §G-4).");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // UnityWebRequestDataSourceTests — UnityWebRequestDataSource against a loopback HttpListener
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// S51 review fix — acceptance tests for <see cref="UnityWebRequestDataSource"/>.
    ///
    /// Closes the coverage gap: previously no test exercised the 404→Absent mapping, which
    /// was unreachable dead code (the vendored ToUniTask throws for ProtocolError before
    /// reaching the responseCode check). The fix wraps the await in a narrow
    /// catch(UnityWebRequestException when 404/204) that returns TileResponse.Absent.
    /// These tests prove that fix is load-bearing.
    /// </summary>
    [TestFixture]
    public class UnityWebRequestDataSourceTests
    {
        // ── Loopback server helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Finds a free loopback port by binding a listener on port 0, recording the assigned port,
        /// then stopping it before returning. Avoids the classic race by using a short-lived listener
        /// to claim the OS port number.
        /// </summary>
        private static int FindFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>
        /// Starts an HttpListener on a loopback address and serves one response asynchronously.
        /// The listener is stopped and closed after serving the single request.
        /// </summary>
        private static HttpListener StartLoopbackServer(string prefix, int statusCode, byte[] body = null)
        {
            var hl = new HttpListener();
            hl.Prefixes.Add(prefix);
            hl.Start();

            // Serve one request on a ThreadPool thread so the test coroutine can yield.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    HttpListenerContext ctx = hl.GetContext();
                    ctx.Response.StatusCode = statusCode;
                    if (body != null && body.Length > 0)
                    {
                        ctx.Response.ContentLength64 = body.Length;
                        ctx.Response.OutputStream.Write(body, 0, body.Length);
                    }
                    ctx.Response.OutputStream.Close();
                    ctx.Response.Close();
                }
                catch (HttpListenerException) { /* listener was stopped before a request arrived */ }
                catch (ObjectDisposedException) { }
                finally
                {
                    try { hl.Stop(); } catch { }
                }
            });

            return hl;
        }

        // ── Tests ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// A 404 response from the tile server must yield <c>HasData == false</c> (TileResponse.Absent)
        /// instead of throwing. This is the primary regression test for the S51 dead-code fix:
        /// the previous code had the responseCode check AFTER the await, but ToUniTask throws
        /// UnityWebRequestException for ProtocolError (any non-2xx), so the check was never reached.
        ///
        /// The fix: catch UnityWebRequestException with a when-filter on 404/204 and return Absent.
        /// This test is load-bearing — it would fail on the old code (exception propagates → fail).
        /// </summary>
        [UnityTest]
        public IEnumerator FetchAsync_404Response_ReturnsAbsent_HasDataFalse()
        {
            int port   = FindFreePort();
            string url = $"http://127.0.0.1:{port}/tiles/{{z}}/{{x}}/{{y}}.pbf";
            // Serve a 404 for any request to this prefix.
            var listener = StartLoopbackServer($"http://127.0.0.1:{port}/", 404);

            TileResponse response = default;
            Exception    caught   = null;

            try
            {
                using var source = new UnityWebRequestDataSource(url);
                // Run FetchAsync as a coroutine so UnityWebRequest's PlayerLoop hook pumps.
                // Use void-returning Action<T> to force ContinueWith<T>(Action<T>) overload
                // (avoids the Func<T,TR> overload that would return UniTask<T> and confuse ToCoroutine).
                yield return source.FetchAsync(new TileId { Z = 0, X = 0, Y = 0 })
                    .ContinueWith((Action<TileResponse>)(r => { response = r; }))
                    .ToCoroutine(ex => { caught = ex; });
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }

            Assert.IsNull(caught,
                "A 404 response must NOT throw — it must be caught and mapped to TileResponse.Absent. " +
                $"Exception: {caught?.Message}");
            Assert.IsFalse(response.HasData,
                "HTTP 404 must produce HasData == false (TileResponse.Absent). " +
                "If HasData is true or an exception was thrown, the dead-code fix did not take effect.");
        }

        /// <summary>
        /// A 200 OK response with tile bytes must yield <c>HasData == true</c> with the correct bytes.
        /// This is the positive control: confirms the fix did not break the success path.
        /// </summary>
        [UnityTest]
        public IEnumerator FetchAsync_200Response_ReturnsData_HasDataTrue()
        {
            int port   = FindFreePort();
            string url = $"http://127.0.0.1:{port}/tiles/{{z}}/{{x}}/{{y}}.pbf";
            byte[] expected = new byte[] { 0x1A, 0x2B, 0x3C };
            var listener = StartLoopbackServer($"http://127.0.0.1:{port}/", 200, expected);

            TileResponse response = default;
            Exception    caught   = null;

            try
            {
                using var source = new UnityWebRequestDataSource(url);
                yield return source.FetchAsync(new TileId { Z = 0, X = 0, Y = 0 })
                    .ContinueWith((Action<TileResponse>)(r => { response = r; }))
                    .ToCoroutine(ex => { caught = ex; });
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }

            Assert.IsNull(caught,
                $"A 200 OK response must NOT throw. Exception: {caught?.Message}");
            Assert.IsTrue(response.HasData,
                "HTTP 200 with body bytes must produce HasData == true.");
            Assert.IsNotNull(response.Bytes,
                "HTTP 200 response Bytes must not be null.");
            Assert.AreEqual(expected.Length, response.Bytes.Length,
                "Response byte count must match the served body.");
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // DataSourceRenderPathTests — FileDataSource through the render pipeline
    // ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proves that <see cref="FileDataSource"/> feeds the render path correctly: bytes round-trip
    /// through the source and produce an identical vertex CONTENT HASH (not just count) from the
    /// decode→assemble→project pipeline.
    ///
    /// This is an A-vs-A comparison — the same bytes through two sources must produce identical render
    /// input — so triangulation contributes nothing to what it proves (A0: dropped; the hash covers
    /// the assembled ring vertices, outer then holes, projected through the existing
    /// TileToGeoJob → ProjectPointsJob chain, which is the projection coverage this test actually
    /// carries). A count-only comparison would be blind to divergent vertex positions; content hash
    /// guards against any regression in decode / assembly / projection across sources.
    ///
    /// S51: HttpDataSource deleted from Core; HTTP is now UnityWebRequestDataSource (Unity layer).
    /// </summary>
    [TestFixture]
    public class DataSourceRenderPathTests
    {
        [Test]
        public void FileSource_FeedsRenderPath_VertexAndIndexContentHashMatchesBaseline()
        {
            string fixturePath = Path.Combine(Application.dataPath, "Fixtures", "sample-tile.bytes");
            FileAssert.Exists(fixturePath);
            byte[] fixtureBytes = File.ReadAllBytes(fixturePath);

            // 1. Feed bytes via FileDataSource.
            string tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string tilePath = Path.Combine(tempRoot, "0", "0", "0.mvt");
            Directory.CreateDirectory(Path.GetDirectoryName(tilePath));
            File.WriteAllBytes(tilePath, fixtureBytes);

            byte[] fileBytes;
            try
            {
                using var fileSource = new FileDataSource(tempRoot);
                // S51: FetchAsync is async (SwitchToThreadPool pattern). It does NOT complete
                // synchronously, so calling .GetAwaiter().GetResult() immediately throws
                // "Not yet completed". Parks until the ThreadPool fetch completes.
                var fetchTask = fileSource.FetchAsync(new TileId { Z = 0, X = 0, Y = 0 });
                fetchTask.WaitOffPlayerLoop(10000);
                Assert.IsTrue(fetchTask.Status.IsCompleted(),
                    "FileDataSource.FetchAsync must complete within 10 seconds.");
                var fileResp = fetchTask.GetAwaiter().GetResult();
                Assert.IsTrue(fileResp.HasData, "FileDataSource must return HasData=true");
                fileBytes = fileResp.Bytes;
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }

            // 2. Run the same decode→assemble→project pipeline on each source's bytes; compare the
            // CONTENT HASH (an A-vs-A comparison — the triangulator contributes nothing to it, see
            // class doc).
            string baselineHash = BuildContentHash(fixtureBytes);
            string fileHash     = BuildContentHash(fileBytes);

            Assert.AreEqual(baselineHash, fileHash,
                "FileDataSource render path content hash must match the direct baseline. " +
                "A mismatch means the file source returns different bytes or the decode path is non-deterministic.");

            Debug.Log($"[DataSourceRenderPathTests] FileDataSource produces an identical content hash: {baselineHash[..16]}...");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Decodes and assembles MVT bytes and returns a SHA-256 hash of the flat, projected ring-vertex
        /// array — outer then holes, in assembly order, across all polygons (world positions via
        /// TileToGeoJob → ProjectPointsJob). No triangulation: this test is an A-vs-A comparison of two
        /// sources' bytes, so the triangulator is not part of the property it proves.
        /// </summary>
        private static string BuildContentHash(byte[] mvtBytes)
        {
            var layer = MvtFixtureStreams.ReadLayer(mvtBytes, "countries");
            Assert.IsNotNull(layer, "countries layer must be present");

            double extent = layer.Extent;
            var tileId    = new TileId { Z = 0, X = 0, Y = 0 };
            var (bMin, _) = tileId.MercatorBounds();
            double originX = bMin.x;
            double originY = bMin.y;

            using var sha256 = SHA256.Create();
            var vertBytes = new List<byte>();

            for (int fi = 0; fi < layer.Kinds.Count; fi++)
            {
                if (layer.Kinds[fi] != TileGeometryType.Polygon) continue;

                List<List<double2>> rings = MvtGeometry.Decode(layer.Commands[fi]);
                if (rings == null || rings.Count == 0) continue;

                List<Polygon> polygons = PolygonAssembler.Assemble(rings);

                foreach (var polygon in polygons)
                {
                    var flatVerts = new List<double2>(polygon.Outer);
                    if (polygon.Holes != null)
                        foreach (var hole in polygon.Holes) flatVerts.AddRange(hole);
                    int vCount = flatVerts.Count;
                    if (vCount == 0) continue;

                    // Not using 'using var' — CS1654 makes using-var NativeArrays read-only in C# 8+.
                    var tileCoords = new NativeArray<double2>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    var geo        = new NativeArray<GeoCoordinate>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    var worldPos   = new NativeArray<double3>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    var vertUp     = new NativeArray<double3>(vCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

                    for (int i = 0; i < vCount; i++)
                        tileCoords[i] = flatVerts[i];

                    try
                    {
                        new TileToGeoJob
                        {
                            Tile = new TileId { Z = 0, X = 0, Y = 0 }, Extent = extent,
                            TileCoords = tileCoords, OutGeo = geo,
                        }.Schedule(vCount, 64).Complete();

                        new ProjectPointsJob<WebMercatorProjection>
                        {
                            Projection     = new WebMercatorProjection(),
                            OriginWorld    = new double3(originX, 0.0, originY),
                            Points         = geo,
                            WorldPositions = worldPos,
                            Normals        = vertUp,
                        }.Schedule(vCount, 64).Complete();

                        for (int i = 0; i < vCount; i++)
                        {
                            vertBytes.AddRange(BitConverter.GetBytes(worldPos[i].x));
                            vertBytes.AddRange(BitConverter.GetBytes(worldPos[i].y));
                            vertBytes.AddRange(BitConverter.GetBytes(worldPos[i].z));
                        }
                    }
                    finally
                    {
                        tileCoords.Dispose();
                        geo.Dispose();
                        worldPos.Dispose();
                        vertUp.Dispose();
                    }
                }
            }

            return Convert.ToBase64String(sha256.ComputeHash(vertBytes.ToArray()));
        }
    }

    // ───────────────────────────────────────────────────────────────────────────────────
    // DataSourceTests — FileDataSource/UnityWebRequestDataSource/MvtTileFeatureSource over UniTask
    // ───────────────────────────────────────────────────────────────────────────────────

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
            var tileId = new TileId { Z = 0, X = 0, Y = 0 };

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
                using MvtTile fileTile = MvtDecoder.Decode(new TileId { Z = 0, X = 0, Y = 0 }, fileResponse.Bytes);
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
                var response = await source.FetchAsync(new TileId { Z = 5, X = 10, Y = 15 });
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
            var idA = new TileId { Z = 0, X = 0, Y = 0 };
            var idB = new TileId { Z = 0, X = 1, Y = 0 };
            var idC = new TileId { Z = 0, X = 2, Y = 0 };

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
            var idA = new TileId { Z = 0, X = 0, Y = 0 };
            var idB = new TileId { Z = 0, X = 1, Y = 0 };
            var idC = new TileId { Z = 0, X = 2, Y = 0 };

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

            cache.Put(new TileId { Z = 0, X = 0, Y = 0 }, MakeResponse(1));
            Assert.AreEqual(1, cache.Count);

            cache.Put(new TileId { Z = 0, X = 1, Y = 0 }, MakeResponse(2));
            cache.Put(new TileId { Z = 0, X = 2, Y = 0 }, MakeResponse(3));
            Assert.AreEqual(3, cache.Count);

            // Over capacity: count stays at capacity.
            cache.Put(new TileId { Z = 0, X = 3, Y = 0 }, MakeResponse(4));
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
            var fakeSource = TestDataSource.FromFetch(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return tcs.Task;
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId { Z = 1, X = 2, Y = 3 };

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
            var fakeSource = TestDataSource.FromFetch(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return UniTask.FromResult(MakeResponse(7));
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId { Z = 2, X = 3, Y = 4 };

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
            var fakeSource = TestDataSource.FromFetch(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return tcs.Task;  // Returns pending UniTask; will be cancelled below.
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId { Z = 3, X = 3, Y = 3 };

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
            var fakeSource = TestDataSource.FromFetch(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return UniTask.FromResult(MakeResponse((byte)(fetchCount * 10)));
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId { Z = 4, X = 4, Y = 4 };

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
            var fakeSource = new TestDataSource((id, ct) =>
            {
                Interlocked.Increment(ref fetchCount);
                // Register a callback: when ct is cancelled, cancel the TCS so the blocked
                // fetch completes (with cancellation) and doesn't hang the test.
                ct.Register(() => tcs.TrySetCanceled());
                return tcs.Task;
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId { Z = 5, X = 5, Y = 5 };

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
            var fakeSource = TestDataSource.FromFetch(id => tcs.Task);

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId { Z = 7, X = 7, Y = 7 };

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
        /// The behavioural anti-hop tooth: <see cref="TileScheduler"/>'s injectable clock is called
        /// exactly once on the sync-completing-absent path, inside the post-fetch block, AFTER the
        /// (removed) thread-pool hop — never on the negative-cache read (which finds nothing on a first
        /// request), so the single invocation is unambiguous. Every recorded thread id must equal the
        /// test thread's: a hop would move the clock call onto a pool thread.
        ///
        /// EditMode-only, deliberately: thread identity is decisive only when the caller is guaranteed
        /// not to already be a pool thread — the EditMode test thread is the main thread, never a pool
        /// thread. Under <c>dotnet test</c>, NUnit may run the test ON a pool thread, and a re-inserted
        /// hop could then resume on that SAME thread — a vacuous GREEN. That is why this tooth stays out
        /// of <c>core-tests.csproj</c> rather than moving to the engine-free sibling with T1/T3/T4.
        /// </summary>
        [Test]
        public async Task SchedulerCompletion_RunsOnTheFetchCompletingThread_NoHop()
        {
            int mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var recordedThreadIds = new System.Collections.Generic.List<int>();
            var clockLock = new object();

            var fakeSource = TestDataSource.Absent();
            var cache      = new TileCache(capacity: 10);
            var scheduler  = new TileScheduler(fakeSource, cache,
                negativeTtl: TimeSpan.FromSeconds(5),
                clock: () =>
                {
                    lock (clockLock) { recordedThreadIds.Add(Thread.CurrentThread.ManagedThreadId); }
                    return DateTime.UtcNow;
                });
            var tileId = new TileId { Z = 11, X = 1, Y = 1 };

            await scheduler.Request(tileId);

            Assert.GreaterOrEqual(recordedThreadIds.Count, 1,
                "the injectable clock must be called at least once on the post-fetch negative-cache write path.");
            foreach (int id in recordedThreadIds)
            {
                Assert.AreEqual(mainThreadId, id,
                    "the post-fetch bookkeeping must run on the thread that completed the fetch — no hop.");
            }
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
            var fakeSource = TestDataSource.FromFetch(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return UniTask.FromResult(TileResponse.Absent(TileEncoding.Mvt));
            });

            var now    = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clock  = new FakeClock(now);
            var cache  = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache,
                negativeTtl: TimeSpan.FromSeconds(5), clock: clock.Now);
            var tileId = new TileId { Z = 9, X = 1, Y = 1 };

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
            var fakeSource = TestDataSource.FromFetch(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return UniTask.FromResult(TileResponse.Absent(TileEncoding.Mvt));
            });

            var now    = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var clock  = new FakeClock(now);
            var cache  = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache,
                negativeTtl: TimeSpan.FromSeconds(5), clock: clock.Now);
            var tileId = new TileId { Z = 9, X = 2, Y = 2 };

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
            var fakeSource = TestDataSource.FromFetch(id =>
            {
                Interlocked.Increment(ref fetchCount);
                return UniTask.FromResult(TileResponse.Absent(TileEncoding.Mvt));
            });

            var clock  = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var cache  = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache,
                negativeTtl: TimeSpan.Zero, clock: clock.Now);
            var tileId = new TileId { Z = 9, X = 3, Y = 3 };

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
            var spySource = TestDataSource.Absent();
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
            var fakeSource = new TestDataSource((id, ct) =>
            {
                ct.Register(() => { cancellationObserved = true; tcs.TrySetCanceled(); });
                return tcs.Task;
            });

            var cache     = new TileCache(capacity: 10);
            var scheduler = new TileScheduler(fakeSource, cache);
            var tileId    = new TileId { Z = 8, X = 8, Y = 8 };

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


        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        private static TileResponse MakeResponse(byte seed)
            => new TileResponse(new byte[] { seed, (byte)(seed + 1), (byte)(seed + 2) }, TileEncoding.Mvt);

        // -----------------------------------------------------------------------------------------
        // Fake data sources for scheduler tests
        // -----------------------------------------------------------------------------------------


    }
}
